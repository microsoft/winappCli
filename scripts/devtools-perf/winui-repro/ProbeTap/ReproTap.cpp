// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Minimal XAML diagnostics TAP. Its SetSite takes IVisualTreeService3 and, when the initialization
// data is "advise=1", subscribes to visual-tree changes with a callback that only counts. It does
// nothing else. Initialization data "advise=0" loads the TAP without ever subscribing.
#include <windows.h>
#include <unknwn.h>
#include <ocidl.h>
#include <xamlom.h>
#include <atomic>
#include <string>

extern "C" const CLSID CLSID_ReproTap = { 0x5E1F7A20, 0x3C4B, 0x4D7E, { 0x9A, 0x61, 0x2B, 0x8C, 0x0D, 0x41, 0x7E, 0x13 } };

static std::atomic<long long> g_adds{0}, g_removes{0};

struct Callback : IVisualTreeServiceCallback2
{
    IUnknown* ftm = nullptr;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID r, void** p) override {
        if (!p) return E_POINTER;
        if (r == IID_IUnknown || r == __uuidof(IVisualTreeServiceCallback) || r == __uuidof(IAgileObject)) { *p = static_cast<IVisualTreeServiceCallback*>(this); return S_OK; }
        if (r == __uuidof(IVisualTreeServiceCallback2)) { *p = static_cast<IVisualTreeServiceCallback2*>(this); return S_OK; }
        if (r == __uuidof(IMarshal) && ftm) return ftm->QueryInterface(r, p);
        *p = nullptr; return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return 2; }
    ULONG STDMETHODCALLTYPE Release() override { return 1; }
    HRESULT STDMETHODCALLTYPE OnVisualTreeChange(ParentChildRelation, VisualElement, VisualMutationType t) override {
        (t == Add ? g_adds : g_removes).fetch_add(1, std::memory_order_relaxed);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnElementStateChanged(InstanceHandle, VisualElementState, LPCWSTR) override { return S_OK; }
};
static Callback g_cb;

// AdviseVisualTreeChange replays the live tree synchronously, so call it off the SetSite thread.
static DWORD WINAPI Subscribe(LPVOID cookie)
{
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    IGlobalInterfaceTable* git = nullptr;
    IVisualTreeService3* vts = nullptr;
    if (SUCCEEDED(CoCreateInstance(CLSID_StdGlobalInterfaceTable, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&git)))) {
        git->GetInterfaceFromGlobal((DWORD)(UINT_PTR)cookie, __uuidof(IVisualTreeService3), reinterpret_cast<void**>(&vts));
        git->RevokeInterfaceFromGlobal((DWORD)(UINT_PTR)cookie);
        git->Release();
    }
    if (!vts) return 1;
    CoCreateFreeThreadedMarshaler(static_cast<IVisualTreeServiceCallback*>(&g_cb), &g_cb.ftm);
    vts->AdviseVisualTreeChange(&g_cb);
    return 0;  // vts is intentionally kept for the life of the process, like a real tool would.
}

struct Tap : IObjectWithSite
{
    std::atomic<long> ref{1};
    IUnknown* site = nullptr;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID r, void** p) override {
        if (!p) return E_POINTER;
        if (r == IID_IUnknown || r == IID_IObjectWithSite) { *p = static_cast<IObjectWithSite*>(this); AddRef(); return S_OK; }
        *p = nullptr; return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++ref; }
    ULONG STDMETHODCALLTYPE Release() override { long r = --ref; if (r == 0) delete this; return r; }
    HRESULT STDMETHODCALLTYPE SetSite(IUnknown* s) override {
        if (site) { site->Release(); site = nullptr; }
        if (!s) return S_OK;
        s->AddRef(); site = s;
        IXamlDiagnostics* diag = nullptr;
        if (FAILED(s->QueryInterface(__uuidof(IXamlDiagnostics), reinterpret_cast<void**>(&diag)))) return S_OK;
        BSTR init = nullptr;
        std::wstring data;
        if (SUCCEEDED(diag->GetInitializationData(&init)) && init) { data.assign(init, SysStringLen(init)); SysFreeString(init); }
        IVisualTreeService3* vts = nullptr;
        if (data.find(L"advise=1") != std::wstring::npos &&
            SUCCEEDED(diag->QueryInterface(__uuidof(IVisualTreeService3), reinterpret_cast<void**>(&vts)))) {
            IGlobalInterfaceTable* git = nullptr;
            DWORD cookie = 0;
            if (SUCCEEDED(CoCreateInstance(CLSID_StdGlobalInterfaceTable, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&git)))) {
                git->RegisterInterfaceInGlobal(vts, __uuidof(IVisualTreeService3), &cookie);
                git->Release();
                if (HANDLE th = CreateThread(nullptr, 0, Subscribe, (LPVOID)(UINT_PTR)cookie, 0, nullptr)) CloseHandle(th);
            }
            vts->Release();
        }
        diag->Release();
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetSite(REFIID r, void** p) override { if (site) return site->QueryInterface(r, p); if (p) *p = nullptr; return E_FAIL; }
};

struct Factory : IClassFactory
{
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID r, void** p) override {
        if (!p) return E_POINTER;
        if (r == IID_IUnknown || r == IID_IClassFactory) { *p = static_cast<IClassFactory*>(this); return S_OK; }
        *p = nullptr; return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return 2; }
    ULONG STDMETHODCALLTYPE Release() override { return 1; }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override {
        if (outer) return CLASS_E_NOAGGREGATION;
        auto t = new (std::nothrow) Tap(); if (!t) return E_OUTOFMEMORY;
        HRESULT hr = t->QueryInterface(riid, ppv); t->Release(); return hr;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL) override { return S_OK; }
};
static Factory g_factory;

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv) {
    if (rclsid == CLSID_ReproTap) return g_factory.QueryInterface(riid, ppv);
    if (ppv) *ppv = nullptr; return CLASS_E_CLASSNOTAVAILABLE;
}
extern "C" HRESULT __stdcall DllCanUnloadNow() { return S_FALSE; }
