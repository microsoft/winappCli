// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsOverlay.cpp; included into that translation unit, not compiled on its own.

static void DevToolsSetSelectionPanelRequestedTheme(int requestedTheme);
static void SetToolbarMenuTheme(int requestedTheme);

static void DevToolsSetToolbarRequestedTheme(int appTheme)
{
    const int toolbarTheme = DevToolsBuildPalette().highContrast
        ? DevToolsTheme::kDefault
        : DevToolsTheme::ToggleTarget(appTheme);
    for (IInspectable* element : { g_railL, g_aRow }) {
        if (!element) continue;
        void* fe = nullptr;
        if (SUCCEEDED(element->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) && fe) {
            DevToolsPutRequestedTheme(fe, toolbarTheme);
            reinterpret_cast<IUnknown*>(fe)->Release();
        }
    }
    DevToolsSetSelectionPanelRequestedTheme(toolbarTheme);
    SetToolbarMenuTheme(toolbarTheme);
    HostSetLayerTheme(appTheme);
}

// The app's own RequestedTheme on its content root, saved by the first switch so the switch back restores it rather
// than leaving DevTools' value in place. `set` is false when the app never set one.
static bool g_themeSwitched = false;
static bool g_themeOriginalSet = false;
static DevToolsX::ElementTheme g_themeOriginal = DevToolsX::ElementTheme::Default;

// The switch back: the app's own value, or none. False when this switch is not a switch back.
static bool RestoreAppRequestedTheme(void* themeFe)
{
    if (!g_themeSwitched) return false;
    g_themeSwitched = false;
    try {
        winrt::Windows::Foundation::IInspectable object{ nullptr };
        winrt::copy_from_abi(object, themeFe);
        const auto element = object.as<DevToolsX::FrameworkElement>();
        if (g_themeOriginalSet) element.RequestedTheme(g_themeOriginal);
        else element.ClearValue(DevToolsX::FrameworkElement::RequestedThemeProperty());
        DevToolsSetToolbarRequestedTheme(static_cast<int>(element.ActualTheme()));
        DevToolsOverlayLog(L"overlay.theme restored the app's own value set=%d theme=%d", (int)g_themeOriginalSet, (int)g_themeOriginal);
        return true;
    } catch (const winrt::hresult_error& e) {
        DevToolsOverlayLog(L"overlay.theme restore failed hr=0x%08X", e.code().value);
        return false;
    }
}

static void SaveAppRequestedTheme(void* themeFe)
{
    try {
        winrt::Windows::Foundation::IInspectable object{ nullptr };
        winrt::copy_from_abi(object, themeFe);
        const auto element = object.as<DevToolsX::FrameworkElement>();
        const auto local = element.ReadLocalValue(DevToolsX::FrameworkElement::RequestedThemeProperty());
        g_themeOriginalSet = local != DevToolsX::DependencyProperty::UnsetValue();
        g_themeOriginal = g_themeOriginalSet ? element.RequestedTheme() : DevToolsX::ElementTheme::Default;
    } catch (const winrt::hresult_error& e) {
        DevToolsOverlayLog(L"overlay.theme could not read the app's own value hr=0x%08X", e.code().value);
        g_themeOriginalSet = false;
    }
}

static void OnThemeToggleClick(void*, void*)
{
    if (!DevToolsTrust_MutationEnabled()) {
        DevToolsOverlayLog(L"overlay.theme refused by process posture");
        return;
    }
    if (!g_appPanel) {
        DevToolsOverlayLog(L"overlay.theme unavailable: app content root not retained");
        return;
    }

    void* themeFe = nullptr;
    HRESULT hrThemeQi = g_appPanel->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &themeFe);
    int actualBefore = DevToolsTheme::kDefault;
    HRESULT hrActual = themeFe ? DevToolsGetActualTheme(themeFe, &actualBefore) : hrThemeQi;
    if (FAILED(hrActual) || !themeFe) {
        DevToolsOverlayLog(L"overlay.theme get ActualTheme failed qi=0x%08X get=0x%08X", hrThemeQi, hrActual);
        if (themeFe) reinterpret_cast<IUnknown*>(themeFe)->Release();
        return;
    }

    if (RestoreAppRequestedTheme(themeFe)) {
        reinterpret_cast<IUnknown*>(themeFe)->Release();
        return;
    }
    SaveAppRequestedTheme(themeFe);
    const int target = DevToolsTheme::ToggleTarget(actualBefore);
    HRESULT hrPut = DevToolsPutRequestedTheme(themeFe, target);
    g_themeSwitched = SUCCEEDED(hrPut);
    int requestedAfter = DevToolsTheme::kDefault;
    HRESULT hrRequestedAfter = DevToolsGetRequestedTheme(themeFe, &requestedAfter);
    int actualAfter = actualBefore;
    HRESULT hrActualAfter = DevToolsGetActualTheme(themeFe, &actualAfter);

    const bool requestedMatches = SUCCEEDED(hrPut) && SUCCEEDED(hrRequestedAfter) && requestedAfter == target;
    const bool effectiveMatches = SUCCEEDED(hrActualAfter) && actualAfter == target;
    DevToolsOverlayLog(
        L"overlay.theme actual=%d target=%d put=0x%08X requested=%d/%s actualAfter=%d/%s",
        actualBefore, target, hrPut, requestedAfter, requestedMatches ? L"match" : L"mismatch",
        actualAfter, effectiveMatches ? L"match" : L"mismatch");
    if (SUCCEEDED(hrPut)) DevToolsSetToolbarRequestedTheme(target);

    reinterpret_cast<IUnknown*>(themeFe)->Release();
}

// `ICanvasStatics::SetLeft` takes a UIElement, so its ABI parameter is the element's IUIElement pointer --
// handing it an IInspectable is a vtable mismatch that faults inside XAML. Every original caller happened to be
// safe because its pointer came out of an IVector<UIElement>, which hands back exactly that interface; an e.
// QI to IUIElement is required; Canvas.SetLeft/Top does not accept arbitrary IInspectable.
static void MoveOverlayElement(IInspectable* el, double x, double y)
{
    if (!g_canvasStatics || !el) return;
    void* ui = nullptr;
    if (FAILED(el->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) || !ui) return;
    DevToolsCanvasSetLeft(g_canvasStatics, reinterpret_cast<IInspectable*>(ui), x);
    DevToolsCanvasSetTop(g_canvasStatics, reinterpret_cast<IInspectable*>(ui), y);
    reinterpret_cast<IUnknown*>(ui)->Release();
}

// Pill hover/focus expands the bar anchored to the same corner; pointer exit collapses it. Focus on the pill
// opens the keyboard actions.
static IInspectable* g_protoRoot = nullptr;
static void BeginNamedStoryboard(IInspectable* root, const wchar_t* name);
static IInspectable* DevToolsSelFindName(IInspectable* root, const wchar_t* name);

// Shared anchor for the pill and bar.
// Pill and bar share one anchor so corner and center parking remain aligned.
static Client ClientGeom() { return Client{ g_protoW, g_protoH, g_titleBarInsetDip }; }
static double ChipX() { double x = 0, y = 0; Anchor(g_corner, ClientGeom(), g_chipW, (double)kChipH, &x, &y); return x; }
static double ChipY() { double x = 0, y = 0; Anchor(g_corner, ClientGeom(), g_chipW, (double)kChipH, &x, &y); return y; }
static double BarX()  { double x = 0, y = 0; Anchor(g_corner, ClientGeom(), (double)kBarW, (double)kBarH, &x, &y); return x; }
static double BarY()  { double x = 0, y = 0; Anchor(g_corner, ClientGeom(), (double)kBarW, (double)kBarH, &x, &y); return y; }

static void SnapTargetPos(int c, double* x, double* y)
{
    double cx = 0.0, cy = 0.0;
    Anchor(c, ClientGeom(), g_chipW, (double)kChipH, &cx, &cy);
    *x = cx + g_chipW / 2.0 - (double)kSnapW / 2.0;
    *y = cy + (double)kChipH / 2.0 - (double)kSnapH / 2.0;
}

static bool g_dragging = false;
static const wchar_t* g_toolbarMenu = nullptr;
static bool CloseToolbarMenu(bool focusAnchor = false);

static void ShowARow(bool open)
{
    if (!open || !g_toolbarVisible) CloseToolbarMenu();
    g_aRowOpen = open;
    MoveOverlayElement(g_aRow, BarX(), BarY());
    MoveOverlayElement(g_railL, ChipX(), ChipY());
    SetOverlayElementVisible(g_aRow, g_toolbarVisible && open);
    SetOverlayElementVisible(g_railL, g_toolbarVisible && !open);
    if (open && g_toolbarVisible) BeginNamedStoryboard(g_protoRoot, L"DevToolsBarReveal");
    DevToolsOverlayLog(L"overlay.toolbar bar %s corner=%d", open ? L"open" : L"closed", g_corner);
    HostYieldToWindowedPopups();
}

// Where the toolbar can draw, in root DIPs: the chip and the bar share one corner anchor.
static bool ToolbarRectDip(RECT* out)
{
    if (!g_toolbarVisible || (!g_railL && !g_aRow)) return false;
    const double left = (std::min)(ChipX(), BarX()), top = (std::min)(ChipY(), BarY());
    const double right = (std::max)(ChipX() + g_chipW, BarX() + kBarW);
    const double bottom = (std::max)(ChipY() + kChipH, BarY() + kBarH);
    *out = RECT{ (LONG)std::floor(left), (LONG)std::floor(top), (LONG)std::ceil(right), (LONG)std::ceil(bottom) };
    return true;
}

// The chip and the bar yield together, so hovering the chip never opens a bar that a popup covers.
static bool ChromeRectOverride(IInspectable* element, winrt::Windows::Foundation::Rect* rect)
{
    const auto identity = HostIdentity(element);
    if (!identity || (identity != HostIdentity(g_railL) && identity != HostIdentity(g_aRow))) return false;
    RECT r{};
    if (!ToolbarRectDip(&r)) return false;
    *rect = { (float)r.left, (float)r.top, (float)(r.right - r.left), (float)(r.bottom - r.top) };
    return true;
}
static void OnAExpand(void*, void*)  { if (g_dragging) return; ShowARow(true); }
// Collapse when the pointer leaves the bar -- unless the toolbar is pinned open (the whole point of the pin).
// An open menu decides for itself, once the pointer has settled on neither the bar nor the menu.
static void OnACollapse(void*, void*){ if (g_pinned || g_dragging || g_toolbarFocusWithin > 0 || g_toolbarMenu) return; ShowARow(false); }
static void OnToolbarGotFocus(void*, void*) { ++g_toolbarFocusWithin; }
// Focus leaving the toolbar for good (not just moving between its buttons) lets it collapse once the pointer is
// not over it either; otherwise a click in the app after using the toolbar leaves it open.
static bool g_barHover = false;
static void CALLBACK ToolbarFocusLeftProc(HWND, UINT, UINT_PTR id, DWORD)
{
    KillTimer(nullptr, id);
    if (g_toolbarFocusWithin == 0 && !g_barHover && !g_pinned && !g_dragging && !g_toolbarMenu && g_aRowOpen) ShowARow(false);
}
static void OnToolbarLostFocus(void*, void*)
{
    if (g_toolbarFocusWithin > 0) --g_toolbarFocusWithin;
    if (g_toolbarFocusWithin == 0) SetTimer(nullptr, 0, 50, &ToolbarFocusLeftProc);
}
static bool FocusToolbarFirstAction()
{
    ShowARow(true);
    winrt::com_ptr<IInspectable> pick;
    pick.attach(DevToolsSelFindName(g_protoRoot, L"DevToolsProtoPick"));
    winrt::com_ptr<IInspectable> ui;
    HRESULT hr = pick ? pick->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), ui.put_void()) : E_POINTER;
    bool focused = false;
    if (SUCCEEDED(hr)) hr = DevToolsFocus(ui.get(), 2, &focused);
    DevToolsOverlayLog(L"overlay.toolbar focus Pick hr=0x%08X succeeded=%d", hr, (int)focused);
    return SUCCEEDED(hr) && focused;
}
static void OnPillGotFocus(void*, void*)
{
    if (g_dragging || !g_toolbarVisible) return;
    FocusToolbarFirstAction();
}

// Ctrl+Shift+F12 moves keyboard focus to the toolbar while it is shown; Esc from the toolbar returns focus to
// the app element that had it.
static winrt::weak_ref<DevToolsX::UIElement> g_toolbarReturnFocus;

// With a text field focused, the chord's Ctrl and Shift key-downs can reach the app right after the F12 they modify,
// so both modifier states read up at the F12. An F12 without them still counts when both follow at once.
static ULONGLONG g_bareF12At = 0;
static unsigned g_lateModifiers = 0;

static bool IsToolbarShortcut(int key)
{
    if (key != VK_F12) return false;
    const bool ctrl = (GetKeyState(VK_CONTROL) & 0x8000) != 0, shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
    const bool other = (GetKeyState(VK_MENU) & 0x8000) || (GetKeyState(VK_LWIN) & 0x8000) || (GetKeyState(VK_RWIN) & 0x8000);
    const bool chord = ctrl && shift && !other;
    g_bareF12At = chord || other ? 0 : GetTickCount64();
    // A modifier already down counts: one can arrive before the F12 and the other after it.
    g_lateModifiers = (ctrl ? 1u : 0u) | (shift ? 2u : 0u);
    return chord;
}

static bool IsLateToolbarShortcut(int key)
{
    // The late key-downs are replayed together with the F12; a person pressing Ctrl+Shift+<key> after tapping F12
    // is far slower than this.
    constexpr ULONGLONG kWindowMs = 50;
    if (!g_bareF12At) return false;
    if (GetTickCount64() - g_bareF12At > kWindowMs || (key != VK_CONTROL && key != VK_SHIFT)) {
        g_bareF12At = 0;
        return false;
    }
    g_lateModifiers |= key == VK_CONTROL ? 1u : 2u;
    if (g_lateModifiers != 3) return false;
    DevToolsOverlayLog(L"overlay.toolbar shortcut: Ctrl and Shift arrived %llu ms after F12", GetTickCount64() - g_bareF12At);
    g_bareF12At = 0;
    return true;
}

// Ctrl+Shift+P turns pick mode on or off anywhere in the app. It is read from the UI thread's message queue, not
// from a XAML key event: an open flyout or menu keeps key events from reaching the app's content, and clicking
// Pick in the inspector window would close that flyout.
static HHOOK g_pickShortcutHook = nullptr;

static bool IsCtrlShiftOnly()
{
    const bool ctrl = (GetKeyState(VK_CONTROL) & 0x8000) != 0, shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
    const bool other = (GetKeyState(VK_MENU) & 0x8000) || (GetKeyState(VK_LWIN) & 0x8000) || (GetKeyState(VK_RWIN) & 0x8000);
    return ctrl && shift && !other;
}

// The app's own windows, including popups they own. The inspector window is not one of them and handles the
// shortcut itself.
static bool IsAppSurfaceWindow(HWND hwnd)
{
    if (!hwnd) return false;
    const HWND root = GetAncestor(hwnd, GA_ROOT), owner = GetAncestor(hwnd, GA_ROOTOWNER);
    for (const auto& surface : g_allSurfaces)
        if (surface.hostHwnd && (surface.hostHwnd == root || surface.hostHwnd == owner)) return true;
    return g_activeSurface.hostHwnd && (g_activeSurface.hostHwnd == root || g_activeSurface.hostHwnd == owner);
}

static void CALLBACK PickShortcutProc(HWND, UINT, UINT_PTR id, DWORD)
{
    KillTimer(nullptr, id);
    DevToolsOverlayLog(L"overlay.pick shortcut Ctrl+Shift+P (armed=%d)", (int)(g_pickCatcher != nullptr));
    OnPickClick(nullptr, nullptr);
}

static LRESULT CALLBACK PickShortcutHookProc(int code, WPARAM removal, LPARAM lParam)
{
    auto* msg = reinterpret_cast<MSG*>(lParam);
    if (code == HC_ACTION && removal == PM_REMOVE && msg && msg->message == WM_KEYDOWN && msg->wParam == 'P' &&
        !(msg->lParam & (1 << 30)) && IsCtrlShiftOnly() && IsAppSurfaceWindow(msg->hwnd)) {
        msg->message = WM_NULL;
        // The toggle changes the XAML tree, so it runs after the hook returns.
        SetTimer(nullptr, 0, 0, &PickShortcutProc);
    }
    return CallNextHookEx(nullptr, code, removal, lParam);
}

static void InstallPickShortcutHook()
{
    if (g_pickShortcutHook) return;
    g_pickShortcutHook = SetWindowsHookExW(WH_GETMESSAGE, &PickShortcutHookProc, nullptr, GetCurrentThreadId());
    DevToolsOverlayLog(L"overlay.pick shortcut hook %s err=%lu", g_pickShortcutHook ? L"installed" : L"FAILED",
        g_pickShortcutHook ? 0ul : GetLastError());
}

// The element with keyboard focus in the toolbar's XamlRoot, and whether it is part of the toolbar.
static DevToolsX::UIElement ToolbarFocusedElement(bool* inToolbar)
{
    *inToolbar = false;
    winrt::Windows::Foundation::IInspectable object{ nullptr };
    winrt::copy_from_abi(object, g_protoRoot);
    auto root = object.as<DevToolsX::UIElement>();
    auto xamlRoot = root.XamlRoot();
    if (!xamlRoot) return nullptr;
    auto focused = DevToolsXI::FocusManager::GetFocusedElement(xamlRoot).try_as<DevToolsX::UIElement>();
    for (DevToolsX::DependencyObject node = focused; node; node = DevToolsXM::VisualTreeHelper::GetParent(node))
        if (node == root) { *inToolbar = true; break; }
    return focused;
}

static bool FocusToolbarFromShortcut()
{
    if (g_dragging) return false;
    // A hidden toolbar comes back first, so the shortcut always ends with focus on it.
    if (!g_toolbarVisible && FAILED(DevToolsOverlay_SetToolbarVisible(true))) return false;
    if (!g_protoRoot) return false;
    try {
        bool inToolbar = false;
        auto focused = ToolbarFocusedElement(&inToolbar);
        if (!inToolbar) g_toolbarReturnFocus = focused ? winrt::make_weak(focused) : nullptr;
        DevToolsOverlayLog(L"overlay.toolbar shortcut from=%s return=%d", inToolbar ? L"toolbar" : L"app",
            (int)(g_toolbarReturnFocus.get() != nullptr));
    } catch (const winrt::hresult_error& e) {
        DevToolsOverlayLog(L"overlay.toolbar shortcut focus read failed hr=0x%08X", e.code().value);
    }
    return FocusToolbarFirstAction();
}

static bool ReturnFocusFromToolbar()
{
    if (!g_protoRoot || !g_toolbarReturnFocus) return false;
    bool returned = false;
    try {
        bool inToolbar = false;
        ToolbarFocusedElement(&inToolbar);
        if (!inToolbar) return false;
        auto target = g_toolbarReturnFocus.get();
        g_toolbarReturnFocus = nullptr;
        returned = target && target.Focus(DevToolsX::FocusState::Keyboard);
    } catch (const winrt::hresult_error& e) {
        DevToolsOverlayLog(L"overlay.toolbar focus return failed hr=0x%08X", e.code().value);
    }
    if (returned && !g_pinned) ShowARow(false);
    DevToolsOverlayLog(L"overlay.toolbar focus returned=%d", (int)returned);
    return returned;
}

// UI Automation (a script, or `winapp ui invoke`) moves focus onto a toolbar button to run it, which left the app's
// own keyboard shortcuts dead. Focus moved there programmatically goes back to the app element that had it once the
// command ran; focus a person moved with the keyboard (Tab, Ctrl+Shift+F12) stays on the toolbar.
static winrt::weak_ref<DevToolsX::UIElement> g_commandReturnFocus;
static bool g_commandReturnArmed = false;

static bool InToolbar(DevToolsX::DependencyObject node)
{
    winrt::Windows::Foundation::IInspectable object{ nullptr };
    winrt::copy_from_abi(object, g_protoRoot);
    const auto root = object.try_as<DevToolsX::DependencyObject>();
    for (; node; node = DevToolsXM::VisualTreeHelper::GetParent(node)) if (node == root) return true;
    return false;
}

static void NoteToolbarGettingFocus(DevToolsXI::GettingFocusEventArgs const& args)
{
    const auto from = args.OldFocusedElement();
    const bool fromApp = from && !InToolbar(from);
    if (fromApp) g_commandReturnFocus = winrt::make_weak(from.try_as<DevToolsX::UIElement>());
    g_commandReturnArmed = fromApp && args.FocusState() == DevToolsX::FocusState::Programmatic;
    DevToolsOverlayLog(L"overlay.toolbar getting focus from=%s state=%d", fromApp ? L"app" : L"toolbar", (int)args.FocusState());
}

static void ReturnFocusAfterCommand()
{
    if (!g_commandReturnArmed || !g_protoRoot) return;
    g_commandReturnArmed = false;
    try {
        bool inToolbar = false;
        ToolbarFocusedElement(&inToolbar);
        const auto target = g_commandReturnFocus.get();
        const bool returned = inToolbar && target && target.Focus(DevToolsX::FocusState::Programmatic);
        DevToolsOverlayLog(L"overlay.toolbar command returned focus=%d", (int)returned);
    } catch (const winrt::hresult_error& e) {
        DevToolsOverlayLog(L"overlay.toolbar command focus return failed hr=0x%08X", e.code().value);
    }
}

template <void (*Command)(void*, void*)>
static void ThenReturnFocus(void* sender, void* args)
{
    Command(sender, args);
    ReturnFocusAfterCommand();
}

static void ProtoSetToggle(const wchar_t* button, const wchar_t* onPart, const wchar_t* offPart, bool on);
static winrt::Windows::Foundation::IInspectable ToolbarPart(const wchar_t* name);
static bool g_syncingCommentToggle = false;

static void ProtoSetPinned(bool on)
{
    g_pinned = on;
    ProtoSetToggle(L"DevToolsProtoPin", L"", L"", on);
}

// Pinning also opens the bar now; unpinning leaves it open until the next PointerExited (the pointer is on the
// bar).
static void OnPinClick(void*, void*)
{
    CloseToolbarMenu();
    const bool now = !g_pinned;
    ProtoSetPinned(now);
    DevToolsSettingsSetBool(L"ToolbarPinned", now);
    if (now) ShowARow(true);
    DevToolsOverlayLog(L"overlay.toolbar pin -> %s (persisted)", now ? L"on" : L"off");
}

// Show/hide one named overlay part by Visibility (Visible=0 / Collapsed=1), resolved in the toolbar fragment's
// namescope.
static void ProtoSetPartVisible(const wchar_t* name, bool visible)
{
    if (!name || !*name) return;
    try {
        if (auto part = ToolbarPart(name).try_as<DevToolsX::IUIElement>()) {
            const HRESULT hr = DevToolsPutVisibility(winrt::get_abi(part), visible ? 0 : 1);
            if (FAILED(hr)) DevToolsOverlayLog(L"overlay.toolbar visibility %s failed hr=0x%08X", name, hr);
        }
    } catch (const winrt::hresult_error& e) {
        DevToolsOverlayLog(L"overlay.toolbar visibility %s failed hr=0x%08X", name, e.code().value);
    }
}

// A toolbar mode: the accent on-part replaces the plain icon, and IsChecked carries the state to UI Automation.
static void ProtoSetToggle(const wchar_t* button, const wchar_t* onPart, const wchar_t* offPart, bool on)
{
    ProtoSetPartVisible(onPart, on);
    ProtoSetPartVisible(offPart, !on);
    try {
        auto element = ToolbarPart(button);
        if (auto split = element.try_as<DevToolsXC::ToggleSplitButton>()) {
            const bool previous = std::exchange(g_syncingCommentToggle, true);
            try { split.IsChecked(on); }
            catch (...) { g_syncingCommentToggle = previous; throw; }
            g_syncingCommentToggle = previous;
        }
        else if (auto radio = element.try_as<DevToolsXC::RadioMenuFlyoutItem>()) radio.IsChecked(on);
        else if (auto item = element.try_as<DevToolsXC::ToggleMenuFlyoutItem>()) item.IsChecked(on);
        else if (auto toggle = element.try_as<DevToolsXCP::ToggleButton>()) toggle.IsChecked(on);
    } catch (const winrt::hresult_error& e) {
        DevToolsOverlayLog(L"overlay.toolbar checked state %s failed hr=0x%08X", button, e.code().value);
    }
}

static void ProtoSetPartText(const wchar_t* name, const std::wstring& text)
{
    if (!g_protoRoot) return;
    if (IInspectable* el = DevToolsSelFindName(g_protoRoot, name)) {
        void* tb = nullptr;
        if (SUCCEEDED(el->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &tb)) && tb) {
            HSTRING h = nullptr;
            if (SUCCEEDED(WindowsCreateString(text.c_str(), (UINT32)text.size(), &h))) { DevToolsPutText(tb, h); WindowsDeleteString(h); }
            reinterpret_cast<IUnknown*>(tb)->Release();
        }
        el->Release();
    }
}

static void ProtoSetPartAutomationName(const wchar_t* name, const std::wstring& text)
{
    static void* statics = nullptr;
    if (!statics) {
        HSTRING cls = nullptr;
        const wchar_t* id = L"Microsoft.UI.Xaml.Automation.AutomationProperties";
        if (SUCCEEDED(WindowsCreateString(id, (UINT32)wcslen(id), &cls))) {
            RoGetActivationFactory(cls, DevToolsIid<DevToolsXA::IAutomationPropertiesStatics>(), &statics);
            WindowsDeleteString(cls);
        }
    }
    if (!statics || !g_protoRoot) return;
    if (IInspectable* el = DevToolsSelFindName(g_protoRoot, name)) {
        void* dobj = nullptr;
        if (SUCCEEDED(el->QueryInterface(DevToolsIid<DevToolsX::IDependencyObject>(), &dobj)) && dobj) {
            HSTRING h = nullptr;
            if (SUCCEEDED(WindowsCreateString(text.c_str(), (UINT32)text.size(), &h))) {
                DevToolsSetAutomationName(statics, dobj, h);
                WindowsDeleteString(h);
            }
            reinterpret_cast<IUnknown*>(dobj)->Release();
        }
        el->Release();
    }
}

static bool CommentsShown();
static size_t CommentCount();
// Retain the named menu objects before WinUI moves their visuals into its popup tree.
static const wchar_t* g_toolbarMenuAnchor = nullptr;
static std::vector<std::pair<std::wstring, winrt::Windows::Foundation::IInspectable>> g_toolbarMenuParts;
static winrt::weak_ref<DevToolsX::FrameworkElement> g_toolbarMenuPresenter;
static bool ToolbarMenuIsOpen() { return g_toolbarMenu != nullptr; }

static winrt::Windows::Foundation::IInspectable ToolbarPart(const wchar_t* name)
{
    winrt::Windows::Foundation::IInspectable part{ nullptr };
    if (IInspectable* element = g_protoRoot && name ? DevToolsSelFindName(g_protoRoot, name) : nullptr)
        winrt::attach_abi(part, element);
    if (!part && name) {
        for (auto const& entry : g_toolbarMenuParts)
            if (entry.first == name) return entry.second;
    }
    return part;
}

static void SetToolbarMenuTheme(int requestedTheme)
{
    try {
        for (const wchar_t* name : { L"DevToolsCommentsMenu", L"DevToolsMoreMenu" }) {
            if (auto menu = ToolbarPart(name).try_as<DevToolsXC::MenuFlyout>()) {
                DevToolsX::Style style{ winrt::xaml_typename<DevToolsXC::MenuFlyoutPresenter>() };
                style.Setters().Append(DevToolsX::Setter{ DevToolsX::FrameworkElement::RequestedThemeProperty(),
                    winrt::box_value(static_cast<DevToolsX::ElementTheme>(requestedTheme)) });
                menu.MenuFlyoutPresenterStyle(style);
            }
        }
        if (auto presenter = g_toolbarMenuPresenter.get())
            presenter.RequestedTheme(static_cast<DevToolsX::ElementTheme>(requestedTheme));
    } catch (const winrt::hresult_error& e) {
        DevToolsOverlayLog(L"overlay.toolbar menu theme failed hr=0x%08X", e.code().value);
    }
}

static bool CloseToolbarMenu(bool focusAnchor)
{
    if (!g_toolbarMenu) return false;
    const wchar_t* menu = g_toolbarMenu;
    const wchar_t* anchorName = g_toolbarMenuAnchor;
    g_toolbarMenu = g_toolbarMenuAnchor = nullptr;
    try {
        ToolbarPart(menu).as<DevToolsXC::MenuFlyout>().Hide();
        if (focusAnchor) {
            if (auto anchor = ToolbarPart(anchorName).try_as<DevToolsX::UIElement>())
                anchor.Focus(DevToolsX::FocusState::Keyboard);
        }
    } catch (const winrt::hresult_error& e) {
        DevToolsOverlayLog(L"overlay.toolbar menu close failed hr=0x%08X", e.code().value);
    }
    UpdateCatcherHole();
    return true;
}

static void SeedToolbarMenuObject(winrt::Windows::Foundation::IInspectable const& object)
{
    if (!object || !g_pickDiag || !g_pickSeed) return;
    InstanceHandle handle = 0;
    const HRESULT hr = g_pickDiag->GetHandleFromIInspectable(
        reinterpret_cast<IInspectable*>(winrt::get_abi(object)), &handle);
    if (SUCCEEDED(hr) && handle) g_pickSeed(handle);
    else DevToolsOverlayLog(L"overlay.toolbar menu exclusion failed hr=0x%08X", hr);
}

static std::string DefaultMode();
static void SetStartModeUi(const std::string& mode);
static DevToolsXCP::FlyoutPlacementMode ToolbarMenuPlacement()
{
    return CornerIsTop(g_corner) ? DevToolsXCP::FlyoutPlacementMode::Bottom : DevToolsXCP::FlyoutPlacementMode::Top;
}

static void PrepareToolbarMenu(const wchar_t* name, const wchar_t* anchor)
{
    if (g_toolbarMenu && wcscmp(g_toolbarMenu, name) != 0) CloseToolbarMenu();
    auto menu = ToolbarPart(name).as<DevToolsXC::MenuFlyout>();
    menu.Placement(ToolbarMenuPlacement());
    const auto anchorElement = ToolbarPart(anchor).as<DevToolsX::FrameworkElement>();
    SetToolbarMenuTheme(DevToolsBuildPalette().highContrast ? DevToolsTheme::kDefault : static_cast<int>(anchorElement.ActualTheme()));
    if (wcscmp(name, L"DevToolsCommentsMenu") == 0) {
        ToolbarPart(L"DevToolsShowAllComments").as<DevToolsXC::MenuFlyoutItem>()
            .Text(L"Open comments list (" + std::to_wstring(CommentCount()) + L")");
        ProtoSetToggle(L"DevToolsToggleMarkers", L"", L"", CommentsShown());
    } else {
        ProtoSetToggle(L"DevToolsProtoPin", L"", L"", g_pinned);
        SetStartModeUi(DefaultMode());
    }
    g_toolbarMenu = name;
    g_toolbarMenuAnchor = anchor;
}

static void WireToolbarMenu(const wchar_t* name, const wchar_t* anchor)
{
    try {
        auto menu = ToolbarPart(name).as<DevToolsXC::MenuFlyout>();
        SeedToolbarMenuObject(menu);
        for (auto const& item : menu.Items()) SeedToolbarMenuObject(item);
        menu.Opening([name, anchor](auto&&, auto&&) {
            try { PrepareToolbarMenu(name, anchor); }
            catch (const winrt::hresult_error& e) {
                DevToolsOverlayLog(L"overlay.toolbar menu opening failed hr=0x%08X", e.code().value);
            }
        });
        menu.Opened([name](auto&&, auto&&) {
            try {
                auto flyout = ToolbarPart(name).as<DevToolsXC::MenuFlyout>();
                auto item = flyout.Items().GetAt(0).as<DevToolsX::DependencyObject>();
                auto root = ToolbarPart(L"DevToolsBar").as<DevToolsX::FrameworkElement>().XamlRoot();
                for (auto const& popup : DevToolsXM::VisualTreeHelper::GetOpenPopupsForXamlRoot(root)) {
                    for (auto node = item; node; node = DevToolsXM::VisualTreeHelper::GetParent(node)) {
                        if (node != popup.Child()) continue;
                        auto presenter = popup.Child().as<DevToolsX::FrameworkElement>();
                        g_toolbarMenuPresenter = winrt::make_weak(presenter);
                        SeedToolbarMenuObject(popup);
                        SeedToolbarMenuObject(presenter);
                        DevToolsXA::AutomationProperties::SetAutomationId(presenter, name);
                        break;
                    }
                }
                UpdateCatcherHole();
                DevToolsOverlayLog(L"overlay.toolbar menu %s open corner=%d", name, g_corner);
            } catch (const winrt::hresult_error& e) {
                DevToolsOverlayLog(L"overlay.toolbar menu opened failed hr=0x%08X", e.code().value);
            }
        });
        menu.Closed([name](auto&&, auto&&) {
            if (g_toolbarMenu && wcscmp(g_toolbarMenu, name) == 0)
                g_toolbarMenu = g_toolbarMenuAnchor = nullptr;
            g_toolbarMenuPresenter = {};
            UpdateCatcherHole();
            if (!g_pinned && !g_barHover && g_toolbarFocusWithin == 0)
                SetTimer(nullptr, 0, 50, &ToolbarFocusLeftProc);
        });
    } catch (const winrt::hresult_error& e) {
        DevToolsOverlayLog(L"overlay.toolbar menu wiring failed hr=0x%08X", e.code().value);
    }
}

static void SetCommentsShown(bool on);
static void ShowAllComments();
static void OnShowAllCommentsClick(void*, void*) { CloseToolbarMenu(); ShowAllComments(); }
// Hidden markers stay hidden on the next launch; showing them again clears that.
static void OnToggleMarkersClick(void*, void*)
{
    CloseToolbarMenu();
    const bool now = !CommentsShown();
    DevToolsSettingsSetBool(L"CommentMarkersHidden", !now);
    SetCommentsShown(now);
}

// The mode winapp run starts a WinUI project in when --devtools isn't given: on, headless or off. One per-user
// setting, shared with `winapp config` (run.devtools); unset means on.
static std::string DefaultMode()
{
    const std::wstring mode = DevToolsSettingsGetText(L"DefaultMode");
    return mode == L"headless" ? "headless" : mode == L"off" ? "off" : "on";
}

// "When the app starts" is a view of that setting: Show toolbar is on, Hide toolbar is headless, Don't start is off.
// The re-enable hint under the radios is only visible while "off" is the selected mode (issue #1148).
static void SetStartModeUi(const std::string& mode)
{
    const wchar_t* name = mode == "headless" ? L"DevToolsStartHidden" : mode == "off" ? L"DevToolsStartOff" : L"DevToolsStartShown";
    for (const wchar_t* item : { L"DevToolsStartShown", L"DevToolsStartHidden", L"DevToolsStartOff" })
        ProtoSetToggle(item, L"", L"", wcscmp(item, name) == 0);
    ProtoSetPartVisible(L"DevToolsStartOffHint", mode == "off");
}

// Hidden for this run: DevTools stays attached, and Ctrl+Shift+F12 or Overlay.show brings the toolbar back. A mode
// left on with no toolbar to show it would be a trap, so hiding turns it off.
static void ExitPickMode();
static void OnHideToolbarClick(void*, void*)
{
    CloseToolbarMenu();
    ExitPickMode();
    DevToolsOverlay_SetToolbarVisible(false);
    DevToolsOverlayLog(L"overlay.toolbar hidden from its menu");
}

static void SetDefaultMode(const char* mode)
{
    CloseToolbarMenu();
    DevToolsSettingsSetText(L"DefaultMode", mode);
    SetStartModeUi(mode);
    DevToolsOverlayLog(L"overlay.toolbar default mode -> %S (persisted)", mode);
}
static void OnStartShownClick(void*, void*) { SetDefaultMode("on"); }
static void OnStartHiddenClick(void*, void*) { SetDefaultMode("headless"); }
// Reopen after WinUI finishes dismissing the clicked item so the recovery instruction remains visible.
static void OnStartOffClick(void*, void*)
{
    SetDefaultMode("off");
    try {
        auto anchor = ToolbarPart(L"DevToolsProtoMore").as<DevToolsX::FrameworkElement>();
        if (!anchor) {
            DevToolsOverlayLog(L"overlay.toolbar recovery instruction has no toolbar anchor");
            return;
        }
        auto weak = winrt::make_weak(anchor);
        if (!anchor.DispatcherQueue().TryEnqueue([weak]() {
            try {
                auto target = weak.get();
                if (target && target.IsLoaded() && g_toolbarVisible)
                    ToolbarPart(L"DevToolsMoreMenu").as<DevToolsXC::MenuFlyout>().ShowAt(target);
            } catch (const winrt::hresult_error& e) {
                DevToolsOverlayLog(L"overlay.toolbar recovery instruction failed hr=0x%08X", e.code().value);
            }
        })) DevToolsOverlayLog(L"overlay.toolbar recovery instruction queue refused");
    } catch (const winrt::hresult_error& e) {
        DevToolsOverlayLog(L"overlay.toolbar recovery instruction scheduling failed hr=0x%08X", e.code().value);
    }
}
static std::wstring ChipSurfaceLabel()
{
    const std::vector<DevToolsSurface>& all = DevToolsOverlay_AllSurfaces();
    if (all.size() < 2) return std::wstring();
    std::wstring name = g_toolbarSurface.displayName;
    if (name.empty()) return std::wstring();
// The pill is anchored to a screen corner and re-places itself on every width change, so an untruncated window
// title would push it across the surface.
    const size_t kMax = 22;
    if (name.size() > kMax) {
        const size_t sep = name.rfind(L" \u00B7 ");
        std::wstring tail = (sep == std::wstring::npos) ? std::wstring() : name.substr(sep + 3);
        if (!tail.empty() && tail.size() <= kMax) return tail;
        name = std::wstring(L"\u2026") + name.substr(name.size() - (kMax - 1));
    }
    return name;
}

static void UpdateChipLabel()
{
    const bool picking = (g_pickCatcher != nullptr);
    const bool commentMode = CommentModeOn();
    const std::wstring surface = ChipSurfaceLabel();
    ProtoSetPartText(L"DevToolsChipLabel", ChipLabel(picking, commentMode, surface));
    ProtoSetPartAutomationName(L"DevToolsChip", ChipAutomationName(picking, commentMode));
    ProtoSetPartVisible(L"DevToolsChipDotOn", picking);
    ProtoSetPartVisible(L"DevToolsGlowHaloOn", picking);
    ProtoSetPartVisible(L"DevToolsChipDot", !picking);
    ProtoSetPartVisible(L"DevToolsGlowHalo", !picking);
    DevToolsOverlayLog(L"overlay.pill label picking=%d commentMode=%d surface='%s'", (int)picking, (int)commentMode,
                       surface.c_str());
}

// Re-anchor from SizeChanged, after ActualWidth reflects the new label. Label assignment alone cannot supply
// the final width.
// Re-anchor after SizeChanged because text assignment alone lacks final ActualWidth.
static void OnChipResize(void*, void*)
{
    if (!g_railL) return;
    void* fe = nullptr;
    if (FAILED(g_railL->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) || !fe) return;
    double w = 0.0;
    HRESULT hr = DevToolsGetActualWidth(fe, &w);
    reinterpret_cast<IUnknown*>(fe)->Release();
    if (FAILED(hr) || w <= 0.0) return;
    if (w == g_chipW) return;
    g_chipW = w;
    if (!g_aRowOpen && !g_dragging) MoveOverlayElement(g_railL, ChipX(), ChipY());
    DevToolsOverlayLog(L"overlay.pill width=%.0f -> anchored %s position=%d", w,
                  CornerIsCenter(g_corner) ? L"center" : (CornerIsLeft(g_corner) ? L"left" : L"right"),
                  g_corner);
}

static void SetCorner(int corner, bool persist)
{
    if (corner < 0 || corner >= kCornerCount) return;
    g_corner = corner;
    if (persist) DevToolsSettingsSetIndex(L"ToolbarCorner", corner);
    ShowARow(g_aRowOpen);
    if (g_pinned) ProtoSetPinned(true);
    DevToolsOverlayLog(L"overlay.toolbar position -> %d (persisted=%d)", corner, (int)persist);
}

static void ShowSnapTargets(bool on)
{
    for (int i = 0; i < kCornerCount; ++i) {
        if (!g_snapUi[i]) continue;
        if (on) { double x = 0, y = 0; SnapTargetPos(i, &x, &y); MoveOverlayElement(g_snapUi[i], x, y); }
        SetOverlayElementVisible(g_snapUi[i], on && g_toolbarVisible);
    }
    if (!on) for (int i = 0; i < kCornerCount; ++i) {
        wchar_t nm[16]; _snwprintf_s(nm, _countof(nm), _TRUNCATE, L"DevToolsSnapNear%d", i);
        ProtoSetPartVisible(nm, false);
    }
}

static int NearestCornerForPill(double px, double py)
{
    return NearestCorner(ClientGeom(), g_chipW, (double)kChipH, px, py);
}

static void HighlightSnapTarget(int corner)
{
    for (int i = 0; i < kCornerCount; ++i) {
        wchar_t nm[16]; _snwprintf_s(nm, _countof(nm), _TRUNCATE, L"DevToolsSnapNear%d", i);
        ProtoSetPartVisible(nm, i == corner);
    }
}

// The pointer is tracked through a full-canvas catcher (see drag-catcher.xaml) rather than a pointer capture:
// the overlay has no CapturePointer wrapper, and a catcher that covers the whole client area gives the same
// guarantee (the pointer cannot leave it) using the primitive this file already trusts.
static IInspectable* g_dragCatcher = nullptr;
static EventSink g_dragMoveSink, g_dragUpSink;
static double g_dragOffX = 0.0, g_dragOffY = 0.0;
static int    g_dragNear = CornerBR;

static void EndToolbarDrag();
static void OnDragCatcherMoved(void*, void*);
static void OnDragCatcherReleased(void*, void*);

static void OnDragHandlePressed(void*, void*)
{
    if (g_dragging || !g_toolbarChildren || !g_railL) return;
    POINT cur{ 0, 0 }; GetCursorPos(&cur);
    float dx = 0, dy = 0;
    if (!DevToolsSurface_ScreenToLocal(g_toolbarSurface, cur.x, cur.y, &dx, &dy)) return;

    g_dragging = true;
    ShowARow(false);
    g_dragOffX = (double)dx - ChipX();
    g_dragOffY = (double)dy - ChipY();
    if (g_dragOffX < 0.0 || g_dragOffX > g_chipW) g_dragOffX = g_chipW / 2.0;
    if (g_dragOffY < 0.0 || g_dragOffY > (double)kChipH) g_dragOffY = (double)kChipH / 2.0;
    g_dragNear = g_corner;
    ShowSnapTargets(true);
    HighlightSnapTarget(g_dragNear);

    const int w = (int)g_protoW, h = (int)g_protoH;
    std::wstring markup = DevToolsXamlSubst(DevToolsXaml::DragCatcher, {
        { L"W", std::to_wstring(w) }, { L"H", std::to_wstring(h) } });
    IInspectable* catcher = nullptr;
    if (FAILED(LoadMarkup(markup, &catcher)) || !catcher) { EndToolbarDrag(); return; }
    if (g_pickDiag && g_pickSeed) {
        InstanceHandle ch = 0;
        if (SUCCEEDED(g_pickDiag->GetHandleFromIInspectable(catcher, &ch)) && ch) g_pickSeed(ch);
    }
    void* cuiEl = nullptr;
    if (SUCCEEDED(catcher->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &cuiEl)) && cuiEl) {
        g_dragMoveSink.Bind(&IID_PointerEventHandler_Del, &OnDragCatcherMoved);
        g_dragUpSink.Bind(&IID_PointerEventHandler_Del, &OnDragCatcherReleased);
        __int64 mtok = 0, rtok = 0;
        DevToolsAddPointerMoved(cuiEl, &g_dragMoveSink, &mtok);
        DevToolsAddPointerReleased(cuiEl, &g_dragUpSink, &rtok);
        reinterpret_cast<IUnknown*>(cuiEl)->Release();
    }
    IInspectable* cui = nullptr;
    if (SUCCEEDED(catcher->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), reinterpret_cast<void**>(&cui))) && cui) {
        unsigned int n = 0; DevToolsVecGetSize(g_toolbarChildren, &n); DevToolsVecInsertAt(g_toolbarChildren, n, cui); cui->Release();
    }
    g_dragCatcher = catcher;
    DevToolsOverlayLog(L"overlay.toolbar drag started at %.0f,%.0f (offset %.0f,%.0f)", (double)dx, (double)dy, g_dragOffX, g_dragOffY);
}

static void OnDragCatcherMoved(void*, void*)
{
    if (!g_dragging || !g_railL) return;
    POINT cur{ 0, 0 }; GetCursorPos(&cur);
    float dx = 0, dy = 0;
    if (!DevToolsSurface_ScreenToLocal(g_toolbarSurface, cur.x, cur.y, &dx, &dy)) { EndToolbarDrag(); return; }
    double x = (double)dx - g_dragOffX, y = (double)dy - g_dragOffY;
    if (x < -20.0) x = -20.0; if (x > g_protoW - 40.0) x = g_protoW - 40.0;
    if (y < -6.0)  y = -6.0;  if (y > g_protoH - 16.0) y = g_protoH - 16.0;
    MoveOverlayElement(g_railL, x, y);
    const int nearest = NearestCornerForPill(x, y);
    if (nearest != g_dragNear) { g_dragNear = nearest; HighlightSnapTarget(nearest); }
}

static void EndToolbarDrag()
{
    if (g_dragCatcher) {
        if (g_toolbarChildren) {
            unsigned int idx = 0; unsigned char found = 0;
            if (SUCCEEDED(DevToolsVecIndexOf(g_toolbarChildren, g_dragCatcher, &idx, &found)) && found)
                DevToolsVecRemoveAt(g_toolbarChildren, idx);
        }
        g_dragCatcher->Release();
        g_dragCatcher = nullptr;
    }
    ShowSnapTargets(false);
    g_dragging = false;
}

// Release: snap to the highlighted corner, then MORPH IN again there, closing the loop the press opened.
static void OnDragCatcherReleased(void*, void*)
{
    if (!g_dragging) return;
    const int landed = g_dragNear;
    EndToolbarDrag();
    SetCorner(landed, /*persist*/ true);
    if (!g_aRowOpen) ShowARow(true);
    DevToolsOverlayLog(L"overlay.toolbar drag released -> corner=%d", landed);
}

static void OnDragHandleEnter(void*, void*) { ProtoSetPartVisible(L"DevToolsDragHaloOn", true); }
static void OnDragHandleExit(void*, void*)  { ProtoSetPartVisible(L"DevToolsDragHaloOn", false); }

// The collapsed pill drags like the bar's handle. A press that ends without moving opens the bar where it was.
static void OnPillPressed(const winrt::Windows::Foundation::IInspectable& sender, const DevToolsXI::PointerRoutedEventArgs& args)
{
    if (!args.GetCurrentPoint(nullptr).Properties().IsLeftButtonPressed()) return;
    // The Button captured the pointer; the drag catcher has to receive it instead.
    sender.as<DevToolsX::UIElement>().ReleasePointerCaptures();
    OnDragHandlePressed(nullptr, nullptr);
}

// Enter, Space and UI Automation's Invoke open the bar.
static void OnPillInvoked(void*, void*)
{
    if (g_dragging || !g_toolbarVisible) return;
    ShowARow(true);
}

static void ClearAdorner();


// Re-anchor the toolbar to the current client size. Best-effort: if the client size can't be read, keep the
// last-known anchor rather than moving anything off-screen.
// Resize re-anchors toolbar; build-time bottom-right coordinates go stale with client size.
static void AlignOverlayOrigin(void*, void*)
{
    if (!g_protoRoot || !g_popup) return;
    static bool aligning = false;
    if (aligning) return;
    aligning = true;
    try {
        winrt::Windows::Foundation::IInspectable object{ nullptr }, popupObject{ nullptr };
        winrt::copy_from_abi(object, g_protoRoot);
        winrt::copy_from_abi(popupObject, g_popup);
        auto canvas = object.as<DevToolsX::UIElement>();
        auto popup = popupObject.as<DevToolsXCP::Popup>();
        auto root = canvas.XamlRoot();
        if (root && popup.IsOpen() && root.Content()) {
// Every overlay child is positioned in XamlRoot DIPs, just like HitTestForXamlRoot. Measure the actual
// Popup child, not its padded parent.
            auto origin = canvas.TransformToVisual(nullptr).TransformPoint({ 0, 0 });
            if (std::isfinite(origin.X) && std::isfinite(origin.Y)) {
                if (std::abs(origin.X) > 0.01f)
                    popup.HorizontalOffset(DevToolsInspector::PopupOffset(popup.HorizontalOffset(), origin.X));
                if (std::abs(origin.Y) > 0.01f)
                    popup.VerticalOffset(DevToolsInspector::PopupOffset(popup.VerticalOffset(), origin.Y));
            }
        }
    } catch (...) {
    }
    aligning = false;
}

static void RepositionToolbar()
{
    DevToolsSurface fresh;
    if (!DevToolsSurface_Resolve(g_pickDiag, g_toolbarSurface.rootHandle, &fresh) ||
        fresh.xamlRootKey != g_toolbarSurface.xamlRootKey) return;
    g_toolbarSurface = fresh;
    const int wDip = (int)fresh.contentW, hDip = (int)fresh.contentH;
    if (wDip <= 0 || hDip <= 0) return;
    g_protoW = (double)wDip; g_protoH = (double)hDip;
    g_titleBarInsetDip = MeasureTitleBarInsetDip();
    ShowARow(g_aRowOpen);
    if (g_pinned)       ProtoSetPinned(true);
    if (g_dragging)     ShowSnapTargets(true);
}
static void ReanchorSelectionNow();
static void OnClientResize(void*, void*)
{
    RepositionToolbar();
    ReanchorSelectionNow();
}

// Begin a named one-shot Storyboard declared in toolbar.xaml's Canvas.Resources, resolved by name in the loaded
// fragment's namescope (IFrameworkElement::FindName) and started via IStoryboard::Begin, both projected.
static void BeginNamedStoryboard(IInspectable* root, const wchar_t* name)
{
    if (!root || !name) return;
    void* fe = nullptr;
    if (FAILED(root->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) || !fe) return;
    IInspectable* sbInsp = nullptr;
    HSTRING hname = nullptr;
    if (SUCCEEDED(WindowsCreateString(name, (UINT32)wcslen(name), &hname))) {
        DevToolsFindName(fe, hname, &sbInsp);
        WindowsDeleteString(hname);
    }
    reinterpret_cast<IUnknown*>(fe)->Release();
    if (!sbInsp) { DevToolsOverlayLog(L"overlay.toolbar storyboard '%s' not found", name); return; }
    void* sb = nullptr;
    if (SUCCEEDED(sbInsp->QueryInterface(DevToolsIid<DevToolsXMA::IStoryboard>(), &sb)) && sb) {
        HRESULT hrB = DevToolsStoryboardBegin(sb);
        DevToolsOverlayLog(L"overlay.toolbar storyboard '%s' Begin hr=0x%08X", name, hrB);
        reinterpret_cast<IUnknown*>(sb)->Release();
    }
    sbInsp->Release();
}
static void PlayAttachPulse(IInspectable* overlayRoot) { BeginNamedStoryboard(overlayRoot, L"DevToolsAttachPulse"); }

// Subscribe the app panel's IFrameworkElement::add_SizeChanged (projected; no slot to name) so window resizes
// re-anchor the top-right toolbar (RepositionToolbar).
static void* g_resizeSource = nullptr;
static __int64 g_resizeToken = 0;
static void UnwireResizeTracking()
{
    if (!g_resizeSource) return;
    const HRESULT hr = DevToolsRemoveSizeChanged(g_resizeSource, g_resizeToken);
    DevToolsOverlayLog(L"overlay.toolbar resize-track remove_SizeChanged hr=0x%08X", hr);
    reinterpret_cast<IUnknown*>(g_resizeSource)->Release();
    g_resizeSource = nullptr;
}
static void WireResizeTracking(IInspectable* panelInsp)
{
    UnwireResizeTracking();
    if (!panelInsp) return;
    void* fe = nullptr;
    if (FAILED(panelInsp->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) || !fe) {
        DevToolsOverlayLog(L"overlay.toolbar resize-track: panel is not a FrameworkElement");
        return;
    }
    g_resizeSink.Bind(&IID_SizeChanged_Del, &OnClientResize);
    HRESULT hr = DevToolsAddSizeChanged(fe, &g_resizeSink, &g_resizeToken);
    if (SUCCEEDED(hr)) g_resizeSource = fe;
    else reinterpret_cast<IUnknown*>(fe)->Release();
    DevToolsOverlayLog(L"overlay.toolbar resize-track add_SizeChanged hr=0x%08X", hr);
}


// Wire the toolbar. MUST run on the UI thread.
static void WirePrototype(IInspectable* canvas)
{
    if (!canvas || !g_toolbarChildren) return;
    CloseToolbarMenu();
    g_toolbarMenuParts.clear();
// Cache the overlay Canvas root (AddRef'd for the agent lifetime): it owns the namescope the reveal storyboards
// resolve through (BeginNamedStoryboard -> FindName), and is the same root PlayAttachPulse is handed at build.
    // Canvas root is AddRef'd because storyboard resolution uses its namescope for agent lifetime.
    if (g_protoRoot != canvas) {
        if (g_protoRoot) g_protoRoot->Release();
        g_protoRoot = canvas;
        g_protoRoot->AddRef();
        void* fe = nullptr;
        if (SUCCEEDED(canvas->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) && fe) {
            const auto iid = DevToolsIid<winrt::Windows::Foundation::EventHandler<winrt::Windows::Foundation::IInspectable>>();
            g_originSink.Bind(&iid, &AlignOverlayOrigin);
            winrt::event_token token{};
            const HRESULT originHr = DevToolsAbi<DevToolsX::IFrameworkElement>(fe)->add_LayoutUpdated(&g_originSink, &token);
            if (FAILED(originHr)) DevToolsOverlayLog(L"overlay.origin LayoutUpdated subscription FAILED hr=0x%08X", originHr);
            reinterpret_cast<IUnknown*>(fe)->Release();
        }
    }
    for (const wchar_t* name : { L"DevToolsCommentsMenu", L"DevToolsShowAllComments", L"DevToolsToggleMarkers",
            L"DevToolsMoreMenu", L"DevToolsHideToolbar", L"DevToolsProtoPin", L"DevToolsStartShown",
            L"DevToolsStartHidden", L"DevToolsStartOff", L"DevToolsStartOffHint" }) {
        auto part = ToolbarPart(name);
        if (part) g_toolbarMenuParts.emplace_back(name, part);
        else DevToolsOverlayLog(L"overlay.toolbar missing menu part %s", name);
    }
    if (!g_canvasStatics) {
        HSTRING cn = nullptr;
        if (SUCCEEDED(WindowsCreateString(L"Microsoft.UI.Xaml.Controls.Canvas",
                (UINT32)wcslen(L"Microsoft.UI.Xaml.Controls.Canvas"), &cn))) {
            RoGetActivationFactory(cn, DevToolsIid<DevToolsXC::ICanvasStatics>(), reinterpret_cast<void**>(&g_canvasStatics));
            WindowsDeleteString(cn);
        }
    }

    auto childAt = [&](unsigned int i, IInspectable** out) -> bool {
        *out = nullptr; return SUCCEEDED(DevToolsVecGetAt(g_toolbarChildren, i, out)) && *out;
    };
    auto borderItems = [&](unsigned int canvasIdx, IInspectable** outBorderUi, IInspectable** outItems) -> bool {
        *outBorderUi = nullptr; *outItems = nullptr;
        IInspectable* borderInsp = nullptr;
        if (FAILED(DevToolsVecGetAt(g_toolbarChildren, canvasIdx, &borderInsp)) || !borderInsp) return false;
        borderInsp->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), reinterpret_cast<void**>(outBorderUi));
        IInspectable* sp = nullptr;
        { IInspectable* border = nullptr;
          if (SUCCEEDED(borderInsp->QueryInterface(DevToolsIid<DevToolsXC::IBorder>(), reinterpret_cast<void**>(&border))) && border) {
              DevToolsBorderGetChild(border, &sp); border->Release();
          } }
        borderInsp->Release();
        if (!sp) return false;
        IInspectable* panel = nullptr;
        if (SUCCEEDED(sp->QueryInterface(DevToolsIid<DevToolsXC::IPanel>(), reinterpret_cast<void**>(&panel))) && panel) {
            IInspectable* kids = nullptr;
            if (SUCCEEDED(DevToolsPanelGetChildren(panel, &kids)) && kids) {
                kids->QueryInterface(DevToolsIid<DevToolsUIElementVector>(), reinterpret_cast<void**>(outItems));
                kids->Release();
            }
            panel->Release();
        }
        sp->Release();
        return *outItems != nullptr;
    };
    auto wireNamedClick = [&](const wchar_t* name, EventSink& sink, void (*cb)(void*, void*)) {
        try {
            auto part = ToolbarPart(name);
            if (auto split = part.try_as<DevToolsXC::ToggleSplitButton>()) {
                split.IsCheckedChanged([cb](DevToolsXC::ToggleSplitButton const& sender, auto&&) {
                    if (g_syncingCommentToggle) return;
                    cb(winrt::get_abi(sender), nullptr);
                    ProtoSetToggle(L"DevToolsProtoComments", L"", L"", g_pickCatcher && CommentModeOn());
                });
            } else if (auto item = part.try_as<DevToolsXC::MenuFlyoutItem>()) {
                item.Click([cb](auto const& sender, auto const& args) { cb(winrt::get_abi(sender), winrt::get_abi(args)); });
            } else if (auto button = part.try_as<DevToolsXCP::ButtonBase>()) {
                sink.Bind(&IID_RoutedEventHandler_Del, cb);
                __int64 token = 0;
                const HRESULT hr = DevToolsAddClick(winrt::get_abi(button), &sink, &token);
                if (FAILED(hr)) DevToolsOverlayLog(L"overlay.toolbar click %s failed hr=0x%08X", name, hr);
            } else DevToolsOverlayLog(L"overlay.toolbar click %s has no command control", name);
        } catch (const winrt::hresult_error& e) {
            DevToolsOverlayLog(L"overlay.toolbar click %s wiring failed hr=0x%08X", name, e.code().value);
        }
    };
    // Use the C++/WinRT named event instead of a manually indexed vtable slot.
    auto wireUiEvent = [&](IInspectable* uiInsp, DevToolsUiEvent which, EventSink& sink, void (*cb)(void*, void*)) {
        if (!uiInsp) return;
        void* cui = nullptr;
        if (SUCCEEDED(uiInsp->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &cui)) && cui) {
            const auto* iid = which == DevToolsUiEvent::GotFocus || which == DevToolsUiEvent::LostFocus
                ? &IID_RoutedEventHandler_Del : &IID_PointerEventHandler_Del;
            sink.Bind(iid, cb);
            __int64 tok = 0;
            const HRESULT hr = DevToolsAddUiEvent(cui, which, &sink, &tok);
            if (FAILED(hr)) DevToolsOverlayLog(L"overlay.toolbar event subscription failed hr=0x%08X", hr);
            reinterpret_cast<IUnknown*>(cui)->Release();
        }
    };

// Cache the movable groups (AddRef'd for the agent lifetime).
    if (g_aRow)    { g_aRow->Release();    g_aRow = nullptr; }    childAt(1, &g_aRow);
    if (g_railL)   { g_railL->Release();   g_railL = nullptr; }   childAt(0, &g_railL);
    for (int i = 0; i < kCornerCount; ++i) {
        if (g_snapUi[i]) { g_snapUi[i]->Release(); g_snapUi[i] = nullptr; }
        childAt(2 + (unsigned int)i, &g_snapUi[i]);
    }

// Wire the pill (hover-in expand; SizeChanged re-anchors it to the parked corner) + bar (hover-out collapse;
// action and pin clicks; the drag handle's press starts a move).
    if (g_railL) {
        wireUiEvent(g_railL, DevToolsUiEvent::PointerEntered, g_protoSink[0], &OnAExpand);
        wireUiEvent(g_railL, DevToolsUiEvent::GotFocus, g_protoSink[12], &OnPillGotFocus);
        wireNamedClick(L"DevToolsChip", g_protoSink[23], &OnPillInvoked);
        try {
            winrt::Windows::Foundation::IInspectable chip{ nullptr };
            winrt::copy_from_abi(chip, g_railL);
            // Handled events too: the Button marks its own press handled.
            chip.as<DevToolsX::UIElement>().AddHandler(DevToolsX::UIElement::PointerPressedEvent(),
                winrt::box_value(DevToolsXI::PointerEventHandler(&OnPillPressed)), true);
        } catch (...) {
            DevToolsOverlayLog(L"overlay.pill press subscription failed hr=0x%08X", (unsigned)winrt::to_hresult());
        }
        void* fe = nullptr;
        if (SUCCEEDED(g_railL->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) && fe) {
            g_protoSink[8].Bind(&IID_SizeChanged_Del, &OnChipResize);
            __int64 tok = 0;
            HRESULT hr = DevToolsAddSizeChanged(fe, &g_protoSink[8], &tok);
            reinterpret_cast<IUnknown*>(fe)->Release();
            DevToolsOverlayLog(L"overlay.pill add_SizeChanged hr=0x%08X", hr);
        }
    }
    IInspectable* barUi = nullptr; IInspectable* items = nullptr;
    if (borderItems(1, &barUi, &items) && items) {
        IInspectable* handle = nullptr;
        if (SUCCEEDED(DevToolsVecGetAt(items, 0, &handle)) && handle) {
            void* cui = nullptr;
            if (SUCCEEDED(handle->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &cui)) && cui) {
                g_protoSink[9].Bind(&IID_PointerEventHandler_Del, &OnDragHandlePressed);
                __int64 tok = 0;
                HRESULT hr = DevToolsAddPointerPressed(cui, &g_protoSink[9], &tok);
                reinterpret_cast<IUnknown*>(cui)->Release();
                DevToolsOverlayLog(L"overlay.toolbar drag-handle add_PointerPressed hr=0x%08X", hr);
            }
            wireUiEvent(handle, DevToolsUiEvent::PointerEntered, g_protoSink[10], &OnDragHandleEnter);
            wireUiEvent(handle, DevToolsUiEvent::PointerExited,  g_protoSink[11], &OnDragHandleExit);
            handle->Release();
        }
        wireNamedClick(L"DevToolsProtoPick", g_protoSink[2], &ThenReturnFocus<&OnPickClick>);
        wireNamedClick(L"DevToolsProtoComments", g_protoSink[3], &ThenReturnFocus<&OnCommentModeClick>);
        wireNamedClick(L"DevToolsShowAllComments", g_protoSink[16], &OnShowAllCommentsClick);
        wireNamedClick(L"DevToolsToggleMarkers", g_protoSink[17], &OnToggleMarkersClick);
        WireToolbarMenu(L"DevToolsCommentsMenu", L"DevToolsProtoComments");
        wireNamedClick(L"DevToolsHideToolbar", g_protoSink[19], &OnHideToolbarClick);
        wireNamedClick(L"DevToolsStartShown", g_protoSink[20], &OnStartShownClick);
        wireNamedClick(L"DevToolsStartHidden", g_protoSink[21], &OnStartHiddenClick);
        wireNamedClick(L"DevToolsStartOff", g_protoSink[22], &OnStartOffClick);
        WireToolbarMenu(L"DevToolsMoreMenu", L"DevToolsProtoMore");
        wireNamedClick(L"DevToolsProtoInspect", g_protoSink[4], &ThenReturnFocus<&OnInspectClick>);
        wireNamedClick(L"DevToolsProtoLayout", g_protoSink[5], &ThenReturnFocus<&OnLayoutToggleClick>);
        wireNamedClick(L"DevToolsProtoTheme", g_protoSink[6], &ThenReturnFocus<&OnThemeToggleClick>);
        wireNamedClick(L"DevToolsProtoPin", g_protoSink[7], &OnPinClick);
        items->Release();
    }
    try {
        winrt::Windows::Foundation::IInspectable object{ nullptr };
        winrt::copy_from_abi(object, g_protoRoot);
        object.as<DevToolsX::UIElement>().GettingFocus([](auto&&, DevToolsXI::GettingFocusEventArgs const& args) {
            NoteToolbarGettingFocus(args);
        });
    } catch (const winrt::hresult_error& e) {
        DevToolsOverlayLog(L"overlay.toolbar GettingFocus subscription failed hr=0x%08X", e.code().value);
    }
    if (barUi) {
        try {
            auto bar = ToolbarPart(L"DevToolsBar").as<DevToolsX::UIElement>();
            bar.PointerEntered([](auto&&, auto&&) { g_barHover = true; });
            bar.PointerExited([](auto&&, auto&&) { g_barHover = false; });
        } catch (const winrt::hresult_error& e) {
            DevToolsOverlayLog(L"overlay.toolbar hover wiring failed hr=0x%08X", e.code().value);
        }
        wireUiEvent(barUi, DevToolsUiEvent::PointerExited, g_protoSink[1], &OnACollapse);
        wireUiEvent(barUi, DevToolsUiEvent::GotFocus, g_protoSink[13], &OnToolbarGotFocus);
        wireUiEvent(barUi, DevToolsUiEvent::LostFocus, g_protoSink[14], &OnToolbarLostFocus);
        barUi->Release();
    }

    DevToolsOverlayLog(L"overlay.toolbar wired (bar=%p pill=%p snaps=%p,%p,%p,%p,%p,%p position=%d)",
                  g_aRow, g_railL, g_snapUi[0], g_snapUi[1], g_snapUi[2],
                  g_snapUi[3], g_snapUi[4], g_snapUi[5], g_corner);
}
