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
            var cppwinrt = new CppWinrtService();
            var layout = new PackageLayoutService();

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
            var winsdkDir = buildToolsService.FindWinsdkDirectory(options.BaseDirectory);
            
            // Setup-specific startup messages
            if (!options.RequireExistingConfig && !options.Quiet)
            {
                Console.WriteLine($"{UiSymbols.Rocket} using config → {configService.ConfigPath}");
                Console.WriteLine($"{UiSymbols.Rocket} winsdk init starting in {options.BaseDirectory}");
                Console.WriteLine($"{UiSymbols.Folder} Workspace → {winsdkDir}");
                
                if (options.IncludeExperimental)
                {
                    Console.WriteLine($"{UiSymbols.Wrench} Experimental/prerelease packages will be included");
                }
            }
            else if (!options.Quiet)
            {
                Console.WriteLine($"{UiSymbols.Folder} Workspace → {winsdkDir}");
            }

            // First ensure basic workspace
            packageService.InitializeWorkspace(winsdkDir);

            if (!options.Quiet)
            {
                if (!options.RequireExistingConfig)
                {
                    Console.WriteLine($"{UiSymbols.Rocket} using config → {configService.ConfigPath}");
                    Console.WriteLine($"{UiSymbols.Rocket} winsdk init starting in {options.BaseDirectory}");
                }
                Console.WriteLine($"{UiSymbols.Folder} Workspace → {winsdkDir}");

                if (options.IncludeExperimental)
                {
                    Console.WriteLine($"{UiSymbols.Wrench} Experimental/prerelease packages will be included");
                }
            }

            // Create all standard workspace directories for full setup/restore
            var pkgsDir = Path.Combine(winsdkDir, "packages");
            var includeOut = Path.Combine(winsdkDir, "include");
            var libOut = Path.Combine(winsdkDir, "lib");
            var binOut = Path.Combine(winsdkDir, "bin");

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
                    winsdkDir,
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
                    winsdkDir,
                    NugetService.SDK_PACKAGES,
                    includeExperimental: options.IncludeExperimental,
                    ignoreConfig: options.IgnoreConfig,
                    quiet: options.Quiet,
                    cancellationToken: cancellationToken);
            }

            // Step 5: Run cppwinrt and set up projections
            var cppWinrtExe = cppwinrt.FindCppWinrtExe(pkgsDir, usedVersions);
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
            layout.CopyIncludesFromPackages(pkgsDir, includeOut);
            Console.WriteLine($"{UiSymbols.Check} Headers ready → {includeOut}");

            var libRoot = Path.Combine(winsdkDir, "lib");
            if (!options.Quiet)
            {
                Console.WriteLine($"{UiSymbols.Books} Copying import libs by arch → {libRoot}");
            }
            layout.CopyLibsAllArch(pkgsDir, libRoot);
            var libArchs = Directory.Exists(libRoot) ? string.Join(", ", Directory.EnumerateDirectories(libRoot).Select(Path.GetFileName)) : "(none)";
            Console.WriteLine($"{UiSymbols.Books} Import libs ready for archs: {libArchs}");

            var binRoot = Path.Combine(winsdkDir, "bin");
            if (!options.Quiet)
            {
                Console.WriteLine($"{UiSymbols.Gear} Copying runtime binaries by arch → {binRoot}");
            }
            layout.CopyRuntimesAllArch(pkgsDir, binRoot);
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
                        var shareDir = Path.Combine(winsdkDir, "share", "Microsoft.WindowsAppSDK");
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
            var winmds = layout.FindWinmds(pkgsDir).ToList();
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
            await CppWinrtRunner.RunWithRspAsync(cppWinrtExe, winmds, includeOut, winsdkDir, verbose: !options.Quiet, cancellationToken: cancellationToken);
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
                baseDirectory: options.BaseDirectory,
                quiet: options.Quiet,
                forceLatest: forceLatestBuildTools,
                cancellationToken: cancellationToken);

            if (buildToolsPath != null && !options.Quiet)
            {
                Console.WriteLine($"{UiSymbols.Check} BuildTools ready → {buildToolsPath}");
            }

            // Step 7: Save configuration (for setup) or we're done (for restore)
            if (!options.RequireExistingConfig)
            {
                // Setup: Save winsdk.yaml with used versions
                var finalConfig = new WinsdkConfig();
                foreach (var kvp in usedVersions)
                {
                    finalConfig.SetVersion(kvp.Key, kvp.Value);
                }
                configService.Save(finalConfig);
                Console.WriteLine($"{UiSymbols.Save} Wrote config → {configService.ConfigPath}");

                // Update .gitignore to exclude .winsdk folder (unless --no-gitignore is specified)
                if (!options.NoGitignore)
                {
                    var path = new DirectoryInfo(winsdkDir);
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
}