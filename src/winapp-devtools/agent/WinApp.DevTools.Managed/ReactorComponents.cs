// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections;
using System.Globalization;
using System.Reflection;

namespace WinApp.DevTools.Managed;

/// <summary>One hook cell of a mounted component, in call order.</summary>
internal sealed record ReactorHook(int Index, string Kind, Type? ValueType, object? Value, bool CanSet, bool Migrated);

/// <summary>One context a component reads through <c>UseContext</c>.</summary>
internal sealed record ReactorContextRead(int HookIndex, string Name, Type? ValueType, object? Value, bool IsDefault);

/// <summary>
/// One mounted component: its identity, props, hooks and contexts, and the two writes DevTools makes (a state value
/// and a re-render). Every member must run on the UI thread that renders the component.
/// </summary>
internal abstract class ReactorComponentHandle
{
    /// <summary><c>reactor</c> when Reactor's public diagnostics answered; <c>internal</c> otherwise.</summary>
    public abstract string Api { get; }
    public abstract string Kind { get; }
    public abstract bool IsRoot { get; }
    public abstract Type? ComponentType { get; }
    public abstract Delegate? RenderFunc { get; }
    public abstract object? Props { get; }
    public abstract object? DeclaringElement { get; }
    public abstract bool IsMounted { get; }
    public abstract IReadOnlyList<ReactorHook> Hooks();
    public abstract IReadOnlyList<ReactorContextRead> Contexts();
    /// <summary>Writes an already typed value into a settable cell; false when the cell refuses it.</summary>
    public abstract bool TrySet(int index, object? value);
    public abstract bool Rerender();

    public virtual string Name => ComponentType is { } type ? ReactorApi.TypeName(type)
        : ReactorApi.DelegateName(RenderFunc, Kind == "memo" ? "Memo" : IsRoot ? "render" : "RenderEachTime");

    /// <summary>The method that renders the component: a class component's <c>Render</c>, or the render delegate.</summary>
    public MethodInfo? RenderMethod
    {
        get
        {
            if (RenderFunc?.Method is { } method)
            {
                return method;
            }
            for (var type = ComponentType; type is not null && type != typeof(object); type = type.BaseType)
            {
                if (type.GetMethod("Render", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                        Type.EmptyTypes) is { IsAbstract: false } render)
                {
                    return render;
                }
            }
            return null;
        }
    }
}

/// <summary>Reactor's public <c>ComponentDiagnostics</c>, bound by name so this agent needs no Reactor reference.</summary>
internal sealed class PublicReactorComponent(object diagnostics) : ReactorComponentHandle
{
    public override string Api => "reactor";

    public override string Kind => Read("Kind")?.ToString()?.ToLowerInvariant() switch
    {
        "function" => "function",
        "memo" => "memo",
        _ => "class",
    };

    public override bool IsRoot => Read("IsRoot") is true;
    public override Type? ComponentType => Read("ComponentType") as Type;
    public override Delegate? RenderFunc => Read("RenderFunc") as Delegate;
    public override object? Props => Read("Props");
    public override object? DeclaringElement => Read("DeclaringElement");
    public override bool IsMounted => Read("IsMounted") is not false;

    public override IReadOnlyList<ReactorHook> Hooks()
    {
        var list = new List<ReactorHook>();
        if (Call("GetHooks") is not IEnumerable hooks)
        {
            return list;
        }
        foreach (var hook in hooks)
        {
            if (hook is null)
            {
                continue;
            }
            list.Add(new ReactorHook(
                ReactorReflect.Member(hook, "Index") is int index ? index : list.Count,
                HookKindName(ReactorReflect.Member(hook, "Kind")?.ToString()),
                ReactorReflect.Member(hook, "ValueType") as Type,
                ReactorReflect.Member(hook, "Value"),
                ReactorReflect.Member(hook, "CanSetValue") is true,
                ReactorReflect.Member(hook, "MigratedByHotReload") is true));
        }
        return list;
    }

    public override IReadOnlyList<ReactorContextRead> Contexts()
    {
        var list = new List<ReactorContextRead>();
        if (Call("GetContexts") is not IEnumerable contexts)
        {
            return list;
        }
        foreach (var context in contexts)
        {
            if (context is null)
            {
                continue;
            }
            var value = ReactorReflect.Member(context, "Value");
            var fallback = ReactorReflect.Member(context, "DefaultValue");
            var type = ReactorReflect.Member(context, "ValueType") as Type;
            list.Add(new ReactorContextRead(
                ReactorReflect.Member(context, "HookIndex") is int index ? index : -1,
                ReactorReflect.Member(context, "Name") as string is { Length: > 0 } name ? name : ReactorComponents.ContextName(type),
                type, value, ReferenceEquals(value, fallback) || (value is not null && value.Equals(fallback))));
        }
        return list;
    }

    public override bool TrySet(int index, object? value) => Call("TrySetHookValue", index, value) is true;
    public override bool Rerender() => Call("RequestRerender") is true;

    private object? Read(string name) => ReactorReflect.Member(diagnostics, name);

    private object? Call(string name, params object?[] args)
    {
        var method = diagnostics.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == args.Length);
        try { return method?.Invoke(diagnostics, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { return null; }
    }

    private static string HookKindName(string? kind) => kind switch
    {
        "State" => "state",
        "Reducer" => "reducer",
        "Ref" => "ref",
        "Memo" => "memo",
        "Effect" => "effect",
        "Context" => "context",
        "Persisted" => "persisted",
        "NavigationLifecycle" => "navigationLifecycle",
        _ => "unknown",
    };
}

/// <summary>Values as DevTools reads, shows and parses them for components.</summary>
internal static class ReactorComponents
{
    private const int MaxValueLength = 200;

    internal static string ContextName(Type? valueType) =>
        valueType is null ? "Context" : $"Context<{FriendlyTypeName(valueType)}>";

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

    /// <summary>
    /// Whether DevTools offers to edit a value: a type that can be typed as text, that is not a secret by its name or
    /// type (even when it holds null, and so shows nothing to redact), and was not redacted.
    /// </summary>
    internal static bool IsEditableValue(string name, Type? type, bool redacted) =>
        !redacted && !ReactorSecrets.IsSecretName(name) && !IsSecretType(type) && IsEditable(type);

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

    /// <summary>Whether a value of this type is a secret; see <see cref="ReactorSecrets.IsSecretType"/>.</summary>
    internal static bool IsSecretType(Type? type) => ReactorSecrets.IsSecretType(type);

    /// <summary>
    /// A value as shown to the developer: redacted when its name or type says it is a secret or its text carries a
    /// labelled one, collections summarised by their count (never enumerated), long text cut.
    /// </summary>
    internal static (string Text, bool Redacted) Format(string name, Type? declared, object? value)
    {
        if (value is not null && (ReactorSecrets.IsSecretName(name) || IsSecretType(declared) || IsSecretType(value.GetType())))
        {
            return (ReactorElementProps.Redacted, true);
        }
        // Every collection is summarised by its count: its own ToString may enumerate it.
        if (value is IEnumerable and not string)
        {
            // A collection expression's runtime type is compiler-generated (<>z__ReadOnlyArray); name the declared one.
            var shownType = value.GetType().Name.StartsWith('<') && declared is not null ? declared : value.GetType();
            var text = ReactorSecrets.Count(value) is { } count
                ? $"{FriendlyTypeName(shownType)} ({count.ToString(CultureInfo.InvariantCulture)} items)"
                : $"{FriendlyTypeName(shownType)} (count unknown)";
            return (text, false);
        }
        if (value is string s)
        {
            if (ReactorSecrets.HasLabelledSecret(s))
            {
                return (ReactorElementProps.Redacted, true);
            }
            var quoted = "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal) + "\"";
            return (quoted.Length > MaxValueLength ? quoted[..MaxValueLength] + "..." : quoted, false);
        }
        var (shown, redacted) = ReactorElementProps.Format(name, value, declared);
        return (shown.Length > MaxValueLength ? shown[..MaxValueLength] + "..." : shown, redacted);
    }

    /// <summary>
    /// Parses command-line text to a hook's value type. No conversion beyond that: a value of any other type is
    /// refused with a reason, because a complex state value cannot be typed safely from text.
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
                if (Enum.TryParse(target, text, ignoreCase: true, out var parsed) &&
                    (Enum.IsDefined(target, parsed!) || target.IsDefined(typeof(FlagsAttribute), false)))
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
