// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace WinApp.Cli.Services.Performance;

internal interface IPerformanceSetWriter
{
    PerformanceSetManifest Write(
        string outputDirectory,
        ValidatedPerformanceScenario scenario,
        int warmup,
        int repeat,
        PerformanceScenarioRunResult runResult);
}

internal sealed class PerformanceSetWriter : IPerformanceSetWriter
{
    internal const string SchemaVersion = "0.1";

    public PerformanceSetManifest Write(
        string outputDirectory,
        ValidatedPerformanceScenario scenario,
        int warmup,
        int repeat,
        PerformanceScenarioRunResult runResult)
    {
        var finalDirectory = Path.GetFullPath(outputDirectory);
        if (Path.Exists(finalDirectory))
        {
            throw new IOException($"Performance set already exists: {finalDirectory}");
        }
        var parent = Path.GetDirectoryName(finalDirectory)
            ?? throw new IOException("Performance set must have a parent directory.");
        Directory.CreateDirectory(parent);
        var staging = Path.Join(
            parent,
            $".{Path.GetFileName(finalDirectory)}.{Guid.NewGuid():N}.staging");

        try
        {
            Directory.CreateDirectory(staging);
            var iterations = CopyIterations(
                staging,
                runResult.Iterations,
                scenario.Steps,
                scenario.MetricDefinitions);
            var manifest = new PerformanceSetManifest
            {
                SchemaVersion = SchemaVersion,
                Status = SanitizeStatus(
                    runResult.Status,
                    ["completed", "failed", "cancelled", "incomplete"],
                    "failed"),
                FailureReason = SanitizeFailureReason(runResult.FailureReason),
                CreatedUtc = DateTimeOffset.UtcNow,
                Scenario = new()
                {
                    Id = scenario.Definition.Id,
                    DefinitionHash = scenario.DefinitionHash,
                },
                Warmup = warmup,
                Repeat = repeat,
                Conditions = runResult.Conditions,
                MetricDefinitions = scenario.MetricDefinitions,
                Iterations = iterations,
            };
            File.WriteAllText(
                Path.Join(staging, "manifest.json"),
                JsonSerializer.Serialize(
                    manifest,
                    PerformanceJsonContext.Default.PerformanceSetManifest),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (Path.Exists(finalDirectory))
            {
                throw new IOException($"Performance set appeared before publication: {finalDirectory}");
            }
            Directory.Move(staging, finalDirectory);
            return manifest;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static List<PerformanceScenarioIteration> CopyIterations(
        string staging,
        IReadOnlyList<PerformanceScenarioIteration> iterations,
        IReadOnlyList<PerformanceScenarioStep> scenarioSteps,
        IReadOnlyList<PerformanceMetricDefinition> metricDefinitions)
    {
        var result = new List<PerformanceScenarioIteration>(iterations.Count);
        foreach (var iteration in iterations.OrderBy(item => item.Ordinal))
        {
            string? relativeBundle = null;
            if (iteration.SourceBundle is { } source)
            {
                if (!Directory.Exists(source)
                    || !File.Exists(Path.Join(source, "manifest.json")))
                {
                    throw new InvalidDataException(
                        $"Iteration {iteration.Ordinal} bundle is missing or incomplete.");
                }
                relativeBundle = Path.Join(
                    "iterations",
                    $"{iteration.Ordinal:D3}-{(iteration.IsWarmup ? "warmup" : "measure")}.winappperf");
                CopyDirectory(source, Path.Join(staging, relativeBundle));
            }
            var sanitizedSteps = iteration.Steps
                .Select(step => (Result: step, Definition: scenarioSteps.FirstOrDefault(
                    definition => definition.Ordinal == step.Ordinal)))
                .Where(step => step.Definition is not null)
                .Select(step => new PerformanceScenarioStepResult
                {
                    Ordinal = step.Definition!.Ordinal,
                    Phase = step.Definition.Phase,
                    Verb = step.Definition.Verb,
                    Status = SanitizeStatus(
                        step.Result.Status,
                        ["completed", "failed", "skipped", "cancelled"],
                        "failed"),
                    DurationMs = Math.Max(0, step.Result.DurationMs),
                    ExitCode = step.Result.ExitCode,
                })
                .ToArray();
            var sanitizedMetrics = metricDefinitions.Select(definition => new PerformanceMetricValue
            {
                Name = definition.Name,
                Value = iteration.Metrics.FirstOrDefault(metric =>
                    metric.Name == definition.Name)?.Value,
            }).ToArray();
            result.Add(iteration with
            {
                Status = SanitizeStatus(
                    iteration.Status,
                    ["completed", "failed", "cancelled"],
                    "failed"),
                FailureReason = SanitizeFailureReason(iteration.FailureReason),
                DurationMs = Math.Max(0, iteration.DurationMs),
                Bundle = relativeBundle,
                Steps = sanitizedSteps,
                Metrics = sanitizedMetrics,
                SourceBundle = null,
            });
        }
        return result;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Join(
                destination,
                Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Join(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static string? SanitizeFailureReason(string? reason)
    {
        if (reason is null)
        {
            return null;
        }
        return reason.Length is > 0 and <= 64
            && reason.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
                ? reason
                : "scenario-runner-failed";
    }

    private static string SanitizeStatus(
        string status,
        IReadOnlyList<string> allowed,
        string fallback) =>
        allowed.Contains(status, StringComparer.Ordinal) ? status : fallback;

    internal static PerformanceSetConditions CurrentConditions() => new()
    {
        Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
        OsVersion = Environment.OSVersion.Version.ToString(),
        LogicalProcessorCount = Environment.ProcessorCount,
        ResponseProbe = "SendMessageTimeout(WM_NULL)",
        ResponseProbeCadenceMs = 250,
        ResponseProbeTimeoutMs = 100,
        ResourceCadenceMs = 500,
        RecordingSchemaVersion = PerformanceBundleSchema.CurrentVersion,
    };
}
