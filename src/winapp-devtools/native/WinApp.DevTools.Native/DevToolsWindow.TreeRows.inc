// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsWindow.cpp; included into that translation unit, not compiled on its own.

// The row palette, resolved off the shell donors for one BATCH of row builds. Held only for the duration of
// a build: reading the donors live each time is what keeps the rows following the app's theme (after a theme
// change the donors have re-resolved), and caching them across builds would defeat that.
struct DevToolsRowPalette
{
    void* accent = nullptr; void* primary = nullptr; void* secondary = nullptr; void* tertiary = nullptr;
    void* transparent = nullptr; void* mono = nullptr; void* glyph = nullptr; void* caption = nullptr;
};

static DevToolsRowPalette AcquireRowPalette()
{
    auto foreground = [](IInspectable* donor) -> void* {
        if (!donor) return nullptr;
        void* tb = nullptr;
        if (FAILED(donor->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &tb)) || !tb) return nullptr;
        IInspectable* brush = nullptr;
        DevToolsGetTextForeground(tb, &brush);
        reinterpret_cast<IUnknown*>(tb)->Release();
        return brush;
    };
    auto fontFamily = [](IInspectable* donor) -> void* {
        if (!donor) return nullptr;
        void* tb = nullptr;
        if (FAILED(donor->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &tb)) || !tb) return nullptr;
        IInspectable* ff = nullptr;
        DevToolsGetFontFamily(tb, &ff);
        reinterpret_cast<IUnknown*>(tb)->Release();
        return ff;
    };
    DevToolsRowPalette p;
    p.accent = foreground(g_palAccent);   p.primary = foreground(g_palPrimary);
    p.secondary = foreground(g_palSecondary); p.tertiary = foreground(g_palTertiary);
    p.mono = fontFamily(g_palMonoFont);   p.glyph = fontFamily(g_palGlyphFont);
    if (g_palTransparent) {
        void* bd = nullptr;
        if (SUCCEEDED(g_palTransparent->QueryInterface(DevToolsIid<DevToolsXC::IBorder>(), &bd)) && bd) {
            IInspectable* b = nullptr;
            DevToolsGetBorderBackground(bd, &b);
            p.transparent = b;
            reinterpret_cast<IUnknown*>(bd)->Release();
        }
    }
    if (g_palCaption) {
        void* fe = nullptr;
        if (SUCCEEDED(g_palCaption->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) && fe) {
            IInspectable* st = nullptr;
            DevToolsGetStyle(fe, &st);
            p.caption = st;
            reinterpret_cast<IUnknown*>(fe)->Release();
        }
    }
    if (!p.accent || !p.primary || !p.secondary || !p.tertiary || !p.transparent || !p.mono || !p.glyph) {
        DevToolsOverlayLog(L"route1.rows PALETTE INCOMPLETE accent=%d primary=%d secondary=%d tertiary=%d "
                      L"transparent=%d mono=%d glyph=%d -- rows would lose their theming",
                      p.accent ? 1 : 0, p.primary ? 1 : 0, p.secondary ? 1 : 0, p.tertiary ? 1 : 0,
                      p.transparent ? 1 : 0, p.mono ? 1 : 0, p.glyph ? 1 : 0);
    }
    int resolved = 0;
    for (void* q : { p.accent, p.primary, p.secondary, p.tertiary, p.transparent, p.mono, p.glyph, p.caption })
        if (q) ++resolved;
    DevToolsOverlayLog(L"route1.rows palette resolved=%d/8", resolved);
    return p;
}

static void ReleaseRowPalette(DevToolsRowPalette& p)
{
    for (void* q : { p.accent, p.primary, p.secondary, p.tertiary, p.transparent, p.mono, p.glyph, p.caption })
        if (q) reinterpret_cast<IUnknown*>(q)->Release();
    p = DevToolsRowPalette{};
}

// Parent a constructed element into a Children collection. WinRT takes a runtime-class parameter as its
// DEFAULT interface pointer and does not QI, so the UIElement view must be handed over explicitly --
// passing the IInspectable runs against the wrong vtable rather than failing (see DevToolsProjected.h).
static void AppendUiTo(IInspectable* vec, IInspectable* element)
{
    void* ui = nullptr;
    if (!element || FAILED(element->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) || !ui) return;
    DevToolsVecAppend(vec, reinterpret_cast<IInspectable*>(ui));
    reinterpret_cast<IUnknown*>(ui)->Release();
}

static void InsertUiAt(IInspectable* vec, unsigned int index, IInspectable* element)
{
    void* ui = nullptr;
    if (!element || FAILED(element->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) || !ui) return;
    DevToolsVecInsertAt(vec, index, reinterpret_cast<IInspectable*>(ui));
    reinterpret_cast<IUnknown*>(ui)->Release();
}

// Every activation is checked for a POINTER, never for an HRESULT: an element that failed to construct
// reads exactly as fast as one that did.
static IInspectable* NewTextBlock(const std::wstring& text)
{
    IInspectable* tb = DevToolsActivate(L"Microsoft.UI.Xaml.Controls.TextBlock");
    if (!tb) return nullptr;
    void* itb = nullptr;
    if (SUCCEEDED(tb->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &itb)) && itb) {
        HSTRING h = MakeStr(text);
        DevToolsPutText(itb, h);
        WindowsDeleteString(h);
        reinterpret_cast<IUnknown*>(itb)->Release();
    }
    return tb;
}

static void SetAutomationTextOn(IInspectable* el, const std::wstring& name, const std::wstring& id,
                                const std::wstring& controlType)
{
    if (!g_apStatics || !el) return;
    void* dobj = nullptr;
    if (FAILED(el->QueryInterface(DevToolsIid<DevToolsX::IDependencyObject>(), &dobj)) || !dobj) return;
    if (!name.empty())        { HSTRING h = MakeStr(name);        DevToolsSetAutomationName(g_apStatics, dobj, h);       WindowsDeleteString(h); }
    if (!id.empty())          { HSTRING h = MakeStr(id);          DevToolsSetAutomationId(g_apStatics, dobj, h);         WindowsDeleteString(h); }
    if (!controlType.empty()) { HSTRING h = MakeStr(controlType); DevToolsSetLocalizedControlType(g_apStatics, dobj, h); WindowsDeleteString(h); }
    reinterpret_cast<IUnknown*>(dobj)->Release();
}

// Build one AddRef'd tree row lazily. Model state exists for all rows; element arrays are sparse
// until expand/filter/select makes a row visible. Consumers must tolerate null unrealized entries.
static IInspectable* BuildOneTreeRow(size_t i,
                                     const DevToolsWindowNode& n,
                                     size_t original,
                                     const DevToolsTreeLayout::A11yRow& a,
                                     const std::vector<char>& collapsed,
                                     int minDepth,
                                     const DevToolsRowPalette& pal,
                                     int* outWired)
{
    // WinRT passes a runtime-class parameter as its DEFAULT interface pointer, and the callee does not QI.
    // ActivateInstance hands back the default interface of the class it made -- IButton for a Button -- so
    // every call below that declares UIElement / FrameworkElement / DependencyObject / ColumnDefinition /
    // Inline gets the pointer QI'd for exactly that. Handing it the IInspectable is the same class of mistake
    // as ChangeView's IReference (see DevToolsProjected.h): it does not fail, it runs against the wrong vtable.
    auto asIface = [](IInspectable* o, REFIID iid) -> void* {
        void* p = nullptr;
        if (o) o->QueryInterface(iid, &p);
        return p;
    };
    auto appendUi = [&](IInspectable* vec, IInspectable* element) {
        void* ui = asIface(element, DevToolsIid<DevToolsX::IUIElement>());
        if (!ui) return;
        DevToolsVecAppend(vec, reinterpret_cast<IInspectable*>(ui));
        reinterpret_cast<IUnknown*>(ui)->Release();
    };
    auto setGridColumn = [&](IInspectable* element, int column) {
        if (!g_gridStatics) return;
        void* fe = asIface(element, DevToolsIid<DevToolsX::IFrameworkElement>());
        if (!fe) return;
        // Grid.SetColumn requires a FrameworkElement, not IUIElement; the wrong ABI pointer can AV in WinUI.
DevToolsGridSetColumn(g_gridStatics, reinterpret_cast<IInspectable*>(fe), column);
        reinterpret_cast<IUnknown*>(fe)->Release();
    };
    auto asDependencyObject = [&](IInspectable* el) -> void* {
        return (g_apStatics && el) ? asIface(el, DevToolsIid<DevToolsX::IDependencyObject>()) : nullptr;
    };
    auto setAutomationText = [&](IInspectable* el, const std::wstring& name, const std::wstring& id,
                                 const std::wstring& controlType) {
        void* dobj = asDependencyObject(el);
        if (!dobj) return;
        if (!name.empty())        { HSTRING h = MakeStr(name);        DevToolsSetAutomationName(g_apStatics, dobj, h);            WindowsDeleteString(h); }
        if (!id.empty())          { HSTRING h = MakeStr(id);          DevToolsSetAutomationId(g_apStatics, dobj, h);              WindowsDeleteString(h); }
        if (!controlType.empty()) { HSTRING h = MakeStr(controlType); DevToolsSetLocalizedControlType(g_apStatics, dobj, h);      WindowsDeleteString(h); }
        reinterpret_cast<IUnknown*>(dobj)->Release();
    };
    auto setTreeItemPosition = [&](IInspectable* el, const DevToolsTreeLayout::A11yRow& a) {
        void* dobj = asDependencyObject(el);
        if (!dobj) return;
        DevToolsSetAutomationLevel(g_apStatics, dobj, (int)(a.level ? a.level : 1));
        DevToolsSetPositionInSet(g_apStatics, dobj, (int)(a.positionInSet ? a.positionInSet : 1));
        DevToolsSetSizeOfSet(g_apStatics, dobj, (int)(a.sizeOfSet ? a.sizeOfSet : 1));
        reinterpret_cast<IUnknown*>(dobj)->Release();
    };

    // Every activation is checked for a POINTER, never for an HRESULT: an element that failed to construct
    // reads exactly as fast as one that did.
    auto newTextBlock = [&](const std::wstring& text) -> IInspectable* {
        IInspectable* tb = DevToolsActivate(L"Microsoft.UI.Xaml.Controls.TextBlock");
        if (!tb) return nullptr;
        void* itb = nullptr;
        if (SUCCEEDED(tb->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &itb)) && itb) {
            HSTRING h = MakeStr(text);
            DevToolsPutText(itb, h);
            WindowsDeleteString(h);
            reinterpret_cast<IUnknown*>(itb)->Release();
        }
        return tb;
    };


        // A node is a branch (gets a chevron) iff the layout says so. Nodes whose children fall beyond the
        // render cap show no chevron (can't collapse what isn't rendered) -- honest.
        const bool hasKids = a.branch;
        const int leftPad = IndentPxForLevel(n.depth - minDepth);
        std::wstring identity = g_treeShortType[i];
        if (!n.name.empty()) { identity += L" #"; identity += n.name; }

        IInspectable* rowGrid = DevToolsActivate(L"Microsoft.UI.Xaml.Controls.Grid");
        if (!rowGrid) { DevToolsOverlayLog(L"route1.rows row %zu: Grid did not activate", i); return nullptr; }
        IInspectable* rowChildren = nullptr;
        {
            void* fe = nullptr;
            if (SUCCEEDED(rowGrid->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) && fe) {
                DevToolsPutMargin(fe, (double)leftPad, 1, 0, 1);
                reinterpret_cast<IUnknown*>(fe)->Release();
            }
            void* ig = nullptr;
            if (SUCCEEDED(rowGrid->QueryInterface(DevToolsIid<DevToolsXC::IGrid>(), &ig)) && ig) {
                IInspectable* defsRaw = nullptr;
                IInspectable* defs = nullptr;
                if (SUCCEEDED(DevToolsGridGetColumnDefinitions(ig, &defsRaw)) && defsRaw) {
                    defsRaw->QueryInterface(DevToolsIid<DevToolsColumnDefinitionVector>(), reinterpret_cast<void**>(&defs));
                    defsRaw->Release();
                }
                if (defs) {
                    const double widths[2] = { 18.0, 1.0 };
                    const int    units[2]  = { (int)DevToolsX::GridUnitType::Pixel, (int)DevToolsX::GridUnitType::Star };
                    for (int c = 0; c < 2; ++c) {
                        IInspectable* cd = DevToolsActivate(L"Microsoft.UI.Xaml.Controls.ColumnDefinition");
                        if (!cd) continue;
                        void* icd = nullptr;
                        if (SUCCEEDED(cd->QueryInterface(DevToolsIid<DevToolsXC::IColumnDefinition>(), &icd)) && icd) {
                            DevToolsColumnDefPutWidth(icd, widths[c], units[c]);
                            DevToolsColumnDefsAppend(defs, reinterpret_cast<IInspectable*>(icd));
                            reinterpret_cast<IUnknown*>(icd)->Release();
                        }
                        cd->Release();
                    }
                    defs->Release();
                }
                reinterpret_cast<IUnknown*>(ig)->Release();
            }
            void* pp = nullptr;
            if (SUCCEEDED(rowGrid->QueryInterface(DevToolsIid<DevToolsXC::IPanel>(), &pp)) && pp) {
                IInspectable* kids = nullptr;
                if (SUCCEEDED(DevToolsPanelGetChildren(pp, &kids)) && kids) {
                    kids->QueryInterface(DevToolsIid<DevToolsUIElementVector>(), reinterpret_cast<void**>(&rowChildren));
                    kids->Release();
                }
                reinterpret_cast<IUnknown*>(pp)->Release();
            }
        }
        if (!rowChildren) {
            DevToolsOverlayLog(L"route1.rows row %zu: the row Grid has no Children collection", i);
            rowGrid->Release();
            return nullptr;
        }

        if (hasKids) {
            IInspectable* chev  = DevToolsActivate(L"Microsoft.UI.Xaml.Controls.Primitives.ToggleButton");
            IInspectable* glyph = newTextBlock(L"\uE70D");
            if (chev && glyph) {
                void* gtb = nullptr;
                if (SUCCEEDED(glyph->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &gtb)) && gtb) {
                    if (pal.glyph) DevToolsPutFontFamily(gtb, pal.glyph);
                    DevToolsPutFontSize(gtb, 9);
                    if (pal.secondary) DevToolsPutTextForeground(gtb, pal.secondary);
                    reinterpret_cast<IUnknown*>(gtb)->Release();
                }
                void* gfe = nullptr;
                if (SUCCEEDED(glyph->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &gfe)) && gfe) {
                    DevToolsPutHorizontalAlignment(gfe, (int)DevToolsX::HorizontalAlignment::Center);
                    DevToolsPutVerticalAlignment(gfe, (int)DevToolsX::VerticalAlignment::Center);
                    reinterpret_cast<IUnknown*>(gfe)->Release();
                }
                void* cctl = nullptr;
                if (SUCCEEDED(chev->QueryInterface(DevToolsIid<DevToolsXC::IControl>(), &cctl)) && cctl) {
                    if (pal.transparent) DevToolsPutBackground(cctl, pal.transparent);
                    DevToolsPutBorderThickness(cctl, 0, 0, 0, 0);
                    DevToolsPutPadding(cctl, 0, 0, 0, 0);
                    reinterpret_cast<IUnknown*>(cctl)->Release();
                }
                void* cfe = nullptr;
                if (SUCCEEDED(chev->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &cfe)) && cfe) {
                    DevToolsPutMinWidth(cfe, 0);
                    DevToolsPutWidth(cfe, 18);
                    DevToolsPutHeight(cfe, 20);
                    DevToolsPutVerticalAlignment(cfe, (int)DevToolsX::VerticalAlignment::Center);
                    reinterpret_cast<IUnknown*>(cfe)->Release();
                }
                void* ccc = nullptr;
                if (SUCCEEDED(chev->QueryInterface(DevToolsIid<DevToolsXC::IContentControl>(), &ccc)) && ccc) {
                    DevToolsPutContent(ccc, glyph);
                    reinterpret_cast<IUnknown*>(ccc)->Release();
                }
                void* ctb = nullptr;
                if (SUCCEEDED(chev->QueryInterface(DevToolsIid<DevToolsXCP::IToggleButton>(), &ctb)) && ctb) {
                    // IsChecked is IReference<bool>, so it has to be boxed -- passing the raw bool is the
                    // no-op-that-returns-S_OK this ABI keeps producing. IsThreeState is left at its default
                    // (false), which is what the markup spelled out.
                    IInspectable* boxed = nullptr;
                    const bool expanded = !(i < collapsed.size() && collapsed[i]);
                    if (SUCCEEDED(BoxBool(expanded, &boxed)) && boxed) {
                        DevToolsToggleButtonPutIsChecked(ctb, boxed);
                        boxed->Release();
                    }
                    chev->QueryInterface(DevToolsIid<DevToolsXCP::IToggleButton>(), &g_treeChevronToggle[i]);
                    reinterpret_cast<IUnknown*>(ctb)->Release();
                }
                setAutomationText(chev, L"Expand or collapse " + identity,
                                  L"TreeToggle" + std::to_wstring(i), L"tree item expander");
                setGridColumn(chev, 0);
                void* cbb = nullptr;
                if (SUCCEEDED(chev->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), &cbb)) && cbb) {
                    ChevronSink* cs = new ChevronSink();
                    cs->Init(i);
                    __int64 ctok = 0;
                    DevToolsAddClick(cbb, cs, &ctok);
                    reinterpret_cast<IUnknown*>(cbb)->Release();
                }
                glyph->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &g_treeGlyph[i]);
                appendUi(rowChildren, chev);
            } else {
                DevToolsOverlayLog(L"route1.rows row %zu: chevron did not build (toggle=%p glyph=%p)", i, chev, glyph);
            }
            if (glyph) glyph->Release();
            if (chev)  chev->Release();
        }

        IInspectable* nodeBtn = DevToolsActivate(L"Microsoft.UI.Xaml.Controls.Button");
        IInspectable* caption = newTextBlock(std::wstring());
        if (!nodeBtn || !caption) {
            DevToolsOverlayLog(L"route1.rows row %zu: node button did not build (button=%p caption=%p)",
                          i, nodeBtn, caption);
            if (caption) caption->Release();
            if (nodeBtn) nodeBtn->Release();
            rowChildren->Release();
            rowGrid->Release();
            return nullptr;
        }
        {
            void* ctb = nullptr;
            if (SUCCEEDED(caption->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &ctb)) && ctb) {
                if (pal.mono) DevToolsPutFontFamily(ctb, pal.mono);
                DevToolsPutFontSize(ctb, 12);
                DevToolsPutTextTrimming(ctb, (int)DevToolsX::TextTrimming::CharacterEllipsis);
                IInspectable* inlinesRaw = nullptr;
                IInspectable* inlines = nullptr;
                if (SUCCEEDED(DevToolsGetInlines(ctb, &inlinesRaw)) && inlinesRaw) {
                    inlinesRaw->QueryInterface(DevToolsIid<DevToolsInlineVector>(), reinterpret_cast<void**>(&inlines));
                    inlinesRaw->Release();
                }
                if (inlines) {
                    auto addRun = [&](const std::wstring& text, void* brush, bool italic) {
                        IInspectable* run = DevToolsActivate(L"Microsoft.UI.Xaml.Documents.Run");
                        if (!run) return;
                        void* ir = nullptr;
                        if (SUCCEEDED(run->QueryInterface(DevToolsIid<DevToolsXDoc::IRun>(), &ir)) && ir) {
                            HSTRING h = MakeStr(text);
                            DevToolsPutRunText(ir, h);
                            WindowsDeleteString(h);
                            reinterpret_cast<IUnknown*>(ir)->Release();
                        }
                        void* ite = nullptr;
                        if (SUCCEEDED(run->QueryInterface(DevToolsIid<DevToolsXDoc::ITextElement>(), &ite)) && ite) {
                            if (brush)  DevToolsPutTextElementForeground(ite, brush);
                            if (italic) DevToolsPutTextElementFontStyle(
                                            ite, (int)winrt::Windows::UI::Text::FontStyle::Italic);
                            reinterpret_cast<IUnknown*>(ite)->Release();
                        }
                        void* iinl = nullptr;
                        if (SUCCEEDED(run->QueryInterface(DevToolsIid<DevToolsXDoc::IInline>(), &iinl)) && iinl) {
                            DevToolsInlinesAppend(inlines, reinterpret_cast<IInspectable*>(iinl));
                            reinterpret_cast<IUnknown*>(iinl)->Release();
                        }
                        run->Release();
                    };
                    addRun(g_treeShortType[i], pal.accent, false);
                    if (!n.name.empty()) {
                        addRun(L"  #", pal.tertiary, false);
                        addRun(n.name, pal.primary, false);
                    }
                    if (!n.preview.empty()) {
                        addRun(L"  \u201C", pal.tertiary, false);
                        addRun(n.preview, pal.secondary, true);
                        addRun(L"\u201D", pal.tertiary, false);
                    }
                    inlines->Release();
                }
                reinterpret_cast<IUnknown*>(ctb)->Release();
            }
            void* bctl = nullptr;
            if (SUCCEEDED(nodeBtn->QueryInterface(DevToolsIid<DevToolsXC::IControl>(), &bctl)) && bctl) {
                if (pal.transparent) DevToolsPutBackground(bctl, pal.transparent);
                DevToolsPutBorderThickness(bctl, 0, 0, 0, 0);
                DevToolsPutPadding(bctl, 6, 2, 6, 2);
                DevToolsPutHorizontalContentAlignment(bctl, (int)DevToolsX::HorizontalAlignment::Left);
                reinterpret_cast<IUnknown*>(bctl)->Release();
            }
            void* bfe = nullptr;
            if (SUCCEEDED(nodeBtn->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &bfe)) && bfe) {
                DevToolsPutHorizontalAlignment(bfe, (int)DevToolsX::HorizontalAlignment::Stretch);
                reinterpret_cast<IUnknown*>(bfe)->Release();
            }
            void* bcc = nullptr;
            if (SUCCEEDED(nodeBtn->QueryInterface(DevToolsIid<DevToolsXC::IContentControl>(), &bcc)) && bcc) {
                DevToolsPutContent(bcc, caption);
                reinterpret_cast<IUnknown*>(bcc)->Release();
            }
            std::wstring aname = identity;
            if (!n.preview.empty()) { aname += L" "; aname += n.preview; }
            setAutomationText(nodeBtn, aname, L"TreeNode" + std::to_wstring(i), L"tree item");
            setTreeItemPosition(nodeBtn, a);
            setGridColumn(nodeBtn, 1);
        }

        // Wire the row: select on click, hover-highlight on pointer enter, and a POSTED revert on pointer
        // exit so a row-to-row move does not flick the highlight back to the selection between them
        // (-- see HoverRevertProc).
        {
            void* bb = nullptr;
            if (SUCCEEDED(nodeBtn->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), &bb)) && bb) {
                std::wstring lbl = n.type;
                if (!n.name.empty()) { lbl += L"  #"; lbl += n.name; }
                NodeClickSink* sink = new NodeClickSink();
                sink->Init(n.handle, lbl, BuildBreadcrumb(g_allTreeNodes, original), &g_ctx, false);
                g_nodeSinks[i] = sink;
                nodeBtn->QueryInterface(DevToolsIid<DevToolsXC::IControl>(), &g_treeControls[i]);
                __int64 tok = 0;
                DevToolsAddClick(bb, sink, &tok);
                ++(*outWired);
                reinterpret_cast<IUnknown*>(bb)->Release();
            }
            void* rowUi = nullptr;
            if (SUCCEEDED(nodeBtn->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &rowUi)) && rowUi) {
                NodeClickSink* hover = new NodeClickSink();
                hover->Init(n.handle, std::wstring(), std::wstring(), &g_ctx, true);
                __int64 htok = 0;
                DevToolsAddPointerEntered(rowUi, hover, &htok);
                NodeClickSink* hoverOut = new NodeClickSink();
                hoverOut->Init(n.handle, std::wstring(), std::wstring(), &g_ctx, true);
                hoverOut->hoverExit = true;
                __int64 xtok = 0;
                DevToolsAddPointerExited(rowUi, hoverOut, &xtok);
                reinterpret_cast<IUnknown*>(rowUi)->Release();
            }
        }
        appendUi(rowChildren, nodeBtn);
        caption->Release();
        nodeBtn->Release();
        rowChildren->Release();

        rowGrid->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &g_treeRows[i]);
        rowGrid->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &g_treeRowFe[i]);
        return rowGrid;
}

// Construct replaceable tree rows directly, assigning text rather than interpolating markup.
// Keep tree accessibility roles, levels, set positions and ToggleButton expansion state.
// The caller retires the prior generation and sizes arrays before sinks capture the new one.
// Return an AddRef'd panel or null.
static IInspectable* BuildTreeRowsDirect(const std::vector<DevToolsWindowNode>& nodes,
                                         const std::vector<DevToolsTreeLayout::A11yRow>& a11y,
                                         const std::vector<char>& collapsed,
                                         const std::vector<char>& willShow,
                                         size_t populationTotal,
                                         bool appOnly,
                                         int* outWired)
{
    *outWired = 0;

    DevToolsRowPalette pal = AcquireRowPalette();
    auto releasePalette = [&]() { ReleaseRowPalette(pal); };

    IInspectable* panel = DevToolsActivate(L"Microsoft.UI.Xaml.Controls.StackPanel");
    IInspectable* panelChildren = nullptr;
    if (panel) {
        void* pp = nullptr;
        if (SUCCEEDED(panel->QueryInterface(DevToolsIid<DevToolsXC::IPanel>(), &pp)) && pp) {
            IInspectable* kids = nullptr;
            if (SUCCEEDED(DevToolsPanelGetChildren(pp, &kids)) && kids) {
                kids->QueryInterface(DevToolsIid<DevToolsUIElementVector>(), reinterpret_cast<void**>(&panelChildren));
                kids->Release();
            }
            reinterpret_cast<IUnknown*>(pp)->Release();
        }
    }
    if (!panel || !panelChildren) {
        DevToolsOverlayLog(L"route1.rows could not create the rows panel (panel=%p children=%p)", panel, panelChildren);
        if (panelChildren) panelChildren->Release();
        if (panel) panel->Release();
        releasePalette();
        return nullptr;
    }
    SetAutomationTextOn(panel, L"Visual tree", L"WinAppDevToolsTree", L"tree");

    const size_t renderCount = nodes.size();
    // Indent is measured from the SHALLOWEST rendered row: the census forest's roots are not guaranteed
    // to sit at depth 0, and a tree whose every row starts 100px in reads as an empty pane with a scrollbar.
    int minDepth = renderCount ? nodes[0].depth : 0;
    for (size_t i = 1; i < renderCount; ++i) if (nodes[i].depth < minDepth) minDepth = nodes[i].depth;

    for (size_t i = 0; i < renderCount; ++i) {
        const DevToolsWindowNode& n = nodes[i];
        const DevToolsTreeLayout::A11yRow a = i < a11y.size() ? a11y[i] : DevToolsTreeLayout::A11yRow{};
        const size_t original = g_renderedOriginalIndices[i];
        g_treeDepth[i] = n.depth;
        g_treeShortType[i] = ShortType(n.type);
        g_treeAppAuthored[i] = original < g_allTreeAppAuthored.size() ? g_allTreeAppAuthored[original] : 0;
        g_treeLabel[i] = DevToolsTreeLayout::BuildTreeFilterLabel(n.type, n.name,
            n.preview + L" " + n.searchText + L" " + n.sourceUri);
        // Addressable whether or not it gets an element: this map answers "is this element in the tree", and
        // a deferred row is in the tree. Populated here rather than in BuildOneTreeRow so it covers
        // rows that builder never runs for.
        if (n.handle) g_nodeIdxByWire[n.handle] = i;

        if (i < willShow.size() && !willShow[i]) continue;

        IInspectable* rowGrid = BuildOneTreeRow(i, n, original, a, collapsed, minDepth, pal, outWired);
        if (!rowGrid) continue;
        AppendUiTo(panelChildren, rowGrid);
        rowGrid->Release();
    }

    if (renderCount < populationTotal) {
        wchar_t note[160];
        if (appOnly) {
            _snwprintf_s(note, _countof(note), _TRUNCATE,
                         L"\u2026 %zu more app-authored nodes not shown (render capped at %zu)",
                         populationTotal - renderCount, kMaxRenderNodes);
        } else {
            _snwprintf_s(note, _countof(note), _TRUNCATE,
                         L"\u2026 %zu more nodes not shown (render capped at %zu)",
                         populationTotal - renderCount, kMaxRenderNodes);
        }
        if (IInspectable* tb = NewTextBlock(note)) {
            void* itb = nullptr;
            if (SUCCEEDED(tb->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &itb)) && itb) {
                if (pal.tertiary) DevToolsPutTextForeground(itb, pal.tertiary);
                DevToolsPutFontStyle(itb, (int)winrt::Windows::UI::Text::FontStyle::Italic);
                DevToolsPutTextWrapping(itb, (int)DevToolsX::TextWrapping::Wrap);
                reinterpret_cast<IUnknown*>(itb)->Release();
            }
            void* fe = nullptr;
            if (SUCCEEDED(tb->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) && fe) {
                if (pal.caption) DevToolsPutStyle(fe, pal.caption);
                DevToolsPutMargin(fe, 6, 8, 6, 4);
                reinterpret_cast<IUnknown*>(fe)->Release();
            }
            AppendUiTo(panelChildren, tb);
            tb->Release();
        }
    }

    unsigned int attached = 0;
    DevToolsVecGetSize(panelChildren, &attached);
    unsigned int intended = 0;
    for (size_t i = 0; i < renderCount; ++i) if (!(i < willShow.size()) || willShow[i]) ++intended;
    const unsigned int expected = intended + (renderCount < populationTotal ? 1u : 0u);
    if (attached != expected)
        DevToolsOverlayLog(L"route1.rows BUILD SHORTFALL: %u element(s) attached, %u expected", attached, expected);
    DevToolsOverlayLog(L"route1.rows built %u of %zu rows (%zu deferred until first shown)",
                  intended, renderCount, renderCount - (size_t)intended);
    if (g_treeRowsPanelChildren) g_treeRowsPanelChildren->Release();
    g_treeRowsPanelChildren = panelChildren;
    releasePalette();
    return panel;
}

static int EnsureRowsRealized(const std::vector<char>& shouldShow)
{
    WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::WinRowRealize);
    if (!g_treeRowsPanelChildren) return 0;
    size_t need = 0;
    for (size_t i = 0; i < shouldShow.size() && i < g_treeRows.size(); ++i)
        if (shouldShow[i] && !g_treeRows[i]) ++need;
    if (!need) return 0;

    DevToolsRowPalette pal = AcquireRowPalette();
    int built = 0, wired = 0;
    for (size_t i = 0; i < shouldShow.size() && i < g_treeRows.size(); ++i) {
        if (!shouldShow[i] || g_treeRows[i]) continue;
        if (i >= g_renderedOriginalIndices.size()) continue;
        const size_t original = g_renderedOriginalIndices[i];
        if (original >= g_allTreeNodes.size()) continue;
        const DevToolsTreeLayout::A11yRow a = i < g_treeA11y.size() ? g_treeA11y[i] : DevToolsTreeLayout::A11yRow{};
        IInspectable* rowGrid = BuildOneTreeRow(i, g_allTreeNodes[original], original, a,
                                                g_treeCollapseAtBuild, g_treeMinDepth, pal, &wired);
        if (!rowGrid) continue;
        unsigned int pos = 0;
        for (size_t j = 0; j < i; ++j) if (j < g_treeRows.size() && g_treeRows[j]) ++pos;
        InsertUiAt(g_treeRowsPanelChildren, pos, rowGrid);
        rowGrid->Release();
        if (i < g_treeViewDepth.size() && i < g_treeRowFe.size() && g_treeRowFe[i] && g_treeViewDepth[i] >= 0) {
            int level = g_treeViewDepth[i] - g_treeIndentBaseline;
            if (level < 0) level = 0;
            DevToolsPutMargin(g_treeRowFe[i], (double)IndentPxForLevel(level), 1.0, 0.0, 1.0);
        }
        ++built;
    }
    ReleaseRowPalette(pal);
    DevToolsOverlayLog(L"route1.rows realized %d of %zu newly visible row(s)", built, need);
    return built;
}

// The header prints the true census total, not the capped rendered row count.
static std::wstring BuildWindowMarkup(size_t flattenedCount, size_t censusTotal)
{
    wchar_t hdr[160];
    const size_t total = censusTotal ? censusTotal : flattenedCount;
    if (total > flattenedCount) {
        _snwprintf_s(hdr, _countof(hdr), _TRUNCATE,
                     L"VISUAL TREE \u00B7 SHOWING %zu OF %zu NODES", flattenedCount, total);
    } else {
        _snwprintf_s(hdr, _countof(hdr), _TRUNCATE, L"VISUAL TREE \u00B7 %zu NODES", total);
    }

    // Assemble: start from the externalized static shell (codegen'd from xaml-window/window-shell.xaml into
    // DevToolsWindowXaml.g.h) and substitute the three placeholders. Kept as one document / one namescope so the
    // window's FindName lookups all resolve after a single XamlReader.Load.
    std::wstring m = DevToolsXaml::WindowShell;
    ReplaceToken(m, L"$TREEHEADER$", DevToolsXmlEscape(hdr));
    return m;
}
