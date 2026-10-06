// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsTap.cpp; included into that translation unit, not compiled on its own.

// property.get|<handle> -> the element's curated properties as JSON (UI-thread live read).
static std::wstring CmdElementProps(const std::vector<std::wstring>& p)
{
    if (p.size() < 2 || p[1].empty()) return L"ERR bad-args";
    // Prevalidate the opaque handle, then resolve it again inside the UI closure immediately
    // before access. Never carry a raw diagnostics handle across that hop.
    unsigned long long wire = wcstoull(p[1].c_str(), nullptr, 10);
    { InstanceHandle ih0 = 0; const wchar_t* e = nullptr; bool ok;
      AcquireSRWLockShared(&g_censusLock); ok = ResolveWire_nolock(wire, &ih0, &e); ReleaseSRWLockShared(&g_censusLock);
      if (!ok) return std::wstring(L"ERR ") + e; }
    auto props = std::make_shared<std::vector<DevToolsReadProp>>();
    auto authoredState = std::make_shared<std::wstring>();
    auto rerr = std::make_shared<const wchar_t*>(nullptr);
    bool completed = false;
    HRESULT hr = RunOnUiThread([wire, props, authoredState, rerr]() -> HRESULT {
        InstanceHandle ih = 0; const wchar_t* e = nullptr; bool ok;
        AcquireSRWLockShared(&g_censusLock); ok = ResolveWire_nolock(wire, &ih, &e); ReleaseSRWLockShared(&g_censusLock);
        if (!ok) { *rerr = e; return E_INVALIDARG; }
        return ReadCuratedProps(ih, props.get(), authoredState.get());
    }, &completed);
    if (completed && *rerr) return std::wstring(L"ERR ") + *rerr;
    if (FAILED(hr)) return L"ERR " + HrStr(hr);
    return DevToolsRead_SerializeProps(wire, *props, *authoredState);
}

// This is the first tap verb the tap CANNOT answer from native state. DevToolsBindingRelay refuses that call rather
// than hanging, so the rule is enforced and not merely written down here.
static std::wstring CmdBindingOp(unsigned long long wire, const std::wstring& op, const std::wstring& prop,
                                 const std::wstring& authoredOverride = std::wstring())
{
    if (!wire || op.empty() || prop.empty()) return L"ERR bad-args";
    // Reject an unknown op HERE, before anything is resolved or sent. The relay allow-lists it again on the
    // way out; two checks because this one is the wire's contract and that one is the channel's scope.
    if (!DevToolsBindingRelay_IsAllowedOp(op)) return L"ERR bad-args";

    { InstanceHandle ih0 = 0; const wchar_t* e = nullptr; bool ok;
      AcquireSRWLockShared(&g_censusLock); ok = ResolveWire_nolock(wire, &ih0, &e); ReleaseSRWLockShared(&g_censusLock);
      if (!ok) return std::wstring(L"ERR ") + e; }

    DevToolsBindingApartment apartment;
    if (FAILED(apartment.result)) return L"ERR " + HrStr(apartment.result);
    struct BindState { winrt::com_ptr<IAgileReference> element; std::wstring authored; };
    auto st = std::make_shared<BindState>();
    st->authored = authoredOverride;
    HRESULT hr = RunOnUiThread([wire, prop, st]() -> HRESULT {
        DevToolsBindingRelay_NoteUiThread();
        // RE-RESOLVE inside the closure: never carry a raw handle across the UI-thread hop (UAF by
        // construction), same discipline as property.get.
        InstanceHandle ih = 0; const wchar_t* e = nullptr; bool ok;
        AcquireSRWLockShared(&g_censusLock); ok = ResolveWire_nolock(wire, &ih, &e); ReleaseSRWLockShared(&g_censusLock);
        if (!ok) return E_INVALIDARG;
        if (st->authored.empty()) {
            std::vector<DevToolsReadProp> props;
            if (SUCCEEDED(ReadCuratedProps(ih, &props, nullptr))) {
                for (const auto& r : props) { if (r.name == prop) { st->authored = r.authored; break; } }
            }
        }
        winrt::com_ptr<IInspectable> insp;
        HRESULT r = g_diag ? g_diag->GetIInspectableFromHandle(ih, insp.put()) : E_FAIL;
        // Check the OUT-PARAM, not just the HRESULT: this surface has shipped S_OK-with-nothing before, and a
        // null pointer handed to the agent would be a null deref inside the app we are inspecting.
        if (SUCCEEDED(r) && insp)
            return RoGetAgileReference(AGILEREFERENCE_DEFAULT, __uuidof(IInspectable), insp.get(), st->element.put());
        return FAILED(r) ? r : E_FAIL;
    });
    if (FAILED(hr) || !st->element) return L"ERR not-found";

    std::wstring json;
    DevToolsBindingRelay_Binding(op, st->element.get(), prop, st->authored, &json);
    return json;
}

// The caller already has both -- the pane's editor collects them as separate fields -- so carrying "{Binding
// Vm.Title, Mode=OneWay}" across this boundary would mean writing a native parser to take it apart again, and a
// second parser that can disagree with the C# one about what "{Binding Path=A, Converter={StaticResource B}}" means.
static std::wstring CmdBindingInstall(unsigned long long wire, const std::wstring& prop,
                                      const std::wstring& path, const std::wstring& mode)
{
    if (!wire || prop.empty()) return L"ERR bad-args";
    { InstanceHandle ih0 = 0; const wchar_t* e = nullptr; bool ok;
      AcquireSRWLockShared(&g_censusLock); ok = ResolveWire_nolock(wire, &ih0, &e); ReleaseSRWLockShared(&g_censusLock);
      if (!ok) return std::wstring(L"ERR ") + e; }

    auto json = std::make_shared<std::wstring>();
    const HRESULT hr = RunOnUiThread([wire, prop, path, mode, json]() -> HRESULT {
        // RE-RESOLVE inside the closure: never carry a raw handle across the UI-thread hop (UAF by
        // construction), the same discipline property.get and CmdBindingOp use.
        InstanceHandle ih = 0; const wchar_t* e = nullptr; bool ok;
        AcquireSRWLockShared(&g_censusLock); ok = ResolveWire_nolock(wire, &ih, &e); ReleaseSRWLockShared(&g_censusLock);
        if (!ok) return E_INVALIDARG;

        const unsigned int idx = FindPropIndex(ih, prop);
        auto readBack = [ih, idx]() -> DevToolsBindingReadBack {
            DevToolsBindingReadBack rb;
            unsigned int sc = 0, pc = 0;
            PropertyChainSource* s = nullptr; PropertyChainValue* v = nullptr;
            if (g_vts3 && SUCCEEDED(ReadPropChain(ih, &sc, &s, &pc, &v)) && v) {
                for (unsigned int i = 0; i < pc; i++) {
                    if (v[i].Index != idx) continue;
                    rb.found = true;
                    rb.isBinding = ((long long)v[i].MetadataBits & 0x10) != 0;
                    rb.value = v[i].Value ? v[i].Value : L"";
                    break;
                }
            }
            FreePropChain(sc, s, pc, v);
            return rb;
        };

        DevToolsBindingInstall_Apply(g_diag, g_vts3, ih, idx, path, mode, readBack, json.get());
        return S_OK;
    });
    if (FAILED(hr)) return L"ERR not-found";
    return *json;
}

// Takes the overlay's RAW InstanceHandle, like the other overlay bridges, and packs it to an opaque wire handle here
// -- CmdBindingOp resolves wire handles because that is what an external client speaks.
static std::wstring CmdBindingWalk(unsigned long long wire, const std::wstring& prop,
                                   const std::wstring& tryPath = std::wstring());

// layout.get|<handle> -> the element's MEASURED layout facts as JSON (UI-thread live read).
//
// Forward declarations: both census accessors are defined with the other snapshot helpers further down.
static std::wstring TypeOf(InstanceHandle h);
// Layout distinguishes requested, arranged and actual/text extents. Omit failed reads, not zeroes.
// Native walking has no managed-host prerequisite. Runtime classic metadata takes precedence over
// authored text; source selectors we cannot resolve are refused before any DataContext read.
static std::wstring CmdBindingWalk(unsigned long long wire, const std::wstring& prop,
                                   const std::wstring& tryPath)
{
    if (!wire || prop.empty()) return L"ERR bad-args";
    { InstanceHandle ih0 = 0; const wchar_t* e = nullptr; bool ok;
      AcquireSRWLockShared(&g_censusLock); ok = ResolveWire_nolock(wire, &ih0, &e); ReleaseSRWLockShared(&g_censusLock);
      if (!ok) return std::wstring(L"ERR ") + e; }

    auto res = std::make_shared<DevToolsProbeResult>();

    const HRESULT hr = RunOnUiThread([wire, prop, tryPath, res]() -> HRESULT {
        InstanceHandle ih = 0; const wchar_t* e = nullptr; bool ok;
        AcquireSRWLockShared(&g_censusLock); ok = ResolveWire_nolock(wire, &ih, &e); ReleaseSRWLockShared(&g_censusLock);
        if (!ok || !g_diag) return E_FAIL;

        DevToolsReadProp selected;
        std::wstring authoredState;
        std::vector<DevToolsReadProp> props;
        if (SUCCEEDED(ReadCuratedProps(ih, &props, &authoredState))) {
            for (const auto& r : props) { if (r.name == prop) { selected = r; break; } }
        }
        if (!DevToolsPathProbe_Prepare(selected, authoredState, tryPath, res.get())) return S_OK;
        const std::wstring& path = res->path;

        IInspectable* insp = nullptr;
        if (FAILED(g_diag->GetIInspectableFromHandle(ih, &insp)) || !insp) return E_FAIL;

        IInspectable* source = nullptr;
        if (res->againstKind == L"binding") {
            void* fe = nullptr;
            if (SUCCEEDED(insp->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) && fe) {
                // Check the OUT-PARAM, not just the HRESULT. A null DataContext is a real, meaningful
                // state here -- NoDataContextBound in the fixture is exactly it -- and DevToolsPathProbe_Walk
                // reports it as `no-context` rather than as an error.
                DevToolsGetDataContext(fe, &source);
                static_cast<IInspectable*>(fe)->Release();
            }
        } else {
            // The x:Class instance. Walk UP and take the first ancestor on which the path's FIRST segment
            // exists -- a lookup by name, one per ancestor, never an enumeration. Bounded so a deep tree
            // cannot turn one row's question into an unbounded climb (is why anything here has a cap).
            std::vector<std::wstring> segs;
            if (!DevToolsPathProbe_SplitPath(path, &segs)) {
                // A malformed path must NOT fall through to the no-source handler below.
                res->state  = L"bad-path";
                res->reason = L"this is not a well-formed property path";
                insp->Release();
                return S_OK;
            }
            {
                std::vector<InstanceHandle> chain;
                {
                    AcquireSRWLockShared(&g_censusLock);
                    InstanceHandle cur = ih;
                    for (int depth = 0; cur && depth < 64; ++depth) {
                        chain.push_back(cur);
                        auto it = g_parent.find(cur);
                        if (it == g_parent.end() || !it->second) break;
                        cur = it->second;
                    }
                    ReleaseSRWLockShared(&g_censusLock);
                }
                HSTRING first = nullptr;
                WindowsCreateString(segs[0].c_str(), (UINT32)segs[0].size(), &first);
                for (InstanceHandle h : chain) {
                    IInspectable* cand = nullptr;
                    if (FAILED(g_diag->GetIInspectableFromHandle(h, &cand)) || !cand) continue;
                    void* provider = nullptr;
                    if (SUCCEEDED(cand->QueryInterface(DevToolsIid<DevToolsXD::ICustomPropertyProvider>(), &provider)) && provider) {
                        IInspectable* probe = nullptr;
                        const HRESULT hp = DevToolsCppGetCustomProperty(provider, first, &probe);
                        static_cast<IInspectable*>(provider)->Release();
                        if (SUCCEEDED(hp) && probe) { probe->Release(); source = cand; break; }
                    }
                    cand->Release();
                }
                WindowsDeleteString(first);
            }
            if (!source) {
                res->state  = L"no-xbind-source";
                res->reason = L"The native walker could not reach this compiled binding's source through "
                              L"ICustomPropertyProvider. Native [bindable] metadata alone is not sufficient, "
                              L"and a Window or template owner may not be reachable from the visual tree. "
                              L"This is NOT retried against DataContext and does not prove the path is missing.";
                insp->Release();
                return S_OK;
            }
        }

        DevToolsPathProbe_Walk(source, path, res.get());
        if (source) source->Release();
        insp->Release();
        return S_OK;
    });

    if (FAILED(hr)) return L"ERR not-found";
    return DevToolsPathProbe_ToJson(*res);
}

static std::wstring CmdElementLayout(const std::vector<std::wstring>& p)
{
    if (p.size() < 2 || p[1].empty()) return L"ERR bad-args";
    unsigned long long wire = wcstoull(p[1].c_str(), nullptr, 10);
    { InstanceHandle ih0 = 0; const wchar_t* e = nullptr; bool ok;
      AcquireSRWLockShared(&g_censusLock); ok = ResolveWire_nolock(wire, &ih0, &e); ReleaseSRWLockShared(&g_censusLock);
      if (!ok) return std::wstring(L"ERR ") + e; }

    auto facts = std::make_shared<DevToolsReadLayout>();
    facts->handle = wire;
    auto rerr = std::make_shared<const wchar_t*>(nullptr);

    bool completed = false;
    HRESULT hr = RunOnUiThread([wire, facts, rerr]() -> HRESULT {
        InstanceHandle ih = 0; const wchar_t* e = nullptr; bool ok;
        AcquireSRWLockShared(&g_censusLock); ok = ResolveWire_nolock(wire, &ih, &e); ReleaseSRWLockShared(&g_censusLock);
        if (!ok) { *rerr = e; return E_INVALIDARG; }
        if (!g_diag) return E_FAIL;

        IInspectable* insp = nullptr;
        if (FAILED(g_diag->GetIInspectableFromHandle(ih, &insp)) || !insp) return E_FAIL;

        void* ui = nullptr; void* fe = nullptr;
        if (SUCCEEDED(insp->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) && ui) {
            float w = 0, h = 0;
            if (SUCCEEDED(DevToolsGetDesiredSize(ui, &w, &h))) { facts->desiredW = w; facts->desiredH = h; facts->haveDesired = true; }
            if (SUCCEEDED(DevToolsGetRenderSize(ui, &w, &h)))  { facts->renderW = w; facts->renderH = h; facts->haveRender  = true; }
        }
        if (SUCCEEDED(insp->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) && fe) {
            double d = 0;
            if (SUCCEEDED(DevToolsGetActualWidth(fe, &d)))  { facts->actualW = d; facts->haveActual = true; }
            if (SUCCEEDED(DevToolsGetActualHeight(fe, &d))) { facts->actualH = d; }
        }
        std::vector<InstanceHandle> path;
        {
            AcquireSRWLockShared(&g_censusLock);
            InstanceHandle cur = ih;
            for (int guard = 0; guard < 512; guard++) {   // bounded: a cycle must not hang the UI thread
                auto it = g_parent.find(cur);
                if (it == g_parent.end() || !it->second) break;
                cur = it->second;
                path.push_back(cur);
            }
            ReleaseSRWLockShared(&g_censusLock);
        }
        for (size_t k = path.size(); k-- > 0 && ui && !facts->haveOffset; ) {
            IInspectable* rInsp = nullptr;
            if (FAILED(g_diag->GetIInspectableFromHandle(path[k], &rInsp)) || !rInsp) continue;
            void* rUi = nullptr;
            if (SUCCEEDED(rInsp->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &rUi)) && rUi) {
                void* gt = nullptr;
                if (SUCCEEDED(DevToolsTransformToVisual(ui, rUi, &gt)) && gt) {
                    float x = 0, y = 0;
                    if (SUCCEEDED(DevToolsTransformPoint(gt, 0.0f, 0.0f, &x, &y))) {
                        facts->offsetX = x; facts->offsetY = y; facts->haveOffset = true;
                    }
                    reinterpret_cast<IUnknown*>(gt)->Release();
                }
                reinterpret_cast<IUnknown*>(rUi)->Release();
            }
            rInsp->Release();
        }
        // The parent is what actually decided the arrangement, and the census already knows it -- no second
        // diagnostics call. Reported as type + wire handle so a client can offer "select the parent".
        {
            InstanceHandle ph = 0;
            AcquireSRWLockShared(&g_censusLock);
            auto it = g_parent.find(ih);
            if (it != g_parent.end()) ph = it->second;
            ReleaseSRWLockShared(&g_censusLock);
            if (ph) {
                facts->parentType = TypeOf(ph);
                AcquireSRWLockExclusive(&g_censusLock);
                unsigned long long pw = PackWire_nolock(ph);
                ReleaseSRWLockExclusive(&g_censusLock);
                if (pw) facts->parentHandle = std::to_wstring(pw);
                ReadParentContext(ih, ph, ui, *facts);
            }
        }
        if (ui) reinterpret_cast<IUnknown*>(ui)->Release();
        if (fe) reinterpret_cast<IUnknown*>(fe)->Release();
        insp->Release();
        return S_OK;
    }, &completed);
    if (completed && *rerr) return std::wstring(L"ERR ") + *rerr;
    if (FAILED(hr)) return L"ERR " + HrStr(hr);

    return DevToolsRead_SerializeLayout(*facts);
}

// UI thread only (dereferences the live handle).
static bool ReadElementSourceInfo(InstanceHandle ih, std::wstring& outFile, unsigned int& outLine, unsigned int& outCol)
{
    outFile.clear(); outLine = 0; outCol = 0;
    if (!g_vts3) return false;
    unsigned int sc = 0, pc = 0;
    PropertyChainSource* s = nullptr; PropertyChainValue* v = nullptr;
    if (SUCCEEDED(ReadPropChain(ih, &sc, &s, &pc, &v)) && s) {
        for (unsigned int i = 0; i < sc; i++) {
            if (!s[i].SrcInfo.FileName || !*s[i].SrcInfo.FileName) continue;
            if (s[i].Handle != ih) continue;
            outFile = DevToolsSourcePath_AppUri(s[i].SrcInfo.FileName);
            outLine = s[i].SrcInfo.LineNumber;
            outCol  = s[i].SrcInfo.ColumnNumber;
            break;
        }
    }
    FreePropChain(sc, s, pc, v);
    return !outFile.empty();
}

static std::wstring BackfillSourceInfo(InstanceHandle ih)
{
    std::wstring file; unsigned int line = 0, col = 0;
    ReadElementSourceInfo(ih, file, line, col);
    if (!file.empty() && !g_sawAnySourceInfo.exchange(true))
        g_sourceEpoch.fetch_add(1, std::memory_order_release);
    AcquireSRWLockExclusive(&g_sourceUriLock);
    g_sourceInfo[ih] = SourceInfoEntry{ file, g_sourceEpoch.load(std::memory_order_acquire), line, col };
    ReleaseSRWLockExclusive(&g_sourceUriLock);
    return file;
}

static std::wstring CmdElementSource(const std::vector<std::wstring>& p)
{
    if (p.size() < 2 || p[1].empty()) return L"ERR bad-args";
    unsigned long long wire = wcstoull(p[1].c_str(), nullptr, 10);
    { InstanceHandle ih0 = 0; const wchar_t* e = nullptr; bool ok;
      AcquireSRWLockShared(&g_censusLock); ok = ResolveWire_nolock(wire, &ih0, &e); ReleaseSRWLockShared(&g_censusLock);
      if (!ok) return std::wstring(L"ERR ") + e; }
    auto fileName = std::make_shared<std::wstring>();
    auto lineNo   = std::make_shared<unsigned int>(0);
    auto colNo    = std::make_shared<unsigned int>(0);
    auto xaml = std::make_shared<std::wstring>();
    auto authoredLocation = std::make_shared<DevToolsAuthoredLocation>();
    auto authoredState = std::make_shared<DevToolsAuthoredState>(DevToolsAuthoredState::NoSourceInfo);
    auto rerr     = std::make_shared<const wchar_t*>(nullptr);
    bool completed = false;
    HRESULT hr = RunOnUiThread([wire, fileName, lineNo, colNo, rerr, xaml, authoredLocation, authoredState]() -> HRESULT {
        InstanceHandle ih = 0; const wchar_t* e = nullptr; bool ok;
        AcquireSRWLockShared(&g_censusLock); ok = ResolveWire_nolock(wire, &ih, &e); ReleaseSRWLockShared(&g_censusLock);
        if (!ok) { *rerr = e; return E_INVALIDARG; }
        ReadElementSourceInfo(ih, *fileName, *lineNo, *colNo);
        *authoredState = ReadElementAuthoredContext(ih, *fileName, *lineNo, *colNo, xaml.get(), authoredLocation.get());
        return S_OK;
    }, &completed);
    if (completed && *rerr) return std::wstring(L"ERR ") + *rerr;
    if (FAILED(hr)) return L"ERR " + HrStr(hr);
    {
        CensusSharedLock lock;
        InstanceHandle ih = 0; const wchar_t* error = nullptr;
        if (!ResolveWire_nolock(wire, &ih, &error)) return std::wstring(L"ERR ") + (error ? error : L"stale-handle");
    }
    std::wstring out = L"{\"handle\":" + std::to_wstring(wire);
    out += L",\"fileName\":\"" + DevToolsJsonEscape(*fileName) + L"\"";
    out += L",\"lineNumber\":" + std::to_wstring(*lineNo);
    out += L",\"columnNumber\":" + std::to_wstring(*colNo);
    out += L",\"authoredState\":\"" + std::wstring(DevToolsAuthored_StateToken(*authoredState)) + L"\"";
    if (authoredLocation->mapped) {
        out += L",\"authoredFileName\":\"" + DevToolsJsonEscape(authoredLocation->sourceFile) + L"\"";
        out += L",\"authoredLineNumber\":" + std::to_wstring(authoredLocation->line);
        if (authoredLocation->endLine >= authoredLocation->line)
            out += L",\"authoredEndLineNumber\":" + std::to_wstring(authoredLocation->endLine);
        out += L",\"authoredColumnNumber\":" + std::to_wstring(authoredLocation->column);
        out += *authoredState == DevToolsAuthoredState::Likely
            ? L",\"coordinateProvenance\":\"likely-source-line\""
            : L",\"coordinateProvenance\":\"disk-matched-unique-declaration\"";
        if (!authoredLocation->evidence.empty())
            out += L",\"sourceEvidence\":\"" + DevToolsJsonEscape(authoredLocation->evidence) + L"\"";
    }
    if (*authoredState == DevToolsAuthoredState::Available || *authoredState == DevToolsAuthoredState::Likely)
        out += L",\"xaml\":\"" + DevToolsJsonEscape(*xaml) + L"\"";
    out += L"}";
    return out;
}

// The two fields are independent facts and must stay separate, so the reply can never contradict itself: -
// `appAuthored` - WIRE handles (same packing visualtree.enumerate emits) of elements whose declaring XAML is
// APP-authored per DevToolsAppXaml.
static AuthoredVerdicts ClassifyAuthored(CensusSnapshot& cs, bool onUiThread)
{
    AuthoredVerdicts out;
    if (g_adviseFailed.load()) { out.err = L"no-tree"; return out; } // subscription never bound -> nothing to classify, ever
    if (cs.type.empty())       { out.err = L"not-ready"; return out; }
    out.censusNodes = cs.type.size();

    int budget = kMaxTreeWalk;
    std::set<InstanceHandle> included;
    SelectBoundedBreadthFirst(cs, CollectRoots(cs), -1, budget, included);
    if (budget > 0) {
        std::vector<InstanceHandle> unreached;
        for (auto& kv : cs.type) if (included.find(kv.first) == included.end()) unreached.push_back(kv.first);
        if (!unreached.empty()) SelectBoundedBreadthFirst(cs, unreached, -1, budget, included);
    }

    // Backfill in UI-thread batches, but finish a whole navigation's new elements in one request when that is quick:
    // one batch per request left `search` and `inspect` unclassified for several calls after navigating.
    static const size_t kBackfillBatch = 100;
    static const ULONGLONG kBackfillBudgetMs = 250;
    std::vector<unsigned long long> pending;
    for (InstanceHandle h : included) {
        if (cs.sourceUri.find(h) != cs.sourceUri.end()) continue;
        auto wi = cs.wire.find(h);
        if (wi == cs.wire.end() || !wi->second) continue;
        pending.push_back(wi->second);
    }
    const ULONGLONG backfillStart = GetTickCount64();
    for (size_t next = 0; next < pending.size() && (next == 0 || GetTickCount64() - backfillStart < kBackfillBudgetMs);
         next += kBackfillBatch) {
        std::vector<unsigned long long> backfillWires(pending.begin() + next,
            pending.begin() + (std::min)(pending.size(), next + kBackfillBatch));
        auto backfill = [backfillWires]() -> HRESULT {
            for (size_t i = 0; i < backfillWires.size(); ++i) {
                InstanceHandle raw = 0; const wchar_t* err = nullptr; bool ok;
                AcquireSRWLockShared(&g_censusLock);
                ok = ResolveWire_nolock(backfillWires[i], &raw, &err);
                ReleaseSRWLockShared(&g_censusLock);
                if (!ok || !raw) continue;
                BackfillSourceInfo(raw);
            }
            return S_OK;
        };
        HRESULT hr = onUiThread ? backfill() : RunOnUiThread(backfill);
        if (FAILED(hr)) { out.err = L"not-ready"; return out; }
    }
    if (!pending.empty()) {
        cs.sourceUri.clear();
        SourceInfo_Snapshot(cs.sourceUri);
    }

    std::vector<InstanceHandle> srcHandles;
    std::vector<std::wstring> srcUris;
    for (InstanceHandle h : included) {
        auto su = cs.sourceUri.find(h);
        auto wi = cs.wire.find(h);
        if (su == cs.sourceUri.end() || wi == cs.wire.end() || !wi->second) continue;
        ++out.classifiedNodes;
        if (su->second.empty()) continue;
        srcHandles.push_back(h);
        srcUris.push_back(su->second);
    }
    std::vector<char> verdicts;
    DevToolsAppXaml_ClassifyBatch(srcUris, verdicts);
    for (size_t i = 0; i < verdicts.size() && i < srcHandles.size(); ++i)
        if (verdicts[i]) out.authored.insert(srcHandles[i]);

    out.sourceInstrumented = !srcUris.empty();
    out.truncated = included.size() < cs.type.size() || out.classifiedNodes < included.size();
    out.denylistFallback = DevToolsAppXaml_ScanDiscredited();
    return out;
}

static std::wstring CmdAppAuthored()
{
    CensusSnapshot cs;
    {
        CensusSharedLock lock;
        cs.type = g_type; cs.children = g_children; cs.parent = g_parent;
        for (auto& kv : g_type) cs.wire[kv.first] = PackWire_nolock(kv.first);
        SourceInfo_Snapshot(cs.sourceUri);
    }

    if (!FilterWindowCensus(cs) || !WindowScopeCurrent()) return L"ERR window-scope-changed";
    const AuthoredVerdicts verdicts = ClassifyAuthored(cs);
    if (verdicts.err) return std::wstring(L"ERR ") + verdicts.err;

    std::wstring out = L"{\"appAuthored\":[";
    bool first = true;
    for (InstanceHandle h : verdicts.authored) {
        auto wi = cs.wire.find(h);
        if (wi == cs.wire.end() || !wi->second) continue;
        if (!first) out += L',';
        first = false;
        out += L'"';
        out += std::to_wstring(wi->second);
        out += L'"';
    }
    out += L"],\"sourceInstrumented\":";
    out += (verdicts.sourceInstrumented ? L"true" : L"false");
    // Truncation is REPORTED, never silent. A capped classification is otherwise indistinguishable
    // from a complete one, so a client would present a partial answer as the whole truth. Additive keys: a
    // client that predates them ignores unknown fields.
    out += L",\"truncated\":";
    out += (verdicts.truncated ? L"true" : L"false");
    out += L",\"classifiedNodes\":";
    out += std::to_wstring(verdicts.classifiedNodes);
    out += L",\"censusNodes\":";
    out += std::to_wstring(verdicts.censusNodes);
    // "denylist-fallback" means that set did not describe this app - typically a packaged layout whose pages live in
    // resources.pri rather than loose on disk - so the verdicts came from the coarser framework-name denylist and are
    // DELIBERATELY GENEROUS.
    out += L",\"classifier\":\"";
    out += (verdicts.denylistFallback ? L"denylist-fallback" : L"allowlist");
    out += L"\"";
    out += L"}";
    return out;
}
