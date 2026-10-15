// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

// Shared by the out-of-process hot reload helper and the in-app agent (linked into both projects).
// Frames are a 4-byte little-endian length followed by UTF-8 JSON.

using System.Text.Json;

namespace WinApp.HotReload.Protocol;

internal sealed class HotReloadRequest
{
    public string Op { get; set; } = "";
    public string? Token { get; set; }
    /// <summary>
    /// The compiler pipeline's first update: for every XAML class, "Event=Method" for the attributes on its root element as
    /// the app was built. Reloading in place connects the root again, so the agent first removes what the markup added.
    /// </summary>
    public Dictionary<string, List<string>> RootEvents { get; set; } = [];
    public List<DeltaPayload> Deltas { get; set; } = [];
    public List<MarkupPayload> Markup { get; set; } = [];
    /// <summary>XAML classes whose live instances are re-initialized in place (constructor body replayed).</summary>
    public List<string> RefreshTypes { get; set; } = [];
    /// <summary>XAML classes whose instances must be constructed again (their field initializers changed).</summary>
    public List<string> RecreateTypes { get; set; } = [];
    /// <summary>Re-create every hosted page: how some non-UI class is constructed changed.</summary>
    public bool RefreshAllPages { get; set; }
    /// <summary>Assemblies newly deployed next to the app (a package or project reference was added); their XAML types are served.</summary>
    public List<string> Assemblies { get; set; } = [];
    /// <summary>Content files (app-relative, "Assets/Logo.png") replaced next to the running app: images showing them reload.</summary>
    public List<string> Assets { get; set; } = [];
    /// <summary>"Type|Property" getters whose body changed: bindings to them are told to read them again.</summary>
    public List<string> ChangedProperties { get; set; } = [];
    /// <summary>
    /// Code bindings call changed (a getter, a converter, an x:Bind function): live bindings read their sources again,
    /// compiled x:Bind included, since bindings to objects without change notification never would.
    /// </summary>
    public bool RefreshBindings { get; set; }
    /// <summary>"Type|Method" static methods that set static fields added to already-initialized types.</summary>
    public List<string> Initializers { get; set; } = [];
    /// <summary>
    /// Per XAML class, "Element.Collection" (Nav.MenuItems) to how many items the markup the app launched with puts there:
    /// for instances loaded before the agent was there, the rest were added by code.
    /// </summary>
    public Dictionary<string, Dictionary<string, int>> LaunchItems { get; set; } = [];
    /// <summary>
    /// "rewrite" (winapp rewrites the XAML and wires it up) or "compiler" (the XAML compiler's own code and .xbf).
    /// Sent with every request, hello included; WINAPP_HOT_RELOAD_PIPELINE in the helper.
    /// </summary>
    public string Pipeline { get; set; } = "";
    /// <summary>How live pages show a save on the compiler pipeline (WINAPP_HOT_RELOAD_PAGE_UPDATE): inplace, recreate, shadow.</summary>
    public string PageUpdate { get; set; } = "";
    /// <summary>The files of this save, relative to the project (for the page update strategy).</summary>
    public List<string> SavedFiles { get; set; } = [];
}

internal sealed class DeltaPayload
{
    public Guid ModuleId { get; set; }
    public byte[] Metadata { get; set; } = [];
    public byte[] IL { get; set; } = [];
    public byte[] Pdb { get; set; } = [];
    public int[] UpdatedTypes { get; set; } = [];
}

internal enum MarkupKind
{
    /// <summary>A page, window or control with x:Class: its instances are re-created.</summary>
    Component,
    /// <summary>A standalone ResourceDictionary: dictionaries loaded from its URI are replaced.</summary>
    Dictionary,
    /// <summary>App.xaml's resources, as a ResourceDictionary: Application.Resources is replaced.</summary>
    Application,
    /// <summary>A Themes/Generic.xaml: its implicit styles are the default styles of its controls.</summary>
    Generic,
}

internal sealed class MarkupPayload
{
    public MarkupKind Kind { get; set; }
    public string Uri { get; set; } = "";
    public ComponentManifest Manifest { get; set; } = new();
    /// <summary>Positions in the runtime markup mapped to the user's file: [runtime line, column, source line, column], in order.</summary>
    public List<int[]> LineMap { get; set; } = [];
    /// <summary>The markup as the runtime parses it (for a component, already transformed).</summary>
    public string Xaml { get; set; } = "";
    /// <summary>The XAML compiler's .xbf (the compiler pipeline), sent to the runtime as binary markup instead of <see cref="Xaml"/>.</summary>
    public byte[]? Xbf { get; set; }
    /// <summary>
    /// App.xaml on the compiler pipeline: the x:Key values the saved markup no longer defines. Other keys the reloaded
    /// resources lack were added by the app's code, and are kept.
    /// </summary>
    public List<string> DroppedKeys { get; set; } = [];
    /// <summary>
    /// A component on the compiler pipeline: "Event=Method" for each attribute on its root element in the saved markup.
    /// Reloading in place connects the root again, so the agent first removes the handlers the previous markup added.
    /// </summary>
    public List<string> RootEvents { get; set; } = [];
}

/// <summary>What the runtime text parser cannot express, applied by the agent after every load of the URI.</summary>
internal sealed class ComponentManifest
{
    public string ClassName { get; set; } = "";
    public List<string> Names { get; set; } = [];
    public List<EventWiring> Events { get; set; } = [];
    public List<RootBinding> Bindings { get; set; } = [];
    public Dictionary<string, string> RootProperties { get; set; } = [];
    /// <summary>Wiring for elements created from templates, keyed by the Hook.Id the markup puts on them.</summary>
    public Dictionary<string, HookSpec> Hooks { get; set; } = [];
}

internal sealed class HookSpec
{
    /// <summary>Events of the hooked element, handled by the component that owns the template.</summary>
    public List<EventWiring> Events { get; set; } = [];
    /// <summary>x:Bind functions of the hooked element, evaluated against its DataContext.</summary>
    public List<RootBinding> Bindings { get; set; } = [];
}

internal sealed class EventWiring
{
    /// <summary>x:Name of the element, or null for the component root.</summary>
    public string? Element { get; set; }
    public string Event { get; set; } = "";
    public string Handler { get; set; } = "";
    /// <summary>For x:Bind event binding (<c>Click="{x:Bind ViewModel.Increment}"</c>): the path to the object owning the handler.</summary>
    public string? Path { get; set; }
    /// <summary>The element as messages name it (its x:Name or type).</summary>
    public string? Label { get; set; }

    /// <summary>The message while the handler doesn't exist yet.</summary>
    public string Waiting(string component) => $"{component}: waiting for handler {(Path is { Length: > 0 } p ? p + "." : "")}{Handler} ({Label ?? Element ?? "root"}.{Event})";
}

/// <summary>An x:Bind evaluated against the component root (outside any DataTemplate).</summary>
internal sealed class RootBinding
{
    public string? Element { get; set; }
    public string Property { get; set; } = "";
    /// <summary>Full type name declaring an attached property, e.g. Microsoft.UI.Xaml.Automation.AutomationProperties.</summary>
    public string? OwnerType { get; set; }
    public string Path { get; set; } = "";
    public string Mode { get; set; } = "OneTime";
    public string? Converter { get; set; }
    public string? ConverterParameter { get; set; }
    public string? FallbackValue { get; set; }
    public string? TargetNullValue { get; set; }
    public string? UpdateSourceTrigger { get; set; }
    /// <summary>For bindings inside templates whose path starts at a named element of the template.</summary>
    public string? ElementName { get; set; }
    /// <summary>For x:Bind functions and casts: the generated static method <c>object M(object source)</c> on the component.</summary>
    public string? Function { get; set; }
    /// <summary>Source-relative property paths whose changes re-evaluate <see cref="Function"/> (one-way bindings).</summary>
    public List<string> Watch { get; set; } = [];
    /// <summary>For two-way x:Bind with BindBack: the generated static method <c>void M(object source, object value)</c>.</summary>
    public string? BindBack { get; set; }
    /// <summary>"line,column" of the attribute in the user's file, for messages about this binding.</summary>
    public string? Location { get; set; }

    /// <summary>The BindBack path as written in markup; the helper turns it into <see cref="BindBack"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? BindBackPath { get; set; }
}

internal sealed class HotReloadResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Capabilities { get; set; }
    public string? BaseDirectory { get; set; }
    /// <summary>False when the app's WinUI offers no hot reload service, or the app isn't enabled for it: XAML edits then need a restart.</summary>
    public bool XamlReload { get; set; }
    /// <summary>Why XAML can't be reloaded although this WinUI supports it (the app isn't a Debug build); null otherwise.</summary>
    public string? XamlReloadUnavailable { get; set; }
    /// <summary>
    /// The app's WinUI can forget what it read about types an update changed (XamlHotReload_InvalidateTypes), so markup can
    /// set a property added to a type it already used; without it, that takes a restart.
    /// </summary>
    public bool InvalidatesTypes { get; set; }
    public List<string> Messages { get; set; } = [];
    /// <summary>Reloads that failed in the app (for example markup the runtime couldn't parse).</summary>
    public List<string> Errors { get; set; } = [];
    /// <summary>What an applied update didn't keep (items code added to a collection a replayed constructor fills).</summary>
    public List<string> Notes { get; set; } = [];
    /// <summary>Why the update was applied but the app must restart to show it correctly (a page update strategy said so).</summary>
    public List<string> Restarts { get; set; } = [];
}

internal static class HotReloadFrames
{
    public const string PipeEnvironmentVariable = "WINAPP_HOT_RELOAD_PIPE";
    public const string TokenEnvironmentVariable = "WINAPP_HOT_RELOAD_TOKEN";
    private const int MaxFrame = 64 * 1024 * 1024;

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        if (!await FillAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return default;
        }
        var length = BitConverter.ToInt32(header, 0);
        if (length is <= 0 or > MaxFrame)
        {
            throw new InvalidDataException("Invalid hot reload frame length.");
        }
        var body = new byte[length];
        if (!await FillAsync(stream, body, cancellationToken).ConfigureAwait(false))
        {
            throw new EndOfStreamException("Truncated hot reload frame.");
        }
        return JsonSerializer.Deserialize<T>(body);
    }

    private static async Task<bool> FillAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return read == 0 ? false : throw new EndOfStreamException("Truncated hot reload frame.");
            }
            read += count;
        }
        return true;
    }
}
