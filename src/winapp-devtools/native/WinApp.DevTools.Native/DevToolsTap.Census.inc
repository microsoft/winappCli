// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsTap.cpp; included into that translation unit, not compiled on its own.

// Coalesce per-element OnVisualTreeChange bursts into one dispatcher turn; inline drain can run before handles retire.
// Consumers may parse/build XAML, so delivery waits until after the framework callback to avoid re-entrancy.
static std::atomic<bool>      g_treeDirty{false};
static DevToolsTreeChangedFn       g_treeSinks[4] = { nullptr, nullptr, nullptr, nullptr };
static std::atomic<unsigned>  g_treeSinkCount{0};
static std::atomic<bool>      g_treeEnqueued{false};

// Deliver one notification. Public (declared in DevToolsTreeWatch.h) so a consumer with its own tick can also
// flush on demand; the dispatcher turn below is the normal path and needs no help.
void DevToolsTreeWatch_Drain()
{
    if (!g_treeDirty.exchange(false, std::memory_order_acq_rel)) return;
    const unsigned n = g_treeSinkCount.load(std::memory_order_acquire);
    // Without this line the only evidence a notification arrived is a consumer's side effect -- so a consumer that
    // correctly does nothing (no selection to drop) is indistinguishable from a hook that never fires.
    Log(L"treewatch: delivering to %u sink(s)", n);
    for (unsigned i = 0; i < n; ++i) if (g_treeSinks[i]) g_treeSinks[i]();
}



// Shared diagnostics and dispatcher, resolved by Worker and retained for the process lifetime.
static IXamlDiagnostics*  g_diag = nullptr;
static IInspectable* g_dispatcher = nullptr;

// Self-excluded census maps populated by visual-tree callbacks and protected by the census lock.
static std::map<std::wstring, InstanceHandle> g_byName;
// Keep per-element names; reversing g_byName loses duplicate x:Name instances (for example, DataTemplate items).
static std::map<InstanceHandle, std::wstring> g_nameOf;
static std::map<InstanceHandle, std::wstring> g_type;
// Backfill blank entries in bounded UI-thread batches; a warm-up race is not framework evidence.
struct SourceInfoEntry { std::wstring file; unsigned long long epoch; };
static std::map<InstanceHandle, SourceInfoEntry> g_sourceInfo;
static SRWLOCK g_sourceUriLock = SRWLOCK_INIT;
static std::atomic<unsigned long long> g_sourceEpoch{ 0 };
static std::atomic<bool> g_sawAnySourceInfo{ false };

// Lock ORDER is census -> sourceUri, never the reverse. The eviction paths hold the census lock exclusively
// and take this one inside; the classification pass releases the census lock before touching it.
static void SourceUri_Evict_nolock(InstanceHandle h)
{
    AcquireSRWLockExclusive(&g_sourceUriLock);
    g_sourceInfo.erase(h);
    ReleaseSRWLockExclusive(&g_sourceUriLock);
}

static void SourceUri_SeedFromAdd_nolock(InstanceHandle h, const wchar_t* fileName)
{
    if (!fileName || !*fileName) return;
    if (!g_sawAnySourceInfo.exchange(true)) g_sourceEpoch.fetch_add(1, std::memory_order_release);
    AcquireSRWLockExclusive(&g_sourceUriLock);
    g_sourceInfo[h] = SourceInfoEntry{ DevToolsSourcePath_AppUri(fileName), g_sourceEpoch.load(std::memory_order_acquire) };
    ReleaseSRWLockExclusive(&g_sourceUriLock);
}

static void SourceInfo_Snapshot(std::map<InstanceHandle, std::wstring>& out)
{
    const unsigned long long epoch = g_sourceEpoch.load(std::memory_order_acquire);
    AcquireSRWLockShared(&g_sourceUriLock);
    for (const auto& kv : g_sourceInfo) {
        if (!kv.second.file.empty() || kv.second.epoch == epoch) out[kv.first] = kv.second.file;
    }
    ReleaseSRWLockShared(&g_sourceUriLock);
}

static std::map<InstanceHandle, std::vector<InstanceHandle>> g_children;
static std::map<InstanceHandle, InstanceHandle> g_parent;
static InstanceHandle g_firstPanel = 0;
static InstanceHandle g_overlayPanel = 0;
// Guard census maps with a shared/exclusive SRW lock and keep it leaf-scoped, never across dispatch or blocking calls,
// so pipe-thread reads cannot lock-order-invert against the UI-thread writer.
static SRWLOCK g_censusLock = SRWLOCK_INIT;

// Opaque decimal wire handle: packed slot, generation and session nonce. The nonce rejects
// other sessions; the generation rejects reused slots. Never recycle a saturated generation.
static const int SLOT_BITS  = 40;
static const int GEN_BITS   = 16;
static const int NONCE_BITS = 8;
static_assert(SLOT_BITS + GEN_BITS + NONCE_BITS == 64, "handle bit budget must total 64");
static const unsigned long long NONCE_MASK = 0xFFull;
static const unsigned long long GEN_MASK   = 0xFFFFull;
static const unsigned long long SLOT_MASK  = 0xFFFFFFFFFFull;
#if defined(SOAK)
#  ifndef WINAPP_DEVTOOLS_SOAK_GEN_CEILING
#    define WINAPP_DEVTOOLS_SOAK_GEN_CEILING 256u
#  endif
static_assert((unsigned long long)(WINAPP_DEVTOOLS_SOAK_GEN_CEILING) <= GEN_MASK,
    "SOAK gen ceiling must fit the GEN field; the knob caps the gen VALUE, it must NOT resize GEN_BITS/GEN_MASK (wire stays 40/16/8)");
static const uint32_t kGenMax = (uint32_t)(WINAPP_DEVTOOLS_SOAK_GEN_CEILING);
#else
static const uint32_t kGenMax = (uint32_t)GEN_MASK;           // generation ceiling; a slot at kGenMax is retired
#endif

// One slot per distinct live InstanceHandle. gen==0 means "never minted"; alive tracks tree membership.
struct HandleSlot { InstanceHandle ih; uint32_t gen; bool alive; };
static std::vector<HandleSlot> g_slots;
static std::vector<uint32_t>   g_freeSlots;
static std::map<InstanceHandle, uint32_t> g_ihToSlot;
static uint64_t g_sessionNonce = 0;

// All five helpers run UNDER g_censusLock (EXCLUSIVE for the mutators MintSlot/FreeSlot, SHARED ok for the
// read-only PackWire/ResolveWire) - they share the census lock rather than adding a second lock/lock-order edge.

// Assign (or refresh) the slot for a live ih. Bumps the generation on slot REUSE so a handle minted against
// a prior occupant fails closed. Called from the Add branch of OnVisualTreeChange (below the overlay
// self-exclusion early-return, so overlay elements never receive a slot).
static void MintSlot_nolock(InstanceHandle h) {
    auto it = g_ihToSlot.find(h);
    if (it != g_ihToSlot.end()) {
        // Same ih re-Added while still mapped (un-Removed overwrite): bump the generation in place.
        HandleSlot& s = g_slots[it->second];
        if (s.gen < kGenMax) s.gen++;
        s.ih = h; s.alive = true;
        return;
    }
    uint32_t slot;
    if (!g_freeSlots.empty()) {
        slot = g_freeSlots.back(); g_freeSlots.pop_back();
        HandleSlot& s = g_slots[slot];
        if (s.gen < kGenMax) s.gen++;   // reused slot -> fresh generation (ABA guard)
        s.ih = h; s.alive = true;
    } else {
        if (g_slots.size() >= (size_t)UINT32_MAX) return; // slot space exhausted -> h stays unslotted (fail closed)
        slot = (uint32_t)g_slots.size();
        HandleSlot s; s.ih = h; s.gen = 1; s.alive = true; // first mint starts at gen 1 (0 == never minted)
        g_slots.push_back(s);
    }
    g_ihToSlot[h] = slot;
}

// Retire the slot for a Removed ih. The index is recycled only while its generation still has headroom; a
// saturated slot is retired forever (never pushed back) so no future handle can ABA-alias a stale generation.
static void FreeSlot_nolock(InstanceHandle h) {
    auto it = g_ihToSlot.find(h);
    if (it == g_ihToSlot.end()) return;
    uint32_t slot = it->second;
    HandleSlot& s = g_slots[slot];
    s.alive = false; s.ih = 0;
    g_ihToSlot.erase(it);
    if (s.gen < kGenMax) g_freeSlots.push_back(slot);
}

// Pack the opaque wire handle for a live ih (0 if h is not slotted - the wire never emits a live handle as 0
// since a live slot always has gen >= 1, which lands above the nonce field).
static unsigned long long PackWire_nolock(InstanceHandle h) {
    auto it = g_ihToSlot.find(h);
    if (it == g_ihToSlot.end()) return 0ull;
    const HandleSlot& s = g_slots[it->second];
    return ((unsigned long long)it->second << (GEN_BITS + NONCE_BITS))
         | (((unsigned long long)s.gen & GEN_MASK) << NONCE_BITS)
         | (g_sessionNonce & NONCE_MASK);
}

// Resolve a wire handle back to a live ih. Returns true + *ih on an exact (slot,gen,nonce) match; false with
// *errVerb set to the wire error token otherwise: wrong session / OOB slot / retired slot / forged-future gen
// -> not-found; a generation BEHIND the slot (the slot was reused since this handle was minted) -> stale-handle.
struct TapWindowScope
{
    HWND hwnd = nullptr;
    unsigned long long rootWire = 0;
    InstanceHandle root = 0;
    InstanceHandle pickRoot = 0;
    // Captured wire generation and XamlRoot identity, never just a reusable raw handle.
    std::map<InstanceHandle, std::pair<unsigned long long, unsigned long long>> members;
};
static thread_local std::shared_ptr<const TapWindowScope> g_windowScope;
struct TapWindowScopeGuard
{
    std::shared_ptr<const TapWindowScope> previous = g_windowScope;
    explicit TapWindowScopeGuard(std::shared_ptr<const TapWindowScope> scope) { g_windowScope = std::move(scope); }
    ~TapWindowScopeGuard() { g_windowScope = std::move(previous); }
};
static bool WindowElementCurrent_ui(InstanceHandle raw);

static bool ResolveWire_nolock(unsigned long long wire, InstanceHandle* ih, const wchar_t** errVerb) {
    *ih = 0; *errVerb = L"not-found";
    if ((wire & NONCE_MASK) != (g_sessionNonce & NONCE_MASK)) return false;
    unsigned long long slot = (wire >> (GEN_BITS + NONCE_BITS)) & SLOT_MASK;
    uint32_t gen = (uint32_t)((wire >> NONCE_BITS) & GEN_MASK);
    if (slot >= g_slots.size()) return false;
    const HandleSlot& s = g_slots[(size_t)slot];
    if (!s.alive) return false;
    if (gen == s.gen) {
        if (g_windowScope) {
            const auto member = g_windowScope->members.find(s.ih);
            if (member == g_windowScope->members.end() || member->second.first != wire) {
                *errVerb = L"outside-window"; return false;
            }
        }
        *ih = s.ih; *errVerb = nullptr; return true;
    }
    if (gen < s.gen)  { *errVerb = L"stale-handle"; return false; }
    return false;
}

static unsigned long long TapWireOf(InstanceHandle raw)
{
    AcquireSRWLockShared(&g_censusLock);
    const auto wire = PackWire_nolock(raw);
    ReleaseSRWLockShared(&g_censusLock);
    return wire;
}

// Handles of the overlay subtree. Seeded on the UI thread from the DETACHED overlay root before it is
// attached, then extended transitively in OnVisualTreeChange (parent-in-set -> child excluded). Guarded
// because it is seeded on the UI thread but tested on whatever thread the tree-change callback runs.
static std::set<InstanceHandle> g_overlaySet;
static SRWLOCK g_overlayLock = SRWLOCK_INIT;
// Those Adds fire synchronously on the UI thread and the app tree is quiescent post-enumeration, so any Add during
// this window is our overlay: exclude it AND seed it, so any later straggler is still caught by the parent-in-set
// rule.
static std::atomic<bool> g_overlayBuilding{false};
// While the inline Quick-edits card is open, its editable TextBoxes realize default-template internal parts
// (ScrollViewer/TextBoxView/ScrollBars/...) ASYNCHRONOUSLY on later layout passes, and XamlDiagnostics re-issues
// DIFFERENT InstanceHandles for the same object across the Add/parent phases - so unlike the flat, part-free toolbar
// markup, those parts cannot be excluded from the element-count census by handle (verified: the seeded live TextBox
// handle never matches the parent handle the parts report).
static std::atomic<bool>   g_cardFrozen{false};
static std::atomic<size_t> g_cardFrozenCount{0};
// While a card is open, visualtree.enumerate filters its snapshot to THIS set so the flattened enumerate covers
// exactly the frozen population - keeping the enumerate-count == pong invariant intact even though the card's async
// template parts DO enter g_type (they can't be excluded by handle, only frozen out).
static std::set<InstanceHandle> g_cardFrozenHandles;
// Scope lives inside the UI-thread build closure, not the wider pipe dispatch/wait, so unrelated app Adds are not
// misclassified as overlay nodes.
struct OverlayBuildScope {
    bool previous;
    OverlayBuildScope() : previous(g_overlayBuilding.exchange(true, std::memory_order_acq_rel)) {}
    ~OverlayBuildScope() { g_overlayBuilding.store(previous, std::memory_order_release); }
};
static void OverlaySeed(InstanceHandle h) {
    AcquireSRWLockExclusive(&g_overlayLock); g_overlaySet.insert(h); ReleaseSRWLockExclusive(&g_overlayLock);
}
size_t DevToolsTap_ExcludedElementCount() {
    AcquireSRWLockShared(&g_overlayLock);
    const size_t n = g_overlaySet.size();
    ReleaseSRWLockShared(&g_overlayLock);
    return n;
}
// Predicate for the element-pick hit-test: is this handle part of our own overlay subtree? (So a pick can
// never select our toolbar/adorner.) Shared lock: read-only, may run concurrently with other pipe ops.
static bool OverlayContains(InstanceHandle h) {
    AcquireSRWLockShared(&g_overlayLock); bool r = g_overlaySet.count(h) != 0; ReleaseSRWLockShared(&g_overlayLock); return r;
}

static std::wstring BuildTreeRemoved(unsigned long long w);

// At child-add time the parent isn't in g_overlaySet yet, so the child is classified mine=0 and censused -- it then
// leaks permanently (inflating the pong baseline, and appearing in visualtree.enumerate and the inspector window
// tree). Lock order matches the rest of the file (census, then overlay; never nested).
static void EvictLeakedOverlaySubtree(InstanceHandle root)
{
    std::vector<InstanceHandle> evicted;
    std::vector<unsigned long long> removedWires;
    AcquireSRWLockExclusive(&g_censusLock);
    std::vector<InstanceHandle> stack;
    { auto it = g_children.find(root); if (it != g_children.end()) stack = it->second; }
    while (!stack.empty()) {
        InstanceHandle h = stack.back(); stack.pop_back();
        auto ci = g_children.find(h);
        if (ci != g_children.end()) for (auto c : ci->second) stack.push_back(c);
        const bool wasCensus = g_type.erase(h) != 0;
        auto pit = g_parent.find(h);
        if (pit != g_parent.end()) {
            auto kit = g_children.find(pit->second);
            if (kit != g_children.end()) kit->second.erase(std::remove(kit->second.begin(), kit->second.end(), h), kit->second.end());
            g_parent.erase(pit);
        }
        g_children.erase(h);
        if (wasCensus) {
            // Preserve the client's identity before retiring the slot. PackWire_nolock cannot recover it after
            // FreeSlot_nolock, and publishing while either registry lock is held would add a re-entrancy edge.
            removedWires.push_back(PackWire_nolock(h));
            FreeSlot_nolock(h);
            SourceUri_Evict_nolock(h); // handles are reused; never let a new element inherit a stale entry
            int prev = g_elemCount.load(std::memory_order_relaxed);
            while (prev > 0 && !g_elemCount.compare_exchange_weak(prev, prev - 1, std::memory_order_relaxed)) {}
        }
        evicted.push_back(h);
    }
    // Drop any x:Name entries that resolve to an evicted overlay handle so name lookups stay app-only.
    if (!evicted.empty()) {
        for (auto it = g_byName.begin(); it != g_byName.end(); ) {
            if (std::find(evicted.begin(), evicted.end(), it->second) != evicted.end()) it = g_byName.erase(it);
            else ++it;
        }
        for (auto h : evicted) g_nameOf.erase(h);
    }
    ReleaseSRWLockExclusive(&g_censusLock);
    if (!evicted.empty()) {
        AcquireSRWLockExclusive(&g_overlayLock);
        for (auto h : evicted) g_overlaySet.insert(h);
        ReleaseSRWLockExclusive(&g_overlayLock);
        DevToolsOverlayLog(L"overlay leak evicted %zu node(s) under root=%llu", evicted.size(), (unsigned long long)root);
    }
    for (unsigned long long wire : removedWires) {
        if (wire) DevToolsEvents_BroadcastTreeDelta(BuildTreeRemoved(wire));
    }
}

static std::wstring BuildTreeAdded(unsigned long long w, unsigned long long parentW,
                                   const wchar_t* type, const wchar_t* name, unsigned int index) {
    std::wstring j = L"{\"added\":[{\"handle\":\"" + std::to_wstring(w) +
                     L"\",\"parent\":\"" + std::to_wstring(parentW) +
                     L"\",\"type\":\"" + DevToolsJsonEscape(type ? type : L"") + L"\"";
    if (name && *name) j += L",\"name\":\"" + DevToolsJsonEscape(name) + L"\"";
    j += L",\"index\":" + std::to_wstring(index) + L"}]}";
    return DevToolsRpcEvent(L"VisualTree.changed", j, L"null");
}
static std::wstring BuildTreeRemoved(unsigned long long w) {
    return DevToolsRpcEvent(L"VisualTree.changed", L"{\"removed\":[\"" + std::to_wstring(w) + L"\"]}", L"null");
}

// Removed-node delivery is deferred to a dispatcher turn, not the framework callback, so consumers may touch the live
// tree without re-entering XAML mid-teardown.
typedef void (*DevToolsNodeRemovedFn)(InstanceHandle);
static DevToolsNodeRemovedFn      g_nodeRemovedSink = nullptr;
static SRWLOCK               g_removedLock = SRWLOCK_INIT;
static std::vector<InstanceHandle> g_removedPending;
static std::atomic<bool>     g_removedEnqueued{false};

struct RemovedHandler : DevToolsSinkBase
{
    void Init() { InitSink(__uuidof(IDispatcherQueueHandler), /*gen*/ 0); }
    virtual HRESULT STDMETHODCALLTYPE Invoke() {
        DevToolsPerf_LatchUiThread();
        WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::TapRemovedTurn);
        // Clear the latch BEFORE draining: a Remove that lands while we are draining must be able to
        // enqueue a fresh turn, or it would sit in g_removedPending until some later burst happened to
        // flush it — which is the same "arrives eventually, or never" failure this replaces.
        g_removedEnqueued.store(false, std::memory_order_release);
        std::vector<InstanceHandle> batch;
        AcquireSRWLockExclusive(&g_removedLock);
        batch.swap(g_removedPending);
        ReleaseSRWLockExclusive(&g_removedLock);
        DevToolsNodeRemovedFn fn = g_nodeRemovedSink;
        if (fn) for (InstanceHandle h : batch) fn(h);
        return S_OK;
    }
};
static RemovedHandler g_removedHandler;

// Called from the Remove branch with NO census/overlay lock held (the sink may re-enter the tree).
static void QueueRemovedNotify(InstanceHandle h)
{
    if (!g_nodeRemovedSink || !g_dispatcher || !h) return;
    AcquireSRWLockExclusive(&g_removedLock);
    g_removedPending.push_back(h);
    ReleaseSRWLockExclusive(&g_removedLock);
    bool expected = false;
    if (!g_removedEnqueued.compare_exchange_strong(expected, true, std::memory_order_acq_rel))
        return;
    bool enq = false;
    if (FAILED(DevToolsDispatcherTryEnqueue(g_dispatcher, reinterpret_cast<IUnknown*>(&g_removedHandler), &enq)) || !enq)
        g_removedEnqueued.store(false, std::memory_order_release);
}

// Lives here rather than with the rest of DevToolsTreeWatch because it needs g_dispatcher and
// DevToolsDispatcherTryEnqueue. Same shape as RemovedHandler above, deliberately: one turn per burst, latch
// cleared BEFORE draining so a mutation landing mid-drain can post a fresh turn instead of being stranded.
struct TreeWatchHandler : DevToolsSinkBase
{
    void Init() { InitSink(__uuidof(IDispatcherQueueHandler), /*gen*/ 0); }
    virtual HRESULT STDMETHODCALLTYPE Invoke() {
        DevToolsPerf_LatchUiThread();
        WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::TapTreeWatchTurn);
        g_treeEnqueued.store(false, std::memory_order_release);
        DevToolsTreeWatch_Drain();
        return S_OK;
    }
};
static TreeWatchHandler g_treeWatchHandler;

// Reports whether a notification can EVER arrive: it needs both the visual-tree subscription (AdviseVisualTreeChange
// is the canonical surface -- returns S_OK then never calls back) and a dispatcher to deliver on.
bool DevToolsTreeWatch_Subscribe(DevToolsTreeChangedFn fn){
    if (!fn) return false;
    unsigned n = g_treeSinkCount.load(std::memory_order_acquire);
    const bool armed = !g_adviseFailed.load() && g_dispatcher != nullptr;
    for (unsigned i = 0; i < n; ++i) if (g_treeSinks[i] == fn) return armed;
    if (n >= (unsigned)(sizeof(g_treeSinks) / sizeof(g_treeSinks[0]))) {
        Log(L"treewatch: sink table full; '%p' NOT subscribed", (void*)fn);
        return false;
    }
    g_treeWatchHandler.Init();
    g_treeSinks[n] = fn;
    g_treeSinkCount.store(n + 1, std::memory_order_release);
    Log(L"treewatch: subscribed sink %u armed=%d (advise=%d dispatcher=%d)", n, (int)armed,
        (int)!g_adviseFailed.load(), (int)(g_dispatcher != nullptr));
    return armed;
}

// Cheap by construction: a relaxed store, then a compare-exchange that posts at most one dispatcher turn per burst. A
// navigation's thousands of mutations therefore cost thousands of atomic stores and ONE delivery -- and the delivery
// runs after the storm rather than inside it.
static void DevToolsTreeWatch_NoteMutation()
{
    g_treeDirty.store(true, std::memory_order_relaxed);
    if (!g_treeSinkCount.load(std::memory_order_acquire) || !g_dispatcher) return;
    bool expected = false;
    if (!g_treeEnqueued.compare_exchange_strong(expected, true, std::memory_order_acq_rel))
        return;
    bool enq = false;
    if (FAILED(DevToolsDispatcherTryEnqueue(g_dispatcher, reinterpret_cast<IUnknown*>(&g_treeWatchHandler), &enq)) || !enq)
        g_treeEnqueued.store(false, std::memory_order_release);
}

bool DevToolsTap_SetNodeRemovedSink(DevToolsNodeRemovedFn fn){
    if (g_adviseFailed.load()) { Log(L"tree-removed sink REFUSED: visual-tree subscription never bound"); return false; }
    if (!g_dispatcher)         { Log(L"tree-removed sink REFUSED: no dispatcher"); return false; }
    g_nodeRemovedSink = fn;
    g_removedHandler.Init();
    Log(L"tree-removed sink armed (fn=%p)", (void*)fn);
    return true;
}


// ResolveResource may return S_OK for a missing name. Correlate ErrorResourceNotFound callback sequence changes
// across the call window; that observation, not HRESULT alone, detects failure.
struct DevToolsResourceError
{
    unsigned long long seq  = 0;
    InstanceHandle     elem = 0;
    std::wstring       context;
};
static SRWLOCK          g_resErrLock = SRWLOCK_INIT;
static DevToolsResourceError g_resErr;

static DevToolsResourceError ResourceErrorSnapshot()
{
    AcquireSRWLockShared(&g_resErrLock);
    DevToolsResourceError copy = g_resErr;
    ReleaseSRWLockShared(&g_resErrLock);
    return copy;
}

struct TreeCb : IVisualTreeServiceCallback2
{
    IUnknown* ftm = nullptr;
    void EnsureFtm() { if (!ftm) CoCreateFreeThreadedMarshaler(static_cast<IVisualTreeServiceCallback*>(this), &ftm); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID r, void** p) override {
        if (!p) return E_POINTER;
        if (r == IID_IUnknown || r == __uuidof(IVisualTreeServiceCallback)) { *p = static_cast<IVisualTreeServiceCallback*>(this); return S_OK; }
        // XAML QIs callback2 for resource-error delivery; callback1 registration alone is not enough.
        if (r == __uuidof(IVisualTreeServiceCallback2)) { *p = static_cast<IVisualTreeServiceCallback2*>(this); return S_OK; }
        if (r == __uuidof(IMarshal) && ftm) return ftm->QueryInterface(r, p);
        if (r == __uuidof(IAgileObject)) { *p = static_cast<IVisualTreeServiceCallback*>(this); return S_OK; }
        *p = nullptr; return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return 2; }
    ULONG STDMETHODCALLTYPE Release() override { return 1; }
    // Records only the RESOURCE-ERROR states. The other states are not a failure of anything this tap asked
    // for, and banking them would make the sequence number move for reasons a resolve call must not read as
    // its own failure.
    HRESULT STDMETHODCALLTYPE OnElementStateChanged(InstanceHandle element, VisualElementState state, LPCWSTR context) override {
        if (state != ErrorResourceNotFound && state != ErrorInvalidResource) return S_OK;
        AcquireSRWLockExclusive(&g_resErrLock);
        ++g_resErr.seq;
        g_resErr.elem    = element;
        g_resErr.context = context ? context : L"";
        const unsigned long long seq = g_resErr.seq;
        ReleaseSRWLockExclusive(&g_resErrLock);
        Log(L"resourceerror #%llu state=%d elem=%llu context='%s'", seq, (int)state,
            (unsigned long long)element, context ? context : L"(null)");
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnVisualTreeChange(ParentChildRelation rel, VisualElement e, VisualMutationType t) override {
        // the whole per-element callback, on whichever thread XAML delivers it.
        WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::TapTreeCallback);
        // tell the tree-change subscribers something moved. Cheap and unconditional so it survives every
        // early return below; delivery happens on a dispatcher turn AFTER this callback completes, so
        // consumers see a finished census and can build UI without re-entering the framework mid-mutation.
        DevToolsTreeWatch_NoteMutation();
        // Both struct parameters are [in] per xamlom.h, so the framework owns and frees their BSTRs
        // (Type/Name/SourceInfo). We must NOT SysFreeString them here — that would be a double free.
        if (t == Add) {
            // This catches the async, post-Activate Adds the seed/overlay-scope self-exclusion misses
            // (XamlDiagnostics re-issues different InstanceHandles for the same objects across phases, so
            // handle/parent-based exclusion can't catch them, but XamlRoot identity is stable).
            bool ownedByInspector = false;
            if (DevToolsWindow_IsOpen() && g_diag) {
                IInspectable* insp = nullptr;
                if (SUCCEEDED(g_diag->GetIInspectableFromHandle(e.Handle, &insp)) && insp) {
                    ownedByInspector = DevToolsWindow_OwnsElement(insp);
                    insp->Release();
                }
            }
            AcquireSRWLockExclusive(&g_overlayLock);
            const bool mine = ownedByInspector
                              || g_overlayBuilding.load(std::memory_order_acquire)
                              || g_overlaySet.count(e.Handle)
                              || (rel.Parent && g_overlaySet.count(rel.Parent));
            if (mine) {
                g_overlaySet.insert(e.Handle);
                ReleaseSRWLockExclusive(&g_overlayLock);
                // Catch children delivered before this parent (out-of-order Add) that were mistakenly censused.
                EvictLeakedOverlaySubtree(e.Handle);
                return S_OK;
            }
            ReleaseSRWLockExclusive(&g_overlayLock);

            AcquireSRWLockExclusive(&g_censusLock);
            std::wstring treeDelta;
            if (e.Name && *e.Name) { g_byName[e.Name] = e.Handle; g_nameOf[e.Handle] = e.Name; }
            if (e.Type) {
                if (g_type.find(e.Handle) == g_type.end())
                    g_elemCount.fetch_add(1, std::memory_order_relaxed);
                g_type[e.Handle] = e.Type;
                // the add notification already carries this element's SourceInfo, so bank it now. The
                // app-authored pass then needs no COM read at all for elements seen this way.
                SourceUri_SeedFromAdd_nolock(e.Handle, e.SrcInfo.FileName);
                // Mint the generation-stamped slot under the census lock, after overlay self-exclusion.
                MintSlot_nolock(e.Handle);
                // Remember the outermost Panel (first StackPanel/Grid/Canvas in tree order) as the overlay
                // insertion target — a real app panel we can append to without reparenting Window.Content.
                if (!g_firstPanel) {
                    std::wstring ty = e.Type;
                    if (ty.find(L"StackPanel") != std::wstring::npos ||
                        ty.find(L"Canvas") != std::wstring::npos ||
                        (ty.size() >= 4 && ty.compare(ty.size() - 4, 4, L"Grid") == 0))
                        g_firstPanel = e.Handle;
                }
            }
            if (rel.Parent) {
                g_parent[e.Handle] = rel.Parent;
                auto& kids = g_children[rel.Parent];
                if (rel.ChildIndex <= kids.size()) kids.insert(kids.begin() + rel.ChildIndex, e.Handle);
                else kids.push_back(e.Handle);
            }
            if (e.Type) {
                // Wire handles are resolved under the same held lock; overlay and inspector elements returned early
                // above, so they never produce a delta. Built here, broadcast (enqueue-only) after the lock is
                // released so a slow client can never stall this worker thread.
                unsigned long long w  = PackWire_nolock(e.Handle);
                unsigned long long pw = rel.Parent ? PackWire_nolock(rel.Parent) : 0ULL;
                treeDelta = BuildTreeAdded(w, pw, e.Type, e.Name, rel.ChildIndex);
            }
            ReleaseSRWLockExclusive(&g_censusLock);
            if (!treeDelta.empty()) DevToolsEvents_BroadcastTreeDelta(treeDelta);
        } else {
            std::wstring treeDelta;
            AcquireSRWLockExclusive(&g_censusLock);
            if (e.Name && *e.Name) {
                // A XAML hot reload re-creates a page and can deliver the new element's Add before the old one's
                // Remove; an unconditional erase by name then wipes the LIVE element out of the name census, and
                // every name-based lookup (`find|<name>`, the write agent) reports it as absent.
                auto nit = g_byName.find(e.Name);
                if (nit != g_byName.end() && nit->second == e.Handle) g_byName.erase(nit);
            }
            // Keyed by HANDLE, so this erase is unambiguous and needs no such guard.
            g_nameOf.erase(e.Handle);
            const bool wasCensus = g_type.erase(e.Handle) != 0;
            g_parent.erase(e.Handle);
            if (rel.Parent) { auto& kids = g_children[rel.Parent]; kids.erase(std::remove(kids.begin(), kids.end(), e.Handle), kids.end()); }
            g_children.erase(e.Handle);
            // Retire a removed census slot under the same lock; generation protects against reuse.
            if (wasCensus) {
                // Reactive delta: resolve the wire handle BEFORE FreeSlot_nolock retires the slot (afterwards
                // PackWire would mint a fresh generation and the client couldn't match its snapshot node). Only a
                // real census shrink emits `removed`; broadcast after every lock is released (enqueue-only).
                treeDelta = BuildTreeRemoved(PackWire_nolock(e.Handle));
                FreeSlot_nolock(e.Handle);
                SourceUri_Evict_nolock(e.Handle);
            }
            ReleaseSRWLockExclusive(&g_censusLock);
            AcquireSRWLockExclusive(&g_overlayLock); g_overlaySet.erase(e.Handle); ReleaseSRWLockExclusive(&g_overlayLock);
            if (!treeDelta.empty()) DevToolsEvents_BroadcastTreeDelta(treeDelta);
            // tell the overlay this node left the census, on a dispatcher turn rather than on the
            // starved tracking tick. Every lock is released above, so the sink may touch the live tree.
            if (wasCensus) QueueRemovedNotify(e.Handle);
            if (wasCensus) {
                int prev = g_elemCount.load(std::memory_order_relaxed);
                while (prev > 0 && !g_elemCount.compare_exchange_weak(prev, prev - 1, std::memory_order_relaxed)) {}
            }
        }
        return S_OK;
    }
};
