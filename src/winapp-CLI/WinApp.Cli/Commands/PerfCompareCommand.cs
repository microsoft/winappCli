// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Text.Json;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;
using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Commands;

internal sealed class PerfCompareCommand : Command, IShortDescription
{
    internal static readonly Argument<DirectoryInfo> BaselineArgument = new("baseline-set")
    {
        Description = "Baseline .winappperfset directory.",
    };

    internal static readonly Argument<DirectoryInfo> CandidateArgument = new("candidate-set")
    {
        Description = "Candidate .winappperfset directory.",
    };

    internal static readonly Option<string?> OutputOption = new("--output")
    {
        Description = "Optional atomic JSON comparison result path.",
    };

    public string ShortDescription => "Compare two compatible repeatable performance sets";

    public PerfCompareCommand()
        : base("compare", "Compare deterministic medians from two compatible .winappperfset directories.")
    {
        Arguments.Add(BaselineArgument);
        Arguments.Add(CandidateArgument);
        Options.Add(OutputOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    internal sealed class Handler(
        IPerformanceSetComparer comparer,
        ICurrentDirectoryProvider currentDirectory,
        ILogger<PerfCompareCommand> logger) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(
            ParseResult parseResult,
            CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(130);
            }
            try
            {
                var result = comparer.Compare(
                    parseResult.GetRequiredValue(BaselineArgument).FullName,
                    parseResult.GetRequiredValue(CandidateArgument).FullName);
                var json = JsonSerializer.Serialize(
                    result,
                    PerformanceJsonContext.Default.PerformanceComparisonResult);
                if (parseResult.GetValue(OutputOption) is { } output)
                {
                    AtomicFile.WriteAllText(
                        Path.GetFullPath(output, currentDirectory.GetCurrentDirectory()),
                        json);
                }
                if (parseResult.GetValue(WinAppRootCommand.JsonOption))
                {
                    parseResult.InvocationConfiguration.Output.WriteLine(json);
                }
                else
                {
                    WriteHuman(parseResult, result);
                }
                return Task.FromResult(result.Status == "completed" ? 0 : 1);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError("{Message}", ex.Message);
                return Task.FromResult(1);
            }
        }

        private static void WriteHuman(ParseResult parseResult, PerformanceComparisonResult result)
        {
            parseResult.InvocationConfiguration.Output.WriteLine(
                $"Performance comparison: {result.Status}");
            if (result.Error is not null)
            {
                parseResult.InvocationConfiguration.Output.WriteLine($"Detail: {result.Error}");
            }
            foreach (var metric in result.Metrics)
            {
                var tolerance = metric.PercentTolerance is { } percent
                    ? $"{Format(percent)}% ({Format(metric.AllowedDifference)} {metric.Unit} at the baseline median)"
                    : $"{Format(metric.AbsoluteTolerance)} {metric.Unit}";
                parseResult.InvocationConfiguration.Output.WriteLine(
                    $"{metric.Name}: baseline {Format(metric.BaselineMedian)} {metric.Unit}; " +
                    $"candidate {Format(metric.CandidateMedian)} {metric.Unit}; " +
                    $"change {FormatSigned(metric.Difference)} {metric.Unit}; " +
                    $"tolerance {tolerance}; direction {metric.Direction}");
                if (metric.Error is not null)
                {
                    parseResult.InvocationConfiguration.Output.WriteLine(
                        $"  Detail: {metric.Error}");
                }
            }
        }

        private static string Format(double? value) =>
            value is null ? "n/a" : value.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        private static string FormatSigned(double? value) =>
            value is null
                ? "n/a"
                : value.Value.ToString("+0.###;-0.###;0", System.Globalization.CultureInfo.InvariantCulture);
    }
}
