// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

#include <string>
#include <map>
#include <vector>

namespace DevToolsErr {
    constexpr int ParseError      = -32700;
    constexpr int InvalidRequest  = -32600;
    constexpr int MethodNotFound  = -32601;
    constexpr int InvalidParams   = -32602;
    constexpr int InternalError   = -32603;
    constexpr int TargetLost           = -32000;
    constexpr int StaleHandle          = -32001;
    constexpr int NotOnDispatcher      = -32002;
    constexpr int CapabilityUnsupported= -32003;
    constexpr int Unauthorized         = -32004;
    constexpr int RefusedUnsafe        = -32005;
    constexpr int UnreachableGate      = -32006;
    constexpr int SourceUnavailable    = -32007;
    constexpr int Cancelled            = -32008;
    constexpr int NotFound             = -32009;
    constexpr int NoHit                = -32010;
    constexpr int NoTree               = -32011;
    constexpr int NotReady             = -32012;
    constexpr int OutsideSurface       = -32013;
}

enum class DevToolsJsonType { Null, Bool, Number, String, Object, Array };

struct DevToolsJson {
    DevToolsJsonType type = DevToolsJsonType::Null;
    bool         b   = false;
    double       num = 0.0;
    std::wstring str;
    std::map<std::wstring, DevToolsJson> obj;
    std::vector<DevToolsJson>            arr;

    bool IsObject() const { return type == DevToolsJsonType::Object; }
    bool HasKey(const std::wstring& key) const;
    const DevToolsJson* Find(const std::wstring& key) const;

    std::wstring   GetString(const std::wstring& key, const std::wstring& def = L"") const;
    long long      GetInt   (const std::wstring& key, long long def) const;
    bool           GetBool  (const std::wstring& key, bool def) const;
};

bool DevToolsJsonParse(const std::wstring& text, DevToolsJson& out);

// Wire handles reject whitespace/signs/overflow so forged or truncated ids cannot alias real slots.
bool DevToolsParseWireHandle(const std::wstring& text, bool allowZero, unsigned long long* value);

std::wstring DevToolsJsonSerialize(const DevToolsJson& v);

std::wstring DevToolsJsonEscape(const std::wstring& s);


struct DevToolsRpcRequest {
    bool         parsed = false;
    bool         valid  = false;
    bool         hasId  = false;
    std::wstring idRaw  = L"null";
    std::wstring method;
    DevToolsJson      params;
};

DevToolsRpcRequest DevToolsRpcParse(const std::wstring& line);

std::wstring DevToolsRpcResult(const std::wstring& idRaw, const std::wstring& resultJson);

std::wstring DevToolsRpcError(const std::wstring& idRaw, int code, const std::wstring& message,
                         const std::wstring& token, const std::wstring& dataExtra = L"");

std::wstring DevToolsRpcEvent(const std::wstring& method, const std::wstring& paramsJson,
                         const std::wstring& originJson = L"null");
