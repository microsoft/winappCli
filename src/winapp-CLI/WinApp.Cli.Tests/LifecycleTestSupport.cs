// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Net;
using Microsoft.Extensions.Logging;

namespace WinApp.Cli.Tests;

/// <summary>
/// Deterministic <see cref="HttpMessageHandler"/> that serves canned responses keyed
/// by a predicate over the request URI, so network-facing services can be exercised
/// end-to-end without touching the real NuGet / GitHub / MS Store endpoints.
/// Records every request URI for assertions.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _rules = [];

    public List<Uri> Requests { get; } = [];

    /// <summary>Fallback status when no rule matches. Defaults to 404.</summary>
    public HttpStatusCode NotMatchedStatus { get; set; } = HttpStatusCode.NotFound;

    public FakeHttpMessageHandler When(Func<HttpRequestMessage, bool> match, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _rules.Add((match, respond));
        return this;
    }

    public FakeHttpMessageHandler WhenUriContains(string fragment, HttpStatusCode status, string content, string mediaType = "application/json")
        => When(
            r => r.RequestUri!.ToString().Contains(fragment, StringComparison.OrdinalIgnoreCase),
            _ => new HttpResponseMessage(status) { Content = new StringContent(content, System.Text.Encoding.UTF8, mediaType) });

    public FakeHttpMessageHandler WhenUriContains(string fragment, HttpStatusCode status, byte[] content)
        => When(
            r => r.RequestUri!.ToString().Contains(fragment, StringComparison.OrdinalIgnoreCase),
            _ => new HttpResponseMessage(status) { Content = new ByteArrayContent(content) });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        foreach (var (match, respond) in _rules)
        {
            if (match(request))
            {
                return Task.FromResult(respond(request));
            }
        }
        return Task.FromResult(new HttpResponseMessage(NotMatchedStatus)
        {
            Content = new StringContent(string.Empty),
        });
    }
}
