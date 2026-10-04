// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsWindow.cpp; included into that translation unit, not compiled on its own.

// Expansion UI owns retained refs and event sinks for the realized row lifetime; stale generation guards callbacks.
static void PopulateExpansionInto(void* fe, const DevToolsCardRow& r, const std::wstring& path, int edit)
{
    const DevToolsKind current = CurrentKindOf(r, path);
    const DevToolsKind shown   = StagedOrCurrentKind(r, path);
    const bool    staged  = (PendingKindFind(path) != nullptr) && (shown != current);

    KindCtx ctx;
    ctx.row = std::make_shared<const DevToolsCardRow>(r);
    ctx.path = path;
    ctx.edit = edit;
    ctx.rowFe = g_expansionRowFe;
    ctx.expFe = fe;

    if (IInspectable* frame = FindInRow(fe, L"PExpFrame")) {
        SetAutomationTextOn(frame, path + L" expansion", path + L" expansion", L"");
        if (edit >= 0) {
            IInspectable* kindEl = FindInRow(fe, L"PKind");
            void* frameUi = nullptr; void* kindUi = nullptr;
            if (kindEl) kindEl->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &kindUi);
            bool wired = false;
            if (kindUi && SUCCEEDED(frame->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &frameUi)) && frameUi) {
                ExpBackgroundFocusSink* bs = new ExpBackgroundFocusSink(); bs->Init(kindUi);
                __int64 tok = 0;
                const HRESULT hr = DevToolsAddPointerPressed(frameUi, bs, &tok);
                wired = SUCCEEDED(hr);
                if (!wired)
                    DevToolsOverlayLog(L"route1.props expansion background for '%s' add_PointerPressed FAILED hr=0x%08X"
                                  L" -- clicking off the field will not commit", path.c_str(), hr);
                reinterpret_cast<IUnknown*>(frameUi)->Release();
            }
            if (kindUi && !wired) reinterpret_cast<IUnknown*>(kindUi)->Release();
            if (kindEl) kindEl->Release();
        }
        frame->Release();
    }

    if (IInspectable* kb = FindInRow(fe, L"PKind")) {
        std::vector<std::wstring> labels;
        for (int i = 0; i < kDevToolsKindCount; ++i) labels.push_back(KindLabel((DevToolsKind)i));
        FillCombo(kb, labels);
        // {x:Bind} STAYS IN THE LIST, GREYED, WITH ITS REASON ON ITS FACE. Each item is named for what
        // CHOOSING it does, per row, so thirty realized rows do not put thirty elements called "Literal" in a
        // UIA namespace this window shares with the overlay.
        NameKindItems(kb, path);
        void* sel = nullptr;
        if (SUCCEEDED(kb->QueryInterface(DevToolsIid<DevToolsXCP::ISelector>(), &sel)) && sel) {
            // SELECT BEFORE SUBSCRIBING, for the same reason the toggle is seeded first: put_SelectedIndex
            // raises SelectionChanged, and a handler attached first would stage a kind change the user never
            // made the moment the expansion was built.
            DevToolsAbi<DevToolsXCP::ISelector>(sel)->put_SelectedIndex((int)shown);
            if (edit >= 0) {
                KindSelectSink* ks = new KindSelectSink();
                ks->Init(ctx, sel);
                __int64 tok = 0;
                const HRESULT hr = DevToolsAddSelectionChanged(sel, ks, &tok);
                if (FAILED(hr))
                    DevToolsOverlayLog(L"route1.props kind picker for '%s' add_SelectionChanged FAILED hr=0x%08X -- it will not stage",
                                  path.c_str(), hr);
            }
            reinterpret_cast<IUnknown*>(sel)->Release();
        }
        void* ctl = nullptr;
        if (SUCCEEDED(kb->QueryInterface(DevToolsIid<DevToolsXC::IControl>(), &ctl)) && ctl) {
            DevToolsPutIsEnabled(ctl, edit >= 0);
            reinterpret_cast<IUnknown*>(ctl)->Release();
        }
        SetAutomationTextOn(kb, path + L" kind", path + L" kind", L"");
        kb->Release();
    }

    PopulateKindEditor(fe, r, path, edit, shown, ctx);

    // Require explicit apply when a kind transition loses a binding/link or has no typable seed
// to restore. Reversible seeded changes need only the normal commit gesture.
    const std::wstring restoreSeed = RowEditSeed(r);
    const bool nothingToRestoreFrom = restoreSeed.empty();
    const bool bindingReplacement = shown == DevToolsKind::Binding;
    const bool irreversible = bindingReplacement ||
        (staged && (KindChangeIsIrreversible(current, shown) || nothingToRestoreFrom));
    if (IInspectable* hint = FindInRow(fe, L"PStageHint")) {
        ShowEl(hint, staged && !irreversible);
        if (staged && !irreversible) {
            SetTextOn(hint, L"Enter applies, Esc cancels. Undoable - Restore original puts "
                          + restoreSeed + L" back.");
            SetAutomationTextOn(hint, path + L" staged hint", path + L" staged hint", L"");
        }
        hint->Release();
    }
    // An existing {Binding} row is not being changed until its path or mode is: the replacement warning and its
    // Apply wait for that edit, and go away again if the edit is undone.
    const bool bindingIdle = bindingReplacement && current == DevToolsKind::Binding && !staged;
    if (IInspectable* sb = FindInRow(fe, L"PStaged")) {
        ShowEl(sb, irreversible && !bindingIdle);
        if (bindingIdle && edit >= 0) {
            try {
                winrt::Windows::Foundation::IInspectable stagedObject{ nullptr };
                winrt::copy_from_abi(stagedObject, sb);
                const auto stagedBox = stagedObject.as<DevToolsX::UIElement>();
                IInspectable* pathEl = FindInRow(fe, L"PPath");
                IInspectable* modeEl = FindInRow(fe, L"PMode");
                if (pathEl && modeEl) {
                    winrt::Windows::Foundation::IInspectable pathObject{ nullptr }, modeObject{ nullptr };
                    winrt::copy_from_abi(pathObject, pathEl);
                    winrt::copy_from_abi(modeObject, modeEl);
                    const auto pathBox = pathObject.as<DevToolsXC::TextBox>();
                    const auto modeBox = modeObject.as<DevToolsXCP::Selector>();
                    const winrt::hstring pathAtOpen = pathBox.Text();
                    const int32_t modeAtOpen = modeBox.SelectedIndex();
                    auto sync = [stagedBox, pathBox, modeBox, pathAtOpen, modeAtOpen]() {
                        const bool editing = pathBox.Text() != pathAtOpen || modeBox.SelectedIndex() != modeAtOpen;
                        stagedBox.Visibility(editing ? DevToolsX::Visibility::Visible : DevToolsX::Visibility::Collapsed);
                    };
                    pathBox.TextChanged([sync](auto&&, auto&&) { sync(); });
                    modeBox.SelectionChanged([sync](auto&&, auto&&) { sync(); });
                }
                if (pathEl) pathEl->Release();
                if (modeEl) modeEl->Release();
            } catch (...) {
                ShowEl(sb, true);
            }
        }
        if (irreversible) {
            std::wstring head, body;
            if (bindingReplacement) {
                head = L"Replace the entire binding, not just its path or mode.";
                body = DevToolsBindingInstall_ReplacementWarning();
                if (current == DevToolsKind::XBind)
                    body += L" The authored x:Bind is compiled and read-only; this is a separate classic binding, not an edit to generated code.";
            } else if (current == DevToolsKind::XBind || current == DevToolsKind::Binding) {
                head = L"This cannot be undone.";
                body = (current == DevToolsKind::XBind)
                     ? std::wstring(L"The compiled x:Bind ends for this run. Nothing in DevTools re-creates "
                                    L"one - restarting the app is the only way back.")
                     : ((r.authored.empty() ? std::wstring(L"The binding") : r.authored)
                        + L" will be gone. Nothing in DevTools can re-attach it; restart the app.");
            } else if (KindIsResource(current) && !KindIsResource(shown)) {
                head = L"This cannot be undone.";
                body = L"This value stops following " + (r.authoredKey.empty() ? std::wstring(L"the resource") : r.authoredKey)
                     + L". Nothing in DevTools can re-attach it; restart the app.";
            } else if (shown == DevToolsKind::Binding) {
                head = L"Restore original will not be offered afterwards.";
                body = L"Grafting a live {Binding} replaces this value and DevTools stops remembering what it "
                       L"was. It applies to the running app only; your .xaml is not changed.";
            } else {
                head = L"Restore original will not be offered afterwards.";
                body = L"This property's current value has no text form DevTools can put back, so there is "
                       L"nothing for an undo to restore it from.";
            }
            if (IInspectable* e = FindInRow(fe, L"PStagedHead")) { SetTextOn(e, head); e->Release(); }
            if (IInspectable* e = FindInRow(fe, L"PStagedBody")) { SetTextOn(e, body); e->Release(); }
        }
        if (IInspectable* b = FindInRow(fe, L"PApply")) {
            // THE FACE AND THE ACCESSIBLE NAME SAY THE SAME THING. A button reading "Replace the x:Bind" whose
            // UIA name is "Apply Text" is a row that lies to exactly the users who cannot see the face.
            const std::wstring label = bindingReplacement ? L"Replace entire binding"
                : CommitLabelFor(current, shown, nothingToRestoreFrom);
            void* cc = nullptr;
            if (SUCCEEDED(b->QueryInterface(DevToolsIid<DevToolsXC::IContentControl>(), &cc)) && cc) {
                IInspectable* boxed = nullptr;
                if (SUCCEEDED(BoxString(label, &boxed)) && boxed) { DevToolsPutContent(cc, boxed); boxed->Release(); }
                reinterpret_cast<IUnknown*>(cc)->Release();
            }
            SetAutomationTextOn(b, label + L" on " + path, L"", L"");
            void* bb = nullptr;
            if (edit >= 0 && SUCCEEDED(b->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), &bb)) && bb) {
                KindApplySink* as = new KindApplySink(); as->Init(ctx);
                __int64 tok = 0;
                const HRESULT hr = DevToolsAddClick(bb, as, &tok);
                if (FAILED(hr))
                    DevToolsOverlayLog(L"route1.props Apply for '%s' add_Click FAILED hr=0x%08X -- a staged change cannot commit",
                                  path.c_str(), hr);
                reinterpret_cast<IUnknown*>(bb)->Release();
            }
            b->Release();
        }
        if (IInspectable* b = FindInRow(fe, L"PCancel")) {
            SetAutomationTextOn(b, L"Cancel " + path, L"", L"");
            void* bb = nullptr;
            if (edit >= 0 && SUCCEEDED(b->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), &bb)) && bb) {
                KindCancelSink* cs = new KindCancelSink(); cs->Init(ctx);
                __int64 tok = 0;
                DevToolsAddClick(bb, cs, &tok);
                reinterpret_cast<IUnknown*>(bb)->Release();
            }
            b->Release();
        }
        sb->Release();
    }

    if (IInspectable* cf = FindInRow(fe, L"PConfirm")) {
        const PendingWrite* pend = PendingFind(path);
        ShowEl(cf, pend != nullptr);
        if (pend) {
            const bool link = ResourceBacked(path) && !LinkLost(path);
            const std::wstring ask = (link ? L"Replace resource with " : L"Replace binding with ")
                                   + QuoteForPrompt(pend->value) + L"?";
            if (IInspectable* ct = FindInRow(fe, L"PConfirmText")) { SetTextOn(ct, ask); ct->Release(); }
        }
        void* bb = nullptr;
        if (edit >= 0 && SUCCEEDED(cf->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), &bb)) && bb) {
            ConfirmSink* cs = new ConfirmSink();
            cs->Init(g_selWire, path);
            __int64 tok = 0;
            DevToolsAddClick(bb, cs, &tok);
            reinterpret_cast<IUnknown*>(bb)->Release();
        }
        cf->Release();
    }

    const std::wstring* seedVal = DirtyFind(path);
    const bool edited = seedVal != nullptr;
    if (IInspectable* e = FindInRow(fe, L"PEditedHdr")) { ShowEl(e, edited); e->Release(); }
    if (IInspectable* e = FindInRow(fe, L"PEditedWas")) {
        if (edited) {
            SetTextOn(e, L"was  " + (seedVal->empty() ? std::wstring(L"(not set)") : *seedVal));
            SetAutomationTextOn(e, path + L" original", path + L" original", L"");
        }
        ShowEl(e, edited);
        e->Release();
    }
    if (IInspectable* rs = FindInRow(fe, L"PRestore")) {
        const bool wirable = edit >= 0 && g_ctx.writeFn;
        ShowEl(rs, wirable && edited && !BindingLost(path));
        SetAutomationTextOn(rs, L"Restore " + path, L"", L"");
        void* bb = nullptr;
        if (wirable && SUCCEEDED(rs->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), &bb)) && bb) {
            UndoSink* us = new UndoSink();
            us->Init(g_selWire, path, r.type);
            __int64 tok = 0;
            DevToolsAddClick(bb, us, &tok);
            reinterpret_cast<IUnknown*>(bb)->Release();
        }
        rs->Release();
    }
    if (IInspectable* cl = FindInRow(fe, L"PClear")) {
        // Offered only where it can do something: on a row that already IS its inherited value ClearProperty
        // is a no-op, and an affordance that cannot do what it implies is worse than none.
        const bool canClear = edit >= 0 && g_ctx.clearFn &&
                              (SourceIsSetHere(r.source) || !r.authoredKind.empty() || edited ||
                               SessionAssignFind(path) != nullptr);
        ShowEl(cl, canClear);
        SetAutomationTextOn(cl, L"Clear value of " + path, L"", L"");
        void* bb = nullptr;
        if (canClear && SUCCEEDED(cl->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), &bb)) && bb) {
            ResetSink* rk = new ResetSink();
            rk->Init(g_selWire, path);
            __int64 tok = 0;
            DevToolsAddClick(bb, rk, &tok);
            reinterpret_cast<IUnknown*>(bb)->Release();
        }
        cl->Release();
    }

    bool anyWitness = false;

    if (IInspectable* w = FindInRow(fe, L"PWitAuthored")) {
        std::wstring line;
        if (r.authoredKind != L"literal" && !r.authored.empty()) {
            const std::wstring inner = MarkupInner(r.authored);
            // Prefer the FULL inner text over `authoredKey`: in the expansion there is room, and the key
            // alone drops "Mode=TwoWay", which is exactly what a reader of a binding row came for.
            const std::wstring body = inner.empty() ? (r.authoredKey.empty() ? r.authored : r.authoredKey) : inner;
            line = std::wstring(OriginChip(r)) + L"  \u00B7  " + body;
        } else if (r.authoredKind == L"literal") {
            line = L"LITERAL  \u00B7  " + r.authored;
        } else if (!r.binding.empty()) {
            // Reconstructed from the LIVE element rather than read from source, which only ever happens for a
            // runtime {Binding}. Said as such rather than presented as authorship we do not have.
            const std::wstring inner = MarkupInner(r.binding);
            line = L"BIND  \u00B7  " + (inner.empty() ? r.binding : inner) + L"   (read from the live element)";
        } else if (r.source == L"Binding") {
            line = L"Binding expression unavailable.";
        }
        if (IInspectable* t = FindInRow(fe, L"POrigin")) {
            SetTextOn(t, line);
            SetAutomationTextOn(t, path + L" origin", path + L" origin", L"");
            t->Release();
        }
        ShowEl(w, !line.empty());
        anyWitness = anyWitness || !line.empty();
        w->Release();
    }

    // WITNESS 2 -- DevTools, this session. CATEGORICALLY WEAKER than the other two and rendered as such: a
    // DASHED-weight rule (a 1px tertiary stroke against the others' divider stroke) plus the limitation said
    // in words. Setting a resource is an ASSIGN, not a rebind (DevToolsTap.cpp:1024-1029) -- the runtime cannot
    // recover the identity a value came from, so the pane can name this key only because it remembers doing
    // it, and a fresh read cannot confirm it. It must never read as an ordinary chain entry.
    if (IInspectable* w = FindInRow(fe, L"PWitSession")) {
        std::wstring text, note;
        if (const SessionAssign* sa = SessionAssignFind(path)) {
            text = sa->detail;
            note = KindIsResource(sa->kind)
                ? L"assigned by ResolveResource - an ASSIGN, not a rebind. The element no longer reports this "
                  L"key, and this line dies when the selection changes."
                : L"grafted with SetBinding onto the running app. Your .xaml still says what it always said, "
                  L"and this line dies when the selection changes.";
        } else if (BindingLost(path)) {
            text = L"the binding was replaced with a literal";
            note = L"DevTools cannot re-attach it. Restart the app to get it back.";
        } else if (LinkLost(path)) {
            text = L"no longer follows " + (r.authoredKey.empty() ? r.authored : r.authoredKey);
            note = L"DevTools cannot re-attach it. Restart the app to get it back.";
        }
        if (IInspectable* t = FindInRow(fe, L"PSessionText")) {
            SetTextOn(t, text);
            SetAutomationTextOn(t, path + L" session note", path + L" session note", L"");
            t->Release();
        }
        if (IInspectable* n = FindInRow(fe, L"PSessionNote")) { SetTextOn(n, note); n->Release(); }
        ShowEl(w, !text.empty());
        anyWitness = anyWitness || !text.empty();
        w->Release();
    }

    // WITNESS 3 -- the runtime's precedence chain. Only where there is more than one link: a one-entry chain
    // says nothing the other witnesses do not.
    if (IInspectable* w = FindInRow(fe, L"PWitChain")) {
        const bool have = r.chain.size() > 1;
        if (have) {
            if (IInspectable* who = FindInRow(fe, L"PChainWho")) {
                SetTextOn(who, KindIsResource(current)
                    ? L"the runtime's precedence chain - a resolved resource keeps no key"
                    : (current == DevToolsKind::XBind
                        ? L"the runtime's precedence chain - a compiled binding lands as a plain local value"
                        : L"the runtime's precedence chain"));
                who->Release();
            }
            if (IInspectable* cb = FindInRow(fe, L"PChain")) {
                void* kids = nullptr; void* pnl = nullptr;
                if (SUCCEEDED(cb->QueryInterface(DevToolsIid<DevToolsXC::IPanel>(), &pnl)) && pnl &&
                    SUCCEEDED(DevToolsPanelGetChildren(pnl, reinterpret_cast<IInspectable**>(&kids))) && kids) {
                    for (size_t k = 0; k < r.chain.size(); ++k) {
                        IInspectable* tb = DevToolsActivate(L"Microsoft.UI.Xaml.Controls.TextBlock");
                        if (!tb) continue;
                        void* tbi = nullptr;
                        if (SUCCEEDED(tb->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &tbi)) && tbi) {
                            const DevToolsCardChainEntry& ce = r.chain[k];
                            std::wstring cv = ce.value;
                            const bool handleLike = cv.size() >= 10 &&
                                cv.find_first_not_of(L"0123456789") == std::wstring::npos;
                            if (handleLike) cv = ShortType(r.valueType.empty() ? r.type : r.valueType);
                            std::wstring line = (ce.winner ? std::wstring(L"\u25B8 ") : std::wstring(L"   "))
                                              + ce.source + L"  " + cv;
                            if (!ce.file.empty() && ce.line)
                                line += L"   " + SourceFileName(ce.file) + L":" + std::to_wstring(ce.line);
                            HSTRING h = MakeStr(line);
                            DevToolsPutText(tbi, h); WindowsDeleteString(h);
                            DevToolsAbi<DevToolsXC::ITextBlock>(tbi)->put_FontSize(11.0);
                            DevToolsPutTextTrimming(tbi, (int)DevToolsX::TextTrimming::CharacterEllipsis);
                            reinterpret_cast<IUnknown*>(tbi)->Release();
                        }
                        DevToolsVecAppend(reinterpret_cast<IInspectable*>(kids), tb);
                        tb->Release();
                    }
                    reinterpret_cast<IUnknown*>(kids)->Release();
                }
                if (pnl) reinterpret_cast<IUnknown*>(pnl)->Release();
                cb->Release();
            }
        }
        ShowEl(w, have);
        anyWitness = anyWitness || have;
        w->Release();
    }
    if (IInspectable* h = FindInRow(fe, L"PDerivedHdr")) { ShowEl(h, anyWitness); h->Release(); }

    if (IInspectable* blk = FindInRow(fe, L"PChildren")) {
        if (!r.children.empty()) {
            void* blkPnl = nullptr;
            void* kids = nullptr;
            if (SUCCEEDED(blk->QueryInterface(DevToolsIid<DevToolsXC::IPanel>(), &blkPnl)) && blkPnl &&
                SUCCEEDED(DevToolsPanelGetChildren(blkPnl, reinterpret_cast<IInspectable**>(&kids))) && kids) {
                std::vector<int> cedits;
                for (size_t ri = 0; ri < g_propRows.size(); ++ri) {
                    if (g_propRows[ri].name != r.name) continue;
                    if (ri < g_paneEditMap.childIdx.size()) cedits = g_paneEditMap.childIdx[ri];
                    break;
                }
                cedits.resize(r.children.size(), -1);
                void* savedRowFe = g_expansionRowFe;
                for (size_t c = 0; c < r.children.size(); ++c) {
                    const int cedit = cedits[c];
                    if (void* kfe = BuildRowElement(r.children[c], path + L"." + r.children[c].name, cedit)) {
                        void* kui = nullptr;
                        if (SUCCEEDED(reinterpret_cast<IUnknown*>(kfe)->QueryInterface(
                                DevToolsIid<DevToolsX::IUIElement>(), &kui)) && kui) {
                            DevToolsVecAppend(reinterpret_cast<IInspectable*>(kids),
                                         reinterpret_cast<IInspectable*>(kui));
                            reinterpret_cast<IUnknown*>(kui)->Release();
                        }
                        reinterpret_cast<IUnknown*>(kfe)->Release();
                    }
                }
                g_expansionRowFe = savedRowFe;
                reinterpret_cast<IUnknown*>(kids)->Release();
                ShowEl(blk, true);
            } else {
                DevToolsOverlayLog(L"route1.props MISS: row '%s' has children but no block to put them in",
                              path.c_str());
            }
        }
        blk->Release();
    }

    const bool bound = (shown == DevToolsKind::XBind || shown == DevToolsKind::Binding);
    if (bound) { PostWalk(path); PostDiagnose(path); }
    const WalkAnswer* answer = bound ? WalkFind(path) : nullptr;
    DevToolsPathWalkView wv;
    if (answer && answer->done) wv = DevToolsPathWalk_FromJson(answer->json, DevToolsBindingRelay_AgentPresent());
    const DiagAnswer* diag = bound ? DiagFind(path) : nullptr;
    DevToolsBindingSummary sum = (diag && diag->done) ? DevToolsBindingRow_Summary(diag->json) : DevToolsBindingSummary{};
    // The XAML declares a binding but the element no longer has one: an edit replaced it with a local value.
    const bool replaced = diag && diag->done &&
        (BindingLost(path) || sum.tone == DevToolsBindingTone::Unknown || sum.tone == DevToolsBindingTone::NotBound);
    if (replaced) {
        sum.tone = DevToolsBindingTone::Warning;
        sum.status = L"Replaced by a local value";
        sum.reason = L"until you restore it or restart the app";
        DevToolsJson act;
        if (DevToolsJsonParse(diag->action, act) && act.IsObject() && act.GetString(L"state") != L"restored")
            sum.reason = L"Restore failed: " + act.GetString(L"reason");
    }
    // With the managed answer in hand the labelled lines say it all; the native walk's prose is the fallback.
    const bool knows = diag && diag->done && sum.tone != DevToolsBindingTone::Unknown;
    const bool pending = (answer && !answer->done) || (diag && !diag->done);
    const bool broken = sum.tone == DevToolsBindingTone::Broken;
    if (knows || pending) wv.steps.clear();

    if (IInspectable* blk = FindInRow(fe, L"PDiagBlock")) {
        ShowEl(blk, bound && (knows || wv.show || pending));
        SetAutomationTextOn(blk, path + L" binding", path + L" binding", L"");
        blk->Release();
    }
    if (IInspectable* g = FindInRow(fe, L"PBindSummary")) { ShowEl(g, knows); g->Release(); }
    if (knows) {
        auto put = [&](const wchar_t* name, const std::wstring& text) {
            if (IInspectable* el = FindInRow(fe, name)) { SetTextOn(el, text); ShowEl(el, !text.empty()); el->Release(); }
        };
        put(L"PBindStatus", broken ? std::wstring() : sum.status);
        put(L"PBindStatusBad", broken ? sum.status : std::wstring());
        put(L"PBindReason", sum.reason);
        // A replaced binding has no live source or path; the lines that would be blank are hidden instead.
        put(L"PBindSource", replaced ? std::wstring() : (sum.source.empty() ? std::wstring(L"(unknown)") : sum.source));
        put(L"PBindPath", replaced ? std::wstring() : sum.pathMode);
        put(L"PBindValue", replaced ? std::wstring() : (sum.value.empty() ? std::wstring(broken ? L"(none)" : L"(empty)") : sum.value));
    }
    {
        // Restore is offered once the binding has been replaced; on a working binding it would do nothing.
        const bool offer = bound && g_ctx.bindFn && replaced;
        if (IInspectable* row = FindInRow(fe, L"PBindRestoreRow")) { ShowEl(row, offer); row->Release(); }
        if (IInspectable* note = FindInRow(fe, L"PBindRestoreNote")) { ShowEl(note, offer && shown == DevToolsKind::XBind); note->Release(); }
        if (IInspectable* b = FindInRow(fe, L"PBindRestore")) {
            SetAutomationTextOn(b, L"Restore the binding on " + path, L"", L"");
            void* bb = nullptr;
            if (offer && SUCCEEDED(b->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), &bb)) && bb) {
                BindRestoreSink* rs = new BindRestoreSink();
                rs->Init(path, shown == DevToolsKind::XBind);
                __int64 tok = 0;
                DevToolsAddClick(bb, rs, &tok);
                reinterpret_cast<IUnknown*>(bb)->Release();
            }
            b->Release();
        }
    }
    if (IInspectable* q = FindInRow(fe, L"PDiagQ")) {
        const std::wstring qline = knows ? std::wstring()
            : (pending ? std::wstring(DevToolsBindingRow_PendingText()) : wv.qHead + wv.qEmphasis + wv.qTail);
        SetTextOn(q, qline);
        SetAutomationTextOn(q, path + L" binding question", path + L" binding question", L"");
        ShowEl(q, !qline.empty());
        q->Release();
    }
    if (IInspectable* a = FindInRow(fe, L"PDiag")) {
        // While the managed answer is on its way, the native walk's prose would flash and then be replaced.
        const std::wstring text = (knows || pending) ? std::wstring() : wv.answer;
        SetTextOn(a, text);
        SetAutomationTextOn(a, path + L" binding answer", path + L" binding answer", L"");
        ShowEl(a, !text.empty());
        a->Release();
    }
    if (IInspectable* host = FindInRow(fe, L"PWalkLines")) {
        void* pnl = nullptr; void* kids = nullptr;
        if (SUCCEEDED(host->QueryInterface(DevToolsIid<DevToolsXC::IPanel>(), &pnl)) && pnl &&
            SUCCEEDED(DevToolsPanelGetChildren(pnl, reinterpret_cast<IInspectable**>(&kids))) && kids) {
            DevToolsVecClear(reinterpret_cast<IInspectable*>(kids));
            for (const DevToolsPathWalkStep& s : wv.steps) {
                IInspectable* tb = DevToolsActivate(L"Microsoft.UI.Xaml.Controls.TextBlock");
                if (!tb) continue;
                void* tbi = nullptr;
                if (SUCCEEDED(tb->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &tbi)) && tbi) {
                    std::wstring line = (s.stop ? std::wstring(DevToolsPathWalk_StopMark()) + L" "
                                                : std::wstring(L"   "))
                                      + s.path + L"   " + s.value;
                    if (!s.type.empty()) line += L"  " + s.type;
                    HSTRING h = MakeStr(line);
                    DevToolsPutText(tbi, h); WindowsDeleteString(h);
                    DevToolsAbi<DevToolsXC::ITextBlock>(tbi)->put_FontSize(11.0);
                    DevToolsPutTextFontWeight(tbi, s.stop ? 700 : 400);
                    DevToolsPutTextTrimming(tbi, (int)DevToolsX::TextTrimming::CharacterEllipsis);
                    reinterpret_cast<IUnknown*>(tbi)->Release();
                }
                if (s.after) {
                    void* ui = nullptr;
                    if (SUCCEEDED(tb->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) && ui) {
                        DevToolsAbi<DevToolsX::IUIElement>(ui)->put_Opacity(0.6);
                        reinterpret_cast<IUnknown*>(ui)->Release();
                    }
                }
                DevToolsVecAppend(reinterpret_cast<IInspectable*>(kids), tb);
                tb->Release();
            }
            reinterpret_cast<IUnknown*>(kids)->Release();
        }
        if (pnl) reinterpret_cast<IUnknown*>(pnl)->Release();
        host->Release();
    }
    if (IInspectable* frame = FindInRow(fe, L"PWalk")) { ShowEl(frame, !wv.steps.empty()); frame->Release(); }
    if (IInspectable* d = FindInRow(fe, L"PWalkAgentNote")) {
        if (wv.revealException)
            SetTextOn(d, L"The exception's type and message live inside a TargetInvocationException and do not "
                         L"survive the ABI. A managed binding host loaded before app startup can read the "
                         L"inner exception directly. Everything else on this row works "
                         L"without it.");
        ShowEl(d, wv.revealException && !knows);
        d->Release();
    }
}

static void RebuildExpansion(void* rowFe, const DevToolsCardRow& r, const std::wstring& path, int edit)
{
    if (!rowFe) return;
    if (IInspectable* host = FindInRow(rowFe, L"PExpand")) {
        void* pnl = nullptr; void* kids = nullptr;
        if (SUCCEEDED(host->QueryInterface(DevToolsIid<DevToolsXC::IPanel>(), &pnl)) && pnl &&
            SUCCEEDED(DevToolsPanelGetChildren(pnl, reinterpret_cast<IInspectable**>(&kids))) && kids)
        {
            DevToolsVecClear(reinterpret_cast<IInspectable*>(kids));
            reinterpret_cast<IUnknown*>(kids)->Release();
        }
        if (pnl) reinterpret_cast<IUnknown*>(pnl)->Release();
        host->Release();
    }
    ReleaseFieldBoxes(path);
    if (EnsureExpansion(rowFe, r, path, edit)) ApplyRowExpansionState(rowFe, path, true);
}


static bool EnsureExpansion(void* rowFe, const DevToolsCardRow& r, const std::wstring& path, int edit)
{
    IInspectable* host = FindInRow(rowFe, L"PExpand");
    if (!host) return false;
    void* pnl = nullptr;
    void* kids = nullptr;
    bool ok = false;
    if (SUCCEEDED(host->QueryInterface(DevToolsIid<DevToolsXC::IPanel>(), &pnl)) && pnl &&
        SUCCEEDED(DevToolsPanelGetChildren(pnl, reinterpret_cast<IInspectable**>(&kids))) && kids)
    {
        unsigned int n = 0;
        DevToolsVecGetSize(reinterpret_cast<IInspectable*>(kids), &n);
        if (n > 0) {
            ok = true;
        } else if (IInspectable* tmpl = ExpansionTemplateForRow(r, edit)) {
            void* dt = nullptr;
            if (SUCCEEDED(tmpl->QueryInterface(DevToolsIid<DevToolsX::IDataTemplate>(), &dt)) && dt) {
                IInspectable* content = nullptr;
                const HRESULT hr = DevToolsTemplateLoadContent(dt, &content);
                reinterpret_cast<IUnknown*>(dt)->Release();
                // LoadContent can answer S_OK with nothing, and an expansion that never materialized
                // reads exactly like a row with nothing to show.
                if (FAILED(hr) || !content) {
                    DevToolsOverlayLog(L"route1.props expansion LoadContent produced nothing for '%s' hr=0x%08X",
                                  path.c_str(), hr);
                } else {
                    void* expFe = nullptr;
                    if (SUCCEEDED(content->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &expFe)) && expFe) {
                        // PopulateExpansionInto reaches the row LINE (for the display value it repaints on
                        // commit) through this, because the expansion's own namescope stops at its root.
                        g_expansionRowFe = rowFe;
                        PopulateExpansionInto(expFe, r, path, edit);
                        g_expansionRowFe = nullptr;
                        DevToolsVecAppend(reinterpret_cast<IInspectable*>(kids),
                                     reinterpret_cast<IInspectable*>(expFe));
                        reinterpret_cast<IUnknown*>(expFe)->Release();
                        ok = true;
                    }
                    content->Release();
                }
            }
        } else {
            DevToolsOverlayLog(L"route1.props no expansion template for row '%s'", path.c_str());
        }
    }
    if (kids) reinterpret_cast<IUnknown*>(kids)->Release();
    if (pnl)  reinterpret_cast<IUnknown*>(pnl)->Release();
    host->Release();
    return ok;
}

// Choose the editor by live Visibility; templated collapsed editors still exist in the namescope.
static bool FocusExpansionEditor(void* rowFe, const std::wstring& path)
{
    static const wchar_t* const kFocusable[] =
        { L"PEdit", L"PToggle", L"PCombo", L"PHex", L"PResKey", L"PPath" };
    IInspectable* e = nullptr;
    for (const wchar_t* nm : kFocusable) {
        IInspectable* cand = FindInRow(rowFe, nm);
        if (!cand) continue;
        void* ui = nullptr;
        bool visible = false;
        if (SUCCEEDED(cand->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) && ui) {
            int vis = 1;
            if (SUCCEEDED(DevToolsGetVisibility(ui, &vis))) visible = (vis == 0);
            reinterpret_cast<IUnknown*>(ui)->Release();
        }
        if (visible && !e) { e = cand; continue; }
        cand->Release();
    }
    // A multi-field row has no templated editor at all -- its boxes are created per wire label -- so the
    // first box is the target and it is reachable only through the registry that created it.
    if (!e) {
        for (const FieldBoxRef& f : g_fieldBoxes)
            if (f.path == path && f.index == 0 && f.box) { e = f.box; e->AddRef(); break; }
    }
    if (!e) return false;
    void* ui = nullptr;
    bool moved = false;
    if (SUCCEEDED(e->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) && ui) {
        DevToolsFocus(ui, 2 /*Programmatic*/, &moved);
        reinterpret_cast<IUnknown*>(ui)->Release();
    }
    e->Release();
    // An editor that appeared but never took focus never raises LostFocus, so it would also never commit
    // -- a row that opened and looks ready and is not.
    if (!moved)
        DevToolsOverlayLog(L"route1.props editor for '%s' did not take focus -- it will not commit", path.c_str());
    return moved;
}

static void* BuildRowElement(const DevToolsCardRow& r, const std::wstring& path, int edit)
{
    IInspectable* tmpl = g_tmplRow;
    if (!tmpl) { DevToolsOverlayLog(L"route1.props no row template for '%s'", path.c_str()); return nullptr; }
    void* dt = nullptr;
    if (FAILED(tmpl->QueryInterface(DevToolsIid<DevToolsX::IDataTemplate>(), &dt)) || !dt) return nullptr;
    IInspectable* content = nullptr;
    const HRESULT hr = DevToolsTemplateLoadContent(dt, &content);
    reinterpret_cast<IUnknown*>(dt)->Release();
    // LoadContent can answer S_OK with nothing, and a row that never materialized reads exactly like
    // a fast one.
    if (FAILED(hr) || !content) {
        DevToolsOverlayLog(L"route1.props LoadContent produced nothing for '%s' hr=0x%08X", path.c_str(), hr);
        return nullptr;
    }
    void* fe = nullptr;
    if (SUCCEEDED(content->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) && fe)
        PopulateRowLine(fe, r, path, edit);
    content->Release();
    return fe;
}



struct PropRowFactory : DevToolsElementFactoryBase
{
    // Do not pool realized rows: sinks and element pointers are tied to the row data they were built for.
HRESULT STDMETHODCALLTYPE GetElement(void* args, void** element) noexcept override
    {
        if (element) *element = nullptr;
        if (!args || !element) return E_POINTER;
        IInspectable* data = nullptr;
        if (FAILED(DevToolsGetArgsData(args, &data)) || !data) return E_FAIL;
        int idx = -1;
        const HRESULT hrU = DevToolsUnboxInt32(data, &idx);
        data->Release();
        if (FAILED(hrU) || idx < 0 || (size_t)idx >= g_paneItems.size()) {
            DevToolsOverlayLog(L"route1.props factory bad index hr=0x%08X idx=%d items=%zu", hrU, idx, g_paneItems.size());
            return E_FAIL;
        }

        const DevToolsPaneItem& item = g_paneItems[(size_t)idx];
        void* fe = nullptr;
        if (item.kind == DevToolsPaneItemKind::GroupHeader) {
            void* dt = nullptr;
            if (!g_tmplHeader ||
                FAILED(g_tmplHeader->QueryInterface(DevToolsIid<DevToolsX::IDataTemplate>(), &dt)) || !dt) return E_FAIL;
            IInspectable* content = nullptr;
            const HRESULT hr = DevToolsTemplateLoadContent(dt, &content);
            reinterpret_cast<IUnknown*>(dt)->Release();
            if (FAILED(hr) || !content) {
                DevToolsOverlayLog(L"route1.props group caption produced nothing hr=0x%08X", hr);
                return E_FAIL;
            }
            if (SUCCEEDED(content->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) && fe)
                if (IInspectable* n = FindInRow(fe, L"PName")) { SetTextOn(n, item.caption); n->Release(); }
            content->Release();
        } else {
            const int edit = item.rowIndex < g_paneEditMap.rowIdx.size()
                           ? g_paneEditMap.rowIdx[item.rowIndex] : -1;
            fe = BuildRowElement(g_propRows[item.rowIndex], g_propRows[item.rowIndex].name, edit);
        }
        if (!fe) return E_FAIL;
        void* ui = nullptr;
        const HRESULT hrElem = reinterpret_cast<IUnknown*>(fe)->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui);
        reinterpret_cast<IUnknown*>(fe)->Release();
        if (FAILED(hrElem) || !ui) return E_FAIL;
        *element = ui;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE RecycleElement(void* /*args*/) noexcept override
    {
        return S_OK;
    }
};
static PropRowFactory g_propRowFactory;


static void BuildPaneItems(const std::vector<DevToolsCardRow>& rows)
{
    g_paneItems.clear();
    for (int g = 0; g < PropGroupCount(); ++g) {
        std::vector<size_t> idxs;
        for (size_t i = 0; i < rows.size(); ++i) {
            if (PropGroupIndexForView(rows[i]) != g) continue;
            // The filter matches name AND value AND authored text, so "#FF0000FF" finds every red thing on
            // the element and "margin" finds Margin. A non-match never enters the list, so it cannot be
            // "matched but not rendered" the way it could when rows were containers being hidden.
            if (!g_propFilterQuery.empty() && i < g_propRowLabels.size() &&
                g_propRowLabels[i].find(g_propFilterQuery) == std::wstring::npos) continue;
            idxs.push_back(i);
        }
        {
            std::vector<size_t> keep;
            for (size_t i : idxs) if (PropRowIsRendered(rows[i])) keep.push_back(i);
            idxs.swap(keep);
        }
        if (idxs.empty()) continue;
        std::stable_sort(idxs.begin(), idxs.end(), [&rows](size_t a, size_t b) {
            const bool ca = DevToolsRead_IsCoreProp(rows[a].name), cb = DevToolsRead_IsCoreProp(rows[b].name);
            if (ca != cb) return ca;
            return rows[a].name < rows[b].name;
        });
        DevToolsPaneItem head; head.kind = DevToolsPaneItemKind::GroupHeader; head.group = g;
        head.caption = GroupTitleForView(g);
        g_paneItems.push_back(head);
        for (size_t i : idxs) {
            DevToolsPaneItem it; it.kind = DevToolsPaneItemKind::Row; it.rowIndex = i; it.group = g;
            g_paneItems.push_back(it);
        }
    }
}

static size_t PushPaneItemsToRepeater()
{
    size_t rowsShown = 0;
    for (const auto& it : g_paneItems) if (it.kind == DevToolsPaneItemKind::Row) ++rowsShown;
    if (!g_propRepeater) return rowsShown;
    try {
        auto items = winrt::single_threaded_vector<winrt::Windows::Foundation::IInspectable>();
        for (size_t i = 0; i < g_paneItems.size(); ++i) items.Append(winrt::box_value((int32_t)i));
        const HRESULT hr = DevToolsRepeaterPutItemsSource(g_propRepeater,
                               reinterpret_cast<IInspectable*>(winrt::get_abi(items)));
        if (FAILED(hr))
            DevToolsOverlayLog(L"route1.props put_ItemsSource FAILED hr=0x%08X -- the pane will render no rows", hr);
    } catch (...) {
        DevToolsOverlayLog(L"route1.props could not build the repeater's ItemsSource");
        return 0;
    }
    return rowsShown;
}

static size_t RebuildPaneItems()
{
    BuildPaneItems(g_propRows);
    return PushPaneItemsToRepeater();
}

struct PropShowAllSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen) return S_OK;
        g_propShowAll = !g_propShowAll;
        const size_t shown = RebuildPaneItems();
        UpdatePropCount(shown);
        if (g_propShowAllLbl) {
            HSTRING hs = MakeStr(PropShowAllLabel(g_propRows));
            DevToolsPutText(g_propShowAllLbl, hs);
            WindowsDeleteString(hs);
        }
        DevToolsOverlayLog(L"route1.props showAll=%d -> %zu of %zu rows", (int)g_propShowAll, shown, g_propRowTotal);
        return S_OK;
    }
    void Init() { InitSink(IID_RoutedEventHandler_Del, g_route1Gen); }
};

static IInspectable* FindInRowOrExpansion(void* rowFe, const wchar_t* name)
{
    if (IInspectable* direct = FindInRow(rowFe, name)) return direct;
    IInspectable* host = FindInRow(rowFe, L"PExpand");
    if (!host) return nullptr;
    IInspectable* found = nullptr;
    void* pnl = nullptr;
    void* kids = nullptr;
    if (SUCCEEDED(host->QueryInterface(DevToolsIid<DevToolsXC::IPanel>(), &pnl)) && pnl &&
        SUCCEEDED(DevToolsPanelGetChildren(pnl, reinterpret_cast<IInspectable**>(&kids))) && kids)
    {
        unsigned int n = 0;
        DevToolsVecGetSize(reinterpret_cast<IInspectable*>(kids), &n);
        if (n > 0) {
            IInspectable* first = nullptr;
            if (SUCCEEDED(DevToolsVecGetAt(reinterpret_cast<IInspectable*>(kids), 0, &first)) && first) {
                void* fe = nullptr;
                if (SUCCEEDED(first->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) && fe) {
                    found = FindInRow(fe, name);
                    reinterpret_cast<IUnknown*>(fe)->Release();
                }
                first->Release();
            }
        }
    }
    if (kids) reinterpret_cast<IUnknown*>(kids)->Release();
    if (pnl)  reinterpret_cast<IUnknown*>(pnl)->Release();
    host->Release();
    return found;
}

static void* PaneRowChild(const std::wstring& prop, const wchar_t* childName)
{
    if (!g_propRepeater) return nullptr;
    for (size_t i = 0; i < g_paneItems.size(); ++i) {
        const DevToolsPaneItem& it = g_paneItems[i];
        if (it.kind != DevToolsPaneItemKind::Row || it.rowIndex >= g_propRows.size()) continue;
        const auto& rootName = g_propRows[it.rowIndex].name;
        const bool exact = prop == rootName;
        if (!exact && prop.compare(0, rootName.size() + 1, rootName + L".") != 0) continue;
        const size_t dot = exact ? std::wstring::npos : rootName.size();
        IInspectable* el = nullptr;
        if (FAILED(DevToolsRepeaterTryGetElement(g_propRepeater, (int)i, &el)) || !el) return nullptr;
        void* fe = nullptr;
        void* child = nullptr;
        if (SUCCEEDED(el->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) && fe) {
            const DevToolsCardRow* model = &g_propRows[it.rowIndex];
            size_t start = dot;
            while (fe && start != std::wstring::npos) {
                const size_t end = prop.find(L'.', start + 1);
                const std::wstring name = prop.substr(start + 1, end - start - 1);
                void* next = nullptr;
                if (IInspectable* children = FindInRowOrExpansion(fe, L"PChildren")) {
                    void* panel = nullptr;
                    IInspectable* items = nullptr;
                    if (SUCCEEDED(children->QueryInterface(DevToolsIid<DevToolsXC::IPanel>(), &panel)) && panel) {
                        if (SUCCEEDED(DevToolsPanelGetChildren(panel, &items)) && items) {
                            for (size_t c = 0; c < model->children.size(); ++c) {
                                if (model->children[c].name != name) continue;
                                IInspectable* item = nullptr;
                                if (SUCCEEDED(DevToolsVecGetAt(items, static_cast<unsigned int>(c), &item)) && item) {
                                    item->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &next);
                                    item->Release();
                                }
                                model = &model->children[c];
                                break;
                            }
                            items->Release();
                        }
                        reinterpret_cast<IUnknown*>(panel)->Release();
                    }
                    children->Release();
                }
                reinterpret_cast<IUnknown*>(fe)->Release();
                fe = next;
                start = end;
            }
            if (!fe) { el->Release(); return nullptr; }
            if (IInspectable* c = FindInRowOrExpansion(fe, childName)) {
                c->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &child);
                c->Release();
            }
            reinterpret_cast<IUnknown*>(fe)->Release();
        }
        el->Release();
        return child;
    }
    return nullptr;
}

static bool RefreshCommittedValue(const std::wstring& prop)
{
    std::vector<DevToolsCardRow> fresh;
    std::wstring type, name, authoredState, authoredXaml;
    if (!g_ctx.rowsFn) {
        DevToolsOverlayLog(L"route1.props refresh read failed for '%s'", prop.c_str());
        return false;
    }
    g_ctx.rowsFn(g_selWire, type, name, fresh, authoredState, authoredXaml);
    auto* current = FindPropertyRow(g_propRows, prop);
    const auto* updated = FindPropertyRow(fresh, prop);
    if (!current || !updated) {
        DevToolsOverlayLog(L"route1.props refresh row missing for '%s'", prop.c_str());
        return false;
    }
    *current = *updated;
    for (size_t i = 0; i < g_propRows.size() && i < g_propRowLabels.size(); ++i) {
        const auto& row = g_propRows[i];
        std::wstring label = row.name + L" " + row.value + L" " + row.authored;
        for (auto& c : label) if (c >= L'A' && c <= L'Z') c = static_cast<wchar_t>(c + 32);
        g_propRowLabels[i] = std::move(label);
    }
    if (void* value = PaneRowChild(prop, L"PValue")) {
        PopulateRowValue(static_cast<IInspectable*>(value), *current);
        reinterpret_cast<IUnknown*>(value)->Release();
    }
    return true;
}
static int WirePropRepeater(void* fragFe)
{
    if (g_propRepeater) { reinterpret_cast<IUnknown*>(g_propRepeater)->Release(); g_propRepeater = nullptr; }
    IInspectable* rep = FindInRow(fragFe, L"PropRepeater");
    if (!rep) { DevToolsOverlayLog(L"route1.props PropRepeater not found -- the pane will render no rows"); return 0; }
    rep->QueryInterface(DevToolsIid<DevToolsXC::IItemsRepeater>(), &g_propRepeater);
    rep->Release();
    if (!g_propRepeater) return 0;
    const HRESULT hrT = DevToolsRepeaterPutItemTemplate(g_propRepeater,
                            static_cast<DevToolsElementFactoryBase*>(&g_propRowFactory));
    if (FAILED(hrT)) {
        DevToolsOverlayLog(L"route1.props put_ItemTemplate FAILED hr=0x%08X -- no row will ever be built", hrT);
        return 0;
    }
    return (int)PushPaneItemsToRepeater();
}
