// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>One focus sample. Outside-window samples deliberately contain no element metadata.</summary>
public sealed record UiAuditFocusPoint
{
    /// <summary>Zero for the initial sample; subsequent samples follow delivered chords.</summary>
    public int Sequence { get; init; }

    /// <summary><c>initial</c>, <c>forward</c>, or <c>reverse</c>.</summary>
    public string Direction { get; init; } = "initial";

    /// <summary>When this explicit sample was taken.</summary>
    public DateTimeOffset ObservedAtUtc { get; init; }

    /// <summary>Scan-local identity. Not a selector and not stable between runs.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? NodeId { get; init; }

    /// <summary>Native runtime identifier in uppercase hexadecimal array form; not stable across restarts.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? RuntimeId { get; init; }

    /// <summary>Provider AutomationId when available. An absent ID does not prevent observation.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? AutomationId { get; init; }

    /// <summary>Accessible name, withheld for password, text-bearing, or privacy-unknown elements.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Name { get; init; }

    /// <summary>Whether the accessible name was deliberately not read.</summary>
    public bool NameWithheld { get; init; }

    /// <summary>UI Automation control type, when available.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ControlType { get; init; }

    /// <summary>Password status across the verified ancestry; null means unknown.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool? IsPassword { get; init; }

    /// <summary>Whether both process and selected-window ancestry were verified.</summary>
    public bool IsInTarget { get; init; }

    /// <summary>Whether the same known scan-local identity appeared earlier; repetition alone is not a failure.</summary>
    public bool Repeat { get; init; }
}
