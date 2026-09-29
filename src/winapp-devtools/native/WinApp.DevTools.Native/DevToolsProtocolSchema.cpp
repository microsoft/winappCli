// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "DevToolsProtocolSchema.h"

#include <algorithm>
#include <vector>

#define WINAPP_DEVTOOLS_PROTOCOL_STRINGIZE_INNER(value) #value
#define WINAPP_DEVTOOLS_PROTOCOL_STRINGIZE(value) WINAPP_DEVTOOLS_PROTOCOL_STRINGIZE_INNER(value)
#define WINAPP_DEVTOOLS_PROTOCOL_WIDEN_INNER(value) L##value
#define WINAPP_DEVTOOLS_PROTOCOL_WIDEN(value) WINAPP_DEVTOOLS_PROTOCOL_WIDEN_INNER(value)
#define WINAPP_DEVTOOLS_PROTOCOL_WIDE_NAME(value) WINAPP_DEVTOOLS_PROTOCOL_WIDEN(WINAPP_DEVTOOLS_PROTOCOL_STRINGIZE(value))

namespace {

constexpr DevToolsProtocolType kTypes[] = {
#define WINAPP_DEVTOOLS_PROTOCOL_TYPE(name, schema) { WINAPP_DEVTOOLS_PROTOCOL_WIDE_NAME(name), schema },
#define WINAPP_DEVTOOLS_PROTOCOL_METHOD(...)
#define WINAPP_DEVTOOLS_PROTOCOL_EVENT(...)
#define WINAPP_DEVTOOLS_PROTOCOL_ERROR(...)
#include "DevToolsProtocolSchema.inc"
#undef WINAPP_DEVTOOLS_PROTOCOL_ERROR
#undef WINAPP_DEVTOOLS_PROTOCOL_EVENT
#undef WINAPP_DEVTOOLS_PROTOCOL_METHOD
#undef WINAPP_DEVTOOLS_PROTOCOL_TYPE
};

constexpr DevToolsProtocolMethod kMethods[] = {
#define WINAPP_DEVTOOLS_PROTOCOL_TYPE(...)
#define WINAPP_DEVTOOLS_PROTOCOL_METHOD(id, name, capability, access, visibility, params, result) \
    { DevToolsProtocolMethodId::id, name, capability, access, visibility, WINAPP_DEVTOOLS_PROTOCOL_WIDE_NAME(params), WINAPP_DEVTOOLS_PROTOCOL_WIDE_NAME(result) },
#define WINAPP_DEVTOOLS_PROTOCOL_EVENT(...)
#define WINAPP_DEVTOOLS_PROTOCOL_ERROR(...)
#include "DevToolsProtocolSchema.inc"
#undef WINAPP_DEVTOOLS_PROTOCOL_ERROR
#undef WINAPP_DEVTOOLS_PROTOCOL_EVENT
#undef WINAPP_DEVTOOLS_PROTOCOL_METHOD
#undef WINAPP_DEVTOOLS_PROTOCOL_TYPE
};

constexpr DevToolsProtocolEvent kEvents[] = {
#define WINAPP_DEVTOOLS_PROTOCOL_TYPE(...)
#define WINAPP_DEVTOOLS_PROTOCOL_METHOD(...)
#define WINAPP_DEVTOOLS_PROTOCOL_EVENT(id, name, capability, params) { name, capability, WINAPP_DEVTOOLS_PROTOCOL_WIDE_NAME(params) },
#define WINAPP_DEVTOOLS_PROTOCOL_ERROR(...)
#include "DevToolsProtocolSchema.inc"
#undef WINAPP_DEVTOOLS_PROTOCOL_ERROR
#undef WINAPP_DEVTOOLS_PROTOCOL_EVENT
#undef WINAPP_DEVTOOLS_PROTOCOL_METHOD
#undef WINAPP_DEVTOOLS_PROTOCOL_TYPE
};

constexpr DevToolsProtocolError kErrors[] = {
#define WINAPP_DEVTOOLS_PROTOCOL_TYPE(...)
#define WINAPP_DEVTOOLS_PROTOCOL_METHOD(...)
#define WINAPP_DEVTOOLS_PROTOCOL_EVENT(...)
#define WINAPP_DEVTOOLS_PROTOCOL_ERROR(name, code, token) { WINAPP_DEVTOOLS_PROTOCOL_WIDE_NAME(name), code, token },
#include "DevToolsProtocolSchema.inc"
#undef WINAPP_DEVTOOLS_PROTOCOL_ERROR
#undef WINAPP_DEVTOOLS_PROTOCOL_EVENT
#undef WINAPP_DEVTOOLS_PROTOCOL_METHOD
#undef WINAPP_DEVTOOLS_PROTOCOL_TYPE
};

void AppendStringArray(std::wstring& json, const std::vector<const wchar_t*>& values)
{
    json += L"[";
    for (size_t i = 0; i < values.size(); ++i) {
        if (i) json += L",";
        json += L"\"";
        json += values[i];
        json += L"\"";
    }
    json += L"]";
}

} // namespace

const DevToolsProtocolMethod* DevToolsProtocolMethods(size_t* count)
{
    if (count) *count = sizeof(kMethods) / sizeof(kMethods[0]);
    return kMethods;
}

const DevToolsProtocolType* DevToolsProtocolTypes(size_t* count)
{
    if (count) *count = sizeof(kTypes) / sizeof(kTypes[0]);
    return kTypes;
}

const DevToolsProtocolEvent* DevToolsProtocolEvents(size_t* count)
{
    if (count) *count = sizeof(kEvents) / sizeof(kEvents[0]);
    return kEvents;
}

const DevToolsProtocolError* DevToolsProtocolErrors(size_t* count)
{
    if (count) *count = sizeof(kErrors) / sizeof(kErrors[0]);
    return kErrors;
}

const wchar_t* DevToolsAccessToken(DevToolsAccess access)
{
    switch (access) {
        case DevToolsAccess::Read:     return L"read";
        case DevToolsAccess::Ui:       return L"ui";
        case DevToolsAccess::Mutation: return L"mutation";
    }
    return L"read"; // unreachable for a valid enumerator; fail closed to the least-privileged token
}

bool DevToolsAccessFromToken(const std::wstring& token, DevToolsAccess* outAccess)
{
    if (!outAccess) return false;
    if (token == L"read")     { *outAccess = DevToolsAccess::Read;     return true; }
    if (token == L"ui")       { *outAccess = DevToolsAccess::Ui;       return true; }
    if (token == L"mutation") { *outAccess = DevToolsAccess::Mutation; return true; }
    return false;
}

const DevToolsProtocolMethod* DevToolsProtocolFindMethod(const std::wstring& name)
{
    for (const auto& method : kMethods) {
        if (name == method.name) return &method;
    }
    return nullptr;
}

namespace {

std::vector<std::wstring> DeclaredProperties(const char* schemaJson)
{
    std::vector<std::wstring> names;
    if (!schemaJson) return names;

    int depth = 0;
    bool inString = false, escaped = false;
    int propertiesDepth = -1;             // depth of the "properties" VALUE object once entered
    std::string token;
    bool expectPropertiesValue = false;

    for (const char* p = schemaJson; *p; ++p) {
        const char c = *p;
        if (inString) {
            if (escaped)            { token += c; escaped = false; }
            else if (c == '\\')     { escaped = true; }
            else if (c == '"')      { inString = false; }
            else                    { token += c; }
            continue;
        }
        switch (c) {
            case '"':
                inString = true;
                token.clear();
                break;
            case ':':
                if (propertiesDepth < 0 && depth == 1 && token == "properties") {
                    expectPropertiesValue = true;
                } else if (propertiesDepth >= 0 && depth == propertiesDepth && !token.empty()) {
                    names.emplace_back(token.begin(), token.end());
                }
                break;
            case '{':
            case '[':
                ++depth;
                if (expectPropertiesValue && c == '{') { propertiesDepth = depth; expectPropertiesValue = false; }
                break;
            case '}':
            case ']':
                if (propertiesDepth >= 0 && depth == propertiesDepth) propertiesDepth = -1;
                --depth;
                break;
            default:
                break;
        }
    }
    return names;
}

} // namespace

bool DevToolsProtocolParamDeclared(const std::wstring& paramsTypeName, const std::wstring& key)
{
    if (key == kDevToolsReservedParamOwner) return true;
    if (key == kDevToolsReservedParamWindow) return true;
    if (key == kDevToolsReservedParamRoot) return true;
    for (const auto& type : kTypes) {
        if (paramsTypeName != type.name) continue;
        for (const auto& declared : DeclaredProperties(type.schemaJson)) {
            if (declared == key) return true;
        }
        return false;
    }
    return true;   // a params type the registry does not define: a build bug, not a live-traffic refusal
}

namespace {

void AppendOwnerToken(std::wstring& json, const std::wstring& token)
{
    for (wchar_t c : token) {
        if ((c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f')) json += c;
    }
}

} // namespace

std::wstring DevToolsProtocolCapabilitiesJson(unsigned long processId, DevToolsAccess posture,
                                        unsigned long long connectionId, const std::wstring& ownerToken)
{
    std::vector<const wchar_t*> domains;
    std::vector<const wchar_t*> methods;
    std::vector<const wchar_t*> events;
    std::wstring access;

    for (const auto& method : kMethods) {
        if (method.visibility != DevToolsVisibility::Public) continue;   // Internal.* stays out of every external list
        methods.push_back(method.name);
        if (std::find_if(domains.begin(), domains.end(), [&](const wchar_t* value) {
                return std::wstring(value) == method.capability;
            }) == domains.end()) {
            domains.push_back(method.capability);
        }
        if (!access.empty()) access += L",";
        access += L"\""; access += method.name; access += L"\":\""; access += DevToolsAccessToken(method.access); access += L"\"";
    }
    for (const auto& event : kEvents) events.push_back(event.name);

    std::wstring json = L"{\"protocol\":\"winapp-devtools\",\"protocolVersion\":\"1\",\"experimental\":true,\"processId\":";
    json += std::to_wstring(processId);
    json += L",\"mutation\":";
    json += (posture == DevToolsAccess::Mutation) ? L"true" : L"false";
    json += L",\"posture\":\"";
    json += DevToolsAccessToken(posture);
    json += L"\",\"connectionId\":\"";
    json += std::to_wstring(connectionId);
    json += L"\",\"ownerToken\":\"";
    AppendOwnerToken(json, ownerToken);
    json += L"\",\"domains\":";
    AppendStringArray(json, domains);
    json += L",\"methods\":";
    AppendStringArray(json, methods);
    json += L",\"events\":";
    AppendStringArray(json, events);
    json += L",\"access\":{";
    json += access;
    json += L"}}";
    return json;
}
