// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class LegacyUwpRunServiceTests
{
    private DirectoryInfo _tempDirectory = null!;
    private FakeProcessRunner _processRunner = null!;
    private FakePackageRegistrationService _packageRegistration = null!;
    private LegacyUwpRunService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDirectory = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"LegacyUwpRunServiceTests_{Guid.NewGuid():N}"));
        _processRunner = new FakeProcessRunner();
        _packageRegistration = new FakePackageRegistrationService();
        _service = new LegacyUwpRunService(
            _processRunner,
            _packageRegistration,
            new TestConsole(),
            NullLogger<LegacyUwpRunService>.Instance)
        {
            LocateWindowsSdkVersions = () => [new Version(10, 0, 26100, 0)],
        };
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_tempDirectory.Exists)
        {
            _tempDirectory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void IsLegacyUwpProject_DetectsUapAndAppContainerExe()
    {
        var uap = WriteProject("""
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <OutputType>AppContainerExe</OutputType>
                <TargetPlatformIdentifier>UAP</TargetPlatformIdentifier>
              </PropertyGroup>
            </Project>
            """);

        Assert.IsTrue(_service.IsLegacyUwpProject(uap));
    }

    [TestMethod]
    public void SelectTargetSdk_UsesInstalledExactVersionThenCompatibleFallback()
    {
        var installed = new[]
        {
            new Version(10, 0, 19041, 0),
            new Version(10, 0, 26100, 0),
        };

        Assert.AreEqual(
            new Version(10, 0, 19041, 0),
            LegacyUwpRunService.SelectTargetSdk("10.0.19041.0", "10.0.17763.0", installed));
        Assert.AreEqual(
            new Version(10, 0, 26100, 0),
            LegacyUwpRunService.SelectTargetSdk("10.0.17763.0", "10.0.17763.0", installed));
    }

    [TestMethod]
    public void BuildMsBuildArguments_UsesPlatformSdkOverrideAndUnsignedLooseLayout()
    {
        var project = WriteProject(ProjectXml);
        var options = new LegacyUwpRunOptions(
            "Debug",
            "x64",
            NoBuild: false,
            NoRestore: false,
            Properties: ["CustomProperty=Value", "Platform=arm64"]);

        var arguments = LegacyUwpRunService.BuildMsBuildArguments(
            project,
            options,
            new Version(10, 0, 26100, 0));

        CollectionAssert.Contains(arguments.ToArray(), "-restore");
        CollectionAssert.Contains(arguments.ToArray(), "-property:CustomProperty=Value");
        CollectionAssert.Contains(arguments.ToArray(), "-property:Platform=x64");
        CollectionAssert.DoesNotContain(arguments.ToArray(), "-property:Platform=arm64");
        CollectionAssert.Contains(arguments.ToArray(), "-property:TargetPlatformVersion=10.0.26100.0");
        CollectionAssert.Contains(arguments.ToArray(), "-property:AppxPackageSigningEnabled=false");
        CollectionAssert.Contains(arguments.ToArray(), "-property:GenerateAppxPackageOnBuild=false");
    }

    [TestMethod]
    public async Task BuildAndPrepareAsync_UsesVisualStudioMsBuildAndResolvesLooseLayout()
    {
        var project = WriteProject(ProjectXml);
        var msbuild = new FileInfo(Path.Combine(_tempDirectory.FullName, "MSBuild.exe"));
        File.WriteAllText(msbuild.FullName, string.Empty);
        _service.LocateVisualStudioMsBuild = () => msbuild;
        var layout = CreateLayout(project, dependenciesXml: null);

        var outcome = await _service.BuildAndPrepareAsync(
            project,
            new LegacyUwpRunOptions("Debug", "x64", false, false, []),
            CancellationToken.None);

        Assert.AreEqual(0, outcome.ExitCode);
        Assert.AreEqual(layout.FullName, outcome.LayoutDirectory!.FullName);
        Assert.AreEqual(1, _processRunner.Requests.Count);
        Assert.AreEqual(msbuild.FullName, _processRunner.Requests[0].FileName);
        CollectionAssert.Contains(
            _processRunner.Requests[0].Arguments.ToArray(),
            "-property:TargetPlatformVersion=10.0.26100.0");
    }

    [TestMethod]
    public async Task BuildAndPrepareAsync_MissingVisualStudioMsBuild_HasActionableError()
    {
        var project = WriteProject(ProjectXml);
        _service.LocateVisualStudioMsBuild = () => null;

        var error = await Assert.ThrowsExactlyAsync<ProjectRunException>(() =>
            _service.BuildAndPrepareAsync(
                project,
                new LegacyUwpRunOptions("Debug", "x64", false, false, []),
                CancellationToken.None));

        StringAssert.Contains(error.Message, "Visual Studio MSBuild");
        StringAssert.Contains(error.Message, "UWP build tools");
    }

    [TestMethod]
    public async Task BuildAndPrepareAsync_InstallsMissingFrameworkFromRestoredAssets()
    {
        var project = WriteProject(ProjectXml);
        var layout = CreateLayout(
            project,
            """<PackageDependency Name="Contoso.Framework" MinVersion="2.0.0.0" Publisher="CN=Contoso" />""");
        var packageRoot = Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, "packages"));
        var libraryDirectory = Directory.CreateDirectory(Path.Combine(packageRoot.FullName, "contoso.framework", "2.0.0"));
        var appx = Path.Combine(libraryDirectory.FullName, "Contoso.Framework.appx");
        CreateFrameworkAppx(appx, "Contoso.Framework", "2.0.0.0", "x64");

        var obj = Directory.CreateDirectory(Path.Combine(project.DirectoryName!, "obj"));
        File.WriteAllText(
            Path.Combine(obj.FullName, "project.assets.json"),
            $$"""
              {
                "packageFolders": { "{{EscapeJson(packageRoot.FullName + Path.DirectorySeparatorChar)}}": {} },
                "libraries": {
                  "Contoso.Framework/2.0.0": {
                    "path": "contoso.framework/2.0.0"
                  }
                }
              }
              """);

        var outcome = await _service.BuildAndPrepareAsync(
            project,
            new LegacyUwpRunOptions("Debug", "x64", NoBuild: true, NoRestore: true, Properties: []),
            CancellationToken.None);

        Assert.AreEqual(layout.FullName, outcome.LayoutDirectory!.FullName);
        CollectionAssert.AreEqual(new[] { appx }, _packageRegistration.InstallPackageCalls);
    }

    [TestMethod]
    public async Task BuildAndPrepareAsync_NoBuild_DoesNotRequireVisualStudioOrInstalledSdk()
    {
        var project = WriteProject(ProjectXml);
        var layout = CreateLayout(project, dependenciesXml: null);
        _service.LocateVisualStudioMsBuild = () => null;
        _service.LocateWindowsSdkVersions = () => [];

        var outcome = await _service.BuildAndPrepareAsync(
            project,
            new LegacyUwpRunOptions("Debug", "x64", NoBuild: true, NoRestore: true, Properties: []),
            CancellationToken.None);

        Assert.AreEqual(layout.FullName, outcome.LayoutDirectory!.FullName);
        Assert.AreEqual(0, _processRunner.Requests.Count);
    }

    private const string ProjectXml = """
        <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup>
            <OutputType>AppContainerExe</OutputType>
            <TargetPlatformIdentifier>UAP</TargetPlatformIdentifier>
            <TargetPlatformVersion>10.0.17763.0</TargetPlatformVersion>
            <TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
          </PropertyGroup>
        </Project>
        """;

    private FileInfo WriteProject(string content)
    {
        var project = new FileInfo(Path.Combine(_tempDirectory.FullName, "App.csproj"));
        File.WriteAllText(project.FullName, content);
        return project;
    }

    private static DirectoryInfo CreateLayout(FileInfo project, string? dependenciesXml)
    {
        var layout = Directory.CreateDirectory(
            Path.Combine(project.DirectoryName!, "bin", "x64", "Debug"));
        File.WriteAllText(
            Path.Combine(layout.FullName, "AppxManifest.xml"),
            $$"""
              <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
                <Identity Name="Test.App" Publisher="CN=Test" Version="1.0.0.0" ProcessorArchitecture="x64" />
                <Dependencies>
                  <TargetDeviceFamily Name="Windows.Universal" MinVersion="10.0.17763.0" MaxVersionTested="10.0.26100.0" />
                  {{dependenciesXml}}
                </Dependencies>
              </Package>
              """);
        return layout;
    }

    private static void CreateFrameworkAppx(string path, string name, string version, string architecture)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var manifest = archive.CreateEntry("AppxManifest.xml");
        using var writer = new StreamWriter(manifest.Open());
        writer.Write(
            $$"""
              <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
                <Identity Name="{{name}}" Publisher="CN=Contoso" Version="{{version}}" ProcessorArchitecture="{{architecture}}" />
              </Package>
              """);
    }

    private static string EscapeJson(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal);

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public List<ProcessRunRequest> Requests { get; } = [];
        public ProcessRunResult Result { get; set; } = new(0, string.Empty, string.Empty);

        public Task<ProcessRunResult> RunAsync(
            ProcessRunRequest request,
            Action<string>? onOutputLine = null,
            Action<string>? onErrorLine = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Result);
        }
    }
}
