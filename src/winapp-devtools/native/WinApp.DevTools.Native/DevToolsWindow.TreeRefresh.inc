// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsWindow.cpp; included into that translation unit, not compiled on its own.

static void ReleaseTreeRowState()
{
    ++g_treeRowsGen;
    SelectRowVisual(nullptr);
    for (auto* p : g_treeRows)     if (p) reinterpret_cast<IUnknown*>(p)->Release();
    for (auto* p : g_treeRowFe)    if (p) reinterpret_cast<IUnknown*>(p)->Release();
    for (auto* p : g_treeControls) if (p) reinterpret_cast<IUnknown*>(p)->Release();
    for (auto* p : g_treeGlyph)    if (p) reinterpret_cast<IUnknown*>(p)->Release();
    for (auto* p : g_treeChevronToggle) if (p) reinterpret_cast<IUnknown*>(p)->Release();
    g_treeRows.clear(); g_treeRowFe.clear(); g_treeControls.clear(); g_treeGlyph.clear(); g_treeChevronToggle.clear();
    g_treeDepth.clear(); g_treeCollapsed.clear(); g_treeVisible.clear();
    g_treeLabel.clear(); g_treeShortType.clear(); g_treeAppAuthored.clear();
    g_nodeSinks.clear(); g_nodeIdxByWire.clear(); g_renderedOriginalIndices.clear();
}

// Refreshing replaces stale handles after navigation so picks resolve against the current app tree.
static bool RebuildTreeRows()
{
    WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::WinTreeRows);
    if (!g_treeHostChildren) return false;

    std::vector<int> depths;
    std::vector<std::wstring> types;
    depths.reserve(g_allTreeNodes.size());
    types.reserve(g_allTreeNodes.size());
    for (const auto& n : g_allTreeNodes) { depths.push_back(n.depth); types.push_back(ShortType(n.type)); }
    const auto layout = DevToolsTreeLayout::BuildRows(
        g_allTreeNodes.size(), depths, g_allTreeAppAuthored, types);
    const bool appOnly = g_jmxActive && g_jmxAvailable;
    const auto selected = DevToolsTreeLayout::SelectRenderRows(layout, kMaxRenderNodes, appOnly);

    size_t populationTotal = g_allTreeNodes.size();
    if (appOnly) {
        populationTotal = 0;
        for (char a : g_allTreeAppAuthored) if (a) ++populationTotal;
    }

    // Read the identifying captions HERE, for exactly these rows, and before a single row is built.
    // Doing it in the snapshot instead made the pick and tree-select paths pay ~1.1 s per refresh for
    // captions RefreshTreeFromApp then discarded on its `same` early-out, and made a JMX-filtered render pay
    // for the nodes it filters out. Filling before the rows are built keeps the caption in the row's FIRST
    // paint -- no row appears bare and populates afterwards.
    if (g_ctx.previewFn) g_ctx.previewFn(g_allTreeNodes, selected);

    std::vector<DevToolsWindowNode> rendered;
    rendered.reserve(selected.size());
    for (size_t i : selected) {
        rendered.push_back(g_allTreeNodes[i]);
        if (!g_allTreeNodes[i].name.empty())
            DevToolsOverlayLog(L"route1.rows admitted mode=%s name=%s wire=%llu",
                          appOnly ? L"app" : L"all", g_allTreeNodes[i].name.c_str(),
                          (unsigned long long)g_allTreeNodes[i].handle);
    }

    std::vector<int> renderedDepths;
    std::vector<std::wstring> renderedTypes;
    renderedDepths.reserve(rendered.size());
    renderedTypes.reserve(rendered.size());
    std::vector<char> renderedAppAuthored;
    renderedAppAuthored.reserve(rendered.size());
    for (size_t i : selected) {
        renderedDepths.push_back(g_allTreeNodes[i].depth);
        renderedTypes.push_back(ShortType(g_allTreeNodes[i].type));
        renderedAppAuthored.push_back(i < g_allTreeAppAuthored.size() ? g_allTreeAppAuthored[i] : 0);
    }
    const auto renderedRows = DevToolsTreeLayout::BuildRows(
        rendered.size(), renderedDepths, renderedAppAuthored, renderedTypes);
    const auto collapsed = DevToolsTreeLayout::ComputeCollapse(renderedRows, g_jmxActive);
    const auto viewDepth = DevToolsTreeLayout::ComputeViewDepths(renderedRows, g_jmxActive);
    const auto a11y = DevToolsTreeLayout::ComputeA11yRows(renderedRows, g_jmxActive);
    const auto willShow = DevToolsTreeLayout::ComputeVisibility(renderedRows, std::vector<char>(collapsed), g_jmxActive);

    // Retire the old generation and size arrays before building. Append replacement content before
    // removing old rows to avoid a blank frame; capture the old removal count before appending.
    unsigned int oldCount = 0;
    DevToolsVecGetSize(g_treeHostChildren, &oldCount);

    ReleaseTreeRowState();
    g_renderedOriginalIndices = selected;
    const size_t count = rendered.size();
    g_treeRows.assign(count, nullptr);
    g_treeRowFe.assign(count, nullptr);
    g_treeControls.assign(count, nullptr);
    g_treeDepth.assign(count, 0);
    g_treeCollapsed.assign(count, 0);
    g_treeVisible.assign(count, 1);
    g_treeGlyph.assign(count, nullptr);
    g_treeChevronToggle.assign(count, nullptr);
    g_treeLabel.assign(count, std::wstring());
    g_treeShortType.assign(count, std::wstring());
    g_treeAppAuthored.assign(count, 0);
    g_nodeSinks.assign(count, nullptr);
    g_nodeIdxByWire.clear();

    g_treeA11y = a11y;
    g_treeCollapseAtBuild = collapsed;
    g_treeMinDepth = 0;
    for (size_t i = 0; i < rendered.size(); ++i)
        if (i == 0 || rendered[i].depth < g_treeMinDepth) g_treeMinDepth = rendered[i].depth;

    int wired = 0;
    IInspectable* panel = BuildTreeRowsDirect(rendered, a11y, collapsed, willShow, populationTotal, appOnly, &wired);
    if (!panel) {
        DevToolsOverlayLog(L"route1.rows rebuild FAILED to build the rows panel");
        return false;
    }

    void* ui = nullptr;
    if (FAILED(panel->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) || !ui) {
        panel->Release();
        DevToolsOverlayLog(L"route1.rows rebuild panel has no IUIElement");
        return false;
    }
    const HRESULT hr = DevToolsVecAppend(g_treeHostChildren, reinterpret_cast<IInspectable*>(ui));
    reinterpret_cast<IUnknown*>(ui)->Release();
    panel->Release();
    if (FAILED(hr)) {
        DevToolsOverlayLog(L"route1.rows rebuild append FAILED hr=0x%08X", hr);
        return false;
    }
    for (unsigned int i = 0; i < oldCount; ++i) DevToolsVecRemoveAt(g_treeHostChildren, 0);

    for (size_t i = 0; i < collapsed.size() && i < g_treeCollapsed.size(); ++i)
        g_treeCollapsed[i] = collapsed[i];
    ApplyIndent(viewDepth, 0);
    RecomputeTreeVisibility();
    if (g_selectedWire) {
        auto it = g_nodeIdxByWire.find(g_selectedWire);
        if (it != g_nodeIdxByWire.end() && it->second < g_treeControls.size())
            SelectRowVisual(g_treeControls[it->second]);
    }
    DevToolsOverlayLog(L"route1.rows rebuilt mode=%s selected=%zu population=%zu wired=%d",
                  appOnly ? L"app" : L"all", rendered.size(), populationTotal, wired);
    return true;
}


// Read this snapshot's source URIs and classify them in ONE step. The loop and the classify call are kept in
// the same function on purpose: DevToolsAppXaml_ClassifyBatch reads the vector the loop fills, so separating them
// invites a caller that classifies before anything has populated it and silently gets an all-framework tree
//. Returns the app-authored count. Also refreshes g_jmxSrcSeen, which is what distinguishes "this app
// is not source-instrumented" from "instrumented, but nothing app-authored is in view".
static int ClassifyTreeSnapshot()
{
    WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::WinClassify);
    std::vector<std::wstring> jmxSrcUris;
    jmxSrcUris.reserve(g_allTreeNodes.size());
    g_jmxSrcSeen = 0;
    for (const auto& n : g_allTreeNodes) {
        jmxSrcUris.push_back(n.sourceUri);
        if (!n.sourceUri.empty()) ++g_jmxSrcSeen;
    }
    DevToolsAppXaml_ClassifyBatch(jmxSrcUris, g_allTreeAppAuthored);
    int appCount = 0;
    for (char a : g_allTreeAppAuthored) if (a) ++appCount;
    return appCount;
}

// Write the node-count header for the CURRENT row list. Same two shapes BuildWindowMarkup authors at open, so
// a refreshed header cannot say something the initial one would not have.
static void SetTreeHeaderCurrent(size_t flattenedCount, size_t censusTotal)
{
    if (!g_treeHeaderTb) return;
    wchar_t hdr[160];
    const size_t total = censusTotal ? censusTotal : flattenedCount;
    if (total > flattenedCount)
        _snwprintf_s(hdr, _countof(hdr), _TRUNCATE, L"VISUAL TREE \u00B7 SHOWING %zu OF %zu NODES",
                     flattenedCount, total);
    else
        _snwprintf_s(hdr, _countof(hdr), _TRUNCATE, L"VISUAL TREE \u00B7 %zu NODES", total);
    HSTRING hs = MakeStr(hdr);
    DevToolsPutText(g_treeHeaderTb, hs);
    WindowsDeleteString(hs);
}

static double ReadTreeScrollOffset()
{
    if (!g_treeScrollUi) return 0.0;
    void* sv = nullptr;
    if (FAILED(reinterpret_cast<IUnknown*>(g_treeScrollUi)->QueryInterface(DevToolsIid<DevToolsXC::IScrollViewer>(), &sv)) || !sv)
        return 0.0;
    double off = 0.0;
    if (FAILED(DevToolsScrollGetVerticalOffset(sv, &off))) off = 0.0;
    reinterpret_cast<IUnknown*>(sv)->Release();
    return off;
}

// ChangeView takes IReference<double>, NOT IInspectable: handing it the IInspectable vtable returns S_OK and
// does nothing. Hence the QI, and hence checking `changed` rather than the HRESULT.
static void RestoreTreeScrollOffset(double offset)
{
    if (!g_treeScrollUi || offset <= 0.0) return;
    void* sv = nullptr;
    if (FAILED(reinterpret_cast<IUnknown*>(g_treeScrollUi)->QueryInterface(DevToolsIid<DevToolsXC::IScrollViewer>(), &sv)) || !sv)
        return;
    DevToolsUpdateLayout(g_treeScrollUi);
    double room = 0.0;
    if (SUCCEEDED(DevToolsScrollGetScrollableHeight(sv, &room)) && room >= 0.0 && offset > room) offset = room;
    IInspectable* boxed = nullptr;
    IInspectable* ref = nullptr;
    do {
        static IInspectable* s_pvStatics = nullptr;
        if (!s_pvStatics) {
            HSTRING cls = MakeStr(L"Windows.Foundation.PropertyValue");
            HRESULT hrF = RoGetActivationFactory(cls, DevToolsIid<winrt::Windows::Foundation::IPropertyValueStatics>(),
                                                 reinterpret_cast<void**>(&s_pvStatics));
            WindowsDeleteString(cls);
            if (FAILED(hrF)) { s_pvStatics = nullptr; break; }
        }
        if (FAILED(DevToolsPropertyValueCreateDouble(s_pvStatics, offset, &boxed)) || !boxed) break;
        if (FAILED(boxed->QueryInterface(DevToolsIid<winrt::Windows::Foundation::IReference<double>>(),
                                         reinterpret_cast<void**>(&ref)))) ref = nullptr;
        if (!ref) break;
        bool changed = false;
        HRESULT hr = DevToolsScrollChangeView(sv, nullptr, ref, nullptr, /*disableAnimation*/ true, &changed);
        if (FAILED(hr) || !changed)
            DevToolsOverlayLog(L"route1.refresh scroll restore to %.1f did NOT take (hr=0x%08X changed=%d)",
                          offset, hr, (int)changed);
    } while (false);
    if (ref)   ref->Release();
    if (boxed) boxed->Release();
    reinterpret_cast<IUnknown*>(sv)->Release();
}

static bool RefreshTreeFromApp(bool refreshValues)
{
    WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::WinTreeRefresh);
    if (!g_ctx.snapshotFn || !g_treeHostChildren) return false;
    if (g_treeRefreshing) return false;
    g_treeRefreshing = true;
    struct Guard { ~Guard() { g_treeRefreshing = false; } } guard;

    std::vector<DevToolsWindowNode> fresh;
    size_t censusTotal = 0;
    g_ctx.snapshotFn(fresh, censusTotal);
    if (fresh.empty()) {
        // Refuse rather than render an empty tree. An empty census here means the app is mid-teardown or the
        // snapshot lost a race, and blanking the pane would destroy the user's view to describe neither state.
        DevToolsOverlayLog(L"route1.refresh census came back EMPTY -- keeping the existing rows and staying stale");
        return false;
    }

    bool same = fresh.size() == g_allTreeNodes.size();
    for (size_t i = 0; same && i < fresh.size(); ++i)
        same = fresh[i].handle == g_allTreeNodes[i].handle;
    if (same && !refreshValues) {
        g_snapshotTotal = censusTotal ? censusTotal : fresh.size();
        SetTreeHeaderCurrent(g_allTreeNodes.size(), g_snapshotTotal);
        g_treeStale = false;
        DevToolsOverlayLog(L"route1.refresh census unchanged (%zu nodes) -- rows already describe the app", fresh.size());
        return true;
    }

    // Save the view state the swap would otherwise drop. Collapse is keyed by WIRE, not row index: after a
    // navigation row 40 is a different element, so restoring by index would apply one page's expansion state
    // to another page's rows.
    std::vector<unsigned long long> oldKeys(g_treeRows.size(), 0ULL);
    for (const auto& kv : g_nodeIdxByWire)
        if (kv.second < oldKeys.size()) oldKeys[kv.second] = (unsigned long long)kv.first;
    const std::vector<char> oldCollapsed = g_treeCollapsed;
    const std::wstring savedQuery = g_treeFilterQuery;
    const double savedScroll = ReadTreeScrollOffset();

    // Roll back on a failed rebuild: the old rows are still realized, so leaving these globals pointing at a
    // tree that was never rendered would make every later row lookup answer about markup nobody can see.
    std::vector<DevToolsWindowNode> priorNodes = g_allTreeNodes;
    std::vector<char>          priorAuthored = g_allTreeAppAuthored;
    const bool priorJmxActive = g_jmxActive, priorJmxAvailable = g_jmxAvailable;

    g_allTreeNodes = std::move(fresh);
    const int appCount = ClassifyTreeSnapshot();
    g_jmxAvailable = appCount > 0;
    // "Just my XAML" is a MODE the user chose, so it is carried across. But it cannot stay on over a page with
    // no app-authored nodes -- that renders an empty tree and looks exactly like a refresh that lost the app.
    if (!g_jmxAvailable) g_jmxActive = false;

    if (!RebuildTreeRows()) {
        g_allTreeNodes = std::move(priorNodes);
        g_allTreeAppAuthored = std::move(priorAuthored);
        g_jmxActive = priorJmxActive; g_jmxAvailable = priorJmxAvailable;
        DevToolsOverlayLog(L"route1.refresh row rebuild FAILED -- rolled back; the tree stays out of date");
        return false;
    }

    {
        std::vector<unsigned long long> newKeys(g_treeRows.size(), 0ULL);
        for (const auto& kv : g_nodeIdxByWire)
            if (kv.second < newKeys.size()) newKeys[kv.second] = (unsigned long long)kv.first;
        std::vector<char> collapsed(g_treeCollapsed.begin(), g_treeCollapsed.end());
        const size_t restored = DevToolsTreeLayout::RestoreCollapseByKey(oldKeys, oldCollapsed, newKeys, collapsed);
        g_treeCollapsed = collapsed;
        RecomputeTreeVisibility();
        DevToolsOverlayLog(L"route1.refresh restored collapse for %zu/%zu rows", restored, newKeys.size());
    }

    ApplyJmxAvailability();

    if (!savedQuery.empty()) ApplyTreeFilter(savedQuery);

    const bool selectionSurvived =
        g_selectedWire && g_nodeIdxByWire.find(g_selectedWire) != g_nodeIdxByWire.end();
    if (selectionSurvived) RevealSelectedRowAfterViewChange();
    else                   RestoreTreeScrollOffset(savedScroll);

    g_snapshotTotal = censusTotal ? censusTotal : g_allTreeNodes.size();
    SetTreeHeaderCurrent(g_allTreeNodes.size(), g_snapshotTotal);
    g_treeStale = false;
    DevToolsOverlayLog(L"route1.refresh rows now describe the live app: %zu nodes (census %zu) jmx=%d selKept=%d",
                  g_allTreeNodes.size(), g_snapshotTotal, (int)g_jmxActive, (int)selectionSurvived);
    // The tree changed, so the shown element's properties may have too (a Button's new content, say). An explicit
    // value refresh re-reads the pane itself.
    if (!refreshValues && g_nodeIdxByWire.count(g_selWire)) DevToolsWindow_RereadSelection(g_selWire);
    return true;
}
