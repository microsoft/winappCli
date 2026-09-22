// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace WinApp.Cli.Services.Performance;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PerformanceScenarioDefinition
{
    public required string SchemaVersion { get; init; }
    public required string Id { get; init; }
    public required IReadOnlyList<PerformanceScenarioStepDefinition> Setup { get; init; }
    public required IReadOnlyList<PerformanceScenarioStepDefinition> Measure { get; init; }
    public required IReadOnlyList<PerformanceScenarioStepDefinition> Cleanup { get; init; }
    public required IReadOnlyList<PerformanceScenarioMetricTolerance> Metrics { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PerformanceScenarioStepDefinition
{
    public required IReadOnlyList<string> Ui { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PerformanceScenarioMetricTolerance
{
    public required string Name { get; init; }
    public double? AbsoluteTolerance { get; init; }
    public double? PercentTolerance { get; init; }
}

internal sealed record ValidatedPerformanceScenario(
    PerformanceScenarioDefinition Definition,
    string DefinitionHash,
    IReadOnlyList<PerformanceScenarioStep> Steps,
    IReadOnlyList<PerformanceMetricDefinition> MetricDefinitions);

internal sealed record PerformanceScenarioStep
{
    public required int Ordinal { get; init; }
    public required string Phase { get; init; }
    public required string Verb { get; init; }
}

internal sealed record PerformanceMetricDefinition
{
    public required string Name { get; init; }
    public required string Unit { get; init; }
    public required string Direction { get; init; }
    public double? AbsoluteTolerance { get; init; }
    public double? PercentTolerance { get; init; }
}

internal sealed record PerformanceMetricValue
{
    public required string Name { get; init; }
    public double? Value { get; init; }
}

internal sealed record PerformanceScenarioStepResult
{
    public required int Ordinal { get; init; }
    public required string Phase { get; init; }
    public required string Verb { get; init; }
    public required string Status { get; init; }
    public required double DurationMs { get; init; }
    public int? ExitCode { get; init; }
}

internal sealed record PerformanceScenarioIteration
{
    public required int Ordinal { get; init; }
    public required bool IsWarmup { get; init; }
    public required string Status { get; init; }
    public string? FailureReason { get; init; }
    public required double DurationMs { get; init; }
    public int? ExitCode { get; init; }
    public string? Bundle { get; init; }
    public required IReadOnlyList<PerformanceScenarioStepResult> Steps { get; init; }
    public required IReadOnlyList<PerformanceMetricValue> Metrics { get; init; }

    [JsonIgnore]
    public string? SourceBundle { get; init; }
}

internal sealed record PerformanceScenarioIdentity
{
    public required string Id { get; init; }
    public required string DefinitionHash { get; init; }
}

internal sealed record PerformanceSetConditions
{
    public required string Architecture { get; init; }
    public required string OsVersion { get; init; }
    public required int LogicalProcessorCount { get; init; }
    public required string ResponseProbe { get; init; }
    public required double ResponseProbeCadenceMs { get; init; }
    public required double ResponseProbeTimeoutMs { get; init; }
    public required double ResourceCadenceMs { get; init; }
    public required string RecordingSchemaVersion { get; init; }
}

internal sealed record PerformanceSetManifest
{
    public required string SchemaVersion { get; init; }
    public required string Status { get; init; }
    public string? FailureReason { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public required PerformanceScenarioIdentity Scenario { get; init; }
    public required int Warmup { get; init; }
    public required int Repeat { get; init; }
    public required PerformanceSetConditions Conditions { get; init; }
    public required IReadOnlyList<PerformanceMetricDefinition> MetricDefinitions { get; init; }
    public required IReadOnlyList<PerformanceScenarioIteration> Iterations { get; init; }
}

internal sealed record PerformanceScenarioRunRequest(
    ValidatedPerformanceScenario Scenario,
    FileSystemInfo? Target,
    int Warmup,
    int Repeat,
    string IterationRoot,
    IReadOnlyList<string>? AdditionalRunArguments = null);

internal sealed record PerformanceScenarioRunResult
{
    public required string Status { get; init; }
    public string? FailureReason { get; init; }
    public required PerformanceSetConditions Conditions { get; init; }
    public required IReadOnlyList<PerformanceScenarioIteration> Iterations { get; init; }
}

internal sealed record PerformanceScenarioCommandResult
{
    public required string Status { get; init; }
    public required string Set { get; init; }
    public string? FailureReason { get; init; }
    public required string ScenarioId { get; init; }
    public required string ScenarioHash { get; init; }
    public required int Warmup { get; init; }
    public required int Repeat { get; init; }
    public required int CompletedMeasuredIterations { get; init; }
}

internal sealed record PerformanceMetricComparison
{
    public required string Name { get; init; }
    public required string Unit { get; init; }
    public required string Direction { get; init; }
    public double? BaselineMedian { get; init; }
    public double? CandidateMedian { get; init; }
    public double? Difference { get; init; }
    public double? AbsoluteTolerance { get; init; }
    public double? PercentTolerance { get; init; }
    public double? AllowedDifference { get; init; }
    public required string Status { get; init; }
    public string? Error { get; init; }
}

internal sealed record PerformanceComparisonResult
{
    public required string Status { get; init; }
    public string? Error { get; init; }
    public required string BaselineSet { get; init; }
    public required string CandidateSet { get; init; }
    public required IReadOnlyList<PerformanceMetricComparison> Metrics { get; init; }
}
