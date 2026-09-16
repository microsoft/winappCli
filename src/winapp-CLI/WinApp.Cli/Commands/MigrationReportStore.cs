// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Commands;

internal static class MigrationReportStore
{
    private static readonly TimeSpan DefaultTransactionLockTimeout =
        TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TransactionLockRetryDelay =
        TimeSpan.FromMilliseconds(50);

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

    internal static Task<MigrationReportTransactionLock> AcquireTransactionLockAsync(
        string reportPath,
        CancellationToken cancellationToken) =>
        AcquireTransactionLockAsync(
            reportPath,
            DefaultTransactionLockTimeout,
            cancellationToken);

    internal static async Task<MigrationReportTransactionLock> AcquireTransactionLockAsync(
        string reportPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The migration report lock timeout must be positive.");
        }

        var lockPath = GetTransactionLockPath(reportPath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            throw new MigrationReportLockException(
                $"The migration report lock directory could not be created: {exception.Message}",
                exception);
        }

        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
                return new MigrationReportTransactionLock(
                    stream,
                    lockPath);
            }
            catch (IOException exception) when (
                IsLockContention(exception))
            {
                if (stopwatch.Elapsed >= timeout)
                {
                    throw new MigrationReportLockException(
                        $"Timed out after {timeout.TotalSeconds:0.###} seconds waiting for another winapp process to finish updating this migration report. Retry after the other migrate command completes.",
                        exception);
                }
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException)
            {
                throw new MigrationReportLockException(
                    $"The migration report lock could not be opened: {exception.Message}",
                    exception);
            }

            var remaining = timeout - stopwatch.Elapsed;
            var delay = remaining < TransactionLockRetryDelay
                ? remaining
                : TransactionLockRetryDelay;
            if (delay <= TimeSpan.Zero)
            {
                throw new MigrationReportLockException(
                    $"Timed out after {timeout.TotalSeconds:0.###} seconds waiting for another winapp process to finish updating this migration report. Retry after the other migrate command completes.");
            }
            await Task.Delay(delay, cancellationToken);
        }
    }

    internal static string GetTransactionLockPath(string reportPath)
    {
        var reportDirectory = Path.GetDirectoryName(
            Path.GetFullPath(reportPath))!;
        return Path.Combine(
            reportDirectory,
            ".migration-evidence",
            ".locks",
            $"{Path.GetFileName(reportPath)}.lock");
    }

    private static bool IsLockContention(IOException exception) =>
        (exception.HResult & 0xFFFF) is 32 or 33;

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
        report.MechanicalVerification.TargetProjectGraph ??= new MigrationTargetProjectGraphVerification();
        report.MechanicalVerification.ProjectItems.ReviewRequiredItems ??= [];
        report.MechanicalVerification.TargetProjectGraph.NestedProjects ??= [];
        report.MechanicalVerification.TargetProjectGraph.Issues ??= [];
        foreach (var issue in report.MechanicalVerification.TargetProjectGraph.Issues)
        {
            issue.ItemKinds ??= [];
            issue.SamplePaths ??= [];
            issue.GeneratedPaths ??= [];
        }
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

internal sealed class MigrationReportTransactionLock(
    FileStream stream,
    string lockPath) : IAsyncDisposable, IDisposable
{
    private FileStream? _stream = stream;

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
        CleanupLockFile();
    }

    public async ValueTask DisposeAsync()
    {
        var current = Interlocked.Exchange(
            ref _stream,
            null);
        if (current is null)
        {
            return;
        }

        await current.DisposeAsync();
        CleanupLockFile();
    }

    private void CleanupLockFile()
    {
        try
        {
            File.Delete(lockPath);
        }
        catch (IOException)
        {
            // The exclusive handle is already released. A stale zero-byte lock file is
            // safe because the next transaction reopens it and locks the handle again.
        }
        catch (UnauthorizedAccessException)
        {
            // The exclusive handle is already released. A stale zero-byte lock file is
            // safe because the next transaction reopens it and locks the handle again.
        }
    }
}

internal sealed class MigrationReportLockException(
    string message,
    Exception? innerException = null) : Exception(message, innerException);
