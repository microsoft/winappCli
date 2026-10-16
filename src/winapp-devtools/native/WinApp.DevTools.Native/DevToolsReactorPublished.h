// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

// Reactor diagnostics published on the control itself, for apps the managed agent cannot load into (Native AOT,
// trimmed builds, late attach). In diagnostics mode Reactor sets one attached string property on each control it
// creates, and the tap reads it through XamlDiagnostics like any other property:
//
//   v=1|at=C:\src\App.cs:35:13|owner=Counter|element=Button|key=t1|name=incrementButton
//
// Fields are `name=value`, separated by '|'. `v` comes first and must be a version this reader knows. `element` is
// required; `at` is `path:line[:column]` (a project-relative path; absolute and /_/ paths are accepted too); `owner`
// is the component whose render created the element; `mounts` is the component a component element mounts; `root` is
// the root component a host renders into this control; `name` is the C# variable or member the element was assigned
// to; `hooks` (on component and root controls) lists the component's hooks as `index:name@line;...`, where the index
// is `?` once it is no longer known statically (after a hook in a branch, loop or composite hook). Unknown fields are ignored so
// Reactor can add some without breaking older readers. '%' and '|' inside a value are written as %25 and %7C
// (Windows paths cannot contain '|', keys can).
//
// The property is registered by Reactor with an owner type that has no XAML type information, so XamlDiagnostics
// names it `.ReactorSource`; a reader also accepts `<Owner>.ReactorSource` in case the owner becomes resolvable.

#include <cwctype>
#include <string>
#include <vector>

namespace DevToolsReactorPublished {

inline constexpr wchar_t kPropertyLeaf[] = L"ReactorSource";
inline constexpr unsigned int kVersion = 1;

// One hook of a component: its index in call order (the index Reactor's hook diagnostics report), the C# variable it
// was assigned to (or the hook method when its result is not stored), and the line of its Use* call (0 when not
// recorded). `indexed` is false for a `?` entry, which names a hook whose index is not known statically: it is shown,
// but cannot be matched to a live hook.
struct Hook
{
    bool indexed = true;
    unsigned int index = 0;
    std::wstring name;
    unsigned int line = 0;
};

struct Record
{
    unsigned int version = 0;
    std::wstring file;
    unsigned int line = 0;
    unsigned int column = 0;
    std::wstring owner;
    std::wstring element;
    std::wstring mounts;
    std::wstring root;
    std::wstring key;
    std::wstring name;
    std::vector<Hook> hooks;
};

// Whether a property-chain name is the published property: `.ReactorSource` or `<Owner>.ReactorSource`.
inline bool IsPropertyName(const std::wstring& name)
{
    const size_t dot = name.rfind(L'.');
    return dot != std::wstring::npos && name.compare(dot + 1, std::wstring::npos, kPropertyLeaf) == 0;
}

inline bool Decode(const std::wstring& in, std::wstring* out)
{
    out->clear();
    out->reserve(in.size());
    for (size_t i = 0; i < in.size(); ++i) {
        if (in[i] != L'%') { *out += in[i]; continue; }
        if (i + 2 >= in.size()) return false;
        const wchar_t a = in[i + 1], b = static_cast<wchar_t>(std::towupper(in[i + 2]));
        if (a == L'2' && b == L'5') *out += L'%';
        else if (a == L'7' && b == L'C') *out += L'|';
        else return false;
        i += 2;
    }
    return true;
}

inline bool ParseNumber(const std::wstring& text, unsigned int* out)
{
    if (text.empty() || text.size() > 9) return false;
    unsigned int value = 0;
    for (wchar_t c : text) {
        if (c < L'0' || c > L'9') return false;
        value = value * 10 + static_cast<unsigned int>(c - L'0');
    }
    *out = value;
    return true;
}

// `path:line` or `path:line:column`, read from the right because the path itself has a drive colon.
inline bool ParseLocation(const std::wstring& at, std::wstring* file, unsigned int* line, unsigned int* column)
{
    const size_t last = at.rfind(L':');
    if (last == std::wstring::npos || last == 0) return false;
    unsigned int tail = 0;
    if (!ParseNumber(at.substr(last + 1), &tail)) return false;
    const std::wstring head = at.substr(0, last);
    const size_t previous = head.rfind(L':');
    unsigned int middle = 0;
    // A second number is the line only when what precedes it is still a path, not a lone drive letter.
    if (previous != std::wstring::npos && previous > 1 && ParseNumber(head.substr(previous + 1), &middle)) {
        *file = head.substr(0, previous);
        *line = middle;
        *column = tail;
    } else {
        *file = head;
        *line = tail;
        *column = 0;
    }
    return !file->empty() && *line > 0;
}

// `0:count@68;1:items@69`. An entry that does not read is skipped rather than failing the element: the hook list is
// a convenience on top of the call site, and an index without a name just shows as an index.
inline std::vector<Hook> ParseHooks(const std::wstring& text)
{
    std::vector<Hook> hooks;
    for (size_t start = 0; start <= text.size();) {
        size_t end = text.find(L';', start);
        if (end == std::wstring::npos) end = text.size();
        const std::wstring entry = text.substr(start, end - start);
        start = end + 1;
        const size_t colon = entry.find(L':');
        if (colon == std::wstring::npos) continue;
        Hook hook;
        if (entry.compare(0, colon, L"?") == 0) hook.indexed = false;
        else if (!ParseNumber(entry.substr(0, colon), &hook.index)) continue;
        std::wstring rest = entry.substr(colon + 1);
        if (const size_t at = rest.rfind(L'@'); at != std::wstring::npos) {
            if (!ParseNumber(rest.substr(at + 1), &hook.line)) hook.line = 0;
            rest.resize(at);
        }
        if (rest.empty()) continue;
        bool duplicate = false;
        for (const auto& known : hooks) duplicate = duplicate || (hook.indexed && known.indexed && known.index == hook.index);
        if (duplicate) continue;
        hook.name = std::move(rest);
        hooks.push_back(std::move(hook));
    }
    return hooks;
}

// The name of hook `index`, or empty when Reactor published none for it.
inline std::wstring HookName(const std::vector<Hook>& hooks, unsigned int index, unsigned int* line = nullptr)
{
    for (const auto& hook : hooks) {
        if (!hook.indexed || hook.index != index) continue;
        if (line) *line = hook.line;
        return hook.name;
    }
    return {};
}

inline bool Parse(const std::wstring& value, Record* out)
{
    *out = Record{};
    std::vector<std::wstring> fields;
    for (size_t start = 0;;) {
        const size_t bar = value.find(L'|', start);
        fields.push_back(value.substr(start, bar == std::wstring::npos ? std::wstring::npos : bar - start));
        if (bar == std::wstring::npos) break;
        start = bar + 1;
    }
    bool seenAt = false;
    for (size_t i = 0; i < fields.size(); ++i) {
        const size_t eq = fields[i].find(L'=');
        if (eq == std::wstring::npos || eq == 0) return false;
        const std::wstring name = fields[i].substr(0, eq);
        std::wstring text;
        if (!Decode(fields[i].substr(eq + 1), &text)) return false;
        if (i == 0) {
            if (name != L"v" || !ParseNumber(text, &out->version) || out->version != kVersion) return false;
            continue;
        }
        std::wstring* target = nullptr;
        if (name == L"at") {
            if (seenAt || !ParseLocation(text, &out->file, &out->line, &out->column)) return false;
            seenAt = true;
            continue;
        }
        if (name == L"hooks") {
            if (!out->hooks.empty()) return false;
            out->hooks = ParseHooks(text);
            continue;
        }
        if (name == L"owner") target = &out->owner;
        else if (name == L"element") target = &out->element;
        else if (name == L"mounts") target = &out->mounts;
        else if (name == L"root") target = &out->root;
        else if (name == L"key") target = &out->key;
        else if (name == L"name") target = &out->name;
        else if (name == L"v") return false;
        if (!target) continue; // a field a newer Reactor added
        if (!target->empty()) return false;
        *target = std::move(text);
    }
    return out->version == kVersion && !out->element.empty();
}

} // namespace DevToolsReactorPublished
