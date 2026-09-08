// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class ProjectContextDetectorTests
{
    private DirectoryInfo _root = null!;
    private ProjectContextDetector _detector = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Directory.CreateDirectory(Path.Join(
            Path.GetTempPath(),
            $"{nameof(ProjectContextDetectorTests)}_{Guid.NewGuid():N}"));
        _detector = new ProjectContextDetector();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_root.Exists)
        {
            _root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void DetectProject_ClassifiesKnownDotnetFrameworkProperties()
    {
        AssertDotnetFramework("winui", "<UseWinUI>true</UseWinUI>", ProjectAppFramework.WinUI);
        AssertDotnetFramework("wpf", "<UseWPF>true</UseWPF>", ProjectAppFramework.Wpf);
        AssertDotnetFramework("winforms", "<UseWindowsForms>true</UseWindowsForms>", ProjectAppFramework.WinForms);
        AssertDotnetFramework("maui", "<UseMaui>true</UseMaui>", ProjectAppFramework.Maui);
    }

    [TestMethod]
    public void DetectProject_PrefersMauiOverItsWindowsUiImplementation()
    {
        var project = CreateProject("maui-winui", """
            <UseWinUI>true</UseWinUI>
            <UseMaui>true</UseMaui>
            """);

        var context = _detector.DetectProject(project);

        Assert.AreEqual(ProjectFamily.Dotnet, context.Family);
        Assert.AreEqual(ProjectAppFramework.Maui, context.Framework);
        Assert.AreEqual(ProjectContextSource.ResolvedProject, context.Source);
        Assert.AreEqual(ProjectContextConfidence.High, context.Confidence);
    }

    [TestMethod]
    public void DetectProject_ClassifiesAvaloniaAndWindowsAppSdkPackageReferences()
    {
        var avalonia = CreateProject(
            "avalonia",
            string.Empty,
            """<PackageReference Include="Avalonia.Desktop" Version="11.0.0" />""");
        var windowsAppSdk = CreateProject(
            "windows-app-sdk",
            string.Empty,
            """<PackageReference Include="Microsoft.WindowsAppSDK" Version="1.8.0" />""");

        Assert.AreEqual(ProjectAppFramework.Avalonia, _detector.DetectProject(avalonia).Framework);
        Assert.AreEqual(ProjectAppFramework.WindowsAppSdk, _detector.DetectProject(windowsAppSdk).Framework);
    }

    [TestMethod]
    public void DetectProject_ClassifiesUwpAndUnpackagedMetadata()
    {
        var uwp = CreateProject(
            "uwp",
            """
            <TargetPlatformIdentifier>UAP</TargetPlatformIdentifier>
            """);
        var unpackaged = CreateProject(
            "unpackaged",
            """
            <UseWinUI>true</UseWinUI>
            <WindowsPackageType>None</WindowsPackageType>
            """);

        Assert.AreEqual(ProjectAppFramework.Uwp, _detector.DetectProject(uwp).Framework);
        Assert.AreEqual(ProjectContextPackaging.Unpackaged, _detector.DetectProject(unpackaged).Packaging);
    }

    [TestMethod]
    public void DetectDirectory_ClassifiesNodeFrameworksFromAllowListedMetadata()
    {
        var electron = CreateDirectory("electron");
        File.WriteAllText(
            Path.Join(electron.FullName, "package.json"),
            """{"devDependencies":{"electron":"1.0.0"}}""");

        var reactNative = CreateDirectory("react-native");
        File.WriteAllText(
            Path.Join(reactNative.FullName, "package.json"),
            """{"dependencies":{"react-native-windows":"1.0.0"}}""");

        var nodeWinUi = CreateDirectory("node-winui");
        File.WriteAllText(
            Path.Join(nodeWinUi.FullName, "package.json"),
            """
            {
              "winapp": {
                "jsBindings": {
                  "additionalWinmds": [
                    { "namespace": "Microsoft.UI.Xaml.Controls", "classes": ["Button"] }
                  ]
                }
              }
            }
            """);

        Assert.AreEqual(ProjectAppFramework.Electron, _detector.DetectDirectory(electron).Framework);
        Assert.AreEqual(ProjectAppFramework.ReactNativeWindows, _detector.DetectDirectory(reactNative).Framework);
        Assert.AreEqual(ProjectAppFramework.WinUI, _detector.DetectDirectory(nodeWinUi).Framework);
    }

    [TestMethod]
    [DataRow("[]")]
    [DataRow("""{"winapp":true}""")]
    [DataRow("""{"winapp":{"jsBindings":true}}""")]
    public void DetectDirectory_NonObjectPackageMetadata_DegradesToUnknown(string packageJson)
    {
        var directory = CreateDirectory($"invalid-shape-{Guid.NewGuid():N}");
        File.WriteAllText(Path.Join(directory.FullName, "package.json"), packageJson);

        var context = _detector.DetectDirectory(directory);

        Assert.AreEqual(ProjectFamily.Node, context.Family);
        Assert.AreEqual(ProjectAppFramework.Unknown, context.Framework);
        Assert.AreEqual(ProjectContextConfidence.Medium, context.Confidence);
    }

    [TestMethod]
    public void DetectDirectory_ClassifiesNativeFlutterAndTauriMarkers()
    {
        var cpp = CreateDirectory("cpp");
        File.WriteAllText(Path.Join(cpp.FullName, "CMakeLists.txt"), "project(app)");
        File.WriteAllText(
            Path.Join(cpp.FullName, "winapp.yaml"),
            """
            packages:
              - name: Microsoft.WindowsAppSDK
                version: 1.8.0
            """);

        var rust = CreateDirectory("rust");
        File.WriteAllText(Path.Join(rust.FullName, "Cargo.toml"), "[package]");

        var flutter = CreateDirectory("flutter");
        File.WriteAllText(Path.Join(flutter.FullName, "pubspec.yaml"), "name: app");

        var tauri = CreateDirectory("tauri");
        var tauriSource = Directory.CreateDirectory(Path.Join(tauri.FullName, "src-tauri"));
        File.WriteAllText(Path.Join(tauriSource.FullName, "tauri.conf.json"), "{}");

        var cppContext = _detector.DetectDirectory(cpp);
        Assert.AreEqual(ProjectFamily.Cpp, cppContext.Family);
        Assert.AreEqual(ProjectAppFramework.WindowsAppSdk, cppContext.Framework);
        Assert.AreEqual(ProjectFamily.Rust, _detector.DetectDirectory(rust).Family);
        Assert.AreEqual(ProjectAppFramework.Flutter, _detector.DetectDirectory(flutter).Framework);

        var tauriContext = _detector.DetectDirectory(tauri);
        Assert.AreEqual(ProjectFamily.Hybrid, tauriContext.Family);
        Assert.AreEqual(ProjectAppFramework.Tauri, tauriContext.Framework);
    }

    [TestMethod]
    public void DetectDirectory_WalksOnlyBoundedAncestorsAndStopsAtRepositoryBoundary()
    {
        File.WriteAllText(Path.Join(_root.FullName, ".git"), "gitdir: elsewhere");
        CreateProject("app", "<UseWPF>true</UseWPF>", directory: _root);
        var output = Directory.CreateDirectory(Path.Join(_root.FullName, "bin", "Debug", "net10.0", "win-x64"));

        var context = _detector.DetectDirectory(output, ProjectTargetKind.BuildOutput);

        Assert.AreEqual(ProjectFamily.Dotnet, context.Family);
        Assert.AreEqual(ProjectAppFramework.Wpf, context.Framework);
        Assert.AreEqual(ProjectTargetKind.SourceProject, context.TargetKind);
        Assert.AreEqual(ProjectContextSource.AncestorMarker, context.Source);
        Assert.AreEqual(ProjectContextConfidence.Medium, context.Confidence);

        var nestedRepository = Directory.CreateDirectory(Path.Join(_root.FullName, "nested-repo"));
        File.WriteAllText(Path.Join(nestedRepository.FullName, ".git"), "gitdir: nested");
        var nestedOutput = Directory.CreateDirectory(Path.Join(nestedRepository.FullName, "bin"));

        var nestedContext = _detector.DetectDirectory(nestedOutput, ProjectTargetKind.BuildOutput);

        Assert.AreEqual(ProjectFamily.Unknown, nestedContext.Family);
        Assert.AreEqual(ProjectTargetKind.BuildOutput, nestedContext.TargetKind);
        Assert.AreEqual(ProjectContextSource.None, nestedContext.Source);
    }

    [TestMethod]
    public void DetectDirectories_ReportsMixedWithoutReturningProjectIdentity()
    {
        var wpf = CreateDirectory("wpf");
        CreateProject("app", "<UseWPF>true</UseWPF>", directory: wpf);
        var electron = CreateDirectory("electron-mixed");
        File.WriteAllText(
            Path.Join(electron.FullName, "package.json"),
            """{"dependencies":{"electron":"1.0.0"}}""");

        var context = _detector.DetectDirectories(
            [wpf, electron],
            ProjectTargetKind.BuildOutput);

        Assert.AreEqual(ProjectFamily.Mixed, context.Family);
        Assert.AreEqual(ProjectAppFramework.Mixed, context.Framework);
        Assert.AreEqual(ProjectContextConfidence.High, context.Confidence);
    }

    [TestMethod]
    public void CreateNuGetContext_UsesOnlyAllowListedFrameworkHints()
    {
        var winui = _detector.CreateNuGetContext("winui");
        var arbitrary = _detector.CreateNuGetContext("C:\\private\\project-name");

        Assert.AreEqual(ProjectFamily.Dotnet, winui.Family);
        Assert.AreEqual(ProjectAppFramework.WinUI, winui.Framework);
        Assert.AreEqual(ProjectTargetKind.SourceProject, winui.TargetKind);
        Assert.AreEqual(ProjectContextSource.NuGetMsBuild, winui.Source);
        Assert.AreEqual(ProjectContextPackaging.Packaged, winui.Packaging);
        Assert.AreEqual(ProjectExecutionMode.Folder, winui.ExecutionMode);
        Assert.AreEqual(ProjectContextConfidence.High, winui.Confidence);

        Assert.AreEqual(ProjectAppFramework.OtherDotnet, arbitrary.Framework);
        Assert.AreEqual(ProjectContextConfidence.Medium, arbitrary.Confidence);
    }

    private void AssertDotnetFramework(
        string directoryName,
        string properties,
        ProjectAppFramework expectedFramework)
    {
        var context = _detector.DetectProject(CreateProject(directoryName, properties));

        Assert.AreEqual(ProjectFamily.Dotnet, context.Family);
        Assert.AreEqual(expectedFramework, context.Framework);
    }

    private FileInfo CreateProject(
        string directoryName,
        string properties,
        string items = "",
        DirectoryInfo? directory = null)
    {
        directory ??= CreateDirectory(directoryName);
        var path = Path.Join(directory.FullName, $"{directoryName}.csproj");
        File.WriteAllText(
            path,
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                {properties}
              </PropertyGroup>
              <ItemGroup>
                {items}
              </ItemGroup>
            </Project>
            """);
        return new FileInfo(path);
    }

    private DirectoryInfo CreateDirectory(string name) =>
        Directory.CreateDirectory(Path.Join(_root.FullName, name));
}
