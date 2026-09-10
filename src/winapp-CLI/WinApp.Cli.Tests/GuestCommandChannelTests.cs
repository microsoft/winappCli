// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// Contract tests for <see cref="GuestCommandChannel"/> against a fake transport.
/// </summary>
/// <remarks>
/// These cover acceptance criterion 13: execution, streaming, cancellation, and error handling must
/// work with no Windows Sandbox involved. Because the channel depends only on
/// <see cref="IGuestTransport"/>, passing here is evidence that orchestration carries no dependency
/// on <c>wsb</c> commands, Sandbox paths, or Sandbox window titles.
/// </remarks>
[TestClass]
public class GuestCommandChannelTests : IDisposable
{
    private const string Epoch = "instance-1:nonce-1";

    // CA1861: hoisted so repeated assertions do not allocate a fresh array each call.
    private static readonly string[] ExpectedStdout = ["first", "second"];
    private static readonly string[] ExpectedStderr = ["warning"];
    private static readonly string[] ExpectedFirstStream = ["a"];
    private static readonly string[] ExpectedSecondStream = ["b"];

    private FakeGuestTransport _transport = null!;
    private GuestCommandChannel _channel = null!;

    [TestInitialize]
    public void Setup()
    {
        _transport = new FakeGuestTransport();
        _channel = new GuestCommandChannel(_transport, new ExecutionTargetEpoch(Epoch));
        _channel.Start();
    }

    [TestCleanup]
    public void ResetTransport() => _transport.Break();

    /// <summary>Reads the next control message the channel sent to the guest.</summary>
    private async Task<GuestMessage> NextRequestAsync()
    {
        var frame = await _transport.PeerInbox.ReadAsync(TestContext.CancellationTokenSource.Token);
        var message = GuestPayloadCodec.TryDecodeJson(frame.Span);
        Assert.IsNotNull(message, "Expected a control message.");
        return message;
    }

    private void PeerReply(GuestMessage message) => _transport.PeerSend(GuestPayloadCodec.EncodeJson(message));

    private static GuestExecRequest SampleRequest => new()
    {
        Executable = "winapp.exe",
        Arguments = ["ui", "inspect", "--app", "MyApp"],
    };

    [TestMethod]
    public async Task GetCapabilities_ReturnsWhatTheGuestReports()
    {
        var pending = _channel.GetCapabilitiesAsync(TestContext.CancellationTokenSource.Token);

        var request = await NextRequestAsync();
        Assert.AreEqual(GuestMessageTypes.CapabilitiesRequest, request.Type);
        Assert.AreEqual(Epoch, request.TargetEpoch, "Every request must carry the epoch it assumes.");

        PeerReply(new GuestMessage
        {
            Type = GuestMessageTypes.CapabilitiesResponse,
            OperationId = request.OperationId,
            Capabilities = new ExecutionTargetCapabilities
            {
                Architecture = "arm64",
                SupportsInteractiveDesktop = true,
                SupportsRealInput = true,
                SupportsScreenCapture = true,
                CooperativeUiTurnsVersion = 1,
                SupportsInternalSystemSetup = true,
                PersistentStorage = false,
            },
        });

        var capabilities = await pending;

        Assert.AreEqual("arm64", capabilities.Architecture);
        Assert.IsFalse(capabilities.PersistentStorage, "Sandbox state does not survive teardown.");
    }

    [TestMethod]
    public async Task Execute_PreservesArgumentBoundariesIncludingUnicodeAndSpaces()
    {
        var request = new GuestExecRequest
        {
            Executable = "winapp.exe",
            Arguments = ["ui", "send-keys", "--text", "hello world \u2014 \U0001F600", "--app", "My App"],
            WorkingDirectory = @"C:\Work",
            Environment = new Dictionary<string, string> { ["WINAPP_UI_WORKFLOW_ID"] = "token" },
        };

        var pending = _channel.ExecuteAsync(request, callbacks: null, TestContext.CancellationTokenSource.Token);

        var sent = await NextRequestAsync();
        Assert.AreEqual(GuestMessageTypes.ExecRequest, sent.Type);

        // Arguments must survive as separate values; joining them into one string is what enables
        // both quoting bugs and injection.
        CollectionAssert.AreEqual(request.Arguments, sent.Exec!.Arguments);
        Assert.AreEqual(@"C:\Work", sent.Exec.WorkingDirectory);
        Assert.AreEqual("token", sent.Exec.Environment!["WINAPP_UI_WORKFLOW_ID"]);

        PeerReply(new GuestMessage { Type = GuestMessageTypes.ExecCompleted, OperationId = sent.OperationId, ExitCode = 0 });
        await pending;
    }

    [TestMethod]
    public async Task Execute_ReportsStartedProcessIdThenExitCode()
    {
        var startedPid = 0;
        var startedTicks = 0L;
        var callbacks = new GuestExecCallbacks(OnStarted: process =>
        {
            startedPid = process.ProcessId;
            startedTicks = process.StartTicksUtc;
        });

        var pending = _channel.ExecuteAsync(SampleRequest, callbacks, TestContext.CancellationTokenSource.Token);
        var sent = await NextRequestAsync();

        PeerReply(new GuestMessage { Type = GuestMessageTypes.ExecStarted, OperationId = sent.OperationId, ProcessId = 4212, ProcessStartTicksUtc = 99 });
        PeerReply(new GuestMessage { Type = GuestMessageTypes.ExecCompleted, OperationId = sent.OperationId, ExitCode = 3 });

        var result = await pending;

        Assert.AreEqual(4212, startedPid);
        Assert.AreEqual(4212, result.ProcessId);

        // Process IDs are reused, so the start time travels with the ID or a later command cannot
        // tell this process from an unrelated one that inherited its number.
        Assert.AreEqual(99, startedTicks);

        // The guest application's exit code must survive intact and stay distinguishable from the
        // infrastructure failures reported as ExecutionTargetException.
        Assert.AreEqual(3, result.ExitCode);
    }

    [TestMethod]
    public async Task Execute_StreamsStdoutAndStderrSeparatelyAndInOrder()
    {
        var stdout = new List<string>();
        var stderr = new List<string>();
        var callbacks = new GuestExecCallbacks(
            OnStandardOutput: chunk => stdout.Add(Encoding.UTF8.GetString(chunk.Span)),
            OnStandardError: chunk => stderr.Add(Encoding.UTF8.GetString(chunk.Span)));

        var pending = _channel.ExecuteAsync(SampleRequest, callbacks, TestContext.CancellationTokenSource.Token);
        var sent = await NextRequestAsync();
        var operationId = Guid.Parse(sent.OperationId!);

        _transport.PeerSend(GuestPayloadCodec.EncodeStream(operationId, GuestStreamId.StandardOutput, "first"u8));
        _transport.PeerSend(GuestPayloadCodec.EncodeStream(operationId, GuestStreamId.StandardError, "warning"u8));
        _transport.PeerSend(GuestPayloadCodec.EncodeStream(operationId, GuestStreamId.StandardOutput, "second"u8));
        PeerReply(new GuestMessage { Type = GuestMessageTypes.ExecCompleted, OperationId = sent.OperationId, ExitCode = 0 });

        await pending;

        CollectionAssert.AreEqual(ExpectedStdout, stdout);
        CollectionAssert.AreEqual(ExpectedStderr, stderr);
    }

    [TestMethod]
    public async Task Execute_LargeOutput_IsDeliveredIntact()
    {
        var received = new List<byte>();
        var callbacks = new GuestExecCallbacks(OnStandardOutput: chunk => received.AddRange(chunk.ToArray()));

        var pending = _channel.ExecuteAsync(SampleRequest, callbacks, TestContext.CancellationTokenSource.Token);
        var sent = await NextRequestAsync();
        var operationId = Guid.Parse(sent.OperationId!);

        var payload = new byte[200_000];
        Random.Shared.NextBytes(payload);

        for (var offset = 0; offset < payload.Length; offset += 60_000)
        {
            var take = Math.Min(60_000, payload.Length - offset);
            _transport.PeerSend(GuestPayloadCodec.EncodeStream(
                operationId, GuestStreamId.StandardOutput, payload.AsSpan(offset, take)));
        }

        PeerReply(new GuestMessage { Type = GuestMessageTypes.ExecCompleted, OperationId = sent.OperationId, ExitCode = 0 });
        await pending;

        CollectionAssert.AreEqual(payload, received.ToArray());
    }

    [TestMethod]
    public async Task SendStandardInput_ChunksOversizedWritesBelowTheFrameLimit()
    {
        var pending = _channel.ExecuteAsync(SampleRequest, callbacks: null, TestContext.CancellationTokenSource.Token);
        var sent = await NextRequestAsync();
        var operationId = Guid.Parse(sent.OperationId!);

        var input = new byte[GuestPayloadCodec.MaxStreamChunkSize + 1_000];
        await _channel.SendStandardInputAsync(operationId, input, TestContext.CancellationTokenSource.Token);

        // A single oversized write must be split, or it could not fit in a frame and would stall.
        var firstChunk = await _transport.PeerInbox.ReadAsync(TestContext.CancellationTokenSource.Token);
        var secondChunk = await _transport.PeerInbox.ReadAsync(TestContext.CancellationTokenSource.Token);

        Assert.IsTrue(GuestPayloadCodec.TryDecodeStream(firstChunk, out _, out var stream, out var firstData));
        Assert.AreEqual(GuestStreamId.StandardInput, stream);
        Assert.AreEqual(GuestPayloadCodec.MaxStreamChunkSize, firstData.Length);

        Assert.IsTrue(GuestPayloadCodec.TryDecodeStream(secondChunk, out _, out _, out var secondData));
        Assert.AreEqual(1_000, secondData.Length);

        PeerReply(new GuestMessage { Type = GuestMessageTypes.ExecCompleted, OperationId = sent.OperationId, ExitCode = 0 });
        await pending;
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Execute_Cancellation_AsksTheGuestToCancelGracefully(bool returnResult)
    {
        using var cancellation = new CancellationTokenSource();
        var output = new StringBuilder();
        var pending = _channel.ExecuteAsync(SampleRequest, new GuestExecCallbacks(
            OnStandardOutput: bytes => output.Append(Encoding.UTF8.GetString(bytes.Span)),
            ReturnResultOnCancellation: returnResult), cancellation.Token);
        var sent = await NextRequestAsync();

        await cancellation.CancelAsync();

        var cancelRequest = await NextRequestAsync();

        // Graceful termination is requested first; the guest enforces its own timeout before killing
        // the process tree, so a well-behaved child can still flush and exit cleanly.
        Assert.AreEqual(GuestMessageTypes.CancelRequest, cancelRequest.Type);
        Assert.AreEqual(sent.OperationId, cancelRequest.OperationId);

        Assert.IsFalse(pending.IsCompleted, "Cancellation must wait until the guest finishes stopping.");
        _transport.PeerSend(GuestPayloadCodec.EncodeStream(
            Guid.Parse(sent.OperationId!), GuestStreamId.StandardOutput, "finalized"u8));
        PeerReply(new GuestMessage
        {
            Type = GuestMessageTypes.ExecCompleted,
            OperationId = sent.OperationId,
            ExitCode = 0,
        });
        if (returnResult)
        {
            Assert.AreEqual(0, (await pending).ExitCode);
        }
        else
        {
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await pending);
        }
        Assert.AreEqual("finalized", output.ToString());

        var next = _channel.ListFilesAsync(
            new GuestPathScope(GuestRootNames.Artifacts, "capture"), TestContext.CancellationTokenSource.Token);
        var nextRequest = await NextRequestAsync();
        PeerReply(new GuestMessage
        {
            Type = GuestMessageTypes.ListFilesCompleted,
            OperationId = nextRequest.OperationId,
        });
        await next.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Execute_CancellationWithoutAcknowledgement_FailsWithinTheBound(bool returnResult)
    {
        await using var transport = new FakeGuestTransport();
        await using var channel = new GuestCommandChannel(transport, new ExecutionTargetEpoch(Epoch))
        {
            CancellationAcknowledgementTimeout = TimeSpan.FromMilliseconds(100),
        };
        channel.Start();
        using var cancellation = new CancellationTokenSource();
        var pending = channel.ExecuteAsync(SampleRequest,
            new GuestExecCallbacks(ReturnResultOnCancellation: returnResult), cancellation.Token);
        await transport.PeerInbox.ReadAsync(TestContext.CancellationTokenSource.Token);
        await cancellation.CancelAsync();
        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(5)));

        StringAssert.Contains(failure.Error.Message, "did not acknowledge");
        Assert.IsFalse(transport.IsConnected);
        await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => channel.GetCapabilitiesAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task GetFile_ThrowingDestination_FailsAllOperationsAndClosesTheChannel()
    {
        using var destination = new ThrowingOutputStream();
        var download = _channel.GetFileAsync(
            new GuestPathScope(GuestRootNames.Artifacts, "op"), "result.bin",
            destination, CancellationToken.None);
        var request = await NextRequestAsync();
        var capabilities = _channel.GetCapabilitiesAsync(CancellationToken.None);
        await NextRequestAsync();
        _transport.PeerSend(GuestPayloadCodec.EncodeStream(
            Guid.Parse(request.OperationId!), GuestStreamId.StandardOutput, "data"u8));

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => download.WaitAsync(TimeSpan.FromSeconds(5)));
        StringAssert.Contains(failure.Error.Message, "disk is full");
        await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => capabilities.WaitAsync(TimeSpan.FromSeconds(5)));
        await _channel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(_transport.IsConnected);
        await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => _channel.ListFilesAsync(new GuestPathScope(GuestRootNames.Work, null), CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => _channel.SendStandardInputAsync(Guid.NewGuid(), "late"u8.ToArray(), CancellationToken.None));
    }

    private sealed class ThrowingOutputStream : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("The disk is full.");
    }

    [TestMethod]
    public async Task Execute_CancelSendInterruptedWithoutAcknowledgement_IsNotOrdinaryCancellation()
    {
        await using var transport = new InterruptedExecutionTransport(GuestMessageTypes.CancelRequest);
        await using var channel = new GuestCommandChannel(transport, new ExecutionTargetEpoch(Epoch));
        channel.Start();
        using var cancellation = new CancellationTokenSource();
        var pending = channel.ExecuteAsync(SampleRequest, null, cancellation.Token);
        await cancellation.CancelAsync();

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        StringAssert.Contains(failure.Error.Message, "before stopping the operation could be confirmed");
        Assert.IsFalse(transport.IsConnected);
    }

    [TestMethod]
    public async Task Execute_InitialSendInterruptedWithUncertainDelivery_IsNotOrdinaryCancellation()
    {
        await using var transport = new InterruptedExecutionTransport(GuestMessageTypes.ExecRequest);
        await using var channel = new GuestCommandChannel(transport, new ExecutionTargetEpoch(Epoch));
        channel.Start();

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => channel.ExecuteAsync(SampleRequest, null, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5)));
        StringAssert.Contains(failure.Error.Message, "delivery could be confirmed");
        Assert.IsFalse(transport.IsConnected);
    }

    private sealed class InterruptedExecutionTransport(string interruptedMessage) : IGuestTransport
    {
        private readonly FakeGuestTransport _inner = new();

        public bool IsConnected => _inner.IsConnected;

        public ValueTask SendFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            if (GuestPayloadCodec.TryDecodeJson(payload.Span)?.Type == interruptedMessage)
            {
                throw new OperationCanceledException("The transport write was interrupted.");
            }
            return _inner.SendFrameAsync(payload, cancellationToken);
        }

        public ValueTask<ReadOnlyMemory<byte>?> ReceiveFrameAsync(CancellationToken cancellationToken) =>
            _inner.ReceiveFrameAsync(cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    [TestMethod]
    public async Task Execute_GuestReportsFailure_SurfacesTheStructuredError()
    {
        var pending = _channel.ExecuteAsync(SampleRequest, callbacks: null, TestContext.CancellationTokenSource.Token);
        var sent = await NextRequestAsync();

        PeerReply(new GuestMessage
        {
            Type = GuestMessageTypes.OperationFailed,
            OperationId = sent.OperationId,
            Error = new ExecutionTargetErrorInfo
            {
                Code = ExecutionTargetErrorCodes.InputNotReady,
                Message = "The Sandbox window is disconnected.",
                UserAction = "Reconnect the Sandbox window, then retry.",
            },
        });

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(async () => await pending);

        Assert.AreEqual(ExecutionTargetErrorCodes.InputNotReady, failure.Error.Code);
        Assert.AreEqual("Reconnect the Sandbox window, then retry.", failure.Error.UserAction);
    }

    [TestMethod]
    public async Task Execute_GuestClosesEarly_ReportsTerminatedNotSuccess()
    {
        var pending = _channel.ExecuteAsync(SampleRequest, callbacks: null, TestContext.CancellationTokenSource.Token);
        await NextRequestAsync();

        _transport.PeerClose();

        // An operation whose guest vanished must never resolve as success.
        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(async () => await pending);

        Assert.AreEqual(ExecutionTargetErrorCodes.Terminated, failure.Error.Code);
        await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => _channel.ExecuteAsync(SampleRequest, null, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => _channel.GetCapabilitiesAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task Dispatch_FrameForUnknownOperation_IsIgnored()
    {
        var pending = _channel.ExecuteAsync(SampleRequest, callbacks: null, TestContext.CancellationTokenSource.Token);
        var sent = await NextRequestAsync();

        // A late frame from an operation that already finished is normal and must not tear down the
        // channel or disturb live operations.
        PeerReply(new GuestMessage
        {
            Type = GuestMessageTypes.ExecCompleted,
            OperationId = Guid.NewGuid().ToString(),
            ExitCode = 99,
        });
        _transport.PeerSend(GuestPayloadCodec.EncodeStream(Guid.NewGuid(), GuestStreamId.StandardOutput, "stale"u8));

        PeerReply(new GuestMessage { Type = GuestMessageTypes.ExecCompleted, OperationId = sent.OperationId, ExitCode = 0 });

        var result = await pending;
        Assert.AreEqual(0, result.ExitCode);
    }

    [TestMethod]
    public async Task Dispatch_MalformedFrames_DoNotBreakTheChannel()
    {
        var pending = _channel.ExecuteAsync(SampleRequest, callbacks: null, TestContext.CancellationTokenSource.Token);
        var sent = await NextRequestAsync();

        // A compromised or buggy guest must not be able to crash the host's receive pump.
        _transport.PeerSend([]);
        _transport.PeerSend([99]);
        _transport.PeerSend([(byte)GuestPayloadKind.Json, .. "not json"u8]);
        _transport.PeerSend([(byte)GuestPayloadKind.Stream, 1, 2, 3]);

        PeerReply(new GuestMessage { Type = GuestMessageTypes.ExecCompleted, OperationId = sent.OperationId, ExitCode = 7 });

        var result = await pending;
        Assert.AreEqual(7, result.ExitCode);
    }

    [TestMethod]
    public async Task Execute_ConcurrentOperations_AreTrackedIndependently()
    {
        var firstOut = new List<string>();
        var secondOut = new List<string>();

        var first = _channel.ExecuteAsync(
            SampleRequest,
            new GuestExecCallbacks(OnStandardOutput: c => firstOut.Add(Encoding.UTF8.GetString(c.Span))),
            TestContext.CancellationTokenSource.Token);
        var firstSent = await NextRequestAsync();

        var second = _channel.ExecuteAsync(
            SampleRequest,
            new GuestExecCallbacks(OnStandardOutput: c => secondOut.Add(Encoding.UTF8.GetString(c.Span))),
            TestContext.CancellationTokenSource.Token);
        var secondSent = await NextRequestAsync();

        Assert.AreNotEqual(firstSent.OperationId, secondSent.OperationId);

        _transport.PeerSend(GuestPayloadCodec.EncodeStream(Guid.Parse(secondSent.OperationId!), GuestStreamId.StandardOutput, "b"u8));
        _transport.PeerSend(GuestPayloadCodec.EncodeStream(Guid.Parse(firstSent.OperationId!), GuestStreamId.StandardOutput, "a"u8));

        PeerReply(new GuestMessage { Type = GuestMessageTypes.ExecCompleted, OperationId = firstSent.OperationId, ExitCode = 0 });
        PeerReply(new GuestMessage { Type = GuestMessageTypes.ExecCompleted, OperationId = secondSent.OperationId, ExitCode = 1 });

        var firstResult = await first;
        var secondResult = await second;

        // Two applications run concurrently in one Sandbox, so their streams must never cross.
        Assert.AreEqual(0, firstResult.ExitCode);
        Assert.AreEqual(1, secondResult.ExitCode);
        CollectionAssert.AreEqual(ExpectedFirstStream, firstOut);
        CollectionAssert.AreEqual(ExpectedSecondStream, secondOut);
    }

    [TestMethod]
    public void PayloadCodec_RoundTripsStreamChunks()
    {
        var operationId = Guid.NewGuid();
        var payload = GuestPayloadCodec.EncodeStream(operationId, GuestStreamId.StandardError, "data"u8);

        Assert.IsTrue(GuestPayloadCodec.TryDecodeStream(payload, out var decodedId, out var stream, out var data));
        Assert.AreEqual(operationId, decodedId);
        Assert.AreEqual(GuestStreamId.StandardError, stream);
        CollectionAssert.AreEqual("data"u8.ToArray(), data.ToArray());
    }

    [TestMethod]
    public void PayloadCodec_RejectsUnknownKindsAndTruncatedHeaders()
    {
        Assert.IsFalse(GuestPayloadCodec.TryGetKind([], out _));
        Assert.IsFalse(GuestPayloadCodec.TryGetKind([0], out _));
        Assert.IsFalse(GuestPayloadCodec.TryGetKind([3], out _));
        Assert.IsFalse(GuestPayloadCodec.TryDecodeStream(new byte[] { (byte)GuestPayloadKind.Stream, 1 }, out _, out _, out _));
        Assert.IsNull(GuestPayloadCodec.TryDecodeJson([(byte)GuestPayloadKind.Json, .. "{"u8]));
    }

    [TestMethod]
    public void PayloadCodec_RejectsUnknownStreamId()
    {
        var payload = new byte[18];
        payload[0] = (byte)GuestPayloadKind.Stream;
        payload[17] = 9;

        Assert.IsFalse(GuestPayloadCodec.TryDecodeStream(payload, out _, out _, out _));
    }

    [TestMethod]
    public async Task ListFiles_LargeUnicodeInventory_UsesBoundedFramesAndExplicitCompletion()
    {
        var expected = Enumerable.Range(0, 10_000)
            .Select(i => new GuestFileInfo(
                $@"日本語\{new string('文', 100)}-{i:D5}.bin", i, i, new string('a', 64)))
            .ToList();
        var pending = _channel.ListFilesAsync(
            new GuestPathScope(GuestRootNames.Deployment, "app"), TestContext.CancellationTokenSource.Token);
        var request = await NextRequestAsync();
        var batches = GuestPayloadCodec.EncodeFileInventory(
            Guid.Parse(request.OperationId!), Epoch, expected).ToList();
        Assert.IsTrue(batches.Count > 1);

        using var codec = new GuestFrameCodec(
            new byte[GuestFrameCodec.KeySize], new byte[GuestFrameCodec.NoncePrefixSize]);
        ulong sequence = 0;
        foreach (var batch in batches)
        {
            Assert.IsTrue(batch.Length <= GuestFrameCodec.MaxPlaintextBytes);
            var encoded = new byte[GuestFrameCodec.GetEncodedSize(batch.Length)];
            Assert.AreEqual(encoded.Length, codec.Encode(batch, sequence++, encoded));
            _transport.PeerSend(batch);
        }

        Assert.IsFalse(pending.IsCompleted, "A batch is not the completed inventory.");
        PeerReply(new GuestMessage
        {
            Type = GuestMessageTypes.ListFilesCompleted,
            OperationId = request.OperationId,
        });
        var actual = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual(expected, actual.ToList());
    }

    [TestMethod]
    public async Task ListFiles_EmptyInventory_CompletesWithoutABatch()
    {
        var pending = _channel.ListFilesAsync(
            new GuestPathScope(GuestRootNames.Work, null), TestContext.CancellationTokenSource.Token);
        var request = await NextRequestAsync();
        PeerReply(new GuestMessage
        {
            Type = GuestMessageTypes.ListFilesCompleted,
            OperationId = request.OperationId,
        });
        Assert.HasCount(0, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>MSTest injects this; used for per-test cancellation.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>Disposes the channel and the fake transport after each test.</summary>
    /// <remarks>
    /// MSTest disposes the test instance after every test method, so this runs in place of a
    /// <c>[TestCleanup]</c> and keeps both disposables owned in one place.
    /// </remarks>
    public void Dispose()
    {
        _channel?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _transport?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}
