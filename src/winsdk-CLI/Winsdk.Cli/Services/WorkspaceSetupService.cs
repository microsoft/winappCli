using System.Runtime.InteropServices;

namespace Winsdk.Cli.Services;

/// <summary>
/// Parameters for workspace setup operations
/// </summary>
internal class WorkspaceSetupOptions
{
    public required string BaseDirectory { get; set; }
    public required string ConfigDir { get; set; }
    public bool Quiet { get; set; }
    public bool Verbose { get; set; }
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
internal class WorkspaceSetupService
{
    public WorkspaceSetupService()
    {
    }

    public async Task<int> SetupWorkspaceAsync(WorkspaceSetupOptions options, CancellationToken cancellationToken = default)
    {
        try
        {
            var configService = new ConfigService(options.ConfigDir);
            var buildToolsService = new BuildToolsService(configService);
            var packageService = new PackageInstallationService(configService);
            var cppwinrtService = new CppWinrtService();
            var layoutService = new PackageLayoutService();

            // Step 1: Handle configuration requirements
            if (options.RequireExistingConfig && !configService.Exists())
            {
                Console.Error.WriteLine($"winsdk.yaml not found in {options.ConfigDir}");
                Console.Error.WriteLine($"Run 'winsdk setup' to initialize a new workspace or navigate to a directory with winsdk.yaml");
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
                    if (!options.Quiet)
                    {
                        Console.WriteLine($"{UiSymbols.Note} winsdk.yaml found but contains no packages. Nothing to restore.");
                    }
                    return 0;
                }
                
                if (!options.Quiet)
                {
                    var operation = options.RequireExistingConfig ? "Found" : "Found existing";
                    Console.WriteLine($"{UiSymbols.Package} {operation} winsdk.yaml with {config.Packages.Count} packages");
                    
                    if (!options.RequireExistingConfig && config.Packages.Count > 0)
                    {
                        Console.WriteLine($"{UiSymbols.Note} Using pinned versions unless overridden.");
                    }
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
                if (!options.Quiet)
                {
                    Console.WriteLine($"{UiSymbols.New} No winsdk.yaml found; will generate one after setup.");
                }
            }

            // Handle config-only mode: just create/validate config file and exit
            if (options.ConfigOnly)
            {
                if (hadExistingConfig)
                {
                    if (!options.Quiet)
                    {
                        Console.WriteLine($"{UiSymbols.Check} Existing configuration file found and validated → {configService.ConfigPath}");
                        Console.WriteLine($"{UiSymbols.Package} Configuration contains {config.Packages.Count} packages");
                        
                        if (options.Verbose && config.Packages.Count > 0)
                        {
                            Console.WriteLine($"{UiSymbols.Note} Configured packages:");
                            foreach (var pkg in config.Packages)
                            {
                                Console.WriteLine($"  • {pkg.Name} = {pkg.Version}");
                            }
                        }
                    }
                }
                else
                {
                    // Generate config with default package versions
                    var nugetService = new NugetService();
                    
                    if (!options.Quiet)
                    {
                        Console.WriteLine($"{UiSymbols.New} Creating configuration file with default SDK packages...");
                    }
                    
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
                            if (options.Verbose)
                            {
                                Console.WriteLine($"{UiSymbols.Note} Could not get version for {packageName}: {ex.Message}");
                            }
                        }
                    }
                    
                    var finalConfig = new WinsdkConfig();
                    foreach (var kvp in defaultVersions)
                    {
                        finalConfig.SetVersion(kvp.Key, kvp.Value);
                    }
                    
                    configService.Save(finalConfig);
                    
                    if (!options.Quiet)
                    {
                        Console.WriteLine($"{UiSymbols.Save} Configuration file created → {configService.ConfigPath}");
                        Console.WriteLine($"{UiSymbols.Package} Added {finalConfig.Packages.Count} default SDK packages");
                        
                        if (options.Verbose)
                        {
                            Console.WriteLine($"{UiSymbols.Note} Generated packages:");
                            foreach (var pkg in finalConfig.Packages)
                            {
                                Console.WriteLine($"  • {pkg.Name} = {pkg.Version}");
                            }
                        }
                        
                        if (options.IncludeExperimental)
                        {
                            Console.WriteLine($"{UiSymbols.Wrench} Prerelease packages were included");
                        }
                    }
                }
                
                Console.WriteLine($"{UiSymbols.Party} Configuration-only operation completed.");
                return 0;
            }

            // Step 3: Initialize workspace
            var globalWinsdkDir = BuildToolsService.GetGlobalWinsdkDirectory();
            var localWinsdkDir = buildToolsService.GetLocalWinsdkDirectory(options.BaseDirectory);
            
            // Setup-specific startup messages
            if (!options.RequireExistingConfig && !options.Quiet)
            {
                Console.WriteLine($"{UiSymbols.Rocket} using config → {configService.ConfigPath}");
                Console.WriteLine($"{UiSymbols.Rocket} winsdk init starting in {options.BaseDirectory}");
                Console.WriteLine($"{UiSymbols.Folder} Global packages → {globalWinsdkDir}");
                Console.WriteLine($"{UiSymbols.Folder} Local workspace → {localWinsdkDir}");
                
                if (options.IncludeExperimental)
                {
                    Console.WriteLine($"{UiSymbols.Wrench} Experimental/prerelease packages will be included");
                }
            }
            else if (!options.Quiet)
            {
                Console.WriteLine($"{UiSymbols.Folder} Global packages → {globalWinsdkDir}");
                Console.WriteLine($"{UiSymbols.Folder} Local workspace → {localWinsdkDir}");
            }

            // First ensure basic workspace (for global packages)
            packageService.InitializeWorkspace(globalWinsdkDir);

            if (!options.Quiet)
            {
                if (!options.RequireExistingConfig)
                {
                    Console.WriteLine($"{UiSymbols.Rocket} using config → {configService.ConfigPath}");
                    Console.WriteLine($"{UiSymbols.Rocket} winsdk init starting in {options.BaseDirectory}");
                }
                Console.WriteLine($"{UiSymbols.Folder} Global packages → {globalWinsdkDir}");
                Console.WriteLine($"{UiSymbols.Folder} Local workspace → {localWinsdkDir}");

                if (options.IncludeExperimental)
                {
                    Console.WriteLine($"{UiSymbols.Wrench} Experimental/prerelease packages will be included");
                }
            }

            // Create all standard workspace directories for full setup/restore
            var pkgsDir = Path.Combine(globalWinsdkDir, "packages");
            var includeOut = Path.Combine(localWinsdkDir, "include");
            var libOut = Path.Combine(localWinsdkDir, "lib");
            var binOut = Path.Combine(localWinsdkDir, "bin");

            Directory.CreateDirectory(includeOut);
            Directory.CreateDirectory(libOut);
            Directory.CreateDirectory(binOut);

            // Step 4: Install packages
            if (!options.Quiet)
            {
                Console.WriteLine($"{UiSymbols.Package} Installing SDK packages → {pkgsDir}");
            }

            Dictionary<string, string> usedVersions;
            if (options.RequireExistingConfig && hadExistingConfig && config.Packages.Count > 0)
            {
                // Restore: use packages from existing config
                var packageNames = config.Packages.Select(p => p.Name).ToArray();
                usedVersions = await packageService.InstallPackagesAsync(
                    globalWinsdkDir,
                    packageNames,
                    includeExperimental: options.IncludeExperimental,
                    ignoreConfig: false, // Use config versions for restore
                    quiet: options.Quiet,
                    cancellationToken: cancellationToken);
            }
            else
            {
                // Setup: install standard SDK packages
                usedVersions = await packageService.InstallPackagesAsync(
                    globalWinsdkDir,
                    NugetService.SDK_PACKAGES,
                    includeExperimental: options.IncludeExperimental,
                    ignoreConfig: options.IgnoreConfig,
                    quiet: options.Quiet,
                    cancellationToken: cancellationToken);
            }

            // Step 5: Run cppwinrt and set up projections
            var cppWinrtExe = cppwinrtService.FindCppWinrtExe(pkgsDir, usedVersions);
            if (cppWinrtExe is null)
            {
                Console.Error.WriteLine("cppwinrt.exe not found in installed packages.");
                return 2;
            }

            if (!options.Quiet)
            {
                Console.WriteLine($"{UiSymbols.Tools} Using cppwinrt tool → {cppWinrtExe}");
            }

            // Copy headers, libs, runtimes
            if (!options.Quiet)
            {
                Console.WriteLine($"{UiSymbols.Files} Copying headers → {includeOut}");
            }
            layoutService.CopyIncludesFromPackages(pkgsDir, includeOut);
            Console.WriteLine($"{UiSymbols.Check} Headers ready → {includeOut}");

            var libRoot = Path.Combine(localWinsdkDir, "lib");
            if (!options.Quiet)
            {
                Console.WriteLine($"{UiSymbols.Books} Copying import libs by arch → {libRoot}");
            }
            layoutService.CopyLibsAllArch(pkgsDir, libRoot);
            var libArchs = Directory.Exists(libRoot) ? string.Join(", ", Directory.EnumerateDirectories(libRoot).Select(Path.GetFileName)) : "(none)";
            Console.WriteLine($"{UiSymbols.Books} Import libs ready for archs: {libArchs}");

            var binRoot = Path.Combine(localWinsdkDir, "bin");
            if (!options.Quiet)
            {
                Console.WriteLine($"{UiSymbols.Gear} Copying runtime binaries by arch → {binRoot}");
            }
            layoutService.CopyRuntimesAllArch(pkgsDir, binRoot);
            var binArchs = Directory.Exists(binRoot) ? string.Join(", ", Directory.EnumerateDirectories(binRoot).Select(Path.GetFileName)) : "(none)";
            Console.WriteLine($"{UiSymbols.Gear} Runtime binaries ready for archs: {binArchs}");

            // Copy Windows App SDK license
            try
            {
                if (usedVersions.TryGetValue("Microsoft.WindowsAppSDK", out var wasdkVersion))
                {
                    var pkgDir = Path.Combine(pkgsDir, $"Microsoft.WindowsAppSDK.{wasdkVersion}");
                    var licenseSrc = Path.Combine(pkgDir, "license.txt");
                    if (File.Exists(licenseSrc))
                    {
                        var shareDir = Path.Combine(localWinsdkDir, "share", "Microsoft.WindowsAppSDK");
                        Directory.CreateDirectory(shareDir);
                        var licenseDst = Path.Combine(shareDir, "copyright");
                        File.Copy(licenseSrc, licenseDst, overwrite: true);
                        Console.WriteLine($"{UiSymbols.Check} License copied → {licenseDst}");
                    }
                }
            }
            catch (Exception ex)
            {
                if (options.Verbose)
                {
                    Console.WriteLine($"{UiSymbols.Note} Failed to copy license: {ex.Message}");
                }
            }

            // Collect winmd inputs and run cppwinrt
            if (!options.Quiet)
            {
                Console.WriteLine($"{UiSymbols.Search} Searching for .winmd metadata...");
            }
            var winmds = layoutService.FindWinmds(pkgsDir, usedVersions).ToList();
            if (!options.Quiet)
            {
                Console.WriteLine($"{UiSymbols.Search} Found {winmds.Count} .winmd");
            }
            if (winmds.Count == 0)
            {
                Console.Error.WriteLine("No .winmd files found for C++/WinRT projection.");
                return 2;
            }

            // Run cppwinrt
            if (!options.Quiet)
            {
                Console.WriteLine($"{UiSymbols.Gear} Generating C++/WinRT projections...");
            }
            await cppwinrtService.RunWithRspAsync(cppWinrtExe, winmds, includeOut, localWinsdkDir, verbose: !options.Quiet, cancellationToken: cancellationToken);
            Console.WriteLine($"{UiSymbols.Check} C++/WinRT headers generated → {includeOut}");

            // Step 6: Handle BuildTools
            var buildToolsPinned = config.GetVersion(BuildToolsService.BUILD_TOOLS_PACKAGE);
            var forceLatestBuildTools = options.ForceLatestBuildTools || string.IsNullOrWhiteSpace(buildToolsPinned);

            if (!options.Quiet)
            {
                if (forceLatestBuildTools && options.RequireExistingConfig)
                {
                    Console.WriteLine($"{UiSymbols.Wrench} BuildTools not pinned, installing latest in cache...");
                }
                else if (!string.IsNullOrWhiteSpace(buildToolsPinned))
                {
                    Console.WriteLine($"{UiSymbols.Wrench} Ensuring BuildTools (pinned version {buildToolsPinned}) in cache...");
                }
            }

            var buildToolsPath = await buildToolsService.EnsureBuildToolsAsync(
                quiet: options.Quiet,
                forceLatest: forceLatestBuildTools,
                cancellationToken: cancellationToken);

            if (buildToolsPath != null && !options.Quiet)
            {
                Console.WriteLine($"{UiSymbols.Check} BuildTools ready → {buildToolsPath}");
            }

            // Step 6.5: Enable Developer Mode (for setup only)
            if (!options.RequireExistingConfig)
            {
                try
                {
                    if (!options.Quiet)
                    {
                        Console.WriteLine($"{UiSymbols.Wrench} Checking Developer Mode...");
                    }

                    var devModeService = new DevModeService();
                    var devModeResult = devModeService.EnsureWin11DevMode();
                    
                    if (devModeResult != 0 && devModeResult != 3010 && !options.Quiet)
                    {
                        Console.WriteLine($"{UiSymbols.Note} Developer Mode setup returned exit code {devModeResult}");
                    }
                }
                catch (Exception ex)
                {
                    if (options.Verbose)
                    {
                        Console.WriteLine($"{UiSymbols.Note} Developer Mode setup failed: {ex.Message}");
                    }
                    // Don't fail the entire setup if developer mode setup fails
                }
            }

            // Install Windows App Runtime (if not already installed)
            try
            {
                var msixDir = FindWindowsAppSdkMsixDirectory(usedVersions);

                if (msixDir != null)
                {
                    if (!options.Quiet)
                    {
                        Console.WriteLine($"{UiSymbols.Wrench} Installing Windows App Runtime...");
                    }

                    // Install Windows App SDK runtime packages
                    await InstallWindowsAppRuntimeAsync(msixDir, options.Verbose, cancellationToken);

                    if (!options.Quiet)
                    {
                        Console.WriteLine($"{UiSymbols.Check} Windows App Runtime installation complete");
                    }
                }
                else if (options.Verbose)
                {
                    Console.WriteLine($"{UiSymbols.Note} MSIX directory not found, skipping Windows App Runtime installation");
                }
            }
            catch (Exception ex)
            {
                if (options.Verbose)
                {
                    Console.WriteLine($"{UiSymbols.Note} Failed to install Windows App Runtime: {ex.Message}");
                }
            }

            // Step 6.6: Generate AppxManifest.xml (for setup only)
            if (!options.RequireExistingConfig)
            {
                // Check if manifest already exists
                var manifestPath = MsixService.FindProjectManifest(options.BaseDirectory);
                if (!File.Exists(manifestPath))
                {
                    try
                    {
                        if (!options.Quiet)
                        {
                            Console.WriteLine($"{UiSymbols.New} Generating AppxManifest.xml...");
                        }

                        var manifestService = new ManifestService();
                        await manifestService.GenerateManifestAsync(
                            directory: options.BaseDirectory,
                            packageName: null, // Will use defaults and prompt if not --yes
                            publisherName: null, // Will use defaults and prompt if not --yes
                            version: "1.0.0.0",
                            description: "Windows Application",
                            executable: null, // Will use defaults and prompt if not --yes
                            sparse: false, // Default to regular MSIX
                            logoPath: null, // Will prompt if not --yes
                            yes: options.AssumeYes,
                            verbose: options.Verbose,
                            cancellationToken: cancellationToken);

                        if (!options.Quiet)
                        {
                            Console.WriteLine($"{UiSymbols.Check} AppxManifest.xml generated → {manifestPath}");
                        }
                    }
                    catch (Exception ex)
                    {
                        if (options.Verbose)
                        {
                            Console.WriteLine($"{UiSymbols.Note} Failed to generate manifest: {ex.Message}");
                        }
                        // Don't fail the entire setup if manifest generation fails
                    }
                }
                else if (!options.Quiet)
                {
                    Console.WriteLine($"{UiSymbols.Check} AppxManifest.xml already exists, skipping generation");
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
                Console.WriteLine($"{UiSymbols.Save} Wrote config → {configService.ConfigPath}");

                // Update .gitignore to exclude .winsdk folder (unless --no-gitignore is specified)
                if (!options.NoGitignore)
                {
                    var path = new DirectoryInfo(localWinsdkDir);
                    if (path.Parent != null)
                    {
                        GitignoreService.UpdateGitignore(path.Parent.FullName, verbose: !options.Quiet);
                    }
                }

                // Step 8: Generate development certificate (unless --no-cert is specified)
                if (!options.NoCert)
                {
                    var certificateServices = new CertificateServices(buildToolsService);
                    var certPath = Path.Combine(options.BaseDirectory, CertificateServices.DefaultCertFileName);
                    
                    await certificateServices.GenerateDevCertificateWithInferenceAsync(
                        outputPath: certPath,
                        explicitPublisher: null,
                        manifestPath: null,
                        password: "password",
                        validDays: 365,
                        skipIfExists: true,
                        updateGitignore: true,
                        install: false,
                        quiet: options.Quiet,
                        verbose: options.Verbose,
                        cancellationToken: cancellationToken);
                }

                Console.WriteLine($"{UiSymbols.Party} winsdk init completed.");
            }
            else
            {
                // Restore: We're done
                Console.WriteLine($"{UiSymbols.Party} Restore completed successfully!");
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"{UiSymbols.Note} Operation cancelled");
            return 1;
        }
        catch (Exception ex)
        {
            var operation = options.RequireExistingConfig ? "Restore" : "Setup";
            Console.Error.WriteLine($"{operation} failed: {ex.Message}");
            if (options.Verbose)
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
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
    /// <param name="msixDir">Directory containing the MSIX packages</param>
    /// <param name="verbose">Enable verbose logging</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of package entries, or null if not found</returns>
    public static async Task<List<MsixPackageEntry>?> ParseMsixInventoryAsync(string msixDir, bool verbose, CancellationToken cancellationToken)
    {
        var architecture = GetSystemArchitecture();
        
        if (verbose)
        {
            Console.WriteLine($"{UiSymbols.Note} Detected system architecture: {architecture}");
        }

        // Look for MSIX packages for the current architecture
        var msixArchDir = Path.Combine(msixDir, $"win10-{architecture}");
        if (!Directory.Exists(msixArchDir))
        {
            if (verbose)
            {
                Console.WriteLine($"{UiSymbols.Note} No MSIX packages found for architecture {architecture}");
                Console.WriteLine($"{UiSymbols.Note} Available directories: {string.Join(", ", Directory.GetDirectories(msixDir).Select(Path.GetFileName))}");
            }
            return null;
        }

        // Read the MSIX inventory file
        var inventoryPath = Path.Combine(msixArchDir, "msix.inventory");
        if (!File.Exists(inventoryPath))
        {
            if (verbose)
            {
                Console.WriteLine($"{UiSymbols.Note} No msix.inventory file found in {msixArchDir}");
            }
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
            if (verbose)
            {
                Console.WriteLine($"{UiSymbols.Note} No valid package entries found in msix.inventory");
            }
            return null;
        }

        if (verbose)
        {
            Console.WriteLine($"{UiSymbols.Package} Found {packageEntries.Count} MSIX packages in inventory");
        }

        return packageEntries;
    }

    /// <summary>
    /// Installs Windows App SDK runtime MSIX packages for the current system architecture
    /// </summary>
    /// <param name="msixDir">Directory containing the MSIX packages</param>
    /// <param name="verbose">Enable verbose logging</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task InstallWindowsAppRuntimeAsync(string msixDir, bool verbose, CancellationToken cancellationToken)
    {
        var powerShellService = new PowerShellService();
        var architecture = GetSystemArchitecture();

        // Get package entries from MSIX inventory
        var packageEntries = await ParseMsixInventoryAsync(msixDir, verbose, cancellationToken);
        if (packageEntries == null || packageEntries.Count == 0)
        {
            return;
        }

        var msixArchDir = Path.Combine(msixDir, $"win10-{architecture}");

        // Build package data for PowerShell script
        var packageData = new List<string>();
        foreach (var entry in packageEntries)
        {
            var msixFilePath = Path.Combine(msixArchDir, entry.FileName);
            if (!File.Exists(msixFilePath))
            {
                if (verbose)
                {
                    Console.WriteLine($"{UiSymbols.Note} MSIX file not found: {msixFilePath}");
                }
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

        if (verbose)
        {
            Console.WriteLine($"{UiSymbols.Gear} Checking and installing {packageEntries.Count} MSIX packages...");
        }

        // Execute the batch script
        var (exitCode, output) = await powerShellService.RunCommandAsync(script, verbose: false, cancellationToken: cancellationToken);

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
            if (parts.Length < 2) continue;

            var action = parts[0];
            var fileName = parts[1];
            var message = parts.Length > 2 ? parts[2] : "";

            switch (action)
            {
                case "SKIP":
                    skippedCount++;
                    if (verbose)
                    {
                        Console.WriteLine($"{UiSymbols.Check} {fileName}: {message}");
                    }
                    break;

                case "INSTALL":
                    if (verbose)
                    {
                        Console.WriteLine($"{UiSymbols.Gear} {fileName}: {message}");
                    }
                    break;

                case "INSTALLING":
                    if (verbose)
                    {
                        Console.WriteLine($"{UiSymbols.Gear} {message}");
                    }
                    break;

                case "SUCCESS":
                    installedCount++;
                    if (verbose)
                    {
                        Console.WriteLine($"{UiSymbols.Check} {fileName}: {message}");
                    }
                    break;

                case "ERROR":
                    errorCount++;
                    if (verbose)
                    {
                        Console.WriteLine($"{UiSymbols.Note} {fileName}: {message}");
                    }
                    break;

                case "COMPLETE":
                    if (verbose)
                    {
                        Console.WriteLine($"{UiSymbols.Check} {message}");
                    }
                    break;
            }
        }

        // Provide summary feedback
        if (!verbose && (installedCount > 0 || errorCount > 0))
        {
            if (installedCount > 0)
            {
                Console.WriteLine($"{UiSymbols.Check} Installed {installedCount} MSIX packages");
            }
            if (errorCount > 0)
            {
                Console.WriteLine($"{UiSymbols.Note} {errorCount} packages failed to install");
            }
        }

        if (exitCode != 0 && verbose)
        {
            Console.WriteLine($"{UiSymbols.Note} PowerShell batch operation returned exit code {exitCode}");
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
    public static string? FindWindowsAppSdkMsixDirectory(Dictionary<string, string>? usedVersions = null)
    {
        var globalWinsdkDir = BuildToolsService.GetGlobalWinsdkDirectory();
        var pkgsDir = Path.Combine(globalWinsdkDir, "packages");
        
        if (!Directory.Exists(pkgsDir))
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
                if (msixDir != null) return msixDir;
            }
            
            // Fallback: check if runtime is included in the main WindowsAppSDK package (for older versions)
            if (usedVersions.TryGetValue("Microsoft.WindowsAppSDK", out var wasdkVersion))
            {
                var msixDir = TryGetMsixDirectory(pkgsDir, $"Microsoft.WindowsAppSDK.{wasdkVersion}");
                if (msixDir != null) return msixDir;
            }
        }

        // General scan approach: Look for Microsoft.WindowsAppSDK.Runtime packages first (WinAppSDK 1.8+)
        var runtimePackages = Directory.GetDirectories(pkgsDir, "Microsoft.WindowsAppSDK.Runtime.*");
        foreach (var runtimePkg in runtimePackages.OrderByDescending(p => p))
        {
            var msixDir = TryGetMsixDirectoryFromPath(runtimePkg);
            if (msixDir != null) return msixDir;
        }

        // Fallback: check if runtime is included in the main WindowsAppSDK package (for older versions)
        var mainPackages = Directory.GetDirectories(pkgsDir, "Microsoft.WindowsAppSDK.*")
            .Where(p => !Path.GetFileName(p).Contains("Runtime", StringComparison.OrdinalIgnoreCase));
        
        foreach (var mainPkg in mainPackages.OrderByDescending(p => p))
        {
            var msixDir = TryGetMsixDirectoryFromPath(mainPkg);
            if (msixDir != null) return msixDir;
        }

        return null;
    }

    /// <summary>
    /// Helper method to check if an MSIX directory exists for a given package directory name
    /// </summary>
    /// <param name="pkgsDir">The packages directory</param>
    /// <param name="packageDirName">The package directory name</param>
    /// <returns>The MSIX directory path if it exists, null otherwise</returns>
    private static string? TryGetMsixDirectory(string pkgsDir, string packageDirName)
    {
        var pkgDir = Path.Combine(pkgsDir, packageDirName);
        return TryGetMsixDirectoryFromPath(pkgDir);
    }

    /// <summary>
    /// Helper method to check if an MSIX directory exists for a given package path
    /// </summary>
    /// <param name="packagePath">The full path to the package directory</param>
    /// <returns>The MSIX directory path if it exists, null otherwise</returns>
    private static string? TryGetMsixDirectoryFromPath(string packagePath)
    {
        var msixDir = Path.Combine(packagePath, "tools", "MSIX");
        return Directory.Exists(msixDir) ? msixDir : null;
    }
}
