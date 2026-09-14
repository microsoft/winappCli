// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
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
            if (sourceItem.ReviewReason is
                "conditional"
                or "wildcard"
                or "msbuild-expression"
                or "unmodeled-ancestor")
            {
                verification.Status = "invalid";
                verification.Reason =
                    $"Source item reason '{sourceItem.ReviewReason}' cannot be resolved by a single deterministic target path.";
                return verification;
            }

            var sourceContentAvailable = TryResolveSourceItemPath(
                sourceRoot,
                sourceItem.Include,
                out var availableSourcePath,
                out var sourcePathError);
            var sourceContentExternal = sourceContentAvailable
                && !IsPathContainedByRoot(sourceRoot, availableSourcePath);
            if ((sourceItem.ReviewReason == "absolute-path"
                    || (sourceItem.ReviewReason == "external-or-missing-source"
                        && sourceContentExternal))
                && sourceContentAvailable
                && decision.Strategy != CopiedLinkedContentStrategy)
            {
                verification.Status = "invalid";
                verification.Reason =
                    "Available absolute or external source content must use copied-linked-content so source and target bytes can be verified.";
                return verification;
            }
            if (sourceItem.ReviewReason is "absolute-path" or "external-or-missing-source"
                && !sourceContentAvailable)
            {
                verification.Status = "invalid";
                verification.Reason =
                    $"{sourcePathError} Missing absolute or external source content cannot be mechanically resolved.";
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
            if (!TryBuildProjectEvidenceGraph(
                    targetRoot,
                    targetProject,
                    out var evidenceGraph,
                    out var evidenceGraphError))
            {
                verification.Status = "invalid";
                verification.Reason = evidenceGraphError;
                return verification;
            }
            if (!TryValidateProjectEvidenceFiles(
                    evidenceGraph,
                    evidenceFiles,
                    out var evidenceParticipationError))
            {
                verification.Status = "invalid";
                verification.Reason = evidenceParticipationError;
                return verification;
            }

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
                if (!TryVerifySdkDefaultPriResourceCoverage(
                        evidenceGraph,
                        normalizedTargetPath,
                        out var defaultCoverageError))
                {
                    verification.Status = "invalid";
                    verification.Reason = defaultCoverageError;
                    return verification;
                }

                if (sourceItem.Metadata.Count == 0)
                {
                    verification.Status = "verified";
                    verification.Reason = null;
                    return verification;
                }

                var evidence = FindProjectItemEvidence(
                    evidenceGraph,
                    evidenceFiles,
                    "PRIResource",
                    normalizedTargetPath,
                    sourceItem.Metadata,
                    allowUpdate: true);
                verification.ProjectEvidence = evidence.Matches;
                if (evidence.Matches.Count == 0)
                {
                    verification.Status = "invalid";
                    verification.Reason = evidence.FailureReason
                        ?? "The source PRIResource metadata is not represented by an active matching target Include or Update item.";
                    return verification;
                }

                verification.Status = "verified";
                verification.Reason = null;
                return verification;
            }

            var projectEvidence = FindProjectItemEvidence(
                evidenceGraph,
                evidenceFiles,
                targetItemType,
                normalizedTargetPath,
                sourceItem.Metadata,
                allowUpdate: false);
            verification.ProjectEvidence = projectEvidence.Matches;
            if (projectEvidence.Matches.Count == 0)
            {
                verification.Status = "invalid";
                verification.Reason = projectEvidence.FailureReason
                    ?? $"No active matching {targetItemType} Include item was found for '{normalizedTargetPath}'.";
                return verification;
            }

            if (decision.Strategy == CopiedLinkedContentStrategy)
            {
                if (!MigratedProjectItemKinds.Contains(sourceItem.ItemType)
                    || !string.Equals(
                        targetItemType,
                        sourceItem.ItemType,
                        StringComparison.Ordinal))
                {
                    verification.Status = "invalid";
                    verification.Reason =
                        "copied-linked-content requires matching source and target Content or PRIResource item types.";
                    return verification;
                }
                if (!sourceContentAvailable)
                {
                    verification.Status = "invalid";
                    verification.Reason = sourcePathError;
                    return verification;
                }

                if (!TryComputeSha256(
                        availableSourcePath,
                        out var sourceSha256,
                        out var sourceHashError))
                {
                    verification.Status = "invalid";
                    verification.Reason =
                        $"Source content could not be hashed: {sourceHashError}";
                    return verification;
                }
                if (!TryComputeSha256(
                        targetPath,
                        out var targetSha256,
                        out var targetHashError))
                {
                    verification.Status = "invalid";
                    verification.Reason =
                        $"Target content could not be hashed: {targetHashError}";
                    return verification;
                }
                verification.SourceSha256 = sourceSha256;
                verification.TargetSha256 = targetSha256;
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
            if (!MigrationPathResolver.TryResolveContainedRelativePath(
                    targetRoot,
                    value,
                    out fullPath,
                    out var relativePath,
                    out error))
            {
                return false;
            }
            normalizedPath = NormalizeProjectItemPath(relativePath);
            return true;
        }

        private static bool IsPathContainedByRoot(
            string root,
            string path)
        {
            var relativePath = Path.GetRelativePath(
                Path.GetFullPath(root),
                Path.GetFullPath(path));
            return MigrationPathResolver.TryResolveContainedRelativePath(
                root,
                relativePath,
                out _,
                out _,
                out _);
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

        private static bool TryComputeSha256(
            string path,
            out string hash,
            out string error)
        {
            hash = string.Empty;
            error = string.Empty;
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    FileOptions.SequentialScan);
                hash = Convert.ToHexString(SHA256.HashData(stream))
                    .ToLowerInvariant();
                return true;
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or System.Security.SecurityException
                or CryptographicException)
            {
                error = exception.Message;
                return false;
            }
        }

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
