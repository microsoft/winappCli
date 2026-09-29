// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

#include <cstddef>
#include <string>

enum class DevToolsProtocolMethodId {
#define WINAPP_DEVTOOLS_PROTOCOL_TYPE(...)
#define WINAPP_DEVTOOLS_PROTOCOL_METHOD(id, name, capability, access, visibility, params, result) id,
#define WINAPP_DEVTOOLS_PROTOCOL_EVENT(...)
#define WINAPP_DEVTOOLS_PROTOCOL_ERROR(...)
#include "DevToolsProtocolSchema.inc"
#undef WINAPP_DEVTOOLS_PROTOCOL_ERROR
#undef WINAPP_DEVTOOLS_PROTOCOL_EVENT
#undef WINAPP_DEVTOOLS_PROTOCOL_METHOD
#undef WINAPP_DEVTOOLS_PROTOCOL_TYPE
};

enum class DevToolsAccess { Read, Ui, Mutation };

enum class DevToolsVisibility { Public, Internal };

const wchar_t* DevToolsAccessToken(DevToolsAccess access);
bool DevToolsAccessFromToken(const std::wstring& token, DevToolsAccess* outAccess);

struct DevToolsProtocolMethod {
    DevToolsProtocolMethodId id;
    const wchar_t* name;
    const wchar_t* capability;
    DevToolsAccess access;
    DevToolsVisibility visibility;
    const wchar_t* paramsType;
    const wchar_t* resultType;
};

struct DevToolsProtocolType {
    const wchar_t* name;
    const char* schemaJson;
};

struct DevToolsProtocolEvent {
    const wchar_t* name;
    const wchar_t* capability;
    const wchar_t* paramsType;
};

struct DevToolsProtocolError {
    const wchar_t* name;
    int code;
    const wchar_t* token;
};

const DevToolsProtocolMethod* DevToolsProtocolMethods(size_t* count);
const DevToolsProtocolType* DevToolsProtocolTypes(size_t* count);
const DevToolsProtocolEvent* DevToolsProtocolEvents(size_t* count);
const DevToolsProtocolError* DevToolsProtocolErrors(size_t* count);

const DevToolsProtocolMethod* DevToolsProtocolFindMethod(const std::wstring& name);

inline constexpr const wchar_t* kDevToolsReservedParamOwner = L"owner";
inline constexpr const wchar_t* kDevToolsReservedParamWindow = L"window";
inline constexpr const wchar_t* kDevToolsReservedParamRoot = L"root";

bool DevToolsProtocolParamDeclared(const std::wstring& paramsTypeName, const std::wstring& key);

std::wstring DevToolsProtocolCapabilitiesJson(unsigned long processId, DevToolsAccess posture,
                                         unsigned long long connectionId, const std::wstring& ownerToken);
