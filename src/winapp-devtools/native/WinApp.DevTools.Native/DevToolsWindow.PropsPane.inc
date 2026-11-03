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

static void WireAddComment(void* frameworkElement);
static void UpdateDetailsHeader(const std::wstring& type, const std::wstring& name,
                                const std::wstring& label, const std::wstring& breadcrumb);

static void ShowPropsForNode(InstanceHandle wire, const std::wstring& label, const std::wstring& breadcrumb,
                             bool force)
{
    WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::WinPropPane);
    if (!g_ctx.propsHostChildren || !g_ctx.rowsFn) return;
    if (!force && wire != 0 && wire == g_selWire) return;
    // Save the focused editor before its pane goes away, while g_selWire still names the element it edits. Its
    // focus-out would otherwise arrive after the selection moved.
    std::wstring flushed;
    if (!g_flushingEditor) {
        const bool leaving = g_selWire != 0 && wire != g_selWire;
        const std::wstring saved = FlushFocusedEditor(leaving);
        if (leaving) flushed = saved;
        ReleasePaneEditors(/*retire*/ !leaving);
    }
    // After the flush: saving the editor can rebuild this pane, which moves the stashed draft into a new box.
    RetireKindEditors();
    RetireRowExpandSinks();
    StashCommentDraft();
    ReleasePropFilterState();
    ReleaseLayoutSectionRefs();
    g_propViewSelector = nullptr;
    g_sourceStatus = nullptr;
    // Selecting a DIFFERENT node resets the pane state that describes what happened to the selection's rows:
    // lost bindings and links, unconfirmed writes, staged kinds. Edit records are kept for the whole session, so
    // Restore still works after the user comes back to an element; only records for elements that are gone are dropped.
    if (wire != g_dirtyNode) {
        g_bindingLost.clear(); g_linkLost.clear(); g_pendingWrites.clear();
        g_sessionAssign.clear(); g_pendingKind.clear(); WalkForget(); g_dirtyNode = wire;
        if (g_ctx.wireLiveFn) {
            for (size_t i = g_editRecords.size(); i-- > 0; )
                if (!g_ctx.wireLiveFn(g_editRecords[i].wire)) g_editRecords.erase(g_editRecords.begin() + i);
            for (size_t i = g_appChanged.size(); i-- > 0; )
                if (!g_ctx.wireLiveFn(g_appChanged[i].wire)) g_appChanged.erase(g_appChanged.begin() + i);
        }
    }
    // The message footer outlives the fragment, so a message about the previous element ends with its selection.
    if (wire != g_selWire) SetStatusText(L"");
    g_selWire = wire; g_selLabel = label; g_selBreadcrumb = breadcrumb;

    std::wstring type, name; std::vector<DevToolsCardRow> rows;
    std::wstring authoredState, authoredXaml;
    { WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::WinPropRead);
      g_ctx.rowsFn(wire, type, name, rows, authoredState, authoredXaml); }
    ReconcileEditRecords(wire, rows);
    // The Reactor label (`<Counter>`, `VStack`) and C# name are the tree row's; the header shows them too.
    g_selReactorLabel.clear();
    g_selTreeName.clear();
    for (const auto& node : g_allTreeNodes) {
        if (node.handle != wire) continue;
        g_selReactorLabel = node.reactorLabel;
        g_selTreeName = node.name;
        break;
    }
    UpdateDetailsHeader(type, name, label, breadcrumb);

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

    g_selReferencesMarkup.clear();
    if (g_referencesFn && wire) g_selReferencesMarkup = g_referencesFn(wire);

    DevToolsWindowLayout layout; bool haveLayout = false;
    if (g_ctx.layoutFn && wire) haveLayout = g_ctx.layoutFn(wire, layout);
    g_paneLayout = layout; g_paneHaveLayout = haveLayout;

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

    // Wire the fragment: the property rows, the disclosures, the Computed|Set selector, the filter, and the
    // comment and source actions. Messages go to the window's footer, which outlives this fragment.
    QueryPerformanceCounter(&tParse1);
    void* fe = nullptr;
    if (SUCCEEDED(frag->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) && fe) {
        auto findByName = [&](const std::wstring& n) -> IInspectable* {
            HSTRING nm = MakeStr(n); IInspectable* f = nullptr;
            DevToolsFindName(fe, nm, &f); WindowsDeleteString(nm); return f;
        };
        const int repeaterItems = WirePropRepeater(fe);
        DevToolsOverlayLog(L"route1.props repeater items=%d rows=%zu", repeaterItems, g_propRows.size());
        g_reactorSection.Clear();
        // Reactor targets that aren't shown yet: only the agent knows, and it can't be asked on the UI thread, so they
        // are appended to the References section when the answer arrives.
        if (g_pendingReferencesFn && wire) {
            if (IInspectable* pr = findByName(L"PendingReferences")) {
                try {
                    winrt::Windows::Foundation::IInspectable host{ nullptr };
                    winrt::copy_from_abi(host, pr);
                    winrt::weak_ref<DevToolsXC::StackPanel> weakHost = host.as<DevToolsXC::StackPanel>();
                    auto queue = DevToolsD::DispatcherQueue::GetForCurrentThread();
                    std::thread([fetch = g_pendingReferencesFn, selected = wire, withHeader = g_selReferencesMarkup.empty(), queue, weakHost]() {
                        const std::wstring markup = fetch(selected, withHeader);
                        if (markup.empty() || !queue) return;
                        queue.TryEnqueue([markup, weakHost]() {
                            auto panel = weakHost.get();
                            if (!panel) return;
                            try { panel.Children().Append(DevToolsXMk::XamlReader::Load(markup).as<DevToolsX::UIElement>()); }
                            catch (const winrt::hresult_error&) {}
                        });
                    }).detach();
                } catch (const winrt::hresult_error&) {}
                pr->Release();
            }
        }
        if (IInspectable* rb = findByName(L"ReactorSectionBody")) {
            try {
                winrt::Windows::Foundation::IInspectable body{ nullptr };
                winrt::copy_from_abi(body, rb);
                const auto ask = g_reactorAskFn;
                g_reactorSection.Begin(body.as<DevToolsXC::Panel>(), static_cast<unsigned long long>(wire), !g_ctx.writeFn,
                    { L"{ThemeResource TextFillColorPrimaryBrush}", L"{ThemeResource TextFillColorSecondaryBrush}",
                      L"{ThemeResource AccentTextFillColorPrimaryBrush}", L"{ThemeResource SystemFillColorCautionBrush}" },
                    ask ? DevToolsComponentSection::AskFn([ask](unsigned long long w, const std::wstring& op, const std::wstring& arg,
                                                               const std::wstring& value, std::wstring* out) {
                        return ask(static_cast<InstanceHandle>(w), op, arg, value, out);
                    }) : DevToolsComponentSection::AskFn());
            } catch (const winrt::hresult_error&) {}
            rb->Release();
        }
        if (IInspectable* lb = findByName(L"LayoutSectionBody")) {
            lb->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), (void**)&g_layoutBody);
            lb->Release();
        }
        if (IInspectable* lt = findByName(L"LayoutSectionToggle")) {
            void* ex = nullptr;
            if (SUCCEEDED(lt->QueryInterface(DevToolsIid<DevToolsXC::IExpander>(), &ex)) && ex) {
                for (bool open : {true, false}) {
                    auto* sink = new LayoutSectionSink(); sink->Init(open);
                    __int64 tok = 0;
                    const HRESULT hr = open
                        ? DevToolsAbi<DevToolsXC::IExpander>(ex)->add_Expanding(sink, DevToolsAbiTok(&tok))
                        : DevToolsAbi<DevToolsXC::IExpander>(ex)->add_Collapsed(sink, DevToolsAbiTok(&tok));
                    if (FAILED(hr)) DevToolsOverlayLog(L"route1.props layout disclosure wiring failed hr=0x%08X", hr);
                }
                reinterpret_cast<IUnknown*>(ex)->Release();
            }
            lt->Release();
        }
        if (IInspectable* at = findByName(L"AxmlToggle")) {
            void* ex = nullptr;
            if (SUCCEEDED(at->QueryInterface(DevToolsIid<DevToolsXC::IExpander>(), &ex)) && ex) {
                for (bool open : {true, false}) {
                    auto* sink = new AxmlToggleSink(); sink->Init(open);
                    __int64 tok = 0;
                    const HRESULT hr = open
                        ? DevToolsAbi<DevToolsXC::IExpander>(ex)->add_Expanding(sink, DevToolsAbiTok(&tok))
                        : DevToolsAbi<DevToolsXC::IExpander>(ex)->add_Collapsed(sink, DevToolsAbiTok(&tok));
                    if (FAILED(hr)) DevToolsOverlayLog(L"route1.props XAML disclosure wiring failed hr=0x%08X", hr);
                }
                reinterpret_cast<IUnknown*>(ex)->Release();
            }
            at->Release();
        }
        if (IInspectable* pv = findByName(L"PropViewSelector")) {
            try {
                winrt::Windows::Foundation::IInspectable object{ nullptr };
                winrt::copy_from_abi(object, pv);
                if (auto selector = object.try_as<DevToolsXC::SelectorBar>()) {
                    g_propViewSelector = selector;
                    selector.SelectionChanged([gen = g_route1Gen](DevToolsXC::SelectorBar const& sender, auto&&) {
                        if (gen == g_route1Gen) ApplyPropViewSelection(sender);
                    });
                }
            } catch (const winrt::hresult_error& e) {
                DevToolsOverlayLog(L"route1.props view selector wiring failed hr=0x%08X -- Computed|Set will not switch",
                              e.code().value);
            }
            pv->Release();
        }
        {
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
        WireAddComment(fe);
        if (IInspectable* st = findByName(L"SourceStatus")) {
            winrt::Windows::Foundation::IInspectable object{ nullptr };
            winrt::copy_from_abi(object, st);
            st->Release();
            const auto status = object.as<DevToolsXC::TextBlock>();
            g_sourceStatus = status;
            for (const auto& [button, action] : {
                    std::pair{ L"OpenSource", SourceAction::Open },
                    std::pair{ L"RevealSource", SourceAction::Reveal },
                    std::pair{ L"CopySourcePath", SourceAction::Copy } }) {
                if (IInspectable* ob = findByName(button)) {
                    void* bb = nullptr;
                    if (SUCCEEDED(ob->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), &bb)) && bb) {
                        auto* sink = new SourceActionSink();
                        sink->Init(action, status);
                        __int64 token = 0;
                        const HRESULT hr = DevToolsAddClick(bb, sink, &token);
                        if (FAILED(hr)) DevToolsOverlayLog(L"route1.source %s add_Click failed hr=0x%08X", button, hr);
                        reinterpret_cast<IUnknown*>(bb)->Release();
                    }
                    ob->Release();
                }
            }
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
    if (!flushed.empty()) {
        SetStatusText(flushed);
        if (g_statusTb) {
            try {
                winrt::Windows::Foundation::IInspectable object{ nullptr };
                winrt::copy_from_abi(object, g_statusTb);
                if (auto peer = winrt::Microsoft::UI::Xaml::Automation::Peers::FrameworkElementAutomationPeer::FromElement(
                        object.as<DevToolsX::UIElement>()))
                    peer.RaiseNotificationEvent(winrt::Microsoft::UI::Xaml::Automation::Peers::AutomationNotificationKind::ActionCompleted,
                                                winrt::Microsoft::UI::Xaml::Automation::Peers::AutomationNotificationProcessing::ImportantMostRecent,
                                                flushed, L"DevToolsEditSaved");
            } catch (...) {}
        }
    }
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

// F5 rereads the tree, every visible row's caption included, and the selected element. The pane is live, so this is a
// fallback with no UI of its own; the pane is left alone while an edit is in progress.
static void RefreshInspectorSnapshot()
{
    if (!g_inspectorContent) return;
    try {
        winrt::Windows::Foundation::IInspectable object{ nullptr };
        winrt::copy_from_abi(object, g_inspectorContent);
        auto root = object.as<DevToolsX::FrameworkElement>();
        // A tree row with focus keeps it across the rebuild.
        InstanceHandle focusWire = 0;
        for (auto node = DevToolsXI::FocusManager::GetFocusedElement(root.XamlRoot()).try_as<DevToolsX::DependencyObject>();
             node && !focusWire; node = DevToolsXM::VisualTreeHelper::GetParent(node)) {
            for (const auto& entry : g_nodeIdxByWire) {
                if (entry.second >= g_treeControls.size() || !g_treeControls[entry.second]) continue;
                winrt::Windows::Foundation::IInspectable row{ nullptr };
                winrt::copy_from_abi(row, g_treeControls[entry.second]);
                if (row.as<DevToolsX::DependencyObject>() == node) { focusWire = entry.first; break; }
            }
        }
        const bool pane = DevToolsInspector::RefreshPaneOnSnapshot(!CanRereadSelection(nullptr), false);
        if (!RefreshTreeFromApp(true)) return;
        if (pane && g_selWire) DevToolsWindow_RereadSelection(g_selWire);
        else DevToolsOverlayLog(L"route1.snapshot F5 kept the properties pane: an edit is in progress");
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

// Window-wide keys: F5 re-reads the snapshot; Ctrl+Shift+P toggles pick mode, as the Pick button's tooltip says.
struct SnapshotRefreshSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void* args)
    {
        if (gen != g_route1Gen) return S_OK;
        int key = 0;
        if (!args || FAILED(DevToolsGetKey(args, &key))) return S_OK;
        if (key == 'P') {
            const bool ctrl = (GetKeyState(VK_CONTROL) & 0x8000) != 0, shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
            if (!ctrl || !shift || (GetKeyState(VK_MENU) & 0x8000)) return S_OK;
            DevToolsPutHandled(args, true);
            ToggleWindowPick();
            return S_OK;
        }
        if (key != VK_F5) return S_OK;
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
    RetireKindEditors();
    RetireRowExpandSinks();
    ReleaseInspectorCaptionTheme();
    if (HWND h = Route1WindowHwnd()) KillTimer(h, kRefreshTimerId);
    if (g_ctx.highlightFn) g_ctx.highlightFn(0, true);
    // Release the editable inspector state (message footer + props-host children).
    g_sourceStatus = nullptr;
    if (g_statusTb) { reinterpret_cast<IUnknown*>(g_statusTb)->Release(); g_statusTb = nullptr; }
    if (g_statusHostUi) { reinterpret_cast<IUnknown*>(g_statusHostUi)->Release(); g_statusHostUi = nullptr; }
    ReleaseLayoutSectionRefs(); ReleasePropFilterState();
    g_propViewSelector = nullptr;
    g_axmlExpanded = false;
    g_propFilterQuery.clear(); g_propFilterRaw.clear();
    RetirePaneEditors();
    g_editRecords.clear(); g_appChanged.clear(); g_dirtyNode = 0; g_propView = DevToolsPropView::Computed; g_propShowAll = false;
    g_bindingLost.clear(); g_linkedRows.clear(); g_linkLost.clear();
    g_sessionAssign.clear(); g_pendingKind.clear(); g_pendingWrites.clear();
    if (g_ctx.propsHostChildren) { g_ctx.propsHostChildren->Release(); g_ctx.propsHostChildren = nullptr; }
    g_selWire = 0; g_selLabel.clear(); g_selBreadcrumb.clear();
    g_selSourceFile.clear(); g_selSourceLine = 0;
    ReleaseTreeRowState();
    g_allTreeNodes.clear(); g_allTreeAppAuthored.clear();
    if (g_treeHostChildren) { g_treeHostChildren->Release(); g_treeHostChildren = nullptr; }
    g_treeA11y.clear();
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
    if (g_focusBtnCtrl) { reinterpret_cast<IUnknown*>(g_focusBtnCtrl)->Release(); g_focusBtnCtrl = nullptr; }
    if (g_pickOnUi) { reinterpret_cast<IUnknown*>(g_pickOnUi)->Release(); g_pickOnUi = nullptr; }
    if (g_focusOnUi) { reinterpret_cast<IUnknown*>(g_focusOnUi)->Release(); g_focusOnUi = nullptr; }
    if (g_focusNoticeUi) { reinterpret_cast<IUnknown*>(g_focusNoticeUi)->Release(); g_focusNoticeUi = nullptr; }
    if (g_focusNoticeTb) { reinterpret_cast<IUnknown*>(g_focusNoticeTb)->Release(); g_focusNoticeTb = nullptr; }
    if (g_treeNoMatchUi) { reinterpret_cast<IUnknown*>(g_treeNoMatchUi)->Release(); g_treeNoMatchUi = nullptr; }
    if (g_treeNoMatchTb) { reinterpret_cast<IUnknown*>(g_treeNoMatchTb)->Release(); g_treeNoMatchTb = nullptr; }
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
