// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;

namespace WinApp.DevTools.Managed.HotReload;

/// <summary>
/// WINAPP_HOT_RELOAD_PAGE_UPDATE=shadow: the page is reloaded in place (its state is kept), then a fresh copy is
/// built the way a restart would build it (same type and navigation parameter, in a Frame of its own inside an
/// invisible popup on the same XamlRoot, so it loads and its bindings run) and compared with the live page. A value
/// the live page shows that the fresh copy doesn't, while the page holds the same data, is reported as a note with the
/// likely cause:
///   note: CaseLate shows 'loaded' here but 'ctor' after a restart: Late changes after Loaded without a change notification (BindPage.xaml.cs:16)
/// Differences that come from state the reload kept (the page holds other data, the user typed or selected something)
/// are what in-place reload is for and aren't reported.
/// </summary>
[PageUpdateStrategy("shadow")]
internal sealed class PageUpdateShadow : IPageUpdateStrategy
{
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(5);

    public void Update(PageUpdate update)
    {
        var page = update.Page;
        PageNavigation.Watch(page.XamlRoot?.Content);
        update.ReloadInPlace();
        // The copy is navigated as the live page was: with its parameter when it's known (or the page can't read it),
        // else by restoring the frame's serialized history (the same parameter and back stack).
        Func<Frame, bool> navigate;
        if (PageNavigation.TryGetParameter(page, out var parameter))
        {
            navigate = host => host.Navigate(page.GetType(), parameter, new SuppressNavigationTransitionInfo());
        }
        else if (update.Frame is { } frame && PageNavigation.TryGetState(frame, page, out var state))
        {
            navigate = host =>
            {
                host.SetNavigationState(state);
                return true;
            };
        }
        else
        {
            update.Note($"{page.GetType().Name}: not compared with a fresh copy: it was shown before hot reload watched its Frame, and its navigation parameter can't be read back");
            return;
        }
        update.WhenSettled(() => CompareAsync(update, page, navigate));
    }

    private static async Task CompareAsync(PageUpdate update, Page page, Func<Frame, bool> navigate)
    {
        var clock = Stopwatch.StartNew();
        var name = page.GetType().Name;
        if (page.XamlRoot is not { } xamlRoot || page.DispatcherQueue is not { } dispatcher)
        {
            update.Note($"{name}: not compared with a fresh copy: the page isn't shown");
            return;
        }
        // The live page's own x:Bind pass after its reload runs at Low priority: let it finish first.
        await UiTicks.Idle(dispatcher);
        var focused = FocusManager.GetFocusedElement(xamlRoot) as UIElement;
        var host = new Frame
        {
            Width = page.ActualWidth > 0 ? page.ActualWidth : xamlRoot.Size.Width,
            Height = page.ActualHeight > 0 ? page.ActualHeight : xamlRoot.Size.Height,
            IsTabStop = false,
        };
        var (open, close) = Host(xamlRoot, host);
        Page? shadow = null;
        var loadedAt = 0L;
        // What the copy shows as constructed (markup and constructor), before the code that runs as it's navigated to
        // and loads: a live value equal to it while a restart shows another was reset by reloading in place.
        Dictionary<string, (DependencyObject Element, Dictionary<string, string> Values)>? constructed = null;
        void OnNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            if (e.Content is Page { } navigated && constructed is null)
            {
                constructed = AuthoredTree.Read(navigated);
            }
        }
        host.Navigated += OnNavigated;
        try
        {
            open();
            if (!navigate(host) || host.Content is not Page created || created.GetType() != page.GetType())
            {
                update.Note($"{name}: not compared with a fresh copy: navigating a new instance failed");
                return;
            }
            shadow = created;
            var early = PageData.Take(shadow, getters: true);
            if (!await UiTicks.Loaded(shadow, LoadTimeout))
            {
                update.Note($"{name}: not compared with a fresh copy: it didn't load within {LoadTimeout.TotalSeconds:0} s");
                return;
            }
            loadedAt = clock.ElapsedMilliseconds;
            var (live, fresh) = await SettledAsync(page, shadow, dispatcher);
            var liveData = PageData.Take(page, getters: true);
            var freshData = PageData.Take(shadow, getters: true);
            HotReloadAgent.Log($"shadow: as constructed: {string.Join("; ", (constructed ?? []).Select(e => e.Key + " " + string.Join(",", e.Value.Values.Where(v => v.Key is "Text" or "Items").Select(v => v.Key + "=" + v.Value))))}");
            foreach (var note in Compare(page, live, fresh, liveData, freshData, early, constructed))
            {
                update.Note(note);
            }
        }
        catch (Exception ex)
        {
            update.Note($"{name}: comparing with a fresh copy failed: {ex.GetBaseException().Message}");
        }
        finally
        {
            if (shadow is not null)
            {
                // Navigated away first, so the copy's OnNavigatedFrom runs and the page can let go of what it subscribed to.
                try
                {
                    host.Navigate(typeof(Page), null, new SuppressNavigationTransitionInfo());
                }
                catch (Exception ex)
                {
                    HotReloadAgent.Log($"shadow: leaving the copy of {name} failed: {ex.Message}");
                }
            }
            host.Content = null;
            close();
            // A copy that took focus as it loaded gives it back.
            var focusMoved = focused is not null && !ReferenceEquals(FocusManager.GetFocusedElement(xamlRoot), focused);
            if (focusMoved)
            {
                focused!.Focus(FocusState.Programmatic);
            }
            HotReloadAgent.Log($"shadow: compared {name} with a fresh copy in {clock.ElapsedMilliseconds} ms (host {HostKind}, loaded after {loadedAt} ms, focus moved: {focusMoved})");
        }
    }

    /// <summary>
    /// Where the fresh copy loads. "popup" (default): an invisible, hit-test-invisible popup on the live page's XamlRoot,
    /// so it sees the same window size, theme and scale and the app's tree is untouched. "window" (experiment,
    /// WINAPP_HOT_RELOAD_SHADOW_HOST=window): a window of its own the size of the live one, shown off-screen without
    /// being activated and left out of the task switcher.
    /// </summary>
    private static readonly string HostKind = Environment.GetEnvironmentVariable("WINAPP_HOT_RELOAD_SHADOW_HOST") is { Length: > 0 } kind ? kind : "popup";

    private static (Action Open, Action Close) Host(XamlRoot xamlRoot, FrameworkElement host)
    {
        if (HostKind == "window")
        {
            var window = new Window { Content = host };
            var scale = xamlRoot.RasterizationScale;
            return (() =>
            {
                var app = window.AppWindow;
                app.IsShownInSwitchers = false;
                app.MoveAndResize(new Windows.Graphics.RectInt32(-32000, -32000, (int)(xamlRoot.Size.Width * scale), (int)(xamlRoot.Size.Height * scale)));
                app.Show(activateWindow: false);
            }, () =>
            {
                window.Content = null;
                window.Close();
            });
        }
        var popup = new Popup { XamlRoot = xamlRoot, Child = host, Opacity = 0, IsHitTestVisible = false, ShouldConstrainToRootBounds = true };
        return (() => popup.IsOpen = true, () =>
        {
            popup.IsOpen = false;
            popup.Child = null;
        });
    }
    /// <summary>Reads both trees until two readings in a row agree (bindings, layout and Loaded handlers are done).</summary>
    private static async Task<(Dictionary<string, (DependencyObject Element, Dictionary<string, string> Values)> Live, Dictionary<string, (DependencyObject Element, Dictionary<string, string> Values)> Fresh)> SettledAsync(Page page, Page shadow, Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
    {
        string Text(Dictionary<string, (DependencyObject Element, Dictionary<string, string> Values)> tree) =>
            string.Join("\n", tree.Select(e => e.Key + " " + string.Join(";", e.Value.Values.Select(v => v.Key + "=" + v.Value))));
        var previous = "";
        for (var i = 0; i < 20; i++)
        {
            await UiTicks.Idle(dispatcher);
            var live = AuthoredTree.Read(page);
            var fresh = AuthoredTree.Read(shadow);
            var text = Text(live) + "\n--\n" + Text(fresh);
            if (text == previous)
            {
                return (live, fresh);
            }
            previous = text;
            await Task.Delay(50);
        }
        return (AuthoredTree.Read(page), AuthoredTree.Read(shadow));
    }

    internal static List<string> Compare(
        Page page,
        Dictionary<string, (DependencyObject Element, Dictionary<string, string> Values)> live,
        Dictionary<string, (DependencyObject Element, Dictionary<string, string> Values)> fresh,
        PageData liveData,
        PageData freshData,
        PageData early,
        Dictionary<string, (DependencyObject Element, Dictionary<string, string> Values)>? constructed = null)
    {
        var notes = new List<string>();
        var sameData = liveData.Values.Count == freshData.Values.Count && liveData.Values.All(v => freshData.Values.TryGetValue(v.Key, out var f) && f == v.Value);
        foreach (var (key, (element, values)) in live)
        {
            var where = AuthoredTree.Describe(key);
            if (!fresh.TryGetValue(key, out var other))
            {
                // Only the topmost element one side lacks: its children are missing because it is.
                if (!fresh.ContainsKey(Parent(key)) && Parent(key).Length > 0)
                {
                    continue;
                }
                if (sameData)
                {
                    notes.Add($"{where} is shown here but missing after a restart");
                }
                continue;
            }
            foreach (var (property, value) in values)
            {
                if (!other.Values.TryGetValue(property, out var restart) || restart == value)
                {
                    continue;
                }
                var subject = property is "Text" or "Content" ? where : $"{where}.{property}";
                var cause = Cause(page, value, restart, liveData, freshData, early);
                if (cause is null && constructed is not null && constructed.TryGetValue(key, out var initial) &&
                    initial.Values.TryGetValue(property, out var markup) && markup == value)
                {
                    // The live page shows what the markup and constructor give it; a restart then runs the code that sets
                    // it (OnNavigatedTo, Loaded), which reloading in place doesn't run again.
                    notes.Add($"{subject} shows '{value}' here but '{restart}' after a restart: the page's code sets it after InitializeComponent (in OnNavigatedTo or Loaded), and reloading in place put back the markup's value");
                    continue;
                }
                if (cause is null && (AuthoredTree.IsInput(element, property) || !sameData))
                {
                    // What the user entered, or a value that follows state the reload kept: in-place reload is for that.
                    HotReloadAgent.Log($"shadow: {subject} '{value}' vs '{restart}': follows kept state");
                    continue;
                }
                if (cause == "")
                {
                    continue;
                }
                notes.Add($"{subject} shows '{value}' here but '{restart}' after a restart{(cause is null ? "" : ": " + cause)}");
            }
        }
        foreach (var key in fresh.Keys.Where(k => !live.ContainsKey(k) && (Parent(k).Length == 0 || live.ContainsKey(Parent(k)))))
        {
            if (sameData)
            {
                notes.Add($"{AuthoredTree.Describe(key)} is missing here but shown after a restart");
            }
        }
        return notes;
    }

    private static string Parent(string key) => key.Contains('/') ? key[..key.LastIndexOf('/')] : "";

    /// <summary>
    /// Why a value differs: a member that holds the live value in both pages (so the data agree) while the fresh copy
    /// shows what it held before it loaded was changed after its bindings read it. "" when the data explain the
    /// difference (the member holds each page's own value), null when no member holds the value.
    /// </summary>
    private static string? Cause(Page page, string value, string restart, PageData liveData, PageData freshData, PageData early)
    {
        if (value.Length == 0 || value == "null")
        {
            return null;
        }
        var holders = liveData.Values.Where(v => v.Value == value).Select(v => v.Key).ToList();
        foreach (var member in holders)
        {
            if (freshData.Values.TryGetValue(member, out var settled) && settled == value)
            {
                var (owner, name) = liveData.Members[member];
                var changedLate = early.Values.TryGetValue(member, out var before) && before == restart;
                var site = SourceSites.WhereSet(owner, name, page.GetType()) is { } line ? $" ({line})" : "";
                return changedLate
                    ? $"{member} changes after Loaded{(PageData.RaisesChanges(owner) ? " (a one-time binding doesn't see it)" : " without a change notification")}{site}"
                    : $"{member} is '{value}' in both, but the binding doesn't show it{site}";
            }
        }
        // The member holds each page's own value: the difference is the state the reload kept.
        return holders.Any(m => freshData.Values.TryGetValue(m, out var settled) && settled == restart) ? "" : null;
    }
}
