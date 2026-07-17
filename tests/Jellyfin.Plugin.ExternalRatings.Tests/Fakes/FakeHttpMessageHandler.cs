using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ExternalRatings.Tests.Fakes;

/// <summary>
/// A test <see cref="HttpMessageHandler"/> that returns canned responses and records every outgoing
/// request (method, uri, body) so tests can assert the URL/body without touching the network.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    public List<RecordedRequest> Requests { get; } = new();

    /// <summary>Always returns the given status and JSON body.</summary>
    public static FakeHttpMessageHandler Json(HttpStatusCode status, string body)
        => new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }));

    /// <summary>Always throws the given exception (network error / timeout simulation).</summary>
    public static FakeHttpMessageHandler Throws(Exception exception)
        => new((_, _) => throw exception);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string? body = null;
        if (request.Content is not null)
        {
            body = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, body));
        return await _responder(request, cancellationToken);
    }
}

/// <summary>A recorded outgoing request.</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Body);
