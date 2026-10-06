// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsTap.cpp; included into that translation unit, not compiled on its own.

// Same root-detection + DFS + orphan-sweep as CmdEnumerate, but emits plain {handle,name,type,depth} structs. handle
// carries the OPAQUE generation-stamped WIRE handle (NOT a raw InstanceHandle): the app tree churns while the window
// stays open, so the click handler must re-resolve the wire to a live raw handle under the census lock right before
// reading props (dereferencing a stale raw handle in GetPropertyValuesChain is a use-after-free -- the same reason
// property.get is wire-based; see Route1ReadProps / CmdElementProps).

// Selection is breadth-first (see SelectBoundedBreadthFirst); EMISSION stays depth-first so the flat list still reads
// top-down as a tree. Those are deliberately different orders: the first decides what survives the cap, the second
// decides how it is displayed.
static void FlattenCensusNode(const CensusSnapshot& cs, InstanceHandle h, int depth,
                              const std::set<InstanceHandle>& included,
                              std::set<InstanceHandle>& emitted, std::vector<DevToolsWindowNode>& out)
{
    if (depth > kMaxEnumDepth) return;
    DevToolsWindowNode n;
    auto wi = cs.wire.find(h); n.handle = (wi != cs.wire.end()) ? (InstanceHandle)wi->second : 0;
    auto nm = cs.name.find(h); if (nm != cs.name.end()) n.name = nm->second;
    auto ty = cs.type.find(h); n.type = (ty != cs.type.end()) ? ty->second : std::wstring(L"?");
    auto su = cs.sourceUri.find(h); if (su != cs.sourceUri.end()) n.sourceUri = su->second;
    n.depth = depth;
    out.push_back(std::move(n));
    auto ch = cs.children.find(h);
    if (ch == cs.children.end()) return;
    for (InstanceHandle child : ch->second) {
        if (cs.type.find(child) == cs.type.end()) continue;
        if (included.find(child) == included.end()) continue;
        if (!emitted.insert(child).second) continue;
        FlattenCensusNode(cs, child, depth + 1, included, emitted, out);
    }
}

// What must not happen is SILENT truncation: a header printing the truncated `nodes.size()` as "VISUAL TREE — N
// NODES" states a partial count as if it were the whole tree.
static std::vector<DevToolsWindowNode> SnapshotFlatCensus(size_t* outTotal, CensusSnapshot* outSnapshot = nullptr)
{
    CensusSnapshot cs;
    {
        CensusSharedLock lock;
        cs.type = g_type; cs.children = g_children; cs.parent = g_parent;
        cs.name = g_nameOf;
        for (auto& kv : g_type) cs.wire[kv.first] = PackWire_nolock(kv.first);
        SourceInfo_Snapshot(cs.sourceUri);
    }
    if (outSnapshot) ComputeSelectorFacts(cs);
    if (outSnapshot) *outSnapshot = cs;
    if (!FilterWindowCensus(cs)) {
        if (outTotal) *outTotal = g_windowScope->members.size();
        return {};
    }
    if (outTotal) *outTotal = cs.type.size();

    int budget = kMaxTreeWalk;
    std::set<InstanceHandle> included;
    SelectBoundedBreadthFirst(cs, CollectRoots(cs), -1, budget, included);
    if (budget > 0) {
        std::vector<InstanceHandle> unreached;
        for (auto& kv : cs.type) {
            if (included.find(kv.first) == included.end()) unreached.push_back(kv.first);
        }
        if (!unreached.empty()) SelectBoundedBreadthFirst(cs, unreached, -1, budget, included);
    }

    std::vector<DevToolsWindowNode> out;
    std::set<InstanceHandle> emitted;
    for (auto& kv : cs.type) {
        InstanceHandle h = kv.first;
        if (included.find(h) == included.end()) continue;
        auto pit = cs.parent.find(h);
        if (pit != cs.parent.end() && included.find(pit->second) != included.end()) continue;
        if (!emitted.insert(h).second) continue;
        FlattenCensusNode(cs, h, 0, included, emitted, out);
    }
    return out;
}

static void Route1ReadPreview(InstanceHandle wire, std::wstring& out, std::wstring* searchText = nullptr,
                             std::vector<DevToolsBatch::PreviewValue>* values = nullptr);
static void ReadPreviewFacts(InstanceHandle wire, DevToolsBatch::PreviewFacts& out);

static std::wstring CmdFind(const std::wstring& query, bool appAuthoredOnly)
{
    if (g_adviseFailed.load()) return L"ERR no-tree";

    size_t censusTotal = 0;
    CensusSnapshot cs;
    const std::vector<DevToolsWindowNode> nodes = SnapshotFlatCensus(&censusTotal, &cs);
    if (!FilterWindowCensus(cs) || !WindowScopeCurrent()) return L"ERR window-scope-changed";
    if (nodes.empty()) return L"ERR not-ready";

    // A match must carry the SAME stable identity enumerate emits, so a selector copied out of a search
    // result addresses the same element as one copied out of a tree listing.
    std::map<unsigned long long, std::wstring> idByWire;
    std::set<unsigned long long> uniqueNameWires;
    for (const auto& kv : cs.wire) {
        if (!kv.second) continue;
        auto idv = cs.id.find(kv.first);
        if (idv != cs.id.end()) idByWire[kv.second] = idv->second;
        if (cs.uniqueName.find(kv.first) != cs.uniqueName.end()) uniqueNameWires.insert(kv.second);
    }

    const std::wstring normalized = DevToolsTreeLayout::NormalizeTreeFilterQuery(query);
    std::set<std::wstring> appAuthored;
    bool classificationTruncated = false;
    bool sourceInstrumented = false;
    if (appAuthoredOnly) {
        const AuthoredVerdicts verdicts = ClassifyAuthored(cs);
        if (verdicts.err) return std::wstring(L"ERR ") + verdicts.err;
        // No refusal for "nothing classified": this result already has an envelope, so the state travels
        // with the (possibly empty) match list. While the classifier is still backfilling, an empty authored
        // set means "not finished", not "this app authored nothing", and the two must not read the same.
        classificationTruncated = verdicts.truncated;
        sourceInstrumented = verdicts.sourceInstrumented;
        for (InstanceHandle h : verdicts.authored) {
            auto wi = cs.wire.find(h);
            if (wi != cs.wire.end() && wi->second) appAuthored.insert(std::to_wstring(wi->second));
        }
    }

    std::vector<bool> matched(nodes.size(), false);
    std::vector<size_t> needsText;
    for (size_t i = 0; i < nodes.size(); ++i) {
        const auto& node = nodes[i];
        if (appAuthoredOnly && appAuthored.find(std::to_wstring(node.handle)) == appAuthored.end()) continue;
        matched[i] = DevToolsTreeLayout::TreeFilterMatches(node.type, node.name, node.sourceUri, normalized);
        if (!matched[i]) needsText.push_back(i);
    }
    // Reuse the bounded caption read and lifetime checks, without truncating searchable text to the row caption.
    for (size_t begin = 0; begin < needsText.size(); begin += kMaxPreviewBatch) {
        const size_t end = (std::min)(begin + kMaxPreviewBatch, needsText.size());
        std::vector<InstanceHandle> wires;
        for (size_t j = begin; j < end; ++j) wires.push_back(nodes[needsText[j]].handle);
        auto hits = std::make_shared<std::vector<bool>>(wires.size(), false);
        const HRESULT hr = RunOnUiThread([wires, hits, normalized]() {
            for (size_t j = 0; j < wires.size(); ++j) {
                std::wstring caption, text;
                Route1ReadPreview(wires[j], caption, &text);
                (*hits)[j] = !text.empty() && DevToolsTreeLayout::TreeFilterMatches(L"", L"", text, normalized);
                if (!(*hits)[j]) {
                    // The AutomationId is what `winapp ui` selects by, so a search for one finds the element here too.
                    DevToolsBatch::PreviewFacts facts;
                    ReadPreviewFacts(wires[j], facts);
                    (*hits)[j] = !facts.automationId.empty() &&
                        DevToolsTreeLayout::TreeFilterMatches(L"", L"", facts.automationId, normalized);
                }
            }
            return S_OK;
        });
        if (FAILED(hr)) return L"ERR ui-thread";
        for (size_t j = begin; j < end; ++j) matched[needsText[j]] = (*hits)[j - begin];
    }

    std::wstring out = L"{\"matches\":[";
    size_t matches = 0;
    size_t searchedNodes = 0;
    for (size_t i = 0; i < nodes.size(); ++i) {
        const std::wstring wire = std::to_wstring((unsigned long long)nodes[i].handle);
        if (appAuthoredOnly && appAuthored.find(wire) == appAuthored.end()) continue;
        ++searchedNodes;
        const auto& node = nodes[i];
        if (!matched[i]) continue;
        if (matches++) out += L',';
        out += L"{\"handle\":\"" + wire;
        out += L"\",\"name\":\"" + DevToolsJsonEscape(node.name);
        out += L"\",\"type\":\"" + DevToolsJsonEscape(node.type);
        out += L"\",\"depth\":" + std::to_wstring(node.depth);
        // Same rule as enumerate: omitted when the runtime never attributed the element to a XAML file.
        if (!node.sourceUri.empty()) out += L",\"file\":\"" + DevToolsJsonEscape(node.sourceUri) + L"\"";
        auto idv = idByWire.find((unsigned long long)nodes[i].handle);
        if (idv != idByWire.end()) out += L",\"id\":\"" + idv->second + L"\"";
        if (uniqueNameWires.find((unsigned long long)nodes[i].handle) != uniqueNameWires.end())
            out += L",\"uniqueName\":true";
        out += L"}";
    }
    out += L"],\"query\":\"" + DevToolsJsonEscape(normalized);
    out += L"\",\"appAuthoredOnly\":";
    out += appAuthoredOnly ? L"true" : L"false";
    out += L",\"searchedNodes\":" + std::to_wstring(searchedNodes);
    out += L",\"censusNodes\":" + std::to_wstring(censusTotal);
    out += L",\"truncated\":";
    out += nodes.size() < censusTotal ? L"true" : L"false";
    if (appAuthoredOnly) {
        out += L",\"sourceInstrumented\":";
        out += sourceInstrumented ? L"true" : L"false";
        out += L",\"classificationTruncated\":";
        out += classificationTruncated ? L"true" : L"false";
    }
    out += L"}";
    return out;
}

// Each takes the node's opaque generation-stamped WIRE handle and MUST re-resolve it to a live raw handle under the
// census lock right before use -- dereferencing a stale RAW handle inside the diagnostics chain is a use-after-free
// (the exact discipline property.get / SetLiteral already follow).
static void Route1ReadRows(InstanceHandle wire, std::wstring& outType, std::wstring& outName,
                           std::vector<DevToolsCardRow>& outRows, std::wstring& outAuthoredState,
                           std::wstring& outAuthoredXaml);
static DevToolsWriteOutcome Route1Write(InstanceHandle wire, const std::wstring& prop, const std::wstring& type,
                                   const std::wstring& value, bool confirmBindingReplace);
static bool Route1Clear(InstanceHandle wire, const std::wstring& prop);
static bool Route1Resolve(InstanceHandle wire, const std::wstring& prop, const std::wstring& resourceName, bool asTheme);
static bool Route1Bind(InstanceHandle wire, const std::wstring& op, const std::wstring& prop,
                       const std::wstring& payload, const std::wstring& mode, std::wstring* outJson);
static bool Route1Source(InstanceHandle wire, std::wstring& outFile, unsigned int& outLine, unsigned int& outCol);
static bool Route1Layout(InstanceHandle wire, DevToolsWindowLayout& out);
static bool Route1WireLive(InstanceHandle wire);
static size_t Route1CensusCount();
static bool CardReadInputBridge(IInspectable* input, std::wstring* outText);

// Highlight bridge the Route-1 window calls (on the UI thread) when a node is selected/hovered.
static InstanceHandle RootVisual();
// (A4): both defined later in this TU, alongside the census helpers they read. Declared here because the
// surface-switch verb (Surface.setActive) has to re-anchor the overlay onto a Panel of the TARGET surface,
// and the default argument lives on the definition.
static InstanceHandle CensusRootOf_nolock(InstanceHandle h);
static HRESULT EnsureSelectionHost(InstanceHandle anchor, bool selection = true)
{
    if (!g_diag || !anchor || !WindowElementCurrent_ui(anchor)) return E_INVALIDARG;
    DevToolsSurface surface;
    if (!DevToolsSurface_Resolve(g_diag, anchor, &surface)) return E_PENDING;
    const InstanceHandle panel = FindOutermostPanel(0, surface.xamlRootKey);
    if (!panel) return E_PENDING;
    OverlayBuildScope scope;
    return selection ? DevToolsOverlay_MoveSelectionToPanel(g_diag, panel) :
        DevToolsOverlay_MoveHighlightToPanel(g_diag, panel);
}
// Make another window the inspected surface: picks, hover and the selection then happen there. RootVisual() reads the
// active surface, so every hit-test follows.
static HRESULT SwitchActiveSurface_ui(unsigned long long xamlRootKey)
{
    if (!g_diag) return E_INVALIDARG;
    const InstanceHandle panel = FindOutermostPanel(0, xamlRootKey);
    if (!panel) return E_FAIL;
    OverlayBuildScope scope;
    return DevToolsOverlay_MoveSelectionToPanel(g_diag, panel);
}
static void Route1Highlight(InstanceHandle wire, bool select)
{
    InstanceHandle ih = 0;
    if (wire != 0) {
        const wchar_t* e = nullptr;
        AcquireSRWLockShared(&g_censusLock);
        if (!ResolveWire_nolock((unsigned long long)wire, &ih, &e)) ih = 0; // stale -> clear
        ReleaseSRWLockShared(&g_censusLock);
    }
    if (select) DevToolsOverlay_SelectHandle(g_diag, RootVisual(), ih, OverlaySeed, OverlayContains);
    else        DevToolsOverlay_HighlightHandle(g_diag, RootVisual(), ih, OverlaySeed, OverlayContains);
}

// Written on the UI thread (PublishOverlayPick); plain atomics suffice (no cross-field invariant). Declared here so
// InprocInspect below can read it; PublishOverlayPick (which writes it) lives further down.
static std::atomic<unsigned long long> g_lastPickHandle{0};
static std::atomic<unsigned long>      g_lastPickGen{0};

// What the CLI last pushed with Internal.setComments. Two readers with different needs: the overlay wants
// id/text/anchor to draw a marker, the DevTools window wants the whole record to list. The tap holds it once so
// the two surfaces cannot show different sets. Written on the pipe thread, read on the UI thread -> mutex.
static std::mutex                    g_commentsMutex;
static long long                     g_commentsGeneration = -1; // UI thread only
static std::vector<DevToolsWindowComment> g_comments;
static std::wstring                  g_cliExe;

static bool TapComments(std::vector<DevToolsWindowComment>* out)
{
    if (!out) return false;
    std::lock_guard<std::mutex> lock(g_commentsMutex);
    *out = g_comments;
    return true;
}

// A comment's live anchor -> a WIRE handle the window can select, or 0 when its element isn't realized right
// now. Same resolver the marker re-anchoring uses, so "the window says it's here" and "there's a marker on it"
// can never disagree.
static InstanceHandle TapCommentAnchorWire(const wchar_t* anchor)
{
    InstanceHandle ih = TapResolveAnchor(anchor);
    if (!ih) return 0;
    CensusSharedLock lock;
    return (InstanceHandle)PackWire_nolock(ih);
}

static std::wstring TapQuoteArg(const std::wstring& in)
{
    return DevToolsCommentText::QuoteArgument(in);
}

// That is the ONLY form the DevTools window may use: it calls on the app UI thread, and blocking there for a CLI
// process would freeze the app being inspected. A protocol verb runs on its own per-connection pipe thread, so it
// waits and reports what the child actually did — "CreateProcess succeeded" is not evidence the comment was written.
static int TapRunCli(const std::wstring& args, DWORD waitMs, const std::wstring& revision = L"")
{
    if (!DevToolsTrust_MutationEnabled()) return -4;
    std::wstring exe;
    {
        std::lock_guard<std::mutex> lock(g_commentsMutex);
        exe = g_cliExe;
    }
    if (exe.empty()) return -1;

    std::wstring cmd = TapQuoteArg(exe) + L" " + args;
    if (DevToolsOverlay_GuestCommentsJson() != L"null") {
        if (waitMs == 0) return -5; // Guest UI writes must use the asynchronous acknowledged owner.
        GUID operation{};
        wchar_t value[40]{};
        if (FAILED(CoCreateGuid(&operation)) || !StringFromGUID2(operation, value, _countof(value))) return -2;
        std::wstring key;
        for (const auto c : std::wstring(value)) if (c != L'{' && c != L'}' && c != L'-') key += c;
        cmd += L" --guest-comments " + TapQuoteArg(DevToolsOverlay_GuestCommentToken(key, revision)) + L" --json";
    }
    wchar_t rootBuf[1024]{};
    DWORD n = GetEnvironmentVariableW(L"WINAPP_DEVTOOLS_SOURCE_ROOT", rootBuf, (DWORD)_countof(rootBuf));
    if (n > 0 && n < _countof(rootBuf)) cmd += L" --source-root " + TapQuoteArg(std::wstring(rootBuf, n));

    std::vector<wchar_t> mutableCmd(cmd.begin(), cmd.end());
    mutableCmd.push_back(L'\0');
    STARTUPINFOW si{}; si.cb = sizeof(si);
    PROCESS_INFORMATION pi{};
    if (!CreateProcessW(exe.c_str(), mutableCmd.data(), nullptr, nullptr, FALSE,
                        CREATE_NO_WINDOW | DETACHED_PROCESS, nullptr, nullptr, &si, &pi)) {
        Log(L"comments.cli CreateProcess failed gle=%lu", GetLastError());
        return -2;
    }
    CloseHandle(pi.hThread);
    if (waitMs == 0) { CloseHandle(pi.hProcess); return 0; }

    int result = -3;
    if (WaitForSingleObject(pi.hProcess, waitMs) == WAIT_OBJECT_0) {
        DWORD code = 0;
        result = GetExitCodeProcess(pi.hProcess, &code) ? (int)code : -3;
    }
    CloseHandle(pi.hProcess);
    Log(L"comments.cli exit=%d for `%s`", result, args.c_str());
    return result;
}

// True when the CLI has told us where winapp.exe is (verb `devtools.cli|`). Without it the store has no
// reachable writer, which a mutating verb must say rather than fail obscurely inside CreateProcess.
static bool CliExeRegistered()
{
    std::lock_guard<std::mutex> lock(g_commentsMutex);
    return !g_cliExe.empty();
}

static std::wstring TapMintCommentId()
{
    GUID g{};
    if (FAILED(CoCreateGuid(&g))) {
        // Not a security token — just an id. A clock+counter fallback keeps this from ever returning "cmt_".
        static std::atomic<unsigned long long> s_seq{0};
        wchar_t buf[32]{};
        swprintf_s(buf, L"cmt_%08llx%04llx", (unsigned long long)GetTickCount64() & 0xFFFFFFFFull,
                   (unsigned long long)(++s_seq) & 0xFFFFull);
        return buf;
    }
    wchar_t buf[32]{};
    swprintf_s(buf, L"cmt_%08lx%04hx", g.Data1, g.Data2);
    return buf;
}

static bool TapResolveComment(const wchar_t* id, std::function<void(int)> completed)
{
    if (!id || !*id) return false;
    if (DevToolsOverlay_GuestCommentsJson() != L"null") {
        std::vector<DevToolsWindowComment> items;
        TapComments(&items);
        for (const auto& item : items) if (item.id == id)
            return DevToolsOverlay_ResolveGuestComment(item.id, item.revision, std::move(completed));
        return false;
    }
    std::wstring args = L"devtools comments update --status resolved " + TapQuoteArg(id)
                      + L" --app " + std::to_wstring(GetCurrentProcessId());
    return TapRunCli(args, 0) == 0;   // UI-thread caller: spawn only, never wait
}

static void Route1SnapshotTree(std::vector<DevToolsWindowNode>& outNodes, size_t& outCensusTotal)
{
    outCensusTotal = 0;
    WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::WinSnapshot);
    outNodes = SnapshotFlatCensus(&outCensusTotal);
}

// Called from RebuildTreeRows -- the one place that knows which nodes become rows -- with the render selection, so a
// caption is read exactly when a row will display it and never for a snapshot that gets discarded.
static void Route1FillPreviews(std::vector<DevToolsWindowNode>& nodes, const std::vector<size_t>& indices)
{
    WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::WinPreview);
    size_t read = 0, got = 0;
    for (size_t i : indices) {
        if (i >= nodes.size() || nodes[i].previewRead) continue;
        nodes[i].previewRead = true;
        ++read;
        if (!nodes[i].handle) continue;
        Route1ReadPreview(nodes[i].handle, nodes[i].preview, &nodes[i].searchText);
        if (!nodes[i].preview.empty()) ++got;
    }
    Log(L"route1.preview captured %zu/%zu node previews (%zu rows requested)", got, read, indices.size());
}

// Read from pipe threads (state replies, disconnect) and written from the UI thread, so unlike the overlay state
// tracker further down it carries its own lock -- see DevToolsOwnedState.h.
static DevToolsOwnedState g_ownedState;

// Use CmdBindingWalk for both an installed path and a nonempty proposed tryPath.
// Call from a worker: the shared native walk marshals to the app UI thread itself.
static bool WindowPathWalkBridge(InstanceHandle wire, const std::wstring& prop, const std::wstring& tryPath,
                                 std::wstring* out)
{
    if (!out || !wire || prop.empty()) return false;
    std::wstring reply = CmdBindingWalk((unsigned long long)wire, prop, tryPath);
    // An `ERR <token>` is a handle that no longer resolves. Answer NOTHING rather than a walk-shaped object:
    // an empty walk under a real question reads as "the path is fine", which is the one thing it must not say.
    if (reply.rfind(L"ERR ", 0) == 0) return false;
    *out = reply;
    return true;
}

static DevToolsCardRow ToCardRow(const DevToolsReadProp& p);

// Called on the app UI thread by OnInspectClick, so it snapshots the census and builds the window synchronously (no
// RunOnUiThread hop). Returns true on success so OnInspectClick can fall back to the external host on failure.
static HRESULT OpenInprocWindow(std::wstring* errorMessage = nullptr)
{
    if (!g_diag) { Log(L"route1.inproc no g_diag"); return E_FAIL; }
    std::vector<DevToolsWindowNode> nodes;
    size_t censusTotal = 0;
    Route1SnapshotTree(nodes, censusTotal);
    OverlayBuildScope scope;
    DevToolsWindow_SetCommentBridge(
        &TapComments,
        &TapCommentAnchorWire,
        DevToolsTrust_MutationEnabled() ? &TapResolveComment : nullptr);
    // the window asks for its OWN binding walk, natively. Registered before the open for the
    // same reason the comment bridge is -- the first expansion the user opens must be able to ask.
    DevToolsWindow_SetPathWalk(&WindowPathWalkBridge);
    DevToolsWindow_SetElementResolver([](InstanceHandle wire) -> IInspectable* {
        InstanceHandle ih = 0; const wchar_t* e = nullptr; bool ok;
        AcquireSRWLockShared(&g_censusLock);
        ok = ResolveWire_nolock((unsigned long long)wire, &ih, &e);
        ReleaseSRWLockShared(&g_censusLock);
        IInspectable* element = nullptr;
        if (!ok || !g_diag || FAILED(g_diag->GetIInspectableFromHandle(ih, &element))) return nullptr;
        return element;
    }, [](InstanceHandle wire, const std::vector<std::wstring>& names, std::vector<DevToolsCardRow>* rows) {
        InstanceHandle ih = 0; const wchar_t* e = nullptr; bool ok;
        AcquireSRWLockShared(&g_censusLock);
        ok = ResolveWire_nolock((unsigned long long)wire, &ih, &e);
        ReleaseSRWLockShared(&g_censusLock);
        const std::set<std::wstring> projection(names.begin(), names.end());
        std::vector<DevToolsReadProp> props;
        if (!ok || FAILED(ReadCuratedProps(ih, &props, nullptr, nullptr, &projection))) return false;
        for (const auto& p : props) rows->push_back(ToCardRow(p));
        return true;
    });
    // Hand the last in-app pick (published by PublishOverlayPick as a wire handle) to the window so it opens with
    // that element selected. 0 when nothing was picked (plain toolbar Inspect) -> window opens with no selection.
    InstanceHandle preselect = g_lastPickHandle.load(std::memory_order_relaxed);
    // "Open in DevTools" names its element, which a comment marker opens without a pick.
    if (const InstanceHandle reveal = DevToolsOverlay_InspectorTarget()) {
        unsigned long long wire;
        { AcquireSRWLockShared(&g_censusLock); wire = PackWire_nolock(reveal); ReleaseSRWLockShared(&g_censusLock); }
        if (wire) { preselect = wire; g_lastPickHandle.store(wire, std::memory_order_relaxed); }
    }
    const bool canMutate = DevToolsTrust_MutationEnabled();
    HRESULT hr = DevToolsWindow_Open(g_diag, nodes, censusTotal, &Route1ReadRows,
                                [](IInspectable* box, std::wstring& t) { return CardReadInputBridge(box, &t); },
                            canMutate ? &Route1Write : nullptr,
                            canMutate ? &Route1Clear : nullptr,
                            canMutate ? &Route1Resolve : nullptr,
                            canMutate ? &Route1Bind : nullptr,
                            &Route1Source, &Route1Layout, &Route1Highlight,
                            &Route1WireLive, &Route1CensusCount, &Route1SnapshotTree, &Route1FillPreviews,
                            OverlaySeed, preselect, errorMessage);
    Log(L"route1.inproc DevToolsWindow_Open hr=0x%08X nodes=%zu", hr, nodes.size());
    return hr;
}

static bool InprocInspect()
{
    g_ownedState.Reconcile(DevToolsStateAxis::Window, DevToolsWindow_IsOpen() ? 1ull : 0ull);
    HRESULT hr = OpenInprocWindow();
    const bool open = SUCCEEDED(hr) && DevToolsWindow_IsOpen();
    g_ownedState.Reconcile(DevToolsStateAxis::Window, open ? 1ull : 0ull);
    return open;
}


// Read a property's concrete resource value on the UI thread. The chain supplies its value
// string; color normalization is separate from authored static/theme classification.
struct ResolveState { std::wstring type; std::wstring value; };
static HRESULT ResolveResourceValue(InstanceHandle h, const std::wstring& prop, ResolveState* st)
{
    if (!g_vts3) return E_FAIL;
    unsigned int sc = 0, pc = 0;
    PropertyChainSource* s = nullptr; PropertyChainValue* v = nullptr;
    HRESULT hr = ReadPropChain(h, &sc, &s, &pc, &v);
    if (SUCCEEDED(hr) && v) {
        for (unsigned int i = 0; i < pc; i++) {
            if (v[i].PropertyName && prop == v[i].PropertyName) {
                st->type = v[i].ValueType ? v[i].ValueType : (v[i].Type ? v[i].Type : L"");
                // A null value arrives as the handle text "0"; show it as empty, like the property rows.
                st->value = ((long long)v[i].MetadataBits & 0x20) || !v[i].Value ? L"" : v[i].Value;
                break;
            }
        }
    }
    FreePropChain(sc, s, pc, v);
    return hr;
}

// The DISPLAY name for a type the diagnostics property chain reported. See DevToolsRead_ShortTypeName: the chain
// reports two different shapes and the assembly-qualified one cannot be reduced by "last dotted segment".
static std::wstring ShortTypeName(const std::wstring& t)
{
    return DevToolsRead_ShortTypeName(t);
}

// `isObject` is MetadataBit::IsValueHandle - the runtime's own statement that this Value is an object reference
// rather than a display string.
static std::wstring ResolveDisplayValue(const std::wstring& name, const std::wstring& valueType,
                                        const std::wstring& raw, bool isObject,
                                        std::wstring* effectiveType, bool* readable)
{
    if (readable) *readable = !isObject;
    if (!isObject) return raw;

    const InstanceHandle handle = wcstoull(raw.c_str(), nullptr, 10);
    winrt::com_ptr<IInspectable> value;
    if (handle && g_diag) {
        HRESULT hr = g_diag->GetIInspectableFromHandle(handle, value.put());
        std::wstring text;
        if (SUCCEEDED(hr) && !value) hr = E_UNEXPECTED;
        if (SUCCEEDED(hr)) {
            hr = ReadInspectableValue(value.get(), name, valueType, &text, effectiveType);
            if (hr == S_OK) {
                if (readable) *readable = true;
                return text;
            }
        }
        if (FAILED(hr)) Log(L"property.read %s: object value unavailable handle=%llu hr=0x%08X", name.c_str(), handle, hr);
    }

    // Unresolved object value: show the type instead of a raw handle.
    // DataContext is the exception: its whole value IS its type, so the plain short type name is the answer,
    // not a "{Braced}" placeholder that reads like a failure.
    if (name == L"DataContext") { std::wstring d = ShortTypeName(valueType); return d.empty() ? std::wstring(L"(object)") : d; }
    std::wstring st = ShortTypeName(valueType);
    return st.empty() ? std::wstring(L"{object}") : (L"{" + st + L"}");
}

static std::wstring CmdResourceResolve(const std::vector<std::wstring>& p)
{
    if (p.size() < 3 || p[1].empty() || p[2].empty()) return L"ERR bad-args";
    // Validate the opaque handle, then re-resolve in the UI closure before the live read.
    unsigned long long wire = wcstoull(p[1].c_str(), nullptr, 10);
    std::wstring prop = p[2];
    { InstanceHandle ih0 = 0; const wchar_t* e = nullptr; bool ok;
      AcquireSRWLockShared(&g_censusLock); ok = ResolveWire_nolock(wire, &ih0, &e); ReleaseSRWLockShared(&g_censusLock);
      if (!ok) return std::wstring(L"ERR ") + e; }
    auto st = std::make_shared<ResolveState>();
    auto rerr = std::make_shared<const wchar_t*>(nullptr);
    bool completed = false;
    HRESULT hr = RunOnUiThread([wire, prop, st, rerr]() -> HRESULT {
        InstanceHandle ih = 0; const wchar_t* e = nullptr; bool ok;
        AcquireSRWLockShared(&g_censusLock); ok = ResolveWire_nolock(wire, &ih, &e); ReleaseSRWLockShared(&g_censusLock);
        if (!ok) { *rerr = e; return E_INVALIDARG; }
        return ResolveResourceValue(ih, prop, st.get());
    }, &completed);
    if (completed && *rerr) return std::wstring(L"ERR ") + *rerr;
    if (FAILED(hr)) return L"ERR " + HrStr(hr);
    std::wstring value = DevToolsRead_NormalizeColor(st->value);
    bool isColor = (!value.empty() && value[0] == L'#');
    std::wstring valueType = isColor ? std::wstring(L"Color") : st->type;
    return DevToolsRead_SerializeResolve(prop, value, valueType);
}

// The overlay build is deferred out of Worker to an explicit `overlay.show` request so the --json/oracle path (which
// never sends it) stays headless by construction.
static const wchar_t* TryBuildOverlay(const std::wstring& eventOrigin = L"null",
                                     unsigned long long ownerConn = 0, std::wstring* errorMessage = nullptr);
static const wchar_t* TryBuildOverlayOnUiThread(std::wstring* errorMessage = nullptr);
static void OnOverlayTreeChanged();
static std::atomic<bool> g_overlayBuilt{false};
static std::wstring TypeOf(InstanceHandle h);
static std::wstring NameOf(InstanceHandle h);
// Census x:Name lookup WITHOUT taking the lock (caller already holds it). Used by the JSON dispatch to label a
// handle-addressed write in its Property.changed payload while it already holds the shared census lock.
static std::wstring NameOf_nolock(InstanceHandle h);
static InstanceHandle RootVisual();

// The generation counter increments on every pick so the window can tell a fresh pick from a repeat of the same
// handle. (Definitions moved above InprocInspect so it can pre-select the picked node on window open.)
static bool PublishOverlayPick(InstanceHandle h, bool releaseCommit) {
    // Publish it as the opaque WIRE handle so the inspector window - which only ever sees wire handles (from
    // visualtree.enumerate) - matches it against its tree and auto-selects. Pack under the census lock (SHARED:
    // PackWire only reads g_ihToSlot/g_slots).
    unsigned long long wire;
    { AcquireSRWLockShared(&g_censusLock); wire = PackWire_nolock(h); ReleaseSRWLockShared(&g_censusLock); }
    g_lastPickHandle.store(wire, std::memory_order_relaxed);
    unsigned long long gen = g_lastPickGen.fetch_add(1, std::memory_order_release) + 1;

    // PublishOverlayPick runs on the UI thread, as does the in-proc window, so the call is direct. wire==0 (no live
    // slot) is the window's "no selection" sentinel, matching the Selection.picked broadcast below.
    const bool claimedByWindow = DevToolsWindow_IsOpen() && DevToolsWindow_OnPicked(wire, releaseCommit);

    // This runs on the UI thread, so it must only ENQUEUE — DevToolsEvents_Broadcast does exactly that (a slow
    // client's writer thread does the blocking I/O).
    std::wstring params = L"{\"generation\":" + std::to_wstring(gen) +
                          L",\"handle\":\"" + std::to_wstring(wire) + L"\"}";
    DevToolsEvents_Broadcast(DevToolsDomain_Selection, DevToolsRpcEvent(L"Selection.picked", params, L"null"),
                        DevToolsCoalesce_KeepNewest, L"picked");
    return claimedByWindow;
}

// UI thread only: a missing cache entry is read from the live property chain.
static std::wstring SourceOf_ui(InstanceHandle h, unsigned int* line = nullptr, unsigned int* column = nullptr)
{
    AcquireSRWLockShared(&g_sourceUriLock);
    const auto it = g_sourceInfo.find(h);
    const bool cached = it != g_sourceInfo.end();
    std::wstring file = cached ? it->second.file : std::wstring();
    if (cached && line) *line = it->second.line;
    if (cached && column) *column = it->second.column;
    ReleaseSRWLockShared(&g_sourceUriLock);
    if (cached) return file;
    file = BackfillSourceInfo(h);
    return line || column ? SourceOf_ui(h, line, column) : file;
}

// With a bound source root, "authored" means a file under it: the package scan misses XAML compiled into
// resources.pri, and a partial scan would misclassify the app's own pages.
static bool IsAuthoredSource(const std::wstring& source)
{
    return DevToolsAuthored_HasSourceRoot() ? DevToolsAuthored_IsProjectSource(source) : DevToolsAppXaml_IsAppAuthored(source);
}

// The element the developer declared: authored source, and not a part of a ControlTemplate (the app's own restyled
// templates included), whose parts the developer never declares per instance.
static bool IsDeclaredElement(InstanceHandle h)
{
    unsigned int line = 0, column = 0;
    const std::wstring source = SourceOf_ui(h, &line, &column);
    return !source.empty() && IsAuthoredSource(source) && !DevToolsAuthored_IsControlTemplatePart(source, line, column);
}

// A pick selects the author's element, not a control template's part or runtime-generated text (a Button's string
// content, a placeholder): any other hit resolves to its nearest declared ancestor. A hit with no such ancestor is
// kept.
static InstanceHandle PickTarget(InstanceHandle hit)
{
    if (DevToolsWindow_ShowsFrameworkRows()) return hit;
    if (IsDeclaredElement(hit)) return hit;
    std::vector<InstanceHandle> ancestors;
    AcquireSRWLockShared(&g_censusLock);
    for (InstanceHandle current = hit; ancestors.size() < 256;) {
        const auto parent = g_parent.find(current);
        if (parent == g_parent.end() || !parent->second) break;
        current = parent->second;
        ancestors.push_back(current);
    }
    ReleaseSRWLockShared(&g_censusLock);
    for (const InstanceHandle ancestor : ancestors) {
        if (IsDeclaredElement(ancestor)) return ancestor;
    }
    // Nothing declared above it (a generated list item, a menu built in code): select the control the hit belongs
    // to, such as the ComboBoxItem or MenuFlyoutItem, rather than a part of its template.
    auto isControl = [](InstanceHandle h) {
        winrt::com_ptr<IInspectable> object;
        if (!g_diag || FAILED(g_diag->GetIInspectableFromHandle(h, object.put())) || !object) return false;
        winrt::com_ptr<IUnknown> control;
        return SUCCEEDED(object->QueryInterface(DevToolsIid<DevToolsXC::IControl>(), control.put_void()));
    };
    if (isControl(hit)) return hit;
    for (const InstanceHandle ancestor : ancestors) {
        if (isControl(ancestor)) return ancestor;
    }
    return hit;
}

static bool OnOverlayReleasedPick(InstanceHandle h)
{
    return PublishOverlayPick(h, true);
}

// `origin` is the requesting client's SESSION connection, unlike an in-app pick's `null`: the event exists so other
// clients converge, and the client that caused it must be able to recognise its own write echoed back rather than
// applying it again and bouncing another select at the target.
static bool PublishProtocolSelection(unsigned long long wire, const std::wstring& origin)
{
    if (g_lastPickHandle.load(std::memory_order_relaxed) == wire) {
        // Selecting the element already shown is a request to look again.
        DevToolsWindow_RereadSelection(wire);
        return false;
    }
    g_lastPickHandle.store(wire, std::memory_order_relaxed);
    const unsigned long long gen = g_lastPickGen.fetch_add(1, std::memory_order_release) + 1;
    if (DevToolsWindow_IsOpen()) DevToolsWindow_OnPicked(wire, /*releaseCommit*/ false);
    const std::wstring params = L"{\"generation\":" + std::to_wstring(gen) +
                                L",\"handle\":\"" + std::to_wstring(wire) + L"\"}";
    DevToolsEvents_Broadcast(DevToolsDomain_Selection, DevToolsRpcEvent(L"Selection.picked", params, origin),
                        DevToolsCoalesce_KeepNewest, L"picked");
    return true;
}

static void OnOverlayFocused(InstanceHandle h) {
    unsigned long long wire;
    { AcquireSRWLockShared(&g_censusLock); wire = PackWire_nolock(h); ReleaseSRWLockShared(&g_censusLock); }
    if (!wire) return;

    g_lastPickHandle.store(wire, std::memory_order_relaxed);
    unsigned long long gen = g_lastPickGen.fetch_add(1, std::memory_order_release) + 1;
    if (DevToolsWindow_IsOpen()) DevToolsWindow_OnFocusChanged(wire);

    std::wstring params = L"{\"generation\":" + std::to_wstring(gen) +
                          L",\"handle\":\"" + std::to_wstring(wire) + L"\"}";
    DevToolsEvents_Broadcast(DevToolsDomain_Focus, DevToolsRpcEvent(L"Focus.changed", params, L"null"),
                        DevToolsCoalesce_KeepNewest, L"changed");
}
