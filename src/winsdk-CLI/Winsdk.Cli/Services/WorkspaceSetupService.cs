// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using Winsdk.Cli.Helpers;
using Winsdk.Cli.Models;

namespace Winsdk.Cli.Services;

/// <summary>
/// Parameters for workspace setup operations
/// </summary>
internal class WorkspaceSetupOptions
{
    public required DirectoryInfo BaseDirectory { get; set; }
    public required DirectoryInfo ConfigDir { get; set; }
    public bool IncludeExperimental { get; set; }
    public bool IgnoreConfig { get; set; }
    public bool NoGitignore { get; set; }
    public bool AssumeYes { get; set; }
    public bool RequireExistingConfig { get; set; }
    public bool ForceLatestBuildTools { get; set; }
    public bool NoCert { get; set; }
    public bool ConfigOnly { get; set; }
}

/// <summary>
/// Shared service for setting up winsdk workspaces
/// </summary>
internal class WorkspaceSetupService(
    IConfigService configService,
    IWinsdkDirectoryService winsdkDirectoryService,
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
    ICurrentDirectoryProvider currentDirectoryProvider,
    ILogger<WorkspaceSetupService> logger) : IWorkspaceSetupService
{
    public async Task<int> SetupWorkspaceAsync(WorkspaceSetupOptions options, CancellationToken cancellationToken = default)
    {
        configService.ConfigPath = new FileInfo(Path.Combine(options.ConfigDir.FullName, "winsdk.yaml"));

        try
        {
            // Step 1: Handle configuration requirements
            if (options.RequireExistingConfig && !configService.Exists())
            {
                logger.LogError("winsdk.yaml not found in {ConfigDir}", options.ConfigDir);
                logger.LogError("Run 'winsdk setup' to initialize a new workspace or navigate to a directory with winsdk.yaml");
                return 1;
            }

            // Step 2: Load or prepare configuration
            WinsdkConfig config;
            bool hadExistingConfig = configService.Exists();
            
            if (hadExistingConfig)
            {
                config = configService.Load();
                
                if (config.Packages.Count == 0 && options.RequireExistingConfig)
                {
                    logger.LogInformation("{UISymbol} winsdk.yaml found but contains no packages. Nothing to restore.", UiSymbols.Note);
                    return 0;
                }
                
                var operation = options.RequireExistingConfig ? "Found" : "Found existing";
                logger.LogInformation("{UISymbol} {Operation} winsdk.yaml with {Count} packages", UiSymbols.Package, operation, config.Packages.Count);

                if (!options.RequireExistingConfig && config.Packages.Count > 0)
                {
                    logger.LogInformation("{UISymbol} Using pinned versions unless overridden.", UiSymbols.Note);
                }

                // For setup command: ask about overwriting existing config
                if (!options.RequireExistingConfig && !options.IgnoreConfig && config.Packages.Count > 0)
                {
                    var overwrite = options.AssumeYes || Program.PromptYesNo("winsdk.yaml exists with pinned versions. Overwrite with latest versions? [y/N]: ");
                    if (overwrite) 
                    {
                        options.IgnoreConfig = true;
                    }
                }
            }
            else
            {
                config = new WinsdkConfig();
                logger.LogInformation("{UISymbol} No winsdk.yaml found; will generate one after setup.", UiSymbols.New);
            }

            // Handle config-only mode: just create/validate config file and exit
            if (options.ConfigOnly)
            {
                if (hadExistingConfig)
                {
                    logger.LogInformation("{UISymbol} Existing configuration file found and validated → {ConfigPath}", UiSymbols.Check, configService.ConfigPath);
                    logger.LogInformation("{UISymbol} Configuration contains {Count} packages", UiSymbols.Package, config.Packages.Count);

                    if (config.Packages.Count > 0)
                    {
                        using (var _ = logger.BeginScope("{UISymbol} Configured packages", UiSymbols.Note))
                        {
                            foreach (var pkg in config.Packages)
                            {
                                logger.LogDebug("{PackageName} = {PackageVersion}", pkg.Name, pkg.Version);
                            }
                        }
                    }
                }
                else
                {
                    // Generate config with default package versions
                    logger.LogInformation("{UISymbol} Creating configuration file with default SDK packages...", UiSymbols.New);
                    
                    // Get latest package versions (respecting prerelease option)
                    var defaultVersions = new Dictionary<string, string>();
                    foreach (var packageName in NugetService.SDK_PACKAGES)
                    {
                        try
                        {
                            var version = await nugetService.GetLatestVersionAsync(
                                packageName, 
                                includePrerelease: options.IncludeExperimental,
                                cancellationToken: cancellationToken);
                            defaultVersions[packageName] = version;
                        }
                        catch (Exception ex)
                        {
                            logger.LogDebug("{UISymbol} Could not get version for {PackageName}: {Message}", UiSymbols.Note, packageName, ex.Message);
                        }
                    }
                    
                    var finalConfig = new WinsdkConfig();
                    foreach (var kvp in defaultVersions)
                    {
                        finalConfig.SetVersion(kvp.Key, kvp.Value);
                    }

                    configService.Save(finalConfig);
                    
                    logger.LogInformation("{UISymbol} Configuration file created → {ConfigPath}", UiSymbols.Save, configService.ConfigPath);
                    logger.LogInformation("{UISymbol} Added {Count} default SDK packages", UiSymbols.Package, finalConfig.Packages.Count);

                    using (var _ = logger.BeginScope("{UISymbol} Generated packages", UiSymbols.Note))
                    {
                        foreach (var pkg in finalConfig.Packages)
                        {
                            logger.LogDebug("{PackageName} = {PackageVersion}", pkg.Name, pkg.Version);
                        }
                    }
                    
                    if (options.IncludeExperimental)
                    {
                        logger.LogInformation("{UISymbol} Prerelease packages were included", UiSymbols.Wrench);
                    }
                }

                logger.LogInformation("{UISymbol} Configuration-only operation completed.", UiSymbols.Party);
                return 0;
            }

            // Step 3: Initialize workspace
            var globalWinsdkDir = winsdkDirectoryService.GetGlobalWinsdkDirectory();
            var localWinsdkDir = winsdkDirectoryService.GetLocalWinsdkDirectory(options.BaseDirectory);

            // Setup-specific startup messages
            if (!options.RequireExistingConfig)
            {
                logger.LogInformation("{UISymbol} using config → {ConfigPath}", UiSymbols.Rocket, configService.ConfigPath);
                logger.LogInformation("{UISymbol} winsdk init starting in {BaseDirectory}", UiSymbols.Rocket, options.BaseDirectory);
                logger.LogInformation("{UISymbol} Global packages → {GlobalWinsdkDir}", UiSymbols.Folder, globalWinsdkDir);
                logger.LogInformation("{UISymbol} Local workspace → {LocalWinsdkDir}", UiSymbols.Folder, localWinsdkDir);

                if (options.IncludeExperimental)
                {
                    logger.LogInformation("{UISymbol} Experimental/prerelease packages will be included", UiSymbols.Wrench);
                }
            }
            else
            {
                logger.LogInformation("{UISymbol} Global packages → {GlobalWinsdkDir}", UiSymbols.Folder, globalWinsdkDir);
                logger.LogInformation("{UISymbol} Local workspace → {LocalWinsdkDir}", UiSymbols.Folder, localWinsdkDir);
            }

            // First ensure basic workspace (for global packages)
            packageInstallationService.InitializeWorkspace(globalWinsdkDir);

            // Create all standard workspace directories for full setup/restore
            var pkgsDir = globalWinsdkDir.CreateSubdirectory("packages");
            var includeOut = localWinsdkDir.CreateSubdirectory("include");
            var libRoot = localWinsdkDir.CreateSubdirectory("lib");
            var binRoot = localWinsdkDir.CreateSubdirectory("bin");

            // Step 4: Install packages
            logger.LogInformation("{UISymbol} Installing SDK packages → {PkgsDir}", UiSymbols.Package, pkgsDir);

            Dictionary<string, string> usedVersions;
            if (options.RequireExistingConfig && hadExistingConfig && config.Packages.Count > 0)
            {
                // Restore: use packages from existing config
                var packageNames = config.Packages.Select(p => p.Name).ToArray();
                usedVersions = await packageInstallationService.InstallPackagesAsync(
                    globalWinsdkDir,
                    packageNames,
                    includeExperimental: options.IncludeExperimental,
                    ignoreConfig: false, // Use config versions for restore
                    cancellationToken: cancellationToken);
            }
            else
            {
                // Setup: install standard SDK packages
                usedVersions = await packageInstallationService.InstallPackagesAsync(
                    globalWinsdkDir,
                    NugetService.SDK_PACKAGES,
                    includeExperimental: options.IncludeExperimental,
                    ignoreConfig: options.IgnoreConfig,
                    cancellationToken: cancellationToken);
            }

            // Step 5: Run cppwinrt and set up projections
            var cppWinrtExe = cppWinrtService.FindCppWinrtExe(pkgsDir, usedVersions);
            if (cppWinrtExe is null)
            {
                logger.LogError("cppwinrt.exe not found in installed packages.");
                return 2;
            }

            logger.LogInformation("{UISymbol} Using cppwinrt tool → {CppWinrtExe}", UiSymbols.Tools, cppWinrtExe);

            // Copy headers, libs, runtimes
            logger.LogInformation("{UISymbol} Copying headers → {IncludeOut}", UiSymbols.Files, includeOut);
            packageLayoutService.CopyIncludesFromPackages(pkgsDir, includeOut);
            logger.LogInformation("{UISymbol} Headers ready → {IncludeOut}", UiSymbols.Check, includeOut);

            logger.LogInformation("{UISymbol} Copying import libs by arch → {LibRoot}", UiSymbols.Books, libRoot);
            packageLayoutService.CopyLibsAllArch(pkgsDir, libRoot);
            var libArchs = libRoot.Exists ? string.Join(", ", libRoot.EnumerateDirectories().Select(d => d.Name)) : "(none)";
            logger.LogInformation("{UISymbol} Import libs ready for archs: {LibArchs}", UiSymbols.Books, libArchs);

            logger.LogInformation("{UISymbol} Copying runtime binaries by arch → {BinRoot}", UiSymbols.Gear, binRoot);
            packageLayoutService.CopyRuntimesAllArch(pkgsDir, binRoot);
            var binArchs = binRoot.Exists ? string.Join(", ", binRoot.EnumerateDirectories().Select(d => d.Name)) : "(none)";
            logger.LogInformation("{UISymbol} Runtime binaries ready for archs: {BinArchs}", UiSymbols.Gear, binArchs);

            // Copy Windows App SDK license
            try
            {
                if (usedVersions.TryGetValue("Microsoft.WindowsAppSDK", out var wasdkVersion))
                {
                    var pkgDir = Path.Combine(pkgsDir.FullName, $"Microsoft.WindowsAppSDK.{wasdkVersion}");
                    var licenseSrc = Path.Combine(pkgDir, "license.txt");
                    if (File.Exists(licenseSrc))
                    {
                        var shareDir = Path.Combine(localWinsdkDir.FullName, "share", "Microsoft.WindowsAppSDK");
                        Directory.CreateDirectory(shareDir);
                        var licenseDst = Path.Combine(shareDir, "copyright");
                        File.Copy(licenseSrc, licenseDst, overwrite: true);
                        logger.LogInformation("{UISymbol} License copied → {LicenseDst}", UiSymbols.Check, licenseDst);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug("{UISymbol} Failed to copy license: {Message}", UiSymbols.Note, ex.Message);
            }

            // Collect winmd inputs and run cppwinrt
            logger.LogInformation("{UISymbol} Searching for .winmd metadata...", UiSymbols.Search);
            var winmds = packageLayoutService.FindWinmds(pkgsDir, usedVersions).ToList();
            logger.LogInformation("{UISymbol} Found {Count} .winmd", UiSymbols.Search, winmds.Count);
            if (winmds.Count == 0)
            {
                logger.LogError("No .winmd files found for C++/WinRT projection.");
                return 2;
            }

            // Run cppwinrt
            logger.LogInformation("{UISymbol} Generating C++/WinRT projections...", UiSymbols.Gear);
            await cppWinrtService.RunWithRspAsync(cppWinrtExe, winmds, includeOut, localWinsdkDir, cancellationToken: cancellationToken);
            logger.LogInformation("{UISymbol} C++/WinRT headers generated → {IncludeOut}", UiSymbols.Check, includeOut);

            // Step 6: Handle BuildTools
            var buildToolsPinned = config.GetVersion(BuildToolsService.BUILD_TOOLS_PACKAGE);
            var forceLatestBuildTools = options.ForceLatestBuildTools || string.IsNullOrWhiteSpace(buildToolsPinned);

            if (forceLatestBuildTools && options.RequireExistingConfig)
            {
                logger.LogInformation("{UISymbol} BuildTools not pinned, installing latest in cache...", UiSymbols.Wrench);
            }
            else if (!string.IsNullOrWhiteSpace(buildToolsPinned))
            {
                logger.LogInformation("{UISymbol} Ensuring BuildTools (pinned version {BuildToolsPinned}) in cache...", UiSymbols.Wrench, buildToolsPinned);
            }

            var buildToolsPath = await buildToolsService.EnsureBuildToolsAsync(
                forceLatest: forceLatestBuildTools,
                cancellationToken: cancellationToken);

            if (buildToolsPath != null)
            {
                logger.LogInformation("{UISymbol} BuildTools ready → {BuildToolsPath}", UiSymbols.Check, buildToolsPath);
            }

            // Step 6.5: Enable Developer Mode (for setup only)
            if (!options.RequireExistingConfig)
            {
                try
                {
                    logger.LogInformation("{UISymbol} Checking Developer Mode...", UiSymbols.Wrench);

                    var devModeResult = devModeService.EnsureWin11DevMode();
                    
                    if (devModeResult != 0 && devModeResult != 3010)
                    {
                        logger.LogInformation("{UISymbol} Developer Mode setup returned exit code {DevModeResult}", UiSymbols.Note, devModeResult);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug("{UISymbol} Developer Mode setup failed: {Message}", UiSymbols.Note, ex.Message);
                    // Don't fail the entire setup if developer mode setup fails
                }
            }

            // Install Windows App Runtime (if not already installed)
            try
            {
                var msixDir = FindWindowsAppSdkMsixDirectory(usedVersions);

                if (msixDir != null)
                {
                    logger.LogInformation("{UISymbol} Installing Windows App Runtime...", UiSymbols.Wrench);

                    // Install Windows App SDK runtime packages
                    await InstallWindowsAppRuntimeAsync(msixDir, cancellationToken);

                    logger.LogInformation("{UISymbol} Windows App Runtime installation complete", UiSymbols.Check);
                }
                else
                {
                    logger.LogDebug("{UISymbol} MSIX directory not found, skipping Windows App Runtime installation", UiSymbols.Note);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug("{UISymbol} Failed to install Windows App Runtime: {Message}", UiSymbols.Note, ex.Message);
            }

            // Step 6.6: Generate AppxManifest.xml (for setup only)
            if (!options.RequireExistingConfig)
            {
                // Check if manifest already exists
                var manifestPath = MsixService.FindProjectManifest(currentDirectoryProvider, options.BaseDirectory);
                if (manifestPath?.Exists != true)
                {
                    try
                    {
                        logger.LogInformation("{UISymbol} Generating AppxManifest.xml...", UiSymbols.New);

                        await manifestService.GenerateManifestAsync(
                            directory: options.BaseDirectory,
                            packageName: null, // Will use defaults and prompt if not --yes
                            publisherName: null, // Will use defaults and prompt if not --yes
                            version: "1.0.0.0",
                            description: "Windows Application",
                            entryPoint: null, // Will use defaults and prompt if not --yes
                            manifestTemplate: ManifestTemplates.Packaged, // Default to regular MSIX
                            logoPath: null, // Will prompt if not --yes
                            yes: options.AssumeYes,
                            cancellationToken: cancellationToken);

                        logger.LogInformation("{UISymbol} AppxManifest.xml generated → {ManifestPath}", UiSymbols.Check, manifestPath);
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug("{UISymbol} Failed to generate manifest: {Message}", UiSymbols.Note, ex.Message);
                        // Don't fail the entire setup if manifest generation fails
                    }
                }
                else
                {
                    logger.LogInformation("{UISymbol} AppxManifest.xml already exists, skipping generation", UiSymbols.Check);
                }
            }

            // Step 7: Save configuration (for setup) or we're done (for restore)
            if (!options.RequireExistingConfig)
            {
                // Setup: Save winsdk.yaml with used versions
                var finalConfig = new WinsdkConfig();
                // only from SDK_PACKAGES
                var versionsToSave = usedVersions
                    .Where(kvp => NugetService.SDK_PACKAGES.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                foreach (var kvp in versionsToSave)
                {
                    finalConfig.SetVersion(kvp.Key, kvp.Value);
                }
                configService.Save(finalConfig);
                logger.LogInformation("{UISymbol} Wrote config → {ConfigPath}", UiSymbols.Save, configService.ConfigPath);

                // Update .gitignore to exclude .winsdk folder (unless --no-gitignore is specified)
                if (!options.NoGitignore)
                {
                    if (localWinsdkDir.Parent != null)
                    {
                        gitignoreService.UpdateGitignore(localWinsdkDir.Parent);
                    }
                }

                // Step 8: Generate development certificate (unless --no-cert is specified)
                if (!options.NoCert)
                {
                    var certPath = new FileInfo(Path.Combine(options.BaseDirectory.FullName, CertificateService.DefaultCertFileName));
                    
                    await certificateService.GenerateDevCertificateWithInferenceAsync(
                        outputPath: certPath,
                        explicitPublisher: null,
                        manifestPath: null,
                        password: "password",
                        validDays: 365,
                        skipIfExists: true,
                        updateGitignore: true,
                        install: false,
                        cancellationToken: cancellationToken);
                }

                logger.LogInformation("{UISymbol} winsdk init completed.", UiSymbols.Party);
            }
            else
            {
                // Restore: We're done
                logger.LogInformation("{UISymbol} Restore completed successfully!", UiSymbols.Party);
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("{UISymbol} Operation cancelled", UiSymbols.Note);
            return 1;
        }
        catch (Exception ex)
        {
            var operation = options.RequireExistingConfig ? "Restore" : "Setup";
            logger.LogError("{Operation} failed: {Message}", operation, ex.Message);
            logger.LogDebug("{StackTrace}", ex.StackTrace);
            return 1;
        }
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
    /// <param name="logger">Logger instance for output</param>
    /// <param name="msixDir">Directory containing the MSIX packages</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of package entries, or null if not found</returns>
    public static async Task<List<MsixPackageEntry>?> ParseMsixInventoryAsync(ILogger logger, DirectoryInfo msixDir, CancellationToken cancellationToken)
    {
        var architecture = GetSystemArchitecture();
        
        logger.LogDebug("{UISymbol} Detected system architecture: {Architecture}", UiSymbols.Note, architecture);

        // Look for MSIX packages for the current architecture
        var msixArchDir = Path.Combine(msixDir.FullName, $"win10-{architecture}");
        if (!Directory.Exists(msixArchDir))
        {
            logger.LogDebug("{UISymbol} No MSIX packages found for architecture {Architecture}", UiSymbols.Note, architecture);
            logger.LogDebug("{UISymbol} Available directories: {Directories}", UiSymbols.Note, string.Join(", ", msixDir.GetDirectories().Select(d => d.Name)));
            return null;
        }

        // Read the MSIX inventory file
        var inventoryPath = Path.Combine(msixArchDir, "msix.inventory");
        if (!File.Exists(inventoryPath))
        {
            logger.LogDebug("{UISymbol} No msix.inventory file found in {MsixArchDir}", UiSymbols.Note, msixArchDir);
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
            logger.LogDebug("{UISymbol} No valid package entries found in msix.inventory", UiSymbols.Note);
            return null;
        }

        logger.LogDebug("{UISymbol} Found {Count} MSIX packages in inventory", UiSymbols.Package, packageEntries.Count);

        return packageEntries;
    }

    /// <summary>
    /// Installs Windows App SDK runtime MSIX packages for the current system architecture
    /// </summary>
    /// <param name="msixDir">Directory containing the MSIX packages</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task InstallWindowsAppRuntimeAsync(DirectoryInfo msixDir, CancellationToken cancellationToken)
    {
        var architecture = GetSystemArchitecture();

        // Get package entries from MSIX inventory
        var packageEntries = await ParseMsixInventoryAsync(logger, msixDir, cancellationToken);
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
                logger.LogDebug("{UISymbol} MSIX file not found: {MsixFilePath}", UiSymbols.Note, msixFilePath);
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

        logger.LogDebug("{UISymbol} Checking and installing {Count} MSIX packages...", UiSymbols.Gear, packageEntries.Count);

        // Execute the batch script
        var (exitCode, output) = await powerShellService.RunCommandAsync(script, cancellationToken: cancellationToken);

        // Parse the output to provide user feedback
        var outputLines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrEmpty(line));

        var installedCount = 0;
        var skippedCount = 0;
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
                    skippedCount++;
                    logger.LogDebug("{UISymbol} {FileName}: {Message}", UiSymbols.Check, fileName, message);
                    break;

                case "INSTALL":
                    logger.LogDebug("{UISymbol} {FileName}: {Message}", UiSymbols.Gear, fileName, message);
                    break;

                case "INSTALLING":
                    logger.LogDebug("{UISymbol} {Message}", UiSymbols.Gear, message);
                    break;

                case "SUCCESS":
                    installedCount++;
                    logger.LogDebug("{UISymbol} {FileName}: {Message}", UiSymbols.Check, fileName, message);
                    break;

                case "ERROR":
                    errorCount++;
                    logger.LogDebug("{UISymbol} {FileName}: {Message}", UiSymbols.Note, fileName, message);
                    break;

                case "COMPLETE":
                    logger.LogDebug("{UISymbol} {Message}", UiSymbols.Check, message);
                    break;
            }
        }

        // Provide summary feedback
        if (installedCount > 0)
        {
            logger.LogInformation("{UISymbol} Installed {Count} MSIX packages", UiSymbols.Check, installedCount);
        }
        if (errorCount > 0)
        {
            logger.LogInformation("{UISymbol} {Count} packages failed to install", UiSymbols.Note, errorCount);
        }

        if (exitCode != 0)
        {
            logger.LogDebug("{UISymbol} PowerShell batch operation returned exit code {ExitCode}", UiSymbols.Note, exitCode);
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
        var globalWinsdkDir = winsdkDirectoryService.GetGlobalWinsdkDirectory();
        var pkgsDir = new DirectoryInfo(Path.Combine(globalWinsdkDir.FullName, "packages"));
        
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
        foreach (var runtimePkg in runtimePackages.OrderByDescending(p => p))
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
        
        foreach (var mainPkg in mainPackages.OrderByDescending(p => p))
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
}
