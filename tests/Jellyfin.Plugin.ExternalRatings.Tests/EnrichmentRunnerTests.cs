using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings.Core;
using Jellyfin.Plugin.ExternalRatings.Core.Abstractions;
using Jellyfin.Plugin.ExternalRatings.Persistence;
using Jellyfin.Plugin.ExternalRatings.Resolvers;
using Jellyfin.Plugin.ExternalRatings.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

public class EnrichmentRunnerTests
{
    private sealed class Harness
    {
        public required EnrichmentRunner Runner { get; init; }
        public required RecordingItemWriter Writer { get; init; }
        public required RecordingBackupStore Backup { get; init; }
        public required InMemoryCacheFileStore CacheStore { get; init; }
        public required StubRatingResolver Resolver { get; init; }
        public required DailyRequestCounter Counter { get; init; }
    }

    private sealed class ProgressRecorder : IProgress<double>
    {
        public List<double> Values { get; } = new();

        public void Report(double value) => Values.Add(value);
    }

    private static Harness Build(
        RatingResult result,
        bool dryRun,
        int dailyLimit = int.MaxValue,
        StubBatchRatingResolver? batchResolver = null,
        StubRatingResolver? singleResolver = null)
    {
        var clock = new FakeClock();
        var resolver = singleResolver ?? new StubRatingResolver(result);
        var writer = new RecordingItemWriter();
        var backup = new RecordingBackupStore();
        var cacheStore = new InMemoryCacheFileStore();
        var cache = new FileRatingCache(cacheStore, clock);
        var breaker = new CircuitBreaker(clock);
        var counter = new DailyRequestCounter(clock);
        Func<PipelineOptions> options = () => new PipelineOptions { DryRun = dryRun };
        var pipeline = new RatingPipeline(
            new FixedRouter(resolver), writer, backup, cache, clock, _ => breaker, options, NullLogger<RatingPipeline>.Instance);
        RatingPrefetcher? prefetcher = batchResolver is null
            ? null
            : new RatingPrefetcher(
                batchResolver, _ => true, cache, clock, breaker, counter, options, () => dailyLimit, NullLogger<RatingPrefetcher>.Instance);
        var runner = new EnrichmentRunner(
            pipeline, prefetcher, cache, backup, counter, () => dailyLimit, NullLogger<EnrichmentRunner>.Instance);
        return new Harness
        {
            Runner = runner,
            Writer = writer,
            Backup = backup,
            CacheStore = cacheStore,
            Resolver = resolver,
            Counter = counter
        };
    }

    private static IReadOnlyList<RatingWorkItem> Items(int count)
    {
        var list = new List<RatingWorkItem>();
        for (var i = 0; i < count; i++)
        {
            var id = Guid.Parse($"00000000-0000-0000-0000-{i:D12}");
            list.Add(new RatingWorkItem(
                new RatingItemRef(id, $"Item {i}"),
                ItemLevel.Movie,
                new Dictionary<string, string> { ["Tmdb"] = (100 + i).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                "myanimelist"));
        }

        return list;
    }

    private static IReadOnlySet<Guid> LiveIds(IReadOnlyList<RatingWorkItem> items)
        => items.Select(i => i.Ref.ItemId).ToHashSet();

    [Fact]
    public async Task RunAsync_EmptyItems_ProcessesNothing()
    {
        var h = Build(RatingResult.NoMatch(), dryRun: true);

        var summary = await h.Runner.RunAsync(Array.Empty<RatingWorkItem>(), new HashSet<Guid>(), null, CancellationToken.None);

        summary.ItemsProcessed.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_ProcessesEveryItem()
    {
        var h = Build(RatingResult.NoMatch(), dryRun: true);
        var items = Items(3);

        var summary = await h.Runner.RunAsync(items, LiveIds(items), null, CancellationToken.None);

        summary.ItemsProcessed.Should().Be(3);
        summary.NoMatch.Should().Be(3);
        h.Resolver.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task RunAsync_FoundWithDryRun_SkipsWrites()
    {
        var h = Build(RatingResult.ForScore(8.0f), dryRun: true);
        var items = Items(2);

        var summary = await h.Runner.RunAsync(items, LiveIds(items), null, CancellationToken.None);

        summary.DryRunSkipped.Should().Be(2);
        summary.Updated.Should().Be(0);
        h.Writer.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_FoundWithDryRunOff_WritesAndBacksUp()
    {
        var h = Build(RatingResult.ForScore(8.0f), dryRun: false);
        var items = Items(2);

        var summary = await h.Runner.RunAsync(items, LiveIds(items), null, CancellationToken.None);

        summary.Updated.Should().Be(2);
        h.Writer.Writes.Should().HaveCount(2);
        h.Backup.Backups.Should().HaveCount(2);
    }

    [Fact]
    public async Task RunAsync_FlushesCacheAfterRun()
    {
        var h = Build(RatingResult.ForScore(8.0f), dryRun: true);
        var items = Items(2);

        await h.Runner.RunAsync(items, LiveIds(items), null, CancellationToken.None);

        // Found results are cached, so the run-end flush persists at least once.
        h.CacheStore.WriteCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task RunAsync_ReportsProgressToCompletion()
    {
        var h = Build(RatingResult.NoMatch(), dryRun: true);
        var items = Items(4);
        var progress = new ProgressRecorder();

        await h.Runner.RunAsync(items, LiveIds(items), progress, CancellationToken.None);

        progress.Values.Should().NotBeEmpty();
        progress.Values.Last().Should().Be(1.0);
    }

    [Fact]
    public async Task RunAsync_PrunesOrphanBackups()
    {
        var h = Build(RatingResult.NoMatch(), dryRun: true);
        var orphan = Guid.Parse("99999999-9999-9999-9999-999999999999");
        await h.Backup.EnsureBackedUpAsync(orphan, 5.0f, CancellationToken.None);
        var items = Items(1);

        await h.Runner.RunAsync(items, LiveIds(items), null, CancellationToken.None);

        h.Backup.TryGet(orphan, out _).Should().BeFalse();
    }

    // ---- Phase 0 prefetch ----

    [Fact]
    public async Task RunAsync_PrefetchSeedsCache_PipelineHitsCacheNotResolver()
    {
        // The single-item resolver throws if the pipeline ever calls it; prefetch must seed the cache
        // so every item is a hit. Both resolvers share the "mdblist" key so the cache keys align.
        var single = new StubRatingResolver((_, _) =>
            throw new InvalidOperationException("pipeline should not resolve; prefetch seeded the cache"))
        {
            Key = "mdblist"
        };
        var batch = new StubBatchRatingResolver(_ => RatingResult.ForScore(8.0f)) { Key = "mdblist" };
        var h = Build(RatingResult.ForScore(8.0f), dryRun: true, batchResolver: batch, singleResolver: single);
        var items = Items(3);

        var summary = await h.Runner.RunAsync(items, LiveIds(items), null, CancellationToken.None);

        summary.ItemsProcessed.Should().Be(3);
        summary.CacheHits.Should().Be(3);
        single.CallCount.Should().Be(0);
        batch.BatchCalls.Should().ContainSingle();
    }

    // ---- Budget gate ----

    [Fact]
    public async Task RunAsync_BudgetExhausted_StopsBeforeProcessing()
    {
        var h = Build(RatingResult.ForScore(8.0f), dryRun: false, dailyLimit: 1000);
        h.Counter.InitializeFromColdStart(1000); // already at the limit
        var items = Items(3);

        var summary = await h.Runner.RunAsync(items, LiveIds(items), null, CancellationToken.None);

        summary.ItemsProcessed.Should().Be(0);
        summary.StoppedForBudget.Should().BeTrue();
        h.Writer.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_BudgetAvailable_DoesNotFlagStopped()
    {
        var h = Build(RatingResult.NoMatch(), dryRun: true, dailyLimit: 1000);
        var items = Items(2);

        var summary = await h.Runner.RunAsync(items, LiveIds(items), null, CancellationToken.None);

        summary.ItemsProcessed.Should().Be(2);
        summary.StoppedForBudget.Should().BeFalse();
    }
}
