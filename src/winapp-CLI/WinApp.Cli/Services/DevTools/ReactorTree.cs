// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace WinApp.Cli.Services.DevTools;

/// <summary>The component a Reactor element mounts: a class component, a function or a memo.</summary>
internal sealed record ReactorComponent(string Kind, string Name, string? Type);

/// <summary>
/// Where a Reactor root renders (<c>window</c>, <c>island</c> for a <c>ReactorHostControl</c> inside XAML, or
/// another <c>host</c>), the window it is shown in, and its root component.
/// </summary>
internal sealed record ReactorHost(string Kind, string? Window, string? WindowId, string? WindowKey, string? Title,
    ReactorComponent? Root)
{
    /// <summary>Where the host was mounted (<c>ReactorApp.Run</c>, <c>OpenWindow</c> or <c>Mount</c>), when Reactor records it.</summary>
    public string? MountFile { get; init; }

    public int MountLine { get; init; }

    /// <summary>The mount site as <c>File.cs:line</c>.</summary>
    public string? ShortMount => MountFile is null ? null : ReactorNode.Short(MountFile, MountLine);
}

/// <summary>
/// One Microsoft.UI.Reactor element and the realized WinUI element it produced (<see cref="Handle"/>). Parent and
/// children follow Reactor's own nesting, skipping the template and framework elements between them.
/// </summary>
internal sealed record ReactorNode(
    string Handle,
    string? Parent,
    string Element,
    string? Key,
    string? File,
    int Line,
    ReactorComponent? Component)
{
    public List<ReactorNode> Children { get; } = [];

    /// <summary>Set on the control a host's root component rendered.</summary>
    public ReactorHost? Host { get; init; }

    /// <summary>The 1-based column of the call site; 0 when Reactor records none.</summary>
    public int Column { get; init; }

    /// <summary>
    /// The C# variable or member the element was assigned to (<c>var incrementButton = Button(...)</c>), shown like an
    /// <c>x:Name</c>; null for an element built inline.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// <c>csharp-variable</c> when Reactor recorded <see cref="Name"/>; <c>csharp-source-text</c> when DevTools read it
    /// from the call site's line, which may have changed since the build.
    /// </summary>
    public string? NameSource { get; init; }

    /// <summary>Whether the control's AutomationProperties.Name is the default Reactor copied from its caption.</summary>
    public bool DefaultAutomationName { get; init; }

    /// <summary><c> #incrementButton</c>, or empty when the element has no C# name.</summary>
    public string NameSuffix => Name is null ? "" : " #" + Name;

    /// <summary>The C# call site as <c>file:line</c>, or null when Reactor recorded none.</summary>
    public string? Source => File is null ? null : Line > 0 ? $"{File}:{Line}" : File;

    /// <summary>The call site as <c>File.cs:line</c>, short enough for a tree line.</summary>
    public string? ShortSource
    {
        get
        {
            return File is null ? null : Short(File, Line);
        }
    }

    // C# call sites are absolute compile-time paths (or /_/ deterministic ones); the file name reads best.
    internal static string Short(string path, int line)
    {
        var file = path.Replace('/', '\\');
        file = file[(file.LastIndexOf('\\') + 1)..];
        return line > 0 ? $"{file}:{line}" : file;
    }
}

/// <summary>The window a group of top-level elements lives in.</summary>
internal sealed record VisualTreeWindow(string Window, string Title, string Surface, IReadOnlyList<string> Roots)
{
    public static IReadOnlyList<VisualTreeWindow> ParseList(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("windows", out var windows) || windows.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var list = new List<VisualTreeWindow>();
        foreach (var window in windows.EnumerateArray())
        {
            if (window.ValueKind != JsonValueKind.Object || ReactorDescription.ReadString(window, "window") is not { } hwnd)
            {
                continue;
            }
            var roots = window.TryGetProperty("roots", out var r) && r.ValueKind == JsonValueKind.Array
                ? r.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray()
                : [];
            list.Add(new VisualTreeWindow(hwnd, ReactorDescription.ReadString(window, "title") ?? "",
                ReactorDescription.ReadString(window, "surface") ?? "", roots));
        }
        return list;
    }
}

internal sealed record ReactorDescription(
    string State,
    string? Reason,
    string? Version,
    bool? SourceMap,
    IReadOnlyList<ReactorNode> Roots,
    IReadOnlyDictionary<string, ReactorNode> ByHandle,
    int Unmapped,
    bool Truncated,
    bool Complete = true,
    string? Annotations = null)
{
    public const string AbsentToken = "reactor-absent";
    public const string UnavailableToken = "reactor-unavailable";
    public const string NoSourceMapToken = "reactor-no-sourcemap";

    public bool IsReactor => State == "ok";

    /// <summary>Whether the app uses Reactor, even when its elements could not be read.</summary>
    public bool Present => State != "absent";

    /// <summary>
    /// The <c>reactor</c> object of an annotated <c>VisualTree.enumerate</c> (or <c>DevTools.negotiate</c>):
    /// <c>present</c>, <c>agent</c> and, when the agent described the elements, the <c>Reactor.describe</c> members.
    /// </summary>
    public static ReactorDescription? ParseAnnotations(JsonElement reactor)
    {
        if (reactor.ValueKind != JsonValueKind.Object ||
            !reactor.TryGetProperty("present", out var present) || present.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }
        var agent = ReadString(reactor, "agent") ?? "absent";
        // Without the agent the tree still comes from what Reactor published on each control (Native AOT, trimmed).
        var described = agent == "ok" || ReadString(reactor, "annotations") == "published";
        var state = !present.GetBoolean() ? "absent" : described && reactor.TryGetProperty("nodes", out _) ? "ok" : "unavailable";
        var reason = ReadString(reactor, "reason") ?? (state == "unavailable" && agent == "absent" ? "no managed DevTools agent is loaded in this app" : null);
        return Build(reactor, state, reason);
    }

    public static ReactorDescription? Parse(string? resultJson)
    {
        if (string.IsNullOrEmpty(resultJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(resultJson, TapWireJson.DocumentOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || ReadString(root, "state") is not { Length: > 0 } state)
            {
                return null;
            }
            return Build(root, state, ReadString(root, "reason"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ReactorDescription Build(JsonElement root, string state, string? reason)
    {
        var files = new List<string>();
        if (root.TryGetProperty("files", out var fileArray) && fileArray.ValueKind == JsonValueKind.Array)
        {
            files.AddRange(fileArray.EnumerateArray().Select(file => file.GetString() ?? string.Empty));
        }

        var ordered = new List<ReactorNode>();
        var byHandle = new Dictionary<string, ReactorNode>(StringComparer.Ordinal);
        if (root.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var node in nodes.EnumerateArray())
            {
                if (ParseNode(node, files) is { } parsed && byHandle.TryAdd(parsed.Handle, parsed))
                {
                    ordered.Add(parsed);
                }
            }
        }

        var roots = new List<ReactorNode>();
        foreach (var node in ordered)
        {
            if (node.Parent is not null && node.Parent != node.Handle && byHandle.TryGetValue(node.Parent, out var parent))
            {
                parent.Children.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        return new ReactorDescription(
            state,
            reason,
            ReadString(root, "version"),
            root.TryGetProperty("sourceMap", out var sourceMap) && sourceMap.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? sourceMap.GetBoolean() : null,
            roots,
            byHandle,
            root.TryGetProperty("unmapped", out var unmapped) && unmapped.TryGetInt32(out var count) ? count : 0,
            root.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True,
            !root.TryGetProperty("complete", out var complete) || complete.ValueKind != JsonValueKind.False,
            ReadString(root, "annotations"));
    }

    /// <summary>
    /// The no-match message for a component name. A description taken while a window was opening can miss elements,
    /// so then the reader is told to run the command again rather than that the component is not rendered.
    /// </summary>
    public string ExplainNoComponent(string component) => Complete
        ? $"No rendered Reactor component is named <{component}>. Run `winapp devtools inspect` to list the components that are rendered now."
        : $"No <{component}> was found, but the app was still changing and not every window answered. Run the command again.";

    /// <summary>
    /// Why there is no Reactor tree to show, as the action the reader should take; null when there is one.
    /// </summary>
    public string? ExplainIfEmpty()
    {
        return State switch
        {
            "absent" => "This app does not use Microsoft.UI.Reactor.",
            "unavailable" => ExplainUnavailable(),
            _ when Roots.Count == 0 => "Reactor is loaded but no rendered element carries a Reactor tag yet.",
            _ => null,
        };
    }

    /// <summary>The error token for <see cref="ExplainIfEmpty"/>.</summary>
    public string Token => State switch
    {
        "absent" => AbsentToken,
        "unavailable" => UnavailableToken,
        _ => "no-match",
    };

    /// <summary>The one note shown when the app uses Reactor but its components cannot be read.</summary>
    public string ExplainUnavailable() =>
        $"This app uses Microsoft.UI.Reactor, but its components cannot be read: {(Reason ?? "the managed DevTools agent did not answer").TrimEnd('.')}. " +
        "Launch it with `winapp run --devtools`. A Debug build loads the managed agent; Native AOT, trimmed and " +
        "late-attached apps show their components when Reactor publishes its diagnostics, which `winapp run --devtools` " +
        "turns on (REACTOR_DIAGNOSTICS=1).";

    /// <summary>
    /// Whether the tree came from what Reactor published on the controls rather than from the managed agent, so
    /// state, handlers, re-renders and logs are not available.
    /// </summary>
    public bool StaticOnly => IsReactor && Annotations == "published";

    /// <summary>The one note for a tree described without the agent: what still works and what does not.</summary>
    public const string StaticOnlyNote =
        "Reactor components, call sites and names come from the diagnostics Reactor publishes on each control; " +
        "the managed DevTools agent is not loaded (Native AOT, trimmed or late-attached app), so state, handlers, " +
        "re-renders and logs are unavailable.";

    /// <summary>
    /// Why elements show no C# source, when that is the launch rather than the element; null otherwise.
    /// </summary>
    public string? ExplainMissingSource()
    {
        // Nothing to explain when there are no Reactor elements here (a XAML window in a hybrid app) or some have sources.
        if (!IsReactor || ByHandle.Count == 0 || ByHandle.Values.Any(node => node.File is not null))
        {
            return null;
        }
        return SourceMap == false
            ? "Reactor source mapping is off, so no C# locations were recorded. Launch with `winapp run --devtools` " +
              "(it sets REACTOR_SOURCEMAP=1) and build in Debug, or set <ReactorSourceMap>true</ReactorSourceMap>."
            : "No C# locations were recorded. Build in Debug, or set <ReactorSourceMap>true</ReactorSourceMap>, and relaunch.";
    }

    private static ReactorNode? ParseNode(JsonElement node, List<string> files)
    {
        if (node.ValueKind != JsonValueKind.Object || ReadString(node, "handle") is not { Length: > 0 } handle)
        {
            return null;
        }

        string? file = null;
        if (node.TryGetProperty("file", out var fileIndex) && fileIndex.TryGetInt32(out var index) && index >= 0 && index < files.Count)
        {
            file = files[index];
        }

        ReactorComponent? component = null;
        if (node.TryGetProperty("component", out var c) && c.ValueKind == JsonValueKind.Object)
        {
            component = ParseComponent(c);
        }

        ReactorHost? host = null;
        if (node.TryGetProperty("host", out var h) && h.ValueKind == JsonValueKind.Object)
        {
            string? mountFile = null;
            var mountLine = 0;
            if (h.TryGetProperty("mount", out var m) && m.ValueKind == JsonValueKind.Object &&
                m.TryGetProperty("file", out var mf) && mf.TryGetInt32(out var mi) && mi >= 0 && mi < files.Count)
            {
                mountFile = files[mi];
                mountLine = m.TryGetProperty("line", out var ml) && ml.TryGetInt32(out var mln) ? mln : 0;
            }
            host = new ReactorHost(ReadString(h, "kind") ?? "host", ReadString(h, "window"), ReadString(h, "windowId"),
                ReadString(h, "windowKey"), ReadString(h, "title"),
                h.TryGetProperty("root", out var r) && r.ValueKind == JsonValueKind.Object ? ParseComponent(r) : null)
            { MountFile = mountFile, MountLine = mountLine };
        }

        return new ReactorNode(
            handle,
            ReadString(node, "parent"),
            ReadString(node, "element") ?? "Element",
            ReadString(node, "key"),
            file,
            node.TryGetProperty("line", out var line) && line.TryGetInt32(out var number) ? number : 0,
            component)
        {
            Host = host,
            Column = node.TryGetProperty("column", out var column) && column.TryGetInt32(out var col) ? col : 0,
            Name = ReadString(node, "name"),
            NameSource = ReadString(node, "name") is null ? null : ReadString(node, "nameSource") ?? "csharp-variable",
            DefaultAutomationName = node.TryGetProperty("defaultAutomationName", out var defaultName) && defaultName.ValueKind == JsonValueKind.True,
        };
    }

    private static ReactorComponent ParseComponent(JsonElement c) =>
        new(ReadString(c, "kind") ?? "class", ReadString(c, "name") ?? "Component", ReadString(c, "type"));

    internal static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
