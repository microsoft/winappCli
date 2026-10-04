// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsOverlay.cpp; included into that translation unit, not compiled on its own.

HRESULT DevToolsOverlay_BuildAndInsert(IXamlDiagnostics* diag, IVisualTreeService3* vts3,
                                  InstanceHandle appPanel, unsigned int index, DevToolsSeedFn seed,
                                  InstanceHandle rootHandle, DevToolsIsOverlayFn isOverlay,
                                  std::wstring* errorMessage)
{
    if (errorMessage) errorMessage->clear();
    if (!diag || !vts3 || !appPanel) return E_INVALIDARG;
    winrt::com_ptr<IInspectable> themePanel;
    HRESULT hr = diag->GetIInspectableFromHandle(appPanel, themePanel.put());
    if (FAILED(hr) || !themePanel) return FAILED(hr) ? hr : E_INVALIDARG;
    winrt::com_ptr<IUnknown> identity;
    hr = ReadHostRootIdentity(themePanel.get(), identity);
    if (FAILED(hr) || !identity) {
        hr = FAILED(hr) ? hr : E_NOINTERFACE;
        DevToolsOverlayLog(L"overlay.host root identity unavailable hr=0x%08X", hr);
        return hr;
    }

// All of these are touched only on the UI thread (BuildAndInsert runs there, and the Click/DragDelta events
// fire there).
    g_pickDiag = diag; g_pickRoot = rootHandle; g_pickSeed = seed; g_pickIsOverlay = isOverlay;
    if (!DevToolsSurface_Resolve(diag, appPanel, &g_toolbarSurface)) return E_PENDING;
    g_overlayUiThreadId = GetCurrentThreadId();
    if (g_appPanel) g_appPanel->Release();
    g_appPanel = themePanel.detach();

// Activate the APP'S OWN XamlReader. RoGetActivationFactory resolves against whatever Microsoft.UI.Xaml the
// target already loaded (WinAppSDK routes Microsoft.* activation in-process), so we bind to the app's WinUI
// version rather than any SDK we compiled against.
    // Activate the target app's XamlReader so controls match the app's loaded WinUI version.
    HSTRING clsId = nullptr;
    hr = WindowsCreateString(L"Microsoft.UI.Xaml.Markup.XamlReader",
                                     (UINT32)wcslen(L"Microsoft.UI.Xaml.Markup.XamlReader"), &clsId);
    if (FAILED(hr)) return hr;
    IInspectable* reader = nullptr;
    hr = RoGetActivationFactory(clsId, DevToolsIid<DevToolsXMk::IXamlReaderStatics>(), reinterpret_cast<void**>(&reader));
    WindowsDeleteString(clsId);
    DevToolsOverlayLog(L"overlay.step1 RoGetActivationFactory(XamlReader) hr=0x%08X reader=%p", hr, reader);
    if (FAILED(hr) || !reader) return FAILED(hr) ? hr : E_NOINTERFACE;

// Parse the embedded markup with the app's runtime XAML reader. The markup is a client-sized
// hit-test-transparent Canvas host containing the VS-style toolbar; the Canvas is the shared coordinate space
// the pick adorner is later added into (DevToolsOverlay_Pick).
    int wDip = 0, hDip = 0;
    if (!GetClientSizeDips(&wDip, &hDip)) { wDip = 1920; hDip = 1080; }
    DevToolsOverlayLog(L"overlay.step2 client size %dx%d dip", wDip, hDip);
    g_protoW = (double)wDip;
    g_protoH = (double)hDip;
    g_titleBarInsetDip = MeasureTitleBarInsetDip();
    g_corner = DevToolsSettingsGetIndex(L"ToolbarCorner", CornerBR, kCornerCount);
    std::wstring markupStr = BuildPrototypeMarkup(wDip, hDip);
    DevToolsOverlayLog(L"overlay.step2 toolbar = corner pill + action bar (corner=%d)", g_corner);
    HSTRING markup = nullptr;
    hr = WindowsCreateString(markupStr.c_str(), (UINT32)markupStr.size(), &markup);
    if (SUCCEEDED(hr))
    {
        IInspectable* overlay = nullptr;
        hr = DevToolsXamlReaderLoad(reader, markup, &overlay);
        if (FAILED(hr) && errorMessage) *errorMessage = DevToolsXamlErrorMessage(hr);
        WindowsDeleteString(markup);
        DevToolsOverlayLog(L"overlay.step2 XamlReader.Load hr=0x%08X overlay=%p", hr, overlay);
        if (SUCCEEDED(hr) && overlay)
        {
// Cache the Canvas host's OWN Children collection so DevToolsOverlay_Pick can later add the pick-highlight
// adorner into the same coordinate space as the toolbar. AddRef-kept for the agent lifetime (single overlay per
// process).
            // Cache/AddRef Canvas Children for overlay lifetime; picks draw in the toolbar coordinate space.
            {
                IInspectable* canvasPanel = nullptr;
                if (SUCCEEDED(overlay->QueryInterface(DevToolsIid<DevToolsXC::IPanel>(), reinterpret_cast<void**>(&canvasPanel))) && canvasPanel)
                {
                    IInspectable* kids = nullptr;
                    if (SUCCEEDED(DevToolsPanelGetChildren(canvasPanel, &kids)) && kids)
                    {
                        ClearLayoutAdorner();
                        if (g_toolbarChildren) { g_toolbarChildren->Release(); g_toolbarChildren = nullptr; }
                        kids->QueryInterface(DevToolsIid<DevToolsUIElementVector>(), reinterpret_cast<void**>(&g_toolbarChildren));
                        kids->Release();
                    }
                    canvasPanel->Release();
                }
                DevToolsOverlayLog(L"overlay.step2b toolbarChildren=%p", g_toolbarChildren);
            }

// Wire the toolbar controls (indicator hover + bar action buttons) to their handlers while the subtree is still
// detached; add_Click/add_Pointer* work on detached elements.
            WirePrototype(overlay);

// Restore the persisted global state (pinned-open, parked corner): if the user pinned or moved the toolbar in
// ANY app on a previous run, apply that now.
            if (DevToolsSettingsGetBool(L"ToolbarPinned", false)) { ProtoSetPinned(true); ShowARow(true); }
// the layout-adorner toggle is persisted the same way.
            if (DevToolsSettingsGetBool(L"LayoutAdorners", false)) SetLayoutAdornersOn(true);
            UpdateChipLabel();

// The toolbar content is hosted in the XAML diagnostics UI layer of the app panel's XamlRoot (HostAttach). Its
// Popup, created from the APP'S OWN WinUI, is the fallback host when that layer is unavailable: inserted into the
// app panel, its content floats on the framework's popup layer. A detached Popup fires no tree-change callbacks.
            IInspectable* overlayUi = nullptr;
            hr = overlay->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), reinterpret_cast<void**>(&overlayUi));
            DevToolsOverlayLog(L"overlay.step3 QI toolbar IUIElement hr=0x%08X ui=%p", hr, overlayUi);
            IInspectable* popupInsp = nullptr;
            HSTRING popupCls = nullptr;
            if (SUCCEEDED(hr) && overlayUi &&
                SUCCEEDED(WindowsCreateString(L"Microsoft.UI.Xaml.Controls.Primitives.Popup",
                    (UINT32)wcslen(L"Microsoft.UI.Xaml.Controls.Primitives.Popup"), &popupCls)))
            {
                HRESULT hrPop = RoActivateInstance(popupCls, &popupInsp);
                WindowsDeleteString(popupCls);
                DevToolsOverlayLog(L"overlay.step3 RoActivateInstance(Popup) hr=0x%08X insp=%p", hrPop, popupInsp);
                IInspectable* popup = nullptr;
                if (SUCCEEDED(hrPop) && popupInsp &&
                    SUCCEEDED(popupInsp->QueryInterface(DevToolsIid<DevToolsXCP::IPopup>(), reinterpret_cast<void**>(&popup))) && popup)
                {
                    if (g_popup) { g_popup->Release(); g_popup = nullptr; }
                    g_popup = popup; g_popup->AddRef();
                    if (g_popupUi) { g_popupUi->Release(); g_popupUi = nullptr; }
                    (void)popupInsp->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), reinterpret_cast<void**>(&g_popupUi));
                    HostRegister(popupInsp, g_popupUi, overlayUi, /*topmost*/ true);
                    HRESULT hrC = DevToolsPopupPutChild(popup, overlayUi);
                    DevToolsPopupPutHorizontalOffset(popup, 0.0);
                    DevToolsPopupPutVerticalOffset(popup, 0.0);
                    DevToolsOverlayLog(L"overlay.step3 put_Child hr=0x%08X", hrC);

// PRESEED the exclusion set with BOTH the Popup and its toolbar-content handle BEFORE the Popup enters the live
// tree / opens, so every Add the insert+open triggers is self-excluded and the app-only count / `pong <count>`
// oracle stays intact.
                    InstanceHandle ph = 0, ch = 0;
                    diag->GetHandleFromIInspectable(overlay, &ch);
                    HRESULT hrH = diag->GetHandleFromIInspectable(popupInsp, &ph);
                    DevToolsOverlayLog(L"overlay.step4 seed handles popup=%llu(hr=0x%08X) child=%llu", ph, hrH, ch);
                    if (seed && ch) seed(ch);
                    if (SUCCEEDED(hrH) && ph)
                    {
                        if (seed) seed(ph);

// Host the toolbar for the app panel's XamlRoot: the UI layer, or the zero-size Popup in the panel's Children.
                        (void)index;
                        bool attached = false;
                        IInspectable* popupUi = nullptr;
                        HRESULT hrU = popupInsp->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), reinterpret_cast<void**>(&popupUi));
                        if (SUCCEEDED(hrU) && popupUi)
                        {
                            IInspectable* panelInsp = nullptr;
                            HRESULT hrP = diag->GetIInspectableFromHandle(appPanel, &panelInsp);
                            DevToolsOverlayLog(L"overlay.step6 GetIInspectableFromHandle(panel=%llu) hr=0x%08X insp=%p", appPanel, hrP, panelInsp);
                            if (SUCCEEDED(hrP) && panelInsp)
                            {
                                if (SUCCEEDED(HostAttach(diag, panelInsp, popupUi, L"step6.popup"))) attached = true;
                                if (attached) WireResizeTracking(panelInsp);
                                if (attached && !g_appPanel) {
                                    g_appPanel = panelInsp;
                                    g_appPanel->AddRef();
                                }
                                panelInsp->Release();
                            }
                            else hr = hrP;
                            popupUi->Release();
                        }
                        else hr = hrU;

// Open the Popup now that it is in the tree (inherits XamlRoot) -> its Child floats on the popup layer, on top
// of all app content.
                        if (attached)
                        {
                            HRESULT hrO = HostPutOpen(popup, true);
                            DevToolsOverlayLog(L"overlay.step7 put_IsOpen hr=0x%08X", hrO);
// Fire the one-shot attach pulse now the overlay is live on the popup layer -- a brief glow brighten that draws
// the eye on attach, then settles static.
                            if (SUCCEEDED(hrO)) PlayAttachPulse(overlay);
// Overall success also requires the toolbar to have actually become the Popup's child in step 3; propagate a
// put_Child failure (hrC) so we can never report S_OK for a childless (invisible) overlay even when insert +
// open themselves succeeded.
                            hr = SUCCEEDED(hrO) ? hrC : hrO;
                        }
                        else hr = FAILED(hr) ? hr : E_FAIL;
                    }
                    else hr = hrH;
                    popup->Release();
                }
                else hr = FAILED(hrPop) ? hrPop : E_NOINTERFACE;
                if (popupInsp) popupInsp->Release();
            }
            if (overlayUi) overlayUi->Release();
            overlay->Release();
        }
    }
    reader->Release();
    if (SUCCEEDED(hr)) hr = DevToolsOverlay_MoveSelectionToPanel(diag, appPanel);
// the rail is visible immediately once the build succeeds -- this is the ONE place BuildAndInsert (idempotent,
// runs once per process) completes, so it is the natural home for the initial true. A failed build must NOT
// claim the toolbar is visible (there is nothing on screen to back that up).
    // Set rail visible only after a successful build so failed construction does not claim visible state.
    if (SUCCEEDED(hr)) {
        g_hostRootIdentity = std::move(identity);
        ++g_hostGeneration;
        g_failedRecoveryPanel = 0;
        g_toolbarVisible = true;
        ShowARow(g_aRowOpen);
        NotifyStateChanged();
    }
    return hr;
}

// Parse a markup fragment with the app's OWN XamlReader (same late-bound path as the toolbar). Returns the
// parsed root IInspectable; caller owns the ref.
static HRESULT LoadMarkup(const std::wstring& xaml, IInspectable** out)
{
    *out = nullptr;
    HSTRING clsId = nullptr;
    HRESULT hr = WindowsCreateString(L"Microsoft.UI.Xaml.Markup.XamlReader",
                                     (UINT32)wcslen(L"Microsoft.UI.Xaml.Markup.XamlReader"), &clsId);
    if (FAILED(hr)) return hr;
    IInspectable* reader = nullptr;
    hr = RoGetActivationFactory(clsId, DevToolsIid<DevToolsXMk::IXamlReaderStatics>(), reinterpret_cast<void**>(&reader));
    WindowsDeleteString(clsId);
    if (FAILED(hr) || !reader) return FAILED(hr) ? hr : E_NOINTERFACE;
    HSTRING m = nullptr;
    hr = WindowsCreateString(xaml.c_str(), (UINT32)xaml.size(), &m);
    if (SUCCEEDED(hr)) { hr = DevToolsXamlReaderLoad(reader, m, out); WindowsDeleteString(m); }
    reader->Release();
    return hr;
}

// Log a handle's runtime class (calibration/diagnostic): resolves the live object and prints its type so we can
// confirm which coordinate space HitTest uses and which end of the returned array is front-most.
static void LogHandleClass(IXamlDiagnostics* diag, const wchar_t* tag, InstanceHandle h)
{
    IInspectable* insp = nullptr;
    if (SUCCEEDED(diag->GetIInspectableFromHandle(h, &insp)) && insp) { LogRuntimeClass(tag, insp); insp->Release(); }
    else DevToolsOverlayLog(L"overlay.%s handle=%llu <unresolved>", tag, h);
}

// sample(c) is true while the point at coordinate c (other axis fixed at the pick point) still hits `target`.
// Returns the last inside coordinate (the edge).
static double FindEdge(const std::function<bool(double)>& sample, double start, int dir)
{
    if (!sample(start)) return start;
    double inside = start, step = 4.0, outside = start + dir * step;
    for (int i = 0; i < 14; ++i) {
        double c = start + dir * step;
        if (!sample(c)) { outside = c; break; }
        inside = c; step *= 2.0; outside = start + dir * step;
    }
    for (int i = 0; i < 16; ++i) { double mid = (inside + outside) / 2.0; if (sample(mid)) inside = mid; else outside = mid; }
    return inside;
}

static double QpcNow()
{
    static LARGE_INTEGER freq{};
    if (!freq.QuadPart) QueryPerformanceFrequency(&freq);
    LARGE_INTEGER c{}; QueryPerformanceCounter(&c);
    return freq.QuadPart ? (double)c.QuadPart * 1000000.0 / (double)freq.QuadPart : 0.0;
}
static int g_adornerX = 0, g_adornerY = 0, g_adornerW = 0, g_adornerH = 0;
static double g_lastAdornerLoadUs = 0.0;
static double g_lastAdornerTotalUs = 0.0;
static double g_lastAdornerClearUs = 0.0;
static double g_lastAdornerInsertUs = 0.0;
static double g_lastPickWorkUs = 0.0;

static void ClearAdorner(){
// -exempt (no null-before-RemoveAt needed): the adorner is a visual highlight with IsHitTestVisible=False and
// no pointer/focus/click sink, so removing it under the pointer raises no re-entrant callback against it.
    // Removing the adorner under the pointer is safe only because it has no pointer/focus/click sinks.
    if (g_adorner && g_highlightChildren) {
        unsigned int idx = 0; unsigned char found = 0;
        if (SUCCEEDED(DevToolsVecIndexOf(g_highlightChildren.get(), g_adorner, &idx, &found)) && found)
            DevToolsVecRemoveAt(g_highlightChildren.get(), idx);
    }
    if (g_adorner) { g_adorner->Release(); g_adorner = nullptr; }
    g_adornerW = g_adornerH = 0;
}

// Draw (or replace) the pick-highlight adorner in the overlay Canvas at the given DIP rect. The adorner is a
// stroke-only, hit-test-transparent Border, so it never intercepts the next pick and — being a child of the
// overlay Canvas (whose handle is in the exclusion set) — is self-excluded from the element-count oracle (bel.
static HRESULT DrawAdorner(IXamlDiagnostics* diag, int x, int y, int w, int h, DevToolsSeedFn seed)
{
    WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::OverlayAdorner);
    if (!g_highlightChildren) { DevToolsOverlayLog(L"overlay.adorner no canvas host"); return E_PENDING; }
    if (w < 1) w = 1; if (h < 1) h = 1;

// DevToolsOverlayLog opens, writes and closes the log file per line, on a path shared with every other injected
// app on the machine, so any span that contains a log call measures the logger, not the overlay -- which is
// exactly how "the adorner costs 60ms" would become a confident, wrong conclusion.
    const double tClear0 = QpcNow();
    ClearAdorner();
    const double clearUs = QpcNow() - tClear0;

    IInspectable* adornInsp = nullptr;
    const double tLoad0 = QpcNow();
    HRESULT hr = LoadMarkup(BuildAdornerMarkup(x, y, w, h), &adornInsp);
    const double loadUs = QpcNow() - tLoad0;
    g_lastAdornerLoadUs = loadUs;
    if (FAILED(hr) || !adornInsp) {
        g_lastAdornerTotalUs = clearUs + loadUs;
        DevToolsOverlayLog(L"overlay.adorner Load hr=0x%08X insp=%p rect=%d,%d,%d,%d", hr, adornInsp, x, y, w, h);
        return FAILED(hr) ? hr : E_NOINTERFACE;
    }

    const double tIns0 = QpcNow();
    InstanceHandle ah = 0;
    if (SUCCEEDED(diag->GetHandleFromIInspectable(adornInsp, &ah)) && ah && seed) seed(ah);
    IInspectable* adornUi = nullptr;
    unsigned int size = 0; HRESULT hrI = E_FAIL;
    if (SUCCEEDED(adornInsp->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), reinterpret_cast<void**>(&adornUi))) && adornUi) {
        hrI = DevToolsVecGetSize(g_highlightChildren.get(), &size);
        if (SUCCEEDED(hrI)) hrI = DevToolsVecInsertAt(g_highlightChildren.get(), size, adornUi);
        adornUi->Release();
        if (SUCCEEDED(hrI)) { g_adorner = adornInsp; adornInsp = nullptr; }
    }
    if (adornInsp) adornInsp->Release();
    const double insertUs = QpcNow() - tIns0;
    g_lastAdornerTotalUs = clearUs + loadUs + insertUs;
    g_lastAdornerClearUs = clearUs;
    g_lastAdornerInsertUs = insertUs;
    g_adornerX = x; g_adornerY = y; g_adornerW = w; g_adornerH = h;
    DevToolsOverlayLog(L"overlay.adorner Load hr=0x%08X rect=%d,%d,%d,%d InsertAt(%u) hr=0x%08X seed=%llu",
        hr, x, y, w, h, size, hrI, ah);
    return hrI;
}

// Bounds changes must stay on the property-set path: live scroll tracking runs during layout and must not parse
// markup or add/remove Canvas children.
// Move/resize highlight by property sets, not child replacement, to avoid WinUI mid-frame churn.
static void PlaceAdorner(IXamlDiagnostics* diag, int x, int y, int w, int h, DevToolsSeedFn seed)
{
    if (!g_selectedHandle || (g_lastHighlightHandle && g_lastHighlightHandle != g_selectedHandle)) return;
    if (g_selectionHost && FAILED(g_selectionHost(g_selectedHandle, false))) return;
    if (w < 1) w = 1; if (h < 1) h = 1;
    if (g_adorner && g_adornerW > 0 && g_adornerH > 0) {
        if (w != g_adornerW || h != g_adornerH) {
            void* fe = nullptr;
            if (SUCCEEDED(g_adorner->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) && fe) {
                DevToolsPutWidth(fe, (double)w);
                DevToolsPutHeight(fe, (double)h);
                reinterpret_cast<IUnknown*>(fe)->Release();
                g_adornerW = w; g_adornerH = h;
            } else {
                DrawAdorner(diag, x, y, w, h, seed);
                return;
            }
        }
        if (x != g_adornerX || y != g_adornerY) {
            MoveOverlayElement(g_adorner, (double)x, (double)y);
            g_adornerX = x; g_adornerY = y;
        }
        return;
    }
    DrawAdorner(diag, x, y, w, h, seed);
}

// Last pick's element bounds in content-DIP space (== the overlay Canvas coordinate space).
// DevToolsOverlay_Pick stamps this on every hit so ShowCardForElement can anchor the card ADJACENT to the
// picked element instead of parking it at a fixed corner.
static RECT g_lastPickBoundsDip{};
// Navigate a live Panel's OWN Children as an IVector<UIElement> (AddRef'd; caller releases).
// Returned Panel.Children vector is AddRef'd for the caller and must be released.
static IInspectable* CardChildrenOf(IInspectable* panelInsp)
{
    if (!panelInsp) return nullptr;
    IInspectable* panel = nullptr;
    if (FAILED(panelInsp->QueryInterface(DevToolsIid<DevToolsXC::IPanel>(), reinterpret_cast<void**>(&panel))) || !panel) return nullptr;
    IInspectable* vec = nullptr;
    IInspectable* kids = nullptr;
    if (SUCCEEDED(DevToolsPanelGetChildren(panel, &kids)) && kids) {
        kids->QueryInterface(DevToolsIid<DevToolsUIElementVector>(), reinterpret_cast<void**>(&vec));
        kids->Release();
    }
    panel->Release();
    return vec;
}
