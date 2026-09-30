// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsTap.cpp; included into that translation unit, not compiled on its own.

static std::wstring HandleRpc(DevToolsConn* conn, const DevToolsRpcRequest& req) {
    const std::wstring& m = req.method;
    const DevToolsJson& p = req.params;
    const std::wstring& id = req.idRaw;

    const DevToolsProtocolMethod* method = DevToolsProtocolFindMethod(m);
    if (!method) {
        if (m == L"DevTools.cancel") {
            // Cancellation is reserved but not a capability: FIFO dispatch means it cannot overtake
            // the bounded operation it would target.
            return DevToolsRpcError(id, DevToolsErr::MethodNotFound,
                               L"DevTools.cancel not supported (operations are bounded and FIFO)", L"unsupported");
        }
        if ((m.size() > 7 && m.compare(m.size() - 7, 7, L".enable") == 0) ||
            (m.size() > 8 && m.compare(m.size() - 8, 8, L".disable") == 0)) {
            std::wstring dom = m.substr(0, m.find_last_of(L'.'));
            return DevToolsRpcError(id, DevToolsErr::MethodNotFound, L"domain has no events: " + dom, L"no-events");
        }
        return DevToolsRpcError(id, DevToolsErr::MethodNotFound, L"unknown method: " + m, L"unknown");
    }
    const DevToolsAccess posture = DevToolsTrust_CurrentPosture();
    if (!DevToolsAccess_Satisfies(posture, method->access)) {
        return DevToolsRpcError(id, DevToolsErr::RefusedUnsafe, L"refused: this session's posture does not permit " + m,
                           L"refused-unsafe",
                           L"\"requiredAccess\":\"" + std::wstring(DevToolsAccessToken(method->access)) +
                           L"\",\"posture\":\"" + DevToolsAccessToken(posture) + L"\"");
    }

    if (p.IsObject()) {
        for (const auto& member : p.obj) {
            if (DevToolsProtocolParamDeclared(method->paramsType, member.first)) continue;
            // The offending name is echoed so a client can fix it, but only a bounded prefix: it is
            // attacker-chosen text and the reply must not become an amplifier for it. Escaping is
            // DevToolsRpcError's job for the message and DevToolsJsonEscape's here for the data member.
            constexpr size_t kMaxEchoedParam = 64;
            std::wstring shown = member.first.substr(0, kMaxEchoedParam);
            if (member.first.size() > kMaxEchoedParam) shown += L"...";
            return DevToolsRpcError(id, DevToolsErr::InvalidParams,
                               L"unknown parameter for " + m + L": " + shown, L"bad-args",
                               L"\"parameter\":\"" + DevToolsJsonEscape(shown) + L"\"");
        }
    }

    std::shared_ptr<TapWindowScope> windowScope;
    const auto window = p.Find(kDevToolsReservedParamWindow);
    const auto root = p.Find(kDevToolsReservedParamRoot);
    if (window || root) {
        unsigned long long value = 0, rootWire = 0;
        if (window && (window->type != DevToolsJsonType::String ||
            !DevToolsParseWireHandle(window->str, false, &value) || value > UINTPTR_MAX ||
            !WindowOwnedByProcess(reinterpret_cast<HWND>(static_cast<uintptr_t>(value))))) {
            return DevToolsRpcError(id, DevToolsErr::InvalidParams,
                L"window must name a live HWND owned by this process", L"invalid-window");
        }
        if (root && (root->type != DevToolsJsonType::String ||
            !DevToolsParseWireHandle(root->str, false, &rootWire)))
            return DevToolsRpcError(id, DevToolsErr::InvalidParams,
                L"root must name a live opaque visual-tree handle", L"invalid-root");
        if (root && m == L"Selection.arm")
            return DevToolsRpcError(id, DevToolsErr::InvalidParams,
                L"Interactive picking cannot retain a subtree constraint. Use window-scoped arming or a scoped coordinate pick.",
                L"root-scope-unsupported");
        const bool supported = m == L"DevTools.negotiate" || m == L"DevTools.ping" ||
            m == L"VisualTree.enumerate" || m == L"VisualTree.find" || m == L"VisualTree.query" ||
            m == L"VisualTree.getPreviews" || m == L"VisualTree.getAppAuthored" ||
            m == L"Property.get" || m == L"Property.setQuery" || m == L"Layout.get" ||
            m == L"Source.get" || m == L"Resource.resolve" || m == L"Binding.walk" ||
            m == L"Binding.diagnose" || m == L"HotReload.setProperty" || m == L"HotReload.clearProperty" ||
            m == L"Surface.list" || m == L"Surface.setActive" || m == L"Overlay.highlight" ||
            m == L"Selection.select" || m == L"Internal.pick" || m == L"Selection.arm";
        if (!supported) return DevToolsRpcError(id, DevToolsErr::InvalidParams,
            L"This operation cannot be constrained to a window; no process-wide operation was performed.",
            L"window-scope-unsupported");
        if (m != L"DevTools.negotiate" || root) {
            windowScope = std::make_shared<TapWindowScope>();
            windowScope->hwnd = reinterpret_cast<HWND>(static_cast<uintptr_t>(value));
            windowScope->rootWire = rootWire;
            const HRESULT captured = RunOnUiThread([windowScope]() { return CaptureWindowScope_ui(*windowScope); });
            if (FAILED(captured)) return DevToolsRpcError(id, DevToolsErr::InvalidParams,
                L"Could not establish the selected window/root's live XAML scope; refresh the target and retry.",
                L"window-scope-unavailable");
        }
    }
    TapWindowScopeGuard windowGuard(windowScope);

    // Every state-mutating verb below names the SESSION whose state it establishes. Resolving it once here --
    // before dispatch, after the access gate -- means no handler can forget to validate it, and a stale or
    // forged token is refused for all of them in one place.
    unsigned long long ownerConn = 0;
    {
        std::wstring ownerError;
        if (!ResolveOwnerParam(p, id, &ownerConn, &ownerError)) return ownerError;
    }
    const std::wstring eventOrigin = OverlayEventOrigin(conn, ownerConn);

    if (m == L"DevTools.negotiate") {
        // Negotiating is also what mints this connection's owner token: a client that has not handshaked has no
        // session, so it has nothing to attribute process-global state to.
        std::wstring capabilities = DevToolsProtocolCapabilitiesJson(
            GetCurrentProcessId(), posture, conn ? DevToolsEvents_ConnId(conn) : 0,
            DevToolsEvents_MarkNegotiated(conn));
        capabilities.pop_back();
        capabilities += L",\"guestComments\":" + DevToolsOverlay_GuestCommentsJson();
        if (const auto error = g_guestInitializationError.load()) {
            capabilities += L",\"guestInitializationError\":\"" + std::wstring(error) + L"\"";
        }
        capabilities += L"}";
        return DevToolsRpcResult(id, capabilities);
    }
    if (m == L"DevTools.ping") {
        if (g_windowScope)
            return DevToolsRpcResult(id, L"{\"count\":" + std::to_wstring(g_windowScope->members.size()) + L"}");
        std::wstring reply = HandleCommand(L"ping");
        std::wstring count = reply.rfind(L"pong ", 0) == 0 ? reply.substr(5) : L"-1";
        return DevToolsRpcResult(id, L"{\"count\":" + count + L"}");
    }
    if (m == L"DevTools.releaseOwnedState") {
        // Connection lifecycle rather than a domain operation: a client calls this when it switches targets or shuts
        // down cleanly, and the tap calls the same code for it on disconnect. The caller must name ITS OWN session
        // with the token DevTools.negotiate gave it.
        if (ownerConn == 0) {
            return DevToolsRpcError(id, DevToolsErr::InvalidParams,
                               L"owner is required: pass the token DevTools.negotiate returned for this session",
                               L"unknown-owner");
        }
        auto outcome = std::make_shared<DevToolsOwnedReleaseOutcome>();
        HRESULT postHr = PostRpcToUi(conn, id, [id, ownerConn, eventOrigin, outcome]() {
            DevToolsOverlayEventOriginScope originScope(eventOrigin);
            *outcome = ApplyOwnedRelease_ui(ownerConn);
            return DevToolsRpcResult(id, DevToolsBatch::BuildReleaseJson(*outcome));
        });
        if (SUCCEEDED(postHr)) return {};
        return DevToolsRpcError(id, DevToolsErr::NotOnDispatcher, L"could not enqueue the owned-state release",
                           L"not-on-dispatcher", L"\"hresult\":\"" + HrStr(postHr) + L"\"");
    }

    if (m == L"VisualTree.query" || m == L"Property.setQuery")
        return HandleQuery(id, p, m == L"Property.setQuery");
    if (m == L"VisualTree.enumerate") {
        std::wstring root = p.GetString(L"rootHandle", L"");
        const DevToolsJson* authoredParam = p.Find(L"authored");
        if (authoredParam && authoredParam->type != DevToolsJsonType::Bool) {
            return DevToolsRpcError(id, DevToolsErr::InvalidParams, L"authored must be a boolean", L"bad-args");
        }
        const bool authored = authoredParam && authoredParam->b;
        std::wstring line = L"visualtree.enumerate";
        // Positional bridge line: every earlier field has to be present once a later one is, so `authored`
        // with no explicit depth still lands in slot 3 rather than being read as the depth.
        if (!root.empty() || p.HasKey(L"depth") || authored) line += L"|" + root;
        if (p.HasKey(L"depth") || authored) line += L"|" + std::to_wstring(p.GetInt(L"depth", -1));
        if (authored) line += L"|1";
        return BridgeJson(id, line);
    }
    if (m == L"VisualTree.find") {
        const DevToolsJson* query = p.Find(L"query");
        const DevToolsJson* authored = p.Find(L"appAuthoredOnly");
        if (!query || query->type != DevToolsJsonType::String ||
            (authored && authored->type != DevToolsJsonType::Bool)) {
            return DevToolsRpcError(id, DevToolsErr::InvalidParams,
                               L"query must be a string and appAuthoredOnly must be a boolean", L"bad-args");
        }
        std::wstring reply = CmdFind(query->str, authored ? authored->b : false);
        if (reply.rfind(L"ERR ", 0) == 0) return BridgeErrToRpc(id, reply);
        return DevToolsRpcResult(id, reply);
    }
    if (m == L"VisualTree.getAppAuthored") return BridgeJson(id, L"visualtree.appauthored");
    if (m == L"VisualTree.getPreviews") {
        const DevToolsJson* handles = p.Find(L"handles");
        if (!handles || handles->type != DevToolsJsonType::Array || handles->arr.empty()) {
            return DevToolsRpcError(id, DevToolsErr::InvalidParams,
                               L"handles must be a non-empty array of canonical wire handle strings",
                               L"bad-args");
        }
        std::wstring list;
        for (const DevToolsJson& handle : handles->arr) {
            if (handle.type != DevToolsJsonType::String) {
                return DevToolsRpcError(id, DevToolsErr::InvalidParams,
                                   L"handles must contain only strings", L"bad-args");
            }
            if (!list.empty()) list += L',';
            list += handle.str;
        }
        return BridgeJson(id, L"visualtree.previews|" + list);
    }

    if (m == L"Property.get")      return BridgeJson(id, L"property.get|" + p.GetString(L"handle"));
    if (m == L"Layout.get")        return BridgeJson(id, L"layout.get|" + p.GetString(L"handle"));
    if (m == L"Source.get")        return BridgeJson(id, L"element.source|" + p.GetString(L"handle"));
    if (m == L"Resource.resolve")  return BridgeJson(id, L"resource.resolve|" + p.GetString(L"handle") + L"|" + p.GetString(L"prop"));

    if (m == L"Binding.walk") {
        const std::wstring handle = p.GetString(L"handle");
        const std::wstring prop   = p.GetString(L"prop");
        unsigned long long wire = 0;
        if (!DevToolsParseWireHandle(handle, false, &wire) || prop.empty())
            return DevToolsRpcError(id, DevToolsErr::InvalidParams,
                               L"handle must be a canonical non-zero decimal string and prop is required",
                               L"bad-args");
        std::wstring reply = CmdBindingWalk(wire, prop);
        if (reply.rfind(L"ERR ", 0) == 0) return BridgeErrToRpc(id, reply);
        return DevToolsRpcResult(id, reply);
    }

    if (m == L"Binding.diagnose" || m == L"Binding.capture" ||
        m == L"Binding.restore"  || m == L"Binding.clearValue") {
        const std::wstring handle = p.GetString(L"handle");
        const std::wstring prop   = p.GetString(L"prop");
        unsigned long long wire = 0;
        if (!DevToolsParseWireHandle(handle, false, &wire) || prop.empty())
            return DevToolsRpcError(id, DevToolsErr::InvalidParams,
                               L"handle must be a canonical non-zero decimal string and prop is required",
                               L"bad-args");
        const wchar_t* op = m == L"Binding.diagnose" ? L"diagnose"
                          : m == L"Binding.capture"  ? L"capture"
                          : m == L"Binding.restore"  ? (p.GetBool(L"confirmOwnerRebind", false) ? L"restoreconfirmed" : L"restore")
                                                     : L"clear";
        std::wstring reply = CmdBindingOp(wire, op, prop);
        if (reply.rfind(L"ERR ", 0) == 0) return BridgeErrToRpc(id, reply);
        return DevToolsRpcResult(id, reply);
    }

    // Mutating verbs carry the SESSION that owns whatever they establish (the validated `owner` token) into the
    // specific UI-thread closure that changes state, and that same session is the events' `origin`.
    // Reads use no origin because there is no event to attribute.
    if (m == L"Overlay.show")
        return BridgeOk(id, L"overlay.show", L"null", eventOrigin, ownerConn);
    if (m == L"Overlay.hide")
        return BridgeOk(id, L"overlay.hide", L"null", eventOrigin, ownerConn);
    if (m == L"Overlay.highlight") {
        return BridgeOk(id, L"highlight|" + p.GetString(L"handle", L"0"),
                        L"null", eventOrigin, ownerConn);
    }
    if (m == L"Overlay.layout") {
        const std::wstring set = p.GetString(L"set", L"");
        if (set.empty())       return BridgeJson(id, L"overlay.layout");
        if (set == L"on" || set == L"off") {
            return BridgeJson(id, L"overlay.layout|" + set, eventOrigin, ownerConn);
        }
        return DevToolsRpcError(id, DevToolsErr::InvalidParams, L"Overlay.layout: set must be \"on\" or \"off\"", L"bad-args");
    }
    if (m == L"Overlay.getState") return BridgeJson(id, L"overlay.getstate");

    if (m == L"Surface.list") return BridgeJson(id, L"surface.list");
    if (m == L"Surface.setActive") {
        const std::wstring name = p.GetString(L"name", L"");
        if (name.empty())
            return DevToolsRpcError(id, DevToolsErr::InvalidParams, L"Surface.setActive: name is required", L"bad-args");
        return BridgeJson(id, L"surface.setactive|" + name, eventOrigin, ownerConn);
    }

    // These operations own WinUI objects and must run on the app dispatcher. Unlike the synchronous bridge
    // used by bounded live-tree reads, their response is completed asynchronously through DevToolsEvents so the
    // per-connection pipe reader never waits for the UI thread.
    if (m == L"Window.open" || m == L"Window.close" || m == L"Window.getState") {
        const bool respond = req.hasId;
        HRESULT postHr = PostRpcToUi(conn, id, [m, id, respond, eventOrigin, ownerConn]() {
            // A session that disconnected in between must establish nothing -- its release has already run, so a
            // window (or a rail) recorded under it now could never be handed back. Reads are exempt: getState claims
            // nothing, so a dead owner simply means "attribute to nobody", which is what it already is.
            if (m != L"Window.getState" && ownerConn != 0 && !DevToolsEvents_IsOwnerLive(ownerConn)) {
                if (!respond) return std::wstring();
                return DevToolsRpcError(id, DevToolsErr::InvalidParams,
                                   L"owner does not name a live negotiated connection", L"unknown-owner");
            }
            DevToolsOverlayEventOriginScope originScope(eventOrigin);
            DevToolsOverlayOwnerScope ownerScope(ownerConn);
            const bool wasOpen = DevToolsWindow_IsOpen();
            HRESULT hr = S_OK;
            std::wstring errorMessage;
            if (m == L"Window.open") {
                // A protocol client may open the window from a headless attach, so initialize that substrate on this
                // same UI turn before the window is reported live. The helper is idempotent, never waits on the pipe,
                // and attributes the rail the build turns on to this validated session rather than to nobody.
                const wchar_t* overlayError = EnsureOverlayForOwner_ui(ownerConn, &errorMessage);
                if (overlayError) {
                    if (!respond) return std::wstring();
                    return DevToolsRpcError(id, DevToolsErr::InternalError,
                                       L"DevTools window support overlay did not open: " +
                                           (errorMessage.empty() ? std::wstring(overlayError) : errorMessage),
                                       L"window-open-failed",
                                       L"\"reason\":\"" + DevToolsJsonEscape(overlayError) + L"\",\"open\":false");
                }
                hr = OpenInprocWindow(&errorMessage);
            }
            else if (m == L"Window.close") hr = DevToolsWindow_Close();

            const bool open = DevToolsWindow_IsOpen();
            if (m == L"Window.getState" || ownerConn == 0) {
                g_ownedState.Reconcile(DevToolsStateAxis::Window, open ? 1ull : 0ull);
            } else {
                g_ownedState.RecordSet(DevToolsStateAxis::Window, ownerConn,
                                       wasOpen ? 1ull : 0ull, open ? 1ull : 0ull);
            }
            const bool expected = m != L"Window.close";
            if (m != L"Window.getState" && (FAILED(hr) || open != expected)) {
                if (!respond) return std::wstring();
                const std::wstring action = m == L"Window.open" ? L"open" : L"close";
                return DevToolsRpcError(id, DevToolsErr::InternalError,
                                   L"DevTools window did not " + action +
                                       (errorMessage.empty() ? L"" : L": " + errorMessage),
                                   L"window-" + action + L"-failed",
                                   L"\"hresult\":\"" + HrStr(hr) + L"\",\"open\":" + (open ? L"true" : L"false"));
            }
            if (!respond) return std::wstring();
            return DevToolsRpcResult(id, std::wstring(L"{\"open\":") + (open ? L"true" : L"false") +
                                    L",\"owner\":\"" + g_ownedState.OwnerToken(DevToolsStateAxis::Window) + L"\"" +
                                    L",\"revision\":" + std::to_wstring(g_ownedState.Revision()) + L"}");
        });
        if (SUCCEEDED(postHr)) return {};
        return DevToolsRpcError(id, DevToolsErr::NotOnDispatcher, L"could not enqueue DevTools window operation",
                           L"not-on-dispatcher", L"\"hresult\":\"" + HrStr(postHr) + L"\"");
    }

    if (m == L"Selection.poll") {
        std::wstring reply = HandleCommand(L"selection.poll");
        auto parts = SplitCh(reply, L'|');
        std::wstring gen = parts.size() > 1 ? parts[1] : L"0";
        std::wstring h   = parts.size() > 2 ? parts[2] : L"0";
        return DevToolsRpcResult(id, L"{\"generation\":" + gen + L",\"handle\":\"" + h + L"\"}");
    }
    if (m == L"Selection.arm")
        return BridgeOk(id, L"selection.arm", L"null", eventOrigin, ownerConn);
    if (m == L"Selection.disarm")
        return BridgeOk(id, L"selection.disarm", L"null", eventOrigin, ownerConn);
    if (m == L"Selection.completePick")
        return BridgeJson(id, L"selection.completepick", eventOrigin, ownerConn);
    if (m == L"Selection.select") {
        // The reply carries the CURRENT PUBLISHED selection (generation + handle), read back after the write, plus
        // `selected`: whether this call actually moved it.
        const std::wstring handle = p.GetString(L"handle", L"");
        unsigned long long wire = 0;
        if (!DevToolsParseWireHandle(handle, /*allowZero*/ true, &wire)) {
            return DevToolsRpcError(id, DevToolsErr::InvalidParams,
                               L"handle must be a canonical decimal wire handle (\"0\" deselects)", L"bad-args");
        }
        std::wstring reply = HandleCommand(L"selection.select|" + std::to_wstring(wire),
                                           eventOrigin, ownerConn);
        if (reply.rfind(L"ERR ", 0) == 0) return BridgeErrToRpc(id, reply);
        auto parts = SplitCh(reply, L'|');
        const std::wstring gen = parts.size() > 1 ? parts[1] : L"0";
        const std::wstring settled = parts.size() > 2 ? parts[2] : L"0";
        const bool selected = parts.size() > 3 && parts[3] == L"1";
        return DevToolsRpcResult(id, std::wstring(L"{\"selected\":") + (selected ? L"true" : L"false") +
                                L",\"generation\":" + gen + L",\"handle\":\"" + settled + L"\"}");
    }
    if (m == L"Selection.getState") return BridgeJson(id, L"selection.getstate");

    if (m == L"HotReload.setProperty") {
        // Addressing: `handle` (opaque wire handle) OR `name` (x:Name). The handle form was added because most
        // elements in a real app have no x:Name, so name-only addressing left the majority of the tree unwritable
        // over the wire.
        std::wstring name   = p.GetString(L"name");
        std::wstring handle = p.GetString(L"handle");
        std::wstring prop   = p.GetString(L"prop");
        std::wstring value  = p.GetString(L"value");
        if (handle.empty() && name.empty())
            return DevToolsRpcError(id, DevToolsErr::InvalidParams, L"either handle or name is required", L"bad-args");
        std::wstring target = handle.empty() ? name : (L"#" + handle);
        std::wstring line = L"hotreload.setprop|" + target + L"|" +
                            BridgeEscape(prop) + L"|" + BridgeEscape(p.GetString(L"type")) +
                            L"|" + BridgeEscape(value);
        std::wstring reply = HandleCommand(line);
        if (reply.rfind(L"ERR ", 0) == 0) return BridgeErrToRpc(id, reply);
        // Publish Property.changed with connection origin so a writer can ignore its own echo.
// Carry wire handle and name; enqueue without blocking. Coalesce only the same element/property,
// keeping different fields independent under backpressure.
        unsigned long long wire = 0;
        {
            AcquireSRWLockShared(&g_censusLock);
            if (!handle.empty()) {
                // Echo the caller's handle back only if it still resolves, so the event never carries a
                // handle that has already gone stale.
                InstanceHandle ih = 0; const wchar_t* e = nullptr;
                unsigned long long w = wcstoull(handle.c_str(), nullptr, 10);
                if (ResolveWire_nolock(w, &ih, &e)) { wire = w; if (name.empty()) name = NameOf_nolock(ih); }
            } else {
                auto it = g_byName.find(name);
                if (it != g_byName.end()) wire = PackWire_nolock(it->second);
            }
            ReleaseSRWLockShared(&g_censusLock);
        }
        std::wstring origin = conn ? (L"\"" + std::to_wstring(DevToolsEvents_ConnId(conn)) + L"\"") : std::wstring(L"null");
        std::wstring params = L"{\"handle\":\"" + std::to_wstring(wire) + L"\",\"name\":\"" + DevToolsJsonEscape(name) +
                              L"\",\"prop\":\"" + DevToolsJsonEscape(prop) + L"\",\"value\":\"" + DevToolsJsonEscape(value) + L"\"}";
        // Coalesce on the element's stable identity: the wire handle when we have one (so two clients editing
        // the same UNNAMED element still collapse to one keep-newest slot), else the name.
        std::wstring coalesceKey = wire ? std::to_wstring(wire) : name;
        DevToolsEvents_Broadcast(DevToolsDomain_Property, DevToolsRpcEvent(L"Property.changed", params, origin),
                            DevToolsCoalesce_KeepNewest, L"prop:" + coalesceKey + L"\x1f" + prop);
        return DevToolsRpcResult(id, L"null");
    }

    if (m == L"HotReload.clearProperty") {
        std::wstring name   = p.GetString(L"name");
        std::wstring handle = p.GetString(L"handle");
        std::wstring prop   = p.GetString(L"prop");
        if (handle.empty() && name.empty())
            return DevToolsRpcError(id, DevToolsErr::InvalidParams, L"either handle or name is required", L"bad-args");
        if (prop.empty())
            return DevToolsRpcError(id, DevToolsErr::InvalidParams, L"prop is required", L"bad-args");
        std::wstring target = handle.empty() ? name : (L"#" + handle);
        std::wstring reply = HandleCommand(L"hotreload.clearprop|" + target + L"|" + BridgeEscape(prop));
        if (reply.rfind(L"ERR ", 0) == 0) return BridgeErrToRpc(id, reply);

        // Announce it the same way a write is announced, and for the same reason: another client's pane is
        // showing the value this just deleted. `value` is empty because the cleared value is whatever the
        // element now inherits -- a re-read, not something this verb can state.
        unsigned long long wire = 0;
        {
            AcquireSRWLockShared(&g_censusLock);
            if (!handle.empty()) {
                InstanceHandle ih = 0; const wchar_t* e = nullptr;
                unsigned long long w = wcstoull(handle.c_str(), nullptr, 10);
                if (ResolveWire_nolock(w, &ih, &e)) { wire = w; if (name.empty()) name = NameOf_nolock(ih); }
            } else {
                auto it = g_byName.find(name);
                if (it != g_byName.end()) wire = PackWire_nolock(it->second);
            }
            ReleaseSRWLockShared(&g_censusLock);
        }
        std::wstring origin = conn ? (L"\"" + std::to_wstring(DevToolsEvents_ConnId(conn)) + L"\"") : std::wstring(L"null");
        std::wstring params = L"{\"handle\":\"" + std::to_wstring(wire) + L"\",\"name\":\"" + DevToolsJsonEscape(name) +
                              L"\",\"prop\":\"" + DevToolsJsonEscape(prop) + L"\",\"value\":\"\"}";
        std::wstring coalesceKey = wire ? std::to_wstring(wire) : name;
        DevToolsEvents_Broadcast(DevToolsDomain_Property, DevToolsRpcEvent(L"Property.changed", params, origin),
                            DevToolsCoalesce_KeepNewest, L"prop:" + coalesceKey + L"\x1f" + prop);
        return DevToolsRpcResult(id, L"null");
    }
    // Comment reads use the host-pushed snapshot; mutations call the trusted CLI, the single store
// writer. The bounded round trip runs on the connection thread, never the app UI thread, and
// timeout is failure. Inspector UI callers use the asynchronous path.
    constexpr DWORD kCliWaitMs = 60000;
    if (m == L"Comment.list") {
        std::vector<DevToolsWindowComment> items;
        TapComments(&items);
        size_t placed = 0;
        std::wstring rows;
        for (const auto& c : items) {
            InstanceHandle wire = c.anchor.empty() ? 0 : TapCommentAnchorWire(c.anchor.c_str());
            if (wire) ++placed;
            if (!rows.empty()) rows += L",";
            rows += L"{\"id\":\"" + DevToolsJsonEscape(c.id) + L"\",\"text\":\"" + DevToolsJsonEscape(c.text) +
                    L"\",\"status\":\"" + DevToolsJsonEscape(c.status) + L"\",\"element\":\"" + DevToolsJsonEscape(c.element) +
                    L"\",\"file\":\"" + DevToolsJsonEscape(c.file) + L"\",\"uri\":\"" + DevToolsJsonEscape(c.uri) +
                    L"\",\"anchor\":\"" + DevToolsJsonEscape(c.anchor) + L"\",\"line\":" + std::to_wstring(c.line) +
                    L",\"placed\":" + (wire ? L"true" : L"false") +
                    L",\"handle\":\"" + std::to_wstring((unsigned long long)wire) + L"\"}";
        }
        return DevToolsRpcResult(id, L"{\"total\":" + std::to_wstring(items.size()) +
                                L",\"placed\":" + std::to_wstring(placed) +
                                L",\"comments\":[" + rows + L"]}");
    }
    if (m == L"Comment.add" || m == L"Comment.resolve" || m == L"Comment.delete") {
        if (!CliExeRegistered())
            return DevToolsRpcError(id, DevToolsErr::CapabilityUnsupported,
                               L"no winapp CLI path registered; the store's only writer is unreachable", L"no-cli");

        if (m == L"Comment.resolve" || m == L"Comment.delete") {
            const bool deleting = (m == L"Comment.delete");
            std::wstring cid = p.GetString(L"id");
            if (cid.empty()) return DevToolsRpcError(id, DevToolsErr::InvalidParams, L"id is required", L"bad-args");
            // Refuse an id the tap has never been told about rather than shelling a CLI run that will do
            // nothing and reporting it as success.
            std::vector<DevToolsWindowComment> items;
            TapComments(&items);
            bool known = false;
            std::wstring revision;
            for (const auto& c : items) if (c.id == cid) { known = true; revision = c.revision; break; }
            if (!known) return DevToolsRpcError(id, DevToolsErr::NotFound, L"no open comment with that id", L"not-found");

            int rc = TapRunCli((deleting ? L"devtools comments delete " : L"devtools comments update --status resolved ") + TapQuoteArg(cid) +
                               L" --app " + std::to_wstring(GetCurrentProcessId()), kCliWaitMs, revision);
            if (rc == 0x57410001) return DevToolsRpcResult(id, L"{\"id\":\"" + DevToolsJsonEscape(cid) +
                L"\",\"persisted\":true,\"warning\":\"Saved on the host; comment markers could not refresh.\"}");
            if (rc != 0) return DevToolsRpcError(id, DevToolsErr::InternalError,
                                            deleting ? L"winapp devtools comments delete failed"
                                                     : L"winapp devtools comments update failed",
                                            L"cli-failed", L"\"exitCode\":" + std::to_wstring(rc));
            if (!deleting) return DevToolsRpcResult(id, L"{\"id\":\"" + DevToolsJsonEscape(cid) + L"\",\"status\":\"resolved\"}");

            // Read the outcome back instead of asserting it from a zero exit code. The CLI re-pushes the
            // open set on its way out, so by here the tap's own view is the post-delete one: the id must be
            // gone, and the counts reported are the ones a following Comment.list will agree with.
            std::vector<DevToolsWindowComment> after;
            TapComments(&after);
            size_t placed = 0;
            bool stillThere = false;
            for (const auto& c : after) {
                if (c.id == cid) stillThere = true;
                if (!c.anchor.empty() && TapCommentAnchorWire(c.anchor.c_str())) ++placed;
            }
            if (stillThere)
                return DevToolsRpcError(id, DevToolsErr::InternalError, L"the CLI reported success but the comment is still listed",
                                   L"cli-failed", L"\"id\":\"" + DevToolsJsonEscape(cid) + L"\"");
            return DevToolsRpcResult(id, L"{\"id\":\"" + DevToolsJsonEscape(cid) + L"\",\"deleted\":true,\"total\":" +
                                    std::to_wstring(after.size()) + L",\"placed\":" + std::to_wstring(placed) + L"}");
        }

        std::wstring text = p.GetString(L"text");
        if (text.empty()) return DevToolsRpcError(id, DevToolsErr::InvalidParams, L"text is required", L"bad-args");
        // Same addressing as HotReload.setProperty: a wire handle for the elements that have no x:Name (most
        // of them), a name for the ones that do.
        std::wstring target = p.HasKey(L"handle") ? p.GetString(L"handle") : p.GetString(L"name");
        if (target.empty() || target == L"0")
            return DevToolsRpcError(id, DevToolsErr::InvalidParams, L"either handle or name is required", L"bad-args");
        std::wstring kind = p.GetString(L"kind");

        std::wstring cid = TapMintCommentId();
        std::wstring args = L"devtools comments add --from-element " + TapQuoteArg(target)
                          + L" --app " + std::to_wstring(GetCurrentProcessId())
                          + L" --id " + TapQuoteArg(cid)
                          + L" -t " + TapQuoteArg(text);
        if (!kind.empty()) args += L" --kind " + TapQuoteArg(kind);
        if (p.GetBool(L"confirmLikelySource", false)) args += L" --confirm-likely-source";
        int rc = TapRunCli(args, kCliWaitMs);
        if (rc == 0x57410001) return DevToolsRpcResult(id, L"{\"id\":\"" + DevToolsJsonEscape(cid) +
            L"\",\"persisted\":true,\"warning\":\"Saved on the host; comment markers could not refresh.\"}");
        if (rc != 0) return DevToolsRpcError(id, DevToolsErr::InternalError, L"winapp devtools comments add failed",
                                        L"cli-failed", L"\"exitCode\":" + std::to_wstring(rc));
        // The CLI re-pushes the open set on its way out, so by the time this returns the marker and the
        // window's list already carry the new comment. `placed` is read back rather than assumed.
        InstanceHandle wire = 0;
        {
            std::vector<DevToolsWindowComment> items;
            TapComments(&items);
            for (const auto& c : items) if (c.id == cid && !c.anchor.empty()) { wire = TapCommentAnchorWire(c.anchor.c_str()); break; }
        }
        return DevToolsRpcResult(id, L"{\"id\":\"" + DevToolsJsonEscape(cid) + L"\",\"status\":\"open\",\"placed\":" +
                                (wire ? L"true" : L"false") + L"}");
    }

    if (m == L"Internal.pick") {
        std::wstring line = L"select|" + std::to_wstring(p.GetInt(L"x", 0)) + L"," + std::to_wstring(p.GetInt(L"y", 0));
        std::wstring reply = HandleCommand(line);
        if (reply.rfind(L"ERR ", 0) == 0) return BridgeErrToRpc(id, reply);
        auto head = SplitCh(reply, L'|');
        std::wstring rest = head.size() > 1 ? head[1] : L"";
        auto f = SplitCh(rest, L'\t');
        std::wstring h = f.size() > 0 ? f[0] : L"0";
        std::wstring ty = f.size() > 1 ? f[1] : L"";
        std::wstring nm = f.size() > 2 ? f[2] : L"";
        auto b = f.size() > 3 ? SplitCh(f[3], L',') : std::vector<std::wstring>{L"0",L"0",L"0",L"0"};
        while (b.size() < 4) b.push_back(L"0");
        std::wstring out = L"{\"handle\":\"" + h + L"\",\"nodeType\":\"" + DevToolsJsonEscape(ty) +
                           L"\",\"name\":\"" + DevToolsJsonEscape(nm) + L"\",\"bounds\":{\"x\":" + b[0] +
                           L",\"y\":" + b[1] + L",\"w\":" + b[2] + L",\"h\":" + b[3] + L"}}";
        return DevToolsRpcResult(id, out);
    }
    if (m == L"Internal.overlayHost") {
        // Tests only: {"force":"popup"} places chrome built from now on in the fallback Popup host.
        const std::wstring force = p.GetString(L"force");
        auto result = std::make_shared<std::wstring>();
        const HRESULT hr = RunOnUiThread([force, result]() -> HRESULT {
            if (force == L"popup") DevToolsOverlay_ForcePopupHost(true);
            else if (force == L"auto") DevToolsOverlay_ForcePopupHost(false);
            *result = L"{\"host\":\"" + std::wstring(DevToolsOverlay_HostPath()) + L"\",\"forcePopup\":" +
                (DevToolsOverlay_IsPopupHostForced() ? L"true" : L"false") + L"}";
            return S_OK;
        });
        if (FAILED(hr)) return DevToolsRpcError(id, DevToolsErr::NotOnDispatcher, L"The app's UI thread did not answer.", L"ui-thread");
        return DevToolsRpcResult(id, *result);
    }
    if (m == L"Internal.sourceRoot") {
        // The CLI asks the APP where its sources are rather than guessing from its own cwd, so the comment store the
        // overlay writes through and the one the CLI pushes back are the same file — `winapp run samples/foo` from a
        // repo root would otherwise read a different.winapp than the composer wrote to.
        wchar_t root[1024]{};
        DWORD n = GetEnvironmentVariableW(L"WINAPP_DEVTOOLS_SOURCE_ROOT", root, (DWORD)_countof(root));
        std::wstring value = (n > 0 && n < _countof(root)) ? std::wstring(root, n) : std::wstring();
        return DevToolsRpcResult(id, L"{\"sourceRoot\":\"" + DevToolsJsonEscape(value) + L"\"}");
    }
    if (m == L"Internal.elementAnchor") {
        // hand the CLI the SAME rebuild-stable anchor the pin re-anchoring uses, so a comment it is about
        // to persist records how to find this element again on a later run. Keeping one anchor scheme is the
        // point: the store's durable half and the overlay's live half must not drift apart.
        unsigned long long wire = (unsigned long long)p.GetInt(L"handle", 0);
        if (!wire) return DevToolsRpcError(id, DevToolsErr::InvalidParams, L"handle is required", L"bad-args");
        InstanceHandle ih = 0; const wchar_t* err = nullptr; bool ok;
        AcquireSRWLockShared(&g_censusLock); ok = ResolveWire_nolock(wire, &ih, &err); ReleaseSRWLockShared(&g_censusLock);
        if (!ok || !ih) return DevToolsRpcError(id, DevToolsErr::NotFound, L"handle does not resolve",
                                           err ? err : L"stale-handle");
        std::wstring anchor;
        bool unique = false;
        CensusSnapshot census;
        {
            CensusSharedLock lock;
            census.type = g_type; census.children = g_children; census.parent = g_parent;
            for (const auto& entry : g_type) census.wire[entry.first] = PackWire_nolock(entry.first);
            SourceInfo_Snapshot(census.sourceUri);
        }
        // One bounded pass; unresolved competing instances remain weak, never presumed absent.
        const auto classification = ClassifyAuthored(census);
        const wchar_t* reason = nullptr;
        TapAnchorOf(ih, &anchor, &unique, &reason);
        if (classification.err) { unique = false; reason = classification.err; }
        else if (classification.truncated && unique) { unique = false; reason = L"incomplete-source-census"; }
        {
            CensusSharedLock lock;
            if (!ResolveWire_nolock(wire, &ih, &err))
                return DevToolsRpcError(id, DevToolsErr::NotFound, L"handle no longer resolves", err ? err : L"stale-handle");
        }
        return DevToolsRpcResult(id, L"{\"anchor\":\"" + DevToolsJsonEscape(anchor) +
            L"\",\"unique\":" + (unique ? L"true" : L"false") +
            L",\"uniquenessReason\":\"" + DevToolsJsonEscape(reason ? reason : L"unverified") + L"\"}");
    }
    if (m == L"Internal.setComments") {
        auto items = std::make_shared<std::vector<DevToolsWindowComment>>();
        if (const DevToolsJson* arr = p.Find(L"comments")) {
            if (arr->type == DevToolsJsonType::Array) {
                for (const auto& e : arr->arr) {
                    if (!e.IsObject()) continue;
                    DevToolsWindowComment it;
                    it.id      = e.GetString(L"id");
                    it.text    = e.GetString(L"text");
                    it.anchor  = e.GetString(L"anchor");
                    it.status  = e.GetString(L"status", L"open");
                    it.element = e.GetString(L"element");
                    it.file    = e.GetString(L"file");
                    it.uri     = e.GetString(L"uri");
                    it.line    = (unsigned int)e.GetInt(L"line", 0);
                    it.revision = e.GetString(L"revision");
                    if (!it.id.empty()) items->push_back(std::move(it));
                }
            }
        }

        // Store generation orders snapshots from concurrent CLIs; -1 is an unversioned (legacy) push.
        const long long generation = p.GetInt(L"generation", -1);
        auto placed = std::make_shared<size_t>(0);
        auto stale = std::make_shared<bool>(false);
        InstanceHandle root = RootVisual();
        HRESULT hr = RunOnUiThread([items, placed, stale, root, generation]() -> HRESULT {
            // Compared when applied, not when queued: a timed-out request can run after a newer one.
            if (generation >= 0 && generation < g_commentsGeneration) {
                *stale = true;
                return S_OK;
            }
            std::vector<DevToolsOverlayComment> view;
            view.reserve(items->size());
            for (const auto& it : *items) {
                view.push_back(DevToolsOverlayComment{ it.id.c_str(), it.text.c_str(), it.anchor.c_str(), it.revision.c_str() });
            }
            HRESULT h = DevToolsOverlay_SetComments(g_diag, root, view.empty() ? nullptr : view.data(), view.size(),
                placed.get(), generation >= 0);
            if (FAILED(h)) return h;
            {
                std::lock_guard<std::mutex> lock(g_commentsMutex);
                g_comments = *items;
            }
            if (generation >= 0) g_commentsGeneration = generation;
            DevToolsWindow_RefreshComments();
            return h;
        });
        if (FAILED(hr)) return DevToolsRpcError(id, DevToolsErr::InternalError, L"could not push comments", L"overlay-push-failed");
        return DevToolsRpcResult(id, L"{\"total\":" + std::to_wstring(items->size()) +
                                L",\"placed\":" + std::to_wstring(*placed) +
                                L",\"stale\":" + (*stale ? L"true" : L"false") + L"}");
    }
    if (m == L"Internal.perf") {
        // CLI-only back-channel (not advertised by DevTools.negotiate) so a measurement surface does not
        // become part of the public protocol contract before anyone has decided it should be.
        const std::wstring action = p.GetString(L"action");
        std::wstring reply = HandleCommand(L"perf|" + action);
        if (reply.rfind(L"ERR ", 0) == 0) return BridgeErrToRpc(id, reply);
        if (action == L"report") return DevToolsRpcResult(id, reply);
        return DevToolsRpcResult(id, L"null");
    }

    // Enable events before enumeration, then reconcile deltas by handle with that snapshot.
// Enable is idempotent and per connection. Request-only domains refuse subscription;
// Overlay is subscribable. Event state describes the process shared by clients.
    if (m == L"Focus.enable" || m == L"Focus.disable") {
        const bool enable = m == L"Focus.enable";
        const bool respond = req.hasId;
        HRESULT postHr = PostRpcToUi(conn, id, [conn, id, enable, respond, eventOrigin]() {
            const DevToolsDomainChange change = DevToolsEvents_SetDomain(conn, DevToolsDomain_Focus, enable);
            if (change == DevToolsDomain_ConnectionGone) return std::wstring();

            if (enable && change == DevToolsDomain_FirstEnabled) {
                // That is an ownable axis, and Focus.enable is a subscription that belongs to THIS connection for
                // exactly as long as this connection lives, so this connection is the honest owner of the rail it
                // caused.
                DevToolsOverlayEventOriginScope originScope(eventOrigin);
                const wchar_t* overlayError = EnsureOverlayForOwner_ui(DevToolsEvents_ConnId(conn));
                if (overlayError || !DevToolsOverlay_SetExternalFocusTracking(true)) {
                    DevToolsEvents_SetDomain(conn, DevToolsDomain_Focus, false);
                    if (DevToolsEvents_DomainSubscriberCount(DevToolsDomain_Focus) == 0)
                        DevToolsOverlay_SetExternalFocusTracking(false);
                    if (!respond) return std::wstring();
                    return DevToolsRpcError(id, DevToolsErr::InternalError,
                                       L"Focus tracking could not be activated", L"focus-enable-failed",
                                       L"\"reason\":\"" +
                                           DevToolsJsonEscape(overlayError ? overlayError : L"focus-hook-failed") + L"\"");
                }
                // A disconnect can unregister this connection while the UI callback is wiring the target hook.
                // Do not leave the external owner active if that removed the only subscriber.
                if (DevToolsEvents_DomainSubscriberCount(DevToolsDomain_Focus) == 0)
                    DevToolsOverlay_SetExternalFocusTracking(false);
            } else if (!enable && change == DevToolsDomain_LastDisabled) {
                DevToolsOverlay_SetExternalFocusTracking(false);
            }

            if (!respond) return std::wstring();
            return DevToolsRpcResult(id, enable ? L"{\"enabled\":true}" : L"{\"enabled\":false}");
        });
        if (SUCCEEDED(postHr)) return {};
        return DevToolsRpcError(id, DevToolsErr::NotOnDispatcher, L"could not enqueue Focus subscription operation",
                           L"not-on-dispatcher", L"\"hresult\":\"" + HrStr(postHr) + L"\"");
    }
    if (m == L"VisualTree.enable" || m == L"Property.enable" ||
        m == L"Selection.enable" || m == L"Overlay.enable") {
        std::wstring dom = m.substr(0, m.size() - 7);
        uint32_t bit = DevToolsEvents_DomainBit(dom);
        if (bit == 0) {
            return DevToolsRpcError(id, DevToolsErr::MethodNotFound, L"domain has no events: " + dom, L"no-events");
        }
        DevToolsEvents_SetDomain(conn, bit, true);
        return DevToolsRpcResult(id, L"{\"enabled\":true}");
    }
    if (m == L"VisualTree.disable" || m == L"Property.disable" ||
        m == L"Selection.disable" || m == L"Overlay.disable") {
        std::wstring dom = m.substr(0, m.size() - 8);
        uint32_t bit = DevToolsEvents_DomainBit(dom);
        if (bit == 0) {
            return DevToolsRpcError(id, DevToolsErr::MethodNotFound, L"domain has no events: " + dom, L"no-events");
        }
        DevToolsEvents_SetDomain(conn, bit, false);
        return DevToolsRpcResult(id, L"{\"enabled\":false}");
    }
    // Overlay.enable/disable are folded into this SAME plain bit-toggle block rather than special- cased like Focus
    // above: unlike Focus tracking (which starts/stops a real GotFocus hook and so needs the
    // FirstEnabled/LastDisabled activation edge), the toolbar/highlight/layout-adorner state Overlay. stateChanged
    // reports is ALWAYS tracked, regardless of subscriber count -- there is no separate subsystem to start or stop,
    // so a subscription here is pure delivery opt-in.

    // Invariant-violation guard, not the normal path: every name that clears the top-of-function registry lookup is
    // dispatched by one of the `if (m ==...)` blocks above (scripts/generate-devtools-schema.ps1's
    // dispatch-completeness check fails the build otherwise).
    return DevToolsRpcError(id, DevToolsErr::MethodNotFound, L"unknown method: " + m, L"unknown");
}

// Parse one NDJSON line and produce its JSON-RPC response line (empty for a notification, which gets no
// reply). All faults are contained here so a garbled/hostile line becomes a structured error, never a crash.
static std::wstring HandleRpcLine(DevToolsConn* conn, const std::wstring& line) {
    DevToolsRpcRequest req = DevToolsRpcParse(line);
    if (!req.parsed) return DevToolsRpcError(L"null", DevToolsErr::ParseError, L"invalid JSON", L"parse-error");
    if (!req.valid)  return DevToolsRpcError(req.hasId ? req.idRaw : L"null", DevToolsErr::InvalidRequest,
                                        L"not a valid JSON-RPC request", L"invalid-request");
    std::wstring resp = HandleRpc(conn, req);
    return req.hasId ? resp : std::wstring();
}

// Each persistent NDJSON connection has one reader and one FIFO response/event writer.
// Join the writer through DevToolsEvents_Unregister before closing the read-owned handle.
// DevToolsPipeFraming owns framing/teardown; this layer supplies trust, dispatch and owned-state release.
static void* PipeFraming_RegisterConn(HANDLE pipe, void*)
{
    return DevToolsEvents_Register(pipe);
}

static unsigned long long PipeFraming_ConnId(void* conn, void*)
{
    return conn ? DevToolsEvents_ConnId(static_cast<DevToolsConn*>(conn)) : 0;
}

static unsigned PipeFraming_UnregisterConn(void* conn, void*)
{
    return static_cast<unsigned>(DevToolsEvents_Unregister(static_cast<DevToolsConn*>(conn)));
}

static bool PipeFraming_VerifyClient(HANDLE pipe, void*)
{
    return DevToolsTrust_VerifyPipeClient(pipe);
}

static std::wstring PipeFraming_DispatchLine(void* conn, const std::wstring& line, void*)
{
    // HandleCommand is reachable only through the private in-process dispatcher bridge.
    return HandleRpcLine(static_cast<DevToolsConn*>(conn), line);
}

static std::wstring PipeFraming_DispatchFault(bool oom, void*)
{
    return oom
        ? DevToolsRpcError(L"null", DevToolsErr::InternalError, L"out of memory", L"internal", L"\"hresult\":\"0x8007000E\"")
        : DevToolsRpcError(L"null", DevToolsErr::InternalError, L"internal error", L"internal", L"\"hresult\":\"0x80004005\"");
}

static void PipeFraming_EnqueueResponse(void* conn, const std::wstring& resp, void*)
{
    DevToolsEvents_EnqueueResponse(static_cast<DevToolsConn*>(conn), resp);
}

static void PipeFraming_AfterUnregister(unsigned long long releasingConn, unsigned lastDisabled, void*)
{
    // For the client's SESSION -- its persistent negotiated connection -- that is everything it established through
    // its short-lived request pipes; for one of those request pipes it is nothing, which is exactly the point (a
    // request closing must not undo the UI mutation it just made).
    if (releasingConn != 0) {
        HRESULT releaseHr = PostToUi([releasingConn] {
            DevToolsOverlayEventOriginScope originScope(L"\"" + std::to_wstring(releasingConn) + L"\"");
            const DevToolsOwnedReleaseOutcome outcome = ApplyOwnedRelease_ui(releasingConn);
            Log(L"pipe: connection %llu released %zu owned axes (%zu retained) at revision %llu",
                releasingConn, outcome.released.size(), outcome.retained.size(), outcome.revision);
        });
        if (FAILED(releaseHr)) Log(L"pipe: could not enqueue owned-state release hr=0x%08X", releaseHr);
    }
    if ((lastDisabled & DevToolsDomain_Focus) != 0) {
        HRESULT cleanupHr = PostToUi([] {
            if (DevToolsEvents_DomainSubscriberCount(DevToolsDomain_Focus) == 0)
                DevToolsOverlay_SetExternalFocusTracking(false);
        });
        if (FAILED(cleanupHr)) Log(L"focus: could not enqueue final disconnect cleanup hr=0x%08X", cleanupHr);
    }
}
