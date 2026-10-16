// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include <windows.h>
#include <xamlOM.h>

#include "DevToolsProjected.h"  // C++/WinRT projected ABI -- the compiler resolves every WinUI vtable slot
#include "DevToolsBinding.h"

namespace {

// Take ownership of an HSTRING returned through an ABI out-param and copy it out.
std::wstring TakeHString(void* raw)
{
    HSTRING hs = reinterpret_cast<HSTRING>(raw);
    if (!hs) return L"";
    UINT32 len = 0;
    const wchar_t* p = WindowsGetStringRawBuffer(hs, &len);
    std::wstring s(p ? p : L"", len);
    WindowsDeleteString(hs);
    return s;
}

const wchar_t* ModeLabel(int32_t mode)
{
    switch (mode) {
        case static_cast<int32_t>(DevToolsXD::BindingMode::OneWay): return L"OneWay";
        case static_cast<int32_t>(DevToolsXD::BindingMode::OneTime): return L"OneTime";
        case static_cast<int32_t>(DevToolsXD::BindingMode::TwoWay): return L"TwoWay";
        default: return L"";
    }
}

} // namespace

std::wstring DevToolsBinding_Describe(IXamlDiagnostics* diag, unsigned long long bindingValueHandle)
{
    if (!diag || !bindingValueHandle) return L"";

    IInspectable* insp = nullptr;
    if (FAILED(diag->GetIInspectableFromHandle((InstanceHandle)bindingValueHandle, &insp)) || !insp) return L"";

    void* ib = nullptr;
    if (FAILED(insp->QueryInterface(DevToolsIid<DevToolsXD::IBinding>(), &ib)) || !ib) { insp->Release(); return L""; }

    std::wstring path, elementName, mode, relativeSource;
    bool hasConverter = false, hasSource = false;

    void* pathObj = nullptr;
    if (SUCCEEDED(DevToolsAbi<DevToolsXD::IBinding>(ib)->get_Path(&pathObj)) && pathObj) {
        void* ipp = nullptr;
        if (SUCCEEDED(static_cast<IInspectable*>(pathObj)->QueryInterface(DevToolsIid<DevToolsX::IPropertyPath>(), &ipp)) && ipp) {
            void* s = nullptr;
            if (SUCCEEDED(DevToolsAbi<DevToolsX::IPropertyPath>(ipp)->get_Path(&s))) path = TakeHString(s);
            static_cast<IInspectable*>(ipp)->Release();
        }
        static_cast<IInspectable*>(pathObj)->Release();
    }

    int32_t m = 0;
    if (SUCCEEDED(DevToolsAbi<DevToolsXD::IBinding>(ib)->get_Mode(&m))) mode = ModeLabel(m);

    void* en = nullptr;
    const HRESULT hrElement = DevToolsAbi<DevToolsXD::IBinding>(ib)->get_ElementName(&en);
    if (SUCCEEDED(hrElement)) elementName = TakeHString(en);

    void* relative = nullptr;
    const HRESULT hrRelative = DevToolsAbi<DevToolsXD::IBinding>(ib)->get_RelativeSource(&relative);
    if (SUCCEEDED(hrRelative) && relative) {
        // Presence matters even when its mode cannot be read: never imply DataContext.
        relativeSource = L"...";
        void* rs = nullptr;
        if (SUCCEEDED(static_cast<IInspectable*>(relative)->QueryInterface(
                DevToolsIid<DevToolsXD::IRelativeSource>(), &rs)) && rs) {
            int32_t relativeMode = 0;
            if (SUCCEEDED(DevToolsAbi<DevToolsXD::IRelativeSource>(rs)->get_Mode(&relativeMode))) {
                if (relativeMode == static_cast<int32_t>(DevToolsXD::RelativeSourceMode::Self))
                    relativeSource = L"{RelativeSource Self}";
                else if (relativeMode == static_cast<int32_t>(DevToolsXD::RelativeSourceMode::TemplatedParent))
                    relativeSource = L"{RelativeSource TemplatedParent}";
            }
            static_cast<IInspectable*>(rs)->Release();
        }
        static_cast<IInspectable*>(relative)->Release();
    }

    void* conv = nullptr;
    if (SUCCEEDED(DevToolsAbi<DevToolsXD::IBinding>(ib)->get_Converter(&conv)) && conv) {
        hasConverter = true;
        static_cast<IInspectable*>(conv)->Release();
    }

    void* src = nullptr;
    const HRESULT hrSource = DevToolsAbi<DevToolsXD::IBinding>(ib)->get_Source(&src);
    if (SUCCEEDED(hrSource) && src) {
        hasSource = true;
        static_cast<IInspectable*>(src)->Release();
    }

    static_cast<IInspectable*>(ib)->Release();
    insp->Release();
    if (FAILED(hrElement) || FAILED(hrRelative) || FAILED(hrSource)) return L"";

    std::wstring out = L"{Binding";
    if (!path.empty()) out += L" " + path;
    auto part = [&out](const std::wstring& p) { out += (out == L"{Binding") ? L" " + p : L", " + p; };
    if (!elementName.empty()) part(L"ElementName=" + elementName);
    if (hasSource)            part(L"Source=...");
    if (!relativeSource.empty()) part(L"RelativeSource=" + relativeSource);
    if (!mode.empty())        part(L"Mode=" + mode);
    if (hasConverter)         part(L"Converter=...");
    out += L"}";
    return out;
}
