// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsOverlay.cpp; included into that translation unit, not compiled on its own.

// The comment count lives on the toolbar's Comments button as a badge -- on the control that owns the state. It
// counts the whole registry: every comment the CLI pushed plus this session's, INCLUDING ones whose element is
// not realized right now.
// Badge counts the full registry, not only visible markers, so hidden-page comments still show backlog.
static void UpdateCommentBadge()
{
    if (!g_protoRoot) return;
    const size_t n = g_pins.size();
    if (IInspectable* txt = DevToolsSelFindName(g_protoRoot, L"DevToolsCommentBadgeText")) {
        const std::wstring s = (n > 99) ? std::wstring(L"99+") : std::to_wstring(n);
        void* tb = nullptr;
        if (SUCCEEDED(txt->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &tb)) && tb) {
            HSTRING h = nullptr;
            if (SUCCEEDED(WindowsCreateString(s.c_str(), (UINT32)s.size(), &h))) { DevToolsPutText(tb, h); WindowsDeleteString(h); }
            reinterpret_cast<IUnknown*>(tb)->Release();
        }
        txt->Release();
    }
    if (IInspectable* badge = DevToolsSelFindName(g_protoRoot, L"DevToolsCommentBadge")) {
        void* ui = nullptr;
        if (SUCCEEDED(badge->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) && ui) {
            DevToolsPutVisibility(ui, n > 0 ? 0 : 1);
            reinterpret_cast<IUnknown*>(ui)->Release();
        }
        badge->Release();
    }
    DevToolsOverlayLog(L"overlay.comments badge count=%zu", n);
    UpdateChipLabel();
}

static bool CommentsShown() { return g_commentsShown; }
static size_t CommentCount() { return g_pins.size(); }

// Two states authored in toolbar.xaml (a plain glyph and an accent chip carrying the same glyph); this swaps
// their Visibility. The announcement runs before the rail parts are touched, because a missing toolbar fragment
// must not silence the other surfaces.
static void SetPickButtonOn(bool on)
{
    if (g_pickModeSink) g_pickModeSink(on);
    DevToolsOverlayLog(L"overlay.pickmode button state=%s", on ? L"ON" : L"OFF");
    if (!g_protoRoot) return;
    auto setVis = [](const wchar_t* name, bool visible) {
        if (IInspectable* el = DevToolsSelFindName(g_protoRoot, name)) {
            void* ui = nullptr;
            if (SUCCEEDED(el->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) && ui) {
                DevToolsPutVisibility(ui, visible ? 0 : 1);
                reinterpret_cast<IUnknown*>(ui)->Release();
            }
            el->Release();
        }
    };
    setVis(L"DevToolsPickOn", on);
    setVis(L"DevToolsPickOff", !on);
    UpdateChipLabel();
}

// ONE rule for comment-marker visibility: the markers are on screen exactly when this flag is on.
static void SetCommentsShown(bool on)
{
    g_commentsShown = on;
    if (on) DrawAllPins(); else ClearAllPinsUi();
    UpdateCommentBadge();
    DevToolsOverlayLog(L"overlay.comments %s (pins=%zu)", on ? L"show" : L"hide", g_pins.size());
}

// Detach one pin's marker from the Canvas and release it (the registry entry itself is the caller's business).
static void DropPinUi(PinnedComment& p)
{
    if (p.ui && g_canvasChildren) {
        unsigned int idx = 0; unsigned char has = 0;
        if (SUCCEEDED(DevToolsVecIndexOf(g_canvasChildren, p.ui, &idx, &has)) && has) DevToolsVecRemoveAt(g_canvasChildren, idx);
    }
    if (p.ui) { p.ui->Release(); p.ui = nullptr; }
}

// the comment behind this marker was deleted, so the marker and its registry entry go with it.
// Delete markers by comment id because store-seeded pins may have stale or unresolved handles.
static void RemovePinById(const std::wstring& id)
{
    if (id.empty()) return;
    for (size_t i = g_pins.size(); i-- > 0;) {
        if (g_pins[i].id != id) continue;
        DropPinUi(g_pins[i]);
        g_pins.erase(g_pins.begin() + (ptrdiff_t)i);
    }
    UpdateCommentBadge();
}

// take the open comment set the CLI read out of the store and make it the registry, so the toolbar toggle
// reveals every persisted comment for this app instead of only the ones typed since the overlay attached.
// Placement is best-effort BY DESIGN: an anchor that does not resolve means the element is not realized right
// now.
// Pushed store comments become the registry; unresolved anchors stay counted but markerless.
HRESULT DevToolsOverlay_SetComments(IXamlDiagnostics* diag, InstanceHandle root,
                               const DevToolsOverlayComment* items, size_t count, size_t* outPlaced, bool authoritative)
{
    if (outPlaced) *outPlaced = 0;
    if (count && !items) return E_INVALIDARG;
    WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::OverlaySetComments);

    g_commentSnapshotAuthoritative = authoritative;
    size_t placed = 0;
    for (size_t i = 0; i < count; ++i) {
        const std::wstring id     = items[i].id     ? items[i].id     : L"";
        const std::wstring text   = items[i].text   ? items[i].text   : L"";
        const std::wstring anchor = items[i].anchor ? items[i].anchor : L"";
        if (id.empty()) continue;

        InstanceHandle h = 0;
        RECT b{};
        if (!anchor.empty() && g_resolveAnchor) h = g_resolveAnchor(anchor.c_str());
        bool live = h && diag && root && TryElementRootBounds(diag, root, h, &b);
        if (!live) h = 0;

        PinnedComment* existing = nullptr;
        for (auto& p : g_pins) if (p.id == id) { existing = &p; break; }

        if (existing) {
            const bool moved = live && existing->handle != h;
            existing->text = text;
            existing->revision = items[i].revision ? items[i].revision : L"";
            if (!anchor.empty()) existing->name = anchor;
            existing->seeded = true;
            if (live && (moved || existing->ui == nullptr)) {
                DropPinUi(*existing);
                existing->handle = h;
                existing->bounds = b;
                if (existing->sink) existing->sink->handle = h;
            } else if (!live && existing->handle == 0) {
                existing->bounds = RECT{};
            }
        } else {
            PinnedComment np{};
            np.handle = h; np.id = id; np.text = text; np.bounds = b;
            np.ui = nullptr; np.sink = nullptr; np.name = anchor; np.seeded = true;
            np.revision = items[i].revision ? items[i].revision : L"";
            g_pins.push_back(np);
        }
        if (live) ++placed;
    }

    // Drop pins the store no longer lists; only an authoritative push also drops session-authored ones.
    for (size_t i = g_pins.size(); i-- > 0;) {
        if (!authoritative && !g_pins[i].seeded) continue;
        bool stillListed = false;
        for (size_t j = 0; j < count && !stillListed; ++j) {
            if (items[j].id && g_pins[i].id == items[j].id) stillListed = true;
        }
        if (stillListed) continue;
        DropPinUi(g_pins[i]);
        g_pins.erase(g_pins.begin() + (ptrdiff_t)i);
    }

// The toggle owns visibility; what changes is that it now has the whole persisted set to reveal, and the badge
// counts it.
    if (g_commentsShown) DrawAllPins();
    UpdateCommentBadge();
    TrackingSync();

    if (outPlaced) *outPlaced = placed;
    DevToolsOverlayLog(L"overlay.comments pushed=%zu registry=%zu placed=%zu (unplaced comments belong to elements not realized right now)",
                  count, g_pins.size(), placed);
    return S_OK;
}

// Retained because the composer/edit-in-place machinery is still reached via the panel's comment box.)
static void CommentModeOpenComposer(InstanceHandle picked)
{
    if (!g_pickDiag || !picked) return;
    IInspectable* el = nullptr;
    if (SUCCEEDED(g_pickDiag->GetIInspectableFromHandle(picked, &el)) && el) { ShowComposerForElement(el, true); el->Release(); }
}

// It reveals or hides the markers for the WHOLE registry: the open set the CLI pushed out of
// `.winapp/ui-comments.json` on attach (DevToolsOverlay_SetComments) plus anything composed since.
static void OnCommentsToggleClick(void*, void*)
{
    SetCommentsShown(!g_commentsShown);
}


// IXamlDiagnostics2 is declared from the public WinAppSDK xamlom.winui.h ABI and queried from the live SetSite
// diagnostics object. Screen-to-content conversion must use the active root's ContentCoordinateConverter, not a
// separately discovered HWND: multiple islands can share one HWND.
// IXamlDiagnostics2 is queried from live diagnostics because multi-root picking requires HitTestForXamlRoot.
static void ScreenToContentDip(int screenX, int screenY, long* dipX, long* dipY)
{
    const DevToolsSurface& s = DevToolsOverlay_ActiveSurface();
    if (s.Valid()) {
        float lx = 0.0f, ly = 0.0f;
        if (DevToolsSurface_ScreenToLocal(s, screenX, screenY, &lx, &ly)) {
            *dipX = (long)lx;
            *dipY = (long)ly;
            return;
        }
    }
    HWND hwnd = nullptr, island = nullptr;
    ResolveAppWindowCached(&hwnd, &island);
    UINT dpi = hwnd ? GetDpiForWindow(hwnd) : 96; if (!dpi) dpi = 96;
    POINT p{ screenX, screenY };
    if (island) ScreenToClient(island, &p); else if (hwnd) ScreenToClient(hwnd, &p);
    *dipX = MulDiv(p.x, 96, (int)dpi);
    *dipY = MulDiv(p.y, 96, (int)dpi);
}

// Refuse picks outside the active root's extent or inside a different resolved surface's screen rectangle.
// Bounds alone cannot detect a point converted through the wrong origin.
// Bounds and surface identity both gate picks; wrong-origin conversion can still land in bounds.
void DevToolsOverlay_ClassifyPickPoint(IXamlDiagnostics* diag, InstanceHandle rootHandle,
                                  int screenX, int screenY, DevToolsSurfacePoint* out)
{
    if (!out) return;
    *out = DevToolsSurfacePoint{};

// The surface the hit-test is about to be named by, resolved from the SAME handle that will be passed to
// HitTestForXamlRoot -- not from the cached active surface, so a caller hit-testing a root other than the
// active one is still classified against the root it is really using.
    DevToolsSurface s;
    if (!DevToolsSurface_Resolve(diag, rootHandle, &s)) return;
    if (g_pickCatcher && rootHandle == g_pickRoot &&
        s.xamlRootKey != reinterpret_cast<unsigned long long>(g_selectionRootIdentity.get())) return;
    float x = 0, y = 0;
    if (!DevToolsSurface_ScreenToLocal(s, screenX, screenY, &x, &y)) return;
    out->contentDipX = (long)x;
    out->contentDipY = (long)y;
    const DevToolsSurface& active = DevToolsOverlay_ActiveSurface();
    if (active.Valid() && active.xamlRootKey == s.xamlRootKey) s = active;

    out->resolved = true;
    out->contentW = (long)s.contentW;
    out->contentH = (long)s.contentH;
    out->activeName = s.displayName;

    const bool outOfBounds = out->contentW > 0 && out->contentH > 0 &&
                             (out->contentDipX < 0 || out->contentDipY < 0 ||
                              out->contentDipX >= out->contentW || out->contentDipY >= out->contentH);
    const DevToolsSurface* at = SurfaceAtScreenPoint(screenX, screenY);
    const bool wrongOrigin = !DevToolsSurface_ContainsScreenPoint(s, screenX, screenY) &&
        at && at->xamlRootKey != s.xamlRootKey;

    if (outOfBounds || wrongOrigin) {
        out->outside = true;
        out->reason = outOfBounds ? L"bounds" : L"origin";
        if (at && at->xamlRootKey != s.xamlRootKey) out->pointName = at->displayName;
        DevToolsOverlayLog(L"overlay.pick.surface REFUSED reason=%s screen=%d,%d contentDip=%ld,%ld "
                      L"rootExtent=%ldx%ld surfaceRect=%d,%d,%d,%d active='%s' point='%s'",
                      out->reason.c_str(), screenX, screenY, out->contentDipX, out->contentDipY,
                      out->contentW, out->contentH,
                      (int)s.contentRect.left, (int)s.contentRect.top,
                      (int)s.contentRect.right, (int)s.contentRect.bottom,
                      out->activeName.c_str(), out->pointName.c_str());
    }
}

HRESULT DevToolsOverlay_Pick(IXamlDiagnostics* diag, InstanceHandle rootHandle, int screenX, int screenY,
                        DevToolsSeedFn seed, DevToolsIsOverlayFn isOverlay, InstanceHandle* outHandle, RECT* outBoundsDip)
{
    WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::OverlayPick);
    if (outHandle) *outHandle = 0;
    if (outBoundsDip) { outBoundsDip->left = outBoundsDip->top = outBoundsDip->right = outBoundsDip->bottom = 0; }
    if (!diag || !rootHandle) return E_INVALIDARG;

// QI-probe-and-branch (== VS's IsMultiRootSupported): prefer the multi-root
// IXamlDiagnostics2::HitTestForXamlRoot; degrade to the single-root IXamlDiagnostics::HitTest when the app's
// loaded WinUI runtime does not implement IXamlDiagnostics2 (older versions). diag2 (when present) is QI'd off
// the SAME live site diagnostics.
    // Prefer multi-root HitTestForXamlRoot and degrade to single-root only when unavailable.
    IXamlDiagnostics2_Abi* diag2 = CachedDiag2(diag);
    const bool hasDiag2 = (diag2 != nullptr);

    DevToolsSurfacePoint point;
    DevToolsOverlay_ClassifyPickPoint(diag, rootHandle, screenX, screenY, &point);
    if (!point.resolved || point.outside) return E_INVALIDARG;
    if (!hasDiag2 && g_allSurfaces.size() > 1) return E_NOINTERFACE;
    if (g_selectionHost) {
        const HRESULT host = g_selectionHost(rootHandle, true);
        if (FAILED(host)) return host;
    }
    const long px = point.contentDipX, py = point.contentDipY;
    
    int hitCount = 0;
    DevToolsOverlayLog(L"overlay.pick TID=%lu screen=%d,%d contentDip=%ld,%ld branch=%s root=%llu",
        GetCurrentThreadId(), screenX, screenY, px, py,
        hasDiag2 ? L"multi-root(HitTestForXamlRoot)" : L"single-root(HitTest)", (unsigned long long)rootHandle);

// One hit-test at a content-DIP point (integer RECT {x,y,x+1,y+1}). Reports the front-most NON-OVERLAY handle
// (via topOut) so a pick never selects our own toolbar/adorner, and whether `find` appears anywhere in the
// returned stack (via hasFind) for bounds.
    auto hitAt = [&](long x, long y, InstanceHandle find, InstanceHandle* topOut, bool* hasFind) {
        ++hitCount;
        if (topOut) *topOut = 0;
        if (hasFind) *hasFind = false;
        RECT r{ x, y, x + 1, y + 1 };
        unsigned int count = 0; InstanceHandle* hs = nullptr;
        HRESULT hr = hasDiag2 ? diag2->HitTestForXamlRoot(rootHandle, r, &count, &hs)
                               : diag->HitTest(r, &count, &hs);
        if (SUCCEEDED(hr) && count && hs) {
            for (unsigned int i = 0; i < count; ++i) {
                if (topOut && !*topOut && hs[i] && (!isOverlay || !isOverlay(hs[i]))) *topOut = hs[i];
                if (hasFind && find && hs[i] == find) *hasFind = true;
            }
        }
        if (hs) CoTaskMemFree(hs);
        return hr;
    };

    InstanceHandle picked = 0;
    const double tHit0 = QpcNow();
    const HRESULT hitResult = hitAt(px, py, 0, &picked, nullptr);
    if (FAILED(hitResult)) {
        g_lastPickBoundsDip = RECT{};
        return hitResult;
    }
    const double firstHitUs = QpcNow() - tHit0;
    if (picked && g_pickTarget) picked = g_pickTarget(picked);
    HRESULT result = S_FALSE;
    if (picked) {
        if (g_selectionHost) {
            const HRESULT allowed = g_selectionHost(picked, false);
            if (FAILED(allowed)) return allowed;
        }
        // Hit-test order is not ancestry; keep source, properties, and geometry on the picked element.
        LogHandleClass(diag, L"pick.top", picked);
        auto inStack = [&](double x, double y) { bool f = false; hitAt((long)x, (long)y, picked, nullptr, &f); return f; };
        const double tBounds0 = QpcNow();
        const int hitsBeforeBounds = hitCount;
        RECT b{};
        bool geometric = TryElementRootBounds(diag, rootHandle, picked, &b);
        if (!geometric) {
            double L = FindEdge([&](double x) { return inStack(x, py); }, px, -1);
            double R = FindEdge([&](double x) { return inStack(x, py); }, px, +1);
            double T = FindEdge([&](double y) { return inStack(px, y); }, py, -1);
            double B = FindEdge([&](double y) { return inStack(px, y); }, py, +1);
            b = RECT{ (LONG)L, (LONG)T, (LONG)(R + 1), (LONG)(B + 1) };
        }
        const double boundsUs = QpcNow() - tBounds0;
        const int boundsHits = hitCount - hitsBeforeBounds;
        DevToolsOverlayLog(L"overlay.pick picked=%llu bounds(dip) L=%ld T=%ld R=%ld B=%ld via=%s",
            (unsigned long long)picked, b.left, b.top, b.right, b.bottom, geometric ? L"geometric" : L"edge-probe");
        const HRESULT draw = DrawAdorner(diag, b.left, b.top, b.right - b.left, b.bottom - b.top, seed);
        if (FAILED(draw)) return draw;
        g_lastHighlightHandle = picked;
        NotifyStateChanged();
        g_lastPickWorkUs = firstHitUs + boundsUs + g_lastAdornerTotalUs;
// evidence: split the redraw into the two costs that were only ever guessed at -- the edge-probing bounds
// derivation (hit-tests) and the adorner rebuild (XamlReader parse + Canvas churn). per hovered-element CHANGE,
// not per pointer move.
        DevToolsOverlayLog(L"overlay.pick.timing firstHitUs=%.0f boundsUs=%.0f boundsHitTests=%d adornerUs=%.0f (clear=%.0f xamlLoad=%.0f insert=%.0f) totalHitTests=%d",
            firstHitUs, boundsUs, boundsHits, g_lastAdornerTotalUs,
            g_lastAdornerClearUs, g_lastAdornerLoadUs, g_lastAdornerInsertUs, hitCount);
        g_lastPickBoundsDip = b;
        if (outHandle) *outHandle = picked;
        if (outBoundsDip) *outBoundsDip = b;
        result = S_OK;
    } else {
        g_lastPickBoundsDip = RECT{};
        DevToolsOverlayLog(L"overlay.pick no app element under point");
    }
    return result;
}

// Cheap single hit-test at a SCREEN point: returns just the front-most non-overlay element handle (0 if none),
// with NO edge-probing and NO adorner draw. Used by the pick-catcher hover to detect when the element under the
// cursor CHANGES, so the expensive DevToolsOverlay_Pick (bounds edge-probe + adorner rebuild) runs only.
static InstanceHandle PeekTopHandleAt(IXamlDiagnostics* diag, InstanceHandle rootHandle, int screenX, int screenY,
                                      DevToolsIsOverlayFn isOverlay, HRESULT* result)
{
    if (result) *result = E_INVALIDARG;
    if (!diag || !rootHandle) return 0;
    IXamlDiagnostics2_Abi* diag2 = CachedDiag2(diag);
    const bool hasDiag2 = (diag2 != nullptr);
    DevToolsSurfacePoint point{};
    DevToolsOverlay_ClassifyPickPoint(diag, rootHandle, screenX, screenY, &point);
    if (!point.resolved || point.outside) return 0;
    const long px = point.contentDipX, py = point.contentDipY;
    RECT r{ px, py, px + 1, py + 1 };
    unsigned int count = 0; InstanceHandle* hs = nullptr;
    HRESULT hr = hasDiag2 ? diag2->HitTestForXamlRoot(rootHandle, r, &count, &hs) : diag->HitTest(r, &count, &hs);
    if (result) *result = hr;
    InstanceHandle top = 0;
    if (SUCCEEDED(hr) && count && hs) {
        for (unsigned int i = 0; i < count; ++i) {
            if (hs[i] && (!isOverlay || !isOverlay(hs[i]))) { top = hs[i]; break; }
        }
    }
    if (hs) CoTaskMemFree(hs);
    return top;
}

// Highlight only: no badge, no props (matches window hover, and avoids realizing any async parts on every
// move). Debounced two ways to protect the app UI thread: (1) a ~16ms rate limit on the cheap peek hit-test,
// and (2) a changed- handle short-circuit so the expensive edge-probe/adorner rebuild in DevToolsOverlay_Pick
// r.
static bool PickerEditorIsOpen() { return DevToolsSelPanelIsOpen() || g_composerUi != nullptr; }

static void OnCatcherPointerMoved(void* sender, void*)
{
    if (!IsCurrentCatcher(sender) || PickerEditorIsOpen() || !g_pickDiag || !g_pickRoot) return;

// Accumulated in a guard destructor so the early-out paths (rate limit, unchanged element) are counted too --
// they are most of the moves, and an average that silently excludes them is not the number the UI thread pays.
// Reported once every 64 moves because DevToolsOverlayLog opens and closes the log file per line, so logg.
    static double s_totalUs = 0.0, s_peekUs = 0.0, s_pickUs = 0.0, s_workUs = 0.0;
    static int    s_moves = 0, s_peeks = 0, s_changes = 0;
    double peekUs = 0.0, pickUs = 0.0, workUs = 0.0;
    struct HoverGuard {
        double t0; const double* peek; const double* pick; const double* work; bool peeked; bool changed;
        ~HoverGuard() {
            s_totalUs += QpcNow() - t0; s_peekUs += *peek; s_pickUs += *pick; s_workUs += *work;
            if (peeked) ++s_peeks;
            if (changed) ++s_changes;
            if (++s_moves >= 64) {
                DevToolsOverlayLog(L"overlay.hover.cost moves=%d peeks=%d changes=%d avgMoveUs=%.0f avgPeekUs=%.0f perChangeUs=%.0f perChangeWorkUs=%.0f",
                    s_moves, s_peeks, s_changes, s_totalUs / s_moves,
                    s_peeks ? s_peekUs / s_peeks : 0.0, s_changes ? s_pickUs / s_changes : 0.0,
                    s_changes ? s_workUs / s_changes : 0.0);
                s_moves = 0; s_peeks = 0; s_changes = 0;
                s_totalUs = 0.0; s_peekUs = 0.0; s_pickUs = 0.0; s_workUs = 0.0;
            }
        }
    } guard{ QpcNow(), &peekUs, &pickUs, &workUs, false, false };

    ULONGLONG now = GetTickCount64();
    if (now - g_hoverLastTick < 16) return;

// The pick COMMITS at the PRESS point (latched it there because a Click-time GetCursorPos has already drifted
// under UI-thread load), but PointerMoved keeps firing through a press-drag-release and would walk the
// highlight to wherever the pointer ended up - showing you B and then selecting A. The moves keep arriving not
// be.
    // Commit at pointer press point; release-time cursor position can drift under UI-thread load.
    if (g_pressValid) return;

    g_hoverLastTick = now;
    POINT pt{ 0, 0 }; GetCursorPos(&pt);
    const double tPeek0 = QpcNow();
    HRESULT hitResult = S_OK;
    InstanceHandle top = PeekTopHandleAt(g_pickDiag, g_pickRoot, pt.x, pt.y, g_pickIsOverlay, &hitResult);
    if (FAILED(hitResult)) {
        DevToolsOverlayLog(L"overlay.pick hover failed hr=0x%08X", hitResult);
        return;
    }
    if (!IsCurrentCatcher(sender) || PickerEditorIsOpen()) return;
    peekUs = QpcNow() - tPeek0;
    guard.peeked = true;
    if (top == g_hoverLastHandle) return;
    g_hoverLastHandle = top;
    guard.changed = true;
    if (!top) {
        ClearAdorner();
        g_lastHighlightHandle = 0;
        NotifyStateChanged();
        return;
    }
// Element changed: run the full pick (edge-probes bounds + draws the highlight adorner).
    InstanceHandle picked = 0; RECT b{};
    const double tPick0 = QpcNow();
    DevToolsOverlay_Pick(g_pickDiag, g_pickRoot, pt.x, pt.y, g_pickSeed, g_pickIsOverlay, &picked, &b);
    pickUs = QpcNow() - tPick0;
    workUs = g_lastPickWorkUs;
}


// Coordinates land in the same content-DIP space as the adorner Canvas (root visual origin == XAML island
// top-left == the ScreenToContentDip origin the hit-test path uses). Uses the C++/WinRT projection for every
// WinUI call (same as the rest of the tap).
struct DevToolsAbiPoint { float X; float Y; };
static bool TrySharedRootContent(void* targetUi, void* suppliedRootUi, void** content)
{
    *content = nullptr;
    winrt::com_ptr<IInspectable> targetRoot, suppliedRoot;
    if (FAILED(DevToolsGetXamlRoot(targetUi, targetRoot.put())) || !targetRoot ||
        FAILED(DevToolsGetXamlRoot(suppliedRootUi, suppliedRoot.put())) || !suppliedRoot) return false;

// Compare COM identity, not interface addresses, before transforming into the supplied surface's content-DIP
// space.
    // Compare COM identity before transforming handles because detached handles can resolve elsewhere.
    winrt::com_ptr<IUnknown> targetIdentity, suppliedIdentity;
    if (FAILED(targetRoot->QueryInterface(IID_IUnknown, targetIdentity.put_void())) || !targetIdentity ||
        FAILED(suppliedRoot->QueryInterface(IID_IUnknown, suppliedIdentity.put_void())) || !suppliedIdentity ||
        targetIdentity.get() != suppliedIdentity.get()) return false;

    winrt::com_ptr<IUnknown> rootContent;
    if (FAILED(DevToolsAbi<DevToolsX::IXamlRoot>(suppliedRoot.get())->get_Content(rootContent.put_void())) ||
        !rootContent) return false;
    *content = rootContent.detach();
    return true;
}

// Geometry can remain nonzero after collapse/unload, and a transparent ancestor can hide an otherwise visible
// child.
static HRESULT ReadElementPresentation(IInspectable* element, bool* presented)
{
    *presented = false;
    winrt::com_ptr<IInspectable> current;
    current.copy_from(element);
    for (unsigned depth = 0; current && depth < 128; ++depth) {
        winrt::com_ptr<IInspectable> ui, fe;
        HRESULT hr = current->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), ui.put_void());
        if (FAILED(hr) && hr != E_NOINTERFACE) return hr;
        if (ui) {
            int visibility = 0;
            double opacity = 0;
            hr = DevToolsGetVisibility(ui.get(), &visibility);
            if (FAILED(hr)) return hr;
            if (visibility != 0) return S_OK;
            hr = DevToolsGetOpacity(ui.get(), &opacity);
            if (FAILED(hr)) return hr;
            if (!std::isfinite(opacity) || opacity < 0) return E_UNEXPECTED;
            if (opacity == 0) return S_OK;
        }
        hr = current->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), fe.put_void());
        if (FAILED(hr) && hr != E_NOINTERFACE) return hr;
        if (fe) {
            bool loaded = false;
            hr = DevToolsGetIsLoaded(fe.get(), &loaded);
            if (FAILED(hr)) return hr;
            if (!loaded) return S_OK;
        }
        winrt::com_ptr<IInspectable> parent;
        hr = DevToolsGetVisualParent(current.get(), parent.put());
        if (FAILED(hr)) return hr;
        current = std::move(parent);
    }
    if (current) return E_UNEXPECTED;
    *presented = true;
    return S_OK;
}

static bool TryElementRootBounds(IXamlDiagnostics* diag, InstanceHandle rootHandle, InstanceHandle target,
                                 RECT* out, bool* noVisibleBox)
{
    if (noVisibleBox) *noVisibleBox = false;
    if (!diag || !rootHandle || !target || !out) return false;

    IInspectable* tInsp = nullptr;
    if (FAILED(diag->GetIInspectableFromHandle(target, &tInsp)) || !tInsp) return false;
    bool presented = false;
    const HRESULT presentation = ReadElementPresentation(tInsp, &presented);
    if (FAILED(presentation) || !presented) {
        if (FAILED(presentation))
            DevToolsOverlayLog(L"overlay.presentation handle=%llu read failed hr=0x%08X",
                (unsigned long long)target, presentation);
        else if (noVisibleBox) *noVisibleBox = true;
        tInsp->Release();
        return false;
    }
    IInspectable* rInsp = nullptr;
    if (FAILED(diag->GetIInspectableFromHandle(rootHandle, &rInsp)) || !rInsp) { tInsp->Release(); return false; }

    bool ok = false;
    void* tFe = nullptr; void* tUi = nullptr; void* rUi = nullptr; void* gt = nullptr;

// get_ActualWidth/get_ActualHeight, TransformToVisual and TransformPoint are all projected calls, so the
// element bounds path carries no offsets of its own.

    if (SUCCEEDED(tInsp->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &tFe)) && tFe &&
        SUCCEEDED(tInsp->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &tUi)) && tUi &&
        SUCCEEDED(rInsp->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &rUi)) && rUi)
    {
// A surface handle identifies a root but need not itself be XamlRoot.Content. Bounds, the corrected popup, and
// HitTestForXamlRoot all use content DIPs.
        void* content = nullptr;
        if (!TrySharedRootContent(tUi, rUi, &content)) {
            reinterpret_cast<IUnknown*>(rUi)->Release();
            reinterpret_cast<IUnknown*>(tUi)->Release();
            reinterpret_cast<IUnknown*>(tFe)->Release();
            rInsp->Release(); tInsp->Release();
            return false;
        }
        reinterpret_cast<IUnknown*>(rUi)->Release();
        rUi = content;

        double w = 0, h = 0;
        float rw = 0, rh = 0;
        if (SUCCEEDED(DevToolsGetRenderSize(tUi, &rw, &rh)) && std::isfinite(rw) && std::isfinite(rh) &&
            rw > 0.5f && rh > 0.5f) {
            w = (double)rw; h = (double)rh;
        } else {
            HRESULT hw = DevToolsGetActualWidth(tFe, &w);
            HRESULT hh = DevToolsGetActualHeight(tFe, &h);
            if (FAILED(hw) || FAILED(hh)) { w = 0; h = 0; }
        }
        if (std::isfinite(w) && std::isfinite(h) &&
            w > 0.5 && h > 0.5 && w < 100000.0 && h < 100000.0)
        {
            HRESULT ht = DevToolsTransformToVisual(tUi, rUi, &gt);
            if (SUCCEEDED(ht) && gt)
            {
                DevToolsAbiPoint p0{}, p1{};
                HRESULT h0 = DevToolsTransformPoint(gt, 0.0f, 0.0f, &p0.X, &p0.Y);
                HRESULT h1 = DevToolsTransformPoint(gt, (float)w, (float)h, &p1.X, &p1.Y);
                if (SUCCEEDED(h0) && SUCCEEDED(h1) &&
                    std::isfinite(p0.X) && std::isfinite(p0.Y) && std::isfinite(p1.X) && std::isfinite(p1.Y))
                {
                    double L = (std::min)((double)p0.X, (double)p1.X);
                    double T = (std::min)((double)p0.Y, (double)p1.Y);
                    double R = (std::max)((double)p0.X, (double)p1.X);
                    double B = (std::max)((double)p0.Y, (double)p1.Y);
                    if (R - L > 0.5 && B - T > 0.5) {
                        out->left = (LONG)L; out->top = (LONG)T; out->right = (LONG)R; out->bottom = (LONG)B;
                        ok = true;
                    }
                }
            }
        }
        else if (noVisibleBox && std::isfinite(w) && std::isfinite(h) && w >= 0.0 && h >= 0.0 &&
                 (w <= 0.5 || h <= 0.5)) {
// Reported so the caller can stop rather than go hunting for pixels that do not exist.
            *noVisibleBox = true;
        }
    }

    if (gt) reinterpret_cast<IUnknown*>(gt)->Release();
    if (rUi) reinterpret_cast<IUnknown*>(rUi)->Release();
    if (tUi) reinterpret_cast<IUnknown*>(tUi)->Release();
    if (tFe) reinterpret_cast<IUnknown*>(tFe)->Release();
    rInsp->Release();
    tInsp->Release();
    return ok;
}

// WinUI has no common base carrying it -- Control, Border, TextBlock, RichTextBlock, StackPanel, Grid and
// ContentPresenter each redeclare it -- so this QIs in turn and stops at the first hit.
// Padding is per-type in WinUI; probe supported interfaces instead of assuming a common base.
template <typename I>
static bool TryPaddingVia(IInspectable* insp, double* l, double* t, double* r, double* b)
{
    void* p = nullptr;
    if (FAILED(insp->QueryInterface(DevToolsIid<I>(), &p)) || !p) return false;
    HRESULT hr = DevToolsGetPaddingAs<I>(p, l, t, r, b);
    reinterpret_cast<IUnknown*>(p)->Release();
    return SUCCEEDED(hr);
}
static bool TryElementPadding(IInspectable* insp, double* l, double* t, double* r, double* b)
{
    return TryPaddingVia<DevToolsXC::IControl>(insp, l, t, r, b)
        || TryPaddingVia<DevToolsXC::IBorder>(insp, l, t, r, b)
        || TryPaddingVia<DevToolsXC::ITextBlock>(insp, l, t, r, b)
        || TryPaddingVia<DevToolsXC::IRichTextBlock>(insp, l, t, r, b)
        || TryPaddingVia<DevToolsXC::IStackPanel>(insp, l, t, r, b)
        || TryPaddingVia<DevToolsXC::IGrid>(insp, l, t, r, b)
        || TryPaddingVia<DevToolsXC::IContentPresenter>(insp, l, t, r, b);
}

// Check root liveness and finite, nondegenerate bounds; an unreadable layer suppresses only that layer.
static bool TryElementLayoutBox(IXamlDiagnostics* diag, InstanceHandle rootHandle, InstanceHandle target,
                                DevToolsLayoutBox* out)
{
    if (!diag || !rootHandle || !target || !out) return false;
    *out = DevToolsLayoutBox{};

    IInspectable* tInsp = nullptr;
    if (FAILED(diag->GetIInspectableFromHandle(target, &tInsp)) || !tInsp) return false;
    IInspectable* rInsp = nullptr;
    if (FAILED(diag->GetIInspectableFromHandle(rootHandle, &rInsp)) || !rInsp) { tInsp->Release(); return false; }

    bool ok = false;
    void* tFe = nullptr; void* tUi = nullptr; void* rUi = nullptr; void* gt = nullptr;

    if (SUCCEEDED(tInsp->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &tFe)) && tFe &&
        SUCCEEDED(tInsp->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &tUi)) && tUi &&
        SUCCEEDED(rInsp->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &rUi)) && rUi)
    {
        void* content = nullptr;
        if (TrySharedRootContent(tUi, rUi, &content))
        {
            reinterpret_cast<IUnknown*>(rUi)->Release();
            rUi = content;
            float rw = 0, rh = 0;
            if (SUCCEEDED(DevToolsGetRenderSize(tUi, &rw, &rh)) &&
                std::isfinite(rw) && std::isfinite(rh) && rw > 0.5f && rh > 0.5f && rw < 100000.0f && rh < 100000.0f &&
                SUCCEEDED(DevToolsTransformToVisual(tUi, rUi, &gt)) && gt)
            {
                auto mapRect = [&](double x0, double y0, double x1, double y1, RECT* dst) -> bool {
                    float ax = 0, ay = 0, bx = 0, by = 0;
                    if (FAILED(DevToolsTransformPoint(gt, (float)x0, (float)y0, &ax, &ay)) ||
                        FAILED(DevToolsTransformPoint(gt, (float)x1, (float)y1, &bx, &by))) return false;
                    if (!std::isfinite(ax) || !std::isfinite(ay) || !std::isfinite(bx) || !std::isfinite(by)) return false;
                    const double L = (std::min)((double)ax, (double)bx), R = (std::max)((double)ax, (double)bx);
                    const double T = (std::min)((double)ay, (double)by), B = (std::max)((double)ay, (double)by);
                    if (R - L < 0.5 || B - T < 0.5) return false;
                    dst->left = (LONG)L; dst->top = (LONG)T; dst->right = (LONG)R; dst->bottom = (LONG)B;
                    return true;
                };

                ok = mapRect(0.0, 0.0, (double)rw, (double)rh, &out->box);
                if (ok)
                {
                    double ml = 0, mt = 0, mr = 0, mb = 0;
                    HRESULT hm = DevToolsGetMargin(tFe, &ml, &mt, &mr, &mb);
                    if (SUCCEEDED(hm) &&
                        std::isfinite(ml) && std::isfinite(mt) && std::isfinite(mr) && std::isfinite(mb) &&
                        (ml != 0.0 || mt != 0.0 || mr != 0.0 || mb != 0.0))
                    {
// A NEGATIVE margin is legal XAML and pulls the rect inside the box; that is the truth about the element, so it
// is drawn rather than rejected.
                        // Negative margin is legal XAML and must be drawn, not rejected.
                        out->haveMargin = mapRect(-ml, -mt, (double)rw + mr, (double)rh + mb, &out->margin);
                    }

                    double pl = 0, pt = 0, pr = 0, pb = 0;
                    bool hp = TryElementPadding(tInsp, &pl, &pt, &pr, &pb);
                    if (hp &&
                        std::isfinite(pl) && std::isfinite(pt) && std::isfinite(pr) && std::isfinite(pb) &&
                        (pl != 0.0 || pt != 0.0 || pr != 0.0 || pb != 0.0) &&
                        pl + pr < (double)rw && pt + pb < (double)rh)
                    {
                        out->havePadding = mapRect(pl, pt, (double)rw - pr, (double)rh - pb, &out->padding);
                    }
                }
            }
        }
    }

    if (gt) reinterpret_cast<IUnknown*>(gt)->Release();
    if (rUi) reinterpret_cast<IUnknown*>(rUi)->Release();
    if (tUi) reinterpret_cast<IUnknown*>(tUi)->Release();
    if (tFe) reinterpret_cast<IUnknown*>(tFe)->Release();
    rInsp->Release();
    tInsp->Release();
    return ok;
}

// Highlight by handle on the UI thread. Prefer layout bounds, then bounded hit-test scanning only when geometry
// is unavailable.
HRESULT DevToolsOverlay_HighlightHandle(IXamlDiagnostics* diag, InstanceHandle rootHandle, InstanceHandle target,
                                   DevToolsSeedFn seed, DevToolsIsOverlayFn isOverlay)
{
    (void)isOverlay;
    WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::OverlayHighlight);
    if (!diag) return E_INVALIDARG;

    auto applied = [](InstanceHandle h) { g_lastHighlightHandle = h; NotifyStateChanged(); };

    if (!target) { ClearAdorner(); applied(0); return S_OK; }
    if (g_selectionHost) {
        const HRESULT host = g_selectionHost(target, false);
        if (FAILED(host)) return host;
        rootHandle = target;
    }
    if (!rootHandle) return E_INVALIDARG;

    IInspectable* insp = nullptr;
    if (FAILED(diag->GetIInspectableFromHandle(target, &insp)) || !insp) { ClearAdorner(); applied(0); return S_FALSE; }
    insp->Release();

// Primary path: geometric bounds (covers transparent layout containers the hit-test scan misses).
    {
        RECT g{};
        bool noVisibleBox = false;
        if (TryElementRootBounds(diag, rootHandle, target, &g, &noVisibleBox)) {
            DevToolsOverlayLog(L"overlay.highlight target=%llu geometric bounds L=%ld T=%ld R=%ld B=%ld",
                (unsigned long long)target, g.left, g.top, g.right, g.bottom);
            const HRESULT draw = DrawAdorner(diag, g.left, g.top, g.right - g.left, g.bottom - g.top, seed);
            if (FAILED(draw)) return draw;
            g_lastPickBoundsDip = g;
            applied(target);
            return S_OK;
        }
// Known zero size has no hittable area: clear the adorner and return S_FALSE.
        if (noVisibleBox) {
            DevToolsOverlayLog(L"overlay.highlight target=%llu has no visible box (cleared, scan skipped)",
                (unsigned long long)target);
            ClearAdorner();
            return S_FALSE;
        }
    }

    IXamlDiagnostics2_Abi* diag2 = CachedDiag2(diag);
    const bool hasDiag2 = (diag2 != nullptr);

// Is `target` anywhere in the hit stack at this content-DIP point?
    auto hasAt = [&](long x, long y) -> bool {
        RECT r{ x, y, x + 1, y + 1 };
        unsigned int count = 0; InstanceHandle* hs = nullptr;
        HRESULT hr = hasDiag2 ? diag2->HitTestForXamlRoot(rootHandle, r, &count, &hs)
                               : diag->HitTest(r, &count, &hs);
        bool found = false;
        if (SUCCEEDED(hr) && count && hs)
            for (unsigned int i = 0; i < count; ++i) if (hs[i] == target) { found = true; break; }
        if (hs) CoTaskMemFree(hs);
        return found;
    };

// Content-DIP extent of the XAML island (grid origin (0,0) == the ScreenToContentDip origin the pick uses).
    DevToolsSurface surface;
    if (!DevToolsSurface_Resolve(diag, rootHandle, &surface) ||
        (!hasDiag2 && g_allSurfaces.size() > 1)) return E_NOINTERFACE;
    const long dw = (long)surface.contentW, dh = (long)surface.contentH;

// A deliberate selection action, not a hot path, so a bounded scan is fine; elements thinner than `step` in
// BOTH axes may be missed (we then clear rather than box the wrong element).
    // Bounded grid scan is deliberate selection work, not a hot path.
    const long step = 20;
    long seedX = -1, seedY = -1;
    for (long y = step / 2; y <= dh && seedY < 0; y += step)
        for (long x = step / 2; x <= dw; x += step)
            if (hasAt(x, y)) { seedX = x; seedY = y; break; }

    HRESULT result = S_FALSE;
    if (seedX >= 0) {
        double L = FindEdge([&](double x) { return hasAt((long)x, seedY); }, seedX, -1);
        double R = FindEdge([&](double x) { return hasAt((long)x, seedY); }, seedX, +1);
        double T = FindEdge([&](double y) { return hasAt(seedX, (long)y); }, seedY, -1);
        double B = FindEdge([&](double y) { return hasAt(seedX, (long)y); }, seedY, +1);
        RECT b{ (LONG)L, (LONG)T, (LONG)(R + 1), (LONG)(B + 1) };
        DevToolsOverlayLog(L"overlay.highlight target=%llu seed=%ld,%ld bounds L=%ld T=%ld R=%ld B=%ld",
            (unsigned long long)target, seedX, seedY, b.left, b.top, b.right, b.bottom);
        const HRESULT draw = DrawAdorner(diag, b.left, b.top, b.right - b.left, b.bottom - b.top, seed);
        if (FAILED(draw)) return draw;
        g_lastPickBoundsDip = b;
        result = S_OK;
        applied(target);
    } else {
        DevToolsOverlayLog(L"overlay.highlight target=%llu not on screen (cleared)", (unsigned long long)target);
        ClearAdorner();
        applied(0);
    }
    return result;
}

HRESULT DevToolsOverlay_SelectHandle(IXamlDiagnostics* diag, InstanceHandle rootHandle, InstanceHandle target,
                                DevToolsSeedFn seed, DevToolsIsOverlayFn isOverlay)
{
    if (!target) { DeselectAll(); return S_OK; }
    if (g_selectionHost) {
        const HRESULT host = g_selectionHost(target, true);
        if (FAILED(host)) return host;
        rootHandle = g_pickRoot;
    }
    HRESULT hr = DevToolsOverlay_HighlightHandle(diag, rootHandle, target, seed, isOverlay);
    if (FAILED(hr) || hr == S_FALSE) return hr;
    if (target == g_selectedHandle) return hr;
    DevToolsOverlay_ShowBadgeForHandle(diag, target, /*fromUserPick*/ false);
    DevToolsOverlayLog(L"overlay.select from tree target=%llu", (unsigned long long)target);
    return hr;
}
