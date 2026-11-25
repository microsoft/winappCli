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

// A literal write forgets a resource record: the value is no longer the key's. A binding record checks itself (by the
// expression's runtime handle), and a TwoWay binding keeps its expression through a write, so it is kept then.
static void SessionEditForget(unsigned long long wire, const std::wstring& prop, bool resourceOnly)
{
    std::lock_guard<std::mutex> lock(g_sessionEditsMutex);
    const auto found = g_sessionEdits.find({ wire, prop });
    if (found != g_sessionEdits.end() && (!resourceOnly || found->second.resource)) g_sessionEdits.erase(found);
}

// UI thread. The element's ActualTheme, or -1 when it has none.
static int ElementTheme_ui(InstanceHandle ih)
{
    int theme = -1;
    IInspectable* insp = nullptr;
    if (g_diag && SUCCEEDED(g_diag->GetIInspectableFromHandle(ih, &insp)) && insp) {
        void* fe = nullptr;
        if (SUCCEEDED(insp->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) && fe) {
            if (FAILED(DevToolsGetActualTheme(fe, &theme))) theme = -1;
            reinterpret_cast<IUnknown*>(fe)->Release();
        }
        insp->Release();
    }
    return theme;
}

// UI thread. Records a key assigned to `prop` (by Resource.assign or the in-app inspector), so a later protocol edit
// can undo back to the key rather than the value it produced, even after a theme change alters that value.
static void SessionEditNoteResource_ui(InstanceHandle ih, unsigned long long wire, const std::wstring& prop,
                                       const std::wstring& key, bool theme, const std::wstring& after,
                                       bool byWindow = false)
{
    DevToolsSessionEdit record;
    record.resource = true; record.key = key; record.theme = theme; record.after = after; record.byWindow = byWindow;
    record.elementTheme = ElementTheme_ui(ih);
    record.highContrast = DevToolsResourceKeys_HighContrast() ? 1 : 0;
    SessionEditNote(wire, prop, record);
}

static void SessionEditNoteBinding(unsigned long long wire, const std::wstring& prop, const std::wstring& path,
                                   const std::wstring& mode, const std::wstring& bindingId)
{
    DevToolsSessionEdit record;
    record.path = path; record.mode = mode; record.bindingId = bindingId;
    SessionEditNote(wire, prop, record);
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
        return DevToolsRpcError(id, DevToolsErr::InvalidParams, L"handle must be a canonical non-zero decimal string.", L"bad-args");
    if (handle.empty() && name.empty())
        return DevToolsRpcError(id, DevToolsErr::InvalidParams, L"Either handle or name is required.", L"bad-args");
    if (p.GetString(L"prop").empty())
        return DevToolsRpcError(id, DevToolsErr::InvalidParams, L"prop is required.", L"bad-args");
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
    observed.elementTheme = ElementTheme_ui(ih);
    observed.highContrast = DevToolsResourceKeys_HighContrast() ? 1 : 0;
    return observed;
}

// UI thread. The value `key` gives where the element looks it up (its dictionaries, its ancestors', the app's), or
// null when DevTools cannot find it there.
static winrt::Windows::Foundation::IInspectable KeyValueInScope_ui(InstanceHandle ih, const std::wstring& key)
{
    if (!g_diag || key.empty()) return nullptr;
    try {
        winrt::com_ptr<IInspectable> raw;
        g_diag->GetIInspectableFromHandle(ih, raw.put());
        winrt::Windows::Foundation::IInspectable selected{ nullptr };
        if (raw) winrt::copy_from_abi(selected, raw.get());
        return DevToolsResourceKeys_Find(selected, key);
    } catch (const winrt::hresult_error&) {
        return nullptr;
    }
}

// UI thread. Whether the property's local value is still the one `key` gives where the element looks it up: the same
// object, or for a value type the same text. App code may have set something else since. 1 yes, 0 no, -1 when
// DevTools cannot find the key's value to compare.
static int KeyMatch_ui(InstanceHandle ih, const std::wstring& prop, const DevToolsReadProp& row, const std::wstring& key)
{
    const unsigned int idx = FindPropIndex(ih, prop);
    if (idx == 0xFFFFFFFF) return -1;
    const winrt::Windows::Foundation::IInspectable resource = KeyValueInScope_ui(ih, key);
    if (!resource) return -1;
    const PriorLocalValue local = ReadPriorLocalValue(ih, idx);
    if (!local.had || !local.restorable) return -1;
    if (local.object) {
        winrt::com_ptr<IInspectable> current;
        if (FAILED(g_diag->GetIInspectableFromHandle(local.object, current.put())) || !current) return -1;
        return current.as<::IUnknown>().get() == resource.as<::IUnknown>().get() ? 1 : 0;
    }
    std::wstring text;
    if (ReadInspectableValue(static_cast<IInspectable*>(winrt::get_abi(resource)), prop, row.type, &text, nullptr) != S_OK)
        return -1;
    return DevToolsRead_NormalizeColor(text) == DevToolsRead_NormalizeColor(row.value) ? 1 : 0;
}

// What an edit is about to replace, read on the UI thread: the property's row, how to put it back, and its exact
// local value (a last resort for rollback). Shared by the protocol edits and the in-app window.
struct DevToolsEditPriorRead
{
    DevToolsReadProp  row;
    DevToolsEditPrior prior;
    PriorLocalValue   local;
    HRESULT           hr = S_OK;
    bool              completed = false, secret = false, settable = false, found = false;
    const wchar_t*    error = nullptr;
};

static DevToolsEditPriorRead ReadEditPriorCore(unsigned long long wire, const std::wstring& prop)
{
    struct State { DevToolsEditPriorRead read; DevToolsEditObserved observed; DevToolsSessionEdit session; bool known = false; };
    auto st = std::make_shared<State>();
    bool completed = false;
    const HRESULT hr = RunOnUiThread([wire, prop, st]() -> HRESULT {
        DevToolsEditPriorRead& r = st->read;
        InstanceHandle ih = 0;
        { CensusSharedLock lock; if (!ResolveWire_nolock(wire, &ih, &r.error)) return E_INVALIDARG; }
        if (DevToolsRead_IsSecretProperty(prop)) { r.secret = true; return S_OK; }
        const unsigned int idx = FindPropIndex(ih, prop);
        if (idx == 0xFFFFFFFF) return S_OK;
        r.settable = true;
        std::vector<DevToolsReadProp> props;
        const HRESULT read = ReadCuratedProps(ih, &props, nullptr);
        if (FAILED(read)) return read;
        for (auto& row : props) if (row.name == prop) { r.row = std::move(row); r.found = true; break; }
        r.local = ReadPriorLocalValue(ih, idx);
        st->observed = ObserveEditTarget_ui(ih, prop);
        if (r.found && !r.row.authoredKey.empty() &&
            (r.row.authoredKind == L"staticResource" || r.row.authoredKind == L"themeResource"))
            st->observed.authoredKeyMatch = KeyMatch_ui(ih, prop, r.row, r.row.authoredKey);
        st->known = SessionEditFind(wire, prop, &st->session);
        if (r.found && st->known && st->session.resource)
            st->observed.sessionKeyMatch = KeyMatch_ui(ih, prop, r.row, st->session.key);
        return S_OK;
    }, &completed);
    // A timed-out turn can still run later and write `st`, so nothing is read from it then.
    DevToolsEditPriorRead out;
    if (completed) out = std::move(st->read);
    out.hr = hr;
    out.completed = completed;
    if (completed && SUCCEEDED(hr) && out.found)
        out.prior = DevToolsClassifyPrior(out.row, st->known ? &st->session : nullptr, st->observed);
    return out;
}

// Reads the property's current row and classifies what an edit would replace. Empty on success.
static std::wstring ReadEditPrior(const std::wstring& id, unsigned long long wire, const std::wstring& prop,
                                  DevToolsReadProp* row, DevToolsEditPrior* prior, PriorLocalValue* local = nullptr)
{
    DevToolsEditPriorRead r = ReadEditPriorCore(wire, prop);
    if (r.completed && r.error) return BridgeErrToRpc(id, std::wstring(L"ERR ") + r.error);
    if (!r.completed || FAILED(r.hr)) return DevToolsRpcError(id, DevToolsErr::NotOnDispatcher,
        L"The app's UI thread did not answer.", L"ui-thread", L"\"hresult\":\"" + HrStr(r.hr) + L"\"");
    if (r.secret) return DevToolsRpcError(id, DevToolsErr::RefusedUnsafe,
        L"'" + prop + L"' holds a secret; DevTools does not read or write it.", L"refused-secret");
    if (!r.settable || !r.found) return DevToolsRpcError(id, DevToolsErr::InvalidParams,
        L"'" + prop + L"' is not a property this element can set.", L"unknown-property");
    *row = std::move(r.row);
    *prior = std::move(r.prior);
    if (local) *local = r.local;
    return L"";
}

// An app binding or {x:Bind} has no text form to put back, so save it with the managed agent first. Binding.restore
// then undoes the edit. Without the agent the edit still runs; its undo is empty and says why.
static void CapturePriorBinding(unsigned long long wire, const std::wstring& prop, DevToolsEditPrior* prior)
{
    if (prior->kind != DevToolsPriorKind::Binding && prior->kind != DevToolsPriorKind::XBind) return;
    // Held by the agent until Binding.restore, so a later Binding.capture of the edited state cannot replace it.
    const std::wstring json = CmdBindingOp(wire, L"captureoriginal", prop);
    DevToolsJson answer;
    const bool parsed = json.rfind(L"ERR ", 0) != 0 && DevToolsJsonParse(json, answer) && answer.IsObject();
    const std::wstring kind = parsed ? answer.GetString(L"restoreKind") : std::wstring();
    prior->captured = parsed && answer.GetString(L"state") == L"captured" && (kind == L"binding" || kind == L"rebind");
    prior->heldEarlier = prior->captured && answer.GetString(L"heldOriginal") == L"True";
    // A TwoWay {x:Bind} writes the edited value into the app's data; no restore undoes that, so the result says so.
    if (parsed && prior->kind == DevToolsPriorKind::XBind) prior->warning = answer.GetString(L"editWarning");
    if (prior->captured) return;
    if (!DevToolsBindingRelay_AgentPresent()) {
        prior->reason = L"the app has no managed DevTools agent to save the binding " + prop + L" has now";
        return;
    }
    std::wstring reason = parsed ? answer.GetString(L"reason") : std::wstring();
    while (!reason.empty() && (reason.back() == L'.' || iswspace(reason.back()))) reason.pop_back();
    prior->reason = L"DevTools could not save the binding " + prop + L" has now" +
                    (reason.empty() ? std::wstring() : L" (" + reason + L")");
}

// An edit that did not happen leaves nothing for Binding.restore to undo, so let go of the original it saved. One an
// earlier edit saved still undoes that edit, so it stays.
static void ReleaseHeldOriginal(unsigned long long wire, const std::wstring& prop, const DevToolsEditPrior& prior)
{
    if (prior.captured && !prior.heldEarlier) CmdBindingOp(wire, L"releaseoriginal", prop);
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

// The in-app window's view of a key a protocol client assigned, so it edits and undoes that key, not the XAML's. A key
// the window assigned itself is left to the window's own record. UI thread, like the window. The same check as a
// protocol edit's undo: the property must still hold that key's value.
static bool WindowSessionKeyBridge(InstanceHandle wire, const std::wstring& prop, std::wstring* key, bool* theme)
{
    DevToolsSessionEdit record;
    if (!key || !theme || !SessionEditFind((unsigned long long)wire, prop, &record) || !record.resource || record.byWindow)
        return false;
    InstanceHandle ih = 0; const wchar_t* e = nullptr;
    { CensusSharedLock lock; if (!ResolveWire_nolock((unsigned long long)wire, &ih, &e)) return false; }
    const DevToolsReadProp row = ReadEditedRow_ui(ih, prop);
    DevToolsEditObserved observed = ObserveEditTarget_ui(ih, prop);
    observed.sessionKeyMatch = KeyMatch_ui(ih, prop, row, record.key);
    const DevToolsEditPrior now = DevToolsClassifyPrior(row, &record, observed);
    if (now.kind != DevToolsPriorKind::Resource || now.key != record.key) return false;
    *key = record.key;
    *theme = record.theme;
    return true;
}

// After an install: the value the new binding shows, and whether it prints a secret its path does not name (a
// record's text lists every member). Checks the text, then the managed agent's redaction, which also sees a declared
// type whose value is null now. Not on the UI thread. False when the UI thread did not answer.
static bool ReadInstalledValue(unsigned long long wire, const std::wstring& prop, std::wstring* value, bool* secret)
{
    auto read = std::make_shared<std::wstring>();
    bool completed = false;
    const HRESULT hr = RunOnUiThread([wire, prop, read]() -> HRESULT {
        DevToolsBindingRow_ForgetLiveWrite(wire, prop);
        InstanceHandle ih = 0; const wchar_t* e = nullptr;
        { CensusSharedLock lock; if (!ResolveWire_nolock(wire, &ih, &e)) return E_INVALIDARG; }
        *read = ReadEditedRow_ui(ih, prop).value;
        return S_OK;
    }, &completed);
    if (!completed || FAILED(hr)) return false;
    *value = *read;
    *secret = DevToolsTextPrintsSecret(*read);
    if (!*secret) {
        DevToolsJson answer;
        const std::wstring json = CmdBindingOp(wire, L"diagnose", prop);
        *secret = json.rfind(L"ERR ", 0) != 0 && DevToolsJsonParse(json, answer) && answer.IsObject() &&
                  answer.GetBool(L"redacted", false);
    }
    return true;
}

// Runs `step` against the live element on the UI thread. True only when it ran and succeeded.
static bool EditStepOnUi(unsigned long long wire, std::function<HRESULT(InstanceHandle)> step)
{
    bool completed = false;
    const HRESULT hr = RunOnUiThread([wire, step]() -> HRESULT {
        InstanceHandle ih = 0; const wchar_t* e = nullptr;
        { CensusSharedLock lock; if (!ResolveWire_nolock(wire, &ih, &e)) return E_INVALIDARG; }
        const HRESULT result = step(ih);
        ClearOriginatedError();
        return result;
    }, &completed);
    return completed && SUCCEEDED(hr);
}

struct DevToolsRollback { bool cleared = false, restored = false, ownerRefreshed = false; };

// Puts back what an edit replaced, for a refused install and a failed Resource.assign alike. Called directly, not
// through the protocol, so nothing saves the refused binding as the app's own. Clears first, so nothing is written
// through it; then the specific route for what was there; then the exact local value it read beforehand.
static DevToolsRollback RollBackEdit(unsigned long long wire, const std::wstring& prop, const DevToolsEditPrior& prior,
                                     const PriorLocalValue& local)
{
    DevToolsRollback out;
    out.cleared = EditStepOnUi(wire, [prop](InstanceHandle ih) { return ClearPropertyByPath(ih, prop); });
    if (!out.cleared) return out;
    DevToolsJson answer;
    switch (prior.kind) {
        case DevToolsPriorKind::Unset:
            out.restored = true;
            break;
        case DevToolsPriorKind::Literal:
            // The exact object first, so a brush shared with other elements stays shared; then its text.
            if (local.had && local.restorable)
                out.restored = EditStepOnUi(wire, [prop, local](InstanceHandle ih) {
                    return RestorePriorLocalValue(ih, FindPropIndex(ih, prop), local);
                });
            if (!out.restored)
                out.restored = EditStepOnUi(wire, [prop, prior](InstanceHandle ih) {
                    return SetLiteral(ih, prop, prior.writeType, prior.value);
                });
            break;
        case DevToolsPriorKind::Resource:
            out.restored = EditStepOnUi(wire, [wire, prop, prior](InstanceHandle ih) {
                const HRESULT hr = ResolveResourceApply(ih, prop, prior.key, prior.theme);
                if (SUCCEEDED(hr)) SessionEditNoteResource_ui(ih, wire, prop, prior.key, prior.theme, ReadEditedRow_ui(ih, prop).value);
                return hr;
            });
            break;
        case DevToolsPriorKind::InstalledBinding: {
            const std::wstring json = CmdBindingInstall(wire, prop, prior.path, prior.mode);
            out.restored = DevToolsJsonParse(json, answer) && answer.IsObject() && answer.GetString(L"bound") == L"True";
            if (out.restored) SessionEditNoteBinding(wire, prop, prior.path, prior.mode, answer.GetString(L"after"));
            break;
        }
        case DevToolsPriorKind::Binding:
            if (prior.captured)
                out.restored = DevToolsJsonParse(CmdBindingOp(wire, L"restore", prop), answer) && answer.IsObject() &&
                               answer.GetString(L"state") == L"restored";
            break;
        default:
            break;
    }
    // An {x:Bind} value, a value with no text form, or a literal that would not parse: the exact value read before
    // the edit. For {x:Bind} that avoids refreshing the whole page, which would reset other live edits on it.
    if (!out.restored && local.had && local.restorable)
        out.restored = EditStepOnUi(wire, [prop, local](InstanceHandle ih) {
            return RestorePriorLocalValue(ih, FindPropIndex(ih, prop), local);
        });
    if (prior.kind == DevToolsPriorKind::XBind && prior.captured && !out.restored)
        out.restored = out.ownerRefreshed = DevToolsJsonParse(CmdBindingOp(wire, L"restoreconfirmed", prop), answer) &&
                                            answer.IsObject() && answer.GetString(L"ownerUpdated") == L"True";
    // Back as it was: the saved original has nothing left to undo. Otherwise Binding.restore may still need it.
    if (out.restored) ReleaseHeldOriginal(wire, prop, prior);
    return out;
}

// How a rollback ended, as the end of an error message.
static std::wstring RollbackSentence(const DevToolsRollback& back)
{
    if (!back.cleared) return L"DevTools could not remove the new value, so it is still in place.";
    if (back.ownerRefreshed)
        return L"DevTools refreshed this page's compiled bindings to put the previous value back, which also resets "
               L"other live edits on this page.";
    return back.restored ? L"DevTools put the previous value back."
                         : L"DevTools could not put the previous value back, so it cleared the property.";
}

// UI thread. Keys for `prop`: with `scannedOnly`, just those found in the element's dictionaries, its ancestors' and
// the application's; otherwise also keys its XAML uses and the WinUI theme brushes, as the inspector suggests them.
// False when the element has no such property.
static bool CollectResourceKeys_ui(InstanceHandle ih, const std::wstring& prop, DevToolsResourceKeyList* list,
                                   std::wstring* type, bool scannedOnly = false)
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
    if (scannedOnly) return true;
    for (const auto& r : props) {
        if (r.authoredKind != L"themeResource" && r.authoredKind != L"staticResource") continue;
        if (DevToolsResourceKeys_TypeFamily(r.type) == family) DevToolsResourceKeys_Add(r.authoredKey, *list);
    }
    DevToolsResourceKeys_AddDefaults(family, *list);
    return true;
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
    PriorLocalValue local;
    if (std::wstring refused = ReadEditPrior(id, wire, prop, &before, &prior, &local); !refused.empty()) return refused;
    CapturePriorBinding(wire, prop, &prior);

    // A failed lookup clears the property. With no saved copy of the binding it could not be put back, so only try
    // a key found in the element's dictionaries (no managed agent, as in a C++ app, or a binding it could not save).
    if (prior.kind == DevToolsPriorKind::Binding && !prior.captured) {
        struct Keys { DevToolsResourceKeyList list; std::wstring type; };
        auto keys = std::make_shared<Keys>();
        bool scanned = false;
        const HRESULT scanHr = RunOnUiThread([wire, prop, keys]() -> HRESULT {
            InstanceHandle ih = 0; const wchar_t* e = nullptr;
            { CensusSharedLock lock; if (!ResolveWire_nolock(wire, &ih, &e)) return E_INVALIDARG; }
            CollectResourceKeys_ui(ih, prop, &keys->list, &keys->type, /*scannedOnly*/ true);
            return S_OK;
        }, &scanned);
        if (!scanned || FAILED(scanHr))
            return DevToolsRpcError(id, DevToolsErr::NotOnDispatcher, L"The app's UI thread did not answer.", L"ui-thread",
                                    L"\"hresult\":\"" + HrStr(scanHr) + L"\"");
        if (std::find(keys->list.keys.begin(), keys->list.keys.end(), key) == keys->list.keys.end()) {
            const std::wstring why = DevToolsResourceKeys_TypeFamily(keys->type).empty()
                ? L"DevTools can't tell what type of value " + prop + L" takes, so it can't confirm that resource '" + key + L"' fits"
                : L"DevTools did not find resource '" + key + L"' among the keys in scope for " + prop;
            return DevToolsRpcError(id, DevToolsErr::NotFound,
                why + L", and " + prior.reason + L". A failed lookup would remove that binding until the app restarts, "
                L"so nothing was changed. To assign the key anyway, clear the property first with HotReload.clearProperty.",
                L"resource-unconfirmed", L"\"key\":\"" + DevToolsJsonEscape(key) + L"\",\"prop\":\"" + DevToolsJsonEscape(prop) + L"\"");
        }
    }

    // A key written over a binding that is left in place goes through it into the app's data, so clear it first.
    const bool clearFirst = prior.kind == DevToolsPriorKind::Binding || prior.kind == DevToolsPriorKind::InstalledBinding;
    struct Outcome { bool applied = false, kept = false; DevToolsReadProp after; const wchar_t* error = nullptr; };
    auto out = std::make_shared<Outcome>();
    bool completed = false;
    const HRESULT hr = RunOnUiThread([wire, prop, key, theme, clearFirst, out]() -> HRESULT {
        InstanceHandle ih = 0;
        { CensusSharedLock lock; if (!ResolveWire_nolock(wire, &ih, &out->error)) return E_INVALIDARG; }
        if (FindPropIndex(ih, prop) == 0xFFFFFFFF || !WindowElementCurrent_ui(ih)) return E_INVALIDARG;
        if (clearFirst && FAILED(ClearPropertyByPath(ih, prop))) { ClearOriginatedError(); out->kept = true; return S_OK; }
        const HRESULT applied = ResolveResourceApply(ih, prop, key, theme);
        ClearOriginatedError();
        if (FAILED(applied)) return S_OK;
        out->applied = true;
        DevToolsBindingRow_ForgetLiveWrite(wire, prop);
        out->after = ReadEditedRow_ui(ih, prop);
        SessionEditNoteResource_ui(ih, wire, prop, key, theme, out->after.value);
        return S_OK;
    }, &completed);
    if (completed && out->error) { ReleaseHeldOriginal(wire, prop, prior); return BridgeErrToRpc(id, std::wstring(L"ERR ") + out->error); }
    if (FAILED(hr)) {
        if (completed) ReleaseHeldOriginal(wire, prop, prior);
        return DevToolsRpcError(id, DevToolsErr::NotOnDispatcher, L"The app's UI thread did not answer.", L"ui-thread",
                                L"\"hresult\":\"" + HrStr(hr) + L"\"");
    }
    if (out->kept) {
        ReleaseHeldOriginal(wire, prop, prior);
        return DevToolsRpcError(id, DevToolsErr::CapabilityUnsupported, L"DevTools could not remove the binding on " + prop +
            L" before assigning resource '" + key + L"', so nothing was changed. A key written over a binding would reach "
            L"the app's data.", L"binding-unavailable", L"\"key\":\"" + DevToolsJsonEscape(key) + L"\",\"prop\":\"" +
            DevToolsJsonEscape(prop) + L"\"");
    }
    if (!out->applied) {
        // A failed lookup can clear the property: put back what it held, the same way a refused install does.
        const DevToolsRollback back = RollBackEdit(wire, prop, prior, local);
        return DevToolsRpcError(id, DevToolsErr::NotFound,
            L"Resource '" + key + L"' was not found or could not be assigned to " + prop + L". " +
            (back.restored ? RollbackSentence(back) + L" Check the key and resource type."
                           : RollbackSentence(back) +
                             (before.value.empty() ? std::wstring() : L" It was '" + before.value + L"'; set it again.")),
            L"resource-not-found",
            L"\"key\":\"" + DevToolsJsonEscape(key) + L"\",\"prop\":\"" + DevToolsJsonEscape(prop) +
            L"\",\"restored\":" + (back.restored ? L"true" : L"false"));
    }

    BroadcastEditedProperty(conn, wire, prop, out->after.value);
    return DevToolsRpcResult(id, L"{\"prop\":\"" + DevToolsJsonEscape(prop) + L"\",\"key\":\"" + DevToolsJsonEscape(key) +
        L"\",\"kind\":\"" + (theme ? L"themeResource" : L"staticResource") +
        L"\",\"before\":\"" + DevToolsJsonEscape(before.value) + L"\",\"after\":\"" + DevToolsJsonEscape(out->after.value) +
        L"\",\"valueType\":\"" + DevToolsJsonEscape(out->after.type) + L"\"," +
        (prior.warning.empty() ? std::wstring() : L"\"warning\":\"" + DevToolsJsonEscape(prior.warning) + L"\",") +
        DevToolsEditUndoMembers(std::to_wstring(wire), prop, prior) + L"}");
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
    PriorLocalValue local;
    if (std::wstring refused = ReadEditPrior(id, wire, prop, &before, &prior, &local); !refused.empty()) return refused;
    CapturePriorBinding(wire, prop, &prior);

    const std::wstring json = CmdBindingInstall(wire, prop, path, mode);
    DevToolsJson answer;
    const bool parsed = json.rfind(L"ERR ", 0) != 0 && DevToolsJsonParse(json, answer) && answer.IsObject();
    auto refuseSecret = [&]() {
        const DevToolsRollback back = RollBackEdit(wire, prop, prior, local);
        return DevToolsRpcError(id, DevToolsErr::RefusedUnsafe,
            L"'" + path + L"' shows a secret: its value prints a member or key whose name ends in Password. " + RollbackSentence(back),
            L"refused-secret", L"\"restored\":" + std::wstring(back.restored ? L"true" : L"false"));
    };
    // The install refuses, and removes in the same UI turn, a value whose text prints a password.
    if (parsed && answer.GetBool(L"secret", false)) return refuseSecret();
    if (!parsed || answer.GetString(L"state") != L"bound" || answer.GetString(L"bound") != L"True") {
        ReleaseHeldOriginal(wire, prop, prior);
        if (json.rfind(L"ERR ", 0) == 0) return BridgeErrToRpc(id, json);
        if (!parsed) return DevToolsRpcError(id, DevToolsErr::InternalError, L"The binding install returned no answer.", L"internal");
        return BindingEditUnavailable(id, L"The binding was not installed: ", answer.GetString(L"reason", L"the binding did not install"));
    }
    const std::wstring undo = DevToolsEditUndoMembers(std::to_wstring(wire), prop, prior, /*editLeftBinding*/ true);

    // Checked before anything reports the new value, so a refused binding never leaves the app. A binding nobody
    // could check is not left in place either.
    std::wstring after;
    bool secret = false;
    if (!ReadInstalledValue(wire, prop, &after, &secret)) {
        const DevToolsRollback back = RollBackEdit(wire, prop, prior, local);
        return DevToolsRpcError(id, DevToolsErr::NotOnDispatcher,
            L"The app's UI thread did not answer while DevTools checked the new binding, so it was not kept. " +
            RollbackSentence(back), L"ui-thread", L"\"restored\":" + std::wstring(back.restored ? L"true" : L"false"));
    }
    if (secret) return refuseSecret();

    SessionEditNoteBinding(wire, prop, path, mode, answer.GetString(L"after"));
    BroadcastEditedProperty(conn, wire, prop, after);
    std::wstring warning = DevToolsBindingInstall_ReplacementWarningCore();
    if (!prior.warning.empty()) warning += L" " + prior.warning;
    return DevToolsRpcResult(id, L"{\"state\":\"bound\",\"path\":\"" + DevToolsJsonEscape(path) +
        L"\",\"mode\":\"" + DevToolsJsonEscape(mode) + L"\",\"before\":\"" + DevToolsJsonEscape(before.value) +
        L"\",\"after\":\"" + DevToolsJsonEscape(after) + L"\",\"bound\":\"True\"" +
        L",\"warning\":\"" + DevToolsJsonEscape(warning) + L"\"," + undo + L"}");
}

// The window's "Write a test value": changes the TwoWay binding's source and keeps the binding installed.
static std::wstring RpcBindingWriteSource(DevToolsConn* conn, const std::wstring& id, const DevToolsJson& p)
{
    const std::wstring prop = p.GetString(L"prop");
    const DevToolsJson* value = p.Find(L"value");
    if (!value) return DevToolsRpcError(id, DevToolsErr::InvalidParams, L"value is required.", L"bad-args");
    if (value->type != DevToolsJsonType::String)
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
    return DevToolsRpcResult(id, json);
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