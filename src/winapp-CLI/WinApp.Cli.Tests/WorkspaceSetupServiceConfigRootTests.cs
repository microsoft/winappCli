// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Regression tests for how <see cref="WorkspaceSetupService"/> roots the NuGet settings hierarchy.
/// Split out from <see cref="WorkspaceSetupServiceTests"/> to keep each file within the repository's
/// file-size guidance.
/// </summary>
[TestClass]
public class WorkspaceSetupServiceConfigRootTests : BaseCommandTests
{
    [TestMethod]
    public async Task SetupWorkspace_RootsNuGetSettingsAtConfigDir_NotWorkingDirectory()
    {
        // Regression test for `init <dir>` / `restore --config-dir <dir>`: the NuGet settings hierarchy must
        // be resolved from the selected config directory, not the process working directory. Without
        // SetupWorkspaceAsync calling NugetSourceProvider.SetConfigRoot(options.ConfigDir), a project's
        // private feed / credentials / globalPackagesFolder would be silently ignored whenever the working
        // directory differs from the target project directory — so this test pins that fix.

        // The working directory (what CurrentDirectoryProvider is rooted at, per BaseCommandTests) declares
        // one feed; a separate config directory declares a DIFFERENT feed. Both <clear /> inherited sources,
        // so each directory resolves exactly one distinct source (and the config dir's <clear /> also
        // removes the parent working-directory feed from its own hierarchy).
        await File.WriteAllTextAsync(Path.Join(_tempDirectory.FullName, "nuget.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="working-only-feed" value="working-feed" />
              </packageSources>
            </configuration>
            """);

        var configDir = _tempDirectory.CreateSubdirectory("project-root");
        await File.WriteAllTextAsync(Path.Join(configDir.FullName, "nuget.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="configdir-only-feed" value="configdir-feed" />
              </packageSources>
            </configuration>
            """);

        // The shared singleton that SetupWorkspaceAsync re-roots is the SAME instance that answers the
        // assertion below.
        var sourceProvider = GetRequiredService<NugetSourceProvider>();
        var workspaceSetupService = GetRequiredService<IWorkspaceSetupService>();

        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = configDir,
            ConfigDir = configDir,
            SdkInstallMode = SdkInstallMode.None,
            UseDefaults = true,
            RequireExistingConfig = false,
            ForceLatestBuildTools = true,
            NoGitignore = true
        };

        // Act
        var exitCode = await workspaceSetupService.SetupWorkspaceAsync(options, TestContext.CancellationToken);
        Assert.AreEqual(0, exitCode, "Setup should complete successfully");

        // Assert: after setup the shared source provider resolves the config directory's feed — proving
        // SetupWorkspaceAsync rooted NuGet settings at ConfigDir rather than the working directory.
        var sources = sourceProvider.GetRepositoriesForPackage("Any.Package")
            .Select(r => r.PackageSource.Name)
            .ToList();

        CollectionAssert.Contains(sources, "configdir-only-feed", "NuGet settings must be resolved from the selected ConfigDir.");
        CollectionAssert.DoesNotContain(sources, "working-only-feed", "NuGet settings must NOT be resolved from the process working directory when an explicit ConfigDir is provided.");
    }

    /// <summary>
    /// For a .NET project the versions chosen here are written into the project by <c>dotnet add package</c>,
    /// which always resolves <c>nuget.config</c> by walking up from the project. An explicit
    /// <c>--config-dir</c> outside that hierarchy therefore used to split the two: winapp could select a
    /// version from a feed only it could see, and <c>dotnet add</c> would then fail with NU1102 looking for it
    /// on the project's own sources. Setup now re-roots at the project so both sides agree.
    /// </summary>
    [TestMethod]
    public async Task SetupWorkspace_DotNetProjectWithConfigDirOutsideProject_RootsNuGetSettingsAtProject()
    {
        var configDir = _tempDirectory.CreateSubdirectory("external-config");
        await File.WriteAllTextAsync(Path.Join(configDir.FullName, "nuget.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="configdir-only-feed" value="configdir-feed" />
              </packageSources>
            </configuration>
            """, TestContext.CancellationToken);

        // A sibling of the config directory, so the config directory is NOT an ancestor of the project and
        // dotnet would never discover it.
        var projectDir = _tempDirectory.CreateSubdirectory("dotnet-project");
        await File.WriteAllTextAsync(Path.Join(projectDir.FullName, "nuget.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="project-only-feed" value="project-feed" />
              </packageSources>
            </configuration>
            """, TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Join(projectDir.FullName, "App.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """, TestContext.CancellationToken);

        var sourceProvider = GetRequiredService<NugetSourceProvider>();
        var workspaceSetupService = GetRequiredService<IWorkspaceSetupService>();

        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = projectDir,
            ConfigDir = configDir,
            SdkInstallMode = SdkInstallMode.None,
            UseDefaults = true,
            RequireExistingConfig = false,
            ForceLatestBuildTools = true,
            NoGitignore = true
        };

        await workspaceSetupService.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        // The exit code is deliberately not asserted: re-rooting happens as soon as the project is selected,
        // and what matters here is which nuget.config hierarchy the rest of setup then resolves against.
        var sources = sourceProvider.GetRepositoriesForPackage("Any.Package")
            .Select(r => r.PackageSource.Name)
            .ToList();

        CollectionAssert.Contains(sources, "project-only-feed", "A .NET project's sources must come from the hierarchy dotnet itself will use.");
        CollectionAssert.DoesNotContain(sources, "configdir-only-feed", "A config directory outside the project is invisible to 'dotnet add package', so selecting versions from it produces references that cannot be restored.");
    }

    /// <summary>
    /// An ancestor <c>--config-dir</c> is not equivalent to rooting at the project. <c>winapp init src/App
    /// --config-dir .</c> with a <c>nuget.config</c> inside <c>src/App</c> would start winapp's lookup at the
    /// repo root and skip the project's own config, while <c>dotnet add package</c> starts at <c>src/App</c>
    /// and uses it — so the two could still pick different feeds. Rooting at the project loses nothing,
    /// because NuGet walks up and still picks the ancestor's settings up on the way.
    /// </summary>
    [TestMethod]
    public async Task SetupWorkspace_DotNetProjectWithAncestorConfigDir_StillRootsNuGetSettingsAtProject()
    {
        // The ancestor the user points --config-dir at.
        await File.WriteAllTextAsync(Path.Join(_tempDirectory.FullName, "nuget.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="ancestor-feed" value="ancestor-feed-path" />
              </packageSources>
            </configuration>
            """, TestContext.CancellationToken);

        // The project declares its own feed, which only a project-rooted lookup can see. It does NOT <clear />,
        // so the ancestor's source is still inherited — proving rooting lower adds levels rather than losing them.
        var projectDir = _tempDirectory.CreateSubdirectory("src").CreateSubdirectory("App");
        await File.WriteAllTextAsync(Path.Join(projectDir.FullName, "nuget.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <add key="project-feed" value="project-feed-path" />
              </packageSources>
            </configuration>
            """, TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Join(projectDir.FullName, "App.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """, TestContext.CancellationToken);

        var sourceProvider = GetRequiredService<NugetSourceProvider>();
        var workspaceSetupService = GetRequiredService<IWorkspaceSetupService>();

        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = projectDir,
            ConfigDir = _tempDirectory,
            SdkInstallMode = SdkInstallMode.None,
            UseDefaults = true,
            RequireExistingConfig = false,
            ForceLatestBuildTools = true,
            NoGitignore = true
        };

        await workspaceSetupService.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        var sources = sourceProvider.GetRepositoriesForPackage("Any.Package")
            .Select(r => r.PackageSource.Name)
            .ToList();

        CollectionAssert.Contains(sources, "project-feed", "A config directory above the project must not hide the project's own nuget.config, which dotnet would use.");
        CollectionAssert.Contains(sources, "ancestor-feed", "Rooting at the project still inherits everything above it, so an ancestor config directory loses nothing.");
    }
}
