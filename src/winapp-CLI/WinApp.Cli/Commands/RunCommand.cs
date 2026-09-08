// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;
using WinApp.Cli.Telemetry.Events;

namespace WinApp.Cli.Commands;

internal partial class RunCommand : Command, IShortDescription
{
    public string ShortDescription => "Run a Windows app: build and launch from a .csproj/.sln, or launch an existing build-output folder.";

    public static Argument<FileSystemInfo> InputArgument { get; }
    public static Option<FileInfo> ManifestOption { get; }
    public static Option<DirectoryInfo?> OutputAppXDirectoryOption { get; }
    public static Option<string> ArgsOption { get; }
    public static Option<bool> NoLaunchOption { get; }
    public static Option<bool> WithAliasOption { get; }
    public static Option<bool> DebugOutputOption { get; }
    public static Option<bool> UnregisterOnExitOption { get; }
    public static Option<bool> DetachOption { get; }
    public static Option<bool> CleanOption { get; }
    public static Option<bool> SymbolsOption { get; }
    public static Option<string?> ExecutableOption { get; }

    // Project-mode build options. Additive and inert in folder mode.
    public static Option<string> ConfigurationOption { get; }
    public static Option<string?> ArchOption { get; }
    public static Option<string?> RuntimeOption { get; }
    public static Option<string?> FrameworkOption { get; }
    public static Option<bool> NoBuildOption { get; }
    public static Option<bool> NoRestoreOption { get; }
    public static Option<string[]> PropertyOption { get; }
    public static Option<string?> ProjectOption { get; }

    /// <summary>
    /// Captures zero or more arguments after the <c>--</c> separator and forwards them to the
    /// launched application. System.CommandLine routes all post-<c>--</c> tokens here as
    /// positional arguments; anything typed before <c>--</c> is validated normally.
    /// </summary>
    public static Argument<string[]> PassthroughArgument { get; }

    static RunCommand()
    {
        InputArgument = new Argument<FileSystemInfo>("input")
        {
            Description = "Path to the app to run: a build-output folder, a .csproj project, a .sln/.slnx solution, or a directory containing one of those at its top level (default: current directory).",
            Arity = ArgumentArity.ZeroOrOne
        };

        PassthroughArgument = new Argument<string[]>("app-args")
        {
            Description = "Arguments to pass to the launched application. Provide after -- (e.g., winapp run . -- --flag value).",
            Arity = ArgumentArity.ZeroOrMore,
            // Hidden from help/schema: this argument exists only to absorb tokens after '--'
            // so System.CommandLine doesn't reject them. Exposing it would mislead users and
            // schema consumers into thinking `winapp run <input> [<app-args>...]` is
            // a valid direct invocation; in reality, app args MUST be preceded by '--'.
            Hidden = true,
        };

        ManifestOption = new Option<FileInfo>("--manifest")
        {
            Description = "Path to the Package.appxmanifest (default: auto-detect from input folder or current directory)"
        };

        OutputAppXDirectoryOption = new Option<DirectoryInfo?>("--output-appx-directory")
        {
            Description = "Output directory for the loose layout package. If not specified, a directory named AppX inside the input directory will be used."
        };

        ArgsOption = new Option<string>("--args")
        {
            Description = "Command-line arguments to pass to the application. Alternatively, use -- followed by arguments to avoid escaping (e.g., winapp run . -- --flag value)."
        };

        NoLaunchOption = new Option<bool>("--no-launch")
        {
            Description = "Only create the debug identity and register the package without launching the application"
        };

        WithAliasOption = new Option<bool>("--with-alias")
        {
            Description = "Launch the app using its execution alias instead of AUMID activation. The app runs in the current terminal with inherited stdin/stdout/stderr. Requires a uap5:ExecutionAlias in the manifest. Use \"winapp manifest add-alias\" to add an execution alias to the manifest."
        };

        DebugOutputOption = new Option<bool>("--debug-output")
        {
            Description = "Capture OutputDebugString messages and first-chance exceptions from the launched application. Only one debugger can attach to a process at a time, so other debuggers (Visual Studio, VS Code) cannot be used simultaneously. Use --no-launch instead if you need to attach a different debugger. For WinUI apps, a crash also triggers a stowed-exception triage pass; the first run downloads debugger components (cached under the winapp global directory) and can be pointed at an existing debugger install via the WINAPP_DBGTOOLS_DIR environment variable. Cannot be combined with --no-launch or --json."
        };

        UnregisterOnExitOption = new Option<bool>("--unregister-on-exit")
        {
            Description = "Unregister the development package after the application exits. Only removes packages registered in development mode."
        };

        DetachOption = new Option<bool>("--detach")
        {
            Description = "Launch the application and return immediately without waiting for it to exit. Useful for CI/automation where you need to interact with the app after launch. Prints the PID to stdout (or in JSON with --json)."
        };
        
        CleanOption = new Option<bool>("--clean")
        {
            Description = "Remove the existing package's application data (LocalState, settings, etc.) before re-deploying. By default, application data is preserved across re-deployments."
        };

        SymbolsOption = new Option<bool>("--symbols")
        {
            Description = "Download symbols from Microsoft Symbol Server for richer native crash analysis, including the WinUI stowed-exception dispatch stack. Only used with --debug-output. First run downloads symbols and caches them locally; subsequent runs use the cache."
        };

        ExecutableOption = new Option<string?>("--executable")
        {
            Description = "Path to the executable relative to the input folder. Use to disambiguate when the manifest contains a $targetnametoken$ placeholder and multiple .exe files are present in the input folder."
        };
        ExecutableOption.Aliases.Add("--exe");

        ConfigurationOption = new Option<string>("--configuration")
        {
            Description = "Project mode: build configuration (e.g., Debug, Release). Ignored in folder mode. Default: Debug.",
            DefaultValueFactory = _ => "Debug",
        };
        ConfigurationOption.Aliases.Add("-c");

        ArchOption = new Option<string?>("--arch")
        {
            Description = "Project mode: target architecture (x64, arm64, or x86). Ignored in folder mode. Default: the current process architecture."
        };

        RuntimeOption = new Option<string?>("--runtime")
        {
            Description = "Project mode: target .NET runtime identifier (RID), e.g. win-x64. Project mode uses only the RID's architecture, always builds the canonical win-<arch>, and rejects non-Windows RIDs (e.g. linux-x64); it overrides --arch. Ignored in folder mode."
        };
        RuntimeOption.Aliases.Add("-r");

        FrameworkOption = new Option<string?>("--framework")
        {
            Description = "Project mode: target framework moniker for multi-targeted projects (e.g. net10.0-windows10.0.26100.0). Ignored in folder mode."
        };
        FrameworkOption.Aliases.Add("-f");

        NoBuildOption = new Option<bool>("--no-build")
        {
            Description = "Project mode: skip building and run the existing build output (still evaluates output properties). Ignored in folder mode."
        };

        NoRestoreOption = new Option<bool>("--no-restore")
        {
            Description = "Project mode: skip restoring the project before building. Ignored in folder mode."
        };

        PropertyOption = new Option<string[]>("--property")
        {
            Description = "Project mode: MSBuild property as Name=Value, forwarded to both build and evaluation. Repeatable (e.g. -p WindowsPackageType=None). Ignored in folder mode.",
            // ZeroOrMore (not OneOrMore) so a valueless '-p' reaches the handler, which emits a
            // --json-aware error; OneOrMore would raise a plain-text parser error, bypassing --json.
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = false,
        };
        PropertyOption.Aliases.Add("-p");

        ProjectOption = new Option<string?>("--project")
        {
            Description = "Project mode: when the input is a solution (.sln/.slnx) or a directory with multiple runnable app projects, selects which project to launch (by name or path). Ignored in folder mode."
        };
    }

    public RunCommand() : base("run", "Builds and runs a Windows app from a .csproj/.sln or a build-output folder. In project mode, invokes dotnet build then launches the app (packaged or unpackaged); in folder mode, creates a debug-signed layout, registers the package, and launches it.")
    {
        Arguments.Add(InputArgument);
        Arguments.Add(PassthroughArgument);
        Options.Add(ManifestOption);
        Options.Add(OutputAppXDirectoryOption);
        Options.Add(ArgsOption);
        Options.Add(NoLaunchOption);
        Options.Add(WithAliasOption);
        Options.Add(DebugOutputOption);
        Options.Add(UnregisterOnExitOption);
        Options.Add(DetachOption);
        Options.Add(CleanOption);
        Options.Add(SymbolsOption);
        Options.Add(ExecutableOption);
        Options.Add(ConfigurationOption);
        Options.Add(ArchOption);
        Options.Add(RuntimeOption);
        Options.Add(FrameworkOption);
        Options.Add(NoBuildOption);
        Options.Add(NoRestoreOption);
        Options.Add(PropertyOption);
        Options.Add(ProjectOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public partial class Handler(
        IMsixService msixService,
        IAppLauncherService appLauncherService,
        IPackageRegistrationService packageRegistrationService,
        IDebugOutputService debugOutputService,
        ICurrentDirectoryProvider currentDirectoryProvider,
        IAnsiConsole ansiConsole,
        IStatusService statusService,
        IProjectRunService projectRunService,
        IProjectContextDetector projectContextDetector,
        ILogger<RunCommand> logger) : AsynchronousCommandLineAction
    {
        // Test seams for the execution-alias launch path. They isolate the two operating-system
        // boundaries — resolving the Windows App Execution Alias proxy location and starting the
        // resolved process — so tests can exercise all of the surrounding validation, debug,
        // cancellation and error-handling logic without registering a real alias proxy under
        // %LOCALAPPDATA%\Microsoft\WindowsApps or spawning the resolved binary. Both default to
        // the production behavior, so runtime behavior is unchanged.
        internal Func<string, FileInfo?> ResolveAliasProxy { get; set; } = alias => ExecutionAliasResolver.ResolveAliasPath(alias);
        internal Func<ProcessStartInfo, Process?> ProcessStarter { get; set; } = Process.Start;

        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            // input is optional (ArgumentArity.ZeroOrOne). The final FileSystemInfo is resolved
            // below, AFTER the passthrough split, because a bare `winapp run -- <app-arg>` makes the
            // parser greedily bind the first post-'--' token to this positional. That "stolen" case is
            // detected by comparing what the passthrough argument absorbed against the raw post-'--'
            // tokens; when it happens we fall back to the current directory (see resolution below).
            var inputArg = parseResult.GetValue(InputArgument);
            var manifest = parseResult.GetValue(ManifestOption);
            var outputAppXDirectory = parseResult.GetValue(OutputAppXDirectoryOption);
            var appArgs = parseResult.GetValue(ArgsOption);
            var noLaunch = parseResult.GetValue(NoLaunchOption);
            var withAlias = parseResult.GetValue(WithAliasOption);
            var debugOutput = parseResult.GetValue(DebugOutputOption);
            var unregisterOnExit = parseResult.GetValue(UnregisterOnExitOption);
            var detach = parseResult.GetValue(DetachOption);
            var clean = parseResult.GetValue(CleanOption);
            var useSymbols = parseResult.GetValue(SymbolsOption);
            var executable = parseResult.GetValue(ExecutableOption);
            var isJson = parseResult.GetValue(WinAppRootCommand.JsonOption);

            // Reject a valueless -p/--property. The option uses ZeroOrMore arity so a bare
            // '-p' (no Name=Value) parses without a value instead of raising a System.CommandLine
            // arity error -- which would bypass this command's --json error envelope and print only
            // plain text. Detect it from the raw result: there is one identifier token per '-p'
            // occurrence, so more identifiers than captured value tokens means at least one '-p' was
            // supplied without its argument. Handling it here lets --json callers get structured JSON.
            if (parseResult.GetResult(PropertyOption) is OptionResult propertyResult &&
                propertyResult.IdentifierTokenCount > propertyResult.Tokens.Count)
            {
                return Fail("A --property/-p option was provided without a value. Expected Name=Value (for example: -p WindowsPackageType=None).", isJson);
            }

            // Collect passthrough args from the token stream.
            // With a ZeroOrMore positional argument, System.CommandLine absorbs ALL extra
            // Argument-typed tokens — including unrecognised option-like tokens before '--'.
            // SplitPassthroughTokens uses a count-based diff between the post-'--' token walk
            // and what ZeroOrMore actually absorbed to detect pre-dash invalids without needing
            // to categorise each token by type (which would confuse option values with positionals).
            var allAbsorbed = parseResult.GetValue(PassthroughArgument) ?? [];
            var (passthroughArgs, invalidPreDashTokens) = WindowsCommandLine.SplitPassthroughTokens(
                parseResult.Tokens, allAbsorbed);
            if (invalidPreDashTokens.Count > 0)
            {
                foreach (var bad in invalidPreDashTokens)
                {
                    logger.LogError("{UISymbol} Unrecognized argument: '{Arg}'. To pass arguments to the app, use -- (e.g., winapp run . -- --flag value).", UiSymbols.Error, bad);
                }

                // In --json mode the logger above is suppressed (LogLevel.None), so users would
                // otherwise see only an empty stdout and exit code 1. Emit a structured error so
                // machine-readable callers can surface a useful message.
                if (isJson)
                {
                    var firstBad = invalidPreDashTokens[0];
                    var jsonError = invalidPreDashTokens.Count == 1
                        ? $"Unrecognized argument: '{firstBad}'. To pass arguments to the app, use -- (e.g., winapp run . -- --flag value)."
                        : $"Unrecognized arguments: {string.Join(", ", invalidPreDashTokens.Select(t => $"'{t}'"))}. To pass arguments to the app, use -- (e.g., winapp run . -- --flag value).";
                    PrintJson(aumid: null, processId: null, errorMessage: jsonError);
                }
                return 1;
            }

            // Resolve the effective input path now that the passthrough split is known.
            // ZeroOrOne input + a trailing ZeroOrMore passthrough means the parser binds the
            // FIRST positional token to input even when it appears after '--' (e.g.
            // `winapp run -- --flag`). SplitPassthroughTokens returns the RAW post-'--' tokens
            // (passthroughArgs) independent of that binding, so when input "stole" the first
            // post-'--' token, passthrough absorbed one fewer token than actually followed '--'.
            // In that case (or when no positional was supplied at all) default to the current
            // directory and let ResolveInputAsync decide project-vs-folder mode from cwd (matches
            // `dotnet run`). The stolen token still reaches the app because passthroughArgs is raw.
            var inputStolenFromPassthrough = allAbsorbed.Length < passthroughArgs.Count;
            var inputFsi = (inputArg is null || inputStolenFromPassthrough)
                ? currentDirectoryProvider.GetCurrentDirectoryInfo()
                : inputArg;

            // Preserve the pre-existing "path must exist" guarantee. The input argument no
            // longer uses AcceptExistingOnly (that validator hard-errors on the stolen post-'--'
            // token above, bypassing the --json envelope), so validate a genuinely-provided path
            // here instead. Defaulted/stolen inputs resolve to the current directory and are skipped.
            if (inputArg is not null && !inputStolenFromPassthrough && !inputArg.Exists)
            {
                return Fail($"'{inputArg.FullName}' does not exist.", isJson);
            }

            // Merge '--args' value with any tokens collected after '--'.
            var passthroughStr = WindowsCommandLine.JoinArguments(passthroughArgs);
            if (passthroughStr != null)
            {
                appArgs = string.IsNullOrEmpty(appArgs) ? passthroughStr : $"{appArgs} {passthroughStr}";
            }

            WarnIfNuGetCallerForwardedAWinAppOption(parseResult, passthroughArgs, isJson);

            // Validate mutually exclusive options. Route through Fail so that under --json these
            // emit the structured error envelope instead of a plain-text banner (Change 2 / L5).
            if (withAlias && noLaunch)
            {
                return Fail("--with-alias and --no-launch cannot be used together.", isJson);
            }

            if (debugOutput && noLaunch)
            {
                return Fail("--debug-output and --no-launch cannot be used together.", isJson);
            }

            if (isJson && debugOutput)
            {
                return Fail("--json and --debug-output cannot be used together.", isJson);
            }

            if (isJson && withAlias)
            {
                return Fail("--json and --with-alias cannot be used together.", isJson);
            }

            if (unregisterOnExit && noLaunch)
            {
                return Fail("--unregister-on-exit and --no-launch cannot be used together.", isJson);
            }

            if (detach && noLaunch)
            {
                return Fail("--detach and --no-launch cannot be used together.", isJson);
            }

            if (detach && debugOutput)
            {
                return Fail("--detach and --debug-output cannot be used together.", isJson);
            }

            if (detach && withAlias)
            {
                return Fail("--detach and --with-alias cannot be used together.", isJson);
            }

            if (detach && unregisterOnExit)
            {
                return Fail("--detach and --unregister-on-exit cannot be used together.", isJson);
            }

            // --symbols only affects the stowed-exception triage that runs under --debug-output.
            // Warn (non-fatal) when it is supplied on its own so the flag isn't silently ignored.
            if (useSymbols && !debugOutput)
            {
                logger.LogWarning("{UISymbol} --symbols has no effect without --debug-output; ignoring.", UiSymbols.Warning);
            }

            // Validate the input path early so the command fails fast with a clear
            // long-path message before any file system operations are attempted.
            try
            {
                LongPathHelper.ValidatePathLength(inputFsi.FullName);
            }
            catch (InvalidOperationException ex)
            {
                // Route through Fail so this run-local validation emits the structured error
                // envelope under --json instead of a suppressed-logger silent exit (Change 2 / L5).
                // Non-json output is unchanged (Fail's log call is identical to the prior line).
                return Fail(ex.Message, isJson);
            }

            // Route folder mode (existing, unchanged behavior) vs project mode (build a .csproj).
            // Project mode is keyed on the input pointing at / containing a top-level buildable .csproj.
            RunInputResolution inputResolution;
            try
            {
                var projectSelector = parseResult.GetValue(ProjectOption);
                // Classify candidates (multi-.csproj / solution) under the SAME effective build inputs
                // the subsequent build uses, so a project whose OutputType/test markers are conditional
                // on Configuration/arch/TFM/user -p is picked the way it will build (e.g.
                // `winapp run App.sln -c Release` must not select a Debug-only app then build Release).
                var classificationInputs = BuildClassificationInputs(parseResult);

                // Input resolution runs BEFORE the first `🔎` context line, and for a directory /
                // solution / multi-project input it spawns silent MSBuild classification evaluates that
                // can take a couple of seconds (several on a large solution like AI Dev Gallery) — long
                // enough that the command looks hung with nothing on screen. On a real interactive
                // terminal, animate a spinner around it so liveness shows immediately (~150 ms) instead
                // of a dead gap. Skipped under --verbose (Debug) so any phase traces render plainly, and
                // under --json/--quiet/agent/CI/redirected (ShouldUseLiveSpinner == false), where it runs
                // exactly as before with no status output.
                if (ProgressDisplay.ShouldUseLiveSpinner(ansiConsole, logger) && !logger.IsEnabled(LogLevel.Debug))
                {
                    inputResolution = await ansiConsole.Status()
                        .AutoRefresh(true)
                        .Spinner(Spinner.Known.Dots)
                        .SpinnerStyle(Style.Parse("blue"))
                        .StartAsync("Resolving project...", async _ =>
                            await projectRunService.ResolveInputAsync(inputFsi, cancellationToken, projectSelector, classificationInputs));
                }
                else
                {
                    // No live spinner here (--verbose/--json/--quiet/agent/CI/redirected). Resolution can
                    // still spawn many silent MSBuild classification evaluates for a large solution, so
                    // announce it up front on the plain path — otherwise a redirected/CI run looks hung
                    // with nothing on screen. Suppressed for --json (pure stdout) and --quiet (Info off).
                    if (!isJson && logger.IsEnabled(LogLevel.Information))
                    {
                        logger.LogInformation("{UISymbol} Resolving project...", UiSymbols.Search);
                    }

                    inputResolution = await projectRunService.ResolveInputAsync(inputFsi, cancellationToken, projectSelector, classificationInputs);
                }
            }
            catch (ProjectRunException ex)
            {
                return Fail(ex.Message, isJson);
            }

            ProjectContextEvent.Log("run", () =>
                string.Equals(
                    parseResult.GetValue(WinAppRootCommand.CallerOption),
                    "nuget-package",
                    StringComparison.Ordinal)
                    ? projectContextDetector.CreateNuGetContext(
                        parseResult.GetValue(WinAppRootCommand.ProjectFrameworkOption))
                    : inputResolution.Mode == WinAppRunMode.Project
                        ? projectContextDetector.DetectProject(inputResolution.Csproj!) with
                        {
                            ExecutionMode = ProjectExecutionMode.Project,
                        }
                        : projectContextDetector.DetectDirectory(
                            inputResolution.ProjectDirectory,
                            ProjectTargetKind.BuildOutput) with
                        {
                            Packaging = ProjectContextPackaging.Packaged,
                            ExecutionMode = ProjectExecutionMode.Folder,
                        });

            if (inputResolution.Mode == WinAppRunMode.Project)
            {
                return await RunProjectModeAsync(parseResult, inputResolution.Csproj!, inputResolution.Solution, inputResolution.SelectionReason, appArgs, isJson, cancellationToken);
            }

            // Folder mode: the FileSystemInfo converter yields a DirectoryInfo for an existing
            // directory. Delegate to the shared pipeline with no project-mode runtime hints, so
            // behavior is identical to before project mode existed.
            var inputFolder = inputResolution.ProjectDirectory;

            // Breadcrumb: we reached folder mode because no top-level .csproj/.sln/.slnx with a runnable
            // app was found, so the path is treated as a pre-built layout (nothing is built). Without
            // this, a user troubleshooting why a source directory was not built only sees a later
            // "manifest not found" and can't tell why nothing built. Keep this at debug level: folder
            // mode is the normal, expected path for a build-output directory — including every
            // `dotnet run` through the NuGet package, which always points winapp at the output
            // folder — so at Info it reads as a warning about a situation that is entirely routine.
            if (!isJson && inputFsi is DirectoryInfo && logger.IsEnabled(LogLevel.Debug))
            {
                ansiConsole.MarkupLineInterpolated(
                    $"{UiSymbols.Search} No .csproj/.sln/.slnx with a runnable app found in '{inputFolder.FullName}' — running it as a build-output folder.");
            }

            return await ExecuteRunPipelineAsync(
                inputFolder, manifest, outputAppXDirectory, appArgs,
                noLaunch, withAlias, debugOutput, unregisterOnExit, detach, clean, useSymbols, executable, isJson,
                runtimeArch: null, projectFile: null, framework: null, noRestore: false, cancellationToken);
        }

        /// <summary>
        /// The shared "run a loose layout" pipeline: resolve the manifest, create + register the
        /// debug identity, launch (AUMID or execution alias), and wait/detach as requested. Folder
        /// mode calls this directly; packaged project mode calls it with the build's TargetDir as
        /// <paramref name="inputFolder"/> plus the resolved <paramref name="runtimeArch"/> and
        /// <paramref name="projectFile"/> so the correct-arch Windows App Runtime is installed.
        /// </summary>
        internal async Task<int> ExecuteRunPipelineAsync(
            DirectoryInfo inputFolder,
            FileInfo? manifest,
            DirectoryInfo? outputAppXDirectory,
            string? appArgs,
            bool noLaunch,
            bool withAlias,
            bool debugOutput,
            bool unregisterOnExit,
            bool detach,
            bool clean,
            bool useSymbols,
            string? executable,
            bool isJson,
            string? runtimeArch,
            FileInfo? projectFile,
            string? framework,
            bool noRestore,
            CancellationToken cancellationToken)
        {
            uint processId = 0;
            string? packageFamilyName = null;
            string? packageFullName = null;
            string? packageName = null;
            string? publisher = null;
            string? applicationId = null;
            string? aumid = null;
            string? errorMessage = null;
            DirectoryInfo? resolvedOutputDir = null;
            var statusMessage = noLaunch ? "Registering packaged application..." : "Launching packaged application...";
            var success = await statusService.ExecuteWithStatusAsync(statusMessage, async (taskContext, cancellationToken) =>
            {
                try
                {
                    // Resolve manifest with priority: --manifest → input folder → cwd
                    FileInfo resolvedManifest;
                    if (manifest != null)
                    {
                        // --manifest no longer uses AcceptExistingOnly (that parser validator hard-errors
                        // before the --json envelope and the packaging-mode gate); validate existence here.
                        if (!manifest.Exists)
                        {
                            throw new FileNotFoundException($"Manifest file not found: {manifest.FullName}");
                        }

                        resolvedManifest = manifest;
                        taskContext.AddDebugMessage($"{UiSymbols.Note} Using specified manifest: {resolvedManifest}");
                    }
                    else
                    {
                        var folderManifest = FindManifest(inputFolder.FullName);
                        if (folderManifest.Exists)
                        {
                            resolvedManifest = folderManifest;
                            taskContext.AddDebugMessage($"{UiSymbols.Note} Using manifest from input folder: {resolvedManifest}");
                        }
                        else
                        {
                            var cwdManifest = FindManifest(currentDirectoryProvider.GetCurrentDirectory());
                            if (cwdManifest.Exists)
                            {
                                resolvedManifest = cwdManifest;
                                taskContext.AddDebugMessage($"{UiSymbols.Note} Using manifest from current directory: {resolvedManifest}");
                            }
                            else
                            {
                                throw new FileNotFoundException(
                                    $"Manifest file not found. Searched in: input folder ({inputFolder.FullName}), current directory ({currentDirectoryProvider.GetCurrentDirectory()}). Use --manifest to specify the path.");
                            }
                        }
                    }

                    outputAppXDirectory ??= new DirectoryInfo(Path.Combine(inputFolder.FullName, "AppX"));
                    resolvedOutputDir = outputAppXDirectory;

                    // Validate that the manifest and output paths are usable (check long path support if needed)
                    LongPathHelper.ValidatePathLength(resolvedManifest.FullName);
                    LongPathHelper.ValidatePathLength(outputAppXDirectory.FullName);

                    // Step 2: Create and register the debug identity
                    taskContext.AddDebugMessage($"{UiSymbols.Package} Creating debug identity...");
                    var identityResult = await msixService.AddLooseLayoutIdentityAsync(
                        resolvedManifest,
                        inputFolder,
                        outputAppXDirectory,
                        taskContext,
                        clean,
                        executable,
                        runtimeArch,
                        projectFile,
                        framework,
                        noRestore,
                        cancellationToken);

                    packageFamilyName = appLauncherService.ComputePackageFamilyName(
                        identityResult.PackageName,
                        identityResult.Publisher);
                    packageFullName = appLauncherService.GetPackageFullName(packageFamilyName);
                    packageName = identityResult.PackageName;
                    publisher = identityResult.Publisher;
                    applicationId = identityResult.ApplicationId;
                    aumid = $"{packageFamilyName}!{applicationId}";

                    taskContext.AddDebugMessage($"{UiSymbols.Package} Package: {identityResult.PackageName}");
                    taskContext.AddDebugMessage($"{UiSymbols.User} Publisher: {publisher}");
                    taskContext.AddDebugMessage($"{UiSymbols.Id} App ID: {applicationId}");
                    taskContext.AddDebugMessage($"{UiSymbols.Link} AUMID: {aumid}");

                    if (noLaunch)
                    {
                        return (0, $"{packageFamilyName} registered (AUMID: {aumid})");
                    }

                    if (withAlias)
                    {
                        // --with-alias: skip AUMID launch, will launch via execution alias after status completes
                        taskContext.AddDebugMessage($"{UiSymbols.Rocket} Will launch via execution alias...");
                        return (0, $"{packageFamilyName} registered (AUMID: {aumid})");
                    }

                    // Step 3: Launch the application using IApplicationActivationManager
                    taskContext.AddDebugMessage($"{UiSymbols.Rocket} Launching application...");
                    processId = appLauncherService.LaunchByAumid(aumid, appArgs);

                    return (0, $"{packageFamilyName} launched (PID: {processId})");
                }
                catch (Exception error)
                {
                    errorMessage = error.Message;
                    return (1, $"{UiSymbols.Error} Failed to launch application: {error.Message}");
                }
            }, cancellationToken);

            if (success != 0)
            {
                if (isJson)
                {
                    PrintJson(aumid, processId: null, errorMessage);
                }
                return success;
            }

            if (noLaunch)
            {
                if (isJson)
                {
                    PrintJson(aumid, processId: null, errorMessage: null);
                }
                return success;
            }

            // --detach: return immediately after launch without waiting for exit
            if (detach)
            {
                if (isJson)
                {
                    PrintJson(aumid, processId, errorMessage: null);
                }
                else
                {
                    // Surface the launched PID for automation, consistent with the unpackaged
                    // project-mode detach path (Change 3 / L6).
                    ansiConsole.WriteLine(processId.ToString());
                }
                return 0;
            }

            // --with-alias: launch via execution alias with inherited stdio
            if (withAlias)
            {
                var aliasExitCode = await LaunchViaExecutionAliasAsync(resolvedOutputDir!, inputFolder, appArgs, debugOutput, useSymbols, packageFullName, cancellationToken);
                if (unregisterOnExit && packageName != null)
                {
                    await UnregisterDevPackageAsync(packageName, cancellationToken);
                }
                return aliasExitCode;
            }

            if (isJson)
            {
                PrintJson(aumid, processId, errorMessage: null);
            }

            // --debug-output: run the debug event loop instead of plain WaitForExit.
            // DebugSetProcessKillOnExit(true) in the debug service handles crash cleanup.
            if (debugOutput)
            {
                var exitCode = await debugOutputService.RunDebugLoopAsync(processId, cancellationToken, useSymbols,
                    symbolSearchPaths: [inputFolder.FullName]);
                if (cancellationToken.IsCancellationRequested)
                {
                    appLauncherService.TerminatePackageProcesses(packageFullName, processId);
                }
                if (unregisterOnExit && packageName != null)
                {
                    await UnregisterDevPackageAsync(packageName, cancellationToken);
                }
                return exitCode;
            }


            // Wait for the launched process to exit before returning.
            // The process may have already exited by the time we get here (common for
            // fast-starting apps), in which case GetProcessById throws ArgumentException.
            // PIDs above int.MaxValue cannot be tracked via Process.GetProcessById.
            int appExitCode;
            if (processId > int.MaxValue)
            {
                appExitCode = 0;
            }
            else
            {
                try
                {
                    using var process = Process.GetProcessById(unchecked((int)processId));
                    await process.WaitForExitAsync(cancellationToken);
                    appExitCode = process.ExitCode;
                }
                catch (ArgumentException)
                {
                    // Process already exited before we could attach — treat as success.
                    appExitCode = 0;
                }
                catch (OperationCanceledException)
                {
                    // Ctrl+C — terminate all processes belonging to the package before exiting.
                    appLauncherService.TerminatePackageProcesses(packageFullName, processId);
                    appExitCode = -1;
                }
            }

            if (unregisterOnExit && packageName != null)
            {
                await UnregisterDevPackageAsync(packageName, cancellationToken);
            }

            return appExitCode;
        }

        void PrintJson(string? aumid, uint? processId, string? errorMessage)
        {
            var result = new RunCommandResult
            {
                AUMID = aumid,
                ProcessId = processId,
                Error = errorMessage
            };

            var json = JsonSerializer.Serialize(result, RunCommandJsonContext.Default.RunCommandResult);

            // Write the machine-readable payload straight to the underlying stdout writer rather than
            // ansiConsole.WriteLine, which renders through Spectre's word-wrapping layer and injects raw
            // CR/LF *inside* the JSON string values once a message exceeds the (redirected) console width
            // (~80 cols) — corrupting the payload so strict parsers (JsonDocument.Parse) reject it. This
            // mirrors how the other JSON-emitting commands (cert/ui) write their output.
            ansiConsole.Profile.Out.Writer.WriteLine(json);
        }

        private static FileInfo FindManifest(string directory) => ManifestHelper.FindManifest(directory);

        /// <summary>
        /// Unregisters dev-mode packages matching the given name.
        /// Only removes packages where <c>IsDevelopmentMode == true</c>.
        /// </summary>
        private async Task UnregisterDevPackageAsync(string packageName, CancellationToken cancellationToken)
        {
            try
            {
                var packages = packageRegistrationService.FindDevPackages(packageName);
                foreach (var pkg in packages)
                {
                    if (!pkg.IsDevelopmentMode)
                    {
                        continue;
                    }

                    await packageRegistrationService.UnregisterAsync(pkg.Name, preserveAppData: false, cancellationToken);
                    logger.LogDebug("Unregistered package {FullName} on exit.", pkg.FullName);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug("Failed to unregister package on exit: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// Maps a <c>winapp run</c> option to the MSBuild property assignment that configures it
        /// through the NuGet integration. Options absent from this map have no dedicated property and
        /// are pointed at <c>WinAppRunArgs</c> instead.
        /// </summary>
        private static readonly Dictionary<string, string> OptionToMSBuildProperty = new(StringComparer.Ordinal)
        {
            ["--detach"] = "WinAppRunDetach=true",
            ["--no-launch"] = "WinAppRunNoLaunch=true",
            ["--debug-output"] = "WinAppRunDebugOutput=true",
            ["--with-alias"] = "WinAppRunUseExecutionAlias=true",
            ["--unregister-on-exit"] = "WinAppRunUnregisterOnExit=true",
            ["--clean"] = "WinAppRunClean=true",
            ["--symbols"] = "WinAppRunSymbols=true",
            ["--executable"] = "WinAppRunExecutable=<path>",
            ["--exe"] = "WinAppRunExecutable=<path>",
            ["--args"] = "WinAppLaunchArgs=<args>",
            ["--manifest"] = "WinAppManifestPath=<path>",
            ["--output-appx-directory"] = "WinAppLooseLayoutPath=<path>",
        };

        /// <summary>
        /// Migration aid for the <c>dotnet run</c> argument-routing change. The NuGet targets now end
        /// <c>RunArguments</c> with a separator, so everything typed after <c>dotnet run</c> reaches the
        /// application instead of configuring winapp. A project that previously relied on
        /// <c>dotnet run --detach</c> keeps working syntactically but silently changes meaning, and
        /// MSBuild cannot warn about it because it never sees those tokens.
        /// </summary>
        /// <remarks>
        /// Only fires for the NuGet caller: a direct <c>winapp run . -- --detach</c> is an explicit,
        /// unambiguous request to forward the token, so warning there would be noise. Suppressed under
        /// <c>--json</c> to keep stdout a single machine-readable document.
        /// <para>
        /// <see cref="OptionToMSBuildProperty"/> is the whole trigger set. Notices are limited to
        /// options that (a) actually did something on this path before the change and (b) have a
        /// property that replaces them, so every notice carries advice that works. Matching every
        /// option the parser knows would flag ordinary application flags -- <c>--help</c>,
        /// <c>--configuration</c>, <c>-p</c> -- because the project-mode options are ignored in folder
        /// mode, which is the only mode the NuGet targets use. There is deliberately no generic
        /// fallback: it could only guess <c>WinAppRunArgs</c>, and for an option that takes a value
        /// that guess drops the value and produces a command that fails.
        /// </para>
        /// </remarks>
        private void WarnIfNuGetCallerForwardedAWinAppOption(
            ParseResult parseResult,
            IReadOnlyList<string> forwardedArgs,
            bool isJson)
        {
            if (isJson || forwardedArgs.Count == 0 || !logger.IsEnabled(LogLevel.Information))
            {
                return;
            }

            if (!string.Equals(parseResult.GetValue(WinAppRootCommand.CallerOption), "nuget-package", StringComparison.Ordinal))
            {
                return;
            }

            var alreadyReported = new HashSet<string>(StringComparer.Ordinal);
            foreach (var arg in forwardedArgs)
            {
                // An option can arrive attached to its value (--executable=foo.exe, --detach=true).
                // Those spellings configured winapp before this change just as the separated form did,
                // so match on the name and keep the original token in the message.
                var name = arg.Split('=', 2)[0];
                if (!OptionToMSBuildProperty.TryGetValue(name, out var replacement) || !alreadyReported.Add(name))
                {
                    continue;
                }

                // Written through ansiConsole (gated on the logger level) to match how the rest of this
                // command emits user-facing text, so --quiet still suppresses it.
                ansiConsole.MarkupLineInterpolated(
                    $"{UiSymbols.Info} '{arg}' was passed to your application, not to winapp. To configure winapp, use -p:{replacement} instead.");
            }
        }

        /// <summary>
        /// Builds the <see cref="ProcessStartInfo"/> used to launch an app via its execution
        /// alias. Extracted so tests can verify that passthrough args (from <c>--args</c> /
        /// <c>--</c>) are forwarded into <see cref="ProcessStartInfo.Arguments"/> without
        /// having to spawn a real process.
        /// </summary>
        internal static ProcessStartInfo BuildAliasProcessStartInfo(string alias, string? appArgs)
        {
            var psi = new ProcessStartInfo
            {
                FileName = alias,
                UseShellExecute = false,
                RedirectStandardInput = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            };

            if (!string.IsNullOrEmpty(appArgs))
            {
                psi.Arguments = appArgs;
            }

            return psi;
        }

        /// <summary>
        /// Launches the app using its execution alias (from the processed manifest in the AppX directory).
        /// The alias process inherits stdin/stdout/stderr so console apps run inline.
        /// </summary>
        private async Task<int> LaunchViaExecutionAliasAsync(
            DirectoryInfo outputAppXDirectory,
            DirectoryInfo inputFolder,
            string? appArgs,
            bool debugOutput,
            bool useSymbols,
            string? packageFullName,
            CancellationToken cancellationToken)
        {
            // Read the processed manifest from the AppX output directory (placeholders already resolved)
            var processedManifest = ManifestHelper.FindManifest(outputAppXDirectory.FullName);
            if (!processedManifest.Exists)
            {
                logger.LogError("{UISymbol} Processed manifest not found at {Path}. Cannot determine execution alias.", UiSymbols.Error, processedManifest.FullName);
                return 1;
            }

            var content = await File.ReadAllTextAsync(processedManifest.FullName, Encoding.UTF8, cancellationToken);
            var aliases = MsixService.ExtractExecutionAliases(content);

            if (aliases.Count == 0)
            {
                logger.LogError("{UISymbol} No execution alias found in the manifest. Add one with 'winapp manifest add-alias' or use AUMID launch (without --with-alias).", UiSymbols.Error);
                return 1;
            }

            // The alias value is attacker-controlled (it comes verbatim from the manifest,
            // which may originate from an untrusted repo). Reject any alias that is not a
            // bare filename before touching the filesystem.
            var alias = aliases[0]; // Use the first alias
            if (!ExecutionAliasResolver.IsSafeAliasName(alias))
            {
                logger.LogError(
                    "{UISymbol} Execution alias '{Alias}' is not a valid bare .exe filename. Aliases must be a single .exe filename with no path separators, drive letters, '..' segments, trailing dots/spaces, or reserved device names (CON, NUL, COM1-9, LPT1-9). Fix the <uap5:ExecutionAlias> entry in the manifest.",
                    UiSymbols.Error,
                    alias);
                return 1;
            }

            // Resolve the alias to the canonical Windows App Execution Alias proxy under
            // %LOCALAPPDATA%\Microsoft\WindowsApps\. Using an absolute path here is the
            // mitigation for the bare-filename CWD/PATH lookup that CreateProcess would
            // otherwise perform — passing just "a.exe" would let an attacker-supplied
            // a.exe in the project folder hijack the launch.
            var aliasFile = ResolveAliasProxy(alias);
            if (aliasFile is null || !aliasFile.Exists)
            {
                logger.LogError(
                    "{UISymbol} Execution alias proxy for '{Alias}' was not found at the expected location ('{ExpectedPath}'). Windows may not have registered the alias yet, or it has been removed. Try re-running 'winapp run' without --with-alias to launch via AUMID instead.",
                    UiSymbols.Error,
                    alias,
                    aliasFile?.FullName ?? ExecutionAliasResolver.GetDefaultWindowsAppsDirectory());
                return 1;
            }

            // Build the ProcessStartInfo via a static helper so the argument-forwarding
            // contract is unit-testable without spawning a real process. The FileName is
            // the fully-qualified WindowsApps path so CreateProcess does not consult CWD
            // or PATH when launching.
            var psi = BuildAliasProcessStartInfo(aliasFile.FullName, appArgs);

            try
            {
                using var process = ProcessStarter(psi);
                if (process == null)
                {
                    logger.LogError("{UISymbol} Failed to start process via execution alias '{Alias}' ({Path}).", UiSymbols.Error, alias, aliasFile.FullName);
                    return 1;
                }

                if (debugOutput)
                {
                    var exitCode = await debugOutputService.RunDebugLoopAsync(unchecked((uint)process.Id), cancellationToken,
                        useSymbols, symbolSearchPaths: [inputFolder.FullName]);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        appLauncherService.TerminatePackageProcesses(packageFullName, unchecked((uint)process.Id));
                    }
                    return exitCode;
                }

                try
                {
                    await process.WaitForExitAsync(cancellationToken);
                    return process.ExitCode;
                }
                catch (OperationCanceledException)
                {
                    // Ctrl+C — terminate all processes belonging to the package before exiting.
                    appLauncherService.TerminatePackageProcesses(packageFullName, unchecked((uint)process.Id));
                    return -1;
                }
            }
            catch (Exception ex)
            {
                logger.LogError("{UISymbol} Failed to launch via execution alias '{Alias}' ({Path}): {Error}", UiSymbols.Error, alias, aliasFile.FullName, ex.Message);
                return 1;
            }
        }
    }
}

internal sealed class RunCommandResult
{
    public string? AUMID { get; set; }
    public uint? ProcessId { get; set; }
    public string? Error { get; set; }
}

[JsonSerializable(typeof(RunCommandResult))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class RunCommandJsonContext : JsonSerializerContext;
