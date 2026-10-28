// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsWindow.cpp; included into that translation unit, not compiled on its own.

template<typename T>
static T TreeObject(void* object)
{
    if (!object) return nullptr;
    winrt::Windows::Foundation::IInspectable value{nullptr};
    winrt::copy_from_abi(value, object);
    return value.as<T>();
}

template<typename T>
static T TreeDescendant(DevToolsX::DependencyObject const& root)
{
    if (!root) return nullptr;
    if (auto match = root.try_as<T>()) return match;
    const int count = DevToolsXM::VisualTreeHelper::GetChildrenCount(root);
    for (int i = 0; i < count; ++i)
        if (auto found = TreeDescendant<T>(DevToolsXM::VisualTreeHelper::GetChild(root, i))) return found;
    return nullptr;
}

static DevToolsXC::ScrollViewer TreeScrollViewer()
{
    return TreeDescendant<DevToolsXC::ScrollViewer>(g_treeView);
}

static void SetIconToggleState(void* indicator, bool on)
{
    if (indicator) DevToolsPutVisibility(indicator, on ? 0 : 1);
}

static void SetModeToggleState(void* control, void* indicator, bool on)
{
    SetIconToggleState(indicator, on);
    if (!control) return;
    void* toggle = nullptr;
    HRESULT hr = reinterpret_cast<IUnknown*>(control)->QueryInterface(
        DevToolsIid<DevToolsXCP::IToggleButton>(), &toggle);
    if (SUCCEEDED(hr) && toggle) {
        IInspectable* boxed = nullptr;
        hr = BoxBool(on, &boxed);
        if (SUCCEEDED(hr) && boxed) {
            hr = DevToolsToggleButtonPutIsChecked(toggle, boxed);
            boxed->Release();
        }
        reinterpret_cast<IUnknown*>(toggle)->Release();
    }
    if (FAILED(hr))
        DevToolsOverlayLog(L"route1.mode toggle state update failed hr=0x%08X", hr);
}

// Show or clear the focus-tracking notice. An empty string hides the whole band, so "nothing to say"
// is a state the pane can be in rather than an empty box the user has to interpret. UI thread only.
static void SetFocusNotice(const std::wstring& text)
{
    if (g_focusNoticeTb) {
        HSTRING hs = MakeStr(text.c_str());
        DevToolsPutText(g_focusNoticeTb, hs);
        WindowsDeleteString(hs);
    }
    if (g_focusNoticeUi) DevToolsPutVisibility(g_focusNoticeUi, text.empty() ? 1 : 0);
}

// Keep Just my XAML availability, label and enabled state synchronized with the current tree.
// Distinguish absent source instrumentation from source present with no app-authored matches.
// Put the unavailable reason in visible text; disabled controls may not show tooltips.
static void ApplyJmxAvailability()
{
    SetToggleLabel(g_jmxLbl,
        g_jmxAvailable       ? (g_jmxActive ? L"Just my XAML: On" : L"Just my XAML: Off")
        : (g_jmxSrcSeen > 0) ? L"Just my XAML: unavailable (no app-authored XAML in view)"
                             : L"Just my XAML: unavailable (no XAML source info)");
    if (g_jmxBtnCtrl) {
        DevToolsPutIsEnabled(g_jmxBtnCtrl, g_jmxAvailable);
        void* toggle = nullptr;
        if (SUCCEEDED(reinterpret_cast<IUnknown*>(g_jmxBtnCtrl)->QueryInterface(
                DevToolsIid<DevToolsXCP::IToggleButton>(), &toggle)) && toggle) {
            IInspectable* boxed = nullptr;
            if (SUCCEEDED(BoxBool(g_jmxActive, &boxed)) && boxed) {
                DevToolsToggleButtonPutIsChecked(toggle, boxed);
                boxed->Release();
            }
            reinterpret_cast<IUnknown*>(toggle)->Release();
        }
    }
}

// Show/hide one tree row and record the result. Every visibility change goes through here so g_treeVisible
// stays an accurate mirror of what the user can see -- BringRowIntoView depends on that to count scroll
// context in real rows. UI thread only.
static void SetRowVisible(size_t j, bool visible)
{
    if (j < g_treeVisible.size()) g_treeVisible[j] = visible ? 1 : 0;
}

// Snapshot the current rows as the COM-free shape DevToolsTreeLayout reasons over. The assembly itself lives in
// DevToolsTreeLayout::BuildRows so the seam between these globals and the pure logic is covered by tests rather
// than being the one join nothing checks.
static std::vector<DevToolsTreeLayout::Row> SnapshotLayoutRows()
{
    return DevToolsTreeLayout::BuildRows(g_treeRows.size(), g_treeDepth, g_treeAppAuthored, g_treeShortType);
}

// "Not a census index" for the focus-target lookup below.
static constexpr size_t kNoCensusRow = static_cast<size_t>(-1);

// The same shape, but over the FULL census rather than the rendered rows. The rendered set is what the
// current view shows; with "Just my XAML" on that is app-authored rows only, so a question about a framework
// element's ancestry can only be answered here.
static std::vector<DevToolsTreeLayout::Row> SnapshotCensusRows()
{
    std::vector<int> depths;
    std::vector<std::wstring> types;
    depths.reserve(g_allTreeNodes.size());
    types.reserve(g_allTreeNodes.size());
    for (const auto& n : g_allTreeNodes) { depths.push_back(n.depth); types.push_back(ShortType(n.type)); }
    return DevToolsTreeLayout::BuildRows(g_allTreeNodes.size(), depths, g_allTreeAppAuthored, types);
}

// Visibility is view-relative and UI-thread-only; collapsed ancestors are evaluated within the active view.
static void RecomputeTreeVisibility()
{
    WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::WinTreeVisible);
    const auto rows    = SnapshotLayoutRows();
    const auto visible = DevToolsTreeLayout::ComputeVisibility(rows, std::vector<char>(g_treeCollapsed), g_jmxActive);
    size_t shown = 0;
    for (size_t j = 0; j < visible.size(); ++j) { SetRowVisible(j, visible[j] != 0); if (visible[j]) ++shown; }
    DevToolsOverlayLog(L"route1.tree recompute rows=%zu visible=%zu jmx=%d", visible.size(), shown, (int)g_jmxActive);
    SyncTreeStructure();
}

static void ApplyDefaultCollapse()
{
    const auto rows = SnapshotLayoutRows();
    const auto collapsed = DevToolsTreeLayout::ComputeCollapse(rows, g_jmxActive);
    for (size_t i = 0; i < collapsed.size() && i < g_treeCollapsed.size(); ++i)
        g_treeCollapsed[i] = collapsed[i];

}

// A tree that has not had its first layout has no viewport to scroll in: a pick that opens the inspector selects
// its row before then. That reveal is kept and made when the tree first gets a size (RevealPendingRow).
static InstanceHandle g_pendingRevealWire = 0;

static bool TreeViewportReady()
{
    double viewport = 0.0;
    return g_treeScroller && SUCCEEDED(DevToolsScrollGetViewportHeight(winrt::get_abi(g_treeScroller), &viewport)) &&
        viewport > 0.0;
}

static void BringRowIntoView(size_t si)
{
    const size_t n = g_treeRows.size();
    if (si >= n || !g_treeRows[si]) return;
    if (!TreeViewportReady()) {
        g_pendingRevealWire = g_selectedWire;
        DevToolsOverlayLog(L"tree.reveal row=%zu waits for the tree's first layout", si);
        return;
    }
    g_pendingRevealWire = 0;
    if (!g_treeView) return;
    const int kContextRows = 3;
    auto nthVisibleAfter = [&](size_t from, int k) -> size_t {
        size_t j = from;
        for (int c = 0; c < k; ) { if (++j >= n) return from; if (g_treeVisible[j]) ++c; }
        return j;
    };
    auto nthVisibleBefore = [&](size_t from, int k) -> size_t {
        size_t j = from;
        for (int c = 0; c < k; ) { if (j == 0) return from; --j; if (g_treeVisible[j]) ++c; }
        return j;
    };
    const size_t after  = nthVisibleAfter(si, kContextRows);
    const size_t before = nthVisibleBefore(si, kContextRows);
    try {
        if (const auto list = TreeDescendant<DevToolsXC::ListViewBase>(g_treeView)) {
            list.ScrollIntoView(g_treeNodes[si]);
            g_treeView.UpdateLayout();
        }
        auto scroller = TreeScrollViewer();
        const double initial = scroller.VerticalOffset();
        const double viewport = scroller.ViewportHeight();
        const double scrollable = scroller.ScrollableHeight();
        if (viewport <= 0.0) {
            SetStatusText(L"Tree layout is not ready to reveal the selection. Retry after layout finishes.");
            return;
        }
        double offset = initial;
        auto reveal = [&](size_t index) {
            if (!g_treeRows[index]) return;
            const auto container = g_treeView.ContainerFromNode(g_treeNodes[index]);
            if (!container) return;
            auto row = container.as<DevToolsX::FrameworkElement>();
            const auto bounds = row.TransformToVisual(scroller).TransformBounds(
                { 0.0f, 0.0f, static_cast<float>(row.ActualWidth()), static_cast<float>(row.ActualHeight()) });
            offset = DevToolsTreeLayout::RevealVerticalOffset(offset, viewport, scrollable, initial + bounds.Y, bounds.Height);
        };
        if (after != si) reveal(after);
        if (before != si) reveal(before);
        reveal(si);
        if (offset != initial && !scroller.ChangeView(nullptr,
                winrt::box_value(offset).as<winrt::Windows::Foundation::IReference<double>>(), nullptr, true)) {
            SetStatusText(L"Could not scroll to the selected row. Scroll the tree or retry.");
        }
    } catch (const winrt::hresult_error& error) {
        DevToolsOverlayLog(L"tree.reveal failed hr=%08X", static_cast<unsigned>(error.code().value));
        SetStatusText(L"Could not reveal the selected row. Retry after the app finishes layout.");
    }
}

static void ExpandAncestorsFor(size_t si)
{
    const auto rows = SnapshotLayoutRows();
    std::vector<char> collapsed(g_treeCollapsed.begin(), g_treeCollapsed.end());
    DevToolsTreeLayout::ExpandAncestors(rows, collapsed, si, g_jmxActive);
    for (size_t i = 0; i < collapsed.size() && i < g_treeCollapsed.size(); ++i)
        g_treeCollapsed[i] = collapsed[i];
}

// Force one row visible, realizing it first when lazy build never gave it an element. A row with no
// element cannot be shown, so SetRowVisible alone leaves the caller believing it revealed something.
static void ForceRowVisible(size_t si)
{
    if (si >= g_treeRows.size()) return;
    SetRowVisible(si, true);
    SyncTreeStructure();
}

// Passive focus tracking and comment selection must not rewrite the user's tree filter.
// If that filter hides the target, report it rather than silently changing the view.
enum class SelectCause { Explicit, Focus, Comment };

static void SelectNodeByWire(InstanceHandle wire, SelectCause cause = SelectCause::Explicit)
{
    if (cause != SelectCause::Focus) SetFocusNotice(L"");
    if (!wire) { g_selectedWire = 0; SyncTreeSelection(); return; }
    auto it = g_nodeIdxByWire.find(wire);
    if (it == g_nodeIdxByWire.end()) {
        if (RefreshTreeFromApp()) it = g_nodeIdxByWire.find(wire);
    }
    if (it == g_nodeIdxByWire.end()) {
        // Genuinely not in this tree (removed, or beyond the render cap). Clear rather than leaving a stale
        // row tinted as if it were still the selection.
        DevToolsOverlayLog(L"route1.select wire=%llu not in tree (after refresh) -> clearing selection",
                      (unsigned long long)wire);
        g_selectedWire = 0;
        SyncTreeSelection();
        return;
    }
    size_t si = it->second;
    if (si >= g_nodeSinks.size()) return;
    if (g_selectedWire == wire) return;

    // (2) The filter must never win over an explicit selection -- and neither a focus change nor a comment
    // click is one. A focus change that reached here at all is on a row this view already shows, so there is
    // nothing to reveal; flipping the filter for it would be discarding a choice the user made on behalf of a
    // keystroke they aimed at their own app. A comment click is in a mode where the filter's toggle is
    // not on screen, so flipping it there is a state change with no visible cause.
    if (cause == SelectCause::Explicit && RowHiddenByJmx(si)) {
        g_jmxActive = false;
        ApplyJmxAvailability();
        ApplyDefaultCollapse();
        DevToolsOverlayLog(L"route1.select row %zu excluded by Just-my-XAML -> filter turned off to reveal it", si);
    } else if (cause == SelectCause::Comment && RowHiddenByJmx(si)) {
        SetStatusText(L"Selected it. Just my XAML is hiding its row in the tree view.");
        DevToolsOverlayLog(L"route1.select row %zu excluded by Just-my-XAML; comment click reported it "
                      L"instead of clearing the filter", si);
    }
    // (1) Expand collapsed ancestors of the target -- see ExpandAncestorsFor for why the walk is relational.
    ExpandAncestorsFor(si);

    RecomputeTreeVisibility();
    if (!g_treeFilterQuery.empty()) {
        ApplyTreeFilter(g_treeFilterQuery);
        ForceRowVisible(si);
    } else if (cause != SelectCause::Explicit && RowHiddenByJmx(si)) {
        ForceRowVisible(si);
    }
    if (!g_nodeSinks[si]) {
        DevToolsOverlayLog(L"route1.select row %zu has no click sink after realize -- selection not applied", si);
        return;
    }
    g_selectedWire = wire;
    BringRowIntoView(si);
    g_nodeSinks[si]->Invoke(nullptr, nullptr);
    DevToolsOverlayLog(L"route1.select selected row %zu for wire=%llu", si, (unsigned long long)wire);
}

// Focus/comment selection does not clear filters, so it must reveal the selected row itself.
static void RevealSelectedRowAfterViewChange()
{
    if (!g_selectedWire) return;
    auto it = g_nodeIdxByWire.find(g_selectedWire);
    if (it == g_nodeIdxByWire.end()) return;
    const size_t si = it->second;
    ExpandAncestorsFor(si);
    RecomputeTreeVisibility();
    if (!g_treeFilterQuery.empty()) ApplyTreeFilter(g_treeFilterQuery);
    ForceRowVisible(si);
    BringRowIntoView(si);
    SyncTreeSelection();
    DevToolsOverlayLog(L"route1.view-change re-revealed selected row %zu (jmx=%d)", si, (int)g_jmxActive);
}

// The tree got a size: make a reveal that had to wait for it, if that row is still the selection.
static void RevealPendingRow()
{
    if (!g_pendingRevealWire || !TreeViewportReady()) return;
    const InstanceHandle wire = std::exchange(g_pendingRevealWire, 0);
    if (wire != g_selectedWire) return;
    const auto it = g_nodeIdxByWire.find(wire);
    if (it == g_nodeIdxByWire.end()) return;
    BringRowIntoView(it->second);
    DevToolsOverlayLog(L"tree.reveal made the waiting reveal of row %zu", it->second);
}

static bool RebuildTreeRows();

static bool SetTreeRowExpanded(size_t index, bool expanded)
{
    if (!g_treeFilterQuery.empty()) {
        SetStatusText(L"Clear the tree filter to expand or collapse rows.");
        DevToolsOverlayLog(L"tree.expander refused while the tree filter is active");
        return false;
    }
    if (index >= g_treeCollapsed.size() || index >= g_treeA11y.size() || !g_treeA11y[index].branch)
        return false;
    if ((g_treeCollapsed[index] == 0) == expanded) return true;
    g_treeCollapsed[index] = expanded ? 0 : 1;
    RecomputeTreeVisibility();
    return true;
}

struct JmxSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void* /*sender*/, void* /*args*/)
    {
        if (gen != g_route1Gen) return S_OK;
        if (!g_jmxAvailable) return S_OK;
        const bool prior = g_jmxActive;
        g_jmxActive = !g_jmxActive;
        if (!RebuildTreeRows()) { g_jmxActive = prior; return S_OK; }
        SetFocusNotice(L"");
        ApplyJmxAvailability();
        if (!g_treeFilterQuery.empty()) ApplyTreeFilter(g_treeFilterQuery);
        RevealSelectedRowAfterViewChange();
        return S_OK;
    }
    void Init() { InitSink(IID_RoutedEventHandler_Del, g_route1Gen); }
};

// The inspector's Pick button and its Ctrl+Shift+P shortcut.
static void ToggleWindowPick()
{
    if (!g_ctx.writeFn) {
        DevToolsOverlay_DisarmPick();
        g_pickRoute.CancelWindowPick();
        SetModeToggleState(g_pickBtnCtrl, g_pickOnUi, false);
        DevToolsOverlayLog(L"route1.pick refused by process posture");
        return;
    }
    if (DevToolsOverlay_IsPickArmed()) {
        DevToolsOverlay_DisarmPick();
        g_pickRoute.CancelWindowPick();
        return;
    }
    if (!DevToolsOverlay_ArmPick()) {
        g_pickRoute.CancelWindowPick();
        SetModeToggleState(g_pickBtnCtrl, g_pickOnUi, DevToolsOverlay_IsPickArmed());
        DevToolsOverlayLog(L"route1.pick arm failed: overlay catcher not ready");
        return;
    }
    g_pickRoute.ArmWindowPick();
    FocusTargetAppForPick();
}

struct PickSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void* /*sender*/, void* /*args*/)
    {
        if (gen == g_route1Gen) ToggleWindowPick();
        return S_OK;
    }
    void Init() { InitSink(IID_RoutedEventHandler_Del, g_route1Gen); }
};

struct FocusSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void* /*sender*/, void* /*args*/)
    {
        if (gen != g_route1Gen) return S_OK;
        const bool next = !g_focusActive;
        if (!DevToolsOverlay_SetFocusTracking(next)) {
            SetModeToggleState(g_focusBtnCtrl, g_focusOnUi, g_focusActive);
            DevToolsOverlayLog(L"route1.focus toggle could not wire the app content root");
            return S_OK;
        }
        g_focusActive = next;
        SetModeToggleState(g_focusBtnCtrl, g_focusOnUi, g_focusActive);
        return S_OK;
    }
    void Init() { InitSink(IID_RoutedEventHandler_Del, g_route1Gen); }
};

// Apply the tree filter query (already the raw box text) to the rendered rows. Empty query -> restore the
// collapse-based visibility. Non-empty -> show only rows whose lowercased "type #name" contains the query
// (case-insensitive substring), ignoring collapse state so every match is reachable. The "Just my XAML" gate
// still applies so a search stays within the active scope. Operates only on the realized (<=
// node-budget) rows, so a per-keystroke pass is cheap. UI thread only.
static void SetTreeNoMatches(const std::wstring& query, bool none)
{
    if (g_treeNoMatchTb && none) {
        HSTRING hs = MakeStr((L"No elements match \"" + query + L"\"").c_str());
        DevToolsPutText(g_treeNoMatchTb, hs);
        WindowsDeleteString(hs);
    }
    if (g_treeNoMatchUi) DevToolsPutVisibility(g_treeNoMatchUi, none ? 0 : 1);
}

static void ApplyTreeFilter(const std::wstring& queryRaw)
{
    WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::WinTreeFilter);
    std::wstring q = DevToolsTreeLayout::NormalizeTreeFilterQuery(queryRaw);
    g_treeFilterQuery = q;
    if (q.empty()) { SetTreeNoMatches(q, false); RecomputeTreeVisibility(); return; }
    const size_t n = g_treeRows.size();
    std::vector<char> match(n, 0);
    size_t shown = 0;
    for (size_t j = 0; j < n; ++j) {
        const bool m = !RowHiddenByJmx(j) && (j < g_treeLabel.size()) && (g_treeLabel[j].find(q) != std::wstring::npos);
        match[j] = m ? 1 : 0;
        if (!m) continue;
        ++shown;
    }
    for (size_t j = 0; j < n; ++j) SetRowVisible(j, match[j] != 0);
    SetTreeNoMatches(queryRaw, shown == 0);
    SyncTreeStructure();
    DevToolsOverlayLog(L"route1.filter q='%s' rows=%zu shown=%zu", q.c_str(), n, shown);
}

// Read filter text directly from the TextBox; the diagnostics chain can return the previous keystroke.
struct FilterSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void* /*sender*/, void* /*args*/)
    {
        if (gen != g_route1Gen) return S_OK;
        std::wstring text;
        if (g_filterTextBox) {
            HSTRING h = nullptr;
            if (SUCCEEDED(DevToolsGetTextBoxText(g_filterTextBox, &h))) {
                UINT32 len = 0;
                const wchar_t* p = WindowsGetStringRawBuffer(h, &len);
                if (p && len) text.assign(p, len);
                WindowsDeleteString(h);
            }
        } else if (g_ctx.readInputFn && g_filterBox) {
            g_ctx.readInputFn(g_filterBox, text);
        }
        DevToolsOverlayLog(L"route1.filter read text='%s' len=%zu", text.c_str(), text.size());
        ApplyTreeFilter(text);
        return S_OK;
    }
    void Init() { InitSink(IID_TextChanged_Del, g_route1Gen); }
};

static void ApplyPropFilter(const std::wstring& queryRaw)
{
    std::wstring q;
    for (wchar_t c : queryRaw) { if (c >= L'A' && c <= L'Z') c = (wchar_t)(c + 32); q += c; }
    while (!q.empty() && q.back() == L' ') q.pop_back();
    g_propFilterQuery = q;
    g_propFilterRaw = queryRaw;
    while (!g_propFilterRaw.empty() && g_propFilterRaw.back() == L' ') g_propFilterRaw.pop_back();

    const size_t shown = RebuildPaneItems();
    UpdatePropCount(shown);
    DevToolsOverlayLog(L"route1.propfilter q='%s' rows=%zu shown=%zu", q.c_str(), g_propRowTotal, shown);
}

// TextChanged sink for the property filter box. Reads the box's text DIRECTLY off the TextBox: the
// diagnostics chain returns the PREVIOUS text here, so clearing the box would re-apply the old query and
// leave the pane filtered with no way back short of reselecting the node.
struct PropFilterSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void* /*sender*/, void* /*args*/)
    {
        if (gen != g_route1Gen) return S_OK;
        std::wstring text;
        if (g_propFilterTextBox) {
            HSTRING h = nullptr;
            if (SUCCEEDED(DevToolsGetTextBoxText(g_propFilterTextBox, &h))) {
                UINT32 len = 0;
                const wchar_t* p = WindowsGetStringRawBuffer(h, &len);
                if (p && len) text.assign(p, len);
                WindowsDeleteString(h);
            }
        }
        ApplyPropFilter(text);
        return S_OK;
    }
    void Init() { InitSink(IID_TextChanged_Del, g_route1Gen); }
};

// The way back from an empty pane. Writes the BOX, not the query: the box's TextChanged is what
// re-applies the filter, so one path owns it and the text the user can still see in the box cannot drift
// from the rows the pane is rendering.
struct PropClearFilterSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen) return S_OK;
        if (!g_propFilterTextBox) { ApplyPropFilter(L""); return S_OK; }
        HSTRING hs = MakeStr(L"");
        const HRESULT hr = DevToolsPutTextBoxText(g_propFilterTextBox, hs);
        WindowsDeleteString(hs);
        // put_Text answering S_OK is not the same fact as the box being empty and the rows being
        // back. Read the value the call was supposed to change; if it did not change, apply directly so
        // the only affordance out of an empty pane cannot be a button that reports success and does nothing.
        std::wstring after;
        HSTRING rb = nullptr;
        if (SUCCEEDED(DevToolsGetTextBoxText(g_propFilterTextBox, &rb))) {
            UINT32 len = 0;
            const wchar_t* p = WindowsGetStringRawBuffer(rb, &len);
            if (p && len) after.assign(p, len);
            WindowsDeleteString(rb);
        }
        if (!after.empty()) {
            DevToolsOverlayLog(L"route1.propfilter clear did not take hr=0x%08X text='%s' -- applying directly",
                          hr, after.c_str());
            ApplyPropFilter(L"");
        }
        return S_OK;
    }
    void Init() { InitSink(IID_RoutedEventHandler_Del, g_route1Gen); }
};
