// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using WinApp.Cli.Services.Learn;

namespace WinApp.Cli.Tests;

[TestClass]
public class LearnSearchClientTests
{
    private static readonly string[] ExpectedMethods = ["initialize", "notifications/initialized", "tools/list", "tools/call", "DELETE"];
    private const string SearchResults = """
        {"results":[
          {"title":"Win32", "contentUrl":"https://learn.microsoft.com/en-us/windows/win32/example", "content":"Win32 excerpt"},
          {"title":"Jump lists", "contentUrl":"https://learn.microsoft.com/windows/apps/develop/jump-list", "content":"Guide excerpt"},
          {"title":"Jump lists section", "contentUrl":"https://learn.microsoft.com/windows/apps/develop/jump-list#tasks", "content":"Duplicate excerpt"},
          {"title":"App SDK", "contentUrl":"https://learn.microsoft.com/en-us/windows-app-sdk/api/example", "content":null},
          {"title":"WinUI", "contentUrl":"https://learn.microsoft.com/en-us/windows/winui/example", "content":"WinUI excerpt"}
        ]}
        """;

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SearchAsync_DiscoversToolAndReturnsBoundedWindowsDocs(bool sse)
    {
        using var handler = new LearnHandler { Sse = sse, Payload = SearchResults };
        using var http = new HttpClient(handler);
        var results = await new LearnSearchClient(http).SearchAsync("how do I add a jump list?");

        Assert.AreEqual(3, results.Count);
        Assert.AreEqual("Jump lists", results[0].Title);
        Assert.AreEqual("Guide excerpt", results[0].Excerpt);
        Assert.AreEqual("App SDK", results[1].Title);
        Assert.AreEqual("", results[1].Excerpt);
        Assert.AreEqual("WinUI", results[2].Title);
        Assert.AreEqual("how do I add a jump list?", handler.Query);
        CollectionAssert.AreEqual(ExpectedMethods, handler.Methods);
        Assert.AreEqual("test-session", handler.CallSession);
        Assert.AreEqual("2025-03-26", handler.CallProtocol);
        Assert.IsTrue(handler.AcceptsJsonAndSse);
        Assert.IsFalse(handler.HasAuthorization);

        handler.Methods.Clear();
        await new LearnSearchClient(http).SearchAsync("second search");
        Assert.AreEqual("second search", handler.Query, "The caller's HTTP client must remain usable.");
    }

    [TestMethod]
    public async Task SearchAsync_DiscoversSearchToolOnLaterPage()
    {
        using var handler = new LearnHandler { ToolSchema = "paged" };
        using var http = new HttpClient(handler);
        var results = await new LearnSearchClient(http).SearchAsync("query");
        Assert.HasCount(3, results);
        Assert.AreEqual(2, handler.Methods.Count(m => m == "tools/list"));
        Assert.AreEqual("query", handler.Query);
    }

    [TestMethod]
    public async Task SearchAsync_ParsesStructuredContentAndTrimsText()
    {
        var payload = new JsonObject
        {
            ["results"] = new JsonArray(new JsonObject
            {
                ["title"] = new string('t', 300),
                ["contentUrl"] = "https://learn.microsoft.com/windows/apps/example",
                ["content"] = new string('x', 600)
            })
        };
        using var handler = new LearnHandler { Structured = true, Payload = payload.ToJsonString() };
        using var http = new HttpClient(handler);
        var results = await new LearnSearchClient(http).SearchAsync("query");

        Assert.HasCount(1, results);
        Assert.AreEqual(LearnSearchClient.MaxTitleLength, results[0].Title.Length);
        Assert.AreEqual(LearnSearchClient.MaxExcerptLength, results[0].Excerpt.Length);
        StringAssert.EndsWith(results[0].Excerpt, "...");
    }

    [TestMethod]
    public async Task SearchAsync_EmptyResultsAreSuccessful()
    {
        using var handler = new LearnHandler { Payload = """{"results":[]}""" };
        using var http = new HttpClient(handler);
        Assert.HasCount(0, await new LearnSearchClient(http).SearchAsync("query"));
    }

    [TestMethod]
    public async Task SearchAsync_KeepsOtherWindowsDocsWhenThereAreNoAppGuides()
    {
        using var handler = new LearnHandler
        {
            Payload = """{"results":[{"title":"Win32","contentUrl":"https://learn.microsoft.com/windows/win32/example"}]}"""
        };
        using var http = new HttpClient(handler);
        var results = await new LearnSearchClient(http).SearchAsync("query");
        Assert.HasCount(1, results);
        Assert.AreEqual("Win32", results[0].Title);
        Assert.AreEqual("", results[0].Excerpt);
    }

    [TestMethod]
    public async Task SearchAsync_TrimmingDoesNotSplitSurrogatePairs()
    {
        var payload = new JsonObject
        {
            ["results"] = new JsonArray(new JsonObject
            {
                ["title"] = "Title",
                ["contentUrl"] = "https://learn.microsoft.com/windows/apps/example",
                ["content"] = new string('x', LearnSearchClient.MaxExcerptLength - 4) + "\U0001F600" + new string('x', 100)
            })
        };
        using var handler = new LearnHandler { Payload = payload.ToJsonString() };
        using var http = new HttpClient(handler);
        var results = await new LearnSearchClient(http).SearchAsync("query");
        Assert.AreEqual(new string('x', LearnSearchClient.MaxExcerptLength - 4) + "...", results[0].Excerpt);
    }

    [TestMethod]
    public async Task SearchAsync_StripsTerminalControlsFromDocs()
    {
        using var handler = new LearnHandler
        {
            Payload = """
                {"results":[{"title":"Title\u001b","contentUrl":"https://learn.microsoft.com/windows/apps/example","content":"First\nsecond\u0007"}]}
                """
        };
        using var http = new HttpClient(handler);
        var results = await new LearnSearchClient(http).SearchAsync("query");
        Assert.AreEqual("Title", results[0].Title);
        Assert.AreEqual("First second", results[0].Excerpt);
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("""{"results":null}""")]
    [DataRow("""{"results":[{}]}""")]
    [DataRow("""{"results":[{"title":"Title","contentUrl":"https://example.com/doc"}]}""")]
    [DataRow("""{"results":[{"title":"Title","contentUrl":"http://learn.microsoft.com/doc"}]}""")]
    [DataRow("""{"results":[{"title":"Title","contentUrl":"https://learn.microsoft.com/doc","content":42}]}""")]
    public async Task SearchAsync_RejectsUnexpectedResultShape(string payload)
    {
        using var handler = new LearnHandler { Payload = payload };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LearnSearchClient(http).SearchAsync("query"));
    }

    [TestMethod]
    public async Task SearchAsync_RejectsMalformedTextJson()
    {
        using var handler = new LearnHandler { Payload = "not JSON" };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => new LearnSearchClient(http).SearchAsync("query"));
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("changed")]
    public async Task SearchAsync_RejectsMissingOrChangedSearchTool(string schema)
    {
        using var handler = new LearnHandler { ToolSchema = schema };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LearnSearchClient(http).SearchAsync("query"));
        Assert.IsFalse(handler.Methods.Contains("tools/call"));
    }

    [TestMethod]
    public async Task SearchAsync_SurfacesToolError()
    {
        using var handler = new LearnHandler { ToolError = true };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<McpException>(() => new LearnSearchClient(http).SearchAsync("query"));
    }

    [TestMethod]
    public async Task SearchAsync_SurfacesHttpFailure()
    {
        using var handler = new LearnHandler { Status = HttpStatusCode.ServiceUnavailable };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => new LearnSearchClient(http).SearchAsync("query"));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    public async Task SearchAsync_RejectsBlankQueryWithoutNetwork(string query)
    {
        using var handler = new LearnHandler();
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => new LearnSearchClient(http).SearchAsync(query));
        Assert.HasCount(0, handler.Methods);
    }

    [TestMethod]
    public async Task SearchAsync_PreCanceledCallDoesNotUseNetwork()
    {
        using var handler = new LearnHandler();
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new LearnSearchClient(http).SearchAsync("query", cancellation.Token));
        Assert.HasCount(0, handler.Methods);
    }

    [TestMethod]
    [DataRow("initialize")]
    [DataRow("tools/list")]
    [DataRow("tools/call")]
    public async Task SearchAsync_PreservesCallerCancellation(string blockedMethod)
    {
        using var handler = new LearnHandler { BlockedMethod = blockedMethod };
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var task = new LearnSearchClient(http).SearchAsync("query", cancellation.Token);
        await handler.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => task);
    }

    [TestMethod]
    [DataRow("initialize")]
    [DataRow("tools/list")]
    [DataRow("tools/call")]
    public async Task SearchAsync_TimesOutAcrossTheWholeSearch(string blockedMethod)
    {
        using var handler = new LearnHandler { BlockedMethod = blockedMethod };
        using var http = new HttpClient(handler);
        var elapsed = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => new LearnSearchClient(http).SearchAsync("query"));
        Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromSeconds(4.5), $"Search took {elapsed.Elapsed}.");
        Assert.IsTrue(elapsed.Elapsed >= TimeSpan.FromSeconds(2.5), "Search should use the 3-second budget.");
    }

    [TestMethod]
    public async Task SearchAsync_BoundsSessionCleanup()
    {
        using var handler = new LearnHandler { BlockedMethod = "DELETE" };
        using var http = new HttpClient(handler);
        var elapsed = Stopwatch.StartNew();
        var results = await new LearnSearchClient(http).SearchAsync("query");
        Assert.HasCount(3, results);
        Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromSeconds(4.5), $"Cleanup took {elapsed.Elapsed}.");
    }

    [TestMethod]
    [DataRow("""{"jsonrpc":"1.0","id":3,"result":{}}""")]
    [DataRow("""{"jsonrpc":"2.0","result":{}}""")]
    [DataRow("""{"jsonrpc":"2.0","id":null,"result":{}}""")]
    [DataRow("""{"jsonrpc":"2.0","id":"3","result":{}}""")]
    [DataRow("""{"jsonrpc":"2.0","id":4,"result":{}}""")]
    [DataRow("""{"jsonrpc":"2.0","id":3}""")]
    [DataRow("""{"jsonrpc":"2.0","id":3,"result":[]}""")]
    [DataRow("""{"jsonrpc":"2.0","id":3,"error":null}""")]
    [DataRow("""{"jsonrpc":"2.0","id":3,"error":{"code":"bad","message":"failure"}}""")]
    [DataRow("""{"jsonrpc":"2.0","id":3,"error":{"code":-1,"message":42}}""")]
    [DataRow("""{"jsonrpc":"2.0","id":3,"result":{},"error":{"code":-1,"message":"failure"}}""")]
    [DataRow("""{"jsonrpc":"2.0","id":99,"method":"ping"}""")]
    [DataRow("""{"jsonrpc":"2.0","id":3,"method":"ping","result":{}}""")]
    public async Task SearchAsync_RejectsMalformedRpcResponses(string response)
    {
        using var handler = new LearnHandler { RpcResponse = response };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LearnSearchClient(http).SearchAsync("query"));
        Assert.AreEqual("DELETE", handler.Methods[^1]);
    }

    [TestMethod]
    public async Task SearchAsync_SurfacesRpcErrorWithoutEchoingServerText()
    {
        using var handler = new LearnHandler
        {
            RpcResponse = """{"jsonrpc":"2.0","id":3,"error":{"code":-32603,"message":"untrusted server text"}}"""
        };
        using var http = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<McpException>(() => new LearnSearchClient(http).SearchAsync("query"));
        Assert.IsFalse(exception.Message.Contains("untrusted server text", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("""{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"changed","capabilities":{"tools":{}}}}""")]
    [DataRow("""{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-03-26","capabilities":{}}}""")]
    [DataRow("""{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-03-26","capabilities":{"tools":true}}}""")]
    public async Task SearchAsync_RejectsUnsupportedInitialization(string response)
    {
        using var handler = new LearnHandler { RpcResponse = response, ResponseMethod = "initialize" };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LearnSearchClient(http).SearchAsync("query"));
        Assert.IsFalse(handler.Methods.Contains("tools/list"));
        Assert.AreEqual("DELETE", handler.Methods[^1]);
    }

    [TestMethod]
    public async Task SearchAsync_RequiresInitializedAcknowledgement()
    {
        using var handler = new LearnHandler { NotificationStatus = HttpStatusCode.OK };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LearnSearchClient(http).SearchAsync("query"));
        Assert.IsFalse(handler.Methods.Contains("tools/list"));
    }

    [TestMethod]
    public async Task SearchAsync_SupportsSessionlessServerAndPreservesEscapedQuery()
    {
        const string query = "What's \"NumberBox\"?\nPath: C:\\example";
        using var handler = new LearnHandler { SessionHeaders = null };
        using var http = new HttpClient(handler);
        Assert.HasCount(3, await new LearnSearchClient(http).SearchAsync(query));
        Assert.AreEqual(query, handler.Query);
        Assert.IsNull(handler.CallSession);
        Assert.IsFalse(handler.Methods.Contains("DELETE"));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("has space")]
    [DataRow("non-ascii-\u00e9")]
    public async Task SearchAsync_RejectsInvalidSessionHeader(string session)
    {
        using var handler = new LearnHandler { SessionHeaders = [session] };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LearnSearchClient(http).SearchAsync("query"));
        Assert.IsFalse(handler.Methods.Contains("notifications/initialized"));
    }

    [TestMethod]
    public async Task SearchAsync_RejectsMultipleSessionHeaders()
    {
        using var handler = new LearnHandler { SessionHeaders = ["first", "second"] };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LearnSearchClient(http).SearchAsync("query"));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.NotFound)]
    [DataRow(HttpStatusCode.MethodNotAllowed)]
    [DataRow(HttpStatusCode.ServiceUnavailable)]
    public async Task SearchAsync_CleanupStatusCannotDiscardDocs(HttpStatusCode status)
    {
        using var handler = new LearnHandler { DeleteStatus = status };
        using var http = new HttpClient(handler);
        using var logger = new LevelLogger<LearnSearchClient>(LogLevel.Debug);
        Assert.HasCount(3, await new LearnSearchClient(http, logger).SearchAsync("query"));
        Assert.HasCount(status == HttpStatusCode.ServiceUnavailable ? 1 : 0, logger.Entries);
    }

    [TestMethod]
    public async Task SearchAsync_PreservesCancellationDuringCleanup()
    {
        using var handler = new LearnHandler { BlockedMethod = "DELETE" };
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var task = new LearnSearchClient(http).SearchAsync("query", cancellation.Token);
        await handler.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => task);
    }

    [TestMethod]
    public async Task SearchAsync_HandlesMultilineSseAndNotifications()
    {
        using var handler = new LearnHandler
        {
            Sse = true, MultilineSse = true,
            SsePrefix = ": keepalive\n\nevent: message\ndata:\n\n"
                + "event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{}}\n\n"
        };
        using var http = new HttpClient(handler);
        Assert.HasCount(3, await new LearnSearchClient(http).SearchAsync("query"));
    }

    [TestMethod]
    public async Task SearchAsync_RejectsSseWithoutResult()
    {
        using var handler = new LearnHandler { Sse = true, RpcResponse = """{"jsonrpc":"2.0","method":"notifications/progress"}""" };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LearnSearchClient(http).SearchAsync("query"));
    }

    [TestMethod]
    public async Task SearchAsync_RejectsUnsupportedContentType()
    {
        using var handler = new LearnHandler { ResponseContentType = "text/plain" };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LearnSearchClient(http).SearchAsync("query"));
    }

    [TestMethod]
    [DataRow(false, "Application/Json")]
    [DataRow(true, "Text/Event-Stream")]
    public async Task SearchAsync_AcceptsCaseInsensitiveContentType(bool sse, string contentType)
    {
        using var handler = new LearnHandler { Sse = sse, ResponseContentType = contentType };
        using var http = new HttpClient(handler);
        Assert.HasCount(3, await new LearnSearchClient(http).SearchAsync("query"));
    }

    [TestMethod]
    public async Task SearchAsync_RejectsRepeatedToolCursor()
    {
        using var handler = new LearnHandler { ToolSchema = "repeated" };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LearnSearchClient(http).SearchAsync("query"));
        Assert.AreEqual(2, handler.Methods.Count(m => m == "tools/list"));
        Assert.IsFalse(handler.Methods.Contains("tools/call"));
    }

    [TestMethod]
    public async Task SearchAsync_ReturnsSseResultWithoutWaitingForStreamToClose()
    {
        using var handler = new LearnHandler { Sse = true, OpenBody = true };
        using var http = new HttpClient(handler);
        Assert.HasCount(3, await new LearnSearchClient(http).SearchAsync("query"));
        Assert.IsFalse(handler.Blocked.Task.IsCompleted, "A complete SSE result must not wait for another event or EOF.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SearchAsync_CancelsWhileReadingResponseBody(bool sse)
    {
        using var handler = new LearnHandler { Sse = sse, OpenBody = true, RpcResponse = "" };
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var task = new LearnSearchClient(http).SearchAsync("query", cancellation.Token);
        await handler.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => task);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SearchAsync_DeadlineIncludesReadingResponseBody(bool sse)
    {
        using var handler = new LearnHandler { Sse = sse, OpenBody = true, RpcResponse = "" };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<TimeoutException>(() => new LearnSearchClient(http).SearchAsync("query"));
        Assert.IsTrue(handler.Blocked.Task.IsCompleted);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SearchAsync_BoundsResponseBody(bool sse)
    {
        using var handler = new LearnHandler { Sse = sse, Payload = new string('x', 1024 * 1024) };
        using var http = new HttpClient(handler);
        if (sse)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => new LearnSearchClient(http).SearchAsync("query"));
        }
        else
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => new LearnSearchClient(http).SearchAsync("query"));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SearchAsync_AllowsResponseExactlyAtByteLimit(bool sse)
    {
        const string response = """{"jsonrpc":"2.0","id":3,"result":{"structuredContent":{"results":[]}}}""";
        var framingBytes = sse ? "event: message\ndata: \n\n".Length : 0;
        using var handler = new LearnHandler { Sse = sse, RpcResponse = response.PadRight(1024 * 1024 - framingBytes) };
        using var http = new HttpClient(handler);
        Assert.HasCount(0, await new LearnSearchClient(http).SearchAsync("query"));
    }

    [TestMethod]
    public async Task SearchAsync_BoundsSseCommentsBeforeResult()
    {
        using var handler = new LearnHandler { Sse = true, SsePrefix = ": " + new string('x', 1024 * 1024) + "\n\n" };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LearnSearchClient(http).SearchAsync("query"));
    }

    private sealed class OpenResponseStream(byte[] bytes, TaskCompletionSource blocked) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await base.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                blocked.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return count;
        }

        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            var buffer = new byte[bufferSize];
            int count;
            while ((count = await ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
        }
    }

    private sealed class LearnHandler : HttpMessageHandler
    {
        public string Payload { get; init; } = SearchResults;
        public bool Sse { get; init; }
        public bool Structured { get; init; }
        public bool ToolError { get; init; }
        public string ToolSchema { get; init; } = "valid";
        public string? BlockedMethod { get; init; }
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public List<string> Methods { get; } = [];
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Query { get; private set; }
        public string? CallSession { get; private set; }
        public string? CallProtocol { get; private set; }
        public bool AcceptsJsonAndSse { get; private set; }
        public bool HasAuthorization { get; private set; }
        public string? RpcResponse { get; init; }
        public string ResponseMethod { get; init; } = "tools/call";
        public string? ResponseContentType { get; init; }
        public string[]? SessionHeaders { get; init; } = ["test-session"];
        public HttpStatusCode NotificationStatus { get; init; } = HttpStatusCode.Accepted;
        public HttpStatusCode DeleteStatus { get; init; } = HttpStatusCode.Accepted;
        public string SsePrefix { get; init; } = "";
        public bool MultilineSse { get; init; }
        public bool OpenBody { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual("https://learn.microsoft.com/api/mcp", request.RequestUri!.AbsoluteUri);
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            var method = request.Method == HttpMethod.Delete ? "DELETE" : body!["method"]!.GetValue<string>();
            Methods.Add(method);
            if (method == BlockedMethod)
            {
                Blocked.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            if (Status != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(Status);
            }
            if (method == "DELETE")
            {
                return new HttpResponseMessage(DeleteStatus);
            }
            Assert.AreEqual("2.0", body!["jsonrpc"]!.GetValue<string>());
            Assert.AreEqual(HttpMethod.Post, request.Method);
            if (method == "notifications/initialized")
            {
                Assert.IsNull(body["id"]);
                return new HttpResponseMessage(NotificationStatus);
            }
            JsonNode result;
            if (method == "initialize")
            {
                Assert.AreEqual("2025-03-26", body["params"]!["protocolVersion"]!.GetValue<string>());
                Assert.AreEqual(0, body["params"]!["capabilities"]!.AsObject().Count);
                Assert.AreEqual("winapp", body["params"]!["clientInfo"]!["name"]!.GetValue<string>());
                result = JsonNode.Parse("""
                    {"protocolVersion":"2025-03-26","capabilities":{"tools":{}},"serverInfo":{"name":"Learn","version":"1.0"}}
                    """)!;
            }
            else if (method == "tools/list")
            {
                result = JsonNode.Parse(ToolSchema == "missing" ? """{"tools":[]}""" : """
                    {"tools":[{"name":"microsoft_docs_search","inputSchema":{"type":"object","properties":{"query":{"type":"string"}}}}]}
                    """)!;
                if (ToolSchema == "changed")
                {
                    result["tools"]![0]!["inputSchema"]!["properties"]!["query"]!["type"] = "number";
                }
                else if (ToolSchema == "repeated" || (ToolSchema == "paged" && body["params"]?["cursor"] is null))
                {
                    result = JsonNode.Parse("""{"tools":[],"nextCursor":"page2"}""")!;
                }
            }
            else
            {
                Assert.AreEqual("tools/call", method);
                Assert.AreEqual("microsoft_docs_search", body!["params"]!["name"]!.GetValue<string>());
                Query = body["params"]!["arguments"]!["query"]!.GetValue<string>();
                CallSession = request.Headers.TryGetValues("Mcp-Session-Id", out var sessions) ? sessions.Single() : null;
                CallProtocol = request.Headers.GetValues("MCP-Protocol-Version").Single();
                AcceptsJsonAndSse = request.Headers.Accept.Any(a => a.MediaType == "application/json")
                    && request.Headers.Accept.Any(a => a.MediaType == "text/event-stream");
                HasAuthorization = request.Headers.Authorization is not null;
                result = new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = Payload }),
                    ["isError"] = ToolError
                };
                if (Structured)
                {
                    result["structuredContent"] = JsonNode.Parse(Payload);
                }
            }
            var rpc = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = body!["id"]!.DeepClone(), ["result"] = result };
            var json = method == ResponseMethod && RpcResponse is not null ? RpcResponse
                : rpc.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = MultilineSse });
            var wireBody = Sse ? $"{SsePrefix}event: message\ndata: {json.Replace("\n", "\ndata: ", StringComparison.Ordinal)}\n\n" : json;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = OpenBody && method == ResponseMethod
                    ? new StreamContent(new OpenResponseStream(Encoding.UTF8.GetBytes(wireBody), Blocked))
                    : new StringContent(wireBody, Encoding.UTF8)
            };
            response.Content.Headers.ContentType = new(ResponseContentType ?? (Sse ? "text/event-stream" : "application/json"));
            if (method == "initialize" && SessionHeaders is not null)
            {
                response.Headers.TryAddWithoutValidation("Mcp-Session-Id", SessionHeaders);
            }
            return response;
        }
    }
}
