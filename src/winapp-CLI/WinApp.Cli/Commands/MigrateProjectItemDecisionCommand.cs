// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Models;

namespace WinApp.Cli.Commands;

internal sealed class MigrateProjectItemDecisionCommand : Command, IShortDescription
{
    public string ShortDescription =>
        "Record and verify an explicit decision for a review-required project item";

    public static Argument<DirectoryInfo> TargetArgument { get; } = new("target")
    {
        Description = "Migrated WinUI project directory containing migration-report.json."
    };

    public static Option<string> ItemOption { get; } = new("--item")
    {
        Description = "Stable project-item ID from mechanicalVerification.projectItems.reviewRequiredItems. A unique ID prefix is accepted.",
        Required = true
    };

    public static Option<string> StrategyOption { get; } = new("--strategy")
    {
        Description = "Decision strategy: sdk-default-item, explicit-target-item, copied-linked-content, or intentionally-not-migrated.",
        Required = true
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
        Description = "Target-relative project/props/targets file containing the matching Include or Update item. Repeat for multiple files.",
        Arity = ArgumentArity.ZeroOrMore,
        AllowMultipleArgumentsPerToken = false
    };

    public static Option<string> RationaleOption { get; } = new("--rationale")
    {
        Description = "Concise explanation of why this deterministic strategy preserves the source item.",
        Required = true
    };

    public MigrateProjectItemDecisionCommand()
        : base(
            "decide-project-item",
            "Record a structured, deterministically verified decision for one review-required source Content or PRIResource item. This command never edits CLI-owned verification fields directly.")
    {
        TargetArgument.AcceptExistingOnly();
        Arguments.Add(TargetArgument);
        Options.Add(ItemOption);
        Options.Add(StrategyOption);
        Options.Add(TargetPathOption);
        Options.Add(TargetItemTypeOption);
        Options.Add(EvidenceFileOption);
        Options.Add(RationaleOption);
    }

    internal sealed class Handler : AsynchronousCommandLineAction
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

            var projectItems = MigrateCommand.Handler.MigrateSourceProjectItems(
                sourceRoot,
                sourceProject,
                targetRoot,
                targetProject,
                applyChanges: false);
            var selector = parseResult.GetValue(ItemOption)?.Trim();
            var exactMatches = string.IsNullOrWhiteSpace(selector)
                ? []
                : projectItems.ReviewRequiredItems
                    .Where(item =>
                        item.Id.Equals(selector, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            var itemMatches = exactMatches.Count > 0
                ? exactMatches
                : string.IsNullOrWhiteSpace(selector)
                    ? []
                    : projectItems.ReviewRequiredItems
                        .Where(item => item.Id.StartsWith(
                            selector,
                            StringComparison.OrdinalIgnoreCase))
                        .ToList();
            if (itemMatches.Count == 0)
            {
                Console.Out.WriteLine(
                    $"[ERROR] No review-required project item matched '{selector}'.");
                WriteAvailableItems(projectItems.ReviewRequiredItems);
                return 1;
            }
            if (itemMatches.Count > 1)
            {
                Console.Out.WriteLine(
                    $"[ERROR] Project item selector '{selector}' is ambiguous.");
                WriteAvailableItems(itemMatches);
                return 1;
            }

            var strategy = parseResult.GetValue(StrategyOption)?
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

            var rationale = parseResult.GetValue(RationaleOption)?.Trim();
            if (string.IsNullOrWhiteSpace(rationale)
                || rationale.Length > 500)
            {
                Console.Out.WriteLine(
                    "[ERROR] --rationale must contain 1 to 500 non-whitespace characters.");
                return 1;
            }

            var targetPath = parseResult.GetValue(TargetPathOption);
            var targetItemType = parseResult.GetValue(TargetItemTypeOption);
            var evidenceFiles = parseResult.GetValue(EvidenceFileOption) ?? [];
            if (strategy == MigrateCommand.Handler.IntentionallyNotMigratedStrategy
                && (!string.IsNullOrWhiteSpace(targetPath)
                    || !string.IsNullOrWhiteSpace(targetItemType)
                    || evidenceFiles.Length > 0))
            {
                Console.Out.WriteLine(
                    "[ERROR] intentionally-not-migrated cannot include target evidence.");
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
                Rationale = rationale
            };
            decision.Verification =
                MigrateCommand.Handler.VerifyProjectItemDecision(
                    sourceRoot,
                    targetRoot,
                    targetProject,
                    sourceItem,
                    decision);
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
                    applyChanges: false).Analysis;
            report.MechanicalVerification =
                MigrateCommand.Handler.VerifyExistingMigration(
                    sourceRoot,
                    sourceProject,
                    targetRoot,
                    targetProject,
                    report);
            report.Status = report.MechanicalVerification.Status == "passed"
                ? "mechanical-migration-complete"
                : "mechanical-verification-failed";
            report.Summary.TodoCategories = report.Todos.Count;
            await MigrationReportStore.WriteAtomicAsync(
                reportPath,
                report,
                cancellationToken);

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
                    : "UWMIG012 is resolved by verified project-item decisions.");
            return 0;
        }

        private static void WriteAvailableItems(
            IEnumerable<MigrationReviewRequiredProjectItem> items)
        {
            foreach (var item in items)
            {
                Console.Out.WriteLine(
                    $"  {item.Id}  {item.ItemType} Include=\"{item.Include}\" " +
                    $"({item.SourceLocation.Path}:{item.SourceLocation.Line})");
            }
        }
    }
}
