// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsOverlay.cpp; included into that translation unit, not compiled on its own.

// ============================ Anchored-chrome tracking ============================ Adorner / anchor / pin
// coordinates must be re-computed continuously, not once at pick time: any scroll, navigation, resize or
// animation would otherwise strand them at coordinates that stopped meaning anything. ScrollViewer offsets
// drive.
static UINT_PTR   g_trackTimer = 0;
static bool       g_trackFirstTickPending = false;
// While the app is scrolling, the diagnostics-driven tick does nothing.
static ULONGLONG  g_scrollSettleAt = 0;
static bool       g_hiddenForScroll = false;
static bool       g_logRestoredRect = false;
static IInspectable* g_selScroller = nullptr;
static DevToolsSelection::ScrollAnchor g_selScrollAnchor{};
static bool       g_selScrollAnchorValid = false;
static bool       g_selScrollLiveSeen = false;
static __int64    g_selScrollChangingTok = 0;
static __int64    g_selScrollChangedTok = 0;
static EventSink  g_viewChangingSink, g_viewChangedSink;
static const UINT kScrollSettleMs = 120;
static const UINT kTrackIntervalMs = 33;

// Toggle an overlay element's Visibility (projected IUIElement::put_Visibility, Visible=0/Collapsed=1 -- the
// same call DevToolsSelSetExpanded makes). Collapsed also removes it from hit-testing and the UIA tree, so
// hidden chrome cannot intercept input.
// Collapsed removes parts from hit testing and layout, unlike opacity.
static void SetOverlayElementVisible(IInspectable* el, bool visible)
{
    if (!el) return;
    void* ui = nullptr;
    if (FAILED(el->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) || !ui) return;
    DevToolsPutVisibility(ui, visible ? 0 : 1);
    reinterpret_cast<IUnknown*>(ui)->Release();
}

static IInspectable* g_layoutUi = nullptr;
enum { kLayoutGuideL = 0, kLayoutGuideR, kLayoutGuideT, kLayoutGuideB, kLayoutMargin, kLayoutPadding,
       kLayoutMarginBandL, kLayoutMarginBandT, kLayoutMarginBandR, kLayoutMarginBandB,
       kLayoutPadBandL, kLayoutPadBandT, kLayoutPadBandR, kLayoutPadBandB,
       kLayoutSizeChip, kLayoutSizeText,
       kLayoutPartCount };
static IInspectable* g_layoutParts[kLayoutPartCount] = {};
static bool g_layoutOn = false;
static DevToolsLayoutBox g_layoutLast{};
static InstanceHandle g_layoutLastHandle = 0;
static bool g_layoutPlaced = false;
static int  g_layoutMarginBands = 0;
static int  g_layoutPadBands = 0;
static wchar_t g_layoutSizeText[32] = L"";

// Degenerate extents are clamped to 1 DIP rather than skipped, so a zero-width margin band still renders as the
// hairline it is.
// Clamp degenerate extents to one DIP so zero-width bands still show as hairlines.
static void PlaceLayoutPart(IInspectable* el, double x, double y, double w, double h)
{
    if (!el) return;
    if (w < 1.0) w = 1.0;
    if (h < 1.0) h = 1.0;
    void* fe = nullptr;
    if (SUCCEEDED(el->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) && fe) {
        DevToolsPutWidth(fe, w);
        DevToolsPutHeight(fe, h);
        reinterpret_cast<IUnknown*>(fe)->Release();
    }
    MoveOverlayElement(el, x, y);
}

// Its handle is pre-seeded into the exclusion set exactly as DrawAdorner does, and it is inserted at index 0 so
// it sits UNDER the selection rectangle: the blue box marks the arranged box, and the box model reads around it
// rather than over it.
// Insert layout adorners below highlight/toolbar and preseed them into the exclusion set.
static bool EnsureLayoutAdorner()
{
    if (g_layoutUi) return true;
    if (!g_canvasChildren || !g_pickDiag) return false;

    IInspectable* insp = nullptr;
    HRESULT hr = LoadMarkup(DevToolsXaml::LayoutAdorner, &insp);
    if (FAILED(hr) || !insp) {
        DevToolsOverlayLog(L"overlay.layout Load hr=0x%08X insp=%p", hr, insp);
        if (insp) insp->Release();
        return false;
    }
    InstanceHandle lh = 0;
    if (SUCCEEDED(g_pickDiag->GetHandleFromIInspectable(insp, &lh)) && lh && g_pickSeed) g_pickSeed(lh);

    IInspectable* ui = nullptr;
    HRESULT hrI = E_FAIL;
    if (SUCCEEDED(insp->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), reinterpret_cast<void**>(&ui))) && ui) {
        hrI = DevToolsVecInsertAt(g_canvasChildren, 0, ui);
        ui->Release();
    }
    if (FAILED(hrI)) {
        DevToolsOverlayLog(L"overlay.layout InsertAt hr=0x%08X", hrI);
        insp->Release();
        return false;
    }
    g_layoutUi = insp;

    static const wchar_t* const kNames[kLayoutPartCount] = {
        L"DevToolsLayoutGuideL", L"DevToolsLayoutGuideR", L"DevToolsLayoutGuideT", L"DevToolsLayoutGuideB",
        L"DevToolsLayoutMargin", L"DevToolsLayoutPadding",
        L"DevToolsLayoutMarginBandL", L"DevToolsLayoutMarginBandT", L"DevToolsLayoutMarginBandR", L"DevToolsLayoutMarginBandB",
        L"DevToolsLayoutPadBandL", L"DevToolsLayoutPadBandT", L"DevToolsLayoutPadBandR", L"DevToolsLayoutPadBandB",
        L"DevToolsLayoutSizeChip", L"DevToolsLayoutSizeText",
    };
    int resolved = 0;
    for (int i = 0; i < kLayoutPartCount; ++i) {
        g_layoutParts[i] = DevToolsSelFindName(g_layoutUi, kNames[i]);
        if (g_layoutParts[i]) ++resolved;
    }
// A part that did not resolve is a typo in a name nothing else would catch ('s class of bug), so the count is
// stated rather than left to be inferred from a missing line on screen.
    DevToolsOverlayLog(L"overlay.layout built (seed=%llu parts=%d/%d)", (unsigned long long)lh, resolved, kLayoutPartCount);
    return true;
}

static void ClearLayoutAdorner()
{
    if (g_layoutUi && g_canvasChildren) {
// Look the element up as the IUIElement the Canvas's IVector<UIElement> was given, not as the IInspectable this
// global holds -- same interface-identity rule that MoveOverlayElement documents.
        void* ui = nullptr;
        if (SUCCEEDED(g_layoutUi->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) && ui) {
            unsigned int idx = 0; unsigned char found = 0;
            if (SUCCEEDED(DevToolsVecIndexOf(g_canvasChildren, reinterpret_cast<IInspectable*>(ui), &idx, &found)) && found)
                DevToolsVecRemoveAt(g_canvasChildren, idx);
            reinterpret_cast<IUnknown*>(ui)->Release();
        }
    }
    for (int i = 0; i < kLayoutPartCount; ++i) {
        if (g_layoutParts[i]) { g_layoutParts[i]->Release(); g_layoutParts[i] = nullptr; }
    }
    if (g_layoutUi) { g_layoutUi->Release(); g_layoutUi = nullptr; }
    g_layoutPlaced = false;
    g_layoutLastHandle = 0;
}

static void SetLayoutAdornerVisible(bool on)
{
    if (!g_layoutUi) return;
    SetOverlayElementVisible(g_layoutUi, on && g_layoutOn && g_layoutPlaced);
}

// Called from every place the selection RECT is placed; cheap enough to run on the tick because the tick only
// reaches it when the rect actually changed.
static void SyncLayoutAdorner()
{
    WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::OverlayLayoutAdorner);
    if (!g_layoutOn || !g_selectedHandle || !g_pickDiag || !g_pickRoot) {
        g_layoutPlaced = false;
        SetOverlayElementVisible(g_layoutUi, false);
        return;
    }
    if (!EnsureLayoutAdorner()) { g_layoutPlaced = false; return; }

    DevToolsLayoutBox b{};
    if (!TryElementLayoutBox(g_pickDiag, g_pickRoot, g_selectedHandle, &b)) {
        g_layoutPlaced = false;
        SetOverlayElementVisible(g_layoutUi, false);
        DevToolsOverlayLog(L"overlay.layout unmeasurable sel=%llu -> hidden", (unsigned long long)g_selectedHandle);
        return;
    }

    const double cw = g_activeSurface.contentW;
    const double ch = g_activeSurface.contentH;
    PlaceLayoutPart(g_layoutParts[kLayoutGuideL], (double)b.box.left,      0.0,                 1.0, ch);
    PlaceLayoutPart(g_layoutParts[kLayoutGuideR], (double)b.box.right - 1, 0.0,                 1.0, ch);
    PlaceLayoutPart(g_layoutParts[kLayoutGuideT], 0.0,                     (double)b.box.top,   cw,  1.0);
    PlaceLayoutPart(g_layoutParts[kLayoutGuideB], 0.0,                     (double)b.box.bottom - 1, cw, 1.0);

    if (b.haveMargin)
        PlaceLayoutPart(g_layoutParts[kLayoutMargin], (double)b.margin.left, (double)b.margin.top,
                        (double)(b.margin.right - b.margin.left), (double)(b.margin.bottom - b.margin.top));
    SetOverlayElementVisible(g_layoutParts[kLayoutMargin], b.haveMargin);

    if (b.havePadding)
        PlaceLayoutPart(g_layoutParts[kLayoutPadding], (double)b.padding.left, (double)b.padding.top,
                        (double)(b.padding.right - b.padding.left), (double)(b.padding.bottom - b.padding.top));
    SetOverlayElementVisible(g_layoutParts[kLayoutPadding], b.havePadding);

    auto placeRing = [](int lIdx, int tIdx, int rIdx, int bIdx, const RECT& outer, const RECT& inner, bool have) -> int {
        if (!have) {
            for (int k : { lIdx, tIdx, rIdx, bIdx }) SetOverlayElementVisible(g_layoutParts[k], false);
            return 0;
        }
        const double ow = (double)(outer.right - outer.left);
        const double lw = (double)(inner.left - outer.left);
        const double rw = (double)(outer.right - inner.right);
        const double th = (double)(inner.top - outer.top);
        const double bh = (double)(outer.bottom - inner.bottom);
        const double midH = (double)(inner.bottom - inner.top);
        struct Strip { int idx; double x, y, w, h; bool on; };
        const Strip strips[4] = {
            { tIdx, (double)outer.left,  (double)outer.top,    ow, th,   th   > 0.5 },
            { bIdx, (double)outer.left,  (double)inner.bottom, ow, bh,   bh   > 0.5 },
            { lIdx, (double)outer.left,  (double)inner.top,    lw, midH, lw   > 0.5 },
            { rIdx, (double)inner.right, (double)inner.top,    rw, midH, rw   > 0.5 },
        };
        int drawn = 0;
        for (const Strip& s : strips) {
            if (s.on) { PlaceLayoutPart(g_layoutParts[s.idx], s.x, s.y, s.w, s.h); ++drawn; }
            SetOverlayElementVisible(g_layoutParts[s.idx], s.on);
        }
        return drawn;
    };
    g_layoutMarginBands = placeRing(kLayoutMarginBandL, kLayoutMarginBandT, kLayoutMarginBandR, kLayoutMarginBandB,
                                    b.margin, b.box, b.haveMargin);
    g_layoutPadBands = placeRing(kLayoutPadBandL, kLayoutPadBandT, kLayoutPadBandR, kLayoutPadBandB,
                                 b.box, b.padding, b.havePadding);

    {
        const long bw = b.box.right - b.box.left, bh2 = b.box.bottom - b.box.top;
        wchar_t txt[64];
        _snwprintf_s(txt, _countof(txt), _TRUNCATE, L"%ld \u00D7 %ld", bw, bh2);
        wcsncpy_s(g_layoutSizeText, txt, _TRUNCATE);
        if (g_layoutParts[kLayoutSizeText]) {
// Query ITextBlock first: DevToolsPutText expects that interface, not FindName's IInspectable. Calling through
// the wrong vtable can wedge or corrupt the UI thread.
            // Query ITextBlock before DevToolsPutText; the wrong vtable can corrupt the UI thread.
            void* tb = nullptr;
            if (SUCCEEDED(g_layoutParts[kLayoutSizeText]->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &tb)) && tb) {
                HSTRING hs = nullptr;
                if (SUCCEEDED(WindowsCreateString(txt, (UINT32)wcslen(txt), &hs))) {
                    DevToolsPutText(tb, hs);
                    WindowsDeleteString(hs);
                }
                reinterpret_cast<IUnknown*>(tb)->Release();
            }
        }
// A label the user cannot see is the same as no label, and an element at y=0 is common.
        // Flip labels inside client bounds when outside placement would make them invisible.
        const double chipH = 18.0, chipW = 8.0 * (double)wcslen(txt) + 10.0;
        double cx = (double)b.box.left;
        double cy = (double)b.box.top - chipH - 2.0;
        if (cy < 0.0) cy = (double)b.box.top + 2.0;
        if (cx + chipW > cw) cx = cw - chipW;
        if (cx < 0.0) cx = 0.0;
        MoveOverlayElement(g_layoutParts[kLayoutSizeChip], cx, cy);
        SetOverlayElementVisible(g_layoutParts[kLayoutSizeChip], true);
    }

    g_layoutLast = b;
    g_layoutLastHandle = g_selectedHandle;
    g_layoutPlaced = true;
    SetOverlayElementVisible(g_layoutUi, !g_chromeHidden && !g_hiddenForScroll);
// This is what the `overlay.layout` verb reports, so a disagreement between the log and the wire is decidable
// rather than arguable.
    // Stored placement is the observable overlay.layout result, not recomputed oracle geometry.
    DevToolsOverlayLog(L"overlay.layout placed sel=%llu box=%ld,%ld,%ldx%ld margin=%d(%ld,%ld,%ldx%ld) padding=%d(%ld,%ld,%ldx%ld)",
        (unsigned long long)g_selectedHandle,
        b.box.left, b.box.top, b.box.right - b.box.left, b.box.bottom - b.box.top,
        (int)b.haveMargin, b.margin.left, b.margin.top, b.margin.right - b.margin.left, b.margin.bottom - b.margin.top,
        (int)b.havePadding, b.padding.left, b.padding.top, b.padding.right - b.padding.left, b.padding.bottom - b.padding.top);
}

// Persisted globally next to ToolbarPinned, so the choice survives the app and applies to the next one -- the
// same treatment every other overlay toggle gets.
static void SetLayoutAdornersOn(bool on)
{
    g_layoutOn = on;
    ProtoSetToggle(L"DevToolsProtoLayout", L"DevToolsLayoutOn", L"DevToolsLayoutOff", on);
    SyncLayoutAdorner();
    DevToolsOverlayLog(L"overlay.layout adorners %s (selection=%llu)", on ? L"on" : L"off",
                  (unsigned long long)g_selectedHandle);
    NotifyStateChanged();
}

static void OnLayoutToggleClick(void*, void*)
{
    const bool now = !g_layoutOn;
    SetLayoutAdornersOn(now);
    DevToolsSettingsSetBool(L"LayoutAdorners", now);
}

// The wire oracle for the gate (`overlay.layout`). Reports the rects the drawing code ACTUALLY PLACED, not a
// recomputation -- a gate that recomputed would pass against a drawing path that never ran.
HRESULT DevToolsOverlay_GetLayoutRects(DevToolsOverlayLayoutRects* out)
{
    if (!out) return E_INVALIDARG;
    out->on = g_layoutOn ? 1 : 0;
    out->placed = g_layoutPlaced ? 1 : 0;
    out->handle = (unsigned long long)g_layoutLastHandle;
    out->box = g_layoutLast.box;
    out->margin = g_layoutLast.margin;
    out->padding = g_layoutLast.padding;
    out->haveMargin = g_layoutLast.haveMargin ? 1 : 0;
    out->havePadding = g_layoutLast.havePadding ? 1 : 0;
    out->marginBands = g_layoutMarginBands;
    out->padBands = g_layoutPadBands;
    wcsncpy_s(out->sizeText, g_layoutSizeText, _TRUNCATE);
    return S_OK;
}

// Set the toggle from the wire. Deliberately the SAME call the toolbar button makes, persistence included, so a
// host (or a gate) driving this cannot reach a state the button cannot -- the mistake selection.arm /
// selection.disarm already avoid for pick mode.
// Wire toggle uses the toolbar path so persistence and button state cannot diverge.
HRESULT DevToolsOverlay_SetLayoutAdorners(bool on)
{
    if (g_layoutOn == on) return S_FALSE;
    SetLayoutAdornersOn(on);
    DevToolsSettingsSetBool(L"LayoutAdorners", on);
    return S_OK;
}

// Lightweight toggle-only read: every field of DevToolsOverlay_GetLayoutRects except `on` requires re-packing a
// wire handle and a RECT the caller almost never wants for a plain state poll.
bool DevToolsOverlay_IsLayoutAdornersOn() { return g_layoutOn; }

// Deliberately does NOT touch the pick catcher, the highlight adorner, or the layout adorners -- those are
// independent overlay state and a hidden toolbar must not silently take them down with it.
// Explicit toolbar hide/show does not touch pick, highlight, or layout adorner state.
HRESULT DevToolsOverlay_SetToolbarVisible(bool visible)
{
    ++g_hostGeneration;
    g_failedRecoveryPanel = 0;
    if (g_toolbarVisible == visible) return S_FALSE;
    if (!g_aRow && !g_railL) return S_FALSE;
    g_toolbarVisible = visible;
    ShowARow(g_aRowOpen);
    ShowSnapTargets(g_dragging);
    DevToolsOverlayLog(L"overlay.toolbar visible=%d", (int)visible);
    NotifyStateChanged();
    return S_OK;
}

// Switch surfaces by reparenting the existing Popup, closed before moving and reopened after. Clear selection:
// bounds from one XamlRoot cannot be transposed onto another.
// Surface switch reparents the existing Popup and drops selection because bounds are XamlRoot-local.
static HRESULT PopupChildren(IInspectable* panel, winrt::com_ptr<IInspectable>& vector)
{
    winrt::com_ptr<IInspectable> panelInterface, children;
    HRESULT hr = panel->QueryInterface(DevToolsIid<DevToolsXC::IPanel>(), panelInterface.put_void());
    if (SUCCEEDED(hr)) hr = DevToolsPanelGetChildren(panelInterface.get(), children.put());
    if (SUCCEEDED(hr)) hr = children->QueryInterface(DevToolsIid<DevToolsUIElementVector>(), vector.put_void());
    return hr;
}

static HRESULT PopupAttached(IInspectable* panel, IInspectable* popup, IInspectable* popupUi, bool* attached)
{
    *attached = false;
    if (!panel || !popup || !popupUi) return S_OK;
    if (HostLayerAttached(panel, popupUi, attached)) return S_OK;
    winrt::com_ptr<IInspectable> fe, children;
    HRESULT hr = panel->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), fe.put_void());
    bool loaded = false;
    if (SUCCEEDED(hr)) hr = DevToolsGetIsLoaded(fe.get(), &loaded);
    if (FAILED(hr) || !loaded) return hr;
    hr = PopupChildren(panel, children);
    unsigned index = 0;
    unsigned char found = 0;
    if (SUCCEEDED(hr)) hr = DevToolsVecIndexOf(children.get(), popupUi, &index, &found);
    if (FAILED(hr) || !found) return hr;
    return DevToolsPopupGetIsOpen(popup, attached);
}

static HRESULT SeedPopup(IXamlDiagnostics* diag, IInspectable* popup, IInspectable* canvas)
{
    for (auto object : {popup, canvas}) {
        InstanceHandle handle = 0;
        const HRESULT hr = diag->GetHandleFromIInspectable(object, &handle);
        if (FAILED(hr) || !handle) return FAILED(hr) ? hr : E_UNEXPECTED;
        if (g_pickSeed) g_pickSeed(handle);
    }
    return S_OK;
}

static void AlignPopupOrigin(IInspectable* child, IInspectable* host)
{
    if (!child || !host) return;
    static bool aligning = false;
    if (aligning) return;
    aligning = true;
    try {
        winrt::Windows::Foundation::IInspectable object{ nullptr }, popupObject{ nullptr };
        winrt::copy_from_abi(object, child);
        winrt::copy_from_abi(popupObject, host);
        auto canvas = object.as<DevToolsX::UIElement>();
        auto popup = popupObject.as<DevToolsXCP::Popup>();
        auto root = canvas.XamlRoot();
        if (root && popup.IsOpen() && root.Content()) {
            const auto origin = canvas.TransformToVisual(root.Content()).TransformPoint({0, 0});
            if (std::isfinite(origin.X) && std::isfinite(origin.Y)) {
                if (std::abs(origin.X) > 0.01f) popup.HorizontalOffset(popup.HorizontalOffset() - origin.X);
                if (std::abs(origin.Y) > 0.01f) popup.VerticalOffset(popup.VerticalOffset() - origin.Y);
            }
        }
    } catch (const winrt::hresult_error& e) {
        DevToolsOverlayLog(L"overlay.popup origin not arranged hr=0x%08X", e.code().value);
    }
    aligning = false;
}

static void AlignSelectionOrigin(void*, void*) { AlignPopupOrigin(g_selectionCanvas.get(), g_selectionPopup.get()); }
static void AlignHighlightOrigin(void*, void*) { AlignPopupOrigin(g_highlightCanvas.get(), g_highlightPopup.get()); }

HRESULT DevToolsOverlay_MoveHighlightToPanel(IXamlDiagnostics* diag, InstanceHandle newPanel)
{
    winrt::com_ptr<IInspectable> panel;
    DevToolsSurface surface;
    if (!diag || !DevToolsSurface_Resolve(diag, newPanel, &surface) ||
        FAILED(diag->GetIInspectableFromHandle(newPanel, panel.put())) || !panel) return E_INVALIDARG;
    try {
        if (!g_highlightPopup) {
            DevToolsXC::Canvas canvas;
            canvas.IsHitTestVisible(false);
            DevToolsXCP::Popup popup;
            popup.Child(canvas);
            g_highlightCanvas.copy_from(reinterpret_cast<IInspectable*>(winrt::get_abi(canvas)));
            g_highlightPopup.copy_from(reinterpret_cast<IInspectable*>(winrt::get_abi(popup)));
            winrt::check_hresult(g_highlightPopup->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), g_highlightPopupUi.put_void()));
            HostRegister(g_highlightPopup.get(), g_highlightPopupUi.get(), g_highlightCanvas.get(), false);
            auto children = canvas.Children();
            g_highlightChildren.copy_from(reinterpret_cast<IInspectable*>(winrt::get_abi(children)));
            winrt::check_hresult(g_highlightCanvas->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), g_highlightLayoutSource.put_void()));
            const auto iid = DevToolsIid<winrt::Windows::Foundation::EventHandler<winrt::Windows::Foundation::IInspectable>>();
            g_highlightOriginSink.Bind(&iid, &AlignHighlightOrigin);
            winrt::check_hresult(DevToolsAbi<DevToolsX::IFrameworkElement>(g_highlightLayoutSource.get())
                ->add_LayoutUpdated(&g_highlightOriginSink, &g_highlightLayoutToken));
        }
        winrt::check_hresult(DevToolsPutWidth(g_highlightLayoutSource.get(), surface.contentW));
        winrt::check_hresult(DevToolsPutHeight(g_highlightLayoutSource.get(), surface.contentH));
        bool attached = false;
        winrt::check_hresult(PopupAttached(g_highlightPanel.get(), g_highlightPopup.get(), g_highlightPopupUi.get(), &attached));
        if (attached && g_highlightPanel.as<IUnknown>().get() == panel.as<IUnknown>().get()) return S_OK;
        winrt::check_hresult(HostPutOpen(g_highlightPopup.get(), false));
        winrt::com_ptr<IInspectable> oldChildren;
        unsigned index = 0;
        unsigned char found = 0;
        if (g_highlightPanel) {
            winrt::check_hresult(PopupChildren(g_highlightPanel.get(), oldChildren));
            winrt::check_hresult(DevToolsVecIndexOf(oldChildren.get(), g_highlightPopupUi.get(), &index, &found));
            if (found) winrt::check_hresult(DevToolsVecRemoveAt(oldChildren.get(), index));
        }
        winrt::check_hresult(SeedPopup(diag, g_highlightPopup.get(), g_highlightCanvas.get()));
        const HRESULT hr = HostAttach(diag, panel.get(), g_highlightPopupUi.get(), L"highlight");
        if (FAILED(hr)) {
            if (found) {
                const HRESULT restore = DevToolsVecInsertAt(oldChildren.get(), index, g_highlightPopupUi.get());
                if (SUCCEEDED(restore)) HostPutOpen(g_highlightPopup.get(), attached);
                else DevToolsOverlayLog(L"overlay.highlight rollback failed hr=0x%08X", restore);
            }
            return hr;
        }
        g_highlightPanel = panel;
        return HostPutOpen(g_highlightPopup.get(), true);
    } catch (const winrt::hresult_error& e) {
        if (!g_highlightPanel) {
            if (g_highlightLayoutSource && g_highlightLayoutToken.value) {
                const HRESULT removed = DevToolsAbi<DevToolsX::IFrameworkElement>(g_highlightLayoutSource.get())
                    ->remove_LayoutUpdated(g_highlightLayoutToken);
                if (FAILED(removed)) DevToolsOverlayLog(L"overlay.highlight unsubscribe failed hr=0x%08X", removed);
            }
            g_highlightLayoutToken = {};
            g_highlightLayoutSource = nullptr;
            g_highlightChildren = nullptr;
            g_highlightPopupUi = nullptr;
            g_highlightPopup = nullptr;
            g_highlightCanvas = nullptr;
        }
        DevToolsOverlayLog(L"overlay.highlight host failed hr=0x%08X", e.code().value);
        return e.code();
    }
}

static void OnSelectionResize(void*, void*)
{
    DevToolsSurface surface;
    if (!DevToolsSurface_Resolve(g_pickDiag, g_pickRoot, &surface) ||
        surface.xamlRootKey != g_activeSurface.xamlRootKey) return;
    g_activeSurface = surface;
    if (g_selectionLayoutSource) {
        const HRESULT width = DevToolsPutWidth(g_selectionLayoutSource.get(), surface.contentW);
        const HRESULT height = DevToolsPutHeight(g_selectionLayoutSource.get(), surface.contentH);
        if (FAILED(width) || FAILED(height))
            DevToolsOverlayLog(L"overlay.selection resize failed hr=0x%08X", FAILED(width) ? width : height);
    }
    ReanchorSelectionNow();
}

HRESULT DevToolsOverlay_MoveSelectionToPanel(IXamlDiagnostics* diag, InstanceHandle newPanel, bool recovering)
{
    DevToolsSurface surface;
    winrt::com_ptr<IInspectable> panel;
    if (!diag || !DevToolsSurface_Resolve(diag, newPanel, &surface) ||
        FAILED(diag->GetIInspectableFromHandle(newPanel, panel.put())) || !panel) return E_INVALIDARG;
    winrt::com_ptr<IUnknown> identity;
    const HRESULT rootResult = ReadHostRootIdentity(panel.get(), identity);
    if (FAILED(rootResult) || !identity ||
        (recovering && identity.get() != g_selectionRootIdentity.get() && !g_selRootLost))
        return FAILED(rootResult) ? rootResult : E_INVALIDARG;
    if (!recovering && g_selRootLost) return E_PENDING;
    bool attached = false;
    const HRESULT attachment = PopupAttached(g_selectionPanel.get(), g_selectionPopup.get(), g_selectionPopupUi.get(), &attached);
    if (FAILED(attachment)) return attachment;
    if (g_selectionPanel && g_pickRoot == newPanel && g_activeSurface.xamlRootKey == surface.xamlRootKey &&
        g_selectionPanel.as<IUnknown>().get() == panel.as<IUnknown>().get() && attached) {
        g_activeSurface = surface;
        const HRESULT width = DevToolsPutWidth(g_selectionLayoutSource.get(), surface.contentW);
        return FAILED(width) ? width : DevToolsPutHeight(g_selectionLayoutSource.get(), surface.contentH);
    }
    std::wstring draft;
    if (!recovering && g_selPanel && g_selComment &&
        (!SelReadCommentText(&draft) ||
         (!DevToolsCommentText::IsBlank(draft) && !DevToolsCommentText::Unchanged(draft, g_selCommentSaved)))) {
        DevToolsOverlayLog(L"overlay.selection root change refused while a comment has unsaved work");
        return E_PENDING;
    }
    if (g_surfaceMoving) return E_PENDING;
    struct Moving {
        Moving() { g_surfaceMoving = true; }
        ~Moving() { g_surfaceMoving = false; }
    } moving;
    if (!recovering) {
        g_selDismissCommitFailed = false;
        const auto generation = g_selGen;
        DevToolsSelFlushCommits();
        if (generation != g_selGen || g_selDismissCommitFailed) return E_PENDING;
        ExitPickMode();
        DeselectAll();
    }
    try {
        if (!g_selectionPopup) {
            DevToolsXC::Canvas canvas;
            DevToolsXCP::Popup popup;
            popup.Child(canvas);
            g_selectionCanvas.copy_from(reinterpret_cast<IInspectable*>(winrt::get_abi(canvas)));
            g_selectionPopup.copy_from(reinterpret_cast<IInspectable*>(winrt::get_abi(popup)));
            winrt::check_hresult(g_selectionPopup->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), g_selectionPopupUi.put_void()));
            HostRegister(g_selectionPopup.get(), g_selectionPopupUi.get(), g_selectionCanvas.get(), false);
            auto children = canvas.Children();
            g_canvasChildren = reinterpret_cast<IInspectable*>(winrt::detach_abi(children));
            winrt::check_hresult(g_selectionCanvas->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), g_selectionLayoutSource.put_void()));
            const auto iid = DevToolsIid<winrt::Windows::Foundation::EventHandler<winrt::Windows::Foundation::IInspectable>>();
            g_selectionOriginSink.Bind(&iid, &AlignSelectionOrigin);
            winrt::check_hresult(DevToolsAbi<DevToolsX::IFrameworkElement>(g_selectionLayoutSource.get())
                ->add_LayoutUpdated(&g_selectionOriginSink, &g_selectionLayoutToken));
        }
        winrt::check_hresult(HostPutOpen(g_selectionPopup.get(), false));
        winrt::com_ptr<IInspectable> previousChildren;
        unsigned index = 0;
        unsigned char found = 0;
        if (g_selectionPanel) {
            winrt::check_hresult(PopupChildren(g_selectionPanel.get(), previousChildren));
            winrt::check_hresult(DevToolsVecIndexOf(previousChildren.get(), g_selectionPopupUi.get(), &index, &found));
            if (found) winrt::check_hresult(DevToolsVecRemoveAt(previousChildren.get(), index));
        }
        winrt::check_hresult(SeedPopup(diag, g_selectionPopup.get(), g_selectionCanvas.get()));
        HRESULT hr = HostAttach(diag, panel.get(), g_selectionPopupUi.get(), L"selection");
        if (FAILED(hr)) {
            if (found) {
                const HRESULT restore = DevToolsVecInsertAt(previousChildren.get(), index, g_selectionPopupUi.get());
                if (SUCCEEDED(restore)) HostPutOpen(g_selectionPopup.get(), true);
                else DevToolsOverlayLog(L"overlay.selection rollback failed hr=0x%08X", restore);
            }
            return hr;
        }
        UnwireAppEscape();
        if (g_selectionResizeSource) {
            const HRESULT removed = DevToolsRemoveSizeChanged(g_selectionResizeSource.get(), g_selectionResizeToken);
            if (FAILED(removed)) DevToolsOverlayLog(L"overlay.selection resize unsubscribe failed hr=0x%08X", removed);
            g_selectionResizeSource = nullptr;
        }
        g_selectionPanel = panel;
        g_selectionRootIdentity = std::move(identity);
        g_pickRoot = newPanel;
        g_activeSurface = surface;
        winrt::check_hresult(DevToolsPutWidth(g_selectionLayoutSource.get(), surface.contentW));
        winrt::check_hresult(DevToolsPutHeight(g_selectionLayoutSource.get(), surface.contentH));
        winrt::com_ptr<IInspectable> resizeSource;
        winrt::check_hresult(panel->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), resizeSource.put_void()));
        g_selectionResizeSink.Bind(&IID_SizeChanged_Del, &OnSelectionResize);
        winrt::check_hresult(DevToolsAddSizeChanged(resizeSource.get(), &g_selectionResizeSink, &g_selectionResizeToken));
        g_selectionResizeSource = std::move(resizeSource);
        WireAppEscapeOnce();
        winrt::check_hresult(HostPutOpen(g_selectionPopup.get(), true));
        if (g_popup && g_toolbarSurface.xamlRootKey == surface.xamlRootKey) {
            bool open = false;
            if (SUCCEEDED(HostGetOpen(g_popup, &open)) && open) {
                winrt::check_hresult(HostPutOpen(g_popup, false));
                winrt::check_hresult(HostPutOpen(g_popup, true));
            }
        }
        if (recovering && g_selExpanded && g_selPopup && !g_selDismissVisualClosed)
            winrt::check_hresult(SetSelectionPopupOpen(true));
        NotifyStateChanged();
        return S_OK;
    } catch (const winrt::hresult_error& e) {
        if (!g_selectionPanel) {
            if (g_selectionLayoutSource && g_selectionLayoutToken.value) {
                const HRESULT removed = DevToolsAbi<DevToolsX::IFrameworkElement>(g_selectionLayoutSource.get())
                    ->remove_LayoutUpdated(g_selectionLayoutToken);
                if (FAILED(removed)) DevToolsOverlayLog(L"overlay.selection layout unsubscribe failed hr=0x%08X", removed);
            }
            g_selectionLayoutToken = {};
            g_selectionLayoutSource = nullptr;
            g_selectionPopupUi = nullptr;
            g_selectionPopup = nullptr;
            g_selectionCanvas = nullptr;
            if (g_canvasChildren) { g_canvasChildren->Release(); g_canvasChildren = nullptr; }
        }
        DevToolsOverlayLog(L"overlay.selection host failed hr=0x%08X", e.code().value);
        return e.code();
    }
}
