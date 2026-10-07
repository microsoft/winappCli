// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Minimal XAML diagnostics TAP used by scripts/devtools-perf to separate WinUI's own diagnostics cost
// from DevTools' engine. It does nothing but subscribe to visual-tree changes and count them, and it
// can unsubscribe and resubscribe on command.
//
// Initialization data (semicolon-separated, from InitializeXamlDiagnosticsEx):
//   dir=<path>   control directory (required)
//   advise=0|1   subscribe on load (default 1)
//   cb2=0|1      expose IVisualTreeServiceCallback2 (element-state notifications; default 1)
// Commands: write one of "advise", "unadvise", "cycle", "release", "status" to <dir>\probe-<pid>.cmd. The tap writes
// <dir>\probe-<pid>.json with counters and the last advise/unadvise duration after each command.
#include <windows.h>
#include <unknwn.h>
#include <ocidl.h>
#include <xamlom.h>
#include <atomic>
#include <string>
#include <cstdio>

// The probe answers to the DevTools CLSID so winapp's own injector (`winapp.exe __devtools-inject`) can load it.
static const CLSID CLSID_DevToolsTap = { 0x7C3D6A11, 0x0000, 0x4F00, { 0x9A,0x00,0x5A,0x4F,0x4B,0x45,0x00,0x01 } };

static std::atomic<long long> g_adds{0}, g_removes{0}, g_states{0};
static bool g_cb2 = true;
static std::wstring g_dir;

struct Callback : IVisualTreeServiceCallback2
{
    IUnknown* ftm = nullptr;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID r, void** p) override {
        if (!p) return E_POINTER;
        if (r == IID_IUnknown || r == __uuidof(IVisualTreeServiceCallback) || r == __uuidof(IAgileObject)) {
            *p = static_cast<IVisualTreeServiceCallback*>(this); return S_OK;
        }
        if (g_cb2 && r == __uuidof(IVisualTreeServiceCallback2)) { *p = static_cast<IVisualTreeServiceCallback2*>(this); return S_OK; }
        if (r == __uuidof(IMarshal) && ftm) return ftm->QueryInterface(r, p);
        *p = nullptr; return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return 2; }
    ULONG STDMETHODCALLTYPE Release() override { return 1; }
    HRESULT STDMETHODCALLTYPE OnVisualTreeChange(ParentChildRelation, VisualElement, VisualMutationType t) override {
        (t == Add ? g_adds : g_removes).fetch_add(1, std::memory_order_relaxed);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnElementStateChanged(InstanceHandle, VisualElementState, LPCWSTR) override {
        g_states.fetch_add(1, std::memory_order_relaxed);
        return S_OK;
    }
};
static Callback g_cb;
// The IXamlDiagnostics site, held so "release" can drop the last reference this TAP owns.
static std::atomic<IUnknown*> g_site{nullptr};

struct WorkerArgs { DWORD cookie; bool advise; };

static IGlobalInterfaceTable* Git() {
    IGlobalInterfaceTable* git = nullptr;
    CoCreateInstance(CLSID_StdGlobalInterfaceTable, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&git));
    return git;
}

static double NowMs() {
    LARGE_INTEGER f, c; QueryPerformanceFrequency(&f); QueryPerformanceCounter(&c);
    return c.QuadPart * 1000.0 / f.QuadPart;
}

static void WriteStatus(bool advised, HRESULT lastHr, double adviseMs, double unadviseMs, long long adviseAdds, long long seq) {
    wchar_t path[MAX_PATH * 2];
    swprintf_s(path, L"%s\\probe-%lu.json", g_dir.c_str(), GetCurrentProcessId());
    std::wstring tmp = std::wstring(path) + L".tmp";
    FILE* f = nullptr;
    if (_wfopen_s(&f, tmp.c_str(), L"w") || !f) return;
    fprintf(f, "{\"seq\":%lld,\"advised\":%s,\"hr\":%ld,\"adds\":%lld,\"removes\":%lld,\"states\":%lld,"
               "\"lastAdviseMs\":%.1f,\"lastAdviseAdds\":%lld,\"lastUnadviseMs\":%.1f,\"cb2\":%s}",
        seq, advised ? "true" : "false", (long)lastHr, g_adds.load(), g_removes.load(), g_states.load(),
        adviseMs, adviseAdds, unadviseMs, g_cb2 ? "true" : "false");
    fclose(f);
    MoveFileExW(tmp.c_str(), path, MOVEFILE_REPLACE_EXISTING);
}

static DWORD WINAPI Worker(LPVOID p)
{
    WorkerArgs a = *static_cast<WorkerArgs*>(p); delete static_cast<WorkerArgs*>(p);
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    IVisualTreeService3* vts = nullptr;
    if (auto git = Git()) {
        git->GetInterfaceFromGlobal(a.cookie, __uuidof(IVisualTreeService3), reinterpret_cast<void**>(&vts));
        git->RevokeInterfaceFromGlobal(a.cookie);
        git->Release();
    }
    if (!vts) return 1;
    CoCreateFreeThreadedMarshaler(static_cast<IVisualTreeServiceCallback*>(&g_cb), &g_cb.ftm);
    bool advised = false;
    HRESULT hr = S_OK;
    double adviseMs = 0, unadviseMs = 0;
    long long adviseAdds = 0, seq = 0;
    auto advise = [&]() {
        if (advised) return;
        const long long before = g_adds.load();
        const double t = NowMs();
        hr = vts->AdviseVisualTreeChange(&g_cb);
        adviseMs = NowMs() - t;
        adviseAdds = g_adds.load() - before;
        advised = SUCCEEDED(hr);
    };
    auto unadvise = [&]() {
        if (!advised) return;
        const double t = NowMs();
        hr = vts->UnadviseVisualTreeChange(&g_cb);
        unadviseMs = NowMs() - t;
        advised = false;
    };
    if (a.advise) advise();
    WriteStatus(advised, hr, adviseMs, unadviseMs, adviseAdds, seq);

    wchar_t cmdPath[MAX_PATH * 2];
    swprintf_s(cmdPath, L"%s\\probe-%lu.cmd", g_dir.c_str(), GetCurrentProcessId());
    for (;;) {
        Sleep(50);
        FILE* f = nullptr;
        if (_wfopen_s(&f, cmdPath, L"r") || !f) continue;
        char cmd[64] = {};
        fgets(cmd, sizeof(cmd), f);
        fclose(f);
        DeleteFileW(cmdPath);
        std::string c(cmd);
        while (!c.empty() && (c.back() == '\n' || c.back() == '\r' || c.back() == ' ')) c.pop_back();
        if (c == "advise") advise();
        else if (c == "unadvise") unadvise();
        else if (c == "cycle") { unadvise(); advise(); }
        else if (c == "release") {
            // Drop every reference this TAP holds to XAML diagnostics, to see whether WinUI tears its
            // diagnostics state down. The worker ends; a later injection starts a new one.
            unadvise();
            vts->Release();
            vts = nullptr;
            if (IUnknown* site = g_site.exchange(nullptr)) site->Release();
            WriteStatus(false, hr, adviseMs, unadviseMs, adviseAdds, ++seq);
            return 0;
        }
        WriteStatus(advised, hr, adviseMs, unadviseMs, adviseAdds, ++seq);
    }
}

static std::wstring Field(const std::wstring& data, const wchar_t* name) {
    const std::wstring key = std::wstring(name) + L"=";
    size_t start = 0;
    while (start <= data.size()) {
        size_t end = data.find(L';', start);
        if (end == std::wstring::npos) end = data.size();
        if (data.compare(start, key.size(), key) == 0) return data.substr(start + key.size(), end - start - key.size());
        start = end + 1;
    }
    return L"";
}

struct Tap : IObjectWithSite
{
    std::atomic<long> ref{1};
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID r, void** p) override {
        if (!p) return E_POINTER;
        if (r == IID_IUnknown || r == IID_IObjectWithSite) { *p = static_cast<IObjectWithSite*>(this); AddRef(); return S_OK; }
        *p = nullptr; return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++ref; }
    ULONG STDMETHODCALLTYPE Release() override { long r = --ref; if (r == 0) delete this; return r; }
    HRESULT STDMETHODCALLTYPE SetSite(IUnknown* s) override {
        if (IUnknown* old = g_site.exchange(nullptr)) old->Release();
        if (!s) return S_OK;
        s->AddRef(); g_site = s;
        IXamlDiagnostics* diag = nullptr;
        if (FAILED(s->QueryInterface(__uuidof(IXamlDiagnostics), reinterpret_cast<void**>(&diag))) || !diag) return S_OK;
        BSTR init = nullptr;
        std::wstring data;
        if (SUCCEEDED(diag->GetInitializationData(&init)) && init) { data.assign(init, SysStringLen(init)); SysFreeString(init); }
        g_dir = Field(data, L"dir");
        g_cb2 = Field(data, L"cb2") != L"0";
        const bool adviseNow = Field(data, L"advise") != L"0";
        IVisualTreeService3* vts = nullptr;
        if (SUCCEEDED(diag->QueryInterface(__uuidof(IVisualTreeService3), reinterpret_cast<void**>(&vts))) && vts) {
            if (auto git = Git()) {
                DWORD cookie = 0;
                git->RegisterInterfaceInGlobal(vts, __uuidof(IVisualTreeService3), &cookie);
                git->Release();
                if (HANDLE th = CreateThread(nullptr, 0, Worker, new WorkerArgs{ cookie, adviseNow }, 0, nullptr)) CloseHandle(th);
            }
            vts->Release();
        }
        diag->Release();
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetSite(REFIID r, void** p) override { if (IUnknown* s = g_site.load()) return s->QueryInterface(r, p); if (p) *p = nullptr; return E_FAIL; }
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
    if (rclsid == CLSID_DevToolsTap) return g_factory.QueryInterface(riid, ppv);
    if (ppv) *ppv = nullptr; return CLASS_E_CLASSNOTAVAILABLE;
}
extern "C" HRESULT __stdcall DllCanUnloadNow() { return S_FALSE; }
