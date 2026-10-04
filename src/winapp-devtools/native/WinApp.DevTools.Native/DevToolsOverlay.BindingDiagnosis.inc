// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsOverlay.cpp; included into that translation unit, not compiled on its own.

// ============================ Binding diagnosis in the row ============================ Defined with the other
// visibility helpers further down; needed here to show/hide the diagnosis parts.
static void SetOverlayElementVisible(IInspectable* el, bool visible);
// Calling the relay from the UI thread would deadlock the agent's UI dispatch; the relay explicitly refuses
// that call.
struct DevToolsSelDiagRow {
    size_t       index = 0;
    std::wstring prop;
    std::wstring json;
    std::wstring walkJson;
    bool         restorable = false;
    bool         confirmOwnerRebind = false;
    std::wstring restoreWarning;
    std::wstring editWarning;
    std::wstring restoreDisclosure;
    bool         writesThrough = false;
    std::wstring writesThroughTo;
    bool         applied = false;
};
static DevToolsBindingAskFn        g_bindAsk = nullptr;
static DevToolsPathWalkAskFn       g_walkAsk = nullptr;
static IInspectable*          g_selDiagRoot = nullptr;
static SRWLOCK                g_selDiagLock = SRWLOCK_INIT;
static std::vector<DevToolsSelDiagRow> g_selDiagRows;
static unsigned long long     g_selDiagGen = 0;
static UINT_PTR               g_selDiagTimer = 0;
static bool                   g_selDiagReadOnly = false;
static volatile LONG          g_selDiagBusy = 0;

void DevToolsOverlay_SetBindingAsk(DevToolsBindingAskFn fn) { g_bindAsk = fn; }
void DevToolsOverlay_SetPathWalkAsk(DevToolsPathWalkAskFn fn) { g_walkAsk = fn; }

static void SelDiagSetText(const std::wstring& name, const std::wstring& text)
{
    if (!g_selDiagRoot) return;
    if (IInspectable* el = DevToolsSelFindName(g_selDiagRoot, name.c_str())) {
        void* tb = nullptr;
        if (SUCCEEDED(el->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &tb)) && tb) {
            HSTRING h = nullptr;
            if (SUCCEEDED(WindowsCreateString(text.c_str(), (UINT32)text.size(), &h))) { DevToolsPutText(tb, h); WindowsDeleteString(h); }
            reinterpret_cast<IUnknown*>(tb)->Release();
        }
        el->Release();
    }
}

static void SelDiagShow(const std::wstring& name, bool visible)
{
    if (!g_selDiagRoot) return;
    if (IInspectable* el = DevToolsSelFindName(g_selDiagRoot, name.c_str())) {
        SetOverlayElementVisible(el, visible);
        el->Release();
    }
}

// UI thread only. The panel shows one compact status per bound row; the full diagnosis lives in the inspector.
static void SelDiagApply(const DevToolsSelDiagRow& r)
{
    const std::wstring i = std::to_wstring(r.index);
    DevToolsBindingSummary s = DevToolsBindingRow_Summary(r.json);
// Without the managed agent the native walk still names the segment where a path stops.
    if (s.tone == DevToolsBindingTone::Unknown && !r.walkJson.empty()) {
        const DevToolsPathWalkView w = DevToolsPathWalk_FromJson(r.walkJson, DevToolsBindingRelay_AgentPresent());
        for (const auto& step : w.steps) {
            if (!step.stop || step.outcome == DevToolsWalkOutcome::Resolved || step.outcome == DevToolsWalkOutcome::NotReached)
                continue;
            s.tone = DevToolsBindingTone::Broken;
            s.status = L"\u2715 Broken at " + step.path;
            break;
        }
    }
    const bool bad = s.tone == DevToolsBindingTone::Broken;
    SelDiagSetText(L"DevToolsSelStatus" + i, bad ? std::wstring() : s.status);
    SelDiagShow(L"DevToolsSelStatus" + i, !bad);
    SelDiagSetText(L"DevToolsSelStatusBadText" + i, bad ? s.status : std::wstring());
    SelDiagShow(L"DevToolsSelStatusBad" + i, bad);
    // A screen reader hears what the chip says, not just that there is one.
    const std::wstring spoken = r.prop + L" binding: " + DevToolsBindingRow_SpokenStatus(s.status) +
        (bad ? L". Open binding details in DevTools" : L"");
    if (IInspectable* chip = DevToolsSelFindName(g_selDiagRoot, ((bad ? L"DevToolsSelStatusBad" : L"DevToolsSelStatus") + i).c_str())) {
        try {
            winrt::Windows::Foundation::IInspectable object{ nullptr };
            winrt::copy_from_abi(object, chip);
            DevToolsXA::AutomationProperties::SetName(object.as<DevToolsX::DependencyObject>(), spoken);
        } catch (...) {}
        chip->Release();
    }
    if (r.writesThrough && !r.writesThroughTo.empty())
        SelDiagSetText(L"DevToolsSelCaution" + i, L"Editing also writes to " + r.writesThroughTo + L".");
}

static void CALLBACK SelDiagTimerProc(HWND, UINT, UINT_PTR, DWORD)
{
// the UI-thread half of a binding diagnosis. The ROUND TRIP is not here and cannot be -- the relay refuses a
// UI-thread call by construction, so the agent is asked from a worker (SelDiagWorker) and its answer is applied
// here.
    WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::OverlayBindDiag);
// Copy under the lock and touch XAML outside it, so a slow layout pass cannot block the worker mid-fetch.
    // Copy answered rows under the lock, then touch XAML outside it so layout cannot block the worker.
    std::vector<DevToolsSelDiagRow> ready;
    bool done = true;
    {
        AcquireSRWLockExclusive(&g_selDiagLock);
        if (g_selDiagGen == g_selGen) {
            for (auto& r : g_selDiagRows) {
                if (!r.applied && !r.json.empty()) { r.applied = true; ready.push_back(r); }
                if (!r.applied) done = false;
            }
        }
        ReleaseSRWLockExclusive(&g_selDiagLock);
    }
    for (const auto& r : ready) SelDiagApply(r);

    if ((done && InterlockedCompareExchange(&g_selDiagBusy, 0, 0) == 0) || g_selDiagGen != g_selGen) {
        if (g_selDiagTimer) { KillTimer(nullptr, g_selDiagTimer); g_selDiagTimer = 0; }
    }
}

static DWORD WINAPI SelDiagWorker(LPVOID)
{
// OFF the UI thread by construction: this is the thread that blocks on the agent while the agent takes the UI
// thread to read live XAML. Inverting the two is the deadlock, so it is a thread and not a continuation on the
// dispatcher.
    // This worker blocks on the agent while the agent marshals to the UI thread; dispatching it would invert
    // the wait.
    for (;;) {
        size_t idx = SIZE_MAX;
        std::wstring prop;
        unsigned long long wire = 0, gen = 0;
        {
            AcquireSRWLockShared(&g_selDiagLock);
            gen = g_selDiagGen; wire = g_selDiagHandle;
            for (size_t k = 0; k < g_selDiagRows.size(); k++) {
                if (g_selDiagRows[k].json.empty()) { idx = k; prop = g_selDiagRows[k].prop; break; }
            }
            ReleaseSRWLockShared(&g_selDiagLock);
        }
        if (idx == SIZE_MAX || !g_bindAsk || !wire) break;
        if (gen != g_selGen) break;

// Capture FIRST, and only then diagnose. Capture has to happen at PICK time: after an override the local value
// is the literal and there is no BindingExpression left to take a ParentBinding from.
        // Capture binding state before any override because a local-value write removes the BindingExpression.
        std::wstring capJson, diagJson;
        const double tAsk0 = QpcNow();
        g_bindAsk(wire, L"capture", prop.c_str(), &capJson);
        const double tCap = QpcNow();
        g_bindAsk(wire, L"diagnose", prop.c_str(), &diagJson);

// It marshals onto the app's UI thread internally, so it belongs here rather than on the drain timer: calling
// it from the UI thread would be waiting for the thread it needs.
        // Native path walk bypasses the managed relay so no-agent processes still get local binding evidence.
        const double tDiag = QpcNow();
        std::wstring walkJson;
        if (g_walkAsk) g_walkAsk(wire, prop.c_str(), &walkJson);
        const double tWalk = QpcNow();

        DevToolsOverlayLog(L"overlay.bindAsk prop=%s captureMs=%.1f diagnoseMs=%.1f walkMs=%.1f totalMs=%.1f (worker thread; the app UI thread pays inside the agent)",
                      prop.c_str(), (tCap - tAsk0) / 1000.0, (tDiag - tCap) / 1000.0,
                      (tWalk - tDiag) / 1000.0, (tWalk - tAsk0) / 1000.0);

        DevToolsJson cap;
        bool restorable = false, through = false;
        std::wstring throughTo;
        if (DevToolsJsonParse(capJson, cap) && cap.IsObject()) {
            const std::wstring kind = cap.GetString(L"restoreKind");
            restorable = (kind == L"binding" || kind == L"rebind");
            through    = cap.GetString(L"writesThrough") == L"True";
            throughTo  = cap.GetString(L"writesThroughTo");
        }
        std::wstring editWarning = cap.GetString(L"editWarning");
        if (editWarning.empty() && cap.GetString(L"writesThrough") != L"False")
            editWarning = L"Editing may change the source model; restore is not model undo.";

        AcquireSRWLockExclusive(&g_selDiagLock);
        if (gen == g_selDiagGen && idx < g_selDiagRows.size()) {
            g_selDiagRows[idx].json = diagJson.empty() ? DevToolsBindingRelay_UnavailableJson(L"the app did not answer") : diagJson;
            g_selDiagRows[idx].walkJson = walkJson;
            g_selDiagRows[idx].restorable = restorable;
            g_selDiagRows[idx].restoreWarning = cap.GetString(L"warning");
            g_selDiagRows[idx].editWarning = editWarning;
            g_selDiagRows[idx].restoreDisclosure = cap.GetString(L"restoreDisclosure");
            g_selDiagRows[idx].writesThrough = through;
            g_selDiagRows[idx].writesThroughTo = throughTo;
        }
        ReleaseSRWLockExclusive(&g_selDiagLock);
    }
    InterlockedExchange(&g_selDiagBusy, 0);
    return 0;
}

// UI thread.
static void SelDiagBegin(IInspectable* root, const std::vector<DevToolsCardRow>& rows, unsigned long long wire)
{
    if (!g_bindAsk || !wire) return;
    g_selDiagReadOnly = !DevToolsTrust_MutationEnabled();

    std::vector<DevToolsSelDiagRow> want;
    for (size_t i = 0; i < rows.size(); i++) {
        if (!SelRowIsBinding(rows[i])) continue;
        DevToolsSelDiagRow r; r.index = i; r.prop = rows[i].name;
        want.push_back(std::move(r));
    }
    if (want.empty()) return;

    if (g_selDiagRoot) { g_selDiagRoot->Release(); g_selDiagRoot = nullptr; }
    g_selDiagRoot = root; g_selDiagRoot->AddRef();

    AcquireSRWLockExclusive(&g_selDiagLock);
    g_selDiagRows = std::move(want);
    g_selDiagGen = g_selGen;
    g_selDiagHandle = wire;
    ReleaseSRWLockExclusive(&g_selDiagLock);

    InterlockedExchange(&g_selDiagBusy, 1);
    if (HANDLE th = CreateThread(nullptr, 0, DevToolsThreadEntry<SelDiagWorker>, nullptr, 0, nullptr)) {
        CloseHandle(th);
    } else {
// Say so on every row rather than leaving them on the pending placeholder forever, which would settle into
// looking like "nothing wrong here".
        InterlockedExchange(&g_selDiagBusy, 0);
        AcquireSRWLockExclusive(&g_selDiagLock);
        for (auto& r : g_selDiagRows) r.json = DevToolsBindingRelay_UnavailableJson(L"the diagnosis could not be started");
        ReleaseSRWLockExclusive(&g_selDiagLock);
    }
    if (!g_selDiagTimer) g_selDiagTimer = SetTimer(nullptr, 0, 100, &SelDiagTimerProc);
}

// UI thread; called from ClearSelectionAnchor.
static void SelDiagClear(){
    if (g_selDiagTimer) { KillTimer(nullptr, g_selDiagTimer); g_selDiagTimer = 0; }
    AcquireSRWLockExclusive(&g_selDiagLock);
    g_selDiagRows.clear();
    g_selDiagHandle = 0;
    ReleaseSRWLockExclusive(&g_selDiagLock);
    if (g_selDiagRoot) { g_selDiagRoot->Release(); g_selDiagRoot = nullptr; }
}

// Tear down the morph anchor (idempotent): remove the subtree from the canvas, release the held parts, and
// release the element-count oracle freeze (the panel's async TextBox parts are gone once the subtree leaves, so
// the live count returns to the frozen baseline).
static void ClearSelectionAnchor()
{
    ResetPickPointer();
    GuestCommentRememberDraft(false);
    // Whatever replaces or removes the peek, focus inside it goes back to the app without keyboard visuals.
    SelReturnFocus();
    if (g_selDismissTimer) {
        KillTimer(nullptr, g_selDismissTimer);
        g_selDismissTimer = 0;
    }
    g_selDismissGen = 0;
    g_selDismissCommitFailed = false;
    g_selDismissVisualClosed = false;
    ++g_selGen;
    g_selRowSinks.clear();
// The bound-row confirm state belongs to THIS anchor: drop the stashed writes and the expression-line refs so a
// later anchor can never raise a prompt for, or commit, the previous element's value.
    g_selPending.clear();
    g_selOperationError.clear();
    g_selOperationProperty.clear();
    g_selPreviewProperties.clear();
    DevToolsSelReleaseExprRefs();
    SelDiagClear();

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
    IInspectable* selTarget  = g_selTarget;  g_selTarget  = nullptr;
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
    for (IInspectable* b : g_selRowBoxes) if (b) b->Release();
    g_selRowBoxes.clear();
    if (selTarget)  selTarget->Release();
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

// Build the PANEL half of the selection chrome (selection-panel.xaml) — emitted only for an in-app pick. It is
// substituted into selection.xaml's $PANEL$ slot, so it is built and fully substituted FIRST and the finished
// string is handed to BuildSelectionMarkup last (see the note there).
static std::wstring LikelySourceConfirmationMarkup(bool likely)
{
    if (!likely) return L"";
    return L"<CheckBox x:Name=\"DevToolsConfirmLikelySource\" IsChecked=\"False\" HorizontalAlignment=\"Stretch\""
        L" AutomationProperties.AutomationId=\"DevToolsConfirmLikelySource\""
        L" AutomationProperties.Name=\"Use this likely source for my comment\">"
        L"<TextBlock Text=\"Use this likely source for my comment\" TextWrapping=\"Wrap\""
        L" Style=\"{StaticResource CaptionTextBlockStyle}\"/></CheckBox>";
}

static std::wstring BuildSelectionPanelMarkup(const std::wstring& title, const std::wstring& titleTip,
                                              int panelX, int panelY, int panelHeight, int leaderX, int leaderY,
                                              const std::wstring& rowsMarkup, const std::wstring& commentText,
                                              const std::wstring& srcFile, unsigned int srcLine,
                                              const std::wstring& authoredState,
                                              const std::wstring& authoredXaml,
                                              double originX, double originY,
                                              bool readOnly)
{
    const DevToolsPalette& pal = DevToolsInverseAcrylicPalette();

    std::wstring srcText, srcTip, srcFg;
    if (!srcFile.empty()) {
        size_t sl = srcFile.find_last_of(L"/\\");
        std::wstring leaf = (sl == std::wstring::npos) ? srcFile : srcFile.substr(sl + 1);
        srcText = leaf; if (srcLine > 0) srcText += L":" + std::to_wstring(srcLine);
        srcTip = srcFile; if (srcLine > 0) srcTip += L" (line " + std::to_wstring(srcLine) + L")";
        srcFg = pal.textPrimary;
    } else {
        srcText = L"Source unavailable";
        srcTip = L"No authored XAML location was reported for this element. Its selection and comment target are unchanged.";
        srcFg = pal.textSecondary;
    }
    // The Source row shares the property rows' label column; its disclosure reveals the authored declaration.
    std::wstring xamlMarkup =
        SelFlatDisclosureOpen(L"DevToolsSelXamlDisclosure", L"XAML source: " + DevToolsXmlEscape(srcText),
            L"<Grid Width=\"" + std::to_wstring(kSelPanelW - 32 - 32) + L"\" ColumnSpacing=\"12\">" +
            std::wstring(kSelLabelColumn) +
            L"<TextBlock Text=\"Source\" Style=\"{StaticResource CaptionTextBlockStyle}\" VerticalAlignment=\"Center\""
            L" Foreground=\"" + pal.textSecondary + L"\"/>"
            L"<TextBlock x:Name=\"DevToolsSelSource\" Grid.Column=\"1\" Text=\"" + DevToolsXmlEscape(srcText) + L"\""
            L" TextTrimming=\"CharacterEllipsis\" VerticalAlignment=\"Center\" Foreground=\"" + srcFg + L"\""
            L" AutomationProperties.AutomationId=\"DevToolsSelSource\"/></Grid>") +
        L"<StackPanel Spacing=\"8\">"
        L"<TextBlock Text=\"" + DevToolsXmlEscape(srcTip) + L"\" TextWrapping=\"Wrap\" IsTextSelectionEnabled=\"True\""
        L" Style=\"{StaticResource CaptionTextBlockStyle}\" Foreground=\"" + pal.textSecondary + L"\"/>";
    if (!authoredXaml.empty()) {
        xamlMarkup += L"<TextBlock Text=\"" + std::wstring(authoredState == L"likely"
                     ? L"Likely source: unique line and identity match; compiled identity and line preservation are unverified."
                     : L"Source on disk; the running app may differ after a rebuild or hot reload.") + L"\""
                     L" Style=\"{StaticResource CaptionTextBlockStyle}\" TextWrapping=\"Wrap\" Foreground=\"" +
                     pal.textSecondary + L"\"/>";
        xamlMarkup += L"<TextBlock Text=\"" + XmlEscapeLines(authoredXaml) +
                     L"\" FontFamily=\"Consolas\" Style=\"{StaticResource CaptionTextBlockStyle}\" TextWrapping=\"Wrap\""
                     L" IsTextSelectionEnabled=\"True\" Foreground=\"" + pal.textPrimary +
                     L"\" AutomationProperties.AutomationId=\"DevToolsSelAuthoredXaml\"/>";
    } else if (authoredState == L"stale" || authoredState == L"noFile" || authoredState == L"unavailable" || authoredState == L"unverifiedBuild") {
        const std::wstring message = authoredState == L"stale"
            ? L"Authored XAML is stale. Rebuild the app to refresh it."
            : authoredState == L"unverifiedBuild"
            ? L"The original source and deployed XAML could not be verified together. Check the original XAML before editing."
            : authoredState == L"unavailable"
            ? L"The source coordinates and element identity do not identify one XAML declaration."
            : L"The authored XAML source file could not be opened.";
        xamlMarkup += L"<TextBlock Text=\"" + DevToolsXmlEscape(message) +
                     L"\" Style=\"{StaticResource CaptionTextBlockStyle}\" TextWrapping=\"Wrap\" Foreground=\"" +
                     pal.textSecondary + L"\"/>";
    }
    xamlMarkup += L"</StackPanel></Expander>";

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
        { L"XAML",    xamlMarkup },
        { L"ROWS",    rowsMarkup },
        { L"COMMENT", DevToolsXmlEscape(commentText, true) },
        { L"SOURCECONFIRM", LikelySourceConfirmationMarkup(authoredState == L"likely") },
        { L"CMTREADONLY", readOnly || !GuestCommentCanEdit(g_selHandle) ? std::wstring(L"True") : std::wstring(L"False") },
        { L"CMTSAVEVIS", readOnly || !GuestCommentCanEdit(g_selHandle) ? std::wstring(L"Collapsed") : std::wstring(L"Visible") },
        { L"CMTLIMIT", g_guestCommentAuthority.load() ? std::wstring(L"65536") : std::wstring(L"0") },
        { L"CMTPLACEHOLDER", readOnly ? std::wstring(L"Mutation access is required to edit comments")
                                      : guestAuthority && !guestAuthority->Bound() ? std::wstring(L"Host comments are unavailable for this launch")
                                      : !GuestCommentCanEdit(g_selHandle) ? std::wstring(L"Finish the earlier host save, then reselect this element")
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
    rows = DevToolsSelCurateRows(rows, shortType);
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
    // Header, comment, Source row and footer, then 32 px rows on an 8 px rhythm; a bound row adds its expression
    // line and disclosure, and a multi-line value its taller editor. The panel still sizes to its content.
    // Room to open one disclosure before the panel starts scrolling.
    constexpr int kSelDisclosureRoom = 160;
    int estimatedHeight = 304;
    for (const auto& row : rows) {
        estimatedHeight += 40;
        if (SelRowHasExpression(row)) estimatedHeight += 32;
        if (DevToolsEditText::IsMultiline(row.value)) estimatedHeight += 72;
    }
    DevToolsSelection::Rect viewport{ 0, 0, w, h };
    DevToolsSelection::Rect workArea{};
    if (GetMonitorWorkAreaDips(&workArea)) viewport = workArea;
    const DevToolsSelection::Rect element{ ex, ey, ex + ew, ey + eh };
    RECT toolbarDip{};
    const bool haveToolbar = g_toolbarSurface.xamlRootKey == g_activeSurface.xamlRootKey && ToolbarRectDip(&toolbarDip);
    const DevToolsSelection::Rect toolbar{ toolbarDip.left, toolbarDip.top, toolbarDip.right, toolbarDip.bottom };
    const DevToolsSelection::Placement placement =
        DevToolsSelection::PlacePanel(element, viewport, pw, estimatedHeight + kSelDisclosureRoom, 14, 8, haveToolbar ? &toolbar : nullptr);
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
        panelMarkup = BuildSelectionPanelMarkup(title, titleTip, px, py, panelHeight, leaderX, leaderY,
                                                BuildSelRowsMarkup(rows, readOnly), initComment,
                                                srcFile, srcLine, authoredState, authoredXaml, originX, originY,
                                                readOnly);

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

// Take ownership of the write target and bump the generation BEFORE wiring the row sinks (they capture the gen
// and write to g_selTarget); a later teardown bumps the gen again, making any pending commit inert.
    // Bump generation before row sinks capture it so teardown makes pending commits inert.
    g_selTarget = el; g_selTarget->AddRef();
    ++g_selGen;
    for (const auto& row : rows) g_selPreviewProperties.push_back(row.name);
    if (g_selPanel && !readOnly) DevToolsSelWireRows(insp, rows);
// Deliberately not gated on the mutation session: a read-only session still gets to know why a binding is
// broken, it just does not get the buttons that change it.
    // Read-only sessions still run diagnosis; mutation trust gates only write affordances.
    if (g_selPanel) SelDiagBegin(insp, rows, g_selHandle);
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
            g_selOpenClickSink.Bind(&IID_RoutedEventHandler_Del, &OnInspectClick);
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
    DevToolsOverlayLog(L"overlay.sel shown title=%s fromUserPick=%d tag=%d,%d panel=%s(%d,%d side=%s) rows=%zu readOnly=%d"
                  L" element=%d,%d,%d,%d viewport=%d,%d,%d,%d",
                  title.c_str(), (int)fromUserPick, tagX, tagY, g_selPanel ? L"yes" : L"no", px, py,
                  onRight ? L"right" : L"left", rows.size(), (int)readOnly,
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
