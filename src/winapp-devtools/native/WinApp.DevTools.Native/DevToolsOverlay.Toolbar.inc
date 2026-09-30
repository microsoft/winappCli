// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsOverlay.cpp; included into that translation unit, not compiled on its own.

static void DevToolsSetSelectionPanelRequestedTheme(int requestedTheme);

static void DevToolsSetToolbarRequestedTheme(int appTheme)
{
    const int toolbarTheme = DevToolsBuildPalette().highContrast
        ? DevToolsTheme::kDefault
        : DevToolsTheme::ToggleTarget(appTheme);
    for (IInspectable* element : { g_railL, g_aRow }) {
        if (!element) continue;
        void* fe = nullptr;
        if (SUCCEEDED(element->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) && fe) {
            DevToolsPutRequestedTheme(fe, toolbarTheme);
            reinterpret_cast<IUnknown*>(fe)->Release();
        }
    }
    DevToolsSetSelectionPanelRequestedTheme(toolbarTheme);
    HostSetLayerTheme(appTheme);
}

static void OnThemeToggleClick(void*, void*)
{
    if (!DevToolsTrust_MutationEnabled()) {
        DevToolsOverlayLog(L"overlay.theme refused by process posture");
        return;
    }
    if (!g_appPanel) {
        DevToolsOverlayLog(L"overlay.theme unavailable: app content root not retained");
        return;
    }

    void* themeFe = nullptr;
    HRESULT hrThemeQi = g_appPanel->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &themeFe);
    int actualBefore = DevToolsTheme::kDefault;
    HRESULT hrActual = themeFe ? DevToolsGetActualTheme(themeFe, &actualBefore) : hrThemeQi;
    if (FAILED(hrActual) || !themeFe) {
        DevToolsOverlayLog(L"overlay.theme get ActualTheme failed qi=0x%08X get=0x%08X", hrThemeQi, hrActual);
        if (themeFe) reinterpret_cast<IUnknown*>(themeFe)->Release();
        return;
    }

    const int target = DevToolsTheme::ToggleTarget(actualBefore);
    HRESULT hrPut = DevToolsPutRequestedTheme(themeFe, target);
    int requestedAfter = DevToolsTheme::kDefault;
    HRESULT hrRequestedAfter = DevToolsGetRequestedTheme(themeFe, &requestedAfter);
    int actualAfter = actualBefore;
    HRESULT hrActualAfter = DevToolsGetActualTheme(themeFe, &actualAfter);

    const bool requestedMatches = SUCCEEDED(hrPut) && SUCCEEDED(hrRequestedAfter) && requestedAfter == target;
    const bool effectiveMatches = SUCCEEDED(hrActualAfter) && actualAfter == target;
    DevToolsOverlayLog(
        L"overlay.theme actual=%d target=%d put=0x%08X requested=%d/%s actualAfter=%d/%s",
        actualBefore, target, hrPut, requestedAfter, requestedMatches ? L"match" : L"mismatch",
        actualAfter, effectiveMatches ? L"match" : L"mismatch");
    if (SUCCEEDED(hrPut)) DevToolsSetToolbarRequestedTheme(target);

    reinterpret_cast<IUnknown*>(themeFe)->Release();
}

// `ICanvasStatics::SetLeft` takes a UIElement, so its ABI parameter is the element's IUIElement pointer --
// handing it an IInspectable is a vtable mismatch that faults inside XAML. Every original caller happened to be
// safe because its pointer came out of an IVector<UIElement>, which hands back exactly that interface; an e.
// QI to IUIElement is required; Canvas.SetLeft/Top does not accept arbitrary IInspectable.
static void MoveOverlayElement(IInspectable* el, double x, double y)
{
    if (!g_canvasStatics || !el) return;
    void* ui = nullptr;
    if (FAILED(el->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) || !ui) return;
    DevToolsCanvasSetLeft(g_canvasStatics, reinterpret_cast<IInspectable*>(ui), x);
    DevToolsCanvasSetTop(g_canvasStatics, reinterpret_cast<IInspectable*>(ui), y);
    reinterpret_cast<IUnknown*>(ui)->Release();
}

// Pill hover/focus expands the bar anchored to the same corner; pointer exit collapses it. Focus on the pill
// opens the keyboard actions.
static IInspectable* g_protoRoot = nullptr;
static void BeginNamedStoryboard(IInspectable* root, const wchar_t* name);
static IInspectable* DevToolsSelFindName(IInspectable* root, const wchar_t* name);

// Shared anchor for the pill and bar.
// Pill and bar share one anchor so corner and center parking remain aligned.
static Client ClientGeom() { return Client{ g_protoW, g_protoH, g_titleBarInsetDip }; }
static double ChipX() { double x = 0, y = 0; Anchor(g_corner, ClientGeom(), g_chipW, (double)kChipH, &x, &y); return x; }
static double ChipY() { double x = 0, y = 0; Anchor(g_corner, ClientGeom(), g_chipW, (double)kChipH, &x, &y); return y; }
static double BarX()  { double x = 0, y = 0; Anchor(g_corner, ClientGeom(), (double)kBarW, (double)kBarH, &x, &y); return x; }
static double BarY()  { double x = 0, y = 0; Anchor(g_corner, ClientGeom(), (double)kBarW, (double)kBarH, &x, &y); return y; }

static void SnapTargetPos(int c, double* x, double* y)
{
    double cx = 0.0, cy = 0.0;
    Anchor(c, ClientGeom(), g_chipW, (double)kChipH, &cx, &cy);
    *x = cx + g_chipW / 2.0 - (double)kSnapW / 2.0;
    *y = cy + (double)kChipH / 2.0 - (double)kSnapH / 2.0;
}

static bool g_dragging = false;

static void ShowARow(bool open)
{
    g_aRowOpen = open;
    MoveOverlayElement(g_aRow, BarX(), BarY());
    MoveOverlayElement(g_railL, ChipX(), ChipY());
    SetOverlayElementVisible(g_aRow, g_toolbarVisible && open);
    SetOverlayElementVisible(g_railL, g_toolbarVisible && !open);
    if (open && g_toolbarVisible) BeginNamedStoryboard(g_protoRoot, L"DevToolsBarReveal");
    DevToolsOverlayLog(L"overlay.toolbar bar %s corner=%d", open ? L"open" : L"closed", g_corner);
}
static void OnAExpand(void*, void*)  { if (g_dragging) return; ShowARow(true); }
// Collapse when the pointer leaves the bar -- unless the toolbar is pinned open (the whole point of the pin).
static void OnACollapse(void*, void*){ if (g_pinned || g_dragging || g_toolbarFocusWithin > 0) return; ShowARow(false); }
static void OnToolbarGotFocus(void*, void*) { ++g_toolbarFocusWithin; }
static void OnToolbarLostFocus(void*, void*) { if (g_toolbarFocusWithin > 0) --g_toolbarFocusWithin; }
static bool FocusToolbarFirstAction()
{
    ShowARow(true);
    winrt::com_ptr<IInspectable> pick;
    pick.attach(DevToolsSelFindName(g_protoRoot, L"DevToolsProtoPick"));
    winrt::com_ptr<IInspectable> ui;
    HRESULT hr = pick ? pick->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), ui.put_void()) : E_POINTER;
    bool focused = false;
    if (SUCCEEDED(hr)) hr = DevToolsFocus(ui.get(), 2, &focused);
    DevToolsOverlayLog(L"overlay.toolbar focus Pick hr=0x%08X succeeded=%d", hr, (int)focused);
    return SUCCEEDED(hr) && focused;
}
static void OnPillGotFocus(void*, void*)
{
    if (g_dragging || !g_toolbarVisible) return;
    FocusToolbarFirstAction();
}

// Ctrl+Shift+F12 moves keyboard focus to the toolbar while it is shown; Esc from the toolbar returns focus to
// the app element that had it.
static winrt::weak_ref<DevToolsX::UIElement> g_toolbarReturnFocus;

static bool IsToolbarShortcut(int key)
{
    return key == VK_F12 && (GetKeyState(VK_CONTROL) & 0x8000) && (GetKeyState(VK_SHIFT) & 0x8000) &&
        !(GetKeyState(VK_MENU) & 0x8000) && !(GetKeyState(VK_LWIN) & 0x8000) && !(GetKeyState(VK_RWIN) & 0x8000);
}

static bool FocusToolbarFromShortcut()
{
    if (!g_toolbarVisible || !g_protoRoot || g_dragging) return false;
    if (g_toolbarFocusWithin == 0) {
        g_toolbarReturnFocus = nullptr;
        try {
            winrt::Windows::Foundation::IInspectable root{ nullptr };
            winrt::copy_from_abi(root, g_protoRoot);
            if (auto xamlRoot = root.as<DevToolsX::UIElement>().XamlRoot())
                if (auto focused = DevToolsXI::FocusManager::GetFocusedElement(xamlRoot).try_as<DevToolsX::UIElement>())
                    g_toolbarReturnFocus = winrt::make_weak(focused);
        } catch (const winrt::hresult_error& e) {
            DevToolsOverlayLog(L"overlay.toolbar shortcut focus read failed hr=0x%08X", e.code().value);
        }
    }
    return FocusToolbarFirstAction();
}

static bool ReturnFocusFromToolbar()
{
    if (g_toolbarFocusWithin == 0) return false;
    auto target = g_toolbarReturnFocus.get();
    g_toolbarReturnFocus = nullptr;
    if (!target) return false;
    bool returned = false;
    try { returned = target.Focus(DevToolsX::FocusState::Keyboard); }
    catch (const winrt::hresult_error&) {}
    if (returned && !g_pinned) ShowARow(false);
    DevToolsOverlayLog(L"overlay.toolbar focus returned=%d", (int)returned);
    return returned;
}

static void ProtoSetToggle(const wchar_t* button, const wchar_t* onPart, const wchar_t* offPart, bool on);

static void ProtoSetPinned(bool on)
{
    g_pinned = on;
    ProtoSetToggle(L"DevToolsProtoPin", L"DevToolsPinOn", L"DevToolsPinOff", on);
}

// Pinning also opens the bar now; unpinning leaves it open until the next PointerExited (the pointer is on the
// bar).
static void OnPinClick(void*, void*)
{
    const bool now = !g_pinned;
    ProtoSetPinned(now);
    DevToolsSettingsSetBool(L"ToolbarPinned", now);
    if (now) ShowARow(true);
    DevToolsOverlayLog(L"overlay.toolbar pin -> %s (persisted)", now ? L"on" : L"off");
}

// Show/hide one named overlay part by Visibility (Visible=0 / Collapsed=1), resolved in the toolbar fragment's
// namescope.
static void ProtoSetPartVisible(const wchar_t* name, bool visible)
{
    if (!g_protoRoot) return;
    if (IInspectable* el = DevToolsSelFindName(g_protoRoot, name)) {
        void* ui = nullptr;
        if (SUCCEEDED(el->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) && ui) {
            DevToolsPutVisibility(ui, visible ? 0 : 1);
            reinterpret_cast<IUnknown*>(ui)->Release();
        }
        el->Release();
    }
}

// A toolbar mode: the accent on-part replaces the plain icon, and IsChecked carries the state to UI Automation.
static void ProtoSetToggle(const wchar_t* button, const wchar_t* onPart, const wchar_t* offPart, bool on)
{
    ProtoSetPartVisible(onPart, on);
    ProtoSetPartVisible(offPart, !on);
    if (!g_protoRoot) return;
    winrt::com_ptr<IInspectable> element;
    element.attach(DevToolsSelFindName(g_protoRoot, button));
    winrt::com_ptr<IUnknown> toggle;
    if (element && SUCCEEDED(element->QueryInterface(DevToolsIid<DevToolsXCP::IToggleButton>(), toggle.put_void()))) {
        const auto checked = winrt::box_value(on);
        DevToolsAbi<DevToolsXCP::IToggleButton>(toggle.get())->put_IsChecked(winrt::get_abi(checked));
    }
}

static void ProtoSetPartText(const wchar_t* name, const std::wstring& text)
{
    if (!g_protoRoot) return;
    if (IInspectable* el = DevToolsSelFindName(g_protoRoot, name)) {
        void* tb = nullptr;
        if (SUCCEEDED(el->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &tb)) && tb) {
            HSTRING h = nullptr;
            if (SUCCEEDED(WindowsCreateString(text.c_str(), (UINT32)text.size(), &h))) { DevToolsPutText(tb, h); WindowsDeleteString(h); }
            reinterpret_cast<IUnknown*>(tb)->Release();
        }
        el->Release();
    }
}

static bool CommentsShown();
static size_t CommentCount();
static std::wstring ChipSurfaceLabel()
{
    const std::vector<DevToolsSurface>& all = DevToolsOverlay_AllSurfaces();
    if (all.size() < 2) return std::wstring();
    std::wstring name = g_toolbarSurface.displayName;
    if (name.empty()) return std::wstring();
// The pill is anchored to a screen corner and re-places itself on every width change, so an untruncated window
// title would push it across the surface.
    const size_t kMax = 22;
    if (name.size() > kMax) {
        const size_t sep = name.rfind(L" \u00B7 ");
        std::wstring tail = (sep == std::wstring::npos) ? std::wstring() : name.substr(sep + 3);
        if (!tail.empty() && tail.size() <= kMax) return tail;
        name = std::wstring(L"\u2026") + name.substr(name.size() - (kMax - 1));
    }
    return name;
}

static void UpdateChipLabel()
{
    const bool picking = (g_pickCatcher != nullptr);
    const bool comments = CommentsShown();
    const size_t n = CommentCount();
    const bool active = picking || comments;
    const std::wstring surface = ChipSurfaceLabel();

    ProtoSetPartText(L"DevToolsChipLabel", picking ? L"Picking"
                                             : (comments ? L"Comments"
                                                         : (surface.empty() ? std::wstring(L"DevTools") : surface)));
    if (!picking && comments) ProtoSetPartText(L"DevToolsChipCountText", (n > 99) ? std::wstring(L"99+") : std::to_wstring(n));
    ProtoSetPartVisible(L"DevToolsChipCount", !picking && comments && n > 0);
    ProtoSetPartVisible(L"DevToolsChipDotOn", active);
    ProtoSetPartVisible(L"DevToolsGlowHaloOn", active);
    ProtoSetPartVisible(L"DevToolsChipDot", !active);
    ProtoSetPartVisible(L"DevToolsGlowHalo", !active);
    DevToolsOverlayLog(L"overlay.pill label picking=%d comments=%d count=%zu surface='%s'",
                  (int)picking, (int)comments, n, surface.c_str());
}

// Re-anchor from SizeChanged, after ActualWidth reflects the new label. Label assignment alone cannot supply
// the final width.
// Re-anchor after SizeChanged because text assignment alone lacks final ActualWidth.
static void OnChipResize(void*, void*)
{
    if (!g_railL) return;
    void* fe = nullptr;
    if (FAILED(g_railL->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) || !fe) return;
    double w = 0.0;
    HRESULT hr = DevToolsGetActualWidth(fe, &w);
    reinterpret_cast<IUnknown*>(fe)->Release();
    if (FAILED(hr) || w <= 0.0) return;
    if (w == g_chipW) return;
    g_chipW = w;
    if (!g_aRowOpen && !g_dragging) MoveOverlayElement(g_railL, ChipX(), ChipY());
    DevToolsOverlayLog(L"overlay.pill width=%.0f -> anchored %s position=%d", w,
                  CornerIsCenter(g_corner) ? L"center" : (CornerIsLeft(g_corner) ? L"left" : L"right"),
                  g_corner);
}

static void SetCorner(int corner, bool persist)
{
    if (corner < 0 || corner >= kCornerCount) return;
    g_corner = corner;
    if (persist) DevToolsSettingsSetIndex(L"ToolbarCorner", corner);
    ShowARow(g_aRowOpen);
    if (g_pinned) ProtoSetPinned(true);
    DevToolsOverlayLog(L"overlay.toolbar position -> %d (persisted=%d)", corner, (int)persist);
}

static void ShowSnapTargets(bool on)
{
    for (int i = 0; i < kCornerCount; ++i) {
        if (!g_snapUi[i]) continue;
        if (on) { double x = 0, y = 0; SnapTargetPos(i, &x, &y); MoveOverlayElement(g_snapUi[i], x, y); }
        SetOverlayElementVisible(g_snapUi[i], on && g_toolbarVisible);
    }
    if (!on) for (int i = 0; i < kCornerCount; ++i) {
        wchar_t nm[16]; _snwprintf_s(nm, _countof(nm), _TRUNCATE, L"DevToolsSnapNear%d", i);
        ProtoSetPartVisible(nm, false);
    }
}

static int NearestCornerForPill(double px, double py)
{
    return NearestCorner(ClientGeom(), g_chipW, (double)kChipH, px, py);
}

static void HighlightSnapTarget(int corner)
{
    for (int i = 0; i < kCornerCount; ++i) {
        wchar_t nm[16]; _snwprintf_s(nm, _countof(nm), _TRUNCATE, L"DevToolsSnapNear%d", i);
        ProtoSetPartVisible(nm, i == corner);
    }
}

// The pointer is tracked through a full-canvas catcher (see drag-catcher.xaml) rather than a pointer capture:
// the overlay has no CapturePointer wrapper, and a catcher that covers the whole client area gives the same
// guarantee — the pointer cannot leave it — using the primitive this file already trusts.
static IInspectable* g_dragCatcher = nullptr;
static EventSink g_dragMoveSink, g_dragUpSink;
static double g_dragOffX = 0.0, g_dragOffY = 0.0;
static int    g_dragNear = CornerBR;

static void EndToolbarDrag();
static void OnDragCatcherMoved(void*, void*);
static void OnDragCatcherReleased(void*, void*);

static void OnDragHandlePressed(void*, void*)
{
    if (g_dragging || !g_toolbarChildren || !g_railL) return;
    POINT cur{ 0, 0 }; GetCursorPos(&cur);
    float dx = 0, dy = 0;
    if (!DevToolsSurface_ScreenToLocal(g_toolbarSurface, cur.x, cur.y, &dx, &dy)) return;

    g_dragging = true;
    ShowARow(false);
    g_dragOffX = (double)dx - ChipX();
    g_dragOffY = (double)dy - ChipY();
    if (g_dragOffX < 0.0 || g_dragOffX > g_chipW) g_dragOffX = g_chipW / 2.0;
    if (g_dragOffY < 0.0 || g_dragOffY > (double)kChipH) g_dragOffY = (double)kChipH / 2.0;
    g_dragNear = g_corner;
    ShowSnapTargets(true);
    HighlightSnapTarget(g_dragNear);

    const int w = (int)g_protoW, h = (int)g_protoH;
    std::wstring markup = DevToolsXamlSubst(DevToolsXaml::DragCatcher, {
        { L"W", std::to_wstring(w) }, { L"H", std::to_wstring(h) } });
    IInspectable* catcher = nullptr;
    if (FAILED(LoadMarkup(markup, &catcher)) || !catcher) { EndToolbarDrag(); return; }
    if (g_pickDiag && g_pickSeed) {
        InstanceHandle ch = 0;
        if (SUCCEEDED(g_pickDiag->GetHandleFromIInspectable(catcher, &ch)) && ch) g_pickSeed(ch);
    }
    void* cuiEl = nullptr;
    if (SUCCEEDED(catcher->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &cuiEl)) && cuiEl) {
        g_dragMoveSink.Bind(&IID_PointerEventHandler_Del, &OnDragCatcherMoved);
        g_dragUpSink.Bind(&IID_PointerEventHandler_Del, &OnDragCatcherReleased);
        __int64 mtok = 0, rtok = 0;
        DevToolsAddPointerMoved(cuiEl, &g_dragMoveSink, &mtok);
        DevToolsAddPointerReleased(cuiEl, &g_dragUpSink, &rtok);
        reinterpret_cast<IUnknown*>(cuiEl)->Release();
    }
    IInspectable* cui = nullptr;
    if (SUCCEEDED(catcher->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), reinterpret_cast<void**>(&cui))) && cui) {
        unsigned int n = 0; DevToolsVecGetSize(g_toolbarChildren, &n); DevToolsVecInsertAt(g_toolbarChildren, n, cui); cui->Release();
    }
    g_dragCatcher = catcher;
    DevToolsOverlayLog(L"overlay.toolbar drag started at %.0f,%.0f (offset %.0f,%.0f)", (double)dx, (double)dy, g_dragOffX, g_dragOffY);
}

static void OnDragCatcherMoved(void*, void*)
{
    if (!g_dragging || !g_railL) return;
    POINT cur{ 0, 0 }; GetCursorPos(&cur);
    float dx = 0, dy = 0;
    if (!DevToolsSurface_ScreenToLocal(g_toolbarSurface, cur.x, cur.y, &dx, &dy)) { EndToolbarDrag(); return; }
    double x = (double)dx - g_dragOffX, y = (double)dy - g_dragOffY;
    if (x < -20.0) x = -20.0; if (x > g_protoW - 40.0) x = g_protoW - 40.0;
    if (y < -6.0)  y = -6.0;  if (y > g_protoH - 16.0) y = g_protoH - 16.0;
    MoveOverlayElement(g_railL, x, y);
    const int nearest = NearestCornerForPill(x, y);
    if (nearest != g_dragNear) { g_dragNear = nearest; HighlightSnapTarget(nearest); }
}

static void EndToolbarDrag()
{
    if (g_dragCatcher) {
        if (g_toolbarChildren) {
            unsigned int idx = 0; unsigned char found = 0;
            if (SUCCEEDED(DevToolsVecIndexOf(g_toolbarChildren, g_dragCatcher, &idx, &found)) && found)
                DevToolsVecRemoveAt(g_toolbarChildren, idx);
        }
        g_dragCatcher->Release();
        g_dragCatcher = nullptr;
    }
    ShowSnapTargets(false);
    g_dragging = false;
}

// Release: snap to the highlighted corner, then MORPH IN again there, closing the loop the press opened.
static void OnDragCatcherReleased(void*, void*)
{
    if (!g_dragging) return;
    const int landed = g_dragNear;
    EndToolbarDrag();
    SetCorner(landed, /*persist*/ true);
    if (!g_aRowOpen) ShowARow(true);
    DevToolsOverlayLog(L"overlay.toolbar drag released -> corner=%d", landed);
}

static void OnDragHandleEnter(void*, void*) { ProtoSetPartVisible(L"DevToolsDragHaloOn", true); }
static void OnDragHandleExit(void*, void*)  { ProtoSetPartVisible(L"DevToolsDragHaloOn", false); }

static void ClearAdorner();


// Re-anchor the toolbar to the current client size. Best-effort: if the client size can't be read, keep the
// last-known anchor rather than moving anything off-screen.
// Resize re-anchors toolbar; build-time bottom-right coordinates go stale with client size.
static void AlignOverlayOrigin(void*, void*)
{
    if (!g_protoRoot || !g_popup) return;
    static bool aligning = false;
    if (aligning) return;
    aligning = true;
    try {
        winrt::Windows::Foundation::IInspectable object{ nullptr }, popupObject{ nullptr };
        winrt::copy_from_abi(object, g_protoRoot);
        winrt::copy_from_abi(popupObject, g_popup);
        auto canvas = object.as<DevToolsX::UIElement>();
        auto popup = popupObject.as<DevToolsXCP::Popup>();
        auto root = canvas.XamlRoot();
        if (root && popup.IsOpen() && root.Content()) {
// Every overlay child is positioned in XamlRoot content DIPs, just like HitTestForXamlRoot. Measure the actual
// Popup child, not its padded parent.
            auto origin = canvas.TransformToVisual(root.Content()).TransformPoint({ 0, 0 });
            if (std::isfinite(origin.X) && std::isfinite(origin.Y)) {
                if (std::abs(origin.X) > 0.01f)
                    popup.HorizontalOffset(DevToolsInspector::PopupOffset(popup.HorizontalOffset(), origin.X));
                if (std::abs(origin.Y) > 0.01f)
                    popup.VerticalOffset(DevToolsInspector::PopupOffset(popup.VerticalOffset(), origin.Y));
            }
        }
    } catch (...) {
    }
    aligning = false;
}

static void RepositionToolbar()
{
    DevToolsSurface fresh;
    if (!DevToolsSurface_Resolve(g_pickDiag, g_toolbarSurface.rootHandle, &fresh) ||
        fresh.xamlRootKey != g_toolbarSurface.xamlRootKey) return;
    g_toolbarSurface = fresh;
    const int wDip = (int)fresh.contentW, hDip = (int)fresh.contentH;
    if (wDip <= 0 || hDip <= 0) return;
    g_protoW = (double)wDip; g_protoH = (double)hDip;
    g_titleBarInsetDip = MeasureTitleBarInsetDip();
    ShowARow(g_aRowOpen);
    if (g_pinned)       ProtoSetPinned(true);
    if (g_dragging)     ShowSnapTargets(true);
}
static void ReanchorSelectionNow();
static void OnClientResize(void*, void*)
{
    RepositionToolbar();
    ReanchorSelectionNow();
}

// Begin a named one-shot Storyboard declared in toolbar.xaml's Canvas.Resources, resolved by name in the loaded
// fragment's namescope (IFrameworkElement::FindName) and started via IStoryboard::Begin, both projected.
static void BeginNamedStoryboard(IInspectable* root, const wchar_t* name)
{
    if (!root || !name) return;
    void* fe = nullptr;
    if (FAILED(root->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) || !fe) return;
    IInspectable* sbInsp = nullptr;
    HSTRING hname = nullptr;
    if (SUCCEEDED(WindowsCreateString(name, (UINT32)wcslen(name), &hname))) {
        DevToolsFindName(fe, hname, &sbInsp);
        WindowsDeleteString(hname);
    }
    reinterpret_cast<IUnknown*>(fe)->Release();
    if (!sbInsp) { DevToolsOverlayLog(L"overlay.toolbar storyboard '%s' not found", name); return; }
    void* sb = nullptr;
    if (SUCCEEDED(sbInsp->QueryInterface(DevToolsIid<DevToolsXMA::IStoryboard>(), &sb)) && sb) {
        HRESULT hrB = DevToolsStoryboardBegin(sb);
        DevToolsOverlayLog(L"overlay.toolbar storyboard '%s' Begin hr=0x%08X", name, hrB);
        reinterpret_cast<IUnknown*>(sb)->Release();
    }
    sbInsp->Release();
}
static void PlayAttachPulse(IInspectable* overlayRoot) { BeginNamedStoryboard(overlayRoot, L"DevToolsAttachPulse"); }

// Subscribe the app panel's IFrameworkElement::add_SizeChanged (projected; no slot to name) so window resizes
// re-anchor the top-right toolbar (RepositionToolbar).
static void* g_resizeSource = nullptr;
static __int64 g_resizeToken = 0;
static void UnwireResizeTracking()
{
    if (!g_resizeSource) return;
    const HRESULT hr = DevToolsRemoveSizeChanged(g_resizeSource, g_resizeToken);
    DevToolsOverlayLog(L"overlay.toolbar resize-track remove_SizeChanged hr=0x%08X", hr);
    reinterpret_cast<IUnknown*>(g_resizeSource)->Release();
    g_resizeSource = nullptr;
}
static void WireResizeTracking(IInspectable* panelInsp)
{
    UnwireResizeTracking();
    if (!panelInsp) return;
    void* fe = nullptr;
    if (FAILED(panelInsp->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) || !fe) {
        DevToolsOverlayLog(L"overlay.toolbar resize-track: panel is not a FrameworkElement");
        return;
    }
    g_resizeSink.Bind(&IID_SizeChanged_Del, &OnClientResize);
    HRESULT hr = DevToolsAddSizeChanged(fe, &g_resizeSink, &g_resizeToken);
    if (SUCCEEDED(hr)) g_resizeSource = fe;
    else reinterpret_cast<IUnknown*>(fe)->Release();
    DevToolsOverlayLog(L"overlay.toolbar resize-track add_SizeChanged hr=0x%08X", hr);
}


// Wire the toolbar. MUST run on the UI thread.
static void WirePrototype(IInspectable* canvas)
{
    if (!canvas || !g_toolbarChildren) return;
// Cache the overlay Canvas root (AddRef'd for the agent lifetime): it owns the namescope the reveal storyboards
// resolve through (BeginNamedStoryboard -> FindName), and is the same root PlayAttachPulse is handed at build.
    // Canvas root is AddRef'd because storyboard resolution uses its namescope for agent lifetime.
    if (g_protoRoot != canvas) {
        if (g_protoRoot) g_protoRoot->Release();
        g_protoRoot = canvas;
        g_protoRoot->AddRef();
        void* fe = nullptr;
        if (SUCCEEDED(canvas->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) && fe) {
            const auto iid = DevToolsIid<winrt::Windows::Foundation::EventHandler<winrt::Windows::Foundation::IInspectable>>();
            g_originSink.Bind(&iid, &AlignOverlayOrigin);
            winrt::event_token token{};
            const HRESULT originHr = DevToolsAbi<DevToolsX::IFrameworkElement>(fe)->add_LayoutUpdated(&g_originSink, &token);
            if (FAILED(originHr)) DevToolsOverlayLog(L"overlay.origin LayoutUpdated subscription FAILED hr=0x%08X", originHr);
            reinterpret_cast<IUnknown*>(fe)->Release();
        }
    }
    if (!g_canvasStatics) {
        HSTRING cn = nullptr;
        if (SUCCEEDED(WindowsCreateString(L"Microsoft.UI.Xaml.Controls.Canvas",
                (UINT32)wcslen(L"Microsoft.UI.Xaml.Controls.Canvas"), &cn))) {
            RoGetActivationFactory(cn, DevToolsIid<DevToolsXC::ICanvasStatics>(), reinterpret_cast<void**>(&g_canvasStatics));
            WindowsDeleteString(cn);
        }
    }

    auto childAt = [&](unsigned int i, IInspectable** out) -> bool {
        *out = nullptr; return SUCCEEDED(DevToolsVecGetAt(g_toolbarChildren, i, out)) && *out;
    };
    auto borderItems = [&](unsigned int canvasIdx, IInspectable** outBorderUi, IInspectable** outItems) -> bool {
        *outBorderUi = nullptr; *outItems = nullptr;
        IInspectable* borderInsp = nullptr;
        if (FAILED(DevToolsVecGetAt(g_toolbarChildren, canvasIdx, &borderInsp)) || !borderInsp) return false;
        borderInsp->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), reinterpret_cast<void**>(outBorderUi));
        IInspectable* sp = nullptr;
        { IInspectable* border = nullptr;
          if (SUCCEEDED(borderInsp->QueryInterface(DevToolsIid<DevToolsXC::IBorder>(), reinterpret_cast<void**>(&border))) && border) {
              DevToolsBorderGetChild(border, &sp); border->Release();
          } }
        borderInsp->Release();
        if (!sp) return false;
        IInspectable* panel = nullptr;
        if (SUCCEEDED(sp->QueryInterface(DevToolsIid<DevToolsXC::IPanel>(), reinterpret_cast<void**>(&panel))) && panel) {
            IInspectable* kids = nullptr;
            if (SUCCEEDED(DevToolsPanelGetChildren(panel, &kids)) && kids) {
                kids->QueryInterface(DevToolsIid<DevToolsUIElementVector>(), reinterpret_cast<void**>(outItems));
                kids->Release();
            }
            panel->Release();
        }
        sp->Release();
        return *outItems != nullptr;
    };
    auto wireNamedClick = [&](const wchar_t* name, EventSink& sink, void (*cb)(void*, void*)) {
        IInspectable* it = DevToolsSelFindName(g_protoRoot, name);
        if (!it) return;
        IInspectable* bb = nullptr;
        if (SUCCEEDED(it->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), reinterpret_cast<void**>(&bb))) && bb) {
            sink.Bind(&IID_RoutedEventHandler_Del, cb);
            __int64 tok = 0; DevToolsAddClick(bb, &sink, &tok); bb->Release();
        }
        it->Release();
    };
    // Use the C++/WinRT named event instead of a manually indexed vtable slot.
    auto wireUiEvent = [&](IInspectable* uiInsp, DevToolsUiEvent which, EventSink& sink, void (*cb)(void*, void*)) {
        if (!uiInsp) return;
        void* cui = nullptr;
        if (SUCCEEDED(uiInsp->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &cui)) && cui) {
            const auto* iid = which == DevToolsUiEvent::GotFocus || which == DevToolsUiEvent::LostFocus
                ? &IID_RoutedEventHandler_Del : &IID_PointerEventHandler_Del;
            sink.Bind(iid, cb);
            __int64 tok = 0;
            const HRESULT hr = DevToolsAddUiEvent(cui, which, &sink, &tok);
            if (FAILED(hr)) DevToolsOverlayLog(L"overlay.toolbar event subscription failed hr=0x%08X", hr);
            reinterpret_cast<IUnknown*>(cui)->Release();
        }
    };

// Cache the movable groups (AddRef'd for the agent lifetime).
    if (g_aRow)    { g_aRow->Release();    g_aRow = nullptr; }    childAt(1, &g_aRow);
    if (g_railL)   { g_railL->Release();   g_railL = nullptr; }   childAt(0, &g_railL);
    for (int i = 0; i < kCornerCount; ++i) {
        if (g_snapUi[i]) { g_snapUi[i]->Release(); g_snapUi[i] = nullptr; }
        childAt(2 + (unsigned int)i, &g_snapUi[i]);
    }

// Wire the pill (hover-in expand; SizeChanged re-anchors it to the parked corner) + bar (hover-out collapse;
// action and pin clicks; the drag handle's press starts a move).
    if (g_railL) {
        wireUiEvent(g_railL, DevToolsUiEvent::PointerEntered, g_protoSink[0], &OnAExpand);
        wireUiEvent(g_railL, DevToolsUiEvent::GotFocus, g_protoSink[12], &OnPillGotFocus);
        void* fe = nullptr;
        if (SUCCEEDED(g_railL->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) && fe) {
            g_protoSink[8].Bind(&IID_SizeChanged_Del, &OnChipResize);
            __int64 tok = 0;
            HRESULT hr = DevToolsAddSizeChanged(fe, &g_protoSink[8], &tok);
            reinterpret_cast<IUnknown*>(fe)->Release();
            DevToolsOverlayLog(L"overlay.pill add_SizeChanged hr=0x%08X", hr);
        }
    }
    IInspectable* barUi = nullptr; IInspectable* items = nullptr;
    if (borderItems(1, &barUi, &items) && items) {
        IInspectable* handle = nullptr;
        if (SUCCEEDED(DevToolsVecGetAt(items, 0, &handle)) && handle) {
            void* cui = nullptr;
            if (SUCCEEDED(handle->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &cui)) && cui) {
                g_protoSink[9].Bind(&IID_PointerEventHandler_Del, &OnDragHandlePressed);
                __int64 tok = 0;
                HRESULT hr = DevToolsAddPointerPressed(cui, &g_protoSink[9], &tok);
                reinterpret_cast<IUnknown*>(cui)->Release();
                DevToolsOverlayLog(L"overlay.toolbar drag-handle add_PointerPressed hr=0x%08X", hr);
            }
            wireUiEvent(handle, DevToolsUiEvent::PointerEntered, g_protoSink[10], &OnDragHandleEnter);
            wireUiEvent(handle, DevToolsUiEvent::PointerExited,  g_protoSink[11], &OnDragHandleExit);
            handle->Release();
        }
        wireNamedClick(L"DevToolsProtoPick", g_protoSink[2], &OnPickClick);
        wireNamedClick(L"DevToolsProtoComments", g_protoSink[3], &OnCommentsToggleClick);
        wireNamedClick(L"DevToolsProtoInspect", g_protoSink[4], &OnInspectClick);
        wireNamedClick(L"DevToolsProtoLayout", g_protoSink[5], &OnLayoutToggleClick);
        wireNamedClick(L"DevToolsProtoTheme", g_protoSink[6], &OnThemeToggleClick);
        wireNamedClick(L"DevToolsProtoPin", g_protoSink[7], &OnPinClick);
        items->Release();
    }
    if (barUi) {
        wireUiEvent(barUi, DevToolsUiEvent::PointerExited, g_protoSink[1], &OnACollapse);
        wireUiEvent(barUi, DevToolsUiEvent::GotFocus, g_protoSink[13], &OnToolbarGotFocus);
        wireUiEvent(barUi, DevToolsUiEvent::LostFocus, g_protoSink[14], &OnToolbarLostFocus);
        barUi->Release();
    }

    DevToolsOverlayLog(L"overlay.toolbar wired (bar=%p pill=%p snaps=%p,%p,%p,%p,%p,%p position=%d)",
                  g_aRow, g_railL, g_snapUi[0], g_snapUi[1], g_snapUi[2],
                  g_snapUi[3], g_snapUi[4], g_snapUi[5], g_corner);
}
