// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsWindow.cpp; included into that translation unit, not compiled on its own.

// The selected element is live: the pane follows property changes the app makes in code. The runtime reports a
// change only for a dependency property it is given, so the commonly changed ones are registered for the selected
// element; a change re-reads just the properties that changed and updates the rows that differ.

static DevToolsWindowElementFn g_elementFn = nullptr;
static DevToolsWindowValuesFn  g_valuesFn = nullptr;
void DevToolsWindow_SetElementResolver(DevToolsWindowElementFn element, DevToolsWindowValuesFn values)
{
    g_elementFn = element;
    g_valuesFn = values;
}

// The app's element for a tree handle, AddRef'd; null when it is gone or the resolver isn't set.
static IInspectable* ResolveLiveElement(InstanceHandle wire) { return g_elementFn ? g_elementFn(wire) : nullptr; }

struct LiveWatch
{
    InstanceHandle wire = 0;
    unsigned gen = 0;
    winrt::weak_ref<DevToolsX::DependencyObject> element;
    std::vector<std::pair<DevToolsX::DependencyProperty, int64_t>> tokens;
    std::vector<std::wstring> changed;   // properties reported since the last read
};
static LiveWatch g_live;
static UINT_PTR  g_liveTimer = 0;
static size_t    g_liveRegistered = 0, g_liveUnregistered = 0;

// The dependency properties apps commonly change from code, for this element's classes.
static std::vector<std::pair<const wchar_t*, DevToolsX::DependencyProperty>> LiveProperties(const DevToolsX::DependencyObject& o)
{
    std::vector<std::pair<const wchar_t*, DevToolsX::DependencyProperty>> out;
    auto add = [&](const wchar_t* name, const DevToolsX::DependencyProperty& dp) {
        for (const auto& existing : out) if (wcscmp(existing.first, name) == 0) return;
        out.emplace_back(name, dp);
    };
    using namespace winrt::Microsoft::UI::Xaml;
    using namespace winrt::Microsoft::UI::Xaml::Controls;
    if (o.try_as<TextBlock>()) {
        add(L"Text", TextBlock::TextProperty()); add(L"Foreground", TextBlock::ForegroundProperty());
        add(L"FontSize", TextBlock::FontSizeProperty()); add(L"FontWeight", TextBlock::FontWeightProperty());
        add(L"FontFamily", TextBlock::FontFamilyProperty()); add(L"FontStyle", TextBlock::FontStyleProperty());
        add(L"TextWrapping", TextBlock::TextWrappingProperty()); add(L"TextAlignment", TextBlock::TextAlignmentProperty());
        add(L"TextTrimming", TextBlock::TextTrimmingProperty()); add(L"Padding", TextBlock::PaddingProperty());
        add(L"MaxLines", TextBlock::MaxLinesProperty());
    }
    if (o.try_as<TextBox>()) {
        add(L"Text", TextBox::TextProperty()); add(L"PlaceholderText", TextBox::PlaceholderTextProperty());
        add(L"Header", TextBox::HeaderProperty()); add(L"IsReadOnly", TextBox::IsReadOnlyProperty());
    }
    if (o.try_as<Primitives::ToggleButton>()) add(L"IsChecked", Primitives::ToggleButton::IsCheckedProperty());
    if (o.try_as<ToggleSwitch>()) { add(L"IsOn", ToggleSwitch::IsOnProperty()); add(L"Header", ToggleSwitch::HeaderProperty()); }
    if (o.try_as<Primitives::RangeBase>()) {
        add(L"Value", Primitives::RangeBase::ValueProperty()); add(L"Minimum", Primitives::RangeBase::MinimumProperty());
        add(L"Maximum", Primitives::RangeBase::MaximumProperty());
    }
    if (o.try_as<Primitives::Selector>()) {
        add(L"SelectedIndex", Primitives::Selector::SelectedIndexProperty());
        add(L"SelectedItem", Primitives::Selector::SelectedItemProperty());
    }
    if (o.try_as<ItemsControl>()) add(L"ItemsSource", ItemsControl::ItemsSourceProperty());
    if (o.try_as<ContentControl>()) add(L"Content", ContentControl::ContentProperty());
    if (o.try_as<ContentPresenter>()) {
        add(L"Content", ContentPresenter::ContentProperty()); add(L"Foreground", ContentPresenter::ForegroundProperty());
        add(L"Background", ContentPresenter::BackgroundProperty());
    }
    if (o.try_as<Control>()) {
        add(L"Foreground", Control::ForegroundProperty()); add(L"Background", Control::BackgroundProperty());
        add(L"BorderBrush", Control::BorderBrushProperty()); add(L"BorderThickness", Control::BorderThicknessProperty());
        add(L"Padding", Control::PaddingProperty()); add(L"FontSize", Control::FontSizeProperty());
        add(L"FontWeight", Control::FontWeightProperty()); add(L"FontFamily", Control::FontFamilyProperty());
        add(L"IsEnabled", Control::IsEnabledProperty()); add(L"CornerRadius", Control::CornerRadiusProperty());
        add(L"HorizontalContentAlignment", Control::HorizontalContentAlignmentProperty());
        add(L"VerticalContentAlignment", Control::VerticalContentAlignmentProperty());
    }
    if (o.try_as<Border>()) {
        add(L"Background", Border::BackgroundProperty()); add(L"BorderBrush", Border::BorderBrushProperty());
        add(L"BorderThickness", Border::BorderThicknessProperty()); add(L"CornerRadius", Border::CornerRadiusProperty());
        add(L"Padding", Border::PaddingProperty());
    }
    if (o.try_as<StackPanel>()) {
        add(L"Orientation", StackPanel::OrientationProperty()); add(L"Spacing", StackPanel::SpacingProperty());
        add(L"Padding", StackPanel::PaddingProperty());
    }
    if (o.try_as<Grid>()) {
        add(L"Padding", Grid::PaddingProperty()); add(L"RowSpacing", Grid::RowSpacingProperty());
        add(L"ColumnSpacing", Grid::ColumnSpacingProperty());
    }
    if (o.try_as<Panel>()) add(L"Background", Panel::BackgroundProperty());
    if (o.try_as<Shapes::Shape>()) {
        add(L"Fill", Shapes::Shape::FillProperty()); add(L"Stroke", Shapes::Shape::StrokeProperty());
        add(L"StrokeThickness", Shapes::Shape::StrokeThicknessProperty());
    }
    if (o.try_as<Image>()) { add(L"Source", Image::SourceProperty()); add(L"Stretch", Image::StretchProperty()); }
    if (o.try_as<IconElement>()) add(L"Foreground", IconElement::ForegroundProperty());
    if (o.try_as<FontIcon>()) add(L"Glyph", FontIcon::GlyphProperty());
    if (o.try_as<ProgressRing>()) add(L"IsActive", ProgressRing::IsActiveProperty());
    if (o.try_as<InfoBar>()) {
        add(L"IsOpen", InfoBar::IsOpenProperty()); add(L"Title", InfoBar::TitleProperty());
        add(L"Message", InfoBar::MessageProperty()); add(L"Severity", InfoBar::SeverityProperty());
    }
    if (o.try_as<FrameworkElement>()) {
        add(L"Width", FrameworkElement::WidthProperty()); add(L"Height", FrameworkElement::HeightProperty());
        add(L"MinWidth", FrameworkElement::MinWidthProperty()); add(L"MinHeight", FrameworkElement::MinHeightProperty());
        add(L"MaxWidth", FrameworkElement::MaxWidthProperty()); add(L"MaxHeight", FrameworkElement::MaxHeightProperty());
        add(L"Margin", FrameworkElement::MarginProperty());
        add(L"HorizontalAlignment", FrameworkElement::HorizontalAlignmentProperty());
        add(L"VerticalAlignment", FrameworkElement::VerticalAlignmentProperty());
        add(L"Tag", FrameworkElement::TagProperty()); add(L"FlowDirection", FrameworkElement::FlowDirectionProperty());
        add(L"RequestedTheme", FrameworkElement::RequestedThemeProperty()); add(L"Style", FrameworkElement::StyleProperty());
    }
    if (o.try_as<UIElement>()) {
        add(L"Opacity", UIElement::OpacityProperty()); add(L"Visibility", UIElement::VisibilityProperty());
        add(L"IsHitTestVisible", UIElement::IsHitTestVisibleProperty());
    }
    return out;
}

static void LiveUnwatch()
{
    if (g_liveTimer) { KillTimer(nullptr, g_liveTimer); g_liveTimer = 0; }
    if (auto element = g_live.element.get()) {
        for (const auto& [dp, token] : g_live.tokens) {
            try { element.UnregisterPropertyChangedCallback(dp, token); ++g_liveUnregistered; } catch (...) {}
        }
    } else {
        // The element is gone, and its callbacks with it.
        g_liveUnregistered += g_live.tokens.size();
    }
    if (!g_live.tokens.empty())
        DevToolsOverlayLog(L"route1.live stopped (registered=%zu unregistered=%zu)", g_liveRegistered, g_liveUnregistered);
    g_live = {};
}

static void UpdateSelectedTreePreview();

static void CALLBACK LiveRefreshProc(HWND, UINT, UINT_PTR id, DWORD)
{
    KillTimer(nullptr, id);
    g_liveTimer = 0;
    if (!g_live.wire || g_live.wire != g_selWire || g_live.gen != g_route1Gen || !g_valuesFn) return;
    if (g_ctx.wireLiveFn && !g_ctx.wireLiveFn(g_live.wire)) { LiveUnwatch(); return; }
    // An edit is in progress: its row is not replaced under the user; look again once it is committed or cancelled.
    if (!CanRereadSelection(nullptr)) { g_liveTimer = SetTimer(nullptr, 0, 250, &LiveRefreshProc); return; }
    LARGE_INTEGER freq{}, t0{}, t1{};
    QueryPerformanceFrequency(&freq);
    QueryPerformanceCounter(&t0);
    const std::vector<std::wstring> names = std::move(g_live.changed);
    g_live.changed.clear();
    std::vector<DevToolsCardRow> fresh;
    if (!g_valuesFn(g_live.wire, names, &fresh)) return;
    const auto plan = DevToolsLiveRows::Decide(g_propRows, fresh, false,
        [](const std::wstring& prop) { return PropIsExpanded(prop); });
    if (plan.action == DevToolsLiveRows::Action::Rebuild) {
        DevToolsWindow_RereadSelection(g_live.wire);
    } else if (plan.action == DevToolsLiveRows::Action::UpdateRows) {
        for (const auto& [i, k] : plan.changed) {
            DevToolsCardRow& row = g_propRows[i];
            row.value = fresh[k].value;
            row.valueState = fresh[k].valueState;
            row.source = fresh[k].source;
            if (i < g_propRowLabels.size()) {
                std::wstring label = row.name + L" " + row.value + L" " + row.authored;
                for (auto& c : label) if (c >= L'A' && c <= L'Z') c = static_cast<wchar_t>(c + 32);
                g_propRowLabels[i] = std::move(label);
            }
            if (void* value = PaneRowChild(row.name, L"PValue")) {
                PopulateRowValue(static_cast<IInspectable*>(value), row);
                reinterpret_cast<IUnknown*>(value)->Release();
            }
        }
    }
    // The caption can change even when no shown row did (it may come from a property the pane does not list).
    UpdateSelectedTreePreview();
    QueryPerformanceCounter(&t1);
    DevToolsOverlayLog(L"route1.live update action=%d read=%zu changed=%zu uiMs=%.2f", (int)plan.action,
        names.size(), plan.changed.size(), (double)(t1.QuadPart - t0.QuadPart) * 1000.0 / (double)freq.QuadPart);
}

// Called after the pane shows an element: follow that element, and stop following the previous one.
static void LiveWatchSelection()
{
    if (g_live.wire == g_selWire && g_live.gen == g_route1Gen && g_live.element.get()) return;
    LiveUnwatch();
    if (!g_selWire || !g_elementFn) return;
    winrt::com_ptr<IInspectable> raw;
    raw.attach(g_elementFn(g_selWire));
    if (!raw) return;
    try {
        winrt::Windows::Foundation::IInspectable object{ nullptr };
        winrt::copy_from_abi(object, raw.get());
        const auto element = object.try_as<DevToolsX::DependencyObject>();
        if (!element) return;
        g_live.wire = g_selWire;
        g_live.gen = g_route1Gen;
        g_live.element = winrt::make_weak(element);
        const InstanceHandle wire = g_selWire;
        const unsigned gen = g_route1Gen;
        for (const auto& [name, dp] : LiveProperties(element)) {
            const int64_t token = element.RegisterPropertyChangedCallback(dp,
                [wire, gen, prop = std::wstring(name)](DevToolsX::DependencyObject const&, DevToolsX::DependencyProperty const&) {
                    if (wire != g_live.wire || gen != g_route1Gen) return;
                    if (std::find(g_live.changed.begin(), g_live.changed.end(), prop) == g_live.changed.end())
                        g_live.changed.push_back(prop);
                    // A burst of changes (an animation, a layout pass) is read once.
                    if (!g_liveTimer) g_liveTimer = SetTimer(nullptr, 0, 100, &LiveRefreshProc);
                });
            g_live.tokens.emplace_back(dp, token);
            ++g_liveRegistered;
        }
        DevToolsOverlayLog(L"route1.live watching %zu properties (registered=%zu unregistered=%zu)",
            g_live.tokens.size(), g_liveRegistered, g_liveUnregistered);
    } catch (...) {
        DevToolsOverlayLog(L"route1.live could not follow the selected element");
        LiveUnwatch();
    }
}

// Reads a tree node's caption again; it is read once when its row is built. True when it changed.
static bool RereadTreeCaption(size_t original, std::wstring* before)
{
    if (!g_ctx.previewFn || original >= g_allTreeNodes.size()) return false;
    *before = g_allTreeNodes[original].preview;
    g_allTreeNodes[original].previewRead = false;
    g_ctx.previewFn(g_allTreeNodes, { original });
    return g_allTreeNodes[original].preview != *before;
}

// The selected element's caption in the tree ("Count: 3"), which follows its properties too.
static void UpdateSelectedTreePreview()
{
    const auto row = g_nodeIdxByWire.find(g_selWire);
    if (row == g_nodeIdxByWire.end() || row->second >= g_renderedOriginalIndices.size() ||
        row->second >= g_treeControls.size() || !g_treeControls[row->second]) return;
    const size_t original = g_renderedOriginalIndices[row->second];
    std::wstring before;
    if (!RereadTreeCaption(original, &before)) return;
    const std::wstring after = g_allTreeNodes[original].preview;
    try {
        winrt::Windows::Foundation::IInspectable object{ nullptr };
        winrt::copy_from_abi(object, static_cast<IInspectable*>(g_treeControls[row->second]));
        const auto button = object.as<DevToolsXC::ContentControl>();
        const auto inlines = button.Content().as<DevToolsXC::TextBlock>().Inlines();
        // The caption ends with the preview between quotes: "\u201C", preview, "\u201D".
        const uint32_t n = inlines.Size();
        if (before.empty() || after.empty() || n < 3) { RefreshTreeFromApp(true); return; }
        inlines.GetAt(n - 2).as<DevToolsXDoc::Run>().Text(after);
        // The row's accessible name ends with the same preview.
        std::wstring name = DevToolsXA::AutomationProperties::GetName(button).c_str();
        if (name.size() >= before.size() && name.compare(name.size() - before.size(), before.size(), before) == 0)
            DevToolsXA::AutomationProperties::SetName(button, name.substr(0, name.size() - before.size()) + after);
    } catch (...) {
        RefreshTreeFromApp(true);
    }
}
