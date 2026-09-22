// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;

namespace WinApp.Cli.Services.Performance;

internal sealed record ManagedDiagnosticsResult
{
    public required string Collector { get; init; }
    public required string Status { get; init; }
    public required string Coverage { get; init; }
    public string? Artifact { get; init; }
    public long? FileSize { get; init; }
    public long? QuotaBytes { get; init; }
    public string? QuotaStatus { get; init; }
    public int? TargetProcessId { get; init; }
    public long? TargetProcessStartTimeUtcTicks { get; init; }
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? StopStartedUtc { get; init; }
    public DateTimeOffset? StopCompletedUtc { get; init; }
    public required string LossStatus { get; init; }
    public string? RecommendedViewer { get; init; }
    public string? Error { get; init; }
}

internal sealed record ManagedEventPipeProvider(
    string Name,
    EventLevel EventLevel,
    long Keywords,
    IReadOnlyDictionary<string, string> Arguments);

internal sealed record ManagedEventPipeConfiguration(
    IReadOnlyList<ManagedEventPipeProvider> Providers,
    int CircularBufferSizeInMegabytes,
    bool RequestRundown,
    bool RequestStackwalk);

internal interface IManagedEventPipeSession : IDisposable
{
    Stream EventStream { get; }

    Task StopAsync(CancellationToken cancellationToken);
}

internal interface IManagedEventPipeClient
{
    Task<IManagedEventPipeSession> StartSessionAsync(
        ProcessIdentity target,
        ManagedEventPipeConfiguration configuration,
        CancellationToken cancellationToken);
}

internal sealed class ManagedEventPipeClient : IManagedEventPipeClient
{
    public async Task<IManagedEventPipeSession> StartSessionAsync(
        ProcessIdentity target,
        ManagedEventPipeConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var providers = configuration.Providers
            .Select(provider => new EventPipeProvider(
                provider.Name,
                provider.EventLevel,
                provider.Keywords,
                provider.Arguments.ToDictionary()))
            .ToArray();
        var sessionConfiguration = new EventPipeSessionConfiguration(
            providers,
            configuration.CircularBufferSizeInMegabytes,
            configuration.RequestRundown,
            configuration.RequestStackwalk);
        var session = await new DiagnosticsClient(target.ProcessId)
            .StartEventPipeSessionAsync(sessionConfiguration, cancellationToken)
            .ConfigureAwait(false);
        return new ManagedEventPipeSession(session);
    }

    private sealed class ManagedEventPipeSession(EventPipeSession session) : IManagedEventPipeSession
    {
        public Stream EventStream => session.EventStream;

        public Task StopAsync(CancellationToken cancellationToken) =>
            session.StopAsync(cancellationToken);

        public void Dispose() => session.Dispose();
    }
}

internal enum ManagedProcessKind
{
    Managed,
    NotManaged,
    Unavailable,
}

internal readonly record struct ManagedProcessProbeResult(ManagedProcessKind Kind, string? Error = null);

internal interface IManagedProcessProbe
{
    ManagedProcessProbeResult Probe(ProcessIdentity process);
}

internal sealed class ManagedProcessProbe : IManagedProcessProbe
{
    public ManagedProcessProbeResult Probe(ProcessIdentity processIdentity)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processIdentity.ProcessId);
        }
        catch (ArgumentException)
        {
            return new(ManagedProcessKind.NotManaged);
        }

        using (process)
        {
            try
            {
                if (process.StartTime.ToUniversalTime().Ticks != processIdentity.StartTimeUtcTicks)
                {
                    return new(ManagedProcessKind.Unavailable, "The target PID was reused before managed-runtime inspection.");
                }

                foreach (ProcessModule module in process.Modules)
                {
                    if (module.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        return new(ManagedProcessKind.Managed);
                    }
                }
                return new(ManagedProcessKind.NotManaged);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                return new(ManagedProcessKind.Unavailable, ex.Message);
            }
        }
    }
}

internal interface IManagedDiagnosticsSession : IAsyncDisposable
{
    ManagedDiagnosticsResult Result { get; }

    Task ObserveAsync(
        IReadOnlyList<StartupEvent> events,
        StartupLaunchDisposition disposition,
        CancellationToken cancellationToken);

    Task StopAsync(StartupLaunchDisposition disposition);
}

internal interface IManagedDiagnosticsSessionFactory
{
    IManagedDiagnosticsSession Create(string tracePath);
}

internal sealed class ManagedDiagnosticsSessionFactory(
    IManagedProcessProbe managedProcessProbe,
    IManagedEventPipeClient eventPipeClient) : IManagedDiagnosticsSessionFactory
{
    public IManagedDiagnosticsSession Create(string tracePath) =>
        new ManagedDiagnosticsSession(managedProcessProbe, eventPipeClient, tracePath);
}

internal sealed class ManagedDiagnosticsSession : IManagedDiagnosticsSession
{
    internal const long ArtifactQuotaBytes = 1024L * 1024 * 1024;
    internal const int CircularBufferSizeInMegabytes = 64;
    internal static readonly TimeSpan AttachTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan StopGracePeriod = TimeSpan.FromSeconds(15);
    private const long DotNetRuntimeKeywords = 0x100003801D;

    private static readonly ManagedEventPipeConfiguration Configuration = new(
        [
            new(
                "Microsoft-DotNETCore-SampleProfiler",
                EventLevel.Informational,
                0,
                new Dictionary<string, string>()),
            new(
                "Microsoft-Windows-DotNETRuntime",
                EventLevel.Informational,
                DotNetRuntimeKeywords,
                new Dictionary<string, string>()),
            new(
                "System.Runtime",
                EventLevel.Informational,
                -1,
                new Dictionary<string, string>
                {
                    ["EventCounterIntervalSec"] = "1",
                }),
        ],
        CircularBufferSizeInMegabytes,
        RequestRundown: true,
        // SampleProfiler collects its own stacks. Avoid requesting stacks for every
        // System.Runtime counter event, which increases collection overhead substantially.
        RequestStackwalk: false);

    private readonly IManagedProcessProbe _managedProcessProbe;
    private readonly IManagedEventPipeClient _eventPipeClient;
    private readonly string _outputPath;
    private readonly long _artifactQuotaBytes;
    private readonly TimeSpan _stopGracePeriod;
    private readonly List<ProcessIdentity> _newProcesses = [];
    private readonly CancellationTokenSource _drainCancellation = new();
    private IManagedEventPipeSession? _eventPipeSession;
    private Task<ManagedEventPipeCopyResult>? _copyTask;
    private bool _sawNonManagedProcess;
    private string? _lastProbeError;
    private bool _stopped;

    public ManagedDiagnosticsSession(
        IManagedProcessProbe managedProcessProbe,
        IManagedEventPipeClient eventPipeClient,
        string outputPath,
        long artifactQuotaBytes = ArtifactQuotaBytes,
        TimeSpan? stopGracePeriod = null)
    {
        _managedProcessProbe = managedProcessProbe;
        _eventPipeClient = eventPipeClient;
        _outputPath = outputPath;
        _artifactQuotaBytes = artifactQuotaBytes;
        _stopGracePeriod = stopGracePeriod ?? StopGracePeriod;
        Result = CreateInitialResult(artifactQuotaBytes);
    }

    public ManagedDiagnosticsResult Result { get; private set; }

    internal static ManagedEventPipeConfiguration EventPipeConfiguration => Configuration;

    public async Task ObserveAsync(
        IReadOnlyList<StartupEvent> events,
        StartupLaunchDisposition disposition,
        CancellationToken cancellationToken)
    {
        if (_eventPipeSession is not null
            || _stopped
            || disposition == StartupLaunchDisposition.AttachedLate
            || Result.Status != "pending")
        {
            return;
        }

        foreach (var startupEvent in events.Where(
            value => value.Type == StartupEventType.ProcessObserved
                && value.Process is not null
                && !value.WasPresentBeforeActivation))
        {
            if (!_newProcesses.Contains(startupEvent.Process!.Value))
            {
                _newProcesses.Add(startupEvent.Process.Value);
            }
        }

        foreach (var process in _newProcesses)
        {
            var probe = _managedProcessProbe.Probe(process);
            if (probe.Kind == ManagedProcessKind.Managed)
            {
                await StartAsync(process, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (probe.Kind == ManagedProcessKind.NotManaged)
            {
                _sawNonManagedProcess = true;
            }
            else
            {
                _lastProbeError = probe.Error;
            }
        }
    }

    public async Task StopAsync(StartupLaunchDisposition disposition)
    {
        if (_stopped)
        {
            return;
        }
        _stopped = true;

        if (_eventPipeSession is null)
        {
            MarkNotAttached(disposition);
            return;
        }

        var stopStarted = DateTimeOffset.UtcNow;
        using var gracePeriod = new CancellationTokenSource(_stopGracePeriod);
        try
        {
            await _eventPipeSession.StopAsync(gracePeriod.Token).ConfigureAwait(false);
            var copy = await _copyTask!
                .WaitAsync(gracePeriod.Token)
                .ConfigureAwait(false);
            CompleteFromCopy(copy, "recorded", stopStarted);
            DisposeEventPipeSession();
        }
        catch (OperationCanceledException) when (gracePeriod.IsCancellationRequested)
        {
            await AbortCopyAsync().ConfigureAwait(false);
            CompleteFromCopy(
                await GetCopyResultAsync().ConfigureAwait(false),
                "stop-timeout",
                stopStarted,
                $"Managed EventPipe did not stop and drain within the {_stopGracePeriod.TotalSeconds:0}-second grace period.");
        }
        catch (Exception ex)
        {
            var targetExited = ex is ServerNotAvailableException;
            try
            {
                var copy = await _copyTask!
                    .WaitAsync(gracePeriod.Token)
                    .ConfigureAwait(false);
                CompleteFromCopy(
                    copy,
                    targetExited ? "recorded" : "stop-failed",
                    stopStarted,
                    targetExited ? null : ex.Message);
                DisposeEventPipeSession();
            }
            catch (OperationCanceledException) when (gracePeriod.IsCancellationRequested)
            {
                await AbortCopyAsync().ConfigureAwait(false);
                CompleteFromCopy(
                    await GetCopyResultAsync().ConfigureAwait(false),
                    "stop-timeout",
                    stopStarted,
                    targetExited
                        ? $"The target exited, but the managed EventPipe stream did not drain within the {_stopGracePeriod.TotalSeconds:0}-second grace period."
                        : $"Managed EventPipe stop failed ({ex.Message}) and the stream did not drain within the {_stopGracePeriod.TotalSeconds:0}-second grace period.");
            }
        }
    }

    private async Task StartAsync(ProcessIdentity target, CancellationToken cancellationToken)
    {
        Result = Result with
        {
            TargetProcessId = target.ProcessId,
            TargetProcessStartTimeUtcTicks = target.StartTimeUtcTicks,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(_outputPath)!);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AttachTimeout);
        try
        {
            _eventPipeSession = await _eventPipeClient
                .StartSessionAsync(target, Configuration, timeout.Token)
                .ConfigureAwait(false);
            _copyTask = CopyCappedAsync(
                _eventPipeSession.EventStream,
                _outputPath,
                _artifactQuotaBytes,
                _drainCancellation.Token);
            Result = Result with
            {
                Status = "recording",
                Coverage = "attached-after-activation",
                StartedUtc = DateTimeOffset.UtcNow,
            };
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested
            && timeout.IsCancellationRequested)
        {
            Result = Result with
            {
                Status = "start-failed",
                Coverage = "unavailable",
                Error = $"Managed EventPipe did not attach within {AttachTimeout.TotalSeconds:0} seconds.",
                LossStatus = "not-produced",
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or Win32Exception
            or TimeoutException
            or DiagnosticsClientException)
        {
            Result = Result with
            {
                Status = "start-failed",
                Coverage = "unavailable",
                Error = ex.Message,
                LossStatus = "not-produced",
            };
        }
    }

    private void MarkNotAttached(StartupLaunchDisposition disposition)
    {
        if (Result.Status != "pending")
        {
            return;
        }

        var (status, error) = disposition == StartupLaunchDisposition.AttachedLate
            ? (
                "attached-late",
                "Managed EventPipe does not attach to a process generation that existed before activation.")
            : _newProcesses.Count == 0
                ? (
                    "not-observed",
                    "No newly launched process generation was evidenced during the recording.")
                : !_sawNonManagedProcess && _lastProbeError is not null
                    ? (
                        "inspection-failed",
                        $"Managed-runtime inspection failed: {_lastProbeError}")
                    : (
                        "non-managed-target",
                        "No newly launched CoreCLR process was observed during the recording.");
        var candidate = _newProcesses.FirstOrDefault();
        Result = Result with
        {
            Status = status,
            Coverage = "not-attached",
            TargetProcessId = candidate.ProcessId == 0 ? null : candidate.ProcessId,
            TargetProcessStartTimeUtcTicks = candidate.ProcessId == 0
                ? null
                : candidate.StartTimeUtcTicks,
            Error = error,
            LossStatus = "not-produced",
        };
    }

    private async Task AbortCopyAsync()
    {
        _drainCancellation.Cancel();
        DisposeEventPipeSession();
        await GetCopyResultAsync().ConfigureAwait(false);
    }

    private async Task<ManagedEventPipeCopyResult> GetCopyResultAsync()
    {
        if (_copyTask is null)
        {
            return new(0, false, null);
        }
        try
        {
            return await _copyTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new(0, false, null);
        }
    }

    private void CompleteFromCopy(
        ManagedEventPipeCopyResult copy,
        string status,
        DateTimeOffset stopStarted,
        string? error = null)
    {
        var quotaDetail = copy.QuotaExceeded
            ? $"Managed EventPipe artifact reached its {Result.QuotaBytes} byte quota; additional bytes were discarded while draining."
            : null;
        var fileSize = File.Exists(_outputPath)
            ? new FileInfo(_outputPath).Length
            : 0;
        Result = Result with
        {
            Status = copy.QuotaExceeded && status == "recorded"
                ? "quota-exceeded"
                : copy.Error is not null && status == "recorded"
                    ? "stop-failed"
                    : status,
            Coverage = copy.QuotaExceeded
                ? "partial"
                : Result.Coverage,
            Artifact = File.Exists(_outputPath) ? "traces/managed.nettrace" : null,
            FileSize = File.Exists(_outputPath) ? fileSize : null,
            QuotaStatus = copy.QuotaExceeded ? "exceeded" : "within-limit",
            LossStatus = copy.QuotaExceeded
                ? "partial"
                : copy.Error is null && status == "recorded"
                    ? "not-inspected"
                    : "not-produced",
            StopStartedUtc = stopStarted,
            StopCompletedUtc = DateTimeOffset.UtcNow,
            Error = error is null
                ? copy.Error ?? quotaDetail
                : quotaDetail is null
                    ? error
                    : $"{error} {quotaDetail}",
        };
    }

    private void DisposeEventPipeSession()
    {
        _eventPipeSession?.Dispose();
        _eventPipeSession = null;
    }

    private static async Task<ManagedEventPipeCopyResult> CopyCappedAsync(
        Stream source,
        string outputPath,
        long artifactQuotaBytes,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(80 * 1024);
        long written = 0;
        var quotaExceeded = false;
        try
        {
            await using var destination = new FileStream(
                outputPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 80 * 1024,
                useAsync: true);
            while (true)
            {
                var count = await source
                    .ReadAsync(buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                var remaining = artifactQuotaBytes - written;
                var bytesToWrite = remaining <= 0
                    ? 0
                    : (int)Math.Min(remaining, count);
                if (bytesToWrite > 0)
                {
                    await destination
                        .WriteAsync(buffer.AsMemory(0, bytesToWrite), cancellationToken)
                        .ConfigureAwait(false);
                    written += bytesToWrite;
                }
                quotaExceeded |= bytesToWrite != count;
            }
            return new(written, quotaExceeded, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(written, quotaExceeded, null);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new(written, quotaExceeded, ex.Message);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static ManagedDiagnosticsResult CreateInitialResult(long artifactQuotaBytes) => new()
    {
        Collector = "Managed EventPipe",
        Status = "pending",
        Coverage = "not-attached",
        QuotaBytes = artifactQuotaBytes,
        QuotaStatus = "not-produced",
        LossStatus = "not-inspected",
        RecommendedViewer = "PerfView or Visual Studio",
    };

    public async ValueTask DisposeAsync()
    {
        if (!_stopped && _eventPipeSession is not null)
        {
            await StopAsync(StartupLaunchDisposition.Pending).ConfigureAwait(false);
        }
        _drainCancellation.Dispose();
    }

    private sealed record ManagedEventPipeCopyResult(
        long BytesWritten,
        bool QuotaExceeded,
        string? Error);
}
