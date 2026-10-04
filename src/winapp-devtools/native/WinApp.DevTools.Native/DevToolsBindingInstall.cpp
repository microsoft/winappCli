// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include <windows.h>

#include "DevToolsProjected.h"
#include "DevToolsBindingInstall.h"
#include "DevToolsBindingMode.h"

namespace {

void* FactoryFor(const wchar_t* cls, REFIID iid)
{
    HSTRING h = nullptr;
    if (FAILED(WindowsCreateString(cls, (UINT32)wcslen(cls), &h))) return nullptr;
    void* f = nullptr;
    const HRESULT hr = RoGetActivationFactory(h, iid, &f);
    WindowsDeleteString(h);
    return SUCCEEDED(hr) ? f : nullptr;
}

::IInspectable* MakeBinding(const std::wstring& path, const std::wstring& mode, std::wstring* err)
{
    ::IInspectable* b = DevToolsActivate(L"Microsoft.UI.Xaml.Data.Binding");
    if (!b) { *err = L"a {Binding} object could not be created in this app"; return nullptr; }

    void* ppFac = FactoryFor(L"Microsoft.UI.Xaml.PropertyPath", DevToolsIid<DevToolsX::IPropertyPathFactory>());
    if (!ppFac) { *err = L"this app's XAML runtime has no PropertyPath factory"; b->Release(); return nullptr; }
    HSTRING ph = nullptr;
    WindowsCreateString(path.c_str(), (UINT32)path.size(), &ph);
    void* pp = nullptr;
    const HRESULT hrPp = DevToolsAbi<DevToolsX::IPropertyPathFactory>(ppFac)->CreateInstance(DevToolsAbiStr(ph), &pp);
    WindowsDeleteString(ph);
    static_cast<::IInspectable*>(ppFac)->Release();
    if (FAILED(hrPp) || !pp) { *err = L"'" + path + L"' is not a usable binding path"; b->Release(); return nullptr; }

    void* ib = nullptr;
    // Check both HRESULT and out-param; this ABI can report success while returning no object.
    if (FAILED(b->QueryInterface(DevToolsIid<DevToolsXD::IBinding>(), &ib)) || !ib) {
        *err = L"the {Binding} object did not expose IBinding";
        static_cast<::IInspectable*>(pp)->Release(); b->Release(); return nullptr;
    }
    const HRESULT hrPath = DevToolsAbi<DevToolsXD::IBinding>(ib)->put_Path(pp);
    const HRESULT hrMode = DevToolsAbi<DevToolsXD::IBinding>(ib)->put_Mode(DevToolsBindingInstall_ModeOrdinal(mode));
    static_cast<::IInspectable*>(ib)->Release();
    static_cast<::IInspectable*>(pp)->Release();
    if (FAILED(hrPath) || FAILED(hrMode)) {
        *err = L"the {Binding} object would not take its path or mode";
        b->Release(); return nullptr;
    }
    return b;
}

} // namespace

bool DevToolsBindingInstall_Apply(IXamlDiagnostics* diag, IVisualTreeService3* vts3,
                             InstanceHandle elem, unsigned int propIndex,
                             const std::wstring& path, const std::wstring& mode,
                             const DevToolsBindingReadBackFn& readBack, std::wstring* outJson)
{
    std::wstring sink;
    if (!outJson) outJson = &sink;
    DevToolsBindingReadBack before, after;

    if (!diag || !vts3 || !readBack) {
        *outJson = DevToolsBindingInstall_Answer(path, mode, before, after, L"this build cannot install bindings");
        return false;
    }
    if (propIndex == 0xFFFFFFFF) {
        *outJson = DevToolsBindingInstall_Answer(path, mode, before, after,
                                            L"this property cannot be set on this element");
        return false;
    }
    if (path.empty()) {
        *outJson = DevToolsBindingInstall_Answer(path, mode, before, after, L"a {Binding} needs a path");
        return false;
    }
    if (DevToolsBindingInstall_ModeOrdinal(mode) < 0) {
        *outJson = DevToolsBindingInstall_Answer(path, mode, before, after,
                                               L"mode must be OneWay, OneTime or TwoWay");
        return false;
    }

    before = readBack();

    std::wstring err;
    ::IInspectable* binding = MakeBinding(path, mode, &err);
    if (!binding) {
        *outJson = DevToolsBindingInstall_Answer(path, mode, before, after, err);
        return false;
    }

    InstanceHandle bh = 0;
    const HRESULT hrH = diag->GetHandleFromIInspectable(binding, &bh);
    if (FAILED(hrH) || !bh) {
        binding->Release();
        *outJson = DevToolsBindingInstall_Answer(path, mode, before, after,
                                            L"the {Binding} could not be handed to the XAML diagnostics API");
        return false;
    }

    const HRESULT hrSet = vts3->SetProperty(elem, bh, propIndex);
    binding->Release();

    after = readBack();
    *outJson = DevToolsBindingInstall_Answer(path, mode, before, after,
                                        (!after.isBinding && FAILED(hrSet))
                                            ? L"the XAML runtime refused the binding"
                                            : std::wstring());
    return after.isBinding;
}
