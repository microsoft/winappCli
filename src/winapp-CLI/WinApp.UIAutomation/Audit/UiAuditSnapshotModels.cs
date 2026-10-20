// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>Bounds for a read-only snapshot of one window's raw UI Automation tree.</summary>
public sealed record UiAuditSnapshotOptions
{
    /// <summary>Maximum descendant depth, inclusive; the root has depth zero. Defaults to 20; allowed range is 1–50.</summary>
    public int MaxDepth { get; init; } = 20;

    /// <summary>Maximum elements examined, including rejected elements. Defaults to 2000; allowed range is 1–10000.</summary>
    public int MaxElements { get; init; } = 2000;

    /// <summary>Maximum retained visible formatting ranges per node. Defaults to 16; allowed range is 1–32.</summary>
    public int MaxTextRunsPerNode { get; init; } = 16;

    /// <summary>Cooperative capture deadline in milliseconds. Defaults to 5000; allowed range is 1–120000.</summary>
    /// <remarks>A provider can block inside one COM call. Use an externally time-bounded process when a hard deadline is required.</remarks>
    public int TimeoutMs { get; init; } = 5000;
}

/// <summary>A bounded, read-only observation of the raw UI Automation tree within exactly one top-level HWND.</summary>
/// <remarks>
/// This is provider evidence, not an accessibility pass/fail verdict. Unsupported attributes and
/// deliberate privacy withholding are explicit even when traversal completes. Accessible names
/// can contain user data. No text content or Value property is collected.
/// Other native top-level windows are excluded even when UIA nests them under the selected window.
/// </remarks>
public sealed record UiAuditSnapshotResult
{
    /// <summary>Version of this document's JSON contract; currently 1.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>UTC time at which capture began.</summary>
    public DateTimeOffset CapturedAtUtc { get; init; }

    /// <summary>Process ID supplied by the caller and verified against the selected native window.</summary>
    public int ProcessId { get; init; }

    /// <summary>Exact native window handle supplied by the caller; no other window is searched.</summary>
    public long WindowHandle { get; init; }

    /// <summary>Root UIA runtime ID, formatted as uppercase hexadecimal integers such as <c>[2A,7705FE,4,6]</c>, when available.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? RootRuntimeId { get; init; }

    /// <summary>Root accessible name when its privacy status permits collection; may contain user data.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Title { get; init; }

    /// <summary><c>captured</c> when traversal and reads completed, or <c>partial</c> after a failure, limit, cancellation, or unverifiable final identity.</summary>
    public string Status { get; init; } = "captured";

    /// <summary>Requested maximum descendant depth, inclusive.</summary>
    public int MaxDepth { get; init; }

    /// <summary>Requested maximum number of examined elements.</summary>
    public int MaxElements { get; init; }

    /// <summary>Whether at least one child subtree was omitted because of the depth bound.</summary>
    public bool DepthLimitReached { get; init; }

    /// <summary>Whether additional elements were omitted because of the examined-element bound.</summary>
    public bool ElementLimitReached { get; init; }

    /// <summary>Observed elements in raw-tree preorder, with scan-local IDs and parent links.</summary>
    public IReadOnlyList<UiAuditNode> Nodes { get; init; } = [];

    /// <summary>Capture-wide warnings, failures, and limitations. Node-specific evidence gaps appear on each node.</summary>
    public IReadOnlyList<UiAuditSnapshotIssue> Issues { get; init; } = [];
}

/// <summary>A machine-readable explanation of an evidence gap, read failure, or privacy warning.</summary>
/// <param name="Code">Stable, lower-case identifier.</param>
/// <param name="Message">Explanation that never includes a provider exception's potentially sensitive message.</param>
public sealed record UiAuditSnapshotIssue(string Code, string Message)
{
    /// <summary>Native top-level HWND omitted at a <c>window_boundary_excluded</c> boundary; absent for other issues. Its element names, runtime IDs, bounds, and text attributes are not captured.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ExcludedWindowHandle { get; init; }
}

/// <summary>A failure that prevents establishing an initial snapshot target.</summary>
/// <param name="code">Stable, lower-case error identifier.</param>
/// <param name="message">Non-sensitive explanation of the failure.</param>
public sealed class UiAuditSnapshotException(string code, string message) : InvalidOperationException(message)
{
    /// <summary>Stable error identifier for command-line and library consumers.</summary>
    public string Code { get; } = code;
}

/// <summary>Captures bounded UIA evidence without focusing, activating, scrolling, or modifying a window.</summary>
public interface IUiAuditSnapshotService
{
    /// <summary>Captures the raw UIA tree for the exact supplied HWND and process on a dedicated MTA thread.</summary>
    /// <param name="target">A nonzero top-level HWND and positive, matching process ID. Neither field is resolved or replaced by this service.</param>
    /// <param name="options">Depth, element, visible-formatting-range, and cooperative time bounds.</param>
    /// <param name="cancellationToken">Stops further calls cooperatively; evidence already collected is returned as partial.</param>
    /// <returns>A snapshot containing explicit evidence gaps and truncation indicators.</returns>
    /// <exception cref="UiAuditSnapshotException">Arguments or the initial native/UIA target cannot be verified.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested before capture started.</exception>
    /// <remarks>COM calls themselves cannot be interrupted; a hard time bound requires an external process deadline.</remarks>
    Task<UiAuditSnapshotResult> CaptureAsync(UiTarget target, UiAuditSnapshotOptions options, CancellationToken cancellationToken = default);
}
