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

public class RatingPrefetcherTests
{
    private const string Target = "myanimelist";
    private const int DailyLimit = 1000;

    private static Guid NewId(int n) => Guid.Parse($"22222222-0000-0000-0000-0000000000{n:D2}");

    private static RatingWorkItem Item(int id, ItemLevel level, string provider, string providerId)
    {
        var ids = new Dictionary<string, string> { [provider] = providerId };
        return new RatingWorkItem(new RatingItemRef(NewId(id), level.ToString()), level, ids, Target);
    }

    private sealed class Harness
    {
        public Harness(IBatchRatingResolver resolver)
        {
            Cache = new FileRatingCache(new InMemoryCacheFileStore(), Clock);
            Cache.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
            Breaker = new CircuitBreaker(Clock);
            Counter = new DailyRequestCounter(Clock);
            Prefetcher = new RatingPrefetcher(
                resolver,
                Cache,
                Clock,
                Breaker,
                Counter,
                () => Options,
                () => DailyLimit,
                NullLogger<RatingPrefetcher>.Instance);
        }

        public FakeClock Clock { get; } = new();

        public FileRatingCache Cache { get; }

        public CircuitBreaker Breaker { get; }

        public DailyRequestCounter Counter { get; }

        public PipelineOptions Options { get; set; } = new();

        public RatingPrefetcher Prefetcher { get; }

        public bool TryGet(ItemLevel level, string provider, string id, out RatingCacheEntry entry)
            => Cache.TryGet(new RatingCacheKey("mdblist", Target, provider, id, level), out entry);
    }

    [Fact]
    public async Task PrefetchAsync_GroupsByLevelAndProvider_OneBatchCallPerGroup()
    {
        var resolver = new StubBatchRatingResolver(id => RatingResult.ForScore(8.0f));
        var h = new Harness(resolver);
        var items = new[]
        {
            Item(1, ItemLevel.Movie, "Tmdb", "100"),
            Item(2, ItemLevel.Movie, "Tmdb", "200"),
            Item(3, ItemLevel.Series, "Imdb", "tt300")
        };

        await h.Prefetcher.PrefetchAsync(items, CancellationToken.None);

        resolver.BatchCalls.Should().HaveCount(2);
        resolver.BatchCalls.Should().ContainSingle(c => c.Level == ItemLevel.Movie && c.Provider == "Tmdb")
            .Which.Ids.Should().BeEquivalentTo("100", "200");
        resolver.BatchCalls.Should().ContainSingle(c => c.Level == ItemLevel.Series && c.Provider == "Imdb")
            .Which.Ids.Should().BeEquivalentTo("tt300");
    }

    [Fact]
    public async Task PrefetchAsync_SeedsFoundIntoCache()
    {
        var resolver = new StubBatchRatingResolver(id => RatingResult.ForScore(7.5f));
        var h = new Harness(resolver);

        await h.Prefetcher.PrefetchAsync(new[] { Item(1, ItemLevel.Movie, "Tmdb", "100") }, CancellationToken.None);

        h.TryGet(ItemLevel.Movie, "Tmdb", "100", out var entry).Should().BeTrue();
        entry.Kind.Should().Be(RatingResolution.Found);
        entry.Score.Should().BeApproximately(7.5f, 0.001f);
    }

    [Fact]
    public async Task PrefetchAsync_SkipsItemsAlreadyCached()
    {
        var resolver = new StubBatchRatingResolver(id => RatingResult.ForScore(8.0f));
        var h = new Harness(resolver);
        // Pre-seed one movie so it is a cache hit and must be excluded from the batch.
        h.Cache.Set(
            new RatingCacheKey("mdblist", Target, "Tmdb", "100", ItemLevel.Movie),
            new RatingCacheEntry(RatingResolution.Found, 9.0f, h.Clock.UtcNow.AddDays(1)));

        await h.Prefetcher.PrefetchAsync(
            new[] { Item(1, ItemLevel.Movie, "Tmdb", "100"), Item(2, ItemLevel.Movie, "Tmdb", "200") },
            CancellationToken.None);

        resolver.BatchCalls.Should().ContainSingle()
            .Which.Ids.Should().BeEquivalentTo("200");
    }

    [Fact]
    public async Task PrefetchAsync_ChunksLargerGroupsByMaxBatchSize()
    {
        var resolver = new StubBatchRatingResolver(id => RatingResult.NoMatch()) { MaxBatchSize = 2 };
        var h = new Harness(resolver);
        var items = Enumerable.Range(1, 5).Select(i => Item(i, ItemLevel.Movie, "Tmdb", i.ToString())).ToArray();

        await h.Prefetcher.PrefetchAsync(items, CancellationToken.None);

        resolver.BatchCalls.Should().HaveCount(3); // 2 + 2 + 1
        resolver.BatchCalls.Sum(c => c.Ids.Length).Should().Be(5);
    }

    [Fact]
    public async Task PrefetchAsync_SeedsNoMatch_ButNotError()
    {
        var resolver = new StubBatchRatingResolver(id => id switch
        {
            "100" => RatingResult.ForScore(8.0f),
            "200" => RatingResult.NoMatch(),
            _ => RatingResult.ForError("boom")
        });
        var h = new Harness(resolver);

        await h.Prefetcher.PrefetchAsync(
            new[]
            {
                Item(1, ItemLevel.Movie, "Tmdb", "100"),
                Item(2, ItemLevel.Movie, "Tmdb", "200"),
                Item(3, ItemLevel.Movie, "Tmdb", "300")
            },
            CancellationToken.None);

        h.TryGet(ItemLevel.Movie, "Tmdb", "100", out var found).Should().BeTrue();
        found.Kind.Should().Be(RatingResolution.Found);
        h.TryGet(ItemLevel.Movie, "Tmdb", "200", out var noMatch).Should().BeTrue();
        noMatch.Kind.Should().Be(RatingResolution.NoMatch);
        h.TryGet(ItemLevel.Movie, "Tmdb", "300", out _).Should().BeFalse(); // errors are never cached
    }

    [Fact]
    public async Task PrefetchAsync_StopsWhenBudgetExhausted_NoBatchCalls()
    {
        var resolver = new StubBatchRatingResolver(id => RatingResult.ForScore(8.0f));
        var h = new Harness(resolver);
        h.Counter.InitializeFromColdStart(DailyLimit); // already at the limit

        await h.Prefetcher.PrefetchAsync(new[] { Item(1, ItemLevel.Movie, "Tmdb", "100") }, CancellationToken.None);

        resolver.BatchCalls.Should().BeEmpty();
        h.TryGet(ItemLevel.Movie, "Tmdb", "100", out _).Should().BeFalse();
    }

    [Fact]
    public async Task PrefetchAsync_SkipsWhenCircuitOpen()
    {
        var resolver = new StubBatchRatingResolver(id => RatingResult.ForScore(8.0f));
        var h = new Harness(resolver);
        h.Breaker.RecordRateLimited(); // open immediately

        await h.Prefetcher.PrefetchAsync(new[] { Item(1, ItemLevel.Movie, "Tmdb", "100") }, CancellationToken.None);

        resolver.BatchCalls.Should().BeEmpty();
    }
}
