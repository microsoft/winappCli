// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;
using WinApp.Cli.Telemetry.Events;

namespace WinApp.Cli.Commands;

internal class InitCommand : Command, IShortDescription
{
    public string ShortDescription => "Initialize existing project with manifest and/or SDK packages";

    public static Argument<DirectoryInfo> BaseDirectoryArgument { get; }
    public static Option<DirectoryInfo> ConfigDirOption { get; }
    public static Option<SdkInstallMode?> SetupSdksOption { get; }
    public static Option<bool> IgnoreConfigOption { get; }
    public static Option<bool> NoGitignoreOption { get; }
    public static Option<bool> UseDefaults { get; }
    public static Option<bool> ConfigOnlyOption { get; }
    public static Option<FileInfo> ExeOption { get; }
    public static Option<bool> SparseOption { get; }
    public static Option<string?> NameOption { get; }
    public static Option<string?> PublisherOption { get; }
    public static Option<DirectoryInfo> OutputDirOption { get; }
    public static Option<bool> ForceOption { get; }

    static InitCommand()
    {
        BaseDirectoryArgument = new Argument<DirectoryInfo>("base-directory")
        {
            Description = "Base/root directory for the winapp workspace, for consumption or installation.",
            Arity = ArgumentArity.ZeroOrOne
        };
        BaseDirectoryArgument.AcceptExistingOnly();
        ConfigDirOption = new Option<DirectoryInfo>("--config-dir")
        {
            Description = "Directory to read/store configuration (default: the selected project directory, or current directory if no project is detected)"
        };
        ConfigDirOption.AcceptExistingOnly();
        SetupSdksOption = new Option<SdkInstallMode?>("--setup-sdks")
        {
            Description = "SDK installation mode: 'stable' (default), 'preview', 'experimental', or 'none' (skip SDK installation)",
            HelpName = "stable|preview|experimental|none"
        };
        IgnoreConfigOption = new Option<bool>("--ignore-config", "--no-config")
        {
            Description = "Don't use configuration file for version management"
        };
        NoGitignoreOption = new Option<bool>("--no-gitignore")
        {
            Description = "Don't update .gitignore file"
        };
        UseDefaults = new Option<bool>("--use-defaults", "--no-prompt")
        {
            Description = "Skip interactive prompts and use default answers. Normal init targets the positional project directory if given, otherwise the current directory (e.g., winapp init . --use-defaults). Sparse init (--exe --sparse) ignores the positional directory and writes to --output-dir instead."
        };
        ConfigOnlyOption = new Option<bool>("--config-only")
        {
            Description = "Only handle configuration file operations (create if missing, validate if exists). Skip package installation and other workspace setup steps."
        };
        ExeOption = new Option<FileInfo>("--exe")
        {
            Description = "Path to the application executable. Requires --sparse. Generates an identity-only sparse manifest for the exe instead of a full package/SDK setup."
        };
        ExeOption.AcceptExistingOnly();
        SparseOption = new Option<bool>("--sparse")
        {
            Description = "Generate a sparse identity manifest (appxmanifest.xml) for an existing desktop exe instead of a full package manifest. Use with --exe. Skips SDK/package installation."
        };
        NameOption = new Option<string?>("--name")
        {
            Description = "Override the package name (sparse only; default: inferred from the exe)"
        };
        PublisherOption = new Option<string?>("--publisher")
        {
            Description = "Override the publisher CN (sparse only; default: inferred from the exe's company name). Bare names are auto-wrapped as CN=<name>."
        };
        OutputDirOption = new Option<DirectoryInfo>("--output-dir")
        {
            Description = "Directory to write the sparse manifest and Assets/ (sparse only; default: a 'sparse/' folder in the current directory)"
        };
        ForceOption = new Option<bool>("--force")
        {
            Description = "Overwrite an existing appxmanifest.xml in the target directory (sparse only). Without this, init fails instead of replacing existing manifest/asset files."
        };
    }

    public InitCommand() : base("init", "Start here for initializing a Windows app with required setup. Sets up everything needed for Windows app development: creates Package.appxmanifest with default assets, downloads Windows SDK and Windows App SDK packages, and generates projections. When SDK packages are managed (--setup-sdks stable/preview/experimental), also creates winapp.yaml to pin versions for 'restore'/'update'; with --setup-sdks none (e.g., for Rust/Tauri projects that bring their own SDK bindings), no winapp.yaml is created. Interactive by default; automatically uses defaults in non-interactive environments (use --use-defaults to skip prompts explicitly). Use 'restore' instead if you cloned a repo that already has winapp.yaml. Use 'manifest generate' if you only need a manifest, or 'cert generate' if you need a development certificate for code signing.")
    {
        Arguments.Add(BaseDirectoryArgument);
        Options.Add(ConfigDirOption);
        Options.Add(SetupSdksOption);
        Options.Add(IgnoreConfigOption);
        Options.Add(NoGitignoreOption);
        Options.Add(UseDefaults);
        Options.Add(ConfigOnlyOption);
        Options.Add(ExeOption);
        Options.Add(SparseOption);
        Options.Add(NameOption);
        Options.Add(PublisherOption);
        Options.Add(OutputDirOption);
        Options.Add(ForceOption);
    }

    public class Handler(
        IWorkspaceSetupService workspaceSetupService,
        IProjectDetectionService projectDetectionService,
        IProjectContextDetector projectContextDetector,
        ICurrentDirectoryProvider currentDirectoryProvider,
        IManifestService manifestService,
        IStatusService statusService,
        IAnsiConsole ansiConsole,
        ILogger<Handler> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var baseDirectoryExplicit = parseResult.GetResult(BaseDirectoryArgument)?.Implicit == false;
            var baseDirectory = parseResult.GetValue(BaseDirectoryArgument) ?? currentDirectoryProvider.GetCurrentDirectoryInfo();
            var configDirExplicit = parseResult.GetResult(ConfigDirOption)?.Implicit == false;
            var configDir = parseResult.GetValue(ConfigDirOption) ?? currentDirectoryProvider.GetCurrentDirectoryInfo();
            var setupSdks = parseResult.GetValue(SetupSdksOption);
            var ignoreConfig = parseResult.GetValue(IgnoreConfigOption);
            var noGitignore = parseResult.GetValue(NoGitignoreOption);
            var useDefaults = parseResult.GetValue(UseDefaults);
            var configOnly = parseResult.GetValue(ConfigOnlyOption);
            var exe = parseResult.GetValue(ExeOption);
            var sparse = parseResult.GetValue(SparseOption);
            var name = parseResult.GetValue(NameOption);
            var publisher = parseResult.GetValue(PublisherOption);
            var outputDir = parseResult.GetValue(OutputDirOption);
            var force = parseResult.GetValue(ForceOption);

            // --exe and the sparse-only overrides (--name / --publisher / --output-dir / --force) apply
            // only to the sparse identity flow. Reject them without --sparse so scripts don't report
            // success after their input is silently discarded by the normal initialization path.
            if (!sparse)
            {
                var sparseOnly = new List<string>();
                if (exe != null) { sparseOnly.Add("--exe"); }
                if (name != null) { sparseOnly.Add("--name"); }
                if (publisher != null) { sparseOnly.Add("--publisher"); }
                if (outputDir != null) { sparseOnly.Add("--output-dir"); }
                if (parseResult.GetResult(ForceOption) is { Implicit: false }) { sparseOnly.Add("--force"); }

                if (sparseOnly.Count > 0)
                {
                    logger.LogError(
                        "{Options} require --sparse. Use 'winapp init --exe <exe> --sparse' for identity packaging, or remove these options for full package initialization.",
                        string.Join(", ", sparseOnly));
                    return 1;
                }
            }

            // Detect non-interactive environments (piped stdin, CI, etc.) and fall back
            // to --use-defaults behavior to avoid InvalidOperationException from prompts.
            if (!useDefaults && !ansiConsole.Profile.Capabilities.Interactive)
            {
                logger.LogWarning("{Warning}  Non-interactive environment detected. Using default values.", UiSymbols.Warning);
                useDefaults = true;
            }

            // Sparse identity flow: generate an identity-only appxmanifest.xml + assets for an
            // existing exe. This intentionally skips all SDK/package installation.
            if (sparse)
            {
                // The positional base-directory argument configures the normal init flow only.
                // Reject it in sparse mode rather than silently ignoring it — output location is
                // controlled by --output-dir (default: a 'sparse/' folder in the current directory).
                if (baseDirectoryExplicit)
                {
                    logger.LogError(
                        "A positional directory is not used with --sparse. Use --output-dir <dir> to choose where the sparse manifest and Assets/ are written (default: ./sparse).");
                    return 1;
                }

                // Sparse identity packaging has no SDK/config/gitignore steps, so the normal-init
                // options below do nothing in this mode. Reject any that were explicitly supplied
                // rather than exiting 0 while silently discarding them.
                var normalOnly = new List<string>();
                if (configOnly) { normalOnly.Add("--config-only"); }
                if (parseResult.GetResult(SetupSdksOption) is { Implicit: false }) { normalOnly.Add("--setup-sdks"); }
                if (configDirExplicit) { normalOnly.Add("--config-dir"); }
                if (ignoreConfig) { normalOnly.Add("--ignore-config"); }
                if (noGitignore) { normalOnly.Add("--no-gitignore"); }

                if (normalOnly.Count > 0)
                {
                    logger.LogError(
                        "{Options} are not used with --sparse (identity packaging installs no SDKs and writes no winapp.yaml). Remove them, or drop --sparse for full package initialization.",
                        string.Join(", ", normalOnly));
                    return 1;
                }

                ProjectContextEvent.Log("init", () =>
                {
                    return projectContextDetector.DetectDirectory(
                        exe?.Directory ?? currentDirectoryProvider.GetCurrentDirectoryInfo(),
                        ProjectTargetKind.BuildOutput) with
                    {
                        Packaging = ProjectContextPackaging.Sparse,
                    };
                });

                return await RunSparseInitAsync(exe, name, publisher, outputDir, useDefaults, force, cancellationToken);
            }

            DirectoryInfo? selectedDirectory;
            var detectedProjectSelected = false;

            if (baseDirectoryExplicit)
            {
                // User specified a directory: skip search, use the directory directly
                selectedDirectory = await InitDirectlyAsync(baseDirectory, useDefaults, cancellationToken);
            }
            else if (useDefaults)
            {
                // --use-defaults without explicit directory: use cwd, but warn if no project detected
                var detected = projectDetectionService.DetectProjectAt(baseDirectory);
                if (detected == null)
                {
                    logger.LogWarning("{Warning}  No known project type detected in current directory. Initializing with winapp.yaml",
                        UiSymbols.Warning);
                }
                selectedDirectory = baseDirectory;
            }
            else
            {
                // No directory specified: search for compatible projects
                var selection = await DetectAndSelectProjectAsync(
                    baseDirectory, cancellationToken);
                selectedDirectory = selection.Directory;
                detectedProjectSelected = selection.DetectedProjectSelected;
            }

            if (selectedDirectory == null)
            {
                // User declined to init in a directory with no compatible projects
                return 1;
            }

            ProjectContextEvent.Log("init", () =>
            {
                var projectContext = projectContextDetector.DetectDirectory(
                    selectedDirectory,
                    ProjectTargetKind.Workspace);
                if (detectedProjectSelected && projectContext.IsKnown)
                {
                    projectContext = projectContext with
                    {
                        Source = ProjectContextSource.SelectedProject,
                        Confidence = ProjectContextConfidence.High,
                    };
                }

                return projectContext;
            });

            // If --config-dir was not explicitly set, use the selected/init directory
            // so winapp.yaml is co-located with the project
            if (!configDirExplicit)
            {
                configDir = selectedDirectory;
            }

            var options = new WorkspaceSetupOptions
            {
                BaseDirectory = selectedDirectory,
                ConfigDir = configDir,
                SdkInstallMode = setupSdks,
                IgnoreConfig = ignoreConfig,
                NoGitignore = noGitignore,
                UseDefaults = useDefaults,
                RequireExistingConfig = false,
                ForceLatestBuildTools = true,
                ConfigOnly = configOnly
            };

            var result = await workspaceSetupService.SetupWorkspaceAsync(options, cancellationToken);

            // If init ran in a nested directory, remind the user to cd there for further commands
            if (result == 0 && selectedDirectory.FullName != baseDirectory.FullName)
            {
                var relativePath = Path.GetRelativePath(baseDirectory.FullName, selectedDirectory.FullName);
                ansiConsole.MarkupLineInterpolated($"{UiSymbols.Info}  Run [blue]cd \"{relativePath}\"[/] to use further winapp commands in your project directory.");
            }

            return result;
        }

        /// <summary>
        /// Generates an identity-only sparse manifest (appxmanifest.xml) plus placeholder assets
        /// for an existing desktop executable. Skips all SDK/package installation because sparse
        /// identity packages have no SDK dependencies.
        /// </summary>
        private async Task<int> RunSparseInitAsync(
            FileInfo? exe,
            string? name,
            string? publisher,
            DirectoryInfo? outputDir,
            bool useDefaults,
            bool force,
            CancellationToken cancellationToken)
        {
            if (exe == null)
            {
                logger.LogError("--sparse requires --exe <path>. Provide the path to the application executable.");
                return 1;
            }

            if (!string.Equals(exe.Extension, ".exe", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError(
                    "--exe must point to a .exe file, but '{Name}' is not an executable. Sparse identity is embedded into an .exe, so a non-exe target cannot be used with embed-identity.",
                    exe.Name);
                return 1;
            }

            // Default output location is a dedicated ./sparse/ folder in the current directory.
            // Keeping the identity manifest and its Assets/ out of the exe's build-output directory
            // means a clean/rebuild won't delete them, and the folder holds only the manifest +
            // Assets/ so 'winapp pack' has nothing to warn about. The manifest references the exe by
            // name, so its location is independent of where the exe lives.
            var targetDir = outputDir ?? new DirectoryInfo(
                Path.Join(currentDirectoryProvider.GetCurrentDirectory(), "sparse"));

            // Guard against silently overwriting a hand-authored manifest or assets. Generation uses
            // File.WriteAllTextAsync/File.Create, which would replace an existing appxmanifest.xml
            // (and matching Assets/) in place — including the narrow case where assets exist but the
            // manifest does not. Require --force to opt into overwriting either.
            var existingManifest = new FileInfo(Path.Join(targetDir.FullName, "appxmanifest.xml"));
            var existingAssets = new DirectoryInfo(Path.Join(targetDir.FullName, "Assets"));
            var assetsHaveContent = existingAssets.Exists && existingAssets.EnumerateFiles("*", SearchOption.AllDirectories).Any();
            if (!force && (existingManifest.Exists || assetsHaveContent))
            {
                var whatExists = existingManifest.Exists
                    ? $"An appxmanifest.xml already exists at '{existingManifest.FullName}'"
                    : $"Generated assets already exist in '{existingAssets.FullName}'";
                logger.LogError(
                    "{WhatExists}. Re-run with --force to overwrite, or choose a different --output-dir.",
                    whatExists);
                return 1;
            }

            // Resolve manifest metadata (inferring from the exe and, in interactive mode, prompting)
            // BEFORE entering the status display. Spectre.Console throws if a prompt runs while a
            // live progress spinner is active, so the interactive phase must happen outside it.
            ManifestGenerationInfo manifestInfo;
            try
            {
                manifestInfo = await manifestService.PrepareSparseManifestInfoAsync(
                    targetDir, exe, name, publisher, useDefaults, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return 1;
            }

            SparseInitResult? sparseResult = null;
            var exitCode = await statusService.ExecuteWithStatusAsync("Generating sparse identity manifest...", async (taskContext, ct) =>
            {
                try
                {
                    sparseResult = await manifestService.GenerateSparseIdentityManifestAsync(
                        targetDir, exe, manifestInfo, taskContext, ct);
                    return (0, "Sparse identity manifest generated.");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    taskContext.AddDebugMessage($"Stack Trace: {ex.StackTrace}");
                    return (1, $"{UiSymbols.Error} Failed to generate sparse manifest: {ex.GetBaseException().Message}");
                }
            }, cancellationToken);

            if (exitCode != 0 || sparseResult == null)
            {
                return exitCode;
            }

            // Summary + next steps. Gated on info logging so --quiet/--json stay script-friendly.
            if (logger.IsEnabled(LogLevel.Information))
            {
                ansiConsole.MarkupLineInterpolated($"{UiSymbols.Check} Generated sparse identity package files:");
                ansiConsole.MarkupLineInterpolated($"   {UiSymbols.Files} Manifest: {sparseResult.ManifestPath.FullName}");
                ansiConsole.MarkupLineInterpolated($"   {UiSymbols.Files} Assets:   {sparseResult.AssetsDirectory.FullName}");
                ansiConsole.MarkupLineInterpolated($"   {UiSymbols.Package} Package:  {sparseResult.Info.PackageName}  Version: {sparseResult.Info.Version}");
                ansiConsole.WriteLine();
                ansiConsole.MarkupLine("[yellow]Note:[/] This is an [bold]identity-only[/] package. The generated assets in [blue]Assets/[/] are resolved from the app's install directory (the external content location) at runtime — they are [bold]not[/] bundled into the .msix. Deploy them alongside your application.");
                ansiConsole.WriteLine();
                ansiConsole.MarkupLine("Next steps:");
                ansiConsole.MarkupLineInterpolated($"   1. Run [blue]winapp pack \"{sparseResult.ManifestPath.FullName}\" --cert <dev.pfx>[/] to create the signed identity .msix");
                ansiConsole.MarkupLineInterpolated($"   2. Run [blue]winapp embed-identity \"{exe.FullName}\" --manifest \"{sparseResult.ManifestPath.FullName}\"[/] to connect your exe to the identity package");
                ansiConsole.MarkupLine("   3. Register in your installer with [blue]Add-AppxPackage -Path <msix> -ExternalLocation <install-dir>[/]");
            }

            return 0;
        }

        /// <summary>
        /// Detects compatible projects in the directory tree and prompts the user to select one.
        /// Only called when no directory argument was provided and --use-defaults is not set.
        /// Returns the selected directory and whether it came from a detected project,
        /// or a null directory if the user declines.
        /// </summary>
        private async Task<(DirectoryInfo? Directory, bool DetectedProjectSelected)> DetectAndSelectProjectAsync(
            DirectoryInfo searchRoot,
            CancellationToken cancellationToken)
        {
            const int maxProjects = 10;
            var useLiveSpinner = ProgressDisplay.ShouldUseLiveSpinner(ansiConsole, logger);

            IReadOnlyList<DetectedProject> results;

            if (useLiveSpinner)
            {
                // Animated mode: run detection inside a Spectre Status spinner
                results = await ansiConsole.Status()
                    .AutoRefresh(true)
                    .Spinner(Spinner.Known.Dots)
                    .SpinnerStyle(Style.Parse("blue"))
                    .StartAsync("Searching for known project types...", async ctx =>
                    {
                        return await projectDetectionService.DetectProjectsAsync(
                            searchRoot, maxProjects, progress: null, cancellationToken);
                    });
            }
            else
            {
                results = await projectDetectionService.DetectProjectsAsync(
                    searchRoot, maxProjects, progress: null, cancellationToken);
            }

            // Handle results based on count
            if (results.Count == 0)
            {
                return (await HandleNoProjectsFoundAsync(searchRoot, cancellationToken), false);
            }

            // If the only result is at the search root, use it directly
            if (results.Count == 1 && results[0].DisplayPath == ".")
            {
                logger.LogInformation("{Check} {TypeLabel} project detected ({FilePath})",
                    UiSymbols.Check, results[0].TypeLabel, results[0].DisplayFilePath);
                return (results[0].Directory, true);
            }

            if (results.Count == 1)
            {
                var selected = await HandleSingleProjectAsync(results[0], cancellationToken);
                return (selected, selected is not null);
            }

            return await HandleMultipleProjectsAsync(results, searchRoot, results.Count >= maxProjects, cancellationToken);
        }

        /// <summary>
        /// Handles direct initialization when a directory was explicitly specified or --use-defaults is set.
        /// Checks for a project at the target path; warns if none found.
        /// </summary>
        private async Task<DirectoryInfo?> InitDirectlyAsync(
            DirectoryInfo targetDirectory,
            bool useDefaults,
            CancellationToken cancellationToken)
        {
            var detected = projectDetectionService.DetectProjectAt(targetDirectory);

            if (detected != null)
            {
                logger.LogInformation("{Check} {TypeLabel} project detected ({FilePath})",
                    UiSymbols.Check, detected.TypeLabel, detected.DisplayFilePath);
                return targetDirectory;
            }

            // No project detected at the specified path
            if (useDefaults)
            {
                logger.LogWarning("{Warning} No known project type detected at {Path}. Initializing with winapp.yaml.",
                    UiSymbols.Warning, targetDirectory.FullName);
                return targetDirectory;
            }

            var proceed = await ansiConsole.PromptAsync(
                new ConfirmationPrompt(
                    $"[yellow]No known project type was detected at this path.[/] Initialize with winapp here anyway?")
                {
                    DefaultValue = false
                },
                cancellationToken);

            if (!proceed)
            {
                logger.LogInformation("Init cancelled by user.");
                return null;
            }

            return targetDirectory;
        }

        private async Task<DirectoryInfo?> HandleNoProjectsFoundAsync(
            DirectoryInfo searchRoot,
            CancellationToken cancellationToken)
        {
            var proceed = await ansiConsole.PromptAsync(
                new ConfirmationPrompt(
                    $"[yellow]No known projects type were found.[/] Initialize with winapp.yaml here?")
                {
                    DefaultValue = false
                },
                cancellationToken);

            if (!proceed)
            {
                logger.LogInformation("Init cancelled by user.");
                return null;
            }

            return searchRoot;
        }

        private async Task<DirectoryInfo?> HandleSingleProjectAsync(
            DetectedProject project,
            CancellationToken cancellationToken)
        {
            var confirm = await ansiConsole.PromptAsync(
                new ConfirmationPrompt(
                    $"Found [green]{Markup.Escape(project.TypeLabel)}[/] project at [blue]{Markup.Escape(project.DisplayFilePath)}[/]. Initialize with winapp?"),
                cancellationToken);

            if (!confirm)
            {
                logger.LogInformation("Init cancelled by user.");
                return null;
            }

            ansiConsole.MarkupLineInterpolated($"{UiSymbols.Check} Selected [green]{project.TypeLabel} project ({project.DisplayFilePath})[/]");
            return project.Directory;
        }

        private async Task<(DirectoryInfo Directory, bool DetectedProjectSelected)> HandleMultipleProjectsAsync(
            IReadOnlyList<DetectedProject> projects,
            DirectoryInfo searchRoot,
            bool searchLimitReached,
            CancellationToken cancellationToken)
        {
            var choices = projects.Select(p =>
                $"{Markup.Escape(p.TypeLabel)} project ({Markup.Escape(p.DisplayFilePath)})").ToList();

            // Always offer the current directory as a fallback option
            var currentDirIsListed = projects.Any(p => p.DisplayPath == ".");
            if (!currentDirIsListed)
            {
                choices.Add("Current directory (./) — no project detected");
            }

            var prompt = new SelectionPrompt<string>()
                .Title("Which project would you like to initialize with winapp?")
                .AddChoices(choices);

            if (searchLimitReached)
            {
                prompt.Title("Which project would you like to initialize with winapp? [dim](If your project wasn't found, run: winapp init <path-to-project>)[/]");
            }

            var selected = await ansiConsole.PromptAsync(prompt, cancellationToken);

            var selectedIndex = choices.IndexOf(selected);

            // If the user picked the appended current-directory fallback
            if (!currentDirIsListed && selectedIndex == projects.Count)
            {
                ansiConsole.MarkupLine("Which project would you like to initialize with winapp? [underline]Current directory (./)[/]");
                return (searchRoot, false);
            }

            var selectedProject = projects[selectedIndex];
            ansiConsole.MarkupLineInterpolated($"Which project would you like to initialize with winapp? [underline]{selectedProject.TypeLabel} project ({selectedProject.DisplayFilePath})[/]");
            return (selectedProject.Directory, true);
        }
    }
}
