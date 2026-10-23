// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class ProjectDetectionServiceTests
{
    private string _tempDir = null!;
    private ProjectDetectionService _sut = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ProjectDetectTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _sut = new ProjectDetectionService(NullLogger<ProjectDetectionService>.Instance, new FakeDotNetService());
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    private DirectoryInfo Root => new(_tempDir);

    private string CreateDir(params string[] segments)
    {
        var path = Path.Combine([_tempDir, .. segments]);
        Directory.CreateDirectory(path);
        return path;
    }

    private void CreateFile(string relativePath, string content = "")
    {
        var fullPath = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    // --- Detection rule tests ---

    [TestMethod]
    public void DetectProject_Dotnet_Csproj()
    {
        CreateFile("MyApp.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
          </PropertyGroup>
        </Project>
        """);
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result);
        Assert.AreEqual(DetectedProjectType.Dotnet, result.Type);
    }

    [TestMethod]
    public void DetectProject_Dotnet_WinExe()
    {
        CreateFile("MyApp.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>WinExe</OutputType>
          </PropertyGroup>
        </Project>
        """);
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result);
        Assert.AreEqual(DetectedProjectType.Dotnet, result.Type);
    }

    [TestMethod]
    public void DetectProject_Dotnet_ExcludesLibrary()
    {
        CreateFile("MyLib.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Library</OutputType>
          </PropertyGroup>
        </Project>
        """);
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void DetectProject_Dotnet_ExcludesDefaultOutputType()
    {
        // No OutputType defaults to Library
        CreateFile("MyLib.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """);
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void DetectProject_Dotnet_ExcludesTestProject()
    {
        CreateFile("MyApp.Tests.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <IsTestProject>true</IsTestProject>
          </PropertyGroup>
        </Project>
        """);
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void DetectProject_Dotnet_IncludesExeWithIsTestProjectFalse()
    {
        CreateFile("MyApp.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <IsTestProject>false</IsTestProject>
          </PropertyGroup>
        </Project>
        """);
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result);
        Assert.AreEqual(DetectedProjectType.Dotnet, result.Type);
    }

    [TestMethod]
    public void DetectProject_Dotnet_PicksExeOverLibraryInSameDir()
    {
        // Directory has both a library and an exe; should still detect
        CreateFile("MyLib.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Library</OutputType>
          </PropertyGroup>
        </Project>
        """);
        CreateFile("MyApp.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
          </PropertyGroup>
        </Project>
        """);
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result);
        Assert.AreEqual(DetectedProjectType.Dotnet, result.Type);
    }

    [TestMethod]
    public void DetectProject_Rust_CargoToml()
    {
        CreateFile("Cargo.toml", "[package]");
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result);
        Assert.AreEqual(DetectedProjectType.Rust, result.Type);
    }

    [TestMethod]
    public void DetectProject_CPP_CMakeLists()
    {
        CreateFile("CMakeLists.txt", "cmake_minimum_required(VERSION 3.0)");
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result);
        Assert.AreEqual(DetectedProjectType.CPP, result.Type);
    }

    [TestMethod]
    public void DetectProject_Flutter_Pubspec()
    {
        CreateFile("pubspec.yaml", "name: my_app");
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result);
        Assert.AreEqual(DetectedProjectType.Flutter, result.Type);
    }

    [TestMethod]
    public void DetectProject_Electron_PackageJson()
    {
        CreateFile("package.json", """
        {
            "name": "my-electron-app",
            "devDependencies": {
                "electron": "^28.0.0"
            }
        }
        """);
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result);
        Assert.AreEqual(DetectedProjectType.Electron, result.Type);
    }

    [TestMethod]
    public void DetectProject_Electron_InDependencies()
    {
        CreateFile("package.json", """
        {
            "name": "my-app",
            "dependencies": {
                "electron": "^28.0.0"
            }
        }
        """);
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result);
        Assert.AreEqual(DetectedProjectType.Electron, result.Type);
    }

    [TestMethod]
    public void DetectProject_NotElectron_NoElectronDep()
    {
        CreateFile("package.json", """
        {
            "name": "my-app",
            "dependencies": {
                "react": "^18.0.0"
            }
        }
        """);
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void DetectProject_Tauri_ConfInSubdir()
    {
        CreateDir("src-tauri");
        CreateFile(Path.Combine("src-tauri", "tauri.conf.json"), "{}");
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result);
        Assert.AreEqual(DetectedProjectType.Tauri, result.Type);
    }

    [TestMethod]
    public void DetectProject_NoProject_EmptyDir()
    {
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNull(result);
    }

    // --- Specificity tests ---

    [TestMethod]
    public void DetectProject_TauriOverRust_WhenBothPresent()
    {
        // Tauri projects typically also have Cargo.toml
        CreateFile("Cargo.toml", "[package]");
        CreateDir("src-tauri");
        CreateFile(Path.Combine("src-tauri", "tauri.conf.json"), "{}");
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result);
        Assert.AreEqual(DetectedProjectType.Tauri, result.Type);
    }

    [TestMethod]
    public void DetectProject_ElectronOverNothing_WhenPackageJsonHasElectron()
    {
        CreateFile("package.json", """
        {
            "devDependencies": { "electron": "^28.0.0" }
        }
        """);
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result);
        Assert.AreEqual(DetectedProjectType.Electron, result.Type);
    }

    [TestMethod]
    public void DetectProject_DotnetOverCpp_WhenBothPresent()
    {
        CreateFile("MyApp.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>WinExe</OutputType>
          </PropertyGroup>
        </Project>
        """);
        CreateFile("CMakeLists.txt", "cmake_minimum_required(VERSION 3.0)");
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result);
        Assert.AreEqual(DetectedProjectType.Dotnet, result.Type);
    }

    [TestMethod]
    public void DetectProject_FlutterOverRust_WhenBothPresent()
    {
        CreateFile("pubspec.yaml", "name: my_app");
        CreateFile("Cargo.toml", "[package]");
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result);
        Assert.AreEqual(DetectedProjectType.Flutter, result.Type);
    }

    // --- BFS tests ---

    [TestMethod]
    public async Task DetectProjects_BFS_FindsRootFirst()
    {
        CreateFile("MyApp.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
          </PropertyGroup>
        </Project>
        """);
        CreateDir("sub");
        CreateFile(Path.Combine("sub", "Cargo.toml"), "[package]");

        var results = await _sut.DetectProjectsAsync(Root, 5, null, CancellationToken.None);

        // Root project should be found; sub should be pruned
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(DetectedProjectType.Dotnet, results[0].Type);
        Assert.AreEqual(".", results[0].DisplayPath);
    }

    [TestMethod]
    public async Task DetectProjects_BFS_FindsMultipleAtSameLevel()
    {
        CreateDir("app1");
        CreateFile(Path.Combine("app1", "Cargo.toml"), "[package]");
        CreateDir("app2");
        CreateFile(Path.Combine("app2", "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.0)");

        var results = await _sut.DetectProjectsAsync(Root, 5, null, CancellationToken.None);

        Assert.AreEqual(2, results.Count);
        var types = results.Select(r => r.Type).ToHashSet();
        Assert.IsTrue(types.Contains(DetectedProjectType.Rust));
        Assert.IsTrue(types.Contains(DetectedProjectType.CPP));
    }

    [TestMethod]
    public async Task DetectProjects_BFS_PrunesSubdirectories()
    {
        // Create a project at sub/app, and a nested one at sub/app/nested
        CreateDir("sub", "app");
        CreateFile(Path.Combine("sub", "app", "Cargo.toml"), "[package]");
        CreateDir("sub", "app", "nested");
        CreateFile(Path.Combine("sub", "app", "nested", "CMakeLists.txt"), "cmake");

        var results = await _sut.DetectProjectsAsync(Root, 5, null, CancellationToken.None);

        // Only the parent project should be found, nested should be pruned
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(DetectedProjectType.Rust, results[0].Type);
    }

    [TestMethod]
    public async Task DetectProjects_BFS_RespectsMaxProjects()
    {
        for (int i = 0; i < 10; i++)
        {
            CreateDir($"app{i}");
            CreateFile(Path.Combine($"app{i}", "Cargo.toml"), "[package]");
        }

        var results = await _sut.DetectProjectsAsync(Root, 3, null, CancellationToken.None);

        Assert.AreEqual(3, results.Count);
    }

    [TestMethod]
    public async Task DetectProjects_BFS_SkipsIgnoredDirectories()
    {
        CreateDir("node_modules", "some-pkg");
        CreateFile(Path.Combine("node_modules", "some-pkg", "Cargo.toml"), "[package]");
        CreateDir("src");
        CreateFile(Path.Combine("src", "CMakeLists.txt"), "cmake");

        var results = await _sut.DetectProjectsAsync(Root, 5, null, CancellationToken.None);

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(DetectedProjectType.CPP, results[0].Type);
    }

    [TestMethod]
    public async Task DetectProjects_BFS_ReportsProgress()
    {
        CreateDir("app1");
        CreateFile(Path.Combine("app1", "Cargo.toml"), "[package]");
        CreateDir("app2");
        CreateFile(Path.Combine("app2", "CMakeLists.txt"), "cmake");

        var reported = new List<DetectedProject>();
        var progress = new SynchronousProgress<DetectedProject>(p => reported.Add(p));

        var results = await _sut.DetectProjectsAsync(Root, 5, progress, CancellationToken.None);

        Assert.AreEqual(results.Count, reported.Count);
    }

    [TestMethod]
    public async Task DetectProjects_BFS_EmptyDir_ReturnsEmpty()
    {
        var results = await _sut.DetectProjectsAsync(Root, 5, null, CancellationToken.None);
        Assert.AreEqual(0, results.Count);
    }

    // --- Display path tests ---

    [TestMethod]
    public void DetectProject_DisplayPath_RootIsDot()
    {
        CreateFile("MyApp.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
          </PropertyGroup>
        </Project>
        """);
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result);
        Assert.AreEqual(".", result.DisplayPath);
    }

    [TestMethod]
    public async Task DetectProject_DisplayPath_NestedUsesForwardSlash()
    {
        CreateDir("src", "my-app");
        CreateFile(Path.Combine("src", "my-app", "Cargo.toml"), "[package]");

        var results = await _sut.DetectProjectsAsync(Root, 5, null, CancellationToken.None);

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("src/my-app", results[0].DisplayPath);
    }

    // --- DetectedProject model tests ---

    [TestMethod]
    public void DetectedProject_ToDisplayString()
    {
        var project = new DetectedProject(DetectedProjectType.Tauri, Root, "src/my-app", "src-tauri/tauri.conf.json");
        Assert.AreEqual("Tauri project (./src/my-app/src-tauri/tauri.conf.json)", project.ToDisplayString());
    }

    [TestMethod]
    public void DetectedProject_TypeLabel_AllTypes()
    {
        Assert.AreEqual("Tauri", new DetectedProject(DetectedProjectType.Tauri, Root, ".", "src-tauri/tauri.conf.json").TypeLabel);
        Assert.AreEqual("Electron", new DetectedProject(DetectedProjectType.Electron, Root, ".", "package.json").TypeLabel);
        Assert.AreEqual("Flutter", new DetectedProject(DetectedProjectType.Flutter, Root, ".", "pubspec.yaml").TypeLabel);
        Assert.AreEqual(".NET", new DetectedProject(DetectedProjectType.Dotnet, Root, ".", "MyApp.csproj").TypeLabel);
        Assert.AreEqual("Rust", new DetectedProject(DetectedProjectType.Rust, Root, ".", "Cargo.toml").TypeLabel);
        Assert.AreEqual("C++", new DetectedProject(DetectedProjectType.CPP, Root, ".", "CMakeLists.txt").TypeLabel);
    }

    // --- Edge cases ---

    [TestMethod]
    public void DetectProject_InvalidPackageJson_NotElectron()
    {
        CreateFile("package.json", "not valid json {{{");
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void DetectProject_EmptyPackageJson_NotElectron()
    {
        CreateFile("package.json", "{}");
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task DetectProjects_Cancellation_Throws()
    {
        CreateDir("app1");
        CreateFile(Path.Combine("app1", "Cargo.toml"), "[package]");

        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await _sut.DetectProjectsAsync(Root, 5, null, cts.Token));
    }

    // --- Namespace-aware csproj parsing ---

    [TestMethod]
    public void DetectProject_Dotnet_LegacyNamespacedCsproj_Exe()
    {
        // Legacy .NET Framework projects use the MSBuild XML namespace
        CreateFile("LegacyApp.csproj", """
        <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
          </PropertyGroup>
        </Project>
        """);
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result, "Should detect legacy namespaced .csproj with OutputType=Exe");
        Assert.AreEqual(DetectedProjectType.Dotnet, result.Type);
    }

    [TestMethod]
    public void DetectProject_Dotnet_LegacyNamespacedCsproj_WinExe()
    {
        CreateFile("WpfApp.csproj", """
        <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup>
            <OutputType>WinExe</OutputType>
          </PropertyGroup>
        </Project>
        """);
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNotNull(result, "Should detect legacy namespaced .csproj with OutputType=WinExe");
        Assert.AreEqual(DetectedProjectType.Dotnet, result.Type);
    }

    [TestMethod]
    public void DetectProject_Dotnet_LegacyNamespacedCsproj_Library_Excluded()
    {
        CreateFile("MyLib.csproj", """
        <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup>
            <OutputType>Library</OutputType>
          </PropertyGroup>
        </Project>
        """);
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNull(result, "Should not detect legacy namespaced library .csproj");
    }

    // --- Dot-prefix directory skipping ---

    [TestMethod]
    public async Task DetectProjects_BFS_SkipsDotPrefixedDirectories()
    {
        // Create a project inside a hidden (dot-prefixed) directory
        CreateDir(".hidden");
        CreateFile(Path.Combine(".hidden", "Cargo.toml"), "[package]");

        // Create a visible project
        CreateDir("visible");
        CreateFile(Path.Combine("visible", "Cargo.toml"), "[package]");

        var results = await _sut.DetectProjectsAsync(Root, 5, null, CancellationToken.None);

        Assert.AreEqual(1, results.Count, "Should only find the visible project");
        Assert.AreEqual("visible", results[0].DisplayPath);
    }

    // --- DetectProjectAt (single-directory public API) ---

    [TestMethod]
    public void DetectProjectAt_WithProject_ReturnsProject()
    {
        CreateFile("Cargo.toml", "[package]");
        var result = _sut.DetectProjectAt(Root);
        Assert.IsNotNull(result);
        Assert.AreEqual(DetectedProjectType.Rust, result.Type);
        Assert.AreEqual(".", result.DisplayPath);
    }

    [TestMethod]
    public void DetectProjectAt_EmptyDir_ReturnsNull()
    {
        var result = _sut.DetectProjectAt(Root);
        Assert.IsNull(result);
    }

    // --- Error / defensive paths ---

    [TestMethod]
    public void DetectProject_MalformedCsproj_IsSkipped()
    {
        // Invalid XML causes XDocument.Load to throw; the csproj should be skipped, not crash.
        CreateFile("Broken.csproj", "<Project><PropertyGroup><OutputType>Exe</OutputType></Broken>");
        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNull(result, "A csproj that fails to parse should be treated as not-a-project");
    }

    [TestMethod]
    public async Task DetectProjects_NonExistentRoot_ReturnsEmpty()
    {
        // A missing root exercises the DirectoryNotFoundException (IOException) handlers in
        // FindTauriConfFile, FindExecutableCsproj and EnqueueSubdirectories without crashing.
        var missing = new DirectoryInfo(Path.Combine(_tempDir, "does-not-exist"));
        var results = await _sut.DetectProjectsAsync(missing, 5, null, CancellationToken.None);
        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public void DetectProject_DirectoryOutsideSearchRoot_UsesFullPath()
    {
        // When the directory is not under the search root, the display path falls back
        // to the absolute path.
        CreateFile("Cargo.toml", "[package]");
        var unrelatedRoot = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"Unrelated_{Guid.NewGuid():N}"));

        var result = ProjectDetectionService.DetectProject(Root, unrelatedRoot);

        Assert.IsNotNull(result);
        Assert.AreEqual(_tempDir.TrimEnd(Path.DirectorySeparatorChar), result.DisplayPath);
    }

    [TestMethod]
    public void DetectProject_ElectronPackageJsonUnreadable_IsNotDetected()
    {
        // package.json exists but is locked for exclusive access, so File.ReadAllText throws
        // IOException inside IsElectronProject, which must be swallowed (returns not-electron).
        CreateFile("package.json", """{ "dependencies": { "electron": "^28.0.0" } }""");
        var packageJsonPath = Path.Combine(_tempDir, "package.json");

        using (new FileStream(packageJsonPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = ProjectDetectionService.DetectProject(Root, Root);
            Assert.IsNull(result, "An unreadable package.json should not be detected as Electron");
        }
    }

    // --- Reparse point (junction) skipping ---

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return false;
            }

            proc.WaitForExit();
            return proc.ExitCode == 0 &&
                   Directory.Exists(linkPath) &&
                   new DirectoryInfo(linkPath).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }

    [TestMethod]
    public void DetectProject_Tauri_SkipsReparsePointSubdir()
    {
        // A tauri.conf.json reachable only through a junction must be ignored, so no Tauri
        // project is detected (guards against following links outside the search root).
        var targetDir = CreateDir("obj", "tauri-target");
        File.WriteAllText(Path.Combine(targetDir, "tauri.conf.json"), "{}");

        var junctionPath = Path.Combine(_tempDir, "src-tauri");
        if (!TryCreateJunction(junctionPath, targetDir))
        {
            Assert.Inconclusive("Could not create a directory junction on this machine.");
        }

        var result = ProjectDetectionService.DetectProject(Root, Root);
        Assert.IsNull(result, "Tauri config behind a reparse point should be skipped");
    }

    [TestMethod]
    public async Task DetectProjects_BFS_SkipsReparsePointDirectories()
    {
        // Project inside a junction target (kept under an ignored 'obj' dir so BFS can only
        // reach it via the junction) must not be discovered, while a normal sibling is.
        var targetDir = CreateDir("obj", "linked-target");
        File.WriteAllText(Path.Combine(targetDir, "Cargo.toml"), "[package]");

        var junctionPath = Path.Combine(_tempDir, "linked");
        if (!TryCreateJunction(junctionPath, targetDir))
        {
            Assert.Inconclusive("Could not create a directory junction on this machine.");
        }

        CreateDir("visible");
        CreateFile(Path.Combine("visible", "CMakeLists.txt"), "cmake");

        var results = await _sut.DetectProjectsAsync(Root, 5, null, CancellationToken.None);

        Assert.AreEqual(1, results.Count, "Only the non-junction project should be found");
        Assert.AreEqual(DetectedProjectType.CPP, results[0].Type);
    }

    [TestMethod]
    public async Task ClassifyRunnableAsync_Cancellation_RethrowsInsteadOfFallback()
    {
        // A user Ctrl+C surfaces as OperationCanceledException from the token-aware dotnet call. It must
        // propagate — not be swallowed and downgraded to the static fallback (Copilot review C3). Without
        // the re-throw this would return a runnability from the static parse and the test would fail.
        CreateFile("App.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><OutputType>Exe</OutputType></PropertyGroup>
        </Project>
        """);
        var csproj = new FileInfo(Path.Combine(_tempDir, "App.csproj"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = _ => throw new OperationCanceledException(cts.Token),
        };
        var sut = new ProjectDetectionService(NullLogger<ProjectDetectionService>.Instance, dotnet);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => sut.ClassifyRunnableAsync(csproj, Root, null, cts.Token));
    }

    [TestMethod]
    public void ClassifyRunnableStatic_TestPackageUnderInactiveCondition_IsAppNotTest()
    {
        // Static fallback must ignore a test PackageReference gated behind a Condition — it can't evaluate
        // the Condition, so assuming the marker is active would misclassify a runnable app as a test and
        // drop it from directory detection (Copilot review). The evaluated path handles Conditions.
        CreateFile("App.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup>
          <ItemGroup Condition="'$(Configuration)'=='Test'">
            <PackageReference Include="xunit" Version="2.9.0" />
          </ItemGroup>
        </Project>
        """);
        var csproj = new FileInfo(Path.Combine(_tempDir, "App.csproj"));

        Assert.AreEqual(ProjectRunnability.App, ProjectDetectionService.ClassifyRunnableStatic(csproj));
    }

    [TestMethod]
    public void ClassifyRunnableStatic_UnconditionalTestPackage_IsTest()
    {
        // The unconditional-marker filter must not regress genuine detection: an ungated test package
        // reference on a WinExe app (the WinUI MSTest shape) still classifies as a test.
        CreateFile("App.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup>
          <ItemGroup>
            <PackageReference Include="MSTest.TestFramework" Version="3.6.0" />
          </ItemGroup>
        </Project>
        """);
        var csproj = new FileInfo(Path.Combine(_tempDir, "App.csproj"));

        Assert.AreEqual(ProjectRunnability.Test, ProjectDetectionService.ClassifyRunnableStatic(csproj));
    }

    [TestMethod]
    public void ClassifyRunnableStatic_UnconditionalTestContainerCapability_IsTest()
    {
        // The unconditional filter is applied to the ProjectCapability scan too, not just PackageReference:
        // an ungated <ProjectCapability Include="TestContainer"> (the SDK-style test shape) classifies as a
        // test even when the app declares WinExe.
        CreateFile("App.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup>
          <ItemGroup>
            <ProjectCapability Include="TestContainer" />
          </ItemGroup>
        </Project>
        """);
        var csproj = new FileInfo(Path.Combine(_tempDir, "App.csproj"));

        Assert.AreEqual(ProjectRunnability.Test, ProjectDetectionService.ClassifyRunnableStatic(csproj));
    }

    [TestMethod]
    public void ClassifyRunnableStatic_TestContainerCapabilityUnderInactiveCondition_IsAppNotTest()
    {
        // The static fallback can't evaluate Conditions, so a TestContainer capability gated behind an
        // inactive Condition — here via a <Choose>/<When> ancestor, exercising the AncestorsAndSelf walk —
        // must be ignored rather than misclassifying a runnable app as a test.
        CreateFile("App.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup>
          <Choose>
            <When Condition="'$(Configuration)'=='Test'">
              <ItemGroup>
                <ProjectCapability Include="TestContainer" />
              </ItemGroup>
            </When>
          </Choose>
        </Project>
        """);
        var csproj = new FileInfo(Path.Combine(_tempDir, "App.csproj"));

        Assert.AreEqual(ProjectRunnability.App, ProjectDetectionService.ClassifyRunnableStatic(csproj));
    }

    // --- TryGetDeclaredOutputType (F1 pre-build gate) ---

    [TestMethod]
    public void TryGetDeclaredOutputType_Exe_ReturnsExe()
    {
        CreateFile("App.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
          </PropertyGroup>
        </Project>
        """);
        var csproj = new FileInfo(Path.Combine(_tempDir, "App.csproj"));

        Assert.AreEqual("Exe", ProjectDetectionService.TryGetDeclaredOutputType(csproj));
    }

    [TestMethod]
    public void TryGetDeclaredOutputType_Library_ReturnsLibrary()
    {
        CreateFile("Lib.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Library</OutputType>
          </PropertyGroup>
        </Project>
        """);
        var csproj = new FileInfo(Path.Combine(_tempDir, "Lib.csproj"));

        Assert.AreEqual("Library", ProjectDetectionService.TryGetDeclaredOutputType(csproj));
    }

    [TestMethod]
    public void TryGetDeclaredOutputType_Absent_ReturnsNull()
    {
        // No OutputType declared: must return null (an SDK default only an evaluation can resolve),
        // NOT collapse to "not runnable" the way ClassifyRunnableStatic does.
        CreateFile("Lib.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """);
        var csproj = new FileInfo(Path.Combine(_tempDir, "Lib.csproj"));

        Assert.IsNull(ProjectDetectionService.TryGetDeclaredOutputType(csproj));
    }

    [TestMethod]
    public void TryGetDeclaredOutputType_LastWinsAcrossPropertyGroups()
    {
        CreateFile("App.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Library</OutputType>
          </PropertyGroup>
          <PropertyGroup>
            <OutputType>WinExe</OutputType>
          </PropertyGroup>
        </Project>
        """);
        var csproj = new FileInfo(Path.Combine(_tempDir, "App.csproj"));

        Assert.AreEqual("WinExe", ProjectDetectionService.TryGetDeclaredOutputType(csproj));
    }

    [TestMethod]
    public void TryGetDeclaredOutputType_MalformedXml_ReturnsNull()
    {
        CreateFile("Broken.csproj", "<Project><PropertyGroup><OutputType>Exe");
        var csproj = new FileInfo(Path.Combine(_tempDir, "Broken.csproj"));

        Assert.IsNull(ProjectDetectionService.TryGetDeclaredOutputType(csproj));
    }
}

/// <summary>
/// A synchronous IProgress implementation that invokes the callback immediately
/// on the calling thread, avoiding the async post behavior of Progress&lt;T&gt;.
/// </summary>
file sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
