// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

#include <unknwn.h>
#include <objidl.h>   // IMarshal / IAgileObject
#include <combaseapi.h>

// A COM event sink. References are counted, but a sink is freed only when it was created with DevToolsNewSink:
// static sinks and sinks created with plain `new` are never deleted. Only QueryInterface, AddRef, Release and the
// derived Invoke may be virtual: WinUI calls Invoke through vtable slot 3.
struct DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE QueryInterface(REFIID r, void** p)
    {
        if (!p) return E_POINTER;
        if (r == IID_IUnknown || (iid && r == *iid) || (iid2 && r == *iid2) || r == __uuidof(IAgileObject)) {
            *p = this;
            AddRef();
            return S_OK;
        }
        // The aggregated marshaler adds its reference through this object.
        if (r == __uuidof(IMarshal) && ftm) return ftm->QueryInterface(r, p);
        *p = nullptr; return E_NOINTERFACE;
    }
    virtual ULONG STDMETHODCALLTYPE AddRef() { return static_cast<ULONG>(InterlockedIncrement(&refs)); }
    virtual ULONG STDMETHODCALLTYPE Release()
    {
        const ULONG left = static_cast<ULONG>(InterlockedDecrement(&refs));
        if (!left && destroy) {
            IUnknown* marshaler = ftm;
            ftm = nullptr;
            destroy(this);
            if (marshaler) marshaler->Release();
        }
        return left;
    }

    void InitSink(const GUID& delegateIid, unsigned currentGen, const GUID* alsoIid = nullptr)
    {
        iid = &delegateIid;
        iid2 = alsoIid;
        gen = currentGen;
        if (!ftm) CoCreateFreeThreadedMarshaler(reinterpret_cast<IUnknown*>(this), &ftm);
    }

    IUnknown*   ftm  = nullptr;
    const GUID* iid  = nullptr;
    const GUID* iid2 = nullptr;
    unsigned    gen  = 0; // generation this sink was wired in; compared against its owner's counter in Invoke
    long        refs = 1; // the creator's reference
    void (*destroy)(DevToolsSinkBase*) = nullptr; // set by DevToolsNewSink; null keeps the sink for the process
};

// Creates a sink that deletes itself when its last reference is released. The caller owns one reference.
template <class T>
T* DevToolsNewSink()
{
    T* sink = new T();
    sink->destroy = [](DevToolsSinkBase* base) { delete static_cast<T*>(base); };
    return sink;
}
