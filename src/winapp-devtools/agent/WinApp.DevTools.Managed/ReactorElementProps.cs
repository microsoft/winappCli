// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections;
using System.Globalization;
using System.Reflection;

namespace WinApp.DevTools.Managed;

/// <summary>One WinUI property Reactor applies from an element's modifiers.</summary>
internal sealed record ReactorAppliedProperty(string Modifier, string Property, object? Value);

/// <summary>
/// A reference Reactor set on an element whose target isn't created yet: <see cref="Property"/> (LabeledBy, XYFocusRight,
/// reference#N ...), where it came from, and what the target is expected to be.
/// </summary>
internal sealed record ReactorPendingReference(string Property, bool IsList, int Index, string? TargetType, string? AutomationId);

/// <summary>One element-record property the app's C# set, and the WinUI property it writes.</summary>
internal sealed record ReactorRecordProp(string Name, string Property, string Value, bool Redacted);

/// <summary>
/// Reads what a Reactor element record says about its control, by reflection over the record alone, so it works
/// against any Reactor version. A record property counts as set when it is a constructor argument (what the factory
/// call passed) or differs from a record built from the same arguments (an initializer or <c>with</c>).
/// </summary>
internal static class ReactorElementProps
{
    private const int MaxValueLength = 200;
    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

    // Element members that describe the element, not a property of its control.
    private static readonly HashSet<string> NotControlProperties = new(StringComparer.Ordinal)
    {
        "EqualityContract", "Key", "Modifiers", "CallSite", "Extensions", "Attached", "ImplicitTransitions",
        "ThemeTransitions", "ThemeBindings", "Setters", "ContentElement", "Command",
        // A component element describes the component it mounts; its props are reported as component props.
        "ComponentType", "Props", "RenderFunc", "Dependencies",
    };

    // Modifiers that are not one control property (groups, references, behaviors).
    private static readonly HashSet<string> NotModifierProperties = new(StringComparer.Ordinal)
    {
        "EqualityContract", "Layout", "Visual", "Accessibility", "Ref", "Backdrop", "Pan", "Pinch", "Rotate",
        "LongPress", "DragSource", "DropTarget", "AttachedFlyout", "ContextFlyout", "RichToolTip",
        "ToolTipPlacementTargetRef", "XYFocusUpRef", "XYFocusDownRef", "XYFocusLeftRef", "XYFocusRightRef",
        "MarginInlineStart", "MarginInlineEnd", "PaddingInlineStart", "PaddingInlineEnd", "BorderInlineStart",
    };

    private static readonly Dictionary<string, string> ModifierProperty = new(StringComparer.Ordinal)
    {
        ["IsVisible"] = "Visibility",
        ["AutomationName"] = "AutomationProperties.Name",
        ["AutomationId"] = "AutomationProperties.AutomationId",
        ["HeadingLevel"] = "AutomationProperties.HeadingLevel",
    };

    /// <summary>
    /// The WinUI property name in Reactor's <c>OwnerType.PropertyName</c> form: <c>FrameworkElement.Width</c> is
    /// <c>Width</c>; an attached property keeps its owner (<c>AutomationProperties.Name</c>).
    /// </summary>
    internal static string PropertyName(string qualified)
    {
        var dot = qualified.LastIndexOf('.');
        if (dot < 0)
        {
            return qualified;
        }
        var owner = qualified[..dot];
        return owner is "UIElement" or "FrameworkElement" or "Control" or "Panel" or "Border" or "TextBlock" or "Shape"
            ? qualified[(dot + 1)..]
            : qualified;
    }

    /// <summary>The element's own record properties that were set, with the WinUI property each one writes.</summary>
    internal static IReadOnlyList<ReactorRecordProp> RecordProps(object tag, Type? elementBase)
    {
        var type = tag.GetType();
        var aliases = Aliases(type);
        var constructor = PrimaryConstructor(type);
        var positional = new HashSet<string>(
            constructor?.GetParameters().Select(p => p.Name ?? "") ?? [], StringComparer.OrdinalIgnoreCase);
        var baseline = Baseline(tag, constructor);

        var list = new List<ReactorRecordProp>();
        foreach (var property in OwnProperties(type, elementBase))
        {
            if (NotControlProperties.Contains(property.Name) || !IsValueProperty(property.PropertyType, elementBase))
            {
                continue;
            }
            object? value;
            try { value = property.GetValue(tag); }
            catch (TargetInvocationException) { continue; }

            var set = positional.Contains(property.Name)
                ? value is not null && !IsEmptyOptional(value)
                : baseline is not null
                    ? !Equals(value, Read(property, baseline))
                    : !IsDefault(value, property.PropertyType);
            if (!set)
            {
                continue;
            }
            var target = aliases.TryGetValue(property.Name, out var alias) ? alias : property.Name == "Label" ? "Content" : property.Name;
            var (text, redacted) = Format(property.Name, value);
            list.Add(new ReactorRecordProp(property.Name, target, text, redacted));
        }
        return list;
    }

    /// <summary>
    /// The modifiers an element carries, named after the WinUI property they write by convention. Used when the
    /// running Reactor cannot report them itself.
    /// </summary>
    internal static IReadOnlyList<ReactorAppliedProperty> Modifiers(object tag)
    {
        var modifiers = tag.GetType().GetProperty("Modifiers", PublicInstance)?.GetValue(tag);
        if (modifiers is null)
        {
            return [];
        }
        var list = new List<ReactorAppliedProperty>();
        foreach (var property in modifiers.GetType().GetProperties(PublicInstance))
        {
            if (NotModifierProperties.Contains(property.Name) || property.GetIndexParameters().Length > 0 ||
                property.Name.StartsWith("On", StringComparison.Ordinal) || typeof(Delegate).IsAssignableFrom(property.PropertyType))
            {
                continue;
            }
            object? value;
            try { value = property.GetValue(modifiers); }
            catch (TargetInvocationException) { continue; }
            if (value is null)
            {
                continue;
            }
            list.Add(new ReactorAppliedProperty(property.Name,
                ModifierProperty.TryGetValue(property.Name, out var mapped) ? mapped : property.Name, value));
        }
        return list;
    }

    /// <summary>
    /// A value shown as itself rather than member by member: primitives, enums, text, numbers, dates, times, GUIDs and
    /// URIs, and their nullables.
    /// </summary>
    internal static bool IsScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(Guid) ||
            type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(DateOnly) ||
            type == typeof(TimeOnly) || type == typeof(Uri) || type == typeof(Version);
    }

    /// <summary>A component's props as name/value rows; a scalar props value is one row named <c>Props</c>.</summary>
    internal static IReadOnlyList<(string Name, string Value, bool Redacted)> ComponentProps(object? props)
    {
        if (props is null)
        {
            return [];
        }
        var type = props.GetType();
        if (IsScalar(type))
        {
            var (text, redacted) = Format("Props", props);
            return [("Props", text, redacted)];
        }
        var list = new List<(string, string, bool)>();
        foreach (var property in type.GetProperties(PublicInstance))
        {
            if (property.Name == "EqualityContract" || property.GetIndexParameters().Length > 0)
            {
                continue;
            }
            object? value;
            try { value = property.GetValue(props); }
            catch (TargetInvocationException) { continue; }
            var (text, redacted) = Format(property.Name, value);
            list.Add((property.Name, text, redacted));
        }
        return list;
    }

    /// <summary>
    /// A value as shown to the developer: redacted when its name says it is a secret (<see cref="DevToolsSecrets"/>).
    /// A collection shows its count, never its items, and a <c>ToString</c> that throws is reported rather than raised.
    /// </summary>
    internal static (string Text, bool Redacted) Format(string name, object? value)
    {
        if (DevToolsSecrets.IsSecretName(name))
        {
            return (DevToolsSecrets.Redacted, true);
        }
        if (value?.GetType() is { IsGenericType: true, Name: "Optional`1" } optional)
        {
            value = optional.GetProperty("HasValue", PublicInstance)?.GetValue(value) is true
                ? optional.GetProperty("Value", PublicInstance)?.GetValue(value)
                : null;
        }
        // Every collection is summarised by its count: its own ToString may enumerate it.
        if (value is IEnumerable and not string)
        {
            var type = ReactorComponents.FriendlyTypeName(value.GetType());
            return (ReactorValueText.Count(value) is { } count ? $"{type} ({count.ToString(CultureInfo.InvariantCulture)} items)" : $"{type} (count unknown)", false);
        }
        var text = value switch
        {
            null => "null",
            string s => s,
            bool b => b ? "True" : "False",
            Delegate d => "handler " + (d.Method.DeclaringType is { } owner ? ReactorApi.TypeName(owner) + "." : "") + d.Method.Name,
            IFormattable f => ReactorValueText.SafeText(f, v => ((IFormattable)v).ToString(null, CultureInfo.InvariantCulture)),
            _ => ReactorValueText.SafeText(value, v => v.ToString()),
        };
        return (text.Length > MaxValueLength ? text[..MaxValueLength] + "..." : text, false);
    }

    /// <summary>
    /// A value Reactor already formatted as text (<c>GetAppliedProperties</c>), redacted by the same rule as
    /// <see cref="Format"/>.
    /// </summary>
    internal static (string Text, bool Redacted) FormatText(string name, string text) =>
        DevToolsSecrets.IsSecretName(name) ? (DevToolsSecrets.Redacted, true) : (text, false);

    private static IEnumerable<PropertyInfo> OwnProperties(Type type, Type? elementBase)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
        {
            if (current == elementBase || (elementBase is null && current.Name == "Element"))
            {
                yield break;
            }
            foreach (var property in current.GetProperties(PublicInstance | BindingFlags.DeclaredOnly))
            {
                if (property.CanRead && property.GetIndexParameters().Length == 0 && seen.Add(property.Name))
                {
                    yield return property;
                }
            }
        }
    }

    // Values a WinUI property can take: not handlers, child elements or element lists (those are children).
    private static bool IsValueProperty(Type type, Type? elementBase)
    {
        if (typeof(Delegate).IsAssignableFrom(type))
        {
            return false;
        }
        bool IsElement(Type t) => elementBase is not null ? elementBase.IsAssignableFrom(t) : t.Name == "Element" || t.BaseType?.Name == "Element";
        if (IsElement(type))
        {
            return false;
        }
        if (type != typeof(string) && type.GetInterfaces().FirstOrDefault(i => i.IsGenericType &&
                i.GetGenericTypeDefinition() == typeof(IEnumerable<>)) is { } enumerable &&
            IsElement(enumerable.GetGenericArguments()[0]))
        {
            return false;
        }
        return !(type.IsArray && type.GetElementType() is { } item && IsElement(item));
    }

    private static Dictionary<string, string> Aliases(Type type)
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var attribute in current.GetCustomAttributes(inherit: false))
            {
                var attributeType = attribute.GetType();
                if (attributeType.Name != "WrapAliasAttribute")
                {
                    continue;
                }
                if (attributeType.GetProperty("Name")?.GetValue(attribute) is string name &&
                    attributeType.GetProperty("ControlProperty")?.GetValue(attribute) is string control)
                {
                    aliases.TryAdd(name, control);
                }
            }
        }
        return aliases;
    }

    // The record's primary constructor: the widest public one that is not the compiler's copy constructor.
    private static ConstructorInfo? PrimaryConstructor(Type type) =>
        type.GetConstructors(PublicInstance)
            .Where(c => c.GetParameters() is var p && !(p.Length == 1 && p[0].ParameterType == type))
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();

    // The same record built from the element's constructor arguments alone, so initializer defaults compare equal.
    private static object? Baseline(object tag, ConstructorInfo? constructor)
    {
        if (constructor is null)
        {
            return null;
        }
        try
        {
            var type = tag.GetType();
            var args = constructor.GetParameters().Select(parameter =>
            {
                var property = type.GetProperty(parameter.Name ?? "", PublicInstance);
                if (property is not null && parameter.ParameterType.IsAssignableFrom(property.PropertyType))
                {
                    return property.GetValue(tag);
                }
                return parameter.HasDefaultValue ? parameter.DefaultValue
                    : parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null;
            }).ToArray();
            return constructor.Invoke(args);
        }
        catch (Exception ex) when (ex is TargetInvocationException or ArgumentException or MemberAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static object? Read(PropertyInfo property, object instance)
    {
        try { return property.GetValue(instance); }
        catch (TargetInvocationException) { return null; }
    }

    private static bool IsDefault(object? value, Type type) =>
        value is null || (type.IsValueType && Nullable.GetUnderlyingType(type) is null && value.Equals(Activator.CreateInstance(type)));

    // Reactor's Optional<T> without a value leaves the control's property uncontrolled.
    private static bool IsEmptyOptional(object value) =>
        value.GetType() is { IsGenericType: true, Name: "Optional`1" } type &&
        type.GetProperty("HasValue", PublicInstance)?.GetValue(value) is false;
}
