// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public class DevToolsPipeClientTests
{
    [TestMethod]
    public void Request_CarriesSelectedWindowOnEveryRoundTrip()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("echo", "{}");
        var target = DevToolsTarget.Ready(agent.Pid, null, false, 657922, "9001");
        Assert.IsTrue(target.Tap!.Request("echo").Ok);
        Assert.IsTrue(target.Tap.Request("echo", writer => writer.WriteString("handle", "123")).Ok);
        foreach (var request in agent.ReceivedRequests)
        {
            using var doc = JsonDocument.Parse(request);
            Assert.AreEqual("657922", doc.RootElement.GetProperty("params").GetProperty("window").GetString());
            Assert.AreEqual("9001", doc.RootElement.GetProperty("params").GetProperty("root").GetString());
        }
    }

    [TestMethod]
    public void Request_AuthenticatesAndPreservesResult()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("echo", """{"value":42}""");
        var response = new VisualTreeTap((uint)agent.Pid).Request("echo");
        Assert.IsTrue(response.Ok, response.Error?.Message);
        Assert.AreEqual("""{"value":42}""", response.ResultJson);
        CollectionAssert.AreEqual(new List<string> { "echo" }, agent.Received);
    }

    [TestMethod]
    public void Request_RejectsWrongServerPidBeforeSending()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("echo", "null");
        var response = new VisualTreeTap((uint)agent.Pid, uint.MaxValue).Request("echo");
        Assert.IsFalse(response.Ok);
        Assert.AreEqual("unauthorized", response.Error!.Token);
        Assert.AreEqual(0, agent.Received.Count);
    }

    [TestMethod]
    public void Request_PreservesRemoteError()
    {
        using var agent = new FakeDevToolsProtocolAgent().Error("echo", -32011, "no-tree", "No visual tree");
        var response = new VisualTreeTap((uint)agent.Pid).Request("echo");
        Assert.AreEqual(new DevToolsProtocolError(-32011, "no-tree", "No visual tree"), response.Error);
        Assert.ThrowsExactly<DevToolsProtocolException>(() => response.RequireResult());
    }

    [TestMethod]
    [DataRow("""{"jsonrpc":"2.0","id":2,"result":null}""")]
    [DataRow("""{"jsonrpc":"2.0","id":"1","result":null}""")]
    [DataRow("""{"jsonrpc":"1.0","id":1,"result":null}""")]
    [DataRow("""{"jsonrpc":"2.0","id":1,"result":null,"error":{}}""")]
    [DataRow("""{"jsonrpc":"2.0","id":1,"error":{"code":"wrong","message":"bad"}}""")]
    [DataRow("""{"jsonrpc":"2.0","result":null}""")]
    [DataRow("OK")]
    [DataRow("ERR old-wire")]
    public void Request_RejectsMalformedOrMismatchedReply(string reply)
    {
        using var agent = new FakeDevToolsProtocolAgent { RawReply = Encoding.UTF8.GetBytes(reply + "\n") };
        var response = new VisualTreeTap((uint)agent.Pid).Request("echo");
        Assert.IsFalse(response.Ok);
        Assert.IsTrue(response.Error!.Token is "internal" or "parse-error", response.Error.Message);
        Assert.AreEqual(1, agent.Received.Count, "The malformed control must reach framing, not fail peer authentication.");
    }

    [TestMethod]
    public void Request_DoesNotLoseResponseAfterCoalescedNotification()
    {
        using var agent = new FakeDevToolsProtocolAgent
        {
            RawReply = Encoding.UTF8.GetBytes(
                "{\"jsonrpc\":\"2.0\",\"method\":\"Selection.changed\",\"params\":{}}\n" +
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"value\":7}}\n"),
        };
        var response = new VisualTreeTap((uint)agent.Pid).Request("echo");
        Assert.AreEqual("""{"value":7}""", response.RequireResult());
    }

    [TestMethod]
    public void Request_EnforcesExactRequestLimitBeforeTransport()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("echo", "null");
        const string empty = """{"jsonrpc":"2.0","id":1,"method":"echo","params":{"text":""}}""";
        var payload = new string('x', VisualTreeTap.MaxRequestBytes - Encoding.UTF8.GetByteCount(empty));
        var tap = new VisualTreeTap((uint)agent.Pid);
        Assert.IsTrue(tap.Request("echo", w => w.WriteString("text", payload)).Ok);
        Assert.AreEqual(VisualTreeTap.MaxRequestBytes, Encoding.UTF8.GetByteCount(agent.ReceivedRequests.Single()));
        var tooLarge = tap.Request("echo", w => w.WriteString("text", payload + "x"));
        Assert.AreEqual("bad-args", tooLarge.Error!.Token);
        Assert.AreEqual(1, agent.Received.Count);
    }

    [TestMethod]
    public void Request_RejectsOversizedResponseAndInvalidUtf8()
    {
        using (var agent = new FakeDevToolsProtocolAgent { RawReply = [0xff, (byte)'\n'] })
        {
            Assert.AreEqual("parse-error", new VisualTreeTap((uint)agent.Pid).Request("echo").Error!.Token);
        }
        using (var agent = new FakeDevToolsProtocolAgent { RawReply = new byte[VisualTreeTap.MaxResponseBytes + 1] })
        {
            var response = new VisualTreeTap((uint)agent.Pid).Request("echo");
            Assert.AreEqual("internal", response.Error!.Token);
            StringAssert.Contains(response.Error.Message, "64 MiB");
        }
    }

    [TestMethod]
    public void Request_DeadlineCoversBlockedWrite()
    {
        using var agent = new FakeDevToolsProtocolAgent(readRequests: false);
        var timer = Stopwatch.StartNew();
        var response = new VisualTreeTap((uint)agent.Pid).Request("echo",
            w => w.WriteString("text", new string('x', 63000)), timeoutMs: 200);
        Assert.AreEqual("no-response", response.Error!.Token);
        Assert.IsLessThan(TimeSpan.FromSeconds(5), timer.Elapsed);
        Assert.AreEqual(0, agent.Received.Count);
    }

    [TestMethod]
    public void Request_ExternalCancellationIsNotSuccessOrTimeout()
    {
        using var agent = new FakeDevToolsProtocolAgent(readRequests: false);
        using var cancellation = new CancellationTokenSource(200);
        Assert.Throws<OperationCanceledException>(() =>
            new VisualTreeTap((uint)agent.Pid).Request("echo", w => w.WriteString("text", new string('x', 63000)),
                cancellationToken: cancellation.Token));
    }

    [TestMethod]
    public void Connect_BusyInstance_ObservesCancellation()
    {
        using var agent = new FakeDevToolsProtocolAgent(readRequests: false);
        using var occupied = DevToolsPipeClient.Connect((uint)agent.Pid, (uint)agent.Pid, 2000, true);
        using var cancellation = new CancellationTokenSource(200);
        var timer = Stopwatch.StartNew();
        Assert.Throws<OperationCanceledException>(() =>
            DevToolsPipeClient.Connect((uint)agent.Pid, (uint)agent.Pid, 12000, true, cancellation.Token));
        Assert.IsLessThan(TimeSpan.FromSeconds(2), timer.Elapsed);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Readiness_PreservesItsBudgetAndExternalCancellation(bool negotiate)
    {
        using var agent = new FakeDevToolsProtocolAgent(readRequests: false);
        var tap = new VisualTreeTap((uint)agent.Pid);
        var timer = Stopwatch.StartNew();
        if (negotiate)
        {
            Assert.IsNull(tap.WaitHello(200, CancellationToken.None));
        }
        else
        {
            Assert.IsFalse(tap.WaitReady(200, CancellationToken.None));
        }
        Assert.IsLessThan(TimeSpan.FromSeconds(2), timer.Elapsed);
        using var cancellation = new CancellationTokenSource(200);
        Assert.Throws<OperationCanceledException>(() =>
        {
            if (negotiate)
            {
                tap.WaitHello(12000, cancellation.Token);
            }
            else
            {
                tap.WaitReady(12000, cancellation.Token);
            }
        });
    }

    [TestMethod]
    [DataRow("""{"methods":{}}""")]
    [DataRow("""{"methods":["Property.get",42]}""")]
    public void Negotiation_MalformedMethodList_IsNotPartialSuccess(string reply)
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("DevTools.negotiate", reply);
        var tap = new VisualTreeTap((uint)agent.Pid);
        Assert.IsNull(tap.GetPublicMethods());
        Assert.AreEqual("internal", tap.LastError!.Token);
        Assert.AreEqual(1, agent.Received.Count);
    }

    [TestMethod]
    public void Json_MalformedResult_IsNotConvertedIntoSuccessfulString()
    {
        Assert.Throws<JsonException>(() => DevToolsJson.Result(123, "{broken"));
    }

    [TestMethod]
    public void Request_DeepAuthoredTree_SurvivesModelAndJsonOutput()
    {
        var nodes = """[{"handle":"101","type":"Button","name":"Leaf","children":[]}]""";
        for (var i = 100; i > 0; i--)
        {
            nodes = $"[{{\"handle\":\"{i}\",\"type\":\"Grid\",\"children\":{nodes}}}]";
        }
        using var agent = new FakeDevToolsProtocolAgent();
        agent.Answer("VisualTree.enumerate",
            $"{{\"nodes\":{nodes},\"sourceInstrumented\":true,\"classificationTruncated\":false,\"authoredNodes\":101,\"censusNodes\":101,\"classifiedNodes\":101}}");
        var reply = new VisualTreeTap((uint)agent.Pid).RequestEnumerate(null, null, authored: true);
        var forest = AuthoredForest.Parse(reply.RequireResult());
        Assert.IsNotNull(forest);
        Assert.AreEqual(101, forest.AuthoredNodes);
        var current = forest.Roots.Single();
        for (var i = 0; i < 100; i++) { current = current.Children.Single(); }
        Assert.AreEqual("Leaf", current.Name);
        using var json = JsonDocument.Parse(DevToolsJson.Result(agent.Pid, reply.ResultJson), TapWireJson.DocumentOptions);
        Assert.IsTrue(json.RootElement.GetProperty("ok").GetBoolean());
    }
}

// Every server belongs to this test process; PID authentication is real, not bypassed.
internal sealed class FakeDevToolsProtocolAgent : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = TapWireJson.MaxDepth };
    private readonly CancellationTokenSource _stop = new();
    private readonly NamedPipeServerStream _server;
    private readonly Task _loop;
    private readonly object _gate = new();
    private readonly List<(string Method, string Reply, Func<string, bool>? Predicate)> _answers = [];
    private readonly List<string> _received = [];
    private readonly List<string> _requests = [];

    public int Pid { get; } = Environment.ProcessId;
    public byte[]? RawReply { get; init; }
    public List<string> Received { get { lock (_gate) { return [.. _received]; } } }
    public List<string> ReceivedRequests { get { lock (_gate) { return [.. _requests]; } } }

    public FakeDevToolsProtocolAgent(bool readRequests = true)
    {
        _server = new NamedPipeServerStream(DevToolsPipeDiscovery.PipeNameFor(Pid), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 128, 128);
        _loop = RunAsync(readRequests);
    }

    public FakeDevToolsProtocolAgent Answer(string method, string result, Func<string, bool>? predicate = null)
    {
        using var doc = JsonDocument.Parse(result, TapWireJson.DocumentOptions);
        var compact = JsonSerializer.Serialize(doc.RootElement, JsonOptions);
        lock (_gate)
        {
            _answers.Add((method, $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{compact}}}\n", predicate));
        }
        return this;
    }

    public FakeDevToolsProtocolAgent Error(string method, int code, string token, string message)
    {
        lock (_gate)
        {
            _answers.Add((method,
                $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{{\"code\":{code},\"message\":{JsonSerializer.Serialize(message)}," +
                $"\"data\":{{\"token\":{JsonSerializer.Serialize(token)}}}}}}}\n", null));
        }
        return this;
    }

    public ResolverForProcess Resolver() => new(Pid);

    private async Task RunAsync(bool readRequests)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _server.WaitForConnectionAsync(_stop.Token);
                try
                {
                    if (!readRequests)
                    {
                        await Task.Delay(Timeout.Infinite, _stop.Token);
                    }
                    using var reader = new StreamReader(_server, Encoding.UTF8, false, 1024, leaveOpen: true);
                    var request = await reader.ReadLineAsync(_stop.Token);
                    if (request is not null)
                    {
                        using var doc = JsonDocument.Parse(request, TapWireJson.DocumentOptions);
                        var method = doc.RootElement.GetProperty("method").GetString()!;
                        string response;
                        lock (_gate)
                        {
                            _received.Add(method);
                            _requests.Add(request);
                            response = _answers.FirstOrDefault(a => a.Method == method && (a.Predicate?.Invoke(request) ?? true)).Reply ??
                                (method == "DevTools.negotiate"
                                    ? "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":\"0\",\"posture\":\"mutation\",\"mutation\":true,\"methods\":" +
                                      JsonSerializer.Serialize(_answers.Select(a => a.Method).Distinct().ToArray()) + "}}\n"
                                    : "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-32601,\"message\":\"No fake response configured\",\"data\":{\"token\":\"unknown-method\"}}}\n");
                        }
                        await _server.WriteAsync(RawReply ?? Encoding.UTF8.GetBytes(response), _stop.Token);
                        // Disconnect discards unread pipe bytes; let the client drain and close first.
                        if (await _server.ReadAsync(new byte[1], _stop.Token) != 0)
                        {
                            throw new InvalidDataException("The single-request test client sent trailing data.");
                        }
                    }
                }
                catch (IOException)
                {
                    // The framing/auth/deadline controls deliberately close the client early.
                }
                finally
                {
                    _server.Disconnect();
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _loop.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        finally
        {
            _server.Dispose();
            _stop.Dispose();
        }
    }

    internal sealed class ResolverForProcess(int pid) : IDevToolsTargetResolver
    {
        public bool? LastAttachIfNeeded { get; private set; }

        public Task<DevToolsTarget> ResolveAsync(string? app, long? window, CancellationToken cancellationToken, bool attachIfNeeded = true)
        {
            LastAttachIfNeeded = attachIfNeeded;
            return Task.FromResult(DevToolsTarget.Ready(pid, "Owned fake agent", false));
        }
    }
}
