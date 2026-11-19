// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsWindow.cpp; included into that translation unit, not compiled on its own.

static bool g_syncingLaunchSettings = false;

static void ConfigureSettingsCard(const DevToolsXC::Grid& card)
{
    // Toolkit SettingsCard uses these card-width breakpoints, not the window width.
    card.SizeChanged([gen = g_route1Gen](const winrt::Windows::Foundation::IInspectable& sender, const DevToolsX::SizeChangedEventArgs&) {
        if (gen != g_route1Gen) return;
        try {
            const auto grid = sender.as<DevToolsXC::Grid>();
            const bool wrapped = grid.ActualWidth() < 476;
            const auto icon = grid.Children().GetAt(0).as<DevToolsX::FrameworkElement>();
            const auto header = grid.Children().GetAt(1).as<DevToolsX::FrameworkElement>();
            const auto actions = grid.Children().GetAt(2).as<DevToolsX::FrameworkElement>();
            icon.Visibility(grid.ActualWidth() < 286 ? DevToolsX::Visibility::Collapsed : DevToolsX::Visibility::Visible);
            header.Margin(DevToolsX::Thickness{ 0, 0, wrapped ? 0.0 : 24.0, 0 });
            DevToolsXC::Grid::SetRow(actions, wrapped ? 1 : 0);
            DevToolsXC::Grid::SetColumn(actions, wrapped ? 1 : 2);
            actions.HorizontalAlignment(wrapped ? DevToolsX::HorizontalAlignment::Left : DevToolsX::HorizontalAlignment::Right);
            grid.RowSpacing(wrapped ? 8 : 0);
        } catch (const winrt::hresult_error& e) {
            DevToolsOverlayLog(L"route1.settings card layout failed hr=0x%08X", e.code().value);
        }
    });
}

static void ConfigureSettingsNavigation()
{
    if (!g_shell.nav) return;
    g_shell.settingsTab = g_shell.nav.SettingsItem().try_as<DevToolsXC::NavigationViewItem>();
    if (!g_shell.settingsTab) return;
    DevToolsXA::AutomationProperties::SetAutomationId(g_shell.settingsTab, L"ViewTabSettings");
    DevToolsXA::AutomationProperties::SetName(g_shell.settingsTab, L"Settings (inspector)");
    if (g_inspectorView == InspectorView::Settings) {
        g_navSyncing = true;
        g_shell.nav.SelectedItem(g_shell.settingsTab);
        g_navSyncing = false;
    }
}

static std::wstring DefaultLaunchMode()
{
    const std::wstring mode = DevToolsSettingsGetText(L"DefaultMode");
    return mode == L"off" || mode == L"headless" ? mode : L"on";
}

static void RefreshLaunchSettings()
{
    if (!g_shell.startEnabled || !g_shell.startShown || !g_shell.startToolbarPreference) return;
    g_syncingLaunchSettings = true;
    try {
        const std::wstring mode = DefaultLaunchMode();
        g_shell.startEnabled.IsOn(mode != L"off");
        // Keep the toolbar choice while disabled, as the old menu did.
        if (mode != L"off") g_shell.startShown.IsOn(mode == L"on");
        if (g_shell.startEnabledState) g_shell.startEnabledState.Text(g_shell.startEnabled.IsOn() ? L"On" : L"Off");
        if (g_shell.startShownState) g_shell.startShownState.Text(g_shell.startShown.IsOn() ? L"On" : L"Off");
        g_shell.startToolbarPreference.Visibility(mode == L"off" ? DevToolsX::Visibility::Collapsed : DevToolsX::Visibility::Visible);
    } catch (const winrt::hresult_error& e) {
        DevToolsOverlayLog(L"route1.settings refresh failed hr=0x%08X", e.code().value);
    }
    g_syncingLaunchSettings = false;
}

static bool SaveLaunchSettings(bool enabled, bool shown)
{
    const char* mode = !enabled ? "off" : shown ? "on" : "headless";
    const bool saved = DevToolsSettingsSetText(L"DefaultMode", mode);
    DevToolsOverlayLog(L"route1.settings default mode -> %S saved=%d", mode, static_cast<int>(saved));
    return saved;
}

static void OnLaunchSettingsToggled()
{
    if (g_syncingLaunchSettings) return;
    try {
        const bool saved = SaveLaunchSettings(g_shell.startEnabled.IsOn(), g_shell.startShown.IsOn());
        if (g_shell.settingsError) {
            g_shell.settingsError.Text(saved ? L"" : L"Could not save launch preferences. Check access to your .winapp state folder and try again.");
            g_shell.settingsError.Visibility(saved ? DevToolsX::Visibility::Collapsed : DevToolsX::Visibility::Visible);
        }
        RefreshLaunchSettings();
    } catch (const winrt::hresult_error& e) {
        DevToolsOverlayLog(L"route1.settings toggle failed hr=0x%08X", e.code().value);
    }
}
