// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "../native/WinApp.DevTools.Native/DevToolsProjected.h"
#include <iostream>

struct SurfaceRoot : IInspectable
{
    ULONG refs = 1;
    bool supportsIsland = true;
    bool hasIsland = true;
    HRESULT islandResult = S_OK;
    unsigned converterReads = 0;

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override
    {
        *value = nullptr;
        if (iid != IID_IUnknown && iid != IID_IInspectable &&
            iid != DevToolsIid<DevToolsX::IXamlRoot3>() &&
            !(supportsIsland && iid == DevToolsIid<DevToolsX::IXamlRoot4>())) return E_NOINTERFACE;
        *value = static_cast<IInspectable*>(this);
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override { return --refs; }
    HRESULT STDMETHODCALLTYPE GetIids(ULONG* count, IID** ids) override
    { *count = 0; *ids = nullptr; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetRuntimeClassName(HSTRING* name) override
    { *name = nullptr; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetTrustLevel(TrustLevel* level) override
    { *level = BaseTrust; return S_OK; }
};

static HRESULT SurfaceSize(void*, float* width, float* height)
{ *width = 800; *height = 600; return S_OK; }
static HRESULT SurfaceVisible(void*, bool* visible)
{ *visible = true; return S_OK; }
static HRESULT SurfaceIsland(void* value, IInspectable** island)
{
    auto root = static_cast<SurfaceRoot*>(value);
    *island = root->hasIsland ? root : nullptr;
    if (*island) (*island)->AddRef();
    return root->islandResult;
}
static HRESULT SurfaceIslandId(void*, unsigned __int64* id)
{ *id = 7; return S_OK; }
static HRESULT SurfaceConverter(void* value, IInspectable** converter)
{
    ++static_cast<SurfaceRoot*>(value)->converterReads;
    *converter = nullptr;
    return S_OK;
}

#define DevToolsXamlRootGetSize SurfaceSize
#define DevToolsXamlRootGetIsHostVisible SurfaceVisible
#define DevToolsXamlRootGetContentIsland SurfaceIsland
#define DevToolsContentIslandGetId SurfaceIslandId
#define DevToolsXamlRootGetCoordinateConverter SurfaceConverter
#include "../native/WinApp.DevTools.Native/DevToolsSurface.cpp"

void DevToolsOverlayLog(const wchar_t*, ...) {}

int main()
{
    unsigned checks = 0, failures = 0;
    auto check = [&](bool condition, const char* name) {
        ++checks;
        if (!condition) ++failures;
        std::cout << (condition ? "PASS " : "FAIL ") << name << '\n';
    };
    DevToolsSurface surface;
    SurfaceRoot live;
    check(FillFromRoot(&live, 1, &surface) && live.converterReads == 1 && live.refs == 1,
        "live island permits converter lookup without leaking references");
    SurfaceRoot closed;
    closed.hasIsland = false;
    check(!FillFromRoot(&closed, 2, &surface) && closed.converterReads == 0 && closed.refs == 1,
        "closed island is refused before the unsafe converter getter");
    SurfaceRoot failed;
    failed.islandResult = E_FAIL;
    check(!FillFromRoot(&failed, 3, &surface) && failed.converterReads == 0 && failed.refs == 1,
        "failed island lookup is refused and releases any returned object");
    SurfaceRoot legacy;
    legacy.supportsIsland = false;
    check(FillFromRoot(&legacy, 4, &surface) && legacy.converterReads == 1 && legacy.refs == 1,
        "runtime without IXamlRoot4 retains its existing converter path");
    std::cout << "surface lifetime: " << checks << " checks, " << failures << " failures\n";
    return failures ? 1 : 0;
}
