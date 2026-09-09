// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using Spectre.Console.Testing;
using WinApp.Cli.Commands;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// The three <c>winapp target</c> verbs that report on, photograph, and film a target's own desktop.
/// </summary>
/// <remarks>
/// What is being checked throughout is the boundary: a target that draws no desktop on this machine
/// says so instead of capturing something else, a capture never foregrounds the window it captures,
/// and <c>--json</c> keeps stdout to one parsable document.
/// </remarks>
[TestClass]
[DoNotParallelize]
public class TargetCaptureCommandTests
{
    private const nint DesktopHwnd = 0x1234;
    private const int DesktopProcessId = 7788;

    private static readonly ExecutionTargetEpoch Epoch = ExecutionTargetEpoch.Create("sandbox-1", "nonce-a");

    private string _root = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Setup() => _root = TestPaths.TempRoot(nameof(TargetCaptureCommandTests));

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    // ---- snapshot ------------------------------------------------------------------

    [TestMethod]
    public async Task Snapshot_ReportsReadinessTheDesktopWindowAndWhatIsOnIt()
    {
        await using var harness = new Harness(GuestWindows(Window(0x20, "Calculator", 800, 600)));
        var console = new TestConsole();

        var exitCode = await RunSnapshotAsync(harness, console, "sandbox");

        Assert.AreEqual(0, exitCode);

        var report = console.Output;
        StringAssert.Contains(report, "real input yes");
        StringAssert.Contains(report, $"HWND {DesktopHwnd}");
        StringAssert.Contains(report, "Calculator");
    }

    /// <summary>
    /// The whole point of a report is that it describes what was already there. A snapshot that
    /// prepared the target would create the Sandbox it then cheerfully reports as running, and the
    /// caller asking "is one up?" would be told yes because it asked.
    /// </summary>
    [TestMethod]
    public async Task Snapshot_NeverPreparesTheTarget()
    {
        await using var harness = new Harness(GuestWindows());

        Assert.AreEqual(0, await RunSnapshotAsync(harness, new TestConsole(), "sandbox"));

        Assert.AreEqual(0, harness.Backend.EnsureCalls, "A snapshot must never create, connect, or repair.");
        Assert.AreEqual(1, harness.Backend.AttachCalls);
    }

    /// <summary>
    /// Resolving the desktop is the other half of the same promise: the path that records what it
    /// found exists for captures, which have a next command to agree with. A report has none, and
    /// writing one would make every poll a new revision of the file it is describing.
    /// </summary>
    [TestMethod]
    public async Task Snapshot_ReadsTheDesktopThroughTheInspectionPathThatRecordsNothing()
    {
        await using var harness = new Harness(GuestWindows());

        Assert.AreEqual(0, await RunSnapshotAsync(harness, new TestConsole(), "sandbox"));

        Assert.AreEqual(1, harness.Rendering.InspectSurfaceCalls);
        Assert.AreEqual(0, harness.Rendering.ResolveSurfaceCalls, "Inspection must not take the writing path.");
    }

    /// <summary>
    /// Guest window titles are chosen by software the caller is deliberately testing. A terminal
    /// reads escape sequences in them as instructions, so a title printed verbatim could erase the
    /// rest of the report or retitle the user's terminal.
    /// </summary>
    [TestMethod]
    public async Task Snapshot_GuestWindowTitleContainingTerminalControls_IsPrintedInert()
    {
        await using var harness = new Harness(GuestWindows(
            Window(0x20, "\u001b]0;pwned\u0007Setup\u001b[2J\u001b[H", 800, 600)));
        var console = new TestConsole();

        Assert.AreEqual(0, await RunSnapshotAsync(harness, console, "sandbox"));

        StringAssert.Contains(console.Output, "Setup");
        Assert.IsFalse(console.Output.Contains('\u001b'), "No escape may reach the terminal.");
        Assert.IsFalse(console.Output.Contains("pwned", StringComparison.Ordinal), "Nor may its payload.");
    }

    [TestMethod]
    public async Task Snapshot_MultiLineGuestWindowTitle_StaysOnOneRow()
    {
        await using var harness = new Harness(GuestWindows(
            Window(0x20, "Real title\r\nWindows: 0 running", 800, 600)));
        var console = new TestConsole();

        Assert.AreEqual(0, await RunSnapshotAsync(harness, console, "sandbox"));

        StringAssert.Contains(console.Output, "Real title\u21b5Windows: 0 running");
    }

    /// <summary>
    /// JSON is data rather than instructions, and a caller diffing titles has to see exactly what
    /// the guest reported. The sanitizing belongs to rendering, not to the value.
    /// </summary>
    [TestMethod]
    public async Task Snapshot_Json_KeepsTheGuestsTitleExactlyAsItWasReported()
    {
        const string Title = "\u001b]0;pwned\u0007Setup";
        await using var harness = new Harness(GuestWindows(Window(0x20, Title, 800, 600)));
        var console = new TestConsole();

        Assert.AreEqual(0, await RunSnapshotAsync(harness, console, "sandbox", "--json"));

        Assert.AreEqual(Title, Deserialize(console.Output).Windows![0].Title);
    }

    [TestMethod]
    public async Task Snapshot_NothingRunning_SaysSoAndSucceeds()
    {
        await using var harness = new Harness(GuestWindows());
        harness.Backend.Running = false;
        var console = new TestConsole();

        Assert.AreEqual(0, await RunSnapshotAsync(harness, console, "sandbox"));

        StringAssert.Contains(console.Output, "not running");
        StringAssert.Contains(console.Output, "winapp run . --on sandbox");
        Assert.AreEqual(0, harness.Backend.EnsureCalls);
    }

    [TestMethod]
    public async Task Snapshot_NothingRunning_Json_ReportsItWithoutAnEpochOrCapabilities()
    {
        await using var harness = new Harness(GuestWindows());
        harness.Backend.Running = false;
        var console = new TestConsole();

        Assert.AreEqual(0, await RunSnapshotAsync(harness, console, "sandbox", "--json"));

        var output = Deserialize(console.Output);

        Assert.IsFalse(output.Running);
        Assert.IsFalse(output.Attached);
        Assert.IsNull(output.Capabilities);
        Assert.IsNull(output.ExecutionTarget.Epoch);
        Assert.IsFalse(output.Desktop.Rendered);
        Assert.AreEqual(0, output.Deployments.Length);
        Assert.IsNull(output.Windows);
    }

    /// <summary>
    /// A running instance whose agent is not answering is a state a caller needs reported, not
    /// repaired: repairing it is what <c>winapp run</c> does, and doing it here would replace the
    /// agent the caller was asking about.
    /// </summary>
    [TestMethod]
    public async Task Snapshot_RunningButAgentSilent_ReportsWhatTheHostKnowsAndDoesNotRepair()
    {
        await using var harness = new Harness(GuestWindows());
        harness.Backend.AgentAnswers = false;
        var console = new TestConsole();

        Assert.AreEqual(0, await RunSnapshotAsync(harness, console, "sandbox", "--json"));

        var output = Deserialize(console.Output);

        Assert.IsTrue(output.Running);
        Assert.IsFalse(output.Attached);
        Assert.IsNull(output.Capabilities);
        Assert.IsTrue(output.Desktop.Rendered, "The client window is a host-side fact, so it is still reported.");
        Assert.IsNull(output.Windows);
        Assert.AreEqual(0, harness.Backend.EnsureCalls);
    }

    [TestMethod]
    public async Task Snapshot_Json_IsOneDocumentDescribingTheTargetAndItsWindows()
    {
        await using var harness = new Harness(GuestWindows(
            Window(0x20, "Calculator", 800, 600),
            Window(0x21, "Settings", 400, 300, foreground: true)));
        var console = new TestConsole();

        var exitCode = await RunSnapshotAsync(harness, console, "sandbox", "--json");

        Assert.AreEqual(0, exitCode);

        var output = Deserialize(console.Output);

        Assert.AreEqual("sandbox", output.ExecutionTarget.Kind);
        Assert.AreEqual(Epoch.Value, output.ExecutionTarget.Epoch);
        Assert.IsTrue(output.Capabilities!.SupportsRealInput);
        Assert.IsTrue(output.Desktop.Rendered);
        Assert.AreEqual(DesktopHwnd, output.Desktop.WindowHandle);
        Assert.AreEqual(DesktopProcessId, output.Desktop.ProcessId);
        Assert.AreEqual(2, output.WindowCount);
        Assert.IsFalse(output.WindowsTruncated);

        // The window the user is actually looking at leads, because it is the one a caller acts on.
        Assert.AreEqual("Settings", output.Windows![0].Title);
    }

    [TestMethod]
    public async Task Snapshot_MinimizedClient_SeparatesGuestSupportFromEffectiveReadiness()
    {
        await using var harness = new Harness(GuestWindows());
        harness.Rendering.Minimized = true;
        var console = new TestConsole();

        Assert.AreEqual(0, await RunSnapshotAsync(harness, console, "sandbox", "--json"));

        var output = Deserialize(console.Output);
        Assert.IsTrue(output.Capabilities!.SupportsRealInput, "Guest capability remains true.");
        Assert.IsTrue(output.Capabilities.SupportsScreenCapture, "Guest capability remains true.");
        Assert.IsTrue(output.Desktop.Minimized);
        Assert.IsFalse(output.Desktop.EffectiveInputReady);
        Assert.IsFalse(output.Desktop.EffectiveCaptureReady);
    }

    [TestMethod]
    public async Task Snapshot_PackagedLaunchReportsValidatedLauncherRatherThanAppPid()
    {
        var appLauncher = new FakeAppLauncherService
        {
            FakePackageFullName = "Contoso.MyApp_1.0.0.0_x64__abc",
            FakeRegisteredLocation = @"C:\WinApp\layouts\packaged",
        };
        await using var harness = new Harness(GuestWindows(), appLauncher: appLauncher);
        using var process = Process.GetCurrentProcess();
        var store = new MutableDeploymentStateStore(State(
            "packaged",
            process.Id,
            process.StartTime.ToUniversalTime().Ticks,
            packaged: true));
        var console = new TestConsole();

        Assert.AreEqual(
            0,
            await RunSnapshotAsync(harness, console, store, "sandbox", "--json"));

        var deployment = Deserialize(console.Output).Deployments.Single();
        Assert.AreEqual("package-launcher", deployment.TrackedOperationKind);
        Assert.AreEqual("running", deployment.TrackedOperationStatus);
        Assert.AreEqual(process.Id, deployment.TrackedOperationProcessId);
        Assert.AreEqual("registered", deployment.RegistrationStatus);

        using var json = JsonDocument.Parse(console.Output);
        var raw = json.RootElement.GetProperty("deployments")[0];
        Assert.IsFalse(raw.TryGetProperty("processId", out _), "No field may imply this is the UI process.");
        Assert.AreEqual(
            process.Id,
            raw.GetProperty("trackedOperationProcessId").GetInt32());
    }

    [TestMethod]
    public async Task Snapshot_ExitedOrReusedTrackedOperationOmitsTheStalePid()
    {
        await using var harness = new Harness(GuestWindows());
        using var process = Process.GetCurrentProcess();
        var store = new MutableDeploymentStateStore(State(
            "unpackaged",
            process.Id,
            process.StartTime.ToUniversalTime().Ticks - 1,
            packaged: false));
        var console = new TestConsole();

        Assert.AreEqual(
            0,
            await RunSnapshotAsync(harness, console, store, "sandbox", "--json"));

        var deployment = Deserialize(console.Output).Deployments.Single();
        Assert.AreEqual("application", deployment.TrackedOperationKind);
        Assert.AreEqual("exited", deployment.TrackedOperationStatus);
        Assert.IsNull(deployment.TrackedOperationProcessId);
        Assert.IsTrue(deployment.RetainedLayout);
    }

    [TestMethod]
    public async Task Snapshot_LiteralV1ProcessFieldsAreValidated()
    {
        await using var harness = new Harness(GuestWindows());
        using var process = Process.GetCurrentProcess();
        var stateRoot = TestPaths.Under(_root, "legacy-state");
        var stateDirectory = TestPaths.Under(
            stateRoot,
            WindowsSandboxTarget.Default.StateKey,
            DeploymentStateStore.DeploymentsFolder);
        Directory.CreateDirectory(stateDirectory);
        await File.WriteAllTextAsync(
            TestPaths.Under(stateDirectory, "legacy-process.json"),
            $$"""
            {
              "schemaVersion": 1,
              "revision": 2,
              "deploymentId": "legacy-process",
              "targetEpoch": "{{Epoch.Value}}",
              "dirty": false,
              "desired": [],
              "processId": {{process.Id}},
              "processStartTicksUtc": {{process.StartTime.ToUniversalTime().Ticks}}
            }
            """,
            TestContext.CancellationToken);
        var store = new DeploymentStateStore(new FixedStateDirectoryProvider(stateRoot));
        var console = new TestConsole();

        Assert.AreEqual(0, await RunSnapshotAsync(harness, console, store, "sandbox", "--json"));

        var deployment = Deserialize(console.Output).Deployments.Single();
        Assert.AreEqual("running", deployment.TrackedOperationStatus);
        Assert.AreEqual(process.Id, deployment.TrackedOperationProcessId);
    }

    [TestMethod]
    public async Task Snapshot_UnregisteredPackagedHistoryRemainsPackaged()
    {
        await using var harness = new Harness(GuestWindows());
        var state = State("packaged-history", null, null, packaged: false) with
        {
            WasPackaged = true,
        };
        var console = new TestConsole();

        Assert.AreEqual(
            0,
            await RunSnapshotAsync(
                harness,
                console,
                new MutableDeploymentStateStore(state),
                "sandbox",
                "--json"));

        var deployment = Deserialize(console.Output).Deployments.Single();
        Assert.AreEqual("packaged", deployment.Kind);
        Assert.IsTrue(deployment.RetainedLayout);
    }

    [TestMethod]
    public async Task Snapshot_UnavailableGuestDoesNotCallUnknownStateRetainedLayout()
    {
        await using var harness = new Harness(GuestWindows());
        harness.Backend.AgentAnswers = false;
        using var process = Process.GetCurrentProcess();
        var store = new MutableDeploymentStateStore(State(
            "unknown",
            process.Id,
            process.StartTime.ToUniversalTime().Ticks,
            packaged: true));
        var console = new TestConsole();

        Assert.AreEqual(0, await RunSnapshotAsync(harness, console, store, "sandbox", "--json"));

        var deployment = Deserialize(console.Output).Deployments.Single();
        Assert.AreEqual("unknown", deployment.RegistrationStatus);
        Assert.AreEqual("unknown", deployment.TrackedOperationStatus);
        Assert.IsFalse(deployment.RetainedLayout);
    }

    [TestMethod]
    public async Task Snapshot_ConcurrentDeploymentChangeOmitsTheRacedRecord()
    {
        await using var harness = new Harness(GuestWindows());
        using var process = Process.GetCurrentProcess();
        var store = new MutableDeploymentStateStore(
            State("changing", process.Id, process.StartTime.ToUniversalTime().Ticks, packaged: false))
        {
            AdvanceRevisionOnRead = true,
        };

        var console = new TestConsole();
        Assert.AreEqual(0, await RunSnapshotAsync(harness, console, store, "sandbox", "--json"));

        Assert.HasCount(0, Deserialize(console.Output).Deployments);
    }

    [TestMethod]
    public async Task Snapshot_HumanOutputCallsCleanInactiveStateARetainedLayout()
    {
        await using var harness = new Harness(GuestWindows());
        var store = new MutableDeploymentStateStore(State(
            "history-only",
            processId: null,
            startTicksUtc: null,
            packaged: false));
        var console = new TestConsole();

        Assert.AreEqual(0, await RunSnapshotAsync(harness, console, store, "sandbox"));

        StringAssert.Contains(console.Output, "history-only (retained layout)");
    }

    [TestMethod]
    public async Task RoutedReadOnlyVerb_DoesNotInspectOrRestoreTheHostClient()
    {
        await using var harness = new Harness(GuestWindows());
        var router = new ExecutionTargetUiRouter(harness.Orchestrator, new TestConsole());

        Assert.AreEqual(
            0,
            await router.RouteAsync(
                ["ui", "status", "--on", "sandbox"],
                TargetUiRequirements.ReadOnly,
                isJson: false,
                TestContext.CancellationToken));

        Assert.AreEqual(0, harness.Rendering.ResolveSurfaceCalls);
        Assert.AreEqual(0, harness.Rendering.InspectSurfaceCalls);
    }

    [TestMethod]
    public async Task RoutedInputVerb_RechecksHostClientImmediatelyBeforeExecution()
    {
        await using var harness = new Harness(GuestWindows());
        var router = new ExecutionTargetUiRouter(harness.Orchestrator, new TestConsole());

        Assert.AreEqual(
            0,
            await router.RouteAsync(
                ["ui", "send-keys", "--on", "sandbox", "-a", "App", "--", "hello"],
                TargetUiRequirements.Interactive,
                isJson: false,
                TestContext.CancellationToken));

        CollectionAssert.AreEqual(
            new[] { TargetDesktopUse.RealInput },
            harness.Rendering.ResolvedUses);
    }

    [TestMethod]
    public async Task Snapshot_ManyGuestWindows_ReportsTheTotalAndSaysItListedFewer()
    {
        var windows = Enumerable.Range(0, TargetSnapshotCommand.MaxWindows + 10)
            .Select(index => Window(0x100 + index, $"Window {index}", 100 + index, 100))
            .ToArray();

        await using var harness = new Harness(GuestWindows(windows));
        var console = new TestConsole();

        Assert.AreEqual(0, await RunSnapshotAsync(harness, console, "sandbox", "--json"));

        var output = Deserialize(console.Output);

        Assert.AreEqual(windows.Length, output.WindowCount);
        Assert.AreEqual(TargetSnapshotCommand.MaxWindows, output.Windows!.Length);
        Assert.IsTrue(output.WindowsTruncated);
    }

    /// <summary>
    /// The window list is the one part of a snapshot that needs a live desktop. Losing it must not
    /// cost the caller the readiness and deployment facts that explain why it is missing.
    /// </summary>
    [TestMethod]
    public async Task Snapshot_GuestCannotListItsWindows_StillReportsEverythingElse()
    {
        await using var harness = new Harness(stdout: "", exitCode: 1);
        var console = new TestConsole();

        Assert.AreEqual(0, await RunSnapshotAsync(harness, console, "sandbox", "--json"));

        var output = Deserialize(console.Output);

        Assert.IsNull(output.Windows);
        Assert.IsTrue(output.Desktop.Rendered);
        Assert.IsTrue(output.Capabilities!.SupportsRealInput);
    }

    [TestMethod]
    public async Task Snapshot_TargetThatDrawsNoDesktopHere_SaysSoInsteadOfFailing()
    {
        await using var harness = new Harness(GuestWindows(), rendersDesktop: false);
        var console = new TestConsole();

        Assert.AreEqual(0, await RunSnapshotAsync(harness, console, "sandbox", "--json"));

        var output = Deserialize(console.Output);

        Assert.IsFalse(output.Desktop.Rendered);
        Assert.AreEqual(ExecutionTargetErrorCodes.Unsupported, output.Desktop.Unavailable);
    }

    [TestMethod]
    public async Task Snapshot_UnknownTarget_IsRefusedBeforeTheTargetIsTouched()
    {
        await using var harness = new Harness(GuestWindows());
        var console = new TestConsole();

        Assert.AreEqual(
            TargetOutput.InvalidCommandLineExitCode,
            await RunSnapshotAsync(harness, console, "vm"));

        Assert.AreEqual(string.Empty, console.Output);
        Assert.AreEqual(0, harness.Backend.EnsureCalls);
    }

    // ---- screenshot ----------------------------------------------------------------

    [TestMethod]
    public async Task Screenshot_WritesAPngAndReportsWhichTargetItCameFrom()
    {
        await using var harness = new Harness(GuestWindows());
        var console = new TestConsole();
        var capture = new FakeWindowCapture
        {
            CaptureWithoutActivationOverride = _ => (new byte[4 * 3 * 2], 3, 2),
        };
        var destination = TestPaths.Under(_root, "shots", "desktop.png");

        var exitCode = await RunScreenshotAsync(
            harness, console, capture, "sandbox", "-o", destination, "--json");

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(File.Exists(destination));

        var payload = JsonSerializer.Deserialize(
            console.Output.Trim(), UiJsonContext.Default.UiScreenshotResult)!;

        Assert.AreEqual(destination, payload.FilePath);
        Assert.AreEqual(3, payload.Width);
        Assert.AreEqual(2, payload.Height);
        Assert.AreEqual(DesktopHwnd, payload.Hwnd);
        Assert.AreEqual(DesktopProcessId, payload.ProcessId);
        Assert.AreEqual(Epoch.Value, payload.ExecutionTarget!.Epoch);
    }

    /// <summary>
    /// A managed client window is parked off-screen on purpose. The ordinary screenshot path
    /// recovers from a blank frame by foregrounding the window and trying again, which would drag
    /// the target's window back onto the user's screen mid-command.
    /// </summary>
    [TestMethod]
    public async Task Screenshot_CapturesTheDesktopWindowThroughTheNoActivationPathOnly()
    {
        await using var harness = new Harness(GuestWindows());
        var capture = new FakeWindowCapture();

        Assert.AreEqual(
            0,
            await RunScreenshotAsync(
                harness,
                new TestConsole(),
                capture,
                "sandbox",
                "-o",
                TestPaths.Under(_root, "desktop.png")));

        CollectionAssert.AreEqual(new[] { DesktopHwnd }, capture.CapturedWithoutActivation);
    }

    /// <summary>
    /// The command promises it takes no focus. When the only way left to get pixels would be to
    /// bring the window to the front, the honest outcome is to fail and say so.
    /// </summary>
    [TestMethod]
    public async Task Screenshot_WindowCannotBeCapturedWhereItSits_FailsInsteadOfForegroundingIt()
    {
        await using var harness = new Harness(GuestWindows());
        var console = new TestConsole();
        var destination = TestPaths.Under(_root, "desktop.png");
        var capture = new FakeWindowCapture { CaptureWithoutActivationOverride = _ => null };

        var (exitCode, stderr) = await CaptureStandardErrorAsync(() => RunScreenshotAsync(
            harness, console, capture, "sandbox", "-o", destination, "--json"));

        Assert.AreEqual(TargetOutput.TargetInfrastructureExitCode, exitCode);
        Assert.AreEqual(string.Empty, console.Output, "A failure must not put anything on stdout under --json.");
        StringAssert.Contains(stderr, ExecutionTargetErrorCodes.ArtifactFailed);
        StringAssert.Contains(stderr, "without bringing its window to the front");
        Assert.IsFalse(File.Exists(destination), "Nothing was captured, so nothing is published.");
    }

    [TestMethod]
    public async Task Screenshot_TargetThatDrawsNoDesktopHere_FailsAndPointsAtTheGuestSideVerb()
    {
        await using var harness = new Harness(GuestWindows(), rendersDesktop: false);
        var console = new TestConsole();
        var destination = TestPaths.Under(_root, "desktop.png");

        var (exitCode, stderr) = await CaptureStandardErrorAsync(() => RunScreenshotAsync(
            harness, console, new FakeWindowCapture(), "sandbox", "-o", destination, "--json"));

        Assert.AreEqual(TargetOutput.TargetInfrastructureExitCode, exitCode);
        Assert.AreEqual(string.Empty, console.Output, "A failure must not put anything on stdout under --json.");
        StringAssert.Contains(stderr, ExecutionTargetErrorCodes.Unsupported);
        StringAssert.Contains(stderr, "winapp ui screenshot");
        Assert.IsFalse(File.Exists(destination));
    }

    [TestMethod]
    public async Task Screenshot_UnknownTarget_IsRefusedBeforeAnythingIsCaptured()
    {
        await using var harness = new Harness(GuestWindows());
        var capture = new FakeWindowCapture();

        Assert.AreEqual(
            TargetOutput.InvalidCommandLineExitCode,
            await RunScreenshotAsync(
                harness, new TestConsole(), capture, "vm", "-o", TestPaths.Under(_root, "desktop.png")));

        Assert.AreEqual(0, capture.CapturedWithoutActivation.Count);
        Assert.AreEqual(0, harness.Backend.EnsureCalls);
    }

    /// <summary>
    /// The destination is often the previous screenshot of the same target, and the reason to take a
    /// new one is usually that something went wrong. Writing in place would destroy the last good
    /// picture the moment capture started, and leave it destroyed if the capture then failed.
    /// </summary>
    [TestMethod]
    public async Task Screenshot_CaptureFails_LeavesAnExistingScreenshotIntact()
    {
        await using var harness = new Harness(GuestWindows());
        var destination = TestPaths.Under(_root, "desktop.png");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, "the previous screenshot", TestContext.CancellationToken);

        var capture = new FakeWindowCapture { CaptureWithoutActivationOverride = _ => null };

        var (exitCode, _) = await CaptureStandardErrorAsync(() => RunScreenshotAsync(
            harness, new TestConsole(), capture, "sandbox", "-o", destination, "--json"));

        Assert.AreEqual(TargetOutput.TargetInfrastructureExitCode, exitCode);
        Assert.AreEqual(
            "the previous screenshot",
            await File.ReadAllTextAsync(destination, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Screenshot_Cancelled_LeavesAnExistingScreenshotIntact()
    {
        await using var harness = new Harness(GuestWindows());
        var destination = TestPaths.Under(_root, "desktop.png");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, "the previous screenshot", TestContext.CancellationToken);

        using var cancellation = new CancellationTokenSource();
        var capture = new FakeWindowCapture
        {
            CaptureWithoutActivationOverride = _ =>
            {
                cancellation.Cancel();
                return (new byte[4 * 3 * 2], 3, 2);
            },
        };

        var command = new TargetScreenshotCommand();
        var handler = new TargetScreenshotCommand.Handler(
            harness.Orchestrator, capture, new TestConsole(), NullLogger<TargetScreenshotCommand>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => handler.InvokeAsync(
                Parse(command, "sandbox", "-o", destination), cancellation.Token));

        Assert.AreEqual(
            "the previous screenshot",
            await File.ReadAllTextAsync(destination, TestContext.CancellationToken));
        Assert.AreEqual(
            0,
            Directory.GetFiles(Path.GetDirectoryName(destination)!, "*.tmp").Length,
            "A half-written temporary file must not be left behind either.");
    }

    [TestMethod]
    public async Task Screenshot_OverwritingAnExistingFile_ReplacesItWholeOnSuccess()
    {
        await using var harness = new Harness(GuestWindows());
        var destination = TestPaths.Under(_root, "desktop.png");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, "the previous screenshot", TestContext.CancellationToken);

        var capture = new FakeWindowCapture
        {
            CaptureWithoutActivationOverride = _ => (new byte[4 * 3 * 2], 3, 2),
        };

        Assert.AreEqual(
            0,
            await RunScreenshotAsync(harness, new TestConsole(), capture, "sandbox", "-o", destination));

        var bytes = await File.ReadAllBytesAsync(destination, TestContext.CancellationToken);
        CollectionAssert.AreEqual(
            new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' },
            bytes.Take(4).ToArray(),
            "The published file is a whole PNG, not the old contents and not a mixture.");
    }

    // ---- record --------------------------------------------------------------------

    [TestMethod]
    public async Task Record_RecordsTheDesktopWindowThroughTheOrdinaryPipeline()
    {
        await using var harness = new Harness(GuestWindows());
        var console = new TestConsole();
        var recording = new FakeUiRecordingService();
        var destination = TestPaths.Under(_root, "desktop.mp4");

        var exitCode = await RunRecordAsync(
            harness, console, recording, "sandbox", "-o", destination, "--duration-sec", "1", "--json");

        Assert.AreEqual(0, exitCode);

        var payload = JsonSerializer.Deserialize(
            console.Output.Trim(), UiJsonContext.Default.UiRecordResult)!;

        Assert.AreEqual(destination, payload.Path);
        Assert.AreEqual("h264", payload.Codec);
        Assert.AreEqual(Epoch.Value, payload.ExecutionTarget!.Epoch);

        Assert.AreEqual(DesktopHwnd, recording.LastTarget!.WindowHandle);
        Assert.IsNull(recording.LastElementId);
        Assert.IsFalse(recording.LastRecordOptions!.CaptureScreen);
        Assert.AreEqual(1, recording.LastRecordOptions.DurationSec);
    }

    /// <summary>
    /// A guest connection is a scarce, single-occupant channel, and a recording can legitimately run
    /// for hours. Everything a recording needs is a host window handle read once up front, so the
    /// channel must be handed back before the long wait starts.
    /// </summary>
    [TestMethod]
    public async Task Record_ReleasesTheGuestChannelBeforeTheLongWaitBegins()
    {
        await using var harness = new Harness(GuestWindows());
        var recording = new FakeUiRecordingService();
        bool? connectedWhileRecording = null;
        recording.WhileRecording = () =>
            connectedWhileRecording = harness.Backend.LastHostTransport?.IsConnected;

        var exitCode = await RunRecordAsync(
            harness,
            new TestConsole(),
            recording,
            "sandbox",
            "-o",
            TestPaths.Under(_root, "desktop.mp4"),
            "--duration-sec",
            "1");

        Assert.AreEqual(0, exitCode);
        Assert.IsNotNull(harness.Backend.LastHostTransport, "The target was prepared, so a channel was opened.");
        Assert.AreEqual(false, connectedWhileRecording, "The channel is still held while recording.");
    }

    [TestMethod]
    public async Task Record_TargetThatDrawsNoDesktopHere_FailsBeforeRecordingStarts()
    {
        await using var harness = new Harness(GuestWindows(), rendersDesktop: false);
        var console = new TestConsole();
        var recording = new FakeUiRecordingService();

        var (exitCode, stderr) = await CaptureStandardErrorAsync(() => RunRecordAsync(
            harness,
            console,
            recording,
            "sandbox",
            "-o",
            TestPaths.Under(_root, "desktop.mp4"),
            "--duration-sec",
            "1",
            "--json"));

        Assert.AreEqual(TargetOutput.TargetInfrastructureExitCode, exitCode);
        Assert.AreEqual(string.Empty, console.Output);
        StringAssert.Contains(stderr, ExecutionTargetErrorCodes.Unsupported);
        Assert.IsNull(recording.LastRecordOptions);
    }

    [TestMethod]
    public async Task Record_UnknownTarget_IsRefusedBeforeAnythingIsRecorded()
    {
        await using var harness = new Harness(GuestWindows());
        var recording = new FakeUiRecordingService();

        Assert.AreEqual(
            TargetOutput.InvalidCommandLineExitCode,
            await RunRecordAsync(
                harness,
                new TestConsole(),
                recording,
                "vm",
                "-o",
                TestPaths.Under(_root, "desktop.mp4"),
                "--duration-sec",
                "1"));

        Assert.IsNull(recording.LastRecordOptions);
        Assert.AreEqual(0, harness.Backend.EnsureCalls);
    }

    /// <summary>
    /// Preparing a target can start a Windows Sandbox, connect a client, and bootstrap an agent —
    /// minutes of work and a window on the user's screen. A request that could never have recorded
    /// must be refused while refusing is still free.
    /// </summary>
    [TestMethod]
    [DataRow("--duration-sec", "-1", DisplayName = "negative duration")]
    [DataRow("--duration-sec", "86401", DisplayName = "longer than a day")]
    [DataRow("--fps", "0", DisplayName = "no cadence")]
    [DataRow("--max-edge", "32", DisplayName = "below the encoder minimum")]
    public async Task Record_InvalidOption_IsRefusedBeforeTheTargetIsTouched(string option, string value)
    {
        await using var harness = new Harness(GuestWindows());
        var console = new TestConsole();
        var recording = new FakeUiRecordingService();
        string[] arguments = option == "--duration-sec"
            ? ["sandbox", "-o", TestPaths.Under(_root, "desktop.mp4"), option, value, "--json"]
            : ["sandbox", "-o", TestPaths.Under(_root, "desktop.mp4"), "--duration-sec", "5", option, value, "--json"];

        var (exitCode, stderr) = await CaptureStandardErrorAsync(
            () => RunRecordAsync(harness, console, recording, arguments));

        Assert.AreEqual(TargetOutput.InvalidCommandLineExitCode, exitCode);
        Assert.AreEqual(string.Empty, console.Output, "A failure must not put anything on stdout under --json.");
        StringAssert.Contains(stderr, ExecutionTargetErrorCodes.TargetInvalidArguments);
        Assert.AreEqual(0, harness.Backend.EnsureCalls, "Nothing may be created to serve a request this bad.");
        Assert.AreEqual(0, harness.Backend.AttachCalls);
        Assert.IsNull(recording.LastRecordOptions);
    }

    [TestMethod]
    public async Task Record_FramesOverAnExistingArtifact_IsRefusedBeforeTheTargetIsTouched()
    {
        await using var harness = new Harness(GuestWindows());
        var destination = TestPaths.Under(_root, "desktop.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, "an earlier recording", TestContext.CancellationToken);

        var (exitCode, stderr) = await CaptureStandardErrorAsync(() => RunRecordAsync(
            harness,
            new TestConsole(),
            new FakeUiRecordingService(),
            "sandbox",
            "-o",
            destination,
            "--duration-sec",
            "1",
            "--frames",
            "--json"));

        Assert.AreEqual(TargetOutput.InvalidCommandLineExitCode, exitCode);
        StringAssert.Contains(stderr, ExecutionTargetErrorCodes.TargetInvalidArguments);
        Assert.AreEqual(0, harness.Backend.EnsureCalls);
        Assert.AreEqual(
            "an earlier recording",
            await File.ReadAllTextAsync(destination, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Record_InvalidOption_HumanOutput_SaysWhatIsWrongAndWhatToDo()
    {
        await using var harness = new Harness(GuestWindows());

        var (exitCode, stderr) = await CaptureStandardErrorAsync(() => RunRecordAsync(
            harness,
            new TestConsole(),
            new FakeUiRecordingService(),
            "sandbox",
            "-o",
            TestPaths.Under(_root, "desktop.mp4"),
            "--fps",
            "0"));

        Assert.AreEqual(TargetOutput.InvalidCommandLineExitCode, exitCode);
        StringAssert.Contains(stderr, "--fps must be at least 1");
        Assert.AreEqual(0, harness.Backend.EnsureCalls);
    }

    // ---- harness -------------------------------------------------------------------

    private Task<int> RunSnapshotAsync(Harness harness, IAnsiConsole console, params string[] arguments) =>
        RunSnapshotAsync(harness, console, new EmptyDeploymentStateStore(), arguments);

    private Task<int> RunSnapshotAsync(
        Harness harness,
        IAnsiConsole console,
        IDeploymentStateStore deployments,
        params string[] arguments)
    {
        var command = new TargetSnapshotCommand();
        var handler = new TargetSnapshotCommand.Handler(
            harness.Orchestrator, deployments, console);

        return handler.InvokeAsync(Parse(command, arguments), TestContext.CancellationToken);
    }

    private Task<int> RunScreenshotAsync(
        Harness harness,
        IAnsiConsole console,
        IWindowCapture capture,
        params string[] arguments)
    {
        var command = new TargetScreenshotCommand();
        var handler = new TargetScreenshotCommand.Handler(
            harness.Orchestrator, capture, console, NullLogger<TargetScreenshotCommand>.Instance);

        return handler.InvokeAsync(Parse(command, arguments), TestContext.CancellationToken);
    }

    private Task<int> RunRecordAsync(
        Harness harness,
        IAnsiConsole console,
        IUiRecordingService recording,
        params string[] arguments)
    {
        var command = new TargetRecordCommand();
        var handler = new TargetRecordCommand.Handler(
            harness.Orchestrator,
            new FakeUiTargetResolver(),
            recording,
            new FakeWindowCapture(),
            new FakeSystemUiQuery { ProcessIdForWindowResult = DesktopProcessId },
            console,
            new FakeInteractiveDesktopLock(),
            NullLogger<UiRecordCommand>.Instance);

        return handler.InvokeAsync(Parse(command, arguments), TestContext.CancellationToken);
    }

    /// <summary>Parses against a root that carries the global options every verb reads.</summary>
    private static ParseResult Parse(Command command, params string[] arguments)
    {
        var root = new RootCommand();
        root.Options.Add(WinAppRootCommand.JsonOption);
        root.Options.Add(WinAppRootCommand.QuietOption);
        root.Subcommands.Add(command);

        return root.Parse([command.Name, .. arguments]);
    }

    /// <summary>
    /// Runs <paramref name="action"/> with stderr captured, because failures are reported there.
    /// </summary>
    /// <remarks>
    /// Redirects the process-wide stream, which is why this class is <c>[DoNotParallelize]</c>: two
    /// tests redirecting at once would restore each other's writer and lose the output.
    /// </remarks>
    private static async Task<(int ExitCode, string StandardError)> CaptureStandardErrorAsync(Func<Task<int>> action)
    {
        var original = Console.Error;
        var captured = new StringWriter();

        try
        {
            Console.SetError(captured);
            return (await action(), captured.ToString());
        }
        finally
        {
            Console.SetError(original);
        }
    }

    private static TargetSnapshotOutput Deserialize(string stdout) =>
        JsonSerializer.Deserialize(stdout.Trim(), TargetJsonContext.Default.TargetSnapshotOutput)!;

    private static WindowInfo Window(long hwnd, string title, int width, int height, bool foreground = false) =>
        new()
        {
            Hwnd = hwnd,
            ProcessId = 100,
            ProcessName = "app",
            Title = title,
            Width = width,
            Height = height,
            IsForeground = foreground,
        };

    private static string GuestWindows(params WindowInfo[] windows) =>
        JsonSerializer.Serialize(windows, UiJsonContext.Default.WindowInfoArray);

    private static DeploymentState State(
        string deploymentId,
        int? processId,
        long? startTicksUtc,
        bool packaged) =>
        new()
        {
            SchemaVersion = DeploymentStateStore.CurrentSchemaVersion,
            Revision = 1,
            DeploymentId = deploymentId,
            TargetEpoch = Epoch.Value,
            Dirty = false,
            Desired = [],
            Package = packaged
                ? new PackageOwnership
                {
                    PackageName = "Contoso.MyApp",
                    Publisher = "CN=Contoso",
                    PackageFullName = "Contoso.MyApp_1.0.0.0_x64__abc",
                    PackageFamilyName = "Contoso.MyApp_fakefamily",
                    RegisteredLocation = @"C:\WinApp\layouts\packaged",
                    Aumid = "Contoso.App_abc!App",
                }
                : null,
            WasPackaged = packaged,
            TrackedOperationProcessId = processId,
            TrackedOperationProcessStartTicksUtc = startTicksUtc,
        };

    /// <summary>A backend, guest agent, and orchestrator wired together over one in-memory transport.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(60));
        private readonly List<Task> _servers = [];
        private readonly List<IDisposable> _leases = [];
        private readonly string _guestManaged = TestPaths.TempRoot("target-capture-guest");

        public Harness(
            string stdout,
            int exitCode = 0,
            bool rendersDesktop = true,
            IAppLauncherService? appLauncher = null)
        {
            Directory.CreateDirectory(_guestManaged);
            Backend = rendersDesktop
                ? new RenderingBackend(this, stdout, exitCode, appLauncher)
                : new FakeBackend(this, stdout, exitCode, appLauncher);

            Orchestrator = new ExecutionTargetOrchestrator(Backend, new FakeLock(this), new FakeLock(this));
        }
        public FakeBackend Backend { get; }

        /// <summary>The same backend when it draws a desktop here, for asserting which path was used.</summary>
        public RenderingBackend Rendering =>
            Backend as RenderingBackend ??
            throw new InvalidOperationException("This harness's target draws no desktop on this machine.");

        public ExecutionTargetOrchestrator Orchestrator { get; }

        public CancellationToken ServerToken => _cancellation.Token;

        public string GuestManaged => _guestManaged;

        public void Track(Task server) => _servers.Add(server);

        public void Track(IDisposable lease) => _leases.Add(lease);

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync();

            foreach (var server in _servers)
            {
                try
                {
                    await server;
                }
                catch (OperationCanceledException)
                {
                    // Expected.
                }
            }

            foreach (var lease in _leases)
            {
                lease.Dispose();
            }

            _cancellation.Dispose();

            try
            {
                Directory.Delete(_guestManaged, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }

    /// <summary>A target that runs commands but draws nothing on this machine.</summary>
    private class FakeBackend(
        Harness harness,
        string stdout,
        int exitCode,
        IAppLauncherService? appLauncher)
        : IExecutionTargetBackend, IInspectableTarget
    {
        public ExecutionTargetRef Target => WindowsSandboxTarget.Default;

        /// <summary>How many times a command asked for a prepared, connected target.</summary>
        /// <remarks>
        /// The measure of "did this command change anything?": preparing is what creates, connects,
        /// and repairs. A snapshot that leaves this at zero cannot have started a Sandbox.
        /// </remarks>
        public int EnsureCalls { get; private set; }

        /// <summary>How many times a command asked what was already there.</summary>
        public int AttachCalls { get; private set; }

        /// <summary>Whether the managed target is running at all.</summary>
        public bool Running { get; set; } = true;

        /// <summary>Whether the running target's agent answers an inspect-only attach.</summary>
        public bool AgentAnswers { get; set; } = true;

        /// <summary>The host end of the last channel handed out, so a test can watch it close.</summary>
        public IGuestTransport? LastHostTransport { get; private set; }

        public Task<TargetSupportResult> ProbeSupportAsync(CancellationToken cancellationToken) =>
            Task.FromResult(TargetSupportResult.Supported);

        public Task<TargetConnection> EnsureConnectedAsync(
            EnsureTargetOptions options,
            CancellationToken cancellationToken)
        {
            EnsureCalls++;
            return Task.FromResult(Connect());
        }

        public Task<TargetAttachment> TryAttachAsync(CancellationToken cancellationToken)
        {
            AttachCalls++;

            if (!Running)
            {
                return Task.FromResult(TargetAttachment.NotRunning);
            }

            return Task.FromResult(AgentAnswers
                ? new TargetAttachment(true, Epoch, Connect())
                : new TargetAttachment(true, Epoch, null));
        }

        public IReadOnlyDictionary<string, string> DescribeForDiagnostics() =>
            new Dictionary<string, string> { ["sandboxId"] = "sandbox-1" };

        private TargetConnection Connect()
        {
            var pair = new LoopbackTransportPair();

            var server = new GuestCommandServer(
                pair.Guest,
                Epoch,
                new ScriptedGuestWinapp(stdout, exitCode),
                new StaticGuestSessionProbe(new GuestSessionInfo(1, "WinSta0", HasInputDesktop: true)),
                new GuestAgentIdentity("1.0.0", "hash", "arm64", 1, 1),
                files: new GuestFileService(harness.GuestManaged),
                guestWinapp: @"C:\WinAppGuest\winapp.exe",
                appLauncher: appLauncher);

            harness.Track(server.RunAsync(harness.ServerToken));

            LastHostTransport = pair.Host;
            return new TargetConnection(Epoch, pair.Host, Reused: true);
        }
    }

    /// <summary>The same target, but one whose desktop this machine draws in a window.</summary>
    private sealed class RenderingBackend(
        Harness harness,
        string stdout,
        int exitCode,
        IAppLauncherService? appLauncher)
        : FakeBackend(harness, stdout, exitCode, appLauncher), IHostRenderedTarget
    {
        /// <summary>How many times a caller asked for the surface on the persisting path.</summary>
        public int ResolveSurfaceCalls { get; private set; }

        /// <summary>How many times a caller asked for the surface on the inspect-only path.</summary>
        public int InspectSurfaceCalls { get; private set; }

        public bool Minimized { get; set; }

        public List<TargetDesktopUse> ResolvedUses { get; } = [];

        public TargetDesktopSurface ResolveDesktopSurface(TargetDesktopUse use)
        {
            ResolveSurfaceCalls++;
            ResolvedUses.Add(use);
            return Surface();
        }

        public TargetDesktopSurface InspectDesktopSurface()
        {
            InspectSurfaceCalls++;
            return Surface();
        }

        private TargetDesktopSurface Surface() =>
            new(
                DesktopHwnd,
                DesktopProcessId,
                "WindowsSandboxRemoteSession",
                Adopted: false,
                IsMinimized: Minimized);
    }

    /// <summary>A guest winapp whose answer is scripted rather than run.</summary>
    private sealed class ScriptedGuestWinapp(string stdout, int exitCode) : IGuestProcessHostFactory
    {
        public IGuestProcessHost Start(
            GuestExecRequest request,
            Action<GuestStreamId, ReadOnlyMemory<byte>> onOutput)
        {
            var host = new FakeGuestProcessHost(request, onOutput, processId: 4321);

            if (stdout.Length > 0)
            {
                host.Emit(GuestStreamId.StandardOutput, stdout);
            }

            host.Exit(exitCode);
            return host;
        }
    }

    /// <summary>A lock that always grants, standing in for both file-backed locks.</summary>
    private sealed class FakeLock(Harness harness) : ITargetMutationLock, ITargetConnectionLock
    {
        TargetMutationLease? ITargetMutationLock.TryAcquire(
            ExecutionTargetRef target,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            new(NewStream(), wasAbandoned: false);

        TargetConnectionLease? ITargetConnectionLock.TryAcquire(
            ExecutionTargetRef target,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            new(NewStream());

        private FileStream NewStream()
        {
            var stream = new FileStream(
                TestPaths.TempFile("target-capture-lock", ".lock"),
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);

            harness.Track(stream);
            return stream;
        }
    }

    /// <summary>A target with nothing deployed, so a snapshot reports capabilities and windows only.</summary>
    private sealed class EmptyDeploymentStateStore : IDeploymentStateStore
    {
        public DeploymentState? Read(ExecutionTargetRef target, string deploymentId) => null;

        public DeploymentState Commit(ExecutionTargetRef target, DeploymentState state, long expectedRevision) =>
            throw new NotSupportedException("A snapshot never writes deployment state.");

        public void Clear(ExecutionTargetRef target, string deploymentId) =>
            throw new NotSupportedException("A snapshot never clears deployment state.");

        public IReadOnlyList<DeploymentState> List(ExecutionTargetRef target) => [];
    }

    private sealed class FixedStateDirectoryProvider(string root) : ITargetStateDirectoryProvider
    {
        public DirectoryInfo GetTargetRoot(ExecutionTargetRef target, bool create)
        {
            var directory = new DirectoryInfo(TestPaths.Under(root, target.StateKey));
            if (create)
            {
                directory.Create();
            }

            return directory;
        }
    }

    private sealed class MutableDeploymentStateStore(DeploymentState state) : IDeploymentStateStore
    {
        private DeploymentState _state = state;
        private int _reads;

        public bool AdvanceRevisionOnRead { get; init; }

        public DeploymentState? Read(ExecutionTargetRef target, string deploymentId)
        {
            _reads++;
            return AdvanceRevisionOnRead && _reads > 0
                ? _state with { Revision = _state.Revision + 1 }
                : _state;
        }

        public DeploymentState Commit(
            ExecutionTargetRef target,
            DeploymentState state,
            long expectedRevision) =>
            _state = state with { Revision = expectedRevision + 1 };

        public void Clear(ExecutionTargetRef target, string deploymentId) =>
            throw new NotSupportedException();

        public IReadOnlyList<DeploymentState> List(ExecutionTargetRef target) => [_state];
    }
}
