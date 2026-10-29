// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WinApp.DevTools.Managed;

/// <summary>
/// Describes the Microsoft.UI.Reactor element behind each realized WinUI control, through Reactor's public
/// <c>Reconciler.GetElementTag</c>. Reactor is never referenced at compile time: an app without it answers
/// <c>absent</c>, and an app whose Reactor lacks the members answers <c>unavailable</c>.
/// </summary>
internal static class ReactorProvider
{
    internal const int MaxVisited = 50000;
    private const int PinnedAnswers = 32;
    private static readonly Guid IUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly object PinLock = new();
    // The answer reports COM identities. Keeping the described elements alive until later answers replace
    // them stops a released element's identity from being reused before the native side maps it to a handle.
    private static readonly Queue<List<object>> Pinned = new();

    public static string Run(BindingTarget target)
    {
        bool handedOff = false;
        try
        {
            if (target.Project() is not DependencyObject root)
            {
                return Unavailable("the selected object is not a XAML element");
            }

            handedOff = true;
            return BindingDiagnosis.RunOnUiThread(action => root.DispatcherQueue.TryEnqueue(() => action()),
                () => target.Project() is DependencyObject current
                    ? Describe(current, ReactorApi.TryLoad(AppDomain.CurrentDomain.GetAssemblies()))
                    : Unavailable("the selected object is not a XAML element"), target);
        }
        catch (Exception ex)
        {
            return Unavailable(ex.GetType().Name + ": " + ex.Message);
        }
        finally { if (!handedOff) target.Dispose(); }
    }

    /// <summary>
    /// Whether Reactor is loaded, without touching the UI thread or any element: what <c>DevTools.negotiate</c>
    /// advertises, so apps without Reactor never pay for a description.
    /// </summary>
    public static string Probe()
    {
        try
        {
            var api = ReactorApi.TryLoad(AppDomain.CurrentDomain.GetAssemblies());
            if (api is null)
            {
                return "{\"state\":\"absent\"}";
            }
            if (api.Error is not null)
            {
                return Unavailable(api.Error, api.Version);
            }

            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartObject();
                w.WriteString("state", "ok");
                w.WriteString("version", api.Version);
                w.WriteString("api", api.Api);
                if (api.SourceMapEnabled() is bool enabled)
                {
                    w.WriteBoolean("sourceMap", enabled);
                }
                w.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (Exception ex)
        {
            return Unavailable(ex.GetType().Name + ": " + ex.Message);
        }
    }

    internal static string Describe(DependencyObject root, ReactorApi? api)
    {
        if (api is null)
        {
            return "{\"state\":\"absent\"}";
        }
        if (api.Error is not null)
        {
            return Unavailable(api.Error, api.Version);
        }

        var hosts = api.WindowHosts();
        var files = new Dictionary<string, int>(StringComparer.Ordinal);
        var keep = new List<object>();
        var seen = new HashSet<nint>();
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("state", "ok");
            w.WriteString("version", api.Version);
            if (api.SourceMapEnabled() is bool enabled)
            {
                w.WriteBoolean("sourceMap", enabled);
            }

            w.WriteStartArray("nodes");
            var stack = new Stack<(DependencyObject Node, nint Parent)>();
            stack.Push((root, 0));
            if (root is UIElement { XamlRoot: { } xamlRoot } && VisualTreeHelper.GetParent(root) is null)
            {
                foreach (var child in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot).Select(popup => popup.Child).OfType<UIElement>())
                {
                    stack.Push((child, 0));
                }
            }

            var visited = 0;
            var truncated = false;
            while (stack.Count > 0)
            {
                if (++visited > MaxVisited)
                {
                    truncated = true;
                    break;
                }

                var (node, parent) = stack.Pop();
                var reactorParent = parent;
                if (node is UIElement element)
                {
                    // A XAML-hosted island renders into its own content; its root control is found further down.
                    if (api.IslandHost(element) is { } island)
                    {
                        hosts[island.RootControl] = island;
                    }
                    var tag = api.GetTag(element);
                    var host = hosts.GetValueOrDefault(element);
                    if ((tag is not null || host is not null) && Identity(element) is var id && id != 0 && seen.Add(id))
                    {
                        keep.Add(element);
                        WriteNode(w, api, tag, element, host, id, parent, files);
                        reactorParent = id;
                    }
                }

                var count = VisualTreeHelper.GetChildrenCount(node);
                for (var i = count - 1; i >= 0; i--)
                {
                    stack.Push((VisualTreeHelper.GetChild(node, i), reactorParent));
                }
            }
            w.WriteEndArray();

            w.WriteStartArray("files");
            foreach (var file in files.OrderBy(pair => pair.Value))
            {
                w.WriteStringValue(file.Key);
            }
            w.WriteEndArray();
            w.WriteBoolean("truncated", truncated);
            w.WriteEndObject();
        }

        lock (PinLock)
        {
            Pinned.Enqueue(keep);
            while (Pinned.Count > PinnedAnswers)
            {
                Pinned.Dequeue();
            }
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// The references from one element whose target Reactor hasn't created, which WinUI can't show. Answers
    /// <c>supported:false</c> (and no edges) for a Reactor without <c>ReactorDiagnostics.GetReferenceEdges</c>.
    /// </summary>
    public static string RunReferences(BindingTarget target)
    {
        bool handedOff = false;
        try
        {
            if (target.Project() is not UIElement first)
            {
                return Unavailable("the selected object is not a XAML element");
            }

            handedOff = true;
            return BindingDiagnosis.RunOnUiThread(action => first.DispatcherQueue.TryEnqueue(() => action()),
                () =>
                {
                    if (target.Project() is not UIElement current)
                    {
                        return Unavailable("the selected object is not a XAML element");
                    }
                    var api = ReactorApi.TryLoad(AppDomain.CurrentDomain.GetAssemblies());
                    return api is null ? "{\"state\":\"absent\"}"
                        : api.Error is not null ? Unavailable(api.Error, api.Version)
                        : DescribeReferences(api.PendingReferences(current));
                }, target);
        }
        catch (Exception ex)
        {
            return Unavailable(ex.GetType().Name + ": " + ex.Message);
        }
        finally { if (!handedOff) target.Dispose(); }
    }

    internal static string DescribeReferences(IReadOnlyList<ReactorPendingReference>? pending)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("state", "ok");
            w.WriteBoolean("supported", pending is not null);
            w.WriteStartArray("edges");
            foreach (var edge in pending ?? [])
            {
                w.WriteStartObject();
                w.WriteString("property", edge.Property);
                if (edge.IsList)
                {
                    w.WriteBoolean("list", true);
                    w.WriteNumber("index", edge.Index);
                }
                if (edge.TargetType is { } type) { w.WriteString("targetType", type); }
                if (edge.AutomationId is { } automationId) { w.WriteString("automationId", automationId); }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// What Reactor set on one element: its own record properties, the modifiers Reactor applies, and the props
    /// of the component it mounts. Answers <c>untagged</c> when no Reactor element produced it.
    /// </summary>
    public static string RunElement(BindingTarget target)
    {
        bool handedOff = false;
        try
        {
            if (target.Project() is not UIElement first)
            {
                return Unavailable("the selected object is not a XAML element");
            }

            handedOff = true;
            return BindingDiagnosis.RunOnUiThread(action => first.DispatcherQueue.TryEnqueue(() => action()),
                () => target.Project() is UIElement current
                    ? DescribeElement(current, ReactorApi.TryLoad(AppDomain.CurrentDomain.GetAssemblies()))
                    : Unavailable("the selected object is not a XAML element"), target);
        }
        catch (Exception ex)
        {
            return Unavailable(ex.GetType().Name + ": " + ex.Message);
        }
        finally { if (!handedOff) target.Dispose(); }
    }

    internal static string DescribeElement(UIElement element, ReactorApi? api)
    {
        if (api is null)
        {
            return "{\"state\":\"absent\"}";
        }
        if (api.Error is not null)
        {
            return Unavailable(api.Error, api.Version);
        }

        var tag = api.GetTag(element);
        // Reactor's text diagnostics format the props of the component mounted here; DevTools redacts them.
        var shownProps = api.DescribeComponent(element);
        object? props = null;
        var hasComponentProps = shownProps is not null;
        if (shownProps is null)
        {
            props = api.ComponentProps(tag, out hasComponentProps);
        }
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("state", "ok");
            w.WriteBoolean("tagged", tag is not null);
            if (tag is not null)
            {
                w.WriteString("element", ReactorApi.ElementKind(tag.GetType()));
                if (api.Key(tag) is { Length: > 0 } key) { w.WriteString("key", key); }
                if (api.CallSite(element, tag) is { } site)
                {
                    w.WriteString("file", site.File);
                    w.WriteNumber("line", site.Line);
                    if (site.Column > 0) { w.WriteNumber("column", site.Column); }
                }
                if (api.Component(tag) is { } component) { WriteComponent(w, "component", component); }

                w.WriteStartArray("props");
                foreach (var prop in ReactorElementProps.RecordProps(tag, api.ElementBase))
                {
                    w.WriteStartObject();
                    w.WriteString("name", prop.Name);
                    w.WriteString("property", prop.Property);
                    w.WriteString("value", prop.Value);
                    if (prop.Redacted) { w.WriteBoolean("redacted", true); }
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                var applied = api.AppliedProperties(element);
                w.WriteString("modifierSource", applied is null ? "convention" : "reactor");
                w.WriteStartArray("modifiers");
                foreach (var modifier in applied ?? ReactorElementProps.Modifiers(tag))
                {
                    w.WriteStartObject();
                    w.WriteString("modifier", modifier.Modifier);
                    w.WriteString("property", modifier.Property);
                    var (value, redacted) = api.TextDiagnostics && modifier.Value is string text
                        ? ReactorElementProps.FormatText(modifier.Property, text)
                        : ReactorElementProps.Format(modifier.Property, modifier.Value);
                    w.WriteString("value", value);
                    if (redacted) { w.WriteBoolean("redacted", true); }
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                // ThemeRef bindings: the property follows a theme resource, through a Style Reactor synthesizes.
                w.WriteStartArray("themeBindings");
                if (ReactorReflect.Member(tag, "ThemeBindings") is System.Collections.IEnumerable bindings)
                {
                    foreach (var (property, resourceKey) in bindings.Cast<object?>()
                        .Select(binding => (Property: ReactorReflect.Member(binding, "Key") as string,
                            ResourceKey: ReactorReflect.Member(ReactorReflect.Member(binding, "Value"), "ResourceKey") as string))
                        .Where(binding => binding.Property is not null && binding.ResourceKey is not null))
                    {
                        w.WriteStartObject();
                        w.WriteString("property", ReactorElementProps.PropertyName(property!));
                        w.WriteString("resourceKey", resourceKey);
                        w.WriteEndObject();
                    }
                }
                w.WriteEndArray();

                w.WriteStartArray("handlers");
                foreach (var handler in ReactorHandlers.Read(tag))
                {
                    w.WriteStartObject();
                    w.WriteString("event", handler.Event);
                    w.WriteString("method", ReactorComponents.HandlerName(handler.Handler));
                    w.WriteString("signature", ReactorHandlers.Signature(handler.Handler));
                    if (handler.Modifier) { w.WriteBoolean("modifier", true); }
                    var invokable = ReactorHandlers.TryArguments(handler.Handler, element, "", out _, out var why);
                    w.WriteBoolean("invokable", invokable);
                    if (!invokable && why?.Contains("--value", StringComparison.Ordinal) == true) { w.WriteBoolean("needsValue", true); }
                    if (ReactorPdb.Locate(handler.Handler.Method) is { } location)
                    {
                        w.WriteString("file", location.File);
                        w.WriteNumber("line", location.Line);
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            if (hasComponentProps)
            {
                w.WriteStartObject("componentProps");
                if (props is not null) { w.WriteString("type", ReactorApi.TypeName(props.GetType())); }
                else if (shownProps is not null && api.MountedProps(tag) is { } record) { w.WriteString("type", ReactorApi.TypeName(record.GetType())); }
                w.WriteStartArray("values");
                var rows = shownProps is not null
                    ? shownProps.Props.Select(p => (p.Name, p.Value, p.Redacted)).ToList()
                    : ReactorElementProps.ComponentProps(props);
                foreach (var (name, value, redacted) in rows)
                {
                    w.WriteStartObject();
                    w.WriteString("name", name);
                    w.WriteString("value", value);
                    if (redacted) { w.WriteBoolean("redacted", true); }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteNode(Utf8JsonWriter w, ReactorApi api, object? tag, UIElement element, ReactorHostInfo? host,
        nint id, nint parent, Dictionary<string, int> files)
    {
        w.WriteStartObject();
        w.WriteString("id", Hex(id));
        if (parent != 0)
        {
            w.WriteString("parent", Hex(parent));
        }
        // An untagged host root still gets a node, named after the control it is, so its root component shows.
        w.WriteString("element", tag is null ? ReactorApi.TypeName(element.GetType()) : ReactorApi.ElementKind(tag.GetType()));
        if (tag is not null && api.Key(tag) is { Length: > 0 } key)
        {
            w.WriteString("key", key);
        }
        if (tag is not null && api.CallSite(element, tag) is { } site && site.File.Length > 0)
        {
            w.WriteNumber("file", FileIndex(files, site.File));
            w.WriteNumber("line", site.Line);
            if (site.Column > 0) { w.WriteNumber("column", site.Column); }
        }
        if (tag is not null && api.Component(tag) is { } component)
        {
            WriteComponent(w, "component", component);
        }
        if (tag is not null && api.HasDefaultAutomationName(element))
        {
            w.WriteBoolean("defaultAutomationName", true);
        }
        if (host is not null)
        {
            w.WriteStartObject("host");
            w.WriteString("kind", host.Kind);
            if (host.WindowId is not null) { w.WriteString("windowId", host.WindowId); }
            if (host.WindowKey is not null) { w.WriteString("windowKey", host.WindowKey); }
            if (host.Title is not null) { w.WriteString("title", host.Title); }
            if (host.Root is { } root) { WriteComponent(w, "root", root); }
            if (host.Mount is { } mount && mount.File.Length > 0)
            {
                w.WriteStartObject("mount");
                w.WriteNumber("file", FileIndex(files, mount.File));
                w.WriteNumber("line", mount.Line);
                if (mount.Column > 0) { w.WriteNumber("column", mount.Column); }
                w.WriteEndObject();
            }
            if (host.ErrorScreen is { } headline) { w.WriteString("errorScreen", headline); }
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    private static int FileIndex(Dictionary<string, int> files, string file)
    {
        if (!files.TryGetValue(file, out var index))
        {
            index = files.Count;
            files.Add(file, index);
        }
        return index;
    }

    private static void WriteComponent(Utf8JsonWriter w, string name, (string Kind, string? Type, string Name) component)
    {
        w.WriteStartObject(name);
        w.WriteString("kind", component.Kind);
        w.WriteString("name", component.Name);
        if (component.Type is not null)
        {
            w.WriteString("type", component.Type);
        }
        w.WriteEndObject();
    }

    private static string Hex(nint value) => "0x" + ((ulong)value).ToString("x", System.Globalization.CultureInfo.InvariantCulture);

    // The COM identity (canonical IUnknown) is the same pointer XamlDiagnostics' own handle resolves to,
    // including for app-derived types whose managed object is the controlling outer.
    internal static nint IdentityOf(object element) => Identity(element);

    private static nint Identity(object element)
    {
        var inspectable = WinRT.MarshalInspectable<object>.FromManaged(element);
        if (inspectable == 0)
        {
            return 0;
        }
        try
        {
            if (Marshal.QueryInterface(inspectable, in IUnknown, out var unknown) < 0 || unknown == 0)
            {
                return 0;
            }
            Marshal.Release(unknown);
            return unknown;
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    private static string Unavailable(string why, string? version = null)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("state", "unavailable");
            w.WriteString("reason", why);
            if (version is not null) { w.WriteString("version", version); }
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}

/// <summary>
/// One place Reactor renders a root: a <c>ReactorWindow</c>, a <c>ReactorHostControl</c> island inside XAML,
/// or a host reported by Reactor's public diagnostics. <see cref="RootControl"/> is the control the root
/// component rendered, which Reactor sets as the window's or island's content without a tag of its own.
/// </summary>
internal sealed record ReactorHostInfo(
    UIElement RootControl,
    string Kind,
    string? WindowId,
    string? WindowKey,
    string? Title,
    (string Kind, string? Type, string Name)? Root,
    (string File, int Line, int Column)? Mount = null,
    string? ErrorScreen = null);
/// <summary>
/// Reflection over Reactor, bound once per Reactor assembly. Reactor versions with the public diagnostics API
/// (<c>ReactorDiagnostics.GetHosts</c> with <c>DescribeComponent</c>) are read only through it;
/// older versions (published 0.1.0-preview.16 and earlier) are read through <see cref="ReactorInternals"/>, the one
/// place that touches Reactor internals. <see cref="PublicDiagnostics"/> picks the path once.
/// </summary>
internal sealed partial class ReactorApi
{
    private const string ReconcilerType = "Microsoft.UI.Reactor.Core.Reconciler";
    private const string DiagnosticsType = "Microsoft.UI.Reactor.Core.Diagnostics.ReactorDiagnostics";
    private static ReactorApi? cached;

    private readonly MethodInfo? getElementTag;
    private readonly PropertyInfo? callSite;
    private readonly PropertyInfo? key;
    private readonly Type? componentElement;
    private readonly PropertyInfo? componentType;
    private readonly Type? funcElement;
    private readonly Type? memoElement;
    private readonly PropertyInfo? sourceMapEnabled;
    private readonly MethodInfo? getHosts;
    private readonly MethodInfo? getAppliedProperties;
    private readonly MethodInfo? describeComponent;
    private readonly MethodInfo? trySetState;
    private readonly MethodInfo? rerenderComponent;
    private readonly Assembly reactorAssembly;
    private readonly MethodInfo? getReferenceEdges;
    private readonly MethodInfo? getSource;
    private readonly MethodInfo? unwrapDecorators;
    private readonly PropertyInfo? componentProps;
    private readonly Type? elementBase;

    private ReactorApi(Assembly assembly)
    {
        reactorAssembly = assembly;
        Version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "unknown";
        var reconciler = assembly.GetType(ReconcilerType);
        getElementTag = reconciler?.GetMethod("GetElementTag", BindingFlags.Public | BindingFlags.Static, [typeof(UIElement)]);
        var element = assembly.GetType("Microsoft.UI.Reactor.Core.Element");
        elementBase = element;
        callSite = element?.GetProperty("CallSite", BindingFlags.Public | BindingFlags.Instance);
        key = element?.GetProperty("Key", BindingFlags.Public | BindingFlags.Instance);
        componentElement = assembly.GetType("Microsoft.UI.Reactor.Core.ComponentElement");
        componentType = componentElement?.GetProperty("ComponentType", BindingFlags.Public | BindingFlags.Instance);
        componentProps = componentElement?.GetProperty("Props", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        funcElement = assembly.GetType("Microsoft.UI.Reactor.Core.FuncElement");
        memoElement = assembly.GetType("Microsoft.UI.Reactor.Core.MemoElement");
        var sourceMap = assembly.GetType("Microsoft.UI.Reactor.Diagnostics.ReactorSourceMap");
        sourceMapEnabled = sourceMap?.GetProperty("Enabled", BindingFlags.Public | BindingFlags.Static);
        getSource = sourceMap?.GetMethod("GetSource", BindingFlags.Public | BindingFlags.Static, [typeof(UIElement)]);
        unwrapDecorators = element is null ? null : sourceMap?.GetMethod("UnwrapDecorators", BindingFlags.NonPublic | BindingFlags.Static, [element]);
        var diagnostics = assembly.GetType(DiagnosticsType);
        getAppliedProperties = diagnostics?.GetMethod("GetAppliedProperties", BindingFlags.Public | BindingFlags.Static, [typeof(UIElement)]);
        getReferenceEdges = diagnostics?.GetMethod("GetReferenceEdges", BindingFlags.Public | BindingFlags.Static, [typeof(UIElement)]);
        getHosts = diagnostics?.GetMethod("GetHosts", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
        // Reactor's text component diagnostics: values formatted and parsed by Reactor itself (DevTools redacts them).
        describeComponent = diagnostics?.GetMethod("DescribeComponent", BindingFlags.Public | BindingFlags.Static, [typeof(UIElement)]);
        trySetState = diagnostics?.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method => method.Name == "TrySetState" && method.GetParameters() is { Length: 4 } p &&
                p[0].ParameterType == typeof(UIElement) && p[1].ParameterType == typeof(int) && p[2].ParameterType == typeof(string) && p[3].IsOut);
        rerenderComponent = diagnostics?.GetMethod("Rerender", BindingFlags.Public | BindingFlags.Static, [typeof(UIElement)]);
        PublicDiagnostics = getHosts is not null && describeComponent is not null;
        Internals = PublicDiagnostics ? null : new ReactorInternals(assembly);
        if (getElementTag is null || element is null)
        {
            Error = $"this Reactor version ({Version}) does not expose Reconciler.GetElementTag(UIElement)";
        }
    }

    public string Version { get; }

    public string? Error { get; }

    /// <summary>Whether this Reactor has the public diagnostics API; false for published 0.1.0-preview.16 and earlier.</summary>
    public bool PublicDiagnostics { get; }

    /// <summary><c>reactor</c> when DevTools reads Reactor through its public diagnostics API, <c>internal</c> otherwise.</summary>
    public string Api => PublicDiagnostics ? "reactor" : "internal";

    /// <summary>The internals adapter, only for a Reactor without the public diagnostics API.</summary>
    internal ReactorInternals? Internals { get; }

    public static ReactorApi? TryLoad(IEnumerable<Assembly> loaded)
    {
        if (cached is { } known)
        {
            return known;
        }
        foreach (var assembly in loaded)
        {
            if (assembly.IsDynamic || assembly.GetType(ReconcilerType) is null)
            {
                continue;
            }
            return cached = new ReactorApi(assembly);
        }
        return null;
    }

    internal static ReactorApi ForAssembly(Assembly assembly) => new(assembly);

    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>Every live Reactor root (windows, plain hosts and islands), keyed by the control its root rendered.</summary>
    public Dictionary<UIElement, ReactorHostInfo> WindowHosts()
    {
        var hosts = new Dictionary<UIElement, ReactorHostInfo>(ReferenceEqualityComparer.Instance);
        foreach (var host in Internals is { } internals ? internals.WindowHosts() : PublicHosts())
        {
            hosts.TryAdd(host.RootControl, host);
        }
        return hosts;
    }

    /// <summary>
    /// A <c>ReactorHostControl</c> island inside XAML, described by the root it renders. Only needed without the
    /// public host registry, which already lists every island.
    /// </summary>
    public ReactorHostInfo? IslandHost(UIElement element) => Internals?.IslandHost(element);

    // The root component Reactor's DescribeComponent names for a host's root control, with its type when one class has
    // that name.
    private (string Kind, string? Type, string Name)? SnapshotRoot(UIElement control)
    {
        if (DescribeComponent(control) is not { } snapshot)
        {
            return null;
        }
        var type = snapshot.Kind == "class" ? ComponentTypeNamed(snapshot.Name) : null;
        return (snapshot.Kind, type is null ? null : type.FullName ?? type.Name, snapshot.Name);
    }

    /// <summary>The <c>ReactorHostInfo</c> objects of <c>ReactorDiagnostics.GetHosts()</c>; empty when it fails.</summary>
    private List<object> HostRegistry()
    {
        var list = new List<object>();
        try
        {
            if (getHosts?.Invoke(null, null) is System.Collections.IEnumerable items)
            {
                list.AddRange(items.OfType<object>());
            }
        }
        catch (TargetInvocationException)
        {
            // A registry that fails to answer reports no hosts.
        }
        return list;
    }

    private IEnumerable<ReactorHostInfo> PublicHosts()
    {
        var list = new List<ReactorHostInfo>();
        foreach (var item in HostRegistry())
        {
            try
            {
                if (Member(item, "RootControl") is not UIElement control || control.XamlRoot is null)
                {
                    // Not rendered yet, or a removed island that stays registered until it is collected.
                    continue;
                }
                (string Kind, string? Type, string Name)? root = SnapshotRoot(control) ?? HostRoot(item);
                var reactorWindow = Member(item, "ReactorWindow");
                var kind = Member(item, "Kind")?.ToString() == "HostControl" ? "island" : reactorWindow is not null ? "window" : "host";
                var window = Member(item, "Window") ?? Member(reactorWindow, "NativeWindow");
                list.Add(new ReactorHostInfo(control, kind,
                    Member(reactorWindow, "Id")?.ToString(),
                    Member(reactorWindow, "Key")?.ToString(),
                    Member(window, "Title") as string,
                    root,
                    Location(Member(item, "MountSite")),
                    GetTag(control) is null ? ErrorScreenHeadline(control) : null));
            }
            catch (Exception ex) when (ex is TargetInvocationException or InvalidCastException or MemberAccessException)
            {
                // A host closing while it is described has no root to report.
            }
        }
        return list;
    }

    /// <summary>
    /// The root component a <c>ReactorHostInfo</c> names: <c>RootComponentName</c> or <c>RootRenderFunctionName</c>
    /// (already demangled, such as <c>Program.Main</c>).
    /// </summary>
    internal static (string Kind, string? Type, string Name)? HostRoot(object item)
    {
        if (Member(item, "RootComponentName") is string { Length: > 0 } component)
        {
            return ("class", null, component);
        }
        return Member(item, "RootRenderFunctionName") is string { Length: > 0 } function ? ("function", null, function) : null;
    }

    /// <summary>Each host's root control mapped to its host object (a <c>ReactorHost</c> or <c>ReactorHostControl</c>).</summary>
    private Dictionary<UIElement, object> PublicHostObjects()
    {
        var hosts = new Dictionary<UIElement, object>(ReferenceEqualityComparer.Instance);
        foreach (var item in HostRegistry())
        {
            try
            {
                if (Member(item, "RootControl") is UIElement root && (Member(item, "Host") ?? Member(item, "HostControl")) is { } host)
                {
                    hosts.TryAdd(root, host);
                }
            }
            catch (TargetInvocationException)
            {
                // Skip a host that closed.
            }
        }
        return hosts;
    }
    private static object? Read(object? target, PropertyInfo? property)
    {
        try { return property?.GetValue(target); }
        catch (TargetInvocationException) { return null; }
    }

    // The first property or field of those names the object's type has, public or not.
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
                    return property.GetValue(target);
                }
                if (type.GetField(name, AnyInstance | BindingFlags.DeclaredOnly) is { } field)
                {
                    return field.GetValue(target);
                }
            }
        }
        return null;
    }

    public object? GetTag(UIElement element)
    {
        try { return getElementTag!.Invoke(null, [element]); }
        catch (TargetInvocationException) { return null; }
    }

    /// <summary>The base <c>Element</c> record type, where an element's own properties stop.</summary>
    public Type? ElementBase => elementBase;

    /// <summary>
    /// The WinUI properties Reactor writes on <paramref name="control"/> from its element's modifiers, from Reactor's
    /// <c>ReactorDiagnostics.GetAppliedProperties</c>; null when this Reactor version does not have it.
    /// </summary>
    public IReadOnlyList<ReactorAppliedProperty>? AppliedProperties(UIElement control)
    {
        if (getAppliedProperties is null)
        {
            return null;
        }
        try
        {
            if (getAppliedProperties.Invoke(null, [control]) is not System.Collections.IEnumerable items)
            {
                return null;
            }
            var list = new List<ReactorAppliedProperty>();
            foreach (var item in items)
            {
                if (item is null || Member(item, "Modifier") is not string modifier || Member(item, "Property") is not string property)
                {
                    continue;
                }
                list.Add(new ReactorAppliedProperty(modifier, ReactorElementProps.PropertyName(property), Member(item, "Value")));
            }
            return list;
        }
        catch (Exception ex) when (ex is TargetInvocationException or InvalidCastException or MemberAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the control's accessible name is the default Reactor gave it (a copy of its caption or placeholder)
    /// rather than one the app set, per <c>GetAppliedProperties</c>. Only asked of controls that have a name.
    /// </summary>
    public bool HasDefaultAutomationName(UIElement control)
    {
        if (getAppliedProperties is null || string.IsNullOrEmpty(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(control)))
        {
            return false;
        }
        return IsDefaultAutomationName(AppliedProperties(control));
    }

    internal static bool IsDefaultAutomationName(IReadOnlyList<ReactorAppliedProperty>? applied) =>
        applied?.Any(p => p.Modifier == "DefaultAutomationName") == true;

    /// <summary>
    /// The references from <paramref name="control"/> whose target Reactor knows but hasn't created (on a tab that isn't
    /// open, say), so WinUI has nothing to show: the unresolved edges of Reactor's
    /// <c>ReactorDiagnostics.GetReferenceEdges</c>. Null when this Reactor version does not have it. UI thread only.
    /// </summary>
    public IReadOnlyList<ReactorPendingReference>? PendingReferences(UIElement control)
    {
        if (getReferenceEdges is null)
        {
            return null;
        }
        try
        {
            return getReferenceEdges.Invoke(null, [control]) is System.Collections.IEnumerable items ? PendingReferencesFrom(items) : null;
        }
        catch (Exception ex) when (ex is TargetInvocationException or InvalidCastException or MemberAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The unresolved edges among Reactor's reference edges (<c>ReferenceEdgeSnapshot</c>), in Reactor's order.
    /// </summary>
    internal static IReadOnlyList<ReactorPendingReference> PendingReferencesFrom(System.Collections.IEnumerable edges)
    {
        var list = new List<ReactorPendingReference>();
        foreach (var edge in edges)
        {
            if (edge is null || Member(edge, "Property") is not string property || Member(edge, "IsResolved") is not false)
            {
                continue;
            }
            var targetType = Member(edge, "ExpectedTargetTypeName") as string;
            list.Add(new ReactorPendingReference(property,
                Member(edge, "IsList") is true, Member(edge, "Index") is int index ? index : 0,
                string.IsNullOrEmpty(targetType) ? null : targetType, Member(edge, "TargetAutomationId") as string));
        }
        return list;
    }

    /// <summary>
    /// The props a component element was rendered with, for Reactor versions without <c>DescribeComponent</c>, which
    /// reports them itself.
    /// </summary>
    public object? ComponentProps(object? tag, out bool known)
    {
        known = tag is not null && componentElement?.IsInstanceOfType(tag) == true && componentProps is not null;
        return known ? componentProps!.GetValue(tag) : null;
    }

    /// <summary>The props record a component element was rendered with; null for any other element.</summary>
    public object? MountedProps(object? tag) =>
        tag is not null && componentElement?.IsInstanceOfType(tag) == true ? componentProps?.GetValue(tag) : null;

    public bool? SourceMapEnabled()
    {
        try { return sourceMapEnabled?.GetValue(null) as bool?; }
        catch (TargetInvocationException) { return null; }
    }

    /// <summary>
    /// The element's key. Flyout, MenuFlyout and CommandBarFlyout mount their <c>Target</c>'s control and tag it with
    /// themselves, so a decorator without a key of its own reports the key its target was given.
    /// </summary>
    public string? Key(object tag) => KeyThroughDecorators(tag, OwnKey, Unwrap);

    private string? OwnKey(object tag) => elementBase?.IsInstanceOfType(tag) == true ? key?.GetValue(tag) as string : null;

    // Reactor's own decorator walk when the version has it (it also knows decorators an app registers).
    private object? Unwrap(object tag)
    {
        if (unwrapDecorators is not null)
        {
            try { return unwrapDecorators.Invoke(null, [tag]); }
            catch (Exception ex) when (ex is TargetInvocationException or ArgumentException) { }
        }
        return DecoratorTarget(tag);
    }

    internal static string? KeyThroughDecorators(object tag, Func<object, string?> ownKey, Func<object, object?> unwrap)
    {
        if (ownKey(tag) is { Length: > 0 } own)
        {
            return own;
        }
        return unwrap(tag) is { } target && !ReferenceEquals(target, tag) && ownKey(target) is { Length: > 0 } wrapped ? wrapped : null;
    }

    /// <summary>The innermost target of Reactor's built-in flyout decorators; null when the element is not one.</summary>
    internal static object? DecoratorTarget(object tag)
    {
        var current = tag;
        for (var depth = 0; depth < 16; depth++)
        {
            if (current.GetType().Name is not ("FlyoutElement" or "MenuFlyoutElement" or "CommandBarFlyoutElement"))
            {
                return ReferenceEquals(current, tag) ? null : current;
            }
            if (Member(current, "Target") is not { } next || ReferenceEquals(next, current))
            {
                return null;
            }
            current = next;
        }
        return null;
    }

    /// <summary>
    /// Where the element was created. Prefers Reactor's <c>ReactorSourceMap.GetSource(UIElement)</c>, which resolves a
    /// flyout-like decorator to the element it wraps; the raw tag otherwise. The column is 0 when Reactor records none.
    /// </summary>
    public (string File, int Line, int Column)? CallSite(UIElement element, object tag)
    {
        if (getSource is not null)
        {
            try
            {
                if (Location(getSource.Invoke(null, [element])) is { } resolved)
                {
                    return resolved;
                }
            }
            catch (TargetInvocationException)
            {
                // Fall back to the tag's own call site.
            }
        }
        return CallSite(tag);
    }

    public (string File, int Line, int Column)? CallSite(object tag) => Location(callSite?.GetValue(tag));

    /// <summary>A Reactor <c>SourceLocation</c> as file, line and column (0 when the version records no column).</summary>
    internal static (string File, int Line, int Column)? Location(object? site)
    {
        if (site is null)
        {
            return null;
        }
        var type = site.GetType();
        var file = type.GetProperty("FilePath")?.GetValue(site) as string;
        var line = type.GetProperty("LineNumber")?.GetValue(site) is int number ? number : 0;
        var column = type.GetProperty("ColumnNumber")?.GetValue(site) is int col ? col : 0;
        return string.IsNullOrEmpty(file) ? null : (file, line, column);
    }

    /// <summary>Kind, CLR type and display name when the element mounts a component; null otherwise.</summary>
    public (string Kind, string? Type, string Name)? Component(object tag)
    {
        var type = tag.GetType();
        if (componentElement?.IsAssignableFrom(type) == true && componentType?.GetValue(tag) is Type component)
        {
            return ("class", component.FullName ?? component.Name, TypeName(component));
        }
        if (funcElement?.IsAssignableFrom(type) == true)
        {
            return ("function", null, DelegateName(type.GetProperty("RenderFunc")?.GetValue(tag) as Delegate, "RenderEachTime"));
        }
        if (memoElement?.IsAssignableFrom(type) == true)
        {
            return ("memo", null, DelegateName(type.GetProperty("RenderFunc")?.GetValue(tag) as Delegate, "Memo"));
        }
        return null;
    }

    internal static string ElementKind(Type type)
    {
        var name = type.Name;
        var tick = name.IndexOf('`');
        if (tick > 0)
        {
            name = name[..tick];
        }
        return name.Length > "Element".Length && name.EndsWith("Element", StringComparison.Ordinal)
            ? name[..^"Element".Length]
            : name;
    }

    // A lambda compiles to `<Render>b__0_0` on a closure nested in the component; name it after both.
    internal static string DelegateName(Delegate? render, string fallback)
    {
        var method = render?.Method;
        if (method is null)
        {
            return fallback;
        }
        var owner = method.DeclaringType;
        while (owner is not null && owner.Name.StartsWith('<'))
        {
            owner = owner.DeclaringType;
        }
        var name = method.Name;
        if (name.StartsWith('<') && name.IndexOf('>') is var close and > 1)
        {
            return $"{fallback} in {(owner is null ? "" : TypeName(owner) + ".")}{name[1..close]}";
        }
        return owner is null ? name : $"{TypeName(owner)}.{name}";
    }

    internal static string TypeName(Type type)
    {
        var name = type.Name;
        var tick = name.IndexOf('`');
        return tick > 0 ? name[..tick] : name;
    }
}
