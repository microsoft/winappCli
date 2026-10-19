// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal partial class MigrateCommand
{
    public partial class Handler
    {
        private static readonly HashSet<string>
            TargetGraphProjectDiscoveryExcludedDirectories =
            new(
                [
                    "bin",
                    "obj",
                    ".git",
                    ".github",
                    ".migration-evidence",
                    ".uwp-source",
                    ".vs"
                ],
                StringComparer.OrdinalIgnoreCase);

        private sealed record NestedTargetProject(
            string ProjectPath,
            string DirectoryPath,
            string FullProjectPath);

        private sealed record ExternalTargetProjectItem(
            string Kind,
            string SamplePath);

        internal static async Task<MigrationTargetProjectGraphVerification>
            AnalyzeTargetProjectGraphAsync(
                string targetRoot,
                string targetProject,
                IMsBuildProjectItemEvaluator evaluator,
                CancellationToken cancellationToken,
                MsBuildProjectItemEvaluation? precomputedEntryEvaluation = null)
        {
            var entryDirectory = Path.GetDirectoryName(targetProject)!;
            var entryProject = NormalizePath(
                Path.GetRelativePath(targetRoot, targetProject));
            var analysis = new MigrationTargetProjectGraphVerification
            {
                EntryProject = entryProject
            };

            var nestedProjects = DiscoverNestedTargetProjects(
                targetRoot,
                entryDirectory,
                targetProject,
                entryProject,
                analysis.Issues);
            analysis.NestedProjects = nestedProjects
                .Select(project => project.ProjectPath)
                .ToList();

            var entryEvaluation = precomputedEntryEvaluation
                ?? await evaluator.EvaluateAsync(
                    new FileInfo(targetProject),
                    cancellationToken);
            var entryError = string.Empty;
            if (!entryEvaluation.Succeeded
                || !TryCollectContainedOwnership(
                    targetRoot,
                    entryEvaluation.Items,
                    entryEvaluation.RestoredPackageDirectories,
                    out var entryOwnership,
                    out var entryExternalItems,
                    out entryError))
            {
                AddIncompleteTargetGraphIssues(
                    analysis,
                    nestedProjects,
                    entryProject,
                    entryEvaluation.Error ?? entryError);
                return FinalizeTargetProjectGraphAnalysis(
                    analysis,
                    "incomplete");
            }
            AddExternalTargetItemsIssue(
                analysis,
                entryProject,
                null,
                entryExternalItems);

            if (nestedProjects.Count == 0)
            {
                return FinalizeTargetProjectGraphAnalysis(
                    analysis,
                    analysis.Issues.Any(issue =>
                        issue.Kind == "target-external-file-items")
                        ? "failed"
                        : analysis.Issues.Count == 0
                            ? "not-required"
                            : "incomplete");
            }

            foreach (var nestedProject in nestedProjects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var nestedEvaluation = await evaluator.EvaluateAsync(
                    new FileInfo(nestedProject.FullProjectPath),
                    cancellationToken);
                var nestedError = string.Empty;
                if (!nestedEvaluation.Succeeded
                    || !TryCollectContainedOwnership(
                        targetRoot,
                        nestedEvaluation.Items,
                        nestedEvaluation.RestoredPackageDirectories,
                        out var nestedOwnership,
                        out var nestedExternalItems,
                        out nestedError))
                {
                    analysis.Issues.Add(CreateTargetGraphIssue(
                        "target-project-graph-incomplete",
                        "review-required",
                        entryProject,
                        nestedProject,
                        [],
                        [nestedProject.ProjectPath],
                        [],
                        nestedEvaluation.Error ?? nestedError));
                    continue;
                }
                AddExternalTargetItemsIssue(
                    analysis,
                    entryProject,
                    nestedProject,
                    nestedExternalItems);

                var collisionPaths = entryOwnership.Keys
                    .Intersect(
                        nestedOwnership.Keys,
                        StringComparer.OrdinalIgnoreCase)
                    .Where(File.Exists)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (collisionPaths.Count == 0)
                {
                    continue;
                }

                var relativePaths = collisionPaths
                    .Select(path => NormalizePath(
                        Path.GetRelativePath(targetRoot, path)))
                    .ToList();
                var itemKinds = collisionPaths
                    .SelectMany(path => entryOwnership[path]
                        .Concat(nestedOwnership[path]))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var generatedPaths = relativePaths
                    .Where(IsGeneratedTargetPath)
                    .Take(8)
                    .ToList();
                analysis.Issues.Add(CreateTargetGraphIssue(
                    "nested-project-default-item-collision",
                    "error",
                    entryProject,
                    nestedProject,
                    itemKinds,
                    relativePaths.Take(8).ToList(),
                    generatedPaths,
                    $"The entry and nested projects both evaluate {collisionPaths.Count} contained physical file(s) as owned items."));
            }

            var status = analysis.Issues.Any(issue =>
                    issue.Kind is
                        "nested-project-default-item-collision"
                        or "target-external-file-items")
                ? "failed"
                : analysis.Issues.Count > 0
                    ? "incomplete"
                    : "passed";
            return FinalizeTargetProjectGraphAnalysis(
                analysis,
                status);
        }

        private static bool TryCollectContainedOwnership(
            string targetRoot,
            IReadOnlyList<MsBuildProjectItem> items,
            IReadOnlyList<string> restoredPackageDirectories,
            out Dictionary<string, HashSet<string>> ownership,
            out List<ExternalTargetProjectItem> externalItems,
            out string error)
        {
            ownership = new Dictionary<string, HashSet<string>>(
                StringComparer.OrdinalIgnoreCase);
            externalItems = [];
            error = string.Empty;
            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.Kind)
                    || string.IsNullOrWhiteSpace(item.Identity)
                    || !Path.IsPathFullyQualified(item.FullPath)
                    || !Path.IsPathFullyQualified(
                        item.DefiningProjectFullPath))
                {
                    error =
                        $"MSBuild returned incomplete ownership facts for item '{item.Identity}'.";
                    return false;
                }

                string fullPath;
                string relativePath;
                try
                {
                    fullPath = Path.GetFullPath(item.FullPath);
                    relativePath = Path.GetRelativePath(
                        Path.GetFullPath(targetRoot),
                        fullPath);
                }
                catch (Exception exception) when (
                    exception is ArgumentException
                    or NotSupportedException
                    or PathTooLongException)
                {
                    error =
                        $"MSBuild returned an invalid path for item '{item.Identity}': {exception.Message}";
                    return false;
                }

                if (Path.IsPathRooted(relativePath)
                    || relativePath == ".."
                    || relativePath.StartsWith(
                        $"..{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal))
                {
                    if (IsRestoredPackageItem(
                            targetRoot,
                            item,
                            restoredPackageDirectories))
                    {
                        continue;
                    }
                    externalItems.Add(new ExternalTargetProjectItem(
                        item.Kind,
                        NormalizePath(item.Identity.Trim())));
                    continue;
                }
                if (!MigrationPathResolver.TryResolveContainedAbsolutePath(
                        targetRoot,
                        fullPath,
                        out var containedPath,
                        out _,
                        out var pathError))
                {
                    error =
                        $"MSBuild item '{item.Identity}' could not be trusted as a contained path: {pathError}";
                    return false;
                }

                if (!ownership.TryGetValue(
                        containedPath,
                        out var kinds))
                {
                    kinds = new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);
                    ownership[containedPath] = kinds;
                }
                kinds.Add(item.Kind);
            }
            return true;
        }

        private static bool IsRestoredPackageItem(
            string targetRoot,
            MsBuildProjectItem item,
            IReadOnlyList<string> restoredPackageDirectories) =>
            restoredPackageDirectories.Any(root =>
                !MigrationPathResolver.TryResolveContainedAbsolutePath(
                    root,
                    targetRoot,
                    out _,
                    out _,
                    out _)
                && !MigrationPathResolver.TryResolveContainedAbsolutePath(
                    targetRoot,
                    item.DefiningProjectFullPath,
                    out _,
                    out _,
                    out _)
                && MigrationPathResolver.TryResolveContainedAbsolutePath(
                    root,
                    item.FullPath,
                    out _,
                    out _,
                    out _)
                && MigrationPathResolver.TryResolveContainedAbsolutePath(
                    root,
                    item.DefiningProjectFullPath,
                    out _,
                    out _,
                    out _));

        private static void AddExternalTargetItemsIssue(
            MigrationTargetProjectGraphVerification analysis,
            string entryProject,
            NestedTargetProject? nestedProject,
            IReadOnlyCollection<ExternalTargetProjectItem> externalItems)
        {
            if (externalItems.Count == 0)
            {
                return;
            }

            analysis.Issues.Add(new MigrationTargetProjectGraphIssue
            {
                Kind = "target-external-file-items",
                EntryProject = entryProject,
                NestedProject = nestedProject?.ProjectPath,
                NestedDirectory = nestedProject?.DirectoryPath,
                ItemKinds = externalItems
                    .Select(item => item.Kind)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                SamplePaths = externalItems
                    .Select(item => item.SamplePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(20)
                    .ToList(),
                Reason =
                    $"{externalItems.Count} evaluated target file item(s) resolve outside the migration target.",
                RequiredResolution =
                    "Copy required files under the migration target and reference them with target-relative paths. Do not depend on the source checkout or machine-specific paths."
            });
        }

        private static bool IsGeneratedTargetPath(string path) =>
            path.Split('/')
                .Any(segment =>
                    segment.Equals(
                        "bin",
                        StringComparison.OrdinalIgnoreCase)
                    || segment.Equals(
                        "obj",
                        StringComparison.OrdinalIgnoreCase));

        private static MigrationTargetProjectGraphVerification
            FinalizeTargetProjectGraphAnalysis(
                MigrationTargetProjectGraphVerification analysis,
                string status)
        {
            analysis.Status = status;
            analysis.Issues = analysis.Issues
                .OrderBy(
                    issue => issue.NestedProject
                        ?? issue.NestedDirectory
                        ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(
                    issue => issue.Kind,
                    StringComparer.Ordinal)
                .ThenBy(
                    issue => issue.Reason,
                    StringComparer.Ordinal)
                .ToList();
            return analysis;
        }

        private static MigrationTargetProjectGraphIssue
            CreateTargetGraphIssue(
                string kind,
                string severity,
                string entryProject,
                NestedTargetProject nestedProject,
                List<string> itemKinds,
                List<string> samplePaths,
                List<string> generatedPaths,
                string reason) =>
            new()
            {
                Kind = kind,
                Severity = severity,
                EntryProject = entryProject,
                NestedProject = nestedProject.ProjectPath,
                NestedDirectory = nestedProject.DirectoryPath,
                ItemKinds = itemKinds,
                SamplePaths = samplePaths,
                GeneratedPaths = generatedPaths,
                Reason = reason,
                RequiredResolution =
                    "Move the nested project outside the entry project's default-item root, or change the evaluated entry project items so each contained file has one project owner."
            };

        private static void AddIncompleteTargetGraphIssues(
            MigrationTargetProjectGraphVerification analysis,
            IReadOnlyCollection<NestedTargetProject> nestedProjects,
            string entryProject,
            string reason)
        {
            if (nestedProjects.Count == 0)
            {
                analysis.Issues.Add(new MigrationTargetProjectGraphIssue
                {
                    Kind = "target-project-graph-incomplete",
                    Severity = "review-required",
                    EntryProject = entryProject,
                    Reason = reason,
                    RequiredResolution =
                        "Make the entry target project evaluable before target project ownership verification."
                });
                return;
            }

            foreach (var nestedProject in nestedProjects)
            {
                analysis.Issues.Add(CreateTargetGraphIssue(
                    "target-project-graph-incomplete",
                    "review-required",
                    entryProject,
                    nestedProject,
                    [],
                    [nestedProject.ProjectPath],
                    [],
                    reason));
            }
        }

        private static List<NestedTargetProject>
            DiscoverNestedTargetProjects(
                string targetRoot,
                string entryDirectory,
                string targetProject,
                string entryProject,
                List<MigrationTargetProjectGraphIssue> issues)
        {
            var projects = new List<NestedTargetProject>();
            var pending = new Stack<string>();
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(
                    entryDirectory,
                    "*",
                    SearchOption.TopDirectoryOnly))
                {
                    pending.Push(directory);
                }
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException)
            {
                issues.Add(new MigrationTargetProjectGraphIssue
                {
                    Kind = "target-project-graph-incomplete",
                    Severity = "review-required",
                    EntryProject = entryProject,
                    Reason =
                        $"The entry project directory could not be inspected safely: {exception.Message}",
                    RequiredResolution =
                        "Make the contained entry project directory inspectable before target project ownership verification."
                });
                return projects;
            }

            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                if (TargetGraphProjectDiscoveryExcludedDirectories.Contains(
                        Path.GetFileName(directory)))
                {
                    continue;
                }

                var relativeDirectory = Path.GetRelativePath(
                    entryDirectory,
                    directory);
                if (!MigrationPathResolver.TryResolveContainedRelativePath(
                        entryDirectory,
                        relativeDirectory,
                        out var containedDirectory,
                        out _,
                        out _)
                    || !MigrationPathResolver.TryResolveContainedRelativePath(
                        targetRoot,
                        Path.GetRelativePath(
                            targetRoot,
                            containedDirectory),
                        out _,
                        out var targetRelativeDirectory,
                        out _))
                {
                    continue;
                }

                try
                {
                    foreach (var project in Directory.EnumerateFiles(
                        containedDirectory,
                        "*.csproj",
                        SearchOption.TopDirectoryOnly))
                    {
                        if (!MigrationPathResolver.TryResolveContainedRelativePath(
                                entryDirectory,
                                Path.GetRelativePath(
                                    entryDirectory,
                                    project),
                                out var containedProject,
                                out _,
                                out _)
                            || string.Equals(
                                containedProject,
                                targetProject,
                                StringComparison.OrdinalIgnoreCase)
                            || !MigrationPathResolver.TryResolveContainedRelativePath(
                                targetRoot,
                                Path.GetRelativePath(
                                    targetRoot,
                                    containedProject),
                                out _,
                                out var targetRelativeProject,
                                out _))
                        {
                            continue;
                        }

                        projects.Add(new NestedTargetProject(
                            targetRelativeProject,
                            targetRelativeDirectory,
                            containedProject));
                    }

                    foreach (var child in Directory.EnumerateDirectories(
                        containedDirectory,
                        "*",
                        SearchOption.TopDirectoryOnly))
                    {
                        pending.Push(child);
                    }
                }
                catch (Exception exception) when (
                    exception is IOException
                    or UnauthorizedAccessException)
                {
                    issues.Add(new MigrationTargetProjectGraphIssue
                    {
                        Kind = "target-project-graph-incomplete",
                        Severity = "review-required",
                        EntryProject = entryProject,
                        NestedDirectory = targetRelativeDirectory,
                        Reason =
                            $"The nested target directory could not be inspected safely: {exception.Message}",
                        RequiredResolution =
                            "Make the contained directory inspectable before target project ownership verification.",
                        SamplePaths = [targetRelativeDirectory]
                    });
                }
            }

            return projects
                .DistinctBy(
                    project => project.ProjectPath,
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(
                    project => project.ProjectPath,
                    StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
