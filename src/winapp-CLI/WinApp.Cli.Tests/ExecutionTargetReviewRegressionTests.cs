// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// Regression tests for the findings of the independent review of the execution-target work.
/// </summary>
/// <remarks>
/// Grouped together deliberately. Each of these is a case where the code was plausible and wrong,
/// so the test exists to state the rule rather than to cover a line: readiness is a property of the
/// moment and not of the connection; a cancelled transfer must release its handle; the no-downgrade
/// rule has no architecture exception; a forwarded owner must not be silently altered; and a
/// managed root must not be walked through a link.
/// </remarks>
[TestClass]
public class ExecutionTargetReviewRegressionTests
{
    private static readonly ExecutionTargetEpoch Epoch = ExecutionTargetEpoch.Create("sandbox-1", "nonce-a");

    private static GuestSessionInfo Interactive => new(SessionId: 1, "WinSta0", HasInputDesktop: true);

    private static GuestAgentIdentity Identity => new("1.0.0", "hash", "arm64", 1, 1);

    public TestContext TestContext { get; set; } = null!;

    // ---- Finding 2: readiness must be re-verified immediately before real input ----

    [TestMethod]
    public async Task RealInputRequest_DisconnectedClient_IsRefusedAtDispatch()
    {
        // The client was connected when the channel opened and is closed by the time the command
        // arrives -- the exact sequence a user produces by closing the Sandbox window mid-workflow.
        var probe = new MutableSessionProbe(Interactive);
        await using var harness = new Harness(probe);

        // Prove the capability handshake saw a healthy session first.
        var capabilities = await harness.Channel.GetCapabilitiesAsync(harness.Token);
        Assert.IsTrue(capabilities.SupportsRealInput);

        probe.Session = Interactive with { HasInputDesktop = false };

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => harness.Channel.ExecuteAsync(
                new GuestExecRequest
                {
                    Executable = "winapp.exe",
                    Arguments = ["ui", "click", "Submit"],
                    RequiresRealInput = true,
                },
                callbacks: null,
                harness.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.InputNotReady, failure.Error.Code);

        // Refused before anything ran: a started process would have reported input it could not
        // deliver, which is the outcome the specification forbids outright.
        Assert.IsTrue(harness.Processes.Started.IsEmpty);
    }

    [TestMethod]
    public async Task NonInputRequest_DisconnectedClient_StillRuns()
    {
        var probe = new MutableSessionProbe(Interactive with { HasInputDesktop = false });
        await using var harness = new Harness(probe);

        // Inspection keeps working with the client closed, so it must not be caught by the input
        // readiness gate.
        var execution = harness.Channel.ExecuteAsync(
            new GuestExecRequest { Executable = "winapp.exe", Arguments = ["ui", "inspect"] },
            callbacks: null,
            harness.Token);

        var process = await harness.Processes.WaitForNextAsync(harness.Token);
        process.Exit(0);

        Assert.AreEqual(0, (await execution).ExitCode);
    }

    // ---- Finding 4: standard input must not be able to overtake its own request ----

    [TestMethod]
    public async Task EagerStandardInput_CannotOvertakeTheRequestItBelongsTo()
    {
        await using var harness = new Harness(new MutableSessionProbe(Interactive));

        var operationId = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);

        var execution = harness.Channel.ExecuteAsync(
            new GuestExecRequest { Executable = "winapp.exe", Arguments = ["ui", "record"] },
            new GuestExecCallbacks(OnOperationId: id => operationId.TrySetResult(id)),
            harness.Token);

        // A caller that writes the instant it learns the operation ID. If the ID were published
        // before the request was sent, these bytes could arrive first and be dropped as belonging
        // to an operation the guest had not heard of.
        var id = await operationId.Task.WaitAsync(harness.Token);
        await harness.Channel.SendStandardInputAsync(id, "go"u8.ToArray(), harness.Token);

        var process = await harness.Processes.WaitForNextAsync(harness.Token);

        await WaitUntilAsync(() => process.StandardInput.Count > 0, harness.Token);
        Assert.AreEqual("go", Encoding.UTF8.GetString(process.StandardInput[0]));

        process.Exit(0);
        await execution;
    }

    // ---- Finding 5: a cancelled upload must release its handle and discard the partial ----

    [TestMethod]
    public async Task CancelledUpload_ReleasesTheDestinationForAnImmediateRetry()
    {
        var managedRoot = TestPaths.TempRoot(nameof(CancelledUpload_ReleasesTheDestinationForAnImmediateRetry));
        Directory.CreateDirectory(managedRoot);

        try
        {
            await using var harness = new Harness(new MutableSessionProbe(Interactive), managedRoot);

            var scope = new GuestPathScope(GuestRootNames.Work, Scope: null);
            var payload = "complete-content"u8.ToArray();
            var hash = await ComputeHashAsync(payload);

            using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(harness.Token))
            {
                // A transfer that announces more than it will ever send, then is cancelled: the
                // guest is left mid-write holding the destination's temporary file.
                var stalled = new StallingStream(payload, cancellation.Token);

                var upload = harness.Channel.PutFileAsync(
                    scope,
                    new GuestFileInfo("payload.bin", payload.Length + 64, DateTime.UtcNow.Ticks, hash),
                    stalled,
                    cancellation.Token);

                await Task.Delay(100, harness.Token);
                await cancellation.CancelAsync();

                await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => upload);
            }

            // Immediately retried in full. This is what fails if the cancelled write was left in
            // the guest's table with its handle open.
            await using var content = new MemoryStream(payload);
            await harness.Channel.PutFileAsync(
                scope,
                new GuestFileInfo("payload.bin", payload.Length, DateTime.UtcNow.Ticks, hash),
                content,
                harness.Token);

            var listed = await harness.Channel.ListFilesAsync(scope, harness.Token);
            Assert.AreEqual(1, listed.Count);
            Assert.AreEqual("payload.bin", listed[0].RelativePath);

            // And no partial file survives to be mistaken for content.
            Assert.IsFalse(
                Directory.EnumerateFiles(managedRoot, "*.part", SearchOption.AllDirectories).Any());
        }
        finally
        {
            TryDeleteDirectory(managedRoot);
        }
    }

    // ---- Finding 6: the no-downgrade rule has no architecture exception ----

    [TestMethod]
    public void NewerGuestUnderEmulation_IsNeverDowngraded()
    {
        var host = new GuestAgentIdentity("1.2.0", "host-hash", "arm64", 1, 1);

        // A guest running emulated, or simply reporting a different architecture spelling. Deciding
        // on architecture first would replace it -- a downgrade reached without ever comparing
        // versions.
        var newerGuest = Heartbeat("2.0.0", "guest-hash", "x64");

        Assert.AreEqual(GuestAgentAction.Reuse, GuestAgentUpdatePlanner.Plan(host, newerGuest).Action);
        Assert.IsFalse(GuestAgentUpdatePlanner.CanForceRepair(host, newerGuest));
    }

    [TestMethod]
    public void NewerIncompatibleGuestUnderEmulation_FailsRatherThanInstalling()
    {
        var host = new GuestAgentIdentity("1.2.0", "host-hash", "arm64", 1, 1);
        var guest = Heartbeat("2.0.0", "guest-hash", "x64", protocolMinimum: 99, protocolMaximum: 100);

        Assert.AreEqual(GuestAgentAction.FailIncompatible, GuestAgentUpdatePlanner.Plan(host, guest).Action);
    }

    [TestMethod]
    public void UnorderableGuestVersion_FailsClosedRatherThanReplacing()
    {
        var host = new GuestAgentIdentity("1.2.0", "host-hash", "arm64", 1, 1);
        var guest = Heartbeat("not-a-version", "guest-hash", "arm64");

        // A version that cannot be ordered cannot be proven older, so replacing it might be a
        // downgrade. Failing is the only outcome that cannot silently move the guest backwards.
        Assert.AreEqual(GuestAgentAction.FailIncompatible, GuestAgentUpdatePlanner.Plan(host, guest).Action);
        Assert.IsFalse(GuestAgentUpdatePlanner.CanForceRepair(host, guest));
    }

    [TestMethod]
    public void OlderGuestWithDifferentArchitecture_IsStillReplaced()
    {
        var host = new GuestAgentIdentity("2.0.0", "host-hash", "arm64", 1, 1);

        // Host is genuinely newer, so replacing is not a downgrade and the architecture mismatch is
        // the reason a full install rather than an in-place update is needed.
        Assert.AreEqual(
            GuestAgentAction.Install,
            GuestAgentUpdatePlanner.Plan(host, Heartbeat("1.0.0", "guest-hash", "x64")).Action);
    }

    // ---- Finding 8: a forwarded owner must never be silently altered ----

    [TestMethod]
    public void ExplicitOwner_IsPreservedByteForByte()
    {
        // Whitespace is significant to the guest's own resolver, so trimming here would make two
        // values that are different locally into one workflow in the guest.
        const string Padded = "  workflow-7  ";

        Assert.AreEqual(
            Padded,
            GuestOwnerContext.ResolveHostOwner(new Dictionary<string, string?>
            {
                [GuestOwnerContext.OwnerVariable] = Padded,
            }));
    }

    [TestMethod]
    public void BlankExplicitOwner_IsRefusedRatherThanIgnored()
    {
        // Falling back to the parent-derived owner would silently group this command with every
        // other command under the same parent, which is not what the caller asked for.
        var failure = Assert.ThrowsExactly<ExecutionTargetException>(
            () => GuestOwnerContext.ResolveHostOwner(new Dictionary<string, string?>
            {
                [GuestOwnerContext.OwnerVariable] = "   ",
            }));

        Assert.AreEqual(ExecutionTargetErrorCodes.TargetAmbiguous, failure.Error.Code);
    }

    [TestMethod]
    public void OversizedExplicitOwner_IsRefusedRatherThanTruncated()
    {
        var oversized = new string('x', GuestOwnerContext.MaximumOwnerLength + 1);

        var failure = Assert.ThrowsExactly<ExecutionTargetException>(
            () => GuestOwnerContext.ResolveHostOwner(new Dictionary<string, string?>
            {
                [GuestOwnerContext.OwnerVariable] = oversized,
            }));

        Assert.AreEqual(ExecutionTargetErrorCodes.TargetAmbiguous, failure.Error.Code);

        // Truncation would merge two distinct long owners into one; and the value itself must never
        // appear in the failure.
        Assert.IsFalse(failure.Error.Message.Contains(oversized, StringComparison.Ordinal));
        Assert.IsFalse(failure.Error.Context?.Values.Any(v => v.Contains('x', StringComparison.Ordinal)) ?? false);
    }

    [TestMethod]
    public void MaximumLengthOwner_IsAccepted()
    {
        var exact = new string('x', GuestOwnerContext.MaximumOwnerLength);

        Assert.AreEqual(
            exact,
            GuestOwnerContext.ResolveHostOwner(new Dictionary<string, string?>
            {
                [GuestOwnerContext.OwnerVariable] = exact,
            }));
    }

    // ---- Finding 9: a managed root must not be walked or written through a link ----

    [TestMethod]
    public async Task DirectoryReparsePoint_IsNotWalkedThrough()
    {
        var root = TestPaths.TempRoot(nameof(DirectoryReparsePoint_IsNotWalkedThrough));
        var outside = TestPaths.TempRoot("outside");

        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(
            TestPaths.Under(outside, "secret.txt"), "not yours", TestContext.CancellationToken);

        try
        {
            var files = new GuestFileService(root);
            var scope = new GuestPathScope(GuestRootNames.Work, Scope: null);
            var workRoot = files.ResolveScopeDirectory(scope, create: true);

            var link = TestPaths.Under(workRoot, "linked");

            if (!TryCreateDirectoryLink(link, outside))
            {
                Assert.Inconclusive("Creating a directory link requires privileges this run does not have.");
                return;
            }

            var listed = await files.ListAsync(scope, TestContext.CancellationToken);

            // Following the link would report a file that is not in the managed root at all, and
            // reconciliation would then treat content outside the root as content it owns.
            Assert.IsFalse(
                listed.Any(f => f.RelativePath.Contains("secret", StringComparison.OrdinalIgnoreCase)),
                "Enumeration must not descend through a directory link.");
        }
        finally
        {
            TryDeleteDirectory(root);
            TryDeleteDirectory(outside);
        }
    }

    [TestMethod]
    public void WritingThroughADirectoryLink_IsRefused()
    {
        var root = TestPaths.TempRoot(nameof(WritingThroughADirectoryLink_IsRefused));
        var outside = TestPaths.TempRoot("outside");

        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);

        try
        {
            var files = new GuestFileService(root);
            var scope = new GuestPathScope(GuestRootNames.Work, Scope: null);
            var workRoot = files.ResolveScopeDirectory(scope, create: true);

            if (!TryCreateDirectoryLink(TestPaths.Under(workRoot, "linked"), outside))
            {
                Assert.Inconclusive("Creating a directory link requires privileges this run does not have.");
                return;
            }

            // Lexically this path is inside the managed root. Only checking the ancestors' reparse
            // attributes catches that it is not.
            var failure = Assert.ThrowsExactly<ExecutionTargetException>(
                () => files.BeginWrite(
                    scope,
                    new GuestFileInfo(@"linked\payload.bin", 4, DateTime.UtcNow.Ticks, new string('0', 64))));

            Assert.AreEqual(ExecutionTargetErrorCodes.DeploymentDirty, failure.Error.Code);
            Assert.IsFalse(File.Exists(TestPaths.Under(outside, "payload.bin")));
        }
        finally
        {
            TryDeleteDirectory(root);
            TryDeleteDirectory(outside);
        }
    }

    private static GuestAgentHeartbeat Heartbeat(
        string version,
        string hash,
        string architecture,
        int protocolMinimum = 1,
        int protocolMaximum = 1) => new()
        {
            SchemaVersion = GuestAgentHeartbeat.CurrentSchemaVersion,
            Version = version,
            BinaryHash = hash,
            Architecture = architecture,
            ProtocolMinimum = protocolMinimum,
            ProtocolMaximum = protocolMaximum,
            Ready = true,
            TargetEpoch = Epoch.Value,
            Port = 5000,
            PublishedUtc = DateTimeOffset.UtcNow,
        };

    private static async Task<string> ComputeHashAsync(byte[] content)
    {
        var path = TestPaths.TempFile("hash", ".bin");
        await File.WriteAllBytesAsync(path, content);

        try
        {
            return await GuestFileService.ComputeHashAsync(path, CancellationToken.None);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static bool TryCreateDirectoryLink(string linkPath, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Symbolic links need Developer Mode or elevation. The test reports inconclusive rather
            // than passing vacuously.
            return false;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp cleanup is not worth failing a test over.
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The expected condition was not reached in time.");
            }

            await Task.Delay(10, cancellationToken);
        }
    }

    /// <summary>A probe whose answer can change between calls, as a real session's does.</summary>
    private sealed class MutableSessionProbe(GuestSessionInfo session) : IGuestSessionProbe
    {
        public GuestSessionInfo Session { get; set; } = session;

        public GuestSessionInfo Probe() => Session;
    }

    /// <summary>A stream that yields its content and then blocks, standing in for a stalled source.</summary>
    private sealed class StallingStream(byte[] content, CancellationToken stallUntil) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => content.Length;

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_position < content.Length)
            {
                var take = Math.Min(buffer.Length, content.Length - _position);
                content.AsMemory(_position, take).CopyTo(buffer);
                _position += take;
                return take;
            }

            // Never completes: the transfer is left mid-flight until it is cancelled.
            await Task.Delay(System.Threading.Timeout.Infinite, stallUntil).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A host channel and guest server over one in-memory transport.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(60));
        private readonly GuestCommandServer _server;
        private readonly Task _serverTask;

        public Harness(IGuestSessionProbe probe, string? managedRoot = null)
        {
            var pair = new LoopbackTransportPair();
            Processes = new FakeGuestProcessHostFactory();

            _server = new GuestCommandServer(
                pair.Guest,
                Epoch,
                Processes,
                probe,
                Identity,
                managedRoot is null ? null : new GuestFileService(managedRoot));

            _serverTask = _server.RunAsync(_cancellation.Token);

            Channel = new GuestCommandChannel(pair.Host, Epoch);
            Channel.Start();
        }

        public FakeGuestProcessHostFactory Processes { get; }

        public GuestCommandChannel Channel { get; }

        public CancellationToken Token => _cancellation.Token;

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
            await _server.DisposeAsync();
            _cancellation.Dispose();
        }
    }
}
