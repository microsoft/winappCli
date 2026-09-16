// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using System.Xml.Linq;
using WinApp.Cli.Models;

namespace WinApp.Cli.Commands;

internal partial class MigrateCommand
{
    public partial class Handler
    {
        private static readonly HashSet<string> TargetGraphRelevantProperties =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "UseWinUI",
                "EnableDefaultItems",
                "EnableDefaultCompileItems",
                "EnableDefaultContentItems",
                "EnableDefaultWindowsAppSdkContentItems",
                "EnableDefaultNoneItems",
                "EnableDefaultPageItems",
                "EnableDefaultApplicationDefinition",
                "EnableDefaultPRIResourceItems",
                "EnableDefaultPriItems",
                "EnableDefaultWindowsAppSdkPRIResourceItems",
                "DefaultItemExcludes",
                "DefaultExcludesInProjectFolder",
                "ImportDirectoryBuildProps",
                "ImportDirectoryBuildTargets",
                "DirectoryBuildPropsPath",
                "DirectoryBuildTargetsPath"
            };

        private static readonly HashSet<string> TargetGraphPolicyProperties =
            new(
                TargetGraphRelevantProperties.Where(property =>
                    property is not "ImportDirectoryBuildProps"
                    and not "ImportDirectoryBuildTargets"
                    and not "DirectoryBuildPropsPath"
                    and not "DirectoryBuildTargetsPath"),
                StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string>
            DirectoryBuildTargetsControlProperties =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "ImportDirectoryBuildTargets",
                "DirectoryBuildTargetsPath"
            };

        private static readonly HashSet<string> TargetGraphRelevantItemKinds =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "Compile",
                "Page",
                "ApplicationDefinition",
                "PRIResource",
                "Content",
                "None"
            };

        private static readonly HashSet<string> TargetGraphExcludedDirectories =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".git",
                ".github",
                ".migration-evidence",
                ".uwp-source",
                ".vs"
            };

        private static readonly HashSet<string>
            TargetGraphProjectDiscoveryExcludedDirectories =
            new(
                TargetGraphExcludedDirectories
                    .Concat(["bin", "obj"]),
                StringComparer.OrdinalIgnoreCase);

        private sealed record NestedTargetProject(
            string ProjectPath,
            string DirectoryPath,
            string FullProjectPath,
            string FullDirectoryPath);

        private sealed record NestedOwnedFile(
            string Path,
            string EntryProjectPath,
            string NestedProjectPath,
            bool Generated,
            IReadOnlyList<TargetOwnedItemKind> Kinds);

        private sealed record TargetOwnedItemKind(string Kind);

        private static readonly IReadOnlyList<TargetOwnedItemKind>
            TargetGraphItemKinds =
            [
                new("Compile"),
                new("Page"),
                new("ApplicationDefinition"),
                new("PRIResource"),
                new("Content"),
                new("None")
            ];

        private sealed record TargetItemOperation(
            string Kind,
            string Operation,
            string Pattern,
            string? ExcludePattern);

        private sealed class TargetDefaultItemPolicy
        {
            internal bool EnableDefaultItems { get; set; } = true;

            internal bool EnableCompile { get; set; } = true;

            internal bool EnableContent { get; set; } = true;

            internal bool EnableWindowsAppSdkContent { get; set; } = true;

            internal bool EnableNone { get; set; } = true;

            internal bool EnablePage { get; set; } = true;

            internal bool EnableApplicationDefinition { get; set; } = true;

            internal bool EnablePriResource { get; set; } = true;

            internal bool EnableWindowsAppSdkPriResource { get; set; } = true;

            internal bool UseWinUi { get; set; }

            internal List<string> Excludes { get; } = [];

            internal bool IsDefaultIncluded(
                string kind,
                string path)
            {
                if (!EnableDefaultItems
                    || Excludes.Any(pattern =>
                        MsBuildGlobMatches(pattern, path)))
                {
                    return false;
                }

                var extension = Path.GetExtension(path);
                return kind switch
                {
                    "Compile" =>
                        EnableCompile
                        && extension.Equals(
                            ".cs",
                            StringComparison.OrdinalIgnoreCase),
                    "Content" =>
                        EnableContent
                        && EnableWindowsAppSdkContent
                        && IsWindowsAppSdkImage(path),
                    "Page" =>
                        UseWinUi
                        && EnablePage
                        && extension.Equals(
                            ".xaml",
                            StringComparison.OrdinalIgnoreCase)
                        && !Path.GetFileName(path).Equals(
                            "App.xaml",
                            StringComparison.OrdinalIgnoreCase),
                    "ApplicationDefinition" =>
                        UseWinUi
                        && EnableApplicationDefinition
                        && Path.GetFileName(path).Equals(
                            "App.xaml",
                            StringComparison.OrdinalIgnoreCase),
                    "PRIResource" =>
                        EnablePriResource
                        && EnableWindowsAppSdkPriResource
                        && extension.Equals(
                            ".resw",
                            StringComparison.OrdinalIgnoreCase),
                    "None" =>
                        EnableNone
                        && !IsClaimedByAnotherDefaultItem(path),
                    _ => false
                };
            }

            private bool IsClaimedByAnotherDefaultItem(
                string path)
            {
                var extension = Path.GetExtension(path);
                if (extension.Equals(
                        ".cs",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return EnableCompile;
                }
                if (extension.Equals(
                        ".xaml",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return UseWinUi
                        && (Path.GetFileName(path).Equals(
                                "App.xaml",
                                StringComparison.OrdinalIgnoreCase)
                            ? EnableApplicationDefinition
                            : EnablePage);
                }
                if (EnableContent
                    && EnableWindowsAppSdkContent
                    && IsWindowsAppSdkImage(path))
                {
                    return true;
                }
                return extension.Equals(
                        ".resw",
                        StringComparison.OrdinalIgnoreCase)
                    && EnablePriResource
                    && EnableWindowsAppSdkPriResource;
            }
        }

        internal static MigrationTargetProjectGraphVerification
            AnalyzeTargetProjectGraph(
                string targetRoot,
                string targetProject)
        {
            var entryDirectory = Path.GetDirectoryName(targetProject)!;
            var entryProject = NormalizePath(
                Path.GetRelativePath(targetRoot, targetProject));
            var analysis = new MigrationTargetProjectGraphVerification
            {
                EntryProject = entryProject
            };

            var discoveryIssues =
                new List<MigrationTargetProjectGraphIssue>();
            var nestedProjects = DiscoverNestedTargetProjects(
                targetRoot,
                entryDirectory,
                targetProject,
                entryProject,
                discoveryIssues);
            analysis.NestedProjects = nestedProjects
                .Select(project => project.ProjectPath)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            analysis.Issues.AddRange(discoveryIssues);
            if (nestedProjects.Count == 0)
            {
                return FinalizeTargetProjectGraphAnalysis(
                    analysis,
                    analysis.Issues.Count == 0
                        ? "not-required"
                        : "incomplete");
            }

            if (!TryBuildProjectEvidenceGraph(
                    targetRoot,
                    targetProject,
                    out var graph,
                    out var graphError,
                    TargetGraphRelevantProperties,
                    TargetGraphRelevantItemKinds,
                    allowDeterministicChoose: true))
            {
                AddIncompleteTargetGraphIssues(
                    analysis,
                    nestedProjects,
                    entryProject,
                    graphError);
                return FinalizeTargetProjectGraphAnalysis(
                    analysis,
                    "incomplete");
            }

            var context = new ProjectConditionContext(
                Path.GetFileNameWithoutExtension(targetProject));
            if (!TryEvaluateTargetDefaultItemPolicy(
                    graph,
                    context,
                    out var policy,
                    out var policyError))
            {
                AddIncompleteTargetGraphIssues(
                    analysis,
                    nestedProjects,
                    entryProject,
                    policyError);
                return FinalizeTargetProjectGraphAnalysis(
                    analysis,
                    "incomplete");
            }
            if (!TryCollectTargetItemOperations(
                    graph,
                    context,
                    out var preDefaultOperations,
                    out var postDefaultOperations,
                    out var operationError))
            {
                AddIncompleteTargetGraphIssues(
                    analysis,
                    nestedProjects,
                    entryProject,
                    operationError);
                return FinalizeTargetProjectGraphAnalysis(
                    analysis,
                    "incomplete");
            }

            foreach (var nestedProject in nestedProjects)
            {
                if (!TryEnumerateNestedOwnedFiles(
                        entryDirectory,
                        targetRoot,
                        nestedProject,
                        out var files,
                        out var fileError))
                {
                    analysis.Issues.Add(
                        CreateTargetGraphIssue(
                            "target-project-graph-incomplete",
                            "review-required",
                            entryProject,
                            nestedProject,
                            [],
                            [nestedProject.ProjectPath],
                            [],
                            fileError));
                    continue;
                }

                var potentialCollisions =
                    new List<(string Kind, NestedOwnedFile File)>();
                foreach (var file in files)
                {
                    foreach (var kind in file.Kinds)
                    {
                        if (IsFileIncludedByProject(
                            file.EntryProjectPath,
                                kind,
                                policy,
                                preDefaultOperations,
                                postDefaultOperations))
                        {
                            potentialCollisions.Add((
                                kind.Kind,
                                file));
                        }
                    }
                }
                if (potentialCollisions.Count == 0)
                {
                    continue;
                }

                var collisions = potentialCollisions
                    .Where(collision =>
                        collision.File.Generated)
                    .ToList();
                var sourceCandidates = potentialCollisions
                    .Where(collision =>
                        !collision.File.Generated)
                    .ToList();
                if (sourceCandidates.Count > 0)
                {
                    if (!TryBuildProjectEvidenceGraph(
                            targetRoot,
                            nestedProject.FullProjectPath,
                            out var nestedGraph,
                            out var nestedGraphError,
                            TargetGraphRelevantProperties,
                            TargetGraphRelevantItemKinds,
                            allowDeterministicChoose: true)
                        || !TryEvaluateTargetDefaultItemPolicy(
                            nestedGraph,
                            new ProjectConditionContext(
                                Path.GetFileNameWithoutExtension(
                                    nestedProject.FullProjectPath)),
                            out var nestedPolicy,
                            out nestedGraphError)
                        || !TryCollectTargetItemOperations(
                            nestedGraph,
                            new ProjectConditionContext(
                                Path.GetFileNameWithoutExtension(
                                    nestedProject.FullProjectPath)),
                            out var nestedPreDefaultOperations,
                            out var nestedPostDefaultOperations,
                            out nestedGraphError))
                    {
                        analysis.Issues.Add(
                            CreateTargetGraphIssue(
                                "nested-project-ownership-incomplete",
                                "review-required",
                                entryProject,
                                nestedProject,
                                sourceCandidates
                                    .Select(candidate => candidate.Kind)
                                    .Distinct(
                                        StringComparer.OrdinalIgnoreCase)
                                    .Order(
                                        StringComparer.OrdinalIgnoreCase)
                                    .ToList(),
                                sourceCandidates
                                    .Select(candidate =>
                                        candidate.File.Path)
                                    .Distinct(
                                        StringComparer.OrdinalIgnoreCase)
                                    .Order(
                                        StringComparer.OrdinalIgnoreCase)
                                    .Take(8)
                                    .ToList(),
                                [],
                                $"The entry project may consume files below the nested project, but ownership cannot be proven from '{nestedProject.ProjectPath}': {nestedGraphError}"));
                    }
                    else
                    {
                        var nestedOwnedPaths = sourceCandidates
                            .Select(candidate => candidate.File)
                            .DistinctBy(
                                file => file.Path,
                                StringComparer.OrdinalIgnoreCase)
                            .Where(file => file.Kinds.Any(kind =>
                                IsFileIncludedByProject(
                                    file.NestedProjectPath,
                                    kind,
                                    nestedPolicy,
                                    nestedPreDefaultOperations,
                                    nestedPostDefaultOperations)))
                            .Select(file => file.Path)
                            .ToHashSet(
                                StringComparer.OrdinalIgnoreCase);
                        collisions.AddRange(sourceCandidates.Where(
                            collision =>
                                nestedOwnedPaths.Contains(
                                    collision.File.Path)));
                    }
                }
                if (collisions.Count == 0)
                {
                    continue;
                }

                var itemKinds = collisions
                    .Select(collision => collision.Kind)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var samplePaths = collisions
                    .Select(collision => collision.File.Path)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .Take(8)
                    .ToList();
                var generatedPaths = collisions
                    .Where(collision => collision.File.Generated)
                    .Select(collision => collision.File.Path)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .Take(8)
                    .ToList();
                analysis.Issues.Add(
                    CreateTargetGraphIssue(
                        "nested-project-default-item-collision",
                        "error",
                        entryProject,
                        nestedProject,
                        itemKinds,
                        samplePaths,
                        generatedPaths,
                        $"The entry project can consume {collisions.Count} file/item combinations owned by the nested project through active SDK default or explicit items."));
            }

            var status = analysis.Issues.Any(issue =>
                    issue.Kind ==
                        "nested-project-default-item-collision")
                ? "failed"
                : analysis.Issues.Count > 0
                    ? "incomplete"
                    : "passed";
            return FinalizeTargetProjectGraphAnalysis(
                analysis,
                status);
        }

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
                    "Move the nested project outside the entry project's default-item root, or add active contained default-item exclusions/removals that cover the nested project source and generated obj/bin content while retaining ProjectReference ownership."
            };

        private static void AddIncompleteTargetGraphIssues(
            MigrationTargetProjectGraphVerification analysis,
            IReadOnlyCollection<NestedTargetProject> nestedProjects,
            string entryProject,
            string reason)
        {
            foreach (var nestedProject in nestedProjects)
            {
                analysis.Issues.Add(
                    CreateTargetGraphIssue(
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
            List<string> entryDirectories;
            try
            {
                entryDirectories = Directory.EnumerateDirectories(
                    entryDirectory,
                    "*",
                    SearchOption.TopDirectoryOnly).ToList();
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
            foreach (var directory in entryDirectories)
            {
                pending.Push(directory);
            }

            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                var name = Path.GetFileName(directory);
                if (TargetGraphProjectDiscoveryExcludedDirectories.Contains(
                        name))
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
                        out _))
                {
                    continue;
                }
                if (!MigrationPathResolver.TryResolveContainedRelativePath(
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
                        var relativeProject = Path.GetRelativePath(
                            entryDirectory,
                            project);
                        if (!MigrationPathResolver.TryResolveContainedRelativePath(
                                entryDirectory,
                                relativeProject,
                                out var containedProject,
                                out _,
                                out _)
                            || string.Equals(
                                containedProject,
                                targetProject,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        if (!MigrationPathResolver.TryResolveContainedRelativePath(
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
                            containedProject,
                            containedDirectory));
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
                            "Move the nested project outside the entry project's default-item root, or make the contained directory inspectable and add active exclusions/removals before verification.",
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

        private static bool TryEnumerateNestedOwnedFiles(
            string entryDirectory,
            string targetRoot,
            NestedTargetProject nestedProject,
            out List<NestedOwnedFile> files,
            out string error)
        {
            files = [];
            error = string.Empty;
            var pending = new Stack<string>();
            pending.Push(nestedProject.FullDirectoryPath);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                if (!string.Equals(
                        directory,
                        nestedProject.FullDirectoryPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    var directoryName = Path.GetFileName(directory);
                    if (TargetGraphExcludedDirectories.Contains(
                            directoryName))
                    {
                        continue;
                    }
                }
                try
                {
                    foreach (var file in Directory.EnumerateFiles(
                        directory,
                        "*",
                        SearchOption.TopDirectoryOnly))
                    {
                        if (string.Equals(
                                file,
                                nestedProject.FullProjectPath,
                                StringComparison.OrdinalIgnoreCase)
                            || Path.GetFileName(file).Equals(
                                "migration-report.json",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        var relativePath = Path.GetRelativePath(
                            entryDirectory,
                            file);
                        if (!MigrationPathResolver.TryResolveContainedRelativePath(
                                entryDirectory,
                                relativePath,
                                out _,
                                out var normalizedPath,
                                out _))
                        {
                            continue;
                        }
                        if (!MigrationPathResolver.TryResolveContainedRelativePath(
                                targetRoot,
                                Path.GetRelativePath(
                                    targetRoot,
                                    file),
                                out _,
                                out var targetRelativePath,
                                out _))
                        {
                            continue;
                        }
                        var generated = normalizedPath
                            .Split('/')
                            .Any(segment =>
                                segment.Equals(
                                    "obj",
                                    StringComparison.OrdinalIgnoreCase)
                                || segment.Equals(
                                    "bin",
                                    StringComparison.OrdinalIgnoreCase));
                        files.Add(new NestedOwnedFile(
                            targetRelativePath,
                            normalizedPath,
                            NormalizePath(Path.GetRelativePath(
                                nestedProject.FullDirectoryPath,
                                file)),
                            generated,
                            TargetGraphItemKinds));
                    }
                    foreach (var child in Directory.EnumerateDirectories(
                        directory,
                        "*",
                        SearchOption.TopDirectoryOnly))
                    {
                        if (MigrationPathResolver.TryResolveContainedRelativePath(
                                entryDirectory,
                                Path.GetRelativePath(
                                    entryDirectory,
                                    child),
                                out var containedChild,
                                out _,
                                out _))
                        {
                            pending.Push(containedChild);
                        }
                    }
                }
                catch (Exception exception) when (
                    exception is IOException
                    or UnauthorizedAccessException)
                {
                    error =
                        $"The nested project directory '{nestedProject.DirectoryPath}' could not be enumerated safely: {exception.Message}";
                    return false;
                }
            }
            return true;
        }

        private static bool TryEvaluateTargetDefaultItemPolicy(
            ProjectEvidenceGraph graph,
            ProjectConditionContext context,
            out TargetDefaultItemPolicy policy,
            out string error)
        {
            policy = new TargetDefaultItemPolicy();
            error = string.Empty;
            var values = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["DefaultItemExcludes"] =
                    "bin/**;obj/**;**/*.user;**/*.*proj;**/*.sln;**/*.vssscc;**/.DS_Store;**/.*/**",
                ["DefaultExcludesInProjectFolder"] = string.Empty
            };
            var visited = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            if (graph.DirectoryBuildProps is not null
                && !TryEvaluateTargetGraphProperties(
                    graph,
                    graph.DirectoryBuildProps,
                    context,
                    visited,
                    values,
                    TargetGraphPolicyProperties,
                    out error))
            {
                return false;
            }
            if (!TryEvaluateTargetGraphProperties(
                    graph,
                    graph.TargetProject,
                    context,
                    visited,
                    values,
                    TargetGraphPolicyProperties,
                    out error))
            {
                return false;
            }

            if (!TryReadBooleanProperty(
                    values,
                    "UseWinUI",
                    defaultValue: false,
                    out var useWinUi,
                    out error)
                || !TryReadBooleanProperty(
                    values,
                    "EnableDefaultItems",
                    defaultValue: true,
                    out var enableDefaultItems,
                    out error)
                || !TryReadBooleanProperty(
                    values,
                    "EnableDefaultCompileItems",
                    defaultValue: true,
                    out var enableCompile,
                    out error)
                || !TryReadBooleanProperty(
                    values,
                    "EnableDefaultContentItems",
                    defaultValue: true,
                    out var enableContent,
                    out error)
                || !TryReadBooleanProperty(
                    values,
                    "EnableDefaultWindowsAppSdkContentItems",
                    defaultValue: true,
                    out var enableWindowsAppSdkContent,
                    out error)
                || !TryReadBooleanProperty(
                    values,
                    "EnableDefaultNoneItems",
                    defaultValue: true,
                    out var enableNone,
                    out error)
                || !TryReadBooleanProperty(
                    values,
                    "EnableDefaultPageItems",
                    defaultValue: true,
                    out var enablePage,
                    out error)
                || !TryReadBooleanProperty(
                    values,
                    "EnableDefaultApplicationDefinition",
                    defaultValue: true,
                    out var enableApplicationDefinition,
                    out error))
            {
                return false;
            }
            policy.UseWinUi = useWinUi;
            policy.EnableDefaultItems = enableDefaultItems;
            policy.EnableCompile = enableCompile;
            policy.EnableContent = enableContent;
            policy.EnableWindowsAppSdkContent =
                enableWindowsAppSdkContent;
            policy.EnableNone = enableNone;
            policy.EnablePage = enablePage;
            policy.EnableApplicationDefinition =
                enableApplicationDefinition;

            var priProperty = values.ContainsKey(
                "EnableDefaultPRIResourceItems")
                ? "EnableDefaultPRIResourceItems"
                : "EnableDefaultPriItems";
            if (!TryReadBooleanProperty(
                    values,
                    priProperty,
                    defaultValue: true,
                    out var enablePriResource,
                    out error))
            {
                return false;
            }
            policy.EnablePriResource = enablePriResource;
            if (!TryReadBooleanProperty(
                    values,
                    "EnableDefaultWindowsAppSdkPRIResourceItems",
                    defaultValue: true,
                    out var enableWindowsAppSdkPriResource,
                    out error))
            {
                return false;
            }
            policy.EnableWindowsAppSdkPriResource =
                enableWindowsAppSdkPriResource;
            foreach (var propertyName in new[]
            {
                "DefaultItemExcludes",
                "DefaultExcludesInProjectFolder"
            })
            {
                if (!values.TryGetValue(
                        propertyName,
                        out var value))
                {
                    continue;
                }
                foreach (var pattern in value.Split(
                    ';',
                    StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries))
                {
                    if (!TryNormalizeTargetGraphPattern(
                            pattern,
                            out var normalizedPattern,
                            out error))
                    {
                        error =
                            $"Property '{propertyName}' contains an unsupported exclusion pattern: {error}";
                        return false;
                    }
                    policy.Excludes.Add(normalizedPattern);
                }
            }
            return true;
        }

        private static bool
            TryEvaluateOrderedDirectoryBuildTargetsImport(
                ProjectEvidenceGraph graph,
                ProjectConditionContext context,
                out bool enabled,
                out string? overridePath,
                out string error)
        {
            var values = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["ImportDirectoryBuildTargets"] = "true",
                ["DirectoryBuildTargetsPath"] = string.Empty
            };
            var evaluationStack = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            if (graph.DirectoryBuildProps is not null
                && !TryEvaluateTargetGraphProperties(
                    graph,
                    graph.DirectoryBuildProps,
                    context,
                    evaluationStack,
                    values,
                    DirectoryBuildTargetsControlProperties,
                    out error))
            {
                enabled = false;
                overridePath = null;
                return false;
            }
            if (!TryEvaluateTargetGraphProperties(
                    graph,
                    graph.TargetProject,
                    context,
                    evaluationStack,
                    values,
                    DirectoryBuildTargetsControlProperties,
                    out error)
                || !TryReadBooleanProperty(
                    values,
                    "ImportDirectoryBuildTargets",
                    defaultValue: true,
                    out enabled,
                    out error))
            {
                enabled = false;
                overridePath = null;
                return false;
            }

            overridePath = values["DirectoryBuildTargetsPath"]
                .Trim();
            if (overridePath.Length == 0)
            {
                overridePath = null;
            }
            return true;
        }

        private static bool TryEvaluateTargetGraphProperties(
            ProjectEvidenceGraph graph,
            string relativeProjectFile,
            ProjectConditionContext context,
            HashSet<string> visited,
            Dictionary<string, string> values,
            HashSet<string> propertyNames,
            out string error)
        {
            error = string.Empty;
            if (!visited.Add(relativeProjectFile))
            {
                error =
                    $"The contained import graph has a cycle at '{relativeProjectFile}'.";
                return false;
            }

            var document = graph.Documents[relativeProjectFile];
            var result = TryEvaluateTargetGraphPropertyElements(
                graph,
                relativeProjectFile,
                document.Root!.Elements(),
                context,
                visited,
                values,
                propertyNames,
                out error);
            visited.Remove(relativeProjectFile);
            return result;
        }

        private static bool TryEvaluateTargetGraphPropertyElements(
            ProjectEvidenceGraph graph,
            string relativeProjectFile,
            IEnumerable<XElement> elements,
            ProjectConditionContext context,
            HashSet<string> visited,
            Dictionary<string, string> values,
            HashSet<string> propertyNames,
            out string error)
        {
            error = string.Empty;
            foreach (var child in elements)
            {
                if (IsProjectElement(child, "PropertyGroup"))
                {
                    var groupCondition = EvaluateElementCondition(
                        child,
                        context);
                    if (groupCondition == DeterministicCondition.Unknown
                        && child.Elements().Any(property =>
                            propertyNames.Contains(
                                property.Name.LocalName)))
                    {
                        error =
                            $"A target default-item property group in '{relativeProjectFile}' has a condition that cannot be evaluated deterministically.";
                        return false;
                    }
                    if (groupCondition == DeterministicCondition.False)
                    {
                        continue;
                    }
                    foreach (var property in child.Elements().Where(property =>
                        propertyNames.Contains(
                            property.Name.LocalName)))
                    {
                        var propertyCondition = EvaluateElementCondition(
                            property,
                            context);
                        if (propertyCondition == DeterministicCondition.Unknown)
                        {
                            error =
                                $"Property '{property.Name.LocalName}' in '{relativeProjectFile}' has a condition that cannot be evaluated deterministically.";
                            return false;
                        }
                        if (propertyCondition == DeterministicCondition.False)
                        {
                            continue;
                        }
                        if (!TryExpandTargetGraphProperty(
                                property.Value,
                                values,
                                out var expanded,
                                out error))
                        {
                            error =
                                $"Property '{property.Name.LocalName}' in '{relativeProjectFile}' cannot be evaluated: {error}";
                            return false;
                        }
                        values[property.Name.LocalName] = expanded;
                    }
                    continue;
                }

                if (IsProjectElement(child, "Import"))
                {
                    if (!TryEvaluateTargetGraphImport(
                        graph,
                        relativeProjectFile,
                        child,
                        context,
                        visited,
                        values,
                        propertyNames,
                        out error))
                    {
                        return false;
                    }
                    continue;
                }
                if (IsProjectElement(child, "ImportGroup"))
                {
                    foreach (var import in child.Elements().Where(element =>
                        IsProjectElement(element, "Import")))
                    {
                        if (!TryEvaluateTargetGraphImport(
                            graph,
                            relativeProjectFile,
                            import,
                            context,
                            visited,
                            values,
                            propertyNames,
                            out error))
                        {
                            return false;
                        }
                    }
                }
                if (IsProjectElement(child, "Choose"))
                {
                    if (!TrySelectTargetGraphChooseBranch(
                            child,
                            context,
                            element =>
                                ContainsTargetGraphPropertyConstruct(
                                    element,
                                    propertyNames),
                            out var activeBranch,
                            out error))
                    {
                        return false;
                    }
                    if (activeBranch is not null
                        && !TryEvaluateTargetGraphPropertyElements(
                            graph,
                            relativeProjectFile,
                            activeBranch.Elements(),
                            context,
                            visited,
                            values,
                            propertyNames,
                            out error))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static bool TryEvaluateTargetGraphImport(
            ProjectEvidenceGraph graph,
            string importingProject,
            XElement import,
            ProjectConditionContext context,
            HashSet<string> visited,
            Dictionary<string, string> values,
            HashSet<string> propertyNames,
            out string error)
        {
            error = string.Empty;
            var condition = EvaluateElementCondition(
                import,
                context);
            if (condition == DeterministicCondition.False)
            {
                return true;
            }
            if (condition == DeterministicCondition.Unknown)
            {
                error =
                    $"An import in '{importingProject}' has a condition that cannot be evaluated deterministically.";
                return false;
            }
            var importValue = import.Attribute("Project")?.Value;
            if (string.IsNullOrWhiteSpace(importValue)
                || !TryResolveLiteralImport(
                    graph.TargetRoot,
                    graph.FullPaths[importingProject],
                    importValue,
                    out _,
                    out var importedRelative)
                || !graph.Documents.ContainsKey(importedRelative))
            {
                error =
                    $"An active import in '{importingProject}' is not a modeled contained literal import.";
                return false;
            }
            return TryEvaluateTargetGraphProperties(
                graph,
                importedRelative,
                context,
                visited,
                values,
                propertyNames,
                out error);
        }

        private static bool TryExpandTargetGraphProperty(
            string rawValue,
            Dictionary<string, string> values,
            out string expanded,
            out string error)
        {
            expanded = rawValue.Trim();
            error = string.Empty;
            foreach (Match match in MsBuildPropertyExpressionRegex()
                .Matches(expanded))
            {
                var referencedName = match.Groups["name"].Value;
                if (!values.TryGetValue(
                        referencedName,
                        out var referencedValue))
                {
                    error =
                        $"MSBuild property '$({referencedName})' is not available in the supported evaluation model.";
                    return false;
                }
                expanded = expanded.Replace(
                    match.Value,
                    referencedValue,
                    StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }

        private static bool TryReadBooleanProperty(
            Dictionary<string, string> values,
            string propertyName,
            bool defaultValue,
            out bool value,
            out string error)
        {
            error = string.Empty;
            if (!values.TryGetValue(
                    propertyName,
                    out var rawValue)
                || string.IsNullOrWhiteSpace(rawValue))
            {
                value = defaultValue;
                return true;
            }
            if (!bool.TryParse(
                    rawValue.Trim(),
                    out value))
            {
                error =
                    $"Property '{propertyName}' has non-Boolean value '{rawValue}'.";
                return false;
            }
            return true;
        }

        private static bool TryCollectTargetItemOperations(
            ProjectEvidenceGraph graph,
            ProjectConditionContext context,
            out List<TargetItemOperation> preDefaultOperations,
            out List<TargetItemOperation> postDefaultOperations,
            out string error)
        {
            preDefaultOperations = [];
            postDefaultOperations = [];
            error = string.Empty;
            var visited = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            if (graph.DirectoryBuildProps is not null
                && !TryCollectTargetItemOperationsFromDocument(
                    graph,
                    graph.DirectoryBuildProps,
                    context,
                    visited,
                    preDefaultOperations,
                    out error))
            {
                return false;
            }

            visited.Clear();
            if (!TryCollectTargetItemOperationsFromDocument(
                    graph,
                    graph.TargetProject,
                    context,
                    visited,
                    postDefaultOperations,
                    out error))
            {
                return false;
            }
            if (graph.DirectoryBuildTargets is not null)
            {
                visited.Clear();
                if (!TryCollectTargetItemOperationsFromDocument(
                        graph,
                        graph.DirectoryBuildTargets,
                        context,
                        visited,
                        postDefaultOperations,
                        out error))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool TryCollectTargetItemOperationsFromDocument(
            ProjectEvidenceGraph graph,
            string relativeProjectFile,
            ProjectConditionContext context,
            HashSet<string> visited,
            List<TargetItemOperation> operations,
            out string error)
        {
            error = string.Empty;
            if (!visited.Add(relativeProjectFile))
            {
                error =
                    $"The contained import graph has a cycle at '{relativeProjectFile}'.";
                return false;
            }

            var document = graph.Documents[relativeProjectFile];
            var result = TryCollectTargetItemOperationElements(
                graph,
                relativeProjectFile,
                document.Root!.Elements(),
                context,
                visited,
                operations,
                out error);
            visited.Remove(relativeProjectFile);
            return result;
        }

        private static bool TryCollectTargetItemOperationElements(
            ProjectEvidenceGraph graph,
            string relativeProjectFile,
            IEnumerable<XElement> elements,
            ProjectConditionContext context,
            HashSet<string> visited,
            List<TargetItemOperation> operations,
            out string error)
        {
            error = string.Empty;
            foreach (var child in elements)
            {
                if (IsProjectElement(child, "ItemGroup"))
                {
                    foreach (var item in child.Elements().Where(element =>
                        TargetGraphRelevantItemKinds.Contains(
                            element.Name.LocalName)))
                    {
                        var condition = EvaluateElementCondition(
                            item,
                            context);
                        if (condition == DeterministicCondition.False)
                        {
                            continue;
                        }
                        if (condition == DeterministicCondition.Unknown)
                        {
                            error =
                                $"{item.Name.LocalName} item evidence in '{relativeProjectFile}' has a condition that cannot be evaluated deterministically.";
                            return false;
                        }
                        foreach (var operationName in new[]
                        {
                            "Include",
                            "Remove"
                        })
                        {
                            var value = item.Attribute(operationName)?.Value;
                            if (string.IsNullOrWhiteSpace(value))
                            {
                                continue;
                            }
                            if (value.Contains(
                                    "$(",
                                    StringComparison.Ordinal)
                                || value.Contains(
                                    "@(",
                                    StringComparison.Ordinal))
                            {
                                error =
                                    $"{item.Name.LocalName} {operationName} in '{relativeProjectFile}' is property/item-expanded and cannot prove nested-project coverage.";
                                return false;
                            }
                            if (!TryNormalizeTargetGraphPatternList(
                                    value,
                                    out var normalizedValue,
                                    out error))
                            {
                                error =
                                    $"{item.Name.LocalName} {operationName} in '{relativeProjectFile}' cannot prove nested-project coverage: {error}";
                                return false;
                            }
                            var exclude = item
                                .Attribute("Exclude")
                                ?.Value;
                            string? normalizedExclude = null;
                            if (!string.IsNullOrWhiteSpace(exclude)
                                && !TryNormalizeTargetGraphPatternList(
                                    exclude,
                                    out normalizedExclude,
                                    out error))
                            {
                                error =
                                    $"{item.Name.LocalName} Exclude in '{relativeProjectFile}' cannot prove nested-project coverage: {error}";
                                return false;
                            }
                            operations.Add(new TargetItemOperation(
                                CanonicalTargetGraphItemKind(
                                    item.Name.LocalName),
                                operationName,
                                normalizedValue,
                                normalizedExclude));
                        }
                    }
                    continue;
                }

                if (IsProjectElement(child, "Import"))
                {
                    if (!TryCollectTargetGraphImportOperations(
                        graph,
                        relativeProjectFile,
                        child,
                        context,
                        visited,
                        operations,
                        out error))
                    {
                        return false;
                    }
                    continue;
                }
                if (IsProjectElement(child, "ImportGroup"))
                {
                    foreach (var import in child.Elements().Where(element =>
                        IsProjectElement(element, "Import")))
                    {
                        if (!TryCollectTargetGraphImportOperations(
                            graph,
                            relativeProjectFile,
                            import,
                            context,
                            visited,
                            operations,
                            out error))
                        {
                            return false;
                        }
                    }
                }
                if (IsProjectElement(child, "Choose"))
                {
                    if (!TrySelectTargetGraphChooseBranch(
                            child,
                            context,
                            ContainsTargetGraphItemConstruct,
                            out var activeBranch,
                            out error))
                    {
                        return false;
                    }
                    if (activeBranch is not null
                        && !TryCollectTargetItemOperationElements(
                            graph,
                            relativeProjectFile,
                            activeBranch.Elements(),
                            context,
                            visited,
                            operations,
                            out error))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static bool TrySelectTargetGraphChooseBranch(
            XElement choose,
            ProjectConditionContext context,
            Func<XElement, bool> containsRelevantConstruct,
            out XElement? activeBranch,
            out string error)
        {
            activeBranch = null;
            error = string.Empty;
            var branches = choose.Elements().Where(element =>
                    IsProjectElement(element, "When")
                    || IsProjectElement(element, "Otherwise"))
                .ToList();
            for (var index = 0;
                 index < branches.Count;
                 index++)
            {
                var branch = branches[index];
                var condition = EvaluateElementCondition(
                    branch,
                    context);
                if (condition == DeterministicCondition.Unknown)
                {
                    if (branches
                        .Skip(index)
                        .SelectMany(candidate =>
                            candidate.Descendants())
                        .Any(containsRelevantConstruct))
                    {
                        error =
                            "A Choose branch affecting target default-item coverage cannot be selected deterministically.";
                        return false;
                    }
                    return true;
                }
                if (condition == DeterministicCondition.True)
                {
                    activeBranch = branch;
                    return true;
                }
            }
            return true;
        }

        private static bool ContainsTargetGraphPropertyConstruct(
            XElement element,
            HashSet<string> propertyNames) =>
            propertyNames.Contains(
                element.Name.LocalName)
            || IsProjectElement(element, "Import");

        private static bool ContainsTargetGraphItemConstruct(
            XElement element) =>
            TargetGraphRelevantItemKinds.Contains(
                element.Name.LocalName)
            || IsProjectElement(element, "Import");

        private static bool TryCollectTargetGraphImportOperations(
            ProjectEvidenceGraph graph,
            string importingProject,
            XElement import,
            ProjectConditionContext context,
            HashSet<string> visited,
            List<TargetItemOperation> operations,
            out string error)
        {
            error = string.Empty;
            var condition = EvaluateElementCondition(
                import,
                context);
            if (condition == DeterministicCondition.False)
            {
                return true;
            }
            if (condition == DeterministicCondition.Unknown)
            {
                error =
                    $"An import in '{importingProject}' has a condition that cannot be evaluated deterministically.";
                return false;
            }
            var importValue = import.Attribute("Project")?.Value;
            if (string.IsNullOrWhiteSpace(importValue)
                || !TryResolveLiteralImport(
                    graph.TargetRoot,
                    graph.FullPaths[importingProject],
                    importValue,
                    out _,
                    out var importedRelative)
                || !graph.Documents.ContainsKey(importedRelative))
            {
                error =
                    $"An active import in '{importingProject}' is not a modeled contained literal import.";
                return false;
            }
            return TryCollectTargetItemOperationsFromDocument(
                graph,
                importedRelative,
                context,
                visited,
                operations,
                out error);
        }

        private static bool IsFileIncludedByProject(
            string path,
            TargetOwnedItemKind kind,
            TargetDefaultItemPolicy policy,
            IReadOnlyCollection<TargetItemOperation> preDefaultOperations,
            IReadOnlyCollection<TargetItemOperation> postDefaultOperations)
        {
            var included = false;
            ApplyTargetItemOperations(
                preDefaultOperations,
                kind.Kind,
                path,
                ref included);
            if (policy.IsDefaultIncluded(
                    kind.Kind,
                    path))
            {
                included = true;
            }
            ApplyTargetItemOperations(
                postDefaultOperations,
                kind.Kind,
                path,
                ref included);
            return included;
        }

        private static void ApplyTargetItemOperations(
            IEnumerable<TargetItemOperation> operations,
            string kind,
            string path,
            ref bool included)
        {
            foreach (var operation in operations.Where(operation =>
                operation.Kind.Equals(
                    kind,
                    StringComparison.OrdinalIgnoreCase)
                && MsBuildGlobMatches(
                    operation.Pattern,
                    path)))
            {
                if (operation.Operation == "Include")
                {
                    if (string.IsNullOrWhiteSpace(
                            operation.ExcludePattern)
                        || !MsBuildGlobMatches(
                            operation.ExcludePattern,
                            path))
                    {
                        included = true;
                    }
                }
                else
                {
                    included = false;
                }
            }
        }

        private static bool IsWindowsAppSdkImage(
            string path) =>
            Path.GetExtension(path).ToLowerInvariant()
                is ".png"
                or ".bmp"
                or ".jpg"
                or ".dds"
                or ".tif"
                or ".tga"
                or ".gif";

        private static string CanonicalTargetGraphItemKind(
            string kind) =>
            kind.ToUpperInvariant() switch
            {
                "COMPILE" => "Compile",
                "PAGE" => "Page",
                "APPLICATIONDEFINITION" =>
                    "ApplicationDefinition",
                "PRIRESOURCE" => "PRIResource",
                "CONTENT" => "Content",
                "NONE" => "None",
                _ => kind
            };

        private static bool MsBuildGlobMatches(
            string patternList,
            string path)
        {
            var normalizedPath = NormalizeProjectItemPath(path);
            foreach (var pattern in patternList.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries))
            {
                var normalizedPattern =
                    NormalizeProjectItemPath(pattern);
                if (normalizedPattern.StartsWith(
                        "./",
                        StringComparison.Ordinal))
                {
                    normalizedPattern = normalizedPattern[2..];
                }
                var regex = "^"
                    + Regex.Escape(normalizedPattern)
                        .Replace(
                            @"\*\*/",
                            "(?:.*/)?",
                            StringComparison.Ordinal)
                        .Replace(
                            @"\*\*",
                            ".*",
                            StringComparison.Ordinal)
                        .Replace(
                            @"\*",
                            "[^/]*",
                            StringComparison.Ordinal)
                        .Replace(
                            @"\?",
                            "[^/]",
                            StringComparison.Ordinal)
                    + "$";
                if (Regex.IsMatch(
                        normalizedPath,
                        regex,
                        RegexOptions.IgnoreCase
                        | RegexOptions.CultureInvariant))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool TryNormalizeTargetGraphPatternList(
            string value,
            out string normalized,
            out string error)
        {
            var patterns = new List<string>();
            foreach (var pattern in value.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries))
            {
                if (!TryNormalizeTargetGraphPattern(
                        pattern,
                        out var normalizedPattern,
                        out error))
                {
                    normalized = string.Empty;
                    return false;
                }
                patterns.Add(normalizedPattern);
            }
            normalized = string.Join(';', patterns);
            error = string.Empty;
            return true;
        }

        private static bool TryNormalizeTargetGraphPattern(
            string value,
            out string normalized,
            out string error)
        {
            normalized = NormalizeProjectItemPath(
                value.Trim());
            error = string.Empty;
            if (normalized.Contains(
                    "$(",
                    StringComparison.Ordinal)
                || normalized.Contains(
                    "@(",
                    StringComparison.Ordinal)
                || normalized.Contains('%')
                || normalized.Contains(':')
                || Path.IsPathRooted(normalized))
            {
                error =
                    $"pattern '{value}' is rooted, escaped, or property/item-expanded.";
                return false;
            }

            var segments = normalized.Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(segment =>
                    segment == ".."
                    || (segment != "."
                        && segment.TrimEnd(
                            ' ',
                            '.') != segment)))
            {
                error =
                    $"pattern '{value}' contains traversal or a Windows trailing-dot/space alias.";
                return false;
            }
            normalized = string.Join(
                '/',
                segments.Where(segment => segment != "."));
            if (normalized.Length == 0)
            {
                error =
                    $"pattern '{value}' does not contain an item path.";
                return false;
            }
            return true;
        }

        [GeneratedRegex(
            @"\$\((?<name>[^)]+)\)",
            RegexOptions.CultureInvariant)]
        private static partial Regex MsBuildPropertyExpressionRegex();
    }
}
