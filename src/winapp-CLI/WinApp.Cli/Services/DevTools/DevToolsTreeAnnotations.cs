// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Spectre.Console;

namespace WinApp.Cli.Services.DevTools;

/// <summary>
/// What an annotated tree adds to the WinUI elements: the Microsoft.UI.Reactor element (component, key and C#
/// call site) behind each control, the root component of each Reactor host, and the window each top-level element
/// lives in. One tree for both kinds of authored code: an element is the app's own when it has a XAML declaration
/// or a Reactor call site.
/// </summary>
internal sealed class DevToolsTreeAnnotations
{
    public static DevToolsTreeAnnotations Empty { get; } = new(null, [], raw: false);

    public DevToolsTreeAnnotations(ReactorDescription? reactor, IReadOnlyList<VisualTreeWindow> windows, bool raw)
    {
        Reactor = reactor;
        Windows = windows;
        Raw = raw;
    }

    public ReactorDescription? Reactor { get; }

    public IReadOnlyList<VisualTreeWindow> Windows { get; }

    /// <summary>The raw WinUI tree (<c>--all</c>): Reactor information is shown inline instead of replacing the line.</summary>
    public bool Raw { get; }

    public ReactorNode? For(string handle) => Reactor?.ByHandle.GetValueOrDefault(handle);

    /// <summary>Whether the tree spans several windows, so it reads as one group per window.</summary>
    public bool Grouped => Windows.Select(window => window.Window).Distinct(StringComparer.Ordinal).Count() > 1;

    /// <summary>
    /// The one note for Reactor apps whose components could not be read, or whose call sites were not recorded,
    /// with its error token; null when there is nothing to say. A window showing Reactor's error screen comes first:
    /// its root's elements are gone, which is why it has no call sites.
    /// </summary>
    public (string Token, string Message)? Note
    {
        get
        {
            if (Reactor is null || !Reactor.Present)
            {
                return null;
            }
            if (!Reactor.IsReactor)
            {
                return (ReactorDescription.UnavailableToken, Reactor.ExplainUnavailable());
            }
            var errorScreens = Reactor.ByHandle.Values.Select(node => node.Host).OfType<ReactorHost>()
                .Where(host => host.ErrorScreen is not null).Select(ReactorDescription.ExplainErrorScreen).ToList();
            if (errorScreens.Count > 0)
            {
                return (ReactorDescription.ErrorScreenToken, string.Join(Environment.NewLine, errorScreens));
            }
            return Reactor.ExplainMissingSource() is { } missing ? (ReactorDescription.NoSourceMapToken, missing) : null;
        }
    }

    public bool Matches(VisualTreeNode node, string filter)
    {
        if (DevToolsFormat.Matches(node, filter))
        {
            return true;
        }
        var reactor = For(node.Handle);
        return reactor is not null && (Contains(reactor.Element, filter) || Contains(reactor.Component?.Name, filter) ||
            Contains(reactor.Name is null ? null : "#" + reactor.Name, filter) ||
            Contains(reactor.Key, filter) || Contains(reactor.Source, filter) || Contains(reactor.Host?.Root?.Name, filter));
    }

    /// <summary>The element's line (without its compact values).</summary>
    public string Line(VisualTreeNode node)
    {
        if (For(node.Handle) is not { } reactor)
        {
            return DevToolsRender.Node(node);
        }
        var key = reactor.Key is null ? "" : $" [grey]key={Markup.Escape(reactor.Key)}[/]";
        // The C# variable the element was assigned to reads like an x:Name; a Name the control itself has wins.
        var shownName = node.Name.Length > 0 ? node.Name : reactor.Name;
        var name = shownName is null ? "" : $" [green]#{Markup.Escape(shownName)}[/]";
        var source = reactor.ShortSource is { } site ? $" [blue]{Markup.Escape(site)}[/]" : "";
        var label = reactor.Component is { } component
            ? $"[bold]<{Markup.Escape(component.Name)}>[/]" + (component.Kind == "class" ? "" : $" [grey]{Markup.Escape(component.Kind)}[/]")
            : Markup.Escape(reactor.Element);
        if (Raw)
        {
            // --all keeps every WinUI line as it was; the Reactor element follows it.
            var element = reactor.Component is null && reactor.Element == node.ShortType ? "" : " " + label;
            var root = reactor.Host?.Root is { } r ? $" [grey]root[/] [bold]<{Markup.Escape(r.Name)}>[/]" : "";
            var rawName = node.Name.Length > 0 ? "" : name;
            return DevToolsRender.Node(node) + (element.Length + rawName.Length + key.Length + source.Length + root.Length == 0
                ? "" : " [grey]·[/]" + element + rawName + key + source + root);
        }
        if (node.File is not null)
        {
            // Declared in XAML and tagged by Reactor: keep the XAML line and add the call site.
            return DevToolsRender.Node(node) + (node.Name.Length > 0 ? "" : name) + key + source;
        }
        var winui = reactor.Component is null && !string.Equals(node.ShortType, reactor.Element, StringComparison.Ordinal)
            ? $" [grey]({Markup.Escape(node.ShortType)})[/]" : "";
        return $"{DevToolsRender.Handle(node.Selector)} {label}{name}{winui}{key}{source}";
    }

    /// <summary>
    /// The synthesized line for the root component a host rendered into this control; null when the control is not
    /// a host's root. The root control carries no tag of its own, so without this line the root component is invisible.
    /// </summary>
    public string? RootLine(VisualTreeNode node)
    {
        if (Raw || For(node.Handle)?.Host is not { Root: { } root } host)
        {
            return null;
        }
        var where = host.Kind switch
        {
            "island" => "Reactor island root",
            "window" => "root",
            _ => "Reactor root",
        };
        var kind = root.Kind == "class" ? "" : $" [grey]{Markup.Escape(root.Kind)}[/]";
        var mount = host.ShortMount is { } site ? $" [blue]{Markup.Escape(site)}[/]" : "";
        var errorScreen = host.ErrorScreen is null ? "" : " [red]showing Reactor's error screen[/]";
        return $"{DevToolsRender.Handle(node.Selector)} [bold]<{Markup.Escape(root.Name)}>[/]{kind} [grey]{where}[/]{mount}{errorScreen}";
    }

    /// <summary>Top-level elements in window order; a single group with no window when the tree is not grouped.</summary>
    public IReadOnlyList<(VisualTreeWindow? Window, IReadOnlyList<VisualTreeNode> Roots)> Group(IReadOnlyList<VisualTreeNode> roots)
    {
        if (!Grouped)
        {
            return [(null, roots)];
        }
        var groups = new List<(VisualTreeWindow? Window, IReadOnlyList<VisualTreeNode> Roots)>();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var window in Windows.DistinctBy(window => window.Window, StringComparer.Ordinal))
        {
            var members = Windows.Where(w => w.Window == window.Window).SelectMany(w => w.Roots).ToHashSet(StringComparer.Ordinal);
            var inWindow = roots.Where(root => members.Contains(root.Handle)).ToArray();
            if (inWindow.Length == 0)
            {
                continue;
            }
            placed.UnionWith(inWindow.Select(root => root.Handle));
            groups.Add((window, inWindow));
        }
        var other = roots.Where(root => !placed.Contains(root.Handle)).ToArray();
        if (other.Length > 0)
        {
            groups.Add((null, other));
        }
        return groups;
    }

    /// <summary>The Reactor window shown in <paramref name="hwnd"/>, from the host its root rendered into.</summary>
    public ReactorHost? ReactorWindow(string hwnd) =>
        Reactor?.ByHandle.Values.Select(node => node.Host)
            .FirstOrDefault(host => host is { Kind: "window" } && host.Window == hwnd);

    public string WindowHeader(VisualTreeWindow? window)
    {
        if (window is null)
        {
            return "[bold]Other surfaces[/]";
        }
        var title = window.Title.Length > 0 ? $"Window \"{Markup.Escape(window.Title)}\"" : "Window";
        var reactor = ReactorWindow(window.Window) is { } host
            ? $" [grey]Reactor {Markup.Escape(host.WindowId ?? "window")}{(host.WindowKey is null ? "" : $" key={Markup.Escape(host.WindowKey)}")}[/]"
            : "";
        return $"[bold]{title}[/] [grey]-w {Markup.Escape(window.Window)}[/]{reactor}";
    }

    /// <summary>The Reactor members of a node's JSON: <c>reactor</c> when a Reactor element produced it.</summary>
    public void WriteNodeExtras(Utf8JsonWriter writer, VisualTreeNode node)
    {
        if (For(node.Handle) is not { } reactor)
        {
            return;
        }
        writer.WriteStartObject("reactor");
        writer.WriteString("element", reactor.Element);
        if (reactor.Name is not null)
        {
            writer.WriteString("name", reactor.Name);
            writer.WriteString("nameSource", reactor.NameSource ?? "csharp-variable");
        }
        if (reactor.Key is not null) { writer.WriteString("key", reactor.Key); }
        if (reactor.File is not null)
        {
            writer.WriteString("file", reactor.File);
            writer.WriteNumber("line", reactor.Line);
            if (reactor.Column > 0) { writer.WriteNumber("column", reactor.Column); }
        }
        if (reactor.Component is { } component) { WriteComponent(writer, "component", component); }
        if (reactor.Host is { } host)
        {
            writer.WriteStartObject("host");
            writer.WriteString("kind", host.Kind);
            if (host.Window is not null) { writer.WriteString("window", host.Window); }
            if (host.WindowId is not null) { writer.WriteString("windowId", host.WindowId); }
            if (host.WindowKey is not null) { writer.WriteString("windowKey", host.WindowKey); }
            if (host.Root is { } root) { WriteComponent(writer, "root", root); }
            WriteMount(writer, host);
            if (host.ErrorScreen is not null) { writer.WriteString("errorScreen", host.ErrorScreen); }
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static void WriteMount(Utf8JsonWriter writer, ReactorHost host)
    {
        if (host.MountFile is null)
        {
            return;
        }
        writer.WriteStartObject("mount");
        writer.WriteString("file", host.MountFile);
        writer.WriteNumber("line", host.MountLine);
        writer.WriteEndObject();
    }

    /// <summary>The envelope members: <c>reactor</c> state, the windows, and the Reactor note when there is one.</summary>
    public void WriteEnvelope(Utf8JsonWriter writer)
    {
        if (Reactor is { } reactor)
        {
            writer.WriteStartObject("reactor");
            writer.WriteBoolean("present", reactor.Present);
            if (reactor.Present)
            {
                // Published annotations describe the tree without the agent, so state and handlers stay unavailable.
                var published = reactor.Annotations == "published";
                writer.WriteString("agent", !reactor.IsReactor ? "unavailable" : published ? "absent" : "ok");
                if (reactor.IsReactor && reactor.Annotations is { } source) { writer.WriteString("annotations", source); }
                if (reactor.Version is not null) { writer.WriteString("version", reactor.Version); }
                if (reactor.SourceMap is bool sourceMap) { writer.WriteBoolean("sourceMap", sourceMap); }
                if (reactor.IsReactor) { writer.WriteNumber("elements", reactor.ByHandle.Count); }
                if (reactor.Truncated) { writer.WriteBoolean("truncated", true); }
            }
            if (Note is { } note)
            {
                writer.WriteStartObject("warning");
                writer.WriteString("token", note.Token);
                writer.WriteString("message", note.Message);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        if (Windows.Count > 0)
        {
            writer.WriteStartArray("windows");
            foreach (var window in Windows.DistinctBy(window => window.Window, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("window", window.Window);
                writer.WriteString("title", window.Title);
                if (ReactorWindow(window.Window) is { } host)
                {
                    writer.WriteStartObject("reactor");
                    if (host.WindowId is not null) { writer.WriteString("windowId", host.WindowId); }
                    if (host.WindowKey is not null) { writer.WriteString("windowKey", host.WindowKey); }
                    if (host.Root is { } root) { WriteComponent(writer, "root", root); }
                    WriteMount(writer, host);
                    if (host.ErrorScreen is not null) { writer.WriteString("errorScreen", host.ErrorScreen); }
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
    }

    /// <summary>The window a top-level element belongs to, or null.</summary>
    public string? WindowOf(string handle) =>
        Windows.FirstOrDefault(window => window.Roots.Contains(handle, StringComparer.Ordinal))?.Window;

    private static void WriteComponent(Utf8JsonWriter writer, string name, ReactorComponent component)
    {
        writer.WriteStartObject(name);
        writer.WriteString("kind", component.Kind);
        writer.WriteString("name", component.Name);
        if (component.Type is not null) { writer.WriteString("type", component.Type); }
        writer.WriteEndObject();
    }

    private static bool Contains(string? value, string filter) =>
        value is not null && value.Contains(filter, StringComparison.OrdinalIgnoreCase);
}
