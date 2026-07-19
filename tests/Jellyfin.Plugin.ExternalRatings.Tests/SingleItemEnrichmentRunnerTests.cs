using System;
using System.Collections.Generic;
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

public class SingleItemEnrichmentRunnerTests
{
    private static readonly Guid ItemId = Guid.Parse("44444444-0000-0000-0000-000000000001");

    private sealed class Harness
    {
        public required SingleItemEnrichmentRunner Runner { get; init; }

        public required RecordingItemWriter Writer { get; init; }

        public required RecordingBackupStore Backup { get; init; }

        public required InMemoryCacheFileStore CacheStore { get; init; }

        public required FileRatingCache Cache { get; init; }

        public required StubRatingResolver Resolver { get; init; }

        public required FakeClock Clock { get; init; }
    }

    private static Harness Build(RatingResult result, bool dryRun)
    {
        var clock = new FakeClock();
        var resolver = new StubRatingResolver(result);
        var writer = new RecordingItemWriter();
        var backup = new RecordingBackupStore();
        var cacheStore = new InMemoryCacheFileStore();
        var cache = new FileRatingCache(cacheStore, clock);
        var breaker = new CircuitBreaker(clock);
        Func<PipelineOptions> options = () => new PipelineOptions { DryRun = dryRun };
        var pipeline = new RatingPipeline(
            resolver, writer, backup, cache, clock, breaker, options, NullLogger<RatingPipeline>.Instance);
        var runner = new SingleItemEnrichmentRunner(pipeline, NullLogger<SingleItemEnrichmentRunner>.Instance);
        return new Harness
        {
            Runner = runner,
            Writer = writer,
            Backup = backup,
            CacheStore = cacheStore,
            Cache = cache,
            Resolver = resolver,
            Clock = clock
        };
    }

    private static RatingWorkItem Movie(string tmdb = "111")
        => new(new RatingItemRef(ItemId, "A Movie"), ItemLevel.Movie, new Dictionary<string, string> { ["Tmdb"] = tmdb }, "myanimelist");

    private static RatingWorkItem MovieWithoutIds()
        => new(new RatingItemRef(ItemId, "A Movie"), ItemLevel.Movie, new Dictionary<string, string>(), "myanimelist");

    [Fact]
    public async Task Found_DryRunOff_WritesAndBacksUp()
    {
        var h = Build(RatingResult.ForScore(8.0f), dryRun: false);

        var outcome = await h.Runner.RunAsync(Movie(), CancellationToken.None);

        outcome.Should().Be(RatingOutcome.Updated);
        h.Writer.Writes.Should().ContainSingle().Which.Value.Should().Be(8.0f);
        h.Backup.Backups.Should().ContainSingle().Which.Should().Be(ItemId);
    }

    [Fact]
    public async Task Found_DryRun_SkipsWrite()
    {
        var h = Build(RatingResult.ForScore(8.0f), dryRun: true);

        var outcome = await h.Runner.RunAsync(Movie(), CancellationToken.None);

        outcome.Should().Be(RatingOutcome.SkippedDryRun);
        h.Writer.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task NoProviderIds_SkipsNoId_WithoutResolverCall()
    {
        var h = Build(RatingResult.ForScore(8.0f), dryRun: false);

        var outcome = await h.Runner.RunAsync(MovieWithoutIds(), CancellationToken.None);

        outcome.Should().Be(RatingOutcome.SkippedNoId);
        h.Resolver.CallCount.Should().Be(0);
        h.Writer.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task DoesNotFlushCache_FlushIsOwnedByFacade()
    {
        var h = Build(RatingResult.ForScore(8.0f), dryRun: true);

        await h.Runner.RunAsync(Movie(), CancellationToken.None);

        // The runner no longer flushes per item; the facade flushes the cache throttled (so a burst of
        // realtime items does not rewrite the whole cache file per event). The runner must not write.
        h.CacheStore.WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task DoesNotPruneOrphanBackups()
    {
        var h = Build(RatingResult.NoMatch(), dryRun: true);
        var orphan = Guid.Parse("99999999-9999-9999-9999-999999999999");
        await h.Backup.EnsureBackedUpAsync(orphan, 5.0f, CancellationToken.None);

        await h.Runner.RunAsync(Movie(), CancellationToken.None);

        // The single-item path must never prune: an unrelated item's backup survives.
        h.Backup.TryGet(orphan, out _).Should().BeTrue();
    }

    [Fact]
    public async Task DoesNotReInitializeCache_UnflushedEntrySurvives()
    {
        var h = Build(RatingResult.NoMatch(), dryRun: true);

        // Seed an unflushed in-memory cache entry (never persisted to the store).
        var seededKey = new RatingCacheKey("stub", "myanimelist", "Tmdb", "seed-999", ItemLevel.Movie);
        h.Cache.Set(seededKey, new RatingCacheEntry(RatingResolution.Found, 7.0f, h.Clock.UtcNow + TimeSpan.FromDays(1)));

        await h.Runner.RunAsync(Movie(), CancellationToken.None);

        // A re-init would clear the dict and reload the (empty) store, losing the seeded entry.
        h.Cache.TryGet(seededKey, out var entry).Should().BeTrue();
        entry.Score.Should().Be(7.0f);
    }
}
