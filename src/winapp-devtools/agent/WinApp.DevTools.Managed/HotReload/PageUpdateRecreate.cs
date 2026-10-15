// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinApp.DevTools.Managed.HotReload;

/// <summary>
/// WINAPP_HOT_RELOAD_PAGE_UPDATE=recreate: the page is built again as a restart would build it. A new instance is
/// navigated in place of the old one (same Frame, same navigation parameter, back stack unchanged), then what the user
/// entered (text, selection, checked and expanded state, scroll offsets, focus) is put back by element path. The page's
/// fields and the view models it owns start over; the save names the ones that held something else.
/// </summary>
[PageUpdateStrategy("recreate")]
internal sealed class PageUpdateRecreate : IPageUpdateStrategy
{
    public void Update(PageUpdate update)
    {
        var page = update.Page;
        var name = page.GetType().Name;
        PageNavigation.Watch(page.XamlRoot?.Content);
        if (update.Frame is null)
        {
            update.ReloadInPlace();
            update.Note($"{name} isn't shown in a Frame, so it can't be re-created: it was reloaded in place and keeps its state");
            return;
        }
        // Taken before the update detached the page's lists (a list without items reads no selection).
        var state = update.StateBefore ?? UiState.Take(page.Content);
        var before = PageData.Take(page, getters: false);
        var fresh = update.Recreate();
        if (ReferenceEquals(fresh, page))
        {
            // The frame handed back the same instance (it keeps the page in its cache, or the navigation failed).
            update.ReloadInPlace();
            update.Note($"{name} couldn't be re-created (its Frame kept the same instance): it was reloaded in place and keeps its state");
            return;
        }
        // Compared right after construction and OnNavigatedTo: a field set later (in Loaded) starts over too.
        var lost = before.Changed(PageData.Take(fresh, getters: false));
        update.Note(lost.Count == 0 ? $"{name} re-created" : $"{name} re-created; its fields start over ({string.Join(", ", lost)})");
        UiState.RestoreWhenLoaded(fresh.Content as FrameworkElement, state, fresh.DispatcherQueue);
    }
}
