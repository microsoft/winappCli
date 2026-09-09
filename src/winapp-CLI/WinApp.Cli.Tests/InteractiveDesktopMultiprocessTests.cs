// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Tests;

/// <summary>
/// True multiprocess coverage of cooperative desktop turns (issue #764): a real second
/// <c>winapp.exe</c> queues behind a turn this test process holds, and the file protocol — leases,
/// <c>state.lock</c>, <c>active.lock</c> — is exercised across an OS process boundary.
/// </summary>
/// <remarks>
/// <para>
/// These cannot be simulated in one process: participant identity is <c>(pid, processStartTicks)</c>,
/// so two "owners" inside a single process would share one identity and one lease file. Only separate
/// processes exercise the real protocol.
/// </para>
/// <para>
/// Gated on <c>WINAPP_UI_MULTIPROCESS_TESTS=1</c> and a published <c>winapp.exe</c>, so the canonical
/// build does not depend on build artifacts being present.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize] // WINAPP_UI_LOCK_DIRECTORY is process-wide and the child inherits it.
[TestCategory("Interactive")]
[TestCategory("UiCoordination")]
public partial class InteractiveDesktopMultiprocessTests
{
    private const string GateVariable = "WINAPP_UI_MULTIPROCESS_TESTS";

    private string _lockDirectory = null!;
    private string? _previousLockOverride;
    private string? _previousOwnerId;
    private string _winappPath = null!;
    private InteractiveDesktopPaths _paths = null!;
    private ParticipantRegistry _participants = null!;
    private InteractiveDesktopStateStore _store = null!;
    private InteractiveDesktopLock _coordinator = null!;
    private readonly List<Process> _children = [];

    [TestInitialize]
    public void Setup()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(GateVariable), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                $"Set {GateVariable}=1 (and build the CLI) to run multiprocess UI coordination coverage.");
        }

        _winappPath = WinappTestBinary.Resolve();

        _lockDirectory = Path.Combine(Path.GetTempPath(), $"winapp-mp-{Guid.NewGuid():N}");
        _previousLockOverride = Environment.GetEnvironmentVariable(
            InteractiveDesktopPaths.LockDirectoryOverrideVariable);
        _previousOwnerId = Environment.GetEnvironmentVariable(UiOwnerResolver.WorkflowIdVariable);

        Environment.SetEnvironmentVariable(
            InteractiveDesktopPaths.LockDirectoryOverrideVariable, _lockDirectory);
        Environment.SetEnvironmentVariable(UiOwnerResolver.WorkflowIdVariable, "multiprocess-test-holder");

        var inspector = new ProcessInspector();
        _paths = new InteractiveDesktopPaths(inspector);
        _participants = new ParticipantRegistry(_paths, inspector, NullLogger<ParticipantRegistry>.Instance);
        _store = new InteractiveDesktopStateStore(
            _paths, _participants, new TickCountClock(), NullLogger<InteractiveDesktopStateStore>.Instance);
        _coordinator = new InteractiveDesktopLock(
            _store, _paths, _participants, new UiOwnerResolver(), inspector,
            new TickCountClock(), new FakePollDelay(),
            // Real named events: the whole point of these tests is that a wake-up crosses a process
            // boundary to a genuine winapp.exe child.
            new ParticipantSignals(inspector, NullLogger<ParticipantSignals>.Instance),
            new TestConsole(),
            NullLogger<InteractiveDesktopLock>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var child in _children)
        {
            try
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            child.Dispose();
        }

        Environment.SetEnvironmentVariable(
            InteractiveDesktopPaths.LockDirectoryOverrideVariable, _previousLockOverride);
        Environment.SetEnvironmentVariable(UiOwnerResolver.WorkflowIdVariable, _previousOwnerId);

        try
        {
            if (_lockDirectory is not null && Directory.Exists(_lockDirectory))
            {
                Directory.Delete(_lockDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leaked temp directory must never fail a test.
        }
    }

    /// <summary>
    /// Starts a real <c>winapp ui click</c> against a process that does not exist. Preflight passes
    /// (an app and a selector were supplied), so the command genuinely enters coordination, waits its
    /// turn, and only then fails to resolve the app — which is exactly the coordination behavior under
    /// test, without needing a live target window.
    /// </summary>
    private Process StartQueuedClick(string ownerId)
    {
        var startInfo = new ProcessStartInfo(_winappPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            ArgumentList = { "ui", "click", "some-selector", "-a", "winapp-no-such-app-zzz", "--json" },
        };
        startInfo.Environment[InteractiveDesktopPaths.LockDirectoryOverrideVariable] = _lockDirectory;
        startInfo.Environment[UiOwnerResolver.WorkflowIdVariable] = ownerId;
        startInfo.Environment["WINAPP_CLI_UPDATE_CHECK"] = "0";

        var child = Process.Start(startInfo)!;
        _children.Add(child);
        return child;
    }

    private InteractiveDesktopState ReadState()
    {
        using var stateLock = _store.AcquireStateLock(CancellationToken.None);
        return _store.Read().State!;
    }

    /// <summary>Waits until <paramref name="predicate"/> holds over the shared state, or times out.</summary>
    private async Task<bool> WaitForStateAsync(Func<InteractiveDesktopState, bool> predicate, int timeoutMs = 15_000)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < timeoutMs)
        {
            if (predicate(ReadState()))
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
    }

    [TestMethod]
    public async Task ASecondProcessQueuesBehindTheTurnAndProceedsWhenItIsReleased()
    {
        var holderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHolder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holder = _coordinator.RunCoordinatedAsync(
            UiTurnMode.DesktopExclusive, "ui click", UiCoordinationTestParse.Quiet(),
            async (_, _) =>
            {
                holderStarted.SetResult();
                await releaseHolder.Task;
                return 0;
            },
            CancellationToken.None);

        await holderStarted.Task;

        var child = StartQueuedClick("multiprocess-test-other");

        Assert.IsTrue(
            await WaitForStateAsync(s => s.Waiters.Count == 1),
            "the second process must register as a global waiter while another owner holds the turn");

        var waiterPid = ReadState().Waiters[0].Pid;
        Assert.AreEqual(child.Id, waiterPid, "the waiter must be the real child process");
        Assert.IsFalse(child.HasExited, "the child must still be waiting, not running");

        releaseHolder.SetResult();
        await holder;

        Assert.IsTrue(child.WaitForExit(30_000), "the child must proceed once the turn is released");

        // The app does not exist, so the command fails after acquiring its turn — the point is that it
        // got that far only after the holder finished.
        Assert.AreEqual(1, child.ExitCode);
        Assert.IsTrue(
            await WaitForStateAsync(s => s.Waiters.Count == 0 && s.OwnerCommands.Count == 0),
            "the child must remove its own entry on completion");
    }

    [TestMethod]
    public async Task KillingAQueuedProcessReleasesItsLeaseAndReclaimsItsTicket()
    {
        var holderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHolder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holder = _coordinator.RunCoordinatedAsync(
            UiTurnMode.DesktopExclusive, "ui click", UiCoordinationTestParse.Quiet(),
            async (_, _) =>
            {
                holderStarted.SetResult();
                await releaseHolder.Task;
                return 0;
            },
            CancellationToken.None);

        await holderStarted.Task;

        var child = StartQueuedClick("multiprocess-test-other");
        Assert.IsTrue(await WaitForStateAsync(s => s.Waiters.Count == 1));

        var leasePath = _paths.LeasePath(child.Id, child.StartTime.ToUniversalTime().Ticks);
        Assert.IsTrue(File.Exists(leasePath), "a queued participant must hold a lease file");

        // Forced termination is the documented recovery for a stuck process. Windows closes the handle,
        // which deletes the DeleteOnClose lease — that is what makes heartbeats unnecessary.
        child.Kill(entireProcessTree: true);
        Assert.IsTrue(child.WaitForExit(30_000));

        Assert.IsTrue(
            await WaitForStateAsync(_ => !File.Exists(leasePath)),
            "Windows must delete the participant lease when the holder is killed");

        releaseHolder.SetResult();
        await holder;

        // The next coordination pass prunes the dead waiter rather than queueing behind it forever.
        Assert.IsTrue(
            await WaitForStateAsync(s => s.Waiters.Count == 0),
            "a killed waiter's ticket must be reclaimed by the next coordinator");
    }

    [TestMethod]
    public async Task TwoProcessesNeverHoldTheDesktopSectionAtTheSameTime()
    {
        // Hold active.lock from this process for a beat, and prove the child cannot take it meanwhile.
        var sectionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holder = _coordinator.RunCoordinatedAsync(
            UiTurnMode.DesktopExclusive, "ui click", UiCoordinationTestParse.Quiet(),
            async (turn, ct) =>
            {
                await using (await turn.EnterAsync(ct))
                {
                    sectionEntered.SetResult();
                    await releaseSection.Task;
                }

                return 0;
            },
            CancellationToken.None);

        await sectionEntered.Task;
        Assert.IsFalse(_store.IsActiveLockFree(), "this process holds the desktop section");

        var child = StartQueuedClick("multiprocess-test-other");
        Assert.IsTrue(await WaitForStateAsync(s => s.Waiters.Count == 1));
        Assert.IsFalse(child.HasExited);
        Assert.IsFalse(_store.IsActiveLockFree(), "the child must not have taken active.lock");

        releaseSection.SetResult();
        await holder;
        Assert.IsTrue(child.WaitForExit(30_000));
    }

    [TestMethod]
    public async Task ASameOwnerProcessJoinsTheTurnInsteadOfQueueingGlobally()
    {
        var holderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHolder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holder = _coordinator.RunCoordinatedAsync(
            UiTurnMode.DesktopExclusive, "ui click", UiCoordinationTestParse.Quiet(),
            async (_, _) =>
            {
                holderStarted.SetResult();
                await releaseHolder.Task;
                return 0;
            },
            CancellationToken.None);

        await holderStarted.Task;

        // Same explicit workflow id as this test process, so the child is the *same* logical workflow.
        var child = StartQueuedClick("multiprocess-test-holder");

        Assert.IsTrue(
            await WaitForStateAsync(s => s.OwnerCommands.Count == 2),
            "a same-owner command joins the owner's command list rather than the global queue");
        Assert.AreEqual(0, ReadState().Waiters.Count);

        releaseHolder.SetResult();
        await holder;
        Assert.IsTrue(child.WaitForExit(30_000));
    }

    [TestMethod]
    public async Task AnObservationFromAnotherOwnerRunsConcurrentlyWithATurn()
    {
        var holderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHolder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holder = _coordinator.RunCoordinatedAsync(
            UiTurnMode.DesktopExclusive, "ui click", UiCoordinationTestParse.Quiet(),
            async (_, _) =>
            {
                holderStarted.SetResult();
                await releaseHolder.Task;
                return 0;
            },
            CancellationToken.None);

        await holderStarted.Task;

        // list-windows is an Observe command: it must not wait for anyone's turn.
        var startInfo = new ProcessStartInfo(_winappPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            ArgumentList = { "ui", "list-windows", "--json" },
        };
        startInfo.Environment[InteractiveDesktopPaths.LockDirectoryOverrideVariable] = _lockDirectory;
        startInfo.Environment[UiOwnerResolver.WorkflowIdVariable] = "multiprocess-test-observer";
        startInfo.Environment["WINAPP_CLI_UPDATE_CHECK"] = "0";

        using var observer = Process.Start(startInfo)!;

        // list-windows emits a large JSON payload. Drain both pipes concurrently — a child that fills
        // the pipe buffer while nobody reads it blocks on write, which would look like a coordination
        // hang rather than the test harness deadlock it actually is.
        var stdout = observer.StandardOutput.ReadToEndAsync();
        var stderr = observer.StandardError.ReadToEndAsync();

        Assert.IsTrue(observer.WaitForExit(30_000),
            "a non-owner observation must run immediately, not wait for the desktop");
        await Task.WhenAll(stdout, stderr);

        Assert.AreEqual(0, observer.ExitCode);
        Assert.AreEqual(0, ReadState().Waiters.Count, "an observation must never enter the queue");

        releaseHolder.SetResult();
        await holder;
    }

    [TestMethod]
    public async Task CorruptStateIsNotResetWhileAnotherProcessIsLive()
    {
        var holderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHolder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holder = _coordinator.RunCoordinatedAsync(
            UiTurnMode.DesktopExclusive, "ui click", UiCoordinationTestParse.Quiet(),
            async (_, _) =>
            {
                holderStarted.SetResult();
                await releaseHolder.Task;
                return 0;
            },
            CancellationToken.None);

        await holderStarted.Task;

        // Corrupt the shared document while this process is provably mid-workflow.
        using (var stateLock = _store.AcquireStateLock(CancellationToken.None))
        {
            File.WriteAllText(_paths.StatePath, "{ not json at all");
        }

        var child = StartQueuedClick("multiprocess-test-other");
        Assert.IsTrue(child.WaitForExit(30_000));

        var stderr = child.StandardError.ReadToEnd() + child.StandardOutput.ReadToEnd();
        Assert.AreEqual(1, child.ExitCode);
        StringAssert.Contains(stderr, UiCoordinationErrorCodes.Unavailable,
            "a mutating command must fail closed rather than rebuild state over a live participant");

        releaseHolder.SetResult();
        await holder;
    }

    [TestMethod]
    public async Task AnInvalidWorkflowIdIsRejectedByTheRealBinaryBeforeAnyWork()
    {
        var startInfo = new ProcessStartInfo(_winappPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            ArgumentList = { "ui", "click", "some-selector", "-a", "winapp-no-such-app-zzz", "--json" },
        };
        startInfo.Environment[InteractiveDesktopPaths.LockDirectoryOverrideVariable] = _lockDirectory;
        startInfo.Environment[UiOwnerResolver.WorkflowIdVariable] = "   ";
        startInfo.Environment["WINAPP_CLI_UPDATE_CHECK"] = "0";

        using var child = Process.Start(startInfo)!;
        Assert.IsTrue(child.WaitForExit(30_000));

        var output = child.StandardError.ReadToEnd() + child.StandardOutput.ReadToEnd();
        Assert.AreEqual(1, child.ExitCode);
        StringAssert.Contains(output, UiCoordinationErrorCodes.InvalidWorkflowId);
        Assert.IsFalse(_participants.AnyLiveParticipant(), "a rejected command must leave no lease behind");

        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task StateStaysReadableWhileAnotherProcessHoldsTheDesktopSection()
    {
        var sectionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holder = _coordinator.RunCoordinatedAsync(
            UiTurnMode.DesktopExclusive, "ui click", UiCoordinationTestParse.Quiet(),
            async (turn, ct) =>
            {
                await using (await turn.EnterAsync(ct))
                {
                    sectionEntered.SetResult();
                    await releaseSection.Task;
                }

                return 0;
            },
            CancellationToken.None);

        await sectionEntered.Task;

        // A queued command must still be able to read and update metadata while the desktop is busy,
        // otherwise nothing could ever join the queue.
        var child = StartQueuedClick("multiprocess-test-other");
        Assert.IsTrue(
            await WaitForStateAsync(s => s.Waiters.Count == 1),
            "state.lock must be independent of active.lock");

        var raw = File.ReadAllText(_paths.StatePath);
        using var document = JsonDocument.Parse(raw);
        Assert.AreEqual(1, document.RootElement.GetProperty("version").GetInt32());

        releaseSection.SetResult();
        await holder;
        Assert.IsTrue(child.WaitForExit(30_000));
    }
}

/// <summary>Minimal parse results for coordination tests that do not exercise a real command.</summary>
internal static class UiCoordinationTestParse
{
    public static System.CommandLine.ParseResult Quiet()
    {
        var command = new System.CommandLine.Command("probe");
        command.Options.Add(WinApp.Cli.Commands.WinAppRootCommand.JsonOption);
        command.Options.Add(WinApp.Cli.Commands.WinAppRootCommand.QuietOption);
        command.Options.Add(WinApp.Cli.Commands.WinAppRootCommand.VerboseOption);
        return command.Parse(["--quiet"]);
    }
}
