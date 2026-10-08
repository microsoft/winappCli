// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsOverlay.cpp; included into that translation unit, not compiled on its own.

// ============================ Comment markers and pick exports ============================

// Fresh comment id: "cmt_" + 12 hex, matching the CLI's own id shape so the overlay owns the id up front (draw
// the pin + re-open for edit without parsing CLI stdout).
static std::wstring GenCommentId()
{
    GUID g{};
    if (SUCCEEDED(CoCreateGuid(&g))) {
        wchar_t buf[16];
        _snwprintf_s(buf, _countof(buf), _TRUNCATE, L"%08x%04x", g.Data1, g.Data2);
        return std::wstring(L"cmt_") + buf;
    }
    return std::wstring(L"cmt_") + std::to_wstring(GetTickCount64());
}

// -exempt: the catcher's sinks are add_Click + add_PointerMoved + add_PointerPressed; none of the three is
// re-synthesized by RemoveAt (unlike PointerExited/LostFocus, which XAML DOES raise against an element being
// removed under the pointer), OnCatcherPointerMoved and OnCatcherPointerPressed both null-guard g_pickCatcher.
static void RemovePickCatcher()
{
    ResetPickPointer();
    if (g_pickCatcher && g_canvasChildren) {
        IInspectable* cui = nullptr;
        if (SUCCEEDED(g_pickCatcher->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), reinterpret_cast<void**>(&cui))) && cui) {
            unsigned int idx = 0; unsigned char found = 0;
            if (SUCCEEDED(DevToolsVecIndexOf(g_canvasChildren, cui, &idx, &found)) && found) DevToolsVecRemoveAt(g_canvasChildren, idx);
            cui->Release();
        }
        g_pickCatcher->Release(); g_pickCatcher = nullptr;
    }
}

// Off-thread guard shared by the pick exports. The catcher mutates the live XAML canvas, so touching it from
// any thread but the one that built the overlay yields RPC_E_WRONG_THREAD (or worse, corruption).
// Pick/comment APIs refuse off-thread mutation; callers must marshal to the app UI thread.
static bool DevToolsPickExportOnUiThread(const wchar_t* who)
{
    if (g_overlayUiThreadId != 0 && GetCurrentThreadId() != g_overlayUiThreadId) {
        DevToolsOverlayLog(L"overlay.%s WRONG THREAD: called on tid=%lu, overlay UI thread is tid=%lu -- must marshal via RunOnUiThread; ignoring",
                      who, GetCurrentThreadId(), g_overlayUiThreadId);
        return false;
    }
    return true;
}


// The bool is the contract needs: an external client must not enable its Pick affordance on a false.
// ArmPick returns true only when a catcher exists; requested pick and deliverable pick are different.
bool DevToolsOverlay_ArmPick()
{
    if (g_selRootLost) {
        DevToolsOverlayLog(L"overlay.ArmPick refused while a detached draft awaits copy or discard");
        return false;
    }
    if (!DevToolsTrust_MutationEnabled()) {
        DevToolsOverlayLog(L"overlay.ArmPick refused by process posture");
        return false;
    }
    if (!DevToolsPickExportOnUiThread(L"ArmPick")) return false;
    if (!g_pickCatcher || CommentModeOn()) ArmInputMode(nullptr, false);
    return g_pickCatcher != nullptr && !CommentModeOn();
}

// Idempotent and safe when nothing is armed: RemovePickCatcher no-ops when g_pickCatcher is null, so no
// double-Release.
void DevToolsOverlay_DisarmPick()
{
    if (!DevToolsPickExportOnUiThread(L"DisarmPick")) return;
    ExitPickMode();
    DeselectAll();
    RemovePickCatcher();
}

void DevToolsOverlay_CompleteOneShotPick()
{
    if (!DevToolsPickExportOnUiThread(L"CompleteOneShotPick")) return;
    ExitPickMode();
}

void DevToolsOverlay_SetPickModeSink(DevToolsPickModeFn fn) { g_pickModeSink = fn; }

// Wired as PreviewKeyDown on the app's own content root, which TUNNELS root->target, so it fires even when
// keyboard focus is inside the app's own controls rather than the overlay -- which is the stated requirement.
// PreviewKeyDown tunnels from the app root, so Esc disarms even when focus is inside app controls. It also
// carries the toolbar focus shortcut (Ctrl+Shift+F12) and Esc's return from the toolbar.
static void OnAppEscape(void* /*sender*/, void* args)
{
    if (!args) return;
    IInspectable* ai = static_cast<IInspectable*>(args);
    void* kr = nullptr;
    if (FAILED(ai->QueryInterface(DevToolsIid<DevToolsXI::IKeyRoutedEventArgs>(), &kr)) || !kr) return;
    int key = 0;
    HRESULT hr = DevToolsGetKey(kr, &key);
    if (SUCCEEDED(hr) && IsLateToolbarShortcut(key) && FocusToolbarFromShortcut()) {
        DevToolsPutHandled(kr, true);
        reinterpret_cast<IUnknown*>(kr)->Release();
        return;
    }
    if (SUCCEEDED(hr) && ((IsToolbarShortcut(key) && FocusToolbarFromShortcut()) ||
                          (key == VK_ESCAPE && (CloseToolbarMenu(true) || ReturnFocusFromToolbar())))) {
        DevToolsPutHandled(kr, true);
        reinterpret_cast<IUnknown*>(kr)->Release();
        return;
    }
    const bool isEsc = SUCCEEDED(hr) && key == VK_ESCAPE;
    const bool acting = isEsc && (g_pickCatcher != nullptr || g_selectedHandle != 0 || g_selRootLost);
    if (acting) {
        DevToolsPutHandled(kr, true);
    }
    reinterpret_cast<IUnknown*>(kr)->Release();
    if (!acting) return;
    // Esc closes one layer at a time: an open panel first, then pick mode and the selection.
    const bool panelOpen = DevToolsSelPanelIsOpen();
    DevToolsOverlayLog(L"overlay.esc pick=%d selection=%llu panel=%d -> %s",
        (int)(g_pickCatcher != nullptr), (unsigned long long)g_selectedHandle, (int)panelOpen,
        panelOpen ? L"close the panel" : L"exit pick mode + deselect");
    if (panelOpen) DismissSelectionPanel(true);
    else { ExitPickMode(); DeselectAll(); }
}

// Each marker owns one sink carrying its element handle. Leaked for the process lifetime, bounded by the number
// of commented elements.
// Marker clicks carry handle/id so saved comments can reopen without re-picking the element.
static void OnPinClicked(InstanceHandle h);
struct PinClickSink : EventSink
{
    InstanceHandle handle = 0;
    HRESULT STDMETHODCALLTYPE Invoke(void*, void*) override { OnPinClicked(handle); return S_OK; }
};

// The display scale of the surface the markers are drawn on, so they can be laid on its pixel grid.
static double PinScale()
{
    HWND hwnd = DevToolsOverlay_ActiveSurface().hostHwnd;
    if (!hwnd) { HWND island = nullptr; ResolveAppWindowCached(&hwnd, &island); }
    const UINT dpi = hwnd ? GetDpiForWindow(hwnd) : 0;
    return dpi ? dpi / 96.0 : 1.0;
}

static std::wstring DipText(double value)
{
    wchar_t text[32];
    swprintf_s(text, L"%.4f", value);
    return text;
}

// A solid disc with a one-pixel white rim and a vector glyph, all on whole device pixels (SnapPin), with no template
// chrome, shadow or animation that could soften its edge.
static std::wstring BuildPinMarkup(const DevToolsSelection::PinPixels& pin, const std::wstring& tip)
{
    const DevToolsPalette& pal = DevToolsBuildPalette();
    std::wstring m;
    m += L"<Button xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"";
    m += L" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";
    m += L" AutomationProperties.AutomationId=\"WinAppDevToolsCommentPin\"";
    // Markers are visual only: hundreds of them would flood agents and screen readers. The Comments pane and the
    // toolbar's comment menu reach the same comments through UI Automation.
    m += L" AutomationProperties.AccessibilityView=\"Raw\" IsTabStop=\"False\"";
    m += L" AutomationProperties.Name=\"Comment on this element\"";
    m += L" ToolTipService.ToolTip=\"" + DevToolsXmlEscape(tip) + L"\"";
    m += L" Canvas.Left=\"" + DipText(pin.x) + L"\" Canvas.Top=\"" + DipText(pin.y) + L"\" Canvas.ZIndex=\"1900\"";
    m += L" Width=\"" + DipText(pin.size) + L"\" Height=\"" + DipText(pin.size) + L"\" Padding=\"0\" UseLayoutRounding=\"True\">";
    m += L"<Button.Template><ControlTemplate TargetType=\"Button\"><Grid Background=\"Transparent\">";
    m += L"<VisualStateManager.VisualStateGroups><VisualStateGroup x:Name=\"CommonStates\">";
    m += L"<VisualState x:Name=\"Normal\"/>";
    m += L"<VisualState x:Name=\"PointerOver\"><VisualState.Setters><Setter Target=\"Disc.Stroke\" Value=\"" + pal.accentText + L"\"/></VisualState.Setters></VisualState>";
    m += L"<VisualState x:Name=\"Pressed\"><VisualState.Setters><Setter Target=\"Disc.Stroke\" Value=\"" + pal.accentText + L"\"/></VisualState.Setters></VisualState>";
    m += L"</VisualStateGroup></VisualStateManager.VisualStateGroups>";
    m += L"<Ellipse x:Name=\"Disc\" Fill=\"" + pal.accent + L"\" Stroke=\"#FFFFFFFF\" StrokeThickness=\"" + DipText(pin.rim) + L"\"/>";
    m += L"<FontIcon Glyph=\"&#xE90A;\" FontFamily=\"Segoe Fluent Icons,Segoe MDL2 Assets\" FontSize=\"" + DipText(pin.glyph) + L"\"";
    m += L" Margin=\"" + DipText((pin.size - pin.glyph) / 2) + L",0,0,0\"";
    // Text puts this glyph half a device pixel right of its layout slot, which splits each 1 px stem over two pixels.
    m += L" RenderTransform=\"1,0,0,1," + DipText(-pin.rim / 2) + L",0\"";
    m += L" Foreground=\"" + pal.accentText + L"\" HorizontalAlignment=\"Left\" VerticalAlignment=\"Center\"/>";
    m += L"</Grid></ControlTemplate></Button.Template>";
    m += L"</Button>";
    return m;
}

// (Re)draw one pin's marker beside its element bounds, self-excluded from the oracle.
static void DrawPinUi(PinnedComment& p)
{
    if (!g_canvasChildren) return;
    RECT current{};
    if (!TryElementRootBounds(g_pickDiag, g_pickRoot, p.handle, &current)) return;
    int w = 0, h = 0; if (!GetClientSizeDips(&w, &h)) { w = 1600; h = 900; }
    if (current.right <= 0 || current.bottom <= 0 || current.left >= w || current.top >= h) return;
    p.bounds = current;
    const auto pin = DevToolsSelection::PlacePin(
        { current.left, current.top, current.right, current.bottom }, DevToolsSelection::PinViewport(w, h, PinTitleBarInsetDip()));
    std::wstring tip = p.text.size() > 120 ? (p.text.substr(0, 117) + L"\u2026") : p.text;
    if (tip.empty()) tip = L"Comment";
    IInspectable* insp = nullptr;
    if (FAILED(LoadMarkup(BuildPinMarkup(DevToolsSelection::SnapPin(pin, PinScale()), tip), &insp)) || !insp) return;
    if (g_pickDiag && g_pickSeed) { InstanceHandle ph = 0; if (SUCCEEDED(g_pickDiag->GetHandleFromIInspectable(insp, &ph)) && ph) g_pickSeed(ph); }
    void* bb = nullptr;
    if (SUCCEEDED(insp->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), &bb)) && bb) {
        if (!p.sink) p.sink = new PinClickSink();
        p.sink->handle = p.handle;
        p.sink->Bind(&IID_RoutedEventHandler_Del, nullptr);
        __int64 tok = 0; DevToolsAddClick(bb, p.sink, &tok);
        reinterpret_cast<IUnknown*>(bb)->Release();
    }
    IInspectable* ui = nullptr;
    if (SUCCEEDED(insp->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), reinterpret_cast<void**>(&ui))) && ui) {
        unsigned int n = 0; DevToolsVecGetSize(g_canvasChildren, &n);
        if (g_cardScope) g_cardScope(1);
        DevToolsVecInsertAt(g_canvasChildren, n, ui);
        if (g_cardScope) g_cardScope(0);
        p.ui = ui;
    }
    insp->Release();
}

// Keyed by the element's raw handle; re-saving updates text/bounds. Saving also turns the comment markers ON
// (SetCommentsShown), so the marker the user just earned is actually on screen and the toolbar toggle agrees
// with what they can see.
static void UpsertPin(InstanceHandle key, const std::wstring& id, const std::wstring& text, const RECT& bounds)
{
// capture a rebuild-stable anchor now, while the element is alive, so the pin can be re-anchored after the
// element is destroyed and rebuilt (source-qualified authored ancestry). Empty when reliable source identity is
// unavailable, in which case the pin simply never re-anchors.
    std::wstring elAnchor;
    if (g_anchorOf && key) g_anchorOf(key, &elAnchor);
    bool found = false;
    for (auto& p : g_pins) {
// Match on the element first, then on the comment id — a pin seeded from the store may still be carrying the
// handle it resolved to, and editing that comment in the panel must update it in place rather than leave a
// second marker on the same element.
        // Store-seeded pins may keep old handles; match by id too so edits update rather than duplicate.
        if ((key && p.handle == key) || (!id.empty() && p.id == id)) {
            if (p.ui && g_canvasChildren) {
                unsigned int idx = 0; unsigned char has = 0;
                if (SUCCEEDED(DevToolsVecIndexOf(g_canvasChildren, p.ui, &idx, &has)) && has) DevToolsVecRemoveAt(g_canvasChildren, idx);
            }
            if (p.ui) { p.ui->Release(); p.ui = nullptr; }
            p.id = id; p.text = text; p.bounds = bounds;
            p.handle = key;
            if (p.sink) p.sink->handle = key;
            if (!elAnchor.empty()) p.name = elAnchor;
            found = true;
            break;
        }
    }
    if (!found) {
        PinnedComment np{}; np.handle = key; np.id = id; np.text = text; np.bounds = bounds; np.ui = nullptr; np.sink = nullptr;
        np.name = elAnchor;
        g_pins.push_back(np);
    }
    SetCommentsShown(true);
}

// Remove all pin markers from the Canvas (registry entries retained so a later toggle-on can redraw them).
// -exempt: a marker's only sink is add_Click, which RemoveAt does not re-synthesize (unlike the
// PointerExited/LostFocus XAML raises against an element being removed under the pointer/with focus), and the
// sink never de.
static void ClearAllPinsUi()
{
    for (auto& p : g_pins) {
        if (p.ui && g_canvasChildren) {
            unsigned int idx = 0; unsigned char found = 0;
            if (SUCCEEDED(DevToolsVecIndexOf(g_canvasChildren, p.ui, &idx, &found)) && found) DevToolsVecRemoveAt(g_canvasChildren, idx);
        }
        if (p.ui) { p.ui->Release(); p.ui = nullptr; }
    }
}

static void DrawAllPins()
{
    for (auto& p : g_pins) if (!p.ui && p.bounds.right > p.bounds.left) DrawPinUi(p);
}

// Bounds are re-read first so the panel anchors where the element is NOW, not where it was when saved.
static void OnPinClicked(InstanceHandle h)
{
    if (!g_pickDiag || !h) return;
    RECT nb{};
    if (TryElementRootBounds(g_pickDiag, g_pickRoot, h, &nb)) g_lastPickBoundsDip = nb;
    DevToolsOverlayLog(L"overlay.comments pin clicked handle=%llu", (unsigned long long)h);
    DevToolsOverlay_ShowBadgeForHandle(g_pickDiag, h, /*fromUserPick*/ true);
}
