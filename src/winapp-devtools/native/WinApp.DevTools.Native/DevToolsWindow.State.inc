// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsWindow.cpp; included into that translation unit, not compiled on its own.

struct WindowContext
{
    DevToolsWindowRowsFn         rowsFn;
    DevToolsWindowReadInputFn    readInputFn;
    DevToolsWindowWriteFn        writeFn;
    DevToolsWindowClearFn        clearFn;
    DevToolsWindowResolveFn      resolveFn;
    DevToolsWindowBindFn         bindFn;
    DevToolsWindowSourceFn       sourceFn;
    DevToolsWindowLayoutFn       layoutFn;
    DevToolsWindowHighlightFn    highlightFn;
    DevToolsWindowWireLiveFn     wireLiveFn = nullptr;
    DevToolsWindowCensusCountFn  censusCountFn = nullptr;
    // Re-read the app's flattened census on demand. Without it the row list can only ever describe the
    // tree as it was when the window opened, and a pick on a page the app navigated to resolves against a
    // tree that never contained the element -- which reads as a broken PICK rather than a stale tree.
    DevToolsWindowSnapshotFn     snapshotFn = nullptr;
    DevToolsWindowPreviewFn      previewFn = nullptr;
    IInspectable* propsHostChildren = nullptr;
};

static void ShowPropsForNode(InstanceHandle wire, const std::wstring& label, const std::wstring& breadcrumb,
                             bool force = false);

// The props pane is a snapshot. Re-reading it rebuilds the pane, so that waits while an editor is open or a
// write is unconfirmed.
static bool CanRereadSelection();

static void SelectRowVisual(void* rowControl);

static unsigned g_route1Gen = 0;
static unsigned g_treeRowsGen = 0;

// WIRE handle of the currently selected tree node (set on select in ShowPropsForNode, cleared on teardown).
// Declared up here so the per-node hover-exit sink below can revert the app highlight back to the selected
// node when the pointer leaves a row (0 = nothing selected -> clear the highlight). The rest of the editable
// selection state lives in the inspector-state block further down.
static InstanceHandle g_selWire = 0;

// WIRE handle the tree ROW selection currently reflects. Distinct from g_selWire, which tracks the
// props pane: this one is the dedupe key for programmatic selection, so an in-app pick that lands on the
// element already selected costs nothing (sticky pick fires this continuously as the cursor moves).
static InstanceHandle g_selectedWire = 0;

// Pointer-exit highlight reverts are posted so row-to-row movement can be canceled by the following enter.
static UINT_PTR g_hoverRevertTimer = 0;
static unsigned g_hoverEpoch = 0;
static unsigned g_hoverRevertEpoch = 0;
static unsigned g_hoverRevertGen = 0;
static void CALLBACK HoverRevertProc(HWND, UINT, UINT_PTR id, DWORD);

static void SetFocusNotice(const std::wstring& text);

struct NodeClickSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void* sender, void* /*args*/)
    {
        // Inert once the owning window has been torn down (stale generation) -- guards against a click that
        // races window close, or a leaked sink from a prior window, touching a freed subtree.
        if (gen != g_route1Gen || rowsGen != g_treeRowsGen) return S_OK;
        if (!ctx) return S_OK;
        if (hoverExit) {
            g_hoverRevertEpoch = ++g_hoverEpoch;
            g_hoverRevertGen = g_route1Gen;
            if (!g_hoverRevertTimer) g_hoverRevertTimer = SetTimer(nullptr, 0, USER_TIMER_MINIMUM, HoverRevertProc);
            return S_OK;
        }
        if (hoverOnly) {
            // The per-row-crossing cost the `hover` scenario exists to measure. The exit branch above
            // is deliberately outside it: an exit only posts a timer, and charging that to the crossing would
            // bury the one thing worth seeing, which is what the enter's highlight costs.
            WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::WinTreeHover);
            ++g_hoverEpoch;
            if (ctx->highlightFn) ctx->highlightFn(handle, false);
            // Hover stops at the highlight -- rebuilding the property pane on every PointerEntered would
            // jank the app UI thread. Only a click (select) reads the props and rebuilds the inspector.
            return S_OK;
        }
        SetFocusNotice(L"");
        if (ctx->highlightFn) ctx->highlightFn(handle, true);
        void* rowCtrl = nullptr;
        if (sender && SUCCEEDED(reinterpret_cast<IUnknown*>(sender)->QueryInterface(
                DevToolsIid<DevToolsXC::IControl>(), &rowCtrl)) && rowCtrl) {
            SelectRowVisual(rowCtrl);
            reinterpret_cast<IUnknown*>(rowCtrl)->Release();
        }
        g_selectedWire = handle;
        // Clicking the selected row again re-reads it, unless that would discard an open editor or pending write.
        ShowPropsForNode(handle, label, breadcrumb, /*force*/ handle == g_selWire && CanRereadSelection());
        return S_OK;
    }
    void Init(InstanceHandle h, const std::wstring& lbl, const std::wstring& bc, WindowContext* c, bool hover)
    {
        handle = h; label = lbl; breadcrumb = bc; ctx = c; hoverOnly = hover; rowsGen = g_treeRowsGen;
        InitSink(IID_RoutedEventHandler_Del, g_route1Gen, &IID_PointerEventHandler_Del);
    }
    InstanceHandle handle = 0;
    std::wstring   label;
    std::wstring   breadcrumb;
    WindowContext* ctx = nullptr;
    bool           hoverOnly = false;
    bool           hoverExit = false;
    unsigned       rowsGen = 0;
};

static IInspectable* g_route1WindowInsp = nullptr;
// Protocol close hides/suspends refresh; WM_CLOSE destroys so app shutdown is not held by a hidden tool window.
static bool g_route1WindowHidden = false;

static bool g_watchdogSawApp = false;
static WindowContext g_ctx;

// the window's own binding-path walk. Set by DevToolsWindow_SetPathWalk, read from the worker
// that asks. Declared here rather than beside the comment bridge below because the property expansion, which
// is what asks, is built long before that point in this file.
// Path-walk callbacks are declared early because property expansions can ask before comment bridge code.
static DevToolsWindowWalkFn g_walkFn;
static IUnknown*     g_inspectorXamlRoot = nullptr;
static IInspectable* g_inspectorContent = nullptr;
static IUnknown* g_captionTheme = nullptr;
static IUnknown* g_captionThemeRoot = nullptr;
static __int64 g_captionThemeToken = 0;
static bool g_captionThemeSubscribed = false;

static HRESULT SyncInspectorCaptionTheme()
{
    if (!g_captionTheme || !g_captionThemeRoot) return S_FALSE;
    int theme = 0;
    const HRESULT hr = DevToolsGetActualTheme(g_captionThemeRoot, &theme);
    if (FAILED(hr)) return hr;
    const auto captionTheme = theme == static_cast<int>(DevToolsX::ElementTheme::Light)
        ? DevToolsW::TitleBarTheme::Light
        : theme == static_cast<int>(DevToolsX::ElementTheme::Dark)
        ? DevToolsW::TitleBarTheme::Dark : DevToolsW::TitleBarTheme::UseDefaultAppMode;
    return DevToolsTitleBarPutPreferredTheme(g_captionTheme, captionTheme);
}

static HRESULT ReleaseInspectorCaptionTheme()
{
    winrt::com_ptr<IUnknown> root, caption;
    root.attach(g_captionThemeRoot); g_captionThemeRoot = nullptr;
    caption.attach(g_captionTheme); g_captionTheme = nullptr;
    const bool subscribed = g_captionThemeSubscribed;
    const __int64 token = g_captionThemeToken;
    g_captionThemeSubscribed = false;
    g_captionThemeToken = 0;
    const HRESULT hr = subscribed && root ? DevToolsRemoveActualThemeChanged(root.get(), token) : S_OK;
    if (FAILED(hr)) DevToolsOverlayLog(L"route1.titlebar remove_ActualThemeChanged hr=0x%08X", hr);
    return hr;
}

struct InspectorCaptionThemeSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen) return S_OK;
        const HRESULT hr = SyncInspectorCaptionTheme();
        if (FAILED(hr)) DevToolsOverlayLog(L"route1.titlebar theme update hr=0x%08X", hr);
        return S_OK;
    }
    void Init()
    {
        using Changed = winrt::Windows::Foundation::TypedEventHandler<
            DevToolsX::FrameworkElement, winrt::Windows::Foundation::IInspectable>;
        InitSink(DevToolsIid<Changed>(), g_route1Gen);
    }
};

static int InspectorActualTheme()
{
    int theme = 0;
    if (!g_inspectorContent) return theme;
    void* fe = nullptr;
    if (SUCCEEDED(g_inspectorContent->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) && fe) {
        DevToolsGetActualTheme(fe, &theme);
        reinterpret_cast<IUnknown*>(fe)->Release();
    }
    return theme;
}

static void CALLBACK HoverRevertProc(HWND, UINT, UINT_PTR id, DWORD)
{
    KillTimer(nullptr, id);
    g_hoverRevertTimer = 0;
    if (g_hoverRevertGen != g_route1Gen) return;
    if (g_hoverEpoch != g_hoverRevertEpoch) return;
    // Scoped here, past both early-outs, so `calls` counts reverts that were APPLIED rather than timers that
    // fired. A cancelled revert costs nothing and must not read as one that did work.
    WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::WinHoverRevert);
    if (g_ctx.highlightFn) g_ctx.highlightFn(g_selWire, false);
    DevToolsOverlayLog(L"route1.hover pointer left the tree -> highlight back to selection %llu",
                  (unsigned long long)g_selWire);
}

struct EditRow { std::wstring prop; std::wstring type; std::wstring original; IInspectable* box = nullptr; };
static std::vector<EditRow> g_editRows;
static IInspectable*        g_statusTb = nullptr;
// Props-pane breadcrumb: the elided one-line chain and the full wrapped chain, both QI'd to IUIElement
// and AddRef-held for the pane's lifetime so BreadcrumbSink can flip between them with put_Visibility.
static void*                g_bcShort = nullptr;
static void*                g_bcFull = nullptr;
static void*                g_bcToggleLbl = nullptr; // the expander's label TextBlock (ITextBlock, AddRef'd)
static std::wstring         g_bcExpandText;
static bool                 g_bcExpanded = false;
// g_selWire (WIRE handle of the currently selected node) is declared above NodeClickSink so the hover-exit
// sink can revert the app highlight to it.
static std::wstring         g_selLabel;
static std::wstring         g_selBreadcrumb;
static std::wstring         g_selSourceFile;
static unsigned int         g_selSourceLine = 0;

// Dirty tracking records what this pane changed because runtime local values cannot recover resource keys.
static InstanceHandle g_dirtyNode = 0;
static std::vector<std::pair<std::wstring, std::wstring>> g_dirtySeed;
// Resolve a named child of the REALIZED row for `prop`, AddRef'd, or null when that row is not currently on
// screen. Defined with the repeater below. Looked up live rather than cached because a virtualized row's
// elements are released the moment it leaves the viewport, and a cached pointer would outlive the element.
static void* PaneRowChild(const std::wstring& prop, const wchar_t* childName);

static size_t RebuildPaneItems();

// Set and Computed views persist across selections, resetting on window teardown.
// Computed groups effective state by concern; Set groups authored/local values by origin.
enum class DevToolsPropView { Computed, Set };
static DevToolsPropView g_propView = DevToolsPropView::Computed;
static bool ShowingSetView() { return g_propView == DevToolsPropView::Set; }

static std::wstring g_propFilterQuery;

static bool g_propShowAll = false;
static void* g_propShowAllLbl = nullptr;

static void* g_propCountTb = nullptr;
static IInspectable* g_propCountEl = nullptr;
static size_t g_propRowTotal = 0;

static void* g_propEmptyUi = nullptr;
static void* g_propEmptyLbl = nullptr;
static void* g_propEmptyClearUi = nullptr;

static void SetAutomationTextOn(IInspectable* el, const std::wstring& name, const std::wstring& id,
                                const std::wstring& controlType);

static std::wstring g_propFilterRaw;

static void UpdatePropEmptyState(size_t shown)
{
    if (!g_propEmptyUi) return;
    const bool empty = (shown == 0);
    DevToolsPutVisibility(g_propEmptyUi, empty ? 0 : 1);
    const bool filtering = !g_propFilterQuery.empty();
    if (g_propEmptyClearUi) DevToolsPutVisibility(g_propEmptyClearUi, (empty && filtering) ? 0 : 1);
    if (!empty || !g_propEmptyLbl) return;
    const std::wstring line = filtering
        ? (L"No property matches \u201C" + g_propFilterRaw + L"\u201D.")
        : std::wstring(L"There are no properties to show here.");
    HSTRING hs = MakeStr(line);
    DevToolsPutText(g_propEmptyLbl, hs);
    WindowsDeleteString(hs);
}

static void UpdatePropCount(size_t shown)
{
    UpdatePropEmptyState(shown);
    if (!g_propCountTb) return;
    HSTRING hs = MakeStr(std::to_wstring(shown) + L" of " + std::to_wstring(g_propRowTotal));
    DevToolsPutText(g_propCountTb, hs);
    WindowsDeleteString(hs);
    if (g_propCountEl)
        SetAutomationTextOn(g_propCountEl,
                            L"Showing " + std::to_wstring(shown) + L" of " +
                            std::to_wstring(g_propRowTotal) + L" properties",
                            L"WinAppDevToolsPropCount", L"");
}
static void* g_propFilterTextBox = nullptr;

static void ReleasePropFilterState()
{
    if (g_propCountTb) { reinterpret_cast<IUnknown*>(g_propCountTb)->Release(); g_propCountTb = nullptr; }
    if (g_propCountEl) { g_propCountEl->Release(); g_propCountEl = nullptr; }
    if (g_propShowAllLbl) { reinterpret_cast<IUnknown*>(g_propShowAllLbl)->Release(); g_propShowAllLbl = nullptr; }
    if (g_propFilterTextBox) { reinterpret_cast<IUnknown*>(g_propFilterTextBox)->Release(); g_propFilterTextBox = nullptr; }
    if (g_propEmptyUi) { reinterpret_cast<IUnknown*>(g_propEmptyUi)->Release(); g_propEmptyUi = nullptr; }
    if (g_propEmptyLbl) { reinterpret_cast<IUnknown*>(g_propEmptyLbl)->Release(); g_propEmptyLbl = nullptr; }
    if (g_propEmptyClearUi) { reinterpret_cast<IUnknown*>(g_propEmptyClearUi)->Release(); g_propEmptyClearUi = nullptr; }
    g_propRowTotal = 0;
}

// A property is "set here" (in code/XAML on this element) when its winning value comes from a Local assignment
// or a Binding. Everything else (Style/Built-in style/Inherited/Default/Trigger/Animation/Visual state) came from
// somewhere other than this element's own declaration -- the exact set-in-code vs inherited distinction.
static bool SourceIsSetHere(const std::wstring& s) { return s == L"Local" || s == L"Binding"; }

static std::wstring* DirtyFind(const std::wstring& p) { for (auto& e : g_dirtySeed) if (e.first == p) return &e.second; return nullptr; }
static bool DirtyHas(const std::wstring& p) { return DirtyFind(p) != nullptr; }
static void DirtyErase(const std::wstring& p) { for (size_t i = 0; i < g_dirtySeed.size(); ++i) if (g_dirtySeed[i].first == p) { g_dirtySeed.erase(g_dirtySeed.begin() + i); return; } }

static std::vector<std::wstring> g_bindingLost;
static bool BindingLost(const std::wstring& p) { for (const auto& e : g_bindingLost) if (e == p) return true; return false; }
static void NoteBindingLost(const std::wstring& p) { if (!BindingLost(p)) g_bindingLost.push_back(p); }

// Track authored resource links and links replaced in this selection. Runtime local-value bits
// cannot recover a resolved resource key, so the pane's authoredKind supplies the pre-write
// confirmation gate. Replacing a link stops theme updates and cannot reattach the original link.
static std::vector<std::wstring> g_linkedRows;
static std::vector<std::wstring> g_linkLost;
static bool ResourceBacked(const std::wstring& p) { for (const auto& e : g_linkedRows) if (e == p) return true; return false; }
static bool LinkLost(const std::wstring& p) { for (const auto& e : g_linkLost) if (e == p) return true; return false; }
static void NoteLinkLost(const std::wstring& p) { if (!LinkLost(p)) g_linkLost.push_back(p); }


struct PendingWrite { std::wstring prop, type, value; };
static std::vector<PendingWrite> g_pendingWrites;
static PendingWrite* PendingFind(const std::wstring& p) { for (auto& e : g_pendingWrites) if (e.prop == p) return &e; return nullptr; }
static void PendingErase(const std::wstring& p) { for (size_t i = 0; i < g_pendingWrites.size(); ++i) if (g_pendingWrites[i].prop == p) { g_pendingWrites.erase(g_pendingWrites.begin() + i); return; } }

enum class DevToolsKind { Literal = 0, Theme = 1, Static = 2, Binding = 3, XBind = 4 };
static const int kDevToolsKindCount = 5;

static const wchar_t* KindLabel(DevToolsKind k)
{
    switch (k) {
    case DevToolsKind::Theme:   return L"{ThemeResource}";
    case DevToolsKind::Static:  return L"{StaticResource}";
    case DevToolsKind::Binding: return L"{Binding}";
    case DevToolsKind::XBind:   return L"{x:Bind}";
    default:               return L"Literal";
    }
}
static bool KindIsResource(DevToolsKind k) { return k == DevToolsKind::Theme || k == DevToolsKind::Static; }

// Irreversible transitions: replacing Binding/x:Bind loses the expression, leaving a resource
// loses its link, and installing a native graft drops the pane's prior-value seed. Disclose these
// transitions; a remembered resolved literal is not a restorable binding.
static bool KindChangeIsIrreversible(DevToolsKind from, DevToolsKind to)
{
    if (from == DevToolsKind::XBind || from == DevToolsKind::Binding) return true;
    if (KindIsResource(from) && !KindIsResource(to))        return true;
    if (to == DevToolsKind::Binding)                             return true;
    return false;
}

static std::wstring CommitLabelFor(DevToolsKind from, DevToolsKind to, bool noSeed)
{
    if (from == DevToolsKind::XBind)                      return L"Replace the x:Bind";
    if (from == DevToolsKind::Binding)                    return L"Replace the {Binding}";
    if (KindIsResource(from) && !KindIsResource(to)) return L"Replace the resource link";
    if (to == DevToolsKind::Binding)                      return L"Graft the {Binding}";
    if (noSeed)                                      return L"Apply without an undo";
    return L"Apply";
}

static const wchar_t* kXBindWhy = L"compiled at build time - no runtime API creates one";

struct SessionAssign
{
    std::wstring prop;
    DevToolsKind      kind = DevToolsKind::Literal;
    std::wstring detail;
};
static std::vector<SessionAssign> g_sessionAssign;
static const SessionAssign* SessionAssignFind(const std::wstring& p)
{
    for (const auto& e : g_sessionAssign) if (e.prop == p) return &e;
    return nullptr;
}
static void NoteSessionAssign(const std::wstring& p, DevToolsKind k, const std::wstring& detail)
{
    for (auto& e : g_sessionAssign) if (e.prop == p) { e.kind = k; e.detail = detail; return; }
    g_sessionAssign.push_back({ p, k, detail });
}
static void SessionAssignErase(const std::wstring& p)
{
    for (size_t i = 0; i < g_sessionAssign.size(); ++i)
        if (g_sessionAssign[i].prop == p) { g_sessionAssign.erase(g_sessionAssign.begin() + i); return; }
}

static std::vector<std::pair<std::wstring, DevToolsKind>> g_pendingKind;
static const DevToolsKind* PendingKindFind(const std::wstring& p)
{
    for (const auto& e : g_pendingKind) if (e.first == p) return &e.second;
    return nullptr;
}
static void PendingKindSet(const std::wstring& p, DevToolsKind k)
{
    for (auto& e : g_pendingKind) if (e.first == p) { e.second = k; return; }
    g_pendingKind.push_back({ p, k });
}
static void PendingKindErase(const std::wstring& p)
{
    for (size_t i = 0; i < g_pendingKind.size(); ++i)
        if (g_pendingKind[i].first == p) { g_pendingKind.erase(g_pendingKind.begin() + i); return; }
}

// Per-editable-row handles on the expression line, so the confirm prompt can be raised and dismissed WITHOUT
// rebuilding the pane (a rebuild here would destroy the TextBox the user just typed into and re-seed it with
// the old value, which reads as "my edit was thrown away"). `text` is the normal expression TextBlock, `btn`
// the Collapsed confirm Button that replaces it in the same grid cell, `label` the Button's inner TextBlock so
// the prompt can name the value being written. All AddRef'd; released on every pane rebuild.

// Drop the props-pane breadcrumb TextBlock refs. Called on every pane rebuild and on window teardown,
// so the sink can never flip Visibility on a released element.
static void ReleaseBreadcrumbRefs()
{
    if (g_bcShort) { reinterpret_cast<IUnknown*>(g_bcShort)->Release(); g_bcShort = nullptr; }
    if (g_bcFull)  { reinterpret_cast<IUnknown*>(g_bcFull)->Release();  g_bcFull  = nullptr; }
    if (g_bcToggleLbl) { reinterpret_cast<IUnknown*>(g_bcToggleLbl)->Release(); g_bcToggleLbl = nullptr; }
}

// The Size & spacing section's disclosure, remembered across panes and runs.
static int g_layoutSectionOpen = -1;
static bool LayoutSectionOpen()
{
    if (g_layoutSectionOpen < 0) g_layoutSectionOpen = DevToolsSettingsGetBool(L"InspectorSizeSpacingOpen", false) ? 1 : 0;
    return g_layoutSectionOpen == 1;
}
static IInspectable* g_layoutBody = nullptr;
static IInspectable* g_layoutBodyChev = nullptr;
static IInspectable* g_axmlOne  = nullptr;
static IInspectable* g_axmlFull = nullptr;
static IInspectable* g_axmlChev = nullptr;
static bool g_axmlExpanded = false;
static void ReleaseLayoutSectionRefs()
{
    if (g_layoutBody) { g_layoutBody->Release(); g_layoutBody = nullptr; }
    if (g_layoutBodyChev) { g_layoutBodyChev->Release(); g_layoutBodyChev = nullptr; }
    if (g_axmlOne)  { g_axmlOne->Release();  g_axmlOne  = nullptr; }
    if (g_axmlFull) { g_axmlFull->Release(); g_axmlFull = nullptr; }
    if (g_axmlChev) { g_axmlChev->Release(); g_axmlChev = nullptr; }
}

static IInspectable* g_selBrush   = nullptr;
static IInspectable* g_clearBrush = nullptr;
static bool          g_selBrushTried = false;
static void*         g_selCtrl    = nullptr; // currently-selected row's Button (QI'd IControl, AddRef'd)

// Parse the two selection brushes with the app's XamlReader on first use. Best-effort: if parsing fails the
// selection just won't tint (no crash) -- SelectRowVisual guards on g_selBrush.
static void EnsureSelBrushes()
{
    if (g_selBrush || g_selBrushTried) return;
    g_selBrushTried = true;
    const wchar_t* kPres = L"http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    std::wstring sel = std::wstring(L"<SolidColorBrush xmlns=\"") + kPres +
        L"\" Color=\"{ThemeResource SystemAccentColor}\" Opacity=\"0.35\"/>";
    std::wstring clr = std::wstring(L"<SolidColorBrush xmlns=\"") + kPres + L"\" Color=\"Transparent\"/>";
    IInspectable* s = nullptr; if (SUCCEEDED(LoadMarkupWin(sel, &s))) g_selBrush = s;
    IInspectable* c = nullptr; if (SUCCEEDED(LoadMarkupWin(clr, &c))) g_clearBrush = c;
}

static void SelectRowVisual(void* rowControl)
{
    EnsureSelBrushes();
    if (!g_selBrush || !g_clearBrush) return;
    if (g_selCtrl == rowControl) return;
    if (g_selCtrl) {
        DevToolsPutBackground(g_selCtrl, g_clearBrush);
        reinterpret_cast<IUnknown*>(g_selCtrl)->Release();
        g_selCtrl = nullptr;
    }
    if (rowControl) {
        DevToolsPutBackground(rowControl, g_selBrush);
        reinterpret_cast<IUnknown*>(rowControl)->AddRef();
        g_selCtrl = rowControl;
    }
}

static std::vector<void*> g_treeRows;      // per rendered row: the Row<i> Grid QI'd to IUIElement (AddRef'd)
static std::vector<void*> g_treeRowFe;
static std::vector<void*> g_treeControls;  // per rendered row: node Button as IControl (AddRef'd; current subtree only)
static std::vector<int>   g_treeDepth;
static std::vector<char>  g_treeCollapsed;
static std::vector<char>  g_treeVisible;
static std::vector<void*> g_treeGlyph;
static std::vector<void*> g_treeChevronToggle;
static std::vector<std::wstring> g_treeLabel;
static std::vector<std::wstring> g_treeShortType;
static std::vector<DevToolsWindowNode> g_allTreeNodes;
static std::vector<char> g_allTreeAppAuthored;
static std::vector<size_t> g_renderedOriginalIndices; // rendered row -> full snapshot index (breadcrumbs/reselection)
static IInspectable* g_treeHostChildren = nullptr;
// Rows are realized LAZILY: only rows the current view shows get an element, so these carry what a
// later realization needs. The rows panel's own Children (AddRef'd) is where a newly realized row is
// INSERTED, at the position that keeps the list in row order.
static IInspectable* g_treeRowsPanelChildren = nullptr;
static std::vector<DevToolsTreeLayout::A11yRow> g_treeA11y;
static std::vector<char> g_treeCollapseAtBuild;
static int g_treeMinDepth = 0;
static std::vector<int> g_treeViewDepth;
static int g_treeIndentBaseline = 0;
static std::vector<NodeClickSink*> g_nodeSinks;
static std::map<InstanceHandle, size_t> g_nodeIdxByWire;
static IInspectable*      g_filterBox = nullptr; // the tree filter TextBox (AddRef'd; read via readInputFn)
static void*              g_filterTextBox = nullptr;
static std::wstring       g_treeFilterQuery;
// The tree list's ScrollViewer as IUIElement (AddRef'd). Declared with the tree state rather than with the
// comments view that also hides/shows it, because the live refresh reads and restores its offset.
static void*              g_treeScrollUi = nullptr;

static void ApplyTreeFilter(const std::wstring& queryRaw);
static void SetStatusText(const std::wstring& s);
// Defined after the row builder it calls. Rows are realized lazily, so every path that makes a row
// visible has to run this first -- a row with no element cannot be shown.
static int EnsureRowsRealized(const std::vector<char>& shouldShow);
static void FocusTargetAppForPick();
static void FocusInspectorAfterPick();
static bool RefreshTreeFromApp(bool refreshValues = false);

static std::vector<char>  g_treeAppAuthored;
static bool  g_jmxActive    = false;
static bool  g_jmxAvailable = false;
static int   g_jmxSrcSeen = 0;
static DevToolsPickRoute g_pickRoute;
static bool  g_focusActive  = false;
static void* g_jmxLbl       = nullptr;
static void* g_jmxBtnCtrl   = nullptr;
static void* g_pickBtnCtrl  = nullptr;
static void* g_pickOnUi     = nullptr; // icon-button active indicator (IUIElement, AddRef'd)
static void* g_focusOnUi    = nullptr; // icon-button active indicator (IUIElement, AddRef'd)
// focus notice: the Border as IUIElement (shown/hidden) and its TextBlock (the message). Both AddRef'd.
static void* g_focusNoticeUi = nullptr;
static void* g_focusNoticeTb = nullptr;
// Tree-header TextBlock + the census population the current row list was built from. The row list is rebuilt
// in place on a tree change, so these carry the population of the CURRENT rows -- and when a rebuild
// fails they are what lets the header admit the list has drifted rather than present it as live.
static void* g_treeHeaderTb = nullptr;
static size_t g_snapshotTotal = 0;
static bool  g_treeStale = false;
static bool  g_treeRefreshing = false;
static bool RowHiddenByJmx(size_t j)
{
    return g_jmxActive && j < g_treeAppAuthored.size() && !g_treeAppAuthored[j];
}

static void SetToggleLabel(void* lbl, const wchar_t* text)
{
    if (!lbl) return;
    HSTRING hs = MakeStr(text);
    DevToolsPutText(lbl, hs);
    WindowsDeleteString(hs);
}
