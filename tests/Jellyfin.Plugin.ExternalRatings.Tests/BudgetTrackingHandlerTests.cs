using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings.Core;
using Jellyfin.Plugin.ExternalRatings.Infrastructure;
using Jellyfin.Plugin.ExternalRatings.Persistence;
using Jellyfin.Plugin.ExternalRatings.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

public class BudgetTrackingHandlerTests
{
    private const int DailyLimit = 1000;

    private static (HttpClient Client, DailyRequestCounter Counter, CircuitBreaker Breaker) Build(FakeHttpMessageHandler inner)
    {
        var clock = new FakeClock();
        var counter = new DailyRequestCounter(clock);
        var breaker = new CircuitBreaker(clock);
        var handler = new BudgetTrackingHandler(
            counter,
            breaker,
            () => DailyLimit,
            NullLogger<BudgetTrackingHandler>.Instance)
        {
            InnerHandler = inner
        };
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.mdblist.com/") };
        return (client, counter, breaker);
    }

    private static FakeHttpMessageHandler WithHeaders(HttpStatusCode status, params (string Name, string Value)[] headers)
        => new((_, _) =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent("{}") };
            foreach (var (name, value) in headers)
            {
                response.Headers.Add(name, value);
            }

            return Task.FromResult(response);
        });

    [Fact]
    public async Task SendAsync_CountsEveryRequest()
    {
        var inner = FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}");
        var (client, counter, _) = Build(inner);

        await client.GetAsync("tmdb/movie/1", CancellationToken.None);
        await client.GetAsync("tmdb/movie/2", CancellationToken.None);

        counter.Count.Should().Be(2);
    }

    [Fact]
    public async Task SendAsync_RateLimitHeaders_SyncsUsedCount()
    {
        // 700 remaining of 1000 ⇒ 300 used; the authoritative sync overrides the local +1.
        var inner = WithHeaders(HttpStatusCode.OK, ("X-RateLimit-Limit", "1000"), ("X-RateLimit-Remaining", "700"));
        var (client, counter, _) = Build(inner);

        await client.GetAsync("tmdb/movie/1", CancellationToken.None);

        counter.Count.Should().Be(300);
    }

    [Fact]
    public async Task SendAsync_RemainingHeaderOnly_UsesConfiguredLimitAsFallback()
    {
        // No X-RateLimit-Limit ⇒ fallback to the configured daily limit: 1000 - 900 = 100 used.
        var inner = WithHeaders(HttpStatusCode.OK, ("X-RateLimit-Remaining", "900"));
        var (client, counter, _) = Build(inner);

        await client.GetAsync("tmdb/movie/1", CancellationToken.None);

        counter.Count.Should().Be(100);
    }

    [Fact]
    public async Task SendAsync_NoRateLimitHeaders_JustCounts()
    {
        var inner = FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}");
        var (client, counter, _) = Build(inner);

        await client.GetAsync("tmdb/movie/1", CancellationToken.None);

        counter.Count.Should().Be(1);
    }

    [Fact]
    public async Task SendAsync_429_TripsBreakerImmediately()
    {
        var inner = FakeHttpMessageHandler.Json(HttpStatusCode.TooManyRequests, "{\"error\":\"rate limit\"}");
        var (client, _, breaker) = Build(inner);

        await client.GetAsync("tmdb/movie/1", CancellationToken.None);

        breaker.IsOpen.Should().BeTrue();
    }
}
