// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Reflection;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
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
            if (op == "resourceget")
            {
                done(Get(element, key, payload == "element"));
                return;
            }
            Set(element, key, payload, done);
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

    private static void Set(UIElement element, string key, string payload, Action<string> done)
    {
        var newline = payload.IndexOf('\n');
        var scope = newline < 0 ? "" : payload[..newline];
        var text = newline < 0 ? payload : payload[(newline + 1)..];
        if (scope is not ("element" or "app"))
        {
            done(ReactorComponentOps.Refused("bad-args", "scope must be element or app"));
            return;
        }
        var theme = ThemeName(element);
        var before = Find(element, key, theme, scope == "element");
        ResourceDictionary dictionary;
        string path;
        if (scope == "element")
        {
            if (element is not FrameworkElement fe)
            {
                done(ReactorComponentOps.Refused("bad-target", "only a FrameworkElement has resources of its own"));
                return;
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
            try
            {
                (dictionary, path) = DictionaryForNewAppKeys(app);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                done(ReactorComponentOps.Refused("resource-read-only", ReadOnlyAppResources));
                return;
            }
        }
        else
        {
            done(ReactorComponentOps.Refused("no-application", "this app has no Application resources"));
            return;
        }

        var exampleType = before?.Value?.GetType();
        if (!TryParse(text, exampleType, out var value, out var error))
        {
            done(ReactorComponentOps.Refused("bad-value", error!));
            return;
        }
        try
        {
            try
            {
                dictionary[key] = value;
            }
            catch (System.Runtime.InteropServices.COMException) when (scope == "app" && ReferenceEquals(dictionary, Application.Current?.Resources))
            {
                // Application.Resources refused a new key without reporting a Source: merge one that takes it.
                (dictionary, path) = AddedAppKeys(dictionary);
                dictionary[key] = value;
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            done(ReactorComponentOps.Refused("resource-read-only", scope == "app" ? ReadOnlyAppResources :
                $"{path} cannot take new keys (it is loaded from a Source); set the key on another element with --scope element <selector>"));
            return;
        }
        var refreshed = Refresh(element, scope == "app");
        // The refresh re-renders Reactor hosts on later dispatcher turns, and a render can write the key again (a
        // .Set(b => b.Resources[...] = ...) in the app's C#), so the value is read back once the renders have finished.
        var api = ReactorApi.TryLoad(AppDomain.CurrentDomain.GetAssemblies()) is { Error: null } loaded ? loaded : null;
        var host = api?.HostFor(element);
        void Report(bool settled)
        {
            try
            {
                var after = Find(element, key, theme, scope == "element");
                // Judge by the dictionary written: a closer dictionary that shadows an app-scope key is not a revert.
                var stored = TryOwn(dictionary, key, out var live);
                var reverted = Reverted(value, live, stored);
                var setBy = reverted && scope == "element" && api?.GetTag(element) is { } tag ? api.CallSite(element, tag) : null;
                done(ReactorComponentOps.Json(w =>
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
                    w.WriteBoolean("settled", settled);
                    if (reverted)
                    {
                        w.WriteBoolean("reverted", true);
                        if (setBy is { } site)
                        {
                            w.WriteStartObject("setBy");
                            w.WriteString("file", site.File);
                            w.WriteNumber("line", site.Line);
                            w.WriteEndObject();
                        }
                    }
                }));
            }
            catch (Exception ex)
            {
                done(ReactorComponentOps.Unavailable(ex.GetType().Name + ": " + ex.Message));
            }
        }
        if (!element.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,
                () => ReactorComponentOps.WhenIdle(
                    work => element.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => work()),
                    () => ReactorApi.IsIdle(host), RenderBudgetMs, done, Report)))
        {
            Report(false);
        }
    }

    private const int RenderBudgetMs = 3000;

    /// <summary>
    /// Whether the live value is no longer the one written: the app set the key again (or removed it) while re-rendering.
    /// </summary>
    internal static bool Reverted(object? written, object? live, bool found) =>
        !found || !(ReferenceEquals(written, live) || Equals(written, live) || FormatValue(written) == FormatValue(live));

    /// <summary>What to do when the application's resources cannot take the key.</summary>
    internal const string ReadOnlyAppResources =
        "Application.Resources is loaded from a Source (such as XamlControlsResources), so it cannot take this key. " +
        "Set it on an element with --scope element <selector>, or add a ResourceDictionary to Application.Resources.MergedDictionaries in the app";

    private static ResourceDictionary? s_addedAppKeys;

    /// <summary>
    /// Where a new application-scope key goes: <c>Application.Resources</c> itself, or, when that dictionary is loaded
    /// from a <c>Source</c> and takes no local keys (Reactor apps set it to a <c>XamlControlsResources</c>), a dictionary
    /// DevTools merges into it once and reuses.
    /// </summary>
    private static (ResourceDictionary Dictionary, string Path) DictionaryForNewAppKeys(ResourceDictionary app)
    {
        return HasSource(app) ? AddedAppKeys(app) : (app, "Resources");
    }

    private static (ResourceDictionary Dictionary, string Path) AddedAppKeys(ResourceDictionary app)
    {
        var merged = app.MergedDictionaries;
        for (var i = 0; i < merged.Count; i++)
        {
            if (ReferenceEquals(merged[i], s_addedAppKeys))
            {
                return (merged[i], $"Application.Resources.MergedDictionaries[{i}]");
            }
        }
        var added = new ResourceDictionary();
        merged.Add(added);
        s_addedAppKeys = added;
        return (added, $"Application.Resources.MergedDictionaries[{merged.Count - 1}]");
    }

    internal static bool HasSource(ResourceDictionary dictionary)
    {
        try { return dictionary.Source is not null; }
        catch (System.Runtime.InteropServices.COMException) { return false; }
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
        // Reactor with Theme.NotifyResourcesChanged re-resolves every ThemeRef and re-renders every host itself.
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
        foreach (var assembly in (notified ? Array.Empty<Assembly>() : AppDomain.CurrentDomain.GetAssemblies()).Where(assembly => !assembly.IsDynamic))
        {
            ReactorInternals.InvalidateThemeCache(assembly);
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
