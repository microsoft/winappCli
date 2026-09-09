// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Tests;

/// <summary>
/// Real-app acceptance coverage for cooperative desktop turns (issue #764 §18.3).
/// </summary>
/// <remarks>
/// <para>
/// Unlike the scheduler and store suites, nothing here is faked. A real window (<see cref="UiaTestFixture"/>,
/// hosted on this test process's UI thread, with a genuine <c>MenuStrip</c> whose drop-down Windows
/// dismisses on foreground loss) plays the target app, and every agent is a separate real
/// <c>winapp.exe</c> process carrying its own <c>WINAPP_UI_WORKFLOW_ID</c>. The properties under test —
/// a transient menu surviving another agent's attempt to act, a reasoning gap handing the turn away,
/// and a recording pinning its owner — are only meaningful end to end, so they are asserted against
/// observable desktop state rather than scheduler internals.
/// </para>
/// <para>
/// Gated on <c>WINAPP_UI_MULTIPROCESS_TESTS=1</c> plus a published <c>winapp.exe</c>: these need an
/// interactive desktop and build artifacts, so the canonical build skips them.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize] // Drives the real foreground window and a process-wide lock-directory override.
[TestCategory("Interactive")]
[TestCategory("UiCoordination")]
public class InteractiveDesktopRealAppTests : IDisposable
{
    private const string GateVariable = "WINAPP_UI_MULTIPROCESS_TESTS";
    private const string OwnerA = "realapp-agent-a";
    private const string OwnerB = "realapp-agent-b";

    private string _lockDirectory = null!;
    private string? _previousLockOverride;
    private string? _previousOwnerId;
    private string _winappPath = null!;
    private string _scratchDirectory = null!;
    private UiaTestFixture _fixture = null!;
    private InteractiveDesktopStateStore _store = null!;
    private Stopwatch? _graceWatch;
    private readonly List<Process> _children = [];

    [TestInitialize]
    public void Setup()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(GateVariable), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                $"Set {GateVariable}=1 on an interactive desktop (and build the CLI) to run real-app UI coordination coverage.");
        }

        _winappPath = WinappTestBinary.Resolve();

        _lockDirectory = Path.Combine(Path.GetTempPath(), $"winapp-realapp-{Guid.NewGuid():N}");
        _scratchDirectory = Path.Combine(Path.GetTempPath(), $"winapp-realapp-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDirectory);

        _previousLockOverride = Environment.GetEnvironmentVariable(
            InteractiveDesktopPaths.LockDirectoryOverrideVariable);
        _previousOwnerId = Environment.GetEnvironmentVariable(UiOwnerResolver.WorkflowIdVariable);
        Environment.SetEnvironmentVariable(
            InteractiveDesktopPaths.LockDirectoryOverrideVariable, _lockDirectory);

        var inspector = new ProcessInspector();
        var paths = new InteractiveDesktopPaths(inspector);
        var participants = new ParticipantRegistry(paths, inspector, NullLogger<ParticipantRegistry>.Instance);
        _store = new InteractiveDesktopStateStore(
            paths, participants, new TickCountClock(), NullLogger<InteractiveDesktopStateStore>.Instance);

        _fixture = new UiaTestFixture();
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

        // An open drop-down holds a desktop-wide keyboard capture. Leaving one behind would silently
        // swallow input in the next test, so menu mode is exited before the window goes away.
        try
        {
            _fixture?.CloseFileMenu();
        }
        catch (InvalidOperationException)
        {
            // The UI thread is already gone; nothing to release.
        }

        _fixture?.Dispose();
        _fixture = null!;

        Environment.SetEnvironmentVariable(
            InteractiveDesktopPaths.LockDirectoryOverrideVariable, _previousLockOverride);
        Environment.SetEnvironmentVariable(UiOwnerResolver.WorkflowIdVariable, _previousOwnerId);

        foreach (var directory in new[] { _lockDirectory, _scratchDirectory })
        {
            try
            {
                if (directory is not null && Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leaked temp directory must never fail a test.
            }
        }
    }

    // ------------------------------------------------------------------ real winapp.exe agents

    private sealed record AgentRun(Process Process, Task<int> Completion, Task<string> Output);

    /// <summary>
    /// Launches a real <c>winapp.exe</c> as <paramref name="ownerId"/> against the fixture window.
    /// </summary>
    /// <remarks>
    /// Both pipes are drained concurrently: <c>ui</c> commands emit payloads large enough to fill the
    /// pipe buffer, and waiting for exit without reading would deadlock.
    /// </remarks>
    private AgentRun StartAgent(string ownerId, params string[] args)
    {
        var startInfo = new ProcessStartInfo(_winappPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment[InteractiveDesktopPaths.LockDirectoryOverrideVariable] = _lockDirectory;
        startInfo.Environment[UiOwnerResolver.WorkflowIdVariable] = ownerId;
        startInfo.Environment["WINAPP_CLI_UPDATE_CHECK"] = "0";

        var process = Process.Start(startInfo)!;
        _children.Add(process);

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var completion = Task.Run(async () =>
        {
            await process.WaitForExitAsync();
            return process.ExitCode;
        });
        var output = Task.Run(async () => await stdout + await stderr);

        return new AgentRun(process, completion, output);
    }

    private async Task<(int ExitCode, string Output)> RunAgentAsync(string ownerId, params string[] args)
    {
        var run = StartAgent(ownerId, args);
        return (await run.Completion, await run.Output);
    }

    /// <summary>Arguments that target the fixture window precisely (HWND beats process-name matching).</summary>
    private string[] TargetArgs => ["-w", _fixture.Hwnd.ToString(System.Globalization.CultureInfo.InvariantCulture)];

    private string[] WithTarget(params string[] args) => [.. args, .. TargetArgs, "--json"];

    private InteractiveDesktopState ReadState()
    {
        using var stateLock = _store.AcquireStateLock(CancellationToken.None);
        return _store.Read().State!;
    }

    /// <summary>
    /// The persisted owner key for an explicit owner id. Raw ids never reach disk — <c>state.json</c>
    /// stores only the domain-separated SHA-256 — so tests must compare against the hash.
    /// </summary>
    private static string KeyOf(string ownerId) => UiOwnerResolver.ComputeWorkflowKey(ownerId);

    private async Task<bool> WaitForStateAsync(Func<InteractiveDesktopState, bool> predicate, int timeoutMs = 20_000)
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

    /// <summary>
    /// Waits for <paramref name="predicate"/>, failing with the agent's own output if it exits first.
    /// </summary>
    /// <remarks>
    /// A child that dies during startup — a mistyped option, an unresolvable target — would otherwise
    /// surface only as an opaque "state never reached" timeout that says nothing about the real cause.
    /// </remarks>
    private async Task WaitForAgentStateAsync(
        AgentRun agent, Func<InteractiveDesktopState, bool> predicate, string because, int timeoutMs = 20_000)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < timeoutMs)
        {
            if (predicate(ReadState()))
            {
                return;
            }

            if (agent.Process.HasExited)
            {
                Assert.Fail(
                    $"{because}, but the agent exited early with code {agent.Process.ExitCode}. Output: {await agent.Output}");
            }

            await Task.Delay(50);
        }

        Assert.Fail($"{because}, but the state was never reached within {timeoutMs} ms.");
    }

    /// <summary>
    /// Has <paramref name="ownerId"/> open the File drop-down through a real exclusive command, so the
    /// turn and the transient UI are established by the same agent action.
    /// </summary>
    /// <remarks>
    /// Doing both in one command is not just convenient — it removes any window between "A owns the
    /// turn" and "A has transient UI on screen" in which the 4 s idle grace could lapse and quietly
    /// invalidate the test's premise. It is also what a real agent does.
    /// </remarks>
    private async Task OpenMenuAsOwnerAsync(string ownerId)
    {
        var (exitCode, output) = await RunAgentAsync(ownerId, WithTarget("ui", "invoke", "File Menu"));
        Assert.AreEqual(0, exitCode, $"the agent's menu-opening command should succeed. Output: {output}");
        _graceWatch = Stopwatch.StartNew();

        // UIA Invoke posts the click; the drop-down appears a moment later.
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < 3_000 && !_fixture.IsFileMenuOpen)
        {
            await Task.Delay(50);
        }

        Assert.IsTrue(
            _fixture.IsFileMenuOpen,
            "the agent's own command must have opened real transient UI for the test to be meaningful");
        Assert.AreEqual(
            KeyOf(ownerId), ReadState().Owner?.Key,
            "the agent must hold the turn immediately after its exclusive command completes");
    }

    // ------------------------------------------------------------------------------ §18.3 (a)

    /// <summary>
    /// §18.3(a): a tight burst by one owner keeps its transient UI intact while a different owner's
    /// mutation is held back.
    /// </summary>
    /// <remarks>
    /// The menu is opened by agent A's own coordinated command rather than directly on the fixture, so
    /// the turn and the transient UI are established by the same action and there is no window in
    /// which the premise could lapse. Agent B is a genuine separate process, so the foreground steal it
    /// would perform is real.
    /// </remarks>
    [TestMethod]
    public async Task ATightBurstKeepsTransientMenuOpenWhileAnotherOwnerWaits()
    {
        await OpenMenuAsOwnerAsync(OwnerA);

        // A different agent tries to act on the same desktop. Its click would take the foreground and
        // dismiss the drop-down, so coordination must hold it until A's burst is finished.
        var agentB = StartAgent(OwnerB, WithTarget("ui", "click", "btnInvoke"));

        await WaitForAgentStateAsync(
            agentB,
            s => s.Waiters.Count == 1 && s.Waiters[0].Pid == agentB.Process.Id,
            "agent B must queue behind agent A's turn instead of acting immediately");

        // A's burst: observations never yield the turn, and each one refreshes nothing that would let B in.
        for (var i = 0; i < 3; i++)
        {
            var (exitCode, output) = await RunAgentAsync(OwnerA, WithTarget("ui", "inspect"));
            Assert.AreEqual(0, exitCode, $"burst step {i} should succeed. Output: {output}");
            Assert.IsTrue(
                _fixture.IsFileMenuOpen,
                $"the transient menu must still be open after burst step {i}: another owner was allowed to interfere");
            Assert.IsFalse(agentB.Process.HasExited, "agent B must still be waiting during the burst");
        }

        Assert.IsTrue(_fixture.IsFileMenuOpen, "the burst must complete with the transient UI intact");

        // Releasing the turn lets B through, and its foreground steal dismisses the menu. Observing that
        // proves the earlier assertions were real protection rather than B simply being slow.
        Assert.AreEqual(0, await agentB.Completion, $"agent B should succeed once it gets the turn. Output: {await agentB.Output}");

        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < 5_000 && _fixture.IsFileMenuOpen)
        {
            await Task.Delay(100);
        }

        Assert.IsFalse(
            _fixture.IsFileMenuOpen,
            "agent B's click should have dismissed the menu once it ran, confirming it was genuinely blocked before");
    }

    // ------------------------------------------------------------------------------ §18.3 (b)

    /// <summary>
    /// §18.3(b): a reasoning gap longer than the idle grace hands the turn to a waiting owner, and the
    /// transient UI the first owner left behind does not survive — so its next step must replay.
    /// </summary>
    /// <remarks>
    /// The replay is itself a coordinated mutation, because that is the only thing a returning agent
    /// can actually do: by then the other owner holds the turn and its own grace, so the returning
    /// agent has to queue, wait, and reacquire before it can put its UI back. An observation cannot
    /// stand in for that step — a non-owner's Observe is detached by design, so it neither waits nor
    /// pins, and would leave the assertion below racing the other owner's grace.
    /// </remarks>
    [TestMethod]
    public async Task AReasoningGapHandsOverTheTurnAndForcesReplay()
    {
        await OpenMenuAsOwnerAsync(OwnerA);

        var agentB = StartAgent(OwnerB, WithTarget("ui", "click", "btnInvoke"));
        await WaitForAgentStateAsync(
            agentB,
            s => s.Waiters.Count == 1,
            "agent B must queue while agent A still holds the turn");

        // The reasoning gap: agent A issues nothing while its model thinks. The grace is not renewed,
        // so ownership legitimately transfers. The grace started when A's command completed, not when B
        // queued, so it is measured from there.
        Assert.AreEqual(0, await agentB.Completion, $"agent B must acquire the turn after the gap. Output: {await agentB.Output}");
        Assert.IsTrue(
            _graceWatch!.ElapsedMilliseconds >= InteractiveDesktopScheduler.IdleGraceMs - 500,
            $"handover must wait out the {InteractiveDesktopScheduler.IdleGraceMs} ms idle grace, "
                + $"but took {_graceWatch.ElapsedMilliseconds} ms from agent A's last command");

        Assert.IsTrue(
            await WaitForStateAsync(s => s.Owner?.Key == KeyOf(OwnerB), timeoutMs: 5_000),
            "the turn must transfer to agent B after agent A's grace expires");

        var menuGone = Stopwatch.StartNew();
        while (menuGone.ElapsedMilliseconds < 5_000 && _fixture.IsFileMenuOpen)
        {
            await Task.Delay(100);
        }

        Assert.IsFalse(
            _fixture.IsFileMenuOpen,
            "the transient UI must NOT survive the handover: this is exactly why an agent has to replay after a gap");

        // Agent A resumes and finds a different world. Its recovery must go through coordination the
        // same way its first step did: reopening the drop-down straight on the fixture would prove
        // nothing here, because it bypasses the arbitration this test exists to exercise. The command
        // below queues behind whatever agent B still holds, waits, reacquires the turn, and reopens the
        // menu — and OpenMenuAsOwnerAsync asserts both halves of that (menu back on screen, turn owned
        // by A again).
        await OpenMenuAsOwnerAsync(OwnerA);

        // Now that A owns the turn again its Observe pins rather than detaches, so the UI it just
        // restored is still standing afterwards. Running the same inspect while A was a non-owner
        // would have been detached — no ticket, no lease, nothing holding the desktop — which is why
        // the menu could not be expected to survive it before this point.
        var (replayExit, replayOutput) = await RunAgentAsync(OwnerA, WithTarget("ui", "inspect"));
        Assert.AreEqual(0, replayExit, $"agent A must be able to replay after the handover. Output: {replayOutput}");
        Assert.IsTrue(_fixture.IsFileMenuOpen, "agent A's restored transient UI must survive its own observation");
        Assert.AreEqual(
            KeyOf(OwnerA), ReadState().Owner?.Key,
            "agent A must still hold the turn it reacquired");
    }

    // ------------------------------------------------------------------------------ §18.3 (c)

    /// <summary>
    /// §18.3(c): a recording pins the turn to its owner. Same-owner input continues during the
    /// recording (a later exclusive command is not blocked by an earlier running shared one), while a
    /// different owner's mutation waits until the recording finishes.
    /// </summary>
    [TestMethod]
    public async Task RecordingPinsTheOwnerWhileSameOwnerInputContinuesAndAnotherOwnerWaits()
    {
        const int recordSeconds = 8;
        var outputPath = Path.Combine(_scratchDirectory, "pinned.mp4");

        // Explicit precondition: this test injects real keystrokes, so it must not inherit menu mode or
        // a stray foreground window from whatever ran before it.
        _fixture.CloseFileMenu();
        Assert.IsFalse(_fixture.IsFileMenuOpen, "no drop-down may be capturing keyboard input");

        var recorder = StartAgent(OwnerA, WithTarget(
            "ui", "record", "--duration-sec", recordSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-o", outputPath));

        await WaitForAgentStateAsync(
            recorder,
            s => s.Owner?.Key == KeyOf(OwnerA) && s.OwnerCommands.Any(
                c => c.Mode == UiTurnMode.TurnShared && c.Status == UiCommandStatus.Running),
            "the recording must register as a running shared command owned by agent A");

        // A different owner's mutation must not interleave with the recording.
        var agentB = StartAgent(OwnerB, WithTarget("ui", "click", "btnInvoke"));
        await WaitForAgentStateAsync(
            agentB,
            s => s.Waiters.Any(w => w.Pid == agentB.Process.Id),
            "agent B must queue behind the recording owner");

        // Same-owner input proceeds while the recording runs: an earlier running TurnShared command does
        // not block a later DesktopExclusive one from the same owner (§10.3).
        //
        // A UIA Invoke is used rather than synthetic keystrokes because its effect is observable
        // deterministically (the button handler sets the result box) and does not depend on desktop-wide
        // keyboard focus, which other tests in this class legitimately disturb. Keystroke ordering itself
        // is covered by the send-keys coverage in RealUiAutomationTests.
        var inputTimer = Stopwatch.StartNew();
        var (actionExit, actionOutput) = await RunAgentAsync(OwnerA, WithTarget("ui", "invoke", "Click Me"));
        inputTimer.Stop();

        Assert.AreEqual(0, actionExit, $"same-owner input must proceed during the recording. Output: {actionOutput}");
        Assert.IsFalse(recorder.Process.HasExited, "the recording must still be running when same-owner input completes");
        Assert.IsFalse(agentB.Process.HasExited, "agent B must still be waiting while the recording owner is active");

        Assert.IsTrue(
            inputTimer.ElapsedMilliseconds < recordSeconds * 1000,
            $"same-owner input waited {inputTimer.ElapsedMilliseconds} ms, which suggests it was blocked by the recording");

        // The control updates on the app's UI thread after the invoke is dispatched, so poll briefly
        // rather than sampling once and racing the message pump.
        var effectWatch = Stopwatch.StartNew();
        var result = _fixture.OnUiThread(() => _fixture.ResultBox.Text);
        while (effectWatch.ElapsedMilliseconds < 5_000 && result != "clicked")
        {
            await Task.Delay(100);
            result = _fixture.OnUiThread(() => _fixture.ResultBox.Text);
        }

        Assert.AreEqual("clicked", result,
            "the same-owner command must have really acted on the app while the recording was running");

        Assert.AreEqual(0, await recorder.Completion, $"the recording should succeed. Output: {await recorder.Output}");
        Assert.IsTrue(File.Exists(outputPath), "the recording must have produced its output file");

        Assert.AreEqual(0, await agentB.Completion, $"agent B should run once the recording releases the turn. Output: {await agentB.Output}");
        Assert.IsTrue(
            agentB.Process.ExitTime >= recorder.Process.ExitTime.AddMilliseconds(-250),
            "agent B must not have completed before the recording released the turn");
    }

    /// <summary>
    /// Backstop for the fixture window: <see cref="Cleanup"/> already disposes it after every test, so
    /// this only matters if the framework tears the class down without running cleanup.
    /// </summary>
    public void Dispose()
    {
        _fixture?.Dispose();
        _fixture = null!;
        GC.SuppressFinalize(this);
    }
}
