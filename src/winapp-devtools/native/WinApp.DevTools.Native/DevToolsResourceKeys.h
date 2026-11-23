// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

// Resource keys a property can take: the element's dictionaries, its ancestors', then the application's. Shared by
// the in-app inspector's key box and Resource.list so the two offer the same keys. UI thread only.

#include <windows.h>
#include <set>
#include <string>
#include <vector>

#include "DevToolsProjected.h"

struct DevToolsResourceKeyList
{
    std::vector<std::wstring> keys;
    std::set<std::wstring> seen;
    std::set<void*> dictionaries;
    size_t entries = 0;
    bool partial = false;
};

// Suggestions exclude known incompatible types; any brush fits any brush property.
inline std::wstring DevToolsResourceKeys_TypeFamily(const std::wstring& type)
{
    const size_t dot = type.find_last_of(L'.');
    const std::wstring s = dot == std::wstring::npos ? type : type.substr(dot + 1);
    if (s.size() >= 5 && s.compare(s.size() - 5, 5, L"Brush") == 0) return L"Brush";
    return s;
}

// WinUI theme brushes offered even when the app's dictionaries cannot be read.
inline const std::vector<std::wstring>& DevToolsResourceKeys_BrushNames()
{
    static const std::vector<std::wstring> names = {
        L"TextFillColorPrimaryBrush",
        L"TextFillColorSecondaryBrush",
        L"TextFillColorTertiaryBrush",
        L"TextFillColorDisabledBrush",
        L"AccentTextFillColorPrimaryBrush",
        L"TextOnAccentFillColorPrimaryBrush",
        L"AccentFillColorDefaultBrush",
        L"AccentFillColorSecondaryBrush",
        L"ControlFillColorDefaultBrush",
        L"CardBackgroundFillColorDefaultBrush",
        L"LayerFillColorDefaultBrush",
        L"SolidBackgroundFillColorBaseBrush",
        L"SystemFillColorSuccessBrush",
        L"SystemFillColorCautionBrush",
        L"SystemFillColorCriticalBrush",
    };
    return names;
}

inline bool DevToolsResourceKeys_HighContrast()
{
    HIGHCONTRASTW hc{};
    hc.cbSize = sizeof(hc);
    if (!SystemParametersInfoW(SPI_GETHIGHCONTRAST, sizeof(hc), &hc, 0)) return false;
    return (hc.dwFlags & HCF_HIGHCONTRASTON) != 0;
}

inline const wchar_t* DevToolsResourceKeys_Theme(DevToolsX::ElementTheme theme, bool highContrast)
{
    if (highContrast) return L"HighContrast";
    return theme == DevToolsX::ElementTheme::Dark ? L"Dark" : L"Light";
}

inline void DevToolsResourceKeys_CollectDictionary(const DevToolsX::ResourceDictionary& dictionary,
                                                   const std::wstring& want, const wchar_t* theme,
                                                   DevToolsResourceKeyList& out)
{
    if (!dictionary || !out.dictionaries.insert(winrt::get_abi(dictionary)).second) return;
    if (out.dictionaries.size() > 128) { out.partial = true; return; }
    for (const auto& entry : dictionary) {
        if (++out.entries > 4096) { out.partial = true; return; }
        const auto key = entry.Key().try_as<winrt::Windows::Foundation::IPropertyValue>();
        if (!key || key.Type() != winrt::Windows::Foundation::PropertyType::String) continue;
        const std::wstring name(key.GetString());
        if (!out.seen.insert(name).second) continue;
        const auto value = entry.Value();
        if (!value) continue;
        std::wstring type(winrt::get_class_name(value));
        const std::wstring boxed = L"Windows.Foundation.IReference`1<";
        if (type.starts_with(boxed) && type.back() == L'>')
            type = type.substr(boxed.size(), type.size() - boxed.size() - 1);
        if (DevToolsResourceKeys_TypeFamily(type) == want) out.keys.push_back(name);
    }
    const auto themes = dictionary.ThemeDictionaries();
    auto key = winrt::box_value(theme);
    if (!themes.HasKey(key)) key = winrt::box_value(L"Default");
    if (themes.HasKey(key))
        DevToolsResourceKeys_CollectDictionary(themes.Lookup(key).try_as<DevToolsX::ResourceDictionary>(), want, theme, out);
    const auto merged = dictionary.MergedDictionaries();
    for (uint32_t i = merged.Size(); i > 0 && !out.partial; --i)
        DevToolsResourceKeys_CollectDictionary(merged.GetAt(i - 1), want, theme, out);
}

// Walks from `selected` up to the application, in the element's current theme. A failure marks the list partial.
inline void DevToolsResourceKeys_Collect(const winrt::Windows::Foundation::IInspectable& selected,
                                         const std::wstring& want, DevToolsResourceKeyList& out)
{
    // Something that is not a XAML object has no resource scope to search.
    if (!selected || !selected.try_as<DevToolsX::DependencyObject>()) { out.partial = true; return; }
    auto node = selected.try_as<DevToolsX::DependencyObject>();
    const auto selectedFe = selected.try_as<DevToolsX::FrameworkElement>();
    const auto actual = selectedFe ? selectedFe.ActualTheme() : DevToolsX::ElementTheme::Default;
    const wchar_t* theme = DevToolsResourceKeys_Theme(actual, DevToolsResourceKeys_HighContrast());
    size_t ancestors = 0;
    while (node && !out.partial) {
        if (++ancestors > 128) { out.partial = true; break; }
        const auto fe = node.try_as<DevToolsX::FrameworkElement>();
        if (fe) DevToolsResourceKeys_CollectDictionary(fe.Resources(), want, theme, out);
        node = fe && fe.Parent() ? fe.Parent() : DevToolsXM::VisualTreeHelper::GetParent(node);
    }
    if (!out.partial)
        DevToolsResourceKeys_CollectDictionary(DevToolsX::Application::Current().Resources(), want, theme, out);
}

inline void DevToolsResourceKeys_Add(const std::wstring& key, DevToolsResourceKeyList& out)
{
    if (!key.empty() && out.seen.insert(key).second) out.keys.push_back(key);
}

inline void DevToolsResourceKeys_AddDefaults(const std::wstring& want, DevToolsResourceKeyList& out)
{
    if (want != L"Brush") return;
    for (const auto& k : DevToolsResourceKeys_BrushNames()) DevToolsResourceKeys_Add(k, out);
}
