// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include <windows.h>
#include <inspectable.h>
#include <wrl/client.h>
#include <atomic>
#include "DevToolsBindingRelay.h"

#ifdef WINAPP_DEVTOOLS_BINDING_FIXTURE_DLL
#define FIXTURE extern "C" __declspec(dllexport)
#else
#define FIXTURE extern "C"
#endif

using Microsoft::WRL::ComPtr;
#ifdef WINAPP_DEVTOOLS_BINDING_FAULT_INJECTION
static thread_local bool failPacketCopy = false;
bool DevToolsBindingTestFailPacketCopy() { return failPacketCopy; }
extern "C" void BindingFixtureFailPacketCopy(bool fail) { failPacketCopy = fail; }
#endif

struct BindingFixture : IInspectable {
    std::atomic<ULONG> refs{1};
    std::atomic<ULONG> late{0};
    ComPtr<IUnknown> marshaler;
    ComPtr<IStream> packet;
    DWORD publisher = 0;
    ULONG revocations = 0;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** out) override {
        *out = nullptr;
        if (iid == IID_IMarshal) return marshaler->QueryInterface(iid, out);
        if (iid != IID_IUnknown && iid != __uuidof(IInspectable) && iid != __uuidof(IAgileObject))
            return E_NOINTERFACE;
        *out = static_cast<IInspectable*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { if (!refs) ++late; return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override { if (!refs) { ++late; return 0; } return --refs; }
    HRESULT STDMETHODCALLTYPE GetIids(ULONG* count, IID** values) override { *count=0; *values=nullptr; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetRuntimeClassName(HSTRING* value) override { *value=nullptr; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetTrustLevel(TrustLevel* value) override { *value=BaseTrust; return S_OK; }
};
FIXTURE HRESULT BindingFixtureCreate(BindingFixture** output)
{
    *output = nullptr;
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr)) return hr;
    auto state = new BindingFixture();
    hr = CoCreateFreeThreadedMarshaler(state, &state->marshaler);
    if (FAILED(hr)) delete state; else *output = state;
    CoUninitialize();
    return hr;
}
FIXTURE HRESULT BindingFixtureOwner(BindingFixture* state, IAgileReference** owner)
{
    return RoGetAgileReference(AGILEREFERENCE_DEFAULT, __uuidof(IInspectable), state, owner);
}
FIXTURE HRESULT BindingFixtureDestroy(BindingFixture* state)
{
    if (state->refs != 1 || state->late || state->packet) return E_UNEXPECTED;
    state->Release();
    delete state;
    return S_OK;
}
FIXTURE void BindingFixtureCounts(BindingFixture* state, ULONG* refs, ULONG* late, ULONG* revocations)
{
    *refs = state->refs; *late = state->late; *revocations = state->revocations;
}
FIXTURE HRESULT BindingFixturePublish(BindingFixture* state, BYTE* bytes, ULONG capacity, ULONG* length)
{
    *length = 0;
    if (state->packet) return E_UNEXPECTED;
    HRESULT hr = CreateStreamOnHGlobal(nullptr, TRUE, &state->packet);
    if (FAILED(hr)) return hr;
    hr = CoMarshalInterface(state->packet.Get(), __uuidof(IInspectable), state, MSHCTX_LOCAL, nullptr, MSHLFLAGS_TABLESTRONG);
    if (FAILED(hr)) { state->packet.Reset(); return hr; }
    state->publisher = GetCurrentThreadId();
    STATSTG stat{};
    hr = state->packet->Stat(&stat, STATFLAG_NONAME);
    if (FAILED(hr)) return hr;
    if (stat.cbSize.QuadPart > capacity) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    LARGE_INTEGER zero{};
    hr = state->packet->Seek(zero, STREAM_SEEK_SET, nullptr);
    if (FAILED(hr)) return hr;
    return state->packet->Read(bytes, static_cast<ULONG>(stat.cbSize.QuadPart), length);
}
FIXTURE HRESULT BindingFixtureRevoke(BindingFixture* state)
{
    if (!state->packet || state->publisher != GetCurrentThreadId()) return RPC_E_WRONG_THREAD;
    LARGE_INTEGER zero{};
    HRESULT hr = state->packet->Seek(zero, STREAM_SEEK_SET, nullptr);
    if (SUCCEEDED(hr)) hr = CoReleaseMarshalData(state->packet.Get());
    if (SUCCEEDED(hr)) { ++state->revocations; state->packet.Reset(); }
    return hr;
}
FIXTURE HRESULT BindingFixtureIdentity(BindingFixture* state, IAgileReference* owner)
{
    ComPtr<IInspectable> resolved;
    HRESULT hr = owner->Resolve(__uuidof(IInspectable), &resolved);
    if (FAILED(hr)) return hr;
    ComPtr<IUnknown> original, projected;
    hr = state->QueryInterface(IID_PPV_ARGS(&original));
    if (FAILED(hr)) return hr;
    hr = resolved.As(&projected);
    return FAILED(hr) ? hr : original.Get() == projected.Get() ? S_OK : E_UNEXPECTED;
}
FIXTURE HRESULT BindingFixtureRelay(BindingFixture* state, const wchar_t* op, wchar_t* reply, ULONG capacity)
{
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr)) return hr;
    {
        ComPtr<IAgileReference> owner;
        hr = BindingFixtureOwner(state, &owner);
        if (SUCCEEDED(hr)) hr = BindingFixtureIdentity(state, owner.Get());
        if (SUCCEEDED(hr)) {
            std::wstring json;
            bool answered = DevToolsBindingRelay_Binding(op, owner.Get(), L"Text", L"source", &json);
            if (json.size() >= capacity) hr = HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
            else { wcscpy_s(reply, capacity, json.c_str()); hr = answered ? S_OK : S_FALSE; }
        }
    }
    CoUninitialize();
    return hr;
}
