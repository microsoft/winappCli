// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;

using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

/// <summary>
/// Runtime provisioning driven end to end through the real command channel into a real guest file
/// service, with only the transport and the guest child process faked
/// (spec §"Runtime provisioning", acceptance criterion 13).
/// </summary>
/// <remarks>
/// Everything the spec asks of this step is observable here without Windows Sandbox: staging into
/// the guest, installing under the mutation lock, verifying the whole graph before every launch,
/// journaling a partial install and repairing it, refusing to downgrade a shared runtime, and
/// failing with the unsatisfied requirement rather than mutating the environment destructively.
/// </remarks>
[TestClass]
public partial class TargetRuntimeServiceTests
{
    private const string RuntimePackage = "Microsoft.WindowsAppRuntime.1.8";
    private const string RequiredVersion = "8000.675.1142.0";
    private const string Publisher = "CN=Microsoft Corporation";

    private static readonly ExecutionTargetRef Target = WindowsSandboxTarget.Default;
    private static readonly ExecutionTargetEpoch Epoch = ExecutionTargetEpoch.Create("sandbox-1", "nonce-a");

    private string _root = null!;
    private string _hostSource = null!;
    private string _hostCache = null!;
    private string _guestManaged = null!;
    private string _stateRoot = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = TestPaths.TempRoot(nameof(TargetRuntimeServiceTests));
        _hostSource = TestPaths.Under(_root, "host");
        _hostCache = TestPaths.Under(_root, "cache");
        _guestManaged = TestPaths.Under(_root, "guest");
        _stateRoot = TestPaths.Under(_root, "state");

        Directory.CreateDirectory(_hostSource);
        Directory.CreateDirectory(_hostCache);
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

    [TestMethod]
    public async Task Ensure_WithNothingToProvision_TouchesNeitherGuestNorState()
    {
        await File.WriteAllTextAsync(
            TestPaths.Under(_hostSource, "app.exe"), "native", TestContext.CancellationToken);

        await using var harness = new Harness(_guestManaged, _stateRoot);

        var result = await harness.EnsureAsync(_hostSource, TestContext.CancellationToken);

        Assert.IsTrue(result.Requirements.IsEmpty);
        Assert.IsTrue(result.AlreadySatisfied);
        Assert.AreEqual(0, harness.GuestInvocations);
        Assert.IsNull(harness.ReadState());
    }

    [TestMethod]
    public async Task Ensure_StagesThePayloadIntoTheGuestAndInstallsItThere()
    {
        await WriteManifestAsync();
        var payload = await WritePayloadAsync(RuntimePackage, RequiredVersion);

        await using var harness = new Harness(_guestManaged, _stateRoot);
        harness.Resolver.Payloads[RuntimePackage] = payload;
        harness.GuestPackages.Installs(RuntimePackage, RequiredVersion);

        var result = await harness.EnsureAsync(_hostSource, TestContext.CancellationToken);

        Assert.IsFalse(result.AlreadySatisfied);
        Assert.IsTrue(result.Report!.Satisfied);
        Assert.IsTrue(result.Report.Items.Single().Installed);

        // The payload has to actually be in the guest's managed runtime scope, under a name the
        // host derived, and the plan beside it.
        var scope = TestPaths.Under(_guestManaged, "runtimes", result.Requirements.PlanId);
        Assert.IsTrue(File.Exists(Path.Join(scope, $"{RuntimePackage}_{RequiredVersion}_x64.msix")));
        Assert.IsTrue(File.Exists(Path.Join(scope, RuntimeProvisionPlan.FileName)));

        // Installed from the staged copy, never from a host path the guest cannot see.
        Assert.AreEqual(1, harness.GuestPackages.InstallPackageCalls.Count);
        StringAssert.StartsWith(harness.GuestPackages.InstallPackageCalls[0], scope);

        var state = harness.ReadState();
        Assert.IsFalse(state!.Dirty);
        Assert.AreEqual(result.Requirements.PlanId, state.PlanId);
    }

    [TestMethod]
    public async Task Ensure_WhenCallerAlreadyReleasedTheMutationLease_ThrowsRatherThanMutateUnprotected()
    {
        // EnsureAsync no longer acquires its own mutation lock: it trusts the caller's held lease
        // from ExecutionTargetOrchestrator.PrepareAsync(Mutating). If a caller released that lease
        // early (a programming error) and then, for a non-empty requirement set, still asked this
        // to provision runtimes, it must fail fast via RequireMutationLease() rather than either
        // deadlocking on a lock it no longer owns (the old per-call acquisition would have,
        // reacquiring against its own now-outer caller) or silently mutating the guest with no
        // lock held at all.
        await WriteManifestAsync();
        var payload = await WritePayloadAsync(RuntimePackage, RequiredVersion);

        await using var harness = new Harness(_guestManaged, _stateRoot);
        harness.Resolver.Payloads[RuntimePackage] = payload;
        harness.GuestPackages.Installs(RuntimePackage, RequiredVersion);

        harness.Prepared.ReleaseMutationLease();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => harness.EnsureAsync(_hostSource, TestContext.CancellationToken));

        // Confirms this failed before touching anything: no guest child was started and no
        // provisioning record was written.
        Assert.AreEqual(0, harness.GuestInvocations);
        Assert.IsNull(harness.ReadState());
    }

    [TestMethod]
    public async Task Ensure_WhenTheGuestAlreadyHasANewerRuntime_InstallsNothing()
    {
        await WriteManifestAsync();
        var payload = await WritePayloadAsync(RuntimePackage, RequiredVersion);

        await using var harness = new Harness(_guestManaged, _stateRoot);
        harness.Resolver.Payloads[RuntimePackage] = payload;
        harness.GuestPackages.Present[RuntimePackage] = "8000.999.0.0";

        var result = await harness.EnsureAsync(_hostSource, TestContext.CancellationToken);

        // Never downgrades or reinstalls over a shared runtime another application in the same guest
        // may already be using.
        Assert.AreEqual(0, harness.GuestPackages.InstallPackageCalls.Count);
        Assert.IsTrue(result.Report!.Satisfied);
        Assert.IsFalse(result.Report.Items.Single().Installed);
    }

    [TestMethod]
    public async Task Ensure_SecondRunOfTheSamePlan_VerifiesTheGraphAgainWithoutReinstalling()
    {
        await WriteManifestAsync();
        var payload = await WritePayloadAsync(RuntimePackage, RequiredVersion);

        await using var harness = new Harness(_guestManaged, _stateRoot);
        harness.Resolver.Payloads[RuntimePackage] = payload;
        harness.GuestPackages.Installs(RuntimePackage, RequiredVersion);

        await harness.EnsureAsync(_hostSource, TestContext.CancellationToken);
        var second = await harness.EnsureAsync(_hostSource, TestContext.CancellationToken);

        // A clean journal says what winapp did, not what the guest currently has: `sandbox exec` can
        // change package state inside the same generation, so the graph is re-verified every time.
        Assert.AreEqual(2, harness.GuestInvocations);
        Assert.IsTrue(second.Report!.Satisfied);

        // Re-verifying is not reinstalling. Nothing was installed, and the warm pass says so.
        Assert.IsTrue(second.AlreadySatisfied);
        Assert.AreEqual(1, harness.GuestPackages.InstallPackageCalls.Count);
    }

    [TestMethod]
    public async Task Ensure_WhenTheGuestGraphChangedUnderneathAVerifiedRecord_FailsRatherThanLaunching()
    {
        await WriteManifestAsync();
        var payload = await WritePayloadAsync(RuntimePackage, RequiredVersion);

        await using var harness = new Harness(_guestManaged, _stateRoot);
        harness.Resolver.Payloads[RuntimePackage] = payload;
        harness.GuestPackages.Installs(RuntimePackage, RequiredVersion);

        var first = await harness.EnsureAsync(_hostSource, TestContext.CancellationToken);
        Assert.IsTrue(first.Report!.Satisfied);
        Assert.IsFalse(harness.ReadState()!.Dirty);

        // Something removed the runtime in this same generation — exactly what `sandbox exec` makes
        // possible. The clean record is now a statement about the past, not about the guest.
        harness.GuestPackages.Registrations.Clear();
        harness.GuestPackages.Present.Clear();
        harness.Resolver.Payloads.Clear();

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => harness.EnsureAsync(_hostSource, TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.RuntimeProvisionFailed, failure.Error.Code);
        StringAssert.Contains(failure.Error.Message, RuntimePackage);
    }

    [TestMethod]
    public async Task Ensure_InANewGeneration_ProvisionsAgain()
    {
        await WriteManifestAsync();
        var payload = await WritePayloadAsync(RuntimePackage, RequiredVersion);

        await using var first = new Harness(_guestManaged, _stateRoot);
        first.Resolver.Payloads[RuntimePackage] = payload;
        first.GuestPackages.Installs(RuntimePackage, RequiredVersion);
        await first.EnsureAsync(_hostSource, TestContext.CancellationToken);

        await using var second = new Harness(
            _guestManaged, _stateRoot, ExecutionTargetEpoch.Create("sandbox-1", "nonce-b"));
        second.Resolver.Payloads[RuntimePackage] = payload;
        second.GuestPackages.Installs(RuntimePackage, RequiredVersion);

        var result = await second.EnsureAsync(_hostSource, TestContext.CancellationToken);

        // Windows Sandbox does not persist, so a record from a previous generation describes a guest
        // that no longer exists.
        Assert.IsFalse(result.AlreadySatisfied);
        Assert.AreEqual(1, second.GuestInvocations);
    }

    [TestMethod]
    public async Task Ensure_AfterAPartialInstall_RebuildsTheStagingAreaBeforeLaunch()
    {
        await WriteManifestAsync();
        var payload = await WritePayloadAsync(RuntimePackage, RequiredVersion);

        await using var harness = new Harness(_guestManaged, _stateRoot);
        harness.Resolver.Payloads[RuntimePackage] = payload;

        // First pass fails partway: the guest reports the graph unsatisfied.
        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => harness.EnsureAsync(_hostSource, TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.RuntimeProvisionFailed, failure.Error.Code);

        // The journal is what makes the next run repair rather than trust a half-applied pass.
        var afterFailure = harness.ReadState();
        Assert.IsTrue(afterFailure!.Dirty);

        var planId = RuntimeRequirementDiscovery
            .Discover(new DirectoryInfo(_hostSource), "x64").PlanId;

        var stale = TestPaths.Under(_guestManaged, "runtimes", planId, "leftover.msix");
        await File.WriteAllTextAsync(stale, "half-transferred", TestContext.CancellationToken);

        harness.GuestPackages.Installs(RuntimePackage, RequiredVersion);
        var repaired = await harness.EnsureAsync(_hostSource, TestContext.CancellationToken);

        Assert.IsTrue(repaired.Report!.Satisfied);
        Assert.IsFalse(File.Exists(stale), "repair must rebuild the staging area rather than reconcile against it");
        Assert.IsFalse(harness.ReadState()!.Dirty);
    }

    [TestMethod]
    public async Task Ensure_WithNoPayloadAndNothingInTheGuest_FailsNamingTheRequirement()
    {
        await WriteManifestAsync(("Microsoft.VCLibs.140.00.UWPDesktop", "14.0.33728.0"));

        await using var harness = new Harness(_guestManaged, _stateRoot);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => harness.EnsureAsync(_hostSource, TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.RuntimeProvisionFailed, failure.Error.Code, Describe(failure));

        // Naming the unsatisfied constraint is the whole point: the alternative is an app that
        // starts and dies with a dependency error nobody can act on.
        StringAssert.Contains(failure.Error.Message, "Microsoft.VCLibs.140.00.UWPDesktop", Describe(failure));
        StringAssert.Contains(failure.Error.Message, "14.0.33728.0");
        Assert.AreEqual(0, harness.GuestPackages.InstallPackageCalls.Count);
    }

    [TestMethod]
    public async Task Ensure_InstallsAndVerifiesTheWholeWindowsAppRuntimeInventory()
    {
        await WriteManifestAsync();

        // What a real cached runtime directory holds beside the declared Framework. The DDLM's name
        // and the Singleton's version deliberately do not follow from the declared dependency —
        // they are read from each package's own manifest, and both must be verified.
        var framework = await WritePayloadAsync(RuntimePackage, RequiredVersion);
        var ddlm = await WritePayloadAsync("Microsoft.WinAppRuntime.DDLM.8000.675.1142.0-x6", RequiredVersion);
        var main = await WritePayloadAsync("MicrosoftCorporationII.WinAppRuntime.Main.1.8", RequiredVersion);
        var singleton = await WritePayloadAsync("MicrosoftCorporationII.WinAppRuntime.Singleton", "8000.675.1142.0");

        await using var harness = new Harness(_guestManaged, _stateRoot);
        harness.Resolver.Payloads[RuntimePackage] = framework;
        harness.Resolver.Derived[RuntimePackage] = [ddlm, main, singleton];

        foreach (var payload in (RuntimePayload[])[framework, ddlm, main, singleton])
        {
            harness.GuestPackages.Installs(payload.PackageName, payload.Version);
        }

        var result = await harness.EnsureAsync(_hostSource, TestContext.CancellationToken);

        // A Framework with no DDLM beside it is a runtime a WinUI app still cannot start against,
        // so all four are staged, installed, and reported on.
        Assert.AreEqual(4, harness.GuestPackages.InstallPackageCalls.Count);
        Assert.IsTrue(result.Report!.Satisfied);
        Assert.AreEqual(4, result.Report.Items.Count);

        CollectionAssert.AreEquivalent(
            new[] { framework.PackageName, ddlm.PackageName, main.PackageName, singleton.PackageName },
            result.Report.Items.Select(item => item.Name).ToArray());
    }

    [TestMethod]
    public async Task Ensure_WhenOnlyAnotherArchitectureIsRegistered_DoesNotAcceptIt()
    {
        await WriteManifestAsync();

        await using var harness = new Harness(_guestManaged, _stateRoot);

        // Same name, same version, wrong architecture. An unfiltered fallback would call this a
        // match and the app would fail to register for a reason nothing reported.
        harness.GuestPackages.Registrations.Add(
            new WinApp.Cli.Services.RegisteredPackageIdentity(RuntimePackage, RequiredVersion, Publisher, "x86"));

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => harness.EnsureAsync(_hostSource, TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.RuntimeProvisionFailed, failure.Error.Code);
        StringAssert.Contains(Describe(failure), "x86", Describe(failure));
    }

    [TestMethod]
    public async Task Ensure_WhenAnArchitectureNeutralPackageIsRegistered_AcceptsIt()
    {
        await WriteManifestAsync();

        await using var harness = new Harness(_guestManaged, _stateRoot);

        // Neutral is the one architecture that satisfies any requirement, and refusing it would
        // reinstall a package that is already correct.
        harness.GuestPackages.Registrations.Add(
            new WinApp.Cli.Services.RegisteredPackageIdentity(RuntimePackage, RequiredVersion, Publisher, "neutral"));

        var result = await harness.EnsureAsync(_hostSource, TestContext.CancellationToken);

        Assert.IsTrue(result.Report!.Satisfied);
        Assert.AreEqual(0, harness.GuestPackages.InstallPackageCalls.Count);
    }

    [TestMethod]
    public async Task Ensure_WhenTheRegisteredPublisherDiffers_DoesNotAcceptIt()
    {
        await WriteManifestAsync();

        await using var harness = new Harness(_guestManaged, _stateRoot);

        // Windows resolves a framework dependency on (name, publisher). A same-named package from
        // someone else is a different package, however new it is.
        harness.GuestPackages.Registrations.Add(
            new WinApp.Cli.Services.RegisteredPackageIdentity(
                RuntimePackage, "9999.0.0.0", "CN=Someone Else", "x64"));

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => harness.EnsureAsync(_hostSource, TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.RuntimeProvisionFailed, failure.Error.Code);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Ensure_RecordsWhatEachPhaseCost()
    {
        await WriteManifestAsync();
        var payload = await WritePayloadAsync(RuntimePackage, RequiredVersion);

        var telemetry = new RecordingTelemetry();
        WinApp.Cli.Telemetry.TelemetryFactory.SetOverrideForTesting(telemetry);

        try
        {
            await using var harness = new Harness(_guestManaged, _stateRoot);
            harness.Resolver.Payloads[RuntimePackage] = payload;
            harness.GuestPackages.Installs(RuntimePackage, RequiredVersion);

            await harness.EnsureAsync(_hostSource, TestContext.CancellationToken);
        }
        finally
        {
            WinApp.Cli.Telemetry.TelemetryFactory.SetOverrideForTesting(null);
        }

        // The spec asks for all five phases, and for nothing identifying to travel with them. The
        // telemetry override is process-wide, so sibling tests contribute too — what is asserted is
        // that every phase was recorded and that nothing but a fixed phase name ever is.
        string[] phases =
        [
            TargetRuntimeService.DiscoveryPhase,
            TargetRuntimeService.CacheResolutionPhase,
            TargetRuntimeService.TransferPhase,
            TargetRuntimeService.InstallationPhase,
            TargetRuntimeService.VerificationPhase,
        ];

        var recorded = telemetry.TimeTaken.Select(entry => entry.EventName).ToList();

        foreach (var phase in phases)
        {
            Assert.Contains(phase, recorded);
        }

        CollectionAssert.IsSubsetOf(recorded.Distinct().ToArray(), phases);
    }

    private static string Describe(ExecutionTargetException failure) =>
        string.Join("; ", failure.Error.Context?.Select(entry => $"{entry.Key}={entry.Value}") ?? []);

    private Task WriteManifestAsync(params (string Name, string MinVersion)[] dependencies)
    {
        var declared = dependencies.Length > 0 ? dependencies : [(RuntimePackage, RequiredVersion)];

        var entries = string.Concat(declared.Select(dependency =>
            $"""<PackageDependency Name="{dependency.Name}" MinVersion="{dependency.MinVersion}" Publisher="{Publisher}" />"""));

        return File.WriteAllTextAsync(
            TestPaths.Under(_hostSource, "appxmanifest.xml"),
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="Contoso.App" Publisher="CN=Contoso" Version="1.0.0.0" ProcessorArchitecture="x64" />
              <Dependencies>{entries}</Dependencies>
            </Package>
            """,
            TestContext.CancellationToken);
    }

    private Task WriteRuntimeConfigAsync(string framework, string version) =>
        File.WriteAllTextAsync(
            TestPaths.Under(_hostSource, "App.runtimeconfig.json"),
            $$"""
            { "runtimeOptions": { "framework": { "name": "{{framework}}", "version": "{{version}}" } } }
            """,
            TestContext.CancellationToken);

    private async Task<RuntimePayload> WritePayloadAsync(string name, string version)
    {
        var path = TestPaths.Under(_hostCache, $"{name}.msix");
        await File.WriteAllTextAsync(path, $"payload-{name}-{version}", TestContext.CancellationToken);
        return new RuntimePayload(new FileInfo(path), name, version, "x64", Publisher);
    }

    /// <summary>Writes a portable layout archive shaped exactly like the real one.</summary>
    /// <remarks>
    /// The content is a stand-in, but the structure is not: the guest installer publishes the
    /// versioned folders a layout carries, and getting those wrong is precisely the failure this
    /// covers.
    /// </remarks>
    private async Task<RuntimeFrameworkPayload> WriteLayoutAsync(string name, string version)
    {
        var path = TestPaths.Under(_hostCache, $"{name}_{version}.zip");

        await using (var stream = File.Create(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            await WriteEntryAsync(archive, $"shared/{name}/{version}/{name}.deps.json", "{}"u8.ToArray());

            if (name == "Microsoft.NETCore.App")
            {
                await WriteEntryAsync(archive, $"shared/{name}/{version}/hostpolicy.dll", "mz"u8.ToArray());
                await WriteEntryAsync(archive, $"shared/{name}/{version}/coreclr.dll", "mz"u8.ToArray());
                await WriteEntryAsync(
                    archive,
                    $"shared/{name}/{version}/System.Private.CoreLib.dll",
                    "mz"u8.ToArray());
                await WriteEntryAsync(archive, $"host/fxr/{version}/hostfxr.dll", "mz"u8.ToArray());
            }
            else if (name == "Microsoft.WindowsDesktop.App")
            {
                await WriteEntryAsync(archive, $"shared/{name}/{version}/WindowsBase.dll", "mz"u8.ToArray());
                await WriteEntryAsync(
                    archive,
                    $"shared/{name}/{version}/System.Windows.Forms.dll",
                    "mz"u8.ToArray());
            }
        }

        return new RuntimeFrameworkPayload(new FileInfo(path), name, version, "x64");
    }

    private static void WriteInstalledFramework(string root, string name, string version)
    {
        var directory = Path.Join(root, "shared", name, version);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Join(directory, $"{name}.deps.json"), "{}");

        if (name == "Microsoft.NETCore.App")
        {
            File.WriteAllText(Path.Join(directory, "hostpolicy.dll"), "mz");
            File.WriteAllText(Path.Join(directory, "coreclr.dll"), "mz");
            File.WriteAllText(Path.Join(directory, "System.Private.CoreLib.dll"), "mz");

            var resolver = Path.Join(root, "host", "fxr", version);
            Directory.CreateDirectory(resolver);
            File.WriteAllText(Path.Join(resolver, "hostfxr.dll"), "mz");
        }
    }

    private async Task WriteEntryAsync(ZipArchive archive, string entryPath, byte[] content)
    {
        await using var entry = archive.CreateEntry(entryPath).Open();
        await entry.WriteAsync(content, TestContext.CancellationToken);
    }
}
