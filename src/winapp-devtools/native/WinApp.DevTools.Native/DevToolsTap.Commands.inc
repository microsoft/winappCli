// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsTap.cpp; included into that translation unit, not compiled on its own.

static std::wstring HandleCommand(const std::wstring& line,
                                  const std::wstring& overlayEventOrigin = L"null",
                                  unsigned long long overlayOwnerConn = 0) {
    if (line == L"hello") {
        // Pure read on the pipe thread. The CLI-only setup back-channel (devtools.cli|) is deliberately omitted from
        // the advertised verb list.
        std::wstring out = L"{\"protocol\":\"winapp-devtools\",\"protocolVersion\":\"0\",\"experimental\":true,\"processId\":";
        out += std::to_wstring(GetCurrentProcessId());
        out += L",\"mutation\":";
        out += DevToolsTrust_MutationEnabled() ? L"true" : L"false";
        out += L",\"verbs\":[\"hello\",\"ping\",\"visualtree.enumerate\",\"visualtree.appauthored\",\"property.get\",\"element.source\","
               L"\"resource.resolve\",\"highlight\",\"selection.poll\",\"overlay.show\",\"overlay.layout\",\"hotreload.setprop\","
               L"\"hotreload.clearprop\",\"selection.arm\",\"selection.disarm\"]}";
        return out;
    }
    if (line.rfind(L"perf|", 0) == 0) {
        // `start` latches the UI thread, zeroes the counters and arms the jank sampler; `stop` disarms it and freezes
        // the window; `report` dumps the whole budget. start/stop MARSHAL onto the app UI thread on purpose: a
        // null-hwnd timer belongs to the thread that created it, so arming it from the pipe thread would produce a
        // sampler whose WM_TIMER is delivered to a thread with no message loop -- ticks=0 forever, which reads as a
        // perfectly smooth run.
        const std::wstring action = line.substr(5);
        if (action == L"report") return DevToolsPerf_ReportJson();
        if (action == L"start" || action == L"stop" || action == L"reset") {
            auto ok = std::make_shared<bool>(false);
            HRESULT hr = RunOnUiThread([action, ok]() {
                DevToolsPerf_LatchUiThread();
                if (action == L"start") {
                    DevToolsPerf_SetEnabled(true);
                    DevToolsPerf_Reset();
                    *ok = DevToolsPerf_SamplerStart();
                } else if (action == L"stop") {
                    DevToolsPerf_SamplerStop();
                    DevToolsPerf_SetEnabled(false);
                    *ok = true;
                } else {
                    DevToolsPerf_Reset();
                    *ok = true;
                }
                return S_OK;
            });
            if (FAILED(hr)) return L"ERR ui-thread";
            if (!*ok) return L"ERR sampler";
            return L"OK";
        }
        return L"ERR bad-args";
    }
    if (line == L"ping") {
        // pong -1 IFF the visual-tree subscription never bound (g_adviseFailed) - there is no census to report.
        // Otherwise DERIVE the count from the census map via the SAME "present in g_type" predicate that
        // visualtree.enumerate uses to decide a handle is a census node (see CensusPopulation_nolock).
        if (g_adviseFailed.load()) return std::wstring(L"pong -1");
        CensusSharedLock lock;
        return L"pong " + std::to_wstring(CensusPopulation_nolock());
    }
    if (line == L"overlay.show") {
        // The --json/oracle path never sends this, so headless reads create no overlay nodes.
        // Return actual attach failure instead of OK-with-no-toolbar so clients can retry or report it.
        if (g_overlayBuilt.load(std::memory_order_acquire)) {
            // already built does NOT mean already VISIBLE -- a prior Overlay.hide may have parked the
            // rail. Show must idempotently ensure the rail is back on screen, not just that the overlay
            // canvas exists, or a client's show-after-hide would silently do nothing.
            bool ownerGone = false;
            HRESULT hr = RunOverlayMutationOnUiThread(overlayEventOrigin, overlayOwnerConn, []() {
                return RunOwnedMutation_ui(DevToolsStateAxis::Toolbar,
                                           []() {
                                               const HRESULT result = DevToolsOverlay_SetToolbarVisible(true);
                                               OnOverlayTreeChanged();
                                               return result;
                                           });
            }, nullptr, &ownerGone);
            if (ownerGone) return std::wstring(L"ERR unknown-owner");
            return SUCCEEDED(hr) ? std::wstring(L"OK") : L"ERR " + HrStr(hr);
        }
        std::wstring errorMessage;
        const wchar_t* err = TryBuildOverlay(overlayEventOrigin, overlayOwnerConn, &errorMessage);
        if (err) return std::wstring(L"ERR ") + err +
            (errorMessage.empty() ? L"" : L"|" + BridgeEscape(errorMessage));
        return L"OK";
    }
    if (line == L"overlay.hide") {
        // explicit counterpart to overlay.show -- hide the floating rail without tearing down the
        // overlay canvas, the pick catcher, the highlight, or the layout adorners (independent overlay state).
        // A no-op (still OK) when the overlay was never built: there is no rail to hide.
        bool ownerGone = false;
        HRESULT hr = RunOverlayMutationOnUiThread(overlayEventOrigin, overlayOwnerConn, []() {
            return RunOwnedMutation_ui(DevToolsStateAxis::Toolbar,
                                       []() { return DevToolsOverlay_SetToolbarVisible(false); });
        }, nullptr, &ownerGone);
        if (ownerGone) return std::wstring(L"ERR unknown-owner");
        return SUCCEEDED(hr) ? std::wstring(L"OK") : L"ERR " + HrStr(hr);
    }
    if (line == L"overlay.getstate") {
        // registry-derived queryable overlay state -- toolbar visibility, the highlighted element (as an opaque WIRE
        // handle, "0" for nothing highlighted), and the layout-adorner toggle, independently.
        struct OverlayState {
            bool toolbarVisible = false; unsigned long long highlightWire = 0; bool layoutOn = false;
            unsigned long long revision = 0;
            std::wstring toolbarOwner, highlightOwner, layoutOwner;
            std::wstring activeSurface, host; size_t surfaceCount = 0;
            unsigned long long toolbarRoot = 0, selectionRoot = 0;
            uintptr_t toolbarWindow = 0, selectionWindow = 0;
        };
        auto st = std::make_shared<OverlayState>();
        // Reconciling and reading the owners happens INSIDE the UI work item, on the same thread and in the same turn
        // as every mutation: folding a value observed here back in from the pipe thread afterwards would let a write
        // that landed in between be mistaken for an unattributed change and disown its owner.
        HRESULT hr = RunOnUiThread([st]() {
            st->toolbarVisible = DevToolsOverlay_IsToolbarVisible();
            st->layoutOn = DevToolsOverlay_IsLayoutAdornersOn();
            {
                AcquireSRWLockShared(&g_censusLock);
                st->highlightWire = PackWire_nolock(DevToolsOverlay_GetHighlightHandle());
                ReleaseSRWLockShared(&g_censusLock);
            }
            g_ownedState.Reconcile(DevToolsStateAxis::Toolbar, st->toolbarVisible ? 1ull : 0ull);
            g_ownedState.Reconcile(DevToolsStateAxis::LayoutAdorners, st->layoutOn ? 1ull : 0ull);
            g_ownedState.Reconcile(DevToolsStateAxis::Highlight, st->highlightWire);
            st->toolbarOwner = g_ownedState.OwnerToken(DevToolsStateAxis::Toolbar);
            st->highlightOwner = g_ownedState.OwnerToken(DevToolsStateAxis::Highlight);
            st->layoutOwner = g_ownedState.OwnerToken(DevToolsStateAxis::LayoutAdorners);
            st->revision = g_ownedState.Revision();
            st->activeSurface = DevToolsOverlay_ToolbarSurface().displayName;
            const auto& toolbar = DevToolsOverlay_ToolbarSurface();
            const auto& selection = DevToolsOverlay_ActiveSurface();
            st->toolbarWindow = reinterpret_cast<uintptr_t>(toolbar.hostHwnd);
            st->selectionWindow = reinterpret_cast<uintptr_t>(selection.hostHwnd);
            {
                CensusSharedLock lock;
                st->toolbarRoot = PackWire_nolock(toolbar.rootHandle);
                st->selectionRoot = PackWire_nolock(selection.rootHandle);
            }
            st->surfaceCount = DevToolsOverlay_AllSurfaces().size();
            st->host = DevToolsOverlay_HostPath();
            return S_OK;
        });
        if (FAILED(hr)) return L"ERR ui-thread";
        std::wstring out = L"{\"toolbarVisible\":";
        out += st->toolbarVisible ? L"true" : L"false";
        out += L",\"highlightHandle\":\"" + std::to_wstring(st->highlightWire) + L"\"";
        out += L",\"layoutAdornersOn\":";
        out += st->layoutOn ? L"true" : L"false";
        out += L",\"revision\":" + std::to_wstring(st->revision);
        out += L",\"toolbarOwner\":\"" + st->toolbarOwner + L"\"";
        out += L",\"highlightOwner\":\"" + st->highlightOwner + L"\"";
        out += L",\"layoutOwner\":\"" + st->layoutOwner + L"\"";
        out += L",\"activeSurface\":\"" + DevToolsJsonEscape(st->activeSurface) + L"\"";
        out += L",\"surfaceCount\":" + std::to_wstring(st->surfaceCount);
        out += L",\"toolbarWindow\":\"" + std::to_wstring(st->toolbarWindow) + L"\"";
        out += L",\"toolbarRoot\":\"" + std::to_wstring(st->toolbarRoot) + L"\"";
        out += L",\"selectionWindow\":\"" + std::to_wstring(st->selectionWindow) + L"\"";
        out += L",\"selectionRoot\":\"" + std::to_wstring(st->selectionRoot) + L"\"";
        out += L",\"host\":\"" + st->host + L"\"";
        out += L"}";
        return out;
    }
    if (line == L"surface.list") {
        struct SurfaceList { std::wstring json; };
        auto st = std::make_shared<SurfaceList>();
        HRESULT hr = RunOnUiThread([st]() {
            DevToolsOverlay_SetSurfaceRootsReader(&TapCensusRoots);
            DevToolsOverlay_RefreshSurfaces(g_diag, 0);
            const DevToolsSurface& active = DevToolsOverlay_ActiveSurface();
            const std::vector<DevToolsSurface>& all = DevToolsOverlay_AllSurfaces();
            std::wstring out = L"{\"surfaces\":[";
            for (size_t i = 0; i < all.size(); ++i) {
                const DevToolsSurface& s = all[i];
                if (g_windowScope && std::none_of(g_windowScope->members.begin(), g_windowScope->members.end(),
                    [&](const auto& member) { return member.second.second == s.xamlRootKey; })) continue;
                if (out.back() != L'[') out += L",";
                out += L"{\"name\":\"" + DevToolsJsonEscape(s.displayName) + L"\"";
                {
                    CensusSharedLock lock;
                    out += L",\"rootHandle\":\"" + std::to_wstring(PackWire_nolock(s.rootHandle)) + L"\"";
                }
                out += L",\"window\":\"" + std::to_wstring(reinterpret_cast<uintptr_t>(s.hostHwnd)) + L"\"";
                out += L",\"active\":"; out += (s.xamlRootKey == active.xamlRootKey) ? L"true" : L"false";
                out += L",\"contentWidth\":" + std::to_wstring((long)s.contentW);
                out += L",\"contentHeight\":" + std::to_wstring((long)s.contentH);
                out += L",\"screenRect\":{\"x\":" + std::to_wstring((long)s.contentRect.left)
                     + L",\"y\":" + std::to_wstring((long)s.contentRect.top)
                     + L",\"w\":" + std::to_wstring((long)(s.contentRect.right - s.contentRect.left))
                     + L",\"h\":" + std::to_wstring((long)(s.contentRect.bottom - s.contentRect.top)) + L"}";
                out += L",\"isIsland\":"; out += (s.islandId != 0) ? L"true" : L"false";
                out += L",\"hostVisible\":"; out += s.hostVisible ? L"true" : L"false";
                // Whether the coordinates are asked of the root or reconstructed from its host window. A
                // client that sees false is looking at a runtime with no IXamlRoot3, where two islands in one
                // window cannot be separated -- so the difference is reported rather than hidden.
                out += L",\"derived\":"; out += s.hasConverter ? L"true" : L"false";
                out += L"}";
            }
            out += L"],\"activeSurface\":\"" + DevToolsJsonEscape(active.displayName) + L"\"}";
            st->json = out;
            return S_OK;
        });
        if (FAILED(hr)) return L"ERR ui-thread";
        return st->json;
    }
    if (line.rfind(L"surface.setactive|", 0) == 0) {
        // (A4): make another XamlRoot the inspected surface. The name is the one Surface.list reports --
        // there is no handle in this API on purpose (naming rule 4), and a window title cannot be the key
        // because two islands share one.
        const std::wstring want = line.substr(wcslen(L"surface.setactive|"));
        if (want.empty()) return std::wstring(L"ERR bad-args");
        struct SwitchState { bool found = false; bool already = false; HRESULT hr = S_OK; std::wstring active; };
        auto st = std::make_shared<SwitchState>();
        HRESULT hr = RunOnUiThread([st, want]() {
            DevToolsOverlay_RefreshSurfaces(g_diag, 0);
            const std::vector<DevToolsSurface>& all = DevToolsOverlay_AllSurfaces();
            const DevToolsSurface* target = nullptr;
            for (const auto& s : all) if (s.displayName == want) { target = &s; break; }
            if (!target) return S_OK;
            st->found = true;
            if (g_windowScope && std::none_of(g_windowScope->members.begin(), g_windowScope->members.end(),
                [&](const auto& member) { return member.second.second == target->xamlRootKey; })) {
                st->hr = E_ACCESSDENIED;
                return S_OK;
            }
            if (target->xamlRootKey == DevToolsOverlay_ActiveSurface().xamlRootKey) {
                st->already = true;
                st->active = target->displayName;
                return S_OK;
            }
            st->hr = SwitchActiveSurface_ui(target->xamlRootKey);
            if (SUCCEEDED(st->hr)) Log(L"overlay.switch active surface -> '%s'", want.c_str());
            st->active = DevToolsOverlay_ActiveSurface().displayName;
            return S_OK;
        });
        if (FAILED(hr)) return L"ERR ui-thread";
        if (!st->found) return std::wstring(L"ERR unknown-surface");
        if (FAILED(st->hr)) return L"ERR " + HrStr(st->hr);
        return L"{\"activeSurface\":\"" + DevToolsJsonEscape(st->active) + L"\",\"changed\":" +
               (st->already ? L"false" : L"true") + L"}";
    }
    if (line == L"overlay.layout" || line == L"overlay.layout|on" || line == L"overlay.layout|off") {
        // This is deliberately not a recomputation from the element: the whole point of the gate that reads it is to
        // catch a box model drawn from the wrong size, which a recomputing oracle would agree with.
        const int want = (line == L"overlay.layout|on") ? 1 : (line == L"overlay.layout|off") ? 0 : -1;
        auto st = std::make_shared<DevToolsOverlayLayoutRects>();
        bool ownerGone = false;
        HRESULT hr = RunOverlayMutationOnUiThread(overlayEventOrigin, overlayOwnerConn, [st, want]() {
            return RunOwnedMutation_ui(DevToolsStateAxis::LayoutAdorners, [st, want]() {
                if (want >= 0) DevToolsOverlay_SetLayoutAdorners(want != 0);
                return DevToolsOverlay_GetLayoutRects(st.get());
            });
        }, nullptr, &ownerGone);
        if (ownerGone) return std::wstring(L"ERR unknown-owner");
        if (FAILED(hr)) return L"ERR ui-thread";
        // The overlay holds a RAW InstanceHandle; every handle on this wire is an opaque census-stamped WIRE
        // handle, so it is packed before it leaves. A handle with no live slot packs to 0.
        unsigned long long wire = 0;
        { AcquireSRWLockShared(&g_censusLock); wire = PackWire_nolock((InstanceHandle)st->handle); ReleaseSRWLockShared(&g_censusLock); }
        auto rect = [](const wchar_t* key, const RECT& r) {
            return std::wstring(L",\"") + key + L"\":{\"x\":" + std::to_wstring(r.left) +
                   L",\"y\":" + std::to_wstring(r.top) +
                   L",\"w\":" + std::to_wstring(r.right - r.left) +
                   L",\"h\":" + std::to_wstring(r.bottom - r.top) + L"}";
        };
        std::wstring out = L"{\"on\":";
        out += st->on ? L"true" : L"false";
        out += L",\"placed\":";
        out += st->placed ? L"true" : L"false";
        out += L",\"handle\":\"" + std::to_wstring(wire) + L"\"";
        out += rect(L"box", st->box);
        if (st->haveMargin)  out += rect(L"margin", st->margin);
        if (st->havePadding) out += rect(L"padding", st->padding);
        out += L",\"marginBands\":" + std::to_wstring(st->marginBands);
        out += L",\"padBands\":" + std::to_wstring(st->padBands);
        out += L",\"sizeText\":\"" + DevToolsJsonEscape(st->sizeText) + L"\"";
        out += L"}";
        return out;
    }
    if (line.rfind(L"select|", 0) == 0) {
        // Element pick: `select|<screenX>,<screenY>` -> hit-test the REAL app tree on the UI thread, draw a
        // self-excluded highlight adorner in the overlay, and reply with our own selection.changed record
        // `selection.changed|<handle>\t<type>\t<name>\t<x>,<y>,<w>,<h>` (bounds in XamlRoot DIPs).
        int sx = 0, sy = 0;
        if (swscanf_s(line.c_str() + 7, L"%d,%d", &sx, &sy) != 2) return L"ERR bad-args";
        // Route the pick's out-params through a heap OpState captured BY VALUE (shared_ptr) so they OUTLIVE a
        // RunOnUiThread timeout: if the 10s wait gives up after a handler already claimed the op, HandleCommand
        // unwinds while the closure may still be running - a by-reference capture of these stack locals would then be
        // a use-after-free.
        struct PickState { InstanceHandle picked = 0; RECT b{}; };
        auto st = std::make_shared<PickState>();
        auto sp = std::make_shared<DevToolsSurfacePoint>();
        HRESULT hr = RunOnUiThread([sx, sy, st, sp]() {
            InstanceHandle root = RootVisual();
            if (!root) return E_INVALIDARG;
            // Re-derive the surface set on the gesture, not on a timer: a window or island can have opened or closed
            // since the last pick, and a stale set would name a surface that is gone (or miss the one the point is
            // in, turning an actionable refusal back into an unnamed one).
            DevToolsOverlay_RefreshSurfaces(g_diag, 0);
            // A point over another app window in front picks in that window.
            if (const DevToolsSurface* at = DevToolsOverlay_SurfaceAtScreenPoint(sx, sy); at && !g_windowScope &&
                at->xamlRootKey != DevToolsOverlay_ActiveSurface().xamlRootKey &&
                SUCCEEDED(SwitchActiveSurface_ui(at->xamlRootKey))) root = RootVisual();
            DevToolsOverlay_ClassifyPickPoint(g_diag, root, sx, sy, sp.get());
            if (sp->outside) return S_OK;
            HRESULT h = DevToolsOverlay_Pick(g_diag, root, sx, sy, OverlaySeed, OverlayContains, &st->picked, &st->b);
            // Surface the compact "selected element" badge for the picked element (same UI thread), so a
            // coordinate pick behaves like the in-app Pick button: badge on pick, card opens via its Edit glyph
            // (the card no longer auto-opens). No-op if the card bridge is unregistered.
            if (SUCCEEDED(h) && st->picked) DevToolsOverlay_ShowBadgeForHandle(g_diag, st->picked);
            // Publish the pick (as the mouse Pick path does via PublishOverlayPick) so the window/host handoff can
            // pre-select this element: a coordinate pick shows the same badge, so its "Open in DevTools" and
            // selection.poll must behave identically to a real in-app Pick.
            if (SUCCEEDED(h) && st->picked) PublishOverlayPick(st->picked, false);
            return h;
        });
        if (SUCCEEDED(hr) && sp->outside) {
            // The refusal carries BOTH surface names because either one alone is unactionable: "that point is
            // in WINDOW ONE" without saying what is being inspected reads as a bug report, and "DevTools is
            // inspecting WINDOW TWO" without saying where the point went does not explain the miss.
            return std::wstring(L"ERR outside-surface|") + sp->activeName + L"|" + sp->pointName;
        }
        if (SUCCEEDED(hr) && st->picked) {
            const RECT b = st->b;
            std::wstring type = TypeOf(st->picked);
            std::wstring name = NameOf(st->picked);
            wchar_t out[512];
            _snwprintf_s(out, _countof(out), _TRUNCATE, L"selection.changed|%llu\t%s\t%s\t%ld,%ld,%ld,%ld",
                (unsigned long long)st->picked, type.c_str(), name.c_str(), b.left, b.top, b.right - b.left, b.bottom - b.top);
            return std::wstring(out);
        }
        return std::wstring(L"ERR no-hit");
    }
    if (line.rfind(L"highlight|", 0) == 0) {
        // The token is an opaque packed WIRE handle (same scheme as property.get), NOT a raw InstanceHandle -
        // RE-RESOLVE it to a raw ih INSIDE the UI-thread closure right before the live hit-test (UAF-by-construction:
        // never carry a raw handle across the hop).
        unsigned long long wire = wcstoull(line.c_str() + wcslen(L"highlight|"), nullptr, 10);
        InstanceHandle root = RootVisual();
        auto rerr = std::make_shared<const wchar_t*>(nullptr);
        bool completed = false;
        bool ownerGone = false;
        HRESULT hr = RunOverlayMutationOnUiThread(overlayEventOrigin, overlayOwnerConn, [wire, root, rerr]() -> HRESULT {
            return RunOwnedMutation_ui(DevToolsStateAxis::Highlight, [wire, root, rerr]() -> HRESULT {
                InstanceHandle ih = 0;
                if (!wire && g_windowScope && DevToolsOverlay_GetHighlightHandle() &&
                    !WindowElementCurrent_ui(DevToolsOverlay_GetHighlightHandle())) {
                    *rerr = L"outside-window";
                    return S_FALSE;
                }
                if (wire != 0ull) {
                    const wchar_t* e = nullptr; bool ok;
                    AcquireSRWLockShared(&g_censusLock); ok = ResolveWire_nolock(wire, &ih, &e); ReleaseSRWLockShared(&g_censusLock);
                    if (!ok) { *rerr = e; return S_FALSE; }
                }
                return DevToolsOverlay_HighlightHandle(g_diag, root, ih, OverlaySeed, OverlayContains);
            });
        }, &completed, &ownerGone);
        if (ownerGone) return std::wstring(L"ERR unknown-owner");
        if (completed && *rerr) return std::wstring(L"ERR ") + *rerr;
        if (FAILED(hr)) return L"ERR " + HrStr(hr);
        return L"OK";
    }
    if (line.rfind(L"selection.select|", 0) == 0) {
        // Highlight-only (`highlight|`) remains the HOVER primitive and is unchanged: a client that only wants to
        // light a rect while the pointer moves must not move the app's selection with it.
        unsigned long long wire = wcstoull(line.c_str() + wcslen(L"selection.select|"), nullptr, 10);
        InstanceHandle root = RootVisual();
        auto rerr = std::make_shared<const wchar_t*>(nullptr);
        auto selected = std::make_shared<bool>(false);
        bool completed = false;
        bool ownerGone = false;
        const std::wstring origin = overlayEventOrigin;
        HRESULT hr = RunOverlayMutationOnUiThread(overlayEventOrigin, overlayOwnerConn,
                                                  [wire, root, rerr, selected, origin]() -> HRESULT {
            const HRESULT mutationHr =
                RunOwnedMutation_ui(DevToolsStateAxis::Highlight, [wire, root, rerr, selected]() -> HRESULT {
                    InstanceHandle ih = 0;
                    if (!wire && g_windowScope && DevToolsOverlay_GetHighlightHandle() &&
                        !WindowElementCurrent_ui(DevToolsOverlay_GetHighlightHandle())) {
                        *rerr = L"outside-window";
                        return S_FALSE;
                    }
                    if (wire != 0ull) {
                        const wchar_t* e = nullptr; bool ok;
                        AcquireSRWLockShared(&g_censusLock); ok = ResolveWire_nolock(wire, &ih, &e); ReleaseSRWLockShared(&g_censusLock);
                        // A stale/forged handle must NOT deselect: DevToolsOverlay_SelectHandle treats target 0 as a
                        // full teardown, so silently mapping an unresolvable handle to 0 would turn a race into a
                        // destructive clear of a selection the caller never asked to drop.
                        if (!ok) { *rerr = e; return S_FALSE; }
                    }
                    const HRESULT selectHr = DevToolsOverlay_SelectHandle(g_diag, root, ih, OverlaySeed, OverlayContains);
                    *selected = (selectHr == S_OK);
                    return selectHr;
                });
            // Published from INSIDE the UI-thread work item, not after it: PublishProtocolSelection drives the
            // in-process window's tree through DevToolsWindow_OnPicked, which is UI-thread-only.
            if (*selected) PublishProtocolSelection(wire, origin);
            return mutationHr;
        }, &completed, &ownerGone);
        if (ownerGone) return std::wstring(L"ERR unknown-owner");
        if (completed && *rerr) return std::wstring(L"ERR ") + *rerr;
        if (FAILED(hr)) return L"ERR " + HrStr(hr);
        return HandleCommand(L"selection.poll") + (*selected ? L"|1" : L"|0");
    }
    if (line == L"selection.poll") {
        // selection.poll -> `selection|<gen>|<handle>` (0/0 if nothing has been picked). The window polls this
        // to learn when the user picked an element in-app (via the toolbar Pick button) so it can select the
        // matching node in its tree. Pipe-thread read of the published slot; no UI-thread hop.
        wchar_t out[96];
        _snwprintf_s(out, _countof(out), _TRUNCATE, L"selection|%lu|%llu",
            g_lastPickGen.load(std::memory_order_acquire), g_lastPickHandle.load(std::memory_order_relaxed));
        return std::wstring(out);
    }
    if (line == L"selection.arm") {
        // Arm through the same UI-thread overlay route as the toolbar. HandleRpc enforces access.
// Check the result: absent canvas or refused UI dispatch returns NotReady, not success.
        auto armed = std::make_shared<bool>(false);
        bool ownerGone = false;
        HRESULT hr = RunOverlayMutationOnUiThread(overlayEventOrigin, overlayOwnerConn, [armed]() -> HRESULT {
            return RunOwnedMutation_ui(DevToolsStateAxis::PickArm, [armed]() -> HRESULT {
                if (g_windowScope) {
                    const HRESULT target = EnsureSelectionHost(RootVisual());
                    if (FAILED(target)) return target;
                }
                *armed = DevToolsOverlay_ArmPick();
                return S_OK;
            });
        }, nullptr, &ownerGone);
        if (ownerGone) return std::wstring(L"ERR unknown-owner");
        if (FAILED(hr)) return L"ERR " + HrStr(hr);
        return *armed ? std::wstring(L"OK") : std::wstring(L"ERR not-ready");
    }
    if (line == L"selection.disarm") {
        bool ownerGone = false;
        HRESULT hr = RunOverlayMutationOnUiThread(overlayEventOrigin, overlayOwnerConn, []() -> HRESULT {
            return RunOwnedMutation_ui(DevToolsStateAxis::PickArm, []() -> HRESULT {
                DevToolsOverlay_DisarmPick();
                return S_OK;
            });
        }, nullptr, &ownerGone);
        if (ownerGone) return std::wstring(L"ERR unknown-owner");
        return SUCCEEDED(hr) ? std::wstring(L"OK") : L"ERR " + HrStr(hr);
    }
    if (line == L"selection.completepick") {
        // DevToolsOverlay_DisarmPick is not the same call -- ending a pick with disarm and re-establishing the
        // selection afterwards is two visible state changes, so the highlight blinks off and back on and every
        // Overlay subscriber sees a highlight-cleared event that never happened.
        bool ownerGone = false;
        HRESULT hr = RunOverlayMutationOnUiThread(overlayEventOrigin, overlayOwnerConn, []() -> HRESULT {
            return RunOwnedMutation_ui(DevToolsStateAxis::PickArm, []() -> HRESULT {
                DevToolsOverlay_CompleteOneShotPick();
                return S_OK;
            });
        }, nullptr, &ownerGone);
        if (ownerGone) return std::wstring(L"ERR unknown-owner");
        if (FAILED(hr)) return L"ERR " + HrStr(hr);
        return HandleCommand(L"selection.getstate");
    }
    if (line == L"selection.getstate") {
        // Read-only; marshals onto the UI thread for the same reason overlay.getstate does -- including the reconcile
        // and the owner/revision read, which must describe the same instant as the `armed` value they travel with.
        struct ArmState { bool armed = false; std::wstring owner; unsigned long long revision = 0; };
        auto st = std::make_shared<ArmState>();
        HRESULT hr = RunOnUiThread([st]() -> HRESULT {
            st->armed = DevToolsOverlay_IsPickArmed();
            g_ownedState.Reconcile(DevToolsStateAxis::PickArm, st->armed ? 1ull : 0ull);
            st->owner = g_ownedState.OwnerToken(DevToolsStateAxis::PickArm);
            st->revision = g_ownedState.Revision();
            return S_OK;
        });
        if (FAILED(hr)) return L"ERR ui-thread";
        return std::wstring(L"{\"armed\":") + (st->armed ? L"true" : L"false") +
               L",\"armOwner\":\"" + st->owner + L"\"" +
               L",\"revision\":" + std::to_wstring(st->revision) + L"}";
    }
    if (line.rfind(L"hotreload.setprop|", 0) == 0) {
        // hotreload.setprop|<target>|<prop>|<type>|<value> -> set a literal property on the app UI thread. <target>
        // addresses the element either by x:Name or by "#<wire>", an opaque wire handle from visualtree.enumerate /
        // Selection.picked. The handle form exists because almost nothing in a real app has an x:Name.
        auto p = Split(line, L'|');
        if (p.size() < 5) return L"ERR bad-args";
        // Rejoin p[4..] so it isn't silently truncated, THEN unescape: the client escapes '\'/LF/CR but never '|', so
        // the rejoin must run on the still-escaped text before the single-pass unescaper collapses '\\'/'\n'/'\r'.
        std::wstring rawValue = p[4];
        for (size_t i = 5; i < p.size(); i++) { rawValue += L'|'; rawValue += p[i]; }
        std::wstring prop, type, value;
        if (!TryUnescapeField(p[2], prop) || !TryUnescapeField(p[3], type) || !TryUnescapeField(rawValue, value))
            return L"ERR bad-args";
        auto detail = std::make_shared<std::wstring>();
        return MutatePropertyTarget(p[1], [prop, type, value, detail](InstanceHandle h) {
            return SetLiteral(h, prop, type, value, detail.get());
        }, detail);
    }
    if (line.rfind(L"hotreload.clearprop|", 0) == 0) {
        // hotreload.clearprop|<target>|<prop> -> ClearProperty on the app UI thread, the same primitive the
        // in-process window's Clear value button uses. Addressing is identical to hotreload.setprop.
        auto p = Split(line, L'|');
        if (p.size() < 3) return L"ERR bad-args";
        // A property name cannot contain '|', so unlike the write there is no tail to rejoin here.
        std::wstring prop;
        if (!TryUnescapeField(p[2], prop) || prop.empty()) return L"ERR bad-args";
        return MutatePropertyTarget(p[1], [prop](InstanceHandle h) { return ClearPropertyByPath(h, prop); });
    }
    if (line.rfind(L"devtools.cli|", 0) == 0) {
        // Trusted injector initialization supplies the absolute CLI path before the pipe opens.
// Only SetSite reaches this private bridge; never expose a caller-controlled executable setter
// as an RPC. The target must be able to launch the path, including packaged-host staging.
        const std::wstring exe = line.substr(wcslen(L"devtools.cli|"));
        DevToolsOverlay_SetCliExe(exe.c_str());
        { std::lock_guard<std::mutex> lock(g_commentsMutex); g_cliExe = exe; }
        return L"OK";
    }
    if (line.rfind(L"visualtree.enumerate", 0) == 0) {
        // visualtree.enumerate[|<rootHandle>][|<depth>] -> census snapshot as JSON (pipe thread; no live COM).
        return CmdEnumerate(Split(line, L'|'));
    }
    if (line.rfind(L"visualtree.previews|", 0) == 0) {
        // visualtree.previews|<wire>,<wire>,... Bounded and pulled, not pushed, deliberately: the read realizes
        // projected interfaces on the app's UI thread, so a census-wide preview pass would put enumerate back in the
        // budget Route1ReadPreview's rewrite escaped.
        const std::wstring list = line.substr(wcslen(L"visualtree.previews|"));
        std::vector<unsigned long long> wires;
        size_t requested = 0;
        bool truncated = false;
        if (!DevToolsBatch::ParsePreviewHandles(list, kMaxPreviewBatch, wires, requested, truncated))
            return L"ERR bad-args";

        auto captions = std::make_shared<std::vector<std::wstring>>(wires.size());
        auto values = std::make_shared<std::vector<std::vector<DevToolsBatch::PreviewValue>>>(wires.size());
        auto facts = std::make_shared<std::vector<DevToolsBatch::PreviewFacts>>(wires.size());
        HRESULT hr = RunOnUiThread([wires, captions, values, facts]() -> HRESULT {
            WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::TapPreviewBatch);
            // Route1ReadPreview takes the WIRE handle and re-resolves it under the census lock itself (the
            // window's own rows are filled the same way), so a handle that went stale between enumerate and
            // this request yields an empty caption and is omitted -- never a dereference of a dead slot.
            for (size_t i = 0; i < wires.size(); ++i) {
                Route1ReadPreview((InstanceHandle)wires[i], (*captions)[i], nullptr, &(*values)[i]);
                ReadPreviewFacts((InstanceHandle)wires[i], (*facts)[i]);
            }
            return S_OK;
        });
        if (FAILED(hr)) return L"ERR ui-thread";
        return DevToolsBatch::BuildPreviewJson(wires, *captions, requested, truncated, values.get(), facts.get());
    }
    if (line == L"visualtree.appauthored") {
        // visualtree.appauthored -> classify every census node by XAML source (UI-thread read). Returns
        // {"appAuthored":[...wire],"sourceInstrumented":bool}: the two facts are split so the reply can never
        // self-contradict, and warmup is explicit (no-tree / not-ready) rather than a silent false->true flip.
        return CmdAppAuthored();
    }
    if (line.rfind(L"property.get|", 0) == 0) {
        // property.get|<handle> -> curated live properties as JSON (UI-thread read).
        return CmdElementProps(Split(line, L'|'));
    }
    if (line.rfind(L"layout.get|", 0) == 0) {
        // layout.get|<handle> -> the element's measured layout facts as JSON (UI-thread live read).
        return CmdElementLayout(Split(line, L'|'));
    }
    if (line.rfind(L"binding.op|", 0) == 0) {
        // binding.op|<handle>|<op>|<prop> -> the managed agent's binding answer as JSON.
        const auto args = Split(line, L'|');
        unsigned long long wire = 0;
        if (args.size() != 4 || !DevToolsParseWireHandle(args[1], false, &wire)) return L"ERR bad-args";
        return CmdBindingOp(wire, args[2], args[3]);
    }
    if (line.rfind(L"element.source|", 0) == 0) {
        // element.source|<handle> -> the element's XAML file:line as JSON (UI-thread read; empty unless instrumented).
        return CmdElementSource(Split(line, L'|'));
    }
    if (line.rfind(L"resource.resolve|", 0) == 0) {
        // resource.resolve|<handle>|<prop> -> concrete resolved value as JSON (UI-thread read).
        return CmdResourceResolve(Split(line, L'|'));
    }
    return L"ERR unknown";
}

static std::wstring BridgeEscape(const std::wstring& s) {
    std::wstring o; o.reserve(s.size());
    for (wchar_t c : s) {
        if (c == L'\\') o += L"\\\\";
        else if (c == L'\n') o += L"\\n";
        else if (c == L'\r') o += L"\\r";
        else o.push_back(c);
    }
    return o;
}

static std::wstring BridgeErrToRpc(const std::wstring& idRaw, const std::wstring& reply) {
    std::wstring token = reply.substr(4);
    int code = DevToolsErr::InternalError;
    std::wstring msg = token, data;
    if (token == L"bad-args")            { code = DevToolsErr::InvalidParams;  msg = L"invalid params"; }
    else if (token.rfind(L"invalid-value|", 0) == 0 || token.rfind(L"property-write-failed|", 0) == 0 ||
             token.rfind(L"build-failed|", 0) == 0) {
        const auto separator = token.find(L'|');
        std::wstring decoded;
        if (TryUnescapeField(token.substr(separator + 1), decoded)) msg = std::move(decoded);
        else msg = token.substr(separator + 1);
        token.resize(separator);
        code = token == L"invalid-value" ? DevToolsErr::InvalidParams : DevToolsErr::InternalError;
    }
    else if (token == L"not-found")      { code = DevToolsErr::NotFound;       msg = L"handle or name not found"; }
    else if (token == L"no-tree")        { code = DevToolsErr::NoTree;         msg = L"no live visual tree"; }
    else if (token == L"no-hit")         { code = DevToolsErr::NoHit;          msg = L"pick hit nothing"; }
    else if (token.rfind(L"outside-surface", 0) == 0) {
        std::wstring active, at;
        const size_t p1 = token.find(L'|');
        if (p1 != std::wstring::npos) {
            const size_t p2 = token.find(L'|', p1 + 1);
            active = token.substr(p1 + 1, (p2 == std::wstring::npos) ? std::wstring::npos : p2 - p1 - 1);
            if (p2 != std::wstring::npos) at = token.substr(p2 + 1);
        }
        code = DevToolsErr::OutsideSurface;
        msg = at.empty() ? L"that point is not in the surface being inspected"
                         : (L"that point is in \u201C" + at + L"\u201D");
        if (!active.empty()) msg += L"; DevTools is inspecting \u201C" + active + L"\u201D";
        data = L"\"activeSurface\":\"" + DevToolsJsonEscape(active) + L"\"," +
               L"\"pointSurface\":\"" + DevToolsJsonEscape(at) + L"\"";
        token = L"outside-surface";
    }
    else if (token == L"stale-handle")   { code = DevToolsErr::StaleHandle;    msg = L"handle slot reused since minted"; }
    else if (token == L"refused-unsafe") { code = DevToolsErr::RefusedUnsafe;  msg = L"refused (unsafe or mutation disabled)"; }
    else if (token == L"unauthorized")   { code = DevToolsErr::Unauthorized;   msg = L"unauthorized"; }
    else if (token == L"not-ready")      { code = DevToolsErr::NotReady;       msg = L"not ready"; }
    // "The app reports no XAML source information" is no longer a refusal on the authored paths -- those
    // carry it as state so a client can tell it apart from "still classifying". The token stays mapped
    // because it is still the honest answer for a verb with nowhere to put that state.
    else if (token == L"no-source-info") { code = DevToolsErr::SourceUnavailable;
                                           msg = L"the app reports no XAML source information"; }
    // The owner token resolved on the pipe thread but its session had disconnected by the time the queued UI
    // mutation ran. Same answer as an owner that never resolved at all -- the client cannot act differently on
    // the two, and both mean "nothing was established".
    else if (token == L"unknown-owner")  { code = DevToolsErr::InvalidParams;
                                           msg = L"owner does not name a live negotiated connection"; }
    else if (token == L"not-on-dispatcher") { code = DevToolsErr::NotOnDispatcher; msg = L"no app dispatcher"; }
    else if (token == L"unknown")        { code = DevToolsErr::MethodNotFound; msg = L"unknown method"; }
    else if (token.rfind(L"0x", 0) == 0) { code = DevToolsErr::InternalError;  msg = L"internal error";
                                           data = L"\"hresult\":\"" + token + L"\""; token = L"internal"; }
    return DevToolsRpcError(idRaw, code, msg, token, data);
}

static std::wstring BridgeJson(const std::wstring& idRaw, const std::wstring& line,
                               const std::wstring& overlayEventOrigin = L"null",
                               unsigned long long overlayOwnerConn = 0) {
    std::wstring reply = HandleCommand(line, overlayEventOrigin, overlayOwnerConn);
    if (reply.rfind(L"ERR ", 0) == 0) return BridgeErrToRpc(idRaw, reply);
    return DevToolsRpcResult(idRaw, reply);
}

static std::wstring BridgeOk(const std::wstring& idRaw, const std::wstring& line,
                             const std::wstring& resultJson = L"null",
                             const std::wstring& overlayEventOrigin = L"null",
                             unsigned long long overlayOwnerConn = 0) {
    std::wstring reply = HandleCommand(line, overlayEventOrigin, overlayOwnerConn);
    if (reply.rfind(L"ERR ", 0) == 0) return BridgeErrToRpc(idRaw, reply);
    return DevToolsRpcResult(idRaw, resultJson);
}

// Resolve the `owner` param a state-mutating request carries into the connection id that owns whatever it
// establishes. An ABSENT owner is legal and yields 0: the CLI's one-shot connections establish nothing ownable, which
// is exactly the pre-ownership behaviour.
static bool ResolveOwnerParam(const DevToolsJson& p, const std::wstring& idRaw,
                              unsigned long long* outOwner, std::wstring* error)
{
    *outOwner = 0;
    const DevToolsJson* owner = p.Find(L"owner");
    if (!owner) return true;
    if (owner->type != DevToolsJsonType::String) {
        *error = DevToolsRpcError(idRaw, DevToolsErr::InvalidParams,
                             L"owner must be the string token DevTools.negotiate returned", L"bad-args");
        return false;
    }
    if (owner->str.empty()) return true;
    const unsigned long long resolved = DevToolsEvents_ResolveOwnerToken(owner->str);
    if (resolved == 0) {
        *error = DevToolsRpcError(idRaw, DevToolsErr::InvalidParams,
                             L"owner does not name a live negotiated connection", L"unknown-owner");
        return false;
    }
    *outOwner = resolved;
    return true;
}

static std::vector<std::wstring> SplitCh(const std::wstring& s, wchar_t d) {
    std::vector<std::wstring> out; size_t start = 0, pos;
    while ((pos = s.find(d, start)) != std::wstring::npos) { out.push_back(s.substr(start, pos - start)); start = pos + 1; }
    out.push_back(s.substr(start));
    return out;
}
