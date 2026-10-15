// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using WinApp.HotReload.Protocol;

namespace WinApp.DevTools.Managed.HotReload;

/// <summary>
/// Applies markup through WinUI's hot reload service (text XAML replaces compiled XBF per URI) and wires what the
/// runtime parser can't: x:Name fields, event handlers, root-scope x:Bind and Window properties. Then rebuilds the
/// live instances whose markup or constructor changed.
/// </summary>
internal static class XamlReload
{
    private const string ReplayMethod = "__WinAppHotReload_Construct";
    private const string ResetMethod = "__WinAppHotReload_Reset";
    private const string GeneratedNamePrefix = "__hr_";
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly Dictionary<string, ComponentManifest> Manifests = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>SPIKE (S1/S2): markup comes from the real XAML compiler's .xbf, wired by the compiled code alone.</summary>
    private static readonly bool RealCompilerSpike = Environment.GetEnvironmentVariable("WINAPP_HOT_RELOAD_REAL_COMPILER") == "1";
    private static readonly List<WeakReference<Window>> KnownWindows = [];
    private static readonly ConditionalWeakTable<object, Dictionary<string, Delegate>> RootHandlers = new();
    private static XamlHotReloadService? s_service;
    private static DispatcherQueue? s_dispatcher;
    private static HashSet<object>? s_loadedDuringRefresh;
    private static List<string>? s_messages;
    private static List<string>? s_notes;
    private static List<string>? s_errors;
    private static ReflectionMetadataProvider? s_provider;
    private static bool s_initialized;

    public static bool Supported => s_service is not null;

    /// <summary>Why XAML can't be reloaded in this app although its WinUI could (it isn't enabled for hot reload).</summary>
    public static string? Unavailable { get; private set; }

    public static async Task<string?> InitializeAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (FindDispatcher() is null)
        {
            if (DateTime.UtcNow > deadline)
            {
                return "no WinUI window was found in the app";
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
        if (s_initialized)
        {
            return null;
        }
        var (service, unavailable) = await XamlHotReloadService.AcquireAsync().ConfigureAwait(false);
        return await OnUiThreadAsync(() => Initialize(service, unavailable)).ConfigureAwait(false);
    }

    private static string? Initialize(XamlHotReloadService? service, string? unavailable)
    {
        if (s_initialized)
        {
            return null;
        }
        s_initialized = true;
        HotReloadAgent.UiThreadId = Environment.CurrentManagedThreadId;
        if (Application.Current is { } application)
        {
            // Recorded without handling it: the app goes down as it would without winapp.
            application.UnhandledException += (_, e) => HotReloadAgent.RecordCrash(e.Exception);
        }
        // Components that loaded before the agent connected: their current values are where they started.
        foreach (var window in LiveWindows())
        {
            UiState.CaptureBaseline(window.Content);
        }
        Unavailable = unavailable;
        if (service is null)
        {
            // C# edits still apply; XAML edits need a restart (a WinUI without hot reload, or an app not enabled for it).
            return null;
        }
        s_service = service;
        Hook.Attached = OnHook;
        service.AdviseComponentLoaded(OnLoaded);
        if (RealCompilerSpike)
        {
            // SPIKE: types come only from the app's own compiled metadata provider (updated by edit and continue).
            return null;
        }
        s_provider = new ReflectionMetadataProvider();
        var provider = WinRT.MarshalInterface<IXamlMetadataProvider>.FromManaged(s_provider);
        try
        {
            Marshal.ThrowExceptionForHR(service.RegisterMetadataProvider(provider));
        }
        finally
        {
            Marshal.Release(provider);
        }
        return null;
    }

    public static Task<T> OnUiThreadAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = FindDispatcher() ?? throw new InvalidOperationException("The app's UI thread is not available.");
        if (!dispatcher.TryEnqueue(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception ex) { completion.SetException(ex); }
        }))
        {
            completion.SetException(new InvalidOperationException("The app's UI thread is shutting down."));
        }
        return completion.Task;
    }

    private static DispatcherQueue? FindDispatcher()
    {
        if (s_dispatcher is null && LiveWindows().FirstOrDefault() is { } window)
        {
            s_dispatcher = window.DispatcherQueue;
        }
        return s_dispatcher;
    }

    /// <summary>Windows held by the app (fields of the Application or statics of the app's types) plus those seen loading.</summary>
    private static List<Window> LiveWindows()
    {
        var windows = new List<Window>();
        void Add(object? value)
        {
            if (value is Window window && !windows.Contains(window))
            {
                windows.Add(window);
            }
        }
        lock (KnownWindows)
        {
            foreach (var weak in KnownWindows)
            {
                Add(weak.TryGetTarget(out var w) ? w : null);
            }
        }
        try
        {
            if (Application.Current is { } app)
            {
                for (var type = app.GetType(); type is not null && type != typeof(Application); type = type.BaseType)
                {
                    foreach (var field in type.GetFields(InstanceMembers | BindingFlags.DeclaredOnly))
                    {
                        Add(field.GetValue(app));
                    }
                }
            }
        }
        catch (COMException)
        {
            // The application object isn't created yet.
        }
        if (Assembly.GetEntryAssembly() is { } entry)
        {
            Type[] types;
            try
            {
                types = entry.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.OfType<Type>().ToArray();
            }
            foreach (var field in types.SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                .Where(f => typeof(Window).IsAssignableFrom(f.FieldType) && !f.FieldType.ContainsGenericParameters && !f.DeclaringType!.ContainsGenericParameters))
            {
                Add(field.GetValue(null));
            }
        }
        return windows;
    }

    /// <summary>Runs on the UI thread after an edit-and-continue delta (if any) was applied.</summary>
    public static void Apply(HotReloadRequest request, List<string> messages, List<string> errors, List<string> notes)
    {
        s_messages = messages;
        s_errors = errors;
        s_notes = notes;
        s_deferredBinds = [];
        CodeAddedItems.LaunchItems(request.LaunchItems);
        try
        {
            var everything = false;
            if (request.Assemblies.Count > 0)
            {
                ReflectionMetadataProvider.AddAssemblies(request.Assemblies);
                InvalidateTypeCaches();
                // A library with default styles: controls pick them up as they are re-created.
                everything |= request.Assemblies.Aggregate(false, (any, name) => AddLibraryMarkup(name) | any);
            }
            foreach (var markup in request.Markup)
            {
                ClearErrorInfo();
                LineMaps[markup.Uri] = markup.LineMap;
                if (markup.Kind == MarkupKind.Component && markup.Manifest.ClassName.Length > 0)
                {
                    ClassUris[markup.Manifest.ClassName] = markup.Uri;
                }
                if (markup.Kind is MarkupKind.Application or MarkupKind.Generic && markup.Xbf is null)
                {
                    try
                    {
                        // App.xaml's resources are swapped in whole. A Generic.xaml is only checked here: the runtime
                        // reads it through the override when it looks up a default style.
                        var resources = (ResourceDictionary)XamlReader.Load(markup.Xaml);
                        if (markup.Kind == MarkupKind.Application)
                        {
                            // Swapping them re-templates controls (a TextBox inside one is briefly gone): what the user typed is read first.
                        TakeStatesBeforeResourceChange();
                        KeepMissing(Application.Current.Resources, resources);
                            Application.Current.Resources = resources;
                            everything = true;
                            continue;
                        }
                    }
                    catch (Exception ex) when (ex is COMException or XamlParseException or InvalidCastException)
                    {
                        ReportError(Describe(ex, markup.Uri));
                        continue;
                    }
                }
                if (markup.Xbf is { } xbf)
                {
                    // SPIKE: the XAML compiler's own .xbf; the page's compiled InitializeComponent and Connect do the rest.
                    Manifests.Remove(markup.Uri);
                    if (markup.Manifest.ClassName.Length > 0)
                    {
                        ClassUris[markup.Manifest.ClassName] = markup.Uri;
                    }
                    s_service!.RemoveComponentMarkup(markup.Uri);
                    var binaryResult = s_service!.SetComponentMarkup(markup.Uri, binary: true, xbf);
                    if (binaryResult < 0)
                    {
                        ReportError($"{markup.Uri}: {Marshal.GetExceptionForHR(binaryResult)?.Message}");
                    }
                    // NEW TYPES: default styles changed; controls pick them up as they are re-created.
                    everything |= binaryResult >= 0 && markup.Kind == MarkupKind.Generic;
                    continue;
                }
                if (markup.Kind == MarkupKind.Component)
                {
                    Manifests[markup.Uri] = markup.Manifest;
                    foreach (var stale in Hooks.Keys.Where(k => k.StartsWith(markup.Manifest.ClassName + "#", StringComparison.Ordinal)).ToList())
                    {
                        Hooks.Remove(stale);
                    }
                    foreach (var (id, hook) in markup.Manifest.Hooks)
                    {
                        Hooks[id] = hook;
                    }
                }
                var hr = s_service!.SetComponentMarkup(markup.Uri, binary: false, Encoding.UTF8.GetBytes(markup.Xaml));
                if (hr >= 0 && markup.Uri.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                {
                    // The text wins over a compiled XBF served for the same file (a library's, from its .pri).
                    _ = s_service.RemoveComponentMarkup(markup.Uri[..^".xaml".Length] + ".xbf");
                }
                if (hr < 0)
                {
                    ReportError($"{markup.Uri}: {Marshal.GetExceptionForHR(hr)?.Message}");
                    continue;
                }
                everything |= markup.Kind == MarkupKind.Generic;
                if (markup.Kind == MarkupKind.Dictionary)
                {
                    try
                    {
                        ReplaceDictionaries(Application.Current.Resources, new Uri(markup.Uri));
                    }
                    catch (Exception ex) when (ex is COMException or XamlParseException)
                    {
                        ReportError(Describe(ex, markup.Uri));
                    }
                    everything = true;
                }
            }
            if (request.Deltas.Count > 0)
            {
                InvalidateTypeCaches();
            }
            try
            {
                Refresh(request.RefreshTypes.ToHashSet(StringComparer.Ordinal), request.RecreateTypes.ToHashSet(StringComparer.Ordinal), request.RefreshAllPages, everything);
            }
            catch (Exception ex) when (ex is COMException or XamlParseException or InvalidOperationException or ArgumentException)
            {
                // The code is in; the UI didn't rebuild. The markup is sent again with the next save.
                ReportError("the UI could not be rebuilt: " + ex.Message);
            }
            NotifyChangedProperties(request.ChangedProperties, request.RefreshBindings);
            if (request.Deltas.Count > 0)
            {
                ReevaluateFunctions();
            }
            if (request.Assets.Count > 0)
            {
                ChangedAssets.UnionWith(request.Assets);
                foreach (var window in LiveWindows())
                {
                    ReloadImages(window.Content, replaced: true);
                }
            }
        }
        finally
        {
            s_messages = null;
            s_errors = null;
            s_notes = null;
        }
    }

    /// <summary>
    /// During an update: x:Bind evaluations of components reloaded in place. They run in a dispatcher callback of their own
    /// after the update (once every constructor replay has finished and the new trees have been queued for layout), as
    /// they would on their own; the update reports back after them, so what they report belongs to this save.
    /// </summary>
    private static List<Action>? s_deferredBinds;

    /// <summary>Runs the evaluations <see cref="Apply"/> deferred, reporting into the same update's lists.</summary>
    public static void ApplyDeferredBindings(List<string> messages, List<string> errors, List<string> notes)
    {
        s_messages = messages;
        s_errors = errors;
        s_notes = notes;
        try
        {
            while (s_deferredBinds is { Count: > 0 } deferred)
            {
                var next = deferred[0];
                deferred.RemoveAt(0);
                next();
            }
        }
        finally
        {
            s_deferredBinds = null;
            s_messages = null;
            s_errors = null;
            s_notes = null;
        }
    }

    /// <summary>
    /// A library deployed while the app runs has its compiled markup embedded in its .pri, which only a build merges
    /// into the app's resources. Its XBFs are served from there instead. Returns whether it has a Generic.xaml.
    /// </summary>
    private static bool AddLibraryMarkup(string assemblyName)
    {
        var pri = Path.Combine(AppContext.BaseDirectory, assemblyName + ".pri");
        if (s_service is null || !File.Exists(pri))
        {
            return false;
        }
        var generic = false;
        var map = new Microsoft.Windows.ApplicationModel.Resources.ResourceManager(pri).MainResourceMap;
        for (uint i = 0; i < map.ResourceCount; i++)
        {
            var (key, candidate) = map.GetValueByIndex(i);
            if (!key.EndsWith(".xbf", StringComparison.OrdinalIgnoreCase) || candidate.Kind != Microsoft.Windows.ApplicationModel.Resources.ResourceCandidateKind.EmbeddedData)
            {
                continue;
            }
            // "Files/CommunityToolkit.WinUI.Controls.SettingsControls/Themes/Generic.xbf"
            var relative = key.StartsWith("Files/", StringComparison.OrdinalIgnoreCase) ? key["Files/".Length..] : key;
            if (!relative.StartsWith(assemblyName + "/", StringComparison.OrdinalIgnoreCase))
            {
                relative = assemblyName + "/" + relative;
            }
            var xbf = candidate.ValueAsBytes;
            var xbfUri = "ms-appx:///" + relative;
            var xamlUri = xbfUri[..^".xbf".Length] + ".xaml";
            foreach (var uri in new[] { xbfUri, xamlUri })
            {
                _ = s_service.SetComponentMarkup(uri, binary: true, xbf);
            }
            HotReloadAgent.Log($"library markup {xamlUri}");
            generic |= relative.EndsWith("/Themes/Generic.xbf", StringComparison.OrdinalIgnoreCase);
        }
        return generic;
    }

    /// <summary>Runtime markup positions to the user's file, by markup URI (see MarkupPayload.LineMap).</summary>
    private static readonly Dictionary<string, List<int[]>> LineMaps = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> ClassUris = new(StringComparer.Ordinal);

    /// <summary>What .NET puts first when the system has no text for the parser's HRESULT; the parser's own text follows.</summary>
    private const string NoText = "The text associated with this error code could not be found.";

    [DllImport("oleaut32.dll")]
    private static extern int SetErrorInfo(uint reserved, IntPtr errorInfo);

    [DllImport("api-ms-win-core-winrt-error-l1-1-1.dll")]
    private static extern int SetRestrictedErrorInfo(IntPtr errorInfo);

    /// <summary>
    /// Forgets error details a previous failure left on the thread, so a failure that comes without its own isn't
    /// reported with an earlier one's message.
    /// </summary>
    private static void ClearErrorInfo()
    {
        try
        {
            _ = SetErrorInfo(0, IntPtr.Zero);
            _ = SetRestrictedErrorInfo(IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    /// <summary>
    /// "Pages/Home.xaml(13,40): Failed to create a 'Brush' from the text 'Oranj'." for a parse error in the markup
    /// at <paramref name="uri"/>, with the runtime's position mapped back to the user's file.
    /// </summary>
    internal static string Describe(Exception ex, string? uri, string? component = null)
    {
        // The runtime's restricted error text often repeats the system message: each line once.
        var message = string.Join(" ", ex.GetBaseException().Message.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && l != NoText).Distinct());
        // A dictionary's items split by a property element (<ResourceDictionary.ThemeDictionaries>) in between.
        message = message.Replace("The property '__implicit_items' is set more than once.",
            "This dictionary's resources are split by another element (such as <ResourceDictionary.ThemeDictionaries>): keep them together in one block (the XAML compiler reports this as WMC0035).", StringComparison.Ordinal)
            .Replace("'__implicit_items'", "the dictionary's resources", StringComparison.Ordinal);
        var position = System.Text.RegularExpressions.Regex.Match(message, @"\s*\[Line: (\d+) Position: (\d+)\]");
        if (uri is null)
        {
            return component is null ? message : $"{component}: {message}";
        }
        if (!position.Success)
        {
            return $"{uri}: {message}";
        }
        var (line, column) = (int.Parse(position.Groups[1].Value), int.Parse(position.Groups[2].Value));
        if (LineMaps.TryGetValue(uri, out var map) && map.LastOrDefault(m => m[0] < line || (m[0] == line && m[1] <= column)) is { } at)
        {
            // Same line as the element or attribute it falls in: keep the offset; otherwise its start.
            (line, column) = at[0] == line ? (at[2], at[3] + column - at[1]) : (at[2], at[3]);
        }
        return $"{uri}({line},{column}): {message.Remove(position.Index, position.Length)}";
    }

    /// <summary>A reload that didn't take effect: the save is reported as an error and retried with the next one.</summary>
    private static void ReportError(string message)
    {
        if (s_errors is { } errors && !errors.Contains(message))
        {
            errors.Add(message);
        }
        else
        {
            System.Diagnostics.Trace.TraceWarning("Hot reload: {0}", message);
        }
    }

    private static void Report(string message)
    {
        if (s_messages is { } messages)
        {
            if (!messages.Contains(message))
            {
                messages.Add(message);
            }
        }
        else
        {
            System.Diagnostics.Trace.TraceWarning("Hot reload: {0}", message);
        }
    }

    /// <summary>"Styles/Colors.xaml" for ms-appx:///Styles/Colors.xaml and for the compiled ms-resource:///Files/Styles/Colors.xaml.</summary>
    private static string AppRelativePath(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
        {
            return uri.OriginalString.TrimStart('/');
        }
        var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        return uri.Scheme == "ms-resource" && path.StartsWith("Files/", StringComparison.OrdinalIgnoreCase) ? path["Files/".Length..] : path;
    }

    /// <summary>Re-loads app-level dictionaries merged from <paramref name="uri"/>; window and page dictionaries reload with them.</summary>
    private static void ReplaceDictionaries(ResourceDictionary dictionary, Uri uri)
    {
        var merged = dictionary.MergedDictionaries;
        for (var i = 0; i < merged.Count; i++)
        {
            if (merged[i].Source is { } source && string.Equals(AppRelativePath(source), AppRelativePath(uri), StringComparison.OrdinalIgnoreCase))
            {
                var updated = new ResourceDictionary { Source = uri };
                KeepMissing(merged[i], updated);
                merged[i] = updated;
            }
            else
            {
                ReplaceDictionaries(merged[i], uri);
            }
        }
    }

    /// <summary>
    /// Keys a dictionary no longer defines stay resolvable with their old values until the app restarts, as removed
    /// fields do: a resource renamed where it's defined first doesn't break the markup that still uses the old key.
    /// </summary>
    private static void KeepMissing(ResourceDictionary old, ResourceDictionary updated)
    {
        foreach (var (key, value) in old.ToList())
        {
            if (!updated.ContainsKey(key))
            {
                try
                {
                    updated[key] = value;
                }
                catch (Exception ex) when (ex is COMException or ArgumentException)
                {
                    // A value that can't be shared between dictionaries (an element): the key goes away.
                }
            }
        }
        foreach (var (theme, oldTheme) in old.ThemeDictionaries.ToList())
        {
            if (oldTheme is ResourceDictionary oldDictionary && updated.ThemeDictionaries.TryGetValue(theme, out var newTheme) && newTheme is ResourceDictionary newDictionary)
            {
                KeepMissing(oldDictionary, newDictionary);
            }
        }
    }

    /// <summary>
    /// Rebuilds the live UI after an update. Instances are kept and re-initialized in place (constructor body replayed),
    /// so pages, their view models and fields survive. Pages are re-created through their Frame only when how they are
    /// constructed changed (<paramref name="recreate"/>, <paramref name="allPages"/>). <paramref name="everything"/> is
    /// for resource changes: every window is rebuilt and every page reloads its markup.
    /// </summary>
    private static void Refresh(HashSet<string> replay, HashSet<string> recreate, bool allPages, bool everything)
    {
        if (replay.Count == 0 && recreate.Count == 0 && !allPages && !everything)
        {
            return;
        }
        bool Affected(object component) => replay.Contains(component.GetType().FullName!) || recreate.Contains(component.GetType().FullName!);
        MarkChanged(replay.Concat(recreate), allPages || everything);
        s_loadedDuringRefresh = new HashSet<object>(ReferenceEqualityComparer.Instance);
        try
        {
            // Pages moved into a re-initialized window: its new tree isn't laid out yet, so a walk can't reach them.
            var kept = new List<(Frame Frame, Page Page)>();
            foreach (var window in LiveWindows())
            {
                // NEW TYPES (real compiler): default styles or library markup changed. Pages reload their markup; a window
                // is re-initialized only when its own markup or code changed. Re-running a window's InitializeComponent
                // moves its frames into a new tree, and a moved Frame then fails GoBack (E_FAIL).
                if ((everything && !RealCompilerSpike) || Affected(window))
                {
                    var state = TakeState(window, window.Content);
                    var frames = NamedFrames(window.Content);
                    var selections = NavigationSelections(window.Content);
                    var oldRoot = window.Content;
                    Reinitialize(window);
                    var keptHere = KeepPages(window, oldRoot, window.Content as FrameworkElement, frames);
                    kept.AddRange(keptHere);
                    KeepLocation(window.Content as FrameworkElement, keptHere.Select(k => k.Frame).ToList(), selections);
                    UiState.RestoreWhenLoaded(window.Content as FrameworkElement, state, s_dispatcher);
                }
            }
            foreach (var window in LiveWindows())
            {
                var frames = new List<(Frame Frame, Page Page)>();
                var others = new List<FrameworkElement>();
                Collect(window.Content, page => allPages || everything || Affected(page), Affected, frames, others);
                frames.AddRange(kept.Where(k => (allPages || everything || Affected(k.Page)) && !s_loadedDuringRefresh.Contains(k.Page) && !frames.Any(f => ReferenceEquals(f.Page, k.Page))));
                kept.Clear();
                var pending = new Queue<(Frame Frame, Page Page)>(frames);
                while (pending.TryDequeue(out var entry))
                {
                    var (frame, page) = entry;
                    var state = TakeState(page, page.Content);
                    var type = page.GetType().FullName!;
                    if (allPages || recreate.Contains(type))
                    {
                        Renavigate(frame, page);
                    }
                    else
                    {
                        // A page's own frames (a gallery page showing its sample in one) keep their pages, which the
                        // page navigated them to in OnNavigatedTo, not in its markup.
                        var nested = NamedFrames(page.Content);
                        var oldRoot = page.Content;
                        if (replay.Contains(type))
                        {
                            Reinitialize(page);
                        }
                        else
                        {
                            // Only resources changed: reload the markup, without replaying the constructor.
                            ReloadMarkup(page);
                        }
                        if (nested.Count > 0 && !ReferenceEquals(oldRoot, page.Content))
                        {
                            foreach (var inner in KeepPages(page, oldRoot, page.Content as FrameworkElement, nested))
                            {
                                if ((allPages || everything || Affected(inner.Page)) && !s_loadedDuringRefresh.Contains(inner.Page))
                                {
                                    pending.Enqueue(inner);
                                }
                            }
                        }
                    }
                    UiState.RestoreWhenLoaded((frame.Content as Page)?.Content as FrameworkElement, state, s_dispatcher);
                }
                foreach (var element in others)
                {
                    var state = TakeState(element, ContentRoot(element));
                    Reinitialize(element);
                    UiState.RestoreWhenLoaded(ContentRoot(element) as FrameworkElement, state, s_dispatcher);
                }
            }
        }
        finally
        {
            s_loadedDuringRefresh = null;
            s_statesBefore = null;
        }
    }
    /// <summary>UI state taken before App resources changed, by window or page (see TakeStatesBeforeResourceChange).</summary>
    private static Dictionary<object, UiState.Snapshot?>? s_statesBefore;

    private static void TakeStatesBeforeResourceChange()
    {
        s_statesBefore ??= new Dictionary<object, UiState.Snapshot?>(ReferenceEqualityComparer.Instance);
        foreach (var window in LiveWindows())
        {
            s_statesBefore.TryAdd(window, UiState.Take(window.Content));
            foreach (var page in Descendants(window.Content).OfType<Frame>().Select(f => f.Content).OfType<Page>())
            {
                s_statesBefore.TryAdd(page, UiState.Take(page.Content));
            }
        }
    }

    private static UiState.Snapshot? TakeState(object component, DependencyObject? root) =>
        s_statesBefore is not null && s_statesBefore.TryGetValue(component, out var before) ? before : UiState.Take(root);

    /// <summary>
    /// A property getter, converter or x:Bind function whose body changed computes a new value, but nothing tells
    /// bindings to read it again. Raise PropertyChanged for changed getters on live instances, then have every live
    /// binding read its source again: compiled x:Bind (page scope and template items) and markup bindings, which
    /// covers converters and objects that never raise PropertyChanged.
    /// </summary>
    private static void NotifyChangedProperties(List<string> properties, bool refresh)
    {
        if (properties.Count == 0 && !refresh)
        {
            return;
        }
        var byType = properties.Select(p => p.Split('|')).GroupBy(p => p[0], p => p[1], StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var components = new List<object>();
        var roots = new List<object>();
        var elements = new List<FrameworkElement>();
        foreach (var window in LiveWindows())
        {
            components.Add(window);
            void Walk(DependencyObject node)
            {
                if (node is Page or UserControl)
                {
                    components.Add(node);
                }
                if (node is FrameworkElement { DataContext: { } context })
                {
                    roots.Add(context);
                }
                if (refresh && node is FrameworkElement element)
                {
                    elements.Add(element);
                }
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                {
                    Walk(VisualTreeHelper.GetChild(node, i));
                }
            }
            if (window.Content is { } content)
            {
                Walk(content);
            }
        }
        foreach (var instance in LiveObjects.Reachable(components.Concat(roots)))
        {
            for (var type = instance.GetType(); type is not null; type = type.BaseType)
            {
                if (type.FullName is { } name && byType.TryGetValue(name, out var names))
                {
                    foreach (var property in names)
                    {
                        RaisePropertyChanged(instance, property);
                    }
                }
            }
        }
        foreach (var component in components)
        {
            UpdateCompiledBindings(component);
        }
        foreach (var element in elements)
        {
            try
            {
                // An item of a compiled DataTemplate: its x:Bind runs again against the item, phase by phase (x:Phase).
                if (Microsoft.UI.Xaml.Markup.XamlBindingHelper.GetDataTemplateComponent(element) is { } template)
                {
                    // ItemsRepeater hands the item to the template without setting DataContext; the compiled
                    // template keeps it as its data root.
                    var item = element.DataContext ?? template.GetType().GetField("dataRoot", InstanceMembers)?.GetValue(template);
                    for (int phase = 0, guard = 0; item is not null && phase >= 0 && guard < 16; guard++)
                    {
                        template.ProcessBindings(item, 0, phase, out var next);
                        phase = next;
                    }
                }
                foreach (var property in DependencyProperties(element.GetType()))
                {
                    if (element.GetBindingExpression(property) is { ParentBinding: { } binding })
                    {
                        element.SetBinding(property, binding);
                    }
                }
            }
            catch (Exception ex)
            {
                HotReloadAgent.Log($"refreshing bindings on {element.GetType().Name} failed: {ex.Message}");
            }
        }
    }

    private static readonly Dictionary<Type, DependencyProperty[]> s_dependencyProperties = [];

    /// <summary>The dependency properties a type and its bases declare (TextBlock.TextProperty, ...).</summary>
    private static DependencyProperty[] DependencyProperties(Type type)
    {
        if (!s_dependencyProperties.TryGetValue(type, out var properties))
        {
            var found = new List<DependencyProperty>();
            for (var t = type; t is not null && t != typeof(DependencyObject); t = t.BaseType)
            {
                foreach (var property in t.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(p => p.PropertyType == typeof(DependencyProperty)))
                {
                    try
                    {
                        if (property.GetValue(null) is DependencyProperty value)
                        {
                            found.Add(value);
                        }
                    }
                    catch (Exception ex) when (ex is TargetInvocationException or COMException)
                    {
                        // Not available on this Windows version.
                    }
                }
            }
            s_dependencyProperties[type] = properties = [.. found];
        }
        return properties;
    }

    private static void RaisePropertyChanged(object instance, string property)
    {
        if (instance is not System.ComponentModel.INotifyPropertyChanged)
        {
            return;
        }
        var handler = LiveObjects.Fields(instance.GetType()).FirstOrDefault(f => f.Name == "PropertyChanged" && typeof(System.ComponentModel.PropertyChangedEventHandler).IsAssignableFrom(f.FieldType))
            ?.GetValue(instance) as System.ComponentModel.PropertyChangedEventHandler;
        try
        {
            handler?.Invoke(instance, new System.ComponentModel.PropertyChangedEventArgs(property));
        }
        catch (Exception ex)
        {
            Report($"{instance.GetType().Name}.{property}: a PropertyChanged handler failed: {ex.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// SPIKE: simulates a reset hook the XAML compiler could generate (a MetadataUpdateHandler): the app's XamlTypeInfo
    /// provider builds its type tables once, so a type added by an update isn't found, and an inserted type shifts the
    /// indexes the updated CreateXamlType uses. A new provider builds them again with the updated code.
    /// </summary>
    /// <summary>
    /// NEW TYPES experiment: WINAPP_HR_NO_INVALIDATE=1 skips WinUI's type cache invalidation (cached misses), to measure
    /// what new types need from WinUI once the app's own XamlTypeInfo is updated by .NET hot reload.
    /// </summary>
    private static void InvalidateTypeCaches()
    {
        if (Environment.GetEnvironmentVariable("WINAPP_HR_NO_INVALIDATE") == "1")
        {
            HotReloadAgent.Log("experiment: WinUI type caches NOT invalidated");
            return;
        }
        s_service?.InvalidateTypeCaches();
    }

    public static void SpikeResetTypeInfo()
    {
        if (Environment.GetEnvironmentVariable("WINAPP_HR_NO_RESET") == "1")
        {
            HotReloadAgent.Log("experiment: the app's XamlTypeInfo provider NOT reset");
            return;
        }
        if (RealCompilerSpike && Application.Current is { } app && app.GetType().GetField("__appProvider", InstanceMembers) is { } field)
        {
            field.SetValue(app, null);
            HotReloadAgent.Log("spike: reset the app's XamlTypeInfo provider");
        }
    }

    /// <summary>
    /// SPIKE: before a delta, stops the compiled x:Bind of the components about to be re-initialized, while their code still
    /// matches their fields (after the delta, an old Bindings object runs the new code against fields it never set).
    /// </summary>
    public static void SpikeStopTracking(IReadOnlyCollection<string> types)
    {
        if (!RealCompilerSpike || types.Count == 0)
        {
            return;
        }
        void Stop(object component)
        {
            if (types.Contains(component.GetType().FullName!) && component.GetType().GetField("Bindings", InstanceMembers)?.GetValue(component) is { } bindings)
            {
                try
                {
                    bindings.GetType().GetMethod("StopTracking")?.Invoke(bindings, null);
                    HotReloadAgent.Log($"spike: stopped tracking {component.GetType().Name} {bindings.GetType().Name}#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(bindings)}");
                }
                catch (Exception ex)
                {
                    HotReloadAgent.Log($"spike: StopTracking failed: {ex.GetBaseException().Message}");
                }
            }
        }
        void Walk(DependencyObject? element)
        {
            if (element is null)
            {
                return;
            }
            Stop(element);
            for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element); i++)
            {
                Walk(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(element, i));
            }
        }
        foreach (var window in LiveWindows())
        {
            Stop(window);
            Walk(window.Content);
        }
    }

    /// <summary>Runs the component's x:Bind again: the compiled Bindings object, or the stand-in live reload installed.</summary>
    private static void UpdateCompiledBindings(object component)
    {
        try
        {
            var bindings = component.GetType().GetField("Bindings", InstanceMembers)?.GetValue(component);
            bindings?.GetType().GetMethod("Update")?.Invoke(bindings, null);
            if (RealCompilerSpike)
            {
                HotReloadAgent.Log($"spike: {component.GetType().Name}.Bindings field={(component.GetType().GetField("Bindings", InstanceMembers) is not null)} value={bindings?.GetType().FullName ?? "null"}");
                foreach (var property in component.GetType().GetProperties(InstanceMembers).Where(p => typeof(System.ComponentModel.INotifyPropertyChanged).IsAssignableFrom(p.PropertyType) && p.GetIndexParameters().Length == 0))
                {
                    var source = property.GetValue(component);
                    var handlers = source is null ? null : LiveObjects.Fields(source.GetType()).FirstOrDefault(f => f.Name == "PropertyChanged")?.GetValue(source) as Delegate;
                    HotReloadAgent.Log($"spike: {component.GetType().Name}.{property.Name}.PropertyChanged has {handlers?.GetInvocationList().Length ?? 0} handler(s): {string.Join(", ", handlers?.GetInvocationList().Select(d => d.Target?.GetType().Name + "#" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(d.Target!)) ?? [])}");
                }
            }
        }
        catch (Exception ex)
        {
            Report($"{component.GetType().Name}: re-evaluating x:Bind failed: {ex.GetBaseException().Message}");
        }
    }

    /// <summary>Frames by x:Name with the page they show, so a re-initialized window keeps its pages (and their view models).</summary>
    private static Dictionary<string, Frame> NamedFrames(DependencyObject? root) =>
        NameIndex(root as FrameworkElement).Values.OfType<Frame>().Where(f => f.Content is Page).ToDictionary(f => f.Name, StringComparer.Ordinal);

    private static List<(Frame Frame, Page Page)> KeepPages(object component, DependencyObject? oldRoot, FrameworkElement? root, Dictionary<string, Frame> oldFrames)
    {
        var kept = new List<(Frame, Page)>();
        HotReloadAgent.Log($"frames to keep: {string.Join(", ", oldFrames.Keys)}");
        Dictionary<string, FrameworkElement>? index = null;
        // Compiled x:Bind reads x:Name fields when it evaluates, which can be before a kept frame is back in its field
        // ({x:Bind rootFrame.CanGoBack} then tracks the frame the reload created): it reads them again once settled.
        if (oldFrames.Count > 0 && s_dispatcher is { } dispatcher)
        {
            dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => UpdateCompiledBindings(component));
        }
        foreach (var (name, oldFrame) in oldFrames)
        {
            var found = root?.FindName(name) ?? (index ??= NameIndex(root)).GetValueOrDefault(name);
            if (found is not Frame frame || ReferenceEquals(frame, oldFrame) || oldFrame.Content is not Page page)
            {
                HotReloadAgent.Log($"not keeping frame {name}: {found?.GetType().Name ?? "no such name in the new markup"}");
                continue;
            }
            if (KeepFrame(component, oldRoot, root, frame, oldFrame))
            {
                // The frame itself stays: its page, back and forward stacks and page cache are as the user left them.
                kept.Add((oldFrame, page));
                continue;
            }
            var backStack = oldFrame.BackStack.ToList();
            oldFrame.Content = null;
            frame.Content = page;
            // Navigation sets Page.Frame; moving the page doesn't, and the page's own Frame.Navigate/GoBack need it.
            page.SetValue(Page.FrameProperty, frame);
            kept.Add((frame, page));
            void CopyBackStack()
            {
                frame.BackStack.Clear();
                // An entry belongs to its frame; the new frame gets copies.
                backStack.ForEach(e => frame.BackStack.Add(new PageStackEntry(e.SourcePageType, e.Parameter, e.NavigationTransitionInfo)));
            }
            try
            {
                CopyBackStack();
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                // The new frame is still navigating (the window's constructor navigated it): copy once that settles.
                s_dispatcher?.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    try
                    {
                        CopyBackStack();
                    }
                    catch (Exception ex) when (ex is COMException or InvalidOperationException)
                    {
                        Report($"{name}: the back stack was not kept: {ex.Message}");
                    }
                });
            }
        }
        return kept;
    }

    /// <summary>What each NavigationView (by x:Name, else by order) had selected: an item's Tag, else its Content.</summary>
    private static List<(string View, string Item)> NavigationSelections(DependencyObject? root)
    {
        var result = new List<(string, string)>();
        var index = 0;
        foreach (var view in Descendants(root).OfType<NavigationView>())
        {
            var key = string.IsNullOrEmpty(view.Name) ? $"#{index}" : view.Name;
            index++;
            if (ReferenceEquals(view.SelectedItem, view.SettingsItem) && view.SelectedItem is not null)
            {
                result.Add((key, "\u0001settings"));
            }
            else if (ItemKey(view.SelectedItem) is { } item)
            {
                result.Add((key, item));
            }
        }
        return result;
    }

    private static string? ItemKey(object? item) => item switch
    {
        null => null,
        NavigationViewItem navigationItem => navigationItem.Tag?.ToString() ?? navigationItem.Content?.ToString(),
        _ => item.ToString(),
    };

    private static IEnumerable<DependencyObject> Descendants(DependencyObject? root)
    {
        if (root is null)
        {
            yield break;
        }
        var stack = new Stack<DependencyObject>([root]);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            // Pages kept from the old window have their own navigation, which isn't being rebuilt.
            if (node is Page && !ReferenceEquals(node, root))
            {
                continue;
            }
            for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--)
            {
                stack.Push(VisualTreeHelper.GetChild(node, i));
            }
        }
    }

    /// <summary>
    /// A rebuilt window shows the page the user was on: its markup re-selects the default navigation item (and the
    /// app's SelectionChanged handler navigates), so navigation on the kept frames is held off until the new tree has
    /// settled, and each NavigationView's selection is put back.
    /// </summary>
    private static void KeepLocation(FrameworkElement? root, List<Frame> frames, List<(string View, string Item)> selections)
    {
        if (root is null || (frames.Count == 0 && selections.Count == 0) || s_dispatcher is not { } dispatcher)
        {
            return;
        }
        void Cancel(object sender, Microsoft.UI.Xaml.Navigation.NavigatingCancelEventArgs e) => e.Cancel = true;
        frames.ForEach(frame => frame.Navigating += Cancel);
        void Release() => frames.ForEach(frame => frame.Navigating -= Cancel);
        void Settle()
        {
            dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                var released = false;
                void Done()
                {
                    if (!released)
                    {
                        released = true;
                        // The selection change handlers run before this.
                        dispatcher.TryEnqueue(DispatcherQueuePriority.Low, Release);
                    }
                }
                try
                {
                    if (RestoreSelections(root, selections, Done) == 0)
                    {
                        Done();
                    }
                    else
                    {
                        // A selected item that never shows (its parent collapsed) doesn't hold navigation off for good.
                        var timer = dispatcher.CreateTimer();
                        timer.Interval = TimeSpan.FromSeconds(2);
                        timer.IsRepeating = false;
                        timer.Tick += (_, _) => Done();
                        timer.Start();
                    }
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
                {
                    Done();
                    HotReloadAgent.Log($"restoring the selection failed: {ex.Message}");
                }
            });
        }
        if (root.IsLoaded)
        {
            Settle();
            return;
        }
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            root.Loaded -= OnLoaded;
            Settle();
        }
        root.Loaded += OnLoaded;
    }

    /// <summary>Selects each view's item again; returns how many wait for their item to show (each calls <paramref name="done"/> once all have).</summary>
    private static int RestoreSelections(DependencyObject root, List<(string View, string Item)> selections, Action done)
    {
        var index = 0;
        var waiting = 0;
        foreach (var view in Descendants(root).OfType<NavigationView>().ToList())
        {
            var key = string.IsNullOrEmpty(view.Name) ? $"#{index}" : view.Name;
            index++;
            if (selections.FirstOrDefault(s => s.View == key).Item is not { } wanted)
            {
                continue;
            }
            if (wanted == "\u0001settings")
            {
                view.SelectedItem = view.SettingsItem;
                continue;
            }
            IEnumerable<object> All(IEnumerable<object> items) => items.SelectMany(i => i is NavigationViewItem n ? All(n.MenuItems).Prepend(i) : [i]);
            if (All(view.MenuItems.Concat(view.FooterMenuItems)).FirstOrDefault(i => ItemKey(i) == wanted) is { } match && !ReferenceEquals(view.SelectedItem, match))
            {
                // A nested item shows once its parent's list is rebuilt: selected before that, it has no container,
                // and the app's SelectionChanged handler gets none.
                if (match is NavigationViewItem { IsLoaded: false } item)
                {
                    HotReloadAgent.Log($"selecting {wanted} once it shows");
                    void OnLoaded(object sender, RoutedEventArgs e)
                    {
                        item.Loaded -= OnLoaded;
                        if (!ReferenceEquals(view.SelectedItem, item))
                        {
                            view.SelectedItem = item;
                        }
                        if (--waiting == 0)
                        {
                            done();
                        }
                    }
                    item.Loaded += OnLoaded;
                    waiting++;
                    continue;
                }
                view.SelectedItem = match;
            }
        }
        return waiting;
    }

    /// <summary>Layout the new markup gives the frame, carried onto the frame that is kept.</summary>
    private static readonly DependencyProperty[] FrameLayout =
    [
        Grid.RowProperty, Grid.ColumnProperty, Grid.RowSpanProperty, Grid.ColumnSpanProperty, FrameworkElement.MarginProperty,
        FrameworkElement.HorizontalAlignmentProperty, FrameworkElement.VerticalAlignmentProperty, FrameworkElement.WidthProperty,
        FrameworkElement.HeightProperty, FrameworkElement.MinWidthProperty, FrameworkElement.MinHeightProperty, Control.PaddingProperty,
        Control.BackgroundProperty, UIElement.VisibilityProperty, Microsoft.UI.Xaml.Automation.AutomationProperties.AutomationIdProperty,
    ];

    /// <summary>Puts <paramref name="oldFrame"/> where the rebuilt markup put <paramref name="newFrame"/>; false when it can't.</summary>
    private static bool KeepFrame(object component, DependencyObject? oldRoot, DependencyObject? newRoot, Frame newFrame, Frame oldFrame)
    {
        // The new tree isn't loaded, so parents are found through the content properties markup sets.
        var place = Owner(newRoot, newFrame);
        if (place is null)
        {
            HotReloadAgent.Log($"keeping frame {oldFrame.Name}: no known parent");
            return false;
        }
        try
        {
            Owner(oldRoot, oldFrame)?.Invoke(null);
            foreach (var property in FrameLayout)
            {
                var value = newFrame.ReadLocalValue(property);
                if (value == DependencyProperty.UnsetValue)
                {
                    oldFrame.ClearValue(property);
                }
                else
                {
                    oldFrame.SetValue(property, value);
                }
            }
            place(oldFrame);
            CodeAddedItems.KeptFromMarkup(oldFrame);
            // The component's x:Name field now names the frame that is in the tree.
            foreach (var field in component.GetType().GetFields(InstanceMembers).Where(f => f.FieldType.IsInstanceOfType(oldFrame)))
            {
                if (ReferenceEquals(field.GetValue(component), newFrame))
                {
                    field.SetValue(component, oldFrame);
                }
            }
            HotReloadAgent.Log($"kept frame {oldFrame.Name}");
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            HotReloadAgent.Log($"keeping frame {oldFrame.Name} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Sets what holds <paramref name="target"/> under <paramref name="node"/> (null removes it), or null when not found.</summary>
    private static Action<UIElement?>? Owner(DependencyObject? node, UIElement target)
    {
        switch (node)
        {
            case null:
                return null;
            case Panel panel:
                for (var i = 0; i < panel.Children.Count; i++)
                {
                    var index = i;
                    if (ReferenceEquals(panel.Children[i], target))
                    {
                        return element => { if (element is null) { panel.Children.RemoveAt(index); } else { panel.Children[index] = element; } };
                    }
                    if (Owner(panel.Children[i], target) is { } inner)
                    {
                        return inner;
                    }
                }
                return null;
            case Border border:
                return ReferenceEquals(border.Child, target) ? element => border.Child = element : Owner(border.Child, target);
            case Viewbox box:
                return ReferenceEquals(box.Child, target) ? element => box.Child = element : Owner(box.Child, target);
            case SplitView split:
                return ReferenceEquals(split.Content, target) ? element => split.Content = element
                    : ReferenceEquals(split.Pane, target) ? element => split.Pane = element
                    : Owner(split.Content, target) ?? Owner(split.Pane, target);
            case UserControl control when control is not Page:
                return ReferenceEquals(control.Content, target) ? element => control.Content = element : Owner(control.Content, target);
            case ContentControl control:
                return ReferenceEquals(control.Content, target) ? element => control.Content = element : Owner(control.Content as DependencyObject, target);
            default:
                return null;
        }
    }
    private static DependencyObject? ContentRoot(object component) => component switch
    {
        Window window => window.Content,
        UserControl control => control.Content,
        ContentControl control => control.Content as DependencyObject,
        _ => component as DependencyObject,
    };

    private static void Collect(DependencyObject? node, Func<Page, bool> pageAffected, Func<object, bool> affected, List<(Frame, Page)> frames, List<FrameworkElement> others)
    {
        if (node is null || s_loadedDuringRefresh!.Contains(node))
        {
            return;
        }
        if (node is Frame { Content: Page page } frame && pageAffected(page) && !s_loadedDuringRefresh.Contains(page))
        {
            frames.Add((frame, page));
            return;
        }
        if (node is FrameworkElement element && node is not Page && affected(element))
        {
            others.Add(element);
            return;
        }
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            Collect(VisualTreeHelper.GetChild(node, i), pageAffected, affected, frames, others);
        }
    }

    /// <summary>
    /// Re-creates the frame's current page with its original navigation parameter: step to a placeholder so the
    /// current entry lands on the back stack, then go back to it. The frame's stacks end up as they were.
    /// </summary>
    private static void Renavigate(Frame frame, Page page)
    {
        HotReloadAgent.Log($"renavigate {page.GetType().FullName}");
        try
        {
            ClearErrorInfo();
            if (frame.CurrentSourcePageType != page.GetType())
            {
                // Shown without navigating (kept from a rebuilt window): there is no entry to go back to, so the new
                // instance takes its place directly.
                var fresh = (Page)Activator.CreateInstance(page.GetType())!;
                frame.Content = fresh;
                fresh.SetValue(Page.FrameProperty, frame);
                return;
            }
            var forward = frame.ForwardStack.ToList();
            if (!frame.Navigate(typeof(Page), null, new SuppressNavigationTransitionInfo()))
            {
                return;
            }
            frame.GoBack(new SuppressNavigationTransitionInfo());
            frame.ForwardStack.Clear();
            foreach (var entry in forward)
            {
                frame.ForwardStack.Add(entry);
            }
        }
        catch (Exception ex)
        {
            // Keep the page the user was looking at rather than a half-built one or the placeholder.
            if (frame.Content?.GetType() != page.GetType())
            {
                frame.Content = page;
            }
            ReportError(Describe(ex, ClassUris.GetValueOrDefault(page.GetType().FullName!), page.GetType().Name));
        }
    }

    /// <summary>Lets InitializeComponent run again on the same instance: markup re-adds its own resources.</summary>
    private static void PrepareForLoad(object component)
    {
        for (var t = component.GetType(); t is not null; t = t.BaseType)
        {
            t.GetField("_contentLoaded", InstanceMembers | BindingFlags.DeclaredOnly)?.SetValue(component, false);
        }
        if (component is FrameworkElement element)
        {
            element.Resources = new ResourceDictionary();
        }
    }

    /// <summary>
    /// What a failed reload puts back: markup that doesn't load (a resource it uses isn't defined yet) must leave the
    /// UI the user had, not a half-built tree.
    /// </summary>
    private static Action Snapshot(object component)
    {
        var resources = (component as FrameworkElement)?.Resources;
        var content = component switch { Window window => window.Content, UserControl control => control.Content, ContentControl control => control.Content, _ => null };
        return () =>
        {
            try
            {
                if (component is FrameworkElement element && resources is not null)
                {
                    element.Resources = resources;
                }
                switch (component)
                {
                    case Window window: window.Content = content as UIElement; break;
                    case UserControl control: control.Content = content as UIElement; break;
                    case ContentControl control: control.Content = content; break;
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
            {
                HotReloadAgent.Log($"restoring {component.GetType().Name} failed: {ex.Message}");
            }
        };
    }

    /// <summary>The component's own dependency properties (declared by the app's types) that hold a collection.</summary>
    private static List<(DependencyProperty Property, object Value)> OwnCollections(object component)
    {
        var result = new List<(DependencyProperty, object)>();
        if (component is not DependencyObject element)
        {
            return result;
        }
        for (var type = component.GetType(); type is not null && type.Assembly == component.GetType().Assembly; type = type.BaseType)
        {
            foreach (var member in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly).Select(f => f.GetValue(null))
                .Concat(type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(p => p.PropertyType == typeof(DependencyProperty)).Select(p => p.GetValue(null))))
            {
                if (member is DependencyProperty property && element.ReadLocalValue(property) is System.Collections.IEnumerable value and not string &&
                    value != DependencyProperty.UnsetValue)
                {
                    result.Add((property, value));
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Elements a component shows that come from elsewhere (a page's sample handed to a ContentPresenter through
    /// Content="{x:Bind Example}") are freed from the old tree, so the rebuilt one can show them; the returned action
    /// puts them back when the reload is undone.
    /// </summary>
    private static Action ReleaseHostedContent(object component)
    {
        var released = new List<(DependencyObject Host, DependencyProperty Property, object Content)>();
        // What the component's own markup built (a NavigationView's content) is rebuilt with it and stays.
        var own = (ContentRoot(component) as FrameworkElement)?.BaseUri?.ToString();
        bool Foreign(UIElement content) => content is not FrameworkElement element || element.BaseUri?.ToString() != own;
        // The component's named presenters and content controls (its x:Name fields): template parts and kept Frames are
        // never among them.
        for (var type = component.GetType(); type is not null && type.Assembly == component.GetType().Assembly; type = type.BaseType)
        {
            foreach (var field in type.GetFields(InstanceMembers | BindingFlags.DeclaredOnly))
            {
                switch (field.GetValue(component))
                {
                    case ContentPresenter { Content: UIElement content } presenter when Foreign(content):
                        released.Add((presenter, ContentPresenter.ContentProperty, content));
                        presenter.Content = null;
                        break;
                    case ContentControl { Content: UIElement content } control and not Frame when Foreign(content):
                        released.Add((control, ContentControl.ContentProperty, content));
                        control.Content = null;
                        break;
                }
            }
        }
        return () =>
        {
            foreach (var (host, property, content) in released)
            {
                try
                {
                    host.SetValue(property, content);
                }
                catch (Exception ex) when (ex is COMException or ArgumentException)
                {
                    HotReloadAgent.Log($"restoring hosted content failed: {ex.Message}");
                }
            }
        };
    }

    /// <summary>
    /// Removes the component's event handlers from an element of the old tree that is about to change (its code-added
    /// items move to the rebuilt one): the app's handler isn't called for an element it no longer shows. The compiled
    /// markup's wiring isn't known, so every method of the component that fits an event is removed from it (removing
    /// one that wasn't subscribed does nothing); handlers markup reloaded since then name other targets too.
    /// </summary>
    private static void Unwire(object component, string element, object owner)
    {
        var handlers = new List<(EventInfo Event, Delegate Handler)>();
        var methods = component.GetType().GetMethods(InstanceMembers).Where(m => !m.IsSpecialName && !m.IsGenericMethodDefinition).ToList();
        foreach (var info in owner.GetType().GetEvents(BindingFlags.Instance | BindingFlags.Public))
        {
            if (info.EventHandlerType is not { } handlerType)
            {
                continue;
            }
            foreach (var method in methods)
            {
                if (Delegate.CreateDelegate(handlerType, component, method, throwOnBindFailure: false) is { } handler)
                {
                    handlers.Add((info, handler));
                }
            }
        }
        if (ClassUris.TryGetValue(component.GetType().FullName!, out var uri) && Manifests.TryGetValue(uri, out var manifest))
        {
            foreach (var wiring in manifest.Events.Where(e => e.Element == element && e.Path is not null))
            {
                if (owner.GetType().GetEvent(wiring.Event) is { EventHandlerType: { } handlerType } info &&
                    MakeHandler(handlerType, ResolvePath(component, wiring.Path), wiring.Handler) is { } handler)
                {
                    handlers.Add((info, handler));
                }
            }
        }
        foreach (var (info, handler) in handlers)
        {
            try
            {
                info.RemoveEventHandler(owner, handler);
            }
            catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException or TargetInvocationException)
            {
                HotReloadAgent.Log($"unwiring {element}.{info.Name} failed: {ex.Message}");
            }
        }
    }

    private static void ReloadMarkup(object component)
    {
        var restore = Snapshot(component);
        var keepAdded = CodeAddedItems.Detach(component, (element, owner) => Unwire(component, element, owner));
        var hosted = ReleaseHostedContent(component);
        try
        {
            ClearErrorInfo();
            PrepareForLoad(component);
            component.GetType().GetMethod("InitializeComponent", InstanceMembers, Type.EmptyTypes)?.Invoke(component, null);
        }
        catch (Exception ex)
        {
            restore();
            hosted();
            ReportError(Describe(ex, ClassUris.GetValueOrDefault(component.GetType().FullName!), component.GetType().Name));
        }
        finally
        {
            keepAdded(component);
        }
    }

    private static void Reinitialize(object component, bool retry = true)
    {
        var type = component.GetType();
        HotReloadAgent.Log($"reinitialize {type.FullName}");
        var restore = Snapshot(component);
        var keepAdded = CodeAddedItems.Detach(component, (element, owner) => Unwire(component, element, owner));
        var hosted = ReleaseHostedContent(component);
        // The lists the old tree shows let go of the collections they show: otherwise they keep listening after they're
        // gone, and a collection the replay changes (or one the app changes later) calls into them and fails.
        var detached = DetachItemsSources(component);
        var again = false;
        try
        {
            ClearErrorInfo();
            PrepareForLoad(component);
            var replay = type.GetMethod(ReplayMethod, InstanceMembers, Type.EmptyTypes);
            // The constructor body runs again; subscriptions it makes to objects that outlive it would otherwise double.
            var events = replay is null ? null : new LiveObjects.EventSnapshot(component);
            var collections = replay is null ? [] : OwnCollections(component);
            string[]? reset = null;
            if (replay is not null)
            {
                // Starts the collections the constructor fills over, so the replay doesn't add the same items again.
                reset = type.GetMethod(ResetMethod, InstanceMembers, Type.EmptyTypes)?.Invoke(component, null) as string[];
            }
            (replay ?? type.GetMethod("InitializeComponent", InstanceMembers, Type.EmptyTypes))?.Invoke(component, null);
            NoteDroppedItems(component, reset);
            events?.RemoveDuplicates();
            foreach (var (property, value) in collections)
            {
                // The constructor creates it empty; whoever hosts the component filled it (ControlExample.Substitutions
                // from the page's markup). The replay would replace it with a new empty one.
                if (component is DependencyObject element && !ReferenceEquals(element.GetValue(property), value))
                {
                    element.SetValue(property, value);
                }
            }
        }
        catch (Exception ex)
        {
            restore();
            hosted();
            detached?.Invoke();
            // The list being replaced can still be releasing the items of a collection the reload reset (it grew since
            // the last reload): the runtime then fails a call into it (E_NOINTERFACE, or the object is already gone).
            // That settles once it's released, so the reload is tried once more before it's reported.
            var cause = ex.GetBaseException();
            // (CsWinRT raises E_NOINTERFACE as an InvalidCastException.)
            var transient = cause is ObjectDisposedException || (cause is COMException or InvalidCastException && cause.HResult == unchecked((int)0x80004002));
            if (retry && transient)
            {
                HotReloadAgent.Log($"reinitialize {type.FullName} failed; trying again: {ex}");
                again = true;
            }
            else if (transient)
            {
                ReportError($"{ClassUris.GetValueOrDefault(type.FullName!) ?? type.Name}: the page couldn't be rebuilt in place ({cause.Message.Split('\n')[0].Trim()}); save again, or restart the app if it keeps failing");
            }
            else
            {
                ReportError(Describe(ex, ClassUris.GetValueOrDefault(type.FullName!), type.Name));
            }
        }
        finally
        {
            // Into the rebuilt collections, or back into the old ones when the reload was undone.
            keepAdded(component);
        }
        if (again)
        {
            // In a callback of its own (still within the update when there is one, so its outcome is this save's).
            if (s_deferredBinds is { } deferred)
            {
                deferred.Insert(0, () => Reinitialize(component, retry: false));
            }
            else
            {
                s_dispatcher?.TryEnqueue(() => Reinitialize(component, retry: false));
            }
        }
    }

    /// <summary>
    /// A collection the replayed constructor fills starts over on reload (see the generated reset method, which returns
    /// "Name|Count" for each as it was): items code added to it while the app ran are gone, and the save says so.
    /// </summary>
    private static void NoteDroppedItems(object component, string[]? reset)
    {
        foreach (var entry in reset ?? [])
        {
            var name = entry[..entry.LastIndexOf('|')];
            var before = int.Parse(entry[(entry.LastIndexOf('|') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            var type = component.GetType();
            var value = type.GetField(name, InstanceMembers)?.GetValue(component) ?? type.GetProperty(name, InstanceMembers)?.GetValue(component);
            var after = (value as System.Collections.ICollection)?.Count ?? 0;
            if (after < before && s_notes is { } notes)
            {
                var note = $"{type.Name}.{name} starts over on each reload because its constructor fills it: {before - after} item(s) added while the app ran were dropped";
                if (!notes.Contains(note))
                {
                    notes.Add(note);
                }
            }
        }
    }

    /// <summary>
    /// Clears ItemsSource on the component's named lists (its x:Name fields that hold an ItemsRepeater or ItemsControl), so
    /// they stop listening to the collections they show; the returned action sets them back.
    /// </summary>
    private static Action DetachItemsSources(object component)
    {
        var detached = new List<(DependencyObject List, DependencyProperty Property, object Value)>();
        for (var type = component.GetType(); type is not null && type.Assembly == component.GetType().Assembly; type = type.BaseType)
        {
            foreach (var field in type.GetFields(InstanceMembers | BindingFlags.DeclaredOnly))
            {
                var list = field.GetValue(component) as DependencyObject;
                var property = list switch
                {
                    ItemsRepeater => ItemsRepeater.ItemsSourceProperty,
                    ItemsControl => ItemsControl.ItemsSourceProperty,
                    _ => null,
                };
                if (property is not null && list!.GetValue(property) is { } value)
                {
                    detached.Add((list, property, value));
                    list.ClearValue(property);
                }
            }
        }
        return () =>
        {
            foreach (var (list, property, value) in detached)
            {
                list.SetValue(property, value);
            }
        };
    }

    /// <summary>After every component load (WinUI's load callback), on the loading thread.</summary>
    private static void OnLoaded(IntPtr componentAbi, string uri)
    {
        try
        {
            var component = WinRT.MarshalInspectable<object>.FromAbi(componentAbi);
            if (component is null)
            {
                return;
            }
            HotReloadAgent.Log($"loaded {component.GetType().FullName} {uri}");
            s_loadedDuringRefresh?.Add(component);
            if (ContentRoot(component) is FrameworkElement root)
            {
                void Baseline(object sender, RoutedEventArgs e)
                {
                    root.Loaded -= Baseline;
                    UiState.CaptureBaseline(root);
                    ReloadImages(root);
                }
                root.Loaded += Baseline;
            }
            if (component is Window window)
            {
                lock (KnownWindows)
                {
                    if (!KnownWindows.Any(w => w.TryGetTarget(out var known) && ReferenceEquals(known, window)))
                    {
                        KnownWindows.Add(new WeakReference<Window>(window));
                    }
                }
            }
            if (Manifests.TryGetValue(uri, out var manifest))
            {
                Wire(component, manifest);
            }
            else if (RealCompilerSpike && (component as FrameworkElement ?? ContentRoot(component) as FrameworkElement) is { } spikeRoot && s_dispatcher is { } spikeDispatcher)
            {
                // SPIKE: re-initialized in place, so the root's Loading (which runs the compiled Bindings.Initialize) won't fire.
                HotReloadAgent.Log($"spike: {component.GetType().Name} root loaded={spikeRoot.IsLoaded}");
                if (spikeRoot.IsLoaded)
                {
                    spikeDispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => UpdateCompiledBindings(component));
                }
            }
            CodeAddedItems.RecordMarkup(component);
            Stamp(component);
        }
        catch (Exception ex)
        {
            Report("load callback failed: " + ex.GetBaseException().Message);
        }
    }

    /// <summary>Counts refreshes; a component loaded before the last refresh that touched its type is out of date.</summary>
    private static int s_refreshes;
    private static int s_allChanged;
    private static readonly Dictionary<string, int> TypeChanged = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<object, StrongBox<int>> Stamps = new();

    private static void MarkChanged(IEnumerable<string> types, bool all)
    {
        s_refreshes++;
        if (all)
        {
            s_allChanged = s_refreshes;
        }
        foreach (var type in types)
        {
            TypeChanged[type] = s_refreshes;
        }
    }

    private static bool IsStale(object component) =>
        Stamps.TryGetValue(component, out var stamp) &&
        stamp.Value < Math.Max(s_allChanged, TypeChanged.GetValueOrDefault(component.GetType().FullName!));

    /// <summary>
    /// Records that <paramref name="component"/> has the current markup. A page a Frame keeps in its cache
    /// (NavigationCacheMode) isn't in the tree when it changes: it's brought up to date as it's shown again.
    /// </summary>
    private static void Stamp(object component)
    {
        if (Stamps.TryGetValue(component, out var stamp))
        {
            stamp.Value = s_refreshes;
            return;
        }
        Stamps.Add(component, new StrongBox<int>(s_refreshes));
        if (component is Page page)
        {
            page.Loaded += (_, _) =>
            {
                if (IsStale(page) && s_dispatcher is { } dispatcher)
                {
                    dispatcher.TryEnqueue(() =>
                    {
                        if (!IsStale(page))
                        {
                            return;
                        }
                        var state = UiState.Take(page.Content);
                        Reinitialize(page);
                        UiState.RestoreWhenLoaded(page.Content as FrameworkElement, state, dispatcher);
                    });
                }
            };
        }
    }

    /// <summary>Loading handlers waiting to evaluate a component's x:Bind, so a reload before it loads can drop them.</summary>
    private static readonly ConditionalWeakTable<object, StrongBox<Windows.Foundation.TypedEventHandler<FrameworkElement, object>>> PendingBinds = new();

    /// <summary>
    /// Named elements under <paramref name="root"/>: its visual tree, content not shown yet, and flyouts (first name wins).
    /// </summary>
    private static Dictionary<string, FrameworkElement> NameIndex(FrameworkElement? root)
    {
        var index = new Dictionary<string, FrameworkElement>(StringComparer.Ordinal);
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<DependencyObject>();
        void Push(object? item)
        {
            if (item is DependencyObject d && seen.Add(d))
            {
                pending.Push(d);
            }
        }
        void PushFlyout(Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase? flyout)
        {
            if (flyout is Flyout { Content: { } content })
            {
                Push(content);
            }
            else if (flyout is MenuFlyout menu)
            {
                foreach (var item in menu.Items)
                {
                    Push(item);
                }
            }
        }
        Push(root);
        while (pending.TryPop(out var current))
        {
            if (current is FrameworkElement { Name: { Length: > 0 } name } element)
            {
                index.TryAdd(name, element);
            }
            if (!ReferenceEquals(current, root) && current is Frame or UserControl or Page)
            {
                // Another component's names are its own.
                continue;
            }
            try
            {
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
                {
                    Push(VisualTreeHelper.GetChild(current, i));
                }
                switch (current)
                {
                    case Panel panel:
                        foreach (var child in panel.Children)
                        {
                            Push(child);
                        }
                        break;
                    case ContentControl control:
                        Push(control.Content);
                        break;
                    case Border border:
                        Push(border.Child);
                        break;
                    case ItemsControl items:
                        foreach (var item in items.Items)
                        {
                            Push(item);
                        }
                        break;
                    case MenuFlyoutSubItem sub:
                        foreach (var item in sub.Items)
                        {
                            Push(item);
                        }
                        break;
                    case Control control:
                        // Not templated yet (this runs right after the markup loads): what a control like TitleBar or
                        // NavigationView holds is only reachable through its properties (Content, RightHeader, MenuItems).
                        foreach (var property in ContentProperties(control.GetType()))
                        {
                            switch (property.GetValue(control))
                            {
                                case IList<object> list:
                                    foreach (var item in list)
                                    {
                                        Push(item);
                                    }
                                    break;
                                case var value:
                                    Push(value);
                                    break;
                            }
                        }
                        break;
                }
                if (current is UIElement ui)
                {
                    PushFlyout(ui.ContextFlyout);
                }
                if (current is FrameworkElement framework)
                {
                    PushFlyout(Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase.GetAttachedFlyout(framework));
                }
                if (current is Button button)
                {
                    PushFlyout(button.Flyout);
                }
            }
            catch (Exception ex) when (ex is COMException or TargetInvocationException)
            {
            }
        }
        return index;
    }

    private static readonly Dictionary<Type, PropertyInfo[]> s_contentProperties = [];

    /// <summary>A control type's public properties that can hold markup elements: object, UIElement and IList&lt;object&gt;.</summary>
    private static PropertyInfo[] ContentProperties(Type type)
    {
        if (!s_contentProperties.TryGetValue(type, out var properties))
        {
            s_contentProperties[type] = properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.Name is not ("DataContext" or "Tag") &&
                    (p.PropertyType == typeof(object) || typeof(UIElement).IsAssignableFrom(p.PropertyType) || p.PropertyType == typeof(IList<object>)))
                .ToArray();
        }
        return properties;
    }

    private static void Wire(object component, ComponentManifest manifest)
    {
        var type = component.GetType();
        var scope = component as FrameworkElement ?? (component as Window)?.Content as FrameworkElement;
        Dictionary<string, FrameworkElement>? index = null;
        // FindName misses elements the markup puts in some controls' content (a TitleBar's Content and headers) and in
        // flyouts; those are found by walking what the markup built instead.
        object? FindNamed(string name) => scope?.FindName(name) ?? (index ??= NameIndex(scope)).GetValueOrDefault(name);
        // Names the transform gave unnamed elements so they can be found here. Once found they're cleared: an element's
        // Name is also its UI Automation id and what DevTools shows, and these are no one's.
        var generated = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var name in manifest.Events.Select(e => e.Element).Concat(manifest.Bindings.Select(b => b.Element)).OfType<string>()
            .Where(n => n.StartsWith(GeneratedNamePrefix, StringComparison.Ordinal)).Distinct())
        {
            if (FindNamed(name) is FrameworkElement element)
            {
                generated[name] = element;
                try
                {
                    element.Name = string.Empty;
                }
                catch (COMException)
                {
                }
            }
        }
        object? Find(string? name) => name is null ? component : generated.TryGetValue(name, out var found) ? found : FindNamed(name);

        foreach (var name in manifest.Names)
        {
            var field = type.GetField(name, InstanceMembers);
            var value = Find(name);
            if (field is not null && value is not null && field.FieldType.IsInstanceOfType(value))
            {
                field.SetValue(component, value);
            }
        }

        foreach (var (name, text) in manifest.RootProperties)
        {
            try
            {
                if (type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public) is { CanWrite: true } property)
                {
                    property.SetValue(component, property.PropertyType == typeof(string) ? text : XamlBindingHelper.ConvertValue(property.PropertyType, text));
                }
            }
            catch (Exception ex)
            {
                Report($"{type.Name}.{name}=\"{text}\": {ex.GetBaseException().Message}");
            }
        }

        foreach (var wiring in manifest.Events)
        {
            var element = Find(wiring.Element);
            var eventInfo = element?.GetType().GetEvent(wiring.Event);
            if (element is null || eventInfo?.EventHandlerType is not { } handlerType)
            {
                HotReloadAgent.Log($"no event {wiring.Event} on {wiring.Element}: {element?.GetType().FullName ?? "not found"} (scope {scope?.GetType().FullName})");
                Report($"{type.Name}: {wiring.Label ?? wiring.Element ?? "root"} has no event {wiring.Event}");
                continue;
            }
            var handler = MakeHandler(handlerType, wiring.Path is null ? component : ResolvePath(component, wiring.Path), wiring.Handler);
            if (handler is null)
            {
                Report(wiring.Waiting(type.Name));
                continue;
            }
            if (wiring.Element is null)
            {
                // The root survives in-place re-initialization; replace the handler wired by the previous load,
                // or the one compiled code connected when the app started.
                var wired = RootHandlers.GetOrCreateValue(component);
                eventInfo.RemoveEventHandler(element, wired.Remove(wiring.Event, out var previous) ? previous : handler);
                wired[wiring.Event] = handler;
            }
            eventInfo.AddEventHandler(element, handler);
        }

        if (manifest.Bindings.Count > 0 && scope is not null)
        {
            void ApplyAll()
            {
                ResetWatchers(component);
                foreach (var binding in manifest.Bindings)
                {
                    ApplyBinding(component, type, Find(binding.Element) as DependencyObject, binding, component);
                }
            }
            // x:Bind evaluates when the component loads, after the constructor has set up what it binds to.
            if (PendingBinds.TryGetValue(component, out var pending))
            {
                // From an earlier load that was replaced before it got that far: it holds that load's elements.
                scope.Loading -= pending.Value;
                PendingBinds.Remove(component);
            }
            if (component is FrameworkElement { IsLoaded: true } or Window { Visible: true })
            {
                // Re-initialized in place: it won't load again, so evaluate once the constructor replay has finished
                // (within the update when there is one, so problems are reported with it).
                if (s_deferredBinds is { } deferred)
                {
                    deferred.Add(ApplyAll);
                }
                else
                {
                    s_dispatcher?.TryEnqueue(ApplyAll);
                }
            }
            else
            {
                void Bind(FrameworkElement sender, object args)
                {
                    scope.Loading -= Bind;
                    PendingBinds.Remove(component);
                    ApplyAll();
                }
                scope.Loading += Bind;
                PendingBinds.Add(component, new StrongBox<Windows.Foundation.TypedEventHandler<FrameworkElement, object>>(Bind));
            }
            BindingsStandIn.Attach(component, ApplyAll);
        }
        else
        {
            BindingsStandIn.Attach(component, () => { });
        }
    }

    /// <summary>Follows a property path ("ViewModel.Child") from an object; an empty path is the object itself.</summary>
    private static object? ResolvePath(object? source, string path)
    {
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            source = source?.GetType().GetProperty(segment, InstanceMembers)?.GetValue(source)
                ?? source?.GetType().GetField(segment, InstanceMembers)?.GetValue(source);
        }
        return source;
    }

    /// <summary>
    /// A handler of <paramref name="handlerType"/> calling <paramref name="name"/> on <paramref name="target"/>: either a method with
    /// a compatible signature, or (as x:Bind event binding allows) one without parameters.
    /// </summary>
    private static Delegate? MakeHandler(Type handlerType, object? target, string name)
    {
        if (target is null)
        {
            return null;
        }
        var methods = target.GetType().GetMethods(InstanceMembers).Where(m => m.Name == name).ToList();
        foreach (var method in methods)
        {
            if (Delegate.CreateDelegate(handlerType, target, method, throwOnBindFailure: false) is { } handler)
            {
                return handler;
            }
        }
        if (methods.FirstOrDefault(m => m.GetParameters().Length == 0) is { } parameterless)
        {
            var parameters = handlerType.GetMethod("Invoke")!.GetParameters().Select(p => System.Linq.Expressions.Expression.Parameter(p.ParameterType)).ToArray();
            var call = System.Linq.Expressions.Expression.Call(System.Linq.Expressions.Expression.Constant(target), parameterless);
            return System.Linq.Expressions.Expression.Lambda(handlerType, call, parameters).Compile();
        }
        return null;
    }

    /// <summary>Watchers of one-way x:Bind functions, per element they bind (or component for root scope), replaced on re-evaluation.</summary>
    private static readonly ConditionalWeakTable<object, List<IDisposable>> Watchers = new();

    private static void ResetWatchers(object owner)
    {
        if (Watchers.TryGetValue(owner, out var list))
        {
            list.ForEach(watcher => watcher.Dispose());
            list.Clear();
        }
    }

    private static DependencyProperty? FindProperty(Type? owner, string name)
    {
        for (var t = owner; t is not null; t = t.BaseType)
        {
            var value = t.GetProperty(name + "Property", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                ?? t.GetField(name + "Property", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (value is DependencyProperty property)
            {
                return property;
            }
        }
        return null;
    }

    /// <summary>
    /// An x:Bind that can't be applied, reported against the attribute in the user's file ("Pages/Home.xaml(12,20): ...")
    /// as an error of the save: no later save makes it work unless the markup or code changes.
    /// </summary>
    private static void BindingError(Type component, RootBinding spec, string reason)
    {
        var where = ClassUris.TryGetValue(component.FullName!, out var uri) ? uri : component.Name;
        var location = spec.Location is { } position ? $"({position})" : "";
        ReportError($"{where}{location}: x:Bind {spec.Path} into {spec.Property}: {reason}");
    }

    /// <param name="source">What the path is relative to: the component, or a template item.</param>
    /// <param name="functions">The component type holding generated x:Bind function methods.</param>
    /// <param name="watchOwner">Owns one-way function watchers, so they are dropped on the next evaluation.</param>
    private static void ApplyBinding(object? source, Type functions, DependencyObject? target, RootBinding spec, object watchOwner)
    {
        try
        {
            var owner = spec.OwnerType is null ? target?.GetType() : FindType(spec.OwnerType);
            var property = FindProperty(owner, spec.Property);
            if (target is not null && property is null && spec.OwnerType is null &&
                target.GetType().GetProperty(spec.Property, BindingFlags.Public | BindingFlags.Instance) is { CanWrite: true } clr)
            {
                ApplyToClrProperty(source, functions, target, clr, spec, watchOwner);
                return;
            }
            if (target is null || property is null)
            {
                BindingError(functions, spec, $"the property {spec.Property} was not found on {target?.GetType().Name ?? "the element"}");
                return;
            }
            if (spec.Function is { } function)
            {
                BindFunction(source, functions, target, property, owner!.GetProperty(spec.Property)?.PropertyType, spec, function, watchOwner);
                return;
            }
            // One-time and one-way x:Bind are values set on the target (again whenever something along the path changes),
            // as compiled x:Bind does. That passes any object (a .NET list into an IList<T> property, an element into
            // Content), which a Binding can't.
            var mode = Enum.TryParse<BindingMode>(spec.Mode, out var parsed) ? parsed : BindingMode.OneTime;
            var propertyType = owner?.GetProperty(spec.Property)?.PropertyType;
            if (mode != BindingMode.TwoWay && TrySetOnce(source, spec.Path, target, property, propertyType, spec))
            {
                if (mode == BindingMode.OneWay && source is not null && spec.Path.Length > 0)
                {
                    var root = source;
                    Watchers.GetOrCreateValue(watchOwner).Add(new PathWatcher(root, [spec.Path], () => TrySetOnce(root, spec.Path, target, property, propertyType, spec)));
                }
                return;
            }
            // Binding only walks public properties; x:Bind also reaches fields (x:Name elements) and non-public members.
            // Those leading steps are resolved here and the binding starts at the object they lead to.
            var path = spec.Path;
            while (path.Length > 0 && source is not null)
            {
                var dot = path.IndexOf('.');
                var head = dot < 0 ? path : path[..dot];
                if (head.Contains('[') || source.GetType().GetProperty(head, BindingFlags.Public | BindingFlags.Instance) is not null)
                {
                    break;
                }
                source = source.GetType().GetField(head, InstanceMembers)?.GetValue(source)
                    ?? source.GetType().GetProperty(head, InstanceMembers)?.GetValue(source);
                path = dot < 0 ? "" : path[(dot + 1)..];
            }
            path = WithoutNullableValue(source?.GetType(), path);
            var binding = new Binding
            {
                Path = new PropertyPath(path),
                Source = source,
                Mode = mode,
            };
            if (Enum.TryParse<UpdateSourceTrigger>(spec.UpdateSourceTrigger, out var trigger))
            {
                binding.UpdateSourceTrigger = trigger;
            }
            if (spec.Converter is { } converter && Resource(target, converter) is IValueConverter valueConverter)
            {
                binding.Converter = valueConverter;
            }
            else if (binding.Mode != BindingMode.TwoWay && owner?.GetProperty(spec.Property)?.PropertyType == typeof(string))
            {
                // x:Bind shows any value as text (ToString); a Binding would show the boxed WinRT type name.
                binding.Converter = TextConverter.Instance;
            }
            if (spec.ConverterParameter is { } parameter) binding.ConverterParameter = parameter;
            if (spec.FallbackValue is { } fallback) binding.FallbackValue = fallback;
            if (spec.TargetNullValue is { } nullValue) binding.TargetNullValue = nullValue;
            BindingOperations.SetBinding(target, property, binding);
        }
        catch (Exception ex)
        {
            BindingError(functions, spec, $"it can't be set ({ex.GetBaseException().Message})");
        }
    }

    /// <summary>Reads a property path by reflection and sets it on the target; false when it can't (a Binding is used instead).</summary>
    private static bool TrySetOnce(object? source, string path, DependencyObject target, DependencyProperty property, Type? propertyType, RootBinding spec)
    {
        if (!TryReadPath(source, path, out var value) || !TryConvertForTarget(value, target, propertyType, spec, out value))
        {
            return false;
        }
        try
        {
            target.SetValue(property, value);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidCastException or COMException)
        {
            HotReloadAgent.Log($"x:Bind {spec.Path}: setting it directly failed ({ex.Message}); binding instead");
            return false;
        }
    }

    /// <summary>The value at a property path (fields and non-public members included, as x:Bind reaches them); false for shapes it can't read.</summary>
    private static bool TryReadPath(object? source, string path, out object? value)
    {
        value = source;
        foreach (var segment in path.Length == 0 ? [] : path.Split('.'))
        {
            if (value is null || segment.Contains('[') || segment.Contains('('))
            {
                return false;
            }
            var type = value.GetType();
            if (segment == "Value" && type.IsValueType && type.GetProperty("Value") is null)
            {
                // A nullable's Value: the boxed value itself.
                continue;
            }
            if (type.GetProperty(segment, InstanceMembers) is { } getter && getter.GetIndexParameters().Length == 0)
            {
                value = getter.GetValue(value);
            }
            else if (type.GetField(segment, InstanceMembers) is { } field)
            {
                value = field.GetValue(value);
            }
            else
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>What x:Bind would assign: through the converter, as text for a string property, TargetNullValue for null, coerced.</summary>
    private static bool TryConvertForTarget(object? value, DependencyObject target, Type? propertyType, RootBinding spec, out object? result)
    {
        result = value;
        if (spec.Converter is { } converter)
        {
            if (Resource(target, converter) is not IValueConverter valueConverter)
            {
                return false;
            }
            value = valueConverter.Convert(value, propertyType ?? typeof(object), spec.ConverterParameter, "");
        }
        else if (propertyType == typeof(string) && value is not null and not string)
        {
            value = TextConverter.Instance.Convert(value, typeof(string), null, "");
        }
        if (value is null && spec.TargetNullValue is { } nullValue)
        {
            value = nullValue;
        }
        return TryCoerce(value, propertyType, out result);
    }

    /// <summary>
    /// x:Bind into a plain .NET property (a non-visual helper's Value, which no Binding can target): its setter is called
    /// with the value, and again whenever something along the path changes, as compiled x:Bind does.
    /// </summary>
    private static void ApplyToClrProperty(object? source, Type functions, DependencyObject target, PropertyInfo clr, RootBinding spec, object watchOwner)
    {
        System.Reflection.MethodInfo? method = null;
        if (spec.Function is { } function &&
            (method = functions.GetMethod(function, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, [typeof(object)])) is null)
        {
            return;
        }
        bool Evaluate()
        {
            object? value;
            if (method is not null)
            {
                try
                {
                    value = method.Invoke(null, [source]);
                }
                catch (TargetInvocationException)
                {
                    value = spec.FallbackValue;
                }
            }
            else if (!TryReadPath(source, spec.Path, out value))
            {
                return false;
            }
            if (value == DependencyProperty.UnsetValue)
            {
                // A function that doesn't compile yet: the helper reports it as waiting.
                return true;
            }
            if (value is null && clr.PropertyType.IsValueType && Nullable.GetUnderlyingType(clr.PropertyType) is null)
            {
                // As compiled x:Bind: a null along the path leaves the property as it is.
                return true;
            }
            if (!TryConvertForTarget(value, target, clr.PropertyType, spec, out value))
            {
                return false;
            }
            try
            {
                clr.SetValue(target, value);
            }
            catch (TargetInvocationException)
            {
                return false;
            }
            return true;
        }
        if (!Evaluate())
        {
            BindingError(functions, spec, $"its value can't be converted to {clr.PropertyType.Name}");
            return;
        }
        var watch = method is not null ? spec.Watch : spec.Path.Length > 0 ? [spec.Path] : [];
        if (!string.Equals(spec.Mode, "OneTime", StringComparison.OrdinalIgnoreCase) && source is not null && watch.Count > 0)
        {
            Watchers.GetOrCreateValue(watchOwner).Add(new PathWatcher(source, watch, () =>
            {
                if (s_dispatcher is null || s_dispatcher.HasThreadAccess) Evaluate();
                else s_dispatcher.TryEnqueue(() => Evaluate());
            }));
        }
    }

    /// <summary>The conversions compiled x:Bind makes implicitly (bool to Visibility and back, text to an enum, numbers).</summary>
    private static bool TryCoerce(object? value, Type? type, out object? result)
    {
        result = value;
        if (value is null || type is null || type.IsInstanceOfType(value))
        {
            return true;
        }
        type = Nullable.GetUnderlyingType(type) ?? type;
        switch (value)
        {
            case bool flag when type == typeof(Visibility):
                result = flag ? Visibility.Visible : Visibility.Collapsed;
                return true;
            case Visibility visibility when type == typeof(bool):
                result = visibility == Visibility.Visible;
                return true;
            case string text when type.IsEnum && Enum.TryParse(type, text, ignoreCase: true, out var parsed):
                result = parsed;
                return true;
            case IConvertible when (type.IsPrimitive || type == typeof(decimal)) && (value.GetType().IsPrimitive || value is decimal):
                try
                {
                    result = Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture);
                    return true;
                }
                catch (Exception ex) when (ex is InvalidCastException or OverflowException)
                {
                    return false;
                }
            default:
                return type.IsInstanceOfType(value);
        }
    }

    /// <summary>
    /// A binding reads a nullable property (CheckBox.IsChecked) as its boxed value, which has no Value property: x:Bind's
    /// "IsChecked.Value" becomes "IsChecked". Followed by property type from <paramref name="type"/>.
    /// </summary>
    private static string WithoutNullableValue(Type? type, string path)
    {
        var segments = path.Split('.');
        var kept = new List<string>();
        foreach (var segment in segments)
        {
            if (segment == "Value" && type is not null && Nullable.GetUnderlyingType(type) is { } underlying)
            {
                type = underlying;
                continue;
            }
            kept.Add(segment);
            type = segment.Contains('[') ? null : type?.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance)?.PropertyType;
        }
        return string.Join('.', kept);
    }

    /// <summary>Content files replaced while the app runs (app-relative, "/"-separated).</summary>
    private static readonly HashSet<string> ChangedAssets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Images showing a replaced file load it again. The image cache would otherwise keep serving the old pixels for
    /// the same URI, so these (and later loads of them, see OnLoaded) skip it.
    /// </summary>
    private static void ReloadImages(DependencyObject? root, bool replaced = false)
    {
        if (root is null || ChangedAssets.Count == 0)
        {
            return;
        }
        BitmapImage? Fresh(ImageSource? source) =>
            source is BitmapImage { UriSource: { } uri } old && ChangedAssets.Any(a => uri.AbsolutePath.EndsWith("/" + a, StringComparison.OrdinalIgnoreCase))
            && (replaced || !old.CreateOptions.HasFlag(BitmapCreateOptions.IgnoreImageCache))
                ? new BitmapImage { CreateOptions = BitmapCreateOptions.IgnoreImageCache, DecodePixelWidth = old.DecodePixelWidth, DecodePixelHeight = old.DecodePixelHeight, UriSource = uri }
                : null;
        Brush? FreshBrush(Brush? brush) =>
            brush is ImageBrush { ImageSource: var source } imageBrush && Fresh(source) is { } image
                ? new ImageBrush { ImageSource = image, Stretch = imageBrush.Stretch, AlignmentX = imageBrush.AlignmentX, AlignmentY = imageBrush.AlignmentY, Opacity = imageBrush.Opacity }
                : null;
        var stack = new Stack<DependencyObject>([root]);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            try
            {
                switch (node)
                {
                    case Image image when Fresh(image.Source) is { } fresh: image.Source = fresh; break;
                    case Microsoft.UI.Xaml.Controls.BitmapIcon icon when icon.UriSource is { } uri && ChangedAssets.Any(a => uri.AbsolutePath.EndsWith("/" + a, StringComparison.OrdinalIgnoreCase)):
                        icon.UriSource = null;
                        icon.UriSource = uri;
                        break;
                }
                if (node is Microsoft.UI.Xaml.Controls.ImageIcon imageIcon && Fresh(imageIcon.Source) is { } iconImage) imageIcon.Source = iconImage;
                if (node is Border border && FreshBrush(border.Background) is { } borderBrush) border.Background = borderBrush;
                if (node is Panel panel && FreshBrush(panel.Background) is { } panelBrush) panel.Background = panelBrush;
                if (node is Control control && FreshBrush(control.Background) is { } controlBrush) control.Background = controlBrush;
                if (node is Microsoft.UI.Xaml.Shapes.Shape shape && FreshBrush(shape.Fill) is { } shapeBrush) shape.Fill = shapeBrush;
            }
            catch (Exception ex) when (ex is COMException or ArgumentException)
            {
                HotReloadAgent.Log($"reloading an image on {node.GetType().Name} failed: {ex.Message}");
            }
            for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--)
            {
                stack.Push(VisualTreeHelper.GetChild(node, i));
            }
        }
    }

    /// <summary>The evaluation of each live x:Bind function binding, by target element and property.</summary>
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, Action>> FunctionBindings = new();

    /// <summary>Evaluates every live x:Bind function binding again, after code it may call changed.</summary>
    private static void ReevaluateFunctions()
    {
        foreach (var (target, evaluations) in FunctionBindings.ToList())
        {
            foreach (var evaluate in evaluations.Values.ToList())
            {
                try
                {
                    evaluate();
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
                {
                    HotReloadAgent.Log($"re-evaluating an x:Bind function on {target.GetType().Name} failed: {ex.Message}");
                }
            }
        }
    }

    /// <summary>Evaluates a generated x:Bind function method and, for one-way bindings, again whenever an argument changes.</summary>
    private static void BindFunction(object? source, Type functions, DependencyObject target, DependencyProperty property, Type? propertyType,
        RootBinding spec, string function, object watchOwner)
    {
        var method = functions.GetMethod(function, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, [typeof(object)]);
        if (method is null || source is null)
        {
            return;
        }
        var evaluating = false;
        void Evaluate()
        {
            object? value;
            try
            {
                value = method.Invoke(null, [source]);
            }
            catch (TargetInvocationException)
            {
                // Like x:Bind: a null along the path leaves the target at its fallback.
                value = spec.FallbackValue;
            }
            if (value == DependencyProperty.UnsetValue)
            {
                return;
            }
            if (propertyType == typeof(string) && value is not null and not string)
            {
                value = value.ToString();
            }
            else if (value is string text && propertyType is not null && propertyType != typeof(string) && propertyType != typeof(object))
            {
                value = XamlBindingHelper.ConvertValue(propertyType, text);
            }
            if (value is null && propertyType is { IsValueType: true } && Nullable.GetUnderlyingType(propertyType) is null)
            {
                // As compiled x:Bind: a null along the path ((x:Double)Combo.SelectedValue before anything is selected)
                // leaves the property as it is; a value type can't hold null.
                return;
            }
            if (!TryCoerce(value, propertyType, out value))
            {
                BindingError(functions, spec, $"a {value?.GetType().Name} can't be converted to {propertyType?.Name}");
                return;
            }
            evaluating = true;
            try
            {
                target.SetValue(property, value);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidCastException or COMException)
            {
                BindingError(functions, spec, $"a {value?.GetType().Name ?? "null"} can't be set ({ex.Message})");
            }
            finally
            {
                evaluating = false;
            }
        }
        Evaluate();
        // A code change can change what the function returns (its body, or one it calls) without any argument changing.
        FunctionBindings.GetOrCreateValue(target)[property] = Evaluate;
        if (spec.BindBack is { } bindBack && functions.GetMethod(bindBack, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) is { } back)
        {
            // Two-way: changes the user makes go back through the BindBack method.
            var token = target.RegisterPropertyChangedCallback(property, (sender, dp) =>
            {
                if (!evaluating)
                {
                    try
                    {
                        back.Invoke(null, [source, sender.GetValue(dp)]);
                    }
                    catch (TargetInvocationException ex)
                    {
                        Report($"x:Bind BindBack {spec.Path}: {ex.GetBaseException().Message}");
                    }
                }
            });
            Watchers.GetOrCreateValue(watchOwner).Add(new Callback(() => target.UnregisterPropertyChangedCallback(property, token)));
        }
        if (!string.Equals(spec.Mode, "OneTime", StringComparison.OrdinalIgnoreCase) && spec.Watch.Count > 0)
        {
            Watchers.GetOrCreateValue(watchOwner).Add(new PathWatcher(source, spec.Watch, () =>
            {
                if (s_dispatcher is null || s_dispatcher.HasThreadAccess) Evaluate();
                else s_dispatcher.TryEnqueue(Evaluate);
            }));
        }
    }

    private sealed class Callback(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    /// <summary>Hook specs by id, from the latest markup of each component.</summary>
    private static readonly Dictionary<string, HookSpec> Hooks = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<string, Delegate>> TemplateHandlers = new();

    /// <summary>Wires an element created from a template: its x:Bind functions follow its DataContext, its events go to the owning component.</summary>
    private static void OnHook(DependencyObject element, string id)
    {
        if (!Hooks.TryGetValue(id, out var spec) || element is not FrameworkElement fe)
        {
            return;
        }
        var className = id[..id.LastIndexOf('#')];
        var functions = FindType(className);
        if (spec.Bindings.Count > 0 && functions is not null)
        {
            void Evaluate()
            {
                ResetWatchers(fe);
                foreach (var binding in spec.Bindings)
                {
                    ApplyBinding(fe.DataContext, functions, fe, binding, fe);
                }
            }
            fe.DataContextChanged += (_, _) => Evaluate();
        }
        if (spec.Events.Count > 0)
        {
            fe.Loaded += (_, _) =>
            {
                DependencyObject? node = fe;
                while (node is not null && node.GetType().FullName != className)
                {
                    node = VisualTreeHelper.GetParent(node);
                }
                if (node is null)
                {
                    return;
                }
                var wired = TemplateHandlers.GetOrCreateValue(fe);
                foreach (var wiring in spec.Events)
                {
                    var eventInfo = fe.GetType().GetEvent(wiring.Event);
                    // Event binding paths start at the item (DataContext); plain handlers live on the owning component.
                    var owner = wiring.Path is null ? node : ResolvePath(fe.DataContext, wiring.Path);
                    var handler = eventInfo?.EventHandlerType is { } handlerType ? MakeHandler(handlerType, owner, wiring.Handler) : null;
                    if (eventInfo is null || handler is null)
                    {
                        continue;
                    }
                    if (wired.Remove(wiring.Event, out var previous))
                    {
                        eventInfo.RemoveEventHandler(fe, previous);
                    }
                    eventInfo.AddEventHandler(fe, handler);
                    wired[wiring.Event] = handler;
                }
            };
        }
    }
    private static object? Resource(DependencyObject target, string markup)
    {
        var key = markup.Trim().TrimStart('{').TrimEnd('}').Trim();
        var space = key.IndexOf(' ');
        if (space < 0)
        {
            return null;
        }
        key = key[(space + 1)..].Trim();
        for (var node = target as FrameworkElement; node is not null; node = node.Parent as FrameworkElement)
        {
            if (node.Resources.TryGetValue(key, out var value))
            {
                return value;
            }
        }
        return Application.Current.Resources.TryGetValue(key, out var appValue) ? appValue : null;
    }

    internal static Type? FindType(string name) =>
        Type.GetType(name, throwOnError: false) ??
        AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, throwOnError: false)).FirstOrDefault(t => t is not null);
}

/// <summary>Shows a bound value as its text, as x:Bind does for text properties.</summary>
internal sealed partial class TextConverter : IValueConverter
{
    public static readonly TextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, string language) => value?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, string language) => value;
}
