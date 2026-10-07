// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsOverlay.cpp; included into that translation unit, not compiled on its own.

// ============================ Comment writer ============================ The comment flyout and the inspector save
// comments by running `winapp devtools comments add`: the CLI is the store's only writer, and the overlay keeps the
// markers it pushes back.
static bool g_commentMode = false;
static std::wstring g_selCommentRevision;

// Captured once when the editor opens: a recycled raw handle must not become the save target.
static void SetCommentTarget(InstanceHandle raw)
{
    g_selHandle = raw;
    g_selCommentWire = raw && g_wireOf ? g_wireOf(raw) : 0;
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
enum class GuestCommentEditor { Selection, Delete, WindowResolve, Window };
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
static void SetCommentMode(bool on) { g_commentMode = on; }

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
    if (guest && g_guestCommentWrite.process && !authority &&
        (guest->editor == GuestCommentEditor::Selection || guest->editor == GuestCommentEditor::Window) &&
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
    if ((guest->editor == GuestCommentEditor::Selection || guest->editor == GuestCommentEditor::Window) &&
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

static bool LaunchCommentAdd(const std::wstring& text, const std::wstring& id)
{
    const auto handle = g_selHandle;
    const auto wire = g_selCommentWire;
    g_commentSourceError.clear();
    std::wstring type, name, state, xaml;
    std::vector<DevToolsCardRow> rows;
    winrt::com_ptr<IInspectable> element;
    const bool hasSource = HasAuthoredSource(handle);
    if (hasSource && (!g_pickDiag || !g_cardRead || FAILED(g_pickDiag->GetIInspectableFromHandle(handle, element.put())) ||
        !element || !g_cardRead(element.get(), &type, &name, &rows, &state, &xaml))) {
        g_commentSourceError = L"Couldn't verify this comment's source. Reselect the element before saving.";
        return false;
    }
    const bool likelyConfirmed = LikelySourceConfirmed(g_selUi);
    if (state == L"likely" && !likelyConfirmed) {
        g_commentSourceError = L"Review the likely XAML source, then check 'Use this likely source for my comment'. Nothing was saved.";
        return false;
    }
    std::wstring outgoing = text;
    if (g_guestCommentWrite.failed && g_guestCommentWrite.id == id &&
        g_guestCommentWrite.wire == wire &&
        DevToolsCommentText::Unchanged(text, g_guestCommentWrite.submitted)) outgoing = g_guestCommentWrite.submitted;
    auto args = DevToolsCommentText::AddArguments(GetCurrentProcessId(), outgoing, id, std::to_wstring(wire));
    if (likelyConfirmed) args += L" --confirm-likely-source";
    GuestCommentWrite guest;
    guest.editor = GuestCommentEditor::Selection;
    guest.handle = handle; guest.root = g_pickRoot; guest.wire = wire;
    guest.generation = g_selGen;
    guest.id = id; guest.submitted = outgoing; guest.draft = text;
    guest.linked = IsLinkedElement(handle);
    guest.revision = g_selCommentRevision;
    if (g_anchorOf && handle) g_anchorOf(handle, &guest.anchor);
    guest.bounds = g_selBounds;
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

std::wstring DevToolsOverlay_AddComment(IInspectable* element, const std::wstring& text, const std::wstring& id,
    std::function<void(int)> completed)
{
    GuestCommentWrite guest;
    InstanceHandle raw = 0;
    if (!element || !g_pickDiag || FAILED(g_pickDiag->GetHandleFromIInspectable(element, &raw)) || !raw ||
        DevToolsCommentText::IsBlank(text)) {
        if (completed) completed(1);
        return id;
    }
    guest.editor = GuestCommentEditor::Window;
    guest.handle = raw; guest.root = g_pickRoot;
    guest.wire = g_wireOf ? g_wireOf(raw) : 0;
    guest.id = id.empty() ? GenCommentId() : id; guest.submitted = guest.draft = text;
    guest.linked = IsLinkedElement(raw);
    if (g_anchorOf) g_anchorOf(raw, &guest.anchor);
    TryElementRootBounds(g_pickDiag, g_pickRoot, raw, &guest.bounds);
    guest.completed = std::move(completed);
    const std::wstring used = guest.id;
    const auto args = DevToolsCommentText::AddArguments(GetCurrentProcessId(), text, guest.id, std::to_wstring(guest.wire));
    // A write refused before it reached the writer still owes its caller an answer.
    if (!LaunchWinapp(args, L"add", &guest) && guest.completed) guest.completed(1);
    return used;
}

// Keyed by the raw pick handle. A comment this panel just saved counts even before
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

// Every open comment on the element except the first, which SelLookupComment puts in the box.
static std::vector<std::wstring> SelOtherComments(InstanceHandle handle)
{
    std::vector<std::wstring> others;
    bool first = true;
    for (const auto& p : g_pins) {
        if (!handle || p.handle != handle) continue;
        if (!std::exchange(first, false)) others.push_back(p.text);
    }
    return others;
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

// Whether the save in flight belongs to the comment the flyout shows now.
static bool GuestCommentMatchesEditor(bool requireGeneration = true)
{
    const auto& write = g_guestCommentWrite;
    std::wstring anchor;
    if (!write.anchor.empty() && (!g_anchorOf || !g_anchorOf(write.handle, &anchor) || anchor != write.anchor)) return false;
    return write.authority == g_guestCommentAuthority.load() && write.root == g_pickRoot &&
        write.handle == g_selHandle &&
        ((write.editor != GuestCommentEditor::Selection && write.editor != GuestCommentEditor::Delete) ||
            (write.wire == g_selCommentWire &&
                (!write.wire || (g_wireOf && g_wireOf(write.handle) == write.wire)))) &&
        write.id == g_selCommentId &&
        (!requireGeneration || write.generation == g_selGen);
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
        !GuestCommentMatchesEditor(false)) return false;
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
    if (!GuestCommentMatchesEditor(false)) return;
    GuestCommentSetStatus(g_selUi, L"DevToolsGuestCommentStatus", g_guestCommentWrite.status);
}

static void GuestCommentRememberDraft()
{
    if (g_guestCommentWrite.discarded || !GuestCommentMatchesEditor(false) ||
        (!g_guestCommentWrite.process && !g_guestCommentWrite.failed)) return;
    std::wstring text;
    if (SelReadCommentText(&text)) g_guestCommentWrite.draft = text;
}

static void GuestCommentRestoreDraft()
{
    auto& write = g_guestCommentWrite;
    if (write.authority != g_guestCommentAuthority.load() || write.root != g_pickRoot ||
        write.handle != g_selHandle || (!write.process && !write.failed)) return;
    if ((write.editor == GuestCommentEditor::Selection || write.editor == GuestCommentEditor::Delete) &&
        (write.wire != g_selCommentWire || (write.wire && (!g_wireOf || g_wireOf(write.handle) != write.wire)))) return;
    std::wstring anchor;
    if (write.anchor.empty() || !g_anchorOf || !g_anchorOf(write.handle, &anchor) || anchor != write.anchor) return;
    g_selCommentId = write.id;
    g_selCommentRevision = write.revision;
    SetSelCommentText(write.draft);
    GuestCommentSetStatus(g_selUi, L"DevToolsGuestCommentStatus", write.status);
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

void DevToolsOverlay_NoticeCommentsStale()
{
    ShowCommentToast(L"Comments could not be refreshed. Run winapp devtools comments list to see them.", true);
}

static void GuestCommentComplete(DWORD exitCode, bool acknowledged, const wchar_t* stage)
{
    auto& write = g_guestCommentWrite;
    if (write.discarded) {
        write = {};
        return;
    }
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
    const bool newerDraft = write.editor == GuestCommentEditor::Selection &&
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
        else if (!GuestCommentMatchesEditor(false))
            ShowCommentToast(L"Comment not saved. Pick the element again to retry.", true);
    }
    if (write.editor == GuestCommentEditor::Window) {
        const bool ok = saved && !write.superseded;
        if (ok) {
            ShowCommentToast(CommentToastText(false, write.linked), false);
            if (!write.authority && !g_commentSnapshotAuthoritative && write.handle)
                UpsertPin(write.handle, write.id, write.submitted, write.bounds);
            else if (!write.authority)
                SetCommentsShown(true);
        }
        auto completed = std::move(write.completed);
        write = {};
        if (completed) completed(ok ? 0 : 1);
        return;
    }
    if (write.editor == GuestCommentEditor::WindowResolve) {
        auto completed = std::move(write.completed);
        if (write.authority == g_guestCommentAuthority.load() && write.root == g_pickRoot &&
            write.generation == g_selGen && completed)
            completed(acknowledged ? static_cast<int>(exitCode) : 1);
        return;
    }
    if (!GuestCommentMatchesEditor()) {
        // The panel has moved on (the next element was picked, or it closed), but this save still earns its marker.
        if (!write.authority && saved && !write.failed && !g_commentSnapshotAuthoritative) {
            if (write.editor == GuestCommentEditor::Delete) RemovePinById(write.id);
            else if (write.handle) UpsertPin(write.handle, write.id, write.submitted, write.bounds);
        }
        if (write.deleteRequested) {
            write.deleteRequested = false;
            write.failed = true;
            write.status = L"Delete was not confirmed because the editor target changed. Draft retained.";
        }
        if (!write.authority && GuestCommentMatchesEditor(false)) {
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
        SetSelectionOperationError(L"Comment", L"");
    }
    if (write.editor == GuestCommentEditor::Delete) {
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
        if (LaunchWinapp(next.args, next.what.c_str(), &next.write)) continue;
        if (next.write.editor != GuestCommentEditor::Window) ShowCommentToast(L"Comment not saved. Pick the element again to retry.", true);
        else if (next.write.completed) next.write.completed(1);
    }
}

static void CALLBACK GuestCommentTimerProc(HWND, UINT, UINT_PTR, DWORD)
{
    auto& write = g_guestCommentWrite;
    if (!write.process) return;
    const DWORD state = WaitForSingleObject(write.process, 0);
    if (state == WAIT_TIMEOUT && GetTickCount64() - write.began < 90'000) return;
    GuestCommentRememberDraft();
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
// Save, blur, and Enter share one CLI writer path.
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
    if (GuestCommentMatchesEditor(false) && g_guestCommentWrite.process &&
        DevToolsCommentText::Unchanged(text, g_guestCommentWrite.submitted)) {
        if (explicitSave) g_guestCommentWrite.closeOnSave = true;
        return;
    }
    if (QueuedCommentWrite* queued = SelQueuedCommentWrite(); queued &&
        DevToolsCommentText::Unchanged(text, queued->write.submitted)) {
        if (explicitSave) queued->write.closeOnSave = true;
        return;
    }
    if (!explicitSave && GuestCommentMatchesEditor(false) && g_guestCommentWrite.superseded) {
        g_selDismissCommitFailed = true;
        GuestCommentRememberDraft();
        GuestCommentRenderStatus();
        return;
    }
    if (!g_guestCommentAuthority.load() && GuestCommentMatchesEditor(false) &&
        !g_guestCommentWrite.process && g_guestCommentWrite.persisted && !g_guestCommentWrite.superseded)
        g_selCommentSaved = g_guestCommentWrite.editor == GuestCommentEditor::Delete ? L"" : g_guestCommentWrite.submitted;
    const bool retryUnconfirmed = !g_guestCommentAuthority.load() && GuestCommentMatchesEditor(false) &&
        !g_guestCommentWrite.process && g_guestCommentWrite.failed &&
        (!g_guestCommentWrite.persisted || g_guestCommentWrite.superseded);
    if (DevToolsCommentText::Unchanged(text, g_selCommentSaved) && !retryUnconfirmed) {
        if (!g_guestCommentAuthority.load() && GuestCommentMatchesEditor(false)) {
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
    if (explicitSave && g_guestCommentWrite.process && GuestCommentMatchesEditor(false))
        g_guestCommentWrite.closeOnSave = true;
    if (QueuedCommentWrite* queued = SelQueuedCommentWrite(); queued && g_commentSourceError.empty()) {
        if (explicitSave) queued->write.closeOnSave = true;
        SetSelectionOperationError(L"Comment", L"");
        GuestCommentSetStatus(g_selUi, L"DevToolsGuestCommentStatus", L"Saving...");
        return;
    }
    if (!g_commentSourceError.empty()) {
        SetSelectionOperationError(L"Comment", g_commentSourceError);
    } else if (!g_guestCommentAuthority.load() && GuestCommentMatchesEditor(false) &&
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
    if (GuestCommentMatchesEditor(false) && g_guestCommentWrite.process) return true;
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
        GuestCommentMatchesEditor(false)) {
        if (!GuestCommentMatchesEditor()) {
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
    if (!g_guestCommentAuthority.load() && GuestCommentMatchesEditor(false) &&
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
    DevToolsOverlayLog(L"overlay.sel comment Save clicked");
    DevToolsSelCommitComment(true);
}

static void OnSelCommentTextChanged(void*, void*)
{
    if (!g_selComment || g_selRootLost) return;
    GuestCommentRememberDraft();
    if (GuestCommentMatchesEditor(false) && (g_guestCommentWrite.process || g_guestCommentWrite.failed)) return;
    std::wstring text;
    if (!SelReadCommentText(&text)) {
        SetSelectionOperationError(L"Comment", L"Couldn't read your draft. Nothing was saved.");
        return;
    }
    DevToolsOverlayLog(L"overlay.sel comment text changed: %zu chars", text.size());
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
