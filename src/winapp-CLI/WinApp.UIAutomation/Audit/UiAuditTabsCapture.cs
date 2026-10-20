// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

internal enum UiAuditTabKind { Other, Tab, TabItem }

internal sealed record UiAuditTabRead
{
    internal UiAuditTabKind Kind { get; init; }
    internal int? ControlType { get; init; }
    internal string RuntimeId { get; init; } = "";
    internal string? AutomationId { get; init; }
    internal bool? Enabled { get; init; }
    internal bool? Offscreen { get; init; }
    internal bool Realized { get; init; }
    internal bool SingleSelection { get; init; }
    internal bool SelectionItem { get; init; }
    internal bool? Selected { get; init; }
    internal bool SelectionContainerMatches { get; init; }
    internal bool Modal { get; init; }
    internal IReadOnlyList<string> Issues { get; init; } = [];
}

internal abstract class UiAuditTabsTree<T> where T : class
{
    internal abstract T Root { get; }
    internal abstract int ProcessId { get; }
    internal abstract string RootRuntimeId { get; }
    internal abstract T? FirstChild(T node);
    internal abstract T? NextSibling(T node);
    internal abstract UiAuditTabRead Read(T node, T? parent, T? nearestTab);
    internal abstract void VerifyRoot();
    internal abstract bool HasVisibleOwnedWindow();
    internal abstract void VerifyNoModal();
    internal abstract void Select(T tab, T item, string tabId, string itemId);
    internal abstract bool ConfirmSelected(T tab, T item, string tabId, string itemId);
    internal virtual string? SelectionReadbackIssue => null;
}

internal sealed class UiAuditTabsCapture<T>(
    UiAuditTabsTree<T> tree, long window, UiAuditTabsOptions options, Action checkBudget, Action<int> delay,
    Func<TimeSpan>? elapsed = null) where T : class
{
    private sealed record Entry(T Node, UiAuditTabRead Data, Entry? ParentTab)
    {
        internal List<(T Node, UiAuditTabItem Item)> Items { get; } = [];
        internal UiAuditTabContainer Container { get; set; } = new();
    }

    private readonly List<Entry> _tabs = [];
    private readonly List<string> _issues = [];
    private readonly HashSet<string> _seenIds = new(StringComparer.Ordinal);
    private readonly StringBuilder _treeMetadata = new();
    private int _examined;
    private bool _partial;
    private bool _modal;
    private bool _depthLimit;
    private bool _elementLimit;
    private UiAuditTabSelection? _selection = options.Select is null ? null : new(options.Container!, options.Select, "blocked");
    private readonly long _started = Stopwatch.GetTimestamp();

    internal UiAuditTabsResult Run()
    {
        var blocked = false;
        try
        {
            checkBudget();
            tree.VerifyRoot();
            if (options.Select is not null && tree.RootRuntimeId != options.RootRuntimeId)
            {
                Issue("root_runtime_id_mismatch");
                return Result(blocked: true);
            }
            Visit(tree.Root, null, null, 0);
            checkBudget();
            tree.VerifyRoot();
            _modal |= tree.HasVisibleOwnedWindow();
            if (_modal)
            {
                Issue("modal_or_visible_owned_window");
                blocked = true;
            }
            if (options.Select is not null)
            {
                if (_partial || _modal)
                {
                    Issue("selection_blocked_by_incomplete_or_modal_discovery");
                    return Result(blocked: true);
                }
                blocked = !SelectExact();
            }
        }
        catch (UiAuditSnapshotStoppedException ex)
        {
            Issue(ex.Code);
            blocked = options.Select is not null;
        }
        catch (UiAuditSnapshotException ex)
        {
            Issue(ex.Code);
            blocked = true;
        }
        catch (Exception ex) when (NativeUiAuditSnapshotReader.IsProviderFailure(ex))
        {
            Issue("provider_read_failed");
            blocked = options.Select is not null;
        }
        return Result(blocked);
    }

    private void Visit(T current, T? parent, Entry? parentTab, int depth)
    {
        checkBudget();
        _examined++;
        UiAuditTabRead data;
        try
        {
            data = tree.Read(current, parent, parentTab?.Node);
        }
        catch (Exception ex) when (NativeUiAuditSnapshotReader.IsProviderFailure(ex))
        {
            Issue("node_read_failed");
            return;
        }
        AppendStructure(data, depth);
        foreach (var issue in data.Issues) { Issue(issue); }
        if (data.RuntimeId.Length == 0) { Issue("runtime_id_unavailable"); }
        if (data.Kind is UiAuditTabKind.Tab or UiAuditTabKind.TabItem && (data.Enabled is null || data.Offscreen is null))
        {
            Issue("tab_state_unavailable");
        }
        if (data.Kind == UiAuditTabKind.TabItem && data.SelectionItem && data.Selected is null)
        {
            Issue("selection_state_unavailable");
        }
        var repeated = data.RuntimeId.Length > 0 && !_seenIds.Add(data.RuntimeId);
        if (repeated) { Issue("repeated_runtime_id"); }
        _modal |= data.Modal;
        var tab = parentTab;
        if (data.Kind == UiAuditTabKind.Tab)
        {
            var reason = repeated ? "repeated_runtime_id" : CommonReason(data);
            reason ??= data.SingleSelection ? null : "single_selection_pattern_required";
            tab = new(current, data, parentTab)
            {
                Container = new()
                {
                    RuntimeId = data.RuntimeId, ParentContainerRuntimeId = parentTab?.Data.RuntimeId,
                    AutomationId = data.AutomationId, IsEnabled = data.Enabled == true, IsOffscreen = data.Offscreen != false,
                    Supported = reason is null, Reason = reason,
                },
            };
            _tabs.Add(tab);
        }
        else if (data.Kind == UiAuditTabKind.TabItem)
        {
            if (parentTab is null)
            {
                Issue($"orphan_tab_item:{data.RuntimeId}");
            }
            else
            {
                var reason = repeated ? "repeated_runtime_id" : CommonReason(data);
                reason ??= !parentTab.Container.Supported ? "tab_container_unsupported"
                    : !data.SelectionItem ? "selection_item_pattern_required"
                    : !data.SelectionContainerMatches ? "selection_container_mismatch"
                    : data.Selected is null ? "selection_state_unavailable" : null;
                parentTab.Items.Add((current, new()
                {
                    RuntimeId = data.RuntimeId, AutomationId = data.AutomationId,
                    IsEnabled = data.Enabled == true, IsOffscreen = data.Offscreen != false,
                    IsSelected = data.Selected == true, Supported = reason is null, Reason = reason,
                }));
            }
        }
        if (repeated) { return; }
        checkBudget();
        var child = tree.FirstChild(current);
        if (child is null) { return; }
        if (depth >= options.MaxDepth)
        {
            _depthLimit = true;
            Issue("depth_limit_reached");
            return;
        }
        while (child is not null)
        {
            checkBudget();
            if (_examined >= options.MaxElements)
            {
                _elementLimit = true;
                Issue("element_limit_reached");
                return;
            }
            Visit(child, current, tab, depth + 1);
            if (_elementLimit) { return; }
            child = tree.NextSibling(child);
        }
    }

    private bool SelectExact()
    {
        var tab = _tabs.SingleOrDefault(entry => entry.Data.RuntimeId == options.Container);
        var matches = tab?.Items.Where(item => item.Item.RuntimeId == options.Select).ToArray() ?? [];
        if (tab is null || !tab.Container.Supported || matches.Length != 1 || !matches[0].Item.Supported)
        {
            Issue("exact_supported_tab_item_required");
            return false;
        }
        var item = matches[0];
        checkBudget();
        tree.VerifyRoot();
        if (tree.HasVisibleOwnedWindow())
        {
            Issue("modal_or_visible_owned_window");
            return false;
        }
        tree.VerifyNoModal();
        var alreadySelected = tree.ConfirmSelected(tab.Node, item.Node, options.Container!, options.Select!);
        if (!alreadySelected)
        {
            checkBudget();
            tree.Select(tab.Node, item.Node, options.Container!, options.Select!);
        }
        var consecutive = 0;
        var lastMatch = TimeSpan.Zero;
        for (var attempt = 0; attempt <= 40; attempt++)
        {
            checkBudget();
            tree.VerifyRoot();
            if (tree.HasVisibleOwnedWindow())
            {
                Issue("selection_confirmation_blocked_by_owned_window");
                return false;
            }
            tree.VerifyNoModal();
            if (tree.ConfirmSelected(tab.Node, item.Node, options.Container!, options.Select!))
            {
                var now = elapsed?.Invoke() ?? Stopwatch.GetElapsedTime(_started);
                if (consecutive == 0)
                {
                    consecutive = 1;
                    lastMatch = now;
                }
                else if (now - lastMatch >= TimeSpan.FromMilliseconds(50))
                {
                    _selection = new(options.Container!, options.Select!, alreadySelected ? "already-selected" : "selected");
                    // Header selection is confirmed, not the old page's descendant inventory. The
                    // caller must discover again after selecting before navigating nested groups.
                    for (var i = 0; i < tab.Items.Count; i++)
                    {
                        var observed = tab.Items[i];
                        if (observed.Item.Supported)
                        {
                            tab.Items[i] = (observed.Node, observed.Item with { IsSelected = observed.Item.RuntimeId == options.Select });
                        }
                    }
                    return true;
                }
            }
            else { consecutive = 0; }
            if (attempt < 40) { delay(50); }
        }
        Issue("selection_not_confirmed_twice");
        if (tree.SelectionReadbackIssue is { } readbackIssue) { Issue(readbackIssue); }
        return false;
    }

    private static string? CommonReason(UiAuditTabRead data) =>
        data.RuntimeId.Length == 0 ? "runtime_id_unavailable"
        : data.Issues.Count > 0 ? "property_unavailable"
        : data.Enabled is null || data.Offscreen is null ? "state_unavailable"
        : data.Enabled == false ? "disabled"
        : data.Offscreen == true ? "offscreen"
        : !data.Realized ? "not_realized_or_hidden" : null;

    private void Issue(string issue)
    {
        _partial = true;
        if (!_issues.Contains(issue, StringComparer.Ordinal)) { _issues.Add(issue); }
    }

    private void AppendStructure(UiAuditTabRead data, int depth)
    {
        // Length-prefix identity strings and format numbers independently of caller culture.
        // Depth distinguishes reparenting that preserves the same preorder runtime-ID sequence.
        _treeMetadata.Append(depth.ToString(CultureInfo.InvariantCulture)).Append(':')
            .Append(data.RuntimeId.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(data.RuntimeId).Append(':')
            .Append(data.ControlType?.ToString(CultureInfo.InvariantCulture) ?? "?").Append(':')
            .Append(State(data.Enabled)).Append(State(data.Offscreen)).Append(State(data.Selected)).Append('\n');
    }

    private static char State(bool? value) => value is null ? '?' : value.Value ? '1' : '0';

    private UiAuditTabsResult Result(bool blocked) => new()
    {
        ProcessId = tree.ProcessId, WindowHandle = window, RootRuntimeId = tree.RootRuntimeId,
        TreeSignature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_treeMetadata.ToString()))),
        Status = blocked ? "blocked" : _partial ? "partial" : "captured",
        MaxDepth = options.MaxDepth, MaxElements = options.MaxElements,
        DepthLimitReached = _depthLimit, ElementLimitReached = _elementLimit,
        Containers = _tabs.Select(tab => tab.Container with { Items = tab.Items.Select(item => item.Item).ToArray() }).ToArray(),
        Issues = _issues.ToArray(), Selection = _selection,
    };
}
