// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsTap.cpp; included into that translation unit, not compiled on its own.

// DevToolsOverlay's state-changed sink (DevToolsOverlay_SetStateChangedSink, DevToolsOverlay.h) is payload-free and
// fires synchronously on the UI thread from inside whatever DevToolsOverlay_ call actually changed something -- a
// DevTools request's RunOnUiThread closure, or a direct in-app gesture (the rail's own buttons, Esc).

// The tracker owns the "last broadcast" snapshot. Touched only from the UI thread (every DevToolsOverlay_* mutator
// this sink observes already asserts that -- see DevToolsOverlay.h), so no lock is needed; see the header's own
// threading note.
static DevToolsOverlayStateTracker g_overlayStateTracker;

// The connection performing the CURRENT UI-thread overlay mutation, so the closure that actually changes an axis can
// record ownership without threading the connection through every bridge signature.
static thread_local unsigned long long g_overlayOwnerConn = 0;

// Depth of the owned mutation currently running on this thread, and whether the overlay's state-changed sink fired
// inside it.
static thread_local int  g_ownedMutationDepth = 0;
static thread_local bool g_ownedMutationNotifyPending = false;

// Origin is installed inside the UI-thread closure that performs a protocol mutation. Per-thread storage
// prevents concurrent pipe threads from racing over a process-global std::wstring, while restoring the prior
// value keeps nested UI dispatch/reentrancy correctly attributed.
static thread_local std::wstring g_overlayEventOrigin = L"null";

class DevToolsOverlayEventOriginScope {
public:
    explicit DevToolsOverlayEventOriginScope(const std::wstring& origin)
        : previous_(g_overlayEventOrigin)
    {
        g_overlayEventOrigin = origin;
    }
    ~DevToolsOverlayEventOriginScope() { g_overlayEventOrigin = previous_; }
    DevToolsOverlayEventOriginScope(const DevToolsOverlayEventOriginScope&) = delete;
    DevToolsOverlayEventOriginScope& operator=(const DevToolsOverlayEventOriginScope&) = delete;

private:
    std::wstring previous_;
};

// It is the client's SESSION -- the persistent negotiated connection its owner token named -- rather than the
// short-lived request pipe, because `origin` exists so a client can recognise and drop its own writes echoed back,
// and a client only ever knows its session's connectionId.
static std::wstring OverlayEventOrigin(DevToolsConn* conn, unsigned long long ownerConn = 0)
{
    if (ownerConn != 0) return L"\"" + std::to_wstring(ownerConn) + L"\"";
    return conn ? (L"\"" + std::to_wstring(DevToolsEvents_ConnId(conn)) + L"\"") : std::wstring(L"null");
}

class DevToolsOverlayOwnerScope {
public:
    explicit DevToolsOverlayOwnerScope(unsigned long long conn) : previous_(g_overlayOwnerConn)
    {
        g_overlayOwnerConn = conn;
    }
    ~DevToolsOverlayOwnerScope() { g_overlayOwnerConn = previous_; }
    DevToolsOverlayOwnerScope(const DevToolsOverlayOwnerScope&) = delete;
    DevToolsOverlayOwnerScope& operator=(const DevToolsOverlayOwnerScope&) = delete;

private:
    unsigned long long previous_;
};

static HRESULT RunOverlayMutationOnUiThread(const std::wstring& origin, unsigned long long ownerConn,
                                            std::function<HRESULT()> fn, bool* completed = nullptr,
                                            bool* ownerGone = nullptr)
{
    // A mutation that establishes state under a session that disconnected in between is unreleasable: that session's
    // disconnect release has already run and found the axis unowned, so the chrome stays painted on the app forever.
    auto gone = std::make_shared<bool>(false);
    bool ran = false;
    HRESULT hr = RunOnUiThread([origin, ownerConn, gone, fn = std::move(fn)]() -> HRESULT {
        if (ownerConn != 0 && !DevToolsEvents_IsOwnerLive(ownerConn)) { *gone = true; return E_ABORT; }
        DevToolsOverlayEventOriginScope originScope(origin);
        DevToolsOverlayOwnerScope ownerScope(ownerConn);
        return fn();
    }, &ran);
    if (completed) *completed = ran;
    // A dispatch that TIMED OUT leaves the work item still Running, so the UI thread may be writing this flag at this
    // very moment; reading it would be a data race, and a stale or torn true would answer a transient UI-thread stall
    // with `unknown-owner`, which a client is entitled to read as "my session is gone" and tear itself down over.
    if (ownerGone) *ownerGone = ran && *gone;
    return hr;
}

// Read the live value of one ownership axis. UI thread only, for the same reason every other overlay getter is:
// DevToolsOverlay's state is only safely touched from that thread.
static unsigned long long ReadOwnedAxis_ui(DevToolsStateAxis axis)
{
    switch (axis) {
        case DevToolsStateAxis::Toolbar:        return DevToolsOverlay_IsToolbarRequested() ? 1ull : 0ull;
        case DevToolsStateAxis::LayoutAdorners: return DevToolsOverlay_IsLayoutAdornersOn() ? 1ull : 0ull;
        case DevToolsStateAxis::PickArm:        return DevToolsOverlay_IsPickArmed() ? 1ull : 0ull;
        case DevToolsStateAxis::Window:         return DevToolsWindow_IsOpen() ? 1ull : 0ull;
        case DevToolsStateAxis::Highlight: {
            AcquireSRWLockShared(&g_censusLock);
            const unsigned long long wire = PackWire_nolock(DevToolsOverlay_GetHighlightHandle());
            ReleaseSRWLockShared(&g_censusLock);
            return wire;
        }
        default: return 0ull;
    }
}

// Wrap one axis-changing UI-thread operation so ownership is recorded from what actually happened rather than from
// what was asked for. It is suppressed for the duration and replayed once here, so every subscriber sees exactly one
// event and that event carries the post-write owner and revision rather than the pre-write ones.
static void OnOverlayStateChanged();
static HRESULT RunOwnedMutation_ui(DevToolsStateAxis axis, const std::function<HRESULT()>& fn)
{
    const unsigned long long before = ReadOwnedAxis_ui(axis);
    HRESULT hr = S_OK;
    {
        ++g_ownedMutationDepth;
        struct Unwind {
            ~Unwind() { --g_ownedMutationDepth; }
        } unwind;
        hr = fn();
        const unsigned long long after = ReadOwnedAxis_ui(axis);
        if (g_overlayOwnerConn != 0) g_ownedState.RecordSet(axis, g_overlayOwnerConn, before, after);
        else                         g_ownedState.Reconcile(axis, after);
    }
    if (g_ownedMutationDepth == 0 && g_ownedMutationNotifyPending) {
        g_ownedMutationNotifyPending = false;
        OnOverlayStateChanged();
    }
    return hr;
}

// Its disconnect release has already run and found the axis unowned, so nothing will ever restore it. So the owner is
// revalidated HERE, in the same UI turn as the build and before RecordSet.
static const wchar_t* EnsureOverlayForOwner_ui(unsigned long long ownerConn, std::wstring* errorMessage = nullptr)
{
    if (g_overlayBuilt.load(std::memory_order_acquire)) return nullptr;
    const bool live = ownerConn != 0 && DevToolsEvents_IsOwnerLive(ownerConn);
    const bool wasVisible = DevToolsOverlay_IsToolbarVisible();
    const wchar_t* error = nullptr;
    DevToolsOverlayOwnerScope ownerScope(live ? ownerConn : 0);
    RunOwnedMutation_ui(DevToolsStateAxis::Toolbar, [&]() -> HRESULT {
        error = TryBuildOverlayOnUiThread(errorMessage);
        if (!live && !error && DevToolsOverlay_IsToolbarVisible() != wasVisible) {
            DevToolsOverlay_SetToolbarVisible(wasVisible);
        }
        return S_OK;
    });
    return error;
}

// DevToolsOverlay_DisarmPick is explicit-cancel and deliberately DESELECTS (DevToolsOverlay.h) -- so undoing a pick
// this client armed would also clear whatever another client, or the user, had selected.
static DevToolsOwnedReleaseOutcome ApplyOwnedRelease_ui(unsigned long long owner)
{
    ReleaseOwnedResources_ui(owner);
    DevToolsOwnedReleaseOutcome outcome = g_ownedState.Release(owner);
    if (outcome.released.empty()) {
        outcome.revision = g_ownedState.Revision();
        return outcome;
    }

    // Restores are attributed to nobody: an undo re-establishes what the app had, it does not make the
    // releasing client the owner of the value it put back.
    DevToolsOverlayOwnerScope ownerScope(0);
    const InstanceHandle root = RootVisual();
    std::vector<DevToolsOwnedRestore> applied;
    std::vector<DevToolsStateAxis> skipped;

    auto restoreOne = [&](const DevToolsOwnedRestore& restore) {
        if (ReadOwnedAxis_ui(restore.axis) != restore.expectedValue) { skipped.push_back(restore.axis); return; }
        RunOwnedMutation_ui(restore.axis, [&]() -> HRESULT {
            switch (restore.axis) {
                case DevToolsStateAxis::Toolbar:
                    DevToolsOverlay_SetToolbarVisible(restore.restoreTo != 0);
                    break;
                case DevToolsStateAxis::LayoutAdorners:
                    DevToolsOverlay_SetLayoutAdorners(restore.restoreTo != 0);
                    break;
                case DevToolsStateAxis::PickArm:
                    if (restore.restoreTo != 0) DevToolsOverlay_ArmPick();
                    else                        DevToolsOverlay_CompleteOneShotPick();
                    break;
                case DevToolsStateAxis::Window:
                    if (restore.restoreTo != 0) OpenInprocWindow();
                    else                        DevToolsWindow_Close();
                    break;
                case DevToolsStateAxis::Highlight: {
                    // Restored through SelectHandle rather than HighlightHandle: the axis carries the anchored
                    // selection as well as the rect, and restoring to 0 must tear down every piece of chrome,
                    // not leave a badge parked on an element nobody selected.
                    InstanceHandle ih = 0;
                    if (restore.restoreTo != 0) {
                        const wchar_t* e = nullptr; bool ok;
                        AcquireSRWLockShared(&g_censusLock);
                        ok = ResolveWire_nolock(restore.restoreTo, &ih, &e);
                        ReleaseSRWLockShared(&g_censusLock);
                        if (!ok) ih = 0;  // the element we found selected is gone; clear rather than guess
                    }
                    DevToolsOverlay_SelectHandle(g_diag, root, ih, OverlaySeed, OverlayContains);
                    break;
                }
                default:
                    break;
            }
            return S_OK;
        });
        applied.push_back(restore);
    };

    for (const DevToolsOwnedRestore& restore : outcome.released) {
        if (restore.axis != DevToolsStateAxis::Highlight) restoreOne(restore);
    }
    for (const DevToolsOwnedRestore& restore : outcome.released) {
        if (restore.axis == DevToolsStateAxis::Highlight) restoreOne(restore);
    }

    // Restoring the Highlight axis moves the chrome, so the slot -- and every surface that reads it -- has to move
    // with it: selection.poll, the in-process window's tree, and every Selection subscriber.
    for (const DevToolsOwnedRestore& restore : applied) {
        if (restore.axis != DevToolsStateAxis::Highlight) continue;
        PublishProtocolSelection(ReadOwnedAxis_ui(DevToolsStateAxis::Highlight), g_overlayEventOrigin);
        break;
    }

    outcome.released = std::move(applied);
    for (DevToolsStateAxis axis : skipped) outcome.retained.push_back(axis);
    outcome.revision = g_ownedState.Revision();
    return outcome;
}

// Fires synchronously on the UI thread every time ANY piece of observable overlay/pick state might have changed;
// re-reads every axis, packs the highlight handle to the same opaque WIRE space every other verb uses, and broadcasts
// Selection. armChanged / Overlay.stateChanged only for the axis (or axes) DevToolsOverlayStateTracker says actually
// moved.
static void OnOverlayStateChanged()
{
    // A protocol write is in flight on this thread: the ownership record does not yet describe it, so anything
    // published now would carry the pre-write owner and revision, and reconciling now would disown the writer
    // mid-write. RunOwnedMutation_ui replays this exactly once when the write is recorded.
    if (g_ownedMutationDepth > 0) {
        g_ownedMutationNotifyPending = true;
        return;
    }

    DevToolsOverlayStateSnapshot current;
    current.armed = DevToolsOverlay_IsPickArmed();
    current.toolbarVisible = DevToolsOverlay_IsToolbarVisible();
    current.layoutAdornersOn = DevToolsOverlay_IsLayoutAdornersOn();
    // The overlay holds a RAW InstanceHandle; every handle on this wire is an opaque census-stamped WIRE handle,
    // packed the same way every other verb packs one.
    {
        AcquireSRWLockShared(&g_censusLock);
        current.highlightHandle = PackWire_nolock(DevToolsOverlay_GetHighlightHandle());
        ReleaseSRWLockShared(&g_censusLock);
    }

    // Fold the observed values into the ownership record BEFORE deciding what to broadcast, so an axis moved by
    // the rail's own buttons, Esc, or the inspector window is disowned and the event carries the new owner.
    // RunOwnedMutation_ui already recorded the protocol writes; these calls are what catch everything else.
    g_ownedState.Reconcile(DevToolsStateAxis::PickArm, current.armed ? 1ull : 0ull);
    g_ownedState.Reconcile(DevToolsStateAxis::Toolbar, current.toolbarVisible ? 1ull : 0ull);
    g_ownedState.Reconcile(DevToolsStateAxis::LayoutAdorners, current.layoutAdornersOn ? 1ull : 0ull);
    g_ownedState.Reconcile(DevToolsStateAxis::Highlight, current.highlightHandle);

    const DevToolsOverlayStateDelta delta = g_overlayStateTracker.Reconcile(current);
    if (!delta.Any()) return;

    if (delta.armedChanged) {
        std::wstring params = L"{\"armed\":" + std::wstring(current.armed ? L"true" : L"false") +
                              L",\"armOwner\":\"" + g_ownedState.OwnerToken(DevToolsStateAxis::PickArm) + L"\"" +
                              L",\"revision\":" + std::to_wstring(g_ownedState.Revision()) + L"}";
        DevToolsEvents_Broadcast(DevToolsDomain_Selection, DevToolsRpcEvent(L"Selection.armChanged", params, g_overlayEventOrigin),
                            DevToolsCoalesce_KeepNewest, L"armChanged");
    }
    if (delta.overlayChanged) {
        std::wstring params = L"{\"toolbarVisible\":" + std::wstring(current.toolbarVisible ? L"true" : L"false") +
                              L",\"highlightHandle\":\"" + std::to_wstring(current.highlightHandle) + L"\"" +
                              L",\"layoutAdornersOn\":" + std::wstring(current.layoutAdornersOn ? L"true" : L"false") +
                              L",\"revision\":" + std::to_wstring(g_ownedState.Revision()) +
                              L",\"toolbarOwner\":\"" + g_ownedState.OwnerToken(DevToolsStateAxis::Toolbar) + L"\"" +
                              L",\"highlightOwner\":\"" + g_ownedState.OwnerToken(DevToolsStateAxis::Highlight) + L"\"" +
                              L",\"layoutOwner\":\"" + g_ownedState.OwnerToken(DevToolsStateAxis::LayoutAdorners) + L"\"}";
        DevToolsEvents_Broadcast(DevToolsDomain_Overlay, DevToolsRpcEvent(L"Overlay.stateChanged", params, g_overlayEventOrigin),
                            DevToolsCoalesce_KeepNewest, L"stateChanged");
    }
}

// Private in-process bridge for the NDJSON JSON-RPC dispatcher, never another public wire.
// HandleRpc validates overlayOwnerConn; zero means no ownership claim. The string bridge does
// not parse caller-supplied ownership or bypass the RPC access checks.
static std::wstring BridgeEscape(const std::wstring& value);
static std::wstring MutatePropertyTarget(const std::wstring& target,
                                        std::function<HRESULT(InstanceHandle)> mutation,
                                        std::shared_ptr<std::wstring> detail = nullptr)
{
    unsigned long long wire = 0;
    {
        CensusSharedLock lock;
        if (!target.empty() && target[0] == L'#') {
            wire = wcstoull(target.c_str() + 1, nullptr, 10);
        } else {
            for (const auto& found : g_nameOf) {
                if (found.second != target || (g_windowScope && !g_windowScope->members.count(found.first))) continue;
                if (wire) return L"ERR ambiguous-selector";
                wire = PackWire_nolock(found.first);
            }
            if (!wire) return L"ERR not-found";
        }
        InstanceHandle handle = 0;
        const wchar_t* error = nullptr;
        if (!ResolveWire_nolock(wire, &handle, &error)) return std::wstring(L"ERR ") + error;
    }
    auto error = std::make_shared<const wchar_t*>(nullptr);
    bool completed = false;
    const HRESULT hr = RunOnUiThread([wire, mutation = std::move(mutation), error]() {
        InstanceHandle handle = 0;
        {
            CensusSharedLock lock;
            if (!ResolveWire_nolock(wire, &handle, error.get())) return E_INVALIDARG;
        }
        return mutation(handle);
    }, &completed);
    if (completed && *error) return std::wstring(L"ERR ") + *error;
    if (completed && FAILED(hr) && detail && !detail->empty())
        return L"ERR " + BridgeEscape(*detail);
    return SUCCEEDED(hr) ? std::wstring(L"OK") : L"ERR " + HrStr(hr);
}

#include "DevToolsTap.Query.inc"
