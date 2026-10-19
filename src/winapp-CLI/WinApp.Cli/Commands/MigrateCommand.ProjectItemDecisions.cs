// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal partial class MigrateCommand
{
    public partial class Handler
    {
        internal const string SdkDefaultItemStrategy = "sdk-default-item";
        internal const string ExplicitTargetItemStrategy = "explicit-target-item";
        internal const string CopiedLinkedContentStrategy = "copied-linked-content";
        internal const string TargetOwnedReplacementStrategy = "target-owned-replacement";
        internal const string TargetNotRequiredStrategy = "target-not-required";
        internal const string IntentionallyNotMigratedStrategy = "intentionally-not-migrated";
        internal const string TargetPlatformNotApplicableReason =
            "target-platform-not-applicable";

        internal static readonly HashSet<string> SupportedProjectItemDecisionStrategies =
            new(StringComparer.Ordinal)
            {
                SdkDefaultItemStrategy,
                ExplicitTargetItemStrategy,
                CopiedLinkedContentStrategy,
                TargetOwnedReplacementStrategy,
                TargetNotRequiredStrategy,
                IntentionallyNotMigratedStrategy
            };

        internal static readonly HashSet<string> SupportedTargetNotRequiredReasons =
            new(StringComparer.Ordinal)
            {
                TargetPlatformNotApplicableReason
            };

        internal sealed record ProjectItemEvaluationContext(
            bool Succeeded,
            IReadOnlyList<EvaluatedProjectItem> Items,
            string BuildContextRoot,
            string? Error,
            MsBuildProjectItemEvaluation? RawEvaluation);

        internal sealed record EvaluatedProjectItem(
            string Kind,
            string FullPath,
            string DefiningProjectFullPath,
            string? DefiningProjectEvidencePath,
            IReadOnlyDictionary<string, string> Metadata);

        internal static async Task<ProjectItemEvaluationContext>
            EvaluateProjectItemFactsAsync(
                string targetRoot,
                string targetProject,
                IMsBuildProjectItemEvaluator evaluator,
                CancellationToken cancellationToken)
        {
            var buildContextRoot = FindTrustedBuildContextRoot(targetRoot);
            XDocument targetDocument;
            try
            {
                targetDocument = XDocument.Parse(
                    await File.ReadAllTextAsync(
                        targetProject,
                        cancellationToken));
            }
            catch (Exception exception) when (
                exception is XmlException
                or IOException
                or UnauthorizedAccessException)
            {
                return EvaluationFailure(
                    buildContextRoot,
                    $"The target project could not be read for authoritative MSBuild evaluation: {exception.Message}");
            }
            if (!IsSupportedMigrationTargetProject(targetDocument))
            {
                return EvaluationFailure(
                    buildContextRoot,
                    "The target project is not a supported Microsoft.NET.Sdk migration project.");
            }

            var evaluation = await evaluator.EvaluateAsync(
                new FileInfo(targetProject),
                cancellationToken);
            if (!evaluation.Succeeded)
            {
                return new ProjectItemEvaluationContext(
                    false,
                    [],
                    buildContextRoot,
                    evaluation.Error
                        ?? "dotnet msbuild did not return evaluated item facts.",
                    evaluation);
            }

            var items = new List<EvaluatedProjectItem>();
            foreach (var item in evaluation.Items.Where(item =>
                MigratedProjectItemKinds.Contains(item.Kind)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryCanonicalizeEvaluatedPath(
                        item.FullPath,
                        out var canonicalItemPath,
                        out var itemPathError))
                {
                    return EvaluationFailure(
                        buildContextRoot,
                        $"MSBuild returned an untrustworthy {item.Kind} FullPath for '{item.Identity}': {itemPathError}",
                        evaluation);
                }

                var relativeItemPath = Path.GetRelativePath(
                    targetRoot,
                    canonicalItemPath);
                if (Path.IsPathRooted(relativeItemPath)
                    || relativeItemPath == ".."
                    || relativeItemPath.StartsWith(
                        $"..{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal))
                {
                    continue;
                }
                if (!MigrationPathResolver.TryResolveContainedAbsolutePath(
                        targetRoot,
                        canonicalItemPath,
                        out canonicalItemPath,
                        out _,
                        out itemPathError))
                {
                    return EvaluationFailure(
                        buildContextRoot,
                        $"MSBuild returned an untrustworthy contained {item.Kind} FullPath for '{item.Identity}': {itemPathError}",
                        evaluation);
                }

                if (!TryCanonicalizeEvaluatedPath(
                        item.DefiningProjectFullPath,
                        out var definingProject,
                        out var definingProjectError)
                    || !File.Exists(definingProject))
                {
                    return EvaluationFailure(
                        buildContextRoot,
                        $"MSBuild returned untrustworthy defining evidence for {item.Kind} item '{item.Identity}': " +
                        (string.IsNullOrWhiteSpace(definingProjectError)
                            ? "the defining project file does not exist."
                            : definingProjectError),
                        evaluation);
                }

                string? definingEvidencePath = null;
                if (MigrationPathResolver.TryResolveContainedAbsolutePath(
                        buildContextRoot,
                        definingProject,
                        out var containedDefiningProject,
                        out _,
                        out _))
                {
                    definingEvidencePath = NormalizePath(
                        Path.GetRelativePath(
                            targetRoot,
                            containedDefiningProject));
                }

                items.Add(new EvaluatedProjectItem(
                    item.Kind,
                    canonicalItemPath,
                    definingProject,
                    definingEvidencePath,
                    item.Metadata));
            }

            return new ProjectItemEvaluationContext(
                true,
                items,
                buildContextRoot,
                null,
                evaluation);
        }

        private static ProjectItemEvaluationContext EvaluationFailure(
            string buildContextRoot,
            string error,
            MsBuildProjectItemEvaluation? rawEvaluation = null) =>
            new(false, [], buildContextRoot, error, rawEvaluation);

        internal static async Task<ProjectItemMigrationResult>
            ReconcileProjectItemDecisionsAsync(
                string sourceRoot,
                string targetRoot,
                string targetProject,
                ProjectItemMigrationResult projectItems,
                MigrationReport report,
                IMsBuildProjectItemEvaluator evaluator,
                CancellationToken cancellationToken,
                ProjectItemEvaluationContext? evaluation = null)
        {
            if (evaluation is null
                && report.ProjectItemDecisions.Any(decision =>
                    decision.Strategy != IntentionallyNotMigratedStrategy))
            {
                evaluation = await EvaluateProjectItemFactsAsync(
                    targetRoot,
                    targetProject,
                    evaluator,
                    cancellationToken);
            }

            var decisionItemsById = projectItems.DecisionRequiredItems
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
                cancellationToken.ThrowIfCancellationRequested();
                decision.Verification = new MigrationProjectItemDecisionVerification();
                if (!decisionItemsById.TryGetValue(decision.ItemId, out var matchingItems))
                {
                    decision.Verification.Status = "stale";
                    decision.Verification.Reason =
                        "The source item is no longer present in the current decision-required inventory.";
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
                    decision,
                    evaluation,
                    validateSuppliedEvidenceFiles: false);
            }

            var accountedIds = report.ProjectItemDecisions
                .Where(decision => decision.Verification.Status is "verified" or "accepted")
                .Select(decision => decision.ItemId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var structuredLocations = projectItems.DecisionRequiredItems
                .Select(item => item.SourceLocation)
                .ToHashSet(MigrationLocationComparer.Instance);
            var unresolved = projectItems.UnresolvedItems
                .Where(location => !structuredLocations.Contains(location))
                .Concat(projectItems.DecisionRequiredItems
                    .Where(item => !accountedIds.Contains(item.Id))
                    .Select(item => item.SourceLocation))
                .Distinct(MigrationLocationComparer.Instance)
                .ToList();
            var accountedExpectedTargets = projectItems.DecisionRequiredItems
                .Where(item => accountedIds.Contains(item.Id))
                .Select(item => item.ExpectedTargetPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missingTargetItems = projectItems.MissingTargetItems
                .Where(location => !accountedExpectedTargets.Contains(location.Path))
                .ToList();
            var verifiedDecisions = report.ProjectItemDecisions
                .Where(decision => decision.Verification.Status == "verified")
                .ToList();
            var acceptedDecisions = report.ProjectItemDecisions
                .Where(decision => decision.Verification.Status == "accepted")
                .ToList();
            var accountedItems = projectItems.AccountedItems
                .Concat(verifiedDecisions.Select(decision =>
                {
                    var sourceItem = decisionItemsById[decision.ItemId][0];
                    _ = TryCanonicalizeMigratedProjectItemKind(
                        decision.TargetItemType ?? sourceItem.ItemType,
                        out var itemKind);
                    return new MigrationProjectItem
                    {
                        Kind = itemKind,
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
                MissingTargetItems = missingTargetItems,
                VerifiedDecisionItems = verifiedDecisions.Count,
                AcceptedDecisionItems = acceptedDecisions.Count
            };
        }

        internal static MigrationProjectItemDecisionVerification
            VerifyProjectItemDecision(
                string sourceRoot,
                string targetRoot,
                string targetProject,
                MigrationReviewRequiredProjectItem sourceItem,
                MigrationProjectItemDecision decision,
                ProjectItemEvaluationContext? evaluation,
                bool validateSuppliedEvidenceFiles)
        {
            var verification = new MigrationProjectItemDecisionVerification();
            if (!SupportedProjectItemDecisionStrategies.Contains(decision.Strategy))
            {
                verification.Status = "invalid";
                verification.Reason =
                    $"Unsupported strategy '{decision.Strategy}'.";
                return verification;
            }
            if (string.IsNullOrWhiteSpace(decision.Rationale)
                || decision.Rationale.Length > 500)
            {
                verification.Status = "invalid";
                verification.Reason =
                    "A project-item decision requires a rationale containing 1 to 500 non-whitespace characters.";
                return verification;
            }
            if (decision.Strategy == IntentionallyNotMigratedStrategy)
            {
                if (!string.IsNullOrWhiteSpace(decision.ReasonCode)
                    || !string.IsNullOrWhiteSpace(decision.TargetPath)
                    || !string.IsNullOrWhiteSpace(decision.TargetItemType)
                    || decision.EvidenceFiles.Count > 0)
                {
                    verification.Status = "invalid";
                    verification.Reason =
                        "intentionally-not-migrated cannot include a reason code or target evidence.";
                    return verification;
                }
                verification.Status = "review-required";
                verification.Reason =
                    "Intentionally omitted items remain review-required and do not close UWMIG012.";
                return verification;
            }
            if (decision.Strategy == TargetNotRequiredStrategy)
            {
                if (!string.IsNullOrWhiteSpace(decision.TargetPath)
                    || !string.IsNullOrWhiteSpace(decision.TargetItemType)
                    || decision.EvidenceFiles.Count > 0)
                {
                    verification.Status = "invalid";
                    verification.Reason =
                        "target-not-required cannot include target path, item type, or project evidence.";
                    return verification;
                }
                if (string.IsNullOrWhiteSpace(decision.ReasonCode)
                    || !SupportedTargetNotRequiredReasons.Contains(decision.ReasonCode))
                {
                    verification.Status = "invalid";
                    verification.Reason =
                        $"target-not-required requires --reason-code {TargetPlatformNotApplicableReason}.";
                    return verification;
                }
                if (!TryRequireSuccessfulEvaluation(
                        evaluation,
                        out var evaluationError))
                {
                    verification.Status = "invalid";
                    verification.Reason =
                        $"target-not-required could not be anchored to the current target project: {evaluationError}";
                    return verification;
                }
                verification.ProjectEvidence =
                [
                    new MigrationLocation
                    {
                        Path = NormalizePath(
                            Path.GetRelativePath(
                                targetRoot,
                                targetProject))
                    }
                ];
                verification.Status = "accepted";
                verification.Reason = null;
                return verification;
            }
            if (decision.Strategy != TargetOwnedReplacementStrategy
                && sourceItem.ReviewReason is
                "conditional"
                or "choose-branch"
                or "wildcard"
                or "item-list"
                or "unmodeled-ancestor"
                or "project-item-operation")
            {
                verification.Status = "invalid";
                verification.Reason =
                    $"Source item reason '{sourceItem.ReviewReason}' cannot be resolved by a single deterministic target path.";
                return verification;
            }
            if (decision.Strategy != TargetOwnedReplacementStrategy
                && sourceItem.ReviewReason == "msbuild-expression")
            {
                verification.Status = "invalid";
                verification.Reason =
                    "MSBuild-expression source paths are unavailable for byte-hash verification. Use target-owned-replacement or record a constrained target-not-required decision.";
                return verification;
            }
            if (!TryCanonicalizeMigratedProjectItemKind(
                    sourceItem.ItemType,
                    out var sourceItemType))
            {
                verification.Status = "invalid";
                verification.Reason =
                    $"Source item type must be Content or PRIResource; found '{sourceItem.ItemType}'.";
                return verification;
            }

            var sourceContentAvailable = TryResolveSourceItemPath(
                sourceRoot,
                sourceItem,
                out var availableSourcePath,
                out var sourcePathError);
            var sourceContentExternal = sourceContentAvailable
                && !IsPathContainedByRoot(sourceRoot, availableSourcePath);
            if (decision.Strategy != TargetOwnedReplacementStrategy
                && (sourceItem.ReviewReason == "absolute-path"
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
            if (decision.Strategy != TargetOwnedReplacementStrategy
                && sourceItem.ReviewReason is
                    ("absolute-path" or "external-or-missing-source")
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
            if (!string.IsNullOrWhiteSpace(sourceItem.ExpectedTargetPath)
                && decision.Strategy != TargetOwnedReplacementStrategy)
            {
                var expectedTargetPath = NormalizeProjectItemPath(
                    sourceItem.ExpectedTargetPath);
                if (!normalizedTargetPath.Equals(
                        expectedTargetPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    verification.Status = "invalid";
                    verification.Reason =
                        $"{decision.Strategy} must target the source item's expected target path '{expectedTargetPath}'. Use target-owned-replacement for a different target-owned path.";
                    return verification;
                }
            }
            if (!File.Exists(targetPath))
            {
                verification.Status = "invalid";
                verification.Reason =
                    $"Target evidence file does not exist: {normalizedTargetPath}.";
                return verification;
            }

            var requestedTargetItemType =
                string.IsNullOrWhiteSpace(decision.TargetItemType)
                ? sourceItem.ItemType
                : decision.TargetItemType;
            if (!TryCanonicalizeMigratedProjectItemKind(
                    requestedTargetItemType,
                    out var targetItemType))
            {
                verification.Status = "invalid";
                verification.Reason =
                    $"Target item type must be Content or PRIResource; found '{requestedTargetItemType}'.";
                return verification;
            }
            decision.TargetItemType = targetItemType;

            if (!TryRequireSuccessfulEvaluation(
                    evaluation,
                    out var targetEvaluationError))
            {
                verification.Status = "invalid";
                verification.Reason =
                    $"Target MSBuild item evaluation failed: {targetEvaluationError}";
                return verification;
            }
            var matches = evaluation!.Items.Where(item =>
                    item.Kind.Equals(
                        targetItemType,
                        StringComparison.OrdinalIgnoreCase)
                    && item.FullPath.Equals(
                        targetPath,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count != 1)
            {
                verification.Status = "invalid";
                verification.Reason = matches.Count == 0
                    ? $"The evaluated target project does not own '{normalizedTargetPath}' as {targetItemType}."
                    : $"The evaluated target project returned ambiguous {targetItemType} ownership for '{normalizedTargetPath}'.";
                return verification;
            }
            var matchedItem = matches[0];
            if (decision.Strategy == SdkDefaultItemStrategy
                && !IsRecognizedSdkDefaultItemDefinition(
                    matchedItem.DefiningProjectFullPath))
            {
                verification.Status = "invalid";
                verification.Reason =
                    "sdk-default-item requires an evaluated item defined by Microsoft.NET.Sdk default-item targets; " +
                    $"the evaluated definition was '{matchedItem.DefiningProjectFullPath}'.";
                return verification;
            }
            if (decision.Strategy is
                    ExplicitTargetItemStrategy
                    or CopiedLinkedContentStrategy
                && matchedItem.DefiningProjectEvidencePath is null)
            {
                verification.Status = "invalid";
                verification.Reason =
                    $"{decision.Strategy} requires an evaluated item defined by a project, props, or targets file inside the trusted build context.";
                return verification;
            }
            if (!TryNormalizeAndValidateEvidenceFiles(
                    targetRoot,
                    evaluation.BuildContextRoot,
                    decision.EvidenceFiles,
                    matchedItem,
                    validateSuppliedEvidenceFiles,
                    out var evidenceFiles,
                    out var evidenceError))
            {
                verification.Status = "invalid";
                verification.Reason = evidenceError;
                return verification;
            }
            decision.EvidenceFiles = evidenceFiles;
            if (matchedItem.DefiningProjectEvidencePath is not null)
            {
                verification.ProjectEvidence =
                [
                    new MigrationLocation
                    {
                        Path = matchedItem.DefiningProjectEvidencePath
                    }
                ];
            }

            if (decision.Strategy == SdkDefaultItemStrategy)
            {
                if (sourceItemType != "PRIResource"
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
                if (!MetadataMatches(
                        sourceItem.Metadata,
                        matchedItem.Metadata,
                        out var metadataError))
                {
                    verification.Status = "invalid";
                    verification.Reason = metadataError;
                    return verification;
                }
            }
            else if (decision.Strategy == ExplicitTargetItemStrategy
                && !MetadataMatches(
                    sourceItem.Metadata,
                    matchedItem.Metadata,
                    out var metadataError))
            {
                verification.Status = "invalid";
                verification.Reason = metadataError;
                return verification;
            }

            if (decision.Strategy == CopiedLinkedContentStrategy)
            {
                if (!MetadataMatches(
                        sourceItem.Metadata,
                        matchedItem.Metadata,
                        out var metadataError))
                {
                    verification.Status = "invalid";
                    verification.Reason = metadataError;
                    return verification;
                }
                if (targetItemType != sourceItemType)
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
                    sourceSha256,
                    targetSha256,
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

        private static bool IsRecognizedSdkDefaultItemDefinition(
            string path)
        {
            if (!Path.GetFileName(path).Equals(
                    "Microsoft.NET.Sdk.DefaultItems.props",
                    StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileName(path).Equals(
                    "Microsoft.NET.Sdk.DefaultItems.targets",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var normalizedPath = path.Replace(
                Path.AltDirectorySeparatorChar,
                Path.DirectorySeparatorChar);
            return normalizedPath.Contains(
                $"{Path.DirectorySeparatorChar}Sdks{Path.DirectorySeparatorChar}Microsoft.NET.Sdk{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryRequireSuccessfulEvaluation(
            ProjectItemEvaluationContext? evaluation,
            out string error)
        {
            if (evaluation?.Succeeded == true)
            {
                error = string.Empty;
                return true;
            }
            error = evaluation?.Error
                ?? "authoritative target item facts were not available.";
            return false;
        }

        private static bool MetadataMatches(
            IReadOnlyCollection<MigrationProjectItemMetadata> requiredMetadata,
            IReadOnlyDictionary<string, string> evaluatedMetadata,
            out string error)
        {
            foreach (var metadata in requiredMetadata)
            {
                if (!evaluatedMetadata.TryGetValue(
                        metadata.Name,
                        out var evaluatedValue)
                    || !string.Equals(
                        metadata.Value.Trim(),
                        evaluatedValue.Trim(),
                        StringComparison.Ordinal))
                {
                    error =
                        $"Evaluated target metadata '{metadata.Name}' does not match the source value.";
                    return false;
                }
            }
            error = string.Empty;
            return true;
        }

        private static bool TryNormalizeAndValidateEvidenceFiles(
            string targetRoot,
            string buildContextRoot,
            IReadOnlyCollection<string> requestedEvidenceFiles,
            EvaluatedProjectItem matchedItem,
            bool validateSuppliedEvidenceFiles,
            out List<string> evidenceFiles,
            out string error)
        {
            evidenceFiles = [];
            error = string.Empty;
            if (!validateSuppliedEvidenceFiles)
            {
                if (matchedItem.DefiningProjectEvidencePath is not null)
                {
                    evidenceFiles.Add(
                        matchedItem.DefiningProjectEvidencePath);
                }
                return true;
            }
            foreach (var evidenceFile in requestedEvidenceFiles
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!string.Equals(
                        evidenceFile,
                        evidenceFile.Trim(),
                        StringComparison.Ordinal)
                    || Path.IsPathRooted(evidenceFile)
                    || evidenceFile.Contains(':')
                    || evidenceFile.Contains("$(", StringComparison.Ordinal)
                    || evidenceFile.Contains("@(", StringComparison.Ordinal)
                    || evidenceFile.IndexOfAny(['*', '?']) >= 0
                    || evidenceFile
                        .Replace(
                            Path.AltDirectorySeparatorChar,
                            Path.DirectorySeparatorChar)
                        .Split(
                            Path.DirectorySeparatorChar,
                            StringSplitOptions.RemoveEmptyEntries)
                        .Any(segment =>
                            segment != ".."
                            && segment != "."
                            && (segment.EndsWith(' ')
                                || segment.EndsWith('.'))))
                {
                    error =
                        "Evidence paths must be safe target/build-context-relative literal paths.";
                    return false;
                }

                string candidate;
                try
                {
                    candidate = Path.GetFullPath(
                        Path.Combine(targetRoot, evidenceFile));
                }
                catch (Exception exception) when (
                    exception is ArgumentException
                    or NotSupportedException
                    or PathTooLongException)
                {
                    error = $"Evidence path is invalid: {exception.Message}";
                    return false;
                }
                if (!MigrationPathResolver.TryResolveContainedAbsolutePath(
                        buildContextRoot,
                        candidate,
                        out var fullPath,
                        out _,
                        out var pathError))
                {
                    error = $"Evidence path is invalid: {pathError}";
                    return false;
                }
                if (!File.Exists(fullPath))
                {
                    error =
                        $"Project evidence file does not exist: {NormalizePath(Path.GetRelativePath(targetRoot, fullPath))}.";
                    return false;
                }
                if (!fullPath.Equals(
                        matchedItem.DefiningProjectFullPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    error =
                        $"Evidence file '{NormalizePath(Path.GetRelativePath(targetRoot, fullPath))}' is not the evaluated defining project for the matched target item.";
                    return false;
                }
            }
            if (matchedItem.DefiningProjectEvidencePath is not null)
            {
                evidenceFiles.Add(
                    matchedItem.DefiningProjectEvidencePath);
            }
            return true;
        }

        private static bool TryCanonicalizeEvaluatedPath(
            string value,
            out string fullPath,
            out string error)
        {
            fullPath = string.Empty;
            if (!Path.IsPathFullyQualified(value)
                || value.Contains("$(", StringComparison.Ordinal)
                || value.Contains("@(", StringComparison.Ordinal)
                || value.IndexOfAny(['*', '?']) >= 0)
            {
                error = "the path is not a fully qualified literal path.";
                return false;
            }
            return MigrationPathResolver.TryCanonicalizeRoot(
                value,
                out fullPath,
                out error);
        }

        private static string FindTrustedBuildContextRoot(string targetRoot)
        {
            for (var directory = new DirectoryInfo(targetRoot);
                 directory is not null;
                 directory = directory.Parent)
            {
                var gitMarker = Path.Combine(directory.FullName, ".git");
                if (Directory.Exists(gitMarker)
                    || File.Exists(gitMarker))
                {
                    return directory.FullName;
                }
            }
            return targetRoot;
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
