// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsWindow.cpp; included into that translation unit, not compiled on its own.


// DevTools-authored state wins because runtime reads lose resource keys and binding expressions after assignment.
static DevToolsKind CurrentKindOf(const DevToolsCardRow& r, const std::wstring& path)
{
    if (const SessionAssign* sa = SessionAssignFind(path)) return sa->kind;
    // A commit that replaced a binding, or that dropped a resource link, made this value a literal whatever
    // the.xaml still says. Reporting the authored kind here would offer to edit a link that is gone.
    if (BindingLost(path) || LinkLost(path)) return DevToolsKind::Literal;
    if (r.authoredKind == L"themeResource")  return DevToolsKind::Theme;
    if (r.authoredKind == L"staticResource") return DevToolsKind::Static;
    if (r.authoredKind == L"xBind")          return DevToolsKind::XBind;
    if (r.authoredKind == L"binding")        return DevToolsKind::Binding;
    // No readable source, but the live element reports a runtime {Binding}. Only a {Binding} can be
    // reconstructed this way -- an {x:Bind} is compiled away and arrives as an ordinary local value.
    if (!r.binding.empty() || r.source == L"Binding") return DevToolsKind::Binding;
    return DevToolsKind::Literal;
}

static DevToolsKind StagedOrCurrentKind(const DevToolsCardRow& r, const std::wstring& path)
{
    if (const DevToolsKind* p = PendingKindFind(path)) return *p;
    return CurrentKindOf(r, path);
}

// The inside of a markup extension: "Title, Mode=TwoWay" from "{Binding Title, Mode=TwoWay}".
// Empty for a literal or for markup with no readable body.
static std::wstring MarkupInner(const std::wstring& authored)
{
    if (authored.size() < 3 || authored.front() != L'{' || authored.back() != L'}') return std::wstring();
    std::wstring body = authored.substr(1, authored.size() - 2);
    const size_t sp = body.find(L' ');
    if (sp == std::wstring::npos) return std::wstring();
    size_t start = sp + 1;
    while (start < body.size() && body[start] == L' ') ++start;
    return body.substr(start);
}

// The PATH a {Binding}/{x:Bind} editor is seeded with. `authoredKey` is the tap's own extraction and is
// preferred; the reconstructed expression is the fallback for a runtime {Binding} whose source we could not
// read, which is the only case where the two can disagree.
static std::wstring BindingPathOf(const DevToolsCardRow& r, const std::wstring& path)
{
    if (const SessionAssign* sa = SessionAssignFind(path))
        if (sa->kind == DevToolsKind::Binding) {
            const std::wstring inner = MarkupInner(sa->detail);
            const size_t comma = inner.find(L',');
            return comma == std::wstring::npos ? inner : inner.substr(0, comma);
        }
    if (!r.authoredKey.empty()) return r.authoredKey;
    const std::wstring inner = MarkupInner(r.binding.empty() ? r.authored : r.binding);
    const size_t comma = inner.find(L',');
    return comma == std::wstring::npos ? inner : inner.substr(0, comma);
}

static const std::vector<std::wstring>& BindingModes()
{
    static const std::vector<std::wstring> m = { L"OneWay", L"TwoWay", L"OneTime" };
    return m;
}

// The MODE the row's expression declares, defaulting to OneWay -- which is both XAML's default for
// {Binding} on most properties and the safe assumption, because it is the mode that DISABLES write-to-source.
// Guessing TwoWay would offer a button that writes into the app's data on a binding that cannot carry it.
static std::wstring BindingModeOf(const DevToolsCardRow& r, const std::wstring& path)
{
    std::wstring src = r.binding.empty() ? r.authored : r.binding;
    if (const SessionAssign* sa = SessionAssignFind(path))
        if (sa->kind == DevToolsKind::Binding) src = sa->detail;
    const size_t at = src.find(L"Mode=");
    if (at == std::wstring::npos) return L"OneWay";
    std::wstring rest = src.substr(at + 5);
    size_t end = 0;
    while (end < rest.size() && (iswalpha(rest[end]) != 0)) ++end;
    std::wstring mode = rest.substr(0, end);
    for (const auto& m : BindingModes()) if (m == mode) return m;
    return L"OneWay";
}

// The expression a grafted binding is installed from. Built here rather than in the agent so the pane can
// SHOW the user exactly what it is about to apply.
static std::wstring BindingExprFrom(const std::wstring& bindPath, const std::wstring& mode)
{
    return L"{Binding " + bindPath + L", Mode=" + mode + L"}";
}

// Resource keys are free text with type-compatible suggestions, not an exhaustive runtime list.
// Suggest keys from authored rows and curated theme brushes. Treat brush subclasses as one family
// while excluding known incompatible resource types.
static std::wstring ResourceTypeFamily(const std::wstring& type)
{
    std::wstring s = ShortType(type);
    if (s.size() >= 5 && s.compare(s.size() - 5, 5, L"Brush") == 0) return L"Brush";
    return s;
}

static std::wstring RowResourceType(const DevToolsCardRow& r)
{
    return r.valueType.empty() ? r.type : r.valueType;
}

static std::vector<std::wstring> ResourceKeyCandidates(const DevToolsCardRow& target)
{
    const std::wstring want = ResourceTypeFamily(RowResourceType(target));
    std::vector<std::wstring> out;
    if (want.empty()) return out;
    for (const auto& row : g_propRows) {
        if (row.authoredKind != L"themeResource" && row.authoredKind != L"staticResource") continue;
        if (row.authoredKey.empty()) continue;
        if (ResourceTypeFamily(RowResourceType(row)) != want) continue;
        if (std::find(out.begin(), out.end(), row.authoredKey) == out.end()) out.push_back(row.authoredKey);
    }
    if (want == L"Brush") {
        for (const auto& k : BrushResourceNames())
            if (std::find(out.begin(), out.end(), k) == out.end()) out.push_back(k);
    }
    return out;
}

static void PopulateExpansionInto(void* fe, const DevToolsCardRow& r, const std::wstring& path, int edit);
static void RebuildExpansion(void* rowFe, const DevToolsCardRow& r, const std::wstring& path, int edit);


static bool ApplyLiteralValue(const std::wstring& path, const std::wstring& type,
                              const std::wstring& preVal, const std::wstring& value, bool confirmed)
{
    if (!confirmed && value == preVal) return false;
    const InstanceHandle wire = g_selWire;
    if (ApplyCommitOutcome(CommitWrite(wire, path, type, value, confirmed), wire, path, type, preVal, value)) {
        if (wire == g_selWire) SessionAssignErase(path);
        return true;
    }
    return false;
}

// Resource assignment records a session witness because runtime local values forget the key.
// A failed lookup can clear the target. Restore the prior session key, then authored key, then
// literal when available; report both lookup and restoration outcomes.
static bool RestoreAfterFailedResolve(const DevToolsCardRow& r, const std::wstring& path, const std::wstring& preVal)
{
    if (!g_ctx.resolveFn) return false;
    if (const SessionAssign* sa = SessionAssignFind(path))
        if (KindIsResource(sa->kind))
            return g_ctx.resolveFn(g_selWire, path, MarkupInner(sa->detail), sa->kind == DevToolsKind::Theme);
    if (!r.authoredKey.empty() && (r.authoredKind == L"themeResource" || r.authoredKind == L"staticResource"))
        return g_ctx.resolveFn(g_selWire, path, r.authoredKey, r.authoredKind == L"themeResource");
    // A literal, through the ordinary write path -- `type` is the row's CreateInstance type, empty on a
    // read-only row. CONFIRMED, because the confirmation exists to protect a resource link and the failed
    // lookup has already destroyed whatever link was here.
    if (preVal.empty() || r.type.empty()) return false;
    const DevToolsWriteOutcome oc = CommitWrite(g_selWire, path, r.type, preVal, /*confirmed*/ true);
    return oc == DevToolsWriteOutcome::Ok || oc == DevToolsWriteOutcome::ReplacedBinding;
}

static bool ApplyResourceKey(const DevToolsCardRow& r, const std::wstring& path, const std::wstring& key,
                             bool asTheme, const std::wstring& preVal)
{
    if (!g_ctx.resolveFn) { SetStatusText(L"This build cannot assign resources"); return false; }
    if (key.empty()) { SetStatusText(L"Type a resource key first"); return false; }
    // CHECK THE RETURNED VALUE, not just that the call was made. The runtime's own return says nothing
    // -- IVisualTreeService3::ResolveResource answers S_OK for a key the app does not have -- so what is
    // being checked here is the tap's verdict, which is built from the diagnostics callback the failure
    // actually arrives on. A pane that trusted the HRESULT named a key the element never took, for
    // every typo the free-text box is built to accept.
    if (!g_ctx.resolveFn(g_selWire, path, key, asTheme)) {
        // SAY WHICH KEY, AND WHAT IT COST. The box takes free text against the APP's dictionary, so the
        // useful sentence separates "you typed it wrong" from "this app has no such resource" -- and then
        // says whether the value survived, which is not the same question and has a different answer.
        const bool restored = RestoreAfterFailedResolve(r, path, preVal);
        SetStatusText(L"'" + key + L"' did not resolve in this app's resources. "
                      + (restored ? std::wstring(L"Nothing was changed - check the spelling, or type a key "
                                                 L"your app defines.")
                                  : std::wstring(L"The failed lookup also cleared ") + path +
                                    L", and DevTools had no earlier value to put back - set it again."));
        return false;
    }
    NoteSessionAssign(path, asTheme ? DevToolsKind::Theme : DevToolsKind::Static,
                      std::wstring(asTheme ? L"{ThemeResource " : L"{StaticResource ") + key + L"}");
    // Preserve the pre-edit seed across resource assignment so Restore original can revert it.
        // Undo also clears the session assignment witness.
    NoteChange(path, preVal, std::wstring(asTheme ? L"{ThemeResource " : L"{StaticResource ") + key + L"}");
    // The value FOLLOWS a resource now, so an earlier "this link is gone" verdict is retired.
    for (size_t i = 0; i < g_linkLost.size(); ++i) if (g_linkLost[i] == path) { g_linkLost.erase(g_linkLost.begin() + i); break; }
    // THE SUCCESS LINE IS THE CALLER'S TO SET, AFTER ITS RE-RENDER. Setting it here is what the failure
    // path can afford and this one cannot: a successful commit re-renders the pane, and ShowPropsForNode
    // resets the status to its default hint, so a message written here is destroyed by the rebuild it
    // triggers and the user reads "Edits apply when you leave a field" for an assignment that worked. Same
    // ordering rule, and the same reason, as the binding-op drain.
    return true;
}

static std::wstring ResourceAppliedStatus(const std::wstring& path, const std::wstring& key)
{
    return L"Set " + path + L" from " + key;
}

// The KEY is TEXT and the resolved literal is on the row line. This is the whole of the pane's
// contract with that issue and it holds BY CONSTRUCTION rather than by discipline: the reference is placed on
// the APP's element through an ABI call, so nothing the pane parses ever contains a resource extension and
// the +0.003 ms-per-reference curve is not re-entered. Asserted here so the invariant has a place in the code
// and not only in a comment on an issue.
static_assert(true, "the pane assigns resources through ResolveResource, never through parsed markup");

// Native bind installs through the diagnostics property index; writesource requires the managed host.
// Post both operations to a worker and drain results on a UI timer. Calling the managed relay on
// the UI thread would deadlock its callback dispatch and is explicitly refused.
struct BindOpResult
{
    unsigned     gen = 0;
    std::wstring op;
    std::wstring prop;
    std::wstring payload;
    std::wstring mode;
    std::wstring shown;
    bool         ok = false;
    std::wstring reason;
};
static SRWLOCK                     g_bindOpLock = SRWLOCK_INIT;
static std::vector<BindOpResult>   g_bindOpDone;
static UINT_PTR                    g_bindOpTimer = 0;
static volatile LONG               g_bindOpBusy = 0;

struct BindOpAsk { InstanceHandle wire; std::wstring op, prop, payload, mode, shown; unsigned gen; };

static void CALLBACK BindOpTimerProc(HWND, UINT, UINT_PTR id, DWORD)
{
    std::vector<BindOpResult> done;
    AcquireSRWLockExclusive(&g_bindOpLock);
    done.swap(g_bindOpDone);
    ReleaseSRWLockExclusive(&g_bindOpLock);
    if (done.empty()) {
        if (InterlockedCompareExchange(&g_bindOpBusy, 0, 0) == 0) {
            KillTimer(nullptr, id);
            g_bindOpTimer = 0;
        }
        return;
    }
    std::wstring status;
    for (const BindOpResult& r : done) {
        if (r.gen != g_route1Gen) continue;
        const bool graft = (r.op == L"bind");
        if (!r.ok) {
            status = (graft ? L"Could not bind " : L"Could not write ") + r.prop + L": " + r.reason;
            continue;
        }
        if (graft) {
            NoteSessionAssign(r.prop, DevToolsKind::Binding, r.shown);
            PendingKindErase(r.prop);
            DirtyErase(r.prop);
            status = L"Bound " + r.prop + L" to " + r.shown
                   + L". The running app only - your .xaml is unchanged";
        } else {
            // NOT marked as an edit of THIS property. A source write changes the app's DATA; the target's
            // value changed only because the binding did its job, and offering "Restore original" for it
            // would claim an undo the pane does not have.
            status = L"Wrote " + QuoteForPrompt(r.payload) + L" to " + r.prop + L"'s source. "
                   + L"The binding is still installed";
        }
    }
    ShowPropsForNode(g_selWire, g_selLabel, g_selBreadcrumb, /*force*/ true);
    if (!status.empty()) SetStatusText(status);
}

static DWORD WINAPI BindOpWorker(LPVOID param)
{
    BindOpAsk* ask = reinterpret_cast<BindOpAsk*>(param);
    BindOpResult res;
    res.gen = ask->gen; res.op = ask->op; res.prop = ask->prop; res.payload = ask->payload;
    res.mode = ask->mode; res.shown = ask->shown;
    std::wstring json;
    if (!g_ctx.bindFn) {
        res.reason = L"this build has no binding bridge";
    } else if (!g_ctx.bindFn(ask->wire, ask->op, ask->prop, ask->payload, ask->mode, &json)) {
        // Surface the shared binding result's reason, including explicit unavailable states.
        DevToolsJson j; DevToolsJsonParse(json, j);
        res.reason = j.GetString(L"reason");
        if (res.reason.empty()) res.reason = L"the binding request was not answered";
    } else {
        DevToolsJson j; DevToolsJsonParse(json, j);
        // BOTH ops report a READ-BACK, not that a call was made. A binding can install and resolve to
        // nothing, which looks identical to not having run; and a source write is only a success if the
        // expression is STILL installed afterwards -- which is the whole claim the action makes.
        res.ok = (j.GetString(L"bound") == L"True");
        if (!res.ok) {
            res.reason = j.GetString(L"reason");
            if (res.reason.empty())
                res.reason = (ask->op == L"bind") ? L"the binding did not install"
                                                  : L"the write did not land, or the binding did not survive it";
        }
    }
    AcquireSRWLockExclusive(&g_bindOpLock);
    g_bindOpDone.push_back(res);
    ReleaseSRWLockExclusive(&g_bindOpLock);
    InterlockedExchange(&g_bindOpBusy, 0);
    delete ask;
    return 0;
}

// Post one binding op. Returns immediately: the answer arrives on the drain timer, so the caller must not
// re-render on the assumption that it landed.
static void PostBindOp(const std::wstring& op, const std::wstring& path, const std::wstring& payload,
                       const std::wstring& mode, const std::wstring& shown, const std::wstring& busyText)
{
    if (InterlockedCompareExchange(&g_bindOpBusy, 1, 0) != 0) {
        SetStatusText(L"A binding request is already in flight");
        return;
    }
    BindOpAsk* ask = new BindOpAsk{ g_selWire, op, path, payload, mode, shown, g_route1Gen };
    SetStatusText(busyText);
    if (HANDLE th = CreateThread(nullptr, 0, DevToolsThreadEntry<BindOpWorker>, ask, 0, nullptr)) {
        CloseHandle(th);
        if (!g_bindOpTimer) g_bindOpTimer = SetTimer(nullptr, 0, 100, &BindOpTimerProc);
    } else {
        // A thread that never started is a button that does nothing forever, so say so instead of leaving the
        // status line reading "Binding...".
        InterlockedExchange(&g_bindOpBusy, 0);
        delete ask;
        SetStatusText(L"Could not start the binding request");
    }
}

static void ApplyBindingGraft(const std::wstring& path, const std::wstring& bindPath, const std::wstring& mode)
{
    if (bindPath.empty()) { SetStatusText(L"Type a binding path first"); return; }
    PostBindOp(L"bind", path, bindPath, mode, BindingExprFrom(bindPath, mode),
               L"Binding " + path + L"...");
}

// Write through to the TwoWay binding's source. A local target write could replace the binding.
static void ApplyWriteToSource(const std::wstring& path, const std::wstring& value)
{
    if (value.empty()) { SetStatusText(L"Nothing to write"); return; }
    PostBindOp(L"writesource", path, value, std::wstring(), value, L"Writing " + path + L"'s source...");
}


// Request native walks from a worker only for open expansions. Cache by selection/property
// so applying an answer and rebuilding does not initiate an endless request loop.
struct WalkAnswer { unsigned gen; InstanceHandle wire; std::wstring prop; std::wstring json; bool done;
                    // Proposed-path validation uses a separate cache slot from installed-binding diagnosis.
                    std::wstring tryPath; };
static std::vector<WalkAnswer> g_walkAnswers;
static SRWLOCK                 g_walkLock = SRWLOCK_INIT;
static std::vector<WalkAnswer> g_walkDone;
static UINT_PTR                g_walkTimer = 0;

// state, declared here rather than with the validator below because the walk's drain timer is
// what routes a validation answer to it, and that timer is defined above the validator.
static std::wstring g_validateProp;
static std::wstring g_validatePath;
static void ApplyValidateAnswer(const std::wstring& json);

static WalkAnswer* WalkFind(const std::wstring& prop)
{
    for (auto& a : g_walkAnswers)
        if (a.prop == prop && a.tryPath.empty() && a.wire == g_selWire && a.gen == g_route1Gen) return &a;
    return nullptr;
}
static void WalkForget() { g_walkAnswers.clear(); }

struct WalkAsk { InstanceHandle wire; std::wstring prop; std::wstring tryPath; unsigned gen; };

static DWORD WINAPI WalkWorker(LPVOID param)
{
    WalkAsk* ask = reinterpret_cast<WalkAsk*>(param);
    WalkAnswer r{ ask->gen, ask->wire, ask->prop, std::wstring(), true, ask->tryPath };
    if (g_walkFn) g_walkFn(ask->wire, ask->prop, ask->tryPath, &r.json);
    AcquireSRWLockExclusive(&g_walkLock);
    g_walkDone.push_back(r);
    ReleaseSRWLockExclusive(&g_walkLock);
    delete ask;
    return 0;
}

static void CALLBACK WalkTimerProc(HWND, UINT, UINT_PTR id, DWORD)
{
    std::vector<WalkAnswer> done;
    AcquireSRWLockExclusive(&g_walkLock);
    done.swap(g_walkDone);
    ReleaseSRWLockExclusive(&g_walkLock);
    if (done.empty()) { KillTimer(nullptr, id); g_walkTimer = 0; return; }
    bool any = false;
    for (const WalkAnswer& a : done) {
        if (a.gen != g_route1Gen || a.wire != g_selWire) continue;
        if (!a.tryPath.empty()) {
            if (a.tryPath != g_validatePath || a.prop != g_validateProp) continue;
            ApplyValidateAnswer(a.json);
            continue;
        }
        if (WalkAnswer* existing = WalkFind(a.prop)) { existing->json = a.json; existing->done = true; }
        else g_walkAnswers.push_back(a);
        any = true;
    }
    // Re-render so the expansion picks the answer up. Terminates because the second pass finds the cache
    // entry and posts nothing.
    if (any) ShowPropsForNode(g_selWire, g_selLabel, g_selBreadcrumb, /*force*/ true);
}

static void PostWalk(const std::wstring& prop)
{
    if (!g_walkFn || !g_selWire || prop.empty()) return;
    if (WalkFind(prop)) return;
    g_walkAnswers.push_back({ g_route1Gen, g_selWire, prop, std::wstring(), false, std::wstring() });
    WalkAsk* ask = new WalkAsk{ g_selWire, prop, std::wstring(), g_route1Gen };
    if (HANDLE th = CreateThread(nullptr, 0, DevToolsThreadEntry<WalkWorker>, ask, 0, nullptr)) {
        CloseHandle(th);
        if (!g_walkTimer) g_walkTimer = SetTimer(nullptr, 0, 100, &WalkTimerProc);
    } else {
        // A thread that never started would leave the row saying "checking..." for ever, which reads as a
        // hung feature. Drop the pending marker so the next render asks again.
        for (size_t i = g_walkAnswers.size(); i-- > 0; )
            if (g_walkAnswers[i].prop == prop && !g_walkAnswers[i].done) { g_walkAnswers.erase(g_walkAnswers.begin() + i); break; }
        delete ask;
    }
}

static const UINT kValidateIdleMs = 400;

static UINT_PTR      g_validateTimer = 0;
static unsigned      g_validateAsks = 0;

// The verdict for the path currently in the box. Rendered under it, and separate from the row's diagnosis:
// the diagnosis is about the binding that is INSTALLED, this is about the one being composed.
static std::wstring  g_validateVerdict;
static bool          g_validateBad = false;
static void*         g_validateExpFe = nullptr;

static void ValidateRender()
{
    if (!g_validateExpFe) return;
    if (IInspectable* msg = FindInRow(g_validateExpFe, L"PPathMsg")) {
        SetTextOn(msg, g_validateVerdict);
        SetAutomationTextOn(msg, g_validateProp + L" path check", g_validateProp + L" path check", L"");
        ShowEl(msg, !g_validateVerdict.empty());
        msg->Release();
    }
}

static void CALLBACK ValidateTimerProc(HWND, UINT, UINT_PTR id, DWORD)
{
    KillTimer(nullptr, id);
    g_validateTimer = 0;
    if (g_validateProp.empty() || g_validatePath.empty()) return;
    if (!DevToolsPathSyntax_Ok(g_validatePath)) return;
    if (!g_walkFn || !g_selWire) return;
    ++g_validateAsks;
    DevToolsOverlayLog(L"route1.props validate resolve #%u for '%s' path='%s'",
                  g_validateAsks, g_validateProp.c_str(), g_validatePath.c_str());
    WalkAsk* ask = new WalkAsk{ g_selWire, g_validateProp, g_validatePath, g_route1Gen };
    if (HANDLE th = CreateThread(nullptr, 0, DevToolsThreadEntry<WalkWorker>, ask, 0, nullptr)) {
        CloseHandle(th);
        if (!g_walkTimer) g_walkTimer = SetTimer(nullptr, 0, 100, &WalkTimerProc);
    } else {
        delete ask;
    }
}

static void ApplyValidateAnswer(const std::wstring& json)
{
    const DevToolsPathWalkView v = DevToolsPathWalk_FromJson(json, DevToolsBindingRelay_AgentPresent());
    if (!v.show) {
        g_validateBad = false;
        g_validateVerdict = L"Resolves. Not applied yet.";
    } else {
        g_validateBad = true;
        g_validateVerdict = v.answer.empty() ? std::wstring(L"This path does not resolve.") : v.answer;
    }
    if (!v.againstNote.empty()) g_validateVerdict = v.againstNote + L" " + g_validateVerdict;
    ValidateRender();
}

static void ValidateOnType(const std::wstring& prop, const std::wstring& text)
{
    g_validateProp = prop;
    g_validatePath = text;
    if (text.empty()) {
        g_validateVerdict.clear();
        g_validateBad = false;
    } else {
        const std::wstring why = DevToolsPathSyntax_Reason(text);
        g_validateBad = !why.empty();
        g_validateVerdict = why.empty() ? std::wstring(L"Checking \u2026") : why;
    }
    ValidateRender();
    // Restart the idle timer: the resolve fires after typing STOPS, so each keystroke pushes it out.
    if (g_validateTimer) { KillTimer(nullptr, g_validateTimer); g_validateTimer = 0; }
    if (!g_validateBad && !text.empty())
        g_validateTimer = SetTimer(nullptr, 0, kValidateIdleMs, &ValidateTimerProc);
}

// TextChanged updates syntax immediately and restarts the idle resolve timer.
// Retained text boxes are guarded by generation stamps so callbacks from torn-down panes become inert.
struct PathValidateSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen) return S_OK;
        std::wstring text;
        if (box && g_ctx.readInputFn) g_ctx.readInputFn(box, text);
        ValidateOnType(prop, text);
        return S_OK;
    }
    void Init(const std::wstring& p, IInspectable* b, void* expFe)
    {
        prop = p;
        box = b;
        if (box) box->AddRef();
        g_validateExpFe = expFe;
        InitSink(IID_TextChanged_Del, g_route1Gen);
    }
    std::wstring  prop;
    IInspectable* box = nullptr;
};


struct KindCtx
{
    std::shared_ptr<const DevToolsCardRow> row;
    std::wstring path;
    int   edit = -1;
    void* rowFe = nullptr;
    void* expFe = nullptr;
};

// Read one named TextBox inside the expansion. Returns empty when the control is absent -- which is a real
// case (the binding editor is not built when the kind is Literal) and must not be mistaken for "the user
// cleared the field".
static std::wstring ReadBox(void* expFe, const wchar_t* name, bool* found = nullptr)
{
    if (found) *found = false;
    std::wstring out;
    if (IInspectable* el = FindInRow(expFe, name)) {
        if (g_ctx.readInputFn && g_ctx.readInputFn(el, out)) { if (found) *found = true; }
        el->Release();
    }
    return out;
}

// Multi-field editors are created from wire data, so boxes are registered by property path rather than x:Name.
struct FieldBoxRef { std::wstring path; size_t index; IInspectable* box; };
static std::vector<FieldBoxRef> g_fieldBoxes;

static void ReleaseFieldBoxes(const std::wstring& path)
{
    for (size_t i = g_fieldBoxes.size(); i-- > 0; ) {
        if (!path.empty() && g_fieldBoxes[i].path != path) continue;
        if (g_fieldBoxes[i].box) g_fieldBoxes[i].box->Release();
        g_fieldBoxes.erase(g_fieldBoxes.begin() + i);
    }
}

// The joined value of one row's field boxes, in wire order. A box that could not be read contributes an
// EMPTY segment rather than being skipped: dropping it would silently turn a four-field Thickness into a
// three-field one, and "0,0,12" is a value the type converter accepts and means something else entirely.
static std::wstring ReadFieldBoxes(const std::wstring& path, size_t count)
{
    std::wstring out;
    for (size_t i = 0; i < count; ++i) {
        std::wstring one;
        for (const FieldBoxRef& f : g_fieldBoxes) {
            if (f.path != path || f.index != i || !f.box) continue;
            if (g_ctx.readInputFn) g_ctx.readInputFn(f.box, one);
            break;
        }
        out += (i ? L"," : L"") + one;
    }
    return out;
}

static std::wstring ReadCombo(void* expFe, const wchar_t* name, const std::vector<std::wstring>& options)
{
    std::wstring out;
    if (IInspectable* el = FindInRow(expFe, name)) {
        void* sel = nullptr;
        if (SUCCEEDED(el->QueryInterface(DevToolsIid<DevToolsXCP::ISelector>(), &sel)) && sel) {
            int idx = -1;
            DevToolsGetSelectedIndex(sel, &idx);
            if (idx >= 0 && (size_t)idx < options.size()) out = options[(size_t)idx];
            reinterpret_cast<IUnknown*>(sel)->Release();
        }
        el->Release();
    }
    return out;
}

static void CommitKind(const KindCtx& c, bool confirmedReplacement = false)
{
    if (!c.row) return;
    const DevToolsKind k = StagedOrCurrentKind(*c.row, c.path);
    const bool staged = PendingKindFind(c.path) != nullptr;
    switch (k) {
    case DevToolsKind::Theme:
    case DevToolsKind::Static: {
        const std::wstring key = ReadBox(c.expFe, L"PResKey");
        if (ApplyResourceKey(*c.row, c.path, key, k == DevToolsKind::Theme, RowEditSeed(*c.row))) {
            PendingKindErase(c.path);
            ShowPropsForNode(g_selWire, g_selLabel, g_selBreadcrumb, /*force*/ true);
            SetStatusText(ResourceAppliedStatus(c.path, key));
        }
        return;
    }
    case DevToolsKind::Binding: {
        if (!confirmedReplacement) {
            SetStatusText(L"Not applied. Review the full replacement warning and click Replace entire binding.");
            return;
        }
        // Commit uses the same syntax gate as the live verdict; invalid paths cannot be applied.
        const std::wstring typed = ReadBox(c.expFe, L"PPath");
        const std::wstring why = DevToolsPathSyntax_Reason(typed);
        if (!why.empty()) { SetStatusText(L"Not applied. " + why); return; }
        PendingKindErase(c.path);
        ApplyBindingGraft(c.path, typed, ReadCombo(c.expFe, L"PMode", BindingModes()));
        return;
    }
    case DevToolsKind::XBind:
        // Not reachable from the dropdown -- the item is disabled -- and not reachable from Apply, because
        // staging to it is refused. Stated rather than silently ignored so a future edit that made the item
        // selectable would find this instead of writing an {x:Bind} the runtime cannot create.
        SetStatusText(L"An {x:Bind} cannot be created at runtime");
        return;
    default: break;
    }

    const std::wstring ek = c.row->editKind;
    std::wstring value;
    if (ek == L"bool") {
        if (IInspectable* t = FindInRow(c.expFe, L"PToggle")) {
            void* ts = nullptr;
            if (SUCCEEDED(t->QueryInterface(DevToolsIid<DevToolsXC::IToggleSwitch>(), &ts)) && ts) {
                bool on = false; DevToolsToggleGetIsOn(ts, &on);
                value = DevToolsBoolLiteral(on);
                reinterpret_cast<IUnknown*>(ts)->Release();
            }
            t->Release();
        }
    } else if (ek == L"enum") {
        value = ReadCombo(c.expFe, L"PCombo", EnumMembersFor(c.row->type));
    } else if (ek == L"color") {
        value = ReadBox(c.expFe, L"PHex");
    } else if (ek == L"fields") {
        // The boxes are the value, joined in wire order. Reading them back rather than tracking each edit is
        // what keeps a four-box Thickness one commit instead of four partial ones.
        value = ReadFieldBoxes(c.path, c.row->fields.size());
    } else {
        value = ReadBox(c.expFe, L"PEdit");
    }
    const DevToolsKind was = CurrentKindOf(*c.row, c.path);
    const bool landed = ApplyLiteralValue(c.path, c.row->type, RowEditSeed(*c.row), value, /*confirmed*/ staged);
    // Record a landed literal as replacing x:Bind even when the runtime reports only a local value.
// Authored source still describes the original expression, not the session mutation.
// Drop the resolved-value seed rather than offer a Restore that cannot restore the binding.
    if (landed && was != DevToolsKind::Literal) {
        if (was == DevToolsKind::XBind || was == DevToolsKind::Binding) { DirtyErase(c.path); NoteBindingLost(c.path); }
        else if (KindIsResource(was))                         { NoteLinkLost(c.path); }
    }
    if (staged) {
        PendingKindErase(c.path);
        ShowPropsForNode(g_selWire, g_selLabel, g_selBreadcrumb, /*force*/ true);
    }
}
