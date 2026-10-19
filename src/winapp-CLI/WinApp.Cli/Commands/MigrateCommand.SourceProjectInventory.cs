// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Xml.Linq;

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
                "DisableDefaultItemsInProjectFolder",
                "EnableDefaultPRIResourceItems",
                "EnableDefaultPriItems",
                "DefaultItemExcludes",
                "DefaultItemExcludesInProjectFolder",
                "DefaultExcludesInProjectFolder",
                "ImportDirectoryBuildProps",
                "ImportDirectoryBuildTargets",
                "DirectoryBuildPropsPath",
                "DirectoryBuildTargetsPath"
            };

        private static readonly HashSet<string>
            KnownUwpInfrastructureImports =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "$(MSBuildExtensionsPath)/$(MSBuildToolsVersion)/Microsoft.Common.props",
                "$(MSBuildExtensionsPath)/Microsoft/WindowsXaml/v$(VisualStudioVersion)/Microsoft.Windows.UI.Xaml.CSharp.targets"
            };

        private static bool HasInventoriedItemOperation(
            XElement item) =>
            item.Attribute("Include") is not null
            || item.Attribute("Update") is not null
            || item.Attribute("Remove") is not null
            || item.Attribute("RemoveMetadata") is not null;

        internal static bool IsSupportedMigrationTargetProject(
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

        private static bool IsDirectProjectItemDeclaration(
            XElement element) =>
            element.Parent is { } itemGroup
            && IsProjectElement(itemGroup, "ItemGroup")
            && itemGroup.Parent == element.Document?.Root;

        private static bool HasRawCondition(
            XElement element) =>
            element.AncestorsAndSelf().Any(current =>
                current.Attribute("Condition") is not null);

        private static bool HasChooseAncestry(
            XElement element) =>
            element.AncestorsAndSelf().Any(current =>
                IsProjectElement(current, "Choose")
                || IsProjectElement(current, "When")
                || IsProjectElement(current, "Otherwise"));

        private static bool HasRawProjectExpression(
            XElement element) =>
            element.DescendantsAndSelf().Any(current =>
                current.Attributes().Any(attribute =>
                    ContainsRawProjectExpression(attribute.Value))
                || ContainsRawProjectExpression(current.Value));

        private static bool IsKnownUwpInfrastructureImport(
            XElement element)
        {
            var project = element.Attribute("Project")?.Value.Trim();
            return !string.IsNullOrWhiteSpace(project)
                && KnownUwpInfrastructureImports.Contains(
                    project.Replace('\\', '/'));
        }

        private static bool ContainsRawProjectExpression(
            string value) =>
            value.Contains("$(", StringComparison.Ordinal)
            || value.Contains("@(", StringComparison.Ordinal)
            || value.Contains("%(", StringComparison.Ordinal);
    }
}
