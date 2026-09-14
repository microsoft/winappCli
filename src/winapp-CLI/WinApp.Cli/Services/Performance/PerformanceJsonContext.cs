// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace WinApp.Cli.Services.Performance;

[JsonSerializable(typeof(PerformanceTimelineEntry))]
[JsonSerializable(typeof(PerformanceBundleManifest))]
[JsonSerializable(typeof(PerformanceRecordResult))]
[JsonSerializable(typeof(ResourceTimelineEntry))]
[JsonSerializable(typeof(ResourceCaptureManifest))]
[JsonSerializable(typeof(ResourceSummary))]
[JsonSerializable(typeof(WprCollectorResult))]
[JsonSerializable(typeof(ManagedCollectorResult))]
[JsonSerializable(typeof(ManagedCollectorsResult))]
[JsonSerializable(typeof(PerformanceArtifact))]
[JsonSerializable(typeof(IReadOnlyList<PerformanceArtifact>))]
[JsonSerializable(typeof(PerformanceOpenManifest))]
[JsonSerializable(typeof(PerformanceOpenResult))]
[JsonSerializable(typeof(ResponseProbeManifest))]
[JsonSerializable(typeof(StartupTimingManifest))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class PerformanceJsonContext : JsonSerializerContext;
