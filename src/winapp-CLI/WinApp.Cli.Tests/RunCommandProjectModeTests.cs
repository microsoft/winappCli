// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Project-mode routing tests for <see cref="RunCommand"/>. A <see cref="FakeProjectRunService"/>
/// supplies canned build outcomes so the packaged/unpackaged launch branches can be verified without
/// invoking the real .NET SDK.
/// </summary>
[TestClass]
public class RunCommandProjectModeTests : BaseCommandTests
{
    private FakeMsixService _fakeMsixService = null!;
    private FakeAppLauncherService _fakeAppLauncherService = null!;
    private FakeDebugOutputService _fakeDebugOutputService = null!;
    private FakeProjectRunService _fakeProjectRunService = null!;

    private const string TestManifestContent = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
                 IgnorableNamespaces="uap rescap">
          <Identity Name="TestPackage" Publisher="CN=TestPublisher" Version="1.0.0.0" />
          <Properties>
            <DisplayName>Test Package</DisplayName>
            <PublisherDisplayName>Test Publisher</PublisherDisplayName>
            <Description>Test package</Description>
            <Logo>Assets\Logo.png</Logo>
          </Properties>
          <Dependencies>
            <TargetDeviceFamily Name="Windows.Universal" MinVersion="10.0.18362.0" MaxVersionTested="10.0.26100.0" />
          </Dependencies>
          <Applications>
            <Application Id="TestApp" Executable="TestApp.exe" EntryPoint="TestApp.App">
              <uap:VisualElements DisplayName="Test App" Description="Test application"
                                  BackgroundColor="#777777" Square150x150Logo="Assets\Logo.png" Square44x44Logo="Assets\Logo.png" />
            </Application>
          </Applications>
          <Capabilities>
            <rescap:Capability Name="runFullTrust" />
          </Capabilities>
        </Package>
        """;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeMsixService = new FakeMsixService();
        _fakeAppLauncherService = new FakeAppLauncherService();
        _fakeDebugOutputService = new FakeDebugOutputService();
        _fakeProjectRunService = new FakeProjectRunService();
        return services
            .AddSingleton<IMsixService>(_fakeMsixService)
            .AddSingleton<IAppLauncherService>(_fakeAppLauncherService)
            .AddSingleton<IDebugOutputService>(_fakeDebugOutputService)
            .AddSingleton<IProjectRunService>(_fakeProjectRunService)
            .AddSingleton<INugetService, FakeNugetService>();
    }

    private FileInfo CreateCsproj(string name = "App.csproj")
    {
        var path = Path.Combine(_tempDirectory.FullName, name);
        File.WriteAllText(path, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        return new FileInfo(path);
    }

    private DirectoryInfo CreateTargetDir(bool withManifest)
    {
        var dir = _tempDirectory.CreateSubdirectory($"bin_{Guid.NewGuid():N}");
        if (withManifest)
        {
            File.WriteAllText(Path.Combine(dir.FullName, "appxmanifest.xml"), TestManifestContent);
        }
        return dir;
    }

    private void SetUnpackagedOutcome(FileInfo csproj, DirectoryInfo targetDir, bool selfContained, string arch = "x64")
    {
        var exe = Path.Combine(targetDir.FullName, "App.exe");
        _fakeProjectRunService.BuildOutcome = new ProjectBuildOutcome(
            new ProjectRunResolution(csproj, targetDir.FullName, exe, ProjectPackaging.Unpackaged, selfContained, arch), 0);
    }

    // Builds `winapp run <csproj> <option> [value]` for the packaged-only-option rejection tests (M7),
    // materializing real paths for the token-valued options so parsing succeeds.
    private string[] BuildRejectionArgs(FileInfo csproj, string option, string? argToken)
    {
        if (argToken is null)
        {
            return [csproj.FullName, option];
        }

        var value = argToken switch
        {
            "MANIFEST" => WriteRejectionManifest().FullName,
            "OUTDIR" => _tempDirectory.CreateSubdirectory($"appx_{Guid.NewGuid():N}").FullName,
            _ => argToken,
        };
        return [csproj.FullName, option, value];
    }

    private FileInfo WriteRejectionManifest()
    {
        var path = Path.Combine(_tempDirectory.FullName, $"manifest_{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, TestManifestContent);
        return new FileInfo(path);
    }

    private void SetPackagedOutcome(FileInfo csproj, DirectoryInfo targetDir, string arch = "x64")
    {
        _fakeProjectRunService.BuildOutcome = new ProjectBuildOutcome(
            new ProjectRunResolution(csproj, targetDir.FullName, null, ProjectPackaging.Packaged, false, arch), 0);
    }

    #region Unpackaged

    [TestMethod]
    public async Task ProjectMode_Unpackaged_InstallsRuntimeForArchAndLaunchesExecutable()
    {
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: false, arch: "x64");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchExecutableCalls.Count, "Unpackaged app should launch via LaunchExecutable");
        StringAssert.Contains(_fakeAppLauncherService.LaunchExecutableCalls[0].ExePath, "App.exe");
        // Launch with the caller's cwd (like `dotnet run`), NOT the build-output directory, so apps
        // resolving config/data via relative paths behave the same as under `dotnet run`.
        var cwd = GetRequiredService<ICurrentDirectoryProvider>().GetCurrentDirectory();
        Assert.AreEqual(cwd, _fakeAppLauncherService.LaunchExecutableCalls[0].WorkingDirectory,
            "Unpackaged app must launch with the caller's current directory, not the exe output folder");
        Assert.AreNotEqual(targetDir.FullName, _fakeAppLauncherService.LaunchExecutableCalls[0].WorkingDirectory,
            "Working directory must not be the build-output directory");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "Unpackaged app must NOT use AUMID activation");
        Assert.AreEqual(1, _fakeMsixService.EnsureRuntimeInstalledCalls.Count, "Runtime should be installed for a non-self-contained app");
        Assert.AreEqual("x64", _fakeMsixService.EnsureRuntimeInstalledCalls[0].Architecture, "Runtime install must honor the resolved arch");
        Assert.AreEqual(csproj.FullName, _fakeMsixService.EnsureRuntimeInstalledCalls[0].ProjectFile);
    }

    [TestMethod]
    public async Task ProjectMode_Unpackaged_Arm64_InstallsRuntimeForArm64()
    {
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: false, arch: "arm64");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--arch", "arm64", "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual("arm64", _fakeMsixService.EnsureRuntimeInstalledCalls[0].Architecture);
    }

    [TestMethod]
    public async Task ProjectMode_Unpackaged_NoRestore_ThreadsNoRestoreIntoRuntimeInstall()
    {
        // C43: a --no-restore run must not trigger an implicit restore during runtime discovery, so the
        // flag has to reach EnsureWindowsAppRuntimeInstalledAsync (which forwards it to dotnet list package).
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        var exe = Path.Combine(targetDir.FullName, "App.exe");
        _fakeProjectRunService.BuildOutcome = new ProjectBuildOutcome(
            new ProjectRunResolution(csproj, targetDir.FullName, exe, ProjectPackaging.Unpackaged, false, "x64", NoRestore: true), 0);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--no-restore", "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeMsixService.EnsureRuntimeInstalledCalls.Count);
        Assert.IsTrue(_fakeMsixService.EnsureRuntimeInstalledCalls[0].NoRestore, "Runtime install must honor the run's --no-restore");
    }

    [TestMethod]
    public async Task ProjectMode_UnpackagedSelfContained_SkipsRuntimeInstall()
    {
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: true);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchExecutableCalls.Count);
        Assert.AreEqual(0, _fakeMsixService.EnsureRuntimeInstalledCalls.Count, "Self-contained apps carry their own runtime — no install");
    }

    [TestMethod]
    public async Task ProjectMode_Unpackaged_PropagatesNonZeroExitCode()
    {
        // Spec M3: a directly-launched unpackaged app that exits non-zero must be reported as a
        // failure, not masked as success. Waiting on the owned handle (not a PID re-attach) keeps
        // ExitCode valid even when the process exits before the wait begins. Runs WITHOUT --detach so
        // the wait/exit-code path is exercised; self-contained to skip the runtime-install branch.
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: true);
        _fakeAppLauncherService.FakeExitCode = 42;
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName]);

        Assert.AreEqual(42, exitCode, "A non-zero exit from the launched app must propagate (M3 exit-code loss)");
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchExecutableCalls.Count);
        Assert.IsNotNull(_fakeAppLauncherService.LastLaunchedProcess);
        Assert.IsTrue(_fakeAppLauncherService.LastLaunchedProcess!.Disposed, "The owned handle must be disposed after the wait");
        Assert.IsFalse(_fakeAppLauncherService.LastLaunchedProcess!.Killed, "A normally-exiting app must not be killed");
    }

    [TestMethod]
    public async Task ProjectMode_Unpackaged_RuntimePrepFailure_AbortsWithoutLaunching()
    {
        // Spec R2-M2: when runtime preparation throws (e.g. the version-specific gate can't confirm the
        // required Windows App Runtime is registered), the run must abort with a non-zero exit and must
        // never launch the app. Verifies the command wiring of the abort, not just the gate in isolation.
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: false);
        _fakeMsixService.EnsureRuntimeInstalledException = new InvalidOperationException("runtime not registered");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--detach"]);

        Assert.AreNotEqual(0, exitCode, "A runtime-prep failure must produce a non-zero exit");
        Assert.AreEqual(1, _fakeMsixService.EnsureRuntimeInstalledCalls.Count, "Runtime prep should have been attempted");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchExecutableCalls.Count, "The app must NOT launch when runtime prep fails");
    }

    [TestMethod]
    [DataRow("--no-launch", null)]
    [DataRow("--with-alias", null)]
    [DataRow("--unregister-on-exit", null)]
    [DataRow("--clean", null)]
    [DataRow("--manifest", "MANIFEST")]
    [DataRow("--output-appx-directory", "OUTDIR")]
    [DataRow("--executable", "Other.exe")]
    public async Task ProjectMode_Unpackaged_RejectsEveryPackagedOnlyOption_AtAuthoritativeGate(string option, string? argToken)
    {
        // M7: every launch/identity option that is only meaningful for a packaged (MSIX) app must be
        // rejected once the target resolves unpackaged. Probe defaults to indeterminate, so this
        // exercises the AUTHORITATIVE post-build gate for the full option set (issue #676).
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: false);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, BuildRejectionArgs(csproj, option, argToken));

        Assert.AreEqual(1, exitCode, $"{option} must be rejected for an unpackaged app");
        Assert.AreEqual(1, _fakeProjectRunService.BuildAndResolveCalls.Count, "Indeterminate packaging must build, then reject at the authoritative gate");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchExecutableCalls.Count, $"App must not launch when {option} was supplied");
    }

    [TestMethod]
    [DataRow("--no-launch", null)]
    [DataRow("--with-alias", null)]
    [DataRow("--unregister-on-exit", null)]
    [DataRow("--clean", null)]
    [DataRow("--manifest", "MANIFEST")]
    [DataRow("--output-appx-directory", "OUTDIR")]
    [DataRow("--executable", "Other.exe")]
    public async Task ProjectMode_DefinitivelyUnpackaged_RejectsEveryPackagedOnlyOption_BeforeBuilding(string option, string? argToken)
    {
        // M7 / Issue #676: when the project is definitively unpackaged (WindowsPackageType=None), every
        // packaged-only option is rejected by the pre-build probe — the user does not pay the build cost.
        var csproj = CreateCsproj();
        _fakeProjectRunService.DefinitivelyUnpackaged = true;
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, BuildRejectionArgs(csproj, option, argToken));

        Assert.AreEqual(1, exitCode, $"{option} on a definitively-unpackaged app must fail");
        Assert.AreEqual(1, _fakeProjectRunService.IsDefinitivelyUnpackagedCalls.Count, "The pre-build probe must run");
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count, $"{option} must reject before building");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchExecutableCalls.Count);
    }

    [TestMethod]
    public async Task ProjectMode_CompatibleOptions_SkipTheFastFailProbe()
    {
        // --detach is valid for unpackaged apps, so the pre-build probe should be short-circuited
        // (no incompatible option to reject) and the app should build + launch normally.
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: false);
        _fakeProjectRunService.DefinitivelyUnpackaged = true;
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(0, _fakeProjectRunService.IsDefinitivelyUnpackagedCalls.Count, "The probe must be skipped when no incompatible option is present");
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchExecutableCalls.Count);
    }

    [TestMethod]
    public async Task ProjectMode_NoBuild_SkipsTheFastFailProbe()
    {
        // Under --no-build there is no build cost to save, so the pre-build probe is skipped; the
        // authoritative post-build gate still rejects the incompatible option.
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: false);
        _fakeProjectRunService.DefinitivelyUnpackaged = true;
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--no-build", "--no-launch"]);

        Assert.AreEqual(1, exitCode, "The incompatible option is still rejected at the authoritative gate");
        Assert.AreEqual(0, _fakeProjectRunService.IsDefinitivelyUnpackagedCalls.Count, "The probe must be skipped under --no-build");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchExecutableCalls.Count);
    }

    [TestMethod]
    public async Task ProjectMode_ForcedUnpackaged_ForwardsPropertyAndLaunchesExecutable()
    {
        // C4 regression (unit level): -p WindowsPackageType=None is forwarded to the build and the app
        // runs unpackaged. The end-to-end WindowsPackageType assertion lives in the Pester sample test.
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: false);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [csproj.FullName, "-p", "WindowsPackageType=None", "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchExecutableCalls.Count);
        CollectionAssert.Contains(_fakeProjectRunService.BuildOptions[0].Properties.ToArray(), "WindowsPackageType=None",
            "User -p property must be forwarded to the build options");
    }

    [TestMethod]
    public async Task ProjectMode_Unpackaged_Detach_SuppressesChildStdio()
    {
        // C37: a detached launch must NOT let the child inherit winapp's std handles — inheriting keeps the
        // npm wrapper's captured stdout pipe open, so `run({detach:true})` would block until the app exits.
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: true);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(LaunchStdioMode.Suppress, _fakeAppLauncherService.LastLaunchStdioMode,
            "A detached launch must suppress child stdio so it doesn't hold the parent capture pipe open");
    }

    [TestMethod]
    public async Task ProjectMode_Unpackaged_Json_SuppressesChildStdio()
    {
        // C37: under --json the child must not inherit winapp's stdout, or app output would corrupt the
        // single JSON object the CLI writes.
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: true);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(LaunchStdioMode.Suppress, _fakeAppLauncherService.LastLaunchStdioMode,
            "A --json launch must suppress child stdio so app output cannot corrupt the JSON envelope");
    }

    [TestMethod]
    public async Task ProjectMode_Unpackaged_Foreground_InheritsChildStdio()
    {
        // C37: a plain foreground run streams the app's output inline (like `dotnet run`), so the child
        // inherits winapp's std handles.
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: true);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(LaunchStdioMode.Inherit, _fakeAppLauncherService.LastLaunchStdioMode,
            "A foreground, non-JSON launch must inherit stdio so output streams inline");
    }

    #endregion

    #region Packaged

    [TestMethod]
    public async Task ProjectMode_Packaged_InstallsArchRuntimeAndLaunchesViaAumid()
    {
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: true);
        SetPackagedOutcome(csproj, targetDir, arch: "x64");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutCalls.Count, "Packaged app should register a loose-layout identity");
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutRuntimeCalls.Count);
        Assert.AreEqual("x64", _fakeMsixService.AddLooseLayoutRuntimeCalls[0].RuntimeArch, "Loose-layout runtime install must honor the resolved arch");
        Assert.AreEqual(csproj.FullName, _fakeMsixService.AddLooseLayoutRuntimeCalls[0].ProjectFile);
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchCalls.Count, "Packaged app should launch via AUMID");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchExecutableCalls.Count, "Packaged app must NOT launch the apphost exe directly");
    }

    [TestMethod]
    public async Task ProjectMode_Packaged_ThreadsResolvedFrameworkIntoRuntimeProvisioning()
    {
        // M2: for a multi-targeted packaged app the resolved TFM must reach loose-layout runtime
        // provisioning so the WASDK runtime is narrowed to that framework's package set (not an
        // arbitrary FirstOrDefault across all TFMs, which can install the wrong runtime version).
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: true);
        const string tfm = "net10.0-windows10.0.26100.0";
        _fakeProjectRunService.BuildOutcome = new ProjectBuildOutcome(
            new ProjectRunResolution(csproj, targetDir.FullName, null, ProjectPackaging.Packaged, false, "x64", tfm), 0);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutRuntimeCalls.Count);
        Assert.AreEqual(tfm, _fakeMsixService.AddLooseLayoutRuntimeCalls[0].Framework, "The resolved target framework must be threaded into runtime provisioning");
    }

    [TestMethod]
    public async Task ProjectMode_Packaged_ThreadsNoRestoreIntoRuntimeProvisioning()
    {
        // A packaged project run with --no-restore must not trigger an implicit restore during the
        // loose-layout runtime-package discovery that follows the build. The run's NoRestore setting
        // has to reach AddLooseLayoutIdentityAsync, not be hard-coded to false on this shared pipeline.
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: true);
        _fakeProjectRunService.BuildOutcome = new ProjectBuildOutcome(
            new ProjectRunResolution(csproj, targetDir.FullName, null, ProjectPackaging.Packaged, false, "x64", null, NoRestore: true), 0);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--no-restore", "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutRuntimeCalls.Count);
        Assert.IsTrue(_fakeMsixService.AddLooseLayoutRuntimeCalls[0].NoRestore, "The run's --no-restore must be threaded into loose-layout runtime provisioning");
    }

    [TestMethod]
    [DataRow("Exe", null, true, DisplayName = "console app defaults to alias launch")]
    [DataRow("Exe", false, false, DisplayName = "console app can opt out via the property")]
    [DataRow("WinExe", null, false, DisplayName = "windowed app defaults to AUMID")]
    [DataRow("WinExe", true, true, DisplayName = "windowed app can opt in via the property")]
    public async Task ProjectMode_Packaged_HonorsTheProjectsExecutionAliasPreference(
        string outputType, bool? preferAlias, bool expectAlias)
    {
        // WinAppRunUseExecutionAlias must mean the same thing however the project is launched. Running
        // the .csproj directly previously ignored it, so the same property behaved differently than it
        // does through `dotnet run` or in a .cs file.
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: true);
        _fakeProjectRunService.BuildOutcome = new ProjectBuildOutcome(
            new ProjectRunResolution(csproj, targetDir.FullName, null, ProjectPackaging.Packaged, false, "x64",
                null, false, null, outputType, preferAlias), 0);
        var command = GetRequiredService<RunCommand>();

        await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName]);

        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutEnsureAliasCalls.Count);
        Assert.AreEqual(expectAlias, _fakeMsixService.AddLooseLayoutEnsureAliasCalls[0],
            $"OutputType={outputType} with WinAppRunUseExecutionAlias={preferAlias?.ToString() ?? "unset"}");
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task ProjectMode_InferredAliasUnavailable_WarnsThatOutputWillNotAppear()
    {
        // Plain `winapp run` now promises a console app's output reaches the terminal. When the inferred
        // alias cannot be used, falling back to AUMID launches it into a process with no console, so it
        // prints nothing — the exact silent success this feature exists to remove. The run still succeeds,
        // but a Debug-level line leaves the user with no way to diagnose the missing output.
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: true);
        _fakeProjectRunService.BuildOutcome = new ProjectBuildOutcome(
            new ProjectRunResolution(csproj, targetDir.FullName, null, ProjectPackaging.Packaged, false, "x64",
                null, false, null, "Exe", null), 0);
        var handler = GetRequiredService<RunCommand.Handler>();
        handler.ResolveAliasProxy = _ => null;
        var command = GetRequiredService<RunCommand>();

        var (exitCode, ambientOutput) = await InvokeWithAmbientConsoleCaptureAsync(command, [csproj.FullName]);

        Assert.AreEqual(0, exitCode, "An inferred alias that cannot be used must not fail the run");
        var output = System.Text.RegularExpressions.Regex.Replace(
            $"{ambientOutput}{ConsoleStdOut}{ConsoleStdErr}{TestAnsiConsole.Output}", @"\s+", " ");
        StringAssert.Contains(output, "not print to this terminal",
            "The user has to be told why the console output is missing");
    }

    [TestMethod]
    public async Task ProjectMode_Packaged_ReadsThePackageGraphFromTheBuildsAssetsFile()
    {
        // `dotnet package list` re-evaluates the project and takes no -c/-r/-p, and the environment is not
        // a substitute: MSBuild ranks environment properties BELOW a value the project assigns, while the
        // build's own -c/-r/-p outrank it. So the only faithful source is project.assets.json — restore's
        // output for the inputs the build actually ran with. Without it, a PackageReference conditioned on
        // Configuration, RID, or a user -p is invisible, and the manifest's framework dependency and
        // runtime provisioning are decided from a graph that does not describe the binary.
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: true);
        var assetsFile = Path.Join(targetDir.FullName, "obj", "project.assets.json");
        _fakeProjectRunService.BuildOutcome = new ProjectBuildOutcome(
            new ProjectRunResolution(csproj, targetDir.FullName, null, ProjectPackaging.Packaged, false, "arm64",
                null, false, null, "WinExe", null, assetsFile),
            0);
        var command = GetRequiredService<RunCommand>();

        await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "-c", "Release", "--arch", "arm64"]);

        Assert.AreEqual(assetsFile, _fakeMsixService.AddLooseLayoutAssetsFileCalls.Single(),
            "The build's own assets file must reach package discovery");
    }

    [TestMethod]
    public async Task ProjectMode_Packaged_NoManifestInOutput_Errors()
    {
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetPackagedOutcome(csproj, targetDir);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--detach"]);

        Assert.AreEqual(1, exitCode, "A packaged app with no AppxManifest.xml in the output is a misconfiguration");
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count);
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count);
    }

    #endregion

    #region Guardrails / errors

    [TestMethod]
    public async Task ProjectMode_SdkTooOld_ErrorsBeforeBuild()
    {
        var csproj = CreateCsproj();
        _fakeProjectRunService.SdkError = "SDK too old.";
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--detach"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count, "Build must not run when the SDK is incapable");
    }

    [TestMethod]
    public async Task ProjectMode_BuildFails_PropagatesExitCode()
    {
        var csproj = CreateCsproj();
        _fakeProjectRunService.BuildOutcome = new ProjectBuildOutcome(null, 7);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--detach"]);

        Assert.AreEqual(7, exitCode, "A build failure must propagate the dotnet exit code");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchExecutableCalls.Count);
    }

    [TestMethod]
    public async Task ProjectMode_BuildThrowsGuardrail_Errors()
    {
        var csproj = CreateCsproj();
        _fakeProjectRunService.BuildThrows = new ProjectRunException("Not a runnable project.");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--detach"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchExecutableCalls.Count);
    }

    [TestMethod]
    public async Task ProjectMode_InvalidArch_ErrorsBeforeBuild()
    {
        var csproj = CreateCsproj();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--arch", "sparc", "--detach"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count, "An unsupported --arch must fail before building");
    }

    [TestMethod]
    public async Task ProjectMode_Runtime_ResolvesArchIntoBuild()
    {
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: false, arch: "arm64");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--runtime", "win-arm64", "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual("arm64", _fakeProjectRunService.BuildOptions[0].Architecture,
            "--runtime's architecture must reach the build options");
    }

    [TestMethod]
    public async Task ProjectMode_Runtime_OverridesArch()
    {
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: false, arch: "x64");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--runtime", "win-x64", "--arch", "arm64", "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual("x64", _fakeProjectRunService.BuildOptions[0].Architecture,
            "--runtime's architecture must win over --arch");
    }

    [TestMethod]
    public async Task ProjectMode_NonWindowsRuntime_ErrorsBeforeBuild()
    {
        var csproj = CreateCsproj();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--runtime", "linux-x64", "--detach"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count, "A non-Windows --runtime must fail before building");
    }

    [TestMethod]
    public async Task ResolveInputAmbiguity_Errors()
    {
        var csproj = CreateCsproj();
        _fakeProjectRunService.ResolveInputThrows = new ProjectRunException("Multiple .csproj files found.");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName]);

        Assert.AreEqual(1, exitCode, "Ambiguous multi-csproj input must surface an error");
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count);
    }

    [TestMethod]
    public async Task ProjectMode_MalformedProperty_Errors()
    {
        // Spec L3: a -p value that isn't Name=Value (here, no '=') is rejected before building so it
        // never becomes a malformed '-p:' MSBuild argument.
        var csproj = CreateCsproj();
        SetUnpackagedOutcome(csproj, CreateTargetDir(withManifest: false), selfContained: false);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "-p", "NoEqualsSign", "--detach"]);

        Assert.AreEqual(1, exitCode, "A malformed -p (no '=') must fail");
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count, "Validation must happen before building");
    }

    [TestMethod]
    public async Task ProjectMode_WhitespaceNameProperty_Errors()
    {
        // C16 (Copilot review): a -p whose name before '=' is empty or whitespace-only (here " =Value")
        // must be rejected before building, not forwarded as a nonsensical '-p: =Value' MSBuild argument.
        var csproj = CreateCsproj();
        SetUnpackagedOutcome(csproj, CreateTargetDir(withManifest: false), selfContained: false);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "-p", " =Value", "--detach"]);

        Assert.AreEqual(1, exitCode, "A -p with a whitespace-only name must fail");
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count, "Validation must happen before building");
    }

    [TestMethod]
    public async Task ProjectMode_LeadingEqualsProperty_Errors()
    {
        // C16: a -p that starts with '=' (empty name) is likewise rejected before building.
        var csproj = CreateCsproj();
        SetUnpackagedOutcome(csproj, CreateTargetDir(withManifest: false), selfContained: false);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "-p", "=Value", "--detach"]);

        Assert.AreEqual(1, exitCode, "A -p with an empty name must fail");
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count, "Validation must happen before building");
    }

    [TestMethod]
    public async Task ProjectMode_ValuelessProperty_Errors()
    {
        // Spec L3: a bare -p with no value is rejected in the handler (via the raw OptionResult:
        // more '-p' identifier tokens than captured values) rather than silently producing no
        // property. Detecting it in the handler -- instead of relying on a System.CommandLine arity
        // error -- keeps the failure on the command's own error path so --json still gets JSON.
        var csproj = CreateCsproj();
        SetUnpackagedOutcome(csproj, CreateTargetDir(withManifest: false), selfContained: false);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "-p"]);

        Assert.AreEqual(1, exitCode, "A valueless -p must fail");
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count, "Validation must happen before building");
    }

    [TestMethod]
    public async Task ProjectMode_ValuelessProperty_Json_EmitsJsonError()
    {
        // Spec L3: under --json a valueless -p must produce a structured JSON error envelope, not
        // just a plain-text/parser error. This is the case the fix targets.
        var csproj = CreateCsproj();
        SetUnpackagedOutcome(csproj, CreateTargetDir(withManifest: false), selfContained: false);
        var command = GetRequiredService<RunCommand>();
        // Widen the test console so Spectre does not word-wrap the (long) JSON error line, which
        // would inject newlines into the string value and make it unparseable.
        TestAnsiConsole.Profile.Width = 1000;

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--json", "-p"]);

        Assert.AreEqual(1, exitCode, "A valueless -p must fail");
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count);
        var output = TestAnsiConsole.Output;
        var jsonStart = output.IndexOf('{');
        var jsonEnd = output.LastIndexOf('}');
        Assert.IsTrue(jsonStart >= 0 && jsonEnd > jsonStart, "Output should contain a JSON object");
        var doc = System.Text.Json.JsonDocument.Parse(output[jsonStart..(jsonEnd + 1)]);
        Assert.IsTrue(doc.RootElement.TryGetProperty("Error", out var error), "JSON must carry an 'Error' field");
        StringAssert.Contains(error.GetString(), "without a value", "Error must explain the valueless -p");
    }

    [TestMethod]
    public async Task ProjectMode_RepeatableProperty_Succeeds()
    {
        // The valueless-detection must not regress the supported repeatable -p happy path.
        var csproj = CreateCsproj();
        SetUnpackagedOutcome(csproj, CreateTargetDir(withManifest: false), selfContained: false);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, [csproj.FullName, "-p", "WindowsPackageType=None", "-p", "Foo=Bar", "--detach"]);

        Assert.AreEqual(0, exitCode, "Repeatable -p with values must succeed");
        Assert.AreEqual(1, _fakeProjectRunService.BuildAndResolveCalls.Count, "The project must still build");
    }

    [TestMethod]
    public async Task ProjectMode_SemicolonPackedProperty_IsRejectedBeforeBuilding()
    {
        // C29 (Copilot review): MSBuild's /p splits a single token on ';' into MULTIPLE properties, so a
        // packed -p like "Foo=bar;RuntimeIdentifier=win-arm64" would smuggle a dedicated-flag property
        // (RuntimeIdentifier) past the ForwardableProperties filter — which only inspects the name before
        // the FIRST '=' — and override the arch winapp conveys via the RID. It must be rejected up front.
        var csproj = CreateCsproj();
        SetUnpackagedOutcome(csproj, CreateTargetDir(withManifest: false), selfContained: false);
        var command = GetRequiredService<RunCommand>();
        // Widen the console so Spectre does not word-wrap the (long) JSON error line.
        TestAnsiConsole.Profile.Width = 1000;

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, [csproj.FullName, "--json", "-p", "Foo=bar;RuntimeIdentifier=win-arm64"]);

        Assert.AreEqual(1, exitCode, "A ';'-packed -p must fail");
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count, "The pack must be rejected before building");
        var output = TestAnsiConsole.Output;
        var jsonStart = output.IndexOf('{');
        var jsonEnd = output.LastIndexOf('}');
        Assert.IsTrue(jsonStart >= 0 && jsonEnd > jsonStart, "Output should contain a JSON object");
        var doc = System.Text.Json.JsonDocument.Parse(output[jsonStart..(jsonEnd + 1)]);
        Assert.IsTrue(doc.RootElement.TryGetProperty("Error", out var error), "JSON must carry an 'Error' field");
        StringAssert.Contains(error.GetString(), "';'", "Error must explain the ';' packing is not allowed");
    }

    [TestMethod]
    public async Task ProjectMode_CommaPackedProperty_IsRejectedWithoutEchoingSecret()
    {
        var csproj = CreateCsproj();
        SetUnpackagedOutcome(csproj, CreateTargetDir(withManifest: false), selfContained: false);
        var command = GetRequiredService<RunCommand>();
        TestAnsiConsole.Profile.Width = 1000;

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            [csproj.FullName, "--json", "-p", "PackageCertificatePassword=p@ss,w0rd"]);

        Assert.AreEqual(1, exitCode, "A comma-packed -p must fail");
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count);
        Assert.IsFalse(
            TestAnsiConsole.Output.Contains("p@ss", StringComparison.Ordinal)
            || TestAnsiConsole.Output.Contains("w0rd", StringComparison.Ordinal),
            "the validation error must not echo any part of a possible secret value");
        StringAssert.Contains(TestAnsiConsole.Output, "cannot pack multiple properties");
    }

    #endregion

    #region Folder mode (regression)

    [TestMethod]
    public async Task FolderMode_DelegatesToPipelineWithoutRuntimeHints()
    {
        // Folder mode must pass null runtimeArch/projectFile so behavior is byte-identical to before
        // project mode existed.
        File.WriteAllText(Path.Combine(_tempDirectory.FullName, "appxmanifest.xml"), TestManifestContent);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--no-launch"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutCalls.Count);
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutRuntimeCalls.Count);
        Assert.IsNull(_fakeMsixService.AddLooseLayoutRuntimeCalls[0].RuntimeArch, "Folder mode must not pass a runtime arch");
        Assert.IsNull(_fakeMsixService.AddLooseLayoutRuntimeCalls[0].ProjectFile, "Folder mode must not pass a project file");
    }

    #endregion
}
