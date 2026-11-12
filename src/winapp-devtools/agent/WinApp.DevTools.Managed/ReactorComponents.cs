// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Reflection;

namespace WinApp.DevTools.Managed;

/// <summary>Type names, text parsing and handler names, as DevTools shows and reads them for Reactor components.</summary>
internal static class ReactorComponents
{
    /// <summary><c>int</c>, <c>string?</c>, <c>List&lt;TaskItem&gt;</c>: the type as C# reads it.</summary>
    internal static string FriendlyTypeName(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } inner)
        {
            return FriendlyTypeName(inner) + "?";
        }
        var alias = Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean => "bool", TypeCode.Byte => "byte", TypeCode.SByte => "sbyte", TypeCode.Char => "char",
            TypeCode.Int16 => "short", TypeCode.UInt16 => "ushort", TypeCode.Int32 => "int", TypeCode.UInt32 => "uint",
            TypeCode.Int64 => "long", TypeCode.UInt64 => "ulong", TypeCode.Single => "float", TypeCode.Double => "double",
            TypeCode.Decimal => "decimal", TypeCode.String => "string",
            _ => null,
        };
        if (alias is not null && !type.IsEnum)
        {
            return alias;
        }
        if (type == typeof(object))
        {
            return "object";
        }
        if (type.IsArray && type.GetElementType() is { } element)
        {
            return FriendlyTypeName(element) + "[]";
        }
        var name = ReactorApi.TypeName(type);
        return type.IsGenericType ? $"{name}<{string.Join(", ", type.GetGenericArguments().Select(FriendlyTypeName))}>" : name;
    }

    /// <summary>A type a value can be typed as on the command line: primitives, strings, enums and their nullables.</summary>
    internal static bool IsEditable(Type? type)
    {
        if (type is null)
        {
            return false;
        }
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
            type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(Guid);
    }

    /// <summary>
    /// Parses command-line text to a handler argument's type. No conversion beyond that: a value of any other type is
    /// refused with a reason, because a complex value cannot be typed safely from text.
    /// </summary>
    internal static bool TryParse(string text, Type type, out object? value, out string? error)
    {
        value = null;
        error = null;
        var underlying = Nullable.GetUnderlyingType(type);
        var target = underlying ?? type;
        if (text == "null" && (underlying is not null || !type.IsValueType))
        {
            return true;
        }
        if (!IsEditable(type))
        {
            error = $"a {FriendlyTypeName(type)} value cannot be typed as text; only numbers, text, true/false, enums and their nullables can be set";
            return false;
        }
        var culture = CultureInfo.InvariantCulture;
        try
        {
            if (target == typeof(string))
            {
                value = text;
                return true;
            }
            if (target.IsEnum)
            {
                // A comma list combines members, which only a [Flags] enum can hold.
                var flags = target.IsDefined(typeof(FlagsAttribute), false);
                if ((flags || !text.Contains(',')) && Enum.TryParse(target, text, ignoreCase: true, out var parsed) &&
                    (Enum.IsDefined(target, parsed!) || flags))
                {
                    value = parsed;
                    return true;
                }
                error = $"'{text}' is not a {FriendlyTypeName(target)}; use one of {string.Join(", ", Enum.GetNames(target))}";
                return false;
            }
            if (target == typeof(bool))
            {
                if (bool.TryParse(text, out var flag))
                {
                    value = flag;
                    return true;
                }
                error = $"'{text}' is not true or false";
                return false;
            }
            if (target == typeof(char))
            {
                if (text.Length == 1)
                {
                    value = text[0];
                    return true;
                }
                error = $"'{text}' is not a single character";
                return false;
            }
            if (TryParseNumber(text, target, out var number) is { } numeric)
            {
                if (!numeric)
                {
                    error = $"'{text}' is not a valid {FriendlyTypeName(type)}";
                    return false;
                }
                value = number;
                return true;
            }
            value = target == typeof(Guid) ? Guid.Parse(text)
                : target == typeof(TimeSpan) ? TimeSpan.Parse(text, culture)
                : target == typeof(DateTime) ? DateTime.Parse(text, culture, DateTimeStyles.RoundtripKind)
                : target == typeof(DateTimeOffset) ? DateTimeOffset.Parse(text, culture)
                : Convert.ChangeType(text, target, culture);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException or ArgumentException)
        {
            error = $"'{text}' is not a valid {FriendlyTypeName(type)}";
            return false;
        }
    }

    /// <summary>
    /// Parses an invariant-culture number of a numeric type: no thousands separators (3,5 is not 35), and nothing that
    /// overflows the type (1e999 is not Infinity). Null when <paramref name="type"/> is not numeric.
    /// </summary>
    private static bool? TryParseNumber(string text, Type type, out object? value)
    {
        const NumberStyles integer = NumberStyles.Integer;
        const NumberStyles real = NumberStyles.Float;
        var culture = CultureInfo.InvariantCulture;
        value = null;
        bool ok;
        switch (Type.GetTypeCode(type))
        {
            case TypeCode.SByte: ok = sbyte.TryParse(text, integer, culture, out var i8); value = i8; break;
            case TypeCode.Byte: ok = byte.TryParse(text, integer, culture, out var u8); value = u8; break;
            case TypeCode.Int16: ok = short.TryParse(text, integer, culture, out var i16); value = i16; break;
            case TypeCode.UInt16: ok = ushort.TryParse(text, integer, culture, out var u16); value = u16; break;
            case TypeCode.Int32: ok = int.TryParse(text, integer, culture, out var i32); value = i32; break;
            case TypeCode.UInt32: ok = uint.TryParse(text, integer, culture, out var u32); value = u32; break;
            case TypeCode.Int64: ok = long.TryParse(text, integer, culture, out var i64); value = i64; break;
            case TypeCode.UInt64: ok = ulong.TryParse(text, integer, culture, out var u64); value = u64; break;
            case TypeCode.Single: ok = float.TryParse(text, real, culture, out var f32) && float.IsFinite(f32); value = f32; break;
            case TypeCode.Double: ok = double.TryParse(text, real, culture, out var f64) && double.IsFinite(f64); value = f64; break;
            case TypeCode.Decimal: ok = decimal.TryParse(text, real, culture, out var dec); value = dec; break;
            default: return null;
        }
        return ok;
    }

    /// <summary>
    /// What a handler runs, as the developer wrote it: <c>Counter.Increment</c>, <c>lambda in Counter.Render</c>, or
    /// <c>Increment in Counter.Render</c> for a local function.
    /// </summary>
    internal static string HandlerName(Delegate handler)
    {
        var method = handler.Method;
        var owner = method.DeclaringType;
        while (owner is not null && owner.Name.StartsWith('<'))
        {
            owner = owner.DeclaringType;
        }
        var prefix = owner is null ? "" : ReactorApi.TypeName(owner) + ".";
        var name = method.Name;
        if (name.StartsWith('<') && name.IndexOf('>') is var close and > 1)
        {
            var container = name[1..close];
            // A local function compiles to <Container>g__Name|n_m.
            var local = name.IndexOf(">g__", StringComparison.Ordinal);
            if (local > 0 && name.IndexOf('|', local) is var bar and > 0)
            {
                return $"{name[(local + 4)..bar]} in {prefix}{container}";
            }
            return $"lambda in {prefix}{container}";
        }
        return prefix + name;
    }
}

/// <summary>Member reads by name over objects whose types this agent never references.</summary>
internal static class ReactorReflect
{
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>The first property or field of those names the object's type has, public or not; null when none.</summary>
    internal static object? Member(object? target, params string[] names)
    {
        if (target is null)
        {
            return null;
        }
        for (var type = target.GetType(); type is not null; type = type.BaseType)
        {
            foreach (var name in names)
            {
                if (type.GetProperty(name, AnyInstance | BindingFlags.DeclaredOnly) is { CanRead: true } property &&
                    property.GetIndexParameters().Length == 0)
                {
                    try { return property.GetValue(target); }
                    catch (TargetInvocationException) { return null; }
                }
                if (type.GetField(name, AnyInstance | BindingFlags.DeclaredOnly) is { } field)
                {
                    return field.GetValue(target);
                }
            }
        }
        return null;
    }
}
