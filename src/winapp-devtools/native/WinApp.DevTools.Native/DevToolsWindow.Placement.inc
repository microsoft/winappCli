// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsWindow.cpp; included into that translation unit, not compiled on its own.

// Minimum size for the inspector window. The window had NO minimum: it could be dragged down to 200px
// wide, at which point property values crop away entirely while the alignment ComboBox keeps its full 188px --
// the same layout-priority inversion is about, just relocated. Below roughly this width the two panes
// cannot both carry readable content, so the window refuses to go there rather than silently losing data.
// Enforced with a WM_GETMINMAXINFO subclass because WinUI's Window exposes no minimum-size property.
static const int kMinWindowWidthDip  = DevToolsInspectorPlacement::kMinWidthDip;
static const int kMinWindowHeightDip = DevToolsInspectorPlacement::kMinHeightDip;

static void ResetAppVisibleModes(HWND hwnd);
static void LiveUnwatch();
static void SetInspectorIcon(HWND hwnd);

// Store the cloaked HWND so every placement exit path can uncloak; a missed uncloak hides the inspector forever.
static HWND g_placeCloakHwnd = nullptr;

// Where the user last left the inspector in this app run, so closing and reopening it keeps its size and spot.
// Kept in memory only: a new app run opens the inspector at its default size beside the app.
static bool g_inspectorRemembered = false;
static DevToolsInspectorPlacement::Remembered g_inspectorLast{};

static RECT ToRECT(const DevToolsInspectorPlacement::Rect& r) { return RECT{ r.left, r.top, r.right, r.bottom }; }
static DevToolsInspectorPlacement::Rect FromRECT(const RECT& r) { return { r.left, r.top, r.right, r.bottom }; }

static void RememberInspectorPlacement(HWND hwnd)
{
    // A window closed while placement still has it cloaked is at a transient rect the user never saw.
    if (!hwnd || hwnd == g_placeCloakHwnd) return;
    RECT r{};
    RECT frame{};
    if (IsIconic(hwnd) || IsZoomed(hwnd)) {
        // Keep the size the window has when restored, not its minimized or maximized one.
        WINDOWPLACEMENT wp{ sizeof(WINDOWPLACEMENT) };
        if (!GetWindowPlacement(hwnd, &wp)) return;
        r = wp.rcNormalPosition;
        // rcNormalPosition is in workspace coordinates, which exclude a taskbar on the monitor's top or left.
        // MonitorFromWindow answers with the monitor of the restored rect, even for a minimized window.
        MONITORINFO mi{ sizeof(MONITORINFO) };
        if (HMONITOR mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST); mon && GetMonitorInfo(mon, &mi))
            OffsetRect(&r, mi.rcWork.left - mi.rcMonitor.left, mi.rcWork.top - mi.rcMonitor.top);
    } else {
        if (!GetWindowRect(hwnd, &r)) return;
        RECT visible{};
        if (SUCCEEDED(DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &visible, sizeof(visible))))
            frame = RECT{ (std::max)(0L, visible.left - r.left), (std::max)(0L, visible.top - r.top),
                          (std::max)(0L, r.right - visible.right), (std::max)(0L, r.bottom - visible.bottom) };
    }
    if (r.right <= r.left || r.bottom <= r.top) return;
    UINT dpi = GetDpiForWindow(hwnd);
    if (dpi == 0) dpi = 96;
    g_inspectorLast = DevToolsInspectorPlacement::Remembered{ FromRECT(r), dpi, FromRECT(frame) };
    g_inspectorRemembered = true;
    DevToolsOverlayLog(L"route1.place remember (%d,%d %dx%d) dpi=%u", r.left, r.top, r.right - r.left, r.bottom - r.top, dpi);
}

static LRESULT CALLBACK Route1MinSizeProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp,
                                          UINT_PTR /*id*/, DWORD_PTR generation)
{
    if (msg == WM_CLOSE && generation == g_route1Gen && g_route1WindowInsp) {
        RememberInspectorPlacement(hwnd);
        ReleaseInspectorCaptionTheme();
        if (!g_flushingEditor) FlushFocusedEditor(false);
        RetirePaneEditors();
        RetireKindEditors();
        RetireRowExpandSinks();
        // Closed by the user: what the window drew on the app goes with it, as when it is hidden.
        ResetAppVisibleModes(hwnd);
        LiveUnwatch();
    }
    if (msg == WM_GETMINMAXINFO) {
        UINT dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        auto* mmi = reinterpret_cast<MINMAXINFO*>(lp);
        mmi->ptMinTrackSize.x = MulDiv(kMinWindowWidthDip,  (int)dpi, 96);
        mmi->ptMinTrackSize.y = MulDiv(kMinWindowHeightDip, (int)dpi, 96);
        return 0;
    }
    if (msg == WM_NCDESTROY) RemoveWindowSubclass(hwnd, Route1MinSizeProc, 1);
    if (msg == WM_DPICHANGED || msg == WM_SETTINGCHANGE) {
        const LRESULT result = DefSubclassProc(hwnd, msg, wp, lp);
        SetInspectorIcon(hwnd);
        return result;
    }
    return DefSubclassProc(hwnd, msg, wp, lp);
}

// IWindowNative is classic COM interop; use it before g_route1WindowInsp exists so first Activate can be cloaked.
struct __declspec(uuid("EECDBF0E-BAE9-4CB6-A68E-9598E1CB57BB")) IWindowNative_Abi : ::IUnknown
{
    virtual HRESULT STDMETHODCALLTYPE get_WindowHandle(HWND* hwnd) = 0;
};

static HWND HwndFromWindowInsp(IInspectable* insp)
{
    if (!insp) return nullptr;
    IWindowNative_Abi* wn = nullptr;
    if (FAILED(insp->QueryInterface(__uuidof(IWindowNative_Abi), reinterpret_cast<void**>(&wn))) || !wn) return nullptr;
    HWND hwnd = nullptr;
    wn->get_WindowHandle(&hwnd);
    wn->Release();
    return hwnd;
}

// Recover the inspector Window's own top-level HWND via IWindowNative.
// Returns null if the window isn't built yet or the QI fails.
static HWND Route1WindowHwnd()
{
    return HwndFromWindowInsp(g_route1WindowInsp);
}

// Cloak/uncloak a window via DWM. A cloaked window still composes and lays out (unlike SW_HIDE), so revealing
// it shows a fully-rendered frame with no blank flash. Best-effort: any failure just leaves the window visible.
// Returns the DWM HRESULT so callers can avoid recording a cloak that DWM rejected.
static HRESULT SetPlacementCloak(HWND hwnd, bool cloak)
{
    if (!hwnd) return E_INVALIDARG;
    BOOL v = cloak ? TRUE : FALSE;
    return DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, &v, static_cast<DWORD>(sizeof(v)));
}

static void UncloakPlacement()
{
    if (g_placeCloakHwnd) {
        SetPlacementCloak(g_placeCloakHwnd, false);
        DevToolsOverlayLog(L"route1.place uncloak hwnd=%p", g_placeCloakHwnd);
        g_placeCloakHwnd = nullptr;
    }
}

// Is the existing inspector Window still a live OS window (not closed by the user)? Recovers its HWND via
// IWindowNative and probes IsWindow. Returns false if we can't confirm it's alive (so we rebuild).
static bool Route1WindowAlive()
{
    HWND hwnd = Route1WindowHwnd();
    return hwnd != nullptr && IsWindow(hwnd);
}

struct FindMainCtx { DWORD pid; HWND exclude; HWND best; LONGLONG bestArea; };
static BOOL CALLBACK FindMainProc(HWND h, LPARAM lp)
{
    auto* c = reinterpret_cast<FindMainCtx*>(lp);
    if (h == c->exclude) return TRUE;
    DWORD wpid = 0; GetWindowThreadProcessId(h, &wpid);
    if (wpid != c->pid) return TRUE;
    if (!IsWindowVisible(h)) return TRUE;
    if (GetWindow(h, GW_OWNER) != nullptr) return TRUE;
    RECT r; if (!GetWindowRect(h, &r)) return TRUE;
    LONGLONG area = (LONGLONG)(r.right - r.left) * (r.bottom - r.top);
    if (area > c->bestArea) { c->bestArea = area; c->best = h; }
    return TRUE;
}
static HWND FindTargetMainHwnd(HWND exclude)
{
    FindMainCtx c{ GetCurrentProcessId(), exclude, nullptr, 0 };
    EnumWindows(FindMainProc, reinterpret_cast<LPARAM>(&c));
    return c.best;
}

static bool ActivateTopLevel(HWND hwnd, const wchar_t* target)
{
    if (!hwnd || !IsWindow(hwnd)) {
        DevToolsOverlayLog(L"route1.pick activate %s failed: no live HWND", target);
        return false;
    }
    if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
    const BOOL accepted = SetForegroundWindow(hwnd);
    const HWND foreground = GetForegroundWindow();
    const bool active = accepted != FALSE && foreground == hwnd;
    DevToolsOverlayLog(L"route1.pick activate %s accepted=%d foreground=%p target=%p active=%d",
                  target, (int)accepted, foreground, hwnd, (int)active);
    return active;
}

static void FocusTargetAppForPick()
{
    ActivateTopLevel(FindTargetMainHwnd(Route1WindowHwnd()), L"app");
}

static void FocusInspectorAfterPick()
{
    ActivateTopLevel(Route1WindowHwnd(), L"inspector");
}

// Placement re-arms a one-shot timer across correction passes; the timer id + proc must precede it.
// WinUI applies default window sizing on a later pump turn; WM_TIMER placement runs after that settles.
static const UINT_PTR kPlaceTimerId = 0xD70;
static VOID CALLBACK PlaceTimerProc(HWND hwnd, UINT, UINT_PTR id, DWORD);

// Place a fresh inspector, clamped onscreen and scaled by DPI: where the user last closed it in this app run, or
// beside the app the first time. Do not reposition a reused window against the user's manual move. Bounded timer
// passes correct residual offsets after WinUI's SDK-dependent placement transform; failures leave the default placement.
static int  g_placePass  = 0;
static int  g_placeSetX  = 0;
static int  g_placeSetY  = 0;
static const int kPlaceMaxPass = 8;
static void PositionInspectorBesideApp()
{
    HWND self = Route1WindowHwnd();
    if (!self) { UncloakPlacement(); g_placePass = 0; return; }

    HWND app = FindTargetMainHwnd(self);
    const DevToolsInspectorPlacement::Remembered* remembered = g_inspectorRemembered ? &g_inspectorLast : nullptr;
    // Return to the remembered spot only while a monitor still shows it; otherwise keep its size beside the app.
    HMONITOR mon = nullptr;
    if (remembered) {
        const RECT last = ToRECT(remembered->rect);
        mon = MonitorFromRect(&last, MONITOR_DEFAULTTONULL);
    }
    const bool restore = mon != nullptr;
    if (!mon) mon = MonitorFromWindow(app ? app : self, MONITOR_DEFAULTTONEAREST);

    // Size for the monitor the window lands on, so moving it there doesn't rescale it after the fact.
    UINT dpi = 0, dpiY = 0;
    if (!mon || FAILED(GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, &dpi, &dpiY)) || dpi == 0) dpi = GetDpiForWindow(self);
    if (dpi == 0) dpi = 96;

    MONITORINFO mi{ sizeof(MONITORINFO) };
    RECT work{ 0, 0, MulDiv(DevToolsInspectorPlacement::kDefaultWidthDip, (int)dpi, 96),
               MulDiv(DevToolsInspectorPlacement::kDefaultHeightDip, (int)dpi, 96) };
    if (mon && GetMonitorInfo(mon, &mi)) work = mi.rcWork;

    RECT ar{};
    const bool haveApp = app && GetWindowRect(app, &ar);
    const DevToolsInspectorPlacement::Rect appRect = FromRECT(ar);
    const RECT target = ToRECT(DevToolsInspectorPlacement::Target(
        dpi, FromRECT(work), haveApp ? &appRect : nullptr, remembered, restore));
    const int x = target.left, y = target.top;
    const int w = target.right - target.left, h = target.bottom - target.top;

    if (g_placePass == 0) {
        // Seed: send the target directly. If WinUI applies no transform this is already correct; if it does,
        // the correction passes below reel it in.
        g_placeSetX = x; g_placeSetY = y;
        SetWindowPos(self, nullptr, g_placeSetX, g_placeSetY, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
        DevToolsOverlayLog(L"route1.place seed self=%p app=%p work=(%d,%d,%d,%d) remembered=%d restore=%d target=(%d,%d %dx%d) dpi=%u",
                      self, app, work.left, work.top, work.right, work.bottom,
                      (int)(remembered != nullptr), (int)restore, x, y, w, h, dpi);
    } else {
        RECT cur{}; GetWindowRect(self, &cur);
        int rx = x - cur.left, ry = y - cur.top;
        int arx = rx < 0 ? -rx : rx, ary = ry < 0 ? -ry : ry;
        const int dw = w - (cur.right - cur.left), dh = h - (cur.bottom - cur.top);
        if (arx <= 2 && ary <= 2 && dw <= 2 && dw >= -2 && dh <= 2 && dh >= -2) {
            DevToolsOverlayLog(L"route1.place settled at (%d,%d %dx%d) after %d pass(es)",
                          cur.left, cur.top, cur.right - cur.left, cur.bottom - cur.top, g_placePass);
            UncloakPlacement();
            g_placePass = 0;
            return;
        }
        g_placeSetX += rx; g_placeSetY += ry;
        SetWindowPos(self, nullptr, g_placeSetX, g_placeSetY, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
        DevToolsOverlayLog(L"route1.place pass=%d landed=(%d,%d) target=(%d,%d) residual=(%d,%d) resend=(%d,%d)",
                      g_placePass, cur.left, cur.top, x, y, rx, ry, g_placeSetX, g_placeSetY);
    }

    // Only the timer reveals a cloaked window, so reveal it now if there is no next pass to do it.
    if (++g_placePass >= kPlaceMaxPass || !SetTimer(self, kPlaceTimerId, 60, PlaceTimerProc)) {
        UncloakPlacement();
        g_placePass = 0;
    }
}

static VOID CALLBACK PlaceTimerProc(HWND hwnd, UINT, UINT_PTR id, DWORD)
{
    KillTimer(hwnd, id);
    PositionInspectorBesideApp();
}

// The inspector is another top-level app-thread Window; close it when it alone would keep the process alive.
static const UINT_PTR kWatchTimerId = 0xD71;

struct CountAppWndCtx { DWORD pid; HWND exclude; int count; };
static BOOL CALLBACK CountAppWndProc(HWND h, LPARAM lp)
{
    auto* c = reinterpret_cast<CountAppWndCtx*>(lp);
    if (h == c->exclude) return TRUE;
    DWORD wpid = 0; GetWindowThreadProcessId(h, &wpid);
    if (wpid != c->pid) return TRUE;
    if (!IsWindowVisible(h)) return TRUE;
    if (GetWindow(h, GW_OWNER) != nullptr) return TRUE;
    c->count++;
    return TRUE;
}

static VOID CALLBACK WatchTimerProc(HWND hwnd, UINT, UINT_PTR id, DWORD)
{
    CountAppWndCtx c{ GetCurrentProcessId(), hwnd, 0 };
    EnumWindows(CountAppWndProc, reinterpret_cast<LPARAM>(&c));
    if (c.count > 0) { g_watchdogSawApp = true; SetTimer(hwnd, id, 400, WatchTimerProc); return; }
    if (!g_watchdogSawApp) { SetTimer(hwnd, id, 400, WatchTimerProc); return; }
    KillTimer(hwnd, id);
    DevToolsOverlayLog(L"route1.watchdog app has no remaining top-level windows; closing inspector so the process can exit");
    PostMessageW(hwnd, WM_CLOSE, 0, 0);
}
