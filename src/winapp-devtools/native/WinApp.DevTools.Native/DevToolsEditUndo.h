// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

// What a property held before a protocol edit (Resource.assign, Binding.install), and the calls that put it back.
// Pure, so the classification can be tested without a live tree.

#include <cstdlib>
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

// What the property holds now, beyond its row: the runtime identity of its binding and the element's theme.
struct DevToolsEditObserved
{
    std::wstring bindingId;
    int          elementTheme = -1;
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
// the value is what the key produced, or the element's theme has changed since (a theme key follows the theme).
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
    if (row.source == L"Local" && !row.authoredKey.empty() &&
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

// A binding path reads every segment it names, so a secret anywhere on it would show in the bound property.
inline bool DevToolsPathNamesSecret(const std::wstring& path)
{
    size_t start = 0;
    while (start <= path.size()) {
        const size_t dot = path.find(L'.', start);
        std::wstring segment = path.substr(start, dot == std::wstring::npos ? std::wstring::npos : dot - start);
        if (const size_t bracket = segment.find(L'['); bracket != std::wstring::npos) segment.resize(bracket);
        if (DevToolsRead_IsSecretProperty(segment)) return true;
        if (dot == std::wstring::npos) break;
        start = dot + 1;
    }
    return false;
}

// A source write has no exact undo: the binding can point at another object by the time it runs, and writing the
// old value then changes that object instead. `sourceBefore` still reports the value the source held.
inline std::wstring DevToolsWriteSourceUndoMembers()
{
    return L"\"undo\":[],\"undoReason\":\"the binding's source can change before an undo runs, so DevTools does not "
           L"write the previous value back for you; sourceBefore shows what it was\"";
}
