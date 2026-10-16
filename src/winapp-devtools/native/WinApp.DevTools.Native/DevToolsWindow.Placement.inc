// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsWindow.cpp; included into that translation unit, not compiled on its own.

// Minimum size for the inspector window. The window had NO minimum: it could be dragged down to 200px
// wide, at which point property values crop away entirely while the alignment ComboBox keeps its full 188px --
// the same layout-priority inversion is about, just relocated. Below roughly this width the two panes
// cannot both carry readable content, so the window refuses to go there rather than silently losing data.
// Enforced with a WM_GETMINMAXINFO subclass because WinUI's Window exposes no minimum-size property.
static const int kMinWindowWidthDip  = 640;
static const int kMinWindowHeightDip = 400;

static void ResetAppVisibleModes(HWND hwnd);
static void LiveUnwatch();

static LRESULT CALLBACK Route1MinSizeProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp,
                                          UINT_PTR /*id*/, DWORD_PTR generation)
{
    if (msg == WM_CLOSE && generation == g_route1Gen && g_route1WindowInsp) {
        ReleaseInspectorCaptionTheme();
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

// Store the cloaked HWND so every placement exit path can uncloak; a missed uncloak hides the inspector forever.
static HWND g_placeCloakHwnd = nullptr;

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

// Place a fresh inspector beside the app, clamped onscreen and scaled by DPI. Do not reposition
// a reused window against the user's manual move. Bounded timer passes correct residual offsets
// after WinUI's SDK-dependent placement transform; failures leave the default placement.
static int  g_placePass  = 0;
static int  g_placeSetX  = 0;
static int  g_placeSetY  = 0;
static const int kPlaceMaxPass = 8;
static void PositionInspectorBesideApp()
{
    HWND self = Route1WindowHwnd();
    if (!self) { UncloakPlacement(); g_placePass = 0; return; }
    UINT dpi = GetDpiForWindow(self);
    if (dpi == 0) dpi = 96;
    const double s = dpi / 96.0;
    int w = (int)(780 * s);
    int h = (int)(620 * s);

    HWND app = FindTargetMainHwnd(self);
    HMONITOR mon = MonitorFromWindow(app ? app : self, MONITOR_DEFAULTTONEAREST);
    MONITORINFO mi{ sizeof(MONITORINFO) };
    RECT work{ 0, 0, w, h };
    if (mon && GetMonitorInfo(mon, &mi)) work = mi.rcWork;

    const int gap  = (int)(12 * s);
    const int edge = (int)(12 * s);

    RECT safe = work;
    if (safe.right - safe.left > w + 2 * edge && safe.bottom - safe.top > h + 2 * edge) {
        safe.left += edge; safe.top += edge; safe.right -= edge; safe.bottom -= edge;
    }

    int x, y;
    RECT ar{};
    if (app) {
        GetWindowRect(app, &ar);
        y = ar.top;
        if (ar.right + gap + w <= safe.right)      x = ar.right + gap;
        else if (ar.left - gap - w >= safe.left)   x = ar.left - gap - w;
        else                                       x = safe.right - w;
    } else {
        x = work.left + (work.right - work.left - w) / 2;
        y = work.top + (work.bottom - work.top - h) / 2;
    }
    // Clamp the TARGET fully on-screen (this is the physical rect we want to end up at). Sizes clamp against
    // the real work area; positions clamp against the inset, which collapses to the work area when it can't fit.
    if (w > work.right - work.left) w = work.right - work.left;
    if (h > work.bottom - work.top) h = work.bottom - work.top;
    if (x < safe.left) x = safe.left;
    if (y < safe.top)  y = safe.top;
    if (x + w > safe.right)  x = safe.right - w;
    if (y + h > safe.bottom) y = safe.bottom - h;
    if (x < work.left) x = work.left;
    if (y < work.top)  y = work.top;
    if (x + w > work.right)  x = work.right - w;
    if (y + h > work.bottom) y = work.bottom - h;

    if (g_placePass == 0) {
        // Seed: send the target directly. If WinUI applies no transform this is already correct; if it does,
        // the correction passes below reel it in.
        g_placeSetX = x; g_placeSetY = y;
        SetWindowPos(self, nullptr, g_placeSetX, g_placeSetY, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
        DevToolsOverlayLog(L"route1.place seed self=%p app=%p work=(%d,%d,%d,%d) safe=(%d,%d,%d,%d) gap=%d edge=%d target=(%d,%d %dx%d) dpi=%u",
                      self, app, work.left, work.top, work.right, work.bottom,
                      safe.left, safe.top, safe.right, safe.bottom, gap, edge, x, y, w, h, dpi);
    } else {
        RECT cur{}; GetWindowRect(self, &cur);
        int rx = x - cur.left, ry = y - cur.top;
        int arx = rx < 0 ? -rx : rx, ary = ry < 0 ? -ry : ry;
        if (arx <= 2 && ary <= 2) {
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

    if (++g_placePass < kPlaceMaxPass)
        SetTimer(self, kPlaceTimerId, 60, PlaceTimerProc);
    else {
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
