// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Models;

namespace WinApp.Cli.Commands;

internal sealed class MigrateVerifyCommand : Command, IShortDescription
{
    public string ShortDescription => "Re-run deterministic verification for a migrated UWP project";

    public static Argument<DirectoryInfo> TargetArgument { get; } = new("target")
    {
        Description = "Migrated WinUI project directory containing migration-report.json."
    };

    public MigrateVerifyCommand()
        : base("verify", "Re-run namespace residual, activation declaration, and project-item decision checks against the recorded migration inventory without modifying application source or behavioral validation.")
    {
        TargetArgument.AcceptExistingOnly();
        Arguments.Add(TargetArgument);
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
                Console.Out.WriteLine("[ERROR] migration-report.json was not found in the target directory.");
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
            if (!Directory.Exists(sourceRoot))
            {
                Console.Out.WriteLine($"[ERROR] Migration source directory no longer exists: {sourceRoot}");
                return 1;
            }

            string? sourceProject = null;
            if (report.Source.ProjectFile is not null)
            {
                if (!MigrationPathResolver.TryResolveContainedRelativePath(
                        sourceRoot,
                        report.Source.ProjectFile,
                        out sourceProject,
                        out _,
                        out var sourceProjectError))
                {
                    Console.Out.WriteLine(
                        $"[ERROR] Source project path recorded by migration-report.json is invalid: {sourceProjectError}");
                    return 1;
                }
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
            if (sourceProject is not null
                && !File.Exists(sourceProject))
            {
                Console.Out.WriteLine("[ERROR] The source project recorded by migration-report.json was not found.");
                return 1;
            }
            if (!File.Exists(targetProject))
            {
                Console.Out.WriteLine("[ERROR] The target project recorded by migration-report.json was not found.");
                return 1;
            }

            report.ActivationAnalysis = MigrateCommand.Handler.AnalyzeActivationContracts(
                sourceRoot,
                targetRoot,
                applyChanges: false,
                report.ActivationAnalysis).Analysis;
            report.MechanicalVerification = MigrateCommand.Handler.VerifyExistingMigration(
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

            var verification = report.MechanicalVerification;
            Console.Out.WriteLine(
                verification.Status == "passed"
                    ? "Mechanical migration verification passed."
                    : "Mechanical migration verification failed.");
            Console.Out.WriteLine(
                $"Legacy namespace residuals: {verification.LegacyNamespaceResiduals.Count}; " +
                $"project items: {verification.ProjectItems.MigratedItems}/{verification.ProjectItems.SourceItems}; " +
                $"unclassified source files: {verification.Inventory.UnclassifiedFiles.Count}.");
            Console.Out.WriteLine(
                $"Activation contracts: {verification.ActivationContracts.VerifiedContracts}/" +
                $"{verification.ActivationContracts.MigratedContracts} verified; " +
                $"{verification.ActivationContracts.ReviewRequiredContracts} review-required; " +
                $"{verification.ActivationContracts.Issues} issue(s).");
            return verification.Status == "passed" ? 0 : 1;
        }
    }
}
