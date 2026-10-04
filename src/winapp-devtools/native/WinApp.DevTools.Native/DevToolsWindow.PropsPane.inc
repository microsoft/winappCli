// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsWindow.cpp; included into that translation unit, not compiled on its own.

// A re-read rebuilds the pane. It is safe unless it would discard an edit: a queued write, a pending resource
// choice, or text the user is typing into a property editor (an editor commits when it loses focus). The property
// filter is not an edit: the rebuilt pane keeps its text, and *filterFocused asks the caller to focus it again.
static bool CanRereadSelection(bool* filterFocused)
{
    if (filterFocused) *filterFocused = false;
    if (!g_pendingWrites.empty() || !g_pendingKind.empty()) return false;
    if (!g_inspectorContent) return true;
    try {
        winrt::Windows::Foundation::IInspectable object{ nullptr };
        winrt::copy_from_abi(object, g_inspectorContent);
        auto root = object.as<DevToolsX::FrameworkElement>();
        bool input = false, filter = false;
        for (auto node = DevToolsXI::FocusManager::GetFocusedElement(root.XamlRoot()).try_as<DevToolsX::DependencyObject>();
             node; node = DevToolsXM::VisualTreeHelper::GetParent(node)) {
            input = input || node.try_as<DevToolsXC::TextBox>() || node.try_as<DevToolsXC::ComboBox>();
            auto fe = node.try_as<DevToolsX::FrameworkElement>();
            if (!fe) continue;
            filter = filter || fe.Name() == L"PropFilter";
            if (fe.Name() == L"PropsHost") {
                if (filterFocused) *filterFocused = filter;
                return !input || filter;
            }
        }
    } catch (const winrt::hresult_error&) {}
    return true;
}

// The quick-edit panel's open-in-DevTools buttons land here: expand the requested property, then scroll its row to
// the top of the pane once the repeater has laid it out. A pane rebuilt meanwhile (a binding answer arriving)
// restarts the scroll, so the row stays revealed. Focus stays put: moving it into a binding editor would start an
// edit the user did not ask for.
static std::wstring g_revealProp;
static UINT_PTR     g_revealTimer = 0;
static int          g_revealTries = 0;
static constexpr int kRevealTicks = 6;

static void CALLBACK RevealTimerProc(HWND, UINT, UINT_PTR id, DWORD)
{
    if (!g_revealProp.empty() && !PropIsExpanded(g_revealProp)) {
        PropSetExpanded(g_revealProp, true);
        RebuildPaneItems();
    }
    int index = -1;
    for (size_t k = 0; k < g_paneItems.size() && !g_revealProp.empty(); ++k) {
        const DevToolsPaneItem& it = g_paneItems[k];
        if (it.kind == DevToolsPaneItemKind::Row && it.rowIndex < g_propRows.size() &&
            g_propRows[it.rowIndex].name == g_revealProp) { index = (int)k; break; }
    }
    bool done = index < 0 || !g_propRepeater || !g_inspectorContent;
    if (!done) {
        try {
            winrt::Windows::Foundation::IInspectable repeaterObject{ nullptr }, rootObject{ nullptr };
            winrt::copy_from_abi(repeaterObject, g_propRepeater);
            winrt::copy_from_abi(rootObject, g_inspectorContent);
            const auto element = repeaterObject.as<DevToolsXC::ItemsRepeater>().GetOrCreateElement(index);
            const auto scroller = rootObject.as<DevToolsX::FrameworkElement>().FindName(L"PropsScroll").as<DevToolsXC::ScrollViewer>();
            scroller.UpdateLayout();
            // The row's own header line, with a little room above it, at the top of the pane.
            const auto top = element.TransformToVisual(scroller.Content().as<DevToolsX::UIElement>())
                .TransformPoint(winrt::Windows::Foundation::Point{ 0, 0 }).Y;
            scroller.ChangeView(nullptr, winrt::box_value((std::max)(0.0, static_cast<double>(top) - 8))
                .as<winrt::Windows::Foundation::IReference<double>>(), nullptr, true);
            DevToolsOverlayLog(L"route1.props revealed '%s' at item %d (y=%.0f)", g_revealProp.c_str(), index, top);
            // A window that is still sizing itself lays the pane out again, so settle over a few ticks.
            done = ++g_revealTries >= kRevealTicks;
        } catch (...) {
            done = ++g_revealTries >= kRevealTicks;
        }
    }
    if (done) {
        KillTimer(nullptr, id);
        g_revealTimer = 0;
        g_revealProp.clear();
    }
}

static void ApplyPendingReveal()
{
    std::wstring prop;
    if (!DevToolsOverlay_TakeInspectorReveal(&prop) || prop.empty()) {
        // A rebuild while a reveal is settling starts its scroll again.
        if (!g_revealProp.empty()) g_revealTries = 0;
        return;
    }
    if (!FindPropertyRow(g_propRows, prop)) {
        DevToolsOverlayLog(L"route1.props reveal '%s': the selected element has no such row", prop.c_str());
        return;
    }
    if (!PropIsExpanded(prop)) {
        PropSetExpanded(prop, true);
        RebuildPaneItems();
    }
    g_revealProp = prop;
    g_revealTries = 0;
    g_revealTimer = SetTimer(nullptr, g_revealTimer, 150, &RevealTimerProc);
}

static void ShowPropsForNode(InstanceHandle wire, const std::wstring& label, const std::wstring& breadcrumb,
                             bool force)
{
    WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::WinPropPane);
    if (!g_ctx.propsHostChildren || !g_ctx.rowsFn) return;
    if (!force && wire != 0 && wire == g_selWire) return;
    for (auto& r : g_editRows) if (r.box) reinterpret_cast<IUnknown*>(r.box)->Release();
    g_editRows.clear();
    ReleasePropFilterState();
    ReleaseBreadcrumbRefs();
    ReleaseLayoutSectionRefs();
    if (g_statusTb) { reinterpret_cast<IUnknown*>(g_statusTb)->Release(); g_statusTb = nullptr; }
    // Selecting a DIFFERENT node starts a fresh dirty-tracking session (its changed markers reflect only edits made
    // while it stays selected). A reset/undo click rebuilds the pane for the SAME wire and must preserve the set.
    // The binding-lost list and any un-confirmed pending write are per-node for the same reason: they describe
    // what happened to THIS element's rows while it was selected.
    if (wire != g_dirtyNode) { g_dirtySeed.clear(); g_bindingLost.clear(); g_linkLost.clear(); g_pendingWrites.clear();
                               g_sessionAssign.clear(); g_pendingKind.clear(); WalkForget(); g_dirtyNode = wire; }
    g_selWire = wire; g_selLabel = label; g_selBreadcrumb = breadcrumb;

    std::wstring type, name; std::vector<DevToolsCardRow> rows;
    std::wstring authoredState, authoredXaml;
    { WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::WinPropRead);
      g_ctx.rowsFn(wire, type, name, rows, authoredState, authoredXaml); }

    const PaneEditMap emap = BuildEditMap(rows);

    // Recompute resource-link state from authoredKind, including inherited parent linkage on child
// property paths; runtime local values alone cannot establish that provenance.
    g_linkedRows.clear();
    for (const auto& r : rows) {
        if (r.authoredKind != L"themeResource" && r.authoredKind != L"staticResource") continue;
        g_linkedRows.push_back(r.name);
        for (const auto& c : r.children) g_linkedRows.push_back(r.name + L"." + c.name);
    }

    g_selSourceFile.clear(); g_selSourceLine = 0;
    if (g_ctx.sourceFn) {
        std::wstring f; unsigned int ln = 0, col = 0;
        if (g_ctx.sourceFn(wire, f, ln, col)) { g_selSourceFile = f; g_selSourceLine = ln; }
    }

    DevToolsWindowLayout layout; bool haveLayout = false;
    if (g_ctx.layoutFn && wire) haveLayout = g_ctx.layoutFn(wire, layout);

    IInspectable* frag = nullptr;
    LARGE_INTEGER freq{}, tAuthor0{}, tAuthor1{}, tParse1{}, tWire1{};
    QueryPerformanceFrequency(&freq);
    QueryPerformanceCounter(&tAuthor0);
    // Set row model before authoring markup; WirePropRepeater consumes parsed controls against these indices.
g_propRows = rows;
    g_paneEditMap = emap;
    g_propRowLabels.clear();
    g_propRowLabels.reserve(rows.size());
    for (const auto& r : rows) {
        std::wstring lab = r.name + L" " + r.value + L" " + r.authored;
        for (auto& c : lab) if (c >= L'A' && c <= L'Z') c = (wchar_t)(c + 32);
        g_propRowLabels.push_back(std::move(lab));
    }
    g_propRowTotal = rows.size();
    BuildPaneItems(g_propRows);

    const std::wstring authored = BuildPropsFragment(type, name, label, breadcrumb, g_selSourceFile,
                                                     g_selSourceLine, rows, authoredState, authoredXaml,
                                                     haveLayout ? &layout : nullptr);
    // the parse pays per resource REFERENCE, and that price rises for the life of the process.
    // Resolve them to literals here, once per key per theme, so the parse below asks for none.
    size_t askedRefs = 0, leftRefs = 0;
    const std::wstring markup = InlineResourceLiterals(authored, &askedRefs, &leftRefs);
    QueryPerformanceCounter(&tAuthor1);
    if (FAILED(LoadMarkupWin(markup, &frag)) || !frag) {
        DevToolsOverlayLog(L"route1.props fragment load failed");
        // Defense-in-depth: never leave a stale, half-wired pane (its Apply button would still be wired
        // to now-released edit rows). Clear the pane and show a constant, value-free error line built from
        // markup that cannot itself fail to parse.
        DevToolsVecClear(g_ctx.propsHostChildren);
        IInspectable* errFrag = nullptr;
        if (SUCCEEDED(LoadMarkupWin(
                L"<TextBlock xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\""
                L" Text=\"Unable to render properties for this node.\" Foreground=\"#FFD08770\""
                L" FontSize=\"12\" Margin=\"12,12,12,12\" TextWrapping=\"Wrap\"/>",
                &errFrag)) && errFrag) {
            void* eui = nullptr;
            if (SUCCEEDED(errFrag->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &eui)) && eui) {
                DevToolsVecAppend(g_ctx.propsHostChildren, reinterpret_cast<IInspectable*>(eui));
                reinterpret_cast<IUnknown*>(eui)->Release();
            }
            errFrag->Release();
        }
        return;
    }

    // Wire the fragment: FindName the Status line + each editable TextBox (E<i>), and attach a per-row
    // LostFocus commit sink so leaving a field writes it to the live app element (no Apply button).
    QueryPerformanceCounter(&tParse1);
    void* fe = nullptr;
    if (SUCCEEDED(frag->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) && fe) {
        auto findByName = [&](const std::wstring& n) -> IInspectable* {
            HSTRING nm = MakeStr(n); IInspectable* f = nullptr;
            DevToolsFindName(fe, nm, &f); WindowsDeleteString(nm); return f;
        };
        const int repeaterItems = WirePropRepeater(fe);
        DevToolsOverlayLog(L"route1.props repeater items=%d rows=%zu", repeaterItems, g_propRows.size());
        if (IInspectable* st = findByName(L"Status")) {
            void* tb = nullptr;
            if (SUCCEEDED(st->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &tb)) && tb) g_statusTb = reinterpret_cast<IInspectable*>(tb);
            st->Release();
        } else {
            bool anyEditableRow = false;
            for (const auto& r2 : rows) if (!r2.type.empty()) { anyEditableRow = true; break; }
            // BuildPropsFragment emits the Status line whenever ANY row is editable, so rows-editable plus
            // no-Status means the emitter and this wiring have drifted. SetStatusText silently no-ops on a
            // null g_statusTb, so EVERY status message would disappear -- including "Replaced the binding on
            // X, that can't be undone", the one message whose whole job is to stop a user believing a
            // destructive edit was harmless. Losing that quietly is worse than losing the line.
            if (anyEditableRow)
                DevToolsOverlayLog(L"route1.wire MISS: rows are editable but there is no Status line -- every status message will be swallowed");
        }
        // Breadcrumb expander: hold both TextBlocks as IUIElement and wire the "Show all N ancestors"
        // HyperlinkButton to flip between them. Only present when the chain was actually long enough to elide.
        if (IInspectable* bs = findByName(L"BcShort")) {
            bs->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &g_bcShort);
            bs->Release();
        }
        if (IInspectable* bf = findByName(L"BcFull")) {
            bf->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &g_bcFull);
            bf->Release();
        }
        if (g_bcShort && g_bcFull) {
            DevToolsPutVisibility(g_bcShort, g_bcExpanded ? 1 : 0);
            DevToolsPutVisibility(g_bcFull, g_bcExpanded ? 0 : 1);
            if (IInspectable* bl = findByName(L"BcToggleLbl")) {
                bl->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &g_bcToggleLbl);
                bl->Release();
            }
            if (IInspectable* bt = findByName(L"BcToggle")) {
                void* bb = nullptr;
                if (SUCCEEDED(bt->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), &bb)) && bb) {
                    BreadcrumbSink* bcs = new BreadcrumbSink(); bcs->Init();
                    __int64 tok = 0; void** bvt = *reinterpret_cast<void***>(bb);
                    DevToolsAddClick(bb, bcs, &tok);
                    reinterpret_cast<IUnknown*>(bb)->Release();
                }
                bt->Release();
            }
        }
        if (IInspectable* lb = findByName(L"LayoutSectionBody")) {
            lb->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), (void**)&g_layoutBody);
            lb->Release();
        }
        if (IInspectable* lc = findByName(L"LayoutSectionChev")) {
            lc->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), (void**)&g_layoutBodyChev);
            lc->Release();
        }
        if (IInspectable* lt = findByName(L"LayoutSectionToggle")) {
            void* bb = nullptr;
            if (SUCCEEDED(lt->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), &bb)) && bb) {
                LayoutSectionSink* sink = new LayoutSectionSink(); sink->Init();
                __int64 tok = 0;
                DevToolsAddClick(bb, sink, &tok);
                reinterpret_cast<IUnknown*>(bb)->Release();
            }
            lt->Release();
        }
        if (IInspectable* a1 = findByName(L"AxmlOne")) {
            a1->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), (void**)&g_axmlOne);
            a1->Release();
        }
        if (IInspectable* af = findByName(L"AxmlFull")) {
            af->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), (void**)&g_axmlFull);
            af->Release();
        }
        if (IInspectable* ac = findByName(L"AxmlChev")) {
            ac->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), (void**)&g_axmlChev);
            ac->Release();
        }
        if (g_axmlOne && g_axmlFull && g_axmlExpanded) {
            DevToolsPutVisibility(g_axmlOne, 1);
            DevToolsPutVisibility(g_axmlFull, 0);
            SetToggleLabel(g_axmlChev, L"\uE70D");
        }
        if (IInspectable* at = findByName(L"AxmlToggle")) {
            void* bb = nullptr;
            if (SUCCEEDED(at->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), &bb)) && bb) {
                AxmlToggleSink* ats = new AxmlToggleSink(); ats->Init();
                __int64 tok = 0;
                DevToolsAddClick(bb, ats, &tok);
                reinterpret_cast<IUnknown*>(bb)->Release();
            }
            at->Release();
        }
        // Wire the view switch. BOTH halves get the same sink: it flips the view, so clicking the one that
        // is already active is a no-op flip back and forth -- which is wrong. The sink therefore SETS the
        // view rather than toggling it, keyed off which button was found.
        for (const wchar_t* n : { L"OnlySetToggle", L"ViewComputed" }) {
            if (IInspectable* tg = findByName(n)) {
                void* bb = nullptr;
                if (SUCCEEDED(tg->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), &bb)) && bb) {
                    OnlySetToggleSink* os = new OnlySetToggleSink();
                    os->Init(wcscmp(n, L"OnlySetToggle") == 0 ? DevToolsPropView::Set : DevToolsPropView::Computed);
                    __int64 tok = 0;
                    DevToolsAddClick(bb, os, &tok);
                    reinterpret_cast<IUnknown*>(bb)->Release();
                }
                tg->Release();
            }
        }
        {
            if (IInspectable* pc = findByName(L"PropCount")) {
                pc->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &g_propCountTb);
                g_propCountEl = pc;
            }
            if (IInspectable* sl = findByName(L"PropShowAllLbl")) {
                sl->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &g_propShowAllLbl);
                sl->Release();
            }
            if (IInspectable* sa = findByName(L"PropShowAll")) {
                void* bb = nullptr;
                if (SUCCEEDED(sa->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), &bb)) && bb) {
                    PropShowAllSink* ps = new PropShowAllSink(); ps->Init();
                    __int64 tok = 0;
                    const HRESULT hrS = DevToolsAddClick(bb, ps, &tok);
                    if (FAILED(hrS)) DevToolsOverlayLog(L"route1.wire PropShowAll add_Click FAILED hr=0x%08X -- the full list is unreachable", hrS);
                    reinterpret_cast<IUnknown*>(bb)->Release();
                }
                sa->Release();
            }
            if (IInspectable* pf = findByName(L"PropFilter")) {
                void* tbx = nullptr;
                if (SUCCEEDED(pf->QueryInterface(DevToolsIid<DevToolsXC::ITextBox>(), &tbx)) && tbx) {
                    g_propFilterTextBox = tbx;
                    PropFilterSink* fs = new PropFilterSink(); fs->Init();
                    __int64 tok = 0;
                    HRESULT hrF = DevToolsAddTextChanged(tbx, fs, &tok);
                    if (FAILED(hrF)) DevToolsOverlayLog(L"route1.wire propfilter add_TextChanged FAILED hr=0x%08X -- the filter will do nothing", hrF);
                }
                pf->Release();
            }
            if (IInspectable* nm = findByName(L"PropNoMatch")) {
                nm->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &g_propEmptyUi);
                nm->Release();
            }
            if (IInspectable* nl = findByName(L"PropNoMatchLbl")) {
                nl->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &g_propEmptyLbl);
                nl->Release();
            }
            if (IInspectable* cf = findByName(L"PropClearFilter")) {
                cf->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &g_propEmptyClearUi);
                void* bb = nullptr;
                if (SUCCEEDED(cf->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), &bb)) && bb) {
                    PropClearFilterSink* cs = new PropClearFilterSink(); cs->Init();
                    __int64 tok = 0;
                    const HRESULT hrC = DevToolsAddClick(bb, cs, &tok);
                    if (FAILED(hrC)) DevToolsOverlayLog(L"route1.wire PropClearFilter add_Click FAILED hr=0x%08X -- the empty pane has no way back", hrC);
                    reinterpret_cast<IUnknown*>(bb)->Release();
                }
                cf->Release();
            }
            if (!g_propEmptyUi)
                DevToolsOverlayLog(L"route1.wire PropNoMatch MISS -- an empty pane will render blank and say nothing");
            ApplyPropFilter(g_propFilterQuery);
        }
        // Source "Open" button: wire its Click to an OpenSourceSink (present only when the node had resolvable
        // source under WINAPP_DEVTOOLS_SOURCE_ROOT). Independent of editable rows -- a read-only node can still
        // link to source. Leaked/gen-guarded like the other sinks.
        if (IInspectable* ob = findByName(L"OpenSource")) {
            void* bb = nullptr;
            if (SUCCEEDED(ob->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), &bb)) && bb) {
                OpenSourceSink* os = new OpenSourceSink();
                os->Init();
                __int64 otok = 0;
                void** bvt = *reinterpret_cast<void***>(bb);
                DevToolsAddClick(bb, os, &otok);
                reinterpret_cast<IUnknown*>(bb)->Release();
            }
            ob->Release();
        }
        reinterpret_cast<IUnknown*>(fe)->Release();
    }

    unsigned int oldCount = 0;
    DevToolsVecGetSize(g_ctx.propsHostChildren, &oldCount);
    void* ui = nullptr;
    if (SUCCEEDED(frag->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) && ui) {
        DevToolsVecAppend(g_ctx.propsHostChildren, reinterpret_cast<IInspectable*>(ui));
        reinterpret_cast<IUnknown*>(ui)->Release();
        for (unsigned int i = 0; i < oldCount; ++i) DevToolsVecRemoveAt(g_ctx.propsHostChildren, 0);
    } else {
        DevToolsVecClear(g_ctx.propsHostChildren);
    }
    frag->Release();
    QueryPerformanceCounter(&tWire1);
    const double ms = freq.QuadPart ? 1000.0 / (double)freq.QuadPart : 0.0;
    DevToolsOverlayLog(L"route1.props shown type=%s name=%s rows=%zu items=%zu markup=%zuB refsAsked=%zu refsLeft=%zu author=%.1fms parse=%.1fms wire=%.1fms",
                  type.c_str(), name.c_str(), rows.size(), g_paneItems.size(), markup.size(),
                  askedRefs, leftRefs,
                  (double)(tAuthor1.QuadPart - tAuthor0.QuadPart) * ms,
                  (double)(tParse1.QuadPart - tAuthor1.QuadPart) * ms,
                  (double)(tWire1.QuadPart - tParse1.QuadPart) * ms);
    ApplyPendingReveal();
    LiveWatchSelection();
}

// Capture canonical XamlRoot identity for inspector census exclusion. If unavailable, retain
// seed-based exclusion until a later live-root read succeeds.
static void CaptureInspectorXamlRoot(IInspectable* content);

// Tear down all Route-1 window state after the inspector window is closed (or found dead). Releases the
// refs we hold into the (now torn-down) inspector tree and bumps the generation so any leaked click sink
// goes inert, so the next Inspect click rebuilds a FRESH window instead of re-activating a destroyed one
// (which is a silent no-op and left the user unable to reopen). UI-thread only (same thread as Open).
// Release the comments view's cached refs (defined with the comments block below).
static void ResetCommentsViewState();

// F5 re-reads the tree and the selected element in full. The pane is live, so this is a fallback and has no UI of
// its own; it does nothing while an edit is in progress.
static void RefreshInspectorSnapshot()
{
    if (!g_inspectorContent) return;
    try {
        winrt::Windows::Foundation::IInspectable object{ nullptr };
        winrt::copy_from_abi(object, g_inspectorContent);
        auto root = object.as<DevToolsX::FrameworkElement>();
        auto focused = DevToolsXI::FocusManager::GetFocusedElement(root.XamlRoot());
        bool input = false, inProperties = false, inTree = false;
        InstanceHandle focusWire = 0;
        auto ancestor = focused.try_as<DevToolsX::DependencyObject>();
        while (ancestor) {
            input = input || bool(ancestor.try_as<DevToolsXC::TextBox>()) ||
                bool(ancestor.try_as<DevToolsXC::ComboBox>()) || bool(ancestor.try_as<DevToolsXC::ToggleSwitch>());
            if (auto fe = ancestor.try_as<DevToolsX::FrameworkElement>()) {
                inProperties = inProperties || fe.Name() == L"PropsHost" || fe.Name() == L"PropsScroll";
                inTree = inTree || fe.Name() == L"TreeRowsHost";
            }
            if (!focusWire) {
                for (const auto& entry : g_nodeIdxByWire) {
                    if (entry.second >= g_treeControls.size() || !g_treeControls[entry.second]) continue;
                    winrt::Windows::Foundation::IInspectable row{ nullptr };
                    winrt::copy_from_abi(row, g_treeControls[entry.second]);
                    if (row.as<DevToolsX::DependencyObject>() == ancestor) { focusWire = entry.first; break; }
                }
            }
            ancestor = DevToolsXM::VisualTreeHelper::GetParent(ancestor);
        }
        bool openEditor = false;
        for (const auto& path : g_propExpanded) {
            const auto* row = FindPropertyRow(g_propRows, path);
            if (row && !row->type.empty()) { openEditor = true; break; }
        }
        if (!DevToolsInspector::CanRefreshSnapshot(input, inProperties || (inTree && !focusWire),
                openEditor || !g_pendingWrites.empty() || !g_pendingKind.empty())) {
            DevToolsOverlayLog(L"route1.snapshot F5 skipped: an edit is in progress");
            return;
        }
        auto scroller = root.FindName(L"PropsScroll").as<DevToolsXC::ScrollViewer>();
        const double scroll = scroller.VerticalOffset();
        const auto wire = g_selWire;
        const auto label = g_selLabel, breadcrumb = g_selBreadcrumb;
        if (!RefreshTreeFromApp(true)) return;
        if (wire) ShowPropsForNode(wire, label, breadcrumb, true);
        scroller.ChangeView(nullptr, winrt::box_value(scroll).as<winrt::Windows::Foundation::IReference<double>>(), nullptr, true);
        if (focusWire) {
            auto row = g_nodeIdxByWire.find(focusWire);
            if (row != g_nodeIdxByWire.end() && row->second < g_treeControls.size() && g_treeControls[row->second]) {
                void* ui = nullptr;
                if (SUCCEEDED(static_cast<IUnknown*>(g_treeControls[row->second])->QueryInterface(
                        DevToolsIid<DevToolsX::IUIElement>(), &ui)) && ui) {
                    bool moved = false;
                    DevToolsFocus(ui, 2 /*Keyboard*/, &moved);
                    reinterpret_cast<IUnknown*>(ui)->Release();
                }
            }
        }
    } catch (...) {
        DevToolsOverlayLog(L"route1.snapshot F5 refresh failed");
    }
}

struct SnapshotRefreshSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void* args)
    {
        if (gen != g_route1Gen) return S_OK;
        int key = 0;
        if (!args || FAILED(DevToolsGetKey(args, &key)) || key != VK_F5) return S_OK;
        DevToolsPutHandled(args, true);
        RefreshInspectorSnapshot();
        return S_OK;
    }
    void Init() { InitSink(IID_KeyEventHandler_Del, g_route1Gen); }
};

// Bump generation before releasing UI refs so in-flight callbacks become inert before teardown.
static void ResetRoute1State()
{
    LiveUnwatch();
    g_route1Gen++;
    ReleaseInspectorCaptionTheme();
    if (HWND h = Route1WindowHwnd()) { KillTimer(h, kRefreshTimerId); KillTimer(h, kDisarmAfterWindowPickTimerId); }
    if (g_ctx.highlightFn) g_ctx.highlightFn(0, true);
    // Release the editable inspector state (tracked TextBox refs + status line + props-host children).
    for (auto& r : g_editRows) if (r.box) reinterpret_cast<IUnknown*>(r.box)->Release();
    g_editRows.clear();
    if (g_statusTb) { reinterpret_cast<IUnknown*>(g_statusTb)->Release(); g_statusTb = nullptr; }
    ReleaseBreadcrumbRefs(); ReleaseLayoutSectionRefs(); ReleasePropFilterState();
    g_bcExpanded = false; g_axmlExpanded = false;
    g_propFilterQuery.clear(); g_propFilterRaw.clear();
    g_dirtySeed.clear(); g_dirtyNode = 0; g_propView = DevToolsPropView::Computed; g_propShowAll = false;
    g_bindingLost.clear(); g_linkedRows.clear(); g_linkLost.clear();
    g_sessionAssign.clear(); g_pendingKind.clear(); g_pendingWrites.clear();
    if (g_ctx.propsHostChildren) { g_ctx.propsHostChildren->Release(); g_ctx.propsHostChildren = nullptr; }
    g_selWire = 0; g_selLabel.clear(); g_selBreadcrumb.clear();
    g_selSourceFile.clear(); g_selSourceLine = 0;
    // Release tree-selection visual state. g_selCtrl points at a Button from the window being torn down --
    // it MUST be dropped here so the next window's first select doesn't put_Background on a freed control.
    if (g_selCtrl)    { reinterpret_cast<IUnknown*>(g_selCtrl)->Release(); g_selCtrl = nullptr; }
    ReleaseTreeRowState();
    g_allTreeNodes.clear(); g_allTreeAppAuthored.clear();
    if (g_treeHostChildren) { g_treeHostChildren->Release(); g_treeHostChildren = nullptr; }
    if (g_treeRowsPanelChildren) { g_treeRowsPanelChildren->Release(); g_treeRowsPanelChildren = nullptr; }
    g_treeA11y.clear(); g_treeCollapseAtBuild.clear(); g_treeViewDepth.clear();
    // The row palette belongs to the window being torn down. Holding it across a close would leave
    // the next window's rows reading brushes out of a dead XamlRoot.
    for (IInspectable** slot : { &g_palTransparent, &g_palAccent, &g_palPrimary, &g_palSecondary,
                                 &g_palTertiary, &g_palMonoFont, &g_palGlyphFont, &g_palCaption }) {
        if (*slot) { (*slot)->Release(); *slot = nullptr; }
    }
    for (IInspectable** slot : { &g_tmplHeader, &g_tmplRow, &g_tmplExpRead, &g_tmplExpEdit }) {
        if (*slot) { (*slot)->Release(); *slot = nullptr; }
    }
    g_propExpanded.clear();
    if (g_propRepeater) { reinterpret_cast<IUnknown*>(g_propRepeater)->Release(); g_propRepeater = nullptr; }
    g_propRows.clear(); g_paneItems.clear();
    g_treeFilterQuery.clear(); g_selectedWire = 0;
    // Release the toolbar-toggle refs and reset filter/pick/focus state. If a mode was active when the window
    // tore down, disable it so the app is not left with a catcher or background focus tracking.
    g_treeAppAuthored.clear();
    if (g_jmxLbl)  { reinterpret_cast<IUnknown*>(g_jmxLbl)->Release();  g_jmxLbl  = nullptr; }
    if (g_jmxBtnCtrl) { reinterpret_cast<IUnknown*>(g_jmxBtnCtrl)->Release(); g_jmxBtnCtrl = nullptr; }
    if (g_pickBtnCtrl) { reinterpret_cast<IUnknown*>(g_pickBtnCtrl)->Release(); g_pickBtnCtrl = nullptr; }
    if (g_pickOnUi) { reinterpret_cast<IUnknown*>(g_pickOnUi)->Release(); g_pickOnUi = nullptr; }
    if (g_focusOnUi) { reinterpret_cast<IUnknown*>(g_focusOnUi)->Release(); g_focusOnUi = nullptr; }
    if (g_focusNoticeUi) { reinterpret_cast<IUnknown*>(g_focusNoticeUi)->Release(); g_focusNoticeUi = nullptr; }
    if (g_focusNoticeTb) { reinterpret_cast<IUnknown*>(g_focusNoticeTb)->Release(); g_focusNoticeTb = nullptr; }
    if (g_pickRoute.IsWindowPickArmed()) { DevToolsOverlay_DisarmPick(); g_pickRoute.CancelWindowPick(); }
    if (g_hoverRevertTimer) { KillTimer(nullptr, g_hoverRevertTimer); g_hoverRevertTimer = 0; }
    if (g_focusActive) { DevToolsOverlay_SetFocusTracking(false); g_focusActive = false; }
    g_jmxActive = false; g_jmxAvailable = false;
    if (g_filterBox) { g_filterBox->Release(); g_filterBox = nullptr; }
    if (g_filterTextBox) { reinterpret_cast<IUnknown*>(g_filterTextBox)->Release(); g_filterTextBox = nullptr; }
    // Release splitter references too: retaining the shell root retains the entire inspector tree.
    if (g_splitPaneFe) { reinterpret_cast<IUnknown*>(g_splitPaneFe)->Release(); g_splitPaneFe = nullptr; }
    if (g_splitHostFe) { reinterpret_cast<IUnknown*>(g_splitHostFe)->Release(); g_splitHostFe = nullptr; }
    g_splitTarget = 0.0;
    if (g_selBrush)   { g_selBrush->Release();   g_selBrush   = nullptr; }
    if (g_clearBrush) { g_clearBrush->Release(); g_clearBrush = nullptr; }
    g_selBrushTried = false;
    g_ctx.rowsFn = nullptr;
    g_ctx.readInputFn = nullptr;
    g_ctx.writeFn = nullptr;
    g_ctx.clearFn = nullptr;
    g_ctx.resolveFn = nullptr;
    g_ctx.bindFn = nullptr;
    g_ctx.sourceFn = nullptr;
    g_ctx.layoutFn = nullptr;
    g_ctx.highlightFn = nullptr;
    g_ctx.wireLiveFn = nullptr;
    g_ctx.censusCountFn = nullptr;
    g_ctx.snapshotFn = nullptr;
    g_ctx.previewFn = nullptr;
    g_treeRefreshing = false;
    ResetCommentsViewState();
    if (g_inspectorXamlRoot) { g_inspectorXamlRoot->Release(); g_inspectorXamlRoot = nullptr; }
    if (g_inspectorContent)  { g_inspectorContent->Release();  g_inspectorContent  = nullptr; }
    if (g_route1WindowInsp)  { g_route1WindowInsp->Release();  g_route1WindowInsp  = nullptr; }
    g_watchdogSawApp = false;
    g_route1WindowHidden = false;
    DevToolsOverlayLog(L"route1.reset teardown complete (gen=%u excluded=%zu)", g_route1Gen, DevToolsTap_ExcludedElementCount());
}
