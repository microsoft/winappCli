// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "DevToolsSurface.h"
#include "DevToolsProjected.h"
#include "DevToolsOverlay.h"   // DevToolsOverlayLog

#include <algorithm>
#include <cmath>
#include <climits>
#include <map>

static std::map<unsigned long long, unsigned int> g_ordinals;
static unsigned int g_nextOrdinal = 1;

static unsigned int OrdinalFor(unsigned long long key)
{
    auto it = g_ordinals.find(key);
    if (it != g_ordinals.end()) return it->second;
    const unsigned int n = g_nextOrdinal++;
    g_ordinals[key] = n;
    return n;
}

unsigned int DevToolsSurface_SessionOrdinal(unsigned long long xamlRootKey)
{
    auto it = g_ordinals.find(xamlRootKey);
    return it == g_ordinals.end() ? 0u : it->second;
}

static std::map<unsigned long long, IInspectable*> g_converters;

static void RememberConverter(unsigned long long key, IInspectable* conv)
{
    if (!key) return;
    auto it = g_converters.find(key);
    if (it != g_converters.end()) {
        if (it->second == conv) return;
        if (it->second) it->second->Release();
        g_converters.erase(it);
    }
    if (conv) { conv->AddRef(); g_converters[key] = conv; }
}

static IInspectable* CachedConverter(unsigned long long key)
{
    auto it = g_converters.find(key);
    return it == g_converters.end() ? nullptr : it->second;
}

void DevToolsSurface_ForgetDeadOrdinals(const std::vector<unsigned long long>& liveKeys)
{
    for (auto it = g_ordinals.begin(); it != g_ordinals.end(); ) {
        if (std::find(liveKeys.begin(), liveKeys.end(), it->first) == liveKeys.end()) it = g_ordinals.erase(it);
        else ++it;
    }
    for (auto it = g_converters.begin(); it != g_converters.end(); ) {
        if (std::find(liveKeys.begin(), liveKeys.end(), it->first) == liveKeys.end()) {
            if (it->second) it->second->Release();
            it = g_converters.erase(it);
        } else ++it;
    }
}


static std::wstring WindowTitle(HWND h)
{
    if (!h || !IsWindow(h)) return std::wstring();
    wchar_t title[256] = L"";
    if (GetWindowTextW(h, title, 256) > 0 && title[0]) return std::wstring(title);
    wchar_t cls[128] = L"";
    GetClassNameW(h, cls, 128);
    wchar_t buf[192];
    _snwprintf_s(buf, _countof(buf), _TRUNCATE, L"%s 0x%llX", cls[0] ? cls : L"window",
                 (unsigned long long)(ULONG_PTR)h);
    return std::wstring(buf);
}

static IInspectable* XamlRootOf(IXamlDiagnostics* diag, InstanceHandle h)
{
    if (!diag || !h) return nullptr;
    IInspectable* insp = nullptr;
    if (FAILED(diag->GetIInspectableFromHandle(h, &insp)) || !insp) return nullptr;
    void* ui = nullptr;
    const HRESULT hq = insp->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui);
    insp->Release();
    if (FAILED(hq) || !ui) return nullptr;
    IInspectable* xr = nullptr;
    const HRESULT hr = DevToolsGetXamlRoot(ui, &xr);
    reinterpret_cast<IUnknown*>(ui)->Release();
    if (FAILED(hr)) { if (xr) xr->Release(); return nullptr; }
    return xr;
}

static unsigned long long ComIdentity(IInspectable* p)
{
    if (!p) return 0;
    IUnknown* unk = nullptr;
    if (FAILED(p->QueryInterface(IID_IUnknown, reinterpret_cast<void**>(&unk))) || !unk) return 0;
    const unsigned long long key = (unsigned long long)(ULONG_PTR)unk;
    unk->Release();
    return key;
}

// The converter for this root, or null on a runtime without IXamlRoot3. Caller releases.
static IInspectable* ConverterOf(IInspectable* xr)
{
    if (!xr) return nullptr;
    void* xr3 = nullptr;
    if (FAILED(xr->QueryInterface(DevToolsIid<DevToolsX::IXamlRoot3>(), &xr3)) || !xr3) return nullptr;
    IInspectable* conv = nullptr;
    const HRESULT hr = DevToolsXamlRootGetCoordinateConverter(xr3, &conv);
    reinterpret_cast<IUnknown*>(xr3)->Release();
    if (FAILED(hr)) { if (conv) conv->Release(); return nullptr; }
    return conv;   // may still be null: the getter can succeed with no converter 
}

static void FillContentRect(DevToolsSurface* s, IInspectable* conv)
{
    s->contentRect = RECT{};
    if (conv && s->contentW > 0.0f && s->contentH > 0.0f) {
        int x = 0, y = 0, w = 0, h = 0;
        if (SUCCEEDED(DevToolsConverterLocalToScreen(conv, 0.0f, 0.0f, s->contentW, s->contentH, &x, &y, &w, &h))
            && w > 0 && h > 0) {
            s->contentRect = RECT{ x, y, x + w, y + h };
            return;
        }
    }
    if (s->hostHwnd && IsWindow(s->hostHwnd)) {
        RECT rc{};
        POINT o{ 0, 0 };
        if (GetClientRect(s->hostHwnd, &rc) && ClientToScreen(s->hostHwnd, &o))
            s->contentRect = RECT{ o.x, o.y, o.x + (rc.right - rc.left), o.y + (rc.bottom - rc.top) };
    }
}

static bool FillFromRoot(IInspectable* xr, InstanceHandle rootHandle, DevToolsSurface* out)
{
    if (!xr || !out) return false;
    *out = DevToolsSurface{};
    out->rootHandle = rootHandle;
    out->xamlRootKey = ComIdentity(xr);
    if (!out->xamlRootKey) return false;

    if (FAILED(DevToolsXamlRootGetSize(xr, &out->contentW, &out->contentH))) return false;
    bool visible = false;
    if (SUCCEEDED(DevToolsXamlRootGetIsHostVisible(xr, &visible))) out->hostVisible = visible;

    // The hosting top-level window. Shared by every island in an island host, so it names the HOST.
    void* xr2 = nullptr;
    if (SUCCEEDED(xr->QueryInterface(DevToolsIid<DevToolsX::IXamlRoot2>(), &xr2)) && xr2) {
        IInspectable* env = nullptr;
        if (SUCCEEDED(DevToolsXamlRootGetContentIslandEnvironment(xr2, &env)) && env) {
            unsigned __int64 id = 0;
            if (SUCCEEDED(DevToolsContentIslandEnvironmentGetAppWindowId(env, &id)) && id)
                out->hostHwnd = (HWND)(ULONG_PTR)id;
            env->Release();
        }
        reinterpret_cast<IUnknown*>(xr2)->Release();
    }

    void* xr4 = nullptr;
    if (SUCCEEDED(xr->QueryInterface(DevToolsIid<DevToolsX::IXamlRoot4>(), &xr4)) && xr4) {
        IInspectable* island = nullptr;
        const HRESULT islandResult = DevToolsXamlRootGetContentIsland(xr4, &island);
        const bool hasIsland = SUCCEEDED(islandResult) && island;
        if (hasIsland) {
            unsigned __int64 iid = 0;
            if (SUCCEEDED(DevToolsContentIslandGetId(island, &iid))) out->islandId = iid;
        }
        if (island) island->Release();
        reinterpret_cast<IUnknown*>(xr4)->Release();
        // WinUI's converter getter dereferences the island even after its window closes.
        if (!hasIsland) return false;
    }

    IInspectable* conv = ConverterOf(xr);
    out->hasConverter = (conv != nullptr);
    FillContentRect(out, conv);
    RememberConverter(out->xamlRootKey, conv);
    if (conv) conv->Release();

    out->displayName = WindowTitle(out->hostHwnd);
    (void)OrdinalFor(out->xamlRootKey);   // assign first-seen order now, before any naming pass needs it
    return true;
}

bool DevToolsSurface_Resolve(IXamlDiagnostics* diag, InstanceHandle element, DevToolsSurface* out)
{
    if (!out) return false;
    *out = DevToolsSurface{};
    IInspectable* xr = XamlRootOf(diag, element);
    if (!xr) return false;
    const bool ok = FillFromRoot(xr, element, out);
    xr->Release();
    return ok;
}


static void ApplyNamingRules(std::vector<DevToolsSurface>& set)
{
    // Rule 3 first: several roots behind ONE window are islands, and the window title cannot tell them apart.
    std::map<HWND, std::vector<size_t>> byHost;
    for (size_t i = 0; i < set.size(); ++i) byHost[set[i].hostHwnd].push_back(i);

    for (auto& kv : byHost) {
        if (kv.second.size() < 2) continue;
        std::vector<size_t> idx = kv.second;
        std::sort(idx.begin(), idx.end(), [&](size_t a, size_t b) {
            return DevToolsSurface_SessionOrdinal(set[a].xamlRootKey) < DevToolsSurface_SessionOrdinal(set[b].xamlRootKey);
        });
        for (size_t n = 0; n < idx.size(); ++n) {
            DevToolsSurface& s = set[idx[n]];
            const std::wstring host = s.displayName.empty() ? L"host" : s.displayName;
            s.displayName = host + L" \u00B7 island " + std::to_wstring(n + 1);
        }
    }

    std::map<std::wstring, std::vector<size_t>> byName;
    for (size_t i = 0; i < set.size(); ++i) byName[set[i].displayName].push_back(i);
    for (auto& kv : byName) {
        if (kv.second.size() < 2) continue;
        std::vector<size_t> idx = kv.second;
        std::sort(idx.begin(), idx.end(), [&](size_t a, size_t b) {
            return DevToolsSurface_SessionOrdinal(set[a].xamlRootKey) < DevToolsSurface_SessionOrdinal(set[b].xamlRootKey);
        });
        for (size_t n = 1; n < idx.size(); ++n)   // the first keeps the bare title
            set[idx[n]].displayName += L" (" + std::to_wstring(n + 1) + L")";
    }

    for (auto& s : set) if (s.displayName.empty()) s.displayName = L"surface " + std::to_wstring(DevToolsSurface_SessionOrdinal(s.xamlRootKey));
}

std::vector<DevToolsSurface> DevToolsSurface_ResolveAll(IXamlDiagnostics* diag,
                                              const std::vector<InstanceHandle>& candidates)
{
    std::vector<DevToolsSurface> set;
    std::vector<unsigned long long> liveKeys;
    if (!diag) return set;

    for (InstanceHandle h : candidates) {
        IInspectable* xr = XamlRootOf(diag, h);
        if (!xr) continue;
        const unsigned long long key = ComIdentity(xr);
        bool seen = false;
        for (const auto& s : set) if (s.xamlRootKey == key) { seen = true; break; }
        if (seen) { xr->Release(); continue; }
        DevToolsSurface s;
        if (FillFromRoot(xr, h, &s)) { set.push_back(s); liveKeys.push_back(s.xamlRootKey); }
        else DevToolsOverlayLog(L"surface.resolveAll candidate %llu has a XamlRoot but no readable metrics", (unsigned long long)h);
        xr->Release();
    }

    DevToolsSurface_ForgetDeadOrdinals(liveKeys);
    std::sort(set.begin(), set.end(), [](const DevToolsSurface& a, const DevToolsSurface& b) {
        return DevToolsSurface_SessionOrdinal(a.xamlRootKey) < DevToolsSurface_SessionOrdinal(b.xamlRootKey);
    });
    ApplyNamingRules(set);
    return set;
}


bool DevToolsSurface_ScreenToLocal(const DevToolsSurface& s, int screenX, int screenY, float* localX, float* localY)
{
    if (!localX || !localY) return false;
    IInspectable* conv = s.hasConverter ? CachedConverter(s.xamlRootKey) : nullptr;
    if (conv) {
        float lx = 0.0f, ly = 0.0f;
        const HRESULT hr = DevToolsConverterScreenToLocal(conv, screenX, screenY, &lx, &ly);
        if (SUCCEEDED(hr)) {
            *localX = lx; *localY = ly;
            return true;
        }
        DevToolsOverlayLog(L"surface.screenToLocal converter FAILED hr=0x%08X key=%llu screen=%d,%d (degrading to the window origin)",
                      hr, s.xamlRootKey, screenX, screenY);
    } else if (s.hasConverter) {
        DevToolsOverlayLog(L"surface.screenToLocal no cached converter for key=%llu (degrading to the window origin)", s.xamlRootKey);
    }
    if (!s.hostHwnd || !IsWindow(s.hostHwnd)) return false;
    POINT p{ screenX, screenY };
    if (!ScreenToClient(s.hostHwnd, &p)) return false;
    UINT dpi = GetDpiForWindow(s.hostHwnd); if (!dpi) dpi = 96;
    *localX = (float)MulDiv(p.x, 96, (int)dpi);
    *localY = (float)MulDiv(p.y, 96, (int)dpi);
    return true;
}

bool DevToolsSurface_LocalToScreen(const DevToolsSurface& s, float localX, float localY, POINT* screen)
{
    if (!screen || !std::isfinite(localX) || !std::isfinite(localY)) return false;
    if (s.hasConverter) {
        auto converter = CachedConverter(s.xamlRootKey);
        if (!converter) return false;
        int x = 0, y = 0, width = 0, height = 0;
        const HRESULT hr = DevToolsConverterLocalToScreen(converter, localX, localY, 1.0f, 1.0f,
            &x, &y, &width, &height);
        if (FAILED(hr)) {
            DevToolsOverlayLog(L"surface.localToScreen converter failed hr=0x%08X key=%llu", hr, s.xamlRootKey);
            return false;
        }
        *screen = {x, y};
        return true;
    }
    if (!s.hostHwnd || !IsWindow(s.hostHwnd)) return false;
    const UINT dpi = GetDpiForWindow(s.hostHwnd);
    if (!dpi) return false;
    const double scale = dpi / 96.0;
    const double x = std::round(localX * scale), y = std::round(localY * scale);
    if (x < LONG_MIN || x > LONG_MAX || y < LONG_MIN || y > LONG_MAX) return false;
    POINT point{static_cast<LONG>(x), static_cast<LONG>(y)};
    if (!ClientToScreen(s.hostHwnd, &point)) return false;
    *screen = point;
    return true;
}

bool DevToolsSurface_ContainsScreenPoint(const DevToolsSurface& s, int screenX, int screenY)
{
    const RECT& r = s.contentRect;
    if (r.right <= r.left || r.bottom <= r.top) return false;
    POINT p{ screenX, screenY };
    return PtInRect(&r, p) != FALSE;
}
