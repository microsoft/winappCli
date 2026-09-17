// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Extensions.Logging;
using Windows.Win32.UI.Accessibility;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

internal sealed partial class UiAutomationService
{
    internal static readonly (string Name, UIA_TEXTATTRIBUTE_ID Id)[] TextAttributes =
    [
        ("FontWeight", UIA_TEXTATTRIBUTE_ID.UIA_FontWeightAttributeId),
        ("FontName", UIA_TEXTATTRIBUTE_ID.UIA_FontNameAttributeId),
        ("FontSize", UIA_TEXTATTRIBUTE_ID.UIA_FontSizeAttributeId),
        ("ForegroundColor", UIA_TEXTATTRIBUTE_ID.UIA_ForegroundColorAttributeId),
        ("IsItalic", UIA_TEXTATTRIBUTE_ID.UIA_IsItalicAttributeId),
        ("StrikethroughStyle", UIA_TEXTATTRIBUTE_ID.UIA_StrikethroughStyleAttributeId),
    ];

    private void AddTextAttributes(IUIAutomationElement? element, string? propertyName, Dictionary<string, object?> props)
    {
        if (element is null)
        {
            if (propertyName is null)
            {
                _logger.LogWarning("Text formatting omitted from all properties because no live element was resolved.");
                return;
            }
            throw new InvalidOperationException("Element is stale. Re-run 'inspect' or 'search'.");
        }

        // Absence is a successful availability query, not a provider exception.
        using var available = element.GetCurrentPropertyValue(UIA_PROPERTY_ID.UIA_IsTextPatternAvailablePropertyId);
        if (!available.As<bool>())
        {
            foreach (var (name, _) in TextAttributes)
            {
                if (propertyName is null || propertyName == name)
                {
                    props[name] = "Unavailable";
                }
            }
            return;
        }

        var pattern = (IUIAutomationTextPattern)element.GetCurrentPattern(UIA_PATTERN_ID.UIA_TextPatternId);
        var range = pattern.get_DocumentRange();
        var mixed = _automation.get_ReservedMixedAttributeValue();
        var notSupported = _automation.get_ReservedNotSupportedValue();
        foreach (var (name, id) in TextAttributes)
        {
            if (propertyName is null || propertyName == name)
            {
                // CsWin32 projects VARIANT as ComVariant, which owns BSTR/IUnknown storage.
                using var value = range.GetAttributeValue(id);
                try
                {
                    props[name] = FormatTextAttribute(value, mixed, notSupported);
                }
                catch (InvalidOperationException) when (propertyName is null)
                {
                    _logger.LogWarning("Text attribute {AttributeName} omitted from all properties because its value could not be decoded (VARIANT type {VariantType}).",
                        name, value.VarType);
                }
            }
        }
    }

    internal static string FormatTextAttribute(ComVariant value, object mixed, object notSupported)
    {
        if (value.VarType == VarEnum.VT_UNKNOWN)
        {
            // Compare canonical IUnknown identity, never wrapper equality or ToString().
            var unknown = value.GetRawDataRef<nint>();
            if (HasComIdentity(unknown, mixed)) { return "Mixed"; }
            if (HasComIdentity(unknown, notSupported)) { return "NotSupported"; }
            throw new InvalidOperationException("The text provider returned an unrecognized attribute token.");
        }

        return value.VarType switch
        {
            // A null BSTR represents an empty string, not UIA's unsupported sentinel.
            VarEnum.VT_BSTR => value.As<string>() ?? string.Empty,
            VarEnum.VT_I4 => value.As<int>().ToString(CultureInfo.InvariantCulture),
            VarEnum.VT_R8 => value.As<double>().ToString(CultureInfo.InvariantCulture),
            VarEnum.VT_BOOL => value.As<bool>().ToString(),
            _ => throw new InvalidOperationException($"The text provider returned an unexpected attribute type: {value.VarType}."),
        };
    }

    private static unsafe bool HasComIdentity(nint unknown, object token)
    {
        var iid = new Guid("00000000-0000-0000-C000-000000000046");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var identity));
        try
        {
            var tokenIdentity = ComInterfaceMarshaller<object>.ConvertToUnmanaged(token);
            try { return identity == (nint)tokenIdentity; }
            finally { ComInterfaceMarshaller<object>.Free(tokenIdentity); }
        }
        finally { Marshal.Release(identity); }
    }
}
