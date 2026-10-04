// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsOverlay.cpp; included into that translation unit, not compiled on its own.

// ============================ In-app comment composer ============================ A small floating card
// (authored as runtime XAML in the overlay Canvas, ships no WinUI) opened from the badge's 💬 glyph. On Save it
// does NOT write any store or pipe verb itself: it shells out to `winapp devtools comments add --from-selecti.
static IInspectable* g_composerBorder = nullptr;
static IInspectable* g_composerUi = nullptr;
static IInspectable* g_composerTarget = nullptr;
static IInspectable* g_composerBox = nullptr;
static EventSink g_composerSaveSink, g_composerCancelSink, g_composerCloseSink;

// The composer also carries: the element's raw handle (pin-registry key + edit lookup), the comment id
// (existing => EDIT in place via `add --id`, else a fresh id => create), a prefill text (loaded when editing an
// existing in-session comment), the mode flag (teardown branches on it), and the element bounds captured at
// ope.
static bool g_commentMode = false;
static InstanceHandle g_composerHandle = 0;
static unsigned long long g_composerCommentWire = 0;
static std::wstring g_composerId;
static std::wstring g_composerInitText;
static unsigned g_composerGeneration = 0;
static std::wstring g_composerRevision, g_selCommentRevision;
static bool g_composerFromCommentMode = false;
static RECT g_composerBounds{};
static std::wstring g_composerSrcFile;
static unsigned int g_composerSrcLine = 0;

static void SetCommentTarget(InstanceHandle raw, bool composer)
{
// Capture once at editor open; a reused raw handle must not become a fresh target at save time.
    // Capture target once at editor open; a recycled raw handle must not become the save target.
    const auto wire = raw && g_wireOf ? g_wireOf(raw) : 0;
    if (composer) { g_composerHandle = raw; g_composerCommentWire = wire; }
    else { g_selHandle = raw; g_selCommentWire = wire; }
}

// The registry is what the CLI last pushed (DevToolsOverlay_SetComments, from `.winapp/ui-comments.json` — the
// CLI is that file's single writer; the overlay parses no JSON and reads no file) PLUS anything composed this
// session, which the CLI persists on the way through. Entries persist across comment-mode toggles (id/tex.
struct PinClickSink;
struct PinnedComment { InstanceHandle handle; std::wstring id; std::wstring text; RECT bounds; IInspectable* ui; PinClickSink* sink;
                       std::wstring name;
// Only seeded pins are removed when a later push drops them, so a push that races an in-flight `comments add`
// cannot delete a just-earned marker.
                       // Only store-seeded pins are pruned by later CLI pushes; session-authored pins survive
                       // races.
                       bool seeded;
                       std::wstring revision; };
static std::vector<PinnedComment> g_pins;
static bool g_commentsShown = false;

static void UpsertPin(InstanceHandle key, const std::wstring& id, const std::wstring& text, const RECT& bounds);
static void RemovePinById(const std::wstring& id);
static void RemovePickCatcher();
static std::wstring GenCommentId();
static void SetCommentsShown(bool on);
static bool TryElementRootBounds(IXamlDiagnostics* diag, InstanceHandle root, InstanceHandle h,
                                 RECT* out, bool* noVisibleBox);
static void SelDeleteComment(const std::wstring* requestedText = nullptr);
enum class GuestCommentEditor { Selection, Composer, Delete, WindowResolve };
struct GuestCommentWrite {
    HANDLE process = nullptr;
    const GuestCommentAuthority* authority = nullptr;
    GuestCommentEditor editor = GuestCommentEditor::Selection;
    InstanceHandle handle = 0, root = 0;
    unsigned long long wire = 0;
    unsigned generation = 0;
    std::wstring id, submitted, draft, revision, operation, status, anchor, deleteText;
    RECT bounds{};
    ULONGLONG began = 0;
    bool failed = false, persisted = false, superseded = false;
    bool deleteRequested = false, discarded = false, linked = true, closeOnSave = false;
    std::function<void(int)> completed;
};
static GuestCommentWrite g_guestCommentWrite;
// Local saves started while another is still being written wait here, in order, so commenting on several
// elements in a row never refuses a save. Host-backed comments keep their one-at-a-time contract.
struct QueuedCommentWrite { std::wstring args, what; GuestCommentWrite write; };
static std::deque<QueuedCommentWrite> g_guestCommentQueue;
static bool g_commentSnapshotAuthoritative = false;
static UINT_PTR g_guestCommentTimer = 0;
static void GuestCommentComplete(DWORD exitCode, bool acknowledged, const wchar_t* stage = nullptr);
static void GuestCommentRenderStatus();
static void CALLBACK GuestCommentTimerProc(HWND, UINT, UINT_PTR, DWORD);

static void DiscardSelectionCommentDraft()
{
    auto& write = g_guestCommentWrite;
    if (write.authority != g_guestCommentAuthority.load() ||
        (write.editor != GuestCommentEditor::Selection && write.editor != GuestCommentEditor::Delete) ||
        write.handle != g_selHandle || write.wire != g_selCommentWire || write.id != g_selCommentId) return;
    if (write.process) {
        write.discarded = true;
        write.deleteRequested = false;
        write.draft.clear();
    } else {
        write = {};
    }
}

static void SetSelCommentText(const std::wstring& text)
{
    if (!g_selComment) return;
    void* tb = nullptr;
    if (SUCCEEDED(g_selComment->QueryInterface(DevToolsIid<DevToolsXC::ITextBox>(), &tb)) && tb) {
        HSTRING h = nullptr;
        if (SUCCEEDED(WindowsCreateString(text.c_str(), (UINT32)text.size(), &h))) {
            const HRESULT hr = DevToolsPutTextBoxText(tb, h);
            if (FAILED(hr)) DevToolsOverlayLog(L"overlay.sel comment box write FAILED hr=0x%08X", hr);
            WindowsDeleteString(h);
        }
        reinterpret_cast<IUnknown*>(tb)->Release();
    }
}

// It exists exactly while this anchor has a saved comment.
static void SetSelCommentDeleteVisible(bool visible)
{
    if (!g_selCommentDelete) return;
    void* ui = nullptr;
    if (SUCCEEDED(g_selCommentDelete->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) && ui) {
        DevToolsPutVisibility(ui, visible ? 0 : 1);
        reinterpret_cast<IUnknown*>(ui)->Release();
    }
}

static bool CommentModeOn() { return g_commentMode; }

// Remove the composer from the Canvas (if shown) and release every ref it holds. Does NOT touch the oracle
// freeze - callers pair g_cardFreeze(1)/(0) around the composer's lifetime, mirroring the card's ClearCard
// discipline.
// Composer teardown releases refs, but freeze lifetime is paired by callers around the composer.
static void ClearComposer()
{
    ResetPickPointer();
    GuestCommentRememberDraft(true);
    ++g_composerGeneration;
// Click is NOT re-synthesized by RemoveAt (unlike the PointerExited/LostFocus that XAML raises against an
// element being removed under the pointer/with focus), and no composer sink calls into the removed subtree, so
// there is no re-entrant dereference window. If a pointer-enter/exit or focus sink is ever added here, adopt.
    // Composer RemoveAt is safe because Click is not re-synthesized like PointerExited or LostFocus.
    if (g_composerUi && g_canvasChildren) {
        unsigned int idx = 0; unsigned char found = 0;
        if (SUCCEEDED(DevToolsVecIndexOf(g_canvasChildren, g_composerUi, &idx, &found)) && found) DevToolsVecRemoveAt(g_canvasChildren, idx);
    }
    if (g_composerUi) { g_composerUi->Release(); g_composerUi = nullptr; }
    if (g_composerBorder) { g_composerBorder->Release(); g_composerBorder = nullptr; }
    if (g_composerBox) { g_composerBox->Release(); g_composerBox = nullptr; }
    if (g_composerTarget) { g_composerTarget->Release(); g_composerTarget = nullptr; }
}

static std::wstring QuoteArg(const std::wstring& in)
{
    return DevToolsCommentText::QuoteArgument(in);
}

// The writer runs off the UI thread.
// The writer runs off the UI thread; only completion may update saved editor state.
static bool LaunchWinapp(const std::wstring& args, const wchar_t* what, GuestCommentWrite* guest = nullptr)
{
    if (!DevToolsTrust_MutationEnabled()) return false;
    const auto authority = g_guestCommentAuthority.load();
    if (guest && g_guestCommentWrite.process && !authority && guest->editor == GuestCommentEditor::Selection &&
        guest->id != g_guestCommentWrite.id) {
        for (auto it = g_guestCommentQueue.begin(); it != g_guestCommentQueue.end(); ++it)
            if (it->write.id == guest->id) {
                guest->closeOnSave = guest->closeOnSave || it->write.closeOnSave;
                g_guestCommentQueue.erase(it);
                break;
            }
        guest->status = L"Saving...";
        g_guestCommentQueue.push_back({ args, what, std::move(*guest) });
        DevToolsOverlayLog(L"overlay.comment queued %s behind the running save (%zu waiting)", what, g_guestCommentQueue.size());
        return true;
    }
    if (!guest || g_guestCommentWrite.process) return false;
    if (g_guestCommentWrite.failed && g_guestCommentWrite.editor != GuestCommentEditor::WindowResolve &&
        g_guestCommentWrite.id != guest->id) {
        g_guestCommentWrite.status = L"An earlier comment save is unconfirmed. Reopen its element to recover the retained draft.";
        GuestCommentRenderStatus();
        return false;
    }
    guest->authority = authority;
    if ((guest->editor == GuestCommentEditor::Selection || guest->editor == GuestCommentEditor::Composer) &&
        (!guest->wire || !g_wireOf || g_wireOf(guest->handle) != guest->wire)) {
        g_guestCommentWrite = std::move(*guest);
        GuestCommentComplete(0x57410102, false);
        return false;
    }
    wchar_t* exe = g_cliExe.load();
    if (!exe || !*exe) {
        g_guestCommentWrite = std::move(*guest);
        GuestCommentComplete(1, false, L"writer-unavailable");
        DevToolsOverlayLog(L"overlay.comment %s: no CLI path configured", what); return false;
    }

    std::wstring guestToken;
    if (authority) {
        if (!authority->Bound()) {
            guest->authority = authority;
            g_guestCommentWrite = std::move(*guest);
            GuestCommentComplete(1, false, L"binding-unavailable");
            return false;
        }
        if (g_guestCommentWrite.failed && g_guestCommentWrite.id == guest->id &&
            g_guestCommentWrite.authority == authority && g_guestCommentWrite.root == guest->root &&
            g_guestCommentWrite.handle == guest->handle && g_guestCommentWrite.wire == guest->wire &&
            g_guestCommentWrite.submitted == guest->submitted && g_guestCommentWrite.revision == guest->revision) {
            guest->operation = g_guestCommentWrite.operation;
        } else {
            GUID operation{};
            if (FAILED(CoCreateGuid(&operation))) return false;
            wchar_t value[40]{};
            if (!StringFromGUID2(operation, value, _countof(value))) return false;
            for (const auto c : std::wstring(value)) if (c != L'{' && c != L'}' && c != L'-') guest->operation += c;
        }
        guest->authority = authority;
        guestToken = DevToolsOverlay_GuestCommentToken(guest->operation, guest->revision);
    }
    wchar_t root[1024];
    DWORD rn = GetEnvironmentVariableW(L"WINAPP_DEVTOOLS_SOURCE_ROOT", root, _countof(root));
    const auto cmd = DevToolsCommentText::WriterCommand(exe, args,
        (rn > 0 && rn < _countof(root)) ? root : L"", guestToken);

    std::vector<wchar_t> mutableCmd(cmd.begin(), cmd.end()); mutableCmd.push_back(L'\0');
    STARTUPINFOW si{}; si.cb = sizeof(si);
    PROCESS_INFORMATION pi{};
    if (CreateProcessW(exe, mutableCmd.data(), nullptr, nullptr, FALSE,
                       CREATE_NO_WINDOW | DETACHED_PROCESS, nullptr, nullptr, &si, &pi)) {
        if (pi.hThread) CloseHandle(pi.hThread);
        guest->process = pi.hProcess;
        guest->began = GetTickCount64();
        guest->status = authority ? L"Saving to host..." : L"Saving...";
        g_guestCommentWrite = std::move(*guest);
        g_guestCommentTimer = SetTimer(nullptr, 0, 100, &GuestCommentTimerProc);
        if (!g_guestCommentTimer) {
            const auto error = GetLastError();
            TerminateProcess(pi.hProcess, 1);
            CloseHandle(pi.hProcess);
            g_guestCommentWrite.process = nullptr;
            GuestCommentComplete(error, false, L"completion-timer");
        }
        GuestCommentRenderStatus();
        DevToolsOverlayLog(L"overlay.comment launched %s pid=%lu", what, pi.dwProcessId);
        return g_guestCommentWrite.process != nullptr;
    }
    const auto launchError = GetLastError();
    DevToolsOverlayLog(L"overlay.comment %s CreateProcess failed err=%lu", what, launchError);
    g_guestCommentWrite = std::move(*guest);
    GuestCommentComplete(launchError, false, L"writer-launch");
    return false;
}

// Capture the editor's exact element lifetime before starting the asynchronous writer.
static std::wstring g_commentSourceError;
static void GuestCommentSetStatus(IInspectable* root, const wchar_t* name, const std::wstring& text);

static bool LikelySourceConfirmed(IInspectable* root)
{
    if (!root) return false;
    winrt::com_ptr<IInspectable> checkbox;
    checkbox.attach(DevToolsSelFindName(root, L"DevToolsConfirmLikelySource"));
    winrt::com_ptr<IUnknown> toggle, boxed;
    bool checked = false;
    const bool confirmed = checkbox && SUCCEEDED(checkbox->QueryInterface(DevToolsIid<DevToolsXCP::IToggleButton>(), toggle.put_void())) &&
        SUCCEEDED(DevToolsAbi<DevToolsXCP::IToggleButton>(toggle.get())->get_IsChecked(boxed.put_void())) && boxed &&
        SUCCEEDED(DevToolsAbi<winrt::Windows::Foundation::IReference<bool>>(boxed.get())->get_Value(&checked)) && checked;
    if (!confirmed) return false;
    const auto unchecked = winrt::box_value(false);
    return SUCCEEDED(DevToolsAbi<DevToolsXCP::IToggleButton>(toggle.get())->put_IsChecked(winrt::get_abi(unchecked)));
}

static bool HasAuthoredSource(InstanceHandle handle)
{
    std::wstring file; unsigned int line = 0, column = 0;
    return g_srcRead && g_srcRead(handle, &file, &line, &column) && !file.empty();
}

// A comment is linked when it lands on a declaration in the project. Framework and template parts carry a source
// file too, but their comment can only offer unrelated candidates found by type.
static bool IsLinkedElement(InstanceHandle handle)
{
    return g_isDeclared ? g_isDeclared(handle) : HasAuthoredSource(handle);
}

static bool LaunchCommentAdd(const std::wstring& text, const std::wstring& id, bool composer = false)
{
    const auto handle = composer ? g_composerHandle : g_selHandle;
    const auto wire = composer ? g_composerCommentWire : g_selCommentWire;
    g_commentSourceError.clear();
    std::wstring type, name, state, xaml;
    std::vector<DevToolsCardRow> rows;
    winrt::com_ptr<IInspectable> element;
    const bool hasSource = HasAuthoredSource(handle);
    if (hasSource && (!g_pickDiag || !g_cardRead || FAILED(g_pickDiag->GetIInspectableFromHandle(handle, element.put())) ||
        !element || !g_cardRead(element.get(), &type, &name, &rows, &state, &xaml))) {
        g_commentSourceError = L"Couldn't verify this comment's source. Reselect the element before saving.";
        if (composer) GuestCommentSetStatus(g_composerBorder, L"GuestCommentStatus", g_commentSourceError);
        return false;
    }
    const bool likelyConfirmed = LikelySourceConfirmed(composer ? g_composerBorder : g_selUi);
    if (state == L"likely" && !likelyConfirmed) {
        g_commentSourceError = L"Review the likely XAML source, then check 'Use this likely source for my comment'. Nothing was saved.";
        if (composer) GuestCommentSetStatus(g_composerBorder, L"GuestCommentStatus", g_commentSourceError);
        return false;
    }
    std::wstring outgoing = text;
    if (g_guestCommentWrite.failed && g_guestCommentWrite.id == id &&
        g_guestCommentWrite.wire == wire &&
        DevToolsCommentText::Unchanged(text, g_guestCommentWrite.submitted)) outgoing = g_guestCommentWrite.submitted;
    auto args = DevToolsCommentText::AddArguments(GetCurrentProcessId(), outgoing, id, std::to_wstring(wire));
    if (likelyConfirmed) args += L" --confirm-likely-source";
    GuestCommentWrite guest;
    guest.editor = composer ? GuestCommentEditor::Composer : GuestCommentEditor::Selection;
    guest.handle = handle; guest.root = g_pickRoot; guest.wire = wire;
    guest.generation = composer ? g_composerGeneration : g_selGen;
    guest.id = id; guest.submitted = outgoing; guest.draft = text;
    guest.linked = IsLinkedElement(handle);
    guest.revision = composer ? g_composerRevision : g_selCommentRevision;
    if (g_anchorOf && handle) g_anchorOf(handle, &guest.anchor);
    guest.bounds = composer ? g_composerBounds : g_selBounds;
    return LaunchWinapp(args, L"add", &guest);
}

// Remove the comment behind this anchor, through the same single writer. Writes are serialized so a preceding
// blur save cannot land after this delete.
// Delete is not resolve: delete removes the store row, while resolve records completed work.
static bool LaunchCommentDelete(const std::wstring& id, const std::wstring& requestedText)
{
    if (id.empty()) return false;
    GuestCommentWrite guest;
    guest.editor = GuestCommentEditor::Delete;
    guest.handle = g_selHandle; guest.root = g_pickRoot; guest.generation = g_selGen;
    guest.wire = g_selCommentWire;
    guest.id = id; guest.draft = g_selCommentSaved; guest.revision = g_selCommentRevision;
    guest.submitted = requestedText;
    if (g_anchorOf && guest.handle) g_anchorOf(guest.handle, &guest.anchor);
    return LaunchWinapp(L"devtools comments delete " + QuoteArg(id) +
                        L" --app " + std::to_wstring(GetCurrentProcessId()), L"delete", &guest);
}

bool DevToolsOverlay_ResolveGuestComment(const std::wstring& id, const std::wstring& revision,
    std::function<void(int)> completed)
{
    if (!g_guestCommentAuthority.load() || g_guestCommentWrite.process) return false;
    GuestCommentWrite guest;
    guest.editor = GuestCommentEditor::WindowResolve;
    guest.root = g_pickRoot;
    guest.generation = g_selGen;
    guest.id = id; guest.submitted = L"resolved"; guest.revision = revision;
    guest.completed = std::move(completed);
    LaunchWinapp(L"devtools comments update --status resolved " + QuoteArg(id) + L" --app " + std::to_wstring(GetCurrentProcessId()),
        L"resolve", &guest);
    return g_guestCommentWrite.process != nullptr;
}

static void CloseComposerRestoreBadge()
{
    bool fromCommentMode = g_composerFromCommentMode;
    IInspectable* el = g_composerTarget; if (el) el->AddRef();
    ClearComposer();
    if (g_cardFreeze) g_cardFreeze(0);
    if (fromCommentMode) {
        if (g_commentMode) OnPickClick(nullptr, nullptr);
    }
// Badge path removed with the info badge: the composer is only entered from comment mode now, so there is
// nothing to restore on close — the pick already raises the morph anchor directly.
    if (el) el->Release();
}

static void OnComposerCancelClick(void*, void*) {
    if (!g_guestCommentWrite.process && g_guestCommentWrite.failed && g_guestCommentWrite.id == g_composerId) g_guestCommentWrite = {};
    DevToolsOverlayLog(L"overlay.composer cancelled"); CloseComposerRestoreBadge();
}
static void OnComposerCloseClick(void*, void*)  { DevToolsOverlayLog(L"overlay.composer closed");    CloseComposerRestoreBadge(); }

static void OnComposerSaveClick(void*, void*)
{
    if (!g_composerBox || !g_cardReadInput) { DevToolsOverlayLog(L"overlay.composer save: no input bridge"); return; }
    std::wstring text;
    if (!g_cardReadInput(g_composerBox, &text)) { DevToolsOverlayLog(L"overlay.composer save: read failed"); return; }
    if (DevToolsCommentText::IsBlank(text)) { DevToolsOverlayLog(L"overlay.composer save: empty, keeping open"); return; }
    if (DevToolsCommentText::Unchanged(text, g_composerInitText)) {
        CloseComposerRestoreBadge();
        return;
    }
    LaunchCommentAdd(text, g_composerId, true);
}

// Keyed by the raw pick handle, like the composer's edit lookup. A comment this panel just saved counts even before
// (or without) a refreshed marker for it, so picking the element again edits it rather than adding a second one.
static void SelLookupComment(InstanceHandle key, std::wstring* outId, std::wstring* outText)
{
    g_selCommentRevision.clear();
    if (key) {
        for (const auto& p : g_pins) {
            if (p.handle == key) {
                g_selCommentRevision = p.revision;
                if (outId) *outId = p.id;
                if (outText) *outText = p.text;
                return;
            }
        }
        const auto& write = g_guestCommentWrite;
        if (write.persisted && !write.superseded && write.editor == GuestCommentEditor::Selection && write.handle == key &&
            write.root == g_pickRoot && !write.id.empty()) {
            if (outId) *outId = write.id;
            if (outText) *outText = write.submitted;
            return;
        }
    }
    if (outId) *outId = GenCommentId();
}

// NOT through the diagnostics chain: the live read of the box is exact. The diagnostics read stays as a fallback
// for a box that will not QI.
static bool SelReadCommentText(std::wstring* out)
{
    if (!g_selComment || !out) return false;
    void* tb = nullptr;
    if (SUCCEEDED(g_selComment->QueryInterface(DevToolsIid<DevToolsXC::ITextBox>(), &tb)) && tb) {
        HSTRING h = nullptr;
        const HRESULT hr = DevToolsGetTextBoxText(tb, &h);
        reinterpret_cast<IUnknown*>(tb)->Release();
        if (SUCCEEDED(hr)) {
            UINT32 len = 0;
            const wchar_t* buf = h ? WindowsGetStringRawBuffer(h, &len) : nullptr;
            out->assign(buf ? buf : L"", len);
            if (h) WindowsDeleteString(h);
            return true;
        }
    }
    return g_cardReadInput && g_cardReadInput(g_selComment, out);
}

static bool GuestCommentMatchesEditor(bool composer, bool requireGeneration = true)
{
    const auto& write = g_guestCommentWrite;
    std::wstring anchor;
    if (!write.anchor.empty() && (!g_anchorOf || !g_anchorOf(write.handle, &anchor) || anchor != write.anchor)) return false;
    return write.authority == g_guestCommentAuthority.load() && write.root == g_pickRoot &&
        write.handle == (composer ? g_composerHandle : g_selHandle) &&
        ((write.editor != GuestCommentEditor::Selection && write.editor != GuestCommentEditor::Composer &&
            write.editor != GuestCommentEditor::Delete) ||
            (write.wire == (composer ? g_composerCommentWire : g_selCommentWire) &&
                (!write.wire || (g_wireOf && g_wireOf(write.handle) == write.wire)))) &&
        write.id == (composer ? g_composerId : g_selCommentId) &&
        (!requireGeneration || write.generation == (composer ? g_composerGeneration : g_selGen));
}

static bool GuestCommentCanEdit(InstanceHandle handle)
{
    const auto& write = g_guestCommentWrite;
    const auto authority = g_guestCommentAuthority.load();
    if (authority && !authority->Bound()) return false;
    // A local save still running for another element does not lock this one: its save waits in the queue.
    if (!authority && write.process && !write.failed) return true;
    return (!write.process && (!write.failed || write.editor == GuestCommentEditor::WindowResolve)) ||
        (write.root == g_pickRoot && write.handle == handle);
}

static bool DiscardFailedSelectionCommentDraft()
{
    if (g_guestCommentAuthority.load() || g_guestCommentWrite.process ||
        !g_guestCommentWrite.failed || (g_guestCommentWrite.persisted && !g_guestCommentWrite.superseded) ||
        !GuestCommentMatchesEditor(false, false)) return false;
    DiscardSelectionCommentDraft();
    return true;
}

static void GuestCommentSetStatus(IInspectable* root, const wchar_t* name, const std::wstring& text)
{
    if (!root) return;
    if (IInspectable* element = DevToolsSelFindName(root, name)) {
        void* block = nullptr;
        if (SUCCEEDED(element->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &block)) && block) {
            HSTRING value = nullptr;
            if (SUCCEEDED(WindowsCreateString(text.c_str(), static_cast<UINT32>(text.size()), &value))) {
                DevToolsPutText(block, value);
                WindowsDeleteString(value);
                SetOverlayElementVisible(element, !text.empty());
            }
            reinterpret_cast<IUnknown*>(block)->Release();
        }
        element->Release();
    }
}

static void GuestCommentRenderStatus()
{
    const bool composer = g_guestCommentWrite.editor == GuestCommentEditor::Composer;
    if (!GuestCommentMatchesEditor(composer, false)) return;
    GuestCommentSetStatus(composer ? g_composerBorder : g_selUi,
        composer ? L"GuestCommentStatus" : L"DevToolsGuestCommentStatus", g_guestCommentWrite.status);
}

static void GuestCommentRememberDraft(bool composer)
{
    if (g_guestCommentWrite.discarded || !GuestCommentMatchesEditor(composer, false) ||
        (!g_guestCommentWrite.process && !g_guestCommentWrite.failed)) return;
    std::wstring text;
    const bool read = composer
        ? g_composerBox && g_cardReadInput && g_cardReadInput(g_composerBox, &text)
        : SelReadCommentText(&text);
    if (read) g_guestCommentWrite.draft = text;
}

static void GuestCommentRestoreDraft(bool composer)
{
    auto& write = g_guestCommentWrite;
    if (write.authority != g_guestCommentAuthority.load() || write.root != g_pickRoot ||
        write.handle != (composer ? g_composerHandle : g_selHandle) || (!write.process && !write.failed)) return;
    if ((write.editor == GuestCommentEditor::Selection || write.editor == GuestCommentEditor::Composer ||
        write.editor == GuestCommentEditor::Delete) &&
        (write.wire != (composer ? g_composerCommentWire : g_selCommentWire) ||
            (write.wire && (!g_wireOf || g_wireOf(write.handle) != write.wire)))) return;
    std::wstring anchor;
    if (write.anchor.empty() || !g_anchorOf || !g_anchorOf(write.handle, &anchor) || anchor != write.anchor) return;
    if (composer) {
        g_composerId = write.id;
        g_composerRevision = write.revision;
        void* box = nullptr;
        if (g_composerBox && SUCCEEDED(g_composerBox->QueryInterface(DevToolsIid<DevToolsXC::ITextBox>(), &box)) && box) {
            HSTRING text = nullptr;
            if (SUCCEEDED(WindowsCreateString(write.draft.c_str(), static_cast<UINT32>(write.draft.size()), &text))) {
                DevToolsPutTextBoxText(box, text); WindowsDeleteString(text);
            }
            reinterpret_cast<IUnknown*>(box)->Release();
        }
    } else {
        g_selCommentId = write.id;
        g_selCommentRevision = write.revision;
        SetSelCommentText(write.draft);
    }
    GuestCommentSetStatus(composer ? g_composerBorder : g_selUi,
        composer ? L"GuestCommentStatus" : L"DevToolsGuestCommentStatus", write.status);
}

// A short confirmation near the toolbar. The panel may already have moved to the next element or closed when a
// save completes, so the confirmation cannot live in it.
static IInspectable* g_commentToast = nullptr;
static UINT_PTR g_commentToastTimer = 0;
static std::wstring g_commentToastText;   // the last confirmation shown

static void HideCommentToast()
{
    if (g_commentToastTimer) { KillTimer(nullptr, g_commentToastTimer); g_commentToastTimer = 0; }
    if (!g_commentToast) return;
    if (g_canvasChildren) {
        unsigned int idx = 0; unsigned char found = 0;
        if (SUCCEEDED(DevToolsVecIndexOf(g_canvasChildren, g_commentToast, &idx, &found)) && found)
            DevToolsVecRemoveAt(g_canvasChildren, idx);
    }
    g_commentToast->Release();
    g_commentToast = nullptr;
}

static void CALLBACK CommentToastTimerProc(HWND, UINT, UINT_PTR, DWORD) { HideCommentToast(); }

static std::wstring CommentToastText(bool deleted, bool linked)
{
    return deleted ? std::wstring(L"Comment deleted")
                   : std::wstring(L"Comment saved") + (linked ? L"" : L" \u00b7 Not linked");
}

static void ShowCommentToast(const std::wstring& text, bool problem)
{
    HideCommentToast();
    g_commentToastText = text;
    if (!g_canvasChildren) return;
    const DevToolsPalette& pal = DevToolsInverseAcrylicPalette();
    int w = 0, h = 0;
    if (!GetClientSizeDips(&w, &h)) { w = 800; h = 600; }
    const int toastW = 240;
    RECT bar{};
    const bool haveBar = g_toolbarSurface.xamlRootKey == g_activeSurface.xamlRootKey && ToolbarRectDip(&bar);
    int x = haveBar ? (int)bar.right - toastW : (w - toastW) / 2;
    int y = haveBar ? (int)bar.top - 44 : h - 64;
    if (haveBar && y < 8) y = (int)bar.bottom + 8;
    x = (std::max)(8, (std::min)(x, w - toastW - 8));
    y = (std::max)(8, y);
    const std::wstring markup =
        L"<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\""
        L" Canvas.Left=\"" + std::to_wstring(x) + L"\" Canvas.Top=\"" + std::to_wstring(y) + L"\" Canvas.ZIndex=\"3000\""
        L" Width=\"" + std::to_wstring(toastW) + L"\" IsHitTestVisible=\"False\" RequestedTheme=\"" +
        DevToolsInverseAppRequestedTheme() + L"\" Background=\"" + pal.surface + L"\" BorderBrush=\"" + pal.rim +
        L"\" BorderThickness=\"1\" CornerRadius=\"8\" Padding=\"12,8\" BackgroundSizing=\"InnerBorderEdge\""
        L" AutomationProperties.AutomationId=\"DevToolsCommentToast\" AutomationProperties.LiveSetting=\"Polite\""
        L" AutomationProperties.Name=\"" + DevToolsXmlEscape(text) + L"\">"
        L"<StackPanel Orientation=\"Horizontal\" Spacing=\"8\">"
        L"<FontIcon FlowDirection=\"LeftToRight\" Glyph=\"" + std::wstring(problem ? L"&#xE7BA;" : L"&#xE73E;") +
        L"\" FontSize=\"14\" VerticalAlignment=\"Center\" Foreground=\"" + (problem ? pal.caution : pal.accent) + L"\"/>"
        L"<TextBlock x:Name=\"DevToolsCommentToastText\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\""
        L" Text=\"" + DevToolsXmlEscape(text) + L"\" TextWrapping=\"Wrap\" VerticalAlignment=\"Center\""
        L" Foreground=\"" + pal.textPrimary + L"\"/></StackPanel></Border>";
    IInspectable* toast = nullptr;
    if (FAILED(LoadMarkup(markup, &toast)) || !toast) {
        DevToolsOverlayLog(L"overlay.comment toast markup failed");
        return;
    }
    IInspectable* ui = nullptr;
    if (SUCCEEDED(toast->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), reinterpret_cast<void**>(&ui))) && ui) {
        unsigned int n = 0; DevToolsVecGetSize(g_canvasChildren, &n);
        if (g_cardScope) g_cardScope(1);
        DevToolsVecInsertAt(g_canvasChildren, n, ui);
        if (g_cardScope) g_cardScope(0);
        g_commentToast = ui;
    }
    toast->Release();
    g_commentToastTimer = SetTimer(nullptr, 0, problem ? 4000 : 1800, &CommentToastTimerProc);
    DevToolsOverlayLog(L"overlay.comment toast '%s'", text.c_str());
}

static void GuestCommentComplete(DWORD exitCode, bool acknowledged, const wchar_t* stage)
{
    auto& write = g_guestCommentWrite;
    if (write.discarded) {
        write = {};
        return;
    }
    const bool composer = write.editor == GuestCommentEditor::Composer;
    const bool saved = acknowledged && (exitCode == 0 ||
        (write.authority && (exitCode == 0x57410001 || exitCode == 0x57410002)));
    write.persisted = saved;
    std::wstring revision;
    bool snapshotMatches = write.editor == GuestCommentEditor::Delete;
    for (const auto& pin : g_pins) if (pin.id == write.id) {
        revision = pin.revision;
        snapshotMatches = write.editor != GuestCommentEditor::Delete && pin.text == write.submitted;
        if (write.authority && saved && exitCode == 0 && (write.editor == GuestCommentEditor::Delete || pin.text != write.submitted))
            exitCode = 0x57410002;
        break;
    }
    // An authoritative snapshot already reflects the store; a completion that disagrees lost a race.
    write.superseded = saved && !write.authority && g_commentSnapshotAuthoritative && !snapshotMatches;
    if (write.superseded) exitCode = 0x57410002;
    const bool newerDraft = (write.editor == GuestCommentEditor::Selection || write.editor == GuestCommentEditor::Composer) &&
        !DevToolsCommentText::Unchanged(write.draft, write.submitted);
    write.failed = !saved || exitCode == 0x57410002 || newerDraft;
    write.status = !saved ? DevToolsCommentText::WriterFailureStatus(exitCode, stage, write.authority != nullptr) :
        exitCode == 0x57410001 ? L"Saved on the host, but markers could not refresh. Refresh comments before editing again." :
        exitCode == 0x57410002 ? (write.authority ?
            L"The earlier save reached the host, which now has a newer edit. Draft retained; refresh before editing." :
            L"Saved, but the refreshed comments differ. Draft retained; refresh before editing.") :
        newerDraft ? (write.authority ? L"Submitted text was saved on the host. Your newer draft is not saved yet." :
            L"Submitted text was saved. Your newer draft is not saved yet.") :
        DevToolsCommentText::SavedStatus(write.authority != nullptr, write.linked);
    if (write.editor == GuestCommentEditor::Selection || write.editor == GuestCommentEditor::Delete) {
        // A save that reached the store is never reported as not saved; one the refreshed comments disagree with
        // says so.
        if (saved && !write.superseded)
            ShowCommentToast(CommentToastText(write.editor == GuestCommentEditor::Delete, write.linked), false);
        else if (saved)
            ShowCommentToast(L"Comment saved, but it changed since. Reopen the element to check it.", true);
        else if (!GuestCommentMatchesEditor(false, false))
            ShowCommentToast(L"Comment not saved. Pick the element again to retry.", true);
    }
    if (write.editor == GuestCommentEditor::WindowResolve) {
        auto completed = std::move(write.completed);
        if (write.authority == g_guestCommentAuthority.load() && write.root == g_pickRoot &&
            write.generation == g_selGen && completed)
            completed(acknowledged ? static_cast<int>(exitCode) : 1);
        return;
    }
    if (!GuestCommentMatchesEditor(composer)) {
        // The panel has moved on (the next element was picked, or it closed), but this save still earns its marker.
        if (!write.authority && !composer && saved && !write.failed && !g_commentSnapshotAuthoritative) {
            if (write.editor == GuestCommentEditor::Delete) RemovePinById(write.id);
            else if (write.handle) UpsertPin(write.handle, write.id, write.submitted, write.bounds);
        }
        if (write.deleteRequested) {
            write.deleteRequested = false;
            write.failed = true;
            write.status = L"Delete was not confirmed because the editor target changed. Draft retained.";
        }
        if (!write.authority && GuestCommentMatchesEditor(composer, false)) {
            SetSelectionOperationError(L"Comment", L"");
            GuestCommentRenderStatus();
        }
        return;
    }
    GuestCommentRenderStatus();
    if (!saved || exitCode == 0x57410002) {
        if (write.deleteRequested) {
            const auto requestedText = write.deleteText;
            write.deleteRequested = false;
            SelDeleteComment(&requestedText);
        }
        return;
    }
    if (!write.authority) {
        // Ordered snapshots, not completion timing, own the marker set once one has arrived.
        if (!g_commentSnapshotAuthoritative) {
            if (write.editor == GuestCommentEditor::Delete) RemovePinById(write.id);
            else if (write.handle) UpsertPin(write.handle, write.id, write.submitted, write.bounds);
        } else if (write.editor != GuestCommentEditor::Delete && write.handle) {
            SetCommentsShown(true);
        }
        if (!composer) SetSelectionOperationError(L"Comment", L"");
    }
    if (composer) {
        g_composerInitText = write.submitted;
        g_composerRevision = revision;
        std::wstring current;
        if (exitCode == 0 && g_composerBox && g_cardReadInput && g_cardReadInput(g_composerBox, &current) &&
            DevToolsCommentText::Unchanged(current, write.submitted)) CloseComposerRestoreBadge();
    } else if (write.editor == GuestCommentEditor::Delete) {
        std::wstring current;
        const bool stillDeletedText = SelReadCommentText(&current) && (current.empty() || current == write.submitted);
        if (!stillDeletedText) {
            write.status = L"Comment deleted. Your newer draft is not saved yet.";
            GuestCommentRenderStatus();
        }
        g_selCommentSaved.clear();
        g_selCommentRevision.clear();
        g_selCommentId = GenCommentId();
        if (stillDeletedText) SetSelCommentText(L"");
        SetSelCommentDeleteVisible(false);
    } else {
        g_selCommentSaved = write.submitted;
        g_selCommentRevision = revision;
        SetSelCommentDeleteVisible(true);
        // Save is done with this element, so the panel closes and pick mode moves on. A comment that is not linked
        // to source keeps the panel, which is where that is said.
        std::wstring current;
        if (write.closeOnSave && exitCode == 0 && write.linked && !write.failed && !write.deleteRequested &&
            SelReadCommentText(&current) && DevToolsCommentText::Unchanged(current, write.submitted))
            DismissSelectionPanel();
    }
    if (write.deleteRequested) {
        const auto requestedText = write.deleteText;
        write.deleteRequested = false;
        SelDeleteComment(&requestedText);
    }
}

// Start the next waiting save once the writer is free. A save that cannot start says so, since its panel is gone.
static void GuestCommentStartQueued()
{
    while (!g_guestCommentWrite.process && !g_guestCommentQueue.empty()) {
        QueuedCommentWrite next = std::move(g_guestCommentQueue.front());
        g_guestCommentQueue.pop_front();
        if (!LaunchWinapp(next.args, next.what.c_str(), &next.write))
            ShowCommentToast(L"Comment not saved. Pick the element again to retry.", true);
    }
}

static void CALLBACK GuestCommentTimerProc(HWND, UINT, UINT_PTR, DWORD)
{
    auto& write = g_guestCommentWrite;
    if (!write.process) return;
    const DWORD state = WaitForSingleObject(write.process, 0);
    if (state == WAIT_TIMEOUT && GetTickCount64() - write.began < 90'000) return;
    GuestCommentRememberDraft(write.editor == GuestCommentEditor::Composer);
    DWORD code = 1;
    const bool acknowledged = state == WAIT_OBJECT_0 && GetExitCodeProcess(write.process, &code);
    const auto error = acknowledged ? code : state == WAIT_TIMEOUT ? ERROR_TIMEOUT : GetLastError();
    if (state != WAIT_OBJECT_0) TerminateProcess(write.process, 1);
    CloseHandle(write.process);
    write.process = nullptr;
    if (g_guestCommentTimer) { KillTimer(nullptr, g_guestCommentTimer); g_guestCommentTimer = 0; }
    GuestCommentComplete(error, acknowledged, acknowledged ? nullptr :
        state == WAIT_TIMEOUT ? L"writer-timeout" : L"writer-wait");
    GuestCommentStartQueued();
}

// This panel's comment save that is waiting for an earlier one to finish, if any.
static QueuedCommentWrite* SelQueuedCommentWrite()
{
    for (auto& queued : g_guestCommentQueue)
        if (queued.write.handle == g_selHandle && queued.write.wire == g_selCommentWire && queued.write.id == g_selCommentId)
            return &queued;
    return nullptr;
}

// "Cleared" and "was always empty" are different states and are distinguished by g_selCommentSaved, which holds
// what is persisted for this anchor right now (the prefill on open, the last commit after that): empty box +
// something saved = the user removed it, empty box + nothing saved = an untouched placeholder that must d.
// Save, blur, and Ctrl+Enter share CLI persistence so inline panel and composer use one writer path.
static void DevToolsSelCommitComment(bool explicitSave)
{
    if (!g_selComment) return;
    if (g_selRootLost) {
        SetSelectionOperationError(L"Comment",
            L"The original window closed. Copy your draft before closing this panel; Close discards it.");
        return;
    }
    std::wstring text;
    if (!SelReadCommentText(&text)) {
        SetSelectionOperationError(L"Comment", L"Couldn't read your comment. Copy it before closing and try again.");
        return;
    }
    // Text already on its way to the writer needs no second save; an explicit save only asks it to finish the
    // comment when it lands.
    if (GuestCommentMatchesEditor(false, false) && g_guestCommentWrite.process &&
        DevToolsCommentText::Unchanged(text, g_guestCommentWrite.submitted)) {
        if (explicitSave) g_guestCommentWrite.closeOnSave = true;
        return;
    }
    if (QueuedCommentWrite* queued = SelQueuedCommentWrite(); queued &&
        DevToolsCommentText::Unchanged(text, queued->write.submitted)) {
        if (explicitSave) queued->write.closeOnSave = true;
        return;
    }
    if (!explicitSave && GuestCommentMatchesEditor(false, false) && g_guestCommentWrite.superseded) {
        g_selDismissCommitFailed = true;
        GuestCommentRememberDraft(false);
        GuestCommentRenderStatus();
        return;
    }
    if (!g_guestCommentAuthority.load() && GuestCommentMatchesEditor(false, false) &&
        !g_guestCommentWrite.process && g_guestCommentWrite.persisted && !g_guestCommentWrite.superseded)
        g_selCommentSaved = g_guestCommentWrite.editor == GuestCommentEditor::Delete ? L"" : g_guestCommentWrite.submitted;
    const bool retryUnconfirmed = !g_guestCommentAuthority.load() && GuestCommentMatchesEditor(false, false) &&
        !g_guestCommentWrite.process && g_guestCommentWrite.failed &&
        (!g_guestCommentWrite.persisted || g_guestCommentWrite.superseded);
    if (DevToolsCommentText::Unchanged(text, g_selCommentSaved) && !retryUnconfirmed) {
        if (!g_guestCommentAuthority.load() && GuestCommentMatchesEditor(false, false)) {
            if (g_guestCommentWrite.process) {
                // The running save is of different text, so closing now would keep what the box no longer says.
                if (!DevToolsCommentText::Unchanged(text, g_guestCommentWrite.submitted)) g_selDismissCommitFailed = true;
            } else {
                DiscardSelectionCommentDraft();
                GuestCommentSetStatus(g_selUi, L"DevToolsGuestCommentStatus",
                    text.empty() ? L"" : DevToolsCommentText::SavedStatus(false, IsLinkedElement(g_selHandle)));
            }
        }
        // Save on text that is already saved (leaving the box saved it) finishes the comment like any save.
        if (explicitSave && !text.empty() && !g_guestCommentWrite.process && IsLinkedElement(g_selHandle))
            DismissSelectionPanel();
        return;
    }
    DevToolsOverlayLog(L"overlay.sel comment commit: %zu chars (saved %zu) id=%s",
                  text.size(), g_selCommentSaved.size(), g_selCommentId.c_str());
    if (DevToolsCommentText::IsBlank(text)) {
        if (!g_selCommentSaved.empty() || retryUnconfirmed) SelDeleteComment();
        return;
    }
    if (g_selCommentId.empty()) g_selCommentId = GenCommentId();
    LaunchCommentAdd(text, g_selCommentId);
    if (explicitSave && g_guestCommentWrite.process && GuestCommentMatchesEditor(false, false))
        g_guestCommentWrite.closeOnSave = true;
    if (QueuedCommentWrite* queued = SelQueuedCommentWrite(); queued && g_commentSourceError.empty()) {
        if (explicitSave) queued->write.closeOnSave = true;
        SetSelectionOperationError(L"Comment", L"");
        GuestCommentSetStatus(g_selUi, L"DevToolsGuestCommentStatus", L"Saving...");
        return;
    }
    if (!g_commentSourceError.empty()) {
        SetSelectionOperationError(L"Comment", g_commentSourceError);
    } else if (!g_guestCommentAuthority.load() && GuestCommentMatchesEditor(false, false) &&
        (g_guestCommentWrite.process || g_guestCommentWrite.failed) && !g_guestCommentWrite.status.empty()) {
        SetSelectionOperationError(L"Comment", L"");
        // A save still running does not hold the panel open: it completes after the panel closes, and a failure keeps
        // the draft for the next time this element's panel opens. A save that already failed keeps it open.
        if (!g_guestCommentWrite.process) g_selDismissCommitFailed = true;
    } else if (!g_guestCommentAuthority.load()) {
        SetSelectionOperationError(L"Comment", g_commentSourceError.empty()
            ? L"Couldn't start saving your comment. Keep this panel open and try again." : g_commentSourceError);
    }
}

// The comment box holds text that is not saved yet.
static bool SelHasCommentDraft()
{
    std::wstring draft;
    return g_selComment && !g_selRootLost && SelReadCommentText(&draft) && !DevToolsCommentText::IsBlank(draft) &&
        !DevToolsCommentText::Unchanged(draft, g_selCommentSaved);
}

// Before the panel moves to another element: save what it holds. False keeps the panel where it is, because its
// comment draft could not be handed to the writer and moving would drop it.
static bool SelFinishBeforeMoving()
{
    if (g_selRootLost) return false;
    g_selDismissCommitFailed = false;
    const unsigned generation = g_selGen;
    DevToolsSelFlushCommits();
    if (generation != g_selGen) return true;
    if (g_selDismissCommitFailed) return false;
    std::wstring draft;
    if (!g_selComment || !SelReadCommentText(&draft)) return true;
    if (DevToolsCommentText::IsBlank(draft) || DevToolsCommentText::Unchanged(draft, g_selCommentSaved)) return true;
    if (GuestCommentMatchesEditor(false, false) && g_guestCommentWrite.process) return true;
    return SelQueuedCommentWrite() != nullptr;
}

// Delete this anchor's comment and put the panel back into the "no comment here" state: the box empties, the
// delete glyph goes, the marker goes, and the anchor takes a FRESH id so typing again authors a new comment
// rather than resurrecting the id that was just removed.
static void SelDeleteComment(const std::wstring* requestedText)
{
    const std::wstring doomed = g_selCommentId;
    if (doomed.empty()) return;
    std::wstring current;
    if (!SelReadCommentText(&current)) {
        SetSelectionOperationError(L"Comment", L"Couldn't read your draft. Nothing was deleted.");
        return;
    }
    if (g_guestCommentWrite.process && g_guestCommentWrite.editor == GuestCommentEditor::Selection &&
        GuestCommentMatchesEditor(false, false)) {
        if (!GuestCommentMatchesEditor(false)) {
            SetSelectionOperationError(L"Comment", L"The earlier save is still completing. Try Delete again when it finishes.");
            return;
        }
        g_guestCommentWrite.deleteRequested = true;
        g_guestCommentWrite.deleteText = current;
        g_guestCommentWrite.status = L"Waiting for the current save before deleting...";
        GuestCommentRenderStatus();
        g_selDismissCommitFailed = true;
        return;
    }
    LaunchCommentDelete(doomed, requestedText ? *requestedText : current);
    if (!g_guestCommentAuthority.load() && GuestCommentMatchesEditor(false, false) &&
        (g_guestCommentWrite.process || g_guestCommentWrite.failed) && !g_guestCommentWrite.status.empty()) {
        SetSelectionOperationError(L"Comment", L"");
        g_selDismissCommitFailed = true;
    } else if (!g_guestCommentAuthority.load()) {
        SetSelectionOperationError(L"Comment", L"Couldn't start deleting your comment. Try again before closing.");
    }
}

static void OnSelCommentDeleteClick(void*, void*)
{
    SelDeleteComment();
}

static void OnSelCommentSaveClick(void*, void*)
{
    DevToolsSelCommitComment(true);
}

static void OnSelCommentTextChanged(void*, void*)
{
    if (!g_selComment || g_selRootLost) return;
    GuestCommentRememberDraft(false);
    if (GuestCommentMatchesEditor(false, false) && (g_guestCommentWrite.process || g_guestCommentWrite.failed)) return;
    std::wstring text;
    if (!SelReadCommentText(&text)) {
        SetSelectionOperationError(L"Comment", L"Couldn't read your draft. Nothing was saved.");
        return;
    }
    if (!DevToolsCommentText::Unchanged(text, g_selCommentSaved))
        GuestCommentSetStatus(g_selUi, L"DevToolsGuestCommentStatus", L"Unsaved changes.");
}

// Comment box LostFocus: commit FIRST, then fall through to the focus-within decrement (the collapse gate), so
// a pointer-out that races the blur can never collapse the panel before the text is saved.
static void OnSelCommentLostFocus(void*, void*)
{
    DevToolsSelCommitComment();
    OnSelLostFocus(nullptr, nullptr);
    g_selFocusedIsComment = false;
}
static std::wstring BuildComposerMarkup(const std::wstring& type, const std::wstring& name, int x, int y,
                                        const std::wstring& initText, const std::wstring& srcUri, unsigned int srcLine,
                                        const std::wstring& authoredState = L"", const std::wstring& authoredXaml = L"")
{
    std::wstring label = type.empty() ? std::wstring(L"Element") : ShortTypeName(type);
    if (!name.empty()) label += L" #" + name;
    bool editing = !initText.empty();
// Leaf.xaml file from the ms-appx URI (".../MainWindow.xaml" -> "MainWindow.xaml") for the what/where strip.
    std::wstring leaf;
    if (!srcUri.empty()) { size_t sl = srcUri.find_last_of(L'/'); leaf = (sl == std::wstring::npos) ? srcUri : srcUri.substr(sl + 1); }
    const DevToolsPalette& pal = DevToolsBuildPalette();
    std::wstring m;
    m += L"<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"";
    m += L" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";
    m += L" AutomationProperties.AutomationId=\"WinAppDevToolsComposer\" AutomationProperties.Name=\"Add comment\"";
    m += L" Canvas.Left=\"" + std::to_wstring(x) + L"\" Canvas.Top=\"" + std::to_wstring(y) + L"\" Canvas.ZIndex=\"2100\"";
    m += L" Width=\"300\" Background=\"" + pal.surface + L"\" CornerRadius=\"8\"";
    m += L" BorderBrush=\"" + pal.border + L"\" BorderThickness=\"1\">";
    m += L"<StackPanel Orientation=\"Vertical\">";
    m += L"<Grid Background=\"" + pal.surfaceAlt + L"\" Padding=\"10,6,6,6\">";
    m += L"<Grid.ColumnDefinitions><ColumnDefinition Width=\"*\"/><ColumnDefinition Width=\"Auto\"/></Grid.ColumnDefinitions>";
    m += L"<TextBlock Grid.Column=\"0\" VerticalAlignment=\"Center\" FontSize=\"12\" FontWeight=\"SemiBold\"";
    m += L" TextTrimming=\"CharacterEllipsis\" Foreground=\"" + pal.textPrimary + L"\"";
    m += L" Text=\"" + DevToolsXmlEscape((editing ? std::wstring(L"Edit comment \u00B7 ") : std::wstring(L"Comment \u00B7 ")) + label) + L"\"/>";
    m += L"<Button Grid.Column=\"1\" Style=\"{x:Null}\" Width=\"24\" Height=\"22\" Padding=\"0\" BorderThickness=\"0\"";
    m += L" Background=\"Transparent\" HorizontalAlignment=\"Right\" AutomationProperties.Name=\"Close\">";
    m += L"<FontIcon Glyph=\"&#xE711;\" FontSize=\"11\" Foreground=\"" + pal.textSecondary + L"\"/></Button>";
    m += L"</Grid>";
// Muted when source-mapped; warning-tinted when the anchor is weak (no app source).
    m += L"<StackPanel Orientation=\"Horizontal\" Spacing=\"6\" Margin=\"10,7,10,0\" VerticalAlignment=\"Center\">";
    m += L"<FontIcon Glyph=\"&#xE7C9;\" FontSize=\"11\" VerticalAlignment=\"Center\"";
    m += L" Foreground=\"" + pal.textSecondary + L"\"/>";
    m += L"<TextBlock VerticalAlignment=\"Center\" FontSize=\"11\" TextTrimming=\"CharacterEllipsis\"";
    m += L" Foreground=\"" + pal.textSecondary + L"\" Text=\"" + DevToolsXmlEscape(label) + L"\"/>";
    if (!leaf.empty()) {
        std::wstring where = leaf; if (srcLine > 0) where += L":" + std::to_wstring(srcLine);
        m += L"<FontIcon Glyph=\"&#xE8A5;\" FontSize=\"11\" Margin=\"6,0,0,0\" VerticalAlignment=\"Center\"";
        m += L" Foreground=\"" + pal.textTertiary + L"\"/>";
        m += L"<TextBlock VerticalAlignment=\"Center\" FontSize=\"11\" TextTrimming=\"CharacterEllipsis\"";
        m += L" Foreground=\"" + pal.textTertiary + L"\" Text=\"" + DevToolsXmlEscape(where) + L"\"/>";
    } else {
        m += L"<FontIcon Glyph=\"&#xE7BA;\" FontSize=\"11\" Margin=\"6,0,0,0\" VerticalAlignment=\"Center\"";
        m += L" Foreground=\"" + pal.caution + L"\"/>";
        m += L"<TextBlock VerticalAlignment=\"Center\" FontSize=\"11\" Text=\"not source-mapped\"";
        m += L" Foreground=\"" + pal.caution + L"\"/>";
    }
    m += L"</StackPanel>";
    m += L"<TextBox AcceptsReturn=\"True\" TextWrapping=\"Wrap\" MinHeight=\"72\" MaxHeight=\"150\" Margin=\"10,10,10,0\"";
    if (g_guestCommentAuthority.load()) m += L" MaxLength=\"65536\"";
    m += L" AutomationProperties.AutomationId=\"WinAppDevToolsComposerInput\" AutomationProperties.Name=\"Comment text\"";
    if (editing) m += L" Text=\"" + DevToolsXmlEscape(initText, true) + L"\"";
    m += L" PlaceholderText=\"Describe what needs fixing on this element\u2026\"/>";
    m += L"<StackPanel Orientation=\"Horizontal\" Spacing=\"8\" HorizontalAlignment=\"Right\" Margin=\"10,8,10,10\">";
    m += L"<Button Content=\"Cancel\" MinWidth=\"72\" AutomationProperties.Name=\"Cancel\"/>";
    m += L"<Button Content=\"Save comment\" MinWidth=\"110\" Style=\"{StaticResource AccentButtonStyle}\"";
    m += L" AutomationProperties.AutomationId=\"WinAppDevToolsComposerSave\" AutomationProperties.Name=\"Save comment\"/>";
    m += L"</StackPanel>";
    m += L"<TextBlock x:Name=\"GuestCommentStatus\" Visibility=\"Collapsed\" TextWrapping=\"Wrap\" Margin=\"10,0,10,10\""
         L" Style=\"{StaticResource CaptionTextBlockStyle}\" AutomationProperties.AutomationId=\"WinAppDevToolsCommentStatus\""
         L" AutomationProperties.LiveSetting=\"Polite\"/>";
    if (authoredState == L"likely") {
        m += L"<StackPanel Margin=\"12,0,12,12\" Spacing=\"8\">"
            L"<TextBlock Text=\"Likely source; compiled identity and line preservation are unverified.\""
            L" TextWrapping=\"Wrap\" Style=\"{StaticResource CaptionTextBlockStyle}\"/>"
            L"<Expander Header=\"Review likely XAML\"><TextBlock Text=\"" + XmlEscapeLines(authoredXaml) +
            L"\" TextWrapping=\"Wrap\" IsTextSelectionEnabled=\"True\" FontFamily=\"Consolas\""
            L" Style=\"{StaticResource CaptionTextBlockStyle}\"/></Expander>" +
            LikelySourceConfirmationMarkup(true) + L"</StackPanel>";
    }
    m += L"</StackPanel></Border>";
    return m;
}

// Wire the parsed composer: Border -> outer StackPanel [0]header-Grid [1]what/where-strip [2]TextBox
// [3]footer-StackPanel. Keeps the TextBox AddRef'd for Save reads.
static void WireComposer(IInspectable* composerBorder)
{
    IInspectable* outer = nullptr;
    { IInspectable* b = nullptr;
      if (SUCCEEDED(composerBorder->QueryInterface(DevToolsIid<DevToolsXC::IBorder>(), reinterpret_cast<void**>(&b))) && b) { DevToolsBorderGetChild(b, &outer); b->Release(); } }
    if (!outer) { DevToolsOverlayLog(L"overlay.composer wire: no outer panel"); return; }
    IInspectable* k = CardChildrenOf(outer); outer->Release();
    if (!k) { DevToolsOverlayLog(L"overlay.composer wire: no children"); return; }

    auto wireBtn = [](IInspectable* btn, EventSink* sink, void (*cb)(void*, void*)) {
        if (!btn) return;
        IInspectable* bb = nullptr;
        if (SUCCEEDED(btn->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), reinterpret_cast<void**>(&bb))) && bb) {
            sink->Bind(&IID_RoutedEventHandler_Del, cb);
            __int64 tok = 0; DevToolsAddClick(bb, sink, &tok); bb->Release();
        }
    };

    IInspectable* header = nullptr;
    if (SUCCEEDED(DevToolsVecGetAt(k, 0, &header)) && header) {
        IInspectable* hk = CardChildrenOf(header);
        if (hk) { IInspectable* close = nullptr; if (SUCCEEDED(DevToolsVecGetAt(hk, 1, &close)) && close) { wireBtn(close, &g_composerCloseSink, &OnComposerCloseClick); close->Release(); } hk->Release(); }
        header->Release();
    }
// input TextBox (kept AddRef'd) — index 2 (after header-Grid[0] + what/where-strip[1]).
    { IInspectable* box = nullptr; if (SUCCEEDED(DevToolsVecGetAt(k, 2, &box)) && box) g_composerBox = box; }
    IInspectable* footer = nullptr;
    if (SUCCEEDED(DevToolsVecGetAt(k, 3, &footer)) && footer) {
        IInspectable* fk = CardChildrenOf(footer);
        if (fk) {
            IInspectable* cancel = nullptr; if (SUCCEEDED(DevToolsVecGetAt(fk, 0, &cancel)) && cancel) { wireBtn(cancel, &g_composerCancelSink, &OnComposerCancelClick); cancel->Release(); }
            IInspectable* save = nullptr;   if (SUCCEEDED(DevToolsVecGetAt(fk, 1, &save))   && save)   { wireBtn(save,   &g_composerSaveSink,   &OnComposerSaveClick);   save->Release(); }
            fk->Release();
        }
        footer->Release();
    }
    k->Release();
    DevToolsOverlayLog(L"overlay.composer wired (box=%p)", (void*)g_composerBox);
}

static void ShowComposerForElement(IInspectable* el, bool fromCommentMode)
{
    if (!el || !g_canvasChildren) return;
    std::wstring type, name, authoredState, authoredXaml;
    if (g_cardRead) {
        std::vector<DevToolsCardRow> rows;
        g_cardRead(el, &type, &name, &rows, &authoredState, &authoredXaml);
    }

// Resolve the element's raw handle (pin-registry key + edit lookup).
    InstanceHandle key = 0;
    if (g_pickDiag) g_pickDiag->GetHandleFromIInspectable(el, &key);
    if (!GuestCommentCanEdit(key)) return;
    SetCommentTarget(key, true);
    g_composerFromCommentMode = fromCommentMode;
    g_composerInitText.clear();
    g_composerRevision.clear();
    g_composerId.clear();
    for (const auto& p : g_pins) { if (key && p.handle == key) { g_composerId = p.id; g_composerInitText = p.text; g_composerRevision = p.revision; break; } }
    if (g_composerId.empty()) g_composerId = GenCommentId();
    g_composerBounds = g_lastPickBoundsDip;

// Resolve the element's XAML source (file:line) so the composer's what/where strip can show it.
    g_composerSrcFile.clear(); g_composerSrcLine = 0;
    if (g_srcRead && key) { std::wstring f; unsigned int ln = 0, col = 0; if (g_srcRead(key, &f, &ln, &col)) { g_composerSrcFile = f; g_composerSrcLine = ln; } }

// Freeze the element-count oracle at the pre-composer baseline for the composer's lifetime (its TextBox spawns
// async default-template parts that can't be excluded by handle), then clear any prior composer.
    if (g_cardFreeze) g_cardFreeze(1);
    ClearComposer();

// Anchor adjacent to the element (same content-DIP Canvas space + placement logic as the card): prefer below
// the element's left edge, flip above on bottom overflow, clamp into the client area; degenerate -> top-right.
    const int cw = 300; const int gap = 8;
    int w = 0, h = 0; if (!GetClientSizeDips(&w, &h)) { w = 1600; h = 900; }
    const RECT& e = g_lastPickBoundsDip;
    bool haveBounds = (e.right > e.left) && (e.bottom > e.top);
    const int estH = 168;
    int cx, cy;
    if (haveBounds) {
        cx = e.left;
        cy = e.bottom + gap;
        if (cy + estH > h - gap && (e.top - gap - estH) >= gap) cy = e.top - gap - estH;
        if (cx + cw > w - gap) cx = w - cw - gap;
        if (cx < gap) cx = gap;
        if (cy < gap) cy = gap;
        if (cy + estH > h - gap) cy = (h - gap - estH > gap) ? (h - gap - estH) : gap;
    } else { cx = (w > 332) ? (w - 316) : 16; cy = 64; }

    IInspectable* insp = nullptr;
    if (FAILED(LoadMarkup(BuildComposerMarkup(type, name, cx, cy, g_composerInitText, g_composerSrcFile, g_composerSrcLine,
        authoredState, authoredXaml), &insp)) || !insp) {
        DevToolsOverlayLog(L"overlay.composer load failed"); if (g_cardFreeze) g_cardFreeze(0); return;
    }
    g_composerTarget = el; g_composerTarget->AddRef();
    g_composerBorder = insp; g_composerBorder->AddRef();
    WireComposer(insp);
    GuestCommentRestoreDraft(true);
    IInspectable* cui = nullptr;
    if (SUCCEEDED(insp->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), reinterpret_cast<void**>(&cui))) && cui) {
        unsigned int n = 0; DevToolsVecGetSize(g_canvasChildren, &n);
        if (g_cardScope) g_cardScope(1);
        DevToolsVecInsertAt(g_canvasChildren, n, cui);
        if (g_cardScope) g_cardScope(0);
        g_composerUi = cui;
        ResetPickPointer();
    }
    insp->Release();
    DevToolsOverlayLog(L"overlay.composer shown type=%s name=%s id=%s edit=%d at %d,%d",
                  type.c_str(), name.c_str(), g_composerId.c_str(), (int)!g_composerInitText.empty(), cx, cy);
}
