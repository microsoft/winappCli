// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Reflection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace WinApp.DevTools.Managed;

/// <summary>Where a resource key resolved: the dictionary that holds it and the value it holds.</summary>
internal sealed record ResourceHit(ResourceDictionary Dictionary, string Path, string Scope, string? Owner, object? Value);

/// <summary>
/// Reads and live-edits XAML resources by key, the way <c>{ThemeResource}</c> looks them up: the element's own
/// resources, its ancestors', then the application's, each with its theme dictionary for the element's theme and its
/// merged dictionaries. Works in any WinUI app with the managed agent, Reactor or not. Edits are in memory only.
/// </summary>
internal static class ResourceOps
{
    public static string Run(BindingTarget target, string op, string key, string payload)
    {
        var mutation = op == "resourceset";
        return ReactorComponentOps.RunOnUi(target, mutation, (element, done) =>
        {
            if (key.Length == 0)
            {
                done(ReactorComponentOps.Refused("bad-args", "a resource key is required"));
                return;
            }
            done(op == "resourceget" ? Get(element, key, payload == "element") : Set(element, key, payload));
        });
    }

    private static string Get(UIElement element, string key, bool fromElement)
    {
        var theme = ThemeName(element);
        var hit = Find(element, key, theme, fromElement);
        return ReactorComponentOps.Json(w =>
        {
            w.WriteString("state", "ok");
            w.WriteString("key", key);
            w.WriteString("theme", theme);
            w.WriteBoolean("found", hit is not null);
            if (hit is not null)
            {
                WriteHit(w, hit);
            }
        });
    }

    private static string Set(UIElement element, string key, string payload)
    {
        var newline = payload.IndexOf('\n');
        var scope = newline < 0 ? "" : payload[..newline];
        var text = newline < 0 ? payload : payload[(newline + 1)..];
        if (scope is not ("element" or "app"))
        {
            return ReactorComponentOps.Refused("bad-args", "scope must be element or app");
        }
        var theme = ThemeName(element);
        var before = Find(element, key, theme, scope == "element");
        ResourceDictionary dictionary;
        string path;
        if (scope == "element")
        {
            if (element is not FrameworkElement fe)
            {
                return ReactorComponentOps.Refused("bad-target", "only a FrameworkElement has resources of its own");
            }
            dictionary = fe.Resources;
            path = "Resources";
        }
        else if (before is { Scope: "application" })
        {
            // Write where the key already lives, so a theme-specific value stays theme-specific.
            dictionary = before.Dictionary;
            path = before.Path;
        }
        else if (Application.Current?.Resources is { } app)
        {
            dictionary = app;
            path = "Resources";
        }
        else
        {
            return ReactorComponentOps.Refused("no-application", "this app has no Application resources");
        }

        var exampleType = before?.Value?.GetType();
        if (!TryParse(text, exampleType, out var value, out var error))
        {
            return ReactorComponentOps.Refused("bad-value", error!);
        }
        dictionary[key] = value;
        var refreshed = Refresh(element, scope == "app");
        var after = Find(element, key, theme, scope == "element");
        return ReactorComponentOps.Json(w =>
        {
            w.WriteString("state", "ok");
            w.WriteString("key", key);
            w.WriteString("scope", scope);
            w.WriteString("theme", theme);
            w.WriteString("requested", text);
            w.WriteString("written", path);
            if (before is not null) { w.WriteString("before", FormatValue(before.Value)); } else { w.WriteNull("before"); }
            if (after is not null) { w.WriteString("after", FormatValue(after.Value)); } else { w.WriteNull("after"); }
            w.WriteString("valueType", value?.GetType().FullName ?? "null");
            w.WriteBoolean("added", before is null || (scope == "element" && before.Scope != "element"));
            w.WriteNumber("refreshedRoots", refreshed);
        }); 
    }

    private static void WriteHit(System.Text.Json.Utf8JsonWriter w, ResourceHit hit)
    {
        w.WriteString("scope", hit.Scope);
        if (hit.Owner is not null) { w.WriteString("owner", hit.Owner); }
        w.WriteString("dictionary", hit.Path);
        w.WriteString("value", FormatValue(hit.Value));
        w.WriteString("valueType", hit.Value?.GetType().FullName ?? "null");
    }

    internal static ResourceHit? Find(UIElement element, string key, string theme, bool fromElement)
    {
        var first = true;
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement fe && Lookup(fe.Resources, key, theme, "Resources") is { } found)
            {
                return found with { Scope = first && fromElement ? "element" : "ancestor", Owner = ReactorApi.TypeName(fe.GetType()) + (fe.Name is { Length: > 0 } name ? "#" + name : "") };
            }
            first = false;
        }
        return Application.Current?.Resources is { } app && Lookup(app, key, theme, "Application.Resources") is { } hit
            ? hit with { Scope = "application" }
            : null;
    }

    private static ResourceHit? Lookup(ResourceDictionary dictionary, string key, string theme, string path, int depth = 0)
    {
        if (depth > 16)
        {
            return null;
        }
        // ResourceDictionary lookups search merged and theme dictionaries too; only own entries say where a key lives.
        if (TryOwn(dictionary, key, out var direct))
        {
            return new ResourceHit(dictionary, path, "", null, direct);
        }
        foreach (var name in new[] { theme, theme == "Dark" ? "Default" : "Light", "Default" }.Distinct())
        {
            if (TryOwn(dictionary.ThemeDictionaries, name, out var themed) && themed is ResourceDictionary themeDictionary &&
                TryOwn(themeDictionary, key, out var value))
            {
                return new ResourceHit(themeDictionary, $"{path}.ThemeDictionaries[{name}]", "", null, value);
            }
        }
        // Later merged dictionaries win, as in XAML lookup.
        for (var i = dictionary.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            if (Lookup(dictionary.MergedDictionaries[i], key, theme, $"{path}.MergedDictionaries[{i}]", depth + 1) is { } hit)
            {
                return hit;
            }
        }
        return null;
    }

    private static bool TryOwn(IEnumerable<KeyValuePair<object, object>> dictionary, string key, out object? value)
    {
        foreach (var pair in dictionary)
        {
            if (pair.Key is string name && name == key)
            {
                value = pair.Value;
                return true;
            }
        }
        value = null;
        return false;
    }

    internal static string ThemeName(UIElement element) =>
        element is FrameworkElement { ActualTheme: ElementTheme.Dark } ? "Dark"
        : element is FrameworkElement { ActualTheme: ElementTheme.Light } ? "Light"
        : Application.Current?.RequestedTheme == ApplicationTheme.Dark ? "Dark" : "Light";

    /// <summary>
    /// Makes <c>{ThemeResource}</c> references (and Reactor's ThemeRef styles) read the edited dictionary again, by
    /// flipping each affected root's theme and back. <c>{StaticResource}</c> references keep the value they loaded.
    /// </summary>
    private static int Refresh(UIElement element, bool everyWindow)
    {
        // Reactor 0.0.0-devtools.4+ re-resolves every ThemeRef and re-renders every host itself.
        var notified = false;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!assembly.IsDynamic && assembly.GetType("Microsoft.UI.Reactor.Core.Theme")?
                    .GetMethod("NotifyResourcesChanged", BindingFlags.Static | BindingFlags.Public, Type.EmptyTypes) is { } notify)
            {
                try { notify.Invoke(null, null); notified = true; }
                catch (TargetInvocationException) { }
            }
        }
        // Older Reactor caches what a ThemeRef resolved to; drop it so the next render resolves the edited value.
        foreach (var assembly in notified ? Array.Empty<Assembly>() : AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!assembly.IsDynamic)
            {
                ReactorInternals.InvalidateThemeCache(assembly);
            }
        }
        var roots = new List<FrameworkElement>();
        if (element.XamlRoot?.Content is FrameworkElement own)
        {
            roots.Add(own);
        }
        if (everyWindow && ReactorApi.TryLoad(AppDomain.CurrentDomain.GetAssemblies()) is { Error: null } api)
        {
            foreach (var host in api.WindowHosts().Keys)
            {
                if (host.XamlRoot?.Content is FrameworkElement root && !roots.Contains(root))
                {
                    roots.Add(root);
                }
            }
        }
        foreach (var root in notified ? Enumerable.Empty<FrameworkElement>() : roots)
        {
            var requested = root.RequestedTheme;
            root.RequestedTheme = root.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            root.RequestedTheme = requested;
        }
        return roots.Count;
    }

    /// <summary>A resource value as typed on the command line, converted to the type the key already holds.</summary>
    internal static bool TryParse(string text, Type? example, out object? value, out string? error)
    {
        value = null;
        error = null;
        try
        {
            if (example is null || example == typeof(string))
            {
                value = example is null && TryColor(text, out var color) ? new SolidColorBrush(color) : text;
                return true;
            }
            if (typeof(Brush).IsAssignableFrom(example))
            {
                if (TryColor(text, out var brushColor))
                {
                    value = new SolidColorBrush(brushColor);
                    return true;
                }
                error = $"'{text}' is not a color; use #RRGGBB, #AARRGGBB or a named color";
                return false;
            }
            if (example == typeof(Windows.UI.Color))
            {
                if (TryColor(text, out var plain))
                {
                    value = plain;
                    return true;
                }
                error = $"'{text}' is not a color; use #RRGGBB, #AARRGGBB or a named color";
                return false;
            }
            if (example == typeof(bool) || example == typeof(double) || example == typeof(int) || example.IsEnum)
            {
                return ReactorComponents.TryParse(text, example, out value, out error);
            }
            value = XamlBindingHelper.ConvertValue(example, text);
            return value is not null;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidCastException or System.Runtime.InteropServices.COMException)
        {
            error = $"'{text}' is not a valid {example?.Name ?? "value"}";
            return false;
        }
    }

    internal static bool TryColor(string text, out Windows.UI.Color color)
    {
        color = default;
        var hex = text.StartsWith('#') ? text[1..] : null;
        if (hex is { Length: 6 or 8 } && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
        {
            if (hex.Length == 6)
            {
                argb |= 0xFF000000;
            }
            color = Windows.UI.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            return true;
        }
        if (typeof(Colors).GetProperty(text, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase)?.GetValue(null) is Windows.UI.Color named)
        {
            color = named;
            return true;
        }
        return false;
    }

    internal static string FormatValue(object? value) => value switch
    {
        null => "null",
        SolidColorBrush brush => $"SolidColorBrush {Hex(brush.Color)}" + (brush.Opacity < 1 ? $" (Opacity {brush.Opacity.ToString(CultureInfo.InvariantCulture)})" : ""),
        Windows.UI.Color color => Hex(color),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static string Hex(Windows.UI.Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
}
