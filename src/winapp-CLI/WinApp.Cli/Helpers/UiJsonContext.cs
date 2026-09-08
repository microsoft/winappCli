// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Source-generated JSON serializer context for UI automation models (NativeAOT-safe).
/// </summary>
[JsonSerializable(typeof(UiElement))]
[JsonSerializable(typeof(UiElement[]))]
[JsonSerializable(typeof(UiTarget))]
[JsonSerializable(typeof(UiStatusResult))]
[JsonSerializable(typeof(UiInspectResult))]
[JsonSerializable(typeof(UiInspectWindowInfo))]
[JsonSerializable(typeof(UiInspectWindowInfo[]))]
[JsonSerializable(typeof(UiSearchResult))]
[JsonSerializable(typeof(UiPropertyResult))]
[JsonSerializable(typeof(Dictionary<string, string?>))]
[JsonSerializable(typeof(UiInvokeResult))]
[JsonSerializable(typeof(UiClickResult))]
[JsonSerializable(typeof(UiScreenshotResult))]
[JsonSerializable(typeof(UiScreenshotResult[]))]
[JsonSerializable(typeof(UiRecordResult))]
[JsonSerializable(typeof(UiRecordStartedEvent))]
[JsonSerializable(typeof(RecordFrameArtifactResult))]
[JsonSerializable(typeof(RecordFrameIndexEntry))]
[JsonSerializable(typeof(RecordFrameBundleManifest))]
[JsonSerializable(typeof(UiGetValueResult))]
[JsonSerializable(typeof(UiWaitForResult))]
[JsonSerializable(typeof(UiScrollResult))]
[JsonSerializable(typeof(UiSetValueResult))]
[JsonSerializable(typeof(UiFocusResult))]
[JsonSerializable(typeof(UiScrollIntoViewResult))]
[JsonSerializable(typeof(UiHoverResult))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(UiSendKeysResult))]
[JsonSerializable(typeof(UiDragResult))]
[JsonSerializable(typeof(UiTouchResult))]
[JsonSerializable(typeof(UiPenResult))]
[JsonSerializable(typeof(UiPointResult))]
[JsonSerializable(typeof(UiPointResult[]))]
[JsonSerializable(typeof(UiErrorResult))]
[JsonSerializable(typeof(UiErrorInfo))]
[JsonSerializable(typeof(UiFocusedResult))]
[JsonSerializable(typeof(UiScreenshotWindowInfo))]
[JsonSerializable(typeof(WindowInfo))]
[JsonSerializable(typeof(WindowInfo[]))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    MaxDepth = 256,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class UiJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(UiRecordStartedEvent))]
[JsonSerializable(typeof(UiErrorResult))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class UiJsonLineContext : JsonSerializerContext;

// JSON output models for --json mode

internal sealed class UiStatusResult
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = "";
    public string? WindowTitle { get; set; }
    public long Hwnd { get; set; }
}

internal sealed class UiInspectResult
{
    /// <summary>Depth used for the tree walk (after any --interactive bump).</summary>
    public int Depth { get; set; }

    /// <summary>Whether the --interactive filter was applied.</summary>
    public bool Interactive { get; set; }

    /// <summary>Whether the --hide-disabled filter was applied.</summary>
    public bool HideDisabled { get; set; }

    /// <summary>Whether the --hide-offscreen filter was applied.</summary>
    public bool HideOffscreen { get; set; }

    /// <summary>One entry per inspected window, each containing its nested element tree.</summary>
    public UiInspectWindowInfo[] Windows { get; set; } = [];
}

internal sealed class UiInspectWindowInfo
{
    public long Hwnd { get; set; }
    public string? Title { get; set; }
    public string? ClassName { get; set; }
    /// <summary>Total real elements (counting nested children) belonging to this window.</summary>
    public int ElementCount { get; set; }
    /// <summary>Root elements for this window. Children are nested via <c>UiElement.Children</c>.</summary>
    public UiElement[] Elements { get; set; } = [];
}

internal sealed class UiSearchResult
{
    public int MatchCount { get; set; }
    public bool HasMore { get; set; }
    public UiElement[] Matches { get; set; } = [];
}

internal sealed class UiPropertyResult
{
    public string ElementId { get; set; } = "";
    public Dictionary<string, string?> Properties { get; set; } = [];
}

internal sealed class UiInvokeResult
{
    public string ElementId { get; set; } = "";
    public string Pattern { get; set; } = "";
    public long Hwnd { get; set; }
}

internal sealed class UiClickResult
{
    public string ElementId { get; set; } = "";
    public string ClickType { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public long Hwnd { get; set; }
}

internal sealed class UiScreenshotResult
{
    public string? ElementId { get; set; }
    public string FilePath { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int ProcessId { get; set; }
    public string? WindowTitle { get; set; }
    public long Hwnd { get; set; }

    /// <summary>For composite multi-window screenshots, details of each captured window. Null for single-window captures.</summary>
    public UiScreenshotWindowInfo[]? Windows { get; set; }
}

internal sealed class UiScreenshotWindowInfo
{
    public long Hwnd { get; set; }
    public string? Title { get; set; }
    public string? Label { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Captured { get; set; }
    public string? Error { get; set; }
}

internal sealed class UiRecordResult
{
    public string Path { get; set; } = "";
    public int DurationSec { get; set; }
    public int Fps { get; set; }
    public int Frames { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public long FileSize { get; set; }
    public string Codec { get; set; } = "h264";
    public string Mode { get; set; } = "";
    public long ElapsedMs { get; set; }
    public double AchievedFps { get; set; }
    public double CadenceRatio { get; set; }
    public string StopReason { get; set; } = "";
    public RecordFrameArtifactResult? FrameArtifacts { get; set; }
    public string[]? Warnings { get; set; }
}

internal sealed class UiRecordStartedEvent
{
    public string Event { get; set; } = "recording-started";
    public string Path { get; set; } = "";
    public int Fps { get; set; }
    public int DurationSec { get; set; }
    public string? FramesDirectory { get; set; }
    public string? FramesManifest { get; set; }
    public string? FramesIndex { get; set; }
}

internal sealed class UiWaitForResult
{
    public bool Found { get; set; }
    public int WaitedMs { get; set; }
    public UiElement? Element { get; set; }

    /// <summary>True if the wait exhausted the --timeout without satisfying the condition.</summary>
    public bool TimedOut { get; set; }
}

/// <summary>Envelope for <c>ui get-focused --json</c>. Always emitted (even when no element has focus)
/// so consumers can deterministically detect the no-focus case.</summary>
internal sealed class UiFocusedResult
{
    public bool HasFocus { get; set; }
    public UiElement? Element { get; set; }
}

internal sealed class UiErrorResult
{
    public UiErrorInfo Error { get; set; } = new();
}

internal sealed class UiErrorInfo
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Selector { get; set; }
    public string? Details { get; set; }
    public string? RecoveryHint { get; set; }
    public UiPartialOutputInfo? PartialOutput { get; set; }
}

internal sealed class UiPartialOutputInfo
{
    public string? VideoPath { get; set; }
    public string? FramesDirectory { get; set; }
}

internal sealed class WindowInfo
{
    public long Hwnd { get; set; }
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = "";
    public string? Title { get; set; }
    public string? Label { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public long OwnerHwnd { get; set; }
    public string? ClassName { get; set; }
    public bool IsForeground { get; set; }
}

internal sealed class UiGetValueResult
{
    public string ElementId { get; set; } = "";
    public string? Text { get; set; }
}

internal sealed class UiScrollResult
{
    public string ElementId { get; set; } = "";
    public string? Direction { get; set; }
    public string? To { get; set; }
    public int? Wheel { get; set; }
    public long Hwnd { get; set; }
}

internal sealed class UiSetValueResult
{
    public string ElementId { get; set; } = "";
    public long Hwnd { get; set; }
}

internal sealed class UiFocusResult
{
    public string ElementId { get; set; } = "";
    public long Hwnd { get; set; }
}

internal sealed class UiScrollIntoViewResult
{
    public string ElementId { get; set; } = "";
    public long Hwnd { get; set; }
}

internal sealed class UiHoverResult
{
    public string ElementId { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int DwellTimeMs { get; set; }
    public long Hwnd { get; set; }
}

internal sealed class UiSendKeysResult
{
    public string Keys { get; set; } = "";
    public string Via { get; set; } = "";
    public int ActionCount { get; set; }
    public string? Target { get; set; }
    public long Hwnd { get; set; }
    public List<string> Warnings { get; set; } = [];
}

internal sealed class UiDragResult
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public int FromX { get; set; }
    public int FromY { get; set; }
    public int ToX { get; set; }
    public int ToY { get; set; }
    public string Button { get; set; } = "left";
    public int HoldMs { get; set; }
    public int DwellMs { get; set; }
    public long Hwnd { get; set; }
}

/// <summary>A point in app/screen pixel space for touch/pen JSON output.</summary>
internal sealed class UiPointResult
{
    public int X { get; set; }
    public int Y { get; set; }
}

internal sealed class UiTouchResult
{
    public string Gesture { get; set; } = "";
    /// <summary>The selector or app <c>x,y</c> the gesture targeted.</summary>
    public string? Target { get; set; }
    public UiPointResult[] Points { get; set; } = [];
    public int Fingers { get; set; }
    public int DurationMs { get; set; }
    /// <summary>Effective hold time in milliseconds (populated for long-press; 0 for other gestures).</summary>
    public int HoldMs { get; set; }
    public long Hwnd { get; set; }

    /// <summary>Non-fatal advisories (e.g. remote-session delivery uncertainty). Omitted when empty.</summary>
    public string[]? Warnings { get; set; }
}

internal sealed class UiPenResult
{
    public string Action { get; set; } = "";
    /// <summary>The selector or app <c>x,y</c> the pen action targeted.</summary>
    public string? Target { get; set; }
    public UiPointResult[] Points { get; set; } = [];
    public float Pressure { get; set; }
    public int TiltX { get; set; }
    public int TiltY { get; set; }
    public bool Eraser { get; set; }
    public int DurationMs { get; set; }
    public long Hwnd { get; set; }

    /// <summary>Non-fatal advisories (e.g. remote-session delivery uncertainty). Omitted when empty.</summary>
    public string[]? Warnings { get; set; }
}
