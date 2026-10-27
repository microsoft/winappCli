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

static const DevToolsCardRow* FindPropertyRowIn(const std::vector<DevToolsCardRow>& rows, const std::wstring& path)
{
    return FindPropertyRow(const_cast<std::vector<DevToolsCardRow>&>(rows), path);
}

static std::wstring CurrentEditSeed(const std::wstring& prop)
{
    const DevToolsCardRow* row = FindPropertyRowIn(g_propRows, prop);
    return row ? RowEditSeed(*row) : std::wstring();
}

// The top-level row a path belongs to: `Padding.Left` -> `Padding`, while an attached property such as
// `AutomationProperties.Name` is its own top-level row.
static std::wstring OwningRowName(const std::vector<DevToolsCardRow>& rows, const std::wstring& prop)
{
    std::wstring best;
    for (const auto& r : rows) {
        if (r.name == prop) return prop;
        if (prop.size() > r.name.size() && prop.compare(0, r.name.size(), r.name) == 0 &&
            prop[r.name.size()] == L'.' && r.name.size() > best.size())
            best = r.name;
    }
    return best.empty() ? prop.substr(0, prop.find(L'.')) : best;
}

// Whether the property's current value is set on the element. A style, default or inherited source wins over
// authored XAML, because the app can clear an authored value. A field such as Padding.Left can only be written
// back, so it always counts as set.
static bool SetHereIn(const std::vector<DevToolsCardRow>& rows, const std::wstring& prop)
{
    for (const auto& r : rows)
        if (r.name == prop)
            return r.source.empty() || SourceIsSetHere(r.source) ||
                   (!SourceIsFallback(r.source) && !r.authoredKind.empty());
    return true;
}

// What Restore should put back for the selection's first edit of `prop`, read before the edit lands.
// False when there is nothing honest to restore (a binding cannot be recreated from its resolved value).
static bool OriginalFor(const std::wstring& prop, const std::wstring& preVal, EditRecord& out)
{
    const SessionAssign* assigned = SessionAssignFind(prop);
    if (assigned && assigned->kind == DevToolsKind::Binding) return false;
    if (OriginalFromAppChange(g_selWire, prop, out)) return true;
    out = EditRecord{};
    out.wire = g_selWire;
    out.prop = prop;
    out.original = preVal;
    if (const SessionAssign* sa = SessionAssignFind(prop)) {
        if (sa->kind == DevToolsKind::Binding) return false;
        if (KindIsResource(sa->kind) && !MarkupInner(sa->detail).empty()) {
            out.kind = DevToolsOriginal::Resource;
            out.key = MarkupInner(sa->detail);
            out.theme = sa->kind == DevToolsKind::Theme;
            out.original = sa->detail;
            return true;
        }
    }
    const DevToolsCardRow* row = FindPropertyRowIn(g_propRows, prop);
    if (row && ResourceBacked(prop) && !row->authoredKey.empty() &&
        (row->authoredKind == L"themeResource" || row->authoredKind == L"staticResource")) {
        out.kind = DevToolsOriginal::Resource;
        out.key = row->authoredKey;
        out.theme = row->authoredKind == L"themeResource";
        out.original = std::wstring(out.theme ? L"{ThemeResource " : L"{StaticResource ") + out.key + L"}";
        return true;
    }
    // Only a whole property can be cleared; a field such as Padding.Left can only be written back.
    if (row && !SetHereIn(g_propRows, prop))
        out.kind = DevToolsOriginal::Unset;
    return true;
}

// The element's ActualTheme, or -1 when it can't be read (DevToolsWindow.LiveProps.inc).
static int ElementThemeOf(InstanceHandle wire);

// Whether a theme switch can change the property's value without the app touching it: a value from a style,
// default or parent, or a {ThemeResource} from the XAML or from DevTools. A literal stays the same in every theme.
static bool ValueFollowsTheme(const EditRecord& rec, const std::vector<DevToolsCardRow>& rows)
{
    const std::wstring owner = OwningRowName(rows, rec.prop);
    if (!SetHereIn(rows, owner)) return true;
    if (const DevToolsCardRow* row = FindPropertyRowIn(rows, owner))
        if (row->authoredKind == L"themeResource") return true;
    if (rec.themeAssigned) return true;
    const EditRecord* ownerRec = owner == rec.prop ? nullptr : RecordFind(rec.wire, owner);
    return ownerRec && ownerRec->themeAssigned;
}

// Compare the element's freshly read rows with what DevTools last wrote. A difference means the app changed the
// value since, so Restore would undo the app's change rather than the user's edit: drop the record and say why.
// A theme switch also changes values that follow the theme, so a different `theme` takes a new baseline for those.
static void ReconcileEditRecordsAt(InstanceHandle wire, const std::vector<DevToolsCardRow>& rows, int theme)
{
    for (size_t i = g_editRecords.size(); i-- > 0; ) {
        EditRecord& rec = g_editRecords[i];
        if (rec.wire != wire) continue;
        const DevToolsCardRow* row = FindPropertyRowIn(rows, rec.prop);
        if (!row) continue;
        const std::wstring now = RowEditSeed(*row);
        const bool themeSwitched = theme >= 0 && rec.baselineTheme >= 0 && theme != rec.baselineTheme;
        if (rec.needsBaseline || (themeSwitched && ValueFollowsTheme(rec, rows))) {
            rec.baseline = now; rec.baselineTheme = theme; rec.needsBaseline = false;
            continue;
        }
        if (now == rec.baseline) {
            if (rec.baselineTheme != theme && theme >= 0) rec.baselineTheme = theme;
            continue;
        }
        const std::wstring prop = rec.prop;
        g_editRecords.erase(g_editRecords.begin() + i);
        AppChangedErase(wire, prop);
        g_appChanged.push_back({ wire, prop, now, SetHereIn(rows, prop) });
        if (wire == g_selWire) UpdateEditedUi(prop);
    }
    // The app can keep changing a property after its record was dropped; a later edit must restore the latest value.
    for (auto& app : g_appChanged) {
        if (app.wire != wire) continue;
        if (const DevToolsCardRow* row = FindPropertyRowIn(rows, app.prop)) {
            app.value = RowEditSeed(*row);
            app.setHere = SetHereIn(rows, app.prop);
        }
    }
}

static void ReconcileEditRecords(InstanceHandle wire, const std::vector<DevToolsCardRow>& rows)
{
    ReconcileEditRecordsAt(wire, rows, ElementThemeOf(wire));
}

static void ClearRowValue(InstanceHandle wire, const std::wstring& prop)
{
    FlushFocusedEditor();
    if (wire != g_selWire) return;
    if (BindingLost(prop)) {
        SetStatusText(L"Clearing " + prop + L" can't bring its binding back. " +
                      (g_ctx.bindFn ? std::wstring(L"Use Restore binding instead")
                                    : std::wstring(L"Restart the app")));
        return;
    }
    CheckEditRecordsNow(wire);
    EditRecord first;
    const bool recordable = !DirtyHas(prop) && OriginalFor(prop, CurrentEditSeed(prop), first);
    const bool ok = g_ctx.clearFn(wire, prop);
    if (ok) {
        // Clear is an edit too: Restore still puts back what the property was before the first one.
        if (EditRecord* rec = DirtyFind(prop)) {
            if (rec->kind == DevToolsOriginal::Unset) DirtyErase(prop);
            else rec->themeAssigned = false;
        } else if (recordable && first.kind != DevToolsOriginal::Unset) {
            g_editRecords.push_back(std::move(first));
        }
        SessionAssignErase(prop);
        AppChangedErase(wire, prop);
        RebaselineRecords(wire);
    }
    ShowPropsForNode(g_selWire, g_selLabel, g_selBreadcrumb, /*force*/ true);
    SetStatusText(ok ? (L"Cleared " + prop + L". It now falls back to its default")
                     : (L"Couldn't clear " + prop));
}

// A failed resource lookup can clear the property. Put back what it held just before: the resource DevTools
// assigned this session, or the literal that replaced the original link.
static bool PutBackAfterFailedRestore(InstanceHandle wire, const std::wstring& prop, const std::wstring& type,
                                      const std::wstring& before)
{
    if (const SessionAssign* sa = SessionAssignFind(prop))
        if (KindIsResource(sa->kind))
            return g_ctx.resolveFn(wire, prop, MarkupInner(sa->detail), sa->kind == DevToolsKind::Theme);
    if (type.empty() || (before.empty() && ShortType(type) != L"String")) return false;
    return DevToolsWriteLanded(CommitWrite(wire, prop, type, before, /*confirmed*/ true));
}

static void RestoreRowValue(InstanceHandle wire, const std::wstring& prop, const std::wstring& type)
{
    FlushFocusedEditor();
    if (wire != g_selWire) return;
    if (BindingLost(prop)) {
        SetStatusText(L"Can't restore " + prop + L". Its binding was replaced. " +
                      (g_ctx.bindFn ? std::wstring(L"Use Restore binding instead")
                                    : std::wstring(L"Restart the app to get it back")));
        return;
    }
    CheckEditRecordsNow(wire);
    const EditRecord* found = DirtyFind(prop);
    if (!found) {
        if (AppChanged(wire, prop))
            SetStatusText(L"The app changed " + prop + L" since your edit, so there's nothing to restore");
        return;
    }
    const EditRecord rec = *found;
    bool ok = false;
    std::wstring done, failed = L"Couldn't restore " + prop;
    switch (rec.kind) {
    case DevToolsOriginal::Value:
        // A value record on a resource-backed row means DevTools' literal already replaced the resource link,
        // even if leaving and coming back to the element forgot that; don't ask to replace it again.
        if (ResourceBacked(prop)) NoteLinkLost(prop);
        if (g_ctx.writeFn) ok = DevToolsWriteLanded(CommitWrite(wire, prop, type, rec.original, /*confirmed*/ false));
        if (ok) SessionAssignErase(prop);
        done = L"Restored " + prop + L" to the value it had before your change";
        break;
    case DevToolsOriginal::Unset:
        ok = g_ctx.clearFn && g_ctx.clearFn(wire, prop);
        if (ok) SessionAssignErase(prop);
        done = L"Restored " + prop + L". It's no longer set here, so it uses its style or default value";
        break;
    case DevToolsOriginal::Resource: {
        if (!g_ctx.resolveFn) break;
        const std::wstring before = CurrentEditSeed(prop);
        ok = g_ctx.resolveFn(wire, prop, rec.key, rec.theme);
        if (ok) {
            for (size_t i = 0; i < g_linkLost.size(); ++i)
                if (g_linkLost[i] == prop) { g_linkLost.erase(g_linkLost.begin() + i); break; }
            const DevToolsCardRow* row = FindPropertyRowIn(g_propRows, prop);
            const bool authored = row && row->authoredKey == rec.key &&
                                  row->authoredKind == (rec.theme ? L"themeResource" : L"staticResource");
            if (authored) SessionAssignErase(prop);
            else NoteSessionAssign(prop, rec.theme ? DevToolsKind::Theme : DevToolsKind::Static, rec.original);
        } else {
            // DevTools changed the value, not the app, so the record keeps its original for another try.
            RebaselineRecords(wire);
            const bool kept = PutBackAfterFailedRestore(wire, prop, type, before);
            RebaselineRecords(wire);
            failed = L"Couldn't restore " + prop + L": '" + rec.key + L"' isn't in the app's resources. " +
                     (kept ? std::wstring(L"Nothing was changed")
                           : L"The failed lookup also cleared " + prop + L", and DevTools couldn't put it back");
        }
        done = L"Restored " + prop + L" to " + rec.original;
        break;
    }
    }
    // A failed restore keeps the record, so the user can try again.
    if (ok) {
        DirtyErase(prop);
        PendingErase(prop);
        AppChangedErase(wire, prop);
        DevToolsBindingRow_ForgetLiveWrite(wire, prop);
        RebaselineRecords(wire);
    }
    ShowPropsForNode(g_selWire, g_selLabel, g_selBreadcrumb, /*force*/ true);
    SetStatusText(ok ? done : failed);
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
    CheckEditRecordsNow(g_selWire);
    // CHECK THE RETURNED VALUE, not just that the call was made. The runtime's own return says nothing
    // -- IVisualTreeService3::ResolveResource answers S_OK for a key the app does not have -- so what is
    // being checked here is the tap's verdict, which is built from the diagnostics callback the failure
    // actually arrives on. A pane that trusted the HRESULT named a key the element never took, for
    // every typo the free-text box is built to accept.
    if (!g_ctx.resolveFn(g_selWire, path, key, asTheme)) {
        // The failed lookup and the put-back are DevTools' changes, not the app's: rebaseline around them.
        RebaselineRecords(g_selWire);
        const bool restored = RestoreAfterFailedResolve(r, path, preVal);
        RebaselineRecords(g_selWire);
        // SAY WHICH KEY, AND WHAT IT COST. The box takes free text against the APP's dictionary, so the
        // useful sentence separates "you typed it wrong" from "this app has no such resource" -- and then
        // says whether the value survived, which is not the same question and has a different answer.
        SetStatusText(L"'" + key + L"' did not resolve in this app's resources. "
                      + (restored ? std::wstring(L"Nothing was changed - check the spelling, or type a key "
                                                 L"your app defines.")
                                  : std::wstring(L"The failed lookup also cleared ") + path +
                                    L", and DevTools had no earlier value to put back - set it again."));
        return false;
    }
    // Record the original before the session witness changes, so a first edit that assigns a resource still
    // remembers what the property was before it. Undo also clears the session assignment witness.
    NoteChange(path, preVal, std::wstring(asTheme ? L"{ThemeResource " : L"{StaticResource ") + key + L"}");
    NoteSessionAssign(path, asTheme ? DevToolsKind::Theme : DevToolsKind::Static,
                      std::wstring(asTheme ? L"{ThemeResource " : L"{StaticResource ") + key + L"}");
    if (EditRecord* rec = DirtyFind(path)) rec->themeAssigned = asTheme;
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
    InstanceHandle wire = 0;
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

static void BindingAnswersForget(const std::wstring& prop);

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
        // Whatever happened, the binding's earlier status, path and value no longer describe the app.
        if (r.wire == g_selWire) BindingAnswersForget(r.prop);
        if (!r.ok) {
            status = (graft ? L"Could not bind " : L"Could not write ") + r.prop + L": " + r.reason;
            continue;
        }
        if (graft) {
            NoteSessionAssign(r.prop, DevToolsKind::Binding, r.shown);
            PendingKindErase(r.prop);
            RecordErase(r.wire, r.prop);
            // A binding is installed again, so an earlier "replaced by a local value" no longer applies.
            if (r.wire == g_selWire) BindingLostErase(r.prop);
            DevToolsBindingRow_ForgetLiveWrite(r.wire, r.prop);
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
    res.gen = ask->gen; res.wire = ask->wire; res.op = ask->op; res.prop = ask->prop; res.payload = ask->payload;
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
static void DiagForgetAll();
static void WalkForget() { g_walkAnswers.clear(); DiagForgetAll(); }

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

// The managed diagnosis behind the BINDING lines. Same worker-and-drain shape as the walk, keyed by selection
// and property. Capture runs only while a binding is live: capturing after a local write has replaced it would
// overwrite the capture the quick panel took at pick time, and with it the only way back.
struct DiagAnswer { unsigned gen; InstanceHandle wire; std::wstring prop; std::wstring json; std::wstring action;
                    bool done; bool afterLiveWrite = false; };
static std::vector<DiagAnswer> g_diagAnswers;
static SRWLOCK                 g_diagLock = SRWLOCK_INIT;
static std::vector<DiagAnswer> g_diagDone;
static UINT_PTR                g_diagTimer = 0;
static volatile LONG           g_diagRestoreBusy = 0;
static volatile LONG           g_diagOutstanding = 0;

static DiagAnswer* DiagFind(const std::wstring& prop)
{
    for (auto& a : g_diagAnswers)
        if (a.prop == prop && a.wire == g_selWire && a.gen == g_route1Gen) return &a;
    return nullptr;
}
static void DiagForgetAll() { g_diagAnswers.clear(); }

static void BindingAnswersForget(const std::wstring& prop)
{
    for (size_t i = g_walkAnswers.size(); i-- > 0;)
        if (g_walkAnswers[i].prop == prop && g_walkAnswers[i].tryPath.empty()) g_walkAnswers.erase(g_walkAnswers.begin() + i);
    for (size_t i = g_diagAnswers.size(); i-- > 0;)
        if (g_diagAnswers[i].prop == prop) g_diagAnswers.erase(g_diagAnswers.begin() + i);
}

// A {Binding} restore reports "restored"; an {x:Bind} restore can only say it re-pushed the owner's bindings.
// An x:Bind restore only reports that the page re-ran its compiled bindings. That counts as restored only when
// the target's value visibly changed; otherwise the override may still be in place.
static bool RestoreLanded(const std::wstring& actionJson)
{
    DevToolsJson act;
    if (!DevToolsJsonParse(actionJson, act) || !act.IsObject()) return false;
    // The app can drop the binding again while it is being reattached, so "restored" alone is not enough.
    if (act.GetString(L"state") == L"restored") return act.GetString(L"bound") == L"True";
    const DevToolsJson* before = act.Find(L"before");
    const DevToolsJson* after = act.Find(L"after");
    return act.GetString(L"ownerUpdated") == L"True" && before && after &&
           before->type == DevToolsJsonType::String && after->type == DevToolsJsonType::String &&
           before->str != after->str;
}

// Restore reattaches the last binding DevTools saw, which can be one applied from the panel rather than the
// one in the XAML (x:Bind -> {Binding Title} -> Literal -> Restore). Record it so the panel shows what is installed.
static void NoteRestoredBinding(const std::wstring& prop, const std::wstring& actionJson)
{
    DevToolsJson act;
    if (!DevToolsJsonParse(actionJson, act) || !act.IsObject() || act.GetString(L"restoreKind") != L"binding") return;
    const std::wstring bindPath = act.GetString(L"path");
    if (bindPath.empty()) return;
    const DevToolsCardRow* row = FindPropertyRow(g_propRows, prop);
    if (!row || (row->authoredKind == L"binding" && bindPath == row->authoredKey)) return;
    std::wstring mode = act.GetString(L"mode");
    if (std::find(BindingModes().begin(), BindingModes().end(), mode) == BindingModes().end()) mode = L"OneWay";
    NoteSessionAssign(prop, DevToolsKind::Binding, BindingExprFrom(bindPath, mode));
}

// A literal written over the binding leaves nothing live to capture, even though the diagnosis of the XAML binding
// still reads as working. Capturing then would drop the saved binding Restore needs ({Binding OtherTitle} after
// x:Bind -> {Binding OtherTitle} -> Literal). After a restore, capture only once the binding is back.
static bool DiagMayCapture(DevToolsBindingTone tone, const std::wstring& op, const std::wstring& action, bool afterLiveWrite)
{
    if (tone != DevToolsBindingTone::Works && tone != DevToolsBindingTone::Broken && tone != DevToolsBindingTone::Warning)
        return false;
    return op.empty() ? !afterLiveWrite : RestoreLanded(action);
}

struct DiagAsk { InstanceHandle wire; std::wstring prop; std::wstring op; unsigned gen; bool afterLiveWrite; };

static DWORD WINAPI DiagWorker(LPVOID param)
{
    std::unique_ptr<DiagAsk> ask(reinterpret_cast<DiagAsk*>(param));
    DiagAnswer r{ ask->gen, ask->wire, ask->prop, std::wstring(), std::wstring(), true, ask->afterLiveWrite };
    if (g_ctx.bindFn) {
        if (!ask->op.empty()) g_ctx.bindFn(ask->wire, ask->op, ask->prop, std::wstring(), std::wstring(), &r.action);
        g_ctx.bindFn(ask->wire, L"diagnose", ask->prop, std::wstring(), std::wstring(), &r.json);
        if (DiagMayCapture(DevToolsBindingRow_Summary(r.json).tone, ask->op, r.action, ask->afterLiveWrite)) {
            std::wstring ignored;
            g_ctx.bindFn(ask->wire, L"capture", ask->prop, std::wstring(), std::wstring(), &ignored);
        }
    }
    AcquireSRWLockExclusive(&g_diagLock);
    g_diagDone.push_back(r);
    ReleaseSRWLockExclusive(&g_diagLock);
    if (!ask->op.empty()) InterlockedExchange(&g_diagRestoreBusy, 0);
    InterlockedDecrement(&g_diagOutstanding);
    return 0;
}

static void CALLBACK DiagTimerProc(HWND, UINT, UINT_PTR id, DWORD)
{
    std::vector<DiagAnswer> done;
    AcquireSRWLockExclusive(&g_diagLock);
    done.swap(g_diagDone);
    ReleaseSRWLockExclusive(&g_diagLock);
    // The managed answer takes longer than one tick; stop only once nothing is still being asked.
    if (done.empty()) {
        if (InterlockedCompareExchange(&g_diagOutstanding, 0, 0) == 0) { KillTimer(nullptr, id); g_diagTimer = 0; }
        return;
    }
    bool any = false, restored = false;
    for (DiagAnswer a : done) {
        if (a.gen != g_route1Gen || a.wire != g_selWire) continue;
        const bool hadAction = !a.action.empty();
        if (hadAction && RestoreLanded(a.action)) {
            // Only now is the binding back. Clearing these when the button was clicked kept a failed restore
            // looking like it worked, and kept a successful one saying the binding could not come back.
            BindingLostErase(a.prop);
            SessionAssignErase(a.prop);
            NoteRestoredBinding(a.prop, a.action);
            DevToolsBindingRow_ForgetLiveWrite(a.wire, a.prop);
            for (size_t i = g_walkAnswers.size(); i-- > 0;)
                if (g_walkAnswers[i].prop == a.prop && g_walkAnswers[i].tryPath.empty()) g_walkAnswers.erase(g_walkAnswers.begin() + i);
            // This answer now describes the restored binding: a later edit must ask again rather than reuse it,
            // and must not be reported as a failed restore.
            a.action.clear();
            a.afterLiveWrite = false;
        }
        if (DiagAnswer* existing = DiagFind(a.prop)) *existing = a;
        else g_diagAnswers.push_back(a);
        any = true;
        restored = restored || hadAction;
    }
    if (!any) return;
    // A restore changed a value the tree's caption may show.
    if (restored) RefreshTreeFromApp(true);
    // Rebuild the pane where the reader is looking, not back at the top.
    double offset = -1;
    DevToolsXC::ScrollViewer scroller{ nullptr };
    try {
        if (g_inspectorContent) {
            winrt::Windows::Foundation::IInspectable object{ nullptr };
            winrt::copy_from_abi(object, g_inspectorContent);
            scroller = object.as<DevToolsX::FrameworkElement>().FindName(L"PropsScroll").try_as<DevToolsXC::ScrollViewer>();
            if (scroller) offset = scroller.VerticalOffset();
        }
    } catch (const winrt::hresult_error&) { scroller = nullptr; }
    ShowPropsForNode(g_selWire, g_selLabel, g_selBreadcrumb, /*force*/ true);
    try {
        if (scroller && offset >= 0)
            scroller.ChangeView(nullptr, winrt::box_value(offset).as<winrt::Windows::Foundation::IReference<double>>(), nullptr, true);
    } catch (const winrt::hresult_error&) {}
}

static bool StartDiag(const std::wstring& prop, const std::wstring& op)
{
    DiagAsk* ask = new DiagAsk{ g_selWire, prop, op, g_route1Gen, DevToolsBindingRow_WasWrittenLive(g_selWire, prop) };
    InterlockedIncrement(&g_diagOutstanding);
    if (HANDLE th = CreateThread(nullptr, 0, DevToolsThreadEntry<DiagWorker>, ask, 0, nullptr)) {
        CloseHandle(th);
        if (!g_diagTimer) g_diagTimer = SetTimer(nullptr, 0, 100, &DiagTimerProc);
        return true;
    }
    InterlockedDecrement(&g_diagOutstanding);
    delete ask;
    return false;
}

static void PostDiagnose(const std::wstring& prop)
{
    if (!g_ctx.bindFn || !g_selWire || prop.empty()) return;
    if (DiagAnswer* cached = DiagFind(prop)) {
        // An answer from before a live edit to this property says nothing about the binding now.
        if (!cached->done || cached->afterLiveWrite || !DevToolsBindingRow_WasWrittenLive(g_selWire, prop)) return;
        g_diagAnswers.erase(g_diagAnswers.begin() + (cached - g_diagAnswers.data()));
    }
    g_diagAnswers.push_back({ g_route1Gen, g_selWire, prop, std::wstring(), std::wstring(), false });
    if (!StartDiag(prop, std::wstring())) g_diagAnswers.pop_back();
}

// Restore binding: re-attach what the XAML declares, then read the binding again so the lines show the result.
struct BindRestoreSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen || wire != g_selWire) return S_OK;
        if (InterlockedCompareExchange(&g_diagRestoreBusy, 1, 0) != 0) return S_OK;
        SetStatusText(L"Restoring the binding on " + prop + L"...");
        if (!StartDiag(prop, xbind ? L"restoreconfirmed" : L"restore")) {
            InterlockedExchange(&g_diagRestoreBusy, 0);
            SetStatusText(L"Could not start restoring " + prop);
            return S_OK;
        }
        return S_OK;
    }
    void Init(const std::wstring& p, bool x) { prop = p; xbind = x; wire = g_selWire; InitSink(IID_RoutedEventHandler_Del, g_route1Gen); }
    std::wstring prop;
    InstanceHandle wire = 0;
    bool xbind = false;
};

static const UINT kValidateIdleMs = 400;

static UINT_PTR      g_validateTimer = 0;
static unsigned      g_validateAsks = 0;

// The verdict for the path currently in the box. Rendered under it, and separate from the row's diagnosis:
// the diagnosis is about the binding that is INSTALLED, this is about the one being composed.
static std::wstring  g_validateVerdict;
static bool          g_validateBad = false;
static void*         g_validateExpFe = nullptr;
// Set when the {Binding} path box was seeded from an x:Bind path, which starts at the page rather than at the
// DataContext, so a path that does not resolve gets a hint about the difference.
static bool          g_validateFromXBind = false;

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
    if (g_validateBad && g_validateFromXBind)
        g_validateVerdict += L" This path was copied from x:Bind, which starts at the page. {Binding} starts at "
                             L"the DataContext, so you may need to remove the first part (for example, ViewModel.).";
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
    InstanceHandle wire = 0;
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
    // The current row, not the expansion's snapshot: after Enter saves, a later focus-out must compare with the
    // value that was saved, or it writes the same text again.
    const DevToolsCardRow* live = FindPropertyRowIn(g_propRows, c.path);
    const std::wstring seed = RowEditSeed(live ? *live : *c.row);
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
        value = DevToolsEditText::WithLineEndingsOf(ReadBox(c.expFe, L"PEdit"), seed);
    }
    const DevToolsKind was = CurrentKindOf(*c.row, c.path);
    const bool landed = ApplyLiteralValue(c.path, c.row->type, seed, value, /*confirmed*/ staged);
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
