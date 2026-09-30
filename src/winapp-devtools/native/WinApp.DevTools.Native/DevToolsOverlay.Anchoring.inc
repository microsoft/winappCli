// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsOverlay.cpp; included into that translation unit, not compiled on its own.

static HRESULT MoveToPanel(IXamlDiagnostics* diag, InstanceHandle newPanel, bool recovering,
                           unsigned long long expectedGeneration = 0)
{
    if (!diag || !newPanel) return E_INVALIDARG;
    if (!g_popup || !g_popupUi || !g_appPanel) return E_FAIL;
    if (g_surfaceMoving) return E_PENDING;

    IInspectable* panelInsp = nullptr;
    if (FAILED(diag->GetIInspectableFromHandle(newPanel, &panelInsp)) || !panelInsp) return E_INVALIDARG;
    winrt::com_ptr<IUnknown> identity;
    HRESULT rootResult = ReadHostRootIdentity(panelInsp, identity);
    if (FAILED(rootResult) || !identity ||
        (recovering && (identity.get() != g_hostRootIdentity.get() ||
                        expectedGeneration != g_hostGeneration || !g_toolbarVisible))) {
        panelInsp->Release();
        return FAILED(rootResult) ? rootResult : E_INVALIDARG;
    }

    g_surfaceMoving = true;
    const auto moveGeneration = ++g_hostGeneration;
    HRESULT closeResult = HostPutOpen(g_popup, false);
    if (FAILED(closeResult)) {
        g_surfaceMoving = false;
        panelInsp->Release();
        return closeResult;
    }
    if (recovering && (moveGeneration != g_hostGeneration || !g_toolbarVisible)) {
        g_surfaceMoving = false;
        panelInsp->Release();
        return E_ABORT;
    }

    winrt::com_ptr<IInspectable> oldPanel, children, vector;
    HRESULT detachResult = g_appPanel->QueryInterface(DevToolsIid<DevToolsXC::IPanel>(), oldPanel.put_void());
    if (SUCCEEDED(detachResult))
        detachResult = oldPanel ? DevToolsPanelGetChildren(oldPanel.get(), children.put()) : E_NOINTERFACE;
    if (SUCCEEDED(detachResult))
        detachResult = children ? children->QueryInterface(DevToolsIid<DevToolsUIElementVector>(), vector.put_void()) : E_NOINTERFACE;
    unsigned int index = 0;
    unsigned char wasChild = 0;
    if (SUCCEEDED(detachResult))
        detachResult = vector ? DevToolsVecIndexOf(vector.get(), g_popupUi, &index, &wasChild) : E_NOINTERFACE;
    if (SUCCEEDED(detachResult) && wasChild)
        detachResult = DevToolsVecRemoveAt(vector.get(), index);
    if (FAILED(detachResult)) {
        DevToolsOverlayLog(L"overlay.switch detach FAILED hr=0x%08X", detachResult);
        if (!recovering || (moveGeneration == g_hostGeneration && g_toolbarVisible)) {
            const HRESULT reopen = HostPutOpen(g_popup, true);
            if (FAILED(reopen)) DevToolsOverlayLog(L"overlay.switch detach rollback reopen FAILED hr=0x%08X", reopen);
        }
        g_surfaceMoving = false;
        panelInsp->Release();
        return detachResult;
    }
    if (recovering && (moveGeneration != g_hostGeneration || !g_toolbarVisible)) {
        g_surfaceMoving = false;
        panelInsp->Release();
        return E_ABORT;
    }

    HRESULT hr = HostAttach(diag, panelInsp, g_popupUi, L"switch");
    if (SUCCEEDED(hr)) {
        EndToolbarDrag();
        UnwireFocusTracking();
        g_appPanel->Release();
        g_appPanel = panelInsp;
        g_appPanel->AddRef();
        g_pickDiag = diag;
        g_hostRootIdentity = std::move(identity);
        g_cachedAppHwnd = g_cachedIsland = nullptr;
        WireResizeTracking(panelInsp);
        if (g_focusTracking) WireFocusTracking();
        // Re-open on the new root's popup layer, then re-derive coordinates from that surface; old-root
        // Canvas/work-area geometry can park the toolbar off-screen.
        if (recovering) {
            DevToolsSurface surface;
            if (!DevToolsSurface_Resolve(diag, newPanel, &surface) ||
                surface.xamlRootKey != reinterpret_cast<unsigned long long>(g_hostRootIdentity.get())) {
                hr = E_PENDING;
            } else {
                g_toolbarSurface = surface;
                for (auto& known : g_allSurfaces)
                    if (known.xamlRootKey == surface.xamlRootKey) known = surface;
            }
        } else {
            if (!DevToolsSurface_Resolve(diag, newPanel, &g_toolbarSurface)) hr = E_PENDING;
        }
        HRESULT hrO = FAILED(hr) ? hr :
            recovering && (moveGeneration != g_hostGeneration || !g_toolbarVisible)
                ? E_ABORT : HostPutOpen(g_popup, g_toolbarVisible);
        if (FAILED(hrO)) hr = hrO;
        RepositionToolbar();
        UpdateChipLabel();
        DevToolsOverlayLog(L"overlay.switch moved to panel=%llu reopen hr=0x%08X active='%s'",
                      (unsigned long long)newPanel, hrO, DevToolsOverlay_ActiveSurface().displayName.c_str());
        NotifyStateChanged();
    } else {
        DevToolsOverlayLog(L"overlay.switch attach FAILED hr=0x%08X; restoring the previous parent", hr);
        const HRESULT restore = wasChild ? HostAttach(diag, g_appPanel, g_popupUi, L"switch.restore") : S_FALSE;
        if (FAILED(restore)) DevToolsOverlayLog(L"overlay.switch rollback attach FAILED hr=0x%08X", restore);
        if (SUCCEEDED(restore) && wasChild &&
            (!recovering || (moveGeneration == g_hostGeneration && g_toolbarVisible))) {
            const HRESULT reopen = HostPutOpen(g_popup, true);
            if (FAILED(reopen)) DevToolsOverlayLog(L"overlay.switch rollback reopen FAILED hr=0x%08X", reopen);
        }
    }
    g_surfaceMoving = false;
    if (recovering && g_selectedHandle) {
        winrt::com_ptr<IInspectable> selected, fe;
        bool detached = FAILED(diag->GetIInspectableFromHandle(g_selectedHandle, selected.put())) || !selected;
        if (!detached &&
            SUCCEEDED(selected->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), fe.put_void())) && fe) {
            bool loaded = true;
            if (SUCCEEDED(DevToolsGetIsLoaded(fe.get(), &loaded))) detached = !loaded;
        }
        if (detached && !SelKeepDetachedIfUnsent()) DeselectAll(g_lastHighlightHandle == g_selectedHandle);
    }
    TrackingTick();
    panelInsp->Release();
    return hr;
}

HRESULT DevToolsOverlay_MoveToPanel(IXamlDiagnostics* diag, InstanceHandle newPanel)
{
    g_failedRecoveryPanel = 0;
    return MoveToPanel(diag, newPanel, false);
}

HRESULT DevToolsOverlay_RecoverHost(IXamlDiagnostics* diag, DevToolsFindHostPanelFn findPanel,
                                  InstanceHandle* currentPanel)
{
    if (!currentPanel || !diag || !findPanel) return E_INVALIDARG;
    *currentPanel = 0;
    if (g_surfaceMoving) return S_FALSE;
    if (g_selectionPopup && g_selectionRootIdentity) {
        bool selectionAttached = false;
        HRESULT selectionResult = PopupAttached(g_selectionPanel.get(), g_selectionPopup.get(),
            g_selectionPopupUi.get(), &selectionAttached);
        if (FAILED(selectionResult)) return selectionResult;
        winrt::com_ptr<IUnknown> actualRoot;
        const HRESULT rootResult = ReadHostRootIdentity(g_selectionPanel.get(), actualRoot);
        if (!selectionAttached || FAILED(rootResult) || actualRoot.get() != g_selectionRootIdentity.get()) {
            const auto candidate = findPanel(reinterpret_cast<unsigned long long>(g_selectionRootIdentity.get()));
            if (candidate) {
                selectionResult = DevToolsOverlay_MoveSelectionToPanel(diag, candidate, true);
                if (FAILED(selectionResult)) return selectionResult;
            } else {
                ExitPickMode();
                UnwireAppEscape();
                if (g_selectionResizeSource) {
                    selectionResult = DevToolsRemoveSizeChanged(g_selectionResizeSource.get(), g_selectionResizeToken);
                    if (FAILED(selectionResult)) return selectionResult;
                    g_selectionResizeSource = nullptr;
                }
                selectionResult = HostPutOpen(g_selectionPopup.get(), false);
                if (FAILED(selectionResult)) return selectionResult;
                const bool draft = SelKeepDetachedIfUnsent();
                if (!draft) DeselectAll(g_lastHighlightHandle == g_selectedHandle);
                ClearSelScroller();
                g_activeSurface = {};
                g_pickRoot = 0;
                if (draft) {
                    g_selRootLost = true;
                    auto* staleTarget = g_selTarget;
                    g_selTarget = nullptr;
                    if (staleTarget) staleTarget->Release();
                    for (const auto* name : {L"DevToolsSelRows", L"DevToolsSelOpen", L"DevToolsSelCommentDelete"}) {
                        if (auto* element = DevToolsSelFindName(g_selUi, name)) {
                            SetOverlayElementVisible(element, false);
                            element->Release();
                        }
                    }
                    SetSelectionOperationError(L"Comment",
                        L"The original window closed. Copy your draft before closing this panel; Close discards it.");
                    const auto fallback = findPanel(0);
                    if (fallback) {
                        selectionResult = DevToolsOverlay_MoveSelectionToPanel(diag, fallback, true);
                        if (FAILED(selectionResult)) return selectionResult;
                        MoveOverlayElement(g_selUi, 0, 0);
                        selectionResult = DevToolsPopupPutHorizontalOffset(g_selPopup, 8);
                        if (SUCCEEDED(selectionResult)) selectionResult = DevToolsPopupPutVerticalOffset(g_selPopup, 8);
                        if (SUCCEEDED(selectionResult)) selectionResult = SetSelectionPopupOpen(true);
                        if (FAILED(selectionResult)) return selectionResult;
                    }
                }
                NotifyStateChanged();
            }
        }
    }
    if (g_lastHighlightHandle) {
        const HRESULT highlightResult = g_selectionHost ?
            g_selectionHost(g_lastHighlightHandle, false) : E_PENDING;
        if (FAILED(highlightResult)) {
            ClearAdorner();
            g_lastHighlightHandle = 0;
            DevToolsOverlayLog(L"overlay.highlight recovery unavailable hr=0x%08X", highlightResult);
            NotifyStateChanged();
        }
    }
    if (!g_toolbarVisible || !g_popup || !g_hostRootIdentity) return S_FALSE;
    const auto generation = g_hostGeneration;
    const auto identity = g_hostRootIdentity;
    bool attached = false;
    HRESULT hr = ReadHostAttachment(&attached);
    if (FAILED(hr)) return hr;
    if (attached) return S_FALSE;
    NotifyStateChanged();
    InstanceHandle candidate = findPanel(reinterpret_cast<unsigned long long>(identity.get()));
    const bool sameRoot = candidate != 0;
    if (!candidate) candidate = findPanel(0);
    if (!candidate || candidate == g_failedRecoveryPanel || generation != g_hostGeneration ||
        identity.get() != g_hostRootIdentity.get() || !g_toolbarVisible || g_surfaceMoving) return S_FALSE;
    hr = MoveToPanel(diag, candidate, sameRoot, generation);
    *currentPanel = g_toolbarSurface.rootHandle;
    if (SUCCEEDED(hr)) {
        bool restored = false;
        hr = ReadHostAttachment(&restored);
        if (SUCCEEDED(hr) && !restored) hr = E_PENDING;
    }
    if (FAILED(hr)) g_failedRecoveryPanel = candidate;
    else g_failedRecoveryPanel = 0;
    DevToolsOverlayLog(L"overlay.host recovery panel=%llu hr=0x%08X", candidate, hr);
    NotifyStateChanged();
    return hr;
}

// Is `target` in the hit stack at this content-DIP point? Viewport arithmetic alone is not enough: an element
// inside a scrolled ScrollViewer can be within window bounds while clipped out of its own scroll host, and
// TransformToVisual happily reports the full layout rect either way.
static bool HandleInStackAt(IXamlDiagnostics* diag, InstanceHandle rootHandle, InstanceHandle target, long x, long y)
{
    if (!diag || !rootHandle || !target) return false;
    IXamlDiagnostics2_Abi* diag2 = CachedDiag2(diag);
    const bool hasDiag2 = (diag2 != nullptr);
    RECT r{ x, y, x + 1, y + 1 };
    unsigned int count = 0; InstanceHandle* hs = nullptr;
    HRESULT hr = hasDiag2 ? diag2->HitTestForXamlRoot(rootHandle, r, &count, &hs) : diag->HitTest(r, &count, &hs);
    bool found = false;
    if (SUCCEEDED(hr) && count && hs)
        for (unsigned int i = 0; i < count; ++i) if (hs[i] == target) { found = true; break; }
    if (hs) CoTaskMemFree(hs);
    return found;
}

// A diagnostics hit-test here would run from the tracking tick against a tree that may be relaid out; nested
// clipping is reconciled after scrolling settles.
static bool AnchorVisible(InstanceHandle target, const RECT& b, int cw, int ch)
{
    (void)target;
    if (b.right <= b.left || b.bottom <= b.top) return false;
    if (b.right <= 0 || b.bottom <= 0 || b.left >= cw || b.top >= ch) return false;
    long l = (b.left   < 0)  ? 0  : b.left;
    long t = (b.top    < 0)  ? 0  : b.top;
    long r = (b.right  > cw) ? cw : b.right;
    long bo = (b.bottom > ch) ? ch : b.bottom;
    return (r > l) && (bo > t);
}

// Re-anchor by translating the existing canvas subtree, not parking and rebuilding it. Defer moves while the
// panel owns focus to preserve typing; flush the deferred move when focus clears, even if the selection bounds
// have stopped changing.
// Move the existing canvas subtree and defer while the panel owns focus so typing and placement survive
// re-anchor.
static void ReanchorSelectionAnchor(const RECT& nb)
{
    if (!g_selUi || g_selDetached) return;
    if (g_selFocusWithin > 0) {
        g_selPendingAnchor = nb;
        if (!g_selAnchorPending) {
            g_selAnchorPending = true;
            DevToolsOverlayLog(L"overlay.sel anchor move deferred (focusWithin=%d)", g_selFocusWithin);
        }
        return;
    }
    g_selAnchorPending = false;
    MoveOverlayElement(g_selUi, (double)(nb.left - g_selBounds.left), (double)(nb.top - g_selBounds.top));
}

// Apply a deferred re-anchor once the panel is closed and unfocused. Re-resolves the element's CURRENT bounds
// rather than replaying the bounds that were pending: the element may have moved again while the panel was
// open, and this runs at a user-driven moment (a collapse or a blur), not on the tick, so one geometry read i.
static void FlushPendingAnchor()
{
    if (!g_selAnchorPending || !g_selUi) return;
    if (g_selFocusWithin > 0) return;
    RECT nb = g_selPendingAnchor;
    if (g_selectedHandle && g_pickDiag && g_pickRoot) {
        RECT fresh{};
        if (TryElementRootBounds(g_pickDiag, g_pickRoot, g_selectedHandle, &fresh)) nb = fresh;
    }
    g_selAnchorPending = false;
    MoveOverlayElement(g_selUi, (double)(nb.left - g_selBounds.left), (double)(nb.top - g_selBounds.top));
    g_selectedBounds = nb;
    DevToolsOverlayLog(L"overlay.sel anchor flush -> moved to %ld,%ld", nb.left, nb.top);
}

static void ReanchorSelectionNow()
{
    if (!g_selectedHandle || !g_pickDiag || !g_pickRoot) return;
    if (g_hiddenForScroll) return;
    RECT nb{};
    if (!TryElementRootBounds(g_pickDiag, g_pickRoot, g_selectedHandle, &nb)) return;
    if (nb.left == g_selectedBounds.left && nb.top == g_selectedBounds.top &&
        nb.right == g_selectedBounds.right && nb.bottom == g_selectedBounds.bottom) return;
    g_selectedBounds = nb;
    g_lastPickBoundsDip = nb;
    if (!g_chromeHidden)
        PlaceAdorner(g_pickDiag, nb.left, nb.top, nb.right - nb.left, nb.bottom - nb.top, g_pickSeed);
    ReanchorSelectionAnchor(nb);
    SyncLayoutAdorner();
    RefreshScrollAnchor(nb);
    DevToolsOverlayLog(L"overlay.sel re-anchored on layout change rect=%ld,%ld,%ld,%ld", nb.left, nb.top, nb.right, nb.bottom);
}

// Tear down EVERY piece of selection chrome together -- rect, morph anchor and any open composer -- and clear
// the selection itself. Composer first, then the anchor (which owns the null-globals-before-RemoveAt teardown
// discipline for its pointer/focus sinks), then the plain adorner.
static void DeselectAll(bool clearHighlight)
{
    ClearComposer();
    ClearSelectionAnchor();
    if (clearHighlight) ClearAdorner();
    g_selectedHandle = 0;
    g_selectedBounds = RECT{};
    g_chromeHidden = false;
    if (clearHighlight) g_lastPickBoundsDip = RECT{};
    ClearSelScroller();
    SyncLayoutAdorner();
    DevToolsOverlayLog(L"overlay.selection deselected (all chrome torn down)");
    TrackingSync();
    if (clearHighlight) g_lastHighlightHandle = 0;
    NotifyStateChanged();
}

// The SELECTION is not touched here -- Esc's deselect is applied by the caller -- so toggling the mode off
// around an existing selection keeps that selection's chrome.
static void ExitPickMode()
{
    if (!g_pickCatcher) return;
    RemovePickCatcher();
    SetPickButtonOn(false);
    ClearAdorner();
    g_hoverLastHandle = 0;
    g_selectedBounds = RECT{};
    DevToolsOverlayLog(L"overlay.pickmode exited (catcher removed, wheel notches forwarded this session=%d)", g_wheelForwards);
    TrackingSync();
// the ClearAdorner() above only drops the un-committed HOVER preview -- if a selection still exists,
// TrackingSync()'s re-anchor tick (PlaceAdorner, not DrawAdorner) redraws ITS box on the next tick, so the
// observable highlight settles back to the committed selection, not to "nothing".
    g_lastHighlightHandle = g_selectedHandle;
    NotifyStateChanged();
}

static void CALLBACK TrackingTimerProc(HWND, UINT, UINT_PTR, DWORD) { TrackingTick(); }

static bool ReadScrollerOffsets(IInspectable* scroller, double* horizontal, double* vertical)
{
    if (!scroller || !horizontal || !vertical) return false;
    void* sv = nullptr;
    if (FAILED(scroller->QueryInterface(DevToolsIid<DevToolsXC::IScrollViewer>(), &sv)) || !sv) return false;
    const HRESULT hh = DevToolsScrollGetHorizontalOffset(sv, horizontal);
    const HRESULT hv = DevToolsScrollGetVerticalOffset(sv, vertical);
    reinterpret_cast<IUnknown*>(sv)->Release();
    return SUCCEEDED(hh) && SUCCEEDED(hv) && std::isfinite(*horizontal) && std::isfinite(*vertical);
}

static void RefreshScrollAnchor(const RECT& bounds)
{
    double horizontal = 0.0, vertical = 0.0;
    g_selScrollAnchorValid = ReadScrollerOffsets(g_selScroller, &horizontal, &vertical);
    if (g_selScrollAnchorValid) {
        g_selScrollAnchor = { { bounds.left, bounds.top, bounds.right, bounds.bottom }, horizontal, vertical };
    }
}

static void TranslateLayoutAdorner(long dx, long dy)
{
    if (!g_layoutPlaced) return;
    auto offset = [dx, dy](RECT& rect) {
        rect.left += dx; rect.right += dx; rect.top += dy; rect.bottom += dy;
    };
    offset(g_layoutLast.box);
    if (g_layoutLast.haveMargin) offset(g_layoutLast.margin);
    if (g_layoutLast.havePadding) offset(g_layoutLast.padding);

    MoveOverlayElement(g_layoutParts[kLayoutGuideL], (double)g_layoutLast.box.left, 0.0);
    MoveOverlayElement(g_layoutParts[kLayoutGuideR], (double)g_layoutLast.box.right - 1.0, 0.0);
    MoveOverlayElement(g_layoutParts[kLayoutGuideT], 0.0, (double)g_layoutLast.box.top);
    MoveOverlayElement(g_layoutParts[kLayoutGuideB], 0.0, (double)g_layoutLast.box.bottom - 1.0);
    if (g_layoutLast.haveMargin)
        MoveOverlayElement(g_layoutParts[kLayoutMargin], (double)g_layoutLast.margin.left, (double)g_layoutLast.margin.top);
    if (g_layoutLast.havePadding)
        MoveOverlayElement(g_layoutParts[kLayoutPadding], (double)g_layoutLast.padding.left, (double)g_layoutLast.padding.top);
}

static void TrackSelectionFromScroll(double horizontal, double vertical)
{
    if (!g_selectedHandle || !g_selScrollAnchorValid) return;
    const auto tracked = DevToolsSelection::TrackScroll(g_selScrollAnchor, horizontal, vertical);
    RECT nb{ tracked.left, tracked.top, tracked.right, tracked.bottom };
    if (nb.left == g_selectedBounds.left && nb.top == g_selectedBounds.top &&
        nb.right == g_selectedBounds.right && nb.bottom == g_selectedBounds.bottom) return;

    const long dx = nb.left - g_selectedBounds.left;
    const long dy = nb.top - g_selectedBounds.top;
    g_selectedBounds = nb;
    g_lastPickBoundsDip = nb;
    TranslateLayoutAdorner(dx, dy);

    const long cw = (long)g_activeSurface.contentW;
    const long ch = (long)g_activeSurface.contentH;
    const bool visible = DevToolsSelection::IntersectsViewport(tracked, cw, ch);
    if (!visible) {
        if (!g_chromeHidden) {
            if (g_lastHighlightHandle == g_selectedHandle) SetOverlayElementVisible(g_adorner, false);
            SetOverlayElementVisible(g_selUi, false);
            SetLayoutAdornerVisible(false);
            g_chromeHidden = true;
            DevToolsOverlayLog(L"overlay.track selection scrolled out of view -> chrome hidden (NOT deselected)");
        }
        return;
    }

    if (g_chromeHidden) {
        g_chromeHidden = false;
        if (g_lastHighlightHandle == g_selectedHandle) SetOverlayElementVisible(g_adorner, true);
        SetOverlayElementVisible(g_selUi, true);
        SetLayoutAdornerVisible(true);
        DevToolsOverlayLog(L"overlay.track selection back in view -> chrome restored");
    }
    if (g_adorner)
        PlaceAdorner(g_pickDiag, nb.left, nb.top, nb.right - nb.left, nb.bottom - nb.top, g_pickSeed);
    ReanchorSelectionAnchor(nb);
}

// If an app exposes a ScrollViewer event but not its next offsets, preserve the safe hide-and-restore fallback.
static void NoteExpectedScrollActivity()
{
    g_scrollSettleAt = GetTickCount64() + kScrollSettleMs;
}

static void NoteScrollActivity()
{
    NoteExpectedScrollActivity();
    if (!g_hiddenForScroll && g_selectedHandle && !g_chromeHidden) {
        if (g_lastHighlightHandle == g_selectedHandle) SetOverlayElementVisible(g_adorner, false);
        SetOverlayElementVisible(g_selUi, false);
        SetLayoutAdornerVisible(false);
        g_hiddenForScroll = true;
        DevToolsOverlayLog(L"overlay.track scroll started -> chrome hidden (selection kept)");
    }
}

// Deliberately does NOT consult g_chromeHidden: that flag means "the element is off-screen", which is the
// tick's business. This only reverses the scroll hide; if the re-resolve that follows finds the element
// off-screen, the tick hides it again on its own terms.
static void UnhideAfterScroll()
{
    if (!g_hiddenForScroll) return;
    g_hiddenForScroll = false;
    g_scrollSettleAt = 0;
    if (!g_chromeHidden) {
        if (g_lastHighlightHandle == g_selectedHandle) SetOverlayElementVisible(g_adorner, true);
        SetOverlayElementVisible(g_selUi, true);
        SetLayoutAdornerVisible(true);
    }
    g_selectedBounds = RECT{};
    g_logRestoredRect = true;
}

static bool TryReadNextScrollOffsets(void* args, double* horizontal, double* vertical)
{
    if (!args || !horizontal || !vertical) return false;
    void* changing = nullptr;
    if (FAILED(static_cast<IInspectable*>(args)->QueryInterface(
            DevToolsIid<DevToolsXC::IScrollViewerViewChangingEventArgs>(), &changing)) || !changing) return false;
    IInspectable* next = nullptr;
    const HRESULT hn = DevToolsViewChangingGetNextView(changing, &next);
    reinterpret_cast<IUnknown*>(changing)->Release();
    if (FAILED(hn) || !next) return false;

    void* view = nullptr;
    const HRESULT hq = next->QueryInterface(DevToolsIid<DevToolsXC::IScrollViewerView>(), &view);
    next->Release();
    if (FAILED(hq) || !view) return false;
    const HRESULT hh = DevToolsScrollViewGetHorizontalOffset(view, horizontal);
    const HRESULT hv = DevToolsScrollViewGetVerticalOffset(view, vertical);
    reinterpret_cast<IUnknown*>(view)->Release();
    return SUCCEEDED(hh) && SUCCEEDED(hv) && std::isfinite(*horizontal) && std::isfinite(*vertical);
}

static void OnSelScrollViewChanging(void*, void* args)
{
    g_scrollSettleAt = GetTickCount64() + kScrollSettleMs;
    double horizontal = 0.0, vertical = 0.0;
    if (!g_selScrollAnchorValid || !TryReadNextScrollOffsets(args, &horizontal, &vertical)) {
        NoteScrollActivity();
        return;
    }
    if (!g_selScrollLiveSeen) {
        g_selScrollLiveSeen = true;
        DevToolsOverlayLog(L"overlay.track live scroll sink INVOKED (offset-only path)");
    }
    TrackSelectionFromScroll(horizontal, vertical);
}

static void OnSelScrollViewChanged(void*, void* args)
{
    bool intermediate = true;
    if (args) {
        void* a = nullptr;
        if (SUCCEEDED(static_cast<IInspectable*>(args)->QueryInterface(
                DevToolsIid<DevToolsXC::IScrollViewerViewChangedEventArgs>(), &a)) && a) {
            DevToolsViewChangedIsIntermediate(a, &intermediate);
            reinterpret_cast<IUnknown*>(a)->Release();
        }
    }
    if (intermediate) {
        if (g_selScrollLiveSeen) NoteExpectedScrollActivity();
        else NoteScrollActivity();
        return;
    }
// Do not re-resolve inline: this is still on XAML's event-dispatch stack.
    // Do not re-resolve inline on XAML event dispatch; the next idle tick sees settled geometry.
    g_scrollSettleAt = 0;
    if (g_hiddenForScroll) {
        DevToolsOverlayLog(L"overlay.track scroll settled -> chrome restored, re-resolve deferred to the tick");
        UnhideAfterScroll();
    } else if (g_selScrollLiveSeen) {
        DevToolsOverlayLog(L"overlay.track live scroll settled rect=%ld,%ld,%ld,%ld",
            g_selectedBounds.left, g_selectedBounds.top, g_selectedBounds.right, g_selectedBounds.bottom);
    }
}

static void ClearSelScroller()
{
    if (g_selScroller) {
        void* sv = nullptr;
        if (SUCCEEDED(g_selScroller->QueryInterface(DevToolsIid<DevToolsXC::IScrollViewer>(), &sv)) && sv) {
            if (g_selScrollChangingTok) DevToolsScrollRemoveViewChanging(sv, g_selScrollChangingTok);
            if (g_selScrollChangedTok)  DevToolsScrollRemoveViewChanged(sv, g_selScrollChangedTok);
            reinterpret_cast<IUnknown*>(sv)->Release();
        }
        g_selScroller->Release(); g_selScroller = nullptr;
    }
    g_selScrollChangingTok = 0; g_selScrollChangedTok = 0;
    g_scrollSettleAt = 0; g_hiddenForScroll = false;
    g_selScrollAnchor = {};
    g_selScrollAnchorValid = false;
    g_selScrollLiveSeen = false;
}

// A failed subscription leaves the non-scroll tracking timer in place; it must not create a partially wired
// event lifetime.
// Failed ScrollViewer subscription leaves timer-only tracking, not a half-wired event lifetime.
static void WireSelScroller(const RECT& bounds)
{
    ClearSelScroller();
    if (!g_pickDiag || !g_pickRoot) return;
// Content-DIP centre of the selection -> screen, because ScrollerAt takes a screen point (it shares the pick's
// coordinate contract).
    DevToolsSurface surface;
    POINT p{};
    if (!DevToolsSurface_Resolve(g_pickDiag, g_pickRoot, &surface) ||
        !DevToolsSurface_LocalToScreen(surface, (bounds.left + bounds.right) / 2.0f,
            (bounds.top + bounds.bottom) / 2.0f, &p)) {
        DevToolsOverlayLog(L"overlay.track selection coordinate conversion unavailable");
        return;
    }

    InstanceHandle svh = 0;
    IInspectable* sv = ScrollerAt(g_pickDiag, g_pickRoot, p.x, p.y, g_pickIsOverlay, false, &svh);
    if (!sv) sv = ScrollerAt(g_pickDiag, g_pickRoot, p.x, p.y, g_pickIsOverlay, true, &svh);
    if (!sv) { DevToolsOverlayLog(L"overlay.track selection has no ancestor scroller (no live scroll signal)"); return; }

    void* scroller = nullptr;
    if (SUCCEEDED(sv->QueryInterface(DevToolsIid<DevToolsXC::IScrollViewer>(), &scroller)) && scroller) {
        g_viewChangingSink.Bind(&IID_ViewChangingHandler_Del, &OnSelScrollViewChanging);
        g_viewChangedSink.Bind(&IID_ViewChangedHandler_Del, &OnSelScrollViewChanged);
        HRESULT h1 = DevToolsScrollAddViewChanging(scroller, &g_viewChangingSink, &g_selScrollChangingTok);
        HRESULT h2 = DevToolsScrollAddViewChanged(scroller, &g_viewChangedSink, &g_selScrollChangedTok);
        DevToolsOverlayLog(L"overlay.track scroller wired add_ViewChanging hr=0x%08X tok=%lld | add_ViewChanged hr=0x%08X tok=%lld",
            h1, (long long)g_selScrollChangingTok, h2, (long long)g_selScrollChangedTok);
        if (FAILED(h1) || FAILED(h2)) {
            if (SUCCEEDED(h1)) DevToolsScrollRemoveViewChanging(scroller, g_selScrollChangingTok);
            if (SUCCEEDED(h2)) DevToolsScrollRemoveViewChanged(scroller, g_selScrollChangedTok);
            g_selScrollChangingTok = 0; g_selScrollChangedTok = 0;
            reinterpret_cast<IUnknown*>(scroller)->Release();
            sv->Release();
            DevToolsOverlayLog(L"overlay.track scroller wiring incomplete -> live tracking unavailable");
            return;
        }
        reinterpret_cast<IUnknown*>(scroller)->Release();
        g_selScroller = sv;
        RefreshScrollAnchor(g_selectedBounds);
        DevToolsOverlayLog(L"overlay.track scroll anchor offsets readable=%d h=%.2f v=%.2f",
            (int)g_selScrollAnchorValid, g_selScrollAnchor.horizontalOffset, g_selScrollAnchor.verticalOffset);
        return;
    }
    sv->Release();
}

static void TrackingSync()
{
    const bool need = (g_selectedHandle != 0) || !g_pins.empty();
    if (need && !g_trackTimer) {
        g_trackTimer = SetTimer(nullptr, 0, kTrackIntervalMs, &TrackingTimerProc);
        g_trackFirstTickPending = true;
        DevToolsOverlayLog(L"overlay.track started (%ums) timer=%llu", kTrackIntervalMs, (unsigned long long)g_trackTimer);
    } else if (!need && g_trackTimer) {
        KillTimer(nullptr, g_trackTimer); g_trackTimer = 0;
        ClearSelScroller();
        DevToolsOverlayLog(L"overlay.track stopped (nothing anchored)");
    }
// arm the census-removal signal alongside the tick, because it exists to cover the case the tick cannot — a
// removal while the app is busy, when WM_TIMER is starved.
    // Census-removal signals cover busy UI periods when WM_TIMER is starved; keep the sink armed and filter by
    // handle.
    if (need && !g_removedSinkState) {
        g_removedSinkState = DevToolsTap_SetNodeRemovedSink(&OnCensusNodeRemoved) ? 1 : -1;
        DevToolsOverlayLog(L"overlay.track census-removal signal %s (%s)",
                      g_removedSinkState == 1 ? L"ARMED" : L"UNAVAILABLE",
                      g_removedSinkState == 1 ? L"tick is no longer the only path"
                                              : L"tick remains the only path");
    }
}

// A true result tells the caller not to deselect the preserved panel.
static bool SelKeepDetachedIfUnsent()
{
    if (!g_selPanel || !g_selComment) return false;
    std::wstring text;
    if (!SelReadCommentText(&text)) return false;
    if (DevToolsCommentText::IsBlank(text)) return false;
    if (DevToolsCommentText::Unchanged(text, g_selCommentSaved)) return false;

    if (IInspectable* strip = DevToolsSelFindName(g_selUi, L"DevToolsSelDetached")) {
        SetOverlayElementVisible(strip, true);
        strip->Release();
    }
    SetOverlayElementVisible(g_selTag, false);
    if (IInspectable* leader = DevToolsSelFindName(g_selUi, L"DevToolsSelLeader")) {
        SetOverlayElementVisible(leader, false);
        leader->Release();
    }
    if (g_lastHighlightHandle == g_selectedHandle) {
        ClearAdorner();
        g_lastHighlightHandle = 0;
    }
    g_selDetached = true;
    g_selectedHandle = 0;
    g_selectedBounds = RECT{};
    g_chromeHidden = false;
    ClearSelScroller();
    DevToolsOverlayLog(L"overlay.sel element destroyed with UNSAVED comment (%zu chars, saved %zu) -> panel kept, detached",
                  text.size(), g_selCommentSaved.size());
    TrackingSync();
    return true;
}

// The handle comparison is FIRST and costs nothing: a navigation removes hundreds of nodes and all but one are
// not ours - no COM resolve, no lock, no allocation on that path. Runs on a dispatcher turn, NOT inside the
// visual-tree callback, so mutating the tree here is safe in a way that doing it there would not be.
static void OnCensusNodeRemoved(InstanceHandle h)
{
// Both come out of the same OnVisualTreeChange callback and both are legitimate: DevToolsTreeWatch_Subscribe -
// coalesced "the tree moved", no payload. Right for a consumer that re-reads a snapshot and needs ADDITIONS too
// (the window's node-count header must notice a tree that GREW, which a removal-only signal cannot).
    if (!h || h != g_selectedHandle) return;
// At most one resolve per batch, because everything else fails the comparison above.
    // A census Remove can be reparenting or virtualization, so treat it as unresolved rather than permanently
    // gone.
    if (g_pickDiag) {
        IInspectable* live = nullptr;
        if (SUCCEEDED(g_pickDiag->GetIInspectableFromHandle(h, &live)) && live) {
            live->Release();
            DevToolsOverlayLog(L"overlay.track selected element removed but still resolves (reparent/recycle) -> keeping selection");
            return;
        }
    }
    if (SelKeepDetachedIfUnsent()) return;
    DevToolsOverlayLog(L"overlay.track selected element removed from census -> deselect (signal, not tick)");
    DeselectAll(g_lastHighlightHandle == g_selectedHandle);
}

static void TrackingTick()
{
    if (g_surfaceMoving || !g_pickDiag || !g_pickRoot) return;
// The 64-tick cost line below cannot answer this: a tick that never runs logs nothing, which is
// indistinguishable from a tick that runs and early-outs.
    if (g_trackFirstTickPending) {
        g_trackFirstTickPending = false;
        DevToolsOverlayLog(L"overlay.track FIRST TICK ran (the tick is being delivered)");
    }
// Without this guard a tick that pumps re-enters the tick, which nests without bound -- observed as a wedged UI
// thread and, earlier, as a first-chance access violation writing to a stack address (the signature of stack
// exhaustion).
    static bool s_inTick = false;
    if (s_inTick) return;

// This runs once per composed frame on the APP's UI thread -- the same thread whose backlog widens the
// press->dispatch window behind -- so the budget here is not academic: an expensive tick would re-open the bug
// just closed. Accumulated in the guard's destructor so EVERY exit path is counted (most ticks early-out), and
// r.
    static double s_sizeUs = 0.0, s_totalUs = 0.0; static int s_ticks = 0;
    const double tTick0 = QpcNow();
    struct TickGuard {
        double t0;
        ~TickGuard() {
            s_inTick = false;
            s_totalUs += QpcNow() - t0;
            if (++s_ticks >= 64) {
                DevToolsOverlayLog(L"overlay.track.cost ticks=%d avgTotalUs=%.1f avgClientSizeUs=%.1f sel=%llu rect=%ld,%ld,%ld,%ld hidden=%d",
                    s_ticks, s_totalUs / s_ticks, s_sizeUs / s_ticks,
                    (unsigned long long)g_selectedHandle, g_selectedBounds.left, g_selectedBounds.top,
                    g_selectedBounds.right, g_selectedBounds.bottom, (int)g_chromeHidden);
                s_ticks = 0; s_totalUs = 0.0; s_sizeUs = 0.0;
            }
        }
    } guard{ tTick0 };
    s_inTick = true;
    WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::OverlayTrackTick);

    const double tSize0 = QpcNow();
    int cw = (int)g_activeSurface.contentW, ch = (int)g_activeSurface.contentH;
    if (cw <= 0 || ch <= 0) {
        if (!GetClientSizeDips(&cw, &ch)) { cw = 4000; ch = 4000; }
    }
    s_sizeUs += QpcNow() - tSize0;

// Park markers whose elements are not presented; keep the saved registry unchanged.
    {
    WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::OverlayPins);
    for (auto& p : g_pins) {
        if (!g_commentsShown) continue;
        RECT nb{};
        std::wstring currentAnchor;
        const bool sameSource = p.name.empty() || (g_anchorOf && p.handle &&
            g_anchorOf(p.handle, &currentAnchor) && currentAnchor == p.name);
        bool ok = sameSource && p.handle && TryElementRootBounds(g_pickDiag, g_pickRoot, p.handle, &nb);
// Re-resolve by source identity before concluding it is gone, so a returning element gets its marker back
// instead of the marker parking forever while the toolbar badge still counts the comment (toggle and screen
// must never disagree).
        if (!ok && g_resolveAnchor && !p.name.empty()) {
            InstanceHandle again = g_resolveAnchor(p.name.c_str());
            if (again && again != p.handle && TryElementRootBounds(g_pickDiag, g_pickRoot, again, &nb)) {
                DevToolsOverlayLog(L"overlay.pin re-anchored '%s' after rebuild (handle %llu -> %llu)",
                              p.name.c_str(), (unsigned long long)p.handle, (unsigned long long)again);
                p.handle = again;
                if (p.sink) p.sink->handle = again;
                ok = true;
            }
        }
        if (!ok || !AnchorVisible(p.handle, nb, cw, ch)) {
            SetOverlayElementVisible(p.ui, false);
            continue;
        }
        p.bounds = nb;
        if (!p.ui) {
            DrawPinUi(p);
            continue;
        }
        const auto pin = DevToolsSelection::PlacePin(
            { nb.left, nb.top, nb.right, nb.bottom }, DevToolsSelection::PinViewport(cw, ch, PinTitleBarInsetDip()));
        MoveOverlayElement(p.ui, (double)pin.x, (double)pin.y);
        SetOverlayElementVisible(p.ui, true);
    }
    }

    if (!g_selectedHandle) return;

// Do not mix diagnostics or geometry reads into that path; virtualizing relayout can disconnect the selected
// element while its diagnostics handle still lives.
    if (g_scrollSettleAt && GetTickCount64() < g_scrollSettleAt) return;
    if (g_hiddenForScroll) {
// This covers a scroll that stops without one (a cancelled manipulation, a scroller that goes away mid-gesture)
// so the chrome can never be left hidden with a live selection behind it.
        DevToolsOverlayLog(L"overlay.track scroll timed out -> re-resolving");
        UnhideAfterScroll();
    }

// Note this only reads the handle and never touches the element, so it cannot dereference a subtree XAML is
// mid-way through tearing down.
    IInspectable* live = nullptr;
    if (FAILED(g_pickDiag->GetIInspectableFromHandle(g_selectedHandle, &live)) || !live) {
        if (SelKeepDetachedIfUnsent()) return;
        DevToolsOverlayLog(L"overlay.track selected element left the tree -> deselect");
        DeselectAll(g_lastHighlightHandle == g_selectedHandle);
        return;
    }
    live->Release();

    RECT nb{};
    if (!TryElementRootBounds(g_pickDiag, g_pickRoot, g_selectedHandle, &nb)) {
        if (!g_chromeHidden) {
// Hidden with Visibility, never by removing it from the Canvas: this also runs on the frame tick, where a child
// removal is the mutation that faults WinUI mid-frame.
            // Hide adorners with Visibility on the frame tick; removing children mid-frame can fault WinUI.
            if (g_lastHighlightHandle == g_selectedHandle) SetOverlayElementVisible(g_adorner, false);
            SetOverlayElementVisible(g_selUi, false); g_chromeHidden = true;
            SetLayoutAdornerVisible(false);
            DevToolsOverlayLog(L"overlay.track selection unmeasurable -> chrome hidden (selection kept)");
        }
        return;
    }

    const bool rectChanged = (nb.left != g_selectedBounds.left) || (nb.top != g_selectedBounds.top) ||
                             (nb.right != g_selectedBounds.right) || (nb.bottom != g_selectedBounds.bottom);
    if (!rectChanged && !g_chromeHidden) return;

    const bool visible = AnchorVisible(g_selectedHandle, nb, cw, ch);
    if (!visible) {
        if (!g_chromeHidden) {
            if (g_lastHighlightHandle == g_selectedHandle) SetOverlayElementVisible(g_adorner, false);
            SetOverlayElementVisible(g_selUi, false); g_chromeHidden = true;
            SetLayoutAdornerVisible(false);
            DevToolsOverlayLog(L"overlay.track selection scrolled out of view -> chrome hidden (NOT deselected)");
        }
        g_selectedBounds = nb;
        RefreshScrollAnchor(nb);
        return;
    }
    if (g_chromeHidden) {
        g_chromeHidden = false;
        if (g_lastHighlightHandle == g_selectedHandle) SetOverlayElementVisible(g_adorner, true);
        SetOverlayElementVisible(g_selUi, true);
        DevToolsOverlayLog(L"overlay.track selection back in view -> chrome restored");
    }
    g_selectedBounds = nb;
    g_lastPickBoundsDip = nb;
    PlaceAdorner(g_pickDiag, nb.left, nb.top, nb.right - nb.left, nb.bottom - nb.top, g_pickSeed);
    SyncLayoutAdorner();
    if (g_logRestoredRect) {
        g_logRestoredRect = false;
        DevToolsOverlayLog(L"overlay.track restored after scroll rect=%ld,%ld,%ld,%ld visible=%d",
            nb.left, nb.top, nb.right, nb.bottom, (int)!g_chromeHidden);
    }
    ReanchorSelectionAnchor(nb);
    RefreshScrollAnchor(nb);
}
