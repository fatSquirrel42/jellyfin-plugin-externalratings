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

public class RatingPipelineTests
{
    private static readonly Guid Id1 = Guid.Parse("11111111-0000-0000-0000-000000000001");

    private static Guid NewId(int n) => Guid.Parse($"11111111-0000-0000-0000-0000000000{n:D2}");

    private static RatingWorkItem Item(Guid id, ItemLevel level, params (string Provider, string Id)[] ids)
    {
        var dict = new Dictionary<string, string>();
        foreach (var (provider, value) in ids)
        {
            dict[provider] = value;
        }

        return new RatingWorkItem(new RatingItemRef(id, level.ToString()), level, dict, "myanimelist");
    }

    private static RatingWorkItem Movie(Guid id, string tmdb = "111") => Item(id, ItemLevel.Movie, ("Tmdb", tmdb));

    private sealed class Harness
    {
        public Harness(IRatingResolver resolver)
            : this(new FixedRouter(resolver))
        {
        }

        public Harness(IRatingRouter router)
        {
            Backup = new RecordingBackupStore(Recorder);
            Writer = new RecordingItemWriter(Recorder);
            Cache = new FileRatingCache(new InMemoryCacheFileStore(), Clock);
            Breaker = new CircuitBreaker(Clock);
            Pipeline = new RatingPipeline(
                router, Writer, Backup, Cache, Clock, _ => Breaker, () => Options, NullLogger<RatingPipeline>.Instance);
        }

        public FakeClock Clock { get; } = new();

        public CallRecorder Recorder { get; } = new();

        public RecordingBackupStore Backup { get; }

        public RecordingItemWriter Writer { get; }

        public FileRatingCache Cache { get; }

        public CircuitBreaker Breaker { get; }

        public RunSummary Summary { get; } = new();

        public PipelineOptions Options { get; set; } = new() { DryRun = false };

        public RatingPipeline Pipeline { get; }

        public Task<RatingOutcome> Run(RatingWorkItem item) => Pipeline.ProcessItemAsync(item, Summary, CancellationToken.None);
    }

    // ---- Found ----

    [Fact]
    public async Task Found_WritesScore_WhenDifferenceExceedsEpsilon()
    {
        var h = new Harness(new StubRatingResolver(RatingResult.ForScore(8.5f)));
        h.Writer.SeedRating(Id1, 6.0f);

        var outcome = await h.Run(Movie(Id1));

        outcome.Should().Be(RatingOutcome.Updated);
        h.Writer.GetCommunityRating(new RatingItemRef(Id1, null)).Should().Be(8.5f);
        h.Summary.Updated.Should().Be(1);
    }

    [Fact]
    public async Task Found_SkipsWrite_WhenWithinEpsilon()
    {
        var h = new Harness(new StubRatingResolver(RatingResult.ForScore(8.5f)));
        h.Writer.SeedRating(Id1, 8.52f);

        var outcome = await h.Run(Movie(Id1));

        outcome.Should().Be(RatingOutcome.SkippedNoChange);
        h.Writer.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Found_JustWithinEpsilon_NoWrite()
    {
        // Difference ~0.04, comfortably inside the 0.05 epsilon.
        var h = new Harness(new StubRatingResolver(RatingResult.ForScore(8.5f)));
        h.Writer.SeedRating(Id1, 8.54f);

        var outcome = await h.Run(Movie(Id1));

        outcome.Should().Be(RatingOutcome.SkippedNoChange);
    }

    [Fact]
    public async Task Found_JustAboveEpsilon_Writes()
    {
        // Difference ~0.06, just above the 0.05 epsilon.
        var h = new Harness(new StubRatingResolver(RatingResult.ForScore(8.5f)));
        h.Writer.SeedRating(Id1, 8.44f);

        var outcome = await h.Run(Movie(Id1));

        outcome.Should().Be(RatingOutcome.Updated);
    }

    [Fact]
    public async Task Found_WritesWhenNoCurrentRating()
    {
        var h = new Harness(new StubRatingResolver(RatingResult.ForScore(8.5f)));

        var outcome = await h.Run(Movie(Id1));

        outcome.Should().Be(RatingOutcome.Updated);
    }

    [Fact]
    public async Task Found_BacksUpBeforeWrite()
    {
        var h = new Harness(new StubRatingResolver(RatingResult.ForScore(8.5f)));
        h.Writer.SeedRating(Id1, 6.0f);

        await h.Run(Movie(Id1));

        h.Recorder.IndexOf($"Backup:{Id1}").Should().BeGreaterThanOrEqualTo(0);
        h.Recorder.IndexOf($"Backup:{Id1}").Should().BeLessThan(h.Recorder.IndexOf($"Write:{Id1}"));
    }

    [Fact]
    public async Task Found_BacksUpOriginalValue()
    {
        var h = new Harness(new StubRatingResolver(RatingResult.ForScore(8.5f)));
        h.Writer.SeedRating(Id1, 6.0f);

        await h.Run(Movie(Id1));

        h.Backup.TryGet(Id1, out var entry).Should().BeTrue();
        entry.OriginalRating.Should().Be(6.0f);
    }

    [Fact]
    public async Task Found_CachesPositive_SecondCallSkipsResolver()
    {
        var resolver = new StubRatingResolver(RatingResult.ForScore(8.5f));
        var h = new Harness(resolver);
        h.Writer.SeedRating(Id1, 6.0f);

        await h.Run(Movie(Id1));
        await h.Run(Movie(Id1));

        resolver.CallCount.Should().Be(1);
        h.Summary.CacheHits.Should().Be(1);
    }

    [Fact]
    public async Task DryRunOn_Found_NoWriteNoBackup_ButCaches()
    {
        var resolver = new StubRatingResolver(RatingResult.ForScore(8.5f));
        var h = new Harness(resolver) { Options = new PipelineOptions { DryRun = true } };
        h.Writer.SeedRating(Id1, 6.0f);

        var outcome = await h.Run(Movie(Id1));

        outcome.Should().Be(RatingOutcome.SkippedDryRun);
        h.Writer.Writes.Should().BeEmpty();
        h.Backup.Backups.Should().BeEmpty();
        h.Cache.TryGet(new RatingCacheKey("stub", "myanimelist", "Tmdb", "111", ItemLevel.Movie), out _).Should().BeTrue();
    }

    [Fact]
    public async Task DryRun_ReadPerItem_ToggleTakesEffectOnNextItem()
    {
        var h = new Harness(new StubRatingResolver(RatingResult.ForScore(8.5f))) { Options = new PipelineOptions { DryRun = true } };
        var id2 = NewId(2);
        h.Writer.SeedRating(Id1, 6.0f);
        h.Writer.SeedRating(id2, 6.0f);

        var first = await h.Run(Movie(Id1));
        h.Options = new PipelineOptions { DryRun = false };
        var second = await h.Run(Movie(id2));

        first.Should().Be(RatingOutcome.SkippedDryRun);
        second.Should().Be(RatingOutcome.Updated);
    }

    [Fact]
    public async Task CacheHit_ReEvaluatesEpsilonAgainstCurrentRating()
    {
        var resolver = new StubRatingResolver(RatingResult.ForScore(8.5f));
        var h = new Harness(resolver);
        h.Writer.SeedRating(Id1, 6.0f);
        await h.Run(Movie(Id1)); // Updated; caches 8.5

        // Now the current rating already matches; a cache hit should skip the write.
        var outcome = await h.Run(Movie(Id1));

        outcome.Should().Be(RatingOutcome.SkippedNoChange);
        resolver.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task H14_NullScore_TreatedAsError()
    {
        var h = new Harness(new StubRatingResolver(new RatingResult(RatingResolution.Found, null)));
        h.Writer.SeedRating(Id1, 6.0f);

        var outcome = await h.Run(Movie(Id1));

        outcome.Should().Be(RatingOutcome.Error);
        h.Writer.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task H14_ScoreBelowZero_TreatedAsError()
    {
        var h = new Harness(new StubRatingResolver(new RatingResult(RatingResolution.Found, -1f)));

        var outcome = await h.Run(Movie(Id1));

        outcome.Should().Be(RatingOutcome.Error);
    }

    [Fact]
    public async Task H14_ScoreAboveTen_TreatedAsError()
    {
        var h = new Harness(new StubRatingResolver(new RatingResult(RatingResolution.Found, 11f)));

        var outcome = await h.Run(Movie(Id1));

        outcome.Should().Be(RatingOutcome.Error);
    }

    // ---- NoMatch ----

    [Fact]
    public async Task NoMatch_Default_WritesNothing()
    {
        var h = new Harness(new StubRatingResolver(RatingResult.NoMatch()));
        h.Writer.SeedRating(Id1, 6.0f);

        var outcome = await h.Run(Movie(Id1));

        outcome.Should().Be(RatingOutcome.NoMatch);
        h.Writer.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task NoMatch_ClearField_BacksUpThenClears()
    {
        var h = new Harness(new StubRatingResolver(RatingResult.NoMatch()))
        {
            Options = new PipelineOptions { DryRun = false, NoMatchBehavior = NoMatchBehavior.ClearField }
        };
        h.Writer.SeedRating(Id1, 6.0f);

        var outcome = await h.Run(Movie(Id1));

        outcome.Should().Be(RatingOutcome.Cleared);
        h.Writer.Writes.Should().ContainSingle();
        h.Writer.Writes[0].Value.Should().BeNull();
        h.Recorder.IndexOf($"Backup:{Id1}").Should().BeLessThan(h.Recorder.IndexOf($"Write:{Id1}"));
    }

    [Fact]
    public async Task NoMatch_ClearField_DryRun_DoesNotClear()
    {
        var h = new Harness(new StubRatingResolver(RatingResult.NoMatch()))
        {
            Options = new PipelineOptions { DryRun = true, NoMatchBehavior = NoMatchBehavior.ClearField }
        };
        h.Writer.SeedRating(Id1, 6.0f);

        var outcome = await h.Run(Movie(Id1));

        outcome.Should().Be(RatingOutcome.SkippedDryRun);
        h.Writer.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task NoMatch_CachesNegative_SecondCallSkipsResolver()
    {
        var resolver = new StubRatingResolver(RatingResult.NoMatch());
        var h = new Harness(resolver);

        await h.Run(Movie(Id1));
        var outcome = await h.Run(Movie(Id1));

        resolver.CallCount.Should().Be(1);
        outcome.Should().Be(RatingOutcome.NoMatch);
    }

    [Fact]
    public async Task NoMatch_Cached_ThenClearFieldToggledOn_ClearsWithoutResolver()
    {
        // Regression (F): a cached NoMatch must re-apply the ClearField decision on later passes instead
        // of waiting out the negative-cache TTL. First pass (LeaveExisting) caches the NoMatch; the second
        // pass with ClearField must clear the field straight from cache, no resolver call.
        var resolver = new StubRatingResolver(RatingResult.NoMatch());
        var h = new Harness(resolver);
        h.Writer.SeedRating(Id1, 6.0f);

        var first = await h.Run(Movie(Id1));
        h.Options = new PipelineOptions { DryRun = false, NoMatchBehavior = NoMatchBehavior.ClearField };
        var second = await h.Run(Movie(Id1));

        first.Should().Be(RatingOutcome.NoMatch);
        second.Should().Be(RatingOutcome.Cleared);
        resolver.CallCount.Should().Be(1);
        h.Writer.Writes.Should().ContainSingle();
        h.Writer.Writes[0].Value.Should().BeNull();
    }

    [Fact]
    public async Task NoMatch_WithUnusedAlternatives_IncrementsH12()
    {
        var h = new Harness(new StubRatingResolver(RatingResult.NoMatch()));
        var item = Item(Id1, ItemLevel.Series, ("Tmdb", "111"), ("Tvdb", "333"));

        await h.Run(item);

        h.Summary.NoMatchWithUnusedAlternatives.Should().Be(1);
    }

    [Fact]
    public async Task NoMatch_NoAlternatives_DoesNotIncrementH12()
    {
        var h = new Harness(new StubRatingResolver(RatingResult.NoMatch()));

        await h.Run(Movie(Id1));

        h.Summary.NoMatchWithUnusedAlternatives.Should().Be(0);
    }

    // ---- NotSupported / No id ----

    [Fact]
    public async Task NoId_Default_Skips_WithoutResolverOrCache()
    {
        var resolver = new StubRatingResolver(RatingResult.ForScore(8.5f));
        var h = new Harness(resolver);
        var item = Item(Id1, ItemLevel.Movie); // no provider ids

        var outcome = await h.Run(item);

        outcome.Should().Be(RatingOutcome.SkippedNoId);
        resolver.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task UnsupportedLevel_Season_Skips()
    {
        var resolver = new StubRatingResolver(RatingResult.ForScore(8.5f));
        var h = new Harness(resolver);
        var item = Item(Id1, ItemLevel.Season, ("Tmdb", "111"));

        var outcome = await h.Run(item);

        outcome.Should().Be(RatingOutcome.NotSupported);
        resolver.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task UnsupportedLevel_ClearField_BacksUpThenClears()
    {
        var h = new Harness(new StubRatingResolver(RatingResult.ForScore(8.5f)))
        {
            Options = new PipelineOptions { DryRun = false, UnsupportedLevelBehavior = UnsupportedLevelBehavior.ClearField }
        };
        h.Writer.SeedRating(Id1, 6.0f);
        var item = Item(Id1, ItemLevel.Season, ("Tmdb", "111"));

        var outcome = await h.Run(item);

        outcome.Should().Be(RatingOutcome.Cleared);
        h.Writer.Writes[0].Value.Should().BeNull();
        h.Recorder.IndexOf($"Backup:{Id1}").Should().BeLessThan(h.Recorder.IndexOf($"Write:{Id1}"));
    }

    // --- routing: no route is a write decision, not a silent skip ---

    private static RatingWorkItem ItemWithSource(Guid id, ItemLevel level, string source, params (string Provider, string Id)[] ids)
    {
        var dict = new Dictionary<string, string>();
        foreach (var (provider, value) in ids)
        {
            dict[provider] = value;
        }

        return new RatingWorkItem(new RatingItemRef(id, level.ToString()), level, dict, source);
    }

    private static IRatingRouter TwoBackendRouter()
    {
        // Mirrors the shipped pair: the offline dataset (imdb, all levels) and mdblist (many
        // sources, movies and series only).
        var dataset = new StubRatingResolver(RatingResult.ForScore(8.0f))
        {
            Key = "imdb-dataset",
            SupportedRatingSources = new[] { new RatingSourceInfo("imdb", "IMDb", "0–10", false) },
            SupportedInputProviders = new Dictionary<ItemLevel, IReadOnlyList<string>>
            {
                [ItemLevel.Movie] = new[] { "Imdb" },
                [ItemLevel.Series] = new[] { "Imdb" },
                [ItemLevel.Season] = new[] { "Imdb" },
                [ItemLevel.Episode] = new[] { "Imdb" }
            }
        };

        var mdblist = new StubRatingResolver(RatingResult.ForScore(7.0f))
        {
            Key = "mdblist",
            SupportedRatingSources = new[]
            {
                new RatingSourceInfo("imdb", "IMDb", "0–10", false),
                new RatingSourceInfo("myanimelist", "MyAnimeList", "0–10", false)
            },
            SupportedInputProviders = new Dictionary<ItemLevel, IReadOnlyList<string>>
            {
                [ItemLevel.Movie] = new[] { "Tmdb", "Imdb" },
                [ItemLevel.Series] = new[] { "Tmdb", "Imdb" }
            }
        };

        return new RatingRouter(new IRatingResolver[] { dataset, mdblist });
    }

    [Fact]
    public async Task SourceWithoutScoresAtThisLevel_IsClearedNotSkipped()
    {
        // The behaviour this design deliberately chose: an episode in a library set to MyAnimeList
        // has no route at all, and the field is emptied rather than left alone, so it always shows
        // the configured source or nothing. Guard it — otherwise it reads like a bug and gets
        // "fixed" back into a silent skip (see docs/lessons-learned.md lesson 8 for the inverse).
        var h = new Harness(TwoBackendRouter())
        {
            Options = new PipelineOptions { DryRun = false, UnsupportedLevelBehavior = UnsupportedLevelBehavior.ClearField }
        };
        h.Writer.SeedRating(Id1, 8.3f);

        var outcome = await h.Run(ItemWithSource(Id1, ItemLevel.Episode, "myanimelist", ("Imdb", "tt2301451")));

        outcome.Should().Be(RatingOutcome.Cleared);
        h.Writer.Writes[0].Value.Should().BeNull();
        h.Recorder.IndexOf($"Backup:{Id1}").Should().BeLessThan(h.Recorder.IndexOf($"Write:{Id1}"));
    }

    [Fact]
    public async Task SourceWithoutScoresAtThisLevel_LeaveExisting_KeepsTheRating()
    {
        var h = new Harness(TwoBackendRouter())
        {
            Options = new PipelineOptions { DryRun = false, UnsupportedLevelBehavior = UnsupportedLevelBehavior.LeaveExisting }
        };
        h.Writer.SeedRating(Id1, 8.3f);

        var outcome = await h.Run(ItemWithSource(Id1, ItemLevel.Episode, "myanimelist", ("Imdb", "tt2301451")));

        outcome.Should().Be(RatingOutcome.NotSupported);
        h.Writer.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task AnEpisodeOnImdb_RoutesToTheDataset()
    {
        var h = new Harness(TwoBackendRouter()) { Options = new PipelineOptions { DryRun = false } };

        var outcome = await h.Run(ItemWithSource(Id1, ItemLevel.Episode, "imdb", ("Imdb", "tt2301451")));

        outcome.Should().Be(RatingOutcome.Updated);
        h.Writer.Writes[0].Value.Should().Be(8.0f);
    }

    [Fact]
    public async Task AMovieOnImdbWithoutAnImdbId_FallsBackToMdblist()
    {
        // The fallback the routing rule exists for: no IMDb id, but mdblist reaches the IMDb score
        // through the Tmdb id. The 7.0 is mdblist's stub, so the write proves which backend ran.
        var h = new Harness(TwoBackendRouter()) { Options = new PipelineOptions { DryRun = false } };

        var outcome = await h.Run(ItemWithSource(Id1, ItemLevel.Movie, "imdb", ("Tmdb", "129")));

        outcome.Should().Be(RatingOutcome.Updated);
        h.Writer.Writes[0].Value.Should().Be(7.0f);
    }

    [Fact]
    public async Task NoUsableIdAtAll_CountsAsSkippedNoId_NotNotSupported()
    {
        // Same write decision, different accounting: the source *does* serve this level, the item
        // just has no id for it.
        var h = new Harness(TwoBackendRouter())
        {
            Options = new PipelineOptions { DryRun = false, UnsupportedLevelBehavior = UnsupportedLevelBehavior.LeaveExisting }
        };

        var outcome = await h.Run(ItemWithSource(Id1, ItemLevel.Episode, "imdb", ("Tvdb", "8951947")));

        outcome.Should().Be(RatingOutcome.SkippedNoId);
    }

    [Fact]
    public async Task NoId_ClearField_DryRun_DoesNotClear()
    {
        var h = new Harness(new StubRatingResolver(RatingResult.ForScore(8.5f)))
        {
            Options = new PipelineOptions { DryRun = true, UnsupportedLevelBehavior = UnsupportedLevelBehavior.ClearField }
        };
        h.Writer.SeedRating(Id1, 6.0f);
        var item = Item(Id1, ItemLevel.Movie); // no id

        var outcome = await h.Run(item);

        outcome.Should().Be(RatingOutcome.SkippedDryRun);
        h.Writer.Writes.Should().BeEmpty();
    }

    // ---- Error ----

    [Fact]
    public async Task Error_WritesNothing_AndNotPersistentlyCached()
    {
        var h = new Harness(new StubRatingResolver(RatingResult.ForError("boom")));
        h.Writer.SeedRating(Id1, 6.0f);

        var outcome = await h.Run(Movie(Id1));

        outcome.Should().Be(RatingOutcome.Error);
        h.Writer.Writes.Should().BeEmpty();
        h.Cache.TryGet(new RatingCacheKey("stub", "myanimelist", "Tmdb", "111", ItemLevel.Movie), out _).Should().BeFalse();
    }

    [Fact]
    public async Task Error_PopulatesInMemoryErrorCache_SkipsResolverWithinTtl()
    {
        var resolver = new StubRatingResolver(RatingResult.ForError("boom"));
        var h = new Harness(resolver);

        await h.Run(Movie(Id1));
        await h.Run(Movie(Id1));

        resolver.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Error_ErrorCacheExpires_ResolverCalledAgain()
    {
        var resolver = new StubRatingResolver(RatingResult.ForError("boom"));
        var h = new Harness(resolver);

        await h.Run(Movie(Id1));
        h.Clock.Advance(TimeSpan.FromHours(2));
        await h.Run(Movie(Id1));

        resolver.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task Error_RecordsCircuitBreakerError_OpensAfterFive()
    {
        var resolver = new StubRatingResolver(RatingResult.ForError("boom"));
        var h = new Harness(resolver);

        for (var i = 1; i <= 5; i++)
        {
            await h.Run(Movie(NewId(i), tmdb: i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        h.Breaker.State.Should().Be(CircuitState.Open);
    }

    [Fact]
    public async Task CircuitOpen_SkipsResolver()
    {
        var resolver = new StubRatingResolver(RatingResult.ForScore(8.5f));
        var h = new Harness(resolver);
        h.Breaker.RecordRateLimited();

        var outcome = await h.Run(Movie(Id1));

        outcome.Should().Be(RatingOutcome.CircuitOpen);
        resolver.CallCount.Should().Be(0);
        h.Summary.CircuitOpenSkips.Should().Be(1);
    }

    [Fact]
    public async Task Found_LeavesCircuitClosed()
    {
        var h = new Harness(new StubRatingResolver(RatingResult.ForScore(8.5f)));

        await h.Run(Movie(Id1));

        h.Breaker.State.Should().Be(CircuitState.Closed);
    }

    // ---- Single-flight ----

    [Fact]
    public async Task ConcurrentSameItem_ResolverCalledOnce()
    {
        var started = new SemaphoreSlim(0, 1);
        var release = new TaskCompletionSource();
        var resolver = new StubRatingResolver(async (_, _) =>
        {
            started.Release();
            await release.Task;
            return RatingResult.ForScore(8.5f);
        });
        var h = new Harness(resolver);

        var first = h.Run(Movie(Id1));
        await started.WaitAsync();
        var second = h.Run(Movie(Id1));
        release.SetResult();
        await Task.WhenAll(first, second);

        resolver.CallCount.Should().Be(1);
    }

    // ---- Summary ----

    [Fact]
    public void Summary_HasNonEmptyRunId()
    {
        var summary = new RunSummary();

        summary.RunId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void PipelineOptions_DefaultsDryRunOn()
    {
        new PipelineOptions().DryRun.Should().BeTrue();
    }
}
