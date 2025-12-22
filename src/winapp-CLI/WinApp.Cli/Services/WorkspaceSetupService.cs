// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Spectre.Console;
using System.Runtime.InteropServices;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Specifies the SDK installation mode for workspace setup
/// </summary>
internal enum SdkInstallMode
{
    /// <summary>
    /// Install stable SDK packages (Windows SDK, WinAppSDK)
    /// </summary>
    Stable,

    /// <summary>
    /// Install preview/experimental SDK packages
    /// </summary>
    Experimental,

    /// <summary>
    /// Skip SDK installation entirely
    /// </summary>
    None
}

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
    public bool NoCert { get; set; }
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
    ICertificateService certificateService,
    IPowerShellService powerShellService,
    INugetService nugetService,
    IManifestService manifestService,
    IDevModeService devModeService,
    IGitignoreService gitignoreService,
    IDirectoryPackagesService directoryPackagesService,
    IStatusService statusService,
    ICurrentDirectoryProvider currentDirectoryProvider) : IWorkspaceSetupService
{
    public async Task<int> SetupWorkspaceAsync(WorkspaceSetupOptions options, CancellationToken cancellationToken = default)
    {
        configService.ConfigPath = new FileInfo(Path.Combine(options.ConfigDir.FullName, "winapp.yaml"));

        return await statusService.ExecuteWithStatusAsync("Setting up workspace", async (taskContext, cancellationToken) =>
        {
            try
            {
                bool hadExistingConfig = default;
                WinappConfig? config = null;
                DirectoryInfo? globalWinappDir = null;
                DirectoryInfo? localWinappDir = null;

                // For init (not restore), prompt for SDK installation choice if not specified
                if (!options.RequireExistingConfig && !options.ConfigOnly && options.SdkInstallMode == null)
                {
                    if (options.UseDefaults)
                    {
                        // Default to Stable when --yes is specified
                        options.SdkInstallMode = SdkInstallMode.Stable;
                    }
                    else
                    {
                        string[] sdkChoices = [
                            "Install Stable SDKs (Windows SDK, WinAppSDK)",
                            "Install Preview SDKs (Windows SDK, WinAppSDK)",
                            "Do not install SDKs"
                        ];

                        var sdkPrompt = new SelectionPrompt<string>()
                            .Title("Select SDK installation option:")
                            .AddChoices(sdkChoices);

                        var sdkChoice = await taskContext.PromptAsync(sdkPrompt, cancellationToken);

                        if (sdkChoice == sdkChoices[0])
                        {
                            options.SdkInstallMode = SdkInstallMode.Stable;
                        }
                        else if (sdkChoice == sdkChoices[1])
                        {
                            options.SdkInstallMode = SdkInstallMode.Experimental;
                        }
                        else
                        {
                            options.SdkInstallMode = SdkInstallMode.None;
                        }
                    }
                }

                // Apply SDK mode settings
                var includeExperimental = options.SdkInstallMode == SdkInstallMode.Experimental;

                var partialResult = await taskContext.AddSubTaskAsync("Processing configuration", async (taskContext, cancellationToken) =>
                {
                    // Step 1: Handle configuration requirements
                    if (options.RequireExistingConfig && !configService.Exists())
                    {
                        return (1, $"winapp.yaml not found in {options.ConfigDir}{Environment.NewLine}Run 'winapp init' to initialize a new workspace or navigate to a directory with winapp.yaml");
                    }

                    // Step 2: Load or prepare configuration
                    hadExistingConfig = configService.Exists();

                    if (hadExistingConfig)
                    {
                        config = configService.Load();

                        if (config.Packages.Count == 0 && options.RequireExistingConfig)
                        {
                            return (0, $"{UiSymbols.Note} winapp.yaml found but contains no packages. Nothing to restore.");
                        }

                        var operation = options.RequireExistingConfig ? "Found" : "Found existing";
                        taskContext.AddDebugMessage($"{UiSymbols.Package} {operation} winapp.yaml with {config.Packages.Count} packages");

                        if (!options.RequireExistingConfig && config.Packages.Count > 0)
                        {
                            taskContext.AddDebugMessage($"{UiSymbols.Note} Using pinned package versions from winapp.yaml unless overridden.");
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
                                var overwriteConfig = await taskContext.PromptAsync(new ConfirmationPrompt("winapp.yaml exists with pinned versions. Overwrite with latest versions?"), cancellationToken);
                                if (!overwriteConfig)
                                {
                                    options.IgnoreConfig = true;
                                }
                            }
                        }
                    }
                    else if (options.SdkInstallMode != SdkInstallMode.None)
                    {
                        config = new WinappConfig();
                        taskContext.AddDebugMessage($"{UiSymbols.New} No winapp.yaml found; will generate one after setup.");
                    }

                    // Handle config-only mode: just create/validate config file and exit
                    if (options.ConfigOnly)
                    {
                        if (hadExistingConfig && config != null)
                        {
                            taskContext.AddStatusMessage($"{UiSymbols.Check} Existing configuration file found and validated → {configService.ConfigPath}");
                            taskContext.AddStatusMessage($"{UiSymbols.Package} Configuration contains {config.Packages.Count} packages");

                            if (config.Packages.Count > 0)
                            {
                                await taskContext.AddSubTaskAsync($"{UiSymbols.Note} Configured packages", (taskContext, cancellationToken) =>
                                {
                                    foreach (var pkg in config.Packages)
                                    {
                                        taskContext.AddDebugMessage($"{UiSymbols.Bullet} {pkg.Name} = {pkg.Version}");
                                    }
                                    return Task.FromResult((0, $"{UiSymbols.Note} Configured packages"));
                                }, cancellationToken);
                            }
                        }
                        else if (options.SdkInstallMode != SdkInstallMode.None)
                        {
                            taskContext.AddStatusMessage("Creating configuration file");

                            // Get latest package versions (respecting prerelease option)
                            var defaultVersions = new Dictionary<string, string>();
                            foreach (var packageName in NugetService.SDK_PACKAGES)
                            {
                                try
                                {
                                    var version = await nugetService.GetLatestVersionAsync(
                                        packageName,
                                        includePrerelease: includeExperimental,
                                        cancellationToken: cancellationToken);
                                    defaultVersions[packageName] = version;
                                }
                                catch (Exception ex)
                                {
                                    taskContext.AddDebugMessage($"{UiSymbols.Note} Could not get version for {packageName}: {ex.Message}");
                                }
                            }

                            var finalConfig = new WinappConfig();
                            foreach (var kvp in defaultVersions)
                            {
                                finalConfig.SetVersion(kvp.Key, kvp.Value);
                            }

                            configService.Save(finalConfig);

                            taskContext.AddDebugMessage($"{UiSymbols.Save} Configuration file created → {configService.ConfigPath}");
                            taskContext.AddDebugMessage($"{UiSymbols.Package} Added {finalConfig.Packages.Count} default SDK packages");

                            await taskContext.AddSubTaskAsync($"{UiSymbols.Note} Generated packages", (taskContext, cancellationToken) =>
                            {
                                foreach (var pkg in finalConfig.Packages)
                                {
                                    taskContext.AddDebugMessage($"{UiSymbols.Bullet} {pkg.Name} = {pkg.Version}");
                                }
                                return Task.FromResult((0, $"{UiSymbols.Note} Generated packages"));
                            }, cancellationToken);

                            if (includeExperimental)
                            {
                                taskContext.AddDebugMessage($"{UiSymbols.Wrench} Prerelease packages were included");
                            }
                        }
                        // else: SdkInstallMode == None and no existing config - nothing to do

                        return (0, "Configuration-only operation completed");
                    }

                    // If skipping SDK installation, we don't need workspace directories
                    if (options.SdkInstallMode == SdkInstallMode.None)
                    {
                        taskContext.AddDebugMessage($"{UiSymbols.Skip} SDK installation skipped by user choice");
                        return (0, "Configuration processed (SDK installation skipped)");
                    }

                    // Step 3: Initialize workspace
                    globalWinappDir = winappDirectoryService.GetGlobalWinappDirectory();
                    localWinappDir = winappDirectoryService.GetLocalWinappDirectory(options.BaseDirectory);

                    // Setup-specific startup messages
                    if (!options.RequireExistingConfig)
                    {
                        taskContext.AddDebugMessage($"{UiSymbols.Rocket} using config → {configService.ConfigPath}");
                        taskContext.AddDebugMessage($"{UiSymbols.Rocket} winapp init starting in {options.BaseDirectory}");
                        taskContext.AddDebugMessage($"{UiSymbols.Folder} Global packages → {globalWinappDir}");
                        taskContext.AddDebugMessage($"{UiSymbols.Folder} Local workspace → {localWinappDir}");

                        if (includeExperimental)
                        {
                            taskContext.AddDebugMessage($"{UiSymbols.Wrench} Experimental/prerelease packages will be included");
                        }
                    }
                    else
                    {
                        taskContext.AddDebugMessage($"{UiSymbols.Folder} Global packages → {globalWinappDir}");
                        taskContext.AddDebugMessage($"{UiSymbols.Folder} Local workspace → {localWinappDir}");
                    }

                    // First ensure basic workspace (for global packages)
                    taskContext.AddStatusMessage("Initializing workspace");
                    packageInstallationService.InitializeWorkspace(globalWinappDir);

                    return (0, "Workspace initialized");
                }, cancellationToken);

                if (partialResult.Item1 != 0)
                {
                    return partialResult;
                }

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
                        try
                        {
                            taskContext.UpdateSubStatus("Checking Developer Mode");
                            var devModeResult = await devModeService.EnsureWin11DevModeAsync(taskContext, cancellationToken);

                            if (devModeResult != 0 && devModeResult != 3010)
                            {
                                taskContext.AddDebugMessage($"{UiSymbols.Note} Developer Mode setup returned exit code {devModeResult}");
                            }
                        }
                        catch (Exception ex)
                        {
                            taskContext.AddDebugMessage($"{UiSymbols.Note} Developer Mode setup failed: {ex.Message}");
                        }
                        return (0, "Developer mode enabled");
                    }, cancellationToken);
                }

                // When not skipping SDK installation, create workspace directories and install packages
                Dictionary<string, string>? usedVersions = null;
                DirectoryInfo? pkgsDir = null;

                if (options.SdkInstallMode != SdkInstallMode.None)
                {
                    // Ensure directories are initialized before use
                    if (globalWinappDir == null || localWinappDir == null)
                    {
                        return (1, "Workspace directories were not initialized.");
                    }

                    // Create all standard workspace directories for full setup/restore
                    pkgsDir = globalWinappDir.CreateSubdirectory("packages");
                    var includeOut = localWinappDir.CreateSubdirectory("include");
                    var libRoot = localWinappDir.CreateSubdirectory("lib");
                    var binRoot = localWinappDir.CreateSubdirectory("bin");

                    // Step 4: Install packages
                    usedVersions = await taskContext.AddSubTaskAsync("Installing SDK packages", async (taskContext, cancellationToken) =>
                    {
                        if (options.RequireExistingConfig && hadExistingConfig && config != null && config.Packages.Count > 0)
                        {
                            // Restore: use packages from existing config
                            var packageNames = config.Packages.Select(p => p.Name).ToArray();
                            return await packageInstallationService.InstallPackagesAsync(
                                globalWinappDir,
                                packageNames,
                                taskContext,
                                includeExperimental: includeExperimental,
                                ignoreConfig: false, // Use config versions for restore
                                cancellationToken: cancellationToken);
                        }

                        // Setup: install standard SDK packages
                        return await packageInstallationService.InstallPackagesAsync(
                            globalWinappDir,
                            NugetService.SDK_PACKAGES,
                            taskContext,
                            includeExperimental: includeExperimental,
                            ignoreConfig: options.IgnoreConfig,
                            cancellationToken: cancellationToken);
                    }, cancellationToken);

                    if (usedVersions == null)
                    {
                        return (1, "Error installing packages.");
                    }

                    partialResult = await taskContext.AddSubTaskAsync("Setting up build environment", async (taskContext, cancellationToken) =>
                    {
                        // Step 5: Run cppwinrt and set up projections
                        var cppWinrtExe = cppWinrtService.FindCppWinrtExe(pkgsDir, usedVersions);
                        if (cppWinrtExe is null)
                        {
                            return (1, "cppwinrt.exe not found in installed packages.");
                        }

                        taskContext.AddDebugMessage($"{UiSymbols.Tools} Using cppwinrt tool → {cppWinrtExe}");

                        // Copy headers, libs, runtimes
                        taskContext.UpdateSubStatus("Copying headers");
                        packageLayoutService.CopyIncludesFromPackages(pkgsDir, includeOut);
                        taskContext.AddDebugMessage($"{UiSymbols.Check} Headers ready → {includeOut}");

                        taskContext.UpdateSubStatus("Copying import libraries");
                        packageLayoutService.CopyLibsAllArch(pkgsDir, libRoot);
                        var libArchs = libRoot.Exists ? string.Join(", ", libRoot.EnumerateDirectories().Select(d => d.Name)) : "(none)";
                        taskContext.AddDebugMessage($"{UiSymbols.Books} Import libs ready for archs: {libArchs}");

                        taskContext.UpdateSubStatus("Copying runtime binaries");
                        packageLayoutService.CopyRuntimesAllArch(pkgsDir, binRoot);
                        var binArchs = binRoot.Exists ? string.Join(", ", binRoot.EnumerateDirectories().Select(d => d.Name)) : "(none)";
                        taskContext.AddDebugMessage($"{UiSymbols.Check} Runtime binaries ready for archs: {binArchs}");

                        // Copy Windows App SDK license
                        try
                        {
                            if (usedVersions.TryGetValue("Microsoft.WindowsAppSDK", out var wasdkVersion))
                            {
                                var pkgDir = Path.Combine(pkgsDir.FullName, $"Microsoft.WindowsAppSDK.{wasdkVersion}");
                                var licenseSrc = Path.Combine(pkgDir, "license.txt");
                                if (File.Exists(licenseSrc))
                                {
                                    var shareDir = Path.Combine(localWinappDir.FullName, "share", "Microsoft.WindowsAppSDK");
                                    Directory.CreateDirectory(shareDir);
                                    var licenseDst = Path.Combine(shareDir, "copyright");
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
                        var winmds = packageLayoutService.FindWinmds(pkgsDir, usedVersions).ToList();
                        taskContext.AddDebugMessage($"{UiSymbols.Search} Found {winmds.Count} .winmd");
                        if (winmds.Count == 0)
                        {
                            return (2, "No .winmd files found for C++/WinRT projection.");
                        }

                        // Run cppwinrt
                        taskContext.UpdateSubStatus("Generating C++/WinRT projections");
                        await cppWinrtService.RunWithRspAsync(cppWinrtExe, winmds, includeOut, localWinappDir, taskContext, cancellationToken: cancellationToken);
                        taskContext.AddDebugMessage($"{UiSymbols.Check} C++/WinRT headers generated → {includeOut}");

                        return (0, "Build environment setup complete");
                    }, cancellationToken);

                    if (partialResult.Item1 != 0)
                    {
                        return partialResult;
                    }

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

                        // Install Windows App Runtime (if not already installed)
                        try
                        {
                            var msixDir = FindWindowsAppSdkMsixDirectory(usedVersions);

                            if (msixDir != null)
                            {
                                taskContext.UpdateSubStatus("Installing Windows App Runtime");

                                // Install Windows App SDK runtime packages
                                await InstallWindowsAppRuntimeAsync(msixDir, taskContext, cancellationToken);

                                taskContext.AddDebugMessage($"{UiSymbols.Check} Windows App Runtime installation complete");
                            }
                            else
                            {
                                taskContext.AddStatusMessage($"{UiSymbols.Note} MSIX directory not found, skipping Windows App Runtime installation");
                            }
                        }
                        catch (Exception ex)
                        {
                            taskContext.AddDebugMessage($"{UiSymbols.Note} Failed to install Windows App Runtime: {ex.Message}");
                        }
                        return (0, "Tools setup complete");
                    }, cancellationToken);
                }

                // Generate AppxManifest.xml (for setup only)
                if (!options.RequireExistingConfig)
                {
                    await taskContext.AddSubTaskAsync("Generating AppxManifest.xml", async (taskContext, cancellationToken) =>
                    {
                        // Check if manifest already exists
                        var manifestPath = MsixService.FindProjectManifest(currentDirectoryProvider, options.BaseDirectory);
                        if (manifestPath?.Exists != true)
                        {
                            try
                            {
                                await manifestService.GenerateManifestAsync(
                                    directory: options.BaseDirectory,
                                    packageName: null, // Will use defaults and prompt if not --yes
                                    publisherName: null, // Will use defaults and prompt if not --yes
                                    version: "1.0.0.0",
                                    description: "Windows Application",
                                    entryPoint: null, // Will use defaults and prompt if not --yes
                                    manifestTemplate: ManifestTemplates.Packaged, // Default to regular MSIX
                                    logoPath: null, // Will prompt if not --yes
                                    yes: options.UseDefaults,
                                    taskContext,
                                    cancellationToken: cancellationToken);

                                taskContext.AddDebugMessage($"{UiSymbols.Check} AppxManifest.xml generated → {manifestPath}");
                                return (0, "Manifest generation complete");
                            }
                            catch (Exception ex)
                            {
                                taskContext.AddDebugMessage($"{UiSymbols.Note} Failed to generate manifest: {ex.Message}");
                                // Don't fail the entire setup if manifest generation fails
                                return (0, "Manifest generation failed, but continuing setup");
                            }
                        }
                        else
                        {
                            taskContext.AddDebugMessage($"{UiSymbols.Check} AppxManifest.xml already exists, skipping generation");
                            return (0, "Manifest already exists");
                        }
                    }, cancellationToken);
                }

                // Step 7: Save configuration (for setup with SDK installation)
                if (!options.RequireExistingConfig && options.SdkInstallMode != SdkInstallMode.None && usedVersions != null)
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
                        return Task.FromResult((0, "Configuration saved"));
                    }, cancellationToken);

                    // Update .gitignore to exclude .winapp folder (unless --no-gitignore is specified)
                    if (!options.NoGitignore)
                    {
                        if (localWinappDir?.Parent != null)
                        {
                            await gitignoreService.UpdateGitignoreAsync(localWinappDir.Parent, taskContext, cancellationToken);
                        }
                    }
                }

                // Step 8: Generate development certificate (unless --no-cert is specified)
                if (!options.NoCert)
                {
                    await taskContext.AddSubTaskAsync("Generating development certificate", async (taskContext, cancellationToken) =>
                    {
                        var certPath = new FileInfo(Path.Combine(options.BaseDirectory.FullName, CertificateService.DefaultCertFileName));

                        await certificateService.GenerateDevCertificateWithInferenceAsync(
                            outputPath: certPath,
                            taskContext: taskContext,
                            explicitPublisher: null,
                            manifestPath: null,
                            password: "password",
                            validDays: 365,
                            skipIfExists: true,
                            updateGitignore: true,
                            install: false,
                            cancellationToken: cancellationToken);

                        return (0, "Development certificate setup complete");
                    }, cancellationToken);
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

                            directoryPackagesService.UpdatePackageVersions(options.ConfigDir, packageVersions, taskContext);
                            return Task.FromResult((0, "Directory.Packages.props update complete"));
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
                var successMessage = options.RequireExistingConfig ? "Restore completed successfully" : "Setup completed successfully";
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
                var operation = options.RequireExistingConfig ? "Restore" : "Init";
                taskContext.StatusError($"{operation} failed: {ex.Message}" + Environment.NewLine +
                                        $"{ex.StackTrace}");
                return (1, "Error!");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Package entry information from MSIX inventory
    /// </summary>
    public class MsixPackageEntry
    {
        public required string FileName { get; set; }
        public required string PackageIdentity { get; set; }
    }

    /// <summary>
    /// Parses the MSIX inventory file and returns package entries (shared implementation)
    /// </summary>
    /// <param name="msixDir">Directory containing the MSIX packages</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of package entries, or null if not found</returns>
    public static async Task<List<MsixPackageEntry>?> ParseMsixInventoryAsync(TaskContext taskContext, DirectoryInfo msixDir, CancellationToken cancellationToken)
    {
        var architecture = GetSystemArchitecture();

        taskContext.AddDebugMessage($"{UiSymbols.Note} Detected system architecture: {architecture}");

        // Look for MSIX packages for the current architecture
        var msixArchDir = Path.Combine(msixDir.FullName, $"win10-{architecture}");
        if (!Directory.Exists(msixArchDir))
        {
            taskContext.AddDebugMessage($"{UiSymbols.Note} No MSIX packages found for architecture {architecture}");
            taskContext.AddDebugMessage($"{UiSymbols.Note} Available directories: {string.Join(", ", msixDir.GetDirectories().Select(d => d.Name))}");
            return null;
        }

        // Read the MSIX inventory file
        var inventoryPath = Path.Combine(msixArchDir, "msix.inventory");
        if (!File.Exists(inventoryPath))
        {
            taskContext.AddDebugMessage($"{UiSymbols.Note} No msix.inventory file found in {msixArchDir}");
            return null;
        }

        var inventoryLines = await File.ReadAllLinesAsync(inventoryPath, cancellationToken);
        var packageEntries = inventoryLines
            .Where(line => !string.IsNullOrWhiteSpace(line) && line.Contains('='))
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .Select(parts => new MsixPackageEntry { FileName = parts[0].Trim(), PackageIdentity = parts[1].Trim() })
            .ToList();

        if (packageEntries.Count == 0)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Note} No valid package entries found in msix.inventory");
            return null;
        }

        taskContext.AddDebugMessage($"{UiSymbols.Package} Found {packageEntries.Count} MSIX packages in inventory");

        return packageEntries;
    }

    /// <summary>
    /// Installs Windows App SDK runtime MSIX packages for the current system architecture
    /// </summary>
    /// <param name="msixDir">Directory containing the MSIX packages</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task InstallWindowsAppRuntimeAsync(DirectoryInfo msixDir, TaskContext taskContext, CancellationToken cancellationToken)
    {
        var architecture = GetSystemArchitecture();

        // Get package entries from MSIX inventory
        var packageEntries = await ParseMsixInventoryAsync(taskContext, msixDir, cancellationToken);
        if (packageEntries == null || packageEntries.Count == 0)
        {
            return;
        }

        var msixArchDir = Path.Combine(msixDir.FullName, $"win10-{architecture}");

        // Build package data for PowerShell script
        var packageData = new List<string>();
        foreach (var entry in packageEntries)
        {
            var msixFilePath = Path.Combine(msixArchDir, entry.FileName);
            if (!File.Exists(msixFilePath))
            {
                taskContext.AddDebugMessage($"{UiSymbols.Note} MSIX file not found: {msixFilePath}");
                continue;
            }

            // Parse the PackageIdentity (format: Name_Version_Architecture_PublisherId)
            var identityParts = entry.PackageIdentity.Split('_');
            var packageName = identityParts[0];
            var newVersionString = identityParts.Length >= 2 ? identityParts[1] : "";

            packageData.Add($"@{{Path='{msixFilePath}';Identity='{entry.PackageIdentity}';Name='{packageName}';Version='{newVersionString}';FileName='{entry.FileName}'}}");
        }

        if (packageData.Count == 0)
        {
            return;
        }

        // Create compact PowerShell script with reusable function
        var script = $@"
function Test-PackageNeedsInstall($pkg) {{
    $exactMatch = Get-AppxPackage | Where-Object {{ $_.PackageFullName -eq $pkg.Identity }}
    if ($exactMatch) {{ return $false }}
    
    $existing = Get-AppxPackage -Name $pkg.Name -ErrorAction SilentlyContinue
    if (-not $existing) {{ return $true }}
    
    $shouldUpgrade = $false
    foreach ($p in $existing) {{ if ([version]$pkg.Version -gt [version]$p.Version) {{ $shouldUpgrade = $true; break }} }}
    return $shouldUpgrade
}}

$packages = @({string.Join(",", packageData)})
$toInstall = @()

foreach ($pkg in $packages) {{
    if (Test-PackageNeedsInstall $pkg) {{
        $toInstall += $pkg.Path
        Write-Output ""INSTALL|$($pkg.FileName)|Will install""
    }} else {{
        Write-Output ""SKIP|$($pkg.FileName)|Already installed or newer version exists""
    }}
}}

if ($toInstall.Count -gt 0) {{
    Write-Output ""INSTALLING|$($toInstall.Count) packages will be installed""
    foreach ($path in $toInstall) {{
        try {{
            Add-AppxPackage -Path $path -ForceApplicationShutdown -ErrorAction Stop
            Write-Output ""SUCCESS|$(Split-Path $path -Leaf)|Installation successful""
        }} catch {{
            Write-Output ""ERROR|$(Split-Path $path -Leaf)|$($_.Exception.Message)""
        }}
    }}
}} else {{
    Write-Output ""COMPLETE|No packages need to be installed""
}}";

        taskContext.AddDebugMessage($"{UiSymbols.Info} Checking and installing {packageEntries.Count} MSIX packages");

        // Execute the batch script
        var (exitCode, output) = await powerShellService.RunCommandAsync(script, taskContext, cancellationToken: cancellationToken);

        // Parse the output to provide user feedback
        var outputLines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrEmpty(line));

        var installedCount = 0;
        var errorCount = 0;

        foreach (var line in outputLines)
        {
            var parts = line.Split('|', 3);
            if (parts.Length < 2)
            {
                continue;
            }

            var action = parts[0];
            var fileName = parts[1];
            var message = parts.Length > 2 ? parts[2] : "";

            switch (action)
            {
                case "SKIP":
                    taskContext.AddDebugMessage($"{UiSymbols.Check} {fileName}: {message}");
                    break;

                case "INSTALL":
                    taskContext.AddDebugMessage($"{UiSymbols.Info} {fileName}: {message}");
                    break;

                case "INSTALLING":
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        taskContext.AddDebugMessage($"{UiSymbols.Info} {message}");
                    }
                    break;

                case "SUCCESS":
                    installedCount++;
                    taskContext.AddDebugMessage($"{UiSymbols.Check} {fileName}: {message}");
                    break;

                case "ERROR":
                    errorCount++;
                    taskContext.AddDebugMessage($"{UiSymbols.Note} {fileName}: {message}");
                    break;

                case "COMPLETE":
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        taskContext.AddDebugMessage($"{UiSymbols.Check} {message}");
                    }
                    break;
            }
        }

        // Provide summary feedback
        if (installedCount > 0)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Check} Installed {installedCount} MSIX packages");
        }
        if (errorCount > 0)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Note} {errorCount} packages failed to install");
        }
    }

    /// <summary>
    /// Gets the current system architecture string for package selection
    /// </summary>
    /// <returns>Architecture string (x64, arm64, x86)</returns>
    public static string GetSystemArchitecture()
    {
        var arch = RuntimeInformation.ProcessArchitecture;
        return arch switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64" // Default fallback
        };
    }

    /// <summary>
    /// Finds the MSIX directory for Windows App SDK runtime packages
    /// </summary>
    /// <param name="usedVersions">Optional dictionary of package versions to look for specific installed packages</param>
    /// <returns>The path to the MSIX directory, or null if not found</returns>
    public DirectoryInfo? FindWindowsAppSdkMsixDirectory(Dictionary<string, string>? usedVersions = null)
    {
        var globalWinappDir = winappDirectoryService.GetGlobalWinappDirectory();
        var pkgsDir = new DirectoryInfo(Path.Combine(globalWinappDir.FullName, "packages"));

        if (!pkgsDir.Exists)
        {
            return null;
        }

        // If we have specific versions from package installation, use those first
        if (usedVersions != null)
        {
            // First try Microsoft.WindowsAppSDK.Runtime package (WinAppSDK 1.8+)
            if (usedVersions.TryGetValue("Microsoft.WindowsAppSDK.Runtime", out var wasdkRuntimeVersion))
            {
                var msixDir = TryGetMsixDirectory(pkgsDir, $"Microsoft.WindowsAppSDK.Runtime.{wasdkRuntimeVersion}");
                if (msixDir != null)
                {
                    return msixDir;
                }
            }

            // Fallback: check if runtime is included in the main WindowsAppSDK package (for older versions)
            if (usedVersions.TryGetValue("Microsoft.WindowsAppSDK", out var wasdkVersion))
            {
                var msixDir = TryGetMsixDirectory(pkgsDir, $"Microsoft.WindowsAppSDK.{wasdkVersion}");
                if (msixDir != null)
                {
                    return msixDir;
                }
            }
        }

        // General scan approach: Look for Microsoft.WindowsAppSDK.Runtime packages first (WinAppSDK 1.8+)
        var runtimePackages = pkgsDir.GetDirectories("Microsoft.WindowsAppSDK.Runtime.*");
        foreach (var runtimePkg in runtimePackages.OrderByDescending(p => ExtractVersionFromPackageName(p.Name), new VersionStringComparer()))
        {
            var msixDir = TryGetMsixDirectoryFromPath(runtimePkg);
            if (msixDir != null)
            {
                return msixDir;
            }
        }

        // Fallback: check if runtime is included in the main WindowsAppSDK package (for older versions)
        var mainPackages = pkgsDir.GetDirectories("Microsoft.WindowsAppSDK.*")
            .Where(p => !p.Name.Contains("Runtime", StringComparison.OrdinalIgnoreCase));

        foreach (var mainPkg in mainPackages.OrderByDescending(p => ExtractVersionFromPackageName(p.Name), new VersionStringComparer()))
        {
            var msixDir = TryGetMsixDirectoryFromPath(mainPkg);
            if (msixDir != null)
            {
                return msixDir;
            }
        }

        return null;
    }

    /// <summary>
    /// Helper method to check if an MSIX directory exists for a given package directory name
    /// </summary>
    /// <param name="pkgsDir">The packages directory</param>
    /// <param name="packageDirName">The package directory name</param>
    /// <returns>The MSIX directory path if it exists, null otherwise</returns>
    private static DirectoryInfo? TryGetMsixDirectory(DirectoryInfo pkgsDir, string packageDirName)
    {
        var pkgDir = new DirectoryInfo(Path.Combine(pkgsDir.FullName, packageDirName));
        return TryGetMsixDirectoryFromPath(pkgDir);
    }

    /// <summary>
    /// Helper method to check if an MSIX directory exists for a given package path
    /// </summary>
    /// <param name="packagePath">The full path to the package directory</param>
    /// <returns>The MSIX directory path if it exists, null otherwise</returns>
    private static DirectoryInfo? TryGetMsixDirectoryFromPath(DirectoryInfo packagePath)
    {
        var msixDir = new DirectoryInfo(Path.Combine(packagePath.FullName, "tools", "MSIX"));
        return msixDir.Exists ? msixDir : null;
    }

    /// <summary>
    /// Extract version string from package folder name for sorting
    /// Handles prerelease tags like "-experimental1"
    /// </summary>
    /// <param name="packageFolderName">Package folder name like "Microsoft.WindowsAppSDK.Runtime.2.0.250930001-experimental1"</param>
    /// <returns>Version string for comparison (e.g., "2.0.250930001-experimental1")</returns>
    private static string ExtractVersionFromPackageName(string packageFolderName)
    {
        // Find the last occurrence of the package name prefix
        // For "Microsoft.WindowsAppSDK.Runtime.2.0.250930001-experimental1", we want "2.0.250930001-experimental1"

        var parts = packageFolderName.Split('.');
        if (parts.Length < 2)
        {
            return "0.0.0.0";
        }

        // Find where the version starts (first part that starts with a digit or contains a digit followed by a hyphen)
        var versionStartIndex = -1;
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0 && char.IsDigit(parts[i][0]))
            {
                versionStartIndex = i;
                break;
            }
        }

        if (versionStartIndex == -1)
        {
            return "0.0.0.0";
        }

        // Join all parts from the version start, preserving hyphens for prerelease tags
        return string.Join(".", parts.Skip(versionStartIndex));
    }

    /// <summary>
    /// Comparer for sorting version strings, including prerelease support
    /// </summary>
    private class VersionStringComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            if (x == null && y == null)
            {
                return 0;
            }
            if (x == null)
            {
                return -1;
            }
            if (y == null)
            {
                return 1;
            }

            // Use the same comparison logic as NugetService.CompareVersions
            return NugetService.CompareVersions(x, y);
        }
    }
}
