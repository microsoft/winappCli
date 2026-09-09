// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Input and solution resolution for <see cref="ProjectRunService" />: mapping a file/folder/solution
/// argument to a runnable project, and computing sibling-restore plans.
/// </summary>
internal sealed partial class ProjectRunService
{
    /// <inheritdoc />
    public async Task<RunInputResolution> ResolveInputAsync(FileSystemInfo input, CancellationToken cancellationToken, string? projectSelector = null, ProjectClassificationInputs? classificationInputs = null)
    {
        // Explicit file input: a .cs file-based app (single-file mode), a .csproj (project mode),
        // or a .sln/.slnx (solution mode).
        if (input is FileInfo file)
        {
            if (IsSingleFileApp(file))
            {
                // Single-file mode is reachable ONLY from an explicitly-typed .cs path — never from
                // directory inference — so folder mode's classification is completely untouched.
                if (!string.IsNullOrWhiteSpace(projectSelector))
                {
                    throw new ProjectRunException(
                        $"--project does not apply to '{file.Name}'. A .NET file-based app IS the project; omit --project.");
                }

                var singleFileDir = file.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory());
                return new RunInputResolution(WinAppRunMode.SingleFile, null, singleFileDir, SingleFile: file);
            }

            if (IsSolutionFile(file))
            {
                return await ResolveSolutionAsync(file, projectSelector, classificationInputs, cancellationToken);
            }

            if (!string.Equals(file.Extension, ".csproj", StringComparison.OrdinalIgnoreCase))
            {
                throw new ProjectRunException(
                    $"'{file.FullName}' is not a runnable input. Pass a .cs file-based app, a .csproj, a .sln/.slnx solution, a directory containing one, or a build-output folder.");
            }

            var projectDir = file.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory());

            // An explicit .csproj IS the project; a --project selector must agree with it rather than be
            // silently dropped (matching it is a harmless no-op).
            if (!string.IsNullOrWhiteSpace(projectSelector) && MatchProjectSelector([file], projectSelector, projectDir) is null)
            {
                throw new ProjectRunException(
                    $"--project '{projectSelector}' does not match the specified project '{file.Name}'. Omit --project when passing a .csproj directly.");
            }

            // A bare .csproj has no solution context, so $(SolutionDir) and sibling Solution* properties
            // are undefined — projects that reference them in imports/AdditionalFiles then fail to build.
            // Walk up to the owning solution so the build defines them as `dotnet build <sln>` / VS does.
            var owningSolution = FindOwningSolution(file);
            return new RunInputResolution(WinAppRunMode.Project, file, projectDir, owningSolution);
        }

        var dir = (DirectoryInfo)input;

        // A solution in the directory wins over loose .csproj files: it carries the config→platform map
        // and defines $(SolutionDir), which some projects need to build at all. Matches what VS opens.
        var solutions = CollapseSolutionMigrationPairs(SafeEnumerateFiles(dir, "*.sln", "*.slnx"));

        if (solutions.Count == 1)
        {
            // A discovered solution with no runnable C# project (native/library-only) degrades to folder
            // mode, so a build-output folder next to such a solution keeps working.
            return await ResolveSolutionAsync(solutions[0], projectSelector, classificationInputs, cancellationToken, allowFolderFallback: true);
        }

        if (solutions.Count > 1)
        {
            var slnNames = string.Join(", ", solutions.Select(s => s.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            throw new ProjectRunException(
                $"Multiple solution files found in '{dir.FullName}' ({slnNames}). Specify which one to run, e.g. 'winapp run {solutions[0].Name}'.");
        }

        var csprojs = SafeEnumerateFiles(dir, "*.csproj");

        // No top-level .csproj → folder mode (unchanged). Build-output folders (bin/…) fall here; this
        // path performs NO MSBuild evaluation.
        if (csprojs.Count == 0)
        {
            return new RunInputResolution(WinAppRunMode.Folder, null, dir);
        }

        if (csprojs.Count == 1)
        {
            // Honor an explicit --project even with only one .csproj, so a mismatched selector errors
            // rather than being silently ignored. An explicit selector is honored as-is (no runnability
            // gate). Attach the owning solution so $(SolutionDir) is defined as on the other paths.
            if (!string.IsNullOrWhiteSpace(projectSelector))
            {
                if (MatchProjectSelector(csprojs, projectSelector, dir) is null)
                {
                    throw new ProjectRunException(
                        $"--project '{projectSelector}' did not match '{csprojs[0].Name}' in '{dir.FullName}'.");
                }

                return new RunInputResolution(WinAppRunMode.Project, csprojs[0], dir, FindOwningSolution(csprojs[0]));
            }

            // Auto-selection: only switch to project mode when the lone project is actually runnable; a
            // non-runnable library beside build output stays in folder mode (G4 guarantee). Resolve the
            // owning solution FIRST so classification sees the same $(SolutionDir)/Solution* context the
            // build will (M1) — otherwise a project whose OutputType/IsTestProject is conditional on it
            // would misclassify. Reuse the solution for the final resolution to avoid walking twice.
            var loneOwningSolution = FindOwningSolution(csprojs[0]);
            var loneProps = BuildClassificationPropertyTokens(classificationInputs, loneOwningSolution);
            var (loneApps, loneTests) = await ClassifyRunnablesAsync(csprojs, dir, loneProps, classificationInputs, loneOwningSolution, cancellationToken);
            var lonePick = PickRunnableProject(loneApps, loneTests, out var lonePickedTest);
            if (lonePick is null)
            {
                // Non-runnable lone project → preserve existing folder-mode behavior unchanged.
                return new RunInputResolution(WinAppRunMode.Folder, null, dir);
            }

            if (lonePickedTest)
            {
                LogRunningLoneTestProject(lonePick, dir.FullName);
            }

            return new RunInputResolution(WinAppRunMode.Project, lonePick, dir, loneOwningSolution);
        }

        // A --project selector disambiguates directly without evaluation.
        if (!string.IsNullOrWhiteSpace(projectSelector))
        {
            var selected = MatchProjectSelector(csprojs, projectSelector, dir);
            if (selected is null)
            {
                // List only the projects the user can actually run (not every library) so the hint guides
                // them to a valid choice instead of a wall of non-runnable names.
                var available = await BuildRunnableAvailableHintAsync(
                    csprojs, dir, BuildClassificationPropertyTokens(classificationInputs, solution: null),
                    classificationInputs, solution: null, cancellationToken);
                throw new ProjectRunException(
                    $"--project '{projectSelector}' did not match a single .csproj in '{dir.FullName}'. Available: {available}.");
            }

            return new RunInputResolution(WinAppRunMode.Project, selected, dir, FindOwningSolution(selected), "matched --project");
        }

        // Multiple .csproj files — classify each via MSBuild evaluation so an executable/test project is
        // detected even when OutputType/IsTestProject come from an import (SDK defaults, Directory.Build.props,
        // the test SDK) a static parse can't see. Evaluation falls back to the static parse per-project when
        // the SDK/restore is unavailable. The effective build inputs (Configuration/arch/TFM/user -p) are
        // threaded in so a candidate whose markers are conditional on them classifies as it will build.
        var dirClassificationProps = BuildClassificationPropertyTokens(classificationInputs, solution: null);
        var (dirApps, dirTests) = await ClassifyRunnablesAsync(csprojs, dir, dirClassificationProps, classificationInputs, solution: null, cancellationToken);

        var dirPick = PickRunnableProject(dirApps, dirTests, out var dirPickedTest);
        if (dirPick is not null)
        {
            if (dirPickedTest)
            {
                LogRunningLoneTestProject(dirPick, dir.FullName);
            }

            return new RunInputResolution(WinAppRunMode.Project, dirPick, dir, FindOwningSolution(dirPick), "only runnable project");
        }

        // Zero or several runnable candidates → we cannot safely guess; require explicit selection.
        var names = string.Join(", ", csprojs.Select(c => c.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        throw new ProjectRunException(
            $"Multiple .csproj files found in '{dir.FullName}' ({names}). Specify which project to run, e.g. 'winapp run {csprojs[0].Name}' or --project <name>.");
    }

    /// <summary>
    /// Builds the "Available: …" hint for a failed <c>--project</c> match. Prefers the projects the user
    /// can actually run (apps first, then test projects) so a mismatch in a large solution doesn't dump a
    /// wall of library/build-only project names; falls back to every project name when classification
    /// surfaces nothing runnable, so the hint is never empty and still helps correct a typo. Runs only on
    /// the (already-failed) error path, so it adds no cost to a successful run.
    /// </summary>
    private async Task<string> BuildRunnableAvailableHintAsync(
        List<FileInfo> projects,
        DirectoryInfo workingDirectory,
        IReadOnlyList<string>? props,
        ProjectClassificationInputs? classificationInputs,
        FileInfo? solution,
        CancellationToken cancellationToken)
    {
        var (apps, tests) = await ClassifyRunnablesAsync(projects, workingDirectory, props, classificationInputs, solution, cancellationToken);
        var runnable = apps.Concat(tests).Select(p => p.Name).ToList();
        var names = runnable.Count > 0 ? runnable : projects.Select(p => p.Name);
        return FormatProjectNameList(names);
    }

    /// <summary>
    /// Maximum project names shown in a "which project?" error before the list is elided. A 100+ project
    /// solution otherwise emits an unreadable single-line wall of names; showing the first few alphabetically
    /// plus an accurate total keeps the message scannable while still conveying the scale (reviewer F5:
    /// "it gets long fast on a real solution").
    /// </summary>
    private const int MaxListedProjectNames = 12;

    /// <summary>
    /// Renders project names alphabetically as a comma-separated list, eliding the tail beyond
    /// <see cref="MaxListedProjectNames"/> as <c>… (+N more of M)</c> so the caller can still see the
    /// total. Used by every "specify --project" error so they stay consistent with each other.
    /// </summary>
    private static string FormatProjectNameList(IEnumerable<string> names)
    {
        var ordered = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        if (ordered.Count <= MaxListedProjectNames)
        {
            return string.Join(", ", ordered);
        }

        var shown = string.Join(", ", ordered.Take(MaxListedProjectNames));
        return $"{shown}, … (+{ordered.Count - MaxListedProjectNames} more of {ordered.Count})";
    }

    /// <summary>True when the file is a solution (<c>.sln</c> or the newer XML <c>.slnx</c>).</summary>
    private static bool IsSolutionFile(FileInfo file) =>
        string.Equals(file.Extension, ".sln", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(file.Extension, ".slnx", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the file is a .NET file-based app — a single <c>.cs</c> the SDK builds through a virtual
    /// <c>&lt;stem&gt;.cs.csproj</c>. Extension-only: the <c>#:</c> directives are optional (a bare
    /// <c>.cs</c> builds with SDK defaults), so requiring them would reject valid input.
    /// </summary>
    internal static bool IsSingleFileApp(FileInfo file) =>
        string.Equals(file.Extension, ".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Collapses same-basename <c>.sln</c>/<c>.slnx</c> pairs — Visual Studio leaves BOTH side by side
    /// during .slnx migration — into a single entry, preferring the newer <c>.slnx</c>. Solutions with
    /// distinct base names are left untouched, so a genuinely multi-solution folder still requires an
    /// explicit choice. Without this, a folder mid-migration is wrongly rejected as "multiple solutions".
    /// </summary>
    private static List<FileInfo> CollapseSolutionMigrationPairs(List<FileInfo> solutions)
    {
        if (solutions.Count <= 1)
        {
            return solutions;
        }

        return solutions
            .GroupBy(s => Path.GetFileNameWithoutExtension(s.Name), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
                group.FirstOrDefault(s => string.Equals(s.Extension, ".slnx", StringComparison.OrdinalIgnoreCase))
                ?? group.First())
            .ToList();
    }

    /// <summary>Matches any project entry in a classic <c>.sln</c>, capturing the (relative) project path (any type).</summary>
    [GeneratedRegex(
        "Project\\(\"\\{[^\"}]*\\}\"\\)\\s*=\\s*\"[^\"]*\",\\s*\"([^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SlnAnyProjectPathRegex();

    /// <summary>
    /// Enumerates top-level files in <paramref name="dir"/> matching any of <paramref name="patterns"/>
    /// (in order), returning an empty list when the directory can't be read (missing/locked/denied).
    /// </summary>
    private static List<FileInfo> SafeEnumerateFiles(DirectoryInfo dir, params string[] patterns)
    {
        try
        {
            return patterns
                .SelectMany(pattern => dir.EnumerateFiles(pattern, SearchOption.TopDirectoryOnly))
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    /// <summary>
    /// Finds the solution that owns a bare <c>.csproj</c> so a direct-file run defines <c>$(SolutionDir)</c>
    /// as <c>dotnet build &lt;sln&gt;</c> / VS do. Walks up from the project directory; at the nearest ancestor
    /// with any <c>.sln</c>/<c>.slnx</c>, prefers a solution that lists this project, else attaches a lone
    /// solution whose listing is indeterminate (empty/unreadable) by locality. A lone solution that CONFIRMS
    /// the project is absent — or several non-listing solutions — is not guessed; the walk continues upward,
    /// returning null when no solution demonstrably owns the project.
    /// </summary>
    private static FileInfo? FindOwningSolution(FileInfo csproj)
    {
        for (var dir = csproj.Directory; dir is not null; dir = dir.Parent)
        {
            var solutions = SafeEnumerateFiles(dir, "*.sln", "*.slnx")
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (solutions.Count == 0)
            {
                continue;
            }

            // Prefer a solution at this level that actually lists the project (deterministic: alpha order).
            var owning = solutions.FirstOrDefault(s => InspectSolutionOwnership(s, csproj) == SolutionOwnership.Lists);
            if (owning is not null)
            {
                return owning;
            }

            // No solution here lists the project. Attach a lone solution by locality ONLY when its listing
            // can't be inspected (empty/unreadable) — as a developer would open that one in VS. When exactly
            // one solution is readable and CONFIRMS the project is absent, don't fabricate ownership (that
            // would inject an unrelated $(SolutionDir)/Solution* set). Keep walking, then fall back to null.
            if (solutions.Count == 1 && InspectSolutionOwnership(solutions[0], csproj) == SolutionOwnership.Indeterminate)
            {
                return solutions[0];
            }
        }

        return null;
    }

    /// <summary>Whether a solution file demonstrably owns a project.</summary>
    private enum SolutionOwnership
    {
        /// <summary>The solution lists the project.</summary>
        Lists,

        /// <summary>The solution was read and lists real projects, but not this one → definitively not owned.</summary>
        ConfirmedAbsent,

        /// <summary>The solution couldn't be read, or parsed to no project entries → ownership unknown.</summary>
        Indeterminate,
    }

    /// <summary>
    /// Inspects whether a solution owns a project, distinguishing a CONFIRMED-absent solution (readable,
    /// lists real projects, none match) from an <see cref="SolutionOwnership.Indeterminate"/> one
    /// (unreadable, or zero entries). Parses the solution text directly (no <c>dotnet</c> shell-out):
    /// classic <c>.sln</c> via regex, <c>.slnx</c> via XML; each path resolved relative to the solution dir.
    /// </summary>
    private static SolutionOwnership InspectSolutionOwnership(FileInfo solution, FileInfo project)
    {
        string text;
        try
        {
            text = File.ReadAllText(solution.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SolutionOwnership.Indeterminate;
        }

        var solutionDir = solution.Directory?.FullName ?? Directory.GetCurrentDirectory();
        var relativePaths = string.Equals(solution.Extension, ".slnx", StringComparison.OrdinalIgnoreCase)
            ? ExtractSlnxProjectPaths(text)
            : ExtractSlnProjectPaths(text);

        var sawProject = false;
        foreach (var relative in relativePaths)
        {
            var full = TryResolveSolutionRelativePath(solutionDir, relative);
            if (full is null)
            {
                continue;
            }

            sawProject = true;
            if (string.Equals(full, project.FullName, StringComparison.OrdinalIgnoreCase))
            {
                return SolutionOwnership.Lists;
            }
        }

        // Read a real, non-empty project list without a match → confirmed absent. An empty/opaque list
        // (nothing parsed) leaves ownership unknown.
        return sawProject ? SolutionOwnership.ConfirmedAbsent : SolutionOwnership.Indeterminate;
    }

    /// <summary>
    /// True when a solution file lists the given project. Thin wrapper over
    /// <see cref="InspectSolutionOwnership"/> for callers that only need the yes/no answer.
    /// </summary>
    private static bool SolutionListsProject(FileInfo solution, FileInfo project) =>
        InspectSolutionOwnership(solution, project) == SolutionOwnership.Lists;

    /// <summary>Extracts every listed project path (any type) from a classic <c>.sln</c> file.</summary>
    private static List<string> ExtractSlnAllProjectPaths(string text) =>
        SlnAnyProjectPathRegex().Matches(text).Select(m => m.Groups[1].Value).ToList();

    /// <summary>Extracts the relative <c>.csproj</c> paths listed in a classic <c>.sln</c> file.</summary>
    private static List<string> ExtractSlnProjectPaths(string text) =>
        ExtractSlnAllProjectPaths(text)
            .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>Extracts every listed project path (any type) from an XML <c>.slnx</c> solution.</summary>
    private static List<string> ExtractSlnxAllProjectPaths(string text)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(text);
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }

        return doc.Descendants()
            .Where(e => string.Equals(e.Name.LocalName, "Project", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Attributes()
                .FirstOrDefault(a => string.Equals(a.Name.LocalName, "Path", StringComparison.OrdinalIgnoreCase))?.Value)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToList();
    }

    /// <summary>Extracts the relative <c>.csproj</c> paths from an XML <c>.slnx</c> solution.</summary>
    private static List<string> ExtractSlnxProjectPaths(string text) =>
        ExtractSlnxAllProjectPaths(text)
            .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>Managed project types that <c>dotnet restore</c> handles on a VS-less host.</summary>
    private static readonly string[] ManagedProjectExtensions = [".csproj", ".vbproj", ".fsproj"];

    /// <summary>True when the project path is a dotnet-restorable managed type (<c>.csproj</c>/<c>.vbproj</c>/<c>.fsproj</c>).</summary>
    private static bool IsManagedProjectPath(string path) =>
        ManagedProjectExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Computes the restore plan for a solution build. VS (and <c>dotnet build &lt;sln&gt;</c>) restore the
    /// <em>whole solution</em> before building — including build-dependency projects that aren't
    /// <c>ProjectReference</c>s of the target — whereas <c>winapp run</c> restores only the target, so those
    /// siblings lack a <c>project.assets.json</c> and the build fails with <c>NETSDK1004</c>. Enumerates the
    /// solution's listed projects (pure text parse — no shell-out, no <c>File.Exists</c> gating) and returns
    /// the managed siblings to restore, excluding the target.
    /// <para>
    /// <paramref name="AllManaged"/> is true when every listed project is a restorable managed type. When
    /// false (a native <c>.vcxproj</c>/<c>.wapproj</c>/<c>.shproj</c> is present, which <c>dotnet restore
    /// &lt;sln&gt;</c> can't handle VS-less), the caller restores managed siblings individually.
    /// </para>
    /// </summary>
    internal static (bool AllManaged, List<FileInfo> ManagedSiblings) ComputeSolutionRestorePlan(FileInfo solution, FileInfo target)
    {
        string text;
        try
        {
            text = File.ReadAllText(solution.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (true, []);
        }

        var solutionDir = solution.Directory?.FullName ?? Directory.GetCurrentDirectory();
        var projectPaths = (string.Equals(solution.Extension, ".slnx", StringComparison.OrdinalIgnoreCase)
                ? ExtractSlnxAllProjectPaths(text)
                : ExtractSlnAllProjectPaths(text))
            // Drop classic-.sln solution-folder entries (their "path" is the folder name, no ...proj extension).
            .Where(p => p.EndsWith("proj", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var allManaged = projectPaths.All(IsManagedProjectPath);

        var resolvedSiblings = projectPaths
            .Where(IsManagedProjectPath)
            .Select(relative => TryResolveSolutionRelativePath(solutionDir, relative))
            .Where(full => full is not null && !string.Equals(full, target.FullName, StringComparison.OrdinalIgnoreCase))
            .Select(full => full!);

        var siblings = DistinctProjectFiles(resolvedSiblings);

        return (allManaged, siblings);
    }

    /// <summary>
    /// Resolves the runnable app project out of a solution and records the solution on the result so the
    /// build defines <c>$(SolutionDir)</c>. A classic <c>.sln</c>'s project list comes from <c>dotnet sln
    /// list</c>; an XML <c>.slnx</c> is parsed locally. Each candidate is classified with the same MSBuild
    /// evaluation used for a multi-<c>.csproj</c> directory. Exactly one launchable (non-test executable)
    /// project is required unless a matching <c>--project</c> selector is supplied.
    /// </summary>
    private async Task<RunInputResolution> ResolveSolutionAsync(FileInfo solution, string? projectSelector, ProjectClassificationInputs? classificationInputs, CancellationToken cancellationToken, bool allowFolderFallback = false)
    {
        var solutionDir = solution.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory());
        var projects = await GetSolutionProjectsAsync(solution, solutionDir, cancellationToken);

        if (projects.Count == 0)
        {
            // A solution that lists no .csproj (e.g. native-only) has nothing runnable. A directory input
            // degrades to folder mode; an explicit .sln keeps the error.
            if (allowFolderFallback)
            {
                return new RunInputResolution(WinAppRunMode.Folder, null, solutionDir);
            }

            throw new ProjectRunException(
                $"No .csproj projects were found in '{solution.Name}'. 'winapp run' needs a runnable C# project in the solution.");
        }

        // An explicit --project selector short-circuits classification.
        if (!string.IsNullOrWhiteSpace(projectSelector))
        {
            var selected = MatchProjectSelector(projects, projectSelector, solutionDir);
            if (selected is null)
            {
                // List only the runnable projects (not every project in the solution) so a large solution
                // doesn't dump 100+ names, most of which the user can't run.
                var available = await BuildRunnableAvailableHintAsync(
                    projects, solutionDir, BuildClassificationPropertyTokens(classificationInputs, solution),
                    classificationInputs, solution, cancellationToken);
                throw new ProjectRunException(
                    $"--project '{projectSelector}' did not match a single project in '{solution.Name}'. Available: {available}.");
            }

            return new RunInputResolution(WinAppRunMode.Project, selected, selected.Directory ?? solutionDir, solution, "matched --project");
        }

        var solutionProps = BuildClassificationPropertyTokens(classificationInputs, solution);
        var (apps, tests) = await ClassifyRunnablesAsync(projects, solutionDir, solutionProps, classificationInputs, solution, cancellationToken);

        var pick = PickRunnableProject(apps, tests, out var pickedTest);
        if (pick is not null)
        {
            if (pickedTest)
            {
                LogRunningLoneTestProject(pick, solution.Name);
            }

            return new RunInputResolution(WinAppRunMode.Project, pick, pick.Directory ?? solutionDir, solution, "only runnable project");
        }

        // Zero or several runnable app projects → we don't emulate VS's startup-project selection; require
        // an explicit --project so the wrong app is never launched. (A lone test project auto-runs above.)
        // Exception: a directory input whose sole solution has NO runnable project (libraries only) degrades
        // to folder mode, mirroring the lone non-runnable .csproj path; an explicit .sln always errors.
        if (allowFolderFallback && apps.Count == 0 && tests.Count == 0)
        {
            return new RunInputResolution(WinAppRunMode.Folder, null, solutionDir);
        }

        var candidatePool = apps.Count > 0 ? apps : (tests.Count > 0 ? tests : projects);
        var candidateList = FormatProjectNameList(candidatePool.Select(p => p.Name));
        string reason;
        if (apps.Count > 1)
        {
            reason = $"'{solution.Name}' contains multiple runnable app projects ({candidateList})";
        }
        else if (tests.Count > 1)
        {
            reason = $"'{solution.Name}' contains only test projects ({candidateList})";
        }
        else
        {
            reason = $"No runnable app project was found in '{solution.Name}' ({candidateList})";
        }

        throw new ProjectRunException(
            $"{reason}. Specify which project to run with --project <name>.");
    }

    /// <summary>
    /// Classifies each candidate project into runnable apps and runnable test projects via
    /// <see cref="IProjectDetectionService.ClassifyRunnableAsync"/>. Non-runnable projects (libraries,
    /// etc.) are dropped. Test projects are kept separate so <see cref="PickRunnableProject"/> can prefer
    /// a real app but still fall back to a lone test project.
    /// </summary>
    private async Task<(List<FileInfo> Apps, List<FileInfo> Tests)> ClassifyRunnablesAsync(
        List<FileInfo> projects,
        DirectoryInfo workingDirectory,
        IReadOnlyList<string>? extraMsbuildProperties,
        ProjectClassificationInputs? classificationInputs,
        FileInfo? solution,
        CancellationToken cancellationToken)
    {
        // Each project's classification spawns up to two `dotnet` evaluations, so a large solution (100+
        // projects) ran for minutes when done sequentially. Classify with bounded parallelism instead —
        // the per-project work is independent — while capping concurrency so we don't spawn a dotnet
        // process storm. Results are reassembled in the original project order so the candidate lists and
        // auto-selection stay deterministic regardless of completion order.
        var degree = Math.Max(1, Math.Min(Environment.ProcessorCount, projects.Count));
        using var throttle = new SemaphoreSlim(degree);
        var kinds = new ProjectRunnability[projects.Count];

        var classifyTasks = projects.Select(async (project, index) =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                var projectProps = await AddEffectiveFrameworkForClassificationAsync(
                    project, workingDirectory, extraMsbuildProperties, classificationInputs, solution, cancellationToken);
                kinds[index] = await projectDetectionService.ClassifyRunnableAsync(project, workingDirectory, projectProps, cancellationToken);
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(classifyTasks);

        var apps = new List<FileInfo>();
        var tests = new List<FileInfo>();
        for (var i = 0; i < projects.Count; i++)
        {
            switch (kinds[i])
            {
                case ProjectRunnability.App:
                    apps.Add(projects[i]);
                    break;
                case ProjectRunnability.Test:
                    tests.Add(projects[i]);
                    break;
            }
        }

        return (apps, tests);
    }

    /// <summary>
    /// Appends <c>-p:TargetFramework=&lt;first&gt;</c> to a candidate's classification properties when it is
    /// multi-targeted and the user gave no <c>--framework</c>, so the classify evaluate reads
    /// <c>OutputType</c>/test markers on the SAME inner single-TFM node the build pins. Without this, a
    /// project whose executable <c>OutputType</c> is conditional on <c>$(TargetFramework)</c> evaluates on
    /// the cross-targeting outer node (empty TFM) → appears non-runnable → auto-selection fails before the
    /// build. Reuses <see cref="ResolveEffectiveFrameworkAsync"/>, which no-ops for single-targeted projects
    /// and when a TFM can't be resolved (SDK-less / pre-restore).
    /// </summary>
    private async Task<IReadOnlyList<string>?> AddEffectiveFrameworkForClassificationAsync(
        FileInfo project,
        DirectoryInfo workingDirectory,
        IReadOnlyList<string>? extraMsbuildProperties,
        ProjectClassificationInputs? classificationInputs,
        FileInfo? solution,
        CancellationToken cancellationToken)
    {
        // Nothing to resolve when arch didn't resolve (inputs null) or the user already pinned a TFM (then
        // BuildClassificationPropertyTokens already threaded -p:TargetFramework in).
        if (classificationInputs is null || !string.IsNullOrWhiteSpace(classificationInputs.Framework))
        {
            return extraMsbuildProperties;
        }

        var probe = new ProjectRunOptions(
            classificationInputs.Configuration,
            classificationInputs.Architecture,
            Framework: null,
            NoBuild: true,
            NoRestore: true,
            classificationInputs.Properties,
            Solution: solution);

        var resolved = await ResolveEffectiveFrameworkAsync(project, probe, workingDirectory, cancellationToken);
        if (string.IsNullOrWhiteSpace(resolved.Framework))
        {
            return extraMsbuildProperties;
        }

        return [.. extraMsbuildProperties ?? [], $"-p:TargetFramework={resolved.Framework}"];
    }

    /// <summary>
    /// Auto-selection: a single real app wins; test projects are skipped when any app exists; a lone test
    /// project (tests-only solution) is run as a convenience. Any other shape (several apps, several
    /// tests-only, none) returns null so the caller can require an explicit <c>--project</c>.
    /// </summary>
    private static FileInfo? PickRunnableProject(List<FileInfo> apps, List<FileInfo> tests, out bool pickedTestProject)
    {
        pickedTestProject = false;

        if (apps.Count == 1)
        {
            return apps[0];
        }

        if (apps.Count == 0 && tests.Count == 1)
        {
            pickedTestProject = true;
            return tests[0];
        }

        return null;
    }

    private void LogRunningLoneTestProject(FileInfo project, string sourceName)
    {
        // Courtesy note that obeys the output mode: gate on Information so --json keeps stdout a pure JSON
        // envelope and --quiet suppresses it. Runs during input resolution, ahead of the command's own
        // gating, so it must self-gate.
        if (!logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        ansiConsole.MarkupLineInterpolated(
            $"{UiSymbols.Note} No runnable app project found in '{sourceName}'; running the only runnable project '{project.Name}', which is a test project.");
    }

    /// <summary>
    /// Lists the C# projects in a solution, resolving each to an absolute <see cref="FileInfo"/>.
    /// Non-<c>.csproj</c> projects are excluded (<c>winapp run</c> builds/launches managed app projects).
    /// A classic <c>.sln</c> is enumerated via <c>dotnet sln list</c>; an XML <c>.slnx</c> is parsed directly
    /// (<c>dotnet sln list</c> needs SDK 9.0.200+ for <c>.slnx</c>, but the target <c>.csproj</c> is what
    /// gets built, so no <c>.slnx</c>-aware SDK is required).
    /// </summary>
    private async Task<List<FileInfo>> GetSolutionProjectsAsync(FileInfo solution, DirectoryInfo solutionDir, CancellationToken cancellationToken)
    {
        // A solution's purpose is to build, so a missing/too-old SDK is always fatal — surface the
        // actionable install/upgrade message rather than masking it as a folder-mode miss.
        var sdkError = await CheckSdkAsync(solutionDir, cancellationToken);
        if (sdkError != null)
        {
            throw new ProjectRunException(sdkError);
        }

        // .slnx: parse locally (dotnet sln list needs SDK 9.0.200+ for .slnx; our 8.0.100 floor doesn't
        // cover it). We build the resolved .csproj directly, so only the local XML parse is needed.
        if (string.Equals(solution.Extension, ".slnx", StringComparison.OrdinalIgnoreCase))
        {
            return ReadSolutionProjectsFromText(solution, solutionDir, ExtractSlnxProjectPaths);
        }

        var arguments = WindowsCommandLine.JoinArguments(["sln", solution.FullName, "list"]) ?? string.Empty;

        int exitCode;
        string stdout;
        string stderr;
        try
        {
            (exitCode, stdout, stderr) = await dotNetService.RunDotnetCommandAsync(solutionDir, arguments, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Honor Ctrl+C during solution discovery instead of reporting it as an unreadable solution.
            throw;
        }
        catch (Exception ex)
        {
            throw new ProjectRunException(
                $"Could not read the solution '{solution.Name}' ('dotnet sln list' failed): {ex.Message}");
        }

        if (exitCode != 0)
        {
            var detail = string.Join(Environment.NewLine,
                new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.TrimEnd()));
            throw new ProjectRunException(
                $"Could not read the solution '{solution.Name}' ('dotnet sln list' exited {exitCode}). {detail}".TrimEnd());
        }

        var resolved = stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            // Skip the `dotnet sln list` header ("Project(s)" and its dashed underline) and non-.csproj entries.
            .Where(raw => !(raw.All(c => c == '-') || string.Equals(raw, "Project(s)", StringComparison.OrdinalIgnoreCase)))
            .Where(raw => raw.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(raw => TryResolveSolutionRelativePath(solutionDir.FullName, raw))
            .Where(full => full is not null)
            .Select(full => full!);

        return DistinctProjectFiles(resolved);
    }

    /// <summary>
    /// Normalizes a solution-relative project path (either slash flavor) and resolves it to an absolute path
    /// under <paramref name="solutionDir"/>. Returns <c>null</c> when the path is malformed so callers skip
    /// it.
    /// </summary>
    private static string? TryResolveSolutionRelativePath(string solutionDir, string relative)
    {
        try
        {
            var normalized = relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.Combine(solutionDir, normalized));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>De-duplicates absolute project paths (case-insensitive) into <see cref="FileInfo"/> entries, preserving first-seen order.</summary>
    private static List<FileInfo> DistinctProjectFiles(IEnumerable<string> fullPaths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<FileInfo>();
        foreach (var full in fullPaths)
        {
            if (seen.Add(full))
            {
                files.Add(new FileInfo(full));
            }
        }

        return files;
    }

    /// <summary>
    /// Reads a solution file's text and resolves the relative <c>.csproj</c> paths (via
    /// <paramref name="extractProjectPaths"/>) to absolute, de-duplicated <see cref="FileInfo"/> entries.
    /// Used for the pure-text solution formats (<c>.slnx</c>). Throws <see cref="ProjectRunException"/> when
    /// the solution can't be read.
    /// </summary>
    private static List<FileInfo> ReadSolutionProjectsFromText(
        FileInfo solution,
        DirectoryInfo solutionDir,
        Func<string, List<string>> extractProjectPaths)
    {
        string text;
        try
        {
            text = File.ReadAllText(solution.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProjectRunException(
                $"Could not read the solution '{solution.Name}': {ex.Message}");
        }

        var resolved = extractProjectPaths(text)
            .Select(relative => TryResolveSolutionRelativePath(solutionDir.FullName, relative))
            .Where(full => full is not null)
            .Select(full => full!);

        return DistinctProjectFiles(resolved);
    }

    /// <summary>
    /// Matches a <c>--project</c> selector against candidate projects by full path, file name (with or
    /// without the <c>.csproj</c> extension). Returns the single match, or null when zero or several
    /// candidates match (ambiguous).
    /// </summary>
    internal static FileInfo? MatchProjectSelector(IReadOnlyList<FileInfo> projects, string selector, DirectoryInfo baseDir)
    {
        var trimmed = selector.Trim();
        // Resolve a path-style selector against the input/solution directory (not the process cwd). --project
        // is user input: an unsupported path format makes Path.GetFullPath throw — treat that as "no match"
        // so the caller emits the normal selector error rather than leaking an exception.
        string rooted;
        try
        {
            rooted = Path.GetFullPath(trimmed, baseDir.FullName);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }

        var matches = projects.Where(p =>
            string.Equals(p.FullName, rooted, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileNameWithoutExtension(p.Name), trimmed, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // A relative path-style selector may not be rooted where we computed. Fall back to matching the
        // selector while honoring its directory intent: a selector with a directory component suffix-matches
        // the full path (so `src/App/App.csproj` resolves there yet a different same-named project isn't
        // picked); a bare leaf falls back to a name match. A fully qualified selector is exact — no fallback.
        if (matches.Count == 0 && !Path.IsPathFullyQualified(trimmed))
        {
            var normalized = trimmed.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (normalized.Contains(Path.DirectorySeparatorChar))
            {
                var suffix = Path.DirectorySeparatorChar + normalized;
                matches = projects.Where(p => p.FullName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            else if (!string.IsNullOrEmpty(normalized))
            {
                matches = projects.Where(p =>
                    string.Equals(p.Name, normalized, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileNameWithoutExtension(p.Name), Path.GetFileNameWithoutExtension(normalized), StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }

        return matches.Count == 1 ? matches[0] : null;
    }
}
