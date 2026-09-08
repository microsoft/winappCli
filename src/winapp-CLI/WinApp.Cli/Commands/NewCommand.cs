// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;
using WinApp.Cli.Telemetry;
using WinApp.Cli.Telemetry.Events;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace WinApp.Cli.Commands;

internal class NewCommand : Command, IShortDescription
{
    public string ShortDescription => "Create a new WinUI app";

    /// <summary>NuGet package id of the official WASDK WinUI C# template pack.</summary>
    internal const string TemplatePackageId = "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates";

    /// <summary>
    /// Short name of the default template (the blank app) used when the caller doesn't pick one and
    /// no prompt is shown. Resolved against the live template list, so if the pack ever renames it we
    /// fall back to the first project template rather than failing.
    /// </summary>
    internal const string DefaultTemplateShortName = "winui";

    /// <summary><c>--template-version latest</c>: install the newest published pack, skip the update prompt.</summary>
    internal const string LatestVersionKeyword = "latest";

    /// <summary><c>--template-version installed</c>: keep whatever pack is installed, skip the feed check and prompt.</summary>
    internal const string InstalledVersionKeyword = "installed";

    /// <summary>Minimum .NET SDK version, matching <c>winapp run</c>'s project mode.</summary>
    internal static readonly Version MinimumSdkVersion = new(8, 0, 100);

    // Starting with .NET SDK 9.0.200, `dotnet new install` requires the `PackageId@Version` form and
    // rejects the older `PackageId::Version` syntax; SDKs below this band still use `::`. The install
    // command picks the separator from the detected SDK so a cold-cache first run works on both.
    internal static readonly Version FirstAtSeparatorSdkVersion = new(9, 0, 200);

    // Fixed dotnet argument lists, hoisted to static readonly fields to satisfy CA1861.
    private static readonly string[] VersionArgs = ["--version"];
    private static readonly string[] ListTemplatePacksArgs = ["new", "uninstall"];
    private static readonly string[] UpdateCheckArgs = ["new", "update", "--check-only"];

    // `dotnet new list winui --columns-all`: enumerate the installed WinUI templates with the Type and
    // Tags columns we classify on. `list` is context-aware — run from inside a WinUI project it also
    // includes item templates (Type=item); elsewhere it lists only project templates.
    private static readonly string[] ListTemplatesArgs = ["new", "list", "winui", "--columns-all"];

    // Force English CLI output for the template subprocesses we parse. `dotnet new uninstall`,
    // `list`, and `update` all localize their labels/headers per DOTNET_CLI_UI_LANGUAGE, which would
    // break the locale-dependent parsing (installed-version detection, column headers, update rows).
    private static readonly Dictionary<string, string> EnglishUiEnvironment = new()
    {
        ["DOTNET_CLI_UI_LANGUAGE"] = "en",
    };

    // Distinct exit codes so agents can branch on the failing stage without parsing text.
    internal const int ExitSuccess = 0;
    internal const int ExitInvalidArgs = 2;
    internal const int ExitSdkMissing = 3;
    internal const int ExitTemplatePackFailed = 4;
    internal const int ExitScaffoldFailed = 5;

    // Windows caps a single path component at 255 characters. The scaffold writes "<name>.csproj",
    // so the project name must leave room for that suffix or dotnet new fails mid-scaffold instead of
    // failing fast on the invalid argument.
    internal const int MaxProjectNameLength = 255 - 7; // ".csproj".Length == 7

    public static Option<string?> TemplateOption { get; }
    public static Option<string?> NameOption { get; }
    public static Option<DirectoryInfo?> OutputOption { get; }
    public static Option<bool> UseDefaultsOption { get; }
    public static Option<bool> ForceOption { get; }
    public static Option<string?> TemplateVersionOption { get; }
    public static Option<bool> ListOption { get; }

    static NewCommand()
    {
        TemplateOption = new Option<string?>("--template", "-t")
        {
            // Dynamic: the accepted values come from the installed pack, not a fixed enum, so the CLI
            // picks up new templates automatically. Validated against the live list at run time.
            Description = "Template short name. XAML templates: winui, winui-navview, winui-tabview, winui-mvvm, winui-lib, winui-unittest. Experimental Reactor (C#-only, MVU) templates: reactor, reactor-mvu, reactor-navview, reactor-tabview. Run 'winapp new --list' to see all.",
            HelpName = "short-name"
        };
        NameOption = new Option<string?>("--name", "-n")
        {
            Description = "Name for the new app/project (default: derived from --output, else 'WinUIApp')."
        };
        OutputOption = new Option<DirectoryInfo?>("--output", "-o")
        {
            Description = "Directory to create the app in (default: ./<name>). Created if it doesn't exist."
        };
        UseDefaultsOption = new Option<bool>("--use-defaults", "--no-prompt")
        {
            Description = "Do not prompt; use defaults (blank template, name from --output/--name, keep installed templates)."
        };
        ForceOption = new Option<bool>("--force")
        {
            Description = "Scaffold even if the output directory already contains files."
        };
        TemplateVersionOption = new Option<string?>("--template-version")
        {
            Description = $"WinUI template pack version: '{LatestVersionKeyword}' (install newest), '{InstalledVersionKeyword}' (keep what's installed), or an explicit version. Default: install latest if none, else prompt to update a stale pack."
        };
        ListOption = new Option<bool>("--list")
        {
            Description = "List the available WinUI templates and exit (installs the latest template pack if none is installed)."
        };
    }

    public NewCommand() : base("new", "Create a new WinUI app from an official Windows App SDK template. Templates cover both markup-based XAML apps (blank, NavigationView, TabView, MVVM) and the experimental Reactor apps (C#-only, MVU) — pick one interactively, then a name (the output directory defaults to ./<name>). Automatically uses defaults in non-interactive environments (use --use-defaults to skip prompts explicitly). Requires the .NET SDK; installs the WinUI template pack on demand (grabbing the latest, or offering to update a stale one) and delegates scaffolding to 'dotnet new'. Use --list to see the available templates. Scaffolds against the installed SDK's target framework and prints a template-specific next step when done (e.g. 'dotnet run' for app templates).")
    {
        Options.Add(TemplateOption);
        Options.Add(NameOption);
        Options.Add(OutputOption);
        Options.Add(UseDefaultsOption);
        Options.Add(ForceOption);
        Options.Add(TemplateVersionOption);
        Options.Add(ListOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    /// <summary>
    /// Emits the <see cref="NewCommandResult"/> JSON shape for a parse-time failure (e.g. an invalid
    /// boolean value or a single-dash typo) so <c>winapp new --json</c> callers still get
    /// machine-readable output instead of System.CommandLine's help/error text. Invoked from the
    /// Program-level parse-error bridge before the handler would otherwise run.
    /// </summary>
    internal static void EmitParseErrorJson(string error, TextWriter? output = null)
    {
        var result = new NewCommandResult
        {
            Created = false,
            Error = error,
        };
        var json = JsonSerializer.Serialize(result, NewCommandJsonContext.Default.NewCommandResult);
        (output ?? Console.Out).WriteLine(json);
    }

    /// <summary>
    /// Returns true only when <paramref name="name"/> is a safe single path segment. The resolved
    /// name becomes both the default output directory (<c>./&lt;name&gt;</c>) and the <c>dotnet new</c>
    /// project name, so path separators, <c>.</c>/<c>..</c>, rooted paths, invalid filename
    /// characters, Windows reserved device names, over-long names (that would push the generated
    /// <c>&lt;name&gt;.csproj</c> past the 255-character path-component limit), and trailing dot/space
    /// names are rejected so the scaffold can't escape the current directory or produce an unusable
    /// folder.
    /// </summary>
    internal static bool IsValidProjectName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Length > MaxProjectNameLength
            || name is "." or ".."
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        // Reject option-shaped names. Even though every token is passed to dotnet new via ArgumentList
        // (so nothing is re-parsed by a shell), a leading '-' makes the child dotnet new parser treat
        // the value as another switch — e.g. a name of "--force" derived from "--output .\--force" would
        // be consumed as dotnet new's own --force flag rather than the project name.
        if (name[0] is '-')
        {
            return false;
        }

        // Windows cannot create directories whose name ends in a space or dot.
        if (name[^1] is ' ' or '.')
        {
            return false;
        }

        // Reserved DOS device names are invalid regardless of any extension (e.g. "CON", "CON.txt").
        var stem = name;
        var dot = stem.IndexOf('.');
        if (dot >= 0)
        {
            stem = stem[..dot];
        }

        return !ReservedDeviceNames.Contains(stem);
    }

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Returns true only when the <paramref name="requestedVersion"/> of the template pack is already
    /// installed. Parses the <c>dotnet new uninstall</c> listing (a package-id line followed by an
    /// indented "Version: x" line) so an older/different installed version is not mistaken for the
    /// requested one.
    /// </summary>
    internal static bool IsTemplatePackInstalled(string uninstallListOutput, string requestedVersion)
        => TryGetInstalledPackVersion(uninstallListOutput, out var installed)
            && installed is not null
            && NuGetVersionHelper.NuGetVersionsEquivalent(installed, requestedVersion);

    /// <summary>
    /// Extracts the installed version of the WinUI template pack from a <c>dotnet new uninstall</c>
    /// listing (the "Version: x" line nested under the package-id header). Returns false when the pack
    /// is not present. Used to distinguish an older installed version (which may be upgraded) from a
    /// newer one (which must not be silently downgraded, since template packs are global).
    /// </summary>
    internal static bool TryGetInstalledPackVersion(string uninstallListOutput, out string? installedVersion)
    {
        installedVersion = null;
        if (string.IsNullOrEmpty(uninstallListOutput))
        {
            return false;
        }

        var lines = uninstallListOutput.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Trim().Equals(TemplatePackageId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // `dotnet new uninstall` nests details under the package header, e.g.
            //    Microsoft.WindowsAppSDK.WinUI.CSharp.Templates   (indent 3)
            //       Version: 0.0.6-alpha                          (indent 6)
            // so the version is a deeper-indented line. Bound the scan to this block by stopping at
            // the next line indented no deeper than the header, which prevents a following package's
            // "Version:" line from being misread as the WinUI pack version.
            var headerIndent = IndentWidth(lines[i]);
            const string marker = "Version:";
            for (var j = i + 1; j < lines.Length; j++)
            {
                var raw = lines[j];
                var line = raw.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (IndentWidth(raw) <= headerIndent)
                {
                    break;
                }

                var idx = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    installedVersion = line[(idx + marker.Length)..].Trim();
                    return installedVersion.Length > 0;
                }
            }

            return false;
        }

        return false;
    }

    /// <summary>Number of leading space/tab characters (indentation) on a line, ignoring a trailing CR.</summary>
    private static int IndentWidth(string line)
    {
        var count = 0;
        while (count < line.Length && (line[count] == ' ' || line[count] == '\t'))
        {
            count++;
        }

        return count;
    }

    public class Handler(
        IDotNetService dotNetService,
        ICurrentDirectoryProvider currentDirectoryProvider,
        IAnsiConsole ansiConsole,
        ITemplateCacheReader templateCacheReader,
        ITemplateUpdateCheckThrottle templateUpdateThrottle,
        ILogger<Handler> logger) : AsynchronousCommandLineAction
    {
        /// <summary>How the caller asked us to resolve the template-pack version.</summary>
        private enum VersionMode
        {
            /// <summary>No <c>--template-version</c>: install latest if none, else prompt to update a stale pack.</summary>
            Default,
            /// <summary><c>latest</c>: install the newest pack, no prompt.</summary>
            Latest,
            /// <summary><c>installed</c>: keep the installed pack, no feed check or prompt.</summary>
            Installed,
            /// <summary>An explicit version was requested.</summary>
            Explicit,
        }

        /// <summary>Mutable accumulator threaded through the invocation so the wrapper can emit one
        /// correlated <see cref="NewCommandEvent"/> covering every exit path.</summary>
        private sealed class InvocationTelemetry
        {
            public string? Template;
            public bool TemplateIsItem;
            public string VersionMode = "Default";
            public bool Interactive;
            public bool ListOnly;
        }

        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var tel = new InvocationTelemetry();
            var correlationId = TelemetryCorrelation.CurrentId;
            var exitCode = ExitSuccess;
            string? outcome = null;
            try
            {
                exitCode = await InvokeCoreAsync(parseResult, tel, cancellationToken);
                return exitCode;
            }
            catch (OperationCanceledException)
            {
                outcome = "cancelled";
                throw;
            }
            catch
            {
                outcome = "error";
                throw;
            }
            finally
            {
                NewCommandEvent.Log(
                    tel.Template,
                    tel.TemplateIsItem,
                    tel.VersionMode,
                    tel.Interactive,
                    tel.ListOnly,
                    outcome ?? MapOutcome(exitCode, tel.ListOnly),
                    exitCode,
                    correlationId);
            }
        }

        /// <summary>Maps a process exit code to a low-cardinality outcome label for telemetry.</summary>
        private static string MapOutcome(int exitCode, bool listOnly) => exitCode switch
        {
            ExitSuccess => listOnly ? "listed" : "created",
            ExitInvalidArgs => "invalid-args",
            ExitSdkMissing => "sdk-missing",
            ExitTemplatePackFailed => "pack-failed",
            ExitScaffoldFailed => "scaffold-failed",
            _ => "unknown",
        };

        private async Task<int> InvokeCoreAsync(ParseResult parseResult, InvocationTelemetry tel, CancellationToken cancellationToken = default)
        {
            var templateArg = parseResult.GetValue(TemplateOption);
            var nameFromOption = parseResult.GetValue(NameOption);
            var output = parseResult.GetValue(OutputOption);
            var useDefaults = parseResult.GetValue(UseDefaultsOption);
            var force = parseResult.GetValue(ForceOption);
            var versionRaw = parseResult.GetValue(TemplateVersionOption);
            var list = parseResult.GetValue(ListOption);
            var isJson = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var quiet = parseResult.GetValue(WinAppRootCommand.QuietOption);

            tel.ListOnly = list;

            var name = nameFromOption;

            // Non-interactive environments (CI, piped stdin) behave like --use-defaults to avoid
            // prompt exceptions — mirrors InitCommand. --json also implies the no-prompt path: Spectre
            // prompt bytes written to stdout would otherwise precede the JSON payload and break
            // JSON.parse for machine callers, even on an interactive TTY.
            if (!useDefaults && (isJson || !ansiConsole.Profile.Capabilities.Interactive))
            {
                if (!isJson)
                {
                    logger.LogWarning("{Warning}  Non-interactive environment detected. Using default values.", UiSymbols.Warning);
                }
                useDefaults = true;
            }

            tel.Interactive = !useDefaults;

            // Classify --template-version. Keywords (latest/installed) are matched case-insensitively
            // and bypass the numeric-version grammar; only an explicit version is shape-validated.
            var (mode, explicitVersion) = ClassifyVersion(versionRaw);
            tel.VersionMode = mode.ToString();
            if (mode == VersionMode.Explicit && !NuGetVersionHelper.IsPlausibleVersion(explicitVersion!))
            {
                var versionError = $"Invalid --template-version '{versionRaw}'. Use '{LatestVersionKeyword}', '{InstalledVersionKeyword}', or a NuGet version such as 1.2.3.";
                if (list && isJson)
                {
                    PrintListJson(null, null, versionError);
                }
                else if (isJson)
                {
                    PrintJson(false, templateArg, name ?? string.Empty, (output ?? currentDirectoryProvider.GetCurrentDirectoryInfo()).FullName, versionError);
                }
                else
                {
                    logger.LogError("{Error} {Detail}", UiSymbols.Error, versionError);
                }
                return ExitInvalidArgs;
            }

            // Fail fast on an explicitly supplied invalid --name before running any dotnet subprocess.
            // An option-shaped or path-bearing name (e.g. "-o", "..\Escaped") is an injection/traversal
            // risk, so reject it up front rather than after the SDK probe and pack enumeration. A name
            // derived later from --output or a prompt is validated at its own resolution point.
            if (nameFromOption is not null && !IsValidProjectName(nameFromOption))
            {
                var nameError = $"Invalid name '{nameFromOption}'. Use a simple name without path separators or invalid filename characters.";
                if (list && isJson)
                {
                    PrintListJson(null, null, nameError);
                }
                else if (isJson)
                {
                    PrintJson(false, templateArg, nameFromOption, (output ?? currentDirectoryProvider.GetCurrentDirectoryInfo()).FullName, nameError);
                }
                else
                {
                    logger.LogError("{Error} {Detail}", UiSymbols.Error, nameError);
                }
                return ExitInvalidArgs;
            }

            var currentDir = currentDirectoryProvider.GetCurrentDirectoryInfo();

            // Resolve the directory whose global.json chain governs both the template-pack SDK and the
            // scaffolded project's target framework. Project output defaults to ./<name> (whose nearest
            // existing ancestor is the current directory); an explicit --output may live under a
            // different global.json, so probe from its nearest existing ancestor — the same chain
            // `dotnet build` will later use. All dotnet subprocesses (SDK probe, pack install,
            // enumeration, scaffold) run here so the pinned TFM matches the resolved SDK, and item
            // templates surface only when this location is inside a WinUI project.
            var workingDir = NearestExistingAncestor(output ?? currentDir) ?? currentDir;

            // Prerequisite: .NET SDK. The pack install's `dotnet new install` separator depends on the
            // SDK band, and the scaffold pins the project's TFM to the SDK major, so probe it up front.
            var (sdkVersion, sdkError) = await WithSpinnerAsync(
                "Checking .NET SDK...",
                () => CheckDotnetSdkAsync(workingDir, isJson, cancellationToken));
            if (sdkVersion is null)
            {
                var detail = sdkError ?? "The .NET SDK is required to create a WinUI app. Install it from https://dotnet.microsoft.com/download, then re-run 'winapp new'.";
                if (list && isJson)
                {
                    PrintListJson(null, null, detail);
                }
                else if (isJson)
                {
                    PrintJson(false, templateArg, name ?? string.Empty, (output ?? currentDir).FullName, detail);
                }
                return ExitSdkMissing;
            }

            // Resolve and ensure the template pack (install latest / keep installed / explicit /
            // prompt-to-update), returning the version now in use. --list keeps an installed pack as-is
            // (no update prompt) but installs the latest when none is present.
            var (packOk, packVersion, packError) = await ResolveTemplatePackAsync(
                workingDir, mode, explicitVersion, useDefaults, forListing: list, sdkVersion, isJson, quiet, cancellationToken);
            if (!packOk)
            {
                var detail = packError ?? $"Failed to prepare the WinUI template pack '{TemplatePackageId}'.";
                if (list && isJson)
                {
                    PrintListJson(null, null, detail);
                }
                else if (isJson)
                {
                    PrintJson(false, templateArg, name ?? string.Empty, (output ?? currentDir).FullName, detail);
                }
                else
                {
                    logger.LogError("{Error} {Detail}", UiSymbols.Error, detail);
                }
                return ExitTemplatePackFailed;
            }

            // Enumerate templates from the installed pack, in the working directory's context (so item
            // templates surface only when that location is inside a WinUI project).
            var (templates, listFailure) = await WithSpinnerAsync(
                "Loading WinUI templates...",
                () => EnumerateTemplatesAsync(workingDir, cancellationToken));
            if (templates.Count == 0)
            {
                var enumError = listFailure is not null
                    ? $"Could not enumerate WinUI templates from the installed pack '{TemplatePackageId}': {listFailure}"
                    : $"Could not enumerate WinUI templates from the installed pack '{TemplatePackageId}'.";
                if (list && isJson)
                {
                    PrintListJson(packVersion, null, enumError);
                }
                else if (isJson)
                {
                    PrintJson(false, templateArg, name ?? string.Empty, (output ?? currentDir).FullName, enumError);
                }
                else
                {
                    logger.LogError("{Error} {Detail}", UiSymbols.Error, enumError);
                }
                return ExitTemplatePackFailed;
            }

            // --list: print the catalog and exit without scaffolding.
            if (list)
            {
                if (isJson)
                {
                    PrintListJson(packVersion, templates, null);
                }
                else if (!quiet)
                {
                    WriteTemplateList(templates, packVersion);
                }
                return ExitSuccess;
            }

            // 1. Resolve the template entry (validate --template against the live catalog).
            WinUiTemplateEntry? entry;
            if (templateArg is not null)
            {
                entry = templates.FirstOrDefault(t => t.MatchesShortName(templateArg));
                if (entry is null)
                {
                    var valid = string.Join(", ", templates.Select(t => t.ShortName));
                    var templateError = $"Invalid template '{templateArg}'. Valid templates: {valid}. Run 'winapp new --list' to see details.";
                    if (isJson)
                    {
                        PrintJson(false, templateArg, name ?? string.Empty, (output ?? currentDir).FullName, templateError);
                    }
                    else
                    {
                        logger.LogError("{Error} {Detail}", UiSymbols.Error, templateError);
                    }
                    return ExitInvalidArgs;
                }
            }
            else if (useDefaults)
            {
                // Never let an experimental template become the silent default: the blank app is
                // resolved by short name, and the fallbacks (for a pack that renamed it) prefer a
                // stable project template over a prerelease one such as the Reactor templates, which
                // sort ahead of the WinUI ones alphabetically.
                entry = templates.FirstOrDefault(t => t.MatchesShortName(DefaultTemplateShortName))
                    ?? templates.FirstOrDefault(t => t.IsProject && !t.IsExperimental)
                    ?? templates.FirstOrDefault(t => t.IsProject)
                    ?? templates[0];
            }
            else
            {
                entry = await PromptTemplateAsync(templates, cancellationToken);
            }

            tel.Template = entry.ShortName;
            tel.TemplateIsItem = entry.IsItem;

            // 1a. Resolve the target-framework pin now, before any name prompt, so a template the
            // installed SDK is too old for (the Reactor templates require .NET 10) fails immediately
            // with an actionable message instead of scaffolding a project that cannot be built.
            var frameworkArgs = new List<string>();
            if (entry.IsProject)
            {
                var frameworkError = ResolveTargetFrameworkArgs(frameworkArgs, entry, sdkVersion);
                if (frameworkError is not null)
                {
                    if (isJson)
                    {
                        PrintJson(false, entry.ShortName, name ?? string.Empty, (output ?? currentDir).FullName, frameworkError, entry.IsExperimental);
                    }
                    else
                    {
                        logger.LogError("{Error} {Detail}", UiSymbols.Error, frameworkError);
                    }
                    return ExitSdkMissing;
                }
            }

            // 2. Resolve name. Only a genuinely absent --name enters the defaulting path; an explicitly
            // supplied but blank value (e.g. --name "   ") is kept so the validation below rejects it
            // instead of silently scaffolding the default. The default is derived from the chosen
            // template (a project noun like "WinUIApp", or an item noun like "MyPage") and auto-numbered
            // to the first free variant so a taken default becomes "WinUIApp1", "WinUIApp2", etc.
            if (name is null)
            {
                // --output is the destination *project directory* for project templates, so its leaf name
                // is a sensible project name. For item templates --output is the target folder the item is
                // added into (e.g. the project dir), NOT the item's name, so deriving the name from it would
                // produce e.g. "DemoApp.xaml". Item templates therefore always fall through to the derived
                // default name (numbered against the target folder), never the --output leaf.
                if (output is not null && entry.IsProject)
                {
                    name = output.Name;
                }
                else
                {
                    var targetDir = output ?? currentDir;
                    var defaultName = EnsureAvailableName(DefaultNameFor(entry), targetDir, entry);
                    name = useDefaults ? defaultName : await PromptNameAsync(defaultName, cancellationToken);
                }
            }

            // 2a. Validate the resolved name is a safe single path segment before it is used to build
            // the default output path or passed to dotnet new. This fails fast (exit code 2) on names
            // that would escape the current directory (e.g. "..\Escaped") or are otherwise invalid.
            if (!IsValidProjectName(name))
            {
                var nameError = $"Invalid name '{name}'. Use a simple name without path separators or invalid filename characters.";
                if (isJson)
                {
                    PrintJson(false, entry.ShortName, name ?? string.Empty, (output ?? currentDir).FullName, nameError, entry.IsExperimental);
                }
                else
                {
                    logger.LogError("{Error} {Detail}", UiSymbols.Error, nameError);
                }
                return ExitInvalidArgs;
            }

            // 3. Resolve the output directory. Project templates default to ./<name>; item templates are
            // added into the existing project, so they default to the current directory.
            var outputDir = output ?? (entry.IsItem
                ? currentDir
                : new DirectoryInfo(Path.Join(currentDir.FullName, name!)));

            // 3a. Preflight a non-empty output directory for project templates only (item templates are
            // intentionally created into an existing, non-empty project). dotnet new --force only
            // overwrites files the template conflicts on; it never refuses a merely non-empty directory,
            // so honour the documented --force contract here.
            if (!force && entry.IsProject)
            {
                bool outputIsNonEmpty;
                try
                {
                    outputIsNonEmpty = outputDir.Exists && outputDir.EnumerateFileSystemInfos().Any();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Enumerating a caller-selected directory can fail (e.g. a protected or locked
                    // folder). Surface it as the structured failure/exit code instead of letting it
                    // escape to Program's generic handler, which would print plain stderr and break
                    // the --json contract.
                    var accessError = $"Could not inspect output directory '{outputDir.FullName}': {ex.Message}";
                    if (isJson)
                    {
                        PrintJson(false, entry.ShortName, name!, outputDir.FullName, accessError, entry.IsExperimental);
                    }
                    else
                    {
                        logger.LogError("{Error} {Detail}", UiSymbols.Error, accessError);
                    }
                    return ExitInvalidArgs;
                }

                if (outputIsNonEmpty)
                {
                    var dirError = $"Output directory '{outputDir.FullName}' is not empty. Use --force to scaffold into it anyway.";
                    if (isJson)
                    {
                        PrintJson(false, entry.ShortName, name!, outputDir.FullName, dirError, entry.IsExperimental);
                    }
                    else
                    {
                        logger.LogError("{Error} {Detail}", UiSymbols.Error, dirError);
                    }
                    return ExitInvalidArgs;
                }
            }

            // 4. Scaffold via dotnet new from the working directory (whose global.json chain governs the
            // SDK the scaffold and later `dotnet build` resolve), pinning the TFM to that SDK.

            // Pass each token via ArgumentList (injection-safe) so a crafted --name or --output cannot
            // inject additional dotnet new options.
            var args = new List<string> { "new", entry.ShortName, "-n", name!, "-o", outputDir.FullName };

            // Target framework, resolved and validated in step 1a (empty for item templates, which take
            // no framework, and for project templates whose metadata declares nothing to pin).
            args.AddRange(frameworkArgs);

            if (force)
            {
                args.Add("--force");
            }

            // Show a transient spinner while dotnet new runs (interactive terminals only); it clears
            // once scaffolding completes and WriteSuccess prints the "Created"/"Added" line in its place.
            var verb = entry.IsItem ? "Adding" : "Creating";
            var scaffoldStatus = $"{verb} {name} from {entry.ShortName}...";

            int exitCode;
            string stdout;
            string stderr;
            if (logger.IsEnabled(LogLevel.Debug))
            {
                // Verbose: stream dotnet new's output live so its post-creation actions (restore,
                // package add, etc.) are visible as they run. Buffering them behind the spinner hides
                // the very output needed to diagnose a failing post action (#753). The lines are still
                // captured into stdout/stderr so a non-zero exit can surface a concise failure detail
                // below, and LogDotnetOutput is skipped to avoid echoing the same text twice. Streaming
                // the real output also supersedes the spinner's delayed "restoring…" status message.
                logger.LogDebug("dotnet {Args}", string.Join(' ', args));
                (exitCode, stdout, stderr) = await dotNetService.RunDotnetCommandAsync(
                    workingDir,
                    args,
                    onOutputLine: line => logger.LogDebug("{Output}", line),
                    onErrorLine: line => logger.LogDebug("{Output}", line),
                    cancellationToken: cancellationToken);
                logger.LogDebug("dotnet new exited with code {ExitCode}", exitCode);
            }
            else
            {
                (exitCode, stdout, stderr) = await WithSpinnerAsync(
                    scaffoldStatus,
                    "Setting up the project; missing NuGet packages are restoring…",
                    ScaffoldStatusDelay,
                    () => dotNetService.RunDotnetCommandAsync(workingDir, args, cancellationToken: cancellationToken));
                LogDotnetOutput(args, exitCode, stdout, stderr);
            }
            if (exitCode != 0)
            {
                var detail = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim();
                if (isJson)
                {
                    PrintJson(false, entry.ShortName, name!, outputDir.FullName,
                        $"dotnet new failed (exit code {exitCode}): {detail}", entry.IsExperimental);
                }
                else
                {
                    logger.LogError("{Error} Failed to scaffold WinUI {Kind}: {Detail}", UiSymbols.Error, ProjectKind(entry), detail);
                }
                return ExitScaffoldFailed;
            }

            if (isJson)
            {
                PrintJson(true, entry.ShortName, name!, outputDir.FullName, null, entry.IsExperimental, packVersion);
            }
            else if (!quiet)
            {
                WriteSuccess(entry, name!, outputDir, currentDir);
            }

            return ExitSuccess;
        }

        /// <summary>Classifies the raw <c>--template-version</c> value into a mode plus explicit version.</summary>
        private static (VersionMode Mode, string? ExplicitVersion) ClassifyVersion(string? raw)
        {
            if (raw is null)
            {
                return (VersionMode.Default, null);
            }

            var trimmed = raw.Trim();
            if (trimmed.Equals(LatestVersionKeyword, StringComparison.OrdinalIgnoreCase))
            {
                return (VersionMode.Latest, null);
            }

            if (trimmed.Equals(InstalledVersionKeyword, StringComparison.OrdinalIgnoreCase))
            {
                return (VersionMode.Installed, null);
            }

            return (VersionMode.Explicit, trimmed);
        }

        private async Task<(bool Ok, string? Error)> InstallPackWithSpinnerAsync(
            DirectoryInfo cwd, string? version, Version sdkVersion, bool isJson, bool quiet, CancellationToken cancellationToken)
            => await WithSpinnerAsync(
                "Installing WinUI template pack...",
                () => InstallPackAsync(cwd, version, sdkVersion, isJson, quiet, cancellationToken));

        /// <summary>
        /// Resolves and installs the template pack according to <paramref name="mode"/>, returning the
        /// version now in use. See <see cref="VersionMode"/> for the per-mode behaviour. In
        /// non-interactive contexts (<paramref name="useDefaults"/>) or when only listing
        /// (<paramref name="forListing"/>) a stale installed pack is kept rather than prompting.
        /// </summary>
        private async Task<(bool Ok, string? Version, string? Error)> ResolveTemplatePackAsync(
            DirectoryInfo cwd, VersionMode mode, string? explicitVersion, bool useDefaults, bool forListing,
            Version sdkVersion, bool isJson, bool quiet, CancellationToken cancellationToken)
        {
            var installed = await QueryInstalledPackVersionAsync(cwd, cancellationToken);

            switch (mode)
            {
                case VersionMode.Installed:
                    if (installed is null)
                    {
                        return (false, null,
                            $"No WinUI template pack is installed to use with '--template-version {InstalledVersionKeyword}'. Re-run without it (or with '--template-version {LatestVersionKeyword}') to install the latest pack.");
                    }
                    return (true, installed, null);

                case VersionMode.Explicit:
                {
                    // An explicit --template-version is a hard request: reuse the installed pack only
                    // when it is exactly that version. Any other installed version (older OR newer) does
                    // not satisfy the request, so install exactly what was asked for. This keeps
                    // scaffolding reproducible across machines even when a newer pack is already present
                    // (which means downgrading the shared global pack when the caller pinned an older one).
                    if (installed is not null
                        && NuGetVersionHelper.Compare(installed, explicitVersion!) is 0)
                    {
                        return (true, installed, null);
                    }

                    var (ok, err) = await InstallPackWithSpinnerAsync(cwd, explicitVersion, sdkVersion, isJson, quiet, cancellationToken);
                    return ok ? (true, explicitVersion, null) : (false, null, err);
                }

                case VersionMode.Latest:
                {
                    var (ok, err) = await InstallPackWithSpinnerAsync(cwd, version: null, sdkVersion, isJson, quiet, cancellationToken);
                    if (!ok)
                    {
                        return (false, null, err);
                    }
                    return (true, await QueryInstalledPackVersionAsync(cwd, cancellationToken) ?? installed, null);
                }

                case VersionMode.Default:
                default:
                {
                    if (installed is null)
                    {
                        // Nothing installed: always grab the latest.
                        var (ok, err) = await InstallPackWithSpinnerAsync(cwd, version: null, sdkVersion, isJson, quiet, cancellationToken);
                        if (!ok)
                        {
                            return (false, null, err);
                        }
                        return (true, await QueryInstalledPackVersionAsync(cwd, cancellationToken), null);
                    }

                    // Installed: only offer an update when the pack is actually behind the feed. The
                    // feed round-trip is throttled to once a day so back-to-back invocations (e.g. list
                    // then scaffold) don't each pay the latency; between checks the cached result is reused.
                    string? latest;
                    if (templateUpdateThrottle.TryGetRecentLatest(installed, out var cachedLatest))
                    {
                        latest = cachedLatest;
                    }
                    else
                    {
                        var (checkSucceeded, feedLatest) = await WithSpinnerAsync(
                            "Checking for WinUI template pack updates...",
                            () => GetLatestAvailableVersionAsync(cwd, installed, cancellationToken));
                        latest = feedLatest;

                        // Only throttle a check that actually reached the feed. A transient failure must
                        // retry on the next run rather than be cached as "up-to-date" for a day.
                        if (checkSucceeded)
                        {
                            templateUpdateThrottle.Record(installed, latest);
                        }
                    }

                    var isStale = latest is not null
                        && NuGetVersionHelper.Compare(installed, latest) is int cmp && cmp < 0;

                    if (!isStale)
                    {
                        return (true, installed, null);
                    }

                    // In non-interactive contexts (or when merely listing) keep the installed pack:
                    // updating it is a global, machine-wide side effect the caller didn't opt into.
                    if (useDefaults || forListing)
                    {
                        return (true, installed, null);
                    }

                    var update = await PromptUpdateAsync(installed!, latest!, cancellationToken);
                    if (!update)
                    {
                        return (true, installed, null);
                    }

                    var (updated, updateErr) = await InstallPackWithSpinnerAsync(cwd, latest, sdkVersion, isJson, quiet, cancellationToken);
                    return updated ? (true, latest, null) : (false, null, updateErr);
                }
            }
        }

        /// <summary>
        /// Echoes a dotnet invocation and its captured output under <c>--verbose</c> (Debug). No-op at
        /// higher log levels, so default/quiet/json runs stay clean. Keeps the buffered call sites (which
        /// still parse stdout) unchanged while making the underlying dotnet commands visible for diagnostics.
        /// </summary>
        private void LogDotnetOutput(IReadOnlyList<string> args, int exitCode, string stdout, string stderr)
        {
            if (!logger.IsEnabled(LogLevel.Debug))
            {
                return;
            }

            logger.LogDebug("dotnet {Args} (exit {ExitCode})", string.Join(' ', args), exitCode);

            var stdoutTrimmed = stdout?.TrimEnd();
            if (!string.IsNullOrEmpty(stdoutTrimmed))
            {
                logger.LogDebug("{Output}", stdoutTrimmed);
            }

            var stderrTrimmed = stderr?.TrimEnd();
            if (!string.IsNullOrEmpty(stderrTrimmed))
            {
                logger.LogDebug("{Output}", stderrTrimmed);
            }
        }

        /// <summary>
        /// Runs <paramref name="operation"/> under an animated Spectre status spinner in interactive
        /// terminals, or plainly (no spinner) when a live display would be inappropriate (--quiet,
        /// --json, CI, AI-agent captures, redirected output). The spinner is transient: it clears once
        /// the operation completes, so callers print their own result line afterwards.
        /// </summary>
        private async Task<T> WithSpinnerAsync<T>(string message, Func<Task<T>> operation)
        {
            if (!ProgressDisplay.ShouldUseLiveSpinner(ansiConsole, logger))
            {
                return await operation();
            }

            return await ansiConsole.Status()
                .AutoRefresh(true)
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("blue"))
                .StartAsync(message, async _ => await operation());
        }

        private async Task<T> WithSpinnerAsync<T>(
            string message,
            string delayedMessage,
            TimeSpan delay,
            Func<Task<T>> operation)
        {
            if (!ProgressDisplay.ShouldUseLiveSpinner(ansiConsole, logger))
            {
                return await operation();
            }

            return await ansiConsole.Status()
                .AutoRefresh(true)
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("blue"))
                .StartAsync(message, async context =>
                {
                    var operationTask = operation();
                    return await AwaitWithDelayedActionAsync(
                        operationTask,
                        Task.Delay(delay),
                        () => context.Status(delayedMessage));
                });
        }

        internal static TimeSpan ScaffoldStatusDelay => TimeSpan.FromSeconds(10);

        internal static async Task<T> AwaitWithDelayedActionAsync<T>(
            Task<T> operationTask,
            Task delayTask,
            Action delayedAction)
        {
            if (await Task.WhenAny(operationTask, delayTask) == delayTask
                && !operationTask.IsCompleted)
            {
                delayedAction();
            }

            return await operationTask;
        }

        /// <summary>Returns the installed WinUI pack version, or <c>null</c> when the pack isn't installed.</summary>
        private async Task<string?> QueryInstalledPackVersionAsync(DirectoryInfo cwd, CancellationToken cancellationToken)
        {
            var (exit, output, stderr) = await dotNetService.RunDotnetCommandAsync(cwd, ListTemplatePacksArgs, EnglishUiEnvironment, cancellationToken: cancellationToken);
            LogDotnetOutput(ListTemplatePacksArgs, exit, output, stderr);
            return exit == 0 && TryGetInstalledPackVersion(output, out var version) ? version : null;
        }

        /// <summary>
        /// Checks the NuGet feed for a newer WinUI pack via <c>dotnet new update --check-only</c> (which
        /// resolves through the caller's configured feeds and surfaces prerelease updates).
        /// <para>
        /// <c>Succeeded</c> is <see langword="false"/> when the check couldn't produce an authoritative
        /// result — a non-zero exit (feed unreachable) or exit 0 with output we can't interpret (an SDK
        /// output-format change, truncated stdout) — so the caller avoids caching a non-result as
        /// "up-to-date" for a day; <c>Latest</c> is the newest available version, or <see langword="null"/>
        /// when the pack is already up-to-date.
        /// </para>
        /// </summary>
        private async Task<(bool Succeeded, string? Latest)> GetLatestAvailableVersionAsync(DirectoryInfo cwd, string installed, CancellationToken cancellationToken)
        {
            var (exit, output, stderr) = await dotNetService.RunDotnetCommandAsync(cwd, UpdateCheckArgs, EnglishUiEnvironment, cancellationToken: cancellationToken);
            LogDotnetOutput(UpdateCheckArgs, exit, output, stderr);
            if (exit != 0)
            {
                return (false, null);
            }

            var (outcome, _, latest) = WinUiTemplateCatalog.ParseUpdateCheck(output, TemplatePackageId);
            if (outcome == UpdateCheckOutcome.Unrecognized)
            {
                // Exit 0 but output we can't interpret: don't cache it as authoritative — retry next run.
                return (false, null);
            }

            return (true, latest);
        }

        /// <summary>
        /// Installs the WinUI template pack. A <c>null</c> <paramref name="version"/> installs the latest
        /// published version (floating to newest stable-or-prerelease); an explicit version pins it. The
        /// <c>dotnet new install</c> separator is chosen from the detected SDK band.
        /// </summary>
        private async Task<(bool Ok, string? Error)> InstallPackAsync(
            DirectoryInfo cwd, string? version, Version sdkVersion, bool isJson, bool quiet, CancellationToken cancellationToken)
        {
            if (!isJson && !quiet && !ProgressDisplay.ShouldUseLiveSpinner(ansiConsole, logger))
            {
                logger.LogInformation("{Info}  Installing WinUI template pack {Pack}...", UiSymbols.Info, TemplatePackageId);
            }

            string packageArg;
            if (version is null)
            {
                packageArg = TemplatePackageId;
            }
            else
            {
                var separator = sdkVersion >= FirstAtSeparatorSdkVersion ? "@" : "::";
                packageArg = $"{TemplatePackageId}{separator}{version}";
            }

            var (installExit, installOut, installErr) = await dotNetService.RunDotnetCommandAsync(
                cwd, new[] { "new", "install", packageArg }, cancellationToken: cancellationToken);
            LogDotnetOutput(new[] { "new", "install", packageArg }, installExit, installOut, installErr);
            if (installExit != 0)
            {
                var detail = !string.IsNullOrWhiteSpace(installErr) ? installErr.Trim() : installOut.Trim();
                if (!isJson)
                {
                    logger.LogError("{Error} Failed to install the WinUI template pack: {Detail}", UiSymbols.Error, detail);
                }

                var error = $"Failed to install the WinUI template pack '{packageArg}' (dotnet new install exit code {installExit})";
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    error += $": {detail}";
                }

                return (false, error);
            }

            return (true, null);
        }

        /// <summary>
        /// Runs <c>dotnet new list</c> in <paramref name="contextDir"/> and parses the WinUI templates.
        /// On a non-zero exit, returns an empty list plus a <c>FailureDetail</c> carrying the dotnet
        /// diagnostic (stderr/stdout + exit code) so the caller can surface it instead of a generic message.
        /// </summary>
        private async Task<(IReadOnlyList<WinUiTemplateEntry> Templates, string? FailureDetail)> EnumerateTemplatesAsync(DirectoryInfo contextDir, CancellationToken cancellationToken)
        {
            var (exit, output, stderr) = await dotNetService.RunDotnetCommandAsync(contextDir, ListTemplatesArgs, EnglishUiEnvironment, cancellationToken: cancellationToken);
            LogDotnetOutput(ListTemplatesArgs, exit, output, stderr);
            if (exit != 0)
            {
                var detail = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : output.Trim();
                var failure = $"dotnet new list failed (exit code {exit})";
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    failure += $": {detail}";
                }

                return ([], failure);
            }

            var parsed = WinUiTemplateCatalog.ParseList(output);

            // `dotnet new list winui` matches by short-name prefix across *every* installed pack and
            // formats its output into fixed-width columns, so the parsed rows can both include
            // templates we don't own and carry names truncated mid-word ("Reactor NavigationView App
            // (Experim..."). Reconcile them against `dotnet new uninstall` — the authoritative,
            // unformatted per-package Templates block — to drop foreign templates and restore real
            // names. Ownership must be *established*, never assumed: if that command fails or its
            // output can't be parsed we cannot tell an official template from a third-party one that
            // borrowed an official alias, so fail the enumeration instead of offering rows of unknown
            // provenance under the "official Windows App SDK template" banner.
            var (packExit, packOutput, packStderr) = await dotNetService.RunDotnetCommandAsync(contextDir, ListTemplatePacksArgs, EnglishUiEnvironment, cancellationToken: cancellationToken);
            LogDotnetOutput(ListTemplatePacksArgs, packExit, packOutput, packStderr);
            if (packExit != 0)
            {
                var packDetail = !string.IsNullOrWhiteSpace(packStderr) ? packStderr.Trim() : packOutput.Trim();
                var packFailure = $"Could not verify which templates '{TemplatePackageId}' owns: dotnet new uninstall failed (exit code {packExit})";
                if (!string.IsNullOrWhiteSpace(packDetail))
                {
                    packFailure += $": {packDetail}";
                }

                return ([], packFailure);
            }

            var packRows = WinUiTemplateCatalog.ParsePackTemplates(packOutput, TemplatePackageId);
            if (packRows.Count == 0)
            {
                return ([], $"Could not verify which templates '{TemplatePackageId}' owns: no template list for it in 'dotnet new uninstall' output. Re-run with --verbose to see that output.");
            }

            return (WinUiTemplateCatalog.RestrictToPack(parsed, packRows), null);
        }

        private async Task<WinUiTemplateEntry> PromptTemplateAsync(IReadOnlyList<WinUiTemplateEntry> templates, CancellationToken cancellationToken)
        {
            var labels = templates.Select(FormatChoice).ToList();

            var prompt = new SelectionPrompt<string>()
                .Title("Which WinUI template would you like to use?")
                .AddChoices(labels);

            var selected = await ansiConsole.PromptAsync(prompt, cancellationToken);
            ansiConsole.MarkupLineInterpolated($"Which WinUI template would you like to use? [underline]{selected}[/]");
            var index = labels.IndexOf(selected);
            return templates[index];
        }

        /// <summary>Human-friendly choice label combining the display name and canonical short name.</summary>
        private static string FormatChoice(WinUiTemplateEntry entry)
        {
            var name = DisplayLabel(entry);
            return entry.IsItem ? $"{name} (item — {entry.ShortName})" : $"{name} ({entry.ShortName})";
        }

        /// <summary>
        /// The name to show for a template, always carrying an "(Experimental)" marker for a prerelease
        /// template. The pack's own names already include it, so it is only appended when missing — that
        /// way an experimental template is never presented as stable even if a future pack drops the
        /// suffix and only the tag identifies it.
        /// </summary>
        private static string DisplayLabel(WinUiTemplateEntry entry)
        {
            var name = string.IsNullOrEmpty(entry.DisplayName) ? entry.ShortName : entry.DisplayName;
            return entry.IsExperimental && !name.Contains("Experimental", StringComparison.OrdinalIgnoreCase)
                ? $"{name} (Experimental)"
                : name;
        }

        private async Task<string> PromptNameAsync(string defaultName, CancellationToken cancellationToken)
        {
            // Reuse the same validation the handler applies so invalid interactive input (empty,
            // path separators, "..", reserved device names, etc.) is corrected in place instead of
            // being accepted here and then rejected after the wizard completes.
            var prompt = new TextPrompt<string>("What should the app be named?")
                .DefaultValue(defaultName)
                .Validate(value => IsValidProjectName(value)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("Use a simple name without path separators or invalid filename characters."));

            return await ansiConsole.PromptAsync(prompt, cancellationToken);
        }

        /// <summary>
        /// Prompts whether to update a stale template pack. Deliberately has <b>no default</b>: updating
        /// the pack is a global, machine-wide side effect, so the caller must answer explicitly.
        /// </summary>
        private async Task<bool> PromptUpdateAsync(string installed, string latest, CancellationToken cancellationToken)
        {
            var prompt = new TextPrompt<bool>($"A newer WinUI template pack is available ({installed} \u2192 {latest}). Update?")
                .AddChoice(true)
                .AddChoice(false)
                .WithConverter(value => value ? "y" : "n");

            return await ansiConsole.PromptAsync(prompt, cancellationToken);
        }

        /// <summary>Writes the success message and a template-specific next step.</summary>
        private void WriteSuccess(WinUiTemplateEntry entry, string name, DirectoryInfo outputDir, DirectoryInfo currentDir)
        {
            if (entry.IsItem)
            {
                ansiConsole.MarkupLineInterpolated($"{UiSymbols.Check} Added [green]{name}[/] to the project in [blue]{outputDir.FullName}[/].");
                return;
            }

            var relative = Path.GetRelativePath(currentDir.FullName, outputDir.FullName);
            ansiConsole.MarkupLineInterpolated($"{UiSymbols.Check} Created WinUI {ProjectKind(entry)} [green]{name}[/] at [blue]{outputDir.FullName}[/].");

            if (entry.IsExperimental)
            {
                ansiConsole.MarkupLineInterpolated($"{UiSymbols.Warning}  [yellow]{entry.ShortName}[/] is an experimental template. It depends on prerelease packages whose APIs can change or be removed in a future release.");
            }

            if (entry.HasTag("Library"))
            {
                ansiConsole.MarkupLineInterpolated($"{UiSymbols.Next} Next: from your app project, run [blue]dotnet add reference \"{Path.Join(outputDir.FullName, name + ".csproj")}\"[/].");
            }
            else if (entry.HasTag("Test"))
            {
                ansiConsole.MarkupLineInterpolated($"{UiSymbols.Next} Next: [blue]cd \"{relative}\"[/] then [blue]winapp run[/] — this packaged MSTest app runs its tests when launched.");
            }
            else
            {
                ansiConsole.MarkupLineInterpolated($"{UiSymbols.Next} Next: [blue]cd \"{relative}\"[/] then [blue]winapp run[/].");
            }
        }

        /// <summary>Writes the human-readable template list for <c>--list</c>.</summary>
        private void WriteTemplateList(IReadOnlyList<WinUiTemplateEntry> templates, string? packVersion)
        {
            var versionSuffix = string.IsNullOrEmpty(packVersion) ? string.Empty : $" (pack {packVersion})";
            ansiConsole.MarkupLineInterpolated($"{UiSymbols.Info}  Available WinUI templates{versionSuffix}:");

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Template");
            table.AddColumn("Short name");
            table.AddColumn("Type");

            foreach (var entry in templates)
            {
                table.AddRow(
                    Markup.Escape(DisplayLabel(entry)),
                    Markup.Escape(entry.ShortName),
                    Markup.Escape(entry.Type));
            }

            ansiConsole.Write(table);

            if (templates.Any(t => t.IsExperimental))
            {
                ansiConsole.MarkupLineInterpolated($"{UiSymbols.Warning}  Templates marked (Experimental) depend on prerelease packages whose APIs can change or be removed in a future release.");
            }
        }

        /// <summary>
        /// Walks up from <paramref name="dir"/> to the nearest directory that already exists on disk, so
        /// commands whose <c>global.json</c> resolution must match the eventual project can be run from a
        /// real working directory even when the output directory hasn't been created yet. Returns
        /// <c>null</c> only if no ancestor exists (e.g. a drive that isn't mounted).
        /// </summary>
        private static DirectoryInfo? NearestExistingAncestor(DirectoryInfo dir)
        {
            for (var d = dir; d is not null; d = d.Parent)
            {
                if (d.Exists)
                {
                    return d;
                }
            }

            return null;
        }

        /// <summary>
        /// Fills <paramref name="args"/> with the target-framework option for a project template, using
        /// the template's own metadata (<c>templatecache.json</c>) to choose both the option name and a
        /// framework the installed SDK can build, and returns an error string when the template supports
        /// no framework this SDK can build.
        /// <para>
        /// Nothing is hard-coded: older packs named the option <c>--dotnetVersion</c> rather than
        /// <c>--dotnet-version</c>, and the offered frameworks change as packs add newer ones. Pinning
        /// matters because the templates default to the newest TFM, so an accepted-but-older SDK would
        /// otherwise scaffold a project it cannot build. When the template offers frameworks but all of
        /// them are newer than the SDK (the Reactor templates offer only <c>net10.0</c>), there is
        /// nothing safe to pin and the scaffold would produce an unbuildable project — so that is
        /// reported as a failure rather than silently allowed. Falls back to the historical heuristic
        /// (<c>--dotnet-version net{major}.0</c> for SDK 8-10) only when no cache describes the template.
        /// </para>
        /// </summary>
        private string? ResolveTargetFrameworkArgs(List<string> args, WinUiTemplateEntry entry, Version sdkVersion)
        {
            foreach (var cacheJson in templateCacheReader.ReadTemplateCacheDocuments())
            {
                var (found, optionName, tfm, choices) = WinUiTemplateCatalog.DeriveTfmOption(
                    cacheJson, TemplatePackageId, entry.ShortName, sdkVersion.Major);
                if (!found)
                {
                    continue;
                }

                // The template was located: its metadata is authoritative. Pin only when it declares a
                // framework choice the SDK can satisfy.
                if (!string.IsNullOrEmpty(optionName) && !string.IsNullOrEmpty(tfm))
                {
                    args.Add("--" + optionName);
                    args.Add(tfm);
                    return null;
                }

                if (tfm is null && WinUiTemplateCatalog.MinimumSdkMajor(choices) is { } minimumMajor)
                {
                    return $"Template '{entry.ShortName}' requires the .NET {minimumMajor} SDK or newer, but .NET SDK {sdkVersion} is installed. "
                        + $"Install .NET {minimumMajor} from https://dotnet.microsoft.com/download, then re-run 'winapp new'.";
                }

                // The template declares no framework choice — there is nothing to pin.
                return null;
            }

            // No cache described the template — fall back to the previous heuristic.
            if (sdkVersion.Major is >= 8 and <= 10)
            {
                args.Add("--dotnet-version");
                args.Add($"net{sdkVersion.Major}.0");
            }

            return null;
        }

        /// <summary>Human-facing noun for the created artifact, derived from the template's tags.</summary>
        private static string ProjectKind(WinUiTemplateEntry entry)
        {
            if (entry.IsItem)
            {
                return "item";
            }

            if (entry.HasTag("Library"))
            {
                return "class library";
            }

            if (entry.HasTag("Test"))
            {
                return "unit test project";
            }

            return "app";
        }

        /// <summary>
        /// The default project/item name for <paramref name="entry"/> before availability numbering.
        /// Project templates use the friendly "WinUIApp"; item templates derive a noun from the template's
        /// display name (e.g. "WinUI Blank Page (Item)" → "MyPage", "WinUI Window (Item)" → "MyWindow")
        /// since "WinUIApp" is nonsensical for a page/window/control added into an existing project.
        /// </summary>
        private static string DefaultNameFor(WinUiTemplateEntry entry)
            => entry.IsItem ? DeriveItemDefaultName(entry.DisplayName) : "WinUIApp";

        /// <summary>
        /// Derives a friendly item default name ("My" + the last meaningful word of the display name).
        /// Strips the "(Item)" suffix and the "WinUI" prefix, keeps only alphanumeric words, and falls
        /// back to "MyItem" when nothing usable remains.
        /// </summary>
        private static string DeriveItemDefaultName(string displayName)
        {
            var words = (displayName ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
                .Where(w => w.Length > 0
                    && !w.Equals("WinUI", StringComparison.OrdinalIgnoreCase)
                    && !w.Equals("Item", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (words.Count == 0)
            {
                return "MyItem";
            }

            var last = words[^1];
            return "My" + char.ToUpperInvariant(last[0]) + last[1..];
        }

        /// <summary>
        /// Returns the first available variant of <paramref name="baseName"/> in <paramref name="parentDir"/>,
        /// appending an incrementing suffix ("WinUIApp", "WinUIApp1", "WinUIApp2", ...) when a name is
        /// already taken. For project templates a name is "taken" when a directory of that name exists; for
        /// item templates when a same-named file (e.g. "MyPage.xaml") already exists in the target folder.
        /// Only used for defaulted/prompted names — an explicit --name is honoured verbatim.
        /// </summary>
        private static string EnsureAvailableName(string baseName, DirectoryInfo parentDir, WinUiTemplateEntry entry)
        {
            bool IsTaken(string candidate)
            {
                try
                {
                    if (entry.IsItem)
                    {
                        return parentDir.Exists && parentDir.EnumerateFiles(candidate + ".*").Any();
                    }

                    return Directory.Exists(Path.Join(parentDir.FullName, candidate));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // If we can't inspect the directory, don't block on numbering — fall through and let
                    // the later preflight/scaffold surface any real conflict with a structured error.
                    return false;
                }
            }

            if (!IsTaken(baseName))
            {
                return baseName;
            }

            for (var i = 1; ; i++)
            {
                var candidate = baseName + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!IsTaken(candidate))
                {
                    return candidate;
                }
            }
        }

        /// <summary>
        /// Verifies the .NET SDK is installed and meets the minimum version. Returns the detected SDK
        /// version, or <c>null</c> (without installing anything) with a specific failure reason if the
        /// SDK is missing, too old, or its version can't be determined. Runs <c>dotnet --version</c> in
        /// <paramref name="probeDir"/> so <c>global.json</c> is resolved from the project's own
        /// directory chain rather than the caller's working directory.
        /// </summary>
        private async Task<(Version? Version, string? Error)> CheckDotnetSdkAsync(DirectoryInfo probeDir, bool isJson, CancellationToken cancellationToken)
        {
            int exitCode;
            string stdout;
            string stderr = string.Empty;
            try
            {
                (exitCode, stdout, stderr) = await dotNetService.RunDotnetCommandAsync(probeDir, VersionArgs, cancellationToken: cancellationToken);
            }
            catch (Win32Exception)
            {
                // dotnet executable not found on PATH.
                exitCode = -1;
                stdout = string.Empty;
            }

            LogDotnetOutput(VersionArgs, exitCode, stdout, stderr);

            if (exitCode != 0)
            {
                const string missing = "The .NET SDK is required to create a WinUI app. Install it from https://dotnet.microsoft.com/download, then re-run 'winapp new'.";
                if (!isJson)
                {
                    logger.LogError("{Error} {Message}", UiSymbols.Error, missing);
                }
                return (null, missing);
            }

            var versionText = stdout.Trim();
            // Strip any prerelease suffix (e.g. 9.0.100-preview.1) before parsing.
            var dashIndex = versionText.IndexOf('-');
            if (dashIndex > 0)
            {
                versionText = versionText[..dashIndex];
            }

            if (Version.TryParse(versionText, out var installed))
            {
                if (installed < MinimumSdkVersion)
                {
                    var tooOld = $".NET SDK {installed} is installed, but {MinimumSdkVersion} or newer is required. Update from https://dotnet.microsoft.com/download, then re-run 'winapp new'.";
                    if (!isJson)
                    {
                        logger.LogError("{Error} {Message}", UiSymbols.Error, tooOld);
                    }
                    return (null, tooOld);
                }

                return (installed, null);
            }

            // Unparseable output means we can't confirm a usable SDK — fail the prerequisite rather
            // than optimistically proceeding into a confusing `dotnet new` error.
            var unparseable = $"Could not determine the installed .NET SDK version (got '{stdout.Trim()}'). Ensure the .NET SDK {MinimumSdkVersion} or newer is installed from https://dotnet.microsoft.com/download, then re-run 'winapp new'.";
            if (!isJson)
            {
                logger.LogError("{Error} {Message}", UiSymbols.Error, unparseable);
            }
            return (null, unparseable);
        }

        private void PrintJson(bool created, string? templateShortName, string name, string path, string? error, bool? experimental = null, string? templateVersion = null)
        {
            var result = new NewCommandResult
            {
                Created = created,
                Template = templateShortName,
                Experimental = experimental,
                Name = name,
                Path = path,
                TemplateVersion = templateVersion,
                Error = error
            };

            var json = JsonSerializer.Serialize(result, NewCommandJsonContext.Default.NewCommandResult);
            // Write via the raw output writer (not ansiConsole.WriteLine) so Spectre does not
            // word-wrap the JSON to the console width and corrupt it on narrow terminals.
            ansiConsole.Profile.Out.Writer.WriteLine(json);
        }

        private void PrintListJson(string? templateVersion, IReadOnlyList<WinUiTemplateEntry>? templates, string? error)
        {
            var result = new NewListResult
            {
                Listed = error is null,
                TemplateVersion = templateVersion,
                Templates = templates?.Select(t => new NewTemplateInfo
                {
                    ShortName = t.ShortName,
                    Aliases = t.ShortNames.ToArray(),
                    DisplayName = t.DisplayName,
                    Type = t.Type,
                    Tags = t.Tags,
                    Experimental = t.IsExperimental,
                }).ToList(),
                Error = error,
            };

            var json = JsonSerializer.Serialize(result, NewCommandJsonContext.Default.NewListResult);
            ansiConsole.Profile.Out.Writer.WriteLine(json);
        }
    }
}

internal sealed class NewCommandResult
{
    public bool Created { get; set; }
    public string? Template { get; set; }

    /// <summary>
    /// True when the template is prerelease (e.g. the Reactor templates). Omitted entirely when the
    /// run failed before a template was resolved against the catalog, since <see cref="Template"/> is
    /// then only the raw requested value and reporting <c>false</c> would claim it is stable.
    /// </summary>
    public bool? Experimental { get; set; }
    public string? Name { get; set; }
    public string? Path { get; set; }
    public string? TemplateVersion { get; set; }
    public string? Error { get; set; }
}

internal sealed class NewTemplateInfo
{
    public string? ShortName { get; set; }
    public string[]? Aliases { get; set; }
    public string? DisplayName { get; set; }
    public string? Type { get; set; }
    public string? Tags { get; set; }

    /// <summary>True when the template is prerelease (e.g. the Reactor templates).</summary>
    public bool Experimental { get; set; }
}

internal sealed class NewListResult
{
    public bool Listed { get; set; }
    public string? TemplateVersion { get; set; }
    public List<NewTemplateInfo>? Templates { get; set; }
    public string? Error { get; set; }
}

[JsonSerializable(typeof(NewCommandResult))]
[JsonSerializable(typeof(NewListResult))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class NewCommandJsonContext : JsonSerializerContext;
