---
name: settings-dashboard
description: "Build a WinUI 3 system settings/dashboard app with many control types"
type: new
app_name: SettingsDashboard
requirements:
  - "NavigationView with left pane must provide navigation between at least 4 pages: Dashboard, Appearance, Network, and About"
  - "Dashboard page must show system info cards (OS version, machine name, CPU, RAM) using InfoBar or styled card layout"
  - "Appearance page must have a ToggleSwitch for dark/light theme that applies immediately app-wide"
  - "Appearance page must have a ComboBox to select accent color from at least 5 predefined options"
  - "Appearance page must have a Slider to adjust UI scaling/font size with live preview"
  - "Appearance page must have RadioButtons to choose between Compact, Normal, and Comfortable spacing modes"
  - "Network page must display a ListView of network adapters with name, type, status, and IP address columns"
  - "Network page must have an AutoSuggestBox to filter the adapter list by name"
  - "Network page must show connection details in an Expander that expands to show subnet mask, gateway, and DNS"
  - "About page must display app version, build date, and links using RichTextBlock or HyperlinkButton"
  - "A NumberBox on the Dashboard page must let the user set a refresh interval (1-60 seconds) for system info updates"
  - "A ContentDialog must appear when the user tries to reset all settings, offering Confirm and Cancel options"
---

Build me a system settings and dashboard app — like a simplified Windows Settings.

Requirements:
- WinUI 3 desktop app with Fluent Design (Mica backdrop, custom title bar)
- NavigationView on the left with at least 4 pages: Dashboard, Appearance, Network, About
- Dashboard page: system info cards showing OS version, machine name, CPU, RAM. A NumberBox to set auto-refresh interval (1-60 seconds)
- Appearance page: ToggleSwitch for dark/light theme (applies immediately), ComboBox for accent color (5+ options), Slider for font size with live preview, RadioButtons for spacing mode (Compact/Normal/Comfortable)
- Network page: ListView showing network adapters with columns (name, type, status, IP). AutoSuggestBox to filter by name. Expander for each adapter showing detailed info (subnet, gateway, DNS)
- About page: app version, build info, HyperlinkButtons for links
- ContentDialog for "Reset all settings" confirmation
- All interactive controls need AutomationProperties.AutomationId
- Frame-based page navigation
- Make sure the app builds and runs
