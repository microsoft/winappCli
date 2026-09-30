// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsOverlay.cpp; included into that translation unit, not compiled on its own.

static IInspectable* g_aRow = nullptr;
static IInspectable* g_railL = nullptr;
static IInspectable* g_snapUi[kCornerCount] = {};
static bool          g_aRowOpen   = false;
static bool          g_pinned     = false;
static double        g_protoW = 0.0;
static double        g_protoH = 0.0;
static double        g_chipW = 104.0;
static EventSink g_protoSink[15];
static int g_toolbarFocusWithin = 0;
static EventSink g_resizeSink;
static EventSink g_originSink;
static EventSink g_selectionOriginSink, g_selectionResizeSink;
static EventSink g_highlightOriginSink;

// Inspect-mode hover state (pick catcher PointerMoved): the last element handle we highlighted and the last
// hit-test tick, so we debounce -- only re-run the (edge-probing) pick+adorner when the hovered element
// actually changes, and rate-limit the cheap peek hit-test so a fast drag can't hammer the app UI thread.
static InstanceHandle g_hoverLastHandle = 0;
static bool           g_hoverLastRaw = false;
static ULONGLONG      g_hoverLastTick = 0;
static void OnCatcherPointerMoved(void*, void*);

// Host visibility is not root lifetime; hidden or minimized hosts still own toolbar state.
static POINT     g_pressPt{ 0, 0 };
static ULONGLONG g_pressTick = 0;
static bool      g_pressValid = false;
static void OnCatcherPointerPressed(void*, void*);
static int       g_pressDriftMax = 0;
// The fix shipped broken precisely because add_ returning S_OK with a valid token was mistaken for the handler
// working; a Button was swallowing PointerPressed and the sink was never invoked. These make the difference
// between "subscribed" and "invoked" visible in the log.
static bool      g_catcherPressSeen = false;
static bool      g_catcherReleaseSeen = false;

static InstanceHandle g_selectedHandle = 0;
static RECT           g_selectedBounds{};
static bool           g_chromeHidden = false;
static void ClearAdorner();
static void DeselectAll(bool clearHighlight = true);
static HRESULT ReadHostRootIdentity(IInspectable* element, winrt::com_ptr<IUnknown>& identity);
static bool SelKeepDetachedIfUnsent();
static void TrackingTick();
static void TrackingSync();
static void ExitPickMode();
static bool DismissSelectionPanel(bool discardDetached = false);
static bool DevToolsSelPanelIsOpen();
static bool PickerEditorIsOpen();
static void UpdateCatcherHole();
static void ResetPickPointer()
{
    g_pressValid = false;
    // After wheel scroll, reset hover and let the next move/tick settle instead of doing synchronous hit-test
    // churn.
    g_hoverLastHandle = 0;
    g_hoverLastTick = 0;
}

static bool IsCurrentCatcher(void* sender)
{
    if (!g_pickCatcher || !sender) return false;
    winrt::com_ptr<IUnknown> root, current, origin;
    return SUCCEEDED(ReadHostRootIdentity(g_selectionPanel.get(), root)) &&
        root && root.get() == g_selectionRootIdentity.get() &&
        SUCCEEDED(g_pickCatcher->QueryInterface(IID_IUnknown, current.put_void())) &&
        SUCCEEDED(static_cast<IUnknown*>(sender)->QueryInterface(IID_IUnknown, origin.put_void())) &&
        current && current.get() == origin.get();
}

static void OnCatcherPointerCanceled(void* sender, void*)
{
    if (IsCurrentCatcher(sender)) ResetPickPointer();
}
static void OnCensusNodeRemoved(InstanceHandle h);
static int  g_removedSinkState = 0;
// The app's own content root is retained for toolbar actions that must affect the app rather than the overlay:
// PreviewKeyDown makes Esc work while focus is in app content, and RequestedTheme switches that content's
// effective theme.
static IInspectable* g_appPanel = nullptr;
static EventSink     g_escKeySink;
static winrt::com_ptr<IUnknown> g_hostRootIdentity;
static unsigned long long g_hostGeneration = 0;
static InstanceHandle g_failedRecoveryPanel = 0;

static HRESULT ReadHostRootIdentity(IInspectable* element, winrt::com_ptr<IUnknown>& identity)
{
    if (!element) return E_INVALIDARG;
    winrt::com_ptr<IInspectable> ui, root;
    HRESULT hr = element->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), ui.put_void());
    if (FAILED(hr) || !ui) return FAILED(hr) ? hr : E_NOINTERFACE;
    hr = DevToolsGetXamlRoot(ui.get(), root.put());
    if (FAILED(hr) || !root) return FAILED(hr) ? hr : E_PENDING;
    return root->QueryInterface(IID_IUnknown, identity.put_void());
}

static HRESULT ReadHostAttachment(bool* attached)
{
    *attached = false;
    if (!g_appPanel || !g_popup || !g_popupUi) return S_OK;
    winrt::com_ptr<IInspectable> fe, panel, children, vector;
    HRESULT hr = g_appPanel->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), fe.put_void());
    if (FAILED(hr) || !fe) return FAILED(hr) ? hr : E_NOINTERFACE;
    bool loaded = false, open = false;
    hr = DevToolsGetIsLoaded(fe.get(), &loaded);
    if (FAILED(hr) || !loaded) return hr;
    winrt::com_ptr<IUnknown> identity;
    hr = ReadHostRootIdentity(g_appPanel, identity);
    if (FAILED(hr)) return hr;
    if (g_hostRootIdentity && identity.get() != g_hostRootIdentity.get()) return S_OK;
    if (HostLayerAttached(g_appPanel, g_popupUi, attached)) return S_OK;
    hr = g_appPanel->QueryInterface(DevToolsIid<DevToolsXC::IPanel>(), panel.put_void());
    if (FAILED(hr) || !panel) return FAILED(hr) ? hr : E_NOINTERFACE;
    hr = DevToolsPanelGetChildren(panel.get(), children.put());
    if (FAILED(hr) || !children) return FAILED(hr) ? hr : E_NOINTERFACE;
    hr = children->QueryInterface(DevToolsIid<DevToolsUIElementVector>(), vector.put_void());
    if (FAILED(hr) || !vector) return FAILED(hr) ? hr : E_NOINTERFACE;
    unsigned index = 0;
    unsigned char found = 0;
    hr = DevToolsVecIndexOf(vector.get(), g_popupUi, &index, &found);
    if (FAILED(hr) || !found) return hr;
    hr = DevToolsPopupGetIsOpen(g_popup, &open);
    if (SUCCEEDED(hr)) *attached = open;
    return hr;
}

// A hidden or minimized host still owns its toolbar.
static bool RailRootStillHosting()
{
    bool attached = false;
    if (FAILED(ReadHostAttachment(&attached)) || !attached) return false;
    void* ui = nullptr;
    if (FAILED(g_appPanel->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) || !ui) return false;
    IInspectable* xr = nullptr;
    const HRESULT hr = DevToolsGetXamlRoot(ui, &xr);
    reinterpret_cast<IUnknown*>(ui)->Release();
    if (FAILED(hr) || !xr) return false;
    float w = 0.0f, h = 0.0f;
    const bool sized = SUCCEEDED(DevToolsXamlRootGetSize(xr, &w, &h)) && w > 0.0f && h > 0.0f;
    xr->Release();
    return sized;
}

static bool          g_escWired = false;
static void*         g_escSource = nullptr;
static __int64       g_escToken = 0;
static bool          g_surfaceMoving = false;
static EventSink     g_focusSink;
static bool          g_focusWired = false;
static void*         g_focusSource = nullptr;
static __int64       g_focusToken = 0;
static bool          g_focusTracking = false;
static DevToolsFocusOwners g_focusOwners;

static std::wstring DevToolsInverseAppRequestedTheme()
{
    if (DevToolsBuildPalette().highContrast || !g_appPanel) return L"Default";
    void* fe = nullptr;
    int actual = DevToolsTheme::kDefault;
    if (FAILED(g_appPanel->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) || !fe)
        return L"Default";
    const HRESULT hr = DevToolsGetActualTheme(fe, &actual);
    reinterpret_cast<IUnknown*>(fe)->Release();
    if (FAILED(hr)) return L"Default";
    return actual == DevToolsTheme::kDark ? L"Light" : L"Dark";
}

static void OnAppEscape(void*, void*);
static void DevToolsSelWireKey(IInspectable* el, EventSink& sink, void (*cb)(void*, void*));
static void UnwireAppEscape()
{
    if (!g_escSource) return;
    const HRESULT hr = DevToolsRemovePreviewKeyDown(g_escSource, g_escToken);
    DevToolsOverlayLog(L"overlay.esc remove_PreviewKeyDown hr=0x%08X", hr);
    reinterpret_cast<IUnknown*>(g_escSource)->Release();
    g_escSource = nullptr;
    g_escWired = false;
}
static void WireAppEscapeOnce()
{
    if (g_escWired || !g_selectionPanel) return;
    void* ui = nullptr;
    if (FAILED(g_selectionPanel->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) || !ui) return;
    g_escKeySink.Bind(&IID_KeyEventHandler_Del, &OnAppEscape);
    const HRESULT hr = DevToolsAddPreviewKeyDown(ui, &g_escKeySink, &g_escToken);
    if (SUCCEEDED(hr)) { g_escSource = ui; g_escWired = true; }
    else reinterpret_cast<IUnknown*>(ui)->Release();
    DevToolsOverlayLog(L"overlay.esc add_PreviewKeyDown hr=0x%08X", hr);
}
static void OnCommentsToggleClick(void*, void*);
static void OnThemeToggleClick(void*, void*);
// Swaps the two states authored in toolbar.xaml (DevToolsPickOff glyph / DevToolsPickOn accent chip).
static void SetPickButtonOn(bool on);
static bool CommentModeOn();
static void CommentModeOpenComposer(InstanceHandle picked);
static InstanceHandle PeekTopHandleAt(IXamlDiagnostics*, InstanceHandle, int, int, DevToolsIsOverlayFn, HRESULT* = nullptr);
static void ScreenToContentDip(int screenX, int screenY, long* dipX, long* dipY);
static void LogHandleClass(IXamlDiagnostics* diag, const wchar_t* tag, InstanceHandle h);
static void NoteScrollActivity();
static void NoteExpectedScrollActivity();
static void UnhideAfterScroll();
static void ClearSelScroller();
static void RefreshScrollAnchor(const RECT& bounds);
static void FlushPendingAnchor();
static void WireSelScroller(const RECT& bounds);
static void SetOverlayElementVisible(IInspectable* el, bool visible);
static IInspectable* ScrollerAt(IXamlDiagnostics*, InstanceHandle, int, int, DevToolsIsOverlayFn, bool, InstanceHandle*);
static bool TryElementRootBounds(IXamlDiagnostics* diag, InstanceHandle rootHandle, InstanceHandle target,
                                 RECT* out, bool* noVisibleBox = nullptr);

struct DevToolsLayoutBox {
    RECT box{};
    RECT margin{};
    RECT padding{};
    bool haveMargin = false;
    bool havePadding = false;
};
static bool TryElementLayoutBox(IXamlDiagnostics* diag, InstanceHandle rootHandle, InstanceHandle target,
                                DevToolsLayoutBox* out);
static void SyncLayoutAdorner();
static void SetLayoutAdornerVisible(bool);
static void SetLayoutAdornersOn(bool on);
static void ClearLayoutAdorner();
static void OnLayoutToggleClick(void*, void*);

// The catcher cannot re-dispatch a routed event, so it drives the scroll directly: hit-test the app tree at the
// cursor (the same primitive the pick uses, so our own chrome is excluded), walk the front-to-back stack for
// the nearest ScrollViewer that can move on the axis, and ChangeView it by the notch.
static const double kWheelLinesPerNotch = 3.0;
static const double kWheelPixelsPerLine = 40.0;

// Returns null on failure, which ChangeView reads as "leave this axis alone" -- the safe degradation.
static IInspectable* BoxDouble(double v)
{
    static IInspectable* s_pvStatics = nullptr;
    if (!s_pvStatics) {
        HSTRING cls = nullptr;
        const wchar_t* name = L"Windows.Foundation.PropertyValue";
        if (FAILED(WindowsCreateString(name, (UINT32)wcslen(name), &cls))) return nullptr;
        HRESULT hr = RoGetActivationFactory(cls, DevToolsIid<winrt::Windows::Foundation::IPropertyValueStatics>(),
                                            reinterpret_cast<void**>(&s_pvStatics));
        WindowsDeleteString(cls);
        if (FAILED(hr)) { s_pvStatics = nullptr; return nullptr; }
    }
    IInspectable* box = nullptr;
    if (FAILED(DevToolsPropertyValueCreateDouble(s_pvStatics, v, &box)) || !box) return nullptr;
// ChangeView's ABI takes an IReference<double>*, not an IInspectable*. Handing it the IInspectable vtable is a
// silent no-op: the call returns S_OK and `changed` comes back false.
    IInspectable* ref = nullptr;
    HRESULT hq = box->QueryInterface(DevToolsIid<winrt::Windows::Foundation::IReference<double>>(),
                                     reinterpret_cast<void**>(&ref));
    box->Release();
    return SUCCEEDED(hq) ? ref : nullptr;
}

// The nearest ScrollViewer at this SCREEN point that has room to move on the requested axis. Returns an
// AddRef'd IScrollViewer-capable IInspectable (caller releases), or null when nothing under the cursor scrolls
// -- in which case the notch is simply dropped, exactly as it would be if the app itself had no scroller there.
static IInspectable* ScrollerAt(IXamlDiagnostics* diag, InstanceHandle rootHandle, int screenX, int screenY,
                                DevToolsIsOverlayFn isOverlay, bool wantHorizontal, InstanceHandle* outHandle)
{
    if (outHandle) *outHandle = 0;
    if (!diag || !rootHandle) return nullptr;
    IXamlDiagnostics2_Abi* diag2 = CachedDiag2(diag);
    DevToolsSurfacePoint point{};
    DevToolsOverlay_ClassifyPickPoint(diag, rootHandle, screenX, screenY, &point);
    if (!point.resolved || point.outside) return nullptr;
    const long px = point.contentDipX, py = point.contentDipY;
    RECT r{ px, py, px + 1, py + 1 };
    unsigned int count = 0; InstanceHandle* hs = nullptr;
    HRESULT hr = diag2 ? diag2->HitTestForXamlRoot(rootHandle, r, &count, &hs) : diag->HitTest(r, &count, &hs);
    IInspectable* found = nullptr;
    if (SUCCEEDED(hr) && count && hs) {
        for (unsigned int i = 0; i < count && !found; ++i) {
            if (!hs[i] || (isOverlay && isOverlay(hs[i]))) continue;
            IInspectable* insp = nullptr;
            if (FAILED(diag->GetIInspectableFromHandle(hs[i], &insp)) || !insp) continue;
            void* sv = nullptr;
            if (SUCCEEDED(insp->QueryInterface(DevToolsIid<DevToolsXC::IScrollViewer>(), &sv)) && sv) {
                double room = 0.0;
                const HRESULT hres = wantHorizontal ? DevToolsScrollGetScrollableWidth(sv, &room)
                                                    : DevToolsScrollGetScrollableHeight(sv, &room);
                if (SUCCEEDED(hres) && room > 0.5) { found = insp; insp = nullptr; if (outHandle) *outHandle = hs[i]; }
                reinterpret_cast<IUnknown*>(sv)->Release();
            }
            if (insp) insp->Release();
        }
    }
    if (hs) CoTaskMemFree(hs);
    return found;
}

static void OnCatcherPointerWheel(void* sender, void* args)
{
    if (!IsCurrentCatcher(sender) || PickerEditorIsOpen()) return;
    if (!g_pickCatcher || !g_pickDiag || !g_pickRoot || !args) return;
    static bool s_wheelSeen = false;
    if (!s_wheelSeen) { s_wheelSeen = true; DevToolsOverlayLog(L"overlay.wheel sink FIRED (forwarding is live)"); }

// Read the notch off the routed args (the only thing we need from them; the position comes from GetCursorPos,
// like every other pick coordinate in this file).
    IInspectable* ai = static_cast<IInspectable*>(args);
    int delta = 0; bool horizontal = false;
    {
        void* pargs = nullptr;
        if (FAILED(ai->QueryInterface(DevToolsIid<DevToolsXI::IPointerRoutedEventArgs>(), &pargs)) || !pargs) return;
        IInspectable* pt = nullptr;
        if (SUCCEEDED(DevToolsArgsGetCurrentPoint(pargs, nullptr, &pt)) && pt) {
            IInspectable* props = nullptr;
            void* pp = nullptr;
            if (SUCCEEDED(pt->QueryInterface(DevToolsIid<DevToolsIn::IPointerPoint>(), &pp)) && pp) {
                if (SUCCEEDED(DevToolsPointGetProperties(pp, &props)) && props) {
                    void* pr = nullptr;
                    if (SUCCEEDED(props->QueryInterface(DevToolsIid<DevToolsIn::IPointerPointProperties>(), &pr)) && pr) {
                        DevToolsPointPropsGetWheelDelta(pr, &delta);
                        DevToolsPointPropsGetIsHorizontalWheel(pr, &horizontal);
                        reinterpret_cast<IUnknown*>(pr)->Release();
                    }
                    props->Release();
                }
                reinterpret_cast<IUnknown*>(pp)->Release();
            }
            pt->Release();
        }
// Mark it handled either way: the catcher owns the pointer while pick mode is armed, and letting the event
// bubble on through the overlay's own tree achieves nothing.
        DevToolsArgsPutHandled(pargs, true);
        reinterpret_cast<IUnknown*>(pargs)->Release();
    }
    if (!delta) { DevToolsOverlayLog(L"overlay.wheel no delta on args"); return; }

    POINT cur{ 0, 0 }; GetCursorPos(&cur);
    InstanceHandle svHandle = 0;
    IInspectable* sv = ScrollerAt(g_pickDiag, g_pickRoot, cur.x, cur.y, g_pickIsOverlay, horizontal, &svHandle);
    if (!sv) {
        DevToolsOverlayLog(L"overlay.wheel delta=%d horiz=%d -> no scroller under cursor %d,%d", delta, (int)horizontal, cur.x, cur.y);
        return;
    }
    LogHandleClass(g_pickDiag, L"wheel.scroller", svHandle);

    void* scroller = nullptr;
    if (SUCCEEDED(sv->QueryInterface(DevToolsIid<DevToolsXC::IScrollViewer>(), &scroller)) && scroller) {
        double offset = 0.0;
        const HRESULT hg = horizontal ? DevToolsScrollGetHorizontalOffset(scroller, &offset)
                                      : DevToolsScrollGetVerticalOffset(scroller, &offset);
        if (SUCCEEDED(hg)) {
            UINT lines = 0;
            if (!SystemParametersInfoW(SPI_GETWHEELSCROLLLINES, 0, &lines, 0) || lines == 0 || lines > 100)
                lines = (UINT)kWheelLinesPerNotch;
            const double step = ((double)delta / 120.0) * (double)lines * kWheelPixelsPerLine;
            double target = offset - step;
            if (target < 0.0) target = 0.0;
            double room = 0.0;
            if (horizontal) DevToolsScrollGetScrollableWidth(scroller, &room); else DevToolsScrollGetScrollableHeight(scroller, &room);
            if (room > 0.0 && target > room) target = room;
            IInspectable* box = BoxDouble(target);
            bool changed = false;
            HRESULT hcv = DevToolsScrollChangeView(scroller, horizontal ? box : nullptr, horizontal ? nullptr : box,
                                              nullptr, true /*disableAnimation*/, &changed);
            DevToolsOverlayLog(L"overlay.wheel delta=%d horiz=%d offset=%.1f -> %.1f scrollable=%.1f ChangeView hr=0x%08X changed=%d",
                delta, (int)horizontal, offset, target, room, hcv, (int)changed);
            if (box) box->Release();
            g_wheelForwards++;
        }
        reinterpret_cast<IUnknown*>(scroller)->Release();
    }
    sv->Release();

// The ViewChanging sink owns selection tracking; do not pre-hide the chrome before that signal arrives.
    NoteExpectedScrollActivity();

// That is an access violation in Microsoft.UI.Xaml.dll: an IXamlDiagnostics hit-test against a tree being laid
// out. Clearing the debounce state instead means the preview re-resolves on the next real pointer move, so the
// rect pauses during the scroll and snaps to the right element as soon as the user moves the mouse -- wh.
    g_hoverLastHandle = 0;
    g_hoverLastTick = 0;
}

// Pick-mode step 1.5: the pointer went DOWN on the catcher. Latch the press point -- this, not the position the
// Click handler later observes, is the coordinate the user aimed at.
// Latch the press point; Click runs after release and can observe a drifted cursor.
static void OnCatcherPointerPressed(void* sender, void*)
{
    if (!IsCurrentCatcher(sender) || PickerEditorIsOpen()) return;
    if (!g_catcherPressSeen) {
        g_catcherPressSeen = true;
        DevToolsOverlayLog(L"overlay.pickmode PointerPressed sink FIRED (press-point commit is live)");
    }
    GetCursorPos(&g_pressPt);
    g_pressTick = GetTickCount64();
    g_pressValid = true;
}

// Pick mode stays armed, so The catcher sits at Canvas.ZIndex 0, below the toolbar (1000), pins (1900) and the
// selection anchor (2000-2002), so staying armed never swallows clicks on our own chrome.
static void OnCatcherClick(void* sender, void*)
{
    if (!IsCurrentCatcher(sender)) return;
    // With the panel open, a catcher click is outside the modal panel and commits/dismisses before picking.
    if (DevToolsSelPanelIsOpen()) {
        DevToolsOverlayLog(L"overlay.pick click outside the open panel -> dismiss (modal), pick mode stays armed");
        g_pressValid = false;
        DismissSelectionPanel();
        return;
    }
    if (PickerEditorIsOpen() || !g_pressValid) return;
    if (!g_catcherReleaseSeen) {
        g_catcherReleaseSeen = true;
        DevToolsOverlayLog(L"overlay.pickmode PointerReleased sink FIRED (commit path is live)");
    }
    POINT cur{ 0, 0 }; GetCursorPos(&cur);
    POINT pt = g_pressValid ? g_pressPt : cur;
    int drift = 0;
    if (g_pressValid) {
        int dx = cur.x - g_pressPt.x, dy = cur.y - g_pressPt.y;
        drift = (abs(dx) > abs(dy)) ? abs(dx) : abs(dy);
        if (drift > g_pressDriftMax) g_pressDriftMax = drift;
        DevToolsOverlayLog(L"overlay.pick.commit source=press press=%d,%d clickHandler=%d,%d driftPx=%d maxDriftPx=%d ageMs=%llu",
            g_pressPt.x, g_pressPt.y, cur.x, cur.y, drift, g_pressDriftMax,
            (unsigned long long)(GetTickCount64() - g_pressTick));
    } else {
        DevToolsOverlayLog(L"overlay.pick.commit source=cursor(no-press-seen) clickHandler=%d,%d", cur.x, cur.y);
    }
    const POINT wouldHaveBeen = cur;
    const bool  probeDrift = g_pressValid && drift > 0;
    g_pressValid = false;
    LogPickOrigin();
    g_hoverLastHandle = 0;
    if (g_pickDiag && g_pickRoot) {
// On cursor drift, compare press-time and handler-time hits for opt-in diagnostics.
        if (probeDrift) {
            InstanceHandle atPress = PeekTopHandleAt(g_pickDiag, g_pickRoot, pt.x, pt.y, g_pickIsOverlay);
            InstanceHandle atClick = PeekTopHandleAt(g_pickDiag, g_pickRoot, wouldHaveBeen.x, wouldHaveBeen.y, g_pickIsOverlay);
            DevToolsOverlayLog(L"overlay.pick.divergence %s press=%d,%d->%llu clickHandler=%d,%d->%llu",
                (atPress == atClick) ? L"same" : L"DIVERGED (pre-fix would have picked the wrong element)",
                pt.x, pt.y, (unsigned long long)atPress,
                wouldHaveBeen.x, wouldHaveBeen.y, (unsigned long long)atClick);
        }
        InstanceHandle picked = 0; RECT b{};
        const HRESULT hr = DevToolsOverlay_Pick(g_pickDiag, g_pickRoot, pt.x, pt.y, g_pickSeed, g_pickIsOverlay, &picked, &b);
        if (FAILED(hr)) {
            DevToolsOverlayLog(L"overlay.pick commit failed hr=0x%08X", hr);
            return;
        }

        if (!picked) {
            DevToolsOverlayLog(L"overlay.pick empty hit -> deselect (sticky pick stays armed)");
            DeselectAll();
            return;
        }

// A pick armed from the DevTools window is claimed by that window, so the ordinary in-app panel must not open
// over the app before focus returns.
        const bool claimedByWindow = picked && g_pickedSink && g_pickedSink(picked);
        if (picked) { g_selectedHandle = picked; g_selectedBounds = b; g_chromeHidden = false; WireSelScroller(b); }
// Display-only badge, so no async TextBox parts realize on a plain pick.
        if (picked && CommentModeOn()) {
            CommentModeOpenComposer(picked);
        } else if (picked && g_cardRead && !claimedByWindow) {
            DevToolsOverlay_ShowBadgeForHandle(g_pickDiag, picked, /*fromUserPick*/ true);
        }
        TrackingSync();
    }
}

// Otherwise insert a transparent, full-canvas catcher on top of the overlay so pointer input anywhere is
// caught.
static void OnPickClick(void* sender, void*)
{
    if (!DevToolsTrust_MutationEnabled()) {
        DevToolsOverlayLog(L"overlay.pick refused by process posture");
        if (g_pickCatcher) { ExitPickMode(); DeselectAll(); }
        return;
    }
// Sticky pick was toggleable before, but toggling off left the rect, the edit affordance and the panel on
// screen, so "how do I unselect this?" had no answer -- turning the mode off is the answer, and now it works.
    if (g_pickCatcher) { ExitPickMode(); DeselectAll(); return; }
    if (sender && g_selectionHost && FAILED(g_selectionHost(g_toolbarSurface.rootHandle, true))) {
        DevToolsOverlayLog(L"overlay.pick could not establish the toolbar window's selection host");
        return;
    }
    if (!g_canvasChildren) return;
    int w = 0, h = 0; if (!GetClientSizeDips(&w, &h)) { w = 4000; h = 4000; }
    std::wstring markup = DevToolsXamlSubst(DevToolsXaml::PickCatcher, {
        { L"W", std::to_wstring(w) }, { L"H", std::to_wstring(h) } });
    IInspectable* catcher = nullptr;
    if (FAILED(LoadMarkup(markup, &catcher)) || !catcher) return;
    if (g_pickDiag && g_pickSeed) {
        InstanceHandle ch = 0;
        if (SUCCEEDED(g_pickDiag->GetHandleFromIInspectable(catcher, &ch)) && ch) g_pickSeed(ch);
    }
// The catcher is driven entirely by POINTER EVENTS -- no ButtonBase, no Click. It must NOT be a Button.
    // Picker uses pointer events, not ButtonBase, because ButtonBase handles press before subscriptions see it.
    ResetPickPointer();
    g_catcherPressSeen = false; g_catcherReleaseSeen = false;
    HRESULT wired = E_NOINTERFACE;
    {
        void* cuiEl = nullptr;
        if (SUCCEEDED(catcher->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &cuiEl)) && cuiEl) {
            g_catcherMovedSink.Bind(&IID_PointerEventHandler_Del, &OnCatcherPointerMoved);
            g_catcherPressedSink.Bind(&IID_PointerEventHandler_Del, &OnCatcherPointerPressed);
            g_catcherSink.Bind(&IID_PointerEventHandler_Del, &OnCatcherClick);
            g_catcherWheelSink.Bind(&IID_PointerEventHandler_Del, &OnCatcherPointerWheel);
            g_catcherCanceledSink.Bind(&IID_PointerEventHandler_Del, &OnCatcherPointerCanceled);
            g_catcherCaptureLostSink.Bind(&IID_PointerEventHandler_Del, &OnCatcherPointerCanceled);
            __int64 mtok = 0, ptok = 0, rtok = 0, wtok = 0, ctok = 0, ltok = 0;
            HRESULT hrP = DevToolsAddPointerPressed(cuiEl, &g_catcherPressedSink, &ptok);
            HRESULT hrM = DevToolsAddPointerMoved(cuiEl, &g_catcherMovedSink, &mtok);
            HRESULT hrR = DevToolsAddPointerReleased(cuiEl, &g_catcherSink, &rtok);
            HRESULT hrW = DevToolsAddPointerWheelChanged(cuiEl, &g_catcherWheelSink, &wtok);
            const HRESULT hrC = DevToolsAddPointerCanceled(cuiEl, &g_catcherCanceledSink, &ctok);
            const HRESULT hrL = DevToolsAddPointerCaptureLost(cuiEl, &g_catcherCaptureLostSink, &ltok);
            wired = S_OK;
            for (const HRESULT result : {hrP, hrM, hrR, hrW, hrC, hrL})
                if (FAILED(result) && SUCCEEDED(wired)) wired = result;
            DevToolsOverlayLog(L"overlay.pickmode catcher=Border add_PointerPressed hr=0x%08X tok=%lld | add_PointerMoved hr=0x%08X tok=%lld | add_PointerReleased hr=0x%08X tok=%lld | add_PointerWheelChanged hr=0x%08X tok=%lld",
                hrP, (long long)ptok, hrM, (long long)mtok, hrR, (long long)rtok, hrW, (long long)wtok);
            reinterpret_cast<IUnknown*>(cuiEl)->Release();
        }
    }
    if (FAILED(wired)) {
        DevToolsOverlayLog(L"overlay.pickmode refused: pointer subscription failed hr=0x%08X", wired);
        catcher->Release();
        return;
    }
    IInspectable* cui = nullptr;
    if (SUCCEEDED(catcher->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), reinterpret_cast<void**>(&cui))) && cui) {
        unsigned int n = 0;
        wired = DevToolsVecGetSize(g_canvasChildren, &n);
        if (SUCCEEDED(wired)) wired = DevToolsVecInsertAt(g_canvasChildren, n, cui);
        cui->Release();
    } else {
        wired = E_NOINTERFACE;
    }
    if (FAILED(wired)) {
        DevToolsOverlayLog(L"overlay.pickmode refused: catcher insertion failed hr=0x%08X", wired);
        catcher->Release();
        return;
    }
    g_pickCatcher = catcher;
    UpdateCatcherHole();
    SetPickButtonOn(true);
    WireAppEscapeOnce();
    DevToolsOverlayLog(L"overlay.pickmode armed (%dx%d catcher, sticky)", w, h);
    NotifyStateChanged();
}

void DevToolsOverlay_SetPickedSink(DevToolsPickedFn sink) { g_pickedSink = sink; }
void DevToolsOverlay_SetPickTarget(DevToolsPickTargetFn target, DevToolsPickRawFn raw) { g_pickTarget = target; g_pickRaw = raw; }
void DevToolsOverlay_SetFocusedSink(DevToolsFocusedFn sink) { g_focusedSink = sink; }
void DevToolsOverlay_SetAnchorBridge(DevToolsAnchorOfFn a, DevToolsResolveAnchorFn r, DevToolsWireOfFn w)
{ g_anchorOf = a; g_resolveAnchor = r; g_wireOf = w; }

static void OnAppGotFocus(void*, void* args)
{
    if (g_surfaceMoving || !g_focusTracking || !args || !g_pickDiag) return;

    void* routedArgs = nullptr;
    if (FAILED(reinterpret_cast<IInspectable*>(args)->QueryInterface(
            DevToolsIid<DevToolsX::IRoutedEventArgs>(), &routedArgs)) || !routedArgs) return;
    IInspectable* source = nullptr;
    HRESULT hrSource = DevToolsGetOriginalSource(routedArgs, &source);
    reinterpret_cast<IUnknown*>(routedArgs)->Release();
    if (FAILED(hrSource) || !source) return;

    InstanceHandle focused = 0;
    HRESULT hrHandle = g_pickDiag->GetHandleFromIInspectable(source, &focused);
    source->Release();
    if (FAILED(hrHandle) || !focused || (g_pickIsOverlay && g_pickIsOverlay(focused)) ||
        focused == g_selectedHandle) return;

    const bool keepCardOpen = DevToolsSelPanelIsOpen();
    HRESULT hr = DevToolsOverlay_HighlightHandle(g_pickDiag, g_pickRoot, focused, g_pickSeed, g_pickIsOverlay);
    if (FAILED(hr) || hr == S_FALSE) return;
    DevToolsOverlay_ShowBadgeForHandle(g_pickDiag, focused, keepCardOpen);
    DevToolsOverlayLog(L"overlay.focus selected handle=%llu cardOpen=%d",
                  (unsigned long long)focused, (int)keepCardOpen);
    if (g_focusedSink) g_focusedSink(focused);
}

static void UnwireFocusTracking()
{
    if (!g_focusSource) return;
    const HRESULT hr = DevToolsRemoveGotFocus(g_focusSource, g_focusToken);
    DevToolsOverlayLog(L"overlay.focus remove_GotFocus hr=0x%08X", hr);
    reinterpret_cast<IUnknown*>(g_focusSource)->Release();
    g_focusSource = nullptr;
    g_focusWired = false;
}

static bool WireFocusTracking()
{
    if (!g_focusWired) {
        if (!g_appPanel) return false;
        void* ui = nullptr;
        if (FAILED(g_appPanel->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) || !ui) return false;
        g_focusSink.Bind(&IID_RoutedEventHandler_Del, &OnAppGotFocus);
        HRESULT hr = DevToolsAddGotFocus(ui, &g_focusSink, &g_focusToken);
        if (FAILED(hr)) {
            reinterpret_cast<IUnknown*>(ui)->Release();
            DevToolsOverlayLog(L"overlay.focus add_GotFocus FAILED hr=0x%08X", hr);
            return false;
        }
        g_focusSource = ui;
        g_focusWired = true;
        DevToolsOverlayLog(L"overlay.focus wired bubbling GotFocus on app content root token=%lld", g_focusToken);
    }
    return true;
}

static bool SetFocusTrackingOwner(bool external, bool enabled, const wchar_t* ownerName)
{
    if (!enabled) {
        g_focusTracking = external ? g_focusOwners.SetExternal(false) : g_focusOwners.SetWindow(false);
        DevToolsOverlayLog(L"overlay.focus owner=%s disabled active=%d", ownerName, (int)g_focusTracking);
        return true;
    }
    if (!WireFocusTracking()) return false;
    g_focusTracking = external ? g_focusOwners.SetExternal(true) : g_focusOwners.SetWindow(true);
    DevToolsOverlayLog(L"overlay.focus owner=%s enabled active=%d", ownerName, (int)g_focusTracking);
    return true;
}

bool DevToolsOverlay_SetFocusTracking(bool enabled)
{
    return SetFocusTrackingOwner(false, enabled, L"window");
}

bool DevToolsOverlay_SetExternalFocusTracking(bool enabled)
{
    return SetFocusTrackingOwner(true, enabled, L"external");
}


// Written once by the CLI over the pipe (DevToolsOverlay_SetCliExe, pipe thread) and read on the UI thread by
// the composer Save handler; held as an atomically-swapped heap string.
// CLI path is a thread-safe handoff from pipe thread to UI thread; null path leaves the composer inert.
static std::atomic<wchar_t*> g_cliExe{nullptr};
struct GuestCommentAuthority {
    std::wstring start, binding, epoch;
    bool Bound() const {
        return DevToolsTrust_ValidGuestContext(start, binding, epoch);
    }
};
static std::atomic<const GuestCommentAuthority*> g_guestCommentAuthority{nullptr};

void DevToolsOverlay_SetGuestComments(const std::wstring& start, const std::wstring& binding, const std::wstring& epoch)
{
    auto context = new GuestCommentAuthority{ start, binding, epoch };
    const GuestCommentAuthority* expected = nullptr;
    if (!g_guestCommentAuthority.compare_exchange_strong(expected, context)) delete context;
}

std::wstring DevToolsOverlay_GuestCommentsJson()
{
    const auto authority = g_guestCommentAuthority.load();
    if (!authority) return L"null";
    const bool bound = authority->Bound();
    return L"{\"mode\":\"" + std::wstring(bound ? L"host" : L"unavailable") +
        L"\",\"start\":\"" + DevToolsJsonEscape(authority->start) +
        L"\",\"binding\":\"" + DevToolsJsonEscape(authority->binding) +
        L"\",\"epoch\":\"" + DevToolsJsonEscape(authority->epoch) + L"\"}";
}

std::wstring DevToolsOverlay_GuestCommentToken(const std::wstring& operation, const std::wstring& revision)
{
    const auto authority = g_guestCommentAuthority.load();
    if (!authority) return L"";
    if (!authority->Bound()) return L"unavailable";
    return std::to_wstring(GetCurrentProcessId()) + L"." + authority->start + L"." + operation + L"." +
        revision + L"." + authority->binding + L"." + authority->epoch;
}

void DevToolsOverlay_SetCliExe(const wchar_t* path)
{
    if (!path || !*path) return;
    wchar_t* copy = _wcsdup(path);
    if (!copy) return;
    wchar_t* old = g_cliExe.exchange(copy);
    if (old) free(old);
    DevToolsOverlayLog(L"overlay.cli exe set: %s", copy);
}

static std::atomic<DevToolsInprocInspectFn> g_inprocInspect{nullptr};

void DevToolsOverlay_SetInprocInspect(DevToolsInprocInspectFn fn) { g_inprocInspect.store(fn); }

void DevToolsOverlay_SetCardBridge(DevToolsCardReadFn read, DevToolsCardReadInputFn readInput, DevToolsCardWriteFn write)
{
    g_cardRead = read; g_cardReadInput = readInput; g_cardWrite = write;
}

void DevToolsOverlay_SetCardScope(DevToolsCardScopeFn scope) { g_cardScope = scope; }

void DevToolsOverlay_SetCardFreeze(DevToolsCardFreezeFn freeze) { g_cardFreeze = freeze; }
void DevToolsOverlay_SetSourceReader(DevToolsSourceReadFn read) { g_srcRead = read; }

// Open the single in-process inspector on the UI thread, or focus its existing window.
static void OnInspectClick(void*, void*)
{
    DevToolsInprocInspectFn inproc = g_inprocInspect.load();
    if (inproc && inproc()) {
        DevToolsOverlayLog(L"overlay.inspect opened in-proc (Route 1) window");
// Auto-save and the do-not-auto-confirm-a-binding rule are the shared dismiss path's, not repeated here.
        DismissSelectionPanel();
        return;
    }
    DevToolsOverlayLog(L"overlay.inspect in-proc route unavailable");
}

static_assert(static_cast<int>(DevToolsX::ElementTheme::Default) == DevToolsTheme::kDefault);
static_assert(static_cast<int>(DevToolsX::ElementTheme::Light) == DevToolsTheme::kLight);
static_assert(static_cast<int>(DevToolsX::ElementTheme::Dark) == DevToolsTheme::kDark);
