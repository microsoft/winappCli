// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using WinApp.Cli.Models;

namespace WinApp.Cli.Commands;

internal partial class MigrateCommand
{
    public partial class Handler
    {
        internal const string SdkDefaultItemStrategy = "sdk-default-item";
        internal const string ExplicitTargetItemStrategy = "explicit-target-item";
        internal const string CopiedLinkedContentStrategy = "copied-linked-content";
        internal const string IntentionallyNotMigratedStrategy = "intentionally-not-migrated";

        internal static readonly HashSet<string> SupportedProjectItemDecisionStrategies =
            new(StringComparer.Ordinal)
            {
                SdkDefaultItemStrategy,
                ExplicitTargetItemStrategy,
                CopiedLinkedContentStrategy,
                IntentionallyNotMigratedStrategy
            };

        internal static ProjectItemMigrationResult ReconcileProjectItemDecisions(
            string sourceRoot,
            string targetRoot,
            string targetProject,
            ProjectItemMigrationResult projectItems,
            MigrationReport report)
        {
            var reviewById = projectItems.ReviewRequiredItems
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.ToList(),
                    StringComparer.OrdinalIgnoreCase);
            var decisionsById = report.ProjectItemDecisions
                .GroupBy(decision => decision.ItemId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.ToList(),
                    StringComparer.OrdinalIgnoreCase);

            foreach (var decision in report.ProjectItemDecisions)
            {
                decision.Verification = new MigrationProjectItemDecisionVerification();
                if (!reviewById.TryGetValue(decision.ItemId, out var matchingItems))
                {
                    decision.Verification.Status = "stale";
                    decision.Verification.Reason =
                        "The source item is no longer present in the current review-required inventory.";
                    continue;
                }
                if (matchingItems.Count != 1)
                {
                    decision.Verification.Status = "invalid";
                    decision.Verification.Reason =
                        "The source item identity is ambiguous in the current inventory.";
                    continue;
                }
                if (decisionsById[decision.ItemId].Count != 1)
                {
                    decision.Verification.Status = "invalid";
                    decision.Verification.Reason =
                        "Multiple decisions were recorded for the same source item.";
                    continue;
                }

                decision.Verification = VerifyProjectItemDecision(
                    sourceRoot,
                    targetRoot,
                    targetProject,
                    matchingItems[0],
                    decision);
            }

            var verifiedIds = report.ProjectItemDecisions
                .Where(decision => decision.Verification.Status == "verified")
                .Select(decision => decision.ItemId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var structuredLocations = projectItems.ReviewRequiredItems
                .Select(item => item.SourceLocation)
                .ToHashSet(MigrationLocationComparer.Instance);
            var unresolved = projectItems.UnresolvedItems
                .Where(location => !structuredLocations.Contains(location))
                .Concat(projectItems.ReviewRequiredItems
                    .Where(item => !verifiedIds.Contains(item.Id))
                    .Select(item => item.SourceLocation))
                .Distinct(MigrationLocationComparer.Instance)
                .ToList();
            var verifiedDecisions = report.ProjectItemDecisions
                .Where(decision => decision.Verification.Status == "verified")
                .ToList();
            var accountedItems = projectItems.AccountedItems
                .Concat(verifiedDecisions.Select(decision =>
                {
                    var sourceItem = reviewById[decision.ItemId][0];
                    return new MigrationProjectItem
                    {
                        Kind = decision.TargetItemType ?? sourceItem.ItemType,
                        Path = decision.TargetPath ?? sourceItem.Link ?? sourceItem.Include,
                        RequiresProjectEntry =
                            decision.Strategy != SdkDefaultItemStrategy
                    };
                }))
                .ToList();

            return projectItems with
            {
                MigratedItems = projectItems.MigratedItems + verifiedDecisions.Count,
                AccountedItems = accountedItems,
                UnresolvedItems = unresolved,
                VerifiedDecisionItems = verifiedDecisions.Count
            };
        }

        internal static MigrationProjectItemDecisionVerification VerifyProjectItemDecision(
            string sourceRoot,
            string targetRoot,
            string targetProject,
            MigrationReviewRequiredProjectItem sourceItem,
            MigrationProjectItemDecision decision)
        {
            var verification = new MigrationProjectItemDecisionVerification();
            if (!SupportedProjectItemDecisionStrategies.Contains(decision.Strategy))
            {
                verification.Status = "invalid";
                verification.Reason =
                    $"Unsupported strategy '{decision.Strategy}'.";
                return verification;
            }
            if (decision.Strategy == IntentionallyNotMigratedStrategy)
            {
                verification.Status = "review-required";
                verification.Reason =
                    "Intentionally omitted items remain review-required and do not close UWMIG012.";
                return verification;
            }
            if (sourceItem.ReviewReason is "conditional" or "wildcard" or "msbuild-expression")
            {
                verification.Status = "invalid";
                verification.Reason =
                    $"Source item reason '{sourceItem.ReviewReason}' cannot be resolved by a single deterministic target path.";
                return verification;
            }
            if (sourceItem.ReviewReason == "external-or-missing-source"
                && sourceItem.ItemType == "Content"
                && decision.Strategy != CopiedLinkedContentStrategy)
            {
                verification.Status = "invalid";
                verification.Reason =
                    "External linked Content must use copied-linked-content so source and target bytes can be verified.";
                return verification;
            }
            if (!TryResolveTargetPath(
                    targetRoot,
                    decision.TargetPath,
                    out var targetPath,
                    out var normalizedTargetPath,
                    out var pathError))
            {
                verification.Status = "invalid";
                verification.Reason = pathError;
                return verification;
            }
            decision.TargetPath = normalizedTargetPath;
            if (!File.Exists(targetPath))
            {
                verification.Status = "invalid";
                verification.Reason =
                    $"Target evidence file does not exist: {normalizedTargetPath}.";
                return verification;
            }

            var targetItemType = string.IsNullOrWhiteSpace(decision.TargetItemType)
                ? sourceItem.ItemType
                : decision.TargetItemType;
            if (!MigratedProjectItemKinds.Contains(targetItemType))
            {
                verification.Status = "invalid";
                verification.Reason =
                    $"Target item type must be Content or PRIResource; found '{targetItemType}'.";
                return verification;
            }
            decision.TargetItemType = targetItemType;

            var evidenceFiles = new List<string>();
            foreach (var evidenceFile in decision.EvidenceFiles
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!TryResolveTargetPath(
                        targetRoot,
                        evidenceFile,
                        out var evidencePath,
                        out var normalizedEvidencePath,
                        out pathError))
                {
                    verification.Status = "invalid";
                    verification.Reason = $"Evidence path is invalid: {pathError}";
                    return verification;
                }
                if (!File.Exists(evidencePath))
                {
                    verification.Status = "invalid";
                    verification.Reason =
                        $"Project evidence file does not exist: {normalizedEvidencePath}.";
                    return verification;
                }
                evidenceFiles.Add(normalizedEvidencePath);
            }
            decision.EvidenceFiles = evidenceFiles;

            if (decision.Strategy == SdkDefaultItemStrategy)
            {
                if (sourceItem.ItemType != "PRIResource"
                    || targetItemType != "PRIResource"
                    || !normalizedTargetPath.EndsWith(
                        ".resw",
                        StringComparison.OrdinalIgnoreCase))
                {
                    verification.Status = "invalid";
                    verification.Reason =
                        "sdk-default-item is supported only for PRIResource .resw items.";
                    return verification;
                }

                if (sourceItem.Metadata.Count == 0)
                {
                    verification.Status = "verified";
                    verification.Reason = null;
                    return verification;
                }

                var matches = FindProjectItemEvidence(
                    targetRoot,
                    targetProject,
                    evidenceFiles,
                    "PRIResource",
                    normalizedTargetPath,
                    sourceItem.Metadata);
                verification.ProjectEvidence = matches;
                if (matches.Count == 0)
                {
                    verification.Status = "invalid";
                    verification.Reason =
                        "The source PRIResource metadata is not represented by a matching target Include or Update item.";
                    return verification;
                }

                verification.Status = "verified";
                verification.Reason = null;
                return verification;
            }

            var projectMatches = FindProjectItemEvidence(
                targetRoot,
                targetProject,
                evidenceFiles,
                targetItemType,
                normalizedTargetPath,
                sourceItem.Metadata);
            verification.ProjectEvidence = projectMatches;
            if (projectMatches.Count == 0)
            {
                verification.Status = "invalid";
                verification.Reason =
                    $"No matching {targetItemType} Include or Update item was found for '{normalizedTargetPath}'.";
                return verification;
            }

            if (decision.Strategy == CopiedLinkedContentStrategy)
            {
                if (sourceItem.ItemType != "Content"
                    || targetItemType != "Content"
                    || string.IsNullOrWhiteSpace(sourceItem.Link))
                {
                    verification.Status = "invalid";
                    verification.Reason =
                        "copied-linked-content requires a source Content item with Link metadata.";
                    return verification;
                }
                if (!TryResolveSourceItemPath(
                        sourceRoot,
                        sourceItem.Include,
                        out var sourcePath,
                        out var sourceError))
                {
                    verification.Status = "invalid";
                    verification.Reason = sourceError;
                    return verification;
                }

                verification.SourceSha256 = ComputeSha256(sourcePath);
                verification.TargetSha256 = ComputeSha256(targetPath);
                verification.ContentMatches = string.Equals(
                    verification.SourceSha256,
                    verification.TargetSha256,
                    StringComparison.Ordinal);
                if (verification.ContentMatches != true)
                {
                    verification.Status = "invalid";
                    verification.Reason =
                        "The copied target content does not match the available source content.";
                    return verification;
                }
            }

            verification.Status = "verified";
            verification.Reason = null;
            return verification;
        }

        private static List<MigrationLocation> FindProjectItemEvidence(
            string targetRoot,
            string targetProject,
            IReadOnlyCollection<string> evidenceFiles,
            string itemType,
            string targetPath,
            IReadOnlyCollection<MigrationProjectItemMetadata> requiredMetadata)
        {
            var projectFiles = new[] { NormalizePath(Path.GetRelativePath(targetRoot, targetProject)) }
                .Concat(evidenceFiles)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            var matches = new List<MigrationLocation>();
            foreach (var relativeProjectFile in projectFiles)
            {
                var projectFile = Path.Combine(
                    targetRoot,
                    relativeProjectFile.Replace('/', Path.DirectorySeparatorChar));
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
                    continue;
                }

                foreach (var element in document.Descendants().Where(element =>
                    element.Name.LocalName == itemType
                    && ItemPathMatches(element, targetPath)
                    && MetadataMatches(element, requiredMetadata)))
                {
                    matches.Add(new MigrationLocation
                    {
                        Path = NormalizePath(relativeProjectFile),
                        Line = (element as IXmlLineInfo)?.HasLineInfo() == true
                            ? ((IXmlLineInfo)element).LineNumber
                            : null
                    });
                }
            }
            return matches;
        }

        private static bool ItemPathMatches(
            XElement item,
            string targetPath)
        {
            var value = item.Attribute("Include")?.Value
                ?? item.Attribute("Update")?.Value;
            if (value is null)
            {
                return false;
            }
            return value.Split(
                    ';',
                    StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries)
                .Select(NormalizeProjectItemPath)
                .Contains(targetPath, StringComparer.OrdinalIgnoreCase);
        }

        private static bool MetadataMatches(
            XElement targetItem,
            IReadOnlyCollection<MigrationProjectItemMetadata> requiredMetadata) =>
            requiredMetadata.All(metadata => targetItem.Elements().Any(element =>
                element.Name.LocalName == metadata.Name
                && string.Equals(
                    element.Value.Trim(),
                    metadata.Value.Trim(),
                    StringComparison.Ordinal)));

        private static bool TryResolveTargetPath(
            string targetRoot,
            string? value,
            out string fullPath,
            out string normalizedPath,
            out string error)
        {
            fullPath = string.Empty;
            normalizedPath = string.Empty;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                error = "--target-path is required for this strategy.";
                return false;
            }
            if (Path.IsPathRooted(value)
                || value.Contains("$(", StringComparison.Ordinal)
                || value.IndexOfAny(['*', '?']) >= 0)
            {
                error = "Target paths must be literal paths relative to the migration target.";
                return false;
            }

            try
            {
                fullPath = Path.GetFullPath(Path.Combine(targetRoot, value));
            }
            catch (Exception exception) when (
                exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
            {
                error = exception.Message;
                return false;
            }
            var relativePath = Path.GetRelativePath(targetRoot, fullPath);
            if (relativePath == ".."
                || relativePath.StartsWith(
                    $"..{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            {
                error = "Target paths cannot escape the migration target.";
                return false;
            }
            normalizedPath = NormalizeProjectItemPath(relativePath);
            return true;
        }

        private static bool TryResolveSourceItemPath(
            string sourceRoot,
            string include,
            out string fullPath,
            out string error)
        {
            error = string.Empty;
            try
            {
                fullPath = Path.GetFullPath(Path.Combine(sourceRoot, include));
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
                error = $"Source content is unavailable: {include}.";
                return false;
            }
            return true;
        }

        private static string NormalizeProjectItemPath(string path) =>
            NormalizePath(path.Trim().Replace(
                Path.AltDirectorySeparatorChar,
                Path.DirectorySeparatorChar));

        private static string ComputeSha256(string path) =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
                .ToLowerInvariant();

        private sealed class MigrationLocationComparer :
            IEqualityComparer<MigrationLocation>
        {
            internal static readonly MigrationLocationComparer Instance = new();

            public bool Equals(MigrationLocation? x, MigrationLocation? y) =>
                x is not null
                && y is not null
                && string.Equals(
                    x.Path,
                    y.Path,
                    StringComparison.OrdinalIgnoreCase)
                && x.Line == y.Line;

            public int GetHashCode(MigrationLocation value) =>
                HashCode.Combine(
                    StringComparer.OrdinalIgnoreCase.GetHashCode(value.Path),
                    value.Line);
        }
    }
}
