// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Text.Json;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;
using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Commands;

internal sealed class PerfRecordCommand : Command, IShortDescription
{
    internal static readonly Argument<FileSystemInfo> TargetArgument = new("target")
    {
        Description = "App to build, launch, and record: a .cs file-based app, project, solution, project directory, or build-output directory.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    internal static readonly Argument<string[]> PassthroughArgument = new("app-args")
    {
        Arity = ArgumentArity.ZeroOrMore,
        Hidden = true,
    };

    internal static readonly Option<string?> OutputOption = new("--output")
    {
        Description = "Evidence bundle directory (default: performance-<timestamp>.winappperf). The path must not already exist.",
    };

    internal static readonly Option<int> DurationOption = new("--duration-sec")
    {
        Description = "Stop after this many seconds. Use 0 to record until Ctrl+C or target exit (default: 0).",
        DefaultValueFactory = _ => 0,
    };

    internal static readonly Option<string> ConfigurationOption = new("--configuration", "-c")
    {
        Description = "Project and single-file mode: build configuration (default: Release).",
        DefaultValueFactory = _ => "Release",
    };

    internal static readonly Option<string?> ArchOption = new("--arch")
    {
        Description = "Project and single-file mode: target architecture (x64, arm64, or x86).",
    };

    internal static readonly Option<string?> RuntimeOption = new("--runtime", "-r")
    {
        Description = "Project mode: target .NET runtime identifier.",
    };

    internal static readonly Option<string?> FrameworkOption = new("--framework", "-f")
    {
        Description = "Project mode: target framework moniker.",
    };

    internal static readonly Option<string?> ProjectOption = new("--project")
    {
        Description = "Select a project when the target is a solution or ambiguous directory.",
    };

    internal static readonly Option<bool> NoBuildOption = new("--no-build")
    {
        Description = "Run existing build output without building.",
    };

    internal static readonly Option<bool> NoRestoreOption = new("--no-restore")
    {
        Description = "Skip restore before building.",
    };

    internal static readonly Option<string[]> PropertyOption = new("--property", "-p")
    {
        Description = "MSBuild property as Name=Value. Repeatable.",
        Arity = ArgumentArity.ZeroOrMore,
        AllowMultipleArgumentsPerToken = false,
    };

    internal static readonly Option<string?> ArgsOption = new("--args")
    {
        Description = "Arguments to pass to the app. Alternatively, place arguments after --.",
    };

    internal static readonly Option<bool> WithWprOption = new("--with-wpr")
    {
        Description = "Collect an elevated WinUI/XAML WPR trace and retain traces/system.etl.",
    };

    public string ShortDescription => "Record app startup, resources, managed EventPipe, and optional system traces";

    public PerfRecordCommand()
        : base("record", "Build and launch an app through winapp run, observe generation-safe startup and resource evidence, retain managed EventPipe evidence for newly launched CoreCLR targets, and optionally collect a WPR trace.")
    {
        Arguments.Add(TargetArgument);
        Arguments.Add(PassthroughArgument);
        Options.Add(OutputOption);
        Options.Add(DurationOption);
        Options.Add(ConfigurationOption);
        Options.Add(ArchOption);
        Options.Add(RuntimeOption);
        Options.Add(FrameworkOption);
        Options.Add(ProjectOption);
        Options.Add(NoBuildOption);
        Options.Add(NoRestoreOption);
        Options.Add(PropertyOption);
        Options.Add(ArgsOption);
        Options.Add(WithWprOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    internal sealed class Handler(
        IPerformanceRecordingSessionFactory recordingSessionFactory,
        IWprCollectorFactory wprCollectorFactory,
        IManagedDiagnosticsSessionFactory managedDiagnosticsFactory,
        IEtlLossInspector etlLossInspector,
        IXamlPerformanceAnalyzer xamlAnalyzer,
        IStorageSpaceProbe storageSpaceProbe,
        ICurrentDirectoryProvider currentDirectory,
        ILogger<PerfRecordCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var quiet = parseResult.GetValue(WinAppRootCommand.QuietOption);
            var liveOutput = new PerformanceLiveOutputPresenter(
                parseResult.GetValue(WinAppRootCommand.VerboseOption));
            if (RunCommand.Handler.HasValuelessProperty(parseResult, PropertyOption))
            {
                return Fail(
                    parseResult,
                    json,
                    "A --property/-p option was provided without a value. Expected Name=Value (for example: -p WindowsPackageType=None).");
            }
            var allAbsorbed = parseResult.GetValue(PassthroughArgument) ?? [];
            var (_, invalidPreDashTokens) = WindowsCommandLine.SplitPassthroughTokens(
                parseResult.Tokens,
                allAbsorbed);
            if (invalidPreDashTokens.Count > 0)
            {
                var invalid = invalidPreDashTokens.Count == 1
                    ? $"Unrecognized argument: '{invalidPreDashTokens[0]}'."
                    : $"Unrecognized arguments: {string.Join(", ", invalidPreDashTokens.Select(value => $"'{value}'"))}.";
                return Fail(parseResult, json, invalid);
            }
            var durationSec = parseResult.GetValue(DurationOption);
            if (durationSec < 0 || durationSec > 86_400)
            {
                return Fail(parseResult, json, "--duration-sec must be between 0 and 86400.");
            }

            var output = parseResult.GetValue(OutputOption);
            output = string.IsNullOrWhiteSpace(output)
                ? Path.Join(
                    currentDirectory.GetCurrentDirectory(),
                    $"performance-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.winappperf")
                : Path.GetFullPath(output, currentDirectory.GetCurrentDirectory());
            var withWpr = parseResult.GetValue(WithWprOption);
            if (withWpr
                && PerformanceRecordingSafety.Validate(
                    durationSec,
                    storageSpaceProbe.GetAvailableBytes(output)) is { } safetyError)
            {
                return Fail(parseResult, json, safetyError);
            }

            using var recording = recordingSessionFactory.Create(output);
            await using var managedDiagnostics = managedDiagnosticsFactory.Create(
                recording.CreateManagedPath());
            var wprResult = new WprCollectorResult
            {
                Requested = false,
                Status = "not-requested",
                Profile = XamlPerformanceAnalyzer.ProfileName,
                Coverage = "not-requested",
                LossStatus = "not-applicable",
            };
            IWprCollector? wprCollector = null;
            string? wprEtlPath = null;
            if (withWpr)
            {
                var availability = wprCollectorFactory.CheckAvailability();
                if (availability.IsAvailable)
                {
                    var paths = recording.CreateWprPaths();
                    wprEtlPath = paths.EtlPath;
                    wprCollector = wprCollectorFactory.Create(paths.EtlPath, paths.TemporaryDirectory);
                    recording.ConfigureWprCollector(wprCollector);
                    wprResult = wprCollector.Result;
                }
                else
                {
                    wprResult = new()
                    {
                        Requested = true,
                        Status = "unavailable",
                        Profile = XamlPerformanceAnalyzer.ProfileName,
                        Coverage = "unavailable",
                        QuotaBytes = PerformanceRecordingSafety.ArtifactQuotaBytes,
                        QuotaStatus = "not-produced",
                        LossStatus = "not-produced",
                        RecommendedViewer = "WPA",
                        Error = availability.Error,
                    };
                }
            }
            await using var wprLifetime = wprCollector;

            var nestedOutput = json
                ? TextWriter.Null
                : parseResult.InvocationConfiguration.Output;
            var nestedError = json
                ? TextWriter.Null
                : parseResult.InvocationConfiguration.Error;
            var launch = await recording.LaunchAsync(
                BuildRunArguments(parseResult),
                nestedOutput,
                nestedError,
                cancellationToken);
            await managedDiagnostics.ObserveAsync(
                recording.LastObservedEvents,
                recording.Disposition,
                cancellationToken);
            if (launch.ParseErrors.Count > 0)
            {
                return Fail(parseResult, json, string.Join(" ", launch.ParseErrors));
            }
            if (!launch.ObservationStarted)
            {
                var exitCode = launch.ExitCode == 0 ? 1 : launch.ExitCode;
                return Fail(
                    parseResult,
                    json,
                    $"The target launch failed before performance observation began (exit code {exitCode}).",
                    exitCode);
            }

            if (launch.ExitCode != 0)
            {
                if (wprCollector is not null)
                {
                    logger.LogInformation("Finalizing WPR trace...");
                    await wprCollector.StopAsync();
                    wprResult = wprCollector.Result;
                }
                wprResult = await InspectWprLossAsync(wprResult, wprEtlPath);
                await managedDiagnostics.StopAsync(recording.Disposition);
                var xaml = await xamlAnalyzer.AnalyzeAsync(
                    withWpr,
                    wprEtlPath,
                    recording.TargetProcess,
                    wprResult.LossStatus,
                    CancellationToken.None);
                var failedResult = recording.Complete(
                    "failed",
                    "launch-failed",
                    wprResult,
                    managedDiagnostics.Result,
                    xaml);
                WriteResult(parseResult, failedResult);
                return launch.ExitCode;
            }

            if (!json && !quiet)
            {
                PerformanceLiveOutputPresenter.WriteStart(
                    parseResult.InvocationConfiguration.Output,
                    recording.TimelineStartedUtc);
                liveOutput.Write(
                    parseResult.InvocationConfiguration.Output,
                    recording.LastWrittenEvents,
                    recording.TargetProcess);
            }

            string stopReason;
            string status;
            try
            {
                stopReason = await ObserveUntilStoppedAsync(
                    recording,
                    managedDiagnostics,
                    durationSec,
                    events =>
                    {
                        if (!json && !quiet)
                        {
                            liveOutput.Write(
                                parseResult.InvocationConfiguration.Output,
                                events,
                                recording.TargetProcess);
                        }
                    },
                    cancellationToken);
                status = recording.Disposition == StartupLaunchDisposition.Pending
                    ? "partial"
                    : recording.Disposition == StartupLaunchDisposition.AttachedLate
                        ? "attached-late"
                        : "completed";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                stopReason = "cancelled";
                status = "cancelled";
            }

            if (wprCollector is not null)
            {
                logger.LogInformation("Finalizing WPR trace...");
                await wprCollector.StopAsync();
                wprResult = wprCollector.Result;
            }
            wprResult = await InspectWprLossAsync(wprResult, wprEtlPath);
            await managedDiagnostics.StopAsync(recording.Disposition);
            var xamlResult = await xamlAnalyzer.AnalyzeAsync(
                withWpr,
                wprEtlPath,
                recording.TargetProcess,
                wprResult.LossStatus,
                CancellationToken.None);
            var requestedCollectorFailed =
                wprResult.Requested && wprResult.Status != "recorded";
            if ((status is "completed" or "attached-late")
                && requestedCollectorFailed)
            {
                status = "partial";
            }

            var result = recording.Complete(
                status,
                stopReason,
                wprResult,
                managedDiagnostics.Result,
                xamlResult);
            WriteResult(parseResult, result);

            return status == "cancelled"
                ? 130
                : requestedCollectorFailed ? 1 : 0;
        }

        private async Task<WprCollectorResult> InspectWprLossAsync(
            WprCollectorResult result,
            string? etlPath)
        {
            if (etlPath is null || result.Artifact is null)
            {
                return result;
            }

            var inspection = await etlLossInspector.InspectAsync(
                etlPath,
                CancellationToken.None);
            return result with
            {
                Status = inspection.Status == "detected"
                    ? "recorded-with-loss"
                    : result.Status,
                Coverage = inspection.Status switch
                {
                    "none" => "raw-etl-complete",
                    "detected" => "partial-event-loss",
                    _ => result.Coverage,
                },
                LossStatus = inspection.Status,
                LostBufferCount = inspection.LostBuffers,
                LostEventCount = inspection.LostEvents,
                LossInspectionToolVersion = inspection.ToolVersion,
                LossInspectionError = inspection.Error,
            };
        }

        private int Fail(
            ParseResult parseResult,
            bool json,
            string message,
            int exitCode = 1)
        {
            if (json)
            {
                parseResult.InvocationConfiguration.Output.WriteLine(JsonSerializer.Serialize(
                    new JsonErrorOutput { Error = message },
                    WinAppJsonContext.Default.JsonErrorOutput));
            }
            else
            {
                logger.LogError("{Message}", message);
            }
            return exitCode;
        }

        private static void WriteResult(ParseResult parseResult, PerformanceRecordResult result)
        {
            if (parseResult.GetValue(WinAppRootCommand.JsonOption))
            {
                parseResult.InvocationConfiguration.Output.WriteLine(JsonSerializer.Serialize(
                    result,
                    PerformanceJsonContext.Default.PerformanceRecordResult));
            }
            else
            {
                parseResult.InvocationConfiguration.Output.WriteLine(
                    $"Performance report: {result.Report.Recording.Status}");
                parseResult.InvocationConfiguration.Output.WriteLine(
                    $"Startup: {result.StartupDisposition}; {result.StartupSummary.Status} " +
                    $"[{result.ResponseProbe.CadenceMs:0} ms cadence, {result.ResponseProbe.TimeoutMs:0} ms timeout]");
                foreach (var stage in result.StartupSummary.Stages)
                {
                    parseResult.InvocationConfiguration.Output.WriteLine(
                        $"  {FormatStartupStage(stage)}");
                }
                if (result.StartupSummary.Outcome is not
                    ("responsive-observed" or "attached-late"))
                {
                    parseResult.InvocationConfiguration.Output.WriteLine(
                        FormatStartupOutcome(result.StartupSummary));
                    foreach (var processExit in result.StartupSummary.ProcessExits)
                    {
                        parseResult.InvocationConfiguration.Output.WriteLine(
                            $"  {FormatStartupProcessExit(processExit)}");
                    }
                }
                WriteResponsiveness(parseResult, result.Report.Responsiveness);
                parseResult.InvocationConfiguration.Output.WriteLine(
                    $"Resources: CPU avg {FormatCores(result.Resources.Summary.AverageCpuCoresUsed)}, " +
                    $"peak {FormatCores(result.Resources.Summary.PeakCpuCoresUsed)}; " +
                    $"private peak {FormatBytes(result.Resources.Summary.PeakPrivateBytes)}; " +
                    $"{result.Resources.SampleCount} samples");
                parseResult.InvocationConfiguration.Output.WriteLine(
                    $"Resource change: private {FormatByteChange(result.Resources.Summary.PrivateBytesChange)}; " +
                    $"I/O read {FormatIoBytes(result.Resources.Summary.ReadBytesDuringRecording)}, " +
                    $"write {FormatIoBytes(result.Resources.Summary.WriteBytesDuringRecording)}");
                if (result.Wpr.Requested)
                {
                    var artifact = result.Wpr.Artifact is null
                        ? string.Empty
                        : $"; {result.Wpr.Artifact} ({result.Wpr.FileSize} bytes)";
                    parseResult.InvocationConfiguration.Output.WriteLine(
                        $"WPR: {result.Wpr.Status}{artifact}");
                    if (result.Wpr.Error is not null)
                    {
                        parseResult.InvocationConfiguration.Output.WriteLine(
                            $"WPR detail: {result.Wpr.Error}");
                    }
                }
                WriteXamlAnalysis(parseResult, result.Xaml);
                WriteManagedCollector(parseResult, result.Managed);
                parseResult.InvocationConfiguration.Output.WriteLine(
                    $"Report: {Path.Join(result.Bundle, result.ReportPath)}");
                parseResult.InvocationConfiguration.Output.WriteLine($"Evidence: {result.Bundle}");
            }
        }

        private static void WriteResponsiveness(
            ParseResult parseResult,
            PerformanceResponsivenessSummary summary)
        {
            var detail = summary.FailureCount == 0
                ? "no response failures"
                : $"{summary.FailureCount} response failure(s), " +
                  $"{summary.RecoveryCount} recovered; longest observed " +
                  $"{FormatMilliseconds(summary.LongestObservedFailureMs)}";
            parseResult.InvocationConfiguration.Output.WriteLine(
                $"Responsiveness: {summary.Status}; {detail}");
            if (summary.ProbeErrorCount > 0)
            {
                parseResult.InvocationConfiguration.Output.WriteLine(
                    $"  Response probe errors: {summary.ProbeErrorCount}");
            }
        }

        private static void WriteXamlAnalysis(
            ParseResult parseResult,
            XamlAnalysisManifest xaml)
        {
            if (!xaml.Requested)
            {
                return;
            }

            parseResult.InvocationConfiguration.Output.WriteLine(
                $"XAML: {xaml.Status}; {xaml.MatchedIntervalCount} target intervals; coverage {xaml.Coverage}");
            if (xaml.Summary is { } summary)
            {
                WriteXamlMetric(parseResult, "Initialization", summary.InitializationMs);
                WriteXamlMetric(parseResult, "Longest interesting Frame", summary.LongestInterestingFrameMs);
                WriteXamlMetric(parseResult, "Longest interesting UpdateLayout", summary.LongestInterestingUpdateLayoutMs);
                if (summary.UiThreadId is { } uiThreadId)
                {
                    parseResult.InvocationConfiguration.Output.WriteLine($"  UI thread: {uiThreadId}");
                }
            }
            if (xaml.Error is not null)
            {
                parseResult.InvocationConfiguration.Output.WriteLine($"XAML detail: {xaml.Error}");
            }
            if (xaml.Remediation is not null)
            {
                parseResult.InvocationConfiguration.Output.WriteLine($"XAML action: {xaml.Remediation}");
            }
        }

        private static void WriteXamlMetric(
            ParseResult parseResult,
            string name,
            double? durationMs)
        {
            if (durationMs is not null)
            {
                parseResult.InvocationConfiguration.Output.WriteLine(
                    $"  {name}: {durationMs.Value:0.0} ms");
            }
        }

        private static void WriteManagedCollector(
            ParseResult parseResult,
            ManagedDiagnosticsResult collector)
        {
            var artifact = collector.Artifact is null
                ? string.Empty
                : $"; {collector.Artifact} ({collector.FileSize} bytes)";
            parseResult.InvocationConfiguration.Output.WriteLine(
                $"{collector.Collector}: {collector.Status}{artifact}");
            if (collector.Error is not null)
            {
                parseResult.InvocationConfiguration.Output.WriteLine(
                    $"{collector.Collector} detail: {collector.Error}");
            }
        }

        private static string FormatMilliseconds(double? value) =>
            value is null ? "not observed" : $"{value.Value:0.0} ms";

        private static string FormatCores(double? value) =>
            value is null ? "not observed" : $"{value.Value:0.00} cores";

        private static string FormatBytes(long? value) =>
            value is null ? "not observed" : $"{value.Value / (1024d * 1024d):0.0} MiB";

        private static string FormatIoBytes(ulong? value) => value switch
        {
            null => "not observed",
            < 1024 => $"{value.Value} bytes",
            < 1024 * 1024 => $"{value.Value / 1024d:0.0} KiB",
            _ => $"{value.Value / (1024d * 1024d):0.0} MiB",
        };

        private static string FormatByteChange(long? value) =>
            value is null ? "not observed" : $"{value.Value / (1024d * 1024d):+0.0;-0.0;0.0} MiB";

        internal static string FormatStartupStage(StartupStageSummary stage)
        {
            var resources = stage.Resources;
            var details = new List<string>
            {
                $"{FormatBoundary(stage.StartBoundary)} -> {FormatBoundary(stage.EndBoundary)}: " +
                $"{stage.DurationMs:0.0} ms",
            };
            if (resources.Status == "not-observed")
            {
                details.Add("resources not observed");
            }
            else
            {
                var sampleWindow = resources.StartSampleMs is { } sampleStart
                    && resources.EndSampleMs is { } sampleEnd
                    ? $" over {Math.Max(0, sampleEnd - sampleStart):0.0} ms sample window"
                    : string.Empty;
                details.Add($"CPU {FormatMilliseconds(resources.CpuTimeMs)}{sampleWindow}");
                details.Add($"private {FormatByteChange(resources.PrivateBytesChange)}");
                details.Add($"I/O read {FormatIoBytes(resources.ReadBytes)}, write {FormatIoBytes(resources.WriteBytes)}");
                if (resources.Status == "partial")
                {
                    details.Add("partial resource evidence");
                }
            }
            if (stage.ResponseFailureDurationMs > 0)
            {
                details.Add($"unresponsive {stage.ResponseFailureDurationMs:0.0} ms");
            }
            return string.Join("; ", details);
        }

        internal static string FormatStartupOutcome(PerformanceStartupSummary summary) =>
            $"Startup outcome: {FormatOutcome(summary.Outcome)}; last observed " +
            $"{FormatBoundary(summary.LastBoundary)} at {summary.LastBoundaryMs:0.0} ms; " +
            $"stop reason {summary.StopReason}";

        internal static string FormatStartupProcessExit(StartupProcessExit processExit)
        {
            var process = processExit.ProcessId is { } processId
                ? $"PID {processId}"
                : "unknown process";
            var code = processExit.ExitCode is { } exitCode
                ? $"{exitCode} ({processExit.ExitCodeHex})"
                : "unknown";
            var before = processExit.BeforeBoundary is { } boundary
                ? $"; before {FormatBoundary(boundary)}"
                : string.Empty;
            return $"Process exit: {process} at {processExit.ElapsedMs:0.0} ms; " +
                $"code {code}; {processExit.ExitKind} exit{before}";
        }

        private static string FormatOutcome(string outcome) => outcome switch
        {
            "activation-failed" => "activation failed",
            "process-not-observed" => "process not observed",
            "exited-before-responsive" => "process exited before Responsive",
            "recording-ended-before-responsive" => "recording ended before Responsive",
            _ => outcome,
        };

        private static string FormatBoundary(string boundary) => boundary switch
        {
            "activation" => "Activation",
            "process" => "Process",
            "first-window" => "First window",
            "visible" => "Visible",
            "responsive" => "Responsive",
            _ => boundary,
        };

        internal static async Task<string> ObserveUntilStoppedAsync(
            IPerformanceRecordingSession recording,
            IManagedDiagnosticsSession managedDiagnostics,
            int durationSec,
            Action<IReadOnlyList<PerformanceTimelineEntry>>? onEvents,
            CancellationToken cancellationToken)
        {
            var deadline = durationSec > 0
                ? DateTimeOffset.UtcNow.AddSeconds(durationSec)
                : DateTimeOffset.MaxValue;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var update = recording.Observe();
                await managedDiagnostics.ObserveAsync(
                    update.Events,
                    update.Disposition,
                    cancellationToken);
                onEvents?.Invoke(recording.LastWrittenEvents);
                if (recording.LaunchProcessAdmissionFailed
                    && !recording.HasObservedProcesses)
                {
                    recording.CaptureResourceSnapshot(force: true);
                    return "launch-process-not-observed";
                }
                if (recording.HasObservedProcesses && !recording.HasActiveProcesses)
                {
                    recording.Observe();
                    recording.CaptureResourceSnapshot(force: true);
                    return "target-exited";
                }
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    return "duration";
                }

                await Task.Delay(PerformanceRecordingSession.PollInterval, cancellationToken);
            }
        }

        private static string[] BuildRunArguments(ParseResult parseResult)
        {
            var arguments = new List<string>();
            if (parseResult.GetValue(TargetArgument) is { } target)
            {
                arguments.Add(target.FullName);
            }

            AddOption(arguments, "--configuration", parseResult.GetValue(ConfigurationOption));
            AddOption(arguments, "--arch", parseResult.GetValue(ArchOption));
            AddOption(arguments, "--runtime", parseResult.GetValue(RuntimeOption));
            AddOption(arguments, "--framework", parseResult.GetValue(FrameworkOption));
            AddOption(arguments, "--project", parseResult.GetValue(ProjectOption));
            AddOption(arguments, "--args", parseResult.GetValue(ArgsOption));
            if (parseResult.GetValue(NoBuildOption))
            {
                arguments.Add("--no-build");
            }
            if (parseResult.GetValue(NoRestoreOption))
            {
                arguments.Add("--no-restore");
            }
            foreach (var property in parseResult.GetValue(PropertyOption) ?? [])
            {
                AddOption(arguments, "--property", property);
            }

            arguments.Add("--detach");
            var passthrough = parseResult.GetValue(PassthroughArgument) ?? [];
            if (passthrough.Length > 0)
            {
                arguments.Add("--");
                arguments.AddRange(passthrough);
            }
            return [.. arguments];
        }

        private static void AddOption(List<string> arguments, string option, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                arguments.Add(option);
                arguments.Add(value);
            }
        }
    }
}
