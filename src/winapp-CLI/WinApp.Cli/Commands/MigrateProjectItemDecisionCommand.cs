// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal sealed class MigrateProjectItemDecisionCommand : Command, IShortDescription
{
    public string ShortDescription =>
        "Record and verify an explicit decision for a decision-required project item";

    public static Argument<DirectoryInfo> TargetArgument { get; } = new("target")
    {
        Description = "Migrated WinUI project directory containing migration-report.json.",
        Arity = ArgumentArity.ExactlyOne
    };

    public static Argument<string> ItemArgument { get; } = new("item")
    {
        Description = "Stable project-item ID from mechanicalVerification.projectItems.decisionRequiredItems. A unique ID prefix is accepted.",
        Arity = ArgumentArity.ExactlyOne
    };

    public static Argument<string> StrategyArgument { get; } = new("strategy")
    {
        Description = "Decision strategy: sdk-default-item, explicit-target-item, copied-linked-content, target-owned-replacement, target-not-required, or intentionally-not-migrated.",
        Arity = ArgumentArity.ExactlyOne
    };

    public static Argument<string> RationaleArgument { get; } = new("rationale")
    {
        Description = "Concise explanation of why this strategy mechanically accounts for the source item.",
        Arity = ArgumentArity.ExactlyOne
    };

    public static Option<string?> TargetPathOption { get; } = new("--target-path")
    {
        Description = "Literal target-relative file path that represents the source item."
    };

    public static Option<string?> TargetItemTypeOption { get; } = new("--target-item-type")
    {
        Description = "Target MSBuild item type when an explicit item is required: Content or PRIResource."
    };

    public static Option<string[]> EvidenceFileOption { get; } = new("--evidence-file")
    {
        Description = "Target/build-context-relative project/props/targets file reported by evaluated MSBuild facts as the matched item's defining project. Repeat for multiple files.",
        Arity = ArgumentArity.ZeroOrMore,
        AllowMultipleArgumentsPerToken = false
    };

    public static Option<string?> ReasonCodeOption { get; } = new("--reason-code")
    {
        Description = "Constrained reason code required by target-not-required. Supported: target-platform-not-applicable."
    };

    public MigrateProjectItemDecisionCommand()
        : base(
            "decide-project-item",
            "Record a structured decision for one decision-required source Content or PRIResource item. Target, item, strategy, and rationale are required positional arguments; the command never edits CLI-owned verification fields directly.")
    {
        TargetArgument.AcceptExistingOnly();
        Arguments.Add(TargetArgument);
        Arguments.Add(ItemArgument);
        Arguments.Add(StrategyArgument);
        Arguments.Add(RationaleArgument);
        Options.Add(TargetPathOption);
        Options.Add(TargetItemTypeOption);
        Options.Add(EvidenceFileOption);
        Options.Add(ReasonCodeOption);
    }

    internal sealed class Handler(
        IMsBuildProjectItemEvaluator msBuildProjectItemEvaluator)
        : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(
            ParseResult parseResult,
            CancellationToken cancellationToken = default)
        {
            var requestedTargetRoot = parseResult.GetValue(TargetArgument)!.FullName;
            if (!MigrationPathResolver.TryCanonicalizeRoot(
                    requestedTargetRoot,
                    out var targetRoot,
                    out var targetRootError))
            {
                Console.Out.WriteLine(
                    $"[ERROR] Migration target path is invalid: {targetRootError}");
                return 1;
            }
            var reportPath = Path.Combine(targetRoot, "migration-report.json");
            if (!File.Exists(reportPath))
            {
                Console.Out.WriteLine(
                    "[ERROR] migration-report.json was not found in the target directory.");
                return 1;
            }

            MigrationReportTransactionLock reportLock;
            try
            {
                reportLock = await MigrationReportStore.AcquireTransactionLockAsync(
                    reportPath,
                    cancellationToken);
            }
            catch (MigrationReportLockException exception)
            {
                Console.Out.WriteLine(
                    $"[ERROR] Could not lock migration-report.json: {exception.Message}");
                return 1;
            }
            await using var transactionScope = reportLock;

            MigrationReport report;
            try
            {
                report = await MigrationReportStore.LoadAsync(
                    reportPath,
                    cancellationToken);
            }
            catch (InvalidDataException exception)
            {
                Console.Out.WriteLine($"[ERROR] {exception.Message}");
                return 1;
            }

            if (!MigrationPathResolver.TryCanonicalizeRoot(
                    report.Source.Root,
                    out var sourceRoot,
                    out var sourceRootError))
            {
                Console.Out.WriteLine(
                    $"[ERROR] Source root recorded by migration-report.json is invalid: {sourceRootError}");
                return 1;
            }
            if (!MigrationPathResolver.TryResolveContainedRelativePath(
                    sourceRoot,
                    report.Source.ProjectFile,
                    out var sourceProject,
                    out _,
                    out var sourceProjectError))
            {
                Console.Out.WriteLine(
                    $"[ERROR] Source project path recorded by migration-report.json is invalid: {sourceProjectError}");
                return 1;
            }
            if (!MigrationPathResolver.TryResolveContainedRelativePath(
                    targetRoot,
                    report.Target.ProjectFile,
                    out var targetProject,
                    out _,
                    out var targetProjectError))
            {
                Console.Out.WriteLine(
                    $"[ERROR] Target project path recorded by migration-report.json is invalid: {targetProjectError}");
                return 1;
            }
            if (!File.Exists(sourceProject))
            {
                Console.Out.WriteLine(
                    "[ERROR] The source project recorded by migration-report.json was not found.");
                return 1;
            }
            if (!File.Exists(targetProject))
            {
                Console.Out.WriteLine(
                    "[ERROR] The target project recorded by migration-report.json was not found.");
                return 1;
            }

            var projectItemEvaluation =
                await MigrateCommand.Handler.EvaluateProjectItemFactsAsync(
                    targetRoot,
                    targetProject,
                    msBuildProjectItemEvaluator,
                    cancellationToken);
            var projectItems = MigrateCommand.Handler.MigrateSourceProjectItems(
                sourceRoot,
                sourceProject,
                targetRoot,
                targetProject,
                applyChanges: false,
                projectItemEvaluation);
            var selector = parseResult.GetValue(ItemArgument)?.Trim();
            var exactMatches = string.IsNullOrWhiteSpace(selector)
                ? []
                : projectItems.DecisionRequiredItems
                    .Where(item =>
                        item.Id.Equals(selector, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            var itemMatches = exactMatches.Count > 0
                ? exactMatches
                : string.IsNullOrWhiteSpace(selector)
                    ? []
                    : projectItems.DecisionRequiredItems
                        .Where(item => item.Id.StartsWith(
                            selector,
                            StringComparison.OrdinalIgnoreCase))
                        .ToList();
            if (itemMatches.Count == 0)
            {
                Console.Out.WriteLine(
                    $"[ERROR] No review-required project item matched '{selector}'.");
                WriteAvailableItems(projectItems.DecisionRequiredItems);
                return 1;
            }
            if (itemMatches.Count > 1)
            {
                Console.Out.WriteLine(
                    $"[ERROR] Project item selector '{selector}' is ambiguous.");
                WriteAvailableItems(itemMatches);
                return 1;
            }

            var strategy = parseResult.GetValue(StrategyArgument)?
                .Trim()
                .ToLowerInvariant();
            if (strategy is null
                || !MigrateCommand.Handler.SupportedProjectItemDecisionStrategies.Contains(
                    strategy))
            {
                Console.Out.WriteLine(
                    $"[ERROR] Unsupported strategy '{strategy}'. " +
                    $"Choose: {string.Join(", ", MigrateCommand.Handler.SupportedProjectItemDecisionStrategies.Order())}.");
                return 1;
            }

            var rationale = parseResult.GetValue(RationaleArgument)?.Trim();
            if (string.IsNullOrWhiteSpace(rationale)
                || rationale.Length > 500)
            {
                Console.Out.WriteLine(
                    "[ERROR] The rationale argument must contain 1 to 500 non-whitespace characters.");
                return 1;
            }

            var targetPath = parseResult.GetValue(TargetPathOption);
            var targetItemType = parseResult.GetValue(TargetItemTypeOption);
            var evidenceFiles = parseResult.GetValue(EvidenceFileOption) ?? [];
            var reasonCode = parseResult.GetValue(ReasonCodeOption)?
                .Trim()
                .ToLowerInvariant();
            if (strategy == MigrateCommand.Handler.IntentionallyNotMigratedStrategy
                && (!string.IsNullOrWhiteSpace(reasonCode)
                    || !string.IsNullOrWhiteSpace(targetPath)
                    || !string.IsNullOrWhiteSpace(targetItemType)
                    || evidenceFiles.Length > 0))
            {
                Console.Out.WriteLine(
                    "[ERROR] intentionally-not-migrated cannot include a reason code or target evidence.");
                return 1;
            }
            if (strategy == MigrateCommand.Handler.TargetNotRequiredStrategy
                && (!string.IsNullOrWhiteSpace(targetPath)
                    || !string.IsNullOrWhiteSpace(targetItemType)
                    || evidenceFiles.Length > 0))
            {
                Console.Out.WriteLine(
                    "[ERROR] target-not-required cannot include target evidence.");
                return 1;
            }
            if (strategy == MigrateCommand.Handler.TargetNotRequiredStrategy
                && (string.IsNullOrWhiteSpace(reasonCode)
                    || !MigrateCommand.Handler.SupportedTargetNotRequiredReasons.Contains(
                        reasonCode)))
            {
                Console.Out.WriteLine(
                    $"[ERROR] target-not-required requires --reason-code {MigrateCommand.Handler.TargetPlatformNotApplicableReason}.");
                return 1;
            }
            if (strategy != MigrateCommand.Handler.TargetNotRequiredStrategy
                && !string.IsNullOrWhiteSpace(reasonCode))
            {
                Console.Out.WriteLine(
                    "[ERROR] --reason-code is supported only with target-not-required.");
                return 1;
            }

            var sourceItem = itemMatches[0];
            var decision = new MigrationProjectItemDecision
            {
                ItemId = sourceItem.Id,
                Strategy = strategy,
                TargetPath = targetPath,
                TargetItemType = targetItemType,
                EvidenceFiles = [.. evidenceFiles],
                Rationale = rationale,
                ReasonCode = reasonCode
            };
            decision.Verification =
                MigrateCommand.Handler.VerifyProjectItemDecision(
                    sourceRoot,
                    targetRoot,
                    targetProject,
                    sourceItem,
                    decision,
                    projectItemEvaluation,
                    validateSuppliedEvidenceFiles: true);
            if (decision.Verification.Status == "invalid")
            {
                Console.Out.WriteLine(
                    $"[ERROR] Decision evidence is invalid: {decision.Verification.Reason}");
                return 1;
            }

            report.ProjectItemDecisions.RemoveAll(existing =>
                existing.ItemId.Equals(
                    sourceItem.Id,
                    StringComparison.OrdinalIgnoreCase));
            report.ProjectItemDecisions.Add(decision);
            report.ActivationAnalysis =
                MigrateCommand.Handler.AnalyzeActivationContracts(
                    sourceRoot,
                    targetRoot,
                    applyChanges: false,
                    report.ActivationAnalysis).Analysis;
            report.MechanicalVerification =
                await MigrateCommand.Handler.VerifyExistingMigrationAsync(
                    sourceRoot,
                    sourceProject,
                    targetRoot,
                    targetProject,
                    report,
                    msBuildProjectItemEvaluator,
                    cancellationToken,
                    projectItemEvaluation);
            report.Status = report.MechanicalVerification.Status == "passed"
                ? "mechanical-migration-complete"
                : "mechanical-verification-failed";
            report.Summary.TodoCategories = report.Todos.Count;
            try
            {
                await MigrationReportStore.WriteAtomicAsync(
                    reportPath,
                    report,
                    cancellationToken);
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or System.Security.SecurityException)
            {
                Console.Out.WriteLine(
                    $"[ERROR] Could not persist migration-report.json: {exception.Message}");
                return 1;
            }

            var recorded = report.ProjectItemDecisions.Single(existing =>
                existing.ItemId.Equals(
                    sourceItem.Id,
                    StringComparison.OrdinalIgnoreCase));
            Console.Out.WriteLine(
                $"Recorded {recorded.Strategy} for {sourceItem.Id}: " +
                $"{recorded.Verification.Status}.");
            if (!string.IsNullOrWhiteSpace(recorded.Verification.Reason))
            {
                Console.Out.WriteLine(recorded.Verification.Reason);
            }
            Console.Out.WriteLine(
                report.Todos.Any(todo => todo.Id == "UWMIG012")
                    ? "UWMIG012 remains pending."
                    : "UWMIG012 is resolved by accounted project-item decisions.");
            return 0;
        }

        private static void WriteAvailableItems(
            IEnumerable<MigrationReviewRequiredProjectItem> items)
        {
            foreach (var item in items)
            {
                Console.Out.WriteLine(
                    $"  {item.Id}  {item.ItemType} {item.ItemOperation}=\"{item.ItemSpec}\" " +
                    $"({item.SourceLocation.Path}:{item.SourceLocation.Line})");
            }
        }
    }
}
