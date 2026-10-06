// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsWindow.cpp; included into that translation unit, not compiled on its own.

// Drop the window-lifetime refs the comments view holds. The BRIDGE is not cleared: the tap registers it once
// per process, and the next window opens with the same source of truth.
static void ResetCommentsViewState()
{
    if (g_commentsHostChildren) { g_commentsHostChildren->Release(); g_commentsHostChildren = nullptr; }
    auto drop = [](void** slot) { if (*slot) { reinterpret_cast<IUnknown*>(*slot)->Release(); *slot = nullptr; } };
    drop(&g_treeScrollUi);
    drop(&g_commentsScrollUi);
    drop(&g_treeControlsUi);
    drop(&g_commentsLbl);
    drop(&g_commentsBadgeUi);
    drop(&g_treeTabBtn);
    drop(&g_commentsTabBtn);
    ReleaseCommentRowUi();
    g_commentRows.clear();
    g_commentRowLive.clear();
    g_commentsView = false;
}

void DevToolsWindow_SetCommentBridge(DevToolsWindowCommentsFn commentsFn,
                                DevToolsWindowAnchorWireFn anchorWireFn,
                                DevToolsWindowCommentResolveFn resolveFn)
{
    g_commentsFn = commentsFn;
    g_anchorWireFn = anchorWireFn;
    g_commentResolveFn = resolveFn;
}

void DevToolsWindow_SetPathWalk(DevToolsWindowWalkFn walkFn) { g_walkFn = walkFn; }

void DevToolsWindow_RefreshComments()
{
    if (!DevToolsWindow_IsOpen()) return;
    if (g_commentsView) RefreshCommentsPane();
    else                UpdateCommentsButtonLabel();
}

HRESULT DevToolsWindow_Open(IXamlDiagnostics* diag,
                       const std::vector<DevToolsWindowNode>& nodes,
                       size_t censusTotal,
                       DevToolsWindowRowsFn rowsFn,
                       DevToolsWindowReadInputFn readInputFn,
                       DevToolsWindowWriteFn writeFn,
                       DevToolsWindowClearFn clearFn,
                       DevToolsWindowResolveFn resolveFn,
                       DevToolsWindowBindFn bindFn,
                       DevToolsWindowSourceFn sourceFn,
                       DevToolsWindowLayoutFn layoutFn,
                       DevToolsWindowHighlightFn highlightFn,
                       DevToolsWindowWireLiveFn wireLiveFn,
                       DevToolsWindowCensusCountFn censusCountFn,
                       DevToolsWindowSnapshotFn snapshotFn,
                       DevToolsWindowPreviewFn previewFn,
                       DevToolsSeedFn seed,
                       InstanceHandle preselectWire,
                       std::wstring* errorMessage)
{
    if (errorMessage) errorMessage->clear();
    // Idempotent: a Route-1 window already exists. If it's still a LIVE OS window, just re-activate (focus)
    // it. If the user has CLOSED it (HWND destroyed), a WinUI Window cannot be re-activated -- doing so is a
    // silent no-op, which is exactly why "couldn't reopen" happened. Detect that, tear down the stale state,
    // and fall through to rebuild a fresh window.
    if (g_route1WindowInsp) {
        if (Route1WindowAlive()) {
            // A hidden reused window must reread the app tree before reveal; hidden rows do not refresh themselves.
if (g_route1WindowHidden) {
                if (HWND h = Route1WindowHwnd()) {
                    ShowWindow(h, IsIconic(h) ? SW_RESTORE : SW_SHOW);
                }
                g_route1WindowHidden = false;
                RefreshTreeFromApp();
                // Put the app-side selection chrome back on the retained selection. The close cleared it
                // (ResetAppVisibleModes) because leaving an adorner on the app with no window to turn it off
                // is worse; but the inspector still shows that element selected and its properties, so
                // without this the two disagree -- the tree says "selected", the app shows nothing. Not done
                // via SelectNodeByWire: it early-outs on the unchanged wire, which is precisely this case.
                if (g_selWire && g_ctx.wireLiveFn && g_ctx.highlightFn && g_ctx.wireLiveFn(g_selWire)) {
                    g_ctx.highlightFn(g_selWire, true);
                }
                DevToolsOverlayLog(L"route1.open re-showing the hidden window (reused instance)");
            }
            void* win = nullptr;
            if (SUCCEEDED(g_route1WindowInsp->QueryInterface(DevToolsIid<DevToolsX::IWindow>(), &win)) && win) {
                DevToolsWinActivate(win);
                reinterpret_cast<IUnknown*>(win)->Release();
            }
            DevToolsOverlayLog(L"route1.open re-activate existing (live) window");
            SelectNodeByWire(preselectWire);
            // The app may have changed this element since the window last read it.
            if (preselectWire && preselectWire == g_selWire) {
                WalkForget();
                ShowPropsForNode(g_selWire, g_selLabel, g_selBreadcrumb, /*force*/ true);
            }
            LiveWatchSelection();
            return S_OK;
        }
        DevToolsOverlayLog(L"route1.open existing window was closed; rebuilding");
        ResetRoute1State();
    }

    g_allTreeNodes = nodes;
    const int appCount = ClassifyTreeSnapshot();
    g_jmxAvailable = appCount > 0;
    g_jmxActive = g_jmxAvailable;

    // 1. Activate a REAL second top-level Window from the app's OWN WinUI runtime.
    HSTRING winCls = MakeStr(L"Microsoft.UI.Xaml.Window");
    IInspectable* winInsp = nullptr;
    HRESULT hr = RoActivateInstance(winCls, &winInsp);
    WindowsDeleteString(winCls);
    DevToolsOverlayLog(L"route1.step1 RoActivateInstance(Window) hr=0x%08X insp=%p", hr, winInsp);
    if (FAILED(hr) || !winInsp) return FAILED(hr) ? hr : E_NOINTERFACE;

    void* win = nullptr;
    hr = winInsp->QueryInterface(DevToolsIid<DevToolsX::IWindow>(), &win);
    DevToolsOverlayLog(L"route1.step1 QI IWindow hr=0x%08X win=%p", hr, win);
    if (FAILED(hr) || !win) { winInsp->Release(); return FAILED(hr) ? hr : E_NOINTERFACE; }

    // 2. Parse the inspector UI markup with the app's own XamlReader (detached subtree, fires no tree-change
    // callbacks until the Window realizes it on Activate).
    IInspectable* content = nullptr;
    hr = LoadMarkupWin(BuildWindowMarkup(nodes.size(), censusTotal), &content, errorMessage);
    DevToolsOverlayLog(L"route1.step2 XamlReader.Load hr=0x%08X content=%p", hr, content);
    if (FAILED(hr) || !content) { reinterpret_cast<IUnknown*>(win)->Release(); winInsp->Release(); return FAILED(hr) ? hr : E_FAIL; }

    // 2b. Locate the "PropsHost" StackPanel (FindName on the content root) and cache its Children collection,
    // so node selection can swap the editable property fragment into it (Clear + Append).
    void* fe = nullptr;
    if (SUCCEEDED(content->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) && fe) {
        HSTRING nm = MakeStr(L"PropsHost");
        IInspectable* found = nullptr;
        DevToolsFindName(fe, nm, &found);
        WindowsDeleteString(nm);
        if (found) {
            IInspectable* panel = nullptr;
            if (SUCCEEDED(found->QueryInterface(DevToolsIid<DevToolsXC::IPanel>(), reinterpret_cast<void**>(&panel))) && panel) {
                IInspectable* kids = nullptr;
                if (SUCCEEDED(DevToolsPanelGetChildren(panel, &kids)) && kids) {
                    kids->QueryInterface(DevToolsIid<DevToolsUIElementVector>(), reinterpret_cast<void**>(&g_ctx.propsHostChildren));
                    kids->Release();
                }
                panel->Release();
            }
            found->Release();
        }
        nm = MakeStr(L"TreeRowsHost");
        found = nullptr;
        DevToolsFindName(fe, nm, &found);
        WindowsDeleteString(nm);
        if (found) {
            IInspectable* panel = nullptr;
            if (SUCCEEDED(found->QueryInterface(DevToolsIid<DevToolsXC::IPanel>(), reinterpret_cast<void**>(&panel))) && panel) {
                IInspectable* kids = nullptr;
                if (SUCCEEDED(DevToolsPanelGetChildren(panel, &kids)) && kids) {
                    kids->QueryInterface(DevToolsIid<DevToolsUIElementVector>(), reinterpret_cast<void**>(&g_treeHostChildren));
                    kids->Release();
                }
                panel->Release();
            }
            found->Release();
        }
        reinterpret_cast<IUnknown*>(fe)->Release();
    }
    // Capture the tree header TextBlock and remember the population this snapshot was built from, so a later
    // tree change can say the list is out of date. Without this the window keeps presenting a snapshot as the
    // live tree with no way for the user to tell it has drifted.
    {
        void* fe2 = nullptr;
        if (SUCCEEDED(content->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe2)) && fe2) {
            HSTRING nm = MakeStr(L"TreeHeader");
            IInspectable* found = nullptr;
            DevToolsFindName(fe2, nm, &found);
            WindowsDeleteString(nm);
            if (g_treeHeaderTb) { reinterpret_cast<IUnknown*>(g_treeHeaderTb)->Release(); g_treeHeaderTb = nullptr; }
            if (found) {
                found->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), reinterpret_cast<void**>(&g_treeHeaderTb));
                found->Release();
            }
            reinterpret_cast<IUnknown*>(fe2)->Release();
        }
        g_snapshotTotal = censusTotal ? censusTotal : nodes.size();
        g_treeStale = false;
    }
    g_ctx.rowsFn = rowsFn;
    g_ctx.readInputFn = readInputFn;
    g_ctx.writeFn = writeFn;
    g_ctx.clearFn = clearFn;
    g_ctx.resolveFn = resolveFn;
    g_ctx.bindFn = bindFn;
    g_ctx.sourceFn = sourceFn;
    g_ctx.layoutFn = layoutFn;
    g_ctx.highlightFn = highlightFn;
    g_ctx.wireLiveFn = wireLiveFn;
    g_ctx.censusCountFn = censusCountFn;
    g_ctx.snapshotFn = snapshotFn;
    g_ctx.previewFn = previewFn;
    DevToolsOverlayLog(L"route1.step2b propsHostChildren=%p", g_ctx.propsHostChildren);

    // 2b-ii. Comments view: cache the second list's host + the two ScrollViewers the toggle swaps, and
    // the filter box (it filters the TREE, so it goes away with it). Every one is optional — an older
    // shell without these names simply has no comments view rather than failing the open.
    {
        void* feC = nullptr;
        if (SUCCEEDED(content->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &feC)) && feC) {
            auto findByName = [&](const wchar_t* n) -> IInspectable* {
                HSTRING nm = MakeStr(n); IInspectable* f = nullptr;
                DevToolsFindName(feC, nm, &f); WindowsDeleteString(nm); return f;
            };
            if (g_commentsHostChildren) { g_commentsHostChildren->Release(); g_commentsHostChildren = nullptr; }
            if (IInspectable* host = findByName(L"CommentsHost")) {
                IInspectable* panel = nullptr;
                if (SUCCEEDED(host->QueryInterface(DevToolsIid<DevToolsXC::IPanel>(), reinterpret_cast<void**>(&panel))) && panel) {
                    IInspectable* kids = nullptr;
                    if (SUCCEEDED(DevToolsPanelGetChildren(panel, &kids)) && kids) {
                        kids->QueryInterface(DevToolsIid<DevToolsUIElementVector>(), reinterpret_cast<void**>(&g_commentsHostChildren));
                        kids->Release();
                    }
                    panel->Release();
                }
                host->Release();
            }
            auto grabUi = [&](const wchar_t* n, void** slot) {
                if (*slot) { reinterpret_cast<IUnknown*>(*slot)->Release(); *slot = nullptr; }
                if (IInspectable* el = findByName(n)) {
                    el->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), slot);
                    el->Release();
                }
            };
            grabUi(L"TreeScroll", &g_treeScrollUi);
            grabUi(L"CommentsScroll", &g_commentsScrollUi);
            grabUi(L"TreeControls", &g_treeControlsUi);
            grabUi(L"PickOn", &g_pickOnUi);
            grabUi(L"FocusOn", &g_focusOnUi);
            grabUi(L"FocusNoticeHost", &g_focusNoticeUi);
            if (g_focusNoticeTb) { reinterpret_cast<IUnknown*>(g_focusNoticeTb)->Release(); g_focusNoticeTb = nullptr; }
            if (IInspectable* fn = findByName(L"FocusNotice")) {
                fn->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &g_focusNoticeTb);
                fn->Release();
            }
            if (!g_focusNoticeUi || !g_focusNoticeTb)
                DevToolsOverlayLog(L"route1.wire MISS: focus notice host=%p text=%p -- focus redirects will move "
                              L"the tree with no explanation", g_focusNoticeUi, g_focusNoticeTb);
            grabUi(L"CommentsBadge", &g_commentsBadgeUi);
            if (g_commentsLbl) { reinterpret_cast<IUnknown*>(g_commentsLbl)->Release(); g_commentsLbl = nullptr; }
            if (IInspectable* lbl = findByName(L"CommentsLbl")) {
                lbl->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), reinterpret_cast<void**>(&g_commentsLbl));
                lbl->Release();
            }
            auto wireTab = [&](const wchar_t* name, void** hold, bool toComments) {
                if (*hold) { reinterpret_cast<IUnknown*>(*hold)->Release(); *hold = nullptr; }
                IInspectable* tab = findByName(name);
                if (!tab) {
                    DevToolsOverlayLog(L"route1.wire MISS: view tab '%s' not found -- the left pane's mode cannot "
                                  L"be changed or reported", name);
                    return;
                }
                tab->QueryInterface(DevToolsIid<DevToolsXCP::IToggleButton>(), hold);
                void* bb = nullptr;
                if (SUCCEEDED(tab->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), &bb)) && bb) {
                    auto* s = new CommentsViewSink(); s->Init(toComments);
                    __int64 tok = 0; DevToolsAddClick(bb, s, &tok);
                    reinterpret_cast<IUnknown*>(bb)->Release();
                }
                tab->Release();
            };
            wireTab(L"TreeTabBtn", &g_treeTabBtn, false);
            wireTab(L"CommentsTabBtn", &g_commentsTabBtn, true);
            reinterpret_cast<IUnknown*>(feC)->Release();
        }
        g_commentsView = false;
        g_commentRows.clear();
        UpdateCommentsButtonLabel();
    }

    // 2c. Wire the static shell. Tree rows live in a separate namescope and are rebuilt through TreeRowsHost.
    void* feRoot = nullptr;
    content->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &feRoot);
    int wired = 0;
    if (feRoot) {
        g_treeFilterQuery.clear();
        g_selectedWire = 0;
        CaptureRowPalette(feRoot);
        CapturePropRowTemplates(feRoot);
        RebuildTreeRows();
        wired = (int)g_treeRows.size();
        // Wire the tree filter box: FindName TreeFilter -> hold its IInspectable (read via readInputFn) -> QI
        // ITextBox -> add_TextChanged(FilterSink) so each keystroke re-applies the filter. Best-effort.
        {
            HSTRING fnm = MakeStr(std::wstring(L"TreeFilter"));
            IInspectable* fInsp = nullptr;
            DevToolsFindName(feRoot, fnm, &fInsp);
            WindowsDeleteString(fnm);
            if (fInsp) {
                fInsp->AddRef();
                g_filterBox = fInsp;
                void* tbox = nullptr;
                if (SUCCEEDED(fInsp->QueryInterface(DevToolsIid<DevToolsXC::ITextBox>(), &tbox)) && tbox) {
                    g_filterTextBox = tbox;
                    FilterSink* fs = new FilterSink();
                    fs->Init();
                    __int64 ftok = 0;
                    DevToolsAddTextChanged(tbox, fs, &ftok);
                }
                fInsp->Release();
            }
        }
        // Wire each toolbar button and optionally hold a text label for controls whose state remains textual.
        auto wireToggle = [&](const wchar_t* btnName, const wchar_t* lblName, IUnknown* sink, void** lblOut, void** btnOut = nullptr) {
            HSTRING bn = MakeStr(btnName);
            IInspectable* bInsp = nullptr;
            DevToolsFindName(feRoot, bn, &bInsp);
            WindowsDeleteString(bn);
            if (bInsp) {
                void* bb = nullptr;
                if (SUCCEEDED(bInsp->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), &bb)) && bb) {
                    __int64 tok = 0;
                    DevToolsAddClick(bb, sink, &tok);
                    reinterpret_cast<IUnknown*>(bb)->Release();
                }
                if (btnOut) bInsp->QueryInterface(DevToolsIid<DevToolsXC::IControl>(), btnOut);
                bInsp->Release();
            }
            if (!lblName || !lblOut) return;
            HSTRING ln = MakeStr(lblName);
            IInspectable* lInsp = nullptr;
            DevToolsFindName(feRoot, ln, &lInsp);
            WindowsDeleteString(ln);
            if (lInsp) {
                void* tb = nullptr;
                if (SUCCEEDED(lInsp->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &tb)) && tb) *lblOut = tb;
                lInsp->Release();
            }
        };
        JmxSink*  jmxSink  = new JmxSink();  jmxSink->Init();
        PickSink* pickSink = new PickSink(); pickSink->Init();
        FocusSink* focusSink = new FocusSink(); focusSink->Init();
        wireToggle(L"JmxBtn",  L"JmxLbl",  reinterpret_cast<IUnknown*>(jmxSink),  &g_jmxLbl, &g_jmxBtnCtrl);
        wireToggle(L"PickBtn", nullptr, reinterpret_cast<IUnknown*>(pickSink), nullptr, &g_pickBtnCtrl);
        if (g_pickBtnCtrl) DevToolsPutIsEnabled(g_pickBtnCtrl, g_ctx.writeFn != nullptr);
        wireToggle(L"FocusBtn", nullptr, reinterpret_cast<IUnknown*>(focusSink), nullptr);
        void* rootUi = nullptr;
        if (SUCCEEDED(static_cast<IUnknown*>(feRoot)->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &rootUi)) && rootUi) {
            auto* keys = new SnapshotRefreshSink(); keys->Init();
            __int64 keyToken = 0;
            const HRESULT keyHr = DevToolsAddPreviewKeyDown(rootUi, keys, &keyToken);
            if (FAILED(keyHr)) DevToolsOverlayLog(L"route1.snapshot F5 subscription FAILED hr=0x%08X", keyHr);
            reinterpret_cast<IUnknown*>(rootUi)->Release();
        }

        // Draggable tree/properties splitter. The Thumb sits over the 1px rule as a 6px grab strip;
        // its DragDelta adjusts the tree pane's Width, and the Auto tree column makes that width the split.
        // Both refs belong to this window generation and are dropped in ResetRoute1State.
        {
            IInspectable* splitInsp = nullptr;
            HSTRING sn = MakeStr(L"PaneSplitter");
            DevToolsFindName(feRoot, sn, &splitInsp);
            WindowsDeleteString(sn);
            IInspectable* paneInsp = nullptr;
            HSTRING pn2 = MakeStr(L"TreePane");
            DevToolsFindName(feRoot, pn2, &paneInsp);
            WindowsDeleteString(pn2);
            if (splitInsp && paneInsp) {
                void* thumb = nullptr;
                // Window-generation ownership: ResetRoute1State releases both references, including the shell root.
                paneInsp->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &g_splitPaneFe);
                if (feRoot) {
                    g_splitHostFe = feRoot;
                    reinterpret_cast<IUnknown*>(g_splitHostFe)->AddRef();
                    auto* sizeSink = new SplitSizeSink();
                    sizeSink->Init();
                    __int64 sizeToken = 0;
                    const HRESULT sizeHr = DevToolsAddSizeChanged(feRoot, sizeSink, &sizeToken);
                    if (FAILED(sizeHr)) DevToolsOverlayLog(L"route1.split resize subscription FAILED hr=0x%08X", sizeHr);
                    ClampInspectorSplit();
                }
                if (SUCCEEDED(splitInsp->QueryInterface(DevToolsIid<DevToolsXCP::IThumb>(), &thumb)) && thumb && g_splitPaneFe) {
                    SplitterSink* ss = new SplitterSink();
                    ss->Init();
                    __int64 stok = 0;
                    HRESULT hrS = DevToolsAddDragDelta(thumb, ss, &stok);
                    // A failed subscription here is a splitter that looks draggable and is not -- the exact
                    // subscribed-but-never-invoked shape this file keeps having to detect, so say it.
                    if (FAILED(hrS)) DevToolsOverlayLog(L"route1.splitter add_DragDelta FAILED hr=0x%08X -- the divider will not resize", hrS);
                    reinterpret_cast<IUnknown*>(thumb)->Release();
                } else {
                    DevToolsOverlayLog(L"route1.splitter MISS: PaneSplitter is not a Thumb, or TreePane has no IFrameworkElement -- the divider will not resize");
                }
            } else {
                DevToolsOverlayLog(L"route1.splitter MISS: PaneSplitter=%p TreePane=%p -- the divider will not resize",
                              (void*)splitInsp, (void*)paneInsp);
            }
            if (splitInsp) splitInsp->Release();
            if (paneInsp)  paneInsp->Release();
        }

        // Empty-tree guarantee: classification ran over the full flattened snapshot
        // before the render cap. If it found nothing, the toggle stays off and the full-tree population is used.
        // The label's three states and the disabled state both live in ApplyJmxAvailability, because a
        // navigation can change availability under the window and the two must never disagree.
        if (!g_jmxBtnCtrl)
            DevToolsOverlayLog(L"route1.jmx MISS: JmxBtn has no IControl -- the toggle cannot be disabled and will accept clicks that do nothing");
        ApplyJmxAvailability();
        // (5a): react to the app's tree changing under us. Subscribe returns whether the tap's own
        // AdviseVisualTreeChange ever bound -- if it did not, NO notification can arrive, and a window that
        // waits for one would sit on a stale tree looking correct. Say so rather than wait forever.
        if (!DevToolsTreeWatch_Subscribe(&OnAppTreeChanged))
            DevToolsOverlayLog(L"route1.treechange NOT armed -- the tap's visual-tree subscription never bound, so the window will not notice navigations");
        DevToolsOverlayLog(L"route1.jmx appAuthored=%d/%zu srcSeen=%d available=%d active=%d enabled=%d",
                      appCount, nodes.size(), g_jmxSrcSeen, (int)g_jmxAvailable, (int)g_jmxActive, (int)g_jmxAvailable);
        reinterpret_cast<IUnknown*>(feRoot)->Release();
    }
    DevToolsOverlayLog(L"route1.step2c wired %d/%zu node click handlers", wired, g_treeRows.size());

    // 3. Self-exclude: seed the content subtree root so the Adds the Window realization fires are treated as
    // ours (the caller also brackets this in the overlay-build scope). Keeps ping->pong intact for the
    // synchronous Adds; async post-Activate Adds are caught by XamlRoot-scoped exclusion (step 4b).
    if (seed && diag) {
        InstanceHandle ch = 0;
        if (SUCCEEDED(diag->GetHandleFromIInspectable(content, &ch)) && ch) seed(ch);
    }

    void* contentUi = nullptr;
    hr = content->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &contentUi);
    if (SUCCEEDED(hr) && contentUi) {
        hr = DevToolsWinPutContent(win, contentUi);
        DevToolsOverlayLog(L"route1.step4 put_Content hr=0x%08X", hr);
        reinterpret_cast<IUnknown*>(contentUi)->Release();
    }
    if (FAILED(hr) || !contentUi) {
        reinterpret_cast<IUnknown*>(win)->Release();
        content->Release();
        winInsp->Release();
        ResetRoute1State();
        return FAILED(hr) ? hr : E_NOINTERFACE;
    }
    HSTRING title = MakeStr(TitleWithPid().c_str());
    DevToolsWinPutTitle(win, title);
    WindowsDeleteString(title);
    HRESULT hrBackdrop = ApplyMicaBackdrop(winInsp);
    DevToolsOverlayLog(L"route1.step4 MicaBackdrop hr=0x%08X", hrBackdrop);

    ConfigureInspectorCaptionTheme(winInsp, content);
    if (HWND preHwnd = HwndFromWindowInsp(winInsp)) {
        SetInspectorIcon(preHwnd);
        // Only record the HWND as cloaked when DWM actually accepted the cloak. If it failed, the window is
        // still visible (we take the flash) and there is nothing to uncloak -- recording it would make the log
        // claim a cloak that never happened. Uncloaking an uncloaked window is a harmless no-op regardless.
        if (SUCCEEDED(SetPlacementCloak(preHwnd, true))) {
            g_placeCloakHwnd = preHwnd;
            DevToolsOverlayLog(L"route1.place cloak hwnd=%p (pre-Activate)", preHwnd);
        } else {
            DevToolsOverlayLog(L"route1.place cloak FAILED hwnd=%p -- leaving window visible", preHwnd);
        }
    }

    HRESULT hrA = DevToolsWinActivate(win);
    DevToolsOverlayLog(L"route1.step4 Activate hr=0x%08X", hrA);
    if (FAILED(hrA)) {
        ReleaseInspectorCaptionTheme();
        const HRESULT closeHr = DevToolsWinClose(win);
        if (FAILED(closeHr)) DevToolsOverlayLog(L"route1.titlebar failed-activation close hr=0x%08X", closeHr);
        reinterpret_cast<IUnknown*>(win)->Release();
        content->Release();
        winInsp->Release();
        ResetRoute1State();
        return hrA;
    }

    // 4b. Remember the content root for LAZY XamlRoot capture. The XamlRoot is NOT populated synchronously
    // within Activate() (it lands a layout turn later, before the async subtree Adds fire), so a capture
    // right here returns null. Instead we re-read it from the content on the first census OwnsElement call.
    if (!g_inspectorContent) { g_inspectorContent = content; content->AddRef(); }
    CaptureInspectorXamlRoot(content);

    g_route1WindowInsp = winInsp; // keep the Window alive (AddRef held by activation ref)
    if (!Route1WindowAlive()) {
        DevToolsOverlayLog(L"route1.step4 Activate returned success but no live inspector HWND exists");
        reinterpret_cast<IUnknown*>(win)->Release();
        content->Release();
        ResetRoute1State();
        return E_FAIL;
    }

    // 4c. Tier 0 placement: size the window compactly and open it beside the app. Deferred to a one-shot
    // timer (see PlaceTimerProc) because WinUI overrides a synchronous SetWindowPos during its own
    // post-Activate initial sizing. Armed after g_route1WindowInsp is set so the proc can recover the HWND.
    bool placementArmed = false;
    if (HWND selfHwnd = Route1WindowHwnd()) {
        g_placePass = 0;
        placementArmed = SetTimer(selfHwnd, kPlaceTimerId, 120, PlaceTimerProc) != 0;
        g_watchdogSawApp = false;
        SetTimer(selfHwnd, kWatchTimerId, 400, WatchTimerProc);
        // Release the theme subscription while its XAML root is still valid.
        if (!SetWindowSubclass(selfHwnd, Route1MinSizeProc, 1, g_route1Gen)) {
            const DWORD error = GetLastError();
            const HRESULT themeHr = ReleaseInspectorCaptionTheme();
            const HRESULT closeHr = DevToolsWinClose(win);
            DevToolsOverlayLog(L"route1.caption close hook failed error=%lu theme=0x%08X close=0x%08X",
                error, themeHr, closeHr);
            reinterpret_cast<IUnknown*>(win)->Release();
            content->Release();
            ResetRoute1State();
            return error ? HRESULT_FROM_WIN32(error) : E_FAIL;
        }
    }

    // Invariant: the window may only stay cloaked while something is guaranteed to uncloak it. PlaceTimerProc is
    // the ONLY code path that reveals a placement-cloaked window, so if it never armed -- Route1WindowHwnd()
    // returned null, or SetTimer failed (it returns 0 on failure) -- reveal the window now and accept the
    // cosmetic default-rect flash. An invisible DevTools window is strictly worse than the flash this removes.
    if (!placementArmed) {
        DevToolsOverlayLog(L"route1.place timer NOT armed -- uncloaking now to avoid an invisible window");
        UncloakPlacement();
    }

    SelectNodeByWire(preselectWire);

    reinterpret_cast<IUnknown*>(win)->Release();
    content->Release();
    return S_OK;
}

static void ResetAppVisibleModes(HWND h)
{
    if (h) {
        KillTimer(h, kDisarmAfterWindowPickTimerId);
        KillTimer(h, kRefreshTimerId);
    }
    if (g_hoverRevertTimer) { KillTimer(nullptr, g_hoverRevertTimer); g_hoverRevertTimer = 0; }
    // The highlight and its tag belong to the window, except a selection the user made in the app, whose panel is open.
    if (g_ctx.highlightFn && !DevToolsOverlay_IsCommentFlyoutOpen()) g_ctx.highlightFn(0, true);
    if (g_pickRoute.IsWindowPickArmed()) {
        DevToolsOverlay_DisarmPick();
        g_pickRoute.CancelWindowPick();
        SetIconToggleState(g_pickOnUi, false);
    }
    if (g_focusActive) {
        DevToolsOverlay_SetFocusTracking(false);
        g_focusActive = false;
        SetIconToggleState(g_focusOnUi, false);
    }
}

HRESULT DevToolsWindow_Close()
{
    if (!g_route1WindowInsp) return S_FALSE;
    if (!Route1WindowAlive()) {
        ResetRoute1State();
        return S_FALSE;
    }

    HWND hwnd = Route1WindowHwnd();
    if (!hwnd) {
        DevToolsOverlayLog(L"route1.close no HWND for the live window; falling back to destroying it");
        void* win = nullptr;
        HRESULT hr = g_route1WindowInsp->QueryInterface(DevToolsIid<DevToolsX::IWindow>(), &win);
        if (FAILED(hr) || !win) return FAILED(hr) ? hr : E_NOINTERFACE;
        hr = DevToolsWinClose(win);
        reinterpret_cast<IUnknown*>(win)->Release();
        if (FAILED(hr)) return hr;
        ResetRoute1State();
        return S_OK;
    }

    ShowWindow(hwnd, SW_HIDE);
    if (IsWindowVisible(hwnd)) {
        DevToolsOverlayLog(L"route1.close ShowWindow(SW_HIDE) left the window visible");
        return E_FAIL;
    }
    ResetAppVisibleModes(hwnd);
    LiveUnwatch();
    g_route1WindowHidden = true;
    DevToolsOverlayLog(L"route1.close hidden (instance reused; gen=%u)", g_route1Gen);
    return S_OK;
}

bool DevToolsWindow_ShowsFrameworkRows()
{
    return g_jmxAvailable && !g_jmxActive;
}

bool DevToolsWindow_IsOpen()
{
    if (g_route1WindowHidden) return false;
    return g_route1WindowInsp != nullptr && Route1WindowAlive();
}

// A completed pick selects a tree row; bring the tree tab forward if comments are showing.
bool DevToolsWindow_OnPicked(InstanceHandle wire, bool releaseCommit)
{
    if (!DevToolsWindow_IsOpen()) return false;
    const bool fromWindow = releaseCommit && g_pickRoute.CommitReleasedPick();
    if (fromWindow) {
        ArmDisarmAfterWindowPick();   // complete the one-shot contract (see the timer's own comment)
        FocusInspectorAfterPick();
    }
    if (g_commentsView) {
        SetCommentsView(false);
        DevToolsOverlayLog(L"route1.pick committed in comments view -> switched to the Visual tree tab");
    }
    // Picking the element already shown re-reads it: the pane is a snapshot, and a pick is a request to look again.
    const bool reread = wire && wire == g_selWire;
    SelectNodeByWire(wire);
    if (reread) DevToolsWindow_RereadSelection(wire);
    return fromWindow;
}

// The pane is rebuilt; its filter is reapplied by ShowPropsForNode and its scroll position is kept here.
void DevToolsWindow_RereadSelection(InstanceHandle wire)
{
    bool filterFocused = false;
    if (!DevToolsWindow_IsOpen() || !wire || wire != g_selWire) return;
    if (!CanRereadSelection(&filterFocused)) {
        DevToolsOverlayLog(L"route1.reread skipped: an edit in the properties pane is in progress");
        return;
    }
    DevToolsXC::ScrollViewer scroller{ nullptr };
    double offset = 0;
    try {
        if (g_inspectorContent) {
            winrt::Windows::Foundation::IInspectable object{ nullptr };
            winrt::copy_from_abi(object, g_inspectorContent);
            scroller = object.as<DevToolsX::FrameworkElement>().FindName(L"PropsScroll").try_as<DevToolsXC::ScrollViewer>();
            if (scroller) offset = scroller.VerticalOffset();
        }
    } catch (const winrt::hresult_error&) { scroller = nullptr; }
    ShowPropsForNode(g_selWire, g_selLabel, g_selBreadcrumb, /*force*/ true);
    try {
        if (scroller) scroller.ChangeView(nullptr, winrt::box_value(offset).as<winrt::Windows::Foundation::IReference<double>>(), nullptr, true);
        if (filterFocused && g_propFilterTextBox) {
            winrt::Windows::Foundation::IInspectable object{ nullptr };
            winrt::copy_from_abi(object, g_propFilterTextBox);
            auto filter = object.as<DevToolsXC::TextBox>();
            filter.Focus(winrt::Microsoft::UI::Xaml::FocusState::Programmatic);
            filter.SelectionStart(static_cast<int32_t>(filter.Text().size()));
        }
    } catch (const winrt::hresult_error&) {}
}

// The overlay reports every pick-mode transition here, whichever surface caused it (the window's toggle, the
// rail's Pick button, or Esc). One owner, one state: this is the only writer of the window's indicator, and
// the routing bit is cleared with the mode so a later overlay-armed pick is not mistakenly claimed by this
// window. UI thread only.
void DevToolsWindow_OnPickModeChanged(bool armed)
{
    if (!armed) g_pickRoute.CancelWindowPick();
    if (!DevToolsWindow_IsOpen()) return;
    SetIconToggleState(g_pickOnUi, armed);
    DevToolsOverlayLog(L"route1.pick mode -> %s (overlay armed=%d)", armed ? L"ON" : L"OFF",
                  (int)DevToolsOverlay_IsPickArmed());
}

// Passive focus tracking never changes Just my XAML. With that filter enabled, walk the full
// census to the nearest authored ancestor; otherwise select the actual focused element.
// Report redirection rather than leave stale properties or silently change the filter.
void DevToolsWindow_OnFocusChanged(InstanceHandle wire)
{
    WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::WinFocus);
    if (!g_route1WindowInsp || !Route1WindowAlive()) return;

    // Nothing to redirect past when the filter is off, or when the focused element is not in the census at
    // all (SelectNodeByWire refreshes and reports that case itself).
    size_t idx = kNoCensusRow;
    if (g_jmxActive && wire) {
        for (size_t i = 0; i < g_allTreeNodes.size(); ++i)
            if (g_allTreeNodes[i].handle == wire) { idx = i; break; }
    }
    if (idx == kNoCensusRow ||
        (idx < g_allTreeAppAuthored.size() && g_allTreeAppAuthored[idx])) {
        SelectNodeByWire(wire, SelectCause::Focus);
        SetFocusNotice(L"");
        return;
    }

    const auto census = SnapshotCensusRows();
    const size_t anc = DevToolsTreeLayout::NearestAppAuthoredAncestor(census, idx);
    const std::wstring focused = ShortType(g_allTreeNodes[idx].type);
    if (anc == DevToolsTreeLayout::kNoRow) {
        // Nothing in the chain is app-authored, so there is no honest place to move the selection to.
        // Selecting the focused element itself would mean revealing a row this view does not contain, and
        // the only way to do that is to turn the filter off -- the one thing this path must not do. Leave
        // the tree where it is and say why, rather than clearing the selection without explanation.
        SetFocusNotice(L"Focus is on " + focused + L", which isn't from your XAML, and neither is "
                       L"anything above it. The tree didn't move. Turn off Just my XAML to see it.");
        DevToolsOverlayLog(L"route1.focus wire=%llu is framework with no app-authored ancestor -> tree unchanged",
                      (unsigned long long)wire);
        return;
    }

    const std::wstring target = ShortType(g_allTreeNodes[anc].type);
    const std::wstring name = g_allTreeNodes[anc].name;
    DevToolsOverlayLog(L"route1.focus wire=%llu (%s) is framework -> selecting app-authored ancestor row %zu (%s)",
                  (unsigned long long)wire, focused.c_str(), anc, target.c_str());
    SelectNodeByWire(g_allTreeNodes[anc].handle, SelectCause::Focus);
    SetFocusNotice(L"Focus is on " + focused + L", which isn't from your XAML. Selected the nearest element "
                   L"you did write: " + target + (name.empty() ? L"" : (L" #" + name)) + L".");
}

// Exclude inspector elements by canonical XamlRoot identity, including asynchronous Adds
// missed by seed handles. Return a caller-owned canonical IUnknown or null.
static IUnknown* GetXamlRootCanonical(IInspectable* element)
{
    if (!element) return nullptr;
    void* uie = nullptr;
    if (FAILED(element->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &uie)) || !uie) return nullptr;
    IInspectable* xr = nullptr;
    HRESULT hr = DevToolsGetXamlRoot(uie, &xr);
    reinterpret_cast<IUnknown*>(uie)->Release();
    if (FAILED(hr) || !xr) return nullptr;
    IUnknown* canon = nullptr;
    xr->QueryInterface(IID_IUnknown, reinterpret_cast<void**>(&canon));
    xr->Release();
    return canon;
}

static void CaptureInspectorXamlRoot(IInspectable* content)
{
    if (g_inspectorXamlRoot) return;
    g_inspectorXamlRoot = GetXamlRootCanonical(content);
    if (g_inspectorXamlRoot) DevToolsOverlayLog(L"route1.step4b inspector XamlRoot captured=%p", g_inspectorXamlRoot);
}

bool DevToolsWindow_OwnsElement(IInspectable* element)
{
    if (!element) return false;
    // Lazy capture: the content's XamlRoot is null until a layout turn after Activate(), so the first
    // OwnsElement call (fired by an async census Add) is where we can finally read it. UI thread only.
    if (!g_inspectorXamlRoot && g_inspectorContent) CaptureInspectorXamlRoot(g_inspectorContent);
    if (!g_inspectorXamlRoot) return false;
    IUnknown* canon = GetXamlRootCanonical(element);
    if (!canon) return false;
    bool owns = (canon == g_inspectorXamlRoot);
    canon->Release();
    return owns;
}
