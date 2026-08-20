// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using WinApp.Cli.Helpers;
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
        : base("verify", "Re-run namespace residual and project-item checks against the recorded migration inventory without modifying application source or behavioral validation.")
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
            var targetRoot = parseResult.GetValue(TargetArgument)!.FullName
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var reportPath = Path.Combine(targetRoot, "migration-report.json");
            if (!File.Exists(reportPath))
            {
                Console.Out.WriteLine("[ERROR] migration-report.json was not found in the target directory.");
                return 1;
            }

            MigrationReport? report;
            try
            {
                report = JsonSerializer.Deserialize(
                    await File.ReadAllTextAsync(reportPath, cancellationToken),
                    MigrateJsonContext.Default.MigrationReport);
            }
            catch (JsonException exception)
            {
                Console.Out.WriteLine($"[ERROR] migration-report.json is invalid: {exception.Message}");
                return 1;
            }

            if (report is null)
            {
                Console.Out.WriteLine("[ERROR] migration-report.json did not contain a migration report.");
                return 1;
            }

            var sourceRoot = Path.GetFullPath(report.Source.Root);
            if (!Directory.Exists(sourceRoot))
            {
                Console.Out.WriteLine($"[ERROR] Migration source directory no longer exists: {sourceRoot}");
                return 1;
            }

            var sourceProject = report.Source.ProjectFile is null
                ? null
                : Path.Combine(sourceRoot, report.Source.ProjectFile.Replace('/', Path.DirectorySeparatorChar));
            var targetProject = report.Target.ProjectFile is null
                ? null
                : Path.Combine(targetRoot, report.Target.ProjectFile.Replace('/', Path.DirectorySeparatorChar));
            if (targetProject is null || !File.Exists(targetProject))
            {
                Console.Out.WriteLine("[ERROR] The target project recorded by migration-report.json was not found.");
                return 1;
            }

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
            await File.WriteAllTextAsync(
                reportPath,
                JsonSerializer.Serialize(report, MigrateJsonContext.Default.MigrationReport),
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
            return verification.Status == "passed" ? 0 : 1;
        }
    }
}
