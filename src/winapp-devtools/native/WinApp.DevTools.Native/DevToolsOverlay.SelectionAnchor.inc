// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsOverlay.cpp; included into that translation unit, not compiled on its own.

// ============================ Selection chrome and comment flyout ============================
static void SetOverlayElementVisible(IInspectable* el, bool visible);
static bool IsLinkedElement(InstanceHandle handle);
static std::vector<std::wstring> SelOtherComments(InstanceHandle handle);
static void OnInspectClick(void*, void*);

static InstanceHandle g_inspectTarget = 0;
InstanceHandle DevToolsOverlay_InspectorTarget() { return g_inspectTarget; }

// Opens (or focuses) the inspector with lement selected.
static void InspectElement(InstanceHandle element)
{
    g_inspectTarget = element;
    OnInspectClick(nullptr, nullptr);
    g_inspectTarget = 0;
}

static void OnSelOpenClick(void*, void*) { InspectElement(g_selHandle); }

// Tear down the morph anchor (idempotent): remove the subtree from the canvas, release the held parts, and
// release the element-count oracle freeze (the panel's async TextBox parts are gone once the subtree leaves, so
// the live count returns to the frozen baseline).
static void ClearSelectionAnchor()
{
    ResetPickPointer();
    GuestCommentRememberDraft(false);
    // Whatever replaces or removes the flyout, focus inside it goes back to the app without keyboard visuals.
    SelReturnFocus();
    if (g_selDismissTimer) {
        KillTimer(nullptr, g_selDismissTimer);
        g_selDismissTimer = 0;
    }
    g_selDismissGen = 0;
    g_selDismissCommitFailed = false;
    g_selDismissVisualClosed = false;
    ++g_selGen;
    g_selOperationError.clear();
    g_selOperationProperty.clear();

// RemoveAt on the subtree under the pointer makes XAML synthesize a re-entrant PointerExited dispatched against
// the element being destroyed; if g_selPanel/g_selIcon were still set, an in-flight pointer/focus callback
// would reach into a half-torn-down XAML element and access-violate inside Microsoft.UI.Xaml.dll (see obser.
    // Null globals before RemoveAt/Release because XAML can re-enter PointerExited during subtree removal.
    IInspectable* selUi      = g_selUi;      g_selUi      = nullptr;
    IInspectable* selPopup   = g_selPopup;   g_selPopup   = nullptr;
    g_hostLiftedChild = nullptr;
    IInspectable* selPanel   = g_selPanel;   g_selPanel   = nullptr;
    IInspectable* selIcon    = g_selIcon;    g_selIcon    = nullptr;
    IInspectable* selTag     = g_selTag;     g_selTag     = nullptr;
    IInspectable* selComment = g_selComment; g_selComment = nullptr;
    IInspectable* selCmtDel  = g_selCommentDelete; g_selCommentDelete = nullptr;
    g_selFocusWithin = 0;
    g_selExpanded = false;
    g_selDetached = false;
    g_selRootLost = false;
    g_selAnchorPending = false;
    g_selPendingAnchor = RECT{};
    g_selFocusedIsComment = false;

    if (selPopup) {
        void* popup = nullptr;
        if (SUCCEEDED(selPopup->QueryInterface(DevToolsIid<DevToolsXCP::IPopup>(), &popup)) && popup) {
            DevToolsPopupPutIsOpen(popup, false);
            reinterpret_cast<IUnknown*>(popup)->Release();
        }
    }
    UpdateCatcherHole();
    if (selUi && g_canvasChildren) {
        unsigned int idx = 0; unsigned char found = 0;
        if (SUCCEEDED(DevToolsVecIndexOf(g_canvasChildren, selUi, &idx, &found)) && found) DevToolsVecRemoveAt(g_canvasChildren, idx);
    }
    if (selComment) selComment->Release();
    if (selCmtDel)  selCmtDel->Release();
    if (selPopup)   selPopup->Release();
    if (selPanel)   selPanel->Release();
    if (selIcon)    selIcon->Release();
    if (selTag)     selTag->Release();
    if (selUi)      selUi->Release();
    if (g_cardFreeze && g_selFrozen) { g_cardFreeze(0); g_selFrozen = false; }
    SetCommentTarget(0, false);
    g_selCommentId.clear();
    g_selCommentSaved.clear();
}

// Build the flyout (selection-panel.xaml); it is substituted into selection.xaml's $PANEL$ slot.
// A likely source is shown for review next to the checkbox that confirms it.
static std::wstring LikelySourceConfirmationMarkup(bool likely, const std::wstring& authoredXaml)
{
    if (!likely) return L"";
    const DevToolsPalette& pal = DevToolsInverseAcrylicPalette();
    std::wstring m = L"<TextBlock Text=\"Likely source: unique line and identity match; compiled identity and line preservation are unverified.\""
        L" Style=\"{StaticResource CaptionTextBlockStyle}\" TextWrapping=\"Wrap\" Foreground=\"" + pal.textSecondary + L"\"/>";
    if (!authoredXaml.empty())
        m += L"<TextBlock Text=\"" + XmlEscapeLines(authoredXaml) + L"\" FontFamily=\"Consolas\""
            L" Style=\"{StaticResource CaptionTextBlockStyle}\" TextWrapping=\"Wrap\" IsTextSelectionEnabled=\"True\""
            L" Foreground=\"" + pal.textPrimary + L"\" AutomationProperties.AutomationId=\"DevToolsSelAuthoredXaml\"/>";
    return m + L"<CheckBox x:Name=\"DevToolsConfirmLikelySource\" IsChecked=\"False\" HorizontalAlignment=\"Stretch\""
        L" AutomationProperties.AutomationId=\"DevToolsConfirmLikelySource\""
        L" AutomationProperties.Name=\"Use this likely source for my comment\">"
        L"<TextBlock Text=\"Use this likely source for my comment\" TextWrapping=\"Wrap\""
        L" Style=\"{StaticResource CaptionTextBlockStyle}\"/></CheckBox>";
}

// The element's other open comments, read-only; the box above edits this element's own comment.
static std::wstring OtherCommentsMarkup(const std::vector<std::wstring>& others)
{
    if (others.empty()) return L"";
    const DevToolsPalette& pal = DevToolsInverseAcrylicPalette();
    std::wstring m = L"<StackPanel x:Name=\"DevToolsSelOtherComments\" Spacing=\"6\" Margin=\"0,4,0,0\""
        L" AutomationProperties.AutomationId=\"DevToolsSelOtherComments\" AutomationProperties.Name=\"Other comments on this element\">"
        L"<TextBlock Text=\"Also on this element\" Style=\"{StaticResource CaptionTextBlockStyle}\" Foreground=\"" +
        pal.textSecondary + L"\"/>";
    for (const auto& text : others)
        m += L"<Border Background=\"" + pal.surfaceAlt + L"\" CornerRadius=\"4\" Padding=\"8,6\">"
            L"<TextBlock Text=\"" + DevToolsXmlEscape(text, true) + L"\" FontSize=\"12\" TextWrapping=\"Wrap\" MaxLines=\"4\""
            L" TextTrimming=\"CharacterEllipsis\" IsTextSelectionEnabled=\"True\" Foreground=\"" + pal.textPrimary + L"\"/></Border>";
    return m + L"</StackPanel>";
}

static std::wstring BuildSelectionPanelMarkup(const std::wstring& title, const std::wstring& titleTip,
                                              int panelX, int panelY, int panelHeight, int leaderX, int leaderY,
                                              const std::wstring& commentText,
                                              const std::wstring& srcFile, unsigned int srcLine, bool linked,
                                              const std::wstring& authoredState,
                                              const std::wstring& authoredXaml,
                                              const std::vector<std::wstring>& otherComments,
                                              double originX, double originY,
                                              bool readOnly)
{
    const DevToolsPalette& pal = DevToolsInverseAcrylicPalette();

    std::wstring srcText = L"Not linked to source", srcTip, srcFg = pal.textSecondary;
    if (linked && !srcFile.empty()) {
        size_t sl = srcFile.find_last_of(L"/\\");
        srcText = (sl == std::wstring::npos) ? srcFile : srcFile.substr(sl + 1);
        if (srcLine > 0) srcText += L":" + std::to_wstring(srcLine);
        srcTip = srcFile; if (srcLine > 0) srcTip += L" (line " + std::to_wstring(srcLine) + L")";
        srcFg = pal.textPrimary;
    } else {
        srcTip = L"No XAML declaration in your project was found for this element. Comments still save, without a file and line.";
    }
    wchar_t ox[16], oy[16];
    _snwprintf_s(ox, _countof(ox), _TRUNCATE, L"%.3f", originX);
    _snwprintf_s(oy, _countof(oy), _TRUNCATE, L"%.3f", originY);
    const auto guestAuthority = g_guestCommentAuthority.load();

    return DevToolsXamlSubst(DevToolsXaml::SelectionPanel, {
        { L"PANELW",  std::to_wstring(kSelPanelW) },
        { L"PANELH",  std::to_wstring(panelHeight) },
        { L"PANELX",  std::to_wstring(panelX) },
        { L"PANELY",  std::to_wstring(panelY) },
        { L"LEADERX", std::to_wstring(leaderX) },
        { L"LEADERY", std::to_wstring(leaderY) },
        { L"ORIGINX", std::wstring(ox) },
        { L"ORIGINY", std::wstring(oy) },
        { L"PANELTHEME", DevToolsInverseAppRequestedTheme() },
        { L"SURFACE", pal.surface },
        { L"SURFACEALT", pal.surfaceAlt },
        { L"BORDER",  pal.border },
        { L"RIM",     pal.rim },
        { L"TEXT",    pal.textPrimary },
        { L"TEXTDIM", pal.textSecondary },
        { L"ACCENT",  pal.accent },
        { L"CAUTION", pal.caution },
        { L"TITLE",   DevToolsXmlEscape(title.empty() ? std::wstring(L"Element") : title) },
        { L"TITLETIP", DevToolsXmlEscape(titleTip) },
        { L"SOURCE",  DevToolsXmlEscape(srcText) },
        { L"SOURCETIP", DevToolsXmlEscape(srcTip) },
        { L"SOURCEFG", srcFg },
        { L"OTHERS",  OtherCommentsMarkup(otherComments) },
        { L"COMMENT", DevToolsXmlEscape(commentText, true) },
        { L"SOURCECONFIRM", LikelySourceConfirmationMarkup(authoredState == L"likely", authoredXaml) },
        { L"CMTREADONLY", readOnly || !GuestCommentCanEdit(g_selHandle) ? std::wstring(L"True") : std::wstring(L"False") },
        { L"CMTSAVEVIS", readOnly || !GuestCommentCanEdit(g_selHandle) ? std::wstring(L"Collapsed") : std::wstring(L"Visible") },
        { L"CMTLIMIT", g_guestCommentAuthority.load() ? std::wstring(L"65536") : std::wstring(L"0") },
        { L"CMTPLACEHOLDER", readOnly ? std::wstring(L"Mutation access is required to edit comments")
                                      : guestAuthority && !guestAuthority->Bound() ? std::wstring(L"Host comments are unavailable for this launch")
                                      : !GuestCommentCanEdit(g_selHandle) ? std::wstring(guestAuthority
                                            ? L"Finish the earlier host save, then reselect this element"
                                            : L"An earlier comment did not save. Reopen its element first")
                                      : std::wstring(L"Comment on this element&#x2026;") },
        { L"CMTDELVIS", readOnly || !GuestCommentCanEdit(g_selHandle) || commentText.empty() ? std::wstring(L"Collapsed") : std::wstring(L"Visible") },
    });
}

// Build selection.xaml with the overlay's OWN opaque palette (never app {ThemeResource}, /) + the selection
// geometry: the always-present identity tag, plus `panelMarkup` (empty for a selection that did NOT come from
// an in-app pick,).
static std::wstring BuildSelectionMarkup(int w, int h, int tagX, int tagY,
                                         const std::wstring& tagText, const std::wstring& tagDim,
                                         const std::wstring& panelMarkup)
{
    const DevToolsPalette& pal = DevToolsBuildPalette();
    return DevToolsXamlSubst(DevToolsXaml::Selection, {
        { L"W",       std::to_wstring(w) },
        { L"H",       std::to_wstring(h) },
        { L"TAGX",    std::to_wstring(tagX) },
        { L"TAGY",    std::to_wstring(tagY) },
        { L"TAGVIS",  panelMarkup.empty() ? std::wstring(L"Visible") : std::wstring(L"Collapsed") },
        { L"TAGTEXT", DevToolsXmlEscape(tagText) },
        { L"TAGDIM",  DevToolsXmlEscape(tagDim) },
        { L"SURFACE", pal.surface },
        { L"RIM",     pal.rim },
        { L"TEXT",    pal.textPrimary },
        { L"TEXTTERTIARY", pal.textTertiary },
        { L"PANEL",   panelMarkup },
    });
}

// The panel is inserted directly on-canvas at its FINAL position (realized once) and never Canvas-moved on/off
// screen: moving a TextBox-bearing subtree realizes the box's async default-template parts mid-move and crashes
// the app.
static void ShowSelectionAnchor(IInspectable* el, bool fromUserPick)
{
    const bool focusComment = std::exchange(g_selFocusCommentOnOpen, false) && fromUserPick;
    if (!el || !g_canvasChildren || !g_cardRead) return;
    std::wstring type, name, authoredState, authoredXaml; std::vector<DevToolsCardRow> rows;
    if (!g_cardRead(el, &type, &name, &rows, &authoredState, &authoredXaml)) {
        DevToolsOverlayLog(L"overlay.sel read failed");
        return;
    }
// Header reads "Button #CounterButton", not the fully-qualified type -- the panel is 400 DIP wide and the
// namespace ate it (the header was ellipsised before the element's own name was visible).
    size_t dot = type.rfind(L'.');
    std::wstring shortType = (dot == std::wstring::npos) ? type : type.substr(dot + 1);
    std::wstring title = shortType;
    std::wstring titleTip = type;
    if (!name.empty()) { title += L"  #"; title += name; titleTip += L"  #" + name; }

    int w = 0, h = 0; if (!GetClientSizeDips(&w, &h)) { w = 1600; h = 900; }
    const RECT& e = g_selPendingHandle == g_selectedHandle ? g_selectedBounds : g_lastPickBoundsDip;
    bool haveBounds = (e.right > e.left) && (e.bottom > e.top);
    const int ex = haveBounds ? (int)e.left : 16;
    const int ey = haveBounds ? (int)e.top  : 64;
    const int ew = haveBounds ? (int)(e.right - e.left) : 0;
    const int eh = haveBounds ? (int)(e.bottom - e.top) : 0;

// When the element is hard against the top of the client area there is nowhere above to put it, so it drops
// just inside the rect instead of off-screen.
    const int kTagH = 26;
    int tagX = ex - 1;
    int tagY = ey - 2 - kTagH;
    if (tagY < 2) tagY = ey + 2;
    if (tagX < 2) tagX = 2;
    if (tagX > w - 60) tagX = (w > 60) ? w - 60 : 2;
    std::wstring tagDim = haveBounds
        ? (std::to_wstring(ew) + L" \x00D7 " + std::to_wstring(eh))
        : std::wstring();

// PANEL geometry: use the monitor work area when the Popup confirms it is unconstrained; otherwise use the
// client area.
    const int pw = kSelPanelW;
    // Header, comment box, save row and footer, plus the likely-source review and each other comment. The panel still
    // sizes to its content; this only decides where it fits.
    const std::vector<std::wstring> otherComments = SelOtherComments(g_selPendingHandle);
    const int estimatedHeight = 300 + (authoredState == L"likely" ? 120 : 0) + 72 * (int)(std::min)(otherComments.size(), size_t(3));
    DevToolsSelection::Rect viewport{ 0, 0, w, h };
    DevToolsSelection::Rect workArea{};
    if (GetMonitorWorkAreaDips(&workArea)) viewport = workArea;
    const DevToolsSelection::Rect element{ ex, ey, ex + ew, ey + eh };
    RECT toolbarDip{};
    const bool haveToolbar = g_toolbarSurface.xamlRootKey == g_activeSurface.xamlRootKey && ToolbarRectDip(&toolbarDip);
    const DevToolsSelection::Rect toolbar{ toolbarDip.left, toolbarDip.top, toolbarDip.right, toolbarDip.bottom };
    const DevToolsSelection::Placement placement =
        DevToolsSelection::PlacePanel(element, viewport, pw, estimatedHeight, 14, 8, haveToolbar ? &toolbar : nullptr);
    const bool onRight = placement.onRight;
    const int px = placement.panelX;
    const int py = placement.panelY;
    const int panelHeight = placement.panelHeight;
    const int leaderX = onRight ? (ex + ew + 2) : (px + pw);
    int leaderY = ey + eh / 2;
    if (leaderY < 0) leaderY = 0;
    if (leaderY > h - 2) leaderY = (h > 2) ? h - 2 : 0;
    const double originX = onRight ? 0.0 : 1.0;
    double originY = panelHeight > 0 ? ((double)leaderY - (double)py) / (double)panelHeight : 0.0;
    if (originY < 0.0) originY = 0.0; if (originY > 1.0) originY = 1.0;

    ClearSelectionAnchor();

    const bool readOnly = !DevToolsTrust_MutationEnabled();

// Resolve this element's comment identity for the inline comment box: adopt the pending handle stashed by
// DevToolsOverlay_ShowBadgeForHandle, look up any existing in-session comment so the box PREFILLS
// (edit-in-place, same id -> the CLI upserts) rather than showing empty over a saved comment, and snapshot the
// element bou.
    SetCommentTarget(g_selPendingHandle, false); g_selPendingHandle = 0;
    g_selBounds = g_lastPickBoundsDip;
    std::wstring initComment; g_selCommentId.clear();
    SelLookupComment(g_selHandle, &g_selCommentId, &initComment);
    g_selCommentSaved = initComment;

// Use the selected element's own authored location; never silently move its comment anchor.
    std::wstring srcFile; unsigned int srcLine = 0;
    if (g_srcRead && g_selHandle) {
        std::wstring f; unsigned int ln = 0, col = 0;
        if (g_srcRead(g_selHandle, &f, &ln, &col)) { srcFile = f; srcLine = ln; }
    }

// Freeze the element-count oracle at the pre-anchor baseline for the panel's lifetime, BEFORE its comment
// TextBox realizes its default-template internal parts (which XamlDiagnostics re-handles across phases and
// can't be excluded by handle). Only taken when a panel is actually built: a tag-only selection realizes no
// async.
    // Freeze census before TextBox template parts realize; they appear asynchronously and are not
    // handle-excludable.
    if (fromUserPick && g_cardFreeze && !g_selFrozen) { g_cardFreeze(1); g_selFrozen = true; }

    std::wstring panelMarkup;
    if (fromUserPick)
        panelMarkup = BuildSelectionPanelMarkup(title, titleTip, px, py, panelHeight, leaderX, leaderY, initComment,
                                                srcFile, srcLine, IsLinkedElement(g_selHandle), authoredState,
                                                authoredXaml, otherComments, originX, originY, readOnly);

    IInspectable* insp = nullptr;
    const std::wstring selMarkup = BuildSelectionMarkup(w, h, tagX, tagY, title, tagDim, panelMarkup);
    const HRESULT hrSel = LoadMarkup(selMarkup, &insp);
    if (FAILED(hrSel) || !insp) {
        DevToolsOverlayLog(L"overlay.sel load FAILED hr=0x%08X insp=%p markupChars=%zu panelChars=%zu fromUserPick=%d"
                      L" -- the element IS selected but no chrome or panel will render",
                      hrSel, insp, selMarkup.size(), panelMarkup.size(), fromUserPick ? 1 : 0);
        if (insp) { insp->Release(); insp = nullptr; }
        if (g_cardFreeze && g_selFrozen) { g_cardFreeze(0); g_selFrozen = false; }
        return;
    }
    g_selPanel   = DevToolsSelFindName(insp, L"DevToolsSelPanel");
    ResetPickPointer();
    g_selPopup   = DevToolsSelFindName(insp, L"DevToolsSelPopup");
    g_selTag     = DevToolsSelFindName(insp, L"DevToolsSelTag");
    g_selComment = DevToolsSelFindName(insp, L"DevToolsSelComment");
// The Close button doubles as the panel's focus parking spot: moving focus onto it is how a keyboard gesture
// COMMITS the field it leaves (the field's already-wired LostFocus does the write), which is why the keyboard
// helpers below still talk about "the icon" — this is what took its place.
    g_selIcon    = DevToolsSelFindName(insp, L"DevToolsSelClose");
    g_selExpanded = (g_selPanel != nullptr);
    g_selDetached = false;

    // A later teardown bumps the generation again, making any pending commit inert.
    ++g_selGen;
    GuestCommentRestoreDraft(false);

    if (g_selIcon) {
        void* bb = nullptr;
        if (SUCCEEDED(g_selIcon->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), &bb)) && bb) {
            g_selIconClickSink.Bind(&IID_RoutedEventHandler_Del, &OnSelCloseClick);
            __int64 tok = 0;
            DevToolsAddClick(bb, &g_selIconClickSink, &tok);
            reinterpret_cast<IUnknown*>(bb)->Release();
        }
    }
    if (g_selPanel) {
// The panel has its own Popup visual root, so subscribe there rather than on the placement Canvas.
        DevToolsSelWireKey(g_selPanel, g_selKeyDownSink, &OnSelKeyDown);
    }
    if (g_selComment) {
        DevToolsSelWireFocus(g_selComment, DevToolsUiEvent::GotFocus, g_selFocusInSink,
                        readOnly ? &OnSelGotFocus : &OnSelCommentGotFocus);
        DevToolsSelWireFocus(g_selComment, DevToolsUiEvent::LostFocus, g_selFocusOutSink,
                        readOnly ? &OnSelLostFocus : &OnSelCommentLostFocus);
        if (!readOnly) {
            void* box = nullptr;
            if (SUCCEEDED(g_selComment->QueryInterface(DevToolsIid<DevToolsXC::ITextBox>(), &box)) && box) {
                static const GUID changed = DevToolsIid<DevToolsXC::TextChangedEventHandler>();
                g_selCommentChangedSink.Bind(&changed, &OnSelCommentTextChanged);
                __int64 token = 0;
                const auto hr = DevToolsAddTextChanged(box, &g_selCommentChangedSink, &token);
                if (FAILED(hr)) DevToolsOverlayLog(L"overlay.comment text-change subscription failed hr=0x%08X", hr);
                reinterpret_cast<IUnknown*>(box)->Release();
            }
        }
    }
    if (IInspectable* save = DevToolsSelFindName(insp, L"DevToolsSelCommentSave")) {
        if (!readOnly) {
            DevToolsSelWireFocus(save, DevToolsUiEvent::GotFocus, g_selCmtSaveInSink, &OnSelGotFocus);
            DevToolsSelWireFocus(save, DevToolsUiEvent::LostFocus, g_selCmtSaveOutSink, &OnSelLostFocus);
            void* button = nullptr;
            if (SUCCEEDED(save->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), &button)) && button) {
                g_selCommentSaveSink.Bind(&IID_RoutedEventHandler_Del, &OnSelCommentSaveClick);
                __int64 token = 0;
                DevToolsAddClick(button, &g_selCommentSaveSink, &token);
                reinterpret_cast<IUnknown*>(button)->Release();
            }
        }
        save->Release();
    }
// Only present in the markup as Visible when this anchor already has a saved comment, so wiring it is
// unconditional but it cannot be clicked when there is nothing to delete.
    g_selCommentDelete = DevToolsSelFindName(insp, L"DevToolsSelCommentDelete");
    if (g_selCommentDelete && !readOnly) {
        DevToolsSelWireFocus(g_selCommentDelete, DevToolsUiEvent::GotFocus, g_selCmtDelInSink,  &OnSelGotFocus);
        DevToolsSelWireFocus(g_selCommentDelete, DevToolsUiEvent::LostFocus, g_selCmtDelOutSink, &OnSelLostFocus);
        void* bb = nullptr;
        if (SUCCEEDED(g_selCommentDelete->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), &bb)) && bb) {
            g_selCommentDeleteSink.Bind(&IID_RoutedEventHandler_Del, &OnSelCommentDeleteClick);
            __int64 tok = 0;
            DevToolsAddClick(bb, &g_selCommentDeleteSink, &tok);
            reinterpret_cast<IUnknown*>(bb)->Release();
        }
    }
    if (IInspectable* open = DevToolsSelFindName(insp, L"DevToolsSelOpen")) {
        DevToolsSelWireFocus(open, DevToolsUiEvent::GotFocus, g_selOpenInSink,  &OnSelGotFocus);
        DevToolsSelWireFocus(open, DevToolsUiEvent::LostFocus, g_selOpenOutSink, &OnSelLostFocus);
        void* bb = nullptr;
        if (SUCCEEDED(open->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), &bb)) && bb) {
            g_selOpenClickSink.Bind(&IID_RoutedEventHandler_Del, &OnSelOpenClick);
            __int64 tok = 0;
            DevToolsAddClick(bb, &g_selOpenClickSink, &tok);
            reinterpret_cast<IUnknown*>(bb)->Release();
        }
        open->Release();
    }

// Insert under the overlay build scope so the anchor's synchronous markup Add self-excludes from the census
// (like the card/toolbar). The comment TextBox's async default-template parts (realized on-canvas) are covered
// by the oracle freeze taken above.
    IInspectable* cui = nullptr;
    if (SUCCEEDED(insp->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), reinterpret_cast<void**>(&cui))) && cui) {
        unsigned int n = 0; DevToolsVecGetSize(g_canvasChildren, &n);
        if (g_cardScope) g_cardScope(1);
        DevToolsVecInsertAt(g_canvasChildren, n, cui);
        if (g_cardScope) g_cardScope(0);
        g_selUi = cui;
    }
    if (g_selPopup) {
        HostLiftPopup(g_selPopup, g_selectionPopup.get());
        void* popup = nullptr;
        if (SUCCEEDED(g_selPopup->QueryInterface(DevToolsIid<DevToolsXCP::IPopup>(), &popup)) && popup) {
            const HRESULT hrOpen = DevToolsPopupPutIsOpen(popup, true);
            bool constrained = true;
            const HRESULT hrRead = DevToolsPopupGetIsConstrainedToRootBounds(popup, &constrained);
            DevToolsOverlayLog(L"overlay.sel popup open hr=0x%08X constrained=%d(hr=0x%08X)",
                          hrOpen, (int)constrained, hrRead);
            reinterpret_cast<IUnknown*>(popup)->Release();
        }
        if (g_selPanel) {
            try {
                winrt::Windows::Foundation::IInspectable panel{ nullptr };
                winrt::copy_from_abi(panel, g_selPanel);
                panel.as<DevToolsX::FrameworkElement>().SizeChanged([](auto&&, auto&&) { UpdateCatcherHole(); });
            } catch (const winrt::hresult_error&) {}
        }
        UpdateCatcherHole();
        if (g_selPanel) { SelRememberFocus(); KeepChordsInPanel(g_selPanel); }
        // A pick in the app is a request to comment: the box takes focus so typing starts at once. Pointer input
        // was last, so programmatic focus shows a caret without keyboard visuals.
        if (focusComment && g_selComment) {
            void* box = nullptr;
            bool focused = false;
            if (SUCCEEDED(g_selComment->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &box)) && box) {
                DevToolsFocus(box, 3 /*FocusState.Programmatic*/, &focused);
                reinterpret_cast<IUnknown*>(box)->Release();
            }
            // Typing continues an existing comment rather than prepending to it.
            if (focused) DevToolsTextBoxCaretToEnd(g_selComment);
            DevToolsOverlayLog(L"overlay.sel comment box focused on open=%d", (int)focused);
        }
    }
    if (!g_selOperationError.empty())
        SetSelectionOperationError(g_selOperationProperty, g_selOperationError);
    if (g_selPanel) BeginNamedStoryboard(g_selPanel, L"DevToolsSelMorph");
    insp->Release();
    DevToolsOverlayLog(L"overlay.sel shown title=%s fromUserPick=%d tag=%d,%d panel=%s(%d,%d side=%s) others=%zu readOnly=%d"
                  L" element=%d,%d,%d,%d viewport=%d,%d,%d,%d",
                  title.c_str(), (int)fromUserPick, tagX, tagY, g_selPanel ? L"yes" : L"no", px, py,
                  onRight ? L"right" : L"left", otherComments.size(), (int)readOnly,
                  element.left, element.top, element.right, element.bottom,
                  viewport.left, viewport.top, viewport.right, viewport.bottom);
}


static void ShowComposerForElement(IInspectable* el, bool fromCommentMode);
static void ClearComposer();
// Last segment of a dotted XAML type name ("Microsoft.UI.Xaml.Controls.TextBlock" -> "TextBlock").
static std::wstring ShortTypeName(const std::wstring& full)
{
    size_t dot = full.rfind(L'.');
    return (dot == std::wstring::npos) ? full : full.substr(dot + 1);
}
void DevToolsOverlay_ShowBadgeForHandle(IXamlDiagnostics* diag, InstanceHandle target, bool fromUserPick)
{
    if (!diag || !target || !g_cardRead) return;
    IInspectable* el = nullptr;
    if (SUCCEEDED(diag->GetIInspectableFromHandle(target, &el)) && el) {
        RECT bounds{};
        if (!TryElementRootBounds(diag, g_pickRoot, target, &bounds)) {
            el->Release();
            DevToolsOverlayLog(L"overlay.sel target bounds unavailable");
            return;
        }
        g_lastPickBoundsDip = bounds;
        if (g_selectedHandle == target) g_selectedBounds = bounds;
        g_selPendingHandle = target;
// It is a parameter, not stored state — every caller below computes it at its own call site, where how the
// selection was made is unambiguous, and it is consumed immediately.
        ShowSelectionAnchor(el, fromUserPick);
        el->Release();
        // Keep all selection routes on one path so catcher, pipe, and tree selections share tracking/chrome.
        g_selectedHandle = target;
        g_selectedBounds = bounds;
        g_chromeHidden = false;
        WireSelScroller(bounds);
        SyncLayoutAdorner();
        TrackingSync();
    }
}
