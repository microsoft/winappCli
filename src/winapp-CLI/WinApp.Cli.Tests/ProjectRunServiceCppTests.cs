// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class ProjectRunServiceCppTests
{
    private const string CppApp = """
        <Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup Label="Configuration">
            <ConfigurationType>Application</ConfigurationType>
          </PropertyGroup>
        </Project>
        """;

    private const string CppLibrary = """
        <Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup Label="Configuration">
            <ConfigurationType>StaticLibrary</ConfigurationType>
          </PropertyGroup>
        </Project>
        """;

    private const string CsharpApp = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>WinExe</OutputType>
            <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private static readonly string[] ExpectedBuildTokens = ["-restore", "-p:RestorePackagesConfig=true", "-t:Build", "-p:Configuration=Debug", "-p:Platform=x64"];
    private static readonly string[] ExpectedArm64Tokens = ["-p:Foo=Bar", "-p:Configuration=Release", "-p:Platform=ARM64"];
    private static readonly string[] ExpectedPackageIds = ["Microsoft.WindowsAppSDK", "Microsoft.WindowsAppSDK.Runtime"];

    private DirectoryInfo _tempDir = null!;
    private FakeDotNetService _dotnet = null!;
    private FakeMSBuildService _msbuild = null!;
    private ProjectRunService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Directory.CreateDirectory(Path.Join(Path.GetTempPath(), $"ProjectRunServiceCppTests_{Guid.NewGuid():N}"));
        _dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, string.Empty, string.Empty) };
        _msbuild = new FakeMSBuildService();
        _service = new ProjectRunService(
            _dotnet,
            new ProjectDetectionService(NullLogger<ProjectDetectionService>.Instance, _dotnet),
            new FakeCsWinRTMetadataShimService(),
            new TestConsole(),
            NullLogger<ProjectRunService>.Instance,
            _msbuild);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { _tempDir.Delete(true); } catch { /* ignore */ }
    }

    private FileInfo WriteFile(string relativePath, string content)
    {
        var path = Path.Join(_tempDir.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return new FileInfo(path);
    }

    private static string Slnx(params string[] projects) =>
        "<Solution>" + string.Concat(projects.Select(p => $"<Project Path=\"{p}\" />")) + "</Solution>";

    private static string Json(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal);

    #region Input resolution

    [TestMethod]
    public async Task ResolveInput_Vcxproj_ResolvesProjectModeWithOwningSolution()
    {
        var solution = WriteFile("App.slnx", Slnx("App/App.vcxproj"));
        var project = WriteFile(@"App\App.vcxproj", CppApp);

        var resolution = await _service.ResolveInputAsync(project, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual(project.FullName, resolution.Csproj!.FullName);
        Assert.AreEqual(solution.FullName, resolution.Solution!.FullName);
    }

    [TestMethod]
    public async Task ResolveInput_DirectoryWithOneCppApp_ResolvesProjectMode()
    {
        var project = WriteFile("App.vcxproj", CppApp);
        WriteFile("Lib.vcxproj", CppLibrary);

        var resolution = await _service.ResolveInputAsync(_tempDir, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual(project.FullName, resolution.Csproj!.FullName);
    }

    [TestMethod]
    public async Task ResolveInput_DirectoryWithOnlyCppLibrary_StaysFolderMode()
    {
        WriteFile("Lib.vcxproj", CppLibrary);

        var resolution = await _service.ResolveInputAsync(_tempDir, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Folder, resolution.Mode);
    }

    [TestMethod]
    public async Task ResolveInput_DirectoryWithTwoCppApps_RequiresProjectSelector()
    {
        WriteFile("One.vcxproj", CppApp);
        var two = WriteFile("Two.vcxproj", CppApp);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(
            () => _service.ResolveInputAsync(_tempDir, CancellationToken.None));
        StringAssert.Contains(ex.Message, "One.vcxproj, Two.vcxproj");

        var selected = await _service.ResolveInputAsync(_tempDir, CancellationToken.None, projectSelector: "Two");
        Assert.AreEqual(two.FullName, selected.Csproj!.FullName);
    }

    [TestMethod]
    public async Task ResolveInput_CppOnlySln_ResolvesWithoutDotnet()
    {
        var solution = WriteFile("App.sln",
            "Microsoft Visual Studio Solution File, Format Version 12.00" + Environment.NewLine +
            "Project(\"{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}\") = \"App\", \"App\\App.vcxproj\", \"{3B8E5C0A-6D0F-4B6F-9A51-2F7C4D1E8A90}\"" + Environment.NewLine +
            "EndProject");
        var project = WriteFile(@"App\App.vcxproj", CppApp);

        var resolution = await _service.ResolveInputAsync(solution, CancellationToken.None);

        Assert.AreEqual(project.FullName, resolution.Csproj!.FullName);
        Assert.AreEqual(solution.FullName, resolution.Solution!.FullName);
        Assert.AreEqual(0, _dotnet.StringInvocations.Count, "a C++-only solution must not need the .NET SDK");
    }

    [TestMethod]
    public async Task ResolveInput_SolutionWithCsharpAppAndNativeHelperExe_KeepsCsharpApp()
    {
        var solution = WriteFile("App.slnx", Slnx("App/App.csproj", "Helper/Helper.vcxproj"));
        var app = WriteFile(@"App\App.csproj", CsharpApp);
        WriteFile(@"Helper\Helper.vcxproj", CppApp);

        var resolution = await _service.ResolveInputAsync(solution, CancellationToken.None);

        Assert.AreEqual(app.FullName, resolution.Csproj!.FullName);
    }

    [TestMethod]
    public async Task ResolveInput_SolutionProjectSelector_MatchesVcxproj()
    {
        var solution = WriteFile("App.slnx", Slnx("App/App.csproj", "Helper/Helper.vcxproj"));
        WriteFile(@"App\App.csproj", CsharpApp);
        var helper = WriteFile(@"Helper\Helper.vcxproj", CppApp);

        var resolution = await _service.ResolveInputAsync(solution, CancellationToken.None, projectSelector: "Helper");

        Assert.AreEqual(helper.FullName, resolution.Csproj!.FullName);
    }

    #endregion

    #region Build and resolve

    private (FileInfo Project, string OutDir) WritePackagedBuildOutput()
    {
        var project = WriteFile("App.vcxproj", CppApp);
        var outDir = Path.Join(_tempDir.FullName, @"x64\Debug\App") + Path.DirectorySeparatorChar;
        WriteFile(@"x64\Debug\App\AppxManifest.xml", "<Package />");
        WriteFile(@"x64\Debug\App\App.build.appxrecipe", "<Project />");
        WriteFile(@"x64\Debug\App\App.exe", string.Empty);
        return (project, outDir);
    }

    private static string PackagedProperties(string outDir) => $$"""
        { "Properties": {
          "OutDir": "{{Json(outDir)}}",
          "TargetPath": "{{Json(outDir)}}App.exe",
          "ConfigurationType": "Application",
          "AppxPackage": "true",
          "WindowsPackageType": "MSIX",
          "WindowsAppSDKSelfContained": "",
          "AppxPackageRecipe": "{{Json(outDir)}}App.build.appxrecipe",
          "FinalAppxManifestName": "{{Json(outDir)}}AppxManifest.xml",
          "Configuration": "Debug",
          "Platform": "x64" } }
        """;

    [TestMethod]
    public async Task BuildAndResolve_Vcxproj_BuildsWithMSBuildThenResolvesPackagedLayout()
    {
        var (project, outDir) = WritePackagedBuildOutput();
        _msbuild.Replies.Add((new ProcessRunResult(0, string.Empty, string.Empty), []));
        _msbuild.Replies.Add((new ProcessRunResult(0, PackagedProperties(outDir), string.Empty), []));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var outcome = await _service.BuildAndResolveAsync(project, options, CancellationToken.None);

        Assert.AreEqual(2, _msbuild.Calls.Count);
        CollectionAssert.IsSubsetOf(ExpectedBuildTokens, _msbuild.Calls[0].ToArray());
        CollectionAssert.Contains(_msbuild.Calls[1].ToArray(), "-getProperty:OutDir");
        CollectionAssert.DoesNotContain(_msbuild.Calls[1].ToArray(), "-t:Build");
        Assert.AreEqual(0, _dotnet.StringInvocations.Count + _dotnet.ArgumentListInvocations.Count, "C++ builds must not shell out to dotnet");

        var resolution = outcome.Resolution!;
        Assert.AreEqual(ProjectPackaging.Packaged, resolution.Packaging);
        Assert.AreEqual(outDir, resolution.TargetDir);
        Assert.AreEqual(Path.Join(outDir, "App.build.appxrecipe"), resolution.AppxRecipePath);
        Assert.AreEqual(Path.Join(outDir, "AppxManifest.xml"), resolution.AppxManifestPath);
        Assert.AreEqual("Debug", resolution.Configuration);
        Assert.AreEqual("x64", resolution.Platform);
    }

    [TestMethod]
    public async Task BuildAndResolve_Vcxproj_NoBuild_OnlyEvaluates()
    {
        var (project, outDir) = WritePackagedBuildOutput();
        _msbuild.Replies.Add((new ProcessRunResult(0, PackagedProperties(outDir), string.Empty), []));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: true, NoRestore: false, Properties: []);

        var outcome = await _service.BuildAndResolveAsync(project, options, CancellationToken.None);

        Assert.AreEqual(1, _msbuild.Calls.Count);
        CollectionAssert.DoesNotContain(_msbuild.Calls[0].ToArray(), "-t:Build");
        Assert.AreEqual(ProjectPackaging.Packaged, outcome.Resolution!.Packaging);
    }

    [TestMethod]
    public async Task BuildAndResolve_Vcxproj_NoRestore_SkipsRestore()
    {
        var (project, outDir) = WritePackagedBuildOutput();
        _msbuild.Replies.Add((new ProcessRunResult(0, string.Empty, string.Empty), []));
        _msbuild.Replies.Add((new ProcessRunResult(0, PackagedProperties(outDir), string.Empty), []));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: true, Properties: []);

        await _service.BuildAndResolveAsync(project, options, CancellationToken.None);

        CollectionAssert.DoesNotContain(_msbuild.Calls[0].ToArray(), "-restore");
    }

    [TestMethod]
    public async Task BuildAndResolve_Vcxproj_MissingWindowsSdk_ExplainsWhatToInstall()
    {
        var project = WriteFile("App.vcxproj", CppApp);
        _msbuild.Replies.Add((new ProcessRunResult(1, string.Empty, string.Empty),
            [@"Microsoft.Cpp.WindowsSDK.targets(46,5): error MSB8036: The Windows SDK version 10.0.99999.0 was not found."]));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: true);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(
            () => _service.BuildAndResolveAsync(project, options, CancellationToken.None));

        StringAssert.Contains(ex.Message, "MSB8036");
        StringAssert.Contains(ex.Message, "winget install Microsoft.WindowsSDK.");
    }

    [TestMethod]
    public async Task BuildAndResolve_Vcxproj_OtherBuildFailure_ReturnsExitCode()
    {
        var project = WriteFile("App.vcxproj", CppApp);
        _msbuild.Replies.Add((new ProcessRunResult(1, string.Empty, string.Empty), ["main.cpp(1): error C2065: 'x': undeclared identifier"]));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: true);

        var outcome = await _service.BuildAndResolveAsync(project, options, CancellationToken.None);

        Assert.IsNull(outcome.Resolution);
        Assert.AreEqual(1, outcome.ExitCode);
        Assert.AreEqual(1, _msbuild.Calls.Count, "a failed build must not be evaluated");
    }

    [TestMethod]
    public async Task BuildAndResolve_Vcxproj_MissingToolchain_SurfacesGuidance()
    {
        var project = WriteFile("App.vcxproj", CppApp);
        _msbuild.LocateFailure = new ProjectRunException(MSBuildService.BuildMissingToolchainMessage("x64", installedProduct: null));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(
            () => _service.BuildAndResolveAsync(project, options, CancellationToken.None));

        StringAssert.Contains(ex.Message, "Desktop development with C++");
        Assert.AreEqual(0, _msbuild.Calls.Count);
    }

    [TestMethod]
    public void BuildCppPropertyTokens_MapsArchitectureAndHonorsUserPlatformAndSolution()
    {
        var solution = WriteFile("App.slnx", Slnx("App.vcxproj"));

        var arm64 = ProjectRunService.BuildCppPropertyTokens(
            new ProjectRunOptions("Release", "arm64", null, false, false, ["Configuration=Debug", "Foo=Bar"], Solution: solution));
        var x86 = ProjectRunService.BuildCppPropertyTokens(new ProjectRunOptions("Debug", "x86", null, false, false, []));
        var userPlatform = ProjectRunService.BuildCppPropertyTokens(new ProjectRunOptions("Debug", "x64", null, false, false, ["Platform=Custom"]));

        CollectionAssert.IsSubsetOf(ExpectedArm64Tokens, arm64);
        CollectionAssert.DoesNotContain(arm64, "-p:Configuration=Debug", "-c wins over -p Configuration");
        Assert.IsTrue(arm64.Any(t => t.StartsWith("-p:SolutionDir=", StringComparison.Ordinal)));
        CollectionAssert.Contains(x86, "-p:Platform=Win32");
        CollectionAssert.Contains(userPlatform, "-p:Platform=Custom");
        Assert.IsFalse(userPlatform.Any(t => t == "-p:Platform=x64"), "a user -p Platform must not be overridden");
    }

    [TestMethod]
    public void CreateCppResolution_Unpackaged_LaunchesTargetPath()
    {
        var project = WriteFile("App.vcxproj", CppApp);
        var exe = WriteFile(@"x64\Debug\App.exe", string.Empty);
        var props = new Dictionary<string, string>
        {
            ["OutDir"] = exe.DirectoryName + Path.DirectorySeparatorChar,
            ["TargetPath"] = exe.FullName,
            ["ConfigurationType"] = "Application",
            ["AppxPackage"] = "true",
            ["WindowsPackageType"] = "None",
        };

        var resolution = ProjectRunService.CreateCppResolution(project, new ProjectRunOptions("Debug", "x64", null, false, false, []), props);

        Assert.AreEqual(ProjectPackaging.Unpackaged, resolution.Packaging);
        Assert.AreEqual(exe.FullName, resolution.RunCommand);
        Assert.IsNull(resolution.AppxRecipePath);
    }

    [TestMethod]
    public void CreateCppResolution_Library_IsRejected()
    {
        var project = WriteFile("Lib.vcxproj", CppLibrary);
        var props = new Dictionary<string, string> { ["ConfigurationType"] = "DynamicLibrary", ["OutDir"] = _tempDir.FullName };

        var ex = Assert.ThrowsExactly<ProjectRunException>(
            () => ProjectRunService.CreateCppResolution(project, new ProjectRunOptions("Debug", "x64", null, false, false, []), props));

        StringAssert.Contains(ex.Message, "ConfigurationType='DynamicLibrary'");
    }

    [TestMethod]
    public void CreateCppResolution_UnpackagedWithoutExe_UnderNoBuild_SuggestsBuilding()
    {
        var project = WriteFile("App.vcxproj", CppApp);
        var props = new Dictionary<string, string>
        {
            ["OutDir"] = _tempDir.FullName,
            ["TargetPath"] = Path.Join(_tempDir.FullName, "App.exe"),
            ["ConfigurationType"] = "Application",
        };

        var ex = Assert.ThrowsExactly<ProjectRunException>(
            () => ProjectRunService.CreateCppResolution(project, new ProjectRunOptions("Debug", "x64", null, NoBuild: true, false, []), props));

        StringAssert.Contains(ex.Message, "Remove --no-build");
    }

    #endregion

    #region Toolchain discovery

    private sealed class ScriptedProcessRunner(Func<IReadOnlyList<string>, ProcessRunResult> reply) : IProcessRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, Action<string>? onOutputLine = null, Action<string>? onErrorLine = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(request.Arguments);
            return Task.FromResult(reply(request.Arguments));
        }
    }

    [TestMethod]
    public async Task LocateCppMSBuild_NoVsWhere_ExplainsWhatToInstall()
    {
        var runner = new ScriptedProcessRunner(_ => throw new AssertFailedException("vswhere must not run when it is absent"));
        var service = new MSBuildService(runner) { VsWherePath = Path.Join(_tempDir.FullName, "missing", "vswhere.exe") };

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => service.LocateCppMSBuildAsync("x64", CancellationToken.None));

        StringAssert.Contains(ex.Message, "no Visual Studio or Build Tools for Visual Studio installation was found");
        StringAssert.Contains(ex.Message, "winget install Microsoft.VisualStudio.BuildTools");
    }

    [TestMethod]
    [DataRow("x64", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64")]
    [DataRow("arm64", "Microsoft.VisualStudio.Component.VC.Tools.ARM64")]
    public async Task LocateCppMSBuild_RequiresVcToolsForTargetArchitecture(string architecture, string component)
    {
        var vswhere = WriteFile(@"Installer\vswhere.exe", string.Empty);
        var msbuild = WriteFile(@"VS\MSBuild\Current\Bin\MSBuild.exe", string.Empty);
        var runner = new ScriptedProcessRunner(_ => new ProcessRunResult(0, msbuild.FullName + Environment.NewLine, string.Empty));
        var service = new MSBuildService(runner) { VsWherePath = vswhere.FullName };

        var located = await service.LocateCppMSBuildAsync(architecture, CancellationToken.None);

        Assert.AreEqual(msbuild.FullName, located);
        CollectionAssert.Contains(runner.Calls[0].ToArray(), component);
    }

    [TestMethod]
    public async Task LocateCppMSBuild_VisualStudioWithoutCppTools_NamesTheInstall()
    {
        var vswhere = WriteFile(@"Installer\vswhere.exe", string.Empty);
        var runner = new ScriptedProcessRunner(args => args.Contains("-find")
            ? new ProcessRunResult(0, string.Empty, string.Empty)
            : new ProcessRunResult(0, "Visual Studio Community 2026" + Environment.NewLine, string.Empty));
        var service = new MSBuildService(runner) { VsWherePath = vswhere.FullName };

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => service.LocateCppMSBuildAsync("arm64", CancellationToken.None));

        StringAssert.Contains(ex.Message, "Visual Studio Community 2026 does not have them installed");
        StringAssert.Contains(ex.Message, "modify Visual Studio Community 2026");
        StringAssert.Contains(ex.Message, "Microsoft.VisualStudio.Component.VC.Tools.ARM64");
    }

    #endregion

    #region packages.config

    [TestMethod]
    public void PackagesConfigReader_ReadsPinnedPackages()
    {
        var project = WriteFile("App.vcxproj", CppApp);
        WriteFile("packages.config", """
            <?xml version="1.0" encoding="utf-8"?>
            <packages>
              <package id="Microsoft.WindowsAppSDK" version="2.3.1" targetFramework="native" />
              <package id="Microsoft.WindowsAppSDK.Runtime" version="2.3.1" targetFramework="native" />
            </packages>
            """);

        var list = PackagesConfigReader.Read(project)!;

        var packages = list.Projects.Single().Frameworks.Single().TopLevelPackages;
        CollectionAssert.AreEqual(ExpectedPackageIds, packages.Select(p => p.Id).ToArray());
        Assert.AreEqual("2.3.1", packages[1].ResolvedVersion);
        Assert.IsTrue(MsixService.ReferencesWindowsAppSdk(list));
    }

    [TestMethod]
    public void PackagesConfigReader_NoPackagesConfig_ReportsNoPackages()
    {
        var project = WriteFile("App.vcxproj", CppApp);

        var list = PackagesConfigReader.Read(project)!;

        Assert.IsFalse(MsixService.ReferencesWindowsAppSdk(list), "a C++ app without packages.config does not need the Windows App Runtime");
    }

    [TestMethod]
    public void PackagesConfigReader_MalformedFile_IsUnknown()
    {
        var project = WriteFile("App.vcxproj", CppApp);
        WriteFile("packages.config", "<packages>");

        Assert.IsNull(PackagesConfigReader.Read(project));
    }

    #endregion
}
