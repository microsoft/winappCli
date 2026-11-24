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

// A parameter the schema types as a string: absent is fine, any other JSON type is refused. Empty on success.
static std::wstring StringParam(const std::wstring& id, const DevToolsJson& p, const wchar_t* name, std::wstring* out)
{
    const DevToolsJson* value = p.Find(name);
    if (!value) return L"";
    if (value->type != DevToolsJsonType::String)
        return DevToolsRpcError(id, DevToolsErr::InvalidParams, std::wstring(name) + L" must be a string.", L"bad-args");
    if (out) *out = value->str;
    return L"";
}

// OneWay, OneTime or TwoWay in any letter case, spelled the way the runtime and the undo steps spell it.
static std::wstring CanonicalBindingMode(const std::wstring& mode)
{
    for (const wchar_t* known : { L"OneWay", L"OneTime", L"TwoWay" })
        if (_wcsicmp(mode.c_str(), known) == 0) return known;
    return L"";
}

// `handle` or `name`, addressed and scoped exactly like HotReload.setProperty. Empty on success.
static std::wstring EditTargetFromParams(const std::wstring& id, const DevToolsJson& p, unsigned long long* wire)
{
    for (const wchar_t* key : { L"handle", L"name", L"prop" })
        if (std::wstring refused = StringParam(id, p, key, nullptr); !refused.empty()) return refused;
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

// The runtime handle of the property's binding expression (empty when it has none) and the element's ActualTheme.
// A restore or another install creates a new expression, so the handle tells the expression DevTools installed apart.
static DevToolsEditObserved ObserveEditTarget_ui(InstanceHandle ih, const std::wstring& prop)
{
    DevToolsEditObserved observed;
    const unsigned int idx = FindPropIndex(ih, prop);
    unsigned int sc = 0, pc = 0;
    PropertyChainSource* s = nullptr; PropertyChainValue* v = nullptr;
    if (idx != 0xFFFFFFFF && g_vts3 && SUCCEEDED(ReadPropChain(ih, &sc, &s, &pc, &v)) && v) {
        for (unsigned int i = 0; i < pc; i++) {
            if (v[i].Index != idx || !((long long)v[i].MetadataBits & 0x10) || !v[i].Value) continue;
            observed.bindingId = v[i].Value;
            break;
        }
    }
    FreePropChain(sc, s, pc, v);
    IInspectable* insp = nullptr;
    if (g_diag && SUCCEEDED(g_diag->GetIInspectableFromHandle(ih, &insp)) && insp) {
        void* fe = nullptr;
        int theme = -1;
        if (SUCCEEDED(insp->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) && fe) {
            if (SUCCEEDED(DevToolsGetActualTheme(fe, &theme))) observed.elementTheme = theme;
            reinterpret_cast<IUnknown*>(fe)->Release();
        }
        insp->Release();
    }
    return observed;
}

// Reads the property's current row and classifies what an edit would replace. Empty on success.
static std::wstring ReadEditPrior(const std::wstring& id, unsigned long long wire, const std::wstring& prop,
                                  DevToolsReadProp* row, DevToolsEditPrior* prior)
{
    struct State { DevToolsReadProp row; DevToolsEditObserved observed; bool secret = false, settable = false, found = false; const wchar_t* error = nullptr; };
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
        st->observed = ObserveEditTarget_ui(ih, prop);
        return S_OK;
    }, &completed);
    if (completed && st->error) return BridgeErrToRpc(id, std::wstring(L"ERR ") + st->error);
    if (FAILED(hr)) return DevToolsRpcError(id, DevToolsErr::NotOnDispatcher, L"The app's UI thread did not answer.", L"ui-thread",
                                            L"\"hresult\":\"" + HrStr(hr) + L"\"");
    if (st->secret) return DevToolsRpcError(id, DevToolsErr::RefusedUnsafe,
        L"'" + prop + L"' holds a secret; DevTools does not read or write it.", L"refused-secret");
    if (!st->settable || !st->found) return DevToolsRpcError(id, DevToolsErr::InvalidParams,
        L"'" + prop + L"' is not a property this element can set", L"unknown-property");
    *row = std::move(st->row);
    DevToolsSessionEdit session;
    const bool known = SessionEditFind(wire, prop, &session);
    *prior = DevToolsClassifyPrior(*row, known ? &session : nullptr, st->observed);
    return L"";
}

// An app binding or {x:Bind} has no text form to put back, so save it with the managed agent first. Binding.restore
// then undoes the edit. Without the agent the edit still runs; its undo is null and says why.
static void CapturePriorBinding(unsigned long long wire, const std::wstring& prop, DevToolsEditPrior* prior)
{
    if (prior->kind != DevToolsPriorKind::Binding && prior->kind != DevToolsPriorKind::XBind) return;
    // Held by the agent until Binding.restore, so a later Binding.capture of the edited state cannot replace it.
    const std::wstring json = CmdBindingOp(wire, L"captureoriginal", prop);
    DevToolsJson answer;
    const bool parsed = json.rfind(L"ERR ", 0) != 0 && DevToolsJsonParse(json, answer) && answer.IsObject();
    const std::wstring kind = parsed ? answer.GetString(L"restoreKind") : std::wstring();
    prior->captured = parsed && answer.GetString(L"state") == L"captured" && (kind == L"binding" || kind == L"rebind");
    if (!prior->captured) {
        const std::wstring reason = parsed ? answer.GetString(L"reason") : std::wstring();
        prior->reason = L"the previous binding could not be captured" + (reason.empty() ? std::wstring() : L": " + reason);
    }
}

// An edit that did not happen leaves nothing for Binding.restore to undo, so let go of the original it saved.
static void ReleaseHeldOriginal(unsigned long long wire, const std::wstring& prop, const DevToolsEditPrior& prior)
{
    if (prior.captured) CmdBindingOp(wire, L"releaseoriginal", prop);
}

// A value can print a secret its path does not name: a record's text lists every member. The managed agent redacts
// such a value when it diagnoses the binding, so its answer says whether the installed binding would show one.
static bool InstalledBindingShowsSecret(unsigned long long wire, const std::wstring& prop)
{
    DevToolsJson answer;
    const std::wstring json = CmdBindingOp(wire, L"diagnose", prop);
    return json.rfind(L"ERR ", 0) != 0 && DevToolsJsonParse(json, answer) && answer.IsObject() &&
           answer.GetBool(L"redacted", false);
}

static std::wstring HandleRpc(DevToolsConn* conn, const DevToolsRpcRequest& req);

// Runs an edit's `undo` steps here, in order, the way a client would. True when there were steps and none failed.
static bool RunUndoSteps(DevToolsConn* conn, const std::wstring& undoMembers)
{
    DevToolsJson parsed;
    if (!DevToolsJsonParse(L"{" + undoMembers + L"}", parsed) || !parsed.IsObject()) return false;
    const DevToolsJson* steps = parsed.Find(L"undo");
    if (!steps || steps->type != DevToolsJsonType::Array || steps->arr.empty()) return false;
    for (const DevToolsJson& step : steps->arr) {
        DevToolsRpcRequest request;
        request.parsed = request.valid = request.hasId = true;
        request.idRaw = L"0";
        request.method = step.GetString(L"method");
        if (const DevToolsJson* params = step.Find(L"params")) request.params = *params;
        DevToolsJson reply;
        if (!DevToolsJsonParse(HandleRpc(conn, request), reply) || !reply.IsObject() || reply.Find(L"error")) return false;
    }
    return true;
}

// UI thread. The keys the inspector would suggest for `prop`, shared by Resource.list and Resource.assign.
// False when the element has no such property.
static bool CollectResourceKeys_ui(InstanceHandle ih, const std::wstring& prop, DevToolsResourceKeyList* list,
                                   std::wstring* type)
{
    std::vector<DevToolsReadProp> props;
    if (FAILED(ReadCuratedProps(ih, &props, nullptr))) return false;
    bool found = false;
    for (const auto& r : props) if (r.name == prop) { *type = r.type; found = true; break; }
    if (!found) return false;
    const std::wstring family = DevToolsResourceKeys_TypeFamily(*type);
    if (family.empty()) return true;
    try {
        winrt::com_ptr<IInspectable> raw;
        if (g_diag) g_diag->GetIInspectableFromHandle(ih, raw.put());
        winrt::Windows::Foundation::IInspectable selected;
        if (raw) winrt::copy_from_abi(selected, raw.get());
        DevToolsResourceKeys_Collect(selected, family, *list);
    } catch (const winrt::hresult_error& error) {
        list->partial = true;
        Log(L"resource.list enumeration failed hr=0x%08X", static_cast<unsigned>(error.code()));
    }
    for (const auto& r : props) {
        if (r.authoredKind != L"themeResource" && r.authoredKind != L"staticResource") continue;
        if (DevToolsResourceKeys_TypeFamily(r.type) == family) DevToolsResourceKeys_Add(r.authoredKey, *list);
    }
    DevToolsResourceKeys_AddDefaults(family, *list);
    return true;
}

// UI thread. Records a key assigned to `prop` (by Resource.assign or the in-app inspector), so a later protocol edit
// can undo back to the key rather than the value it produced.
static void SessionEditNoteResource_ui(InstanceHandle ih, unsigned long long wire, const std::wstring& prop,
                                       const std::wstring& key, bool theme, const std::wstring& after)
{
    DevToolsSessionEdit record;
    record.resource = true; record.key = key; record.theme = theme; record.after = after;
    record.elementTheme = ObserveEditTarget_ui(ih, prop).elementTheme;
    SessionEditNote(wire, prop, record);
}

// UI thread. The text a property shows now, as Property.get reads it.
static DevToolsReadProp ReadEditedRow_ui(InstanceHandle ih, const std::wstring& prop)
{
    std::vector<DevToolsReadProp> props;
    const std::set<std::wstring> only{ prop };
    ReadCuratedProps(ih, &props, nullptr, nullptr, &only, nullptr, nullptr);
    for (auto& r : props) if (r.name == prop) return std::move(r);
    return {};
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
    std::wstring prop, key;
    if (std::wstring refused = StringParam(id, p, L"key", &key); !refused.empty()) return refused;
    prop = p.GetString(L"prop");
    const DevToolsJson* themeParam = p.Find(L"theme");
    if (themeParam && themeParam->type != DevToolsJsonType::Bool)
        return DevToolsRpcError(id, DevToolsErr::InvalidParams, L"theme must be a boolean.", L"bad-args");
    const bool theme = themeParam && themeParam->b;
    if (key.empty()) return DevToolsRpcError(id, DevToolsErr::InvalidParams, L"key is required.", L"bad-args");
    unsigned long long wire = 0;
    if (std::wstring refused = EditTargetFromParams(id, p, &wire); !refused.empty()) return refused;
    DevToolsReadProp before;
    DevToolsEditPrior prior;
    if (std::wstring refused = ReadEditPrior(id, wire, prop, &before, &prior); !refused.empty()) return refused;
    CapturePriorBinding(wire, prop, &prior);

    // A failed lookup clears the property. With no saved copy of the binding it could not be put back, so only try
    // a key the inspector can see in scope (no managed agent, as in a C++ app, or a binding it could not capture).
    if (prior.kind == DevToolsPriorKind::Binding && !prior.captured) {
        struct Keys { DevToolsResourceKeyList list; std::wstring type; };
        auto keys = std::make_shared<Keys>();
        RunOnUiThread([wire, prop, keys]() -> HRESULT {
            InstanceHandle ih = 0; const wchar_t* e = nullptr;
            { CensusSharedLock lock; if (!ResolveWire_nolock(wire, &ih, &e)) return E_INVALIDARG; }
            CollectResourceKeys_ui(ih, prop, &keys->list, &keys->type);
            return S_OK;
        });
        if (std::find(keys->list.keys.begin(), keys->list.keys.end(), key) == keys->list.keys.end())
            return DevToolsRpcError(id, DevToolsErr::NotFound,
                L"DevTools cannot find resource '" + key + L"' for " + prop + L", and " + prior.reason +
                L", so a failed lookup would remove the binding for good. Nothing was changed. To assign the key "
                L"anyway, clear the property first with HotReload.clearProperty.",
                L"resource-unconfirmed", L"\"key\":\"" + DevToolsJsonEscape(key) + L"\",\"prop\":\"" + DevToolsJsonEscape(prop) + L"\"");
    }

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
        out->after = ReadEditedRow_ui(ih, prop);
        SessionEditNoteResource_ui(ih, wire, prop, key, theme, out->after.value);
        return S_OK;
    }, &completed);
    if (completed && out->error) return BridgeErrToRpc(id, std::wstring(L"ERR ") + out->error);
    if (FAILED(hr)) return DevToolsRpcError(id, DevToolsErr::NotOnDispatcher, L"The app's UI thread did not answer.", L"ui-thread",
                                            L"\"hresult\":\"" + HrStr(hr) + L"\"");
    if (!out->applied) {
        bool restored = out->restored;
        if (out->bindingLost && prior.kind == DevToolsPriorKind::InstalledBinding) {
            // A binding DevTools installed has no capture; install the same path and mode again.
            DevToolsJson answer;
            const std::wstring json = CmdBindingInstall(wire, prop, prior.path, prior.mode);
            restored = DevToolsJsonParse(json, answer) && answer.IsObject() && answer.GetString(L"bound") == L"True";
            if (restored) {
                DevToolsSessionEdit edit;
                if (SessionEditFind(wire, prop, &edit)) { edit.bindingId = answer.GetString(L"after"); SessionEditNote(wire, prop, edit); }
            }
        } else if (out->bindingLost && prior.captured) {
            DevToolsJson answer;
            const std::wstring json = CmdBindingOp(wire, L"restore", prop);
            restored = DevToolsJsonParse(json, answer) && answer.IsObject() && answer.GetString(L"state") == L"restored";
        }
        return DevToolsRpcError(id, DevToolsErr::NotFound,
            L"Resource '" + key + L"' was not found or could not be assigned to " + prop + L". " +
            (restored ? std::wstring(L"The previous value was restored. Check the key and resource type.")
                      : L"The previous value could not be restored" +
                        (before.value.empty() ? std::wstring(L".") : L"; it was '" + before.value + L"'.") +
                        L" Set the property again."),
            L"resource-not-found",
            L"\"key\":\"" + DevToolsJsonEscape(key) + L"\",\"prop\":\"" + DevToolsJsonEscape(prop) +
            L"\",\"restored\":" + (restored ? L"true" : L"false"));
    }

    BroadcastEditedProperty(conn, wire, prop, out->after.value);
    return DevToolsRpcResult(id, L"{\"prop\":\"" + DevToolsJsonEscape(prop) + L"\",\"key\":\"" + DevToolsJsonEscape(key) +
        L"\",\"kind\":\"" + (theme ? L"themeResource" : L"staticResource") +
        L"\",\"before\":\"" + DevToolsJsonEscape(before.value) + L"\",\"after\":\"" + DevToolsJsonEscape(out->after.value) +
        L"\",\"valueType\":\"" + DevToolsJsonEscape(out->after.type) +
        L"\",\"previousKind\":\"" + DevToolsPriorKindToken(prior) + L"\",\"captured\":" + (prior.captured ? L"true" : L"false") +
        L"," + DevToolsEditUndoMembers(std::to_wstring(wire), prop, prior) + L"}");
}

// A binding edit that could not happen is an error, like a failed Resource.assign; `reason` says why.
static std::wstring BindingEditUnavailable(const std::wstring& id, const std::wstring& lead, const std::wstring& reason)
{
    std::wstring sentence = reason.empty() ? std::wstring(L"the app gave no reason") : reason;
    if (sentence.back() == L'.') sentence.pop_back();
    return DevToolsRpcError(id, DevToolsErr::CapabilityUnsupported, lead + sentence + L". Nothing was changed.",
                            L"binding-unavailable", L"\"reason\":\"" + DevToolsJsonEscape(reason) + L"\"");
}

// The in-process window's "Replace entire binding", which is also how an {x:Bind} property is switched to {Binding}.
static std::wstring RpcBindingInstall(DevToolsConn* conn, const std::wstring& id, const DevToolsJson& p)
{
    std::wstring path, modeText = L"OneWay";
    if (std::wstring refused = StringParam(id, p, L"path", &path); !refused.empty()) return refused;
    if (std::wstring refused = StringParam(id, p, L"mode", &modeText); !refused.empty()) return refused;
    const std::wstring prop = p.GetString(L"prop"), mode = CanonicalBindingMode(modeText);
    if (path.empty()) return DevToolsRpcError(id, DevToolsErr::InvalidParams, L"path is required.", L"bad-args");
    if (const std::wstring why = DevToolsPathSyntax_Reason(path); !why.empty())
        return DevToolsRpcError(id, DevToolsErr::InvalidParams, why, L"bad-path");
    if (mode.empty())
        return DevToolsRpcError(id, DevToolsErr::InvalidParams, L"mode must be OneWay, OneTime or TwoWay.", L"bad-args");
    if (DevToolsPathNamesSecret(path))
        return DevToolsRpcError(id, DevToolsErr::RefusedUnsafe,
            L"'" + path + L"' reads a secret; DevTools does not bind a property to it.", L"refused-secret");
    unsigned long long wire = 0;
    if (std::wstring refused = EditTargetFromParams(id, p, &wire); !refused.empty()) return refused;
    DevToolsReadProp before;
    DevToolsEditPrior prior;
    if (std::wstring refused = ReadEditPrior(id, wire, prop, &before, &prior); !refused.empty()) return refused;
    CapturePriorBinding(wire, prop, &prior);

    const std::wstring json = CmdBindingInstall(wire, prop, path, mode);
    DevToolsJson answer;
    const bool parsed = json.rfind(L"ERR ", 0) != 0 && DevToolsJsonParse(json, answer) && answer.IsObject();
    if (!parsed || answer.GetString(L"state") != L"bound" || answer.GetString(L"bound") != L"True") {
        ReleaseHeldOriginal(wire, prop, prior);
        if (json.rfind(L"ERR ", 0) == 0) return BridgeErrToRpc(id, json);
        if (!parsed) return DevToolsRpcError(id, DevToolsErr::InternalError, L"The binding install returned no answer.", L"internal");
        return BindingEditUnavailable(id, L"The binding was not installed: ", answer.GetString(L"reason", L"the binding did not install"));
    }
    const std::wstring undo = DevToolsEditUndoMembers(std::to_wstring(wire), prop, prior, /*editLeftBinding*/ true);

    // Checked before anything reports the new value, so a refused binding never leaves the app.
    if (InstalledBindingShowsSecret(wire, prop)) {
        bool restored = RunUndoSteps(conn, undo);
        if (!restored) RunUndoSteps(conn, L"\"undo\":[" + DevToolsUndoStep(L"HotReload.clearProperty",
                                                                         DevToolsUndoTarget(std::to_wstring(wire), prop)) + L"]");
        return DevToolsRpcError(id, DevToolsErr::RefusedUnsafe,
            L"'" + path + L"' shows a secret: its value prints a member whose name ends in Password. " +
            (restored ? std::wstring(L"DevTools put the previous value back.")
                      : std::wstring(L"DevTools could not put the previous value back, so it cleared the property.")),
            L"refused-secret", L"\"restored\":" + std::wstring(restored ? L"true" : L"false"));
    }

    // The install's own read-back reports a binding as its expression handle, so read the value it produces instead.
    // A timed-out UI turn can still be writing, so read nothing from it unless it finished.
    struct Read { std::wstring value; };
    auto read = std::make_shared<Read>();
    const HRESULT readHr = RunOnUiThread([wire, prop, read]() -> HRESULT {
        DevToolsBindingRow_ForgetLiveWrite(wire, prop);
        InstanceHandle ih = 0; const wchar_t* e = nullptr;
        { CensusSharedLock lock; if (!ResolveWire_nolock(wire, &ih, &e)) return E_INVALIDARG; }
        read->value = ReadEditedRow_ui(ih, prop).value;
        return S_OK;
    });
    if (FAILED(readHr)) return DevToolsRpcError(id, DevToolsErr::NotOnDispatcher,
        L"The binding was installed, but the app's UI thread did not answer the read-back.", L"ui-thread",
        L"\"hresult\":\"" + HrStr(readHr) + L"\"");
    const std::wstring after = read->value;
    DevToolsSessionEdit record;
    record.path = path; record.mode = mode; record.bindingId = answer.GetString(L"after");
    SessionEditNote(wire, prop, record);
    BroadcastEditedProperty(conn, wire, prop, after);
    return DevToolsRpcResult(id, L"{\"state\":\"bound\",\"path\":\"" + DevToolsJsonEscape(path) +
        L"\",\"mode\":\"" + DevToolsJsonEscape(mode) + L"\",\"before\":\"" + DevToolsJsonEscape(before.value) +
        L"\",\"after\":\"" + DevToolsJsonEscape(after) + L"\",\"bound\":\"True\",\"previousKind\":\"" +
        DevToolsPriorKindToken(prior) + L"\",\"captured\":" + (prior.captured ? L"true" : L"false") +
        L",\"warning\":\"" + DevToolsJsonEscape(DevToolsBindingInstall_ReplacementWarningCore()) + L"\"," + undo + L"}");
}

// The window's "Write a test value": changes the TwoWay binding's source and keeps the binding installed.
static std::wstring RpcBindingWriteSource(DevToolsConn* conn, const std::wstring& id, const DevToolsJson& p)
{
    const std::wstring prop = p.GetString(L"prop");
    const DevToolsJson* value = p.Find(L"value");
    if (!value || value->type != DevToolsJsonType::String)
        return DevToolsRpcError(id, DevToolsErr::InvalidParams, L"value must be a string.", L"bad-args");
    unsigned long long wire = 0;
    if (std::wstring refused = EditTargetFromParams(id, p, &wire); !refused.empty()) return refused;
    const std::wstring json = CmdBindingOp(wire, L"writesource", prop, value->str);
    if (json.rfind(L"ERR ", 0) == 0) return BridgeErrToRpc(id, json);
    DevToolsJson answer;
    if (!DevToolsJsonParse(json, answer) || !answer.IsObject())
        return DevToolsRpcError(id, DevToolsErr::InternalError, L"The source write returned no answer.", L"internal");
    if (answer.GetString(L"state") != L"wroteSource")
        return BindingEditUnavailable(id, L"The source was not written: ", answer.GetString(L"reason"));
    BroadcastEditedProperty(conn, wire, prop, answer.GetString(L"after"));
    return DevToolsRpcResult(id, AppendMembers(json, DevToolsWriteSourceUndoMembers(answer.Find(L"sourceBefore") != nullptr)));
}

// The in-app inspector's key suggestions: keys whose values fit the property, from the element's dictionaries up to
// the application's, then keys the element's XAML already uses, then the WinUI theme brushes.
static std::wstring RpcResourceList(const std::wstring& id, const DevToolsJson& p)
{
    const std::wstring prop = p.GetString(L"prop");
    unsigned long long wire = 0;
    if (std::wstring refused = EditTargetFromParams(id, p, &wire); !refused.empty()) return refused;
    struct State { DevToolsResourceKeyList list; std::wstring type; bool found = false; const wchar_t* error = nullptr; };
    auto st = std::make_shared<State>();
    bool completed = false;
    const HRESULT hr = RunOnUiThread([wire, prop, st]() -> HRESULT {
        InstanceHandle ih = 0;
        { CensusSharedLock lock; if (!ResolveWire_nolock(wire, &ih, &st->error)) return E_INVALIDARG; }
        st->found = CollectResourceKeys_ui(ih, prop, &st->list, &st->type);
        return S_OK;
    }, &completed);
    if (completed && st->error) return BridgeErrToRpc(id, std::wstring(L"ERR ") + st->error);
    if (FAILED(hr)) return DevToolsRpcError(id, DevToolsErr::NotOnDispatcher, L"The app's UI thread did not answer.", L"ui-thread",
                                            L"\"hresult\":\"" + HrStr(hr) + L"\"");
    if (!st->found) return DevToolsRpcError(id, DevToolsErr::InvalidParams,
        L"'" + prop + L"' is not a property of this element", L"unknown-property");
    std::wstring keys;
    for (const auto& k : st->list.keys) keys += (keys.empty() ? L"\"" : L",\"") + DevToolsJsonEscape(k) + L"\"";
    return DevToolsRpcResult(id, L"{\"prop\":\"" + DevToolsJsonEscape(prop) + L"\",\"valueType\":\"" +
        DevToolsJsonEscape(st->type) + L"\",\"keys\":[" + keys + L"],\"partial\":" + (st->list.partial ? L"true" : L"false") + L"}");
}