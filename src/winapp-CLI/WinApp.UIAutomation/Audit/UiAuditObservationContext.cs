// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

internal interface IUiAuditObservationEnvironmentFactory
{
    IUiAuditObservationEnvironment Create(UiTarget target, UiAuditObservationBudget budget, UiAuditEventBuffer events);
}

internal interface IUiAuditObservationEnvironment : IDisposable
{
    int ProcessId { get; }
    bool TargetExists { get; }
    bool HasInteractiveDesktop { get; }
    bool IsForeground { get; }
    void Subscribe();
    void Activate();
    UiAuditFocusPoint ReadFocus();
    void SendTab(bool reverse);
}

internal sealed class UiAuditObservationBudget : IDisposable
{
    private readonly TimeProvider _clock;
    private readonly long _start;
    private readonly CancellationTokenSource _deadline;
    private readonly CancellationTokenSource _linked;
    private readonly int _durationMs;

    internal UiAuditObservationBudget(int durationSec, TimeProvider clock, CancellationToken cancellationToken)
    {
        _clock = clock;
        _start = clock.GetTimestamp();
        StartedAtUtc = clock.GetUtcNow();
        _durationMs = durationSec * 1000;
        Cancellation = cancellationToken;
        _deadline = new CancellationTokenSource(TimeSpan.FromSeconds(durationSec), clock);
        _linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _deadline.Token);
    }

    internal DateTimeOffset StartedAtUtc { get; }
    internal DateTimeOffset UtcNow => _clock.GetUtcNow();
    internal CancellationToken Cancellation { get; }
    internal CancellationToken Token => _linked.Token;
    internal int RemainingMs => Math.Max(0, _durationMs - (int)_clock.GetElapsedTime(_start).TotalMilliseconds);
    internal bool Expired => RemainingMs == 0 || _deadline.IsCancellationRequested;

    internal void Check()
    {
        Cancellation.ThrowIfCancellationRequested();
        if (Expired)
        {
            throw new UiAuditObservationException("duration_elapsed", "The observation time budget elapsed.");
        }
    }

    public void Dispose()
    {
        _linked.Dispose();
        _deadline.Dispose();
    }
}

internal sealed class UiAuditEventBuffer(int maxEvents, UiAuditObservationBudget budget)
{
    private readonly object _gate = new();
    private readonly List<UiAuditEvent> _events = [];
    private readonly Dictionary<string, UiAuditObservationIssue> _issues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _identities = new(StringComparer.Ordinal);
    private int _nextIdentity;
    private bool _closed;
    private bool _eventLimitReached;
    private bool _coverageLimited;
    private string? _stopReason;

    internal bool CanAccept
    {
        get
        {
            lock (_gate)
            {
                return !_closed && _stopReason is null && !budget.Token.IsCancellationRequested && !budget.Expired;
            }
        }
    }

    internal string? StopReason { get { lock (_gate) { return _stopReason; } } }
    internal bool EventLimitReached { get { lock (_gate) { return _eventLimitReached; } } }
    internal bool CoverageLimited { get { lock (_gate) { return _coverageLimited; } } }

    internal void Add(UiAuditEvent value)
    {
        lock (_gate)
        {
            if (_closed || _stopReason is not null || budget.Token.IsCancellationRequested || budget.Expired)
            {
                return;
            }

            _events.Add(value with { Sequence = _events.Count + 1 });
            if (_events.Count == maxEvents)
            {
                _eventLimitReached = true;
                _stopReason = "event_limit";
                Issue("event_limit", "The native event retention limit was reached; later events were not collected.");
            }
        }
    }

    internal string Identity(string? runtimeId)
    {
        lock (_gate)
        {
            if (runtimeId is not null && _identities.TryGetValue(runtimeId, out var known))
            {
                return known;
            }

            var id = $"o{++_nextIdentity}";
            // Native callbacks are serialized, and explicit focus samples are separately bounded.
            if (runtimeId is not null && _identities.Count < maxEvents + 61)
            {
                _identities.Add(runtimeId, id);
            }
            return id;
        }
    }

    internal void Issue(string code, string message, bool limitsCoverage = true)
    {
        lock (_gate)
        {
            _coverageLimited |= limitsCoverage;
            if (_issues.Count < 64)
            {
                _issues.TryAdd(code, new UiAuditObservationIssue(code, message));
            }
        }
    }

    internal void Stop(string code, string message)
    {
        lock (_gate)
        {
            _stopReason ??= code;
            Issue(code, message);
        }
    }

    internal void Close() { lock (_gate) { _closed = true; } }
    internal UiAuditEvent[] Events() { lock (_gate) { return [.. _events]; } }
    internal UiAuditObservationIssue[] Issues() { lock (_gate) { return [.. _issues.Values]; } }
}

internal static class UiAuditObservationPrivacy
{
    internal const int MaximumStringLength = 512;
    internal const int MaximumRuntimeIdLength = 128;
    private static readonly string[] ControlTypes =
    [
        "Button", "Calendar", "CheckBox", "ComboBox", "Edit", "Hyperlink", "Image",
        "ListItem", "List", "Menu", "MenuBar", "MenuItem", "ProgressBar", "RadioButton",
        "ScrollBar", "Slider", "Spinner", "StatusBar", "Tab", "TabItem", "Text", "ToolBar",
        "ToolTip", "Tree", "TreeItem", "Custom", "Group", "Thumb", "DataGrid", "DataItem",
        "Document", "SplitButton", "Window", "Pane", "Header", "HeaderItem", "Table",
        "TitleBar", "Separator", "SemanticZoom", "AppBar",
    ];

    internal static bool? Boolean(ComVariant value)
        => value.VarType == VarEnum.VT_BOOL ? value.As<bool>() : null;

    internal static bool WithholdName(bool? password, bool? textBearing)
        => password != false || textBearing != false;

    internal static string? ReadName(bool? password, bool? textBearing, Func<string?> read)
        => WithholdName(password, textBearing) ? null : read();

    internal static string? RuntimeId(ReadOnlySpan<int> values)
    {
        if (values.Length is 0 or > MaximumRuntimeIdLength)
        {
            return null;
        }
        var parts = new string[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            parts[i] = unchecked((uint)values[i]).ToString("X", CultureInfo.InvariantCulture);
        }
        return $"[{string.Join(",", parts)}]";
    }

    internal static bool TextControlType(int type) => type is 50003 or 50004 or 50030;

    internal static string? ControlType(int type)
        => type is >= 50000 and <= 50040 ? ControlTypes[type - 50000] : null;
}
