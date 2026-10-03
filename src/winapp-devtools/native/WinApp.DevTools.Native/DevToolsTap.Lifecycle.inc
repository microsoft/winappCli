// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsTap.cpp; included into that translation unit, not compiled on its own.

// The diagnostics type name is either a short ("StackPanel") or namespace-qualified
// ("Microsoft.UI.Xaml.Controls.Grid") class name, so we suffix-match the WinUI Panel-derived containers we
// can drive via Panel.Children. DataGrid is a Control (not a Panel) despite the "Grid" suffix -> excluded.
static bool IsPanelType(const std::wstring& ty) {
    auto ends = [&](const wchar_t* s) { size_t n = wcslen(s); return ty.size() >= n && ty.compare(ty.size() - n, n, s) == 0; };
    if (ends(L"DataGrid")) return false;
    return ends(L"Panel") || ends(L"Grid") || ends(L"Canvas");
}
// Depth of a handle from the tree root (parent-chain length) via the census parent map. Cycle-guarded.
static unsigned int DepthOf_nolock(InstanceHandle h) {
    unsigned int d = 0; std::set<InstanceHandle> seen;
    while (h && seen.insert(h).second) { auto it = g_parent.find(h); if (it == g_parent.end()) break; h = it->second; ++d; }
    return d;
}
static unsigned int DepthOf(InstanceHandle h) { AcquireSRWLockShared(&g_censusLock); auto r = DepthOf_nolock(h); ReleaseSRWLockShared(&g_censusLock); return r; }
// The overlay would then target an element it cannot append to, so AddChild fails and the overlay is misplaced or
// absent with no diagnostic. Caller must hold g_censusLock (shared suffices).
static InstanceHandle CensusRootOf_nolock(InstanceHandle h)
{
    InstanceHandle cur = h;
    for (int d = 0; d < kMaxEnumDepth && cur; ++d) {
        auto pit = g_parent.find(cur);
        if (pit == g_parent.end()) return cur;
        if (g_type.find(pit->second) == g_type.end()) return cur;
        cur = pit->second;
    }
    return cur;
}

static bool ConfirmAppContentPanel(InstanceHandle h)
{
    if (!g_diag || !h) return false;
    winrt::com_ptr<IInspectable> object;
    if (FAILED(g_diag->GetIInspectableFromHandle(h, object.put())) || !object)
        return false;
    try {
        winrt::Windows::Foundation::IInspectable projected{nullptr};
        winrt::copy_from_abi(projected, object.get());
        const auto panel = projected.try_as<DevToolsXC::Panel>();
        if (!panel) return false;
        const auto root = panel.XamlRoot();
        const auto content = root ? root.Content() : nullptr;
        if (!content) return false;
        const auto contentIdentity = content.as<IUnknown>();
        auto current = object;
        // Async editor template parts can enter the census with new diagnostic handles.
        // A host must belong to the app content tree, never a popup's separate visual tree.
        for (int depth = 0; current && depth < kMaxEnumDepth; ++depth) {
            if (current.as<IUnknown>().get() == contentIdentity.get()) return true;
            winrt::com_ptr<IInspectable> parent;
            winrt::check_hresult(DevToolsGetVisualParent(current.get(), parent.put()));
            current = std::move(parent);
        }
    } catch (const winrt::hresult_error& error) {
        Log(L"overlay.anchor content ancestry unavailable handle=%llu hr=0x%08X",
            (unsigned long long)h, error.code().value);
    }
    return false;
}

static InstanceHandle FindOutermostPanel(InstanceHandle wantCensusRoot, unsigned long long xamlRootKey) {
    // Gather every type-name candidate with its depth under the lock (cheap, no COM), then confirm shallowest first
    // OUTSIDE the lock. Splitting it this way keeps the census lock free of COM calls while still letting the runtime
    // — not the name — have the final say on which element the overlay attaches to.
    std::vector<std::pair<unsigned int, InstanceHandle>> candidates;
    AcquireSRWLockShared(&g_censusLock);
    for (auto& kv : g_type) {
        if (!IsPanelType(kv.second)) continue;
        if (wantCensusRoot && CensusRootOf_nolock(kv.first) != wantCensusRoot) continue;
        candidates.emplace_back(DepthOf_nolock(kv.first), kv.first); // already under the shared lock; must not re-enter it
    }
    ReleaseSRWLockShared(&g_censusLock);
    std::sort(candidates.begin(), candidates.end());
    for (const auto& c : candidates) {
        if (ConfirmAppContentPanel(c.second)) {
            if (xamlRootKey) {
                winrt::com_ptr<IInspectable> element, fe;
                if (FAILED(g_diag->GetIInspectableFromHandle(c.second, element.put())) || !element ||
                    FAILED(element->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), fe.put_void())) || !fe)
                    continue;
                bool loaded = false;
                DevToolsSurface surface;
                if (FAILED(DevToolsGetIsLoaded(fe.get(), &loaded)) || !loaded ||
                    !DevToolsSurface_Resolve(g_diag, c.second, &surface) || surface.xamlRootKey != xamlRootKey)
                    continue;
            }
            return c.second;
        }
        Log(L"overlay.anchor rejected candidate outside app content or without IPanel handle=%llu type='%s' depth=%u",
            (unsigned long long)c.second, TypeOf(c.second).c_str(), c.first);
    }
    return 0;
}

static HWINEVENTHOOK g_appForegroundHook = nullptr;
static std::vector<std::pair<unsigned long long, unsigned long long>> g_appFocusHistory;

static InstanceHandle FindOverlayRecoveryPanel(unsigned long long xamlRootKey)
{
    if (xamlRootKey) return FindOutermostPanel(0, xamlRootKey);
    DevToolsOverlay_RefreshSurfaces(g_diag, 0);
    for (const auto& previous : g_appFocusHistory) {
        InstanceHandle raw = 0;
        const wchar_t* error = nullptr;
        {
            CensusSharedLock lock;
            if (!ResolveWire_nolock(previous.first, &raw, &error)) continue;
        }
        DevToolsSurface surface;
        if (DevToolsSurface_Resolve(g_diag, raw, &surface) && surface.xamlRootKey == previous.second)
            if (const auto panel = FindOutermostPanel(0, surface.xamlRootKey)) return panel;
    }
    for (const auto& surface : DevToolsOverlay_AllSurfaces())
        if (const auto panel = FindOutermostPanel(0, surface.xamlRootKey)) return panel;
    return 0;
}

static void FollowAppForeground(HWND hwnd)
{
    if (!g_diag || !g_overlayBuilt.load(std::memory_order_acquire) || !WindowOwnedByProcess(hwnd)) return;
    TapWindowScopeGuard unscoped(nullptr);
    DevToolsOverlay_RefreshSurfaces(g_diag, 0);
    const DevToolsSurface* candidate = nullptr;
    const auto& toolbar = DevToolsOverlay_ToolbarSurface();
    for (const auto& surface : DevToolsOverlay_AllSurfaces()) {
        if (surface.hostHwnd != hwnd) continue;
        if (surface.xamlRootKey == toolbar.xamlRootKey) { candidate = &surface; break; }
        if (candidate) {
            Log(L"overlay.focus ambiguous app window; retaining toolbar host");
            return;
        }
        candidate = &surface;
    }
    // Inspector and foreign windows are absent from the app-only XAML census.
    if (!candidate) return;
    const auto key = candidate->xamlRootKey;
    unsigned long long wire = 0;
    {
        CensusSharedLock lock;
        wire = PackWire_nolock(candidate->rootHandle);
    }
    if (!wire) return;
    g_appFocusHistory.erase(std::remove_if(g_appFocusHistory.begin(), g_appFocusHistory.end(),
        [key](const auto& old) { return old.second == key; }), g_appFocusHistory.end());
    g_appFocusHistory.insert(g_appFocusHistory.begin(), {wire, key});
    if (g_appFocusHistory.size() > 64) g_appFocusHistory.resize(64);
    if (key == toolbar.xamlRootKey) return;
    const auto panel = FindOutermostPanel(0, key);
    if (!panel) return;
    OverlayBuildScope scope;
    const HRESULT hr = DevToolsOverlay_MoveToPanel(g_diag, panel);
    if (SUCCEEDED(hr)) g_overlayPanel = panel;
    else Log(L"overlay.focus host move failed hr=0x%08X", hr);
}

static void CALLBACK OnAppForeground(HWINEVENTHOOK, DWORD, HWND hwnd, LONG, LONG, DWORD, DWORD)
{
    FollowAppForeground(hwnd);
}

static void OnOverlayTreeChanged()
{
    if (!g_overlayBuilt.load(std::memory_order_acquire) || !g_diag) return;
    OverlayBuildScope scope;
    InstanceHandle panel = 0;
    const HRESULT hr = DevToolsOverlay_RecoverHost(g_diag, FindOverlayRecoveryPanel, &panel);
    if (panel) g_overlayPanel = panel;
    if (FAILED(hr)) Log(L"overlay.host recovery unavailable hr=0x%08X", hr);
    DevToolsOverlay_YieldToWindowedPopups();
}

static InstanceHandle RootVisual() {
    if (g_windowScope) {
        return g_windowScope->root ? g_windowScope->root : g_windowScope->pickRoot;
    }
    const auto root = DevToolsOverlay_ActiveSurface().rootHandle;
    return root ? root : (g_overlayPanel ? g_overlayPanel : g_firstPanel);
}

// Build the overlay on the UI thread within census self-exclusion. Return nullptr on success
// or an explicit reason token; the wire must not report a failed build as a visible overlay.
static const wchar_t* TryBuildOverlayOnUiThread(std::wstring* errorMessage)
{
    if (errorMessage) errorMessage->clear();
    if (g_overlayBuilt.load(std::memory_order_acquire)) return nullptr;
    if (!g_diag || !g_vts3 || !g_dispatcher) { Log(L"overlay: prerequisites missing (diag/vts3/dispatcher)"); return L"not-ready"; }
    InstanceHandle panel = g_overlayPanel;
    if (!panel) {
        // No Panel anywhere under the root. The overlay hosts its Popup in a real app Panel's Children, so
        // there is nothing to attach to and the app is left untouched -- which is correct, but must be SAID.
        Log(L"overlay: no app Panel found; skipping overlay (app left untouched)");
        return L"no-host-panel";
    }
    DevToolsOverlay_SetStateChangedSink(&OnOverlayStateChanged);
    DevToolsOverlay_SetSurfaceRootsReader(&TapCensusRoots);
    DevToolsOverlay_SetSelectionHostBridge(&EnsureSelectionHost);
    if (!DevToolsOverlay_RefreshSurfaces(g_diag, panel))
        Log(L"overlay: no surface resolved for anchor=%llu; coordinates degrade to the window search", (unsigned long long)panel);
    unsigned int index = ChildCountOf(panel);
    // Bracket the whole UI-thread build so every Add it triggers is treated as overlay (excluded + seeded),
    // regardless of the order the framework realizes the subtree in.
    g_overlayBuilding.store(false, std::memory_order_release);
    InstanceHandle root = RootVisual();
    HRESULT hr;
    {
        OverlayBuildScope scope;
        hr = DevToolsOverlay_BuildAndInsert(g_diag, g_vts3, panel, index, OverlaySeed, root, OverlayContains, errorMessage);
    }
    Log(L"overlay: BuildAndInsert panel=%llu type=%s index=%u hr=0x%08X count=%d", panel, TypeOf(panel).c_str(), index, hr, g_elemCount.load());
    DevToolsOverlay_SetPickedSink(&OnOverlayReleasedPick);
    DevToolsOverlay_SetPickTarget(&PickTarget, &DevToolsWindow_ShowsFrameworkRows);
    DevToolsOverlay_SetFocusedSink(&OnOverlayFocused);
    // One owner for pick mode: the overlay's catcher. The DevTools window's toggle follows it from here, so
    // the two can no longer disagree about whether the app is picking.
    DevToolsOverlay_SetPickModeSink(&DevToolsWindow_OnPickModeChanged);
    DevToolsOverlay_SetAnchorBridge(
        [](InstanceHandle h, std::wstring* anchor) { return TapAnchorOf(h, anchor); },
        &TapResolveAnchor, &TapWireOf);
    // Drop 2: register the inline property-card bridge so the overlay's Quick-edits card can read/write the
    // picked element's curated properties in-process (no pipe, no shipped host).
    DevToolsOverlay_SetCardBridge(&CardReadBridge, &CardReadInputBridge, &CardWriteBridge);
    DevToolsOverlay_SetCardScope(&CardScopeBridge);
    DevToolsOverlay_SetCardFreeze(&CardFreezeBridge);
    DevToolsOverlay_SetSourceReader(&OverlaySourceBridge);
    DevToolsOverlay_SetDeclaredReader(&IsDeclaredElement);
    // let the in-app panel ask the injected managed agent why a binding is broken. It goes through the
    // SAME CmdBindingOp the `Binding.*` DevTools verbs use, so the panel and an external protocol client cannot
    // disagree -- which is exactly the defect shipped by having two implementations of one capability.
    DevToolsOverlay_SetBindingAsk(&OverlayBindingAskBridge);
    DevToolsOverlay_SetPathWalkAsk(&OverlayPathWalkBridge);
    // Route 1: register the in-proc Inspect bridge so the Inspect button can open a second WinUI Window on
    // this app's UI thread (default for WinUI targets) instead of launching the external host.
    DevToolsOverlay_SetInprocInspect(&InprocInspect);
    // The build itself can fail on the UI thread (a XamlReader parse, a Popup that will not attach). Report
    // that too rather than only logging it -- the bridges above are registered either way and are harmless
    // without an overlay, but the caller must not be told a toolbar exists when it does not.
    if (FAILED(hr)) {
        if (errorMessage) *errorMessage = L"DevTools overlay build failed (" + HrStr(hr) + L")" +
            (errorMessage->empty() ? L"." : L": " + *errorMessage);
        return L"build-failed";
    }
    g_overlayBuilt.store(true, std::memory_order_release);
    if (!g_appForegroundHook) {
        g_appForegroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, nullptr,
            &OnAppForeground, GetCurrentProcessId(), 0, WINEVENT_OUTOFCONTEXT);
        if (!g_appForegroundHook) Log(L"overlay.focus subscription failed error=%lu", GetLastError());
    }
    GUITHREADINFO focus{sizeof(focus)};
    if (GetGUIThreadInfo(GetCurrentThreadId(), &focus)) FollowAppForeground(focus.hwndActive);
    if (!DevToolsTreeWatch_Subscribe(&OnOverlayTreeChanged))
        Log(L"overlay.host tree-change recovery unavailable: subscription not armed");
    return nullptr;
}

static const wchar_t* TryBuildOverlay(const std::wstring& eventOrigin, unsigned long long ownerConn,
                                    std::wstring* errorMessage)
{
    if (errorMessage) errorMessage->clear();
    auto error = std::make_shared<std::pair<const wchar_t*, std::wstring>>(nullptr, L"");
    // That is the first Overlay.show for most clients -- the sample attaches headlessly, so the canvas does not exist
    // until it asks -- and attributing that false->true transition to nobody would leave the client's own rail
    // permanently unownable: every later Overlay.show is a no-op, and a no-op establishes nothing
    // (DevToolsOwnedState::RecordSet).
    bool ownerGone = false;
    HRESULT hr = RunOverlayMutationOnUiThread(eventOrigin, ownerConn, [error]() {
        return RunOwnedMutation_ui(DevToolsStateAxis::Toolbar, [error]() {
            error->first = TryBuildOverlayOnUiThread(&error->second);
            return S_OK;
        });
    }, nullptr, &ownerGone);
    if (ownerGone) return L"unknown-owner";
    if (FAILED(hr)) return L"not-on-dispatcher";
    if (errorMessage) *errorMessage = std::move(error->second);
    return error->first;
}

static DWORD WINAPI Worker(LPVOID p)
{
    WorkerArgs a = *reinterpret_cast<WorkerArgs*>(p); delete reinterpret_cast<WorkerArgs*>(p);
    // Install crash capture first thing on the worker thread (post-injection, off the loader lock) so
    // any subsequent AV in the tap is recorded with a minidump instead of vanishing with the app.
    DevToolsInstallCrashHandler();
    // Initialize COM for this worker's setup. Returning does not pin the MTA;
    // later binding callers own their COM scopes independently.
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    auto git = GetGit();
    if (git) {
        git->GetInterfaceFromGlobal(a.cd, __uuidof(IXamlDiagnostics), reinterpret_cast<void**>(&g_diag));
        git->GetInterfaceFromGlobal(a.cv, __uuidof(IVisualTreeService3), reinterpret_cast<void**>(&g_vts3));
        // The cookies were only a cross-thread transport; now that we hold our own references, revoke them
        // so the GIT doesn't retain a second marshaled reference for the life of the process.
        git->RevokeInterfaceFromGlobal(a.cd);
        git->RevokeInterfaceFromGlobal(a.cv);
    } else {
        Log(L"no GIT");
    }
    if (g_vts3) {
        g_treeCb.EnsureFtm();
        // Mint a session nonce from QPC/process/tick state for wire-handle session separation.
        {
            LARGE_INTEGER qpc; QueryPerformanceCounter(&qpc);
            uint64_t z = (uint64_t)qpc.QuadPart ^ ((uint64_t)GetCurrentProcessId() << 32) ^ (uint64_t)GetTickCount64();
            z += 0x9E3779B97F4A7C15ull;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ull;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBull;
            z = z ^ (z >> 31);
            g_sessionNonce = z & NONCE_MASK;
        }
        HRESULT hrA = g_vts3->AdviseVisualTreeChange(&g_treeCb);
        if (FAILED(hrA)) g_adviseFailed.store(true);
        Log(L"AdviseVisualTreeChange hr=0x%08X elements=%d named=%zu firstPanel=%llu", hrA, g_elemCount.load(), g_byName.size(), g_firstPanel);
        // Acquire the app UI-thread dispatcher (needed to build/mutate the overlay on the UI thread).
        if (g_diag) {
            IInspectable* di = nullptr;
            if (SUCCEEDED(g_diag->GetDispatcher(&di)) && di) { di->QueryInterface(DevToolsIid<DevToolsD::IDispatcherQueue>(), reinterpret_cast<void**>(&g_dispatcher)); di->Release(); }
        }
        Log(L"dispatcher=%p", g_dispatcher);
        {
            wchar_t root[MAX_PATH] = L"";
            DWORD rn = GetEnvironmentVariableW(L"WINAPP_DEVTOOLS_SOURCE_ROOT", root, _countof(root));
            unsigned long long built = 0;
            wchar_t exe[MAX_PATH] = L"";
            if (GetModuleFileNameW(nullptr, exe, _countof(exe))) {
                WIN32_FILE_ATTRIBUTE_DATA fad{};
                if (GetFileAttributesExW(exe, GetFileExInfoStandard, &fad)) {
                    ULARGE_INTEGER li{};
                    li.LowPart = fad.ftLastWriteTime.dwLowDateTime;
                    li.HighPart = fad.ftLastWriteTime.dwHighDateTime;
                    if (li.QuadPart > 116444736000000000ULL)
                        built = (li.QuadPart - 116444736000000000ULL) / 10000000ULL;
                }
            }
            DevToolsAuthored_Init((rn > 0 && rn < _countof(root)) ? root : L"", built);
            wchar_t inventory[32768]{}, inventoryHash[65]{}, payloadHeld[65]{};
            const DWORD inventoryLength = GetEnvironmentVariableW(L"WINAPP_DEVTOOLS_SOURCE_INVENTORY", inventory, _countof(inventory));
            const DWORD hashLength = GetEnvironmentVariableW(L"WINAPP_DEVTOOLS_SOURCE_INVENTORY_HASH", inventoryHash, _countof(inventoryHash));
            const DWORD heldLength = GetEnvironmentVariableW(L"WINAPP_DEVTOOLS_SOURCE_PAYLOAD_HELD", payloadHeld, _countof(payloadHeld));
            if (inventoryLength && inventoryLength < _countof(inventory) && hashLength == 64) {
                const std::wstring image(exe);
                DevToolsAuthored_InitCoordinates(inventory, inventoryHash,
                    heldLength == 64 && wcscmp(payloadHeld, inventoryHash) == 0
                        ? image.substr(0, image.find_last_of(L"\\/")) : L"");
            }
            Log(L"authored.init root='%s' builtUnix=%llu", (rn > 0 && rn < _countof(root)) ? root : L"(none)", built);
        }
        auto panel = std::make_shared<InstanceHandle>(0);
        const HRESULT anchorResult = RunOnUiThread([panel]() {
            *panel = FindOutermostPanel(0);
            return S_OK;
        });
        if (SUCCEEDED(anchorResult)) g_overlayPanel = *panel;
        else Log(L"overlay: initial app panel discovery failed hr=0x%08X", anchorResult);
        Log(L"overlay: outermost panel=%llu type=%s depth=%u children=%u (firstPanel=%llu)",
            g_overlayPanel, TypeOf(g_overlayPanel).c_str(), DepthOf(g_overlayPanel), ChildCountOf(g_overlayPanel), g_firstPanel);
    } else {
        // Couldn't bind the visual tree service (no GIT, or the interface didn't resolve). Start the pipe
        // anyway so ping gets a fast, definitive "connected but tree didn't bind" (pong -1) instead of a
        // 30s client timeout.
        Log(L"worker: IVisualTreeService3 unavailable; serving pong -1");
        g_adviseFailed.store(true);
    }
    HANDLE pipeThread = CreateThread(nullptr, 0, DevToolsThreadEntry<PipeServer>, nullptr, 0, nullptr);
    if (pipeThread) CloseHandle(pipeThread);
    return 0;
}

struct DevToolsTap : IObjectWithSite
{
    std::atomic<long> ref{1};
    IUnknown* site = nullptr;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID r, void** p) override {
        if (!p) return E_POINTER;
        if (r == IID_IUnknown || r == IID_IObjectWithSite) { *p = static_cast<IObjectWithSite*>(this); AddRef(); return S_OK; }
        *p = nullptr; return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++ref; }
    ULONG STDMETHODCALLTYPE Release() override { long r = --ref; if (r == 0) delete this; return r; }
    HRESULT STDMETHODCALLTYPE SetSite(IUnknown* s) override {
        Log(L"SetSite(%p)", s);
        if (site) { site->Release(); site = nullptr; }
        if (!s) return S_OK;
        s->AddRef(); site = s;
        IXamlDiagnostics* diag = nullptr;
        if (FAILED(s->QueryInterface(__uuidof(IXamlDiagnostics), reinterpret_cast<void**>(&diag))) || !diag) { Log(L"no IXamlDiagnostics"); return S_OK; }

        // Trust posture (see DevToolsTrust.h): read the injector's non-secret JSON init blob ONCE, here, before
        // the command pipe (or anything else) ever starts serving. NEVER log its contents or the cliExe it
        // carries, even under WINAPP_DEVTOOLS_LOG -- Log() calls below deliberately omit both.
        BSTR initBstr = nullptr;
        std::wstring initJson;
        if (SUCCEEDED(diag->GetInitializationData(&initBstr)) && initBstr) {
            initJson.assign(initBstr, SysStringLen(initBstr));
            SysFreeString(initBstr);
        }
        DevToolsTrustInit init = DevToolsTrust_ParseInit(initJson, DevToolsTrust_MutationEnvDenied());
        const bool firstInitialization = DevToolsTrust_InitializePosture(init.posture);
        if (firstInitialization) {
            if (init.cliSibling) {
                init.cliExe = GuestSiblingWriterPath();
                if (init.cliExe.empty()) init.guestInitializationError = L"sibling-writer-unavailable";
            }
            g_guestInitializationError.store(init.guestInitializationError);
        }
        if (firstInitialization && init.guestComments) {
            DevToolsOverlay_SetGuestComments(init.guestCommentStart, init.guestCommentBinding, init.guestCommentEpoch);
        }
        if (firstInitialization && !init.cliExe.empty()) {
            HandleCommand(L"devtools.cli|" + init.cliExe);
        }

        IVisualTreeService3* vts3 = nullptr;
        if (FAILED(diag->QueryInterface(__uuidof(IVisualTreeService3), reinterpret_cast<void**>(&vts3))) || !vts3) { Log(L"no IVisualTreeService3"); diag->Release(); return S_OK; }
        auto git = GetGit(); DWORD cd = 0, cv = 0;
        git->RegisterInterfaceInGlobal(diag, __uuidof(IXamlDiagnostics), &cd);
        git->RegisterInterfaceInGlobal(vts3, __uuidof(IVisualTreeService3), &cv);
        vts3->Release(); diag->Release();
        auto args = new WorkerArgs{ cd, cv };
        HANDLE th = CreateThread(nullptr, 0, DevToolsThreadEntry<Worker>, args, 0, nullptr);
        if (th) { CloseHandle(th); }
        else {
            // Worker never ran to revoke the GIT registrations; revoke them here so the marshaled diag/vts3
            // references aren't retained in the GIT for the process lifetime on this failure path.
            git->RevokeInterfaceFromGlobal(cd);
            git->RevokeInterfaceFromGlobal(cv);
            delete args;
        }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetSite(REFIID r, void** p) override { if (site) return site->QueryInterface(r, p); if (p) *p = nullptr; return E_FAIL; }
};

struct Factory : IClassFactory
{
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID r, void** p) override {
        if (!p) return E_POINTER;
        if (r == IID_IUnknown || r == IID_IClassFactory) { *p = static_cast<IClassFactory*>(this); return S_OK; }
        *p = nullptr; return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return 2; }
    ULONG STDMETHODCALLTYPE Release() override { return 1; }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override {
        if (outer) return CLASS_E_NOAGGREGATION;
        auto t = new (std::nothrow) DevToolsTap(); if (!t) return E_OUTOFMEMORY;
        HRESULT hr = t->QueryInterface(riid, ppv); t->Release(); return hr;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL) override { return S_OK; }
};
static Factory g_factory;

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv) {
    if (rclsid == CLSID_DevToolsTap) return g_factory.QueryInterface(riid, ppv);
    if (ppv) *ppv = nullptr; return CLASS_E_CLASSNOTAVAILABLE;
}
// This agent runs a pipe/worker thread and holds a live diagnostics subscription for the whole process
// lifetime, so it must never be unloaded by a host CoFreeUnusedLibraries. Always report "still in use".
extern "C" HRESULT __stdcall DllCanUnloadNow() { return S_FALSE; }
BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) {
    // Keep DllMain trivial: no logging or COM under the loader lock, and no DisableThreadLibraryCalls -
    // with the static CRT (/MT) the runtime relies on thread attach/detach to manage per-thread data.
    return TRUE;
}
