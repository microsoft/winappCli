// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using WinApp.HotReload.Protocol;

namespace WinApp.DevTools.Managed.HotReload;

/// <summary>
/// Rebuilds the owner of what a save changed, as a restart would build it: a page is opened again in its Frame (same
/// parameter, same back and forward stacks), a UserControl rebuilds the pages showing it, and App.xaml or a dictionary
/// rebuilds the resources and then every page on screen. The Frame builds the new page before it lets go of the old
/// one, so markup that fails to load leaves the screen as it was (experiment E8). What the user typed, selected and
/// scrolled is put back (<see cref="UiState"/>); the page's and its UserControls' fields start over, and the note says which.
/// </summary>
internal static class Rebuild
{
    private static readonly HashSet<Type> s_landed = [];
    /// <summary>When each component type last changed, and when each live page or UserControl was built (save counts).</summary>
    private static readonly Dictionary<Type, int> s_changedAt = [];
    private static readonly ConditionalWeakTable<object, StrongBox<int>> s_builtAt = new();
    private static readonly ConditionalWeakTable<Frame, object> s_watched = new();
    private static readonly List<WeakReference<Window>> s_windows = [];
    private static int s_generation;
    private static DispatcherQueue? s_dispatcher;

    public static DispatcherQueue? Dispatcher => s_dispatcher ??= LiveWindows().FirstOrDefault()?.DispatcherQueue;

    /// <summary>Types a C# update changed (from <see cref="UpdateLanded"/>): the next reload rebuilds what they own.</summary>
    public static void Landed(Type[] types)
    {
        lock (s_landed)
        {
            s_landed.UnionWith(types);
        }
    }

    /// <summary>Once the hot reload service is there: what's on screen gives its starting values, and components loading from now on are followed.</summary>
    public static async Task StartAsync(XamlService service)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Dispatcher is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100).ConfigureAwait(false);
        }
        Dispatcher?.TryEnqueue(() =>
        {
            service.AdviseComponentLoaded(ComponentLoaded);
            foreach (var window in LiveWindows())
            {
                UiState.CaptureBaseline(window.Content);
                Watch(window.Content);
            }
        });
    }

    private static void ComponentLoaded(object component)
    {
        s_builtAt.AddOrUpdate(component, new StrongBox<int>(s_generation));
        if (component is Window window)
        {
            lock (s_windows)
            {
                s_windows.Add(new WeakReference<Window>(window));
            }
        }
        else if (component is FrameworkElement element)
        {
            void OnLoaded(object sender, RoutedEventArgs e)
            {
                element.Loaded -= OnLoaded;
                UiState.CaptureBaseline(element);
                Watch(element);
            }
            element.Loaded += OnLoaded;
        }
    }

    /// <summary>On the UI thread: hands WinUI the save's markup, then rebuilds the owners of what changed.</summary>
    public static async Task<HotReloadResponse> RunAsync(XamlService service, List<MarkupPayload> markup)
    {
        List<Type> landed;
        lock (s_landed)
        {
            landed = [.. s_landed];
            s_landed.Clear();
        }
        s_generation++;
        if (landed.Count > 0)
        {
            // Markup that uses a member an update just added finds it.
            _ = service.InvalidateTypeCaches();
        }
        var changed = landed.Select(Outermost).Where(IsComponent).ToHashSet();
        foreach (var payload in markup.Where(m => m.Kind == "component"))
        {
            if (FindType(payload.ClassName) is { } type)
            {
                changed.Add(type);
            }
        }
        var resources = markup.Any(m => m.Kind != "component");
        var (pages, restart) = Owners(changed, resources);
        if (restart is not null)
        {
            return new HotReloadResponse { Ok = true, Restart = restart };
        }
        foreach (var payload in markup)
        {
            Marshal.ThrowExceptionForHR(service.SetComponentXbf(payload.Uri, payload.Xbf));
        }
        var notes = new List<string>();
        MarkupPayload? loading = null;
        // Taken before anything changes: new resources re-template controls, and a page that fails to load is built
        // again from these.
        var states = pages.Select(p => UiState.Take(p.Page)).ToList();
        try
        {
            foreach (var payload in markup.Where(m => m.Kind is "application" or "dictionary"))
            {
                loading = payload;
                LoadResources(payload);
            }
            for (var i = 0; i < pages.Count; i++)
            {
                var (frame, page, shows) = pages[i];
                loading = markup.FirstOrDefault(m => m.ClassName == page.GetType().FullName) ?? markup.FirstOrDefault(m => m.Kind == "component");
                if (await RebuildPageAsync(frame, page, states[i], shows) is { } note)
                {
                    notes.Add(note);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Back to the markup that loaded: the next time the app loads it (another navigation), it still works.
            foreach (var payload in markup)
            {
                _ = service.SetComponentXbf(payload.Uri, payload.Previous);
            }
            // A page that failed after it replaced the old one (markup that fails as the page lays out, in a template):
            // built again from that markup, with what the user had.
            for (var i = 0; i < pages.Count; i++)
            {
                if (!ReferenceEquals(pages[i].Frame.Content, pages[i].Page) && pages[i].Frame.Content is Page shown)
                {
                    try
                    {
                        await RebuildPageAsync(pages[i].Frame, shown, states[i]);
                    }
                    catch (Exception again) when (again is not OutOfMemoryException)
                    {
                        System.Diagnostics.Trace.TraceWarning("Hot reload: {0} couldn't be built again: {1}", shown.GetType().Name, again.Message);
                    }
                }
            }
            return ex is XamlParseException
                ? new HotReloadResponse { Ok = true, Error = LoadError(ex), ErrorUri = loading?.Uri }
                : new HotReloadResponse { Ok = true, Error = $"{ex.GetType().Name}: {ex.Message}" };
        }
        foreach (var type in changed)
        {
            s_changedAt[type] = s_generation;
        }
        // A changed page or UserControl that nothing on screen shows: built with the change when the app opens it.
        notes.AddRange(markup.Where(m => m.Kind == "component" && FindType(m.ClassName) is { } type && !pages.Any(p => p.Page.GetType() == type || p.Shows == type.Name))
            .Select(m => $"{m.ClassName![(m.ClassName!.LastIndexOf('.') + 1)..]} isn't on screen; it has the change when the app shows it"));
        if (resources)
        {
            notes.AddRange(LiveWindows().Where(w => w.Content is not (Frame or Page)).Select(w =>
                $"{w.GetType().Name}'s own markup (outside its pages) keeps the resources it loaded with until the app restarts"));
        }
        foreach (var window in LiveWindows())
        {
            Watch(window.Content);
        }
        return new HotReloadResponse { Ok = true, Notes = notes };
    }

    /// <summary>
    /// The pages to open again: each one on screen whose type changed, or that shows a changed UserControl, or every
    /// one after a resource change; only the outermost when pages are nested in each other's frames. A changed
    /// component outside a page in a Frame (directly in a window) can't be rebuilt: that's the restart reason.
    /// </summary>
    private static (List<(Frame Frame, Page Page, string? Shows)> Pages, string? Restart) Owners(HashSet<Type> changed, bool resources)
    {
        var marked = new List<(Frame Frame, Page Page, string? Shows)>();
        var parents = new Dictionary<Page, Page?>(ReferenceEqualityComparer.Instance);
        string? restart = null;
        void Visit(DependencyObject? node, Page? owner)
        {
            if (node is null)
            {
                return;
            }
            if (node is Page { Frame: { } frame } page && ReferenceEquals(frame.Content, page))
            {
                parents[page] = owner;
                if (changed.Contains(page.GetType()) || (resources && owner is null))
                {
                    marked.Add((frame, page, null));
                }
                owner = page;
            }
            else if (changed.Contains(node.GetType()))
            {
                if (owner is null)
                {
                    restart ??= $"{node.GetType().Name} is shown outside a page in a Frame, so winapp can't rebuild it while the app runs; " +
                        "it takes effect when the app restarts (put it in a Page to keep edits live)";
                }
                else if (!marked.Any(m => ReferenceEquals(m.Page, owner)))
                {
                    marked.Add((owner.Frame, owner, node.GetType().Name));
                }
            }
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                Visit(VisualTreeHelper.GetChild(node, i), owner);
            }
        }
        foreach (var window in LiveWindows())
        {
            Visit(window.Content, null);
        }
        bool InsideMarked(Page page)
        {
            for (var parent = parents.GetValueOrDefault(page); parent is not null; parent = parents.GetValueOrDefault(parent))
            {
                if (marked.Any(m => ReferenceEquals(m.Page, parent)))
                {
                    return true;
                }
            }
            return false;
        }
        var pages = marked.Where(m => !InsideMarked(m.Page)).ToList();
        foreach (var (frame, page, _) in pages)
        {
            if (NavigationState(frame) is null && !s_parameters.TryGetValue(page, out _))
            {
                restart ??= $"{page.GetType().Name} was opened with a navigation parameter winapp can't read back, so it can't be opened again " +
                    "while the app runs; it takes effect when the app restarts";
            }
        }
        return (pages, restart);
    }

    /// <summary>
    /// Opens the page again in its Frame. The Frame's own navigation state (when every parameter in it can be saved)
    /// brings back the same parameter and stacks; otherwise the parameter seen when the page was opened. Returns the note.
    /// </summary>
    /// <param name="state">What the user had on the page, when taken before (else now).</param>
    /// <param name="shows">The changed UserControl the page is rebuilt for, if that's why.</param>
    private static async Task<string?> RebuildPageAsync(Frame frame, Page page, UiState.Snapshot? state = null, string? shows = null)
    {
        var name = page.GetType().Name;
        state ??= UiState.Take(page);
        var fields = Fields.Take(page);
        var nested = Descendants(page).OfType<Frame>().Where(f => f.Name.Length > 0 && ReferenceEquals(page.FindName(f.Name), f))
            .Select(f => (f.Name, State: NavigationState(f))).Where(f => f.State is not null).ToList();
        NavigationFailedEventHandler reported = (_, e) => e.Handled = true;
        frame.NavigationFailed += reported;
        // Markup that fails only as the new page lays out (a value in a template) is this save's error, not the app's.
        XamlParseException? late = null;
        Microsoft.UI.Xaml.UnhandledExceptionEventHandler caught = (_, e) =>
        {
            if (late is null && e.Exception is XamlParseException parse)
            {
                late = parse;
                e.Handled = true;
            }
        };
        Application.Current.UnhandledException += caught;
        try
        {
            // A page the Frame keeps in its cache would be handed back as it is.
            var cacheMode = page.NavigationCacheMode;
            page.NavigationCacheMode = NavigationCacheMode.Disabled;
            try
            {
                if (NavigationState(frame) is { } navigation)
                {
                    frame.SetNavigationState(navigation);
                }
                else
                {
                    var forward = frame.ForwardStack.ToList();
                    frame.Navigate(page.GetType(), s_parameters.TryGetValue(page, out var parameter) ? parameter.Value : null, new SuppressNavigationTransitionInfo());
                    frame.BackStack.RemoveAt(frame.BackStack.Count - 1);
                    foreach (var entry in forward)
                    {
                        frame.ForwardStack.Add(entry);
                    }
                }
            }
            finally
            {
                frame.NavigationFailed -= reported;
                if (ReferenceEquals(frame.Content, page))
                {
                    page.NavigationCacheMode = cacheMode;
                }
            }
            if (frame.Content is not Page fresh || ReferenceEquals(fresh, page))
            {
                return $"{name} couldn't be opened again in its Frame; it keeps what it showed until the app restarts";
            }
            LoadTemplates(fresh);
            await LoadedAsync(fresh);
            foreach (var (frameName, navigation) in nested)
            {
                if (fresh.FindName(frameName) is Frame inner && NavigationState(inner) != navigation)
                {
                    inner.SetNavigationState(navigation);
                    await LoadedAsync(inner.Content as FrameworkElement);
                }
            }
            // Let what restoring raised run (a selection's handler) before the fields are compared. With nothing restored,
            // nothing was raised; the wait is for the app to go idle, which a big page can take most of a second to do.
            if (UiState.Restore(fresh, state))
            {
                await YieldAsync(fresh.DispatcherQueue);
            }
            var lost = Fields.Changed(fields, Fields.Take(fresh));
            if (late is not null)
            {
                throw late;
            }
            var why = shows is null ? "" : $" for the new {shows}";
            return lost.Count == 0 ? $"{name} re-created{why}" : $"{name} re-created{why}; its fields start over ({string.Join(", ", lost)})";
        }
        finally
        {
            Application.Current.UnhandledException -= caught;
        }
    }

    /// <summary>App.xaml again into the application, or a merged dictionary again where it's merged; built before anything is replaced.</summary>
    private static void LoadResources(MarkupPayload payload)
    {
        var app = Application.Current;
        var uri = new Uri(payload.Uri);
        if (payload.Kind == "application")
        {
            var old = app.Resources;
            Application.LoadComponent(app, uri, Microsoft.UI.Xaml.Controls.Primitives.ComponentResourceLocation.Application);
            if (!ReferenceEquals(old, app.Resources))
            {
                // Resources the app's code added (in OnLaunched, say) stay; keys the markup dropped go.
                foreach (var (key, value) in old.Where(p => !app.Resources.ContainsKey(p.Key) && !(p.Key is string k && payload.DroppedKeys.Contains(k))).ToList())
                {
                    app.Resources[key] = value;
                }
            }
            return;
        }
        void Replace(ResourceDictionary dictionary)
        {
            var merged = dictionary.MergedDictionaries;
            for (var i = 0; i < merged.Count; i++)
            {
                if (merged[i].Source is { } source && string.Equals(source.AbsolutePath.TrimStart('/'), uri.AbsolutePath.TrimStart('/'), StringComparison.OrdinalIgnoreCase))
                {
                    merged[i] = new ResourceDictionary { Source = uri };
                }
                else
                {
                    Replace(merged[i]);
                }
            }
        }
        Replace(app.Resources);
    }

    /// <summary>WinUI's message for markup that doesn't load: the part that names the value and its line.</summary>
    private static string LoadError(Exception ex) =>
        ex.Message.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0 && !l.StartsWith("The text associated with this error code", StringComparison.Ordinal))
            ?? ex.GetType().Name;

    private static string? NavigationState(Frame frame)
    {
        try
        {
            return frame.GetNavigationState();
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            // A parameter that isn't a string, number, character or GUID.
            return null;
        }
    }

    /// <summary>The parameter each page was opened with, from the frames watched (for frames whose state can't be saved).</summary>
    private static readonly ConditionalWeakTable<Page, StrongBox<object?>> s_parameters = new();

    /// <summary>
    /// Follows navigations in the frames under <paramref name="root"/>: the parameter each page gets, and a page the
    /// Frame kept in its cache from before a save is opened again when it's shown (as a restart would build it).
    /// </summary>
    private static void Watch(DependencyObject? root)
    {
        foreach (var frame in Descendants(root).OfType<Frame>())
        {
            if (s_watched.TryGetValue(frame, out _))
            {
                continue;
            }
            s_watched.Add(frame, new object());
            frame.Navigated += (_, e) =>
            {
                if (e.Content is not Page page)
                {
                    return;
                }
                s_parameters.AddOrUpdate(page, new StrongBox<object?>(e.Parameter));
                void OnLoaded(object sender, RoutedEventArgs args)
                {
                    page.Loaded -= OnLoaded;
                    Watch(page);
                    if (ReferenceEquals(frame.Content, page) && IsStale(page))
                    {
                        page.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, async () =>
                        {
                            try
                            {
                                await RebuildPageAsync(frame, page);
                            }
                            catch (Exception ex) when (ex is XamlParseException or COMException or ArgumentException)
                            {
                                System.Diagnostics.Trace.TraceWarning("Hot reload: {0} kept from before a save couldn't be opened again: {1}", page.GetType().Name, ex.Message);
                            }
                        });
                    }
                }
                page.Loaded += OnLoaded;
            };
        }
    }

    /// <summary>Whether the page, or a component in it, was built before its type last changed.</summary>
    private static bool IsStale(Page page) =>
        Descendants(page).Prepend(page).Any(node => s_changedAt.TryGetValue(node.GetType(), out var changed) &&
            (s_builtAt.TryGetValue(node, out var built) ? built.Value : 0) < changed);

    private static Task LoadedAsync(FrameworkElement? element)
    {
        if (element is null || element.IsLoaded)
        {
            return Task.CompletedTask;
        }
        var loaded = new TaskCompletionSource();
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            element.Loaded -= OnLoaded;
            loaded.TrySetResult();
        }
        element.Loaded += OnLoaded;
        return Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromSeconds(2)));
    }

    private static Task YieldAsync(DispatcherQueue queue)
    {
        var done = new TaskCompletionSource();
        if (!queue.TryEnqueue(DispatcherQueuePriority.Low, () => done.SetResult()))
        {
            done.SetResult();
        }
        return done.Task;
    }

    /// <summary>
    /// Loads each data template the new page uses once, before it lays out. A value only the runtime rejects, in a
    /// template, otherwise fails as the list lays out its items, where WinUI ends the app; here it throws, and the
    /// page is built again from the markup that loaded.
    /// </summary>
    private static void LoadTemplates(Page page)
    {
        var templates = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var node in Descendants(page).Prepend(page))
        {
            if (node is FrameworkElement element)
            {
                templates.UnionWith(element.Resources.Values.OfType<DataTemplate>());
            }
            if ((node switch { ItemsControl items => items.ItemTemplate, ContentControl content => content.ContentTemplate, ContentPresenter presenter => presenter.ContentTemplate, _ => null }) is { } template)
            {
                templates.Add(template);
            }
        }
        foreach (var template in templates.OfType<DataTemplate>())
        {
            template.LoadContent();
        }
    }

    internal static IEnumerable<DependencyObject> Descendants(DependencyObject? root)
    {
        if (root is null)
        {
            yield break;
        }
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!ReferenceEquals(node, root))
            {
                yield return node;
            }
            for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--)
            {
                stack.Push(VisualTreeHelper.GetChild(node, i));
            }
        }
    }

    /// <summary>A page or UserControl: what has markup of its own and can be rebuilt.</summary>
    private static bool IsComponent(Type type) => typeof(Page).IsAssignableFrom(type) || typeof(UserControl).IsAssignableFrom(type);

    /// <summary>The type a nested type (a lambda's closure, x:Bind's generated classes) belongs to.</summary>
    private static Type Outermost(Type type)
    {
        while (type.DeclaringType is { } outer)
        {
            type = outer;
        }
        return type;
    }

    private static Type? FindType(string? fullName) => fullName is null ? null
        : AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).Select(a => a.GetType(fullName)).FirstOrDefault(t => t is not null);

    /// <summary>The app's windows: those seen loading, and those its Application object or static fields hold.</summary>
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
        lock (s_windows)
        {
            foreach (var weak in s_windows)
            {
                Add(weak.TryGetTarget(out var window) ? window : null);
            }
        }
        try
        {
            if (Application.Current is { } app)
            {
                for (var type = app.GetType(); type is not null && type != typeof(Application); type = type.BaseType)
                {
                    foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                        .Where(f => typeof(Window).IsAssignableFrom(f.FieldType)))
                    {
                        Add(field.GetValue(field.IsStatic ? null : app));
                    }
                }
            }
        }
        catch (COMException)
        {
            // The application object isn't created yet.
        }
        return windows;
    }

    /// <summary>The fields of a page and of its named UserControls, to say which started over.</summary>
    private static class Fields
    {
        public static Dictionary<string, string> Take(Page page)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            Add(values, page, "");
            foreach (var control in Descendants(page).OfType<UserControl>().Where(c => c.Name.Length > 0 && ReferenceEquals(page.FindName(c.Name), c)))
            {
                Add(values, control, $"{control.Name}.");
            }
            return values;
        }

        public static List<string> Changed(Dictionary<string, string> before, Dictionary<string, string> after) =>
            [.. before.Where(p => after.TryGetValue(p.Key, out var now) && now != p.Value).Select(p => p.Key)];

        private static void Add(Dictionary<string, string> values, object instance, string prefix)
        {
            foreach (var field in OwnFields(instance.GetType()))
            {
                values[prefix + DisplayName(field)] = Describe(field.GetValue(instance), 0);
            }
        }

        /// <summary>Fields the app's code declares: not the markup's named elements, compiled bindings or WinUI's own.</summary>
        private static IEnumerable<FieldInfo> OwnFields(Type type)
        {
            for (; type is not null && IsAppType(type); type = type.BaseType!)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!typeof(DependencyObject).IsAssignableFrom(field.FieldType) && !typeof(Delegate).IsAssignableFrom(field.FieldType) &&
                        field.GetCustomAttribute<System.CodeDom.Compiler.GeneratedCodeAttribute>() is null && !field.FieldType.Name.EndsWith("_Bindings", StringComparison.Ordinal))
                    {
                        yield return field;
                    }
                }
            }
        }

        private static string DisplayName(FieldInfo field) =>
            field.Name.StartsWith('<') && field.Name.IndexOf('>') is > 1 and var end ? field.Name[1..end] : field.Name;

        /// <summary>A comparable text for a value: plain values as they are, collections by count, the app's own objects one level deep.</summary>
        private static string Describe(object? value, int depth) => value switch
        {
            null => "null",
            string or Enum or decimal or DateTime or TimeSpan or Guid => value.ToString()!,
            _ when value.GetType().IsPrimitive => value.ToString()!,
            System.Collections.ICollection collection => $"count {collection.Count}",
            _ when depth == 0 && IsAppType(value.GetType()) =>
                "{" + string.Join(";", OwnFields(value.GetType()).Select(f => Describe(f.GetValue(value), depth + 1))) + "}",
            _ => value.GetType().FullName ?? "",
        };

        private static bool IsAppType(Type type) =>
            type.Assembly.GetName().Name is { } name && !name.StartsWith("Microsoft.", StringComparison.Ordinal) &&
            !name.StartsWith("System.", StringComparison.Ordinal) && name is not ("System" or "mscorlib" or "WinRT.Runtime");
    }
}
