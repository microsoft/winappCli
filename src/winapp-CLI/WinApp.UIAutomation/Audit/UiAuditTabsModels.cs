// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using System.Security.Cryptography;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>Bounds and optional explicit approval for exact standard-tab selection.</summary>
public sealed record UiAuditTabsOptions
{
    /// <summary>Maximum raw-tree descendant depth, inclusive; the root is depth zero. Allowed range: 1-50.</summary>
    public int MaxDepth { get; init; } = 20;
    /// <summary>Maximum examined elements per bounded traversal. Allowed range: 1-10000.</summary>
    public int MaxElements { get; init; } = 2000;
    /// <summary>Cooperative timeout in milliseconds, including selection confirmation. Allowed range: 1000-120000.</summary>
    public int TimeoutMs { get; init; } = 15000;
    /// <summary>Exact runtime ID of the TabItem to select, or null for read-only discovery.</summary>
    public string? Select { get; init; }
    /// <summary>Exact runtime ID of its nearest ancestor standard Tab group.</summary>
    public string? Container { get; init; }
    /// <summary>Root runtime ID from a previous discovery of this exact window.</summary>
    public string? RootRuntimeId { get; init; }
    /// <summary>Explicit approval to call SelectionItem.Select; all three runtime IDs are also required.</summary>
    public bool AcceptNavigation { get; init; }
}

/// <summary>Bounded, name-free discovery of standard tabs and optional exact selection evidence.</summary>
public sealed record UiAuditTabsResult
{
    /// <summary>JSON contract version, currently 1.</summary>
    public int SchemaVersion { get; init; } = 1;
    /// <summary>Verified native process ID, or zero if initial target verification failed.</summary>
    public int ProcessId { get; init; }
    /// <summary>Exact requested top-level HWND; no other window is substituted.</summary>
    public long WindowHandle { get; init; }
    /// <summary>Root runtime ID in audit-snapshot's bracketed hexadecimal format, or empty if unavailable.</summary>
    public string RootRuntimeId { get; init; } = "";
    /// <summary>Uppercase SHA256 of bounded discovery metadata: preorder depth, runtime IDs, control types, enabled/offscreen state and selected state when read. No names, values or AutomationIds are included.</summary>
    /// <remarks>Unknown states have explicit markers in the hash input. Selection responses describe pre-selection discovery; use fresh read-only snapshots for settling. A stable signature is not semantic or content-loaded readiness.</remarks>
    public string TreeSignature { get; init; } = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>()));
    /// <summary>captured, partial, or blocked. Only captured represents completed discovery and any requested selection.</summary>
    public string Status { get; init; } = "blocked";
    /// <summary>Requested maximum descendant depth.</summary>
    public int MaxDepth { get; init; }
    /// <summary>Requested maximum examined elements.</summary>
    public int MaxElements { get; init; }
    /// <summary>Whether children were omitted at the depth bound.</summary>
    public bool DepthLimitReached { get; init; }
    /// <summary>Whether elements were omitted at the examination bound.</summary>
    public bool ElementLimitReached { get; init; }
    /// <summary>Pre-selection Tab inventory; unsupported controls remain explicit. Rediscover after selection before using nested groups.</summary>
    public IReadOnlyList<UiAuditTabContainer> Containers { get; init; } = [];
    /// <summary>Explicit limitations and failures; never contains accessible names, values or text.</summary>
    public IReadOnlyList<string> Issues { get; init; } = [];
    /// <summary>Requested exact selection and its confirmed outcome, or null for discovery.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public UiAuditTabSelection? Selection { get; init; }
}

/// <summary>An observed standard Tab group and its directly owned TabItems.</summary>
public sealed record UiAuditTabContainer
{
    /// <summary>Exact runtime ID, or empty when unavailable.</summary>
    public string RuntimeId { get; init; } = "";
    /// <summary>Nearest ancestor Tab group's runtime ID, independent of TabItem ancestry.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ParentContainerRuntimeId { get; init; }
    /// <summary>Optional AutomationId; never required for classification or selection.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? AutomationId { get; init; }
    /// <summary>Whether enabled was confirmed; unsupported properties produce an explicit reason.</summary>
    public bool IsEnabled { get; init; }
    /// <summary>Whether offscreen or visibility could not be confirmed.</summary>
    public bool IsOffscreen { get; init; }
    /// <summary>Whether this is a realized, visible, enabled, single-selection standard Tab group.</summary>
    public bool Supported { get; init; }
    /// <summary>Explicit unsupported or skipped reason, otherwise null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Reason { get; init; }
    /// <summary>Observed TabItems whose nearest Tab ancestor is this group.</summary>
    public IReadOnlyList<UiAuditTabItem> Items { get; init; } = [];
}

/// <summary>An observed standard TabItem; no name or text is read.</summary>
public sealed record UiAuditTabItem
{
    /// <summary>Exact runtime ID, or empty when unavailable.</summary>
    public string RuntimeId { get; init; } = "";
    /// <summary>Optional AutomationId; never used as a fallback identity.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? AutomationId { get; init; }
    /// <summary>Whether enabled was confirmed.</summary>
    public bool IsEnabled { get; init; }
    /// <summary>Whether offscreen or visibility could not be confirmed.</summary>
    public bool IsOffscreen { get; init; }
    /// <summary>Observed SelectionItem state; unsupported readback is explicitly marked unsupported.</summary>
    public bool IsSelected { get; init; }
    /// <summary>Whether exact SelectionItem selection is supported and its Tab ownership was verified.</summary>
    public bool Supported { get; init; }
    /// <summary>Explicit unsupported or skipped reason, otherwise null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Reason { get; init; }
}

/// <summary>Selection evidence for the exact requested runtime IDs.</summary>
/// <param name="ContainerRuntimeId">Requested standard Tab group ID.</param>
/// <param name="ItemRuntimeId">Requested TabItem ID.</param>
/// <param name="Status">selected, already-selected, or blocked; successful states require two readbacks at least 50 ms apart. Blocked can follow an unconfirmed selection attempt; no rollback is performed.</param>
public sealed record UiAuditTabSelection(string ContainerRuntimeId, string ItemRuntimeId, string Status);

/// <summary>Discovers standard tabs without reading names or changing focus; selection requires explicit approval and exact IDs.</summary>
public interface IUiAuditTabsService
{
    /// <summary>Captures one exact HWND on an MTA thread and optionally selects one verified standard TabItem.</summary>
    /// <param name="windowHandle">Exact positive top-level native window handle. The native process and UIA root are pinned internally.</param>
    /// <param name="options">Traversal bounds and, for mutation, all three expected runtime IDs plus consent.</param>
    /// <param name="cancellationToken">Stops additional provider calls cooperatively. A blocked COM call requires an external hard deadline.</param>
    /// <returns>Typed discovery and selection evidence, including operational failures and truncation.</returns>
    /// <exception cref="ArgumentException">Bounds, HWND or selection approval arguments are invalid.</exception>
    Task<UiAuditTabsResult> CaptureAsync(long windowHandle, UiAuditTabsOptions options, CancellationToken cancellationToken = default);
}
