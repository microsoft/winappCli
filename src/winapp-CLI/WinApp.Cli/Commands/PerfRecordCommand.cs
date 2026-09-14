// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Text.Json;
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
        Description = "Stop after this many seconds. Use 0 to record until Enter, Ctrl+C, redirected-input completion, or target exit (default: 0).",
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
        Description = "Collect an elevated WPR FileIO/Loader trace as traces/system.etl for analysis in WPA.",
    };

    internal static readonly Option<bool> WithDotNetTraceOption = new("--with-dotnet-trace")
    {
        Description = "Attach dotnet-trace after a newly launched managed process is evidenced and retain traces/managed.nettrace.",
    };

    internal static readonly Option<bool> WithDotNetCountersOption = new("--with-dotnet-counters")
    {
        Description = "Attach dotnet-counters after a newly launched managed process is evidenced and retain traces/managed-counters.json.",
    };

    public string ShortDescription => "Record app startup, resources, and optional system or managed traces";

    public PerfRecordCommand()
        : base("record", "Build and launch an app through winapp run, observe generation-safe startup and resource evidence, and optionally retain original WPR and .NET diagnostic artifacts.")
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
        Options.Add(WithDotNetTraceOption);
        Options.Add(WithDotNetCountersOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    internal sealed class Handler(
        RunCommand runCommand,
        RunCommand.Handler runHandler,
        IPerformanceClock clock,
        IPackageProcessSnapshot packageProcesses,
        ITopLevelWindowProbe windowProbe,
        IWindowResponseProbe responseProbe,
        IProcessIdentityProbe processProbe,
        ISystemUiQuery systemUiQuery,
        IWprCollectorFactory wprCollectorFactory,
        IManagedDiagnosticsSessionFactory managedDiagnosticsFactory,
        IStorageSpaceProbe storageSpaceProbe,
        ICurrentDirectoryProvider currentDirectory,
        ILogger<PerfRecordCommand> logger) : AsynchronousCommandLineAction
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan ResourceCadence = TimeSpan.FromMilliseconds(500);

        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var durationSec = parseResult.GetValue(DurationOption);
            if (durationSec < 0 || durationSec > 86_400)
            {
                logger.LogError("--duration-sec must be between 0 and 86400.");
                return 1;
            }

            var output = parseResult.GetValue(OutputOption);
            output = string.IsNullOrWhiteSpace(output)
                ? Path.Join(
                    currentDirectory.GetCurrentDirectory(),
                    $"performance-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.winappperf")
                : Path.GetFullPath(output, currentDirectory.GetCurrentDirectory());
            var withWpr = parseResult.GetValue(WithWprOption);
            var withDotNetTrace = parseResult.GetValue(WithDotNetTraceOption);
            var withDotNetCounters = parseResult.GetValue(WithDotNetCountersOption);
            var withDeepDiagnostics = withWpr || withDotNetTrace || withDotNetCounters;
            if (withDeepDiagnostics
                && PerformanceRecordingSafety.Validate(
                    durationSec,
                    storageSpaceProbe.GetAvailableBytes(output)) is { } safetyError)
            {
                logger.LogError("{Message}", safetyError);
                return 1;
            }

            var calibration = clock.Calibrate();
            using var writer = new PerformanceBundleWriter(output, calibration);
            var managedPaths = writer.CreateManagedPaths();
            await using var managedDiagnostics = await managedDiagnosticsFactory.CreateAsync(
                withDotNetTrace,
                withDotNetCounters,
                managedPaths.TracePath,
                managedPaths.CountersPath,
                durationSec,
                cancellationToken);
            var wprResult = new WprCollectorResult
            {
                Requested = false,
                Status = "not-requested",
                Profile = "FileIO.Verbose",
                Coverage = "not-requested",
                LossStatus = "not-applicable",
            };
            IWprCollector? wprCollector = null;
            if (withWpr)
            {
                var availability = wprCollectorFactory.CheckAvailability();
                if (availability.IsAvailable)
                {
                    var paths = writer.CreateWprPaths();
                    wprCollector = wprCollectorFactory.Create(paths.EtlPath, paths.TemporaryDirectory);
                    wprResult = wprCollector.Result;
                }
                else
                {
                    wprResult = new()
                    {
                        Requested = true,
                        Status = "unavailable",
                        Profile = "FileIO.Verbose",
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

            using var observer = new StartupLaunchObserver(
                clock,
                packageProcesses,
                windowProbe,
                processProbe,
                systemUiQuery,
                calibration,
                wprCollector);

            var runParseResult = runCommand.Parse(BuildRunArguments(parseResult));
            if (runParseResult.Errors.Count > 0)
            {
                foreach (var error in runParseResult.Errors)
                {
                    logger.LogError("{Message}", error.Message);
                }
                return 1;
            }
            runParseResult.InvocationConfiguration.Output = parseResult.InvocationConfiguration.Output;
            runParseResult.InvocationConfiguration.Error = parseResult.InvocationConfiguration.Error;

            var launchResult = await runHandler.InvokeForObservationAsync(
                runParseResult,
                observer,
                cancellationToken);
            if (observer.Session is null)
            {
                return launchResult == 0 ? 1 : launchResult;
            }

            writer.Write(observer.Session.Events);
            var resourceSampler = new ResourceSampler(
                clock,
                ResourceCadence,
                Environment.ProcessorCount);
            writer.Write(resourceSampler.TrySample(observer.Session.CaptureResourceCounters()));
            if (launchResult != 0)
            {
                if (wprCollector is not null)
                {
                    logger.LogInformation("Finalizing WPR trace...");
                    await wprCollector.StopAsync();
                    wprResult = wprCollector.Result;
                }
                await managedDiagnostics.StopAsync(observer.Session.Disposition);
                var failedResult = writer.Complete(
                    "failed",
                    "launch-failed",
                    observer.Session.Disposition,
                    observer.ActivationProcessId,
                    CreateResponseProbeManifest(responseProbe),
                    wprResult,
                    managedDiagnostics.Result);
                WriteResult(parseResult, failedResult);
                return launchResult;
            }

            string stopReason;
            string status;
            try
            {
                stopReason = await ObserveUntilStoppedAsync(
                    observer,
                    writer,
                    resourceSampler,
                    managedDiagnostics,
                    durationSec,
                    cancellationToken);
                status = observer.Session.Disposition == StartupLaunchDisposition.Pending
                    ? "partial"
                    : observer.Session.Disposition == StartupLaunchDisposition.AttachedLate
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
            await managedDiagnostics.StopAsync(observer.Session.Disposition);
            if ((status is "completed" or "attached-late")
                && wprResult.Requested
                && wprResult.Status != "recorded")
            {
                status = "partial";
            }
            if ((status is "completed" or "attached-late")
                && RequestedManagedCollectorFailed(managedDiagnostics.Result))
            {
                status = "partial";
            }

            var result = writer.Complete(
                status,
                stopReason,
                observer.Session.Disposition,
                observer.ActivationProcessId,
                CreateResponseProbeManifest(responseProbe),
                wprResult,
                managedDiagnostics.Result);
            WriteResult(parseResult, result);

            return status == "cancelled" ? 130 : 0;
        }

        private static ResponseProbeManifest CreateResponseProbeManifest(
            IWindowResponseProbe responseProbe) => new()
            {
                CadenceMs = PollInterval.TotalMilliseconds,
                TimeoutMs = responseProbe.Timeout.TotalMilliseconds,
                Method = "SendMessageTimeout(WM_NULL)",
            };

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
                    $"Performance recording: {result.Status}");
                parseResult.InvocationConfiguration.Output.WriteLine(
                    $"Startup: {result.StartupDisposition}; process {FormatMilliseconds(result.Startup.FirstProcessMs)}; " +
                    $"visible {FormatMilliseconds(result.Startup.FirstVisibleWindowMs)}; " +
                    $"responsive {FormatMilliseconds(result.Startup.FirstResponsiveWindowMs)} " +
                    $"[{result.ResponseProbe.CadenceMs:0} ms cadence, {result.ResponseProbe.TimeoutMs:0} ms timeout]");
                parseResult.InvocationConfiguration.Output.WriteLine(
                    $"Resources: CPU avg {FormatCores(result.Resources.Summary.AverageCpuCoresUsed)}, " +
                    $"peak {FormatCores(result.Resources.Summary.PeakCpuCoresUsed)}; " +
                    $"private peak {FormatBytes(result.Resources.Summary.PeakPrivateBytes)}; " +
                    $"{result.Resources.SampleCount} samples");
                parseResult.InvocationConfiguration.Output.WriteLine(
                    $"Resource change: private {FormatByteChange(result.Resources.Summary.PrivateBytesChange)}; " +
                    $"I/O read {FormatBytes(result.Resources.Summary.ReadBytesDuringRecording)}, " +
                    $"write {FormatBytes(result.Resources.Summary.WriteBytesDuringRecording)}");
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
                WriteManagedCollector(parseResult, result.Managed.DotNetTrace);
                WriteManagedCollector(parseResult, result.Managed.DotNetCounters);
                parseResult.InvocationConfiguration.Output.WriteLine($"Evidence: {result.Bundle}");
            }
        }

        private static void WriteManagedCollector(
            ParseResult parseResult,
            ManagedCollectorResult collector)
        {
            if (!collector.Requested)
            {
                return;
            }

            var artifact = collector.Artifact is null
                ? string.Empty
                : $"; {collector.Artifact} ({collector.FileSize} bytes)";
            parseResult.InvocationConfiguration.Output.WriteLine(
                $"{collector.Tool}: {collector.Status}{artifact}");
            if (collector.Error is not null)
            {
                parseResult.InvocationConfiguration.Output.WriteLine(
                    $"{collector.Tool} detail: {collector.Error}");
            }
        }

        private static bool RequestedManagedCollectorFailed(ManagedCollectorsResult result) =>
            (result.DotNetTrace.Requested && result.DotNetTrace.Status != "recorded")
            || (result.DotNetCounters.Requested && result.DotNetCounters.Status != "recorded");

        private static string FormatMilliseconds(double? value) =>
            value is null ? "not observed" : $"{value.Value:0.0} ms";

        private static string FormatCores(double? value) =>
            value is null ? "not observed" : $"{value.Value:0.00} cores";

        private static string FormatBytes(long? value) =>
            value is null ? "not observed" : $"{value.Value / (1024d * 1024d):0.0} MiB";

        private static string FormatBytes(ulong? value) =>
            value is null ? "not observed" : $"{value.Value / (1024d * 1024d):0.0} MiB";

        private static string FormatByteChange(long? value) =>
            value is null ? "not observed" : $"{value.Value / (1024d * 1024d):+0.0;-0.0;0.0} MiB";

        private static async Task<string> ObserveUntilStoppedAsync(
            StartupLaunchObserver observer,
            PerformanceBundleWriter writer,
            ResourceSampler resourceSampler,
            IManagedDiagnosticsSession managedDiagnostics,
            int durationSec,
            CancellationToken cancellationToken)
        {
            var deadline = durationSec > 0
                ? DateTimeOffset.UtcNow.AddSeconds(durationSec)
                : DateTimeOffset.MaxValue;
            Task<string?>? inputTask = durationSec == 0
                ? Console.In.ReadLineAsync(cancellationToken).AsTask()
                : null;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var update = observer.Observe();
                writer.Write(update.Events);
                await managedDiagnostics.ObserveAsync(
                    update.Events,
                    update.Disposition,
                    cancellationToken);
                writer.Write(resourceSampler.TrySample(
                    observer.Session!.CaptureResourceCounters()));

                if (observer.Session.HasObservedProcesses && !observer.Session.HasActiveProcesses)
                {
                    var finalUpdate = observer.Observe();
                    writer.Write(finalUpdate.Events);
                    writer.Write(resourceSampler.TrySample(
                        observer.Session.CaptureResourceCounters()));
                    return "target-exited";
                }
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    return "duration";
                }
                if (inputTask?.IsCompleted == true)
                {
                    await inputTask;
                    return "input";
                }

                await Task.Delay(PollInterval, cancellationToken);
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
