// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace WinApp.Cli.Services.Performance;

internal sealed record XamlInterval
{
    public required int ProcessId { get; init; }
    public required int ThreadId { get; init; }
    public required string Type { get; init; }
    public required bool IsInteresting { get; init; }
    public required double DurationMs { get; init; }
    public required double WeightMs { get; init; }
    public required int Count { get; init; }
    public required double TraceStartSeconds { get; init; }
    public required double TraceStopSeconds { get; init; }
}

internal sealed record XamlPhaseSummary
{
    public int? UiThreadId { get; init; }
    public double? RegionOfInterestMs { get; init; }
    public double? InitializationMs { get; init; }
    public double? LongestInterestingFrameMs { get; init; }
    public double? LongestInterestingUpdateLayoutMs { get; init; }
    public double? GraphicsDeviceCreationMs { get; init; }
}

internal sealed record XamlAnalysisManifest
{
    public required bool Requested { get; init; }
    public required string Status { get; init; }
    public required string Coverage { get; init; }
    public required string Profile { get; init; }
    public int? TargetProcessId { get; init; }
    public long? TargetProcessStartTimeUtcTicks { get; init; }
    public string? ExporterVersion { get; init; }
    public string? PluginVersion { get; init; }
    public string? SummaryPath { get; init; }
    public required int MatchedIntervalCount { get; init; }
    public XamlPhaseSummary? Summary { get; init; }
    public string? Error { get; init; }
    public string? Remediation { get; init; }
    public string? RecommendedViewer { get; init; }
}

internal sealed record XamlPerformanceSummary
{
    public const string CurrentSchemaVersion = "0.1";

    public required string SchemaVersion { get; init; }
    public required int TargetProcessId { get; init; }
    public required long TargetProcessStartTimeUtcTicks { get; init; }
    public required string Profile { get; init; }
    public required string Coverage { get; init; }
    public required XamlPhaseSummary Summary { get; init; }
    public required IReadOnlyList<XamlInterval> Intervals { get; init; }
}

internal sealed record XamlAnalysisResult(
    XamlAnalysisManifest Manifest,
    XamlPerformanceSummary? Summary);

internal interface IXamlPerformanceAnalyzer
{
    Task<XamlAnalysisResult> AnalyzeAsync(
        bool requested,
        string? etlPath,
        ProcessIdentity? targetProcess,
        string? lossStatus,
        CancellationToken cancellationToken);
}

internal sealed class XamlPerformanceAnalyzer(
    IWptXamlToolResolver toolResolver,
    IProcessRunner processRunner,
    ILogger<XamlPerformanceAnalyzer> logger) : IXamlPerformanceAnalyzer
{
    internal const string ProfileName = "WinAppPerf.Verbose";
    internal const string SummaryPath = "summaries/xaml.json";
    private const string ExportedCsvName = "Xaml_Frame_Analysis_Summary_Table_All_Xaml_Info.csv";
    private static readonly TimeSpan ExportTimeout = TimeSpan.FromMinutes(2);

    public async Task<XamlAnalysisResult> AnalyzeAsync(
        bool requested,
        string? etlPath,
        ProcessIdentity? targetProcess,
        string? lossStatus,
        CancellationToken cancellationToken)
    {
        if (!requested)
        {
            return NotRequested();
        }

        if (string.IsNullOrWhiteSpace(etlPath) || !File.Exists(etlPath))
        {
            return Unavailable(
                targetProcess,
                "The XAML ETL artifact was not produced.",
                "Run the recording again from an elevated terminal.");
        }

        if (targetProcess is null)
        {
            return Unavailable(
                null,
                "The target process generation was not observed, so XAML rows cannot be attributed safely.",
                "Verify that the target launches a new WinUI 3 process during the recording.");
        }

        var resolution = toolResolver.Resolve();
        if (!resolution.IsAvailable || resolution.WpaExporterPath is null)
        {
            return Unavailable(
                targetProcess,
                $"XAML analysis tooling is unavailable ({resolution.UnavailableReason}).",
                resolution.Remediation,
                resolution.ToolVersion,
                resolution.PluginVersion);
        }

        var traceDirectory = Path.GetDirectoryName(Path.GetFullPath(etlPath))
            ?? throw new IOException("The XAML ETL path must have a parent directory.");
        var scratchDirectory = Path.Join(
            traceDirectory,
            $".xaml-analysis-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratchDirectory);

        try
        {
            var profilePath = Path.Join(scratchDirectory, "xaml-frame-analysis.wpaProfile");
            await using (var source = WptXamlProfileResources.OpenAllXamlInfo())
            await using (var destination = new FileStream(
                profilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

            ProcessRunResult export;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ExportTimeout);
            try
            {
                export = await processRunner.RunAsync(
                    new(
                        resolution.WpaExporterPath,
                        [
                            "-i",
                            Path.GetFullPath(etlPath),
                            "-profile",
                            profilePath,
                            "-outputfolder",
                            scratchDirectory,
                            "-outputformat",
                            "CSV",
                        ]),
                    cancellationToken: timeout.Token);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested
                && timeout.IsCancellationRequested)
            {
                return Failed(
                    targetProcess.Value,
                    resolution,
                    $"WPAExporter did not complete within {ExportTimeout.TotalSeconds:0} seconds.");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                return Failed(targetProcess.Value, resolution, ex.Message);
            }

            var csvPath = Path.Join(scratchDirectory, ExportedCsvName);
            if (export.ExitCode != 0 || !File.Exists(csvPath))
            {
                var detail = FirstUsefulLine(export.StandardError)
                    ?? FirstUsefulLine(export.StandardOutput)
                    ?? $"WPAExporter exited with code {export.ExitCode} without producing {ExportedCsvName}.";
                return Failed(targetProcess.Value, resolution, detail);
            }

            IReadOnlyList<XamlInterval> intervals;
            try
            {
                intervals = ParseTargetIntervals(csvPath, targetProcess.Value.ProcessId);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                return Failed(targetProcess.Value, resolution, ex.Message);
            }

            var phaseSummary = CreatePhaseSummary(intervals);
            var coverage = string.Equals(lossStatus, "none", StringComparison.OrdinalIgnoreCase)
                ? "complete"
                : string.Equals(lossStatus, "detected", StringComparison.OrdinalIgnoreCase)
                    ? "analyzed-event-loss"
                    : "analyzed-loss-not-inspected";
            var summary = new XamlPerformanceSummary
            {
                SchemaVersion = XamlPerformanceSummary.CurrentSchemaVersion,
                TargetProcessId = targetProcess.Value.ProcessId,
                TargetProcessStartTimeUtcTicks = targetProcess.Value.StartTimeUtcTicks,
                Profile = ProfileName,
                Coverage = coverage,
                Summary = phaseSummary,
                Intervals = intervals,
            };
            return new(
                new()
                {
                    Requested = true,
                    Status = "analyzed",
                    Coverage = coverage,
                    Profile = ProfileName,
                    TargetProcessId = targetProcess.Value.ProcessId,
                    TargetProcessStartTimeUtcTicks = targetProcess.Value.StartTimeUtcTicks,
                    ExporterVersion = resolution.ToolVersion,
                    PluginVersion = resolution.PluginVersion,
                    SummaryPath = SummaryPath,
                    MatchedIntervalCount = intervals.Count,
                    Summary = phaseSummary,
                    RecommendedViewer = "WPA",
                },
                summary);
        }
        finally
        {
            try
            {
                Directory.Delete(scratchDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(
                    ex,
                    "Could not remove temporary XAML analysis directory {Directory}.",
                    scratchDirectory);
            }
        }
    }

    internal static IReadOnlyList<XamlInterval> ParseTargetIntervals(
        string csvPath,
        int targetProcessId)
    {
        using var reader = new StreamReader(csvPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var headerLine = reader.ReadLine()
            ?? throw new InvalidDataException("The XAML analysis CSV is empty.");
        var headers = ParseCsvLine(headerLine);
        var indexes = RequiredColumnIndexes(headers);
        var result = new List<XamlInterval>();
        string? line;
        var lineNumber = 1;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var fields = ParseCsvLine(line);
            if (fields.Count != headers.Count)
            {
                throw new InvalidDataException(
                    $"XAML analysis CSV row {lineNumber} has {fields.Count} fields; expected {headers.Count}.");
            }

            var type = fields[indexes.Type].Trim();
            if (type.Length == 0)
            {
                continue;
            }

            var processId = ParseProcessId(fields[indexes.Process], lineNumber);
            if (processId != targetProcessId)
            {
                continue;
            }

            result.Add(new()
            {
                ProcessId = processId,
                ThreadId = ParseInt(fields[indexes.ThreadId], "Thread ID", lineNumber),
                Type = type,
                IsInteresting = ParseBool(fields[indexes.IsInteresting], "IsInteresting", lineNumber),
                DurationMs = ParseDouble(fields[indexes.Duration], "Duration (ms)", lineNumber),
                WeightMs = ParseDouble(fields[indexes.Weight], "Weight (ms)", lineNumber),
                Count = ParseInt(fields[indexes.Count], "Count", lineNumber),
                TraceStartSeconds = ParseDouble(fields[indexes.Start], "Start (s)", lineNumber),
                TraceStopSeconds = ParseDouble(fields[indexes.Stop], "Stop (s)", lineNumber),
            });
        }

        return result;
    }

    internal static IReadOnlyList<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    value.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                fields.Add(value.ToString());
                value.Clear();
            }
            else
            {
                value.Append(character);
            }
        }

        if (quoted)
        {
            throw new InvalidDataException("The XAML analysis CSV contains an unterminated quoted field.");
        }

        fields.Add(value.ToString());
        return fields;
    }

    private static XamlPhaseSummary CreatePhaseSummary(IReadOnlyList<XamlInterval> intervals)
    {
        var initialization = Longest(intervals, "WXM::InitializeForCurrentThread", interestingOnly: true);
        var frame = Longest(intervals, "Frame", interestingOnly: true);
        var layout = Longest(intervals, "UpdateLayout", interestingOnly: true);
        return new()
        {
            UiThreadId = initialization?.ThreadId ?? frame?.ThreadId ?? layout?.ThreadId,
            RegionOfInterestMs = Longest(intervals, "Region of Interest")?.DurationMs,
            InitializationMs = initialization?.DurationMs,
            LongestInterestingFrameMs = frame?.DurationMs,
            LongestInterestingUpdateLayoutMs = layout?.DurationMs,
            GraphicsDeviceCreationMs = Longest(intervals, "Create graphics device")?.DurationMs,
        };
    }

    private static XamlInterval? Longest(
        IEnumerable<XamlInterval> intervals,
        string type,
        bool interestingOnly = false) =>
        intervals
            .Where(interval =>
                interval.Type.Equals(type, StringComparison.Ordinal)
                && (!interestingOnly || interval.IsInteresting))
            .MaxBy(interval => interval.DurationMs);

    private static (int Process, int ThreadId, int Type, int IsInteresting, int Duration, int Weight, int Count, int Start, int Stop)
        RequiredColumnIndexes(IReadOnlyList<string> headers)
    {
        return (
            Find("Process"),
            Find("Thread ID"),
            Find("Type"),
            Find("IsInteresting"),
            Find("Duration (ms)"),
            Find("Weight (ms)"),
            Find("Count"),
            Find("Start (s)"),
            Find("Stop (s)"));

        int Find(string name)
        {
            for (var index = 0; index < headers.Count; index++)
            {
                if (headers[index].Equals(name, StringComparison.Ordinal))
                {
                    return index;
                }
            }

            throw new InvalidDataException(
                $"The XAML analysis CSV does not contain required column '{name}'.");
        }
    }

    private static int ParseProcessId(string value, int lineNumber)
    {
        var close = value.LastIndexOf(')');
        var open = close > 0 ? value.LastIndexOf('(', close - 1) : -1;
        if (open < 0
            || close != value.Length - 1
            || !int.TryParse(
                value.AsSpan(open + 1, close - open - 1),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var processId))
        {
            throw new InvalidDataException(
                $"XAML analysis CSV row {lineNumber} has invalid Process value '{value}'.");
        }

        return processId;
    }

    private static int ParseInt(string value, string column, int lineNumber)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        throw new InvalidDataException(
            $"XAML analysis CSV row {lineNumber} has invalid {column} value '{value}'.");
    }

    private static double ParseDouble(string value, string column, int lineNumber)
    {
        if (double.TryParse(
                value,
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out var parsed)
            && double.IsFinite(parsed)
            && parsed >= 0)
        {
            return parsed;
        }

        throw new InvalidDataException(
            $"XAML analysis CSV row {lineNumber} has invalid {column} value '{value}'.");
    }

    private static bool ParseBool(string value, string column, int lineNumber)
    {
        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        throw new InvalidDataException(
            $"XAML analysis CSV row {lineNumber} has invalid {column} value '{value}'.");
    }

    private static XamlAnalysisResult NotRequested() => new(
        new()
        {
            Requested = false,
            Status = "not-requested",
            Coverage = "not-requested",
            Profile = ProfileName,
            MatchedIntervalCount = 0,
        },
        null);

    private static XamlAnalysisResult Unavailable(
        ProcessIdentity? targetProcess,
        string error,
        string? remediation,
        string? exporterVersion = null,
        string? pluginVersion = null) => new(
            new()
            {
                Requested = true,
                Status = "analysis-unavailable",
                Coverage = "unavailable",
                Profile = ProfileName,
                TargetProcessId = targetProcess?.ProcessId,
                TargetProcessStartTimeUtcTicks = targetProcess?.StartTimeUtcTicks,
                ExporterVersion = exporterVersion,
                PluginVersion = pluginVersion,
                MatchedIntervalCount = 0,
                Error = error,
                Remediation = remediation,
                RecommendedViewer = "WPA",
            },
            null);

    private static XamlAnalysisResult Failed(
        ProcessIdentity targetProcess,
        WptXamlToolResolution resolution,
        string error) => new(
            new()
            {
                Requested = true,
                Status = "analysis-failed",
                Coverage = "unavailable",
                Profile = ProfileName,
                TargetProcessId = targetProcess.ProcessId,
                TargetProcessStartTimeUtcTicks = targetProcess.StartTimeUtcTicks,
                ExporterVersion = resolution.ToolVersion,
                PluginVersion = resolution.PluginVersion,
                MatchedIntervalCount = 0,
                Error = error,
                Remediation = "Open the retained ETL in WPA and inspect XAML Frame Analysis manually.",
                RecommendedViewer = "WPA",
            },
            null);

    private static string? FirstUsefulLine(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line =>
                !line.StartsWith("Cannot use file stream for", StringComparison.OrdinalIgnoreCase));
}
