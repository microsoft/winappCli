// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsWindow.cpp; included into that translation unit, not compiled on its own.

static void SetStatusText(const std::wstring& s)
{
    if (!g_statusTb) return;
    HSTRING hs = MakeStr(s);
    DevToolsPutText(g_statusTb, hs);
    WindowsDeleteString(hs);
}

static void NoteChange(const std::wstring& prop, const std::wstring& preVal, const std::wstring& newVal)
{
    std::wstring* seedp = DirtyFind(prop);
    const std::wstring seed = seedp ? *seedp : preVal;
    bool dirty;
    if (newVal == seed) { DirtyErase(prop); dirty = false; }
    else { if (!seedp) g_dirtySeed.push_back({ prop, seed }); dirty = true; }
    if (void* dot = PaneRowChild(prop, L"PDot")) {
        DevToolsPutVisibility(dot, dirty ? 0 : 1);
        reinterpret_cast<IUnknown*>(dot)->Release();
    }
    // Restore has nothing to put back once the binding is gone, and saying otherwise is the lie removed.
    if (void* rs = PaneRowChild(prop, L"PRestore")) {
        DevToolsPutVisibility(rs, (dirty && !BindingLost(prop)) ? 0 : 1);
        reinterpret_cast<IUnknown*>(rs)->Release();
    }
}

// Render a single-quoted value for a prompt, elided so a long paste can't blow out the pane.
static std::wstring QuoteForPrompt(const std::wstring& v)
{
    std::wstring s = v;
    for (auto& c : s) if (c == L'\r' || c == L'\n' || c == L'\t') c = L' ';
    if (s.size() > 40) { s.resize(39); s += L'\u2026'; }
    return L"'" + s + L"'";
}

// One window pre-write gate extends the tap's live-binding check with authored resource links.
// Both reuse NeedsConfirm. A resolved local value alone cannot reveal that replacing it loses
// theme/resource tracking.
static DevToolsWriteOutcome CommitWrite(InstanceHandle wire, const std::wstring& prop, const std::wstring& type,
                                   const std::wstring& value, bool confirmed)
{
    if (!g_ctx.writeFn) return DevToolsWriteOutcome::Failed;
    if (!confirmed && ResourceBacked(prop) && !LinkLost(prop)) return DevToolsWriteOutcome::NeedsConfirm;
    WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::WinPropWrite);
    const DevToolsWriteOutcome oc = g_ctx.writeFn(wire, prop, type, value, confirmed);
    // A landed write over a resource link retires the link. Recorded BEFORE the caller reacts, so the pane
    // rebuild that ApplyCommitOutcome may trigger already sees the row as unlinked -- otherwise the row would
    // re-render still armed and prompt again for a link that is already gone.
    if (wire == g_selWire && confirmed && ResourceBacked(prop) &&
        (oc == DevToolsWriteOutcome::Ok || oc == DevToolsWriteOutcome::ReplacedBinding))
    {
        NoteLinkLost(prop);
    }
    return oc;
}

static bool RefreshCommittedValue(const std::wstring& prop);
static bool ApplyCommitOutcome(DevToolsWriteOutcome oc, InstanceHandle wire,
                               const std::wstring& prop, const std::wstring& type,
                               const std::wstring& preVal, const std::wstring& val)
{
    WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::WinPropCommit);
    if (wire != g_selWire) return DevToolsWriteLanded(oc);
    switch (oc) {
    case DevToolsWriteOutcome::Ok:
        PendingErase(prop);
        NoteChange(prop, preVal, val);
        if (LinkLost(prop)) {
            ShowPropsForNode(g_selWire, g_selLabel, g_selBreadcrumb, /*force*/ true);
            SetStatusText(L"Set " + prop + L" to a fixed value. It no longer follows the resource");
            return true;
        }
        if (RefreshCommittedValue(prop)) SetStatusText(L"Updated " + prop);
        else SetStatusText(L"Updated " + prop + L", but couldn't refresh its displayed value");
        return true;

    case DevToolsWriteOutcome::ReplacedBinding: {
        // The write landed AND the binding is gone for good. Do NOT record a dirty seed: the seed would be the
        // resolved literal, and offering an Undo that rewrites it would claim a restore that cannot happen.
        PendingErase(prop);
        DirtyErase(prop);
        NoteBindingLost(prop);
        // Rebuild so the row re-renders with its undo + reset affordances DISABLED and the reason attached.
        // Safe from inside a commit/click handler for the same reason ResetSink's rebuild is (the sinks are
        // process-lifetime and gen-guarded, not owned by the subtree being replaced).
        ShowPropsForNode(g_selWire, g_selLabel, g_selBreadcrumb, /*force*/ true);
        SetStatusText(L"Replaced the binding on " + prop + L" with a literal. That can't be undone");
        return true;
    }

    case DevToolsWriteOutcome::NeedsConfirm: {
        PendingErase(prop);
        g_pendingWrites.push_back({ prop, type, val });
        // Name the link that is about to be broken. "Replace binding" on a {ThemeResource} row would be a
        // confidently wrong prompt: there is no binding, and the thing the user is about to lose is the value
        // following the theme.
        const bool link = ResourceBacked(prop) && !LinkLost(prop);
        const std::wstring ask = (link ? L"Replace resource with " : L"Replace binding with ")
                               + QuoteForPrompt(val) + L"?";
        if (void* ct = PaneRowChild(prop, L"PConfirmText")) {
            HSTRING hs = MakeStr(ask); DevToolsPutText(ct, hs); WindowsDeleteString(hs);
            reinterpret_cast<IUnknown*>(ct)->Release();
        }
        if (void* po = PaneRowChild(prop, L"POrigin")) {
            DevToolsPutVisibility(po, 1);
            reinterpret_cast<IUnknown*>(po)->Release();
        }
        if (void* cf = PaneRowChild(prop, L"PConfirm")) {
            DevToolsPutVisibility(cf, 0);
            reinterpret_cast<IUnknown*>(cf)->Release();
        }
        SetStatusText(link
            ? (prop + L" comes from a resource. Confirm below to replace it with a fixed value")
            : (prop + L" is bound. Confirm below to replace the binding"));
        return false;
    }

    case DevToolsWriteOutcome::Failed:
    default:
        SetStatusText(L"Couldn't set " + prop);
        return false;
    }
}

// Commit one editable row on focus-out: read its live TextBox text through the projected getter and, if
// it changed from the value it was seeded with, write it to the app element via the in-process SetProperty
// bridge. Self-contained (captures its own node handle + property + seeded value + box) so it commits the
// right thing even if the pane has since been rebuilt for another node. UI thread only (TextBox.LostFocus).
struct LostFocusSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen) return S_OK;
        if (!g_ctx.readInputFn || !g_ctx.writeFn || !box) return S_OK;
        std::wstring text;
        if (!g_ctx.readInputFn(box, text)) {
            DevToolsOverlayLog(L"route1.input read failed for '%s'", prop.c_str());
            if (selWire == g_selWire) SetStatusText(L"Couldn't read the " + prop + L" editor");
            return S_OK;
        }
        if (text == original) return S_OK;
        if (ApplyCommitOutcome(CommitWrite(selWire, prop, type, text, /*confirmBindingReplace*/ false),
                               selWire, prop, type, original, text)) original = text;
        return S_OK;
    }
    void Init(InstanceHandle w, const std::wstring& p, const std::wstring& t, const std::wstring& orig, IInspectable* b)
    {
        selWire = w; prop = p; type = t; original = orig; box = b;
        InitSink(IID_RoutedEventHandler_Del, g_route1Gen);
    }
    InstanceHandle selWire = 0;
    std::wstring   prop, type, original;
    IInspectable*  box = nullptr;
};

// Clear the captured node/property through ClearProperty, then reread inheritance.
// Clearing can remove a binding/resource link and is distinct from restoring a prior literal.
// Capture identity so a pane rebuild cannot redirect the action. UI-thread Button.Click.
struct ResetSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen) return S_OK;
        if (!g_ctx.clearFn) return S_OK;
        if (BindingLost(prop)) {
            SetStatusText(L"Clearing " + prop + L" can't bring its binding back. Restart the app");
            return S_OK;
        }
        bool ok = g_ctx.clearFn(selWire, prop);
        DirtyErase(prop);
        ShowPropsForNode(g_selWire, g_selLabel, g_selBreadcrumb, /*force*/ true);
        SetStatusText(ok ? (L"Cleared " + prop + L" . It now falls back to its default")
                         : (L"Couldn't clear " + prop));
        return S_OK;
    }
    void Init(InstanceHandle w, const std::wstring& p)
    {
        selWire = w; prop = p;
        InitSink(IID_RoutedEventHandler_Del, g_route1Gen);
    }
    InstanceHandle selWire = 0;
    std::wstring   prop;
};

// Restore the selection's pre-edit literal and clear its dirty record. This is not ClearProperty.
// Refuse when a binding was replaced: restoring its resolved literal cannot restore the expression.
struct UndoSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen) return S_OK;
        if (!g_ctx.writeFn) return S_OK;
        if (BindingLost(prop)) {
            SetStatusText(L"Can't restore " + prop + L" . Its binding was replaced and can't be restored");
            return S_OK;
        }
        std::wstring* seedp = DirtyFind(prop);
        if (!seedp) return S_OK;
        const std::wstring seed = *seedp;
        DevToolsWriteOutcome oc = CommitWrite(selWire, prop, type, seed, /*confirmBindingReplace*/ false);
        DirtyErase(prop);
        // an incorrect resource-link claim.
        if (DevToolsWriteLanded(oc)) SessionAssignErase(prop);
        ShowPropsForNode(g_selWire, g_selLabel, g_selBreadcrumb, /*force*/ true);
        SetStatusText(DevToolsWriteLanded(oc) ? (L"Restored " + prop + L" to the value it had before your change")
                                         : (L"Couldn't restore " + prop));
        return S_OK;
    }
    void Init(InstanceHandle w, const std::wstring& p, const std::wstring& t)
    {
        selWire = w; prop = p; type = t;
        InitSink(IID_RoutedEventHandler_Del, g_route1Gen);
    }
    InstanceHandle selWire = 0;
    std::wstring   prop, type;
};

struct ConfirmSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen) return S_OK;
        if (!g_ctx.writeFn) return S_OK;
        PendingWrite* pw = PendingFind(prop);
        if (!pw) return S_OK;
        const std::wstring type = pw->type, val = pw->value;
        ApplyCommitOutcome(CommitWrite(selWire, prop, type, val, /*confirmBindingReplace*/ true),
                           selWire, prop, type, /*preVal*/ std::wstring(), val);
        return S_OK;
    }
    void Init(InstanceHandle w, const std::wstring& p)
    {
        selWire = w; prop = p;
        InitSink(IID_RoutedEventHandler_Del, g_route1Gen);
    }
    InstanceHandle selWire = 0;
    std::wstring   prop;
};

// ToggleSwitch pointer is non-owning; the routed event only fires while the switch is alive in the tree.
struct BoolToggleSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen) return S_OK;
        if (!g_ctx.writeFn || !toggle) return S_OK;
        bool on = false;
        if (FAILED(DevToolsToggleGetIsOn(toggle, &on))) return S_OK;
        const std::wstring val = DevToolsBoolLiteral(on);
        if (val == original) return S_OK;
        if (ApplyCommitOutcome(CommitWrite(selWire, prop, type, val, /*confirmBindingReplace*/ false),
                               selWire, prop, type, original, val)) original = val;
        return S_OK;
    }
    void Init(InstanceHandle w, const std::wstring& p, const std::wstring& t, const std::wstring& orig, void* ts)
    {
        selWire = w; prop = p; type = t; original = orig; toggle = ts;
        InitSink(IID_RoutedEventHandler_Del, g_route1Gen);
    }
    InstanceHandle selWire = 0;
    std::wstring   prop, type, original;
    void*          toggle = nullptr;
};

static void* g_splitPaneFe = nullptr;
static void* g_splitHostFe = nullptr;
static double g_splitTarget = 0.0;

static void ClampInspectorSplit()
{
    if (!g_splitPaneFe || !g_splitHostFe) return;
    double available = 0.0;
    if (FAILED(DevToolsGetActualWidth(g_splitHostFe, &available)) || available <= 0.0) return;
    if (g_splitTarget <= 0.0) g_splitTarget = 420.0;
    g_splitTarget = DevToolsInspector::TreeWidth(g_splitTarget, available);
    DevToolsPutWidth(g_splitPaneFe, g_splitTarget);
}

struct SplitSizeSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen == g_route1Gen) ClampInspectorSplit();
        return S_OK;
    }
    void Init() { InitSink(DevToolsIid<DevToolsX::SizeChangedEventHandler>(), g_route1Gen); }
};

struct SplitterSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void* args)
    {
        if (gen != g_route1Gen) return S_OK;
        void* pane = g_splitPaneFe;
        if (!pane || !args) return S_OK;
        double dx = 0.0, dy = 0.0;
        if (FAILED(DevToolsDragDeltaGetChange(args, &dx, &dy))) return S_OK;
        if (dx == 0.0) return S_OK;
        if (g_splitTarget <= 0.0) {
            double cur = 0.0;
            if (FAILED(DevToolsGetActualWidth(pane, &cur)) || cur <= 0.0) return S_OK;
            g_splitTarget = cur;
        }
        g_splitTarget += dx;
        ClampInspectorSplit();
        return S_OK;
    }
    void Init()
    {
        InitSink(IID_DragDeltaEventHandler_Del, g_route1Gen);
    }
};

// Switch Set/Computed view and rebuild from Click. WinUI retains the sender; the sink's
// generation guard makes stale callbacks inert.
// Segment clicks set the target view rather than flip state, so re-clicking the active half is stable.
struct OnlySetToggleSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen) return S_OK;
        if (g_propView == view) {
            ShowPropsForNode(g_selWire, g_selLabel, g_selBreadcrumb, /*force*/ true);
            return S_OK;
        }
        g_propView = view;
        ShowPropsForNode(g_selWire, g_selLabel, g_selBreadcrumb, /*force*/ true);
        return S_OK;
    }
    void Init(DevToolsPropView v) { view = v; InitSink(IID_RoutedEventHandler_Del, g_route1Gen); }
    DevToolsPropView view = DevToolsPropView::Computed;
};

// Expand / collapse the props-pane ancestor breadcrumb. The breadcrumb is shown elided to one line
// (BcShort); this flips to the full wrapped chain (BcFull) and back. Both TextBlocks are AddRef'd IUIElements
// held for the pane's lifetime, so the toggle is one put_Visibility pair -- no pane rebuild, no lost scroll
// position.
struct BreadcrumbSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen) return S_OK;
        if (!g_bcShort || !g_bcFull) return S_OK;
        g_bcExpanded = !g_bcExpanded;
        DevToolsPutVisibility(g_bcShort, g_bcExpanded ? 1 : 0);
        DevToolsPutVisibility(g_bcFull, g_bcExpanded ? 0 : 1);
        SetToggleLabel(g_bcToggleLbl, g_bcExpanded ? L"Show less" : g_bcExpandText.c_str());
        return S_OK;
    }
    void Init() { InitSink(IID_RoutedEventHandler_Del, g_route1Gen); }
};

// Disclosure sinks hold AddRef’d pane elements only for the current pane lifetime.
struct LayoutSectionSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen) return S_OK;
        if (!g_layoutBody) return S_OK;
        g_layoutSectionOpen = LayoutSectionOpen() ? 0 : 1;
        DevToolsSettingsSetBool(L"InspectorSizeSpacingOpen", g_layoutSectionOpen == 1);
        DevToolsPutVisibility(g_layoutBody, g_layoutSectionOpen == 1 ? 0 : 1);
        SetToggleLabel(g_layoutBodyChev, g_layoutSectionOpen == 1 ? L"\uE70D" : L"\uE76C");
        return S_OK;
    }
    void Init() { InitSink(IID_RoutedEventHandler_Del, g_route1Gen); }
};

struct AxmlToggleSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen) return S_OK;
        if (!g_axmlOne || !g_axmlFull) return S_OK;
        g_axmlExpanded = !g_axmlExpanded;
        DevToolsPutVisibility(g_axmlOne,  g_axmlExpanded ? 1 : 0);
        DevToolsPutVisibility(g_axmlFull, g_axmlExpanded ? 0 : 1);
        SetToggleLabel(g_axmlChev, g_axmlExpanded ? L"\uE70D" : L"\uE76C");
        return S_OK;
    }
    void Init() { InitSink(IID_RoutedEventHandler_Del, g_route1Gen); }
};

// Short display for the source line: the leaf filename + ":<line>" from a ms-appx:///path/Foo.xaml URI (or a
// raw path). Empty in -> empty out (so an un-instrumented node renders nothing / "source unavailable").
static std::wstring SourceFileName(const std::wstring& uri)
{
    if (uri.empty()) return L"";
    size_t slash = uri.find_last_of(L"/\\");
    return (slash == std::wstring::npos) ? uri : uri.substr(slash + 1);
}
static std::wstring SourceDisplay(const std::wstring& uri, unsigned int line)
{
    std::wstring leaf = SourceFileName(uri);
    if (leaf.empty()) return L"";
    if (line > 0) leaf += L" : " + std::to_wstring(line);
    return leaf;
}
