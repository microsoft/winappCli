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
        private static readonly HashSet<string> PortableTargetFileItemKinds =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "ApplicationDefinition",
                "Compile",
                "Content",
                "EmbeddedResource",
                "None",
                "Page",
                "PRIResource"
            };

        private static bool TryResolveSourceItemPath(
            string sourceRoot,
            MigrationReviewRequiredProjectItem sourceItem,
            out string fullPath,
            out string error)
        {
            fullPath = string.Empty;
            error = string.Empty;
            if (!MigrationPathResolver.TryResolveContainedRelativePath(
                    sourceRoot,
                    sourceItem.SourceProject,
                    out var sourceProject,
                    out _,
                    out error))
            {
                error = $"Source project path is invalid: {error}";
                return false;
            }

            XDocument sourceDocument;
            try
            {
                sourceDocument = XDocument.Load(
                    sourceProject,
                    LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            }
            catch (Exception exception) when (
                exception is XmlException
                or IOException
                or UnauthorizedAccessException)
            {
                error = $"Source project could not be read: {exception.Message}";
                return false;
            }

            if (!TryExpandProjectPathValue(
                    sourceProject,
                    sourceDocument,
                    sourceItem.Include,
                    out var expandedInclude,
                    out _,
                    out error))
            {
                error =
                    $"Source item expression '{sourceItem.Include}' cannot be resolved deterministically: {error}";
                return false;
            }
            if (expandedInclude.Contains(';')
                || expandedInclude.Contains("@(", StringComparison.Ordinal)
                || expandedInclude.IndexOfAny(['*', '?']) >= 0)
            {
                error =
                    $"Source item expression '{sourceItem.Include}' does not resolve to one literal file.";
                return false;
            }

            try
            {
                fullPath = Path.GetFullPath(Path.IsPathRooted(expandedInclude)
                    ? expandedInclude
                    : Path.Combine(
                        Path.GetDirectoryName(sourceProject)!,
                        expandedInclude));
            }
            catch (Exception exception) when (
                exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
            {
                fullPath = string.Empty;
                error = $"Source content path is invalid: {exception.Message}";
                return false;
            }
            if (!File.Exists(fullPath))
            {
                error = $"Source content is unavailable: {sourceItem.Include}.";
                return false;
            }
            return true;
        }

        private static void AddTargetPortabilityIssues(
            string targetRoot,
            string targetProject,
            MigrationTargetProjectGraphVerification analysis)
        {
            XDocument targetDocument;
            try
            {
                targetDocument = XDocument.Load(
                    targetProject,
                    LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            }
            catch (Exception exception) when (
                exception is XmlException
                or IOException
                or UnauthorizedAccessException)
            {
                analysis.Issues.Add(new MigrationTargetProjectGraphIssue
                {
                    Kind = "target-project-portability-incomplete",
                    Severity = "review-required",
                    EntryProject = NormalizePath(
                        Path.GetRelativePath(targetRoot, targetProject)),
                    Reason =
                        $"The target project could not be inspected for external file dependencies: {exception.Message}",
                    RequiredResolution =
                        "Make the contained target project inspectable before portability verification."
                });
                analysis.Status = "incomplete";
                return;
            }

            var externalItems = new List<(string Kind, string Spec)>();
            var context = new ProjectConditionContext(
                Path.GetFileNameWithoutExtension(targetProject));
            foreach (var item in targetDocument.Descendants().Where(element =>
                PortableTargetFileItemKinds.Contains(element.Name.LocalName)
                && element.Attribute("Include") is not null
                && EvaluateElementCondition(element, context)
                    == DeterministicCondition.True))
            {
                foreach (var rawSpec in item.Attribute("Include")!.Value.Split(
                    ';',
                    StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries))
                {
                    if (rawSpec.Contains("@(", StringComparison.Ordinal)
                        || rawSpec.IndexOfAny(['*', '?']) >= 0
                        || !TryExpandProjectPathValue(
                            targetProject,
                            targetDocument,
                            rawSpec,
                            out var expandedSpec,
                            out var hasRootedLiteral,
                            out _))
                    {
                        continue;
                    }

                    string fullPath;
                    try
                    {
                        fullPath = Path.GetFullPath(Path.IsPathRooted(expandedSpec)
                            ? expandedSpec
                            : Path.Combine(
                                Path.GetDirectoryName(targetProject)!,
                                expandedSpec));
                    }
                    catch (Exception exception) when (
                        exception is ArgumentException
                        or NotSupportedException
                        or PathTooLongException)
                    {
                        continue;
                    }

                    if (hasRootedLiteral
                        || !IsPathContainedByRoot(targetRoot, fullPath))
                    {
                        externalItems.Add((item.Name.LocalName, rawSpec));
                    }
                }
            }

            if (externalItems.Count == 0)
            {
                return;
            }

            analysis.Issues.Add(new MigrationTargetProjectGraphIssue
            {
                Kind = "target-external-file-items",
                EntryProject = NormalizePath(
                    Path.GetRelativePath(targetRoot, targetProject)),
                ItemKinds = externalItems
                    .Select(item => item.Kind)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                SamplePaths = externalItems
                    .Select(item => item.Spec)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(20)
                    .ToList(),
                Reason =
                    $"{externalItems.Count} active target file item(s) use a machine-specific rooted path or resolve outside the migration target.",
                RequiredResolution =
                    "Copy required files under the migration target and reference them with target-relative paths. Do not depend on the source checkout or machine-specific paths."
            });
            analysis.Status = "failed";
        }

        private static bool TryExpandProjectPathValue(
            string projectFile,
            XDocument projectDocument,
            string rawValue,
            out string expanded,
            out bool hasRootedLiteral,
            out string error)
        {
            var projectDirectory = Path.GetDirectoryName(projectFile)!;
            var values = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["MSBuildProjectDirectory"] = projectDirectory,
                ["MSBuildProjectFile"] = Path.GetFileName(projectFile),
                ["MSBuildProjectFullPath"] = projectFile,
                ["MSBuildProjectName"] = Path.GetFileNameWithoutExtension(projectFile),
                ["MSBuildProjectExtension"] = Path.GetExtension(projectFile),
                ["MSBuildThisFileDirectory"] =
                    Path.TrimEndingDirectorySeparator(projectDirectory)
                    + Path.DirectorySeparatorChar
            };
            var rootedProperties = values.Keys.ToDictionary(
                name => name,
                _ => false,
                StringComparer.OrdinalIgnoreCase);
            var context = new ProjectConditionContext(
                Path.GetFileNameWithoutExtension(projectFile));

            foreach (var propertyGroup in projectDocument.Root?.Elements().Where(
                element => IsProjectElement(element, "PropertyGroup"))
                ?? [])
            {
                if (EvaluateElementCondition(propertyGroup, context)
                    != DeterministicCondition.True)
                {
                    continue;
                }
                foreach (var property in propertyGroup.Elements())
                {
                    if (EvaluateElementCondition(property, context)
                        != DeterministicCondition.True)
                    {
                        continue;
                    }
                    if (TryExpandSupportedProjectValue(
                        property.Value,
                        values,
                        rootedProperties,
                        out var propertyValue,
                        out var rootedLiteral,
                        out _))
                    {
                        values[property.Name.LocalName] = propertyValue;
                        rootedProperties[property.Name.LocalName] = rootedLiteral;
                    }
                }
            }

            return TryExpandSupportedProjectValue(
                rawValue,
                values,
                rootedProperties,
                out expanded,
                out hasRootedLiteral,
                out error);
        }

        private static bool TryExpandSupportedProjectValue(
            string rawValue,
            Dictionary<string, string> values,
            Dictionary<string, bool> rootedProperties,
            out string expanded,
            out bool hasRootedLiteral,
            out string error)
        {
            expanded = rawValue.Trim();
            hasRootedLiteral = Path.IsPathRooted(expanded);
            error = string.Empty;

            for (var iteration = 0; iteration < 64; iteration++)
            {
                var propertyMatch = SimpleMsBuildPropertyRegex().Match(expanded);
                if (propertyMatch.Success)
                {
                    var propertyName = propertyMatch.Groups["name"].Value;
                    if (!values.TryGetValue(propertyName, out var propertyValue))
                    {
                        error =
                            $"MSBuild property '$({propertyName})' is not available in the supported evaluation model.";
                        return false;
                    }
                    hasRootedLiteral |= rootedProperties.TryGetValue(
                        propertyName,
                        out var propertyIsRooted)
                        && propertyIsRooted;
                    expanded = string.Concat(
                        expanded.AsSpan(0, propertyMatch.Index),
                        propertyValue,
                        expanded.AsSpan(
                            propertyMatch.Index + propertyMatch.Length));
                    continue;
                }

                var functionMatch =
                    GetDirectoryNameOfFileAboveRegex().Match(expanded);
                if (functionMatch.Success)
                {
                    var start = TrimMsBuildArgument(
                        functionMatch.Groups["start"].Value);
                    var marker = TrimMsBuildArgument(
                        functionMatch.Groups["marker"].Value);
                    if (string.IsNullOrWhiteSpace(start)
                        || string.IsNullOrWhiteSpace(marker)
                        || !string.Equals(
                            marker,
                            Path.GetFileName(marker),
                            StringComparison.Ordinal))
                    {
                        error =
                            "GetDirectoryNameOfFileAbove requires a directory and one literal marker filename.";
                        return false;
                    }
                    var containingDirectory = FindDirectoryContainingFile(
                        start,
                        marker);
                    if (containingDirectory is null)
                    {
                        error =
                            $"GetDirectoryNameOfFileAbove could not find '{marker}' from '{start}'.";
                        return false;
                    }
                    expanded = string.Concat(
                        expanded.AsSpan(0, functionMatch.Index),
                        containingDirectory,
                        expanded.AsSpan(
                            functionMatch.Index + functionMatch.Length));
                    continue;
                }
                break;
            }

            if (expanded.Contains("$(", StringComparison.Ordinal)
                || expanded.Contains("@(", StringComparison.Ordinal))
            {
                error = "The value contains an unsupported MSBuild expression.";
                return false;
            }
            return true;
        }

        private static string TrimMsBuildArgument(string value) =>
            value.Trim().Trim('\'', '"');

        private static string? FindDirectoryContainingFile(
            string start,
            string marker)
        {
            DirectoryInfo? directory;
            try
            {
                directory = new DirectoryInfo(Path.GetFullPath(start));
            }
            catch (Exception exception) when (
                exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
            {
                return null;
            }
            for (; directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, marker)))
                {
                    return directory.FullName;
                }
            }
            return null;
        }

        [GeneratedRegex(
            @"\$\((?<name>[A-Za-z_][A-Za-z0-9_.-]*)\)",
            RegexOptions.CultureInvariant)]
        private static partial Regex SimpleMsBuildPropertyRegex();

        [GeneratedRegex(
            @"\$\(\[MSBuild\]::GetDirectoryNameOfFileAbove\((?<start>[^,]+),(?<marker>[^)]+)\)\)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex GetDirectoryNameOfFileAboveRegex();
    }
}
