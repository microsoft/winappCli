// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

// SPIKE (hot reload option A): the in-app half of the XAML side-car that runs next to 'dotnet watch'. dotnet watch
// applies every C# change (including the XAML compiler's generated C#); this agent hands WinUI the compiled .xbf and,
// once .NET reports the update landed (MetadataUpdateHandler), re-creates the pages that show the changed markup.

using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

[assembly: System.Reflection.Metadata.MetadataUpdateHandler(typeof(WinApp.DevTools.Managed.HotReload.XamlAgent))]

namespace WinApp.DevTools.Managed.HotReload;

internal static class XamlAgent
{
    public const string PipeVariable = "WINAPP_XAML_SIDECAR_PIPE";
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly string? LogPath = Environment.GetEnvironmentVariable("WINAPP_XAML_AGENT_LOG");

    private static StreamWriter? s_writer;
    private static readonly object s_writeLock = new();
    private static XamlHotReloadService? s_service;
    private static DispatcherQueue? s_dispatcher;
    private static Pending? s_pending;
    private static int s_started;
    private static readonly Dictionary<string, byte[]> s_lastGood = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> s_classUris = new(StringComparer.Ordinal);
    private static readonly HashSet<string> s_updatedTypes = new(StringComparer.Ordinal);

    private sealed class Pending
    {
        public int Id;
        public List<(string Uri, string Kind, string Class, byte[] Xbf)> Items = [];
        public bool Written;
        public long Generation;
        public long ExpectedUpdates = 1;
        public Timer? Timeout;
    }

    internal static void Log(string message)
    {
        if (LogPath is not null)
        {
            lock (LogPath)
            {
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        }
    }

    public static void Start()
    {
        if (Environment.GetEnvironmentVariable(PipeVariable) is not { Length: > 0 } pipe || Interlocked.Exchange(ref s_started, 1) != 0)
        {
            return;
        }
        _ = Task.Run(() => RunAsync(pipe));
    }

    // ---- .NET hot reload notifications (called by dotnet watch's agent after every applied update) ----

    public static void ClearCache(Type[]? types)
    {
    }

    public static void UpdateApplication(Type[]? types)
    {
        var names = types?.Select(t => t.FullName ?? t.Name).ToArray() ?? [];
        lock (s_updatedTypes)
        {
            s_updatedTypes.UnionWith(names.Where(n => !n.Contains('+') && !n.StartsWith('<')));
        }
        Log($"update applied: {string.Join(", ", names)}");
        Send(new JsonObject { ["op"] = "updated", ["types"] = new JsonArray([.. names.Select(n => (JsonNode?)n)]) });
        if (s_pending is { Written: true } pending)
        {
            // dotnet watch can apply a save's files as one update or several (seen: 2 files, 2 updates 260 ms apart). Rebuild
            // once as many updates landed as files changed, else after 300 ms without another.
            var generation = Interlocked.Increment(ref pending.Generation);
            var enough = generation >= pending.ExpectedUpdates;
            _ = Task.Delay(enough ? 30 : 300).ContinueWith(_ =>
            {
                if (Interlocked.Read(ref pending.Generation) == generation)
                {
                    RebuildPending(pending, enough ? $"update {generation} of {pending.ExpectedUpdates}" : $"quiet after {generation} of {pending.ExpectedUpdates} updates");
                }
            }, TaskScheduler.Default);
        }
    }

    // ---- connection to the side-car ----

    private static async Task RunAsync(string pipeName)
    {
        try
        {
            var unavailable = await InitializeAsync().ConfigureAwait(false);
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(30_000).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false));
            lock (s_writeLock)
            {
                s_writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            }
            Send(new JsonObject { ["op"] = "hello", ["pid"] = Environment.ProcessId, ["xaml"] = s_service is not null, ["unavailable"] = unavailable });
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                try
                {
                    Handle(JsonNode.Parse(line)!.AsObject());
                }
                catch (Exception ex)
                {
                    Log($"message failed: {ex}");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"side-car connection failed: {ex}");
        }
    }

    private static void Send(JsonObject message)
    {
        lock (s_writeLock)
        {
            try
            {
                s_writer?.WriteLine(message.ToJsonString());
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<string?> InitializeAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (FindDispatcher() is null)
        {
            if (DateTime.UtcNow > deadline)
            {
                return "no WinUI window was found in the app";
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
        var (service, unavailable) = await XamlHotReloadService.AcquireAsync().ConfigureAwait(false);
        s_service = service;
        return service is null ? unavailable ?? "this WinUI has no XAML hot reload service" : null;
    }

    private static void Handle(JsonObject message)
    {
        switch ((string?)message["op"])
        {
            case "ping":
                // Watchdog: answered from the UI thread, so a UI thread stuck in an update handler never answers.
                var ping = (long)message["id"]!;
                FindDispatcher()?.TryEnqueue(() => Send(new JsonObject { ["op"] = "pong", ["id"] = ping }));
                break;
            case "markup":
                var pending = new Pending { Id = (int)message["id"]! };
                foreach (var item in message["items"]!.AsArray())
                {
                    pending.Items.Add(((string)item!["uri"]!, (string)item["kind"]!, (string?)item["class"] ?? "", Convert.FromBase64String((string)item["xbf"]!)));
                }
                var expectUpdate = (bool)message["expectUpdate"]!;
                OnUiThread(() =>
                {
                    foreach (var (uri, kind, cls, xbf) in pending.Items)
                    {
                        var hr = s_service!.SetComponentMarkup(uri, binary: true, xbf);
                        Log($"markup {uri} ({xbf.Length} bytes): 0x{hr:X8}");
                        if (cls.Length > 0)
                        {
                            s_classUris[cls] = uri;
                        }
                    }
                    if (expectUpdate)
                    {
                        // The generated C# is about to change: x:Bind objects of the live instances stop now, while their code
                        // still matches their fields (the page is re-created after the update anyway).
                        StopTracking(pending.Items.Select(i => i.Class).Where(c => c.Length > 0).ToHashSet(StringComparer.Ordinal));
                    }
                });
                s_pending = pending;
                Send(new JsonObject { ["op"] = "markupSet", ["id"] = pending.Id });
                if (!expectUpdate)
                {
                    RebuildPending(pending, "no code change");
                }
                break;
            case "written":
                if (s_pending is { } written && written.Id == (int)message["id"]!)
                {
                    written.ExpectedUpdates = Math.Max(1, (long?)message["expectedUpdates"] ?? 1);
                    written.Written = true;
                    written.Timeout = new Timer(_ =>
                    {
                        if (ReferenceEquals(s_pending, written))
                        {
                            RebuildPending(written, "timeout", "the generated C# didn't reach the app within 20 s (dotnet watch didn't apply it; see its output above)");
                        }
                    }, null, 20_000, Timeout.Infinite);
                }
                break;
        }
    }

    private static void RebuildPending(Pending pending, string trigger, string? error = null)
    {
        if (!ReferenceEquals(Interlocked.CompareExchange(ref s_pending, null, pending), pending))
        {
            return;
        }
        pending.Timeout?.Dispose();
        var notes = new List<string>();
        var errors = new List<string>();
        if (error is not null)
        {
            errors.Add(error);
        }
        var clock = Stopwatch.StartNew();
        try
        {
            OnUiThread(() => Rebuild(pending, notes, errors));
        }
        catch (Exception ex)
        {
            errors.Add("the UI could not be rebuilt: " + ex.GetBaseException().Message);
        }
        Log($"rebuilt ({trigger}) in {clock.ElapsedMilliseconds} ms; notes {notes.Count}, errors {errors.Count}");
        Send(new JsonObject
        {
            ["op"] = "applied",
            ["id"] = pending.Id,
            ["trigger"] = trigger,
            ["rebuildMs"] = clock.ElapsedMilliseconds,
            ["notes"] = new JsonArray([.. notes.Select(n => (JsonNode?)n)]),
            ["errors"] = new JsonArray([.. errors.Select(e => (JsonNode?)e)]),
        });
    }

    // ---- rebuilding the owners ----

    private static void Rebuild(Pending pending, List<string> notes, List<string> errors)
    {
        ResetTypeInfo();
        // WinUI caches what it read about a type: types an update added or changed are read again (as hr-latest does).
        s_service?.InvalidateTypeCaches();
        string[] updated;
        lock (s_updatedTypes)
        {
            updated = [.. s_updatedTypes];
            s_updatedTypes.Clear();
        }
        if (updated.Length > 0 && s_service is { CanInvalidateTypes: true } service)
        {
            var hr = service.InvalidateTypes(updated, out var marked);
            Log($"invalidated {marked} of {updated.Length} type(s): 0x{hr:X8}");
        }
        var classes = pending.Items.Where(i => i.Class.Length > 0).Select(i => i.Class).ToHashSet(StringComparer.Ordinal);
        var everything = false;
        foreach (var (uri, kind, _, _) in pending.Items)
        {
            try
            {
                switch (kind)
                {
                    case "application":
                        var app = Application.Current;
                        Application.LoadComponent(app, new Uri(uri), Microsoft.UI.Xaml.Controls.Primitives.ComponentResourceLocation.Application);
                        everything = true;
                        break;
                    case "dictionary":
                        ReplaceDictionaries(Application.Current.Resources, new Uri(uri));
                        everything = true;
                        break;
                    case "generic":
                        everything = true;
                        break;
                }
            }
            catch (Exception ex) when (ex is COMException or XamlParseException or ArgumentException or InvalidOperationException)
            {
                errors.Add(Describe(ex, uri));
            }
        }

        foreach (var window in LiveWindows())
        {
            if (classes.Contains(window.GetType().FullName!))
            {
                ReloadWindow(window, notes, errors);
            }
            var targets = new List<(Frame Frame, Page Page)>();
            var loose = new List<FrameworkElement>();
            Collect(window.Content, classes, everything, targets, loose, insidePage: false);
            foreach (var (frame, page) in targets)
            {
                var state = UiState.Take(page.Content);
                var name = page.GetType().Name;
                if (Recreate(frame, page, errors) is { } fresh && !ReferenceEquals(fresh, page))
                {
                    UiState.RestoreWhenLoaded(fresh.Content as FrameworkElement, state, s_dispatcher);
                    notes.Add($"{name} re-created; its fields start over");
                }
            }
            foreach (var element in loose)
            {
                notes.Add($"{element.GetType().Name} isn't inside a page, so it isn't rebuilt: it takes effect when the app restarts");
            }
        }
        // Markup that failed to load is replaced by the last that loaded, so a later navigation doesn't fail on it.
        foreach (var (uri, kind, _, xbf) in pending.Items)
        {
            if (errors.Count == 0)
            {
                s_lastGood[uri] = xbf;
            }
            else if (s_lastGood.TryGetValue(uri, out var good))
            {
                s_service?.SetComponentMarkup(uri, binary: true, good);
            }
            else
            {
                s_service?.RemoveComponentMarkup(uri);
            }
        }
    }

    /// <summary>The top-most pages to re-create: their class changed, or they show a changed UserControl, or resources changed.</summary>
    private static void Collect(DependencyObject? node, HashSet<string> classes, bool everything, List<(Frame, Page)> targets, List<FrameworkElement> loose, bool insidePage)
    {
        if (node is null)
        {
            return;
        }
        if (node is Frame { Content: Page page } frame)
        {
            if (everything || classes.Contains(page.GetType().FullName!) || Shows(page.Content, classes))
            {
                targets.Add((frame, page));
                return;
            }
            Collect(page.Content, classes, everything, targets, loose, insidePage: true);
            return;
        }
        if (!insidePage && node is FrameworkElement element && classes.Contains(element.GetType().FullName!) && element is not Page)
        {
            loose.Add(element);
        }
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            Collect(VisualTreeHelper.GetChild(node, i), classes, everything, targets, loose, insidePage);
        }
    }

    /// <summary>Whether the tree (not counting pages in nested frames) has an element of one of the classes.</summary>
    private static bool Shows(DependencyObject? node, HashSet<string> classes)
    {
        if (node is null)
        {
            return false;
        }
        if (classes.Contains(node.GetType().FullName!))
        {
            return true;
        }
        if (node is Frame)
        {
            return false;
        }
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            if (Shows(VisualTreeHelper.GetChild(node, i), classes))
            {
                return true;
            }
        }
        return false;
    }

    private static Page? Recreate(Frame frame, Page page, List<string> errors)
    {
        try
        {
            page.NavigationCacheMode = NavigationCacheMode.Disabled;
        }
        catch (COMException)
        {
        }
        var type = page.GetType();
        if (MarkupError(page) is { } error)
        {
            // Not handed to the frame: a page that fails to build during navigation ends the app.
            errors.Add(Describe(error, s_classUris.GetValueOrDefault(type.FullName!), type.Name));
            return null;
        }
        NavigationFailedEventHandler handled = (_, e) => e.Handled = true;
        frame.NavigationFailed += handled;
        try
        {
            if (frame.CurrentSourcePageType != type)
            {
                var fresh = (Page)Activator.CreateInstance(type)!;
                frame.Content = fresh;
                return fresh;
            }
            var forward = frame.ForwardStack.ToList();
            if (!frame.Navigate(typeof(Page), null, new SuppressNavigationTransitionInfo()))
            {
                return null;
            }
            frame.GoBack(new SuppressNavigationTransitionInfo());
            frame.ForwardStack.Clear();
            foreach (var entry in forward)
            {
                frame.ForwardStack.Add(entry);
            }
            return frame.Content as Page;
        }
        catch (Exception ex)
        {
            if (frame.Content?.GetType() != type)
            {
                frame.Content = page;
            }
            errors.Add(Describe(ex, s_classUris.GetValueOrDefault(type.FullName!), type.Name));
            return null;
        }
        finally
        {
            frame.NavigationFailed -= handled;
        }
    }

    /// <summary>Why the page's current markup doesn't load, found by loading it into a plain Page (no app code runs).</summary>
    private static Exception? MarkupError(Page page)
    {
        if (!s_classUris.TryGetValue(page.GetType().FullName!, out var uri))
        {
            return null;
        }
        try
        {
            Application.LoadComponent(new Page(), new Uri(uri), Microsoft.UI.Xaml.Controls.Primitives.ComponentResourceLocation.Application);
            return null;
        }
        catch (XamlParseException ex)
        {
            return ex;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A Window's markup: InitializeComponent runs again on the same instance (no constructor replay; E4 decides the real design).</summary>
    private static void ReloadWindow(Window window, List<string> notes, List<string> errors)
    {
        var frames = Named<Frame>(window.Content).Where(f => f.Value.Content is Page).ToDictionary(f => f.Key, f => f.Value);
        var pages = frames.ToDictionary(f => f.Key, f => (Page)f.Value.Content);
        var state = UiState.Take(window.Content);
        var oldContent = window.Content;
        try
        {
            if (window.GetType().GetField("_contentLoaded", InstanceMembers) is { } loaded)
            {
                loaded.SetValue(window, false);
            }
            window.GetType().GetMethod("InitializeComponent", InstanceMembers, Type.EmptyTypes)?.Invoke(window, null);
            foreach (var (name, frame) in Named<Frame>(window.Content))
            {
                if (pages.TryGetValue(name, out var page) && frame.Content is null)
                {
                    frames[name].Content = null;
                    frame.Content = page;
                }
            }
            UiState.RestoreWhenLoaded(window.Content as FrameworkElement, state, s_dispatcher);
            notes.Add($"{window.GetType().Name} reloaded in place (its markup only; its constructor didn't run again)");
        }
        catch (Exception ex)
        {
            window.Content = oldContent;
            errors.Add(Describe(ex, s_classUris.GetValueOrDefault(window.GetType().FullName!), window.GetType().Name));
        }
    }

    private static Dictionary<string, T> Named<T>(UIElement? root) where T : FrameworkElement
    {
        var found = new Dictionary<string, T>(StringComparer.Ordinal);
        void Walk(DependencyObject node)
        {
            if (node is T element && !string.IsNullOrEmpty(element.Name))
            {
                found.TryAdd(element.Name, element);
            }
            if (node is Frame)
            {
                return;
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                Walk(VisualTreeHelper.GetChild(node, i));
            }
        }
        if (root is not null)
        {
            Walk(root);
        }
        return found;
    }

    private static void ReplaceDictionaries(ResourceDictionary dictionary, Uri uri)
    {
        var merged = dictionary.MergedDictionaries;
        for (var i = 0; i < merged.Count; i++)
        {
            if (merged[i].Source is { } source && string.Equals(source.AbsolutePath, uri.AbsolutePath, StringComparison.OrdinalIgnoreCase))
            {
                merged[i] = new ResourceDictionary { Source = uri };
            }
            else
            {
                ReplaceDictionaries(merged[i], uri);
            }
        }
    }

    /// <summary>Stops the compiled x:Bind of live instances of these classes before their generated code changes.</summary>
    private static void StopTracking(HashSet<string> classes)
    {
        void Walk(DependencyObject? node)
        {
            if (node is null)
            {
                return;
            }
            if (classes.Contains(node.GetType().FullName!) && node.GetType().GetField("Bindings", InstanceMembers) is { } field &&
                field.GetValue(node) is { } bindings)
            {
                try
                {
                    bindings.GetType().GetMethod("StopTracking")?.Invoke(bindings, null);
                    field.SetValue(node, null);
                }
                catch (Exception ex)
                {
                    Log($"{node.GetType().Name}: stopping x:Bind failed: {ex.GetBaseException().Message}");
                }
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                Walk(VisualTreeHelper.GetChild(node, i));
            }
        }
        foreach (var window in LiveWindows())
        {
            Walk(window.Content);
        }
    }

    /// <summary>The generated XamlTypeInfo provider builds its type tables once: a new one sees types an update added.</summary>
    private static void ResetTypeInfo()
    {
        if (Application.Current is { } app && app.GetType().GetField("__appProvider", InstanceMembers) is { } field)
        {
            field.SetValue(app, null);
        }
    }

    private static string Describe(Exception ex, string? uri, string? component = null)
    {
        var message = ex.Message.Split('\n')[0].Trim();
        var where = uri is null ? component ?? "" : uri.Replace("ms-appx:///", "", StringComparison.OrdinalIgnoreCase);
        // XamlParseException: "... [Line: 12 Position: 34]"
        var match = System.Text.RegularExpressions.Regex.Match(ex.Message, @"\[Line: (\d+) Position: (\d+)\]");
        if (match.Success)
        {
            message = message.Replace(match.Value, "").Trim();
            where += $"({match.Groups[1].Value},{match.Groups[2].Value})";
        }
        return $"{where}: {message}";
    }

    // ---- the UI thread and the app's windows ----

    private static void OnUiThread(Action action)
    {
        var dispatcher = FindDispatcher() ?? throw new InvalidOperationException("The app's UI thread is not available.");
        if (dispatcher.HasThreadAccess)
        {
            action();
            return;
        }
        using var done = new ManualResetEventSlim();
        Exception? failure = null;
        if (!dispatcher.TryEnqueue(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { done.Set(); }
        }))
        {
            throw new InvalidOperationException("The app's UI thread is shutting down.");
        }
        done.Wait();
        if (failure is not null)
        {
            throw failure;
        }
    }

    private static DispatcherQueue? FindDispatcher()
    {
        if (s_dispatcher is null && LiveWindows().FirstOrDefault() is { } window)
        {
            s_dispatcher = window.DispatcherQueue;
        }
        return s_dispatcher;
    }

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
            foreach (var property in types.SelectMany(t => t.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                .Where(p => typeof(Window).IsAssignableFrom(p.PropertyType) && p.GetIndexParameters().Length == 0 && !p.DeclaringType!.ContainsGenericParameters))
            {
                try
                {
                    Add(property.GetValue(null));
                }
                catch (Exception)
                {
                }
            }
        }
        return windows;
    }
}
