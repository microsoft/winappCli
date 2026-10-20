// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>A reviewed locator, resolved again in the explicitly selected window on replay.</summary>
public sealed record UiActionTarget
{
    /// <summary>Exact application-assigned ID, preferred over a name.</summary>
    public string? AutomationId { get; init; }
    /// <summary>Exact accessible name, used only when no ID is available.</summary>
    public string? Name { get; init; }
    /// <summary>UIA control type, such as Button or Edit.</summary>
    public required string ControlType { get; init; }
}

/// <summary>One recorded action or a manually reviewed assertion.</summary>
public sealed record UiActionStep
{
    /// <summary>Create a step using required object-initializer fields.</summary>
    public UiActionStep() { }

    /// <summary>Deserialize optional fields with their documented defaults.</summary>
    [JsonConstructor]
    [SetsRequiredMembers]
    public UiActionStep(string kind, UiActionTarget target, long elapsedMs = 0, string? keys = null,
        string? text = null, string? parameter = null, string? button = null, int clickCount = 1,
        double xFraction = 0.5, double yFraction = 0.5, int wheelDelta = 0,
        string? property = null, string? expected = null)
    {
        Kind = kind;
        Target = target;
        ElapsedMs = elapsedMs;
        Keys = keys;
        Text = text;
        Parameter = parameter;
        Button = button;
        ClickCount = clickCount;
        XFraction = xFraction;
        YFraction = yFraction;
        WheelDelta = wheelDelta;
        Property = property;
        Expected = expected;
    }

    /// <summary>click, key, text, wheel, or assert.</summary>
    public required string Kind { get; init; }
    /// <summary>The target of the action; keyboard actions require this target to have focus.</summary>
    public required UiActionTarget Target { get; init; }
    /// <summary>Recorded relative time for diagnostics, not a fixed replay delay.</summary>
    public long ElapsedMs { get; init; }
    /// <summary>Key-string chord for a key action, not literal text.</summary>
    public string? Keys { get; init; }
    /// <summary>Literal replacement text when explicitly recorded with text capture enabled.</summary>
    public string? Text { get; init; }
    /// <summary>Required replay parameter for replacement text; never combined with Text.</summary>
    public string? Parameter { get; init; }
    /// <summary>left or right for a click.</summary>
    public string? Button { get; init; }
    /// <summary>One or two for single or double clicks.</summary>
    public int ClickCount { get; init; } = 1;
    /// <summary>Horizontal position relative to the current element bounds, from zero to one.</summary>
    public double XFraction { get; init; } = 0.5;
    /// <summary>Vertical position relative to the current element bounds, from zero to one.</summary>
    public double YFraction { get; init; } = 0.5;
    /// <summary>Vertical mouse-wheel delta, in Windows wheel units.</summary>
    public int WheelDelta { get; init; }
    /// <summary>Name, Value, IsEnabled, or ToggleState for an assert step.</summary>
    public string? Property { get; init; }
    /// <summary>Exact expected property value for a manually authored assert step.</summary>
    public string? Expected { get; init; }
}

/// <summary>Versioned, local-only recording. Recorded observations are not a correctness oracle.</summary>
public sealed record UiActionFlow
{
    /// <summary>Create a recording using required object-initializer fields.</summary>
    public UiActionFlow() { }

    /// <summary>Deserialize optional metadata while preserving default recording state.</summary>
    [JsonConstructor]
    [SetsRequiredMembers]
    public UiActionFlow(string processName, int schemaVersion = 1, string status = "recorded",
        UiActionTarget? initialFocus = null, IReadOnlyList<UiActionStep>? steps = null,
        IReadOnlyList<string>? warnings = null)
    {
        ProcessName = processName;
        SchemaVersion = schemaVersion;
        Status = status;
        InitialFocus = initialFocus;
        Steps = steps ?? [];
        Warnings = warnings ?? [];
    }

    /// <summary>Format version; currently one.</summary>
    public int SchemaVersion { get; init; } = 1;
    /// <summary>recorded or incomplete. Incomplete recordings must never be replayed.</summary>
    public string Status { get; init; } = "recorded";
    /// <summary>Recorded executable name. Replay checks it case-insensitively against the explicit target, never launching an app from it.</summary>
    public required string ProcessName { get; init; }
    /// <summary>Initial keyboard focus; only this anchor may be focused programmatically at replay startup.</summary>
    public UiActionTarget? InitialFocus { get; init; }
    /// <summary>Captured actions in order.</summary>
    public IReadOnlyList<UiActionStep> Steps { get; init; } = [];
    /// <summary>Recording limitations and scope transitions requiring review.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>Options for opt-in, app-scoped input recording.</summary>
public sealed record UiActionRecordOptions
{
    /// <summary>Zero records until cancellation; otherwise stops after this many seconds.</summary>
    public int DurationSec { get; init; }
    /// <summary>Opt in to storing complete non-password edit-field values instead of parameter placeholders.</summary>
    public bool CaptureText { get; init; }
    /// <summary>Opt in to recording OS-injected input, primarily for controlled automation tests.</summary>
    public bool IncludeInjected { get; init; }
    /// <summary>Maximum actions retained before recording stops as incomplete.</summary>
    public int MaxSteps { get; init; } = 1000;
}

/// <summary>The final recording and an explicit reason why capture ended.</summary>
public sealed record UiActionRecordResult(UiActionFlow Flow, string StopReason);

/// <summary>Replay configuration; no input is injected without review acceptance.</summary>
public sealed record UiActionReplayOptions
{
    /// <summary>Explicit user approval to execute the recorded inputs against the selected app.</summary>
    public bool AcceptRecording { get; init; }
    /// <summary>Timeout for each element, focus, or assertion checkpoint.</summary>
    public int TimeoutMs { get; init; } = 5000;
    /// <summary>Values of reviewed, named text parameters.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
}

/// <summary>Completed input delivery is distinct from a passing business or accessibility test.</summary>
public sealed record UiActionReplayResult(int CompletedSteps, int AssertionsChecked);

/// <summary>Records explicit user interactions and replays approved action flows.</summary>
public interface IUiActionService
{
    /// <summary>Capture a single window; cancellation stops capture and returns the retained flow.</summary>
    Task<UiActionRecordResult> RecordAsync(UiTarget target, UiActionRecordOptions options,
        CancellationToken cancellationToken, Action? onReady = null);
    /// <summary>Replay a validated flow against the explicitly selected window.</summary>
    Task<UiActionReplayResult> ReplayAsync(UiTarget target, UiActionFlow flow,
        UiActionReplayOptions options, CancellationToken cancellationToken);
}

/// <summary>A failed flow operation with an actionable machine-readable error code.</summary>
public sealed class UiActionException(string code, string message) : InvalidOperationException(message)
{
    /// <summary>Stable failure category; no text parameter values are included.</summary>
    public string Code { get; } = code;
}
