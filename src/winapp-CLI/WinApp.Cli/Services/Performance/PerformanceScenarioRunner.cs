// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Performance;

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;
using WinApp.Cli.Services.InteractiveDesktop;

internal interface IPerformanceScenarioRunner
{
    Task<PerformanceScenarioRunResult> RunAsync(
        PerformanceScenarioRunRequest request,
        CancellationToken cancellationToken);
}

internal interface IPerformanceScenarioDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class PerformanceScenarioDelay : IPerformanceScenarioDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

internal sealed class PerformanceScenarioRunner(
    IPerformanceRecordingSessionFactory recordingSessionFactory,
    IPerformanceUiCommandInvoker uiCommandInvoker,
    IUiWorkflowContext workflowContext,
    IUiActionBoundaryReporter actionReporter,
    IPerformanceClock clock,
    IPerformanceScenarioDelay delay) : IPerformanceScenarioRunner
{
        private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan TargetExitTimeout = TimeSpan.FromSeconds(30);

        public async Task<PerformanceScenarioRunResult> RunAsync(
            PerformanceScenarioRunRequest request,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(request.IterationRoot);
            var iterations = new List<PerformanceScenarioIteration>(
                request.Warmup + request.Repeat);
            try
            {
                for (var index = 0; index < request.Warmup + request.Repeat; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var iteration = await RunIterationAsync(
                        request,
                        index + 1,
                        index < request.Warmup,
                        cancellationToken);
                    iterations.Add(iteration);
                    if (iteration.Status != "completed")
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new()
                {
                    Status = "cancelled",
                    FailureReason = "cancelled",
                    Conditions = PerformanceSetWriter.CurrentConditions(),
                    Iterations = iterations,
                };
            }

            var expected = request.Warmup + request.Repeat;
            return new()
            {
                Status = iterations.Count == expected
                    && iterations.All(value => value.Status == "completed")
                    ? "completed"
                    : "failed",
                FailureReason = iterations.Count == expected
                    && iterations.All(value => value.Status == "completed")
                    ? null
                    : "iteration-failed",
                Conditions = PerformanceSetWriter.CurrentConditions(),
                Iterations = iterations,
            };
        }

        private async Task<PerformanceScenarioIteration> RunIterationAsync(
            PerformanceScenarioRunRequest request,
            int ordinal,
            bool isWarmup,
            CancellationToken cancellationToken)
        {
            var iterationStarted = clock.GetTimestamp();
            var bundle = Path.Join(
                request.IterationRoot,
                $"{ordinal:D3}-{(isWarmup ? "warmup" : "measure")}.winappperf");
            using var recording = recordingSessionFactory.Create(bundle);
            var steps = new List<PerformanceScenarioStepResult>(request.Scenario.Steps.Count);
            var launch = await recording.LaunchAsync(
                BuildRunArguments(request.Target, request.AdditionalRunArguments),
                TextWriter.Null,
                TextWriter.Null,
                cancellationToken);
            if (!launch.ObservationStarted)
            {
                return FailedIteration(
                    ordinal,
                    isWarmup,
                    iterationStarted,
                    "launch-failed",
                    launch.ExitCode,
                    steps);
            }
            if (launch.ExitCode != 0)
            {
                recording.Complete(
                    "failed",
                    "launch-failed",
                    NotRequestedWpr(),
                    NotCollectedManagedDiagnostics());
                return FailedIteration(
                    ordinal,
                    isWarmup,
                    iterationStarted,
                    "launch-failed",
                    launch.ExitCode,
                    steps,
                    bundle);
            }

            var target = await WaitForResponsiveTargetAsync(recording, cancellationToken);
            if (target is null)
            {
                var reason = recording.Disposition == StartupLaunchDisposition.AttachedLate
                    ? "attached-late"
                    : "responsive-window-not-observed";
                recording.CaptureResourceSnapshot(force: true);
                recording.Complete(
                    "failed",
                    reason,
                    NotRequestedWpr(),
                    NotCollectedManagedDiagnostics());
                return FailedIteration(
                    ordinal,
                    isWarmup,
                    iterationStarted,
                    reason,
                    recording.TargetExitCode,
                    steps,
                    bundle);
            }

            var workflowId = $"perf-scenario-{Guid.NewGuid():N}";
            var succeeded = true;
            double? measureStartMs = null;
            double? measureEndMs = null;
            using (workflowContext.Push(workflowId))
            {
                try
                {
                    recording.WriteScenarioMarker("setup", "phase-start");
                    succeeded = await RunPhaseAsync(
                        request,
                        recording,
                        uiCommandInvoker,
                        target.Value,
                        "setup",
                        steps,
                        continueAfterFailure: false,
                        cancellationToken);
                    recording.WriteScenarioMarker(
                        "setup",
                        "phase-end",
                        status: succeeded ? "completed" : "failed");

                    if (succeeded)
                    {
                        measureStartMs = recording.WriteScenarioMarker("measure", "phase-start");
                        recording.CaptureResourceSnapshot(force: true);
                        succeeded = await RunPhaseAsync(
                            request,
                            recording,
                            uiCommandInvoker,
                            target.Value,
                            "measure",
                            steps,
                            continueAfterFailure: false,
                            cancellationToken);
                        recording.CaptureResourceSnapshot(force: true);
                        measureEndMs = recording.WriteScenarioMarker(
                            "measure",
                            "phase-end",
                            status: succeeded ? "completed" : "failed");
                    }
                    else
                    {
                        AddSkippedPhase(request, "measure", steps);
                    }

                    recording.WriteScenarioMarker("cleanup", "phase-start");
                    var cleanupSucceeded = await RunPhaseAsync(
                        request,
                        recording,
                        uiCommandInvoker,
                        target.Value,
                        "cleanup",
                        steps,
                        continueAfterFailure: true,
                        cancellationToken);
                    recording.WriteScenarioMarker(
                        "cleanup",
                        "phase-end",
                        status: cleanupSucceeded ? "completed" : "failed");
                    succeeded &= cleanupSucceeded;
                }
                finally
                {
                    var yieldCode = await uiCommandInvoker.YieldAsync(CancellationToken.None);
                    recording.WriteScenarioMarker(
                        "lifecycle",
                        "yield",
                        status: yieldCode == 0 ? "completed" : "failed");
                    succeeded &= yieldCode == 0;
                }
            }

            var uiSucceeded = succeeded;
            var exited = await WaitForNewTargetExitAsync(recording, cancellationToken);
            succeeded &= exited;
            if (recording.TargetExitCode is not (null or 0))
            {
                succeeded = false;
            }
            recording.CaptureResourceSnapshot(force: true);
            recording.Complete(
                succeeded ? "completed" : "failed",
                exited ? "target-exited" : "target-exit-timeout",
                NotRequestedWpr(),
                NotCollectedManagedDiagnostics());

            IReadOnlyList<PerformanceMetricValue> metrics =
                succeeded && measureStartMs is { } measureStart && measureEndMs is { } measureEnd
                    ? PerformanceMetricExtractor.Extract(
                        bundle,
                        measureStart,
                        measureEnd,
                        request.Scenario.MetricDefinitions)
                    : [];
            var metricsAvailable = metrics.All(metric =>
                metric.Value is { } value && double.IsFinite(value));
            succeeded &= metricsAvailable;
            return new()
            {
                Ordinal = ordinal,
                IsWarmup = isWarmup,
                Status = succeeded ? "completed" : "failed",
                FailureReason = succeeded
                    ? null
                    : !uiSucceeded
                        ? "ui-step-failed"
                        : !exited
                            ? "target-exit-timeout"
                            : !metricsAvailable
                                ? "metric-unavailable"
                            : "target-exit-code",
                DurationMs = ElapsedMilliseconds(iterationStarted),
                ExitCode = recording.TargetExitCode,
                SourceBundle = bundle,
                Steps = steps,
                Metrics = metrics,
            };
        }

        private async Task<PerformanceWindowTarget?> WaitForResponsiveTargetAsync(
            IPerformanceRecordingSession recording,
            CancellationToken cancellationToken)
        {
            var started = clock.GetTimestamp();
            while (Elapsed(started) < StartupTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (recording.Disposition == StartupLaunchDisposition.AttachedLate
                    && !recording.HasNewlyLaunchedTarget)
                {
                    return null;
                }
                if (recording.ResponsiveWindow is { } target)
                {
                    return target;
                }
                if (recording.HasNewlyLaunchedTarget
                    && recording.HasNewlyLaunchedTargetExited)
                {
                    return null;
                }

                recording.Observe();
                await delay.DelayAsync(
                    PerformanceRecordingSession.PollInterval,
                    cancellationToken);
            }
            return null;
        }

        private async Task<bool> RunPhaseAsync(
            PerformanceScenarioRunRequest request,
            IPerformanceRecordingSession recording,
            IPerformanceUiCommandInvoker invoker,
            PerformanceWindowTarget target,
            string phase,
            List<PerformanceScenarioStepResult> results,
            bool continueAfterFailure,
            CancellationToken cancellationToken)
        {
            var definitions = DefinitionsForPhase(request.Scenario, phase);
            var succeeded = true;
            foreach (var (step, arguments) in definitions)
            {
                if (!succeeded && !continueAfterFailure)
                {
                    results.Add(Skipped(step));
                    continue;
                }

                var started = clock.GetTimestamp();
                recording.WriteScenarioMarker(
                    phase,
                    "step-start",
                    step.Ordinal,
                    step.Verb);
                using var actionScope = actionReporter.Push(boundary =>
                    recording.WriteUiActionBoundary(
                        phase,
                        step.Ordinal,
                        step.Verb,
                        boundary,
                        target));
                var invokeTask = invoker.InvokeAsync(
                    arguments,
                    target.WindowHandle,
                    cancellationToken);
                try
                {
                    while (!invokeTask.IsCompleted)
                    {
                        var pollTask = delay.DelayAsync(
                            PerformanceRecordingSession.PollInterval,
                            cancellationToken);
                        if (await Task.WhenAny(invokeTask, pollTask) == invokeTask)
                        {
                            break;
                        }
                        await pollTask;
                        recording.Observe();
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await invokeTask;
                    throw;
                }
                var exitCode = await invokeTask;
                recording.Observe();
                var status = exitCode == 0 ? "completed" : "failed";
                recording.WriteScenarioMarker(
                    phase,
                    "step-end",
                    step.Ordinal,
                    step.Verb,
                    status);
                results.Add(new()
                {
                    Ordinal = step.Ordinal,
                    Phase = phase,
                    Verb = step.Verb,
                    Status = status,
                    DurationMs = ElapsedMilliseconds(started),
                    ExitCode = exitCode,
                });
                succeeded &= exitCode == 0;
            }
            return succeeded;
        }

        private async Task<bool> WaitForNewTargetExitAsync(
            IPerformanceRecordingSession recording,
            CancellationToken cancellationToken)
        {
            if (recording.Disposition == StartupLaunchDisposition.AttachedLate
                || !recording.HasNewlyLaunchedTarget)
            {
                return false;
            }

            var started = clock.GetTimestamp();
            while (Elapsed(started) < TargetExitTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (recording.HasNewlyLaunchedTargetExited
                    && !recording.HasActiveProcesses)
                {
                    return true;
                }
                recording.Observe();
                await delay.DelayAsync(
                    PerformanceRecordingSession.PollInterval,
                    cancellationToken);
            }
            return recording.HasNewlyLaunchedTargetExited
                && !recording.HasActiveProcesses;
        }

        private static (PerformanceScenarioStep Step, IReadOnlyList<string> Arguments)[]
            DefinitionsForPhase(ValidatedPerformanceScenario scenario, string phase)
        {
            var definitions = phase switch
            {
                "setup" => scenario.Definition.Setup,
                "measure" => scenario.Definition.Measure,
                "cleanup" => scenario.Definition.Cleanup,
                _ => throw new ArgumentOutOfRangeException(nameof(phase)),
            };
            var steps = scenario.Steps.Where(value => value.Phase == phase).ToArray();
            return steps.Zip(
                definitions,
                (step, definition) => (step, definition.Ui)).ToArray();
        }

        private static void AddSkippedPhase(
            PerformanceScenarioRunRequest request,
            string phase,
            List<PerformanceScenarioStepResult> results)
        {
            foreach (var (step, _) in DefinitionsForPhase(request.Scenario, phase))
            {
                results.Add(Skipped(step));
            }
        }

        private static PerformanceScenarioStepResult Skipped(PerformanceScenarioStep step) => new()
        {
            Ordinal = step.Ordinal,
            Phase = step.Phase,
            Verb = step.Verb,
            Status = "skipped",
            DurationMs = 0,
        };

        private static List<string> BuildRunArguments(
            FileSystemInfo? target,
            IReadOnlyList<string>? additionalArguments)
        {
            var arguments = new List<string>();
            if (target is not null)
            {
                arguments.Add(target.FullName);
            }
            arguments.Add("--detach");
            if (additionalArguments is not null)
            {
                arguments.AddRange(additionalArguments);
            }
            return arguments;
        }

        private PerformanceScenarioIteration FailedIteration(
            int ordinal,
            bool isWarmup,
            PerformanceTimestamp started,
            string reason,
            int? exitCode,
            IReadOnlyList<PerformanceScenarioStepResult> steps,
            string? bundle = null) => new()
            {
                Ordinal = ordinal,
                IsWarmup = isWarmup,
                Status = "failed",
                FailureReason = reason,
                DurationMs = ElapsedMilliseconds(started),
                ExitCode = exitCode,
                SourceBundle = bundle,
                Steps = steps,
                Metrics = [],
            };

        private TimeSpan Elapsed(PerformanceTimestamp started) =>
            clock.GetTimestamp().ElapsedSince(started, clock.Frequency);

        private double ElapsedMilliseconds(PerformanceTimestamp started) =>
            Elapsed(started).TotalMilliseconds;

        private static WprCollectorResult NotRequestedWpr() => new()
        {
            Requested = false,
            Status = "not-requested",
            Profile = XamlPerformanceAnalyzer.ProfileName,
            Coverage = "not-requested",
            LossStatus = "not-applicable",
        };

        private static ManagedDiagnosticsResult NotCollectedManagedDiagnostics() => new()
        {
            Collector = "Managed EventPipe",
            Status = "not-collected",
            Coverage = "not-collected",
            LossStatus = "not-applicable",
        };
}
