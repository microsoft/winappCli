// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace WinApp.Cli.Services.Performance;

[JsonSerializable(typeof(PerformanceTimelineEntry))]
[JsonSerializable(typeof(PerformanceBundleManifest))]
[JsonSerializable(typeof(PerformanceRecordResult))]
[JsonSerializable(typeof(PerformanceReport))]
[JsonSerializable(typeof(PerformanceReportRecording))]
[JsonSerializable(typeof(PerformanceReportStartup))]
[JsonSerializable(typeof(PerformanceReportCollectors))]
[JsonSerializable(typeof(PerformanceReportEvidence))]
[JsonSerializable(typeof(PerformanceResponsivenessSummary))]
[JsonSerializable(typeof(PerformanceResponseFailureInterval))]
[JsonSerializable(typeof(ResourceTimelineEntry))]
[JsonSerializable(typeof(PerformanceScenarioMarkerEntry))]
[JsonSerializable(typeof(PerformanceUiActionEntry))]
[JsonSerializable(typeof(ResourceCaptureManifest))]
[JsonSerializable(typeof(ResourceSummary))]
[JsonSerializable(typeof(WprCollectorResult))]
[JsonSerializable(typeof(ManagedDiagnosticsResult))]
[JsonSerializable(typeof(PerformanceArtifact))]
[JsonSerializable(typeof(IReadOnlyList<PerformanceArtifact>))]
[JsonSerializable(typeof(PerformanceOpenManifest))]
[JsonSerializable(typeof(PerformanceOpenResult))]
[JsonSerializable(typeof(ResponseProbeManifest))]
[JsonSerializable(typeof(StartupTimingManifest))]
[JsonSerializable(typeof(PerformanceStartupSummary))]
[JsonSerializable(typeof(StartupProcessExit))]
[JsonSerializable(typeof(StartupStageSummary))]
[JsonSerializable(typeof(StartupStageResourceFacts))]
[JsonSerializable(typeof(StartupEvidenceCoverage))]
[JsonSerializable(typeof(XamlAnalysisManifest))]
[JsonSerializable(typeof(XamlPerformanceSummary))]
[JsonSerializable(typeof(XamlPhaseSummary))]
[JsonSerializable(typeof(XamlInterval))]
[JsonSerializable(typeof(IReadOnlyList<XamlInterval>))]
[JsonSerializable(typeof(PerformanceScenarioDefinition))]
[JsonSerializable(typeof(PerformanceSetManifest))]
[JsonSerializable(typeof(PerformanceScenarioCommandResult))]
[JsonSerializable(typeof(PerformanceComparisonResult))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class PerformanceJsonContext : JsonSerializerContext;
