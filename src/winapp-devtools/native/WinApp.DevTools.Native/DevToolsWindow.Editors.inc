// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsWindow.cpp; included into that translation unit, not compiled on its own.

// A KIND was selected in the dropdown. It STAGES -- nothing is written. That is the whole of Q4: opening this
// list to see what is available must not destroy a live binding, and a kind with no content yet is not a
// valid state to apply.
struct KindSelectSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen || !ctx.row || !sel) return S_OK;
        int idx = -1;
        DevToolsGetSelectedIndex(sel, &idx);
        if (idx < 0 || idx >= kDevToolsKindCount) return S_OK;
        const DevToolsKind chosen = (DevToolsKind)idx;
        const DevToolsKind current = CurrentKindOf(*ctx.row, ctx.path);
        if (chosen == DevToolsKind::XBind) {
            // The item is disabled, so this is unreachable through the UI; if a future template change made it
            // selectable, snapping back is better than staging something that can never be applied.
            SetStatusText(L"An {x:Bind} cannot be created at runtime");
            DevToolsAbi<DevToolsXCP::ISelector>(sel)->put_SelectedIndex((int)StagedOrCurrentKind(*ctx.row, ctx.path));
            return S_OK;
        }
        if (chosen == current) PendingKindErase(ctx.path);
        else                   PendingKindSet(ctx.path, chosen);
        RebuildExpansion(ctx.rowFe, *ctx.row, ctx.path, ctx.edit);
        return S_OK;
    }
    void Init(const KindCtx& c, void* selector)
    {
        ctx = c; sel = selector;
        InitSink(IID_SelectionChanged_Del, g_route1Gen);
    }
    KindCtx ctx;
    void* sel = nullptr;
};

struct KindApplySink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen || !ctx.row) return S_OK;
        CommitKind(ctx, true);
        return S_OK;
    }
    void Init(const KindCtx& c) { ctx = c; InitSink(IID_RoutedEventHandler_Del, g_route1Gen); }
    KindCtx ctx;
};
struct KindCancelSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen || !ctx.row) return S_OK;
        PendingKindErase(ctx.path);
        RebuildExpansion(ctx.rowFe, *ctx.row, ctx.path, ctx.edit);
        SetStatusText(L"Cancelled");
        return S_OK;
    }
    void Init(const KindCtx& c) { ctx = c; InitSink(IID_RoutedEventHandler_Del, g_route1Gen); }
    KindCtx ctx;
};

// The {x:Bind} EXIT. It stages {Binding} -- it does not apply it -- so the user sees the pre-filled path and
// the banner naming what a graft does before anything happens. That is the whole difference from the round-9
// design, where the same graft happened as a side effect of typing and had to be warned about after the fact.
struct XBindExitSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen || !ctx.row) return S_OK;
        PendingKindSet(ctx.path, DevToolsKind::Binding);
        RebuildExpansion(ctx.rowFe, *ctx.row, ctx.path, ctx.edit);
        return S_OK;
    }
    void Init(const KindCtx& c) { ctx = c; InitSink(IID_RoutedEventHandler_Del, g_route1Gen); }
    KindCtx ctx;
};

// Focus reveals the same explanation as the tooltip for keyboard/touch access.
// This sink retains its element; generation guards make callbacks from stale subtrees inert.
struct XBindInfoRevealSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen || !target) return S_OK;
        ShowEl(target, show);
        return S_OK;
    }
    void Init(IInspectable* t, bool s)
    {
        target = t;
        if (target) target->AddRef();
        show = s;
        InitSink(IID_RoutedEventHandler_Del, g_route1Gen);
    }
    IInspectable* target = nullptr;
    bool show = false;
};

struct KindKeySink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void* args)
    {
        if (gen != g_route1Gen || !ctx.row || !args) return S_OK;
        int key = 0;
        if (FAILED(DevToolsGetKey(args, &key))) return S_OK;
        if (key == 13 /*Enter*/) {
            if (DevToolsEditText::StartsNewLine(multiline, (GetKeyState(VK_SHIFT) & 0x8000) != 0,
                    (GetKeyState(VK_CONTROL) & 0x8000) != 0)) return S_OK;
            DevToolsPutHandled(args, true); CommitKind(ctx);
        }
        else if (key == 27 /*Escape*/ && PendingKindFind(ctx.path)) {
            DevToolsPutHandled(args, true);
            PendingKindErase(ctx.path);
            RebuildExpansion(ctx.rowFe, *ctx.row, ctx.path, ctx.edit);
        }
        return S_OK;
    }
    void Init(const KindCtx& c, bool lines = false) { ctx = c; multiline = lines; InitSink(IID_KeyEventHandler_Del, g_route1Gen); }
    KindCtx ctx;
    bool multiline = false;
};

struct ExpBackgroundFocusSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen || !target) return S_OK;
        bool moved = false;
        DevToolsFocus(target, 1 /*Pointer*/, &moved);
        return S_OK;
    }
    // Non-owning, for the same reason LostFocusSink's box is: the target lives in the same expansion this
    // handler is registered on, so it cannot outlive the element that raises the event.
    void Init(void* focusTargetUi) { target = focusTargetUi; InitSink(IID_PointerEventHandler_Del, g_route1Gen); }
    void* target = nullptr;
};

// A suggested resource key was clicked: put it in the field. It does NOT apply, for the same reason selecting
// a kind does not -- looking at what is available must not be the same gesture as taking it.
struct ResSuggestSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen || !box) return S_OK;
        void* tx = nullptr;
        if (SUCCEEDED(reinterpret_cast<IInspectable*>(box)->QueryInterface(DevToolsIid<DevToolsXC::ITextBox>(), &tx)) && tx) {
            HSTRING h = MakeStr(key); DevToolsPutTextBoxText(tx, h); WindowsDeleteString(h);
            reinterpret_cast<IUnknown*>(tx)->Release();
        }
        return S_OK;
    }
    void Init(void* keyBox, const std::wstring& k)
    { box = keyBox; key = k; InitSink(IID_RoutedEventHandler_Del, g_route1Gen); }
    void* box = nullptr;
    std::wstring key;
};

// Write a test value THROUGH a TwoWay binding to its SOURCE. Deliberately an action and not a field: it
// changes the app's DATA rather than its appearance, and looking like value editing is exactly the ambiguity
// the kind-specific editor removed. The binding survives -- and that is asserted by the agent's read-back
// rather than assumed, because the obvious route (a local write on the target) demonstrably does not.
struct WriteToSourceSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen || !ctx.row) return S_OK;
        ApplyWriteToSource(ctx.path, ReadBox(ctx.expFe, L"PWriteVal"));
        return S_OK;
    }
    void Init(const KindCtx& c) { ctx = c; InitSink(IID_RoutedEventHandler_Del, g_route1Gen); }
    KindCtx ctx;
};

// Unset editors say which value is absent instead of showing runtime sentinel tokens as editable text.
static void PopulateRowValue(IInspectable* v, const DevToolsCardRow& r)
{
    std::wstring shown = r.value;
    const bool unset = r.valueState == L"unset";
    if (unset)                              shown = L"Auto";
    else if (r.valueState == L"null")       shown = L"(null)";
    else if (r.valueState == L"unresolved") shown = ShortType(r.value.empty() ? r.type : r.value);
    else if (shown.empty() && ShortType(r.type) != L"String") shown = RowAuthoredLiteral(r);
    const bool noValue = RowValueIsMissing(r);
    if (noValue && shown.size() > 2 && shown.front() == L'{' && shown.back() == L'}')
        shown = shown.substr(1, shown.size() - 2);
    SetTextOn(v, shown);
    void* tb = nullptr;
    if (SUCCEEDED(v->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &tb)) && tb) {
        if (void* brush = noValue ? static_cast<void*>(g_palSecondary) : static_cast<void*>(g_palPrimary))
            DevToolsPutTextForeground(tb, brush);
        DevToolsPutFontStyle(tb, noValue ? 2 /*Italic*/ : 0 /*Normal*/);
        reinterpret_cast<IUnknown*>(tb)->Release();
    }
}

// Populate top-level and child rows through one path/edit map. Collapsed-state signals use
// type styles, not fills that High Contrast can replace.
static void PopulateRowLine(void* fe, const DevToolsCardRow& r, const std::wstring& path, int edit)
{
    // The NAME. A value set on this element gets bold so "what I set" pops out of the defaulted majority;
    // an UNSET one goes italic and dim. Weight and style rather than colour, because the template owns the
    // theme and both survive High Contrast.
    const bool setHere = !r.authoredKind.empty() || SourceIsSetHere(r.source);
    const bool unset   = r.valueState == L"unset";
    if (IInspectable* n = FindInRow(fe, L"PName")) {
        SetTextOn(n, r.name);
        void* tb = nullptr;
        if (SUCCEEDED(n->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &tb)) && tb) {
            DevToolsPutTextFontWeight(tb, setHere ? 700 : 400);
            DevToolsPutFontStyle(tb, unset ? 2 /*Italic*/ : 0 /*Normal*/);
            if (void* brush = unset ? static_cast<void*>(g_palTertiary) : static_cast<void*>(g_palSecondary))
                DevToolsPutTextForeground(tb, brush);
            reinterpret_cast<IUnknown*>(tb)->Release();
        }
        n->Release();
    }

    // Explicit accessible identities follow recycled row data. Property value opens the row;
// Property editor names its separate input control. Keep names distinct across both inspector views.
    if (IInspectable* e = FindInRow(fe, L"PValueBtn"))
        { SetAutomationTextOn(e, path + L" value", path + L" value", L""); e->Release(); }
    if (IInspectable* e = FindInRow(fe, L"PValue"))
        { SetAutomationTextOn(e, path + L" shown", path + L" shown", L""); e->Release(); }
    // The changed MARKER states a fact; it is not an action, and its name must not read as one.
    if (IInspectable* d = FindInRow(fe, L"PDot"))
        { SetAutomationTextOn(d, path + L" changed", L"", L""); d->Release(); }

    if (IInspectable* c = FindInRow(fe, L"PChevron"))
        { SetAutomationTextOn(c, L"Expand " + path, L"", L""); c->Release(); }
    if (IInspectable* v = FindInRow(fe, L"PValue")) {
        PopulateRowValue(v, r);
        v->Release();
    }

    if (IInspectable* d = FindInRow(fe, L"PDot")) {
        ShowEl(d, DirtyHas(path));
        d->Release();
    }

    const bool expandable = RowHasExpansion(r, path, edit);
    std::shared_ptr<const DevToolsCardRow> shared;
    if (expandable) shared = std::make_shared<const DevToolsCardRow>(r);
    if (IInspectable* ch = FindInRow(fe, L"PChevron")) {
        ShowEl(ch, expandable);
        void* bb = nullptr;
        if (expandable && SUCCEEDED(ch->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), &bb)) && bb) {
            void* rowUi = nullptr;
            reinterpret_cast<IUnknown*>(fe)->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &rowUi);
            RowExpandSink* es = new RowExpandSink();
            es->Init(shared, path, edit, rowUi, /*wantsEditor*/ false);
            __int64 tok = 0;
            const HRESULT hrE = DevToolsAddClick(bb, es, &tok);
            if (FAILED(hrE))
                DevToolsOverlayLog(L"route1.props row '%s' triangle add_Click FAILED hr=0x%08X -- it will not open",
                              path.c_str(), hrE);
            reinterpret_cast<IUnknown*>(bb)->Release();
        }
        ch->Release();
    }
    if (IInspectable* btn = FindInRow(fe, L"PValueBtn")) {
        void* bb = nullptr;
        if (expandable && SUCCEEDED(btn->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), &bb)) && bb) {
            void* rowUi = nullptr;
            reinterpret_cast<IUnknown*>(fe)->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &rowUi);
            RowExpandSink* vs = new RowExpandSink();
            vs->Init(shared, path, edit, rowUi, /*wantsEditor*/ edit >= 0);
            __int64 tok = 0;
            const HRESULT hrV = DevToolsAddClick(bb, vs, &tok);
            if (FAILED(hrV))
                DevToolsOverlayLog(L"route1.props row '%s' value add_Click FAILED hr=0x%08X -- it will not open",
                              path.c_str(), hrV);
            reinterpret_cast<IUnknown*>(bb)->Release();
        }
        btn->Release();
    }

    if (expandable && PropIsExpanded(r.name)) {
        if (EnsureExpansion(fe, r, path, edit)) ApplyRowExpansionState(fe, path, true);
    }
}

static void NameKindItems(IInspectable* combo, const std::wstring& path)
{
    if (!combo) return;
    void* ic = nullptr;
    if (FAILED(combo->QueryInterface(DevToolsIid<DevToolsXC::IItemsControl>(), &ic)) || !ic) return;
    IInspectable* raw = nullptr;
    void* items = nullptr;
    if (SUCCEEDED(DevToolsAbi<DevToolsXC::IItemsControl>(ic)->get_Items(reinterpret_cast<void**>(&raw))) && raw) {
        raw->QueryInterface(
            DevToolsIid<winrt::Windows::Foundation::Collections::IVector<winrt::Windows::Foundation::IInspectable>>(),
            &items);
        raw->Release();
    }
    reinterpret_cast<IUnknown*>(ic)->Release();
    if (!items) return;
    for (int i = 0; i < kDevToolsKindCount; ++i) {
        IInspectable* item = nullptr;
        if (FAILED(DevToolsVecGetAt(reinterpret_cast<IInspectable*>(items), (unsigned)i, &item)) || !item) continue;
        const bool xbind = ((DevToolsKind)i == DevToolsKind::XBind);
        if (xbind) {
            void* ctl = nullptr;
            if (SUCCEEDED(item->QueryInterface(DevToolsIid<DevToolsXC::IControl>(), &ctl)) && ctl) {
                DevToolsPutIsEnabled(ctl, false);
                reinterpret_cast<IUnknown*>(ctl)->Release();
            }
            SetAutomationTextOn(item, std::wstring(L"{x:Bind} unavailable for ") + path + L" - " + kXBindWhy,
                                L"", L"");
        } else {
            SetAutomationTextOn(item, L"Make " + path + L" a " + KindLabel((DevToolsKind)i), L"", L"");
        }
        item->Release();
    }
    reinterpret_cast<IUnknown*>(items)->Release();
}

static void SetPlaceholderOn(IInspectable* el, const std::wstring& text)
{
    if (!el || text.empty()) return;
    void* tx = nullptr;
    if (SUCCEEDED(el->QueryInterface(DevToolsIid<DevToolsXC::ITextBox>(), &tx)) && tx) {
        HSTRING h = MakeStr(text);
        DevToolsPutPlaceholderText(tx, h);
        WindowsDeleteString(h);
        reinterpret_cast<IUnknown*>(tx)->Release();
    }
}

// Editor part names are shared by automation wiring and live kind selection; keep the taxonomy in one place.
static const wchar_t* const kAllEditorParts[] =
    { L"PEdit", L"PToggle", L"PCombo", L"PHex", L"PResKey", L"PPath", L"PXExpr" };

// Only the visible editor gets the stable accessible name; collapsed alternatives stay out of UIA.
static void NameTheEditor(void* fe, const std::wstring& path, const wchar_t* primary)
{
    if (!primary) return;
    bool named = false;
    for (const wchar_t* nm : kAllEditorParts) {
        if (wcscmp(nm, primary) != 0) continue;
        if (IInspectable* e = FindInRow(fe, nm)) {
            SetAutomationTextOn(e, path + L" editor", path + L" editor", L"");
            e->Release();
            named = true;
        }
        break;
    }
    // a primary part that is not in the expansion is a row whose editor cannot be addressed by name,
    // which is indistinguishable from a row that has no editor at all until someone tries to drive it.
    if (!named)
        DevToolsOverlayLog(L"route1.props MISS: '%s' named editor part %s is not in the expansion",
                      path.c_str(), primary);
}

static void PopulateKindEditor(void* fe, const DevToolsCardRow& r, const std::wstring& path, int edit,
                               DevToolsKind kind, const KindCtx& ctx)
{
    static const wchar_t* const kBlocks[] =
        { L"PEdText", L"PEdBool", L"PEdEnum", L"PEdColor", L"PEdFields", L"PEdRes", L"PEdBind", L"PEdXBind" };
    const wchar_t* show = L"PEdText";
    const wchar_t* primary = L"PEdit";

    if (kind == DevToolsKind::XBind)            { show = L"PEdXBind"; primary = L"PXExpr"; }
    else if (kind == DevToolsKind::Binding)     { show = L"PEdBind";  primary = L"PPath"; }
    else if (KindIsResource(kind))         { show = L"PEdRes";   primary = L"PResKey"; }
    else {
        // LITERAL -> the tap's editKind, which is the ONE taxonomy for "how do I render and edit this?"
        // (DevToolsRead.h:78-88). "none" falls back to a plain text box, which is what the header says a surface
        // must do when the value has no structured shape but the property is still settable.
        const std::wstring ek = r.editKind;
        if (ek == L"bool")        { show = L"PEdBool";   primary = L"PToggle"; }
        else if (ek == L"enum")   { show = L"PEdEnum";   primary = L"PCombo"; }
        else if (ek == L"color")  { show = L"PEdColor";  primary = L"PHex"; }
        // "fields" has no single templated editor -- its boxes are created per label off the wire -- so the
        // "<prop> editor" name goes on the FIRST box, in the loop that makes them.
        else if (ek == L"fields") { show = L"PEdFields"; primary = nullptr; }
    }
    for (const wchar_t* b : kBlocks) {
        if (IInspectable* el = FindInRow(fe, b)) { ShowEl(el, wcscmp(b, show) == 0); el->Release(); }
    }

    auto wireKeys = [&](const wchar_t* name, bool multiline = false) {
        if (IInspectable* el = FindInRow(fe, name)) {
            void* ui = nullptr;
            if (SUCCEEDED(el->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) && ui) {
                KindKeySink* ks = new KindKeySink(); ks->Init(ctx, multiline);
                __int64 tok = 0;
                DevToolsAddPreviewKeyDown(ui, ks, &tok);
                reinterpret_cast<IUnknown*>(ui)->Release();
            }
            el->Release();
        }
    };

    switch (kind) {
    case DevToolsKind::XBind: {
        const std::wstring expr = r.authored.empty()
            ? std::wstring(L"{x:Bind ...}  (the source for this element could not be read)")
            : r.authored;
        if (IInspectable* e = FindInRow(fe, L"PXExpr")) { SetTextOn(e, expr); e->Release(); }
        static const wchar_t* const kXBindHeadline =
            L"{x:Bind} is compiled at build time, so it cannot be edited here.";
        static const wchar_t* const kXBindDetail =
            L"An {x:Bind} is compiled into generated code when the app is built, and the runtime exposes no "
            L"way to create or change one.\n\n"
            L"Choosing {Binding} installs a runtime binding with SetBinding - on the running app only. Your "
            L".xaml is not changed, and the compiled binding cannot be restored without restarting.";
        if (IInspectable* e = FindInRow(fe, L"PXWhy")) { SetTextOn(e, kXBindHeadline); e->Release(); }
        if (IInspectable* t = FindInRow(fe, L"PXInfoText")) {
            SetTextOn(t, kXBindDetail);
            ShowEl(t, false);
            SetAutomationTextOn(t, L"Why " + path + L" cannot be edited", L"", L"");
            if (IInspectable* b = FindInRow(fe, L"PXInfo")) {
                // Named per row: thirty realized rows must not put thirty elements called "More information"
                // into a UIA namespace this window shares with the in-app overlay.
                SetAutomationTextOn(b, L"Why " + path + L" cannot be edited, and what switching to a Binding does",
                                    L"", L"");
                void* ui = nullptr;
                if (SUCCEEDED(b->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) && ui) {
                    XBindInfoRevealSink* on = new XBindInfoRevealSink();  on->Init(t, true);
                    XBindInfoRevealSink* off = new XBindInfoRevealSink(); off->Init(t, false);
                    __int64 tok = 0;
                    const HRESULT hrG = DevToolsAddGotFocus(ui, on, &tok);
                    const HRESULT hrL = DevToolsAddLostFocus(ui, off, &tok);
                    if (FAILED(hrG) || FAILED(hrL))
                        DevToolsOverlayLog(L"route1.props x:Bind info for '%s' focus wiring FAILED got=0x%08X lost=0x%08X"
                                      L" -- the keyboard route to the explanation is gone",
                                      path.c_str(), hrG, hrL);
                    reinterpret_cast<IUnknown*>(ui)->Release();
                }
                b->Release();
            }
            t->Release();
        }
        if (IInspectable* b = FindInRow(fe, L"PXBindExit")) {
            SetAutomationTextOn(b, L"Switch " + path + L" to a Binding", L"", L"");
            void* bb = nullptr;
            if (edit >= 0 && SUCCEEDED(b->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), &bb)) && bb) {
                XBindExitSink* xs = new XBindExitSink(); xs->Init(ctx);
                __int64 tok = 0;
                const HRESULT hr = DevToolsAddClick(bb, xs, &tok);
                if (FAILED(hr))
                    DevToolsOverlayLog(L"route1.props x:Bind exit for '%s' add_Click FAILED hr=0x%08X -- the row is a dead end",
                                  path.c_str(), hr);
                reinterpret_cast<IUnknown*>(bb)->Release();
            }
            b->Release();
        }
        break;
    }

    case DevToolsKind::Theme:
    case DevToolsKind::Static: {
        if (IInspectable* e = FindInRow(fe, L"PResLbl"))
            { SetTextOn(e, kind == DevToolsKind::Theme ? L"THEME RESOURCE KEY" : L"STATIC RESOURCE KEY"); e->Release(); }
        const SessionAssign* sa = SessionAssignFind(path);
        const bool sameKind = sa && sa->kind == kind;
        // SEEDING: a resource kind the row ALREADY has keeps its key; a newly staged one starts EMPTY with a
        // typeahead, because there is no key to carry over from a literal or a binding.
        std::wstring seedKey;
        if (CurrentKindOf(r, path) == kind)
            seedKey = sameKind ? MarkupInner(sa->detail) : r.authoredKey;
        if (IInspectable* e = FindInRow(fe, L"PResKey")) {
            void* tx = nullptr;
            if (SUCCEEDED(e->QueryInterface(DevToolsIid<DevToolsXC::ITextBox>(), &tx)) && tx) {
                HSTRING h = MakeStr(seedKey); DevToolsPutTextBoxText(tx, h); WindowsDeleteString(h);
                reinterpret_cast<IUnknown*>(tx)->Release();
            }
            e->Release();
        }
        if (IInspectable* box = FindInRow(fe, L"PResSug")) {
            const bool canResolve = g_ctx.resolveFn != nullptr;
            std::vector<std::wstring> keys = canResolve ? ResourceKeyCandidates(r) : std::vector<std::wstring>();
            if (keys.size() > 6) keys.resize(6);
            void* pnl = nullptr; void* kids = nullptr;
            if (!keys.empty() &&
                SUCCEEDED(box->QueryInterface(DevToolsIid<DevToolsXC::IPanel>(), &pnl)) && pnl &&
                SUCCEEDED(DevToolsPanelGetChildren(pnl, reinterpret_cast<IInspectable**>(&kids))) && kids)
            {
                IInspectable* keyBox = FindInRow(fe, L"PResKey");
                for (const std::wstring& k : keys) {
                    IInspectable* btn = DevToolsActivate(L"Microsoft.UI.Xaml.Controls.Button");
                    if (!btn) continue;
                    void* cc = nullptr;
                    if (SUCCEEDED(btn->QueryInterface(DevToolsIid<DevToolsXC::IContentControl>(), &cc)) && cc) {
                        IInspectable* boxed = nullptr;
                        if (SUCCEEDED(BoxString(k, &boxed)) && boxed) { DevToolsPutContent(cc, boxed); boxed->Release(); }
                        reinterpret_cast<IUnknown*>(cc)->Release();
                    }
                    void* ctl = nullptr;
                    if (SUCCEEDED(btn->QueryInterface(DevToolsIid<DevToolsXC::IControl>(), &ctl)) && ctl) {
                        DevToolsPutControlFontSize(ctl, 11.0);
                        DevToolsPutPadding(ctl, 6, 2, 6, 2);
                        reinterpret_cast<IUnknown*>(ctl)->Release();
                    }
                    void* sfe = nullptr;
                    if (SUCCEEDED(btn->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &sfe)) && sfe) {
                        DevToolsPutHorizontalAlignment(sfe, 0 /*Left*/);
                        reinterpret_cast<IUnknown*>(sfe)->Release();
                    }
                    void* bb = nullptr;
                    if (keyBox && SUCCEEDED(btn->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), &bb)) && bb) {
                        ResSuggestSink* ss = new ResSuggestSink(); ss->Init(keyBox, k);
                        __int64 tok = 0;
                        DevToolsAddClick(bb, ss, &tok);
                        reinterpret_cast<IUnknown*>(bb)->Release();
                    }
                    SetAutomationTextOn(btn, L"Use key " + k + L" for " + path, L"", L"");
                    DevToolsVecAppend(reinterpret_cast<IInspectable*>(kids), btn);
                    btn->Release();
                }
                if (keyBox) keyBox->Release();
                reinterpret_cast<IUnknown*>(kids)->Release();
                ShowEl(box, true);
            }
            if (pnl) reinterpret_cast<IUnknown*>(pnl)->Release();
            if (IInspectable* n = FindInRow(fe, L"PResNote")) {
                if (!canResolve) {
                    SetTextOn(n, L"This build cannot assign resources, so a key cannot be applied here.");
                } else if (keys.empty()) {
                    SetTextOn(n, L"No suggestion fits a " + ShortType(RowResourceType(r)) + L": the keys "
                                 L"DevTools can name are this element's own resource keys and the built-in "
                                 L"theme brushes, and none of them is assignable here. Type a key from your "
                                 L"app's resources - the resolved value appears on the row above.");
                }
                n->Release();
            }
            box->Release();
        }
        wireKeys(L"PResKey");
        break;
    }

    case DevToolsKind::Binding: {
        // SEEDING: -> {Binding} takes the EXISTING path, which is the graft case. Coming from an {x:Bind}
        // that is exactly what makes the exit useful: the path the developer wrote is already there.
        if (IInspectable* e = FindInRow(fe, L"PPath")) {
            void* tx = nullptr;
            if (SUCCEEDED(e->QueryInterface(DevToolsIid<DevToolsXC::ITextBox>(), &tx)) && tx) {
                HSTRING h = MakeStr(BindingPathOf(r, path)); DevToolsPutTextBoxText(tx, h); WindowsDeleteString(h);
                reinterpret_cast<IUnknown*>(tx)->Release();
            }
            if (edit >= 0) {
                void* tbx = nullptr;
                if (SUCCEEDED(e->QueryInterface(DevToolsIid<DevToolsXC::ITextBox>(), &tbx)) && tbx) {
                    PathValidateSink* vs = new PathValidateSink();
                    vs->Init(path, e, fe);
                    __int64 tok = 0;
                    const HRESULT hr = DevToolsAddTextChanged(tbx, vs, &tok);
                    if (FAILED(hr))
                        DevToolsOverlayLog(L"route1.props path validator for '%s' add_TextChanged FAILED hr=0x%08X"
                                      L" -- a bad path will only be caught on commit", path.c_str(), hr);
                    reinterpret_cast<IUnknown*>(tbx)->Release();
                }
            }
            e->Release();
        }
        g_validateVerdict.clear();
        g_validateBad = false;
        g_validateProp = path;
        g_validatePath.clear();
        if (IInspectable* m = FindInRow(fe, L"PPathMsg")) { SetTextOn(m, L""); ShowEl(m, false); m->Release(); }
        const std::wstring mode = BindingModeOf(r, path);
        if (IInspectable* e = FindInRow(fe, L"PMode")) {
            FillCombo(e, BindingModes());
            void* sel = nullptr;
            if (SUCCEEDED(e->QueryInterface(DevToolsIid<DevToolsXCP::ISelector>(), &sel)) && sel) {
                int idx = 0;
                for (size_t i = 0; i < BindingModes().size(); ++i) if (BindingModes()[i] == mode) idx = (int)i;
                DevToolsAbi<DevToolsXCP::ISelector>(sel)->put_SelectedIndex(idx);
                reinterpret_cast<IUnknown*>(sel)->Release();
            }
            SetAutomationTextOn(e, path + L" binding mode", path + L" binding mode", L"");
            e->Release();
        }
        // WRITE A TEST VALUE, gated on Mode. It is a FIELD of the binding editor and not a bolt-on action,
        // because writing through exists only because Mode is TwoWay -- it is a property of the binding, not
        // of the value. On OneWay it is DISABLED with its reason on its face, never hidden.
        const bool twoWay = (mode == L"TwoWay");
        // The VALUE to push. Seeded with what the element currently shows, so "write the thing I can see back
        // through the binding" is one click -- and typing over it is how a real test value gets there.
        if (IInspectable* e = FindInRow(fe, L"PWriteVal")) {
            void* tx = nullptr;
            if (SUCCEEDED(e->QueryInterface(DevToolsIid<DevToolsXC::ITextBox>(), &tx)) && tx) {
                HSTRING h = MakeStr(RowEditSeed(r)); DevToolsPutTextBoxText(tx, h); WindowsDeleteString(h);
                reinterpret_cast<IUnknown*>(tx)->Release();
            }
            void* ctl = nullptr;
            if (SUCCEEDED(e->QueryInterface(DevToolsIid<DevToolsXC::IControl>(), &ctl)) && ctl) {
                DevToolsPutIsEnabled(ctl, twoWay && edit >= 0);
                reinterpret_cast<IUnknown*>(ctl)->Release();
            }
            SetAutomationTextOn(e, path + L" test value", path + L" test value", L"");
            e->Release();
        }
        if (IInspectable* b = FindInRow(fe, L"PWriteSrc")) {
            IInspectable* boxed = nullptr;
            const std::wstring label = twoWay ? L"Write to source" : L"OneWay";
            void* cc = nullptr;
            if (SUCCEEDED(b->QueryInterface(DevToolsIid<DevToolsXC::IContentControl>(), &cc)) && cc) {
                if (SUCCEEDED(BoxString(label, &boxed)) && boxed) { DevToolsPutContent(cc, boxed); boxed->Release(); }
                reinterpret_cast<IUnknown*>(cc)->Release();
            }
            void* ctl = nullptr;
            if (SUCCEEDED(b->QueryInterface(DevToolsIid<DevToolsXC::IControl>(), &ctl)) && ctl) {
                DevToolsPutIsEnabled(ctl, twoWay && edit >= 0);
                reinterpret_cast<IUnknown*>(ctl)->Release();
            }
            SetAutomationTextOn(b, twoWay ? (L"Write " + path + L" to its source")
                                          : (L"Write " + path + L" to its source - OneWay, the source cannot be written"),
                                L"", L"");
            void* bb = nullptr;
            if (twoWay && edit >= 0 && SUCCEEDED(b->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), &bb)) && bb) {
                WriteToSourceSink* ws = new WriteToSourceSink(); ws->Init(ctx);
                __int64 tok = 0;
                DevToolsAddClick(bb, ws, &tok);
                reinterpret_cast<IUnknown*>(bb)->Release();
            }
            b->Release();
        }
        if (IInspectable* e = FindInRow(fe, L"PBindNote")) {
            SetTextOn(e, twoWay
                ? L"A literal write to a TwoWay binding is not destructive: the expression stays installed and "
                  L"the write goes through to the source. Applied to the running app only."
                : L"Applied with SetBinding to the running app only - your .xaml is not changed.");
            e->Release();
        }
        wireKeys(L"PPath");
        break;
    }

    default: {
        const std::wstring ek = r.editKind;
        const std::wstring seed = RowEditSeed(r);
        if (ek == L"bool") {
            if (IInspectable* t = FindInRow(fe, L"PToggle")) {
                void* ts = nullptr;
                if (SUCCEEDED(t->QueryInterface(DevToolsIid<DevToolsXC::IToggleSwitch>(), &ts)) && ts) {
                    DevToolsTogglePutIsOn(ts, DevToolsBoolValueIsOn(r.value));
                    if (edit >= 0) {
                        BoolToggleSink* bs = new BoolToggleSink();
                        bs->Init(g_selWire, path, r.type, DevToolsBoolLiteral(DevToolsBoolValueIsOn(seed)), ts);
                        __int64 btok = 0;
                        const HRESULT hrT = DevToolsAddToggled(ts, bs, &btok);
                        if (FAILED(hrT))
                            DevToolsOverlayLog(L"route1.props bool row '%s' add_Toggled FAILED hr=0x%08X -- it will not commit",
                                          path.c_str(), hrT);
                    }
                    reinterpret_cast<IUnknown*>(ts)->Release();
                } else {
                    DevToolsOverlayLog(L"route1.props MISS: bool row '%s' editor is not a ToggleSwitch", path.c_str());
                }
                t->Release();
            }
        } else if (ek == L"enum") {
            if (IInspectable* c = FindInRow(fe, L"PCombo")) {
                const std::vector<std::wstring> members = EnumMembersFor(r.type);
                FillCombo(c, members);
                void* sel = nullptr;
                if (SUCCEEDED(c->QueryInterface(DevToolsIid<DevToolsXCP::ISelector>(), &sel)) && sel) {
                    DevToolsAbi<DevToolsXCP::ISelector>(sel)->put_SelectedIndex(EnumIndexOf(members, r.value));
                    if (edit >= 0) {
                        const int cur = EnumIndexOf(members, seed);
                        const std::wstring orig = cur >= 0 ? members[(size_t)cur] : seed;
                        EnumSelectSink* es = new EnumSelectSink();
                        es->Init(g_selWire, path, r.type, orig, members, sel);
                        __int64 stok = 0;
                        const HRESULT hrS = DevToolsAddSelectionChanged(sel, es, &stok);
                        if (FAILED(hrS))
                            DevToolsOverlayLog(L"route1.props enum row '%s' add_SelectionChanged FAILED hr=0x%08X -- it will not commit",
                                          path.c_str(), hrS);
                    }
                    reinterpret_cast<IUnknown*>(sel)->Release();
                }
                c->Release();
            }
        } else if (ek == L"color") {
            if (IInspectable* sw = FindInRow(fe, L"PSwatch")) {
                if (IsHexColor(seed)) {
                    IInspectable* brush = nullptr;
                    // Parsed as a literal, never as a resource reference: this is a #AARRGGBB string.
                    if (SUCCEEDED(LoadMarkupWin(L"<SolidColorBrush xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Color=\""
                                                + seed + L"\"/>", &brush)) && brush) {
                        void* bd = nullptr;
                        if (SUCCEEDED(sw->QueryInterface(DevToolsIid<DevToolsXC::IBorder>(), &bd)) && bd) {
                            DevToolsPutBorderBackground(bd, brush);
                            reinterpret_cast<IUnknown*>(bd)->Release();
                        }
                        brush->Release();
                    }
                }
                sw->Release();
            }
            LostFocusSink* hexCommit = nullptr;
            IInspectable* hexBox = nullptr;
            if (IInspectable* hx = FindInRow(fe, L"PHex")) {
                void* tx = nullptr;
                if (SUCCEEDED(hx->QueryInterface(DevToolsIid<DevToolsXC::ITextBox>(), &tx)) && tx) {
                    HSTRING h = MakeStr(seed); DevToolsPutTextBoxText(tx, h); WindowsDeleteString(h);
                    reinterpret_cast<IUnknown*>(tx)->Release();
                }
                SetPlaceholderOn(hx, RowEditPlaceholder(r));
                if (edit >= 0) {
                    void* ui = nullptr; void* box = nullptr;
                    hx->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui);
                    hx->QueryInterface(DevToolsIid<DevToolsXC::ITextBox>(), &box);
                    if (ui && box) {
                        LostFocusSink* commit = new LostFocusSink();
                        commit->Init(g_selWire, path, r.type, seed, reinterpret_cast<IInspectable*>(box));
                        __int64 tok = 0;
                        DevToolsAddLostFocus(ui, commit, &tok);
                        hexCommit = commit;
                        hexBox = hx; hexBox->AddRef();
                    }
                }
                hx->Release();
            }
            // The swatch opens a full color picker. Closing it writes the chosen color through the hex box's own
            // commit, so the picker and typing share one write path and one change check.
            if (IInspectable* sb = FindInRow(fe, L"PSwatchBtn")) {
                try {
                    winrt::Windows::Foundation::IInspectable object{ nullptr };
                    winrt::copy_from_abi(object, sb);
                    const auto button = object.as<DevToolsXC::Button>();
                    button.IsEnabled(hexCommit != nullptr);
                    DevToolsXA::AutomationProperties::SetName(button, L"Pick a color for " + path);
                    if (hexCommit && hexBox) {
                        winrt::Windows::Foundation::IInspectable boxObject{ nullptr };
                        winrt::copy_from_abi(boxObject, hexBox);
                        const auto hex = boxObject.as<DevToolsXC::TextBox>();
                        const unsigned gen = g_route1Gen;
                        hexCommit->AddRef();
                        const std::shared_ptr<LostFocusSink> commit(hexCommit, [](LostFocusSink* s) { s->Release(); });
                        button.Click([hex, commit, gen](auto&& sender, auto&&) {
                            if (gen != g_route1Gen) return;
                            DevToolsXC::ColorPicker picker;
                            picker.IsAlphaEnabled(true);
                            picker.IsColorChannelTextInputVisible(false);
                            picker.IsHexInputVisible(true);
                            const std::wstring seedHex = hex.Text().c_str();
                            if (IsHexColor(seedHex)) {
                                const auto channel = [&](size_t at) {
                                    return static_cast<uint8_t>(wcstoul(seedHex.substr(at, 2).c_str(), nullptr, 16));
                                };
                                picker.Color(winrt::Windows::UI::Color{ channel(1), channel(3), channel(5), channel(7) });
                            }
                            DevToolsXC::Flyout flyout;
                            flyout.Content(picker);
                            flyout.Closed([picker, hex, commit, gen](auto&&, auto&&) {
                                if (gen != g_route1Gen) return;
                                const auto c = picker.Color();
                                wchar_t chosen[10];
                                swprintf_s(chosen, L"#%02X%02X%02X%02X", c.A, c.R, c.G, c.B);
                                if (_wcsicmp(chosen, hex.Text().c_str()) == 0) return;
                                hex.Text(chosen);
                                commit->Invoke(nullptr, nullptr);
                            });
                            flyout.ShowAt(sender.template as<DevToolsX::FrameworkElement>());
                        });
                    }
                } catch (...) {
                    DevToolsOverlayLog(L"route1.props color picker for '%s' could not be wired", path.c_str());
                }
                sb->Release();
            }
            if (hexBox) hexBox->Release();
            if (IInspectable* pb = FindInRow(fe, L"PPreset")) {
                if (edit >= 0 && g_ctx.writeFn) {
                    std::vector<std::wstring> presets;
                    for (const auto& cp : ColorPresets()) presets.push_back(cp.second);
                    FillCombo(pb, presets);
                    void* psel = nullptr;
                    if (SUCCEEDED(pb->QueryInterface(DevToolsIid<DevToolsXCP::ISelector>(), &psel)) && psel) {
                        // Placeholder-only: nothing is pre-selected, so building the expansion cannot commit.
                        EnumSelectSink* ps = new EnumSelectSink();
                        ps->Init(g_selWire, path, r.type, seed, presets, psel);
                        __int64 tok = 0;
                        DevToolsAddSelectionChanged(psel, ps, &tok);
                        reinterpret_cast<IUnknown*>(psel)->Release();
                    }
                    SetAutomationTextOn(pb, path + L" colour preset", path + L" colour preset", L"");
                    ShowEl(pb, true);
                }
                pb->Release();
            }
            wireKeys(L"PHex");
        } else if (ek == L"fields") {
            // ONE editor shape for every flat struct. The number of boxes and their names come off the wire
            // (DevToolsRead.h:83-87), so a Thickness gets four, a Point two and a Vector3 three without a branch.
            if (IInspectable* lbl = FindInRow(fe, L"PFieldsLbl"))
                { SetTextOn(lbl, ShortType(r.type)); lbl->Release(); }
            if (IInspectable* host = FindInRow(fe, L"PFieldBoxes")) {
                const std::vector<std::wstring> vals = SplitFields(seed, r.fields.size());
                void* pnl = nullptr; void* kids = nullptr;
                if (SUCCEEDED(host->QueryInterface(DevToolsIid<DevToolsXC::IPanel>(), &pnl)) && pnl &&
                    SUCCEEDED(DevToolsPanelGetChildren(pnl, reinterpret_cast<IInspectable**>(&kids))) && kids)
                {
                    for (size_t i = 0; i < r.fields.size(); ++i) {
                        IInspectable* col = DevToolsActivate(L"Microsoft.UI.Xaml.Controls.StackPanel");
                        IInspectable* cap = NewTextBlock(r.fields[i]);
                        IInspectable* bx  = DevToolsActivate(L"Microsoft.UI.Xaml.Controls.TextBox");
                        if (!col || !bx) { if (col) col->Release(); if (cap) cap->Release(); if (bx) bx->Release(); continue; }
                        if (cap) {
                            void* ctb = nullptr;
                            if (SUCCEEDED(cap->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &ctb)) && ctb) {
                                DevToolsAbi<DevToolsXC::ITextBlock>(ctb)->put_FontSize(10.0);
                                if (g_palTertiary) DevToolsPutTextForeground(ctb, g_palTertiary);
                                reinterpret_cast<IUnknown*>(ctb)->Release();
                            }
                        }
                        void* tx = nullptr;
                        if (SUCCEEDED(bx->QueryInterface(DevToolsIid<DevToolsXC::ITextBox>(), &tx)) && tx) {
                            HSTRING h = MakeStr(i < vals.size() ? vals[i] : std::wstring());
                            DevToolsPutTextBoxText(tx, h); WindowsDeleteString(h);
                            reinterpret_cast<IUnknown*>(tx)->Release();
                        }
                        void* bctl = nullptr;
                        if (SUCCEEDED(bx->QueryInterface(DevToolsIid<DevToolsXC::IControl>(), &bctl)) && bctl) {
                            DevToolsPutControlFontSize(bctl, 12.0);
                            reinterpret_cast<IUnknown*>(bctl)->Release();
                        }
                        void* bfe = nullptr;
                        if (SUCCEEDED(bx->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &bfe)) && bfe) {
                            DevToolsPutWidth(bfe, 64.0);
                            reinterpret_cast<IUnknown*>(bfe)->Release();
                        }
                        SetAutomationTextOn(bx, i == 0 ? (path + L" editor") : (path + L" " + r.fields[i]),
                                            path + L" " + r.fields[i], L"");
                        void* cpnl = nullptr; void* ckids = nullptr;
                        if (SUCCEEDED(col->QueryInterface(DevToolsIid<DevToolsXC::IPanel>(), &cpnl)) && cpnl &&
                            SUCCEEDED(DevToolsPanelGetChildren(cpnl, reinterpret_cast<IInspectable**>(&ckids))) && ckids)
                        {
                            if (cap) DevToolsVecAppend(reinterpret_cast<IInspectable*>(ckids), cap);
                            DevToolsVecAppend(reinterpret_cast<IInspectable*>(ckids), bx);
                            reinterpret_cast<IUnknown*>(ckids)->Release();
                        }
                        if (cpnl) reinterpret_cast<IUnknown*>(cpnl)->Release();
                        DevToolsVecAppend(reinterpret_cast<IInspectable*>(kids), col);
                        if (edit >= 0) {
                            void* bui = nullptr;
                            if (SUCCEEDED(bx->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &bui)) && bui) {
                                KindApplySink* fs = new KindApplySink(); fs->Init(ctx);
                                __int64 tok = 0;
                                DevToolsAddLostFocus(bui, fs, &tok);
                                KindKeySink* ks = new KindKeySink(); ks->Init(ctx);
                                __int64 ktok = 0;
                                DevToolsAddPreviewKeyDown(bui, ks, &ktok);
                                reinterpret_cast<IUnknown*>(bui)->Release();
                            }
                        }
                        g_fieldBoxes.push_back({ path, i, bx });
                        col->Release();
                    }
                    reinterpret_cast<IUnknown*>(kids)->Release();
                }
                if (pnl) reinterpret_cast<IUnknown*>(pnl)->Release();
                host->Release();
            }
        } else {
            if (IInspectable* e = FindInRow(fe, L"PEdTextLbl"))
                { SetTextOn(e, ek == L"number" ? L"NUMBER" : L"VALUE"); e->Release(); }
            if (IInspectable* e = FindInRow(fe, L"PEdit")) {
                void* tx = nullptr;
                if (SUCCEEDED(e->QueryInterface(DevToolsIid<DevToolsXC::ITextBox>(), &tx)) && tx) {
                    // A single-line box keeps only the first line, so a multi-line value gets a multi-line editor.
                    const bool multiline = DevToolsEditText::IsMultiline(seed);
                    DevToolsPutTextBoxMultiline(tx, multiline);
                    void* efe = nullptr;
                    if (SUCCEEDED(e->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &efe)) && efe) {
                        DevToolsAbi<DevToolsX::IFrameworkElement>(efe)->put_MaxHeight(multiline ? 160.0 : INFINITY);
                        reinterpret_cast<IUnknown*>(efe)->Release();
                    }
                    HSTRING h = MakeStr(seed); DevToolsPutTextBoxText(tx, h); WindowsDeleteString(h);
                    reinterpret_cast<IUnknown*>(tx)->Release();
                }
                SetPlaceholderOn(e, RowEditPlaceholder(r));
                e->Release();
            }
            if (edit >= 0) {
                if (IInspectable* ed = FindInRow(fe, L"PEdit")) {
                    void* edUi = nullptr; void* box = nullptr;
                    ed->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &edUi);
                    ed->QueryInterface(DevToolsIid<DevToolsXC::ITextBox>(), &box);
                    if (edUi && box) {
                        LostFocusSink* commit = new LostFocusSink();
                        commit->Init(g_selWire, path, r.type, seed, reinterpret_cast<IInspectable*>(box));
                        __int64 mtok = 0;
                        const HRESULT hrM = DevToolsAddLostFocus(edUi, commit, &mtok);
                        if (FAILED(hrM))
                            DevToolsOverlayLog(L"route1.props row '%s' commit wiring FAILED hr=0x%08X -- it will not commit",
                                          path.c_str(), hrM);
                    } else {
                        DevToolsOverlayLog(L"route1.props MISS: editable row '%s' expansion has no editor -- it will never commit",
                                      path.c_str());
                    }
                    ed->Release();
                }
            }
            wireKeys(L"PEdit", DevToolsEditText::IsMultiline(seed));
        }
        break;
    }
    }

    NameTheEditor(fe, path, primary);
}
