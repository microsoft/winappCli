// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;
using WinApp.Cli.Services;
using WinApp.Cli.Tools;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="UpdateCommand"/>. All external effects (NuGet queries, package
/// installation, build-tool acquisition, runtime install) are faked so the command's control
/// flow — config handling, per-package update decisions, build-tools gating, and the runtime
/// step — is exercised deterministically without network or machine state.
///
/// The version-decision gate (<c>CompareVersions(latest, current) &gt; 0</c>) is pinned down
/// explicitly: only a strictly-greater "latest" rewrites winapp.yaml and triggers a reinstall,
/// while a normalized-equal or lower "latest" leaves both the persisted version and the installed
/// set untouched. The failure paths are pinned too: a latest-version lookup that throws must exit
/// non-zero and preserve the pin rather than report a false "up to date", and a cancellation
/// (Ctrl+C) mid-lookup must abort the whole command instead of being recorded as an ordinary
/// lookup failure that lets the loop keep running.
/// </summary>
[TestClass]
public class UpdateCommandTests : BaseCommandTests
{
    private FakeNugetService _fakeNuget = null!;
    private FakePackageInstallationService _fakeInstall = null!;
    private FakeWorkspaceSetupService _fakeWorkspace = null!;
    private UpdateRuntimeFake _fakeRuntime = null!;
    private FakeUpdateBuildToolsService _fakeBuildTools = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeNuget = new FakeNugetService();
        _fakeInstall = new FakePackageInstallationService();
        _fakeWorkspace = new FakeWorkspaceSetupService();
        _fakeRuntime = new UpdateRuntimeFake();
        _fakeBuildTools = new FakeUpdateBuildToolsService { BuildToolsDirectory = _ => CreateBuildToolsDir() };

        return services
            .AddSingleton<INugetService>(_fakeNuget)
            .AddSingleton<IPackageInstallationService>(_fakeInstall)
            .AddSingleton<IWorkspaceSetupService>(_fakeWorkspace)
            .AddSingleton<IWindowsAppRuntimeService>(_fakeRuntime)
            .AddSingleton<IBuildToolsService>(_fakeBuildTools);
    }

    private const string PackageName = "Test.Pkg";

    private DirectoryInfo CreateBuildToolsDir()
        => _tempDirectory.CreateSubdirectory("buildtools");

    private void WriteConfig(params (string Name, string Version)[] packages)
    {
        var config = new WinappConfig();
        foreach (var (name, version) in packages)
        {
            config.SetVersion(name, version);
        }
        _configService.Save(config);
    }

    private void SaveConfigWith(string pinnedVersion)
        => WriteConfig((PackageName, pinnedVersion));

    private async Task<int> RunUpdateAsync()
    {
        var command = GetRequiredService<UpdateCommand>();
        return await ParseAndInvokeWithCaptureAsync(command, []);
    }

    // ── Command metadata ────────────────────────────────────────────────

    [TestMethod]
    public void UpdateCommand_ExposesNameShortDescriptionAndSetupSdksOption()
    {
        var command = GetRequiredService<UpdateCommand>();

        Assert.AreEqual("update", command.Name);
        Assert.AreEqual("Update packages in winapp.yaml", command.ShortDescription);
        Assert.Contains(InitCommand.SetupSdksOption, command.Options, "update must expose the --setup-sdks option.");
    }

    // ── No / empty config ───────────────────────────────────────────────

    [TestMethod]
    public async Task Update_NoWinappYaml_InstallsBuildToolsOnly()
    {
        var command = GetRequiredService<UpdateCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(0, exitCode);
        Assert.IsEmpty(_fakeInstall.InstallPackagesCalls, "No config means no package install");
        Assert.HasCount(1, _fakeBuildTools.EnsureCalls);
        Assert.IsTrue(_fakeBuildTools.EnsureCalls[0], "Update forces the latest build tools");
    }

    [TestMethod]
    public async Task Update_ConfigWithNoPackages_SkipsPackageUpdate()
    {
        WriteConfig(); // empty packages list
        var command = GetRequiredService<UpdateCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(0, exitCode);
        Assert.IsEmpty(_fakeInstall.InstallPackagesCalls);
        Assert.IsEmpty(_fakeNuget.QueriedPackages);
    }

    // ── Package update decisions ────────────────────────────────────────

    [TestMethod]
    public async Task Update_AllPackagesUpToDate_DoesNotReinstall()
    {
        _fakeNuget.DefaultVersion = "1.6.0";
        WriteConfig(("Microsoft.WindowsAppSDK", "1.6.0")); // already latest
        var command = GetRequiredService<UpdateCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(0, exitCode);
        Assert.HasCount(1, _fakeNuget.QueriedPackages);
        Assert.IsEmpty(_fakeInstall.InstallPackagesCalls, "Up-to-date packages are not reinstalled");
    }

    [TestMethod]
    public async Task Update_OutOfDatePackages_SavesConfigAndReinstalls()
    {
        _fakeNuget.DefaultVersion = "1.6.0";
        WriteConfig(("Microsoft.WindowsAppSDK", "1.0.0")); // stale
        var command = GetRequiredService<UpdateCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(0, exitCode);
        Assert.HasCount(1, _fakeInstall.InstallPackagesCalls);
        var call = _fakeInstall.InstallPackagesCalls[0];
        CollectionAssert.Contains(call.Packages, "Microsoft.WindowsAppSDK");
        Assert.IsFalse(call.IgnoreConfig, "Update installs using the updated config");

        // winapp.yaml was rewritten with the new version
        var reloaded = _configService.Load();
        Assert.AreEqual("1.6.0", reloaded.GetVersion("Microsoft.WindowsAppSDK"));
    }

    // ── Version-decision gate: CompareVersions(latest, current) > 0 ──────

    [TestMethod]
    public async Task Update_LatestIsHigher_RewritesYamlAndReinstalls()
    {
        // Pinned 1.0.0, feed reports 2.0.0 → strictly greater → the persisted version advances and the
        // updated package is reinstalled.
        SaveConfigWith("1.0.0");
        _fakeNuget.DefaultVersion = "2.0.0";

        var exitCode = await RunUpdateAsync();

        Assert.AreEqual(0, exitCode);
        var persisted = _configService.Load().Packages.Single(p => p.Name == PackageName).Version;
        Assert.AreEqual("2.0.0", persisted, "A strictly-greater latest version must be written to winapp.yaml.");
        CollectionAssert.Contains(_fakeInstall.InstalledPackages, PackageName, "An update must reinstall the updated package.");
    }

    [TestMethod]
    public async Task Update_LatestIsNormalizedEqual_LeavesYamlAndSkipsInstall()
    {
        // Pinned 1.0.0, feed reports the normalized-equal "1.0" → CompareVersions == 0 → no update. The
        // persisted version stays exactly as written and nothing is reinstalled.
        SaveConfigWith("1.0.0");
        _fakeNuget.DefaultVersion = "1.0";

        var exitCode = await RunUpdateAsync();

        Assert.AreEqual(0, exitCode);
        var persisted = _configService.Load().Packages.Single(p => p.Name == PackageName).Version;
        Assert.AreEqual("1.0.0", persisted, "A normalized-equal latest version must not rewrite the pinned version.");
        Assert.IsEmpty(_fakeInstall.InstalledPackages, "A no-op update must not reinstall packages.");
    }

    [TestMethod]
    public async Task Update_LatestIsLower_LeavesYamlAndSkipsInstall()
    {
        // Pinned 2.0.0, feed reports a lower 1.5.0 → CompareVersions < 0 → no update, no silent downgrade.
        SaveConfigWith("2.0.0");
        _fakeNuget.DefaultVersion = "1.5.0";

        var exitCode = await RunUpdateAsync();

        Assert.AreEqual(0, exitCode);
        var persisted = _configService.Load().Packages.Single(p => p.Name == PackageName).Version;
        Assert.AreEqual("2.0.0", persisted, "A lower latest version must never downgrade the pinned version.");
        Assert.IsEmpty(_fakeInstall.InstalledPackages, "A lower latest version must not trigger a reinstall.");
    }

    [TestMethod]
    public async Task Update_PreviewSdks_ReinstallsWithPreviewMode()
    {
        _fakeNuget.DefaultVersion = "2.0.0-preview";
        WriteConfig(("Microsoft.WindowsAppSDK", "1.0.0"));
        var command = GetRequiredService<UpdateCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--setup-sdks", "preview"]);

        Assert.AreEqual(0, exitCode);
        Assert.HasCount(1, _fakeInstall.InstallPackagesCalls);
    }

    /// <summary>
    /// `--setup-sdks none` means "skip SDK installation". It previously passed None straight to
    /// GetLatestVersionAsync, which rejects None outright, so every package was recorded as a lookup failure
    /// and the documented option always exited 1 on a non-empty config. Skipping the version checks alone was
    /// not enough though: build tools were still downloaded and the Windows App Runtime still installed, so a
    /// documented no-install option modified the machine. `init` already skips both under None.
    /// </summary>
    [TestMethod]
    public async Task Update_SetupSdksNone_SkipsAllSdkInstallationAndSucceeds()
    {
        WriteConfig((PackageName, "1.0.0"));
        // Make the runtime discoverable, so "not installed" can only be the None gate rather than the
        // absence of anything to install.
        _fakeRuntime.MsixDirectory = _tempDirectory.CreateSubdirectory("msix");
        var command = GetRequiredService<UpdateCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--setup-sdks", "none"]);

        Assert.AreEqual(0, exitCode);
        Assert.IsEmpty(_fakeNuget.QueriedPackages, "None means no channel, so no latest-version lookup may be attempted.");
        Assert.IsEmpty(_fakeInstall.InstallPackagesCalls, "None must not reinstall SDK packages.");
        Assert.IsEmpty(_fakeBuildTools.EnsureCalls, "None must not download or update build tools.");
        Assert.IsEmpty(_fakeRuntime.InstallRuntimeCalls, "None must not install the Windows App Runtime — that modifies the machine.");
        // The pinned version must survive untouched.
        var persisted = _configService.Load().Packages.Single(p => p.Name == PackageName).Version;
        Assert.AreEqual("1.0.0", persisted);
    }

    /// <summary>
    /// The companion to the test above: without `--setup-sdks none`, update must still do the build-tool and
    /// runtime work, so the gate cannot be mistaken for "update never installs anything".
    /// </summary>
    [TestMethod]
    public async Task Update_DefaultSetupSdks_StillUpdatesBuildToolsAndRuntime()
    {
        WriteConfig((PackageName, "1.0.0"));
        _fakeRuntime.MsixDirectory = _tempDirectory.CreateSubdirectory("msix");
        var command = GetRequiredService<UpdateCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(0, exitCode);
        Assert.IsNotEmpty(_fakeBuildTools.EnsureCalls, "the default mode must still update build tools.");
        Assert.IsNotEmpty(_fakeRuntime.InstallRuntimeCalls, "the default mode must still install the runtime when one is found.");
    }

    // ── Lookup / cancellation failure paths ─────────────────────────────

    [TestMethod]
    public async Task Update_LatestVersionLookupFails_ExitsNonZeroAndPreservesPin()
    {
        // A latest-version lookup that fails closed (a feed outage or auth failure) must NOT be reported as an
        // authoritative "up to date" result. GetLatestVersionAsync throws; the handler must keep the pinned
        // version in winapp.yaml untouched, skip the reinstall, and exit non-zero so callers (and CI) surface
        // the failure instead of a false success that silently freezes the pin.
        SaveConfigWith("1.0.0");
        _fakeNuget.PackagesToThrow.Add(PackageName);

        var exitCode = await RunUpdateAsync();

        Assert.AreEqual(1, exitCode, "A failed latest-version lookup must fail the command, not report success.");
        var persisted = _configService.Load().Packages.Single(p => p.Name == PackageName).Version;
        Assert.AreEqual("1.0.0", persisted, "A failed lookup must leave the pinned version unchanged.");
        Assert.IsEmpty(_fakeInstall.InstalledPackages, "A failed lookup must not trigger a reinstall.");
    }

    [TestMethod]
    public async Task Update_CancelledDuringLookup_AbortsWithoutCheckingRemainingPackages()
    {
        // Ctrl+C during a latest-version lookup must abort the whole command — cancellation must NOT be
        // swallowed by the ordinary lookup-failure handler, which would let the loop keep checking the
        // remaining packages and then proceed to install / build-tool work. The fake cancels the flow token
        // while checking the first package and throws; the handler must rethrow that cancellation, so the
        // second package is never queried and nothing is installed. Contrast with the fail-closed test above,
        // where an ordinary lookup failure lets the loop continue.
        WriteConfig(("First.Pkg", "1.0.0"), ("Second.Pkg", "1.0.0"));

        using var cts = new CancellationTokenSource();
        _fakeNuget.CancelOnQuery = cts;
        _fakeNuget.CancelOnQueryPackage = "First.Pkg";

        var command = GetRequiredService<UpdateCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [], cts.Token);

        Assert.AreNotEqual(0, exitCode, "A cancelled update must not report success.");
        CollectionAssert.Contains(_fakeNuget.QueriedPackages, "First.Pkg", "The first package triggers the cancellation.");
        CollectionAssert.DoesNotContain(_fakeNuget.QueriedPackages, "Second.Pkg",
            "Cancellation must abort the loop, so the second package is never queried.");
        Assert.IsEmpty(_fakeInstall.InstalledPackages, "A cancelled update must not reinstall packages.");
    }

    // ── Build tools gating ──────────────────────────────────────────────

    [TestMethod]
    public async Task Update_BuildToolsInstallFails_ReturnsError()
    {
        _fakeBuildTools.BuildToolsDirectory = _ => null; // acquisition failed
        var command = GetRequiredService<UpdateCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "Failed to install/update build tools");
    }

    // ── Runtime install step ────────────────────────────────────────────

    [TestMethod]
    public async Task Update_MsixDirectoryFound_InstallsRuntime()
    {
        _fakeRuntime.MsixDirectory = _tempDirectory.CreateSubdirectory("msix");
        var command = GetRequiredService<UpdateCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(0, exitCode);
        Assert.HasCount(1, _fakeRuntime.InstallRuntimeCalls);
    }

    [TestMethod]
    public async Task Update_NoMsixDirectory_SkipsRuntimeInstall()
    {
        _fakeRuntime.MsixDirectory = null;
        var command = GetRequiredService<UpdateCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(0, exitCode);
        Assert.IsEmpty(_fakeRuntime.InstallRuntimeCalls);
    }

    // ── Failure / options ───────────────────────────────────────────────

    [TestMethod]
    public async Task Update_UnexpectedException_ReturnsError()
    {
        _fakeBuildTools.BuildToolsDirectory = _ => throw new InvalidOperationException("boom");
        var command = GetRequiredService<UpdateCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "Update command failed");
    }

    /// <summary>
    /// Configurable build-tools fake. Only <see cref="EnsureBuildToolsAsync"/> is used by the
    /// update flow; the factory lets each test return a directory, null, or throw.
    /// </summary>
    private sealed class FakeUpdateBuildToolsService : IBuildToolsService
    {
        public List<bool> EnsureCalls { get; } = [];
        public Func<TaskContext, DirectoryInfo?> BuildToolsDirectory { get; set; } = _ => null;

        public Task<DirectoryInfo?> EnsureBuildToolsAsync(TaskContext taskContext, bool forceLatest = false, CancellationToken cancellationToken = default)
        {
            EnsureCalls.Add(forceLatest);
            return Task.FromResult(BuildToolsDirectory(taskContext));
        }

        public FileInfo? GetBuildToolPath(string toolName) => throw new NotSupportedException();

        public Task<FileInfo> EnsureBuildToolAvailableAsync(string toolName, TaskContext taskContext, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<(string stdout, string stderr)> RunBuildToolAsync(Tool tool, string arguments, TaskContext taskContext, bool printErrors = true, FileInfo? toolPathOverride = null, IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}

/// <summary>
/// Windows App Runtime fake for the update flow's final step: it decides whether an MSIX
/// directory is "found" and records the install calls so the runtime step can be asserted
/// deterministically.
/// </summary>
internal sealed class UpdateRuntimeFake : IWindowsAppRuntimeService
{
    public DirectoryInfo? MsixDirectory { get; set; }
    public List<DirectoryInfo> InstallRuntimeCalls { get; } = [];

    public DirectoryInfo? FindWindowsAppSdkMsixDirectory(Dictionary<string, string>? usedVersions = null, bool requireExactVersion = false) => MsixDirectory;

    public Task<(int InstalledCount, int ErrorCount, IReadOnlyList<(string Name, string Version)> RuntimePackages)> InstallWindowsAppRuntimeAsync(DirectoryInfo msixDir, TaskContext taskContext, CancellationToken cancellationToken, string? architecture = null)
    {
        InstallRuntimeCalls.Add(msixDir);
        return Task.FromResult((1, 0, (IReadOnlyList<(string Name, string Version)>)[]));
    }

    public bool IsWindowsAppRuntimeRegistered(string? architecture, IReadOnlyList<(string Name, string Version)>? expectedRuntimePackages = null)
        => true;
}
