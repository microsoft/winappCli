// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Commands;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services;

using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

/// <summary>
/// The <c>run --on sandbox</c> and <c>unregister --on sandbox</c> orchestration, driven through the real
/// command channel into a real guest server with only the transport faked.
/// </summary>
/// <remarks>
/// Acceptance criterion 13 again: deployment, launch, ownership, and unregistration all run here
/// without Windows Sandbox. If any of it reached for a <c>wsb</c> command or a Sandbox path, these
/// tests could not run at all.
/// </remarks>
[TestClass]
public class SandboxRunTests
{
    private const string ManagedRoot = @"C:\WinAppGuest";

    private static readonly ExecutionTargetEpoch Epoch = ExecutionTargetEpoch.Create("sandbox-1", "nonce-a");

    private static readonly string[] FullOptionMatrixArguments =
    [
        "run", @"C:\WinApp\deployments\abc",
        "--managed-appx-directory", @"C:\WinApp\deployments\abc-layout",
        "--no-launch", "--with-alias", "--debug-output", "--unregister-on-exit",
        "--detach", "--clean", "--json", "--args", "--flag value",
    ];

    private static readonly string[] MinimalRunArguments =
    [
        "run", @"C:\WinApp\deployments\abc", "--managed-appx-directory", @"C:\WinApp\deployments\abc-layout",
    ];

    private string _root = null!;
    private string _hostSource = null!;
    private string _guestManaged = null!;
    private string _stateRoot = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = TestPaths.TempRoot(nameof(SandboxRunTests));
        _hostSource = TestPaths.Under(_root, "host");
        _guestManaged = TestPaths.Under(_root, "guest");
        _stateRoot = TestPaths.Under(_root, "state");

        Directory.CreateDirectory(_hostSource);
        Directory.CreateDirectory(_guestManaged);
        Directory.CreateDirectory(_stateRoot);
    }

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

    // ---- Guest command translation -------------------------------------------------

    [TestMethod]
    public void BuildRunArguments_ForwardsTheWholeOptionMatrix()
    {
        var arguments = GuestRunPlanner.BuildRunArguments(
            @"C:\WinApp\deployments\abc",
            @"C:\WinApp\deployments\abc-layout",
            new GuestRunOptions(
                NoLaunch: true,
                WithAlias: true,
                DebugOutput: true,
                UnregisterOnExit: true,
                Detach: true,
                Clean: true,
                Json: true,
                AppArguments: "--flag value"));

        // Every option is the guest's ordinary winapp run option, so its meaning cannot drift from
        // the local one.
        CollectionAssert.AreEqual(FullOptionMatrixArguments, arguments);
    }

    [TestMethod]
    public void BuildRunArguments_WithNoOptions_PassesOnlyTheTwoPaths()
    {
        var arguments = GuestRunPlanner.BuildRunArguments(
            @"C:\WinApp\deployments\abc", @"C:\WinApp\deployments\abc-layout", new GuestRunOptions());

        CollectionAssert.AreEqual(MinimalRunArguments, arguments);
    }

    /// <summary>
    /// The guest registration layout is a directory the host created for this deployment, so the
    /// guest has to be told it is winapp's own. Sending it as <c>--output-appx-directory</c> would
    /// tell the guest the opposite -- that a person named it -- and a file dropped from the app
    /// would then be left behind in the directory the package is registered from.
    /// </summary>
    [TestMethod]
    public void BuildRunArguments_MarksTheGuestLayoutAsWinappsOwn_NotAsAUserSuppliedDirectory()
    {
        var arguments = GuestRunPlanner.BuildRunArguments(
            @"C:\WinApp\deployments\abc", @"C:\WinApp\deployments\abc-layout", new GuestRunOptions());

        CollectionAssert.DoesNotContain(arguments, "--output-appx-directory");

        var index = arguments.IndexOf("--managed-appx-directory");
        Assert.AreNotEqual(-1, index, "the guest layout must be marked as winapp-managed");
        Assert.AreEqual(@"C:\WinApp\deployments\abc-layout", arguments[index + 1]);
    }

    /// <summary>
    /// The guest resolves that option to exact reconciliation, which is the whole point of sending
    /// it: parsing it as anything else would silently restore the additive behavior.
    /// </summary>
    [TestMethod]
    public void GuestRunArguments_ParseBackToAWinappOwnedLayout()
    {
        var arguments = GuestRunPlanner.BuildRunArguments(
            @"C:\WinApp\deployments\abc", @"C:\WinApp\deployments\abc-layout", new GuestRunOptions());

        var parseResult = new RunCommand().Parse(arguments.ToArray());

        Assert.IsTrue(RunCommand.TryResolveLayoutOutput(parseResult, out var layoutOutput, out var error), error);
        Assert.AreEqual(LayoutReconciliation.Exact, layoutOutput.Reconciliation);
        Assert.AreEqual(@"C:\WinApp\deployments\abc-layout", layoutOutput.Directory?.FullName);
    }

    [TestMethod]
    public void EnsureSupportedForUnpackaged_DebugOutput_IsRefusedUpFront()
    {
        var failure = Assert.ThrowsExactly<ExecutionTargetException>(() =>
            GuestRunPlanner.EnsureSupportedForUnpackaged(new GuestRunOptions(DebugOutput: true)));

        Assert.AreEqual(ExecutionTargetErrorCodes.Unsupported, failure.Error.Code);
    }

    // ---- Guest path resolution -----------------------------------------------------

    [TestMethod]
    public void GuestPaths_ResolvesUnderTheRootTheGuestReported()
    {
        Assert.AreEqual(
            @"C:\WinAppGuest\deployments\abc",
            GuestPaths.Resolve(Capabilities(), GuestPaths.PayloadScope("abc")));
    }

    /// <summary>
    /// Nested, the layout would be enumerated by the next reconciliation, found absent from the
    /// host's desired state, and deleted — destroying what the previous run registered from.
    /// </summary>
    [TestMethod]
    public void GuestPaths_LayoutIsASiblingOfThePayloadNotAChildOfIt()
    {
        var payload = GuestPaths.Resolve(Capabilities(), GuestPaths.PayloadScope("abc"));
        var layout = GuestPaths.Resolve(Capabilities(), GuestPaths.LayoutScope("abc"));

        Assert.AreNotEqual(payload, layout);
        Assert.IsFalse(
            layout.StartsWith(payload + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "The registration layout must not live inside the folder reconciliation owns");
    }

    [TestMethod]
    public void GuestPaths_WithoutAReportedRoot_RefusesRatherThanGuessing()
    {
        var failure = Assert.ThrowsExactly<ExecutionTargetException>(() =>
            GuestPaths.Resolve(Capabilities(managedRoot: null), GuestPaths.PayloadScope("abc")));

        Assert.AreEqual(ExecutionTargetErrorCodes.AgentIncompatible, failure.Error.Code);
    }

    // ---- Deployment ----------------------------------------------------------------

    [TestMethod]
    public async Task Deploy_PlacesTheLayoutWhereTheGuestRunWillBeToldToLookForIt()
    {
        await WriteHostFileAsync("appxmanifest.xml", "<Package/>");
        await WriteHostFileAsync("app.exe", "binary");

        await using var harness = new Harness(_guestManaged, _stateRoot);

        var deployment = await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken);

        Assert.AreEqual(@"C:\WinAppGuest\deployments\dep-1", deployment.PayloadPath);
        Assert.AreEqual(@"C:\WinAppGuest\deployments\dep-1-layout", deployment.LayoutPath);
        Assert.IsFalse(deployment.State.Dirty);

        Assert.AreEqual("binary", await File.ReadAllTextAsync(
            TestPaths.Under(_guestManaged, "deployments", "dep-1", "app.exe"), TestContext.CancellationToken));
    }

    /// <summary>
    /// A recipe lists build outputs by absolute host path. Guest winapp would prefer it over the
    /// files actually present, resolve none of them, and register an empty layout.
    /// </summary>
    [TestMethod]
    public async Task Deploy_NeverTransfersAnAppxRecipe()
    {
        await WriteHostFileAsync("appxmanifest.xml", "<Package/>");
        await WriteHostFileAsync("app.exe", "binary");
        await WriteHostFileAsync("MyApp.build.appxrecipe", "<Project/>");

        await using var harness = new Harness(_guestManaged, _stateRoot);

        await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken);

        Assert.IsFalse(File.Exists(TestPaths.Under(_guestManaged, "deployments", "dep-1", "MyApp.build.appxrecipe")));
        Assert.IsTrue(File.Exists(TestPaths.Under(_guestManaged, "deployments", "dep-1", "app.exe")));
    }

    [TestMethod]
    public async Task Deploy_Clean_DiscardsTheRegistrationLayoutTheLastRunProduced()
    {
        await WriteHostFileAsync("app.exe", "binary");

        var staleLayout = TestPaths.Under(_guestManaged, "deployments", "dep-1-layout");
        Directory.CreateDirectory(staleLayout);
        await File.WriteAllTextAsync(
            TestPaths.Under(staleLayout, "old.exe"), "previous-build", TestContext.CancellationToken);

        await using var harness = new Harness(_guestManaged, _stateRoot);

        await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: true, TestContext.CancellationToken);

        // Left in place, --clean would have registered the previous build's files.
        Assert.IsFalse(File.Exists(TestPaths.Under(staleLayout, "old.exe")));
    }

    // ---- Launch --------------------------------------------------------------------

    [TestMethod]
    public async Task Run_AsksTheGuestToRunItsOwnWinapp()
    {
        await WriteHostFileAsync("app.exe", "binary");

        await using var harness = new Harness(_guestManaged, _stateRoot);

        var deployment = await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken);

        var run = harness.Runner.RunAsync(
            harness.Target,
            deployment.State,
            new GuestExecRequest
            {
                UseGuestWinapp = true,
                Arguments = GuestRunPlanner.BuildRunArguments(
                    deployment.PayloadPath, deployment.LayoutPath, new GuestRunOptions()),
            },
            new GuestExecCallbacks(),
            TestContext.CancellationToken);

        var process = await harness.Processes.WaitForNextAsync(TestContext.CancellationToken);

        // The host names the binary by intent, never by path: it does not know where the agent
        // installed itself, and a host-supplied path would make the agent's identity host-selectable.
        Assert.AreEqual(Harness.GuestWinappPath, process.Request.Executable);
        Assert.AreEqual("run", process.Request.Arguments[0]);

        process.Exit(7);

        // The guest application's exit code survives, distinct from an infrastructure failure.
        Assert.AreEqual(7, (await run).ExitCode);
    }

    [TestMethod]
    public async Task Run_RecordsTheLaunchedProcessAgainstTheDeployment()
    {
        await WriteHostFileAsync("app.exe", "binary");

        await using var harness = new Harness(_guestManaged, _stateRoot);

        var deployment = await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken);

        var run = harness.Runner.RunAsync(
            harness.Target,
            deployment.State,
            new GuestExecRequest { UseGuestWinapp = true, Arguments = ["run"] },
            new GuestExecCallbacks(),
            TestContext.CancellationToken);

        var process = await harness.Processes.WaitForNextAsync(TestContext.CancellationToken);
        process.Exit(0);
        await run;

        var state = harness.States.Read(WindowsSandboxTarget.Default, "dep-1");
        Assert.AreEqual(process.ProcessId, state!.TrackedOperationProcessId);
        Assert.AreEqual(process.StartTicksUtc, state.TrackedOperationProcessStartTicksUtc);

        // Recording the process advances the stored revision, so the caller has to be handed
        // the record back. A caller that kept its pre-launch state and committed against it
        // later — clearing package ownership after --unregister-on-exit, for example — would be
        // one revision behind, and that commit is refused without failing anything visibly.
        var outcome = await run;
        Assert.AreEqual(state.Revision, outcome.State.Revision, "RunAsync must return the committed record.");
        Assert.IsGreaterThan(deployment.State.Revision, outcome.State.Revision);

        var cleared = harness.Runner.ClearPackage(WindowsSandboxTarget.Default, outcome.State);
        Assert.IsNull(cleared.Package, "Clearing ownership against the returned record must succeed.");
    }

    [TestMethod]
    public async Task Run_WhenTheAgentCannotLocateItsOwnBinary_RefusesRatherThanRunningSomethingElse()
    {
        await using var harness = new Harness(_guestManaged, _stateRoot, guestWinapp: null);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            harness.Target.Operations.ExecuteAsync(
                new GuestExecRequest { UseGuestWinapp = true, Arguments = ["run"] },
                callbacks: null,
                TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.AgentIncompatible, failure.Error.Code);
        Assert.IsTrue(harness.Processes.Started.IsEmpty, "Nothing should have been started");
    }

    [TestMethod]
    public async Task Run_WithNeitherAnExecutableNorTheGuestWinappFlag_IsRefused()
    {
        await using var harness = new Harness(_guestManaged, _stateRoot);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            harness.Target.Operations.ExecuteAsync(
                new GuestExecRequest { Arguments = ["--info"] },
                callbacks: null,
                TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.TargetAmbiguous, failure.Error.Code);
    }

    // ---- Ownership -----------------------------------------------------------------

    [TestMethod]
    public async Task ReconcilePackage_TwoClaims_SelectsTheActualRegisteredLocation()
    {
        await WriteHostFileAsync("app.exe", "binary");

        await using var harness = new Harness(_guestManaged, _stateRoot);

        var first = await CreateOwnedDeploymentAsync(harness, "dep-x64");
        var latest = await CreateOwnedDeploymentAsync(harness, "dep-arm64");
        harness.AppLauncher.FakePackageFullName = "Contoso.MyApp_2.0.0.0_arm64__abc";
        harness.AppLauncher.FakeRegisteredLocation = latest.LayoutPath;

        var result = await ReconcileForUnregisterAsync(harness);

        Assert.AreEqual(latest.State.DeploymentId, result.Owner!.DeploymentId);
        Assert.AreEqual(2, result.Claims.Count);
        Assert.IsTrue(result.Claims.Any(claim => claim.DeploymentId == first.State.DeploymentId));
    }

    [TestMethod]
    public async Task ReconcilePackage_DuplicateClaimsForSameRegistration_AreNotAmbiguous()
    {
        await WriteHostFileAsync("app.exe", "binary");

        await using var harness = new Harness(_guestManaged, _stateRoot);

        var first = await CreateOwnedDeploymentAsync(harness, "dep-1");
        var duplicate = await harness.Runner.DeployAsync(
            harness.Target, "dep-2", new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken);
        harness.Runner.CommitPackage(
            WindowsSandboxTarget.Default,
            duplicate.State,
            Ownership(first.LayoutPath));
        harness.AppLauncher.FakeRegisteredLocation = first.LayoutPath;

        var result = await ReconcileForUnregisterAsync(harness);

        Assert.AreEqual(
            first.State.DeploymentId,
            result.Owner!.DeploymentId,
            "The deployment whose own managed layout is registered must be selected.");
        Assert.AreEqual(2, result.Claims.Count);
    }

    [TestMethod]
    public async Task ReconcilePackage_NoActualRegistration_ClearsCurrentClaimsButIgnoresOldGeneration()
    {
        await WriteHostFileAsync("app.exe", "binary");

        await using var harness = new Harness(_guestManaged, _stateRoot);

        await CreateOwnedDeploymentAsync(harness, "dep-1");
        await CreateOwnedDeploymentAsync(harness, "dep-2");
        var old = new DeploymentState
        {
            SchemaVersion = DeploymentStateStore.CurrentSchemaVersion,
            Revision = 0,
            DeploymentId = "dep-old",
            TargetEpoch = ExecutionTargetEpoch.Create("sandbox-1", "old").Value,
            Dirty = false,
            Package = Ownership(@"C:\WinAppGuest\deployments\dep-old-layout"),
        };
        harness.States.Commit(WindowsSandboxTarget.Default, old, expectedRevision: 0);
        harness.AppLauncher.FakePackageFullName = null;

        var result = await ReconcileForUnregisterAsync(harness);

        Assert.IsNull(result.Actual);
        Assert.IsNull(harness.States.Read(WindowsSandboxTarget.Default, "dep-1")!.Package);
        Assert.IsNull(harness.States.Read(WindowsSandboxTarget.Default, "dep-2")!.Package);
        Assert.IsNotNull(harness.States.Read(WindowsSandboxTarget.Default, "dep-old")!.Package);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ReconcilePackage_UnownedOrNonDevelopmentRegistration_RefusesAndPreservesClaims(
        bool isDevelopmentMode)
    {
        await WriteHostFileAsync("app.exe", "binary");

        await using var harness = new Harness(_guestManaged, _stateRoot);

        await CreateOwnedDeploymentAsync(harness, "dep-1");
        harness.AppLauncher.FakeRegisteredLocation = isDevelopmentMode
            ? @"C:\Users\WDAGUtilityAccount\External"
            : @"C:\WinAppGuest\deployments\dep-1-layout";
        harness.AppLauncher.FakeIsDevelopmentMode = isDevelopmentMode;

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => ReconcileForUnregisterAsync(harness));

        Assert.AreEqual(
            isDevelopmentMode
                ? ExecutionTargetErrorCodes.PackageConflict
                : ExecutionTargetErrorCodes.ProvisionedPackageConflict,
            failure.Error.Code);
        Assert.IsNotNull(harness.States.Read(WindowsSandboxTarget.Default, "dep-1")!.Package);
    }

    [TestMethod]
    public async Task ReconcilePackage_QueryFailure_PreservesClaims()
    {
        await WriteHostFileAsync("app.exe", "binary");
        await using var harness = new Harness(_guestManaged, _stateRoot);
        await CreateOwnedDeploymentAsync(harness, "dep-1");
        harness.AppLauncher.GetRegisteredPackageFailure = new InvalidOperationException("inventory unavailable");

        await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() => ReconcileForUnregisterAsync(harness));

        Assert.IsNotNull(harness.States.Read(WindowsSandboxTarget.Default, "dep-1")!.Package);
    }

    [TestMethod]
    public async Task ReconcileBeforeRegistration_FirstDeploymentRefusesExternalPackage()
    {
        await WriteHostFileAsync("app.exe", "binary");
        await using var harness = new Harness(_guestManaged, _stateRoot);
        harness.AppLauncher.FakeRegisteredLocation = @"C:\External\App";

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            harness.Runner.ReconcilePackageBeforeRegistrationAsync(
                harness.Target,
                "Contoso.MyApp",
                "CN=Contoso",
                "Contoso.MyApp_abc",
                TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.PackageConflict, failure.Error.Code);
        Assert.HasCount(0, harness.PackageRegistration.UnregisterByFullNameCalls);
    }

    [TestMethod]
    public async Task ReconcileRegistration_SuccessTransfersOwnershipAndClearsStaleClaims()
    {
        await WriteHostFileAsync("app.exe", "binary");
        await using var harness = new Harness(_guestManaged, _stateRoot);
        await CreateOwnedDeploymentAsync(harness, "dep-old");
        var latest = await CreateOwnedDeploymentAsync(harness, "dep-new");
        harness.AppLauncher.FakePackageFullName = "Contoso.MyApp_2.0.0.0_arm64__abc";
        harness.AppLauncher.FakeRegisteredLocation = latest.LayoutPath;

        var owner = await harness.Runner.ReconcileRegistrationAttemptAsync(
            harness.Target,
            latest.State.DeploymentId,
            "Contoso.MyApp",
            "CN=Contoso",
            "Contoso.MyApp_abc",
            registrationSucceeded: true,
            TestContext.CancellationToken);

        Assert.AreEqual(latest.State.DeploymentId, owner!.DeploymentId);
        Assert.AreEqual(harness.AppLauncher.FakePackageFullName, owner.Package!.PackageFullName);
        Assert.IsNull(harness.States.Read(WindowsSandboxTarget.Default, "dep-old")!.Package);
    }

    [TestMethod]
    public async Task ReconcileRegistration_FailedAttemptRestoresSurvivingOwnerAndClearsFalseClaim()
    {
        await WriteHostFileAsync("app.exe", "binary");
        await using var harness = new Harness(_guestManaged, _stateRoot);
        var prior = await CreateOwnedDeploymentAsync(harness, "dep-prior");
        await CreateOwnedDeploymentAsync(harness, "dep-attempt");
        harness.AppLauncher.FakeRegisteredLocation = prior.LayoutPath;

        var owner = await harness.Runner.ReconcileRegistrationAttemptAsync(
            harness.Target,
            "dep-attempt",
            "Contoso.MyApp",
            "CN=Contoso",
            "Contoso.MyApp_abc",
            registrationSucceeded: false,
            TestContext.CancellationToken);

        Assert.AreEqual("dep-prior", owner!.DeploymentId);
        Assert.IsNotNull(harness.States.Read(WindowsSandboxTarget.Default, "dep-prior")!.Package);
        Assert.IsNull(harness.States.Read(WindowsSandboxTarget.Default, "dep-attempt")!.Package);
    }

    [TestMethod]
    public async Task ReconcileRegistration_FailedAttemptWithNoActualRegistration_ClearsFalseClaims()
    {
        await WriteHostFileAsync("app.exe", "binary");
        await using var harness = new Harness(_guestManaged, _stateRoot);
        await CreateOwnedDeploymentAsync(harness, "dep-attempt");
        harness.AppLauncher.FakePackageFullName = null;

        var owner = await harness.Runner.ReconcileRegistrationAttemptAsync(
            harness.Target,
            "dep-attempt",
            "Contoso.MyApp",
            "CN=Contoso",
            "Contoso.MyApp_abc",
            registrationSucceeded: false,
            TestContext.CancellationToken);

        Assert.IsNull(owner);
        Assert.IsNull(harness.States.Read(WindowsSandboxTarget.Default, "dep-attempt")!.Package);
    }

    [TestMethod]
    public async Task ReconcileRegistration_SuccessWithoutActualRegistration_FailsAndPreservesRecoveryClaim()
    {
        await WriteHostFileAsync("app.exe", "binary");
        await using var harness = new Harness(_guestManaged, _stateRoot);
        await CreateOwnedDeploymentAsync(harness, "dep-attempt");
        harness.AppLauncher.FakePackageFullName = null;

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            harness.Runner.ReconcileRegistrationAttemptAsync(
                harness.Target,
                "dep-attempt",
                "Contoso.MyApp",
                "CN=Contoso",
                "Contoso.MyApp_abc",
                registrationSucceeded: true,
                TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.PackageConflict, failure.Error.Code);
        Assert.IsNotNull(harness.States.Read(WindowsSandboxTarget.Default, "dep-attempt")!.Package);
    }

    [TestMethod]
    public async Task UnregisterOwnedPackage_ExactRemovalAndConfirmedAbsenceClearAllClaims()
    {
        await WriteHostFileAsync("app.exe", "binary");
        await using var harness = new Harness(_guestManaged, _stateRoot);
        var owner = await CreateOwnedDeploymentAsync(harness, "dep-owner");
        await CreateOwnedDeploymentAsync(harness, "dep-stale");
        foreach (var deploymentId in new[] { "dep-owner", "dep-stale" })
        {
            var state = harness.States.Read(WindowsSandboxTarget.Default, deploymentId)!;
            harness.States.Commit(
                WindowsSandboxTarget.Default,
                state with
                {
                    TrackedOperationProcessId = 4000,
                    TrackedOperationProcessStartTicksUtc = 5000,
                },
                state.Revision);
        }
        harness.AppLauncher.FakeRegisteredLocation = owner.LayoutPath;
        var fullName = harness.AppLauncher.FakePackageFullName!;
        harness.PackageRegistration.OnUnregisterByFullName = (_, _) =>
            harness.AppLauncher.FakePackageFullName = null;

        var removed = await harness.Runner.UnregisterOwnedPackageAsync(
            harness.Target,
            "Contoso.MyApp",
            "CN=Contoso",
            "Contoso.MyApp_abc",
            requiredDeploymentId: null,
            requiredRevision: null,
            TestContext.CancellationToken);

        Assert.AreEqual(fullName, removed!.FullName);
        Assert.AreEqual(
            (fullName, false),
            harness.PackageRegistration.UnregisterByFullNameCalls.Single());
        Assert.IsNull(harness.States.Read(WindowsSandboxTarget.Default, "dep-owner")!.Package);
        Assert.IsNull(harness.States.Read(WindowsSandboxTarget.Default, "dep-stale")!.Package);
        Assert.IsNull(
            harness.States.Read(WindowsSandboxTarget.Default, "dep-owner")!.TrackedOperationProcessId);
        Assert.IsNull(
            harness.States.Read(WindowsSandboxTarget.Default, "dep-stale")!.TrackedOperationProcessId);
    }

    [TestMethod]
    public async Task UnregisterOwnedPackage_LiteralV1ClaimPreservesPackagedHistoryWithoutReconcile()
    {
        const string Layout = @"C:\WinAppGuest\deployments\legacy-owner-layout";
        await using var harness = new Harness(_guestManaged, _stateRoot);
        await WriteLegacyStateAsync(
            "legacy-owner",
            $$"""
            {
              "schemaVersion": 1,
              "revision": 3,
              "deploymentId": "legacy-owner",
              "targetEpoch": "{{Epoch.Value}}",
              "dirty": false,
              "desired": [],
              "package": {
                "packageName": "Contoso.MyApp",
                "publisher": "CN=Contoso",
                "packageFamilyName": "Contoso.MyApp_abc",
                "registeredLocation": "C:\\WinAppGuest\\deployments\\legacy-owner-layout"
              }
            }
            """);
        harness.AppLauncher.FakeRegisteredLocation = Layout;
        harness.PackageRegistration.OnUnregisterByFullName = (_, _) =>
            harness.AppLauncher.FakePackageFullName = null;

        await harness.Runner.UnregisterOwnedPackageAsync(
            harness.Target,
            "Contoso.MyApp",
            "CN=Contoso",
            "Contoso.MyApp_abc",
            requiredDeploymentId: null,
            requiredRevision: null,
            TestContext.CancellationToken);

        var cleared = harness.States.Read(WindowsSandboxTarget.Default, "legacy-owner")!;
        Assert.IsNull(cleared.Package);
        Assert.IsTrue(cleared.WasPackaged);
    }

    [TestMethod]
    public async Task UnregisterOwnedPackage_WhenWindowsStillReportsRegistration_PreservesAllClaims()
    {
        await WriteHostFileAsync("app.exe", "binary");
        await using var harness = new Harness(_guestManaged, _stateRoot);
        var owner = await CreateOwnedDeploymentAsync(harness, "dep-owner");
        await CreateOwnedDeploymentAsync(harness, "dep-stale");
        harness.AppLauncher.FakeRegisteredLocation = owner.LayoutPath;

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            harness.Runner.UnregisterOwnedPackageAsync(
                harness.Target,
                "Contoso.MyApp",
                "CN=Contoso",
                "Contoso.MyApp_abc",
                requiredDeploymentId: null,
                requiredRevision: null,
                TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.PackageConflict, failure.Error.Code);
        Assert.IsNotNull(harness.States.Read(WindowsSandboxTarget.Default, "dep-owner")!.Package);
        Assert.IsNotNull(harness.States.Read(WindowsSandboxTarget.Default, "dep-stale")!.Package);
    }

    [TestMethod]
    public async Task UnregisterOwnedPackage_OlderRevisionCannotRemoveNewerRegistration()
    {
        await WriteHostFileAsync("app.exe", "binary");
        await using var harness = new Harness(_guestManaged, _stateRoot);
        var deployment = await CreateOwnedDeploymentAsync(harness, "dep-owner");
        var older = harness.States.Read(WindowsSandboxTarget.Default, "dep-owner")!;
        harness.Runner.CommitPackage(
            WindowsSandboxTarget.Default,
            older,
            Ownership(deployment.LayoutPath));
        harness.AppLauncher.FakeRegisteredLocation = deployment.LayoutPath;

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            harness.Runner.UnregisterOwnedPackageAsync(
                harness.Target,
                "Contoso.MyApp",
                "CN=Contoso",
                "Contoso.MyApp_abc",
                older.DeploymentId,
                older.Revision,
                TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.PackageConflict, failure.Error.Code);
        Assert.HasCount(0, harness.PackageRegistration.UnregisterByFullNameCalls);
        Assert.IsNotNull(harness.States.Read(WindowsSandboxTarget.Default, "dep-owner")!.Package);
    }

    // ---- Stop before redeploy -------------------------------------------------------

    [TestMethod]
    public async Task Deploy_FirstDeploymentForAnIdentity_NeverAsksToStopAnything()
    {
        await WriteHostFileAsync("app.exe", "v1");

        await using var harness = new Harness(_guestManaged, _stateRoot);

        await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken);

        Assert.AreEqual(0, harness.AppLauncher.StopPackageCalls.Count);
    }

    [TestMethod]
    public async Task Deploy_RedeployingAPackagedApp_StopsItsPreviousProcessesEveryTime()
    {
        await WriteHostFileAsync("app.exe", "v1");

        await using var harness = new Harness(_guestManaged, _stateRoot);

        var deployment = await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken);

        harness.Runner.CommitPackage(WindowsSandboxTarget.Default, deployment.State, Ownership(deployment.LayoutPath));

        // The guest's simulated live registration: genuinely this deployment's own layout, so its
        // own redeploy below is allowed to stop it.
        harness.AppLauncher.FakeRegisteredLocation = deployment.LayoutPath;

        // Unchanged rerun: this used to leave the previous instance running unstopped, which is
        // exactly what let a second one launch alongside it.
        await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken);

        CollectionAssert.AreEqual(
            new[] { harness.AppLauncher.FakePackageFullName }, harness.AppLauncher.StopPackageCalls);

        // Editing the output and rerunning must stop it too, not just an unchanged rerun.
        await WriteHostFileAsync("app.exe", "v2");

        await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken);

        CollectionAssert.AreEqual(
            new[] { harness.AppLauncher.FakePackageFullName, harness.AppLauncher.FakePackageFullName },
            harness.AppLauncher.StopPackageCalls);
    }

    /// <summary>
    /// Two deployments built from different source paths (so different deployment IDs) can share
    /// a package identity. Deployment B recording ownership under the same family as deployment A
    /// -- because, for example, its own registration attempt failed after B optimistically
    /// committed the ownership record -- must never let B's retry terminate A's genuinely running,
    /// unrelated application.
    /// </summary>
    [TestMethod]
    public async Task Deploy_RepairsAStalePreRegistrationJournalWithoutStoppingTheActualOwner()
    {
        await WriteHostFileAsync("app.exe", "v1");

        await using var harness = new Harness(_guestManaged, _stateRoot);

        // Deployment A: registered and genuinely running from its own layout.
        var depA = await harness.Runner.DeployAsync(
            harness.Target, "dep-a", new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken);
        harness.Runner.CommitPackage(WindowsSandboxTarget.Default, depA.State, Ownership(depA.LayoutPath));
        harness.AppLauncher.FakeRegisteredLocation = depA.LayoutPath;

        // Deployment B: a different source path, same package identity (Ownership() always uses
        // the same family name), recording ownership from *its own* layout -- not A's -- exactly
        // as it would if B's own registration attempt had failed after this optimistic commit.
        var depB = await harness.Runner.DeployAsync(
            harness.Target, "dep-b", new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken);
        harness.Runner.CommitPackage(WindowsSandboxTarget.Default, depB.State, Ownership(depB.LayoutPath));

        await WriteHostFileAsync("app.exe", "v2");

        await harness.Runner.ReconcilePackageBeforeRegistrationAsync(
            harness.Target,
            "Contoso.MyApp",
            "CN=Contoso",
            "Contoso.MyApp_abc",
            TestContext.CancellationToken);
        var retried = await harness.Runner.DeployAsync(
            harness.Target, "dep-b", new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken);

        // A's legitimate registration was never touched. B's false journal was cleared before its
        // payload was safely updated, so the caller can proceed to retry registration.
        Assert.AreEqual(0, harness.AppLauncher.StopPackageCalls.Count);
        Assert.AreEqual("v2", await File.ReadAllTextAsync(
            TestPaths.Under(_guestManaged, "deployments", "dep-b", "app.exe"), TestContext.CancellationToken));
        Assert.IsNotNull(harness.States.Read(WindowsSandboxTarget.Default, "dep-a")!.Package);
        Assert.IsNull(retried.State.Package);
    }

    [TestMethod]
    public async Task Deploy_WhenStoppingThePreviousPackageCannotBeProven_RefusesBeforeMutatingAnything()
    {
        await WriteHostFileAsync("app.exe", "v1");

        await using var harness = new Harness(_guestManaged, _stateRoot);

        var deployment = await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken);

        harness.Runner.CommitPackage(WindowsSandboxTarget.Default, deployment.State, Ownership(deployment.LayoutPath));
        harness.AppLauncher.FakeRegisteredLocation = deployment.LayoutPath;

        await WriteHostFileAsync("app.exe", "v2");
        harness.AppLauncher.StopPackageProcessesFailure = new InvalidOperationException("still running");

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            harness.Runner.DeployAsync(
                harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.StaleHandle, failure.Error.Code);

        // The refusal happened before anything was written: the guest's copy is still the old
        // build, never left half up-to-date by a redeploy that could not prove the app was stopped.
        Assert.AreEqual("v1", await File.ReadAllTextAsync(
            TestPaths.Under(_guestManaged, "deployments", "dep-1", "app.exe"), TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Deploy_ForAnUnpackagedDeployment_AsksTheGuestToStopThePreviouslyTrackedProcess()
    {
        await WriteHostFileAsync("app.exe", "v1");

        await using var harness = new Harness(_guestManaged, _stateRoot);

        var deployment = await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken);

        var run = harness.Runner.RunAsync(
            harness.Target,
            deployment.State,
            new GuestExecRequest { Executable = "app.exe", Arguments = [] },
            new GuestExecCallbacks(),
            TestContext.CancellationToken);

        var process = await harness.Processes.WaitForNextAsync(TestContext.CancellationToken);
        process.Exit(0);
        await run;

        var stopCalls = new List<(int ProcessId, long StartTicksUtc)>();
        harness.Server.StopTrackedProcessImpl = (pid, ticks) =>
        {
            stopCalls.Add((pid, ticks));
            return GuestCommandServer.ProcessStopOutcome.Stopped;
        };

        await WriteHostFileAsync("app.exe", "v2");

        await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken);

        Assert.AreEqual(1, stopCalls.Count);
        Assert.AreEqual(process.ProcessId, stopCalls[0].ProcessId);
        Assert.AreEqual(process.StartTicksUtc, stopCalls[0].StartTicksUtc);
    }

    [TestMethod]
    public async Task Deploy_LiteralV1ProcessFieldsStillStopThePreviousInstance()
    {
        await WriteHostFileAsync("app.exe", "v1");
        await using var harness = new Harness(_guestManaged, _stateRoot);
        await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken);

        const int processId = 4312;
        const long startTicksUtc = 638_900_000_000_000_000;
        await WriteLegacyStateAsync(
            "dep-1",
            $$"""
            {
              "schemaVersion": 1,
              "revision": 7,
              "deploymentId": "dep-1",
              "targetEpoch": "{{Epoch.Value}}",
              "dirty": false,
              "desired": [],
              "processId": {{processId}},
              "processStartTicksUtc": {{startTicksUtc}}
            }
            """);

        var stopCalls = new List<(int ProcessId, long StartTicksUtc)>();
        harness.Server.StopTrackedProcessImpl = (pid, ticks) =>
        {
            stopCalls.Add((pid, ticks));
            return GuestCommandServer.ProcessStopOutcome.Stopped;
        };
        await WriteHostFileAsync("app.exe", "v2");

        await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken);

        CollectionAssert.AreEqual(
            new[] { (processId, startTicksUtc) },
            stopCalls);
    }

    [TestMethod]
    public async Task Deploy_WhenTheTrackedProcessCannotBeProvenStopped_RefusesBeforeMutatingAnything()
    {
        await WriteHostFileAsync("app.exe", "v1");

        await using var harness = new Harness(_guestManaged, _stateRoot);

        var deployment = await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken);

        var run = harness.Runner.RunAsync(
            harness.Target,
            deployment.State,
            new GuestExecRequest { Executable = "app.exe", Arguments = [] },
            new GuestExecCallbacks(),
            TestContext.CancellationToken);

        var process = await harness.Processes.WaitForNextAsync(TestContext.CancellationToken);
        process.Exit(0);
        await run;

        harness.Server.StopTrackedProcessImpl = (_, _) => GuestCommandServer.ProcessStopOutcome.Unproven;

        await WriteHostFileAsync("app.exe", "v2");

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            harness.Runner.DeployAsync(
                harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.StaleHandle, failure.Error.Code);

        Assert.AreEqual("v1", await File.ReadAllTextAsync(
            TestPaths.Under(_guestManaged, "deployments", "dep-1", "app.exe"), TestContext.CancellationToken));
    }

    // ---- --clean layout ordering -----------------------------------------------------

    [TestMethod]
    public async Task Deploy_Clean_WhenReconciliationFails_NeverTouchesTheRegistrationLayout()
    {
        await WriteHostFileAsync("app.exe", "binary");

        await using var harness = new Harness(_guestManaged, _stateRoot);

        var deployment = await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: true, TestContext.CancellationToken);

        // A registration layout the first run produced.
        var layoutDirectory = TestPaths.Under(_guestManaged, "deployments", "dep-1-layout");
        Directory.CreateDirectory(layoutDirectory);
        await File.WriteAllTextAsync(
            TestPaths.Under(layoutDirectory, "appxmanifest.xml"), "<Package/>", TestContext.CancellationToken);

        // Lock the payload file the next clean reconciliation must delete, simulating the still-open
        // handle a `--clean` interrupted by a running process would hit.
        var payloadFile = TestPaths.Under(_guestManaged, "deployments", "dep-1", "app.exe");
        await using (new FileStream(payloadFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
                harness.Runner.DeployAsync(
                    harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: true, TestContext.CancellationToken));

            Assert.AreEqual(ExecutionTargetErrorCodes.TransferInterrupted, failure.Error.Code);
        }

        // The registration layout and its manifest must still be there: the old ordering wiped the
        // layout *before* attempting the payload delete above, which is what left it missing.
        Assert.IsTrue(File.Exists(TestPaths.Under(layoutDirectory, "appxmanifest.xml")));
    }

    [TestMethod]
    public async Task Deploy_Clean_WhenLayoutCleanupIsInterruptedByALockedFile_PersistsDirtyTruthfully()
    {
        await WriteHostFileAsync("app.exe", "binary");

        await using var harness = new Harness(_guestManaged, _stateRoot);

        var deployment = await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: true, TestContext.CancellationToken);

        var owned = harness.Runner.CommitPackage(WindowsSandboxTarget.Default, deployment.State, Ownership(deployment.LayoutPath));
        Assert.IsFalse(owned.Dirty);
        harness.AppLauncher.FakeRegisteredLocation = deployment.LayoutPath;

        // A registration layout the first run produced, with more than one entry so a partial,
        // non-transactional delete has something to prove: one file survives the interrupted
        // cleanup below, the other does not.
        var layoutDirectory = TestPaths.Under(_guestManaged, "deployments", "dep-1-layout");
        Directory.CreateDirectory(layoutDirectory);
        await File.WriteAllTextAsync(
            TestPaths.Under(layoutDirectory, "appxmanifest.xml"), "<Package/>", TestContext.CancellationToken);
        await File.WriteAllTextAsync(
            TestPaths.Under(layoutDirectory, "resources.pri"), "pri", TestContext.CancellationToken);

        var lockedFile = TestPaths.Under(layoutDirectory, "resources.pri");

        await using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // The payload itself reconciles cleanly; only the registration-layout cleanup that runs
            // afterward, inside the same dirty window, is interrupted.
            var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
                harness.Runner.DeployAsync(
                    harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: true, TestContext.CancellationToken));

            Assert.AreEqual(ExecutionTargetErrorCodes.TransferInterrupted, failure.Error.Code);
        }

        // Directory.Delete(recursive: true) is not transactional: the unlocked entry is already
        // gone even though the call as a whole failed. This is what makes trusting a "clean" state
        // here dangerous, and exactly why the flag must not have flipped.
        Assert.IsFalse(File.Exists(TestPaths.Under(layoutDirectory, "appxmanifest.xml")));
        Assert.IsTrue(File.Exists(lockedFile));

        var persisted = harness.States.Read(WindowsSandboxTarget.Default, "dep-1");
        Assert.IsTrue(persisted!.Dirty, "A layout cleanup interrupted partway through must leave the deployment dirty.");

        // Package ownership must survive the failure too: unregister and the next run both still
        // need to know what this deployment registered.
        Assert.IsNotNull(persisted.Package);
        Assert.AreEqual(owned.Package!.PackageFamilyName, persisted.Package!.PackageFamilyName);

        // The next retry, once the lock is released, must repair deterministically: the same
        // deployment call succeeds and finally commits a truthfully clean state.
        var repaired = await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: true, TestContext.CancellationToken);

        Assert.IsFalse(repaired.State.Dirty);
        Assert.IsFalse(File.Exists(lockedFile));
    }

    /// <summary>
    /// A package stays registered even after an interrupted <c>--clean</c> deletes the layout
    /// folder it was registered from -- the registration entry and the files on disk are two
    /// separate things. A retry redeploying that same deployment must still be able to prove it
    /// owns the registration (the package manager's recorded location matches this deployment's
    /// own, whether or not anything still exists there) and both stop whatever might still be
    /// running and repair the layout, rather than fail every time before it gets that far.
    /// </summary>
    [TestMethod]
    public async Task Deploy_WhenTheLayoutWasDeletedButThePackageIsStillRegisteredFromIt_StopAndRepairSucceed()
    {
        await WriteHostFileAsync("app.exe", "v1");

        await using var harness = new Harness(_guestManaged, _stateRoot);

        var deployment = await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: true, TestContext.CancellationToken);

        harness.Runner.CommitPackage(WindowsSandboxTarget.Default, deployment.State, Ownership(deployment.LayoutPath));

        // The package manager's own record of where this deployment registered from, unaffected by
        // whatever winapp has since done to the files themselves -- exactly what
        // Package.InstalledPath keeps reporting even after the folder underneath it is gone.
        harness.AppLauncher.FakeRegisteredLocation = deployment.LayoutPath;

        // A registration layout a prior real run produced, then simulate an interrupted `--clean`
        // (or an operator deleting the folder): the directory is gone from the guest, but the
        // package stays registered from it.
        var layoutDirectory = TestPaths.Under(_guestManaged, "deployments", "dep-1-layout");
        Directory.CreateDirectory(layoutDirectory);
        await File.WriteAllTextAsync(
            TestPaths.Under(layoutDirectory, "appxmanifest.xml"), "<Package/>", TestContext.CancellationToken);
        Directory.Delete(layoutDirectory, recursive: true);
        Assert.IsFalse(Directory.Exists(layoutDirectory));

        await WriteHostFileAsync("app.exe", "v2");

        // The retry must stop the (possibly still running) previous instance -- proven purely by
        // the recorded registration location matching, never by anything on disk -- and then
        // reconcile the payload cleanly.
        var repaired = await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: true, TestContext.CancellationToken);

        CollectionAssert.AreEqual(
            new[] { harness.AppLauncher.FakePackageFullName }, harness.AppLauncher.StopPackageCalls);
        Assert.IsFalse(repaired.State.Dirty);
        Assert.AreEqual("v2", await File.ReadAllTextAsync(
            TestPaths.Under(_guestManaged, "deployments", "dep-1", "app.exe"), TestContext.CancellationToken));
    }

    /// <summary>
    /// The companion negative case: a deleted layout must never be used as an excuse to skip the
    /// ownership proof. If the currently registered location is genuinely a different one, the
    /// redeploy still refuses, exactly as it would if the folder existed.
    /// </summary>
    [TestMethod]
    public async Task Deploy_WhenTheLayoutIsDeletedAndTheRegisteredLocationIsADifferentDeployment_StillRefuses()
    {
        await WriteHostFileAsync("app.exe", "v1");

        await using var harness = new Harness(_guestManaged, _stateRoot);

        var deployment = await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: true, TestContext.CancellationToken);

        harness.Runner.CommitPackage(WindowsSandboxTarget.Default, deployment.State, Ownership(deployment.LayoutPath));

        // Registered from a different deployment's layout entirely -- the mismatch that matters,
        // independent of whether dep-1's own layout still exists.
        harness.AppLauncher.FakeRegisteredLocation = @"C:\WinAppGuest\deployments\dep-other-layout";

        var layoutDirectory = TestPaths.Under(_guestManaged, "deployments", "dep-1-layout");
        Directory.CreateDirectory(layoutDirectory);
        await File.WriteAllTextAsync(
            TestPaths.Under(layoutDirectory, "appxmanifest.xml"), "<Package/>", TestContext.CancellationToken);
        Directory.Delete(layoutDirectory, recursive: true);

        await WriteHostFileAsync("app.exe", "v2");

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            harness.Runner.DeployAsync(
                harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: true, TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.StaleHandle, failure.Error.Code);
        Assert.AreEqual(0, harness.AppLauncher.StopPackageCalls.Count);
        Assert.AreEqual("v1", await File.ReadAllTextAsync(
            TestPaths.Under(_guestManaged, "deployments", "dep-1", "app.exe"), TestContext.CancellationToken));
    }

    // ---- Strict package-inventory lookup (fail-closed on query failure) --------------

    [TestMethod]
    public async Task Deploy_WhenThePackageInventoryQueryFails_RefusesBeforeMutatingAnything()
    {
        await WriteHostFileAsync("app.exe", "v1");

        await using var harness = new Harness(_guestManaged, _stateRoot);

        var deployment = await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken);

        harness.Runner.CommitPackage(WindowsSandboxTarget.Default, deployment.State, Ownership(deployment.LayoutPath));

        await WriteHostFileAsync("app.exe", "v2");

        // A query failure (transient COM error, denied inventory read) is not the same as a query
        // that ran and confirmed nothing is registered. It must never be read as "safe to proceed".
        harness.AppLauncher.GetRegisteredPackageFailure = new InvalidOperationException("inventory unavailable");

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            harness.Runner.DeployAsync(
                harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.StaleHandle, failure.Error.Code);

        // Never even reached the termination call, since the lookup itself failed first.
        Assert.AreEqual(0, harness.AppLauncher.StopPackageCalls.Count);

        // And, as always, nothing was mutated: the guest's copy is still the old build.
        Assert.AreEqual("v1", await File.ReadAllTextAsync(
            TestPaths.Under(_guestManaged, "deployments", "dep-1", "app.exe"), TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Deploy_WhenTheInventoryConfirmsNothingIsRegistered_ProceedsSafely()
    {
        await WriteHostFileAsync("app.exe", "v1");

        await using var harness = new Harness(_guestManaged, _stateRoot);

        var deployment = await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken);

        harness.Runner.CommitPackage(WindowsSandboxTarget.Default, deployment.State, Ownership(deployment.LayoutPath));

        // The query itself succeeds and confirms nothing is registered under that family any more
        // (distinct from a query that failed): safe to treat as nothing to stop.
        harness.AppLauncher.FakePackageFullName = null;

        await WriteHostFileAsync("app.exe", "v2");

        var redeployed = await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken);

        Assert.IsFalse(redeployed.State.Dirty);
        Assert.AreEqual(0, harness.AppLauncher.StopPackageCalls.Count);
        Assert.AreEqual("v2", await File.ReadAllTextAsync(
            TestPaths.Under(_guestManaged, "deployments", "dep-1", "app.exe"), TestContext.CancellationToken));
    }

    // ---- Additive JSON -------------------------------------------------------------

    [TestMethod]
    public void AugmentGuestJson_AddsTheExecutionTargetWithoutDisturbingTheGuestPayload()
    {
        var guestPayload = """{"AUMID":"Contoso.MyApp_abc!App","ProcessId":4212}"""u8.ToArray();

        var augmented = RunCommand.Handler.TryAugmentGuestJson(
            guestPayload, TargetInfo());

        using var document = JsonDocument.Parse(augmented!);
        var root = document.RootElement;

        Assert.AreEqual("Contoso.MyApp_abc!App", root.GetProperty("AUMID").GetString());
        Assert.AreEqual(4212u, root.GetProperty("ProcessId").GetUInt32());
        Assert.IsTrue(root.GetProperty("Sandbox").GetBoolean());
        Assert.AreEqual("sandbox", root.GetProperty("ProcessScope").GetString());

        // The guest's own process ID, not the agent's child: a UI command pointed at the launcher
        // would target the wrong process.
        Assert.AreEqual("--on sandbox -a 4212", root.GetProperty("UiTargetArgs").GetString());

        var target = root.GetProperty("ExecutionTarget");
        Assert.AreEqual("sandbox", target.GetProperty("Kind").GetString());
        Assert.AreEqual("default", target.GetProperty("Id").GetString());
        Assert.AreEqual("arm64", target.GetProperty("Architecture").GetString());
        Assert.AreEqual(Epoch.Value, target.GetProperty("Epoch").GetString());
    }

    [TestMethod]
    public void AugmentGuestJson_WithoutAnApplicationProcess_OmitsTheCopyableCommand()
    {
        var guestPayload = """{"AUMID":"Contoso.MyApp_abc!App"}"""u8.ToArray();

        var augmented = RunCommand.Handler.TryAugmentGuestJson(
            guestPayload, TargetInfo());

        using var document = JsonDocument.Parse(augmented!);
        Assert.IsFalse(document.RootElement.TryGetProperty("UiTargetArgs", out _));
    }

    [TestMethod]
    public void AugmentGuestJson_NoLaunchNeverUsesTheRegistrationProcessAsAUiTarget()
    {
        var guestPayload = """{"AUMID":"Contoso.MyApp_abc!App","ProcessId":null}"""u8.ToArray();

        var augmented = RunCommand.Handler.TryAugmentGuestJson(guestPayload, TargetInfo());

        using var document = JsonDocument.Parse(augmented!);
        Assert.IsFalse(document.RootElement.TryGetProperty("UiTargetArgs", out _));
    }

    /// <summary>
    /// Losing an additive field is recoverable; handing a caller a re-encoded or truncated document
    /// is not.
    /// </summary>
    [TestMethod]
    public void AugmentGuestJson_UnparseablePayload_IsRelayedUnchanged()
    {
        Assert.IsNull(RunCommand.Handler.TryAugmentGuestJson(
            "not json at all"u8.ToArray(), TargetInfo()));
    }

    [TestMethod]
    public void AugmentGuestJson_PayloadThatOverranTheCaptureBound_IsRelayedUnchanged()
    {
        var oversized = new byte[RunCommand.Handler.MaxCapturedJsonBytes];

        Assert.IsNull(RunCommand.Handler.TryAugmentGuestJson(oversized, TargetInfo()));
    }

    [TestMethod]
    public void DirectUnpackagedJsonResult_IsAlwaysAHostScopedEnvelope()
    {
        var result = RunCommand.Handler.CreateDirectGuestResult(WindowsSandboxTarget.Default, 
            architecture: "arm64",
            epoch: "epoch-1");

        Assert.IsNull(result.ProcessId);
        Assert.IsTrue(result.Sandbox);
        Assert.AreEqual("sandbox", result.ProcessScope);
        Assert.IsNull(result.UiTargetArgs);
        Assert.AreEqual("arm64", result.ExecutionTarget!.Architecture);
        Assert.AreEqual("epoch-1", result.ExecutionTarget.Epoch);
    }

    private static ExecutionTargetInfo TargetInfo() => new()
    {
        Kind = WindowsSandboxTarget.Default.Kind,
        Id = WindowsSandboxTarget.Default.Id,
        Architecture = "arm64",
        Epoch = Epoch.Value,
    };

    private static PackageOwnership Ownership(string layoutPath) => new()
    {
        PackageName = "Contoso.MyApp",
        Publisher = "CN=Contoso",
        PackageFamilyName = "Contoso.MyApp_abc",
        RegisteredLocation = layoutPath,
    };

    private async Task<GuestDeployment> CreateOwnedDeploymentAsync(Harness harness, string deploymentId)
    {
        var deployment = await harness.Runner.DeployAsync(
            harness.Target,
            deploymentId,
            new DirectoryInfo(_hostSource),
            clean: false,
            TestContext.CancellationToken);
        harness.Runner.CommitPackage(
            WindowsSandboxTarget.Default,
            deployment.State,
            Ownership(deployment.LayoutPath));
        return deployment;
    }

    private Task<PackageOwnershipReconciliation> ReconcileForUnregisterAsync(Harness harness) =>
        harness.Runner.ReconcilePackageForUnregisterAsync(
            harness.Target,
            "Contoso.MyApp",
            "CN=Contoso",
            "Contoso.MyApp_abc",
            TestContext.CancellationToken);

    private static ExecutionTargetCapabilities Capabilities(string? managedRoot = ManagedRoot) => new()
    {
        Architecture = "arm64",
        SupportsInteractiveDesktop = true,
        SupportsRealInput = true,
        SupportsScreenCapture = true,
        CooperativeUiTurnsVersion = 1,
        SupportsInternalSystemSetup = true,
        PersistentStorage = false,
        ManagedRoot = managedRoot,
    };

    private async Task WriteHostFileAsync(string relativePath, string contents)
    {
        var path = TestPaths.Under(_hostSource, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents, TestContext.CancellationToken);
    }

    private async Task WriteLegacyStateAsync(string deploymentId, string json)
    {
        var directory = TestPaths.Under(
            _stateRoot,
            WindowsSandboxTarget.Default.StateKey,
            DeploymentStateStore.DeploymentsFolder);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            TestPaths.Under(directory, $"{deploymentId}.json"),
            json,
            TestContext.CancellationToken);
    }

    /// <summary>Host runner and guest server sharing one in-memory transport and a real file service.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        /// <summary>Stands in for the guest's own winapp binary.</summary>
        public const string GuestWinappPath = @"C:\WinAppGuest\agent\current\winapp.exe";

        private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(60));
        private readonly Task _serverTask;
        private readonly GuestCommandChannel _channel;
        private readonly TargetMutationLease _mutationLease;

        public Harness(string guestManagedRoot, string stateRoot, string? guestWinapp = GuestWinappPath)
        {
            var pair = new LoopbackTransportPair();
            AppLauncher = new FakeAppLauncherService();
            PackageRegistration = new FakePackageRegistrationService();

            Server = new GuestCommandServer(
                pair.Guest,
                Epoch,
                Processes,
                new StaticGuestSessionProbe(new GuestSessionInfo(1, "WinSta0", true)),
                new GuestAgentIdentity("1.0.0", "hash", "arm64", 1, 1),
                new GuestFileService(guestManagedRoot),
                guestWinapp,
                AppLauncher,
                PackageRegistration);

            _serverTask = Server.RunAsync(_cancellation.Token);

            _channel = new GuestCommandChannel(pair.Host, Epoch);
            _channel.Start();

            States = new DeploymentStateStore(new SandboxTestStateDirectoryProvider(stateRoot));
            Runner = new GuestApplicationRunner(new TargetDeploymentService(States));

            // DeployAsync requires the caller to already hold the mutation lease, exactly as
            // production callers do via ExecutionTargetOrchestrator.PrepareAsync(Mutating). The
            // harness stands in for that caller with a real, held lease over its own scratch lock
            // file.
            var mutationLockPath = TestPaths.TempFile("deployment-mutation-lock", ".lock");
            var mutationStream = new FileStream(
                mutationLockPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            _mutationLease = new TargetMutationLease(mutationStream, wasAbandoned: false);

            // The managed root is a guest-side value, so the harness reports a stable one rather
            // than the host temp folder the fake file service actually writes to.
            Target = new PreparedTarget(WindowsSandboxTarget.Default, _channel, Epoch, Capabilities(), Reused: false, MutationLease: _mutationLease);
        }

        public FakeGuestProcessHostFactory Processes { get; } = new();

        public FakeAppLauncherService AppLauncher { get; }

        public FakePackageRegistrationService PackageRegistration { get; }

        public GuestCommandServer Server { get; }

        public DeploymentStateStore States { get; }

        public GuestApplicationRunner Runner { get; }

        public PreparedTarget Target { get; }

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

            _mutationLease.Dispose();
            await _channel.DisposeAsync();
            _cancellation.Dispose();
        }
    }

    /// <summary>A state directory provider rooted at a test-owned folder.</summary>
    private sealed class SandboxTestStateDirectoryProvider(string root) : ITargetStateDirectoryProvider
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
}
