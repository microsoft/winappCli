// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// Standard input really reaches the guest process, for every command that says it does.
/// </summary>
/// <remarks>
/// Driven against the real <see cref="GuestCommandServer"/> over an in-memory transport, so what is
/// asserted is what the guest process actually received — not merely that a send was attempted.
/// The regression these guard is a set of callbacks that forwarded stdout and stderr and quietly
/// dropped stdin, which no output-only assertion would have caught.
/// </remarks>
[TestClass]
public class GuestStandardInputPumpTests
{
    private static readonly ExecutionTargetEpoch Epoch = ExecutionTargetEpoch.Create("sandbox-1", "nonce-a");

    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Input that is already available before the command starts must still arrive.
    /// </summary>
    /// <remarks>
    /// This is the ordinary <c>echo hi | winapp target exec sandbox ...</c> shape: the bytes exist before
    /// winapp does. They are only deliverable because the pump starts from the operation ID, which
    /// the channel publishes as it sends the request. A pump started any earlier would address an
    /// operation the guest has not heard of, and the guest would drop it.
    /// </remarks>
    [TestMethod]
    public async Task EagerInput_ReachesTheGuestProcess()
    {
        await using var harness = new Harness();

        // Fully buffered before the operation exists.
        using var input = new MemoryStream("hello guest"u8.ToArray());

        var execution = harness.Channel.ExecuteAsync(
            Request("findstr", "x"),
            new GuestExecCallbacks(
                OnOperationId: id => _ = GuestStandardInputPump.RunAsync(
                    harness.Channel, id, input, harness.Token)),
            harness.Token);

        var process = await harness.WaitForProcessAsync();

        await harness.WaitUntilAsync(() => process.StandardInputClosed);

        Assert.AreEqual(
            "hello guest",
            string.Concat(process.StandardInput.Select(Encoding.UTF8.GetString)));

        process.Exit(0);
        Assert.AreEqual(0, (await execution).ExitCode);
    }

    /// <summary>
    /// Input larger than one read arrives whole, in order, and byte-identical.
    /// </summary>
    /// <remarks>
    /// The payload deliberately mixes arbitrary bytes with a multi-byte UTF-8 character positioned
    /// to straddle the pump's internal buffer boundary. Anything that decoded per chunk instead of
    /// forwarding raw bytes would corrupt exactly this case, and a single-chunk test would never
    /// show it.
    /// </remarks>
    [TestMethod]
    public async Task MultiChunkBinaryAndUtf8_ArriveByteIdenticalAndInOrder()
    {
        await using var harness = new Harness();

        var payload = BuildStraddlingPayload();
        using var input = new MemoryStream(payload);

        var execution = harness.Channel.ExecuteAsync(
            Request("cmd", "/c", "more"),
            new GuestExecCallbacks(
                OnOperationId: id => _ = GuestStandardInputPump.RunAsync(
                    harness.Channel, id, input, harness.Token)),
            harness.Token);

        var process = await harness.WaitForProcessAsync();

        await harness.WaitUntilAsync(() => process.StandardInputClosed);

        var received = process.StandardInput.SelectMany(chunk => chunk).ToArray();

        Assert.IsGreaterThan(1, process.StandardInput.Count, "The payload must span more than one chunk.");
        CollectionAssert.AreEqual(payload, received);

        process.Exit(0);
        await execution;
    }

    /// <summary>
    /// Host EOF closes guest standard input, which is what lets a read-to-end guest process finish.
    /// </summary>
    [TestMethod]
    public async Task HostEof_ClosesGuestStandardInput()
    {
        await using var harness = new Harness();

        using var input = new MemoryStream("done"u8.ToArray());

        var execution = harness.Channel.ExecuteAsync(
            Request("findstr", "x"),
            new GuestExecCallbacks(
                OnOperationId: id => _ = GuestStandardInputPump.RunAsync(
                    harness.Channel, id, input, harness.Token)),
            harness.Token);

        var process = await harness.WaitForProcessAsync();

        await harness.WaitUntilAsync(() => process.StandardInputClosed);
        Assert.IsTrue(process.StandardInputClosed);

        process.Exit(0);
        await execution;
    }

    /// <summary>
    /// A command with nothing on standard input closes guest stdin immediately and does not wait.
    /// </summary>
    /// <remarks>
    /// The empty stream stands in for input that is redirected from an empty source or already at
    /// end. The point is that the pump completes on its own rather than leaving the guest waiting
    /// for input that is never coming.
    /// </remarks>
    [TestMethod]
    public async Task EmptyInput_ClosesGuestStandardInputWithoutWaiting()
    {
        await using var harness = new Harness();

        using var input = new MemoryStream([]);

        var execution = harness.Channel.ExecuteAsync(
            Request("dotnet", "--info"),
            new GuestExecCallbacks(
                OnOperationId: id => _ = GuestStandardInputPump.RunAsync(
                    harness.Channel, id, input, harness.Token)),
            harness.Token);

        var process = await harness.WaitForProcessAsync();

        await harness.WaitUntilAsync(() => process.StandardInputClosed);

        Assert.IsEmpty(process.StandardInput);
        Assert.IsTrue(process.StandardInputClosed);

        process.Exit(0);
        Assert.AreEqual(0, (await execution).ExitCode);
    }

    /// <summary>
    /// Cancellation stops the pump and does not announce end of input.
    /// </summary>
    /// <remarks>
    /// Ctrl+C is the user reclaiming the command. The guest tears the operation down, so sending EOF
    /// into that teardown would be a write on a closing channel rather than useful signalling. The
    /// pump must also complete rather than hang, which is what awaiting it asserts.
    /// </remarks>
    [TestMethod]
    public async Task Cancellation_StopsThePumpWithoutAnnouncingEof()
    {
        await using var harness = new Harness();

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(harness.Token);

        var execution = harness.Channel.ExecuteAsync(
            Request("cmd", "/c", "more"),
            callbacks: null,
            harness.Token);

        var process = await harness.WaitForProcessAsync();

        // A stream that never yields and never ends, so only cancellation can end the pump.
        var pump = GuestStandardInputPump.RunAsync(
            harness.Channel, Guid.NewGuid(), new BlockingStream(), cancellation.Token);

        await cancellation.CancelAsync();

        // Completes, and swallows the cancellation rather than faulting the command around it.
        await pump.WaitAsync(harness.Token);

        Assert.IsTrue(pump.IsCompletedSuccessfully);
        Assert.IsFalse(process.StandardInputClosed, "Cancellation must not be reported to the guest as EOF.");

        process.Exit(0);
        await execution;
    }

    /// <summary>
    /// Every command documented as streaming stdin actually attaches the pump.
    /// </summary>
    /// <remarks>
    /// The defect was not in the pump but in the wiring: two call sites built callbacks that carried
    /// output handlers and no <c>OnOperationId</c>, so their documented stdin forwarding silently did
    /// nothing. The behaviour is one line per call site and is invisible to a channel-level test,
    /// so the source is asserted directly — that is exactly the shape of the regression.
    /// </remarks>
    [TestMethod]
    public async Task EveryCommandThatDocumentsStdinForwarding_AttachesThePump()
    {
        var commands = new DirectoryInfo(Path.Join(FindRepositoryRoot(), "src", "winapp-CLI", "WinApp.Cli", "Commands"));

        foreach (var file in (string[])["TargetCommand.cs", "ExecutionTargetUiRouter.cs", "RunCommand.Target.cs"])
        {
            var source = await File.ReadAllTextAsync(
                Path.Join(commands.FullName, file), TestContext.CancellationToken);

            StringAssert.Contains(
                source,
                $"{nameof(GuestStandardInputPump)}.{nameof(GuestStandardInputPump.Attach)}",
                $"{file} documents stdin forwarding but does not attach the shared pump.");
        }
    }

    /// <summary>Bytes that force more than one pump read and split a UTF-8 character across the seam.</summary>
    private static byte[] BuildStraddlingPayload()
    {
        const int BufferSize = 8 * 1024;

        var payload = new byte[(BufferSize * 2) + 64];
        Random.Shared.NextBytes(payload);

        // '€' is three bytes; placing it one byte before the boundary guarantees it is split.
        var euro = Encoding.UTF8.GetBytes("€");
        payload[BufferSize - 1] = euro[0];
        payload[BufferSize] = euro[1];
        payload[BufferSize + 1] = euro[2];

        return payload;
    }

    private static GuestExecRequest Request(string executable, params string[] arguments) => new()
    {
        Executable = executable,
        Arguments = [.. arguments],
    };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Join(directory.FullName, "scripts", "build-cli.ps1")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the winapp repository root.");
    }

    /// <summary>A stream that yields nothing and never ends, so only cancellation completes a read.</summary>
    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            new(Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(
                _ => 0, TaskScheduler.Default));

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Host channel against the real guest command server over one in-memory transport.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(60));
        private readonly Task _serverTask;
        private readonly FakeGuestProcessHostFactory _processes = new();

        public Harness()
        {
            var pair = new LoopbackTransportPair();

            var server = new GuestCommandServer(
                pair.Guest,
                Epoch,
                _processes,
                new StaticGuestSessionProbe(new GuestSessionInfo(1, "WinSta0", true)),
                new GuestAgentIdentity("1.0.0", "hash", "arm64", 1, 1));

            _serverTask = server.RunAsync(_cancellation.Token);

            Channel = new GuestCommandChannel(pair.Host, Epoch);
            Channel.Start();
        }

        public GuestCommandChannel Channel { get; }

        public CancellationToken Token => _cancellation.Token;

        public async Task<FakeGuestProcessHost> WaitForProcessAsync()
        {
            await _processes.StartSignal.WaitAsync(Token);
            _processes.Started.TryPeek(out var process);

            return process!;
        }

        public async Task WaitUntilAsync(Func<bool> condition)
        {
            while (!condition())
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync();

            try
            {
                await _serverTask;
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }

            await Channel.DisposeAsync();
            _cancellation.Dispose();
        }
    }
}
