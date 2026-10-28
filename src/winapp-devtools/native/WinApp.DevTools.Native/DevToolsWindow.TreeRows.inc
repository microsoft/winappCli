// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsWindow.cpp; included into that translation unit, not compiled on its own.

#include <winrt/Microsoft.UI.Xaml.Automation.h>

namespace DevToolsTreeA = winrt::Microsoft::UI::Xaml::Automation;

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

static size_t TreeNodeIndex(DevToolsXC::TreeViewNode const& node)
{
    for (size_t i = 0; i < g_treeNodes.size(); ++i)
        if (g_treeNodes[i] == node) return i;
    return DevToolsTreeLayout::kNoRow;
}

static std::wstring TreeRowName(size_t index)
{
    const auto& node = g_allTreeNodes[g_renderedOriginalIndices[index]];
    std::wstring name = g_treeShortType[index];
    if (!node.name.empty()) name += L" #" + node.name;
    if (!node.preview.empty()) name += L" " + node.preview;
    return name;
}

static std::wstring TreeRowAccessibleName(size_t index)
{
    const std::wstring name = TreeRowName(index);
    size_t count = 0, position = 0;
    for (size_t i = 0; i < g_treeParents.size(); ++i) {
        if (!g_treeIncluded[i] || g_treeParents[i] != g_treeParents[index] || TreeRowName(i) != name) continue;
        ++count;
        if (i <= index) ++position;
    }
    return count < 2 ? name : name + L" (" + std::to_wstring(position) + L" of " + std::to_wstring(count) + L")";
}

static void NameTreeContainer(size_t index, DevToolsXC::TreeViewItem const& container)
{
    if (index >= g_treeParents.size() || index >= g_treeIncluded.size()) return;
    if (const auto caption = container.ContentTemplateRoot().try_as<DevToolsXC::TextBlock>()) {
        const auto source = g_treeNodes[index].Content().as<DevToolsXC::TextBlock>();
        caption.FontFamily(source.FontFamily());
        const auto segments = source.Inlines().Size();
        if (caption.Inlines().Size() != segments) winrt::throw_hresult(E_UNEXPECTED);
        // Run.Text is not a dependency property. Copy it when preparing or updating a container.
        for (uint32_t segment = 0; segment < segments; ++segment)
            caption.Inlines().GetAt(segment).as<DevToolsXDoc::Run>().Text(
                source.Inlines().GetAt(segment).as<DevToolsXDoc::Run>().Text());
    }
    DevToolsTreeA::AutomationProperties::SetAutomationId(container, L"TreeNode" + std::to_wstring(index));
    DevToolsTreeA::AutomationProperties::SetName(container, TreeRowAccessibleName(index));
    container.AllowDrop(false);
    const auto& node = g_allTreeNodes[g_renderedOriginalIndices[index]];
    std::wstring full = g_treeShortType[index];
    if (!node.name.empty()) full += L"  #" + node.name;
    if (!node.preview.empty()) full += L"  \u201C" + node.preview + L"\u201D";
    DevToolsXC::ToolTipService::SetToolTip(container, winrt::box_value(full));
    void* control = nullptr;
    winrt::check_hresult(reinterpret_cast<IUnknown*>(winrt::get_abi(container))->QueryInterface(
        DevToolsIid<DevToolsXC::IControl>(), &control));
    for (auto& previous : g_treeControls) {
        if (previous != control) continue;
        reinterpret_cast<IUnknown*>(previous)->Release();
        previous = nullptr;
    }
    if (g_treeControls[index]) reinterpret_cast<IUnknown*>(g_treeControls[index])->Release();
    g_treeControls[index] = control;
}

static void SyncTreeSelection()
{
    if (!g_treeView) return;
    const bool prior = std::exchange(g_treeUpdating, true);
    try {
        const auto row = g_nodeIdxByWire.find(g_selectedWire);
        const auto selected = row != g_nodeIdxByWire.end() && row->second < g_treeNodes.size() &&
                row->second < g_treeIncluded.size() && g_treeIncluded[row->second]
            ? g_treeNodes[row->second] : DevToolsXC::TreeViewNode{nullptr};
        if (g_treeView.SelectedNode() != selected) g_treeView.SelectedNode(selected);
    } catch (const winrt::hresult_error& error) {
        DevToolsOverlayLog(L"tree.selection failed hr=0x%08X", error.code().value);
        SetStatusText(L"Could not update the tree selection. Refresh the inspector and retry.");
    }
    g_treeUpdating = prior;
}

// Keep the inspection model stable while WinUI owns containers, navigation, selection and UI Automation.
static void SyncTreeStructure()
{
    if (!g_treeView) return;
    const bool prior = std::exchange(g_treeUpdating, true);
    try {
        std::vector<char> included(g_treeNodes.size(), 0);
        for (size_t i = 0; i < included.size(); ++i)
            included[i] = g_treeFilterQuery.empty() ? !RowHiddenByJmx(i) : g_treeVisible[i];
        const auto parents = DevToolsTreeLayout::ComputeVisibleParents(SnapshotLayoutRows(), included);
        if (included != g_treeIncluded || parents != g_treeParents) {
            g_treeView.SelectedNode(nullptr);
            g_treeView.RootNodes().Clear();
            for (auto const& node : g_treeNodes) node.Children().Clear();
            g_treeIncluded = included;
            g_treeParents = parents;
            for (size_t i = 0; i < included.size(); ++i) {
                if (!included[i]) continue;
                const size_t parent = parents[i];
                if (parent == DevToolsTreeLayout::kNoRow) g_treeView.RootNodes().Append(g_treeNodes[i]);
                else g_treeNodes[parent].Children().Append(g_treeNodes[i]);
            }
        }
        for (size_t i = 0; i < g_treeNodes.size(); ++i) {
            g_treeNodes[i].IsExpanded(!g_treeFilterQuery.empty() || !g_treeCollapsed[i]);
            if (const auto container = g_treeView.ContainerFromNode(g_treeNodes[i]).try_as<DevToolsXC::TreeViewItem>())
                NameTreeContainer(i, container);
        }
        SyncTreeSelection();
    } catch (const winrt::hresult_error& error) {
        DevToolsOverlayLog(L"tree.structure failed hr=0x%08X", error.code().value);
        SetStatusText(L"Could not update the visual tree. Refresh the inspector and retry.");
    }
    g_treeUpdating = prior;
}

static void OnTreeExpansion(DevToolsXC::TreeViewNode const& node, bool expanded)
{
    if (g_treeUpdating) return;
    const size_t index = TreeNodeIndex(node);
    if (index >= g_treeCollapsed.size()) return;
    if (!g_treeFilterQuery.empty()) {
        if (!expanded) {
            const auto restore = [node, index, generation = g_treeRowsGen] {
                if (generation != g_treeRowsGen || g_treeFilterQuery.empty() ||
                    index >= g_treeNodes.size() || g_treeNodes[index] != node) return;
                const bool prior = std::exchange(g_treeUpdating, true);
                try {
                    node.IsExpanded(true);
                } catch (const winrt::hresult_error& error) {
                    DevToolsOverlayLog(L"tree.filtered expansion failed hr=0x%08X", error.code().value);
                    SetStatusText(L"Could not restore filtered rows. Clear the filter and retry.");
                }
                g_treeUpdating = prior;
            };
            const auto queue = g_treeView.DispatcherQueue();
            if (!queue || !queue.TryEnqueue(restore)) {
                DevToolsOverlayLog(L"tree.filtered expansion could not be queued");
                SetStatusText(L"Could not restore filtered rows. Clear the filter and retry.");
                return;
            }
        }
        SetStatusText(L"Clear the tree filter to expand or collapse rows.");
        return;
    }
    SetTreeRowExpanded(index, expanded);
}

static DevToolsXC::TextBlock BuildTreeCaption(size_t index, DevToolsWindowNode const& node)
{
    DevToolsXC::TextBlock caption;
    caption.FontSize(12);
    caption.TextTrimming(DevToolsX::TextTrimming::CharacterEllipsis);
    if (g_palMonoFont) caption.FontFamily(TreeObject<DevToolsXC::TextBlock>(g_palMonoFont).FontFamily());
    auto addRun = [&](std::wstring const& text, IInspectable* donor, bool italic = false) {
        DevToolsXDoc::Run run;
        run.Text(text);
        if (donor) run.Foreground(TreeObject<DevToolsXC::TextBlock>(donor).Foreground());
        if (italic) run.FontStyle(winrt::Windows::UI::Text::FontStyle::Italic);
        caption.Inlines().Append(run);
    };
    // Keep every segment present so recycled captions always copy the same six runs.
    addRun(g_treeShortType[index], g_palAccent);
    addRun(node.name.empty() ? L"" : L"  #", g_palTertiary);
    addRun(node.name, g_palPrimary);
    addRun(node.preview.empty() ? L"" : L"  \u201C", g_palTertiary);
    addRun(node.preview, g_palSecondary, true);
    addRun(node.preview.empty() ? L"" : L"\u201D", g_palTertiary);
    DevToolsTreeA::AutomationProperties::SetAccessibilityView(
        caption, winrt::Microsoft::UI::Xaml::Automation::Peers::AccessibilityView::Raw);
    return caption;
}

static void EndTreeHover()
{
    if (!g_treeHoverWire) return;
    g_treeHoverWire = 0;
    g_hoverRevertEpoch = ++g_hoverEpoch;
    g_hoverRevertGen = g_route1Gen;
    if (!g_hoverRevertTimer) g_hoverRevertTimer = SetTimer(nullptr, 0, USER_TIMER_MINIMUM, HoverRevertProc);
}

static IInspectable* BuildTreeRowsDirect(const std::vector<DevToolsWindowNode>& nodes,
                                         const std::vector<char>& collapsed,
                                         size_t populationTotal,
                                         bool appOnly,
                                         int* outWired)
{
    *outWired = 0;
    try {
        DevToolsXC::Grid panel;
        panel.RowDefinitions().Append(DevToolsXC::RowDefinition{});
        DevToolsXC::RowDefinition noteRow;
        noteRow.Height({0, DevToolsX::GridUnitType::Auto});
        panel.RowDefinitions().Append(noteRow);
        g_treeView = DevToolsXC::TreeView{};
        winrt::com_ptr<IInspectable> itemTemplate;
        winrt::check_hresult(LoadMarkupWin(
            LR"(<DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">)"
            LR"(<TextBlock FontSize="12" TextTrimming="CharacterEllipsis">)"
            LR"(<Run Foreground="{ThemeResource AccentTextFillColorPrimaryBrush}"/>)"
            LR"(<Run Foreground="{ThemeResource TextFillColorTertiaryBrush}"/>)"
            LR"(<Run Foreground="{ThemeResource TextFillColorPrimaryBrush}"/>)"
            LR"(<Run Foreground="{ThemeResource TextFillColorTertiaryBrush}"/>)"
            LR"(<Run Foreground="{ThemeResource TextFillColorSecondaryBrush}" FontStyle="Italic"/>)"
            LR"(<Run Foreground="{ThemeResource TextFillColorTertiaryBrush}"/>)"
            LR"(</TextBlock></DataTemplate>)",
            itemTemplate.put()));
        g_treeView.ItemTemplate(itemTemplate.as<DevToolsX::DataTemplate>());
        g_treeView.SelectionMode(DevToolsXC::TreeViewSelectionMode::Single);
        g_treeView.CanDragItems(false);
        g_treeView.CanReorderItems(false);
        g_treeView.AllowDrop(false);
        DevToolsXC::ScrollViewer::SetHorizontalScrollMode(g_treeView, DevToolsXC::ScrollMode::Disabled);
        DevToolsXC::ScrollViewer::SetHorizontalScrollBarVisibility(g_treeView, DevToolsXC::ScrollBarVisibility::Disabled);
        panel.Children().Append(g_treeView);
        g_treeView.Loaded([generation = g_treeRowsGen, containersWired = false](auto const&, auto const&) mutable {
            if (generation != g_treeRowsGen) return;
            if (const auto list = TreeDescendant<DevToolsXC::ListViewBase>(g_treeView)) {
                DevToolsTreeA::AutomationProperties::SetAutomationId(list, L"WinAppDevToolsTree");
                DevToolsTreeA::AutomationProperties::SetName(list, L"Visual tree");
                if (!containersWired) {
                    list.ContainerContentChanging([generation](auto const&, auto const& args) {
                        if (generation != g_treeRowsGen || args.InRecycleQueue() || !g_treeView) return;
                        try {
                            const auto container = args.ItemContainer().template try_as<DevToolsXC::TreeViewItem>();
                            if (!container) return;
                            auto index = TreeNodeIndex(args.Item().template try_as<DevToolsXC::TreeViewNode>());
                            if (index >= g_treeNodes.size())
                                index = TreeNodeIndex(g_treeView.NodeFromContainer(container));
                            if (index < g_treeNodes.size()) NameTreeContainer(index, container);
                        } catch (const winrt::hresult_error& error) {
                            DevToolsOverlayLog(L"tree.container failed hr=0x%08X", error.code().value);
                            SetStatusText(L"Could not prepare a tree row. Refresh the inspector and retry.");
                        }
                    });
                    containersWired = true;
                }
                for (size_t i = 0; i < g_treeNodes.size(); ++i)
                    if (const auto container = g_treeView.ContainerFromNode(g_treeNodes[i]).try_as<DevToolsXC::TreeViewItem>())
                        NameTreeContainer(i, container);
                g_treeScroller = TreeScrollViewer();
                RevealPendingRow();
            } else {
                DevToolsOverlayLog(L"tree.loaded has no list for accessibility and scrolling");
                SetStatusText(L"The visual tree did not finish loading. Refresh the inspector and retry.");
            }
        });
        g_treeView.LayoutUpdated([generation = g_treeRowsGen](auto const&, auto const&) {
            if (generation == g_treeRowsGen && g_pendingRevealWire) RevealPendingRow();
        });
        g_treeNodes.reserve(nodes.size());
        for (size_t i = 0; i < nodes.size(); ++i) {
            auto const& n = nodes[i];
            const size_t original = g_renderedOriginalIndices[i];
            g_treeDepth[i] = n.depth;
            g_treeShortType[i] = ShortType(n.type);
            g_treeAppAuthored[i] = original < g_allTreeAppAuthored.size() ? g_allTreeAppAuthored[original] : 0;
            g_treeLabel[i] = DevToolsTreeLayout::BuildTreeFilterLabel(n.type, n.name,
                n.preview + L" " + n.searchText + L" " + n.sourceUri);
            if (n.handle) g_nodeIdxByWire[n.handle] = i;
            auto caption = BuildTreeCaption(i, n);
            DevToolsXC::TreeViewNode treeNode;
            treeNode.Content(caption);
            treeNode.IsExpanded(i >= collapsed.size() || !collapsed[i]);
            g_treeNodes.push_back(treeNode);
            reinterpret_cast<IUnknown*>(winrt::get_abi(caption))->QueryInterface(
                DevToolsIid<DevToolsX::IUIElement>(), &g_treeRows[i]);
            auto sink = std::make_unique<NodeClickSink>();
            std::wstring label = n.type;
            if (!n.name.empty()) label += L"  #" + n.name;
            sink->Init(n.handle, label, BuildBreadcrumb(g_allTreeNodes, original), &g_ctx);
            g_nodeSinks[i] = std::move(sink);
            ++*outWired;
        }
        const unsigned generation = g_treeRowsGen;
        g_treeView.SelectionChanged([generation](auto const&, auto const& args) {
            if (generation != g_treeRowsGen || g_treeUpdating || args.AddedItems().Size() == 0) return;
            const size_t index = TreeNodeIndex(args.AddedItems().GetAt(0).template try_as<DevToolsXC::TreeViewNode>());
            if (index >= g_nodeSinks.size() || !g_nodeSinks[index]) return;
            const auto wire = g_nodeSinks[index]->handle;
            g_selectedWire = wire;
            // A click also invokes the item. Let that event read the pane once; keyboard selection still reads it.
            auto readSelection = [generation, index, wire] {
                if (generation == g_treeRowsGen && wire == g_selectedWire && wire != g_selWire &&
                    index < g_nodeSinks.size() && g_nodeSinks[index])
                    g_nodeSinks[index]->Invoke(nullptr, nullptr);
            };
            const auto queue = g_treeView.DispatcherQueue();
            if (!queue || !queue.TryEnqueue(readSelection)) {
                DevToolsOverlayLog(L"tree.selection could not queue the pane read; reading immediately");
                readSelection();
            }
        });
        g_treeView.ItemInvoked([generation](auto const&, auto const& args) {
            if (generation != g_treeRowsGen || g_treeUpdating) return;
            const size_t index = TreeNodeIndex(args.InvokedItem().template try_as<DevToolsXC::TreeViewNode>());
            if (index < g_nodeSinks.size() && g_nodeSinks[index] && g_selectedWire == g_nodeSinks[index]->handle)
                g_nodeSinks[index]->Invoke(nullptr, nullptr);
        });
        g_treeView.Expanding([generation](auto const&, auto const& args) {
            if (generation == g_treeRowsGen) OnTreeExpansion(args.Node(), true);
        });
        g_treeView.Collapsed([generation](auto const&, auto const& args) {
            if (generation == g_treeRowsGen) OnTreeExpansion(args.Node(), false);
        });
        g_treeView.PointerMoved([generation](auto const&, auto const& args) {
            if (generation != g_treeRowsGen) return;
            try {
                auto parent = args.OriginalSource().template try_as<DevToolsX::DependencyObject>();
                while (parent && !parent.try_as<DevToolsXC::TreeViewItem>())
                    parent = DevToolsXM::VisualTreeHelper::GetParent(parent);
                if (!parent) { EndTreeHover(); return; }
                const size_t index = TreeNodeIndex(g_treeView.NodeFromContainer(parent));
                if (index >= g_nodeSinks.size() || !g_nodeSinks[index]) return;
                const auto wire = g_nodeSinks[index]->handle;
                if (wire == g_treeHoverWire) return;
                WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::WinTreeHover);
                g_treeHoverWire = wire;
                ++g_hoverEpoch;
                if (g_ctx.highlightFn) g_ctx.highlightFn(wire, false);
            } catch (const winrt::hresult_error& error) {
                DevToolsOverlayLog(L"tree.hover failed hr=0x%08X", error.code().value);
            }
        });
        g_treeView.PointerExited([generation](auto const&, auto const&) {
            if (generation != g_treeRowsGen) return;
            EndTreeHover();
        });
        if (nodes.size() < populationTotal) {
            DevToolsXC::TextBlock note;
            note.Text(L"\u2026 " + std::to_wstring(populationTotal - nodes.size()) +
                (appOnly ? L" more app-authored nodes not shown (render capped at " : L" more nodes not shown (render capped at ") +
                std::to_wstring(kMaxRenderNodes) + L")");
            note.TextWrapping(DevToolsX::TextWrapping::Wrap);
            note.Margin({6, 8, 6, 4});
            if (g_palTertiary) note.Foreground(TreeObject<DevToolsXC::TextBlock>(g_palTertiary).Foreground());
            DevToolsXC::Grid::SetRow(note, 1);
            panel.Children().Append(note);
        }
        return reinterpret_cast<IInspectable*>(winrt::detach_abi(panel));
    } catch (const winrt::hresult_error& error) {
        DevToolsOverlayLog(L"tree.build failed hr=0x%08X", error.code().value);
        SetStatusText(L"Could not build the visual tree. Refresh the inspector and retry.");
        return nullptr;
    }
}

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
    std::wstring markup = DevToolsXaml::WindowShell;
    ReplaceToken(markup, L"$TREEHEADER$", DevToolsXmlEscape(hdr));
    return markup;
}
