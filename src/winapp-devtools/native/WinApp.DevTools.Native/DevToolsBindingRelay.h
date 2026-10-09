// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

#include <string>
#include <vector>
#include <objbase.h>
#include <objidl.h>

class DevToolsBindingApartment final {
public:
    DevToolsBindingApartment() : result(CoInitializeEx(nullptr, COINIT_MULTITHREADED)) {}
    ~DevToolsBindingApartment() { if (SUCCEEDED(result)) CoUninitialize(); }
    DevToolsBindingApartment(const DevToolsBindingApartment&) = delete;
    DevToolsBindingApartment& operator=(const DevToolsBindingApartment&) = delete;
    const HRESULT result;
};

void DevToolsBindingRelay_NoteUiThread();

// Check whether a managed host pipe may be available. This does not gate the independent native path walk.
bool DevToolsBindingRelay_AgentPresent();

// Ask the agent one binding question about one property on one live element.
bool DevToolsBindingRelay_Binding(const std::wstring& op, IAgileReference* element,
                         const std::wstring& prop, const std::wstring& authored,
                         std::wstring* outJson);


// BINDING2\n, then a length-prefixed COM packet and three length-prefixed UTF-8 strings.
std::vector<char> DevToolsBindingRelay_BuildBindingFrame(const std::vector<char>& packet, const std::wstring& op,
                                                const std::wstring& prop, const std::wstring& authored);

bool DevToolsBindingRelay_IsAllowedOp(const std::wstring& op);

std::wstring DevToolsBindingRelay_UnavailableJson(const std::wstring& reason);

enum class DevToolsAppRuntime { Clr, NativeAot, None };
const wchar_t* DevToolsBindingRelay_NoAgentReason(DevToolsAppRuntime runtime);
