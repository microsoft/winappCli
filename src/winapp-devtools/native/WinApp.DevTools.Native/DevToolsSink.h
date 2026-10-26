// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

#include <unknwn.h>
#include <objidl.h>   // IMarshal / IAgileObject
#include <combaseapi.h>

struct DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE QueryInterface(REFIID r, void** p)
    {
        if (!p) return E_POINTER;
        if (r == IID_IUnknown || (iid && r == *iid) || (iid2 && r == *iid2)) { *p = this; return S_OK; }
        if (r == __uuidof(IAgileObject)) { *p = this; return S_OK; }
        if (r == __uuidof(IMarshal) && ftm) return ftm->QueryInterface(r, p);
        *p = nullptr; return E_NOINTERFACE;
    }
    virtual ULONG STDMETHODCALLTYPE AddRef() { return 2; }
    virtual ULONG STDMETHODCALLTYPE Release() { return 1; }

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
};
