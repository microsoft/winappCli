// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsTap.cpp; included into that translation unit, not compiled on its own.

static void PipeFraming_Log(const wchar_t* message, void*)
{
    Log(L"%s", message);
}

static const DevToolsFramingHost& PipeFramingHost()
{
    static const DevToolsFramingHost host = [] {
        DevToolsFramingHost h;
        h.user = nullptr;
        h.RegisterConn = PipeFraming_RegisterConn;
        h.ConnId = PipeFraming_ConnId;
        h.UnregisterConn = PipeFraming_UnregisterConn;
        h.VerifyClient = PipeFraming_VerifyClient;
        h.DispatchLine = PipeFraming_DispatchLine;
        h.DispatchFault = PipeFraming_DispatchFault;
        h.EnqueueResponse = PipeFraming_EnqueueResponse;
        h.AfterUnregister = PipeFraming_AfterUnregister;
        h.Log = PipeFraming_Log;
        return h;
    }();
    return host;
}

static DWORD WINAPI PipeConnection(LPVOID param)
{
    DevToolsRunPipeFraming(reinterpret_cast<HANDLE>(param), PipeFramingHost());
    return 0;
}

static DWORD WINAPI PipeServer(LPVOID)
{
    wchar_t pipe[64]; _snwprintf_s(pipe, _countof(pipe), _TRUNCATE, L"\\\\.\\pipe\\winapp-devtools-%lu", GetCurrentProcessId());
    Log(L"pipe server: %s", pipe);
    PSECURITY_DESCRIPTOR sd = DevToolsTrust_CreatePipeSecurityDescriptor();      // process-lifetime; never freed
    if (!sd) {
        // Fail closed: never expose the command pipe with the default DACL (which would grant read
        // access broadly). If the current user's SID/integrity level can't be resolved to lock the pipe
        // down, don't serve at all — the CLI simply reports the DevTools agent as unavailable.
        Log(L"pipe server: could not build owner-only security descriptor; refusing to create pipe");
        return 0;
    }
    SECURITY_ATTRIBUTES sa{ sizeof(sa), sd, FALSE };

    // The accept loop itself lives in DevToolsPipeAcceptLoop.h so the native unit tests can drive the REAL loop
    // against real pipe instances (see pipeaccept-tests.cpp) without XAML, injection or a live app. The
    // owner-only security descriptor built above is the one the pool applies to every instance it posts.
    DevToolsRunPipeAcceptLoop(pipe, &sa, PipeConnection, nullptr,
        [](const wchar_t* message) { Log(L"%s", message); });
    return 0;
}

static IGlobalInterfaceTable* GetGit() {
    static IGlobalInterfaceTable* git = nullptr;
    if (!git) CoCreateInstance(CLSID_StdGlobalInterfaceTable, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&git));
    return git;
}

// Cross-thread transport for the two GIT cookies (IXamlDiagnostics + IVisualTreeService3).
struct WorkerArgs { DWORD cd; DWORD cv; };

// Self-excluded by construction (OverlaySeed before attach), so the element count/oracle is unaffected. Census
// readers run on the pipe thread; they take g_censusLock SHARED and return COPIES, so a concurrent UI-thread writer
// (OnVisualTreeChange) can never invalidate a returned pointer/iterator.
static std::wstring TypeOf_nolock(InstanceHandle h) { auto it = g_type.find(h); return it == g_type.end() ? std::wstring(L"?") : it->second; }
static unsigned int ChildCountOf_nolock(InstanceHandle h) { auto it = g_children.find(h); return it == g_children.end() ? 0u : (unsigned int)it->second.size(); }
// Reverse census lookup (name<-handle); the census stores name->handle, and a pick has only the handle.
// Empty string if the element is unnamed (the common case). Linear over the small named-element map.
static std::wstring NameOf_nolock(InstanceHandle h) { auto it = g_nameOf.find(h); return it == g_nameOf.end() ? std::wstring() : it->second; }
static std::wstring TypeOf(InstanceHandle h) { AcquireSRWLockShared(&g_censusLock); auto r = TypeOf_nolock(h); ReleaseSRWLockShared(&g_censusLock); return r; }
static unsigned int ChildCountOf(InstanceHandle h) { AcquireSRWLockShared(&g_censusLock); auto r = ChildCountOf_nolock(h); ReleaseSRWLockShared(&g_censusLock); return r; }
static std::wstring NameOf(InstanceHandle h) { AcquireSRWLockShared(&g_censusLock); auto r = NameOf_nolock(h); ReleaseSRWLockShared(&g_censusLock); return r; }

// The overlay's inline card reads/writes the picked element's curated properties WITHOUT a pipe or shipped
// host, by calling the same diagnostics primitives the read verbs / SetLiteral already use. These three
// callbacks are registered with the overlay (DevToolsOverlay_SetCardBridge) and invoked on the app UI thread.

// Map a curated property name to the XAML type CreateInstance needs on write. Callers must consume THAT, never call
// this directly to decide editability - doing so is what let both surfaces offer an editor on a read-only property
// whose write silently failed.
static const wchar_t* CardMapType(const std::wstring& name)
{
    if (name == L"Text" || name == L"Content") return L"String";
    if (name == L"Foreground" || name == L"Background") return L"SolidColorBrush";
    if (name == L"Visibility") return L"Visibility";
    if (name == L"HorizontalAlignment") return L"HorizontalAlignment";
    if (name == L"VerticalAlignment") return L"VerticalAlignment";
    if (name == L"Margin" || name == L"Padding" || name == L"BorderThickness") return L"Thickness";
    if (name == L"Opacity" || name == L"Width" || name == L"Height" || name == L"FontSize") return L"Double";
    // Layout clamps. Settable Doubles like Width/Height; a MaxWidth of "inf" round-trips through
    // CreateInstance only as a finite number, which is honest -- you cannot type infinity back into a box,
    // and the row still READS "inf" so the clamp is visible even where it cannot be restored by hand.
    if (name == L"MinWidth" || name == L"MaxWidth" || name == L"MinHeight" || name == L"MaxHeight") return L"Double";
    if (name == L"IsEnabled") return L"Boolean";
    return nullptr;
}

// Preferred, reference-like display order for card rows (only those actually present on the element show).
static const wchar_t* const kCardOrder[] = {
    L"Content", L"Text", L"Foreground", L"Background", L"FontSize", L"Opacity",
    L"Visibility", L"IsEnabled", L"HorizontalAlignment", L"VerticalAlignment", L"Width", L"Height", L"Margin", L"Padding",
};

static DevToolsCardRow ToCardRow(const DevToolsReadProp& p);

// UI thread only. A row's `type` is the SHARED DevToolsReadProp::writeType (read-only-gated in ReadCuratedProps), so
// this surface, the window pane and the wire cannot disagree about what is editable; an empty `type` means read-only
// and the panel renders a static value rather than dropping the row.
static bool CardReadBridge(IInspectable* target, std::wstring* outType, std::wstring* outName,
                           std::vector<DevToolsCardRow>* outRows, std::wstring* outAuthoredState,
                           std::wstring* outAuthoredXaml)
{
    if (!target || !g_diag || !g_vts3) return false;
    InstanceHandle h = 0;
    if (FAILED(g_diag->GetHandleFromIInspectable(target, &h)) || !h) return false;
    if (outType) *outType = TypeOf(h);
    if (outName) *outName = NameOf(h);
    std::vector<DevToolsReadProp> props;
    if (FAILED(ReadCuratedProps(h, &props, outAuthoredState, outAuthoredXaml))) return false;
    if (outRows) {
        for (const wchar_t* want : kCardOrder) {
            for (const auto& p : props) {
                if (p.name != want) continue;
                outRows->push_back(ToCardRow(p));
                break;
            }
        }
    }
    return true;
}

// Diagnostics display text is not input: an empty TextBox can be reported as "0".
// Read its live projected value, preserving successful empty HSTRINGs. UI thread only.
static bool CardReadInputBridge(IInspectable* input, std::wstring* outText)
{
    const HRESULT hr = ReadControlText(input, outText);
    if (hr != S_OK) {
        Log(L"input.read: live Text unavailable hr=0x%08X", hr);
        return false;
    }
    return true;
}

// These surfaces edit the live tree in-process with no wire connection behind them, so origin is null: a client that
// renders solely from the event stream applies the change like any other, and an optimistically-rendering wire client
// has no self-echo to suppress here (it did not make this edit).
static void EmitLocalPropertyChanged(InstanceHandle h, const std::wstring& prop, const std::wstring& value)
{
    unsigned long long wire = 0; std::wstring name;
    {
        AcquireSRWLockShared(&g_censusLock);
        wire = PackWire_nolock(h);
        name = NameOf_nolock(h);
        ReleaseSRWLockShared(&g_censusLock);
    }
    std::wstring params = L"{\"handle\":\"" + std::to_wstring(wire) + L"\",\"name\":\"" + DevToolsJsonEscape(name) +
                          L"\",\"prop\":\"" + DevToolsJsonEscape(prop) + L"\",\"value\":\"" + DevToolsJsonEscape(value) + L"\"}";
    DevToolsEvents_Broadcast(DevToolsDomain_Property, DevToolsRpcEvent(L"Property.changed", params, L"null"),
                        DevToolsCoalesce_KeepNewest, L"prop:" + std::to_wstring(wire) + L"\x1f" + prop);
}

// The window has three commit sites (TextBox blur, enum ComboBox, and preset pickers) and the card has one; asking
// each surface "is this property bound, and did the user confirm replacing it?" would create copies of a predicate
// that must never disagree.

// Ground truth from the property chain's MetadataBit IsValueBindingExpression (0x10) - the same bit ValueSourceLabel
// turns into the "Binding" pill, so the gate and the pill can never disagree about whether a row is bound.
static bool IsPropBoundLive(InstanceHandle h, const std::wstring& prop)
{
    if (!g_vts3) return false;
    unsigned int sc = 0, pc = 0; PropertyChainSource* s = nullptr; PropertyChainValue* v = nullptr;
    if (FAILED(ReadPropChain(h, &sc, &s, &pc, &v)) || !v) { FreePropChain(sc, s, pc, v); return false; }
    bool bound = false;
    for (unsigned int i = 0; i < pc; i++) {
        if (!v[i].PropertyName || prop != v[i].PropertyName) continue;
        bound = ((long long)v[i].MetadataBits & 0x10) != 0;
        break;
    }
    FreePropChain(sc, s, pc, v);
    return bound;
}

// Deeper paths are rejected rather than walked: nothing produces them, and a write primitive that accepts a path no
// reader can generate is a way to reach arbitrary objects in the app from a surface that never intended to. This
// function must not be treated as that gate: it resolves, it does not authorise.
static bool ResolveWriteTarget(InstanceHandle h, const std::wstring& path,
                               InstanceHandle* outHandle, std::wstring* outLeaf)
{
    const size_t dot = path.find(L'.');
    if (dot == std::wstring::npos) { *outHandle = h; *outLeaf = path; return true; }
    const std::wstring parent = path.substr(0, dot);
    const std::wstring leaf   = path.substr(dot + 1);
    if (parent.empty() || leaf.empty() || leaf.find(L'.') != std::wstring::npos) return false;
    if (!g_vts3) return false;
    unsigned int sc = 0, pc = 0; PropertyChainSource* s = nullptr; PropertyChainValue* v = nullptr;
    if (FAILED(ReadPropChain(h, &sc, &s, &pc, &v)) || !v) { FreePropChain(sc, s, pc, v); return false; }
    InstanceHandle child = 0;
    for (unsigned int i = 0; i < pc; i++) {
        if (!v[i].PropertyName || parent != v[i].PropertyName) continue;
        // Only an OBJECT-valued property has an inside to write into. IsValueHandle is the runtime's own
        // statement of that; without it, `Value` is a rendered string and parsing it as a handle would hand
        // SetProperty a number that means nothing.
        if (((long long)v[i].MetadataBits & 0x1) != 0 && v[i].Value)
            child = (InstanceHandle)wcstoull(v[i].Value, nullptr, 10);
        break;
    }
    FreePropChain(sc, s, pc, v);
    if (!child) { Log(L"write: '%s' has no object value to write '%s' into", parent.c_str(), leaf.c_str()); return false; }
    *outHandle = child; *outLeaf = leaf;
    return true;
}

// UI thread only. Such a memo also has to key on element identity across time -- with a recyclable raw handle, so a
// dead element's consent could be inherited by whatever is slotted next.
static DevToolsWriteOutcome InteractiveWrite(InstanceHandle h, const std::wstring& prop, const std::wstring& type,
                                        const std::wstring& value, bool confirmBindingReplace)
{
    InstanceHandle target = 0; std::wstring leaf;
    if (!ResolveWriteTarget(h, prop, &target, &leaf)) return DevToolsWriteOutcome::Failed;
    const bool bound = IsPropBoundLive(target, leaf);
    if (bound && !confirmBindingReplace) {
        Log(L"write.gate '%s' refused: bound, unconfirmed", prop.c_str());
        return DevToolsWriteOutcome::NeedsConfirm;
    }
    HRESULT hr = SetLiteral(target, leaf, type, value);
    if (FAILED(hr)) {
        Log(L"write '%s'='%s' (%s) hr=0x%08X", prop.c_str(), value.c_str(), type.c_str(), hr);
        return DevToolsWriteOutcome::Failed;
    }
    Log(L"write '%s'='%s' (%s) OK bound=%d", prop.c_str(), value.c_str(), type.c_str(), (int)bound);
    // The change event names the ELEMENT and the path the user edited, not the anonymous child object: a
    // client watching the stream has no handle for the brush and could do nothing with one.
    EmitLocalPropertyChanged(h, prop, value);
    return bound ? DevToolsWriteOutcome::ReplacedBinding : DevToolsWriteOutcome::Ok;
}


static DevToolsWriteOutcome CardWriteBridge(IInspectable* target, const wchar_t* prop, const wchar_t* type,
                                       const wchar_t* value, bool confirmBindingReplace)
{
    if (!target || !g_diag || !prop || !type || !value) return DevToolsWriteOutcome::Failed;
    InstanceHandle h = 0;
    if (FAILED(g_diag->GetHandleFromIInspectable(target, &h)) || !h) return DevToolsWriteOutcome::Failed;
    return InteractiveWrite(h, prop, type, value, confirmBindingReplace);
}


// Unlike the overlay card (editable rows only), this returns EVERY curated property so the window shows the full set;
// Does `wire` still resolve to a live element? Shares ResolveWire_nolock with every other verb, so liveness here
// cannot drift from liveness as the read/write paths see it -- one predicate, not a second opinion.
static bool Route1WireLive(InstanceHandle wire)
{
    InstanceHandle ih = 0; const wchar_t* e = nullptr; bool ok;
    AcquireSRWLockShared(&g_censusLock);
    ok = ResolveWire_nolock((unsigned long long)wire, &ih, &e);
    ReleaseSRWLockShared(&g_censusLock);
    return ok;
}

// The live census population. Same lock and same map every other verb reads, so the number the window compares
// against cannot drift from the tree the census would actually produce.
static size_t Route1CensusCount()
{
    AcquireSRWLockShared(&g_censusLock);
    const size_t n = g_type.size();
    ReleaseSRWLockShared(&g_censusLock);
    return n;
}

// Recursive, because a child row IS a row.
static DevToolsCardRow ToCardRow(const DevToolsReadProp& p)
{
    DevToolsCardRow row;
    row.name = p.name;
    row.type = p.writeType;
    // Any COLOUR-shaped value is normalised to #AARRGGBB so a swatch can be painted from it directly. Keyed
    // off the shared editKind rather than a name test: the old name test knew Foreground and Background and
    // therefore left BorderBrush, Fill and Stroke un-normalised the moment they became visible.
    row.value = (p.editKind == L"color") ? DevToolsRead_NormalizeColor(p.value) : p.value;
    row.source = p.source;
    row.valueType = p.type;
    row.binding = p.binding;
    row.authored = p.authored;
    row.authoredKind = p.authoredKind;
    row.authoredKey = p.authoredKey;
    row.fields = p.fields;
    row.valueState = p.valueState;
    row.editKind = p.editKind;
    for (const auto& c : p.chain) {
        DevToolsCardChainEntry e;
        e.source = c.source; e.value = c.value; e.file = c.file; e.line = c.line; e.winner = c.winner;
        row.chain.push_back(std::move(e));
    }
    for (const auto& c : p.children) row.children.push_back(ToCardRow(c));
    return row;
}

// That gate is MetadataBit::IsPropertyReadOnly, decided once in ReadCuratedProps - so a property the runtime refuses
// to set is now visibly non-editable here instead of offering an editor that silently fails. `wire` is re-resolved to
// a live raw handle under the census lock; a stale handle yields a friendly header and no rows rather than a crash.
static void Route1ReadRows(InstanceHandle wire, std::wstring& outType, std::wstring& outName,
                           std::vector<DevToolsCardRow>& outRows, std::wstring& outAuthoredState,
                           std::wstring& outAuthoredXaml)
{
    InstanceHandle ih = 0; const wchar_t* e = nullptr; bool ok;
    AcquireSRWLockShared(&g_censusLock);
    ok = ResolveWire_nolock((unsigned long long)wire, &ih, &e);
    ReleaseSRWLockShared(&g_censusLock);
    if (!ok) { outType = L"(element)"; outName = e ? e : L"stale"; return; }
    outType = TypeOf(ih);
    outName = NameOf(ih);
    std::vector<DevToolsReadProp> props;
    if (FAILED(ReadCuratedProps(ih, &props, &outAuthoredState, &outAuthoredXaml))) return;
    for (const auto& p : props) outRows.push_back(ToCardRow(p));
}

// Microsoft.UI.Xaml.Automation.AutomationProperties statics, resolved once. Used only to answer "did the
// author set an AutomationId on this element", which is the tree caption's last fallback.
static void* AutomationPropertiesStatics()
{
    static void* s_statics = nullptr;
    static bool s_tried = false;
    if (s_tried) return s_statics;
    s_tried = true;
    HSTRING cls = nullptr;
    const wchar_t* name = L"Microsoft.UI.Xaml.Automation.AutomationProperties";
    if (FAILED(WindowsCreateString(name, (UINT32)wcslen(name), &cls))) return nullptr;
    HRESULT hr = RoGetActivationFactory(cls, DevToolsIid<DevToolsXA::IAutomationPropertiesStatics>(), &s_statics);
    WindowsDeleteString(cls);
    if (FAILED(hr)) { s_statics = nullptr; Log(L"route1.preview AutomationProperties statics hr=0x%08X", hr); }
    return s_statics;
}

// UI-thread caption read: Text, string Content, glyph, then authored AutomationId.
// Re-resolve the wire handle and use projected getters without realizing elements or reading
// the full property chain. Empty means no identifying caption; object Content is not text.
static void Route1ReadPreview(InstanceHandle wire, std::wstring& out, std::wstring* searchText,
                             std::vector<DevToolsBatch::PreviewValue>* values)
{
    out.clear();
    if (searchText) searchText->clear();
    if (!g_diag) return;
    InstanceHandle ih = 0; const wchar_t* e = nullptr; bool ok;
    AcquireSRWLockShared(&g_censusLock);
    ok = ResolveWire_nolock((unsigned long long)wire, &ih, &e);
    ReleaseSRWLockShared(&g_censusLock);
    if (!ok) { Log(L"route1.preview wire %llu stale (%s)", (unsigned long long)wire, e ? e : L"?"); return; }
    IInspectable* insp = nullptr;
    HRESULT hrI = g_diag->GetIInspectableFromHandle(ih, &insp);
    if (FAILED(hrI) || !insp) { Log(L"route1.preview handle %llu did not resolve hr=0x%08X", (unsigned long long)ih, hrI); return; }

    // QI for `I`, run `read`, release. `read` returns the caption it found (possibly empty). Returns false
    // when the element does not implement `I` at all, so the caller can tell "not this kind of element"
    // from "this kind of element, with nothing to say".
    auto tryRead = [&](REFIID iid, const std::function<std::wstring(void*)>& read, std::wstring& dest) {
        void* itf = nullptr;
        if (FAILED(insp->QueryInterface(iid, &itf)) || !itf) return false;
        dest = read(itf);
        reinterpret_cast<IUnknown*>(itf)->Release();
        return true;
    };

    std::wstring text, glyph, autoId;
    std::wstring extraSearch;
    auto readString = [&](const wchar_t* name, const std::function<HRESULT(HSTRING*)>& get, bool* readable = nullptr) {
        HSTRING hs = nullptr;
        const HRESULT hr = get(&hs);
        if (readable) *readable = SUCCEEDED(hr);
        std::wstring value = HStrTake(hs);
        if (values) values->push_back({name, value, SUCCEEDED(hr) ? L"value" : L"unreadable"});
        if (SUCCEEDED(hr) && !value.empty()) extraSearch += L"\n" + value;
        return SUCCEEDED(hr) ? value : std::wstring();
    };
    auto readObject = [&](const wchar_t* name, const std::function<HRESULT(IInspectable**)>& get) {
        IInspectable* object = nullptr;
        const HRESULT hr = get(&object);
        std::wstring value, type, state;
        if (FAILED(hr)) state = L"unreadable";
        else if (!object) state = L"null";
        else {
            const HRESULT read = ReadInspectableValue(object, name, L"", &value, &type);
            state = read == S_OK ? L"value" : FAILED(read) ? L"unreadable" : L"object";
            if (state == L"object") {
                HSTRING className = nullptr;
                if (SUCCEEDED(object->GetRuntimeClassName(&className))) type = HStrTake(className);
            }
        }
        if (object) object->Release();
        if (values) values->push_back({name, value, state, type});
        if (state == L"value" && !value.empty()) extraSearch += L"\n" + value;
        return state == L"value" ? value : std::wstring();
    };
    tryRead(DevToolsIid<DevToolsXC::ITextBlock>(), [&](void* p) {
        return readString(L"Text", [p](HSTRING* hs) { return DevToolsGetTextBlockText(p, hs); });
    }, text);
    if (text.empty())
        tryRead(DevToolsIid<DevToolsXC::ITextBox>(), [&](void* p) {
            bool readable = false;
            auto current = readString(L"Text", [p](HSTRING* hs) { return DevToolsGetTextBoxText(p, hs); }, &readable);
            if (readable && current.empty())
                readString(L"PlaceholderText", [p](HSTRING* hs) { return DevToolsGetPlaceholderText(p, hs); });
            readObject(L"Header", [p](IInspectable** value) { return DevToolsGetTextBoxHeader(p, value); });
            return current;
        }, text);
    if (text.empty()) {
        auto readContent = [&](HRESULT (*get)(void*, IInspectable**)) {
            return [get, &readObject](void* p) -> std::wstring {
                return readObject(L"Content", [get, p](IInspectable** content) { return get(p, content); });
            };
        };
        if (!tryRead(DevToolsIid<DevToolsXC::IContentControl>(), readContent(&DevToolsGetContentControlContent), text) || text.empty())
            tryRead(DevToolsIid<DevToolsXC::IContentPresenter>(), readContent(&DevToolsGetContentPresenterContent), text);
    }

    if (values || searchText) {
        std::wstring supplemental;
        tryRead(DevToolsIid<DevToolsXC::IAutoSuggestBox>(), [&](void* p) {
            bool readable = false;
            auto current = readString(L"Text", [p](HSTRING* value) { return DevToolsGetSuggestText(p, value); }, &readable);
            if (readable && current.empty())
                readString(L"PlaceholderText", [p](HSTRING* value) { return DevToolsGetSuggestPlaceholder(p, value); });
            readObject(L"Header", [p](IInspectable** value) { return DevToolsGetSuggestHeader(p, value); });
            return current;
        }, supplemental);
        auto readBool = [&](const wchar_t* name, const std::function<HRESULT(bool*)>& get) {
            bool value = false;
            const HRESULT hr = get(&value);
            if (values) values->push_back({name, value ? L"true" : L"false", SUCCEEDED(hr) ? L"value" : L"unreadable", L"Boolean"});
            return std::wstring();
        };
        tryRead(DevToolsIid<DevToolsXC::IExpander>(), [&](void* p) {
            readBool(L"IsExpanded", [p](bool* value) { return DevToolsGetIsExpanded(p, value); });
            return readObject(L"Header", [p](IInspectable** value) { return DevToolsGetExpanderHeader(p, value); });
        }, supplemental);
        tryRead(DevToolsIid<DevToolsXCP::ISelectorItem>(), [&](void* p) {
            return readBool(L"IsSelected", [p](bool* value) { return DevToolsGetIsSelected(p, value); });
        }, supplemental);
        tryRead(DevToolsIid<DevToolsXCP::IToggleButton>(), [&](void* p) {
            return readObject(L"IsChecked", [p](IInspectable** value) { return DevToolsGetIsChecked(p, value); });
        }, supplemental);
        std::wstring accessibility;
        void* statics = AutomationPropertiesStatics();
        if (statics)
            tryRead(DevToolsIid<DevToolsX::IDependencyObject>(), [&](void* p) {
                return readString(L"AutomationProperties.Name", [p, statics](HSTRING* hs) {
                    return DevToolsGetAutomationName(statics, p, hs);
                });
            }, accessibility);
    }
    if (searchText) *searchText = text + extraSearch;
    if (text.empty())
        tryRead(DevToolsIid<DevToolsXC::IFontIcon>(), [](void* p) {
            HSTRING hs = nullptr; return SUCCEEDED(DevToolsGetFontIconGlyph(p, &hs)) ? HStrTake(hs) : std::wstring();
        }, glyph);

    if (text.empty() && glyph.empty()) {
        void* statics = AutomationPropertiesStatics();
        if (statics)
            tryRead(DevToolsIid<DevToolsX::IDependencyObject>(), [statics](void* p) {
                HSTRING hs = nullptr;
                return SUCCEEDED(DevToolsGetAutomationId(statics, p, &hs)) ? HStrTake(hs) : std::wstring();
            }, autoId);
    }
    insp->Release();

    if (!text.empty()) out = text;
    else if (!glyph.empty()) {
        wchar_t buf[16]; _snwprintf_s(buf, _countof(buf), _TRUNCATE, L"U+%04X", (unsigned)(unsigned short)glyph[0]);
        out = buf;
    }
    else if (!autoId.empty()) out = autoId;
    for (auto& c : out) if (c == L'\r' || c == L'\n' || c == L'\t') c = L' ';
    if (out.size() > 42) { out.resize(41); out += L'\u2026'; }
}

// UI thread: the element's AutomationId and its confirmed declaration. The declaration comes from the cached runtime
// position and the build map, so it reads no source text.
static void ReadPreviewFacts(InstanceHandle wire, DevToolsBatch::PreviewFacts& out)
{
    out = {};
    InstanceHandle ih = 0; const wchar_t* e = nullptr;
    std::wstring type, name;
    {
        CensusSharedLock lock;
        if (!ResolveWire_nolock((unsigned long long)wire, &ih, &e)) return;
        if (const auto t = g_type.find(ih); t != g_type.end()) type = t->second;
        if (const auto n = g_nameOf.find(ih); n != g_nameOf.end()) name = n->second;
    }
    std::wstring file; unsigned int line = 0, column = 0;
    AcquireSRWLockShared(&g_sourceUriLock);
    if (const auto s = g_sourceInfo.find(ih); s != g_sourceInfo.end()) { file = s->second.file; line = s->second.line; column = s->second.column; }
    ReleaseSRWLockShared(&g_sourceUriLock);
    if (!file.empty() && line && column) {
        DevToolsAuthoredLocation location;
        if (DevToolsAuthored_Locate(file, line, column, type, name, &location)) {
            out.file = location.sourceFile; out.line = location.line;
            out.endLine = (std::max)(location.line, location.endLine); out.column = location.column;
        }
    }
    void* statics = AutomationPropertiesStatics();
    IInspectable* insp = nullptr;
    if (!statics || !g_diag || FAILED(g_diag->GetIInspectableFromHandle(ih, &insp)) || !insp) return;
    void* dependency = nullptr;
    if (SUCCEEDED(insp->QueryInterface(DevToolsIid<DevToolsX::IDependencyObject>(), &dependency)) && dependency) {
        HSTRING hs = nullptr;
        if (SUCCEEDED(DevToolsGetAutomationId(statics, dependency, &hs))) out.automationId = HStrTake(hs);
        reinterpret_cast<IUnknown*>(dependency)->Release();
    }
    insp->Release();
}

// Runs on the UI thread (the window's commit sinks), so it may touch the live tree directly. Stale handle -> Failed.
static DevToolsWriteOutcome Route1Write(InstanceHandle wire, const std::wstring& prop, const std::wstring& type,
                                   const std::wstring& value, bool confirmBindingReplace)
{
    if (!DevToolsTrust_MutationEnabled()) {
        Log(L"route1.write refused by process posture");
        return DevToolsWriteOutcome::Failed;
    }
    InstanceHandle ih = 0; const wchar_t* e = nullptr; bool ok;
    AcquireSRWLockShared(&g_censusLock);
    ok = ResolveWire_nolock((unsigned long long)wire, &ih, &e);
    ReleaseSRWLockShared(&g_censusLock);
    if (!ok) { Log(L"route1.write stale wire (%s)", e ? e : L"?"); return DevToolsWriteOutcome::Failed; }
    return InteractiveWrite(ih, prop, type, value, confirmBindingReplace);
}

// Reset one curated property from the in-proc window's inspector to its inherited/default value: re-resolve
// `wire` to a live raw handle under the census lock, then ClearProperty via the shared ClearLiteral primitive.
// Runs on the UI thread (the window's per-row reset Button.Click). Stale handle -> no-op.
static bool Route1Clear(InstanceHandle wire, const std::wstring& prop)
{
    if (!DevToolsTrust_MutationEnabled()) {
        Log(L"route1.clear refused by process posture");
        return false;
    }
    InstanceHandle ih = 0; const wchar_t* e = nullptr; bool ok;
    AcquireSRWLockShared(&g_censusLock);
    ok = ResolveWire_nolock((unsigned long long)wire, &ih, &e);
    ReleaseSRWLockShared(&g_censusLock);
    if (!ok) { Log(L"route1.clear stale wire (%s)", e ? e : L"?"); return false; }
    // The SHARED clear primitive, so the in-process button and an external inspector cannot disagree about what a
    // property name means. Resolving the sub-object first -- which is what this did -- reports "unresolvable" for
    // every ATTACHED property, so the button silently could not undo a write the pane had just made.
    HRESULT hr = ClearPropertyByPath(ih, prop);
    if (FAILED(hr)) { Log(L"route1.clear '%s' hr=0x%08X", prop.c_str(), hr); return false; }
    Log(L"route1.clear '%s' OK", prop.c_str());
    return true;
}

// Assign a named resource through shared ResolveResourceApply after resolving the live handle.
// False can mean the runtime cleared the property during a failed lookup; it does not guarantee
// no mutation. The pane owns restoring its captured prior value.
static bool Route1Resolve(InstanceHandle wire, const std::wstring& prop, const std::wstring& resourceName, bool asTheme)
{
    if (!DevToolsTrust_MutationEnabled()) {
        Log(L"route1.resolve refused by process posture");
        return false;
    }
    InstanceHandle ih = 0; const wchar_t* e = nullptr; bool ok;
    AcquireSRWLockShared(&g_censusLock);
    ok = ResolveWire_nolock((unsigned long long)wire, &ih, &e);
    ReleaseSRWLockShared(&g_censusLock);
    if (!ok) { Log(L"route1.resolve stale wire (%s)", e ? e : L"?"); return false; }
    HRESULT hr = ResolveResourceApply(ih, prop, resourceName, asTheme);
    if (FAILED(hr)) { Log(L"route1.resolve '%s' <- %s hr=0x%08X", prop.c_str(), resourceName.c_str(), hr); return false; }
    Log(L"route1.resolve '%s' <- %s OK", prop.c_str(), resourceName.c_str());
    return true;
}

// bind installs a native Binding from path/mode; writesource uses the managed agent to change
// the source without replacing the target binding. payload is the value for writesource.
// Call from a worker because managed binding work dispatches back to the app UI thread.
static bool Route1Bind(InstanceHandle wire, const std::wstring& op, const std::wstring& prop,
                       const std::wstring& payload, const std::wstring& mode, std::wstring* outJson)
{
    if (!outJson) return false;
    if (!DevToolsTrust_MutationEnabled()) {
        Log(L"route1.bind refused by process posture");
        *outJson = DevToolsBindingRelay_UnavailableJson(L"this process does not allow DevTools to mutate it");
        return false;
    }
    if (op == L"bind") {
        *outJson = CmdBindingInstall((unsigned long long)wire, prop, payload, mode);
        if (outJson->rfind(L"ERR ", 0) == 0) {
            *outJson = DevToolsBindingRelay_UnavailableJson(L"this element is no longer in the visual tree");
            return false;
        }
        // TRUE means "an answer was produced", not "it worked". The caller decides success from the answer's
        // own read-back (`bound`), which is the same discipline it already applies to the agent ops --
        // one place, so the two routes cannot end up disagreeing about what success means.
        return true;
    }
    *outJson = CmdBindingOp((unsigned long long)wire, op, prop, payload);
    // CmdBindingOp answers `ERR <token>` only for a handle that no longer resolves; every other outcome is
    // already a diagnosis object. Translate that one case so the window has a single response shape.
    if (outJson->rfind(L"ERR ", 0) == 0) {
        *outJson = DevToolsBindingRelay_UnavailableJson(L"this element is no longer in the visual tree");
        return false;
    }
    return true;
}

// Same wire re-resolution discipline as the other Route-1 bridges (never carry a raw handle across time). Already on
// the UI thread, so no RunOnUiThread hop.
// UI thread only. Shows where the declaration opens in the authored file when the build proves it; the runtime's
// own line points somewhere inside the tag and is kept only when no verified mapping exists.
static bool ReadDisplaySourceInfo(InstanceHandle ih, std::wstring& file, unsigned int& line, unsigned int& column)
{
    if (!ReadElementSourceInfo(ih, file, line, column)) return false;
    std::wstring xaml;
    DevToolsAuthoredLocation location;
    if (ReadElementAuthoredContext(ih, file, line, column, &xaml, &location) == DevToolsAuthoredState::Available &&
        location.mapped) {
        line = location.line;
        column = location.column;
    }
    return true;
}

static bool Route1Source(InstanceHandle wire, std::wstring& outFile, unsigned int& outLine, unsigned int& outCol)
{
    InstanceHandle ih = 0; const wchar_t* e = nullptr; bool ok;
    AcquireSRWLockShared(&g_censusLock);
    ok = ResolveWire_nolock((unsigned long long)wire, &ih, &e);
    ReleaseSRWLockShared(&g_censusLock);
    if (!ok) { outFile.clear(); outLine = 0; outCol = 0; return false; }
    return ReadDisplaySourceInfo(ih, outFile, outLine, outCol);
}

// Read parent layout facts once per selection on the UI thread. Missing reads stay absent,
// not plausible zero values that would explain placement using fabricated evidence.
static void ReadParentContext(InstanceHandle elem, InstanceHandle parent, void* elemUi, DevToolsWindowLayout& out)
{
    if (!parent || !g_diag) return;

    IInspectable* pInsp = nullptr;
    if (SUCCEEDED(g_diag->GetIInspectableFromHandle(parent, &pInsp)) && pInsp) {
        void* pUi = nullptr;
        if (SUCCEEDED(pInsp->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &pUi)) && pUi) {
            float w = 0, h = 0;
            if (SUCCEEDED(DevToolsGetRenderSize(pUi, &w, &h))) { out.parentW = w; out.parentH = h; out.haveParentBox = true; }
            if (elemUi) {
                void* gt = nullptr;
                if (SUCCEEDED(DevToolsTransformToVisual(elemUi, pUi, &gt)) && gt) {
                    float x = 0, y = 0;
                    if (SUCCEEDED(DevToolsTransformPoint(gt, 0.0f, 0.0f, &x, &y))) {
                        out.inParentX = x; out.inParentY = y; out.haveInParent = true;
                    }
                    reinterpret_cast<IUnknown*>(gt)->Release();
                }
            }
            reinterpret_cast<IUnknown*>(pUi)->Release();
        }
        pInsp->Release();
    }

    if (g_vts3) {
        unsigned int sc = 0, pc = 0;
        PropertyChainSource* s = nullptr; PropertyChainValue* v = nullptr;
        if (SUCCEEDED(ReadPropChain(parent, &sc, &s, &pc, &v)) && v) {
            std::set<std::wstring> taken;
            for (unsigned int i = 0; i < pc; i++) {
                if (!v[i].PropertyName) continue;
                const std::wstring n = v[i].PropertyName;
                if (n != L"Orientation" && n != L"Spacing" && n != L"Padding") continue;
                if (!taken.insert(n).second) continue;
                const std::wstring type = PickValueType(v[i]);
                const std::wstring val = ResolveDisplayValue(n, type, v[i].Value ? v[i].Value : L"",
                                                             ((long long)v[i].MetadataBits & 0x1) != 0);
                if (n == L"Orientation") {
                    const std::wstring named = EnumNameForValue(type, val);
                    out.parentOrientation = named.empty() ? val : named;
                } else if (n == L"Spacing") {
                    out.parentSpacing = val;
                } else {
                    out.parentPadding = val;
                }
            }
            FreePropChain(sc, s, pc, v);
        }

        if (ShortTypeName(TypeOf(parent)) == L"Grid") {
            unsigned int esc = 0, epc = 0;
            PropertyChainSource* es = nullptr; PropertyChainValue* ev = nullptr;
            if (SUCCEEDED(ReadPropChain(elem, &esc, &es, &epc, &ev)) && ev) {
                std::set<std::wstring> taken;
                for (unsigned int i = 0; i < epc; i++) {
                    if (!ev[i].PropertyName) continue;
                    const std::wstring n = ev[i].PropertyName;
                    if (n != L"Grid.Row" && n != L"Grid.Column" && n != L"Grid.RowSpan" && n != L"Grid.ColumnSpan") continue;
                    if (!taken.insert(n).second) continue;
                    const std::wstring val = ev[i].Value ? ev[i].Value : L"";
                    if (n == L"Grid.Row")            out.gridRow = val;
                    else if (n == L"Grid.Column")    out.gridColumn = val;
                    else if (n == L"Grid.RowSpan")   out.gridRowSpan = val;
                    else                             out.gridColumnSpan = val;
                }
                FreePropChain(esc, es, epc, ev);
            }
        }
    }

    // Position among the siblings, from the census. "It is the first child, so no Spacing applies" is only
    // sayable with this, and it is free: the tree is already nested.
    {
        AcquireSRWLockShared(&g_censusLock);
        auto it = g_children.find(parent);
        if (it != g_children.end()) {
            out.childCount = (int)it->second.size();
            for (size_t k = 0; k < it->second.size(); ++k)
                if (it->second[k] == elem) { out.childIndex = (int)k; break; }
        }
        ReleaseSRWLockShared(&g_censusLock);
    }
}

// Already on the UI thread, so the reads happen inline with no RunOnUiThread hop -- the same reason Route1Source has
// none. Shares its reads with CmdElementLayout deliberately at the level of the projected calls rather than by
// calling that verb: this path already holds the UI thread and must not re-enter the dispatcher.
static bool Route1Layout(InstanceHandle wire, DevToolsWindowLayout& out)
{
    InstanceHandle ih = 0; const wchar_t* e = nullptr; bool ok;
    AcquireSRWLockShared(&g_censusLock);
    ok = ResolveWire_nolock((unsigned long long)wire, &ih, &e);
    ReleaseSRWLockShared(&g_censusLock);
    if (!ok || !g_diag) return false;

    IInspectable* insp = nullptr;
    if (FAILED(g_diag->GetIInspectableFromHandle(ih, &insp)) || !insp) return false;

    void* ui = nullptr; void* fe = nullptr;
    if (SUCCEEDED(insp->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) && ui) {
        float w = 0, h = 0;
        if (SUCCEEDED(DevToolsGetDesiredSize(ui, &w, &h))) { out.desiredW = w; out.desiredH = h; out.haveDesired = true; }
        if (SUCCEEDED(DevToolsGetRenderSize(ui, &w, &h)))  { out.renderW  = w; out.renderH  = h; out.haveRender  = true; }
    }
    if (SUCCEEDED(insp->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) && fe) {
        double d = 0;
        if (SUCCEEDED(DevToolsGetActualWidth(fe, &d)))  { out.actualW = d; out.haveActual = true; }
        if (SUCCEEDED(DevToolsGetActualHeight(fe, &d))) { out.actualH = d; }
    }
    std::vector<InstanceHandle> path;
    {
        AcquireSRWLockShared(&g_censusLock);
        InstanceHandle cur = ih;
        for (int guard = 0; guard < 512; guard++) {
            auto it = g_parent.find(cur);
            if (it == g_parent.end() || !it->second) break;
            cur = it->second;
            path.push_back(cur);
        }
        ReleaseSRWLockShared(&g_censusLock);
    }
    for (size_t k = path.size(); k-- > 0 && ui && !out.haveOffset; ) {
        IInspectable* rInsp = nullptr;
        if (FAILED(g_diag->GetIInspectableFromHandle(path[k], &rInsp)) || !rInsp) continue;
        void* rUi = nullptr;
        if (SUCCEEDED(rInsp->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &rUi)) && rUi) {
            void* gt = nullptr;
            if (SUCCEEDED(DevToolsTransformToVisual(ui, rUi, &gt)) && gt) {
                float x = 0, y = 0;
                if (SUCCEEDED(DevToolsTransformPoint(gt, 0.0f, 0.0f, &x, &y))) {
                    out.offsetX = x; out.offsetY = y; out.haveOffset = true;
                }
                reinterpret_cast<IUnknown*>(gt)->Release();
            }
            reinterpret_cast<IUnknown*>(rUi)->Release();
        }
        rInsp->Release();
    }
    if (!path.empty()) out.parentType = TypeOf(path.front());
    if (!path.empty()) ReadParentContext(ih, path.front(), ui, out);

    if (ui) reinterpret_cast<IUnknown*>(ui)->Release();
    if (fe) reinterpret_cast<IUnknown*>(fe)->Release();
    insp->Release();
    return out.haveDesired || out.haveRender || out.haveOffset;
}

// The overlay works in RAW diagnostics handles (straight from HitTest), so this is a direct ReadElementSourceInfo —
// no wire resolution (unlike Route1Source, which the window calls with wire tokens). UI thread only (the overlay's
// pick + composer both run there).
static bool OverlaySourceBridge(InstanceHandle raw, std::wstring* outFile, unsigned int* outLine, unsigned int* outCol)
{
    std::wstring f; unsigned int ln = 0, col = 0;
    bool ok = ReadDisplaySourceInfo(raw, f, ln, col);
    if (outFile) *outFile = f;
    if (outLine) *outLine = ln;
    if (outCol) *outCol = col;
    return ok;
}

// Flips the SAME g_overlayBuilding flag TryBuildOverlay's OverlayBuildScope uses, so every card element's Add during
// the InsertAt self-excludes from the element-count oracle by its LIVE handle - which then also excludes each
// TextBox's async default-template parts via the parent-in-set rule (their live parent is now in the set).
static void CardScopeBridge(int on) { g_overlayBuilding.store(on != 0, std::memory_order_release); }

// Called on the app UI thread from ShowCardForElement / the card Close handler.
static void CardFreezeBridge(int on) {
    if (on) {
        if (!g_cardFrozen.load(std::memory_order_acquire)) {
            AcquireSRWLockExclusive(&g_censusLock);
            size_t n = CensusPopulation_nolock();
            // Snapshot the baseline handle set so enumerate can be filtered to it while frozen (keeps the
            // enumerate-count == pong invariant despite the card's un-excludable async parts entering g_type).
            g_cardFrozenHandles.clear();
            for (const auto& kv : g_type) g_cardFrozenHandles.insert(kv.first);
            ReleaseSRWLockExclusive(&g_censusLock);
            g_cardFrozenCount.store(n, std::memory_order_release);
            g_cardFrozen.store(true, std::memory_order_release);
            DevToolsOverlayLog(L"card.freeze ON baseline=%zu", n);
        }
    } else {
        bool was = g_cardFrozen.exchange(false, std::memory_order_release);
        AcquireSRWLockExclusive(&g_censusLock);
        g_cardFrozenHandles.clear();
        ReleaseSRWLockExclusive(&g_censusLock);
        if (was) DevToolsOverlayLog(L"card.freeze OFF (unfrozen)");
    }
}
