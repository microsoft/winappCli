// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>Limits and consent for one window's event observation.</summary>
public sealed record UiAuditObservationOptions
{
    /// <summary>Total observation and keyboard-probe budget, from 1 through 60 seconds.</summary>
    public int DurationSec { get; init; } = 5;

    /// <summary>Maximum Tab presses in each direction, from 0 through 30. Zero sends no input.</summary>
    public int TabSteps { get; init; }

    /// <summary>Consent to initial window activation and real Tab/Shift+Tab input, which can change application state.</summary>
    public bool AcceptInput { get; init; }

    /// <summary>Maximum retained native events, from 1 through 5000.</summary>
    public int MaxEvents { get; init; } = 500;
}

/// <summary>Bounded observations, not an accessibility verdict or proof of keyboard reachability.</summary>
public sealed record UiAuditObservationResult
{
    /// <summary>Version of this result's JSON contract.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Process that owns the selected window.</summary>
    public int ProcessId { get; init; }

    /// <summary>The single selected top-level window.</summary>
    public long WindowHandle { get; init; }

    /// <summary>Start of the observation budget, including setup.</summary>
    public DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>Time observation and subscription cleanup finished.</summary>
    public DateTimeOffset EndedAtUtc { get; init; }

    /// <summary><c>completed</c> or <c>partial</c>; describes collection, not application accessibility.</summary>
    public string Status { get; init; } = "partial";

    /// <summary>Requested total budget in seconds.</summary>
    public int DurationSec { get; init; }

    /// <summary>Requested number of Tab presses per direction.</summary>
    public int RequestedTabSteps { get; init; }

    /// <summary>Total complete Tab/Shift+Tab chords accepted by the input transport, not confirmed application responses.</summary>
    public int CompletedTabSteps { get; init; }

    /// <summary>Configured maximum retained native events.</summary>
    public int MaxEvents { get; init; }

    /// <summary>Whether native event collection reached its retention limit.</summary>
    public bool EventLimitReached { get; init; }

    /// <summary>Machine-readable reason collection stopped.</summary>
    public string StopReason { get; init; } = "";

    /// <summary>Explicit focus samples, kept separate from native event callbacks.</summary>
    public UiAuditFocusPoint[] FocusTrace { get; init; } = [];

    /// <summary>Actual native UI Automation events whose source was verified inside the selected window.</summary>
    public UiAuditEvent[] Events { get; init; } = [];

    /// <summary>Collection limitations and environment conditions, never application defect verdicts.</summary>
    public UiAuditObservationIssue[] Issues { get; init; } = [];
}

/// <summary>A limitation of this observation run.</summary>
/// <param name="Code">Stable machine-readable identifier.</param>
/// <param name="Message">Description that contains no provider exception text or outside-window content.</param>
public sealed record UiAuditObservationIssue(string Code, string Message);

/// <summary>Invalid observation options or an environment condition preventing safe observation.</summary>
public sealed class UiAuditObservationException : InvalidOperationException
{
    /// <summary>Machine-readable failure code.</summary>
    public string Code { get; }

    /// <summary>Creates a failure with a content-free diagnostic.</summary>
    /// <param name="code">Machine-readable code.</param>
    /// <param name="message">Diagnostic that must not contain captured application content.</param>
    public UiAuditObservationException(string code, string message) : base(message) => Code = code;
}
