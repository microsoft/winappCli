// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using WinApp.Cli.Models;

namespace WinApp.Cli.Commands;

internal partial class MigrateCommand
{
    public partial class Handler
    {
        private const string LegacyMsBuildNamespace =
            "http://schemas.microsoft.com/developer/msbuild/2003";

        private enum DeterministicCondition
        {
            False,
            True,
            Unknown
        }

        private sealed record ProjectConditionContext(string ProjectName);

        private sealed class ProjectEvidenceGraph
        {
            internal required string TargetProject { get; init; }

            internal Dictionary<string, XDocument> Documents { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            internal Dictionary<string, string> RejectedDocuments { get; } =
                new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed record ProjectItemEvidenceResult(
            List<MigrationLocation> Matches,
            string? FailureReason);

        private static bool TryBuildProjectEvidenceGraph(
            string targetRoot,
            string targetProject,
            out ProjectEvidenceGraph graph,
            out string error)
        {
            graph = null!;
            error = string.Empty;
            if (!MigrationPathResolver.TryResolveContainedRelativePath(
                    targetRoot,
                    Path.GetRelativePath(targetRoot, targetProject),
                    out targetProject,
                    out var targetProjectRelative,
                    out error))
            {
                return false;
            }

            graph = new ProjectEvidenceGraph
            {
                TargetProject = targetProjectRelative
            };
            var context = new ProjectConditionContext(
                Path.GetFileNameWithoutExtension(targetProject));
            if (!TryAddParticipatingProjectFile(
                    targetRoot,
                    targetProject,
                    graph,
                    context,
                    required: true,
                    out error))
            {
                return false;
            }

            var targetDocument = graph.Documents[targetProjectRelative];
            if (!IsSdkStyleProject(targetDocument))
            {
                return true;
            }

            AddAutomaticDirectoryBuildFile(
                targetRoot,
                targetProject,
                "Directory.Build.props",
                "ImportDirectoryBuildProps",
                "DirectoryBuildPropsPath",
                graph,
                context);
            AddAutomaticDirectoryBuildFile(
                targetRoot,
                targetProject,
                "Directory.Build.targets",
                "ImportDirectoryBuildTargets",
                "DirectoryBuildTargetsPath",
                graph,
                context);
            return true;
        }

        private static bool TryValidateProjectEvidenceFiles(
            ProjectEvidenceGraph graph,
            IReadOnlyCollection<string> evidenceFiles,
            out string error)
        {
            error = string.Empty;
            foreach (var evidenceFile in evidenceFiles)
            {
                if (graph.Documents.ContainsKey(evidenceFile))
                {
                    continue;
                }
                error = graph.RejectedDocuments.TryGetValue(
                    evidenceFile,
                    out var rejectedReason)
                    ? rejectedReason
                    : $"Evidence file '{evidenceFile}' is not the target project, an active Directory.Build file, or reachable through active literal imports.";
                return false;
            }
            return true;
        }

        private static bool TryAddParticipatingProjectFile(
            string targetRoot,
            string projectFile,
            ProjectEvidenceGraph graph,
            ProjectConditionContext context,
            bool required,
            out string error)
        {
            error = string.Empty;
            if (!MigrationPathResolver.TryResolveContainedRelativePath(
                    targetRoot,
                    Path.GetRelativePath(targetRoot, projectFile),
                    out projectFile,
                    out var relativePath,
                    out error))
            {
                return !required;
            }
            if (graph.Documents.ContainsKey(relativePath))
            {
                return true;
            }

            XDocument document;
            try
            {
                document = XDocument.Load(
                    projectFile,
                    LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            }
            catch (Exception exception) when (
                exception is XmlException or IOException)
            {
                error = $"MSBuild evidence file '{relativePath}' could not be read: {exception.Message}";
                graph.RejectedDocuments[relativePath] = error;
                return !required;
            }

            if (!IsProjectRoot(document.Root))
            {
                error =
                    $"MSBuild evidence file '{relativePath}' does not have a supported Project root.";
                graph.RejectedDocuments[relativePath] = error;
                return !required;
            }

            var rootCondition = EvaluateCondition(
                document.Root!.Attribute("Condition")?.Value,
                context);
            if (rootCondition != DeterministicCondition.True)
            {
                error = rootCondition == DeterministicCondition.False
                    ? $"MSBuild evidence file '{relativePath}' has an inactive Project condition."
                    : $"MSBuild evidence file '{relativePath}' has a Project condition that cannot be proven active.";
                graph.RejectedDocuments[relativePath] = error;
                return !required;
            }

            graph.Documents.Add(relativePath, document);
            foreach (var import in document.Descendants().Where(element =>
                IsEvaluationImport(element)
                && IsProjectElement(element, "Import")))
            {
                var importValue = import.Attribute("Project")?.Value.Trim();
                if (string.IsNullOrWhiteSpace(importValue)
                    || !TryResolveLiteralImport(
                        targetRoot,
                        projectFile,
                        importValue,
                        out var importedProject,
                        out var importedRelative))
                {
                    continue;
                }

                var condition = EvaluateElementCondition(import, context);
                if (condition != DeterministicCondition.True)
                {
                    graph.RejectedDocuments[importedRelative] =
                        condition == DeterministicCondition.False
                            ? $"Import of '{importedRelative}' is deterministically inactive."
                            : $"Import of '{importedRelative}' is conditioned and cannot be proven active.";
                    continue;
                }
                if (!File.Exists(importedProject))
                {
                    graph.RejectedDocuments[importedRelative] =
                        $"Imported evidence file '{importedRelative}' does not exist.";
                    continue;
                }

                TryAddParticipatingProjectFile(
                    targetRoot,
                    importedProject,
                    graph,
                    context,
                    required: false,
                    out _);
            }
            return true;
        }

        private static void AddAutomaticDirectoryBuildFile(
            string targetRoot,
            string targetProject,
            string fileName,
            string importEnabledProperty,
            string overridePathProperty,
            ProjectEvidenceGraph graph,
            ProjectConditionContext context)
        {
            var automaticFile = FindNearestContainedAncestorFile(
                targetRoot,
                Path.GetDirectoryName(targetProject)!,
                fileName);
            if (automaticFile is null)
            {
                return;
            }
            var automaticRelative = NormalizePath(
                Path.GetRelativePath(targetRoot, automaticFile));
            if (!IsAutomaticDirectoryBuildImportEnabled(
                    graph.Documents.Values,
                    importEnabledProperty,
                    overridePathProperty,
                    context,
                    out var reason))
            {
                graph.RejectedDocuments[automaticRelative] = reason;
                return;
            }

            TryAddParticipatingProjectFile(
                targetRoot,
                automaticFile,
                graph,
                context,
                required: false,
                out _);
        }

        private static bool IsAutomaticDirectoryBuildImportEnabled(
            IEnumerable<XDocument> participatingDocuments,
            string importEnabledProperty,
            string overridePathProperty,
            ProjectConditionContext context,
            out string reason)
        {
            reason = string.Empty;
            foreach (var property in participatingDocuments
                .SelectMany(document => document.Descendants())
                .Where(element =>
                    IsEvaluationProperty(element)
                    && (IsProjectElement(element, importEnabledProperty)
                        || IsProjectElement(element, overridePathProperty))))
            {
                var condition = EvaluateElementCondition(property, context);
                if (condition == DeterministicCondition.Unknown)
                {
                    reason =
                        $"Automatic Directory.Build import control '{property.Name.LocalName}' is conditioned and cannot be proven active.";
                    return false;
                }
                if (condition == DeterministicCondition.False)
                {
                    continue;
                }
                if (property.Name.LocalName.Equals(
                        overridePathProperty,
                        StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(property.Value))
                {
                    reason =
                        $"Automatic Directory.Build discovery is overridden by {overridePathProperty}.";
                    return false;
                }
                if (property.Name.LocalName.Equals(
                        importEnabledProperty,
                        StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(property.Value.Trim(), out var enabled)
                    && !enabled)
                {
                    reason =
                        $"Automatic Directory.Build import is disabled by {importEnabledProperty}.";
                    return false;
                }
            }
            return true;
        }

        private static string? FindNearestContainedAncestorFile(
            string targetRoot,
            string startDirectory,
            string fileName)
        {
            var canonicalRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(targetRoot));
            for (var directory = new DirectoryInfo(startDirectory);
                 directory is not null;
                 directory = directory.Parent)
            {
                var relative = Path.GetRelativePath(
                    canonicalRoot,
                    directory.FullName);
                if (relative == ".."
                    || relative.StartsWith(
                        $"..{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal))
                {
                    break;
                }

                var candidate = Path.Combine(directory.FullName, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                if (string.Equals(
                    directory.FullName,
                    canonicalRoot,
                    StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }
            return null;
        }

        private static bool TryResolveLiteralImport(
            string targetRoot,
            string importingProject,
            string value,
            out string fullPath,
            out string normalizedRelativePath)
        {
            fullPath = string.Empty;
            normalizedRelativePath = string.Empty;
            if (Path.IsPathRooted(value)
                || value.Contains(':')
                || value.Contains("$(", StringComparison.Ordinal)
                || value.Contains("@(", StringComparison.Ordinal)
                || value.IndexOfAny(['*', '?']) >= 0)
            {
                return false;
            }

            string candidate;
            try
            {
                candidate = Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(importingProject)!,
                    value.Replace(
                        Path.AltDirectorySeparatorChar,
                        Path.DirectorySeparatorChar)));
            }
            catch (Exception exception) when (
                exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
            {
                return false;
            }
            var candidateRelative = Path.GetRelativePath(targetRoot, candidate);
            return MigrationPathResolver.TryResolveContainedRelativePath(
                targetRoot,
                candidateRelative,
                out fullPath,
                out normalizedRelativePath,
                out _);
        }

        private static ProjectItemEvidenceResult FindProjectItemEvidence(
            ProjectEvidenceGraph graph,
            IReadOnlyCollection<string> evidenceFiles,
            string itemType,
            string targetPath,
            IReadOnlyCollection<MigrationProjectItemMetadata> requiredMetadata,
            bool allowUpdate)
        {
            var selectedFiles = new[] { graph.TargetProject }
                .Concat(evidenceFiles)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var evidenceFile in selectedFiles)
            {
                if (graph.Documents.ContainsKey(evidenceFile))
                {
                    continue;
                }
                var reason = graph.RejectedDocuments.TryGetValue(
                    evidenceFile,
                    out var rejectedReason)
                    ? rejectedReason
                    : $"Evidence file '{evidenceFile}' is not the target project, an active Directory.Build file, or reachable through active literal imports.";
                return new ProjectItemEvidenceResult([], reason);
            }

            if (HasPotentialActiveRemoval(
                    graph,
                    itemType,
                    targetPath,
                    out var removalReason))
            {
                return new ProjectItemEvidenceResult([], removalReason);
            }

            var matches = new List<MigrationLocation>();
            var updateOnly = false;
            string? conditionedReason = null;
            foreach (var relativeProjectFile in selectedFiles)
            {
                var document = graph.Documents[relativeProjectFile];
                foreach (var item in document.Descendants().Where(element =>
                    IsEvaluationItem(element)
                    && IsProjectElement(element, itemType)))
                {
                    var includeMatches = ItemAttributeMatches(
                        item.Attribute("Include")?.Value,
                        targetPath);
                    var updateMatches = ItemAttributeMatches(
                        item.Attribute("Update")?.Value,
                        targetPath);
                    if (!includeMatches
                        && (!allowUpdate || !updateMatches))
                    {
                        updateOnly |= updateMatches;
                        continue;
                    }

                    var condition = EvaluateElementCondition(
                        item,
                        new ProjectConditionContext(
                            Path.GetFileNameWithoutExtension(
                                graph.TargetProject)));
                    if (condition == DeterministicCondition.Unknown)
                    {
                        conditionedReason =
                            $"Matching {itemType} evidence in '{relativeProjectFile}' is conditioned and cannot be proven active.";
                        continue;
                    }
                    if (condition == DeterministicCondition.False
                        || IncludeExcludesTarget(item, targetPath)
                        || !MetadataMatches(
                            item,
                            requiredMetadata,
                            new ProjectConditionContext(
                                Path.GetFileNameWithoutExtension(
                                    graph.TargetProject))))
                    {
                        continue;
                    }

                    matches.Add(new MigrationLocation
                    {
                        Path = NormalizePath(relativeProjectFile),
                        Line = (item as IXmlLineInfo)?.HasLineInfo() == true
                            ? ((IXmlLineInfo)item).LineNumber
                            : null
                    });
                }
            }

            if (matches.Count > 0)
            {
                return new ProjectItemEvidenceResult(matches, null);
            }
            if (conditionedReason is not null)
            {
                return new ProjectItemEvidenceResult([], conditionedReason);
            }
            if (updateOnly && !allowUpdate)
            {
                return new ProjectItemEvidenceResult(
                    [],
                    $"A matching {itemType} Update was found, but Update does not include an item.");
            }
            return new ProjectItemEvidenceResult([], null);
        }

        private static bool TryVerifySdkDefaultPriResourceCoverage(
            ProjectEvidenceGraph graph,
            string targetPath,
            out string reason)
        {
            reason = string.Empty;
            var targetDocument = graph.Documents[graph.TargetProject];
            if (!IsSdkStyleProject(targetDocument))
            {
                reason =
                    "The target project is not SDK-style, so default PRIResource inclusion cannot be proven.";
                return false;
            }

            var context = new ProjectConditionContext(
                Path.GetFileNameWithoutExtension(graph.TargetProject));
            var useWinUi = FindActivePropertyValues(
                [targetDocument],
                "UseWinUI",
                context,
                out var hasUnknownUseWinUi);
            var allUseWinUi = FindActivePropertyValues(
                graph,
                "UseWinUI",
                context,
                out var hasUnknownUseWinUiOverride);
            if (hasUnknownUseWinUi
                || hasUnknownUseWinUiOverride
                || !useWinUi.Any(value =>
                    bool.TryParse(value, out var enabled) && enabled)
                || allUseWinUi.Any(value =>
                    bool.TryParse(value, out var enabled) && !enabled))
            {
                reason =
                    "The active target build does not deterministically enable UseWinUI, so default .resw PRIResource inclusion cannot be proven.";
                return false;
            }

            foreach (var propertyName in new[]
            {
                "EnableDefaultItems",
                "EnableDefaultPRIResourceItems",
                "EnableDefaultPriItems"
            })
            {
                var values = FindActivePropertyValues(
                    graph,
                    propertyName,
                    context,
                    out var hasUnknown);
                if (hasUnknown
                    || values.Any(value =>
                        bool.TryParse(value, out var enabled) && !enabled))
                {
                    reason =
                        $"{propertyName} prevents deterministic SDK default PRIResource coverage.";
                    return false;
                }
            }

            foreach (var propertyName in new[]
            {
                "DefaultItemExcludes",
                "DefaultItemExcludesInProjectFolder"
            })
            {
                var values = FindActivePropertyValues(
                    graph,
                    propertyName,
                    context,
                    out var hasUnknown);
                if (hasUnknown)
                {
                    reason =
                        $"{propertyName} is conditioned and default PRIResource coverage cannot be proven.";
                    return false;
                }
                foreach (var value in values)
                {
                    foreach (var pattern in value.Split(
                        ';',
                        StringSplitOptions.RemoveEmptyEntries
                        | StringSplitOptions.TrimEntries))
                    {
                        if (pattern.Equals(
                            $"$({propertyName})",
                            StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        if (PotentialProjectPatternMatches(pattern, targetPath))
                        {
                            reason =
                                $"{propertyName} may exclude '{targetPath}' from SDK default items.";
                            return false;
                        }
                    }
                }
            }

            if (HasPotentialActiveRemoval(
                    graph,
                    "PRIResource",
                    targetPath,
                    out reason))
            {
                return false;
            }
            return true;
        }

        private static List<string> FindActivePropertyValues(
            ProjectEvidenceGraph graph,
            string propertyName,
            ProjectConditionContext context,
            out bool hasUnknown)
            => FindActivePropertyValues(
                graph.Documents.Values,
                propertyName,
                context,
                out hasUnknown);

        private static List<string> FindActivePropertyValues(
            IEnumerable<XDocument> documents,
            string propertyName,
            ProjectConditionContext context,
            out bool hasUnknown)
        {
            hasUnknown = false;
            var values = new List<string>();
            foreach (var document in documents)
            {
                foreach (var property in document.Descendants().Where(element =>
                    IsEvaluationProperty(element)
                    && IsProjectElement(element, propertyName)))
                {
                    var condition = EvaluateElementCondition(property, context);
                    hasUnknown |= condition == DeterministicCondition.Unknown;
                    if (condition == DeterministicCondition.True)
                    {
                        values.Add(property.Value.Trim());
                    }
                }
            }
            return values;
        }

        private static bool HasPotentialActiveRemoval(
            ProjectEvidenceGraph graph,
            string itemType,
            string targetPath,
            out string reason)
        {
            reason = string.Empty;
            var context = new ProjectConditionContext(
                Path.GetFileNameWithoutExtension(graph.TargetProject));
            foreach (var (relativePath, document) in graph.Documents)
            {
                foreach (var item in document.Descendants().Where(element =>
                    IsEvaluationItem(element)
                    && IsProjectElement(element, itemType)
                    && PotentialProjectPatternMatches(
                        element.Attribute("Remove")?.Value,
                        targetPath)))
                {
                    var condition = EvaluateElementCondition(item, context);
                    if (condition == DeterministicCondition.False)
                    {
                        continue;
                    }
                    reason = condition == DeterministicCondition.Unknown
                        ? $"A {itemType} removal in '{relativePath}' may remove '{targetPath}', but its condition cannot be proven."
                        : $"An active {itemType} Remove excludes '{targetPath}' from the target build.";
                    return true;
                }
            }
            return false;
        }

        private static bool IncludeExcludesTarget(
            XElement item,
            string targetPath) =>
            PotentialProjectPatternMatches(
                item.Attribute("Exclude")?.Value,
                targetPath);

        private static bool ItemAttributeMatches(
            string? value,
            string targetPath)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            return value.Split(
                    ';',
                    StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries)
                .Where(path =>
                    !path.Contains("$(", StringComparison.Ordinal)
                    && !path.Contains("@(", StringComparison.Ordinal)
                    && path.IndexOfAny(['*', '?']) < 0)
                .Select(NormalizeProjectItemPath)
                .Contains(targetPath, StringComparer.OrdinalIgnoreCase);
        }

        private static bool PotentialProjectPatternMatches(
            string? value,
            string targetPath)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            foreach (var rawPattern in value.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries))
            {
                var pattern = NormalizeProjectItemPath(rawPattern);
                if (string.Equals(
                    pattern,
                    targetPath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                var wildcardIndex = pattern.IndexOfAny(['*', '?']);
                if (pattern.Contains("$(", StringComparison.Ordinal)
                    || pattern.Contains("@(", StringComparison.Ordinal))
                {
                    return true;
                }
                if (wildcardIndex >= 0)
                {
                    var literalPrefix = pattern[..wildcardIndex]
                        .TrimEnd('/', '\\');
                    if (literalPrefix.Length == 0
                        || targetPath.StartsWith(
                            literalPrefix,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool MetadataMatches(
            XElement targetItem,
            IReadOnlyCollection<MigrationProjectItemMetadata> requiredMetadata,
            ProjectConditionContext context) =>
            requiredMetadata.All(metadata => targetItem.Elements().Any(element =>
                IsProjectElement(element, metadata.Name)
                && EvaluateElementCondition(element, context)
                    == DeterministicCondition.True
                && string.Equals(
                    element.Value.Trim(),
                    metadata.Value.Trim(),
                    StringComparison.Ordinal)));

        private static DeterministicCondition EvaluateElementCondition(
            XElement element,
            ProjectConditionContext context)
        {
            var result = DeterministicCondition.True;
            foreach (var current in element.AncestorsAndSelf().Reverse())
            {
                var evaluation = EvaluateCondition(
                    current.Attribute("Condition")?.Value,
                    context);
                if (evaluation == DeterministicCondition.False)
                {
                    return DeterministicCondition.False;
                }
                if (evaluation == DeterministicCondition.Unknown)
                {
                    result = DeterministicCondition.Unknown;
                }
            }
            return result;
        }

        private static DeterministicCondition EvaluateCondition(
            string? condition,
            ProjectConditionContext context)
        {
            if (string.IsNullOrWhiteSpace(condition))
            {
                return DeterministicCondition.True;
            }

            var trimmed = condition.Trim();
            if (trimmed.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                return DeterministicCondition.True;
            }
            if (trimmed.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                return DeterministicCondition.False;
            }

            var match = ProjectNameCondition().Match(trimmed);
            if (!match.Success)
            {
                return DeterministicCondition.Unknown;
            }
            var equal = string.Equals(
                context.ProjectName,
                match.Groups["value"].Value,
                StringComparison.OrdinalIgnoreCase);
            return match.Groups["operator"].Value == "=="
                ? equal
                    ? DeterministicCondition.True
                    : DeterministicCondition.False
                : equal
                    ? DeterministicCondition.False
                    : DeterministicCondition.True;
        }

        private static bool IsSdkStyleProject(XDocument document)
        {
            if (!IsProjectRoot(document.Root))
            {
                return false;
            }
            return !string.IsNullOrWhiteSpace(
                    document.Root!.Attribute("Sdk")?.Value)
                || document.Root.Elements().Any(element =>
                    IsProjectElement(element, "Sdk")
                    && !string.IsNullOrWhiteSpace(
                        element.Attribute("Name")?.Value));
        }

        private static bool IsProjectRoot(XElement? element) =>
            element is not null
            && (element.Name == XName.Get("Project")
                || element.Name ==
                    XName.Get("Project", LegacyMsBuildNamespace));

        private static bool IsProjectElement(
            XElement element,
            string localName)
        {
            var root = element.Document?.Root;
            return IsProjectRoot(root)
                && element.Name.Namespace == root!.Name.Namespace
                && element.Name.LocalName.Equals(
                    localName,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsEvaluationImport(XElement element)
        {
            if (element.Parent == element.Document?.Root)
            {
                return true;
            }
            return element.Parent is { } importGroup
                && IsProjectElement(importGroup, "ImportGroup")
                && importGroup.Parent == element.Document?.Root;
        }

        private static bool IsEvaluationItem(XElement element) =>
            element.Parent is { } itemGroup
            && IsProjectElement(itemGroup, "ItemGroup")
            && itemGroup.Parent == element.Document?.Root;

        private static bool IsEvaluationProperty(XElement element) =>
            element.Parent is { } propertyGroup
            && IsProjectElement(propertyGroup, "PropertyGroup")
            && propertyGroup.Parent == element.Document?.Root;

        [GeneratedRegex(
            @"^\s*'\$\(MSBuildProjectName\)'\s*(?<operator>==|!=)\s*'(?<value>[^']*)'\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex ProjectNameCondition();
    }
}
