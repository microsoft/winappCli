// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsOverlay.cpp; included into that translation unit, not compiled on its own.

// ============================ Comment flyout ============================ One panel (source location, comment
// box, the element's other comments, "open in DevTools") anchored beside the selected element. Authored in
// xaml/selection.xaml + selection-panel.xaml and driven by name.
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
static unsigned      g_selGen = 0;
static UINT_PTR      g_selDismissTimer = 0;
static unsigned      g_selDismissGen = 0;
static bool          g_selDismissCommitFailed = false;
// Caller WindowsDeleteStrings it.
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

static const int kSelPanelW = 400;

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
    // Names inside the flyout are not reachable from the selection root once its Popup is lifted.
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


// Dismissal saves the comment first (inline), then moves focus to the Close button so focus is not orphaned when the
// panel leaves the tree. Teardown is deferred to the next UI turn.
static bool DevToolsSelFocusIcon();
// Saves the comment INLINE, so a dismiss does not depend on the focus-driven LostFocus winning a race.
static void DevToolsSelFlushCommits();
static void DevToolsSelCommitComment(bool explicitSave = false);
static void DiscardSelectionCommentDraft();
static bool DiscardFailedSelectionCommentDraft();
// accessor rather than reaching for g_selPanel directly, because the catcher's click handler sits above the
// selection globals.
static bool DevToolsSelPanelIsOpen() { return g_selPanel != nullptr; }
static bool g_selDismissVisualClosed = false;
static double g_selDismissBegan = 0;

// What had focus when the flyout opened. Closing the flyout hands focus back with pointer state: left alone,
// WinUI moves focus out of the removed peek with keyboard state, which draws a focus rectangle and opens
// accelerator tips in the app.
static winrt::com_ptr<IInspectable> g_selReturnFocus;

static void SelRememberFocus()
{
    g_selReturnFocus = nullptr;
    winrt::com_ptr<IInspectable> focused;
    bool inPanel = false, inToolbar = false;
    if (!g_selPanel || FAILED(DevToolsFocusedWithin(g_selPanel, focused.put(), &inPanel)) || inPanel) return;
    // Focus left on the toolbar (reached with Ctrl+Shift+F12) is DevTools chrome, not the app's: return to what the
    // app had before the toolbar took it.
    winrt::com_ptr<IInspectable> chrome;
    if (focused && g_protoRoot && SUCCEEDED(DevToolsFocusedWithin(g_protoRoot, chrome.put(), &inToolbar)) && inToolbar) {
        focused = nullptr;
        if (auto app = g_toolbarReturnFocus.get()) winrt::copy_to_abi(app, *reinterpret_cast<void**>(focused.put()));
    }
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
// would take clicks meant for the open flyout. While the flyout is open there, the catcher keeps only a
// transparent border around the flyout's rectangle: clicks outside still dismiss, clicks on the flyout reach it.
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
        DevToolsOverlayLog(L"overlay.sel dismiss cancelled: the comment save failed");
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

// Comment box GotFocus: bump the focus-within counter (collapse gate) AND flag that the comment holds focus, so
// Enter saves it while Shift+Enter is left to the box to insert a newline.
static void OnSelCommentGotFocus(void*, void*)
{
    ++g_selFocusWithin;
    g_selFocusedIsComment = true;
    DevToolsOverlayLog(L"overlay.sel focus-in within=%d (comment)", g_selFocusWithin);
}

// Esc closes the flyout (saving the draft); Enter saves the comment and Shift+Enter is left to the box for a new
// line. Ctrl+Enter, the earlier save gesture, still saves.
static void OnSelKeyDown(void* /*sender*/, void* args)
{
    if (!args) return;
    IUnknown* a = reinterpret_cast<IUnknown*>(args);
    void* kra = nullptr;
    if (FAILED(a->QueryInterface(DevToolsIid<DevToolsXI::IKeyRoutedEventArgs>(), &kra)) || !kra) return;
    int key = 0;
    DevToolsGetKey(kra, &key);

    const bool ctrl = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
    const bool shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
    bool handled = false;
    if (key == VK_CONTROL || key == VK_MENU || (key >= VK_LCONTROL && key <= VK_RMENU)) {
        // The modifier itself stays in the panel, so the app never starts showing its accelerator or access-key tips.
        // The panel's own chords still arrive: each is a separate key-down with the modifier held.
        handled = true;
    } else if (key == VK_ESCAPE) {
        const bool queued = DismissSelectionPanel(!g_selRootLost);
        handled = true;
        DevToolsOverlayLog(L"overlay.sel key Esc -> commit+deselect queued=%d", (int)queued);
    } else if (key == VK_RETURN && g_selFocusedIsComment && (!shift || ctrl)) {
        DevToolsSelCommitComment(true);
        const bool focused = DevToolsSelFocusIcon();
        handled = true;
        DevToolsOverlayLog(L"overlay.sel key Enter -> save comment focused=%d", (int)focused);
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

static std::wstring g_selOperationError;
static std::wstring g_selOperationProperty;

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

static void GuestCommentRememberDraft(bool composer);
static void GuestCommentRestoreDraft(bool composer);
static bool GuestCommentCanEdit(InstanceHandle handle);

static void DevToolsSelFlushCommits()
{
    if (!g_selRootLost) DevToolsSelCommitComment();
}
