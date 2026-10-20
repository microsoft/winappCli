// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

internal sealed partial class NativeUiAuditSnapshotReader : UiAuditSnapshotTree<IUIAutomationElement>, IDisposable
{
    private const int MaximumStringLength = 1024;
    private static readonly Guid IUnknownId = new("00000000-0000-0000-C000-000000000046");
    private readonly NativeUiAuditSnapshotScope _scope;
    private readonly NativeUiAuditSnapshotComScope _owned = new();
    private readonly UiAuditSnapshotState _state;
    private readonly IUIAutomation _automation;
    private readonly IUIAutomationTreeWalker _walker;
    private object? _mixedAttributeValue;

    private static readonly (UIA_PROPERTY_ID Property, string Name)[] PatternProperties =
    [
        (UIA_PROPERTY_ID.UIA_IsInvokePatternAvailablePropertyId, "Invoke"),
        (UIA_PROPERTY_ID.UIA_IsSelectionPatternAvailablePropertyId, "Selection"),
        (UIA_PROPERTY_ID.UIA_IsValuePatternAvailablePropertyId, "Value"),
        (UIA_PROPERTY_ID.UIA_IsRangeValuePatternAvailablePropertyId, "RangeValue"),
        (UIA_PROPERTY_ID.UIA_IsScrollPatternAvailablePropertyId, "Scroll"),
        (UIA_PROPERTY_ID.UIA_IsExpandCollapsePatternAvailablePropertyId, "ExpandCollapse"),
        (UIA_PROPERTY_ID.UIA_IsGridPatternAvailablePropertyId, "Grid"),
        (UIA_PROPERTY_ID.UIA_IsGridItemPatternAvailablePropertyId, "GridItem"),
        (UIA_PROPERTY_ID.UIA_IsMultipleViewPatternAvailablePropertyId, "MultipleView"),
        (UIA_PROPERTY_ID.UIA_IsWindowPatternAvailablePropertyId, "Window"),
        (UIA_PROPERTY_ID.UIA_IsSelectionItemPatternAvailablePropertyId, "SelectionItem"),
        (UIA_PROPERTY_ID.UIA_IsDockPatternAvailablePropertyId, "Dock"),
        (UIA_PROPERTY_ID.UIA_IsTablePatternAvailablePropertyId, "Table"),
        (UIA_PROPERTY_ID.UIA_IsTableItemPatternAvailablePropertyId, "TableItem"),
        (UIA_PROPERTY_ID.UIA_IsTextPatternAvailablePropertyId, "Text"),
        (UIA_PROPERTY_ID.UIA_IsTogglePatternAvailablePropertyId, "Toggle"),
        (UIA_PROPERTY_ID.UIA_IsTransformPatternAvailablePropertyId, "Transform"),
        (UIA_PROPERTY_ID.UIA_IsScrollItemPatternAvailablePropertyId, "ScrollItem"),
        (UIA_PROPERTY_ID.UIA_IsLegacyIAccessiblePatternAvailablePropertyId, "LegacyIAccessible"),
        (UIA_PROPERTY_ID.UIA_IsItemContainerPatternAvailablePropertyId, "ItemContainer"),
        (UIA_PROPERTY_ID.UIA_IsVirtualizedItemPatternAvailablePropertyId, "VirtualizedItem"),
        (UIA_PROPERTY_ID.UIA_IsSynchronizedInputPatternAvailablePropertyId, "SynchronizedInput"),
        (UIA_PROPERTY_ID.UIA_IsObjectModelPatternAvailablePropertyId, "ObjectModel"),
        (UIA_PROPERTY_ID.UIA_IsAnnotationPatternAvailablePropertyId, "Annotation"),
        (UIA_PROPERTY_ID.UIA_IsTextPattern2AvailablePropertyId, "Text2"),
        (UIA_PROPERTY_ID.UIA_IsStylesPatternAvailablePropertyId, "Styles"),
        (UIA_PROPERTY_ID.UIA_IsSpreadsheetPatternAvailablePropertyId, "Spreadsheet"),
        (UIA_PROPERTY_ID.UIA_IsSpreadsheetItemPatternAvailablePropertyId, "SpreadsheetItem"),
        (UIA_PROPERTY_ID.UIA_IsTransformPattern2AvailablePropertyId, "Transform2"),
        (UIA_PROPERTY_ID.UIA_IsTextChildPatternAvailablePropertyId, "TextChild"),
        (UIA_PROPERTY_ID.UIA_IsDragPatternAvailablePropertyId, "Drag"),
        (UIA_PROPERTY_ID.UIA_IsDropTargetPatternAvailablePropertyId, "DropTarget"),
        (UIA_PROPERTY_ID.UIA_IsTextEditPatternAvailablePropertyId, "TextEdit"),
        (UIA_PROPERTY_ID.UIA_IsCustomNavigationPatternAvailablePropertyId, "CustomNavigation"),
        (UIA_PROPERTY_ID.UIA_IsSelectionPattern2AvailablePropertyId, "Selection2"),
    ];

    internal IUIAutomationElement Root { get; }
    internal string? InitialRuntimeId { get; }

    internal NativeUiAuditSnapshotReader(UiTarget target, UiAuditSnapshotState state)
    {
        _state = state;
        state.Budget.Check();
        _scope = new NativeUiAuditSnapshotScope(target);
        try
        {
            state.Budget.Check();
            _automation = _owned.Add(CUIAutomation8.CreateInstance<IUIAutomation>())
                ?? throw new UiAuditSnapshotException("uia_unavailable", "UI Automation could not be created.");
            state.Budget.Check();
            Root = _owned.Add(_automation.ElementFromHandle(_scope.Hwnd))
                ?? throw new UiAuditSnapshotException("invalid_target", "The selected window has no UIA root.");
            VerifyRootIdentity(Root);
            state.Budget.Check();
            _walker = _owned.Add(_automation.get_RawViewWalker())
                ?? throw new UiAuditSnapshotException("uia_unavailable", "UI Automation did not provide a raw-view tree walker.");
            InitialRuntimeId = ReadRuntimeId(Root, state.Issues);
            _scope.Verify();
        }
        catch
        {
            _owned.Dispose();
            _scope.Dispose();
            throw;
        }
    }

    internal override IUIAutomationElement? FirstChild(IUIAutomationElement parent)
    {
        _state.Budget.Check();
        return _walker.GetFirstChildElement(parent);
    }

    internal override IUIAutomationElement? NextSibling(IUIAutomationElement node)
    {
        _state.Budget.Check();
        return _walker.GetNextSiblingElement(node);
    }

    internal override void Release(IUIAutomationElement node)
    {
        if (!ReferenceEquals(node, Root))
        {
            NativeUiAuditSnapshotComScope.Release(node);
        }
    }

    internal override UiAuditNode? ReadNode(IUIAutomationElement element, IUIAutomationElement? parent,
        UiAuditNode header, bool inheritedWithholding)
    {
        _state.Budget.Check();
        _scope.Verify();
        using var owned = new NativeUiAuditSnapshotComScope(Root, _walker, _automation, element, parent);
        if (!VerifyNodeScope(element, parent, owned))
        {
            return null;
        }

        var issues = new List<UiAuditSnapshotIssue>();
        var privacy = ReadPrivacy(element, issues);
        var withhold = UiAuditSnapshotTraversal.ShouldWithhold(inheritedWithholding, privacy);
        if (withhold)
        {
            _state.Issue(issues, "privacy_withheld", "Accessible name and text details were withheld because this node or an ancestor is password-protected or has indeterminate privacy.",
                partial: false);
        }
        var node = header with
        {
            RuntimeId = parent is null ? InitialRuntimeId : ReadRuntimeId(element, issues),
            IsPassword = privacy,
            AutomationId = ReadString(element, UIA_PROPERTY_ID.UIA_AutomationIdPropertyId, "AutomationId", issues),
            Name = withhold ? null : ReadString(element, UIA_PROPERTY_ID.UIA_NamePropertyId, "Name", issues),
            ControlType = ReadControlType(element, issues),
            FrameworkId = ReadString(element, UIA_PROPERTY_ID.UIA_FrameworkIdPropertyId, "FrameworkId", issues),
            ClassName = ReadString(element, UIA_PROPERTY_ID.UIA_ClassNamePropertyId, "ClassName", issues),
            IsEnabled = ReadBoolean(element, UIA_PROPERTY_ID.UIA_IsEnabledPropertyId, "IsEnabled", issues),
            IsKeyboardFocusable = ReadBoolean(element, UIA_PROPERTY_ID.UIA_IsKeyboardFocusablePropertyId, "IsKeyboardFocusable", issues),
            HasKeyboardFocus = ReadBoolean(element, UIA_PROPERTY_ID.UIA_HasKeyboardFocusPropertyId, "HasKeyboardFocus", issues),
            IsOffscreen = ReadBoolean(element, UIA_PROPERTY_ID.UIA_IsOffscreenPropertyId, "IsOffscreen", issues),
            Bounds = ReadBounds(element, issues),
        };

        var patterns = new List<string>();
        foreach (var pattern in PatternProperties)
        {
            if (ReadBoolean(element, pattern.Property, pattern.Name + " pattern availability", issues) == true)
            {
                patterns.Add(pattern.Name);
            }
        }
        node = node with { Patterns = patterns.ToArray() };
        if (patterns.Contains("Text", StringComparer.Ordinal))
        {
            node = node with { TextRuns = withhold ? [WithheldRun(node.Id + ".t1")] : ReadTextRuns(element, node.Id, issues, owned) };
        }

        if (!withhold)
        {
            var finalPrivacy = ReadPrivacy(element, issues);
            if (finalPrivacy != false)
            {
                _state.Issue(issues, "privacy_changed", "The node's privacy could not be reconfirmed after capture; its name and text details were discarded.");
                node = Withhold(node) with { IsPassword = finalPrivacy };
            }
        }
        if (!VerifyNodeScope(element, parent, owned))
        {
            return null;
        }
        return node with { Issues = issues.ToArray() };
    }

    private bool VerifyNodeScope(IUIAutomationElement element, IUIAutomationElement? parent, NativeUiAuditSnapshotComScope owned)
    {
        try
        {
            _state.Budget.Check();
            using var process = element.GetCurrentPropertyValueEx(UIA_PROPERTY_ID.UIA_ProcessIdPropertyId, true);
            if (process.VarType != VarEnum.VT_I4 || process.As<int>() != _scope.ProcessId)
            {
                _state.Issue(_state.Issues, "node_out_of_scope", "A raw-tree element with an unavailable or different process ID was omitted with its subtree.");
                return false;
            }
            if (!VerifyNativeWindowScope(element))
            {
                return false;
            }
            if (parent is not null)
            {
                _state.Budget.Check();
                var actualParent = owned.Add(_walker.GetParentElement(element));
                _state.Budget.Check();
                if (actualParent is null || !_automation.CompareElements(actualParent, parent))
                {
                    _state.Issue(_state.Issues, "node_out_of_scope", "A raw-tree parent link could not be verified; the element and its subtree were omitted.");
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex) when (IsProviderFailure(ex))
        {
            _state.Issue(_state.Issues, "node_identity_unavailable", FailureMessage("Element process and raw-parent identity", ex));
            return false;
        }
    }

    private bool VerifyNativeWindowScope(IUIAutomationElement element)
    {
        using var owned = new NativeUiAuditSnapshotComScope(Root, _walker, _automation, element);
        var nativeWindow = UiAuditSnapshotTraversal.NearestNativeWindow(element, _state.Options.MaxDepth,
            current =>
            {
                using var process = current.GetCurrentPropertyValueEx(UIA_PROPERTY_ID.UIA_ProcessIdPropertyId, true);
                if (process.VarType != VarEnum.VT_I4 || process.As<int>() != _scope.ProcessId)
                {
                    throw new InvalidOperationException("Native-window ancestry could not be verified in the selected process.");
                }
                _state.Budget.Check();
                return (long)(nint)current.get_CurrentNativeWindowHandle();
            },
            current =>
            {
                if (_automation.CompareElements(current, Root))
                {
                    return null;
                }
                _state.Budget.Check();
                return owned.Add(_walker.GetParentElement(current));
            },
            _state.Budget.Check);
        if (nativeWindow is null)
        {
            _state.Issue(_state.Issues, "node_window_unavailable",
                "No native window could be verified in the bounded raw ancestry; the element and its subtree were omitted.");
            return false;
        }
        // GA_ROOT follows native parent links, not owner links: child HWNDs remain in scope,
        // while an owned top-level dialog is a different window even when UIA nests it here.
        _state.Budget.Check();
        var nativeRoot = PInvoke.GetAncestor(new HWND((nint)nativeWindow.Value), GET_ANCESTOR_FLAGS.GA_ROOT);
        if (nativeRoot.IsNull)
        {
            _state.Issue(_state.Issues, "node_window_unavailable", "An element's native window root could not be verified; its subtree was omitted.");
            return false;
        }
        if (nativeRoot != _scope.Hwnd)
        {
            _state.Issue(_state.Issues, "window_boundary_excluded",
                FormattableString.Invariant($"Another top-level window (HWND {(nint)nativeRoot}) and its descendants were excluded from this snapshot."),
                partial: false, excludedWindowHandle: (long)(nint)nativeRoot);
            return false;
        }
        return true;
    }

    private void VerifyRootIdentity(IUIAutomationElement root)
    {
        _scope.Verify();
        _state.Budget.Check();
        using var process = root.GetCurrentPropertyValueEx(UIA_PROPERTY_ID.UIA_ProcessIdPropertyId, true);
        if (process.VarType != VarEnum.VT_I4 || process.As<int>() != _scope.ProcessId)
        {
            throw new UiAuditSnapshotException("target_mismatch", "The UIA root does not report the exact selected process ID.");
        }
        _state.Budget.Check();
        if (root.get_CurrentNativeWindowHandle() != _scope.Hwnd)
        {
            throw new UiAuditSnapshotException("target_mismatch", "The UIA root does not identify the exact selected HWND.");
        }
    }

    internal void VerifyNativeTarget() => _scope.Verify();

    internal void VerifyFinalRoot()
    {
        using var owned = new NativeUiAuditSnapshotComScope(Root, _walker, _automation);
        _state.Budget.Check();
        var currentRoot = owned.Add(_automation.ElementFromHandle(_scope.Hwnd));
        if (currentRoot is null)
        {
            throw new UiAuditSnapshotException("target_changed", "The selected HWND no longer has a UIA root.");
        }
        VerifyRootIdentity(currentRoot);
        VerifyRootIdentity(Root);
        _state.Budget.Check();
        if (!_automation.CompareElements(Root, currentRoot))
        {
            throw new UiAuditSnapshotException("target_changed", "The selected HWND's UIA root changed during capture.");
        }
        var runtimeId = ReadRuntimeId(currentRoot, _state.Issues);
        if (InitialRuntimeId is not null && runtimeId is not null && InitialRuntimeId != runtimeId)
        {
            throw new UiAuditSnapshotException("target_changed", "The selected HWND's root runtime ID changed during capture.");
        }
        var privacy = ReadPrivacy(currentRoot, _state.Issues);
        if (privacy != false)
        {
            var changed = _state.Nodes.Count > 0 && _state.Nodes[0].IsPassword != privacy;
            for (var index = 0; index < _state.Nodes.Count; index++)
            {
                _state.Nodes[index] = Withhold(_state.Nodes[index]);
            }
            if (_state.Nodes.Count > 0)
            {
                _state.Nodes[0] = _state.Nodes[0] with { IsPassword = privacy };
            }
            _state.Issue(_state.Issues, "root_privacy_withheld", "The root's final privacy status requires withholding all captured names and text details.",
                partial: privacy is null || changed);
        }
        _scope.Verify();
    }

    private bool? ReadPrivacy(IUIAutomationElement element, List<UiAuditSnapshotIssue> issues)
    {
        try
        {
            _state.Budget.Check();
            if (element.get_CurrentIsPassword())
            {
                return true;
            }
        }
        catch (Exception ex) when (IsProviderFailure(ex))
        {
            _state.Issue(issues, "privacy_unavailable", FailureMessage("Direct IsPassword", ex));
            return null;
        }
        // UIA's typed getter can supply false for an unsupported property. Require an explicit
        // provider Boolean, with defaults disabled, before allowing any name or text-range read.
        var privacy = ReadBoolean(element, UIA_PROPERTY_ID.UIA_IsPasswordPropertyId, "IsPassword", issues);
        if (privacy is null)
        {
            _state.Issue(issues, "privacy_unavailable", "The provider did not supply an explicit IsPassword Boolean; sensitive details were withheld.");
        }
        return privacy;
    }

    private bool? ReadBoolean(IUIAutomationElement element, UIA_PROPERTY_ID property, string label, List<UiAuditSnapshotIssue> issues)
    {
        try
        {
            _state.Budget.Check();
            using var value = element.GetCurrentPropertyValueEx(property, true);
            var field = UiAuditFieldReader.Read(value, UiAuditFieldKind.Boolean, ClassifySentinel(value));
            RecordFieldIssue(field, label, issues);
            return field.Boolean;
        }
        catch (Exception ex) when (IsProviderFailure(ex))
        {
            _state.Issue(issues, "property_error", FailureMessage(label, ex));
            return null;
        }
    }

    private unsafe string? ReadString(IUIAutomationElement element, UIA_PROPERTY_ID property, string label, List<UiAuditSnapshotIssue> issues)
    {
        try
        {
            _state.Budget.Check();
            using var value = element.GetCurrentPropertyValueEx(property, true);
            if (Unavailable(value, label, issues))
            {
                return null;
            }
            if (value.VarType != VarEnum.VT_BSTR)
            {
                _state.Issue(issues, "property_error", label + " did not contain a BSTR.");
                return null;
            }
            var text = value.GetRawDataRef<BSTR>().AsSpan();
            var length = Math.Min(text.Length, MaximumStringLength);
            if (text.Length > length)
            {
                if (property != UIA_PROPERTY_ID.UIA_NamePropertyId)
                {
                    _state.Issue(issues, "property_truncated", label + " exceeded the 1024-character metadata bound and was omitted rather than used as a shortened identifier.");
                    return null;
                }
                _state.Issue(issues, "property_truncated", label + " exceeded the 1024-character metadata bound and was truncated.");
                if (char.IsHighSurrogate(text[length - 1]))
                {
                    length--;
                }
            }
            return new string(text[..length]);
        }
        catch (Exception ex) when (IsProviderFailure(ex))
        {
            _state.Issue(issues, "property_error", FailureMessage(label, ex));
            return null;
        }
    }

    private string ReadControlType(IUIAutomationElement element, List<UiAuditSnapshotIssue> issues)
    {
        try
        {
            _state.Budget.Check();
            using var value = element.GetCurrentPropertyValueEx(UIA_PROPERTY_ID.UIA_ControlTypePropertyId, true);
            if (Unavailable(value, "ControlType", issues))
            {
                return "Unknown";
            }
            if (value.VarType != VarEnum.VT_I4)
            {
                _state.Issue(issues, "property_error", "ControlType did not contain an integer.");
                return "Unknown";
            }
            var controlType = (UIA_CONTROLTYPE_ID)value.As<int>();
            return controlType == UIA_CONTROLTYPE_ID.UIA_CustomControlTypeId
                ? "Custom" : UiAutomationService.GetControlTypeName(controlType);
        }
        catch (Exception ex) when (IsProviderFailure(ex))
        {
            _state.Issue(issues, "property_error", FailureMessage("ControlType", ex));
            return "Unknown";
        }
    }

    private unsafe UiAuditNodeBounds? ReadBounds(IUIAutomationElement element, List<UiAuditSnapshotIssue> issues)
    {
        try
        {
            _state.Budget.Check();
            using var value = element.GetCurrentPropertyValueEx(UIA_PROPERTY_ID.UIA_BoundingRectanglePropertyId, true);
            if (Unavailable(value, "BoundingRectangle", issues))
            {
                return null;
            }
            if (value.VarType != (VarEnum.VT_ARRAY | VarEnum.VT_R8))
            {
                _state.Issue(issues, "bounds_error", "BoundingRectangle did not contain a double SAFEARRAY.");
                return null;
            }
            var rectangle = NativeUiAuditSnapshotArrays.ReadDoubles((SAFEARRAY*)value.GetRawDataRef<nint>(), 4, out var truncated);
            var bounds = rectangle.Length == 4 && !truncated
                ? UiAuditSnapshotTraversal.Bounds(rectangle[0], rectangle[1], rectangle[2], rectangle[3]) : null;
            if (bounds is null)
            {
                _state.Issue(issues, "bounds_error", "BoundingRectangle was malformed, negative-sized, or nonfinite.");
            }
            return bounds;
        }
        catch (Exception ex) when (IsProviderFailure(ex))
        {
            _state.Issue(issues, "bounds_error", FailureMessage("BoundingRectangle", ex));
            return null;
        }
    }

    private unsafe string? ReadRuntimeId(IUIAutomationElement element, List<UiAuditSnapshotIssue> issues)
    {
        try
        {
            _state.Budget.Check();
            var array = element.GetRuntimeId();
            try
            {
                if (array is null)
                {
                    _state.Issue(issues, "runtime_id_unavailable", "The provider did not supply a runtime ID.", partial: false);
                    return null;
                }
                var values = NativeUiAuditSnapshotArrays.ReadIntegers(array, 128, out var truncated);
                if (truncated)
                {
                    _state.Issue(issues, "runtime_id_limit", "The runtime ID exceeded 128 integers and was omitted rather than shortened.");
                    return null;
                }
                var runtimeId = UiAuditSnapshotTraversal.RuntimeId(values);
                if (runtimeId is null)
                {
                    _state.Issue(issues, "runtime_id_unavailable", "The provider supplied an empty runtime ID.", partial: false);
                }
                return runtimeId;
            }
            finally
            {
                if (array is not null)
                {
                    PInvoke.SafeArrayDestroy(array).ThrowOnFailure();
                }
            }
        }
        catch (Exception ex) when (IsProviderFailure(ex))
        {
            _state.Issue(issues, "runtime_id_error", FailureMessage("RuntimeId", ex));
            return null;
        }
    }

    private bool Unavailable(ComVariant value, string label, List<UiAuditSnapshotIssue> issues)
    {
        var sentinel = ClassifySentinel(value);
        if (value.VarType is VarEnum.VT_EMPTY or VarEnum.VT_NULL || sentinel == UiAuditFieldSentinel.Unsupported)
        {
            _state.Issue(issues, "property_unsupported", label + " is not supplied by the provider.", partial: false);
            return true;
        }
        if (sentinel == UiAuditFieldSentinel.Mixed)
        {
            _state.Issue(issues, "property_mixed", label + " contains a mixed UIA attribute sentinel.", partial: false);
            return true;
        }
        return false;
    }

    private UiAuditFieldSentinel ClassifySentinel(ComVariant value)
    {
        if (value.VarType != VarEnum.VT_UNKNOWN)
        {
            return UiAuditFieldSentinel.None;
        }
        _state.Budget.Check();
        if (_automation.CheckNotSupported(value))
        {
            return UiAuditFieldSentinel.Unsupported;
        }
        if (_mixedAttributeValue is null)
        {
            _state.Budget.Check();
            _mixedAttributeValue = _owned.Add(_automation.get_ReservedMixedAttributeValue());
        }
        return IsSameUnknown(value, _mixedAttributeValue) ? UiAuditFieldSentinel.Mixed : UiAuditFieldSentinel.None;
    }

    internal static unsafe bool IsSameUnknown(ComVariant value, object? expected)
    {
        if (value.VarType != VarEnum.VT_UNKNOWN || expected is null || value.GetRawDataRef<nint>() == 0)
        {
            return false;
        }
        var expectedUnknown = ComInterfaceMarshaller<object>.ConvertToUnmanaged(expected);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(value.GetRawDataRef<nint>(), in IUnknownId, out var identity));
            try
            {
                return identity == (nint)expectedUnknown;
            }
            finally
            {
                Marshal.Release(identity);
            }
        }
        finally
        {
            ComInterfaceMarshaller<object>.Free(expectedUnknown);
        }
    }

    private void RecordFieldIssue(UiAuditField field, string label, List<UiAuditSnapshotIssue> issues)
    {
        if (field.Status != "value")
        {
            _state.Issue(issues, "property_" + field.Status,
                label + ": " + (field.Error ?? field.Status + "."),
                partial: field.Status == "error");
        }
    }

    private static UiAuditNode Withhold(UiAuditNode node) => node with
    {
        Name = null,
        TextRuns = node.TextRuns.Select(run => WithheldRun(run.Id)).ToArray(),
    };

    private static UiAuditNodeTextRun WithheldRun(string id) => new()
    {
        Id = id,
        ForegroundColor = UiAuditFieldReader.Withheld(),
        BackgroundColor = UiAuditFieldReader.Withheld(),
        FontSize = UiAuditFieldReader.Withheld(),
        FontWeight = UiAuditFieldReader.Withheld(),
        IsHidden = UiAuditFieldReader.Withheld(),
    };

    internal static bool IsProviderFailure(Exception ex)
        => ex is not UiAuditSnapshotException
            && ex is COMException or InvalidOperationException or ArgumentException or InvalidCastException or OverflowException or NotSupportedException;

    internal static string FailureMessage(string operation, Exception ex)
        => $"{operation} failed (HRESULT 0x{ex.HResult:X8}); no default value was substituted.";

    public void Dispose()
    {
        _owned.Dispose();
        _scope.Dispose();
    }
}
