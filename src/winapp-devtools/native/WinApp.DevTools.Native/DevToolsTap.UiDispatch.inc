// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsTap.cpp; included into that translation unit, not compiled on its own.

static TreeCb g_treeCb;

#ifdef WINAPP_DEVTOOLS_NATIVE_RUNTIME_TESTS
static HRESULT (*g_testEnqueueUiOperation)(void*, IUnknown*, bool*) = nullptr;
static HRESULT (*g_testPropertyMutation)(InstanceHandle, const std::wstring&, const std::wstring&,
    const std::wstring&, bool) = nullptr;
#endif

static HRESULT EnqueueUiOperation(void* dispatcher, IUnknown* handler, bool* enqueued)
{
#ifdef WINAPP_DEVTOOLS_NATIVE_RUNTIME_TESTS
    if (g_testEnqueueUiOperation) return g_testEnqueueUiOperation(dispatcher, handler, enqueued);
#endif
    return DevToolsDispatcherTryEnqueue(dispatcher, handler, enqueued);
}

// Run a closure on the app UI thread and return its HRESULT. Each call owns its handler, completion event,
// closure, and result so independent named-pipe connection threads cannot overwrite one another.
static HRESULT RunOnUiThread(std::function<HRESULT()> fn, bool* completed = nullptr)
{
    if (completed) *completed = false;
    if (!g_dispatcher) return E_FAIL;
    return DevToolsUiDispatch_Run(g_dispatcher, EnqueueUiOperation, [fn = std::move(fn), scope = g_windowScope]() {
        TapWindowScopeGuard guard(scope);
        DevToolsPerf_LatchUiThread();
        if (scope) {
            for (const auto& member : scope->members)
                if (!WindowElementCurrent_ui(member.first)) return E_ACCESSDENIED;
        }
        return fn();
    }, 10000, completed);
}

// A Window. request cannot use RunOnUiThread: that helper waits on the dispatcher turn, which would leave the
// connection's read thread parked in the same UI -> pipe -> UI wait shape the binding bridge rejects. Window requests
// instead post one independently-owned callback and return from dispatch immediately.
struct UiResponseHandler final : IDispatcherQueueHandler
{
    UiResponseHandler(DevToolsConn* connection, std::wstring requestId, std::function<std::wstring()> operation)
        : conn(connection), id(std::move(requestId)), fn(std::move(operation))
    {
        DevToolsEvents_Retain(conn);
    }

    ~UiResponseHandler()
    {
        if (conn) DevToolsEvents_Release(conn);
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** value) override
    {
        if (!value) return E_POINTER;
        if (riid == IID_IUnknown || riid == __uuidof(IDispatcherQueueHandler) || riid == __uuidof(IAgileObject)) {
            *value = static_cast<IDispatcherQueueHandler*>(this);
            AddRef();
            return S_OK;
        }
        *value = nullptr;
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() override
    {
        return refs.fetch_add(1, std::memory_order_relaxed) + 1;
    }

    ULONG STDMETHODCALLTYPE Release() override
    {
        ULONG remaining = refs.fetch_sub(1, std::memory_order_acq_rel) - 1;
        if (remaining == 0) delete this;
        return remaining;
    }

    HRESULT STDMETHODCALLTYPE Invoke() override
    {
        // A C++ exception must never escape a COM callback: this runs on the application's UI thread, invoked by the
        // DispatcherQueue, with no framing or dispatch boundary anywhere on the stack to catch it.
        try {
            std::wstring response;
            try {
                response = fn ? fn() : DevToolsRpcError(id, DevToolsErr::InternalError, L"missing UI operation", L"internal");
            } catch (const std::bad_alloc&) {
                response = DevToolsRpcError(id, DevToolsErr::InternalError, L"out of memory", L"internal",
                                       L"\"hresult\":\"0x8007000E\"");
            } catch (...) {
                response = DevToolsRpcError(id, DevToolsErr::InternalError, L"window operation failed", L"internal",
                                       L"\"hresult\":\"0x80004005\"");
            }
            DevToolsEvents_EnqueueResponse(conn, response);
        } catch (...) {
        }
        DevToolsEvents_Release(conn);
        conn = nullptr;
        fn = nullptr;
        return S_OK;
    }

private:
    std::atomic<ULONG> refs{1};
    DevToolsConn* conn;
    std::wstring id;
    std::function<std::wstring()> fn;
};

// Post one response-producing operation to the app dispatcher without waiting. On success the callback owns
// the eventual response; on failure the caller must return a synchronous error response.
static HRESULT PostRpcToUi(DevToolsConn* conn, const std::wstring& id, std::function<std::wstring()> fn)
{
    if (!conn || !g_dispatcher) return E_FAIL;
    auto* handler = new (std::nothrow) UiResponseHandler(conn, id, std::move(fn));
    if (!handler) return E_OUTOFMEMORY;
    bool enqueued = false;
    HRESULT hr = DevToolsDispatcherTryEnqueue(g_dispatcher, handler, &enqueued);
    handler->Release();
    if (FAILED(hr)) return hr;
    return enqueued ? S_OK : E_FAIL;
}

// Post cleanup that owns no connection response. Used when a disconnect removes the final subscriber: the
// connection thread must not touch XAML objects, and the callback rechecks the count to tolerate a new enable
// racing ahead of this dispatcher turn.
static HRESULT PostToUi(std::function<void()> fn)
{
    if (!g_dispatcher) return E_FAIL;
    auto* handler = new (std::nothrow) UiResponseHandler(nullptr, L"", [fn = std::move(fn)]() {
        fn();
        return std::wstring();
    });
    if (!handler) return E_OUTOFMEMORY;
    bool enqueued = false;
    HRESULT hr = DevToolsDispatcherTryEnqueue(g_dispatcher, handler, &enqueued);
    handler->Release();
    if (FAILED(hr)) return hr;
    return enqueued ? S_OK : E_FAIL;
}

// Shared live-property mutation primitives, dispatched to the app UI thread.
static std::vector<std::wstring> Split(const std::wstring& s, wchar_t d) {
    std::vector<std::wstring> out; size_t start = 0, p;
    while ((p = s.find(d, start)) != std::wstring::npos) { out.push_back(s.substr(start, p - start)); start = p + 1; }
    out.push_back(s.substr(start)); return out;
}
static std::wstring HrStr(HRESULT hr) { wchar_t b[16]; _snwprintf_s(b, _countof(b), _TRUNCATE, L"0x%08X", hr); return b; }

// A single pass (not sequential replaces) is required so a literal backslash-n on the wire (`\\` + `n`) round-trips
// as backslash-n and is NOT mistaken for a newline.
static bool TryUnescapeField(const std::wstring& in, std::wstring& out) {
    out.clear();
    out.reserve(in.size());
    for (size_t i = 0; i < in.size(); i++) {
        wchar_t c = in[i];
        if (c != L'\\') { out += c; continue; }
        if (i + 1 >= in.size()) return false;
        switch (in[++i]) {
            case L'\\': out += L'\\'; break;
            case L'n':  out += L'\n'; break;
            case L'r':  out += L'\r'; break;
            default:    return false;
        }
    }
    return true;
}

// GetPropertyValuesChain hands back two caller-owned arrays whose BSTR members we own. Free every one
// (and the arrays themselves) on EVERY path - otherwise each call leaks the target's whole 100-200 entry
// property chain into its process.
static void FreePropChain(unsigned int sc, PropertyChainSource* s, unsigned int pc, PropertyChainValue* v)
{
    if (v) {
        for (unsigned int i = 0; i < pc; i++) {
            SysFreeString(v[i].Type); SysFreeString(v[i].DeclaringType); SysFreeString(v[i].ValueType);
            SysFreeString(v[i].ItemType); SysFreeString(v[i].Value); SysFreeString(v[i].PropertyName);
        }
        CoTaskMemFree(v);
    }
    if (s) {
        for (unsigned int i = 0; i < sc; i++) {
            SysFreeString(s[i].TargetType); SysFreeString(s[i].Name);
            SysFreeString(s[i].SrcInfo.FileName); SysFreeString(s[i].SrcInfo.Hash);
        }
        CoTaskMemFree(s);
    }
}

// Every chain read goes through here, so a secret's value (DevToolsRead_IsSecretProperty) is replaced before any
// caller can format, compare, search or send it.
static HRESULT ReadPropChain(InstanceHandle h, unsigned int* sc, PropertyChainSource** s, unsigned int* pc, PropertyChainValue** v)
{
    const HRESULT hr = g_vts3->GetPropertyValuesChain(h, sc, s, pc, v);
    if (SUCCEEDED(hr) && *v) {
        for (unsigned int i = 0; i < *pc; i++) {
            PropertyChainValue& value = (*v)[i];
            if (!value.PropertyName || !DevToolsRead_IsSecretProperty(value.PropertyName)) continue;
            SysFreeString(value.Value);
            value.Value = SysAllocString(kDevToolsRedacted);
            value.MetadataBits &= ~static_cast<decltype(value.MetadataBits)>(0x1); // no longer an object handle
        }
    }
    return hr;
}

// Writing a secret would echo it through the write result and change events.
static bool RefuseSecretWrite(const std::wstring& prop, std::wstring* error)
{
    if (!DevToolsRead_IsSecretProperty(prop)) return false;
    if (error) *error = L"'" + prop + L"' holds a secret; DevTools does not read or write it.";
    Log(L"setprop: refused secret property '%s'", prop.c_str());
    return true;
}

// Resolve a property's diagnostics index from its chain on the UI thread. Do not cache by
// raw handle: handles can be reused for an element with different property indices.
// GetPropertyIndex needs a fully qualified name whose declaring type also requires that chain.
static unsigned int FindPropIndex(InstanceHandle h, const std::wstring& prop)
{
    unsigned int idx = 0xFFFFFFFF, sc = 0, pc = 0;
    PropertyChainSource* s = nullptr; PropertyChainValue* v = nullptr;
    if (g_vts3 && SUCCEEDED(ReadPropChain(h, &sc, &s, &pc, &v)) && v)
        for (unsigned int i = 0; i < pc; i++)
            if (v[i].PropertyName && prop == v[i].PropertyName) { idx = v[i].Index; break; }
    FreePropChain(sc, s, pc, v);
    return idx;
}
// Set a literal property: CreateInstance from a XAML type-converter literal, then SetProperty. UI thread.
static HRESULT SetLiteral(InstanceHandle h, const std::wstring& prop, const std::wstring& type, const std::wstring& value,
                          std::wstring* error = nullptr)
{
    if (!WindowElementCurrent_ui(h)) return E_ACCESSDENIED;
    if (RefuseSecretWrite(prop, error)) return E_ACCESSDENIED;
#ifdef WINAPP_DEVTOOLS_NATIVE_RUNTIME_TESTS
    if (g_testPropertyMutation) return g_testPropertyMutation(h, prop, type, value, false);
#endif
    unsigned int idx = FindPropIndex(h, prop);
    if (idx == 0xFFFFFFFF) { Log(L"setprop: '%s' not found on %llu", prop.c_str(), h); return E_INVALIDARG; }
    BSTR tn = SysAllocString(type.c_str()), vv = SysAllocString(value.c_str());
    InstanceHandle valH = 0;
    HRESULT hr = DevToolsInspector::WriteLiteral(prop, type, value, [&]() {
        if (!tn || !vv) return E_OUTOFMEMORY;
        HRESULT converted = g_vts3->CreateInstance(tn, vv, &valH);
        return SUCCEEDED(converted) && !valH ? E_INVALIDARG : converted;
    }, [&]() { return WindowElementCurrent_ui(h) ? g_vts3->SetProperty(h, valH, idx) : E_ACCESSDENIED; }, error);
    SysFreeString(tn); SysFreeString(vv);
    return hr;
}

// Reset a property to its inherited/default value: resolve its dispatcher index (same cached walk SetLiteral
// uses), then IVisualTreeService::ClearProperty. UI thread. Mirrors SetLiteral's not-found handling.
static HRESULT ClearLiteral(InstanceHandle h, const std::wstring& prop)
{
    unsigned int idx = FindPropIndex(h, prop);
    if (idx == 0xFFFFFFFF) { Log(L"clearprop: '%s' not found on %llu", prop.c_str(), h); return E_INVALIDARG; }
    if (!g_vts3) return E_NOINTERFACE;
    return WindowElementCurrent_ui(h) ? g_vts3->ClearProperty(h, idx) : E_ACCESSDENIED;
}

// Defined below, next to the write path it was extracted from. Declared here because the `hotreload.clearprop`
// bridge needs the same dotted-path resolution the in-process Clear value button gets through Route1Clear.
static bool ResolveWriteTarget(InstanceHandle h, const std::wstring& path,
                               InstanceHandle* outHandle, std::wstring* outLeaf);

// Splitting on the dot first turns the attached case into a search for a property called "AutomationProperties",
// finds nothing, and reports a failure the caller cannot act on. The write path never had this problem because
// SetLiteral resolves the full name directly, which is why a row could be written and then not un-written.
static HRESULT ClearPropertyByPath(InstanceHandle h, const std::wstring& prop)
{
    if (!WindowElementCurrent_ui(h)) return E_ACCESSDENIED;
#ifdef WINAPP_DEVTOOLS_NATIVE_RUNTIME_TESTS
    if (g_testPropertyMutation) return g_testPropertyMutation(h, prop, L"", L"", true);
#endif
    if (FindPropIndex(h, prop) != 0xFFFFFFFF) return ClearLiteral(h, prop);
    InstanceHandle target = 0; std::wstring leaf;
    if (!ResolveWriteTarget(h, prop, &target, &leaf)) return E_INVALIDARG;
    return ClearLiteral(target, leaf);
}

// Assign a named resource in the element's lookup scope, not a recovered original resource link.
// Convert ErrorResourceNotFound observed during the call into failure, even if ResolveResource
// returned S_OK, so callers never report a miss as a successful assignment.
static HRESULT ResolveResourceApply(InstanceHandle h, const std::wstring& prop, const std::wstring& resourceName, bool asTheme)
{
    if (RefuseSecretWrite(prop, nullptr)) return E_ACCESSDENIED;
    unsigned int idx = FindPropIndex(h, prop);
    if (idx == 0xFFFFFFFF) { Log(L"resolveres: '%s' not found on %llu", prop.c_str(), h); return E_INVALIDARG; }
    if (!g_vts3) return E_NOINTERFACE;
    ResourceType rt = asTheme ? ResourceTypeTheme : ResourceTypeStatic;
    const DevToolsResourceError before = ResourceErrorSnapshot();
    HRESULT hr = g_vts3->ResolveResource(h, resourceName.c_str(), rt, idx);
    const DevToolsResourceError after  = ResourceErrorSnapshot();
    const bool notFound = after.seq != before.seq;
    Log(L"resolveres: '%s' <- %s (theme=%d) idx=%u hr=0x%08X errseq %llu->%llu", prop.c_str(), resourceName.c_str(),
        asTheme ? 1 : 0, idx, hr, before.seq, after.seq);
    if (notFound)
        Log(L"resolveres: '%s' <- %s DID NOT RESOLVE (errelem=%llu ours=%llu context='%s')", prop.c_str(),
            resourceName.c_str(), (unsigned long long)after.elem, (unsigned long long)h, after.context.c_str());
    if (SUCCEEDED(hr) && notFound) return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    return hr;
}
