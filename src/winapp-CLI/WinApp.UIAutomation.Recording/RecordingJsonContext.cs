// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording;

/// <summary>
/// Source-generated serialization for the frame-bundle artifacts written alongside a recording.
/// Options mirror the CLI's JSON output (indented, camelCase, LF newlines) so the manifest and
/// index files are byte-identical to what <c>winapp ui record --frames</c> has always produced.
/// </summary>
[JsonSerializable(typeof(RecordFrameBundleManifest))]
[JsonSerializable(typeof(RecordFrameIndexEntry))]
[JsonSerializable(typeof(RecordFrameArtifactResult))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    MaxDepth = 256,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class RecordingJsonContext : JsonSerializerContext;
