// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsOverlay.cpp; included into that translation unit, not compiled on its own.

// Overlay children are AddRef-kept for process lifetime and draw in the toolbar coordinate space.
static IInspectable* g_canvasChildren = nullptr;
static IInspectable* g_toolbarChildren = nullptr;
static winrt::com_ptr<IInspectable> g_highlightChildren;
static IInspectable* g_adorner = nullptr;

// The target app's main top-level window (largest visible unowned window owned by this process). Used for
// screen<->client mapping (pick input is screen px) and DPI-aware conversion to XAML DIPs.
static HWND FindAppWindow()
{
    DWORD pid = GetCurrentProcessId();
    HWND best = nullptr; long bestArea = 0; HWND h = nullptr;
    while ((h = FindWindowExW(nullptr, h, nullptr, nullptr)) != nullptr)
    {
        DWORD wpid = 0; GetWindowThreadProcessId(h, &wpid);
        if (wpid != pid || !IsWindowVisible(h) || GetWindow(h, GW_OWNER) != nullptr) continue;
        RECT rc{}; if (!GetClientRect(h, &rc)) continue;
        long area = (long)(rc.right - rc.left) * (long)(rc.bottom - rc.top);
        if (area > bestArea) { bestArea = area; best = h; }
    }
    return best;
}

// The XAML content island is hosted in a CHILD HWND of the top-level window; its client origin in screen space
// == the XamlRoot Content top-left (offset below the title bar from the top-level client origin). Pick the
// largest descendant child so a screen->content DIP conversion (ScreenToContentDip, used by the element pick.
// The content island is a child HWND; pick conversion uses the island origin, not the top-level client.
static HWND FindContentIsland(HWND parent)
{
    HWND best = parent; long bestArea = 0; HWND c = nullptr;
    while ((c = FindWindowExW(parent, c, nullptr, nullptr)) != nullptr)
    {
        RECT rc{}; if (!GetClientRect(c, &rc)) continue;
        long area = (long)(rc.right - rc.left) * (long)(rc.bottom - rc.top);
        if (area > bestArea) { bestArea = area; best = c; }
    }
    if (best != parent)
    {
        HWND inner = nullptr; long innerArea = 0; c = nullptr;
        while ((c = FindWindowExW(best, c, nullptr, nullptr)) != nullptr)
        {
            RECT rc{}; if (!GetClientRect(c, &rc)) continue;
            long area = (long)(rc.right - rc.left) * (long)(rc.bottom - rc.top);
            if (area > innerArea) { innerArea = area; inner = c; }
        }
        if (inner) best = inner;
    }
    return best;
}

// Cache HWND discovery on pointer paths; validate visibility, process identity and liveness before reuse. Do
// not cache DPI, which can change when the window moves between monitors.
static HWND g_cachedAppHwnd = nullptr;
static HWND g_cachedIsland  = nullptr;

static bool AppWindowStillValid(HWND h)
{
    if (!h || !IsWindow(h) || !IsWindowVisible(h)) return false;
    DWORD wpid = 0; GetWindowThreadProcessId(h, &wpid);
    return wpid == GetCurrentProcessId();
}

static void ResolveAppWindowCached(HWND* hwndOut, HWND* islandOut)
{
    const auto& surface = DevToolsOverlay_ActiveSurface();
    if (surface.Valid()) {
        const HWND host = AppWindowStillValid(surface.hostHwnd) ? surface.hostHwnd : nullptr;
        if (host != g_cachedAppHwnd || !g_cachedIsland || !IsWindow(g_cachedIsland)) {
            g_cachedAppHwnd = host;
            g_cachedIsland = host ? FindContentIsland(host) : nullptr;
        }
        if (hwndOut) *hwndOut = g_cachedAppHwnd;
        if (islandOut) *islandOut = g_cachedIsland;
        return;
    }
    if (!AppWindowStillValid(g_cachedAppHwnd) || !g_cachedIsland || !IsWindow(g_cachedIsland)) {
        g_cachedAppHwnd = FindAppWindow();
        g_cachedIsland  = g_cachedAppHwnd ? FindContentIsland(g_cachedAppHwnd) : nullptr;
        DevToolsOverlayLog(L"overlay.window resolved (cache miss) top=%p island=%p", g_cachedAppHwnd, g_cachedIsland);
    }
    if (hwndOut)   *hwndOut   = g_cachedAppHwnd;
    if (islandOut) *islandOut = g_cachedIsland;
}

// The hover path QI'd for it on EVERY pointer move (the peek) and again per element change (the pick), and the
// 30Hz tracking tick QI'd once per visibility probe. It is an additional interface on the ONE live site
// diagnostics object, so the answer never changes for a given `diag` -- cache it, keyed on that pointer, and
// ke.
// Cache IXamlDiagnostics2 to avoid QI on every hover move and tracking tick.
static IXamlDiagnostics2_Abi* g_diag2Cache = nullptr;
static IXamlDiagnostics*      g_diag2Owner = nullptr;

static IXamlDiagnostics2_Abi* CachedDiag2(IXamlDiagnostics* diag)
{
    if (!diag) return nullptr;
    if (g_diag2Owner == diag) return g_diag2Cache;
    if (g_diag2Cache) { g_diag2Cache->Release(); g_diag2Cache = nullptr; }
    IXamlDiagnostics2_Abi* d2 = nullptr;
    if (FAILED(diag->QueryInterface(__uuidof(IXamlDiagnostics2_Abi), reinterpret_cast<void**>(&d2)))) d2 = nullptr;
    g_diag2Cache = d2; g_diag2Owner = diag;
    DevToolsOverlayLog(L"overlay.diag2 QI cached hasDiag2=%d", (int)(d2 != nullptr));
    return g_diag2Cache;
}

// Log the resolved window/island chain at pick commit to diagnose coordinate-origin errors.
static void LogPickOrigin()
{
    HWND hwnd = FindAppWindow();
    if (!hwnd) { DevToolsOverlayLog(L"overlay.pick.origin no app window resolved"); return; }
    HWND island = FindContentIsland(hwnd);
    wchar_t topCls[128] = L"", islCls[128] = L"";
    GetClassNameW(hwnd, topCls, 128);
    if (island) GetClassNameW(island, islCls, 128);
    POINT o{ 0, 0 }; ClientToScreen(island ? island : hwnd, &o);
    RECT rc{}; GetClientRect(island ? island : hwnd, &rc);
    UINT dpi = GetDpiForWindow(hwnd); if (!dpi) dpi = 96;
    DevToolsOverlayLog(L"overlay.pick.origin top=%p(%s) island=%p(%s) sameAsTop=%d originScreen=%d,%d client=%dx%d dpi=%u",
        hwnd, topCls, island, islCls, (int)(island == hwnd), o.x, o.y,
        (int)(rc.right - rc.left), (int)(rc.bottom - rc.top), dpi);
}
// Active surface resolved from the overlay's XamlRoot. Extent, coordinate conversion and name must describe
// that same root.
static DevToolsSurface g_activeSurface;
static DevToolsSurface g_toolbarSurface;
static DevToolsSelectionHostFn g_selectionHost = nullptr;
void DevToolsOverlay_SetSelectionHostBridge(DevToolsSelectionHostFn fn) { g_selectionHost = fn; }
const DevToolsSurface& DevToolsOverlay_ToolbarSurface() { return g_toolbarSurface; }
static std::vector<DevToolsSurface> g_allSurfaces;

const DevToolsSurface& DevToolsOverlay_ActiveSurface() { return g_activeSurface; }
const std::vector<DevToolsSurface>& DevToolsOverlay_AllSurfaces() { return g_allSurfaces; }

// The tap owns the census, so it -- not the overlay -- knows which handles are the tops of the live trees.
static DevToolsSurfaceRootsFn g_surfaceRoots = nullptr;
void DevToolsOverlay_SetSurfaceRootsReader(DevToolsSurfaceRootsFn fn) { g_surfaceRoots = fn; }

// `anchor` is a live element on the surface to make active; passing 0 keeps the current active root if it is
// still among the resolved surfaces. on the hover path: the per-root coordinate converter is cached inside
// DevToolsSurface, so a pointer move costs a map lookup rather than a re-resolve.
static void HostPruneLayers(const std::vector<DevToolsSurface>& live);
bool DevToolsOverlay_RefreshSurfaces(IXamlDiagnostics* diag, InstanceHandle anchor)
{
    if (!diag) return false;
    std::vector<InstanceHandle> roots;
    if (g_surfaceRoots) roots = g_surfaceRoots();
// The anchor is always a candidate: on a runtime where the census has not caught up, it is the one handle we
// know is live and on the surface being asked about.
    if (anchor) roots.insert(roots.begin(), anchor);
    const size_t candidateCount = roots.size();
    g_allSurfaces = DevToolsSurface_ResolveAll(diag, roots);
    HostPruneLayers(g_allSurfaces);

    unsigned long long wanted = g_activeSurface.xamlRootKey;
    if (anchor) {
        DevToolsSurface probe;
        wanted = DevToolsSurface_Resolve(diag, anchor, &probe) ? probe.xamlRootKey : 0ULL;
    }

    const DevToolsSurface* chosen = nullptr;
    for (const auto& s : g_allSurfaces) if (s.xamlRootKey == wanted) { chosen = &s; break; }
    if (!chosen) { g_activeSurface = DevToolsSurface{}; return false; }

    const bool changed = (g_activeSurface.xamlRootKey != chosen->xamlRootKey);
    g_activeSurface = *chosen;
    DevToolsOverlayLog(L"overlay.surfaces n=%u of %u candidate root(s) active='%s' root=%llu extent=%.0fx%.0f "
                  L"screen=%d,%d,%d,%d converter=%d changed=%d",
                  (unsigned)g_allSurfaces.size(), (unsigned)candidateCount, g_activeSurface.displayName.c_str(),
                  (unsigned long long)g_activeSurface.rootHandle, g_activeSurface.contentW, g_activeSurface.contentH,
                  (int)g_activeSurface.contentRect.left, (int)g_activeSurface.contentRect.top,
                  (int)g_activeSurface.contentRect.right, (int)g_activeSurface.contentRect.bottom,
                  (int)g_activeSurface.hasConverter, (int)changed);
    return true;
}

// Which resolved surface a SCREEN point is in. Where app windows overlap, the one in front wins: this process's
// top-level windows are walked in z-order (skipping windows that are not a surface's host, such as popup hosts),
// so a point over a second window never resolves to the window it covers. Islands sharing one window fall back to
// containment.
static const DevToolsSurface* SurfaceAtScreenPoint(int screenX, int screenY)
{
    const POINT point{ screenX, screenY };
    const DWORD self = GetCurrentProcessId();
    for (HWND window = GetTopWindow(nullptr); window; window = GetWindow(window, GW_HWNDNEXT)) {
        DWORD owner = 0;
        RECT bounds{};
        if (!GetWindowThreadProcessId(window, &owner) || owner != self || !IsWindowVisible(window) || IsIconic(window) ||
            !GetWindowRect(window, &bounds) || !PtInRect(&bounds, point)) continue;
        for (const auto& s : g_allSurfaces)
            if (s.hostHwnd == window && DevToolsSurface_ContainsScreenPoint(s, screenX, screenY)) return &s;
    }
    for (const auto& s : g_allSurfaces)
        if (DevToolsSurface_ContainsScreenPoint(s, screenX, screenY)) return &s;
    return nullptr;
}

const DevToolsSurface* DevToolsOverlay_SurfaceAtScreenPoint(int screenX, int screenY) { return SurfaceAtScreenPoint(screenX, screenY); }
static DevToolsSurfaceSwitchFn g_surfaceSwitch = nullptr;
void DevToolsOverlay_SetSurfaceSwitch(DevToolsSurfaceSwitchFn fn) { g_surfaceSwitch = fn; }

// Canvas extent comes from the active XamlRoot, the same coordinate space used for picking.
static bool GetClientSizeDips(int* wDip, int* hDip)
{
    const DevToolsSurface& s = DevToolsOverlay_ActiveSurface();
    if (s.Valid() && s.contentW > 0.0f && s.contentH > 0.0f) {
        *wDip = (int)s.contentW;
        *hDip = (int)s.contentH;
        return (*wDip > 0 && *hDip > 0);
    }
// No surface resolved yet (pre-attach, or a runtime with no IXamlRoot2).
    HWND hwnd = nullptr, island = nullptr;
    ResolveAppWindowCached(&hwnd, &island);
    if (!hwnd) return false;
    HWND content = island ? island : hwnd;
    RECT rc{}; if (!GetClientRect(content, &rc)) return false;
    UINT dpi = GetDpiForWindow(hwnd); if (!dpi) dpi = 96;
    *wDip = MulDiv(rc.right - rc.left, 96, (int)dpi);
    *hDip = MulDiv(rc.bottom - rc.top, 96, (int)dpi);
    return (*wDip > 0 && *hDip > 0);
}

static bool GetMonitorWorkAreaDips(DevToolsSelection::Rect* area)
{
    if (!area) return false;
// The work area is expressed RELATIVE TO THE SURFACE'S CONTENT ORIGIN, because that is the space the toolbar is
// positioned in. Taking the origin from the active surface's screen rect keeps it on the same root as the
// Canvas it bounds; taking it from a searched window is how the pill left the screen.
    // Work area coordinates are relative to the surface content origin, the toolbar's positioning space.
    const DevToolsSurface& s = DevToolsOverlay_ActiveSurface();
    HWND hwnd = nullptr, island = nullptr;
    POINT origin{ 0, 0 };
    UINT dpi = 96;
    if (s.Valid() && s.hostHwnd && IsWindow(s.hostHwnd) &&
        s.contentRect.right > s.contentRect.left && s.contentW > 0.0f) {
        hwnd = s.hostHwnd;
        origin.x = s.contentRect.left;
        origin.y = s.contentRect.top;
// The surface's own physical-per-DIP ratio, so a per-monitor-DPI island is not measured with its host window's
// scale.
        const int physW = s.contentRect.right - s.contentRect.left;
        const int derived = (int)(96.0f * (float)physW / s.contentW + 0.5f);
        dpi = (UINT)(derived > 1 ? derived : 1);
    } else {
        ResolveAppWindowCached(&hwnd, &island);
        if (!hwnd) return false;
        if (!ClientToScreen(island ? island : hwnd, &origin)) return false;
        dpi = GetDpiForWindow(hwnd); if (!dpi) dpi = 96;
    }

    MONITORINFO monitor{ sizeof(monitor) };
    if (!GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), &monitor)) return false;

    area->left = MulDiv(monitor.rcWork.left - origin.x, 96, (int)dpi);
    area->top = MulDiv(monitor.rcWork.top - origin.y, 96, (int)dpi);
    area->right = MulDiv(monitor.rcWork.right - origin.x, 96, (int)dpi);
    area->bottom = MulDiv(monitor.rcWork.bottom - origin.y, 96, (int)dpi);
    return area->right > area->left && area->bottom > area->top;
}

// System metrics and WM_NCHITTEST do not reliably describe WinUI's InputNonClientPointerSource drag regions.
// AppWindowTitleBar supplies WinUI caption insets; system metrics do not describe custom drag regions.
static double MeasureTitleBarInsetDip()
{
    HWND hwnd = g_toolbarSurface.hostHwnd;
    if (!hwnd) return 0.0;

// Activated out of the app's OWN Windows App SDK, the same way the overlay reaches XamlReader.
    static IInspectable* s_awStatics = nullptr;
    if (!s_awStatics) {
        const wchar_t* cls = L"Microsoft.UI.Windowing.AppWindow";
        HSTRING clsId = nullptr;
        if (FAILED(WindowsCreateString(cls, (UINT32)wcslen(cls), &clsId))) return 0.0;
        HRESULT hrF = RoGetActivationFactory(clsId, DevToolsIid<DevToolsW::IAppWindowStatics>(),
                                             reinterpret_cast<void**>(&s_awStatics));
        WindowsDeleteString(clsId);
        if (FAILED(hrF) || !s_awStatics) {
            s_awStatics = nullptr;
            DevToolsOverlayLog(L"overlay.titlebar AppWindow factory hr=0x%08X -- inset 0", hrF);
            return 0.0;
        }
    }

// WindowId.Value == HWND for a desktop window. That is asserted rather than assumed: the AppWindow we get back
// must agree it IS this window, otherwise we are reading some other window's caption.
    // Assert WindowId.Value matches HWND before reading AppWindow titlebar data.
    IInspectable* appWindow = nullptr;
    HRESULT hr = DevToolsAppWindowGetFromWindowId(s_awStatics, (unsigned __int64)(ULONG_PTR)hwnd, &appWindow);
    if (FAILED(hr) || !appWindow) {
        DevToolsOverlayLog(L"overlay.titlebar GetFromWindowId(hwnd=%p) hr=0x%08X appWindow=%p -- inset 0",
                      hwnd, hr, appWindow);
        if (appWindow) appWindow->Release();
        return 0.0;
    }
    unsigned __int64 gotId = 0;
    const HRESULT hrId = DevToolsAppWindowGetId(appWindow, &gotId);

    IInspectable* titleBar = nullptr;
    hr = DevToolsAppWindowGetTitleBar(appWindow, &titleBar);
    appWindow->Release();
    if (FAILED(hr) || !titleBar) {
        DevToolsOverlayLog(L"overlay.titlebar get_TitleBar hr=0x%08X titleBar=%p -- inset 0", hr, titleBar);
        if (titleBar) titleBar->Release();
        return 0.0;
    }

    bool extends = false; int heightPx = 0, leftPx = 0, rightPx = 0;
    const HRESULT hrE = DevToolsTitleBarGetExtendsContentIntoTitleBar(titleBar, &extends);
    const HRESULT hrH = DevToolsTitleBarGetHeight(titleBar, &heightPx);
    DevToolsTitleBarGetLeftInset(titleBar, &leftPx);
    DevToolsTitleBarGetRightInset(titleBar, &rightPx);
    titleBar->Release();

    UINT dpi = GetDpiForWindow(hwnd); if (!dpi) dpi = 96;
    const bool sameWindow = SUCCEEDED(hrId) && gotId == (unsigned __int64)(ULONG_PTR)hwnd;
// Every value is checked, never just the HRESULT: this ABI has a recorded habit of returning S_OK having done
// nothing, and an S_OK with heightPx == 0 is indistinguishable from success until it is looked at.
    const double dip = (sameWindow && SUCCEEDED(hrE) && extends && SUCCEEDED(hrH) && heightPx > 0)
                       ? (double)MulDiv(heightPx, 96, (int)dpi) : 0.0;
    DevToolsOverlayLog(L"overlay.titlebar hwnd=%p id=0x%llX same=%d extends=%d(hr=0x%08X) height=%dpx(hr=0x%08X) "
                  L"insets=%d,%dpx dpi=%u -> inset %.0f dip",
                  hwnd, gotId, (int)sameWindow, (int)extends, hrE, heightPx, hrH, leftPx, rightPx, dpi, dip);
    return dip;
}

// Cached because the corner anchors are read on every pointer move of a toolbar drag, and this is several
// cross-apartment property gets. Refreshed wherever the client size is (a DPI change or a monitor move arrives
// as a resize), so it can never be stale relative to the geometry it is added to.
static double g_titleBarInsetDip = 0.0;

// Markers sit on the active surface; the measured inset belongs to the toolbar's window.
static int PinTitleBarInsetDip()
{
    return g_activeSurface.xamlRootKey == g_toolbarSurface.xamlRootKey ? (int)ceil(g_titleBarInsetDip) : 0;
}


static std::wstring XmlEscapeLines(const std::wstring& s) { return DevToolsXmlEscape(s, /*encodeWhitespace*/ true); }

// Substitute $TOKEN$ placeholders in an embedded.xaml fragment (DevToolsXaml::*) with runtime values, so the
// markup stays a static, reviewable file while geometry, palette colors, glyphs and text are filled in at load
// time. Callers pre-escape any element/user text (XmlEscape) and pass palette-derived colors or tool-owned.
static std::wstring DevToolsXamlSubst(const wchar_t* tmpl,
                                 std::initializer_list<std::pair<const wchar_t*, std::wstring>> subs)
{
    std::wstring s(tmpl);
    for (const auto& kv : subs) {
        const std::wstring token = std::wstring(L"$") + kv.first + L"$";
        for (size_t pos = s.find(token); pos != std::wstring::npos; pos = s.find(token, pos + kv.second.size()))
            s.replace(pos, token.size(), kv.second);
    }
    return s;
}

// The toolbar requests the opposite of the app's effective theme and uses standard WinUI Acrylic/semantic
// brushes; every Button still opts out of app styles (Style="{x:Null}"). The markup lives in toolbar.xaml and
// is filled in via $TOKEN$ substitution.
static const int kChipW         = 104;
static const int kChipH         = 28;
// Bar width must clear the StackPanel content so the last item keeps the same inset as the first. Content =
// dragHandle(24)+margin(2) + Pick(30)+Comments(34)+Inspect(30)+Layout(30)+Theme(30) + sep(1)+margin(6) +
// Pin(24) + 7*Spacing(3) = 232 DIP; chrome = 2*Border(1) + 2*Padding(6) = 14 DIP.
static const int kBarW          = 252;
static const int kBarH          = 36;
static const int kSnapW         = 112;
static const int kSnapH         = 32;

using namespace DevToolsToolbar;
static int g_corner = CornerBR;

// The overlay therefore paints from its own fixed, fully opaque palette. Under High Contrast the palette is
// derived from system colours (COLOR_WINDOW / COLOR_WINDOWTEXT / COLOR_HIGHLIGHT) so it honours the user's
// contrast pairing.
struct DevToolsPalette {
    std::wstring surface;
    std::wstring surfaceAlt;
    std::wstring border;
// This is a LIGHT hairline instead: it reads as an edge against a dark app, and against a light app it is
// unobtrusive because there the dark body is already doing the separating. It is a pre-BLENDED opaque colour
// (light-over-surface computed here, not an alpha), so it can never let app content show through the way /did.
    std::wstring rim;
    std::wstring textPrimary;
    std::wstring textSecondary;
    std::wstring textTertiary;
    std::wstring accent;
    std::wstring accentText;
    std::wstring liveRest;
    std::wstring caution;
    bool highContrast = false;
};

static std::wstring DevToolsSysColorHex(int idx)
{
    DWORD c = GetSysColor(idx);
    wchar_t buf[10];
    _snwprintf_s(buf, _countof(buf), _TRUNCATE, L"#FF%02X%02X%02X", GetRValue(c), GetGValue(c), GetBValue(c));
    return std::wstring(buf);
}

static const DevToolsPalette& DevToolsBuildPalette()
{
    static const DevToolsPalette pal = []() -> DevToolsPalette {
        DevToolsPalette p;
        HIGHCONTRAST hc; hc.cbSize = sizeof(hc); hc.dwFlags = 0; hc.lpszDefaultScheme = nullptr;
        p.highContrast = SystemParametersInfoW(SPI_GETHIGHCONTRAST, sizeof(hc), &hc, 0)
                         && (hc.dwFlags & HCF_HIGHCONTRASTON) != 0;
        if (p.highContrast) {
            const std::wstring win  = DevToolsSysColorHex(COLOR_WINDOW);
            const std::wstring text = DevToolsSysColorHex(COLOR_WINDOWTEXT);
            const std::wstring hl   = DevToolsSysColorHex(COLOR_HIGHLIGHT);
            const std::wstring hlt  = DevToolsSysColorHex(COLOR_HIGHLIGHTTEXT);
            p.surface = win; p.surfaceAlt = win; p.border = text; p.rim = text;
            p.textPrimary = text; p.textSecondary = text; p.textTertiary = text;
            p.accent = hl; p.accentText = hlt; p.liveRest = text; p.caution = hl;
        } else {
// The overlay's own WARM-NEUTRAL palette (Nikola's round-3 pick) -- fixed, fully opaque, app-theme independent.
            p.surface       = L"#FF262220";
            p.surfaceAlt    = L"#FF2F2A25";
            p.border        = L"#FF443C34";
// white at ~30% blended over the surface -- an OPAQUE #6F6A66, computed once here so nothing translucent ever
// reaches the tree.
            p.rim           = L"#FF6F6A66";
            p.textPrimary   = L"#FFECE5DC";
            p.textSecondary = L"#FFB3A89B";
            p.textTertiary  = L"#FF8A8078";
            p.accent        = L"#FF4CC2FF";
            p.accentText    = L"#FF06222E";
            p.liveRest      = L"#FFC1A889";
            p.caution       = L"#FFFFC83D";
        }
        return p;
    }();
    return pal;
}

static const DevToolsPalette& DevToolsInverseAcrylicPalette()
{
    static const DevToolsPalette pal = {
        L"{ThemeResource AcrylicBackgroundFillColorDefaultBrush}",
        L"{ThemeResource ControlFillColorSecondaryBrush}",
        L"{ThemeResource DividerStrokeColorDefaultBrush}",
        L"{ThemeResource SurfaceStrokeColorFlyoutBrush}",
        L"{ThemeResource TextFillColorPrimaryBrush}",
        L"{ThemeResource TextFillColorSecondaryBrush}",
        L"{ThemeResource TextFillColorTertiaryBrush}",
        L"{ThemeResource AccentFillColorDefaultBrush}",
        L"{ThemeResource TextOnAccentFillColorPrimaryBrush}",
        L"{ThemeResource TextFillColorSecondaryBrush}",
        L"{ThemeResource SystemFillColorCautionBrush}",
        false,
    };
    return pal;
}

static std::wstring DevToolsInverseAppRequestedTheme();

// Build the corner-parked toolbar markup from the toolbar.xaml resource. Place the collapsed pill at the
// persisted corner, park the action bar and the snap targets, and apply the same inverse-themed WinUI Acrylic
// treatment as the comment flyout.
static std::wstring BuildPrototypeMarkup(int wDip, int hDip)
{
    const DevToolsPalette& pal = DevToolsInverseAcrylicPalette();
    double chipX = 0.0, chipY = 0.0;
    Anchor(g_corner, Client{ (double)wDip, (double)hDip, g_titleBarInsetDip }, (double)kChipW, (double)kChipH,
           &chipX, &chipY);
    return DevToolsXamlSubst(DevToolsXaml::Toolbar, {
        { L"W",          std::to_wstring(wDip) },
        { L"H",          std::to_wstring(hDip) },
        { L"CHIPX",      std::to_wstring((int)chipX) },
        { L"CHIPY",      std::to_wstring((int)chipY) },
        { L"CHIPH",      std::to_wstring(kChipH) },
        { L"BARW",       std::to_wstring(kBarW) },
        { L"BARH",       std::to_wstring(kBarH) },
        { L"SNAPW",      std::to_wstring(kSnapW) },
        { L"SNAPH",      std::to_wstring(kSnapH) },
        { L"TOOLBARTHEME", DevToolsInverseAppRequestedTheme() },
        { L"SURFACE",    pal.surface },
        { L"BORDER",     pal.border },
        { L"RIM",        pal.rim },
        { L"TEXT",       pal.textPrimary },
        { L"TEXTDIM",    pal.textSecondary },
        { L"TEXTTERTIARY", pal.textTertiary },
        { L"ACCENT",     pal.accent },
        { L"ACCENTTEXT", pal.accentText },
        { L"LIVE",       pal.liveRest },
        { L"PICKENABLED", DevToolsTrust_MutationEnabled() ? std::wstring(L"True") : std::wstring(L"False") },
        { L"THEMEENABLED", DevToolsTrust_MutationEnabled() ? std::wstring(L"True") : std::wstring(L"False") },
        { L"SNAPFILL",   std::wstring(L"{ThemeResource AccentFillColorSecondaryBrush}") },
    });
}


static std::wstring BuildAdornerMarkup(int x, int y, int w, int h)
{
    return DevToolsXamlSubst(DevToolsXaml::Adorner, {
        { L"X", std::to_wstring(x) }, { L"Y", std::to_wstring(y) },
        { L"W", std::to_wstring(w) }, { L"H", std::to_wstring(h) } });
}

// Record the WinRT runtime class name of an IInspectable (opt-in log) so the anchor probe captures exactly what
// the framework's UI layer / hit-test hands back across WinUI versions.
static void LogRuntimeClass(const wchar_t* tag, IInspectable* insp)
{
    if (!insp) { DevToolsOverlayLog(L"overlay.%s runtimeclass=<null>", tag); return; }
    HSTRING cls = nullptr;
    if (SUCCEEDED(insp->GetRuntimeClassName(&cls)) && cls)
    {
        UINT32 len = 0; const wchar_t* raw = WindowsGetStringRawBuffer(cls, &len);
        DevToolsOverlayLog(L"overlay.%s runtimeclass=%s", tag, raw ? raw : L"?");
        WindowsDeleteString(cls);
    }
    else DevToolsOverlayLog(L"overlay.%s runtimeclass=<unavailable>", tag);
}

// The diagnostics AddChild refuses a XamlReader-produced child.
static HRESULT AttachViaPanelChildren(IInspectable* panelInsp, IInspectable* overlayUi, const wchar_t* tag)
{
    if (!panelInsp || !overlayUi) return E_INVALIDARG;
    IInspectable* panel = nullptr;
    HRESULT hr = panelInsp->QueryInterface(DevToolsIid<DevToolsXC::IPanel>(), reinterpret_cast<void**>(&panel));
    DevToolsOverlayLog(L"overlay.%s QI IPanel hr=0x%08X panel=%p", tag, hr, panel);
    if (FAILED(hr) || !panel) return FAILED(hr) ? hr : E_NOINTERFACE;
    IInspectable* childrenInsp = nullptr;
    hr = DevToolsPanelGetChildren(panel, &childrenInsp);
    DevToolsOverlayLog(L"overlay.%s get_Children hr=0x%08X children=%p", tag, hr, childrenInsp);
    if (SUCCEEDED(hr) && childrenInsp)
    {
        IInspectable* vec = nullptr;
        HRESULT hrV = childrenInsp->QueryInterface(DevToolsIid<DevToolsUIElementVector>(), reinterpret_cast<void**>(&vec));
        DevToolsOverlayLog(L"overlay.%s QI IVector<UIElement> hr=0x%08X vec=%p", tag, hrV, vec);
        if (SUCCEEDED(hrV) && vec)
        {
            unsigned int size = 0; DevToolsVecGetSize(vec, &size);
// A Popup is zero-SIZE but not zero-POSITION: a panel that lays children out positionally still gives it a
// slot, and the Popup's child renders relative to that slot. Z-order is not a consideration either way: the
// Popup's child is presented on the framework's popup layer above all app content regardless of sibling order.
            hr = DevToolsVecInsertAt(vec, 0, overlayUi);
            DevToolsOverlayLog(L"overlay.%s InsertAt(at=0 of %u) hr=0x%08X", tag, size, hr);
            vec->Release();
        }
        else hr = hrV;
        childrenInsp->Release();
    }
    panel->Release();
    return hr;
}

// Toolbar events run on the app UI thread and may access its live tree directly.
static HRESULT LoadMarkup(const std::wstring& xaml, IInspectable** out);

// Context the click handlers need, cached at build time (all valid only on the UI thread).
static IXamlDiagnostics*   g_pickDiag = nullptr;
static InstanceHandle      g_pickRoot = 0;
static DevToolsSeedFn           g_pickSeed = nullptr;
static DevToolsIsOverlayFn      g_pickIsOverlay = nullptr;
static DevToolsPickedFn         g_pickedSink = nullptr;
static DevToolsPickTargetFn     g_pickTarget = nullptr;
static DevToolsPickRawFn        g_pickRaw = nullptr;
static DevToolsFocusedFn        g_focusedSink = nullptr;
static DevToolsAnchorOfFn       g_anchorOf = nullptr;
static DevToolsResolveAnchorFn  g_resolveAnchor = nullptr;
static DevToolsWireOfFn         g_wireOf = nullptr;
static IInspectable* g_canvasStatics = nullptr;
static IInspectable*         g_popup = nullptr;
// The same Popup as a UIElement.
// Keep the Popup UIElement so surface switches reparent the existing overlay instead of rebuilding.
static IInspectable* g_popupUi = nullptr;
static winrt::com_ptr<IInspectable> g_selectionPopup, g_selectionPopupUi, g_selectionCanvas, g_selectionPanel;
static winrt::com_ptr<IInspectable> g_highlightPopup, g_highlightPopupUi, g_highlightCanvas, g_highlightPanel;
static winrt::com_ptr<IInspectable> g_highlightLayoutSource;
static winrt::event_token g_highlightLayoutToken{};
static winrt::com_ptr<IUnknown> g_selectionRootIdentity;
static winrt::com_ptr<IInspectable> g_selectionLayoutSource, g_selectionResizeSource;
static winrt::event_token g_selectionLayoutToken{};
static __int64 g_selectionResizeToken = 0;
static IInspectable*       g_pickCatcher = nullptr;
static DevToolsPickModeFn       g_pickModeSink = nullptr;
static DWORD               g_overlayUiThreadId = 0;

static DevToolsOverlayStateChangedFn g_stateChangedSink = nullptr;
static bool             g_toolbarVisible = false;
static InstanceHandle   g_lastHighlightHandle = 0;

// Cheap and safe to call defensively (a no-op sink before DevToolsTap registers one, e.g.
static void NotifyStateChanged()
{
    if (g_stateChangedSink) g_stateChangedSink();
}

void DevToolsOverlay_SetStateChangedSink(DevToolsOverlayStateChangedFn sink) { g_stateChangedSink = sink; }
bool DevToolsOverlay_IsPickArmed() { return g_pickCatcher != nullptr; }
// A built toolbar is present only while its XamlRoot still hosts content.
static bool RailRootStillHosting();
bool DevToolsOverlay_IsToolbarVisible() { return g_toolbarVisible && RailRootStillHosting(); }
bool DevToolsOverlay_IsToolbarRequested() { return g_toolbarVisible; }
InstanceHandle DevToolsOverlay_GetHighlightHandle() { return g_lastHighlightHandle; }

static DevToolsCardReadFn       g_cardRead = nullptr;
// Set by an in-app pick just before its panel opens: only that path moves focus into the comment box.
static bool g_selFocusCommentOnOpen = false;
static DevToolsCardReadInputFn  g_cardReadInput = nullptr;
static DevToolsCardScopeFn      g_cardScope = nullptr;
static DevToolsCardFreezeFn     g_cardFreeze = nullptr;
static DevToolsSourceReadFn     g_srcRead = nullptr;
static DevToolsDeclaredFn       g_isDeclared = nullptr;

static const GUID IID_RoutedEventHandler_Del =
    { 0xDAE23D85, 0x69CA, 0x5BDF, { 0x80, 0x5B, 0x61, 0x61, 0xA3, 0xA2, 0x15, 0xCC } };
static const GUID IID_DragDeltaEventHandler_Del =
    { 0x49CA91D0, 0xFC43, 0x56B1, { 0x98, 0xBD, 0x68, 0xE2, 0xE1, 0xE2, 0x4D, 0xE9 } };
// Microsoft.UI.Xaml.Input.PointerEventHandler = A48A71E1-8BB4-5597-9E31-903A3F6A04FB. Same Invoke(sender,args)
// ABI.
// PointerMoved uses the PointerEventHandler IID; moving over the catcher should not require ButtonBase.
static const GUID IID_PointerEventHandler_Del =
    { 0xA48A71E1, 0x8BB4, 0x5597, { 0x9E, 0x31, 0x90, 0x3A, 0x3F, 0x6A, 0x04, 0xFB } };
static const GUID IID_ViewChangingHandler_Del = DevToolsIid<DevToolsViewChangingHandler>();
static const GUID IID_ViewChangedHandler_Del  = DevToolsIid<DevToolsViewChangedHandler>();
// Microsoft.UI.Xaml.Controls.SelectionChangedEventHandler (delegate). Same Invoke(sender,args)->HRESULT ABI
// shape as RoutedEventHandler; a CardEnumSelectSink answers this IID so a card ComboBox commits its selected
// enum member on change.
static const GUID IID_SelectionChanged_Del =
    { 0xA232390D, 0x0E34, 0x595E, { 0x89, 0x31, 0xFA, 0x92, 0x8A, 0x99, 0x09, 0xF4 } };
// Microsoft.UI.Xaml.SizeChangedEventHandler = 8D7B1A58-14C6-51C9-892C-9FCCE368E77D. Same Invoke(sender,args)
// ABI shape as RoutedEventHandler; a sink answers this IID so the app panel's SizeChanged re-anchors the
// top-right toolbar on window resize ( -- otherwise a resize visibly detaches it from the corner).
static const GUID IID_SizeChanged_Del =
    { 0x8D7B1A58, 0x14C6, 0x51C9, { 0x89, 0x2C, 0x9F, 0xCC, 0xE3, 0x68, 0xE7, 0x7D } };
// Microsoft.UI.Xaml.Input.KeyEventHandler = DB68E7CC-9A2B-527D-9989-25284DACCC03. Same Invoke(sender,args) ABI
// shape (args = IKeyRoutedEventArgs); a sink answers this IID so the panel handles Esc (collapse) / Enter /
// Ctrl+Enter (commit) for the morph panel's keyboard/a11y contract.
static const GUID IID_KeyEventHandler_Del =
    { 0xDB68E7CC, 0x9A2B, 0x527D, { 0x99, 0x89, 0x25, 0x28, 0x4D, 0xAC, 0xCC, 0x03 } };

// One agile COM delegate implementation for BOTH handler types (RoutedEventHandler and DragDeltaEventHandler
// share the IUnknown + Invoke(void*, void*) ABI shape; only the QI'd IID differs). Static, process-lifetime
// (AddRef/Release are no-ops via DevToolsSinkBase), agile like the existing UiHandler.
// The sink is agile/free-threaded through DevToolsSinkBase, matching the other event sinks.
struct EventSink : DevToolsSinkBase
{
    void (*cb)(void* sender, void* args) = nullptr;
    void Bind(const GUID* delegateIid, void (*callback)(void*, void*)) {
        cb = callback;
        InitSink(*delegateIid, /*gen*/ 0);
    }
    virtual HRESULT STDMETHODCALLTYPE Invoke(void* sender, void* args) { if (cb) cb(sender, args); return S_OK; }
};
static EventSink g_catcherSink;
static EventSink g_catcherMovedSink;
static EventSink g_catcherPressedSink;
static EventSink g_catcherWheelSink;
static EventSink g_catcherCanceledSink;
static EventSink g_catcherCaptureLostSink;
static int       g_wheelForwards = 0;
