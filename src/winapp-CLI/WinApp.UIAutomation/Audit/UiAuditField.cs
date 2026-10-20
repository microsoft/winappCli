// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text.Json.Serialization;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>A typed UIA text attribute, with absence and provider sentinel values kept distinct.</summary>
public sealed record UiAuditField
{
    /// <summary>One of <c>value</c>, <c>mixed</c>, <c>unsupported</c>, <c>error</c>, or <c>withheld</c>.</summary>
    public string Status { get; init; } = "unsupported";

    /// <summary>Numeric attribute value, only when status is <c>value</c> and the attribute is numeric.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Number { get; init; }

    /// <summary>Boolean attribute value, only when status is <c>value</c> and the attribute is Boolean.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool? Boolean { get; init; }

    /// <summary>Native VARIANT type, such as <c>VT_I4</c> or <c>VT_BOOL</c>, when it was obtained.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? NativeType { get; init; }

    /// <summary>Non-sensitive failure explanation; never the provider's exception message.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Error { get; init; }
}

internal enum UiAuditFieldKind { Boolean, Color, FontSize, FontWeight }
internal enum UiAuditFieldSentinel { None, Mixed, Unsupported }

internal static class UiAuditFieldReader
{
    internal static UiAuditField Read(ComVariant value, UiAuditFieldKind kind, UiAuditFieldSentinel sentinel = UiAuditFieldSentinel.None)
    {
        var nativeType = value.VarType.ToString();
        if (sentinel != UiAuditFieldSentinel.None)
        {
            return new UiAuditField
            {
                Status = sentinel == UiAuditFieldSentinel.Mixed ? "mixed" : "unsupported",
                NativeType = nativeType,
            };
        }

        if (value.VarType is VarEnum.VT_EMPTY or VarEnum.VT_NULL)
        {
            return new UiAuditField { Status = "unsupported", NativeType = nativeType };
        }
        if (value.VarType is VarEnum.VT_ERROR or VarEnum.VT_HRESULT)
        {
            return Failure($"The provider returned HRESULT 0x{value.GetRawDataRef<int>():X8}.", nativeType);
        }
        if (kind == UiAuditFieldKind.Boolean && value.VarType == VarEnum.VT_BOOL)
        {
            var raw = value.GetRawDataRef<short>();
            return raw is 0 or -1
                ? new UiAuditField { Status = "value", Boolean = raw != 0, NativeType = nativeType }
                : Failure("The provider returned an invalid VARIANT_BOOL.", nativeType);
        }

        double? number = kind switch
        {
            UiAuditFieldKind.Color or UiAuditFieldKind.FontWeight when value.VarType == VarEnum.VT_I4 => value.As<int>(),
            UiAuditFieldKind.Color or UiAuditFieldKind.FontWeight when value.VarType == VarEnum.VT_UI4 => value.As<uint>(),
            UiAuditFieldKind.FontSize when value.VarType == VarEnum.VT_R8 => value.As<double>(),
            _ => null,
        };
        if (number is null)
        {
            return Failure("The provider returned a VARIANT type that does not match the attribute.", nativeType);
        }
        if (!double.IsFinite(number.Value)
            || (kind == UiAuditFieldKind.Color && number is < 0 or > 0x00ffffff)
            || (kind == UiAuditFieldKind.FontSize && number <= 0)
            || (kind == UiAuditFieldKind.FontWeight && number < 0))
        {
            return Failure("The provider returned an invalid numeric attribute value.", nativeType);
        }
        return new UiAuditField { Status = "value", Number = number, NativeType = nativeType };
    }

    internal static UiAuditField Failure(string message, string? nativeType = null)
        => new() { Status = "error", Error = message, NativeType = nativeType };

    internal static UiAuditField Withheld() => new() { Status = "withheld" };
}
