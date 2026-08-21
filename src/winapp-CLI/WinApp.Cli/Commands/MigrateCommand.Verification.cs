// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using System.Xml;
using System.Xml.Linq;
using WinApp.Cli.Models;

namespace WinApp.Cli.Commands;

internal partial class MigrateCommand
{
    public partial class Handler
    {
        private const long UnknownTextInspectionLimit = 4 * 1024 * 1024;

        private static readonly HashSet<string> KnownTextExtensions =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".c", ".config", ".cpp", ".cs", ".csproj", ".fs", ".h", ".hpp", ".idl",
                ".json", ".md", ".props", ".projitems", ".razor", ".resw", ".resx", ".targets",
                ".tt", ".txt", ".vb", ".vbproj", ".vcxproj", ".xaml", ".xml"
            };

        private static readonly HashSet<string> MigratedProjectItemKinds =
            new(StringComparer.Ordinal)
            {
                "Content",
                "PRIResource"
            };

        internal sealed record ProjectItemMigrationResult(
            int SourceItems,
            int MigratedItems,
            List<MigrationProjectItem> AccountedItems,
            List<MigrationLocation> UnresolvedItems,
            List<MigrationLocation> MissingTargetItems,
            int ChangedFiles);

        private static int RewriteReswNamespaces(string targetRoot)
        {
            var changedFiles = 0;
            var files = EnumerateFiles(targetRoot)
                .Where(path => path.EndsWith(".resw", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var file in files)
            {
                EncodedTextFile sourceFile;
                XDocument document;
                try
                {
                    sourceFile = ReadTextFile(file);
                    document = XDocument.Parse(sourceFile.Content, LoadOptions.PreserveWhitespace);
                }
                catch (Exception exception) when (
                    exception is DecoderFallbackException or XmlException)
                {
                    continue;
                }

                var changed = false;
                foreach (var dataElement in document.Descendants().Where(element =>
                    element.Name.LocalName == "data"))
                {
                    var name = dataElement.Attribute("name");
                    if (name is null
                        || !name.Value.Contains("Windows.UI.Xaml", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    name.Value = name.Value.Replace(
                        "Windows.UI.Xaml",
                        "Microsoft.UI.Xaml",
                        StringComparison.Ordinal);
                    changed = true;
                }

                if (!changed)
                {
                    continue;
                }

                WriteTextFile(
                    file,
                    document.ToString(SaveOptions.DisableFormatting),
                    sourceFile.Encoding);
                changedFiles++;
            }

            Console.Out.WriteLine(
                $"    Rewrote Windows.UI.Xaml resource keys in {changedFiles} of {files.Count} .resw files");
            return changedFiles;
        }

        internal static ProjectItemMigrationResult MigrateSourceProjectItems(
            string sourceRoot,
            string? sourceProject,
            string targetRoot,
            string targetProject,
            bool applyChanges)
        {
            if (sourceProject is null)
            {
                return new ProjectItemMigrationResult(0, 0, [], [], [], 0);
            }

            XDocument sourceDocument;
            XDocument targetDocument;
            try
            {
                sourceDocument = XDocument.Load(
                    sourceProject,
                    LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
                targetDocument = XDocument.Load(targetProject, LoadOptions.PreserveWhitespace);
            }
            catch (XmlException)
            {
                return new ProjectItemMigrationResult(
                    0,
                    0,
                    [],
                    [new MigrationLocation
                    {
                        Path = NormalizePath(Path.Combine(".uwp-source", Path.GetFileName(sourceProject) + ".reference"))
                    }],
                    [],
                    0);
            }

            var sourceItems = sourceDocument
                .Descendants()
                .Where(element => MigratedProjectItemKinds.Contains(element.Name.LocalName))
                .Where(element => element.Attribute("Include") is not null)
                .ToList();
            var unresolved = new List<MigrationLocation>();
            var missingTargetItems = new List<MigrationLocation>();
            var migratable = new List<(
                string Kind,
                string RelativePath,
                XElement SourceElement,
                bool RequiresProjectEntry)>();

            foreach (var item in sourceItems)
            {
                var include = item.Attribute("Include")!.Value.Trim();
                if (item.Attribute("Condition") is not null
                    || item.Parent?.Attribute("Condition") is not null
                    || include.IndexOfAny(['*', '?']) >= 0
                    || include.Contains("$(", StringComparison.Ordinal)
                    || include.Contains("@(", StringComparison.Ordinal)
                    || Path.IsPathRooted(include))
                {
                    unresolved.Add(ProjectItemLocation(sourceRoot, sourceProject, item));
                    continue;
                }

                string sourcePath;
                try
                {
                    sourcePath = Path.GetFullPath(Path.Combine(sourceRoot, include));
                }
                catch (Exception exception) when (
                    exception is ArgumentException
                    or NotSupportedException
                    or PathTooLongException)
                {
                    unresolved.Add(ProjectItemLocation(sourceRoot, sourceProject, item));
                    continue;
                }
                var relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
                if (relativePath == ".."
                    || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || !File.Exists(sourcePath))
                {
                    unresolved.Add(ProjectItemLocation(sourceRoot, sourceProject, item));
                    continue;
                }

                var targetPath = Path.Combine(targetRoot, relativePath);
                if (!File.Exists(targetPath))
                {
                    missingTargetItems.Add(new MigrationLocation
                    {
                        Path = NormalizePath(relativePath)
                    });
                    continue;
                }

                var usesDefaultPriItem =
                    item.Name.LocalName == "PRIResource"
                    && relativePath.EndsWith(".resw", StringComparison.OrdinalIgnoreCase);
                if (usesDefaultPriItem && item.Elements().Any())
                {
                    unresolved.Add(ProjectItemLocation(sourceRoot, sourceProject, item));
                    continue;
                }

                migratable.Add((
                    item.Name.LocalName,
                    relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar),
                    item,
                    !usesDefaultPriItem));
            }

            migratable = migratable
                .DistinctBy(item => (item.Kind, item.RelativePath), StringTupleComparer.OrdinalIgnoreCase)
                .ToList();
            var accountedItems = migratable.Select(item => new MigrationProjectItem
            {
                Kind = item.Kind,
                Path = NormalizePath(item.RelativePath),
                RequiresProjectEntry = item.RequiresProjectEntry
            }).ToList();
            var explicitItems = migratable
                .Where(item => item.RequiresProjectEntry)
                .ToList();
            if (!applyChanges)
            {
                var targetEntries = targetDocument
                    .Descendants()
                    .Where(element =>
                        element.Attribute("Include") is not null
                        && MigratedProjectItemKinds.Contains(element.Name.LocalName))
                    .SelectMany(element => element.Attribute("Include")!.Value
                        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(path => (
                            Kind: element.Name.LocalName,
                            Path: path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar))))
                    .ToHashSet(StringTupleComparer.OrdinalIgnoreCase);
                foreach (var item in explicitItems)
                {
                    if (!targetEntries.Contains((item.Kind, item.RelativePath)))
                    {
                        missingTargetItems.Add(new MigrationLocation
                        {
                            Path = NormalizePath(item.RelativePath)
                        });
                    }
                }

                return new ProjectItemMigrationResult(
                    sourceItems.Count,
                    migratable.Count - missingTargetItems.Count,
                    accountedItems,
                    unresolved,
                    missingTargetItems,
                    0);
            }

            if (explicitItems.Count == 0)
            {
                return new ProjectItemMigrationResult(
                    sourceItems.Count,
                    migratable.Count,
                    accountedItems,
                    unresolved,
                    missingTargetItems,
                    0);
            }

            var projectNamespace = targetDocument.Root!.Name.Namespace;
            var itemGroup = new XElement(
                projectNamespace + "ItemGroup",
                new XAttribute("Label", "winapp-migrate:source-items"));

            foreach (var group in explicitItems.GroupBy(item => item.Kind, StringComparer.Ordinal))
            {
                var paths = string.Join(";", group.Select(item => item.RelativePath));
                foreach (var removeKind in RemovalKinds(group.Key))
                {
                    itemGroup.Add(new XElement(
                        projectNamespace + removeKind,
                        new XAttribute("Remove", paths)));
                }
            }

            foreach (var item in explicitItems)
            {
                var migrated = new XElement(
                    projectNamespace + item.Kind,
                    new XAttribute("Include", item.RelativePath));
                foreach (var metadata in item.SourceElement.Elements())
                {
                    migrated.Add(new XElement(
                        projectNamespace + metadata.Name.LocalName,
                        metadata.Attributes().Select(attribute =>
                            new XAttribute(attribute.Name.LocalName, attribute.Value)),
                        metadata.Value));
                }
                itemGroup.Add(migrated);
            }

            targetDocument.Root.Add(itemGroup);
            targetDocument.Save(targetProject, SaveOptions.DisableFormatting);
            return new ProjectItemMigrationResult(
                sourceItems.Count,
                migratable.Count,
                accountedItems,
                unresolved,
                missingTargetItems,
                1);
        }

        private static IEnumerable<string> RemovalKinds(string itemKind) =>
            itemKind == "Content"
                ? ["None", "Content"]
                : ["None", "EmbeddedResource", "PRIResource"];

        private static MigrationLocation ProjectItemLocation(
            string sourceRoot,
            string sourceProject,
            XObject item)
        {
            var line = (item as IXmlLineInfo)?.HasLineInfo() == true
                ? ((IXmlLineInfo)item).LineNumber
                : (int?)null;
            return new MigrationLocation
            {
                Path = NormalizePath(Path.Combine(
                    ".uwp-source",
                    Path.GetRelativePath(sourceRoot, sourceProject) + ".reference")),
                Line = line
            };
        }

        internal static MigrationMechanicalVerification VerifyMechanicalMigration(
            string sourceRoot,
            IReadOnlyCollection<string> sourceFiles,
            IReadOnlyCollection<string> copied,
            IReadOnlyCollection<string> preserved,
            IReadOnlyCollection<string> intentionallyExcluded,
            string targetRoot,
            ProjectItemMigrationResult projectItems,
            MigrationReport report)
        {
            var copiedSet = copied
                .Select(NormalizePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var preservedSet = preserved
                .Select(NormalizePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var excludedSet = intentionallyExcluded
                .Select(NormalizePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sourceRelativePaths = sourceFiles
                .Select(path => NormalizePath(Path.GetRelativePath(sourceRoot, path)))
                .ToList();
            var unclassified = sourceRelativePaths
                .Where(path => !copiedSet.Contains(path)
                    && !preservedSet.Contains(path)
                    && !excludedSet.Contains(path))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var (residuals, uninspectedFiles) = FindLegacyNamespaceResiduals(targetRoot);
            RefreshMechanicalTodos(report, residuals, projectItems);

            var failed = residuals.Count > 0
                || unclassified.Count > 0
                || projectItems.MissingTargetItems.Count > 0;
            return new MigrationMechanicalVerification
            {
                Status = failed ? "failed" : "passed",
                Inventory = new MigrationFileInventory
                {
                    SourceFiles = sourceRelativePaths.Count,
                    ClassifiedFiles = sourceRelativePaths.Count - unclassified.Count,
                    CopiedFiles = copiedSet.Count,
                    PreservedReferenceFiles = preservedSet.Count,
                    IntentionallyExcludedFiles = excludedSet.Count,
                    UnclassifiedFiles = unclassified
                },
                LegacyNamespaceResiduals = residuals,
                UninspectedFiles = uninspectedFiles,
                ProjectItems = new MigrationProjectItemVerification
                {
                    SourceItems = projectItems.SourceItems,
                    MigratedItems = projectItems.MigratedItems,
                    AccountedItems = projectItems.AccountedItems,
                    UnresolvedItems = projectItems.UnresolvedItems,
                    MissingTargetItems = projectItems.MissingTargetItems
                }
            };
        }

        internal static MigrationMechanicalVerification VerifyExistingMigration(
            string sourceRoot,
            string? sourceProject,
            string targetRoot,
            string targetProject,
            MigrationReport report)
        {
            var projectItems = MigrateSourceProjectItems(
                sourceRoot,
                sourceProject,
                targetRoot,
                targetProject,
                applyChanges: false);
            var (residuals, uninspectedFiles) = FindLegacyNamespaceResiduals(targetRoot);
            RefreshMechanicalTodos(report, residuals, projectItems);

            var failed = residuals.Count > 0
                || report.MechanicalVerification.Inventory.UnclassifiedFiles.Count > 0
                || projectItems.MissingTargetItems.Count > 0;
            return new MigrationMechanicalVerification
            {
                Status = failed ? "failed" : "passed",
                Inventory = report.MechanicalVerification.Inventory,
                LegacyNamespaceResiduals = residuals,
                UninspectedFiles = uninspectedFiles,
                ProjectItems = new MigrationProjectItemVerification
                {
                    SourceItems = projectItems.SourceItems,
                    MigratedItems = projectItems.MigratedItems,
                    AccountedItems = projectItems.AccountedItems,
                    UnresolvedItems = projectItems.UnresolvedItems,
                    MissingTargetItems = projectItems.MissingTargetItems
                }
            };
        }

        private static (List<MigrationLocation> Residuals, int UninspectedFiles)
            FindLegacyNamespaceResiduals(string targetRoot)
        {
            var residuals = new List<MigrationLocation>();
            var uninspectedFiles = 0;
            foreach (var file in EnumerateFiles(targetRoot))
            {
                var relativePath = Path.GetRelativePath(targetRoot, file);
                if (string.Equals(
                        Path.GetFileName(relativePath),
                        "migration-report.json",
                        StringComparison.OrdinalIgnoreCase)
                    || relativePath.Split(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar)[0]
                        .Equals(".migration-evidence", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!TryReadInspectableText(file, out var text))
                {
                    uninspectedFiles++;
                    continue;
                }

                var lines = text.Split(["\r\n", "\n"], StringSplitOptions.None);
                for (var index = 0; index < lines.Length; index++)
                {
                    if (lines[index].Contains("Windows.UI.Xaml", StringComparison.Ordinal))
                    {
                        residuals.Add(new MigrationLocation
                        {
                            Path = NormalizePath(relativePath),
                            Line = index + 1
                        });
                    }
                }
            }
            return (residuals, uninspectedFiles);
        }

        private static void RefreshMechanicalTodos(
            MigrationReport report,
            List<MigrationLocation> residuals,
            ProjectItemMigrationResult projectItems)
        {
            report.Todos.RemoveAll(todo => todo.Id is "UWMIG011" or "UWMIG012");
            if (residuals.Count > 0)
            {
                report.Todos.Add(new MigrationTodo
                {
                    Id = "UWMIG011",
                    Category = "legacy-xaml-namespace",
                    Priority = "required",
                    Summary = "Resolve Windows.UI.Xaml references left after the mechanical namespace pass",
                    Reason = "The namespace transform promises that no unexplained Windows.UI.Xaml references remain in inspectable target text.",
                    Locations = residuals
                });
            }

            if (projectItems.UnresolvedItems.Count > 0
                || projectItems.MissingTargetItems.Count > 0)
            {
                report.Todos.Add(new MigrationTodo
                {
                    Id = "UWMIG012",
                    Category = "project-items",
                    Priority = "required",
                    Summary = "Resolve source Content or PRIResource items that could not be migrated deterministically",
                    Reason = "Conditional, wildcard, external, or missing source project items require an explicit target-project decision.",
                    Locations = projectItems.UnresolvedItems
                        .Concat(projectItems.MissingTargetItems)
                        .ToList()
                });
            }
        }

        private static bool TryReadInspectableText(string path, out string text)
        {
            var extension = Path.GetExtension(path);
            if (!KnownTextExtensions.Contains(extension)
                && new FileInfo(path).Length > UnknownTextInspectionLimit)
            {
                text = string.Empty;
                return false;
            }

            try
            {
                text = ReadTextFile(path).Content;
            }
            catch (DecoderFallbackException)
            {
                text = string.Empty;
                return false;
            }

            if (KnownTextExtensions.Contains(extension))
            {
                return true;
            }

            var controls = text.Count(character =>
                char.IsControl(character)
                && character is not '\r' and not '\n' and not '\t' and not '\f');
            if (controls > Math.Max(4, text.Length / 100))
            {
                text = string.Empty;
                return false;
            }
            return true;
        }

        private sealed class StringTupleComparer : IEqualityComparer<(string Kind, string RelativePath)>
        {
            internal static readonly StringTupleComparer OrdinalIgnoreCase = new();

            public bool Equals(
                (string Kind, string RelativePath) x,
                (string Kind, string RelativePath) y) =>
                string.Equals(x.Kind, y.Kind, StringComparison.Ordinal)
                && string.Equals(x.RelativePath, y.RelativePath, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode((string Kind, string RelativePath) value) =>
                HashCode.Combine(
                    StringComparer.Ordinal.GetHashCode(value.Kind),
                    StringComparer.OrdinalIgnoreCase.GetHashCode(value.RelativePath));
        }
    }
}
