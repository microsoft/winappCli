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

// The last write's failure reason, and the row it is shown on until that row's next commit or another selection.
static void SetTextOn(IInspectable* el, const std::wstring& text);
static std::wstring g_lastWriteReason;
static InstanceHandle g_writeErrorWire = 0;
static std::wstring g_writeErrorPath, g_writeErrorText;

static void ShowWriteError(const std::wstring& prop, const std::wstring& text)
{
    g_writeErrorWire = text.empty() ? 0 : g_selWire;
    g_writeErrorPath = text.empty() ? std::wstring() : prop;
    g_writeErrorText = text;
    if (void* er = PaneRowChild(prop, L"PError")) {
        if (!text.empty()) SetTextOn(reinterpret_cast<IInspectable*>(er), text);
        DevToolsPutVisibility(er, text.empty() ? 1 : 0);
        reinterpret_cast<IUnknown*>(er)->Release();
    }
}

// Defined once the row model is available (DevToolsWindow.KindModel.inc / Expansion.inc).
static bool OriginalFor(const std::wstring& prop, const std::wstring& preVal, EditRecord& out);
static void UpdateEditedUi(const std::wstring& prop);
// Read the element's edited properties now and compare them with their records (DevToolsWindow.LiveProps.inc).
static void CheckEditRecordsNow(InstanceHandle wire);

// A DevTools write can change other properties of the same element (Padding.Left changes Padding), so the values
// read right after it become every record's new baseline instead of looking like the app changed them.
static void RebaselineRecords(InstanceHandle wire)
{
    for (auto& e : g_editRecords) if (e.wire == wire) e.needsBaseline = true;
    CheckEditRecordsNow(wire);
}

static void NoteChange(const std::wstring& prop, const std::wstring& preVal, const std::wstring& newVal)
{
    if (EditRecord* rec = DirtyFind(prop)) {
        // An Unset original is not restored by typing the value it showed: the property would still be set here.
        if (rec->kind != DevToolsOriginal::Unset && newVal == rec->original) DirtyErase(prop);
    } else {
        // After an app change, the editor may still hold the value it opened with; the app's value is what was
        // really there before this edit.
        const AppChange* app = AppChangedFind(g_selWire, prop);
        const std::wstring before = app ? app->value : preVal;
        EditRecord first;
        if (newVal != before && OriginalFor(prop, before, first)) g_editRecords.push_back(std::move(first));
    }
    AppChangedErase(g_selWire, prop);
    RebaselineRecords(g_selWire);
    UpdateEditedUi(prop);
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
    // An app change made since the last read must be seen as the app's before this write's baseline replaces it.
    CheckEditRecordsNow(wire);
    g_lastWriteReason.clear();
    const DevToolsWriteOutcome oc = g_ctx.writeFn(wire, prop, type, value, confirmed, &g_lastWriteReason);
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

// The last commit's outcome, so a flush can say whether the edit it saved actually landed.
static unsigned             g_commitSeq = 0;
static DevToolsWriteOutcome g_lastCommitOc = DevToolsWriteOutcome::Ok;
static std::wstring         g_lastCommitWhy;

static std::wstring WriteFailureText(const std::wstring& prop)
{
    // The tap's reason is "token|message"; the message is what the row shows.
    const size_t bar = g_lastWriteReason.find(L'|');
    const std::wstring why = bar == std::wstring::npos ? g_lastWriteReason : g_lastWriteReason.substr(bar + 1);
    return why.empty() ? L"Couldn't set " + prop : why;
}

static bool ApplyCommitOutcome(DevToolsWriteOutcome oc, InstanceHandle wire,
                               const std::wstring& prop, const std::wstring& type,
                               const std::wstring& preVal, const std::wstring& val,
                               const EditRecord* captured = nullptr)
{
    WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::WinPropCommit);
    ++g_commitSeq;
    g_lastCommitOc = oc;
    g_lastCommitWhy = oc == DevToolsWriteOutcome::Failed ? WriteFailureText(prop) : std::wstring();
    if (wire != g_selWire) {
        // A late save to an element that is no longer selected: its records' baselines must follow the write, and a
        // first edit still needs a record so Restore can put back what the editor's element had. `captured` was
        // taken while that element was selected, so it knows whether the value was set there or came from a resource.
        if (oc == DevToolsWriteOutcome::Ok && val != preVal && !RecordFind(wire, prop)) {
            EditRecord rec;
            if (OriginalFromAppChange(wire, prop, rec)) g_editRecords.push_back(std::move(rec));
            else if (captured) g_editRecords.push_back(*captured);
            AppChangedErase(wire, prop);
        }
        if (DevToolsWriteLanded(oc)) RebaselineRecords(wire);
        return DevToolsWriteLanded(oc);
    }
    if (oc != DevToolsWriteOutcome::Failed && g_writeErrorWire == wire && g_writeErrorPath == prop) ShowWriteError(prop, L"");
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
        AppChangedErase(wire, prop);
        NoteBindingLost(prop);
        RebaselineRecords(wire);
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
            SetTextOn(reinterpret_cast<IInspectable*>(ct), ask);
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
    default: {
        const std::wstring text = g_lastCommitWhy;
        ShowWriteError(prop, text);
        SetStatusText(text);
        return false;
    }
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
        if (gen != g_route1Gen || retired) return S_OK;
        if (!g_ctx.readInputFn || !g_ctx.writeFn || !box) return S_OK;
        std::wstring text;
        if (!g_ctx.readInputFn(box, text)) {
            DevToolsOverlayLog(L"route1.input read failed for '%s'", prop.c_str());
            if (selWire == g_selWire) SetStatusText(L"Couldn't read the " + prop + L" editor");
            return S_OK;
        }
        std::wstring value;
        if (!DevToolsEditText::ValueToWrite(text, hasShown ? &shown : nullptr, original, &value)) return S_OK;
        if (ApplyCommitOutcome(CommitWrite(selWire, prop, type, value, /*confirmBindingReplace*/ false),
                               selWire, prop, type, original, value, recordable ? &first : nullptr)) {
            original = value; shown = text; hasShown = true;
        }
        return S_OK;
    }
    void Init(InstanceHandle w, const std::wstring& p, const std::wstring& t, const std::wstring& orig, IInspectable* b)
    {
        selWire = w; prop = p; type = t; original = orig; box = b;
        hasShown = box && g_ctx.readInputFn && g_ctx.readInputFn(box, shown);
        recordable = w == g_selWire && OriginalFor(p, orig, first);
        InitSink(IID_RoutedEventHandler_Del, g_route1Gen);
    }
    InstanceHandle selWire = 0;
    std::wstring   prop, type, original, shown;
    bool           hasShown = false;
    // What Restore puts back if this box's save lands after the selection moved to another element.
    EditRecord     first;
    bool           recordable = false;
    // Set once the pane that owns the box is rebuilt: the flush already saved the box, and a late focus-out
    // from the removed box would write its text over whatever the rebuild just did (for example, Restore).
    bool           retired = false;
    IInspectable*  box = nullptr;
};

// The pane's text editors, so an edit can be saved before the selection changes or the pane is rebuilt, even
// when focus never leaves the box (UI Automation can select another element or invoke Restore directly).
struct PaneEditor
{
    std::wstring          path;
    IUnknown*             ident = nullptr;
    bool*                 retired = nullptr;
    LostFocusSink*        text = nullptr;
    std::function<void()> commit;
};
static std::vector<PaneEditor> g_paneEditors;
static bool g_flushingEditor = false;

static void RegisterPaneEditor(const std::wstring& path, void* element, bool* retired, LostFocusSink* text,
                               std::function<void()> commit)
{
    if (!element) return;
    IUnknown* ident = nullptr;
    if (FAILED(reinterpret_cast<IUnknown*>(element)->QueryInterface(IID_IUnknown, reinterpret_cast<void**>(&ident))) || !ident)
        return;
    g_paneEditors.push_back({ path, ident, retired, text, std::move(commit) });
}

// A row's expansion is being rebuilt: its old boxes are leaving the registry but stay live, so a focus-out
// still saves them as it did before.
static void ForgetPaneEditors(const std::wstring& path)
{
    for (size_t i = g_paneEditors.size(); i-- > 0; ) {
        if (g_paneEditors[i].path != path) continue;
        if (g_paneEditors[i].ident) g_paneEditors[i].ident->Release();
        g_paneEditors.erase(g_paneEditors.begin() + i);
    }
}

// `retire` when the pane is rebuilt for the same element: the flush already saved the boxes, and a late focus-out
// would write over what the rebuild shows (for example, Restore). When the selection moves to another element the
// boxes stay live, so a focus-out that arrives after the move still saves to the element its box was built for.
static void ReleasePaneEditors(bool retire)
{
    for (auto& e : g_paneEditors) {
        if (retire && e.retired) *e.retired = true;
        if (e.ident) e.ident->Release();
    }
    g_paneEditors.clear();
}
static void RetirePaneEditors() { ReleasePaneEditors(/*retire*/ true); }

// Save the property editor that has keyboard focus, as leaving it would. Returns what happened, for the status
// line, or an empty string when no editor was focused or its text was unchanged.
static std::wstring FlushFocusedEditor()
{
    if (g_flushingEditor || g_paneEditors.empty() || !g_inspectorContent) return std::wstring();
    std::function<void()> commit;
    std::wstring path;
    try {
        winrt::Windows::Foundation::IInspectable object{ nullptr };
        winrt::copy_from_abi(object, g_inspectorContent);
        auto root = object.as<DevToolsX::FrameworkElement>();
        auto focused = DevToolsXI::FocusManager::GetFocusedElement(root.XamlRoot());
        if (!focused) return std::wstring();
        const auto ident = focused.as<winrt::Windows::Foundation::IUnknown>();
        for (const auto& e : g_paneEditors)
            if (e.ident == static_cast<IUnknown*>(winrt::get_abi(ident))) { commit = e.commit; path = e.path; break; }
    } catch (const winrt::hresult_error&) {
        return std::wstring();
    }
    if (!commit) return std::wstring();
    const unsigned seq = g_commitSeq;
    const std::wstring label = g_selLabel;
    g_flushingEditor = true;
    commit();
    g_flushingEditor = false;
    if (seq == g_commitSeq) return std::wstring();
    switch (g_lastCommitOc) {
    case DevToolsWriteOutcome::Ok:
    case DevToolsWriteOutcome::ReplacedBinding:
        return L"Saved " + path + L" on " + label;
    case DevToolsWriteOutcome::NeedsConfirm:
        return L"Didn't save " + path + L" on " + label + L". It needs confirmation";
    default:
        return L"Couldn't save " + path + L" on " + label + L": " + g_lastCommitWhy;
    }
}

// Defined once the row model is available (DevToolsWindow.KindModel.inc).
static void ClearRowValue(InstanceHandle wire, const std::wstring& prop);
static void RestoreRowValue(InstanceHandle wire, const std::wstring& prop, const std::wstring& type);
static std::wstring CurrentEditSeed(const std::wstring& prop);

// Clear the captured node/property through ClearProperty, then reread inheritance.
// Clearing can remove a binding/resource link and is distinct from restoring a prior literal.
// Capture identity so a pane rebuild cannot redirect the action. UI-thread Button.Click.
struct ResetSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen) return S_OK;
        if (!g_ctx.clearFn) return S_OK;
        ClearRowValue(selWire, prop);
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

// Put back what the property was before the session's first edit: a value, "not set here", or a resource.
// Refuse when a binding was replaced: restoring its resolved literal cannot restore the expression.
struct UndoSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen) return S_OK;
        RestoreRowValue(selWire, prop, type);
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
        const std::wstring preVal = selWire == g_selWire ? CurrentEditSeed(prop) : std::wstring();
        ApplyCommitOutcome(CommitWrite(selWire, prop, type, val, /*confirmBindingReplace*/ true),
                           selWire, prop, type, preVal, val);
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

// Left and Right move the focused splitter; Shift moves it further.
struct SplitterKeySink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void* args)
    {
        if (gen != g_route1Gen || !args || !g_splitPaneFe) return S_OK;
        int key = 0;
        if (FAILED(DevToolsGetKey(args, &key)) || (key != VK_LEFT && key != VK_RIGHT)) return S_OK;
        if (g_splitTarget <= 0.0) {
            double cur = 0.0;
            if (FAILED(DevToolsGetActualWidth(g_splitPaneFe, &cur)) || cur <= 0.0) return S_OK;
            g_splitTarget = cur;
        }
        const double step = (GetKeyState(VK_SHIFT) & 0x8000) ? 64.0 : 16.0;
        g_splitTarget += key == VK_LEFT ? -step : step;
        ClampInspectorSplit();
        DevToolsPutHandled(args, true);
        return S_OK;
    }
    void Init() { InitSink(IID_KeyEventHandler_Del, g_route1Gen); }
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
