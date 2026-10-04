// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

#include <algorithm>
#include <map>
#include <string>
#include <vector>

namespace DevToolsCommentAnchor {

template<typename Handle>
std::wstring Capture(Handle handle, const std::map<Handle, std::wstring>& names,
    const std::map<Handle, std::wstring>& types, const std::map<Handle, std::wstring>& sources,
    const std::map<Handle, Handle>& parents, const std::map<Handle, std::vector<Handle>>& children)
{
    const auto source = sources.find(handle);
    if (source == sources.end() || source->second.empty()) return L"";
    std::wstring key = L"source:";
    const auto field = [&key](const std::wstring& value) {
        key += std::to_wstring(value.size()) + L":" + value;
    };
    for (unsigned int depth = 0; handle && depth < 64; ++depth) {
        const auto type = types.find(handle);
        if (type == types.end()) return L"";
        const auto file = sources.find(handle);
        const auto name = names.find(handle);
        const auto parent = parents.find(handle);
        if (file != sources.end() && !file->second.empty()) {
            field(file->second);
            field(type->second);
            field(name == names.end() ? L"" : name->second);
            // An unnamed authored node needs its structural slot; a name carries its own stable identity.
            if (name == names.end() || name->second.empty()) {
                if (parent != parents.end()) {
                    const auto siblings = children.find(parent->second);
                    if (siblings == children.end()) return L"";
                    const auto slot = std::find(siblings->second.begin(), siblings->second.end(), handle);
                    if (slot == siblings->second.end()) return L"";
                    field(std::to_wstring(slot - siblings->second.begin()));
                } else field(L"root");
            }
        }
        if (parent == parents.end() || !parent->second) return key;
        handle = parent->second;
    }
    return L""; // a cyclic or incomplete census is not a source identity
}

template<typename Handle>
bool IsUnique(Handle selected, const std::wstring& anchor, const std::map<Handle, std::wstring>& names,
    const std::map<Handle, std::wstring>& types, const std::map<Handle, std::wstring>& sources,
    const std::map<Handle, Handle>& parents, const std::map<Handle, std::vector<Handle>>& children,
    const wchar_t** reason = nullptr)
{
    const auto unavailable = [reason](const wchar_t* value) { if (reason) *reason = value; return false; };
    if (reason) *reason = L"unique";
    if (anchor.empty() || Capture(selected, names, types, sources, parents, children) != anchor)
        return unavailable(L"missing-source-identity");
    const auto selectedType = types.find(selected);
    const auto selectedSource = sources.find(selected);
    const auto selectedName = names.find(selected);
    const std::wstring name = selectedName == names.end() ? L"" : selectedName->second;
    unsigned int matches = 0;
    for (const auto& entry : types) {
        const auto candidateName = names.find(entry.first);
        if (entry.second != selectedType->second || (candidateName == names.end() ? L"" : candidateName->second) != name) continue;
        const auto source = sources.find(entry.first);
        // Absent is unclassified; an explicit empty value is classified as having no source.
        if (source == sources.end()) return unavailable(L"unclassified-peer");
        if (source->second.empty() || source->second != selectedSource->second) continue;
        Handle ancestor = entry.first;
        for (unsigned int depth = 0; ancestor && depth < 64; ++depth) {
            if (sources.find(ancestor) == sources.end() || types.find(ancestor) == types.end())
                return unavailable(L"unclassified-ancestor");
            const auto parent = parents.find(ancestor);
            ancestor = parent == parents.end() ? 0 : parent->second;
        }
        if (ancestor) return unavailable(L"incomplete-ancestry");
        const auto candidate = Capture(entry.first, names, types, sources, parents, children);
        if (candidate.empty()) return unavailable(L"incomplete-ancestry");
        if (candidate == anchor && ++matches > 1) return unavailable(L"repeated-source-instance");
    }
    return matches == 1 || unavailable(L"missing-source-identity");
}

template<typename Handle>
Handle Resolve(const std::wstring& anchor, const std::map<Handle, std::wstring>& names,
    const std::map<Handle, std::wstring>& types, const std::map<Handle, std::wstring>& sources,
    const std::map<Handle, Handle>& parents, const std::map<Handle, std::vector<Handle>>& children)
{
    if (anchor.rfind(L"source:", 0) != 0) return 0;
    for (const auto& entry : sources) {
        if (!entry.second.empty() && Capture(entry.first, names, types, sources, parents, children) == anchor)
            return entry.first;
    }
    return 0;
}
} // namespace DevToolsCommentAnchor
