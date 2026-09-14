// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;
using WinApp.Cli.Services;

namespace WinApp.Cli.Services.Performance;

internal enum ManagedDiagnosticTool
{
    DotNetTrace,
    DotNetCounters,
}

internal sealed record DiagnosticToolResolution(
    ManagedDiagnosticTool Tool,
    bool IsAvailable,
    string? ExecutablePath = null,
    string? Version = null,
    string? Error = null);

internal sealed record ManagedCollectorResult
{
    public required bool Requested { get; init; }
    public required string Tool { get; init; }
    public required string Status { get; init; }
    public required string Coverage { get; init; }
    public string? ToolVersion { get; init; }
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

internal sealed record ManagedCollectorsResult
{
    public required ManagedCollectorResult DotNetTrace { get; init; }
    public required ManagedCollectorResult DotNetCounters { get; init; }
}

internal interface IDiagnosticToolResolver
{
    Task<DiagnosticToolResolution> ResolveAsync(
        ManagedDiagnosticTool tool,
        CancellationToken cancellationToken);
}

internal sealed class DiagnosticToolResolver : IDiagnosticToolResolver
{
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(5);
    private readonly IProcessRunner _processRunner;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Func<string> _getUserProfile;

    public DiagnosticToolResolver(IProcessRunner processRunner)
        : this(
            processRunner,
            Environment.GetEnvironmentVariable,
            () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
    {
    }

    internal DiagnosticToolResolver(
        IProcessRunner processRunner,
        Func<string, string?> getEnvironmentVariable,
        Func<string> getUserProfile)
    {
        _processRunner = processRunner;
        _getEnvironmentVariable = getEnvironmentVariable;
        _getUserProfile = getUserProfile;
    }

    public async Task<DiagnosticToolResolution> ResolveAsync(
        ManagedDiagnosticTool tool,
        CancellationToken cancellationToken)
    {
        var executableName = tool switch
        {
            ManagedDiagnosticTool.DotNetTrace => "dotnet-trace.exe",
            ManagedDiagnosticTool.DotNetCounters => "dotnet-counters.exe",
            _ => throw new ArgumentOutOfRangeException(nameof(tool)),
        };
        var profile = _getUserProfile();
        var executablePath = SafeExecutableResolver.Resolve(
            executableName,
            _getEnvironmentVariable("PATH"),
            string.IsNullOrWhiteSpace(profile)
                ? []
                : [Path.Join(profile, ".dotnet", "tools")]);
        if (executablePath is null)
        {
            return new(
                tool,
                false,
                Error: $"{executableName} was not found on PATH or in %USERPROFILE%\\.dotnet\\tools. Install it explicitly, then retry.");
        }

        ProcessRunResult version;
        using var versionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        versionTimeout.CancelAfter(VersionTimeout);
        try
        {
            version = await _processRunner.RunAsync(
                new(executablePath, ["--version"]),
                cancellationToken: versionTimeout.Token);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested
            && versionTimeout.IsCancellationRequested)
        {
            return new(
                tool,
                false,
                executablePath,
                Error: $"{executableName} did not report its version within {VersionTimeout.TotalSeconds:0} seconds.");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return new(tool, false, executablePath, Error: ex.Message);
        }

        if (version.ExitCode != 0)
        {
            return new(
                tool,
                false,
                executablePath,
                Error: ManagedDiagnosticCollector.GetProcessError(version, executableName));
        }

        var value = version.StandardOutput.Trim();
        return new(
            tool,
            true,
            executablePath,
            string.IsNullOrEmpty(value) ? "unknown" : value.Split(['\r', '\n'])[0]);
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
                    if (module.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase)
                        || module.ModuleName.Equals("clr.dll", StringComparison.OrdinalIgnoreCase))
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
    ManagedCollectorsResult Result { get; }

    Task ObserveAsync(
        IReadOnlyList<StartupEvent> events,
        StartupLaunchDisposition disposition,
        CancellationToken cancellationToken);

    Task StopAsync(StartupLaunchDisposition disposition);
}

internal interface IManagedDiagnosticsSessionFactory
{
    Task<IManagedDiagnosticsSession> CreateAsync(
        bool withTrace,
        bool withCounters,
        string tracePath,
        string countersPath,
        int durationSeconds,
        CancellationToken cancellationToken);
}

internal sealed class ManagedDiagnosticsSessionFactory(
    IDiagnosticToolResolver resolver,
    IManagedProcessProbe managedProcessProbe,
    IOwnedToolProcessFactory processFactory) : IManagedDiagnosticsSessionFactory
{
    public async Task<IManagedDiagnosticsSession> CreateAsync(
        bool withTrace,
        bool withCounters,
        string tracePath,
        string countersPath,
        int durationSeconds,
        CancellationToken cancellationToken)
    {
        var traceResolution = withTrace
            ? await resolver.ResolveAsync(ManagedDiagnosticTool.DotNetTrace, cancellationToken)
            : new(ManagedDiagnosticTool.DotNetTrace, false);
        var countersResolution = withCounters
            ? await resolver.ResolveAsync(ManagedDiagnosticTool.DotNetCounters, cancellationToken)
            : new(ManagedDiagnosticTool.DotNetCounters, false);
        return new ManagedDiagnosticsSession(
            managedProcessProbe,
            processFactory,
            traceResolution,
            countersResolution,
            withTrace,
            withCounters,
            tracePath,
            countersPath,
            durationSeconds);
    }
}

internal sealed class ManagedDiagnosticsSession : IManagedDiagnosticsSession
{
    internal const long ArtifactQuotaBytes = 1024L * 1024 * 1024;
    private readonly IManagedProcessProbe _managedProcessProbe;
    private readonly ManagedDiagnosticCollector _trace;
    private readonly ManagedDiagnosticCollector _counters;
    private readonly List<ProcessIdentity> _newProcesses = [];
    private bool _sawNonManagedProcess;
    private string? _lastProbeError;
    private bool _attached;

    public ManagedDiagnosticsSession(
        IManagedProcessProbe managedProcessProbe,
        IOwnedToolProcessFactory processFactory,
        DiagnosticToolResolution traceResolution,
        DiagnosticToolResolution countersResolution,
        bool withTrace,
        bool withCounters,
        string tracePath,
        string countersPath,
        int durationSeconds)
    {
        _managedProcessProbe = managedProcessProbe;
        _trace = new(
            processFactory,
            traceResolution,
            withTrace,
            tracePath,
            "traces/managed.nettrace",
            durationSeconds);
        _counters = new(
            processFactory,
            countersResolution,
            withCounters,
            countersPath,
            "traces/managed-counters.json",
            durationSeconds);
    }

    public ManagedCollectorsResult Result => new()
    {
        DotNetTrace = _trace.Result,
        DotNetCounters = _counters.Result,
    };

    public async Task ObserveAsync(
        IReadOnlyList<StartupEvent> events,
        StartupLaunchDisposition disposition,
        CancellationToken cancellationToken)
    {
        if (_attached || disposition == StartupLaunchDisposition.AttachedLate)
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
                _attached = true;
                await Task.WhenAll(
                    _trace.StartAsync(process, cancellationToken),
                    _counters.StartAsync(process, cancellationToken));
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
        if (!_attached)
        {
            var status = disposition == StartupLaunchDisposition.AttachedLate
                ? "attached-late-not-supported"
                : _newProcesses.Count > 0 && !_sawNonManagedProcess && _lastProbeError is not null
                    ? "managed-inspection-failed"
                : _newProcesses.Count > 0
                    ? "non-managed-target"
                    : "target-not-observed";
            var error = disposition == StartupLaunchDisposition.AttachedLate
                ? "Managed diagnostics are not attached to a process generation that existed before activation."
                : _newProcesses.Count > 0 && !_sawNonManagedProcess && _lastProbeError is not null
                    ? $"Managed-runtime inspection failed: {_lastProbeError}"
                : _newProcesses.Count > 0
                    ? "No newly launched managed process was evidenced during the recording."
                    : "No newly launched process generation was evidenced during the recording.";
            ProcessIdentity? candidate = _newProcesses.Count > 0 ? _newProcesses[0] : null;
            _trace.MarkNotAttached(status, error, candidate);
            _counters.MarkNotAttached(status, error, candidate);
            return;
        }

        await Task.WhenAll(_trace.StopAsync(), _counters.StopAsync());
    }

    public async ValueTask DisposeAsync()
    {
        await _trace.DisposeAsync();
        await _counters.DisposeAsync();
    }
}

internal sealed class ManagedDiagnosticCollector : IAsyncDisposable
{
    private static readonly TimeSpan StopGracePeriod = TimeSpan.FromSeconds(15);
    private readonly IOwnedToolProcessFactory _processFactory;
    private readonly DiagnosticToolResolution _resolution;
    private readonly bool _requested;
    private readonly string _outputPath;
    private readonly string _artifactPath;
    private readonly int _durationSeconds;
    private IOwnedToolProcess? _process;

    public ManagedDiagnosticCollector(
        IOwnedToolProcessFactory processFactory,
        DiagnosticToolResolution resolution,
        bool requested,
        string outputPath,
        string artifactPath,
        int durationSeconds)
    {
        _processFactory = processFactory;
        _resolution = resolution;
        _requested = requested;
        _outputPath = outputPath;
        _artifactPath = artifactPath;
        _durationSeconds = durationSeconds;
        Result = CreateInitialResult();
    }

    public ManagedCollectorResult Result { get; private set; }

    public async Task StartAsync(ProcessIdentity target, CancellationToken cancellationToken)
    {
        if (!_requested || _process is not null)
        {
            return;
        }

        Result = Result with
        {
            TargetProcessId = target.ProcessId,
            TargetProcessStartTimeUtcTicks = target.StartTimeUtcTicks,
        };
        if (!_resolution.IsAvailable)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_outputPath)!);
        try
        {
            _process = _processFactory.Start(new(
                _resolution.ExecutablePath!,
                CreateArguments(_resolution.Tool, target.ProcessId, _outputPath, _durationSeconds)));
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            Result = Result with
            {
                Status = "start-failed",
                Coverage = "unavailable",
                TargetProcessId = target.ProcessId,
                TargetProcessStartTimeUtcTicks = target.StartTimeUtcTicks,
                Error = ex.Message,
                LossStatus = "not-produced",
            };
            return;
        }

        Result = Result with
        {
            Status = "recording",
            Coverage = "attached-after-activation",
            TargetProcessId = target.ProcessId,
            TargetProcessStartTimeUtcTicks = target.StartTimeUtcTicks,
            StartedUtc = DateTimeOffset.UtcNow,
        };

        var completed = await Task.WhenAny(
            _process.Completion,
            Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken));
        if (completed == _process.Completion)
        {
            await FinishAsync(await _process.Completion, startPhase: true);
        }
    }

    public void MarkNotAttached(string status, string error, ProcessIdentity? candidate)
    {
        if (!_requested || _process is not null)
        {
            return;
        }

        Result = Result with
        {
            Status = _resolution.IsAvailable ? status : Result.Status,
            Coverage = _resolution.IsAvailable ? "not-attached" : Result.Coverage,
            TargetProcessId = candidate?.ProcessId,
            TargetProcessStartTimeUtcTicks = candidate?.StartTimeUtcTicks,
            Error = _resolution.IsAvailable ? error : Result.Error,
        };
    }

    public async Task StopAsync()
    {
        if (_process is null || Result.Status != "recording")
        {
            return;
        }

        var stopStarted = DateTimeOffset.UtcNow;
        var input = _resolution.Tool == ManagedDiagnosticTool.DotNetTrace ? Environment.NewLine : "q";
        var naturalStopUtc = Result.StartedUtc!.Value.AddSeconds(_durationSeconds);
        var softStopTimeout = naturalStopUtc > stopStarted
            ? naturalStopUtc - stopStarted + StopGracePeriod
            : StopGracePeriod;
        var stopped = await _process.RequestSoftStopAsync(input, softStopTimeout);
        if (!stopped)
        {
            Result = Result with
            {
                Status = "stop-timeout",
                StopStartedUtc = stopStarted,
                StopCompletedUtc = DateTimeOffset.UtcNow,
                Error = $"The collector did not exit by its bounded duration plus the {StopGracePeriod.TotalSeconds:0}-second stop grace period.",
            };
            CaptureArtifact();
            await _process.DisposeAsync();
            return;
        }

        await FinishAsync(await _process.Completion, startPhase: false, stopStarted);
    }

    private async Task FinishAsync(
        ProcessRunResult processResult,
        bool startPhase,
        DateTimeOffset? stopStarted = null)
    {
        if (processResult.ExitCode != 0)
        {
            Result = Result with
            {
                Status = startPhase ? "start-failed" : "stop-failed",
                Coverage = startPhase ? "unavailable" : Result.Coverage,
                StopStartedUtc = stopStarted,
                StopCompletedUtc = startPhase ? null : DateTimeOffset.UtcNow,
                Error = GetProcessError(
                    processResult,
                    Path.GetFileName(_resolution.ExecutablePath) ?? Result.Tool),
                LossStatus = File.Exists(_outputPath) ? Result.LossStatus : "not-produced",
            };
            CaptureArtifact();
            return;
        }

        if (!File.Exists(_outputPath))
        {
            Result = Result with
            {
                Status = startPhase ? "start-failed" : "stop-failed",
                Coverage = startPhase ? "unavailable" : Result.Coverage,
                StopStartedUtc = stopStarted,
                StopCompletedUtc = startPhase ? null : DateTimeOffset.UtcNow,
                Error = $"{Path.GetFileName(_resolution.ExecutablePath)} exited successfully but did not create {_artifactPath}.",
                LossStatus = "not-produced",
            };
            return;
        }

        Result = Result with
        {
            Status = "recorded",
            StopStartedUtc = stopStarted,
            StopCompletedUtc = DateTimeOffset.UtcNow,
        };
        CaptureArtifact();
        await Task.CompletedTask;
    }

    private void CaptureArtifact()
    {
        if (!File.Exists(_outputPath))
        {
            return;
        }

        var size = new FileInfo(_outputPath).Length;
        Result = Result with
        {
            Artifact = _artifactPath,
            FileSize = size,
            QuotaStatus = size <= ManagedDiagnosticsSession.ArtifactQuotaBytes
                ? "within-limit"
                : "exceeded",
            Status = size <= ManagedDiagnosticsSession.ArtifactQuotaBytes
                ? Result.Status
                : "quota-exceeded",
        };
    }

    private ManagedCollectorResult CreateInitialResult()
    {
        var toolName = _resolution.Tool == ManagedDiagnosticTool.DotNetTrace
            ? "dotnet-trace"
            : "dotnet-counters";
        if (!_requested)
        {
            return new()
            {
                Requested = false,
                Tool = toolName,
                Status = "not-requested",
                Coverage = "not-requested",
                LossStatus = "not-applicable",
            };
        }
        if (!_resolution.IsAvailable)
        {
            return new()
            {
                Requested = true,
                Tool = toolName,
                Status = "unavailable",
                Coverage = "unavailable",
                QuotaBytes = ManagedDiagnosticsSession.ArtifactQuotaBytes,
                QuotaStatus = "not-produced",
                LossStatus = "not-produced",
                Error = _resolution.Error,
            };
        }

        return new()
        {
            Requested = true,
            Tool = toolName,
            Status = "pending",
            Coverage = "not-attached",
            ToolVersion = _resolution.Version,
            QuotaBytes = ManagedDiagnosticsSession.ArtifactQuotaBytes,
            QuotaStatus = "not-produced",
            LossStatus = "not-inspected",
            RecommendedViewer = _resolution.Tool == ManagedDiagnosticTool.DotNetTrace
                ? "PerfView or Visual Studio"
                : "JSON viewer",
        };
    }

    private static IReadOnlyList<string> CreateArguments(
        ManagedDiagnosticTool tool,
        int processId,
        string outputPath,
        int durationSeconds)
    {
        var duration = TimeSpan.FromSeconds(durationSeconds).ToString(@"dd\:hh\:mm\:ss");
        return tool switch
        {
            ManagedDiagnosticTool.DotNetTrace =>
            [
                "collect",
                "--process-id", processId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--output", outputPath,
                "--format", "NetTrace",
                "--duration", duration,
            ],
            ManagedDiagnosticTool.DotNetCounters =>
            [
                "collect",
                "--process-id", processId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--output", outputPath,
                "--format", "json",
                "--refresh-interval", "1",
                "--duration", duration,
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(tool)),
        };
    }

    internal static string GetProcessError(ProcessRunResult result, string toolName)
    {
        var message = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        message = message.Trim();
        return string.IsNullOrEmpty(message)
            ? $"{toolName} exited with code {result.ExitCode}."
            : message.Length <= 1_000 ? message : message[..1_000];
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is not null)
        {
            await _process.DisposeAsync();
        }
    }
}
