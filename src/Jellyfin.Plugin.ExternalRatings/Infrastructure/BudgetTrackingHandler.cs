using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExternalRatings.Core;
using Jellyfin.Plugin.ExternalRatings.Persistence;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExternalRatings.Infrastructure;

/// <summary>
/// A <see cref="DelegatingHandler"/> that feeds the budget and circuit-breaker seams off every
/// mdblist HTTP call (spec §7.3, §10, M10). Each attempt increments the
/// <see cref="DailyRequestCounter"/> (retries count too); an authoritative server count derived from
/// the <c>X-RateLimit-*</c> headers reconciles the local counter; and an HTTP 429 trips the shared
/// <see cref="CircuitBreaker"/> immediately.
/// </summary>
internal sealed class BudgetTrackingHandler : DelegatingHandler
{
    private const string RemainingHeader = "X-RateLimit-Remaining";
    private const string LimitHeader = "X-RateLimit-Limit";

    private readonly DailyRequestCounter _counter;
    private readonly CircuitBreaker _breaker;
    private readonly Func<int> _dailyLimitAccessor;
    private readonly ILogger<BudgetTrackingHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="BudgetTrackingHandler"/> class.</summary>
    /// <param name="counter">The shared daily request counter.</param>
    /// <param name="breaker">The shared circuit breaker.</param>
    /// <param name="dailyLimitAccessor">Fallback daily limit when the response omits <c>X-RateLimit-Limit</c>.</param>
    /// <param name="logger">The logger.</param>
    public BudgetTrackingHandler(
        DailyRequestCounter counter,
        CircuitBreaker breaker,
        Func<int> dailyLimitAccessor,
        ILogger<BudgetTrackingHandler> logger)
    {
        _counter = counter;
        _breaker = breaker;
        _dailyLimitAccessor = dailyLimitAccessor;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Count the attempt before it leaves; retries count too (§7.3).
        _counter.RecordRequest();

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _logger.LogWarning("mdblist returned HTTP 429; tripping the circuit breaker (M10)");
            _breaker.RecordRateLimited();
        }

        ReconcileFromHeaders(response);
        return response;
    }

    private void ReconcileFromHeaders(HttpResponseMessage response)
    {
        // X-RateLimit-Remaining is what's LEFT; the counter tracks what's USED, so used = limit - remaining.
        if (!TryGetIntHeader(response, RemainingHeader, out var remaining))
        {
            return;
        }

        var limit = TryGetIntHeader(response, LimitHeader, out var headerLimit)
            ? headerLimit
            : _dailyLimitAccessor();

        var used = Math.Max(0, limit - remaining);
        _counter.SyncFromHeader(used);
    }

    private static bool TryGetIntHeader(HttpResponseMessage response, string name, out int value)
    {
        value = 0;
        if (!response.Headers.TryGetValues(name, out var values))
        {
            return false;
        }

        foreach (var raw in values)
        {
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }

        return false;
    }
}
