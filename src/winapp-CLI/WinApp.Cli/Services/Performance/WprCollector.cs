// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Security.Principal;

namespace WinApp.Cli.Services.Performance;

internal sealed record WprAvailability(bool IsAvailable, string? Error = null);

internal sealed record WprCollectorResult
{
    public required bool Requested { get; init; }
    public required string Status { get; init; }
    public required string Profile { get; init; }
    public string? Artifact { get; init; }
    public long? FileSize { get; init; }
    public long? TotalArtifactBytes { get; init; }
    public long? QuotaBytes { get; init; }
    public string? QuotaStatus { get; init; }
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? StopStartedUtc { get; init; }
    public DateTimeOffset? StopCompletedUtc { get; init; }
    public bool? TemporaryFilesRetained { get; init; }
    public required string Coverage { get; init; }
    public string? LossStatus { get; init; }
    public int? LostBufferCount { get; init; }
    public int? LostEventCount { get; init; }
    public string? LossInspectionToolVersion { get; init; }
    public string? LossInspectionError { get; init; }
    public string? RecommendedViewer { get; init; }
    public string? Error { get; init; }
}

internal interface IWprCollector : IAsyncDisposable
{
    WprCollectorResult Result { get; }

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync();
}

internal interface IWprCollectorFactory
{
    WprAvailability CheckAvailability();

    IWprCollector Create(string etlPath, string temporaryDirectory);
}

internal sealed class WprCollectorFactory(IProcessRunner processRunner) : IWprCollectorFactory
{
    private const string Profile = XamlPerformanceAnalyzer.ProfileName;
    private readonly string _wprPath = Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "System32",
        "wpr.exe");

    public WprAvailability CheckAvailability()
    {
        if (!File.Exists(_wprPath))
        {
            return new(false, $"Windows Performance Recorder was not found at {_wprPath}.");
        }

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator)
            ? new(true)
            : new(false, "WPR system tracing requires an elevated terminal.");
    }

    public IWprCollector Create(string etlPath, string temporaryDirectory) =>
        new WprCollector(
            processRunner,
            _wprPath,
            etlPath,
            temporaryDirectory,
            $"winapp-perf-{Guid.NewGuid():N}");

    private sealed class WprCollector(
        IProcessRunner processRunner,
        string wprPath,
        string etlPath,
        string temporaryDirectory,
        string instanceName) : IWprCollector
    {
        private bool _started;

        public WprCollectorResult Result { get; private set; } = new()
        {
            Requested = true,
            Status = "pending",
            Profile = Profile,
            Coverage = "not-started",
            QuotaBytes = PerformanceRecordingSafety.ArtifactQuotaBytes,
            QuotaStatus = "not-produced",
            LossStatus = "not-inspected",
            RecommendedViewer = "WPA",
        };

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (Result.Status != "pending")
            {
                throw new InvalidOperationException("The WPR collector can only be started once.");
            }

            Directory.CreateDirectory(temporaryDirectory);
            var captureProfilePath = Path.Join(
                temporaryDirectory,
                WptXamlProfileResources.CaptureProfileResourceName);
            ProcessRunResult result;
            try
            {
                await using (var source = WptXamlProfileResources.OpenCaptureProfile())
                await using (var destination = new FileStream(
                    captureProfilePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                {
                    await source.CopyToAsync(destination, CancellationToken.None);
                }
                result = await processRunner.RunAsync(
                    new(
                        wprPath,
                        [
                            "-start",
                            $"{captureProfilePath}!{Profile}",
                            "-filemode",
                            "-recordtempto",
                            temporaryDirectory,
                            "-instancename",
                            instanceName,
                        ]),
                    cancellationToken: CancellationToken.None);
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
            {
                var temporaryFilesRetained = false;
                if (Directory.Exists(temporaryDirectory))
                {
                    try
                    {
                        Directory.Delete(temporaryDirectory, recursive: true);
                    }
                    catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
                    {
                        temporaryFilesRetained = true;
                    }
                }
                Result = Result with
                {
                    Status = "start-failed",
                    Coverage = "unavailable",
                    LossStatus = "not-produced",
                    TemporaryFilesRetained = temporaryFilesRetained ? true : null,
                    Error = ex.Message,
                };
                return;
            }
            finally
            {
                try
                {
                    File.Delete(captureProfilePath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The private WPR temp directory is removed after stop; preserve it rather
                    // than turning successful capture startup into a failure.
                }
            }

            if (result.ExitCode != 0)
            {
                if (Directory.Exists(temporaryDirectory))
                {
                    Directory.Delete(temporaryDirectory, recursive: true);
                }
                Result = Result with
                {
                    Status = "start-failed",
                    Coverage = "unavailable",
                    LossStatus = "not-produced",
                    Error = GetError(result),
                };
                return;
            }

            _started = true;
            Result = Result with
            {
                Status = "recording",
                Coverage = "collecting",
                StartedUtc = DateTimeOffset.UtcNow,
            };
            cancellationToken.ThrowIfCancellationRequested();
        }

        public async Task StopAsync()
        {
            if (!_started)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(etlPath)!);
            var stopStartedUtc = DateTimeOffset.UtcNow;
            ProcessRunResult result;
            try
            {
                // Stop intentionally ignores the caller's cancelled token: a soft stop retains the
                // owned trace, whereas killing wpr.exe can leave its ETW session running.
                result = await processRunner.RunAsync(
                    new(
                        wprPath,
                        [
                            "-stop",
                            etlPath,
                            "winapp perf record",
                            "-instancename",
                            instanceName,
                        ]),
                    cancellationToken: CancellationToken.None);
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
            {
                Result = Result with
                {
                    Status = "stop-failed",
                    Coverage = "partial-unmerged",
                    StopStartedUtc = stopStartedUtc,
                    StopCompletedUtc = DateTimeOffset.UtcNow,
                    Error = ex.Message,
                };
                return;
            }
            finally
            {
                _started = false;
            }

            if (result.ExitCode != 0 || !File.Exists(etlPath))
            {
                Result = Result with
                {
                    Status = "stop-failed",
                    Coverage = "partial-unmerged",
                    StopStartedUtc = stopStartedUtc,
                    StopCompletedUtc = DateTimeOffset.UtcNow,
                    Error = result.ExitCode == 0
                        ? "WPR reported success but did not create system.etl."
                        : GetError(result),
                };
                return;
            }

            var stopTemporaryFilesRetained = false;
            if (Directory.Exists(temporaryDirectory))
            {
                try
                {
                    Directory.Delete(temporaryDirectory, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    stopTemporaryFilesRetained = true;
                }
            }
            var fileSize = new FileInfo(etlPath).Length;
            var totalArtifactBytes = Directory
                .EnumerateFiles(Path.GetDirectoryName(etlPath)!, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length);
            Result = Result with
            {
                Status = totalArtifactBytes <= PerformanceRecordingSafety.ArtifactQuotaBytes
                    ? "recorded"
                    : "quota-exceeded",
                Coverage = "raw-etl-event-loss-not-inspected",
                Artifact = "traces/system.etl",
                FileSize = fileSize,
                TotalArtifactBytes = totalArtifactBytes,
                QuotaStatus = totalArtifactBytes <= PerformanceRecordingSafety.ArtifactQuotaBytes
                    ? "within-limit"
                    : "exceeded",
                StopStartedUtc = stopStartedUtc,
                StopCompletedUtc = DateTimeOffset.UtcNow,
                TemporaryFilesRetained = stopTemporaryFilesRetained ? true : null,
            };
        }

        public async ValueTask DisposeAsync() => await StopAsync();

        private static string GetError(ProcessRunResult result)
        {
            var message = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput
                : result.StandardError;
            message = message.Trim();
            return string.IsNullOrEmpty(message)
                ? $"wpr.exe exited with code {result.ExitCode}."
                : message.Length <= 1_000 ? message : message[..1_000];
        }
    }
}
