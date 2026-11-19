// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsTap.cpp; included into that translation unit, not compiled on its own.

// Resource.assign, Binding.install and Binding.writeSource: the in-process inspector's resource and binding
// editors, for protocol clients. They share the window's primitives and answer with the call that undoes them.

static std::mutex g_sessionEditsMutex;
static std::map<std::pair<unsigned long long, std::wstring>, DevToolsSessionEdit> g_sessionEdits;
static constexpr size_t kMaxSessionEdits = 4096;

static bool SessionEditFind(unsigned long long wire, const std::wstring& prop, DevToolsSessionEdit* out)
{
    std::lock_guard<std::mutex> lock(g_sessionEditsMutex);
    const auto found = g_sessionEdits.find({ wire, prop });
    if (found == g_sessionEdits.end()) return false;
    *out = found->second;
    return true;
}

static void SessionEditNote(unsigned long long wire, const std::wstring& prop, DevToolsSessionEdit edit)
{
    std::lock_guard<std::mutex> lock(g_sessionEditsMutex);
    if (g_sessionEdits.size() >= kMaxSessionEdits) g_sessionEdits.clear();
    g_sessionEdits[{ wire, prop }] = std::move(edit);
}

// `handle` or `name`, addressed and scoped exactly like HotReload.setProperty. Empty on success.
static std::wstring EditTargetFromParams(const std::wstring& id, const DevToolsJson& p, unsigned long long* wire)
{
    const std::wstring name = p.GetString(L"name"), handle = p.GetString(L"handle");
    unsigned long long parsed = 0;
    if (p.HasKey(L"handle") && !DevToolsParseWireHandle(handle, false, &parsed))
        return DevToolsRpcError(id, DevToolsErr::InvalidParams, L"handle must be a canonical non-zero decimal string", L"bad-args");
    if (handle.empty() && name.empty())
        return DevToolsRpcError(id, DevToolsErr::InvalidParams, L"either handle or name is required", L"bad-args");
    if (p.GetString(L"prop").empty())
        return DevToolsRpcError(id, DevToolsErr::InvalidParams, L"prop is required", L"bad-args");
    const std::wstring refused = ResolvePropertyTarget(handle.empty() ? name : L"#" + handle, wire);
    return refused.empty() ? std::wstring() : BridgeErrToRpc(id, refused);
}

// Reads the property's current row and classifies what an edit would replace. Empty on success.
static std::wstring ReadEditPrior(const std::wstring& id, unsigned long long wire, const std::wstring& prop,
                                  DevToolsReadProp* row, DevToolsEditPrior* prior)
{
    struct State { DevToolsReadProp row; bool secret = false, settable = false, found = false; const wchar_t* error = nullptr; };
    auto st = std::make_shared<State>();
    bool completed = false;
    const HRESULT hr = RunOnUiThread([wire, prop, st]() -> HRESULT {
        InstanceHandle ih = 0;
        { CensusSharedLock lock; if (!ResolveWire_nolock(wire, &ih, &st->error)) return E_INVALIDARG; }
        if (DevToolsRead_IsSecretProperty(prop)) { st->secret = true; return S_OK; }
        if (FindPropIndex(ih, prop) == 0xFFFFFFFF) return S_OK;
        st->settable = true;
        std::vector<DevToolsReadProp> props;
        const HRESULT read = ReadCuratedProps(ih, &props, nullptr);
        if (FAILED(read)) return read;
        for (auto& r : props) if (r.name == prop) { st->row = std::move(r); st->found = true; break; }
        return S_OK;
    }, &completed);
    if (completed && st->error) return BridgeErrToRpc(id, std::wstring(L"ERR ") + st->error);
    if (FAILED(hr)) return DevToolsRpcError(id, DevToolsErr::NotOnDispatcher, L"The app's UI thread did not answer.", L"ui-thread",
                                            L"\"hresult\":\"" + HrStr(hr) + L"\"");
    if (st->secret) return DevToolsRpcError(id, DevToolsErr::RefusedUnsafe,
        L"'" + prop + L"' holds a secret; DevTools does not read or write it.", L"refused-unsafe");
    if (!st->settable || !st->found) return DevToolsRpcError(id, DevToolsErr::InvalidParams,
        L"'" + prop + L"' is not a property this element can set", L"unknown-property");
    *row = std::move(st->row);
    DevToolsSessionEdit session;
    const bool known = SessionEditFind(wire, prop, &session);
    *prior = DevToolsClassifyPrior(*row, known ? &session : nullptr);
    return L"";
}

// An app binding or {x:Bind} has no text form to put back, so save it with the managed agent first. Binding.restore
// then undoes the edit. Without the agent the edit still runs; its undo is null and says why.
static void CapturePriorBinding(unsigned long long wire, const std::wstring& prop, DevToolsEditPrior* prior)
{
    if (prior->kind != DevToolsPriorKind::Binding && prior->kind != DevToolsPriorKind::XBind) return;
    const std::wstring json = CmdBindingOp(wire, L"capture", prop);
    DevToolsJson answer;
    const bool parsed = json.rfind(L"ERR ", 0) != 0 && DevToolsJsonParse(json, answer) && answer.IsObject();
    const std::wstring kind = parsed ? answer.GetString(L"restoreKind") : std::wstring();
    prior->captured = parsed && answer.GetString(L"state") == L"captured" && (kind == L"binding" || kind == L"rebind");
    if (!prior->captured) {
        const std::wstring reason = parsed ? answer.GetString(L"reason") : std::wstring();
        prior->reason = L"the previous binding could not be captured" + (reason.empty() ? std::wstring() : L": " + reason);
    }
}

// Same event HotReload.setProperty raises, with this connection as its origin so the writer can ignore the echo.
static void BroadcastEditedProperty(DevToolsConn* conn, unsigned long long wire, const std::wstring& prop,
                                    const std::wstring& value)
{
    std::wstring name;
    {
        CensusSharedLock lock;
        InstanceHandle ih = 0; const wchar_t* e = nullptr;
        if (ResolveWire_nolock(wire, &ih, &e)) name = NameOf_nolock(ih);
    }
    const std::wstring origin = conn ? (L"\"" + std::to_wstring(DevToolsEvents_ConnId(conn)) + L"\"") : std::wstring(L"null");
    const std::wstring params = L"{\"handle\":\"" + std::to_wstring(wire) + L"\",\"name\":\"" + DevToolsJsonEscape(name) +
                                L"\",\"prop\":\"" + DevToolsJsonEscape(prop) + L"\",\"value\":\"" + DevToolsJsonEscape(value) + L"\"}";
    DevToolsEvents_Broadcast(DevToolsDomain_Property, DevToolsRpcEvent(L"Property.changed", params, origin),
                             DevToolsCoalesce_KeepNewest, L"prop:" + std::to_wstring(wire) + L"\x1f" + prop);
}

static std::wstring AppendMembers(const std::wstring& object, const std::wstring& members)
{
    if (object.size() < 2 || object.back() != L'}') return object;
    return object.substr(0, object.size() - 1) + (object.size() > 2 ? L"," : L"") + members + L"}";
}

// IVisualTreeService3::ResolveResource answers S_OK for a key the app does not have, so success is the resource-error
// callback staying quiet AND a local value being set. A failed lookup can clear the property, so put back what it held.
static std::wstring RpcResourceAssign(DevToolsConn* conn, const std::wstring& id, const DevToolsJson& p)
{
    const std::wstring prop = p.GetString(L"prop"), key = p.GetString(L"key");
    const DevToolsJson* themeParam = p.Find(L"theme");
    if (themeParam && themeParam->type != DevToolsJsonType::Bool)
        return DevToolsRpcError(id, DevToolsErr::InvalidParams, L"theme must be a boolean", L"bad-args");
    const bool theme = themeParam && themeParam->b;
    if (key.empty()) return DevToolsRpcError(id, DevToolsErr::InvalidParams, L"key is required", L"bad-args");
    unsigned long long wire = 0;
    if (std::wstring refused = EditTargetFromParams(id, p, &wire); !refused.empty()) return refused;
    DevToolsReadProp before;
    DevToolsEditPrior prior;
    if (std::wstring refused = ReadEditPrior(id, wire, prop, &before, &prior); !refused.empty()) return refused;
    CapturePriorBinding(wire, prop, &prior);

    struct Outcome { bool applied = false, restored = false, bindingLost = false; DevToolsReadProp after; const wchar_t* error = nullptr; };
    auto out = std::make_shared<Outcome>();
    bool completed = false;
    const HRESULT hr = RunOnUiThread([wire, prop, key, theme, prior, out]() -> HRESULT {
        InstanceHandle ih = 0;
        { CensusSharedLock lock; if (!ResolveWire_nolock(wire, &ih, &out->error)) return E_INVALIDARG; }
        const unsigned int idx = FindPropIndex(ih, prop);
        if (idx == 0xFFFFFFFF || !WindowElementCurrent_ui(ih)) return E_INVALIDARG;
        const PriorLocalValue local = ReadPriorLocalValue(ih, idx);
        HRESULT applied = ResolveResourceApply(ih, prop, key, theme);
        if (SUCCEEDED(applied)) {
            const PriorLocalValue now = ReadPriorLocalValue(ih, idx);
            if (!now.had || !now.restorable) applied = HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        }
        if (FAILED(applied)) {
            ClearOriginatedError();
            // Like the window: a key by key, a literal by its text. The old value object may already be released.
            if (prior.kind == DevToolsPriorKind::Resource)
                out->restored = SUCCEEDED(ResolveResourceApply(ih, prop, prior.key, prior.theme));
            else if (prior.kind == DevToolsPriorKind::Literal)
                out->restored = SUCCEEDED(SetLiteral(ih, prop, prior.writeType, prior.value));
            if (!out->restored && local.restorable) out->restored = SUCCEEDED(RestorePriorLocalValue(ih, idx, local));
            out->bindingLost = !out->restored && !local.restorable;
            ClearOriginatedError();
            return S_OK;
        }
        out->applied = true;
        DevToolsBindingRow_ForgetLiveWrite(wire, prop);
        std::vector<DevToolsReadProp> props;
        const std::set<std::wstring> only{ prop };
        ReadCuratedProps(ih, &props, nullptr, nullptr, &only, nullptr, nullptr);
        for (auto& r : props) if (r.name == prop) { out->after = std::move(r); break; }
        return S_OK;
    }, &completed);
    if (completed && out->error) return BridgeErrToRpc(id, std::wstring(L"ERR ") + out->error);
    if (FAILED(hr)) return DevToolsRpcError(id, DevToolsErr::NotOnDispatcher, L"The app's UI thread did not answer.", L"ui-thread",
                                            L"\"hresult\":\"" + HrStr(hr) + L"\"");
    if (!out->applied) {
        bool restored = out->restored;
        if (out->bindingLost && prior.captured) {
            DevToolsJson answer;
            const std::wstring json = CmdBindingOp(wire, L"restore", prop);
            restored = DevToolsJsonParse(json, answer) && answer.IsObject() && answer.GetString(L"state") == L"restored";
        }
        return DevToolsRpcError(id, DevToolsErr::NotFound,
            L"Resource '" + key + L"' was not found or could not be assigned to " + prop + L". " +
            (restored ? std::wstring(L"The previous value was restored. Check the key and resource type.")
                      : std::wstring(L"The previous value could not be restored; set the property again.")),
            L"resource-not-found",
            L"\"key\":\"" + DevToolsJsonEscape(key) + L"\",\"prop\":\"" + DevToolsJsonEscape(prop) +
            L"\",\"restored\":" + (restored ? L"true" : L"false"));
    }

    SessionEditNote(wire, prop, DevToolsSessionEdit{ true, key, theme, L"", L"", out->after.value });
    BroadcastEditedProperty(conn, wire, prop, out->after.value);
    return DevToolsRpcResult(id, L"{\"prop\":\"" + DevToolsJsonEscape(prop) + L"\",\"key\":\"" + DevToolsJsonEscape(key) +
        L"\",\"kind\":\"" + (theme ? L"themeResource" : L"staticResource") +
        L"\",\"before\":\"" + DevToolsJsonEscape(before.value) + L"\",\"after\":\"" + DevToolsJsonEscape(out->after.value) +
        L"\",\"valueType\":\"" + DevToolsJsonEscape(out->after.type) +
        L"\",\"previousKind\":\"" + DevToolsPriorKindToken(prior) + L"\",\"captured\":" + (prior.captured ? L"true" : L"false") +
        L"," + DevToolsEditUndoMembers(std::to_wstring(wire), prop, prior) + L"}");
}

// The in-process window's "Replace entire binding", which is also how an {x:Bind} property is switched to {Binding}.
static std::wstring RpcBindingInstall(DevToolsConn* conn, const std::wstring& id, const DevToolsJson& p)
{
    const std::wstring prop = p.GetString(L"prop"), path = p.GetString(L"path"), mode = p.GetString(L"mode", L"OneWay");
    if (path.empty()) return DevToolsRpcError(id, DevToolsErr::InvalidParams, L"path is required", L"bad-args");
    if (const std::wstring why = DevToolsPathSyntax_Reason(path); !why.empty())
        return DevToolsRpcError(id, DevToolsErr::InvalidParams, why, L"bad-path");
    if (DevToolsBindingInstall_ModeOrdinal(mode) < 0)
        return DevToolsRpcError(id, DevToolsErr::InvalidParams, L"mode must be OneWay, OneTime or TwoWay", L"bad-args");
    unsigned long long wire = 0;
    if (std::wstring refused = EditTargetFromParams(id, p, &wire); !refused.empty()) return refused;
    DevToolsReadProp before;
    DevToolsEditPrior prior;
    if (std::wstring refused = ReadEditPrior(id, wire, prop, &before, &prior); !refused.empty()) return refused;
    CapturePriorBinding(wire, prop, &prior);

    const std::wstring json = CmdBindingInstall(wire, prop, path, mode);
    if (json.rfind(L"ERR ", 0) == 0) return BridgeErrToRpc(id, json);
    DevToolsJson answer;
    if (!DevToolsJsonParse(json, answer) || !answer.IsObject())
        return DevToolsRpcError(id, DevToolsErr::InternalError, L"the binding install returned no answer", L"internal");
    if (answer.GetString(L"state") != L"bound") return DevToolsRpcResult(id, json);
    if (answer.GetString(L"bound") != L"True")
        return DevToolsRpcResult(id, L"{\"state\":\"unavailable\",\"reason\":\"the binding did not install\"}");

    // The install's own read-back reports a binding as its expression handle, so read the value it produces instead.
    auto after = std::make_shared<std::wstring>();
    RunOnUiThread([wire, prop, after]() -> HRESULT {
        DevToolsBindingRow_ForgetLiveWrite(wire, prop);
        InstanceHandle ih = 0; const wchar_t* e = nullptr;
        { CensusSharedLock lock; if (!ResolveWire_nolock(wire, &ih, &e)) return E_INVALIDARG; }
        std::vector<DevToolsReadProp> props;
        const std::set<std::wstring> only{ prop };
        ReadCuratedProps(ih, &props, nullptr, nullptr, &only, nullptr, nullptr);
        for (const auto& r : props) if (r.name == prop) { *after = r.value; break; }
        return S_OK;
    });
    std::wstring warning = DevToolsBindingInstall_ReplacementWarning();
    if (const size_t cut = warning.find(L" A prior managed capture"); cut != std::wstring::npos) warning.resize(cut);
    SessionEditNote(wire, prop, DevToolsSessionEdit{ false, L"", false, path, mode, *after });
    BroadcastEditedProperty(conn, wire, prop, *after);
    return DevToolsRpcResult(id, L"{\"state\":\"bound\",\"path\":\"" + DevToolsJsonEscape(path) +
        L"\",\"mode\":\"" + DevToolsJsonEscape(mode) + L"\",\"before\":\"" + DevToolsJsonEscape(before.value) +
        L"\",\"after\":\"" + DevToolsJsonEscape(*after) + L"\",\"bound\":\"True\",\"previousKind\":\"" +
        DevToolsPriorKindToken(prior) + L"\",\"captured\":" + (prior.captured ? L"true" : L"false") +
        L",\"warning\":\"" + DevToolsJsonEscape(warning) + L"\"," +
        DevToolsEditUndoMembers(std::to_wstring(wire), prop, prior) + L"}");
}

// The window's "Write a test value": changes the TwoWay binding's source and keeps the binding installed.
static std::wstring RpcBindingWriteSource(DevToolsConn* conn, const std::wstring& id, const DevToolsJson& p)
{
    const std::wstring prop = p.GetString(L"prop");
    const DevToolsJson* value = p.Find(L"value");
    if (!value || value->type != DevToolsJsonType::String)
        return DevToolsRpcError(id, DevToolsErr::InvalidParams, L"value must be a string", L"bad-args");
    unsigned long long wire = 0;
    if (std::wstring refused = EditTargetFromParams(id, p, &wire); !refused.empty()) return refused;
    const std::wstring json = CmdBindingOp(wire, L"writesource", prop, value->str);
    if (json.rfind(L"ERR ", 0) == 0) return BridgeErrToRpc(id, json);
    DevToolsJson answer;
    if (!DevToolsJsonParse(json, answer) || !answer.IsObject() || answer.GetString(L"state") != L"wroteSource")
        return DevToolsRpcResult(id, json);
    BroadcastEditedProperty(conn, wire, prop, answer.GetString(L"after"));
    const DevToolsJson* sourceBefore = answer.Find(L"sourceBefore");
    const std::wstring undo = sourceBefore && sourceBefore->type == DevToolsJsonType::String
        ? L"\"undo\":{\"method\":\"Binding.writeSource\",\"params\":{" + DevToolsUndoTarget(std::to_wstring(wire), prop) +
          L",\"value\":\"" + DevToolsJsonEscape(sourceBefore->str) + L"\"}}"
        : L"\"undo\":null,\"undoReason\":\"the source's previous value cannot be written back as text\"";
    return DevToolsRpcResult(id, AppendMembers(json, undo));
}
