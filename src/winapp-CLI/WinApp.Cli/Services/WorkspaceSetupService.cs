// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Parameters for workspace setup operations
/// </summary>
internal class WorkspaceSetupOptions
{
    public required DirectoryInfo BaseDirectory { get; set; }
    public required DirectoryInfo ConfigDir { get; set; }
    public SdkInstallMode? SdkInstallMode { get; set; }
    public bool IgnoreConfig { get; set; }
    public bool NoGitignore { get; set; }
    public bool UseDefaults { get; set; }
    public bool RequireExistingConfig { get; set; }
    public bool ForceLatestBuildTools { get; set; }
    public bool ConfigOnly { get; set; }
}

/// <summary>
/// Shared service for setting up winapp workspaces
/// </summary>
internal class WorkspaceSetupService(
    IConfigService configService,
    IWinappDirectoryService winappDirectoryService,
    IPackageInstallationService packageInstallationService,
    IBuildToolsService buildToolsService,
    ICppWinrtService cppWinrtService,
    IPackageLayoutService packageLayoutService,
    IWinmdsLockfileService winmdsLockfileService,
    IWindowsAppRuntimeService windowsAppRuntimeService,
    INugetService nugetService,
    IManifestService manifestService,
    IDevModeService devModeService,
    IGitignoreService gitignoreService,
    IDirectoryPackagesService directoryPackagesService,
    IDotNetService dotNetService,
    IDotNetProjectRestoreService dotNetProjectRestoreService,
    IStatusService statusService,
    ICurrentDirectoryProvider currentDirectoryProvider,
    NugetSourceProvider nugetSourceProvider,
    IAnsiConsole ansiConsole,
    ILogger<WorkspaceSetupService> logger) : IWorkspaceSetupService
{
    public async Task<int> SetupWorkspaceAsync(WorkspaceSetupOptions options, CancellationToken cancellationToken = default)
    {
        configService.ConfigPath = new FileInfo(Path.Join(options.ConfigDir.FullName, "winapp.yaml"));

        // Resolve the user's nuget.config hierarchy from the selected project/config directory, which can
        // differ from the process working directory when `init <dir>` / `restore <dir>` / `--config-dir <dir>`
        // is used. Without this, a project-level private feed, credentials or globalPackagesFolder would be
        // ignored unless the user first changed into that directory. For .NET projects this is re-rooted at
        // the project below, once one has been selected.
        nugetSourceProvider.SetConfigRoot(options.ConfigDir);

        // Detect .NET project (.csproj) in the base directory
        FileInfo? csprojFile = null;
        bool isDotNetProject = false;

        if (!options.RequireExistingConfig)
        {
            var csprojFiles = dotNetService.FindCsproj(options.BaseDirectory);
            if (csprojFiles.Count > 0)
            {
                isDotNetProject = true;
                logger.LogDebug("Detected {Count} .NET project(s) in {BaseDirectory}", csprojFiles.Count, options.BaseDirectory);
                csprojFile = await SelectCsprojFileAsync(csprojFiles, cancellationToken);
                logger.LogDebug(".NET project setup for {CsprojFile}", csprojFile.FullName);
                AlignNugetConfigRootWithProject(options.ConfigDir, csprojFile);
            }
        }
        else if (dotNetService.FindCsproj(options.BaseDirectory).Count > 0 && !configService.Exists())
        {
            // Restore on a .NET project that `winapp init` configured. For .NET projects init records the SDK
            // package versions as PackageReferences in the .csproj rather than in a winapp.yaml, so there is
            // no winapp.yaml to read and `dotnet restore` is what actually restores them. Run it instead of
            // failing with an instruction to run it by hand: `winapp init` leaves the project in exactly this
            // shape, so `winapp restore` immediately afterwards must not be an error.
            return await dotNetProjectRestoreService.RestoreAsync(options.BaseDirectory, options.ConfigDir, cancellationToken);
        }

        // Restore on a non-.NET project with no winapp.yaml — nothing to restore.
        // No-op success rather than error: a project that doesn't declare SDK
        // package versions in winapp.yaml simply has nothing for restore to do.
        if (options.RequireExistingConfig && !configService.Exists())
        {
            logger.LogInformation("{UISymbol} No winapp.yaml found in {ConfigDir}. Nothing to restore.", UiSymbols.Note, options.ConfigDir);
            logger.LogInformation("If this project needs Windows SDK packages, run 'winapp init' to set them up.");
            return 0;
        }

        // Configuration / prompting phase
        bool hadExistingConfig;
        WinappConfig? config;
        bool shouldGenerateManifest;
        ManifestGenerationInfo? manifestGenerationInfo;
        bool shouldEnableDeveloperMode;
        string? recommendedTfm;

        (var initializationResult, config, hadExistingConfig, shouldGenerateManifest, manifestGenerationInfo, shouldEnableDeveloperMode, recommendedTfm) = await InitializeConfigurationAsync(options, isDotNetProject, csprojFile, cancellationToken);
        if (initializationResult != 0)
        {
            return initializationResult;
        }

        // Handle config-only mode: just create/validate config file and exit (only for non-.NET path)
        if (!isDotNetProject && options.ConfigOnly)
        {
            if (hadExistingConfig && config != null)
            {
                logger.LogInformation("{UISymbol} Existing configuration file found and validated → {ConfigPath}", UiSymbols.Check, configService.ConfigPath);
                logger.LogInformation("{UISymbol} Configuration contains {PackageCount} packages", UiSymbols.Package, config.Packages.Count);

                if (config.Packages.Count > 0)
                {
                    logger.LogInformation("Configured packages:");
                    foreach (var pkg in config.Packages)
                    {
                        logger.LogInformation("{UISymbol} {PackageName} = {PackageVersion}", UiSymbols.Bullet, pkg.Name, pkg.Version);
                    }
                }
            }
            else if (options.SdkInstallMode != SdkInstallMode.None)
            {
                logger.LogInformation("Creating configuration file");

                // Get latest package versions (respecting prerelease option)
                var defaultVersions = new Dictionary<string, string>();
                foreach (var packageName in NugetService.SDK_PACKAGES)
                {
                    try
                    {
                        var version = await nugetService.GetLatestVersionAsync(
                            packageName,
                            options.SdkInstallMode ?? SdkInstallMode.Stable,
                            cancellationToken: cancellationToken);
                        defaultVersions[packageName] = version;
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug("{UISymbol} Could not get version for {PackageName}: {ErrorMessage}", UiSymbols.Note, packageName, NugetErrorMessage.Redact(ex.Message));
                    }
                }

                var finalConfig = new WinappConfig();
                foreach (var kvp in defaultVersions)
                {
                    finalConfig.SetVersion(kvp.Key, kvp.Value);
                }

                configService.Save(finalConfig);

                logger.LogDebug("{UISymbol} Configuration file created → {ConfigPath}", UiSymbols.Save, configService.ConfigPath);
                logger.LogDebug("{UISymbol} Added {PackageCount} default SDK packages", UiSymbols.Package, finalConfig.Packages.Count);

                logger.LogDebug("Generated packages");
                foreach (var pkg in finalConfig.Packages)
                {
                    logger.LogDebug("{UISymbol} {PackageName} = {PackageVersion}", UiSymbols.Bullet, pkg.Name, pkg.Version);
                }

                if (options.SdkInstallMode == SdkInstallMode.Experimental)
                {
                    logger.LogDebug("{UISymbol} Prerelease packages were included", UiSymbols.Wrench);
                }
                else if (options.SdkInstallMode == SdkInstallMode.Preview)
                {
                    logger.LogDebug("{UISymbol} Preview packages were included", UiSymbols.Wrench);
                }
            }
            // else: SdkInstallMode == None and no existing config - nothing to do

            logger.LogInformation("Configuration-only operation completed");
            return 0;
        }

        // Initialize workspace directories (native/C++ projects only)
        DirectoryInfo? globalWinappDir = null;
        DirectoryInfo? localWinappDir = null;

        if (!isDotNetProject)
        {
            if (options.SdkInstallMode == SdkInstallMode.None)
            {
                // The "why we're skipping" message is emitted by AskSdkInstallModeAsync (interactive
                // choice, --setup-sdks none) or — for .NET — by the early-exit when the project
                // already references WinAppSDK. Don't repeat a generic / potentially-misleading
                // "by user choice" line here (#464).
                logger.LogInformation("Configuration processed (SDK installation skipped)");
            }
            else
            {
                // Step 3: Initialize workspace
                globalWinappDir = winappDirectoryService.GetGlobalWinappDirectory();
                localWinappDir = winappDirectoryService.GetLocalWinappDirectory(options.BaseDirectory);

                // Setup-specific startup messages
                if (!options.RequireExistingConfig)
                {
                    logger.LogDebug("{UISymbol} using config → {ConfigPath}", UiSymbols.Rocket, configService.ConfigPath);
                    logger.LogDebug("{UISymbol} winapp init starting in {BaseDirectory}", UiSymbols.Rocket, options.BaseDirectory);
                    logger.LogDebug("{UISymbol} Global packages → {GlobalWinappDir}", UiSymbols.Folder, globalWinappDir);
                    logger.LogDebug("{UISymbol} Global workspace → {GlobalWinappDir}", UiSymbols.Folder, globalWinappDir);
                    logger.LogDebug("{UISymbol} Local workspace → {LocalWinappDir}", UiSymbols.Folder, localWinappDir);

                    if (options.SdkInstallMode == SdkInstallMode.Experimental)
                    {
                        logger.LogDebug("{UISymbol} Experimental/prerelease packages will be included", UiSymbols.Wrench);
                    }
                }
                else
                {
                    logger.LogDebug("{UISymbol} Global packages → {GlobalWinappDir}", UiSymbols.Folder, globalWinappDir);
                    logger.LogDebug("{UISymbol} Local workspace → {LocalWinappDir}", UiSymbols.Folder, localWinappDir);
                }

                // First ensure basic workspace (for global packages)
                logger.LogDebug("{UISymbol} Initializing workspace at {LocalWinappDir}", UiSymbols.Sync, localWinappDir);
                packageInstallationService.InitializeWorkspace(globalWinappDir);
            }
        }
        else if (options.SdkInstallMode == SdkInstallMode.None)
        {
            // For .NET projects: AskSdkInstallModeAsync already logged the actual reason we're
            // skipping (auto-skipped because WinAppSDK is already referenced, or the user picked
            // "Do not setup", or --setup-sdks=none was passed). Don't append a misleading
            // "by user choice" line on top of that (#464).
        }

        // Prompt to install the WinApp CLI package before entering the live display context
        // (Spectre.Console does not allow interactive prompts inside a live display)
        var installWinAppPackage = false;
        if (isDotNetProject && csprojFile != null)
        {
            var hasWinAppPackage = await dotNetService.HasPackageReferenceAsync(
                csprojFile,
                DotNetService.WINDOWS_SDK_BUILD_TOOLS_WINAPP_PACKAGE,
                cancellationToken);

            if (hasWinAppPackage)
            {
                logger.LogDebug("{UISymbol} {Package} already referenced by project; skipping install prompt",
                    UiSymbols.Skip, DotNetService.WINDOWS_SDK_BUILD_TOOLS_WINAPP_PACKAGE);
                installWinAppPackage = true;
            }
            else if (options.UseDefaults)
            {
                installWinAppPackage = true;
            }
            else
            {
                installWinAppPackage = await ShowConfirmationPromptAsync(
                    ansiConsole,
                    $"Add package {DotNetService.WINDOWS_SDK_BUILD_TOOLS_WINAPP_PACKAGE}? (Enables running the app packaged via 'dotnet run')",
                    cancellationToken);
                if (!installWinAppPackage)
                {
                    logger.LogWarning("{UISymbol} Skipped {Package} — packaged app support via 'dotnet run' will not be available",
                        UiSymbols.Warning, DotNetService.WINDOWS_SDK_BUILD_TOOLS_WINAPP_PACKAGE);
                }
            }
        }

        var statusLabel = isDotNetProject ? "Setting up .NET project" : "Setting up workspace";
        return await statusService.ExecuteWithStatusAsync(statusLabel, async (taskContext, cancellationToken) =>
        {
            try
            {
                // Config-only mode completes here - skip all other setup steps
                if (options.ConfigOnly)
                {
                    return (0, "Configuration-only operation completed");
                }

                // Enable Developer Mode (for setup only)
                if (!options.RequireExistingConfig)
                {
                    await taskContext.AddSubTaskAsync("Configuring developer mode", async (taskContext, cancellationToken) =>
                    {
                        if (!shouldEnableDeveloperMode)
                        {
                            taskContext.AddDebugMessage($"{UiSymbols.Skip} Developer Mode setup skipped");
                            return (0, "Developer Mode setup skipped");
                        }
                        try
                        {
                            if (devModeService.IsEnabled())
                            {
                                taskContext.AddDebugMessage("Developer Mode already enabled.");
                                return (0, "Developer mode: already enabled");
                            }
                            taskContext.UpdateSubStatus("Checking Developer Mode");
                            var devModeResult = await devModeService.EnsureWin11DevModeAsync(taskContext, cancellationToken);

                            if (devModeResult == -1)
                            {
                                return (-1, "Developer mode: [red]not enabled[/]");
                            }

                            if (devModeResult != 0 && devModeResult != 3010)
                            {
                                taskContext.AddDebugMessage($"{UiSymbols.Note} Developer Mode setup returned exit code {devModeResult}");
                            }

                            return (devModeResult, "Developer mode: enabled");
                        }
                        catch (Exception ex)
                        {
                            taskContext.AddDebugMessage($"{UiSymbols.Note} Developer Mode setup failed: {ex.Message}");
                            return (1, "Developer Mode setup failed");
                        }
                    }, cancellationToken);
                }

                Dictionary<string, string>? usedVersions = null;
                DirectoryInfo? nugetCacheDir = null;
                (int, string) partialResult;
                var sdkInstallMode = options.SdkInstallMode ?? SdkInstallMode.Stable;

                // .NET-specific: Update TargetFramework (independent of SDK install mode)
                if (isDotNetProject && csprojFile != null && recommendedTfm != null)
                {
                    dotNetService.SetTargetFramework(csprojFile, recommendedTfm);
                    taskContext.AddStatusMessage($"{UiSymbols.Check} Updated TargetFramework to {recommendedTfm}");
                }

                // .NET-specific: Add NuGet package references and configure project
                if (isDotNetProject && csprojFile != null)
                {
                    if (await dotNetService.UpdatePublishProfileAsync(csprojFile, cancellationToken))
                    {
                        taskContext.AddDebugMessage($"{UiSymbols.Check} Updated PublishProfile with existence condition");
                    }

                    if (await dotNetService.EnsureRuntimeIdentifierAsync(csprojFile, cancellationToken))
                    {
                        taskContext.AddDebugMessage($"{UiSymbols.Check} Added default RuntimeIdentifier");
                    }

                    // Build dynamic package list:
                    // WinApp integration package is added only when the user opted in
                    var packages = new List<(string Name, bool Required)>();

                    if (installWinAppPackage)
                    {
                        // Non-required: a transient NuGet failure should not abort init
                        packages.Add((DotNetService.WINDOWS_SDK_BUILD_TOOLS_WINAPP_PACKAGE, false));
                    }

                    if (options.SdkInstallMode != SdkInstallMode.None)
                    {
                        packages.Add((DotNetService.WINAPP_SDK_NUGET_PACKAGE, true));
                    }

                    partialResult = await taskContext.AddSubTaskAsync("Adding NuGet packages to project", async (taskContext, cancellationToken) =>
                    {
                        usedVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        var failedPackages = new List<string>();

                        // When SdkInstallMode is None, still use Stable versions for build tools packages
                        var versionQueryMode = sdkInstallMode == SdkInstallMode.None ? SdkInstallMode.Stable : sdkInstallMode;

                        // Query existing package versions so we can preserve them
                        // (except for the WinApp CLI package which should always be updated)
                        var existingVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        try
                        {
                            var packageList = await dotNetService.GetPackageListAsync(csprojFile, includeTransitive: false, cancellationToken: cancellationToken);
                            var project = packageList?.Projects?.FirstOrDefault();
                            if (project is not null)
                            {
                                foreach (var pkg in (project.Frameworks ?? [])
                                    .SelectMany(f => f.TopLevelPackages ?? []))
                                {
                                    existingVersions[pkg.Id] = pkg.ResolvedVersion;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            taskContext.AddDebugMessage($"{UiSymbols.Note} Could not query existing packages: {NugetErrorMessage.Redact(ex.Message)}");
                        }

                        foreach (var (packageName, required) in packages)
                        {
                            // Preserve existing package versions unless it's the WinApp CLI package
                            if (existingVersions.TryGetValue(packageName, out var existingVersion)
                                && !string.Equals(packageName, DotNetService.WINDOWS_SDK_BUILD_TOOLS_WINAPP_PACKAGE, StringComparison.OrdinalIgnoreCase))
                            {
                                usedVersions[packageName] = existingVersion;
                                taskContext.AddStatusMessage($"{UiSymbols.Check} Keeping {packageName} {existingVersion}");
                                continue;
                            }

                            taskContext.UpdateSubStatus($"Querying latest {packageName} version");
                            string? version = null;
                            try
                            {
                                version = await nugetService.GetLatestVersionAsync(packageName, versionQueryMode, cancellationToken: cancellationToken);
                                if (version != null)
                                {
                                    taskContext.AddDebugMessage($"{UiSymbols.Package} {packageName} → {version}");
                                }
                            }
                            catch (Exception ex)
                            {
                                // Surface the underlying reason rather than burying it in verbose-only output.
                                // Sources come from the user's nuget.config, so the likely causes here are a
                                // feed that is unreachable/blocked, unauthenticated, or simply doesn't carry the
                                // package — and the NuGet layer's message names the offending source and why it
                                // failed. Reporting only "Failed to get version for X" leaves the user with no
                                // way to connect the failure to the nuget.config that selected that feed.
                                taskContext.AddStatusMessage($"{UiSymbols.Warning} Could not get version for {packageName}: {NugetErrorMessage.Redact(ex.Message)}");
                                if (required)
                                {
                                    return (1, $"Failed to get version for {packageName}: {NugetErrorMessage.Redact(ex.Message)}");
                                }
                            }

                            try
                            {
                                version = await dotNetService.AddOrUpdatePackageReferenceAsync(csprojFile, packageName, version, cancellationToken);
                                usedVersions[packageName] = version;
                                taskContext.AddStatusMessage($"{UiSymbols.Check} Added {packageName} {version}");
                            }
                            catch (Exception ex)
                            {
                                // Same reasoning as the version lookup above: 'dotnet add package' fails here
                                // when the feeds in the project's nuget.config cannot serve the package, and its
                                // message carries that detail. Keep it visible instead of debug-only.
                                taskContext.AddStatusMessage($"{UiSymbols.Warning} Could not add {packageName}: {NugetErrorMessage.Redact(ex.Message)}");
                                if (required)
                                {
                                    return (1, $"Failed to add {packageName} package reference: {NugetErrorMessage.Redact(ex.Message)}");
                                }
                                failedPackages.Add(packageName);
                            }
                        }

                        if (failedPackages.Count > 0)
                        {
                            var failedList = string.Join(", ", failedPackages);
                            if (usedVersions.Count > 0)
                            {
                                return (0, $"NuGet packages added to [underline]{csprojFile.Name}[/], but failed to add: {failedList}");
                            }

                            // Only optional package failures reach this point. Required package failures
                            // already return non-zero in the catch block above, so do not abort init here.
                            return (0, $"Failed to add optional NuGet packages: {failedList}");
                        }

                        return (0, $"NuGet packages added to [underline]{csprojFile.Name}[/]");
                    }, cancellationToken);

                    if (partialResult.Item1 != 0)
                    {
                        return partialResult;
                    }

                    // Apply MSIX csproj properties if the WindowsAppSDK package is in the project
                    // (whether we just added it or it was already there)
                    if (await dotNetService.HasPackageReferenceAsync(csprojFile, DotNetService.WINAPP_SDK_NUGET_PACKAGE, cancellationToken))
                    {
                        if (await dotNetService.EnsureEnableMsixToolingAsync(csprojFile, cancellationToken))
                        {
                            taskContext.AddDebugMessage($"{UiSymbols.Check} Enabled MSIX tooling");
                        }

                        if (await dotNetService.RemoveWindowsPackageTypeNoneAsync(csprojFile, cancellationToken))
                        {
                            taskContext.AddStatusMessage($"{UiSymbols.Check} Removed WindowsPackageType=None to enable packaged app mode");
                        }
                    }

                    // Add descriptive comments above package references in the csproj
                    var packageComments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [DotNetService.WINDOWS_SDK_BUILD_TOOLS_WINAPP_PACKAGE] = "WinApp CLI integration: enables 'dotnet run' support for packaged apps",
                        [DotNetService.WINAPP_SDK_NUGET_PACKAGE] = "Windows App SDK: provides WinUI 3, app lifecycle, windowing, and other modern Windows APIs"
                    };
                    await dotNetService.AnnotatePackageReferencesAsync(csprojFile, packageComments, cancellationToken);
                }

                // Native/C++ specific: Install SDK packages, headers, and build tools
                if (!isDotNetProject && options.SdkInstallMode != SdkInstallMode.None)
                {
                    // Ensure directories are initialized before use
                    if (globalWinappDir == null || localWinappDir == null)
                    {
                        return (1, "Workspace directories were not initialized.");
                    }

                    // Create all standard workspace directories for full setup/restore
                    nugetCacheDir = nugetService.GetNuGetGlobalPackagesDir();
                    var includeOut = localWinappDir.CreateSubdirectory("include");
                    var libRoot = localWinappDir.CreateSubdirectory("lib");
                    var binRoot = localWinappDir.CreateSubdirectory("bin");

                    // Step 4: Install packages
                    partialResult = await taskContext.AddSubTaskAsync("Installing SDK packages", async (taskContext, cancellationToken) =>
                    {
                        if (options.RequireExistingConfig && hadExistingConfig && config != null && config.Packages.Count > 0)
                        {
                            // Restore: use packages from existing config
                            var packageNames = config.Packages.Select(p => p.Name).ToArray();
                            usedVersions = await packageInstallationService.InstallPackagesAsync(
                                globalWinappDir,
                                packageNames,
                                taskContext,
                                sdkInstallMode: sdkInstallMode,
                                ignoreConfig: false, // Use config versions for restore
                                cancellationToken: cancellationToken);
                        }
                        else
                        {
                            // Setup: install standard SDK packages
                            usedVersions = await packageInstallationService.InstallPackagesAsync(
                                globalWinappDir,
                                NugetService.SDK_PACKAGES,
                                taskContext,
                                sdkInstallMode: sdkInstallMode,
                                ignoreConfig: options.IgnoreConfig,
                                cancellationToken: cancellationToken);
                        }

                        if (usedVersions == null)
                        {
                            return (1, "Error installing packages.");
                        }

                        // Step 5: Run cppwinrt and set up projections
                        var cppWinrtExe = cppWinrtService.FindCppWinrtExe(nugetCacheDir, usedVersions);
                        if (cppWinrtExe is null)
                        {
                            return (1, "cppwinrt.exe not found in installed packages.");
                        }

                        taskContext.AddDebugMessage($"{UiSymbols.Tools} Using cppwinrt tool → {cppWinrtExe}");

                        // Copy headers, libs, runtimes
                        taskContext.UpdateSubStatus("Copying headers");
                        packageLayoutService.CopyIncludesFromPackages(nugetCacheDir, includeOut, usedVersions);
                        taskContext.AddDebugMessage($"{UiSymbols.Check} Headers ready → {includeOut}");

                        taskContext.UpdateSubStatus("Copying import libraries");
                        packageLayoutService.CopyLibsAllArch(nugetCacheDir, libRoot, usedVersions);
                        var libArchs = libRoot.Exists ? string.Join(", ", libRoot.EnumerateDirectories().Select(d => d.Name)) : "(none)";
                        taskContext.AddDebugMessage($"{UiSymbols.Books} Import libs ready for archs: {libArchs}");

                        taskContext.UpdateSubStatus("Copying runtime binaries");
                        packageLayoutService.CopyRuntimesAllArch(nugetCacheDir, binRoot, usedVersions);
                        var binArchs = binRoot.Exists ? string.Join(", ", binRoot.EnumerateDirectories().Select(d => d.Name)) : "(none)";
                        taskContext.AddDebugMessage($"{UiSymbols.Check} Runtime binaries ready for archs: {binArchs}");

                        // Copy Windows App SDK license
                        try
                        {
                            if (usedVersions.TryGetValue(BuildToolsService.WINAPP_SDK_PACKAGE, out var wasdkVersion))
                            {
                                var pkgDir = nugetService.GetNuGetPackageDir(BuildToolsService.WINAPP_SDK_PACKAGE, wasdkVersion);
                                var licenseSrc = Path.Join(pkgDir.FullName, "license.txt");
                                if (File.Exists(licenseSrc))
                                {
                                    var shareDir = Path.Join(localWinappDir.FullName, "share", BuildToolsService.WINAPP_SDK_PACKAGE);
                                    Directory.CreateDirectory(shareDir);
                                    var licenseDst = Path.Join(shareDir, "copyright");
                                    File.Copy(licenseSrc, licenseDst, overwrite: true);
                                    taskContext.AddDebugMessage($"{UiSymbols.Check} License copied → {licenseDst}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            taskContext.AddDebugMessage($"{UiSymbols.Note} Failed to copy license: {ex.Message}");
                        }

                        // Collect winmd inputs and run cppwinrt
                        taskContext.UpdateSubStatus("Searching for .winmd metadata");
                        var winmds = packageLayoutService.FindWinmds(nugetCacheDir, usedVersions).ToList();
                        taskContext.AddDebugMessage($"{UiSymbols.Search} Found {winmds.Count} .winmd");
                        if (winmds.Count == 0)
                        {
                            return (2, "No .winmd files found for C++/WinRT projection.");
                        }

                        // Cache the WinMD inventory for the npm JS-bindings pipeline.
                        var yamlHash = (options.RequireExistingConfig && config?.Packages.Count > 0)
                            ? YamlPackagesHasher.Compute(config.Packages)
                            : YamlPackagesHasher.ComputeFromVersions(usedVersions
                                .Where(kvp => NugetService.SDK_PACKAGES.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase)));
                        await winmdsLockfileService.WriteAsync(
                            localWinappDir, usedVersions, winmds, nugetCacheDir, yamlHash, cancellationToken);

                        // Run cppwinrt
                        taskContext.UpdateSubStatus("Generating C++/WinRT projections");
                        await cppWinrtService.RunWithRspAsync(cppWinrtExe, winmds, includeOut, localWinappDir, taskContext, cancellationToken: cancellationToken);
                        taskContext.AddDebugMessage($"{UiSymbols.Check} C++/WinRT headers generated → {includeOut}");

                        partialResult = await taskContext.AddSubTaskAsync("Setting up tools", async (taskContext, cancellationToken) =>
                        {
                            // Step 6: Handle BuildTools
                            var buildToolsPinned = config?.GetVersion(BuildToolsService.BUILD_TOOLS_PACKAGE);
                            var forceLatestBuildTools = options.ForceLatestBuildTools || string.IsNullOrWhiteSpace(buildToolsPinned);

                            if (forceLatestBuildTools && options.RequireExistingConfig)
                            {
                                taskContext.UpdateSubStatus("Installing BuildTools");
                            }
                            else if (!string.IsNullOrWhiteSpace(buildToolsPinned))
                            {
                                taskContext.UpdateSubStatus($"Installing BuildTools {buildToolsPinned}");
                            }

                            var buildToolsPath = await buildToolsService.EnsureBuildToolsAsync(
                                taskContext,
                                forceLatest: forceLatestBuildTools,
                                cancellationToken: cancellationToken);

                            if (buildToolsPath != null)
                            {
                                taskContext.AddDebugMessage($"{UiSymbols.Check} BuildTools ready → {buildToolsPath}");
                            }

                            return (0, "Tools setup complete");
                        }, cancellationToken);

                        if (partialResult.Item1 != 0)
                        {
                            return partialResult;
                        }

                        return (0, "SDK and Windows App SDK packages downloaded and C++ headers generated in [underline].winapp[/]");
                    }, cancellationToken);

                    if (partialResult.Item1 != 0)
                    {
                        return partialResult;
                    }

                    if (usedVersions == null)
                    {
                        return (1, "Error determining installed package versions.");
                    }
                }

                // Install Windows App SDK Runtime (shared: both .NET and native paths)
                if (options.SdkInstallMode != SdkInstallMode.None)
                {
                    await taskContext.AddSubTaskAsync("Installing Windows App SDK Runtime", async (taskContext, cancellationToken) =>
                    {
                        try
                        {
                            var msixDir = windowsAppRuntimeService.FindWindowsAppSdkMsixDirectory(usedVersions);

                            if (msixDir != null)
                            {
                                // Install Windows App SDK runtime packages
                                (int installedCount, int errorCount, _) = await windowsAppRuntimeService.InstallWindowsAppRuntimeAsync(msixDir, taskContext, cancellationToken);

                                string? version = null;
                                if (usedVersions != null)
                                {
                                    usedVersions.TryGetValue(BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE, out version);
                                }

                                if (errorCount > 0)
                                {
                                    return (1, "Some Windows App Runtime packages failed to install.");
                                }
                                else if (installedCount == 0)
                                {
                                    return (0, version != null
                                        ? $"Windows App SDK Runtime ([underline]{version}[/]) already installed"
                                        : "Windows App SDK Runtime already installed");
                                }

                                return (0, version != null
                                    ? $"Windows App SDK Runtime installed: [underline]{version}[/]"
                                    : "Windows App SDK Runtime installed");
                            }
                            else
                            {
                                taskContext.AddStatusMessage($"{UiSymbols.Note} MSIX directory not found, skipping Windows App Runtime installation");
                                return (1, "Error locating Windows App SDK MSIX packages.");
                            }
                        }
                        catch (Exception ex)
                        {
                            taskContext.AddDebugMessage($"{UiSymbols.Note} Failed to install Windows App Runtime: {ex.Message}");
                            return (1, "Windows App Runtime installation failed.");
                        }
                    }, cancellationToken);
                }

                // Generate AppxManifest.xml (for setup only)
                if (!options.RequireExistingConfig)
                {
                    await SetupManifestSubTaskAsync(options, shouldGenerateManifest, manifestGenerationInfo, taskContext, cancellationToken);
                }

                // Add generated assets as Content items so MSIX tooling includes them in the package layout
                if (isDotNetProject && csprojFile != null && shouldGenerateManifest)
                {
                    if (await dotNetService.EnsureAssetContentItemsAsync(csprojFile, cancellationToken))
                    {
                        taskContext.AddDebugMessage($"{UiSymbols.Check} Added asset Content items to .csproj");
                    }
                }

                // Save configuration (native/C++ projects only — .NET uses .csproj PackageReferences)
                if (!isDotNetProject && !options.RequireExistingConfig && options.SdkInstallMode != SdkInstallMode.None && usedVersions != null)
                {
                    await taskContext.AddSubTaskAsync("Saving configuration", (taskContext, cancellationToken) =>
                    {
                        // Setup: Save winapp.yaml with used versions
                        var finalConfig = new WinappConfig();
                        // only from SDK_PACKAGES
                        var versionsToSave = usedVersions
                            .Where(kvp => NugetService.SDK_PACKAGES.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase))
                            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                        foreach (var kvp in versionsToSave)
                        {
                            finalConfig.SetVersion(kvp.Key, kvp.Value);
                        }
                        configService.Save(finalConfig);
                        taskContext.AddDebugMessage($"{UiSymbols.Save} Wrote config → {configService.ConfigPath}");
                        return Task.FromResult((0, "Configuration file created: [underline]winapp.yaml[/]"));
                    }, cancellationToken);
                }

                if (!options.RequireExistingConfig && options.SdkInstallMode != SdkInstallMode.None && !options.NoGitignore && localWinappDir?.Parent != null)
                {
                    var gitignorePath = Path.Join(localWinappDir.Parent.FullName, ".gitignore");

                    if (File.Exists(gitignorePath))
                    {
                        await taskContext.AddSubTaskAsync("Updating .gitignore", async (taskContext, cancellationToken) =>
                        {
                            // Update .gitignore to exclude .winapp folder (unless --no-gitignore is specified)
                            var addedWinAppToGitIgnore = await gitignoreService.AddWinAppFolderToGitIgnoreAsync(localWinappDir.Parent, taskContext, cancellationToken);

                            if (addedWinAppToGitIgnore)
                            {
                                return (0, "Added .winapp to [underline].gitignore[/]");
                            }

                            return (0, "[underline].gitignore[/] is up to date");
                        }, cancellationToken);
                    }
                }

                // Update Directory.Packages.props versions to match winapp.yaml if needed (only with SDK installation)
                if (options.SdkInstallMode != SdkInstallMode.None && config != null && directoryPackagesService.Exists(options.ConfigDir))
                {
                    await taskContext.AddSubTaskAsync("Updating Directory.Packages.props", (taskContext, cancellationToken) =>
                    {
                        try
                        {
                            var packageVersions = config.Packages.ToDictionary(
                                p => p.Name,
                                p => p.Version,
                                StringComparer.OrdinalIgnoreCase);

                            var wasUpdated = directoryPackagesService.UpdatePackageVersions(options.ConfigDir, packageVersions, taskContext);
                            return Task.FromResult((0, message: wasUpdated
                                ? "Directory.Packages.props updated"
                                : "Directory.Packages.props is up to date"));
                        }
                        catch (Exception ex)
                        {
                            taskContext.AddDebugMessage($"{UiSymbols.Note} Failed to update Directory.Packages.props: {ex.Message}");
                            // Don't fail the restore if Directory.Packages.props update fails
                            return Task.FromResult((0, "Directory.Packages.props update failed"));
                        }
                    }, cancellationToken);
                }

                // We're done
                string successMessage;
                if (isDotNetProject)
                {
                    successMessage = ".NET project setup completed successfully";
                }
                else
                {
                    successMessage = options.RequireExistingConfig ? "Restore completed successfully" : "Setup completed successfully";
                }
                if (options.SdkInstallMode == SdkInstallMode.None)
                {
                    successMessage += " (SDK installation skipped)";
                }
                return (0, successMessage);
            }
            catch (OperationCanceledException)
            {
                return (1, "Operation cancelled");
            }
            catch (Exception ex)
            {
                var operation = isDotNetProject ? ".NET Init" : (options.RequireExistingConfig ? "Restore" : "Init");
                taskContext.StatusError($"{operation} failed: {ex.Message}" + Environment.NewLine +
                                        $"{ex.StackTrace}");
                return (1, "Error!");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Selects the .csproj file to configure when multiple are found.
    /// </summary>
    /// <summary>
    /// For a .NET project, keeps winapp's own NuGet lookups on the same <c>nuget.config</c> hierarchy the .NET
    /// SDK will use. Versions are chosen here but written into the project as <c>PackageReference</c> entries
    /// by <c>dotnet add package</c>, which always resolves <c>nuget.config</c> by walking up from the project.
    /// Any other root can disagree with it: an unrelated <c>--config-dir</c> misses the project's sources
    /// entirely, and even an ANCESTOR one does, because starting higher up skips a <c>nuget.config</c> sitting
    /// in the project itself. Either way a version can be selected from a feed dotnet cannot see, and the
    /// reference then fails to restore with NU1102.
    ///
    /// So the root is always the project directory. NuGet walks up from there, so anything an ancestor
    /// <c>--config-dir</c> would have contributed is still included — this only adds back the levels below it.
    /// The ancestor check decides only whether the user needs to be told their <c>--config-dir</c> does not
    /// apply, not which root to use.
    ///
    /// Aligning to the project rather than forcing <c>dotnet</c> to the config directory is deliberate. The
    /// only way to do the latter is <c>--configfile</c>, which replaces the entire hierarchy with one file and
    /// so drops the user- and machine-level sources and their credentials.
    /// </summary>
    private void AlignNugetConfigRootWithProject(DirectoryInfo configDir, FileInfo csprojFile)
    {
        var projectDirectory = csprojFile.Directory!;

        // Warn only when the selected directory is outside what dotnet discovers. An ancestor still
        // contributes its settings once we root at the project, so there is nothing to report.
        if (!DirectoryRelationship.IsSameOrAncestor(configDir, projectDirectory))
        {
            logger.LogWarning(
                "{UISymbol} The selected configuration directory ({ConfigDir}) does not apply to {Project}: 'dotnet add package' resolves nuget.config relative to the project. Using the project's own nuget.config hierarchy for package sources so the versions selected here can actually be restored.",
                UiSymbols.Warning,
                configDir.FullName,
                csprojFile.Name);
        }

        nugetSourceProvider.SetConfigRoot(projectDirectory);
    }

    private async Task<FileInfo> SelectCsprojFileAsync(IReadOnlyList<FileInfo> csprojFiles, CancellationToken cancellationToken)
    {
        if (csprojFiles.Count == 1)
        {
            return csprojFiles[0];
        }

        // Multiple .csproj files found — ask the user which one to use
        var choices = csprojFiles.Select(f => f.Name).ToArray();
        var selected = await ansiConsole.PromptAsync(
            new SelectionPrompt<string>()
                .Title("Multiple .csproj files found. Which project should be configured?")
                .AddChoices(choices),
            cancellationToken);
        return csprojFiles.First(f => f.Name == selected);
    }

    private async Task SetupManifestSubTaskAsync(WorkspaceSetupOptions options, bool shouldGenerateManifest, ManifestGenerationInfo? manifestGenerationInfo, TaskContext taskContext, CancellationToken cancellationToken)
    {
        await taskContext.AddSubTaskAsync("Generating Manifest and Assets", async (taskContext, cancellationToken) =>
        {
            if (!shouldGenerateManifest || manifestGenerationInfo == null)
            {
                taskContext.AddDebugMessage($"{UiSymbols.Skip} AppxManifest.xml generation skipped");
                return (0, "Manifest generation skipped");
            }

            try
            {
                await manifestService.GenerateManifestAsync(
                    directory: options.BaseDirectory,
                    manifestGenerationInfo: manifestGenerationInfo,
                    manifestTemplate: ManifestTemplates.Packaged,
                    logoPath: null,
                    executable: null,
                    taskContext,
                    cancellationToken: cancellationToken);

                return (0, "Manifest and Assets created: [underline]Package.appxmanifest[/]");
            }
            catch (Exception ex)
            {
                taskContext.AddDebugMessage($"{UiSymbols.Note} Failed to generate manifest: {ex.Message}");
                return (0, "Manifest generation failed, but continuing setup");
            }
        }, cancellationToken);
    }

    private async Task<(int ReturnCode, WinappConfig? Config, bool HadExistingConfig, bool ShouldGenerateManifest, ManifestGenerationInfo? ManifestGenerationInfo, bool ShouldEnableDeveloperMode, string? RecommendedTfm)> InitializeConfigurationAsync(WorkspaceSetupOptions options, bool isDotNetProject, FileInfo? csprojFile, CancellationToken cancellationToken)
    {
        if (!options.RequireExistingConfig && !options.ConfigOnly && options.SdkInstallMode == null && options.UseDefaults)
        {
            // Default to Stable when --use-defaults
            options.SdkInstallMode = SdkInstallMode.Stable;
        }

        var hadExistingConfig = configService.Exists();
        bool shouldGenerateManifest = true;
        bool shouldEnableDeveloperMode = false;
        string? recommendedTfm = null;
        ManifestGenerationInfo? manifestGenerationInfo = null;
        WinappConfig? config = null;

        // Step 2: Load or prepare configuration
        if (hadExistingConfig)
        {
            config = configService.Load();

            if (config.Packages.Count == 0 && options.RequireExistingConfig)
            {
                logger.LogInformation("{UISymbol} winapp.yaml found but contains no packages. Nothing to restore.", UiSymbols.Note);
                shouldEnableDeveloperMode = await AskShouldEnableDeveloperModeAsync(options, cancellationToken);
                return (0, config, hadExistingConfig, shouldGenerateManifest, manifestGenerationInfo, shouldEnableDeveloperMode, recommendedTfm);
            }

            var operation = options.RequireExistingConfig ? "Found" : "Found existing";
            logger.LogDebug("{UISymbol} {Operation} winapp.yaml with {PackageCount} packages", UiSymbols.Package, operation, config.Packages.Count);

            if (!options.RequireExistingConfig && config.Packages.Count > 0)
            {
                logger.LogDebug("{UISymbol} Using pinned package versions from winapp.yaml unless overridden.", UiSymbols.Note);
            }

            // For setup command: ask about overwriting existing config (only if not skipping SDK installation and not config-only mode)
            if (!options.RequireExistingConfig && !options.IgnoreConfig && !options.ConfigOnly && options.SdkInstallMode != SdkInstallMode.None && config.Packages.Count > 0)
            {
                if (options.UseDefaults)
                {
                    options.IgnoreConfig = true;
                }
                else
                {
                    var overwriteConfig = await ShowConfirmationPromptAsync(ansiConsole, "winapp.yaml exists with pinned versions. Overwrite?", cancellationToken);
                    shouldGenerateManifest = await AskShouldGenerateManifestAsync(options, cancellationToken);
                    if (shouldGenerateManifest)
                    {
                        manifestGenerationInfo = await PromptForManifestInfoAsync(options, cancellationToken);
                    }
                    if (!overwriteConfig)
                    {
                        options.IgnoreConfig = true;
                    }
                    else
                    {
                        await AskSdkInstallModeAsync(options, isDotNetProject, csprojFile, cancellationToken);
                    }
                }
            }
        }
        else
        {
            shouldGenerateManifest = await AskShouldGenerateManifestAsync(options, cancellationToken);
            if (shouldGenerateManifest)
            {
                manifestGenerationInfo = await PromptForManifestInfoAsync(options, cancellationToken);
            }

            await AskSdkInstallModeAsync(options, isDotNetProject, csprojFile, cancellationToken);
            if (options.SdkInstallMode != SdkInstallMode.None)
            {
                config = new WinappConfig();
                logger.LogDebug("{UISymbol} No winapp.yaml found; will generate one after setup.", UiSymbols.New);
            }
        }

        // .NET: Validate TargetFramework (interactive)
        if (isDotNetProject && csprojFile != null)
        {
            if (dotNetService.IsMultiTargeted(csprojFile))
            {
                logger.LogError("The project '{CsprojFile}' uses multi-targeting (TargetFrameworks). winapp init does not support multi-targeted projects.", csprojFile.Name);
                return (1, config, hadExistingConfig, shouldGenerateManifest, manifestGenerationInfo, shouldEnableDeveloperMode, recommendedTfm);
            }

            var currentTfm = dotNetService.GetTargetFramework(csprojFile);
            logger.LogDebug("Current TargetFramework: {Tfm}", currentTfm ?? "(not set)");

            if (currentTfm == null || !dotNetService.IsTargetFrameworkSupported(currentTfm))
            {
                recommendedTfm = dotNetService.GetRecommendedTargetFramework(currentTfm);

                if (!options.UseDefaults)
                {
                    var currentDisplay = currentTfm ?? "(not set)";

                    var promptSuffix = options.SdkInstallMode != SdkInstallMode.None
                        ? " (Required for Windows App SDK)"
                        : "";

                    var shouldUpdate = await ShowConfirmationPromptAsync(ansiConsole, $"Update TargetFramework to \"{recommendedTfm}\"{promptSuffix}?", cancellationToken);

                    if (!shouldUpdate)
                    {
                        if (options.SdkInstallMode != SdkInstallMode.None)
                        {
                            logger.LogError("TargetFramework '{Tfm}' is not supported for Windows App SDK. Cannot continue.", currentDisplay);
                            return (1, config, hadExistingConfig, shouldGenerateManifest, manifestGenerationInfo, shouldEnableDeveloperMode, recommendedTfm);
                        }

                        // Not installing SDKs, so TFM update is not required — skip it
                        recommendedTfm = null;
                    }
                }
                else
                {
                    var currentDisplay = currentTfm ?? "(not set)";
                    logger.LogWarning(
                        "TargetFramework '{CurrentTfm}' is not supported for Windows App SDK. Automatically updating to '{RecommendedTfm}' because --use-defaults was specified.",
                        currentDisplay,
                        recommendedTfm);
                    logger.LogInformation("Automatically updating TargetFramework from {CurrentTfm} to {RecommendedTfm} because --use-defaults was specified.", Markup.Escape(currentDisplay), recommendedTfm);
                }
            }
            else
            {
                logger.LogDebug("{UISymbol} TargetFramework '{Tfm}' is supported", UiSymbols.Check, currentTfm);
            }
        }

        shouldEnableDeveloperMode = await AskShouldEnableDeveloperModeAsync(options, cancellationToken);

        return (0, config, hadExistingConfig, shouldGenerateManifest, manifestGenerationInfo, shouldEnableDeveloperMode, recommendedTfm);
    }

    private static async Task<bool> ShowConfirmationPromptAsync(IAnsiConsole ansiConsole, string prompt, CancellationToken cancellationToken)
    {
        var result = await ansiConsole.PromptAsync(new ConfirmationPrompt(prompt), cancellationToken);

        ansiConsole.Cursor.MoveUp();
        ansiConsole.Write("\x1b[2K"); // Clear line
        ansiConsole.MarkupLine($"{prompt}: [underline]{(result ? "Yes" : "No")}[/]");

        return result;
    }

    private async Task<ManifestGenerationInfo?> PromptForManifestInfoAsync(WorkspaceSetupOptions options, CancellationToken cancellationToken)
    {
        if (options.ConfigOnly)
        {
            return null;
        }

        return await manifestService.PromptForManifestInfoAsync(options.BaseDirectory, null, null, "1.0.0.0", "Windows Application", null, options.UseDefaults, cancellationToken);
    }

    private async Task<bool> AskShouldEnableDeveloperModeAsync(WorkspaceSetupOptions options, CancellationToken cancellationToken)
    {
        if (options.ConfigOnly || options.RequireExistingConfig)
        {
            return false;
        }

        if (devModeService.IsEnabled())
        {
            return false;
        }

        if (options.UseDefaults)
        {
            return false;
        }

        return await ShowConfirmationPromptAsync(ansiConsole, "Enable Developer Mode (requires elevation and you will be prompted by User Account Control)", cancellationToken);
    }

    private async Task<bool> AskShouldGenerateManifestAsync(WorkspaceSetupOptions options, CancellationToken cancellationToken)
    {
        if (options.RequireExistingConfig)
        {
            return true;
        }

        // Check if manifest already exists, and if so, ask about overwriting
        var manifestPath = MsixService.FindProjectManifest(currentDirectoryProvider, options.BaseDirectory);
        if ((manifestPath?.Exists) == true)
        {
            logger.LogDebug("{UISymbol} {ManifestFileName} already exists at {ManifestPath}", UiSymbols.Check, manifestPath.Name, manifestPath.FullName);
            if (options.UseDefaults)
            {
                // With --use-defaults, skip overwriting existing manifest (non-destructive)
                return false;
            }
            else
            {
                return await ShowConfirmationPromptAsync(ansiConsole, $"{manifestPath.Name} already exists. Overwrite?", cancellationToken);
            }
        }

        return true;
    }

    private async Task AskSdkInstallModeAsync(WorkspaceSetupOptions options, bool isDotNetProject, FileInfo? csprojFile, CancellationToken cancellationToken)
    {
        // For init (not restore), prompt for SDK installation choice if not specified
        if (!options.RequireExistingConfig && !options.ConfigOnly && options.SdkInstallMode == null)
        {
            // If the .NET project already references WinAppSDK, skip the prompt and default to None.
            // This call may take a while on a fresh machine because `dotnet list package` triggers
            // an implicit restore — surface a spinner so the user knows we're doing something (#463).
            if (isDotNetProject && csprojFile != null)
            {
                var alreadyReferencesWinAppSdk = await RunWithStatusAsync(
                    "Detecting project SDK references...",
                    ct => dotNetService.HasPackageReferenceAsync(csprojFile, DotNetService.WINAPP_SDK_NUGET_PACKAGE, ct),
                    cancellationToken);
                if (alreadyReferencesWinAppSdk)
                {
                    options.SdkInstallMode = SdkInstallMode.None;
                    logger.LogInformation("{UISymbol} Project already references {PackageName}; skipping Windows App SDK setup.", UiSymbols.Check, DotNetService.WINAPP_SDK_NUGET_PACKAGE);
                    return;
                }
            }
            // Determine which packages to show versions for
            var packages = isDotNetProject
                ? [BuildToolsService.WINAPP_SDK_PACKAGE]
                : new[] { BuildToolsService.CPP_SDK_PACKAGE, BuildToolsService.WINAPP_SDK_PACKAGE };

            // Fetch versions for all modes in parallel (failures are non-fatal). On a fresh machine
            // these NuGet feed calls can take many seconds; show a spinner so the prompt doesn't
            // appear to hang (#463).
            var modes = new[] { SdkInstallMode.Stable, SdkInstallMode.Preview, SdkInstallMode.Experimental };
            var versionTasks = await RunWithStatusAsync(
                "Fetching latest SDK versions...",
                async ct =>
                {
                    var tasks = modes
                        .SelectMany(mode => packages.Select(pkg => (Mode: mode, Package: pkg, Task: SafeGetLatestVersionAsync(pkg, mode, ct))))
                        .ToList();
                    await Task.WhenAll(tasks.Select(v => v.Task));
                    return tasks;
                },
                cancellationToken);

            // Build a lookup: (mode) → version label
            var versionsByMode = modes.ToDictionary(
                mode => mode,
                mode =>
                {
                    var parts = versionTasks
                        .Where(v => v.Mode == mode && v.Task.Result != null)
                        .Select(v => $"{(v.Package == BuildToolsService.CPP_SDK_PACKAGE ? "Windows SDK" : "Windows App SDK")} [green]{v.Task.Result}[/]");
                    return string.Join(", ", parts);
                });

            var label = isDotNetProject ? "Windows App SDK" : "SDKs";
            string FormatChoice(string modeLabel, SdkInstallMode mode)
            {
                var versions = versionsByMode[mode];
                return string.IsNullOrEmpty(versions)
                    ? $"Setup {modeLabel} {label}"
                    : $"Setup {modeLabel} {label} ({versions})";
            }
            string[] sdkChoices = [
                FormatChoice("Stable", SdkInstallMode.Stable),
                FormatChoice("Preview", SdkInstallMode.Preview),
                FormatChoice("Experimental", SdkInstallMode.Experimental),
                $"Do not setup {label}"
            ];

            ansiConsole.WriteLine($"Select {label} setup option:");
            var sdkPrompt = new SelectionPrompt<string>()
                .AddChoices(sdkChoices);

            var sdkChoice = await ansiConsole.PromptAsync(sdkPrompt, cancellationToken);

            ansiConsole.Cursor.MoveUp();
            ansiConsole.Write("\x1b[2K"); // Clear line

            if (sdkChoice == sdkChoices[0])
            {
                options.SdkInstallMode = SdkInstallMode.Stable;
            }
            else if (sdkChoice == sdkChoices[1])
            {
                options.SdkInstallMode = SdkInstallMode.Preview;
            }
            else if (sdkChoice == sdkChoices[2])
            {
                options.SdkInstallMode = SdkInstallMode.Experimental;
            }
            else
            {
                options.SdkInstallMode = SdkInstallMode.None;
                logger.LogInformation("Setup {Label}: Do not setup {Label}", label, label);
                return;
            }

            ansiConsole.MarkupLine($"Setup {label}: [underline]{Markup.Remove(sdkChoice["Setup ".Length..])}[/]");
        }
    }

    private async Task<string?> SafeGetLatestVersionAsync(string packageName, SdkInstallMode mode, CancellationToken cancellationToken)
    {
        try
        {
            return await nugetService.GetLatestVersionAsync(packageName, sdkInstallMode: mode, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug("Failed to fetch latest version for {PackageName} ({Mode}): {ErrorMessage}", packageName, mode, NugetErrorMessage.Redact(ex.Message));
            return null;
        }
    }

    /// <summary>
    /// Gets the current system architecture string for package selection. Thin delegator to
    /// <see cref="RunArchHelper.DefaultArchitecture"/>, which owns the process-arch mapping; kept for
    /// the many existing callers that reference this name.
    /// </summary>
    /// <returns>Architecture string (x64, arm64, x86)</returns>
    public static string GetSystemArchitecture() => RunArchHelper.DefaultArchitecture();

    /// <summary>
    /// Runs <paramref name="work"/> while showing a Spectre.Console spinner with <paramref name="message"/>.
    /// In non-interactive contexts (redirected output, no Information logging), falls back to a single
    /// log line so the user still sees what's happening (#463).
    /// </summary>
    private async Task<T> RunWithStatusAsync<T>(string message, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        if (Environment.UserInteractive
            && !Console.IsOutputRedirected
            && logger.IsEnabled(LogLevel.Information)
            && ansiConsole.Profile.Capabilities.Interactive)
        {
            T result = default!;
            await ansiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync(message, async _ =>
                {
                    result = await work(cancellationToken);
                });
            return result;
        }

        logger.LogInformation("{Message}", message);
        return await work(cancellationToken);
    }
}
