// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsOverlay.cpp; included into that translation unit, not compiled on its own.

// ============================ Selection morph anchor ============================ One inline panel (quick-edit
// rows + comment box + "open in DevTools") anchored beside the selection. Authored in xaml/selection.xaml +
// selection-panel.xaml, loaded through the same XamlReader loader as the toolbar and driven by name (DevTo.
static IInspectable* g_selUi      = nullptr;
static IInspectable* g_selPopup   = nullptr;
static IInspectable* g_selIcon    = nullptr;
static IInspectable* g_selPanel   = nullptr;
static IInspectable* g_selComment = nullptr;

static void DevToolsSetSelectionPanelRequestedTheme(int requestedTheme)
{
    if (!g_selPanel) return;
    void* fe = nullptr;
    if (SUCCEEDED(g_selPanel->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) && fe) {
        DevToolsPutRequestedTheme(fe, requestedTheme);
        reinterpret_cast<IUnknown*>(fe)->Release();
    }
}

static int           g_selFocusWithin = 0;
static bool          g_selFrozen = false;
static bool          g_selExpanded = false;
static bool          g_selDetached = false;
static bool          g_selRootLost = false;
// a re-anchor that arrives while the panel is open is DEFERRED here, not dropped, and applied when the panel
// closes or focus leaves it.
static RECT          g_selPendingAnchor{};
static bool          g_selAnchorPending = false;
static EventSink     g_selFocusInSink, g_selFocusOutSink;
static EventSink     g_selOpenInSink, g_selOpenOutSink;
static EventSink     g_selIconClickSink;
static IInspectable* g_selTag      = nullptr;
static IInspectable* g_selTarget = nullptr;
static unsigned      g_selGen = 0;
static UINT_PTR      g_selDismissTimer = 0;
static unsigned      g_selDismissGen = 0;
static bool          g_selDismissCommitFailed = false;
static unsigned long long g_selDiagHandle = 0;
static std::vector<IInspectable*> g_selRowBoxes;

struct DevToolsSelPending { std::wstring prop, type, value; };
static std::vector<DevToolsSelPending> g_selPending;
static DevToolsSelPending* DevToolsSelPendingFind(const std::wstring& p) { for (auto& e : g_selPending) if (e.prop == p) return &e; return nullptr; }
static void DevToolsSelPendingErase(const std::wstring& p) { for (size_t i = 0; i < g_selPending.size(); ++i) if (g_selPending[i].prop == p) { g_selPending.erase(g_selPending.begin() + i); return; } }
// AddRef'd; released in ClearSelectionAnchor with g_selRowBoxes.
// Row handles are AddRef'd and released with the selection anchor lifetime.
struct DevToolsSelExprRefs { std::wstring prop; void* text = nullptr; void* textTb = nullptr; void* btn = nullptr; void* label = nullptr; };
static std::vector<DevToolsSelExprRefs> g_selExprRefs;
static DevToolsSelExprRefs* DevToolsSelExprFind(const std::wstring& p) { for (auto& e : g_selExprRefs) if (e.prop == p) return &e; return nullptr; }
static void DevToolsSelReleaseExprRefs()
{
    for (auto& e : g_selExprRefs) {
        if (e.text)   reinterpret_cast<IUnknown*>(e.text)->Release();
        if (e.textTb) reinterpret_cast<IUnknown*>(e.textTb)->Release();
        if (e.btn)    reinterpret_cast<IUnknown*>(e.btn)->Release();
        if (e.label)  reinterpret_cast<IUnknown*>(e.label)->Release();
    }
    g_selExprRefs.clear();
}

// HSTRING from a wide string, for the two runtime put_Text calls below (the card otherwise authors all its text
// in markup). Caller WindowsDeleteStrings it.
static HSTRING DevToolsSelMakeStr(const std::wstring& s)
{
    HSTRING h = nullptr;
    WindowsCreateString(s.c_str(), (UINT32)s.size(), &h);
    return h;
}
static EventSink     g_selOpenClickSink;
static EventSink     g_selKeyDownSink;
static bool          g_selFocusedIsComment = false;
static InstanceHandle g_selPendingHandle = 0;
static InstanceHandle g_selHandle = 0;
static unsigned long long g_selCommentWire = 0;
static void SetCommentTarget(InstanceHandle raw, bool composer);
static std::wstring   g_selCommentId;
static std::wstring   g_selCommentSaved;
// Shown only when the anchor already carries a saved comment (there is nothing to delete otherwise), and hidden
// again the moment the comment is gone, so the button never offers to
static IInspectable* g_selCommentDelete = nullptr;
static EventSink     g_selCommentDeleteSink;
static EventSink     g_selCmtDelInSink, g_selCmtDelOutSink;
static EventSink     g_selCommentSaveSink, g_selCmtSaveInSink, g_selCmtSaveOutSink;
static EventSink     g_selCommentChangedSink;
static RECT           g_selBounds{};

static void OnSelCommentLostFocus(void*, void*);
static void OnSelCommentSaveClick(void*, void*);
static void OnSelCommentTextChanged(void*, void*);
static void OnSelCommentDeleteClick(void*, void*);
static void SelLookupComment(InstanceHandle key, std::wstring* outId, std::wstring* outText);

static const int kSelIconSz = 22;
static const int kSelPanelW = 400;
static const int kSelEditorW = 254;

// Caller Releases the result.
static IInspectable* DevToolsSelFindName(IInspectable* root, const wchar_t* name)
{
    if (!root) return nullptr;
    void* fe = nullptr;
    if (FAILED(root->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) || !fe) return nullptr;
    IInspectable* out = nullptr; HSTRING h = nullptr;
    if (SUCCEEDED(WindowsCreateString(name, (UINT32)wcslen(name), &h))) {
        DevToolsFindName(fe, h, &out);
        WindowsDeleteString(h);
    }
    reinterpret_cast<IUnknown*>(fe)->Release();
    // Names inside the quick peek are not reachable from the selection root once its Popup is lifted.
    if (!out && g_hostLiftedChild && root != reinterpret_cast<IInspectable*>(winrt::get_abi(g_hostLiftedChild)))
        return DevToolsSelFindName(reinterpret_cast<IInspectable*>(winrt::get_abi(g_hostLiftedChild)), name);
    return out;
}

// Subscribe a routed focus event (GotFocus / LostFocus) on a UIElement, EventSink-backed (RoutedEventHandler
// IID).
static void DevToolsSelWireFocus(IInspectable* el, DevToolsUiEvent which, EventSink& sink, void (*cb)(void*, void*))
{
    if (!el) return;
    void* ui = nullptr;
    if (FAILED(el->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) || !ui) return;
    sink.Bind(&IID_RoutedEventHandler_Del, cb);
    __int64 tok = 0;
    HRESULT hr = DevToolsAddUiEvent(ui, which, &sink, &tok);
    reinterpret_cast<IUnknown*>(ui)->Release();
    DevToolsOverlayLog(L"overlay.sel wire-focus event=%s hr=0x%08X", DevToolsUiEventName(which), hr);
}

// Focus-within counter — the single signal that both gates collapse and (later) marks the field we must not
// re-render on a property echo. Logged so the loaded-XAML TextBox focus PROOF is reproducible from the log.
static void OnSelGotFocus(void*, void*)
{
    ++g_selFocusWithin;
    DevToolsOverlayLog(L"overlay.sel focus-in within=%d", g_selFocusWithin);
}
static void OnSelLostFocus(void*, void*)
{
    if (g_selFocusWithin > 0) --g_selFocusWithin;
    DevToolsOverlayLog(L"overlay.sel focus-out within=%d", g_selFocusWithin);
    if (g_selFocusWithin == 0) FlushPendingAnchor();
}

static void DevToolsSelWirePointer(IInspectable* el, DevToolsUiEvent which, EventSink& sink, void (*cb)(void*, void*))
{
    if (!el) return;
    void* ui = nullptr;
    if (FAILED(el->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) || !ui) return;
    sink.Bind(&IID_PointerEventHandler_Del, cb);
    __int64 tok = 0;
    HRESULT hr = DevToolsAddUiEvent(ui, which, &sink, &tok);
    reinterpret_cast<IUnknown*>(ui)->Release();
    DevToolsOverlayLog(L"overlay.sel wire-pointer event=%s hr=0x%08X", DevToolsUiEventName(which), hr);
}


// Dismissal COMMITS FIRST (inline,) and then moves focus to the Close button, so focus is not orphaned when the
// panel leaves the tree and an editor that is still focused blurs through its ordinary path. Teardown is
// deferred to the next UI turn because ClearSelectionAnchor invalidates the row sinks, but nothing the user t.
static bool DevToolsSelFocusIcon();
// Applies every dirty row's (and the comment's) commit INLINE, so a dismiss does not depend on the focus-driven
// LostFocus winning a race it is not guaranteed to win.
static void DevToolsSelFlushCommits();
static bool SelRowCanEdit(const DevToolsCardRow& row);
static void DevToolsSelCommitComment(bool explicitSave = false);
static void DiscardSelectionCommentDraft();
static bool DiscardFailedSelectionCommentDraft();
// accessor rather than reaching for g_selPanel directly, because the catcher's click handler sits above the
// selection globals.
static bool DevToolsSelPanelIsOpen() { return g_selPanel != nullptr; }
static bool g_selDismissVisualClosed = false;
static double g_selDismissBegan = 0;

// What had focus when the quick peek opened. Closing the peek hands focus back with pointer state: left alone,
// WinUI moves focus out of the removed peek with keyboard state, which draws a focus rectangle and opens
// accelerator tips in the app.
static winrt::com_ptr<IInspectable> g_selReturnFocus;

static void SelRememberFocus()
{
    g_selReturnFocus = nullptr;
    winrt::com_ptr<IInspectable> focused;
    bool inPanel = false;
    if (g_selPanel && SUCCEEDED(DevToolsFocusedWithin(g_selPanel, focused.put(), &inPanel)) && !inPanel)
        g_selReturnFocus = std::move(focused);
}

static void SelReturnFocus()
{
    winrt::com_ptr<IInspectable> focused, target = std::move(g_selReturnFocus);
    bool inPanel = false;
    if (!g_selPanel || FAILED(DevToolsFocusedWithin(g_selPanel, focused.put(), &inPanel)) || (focused && !inPanel)) return;
    bool moved = false;
    const HRESULT hr = DevToolsFocusWithPointer(target.get(), g_selPanel, &moved);
    DevToolsOverlayLog(L"overlay.sel focus returned to the app hr=0x%08X moved=%d", hr, (int)moved);
}

// A Ctrl or Alt chord typed in a DevTools panel belongs to the panel. Its editors handle their own (Ctrl+C, Ctrl+A)
// before the key bubbles here; the rest would otherwise reach the app's keyboard accelerators and access keys.
static void KeepChordsInPanel(IInspectable* panelRoot)
{
    if (!panelRoot) return;
    try {
        winrt::Windows::Foundation::IInspectable object{ nullptr };
        winrt::copy_from_abi(object, panelRoot);
        object.as<DevToolsX::UIElement>().KeyDown([](auto&&, DevToolsXI::KeyRoutedEventArgs const& args) {
            if ((GetKeyState(VK_CONTROL) & 0x8000) || (GetKeyState(VK_MENU) & 0x8000)) args.Handled(true);
        });
    } catch (const winrt::hresult_error& e) {
        DevToolsOverlayLog(L"overlay.sel chord guard failed hr=0x%08X", e.code().value);
    }
}

// On the UI layer a windowed Popup's input is hit-tested against the layer as well, so the full-size pick catcher
// would take clicks meant for the open quick peek. While the peek is open there, the catcher keeps only a
// transparent border around the peek's rectangle: clicks outside still dismiss, clicks on the peek reach it.
static void UpdateCatcherHole()
{
    HostYieldToWindowedPopups();
    if (!g_pickCatcher) return;
    try {
        winrt::Windows::Foundation::IInspectable object{ nullptr }, popupObject{ nullptr }, panelObject{ nullptr };
        winrt::copy_from_abi(object, g_pickCatcher);
        auto catcher = object.as<DevToolsXC::Border>();
        if (g_selPopup) winrt::copy_from_abi(popupObject, g_selPopup);
        if (g_selPanel) winrt::copy_from_abi(panelObject, g_selPanel);
        auto popup = popupObject ? popupObject.try_as<DevToolsXCP::Popup>() : nullptr;
        auto panel = panelObject ? panelObject.try_as<DevToolsX::FrameworkElement>() : nullptr;
        const DevToolsHostSlot* slot = HostSlotFor(g_selectionPopup.get());
        const double width = catcher.Width(), height = catcher.Height();
        double left = 0, top = 0, right = 0, bottom = 0;
        bool hole = slot && slot->layerKey && popup && popup.IsOpen() && panel;
        if (hole) {
            left = (std::clamp)(popup.HorizontalOffset(), 0.0, width);
            top = (std::clamp)(popup.VerticalOffset(), 0.0, height);
            right = (std::clamp)(popup.HorizontalOffset() + panel.ActualWidth(), 0.0, width);
            bottom = (std::clamp)(popup.VerticalOffset() + panel.ActualHeight(), 0.0, height);
            hole = right > left && bottom > top;
        }
        const DevToolsXM::SolidColorBrush transparent{ winrt::Windows::UI::Color{ 0, 0, 0, 0 } };
        if (!hole) {
            catcher.BorderThickness({ 0, 0, 0, 0 });
            catcher.Background(transparent);
            return;
        }
        catcher.BorderBrush(transparent);
        catcher.Background(nullptr);
        catcher.BorderThickness({ left, top, width - right, height - bottom });
    } catch (const winrt::hresult_error& e) {
        DevToolsOverlayLog(L"overlay.pickmode catcher hole failed hr=0x%08X", e.code().value);
    }
}

static HRESULT SetSelectionPopupOpen(bool open)
{
    if (!g_selPopup) return E_POINTER;
    winrt::com_ptr<IUnknown> popup;
    HRESULT hr = g_selPopup->QueryInterface(DevToolsIid<DevToolsXCP::IPopup>(), popup.put_void());
    if (SUCCEEDED(hr)) hr = DevToolsPopupPutIsOpen(popup.get(), open);
    UpdateCatcherHole();
    return hr;
}
static void CALLBACK DevToolsSelDismissTimerProc(HWND, UINT, UINT_PTR, DWORD)
{
    const UINT_PTR timer = g_selDismissTimer;
    g_selDismissTimer = 0;
    if (timer) KillTimer(nullptr, timer);
    if (g_selDismissGen != g_selGen || !g_selPanel) return;
    if (g_selDismissCommitFailed) {
        if (g_selDismissVisualClosed) {
            const HRESULT hr = SetSelectionPopupOpen(true);
            DevToolsOverlayLog(L"overlay.sel dismiss restore after late failure hr=0x%08X", hr);
            if (SUCCEEDED(hr)) g_selDismissVisualClosed = false;
        }
        g_selDismissGen = 0;
        DevToolsOverlayLog(L"overlay.sel dismiss cancelled: focused row commit failed");
        return;
    }
    DevToolsOverlayLog(L"overlay.sel dismiss teardown elapsedMs=%.1f", (QpcNow() - g_selDismissBegan) / 1000.0);
    DeselectAll();
    // Pick mode resumes where the pointer is now, not where it was when the panel opened.
    if (g_pickCatcher) OnCatcherPointerMoved(g_pickCatcher, nullptr);
}
static bool DismissSelectionPanel(bool discardDetached)
{
    if (g_selRootLost) {
        if (!discardDetached) return false;
        DiscardSelectionCommentDraft();
        DeselectAll(g_lastHighlightHandle == g_selectedHandle);
        return true;
    }
    if (!g_selPanel) return false;
    if (discardDetached && DiscardFailedSelectionCommentDraft()) {
        DeselectAll(g_lastHighlightHandle == g_selectedHandle);
        return true;
    }
    if (g_selDismissTimer) return true;
    const unsigned generation = g_selGen;
    g_selDismissBegan = QpcNow();
    DevToolsOverlayLog(L"overlay.sel dismiss entry gen=%u", generation);
    g_selDismissCommitFailed = false;
// Moving focus to Close still runs, for focus hygiene and as the ordinary blur path, but it CANNOT be the only
// commit path: XAML delivers the resulting LostFocus on the dispatcher, which loses to this WM_TIMER often
// enough to measure (4 runs in 8 on a busy machine), and by then ClearSelectionAnchor has bumped g_selGen an.
    // Commit before teardown synchronously; focus movement alone cannot guarantee LostFocus wins.
    DevToolsSelFlushCommits();
    if (generation != g_selGen) return true;
    DevToolsOverlayLog(L"overlay.sel dismiss flush elapsedMs=%.1f failed=%d",
        (QpcNow() - g_selDismissBegan) / 1000.0, static_cast<int>(g_selDismissCommitFailed));
    if (g_selDismissCommitFailed) return false;
    g_selDismissGen = g_selGen;
    g_selDismissTimer = SetTimer(nullptr, 0, 1, &DevToolsSelDismissTimerProc);
    if (!g_selDismissTimer) {
        DevToolsOverlayLog(L"overlay.sel dismiss refused: could not schedule commit-safe teardown error=%lu", GetLastError());
        g_selDismissGen = 0;
        return false;
    }
    if (!DevToolsSelFocusIcon()) {
        KillTimer(nullptr, g_selDismissTimer);
        g_selDismissTimer = 0;
        g_selDismissGen = 0;
        DevToolsOverlayLog(L"overlay.sel dismiss refused: Close button did not take focus");
        return false;
    }
    if (generation != g_selGen) return true;
    SelReturnFocus();
    if (!g_selDismissCommitFailed) {
        g_selDismissVisualClosed = true;
        const HRESULT hr = SetSelectionPopupOpen(false);
        if (FAILED(hr)) g_selDismissVisualClosed = false;
        DevToolsOverlayLog(L"overlay.sel dismiss visual-close elapsedMs=%.1f hr=0x%08X",
            (QpcNow() - g_selDismissBegan) / 1000.0, hr);
    }
    if (!g_selPending.empty())
        DevToolsOverlayLog(L"overlay.sel dismiss: discarding %zu pending binding confirm(s) -- not auto-confirmed",
                      g_selPending.size());
    DevToolsOverlayLog(L"overlay.sel dismiss queued after inline commit gen=%u timer=%llu",
                  g_selDismissGen, (unsigned long long)g_selDismissTimer);
    return true;
}

static void OnSelCloseClick(void*, void*)
{
    DevToolsOverlayLog(L"overlay.sel close (X) -> dismiss");
    DismissSelectionPanel(true);
}

// Move keyboard focus to the panel's Close button (projected UIElement.Focus, FocusState.Programmatic). Used
// as the COMMIT mechanism for keyboard gestures: a focused editor loses focus as focus lands on the icon,
// firing its already-wired LostFocus -> the property/comment commit runs exactly as a Tab-away would.
static bool DevToolsSelFocusIcon()
{
    if (!g_selIcon) return false;
    void* ui = nullptr;
    HRESULT hr = g_selIcon->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui);
    if (FAILED(hr) || !ui) {
        DevToolsOverlayLog(L"overlay.sel focus Close QI failed hr=0x%08X", hr);
        return false;
    }
    bool ok = false;
    hr = DevToolsFocus(ui, 3 /*FocusState.Programmatic*/, &ok);
    reinterpret_cast<IUnknown*>(ui)->Release();
    DevToolsOverlayLog(L"overlay.sel focus Close hr=0x%08X succeeded=%d", hr, (int)ok);
    return SUCCEEDED(hr) && ok;
}

// Comment box GotFocus: bump the focus-within counter (collapse gate) AND flag that the MULTILINE comment holds
// focus, so a plain Enter is left to insert a newline (AcceptsReturn) and only Ctrl+Enter commits it.
static void OnSelCommentGotFocus(void*, void*)
{
    ++g_selFocusWithin;
    g_selFocusedIsComment = true;
    DevToolsOverlayLog(L"overlay.sel focus-in within=%d (comment)", g_selFocusWithin);
}

// Keyboard/a11y contract for the morph panel: Esc -> commit the active field, then collapse (focus lands on the
// icon so nav is not orphaned). "Commit" is expressed by moving focus to the icon, which fires the field's
// wired LostFocus - so there is exactly one commit path (blur), keyboard and pointer alike.
// Panel keyboard handling commits before collapse so accessibility navigation is not orphaned.
// Whether Enter (with Ctrl or not) commits the quick-edit text editor that raised it. A multi-line editor needs Ctrl.
static bool SelectionSourceIsTextEditor(IInspectable* source, bool ctrl);

static void OnSelKeyDown(void* /*sender*/, void* args)
{
    if (!args) return;
    IUnknown* a = reinterpret_cast<IUnknown*>(args);
    void* kra = nullptr;
    if (FAILED(a->QueryInterface(DevToolsIid<DevToolsXI::IKeyRoutedEventArgs>(), &kra)) || !kra) return;
    int key = 0;
    DevToolsGetKey(kra, &key);

    const int kVkEnter = 13, kVkEscape = 27;
    const bool ctrl = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
    bool handled = false;

    if (key == kVkEscape) {
        const bool queued = DismissSelectionPanel(!g_selRootLost);
        handled = true;
        DevToolsOverlayLog(L"overlay.sel key Esc -> commit+deselect queued=%d", (int)queued);
    } else if (key == kVkEnter) {
        if (g_selFocusedIsComment) {
            if (ctrl) {
                DevToolsSelCommitComment(true);
                const bool focused = DevToolsSelFocusIcon();
                handled = true;
                DevToolsOverlayLog(L"overlay.sel key Ctrl+Enter -> commit comment focused=%d", (int)focused);
            }
        } else {
            winrt::com_ptr<IUnknown> routed;
            winrt::com_ptr<IInspectable> source;
            if (SUCCEEDED(a->QueryInterface(DevToolsIid<DevToolsX::IRoutedEventArgs>(), routed.put_void())) &&
                SUCCEEDED(DevToolsGetOriginalSource(routed.get(), source.put())) && source &&
                SelectionSourceIsTextEditor(source.get(), ctrl)) {
                handled = DevToolsSelFocusIcon();
                DevToolsOverlayLog(L"overlay.sel key Enter -> commit row focused=%d", (int)handled);
            }
        }
    }
    if (handled) DevToolsPutHandled(kra, true);
    reinterpret_cast<IUnknown*>(kra)->Release();
}

// Same shape as the focus/pointer wirers; logged.
static void DevToolsSelWireKey(IInspectable* el, EventSink& sink, void (*cb)(void*, void*))
{
    if (!el) return;
    void* ui = nullptr;
    if (FAILED(el->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) || !ui) return;
    sink.Bind(&IID_KeyEventHandler_Del, cb);
    __int64 tok = 0;
    HRESULT hr = DevToolsAddPreviewKeyDown(ui, &sink, &tok);
    reinterpret_cast<IUnknown*>(ui)->Release();
    DevToolsOverlayLog(L"overlay.sel wire-key add_PreviewKeyDown hr=0x%08X", hr);
}

// The panel and RPC dispatcher share DevToolsTrust's process-wide posture. Denied mutations must suppress
// editors as well as refuse writes.
// Editors and writes share DevToolsTrust posture; denied mutations must hide editors too.
static std::wstring DevToolsSelQuoteForPrompt(const std::wstring& v)
{
    std::wstring s = v;
    for (auto& c : s) if (c == L'\r' || c == L'\n' || c == L'\t') c = L' ';
    if (s.size() > 24) { s.resize(23); s += L'\u2026'; }
    return L"'" + s + L"'";
}

// Returns true when the caller should adopt `val` as its new seed. Refused binding writes raise the row's
// confirmation.
static std::wstring g_selOperationError;
static std::wstring g_selOperationProperty;
static std::vector<std::wstring> g_selPreviewProperties;

static void SetSelectionText(const std::wstring& name, const std::wstring& text)
{
    if (!g_selUi) return;
    if (IInspectable* el = DevToolsSelFindName(g_selUi, name.c_str())) {
        void* tb = nullptr;
        if (SUCCEEDED(el->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &tb)) && tb) {
            HSTRING value = DevToolsSelMakeStr(text);
            const HRESULT hr = DevToolsPutText(tb, value);
            WindowsDeleteString(value);
            if (FAILED(hr)) DevToolsOverlayLog(L"overlay.sel status text hr=0x%08X", hr);
            reinterpret_cast<IUnknown*>(tb)->Release();
        }
        el->Release();
    }
}

static void SetSelectionOperationError(const std::wstring& prop, const std::wstring& message)
{
    if (message.empty() && prop != g_selOperationProperty) return;
    g_selOperationProperty = message.empty() ? L"" : prop;
    g_selOperationError = message;
    if (!message.empty()) {
        g_selDismissCommitFailed = true;
        DevToolsOverlayLog(L"overlay.sel operation failed: %s", message.c_str());
    }
    SetSelectionText(L"DevToolsSelOperationStatus", message);
    if (g_selUi) {
        if (IInspectable* el = DevToolsSelFindName(g_selUi, L"DevToolsSelOperationStatus")) {
            void* ui = nullptr;
            if (SUCCEEDED(el->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) && ui) {
                DevToolsPutVisibility(ui, message.empty() ? 1 : 0);
                reinterpret_cast<IUnknown*>(ui)->Release();
            }
            el->Release();
        }
    }
}

static std::wstring SelectionPreviewValue(const DevToolsCardRow& row)
{
    if (row.valueState == L"null") return L"(null)";
    if (row.valueState == L"unset") {
        if (row.value == L"inf" || row.value == L"Infinity")
            return row.name == L"MaxWidth" || row.name == L"MaxHeight" ? L"Unbounded" : L"Infinity";
        return L"Auto";
    }
    if (row.valueState == L"unresolved") return L"Unavailable";
    const auto members = DevToolsEnumMembersFor(row.type);
    const int member = DevToolsEnumIndexOf(members, row.value);
    if (member >= 0) return members[(size_t)member];
    return row.value.empty() ? L"(empty)" : row.value;
}

static bool SelectionHasColorSwatch(const DevToolsCardRow& row)
{
    return row.valueState != L"unresolved" && row.valueState != L"null" &&
        (row.editKind == L"color" || row.valueType == L"Microsoft.UI.Xaml.Media.SolidColorBrush" ||
         row.valueType == L"Windows.UI.Color") && DevToolsRead_IsHexColor(row.value);
}

static HRESULT RefreshSelectionSwatch(size_t index, const DevToolsCardRow& row)
{
    if (!g_selUi) return S_OK;
    winrt::com_ptr<IInspectable> swatch;
    swatch.attach(DevToolsSelFindName(g_selUi, (L"DevToolsSelSwatch" + std::to_wstring(index)).c_str()));
    if (!swatch) return S_OK;
    winrt::com_ptr<IUnknown> ui;
    HRESULT hr = swatch->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), ui.put_void());
    if (FAILED(hr)) return hr;
    hr = DevToolsPutVisibility(ui.get(), SelectionHasColorSwatch(row) ? 0 : 1);
    if (FAILED(hr) || !SelectionHasColorSwatch(row)) return hr;
    winrt::com_ptr<IInspectable> value;
    hr = LoadMarkup(L"<SolidColorBrush xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Color=\"" +
        row.value + L"\"/>", value.put());
    if (FAILED(hr) || !value) return FAILED(hr) ? hr : E_NOINTERFACE;
    winrt::com_ptr<IUnknown> brush, border;
    hr = value->QueryInterface(DevToolsIid<DevToolsXM::IBrush>(), brush.put_void());
    if (FAILED(hr)) return hr;
    hr = swatch->QueryInterface(DevToolsIid<DevToolsXC::IBorder>(), border.put_void());
    return FAILED(hr) ? hr : DevToolsPutBorderBackground(border.get(), brush.get());
}

static void UpdateSelectionPreview(const std::wstring& prop)
{
    const auto it = std::find(g_selPreviewProperties.begin(), g_selPreviewProperties.end(), prop);
    if (it == g_selPreviewProperties.end()) return;
    const auto index = it - g_selPreviewProperties.begin();
    const unsigned gen = g_selGen;
    std::wstring type, name, state, xaml;
    std::vector<DevToolsCardRow> rows;
    const bool read = g_cardRead && g_selTarget && g_cardRead(g_selTarget, &type, &name, &rows, &state, &xaml);
    if (gen != g_selGen) return;
    if (read) {
        const auto fresh = std::find_if(rows.begin(), rows.end(), [&](const auto& row) { return row.name == prop; });
        if (fresh != rows.end()) {
            SetSelectionText(L"DevToolsSelValue" + std::to_wstring(index), SelectionPreviewValue(*fresh));
            const HRESULT hr = RefreshSelectionSwatch(static_cast<size_t>(index), *fresh);
            if (FAILED(hr))
                SetSelectionOperationError(prop, L"Value changed, but its color swatch couldn't refresh. Reselect the element.");
            return;
        }
    }
    SetSelectionOperationError(prop, L"Value changed, but its preview couldn't refresh. Reselect the element.");
}

static bool DevToolsSelApplyCommit(DevToolsWriteOutcome oc, const std::wstring& prop, const std::wstring& type,
                              const std::wstring& val)
{
    DevToolsOverlayLog(L"overlay.sel row commit %s=%s outcome=%d", prop.c_str(), val.c_str(), (int)oc);
    switch (oc) {
    case DevToolsWriteOutcome::Ok:
        DevToolsSelPendingErase(prop);
        SetSelectionOperationError(prop, L"");
        UpdateSelectionPreview(prop);
        return true;

    case DevToolsWriteOutcome::ReplacedBinding: {
        const unsigned gen = g_selGen;
        DevToolsSelPendingErase(prop);
        SetSelectionOperationError(prop, L"");
        UpdateSelectionPreview(prop);
        if (gen != g_selGen) return true;
        if (DevToolsSelExprRefs* x = DevToolsSelExprFind(prop)) {
            if (x->btn) DevToolsPutVisibility(x->btn, 1);
            if (x->text) DevToolsPutVisibility(x->text, 0);
            if (x->textTb) {
                HSTRING hs = DevToolsSelMakeStr(L"Binding replaced. Inspect restoration support in DevTools.");
                DevToolsPutText(x->textTb, hs); WindowsDeleteString(hs);
            }
        }
        return true;
    }

    case DevToolsWriteOutcome::NeedsConfirm: {
        DevToolsSelPendingErase(prop);
        g_selPending.push_back({ prop, type, val });
        if (DevToolsSelExprRefs* x = DevToolsSelExprFind(prop)) {
            if (x->label) {
                HSTRING hs = DevToolsSelMakeStr(L"Replace binding with " + DevToolsSelQuoteForPrompt(val) + L"?");
                DevToolsPutText(x->label, hs); WindowsDeleteString(hs);
            }
            if (x->text) DevToolsPutVisibility(x->text, 1);
            if (x->btn)  DevToolsPutVisibility(x->btn, 0);
        }
        return false;
    }

    case DevToolsWriteOutcome::Failed:
    default:
        SetSelectionOperationError(prop, L"Couldn't set " + prop + L". Check the value and try again.");
        return false;
    }
}

// Rows are seeded once, so Property.changed cannot echo into a focused editor and move its caret. Any future
// live refresh must ignore its own origin and leave focused editors untouched.
// Commit on blur/selection change with a generation guard; never write per keystroke.
static bool TrySelectionNumber(const std::wstring& text, double* value)
{
    std::wistringstream input(text);
    input.imbue(std::locale::classic());
    double parsed = 0;
    if (!(input >> parsed) || !std::isfinite(parsed)) return false;
    input >> std::ws;
    if (!input.eof()) return false;
    if (value) *value = parsed;
    return true;
}

struct SelRowSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*) {
// An inert sink returning S_OK with no trace is how a dropped commit stayed invisible for three issues: the
// editor's value cannot be read here (the box is released during teardown), so say that the commit was refused
// and let the reader decide whether the row held an edit.
        if (gen != g_selGen) {
            DevToolsOverlayLog(L"overlay.sel row commit SKIPPED %s: sink inert, anchor gen %u -> %u -- any uncommitted edit in this row is gone",
                          prop.c_str(), gen, g_selGen);
            return S_OK;
        }
        if (!g_selTarget || !g_cardWrite) {
            DevToolsOverlayLog(L"overlay.sel row commit SKIPPED %s: no target/write bridge", prop.c_str());
            SetSelectionOperationError(prop, L"Editing is unavailable. Reselect the element and try again.");
            return S_OK;
        }
        std::wstring val;
        auto readFailed = [&]() {
            SetSelectionOperationError(prop, L"Couldn't read " + prop + L". Reopen its editor and try again.");
            return S_OK;
        };
        if (number || slider) {
            double value = 0;
            if (!std::isfinite(originalNumber)) return readFailed();
            const HRESULT hr = slider ? DevToolsRangeGetValue(slider, &value) : DevToolsNumberBoxGetValue(number, &value);
            if (FAILED(hr) || !std::isfinite(value)) return readFailed();
            if (value == originalNumber) return S_OK;
            std::wostringstream formatted;
            formatted.imbue(std::locale::classic());
            formatted << std::setprecision(17) << value;
            val = formatted.str();
        } else if (toggle) {
            bool on = false;
            if (FAILED(DevToolsToggleGetIsOn(toggle, &on))) return readFailed();
            val = DevToolsBoolLiteral(on);
        } else if (selector) {
            int idx = -1;
            if (FAILED(DevToolsGetSelectedIndex(selector, &idx))) return readFailed();
            if (idx < 0 || idx >= (int)members.size()) return readFailed();
            val = members[(size_t)idx];
        } else {
            if (!g_cardReadInput || !box) return readFailed();
            std::wstring current;
            if (!g_cardReadInput(box, &current)) return readFailed();
            if (!DevToolsEditText::ValueToWrite(current, hasShown ? &shown : nullptr, original, &val)) return S_OK;
        }
        if (val == original) return S_OK;
        const unsigned before = g_selGen;
        const auto outcome = g_cardWrite(g_selTarget, prop.c_str(), type.c_str(), val.c_str(), false);
        if (before != g_selGen) return S_OK;
        if (DevToolsSelApplyCommit(outcome, prop, type, val) && before == g_selGen) AcceptValue(val);
        return S_OK;
    }
    void InitText(const std::wstring& p, const std::wstring& t, const std::wstring& orig, IInspectable* b) {
        prop = p; type = t; original = orig; box = b;
        multiline = DevToolsEditText::IsMultiline(orig);
        RememberShown();
        InitSink(IID_RoutedEventHandler_Del, g_selGen, &IID_SelectionChanged_Del);
    }
    // What the editor holds now, so closing it unchanged writes nothing even where it cannot hold the value exactly.
    void RememberShown() { hasShown = box && g_cardReadInput && g_cardReadInput(box, &shown); }
    void InitEnum(const std::wstring& p, const std::wstring& t, const std::wstring& orig,
                  std::vector<std::wstring> mem, IInspectable* sel) {
        prop = p; type = t; original = orig; members = std::move(mem); selector = sel;
        InitSink(IID_RoutedEventHandler_Del, g_selGen, &IID_SelectionChanged_Del);
    }
    void InitNumber(const std::wstring& p, const std::wstring& t, const std::wstring& orig, void* input) {
        prop = p; type = t; original = orig; number = input;
        TrySelectionNumber(orig, &originalNumber);
        using Changed = winrt::Windows::Foundation::TypedEventHandler<DevToolsXC::NumberBox, DevToolsXC::NumberBoxValueChangedEventArgs>;
        InitSink(DevToolsIid<Changed>(), g_selGen);
    }
    void InitSlider(const std::wstring& p, const std::wstring& t, const std::wstring& orig, void* input, double seed) {
        prop = p; type = t; original = orig; slider = input; originalNumber = seed;
        InitSink(DevToolsIid<DevToolsXCP::RangeBaseValueChangedEventHandler>(), g_selGen);
    }
    void AcceptValue(const std::wstring& value) {
        original = value;
        if (number || slider) TrySelectionNumber(value, &originalNumber);
        if (box) RememberShown();
    }
    // Rows are a snapshot; a text editor re-reads its value when it gains focus, unless the user already typed.
    void RefreshTextFromLive() {
        if (!box || gen != g_selGen || !g_cardRead || !g_cardReadInput || !g_selTarget) return;
        std::wstring current;
        if (!g_cardReadInput(box, &current) || (hasShown ? current != shown : current != original)) return;
        std::wstring type, name, state, xaml;
        std::vector<DevToolsCardRow> rows;
        if (!g_cardRead(g_selTarget, &type, &name, &rows, &state, &xaml) || gen != g_selGen) return;
        const auto fresh = std::find_if(rows.begin(), rows.end(), [&](const auto& row) { return row.name == prop; });
        if (fresh == rows.end() || fresh->value == original || !SelRowCanEdit(*fresh)) return;
        void* textBox = nullptr;
        if (FAILED(box->QueryInterface(DevToolsIid<DevToolsXC::ITextBox>(), &textBox)) || !textBox) return;
        HSTRING text = DevToolsSelMakeStr(fresh->value);
        const HRESULT hr = DevToolsPutTextBoxText(textBox, text);
        WindowsDeleteString(text);
        reinterpret_cast<IUnknown*>(textBox)->Release();
        if (FAILED(hr)) return;
        AcceptValue(fresh->value);
        UpdateSelectionPreview(prop);
    }
// `original` is load-bearing here rather than an optimisation: WinUI raises Toggled from OnIsOnChanged, so a
// row seeded IsOn="True" in markup raises one on REALIZE -- without the compare in Invoke, merely selecting an
// element would commit a write the user never made (and on a bound boolean would raise the binding-replace.
    // Boolean rows use the existing RoutedEventHandler sink; original value distinguishes unchanged toggles.
    void InitBool(const std::wstring& p, const std::wstring& t, const std::wstring& orig, void* ts) {
        prop = p; type = t; original = orig; toggle = ts;
        InitSink(IID_RoutedEventHandler_Del, g_selGen, &IID_SelectionChanged_Del);
    }
    IInspectable*             box = nullptr;
    IInspectable*            selector = nullptr;
    void*                     toggle = nullptr;
    void*                     number = nullptr;
    void*                     slider = nullptr;
    double                    originalNumber = std::numeric_limits<double>::quiet_NaN();
    std::wstring              prop, type, original, shown;
    bool                      hasShown = false, multiline = false;
    std::vector<std::wstring> members;
};

struct SelRowFocusSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*) {
        if (gen == g_selGen && row) row->RefreshTextFromLive();
        return S_OK;
    }
    void Init(SelRowSink* r) { row = r; InitSink(IID_RoutedEventHandler_Del, g_selGen); }
    SelRowSink* row = nullptr;
};
