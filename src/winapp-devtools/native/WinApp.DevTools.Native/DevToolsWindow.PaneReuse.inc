// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsWindow.cpp; included into that translation unit, not compiled on its own.

// WinUI keeps memory for every element that enters and leaves the inspector window, so a details pane rebuilt for
// each selection grew the app for as long as it ran (#1283). The pane is built once per window and updated in place.
// Its markup-built sections (the source line and Size & spacing) keep the elements of each shape they have shown and
// copy new values into them; only a shape they have not shown before adds elements.

// The elements the pane's markup nests: a panel's children, a border's child, an expander's header and a content
// control's content.
static void PaneTreeChildren(DevToolsX::DependencyObject const& node, std::vector<DevToolsX::DependencyObject>& out)
{
    if (const auto panel = node.try_as<DevToolsXC::Panel>()) {
        for (auto const& child : panel.Children()) out.push_back(child);
    } else if (const auto border = node.try_as<DevToolsXC::Border>()) {
        if (const auto child = border.Child()) out.push_back(child);
    } else if (const auto content = node.try_as<DevToolsXC::ContentControl>()) {
        if (const auto expander = node.try_as<DevToolsXC::Expander>())
            if (const auto header = expander.Header().try_as<DevToolsX::UIElement>()) out.push_back(header);
        if (const auto child = content.Content().try_as<DevToolsX::UIElement>()) out.push_back(child);
    }
}

// Two trees with the same shape have the same element types, children, grid tracks and text runs, so one can take
// the other's values.
static void PaneTreeShape(DevToolsX::DependencyObject const& node, std::wstring& shape)
{
    shape += winrt::get_class_name(node);
    if (const auto grid = node.try_as<DevToolsXC::Grid>())
        shape += L"/" + std::to_wstring(grid.ColumnDefinitions().Size()) + L"x" + std::to_wstring(grid.RowDefinitions().Size());
    if (const auto text = node.try_as<DevToolsXC::TextBlock>())
        if (text.ReadLocalValue(DevToolsXC::TextBlock::TextProperty()) == DevToolsX::DependencyProperty::UnsetValue())
            for (auto const& item : text.Inlines()) { shape += L"|"; shape += winrt::get_class_name(item); }
    if (const auto expander = node.try_as<DevToolsXC::Expander>())
        if (expander.Header().try_as<DevToolsX::UIElement>()) shape += L"^";
    std::vector<DevToolsX::DependencyObject> children;
    PaneTreeChildren(node, children);
    shape += L"(";
    for (auto const& child : children) { PaneTreeShape(child, shape); shape += L","; }
    shape += L")";
}

static void CopyLocalValue(DevToolsX::DependencyObject const& to, DevToolsX::DependencyObject const& from,
                           DevToolsX::DependencyProperty const& property)
{
    const auto unset = DevToolsX::DependencyProperty::UnsetValue();
    const auto value = from.ReadLocalValue(property);
    if (value == unset) {
        if (to.ReadLocalValue(property) != unset) to.ClearValue(property);
    } else if (const auto dashes = value.try_as<DevToolsXM::DoubleCollection>()) {
        // A collection belongs to one element, so the live element gets its own copy.
        DevToolsXM::DoubleCollection copy;
        for (const double v : dashes) copy.Append(v);
        to.SetValue(property, copy);
    } else {
        to.SetValue(property, value);
    }
}

// Copy what the pane's markup sets from a freshly loaded tree onto a live tree of the same shape.
static void CopyPaneState(DevToolsX::DependencyObject const& live, DevToolsX::DependencyObject const& fresh)
{
    namespace Shapes = winrt::Microsoft::UI::Xaml::Shapes;
    using DevToolsX::UIElement;
    using DevToolsX::FrameworkElement;
    const auto copy = [&](std::initializer_list<DevToolsX::DependencyProperty> properties) {
        size_t index = 0;
        for (auto const& property : properties) {
            try {
                CopyLocalValue(live, fresh, property);
            } catch (const winrt::hresult_error& e) {
                DevToolsOverlayLog(L"route1.props copy failed on %s property %zu hr=0x%08X",
                                   winrt::get_class_name(live).c_str(), index, e.code().value);
                throw;
            }
            ++index;
        }
    };
    if (fresh.try_as<UIElement>()) {
        copy({ UIElement::VisibilityProperty(), UIElement::OpacityProperty(), UIElement::IsHitTestVisibleProperty(),
               FrameworkElement::WidthProperty(), FrameworkElement::HeightProperty(), FrameworkElement::MinWidthProperty(),
               FrameworkElement::MinHeightProperty(), FrameworkElement::MaxHeightProperty(), FrameworkElement::MarginProperty(),
               FrameworkElement::HorizontalAlignmentProperty(), FrameworkElement::VerticalAlignmentProperty(),
               FrameworkElement::StyleProperty(), DevToolsXC::Grid::RowProperty(), DevToolsXC::Grid::ColumnProperty(),
               DevToolsXC::Grid::RowSpanProperty(), DevToolsXC::Grid::ColumnSpanProperty(),
               DevToolsXC::ToolTipService::ToolTipProperty(), DevToolsXA::AutomationProperties::NameProperty(),
               DevToolsXA::AutomationProperties::HelpTextProperty(), DevToolsXA::AutomationProperties::AutomationIdProperty(),
               DevToolsXA::AutomationProperties::LiveSettingProperty(),
               DevToolsXA::AutomationProperties::AccessibilityViewProperty() });
    }
    if (fresh.try_as<DevToolsXC::Panel>()) copy({ DevToolsXC::Panel::BackgroundProperty() });
    if (fresh.try_as<DevToolsXC::StackPanel>())
        copy({ DevToolsXC::StackPanel::SpacingProperty(), DevToolsXC::StackPanel::OrientationProperty() });
    if (const auto grid = fresh.try_as<DevToolsXC::Grid>()) {
        copy({ DevToolsXC::Grid::ColumnSpacingProperty(), DevToolsXC::Grid::RowSpacingProperty() });
        const auto target = live.as<DevToolsXC::Grid>();
        for (uint32_t i = 0; i < grid.ColumnDefinitions().Size(); ++i)
            target.ColumnDefinitions().GetAt(i).Width(grid.ColumnDefinitions().GetAt(i).Width());
        for (uint32_t i = 0; i < grid.RowDefinitions().Size(); ++i)
            target.RowDefinitions().GetAt(i).Height(grid.RowDefinitions().GetAt(i).Height());
    }
    if (fresh.try_as<DevToolsXC::Border>())
        copy({ DevToolsXC::Border::BackgroundProperty(), DevToolsXC::Border::BorderBrushProperty(),
               DevToolsXC::Border::BorderThicknessProperty(), DevToolsXC::Border::PaddingProperty(),
               DevToolsXC::Border::CornerRadiusProperty() });
    if (const auto text = fresh.try_as<DevToolsXC::TextBlock>()) {
        copy({ DevToolsXC::TextBlock::ForegroundProperty(), DevToolsXC::TextBlock::FontSizeProperty(),
               DevToolsXC::TextBlock::FontWeightProperty(), DevToolsXC::TextBlock::FontFamilyProperty(),
               DevToolsXC::TextBlock::FontStyleProperty(), DevToolsXC::TextBlock::TextWrappingProperty(),
               DevToolsXC::TextBlock::TextTrimmingProperty(), DevToolsXC::TextBlock::CharacterSpacingProperty(),
               DevToolsXC::TextBlock::IsTextSelectionEnabledProperty() });
        const auto target = live.as<DevToolsXC::TextBlock>();
        const auto inlines = text.Inlines();
        // Text set as an attribute is a value; only markup with explicit runs is copied run by run.
        if (text.ReadLocalValue(DevToolsXC::TextBlock::TextProperty()) != DevToolsX::DependencyProperty::UnsetValue() ||
            inlines.Size() == 0) {
            target.Text(text.Text());
        } else {
            for (uint32_t i = 0; i < inlines.Size(); ++i) {
                const auto run = inlines.GetAt(i).try_as<DevToolsXDoc::Run>();
                if (!run) continue;
                const auto liveRun = target.Inlines().GetAt(i);
                liveRun.as<DevToolsXDoc::Run>().Text(run.Text());
                for (auto const& property : { DevToolsXDoc::TextElement::ForegroundProperty(),
                                              DevToolsXDoc::TextElement::FontWeightProperty(),
                                              DevToolsXDoc::TextElement::FontFamilyProperty() })
                    CopyLocalValue(liveRun, run, property);
            }
        }
    }
    if (fresh.try_as<Shapes::Shape>())
        copy({ Shapes::Shape::FillProperty(), Shapes::Shape::StrokeProperty(), Shapes::Shape::StrokeThicknessProperty(),
               Shapes::Shape::StrokeDashArrayProperty(), Shapes::Shape::StrokeDashCapProperty() });
    if (fresh.try_as<DevToolsXC::Control>())
        copy({ DevToolsXC::Control::PaddingProperty(), DevToolsXC::Control::FontSizeProperty(),
               DevToolsXC::Control::ForegroundProperty(), DevToolsXC::Control::BackgroundProperty(),
               DevToolsXC::Control::IsEnabledProperty(), DevToolsXC::Control::HorizontalContentAlignmentProperty() });
    if (const auto content = fresh.try_as<DevToolsXC::ContentControl>())
        if (!content.Content().try_as<UIElement>()) copy({ DevToolsXC::ContentControl::ContentProperty() });
    if (fresh.try_as<DevToolsXC::Expander>()) copy({ DevToolsXC::Expander::IsExpandedProperty() });
    if (fresh.try_as<DevToolsXC::FontIcon>())
        copy({ DevToolsXC::FontIcon::GlyphProperty(), DevToolsXC::FontIcon::FontSizeProperty(),
               DevToolsXC::IconElement::ForegroundProperty() });

    std::vector<DevToolsX::DependencyObject> liveChildren, freshChildren;
    PaneTreeChildren(live, liveChildren);
    PaneTreeChildren(fresh, freshChildren);
    for (size_t i = 0; i < freshChildren.size() && i < liveChildren.size(); ++i)
        CopyPaneState(liveChildren[i], freshChildren[i]);
}

// A part of the pane built from markup. Every shape it has shown stays in its host, collapsed while another shape
// is on screen, so nothing is added to or removed from the window when a shape is shown again.
struct PaneSlot
{
    struct Tree
    {
        std::wstring shape;
        DevToolsX::UIElement root{ nullptr };
        // Wrappers a tree's handlers reach weakly; XAML holding the element doesn't keep the wrapper alive.
        std::vector<winrt::Windows::Foundation::IInspectable> held;
    };
    DevToolsXC::Panel host{ nullptr };
    std::vector<Tree> trees; // most recently shown first
};
static const size_t kPaneSlotShapes = 6;
static const wchar_t* const kPaneSectionOpen =
    L"<StackPanel xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\""
    L" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">";
static PaneSlot g_sourceSlot, g_layoutSlot, g_referencesSlot;

static void ShowPaneSlotTree(PaneSlot& slot, size_t shown)
{
    for (size_t i = 0; i < slot.trees.size(); ++i)
        slot.trees[i].root.Visibility(i == shown ? DevToolsX::Visibility::Visible : DevToolsX::Visibility::Collapsed);
}

static void HidePaneSlot(PaneSlot& slot)
{
    try { ShowPaneSlotTree(slot, SIZE_MAX); } catch (const winrt::hresult_error&) {}
}

// Show one element's markup in the slot. Returns the tree on screen, or null when the markup did not load.
// *built is true when the tree is new, so the caller wires its controls once.
static DevToolsX::UIElement ShowInPaneSlot(PaneSlot& slot, const std::wstring& markup, bool* built)
{
    if (built) *built = false;
    if (!slot.host) return nullptr;
    IInspectable* raw = nullptr;
    if (FAILED(LoadMarkupWin(InlineResourceLiterals(markup, nullptr, nullptr), &raw)) || !raw) {
        DevToolsOverlayLog(L"route1.props section markup failed to load");
        return nullptr;
    }
    winrt::Windows::Foundation::IInspectable object{ nullptr };
    winrt::attach_abi(object, raw);
    try {
        const auto fresh = object.as<DevToolsX::UIElement>();
        std::wstring shape;
        PaneTreeShape(fresh, shape);
        for (size_t i = 0; i < slot.trees.size(); ++i) {
            if (slot.trees[i].shape != shape) continue;
            const auto tree = slot.trees[i];
            CopyPaneState(tree.root, fresh);
            slot.trees.erase(slot.trees.begin() + i);
            slot.trees.insert(slot.trees.begin(), tree);
            ShowPaneSlotTree(slot, 0);
            return tree.root;
        }
        auto children = slot.host.Children();
        children.Append(fresh);
        slot.trees.insert(slot.trees.begin(), { std::move(shape), fresh, {} });
        if (slot.trees.size() > kPaneSlotShapes) {
            uint32_t at = 0;
            if (children.IndexOf(slot.trees.back().root, at)) children.RemoveAt(at);
            slot.trees.pop_back();
        }
        ShowPaneSlotTree(slot, 0);
        if (built) *built = true;
        return fresh;
    } catch (const winrt::hresult_error& e) {
        DevToolsOverlayLog(L"route1.props section update failed hr=0x%08X", e.code().value);
        return nullptr;
    }
}

static void ReleasePaneSlot(PaneSlot& slot)
{
    slot.trees.clear();
    slot.host = nullptr;
}

// Handlers on a property row's expansion, other than the KindEditorSinks that RetireKindEditors removes. A reused
// row removes them before it is wired for another property, so an old handler never acts on the new one.
struct PaneEventRevoker { std::wstring path; std::function<void()> revoke; };
static std::vector<PaneEventRevoker> g_paneEventRevokers;

static void TrackPaneEvent(const std::wstring& path, std::function<void()> revoke)
{
    g_paneEventRevokers.push_back({ path, std::move(revoke) });
}

static HRESULT WirePaneEvent(const std::wstring& path, void* publisher, void* sink,
                             HRESULT (*add)(void*, void*, __int64*), HRESULT (*remove)(void*, __int64))
{
    __int64 token = 0;
    const HRESULT hr = add(publisher, sink, &token);
    if (SUCCEEDED(hr)) {
        winrt::com_ptr<IUnknown> held;
        held.copy_from(reinterpret_cast<IUnknown*>(publisher));
        TrackPaneEvent(path, [held, token, remove] { remove(held.get(), token); });
    }
    return hr;
}

// Remove the handlers of `path` and its child properties, or of every row when `path` is empty.
static void RevokePaneEvents(const std::wstring& path = std::wstring())
{
    std::vector<PaneEventRevoker> revoking;
    for (size_t i = g_paneEventRevokers.size(); i-- > 0; ) {
        const auto& p = g_paneEventRevokers[i].path;
        if (!path.empty() && p != path && p.compare(0, path.size() + 1, path + L".") != 0) continue;
        revoking.push_back(std::move(g_paneEventRevokers[i]));
        g_paneEventRevokers.erase(g_paneEventRevokers.begin() + i);
    }
    for (auto& r : revoking) {
        try { r.revoke(); } catch (const winrt::hresult_error&) {}
    }
}
