// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Xml;
using System.Xml.Linq;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal sealed class MigrateVerifyCommand : Command, IShortDescription
{
    public string ShortDescription => "Re-run deterministic verification for a migrated UWP project";

    public static Argument<DirectoryInfo> TargetArgument { get; } = new("target")
    {
        Description = "Migrated WinUI project directory containing migration-report.json."
    };

    public static Option<string?> TargetProjectOption { get; } = new("--target-project")
    {
        Description = "Target-relative path to the contained existing supported target project. Use this after renaming the migrated project."
    };

    public MigrateVerifyCommand()
        : base("verify", "Re-run namespace residual, activation declaration, project-item decision, and target-project ownership checks against the recorded migration inventory without modifying application source or behavioral validation.")
    {
        TargetArgument.AcceptExistingOnly();
        Arguments.Add(TargetArgument);
        Options.Add(TargetProjectOption);
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
            var requestedTargetProject = parseResult.GetValue(TargetProjectOption);
            var targetProjectValue = string.IsNullOrWhiteSpace(requestedTargetProject)
                ? report.Target.ProjectFile
                : requestedTargetProject;
            if (string.IsNullOrWhiteSpace(targetProjectValue))
            {
                Console.Out.WriteLine(
                    "[ERROR] The migration report does not identify a target project. Specify --target-project.");
                return 1;
            }
            if (!MigrationPathResolver.TryResolveContainedRelativePath(
                    targetRoot,
                    targetProjectValue,
                    out var targetProject,
                    out var normalizedTargetProject,
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
                Console.Out.WriteLine(
                    string.IsNullOrWhiteSpace(requestedTargetProject)
                        ? "[ERROR] The target project recorded by migration-report.json was not found. Specify --target-project after renaming it."
                        : "[ERROR] The selected target project was not found.");
                return 1;
            }
            if (!Path.GetExtension(targetProject).Equals(
                    ".csproj",
                    StringComparison.OrdinalIgnoreCase))
            {
                Console.Out.WriteLine(
                    "[ERROR] --target-project must select a supported .csproj target project.");
                return 1;
            }
            XDocument targetDocument;
            try
            {
                targetDocument = XDocument.Load(targetProject);
            }
            catch (Exception exception) when (
                exception is XmlException
                or IOException
                or UnauthorizedAccessException)
            {
                Console.Out.WriteLine(
                    $"[ERROR] The selected target project could not be read: {exception.Message}");
                return 1;
            }
            if (!MigrateCommand.Handler.IsSupportedMigrationTargetProject(
                    targetDocument))
            {
                Console.Out.WriteLine(
                    "[ERROR] --target-project must select a supported Microsoft.NET.Sdk target project.");
                return 1;
            }
            report.Target.ProjectFile = normalizedTargetProject;

            report.ActivationAnalysis = MigrateCommand.Handler.AnalyzeActivationContracts(
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
                cancellationToken);
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
            Console.Out.WriteLine(
                $"Target project graph: {verification.TargetProjectGraph.Status}; " +
                $"{verification.TargetProjectGraph.NestedProjects.Count} nested project(s); " +
                $"{verification.TargetProjectGraph.Issues.Count} issue(s).");
            return verification.Status == "passed" ? 0 : 1;
        }
    }
}
