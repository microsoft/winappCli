// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

/// <summary>
/// Closing the window winapp opened for a Sandbox it started ends that Sandbox, and nothing else.
/// </summary>
/// <remarks>
/// <c>wsb start</c> creates a Sandbox with no window and only <c>wsb stop</c> ends it, so closing the
/// viewer winapp opened left the Sandbox running out of sight, and opening Windows Sandbox from
/// Start then failed with "Only one running instance of Windows Sandbox is allowed".
/// </remarks>
[TestClass]
public class SandboxClientWatcherTests
{
    private const string InstanceId = "sandbox-1";
    private const int ClientProcessId = 4321;
    private const long ClientStartTicks = 638_900_000_000_000_000;
    private const long ClientWindow = 0x1234;

    private static readonly SandboxClientWindow s_client = new((nint)ClientWindow, ClientProcessId, ClientStartTicks);

    private DirectoryInfo _root = null!;
    private TargetStateStore _stateStore = null!;
    private FakeWindowsSandboxCli _cli = null!;
    private SandboxClientWatcher _watcher = null!;
    private int _ended;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = new DirectoryInfo(TestPaths.TempRoot(nameof(SandboxClientWatcherTests)));
        _root.Create();
        var directories = new TargetStateDirectoryProvider(_root.FullName);
        _stateStore = new TargetStateStore(directories);
        _cli = new FakeWindowsSandboxCli();
        _watcher = new SandboxClientWatcher(
            _cli, _stateStore, new TargetMutationLock(directories), new TargetConnectionLock(directories))
        {
            WaitForClientExitAsync = (_, _) => Task.CompletedTask,
            IsAnotherClientRunning = _ => false,
            EndClosedClient = _ => _ended++,
        };
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
            // Temp cleanup is not worth failing a test over.
        }
    }

    private void Record(string origin, int clientProcessId = ClientProcessId, bool ownedByWinapp = true) =>
        _stateStore.Commit(
            WindowsSandboxTarget.Default,
            new TargetState
            {
                SchemaVersion = 0,
                Revision = 0,
                TargetKind = WindowsSandboxTarget.Default.Kind,
                TargetId = WindowsSandboxTarget.Default.Id,
                InstanceId = InstanceId,
                BootNonce = "nonce",
                InstanceOrigin = origin,
                ClientWindowHandle = ClientWindow,
                ClientProcessId = clientProcessId,
                ClientProcessStartTicksUtc = ClientStartTicks,
                ClientOwnedByWinapp = ownedByWinapp,
            },
            _stateStore.Read(WindowsSandboxTarget.Default)?.Revision ?? 0);

    private Task<bool> RunAsync() =>
        _watcher.RunAsync(InstanceId, s_client, TestContext.CancellationToken);

    [TestMethod]
    [DataRow(nameof(SandboxInstanceOrigin.Created))]
    [DataRow(nameof(SandboxInstanceOrigin.RecoveredStart))]
    public async Task ClosingTheWindow_StopsASandboxWinappStarted(string origin)
    {
        Record(origin);

        Assert.IsTrue(await RunAsync());
        CollectionAssert.AreEqual(new[] { InstanceId }, _cli.Stopped);
        Assert.AreEqual(1, _ended, "The closed window's lingering process is ended, so no \"connection lost\" window appears.");
    }

    [TestMethod]
    public async Task ClosingTheWindow_NeverStopsASandboxWinappAdopted()
    {
        Record(nameof(SandboxInstanceOrigin.Adopted));

        Assert.IsFalse(await RunAsync());
        Assert.IsEmpty(_cli.Stopped);
    }

    [TestMethod]
    public async Task AWindowThatWasReplaced_DoesNotStopTheSandbox()
    {
        // Another winapp command recorded a newer window while this one was being watched.
        Record(nameof(SandboxInstanceOrigin.Created));
        _watcher.WaitForClientExitAsync = (_, _) =>
        {
            Record(nameof(SandboxInstanceOrigin.Created), clientProcessId: ClientProcessId + 1);
            return Task.CompletedTask;
        };

        Assert.IsFalse(await RunAsync());
        Assert.IsEmpty(_cli.Stopped);
    }

    [TestMethod]
    public async Task AnotherSandboxWindowStillOpen_DoesNotStopTheSandbox()
    {
        Record(nameof(SandboxInstanceOrigin.Created));
        _watcher.IsAnotherClientRunning = closed => closed == ClientProcessId;

        Assert.IsFalse(await RunAsync());
        Assert.IsEmpty(_cli.Stopped);
    }

    [TestMethod]
    public async Task AWindowWinappDidNotRecordOpening_IsNotWatched()
    {
        Record(nameof(SandboxInstanceOrigin.Created), ownedByWinapp: false);
        var waited = false;
        _watcher.WaitForClientExitAsync = (_, _) =>
        {
            waited = true;
            return Task.CompletedTask;
        };

        Assert.IsFalse(await RunAsync());
        Assert.IsFalse(waited, "A watcher with nothing to watch should exit at once.");
        Assert.IsEmpty(_cli.Stopped);
    }

    /// <summary>
    /// The Sandbox is not stopped underneath a winapp command that is connecting a new window to it.
    /// </summary>
    [TestMethod]
    public async Task ACommandConnectingAWindow_IsWaitedFor()
    {
        Record(nameof(SandboxInstanceOrigin.Created));
        var directories = new TargetStateDirectoryProvider(_root.FullName);
        var stopped = false;
        _watcher.WaitForClientExitAsync = (_, _) => Task.CompletedTask;

        Task<bool> run;
        using (new TargetConnectionLock(directories).TryAcquire(WindowsSandboxTarget.Default, TimeSpan.FromSeconds(5), TestContext.CancellationToken))
        {
            run = Task.Run(RunAsync, TestContext.CancellationToken);
            await Task.Delay(300, TestContext.CancellationToken);
            stopped = _cli.Stopped.Count > 0;

            // The connecting command records its new window before releasing the lock.
            Record(nameof(SandboxInstanceOrigin.Created), clientProcessId: ClientProcessId + 1);
        }

        Assert.IsFalse(stopped, "The Sandbox must not be stopped while another command holds the connection lock.");
        Assert.IsFalse(await run, "Once the newer window is recorded, closing the old one is not a reason to stop.");
        Assert.IsEmpty(_cli.Stopped);
    }

    [TestMethod]
    public void Arguments_IdentifyTheExactWindow()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                SandboxClientWatcher.Verb,
                "--instance-id", InstanceId,
                "--client-window", "4660",
                "--client-pid", "4321",
                "--client-start-ticks", "638900000000000000",
            },
            SandboxClientWatcher.Arguments(InstanceId, s_client).ToArray());
    }
}
