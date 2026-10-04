// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsWindow.cpp; included into that translation unit, not compiled on its own.

static void SetIconToggleState(void* indicator, bool on)
{
    if (indicator) DevToolsPutVisibility(indicator, on ? 0 : 1);
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
    if (j < g_treeRows.size() && g_treeRows[j]) DevToolsPutVisibility(g_treeRows[j], visible ? 0 : 1);
}

static void SyncChevronState(size_t i)
{
    if (i < g_treeGlyph.size() && g_treeGlyph[i]) {
        HSTRING hs = MakeStr(g_treeCollapsed[i] ? L"\uE76C" : L"\uE70D");
        DevToolsPutText(g_treeGlyph[i], hs);
        WindowsDeleteString(hs);
    }
    if (i < g_treeChevronToggle.size() && g_treeChevronToggle[i]) {
        IInspectable* boxed = nullptr;
        if (SUCCEEDED(BoxBool(!g_treeCollapsed[i], &boxed)) && boxed) {
            DevToolsToggleButtonPutIsChecked(g_treeChevronToggle[i], boxed);
            boxed->Release();
        }
    }
}

// Apply row indentation for a set of view depths, measured from `baseline` (the depth that should render
// flush left). UI thread only.
static void ApplyIndent(const std::vector<int>& viewDepth, int baseline)
{
    g_treeViewDepth = viewDepth;
    g_treeIndentBaseline = baseline;
    for (size_t i = 0; i < viewDepth.size() && i < g_treeRowFe.size(); ++i) {
        if (viewDepth[i] < 0 || !g_treeRowFe[i]) continue;
        int level = viewDepth[i] - baseline;
        if (level < 0) level = 0;
        DevToolsPutMargin(g_treeRowFe[i], (double)IndentPxForLevel(level), 1.0, 0.0, 1.0);
    }
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
    // Build any row this view has just made visible for the first time, BEFORE the visibility pass -- a row
    // with no element cannot be shown, and an expand that reveals nothing reads as a dead chevron.
    EnsureRowsRealized(visible);
    size_t shown = 0;
    for (size_t j = 0; j < visible.size(); ++j) { SetRowVisible(j, visible[j] != 0); if (visible[j]) ++shown; }
    DevToolsOverlayLog(L"route1.tree recompute rows=%zu visible=%zu jmx=%d", visible.size(), shown, (int)g_jmxActive);
    for (size_t i = 0; i < g_treeGlyph.size(); ++i) SyncChevronState(i);
}

static void ApplyDefaultCollapse()
{
    const auto rows = SnapshotLayoutRows();
    const auto collapsed = DevToolsTreeLayout::ComputeCollapse(rows, g_jmxActive);
    const auto viewDepth = DevToolsTreeLayout::ComputeViewDepths(rows, g_jmxActive);
    for (size_t i = 0; i < collapsed.size() && i < g_treeCollapsed.size(); ++i)
        g_treeCollapsed[i] = collapsed[i];

    // Re-indent for THIS view. Indentation is a property of the view: with "Just my XAML" on - the default -
    // the shallowest visible row can be 20+ levels deep, and its absolute census indent would leave every row
    // starting ~45% into the tree pane as dead gutter for almost no nesting information. viewDepth is the
    // depth within the current effective view, so the filtered view starts flush left as the unfiltered one does.
    for (size_t i = 0; i < viewDepth.size() && i < g_treeRowFe.size(); ++i) {
        if (viewDepth[i] < 0 || !g_treeRowFe[i]) continue;
        DevToolsPutMargin(g_treeRowFe[i], (double)IndentPxForLevel(viewDepth[i]), 1.0, 0.0, 1.0);
    }
}

static void BringRowIntoView(size_t si)
{
    const size_t n = g_treeRows.size();
    if (si >= n || !g_treeRows[si] || !g_treeScrollUi) return;
    DevToolsUpdateLayout(g_treeRows[si]);

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
        winrt::Windows::Foundation::IInspectable value{ nullptr };
        winrt::copy_from_abi(value, g_treeScrollUi);
        auto scroller = value.as<DevToolsXC::ScrollViewer>();
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
            winrt::copy_from_abi(value, g_treeRows[index]);
            auto row = value.as<DevToolsX::FrameworkElement>();
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
    if (!g_treeRows[si]) {
        std::vector<char> one(g_treeRows.size(), 0);
        one[si] = 1;
        EnsureRowsRealized(one);
    }
    SetRowVisible(si, true);
}

// Passive focus tracking and comment selection must not rewrite the user's tree filter.
// If that filter hides the target, report it rather than silently changing the view.
enum class SelectCause { Explicit, Focus, Comment };

static void SelectNodeByWire(InstanceHandle wire, SelectCause cause = SelectCause::Explicit)
{
    if (cause != SelectCause::Focus) SetFocusNotice(L"");
    if (!wire) { g_selectedWire = 0; return; }
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
        SelectRowVisual(nullptr);
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
    g_nodeSinks[si]->Invoke(g_treeControls[si], nullptr);
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
    if (si < g_treeControls.size() && g_treeControls[si]) SelectRowVisual(g_treeControls[si]);
    DevToolsOverlayLog(L"route1.view-change re-revealed selected row %zu (jmx=%d)", si, (int)g_jmxActive);
}

static bool RebuildTreeRows();

struct ChevronSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void* /*sender*/, void* /*args*/)
    {
        if (gen != g_route1Gen || rowsGen != g_treeRowsGen) return S_OK;
        if (idx < g_treeCollapsed.size()) {
            g_treeCollapsed[idx] = g_treeCollapsed[idx] ? 0 : 1;
            RecomputeTreeVisibility();
        }
        return S_OK;
    }
    void Init(size_t i) { idx = i; rowsGen = g_treeRowsGen; InitSink(IID_RoutedEventHandler_Del, g_route1Gen); }
    size_t    idx = 0;
    unsigned  rowsGen = 0;
};

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

struct PickSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void* /*sender*/, void* /*args*/)
    {
        if (gen != g_route1Gen) return S_OK;
        if (!g_ctx.writeFn) {
            DevToolsOverlay_DisarmPick();
            g_pickRoute.CancelWindowPick();
            DevToolsOverlayLog(L"route1.pick refused by process posture");
            return S_OK;
        }
        if (DevToolsOverlay_IsPickArmed()) {
            DevToolsOverlay_DisarmPick();
            g_pickRoute.CancelWindowPick();
            return S_OK;
        }
        if (!DevToolsOverlay_ArmPick()) {
            g_pickRoute.CancelWindowPick();
            DevToolsOverlayLog(L"route1.pick arm failed: overlay catcher not ready");
            return S_OK;
        }
        g_pickRoute.ArmWindowPick();
        FocusTargetAppForPick();
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
            DevToolsOverlayLog(L"route1.focus toggle could not wire the app content root");
            return S_OK;
        }
        g_focusActive = next;
        SetIconToggleState(g_focusOnUi, g_focusActive);
        return S_OK;
    }
    void Init() { InitSink(IID_RoutedEventHandler_Del, g_route1Gen); }
};

// Apply the tree filter query (already the raw box text) to the rendered rows. Empty query -> restore the
// collapse-based visibility. Non-empty -> show only rows whose lowercased "type #name" contains the query
// (case-insensitive substring), ignoring collapse state so every match is reachable. The "Just my XAML" gate
// still applies so a search stays within the active scope. Operates only on the realized (<=
// node-budget) rows, so a per-keystroke pass is cheap. UI thread only.
static void ApplyTreeFilter(const std::wstring& queryRaw)
{
    WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::WinTreeFilter);
    std::wstring q = DevToolsTreeLayout::NormalizeTreeFilterQuery(queryRaw);
    g_treeFilterQuery = q;
    const auto rows = SnapshotLayoutRows();
    const auto viewDepth = DevToolsTreeLayout::ComputeViewDepths(rows, g_jmxActive);
    if (q.empty()) { ApplyIndent(viewDepth, 0); RecomputeTreeVisibility(); return; }
    const size_t n = g_treeRows.size();
    std::vector<char> match(n, 0);
    int minDepth = -1;
    size_t shown = 0;
    for (size_t j = 0; j < n; ++j) {
        const bool m = !RowHiddenByJmx(j) && (j < g_treeLabel.size()) && (g_treeLabel[j].find(q) != std::wstring::npos);
        match[j] = m ? 1 : 0;
        if (!m) continue;
        ++shown;
        const int d = j < viewDepth.size() ? viewDepth[j] : 0;
        if (d >= 0 && (minDepth < 0 || d < minDepth)) minDepth = d;
    }
    // Indentation is relative to the SHALLOWEST MATCH, not to the census. A filtered view shows an arbitrary
    // slice of the tree, and giving each match its unfiltered indent left a lone result sitting ~160px into an
    // otherwise empty pane. Subtracting the baseline keeps the nesting between matches while starting flush left.
    ApplyIndent(viewDepth, minDepth < 0 ? 0 : minDepth);
    // A match inside a collapsed subtree has never been shown, so it has no element yet. Without
    // this the filter would silently drop exactly the results the user could not already see.
    EnsureRowsRealized(match);
    for (size_t j = 0; j < n; ++j) SetRowVisible(j, match[j] != 0);
    DevToolsOverlayLog(L"route1.filter q='%s' rows=%zu shown=%zu indentBase=%d", q.c_str(), n, shown, minDepth);
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
