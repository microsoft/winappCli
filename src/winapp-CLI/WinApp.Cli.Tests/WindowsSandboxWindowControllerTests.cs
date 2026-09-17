// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

/// <summary>
/// How winapp decides which Windows Sandbox client window is the one it manages.
/// </summary>
/// <remarks>
/// The rules matter because Windows routinely leaves earlier <c>WindowsSandboxRemoteSession</c>
/// processes behind, and the user may have opened a Sandbox of their own. Choosing wrongly parks a
/// stranger's window off-screen, or captures someone else's desktop and reports it as this
/// target's — both of which look exactly like success.
/// </remarks>
[TestClass]
public class WindowsSandboxWindowControllerTests
{
    private const int OurLauncher = 5100;
    private const int OtherLauncher = 5200;

    /// <summary>When winapp's own <c>wsb connect</c> started.</summary>
    private const long LauncherStartTicks = 1_000_000;

    /// <summary>When a client winapp's launcher created started: after it, as a child must.</summary>
    private const long ClientStartTicks = LauncherStartTicks + 10_000;

    [TestMethod]
    public void SelectOwnedClient_TakesTheClientTheLauncherCreated()
    {
        var (client, ambiguous) = WindowsSandboxWindowController.SelectOwnedClient(
            Ownership(),
            [
                Candidate(11, 100, OtherLauncher),
                Candidate(12, 200, OurLauncher),
            ]);

        Assert.IsFalse(ambiguous);
        Assert.IsNotNull(client);
        Assert.AreEqual((nint)200, client.Handle);
        Assert.AreEqual(12, client.ProcessId);
    }

    [TestMethod]
    public void SelectOwnedClient_IgnoresHandlelessProcesses()
    {
        var (client, ambiguous) = WindowsSandboxWindowController.SelectOwnedClient(
            Ownership(),
            [Candidate(12, nint.Zero, OurLauncher)]);

        Assert.IsNull(client, "A process with no window yet is not something that can be parked.");
        Assert.IsFalse(ambiguous);
    }

    [TestMethod]
    public void SelectOwnedClient_IgnoresClientsWhoseParentWindowsWouldNotReport()
    {
        var (client, ambiguous) = WindowsSandboxWindowController.SelectOwnedClient(
            Ownership(),
            [Candidate(12, 200, parentProcessId: null)]);

        Assert.IsNull(client, "No parentage is no proof, and a guess is what this class exists to avoid.");
        Assert.IsFalse(ambiguous);
    }

    /// <summary>
    /// The case a timing rule gets wrong: another caller's client is the only one on the desktop,
    /// so "the new window that appeared" and "my window" are the same window to anything counting
    /// windows instead of reading parentage.
    /// </summary>
    [TestMethod]
    public void SelectOwnedClient_NeverTakesAnotherLaunchersOnlyClient()
    {
        var (client, ambiguous) = WindowsSandboxWindowController.SelectOwnedClient(
            Ownership(),
            [Candidate(12, 200, OtherLauncher)]);

        Assert.IsNull(client);
        Assert.IsFalse(ambiguous, "Someone else's window is not an ambiguity, it is simply not a candidate.");
    }

    [TestMethod]
    public void SelectOwnedClient_RefusesToGuessBetweenTwoClientsOfOneLauncher()
    {
        var (client, ambiguous) = WindowsSandboxWindowController.SelectOwnedClient(
            Ownership(),
            [
                Candidate(12, 200, OurLauncher),
                Candidate(13, 300, OurLauncher),
            ]);

        Assert.IsNull(client);
        Assert.IsTrue(ambiguous);
    }

    /// <summary>
    /// The trap a bare parent ID walks into. Windows stamps a client's parent ID once, when the
    /// client is created, and never revises it: the client outlives its launcher, that launcher's
    /// process ID is eventually handed to something else, and one day that something else is
    /// winapp's own <c>wsb connect</c>. From then on a window that has been sitting on the user's
    /// desktop for an hour claims, truthfully as far as Windows is concerned, to be winapp's child.
    /// It cannot be: it is older than the launcher it names.
    /// </summary>
    [TestMethod]
    public void SelectOwnedClient_IgnoresAClientOlderThanTheLauncherItNames()
    {
        var (client, ambiguous) = WindowsSandboxWindowController.SelectOwnedClient(
            Ownership(),
            [Candidate(12, 200, OurLauncher, startTicksUtc: LauncherStartTicks - 1)]);

        Assert.IsNull(client, "A child cannot predate the parent that created it.");
        Assert.IsFalse(ambiguous, "A stale parent ID is not an ambiguity, it is a non-match.");
    }

    /// <summary>
    /// Windows records process creation coarsely enough that a client started immediately can share
    /// its launcher's timestamp, so the test is "not older", not "strictly newer" — otherwise the
    /// rule would reject the very windows it exists to identify.
    /// </summary>
    [TestMethod]
    public void SelectOwnedClient_TakesAClientThatStartedInTheSameTickAsItsLauncher()
    {
        var (client, ambiguous) = WindowsSandboxWindowController.SelectOwnedClient(
            Ownership(),
            [Candidate(12, 200, OurLauncher, startTicksUtc: LauncherStartTicks)]);

        Assert.IsNotNull(client);
        Assert.AreEqual((nint)200, client.Handle);
        Assert.IsFalse(ambiguous);
    }

    [TestMethod]
    public void SelectOwnedClient_IgnoresClientsWhoseStartTimeWindowsWouldNotReport()
    {
        var (client, ambiguous) = WindowsSandboxWindowController.SelectOwnedClient(
            Ownership(),
            [Candidate(12, 200, OurLauncher, startTicksUtc: 0)]);

        Assert.IsNull(client, "An age that cannot be read cannot rule out a recycled parent ID.");
        Assert.IsFalse(ambiguous);
    }

    [TestMethod]
    public void ResolveClient_PrefersTheRecordedWindowWhileItIsStillOpen()
    {
        var recorded = Client(13, 300);

        var resolved = WindowsSandboxWindowController.ResolveClient(
            recorded,
            [Client(12, 200), recorded]);

        Assert.AreEqual(recorded, resolved);
    }

    [TestMethod]
    public void ResolveClient_IgnoresARecordedWindowThatIsNoLongerOpen()
    {
        // Windows recycles both handles and process IDs, so the recorded window is only the same
        // window while its start time matches too.
        var resolved = WindowsSandboxWindowController.ResolveClient(
            Client(13, 300, startTicksUtc: 1000),
            [Client(13, 300, startTicksUtc: 5000)]);

        Assert.AreEqual(5000, resolved.StartTicksUtc);
    }

    [TestMethod]
    public void ResolveClient_AdoptsTheOnlyOpenWindowWhenNothingWasRecorded()
    {
        var resolved = WindowsSandboxWindowController.ResolveClient(remembered: null, [Client(13, 300)]);

        Assert.AreEqual((nint)300, resolved.Handle);
    }

    [TestMethod]
    public void ResolveClient_FailsWhenNoClientWindowIsOpen()
    {
        var ex = Assert.ThrowsExactly<ExecutionTargetException>(
            () => WindowsSandboxWindowController.ResolveClient(remembered: null, []));

        Assert.AreEqual(ExecutionTargetErrorCodes.NoInteractiveSession, ex.Error.Code);
        Assert.IsNotNull(ex.Error.UserAction);
    }

    [TestMethod]
    public void ResolveClient_FailsWhenSeveralWindowsAreOpenAndNoneWasRecorded()
    {
        var ex = Assert.ThrowsExactly<ExecutionTargetException>(
            () => WindowsSandboxWindowController.ResolveClient(
                remembered: null,
                [Client(12, 200), Client(13, 300)]));

        Assert.AreEqual(ExecutionTargetErrorCodes.TargetAmbiguous, ex.Error.Code);
        Assert.IsNotNull(ex.Error.Context);
        Assert.AreEqual("12,13", ex.Error.Context["clientProcessIds"]);
    }

    [TestMethod]
    public void ResolveClient_FailsWhenTheRecordedWindowIsGoneAndOthersRemain()
    {
        var ex = Assert.ThrowsExactly<ExecutionTargetException>(
            () => WindowsSandboxWindowController.ResolveClient(
                Client(11, 100),
                [Client(12, 200), Client(13, 300)]));

        Assert.AreEqual(ExecutionTargetErrorCodes.TargetAmbiguous, ex.Error.Code);
    }

    [TestMethod]
    public void InspectClient_Minimized_ReadsStateWithoutMovingTheWindow()
    {
        var parked = 0;
        var controller = new WindowsSandboxWindowController(
            () => [Candidate(12, 200, OurLauncher)],
            (_, _) => parked++,
            _ => true,
            () => Snapshot(900).ForegroundWindow);

        var status = controller.InspectClient(Client(12, 200, ClientStartTicks));

        Assert.IsTrue(status.IsMinimized);
        Assert.AreEqual(0, parked, "Read-only inspection must not restore or reposition the client.");
    }

    [TestMethod]
    public void EnsureClientReady_Minimized_RestoresWithoutChangingForeground()
    {
        var minimized = true;
        var foreground = Snapshot(900).ForegroundWindow;
        var controller = new WindowsSandboxWindowController(
            () => [Candidate(12, 200, OurLauncher)],
            (_, _) => minimized = false,
            _ => minimized,
            () => foreground);

        var status = controller.EnsureClientReady(
            Client(12, 200, ClientStartTicks),
            TargetDesktopUse.RealInput);

        Assert.IsFalse(status.IsMinimized);
        Assert.AreEqual(Snapshot(900).ForegroundWindow, foreground);
    }

    [TestMethod]
    public void EnsureClientReady_RestoreRefused_FailsInsteadOfClaimingInputReadiness()
    {
        var controller = new WindowsSandboxWindowController(
            () => [Candidate(12, 200, OurLauncher)],
            (_, _) => { },
            _ => true,
            () => Snapshot(900).ForegroundWindow);

        var failure = Assert.ThrowsExactly<ExecutionTargetException>(() =>
            controller.EnsureClientReady(
                Client(12, 200, ClientStartTicks),
                TargetDesktopUse.RealInput));

        Assert.AreEqual(ExecutionTargetErrorCodes.InputNotReady, failure.Error.Code);
    }

    [TestMethod]
    public void EnsureClientReady_MinimizedAdoptedClientFailsWithoutMovingIt()
    {
        var parked = new List<nint>();
        var controller = new WindowsSandboxWindowController(
            () => [Candidate(12, 200, OurLauncher)],
            (client, _) => parked.Add(client.Handle),
            _ => true,
            () => Snapshot(900).ForegroundWindow);

        var failure = Assert.ThrowsExactly<ExecutionTargetException>(() =>
            controller.EnsureClientReady(
                remembered: null,
                TargetDesktopUse.PixelCapture));

        Assert.AreEqual(ExecutionTargetErrorCodes.ArtifactFailed, failure.Error.Code);
        Assert.AreEqual("True", failure.Error.Context!["adopted"]);
        Assert.AreEqual(0, parked.Count, "A manual client must never be moved off-screen.");
    }

    [TestMethod]
    public void EnsureClientReady_WindowReplacedDuringRestore_FailsCapture()
    {
        var replaced = false;
        var controller = new WindowsSandboxWindowController(
            () => replaced
                ? [Candidate(12, 200, OurLauncher, ClientStartTicks + 1)]
                : [Candidate(12, 200, OurLauncher)],
            (_, _) => replaced = true,
            _ => !replaced,
            () => Snapshot(900).ForegroundWindow);

        var failure = Assert.ThrowsExactly<ExecutionTargetException>(() =>
            controller.EnsureClientReady(
                Client(12, 200, ClientStartTicks),
                TargetDesktopUse.PixelCapture));

        Assert.AreEqual(ExecutionTargetErrorCodes.ArtifactFailed, failure.Error.Code);
        Assert.AreEqual("False", failure.Error.Context!["restored"]);
    }

    [TestMethod]
    public void EnsureClientReady_ForegroundChangedDuringRestore_FailsClosed()
    {
        var minimized = true;
        var foreground = Snapshot(900).ForegroundWindow;
        var controller = new WindowsSandboxWindowController(
            () => [Candidate(12, 200, OurLauncher)],
            (_, _) =>
            {
                minimized = false;
                foreground = Snapshot(200).ForegroundWindow;
            },
            _ => minimized,
            () => foreground);

        var failure = Assert.ThrowsExactly<ExecutionTargetException>(() =>
            controller.EnsureClientReady(
                Client(12, 200, ClientStartTicks),
                TargetDesktopUse.RealInput));

        Assert.AreEqual("False", failure.Error.Context!["foregroundPreserved"]);
    }

    // ---- claiming the window a connect created ---------------------------------------

    [TestMethod]
    public async Task PlaceConnectedClient_ParksTheClientItsOwnLauncherCreated()
    {
        var scripted = new ScriptedDesktop([]);
        scripted.Add([Candidate(12, 200, OurLauncher)]);
        var controller = scripted.CreateController();

        var placed = await controller.PlaceConnectedClientAsync(
            Snapshot(), Attempt(), TestContext.CancellationToken);

        Assert.IsNotNull(placed);
        Assert.AreEqual((nint)200, placed.Handle);
        CollectionAssert.AreEqual(new nint[] { 200 }, scripted.Parked);
    }

    [TestMethod]
    public async Task ObserveConnect_StartsExactOwnerPlacementBeforeConnectReturns()
    {
        var scripted = new ScriptedDesktop([Candidate(12, 200, OurLauncher)]);
        var controller = scripted.CreateController();
        var snapshot = Snapshot();

        var attempt = Attempt();
        controller.ObserveConnect(snapshot, attempt, TestContext.CancellationToken);

        Assert.IsNotNull(attempt.Placement);
        var placed = await attempt.Placement;
        Assert.AreEqual((nint)200, placed!.Handle);
        CollectionAssert.AreEqual(new nint[] { 200 }, scripted.Parked);
    }

    /// <summary>
    /// Two <c>wsb connect</c> calls milliseconds apart. The other caller's client is the one that
    /// appears first and, for several polls, the only window on the desktop. Anything that claims
    /// "the new window" — with or without a settling delay — parks and persists a stranger's client
    /// here; parentage does not, because it never considered that window a candidate.
    /// </summary>
    [TestMethod]
    public async Task PlaceConnectedClient_AnotherConnectsClientArrivesFirstAndAlone_IsNeverClaimed()
    {
        var scripted = new ScriptedDesktop([]);
        scripted.Add([Candidate(12, 200, OtherLauncher)]);
        scripted.Add([Candidate(12, 200, OtherLauncher)]);
        scripted.Add([Candidate(12, 200, OtherLauncher)]);
        scripted.Add([Candidate(12, 200, OtherLauncher), Candidate(13, 300, OurLauncher)]);
        var controller = scripted.CreateController();

        var placed = await controller.PlaceConnectedClientAsync(
            Snapshot(), Attempt(), TestContext.CancellationToken);

        Assert.IsNotNull(placed, "winapp's own client did arrive, and parentage identifies it.");
        Assert.AreEqual((nint)300, placed.Handle);
        CollectionAssert.AreEqual(
            new nint[] { 300 },
            scripted.Parked,
            "Another caller's window must never be moved off-screen.");
    }

    /// <summary>
    /// The same race, except winapp's own connect never produces a window. Waiting longer cannot
    /// make the stranger's client winapp's, so the run ends having claimed nothing.
    /// </summary>
    [TestMethod]
    public async Task PlaceConnectedClient_OnlyAnotherLaunchersClientEverAppears_ClaimsNothing()
    {
        var scripted = new ScriptedDesktop([Candidate(12, 200, OtherLauncher)]);
        var controller = scripted.CreateController();

        var placed = await controller.PlaceConnectedClientAsync(
            Snapshot(), Attempt(), TestContext.CancellationToken);

        Assert.IsNull(placed);
        Assert.AreEqual(0, scripted.Parked.Count);
    }

    [TestMethod]
    public async Task PlaceConnectedClient_LauncherCouldNotBeIdentified_ClaimsNothingWithoutLooking()
    {
        var scripted = new ScriptedDesktop([Candidate(12, 200, OurLauncher)]);
        var controller = scripted.CreateController();

        var placed = await controller.PlaceConnectedClientAsync(
            Snapshot(), SandboxConnectAttempt.Unidentified, TestContext.CancellationToken);

        Assert.IsNull(placed, "With no launcher there is no evidence, and winapp does not guess.");
        Assert.AreEqual(0, scripted.Parked.Count);
        Assert.AreEqual(0, scripted.Looks);
    }

    [TestMethod]
    public async Task PlaceConnectedClient_OneLauncherSomehowOwnsTwoWindows_ClaimsNothingImmediately()
    {
        var scripted = new ScriptedDesktop(
            [Candidate(12, 200, OurLauncher), Candidate(13, 300, OurLauncher)]);
        var controller = scripted.CreateController();

        Assert.IsNull(await controller.PlaceConnectedClientAsync(
            Snapshot(), Attempt(), TestContext.CancellationToken));
        Assert.AreEqual(0, scripted.Parked.Count);
        Assert.AreEqual(1, scripted.Looks, "The evidence is already lost, so there is nothing to wait for.");
    }

    /// <summary>
    /// The recycled-parent-ID case as it reaches the desktop: a Sandbox window the user opened long
    /// ago is sitting there when winapp connects, and Windows reports its parent as the very process
    /// ID winapp's launcher now has. Nothing about it may be touched.
    /// </summary>
    [TestMethod]
    public async Task PlaceConnectedClient_PreExistingClientWithARecycledParentId_IsNeverClaimed()
    {
        var scripted = new ScriptedDesktop(
            [Candidate(12, 200, OurLauncher, startTicksUtc: LauncherStartTicks - 60_000_000)]);
        var controller = scripted.CreateController();

        var placed = await controller.PlaceConnectedClientAsync(
            Snapshot(), Attempt(), TestContext.CancellationToken);

        Assert.IsNull(placed, "An older window cannot be the one this connect just created.");
        Assert.AreEqual(0, scripted.Parked.Count, "The user's own Sandbox window must not be moved.");
    }

    /// <summary>
    /// The same stale window, with winapp's real client arriving a few polls later. The stale one is
    /// never claimed even while it is the only window on the desktop, and the genuine one still is.
    /// </summary>
    [TestMethod]
    public async Task PlaceConnectedClient_StaleParentIdWindowIsSkippedAndTheRealClientIsStillFound()
    {
        var stale = Candidate(12, 200, OurLauncher, startTicksUtc: LauncherStartTicks - 60_000_000);
        var scripted = new ScriptedDesktop([stale]);
        scripted.Add([stale]);
        scripted.Add([stale, Candidate(13, 300, OurLauncher)]);
        var controller = scripted.CreateController();

        var placed = await controller.PlaceConnectedClientAsync(
            Snapshot(), Attempt(), TestContext.CancellationToken);

        Assert.IsNotNull(placed);
        Assert.AreEqual((nint)300, placed.Handle);
        CollectionAssert.AreEqual(new nint[] { 300 }, scripted.Parked);
    }

    [TestMethod]
    public async Task PlaceConnectedClient_NoWindowEverAppears_GivesUpWithoutClaimingAnything()
    {
        var scripted = new ScriptedDesktop([]);
        var controller = scripted.CreateController();

        Assert.IsNull(await controller.PlaceConnectedClientAsync(
            Snapshot(), Attempt(), TestContext.CancellationToken));
        Assert.AreEqual(0, scripted.Parked.Count);
        Assert.IsTrue(scripted.Looks > 1, "Waiting for a client that is still starting is expected.");
    }

    public TestContext TestContext { get; set; } = null!;

    private static WindowsSandboxWindowSnapshot Snapshot(nint foreground = 0) => new(new(foreground));

    private static SandboxConnectOwnership Ownership() => new(OurLauncher, LauncherStartTicks);

    private static SandboxConnectAttempt Attempt() =>
        SandboxConnectAttempt.ForLauncher(OurLauncher, LauncherStartTicks);

    private static SandboxClientWindow Client(int processId, nint handle, long startTicksUtc = 1000) =>
        new(handle, processId, startTicksUtc);

    private static SandboxClientCandidate Candidate(
        int processId,
        nint handle,
        int? parentProcessId,
        long startTicksUtc = ClientStartTicks) =>
        new(Client(processId, handle, startTicksUtc), parentProcessId);

    /// <summary>
    /// A desktop whose client windows change on a script, with time advancing only when the
    /// controller waits, so a race that lasts milliseconds in production is reproducible here.
    /// </summary>
    private sealed class ScriptedDesktop
    {
        private readonly List<IReadOnlyList<SandboxClientCandidate>> _looks = [];
        private DateTimeOffset _now = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public ScriptedDesktop(IReadOnlyList<SandboxClientCandidate> initial) => _looks.Add(initial);

        /// <summary>What the next look at the desktop returns; the last entry then repeats.</summary>
        public void Add(IReadOnlyList<SandboxClientCandidate> clients) => _looks.Add(clients);

        public List<nint> Parked { get; } = [];

        public int Looks { get; private set; }

        public WindowsSandboxWindowController CreateController()
        {
            var controller = new WindowsSandboxWindowController(
                Look,
                (client, _) => Parked.Add(client.Handle))
            {
                UtcNow = () => _now,
            };

            controller.Delay = (delay, _) =>
            {
                _now += delay;
                return Task.CompletedTask;
            };

            return controller;
        }

        private IReadOnlyList<SandboxClientCandidate> Look()
        {
            var index = Math.Min(Looks, _looks.Count - 1);
            Looks++;
            return _looks[index];
        }
    }
}
