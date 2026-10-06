// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsTap.cpp; included into that translation unit, not compiled on its own.

// Both live here, beside the other diagnostics primitives (SetLiteral/FindPropIndex/ResolveResourceApply)
// they are built out of. UI thread only - every entry point below is called from inside a RunOnUiThread op.

struct DevToolsEnumTable
{
    std::vector<int>          ints;
    std::vector<std::wstring> names;
};

// Every enum the target app knows, keyed by BOTH the full type name and its short (last-dotted-segment) form,
// because GetPropertyValuesChain's ValueType spelling is not guaranteed to match GetEnums' Name spelling.
static std::map<std::wstring, DevToolsEnumTable> g_enumTables;
static bool g_enumTablesLoaded = false;

// It stays as a FALLBACK (see EnumTableFor), so a GetEnums failure degrades to today's behaviour instead of silently
// losing every enum dropdown.
static void LoadEnumTables()
{
    if (g_enumTablesLoaded) return;
    g_enumTablesLoaded = true; // one attempt per session: a failure must not re-walk on every property read
    if (!g_vts3) return;
    unsigned int count = 0;
    EnumType* enums = nullptr;
    HRESULT hr = g_vts3->GetEnums(&count, &enums);
    if (FAILED(hr) || !enums) { Log(L"enums: GetEnums hr=0x%08X count=%u (falling back to curated table)", hr, count); return; }
    for (unsigned int i = 0; i < count; i++) {
        std::wstring typeName = enums[i].Name ? enums[i].Name : L"";
        DevToolsEnumTable t;
        LONG lo = 0, hi = -1;
        if (enums[i].ValueStrings && SUCCEEDED(SafeArrayGetLBound(enums[i].ValueStrings, 1, &lo))
                                  && SUCCEEDED(SafeArrayGetUBound(enums[i].ValueStrings, 1, &hi))) {
            for (LONG k = lo; k <= hi; k++) {
                BSTR nm = nullptr;
                if (SUCCEEDED(SafeArrayGetElement(enums[i].ValueStrings, &k, &nm)) && nm) {
                    t.names.push_back(nm);
                    SysFreeString(nm);
                } else {
                    t.names.push_back(L"");
                }
            }
        }
        LONG ilo = 0, ihi = -1;
        if (enums[i].ValueInts && SUCCEEDED(SafeArrayGetLBound(enums[i].ValueInts, 1, &ilo))
                               && SUCCEEDED(SafeArrayGetUBound(enums[i].ValueInts, 1, &ihi))) {
            for (LONG k = ilo; k <= ihi; k++) {
                int iv = 0;
                if (SUCCEEDED(SafeArrayGetElement(enums[i].ValueInts, &k, &iv))) t.ints.push_back(iv);
            }
        }
        if (!typeName.empty() && !t.names.empty()) {
            size_t dot = typeName.find_last_of(L'.');
            if (dot != std::wstring::npos) g_enumTables[typeName.substr(dot + 1)] = t;
            g_enumTables[typeName] = t;
        }
    }
    for (unsigned int i = 0; i < count; i++) {
        SysFreeString(enums[i].Name);
        if (enums[i].ValueInts)    SafeArrayDestroy(enums[i].ValueInts);
        if (enums[i].ValueStrings) SafeArrayDestroy(enums[i].ValueStrings);
    }
    CoTaskMemFree(enums);
    Log(L"enums: loaded %zu enum tables from GetEnums", g_enumTables.size());
}

// Member names for `type`, preferring the runtime's GetEnums tables and falling back to the shared curated
// helper (Visibility + the two alignments) so a GetEnums failure never costs an enum editor that works today.
// Empty -> not an enum.
static std::vector<std::wstring> EnumTableFor(const std::wstring& type)
{
    if (type.empty()) return {};
    LoadEnumTables();
    auto it = g_enumTables.find(type);
    if (it != g_enumTables.end()) return it->second.names;
    size_t dot = type.find_last_of(L'.');
    if (dot != std::wstring::npos) {
        it = g_enumTables.find(type.substr(dot + 1));
        if (it != g_enumTables.end()) return it->second.names;
    }
    return DevToolsEnumMembersFor(type.substr(dot == std::wstring::npos ? 0 : dot + 1));
}

static std::wstring EnumNameForValue(const std::wstring& type, const std::wstring& raw)
{
    if (raw.empty()) return L"";
    LoadEnumTables();
    const DevToolsEnumTable* t = nullptr;
    auto it = g_enumTables.find(type);
    if (it == g_enumTables.end()) {
        size_t dot = type.find_last_of(L'.');
        if (dot != std::wstring::npos) it = g_enumTables.find(type.substr(dot + 1));
    }
    if (it != g_enumTables.end()) t = &it->second;

    wchar_t* end = nullptr;
    long n = wcstol(raw.c_str(), &end, 10);
    const bool numeric = end && *end == L'\0' && !raw.empty();
    if (t && numeric) {
        for (size_t k = 0; k < t->ints.size() && k < t->names.size(); ++k)
            if (t->ints[k] == (int)n) return t->names[k];
        if (n >= 0 && (size_t)n < t->names.size()) return t->names[(size_t)n];
        return L"";
    }
    if (numeric) {
        // No GetEnums table: fall back to the shared curated declaration order (same mapping the in-proc
        // surfaces already use), so Visibility/alignments still cross the wire as names.
        std::vector<std::wstring> members = EnumTableFor(type);
        if (n >= 0 && (size_t)n < members.size()) return members[(size_t)n];
        return L"";
    }
    return raw;
}

// An identity-backed resourceKey (activate a scratch element, ResolveResource each curated theme-brush name onto it,
// match a property's brush by OBJECT IDENTITY) could not be made deterministic: ResolveResource against a DETACHED
// element resolves only some names, and which ones varied per run (3 to 14 of 15 curated brushes across successive
// launches).


// Additive, risk-tier-0 (read; session-grant, no consent) verbs. visualtree.enumerate is served ENTIRELY from the
// census (no live COM, no UI-thread dispatch - so it cannot deadlock or return RPC_E_WRONG_THREAD, and it never
// surfaces our own overlay subtree because the census already excludes it). property.get and resource.resolve need
// live values, so they read on the app UI thread via RunOnUiThread - the exact proven path SetLiteral/FindPropIndex
// use.

// Immutable census snapshot the enumerate serializer walks AFTER the shared lock is released. C5:
// copy-then-release keeps the census lock leaf-scoped (held only for the O(map-copy) window) and removes any
// throw-while-locked path - a std::bad_alloc while building the JSON can't strand the lock under a mutation storm.
struct CensusSnapshot {
    std::map<InstanceHandle, std::wstring> type;
    std::map<InstanceHandle, std::vector<InstanceHandle>> children;
    std::map<InstanceHandle, InstanceHandle> parent;
    std::map<InstanceHandle, std::wstring> name;
    std::map<InstanceHandle, unsigned long long> wire;
    std::map<InstanceHandle, std::wstring> sourceUri;
    std::map<InstanceHandle, std::wstring> id;                        // handle -> stable structural identity
    std::set<InstanceHandle> uniqueName;
};

// RAII guard: take g_censusLock SHARED, release on scope exit even if a map copy throws. Guarantees the
// enumerate critical section can never leak the lock and wedge every future mutation/enumerate (H2).
struct CensusSharedLock {
    CensusSharedLock()  { AcquireSRWLockShared(&g_censusLock); }
    ~CensusSharedLock() { ReleaseSRWLockShared(&g_censusLock); }
    CensusSharedLock(const CensusSharedLock&) = delete;
    CensusSharedLock& operator=(const CensusSharedLock&) = delete;
};

// Capture source and authored ancestor identity under one census snapshot. Names alone are local to a
// namescope: the global last-name index cannot distinguish HomePage, DetailsPage and a dialog.
static const int kMaxTreeWalk = 20000;
static bool TapAnchorOf(InstanceHandle h, std::wstring* outAnchor, bool* unique = nullptr, const wchar_t** reason = nullptr)
{
    if (!h || !outAnchor) return false;
    CensusSharedLock lock;
    const auto sourceEpoch = g_sourceEpoch.load(std::memory_order_acquire);
    std::map<InstanceHandle, std::wstring> sources;
    SourceInfo_Snapshot(sources);
    *outAnchor = DevToolsCommentAnchor::Capture(h, g_nameOf, g_type, sources, g_parent, g_children);
    if (unique) {
        if (reason) *reason = L"incomplete-census";
        *unique = !g_adviseFailed.load() && g_type.size() <= kMaxTreeWalk &&
            DevToolsCommentAnchor::IsUnique(h, *outAnchor, g_nameOf, g_type, sources, g_parent, g_children, reason);
        if (sourceEpoch != g_sourceEpoch.load(std::memory_order_acquire)) {
            *unique = false;
            if (reason) *reason = L"source-epoch-changed";
        }
    }
    return !outAnchor->empty();
}

// resolve an anchor from TapAnchorOf back to a live handle. 0 when it does not currently resolve, which
// the caller must treat as "not back yet" rather than "gone forever" — the page may not have been rebuilt at
// the moment we looked.
static InstanceHandle TapResolveAnchor(const wchar_t* anchor)
{
    if (!anchor || !*anchor) return 0;
    CensusSharedLock lock;
    std::map<InstanceHandle, std::wstring> sources;
    SourceInfo_Snapshot(sources);
    return DevToolsCommentAnchor::Resolve(std::wstring(anchor), g_nameOf, g_type, sources, g_parent, g_children);
}

// Caller must hold g_censusLock (shared suffices - this only reads g_type). Overlay elements never enter g_type (they
// self-exclude at Add), so this is non-overlay by construction.
static size_t CensusPopulation_nolock() {
    // While the comment flyout is open, report the frozen pre-card baseline so the card's transient,
    // handle-unexcludable async template parts don't inflate the app-only oracle (see g_cardFrozen above).
    if (g_cardFrozen.load(std::memory_order_acquire)) return g_cardFrozenCount.load(std::memory_order_acquire);
    size_t n = 0;
    // Inclusion predicate: a handle KEYED in g_type is a live census node - the exact test enumerate applies
    // (SnapshotNode emits a child IFF cs.type.find(child) != end, and every g_type entry is surfaced once by the root
    // walk + orphan sweep).
    for (const auto& kv : g_type) { (void)kv; ++n; }
    return n;
}

static const int kMaxEnumDepth = 512;

// Census snapshot budget shared by enumerate, app-authored classification, and the inspector window.
// VisualElement::SrcInfo is banked when Add delivers it; unresolved warm-up gaps are backfilled progressively
// in bounded batches rather than one multi-second UI-thread pass.

static bool WindowOwnedByProcess(HWND hwnd)
{
    DWORD pid = 0;
    return IsWindow(hwnd) && GetWindowThreadProcessId(hwnd, &pid) && pid == GetCurrentProcessId();
}

static bool WithinRoot_nolock(InstanceHandle raw, InstanceHandle root)
{
    if (!root) return true;
    std::set<InstanceHandle> visited;
    while (raw && visited.size() < kMaxEnumDepth && visited.insert(raw).second) {
        if (raw == root) return true;
        const auto parent = g_parent.find(raw);
        raw = parent == g_parent.end() ? 0 : parent->second;
    }
    return false;
}

static bool WindowSurfaceOf_ui(InstanceHandle raw, DevToolsSurface& surface)
{
    // Non-UI nodes (for example text runs) inherit their visual parent's surface.
    // A detached UIElement must not inherit a stale census parent's window.
    std::set<InstanceHandle> visited;
    while (raw && visited.size() < kMaxEnumDepth && visited.insert(raw).second) {
        winrt::com_ptr<IInspectable> object;
        if (!g_diag || FAILED(g_diag->GetIInspectableFromHandle(raw, object.put())) || !object) return false;
        winrt::com_ptr<IUnknown> ui;
        if (SUCCEEDED(object->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), ui.put_void())))
            return DevToolsSurface_Resolve(g_diag, raw, &surface);
        CensusSharedLock lock;
        const auto parent = g_parent.find(raw);
        raw = parent == g_parent.end() ? 0 : parent->second;
    }
    return false;
}

static bool WindowElementCurrent_ui(InstanceHandle raw)
{
    if (!g_windowScope) return true;
    const auto member = g_windowScope->members.find(raw);
    if (member == g_windowScope->members.end() || !WindowOwnedByProcess(g_windowScope->hwnd)) return false;
    {
        CensusSharedLock lock;
        if (!g_type.count(raw) || PackWire_nolock(raw) != member->second.first) return false;
        if (g_windowScope->root &&
            (PackWire_nolock(g_windowScope->root) != g_windowScope->rootWire ||
             !WithinRoot_nolock(raw, g_windowScope->root))) return false;
    }
    DevToolsSurface surface;
    return WindowSurfaceOf_ui(raw, surface) && surface.hostHwnd == g_windowScope->hwnd &&
        surface.xamlRootKey == member->second.second;
}

static InstanceHandle FindOutermostPanel(InstanceHandle wantCensusRoot, unsigned long long xamlRootKey = 0);

static HRESULT CaptureWindowScope_ui(TapWindowScope& scope)
{
    if (scope.rootWire) {
        {
            CensusSharedLock lock;
            const wchar_t* error = nullptr;
            if (!ResolveWire_nolock(scope.rootWire, &scope.root, &error)) return E_INVALIDARG;
        }
        DevToolsSurface surface;
        if (!WindowSurfaceOf_ui(scope.root, surface) || (scope.hwnd && scope.hwnd != surface.hostHwnd))
            return E_INVALIDARG;
        scope.hwnd = surface.hostHwnd;
    }
    if (!WindowOwnedByProcess(scope.hwnd)) return E_INVALIDARG;
    std::vector<std::pair<InstanceHandle, unsigned long long>> candidates;
    {
        CensusSharedLock lock;
        if (g_adviseFailed.load() || g_type.size() > kMaxTreeWalk) return E_FAIL;
        for (const auto& item : g_type)
            if (WithinRoot_nolock(item.first, scope.root))
                candidates.emplace_back(item.first, PackWire_nolock(item.first));
    }
    std::set<unsigned long long> rootKeys;
    const auto started = std::chrono::steady_clock::now();
    for (const auto& candidate : candidates) {
        if (std::chrono::steady_clock::now() - started > std::chrono::seconds(2)) return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
        DevToolsSurface surface;
        if (WindowSurfaceOf_ui(candidate.first, surface) && surface.hostHwnd == scope.hwnd) {
            scope.members.emplace(candidate.first, std::make_pair(candidate.second, surface.xamlRootKey));
            rootKeys.insert(surface.xamlRootKey);
        }
    }
    // A window also contains PopupRoot census nodes. Capture the confirmed app panel on
    // the UI thread; RootVisual is also read by callers before UI dispatch.
    if (!scope.root && rootKeys.size() == 1) {
        const auto panel = FindOutermostPanel(0, *rootKeys.begin());
        if (scope.members.count(panel)) scope.pickRoot = panel;
    }
    return scope.members.empty() ? E_INVALIDARG : S_OK;
}

static bool FilterWindowCensus(CensusSnapshot& cs)
{
    if (!g_windowScope) return true;
    for (const auto& member : g_windowScope->members) {
        const auto wire = cs.wire.find(member.first);
        if (wire == cs.wire.end() || wire->second != member.second.first) {
            cs.type.clear();
            return false;
        }
    }
    for (auto it = cs.type.begin(); it != cs.type.end();) {
        if (!g_windowScope->members.count(it->first)) it = cs.type.erase(it);
        else ++it;
    }
    return true;
}

static bool WindowScopeCurrent()
{
    return !g_windowScope || SUCCEEDED(RunOnUiThread([]() { return S_OK; }));
}

// Bound preview batches and their UI-thread work. Match the schema maxItems value, and use
// TapPreviewBatch timing to assess the cost of projected caption reads.
static const size_t kMaxPreviewBatch = 256;

// A depth-first walk with a single global budget does not degrade, it AMPUTATES: DFS spends the whole budget on
// whichever subtree it enters first, so later siblings go missing ENTIRELY rather than being thinned.
static void SelectBoundedBreadthFirst(const CensusSnapshot& cs,
                                      const std::vector<InstanceHandle>& roots,
                                      int maxDepth,
                                      int& budget,
                                      std::set<InstanceHandle>& included)
{
    std::vector<InstanceHandle> frontier;
    for (InstanceHandle r : roots) {
        if (budget <= 0) break;
        if (cs.type.find(r) == cs.type.end()) continue;   // Never admit a non-census handle.
        if (!included.insert(r).second) continue;
        --budget;
        frontier.push_back(r);
    }

    for (int depth = 0;
         !frontier.empty() && budget > 0 && depth < kMaxEnumDepth && (maxDepth < 0 || depth < maxDepth);
         ++depth)
    {
        std::vector<InstanceHandle> next;
        for (size_t pass = 0; budget > 0; ++pass) {
            bool admittedAny = false;
            for (InstanceHandle h : frontier) {
                if (budget <= 0) break;
                auto ch = cs.children.find(h);
                if (ch == cs.children.end() || pass >= ch->second.size()) continue;
                InstanceHandle c = ch->second[pass];
                if (cs.type.find(c) == cs.type.end()) continue;
                admittedAny = true;
                if (!included.insert(c).second) continue;
                --budget;
                next.push_back(c);
            }
            if (!admittedAny) break;
        }
        frontier.swap(next);
    }
}

// A root has no in-census parent, including reparenting under an uncensused handle.
// This keeps every census subtree reachable and enumeration consistent with its population.
static std::vector<InstanceHandle> CollectRoots(const CensusSnapshot& cs)
{
    std::vector<InstanceHandle> roots;
    for (auto& kv : cs.type) {
        auto pit = cs.parent.find(kv.first);
        if (pit != cs.parent.end() && cs.type.find(pit->second) != cs.type.end()) continue;
        roots.push_back(kv.first);
    }
    return roots;
}

// Resolve surface candidates from census roots and their first-child chains. Hosting roots
// need not be UIElements, so a descendant may be the first node with a XamlRoot.
// DevToolsSurface_ResolveAll deduplicates roots shared with popups. UI thread; shared census lock only.
static std::vector<InstanceHandle> TapCensusRoots()
{
    static const int kChainDepth = 8;   // deeper than any host-node stack seen; bounded so a cycle cannot spin
    std::vector<InstanceHandle> roots;
    CensusSharedLock lock;
    for (auto& kv : g_type) {
        auto pit = g_parent.find(kv.first);
        if (pit != g_parent.end() && g_type.find(pit->second) != g_type.end()) continue;
        InstanceHandle cur = kv.first;
        for (int d = 0; d < kChainDepth && cur; ++d) {
            roots.push_back(cur);
            auto cit = g_children.find(cur);
            if (cit == g_children.end() || cit->second.empty()) break;
            cur = cit->second[0];
        }
    }
    return roots;
}

// See DevToolsTreeLayout's identity block for why the id is a path fold rather than a property of the element alone:
// it is what makes two DataTemplate instances of one x:Name individually addressable, and what makes a moved
// element's selector fail loudly instead of resolving to a similar element.
static void FoldIdentities(const CensusSnapshot& cs, InstanceHandle h, unsigned long long value,
                           std::map<InstanceHandle, unsigned long long>& values, int depth)
{
    if (depth > kMaxEnumDepth) return;
    if (!values.emplace(h, value).second) return;
    auto ch = cs.children.find(h);
    if (ch == cs.children.end()) return;
    std::map<std::wstring, int> perType;
    for (InstanceHandle c : ch->second) {
        auto ty = cs.type.find(c);
        if (ty == cs.type.end()) continue;          // never in the census -> never in the identity space
        const int ordinal = perType[ty->second]++;
        if (values.find(c) != values.end()) continue;
        auto nm = cs.name.find(c);
        const std::wstring segment = DevToolsTreeLayout::IdentitySegment(
            ty->second, nm == cs.name.end() ? std::wstring() : nm->second, ordinal);
        FoldIdentities(cs, c, DevToolsTreeLayout::IdentityFold(value, segment), values, depth + 1);
    }
}

// Compute the facts a client's SELECTOR vocabulary needs, over the WHOLE census rather than over whatever a
// particular request selected. Deriving either from the request's own bounded walk would make an element's selector
// depend on the --depth someone happened to pass, so the same element would print two different selectors.
static void ComputeSelectorFacts(CensusSnapshot& cs)
{
    std::map<InstanceHandle, unsigned long long> values;
    std::map<std::wstring, int> perRootType;
    for (InstanceHandle root : CollectRoots(cs)) {
        auto ty = cs.type.find(root);
        if (ty == cs.type.end()) continue;
        const int ordinal = perRootType[ty->second]++;
        if (values.find(root) != values.end()) continue;
        auto nm = cs.name.find(root);
        const std::wstring segment = DevToolsTreeLayout::IdentitySegment(
            ty->second, nm == cs.name.end() ? std::wstring() : nm->second, ordinal);
        FoldIdentities(cs, root, DevToolsTreeLayout::IdentityFold(DevToolsTreeLayout::kIdentitySeed, segment), values, 0);
    }

    cs.id.clear();
    for (const auto& kv : values) cs.id[kv.first] = DevToolsTreeLayout::IdentityToken(kv.second);

    std::map<std::wstring, int> nameCounts;
    for (const auto& kv : cs.name) {
        if (kv.second.empty() || cs.type.find(kv.first) == cs.type.end()) continue;
        ++nameCounts[kv.second];
    }

    cs.uniqueName.clear();
    for (const auto& kv : cs.name) {
        if (kv.second.empty() || cs.type.find(kv.first) == cs.type.end()) continue;
        if (nameCounts[kv.second] == 1) cs.uniqueName.insert(kv.first);
    }
}

// No budget here: the bound was already applied by SelectBoundedBreadthFirst, so this is a pure shaping pass and the
// emitted population is exactly `included`. childCount still reports the TRUE child count even where children were
// not included, so a client can tell a thinned node from a leaf and re-enumerate that subtree by handle.
static DevToolsReadNode SnapshotNode(const CensusSnapshot& cs, InstanceHandle h, int curDepth,
                                const std::set<InstanceHandle>& included,
                                std::set<InstanceHandle>& emitted)
{
    DevToolsReadNode n;
    // Read the wire handle prepacked under the census lock; emission itself is lock-free.
    auto wr = cs.wire.find(h);
    n.handle = (wr != cs.wire.end()) ? wr->second : 0ull;
    auto nm = cs.name.find(h);  if (nm != cs.name.end()) n.name = nm->second;
    auto ty = cs.type.find(h);  n.type = (ty != cs.type.end()) ? ty->second : std::wstring(L"?");
    auto su = cs.sourceUri.find(h); if (su != cs.sourceUri.end()) n.file = su->second;
    auto idv = cs.id.find(h);   if (idv != cs.id.end()) n.id = idv->second;
    n.uniqueName = cs.uniqueName.find(h) != cs.uniqueName.end();
    auto ch = cs.children.find(h);
    n.childCount = (ch != cs.children.end()) ? (unsigned int)ch->second.size() : 0u;
    if (curDepth >= kMaxEnumDepth || ch == cs.children.end()) return n;
    for (InstanceHandle child : ch->second) {
        // Skip children absent from g_type so flattened enumeration cannot exceed census population.
        if (cs.type.find(child) == cs.type.end()) continue;
        if (included.find(child) == included.end()) continue;
        if (!emitted.insert(child).second) continue;           // never emit a handle twice (cycle / re-parent)
        n.children.push_back(SnapshotNode(cs, child, curDepth + 1, included, emitted));
    }
    return n;
}

// The app-authored classification, as facts rather than as JSON. Defined with the classifier further down; declared
// here because the enumerate path is compiled first.
struct AuthoredVerdicts
{
    std::set<InstanceHandle> authored;
    bool   sourceInstrumented = false;
    bool   truncated = false;
    size_t classifiedNodes = 0;
    size_t censusNodes = 0;
    bool   denylistFallback = false;
    const wchar_t* err = nullptr;
};

// Classify against a caller-supplied census snapshot, refreshing its sourceUri map after any backfill. The
// snapshot is shared rather than re-taken so an authored ENUMERATE classifies exactly the tree it is about to
// serialize: two independent snapshots would let an element arrive between them and be silently unclassified.
static AuthoredVerdicts ClassifyAuthored(CensusSnapshot& cs, bool onUiThread = false);

// Project the census onto its app-authored elements: keep only the elements the app wrote, and reparent each to its
// NEAREST authored ancestor so the framework/template elements between them are skipped rather than occupying a
// level.
static void ProjectAuthoredWalk(const CensusSnapshot& cs, InstanceHandle h, InstanceHandle authoredAncestor,
                                const std::set<InstanceHandle>& authored, CensusSnapshot& out,
                                std::vector<InstanceHandle>& roots, std::set<InstanceHandle>& seen, int depth)
{
    if (depth > kMaxEnumDepth) return;
    if (!seen.insert(h).second) return;
    InstanceHandle nextAncestor = authoredAncestor;
    if (authored.find(h) != authored.end()) {
        auto ty = cs.type.find(h);
        if (ty != cs.type.end()) {
            out.type[h] = ty->second;
            auto nm = cs.name.find(h);        if (nm != cs.name.end())  out.name[h] = nm->second;
            auto su = cs.sourceUri.find(h);   if (su != cs.sourceUri.end()) out.sourceUri[h] = su->second;
            auto wr = cs.wire.find(h);        if (wr != cs.wire.end())  out.wire[h] = wr->second;
            auto idv = cs.id.find(h);         if (idv != cs.id.end())   out.id[h] = idv->second;
            if (cs.uniqueName.find(h) != cs.uniqueName.end()) out.uniqueName.insert(h);
            // Touch the children entry so an authored leaf reports childCount 0 rather than inheriting the
            // raw child count of the template parts it hides.
            out.children[h];
            if (authoredAncestor) {
                out.parent[h] = authoredAncestor;
                out.children[authoredAncestor].push_back(h);
            } else {
                roots.push_back(h);
            }
            nextAncestor = h;
        }
    }
    auto ch = cs.children.find(h);
    if (ch == cs.children.end()) return;
    for (InstanceHandle c : ch->second) {
        if (cs.type.find(c) == cs.type.end()) continue;
        ProjectAuthoredWalk(cs, c, nextAncestor, authored, out, roots, seen, depth + 1);
    }
}

// Build the authored projection of `cs`, walking from `walkRoots` (the whole census, or one requested
// subtree). Emission order is the raw tree's pre-order, so authored siblings keep document order even where
// the framework levels between them were skipped.
static CensusSnapshot ProjectAuthored(const CensusSnapshot& cs,
                                      const std::set<InstanceHandle>& authored,
                                      const std::vector<InstanceHandle>& walkRoots)
{
    CensusSnapshot out;
    std::vector<InstanceHandle> roots;
    std::set<InstanceHandle> seen;
    for (InstanceHandle r : walkRoots) {
        if (cs.type.find(r) == cs.type.end()) continue;
        ProjectAuthoredWalk(cs, r, 0, authored, out, roots, seen, 0);
    }
    return out;
}

// Raw enumeration stays on the pipe thread and reads only the self-excluded census. Authored enumeration backfills
// source information in bounded UI-thread batches with no census lock held.
static std::wstring CmdEnumerate(const std::vector<std::wstring>& p)
{
    bool hasRoot = (p.size() >= 2 && !p[1].empty());
    // Resolve the opaque root handle under the census lock together with the snapshot.
    unsigned long long rootWire = hasRoot ? wcstoull(p[1].c_str(), nullptr, 10) : 0ull;
    InstanceHandle root = 0;
    const wchar_t* rootErr = nullptr;
    int depth = -1;
    if (p.size() >= 3 && !p[2].empty()) { depth = _wtoi(p[2].c_str()); if (depth < 0) depth = -1; }
    const bool authoredOnly = (p.size() >= 4 && p[3] == L"1");

    // Leaf-scoped critical section (C5): copy the census maps under the shared lock, then RELEASE before
    // serializing so the lock is never held across the O(nodes) JSON build (or a throw within it).
    CensusSnapshot cs;
    bool cardFrozen = false;
    std::set<InstanceHandle> frozenBaseline;
    {
        CensusSharedLock lock;
        cs.type = g_type;
        cs.children = g_children;
        cs.parent = g_parent;
        cs.name = g_nameOf;
        // Bank each node's XAML source URI alongside the census maps: the tree subscription already delivered
        // it, so carrying it here keeps enumerate a pipe-thread read (no UI-thread hop) while still telling a
        // client which file every element came from.
        SourceInfo_Snapshot(cs.sourceUri);
        for (auto& kv : g_type) cs.wire[kv.first] = PackWire_nolock(kv.first);
        if (hasRoot) ResolveWire_nolock(rootWire, &root, &rootErr);
        cardFrozen = g_cardFrozen.load(std::memory_order_acquire);
        if (cardFrozen) frozenBaseline = g_cardFrozenHandles;
    }

    // Card-open filtering: drop every census handle that wasn't part of the frozen baseline so the flattened
    // enumerate == the frozen pong count. Card parts are only ever leaves/new subtrees under existing panels, never
    // parents of baseline nodes, so no baseline subtree is dropped.
    if (cardFrozen) {
        for (auto it = cs.type.begin(); it != cs.type.end(); ) {
            if (frozenBaseline.find(it->first) == frozenBaseline.end()) it = cs.type.erase(it);
            else ++it;
        }
    }

    if (hasRoot && rootErr) return std::wstring(L"ERR ") + rootErr;
    if (hasRoot && cs.type.find(root) == cs.type.end()) return L"ERR not-found";

    // Identity is a fact about the REAL tree, so it is folded over the raw census before any projection or
    // depth bound narrows the view. An id derived from the projected tree would change the moment someone
    // passed --all, and the same element would print two different selectors.
    ComputeSelectorFacts(cs);
    if (!FilterWindowCensus(cs) || !WindowScopeCurrent()) return L"ERR window-scope-changed";

    // In authored mode this REPLACES the census with its authored projection, so the depth bound below counts
    // authored levels and every downstream step (selection, childCount, "+more") describes the tree the caller
    // actually asked for.
    CensusSnapshot view = cs;
    AuthoredVerdicts verdicts;
    if (authoredOnly) {
        verdicts = ClassifyAuthored(cs);
        if (verdicts.err) return std::wstring(L"ERR ") + verdicts.err;
        // Deliberately NO refusal for "nothing classified" here. While the classifier is still backfilling,
        // an empty authored set means "not finished", not "this app authored nothing" -- and those two must
        // never be spelled the same way. The envelope below carries the state; the client decides.
        view = ProjectAuthored(cs, verdicts.authored, hasRoot ? std::vector<InstanceHandle>{ root }
                                                              : CollectRoots(cs));
        // Its authored descendants become the roots of this view rather than the request failing: the caller asked
        // for that subtree, and the subtree does have authored content.
        if (hasRoot && view.type.find(root) == view.type.end()) hasRoot = false;
    }

    std::vector<DevToolsReadNode> roots;
    int budget = kMaxTreeWalk;
    std::set<InstanceHandle> included;
    if (hasRoot) {
        SelectBoundedBreadthFirst(view, { root }, depth, budget, included);
    } else {
        SelectBoundedBreadthFirst(view, CollectRoots(view), depth, budget, included);
        if (depth < 0 && budget > 0) {
            std::vector<InstanceHandle> unreached;
            for (auto& kv : view.type) {
                if (included.find(kv.first) == included.end()) unreached.push_back(kv.first);
            }
            if (!unreached.empty()) {
                SelectBoundedBreadthFirst(view, unreached, depth, budget, included);
            }
        }
    }

    // Emit every selected node exactly once, each under its nearest SELECTED ancestor. A node whose parent
    // was not selected becomes a top-level entry rather than disappearing, so trimming never orphans a
    // subtree out of the reply.
    std::set<InstanceHandle> emitted;
    for (auto& kv : view.type) {
        InstanceHandle h = kv.first;
        if (included.find(h) == included.end()) continue;
        auto pit = view.parent.find(h);
        if (pit != view.parent.end() && included.find(pit->second) != included.end()) continue;
        if (!emitted.insert(h).second) continue;
        roots.push_back(SnapshotNode(view, h, 0, included, emitted));
    }
    if (authoredOnly) {
        std::wstring out = L"{\"nodes\":";
        out += DevToolsRead_SerializeTree(roots);
        out += L",\"authored\":true,\"sourceInstrumented\":";
        out += verdicts.sourceInstrumented ? L"true" : L"false";
        out += L",\"classificationTruncated\":";
        out += verdicts.truncated ? L"true" : L"false";
        out += L",\"classifiedNodes\":" + std::to_wstring(verdicts.classifiedNodes);
        out += L",\"censusNodes\":" + std::to_wstring(verdicts.censusNodes);
        out += L",\"authoredNodes\":" + std::to_wstring(verdicts.authored.size());
        out += L",\"classifier\":\"";
        out += (verdicts.denylistFallback ? L"denylist-fallback" : L"allowlist");
        out += L"\"}";
        return out;
    }
    if (roots.empty()) return L"ERR no-tree";
    return DevToolsRead_SerializeTree(roots);
}
