// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Net;
using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Services.Learn;

internal sealed record LearnSearchResult(string Title, Uri Url, string Excerpt);

internal interface ILearnSearchClient
{
    Task<IReadOnlyList<LearnSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default);
}

/// <summary>
/// Searches public Learn documentation. An injected HTTP client remains owned by the caller.
/// </summary>
internal sealed class LearnSearchClient(HttpClient httpClient, ILogger<LearnSearchClient>? logger = null) : ILearnSearchClient, IDisposable
{
    private readonly bool _ownsHttpClient;
    private readonly ILogger<LearnSearchClient> _logger = logger ?? NullLogger<LearnSearchClient>.Instance;
    internal static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(3);
    internal const int MaxResults = 3;
    internal const int MaxExcerptLength = 400;
    internal const int MaxTitleLength = 200;
    private const long MaxResponseBytes = 1024 * 1024;
    private const string ProtocolVersion = "2025-03-26";
    private const string SearchTool = "microsoft_docs_search";
    private static readonly Uri Endpoint = new("https://learn.microsoft.com/api/mcp");

    public LearnSearchClient(ILogger<LearnSearchClient> logger) : this(new HttpClient(), logger)
    {
        _ownsHttpClient = true;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            httpClient.Dispose();
        }
    }

    public async Task<IReadOnlyList<LearnSearchResult>> SearchAsync(
        string query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(SearchTimeout);
        string? sessionId = null;
        try
        {
            try
            {
                // The full SDK capability model includes obsolete features this client never advertises.
                var initialized = await CallAsync("initialize", new LearnInitializeParams
                {
                    ProtocolVersion = ProtocolVersion,
                    Capabilities = new JsonObject(),
                    ClientInfo = new Implementation { Name = "winapp", Version = VersionHelper.GetVersionString() }
                }, LearnJsonContext.Default.LearnInitializeParams, 1, null, deadline.Token).ConfigureAwait(false);
                sessionId = initialized.SessionId;
                if (RequiredString(initialized.Result, "protocolVersion") != ProtocolVersion
                    || !RequiredObject(initialized.Result, "capabilities").TryGetProperty("tools", out var toolsCapability)
                    || toolsCapability.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("Microsoft Learn did not negotiate the expected documentation search protocol.");
                }
                using (var request = CreateRequest("notifications/initialized", new InitializedNotificationParams(),
                    LearnJsonContext.Default.InitializedNotificationParams, null, sessionId))
                using (var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    if (response.StatusCode != HttpStatusCode.Accepted)
                    {
                        throw new InvalidDataException("Microsoft Learn did not acknowledge initialization.");
                    }
                }

                int requestId = 2;
                JsonElement? tool = null;
                var listRequest = new ListToolsRequestParams();
                var cursors = new HashSet<string>(StringComparer.Ordinal);
                do
                {
                    var page = await CallAsync("tools/list", listRequest, LearnJsonContext.Default.ListToolsRequestParams,
                        requestId++, sessionId, deadline.Token).ConfigureAwait(false);
                    if (!page.Result.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
                    {
                        throw new InvalidDataException("Microsoft Learn returned an invalid tool list.");
                    }
                    foreach (var item in tools.EnumerateArray())
                    {
                        if (RequiredString(item, "name") == SearchTool)
                        {
                            tool = item;
                            break;
                        }
                    }
                    listRequest.Cursor = null;
                    if (tool is null && page.Result.TryGetProperty("nextCursor", out var cursor) && cursor.ValueKind != JsonValueKind.Null)
                    {
                        if (cursor.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(cursor.GetString())
                            || !cursors.Add(cursor.GetString()!))
                        {
                            throw new InvalidDataException("Microsoft Learn returned an invalid or repeated tool-list cursor.");
                        }
                        listRequest.Cursor = cursor.GetString();
                    }
                }
                while (tool is null && listRequest.Cursor is not null);
                if (tool is null
                    || !RequiredObject(tool.Value, "inputSchema").TryGetProperty("properties", out var properties)
                    || properties.ValueKind != JsonValueKind.Object
                    || !properties.TryGetProperty("query", out var querySchema)
                    || querySchema.ValueKind != JsonValueKind.Object
                    || !querySchema.TryGetProperty("type", out var queryType)
                    || queryType.ValueKind != JsonValueKind.String
                    || queryType.GetString() != "string")
                {
                    throw new InvalidDataException("Microsoft Learn no longer advertises the expected microsoft_docs_search query input.");
                }

                var called = await CallAsync("tools/call", new CallToolRequestParams
                {
                    Name = SearchTool,
                    Arguments = new Dictionary<string, JsonElement>
                    {
                        ["query"] = JsonSerializer.SerializeToElement(query, LearnJsonContext.Default.String)
                    }
                }, LearnJsonContext.Default.CallToolRequestParams, requestId, sessionId, deadline.Token).ConfigureAwait(false);
                var result = called.Result;
                if (result.TryGetProperty("isError", out var error))
                {
                    if (error.ValueKind == JsonValueKind.True)
                    {
                        throw new McpException("Microsoft Learn documentation search failed.");
                    }
                    if (error.ValueKind != JsonValueKind.False)
                    {
                        throw new InvalidDataException("Microsoft Learn returned an invalid tool error flag.");
                    }
                }
                if (result.TryGetProperty("structuredContent", out var structured) && structured.ValueKind != JsonValueKind.Null)
                {
                    return ParseResults(structured);
                }

                // Learn also returns its result object as JSON inside one text block.
                if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array
                    || content.GetArrayLength() != 1 || RequiredString(content[0], "type") != "text")
                {
                    throw new InvalidDataException("Microsoft Learn returned no documentation search result object.");
                }
                using var document = JsonDocument.Parse(RequiredString(content[0], "text"));
                return ParseResults(document.RootElement);
            }
            finally
            {
                if (sessionId is not null)
                {
                    await CloseSessionAsync(sessionId, deadline.Token).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("Microsoft Learn documentation search exceeded its 3-second timeout.", ex);
        }
    }

    private async Task<(JsonElement Result, string? SessionId)> CallAsync<T>(
        string method, T parameters, JsonTypeInfo<T> typeInfo, int id, string? sessionId, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, parameters, typeInfo, id, sessionId);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string? responseSession = null;
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var values))
        {
            var sessions = values.ToArray();
            if (sessions.Length != 1 || sessions[0].Length == 0 || sessions[0].Any(c => c < 0x21 || c > 0x7E))
            {
                throw new InvalidDataException("Microsoft Learn returned an invalid session header.");
            }
            responseSession = sessions[0];
        }
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            await response.Content.LoadIntoBufferAsync(MaxResponseBytes, cancellationToken).ConfigureAwait(false);
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return (ReadRpcResult(document.RootElement, id, allowNotification: false)
                ?? throw new InvalidDataException("Microsoft Learn returned no request result."), responseSession);
        }
        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = new BoundedResponseStream(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
            await foreach (var item in SseParser.Create(stream).EnumerateAsync(cancellationToken).ConfigureAwait(false))
            {
                if (item.EventType != "message" || item.Data.Length == 0)
                {
                    continue;
                }
                using var document = JsonDocument.Parse(item.Data);
                if (ReadRpcResult(document.RootElement, id, allowNotification: true) is { } result)
                {
                    return (result, responseSession);
                }
            }
            throw new InvalidDataException("Microsoft Learn closed its response stream without a matching result.");
        }
        throw new InvalidDataException("Microsoft Learn returned an unsupported response content type.");
    }

    private static JsonElement? ReadRpcResult(JsonElement message, int id, bool allowNotification)
    {
        if (RequiredString(message, "jsonrpc") != "2.0")
        {
            throw new InvalidDataException("Microsoft Learn returned an invalid JSON-RPC version.");
        }
        if (!message.TryGetProperty("id", out var responseId))
        {
            if (allowNotification && message.TryGetProperty("method", out var method)
                && method.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(method.GetString())
                && !message.TryGetProperty("result", out _) && !message.TryGetProperty("error", out _))
            {
                return null;
            }
            throw new InvalidDataException("Microsoft Learn returned a response without a request ID.");
        }
        if (responseId.ValueKind != JsonValueKind.Number || !responseId.TryGetInt32(out var actualId) || actualId != id)
        {
            throw new InvalidDataException("Microsoft Learn returned a mismatched request ID.");
        }
        if (message.TryGetProperty("method", out _))
        {
            throw new InvalidDataException("Microsoft Learn returned a server request instead of a response.");
        }
        if (message.TryGetProperty("error", out var error))
        {
            if (error.ValueKind != JsonValueKind.Object || message.TryGetProperty("result", out _)
                || !error.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out _)
                || !error.TryGetProperty("message", out var errorMessage) || errorMessage.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("Microsoft Learn returned an invalid JSON-RPC error.");
            }
            throw new McpException("Microsoft Learn rejected the documentation search request.");
        }
        return RequiredObject(message, "result").Clone();
    }

    private static HttpRequestMessage CreateRequest<T>(
        string method, T parameters, JsonTypeInfo<T> typeInfo, int? id, string? sessionId)
    {
        var envelope = new LearnRpcRequest
        {
            Id = id, Method = method, Params = JsonSerializer.SerializeToElement(parameters, typeInfo)
        };
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(envelope, LearnJsonContext.Default.LearnRpcRequest))
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        AddSessionHeaders(request, sessionId);
        return request;
    }

    private static void AddSessionHeaders(HttpRequestMessage request, string? sessionId)
    {
        request.Headers.Add("MCP-Protocol-Version", ProtocolVersion);
        if (sessionId is not null)
        {
            request.Headers.Add("Mcp-Session-Id", sessionId);
        }
    }

    private async Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, Endpoint);
            AddSessionHeaders(request, sessionId);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is not HttpStatusCode.MethodNotAllowed and not HttpStatusCode.NotFound)
            {
                response.EnsureSuccessStatusCode();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            // Cleanup cannot discard valid docs or exceed the search's remaining deadline.
            _logger.LogDebug(ex, "Microsoft Learn session cleanup could not complete");
        }
    }

    private static JsonElement RequiredObject(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Microsoft Learn returned a missing or invalid {name}.");
        }
        return value;
    }

    private static LearnSearchResult[] ParseResults(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Microsoft Learn returned an unexpected documentation search response.");
        }

        var matches = new List<LearnSearchResult>();
        foreach (var result in results.EnumerateArray())
        {
            var title = RequiredString(result, "title");
            var url = RequiredString(result, "contentUrl");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !string.Equals(uri.Host, "learn.microsoft.com", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Microsoft Learn returned a documentation URL outside https://learn.microsoft.com.");
            }
            var content = "";
            if (result.TryGetProperty("content", out var excerpt) && excerpt.ValueKind != JsonValueKind.Null)
            {
                if (excerpt.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException("Microsoft Learn returned a non-text documentation excerpt.");
                }
                content = excerpt.GetString()!;
            }
            matches.Add(new LearnSearchResult(Trim(title, MaxTitleLength), uri, Trim(content, MaxExcerptLength)));
        }

        // Prefer Windows app guides without hiding relevant Win32 or other Windows docs.
        return matches.OrderByDescending(m => IsWindowsAppDoc(m.Url))
            .DistinctBy(m => m.Url.GetLeftPart(UriPartial.Path), StringComparer.OrdinalIgnoreCase)
            .Take(MaxResults)
            .ToArray();
    }

    private static string RequiredString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"Microsoft Learn returned a missing or invalid {name}.");
        }
        return value.GetString()!;
    }

    private static bool IsWindowsAppDoc(Uri url) =>
        url.AbsolutePath.Contains("/windows/apps/", StringComparison.OrdinalIgnoreCase)
        || url.AbsolutePath.Contains("/windows-app-sdk/", StringComparison.OrdinalIgnoreCase)
        || url.AbsolutePath.Contains("/winui/", StringComparison.OrdinalIgnoreCase);

    private static string Trim(string text, int maxLength)
    {
        text = ScenarioSanitizer.StripLineControlChars(text)!.Trim();
        if (text.Length <= maxLength)
        {
            return text;
        }
        var length = maxLength - 3;
        if (char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }
        return text[..length] + "...";
    }

    private sealed class BoundedResponseStream(Stream inner) : Stream
    {
        private long _bytesRead;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            CountBytes(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            CountBytes(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int CountBytes(int count)
        {
            _bytesRead += count;
            if (_bytesRead > MaxResponseBytes)
            {
                throw new InvalidDataException("Microsoft Learn returned an oversized response stream.");
            }
            return count;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(LearnInitializeParams))]
[JsonSerializable(typeof(InitializedNotificationParams))]
[JsonSerializable(typeof(ListToolsRequestParams))]
[JsonSerializable(typeof(CallToolRequestParams))]
[JsonSerializable(typeof(LearnRpcRequest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class LearnJsonContext : JsonSerializerContext;

internal sealed class LearnRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";
    public int? Id { get; init; }
    public required string Method { get; init; }
    public required JsonElement Params { get; init; }
}

internal sealed class LearnInitializeParams
{
    public required string ProtocolVersion { get; init; }
    public required JsonObject Capabilities { get; init; }
    public required Implementation ClientInfo { get; init; }
}
