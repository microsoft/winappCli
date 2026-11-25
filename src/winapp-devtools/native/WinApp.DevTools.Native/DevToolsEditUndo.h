// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

// What a property held before a protocol edit (Resource.assign, Binding.install), and the calls that put it back.
// Pure, so the classification can be tested without a live tree.

#include <cstdlib>
#include <cwctype>
#include <string>

#include "DevToolsProtocol.h"
#include "DevToolsRead.h"

enum class DevToolsPriorKind { Unset, Literal, Resource, Binding, InstalledBinding, XBind, Unwritable };

// An edit this session made through the protocol, so a later edit knows what the runtime cannot tell it:
// that a local value came from a resource key, or that the current binding is the one DevTools installed.
struct DevToolsSessionEdit
{
    bool         resource = false;   // true: a resource key; false: an installed {Binding}
    std::wstring key;
    bool         theme = false;
    std::wstring path, mode;
    std::wstring after;              // resource: the value the key produced
    std::wstring bindingId;          // binding: the runtime handle of the expression DevTools installed
    int          elementTheme = -1;  // resource: the element's ActualTheme when the key was assigned
};

// What the property holds now, beyond its row: the runtime identity of its binding, the element's theme, and
// whether its value is the one its authored resource key gives (1), is not (0), or DevTools could not tell (-1).
struct DevToolsEditObserved
{
    std::wstring bindingId;
    int          elementTheme = -1;
    int          authoredKeyMatch = -1;
};

struct DevToolsEditPrior
{
    DevToolsPriorKind kind = DevToolsPriorKind::Unset;
    std::wstring value, writeType;   // Literal
    std::wstring key;                // Resource
    bool         theme = false;
    std::wstring path, mode;         // InstalledBinding
    bool         captured = false;   // Binding / XBind: a managed capture saved a restore route
    std::wstring reason;             // why there is no undo, when there is none
};

inline const wchar_t* DevToolsPriorKindToken(const DevToolsEditPrior& prior)
{
    switch (prior.kind) {
        case DevToolsPriorKind::Literal:          return L"literal";
        case DevToolsPriorKind::Resource:         return prior.theme ? L"themeResource" : L"staticResource";
        case DevToolsPriorKind::Binding:          return L"binding";
        case DevToolsPriorKind::InstalledBinding: return L"binding";
        case DevToolsPriorKind::XBind:            return L"xBind";
        case DevToolsPriorKind::Unwritable:       return L"literal";
        default:                                  return L"unset";
    }
}

// A binding is present even when its current value is null or an object the reader cannot show as text.
inline bool DevToolsRowIsBound(const DevToolsReadProp& row)
{
    return row.valueState == L"binding" || row.source == L"Binding" || !row.binding.empty();
}

// Text keeps only a brush's colour; any other setting it carries (Opacity) would be lost by writing it back.
inline std::wstring DevToolsLossyTextReason(const DevToolsReadProp& row)
{
    for (const auto& child : row.children) {
        if (child.name == L"Color") continue;
        if (child.name == L"Opacity" && !child.value.empty() && std::wcstod(child.value.c_str(), nullptr) == 1.0) continue;
        return L"the previous value has settings its text form does not keep (" + child.name + L"=" + child.value + L")";
    }
    return L"";
}

// Session records come first because the runtime forgets a resource key and does not mark who installed a binding.
// A binding record counts only while the very expression DevTools installed is still there; a resource record while
// the value is what the key produced, or the element's theme changed since (a theme key follows the theme). A later
// literal write or clear forgets the record. An authored key counts unless its value is known to differ now.
inline DevToolsEditPrior DevToolsClassifyPrior(const DevToolsReadProp& row, const DevToolsSessionEdit* session,
                                               const DevToolsEditObserved& observed = {})
{
    DevToolsEditPrior prior;
    const bool bound = DevToolsRowIsBound(row);
    const bool local = row.source == L"Local" || row.source == kDevToolsReadLiveEditSource;
    if (session) {
        const bool themeMoved = session->theme && session->elementTheme >= 0 && observed.elementTheme >= 0 &&
                                observed.elementTheme != session->elementTheme;
        if (session->resource && !bound && local && (row.value == session->after || themeMoved)) {
            prior.kind = DevToolsPriorKind::Resource; prior.key = session->key; prior.theme = session->theme;
            return prior;
        }
        if (!session->resource && bound && !session->bindingId.empty() && observed.bindingId == session->bindingId) {
            prior.kind = DevToolsPriorKind::InstalledBinding; prior.path = session->path; prior.mode = session->mode;
            return prior;
        }
    }
    if (bound) { prior.kind = DevToolsPriorKind::Binding; return prior; }
    if (row.source == L"Local" && row.authoredKind == L"xBind") { prior.kind = DevToolsPriorKind::XBind; return prior; }
    if (row.source == L"Local" && !row.authoredKey.empty() && observed.authoredKeyMatch != 0 &&
        (row.authoredKind == L"staticResource" || row.authoredKind == L"themeResource")) {
        prior.kind = DevToolsPriorKind::Resource; prior.key = row.authoredKey;
        prior.theme = row.authoredKind == L"themeResource";
        return prior;
    }
    if (!local) return prior;
    if (!row.writeType.empty() && row.valueState.empty() && !row.redacted) {
        prior.reason = DevToolsLossyTextReason(row);
        prior.kind = prior.reason.empty() ? DevToolsPriorKind::Literal : DevToolsPriorKind::Unwritable;
        prior.value = row.value; prior.writeType = row.writeType;
        return prior;
    }
    prior.kind = DevToolsPriorKind::Unwritable;
    prior.reason = L"the previous local value cannot be written back as text";
    return prior;
}

inline std::wstring DevToolsUndoTarget(const std::wstring& handle, const std::wstring& prop)
{
    return L"\"handle\":\"" + DevToolsJsonEscape(handle) + L"\",\"prop\":\"" + DevToolsJsonEscape(prop) + L"\"";
}

inline std::wstring DevToolsUndoStep(const wchar_t* method, const std::wstring& params)
{
    return L"{\"method\":\"" + std::wstring(method) + L"\",\"params\":{" + params + L"}}";
}

// `"undo":[...]`: the calls to run in order. Empty, with `undoReason`, when nothing can be put back. When the edit
// left a binding in place, a literal or key written over it would write through to the source, so clear it first.
inline std::wstring DevToolsEditUndoMembers(const std::wstring& handle, const std::wstring& prop,
                                            const DevToolsEditPrior& prior, bool editLeftBinding = false)
{
    const std::wstring target = DevToolsUndoTarget(handle, prop);
    const std::wstring clear = DevToolsUndoStep(L"HotReload.clearProperty", target);
    const std::wstring first = editLeftBinding ? clear + L"," : std::wstring();
    auto steps = [](const std::wstring& list) { return L"\"undo\":[" + list + L"]"; };
    switch (prior.kind) {
        case DevToolsPriorKind::Unset:
            return steps(clear);
        case DevToolsPriorKind::Literal:
            return steps(first + DevToolsUndoStep(L"HotReload.setProperty", target + L",\"type\":\"" +
                DevToolsJsonEscape(prior.writeType) + L"\",\"value\":\"" + DevToolsJsonEscape(prior.value) + L"\""));
        case DevToolsPriorKind::Resource:
            return steps(first + DevToolsUndoStep(L"Resource.assign", target + L",\"key\":\"" + DevToolsJsonEscape(prior.key) +
                L"\",\"theme\":" + (prior.theme ? L"true" : L"false")));
        case DevToolsPriorKind::InstalledBinding:
            return steps(DevToolsUndoStep(L"Binding.install", target + L",\"path\":\"" + DevToolsJsonEscape(prior.path) +
                L"\",\"mode\":\"" + DevToolsJsonEscape(prior.mode.empty() ? L"OneWay" : prior.mode) + L"\""));
        case DevToolsPriorKind::Binding:
            if (prior.captured) return steps(DevToolsUndoStep(L"Binding.restore", target));
            break;
        case DevToolsPriorKind::XBind:
            // Restoring a compiled binding re-runs its owner's Bindings.Update(), which Binding.restore asks to confirm.
            if (prior.captured) return steps(first + DevToolsUndoStep(L"Binding.restore", target + L",\"confirmOwnerRebind\":true"));
            break;
        default:
            break;
    }
    return steps(L"") + L",\"undoReason\":\"" + DevToolsJsonEscape(prior.reason.empty()
        ? std::wstring(L"the previous value has no restore route") : prior.reason) + L"\"";
}

// A binding path reads every segment it names, and every indexer key (`Credentials[Password]`), so a secret
// anywhere on it would show in the bound property.
inline bool DevToolsPathNamesSecret(const std::wstring& path)
{
    std::wstring name;
    auto secret = [&name]() {
        const size_t first = name.find_first_not_of(L" \t'\"");
        const size_t last = name.find_last_not_of(L" \t'\"");
        const bool found = first != std::wstring::npos && DevToolsRead_IsSecretProperty(name.substr(first, last - first + 1));
        name.clear();
        return found;
    };
    for (const wchar_t c : path) {
        if (c == L'.' || c == L'[' || c == L']') { if (secret()) return true; continue; }
        name += c;
    }
    return secret();
}

// Text that shows a member whose name ends in "Password" the way a record prints it ("Login { User = ada,
// Password = hunter2 }"). The native read-back cannot see the value's type, so this checks its text instead.
inline bool DevToolsTextPrintsSecret(const std::wstring& text)
{
    if (text.find(L"{ ") == std::wstring::npos) return false;
    const std::wstring word = L"password";
    for (size_t i = 0; i < text.size(); ++i) {
        if (text[i] != L'{' && text[i] != L',') continue;
        size_t j = i + 1;
        if (j >= text.size() || !iswspace(text[j])) continue;
        while (j < text.size() && iswspace(text[j])) ++j;
        size_t end = j;
        while (end < text.size() && (iswalnum(text[end]) || text[end] == L'_')) ++end;
        if (end - j < word.size() || _wcsnicmp(text.c_str() + end - word.size(), word.c_str(), word.size()) != 0) continue;
        size_t k = end;
        if (k >= text.size() || !iswspace(text[k])) continue;
        while (k < text.size() && iswspace(text[k])) ++k;
        if (k + 1 < text.size() && text[k] == L'=' && iswspace(text[k + 1])) return true;
    }
    return false;
}