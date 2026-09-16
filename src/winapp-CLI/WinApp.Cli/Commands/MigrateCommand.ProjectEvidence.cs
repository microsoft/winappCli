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

        private static readonly HashSet<string> ClosureRelevantProperties =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "UseWinUI",
                "EnableDefaultItems",
                "EnableDefaultPRIResourceItems",
                "EnableDefaultPriItems",
                "DefaultItemExcludes",
                "DefaultExcludesInProjectFolder",
                "ImportDirectoryBuildProps",
                "ImportDirectoryBuildTargets",
                "DirectoryBuildPropsPath",
                "DirectoryBuildTargetsPath"
            };

        private enum DeterministicCondition
        {
            False,
            True,
            Unknown
        }

        private enum DirectoryBuildImportStatus
        {
            Disabled,
            Enabled,
            Unmodeled
        }

        private sealed record ProjectConditionContext(string ProjectName);

        private sealed class ProjectEvidenceGraph
        {
            internal required string TargetRoot { get; init; }

            internal required string TargetProject { get; init; }

            internal Dictionary<string, XDocument> Documents { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            internal Dictionary<string, string> FullPaths { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            internal Dictionary<string, string> RejectedDocuments { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            internal List<string> IncompleteReasons { get; } = [];

            internal string? DirectoryBuildProps { get; set; }

            internal string? DirectoryBuildTargets { get; set; }
        }

        private sealed record ProjectItemEvidenceResult(
            List<MigrationLocation> Matches,
            string? FailureReason);

        private static bool TryBuildProjectEvidenceGraph(
            string targetRoot,
            string targetProject,
            out ProjectEvidenceGraph graph,
            out string error,
            HashSet<string>? relevantProperties = null,
            HashSet<string>? relevantItemKinds = null,
            bool allowDeterministicChoose = false)
        {
            relevantProperties ??= ClosureRelevantProperties;
            relevantItemKinds ??= MigratedProjectItemKinds;
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
                TargetRoot = targetRoot,
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
                    relevantProperties,
                    relevantItemKinds,
                    allowDeterministicChoose,
                    out error))
            {
                return false;
            }

            var targetDocument = graph.Documents[targetProjectRelative];
            if (!IsSupportedMigrationTargetProject(targetDocument))
            {
                error =
                    "The target project does not use the supported Microsoft.NET.Sdk migration shape, so its import graph cannot be proven.";
                return false;
            }

            AddAutomaticDirectoryBuildFile(
                targetRoot,
                targetProject,
                "Directory.Build.props",
                "ImportDirectoryBuildProps",
                "DirectoryBuildPropsPath",
                evaluateImportControl: false,
                graph,
                context,
                relevantProperties,
                relevantItemKinds,
                allowDeterministicChoose);
            AddAutomaticDirectoryBuildFile(
                targetRoot,
                targetProject,
                "Directory.Build.targets",
                "ImportDirectoryBuildTargets",
                "DirectoryBuildTargetsPath",
                evaluateImportControl: true,
                graph,
                context,
                relevantProperties,
                relevantItemKinds,
                allowDeterministicChoose);
            if (graph.IncompleteReasons.Count > 0)
            {
                error =
                    $"The target MSBuild graph cannot be proven complete: {graph.IncompleteReasons[0]}";
                return false;
            }
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
            HashSet<string> relevantProperties,
            HashSet<string> relevantItemKinds,
            bool allowDeterministicChoose,
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
                exception is XmlException
                or IOException
                or UnauthorizedAccessException)
            {
                error = $"MSBuild evidence file '{relativePath}' could not be read: {exception.Message}";
                graph.RejectedDocuments[relativePath] = error;
                if (!required)
                {
                    graph.IncompleteReasons.Add(error);
                }
                return !required;
            }

            if (!IsProjectRoot(document.Root))
            {
                error =
                    $"MSBuild evidence file '{relativePath}' does not have a supported Project root.";
                graph.RejectedDocuments[relativePath] = error;
                if (!required)
                {
                    graph.IncompleteReasons.Add(error);
                }
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
                if (!required
                    && rootCondition == DeterministicCondition.Unknown)
                {
                    graph.IncompleteReasons.Add(error);
                }
                return !required;
            }

            graph.Documents.Add(relativePath, document);
            graph.FullPaths.Add(relativePath, projectFile);
            RecordUnmodeledClosureConstructs(
                document,
                relativePath,
                context,
                graph.IncompleteReasons,
                relevantProperties,
                relevantItemKinds,
                allowDeterministicChoose);
            foreach (var import in document.Descendants().Where(element =>
                IsSupportedEvidenceImport(
                    element,
                    allowDeterministicChoose)
                && IsProjectElement(element, "Import")))
            {
                var importValue = import.Attribute("Project")?.Value.Trim();
                var condition = EvaluateElementCondition(import, context);
                if (condition == DeterministicCondition.False)
                {
                    continue;
                }
                if (condition == DeterministicCondition.Unknown)
                {
                    graph.IncompleteReasons.Add(
                        $"Import '{importValue}' in '{relativePath}' is conditioned and cannot be proven inactive.");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(importValue))
                {
                    graph.IncompleteReasons.Add(
                        $"Active Import in '{relativePath}' does not contain a literal Project path.");
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(
                        import.Attribute("Sdk")?.Value))
                {
                    if (TryResolveLiteralImport(
                        targetRoot,
                        projectFile,
                        importValue,
                        out _,
                        out var sdkImportedRelative))
                    {
                        graph.RejectedDocuments[sdkImportedRelative] =
                            $"Import of '{sdkImportedRelative}' is SDK-qualified and cannot be resolved as contained literal evidence.";
                    }
                    graph.IncompleteReasons.Add(
                        $"Active SDK-qualified import '{importValue}' in '{relativePath}' cannot be resolved deterministically.");
                    continue;
                }
                if (!TryResolveLiteralImport(
                    targetRoot,
                    projectFile,
                    importValue,
                    out var importedProject,
                    out var importedRelative))
                {
                    graph.IncompleteReasons.Add(
                        $"Active import '{importValue}' in '{relativePath}' is not a contained literal import.");
                    continue;
                }
                if (!File.Exists(importedProject))
                {
                    graph.RejectedDocuments[importedRelative] =
                        $"Imported evidence file '{importedRelative}' does not exist.";
                    graph.IncompleteReasons.Add(
                        graph.RejectedDocuments[importedRelative]);
                    continue;
                }

                TryAddParticipatingProjectFile(
                    targetRoot,
                    importedProject,
                    graph,
                    context,
                    required: false,
                    relevantProperties,
                    relevantItemKinds,
                    allowDeterministicChoose,
                    out _);
            }
            return true;
        }

        private static void RecordUnmodeledClosureConstructs(
            XDocument document,
            string relativePath,
            ProjectConditionContext context,
            List<string> incompleteReasons,
            HashSet<string> relevantProperties,
            HashSet<string> relevantItemKinds,
            bool allowDeterministicChoose)
        {
            foreach (var property in document.Descendants().Where(element =>
                IsProjectElement(element, element.Name.LocalName)
                && relevantProperties.Contains(element.Name.LocalName)
                && !IsSupportedEvidenceProperty(
                    element,
                    allowDeterministicChoose)))
            {
                if (EvaluateElementCondition(property, context)
                    != DeterministicCondition.False)
                {
                    incompleteReasons.Add(
                        $"Property '{property.Name.LocalName}' in '{relativePath}' is under an unsupported MSBuild ancestor construct.");
                }
            }

            foreach (var item in document.Descendants().Where(element =>
                IsProjectElement(element, element.Name.LocalName)
                && relevantItemKinds.Contains(element.Name.LocalName)
                && HasClosureRelevantItemOperation(element)
                && !IsSupportedEvidenceItem(
                    element,
                    allowDeterministicChoose)))
            {
                if (EvaluateElementCondition(item, context)
                    != DeterministicCondition.False)
                {
                    incompleteReasons.Add(
                        $"{item.Name.LocalName} item evidence in '{relativePath}' is under an unsupported MSBuild ancestor construct.");
                }
            }

            foreach (var import in document.Descendants().Where(element =>
                IsProjectElement(element, "Import")
                && !IsSupportedEvidenceImport(
                    element,
                    allowDeterministicChoose)))
            {
                if (EvaluateElementCondition(import, context)
                    != DeterministicCondition.False)
                {
                    incompleteReasons.Add(
                        $"Import in '{relativePath}' is under an unsupported MSBuild ancestor construct.");
                }
            }
        }

        private static bool HasClosureRelevantItemOperation(
            XElement item) =>
            item.Attribute("Include") is not null
            || item.Attribute("Update") is not null
            || item.Attribute("Remove") is not null
            || item.Attribute("RemoveMetadata") is not null;

        private static void AddAutomaticDirectoryBuildFile(
            string targetRoot,
            string targetProject,
            string fileName,
            string importEnabledProperty,
            string overridePathProperty,
            bool evaluateImportControl,
            ProjectEvidenceGraph graph,
            ProjectConditionContext context,
            HashSet<string> relevantProperties,
            HashSet<string> relevantItemKinds,
            bool allowDeterministicChoose)
        {
            string? automaticFileOverride = null;
            if (evaluateImportControl)
            {
                if (allowDeterministicChoose)
                {
                    if (!TryEvaluateOrderedDirectoryBuildTargetsImport(
                            graph,
                            context,
                            out var enabled,
                            out var overridePath,
                            out var reason))
                    {
                        graph.IncompleteReasons.Add(reason);
                        return;
                    }
                    if (!enabled)
                    {
                        return;
                    }
                    if (overridePath is not null)
                    {
                        if (!TryResolveLiteralImport(
                                targetRoot,
                                targetProject,
                                overridePath,
                                out automaticFileOverride,
                                out _)
                            || !File.Exists(automaticFileOverride))
                        {
                            graph.IncompleteReasons.Add(
                                $"The active {overridePathProperty} value '{overridePath}' is not an existing contained literal file.");
                            return;
                        }
                    }
                }
                else
                {
                    var importStatus =
                        GetAutomaticDirectoryBuildImportStatus(
                            graph.Documents.Values,
                            importEnabledProperty,
                            overridePathProperty,
                            context,
                            allowDeterministicChoose,
                            out var reason);
                    if (importStatus ==
                        DirectoryBuildImportStatus.Disabled)
                    {
                        return;
                    }
                    if (importStatus ==
                        DirectoryBuildImportStatus.Unmodeled)
                    {
                        graph.IncompleteReasons.Add(reason);
                        return;
                    }
                }
            }

            var automaticFile = automaticFileOverride
                ?? FindNearestAncestorFile(
                    Path.GetDirectoryName(targetProject)!,
                    fileName);
            if (automaticFile is null)
            {
                return;
            }
            var candidateRelative = Path.GetRelativePath(
                targetRoot,
                automaticFile);
            if (!MigrationPathResolver.TryResolveContainedRelativePath(
                    targetRoot,
                    candidateRelative,
                    out automaticFile,
                    out var automaticRelative,
                    out _))
            {
                graph.IncompleteReasons.Add(
                    $"The nearest {fileName} is outside the migration target and may affect the build.");
                return;
            }

            TryAddParticipatingProjectFile(
                targetRoot,
                automaticFile,
                graph,
                context,
                required: false,
                relevantProperties,
                relevantItemKinds,
                allowDeterministicChoose,
                out _);
            if (fileName.Equals(
                    "Directory.Build.props",
                    StringComparison.OrdinalIgnoreCase)
                && graph.Documents.ContainsKey(automaticRelative))
            {
                graph.DirectoryBuildProps = automaticRelative;
            }
            if (fileName.Equals(
                    "Directory.Build.targets",
                    StringComparison.OrdinalIgnoreCase)
                && graph.Documents.ContainsKey(automaticRelative))
            {
                graph.DirectoryBuildTargets = automaticRelative;
            }
        }

        private static DirectoryBuildImportStatus GetAutomaticDirectoryBuildImportStatus(
            IEnumerable<XDocument> participatingDocuments,
            string importEnabledProperty,
            string overridePathProperty,
            ProjectConditionContext context,
            bool allowDeterministicChoose,
            out string reason)
        {
            reason = string.Empty;
            foreach (var property in participatingDocuments
                .SelectMany(document => document.Descendants())
                .Where(element =>
                    IsSupportedEvidenceProperty(
                        element,
                        allowDeterministicChoose)
                    && (IsProjectElement(element, importEnabledProperty)
                        || IsProjectElement(element, overridePathProperty))))
            {
                var condition = EvaluateElementCondition(property, context);
                if (condition == DeterministicCondition.Unknown)
                {
                    reason =
                        $"Automatic Directory.Build import control '{property.Name.LocalName}' is conditioned and cannot be proven active.";
                    return DirectoryBuildImportStatus.Unmodeled;
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
                    return DirectoryBuildImportStatus.Unmodeled;
                }
                if (property.Name.LocalName.Equals(
                        importEnabledProperty,
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (!bool.TryParse(
                            property.Value.Trim(),
                            out var enabled))
                    {
                        reason =
                            $"Automatic Directory.Build import is not proven enabled by {importEnabledProperty}.";
                        return DirectoryBuildImportStatus.Unmodeled;
                    }
                    if (!enabled)
                    {
                        reason =
                            $"Automatic Directory.Build import is disabled by {importEnabledProperty}.";
                        return DirectoryBuildImportStatus.Disabled;
                    }
                }
            }
            return DirectoryBuildImportStatus.Enabled;
        }

        private static string? FindNearestAncestorFile(
            string startDirectory,
            string fileName)
        {
            for (var directory = new DirectoryInfo(startDirectory);
                 directory is not null;
                 directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
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
            List<MigrationProjectItemMetadata> requiredMetadata,
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
            if (requiredMetadata.Count > 0
                && HasPotentialMetadataConflict(
                    graph,
                    itemType,
                    targetPath,
                    requiredMetadata,
                    out var metadataConflictReason))
            {
                return new ProjectItemEvidenceResult(
                    [],
                    metadataConflictReason);
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
            if (!IsSupportedMigrationTargetProject(targetDocument))
            {
                reason =
                    "The target project is not SDK-style, so default PRIResource inclusion cannot be proven.";
                return false;
            }

            var context = new ProjectConditionContext(
                Path.GetFileNameWithoutExtension(graph.TargetProject));
            if (!TryEvaluateEffectiveUseWinUi(
                    graph,
                    context,
                    out var useWinUiEnabled,
                    out var useWinUiReason)
                || !useWinUiEnabled)
            {
                reason = useWinUiReason;
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
                        !bool.TryParse(value, out var enabled) || !enabled))
                {
                    reason =
                        $"{propertyName} prevents deterministic SDK default PRIResource coverage.";
                    return false;
                }
            }

            foreach (var propertyName in new[]
            {
                "DefaultItemExcludes",
                "DefaultExcludesInProjectFolder"
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

        private static bool TryEvaluateEffectiveUseWinUi(
            ProjectEvidenceGraph graph,
            ProjectConditionContext context,
            out bool enabled,
            out string reason)
        {
            bool? effectiveValue = null;
            var evaluated = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            if (graph.DirectoryBuildProps is not null
                && !TryEvaluateUseWinUiDocument(
                    graph,
                    graph.DirectoryBuildProps,
                    context,
                    evaluated,
                    ref effectiveValue,
                    out reason))
            {
                enabled = false;
                return false;
            }
            if (!TryEvaluateUseWinUiDocument(
                    graph,
                    graph.TargetProject,
                    context,
                    evaluated,
                    ref effectiveValue,
                    out reason))
            {
                enabled = false;
                return false;
            }

            enabled = effectiveValue == true;
            reason = enabled
                ? string.Empty
                : "The active props-to-project evaluation does not enable UseWinUI before SDK default PRIResource items are established.";
            return true;
        }

        private static bool TryEvaluateUseWinUiDocument(
            ProjectEvidenceGraph graph,
            string relativeProjectFile,
            ProjectConditionContext context,
            HashSet<string> evaluated,
            ref bool? effectiveValue,
            out string reason)
        {
            reason = string.Empty;
            if (!evaluated.Add(relativeProjectFile))
            {
                return true;
            }

            var document = graph.Documents[relativeProjectFile];
            if (document.Descendants().Any(element =>
                IsProjectElement(element, "UseWinUI")
                && !IsEvaluationProperty(element)
                && EvaluateElementCondition(
                    element,
                    context) != DeterministicCondition.False))
            {
                reason =
                    $"UseWinUI in '{relativeProjectFile}' appears in an unsupported evaluation construct and cannot be ordered deterministically.";
                return false;
            }
            foreach (var child in document.Root!.Elements())
            {
                if (IsProjectElement(child, "PropertyGroup"))
                {
                    foreach (var property in child.Elements().Where(element =>
                        IsProjectElement(element, "UseWinUI")))
                    {
                        var condition = EvaluateElementCondition(
                            property,
                            context);
                        if (condition == DeterministicCondition.False)
                        {
                            continue;
                        }
                        if (condition == DeterministicCondition.Unknown
                            || !bool.TryParse(
                                property.Value.Trim(),
                                out var parsedValue))
                        {
                            reason =
                                $"UseWinUI in '{relativeProjectFile}' is conditioned or property-expanded and cannot be evaluated deterministically.";
                            return false;
                        }
                        effectiveValue = parsedValue;
                    }
                    continue;
                }

                if (IsProjectElement(child, "Import"))
                {
                    if (!TryEvaluateUseWinUiImport(
                        graph,
                        relativeProjectFile,
                        child,
                        context,
                        evaluated,
                        ref effectiveValue,
                        out reason))
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
                        if (!TryEvaluateUseWinUiImport(
                            graph,
                            relativeProjectFile,
                            import,
                            context,
                            evaluated,
                            ref effectiveValue,
                            out reason))
                        {
                            return false;
                        }
                    }
                }
            }
            return true;
        }

        private static bool TryEvaluateUseWinUiImport(
            ProjectEvidenceGraph graph,
            string importingRelativePath,
            XElement import,
            ProjectConditionContext context,
            HashSet<string> evaluated,
            ref bool? effectiveValue,
            out string reason)
        {
            reason = string.Empty;
            var condition = EvaluateElementCondition(import, context);
            if (condition == DeterministicCondition.False)
            {
                return true;
            }
            if (condition == DeterministicCondition.Unknown)
            {
                reason =
                    $"Import in '{importingRelativePath}' is conditioned and UseWinUI evaluation cannot be proven.";
                return false;
            }

            var importValue = import.Attribute("Project")?.Value;
            if (string.IsNullOrWhiteSpace(importValue)
                || !TryResolveLiteralImport(
                    graph.TargetRoot,
                    graph.FullPaths[importingRelativePath],
                    importValue,
                    out _,
                    out var importedRelative)
                || !graph.Documents.ContainsKey(importedRelative))
            {
                reason =
                    $"Import in '{importingRelativePath}' cannot be resolved while evaluating UseWinUI.";
                return false;
            }
            return TryEvaluateUseWinUiDocument(
                graph,
                importedRelative,
                context,
                evaluated,
                ref effectiveValue,
                out reason);
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

        private static bool HasPotentialMetadataConflict(
            ProjectEvidenceGraph graph,
            string itemType,
            string targetPath,
            IReadOnlyCollection<MigrationProjectItemMetadata> requiredMetadata,
            out string reason)
        {
            reason = string.Empty;
            var context = new ProjectConditionContext(
                Path.GetFileNameWithoutExtension(graph.TargetProject));
            foreach (var (relativePath, document) in graph.Documents)
            {
                foreach (var item in document.Descendants().Where(element =>
                    IsEvaluationItem(element)
                    && IsProjectElement(element, itemType)))
                {
                    var include = item.Attribute("Include")?.Value;
                    var update = item.Attribute("Update")?.Value;
                    var literalTargetMatches =
                        ItemAttributeMatches(include, targetPath)
                        || ItemAttributeMatches(update, targetPath);
                    var nonLiteralUpdate = IsNonLiteralItemSpec(update);
                    if (!literalTargetMatches && !nonLiteralUpdate)
                    {
                        continue;
                    }
                    var itemCondition = EvaluateElementCondition(item, context);
                    if (itemCondition == DeterministicCondition.False)
                    {
                        continue;
                    }
                    foreach (var metadata in requiredMetadata)
                    {
                        if (RemovesMetadata(
                                item.Attribute("RemoveMetadata")?.Value,
                                metadata.Name))
                        {
                            reason = nonLiteralUpdate
                                ? $"An active nonliteral {itemType} Update in '{relativePath}' may remove required metadata '{metadata.Name}'."
                                : $"Active {itemType} evidence in '{relativePath}' removes required metadata '{metadata.Name}'.";
                            return true;
                        }
                        var metadataAttribute = item.Attributes().FirstOrDefault(
                            attribute =>
                                attribute.Name.Namespace == XNamespace.None
                                && attribute.Name.LocalName.Equals(
                                    metadata.Name,
                                    StringComparison.OrdinalIgnoreCase));
                        if (metadataAttribute is not null
                            && (itemCondition == DeterministicCondition.Unknown
                                || !string.Equals(
                                    metadataAttribute.Value.Trim(),
                                    metadata.Value.Trim(),
                                    StringComparison.Ordinal)))
                        {
                            reason = nonLiteralUpdate
                                ? $"An active nonliteral {itemType} Update in '{relativePath}' may conflict with required metadata '{metadata.Name}'."
                                : $"Active {itemType} evidence in '{relativePath}' conflicts with required metadata '{metadata.Name}'.";
                            return true;
                        }
                        foreach (var targetMetadata in item.Elements().Where(element =>
                            IsProjectElement(element, metadata.Name)))
                        {
                            var metadataCondition = EvaluateElementCondition(
                                targetMetadata,
                                context);
                            if (itemCondition == DeterministicCondition.Unknown
                                || metadataCondition == DeterministicCondition.Unknown)
                            {
                                reason =
                                    $"A conditioned {itemType} update in '{relativePath}' may change required metadata '{metadata.Name}'.";
                                return true;
                            }
                            if (metadataCondition == DeterministicCondition.True
                                && !string.Equals(
                                    targetMetadata.Value.Trim(),
                                    metadata.Value.Trim(),
                                    StringComparison.Ordinal))
                            {
                                reason = nonLiteralUpdate
                                    ? $"An active nonliteral {itemType} Update in '{relativePath}' may conflict with required metadata '{metadata.Name}'."
                                    : $"Active {itemType} evidence in '{relativePath}' conflicts with required metadata '{metadata.Name}'.";
                                return true;
                            }
                        }
                    }
                }
            }
            return false;
        }

        private static bool IsNonLiteralItemSpec(string? value) =>
            !string.IsNullOrWhiteSpace(value)
            && (value.Contains("$(", StringComparison.Ordinal)
                || value.Contains("@(", StringComparison.Ordinal)
                || value.IndexOfAny(['*', '?']) >= 0);

        private static bool RemovesMetadata(
            string? value,
            string metadataName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            if (value.Contains("$(", StringComparison.Ordinal)
                || value.Contains("@(", StringComparison.Ordinal))
            {
                return true;
            }
            return value.Split(
                    ';',
                    StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries)
                .Contains(metadataName, StringComparer.OrdinalIgnoreCase);
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
            requiredMetadata.All(metadata =>
                targetItem.Elements().Any(element =>
                    IsProjectElement(element, metadata.Name)
                    && EvaluateElementCondition(element, context)
                        == DeterministicCondition.True
                    && string.Equals(
                        element.Value.Trim(),
                        metadata.Value.Trim(),
                        StringComparison.Ordinal))
                || targetItem.Attributes().Any(attribute =>
                    attribute.Name.Namespace == XNamespace.None
                    && attribute.Name.LocalName.Equals(
                        metadata.Name,
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        attribute.Value.Trim(),
                        metadata.Value.Trim(),
                        StringComparison.Ordinal)));

        private static DeterministicCondition EvaluateElementCondition(
            XElement element,
            ProjectConditionContext context)
        {
            var result = DeterministicCondition.True;
            foreach (var current in element.AncestorsAndSelf().Reverse())
            {
                var evaluation =
                    IsProjectElement(current, "When")
                    || IsProjectElement(current, "Otherwise")
                        ? EvaluateChooseBranch(current, context)
                        : EvaluateCondition(
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

        private static DeterministicCondition EvaluateChooseBranch(
            XElement branch,
            ProjectConditionContext context)
        {
            var choose = branch.Parent;
            if (choose is null
                || !IsProjectElement(choose, "Choose"))
            {
                return DeterministicCondition.Unknown;
            }

            var priorTrue = false;
            var priorUnknown = false;
            foreach (var candidate in choose.Elements().Where(element =>
                IsProjectElement(element, "When")
                || IsProjectElement(element, "Otherwise")))
            {
                if (IsProjectElement(candidate, "When"))
                {
                    var condition = EvaluateCondition(
                        candidate.Attribute("Condition")?.Value,
                        context);
                    if (candidate == branch)
                    {
                        if (priorTrue
                            || condition == DeterministicCondition.False)
                        {
                            return DeterministicCondition.False;
                        }
                        if (priorUnknown)
                        {
                            return DeterministicCondition.Unknown;
                        }
                        return condition;
                    }
                    if (!priorTrue)
                    {
                        priorTrue =
                            condition == DeterministicCondition.True;
                        priorUnknown |=
                            condition == DeterministicCondition.Unknown;
                    }
                    continue;
                }

                if (candidate == branch)
                {
                    if (priorTrue)
                    {
                        return DeterministicCondition.False;
                    }
                    return priorUnknown
                        ? DeterministicCondition.Unknown
                        : DeterministicCondition.True;
                }
                if (!priorTrue && !priorUnknown)
                {
                    priorTrue = true;
                }
                else if (priorUnknown)
                {
                    priorUnknown = true;
                }
            }
            return DeterministicCondition.Unknown;
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

        private static bool IsSupportedMigrationTargetProject(
            XDocument document)
        {
            if (!IsProjectRoot(document.Root))
            {
                return false;
            }
            return string.Equals(
                    document.Root!.Attribute("Sdk")?.Value.Trim(),
                    "Microsoft.NET.Sdk",
                    StringComparison.OrdinalIgnoreCase)
                && !document.Root.Elements().Any(element =>
                    IsProjectElement(element, "Sdk"));
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

        private static bool IsSupportedEvidenceImport(
            XElement element,
            bool allowDeterministicChoose) =>
            IsEvaluationImport(element)
            || (allowDeterministicChoose
                && IsDirectChooseBranchChild(element, "Import"))
            || (allowDeterministicChoose
                && element.Parent is { } importGroup
                && IsProjectElement(importGroup, "ImportGroup")
                && IsDirectChooseBranchChild(
                    importGroup,
                    "ImportGroup"));

        private static bool IsSupportedEvidenceItem(
            XElement element,
            bool allowDeterministicChoose) =>
            IsEvaluationItem(element)
            || (allowDeterministicChoose
                && element.Parent is { } itemGroup
                && IsProjectElement(itemGroup, "ItemGroup")
                && IsDirectChooseBranchChild(
                    itemGroup,
                    "ItemGroup"));

        private static bool IsSupportedEvidenceProperty(
            XElement element,
            bool allowDeterministicChoose) =>
            IsEvaluationProperty(element)
            || (allowDeterministicChoose
                && element.Parent is { } propertyGroup
                && IsProjectElement(
                    propertyGroup,
                    "PropertyGroup")
                && IsDirectChooseBranchChild(
                    propertyGroup,
                    "PropertyGroup"));

        private static bool IsDirectChooseBranchChild(
            XElement element,
            string localName) =>
            IsProjectElement(element, localName)
            && element.Parent is { } branch
            && (IsProjectElement(branch, "When")
                || IsProjectElement(branch, "Otherwise"))
            && branch.Parent is { } choose
            && IsProjectElement(choose, "Choose")
            && choose.Parent == element.Document?.Root;

        [GeneratedRegex(
            @"^\s*'\$\(MSBuildProjectName\)'\s*(?<operator>==|!=)\s*'(?<value>[^']*)'\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex ProjectNameCondition();
    }
}
