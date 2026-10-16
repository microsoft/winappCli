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

// Ask the agent whether Microsoft.UI.Reactor is loaded (PROBE1\n -> one line). No element crosses and the agent
// does not touch the UI thread, so it is cheap enough for every DevTools.negotiate. Returns false with an
// `unavailable` answer when no agent answered.
bool DevToolsBindingRelay_Probe(std::wstring* outJson);

// Opens the agent's Reactor event stream (EVENTS1 <mask>; mask 1 = component renders). The caller reads
// "EVENT {json}" lines from the returned overlapped handle until it closes, and may send "MASK <n>" lines with
// DevToolsBindingRelay_WriteLine. INVALID_HANDLE_VALUE with `error` set when no agent answered.
HANDLE DevToolsBindingRelay_OpenEvents(int mask, std::wstring* error);

// Writes one line (a newline is added) to an overlapped agent pipe, bounded by the connect timeout.
bool DevToolsBindingRelay_WriteLine(HANDLE pipe, const std::string& line);


// BINDING2\n, then a length-prefixed COM packet and three length-prefixed UTF-8 strings.
std::vector<char> DevToolsBindingRelay_BuildBindingFrame(const std::vector<char>& packet, const std::wstring& op,
                                                const std::wstring& prop, const std::wstring& authored);

bool DevToolsBindingRelay_IsAllowedOp(const std::wstring& op);

size_t DevToolsBindingRelay_MaxReplyBytes(const std::wstring& op);

std::wstring DevToolsBindingRelay_UnavailableJson(const std::wstring& reason);

enum class DevToolsAppRuntime { Clr, NativeAot, None };
// The .NET runtime this process runs on: the CLR, Native AOT, or none.
DevToolsAppRuntime DevToolsBindingRelay_AppRuntime();
const wchar_t* DevToolsBindingRelay_NoAgentReason(DevToolsAppRuntime runtime);
