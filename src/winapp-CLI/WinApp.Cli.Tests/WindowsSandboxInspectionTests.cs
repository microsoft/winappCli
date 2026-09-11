// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

/// <summary>
/// What reading a Windows Sandbox target is allowed to change about it: nothing.
/// </summary>
/// <remarks>
/// A report an agent polls has to be free to run at any moment, including while another winapp
/// process is preparing the same target. Writing back what it just read would make every snapshot a
/// new revision of the file it is describing, and would contend for that file with the command
/// actually doing the work — so a caller asking "what is there?" could slow down, or lose, the
/// answer it was waiting for.
/// </remarks>
[TestClass]
public class WindowsSandboxInspectionTests
{
    private const string InstanceId = "sandbox-under-inspection";
    private static readonly SandboxClientWindow LiveClient = new(0x900, 4321, 777_000);

    private DirectoryInfo _root = null!;
    private TargetStateStore _stateStore = null!;
    private FakeWindowsSandboxCli _cli = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = new DirectoryInfo(TestPaths.TempRoot(nameof(WindowsSandboxInspectionTests)));
        _root.Create();
        _stateStore = new TargetStateStore(new TargetStateDirectoryProvider(_root.FullName));
        _cli = new FakeWindowsSandboxCli();
        _cli.SetRunning(InstanceId);

        // A target another command already prepared: there is something on disk for a read to
        // disturb, which is the only state in which "reads nothing" is worth asserting.
        _stateStore.Commit(
            WindowsSandboxTarget.Default,
            new TargetState
            {
                SchemaVersion = 0,
                Revision = 0,
                TargetKind = WindowsSandboxTarget.Default.Kind,
                TargetId = WindowsSandboxTarget.Default.Id,
                InstanceId = InstanceId,
                BootNonce = "nonce-for-inspection",
            },
            expectedRevision: 0);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            _root.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    [TestMethod]
    public async Task Inspect_LeavesThePersistedRecordExactlyAsItFoundIt()
    {
        var backend = CreateBackend();
        await backend.TryAttachAsync(TestContext.CancellationToken);
        var before = Fingerprint();

        var surface = backend.InspectDesktopSurface();

        Assert.AreEqual(LiveClient.Handle, surface.WindowHandle);
        Assert.AreEqual(before, Fingerprint(), "Inspection wrote to the state it was asked to describe.");
    }

    [TestMethod]
    public async Task Inspect_RepeatedManyTimes_NeverAdvancesTheRevision()
    {
        var backend = CreateBackend();
        await backend.TryAttachAsync(TestContext.CancellationToken);
        var revision = _stateStore.Read(WindowsSandboxTarget.Default)?.Revision;

        for (var i = 0; i < 5; i++)
        {
            backend.InspectDesktopSurface();
        }

        Assert.AreEqual(revision, _stateStore.Read(WindowsSandboxTarget.Default)?.Revision);
    }

    [TestMethod]
    public async Task Attach_ForAReport_DoesNotRecordTheConnectionItLookedUp()
    {
        var backend = CreateBackend();
        var before = Fingerprint();

        await backend.TryAttachAsync(TestContext.CancellationToken);

        Assert.AreEqual(before, Fingerprint(), "Attaching to look is not attaching to use.");
    }

    /// <summary>
    /// A manual client may be used where it stands, but must not be persisted as winapp-owned.
    /// </summary>
    [TestMethod]
    public async Task Resolve_ForACapture_DoesNotRecordTheClientItAdopted()
    {
        var backend = CreateBackend();
        await backend.TryAttachAsync(TestContext.CancellationToken);
        var before = _stateStore.Read(WindowsSandboxTarget.Default)!.Revision;

        backend.ResolveDesktopSurface(TargetDesktopUse.PixelCapture);

        var after = _stateStore.Read(WindowsSandboxTarget.Default)!;
        Assert.AreEqual(before, after.Revision);
        Assert.IsNull(after.ClientWindowHandle);
        Assert.IsFalse(after.ClientOwnedByWinapp);
    }

    /// <summary>
    /// A stale owned identity remains only a lookup hint; the sole live fallback stays adopted across
    /// repeated resolutions and therefore cannot acquire permission to move when later minimized.
    /// </summary>
    [TestMethod]
    public async Task Resolve_WithStaleRememberedClient_NeverCachesOrMovesTheAdoptedFallback()
    {
        var staleClient = new SandboxClientWindow(0x700, 7000, 700_000);
        var state = _stateStore.Read(WindowsSandboxTarget.Default)!;
        _stateStore.Commit(
            WindowsSandboxTarget.Default,
            state with
            {
                ClientWindowHandle = staleClient.Handle,
                ClientProcessId = staleClient.ProcessId,
                ClientProcessStartTicksUtc = staleClient.StartTicksUtc,
                ClientOwnedByWinapp = true,
            },
            state.Revision);

        var controller = new TrackingAdoptedClientController(LiveClient);
        var backend = CreateBackend(controller);
        await backend.TryAttachAsync(TestContext.CancellationToken);
        var before = _stateStore.Read(WindowsSandboxTarget.Default)!;

        var first = backend.ResolveDesktopSurface(TargetDesktopUse.PixelCapture);
        var second = backend.ResolveDesktopSurface(TargetDesktopUse.PixelCapture);

        Assert.IsTrue(first.Adopted);
        Assert.IsTrue(second.Adopted);
        CollectionAssert.AreEqual(
            new[] { staleClient, staleClient },
            controller.RememberedClients);

        controller.IsMinimized = true;
        Assert.ThrowsExactly<ExecutionTargetException>(
            () => backend.ResolveDesktopSurface(TargetDesktopUse.PixelCapture));

        Assert.AreEqual(0, controller.ParkCount);
        CollectionAssert.AreEqual(
            new[] { staleClient, staleClient, staleClient },
            controller.RememberedClients);
        var after = _stateStore.Read(WindowsSandboxTarget.Default)!;
        Assert.AreEqual(before.Revision, after.Revision);
        Assert.AreEqual(staleClient.Handle, after.ClientWindowHandle);
        Assert.AreEqual(staleClient.ProcessId, after.ClientProcessId);
        Assert.AreEqual(staleClient.StartTicksUtc, after.ClientProcessStartTicksUtc);
        Assert.IsTrue(after.ClientOwnedByWinapp);
    }

    /// <summary>Everything about the persisted record that a read must not disturb.</summary>
    private string Fingerprint()
    {
        var directories = new TargetStateDirectoryProvider(_root.FullName);
        var files = directories
            .GetTargetRoot(WindowsSandboxTarget.Default, create: true)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .OrderBy(file => file.FullName, StringComparer.Ordinal)
            .Select(file => $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");

        var state = _stateStore.Read(WindowsSandboxTarget.Default);

        return string.Join('\n', [$"revision={state?.Revision}", .. files]);
    }

    private WindowsSandboxBackend CreateBackend(IWindowsSandboxWindowController? windowController = null)
    {
        var directories = new TargetStateDirectoryProvider(_root.FullName);
        var binary = new FileInfo(Path.Join(_root.FullName, "winapp.exe"));
        File.WriteAllText(binary.FullName, "agent");

        return new WindowsSandboxBackend(
            _cli,
            new WindowsSandboxLifecycle(_cli, _stateStore),
            directories,
            new InspectionBinaryProvider(binary),
            windowController ?? new FixedClientWindowController(LiveClient),
            setup: null,
            stateStore: _stateStore);
    }

    private sealed class InspectionBinaryProvider(FileInfo binary) : IHostWinappBinaryProvider
    {
        public FileInfo GetBinary() => binary;
    }

    /// <summary>A desktop with exactly one client window open, which is never moved.</summary>
    private sealed class FixedClientWindowController(SandboxClientWindow client)
        : IWindowsSandboxWindowController
    {
        public WindowsSandboxWindowSnapshot Capture() => new(default);

        public Task<SandboxClientWindow?> PlaceConnectedClientAsync(
            WindowsSandboxWindowSnapshot snapshot,
            SandboxConnectAttempt attempt,
            CancellationToken cancellationToken) => Task.FromResult<SandboxClientWindow?>(client);

        public SandboxClientWindow ResolveClient(SandboxClientWindow? remembered) => client;
    }

    private sealed class TrackingAdoptedClientController(SandboxClientWindow client)
        : IWindowsSandboxWindowController
    {
        public List<SandboxClientWindow?> RememberedClients { get; } = [];

        public bool IsMinimized { get; set; }

        public int ParkCount { get; private set; }

        public WindowsSandboxWindowSnapshot Capture() => new(default);

        public Task<SandboxClientWindow?> PlaceConnectedClientAsync(
            WindowsSandboxWindowSnapshot snapshot,
            SandboxConnectAttempt attempt,
            CancellationToken cancellationToken) => Task.FromResult<SandboxClientWindow?>(client);

        public SandboxClientWindow ResolveClient(SandboxClientWindow? remembered) => client;

        public SandboxClientStatus EnsureClientReady(
            SandboxClientWindow? remembered,
            TargetDesktopUse use)
        {
            RememberedClients.Add(remembered);
            if (!IsMinimized)
            {
                return new SandboxClientStatus(client, IsMinimized: false);
            }

            if (remembered == client)
            {
                ParkCount++;
                return new SandboxClientStatus(client, IsMinimized: false);
            }

            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.ArtifactFailed,
                "The adopted client is minimized.",
                userAction: "Restore or reconnect the existing Windows Sandbox window, then retry.");
        }
    }
}
