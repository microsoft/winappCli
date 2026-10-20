// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

internal sealed class NativeUiAuditTabsTree : UiAuditTabsTree<IUIAutomationElement>, IDisposable
{
    private const int UiaNotSupported = unchecked((int)0x80040204);
    private readonly NativeUiAuditSnapshotComScope _owned = new();
    private readonly NativeUiAuditSnapshotScope _scope;
    private readonly IUIAutomation _automation;
    private readonly IUIAutomationTreeWalker _walker;
    private readonly UiAuditTabsOptions _options;
    private readonly Action _check;
    private string? _selectionReadbackIssue;

    internal override IUIAutomationElement Root { get; }
    internal override int ProcessId => _scope.ProcessId;
    internal override string RootRuntimeId { get; }
    internal override string? SelectionReadbackIssue => _selectionReadbackIssue;

    internal unsafe NativeUiAuditTabsTree(long window, UiAuditTabsOptions options, Action check)
    {
        _check = check;
        _options = options;
        check();
        uint pid = 0;
        PInvoke.GetWindowThreadProcessId(new HWND(checked((nint)window)), &pid);
        _scope = new NativeUiAuditSnapshotScope(new() { WindowHandle = window, ProcessId = checked((int)pid) });
        try
        {
            check();
            _automation = _owned.Add(CUIAutomation8.CreateInstance<IUIAutomation>())!;
            check();
            Root = _owned.Add(_automation.ElementFromHandle(_scope.Hwnd))
                ?? throw Block("uia_root_unavailable");
            _walker = _owned.Add(_automation.get_RawViewWalker())!;
            RootRuntimeId = RuntimeId(Root);
            VerifyRoot();
        }
        catch
        {
            _owned.Dispose();
            _scope.Dispose();
            throw;
        }
    }

    internal override IUIAutomationElement? FirstChild(IUIAutomationElement node)
    {
        _check();
        return _owned.Add(_walker.GetFirstChildElement(node));
    }

    internal override IUIAutomationElement? NextSibling(IUIAutomationElement node)
    {
        _check();
        return _owned.Add(_walker.GetNextSiblingElement(node));
    }

    internal override UiAuditTabRead Read(IUIAutomationElement node, IUIAutomationElement? parent, IUIAutomationElement? nearestTab)
    {
        using var owned = new NativeUiAuditSnapshotComScope(_owned);
        VerifyNode(node);
        if (parent is not null)
        {
            _check();
            var actualParent = owned.Add(_walker.GetParentElement(node));
            if (actualParent is null || !Same(actualParent, parent)) { throw Block("raw_parent_changed"); }
        }
        var issues = new List<string>();
        var type = ReadRequired(() => ControlType(node), issues, "control_type_unavailable");
        var kind = type == UIA_CONTROLTYPE_ID.UIA_TabControlTypeId ? UiAuditTabKind.Tab
            : type == UIA_CONTROLTYPE_ID.UIA_TabItemControlTypeId ? UiAuditTabKind.TabItem : UiAuditTabKind.Other;
        var id = ReadMetadata(() => RuntimeId(node), issues, "runtime_id_unavailable") ?? "";
        bool? enabled = null;
        bool? offscreen = null;
        try
        {
            enabled = OptionalBoolean(node, UIA_PROPERTY_ID.UIA_IsEnabledPropertyId);
            offscreen = OptionalBoolean(node, UIA_PROPERTY_ID.UIA_IsOffscreenPropertyId);
        }
        catch (Exception ex) when (NativeUiAuditSnapshotReader.IsProviderFailure(ex))
        {
            issues.Add("structural_state_unreadable");
        }
        var realized = false;
        string? automationId = null;
        var single = false;
        var selectionItem = false;
        bool? selected = null;
        var relationship = false;
        if (kind is UiAuditTabKind.Tab or UiAuditTabKind.TabItem)
        {
            // Overflow tabs can legitimately expose no bounds. They are never selection targets.
            realized = enabled == true && offscreen == false
                && ReadRequired(() => HasBounds(node), issues, "bounds_unavailable") == true;
            automationId = ReadMetadata(() => OptionalString(node, UIA_PROPERTY_ID.UIA_AutomationIdPropertyId), issues, "automation_id_unavailable");
            try
            {
                if (kind == UiAuditTabKind.Tab)
                {
                    var pattern = Pattern<IUIAutomationSelectionPattern>(node, UIA_PATTERN_ID.UIA_SelectionPatternId, owned);
                    _check();
                    single = pattern is not null && !pattern.get_CurrentCanSelectMultiple();
                }
                else
                {
                    var pattern = Pattern<IUIAutomationSelectionItemPattern>(node, UIA_PATTERN_ID.UIA_SelectionItemPatternId, owned);
                    selectionItem = pattern is not null;
                    if (pattern is not null)
                    {
                        _check();
                        selected = pattern.get_CurrentIsSelected();
                        relationship = nearestTab is not null && SelectionOwner(node, nearestTab, pattern, owned) is not null;
                    }
                }
            }
            catch (Exception ex) when (ex is UiAuditSnapshotException || NativeUiAuditSnapshotReader.IsProviderFailure(ex))
            {
                issues.Add("selection_pattern_unreadable");
            }
        }
        var modal = false;
        if (!Same(node, Root))
        {
            try
            {
                modal = IsModal(node, type);
            }
            catch (Exception ex) when (ex is UiAuditSnapshotException || NativeUiAuditSnapshotReader.IsProviderFailure(ex))
            {
                issues.Add("modal_state_unavailable");
            }
        }
        return new()
        {
            Kind = kind, ControlType = type is { } controlType ? (int)controlType : null,
            RuntimeId = id, AutomationId = automationId, Enabled = enabled, Offscreen = offscreen,
            Realized = realized, SingleSelection = single, SelectionItem = selectionItem,
            Selected = selected, SelectionContainerMatches = relationship, Modal = modal, Issues = issues,
        };
    }

    internal override void VerifyRoot()
    {
        _check();
        _scope.Verify();
        using var owned = new NativeUiAuditSnapshotComScope(_owned);
        var current = owned.Add(_automation.ElementFromHandle(_scope.Hwnd)) ?? throw Block("uia_root_unavailable");
        VerifyNode(Root);
        VerifyNode(current);
        _check();
        if (Root.get_CurrentNativeWindowHandle() != _scope.Hwnd || current.get_CurrentNativeWindowHandle() != _scope.Hwnd
            || !Same(Root, current) || RuntimeId(current) != RootRuntimeId || RuntimeId(Root) != RootRuntimeId)
        {
            throw Block("root_identity_changed");
        }
    }

    internal override bool HasVisibleOwnedWindow()
    {
        _check();
        _scope.Verify();
        var seen = new HashSet<nint>();
        var window = PInvoke.GetWindow(_scope.Hwnd, GET_WINDOW_CMD.GW_HWNDFIRST);
        for (var count = 0; !window.IsNull; count++)
        {
            _check();
            if (count >= _options.MaxElements || !seen.Add((nint)window)) { throw Block("owned_window_inventory_incomplete"); }
            if (window != _scope.Hwnd && PInvoke.IsWindowVisible(window))
            {
                var owner = PInvoke.GetWindow(window, GET_WINDOW_CMD.GW_OWNER);
                for (var depth = 0; !owner.IsNull; depth++)
                {
                    _check();
                    if (owner == _scope.Hwnd) { return true; }
                    if (depth >= _options.MaxDepth) { throw Block("owned_window_ancestry_incomplete"); }
                    owner = PInvoke.GetWindow(owner, GET_WINDOW_CMD.GW_OWNER);
                }
            }
            window = PInvoke.GetWindow(window, GET_WINDOW_CMD.GW_HWNDNEXT);
        }
        return false;
    }

    internal override void VerifyNoModal()
    {
        using var owned = new NativeUiAuditSnapshotComScope(_owned);
        var examined = 0;
        void Visit(IUIAutomationElement node, int depth)
        {
            _check();
            if (++examined > _options.MaxElements) { throw Block("modal_inventory_element_limit"); }
            VerifyNode(node);
            if (!Same(node, Root) && IsModal(node, ControlType(node))) { throw Block("in_tree_modal_or_popup"); }
            _check();
            var child = owned.Add(_walker.GetFirstChildElement(node));
            if (child is not null && depth >= _options.MaxDepth) { throw Block("modal_inventory_depth_limit"); }
            while (child is not null)
            {
                Visit(child, depth + 1);
                _check();
                child = owned.Add(_walker.GetNextSiblingElement(child));
            }
        }
        VerifyRoot();
        Visit(Root, 0);
    }

    private bool IsModal(IUIAutomationElement node, UIA_CONTROLTYPE_ID? type)
    {
        var dialog = OptionalBoolean(node, UIA_PROPERTY_ID.UIA_IsDialogPropertyId) == true;
        var windowModal = OptionalBoolean(node, UIA_PROPERTY_ID.UIA_WindowIsModalPropertyId) == true;
        var popup = type == UIA_CONTROLTYPE_ID.UIA_MenuControlTypeId;
        if (type is UIA_CONTROLTYPE_ID.UIA_PaneControlTypeId or UIA_CONTROLTYPE_ID.UIA_CustomControlTypeId)
        {
            var className = OptionalString(node, UIA_PROPERTY_ID.UIA_ClassNamePropertyId);
            popup |= className is not null && (className.Equals("Popup", StringComparison.OrdinalIgnoreCase)
                || className.EndsWith(".Popup", StringComparison.OrdinalIgnoreCase));
        }
        // Unknown visibility cannot establish that a possible dialog is safely hidden.
        return (dialog || windowModal || popup || type == UIA_CONTROLTYPE_ID.UIA_WindowControlTypeId)
            && OptionalBoolean(node, UIA_PROPERTY_ID.UIA_IsOffscreenPropertyId) != true;
    }

    internal override void Select(IUIAutomationElement tab, IUIAutomationElement item, string tabId, string itemId)
    {
        VerifyNoModal();
        using var owned = new NativeUiAuditSnapshotComScope(_owned);
        var (pattern, _) = VerifySelectionPair(tab, item, tabId, itemId, owned);
        if (HasVisibleOwnedWindow()) { throw Block("modal_or_visible_owned_window"); }
        VerifyRoot();
        _check();
        pattern.Select();
    }

    internal override bool ConfirmSelected(IUIAutomationElement tab, IUIAutomationElement item, string tabId, string itemId)
    {
        using var owned = new NativeUiAuditSnapshotComScope(_owned);
        var (pattern, _) = VerifySelectionPair(tab, item, tabId, itemId, owned);
        _check();
        var selected = (bool)pattern.get_CurrentIsSelected();
        var propertySelected = OptionalBoolean(item, UIA_PROPERTY_ID.UIA_SelectionItemIsSelectedPropertyId);
        // Selection.GetCurrentSelection may expose a different peer than the standard TabItem
        // (including with TabView's inner ListView). Confirm the exact item's own state instead.
        _selectionReadbackIssue = propertySelected is null ? "selection_state_unavailable"
            : selected != propertySelected ? "selection_state_disagrees" : "selection_item_not_selected";
        return selected && propertySelected == true;
    }

    private (IUIAutomationSelectionItemPattern Pattern, IUIAutomationElement Owner) VerifySelectionPair(
        IUIAutomationElement tab, IUIAutomationElement item, string tabId, string itemId, NativeUiAuditSnapshotComScope owned)
    {
        VerifyRoot();
        if (!PInvoke.IsWindowVisible(_scope.Hwnd) || PInvoke.IsIconic(_scope.Hwnd)
            || !PInvoke.IsWindowEnabled(_scope.Hwnd))
        {
            throw Block("root_hidden_or_disabled");
        }
        if (RuntimeId(tab) != tabId || RuntimeId(item) != itemId
            || ControlType(tab) != UIA_CONTROLTYPE_ID.UIA_TabControlTypeId || ControlType(item) != UIA_CONTROLTYPE_ID.UIA_TabItemControlTypeId)
        {
            throw Block("exact_standard_tab_identity_required");
        }
        foreach (var node in new[] { tab, item })
        {
            VerifyNode(node);
            if (!Boolean(node, UIA_PROPERTY_ID.UIA_IsEnabledPropertyId)
                || Boolean(node, UIA_PROPERTY_ID.UIA_IsOffscreenPropertyId) || !HasBounds(node))
            {
                throw Block("tab_disabled_offscreen_or_unrealized");
            }
        }
        var tabPattern = Pattern<IUIAutomationSelectionPattern>(tab, UIA_PATTERN_ID.UIA_SelectionPatternId, owned)
            ?? throw Block("selection_pattern_required");
        _check();
        if (tabPattern.get_CurrentCanSelectMultiple()) { throw Block("single_selection_required"); }
        var itemPattern = Pattern<IUIAutomationSelectionItemPattern>(item, UIA_PATTERN_ID.UIA_SelectionItemPatternId, owned)
            ?? throw Block("selection_item_pattern_required");
        var owner = SelectionOwner(item, tab, itemPattern, owned) ?? throw Block("selection_container_mismatch");
        if (!Boolean(owner, UIA_PROPERTY_ID.UIA_IsEnabledPropertyId)
            || Boolean(owner, UIA_PROPERTY_ID.UIA_IsOffscreenPropertyId) || !HasBounds(owner))
        {
            throw Block("selection_container_disabled_or_hidden");
        }
        return (itemPattern, owner);
    }

    private IUIAutomationElement? SelectionOwner(IUIAutomationElement item, IUIAutomationElement tab,
        IUIAutomationSelectionItemPattern pattern, NativeUiAuditSnapshotComScope owned)
    {
        var itemPath = ScopedPath(item, owned);
        var nearest = itemPath.Skip(1).FirstOrDefault(node => ControlType(node) == UIA_CONTROLTYPE_ID.UIA_TabControlTypeId);
        if (nearest is null || !Same(nearest, tab)) { return null; }
        _check();
        var owner = owned.Add(pattern.get_CurrentSelectionContainer());
        if (owner is null || !itemPath.Skip(1).Any(node => Same(node, owner))) { return null; }
        var ownerPath = ScopedPath(owner, owned);
        var ownerTab = ownerPath.FirstOrDefault(node => ControlType(node) == UIA_CONTROLTYPE_ID.UIA_TabControlTypeId);
        if (ownerTab is null || !Same(ownerTab, tab)) { return null; }
        var selection = Pattern<IUIAutomationSelectionPattern>(owner, UIA_PATTERN_ID.UIA_SelectionPatternId, owned);
        _check();
        return selection is not null && !selection.get_CurrentCanSelectMultiple() ? owner : null;
    }

    private List<IUIAutomationElement> ScopedPath(IUIAutomationElement node, NativeUiAuditSnapshotComScope owned)
    {
        var path = new List<IUIAutomationElement>();
        IUIAutomationElement? current = node;
        for (var depth = 0; current is not null && depth <= _options.MaxDepth; depth++)
        {
            _check();
            VerifyNode(current);
            path.Add(current);
            if (Same(current, Root)) { return path; }
            current = owned.Add(_walker.GetParentElement(current));
        }
        throw Block("bounded_root_ancestry_unverified");
    }

    private void VerifyNode(IUIAutomationElement node)
    {
        _check();
        _scope.Verify();
        using var process = node.GetCurrentPropertyValueEx(UIA_PROPERTY_ID.UIA_ProcessIdPropertyId, true);
        if (process.VarType != VarEnum.VT_I4 || process.As<int>() != ProcessId) { throw Block("node_process_mismatch"); }
        _check();
        var native = node.get_CurrentNativeWindowHandle();
        if (!native.IsNull && PInvoke.GetAncestor(native, GET_ANCESTOR_FLAGS.GA_ROOT) != _scope.Hwnd)
        {
            throw Block("node_window_boundary");
        }
    }

    private UIA_CONTROLTYPE_ID ControlType(IUIAutomationElement node)
    {
        _check();
        using var type = node.GetCurrentPropertyValueEx(UIA_PROPERTY_ID.UIA_ControlTypePropertyId, true);
        return type.VarType == VarEnum.VT_I4 ? (UIA_CONTROLTYPE_ID)type.As<int>() : throw Block("control_type_unavailable");
    }

    private bool Same(IUIAutomationElement first, IUIAutomationElement second)
    {
        _check();
        return _automation.CompareElements(first, second);
    }

    private bool Boolean(IUIAutomationElement node, UIA_PROPERTY_ID property) =>
        OptionalBoolean(node, property) ?? throw Block("required_boolean_unavailable");

    private bool? OptionalBoolean(IUIAutomationElement node, UIA_PROPERTY_ID property)
    {
        _check();
        using var value = node.GetCurrentPropertyValueEx(property, true);
        return value.VarType == VarEnum.VT_BOOL ? value.As<bool>() : null;
    }

    private unsafe string? OptionalString(IUIAutomationElement node, UIA_PROPERTY_ID property)
    {
        _check();
        using var value = node.GetCurrentPropertyValueEx(property, true);
        if (value.VarType != VarEnum.VT_BSTR) { return null; }
        var text = value.GetRawDataRef<BSTR>().AsSpan();
        if (text.Length > 1024) { throw Block("metadata_length_limit"); }
        return text.Length == 0 ? null : new string(text);
    }

    private unsafe bool HasBounds(IUIAutomationElement node)
    {
        _check();
        using var value = node.GetCurrentPropertyValueEx(UIA_PROPERTY_ID.UIA_BoundingRectanglePropertyId, true);
        if (value.VarType != (VarEnum.VT_ARRAY | VarEnum.VT_R8)) { throw Block("bounds_unavailable"); }
        var bounds = NativeUiAuditSnapshotArrays.ReadDoubles((global::Windows.Win32.System.Com.SAFEARRAY*)value.GetRawDataRef<nint>(), 4, out var truncated);
        if (truncated || bounds.Length != 4 || bounds.Any(number => !double.IsFinite(number))) { throw Block("bounds_unavailable"); }
        return bounds[2] > 0 && bounds[3] > 0;
    }

    private unsafe string RuntimeId(IUIAutomationElement node)
    {
        _check();
        var array = node.GetRuntimeId();
        try
        {
            var values = NativeUiAuditSnapshotArrays.ReadIntegers(array, 128, out var truncated);
            return !truncated ? UiAuditSnapshotTraversal.RuntimeId(values) ?? throw Block("runtime_id_unavailable")
                : throw Block("runtime_id_length_limit");
        }
        finally
        {
            if (array is not null) { PInvoke.SafeArrayDestroy(array).ThrowOnFailure(); }
        }
    }

    private T? Pattern<T>(IUIAutomationElement node, UIA_PATTERN_ID id, NativeUiAuditSnapshotComScope owned) where T : class
    {
        _check();
        try { return owned.Add(node.GetCurrentPattern(id)) as T; }
        catch (COMException ex) when (ex.HResult == UiaNotSupported) { return null; }
    }

    private static T? ReadRequired<T>(Func<T> read, List<string> issues, string issue) where T : struct
    {
        try { return read(); }
        catch (Exception ex) when (ex is UiAuditSnapshotException || NativeUiAuditSnapshotReader.IsProviderFailure(ex)) { issues.Add(issue); return null; }
    }

    private static string? ReadMetadata(Func<string?> read, List<string> issues, string issue)
    {
        try { return read(); }
        catch (Exception ex) when (ex is UiAuditSnapshotException || NativeUiAuditSnapshotReader.IsProviderFailure(ex)) { issues.Add(issue); return null; }
    }

    private static UiAuditSnapshotException Block(string code) => new(code, code);

    public void Dispose()
    {
        _owned.Dispose();
        _scope.Dispose();
    }
}
