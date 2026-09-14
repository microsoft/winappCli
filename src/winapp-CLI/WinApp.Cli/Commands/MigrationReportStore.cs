// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Commands;

internal static class MigrationReportStore
{
    private static readonly HashSet<string> SupportedSchemaVersions =
        new(StringComparer.Ordinal)
        {
            "1.2",
            MigrationReport.CurrentSchemaVersion
        };

    internal static async Task<MigrationReport> LoadAsync(
        string reportPath,
        CancellationToken cancellationToken)
    {
        MigrationReport? report;
        try
        {
            report = JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(reportPath, cancellationToken),
                MigrateJsonContext.Default.MigrationReport);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"migration-report.json is invalid: {exception.Message}",
                exception);
        }

        if (report is null)
        {
            throw new InvalidDataException(
                "migration-report.json did not contain a migration report.");
        }
        if (!SupportedSchemaVersions.Contains(report.SchemaVersion))
        {
            throw new InvalidDataException(
                $"migration-report.json schemaVersion '{report.SchemaVersion}' is not supported. " +
                $"Supported versions: {string.Join(", ", SupportedSchemaVersions.Order())}.");
        }
        if (report.Source is null || report.Target is null)
        {
            throw new InvalidDataException(
                "migration-report.json must contain source and target project records.");
        }

        NormalizeForWrite(report);
        return report;
    }

    internal static async Task WriteAtomicAsync(
        string reportPath,
        MigrationReport report,
        CancellationToken cancellationToken)
    {
        NormalizeForWrite(report);
        var tempPath = Path.Combine(
            Path.GetDirectoryName(reportPath)!,
            $".{Path.GetFileName(reportPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                tempPath,
                JsonSerializer.Serialize(report, MigrateJsonContext.Default.MigrationReport),
                cancellationToken);
            File.Move(tempPath, reportPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    internal static void NormalizeForWrite(MigrationReport report)
    {
        report.SchemaVersion = MigrationReport.CurrentSchemaVersion;
        report.Summary ??= new MigrationSummary();
        report.Transforms ??= [];
        report.Todos ??= [];
        report.DependencyAnalysis ??= new MigrationDependencyAnalysis();
        report.ActivationAnalysis ??= new MigrationActivationAnalysis();
        report.ProjectItemDecisions ??= [];
        report.MechanicalVerification ??= new MigrationMechanicalVerification();
        report.MechanicalVerification.Inventory ??= new MigrationFileInventory();
        report.MechanicalVerification.ProjectItems ??= new MigrationProjectItemVerification();
        report.MechanicalVerification.ActivationContracts ??= new MigrationActivationVerification();
        report.MechanicalVerification.ProjectItems.ReviewRequiredItems ??= [];
        foreach (var decision in report.ProjectItemDecisions)
        {
            decision.EvidenceFiles ??= [];
            decision.Verification ??= new MigrationProjectItemDecisionVerification();
            decision.Verification.ProjectEvidence ??= [];
        }
        report.Validation ??= new MigrationValidation();
        report.Validation.SourceBaseline ??= new MigrationValidationPhase
        {
            EvidenceRoot = ".migration-evidence/source"
        };
        report.Validation.TargetReplay ??= new MigrationValidationPhase
        {
            EvidenceRoot = ".migration-evidence/target"
        };
    }
}
