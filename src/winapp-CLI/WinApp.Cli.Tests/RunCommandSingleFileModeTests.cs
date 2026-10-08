// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Single-file-mode routing tests for <see cref="RunCommand"/>: a .NET file-based app (a single
/// <c>.cs</c>) builds, gets a manifest, and reaches the shared loose-layout pipeline. A
/// <see cref="FakeProjectRunService"/> supplies canned build outcomes so the routing, option-rejection
/// and manifest-precedence logic is verified without invoking the real .NET SDK.
/// </summary>
[TestClass]
public class RunCommandSingleFileModeTests : BaseCommandTests
{
    private FakeMsixService _fakeMsixService = null!;
    private FakeAppLauncherService _fakeAppLauncherService = null!;
    private FakeDebugOutputService _fakeDebugOutputService = null!;
    private FakeProjectRunService _fakeProjectRunService = null!;
    private FakePackageRegistrationService _fakePackageRegistrationService = null!;

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeMsixService = new FakeMsixService();
        _fakeAppLauncherService = new FakeAppLauncherService();
        _fakeDebugOutputService = new FakeDebugOutputService();
        _fakeProjectRunService = new FakeProjectRunService();
        _fakePackageRegistrationService = new FakePackageRegistrationService();
        return services
            .AddSingleton<IMsixService>(_fakeMsixService)
            .AddSingleton<IAppLauncherService>(_fakeAppLauncherService)
            .AddSingleton<IDebugOutputService>(_fakeDebugOutputService)
            .AddSingleton<IProjectRunService>(_fakeProjectRunService)
            .AddSingleton<IPackageRegistrationService>(_fakePackageRegistrationService)
            .AddSingleton<INugetService, FakeNugetService>();
    }

    /// <summary>Writes a .cs file-based app and the build output folder its evaluate pass would report.</summary>
    private (FileInfo SingleFile, DirectoryInfo OutputDirectory) CreateSingleFileApp(string name = "counter.cs")
    {
        var sourceDir = _tempDirectory.CreateSubdirectory($"src_{Guid.NewGuid():N}");
        var singleFile = new FileInfo(Path.Join(sourceDir.FullName, name));
        File.WriteAllText(singleFile.FullName, "Console.WriteLine(\"hi\");");

        // Stands in for %TEMP%\dotnet\runfile\<stem>-<hash>\bin\debug\, which belongs to exactly one .cs.
        var outputDir = _tempDirectory.CreateSubdirectory($"runfile_{Guid.NewGuid():N}");
        File.WriteAllText(Path.Join(outputDir.FullName, "counter.exe"), "MZ");
        return (singleFile, outputDir);
    }

    private void SetOutcome(
        FileInfo singleFile,
        DirectoryInfo outputDirectory,
        string executableName = "counter.exe",
        params (string Name, string Value)[] properties)
        => SetOutcome(singleFile, outputDirectory, executableName, selfContained: false, properties);

    private void SetOutcome(
        FileInfo singleFile,
        DirectoryInfo outputDirectory,
        string executableName,
        bool selfContained,
        params (string Name, string Value)[] properties)
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in properties)
        {
            props[name] = value;
        }

        _fakeProjectRunService.SingleFileBuildOutcome = new SingleFileBuildOutcome(
            new SingleFileRunResolution(
                singleFile, outputDirectory.FullName, executableName, "x64", "net10.0-windows10.0.19041.0", selfContained,
                ProjectPackaging.Packaged, null, null, props), 0);
    }

    /// <summary>Sets an UNPACKAGED outcome, so the shared unpackaged gate is exercised.</summary>
    private void SetUnpackagedOutcome(FileInfo singleFile, DirectoryInfo outputDirectory, string? projectAssetsFile = null)
    {
        _fakeProjectRunService.SingleFileBuildOutcome = new SingleFileBuildOutcome(
            new SingleFileRunResolution(
                singleFile, outputDirectory.FullName, "counter.exe", "x64", "net10.0-windows10.0.19041.0", false,
                ProjectPackaging.Unpackaged, Path.Join(outputDirectory.FullName, "counter.exe"), null,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                projectAssetsFile), 0);
    }

    private static XDocument LoadGeneratedManifest(DirectoryInfo outputDirectory)
    {
        var path = Path.Join(outputDirectory.FullName, "Package.appxmanifest");
        Assert.IsTrue(File.Exists(path), $"A manifest should have been generated at {path}");
        return XDocument.Load(path);
    }

    #region Routing

    [TestMethod]
    public async Task SingleFileMode_BuildsAndRegistersThroughTheSharedLooseLayoutPipeline()
    {
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeProjectRunService.BuildAndResolveSingleFileCalls.Count, "The .cs should be built via the single-file pass");
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count, "The .csproj project pass must not run for a .cs");
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutCalls.Count, "A file-based app registers as a packaged loose layout");
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchCalls.Count, "A packaged app launches via AUMID");
    }

    [TestMethod]
    public async Task SingleFileMode_ForwardsConfigurationAndPropertiesToTheBuild()
    {
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, [singleFile.FullName, "-c", "Release", "-p", "Foo=Bar", "--no-restore", "--detach"]);

        Assert.AreEqual(0, exitCode);
        var options = _fakeProjectRunService.SingleFileBuildOptions.Single();
        Assert.AreEqual("Release", options.Configuration);
        Assert.IsTrue(options.NoRestore);
        CollectionAssert.Contains(options.Properties.ToList(), "Foo=Bar");
    }

    [TestMethod]
    public async Task SingleFileMode_BuildFailure_PropagatesTheDotnetExitCode()
    {
        var (singleFile, _) = CreateSingleFileApp();
        _fakeProjectRunService.SingleFileBuildOutcome = new SingleFileBuildOutcome(null, 3);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(3, exitCode);
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count);
    }

    [TestMethod]
    public async Task SingleFileMode_SdkTooOld_FailsBeforeBuilding()
    {
        // Building a bare .cs through a virtual project only exists from .NET 10, a higher floor than the
        // 8.0.100 that --getProperty alone needs.
        var (singleFile, _) = CreateSingleFileApp();
        _fakeProjectRunService.SingleFileSdkError = "The .NET SDK 9.0.100 cannot build .NET file-based apps.";
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveSingleFileCalls.Count, "The SDK gate must run before the build");
    }

    [TestMethod]
    public async Task SingleFileMode_GuardrailViolation_ReportsTheMessageAndFails()
    {
        var (singleFile, _) = CreateSingleFileApp();
        _fakeProjectRunService.SingleFileBuildThrows =
            new ProjectRunException("'counter.cs' declares WindowsPackageType=None");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count);
    }

    [TestMethod]
    [DataRow("--with-alias", DisplayName = "--with-alias")]
    [DataRow("--without-alias", DisplayName = "--without-alias")]
    public async Task SingleFileMode_UnpackagedApp_RejectsBothAliasOptions(string option)
    {
        // An unpackaged app launches its apphost directly and has no manifest to declare an alias in, so
        // opting OUT of one is as meaningless as opting in. Silently accepting either would make a
        // packaging mistake look supported.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetUnpackagedOutcome(singleFile, outputDir);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, option]);

        Assert.AreEqual(1, exitCode, $"{option} must be rejected for an unpackaged app");
        StringAssert.Contains(ConsoleStdErr.ToString(), option);
    }

    [TestMethod]
    public async Task SingleFileMode_UnpackagedApp_StillReadsThePackageGraphFromTheBuildsAssetsFile()
    {
        // An unpackaged app has no manifest, but it still provisions the Windows App Runtime — and that
        // decision is gated on whether the app references the Windows App SDK at all. Dropping the assets
        // file here would re-evaluate the default graph, report "No Windows App SDK reference", and launch
        // an app whose runtime was never prepared.
        var (singleFile, outputDir) = CreateSingleFileApp();
        var assetsFile = Path.Join(outputDir.FullName, "obj", "project.assets.json");
        SetUnpackagedOutcome(singleFile, outputDir, assetsFile);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(assetsFile, _fakeMsixService.EnsureRuntimeInstalledAssetsFileCalls.Single(),
            "The build's own assets file must reach unpackaged runtime provisioning too");
    }

    #endregion

    #region Rejected options

    [TestMethod]
    [DataRow("--project", "MyApp", DisplayName = "--project")]
    [DataRow("--framework", "net10.0-windows10.0.22621.0", DisplayName = "--framework")]
    public async Task SingleFileMode_ProjectOnlyBuildOptions_AreRejectedBeforeBuilding(string option, string value)
    {
        // A file-based app declares its own target framework inline, and it IS the project.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, option, value, "--detach"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveSingleFileCalls.Count, "Rejection must happen before any build work");
    }

    [TestMethod]
    [DataRow("--arch", "arm64", "arm64", DisplayName = "--arch")]
    [DataRow("--runtime", "win-arm64", "arm64", DisplayName = "--runtime")]
    public async Task SingleFileMode_ArchitectureOptions_AreHonored(string option, string value, string expected)
    {
        // winapp conveys the architecture to the build for a .cs exactly as it does for a .csproj, so
        // these are supported rather than rejected.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, option, value, "--detach"]);

        Assert.AreEqual(0, exitCode);
        var options = _fakeProjectRunService.SingleFileBuildOptions.Single();
        Assert.AreEqual(expected, options.Architecture);
        Assert.IsTrue(options.ArchitectureIsExplicit, "An explicit request must override a RuntimeIdentifier declared in the file");
    }

    [TestMethod]
    public async Task SingleFileMode_NoArchitectureOption_UsesTheMachineArchitecture()
    {
        // Without this a .cs builds AnyCPU, and the Windows App SDK self-contained targets fail with
        // "WindowsAppSDKSelfContained requires a supported Windows architecture" — so a plain
        // `winapp run app.cs` could not build a WinUI app at all. Project mode has always injected the RID.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        var options = _fakeProjectRunService.SingleFileBuildOptions.Single();
        Assert.AreEqual(RunArchHelper.DefaultArchitecture(), options.Architecture);
        Assert.IsFalse(options.ArchitectureIsExplicit, "The default must defer to a RuntimeIdentifier declared in the file");
    }

    [TestMethod]
    public async Task SingleFileMode_RejectedOption_EmitsAStructuredErrorUnderJson()
    {
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--framework", "net10.0", "--json"]);

        Assert.AreEqual(1, exitCode);
        var stdout = TestAnsiConsole.Output.Trim();
        using var document = JsonDocument.Parse(stdout);
        var error = document.RootElement.GetProperty("Error").GetString();
        StringAssert.Contains(error, "--framework", "The JSON error should name the rejected option");
        StringAssert.Contains(error, "#:property TargetFramework", "The JSON error should point at the replacement directive");
    }

    [TestMethod]
    public async Task SingleFileMode_MalformedProperty_IsRejected()
    {
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "-p", "A=1;B=2", "--detach"]);

        Assert.AreEqual(1, exitCode, "A packed -p must be rejected in single-file mode as it is in project mode");
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveSingleFileCalls.Count);
    }

    #endregion

    #region Manifest inference

    [TestMethod]
    public async Task SingleFileMode_GeneratesAManifestFromTheDeclaredProperties()
    {
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir, "counter.exe",
            ("WinAppPackageName", "com.contoso.counter"),
            ("WinAppDisplayName", "Contoso Counter"),
            ("WinAppDescription", "Counts things"),
            ("Version", "1.2.3-preview.4"));
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        var root = LoadGeneratedManifest(outputDir).Root!;
        var identity = root.Element(Ns + "Identity")!;
        Assert.AreEqual("com.contoso.counter", identity.Attribute("Name")!.Value);
        Assert.AreEqual("1.2.3.0", identity.Attribute("Version")!.Value, "the semver suffix is cut and the version padded to four parts");
        Assert.AreEqual("Contoso Counter", root.Element(Ns + "Properties")!.Element(Ns + "DisplayName")!.Value);

        var app = root.Element(Ns + "Applications")!.Element(Ns + "Application")!;
        Assert.AreEqual("App", app.Attribute("Id")!.Value, "single-file mode uses a fixed Application Id");
        Assert.AreEqual("counter.exe", app.Attribute("Executable")!.Value,
            "the executable is concrete, not $targetnametoken$.exe, so RestartAgent.exe can't make it ambiguous");

        var visual = app.Element(Uap + "VisualElements")!;
        Assert.AreEqual("Contoso Counter", visual.Attribute("DisplayName")!.Value);
        Assert.AreEqual("Counts things", visual.Attribute("Description")!.Value);
    }

    [TestMethod]
    public async Task SingleFileMode_GeneratedManifestDeclaresOnlyRunFullTrust()
    {
        // WinUI 3 desktop apps are full-trust by default, so capabilities are fixed template boilerplate
        // rather than something a #:property can widen.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var command = GetRequiredService<RunCommand>();

        await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        var capabilities = LoadGeneratedManifest(outputDir).Root!.Element(Ns + "Capabilities")!;
        var names = capabilities.Elements().Select(e => e.Attribute("Name")!.Value).ToList();
        CollectionAssert.AreEqual(new List<string> { "runFullTrust" }, names);
    }

    [TestMethod]
    public async Task SingleFileMode_GeneratesTheDefaultAssetSet()
    {
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var command = GetRequiredService<RunCommand>();

        await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        var assets = new DirectoryInfo(Path.Join(outputDir.FullName, "Assets"));
        Assert.IsTrue(assets.Exists, "The manifest references Assets\\*, so they must be generated alongside it");
        Assert.IsTrue(assets.GetFiles("*.png").Length > 0);
    }

    [TestMethod]
    public async Task SingleFileMode_UnusableVersion_FailsWithoutRegistering()
    {
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir, "counter.exe", ("WinAppVersion", "70000.1.2.3"));
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(1, exitCode, "An out-of-range version is rejected rather than silently truncated");
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count);
    }

    #endregion

    #region Manifest precedence

    [TestMethod]
    public async Task SingleFileMode_ExplicitManifestOption_WinsAndSkipsGeneration()
    {
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var explicitManifest = new FileInfo(Path.Join(_tempDirectory.FullName, $"explicit_{Guid.NewGuid():N}.appxmanifest"));
        File.WriteAllText(explicitManifest.FullName, "<Package/>");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, [singleFile.FullName, "--manifest", explicitManifest.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(explicitManifest.FullName, _fakeMsixService.AddLooseLayoutCalls.Single().ManifestPath);
        Assert.IsFalse(File.Exists(Path.Join(outputDir.FullName, "Package.appxmanifest")),
            "--manifest should short-circuit generation entirely");
    }

    [TestMethod]
    public async Task SingleFileMode_WinAppManifestPath_IsHonoredAndSkipsGeneration()
    {
        var (singleFile, outputDir) = CreateSingleFileApp();
        var declared = new FileInfo(Path.Join(_tempDirectory.FullName, $"declared_{Guid.NewGuid():N}.appxmanifest"));
        File.WriteAllText(declared.FullName, "<Package/>");
        SetOutcome(singleFile, outputDir, "counter.exe", ("WinAppManifestPath", declared.FullName));
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(declared.FullName, _fakeMsixService.AddLooseLayoutCalls.Single().ManifestPath);
        Assert.IsFalse(File.Exists(Path.Join(outputDir.FullName, "Package.appxmanifest")));
    }

    [TestMethod]
    public async Task SingleFileMode_WinAppManifestPathPointingNowhere_FailsWithAnActionableError()
    {
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir, "counter.exe",
            ("WinAppManifestPath", Path.Join(_tempDirectory.FullName, "does-not-exist.appxmanifest")));
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(1, exitCode, "A declared manifest path that resolves to nothing is a misconfiguration, not a reason to generate");
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count);
    }

    [TestMethod]
    public async Task SingleFileMode_ManifestAuthoredNextToTheFile_IsUsedVerbatim()
    {
        // The generated manifest lives in the OUTPUT directory, so probing the output first (as the
        // NuGet targets do) would permanently shadow a manifest the user hand-wrote next to their .cs.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var authored = new FileInfo(Path.Join(singleFile.DirectoryName!, "counter.appxmanifest"));
        File.WriteAllText(authored.FullName, "<Package/>");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(authored.FullName, _fakeMsixService.AddLooseLayoutCalls.Single().ManifestPath);
        Assert.IsFalse(File.Exists(Path.Join(outputDir.FullName, "Package.appxmanifest")),
            "An authored manifest must suppress generation, not be shadowed by it");
    }

    [TestMethod]
    [DataRow("Package.appxmanifest", DisplayName = "Package.appxmanifest")]
    [DataRow("appxmanifest.xml", DisplayName = "appxmanifest.xml")]
    public async Task SingleFileMode_DirectoryWideManifestNextToTheFile_IsNotPickedUpImplicitly(string manifestName)
    {
        // foo.cs and bar.cs can share a source directory, so a directory-wide manifest name would be
        // silently applied to BOTH — registering one under the other's identity. Only the per-file
        // <stem>.appxmanifest is discovered implicitly; a shared one must be named with --manifest.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        File.WriteAllText(Path.Join(singleFile.DirectoryName!, manifestName), "<Package/>");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(
            Path.Join(outputDir.FullName, "Package.appxmanifest"),
            _fakeMsixService.AddLooseLayoutCalls.Single().ManifestPath,
            "A directory-wide manifest beside the .cs must not be adopted; generate one instead");
    }

    [TestMethod]
    public async Task SingleFileMode_PerFileAuthoredManifest_BeatsThePerDirectoryOne()
    {
        // foo.cs and bar.cs can share a source directory, so the per-file name wins when both exist.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var perFile = new FileInfo(Path.Join(singleFile.DirectoryName!, "counter.appxmanifest"));
        File.WriteAllText(perFile.FullName, "<Package/>");
        File.WriteAllText(Path.Join(singleFile.DirectoryName!, "Package.appxmanifest"), "<Package/>");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(perFile.FullName, _fakeMsixService.AddLooseLayoutCalls.Single().ManifestPath);
    }

    #endregion

    #region Architecture

    [TestMethod]
    public async Task SingleFileMode_ProvisionsTheRuntimeForTheAppsArchitecture()
    {
        // Single-file mode rejects --arch, so the architecture the app declared via #:property is the
        // ONLY thing that can reach runtime provisioning. Passing null there would install the machine's
        // architecture instead, and a cross-architecture app would fail to launch.
        var (singleFile, outputDir) = CreateSingleFileApp();
        _fakeProjectRunService.SingleFileBuildOutcome = new SingleFileBuildOutcome(
            new SingleFileRunResolution(
                singleFile, outputDir.FullName, "counter.exe", "arm64", "net10.0-windows10.0.22621.0", false, ProjectPackaging.Packaged, null, null,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)), 0);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        var runtimeCall = _fakeMsixService.AddLooseLayoutRuntimeCalls.Single();
        Assert.AreEqual("arm64", runtimeCall.RuntimeArch);
        Assert.AreEqual("net10.0-windows10.0.22621.0", runtimeCall.Framework,
            "The built TFM must reach runtime provisioning so the Windows App SDK version resolves correctly");
    }

    [TestMethod]
    public async Task SingleFileMode_ResolvesPackagesFromTheCsFile_NotAnUnrelatedProjectInTheCurrentDirectory()
    {
        // With a null project file the loose-layout pipeline globs the CURRENT DIRECTORY for any .csproj
        // and uses its package list. A .cs file-based app has no project, so that could only ever find an
        // unrelated one — and would write ITS Windows App SDK dependency into this app's manifest.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(singleFile.FullName, _fakeMsixService.AddLooseLayoutRuntimeCalls.Single().ProjectFile,
            "The .cs itself must be the package-list source");
    }

    [TestMethod]
    public async Task SingleFileMode_ReadsThePackageGraphFromTheBuildsAssetsFile()
    {
        // `dotnet package list --file` re-evaluates the app and takes no -c/-r/-p, and the environment is
        // not a substitute: a `#:property MyFeature=off` in the file OUTRANKS an environment value, while
        // the build's own -p:MyFeature=on outranks the file. Measured: built that way the package is in
        // the graph, and the same request made through the environment is not. project.assets.json is
        // restore's output for the inputs the build ran with, so it is the only faithful source.
        var (singleFile, outputDir) = CreateSingleFileApp();
        var assetsFile = Path.Join(outputDir.FullName, "obj", "project.assets.json");
        _fakeProjectRunService.SingleFileBuildOutcome = new SingleFileBuildOutcome(
            new SingleFileRunResolution(
                singleFile, outputDir.FullName, "counter.exe", "arm64", "net10.0-windows10.0.22621.0", false,
                ProjectPackaging.Packaged, null, null,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                assetsFile),
            0);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach", "-c", "Release"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(assetsFile, _fakeMsixService.AddLooseLayoutAssetsFileCalls.Single(),
            "The build's own assets file must reach package discovery");
    }

    [TestMethod]
    public async Task SingleFileMode_SelfContainedApp_SkipsFrameworkDependencyAndRuntimeProvisioning()
    {
        // A self-contained app already carries the Windows App SDK. Adding a framework PackageDependency
        // for it makes registration fail on a machine that lacks that framework, even though the app
        // never needed it.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir, "counter.exe", selfContained: true);        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(_fakeMsixService.AddLooseLayoutSelfContainedCalls.Single(),
            "WindowsAppSDKSelfContained must reach the loose-layout pipeline, not be discarded");
    }

    [TestMethod]
    public async Task SingleFileMode_NoRestore_ReachesTheLooseLayoutPipeline()
    {
        // The generated manifest carries no MSBuild metadata, so registration takes the raw-manifest
        // branch. That branch used to hard-code noRestore:false, so `--no-restore` silently still ran an
        // implicit restore during package discovery — contrary to the option and the docs.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--no-restore", "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(_fakeMsixService.AddLooseLayoutRuntimeCalls.Single().NoRestore,
            "--no-restore must be threaded into loose-layout package discovery");
    }

    [TestMethod]
    public async Task SingleFileMode_DefaultsTheExecutableToTheResolvedApp()
    {
        // An authored manifest can still use the $targetnametoken$ placeholder. Resolving it by scanning
        // the output hits "multiple .exe files found", because every WinAppSDK self-contained output ships
        // a RestartAgent.exe beside the app. The build already knows which one is the app.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual("counter.exe", _fakeMsixService.AddLooseLayoutExecutableCalls.Single());
    }

    [TestMethod]
    public async Task SingleFileMode_ExplicitExecutable_OverridesTheResolvedDefault()
    {
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, [singleFile.FullName, "--executable", "other.exe", "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual("other.exe", _fakeMsixService.AddLooseLayoutExecutableCalls.Single(),
            "An explicit --executable must still win over the resolved default");

        // The generated manifest writes a CONCRETE Executable, so the override has to reach generation.
        // Checking only what flows downstream would miss this: placeholder resolution finds nothing to
        // substitute in a generated manifest and silently leaves the build's executable in place.
        var app = LoadGeneratedManifest(outputDir).Root!
            .Element(Ns + "Applications")!.Element(Ns + "Application")!;
        Assert.AreEqual("other.exe", app.Attribute("Executable")!.Value,
            "--executable must be written into the generated manifest, not just passed downstream");
    }

    #endregion

    #region Console apps and execution aliases

    [TestMethod]
    public async Task SingleFileMode_WithAlias_AddsAnExecutionAliasToTheGeneratedManifest()
    {
        // The generated manifest is rebuilt on every run, so "add one with winapp manifest add-alias" is
        // advice a user cannot act on here — any alias they add is destroyed by the next run. Without
        // this, alias launch can never succeed for a file-based app.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var command = GetRequiredService<RunCommand>();

        await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--with-alias"]);

        var root = LoadGeneratedManifest(outputDir).Root!;
        XNamespace uap5 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/5";
        var alias = root.Descendants(uap5 + "ExecutionAlias").SingleOrDefault();
        Assert.IsNotNull(alias, "Alias launch must produce a uap5:ExecutionAlias in the generated manifest");
        var aliasValue = alias.Attribute("Alias")!.Value;
        StringAssert.StartsWith(aliasValue, "winapp-counter-",
            "The alias is derived from the package family name and prefixed, so it cannot collide with a real tool on PATH or with another publisher's same-named app");
        StringAssert.EndsWith(aliasValue, ".exe");
    }

    [TestMethod]
    public async Task SingleFileMode_AliasLaunch_PrintsTheAliasItRegistered()
    {
        // The generated alias carries an opaque publisher hash, so a user cannot work out which command
        // was registered unless winapp names it. Reporting only the AUMID leaves them with a package they
        // can't invoke by name — and the documentation promises the alias is reported. The name is read
        // from the manifest that was actually registered, so it is the one that really works.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var command = GetRequiredService<RunCommand>();

        await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--with-alias"]);

        // Spectre wraps at the profile width and can break a long alias mid-token, so compare with
        // whitespace collapsed rather than asserting on the raw output.
        var output = Regex.Replace(TestAnsiConsole.Output + ConsoleStdOut, @"\s+", "");
        StringAssert.Contains(output, "(alias:winapp-counter-",
            $"The registered alias must be named. Output was: {TestAnsiConsole.Output}");
    }

    [TestMethod]
    public async Task SingleFileMode_CarriesTheExactBuildRid_NotOneRebuiltFromTheArchitecture()
    {
        // Reconstructing "win-x64" from the architecture loses a custom RID: an app declaring
        // `#:property RuntimeIdentifier=win10-x64` is restored under the .../win10-x64 target, so a
        // rebuilt RID matches no target and the reader falls back to the plain TFM — missing exactly the
        // RID-conditional packages the assets path exists to find.
        var (singleFile, outputDir) = CreateSingleFileApp();
        var assetsFile = Path.Join(outputDir.FullName, "obj", "project.assets.json");
        _fakeProjectRunService.SingleFileBuildOutcome = new SingleFileBuildOutcome(
            new SingleFileRunResolution(
                singleFile, outputDir.FullName, "counter.exe", "x64", "net10.0-windows10.0.22621.0", false,
                ProjectPackaging.Packaged, null, null,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                assetsFile,
                "win10-x64"),
            0);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual("win10-x64", _fakeMsixService.AddLooseLayoutRuntimeIdentifierCalls.Single(),
            "The RID the build evaluated must reach the assets reader verbatim");
    }

    [TestMethod]
    public async Task SingleFileMode_WithoutAlias_LeavesTheGeneratedManifestClean()
    {
        // An execution alias registers a global command on the user's PATH, so it is added only when the
        // user asks to launch through one.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var command = GetRequiredService<RunCommand>();

        await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        XNamespace uap5 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/5";
        Assert.AreEqual(0, LoadGeneratedManifest(outputDir).Root!.Descendants(uap5 + "ExecutionAlias").Count(),
            "No alias should be registered unless --with-alias was requested");
    }

    [TestMethod]
    public async Task SingleFileMode_ConsoleApp_LaunchesViaAliasByDefault()
    {
        // The point of the default: a console app launched by AUMID has no console and prints nothing,
        // which is the single most confusing thing about running one with identity. The user should not
        // have to know that to see their own output.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir, "counter.exe", ("OutputType", "Exe"));
        var command = GetRequiredService<RunCommand>();

        await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName]);

        XNamespace uap5 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/5";
        Assert.IsNotNull(LoadGeneratedManifest(outputDir).Root!.Descendants(uap5 + "ExecutionAlias").SingleOrDefault(),
            "A console app should get an execution alias without the user asking for one");
    }

    [TestMethod]
    public async Task SingleFileMode_ConsoleAppWithoutAlias_LaunchesViaAumidAndSaysSo()
    {
        // --without-alias is the opt-out. Losing console output is the surprising part of an otherwise
        // reasonable request, so it is called out rather than left to be discovered.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir, "counter.exe", ("OutputType", "Exe"));
        var command = GetRequiredService<RunCommand>();

        await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--without-alias"]);

        XNamespace uap5 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/5";
        Assert.AreEqual(0, LoadGeneratedManifest(outputDir).Root!.Descendants(uap5 + "ExecutionAlias").Count(),
            "--without-alias must not add an alias");
        StringAssert.Contains(TestAnsiConsole.Output, "will not print here",
            "The user should be told their console app produces no output this way");
    }

    [TestMethod]
    public async Task SingleFileMode_WindowedApp_DoesNotHintAtAlias()
    {
        // A WinExe shows a window, so the hint would be noise.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir, "counter.exe", ("OutputType", "WinExe"));
        var command = GetRequiredService<RunCommand>();

        await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName]);

        Assert.IsFalse(TestAnsiConsole.Output.Contains("gives it no console", StringComparison.Ordinal),
            "A windowed app should not be told about the console hint");
    }

    [TestMethod]
    public async Task SingleFileMode_UseExecutionAliasProperty_LaunchesViaAliasWithoutTheFlag()
    {
        // A windowed app keeps AUMID by default, so the property is how one opts in.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir, "counter.exe",
            ("OutputType", "WinExe"), ("WinAppRunUseExecutionAlias", "true"));
        var command = GetRequiredService<RunCommand>();

        await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName]);

        var root = LoadGeneratedManifest(outputDir).Root!;
        XNamespace uap5 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/5";
        Assert.IsNotNull(root.Descendants(uap5 + "ExecutionAlias").SingleOrDefault(),
            "WinAppRunUseExecutionAlias=true must opt a windowed app into alias launch");
    }

    [TestMethod]
    public async Task SingleFileMode_UseExecutionAliasPropertyFalse_OptsAConsoleAppOutOfTheDefault()
    {
        // The property overrides the output-type default in BOTH directions, so a console app can opt
        // out from inside the file rather than needing --without-alias on every run.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir, "counter.exe",
            ("OutputType", "Exe"), ("WinAppRunUseExecutionAlias", "false"));
        var command = GetRequiredService<RunCommand>();

        await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName]);

        XNamespace uap5 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/5";
        Assert.AreEqual(0, LoadGeneratedManifest(outputDir).Root!.Descendants(uap5 + "ExecutionAlias").Count(),
            "WinAppRunUseExecutionAlias=false must keep AUMID activation for a console app");
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow("ture", DisplayName = "typo")]
    [DataRow("1", DisplayName = "numeric")]
    [DataRow("yes", DisplayName = "yes is not an MSBuild boolean here")]
    public async Task SingleFileMode_MalformedUseExecutionAliasProperty_KeepsTheInferredDefault(string value)
    {
        // Reading a malformed value as an explicit false would silently drop a console app's output, and
        // would disagree with the NuGet targets, whose '== true' / '== false' conditions forward NO switch
        // for a value they do not recognize — the same typo would then mean opposite things through
        // `dotnet run` and a direct `winapp run`.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir, "counter.exe",
            ("OutputType", "Exe"), ("WinAppRunUseExecutionAlias", value));
        var command = GetRequiredService<RunCommand>();

        var (exitCode, ambientOutput) = await InvokeWithAmbientConsoleCaptureAsync(command, [singleFile.FullName]);

        Assert.AreEqual(0, exitCode, "A malformed preference must not fail the run");
        XNamespace uap5 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/5";
        Assert.IsNotNull(
            LoadGeneratedManifest(outputDir).Root!.Descendants(uap5 + "ExecutionAlias").SingleOrDefault(),
            "The console default must still apply when the property cannot be read");
        StringAssert.Contains(ambientOutput, "WinAppRunUseExecutionAlias",
            "A typo that quietly does nothing is invisible; it has to be reported");
    }

    [TestMethod]
    public async Task SingleFileMode_UseExecutionAliasProperty_DoesNotOverrideAnExplicitLaunchSwitch()
    {
        // --detach describes a launch model an alias cannot express. A property in a checked-in app must
        // not turn an unrelated command line into an error, so the command line wins.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir, "counter.exe",
            ("OutputType", "Exe"), ("WinAppRunUseExecutionAlias", "true"));
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode, "--detach must still succeed when the file prefers alias launch");
        XNamespace uap5 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/5";
        Assert.AreEqual(0, LoadGeneratedManifest(outputDir).Root!.Descendants(uap5 + "ExecutionAlias").Count(),
            "The explicit launch switch wins, so no alias is added");
    }

    [TestMethod]
    public async Task SingleFileMode_WithAliasAndWithoutAlias_IsRejected()
    {
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--with-alias", "--without-alias"]);

        Assert.AreNotEqual(0, exitCode, "Asking for both launch mechanisms at once is a contradiction");
        StringAssert.Contains(ConsoleStdErr.ToString(), "cannot be used together",
            "The conflict must be reported, not silently resolved in favor of one of them");
    }

    #endregion

    #region Registration lifetime

    /// <summary>
    /// Widens the test console so an assertion can see a whole command line. Spectre wraps at the profile
    /// width (80 columns by default) and breaks a long path MID-TOKEN, which no substring assertion can
    /// match through.
    /// </summary>
    private void WidenConsoleForCommandAssertions() => TestAnsiConsole.Profile.Width = 500;

    [TestMethod]
    public async Task SingleFileMode_FirstRegistration_SaysThePackageOutlivesTheRun()
    {
        // `winapp run app.cs` leaves a package registered, which is invisible unless we say so — and the
        // generated manifest lives in the SDK's temp output, so the user has no path to point unregister at.
        WidenConsoleForCommandAssertions();
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        _fakePackageRegistrationService.FakeDevPackages = [];
        var command = GetRequiredService<RunCommand>();

        await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        var output = TestAnsiConsole.Output;
        StringAssert.Contains(output, "stays registered");

        // The FULL path, not the bare name: the note is read after the run, from wherever the user happens
        // to be, and `winapp unregister counter.cs` only resolves inside the file's own directory.
        StringAssert.Contains(output, $"winapp unregister {singleFile.FullName}");
    }

    [TestMethod]
    public async Task SingleFileMode_PathWithSpaces_QuotesTheUnregisterCommand()
    {
        // Unquoted, `winapp unregister C:\my apps\counter.cs` parses as two arguments and fails.
        WidenConsoleForCommandAssertions();
        var sourceDir = _tempDirectory.CreateSubdirectory($"my apps {Guid.NewGuid():N}");
        var singleFile = new FileInfo(Path.Join(sourceDir.FullName, "counter.cs"));
        File.WriteAllText(singleFile.FullName, "Console.WriteLine(\"hi\");");
        var outputDir = _tempDirectory.CreateSubdirectory($"runfile_{Guid.NewGuid():N}");
        File.WriteAllText(Path.Join(outputDir.FullName, "counter.exe"), "MZ");

        SetOutcome(singleFile, outputDir);
        _fakePackageRegistrationService.FakeDevPackages = [];
        var command = GetRequiredService<RunCommand>();

        await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        StringAssert.Contains(TestAnsiConsole.Output, $"winapp unregister \"{singleFile.FullName}\"");
    }

    [TestMethod]
    public async Task SingleFileMode_IdentityShapingOptions_AreRepeatedInTheUnregisterCommand()
    {
        // `unregister` re-evaluates the identity from the .cs, and a command-line property overrides the
        // file's own #:property directives — so without these the command resolves a DIFFERENT package.
        WidenConsoleForCommandAssertions();
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        _fakePackageRegistrationService.FakeDevPackages = [];
        var command = GetRequiredService<RunCommand>();

        await ParseAndInvokeWithCaptureAsync(
            command,
            [singleFile.FullName, "--detach", "-c", "Release", "-p", "WinAppPackageName=com.contoso.counter"]);

        var output = TestAnsiConsole.Output;
        StringAssert.Contains(output, "-c Release");
        StringAssert.Contains(output, "-p WinAppPackageName=com.contoso.counter");
    }

    [TestMethod]
    public async Task SingleFileMode_ExplicitManifest_UnregisterCommandNamesTheManifestAlone()
    {
        // `unregister` REJECTS an input alongside --manifest, so naming both would emit a command that
        // cannot run. The manifest states the identity, so it stands on its own.
        WidenConsoleForCommandAssertions();
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var manifest = new FileInfo(Path.Join(outputDir.FullName, "Authored.appxmanifest"));
        File.WriteAllText(
            manifest.FullName,
            """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="com.contoso.authored" Publisher="CN=Test" Version="1.0.0.0" />
            </Package>
            """);
        _fakePackageRegistrationService.FakeDevPackages = [];
        var command = GetRequiredService<RunCommand>();

        await ParseAndInvokeWithCaptureAsync(
            command,
            [singleFile.FullName, "--detach", "--manifest", manifest.FullName]);

        var output = TestAnsiConsole.Output;
        StringAssert.Contains(output, $"winapp unregister --manifest {manifest.FullName}");
        Assert.IsFalse(
            output.Contains($"unregister {singleFile.FullName}", StringComparison.Ordinal),
            "An input alongside --manifest is rejected by unregister");
    }

    [TestMethod]
    public async Task SingleFileMode_NotYetRegistered_ReportsNothingWhenRegistrationFails()
    {
        // The note promises the package outlives the run. Printing it before registration is attempted
        // would state that about a package that does not exist.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        _fakePackageRegistrationService.FakeDevPackages = [];
        _fakeMsixService.ExceptionToThrow = new InvalidOperationException("registration refused");
        var command = GetRequiredService<RunCommand>();

        await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.IsFalse(TestAnsiConsole.Output.Contains("stays registered", StringComparison.Ordinal),
            "Nothing stays registered when registration never succeeded");
    }

    [TestMethod]
    public async Task SingleFileMode_NonstandardOutputPath_UnregisterCommandStillNamesTheLayout()
    {
        // `unregister app.cs` trusts the verified bin\<config> build root, which a nonstandard OutputPath
        // prevents it from resolving — so without the layout the printed command safety-skips and exits 0,
        // leaving the registration and its Start-menu entry behind.
        WidenConsoleForCommandAssertions();
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        _fakePackageRegistrationService.FakeDevPackages = [];
        var command = GetRequiredService<RunCommand>();

        await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        StringAssert.Contains(
            TestAnsiConsole.Output,
            $"--output-appx-directory {Path.Join(outputDir.FullName, "AppX")}");
    }

    [TestMethod]
    public async Task SingleFileMode_ExplicitManifest_UnregisterCommandNamesTheEffectiveLayout()
    {
        // `unregister --manifest` trusts only the manifest's own directory and the current directory, and
        // a file-based app's layout is under %TEMP%\dotnet\runfile — neither of those. Without the layout
        // the printed command reports "registered from a different project tree" and removes nothing.
        WidenConsoleForCommandAssertions();
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var manifest = new FileInfo(Path.Join(_tempDirectory.FullName, $"custom_{Guid.NewGuid():N}.appxmanifest"));
        File.WriteAllText(
            manifest.FullName,
            """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="com.contoso.authored" Publisher="CN=Test" Version="1.0.0.0" />
            </Package>
            """);
        _fakePackageRegistrationService.FakeDevPackages = [];
        var command = GetRequiredService<RunCommand>();

        await ParseAndInvokeWithCaptureAsync(
            command,
            [singleFile.FullName, "--detach", "--manifest", manifest.FullName]);

        StringAssert.Contains(
            TestAnsiConsole.Output,
            $"--output-appx-directory {Path.Join(outputDir.FullName, "AppX")}");
    }

    [TestMethod]
    public async Task SingleFileMode_SecretProperty_IsMaskedInTheUnregisterCommand()
    {
        // The notice is copied into CI logs, so it must not carry a credential the user passed through -p.
        WidenConsoleForCommandAssertions();
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        _fakePackageRegistrationService.FakeDevPackages = [];
        var command = GetRequiredService<RunCommand>();

        await ParseAndInvokeWithCaptureAsync(
            command,
            [singleFile.FullName, "--detach", "-p", "ApiKey=super-secret"]);

        var output = TestAnsiConsole.Output;
        Assert.IsFalse(output.Contains("super-secret", StringComparison.Ordinal),
            "A secret-looking property value must not reach the console");
        StringAssert.Contains(output, "ApiKey=***");
    }

    [TestMethod]
    public async Task SingleFileMode_RegistrationBecomingVisible_StillReportsTheFirstRegistration()
    {
        // The check has to read registration state BEFORE the pipeline. Reading it from the success
        // callback would see the package this very run just created, conclude "already registered", and
        // suppress the notice on every run — including the first, which is the only one it exists for.
        WidenConsoleForCommandAssertions();
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        _fakePackageRegistrationService.FakeDevPackages = [];
        _fakeMsixService.OnAddLooseLayout = () =>
            _fakePackageRegistrationService.FakeDevPackages =
            [
                new DevPackageInfo("counter_1.0.0.0_x64__abc", "counter", "1.0.0.0",
                    Path.Join(outputDir.FullName, "AppX"), IsDevelopmentMode: true)
            ];
        var command = GetRequiredService<RunCommand>();

        await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        StringAssert.Contains(TestAnsiConsole.Output, "stays registered");
    }

    [TestMethod]
    public async Task SingleFileMode_AlreadyRegisteredFromTheSamePlace_StaysQuiet()
    {
        // Re-running REPLACES the app's own registration rather than accumulating another, so repeating
        // the notice on every inner-loop run would be noise that trains users to stop reading output.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("counter_1.0.0.0_x64__abc", "counter", "1.0.0.0",
                Path.Join(outputDir.FullName, "AppX"), IsDevelopmentMode: true)
        ];
        var command = GetRequiredService<RunCommand>();

        await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.IsFalse(TestAnsiConsole.Output.Contains("stays registered", StringComparison.Ordinal),
            "The persistence note should only fire on the run that first registers the identity");
    }

    [TestMethod]
    public async Task SingleFileMode_UnregisterOnExit_SuppressesThePersistenceNote()
    {
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        _fakePackageRegistrationService.FakeDevPackages = [];
        var command = GetRequiredService<RunCommand>();

        await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--unregister-on-exit"]);

        Assert.IsFalse(TestAnsiConsole.Output.Contains("stays registered", StringComparison.Ordinal),
            "The package does not outlive a run that already removes it");
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task SingleFileMode_RegisteredFromElsewhere_WarnsAboutReplacingIt()
    {
        // A dev registration of this app's identity already exists, installed from a DIFFERENT location.
        // Reachable when two .cs files explicitly share one '#:property WinAppPackageName' — the path hash
        // in the default identity is what keeps unrelated same-named files from landing here.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("counter_1.0.0.0_x64__abc", "counter", "1.0.0.0",
                Path.Join(_tempDirectory.FullName, "some_other_app", "AppX"), IsDevelopmentMode: true)
        ];
        var command = GetRequiredService<RunCommand>();

        var (_, ambientOutput) = await InvokeWithAmbientConsoleCaptureAsync(command, [singleFile.FullName, "--detach"]);

        StringAssert.Contains(ambientOutput, "Replacing the existing registration");
        Assert.IsFalse(TestAnsiConsole.Output.Contains("stays registered", StringComparison.Ordinal),
            "The replacement warning already covers this run; a persistence note as well would be noise");
    }

    #endregion

    #region Capabilities

    private static readonly XNamespace SystemAiNs = "http://schemas.microsoft.com/appx/manifest/systemai/windows10";

    [TestMethod]
    public async Task SingleFileMode_DeclaredCapabilities_AreWrittenToTheGeneratedManifest()
    {
        // The scenario that forced this: the Windows AI APIs are gated on systemAIModels, which full
        // trust does not substitute for.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir, "counter.exe", ("WinAppCapabilities", "systemAIModels;internetClient;microphone"));
        var command = GetRequiredService<RunCommand>();

        await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        var capabilities = LoadGeneratedManifest(outputDir).Root!.Element(Ns + "Capabilities")!;

        Assert.IsNotNull(capabilities.Elements(SystemAiNs + "Capability")
            .FirstOrDefault(e => e.Attribute("Name")?.Value == "systemAIModels"),
            "systemAIModels must be a systemai:Capability — the rescap spelling registers but grants nothing");
        Assert.IsNotNull(capabilities.Elements(Ns + "Capability")
            .FirstOrDefault(e => e.Attribute("Name")?.Value == "internetClient"));
        Assert.IsNotNull(capabilities.Elements(Ns + "DeviceCapability")
            .FirstOrDefault(e => e.Attribute("Name")?.Value == "microphone"));
    }

    [TestMethod]
    public async Task SingleFileMode_DeviceCapabilities_AreOrderedAfterEveryCapability()
    {
        // The schema requires it, so declaring a device capability first must not emit it first.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir, "counter.exe", ("WinAppCapabilities", "microphone;internetClient"));
        var command = GetRequiredService<RunCommand>();

        await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        var children = LoadGeneratedManifest(outputDir).Root!.Element(Ns + "Capabilities")!.Elements().ToList();
        var lastCapability = children.FindLastIndex(e => e.Name.LocalName == "Capability");
        var firstDevice = children.FindIndex(e => e.Name.LocalName == "DeviceCapability");

        Assert.IsGreaterThan(-1, firstDevice);
        Assert.IsLessThan(firstDevice, lastCapability, "Every Capability must precede the first DeviceCapability");
    }

    [TestMethod]
    public async Task SingleFileMode_SystemAiCapability_DeclaresItsNamespaceAndRaisesMaxVersionTested()
    {
        // A namespace that is used but undeclared is invalid XML, and a MaxVersionTested below the
        // capability's floor registers successfully while never granting it.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir, "counter.exe", ("WinAppCapabilities", "systemAIModels"));
        var command = GetRequiredService<RunCommand>();

        await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        var root = LoadGeneratedManifest(outputDir).Root!;

        Assert.AreEqual(SystemAiNs.NamespaceName, root.Attribute(XNamespace.Xmlns + "systemai")?.Value);
        StringAssert.Contains(root.Attribute("IgnorableNamespaces")!.Value, "systemai");

        var family = root.Element(Ns + "Dependencies")!.Element(Ns + "TargetDeviceFamily")!;
        Assert.IsTrue(Version.Parse(family.Attribute("MaxVersionTested")!.Value) >= Version.Parse("10.0.26226.0"));
    }

    [TestMethod]
    public async Task SingleFileMode_NoCapabilitiesDeclared_LeavesTheTemplateBlockAlone()
    {
        // The default app declares only the template's runFullTrust, and its MaxVersionTested is not
        // raised by a capability it never asked for.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var command = GetRequiredService<RunCommand>();

        await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        var root = LoadGeneratedManifest(outputDir).Root!;
        Assert.AreEqual(1, root.Element(Ns + "Capabilities")!.Elements().Count());
        Assert.IsNull(root.Attribute(XNamespace.Xmlns + "systemai"));
    }

    [TestMethod]
    public async Task SingleFileMode_UnusableCapability_FailsBeforeRegistering()
    {
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir, "counter.exe", ("WinAppCapabilities", "notARealCapability"));
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count);
    }

    #endregion
}



