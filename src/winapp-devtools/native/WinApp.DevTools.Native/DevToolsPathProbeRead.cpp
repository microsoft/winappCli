// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "DevToolsProjected.h"
#include "DevToolsPathProbe.h"

namespace {

std::wstring TakeHString(HSTRING h)
{
    UINT32 len = 0;
    const wchar_t* p = h ? WindowsGetStringRawBuffer(h, &len) : nullptr;
    std::wstring s = p ? std::wstring(p, len) : std::wstring();
    if (h) WindowsDeleteString(h);
    return s;
}

std::wstring TypeOf(::IInspectable* obj)
{
    if (!obj) return L"";
    void* cpp = nullptr;
    if (SUCCEEDED(obj->QueryInterface(DevToolsIid<DevToolsXD::ICustomPropertyProvider>(), &cpp)) && cpp) {
        HSTRING h = nullptr;
        const HRESULT hr = DevToolsCppGetTypeName(cpp, &h);
        static_cast<::IInspectable*>(cpp)->Release();
        // Check the OUT-PARAM as well as the HRESULT: this ABI has shipped S_OK-with-nothing before.
        if (SUCCEEDED(hr) && h) {
            std::wstring s = TakeHString(h);
            // Strip assembly qualification from the reported type; consumers need its name, not assembly identity.
            const size_t comma = s.find(L',');
            if (comma != std::wstring::npos) s.erase(comma);
            if (!s.empty()) return s;
        }
    }
    HSTRING rc = nullptr;
    if (SUCCEEDED(obj->GetRuntimeClassName(&rc)) && rc) return TakeHString(rc);
    return L"";
}

bool ScalarText(::IInspectable* obj, std::wstring* out)
{
    if (!obj || !out) return false;
    void* ipv = nullptr;
    if (FAILED(obj->QueryInterface(DevToolsIid<winrt::Windows::Foundation::IPropertyValue>(), &ipv)) || !ipv) return false;
    auto* p = DevToolsAbi<winrt::Windows::Foundation::IPropertyValue>(ipv);

    auto num = [](double d) {
        std::wstring s = std::to_wstring(d);
        const size_t dot = s.find(L'.');
        if (dot != std::wstring::npos) {
            const size_t last = s.find_last_not_of(L'0');
            s.erase(last == dot ? dot : last + 1);
        }
        return s;
    };

    bool got = false;
    int32_t ptype = 0;
    if (SUCCEEDED(p->get_Type(&ptype))) {
        switch (ptype) {
            case 12: { HSTRING hs = nullptr; if (SUCCEEDED(p->GetString(reinterpret_cast<void**>(&hs)))) { *out = TakeHString(hs); got = true; } break; }
            case 11: { bool b = false;    if (SUCCEEDED(p->GetBoolean(&b))) { *out = b ? L"True" : L"False"; got = true; } break; }
            case 9:  { double d = 0;      if (SUCCEEDED(p->GetDouble(&d)))  { *out = num(d); got = true; } break; }
            case 8:  { float f = 0;       if (SUCCEEDED(p->GetSingle(&f)))  { *out = num(f); got = true; } break; }
            case 4:  { int32_t i = 0;     if (SUCCEEDED(p->GetInt32(&i)))   { *out = std::to_wstring(i); got = true; } break; }
            default: break;
        }
    }
    static_cast<::IInspectable*>(ipv)->Release();
    return got;
}

} // namespace

void DevToolsPathProbe_Walk(::IInspectable* source, const std::wstring& path, DevToolsProbeResult* out)
{
    if (!out) return;
    out->path = path;

    std::vector<std::wstring> segs;
    if (!DevToolsPathProbe_SplitPath(path, &segs)) {
        out->state  = L"bad-path";
        out->reason = L"this is not a well-formed property path";
        return;
    }

    if (!source) {
        out->state  = L"no-context";
        out->reason = L"there is no object to resolve this path against";
        return;
    }

    out->against = TypeOf(source);

    ::IInspectable* current = source;
    current->AddRef();

    bool stopped = false;
    std::wstring prefix;
    for (size_t i = 0; i < segs.size(); ++i) {
        prefix = prefix.empty() ? segs[i] : prefix + L"." + segs[i];

        DevToolsProbeSegment seg;
        seg.path = prefix;

        if (stopped) {
            seg.reached = false;
            out->segments.push_back(seg);
            continue;
        }

        void* provider = nullptr;
        if (FAILED(current->QueryInterface(DevToolsIid<DevToolsXD::ICustomPropertyProvider>(), &provider)) || !provider) {
            out->state  = L"not-probeable";
            out->reason = L"this object does not expose ICustomPropertyProvider; [bindable] metadata alone "
                          L"does not make it probeable by this walker. The path cannot be "
                          L"walked past " + (i == 0 ? std::wstring(L"the start") : out->segments.back().path);
            break;
        }

        if (segs[i].find(L'[') != std::wstring::npos) {
            static_cast<::IInspectable*>(provider)->Release();
            out->state  = L"indexer-unsupported";
            out->reason = L"DevTools cannot follow an indexer like " + segs[i] +
                          L". The property before it may be perfectly fine; only the indexing step is "
                          L"unsupported here.";
            break;
        }

        HSTRING name = nullptr;
        WindowsCreateString(segs[i].c_str(), (UINT32)segs[i].size(), &name);
        ::IInspectable* prop = nullptr;
        const HRESULT hrProp = DevToolsCppGetCustomProperty(provider, name, &prop);
        WindowsDeleteString(name);
        static_cast<::IInspectable*>(provider)->Release();

        if (FAILED(hrProp) || !prop) {
            seg.found = false;
            out->segments.push_back(seg);
            stopped = true;
            continue;
        }

        ::IInspectable* value = nullptr;
        const HRESULT hrValue = DevToolsCpGetValue(prop, current, &value);
        prop->Release();

        seg.found = true;
        seg.hr    = (long)hrValue;

        if (FAILED(hrValue)) {
            out->segments.push_back(seg);
            stopped = true;
            if (value) value->Release();
            continue;
        }

        if (!value) {
            seg.isNull = true;
            out->segments.push_back(seg);
            stopped = true;
            continue;
        }

        seg.type = TypeOf(value);
        std::wstring text;
        if (ScalarText(value, &text)) seg.value = text;
        out->segments.push_back(seg);

        current->Release();
        current = value;   // ownership moves; the next segment probes against it
    }

    current->Release();
}
