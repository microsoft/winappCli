// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>An actual native callback. Property values and input text are never retained.</summary>
public sealed record UiAuditEvent
{
    /// <summary>One-based order of retained callbacks.</summary>
    public int Sequence { get; init; }

    /// <summary>Time the callback was received, before inspecting its source.</summary>
    public DateTimeOffset ReceivedAtUtc { get; init; }

    /// <summary><c>focus</c>, <c>property</c>, <c>structure</c>, <c>live-region</c>, or <c>notification</c>.</summary>
    public string Kind { get; init; } = "";

    /// <summary>Scan-local identity of the source; not a reusable selector.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? NodeId { get; init; }

    /// <summary>Source runtime identifier in uppercase hexadecimal array form.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? RuntimeId { get; init; }

    /// <summary>Source AutomationId when available.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? AutomationId { get; init; }

    /// <summary>Source name, withheld for password, text-bearing, or privacy-unknown elements.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Name { get; init; }

    /// <summary>Whether the source name was deliberately not read.</summary>
    public bool NameWithheld { get; init; }

    /// <summary>Source control type when available.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ControlType { get; init; }

    /// <summary>Actual property identifier for a property-changed callback. Its new value is not recorded.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? PropertyId { get; init; }

    /// <summary>Actual native structure-change type, when applicable.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? StructureChangeType { get; init; }

    /// <summary>Removed child's runtime identifier supplied by a structure callback, when available.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ChangedRuntimeId { get; init; }

    /// <summary>Actual native notification kind, when applicable.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? NotificationKind { get; init; }

    /// <summary>Actual native notification processing mode, when applicable.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? NotificationProcessing { get; init; }

    /// <summary>Notification display text. This observer withholds it because it can contain input values.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? DisplayString { get; init; }

    /// <summary>True for notification callbacks whose display text was not inspected.</summary>
    public bool DisplayStringWithheld { get; init; }

    /// <summary>Notification activity text. This observer withholds this provider-controlled string.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ActivityId { get; init; }

    /// <summary>True for notification callbacks whose activity text was not inspected.</summary>
    public bool ActivityIdWithheld { get; init; }
}
