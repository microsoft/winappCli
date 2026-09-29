// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

#include "DevToolsOwnedState.h"
#include "DevToolsProtocol.h"

#include <string>
#include <vector>

namespace DevToolsBatch {

struct PreviewValue
{
    std::wstring name, value, valueState = L"value", valueType = L"String";
};

inline bool ParsePreviewHandles(const std::wstring& list, size_t cap,
                               std::vector<unsigned long long>& out, size_t& outCount, bool& truncated)
{
    out.clear();
    outCount = 0;
    truncated = false;
    for (size_t start = 0; start <= list.size(); ) {
        const size_t comma = list.find(L',', start);
        const std::wstring token =
            list.substr(start, comma == std::wstring::npos ? std::wstring::npos : comma - start);
        if (!token.empty()) {
            unsigned long long wire = 0;
            if (!DevToolsParseWireHandle(token, /*allowZero*/ false, &wire)) return false;
            out.push_back(wire);
        }
        if (comma == std::wstring::npos) break;
        start = comma + 1;
    }
    if (out.empty()) return false;
    outCount = out.size();
    truncated = out.size() > cap;
    if (truncated) out.resize(cap);
    return true;
}

inline std::wstring BuildPreviewJson(const std::vector<unsigned long long>& wires,
                                     const std::vector<std::wstring>& captions,
                                     size_t requested, bool truncated,
                                     const std::vector<std::vector<PreviewValue>>* values = nullptr)
{
    std::wstring out = L"{\"previews\":[";
    size_t returned = 0;
    for (size_t i = 0; i < wires.size() && i < captions.size(); ++i) {
        if (captions[i].empty() && (!values || i >= values->size() || (*values)[i].empty())) continue;
        if (returned++) out += L',';
        out += L"{\"handle\":\"" + std::to_wstring(wires[i]) +
               L"\",\"preview\":\"" + DevToolsJsonEscape(captions[i]) + L"\"";
        if (values && i < values->size()) {
            out += L",\"values\":[";
            bool first = true;
            for (const auto& value : (*values)[i]) {
                if (!first) out += L',';
                first = false;
                const bool cut = value.value.size() > 120;
                out += L"{\"name\":\"" + DevToolsJsonEscape(value.name) +
                    L"\",\"value\":\"" + DevToolsJsonEscape(value.value.substr(0, 120)) +
                    L"\",\"valueState\":\"" + DevToolsJsonEscape(value.valueState) +
                    L"\",\"valueType\":\"" + DevToolsJsonEscape(value.valueType) +
                    L"\",\"bindingState\":\"unknown\",\"truncated\":" + (cut ? L"true" : L"false") + L"}";
            }
            out += L"]";
        }
        out += L"}";
    }
    out += L"],\"requested\":" + std::to_wstring(requested);
    out += L",\"returned\":" + std::to_wstring(returned);
    out += L",\"truncated\":";
    out += truncated ? L"true" : L"false";
    out += L"}";
    return out;
}

inline std::wstring BuildReleaseJson(const DevToolsOwnedReleaseOutcome& outcome)
{
    std::wstring out = L"{\"revision\":" + std::to_wstring(outcome.revision) + L",\"released\":[";
    for (size_t i = 0; i < outcome.released.size(); ++i) {
        if (i) out += L',';
        out += L"\"" + std::wstring(DevToolsStateAxisName(outcome.released[i].axis)) + L"\"";
    }
    out += L"],\"retained\":[";
    for (size_t i = 0; i < outcome.retained.size(); ++i) {
        if (i) out += L',';
        out += L"\"" + std::wstring(DevToolsStateAxisName(outcome.retained[i])) + L"\"";
    }
    out += L"]}";
    return out;
}

} // namespace DevToolsBatch
