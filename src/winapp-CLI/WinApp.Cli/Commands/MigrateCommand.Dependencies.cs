// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Xml;
using System.Xml.Linq;
using WinApp.Cli.Models;

namespace WinApp.Cli.Commands;

internal partial class MigrateCommand
{
    public partial class Handler
    {
        private static MigrationDependencyAnalysis AnalyzeSourceDependencies(
            string sourceRoot,
            string? sourceProject)
        {
            var analysis = new MigrationDependencyAnalysis();
            if (sourceProject is null)
            {
                analysis.Status = "not-available";
                return analysis;
            }

            var pending = new Queue<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            pending.Enqueue(Path.GetFullPath(sourceProject));

            while (pending.TryDequeue(out var projectPath))
            {
                if (!visited.Add(projectPath) || !File.Exists(projectPath))
                {
                    continue;
                }

                XDocument document;
                try
                {
                    document = XDocument.Load(
                        projectPath,
                        LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
                }
                catch (Exception exception) when (
                    exception is IOException
                    or UnauthorizedAccessException
                    or XmlException)
                {
                    analysis.Issues.Add(new MigrationDependencyIssue
                    {
                        Kind = "project-inspection-failed",
                        SourceProject = NormalizePath(
                            Path.GetRelativePath(sourceRoot, projectPath)),
                        Reason = exception.Message
                    });
                    continue;
                }

                var relativeProject = NormalizePath(Path.GetRelativePath(sourceRoot, projectPath));
                analysis.Projects.Add(new MigrationDependencyProject
                {
                    Path = relativeProject,
                    TargetFrameworks = ReadTargetFrameworks(document),
                    TargetPlatformIdentifier = ReadProjectProperty(
                        document,
                        "TargetPlatformIdentifier"),
                    TargetPlatformVersion = ReadProjectProperty(document, "TargetPlatformVersion"),
                    TargetPlatformMinVersion = ReadProjectProperty(
                        document,
                        "TargetPlatformMinVersion")
                });

                foreach (var package in document.Descendants().Where(element =>
                    element.Name.LocalName == "PackageReference"
                    && element.Attribute("Include") is not null))
                {
                    var include = package.Attribute("Include")!.Value.Trim();
                    var version = ReadItemVersion(package);
                    var condition = ReadItemCondition(package);
                    analysis.PackageReferences.Add(new MigrationPackageReference
                    {
                        Id = include,
                        Version = version,
                        SourceProject = relativeProject,
                        Line = LineNumber(package),
                        Condition = condition,
                        ResolutionStatus = ContainsMsBuildExpression(include, version)
                                ? "unresolved-properties"
                                : condition is not null
                                    ? "unresolved-condition"
                                    : include.IndexOfAny(['*', '?']) >= 0
                                        ? "unresolved-wildcard"
                                : "declared"
                    });
                }

                foreach (var reference in document.Descendants().Where(element =>
                    element.Name.LocalName == "ProjectReference"
                    && element.Attribute("Include") is not null))
                {
                    var include = reference.Attribute("Include")!.Value.Trim();
                    var resolvedPath = ResolveProjectReference(projectPath, include);
                    var relativeResolvedPath = resolvedPath is null
                        ? null
                        : NormalizePath(Path.GetRelativePath(sourceRoot, resolvedPath));
                    var condition = ReadItemCondition(reference);
                    analysis.ProjectReferences.Add(new MigrationProjectReference
                    {
                        Include = include,
                        ResolvedPath = relativeResolvedPath,
                        SourceProject = relativeProject,
                        Line = LineNumber(reference),
                        Condition = condition,
                        ResolutionStatus = resolvedPath is null
                            ? "unresolved"
                            : condition is not null
                                ? "unresolved-condition"
                            : File.Exists(resolvedPath)
                                ? "resolved"
                                : "missing",
                        OutsideSourceRoot = relativeResolvedPath is not null
                            && IsOutsideRoot(relativeResolvedPath)
                    });

                    if (resolvedPath is not null && File.Exists(resolvedPath))
                    {
                        pending.Enqueue(resolvedPath);
                    }
                }
            }

            analysis.Projects = analysis.Projects
                .OrderBy(project => project.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            analysis.PackageReferences = analysis.PackageReferences
                .OrderBy(package => package.SourceProject, StringComparer.OrdinalIgnoreCase)
                .ThenBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            analysis.ProjectReferences = analysis.ProjectReferences
                .OrderBy(reference => reference.SourceProject, StringComparer.OrdinalIgnoreCase)
                .ThenBy(reference => reference.Include, StringComparer.OrdinalIgnoreCase)
                .ToList();
            analysis.Issues = analysis.Issues
                .OrderBy(issue => issue.SourceProject, StringComparer.OrdinalIgnoreCase)
                .ToList();
            analysis.Status = analysis.Issues.Count > 0
                ? "incomplete"
                : analysis.PackageReferences.Count == 0
                && analysis.ProjectReferences.Count == 0
                    ? "no-dependencies"
                    : "review-required";
            return analysis;
        }

        private static List<string> ReadTargetFrameworks(XDocument document)
        {
            return document.Descendants()
                .Where(element => element.Name.LocalName is "TargetFramework" or "TargetFrameworks")
                .SelectMany(element => element.Value.Split(
                    ';',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string? ReadItemVersion(XElement item)
        {
            return item.Attribute("Version")?.Value.Trim()
                ?? item.Elements().FirstOrDefault(element =>
                    element.Name.LocalName == "Version")?.Value.Trim();
        }

        private static string? ReadProjectProperty(XDocument document, string propertyName)
        {
            return document.Descendants().FirstOrDefault(element =>
                element.Name.LocalName == propertyName)?.Value.Trim();
        }

        private static int? LineNumber(XElement element)
        {
            return element is IXmlLineInfo lineInfo && lineInfo.HasLineInfo()
                ? lineInfo.LineNumber
                : null;
        }

        private static string? ReadItemCondition(XElement item)
        {
            return item.Attribute("Condition")?.Value.Trim()
                ?? item.Parent?.Attribute("Condition")?.Value.Trim();
        }

        private static bool ContainsMsBuildExpression(params string?[] values)
        {
            return values.Any(value =>
                value?.Contains("$(", StringComparison.Ordinal) == true
                || value?.Contains("@(", StringComparison.Ordinal) == true);
        }

        private static string? ResolveProjectReference(string projectPath, string include)
        {
            if (string.IsNullOrWhiteSpace(include)
                || include.IndexOfAny(['*', '?']) >= 0
                || include.Contains("$(", StringComparison.Ordinal)
                || include.Contains("@(", StringComparison.Ordinal))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(projectPath)!,
                    include));
            }
            catch (Exception exception) when (
                exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
            {
                return null;
            }
        }

        private static bool IsOutsideRoot(string relativePath)
        {
            return Path.IsPathRooted(relativePath)
                || relativePath == ".."
                || relativePath.StartsWith("../", StringComparison.Ordinal);
        }
    }
}
