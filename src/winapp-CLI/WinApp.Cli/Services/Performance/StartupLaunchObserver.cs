// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Performance;

internal sealed class StartupLaunchObserver(
    IPerformanceClock clock,
    IPackageProcessSnapshot packageProcesses,
    ITopLevelWindowProbe windowProbe,
    IProcessIdentityProbe processProbe,
    ISystemUiQuery systemUiQuery,
    PerformanceClockCalibration calibration,
    IWprCollector? wprCollector = null) : IRunLaunchObserver, IDisposable
{
    private string? _packageFamilyName;

    public PerformanceClockCalibration Calibration { get; } = calibration;

    public StartupObservationSession? Session { get; private set; }

    public int ActivationProcessId { get; private set; }

    public bool LaunchProcessAdmissionFailed { get; private set; }

    internal IWprCollector? WprCollector { get; set; } = wprCollector;

    public async Task BeforeLaunchAsync(
        string? packageFamilyName,
        CancellationToken cancellationToken)
    {
        if (Session is not null)
        {
            throw new InvalidOperationException("The observed run attempted more than one launch.");
        }

        _packageFamilyName = packageFamilyName;
        var baseline = packageFamilyName is null
            ? []
            : packageProcesses.Capture(packageFamilyName);
        if (WprCollector is not null)
        {
            await WprCollector.StartAsync(cancellationToken);
        }
        Session = new(
            clock,
            windowProbe,
            new TargetOwnership(processProbe, systemUiQuery),
            clock.GetTimestamp(),
            baseline);
    }

    public void AfterLaunch(uint processId)
    {
        if (Session is null)
        {
            throw new InvalidOperationException("The launch completed before its observation boundary began.");
        }
        if (processId > int.MaxValue)
        {
            throw new InvalidOperationException($"The launch returned unsupported process ID {processId}.");
        }

        ActivationProcessId = (int)processId;
        Observe();
    }

    public StartupObservationUpdate Observe()
    {
        if (Session is null)
        {
            throw new InvalidOperationException("The target has not been launched.");
        }

        var candidates = _packageFamilyName is null
            ? []
            : packageProcesses.Capture(_packageFamilyName);
        var update = Session.Observe(ActivationProcessId, candidates);
        LaunchProcessAdmissionFailed =
            !Session.HasObservedProcesses
            && update.ProcessFailures.ContainsKey(ActivationProcessId);
        return update;
    }

    public void Dispose() => Session?.Dispose();
}
