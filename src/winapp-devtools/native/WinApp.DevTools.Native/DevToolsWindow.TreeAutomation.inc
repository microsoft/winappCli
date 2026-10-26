// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsWindow.cpp; exposes the flat renderer as a hierarchical accessibility tree.

#include <uiautomation.h>
#include <winrt/Microsoft.UI.Xaml.Automation.h>
#include <winrt/Microsoft.UI.Xaml.Automation.Peers.h>
#include <winrt/Microsoft.UI.Xaml.Automation.Provider.h>

namespace DevToolsTreeAP = winrt::Microsoft::UI::Xaml::Automation::Peers;
namespace DevToolsTreeA = winrt::Microsoft::UI::Xaml::Automation;
namespace DevToolsTreeProvider = winrt::Microsoft::UI::Xaml::Automation::Provider;

static DevToolsX::UIElement TreeAutomationElement(void* object)
{
    winrt::Windows::Foundation::IInspectable value{nullptr};
    winrt::copy_from_abi(value, object);
    return value.as<DevToolsX::UIElement>();
}

static winrt::Windows::Foundation::Collections::IVector<DevToolsTreeAP::AutomationPeer>
TreeAutomationChildren(size_t parent, unsigned generation)
{
    auto children = winrt::single_threaded_vector<DevToolsTreeAP::AutomationPeer>();
    if (generation != g_treeRowsGen) return children;
    for (size_t i = 0; i < g_treeAutomationParents.size(); ++i) {
        if (!g_treeVisible[i] || !g_treeControls[i] || g_treeAutomationParents[i] != parent) continue;
        children.Append(DevToolsTreeAP::FrameworkElementAutomationPeer::CreatePeerForElement(
            TreeAutomationElement(g_treeControls[i])));
    }
    if (parent == DevToolsTreeLayout::kNoRow && g_treeAutomationNote)
        children.Append(DevToolsTreeAP::FrameworkElementAutomationPeer::CreatePeerForElement(g_treeAutomationNote));
    return children;
}

struct DevToolsTreePeer : DevToolsTreeAP::FrameworkElementAutomationPeerT<
    DevToolsTreePeer, DevToolsTreeProvider::ISelectionProvider>
{
    DevToolsTreePeer(DevToolsX::FrameworkElement const& owner, unsigned generation)
        : FrameworkElementAutomationPeerT(owner), m_generation(generation) {}

    DevToolsTreeAP::AutomationControlType GetAutomationControlTypeCore() { return DevToolsTreeAP::AutomationControlType::Tree; }
    winrt::hstring GetClassNameCore() { return L"DevToolsTree"; }
    bool IsControlElementCore() { return true; }
    bool IsContentElementCore() { return true; }
    auto GetChildrenCore() { return TreeAutomationChildren(DevToolsTreeLayout::kNoRow, m_generation); }
    winrt::Windows::Foundation::IInspectable GetPatternCore(DevToolsTreeAP::PatternInterface pattern)
    {
        if (pattern == DevToolsTreeAP::PatternInterface::Selection)
            return *this;
        return FrameworkElementAutomationPeerT::GetPatternCore(pattern);
    }
    bool CanSelectMultiple() { return false; }
    bool IsSelectionRequired() { return false; }
    winrt::com_array<DevToolsTreeProvider::IRawElementProviderSimple> GetSelection()
    {
        if (m_generation != g_treeRowsGen) throw winrt::hresult_error(UIA_E_ELEMENTNOTAVAILABLE);
        for (size_t i = 0; i < g_treeControls.size(); ++i) {
            if (g_treeVisible[i] && g_treeControls[i] && g_treeControls[i] == g_selCtrl) {
                auto peer = DevToolsTreeAP::FrameworkElementAutomationPeer::CreatePeerForElement(
                    TreeAutomationElement(g_treeControls[i]));
                return {ProviderFromPeer(peer)};
            }
        }
        return {};
    }
    unsigned m_generation;
};

struct DevToolsTreeItemPeer : DevToolsTreeAP::FrameworkElementAutomationPeerT<
    DevToolsTreeItemPeer, DevToolsTreeProvider::ISelectionItemProvider,
    DevToolsTreeProvider::IExpandCollapseProvider, DevToolsTreeProvider::IInvokeProvider>
{
    DevToolsTreeItemPeer(DevToolsX::FrameworkElement const& owner, size_t index, unsigned generation)
        : FrameworkElementAutomationPeerT(owner), m_index(index), m_generation(generation) {}

    DevToolsTreeAP::AutomationControlType GetAutomationControlTypeCore() { return DevToolsTreeAP::AutomationControlType::TreeItem; }
    winrt::hstring GetClassNameCore() { return L"DevToolsTreeItem"; }
    winrt::hstring GetNameCore()
    {
        const auto name = FrameworkElementAutomationPeerT::GetNameCore();
        if (m_generation != g_treeRowsGen || m_index >= g_treeAutomationParents.size()) return name;
        size_t count = 0, position = 0;
        for (size_t i = 0; i < g_treeAutomationParents.size(); ++i) {
            if (!g_treeVisible[i] || !g_treeControls[i] ||
                g_treeAutomationParents[i] != g_treeAutomationParents[m_index]) continue;
            if (DevToolsTreeA::AutomationProperties::GetName(TreeAutomationElement(g_treeControls[i])) != name) continue;
            ++count;
            if (i <= m_index) ++position;
        }
        if (count < 2) return name;
        return winrt::hstring(std::wstring(name) + L" (" + std::to_wstring(position) + L" of " + std::to_wstring(count) + L")");
    }
    bool IsControlElementCore() { return true; }
    bool IsContentElementCore() { return true; }
    auto GetChildrenCore() { return TreeAutomationChildren(m_index, m_generation); }
    winrt::Windows::Foundation::IInspectable NavigateCore(DevToolsTreeAP::AutomationNavigationDirection direction)
    {
        CheckAvailable();
        const size_t parent = g_treeAutomationParents[m_index];
        SetParent(DevToolsTreeAP::FrameworkElementAutomationPeer::CreatePeerForElement(
            parent == DevToolsTreeLayout::kNoRow ? g_treeAutomationRoot
                : TreeAutomationElement(g_treeControls[parent])));
        return FrameworkElementAutomationPeerT::NavigateCore(direction);
    }
    winrt::Windows::Foundation::IInspectable GetPatternCore(DevToolsTreeAP::PatternInterface pattern)
    {
        if (pattern == DevToolsTreeAP::PatternInterface::SelectionItem ||
            pattern == DevToolsTreeAP::PatternInterface::ExpandCollapse ||
            pattern == DevToolsTreeAP::PatternInterface::Invoke)
            return *this;
        return FrameworkElementAutomationPeerT::GetPatternCore(pattern);
    }
    void CheckAvailable()
    {
        if (m_generation != g_treeRowsGen || m_index >= g_treeControls.size() ||
            !g_treeControls[m_index] || !g_treeVisible[m_index])
            throw winrt::hresult_error(UIA_E_ELEMENTNOTAVAILABLE);
    }
    bool IsSelected()
    {
        CheckAvailable();
        return g_treeControls[m_index] == g_selCtrl;
    }
    DevToolsTreeProvider::IRawElementProviderSimple SelectionContainer()
    {
        CheckAvailable();
        return ProviderFromPeer(DevToolsTreeAP::FrameworkElementAutomationPeer::CreatePeerForElement(g_treeAutomationRoot));
    }
    void Select()
    {
        CheckAvailable();
        if (!Owner().as<DevToolsXC::Control>().IsEnabled())
            throw winrt::hresult_error(UIA_E_ELEMENTNOTENABLED);
        if (m_index >= g_nodeSinks.size() || !g_nodeSinks[m_index])
            throw winrt::hresult_error(UIA_E_INVALIDOPERATION);
        g_nodeSinks[m_index]->Invoke(g_treeControls[m_index], nullptr);
    }
    void Invoke() { Select(); }
    void AddToSelection()
    {
        if (g_selCtrl && !IsSelected()) throw winrt::hresult_error(UIA_E_INVALIDOPERATION);
        Select();
    }
    void RemoveFromSelection()
    {
        if (!IsSelected()) return;
        g_selectedWire = 0;
        SelectRowVisual(nullptr);
        if (g_ctx.highlightFn) g_ctx.highlightFn(0, true);
        ShowPropsForNode(0, L"(no selection)", L"", true);
    }
    DevToolsTreeA::ExpandCollapseState ExpandCollapseState()
    {
        CheckAvailable();
        if (m_index >= g_treeA11y.size() || !g_treeA11y[m_index].branch)
            return DevToolsTreeA::ExpandCollapseState::LeafNode;
        for (size_t parent : g_treeAutomationParents)
            if (parent == m_index) return DevToolsTreeA::ExpandCollapseState::Expanded;
        return DevToolsTreeA::ExpandCollapseState::Collapsed;
    }
    void SetExpanded(bool expanded)
    {
        const auto state = ExpandCollapseState();
        if (state == DevToolsTreeA::ExpandCollapseState::LeafNode)
            throw winrt::hresult_error(UIA_E_INVALIDOPERATION);
        if (!SetTreeRowExpanded(m_index, expanded))
            throw winrt::hresult_error(UIA_E_INVALIDOPERATION, L"Clear the tree filter to expand or collapse rows.");
    }
    void Expand() { SetExpanded(true); }
    void Collapse() { SetExpanded(false); }
    size_t m_index;
    unsigned m_generation;
};

struct DevToolsTreePanel : DevToolsXC::StackPanelT<DevToolsTreePanel>
{
    explicit DevToolsTreePanel(unsigned generation) : m_generation(generation) {}
    DevToolsTreeAP::AutomationPeer OnCreateAutomationPeer()
    {
        return winrt::make<DevToolsTreePeer>(*this, m_generation);
    }
    unsigned m_generation;
};

struct DevToolsTreeRow : DevToolsXC::ButtonT<DevToolsTreeRow>
{
    DevToolsTreeRow(size_t index, unsigned generation) : m_index(index), m_generation(generation) {}
    DevToolsTreeAP::AutomationPeer OnCreateAutomationPeer()
    {
        return winrt::make<DevToolsTreeItemPeer>(*this, m_index, m_generation);
    }
    void OnKeyDown(DevToolsXI::KeyRoutedEventArgs const& args)
    {
        if (m_generation == g_treeRowsGen && HandleTreeRowKey(m_index, static_cast<int>(args.Key())))
            args.Handled(true);
        else ButtonT::OnKeyDown(args);
    }
    size_t m_index;
    unsigned m_generation;
};

static void NotifyTreeSelectionChanged(void* previous, void* current)
{
    if (!g_treeAutomationRoot) return;
    try {
        for (void* control : {previous, current}) {
            if (!control) continue;
            auto peer = DevToolsTreeAP::FrameworkElementAutomationPeer::FromElement(TreeAutomationElement(control));
            if (!peer) continue;
            peer.RaisePropertyChangedEvent(DevToolsTreeA::SelectionItemPatternIdentifiers::IsSelectedProperty(),
                winrt::box_value(control == previous), winrt::box_value(control == current));
            peer.RaiseAutomationEvent(control == current
                ? DevToolsTreeAP::AutomationEvents::SelectionItemPatternOnElementSelected
                : DevToolsTreeAP::AutomationEvents::SelectionItemPatternOnElementRemovedFromSelection);
        }
        if (auto root = DevToolsTreeAP::FrameworkElementAutomationPeer::FromElement(g_treeAutomationRoot))
            root.RaiseAutomationEvent(DevToolsTreeAP::AutomationEvents::SelectionPatternOnInvalidated);
    } catch (const winrt::hresult_error& error) {
        DevToolsOverlayLog(L"tree.automation selection notification failed hr=0x%08X", error.code().value);
    }
}

static void SyncTreeAutomation()
{
    if (!g_treeAutomationRoot) return;
    const auto previous = std::move(g_treeAutomationParents);
    auto available = g_treeVisible;
    for (size_t i = 0; i < available.size(); ++i) if (!g_treeControls[i]) available[i] = 0;
    g_treeAutomationParents = DevToolsTreeLayout::ComputeVisibleParents(SnapshotLayoutRows(), available);
    std::vector<int> counts(g_treeRows.size(), 0), positions(g_treeRows.size(), 0), levels(g_treeRows.size(), 0);
    std::vector<char> wasExpanded(g_treeRows.size(), 0), expanded(g_treeRows.size(), 0);
    int roots = 0, rootPosition = 0;
    for (size_t parent : previous) if (parent != DevToolsTreeLayout::kNoRow) wasExpanded[parent] = 1;
    for (size_t i = 0; i < g_treeAutomationParents.size(); ++i) {
        if (!available[i]) continue;
        const size_t parent = g_treeAutomationParents[i];
        if (parent == DevToolsTreeLayout::kNoRow) ++roots;
        else { ++counts[parent]; expanded[parent] = 1; }
    }
    try {
        for (size_t i = 0; i < g_treeAutomationParents.size(); ++i) {
            if (!g_treeVisible[i] || !g_treeControls[i]) continue;
            const size_t parent = g_treeAutomationParents[i];
            levels[i] = parent == DevToolsTreeLayout::kNoRow ? 1 : levels[parent] + 1;
            auto element = TreeAutomationElement(g_treeControls[i]);
            DevToolsTreeA::AutomationProperties::SetLevel(element, levels[i]);
            DevToolsTreeA::AutomationProperties::SetPositionInSet(element,
                parent == DevToolsTreeLayout::kNoRow ? ++rootPosition : ++positions[parent]);
            DevToolsTreeA::AutomationProperties::SetSizeOfSet(element,
                parent == DevToolsTreeLayout::kNoRow ? roots : counts[parent]);
            if (auto peer = DevToolsTreeAP::FrameworkElementAutomationPeer::FromElement(element)) {
                peer.InvalidatePeer();
                if (wasExpanded[i] != expanded[i])
                    peer.RaisePropertyChangedEvent(DevToolsTreeA::ExpandCollapsePatternIdentifiers::ExpandCollapseStateProperty(),
                        winrt::box_value(wasExpanded[i] ? DevToolsTreeA::ExpandCollapseState::Expanded : DevToolsTreeA::ExpandCollapseState::Collapsed),
                        winrt::box_value(expanded[i] ? DevToolsTreeA::ExpandCollapseState::Expanded : DevToolsTreeA::ExpandCollapseState::Collapsed));
            }
        }
        if (auto root = DevToolsTreeAP::FrameworkElementAutomationPeer::FromElement(g_treeAutomationRoot)) {
            root.InvalidatePeer();
            root.RaiseAutomationEvent(DevToolsTreeAP::AutomationEvents::StructureChanged);
            root.RaiseAutomationEvent(DevToolsTreeAP::AutomationEvents::SelectionPatternOnInvalidated);
        }
    } catch (const winrt::hresult_error& error) {
        DevToolsOverlayLog(L"tree.automation hierarchy update failed hr=0x%08X", error.code().value);
    }
}
