// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

// What a property held before a protocol edit (Resource.assign, Binding.install), and the one protocol call
// that puts it back. Pure, so the classification can be tested without a live tree.

#include <string>

#include "DevToolsProtocol.h"
#include "DevToolsRead.h"

enum class DevToolsPriorKind { Unset, Literal, Resource, Binding, InstalledBinding, XBind, Unwritable };

// An edit this session made through the protocol, so a later edit knows what the runtime cannot tell it:
// that a local value came from a resource key, or that a binding was installed by DevTools.
struct DevToolsSessionEdit
{
    bool         resource = false;   // true: a resource key; false: an installed {Binding}
    std::wstring key;
    bool         theme = false;
    std::wstring path, mode;
    std::wstring after;              // the value the edit produced, to tell whether it still holds
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

// "{Binding Vm.Title, Mode=OneWay}" -> "Vm.Title", as DevToolsBinding_Describe spells it.
inline std::wstring DevToolsDescribedBindingPath(const std::wstring& described)
{
    const std::wstring head = L"{Binding ";
    if (described.rfind(head, 0) != 0) return L"";
    const size_t end = described.find_first_of(L",}", head.size());
    const std::wstring path = described.substr(head.size(), end == std::wstring::npos ? std::wstring::npos : end - head.size());
    return path.find(L'=') == std::wstring::npos ? path : std::wstring();
}

// Session records come first because the runtime forgets a resource key and does not mark who installed a binding.
// A record counts only while the property still shows what that edit produced.
inline DevToolsEditPrior DevToolsClassifyPrior(const DevToolsReadProp& row, const DevToolsSessionEdit* session)
{
    DevToolsEditPrior prior;
    const bool bound = row.valueState == L"binding";
    const bool local = row.source == L"Local" || row.source == kDevToolsReadLiveEditSource;
    if (session) {
        if (session->resource && !bound && local && row.value == session->after) {
            prior.kind = DevToolsPriorKind::Resource; prior.key = session->key; prior.theme = session->theme;
            return prior;
        }
        if (!session->resource && bound && DevToolsDescribedBindingPath(row.binding) == session->path) {
            prior.kind = DevToolsPriorKind::InstalledBinding; prior.path = session->path; prior.mode = session->mode;
            return prior;
        }
    }
    if (bound) { prior.kind = DevToolsPriorKind::Binding; return prior; }
    if (row.source == L"Local" && row.authoredKind == L"xBind") { prior.kind = DevToolsPriorKind::XBind; return prior; }
    if (row.source == L"Local" && !row.authoredKey.empty() &&
        (row.authoredKind == L"staticResource" || row.authoredKind == L"themeResource")) {
        prior.kind = DevToolsPriorKind::Resource; prior.key = row.authoredKey;
        prior.theme = row.authoredKind == L"themeResource";
        return prior;
    }
    if (!local) return prior;
    if (!row.writeType.empty() && row.valueState.empty() && !row.redacted) {
        prior.kind = DevToolsPriorKind::Literal; prior.value = row.value; prior.writeType = row.writeType;
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

// `"undo":{...}` or `"undo":null,"undoReason":"..."`, ready to append to a result object.
inline std::wstring DevToolsEditUndoMembers(const std::wstring& handle, const std::wstring& prop,
                                            const DevToolsEditPrior& prior)
{
    const std::wstring target = DevToolsUndoTarget(handle, prop);
    auto call = [](const wchar_t* method, const std::wstring& params) {
        return L"\"undo\":{\"method\":\"" + std::wstring(method) + L"\",\"params\":{" + params + L"}}";
    };
    switch (prior.kind) {
        case DevToolsPriorKind::Unset:
            return call(L"HotReload.clearProperty", target);
        case DevToolsPriorKind::Literal:
            return call(L"HotReload.setProperty", target + L",\"type\":\"" + DevToolsJsonEscape(prior.writeType) +
                        L"\",\"value\":\"" + DevToolsJsonEscape(prior.value) + L"\"");
        case DevToolsPriorKind::Resource:
            return call(L"Resource.assign", target + L",\"key\":\"" + DevToolsJsonEscape(prior.key) +
                        L"\",\"theme\":" + (prior.theme ? L"true" : L"false"));
        case DevToolsPriorKind::InstalledBinding:
            return call(L"Binding.install", target + L",\"path\":\"" + DevToolsJsonEscape(prior.path) +
                        L"\",\"mode\":\"" + DevToolsJsonEscape(prior.mode.empty() ? L"OneWay" : prior.mode) + L"\"");
        case DevToolsPriorKind::Binding:
            if (prior.captured) return call(L"Binding.restore", target);
            break;
        case DevToolsPriorKind::XBind:
            // Restoring a compiled binding re-runs its owner's Bindings.Update(), which Binding.restore asks to confirm.
            if (prior.captured) return call(L"Binding.restore", target + L",\"confirmOwnerRebind\":true");
            break;
        default:
            break;
    }
    return L"\"undo\":null,\"undoReason\":\"" + DevToolsJsonEscape(prior.reason.empty()
        ? std::wstring(L"the previous value has no restore route") : prior.reason) + L"\"";
}
