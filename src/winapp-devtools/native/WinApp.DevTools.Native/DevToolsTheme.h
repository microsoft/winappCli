// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

#include <windows.h>
#include <cstdio>
#include <string>

namespace DevToolsTheme
{
constexpr int kDefault = 0;
constexpr int kLight = 1;
constexpr int kDark = 2;

inline int ToggleTarget(int actualTheme) noexcept
{
    return actualTheme == kDark ? kLight : kDark;
}
}

// The overlay paints from its own fixed, fully opaque palette. Under High Contrast the palette is
// derived from system colours (COLOR_WINDOW / COLOR_WINDOWTEXT / COLOR_HIGHLIGHT) so it honours the user's
// contrast pairing.
struct DevToolsPalette {
    std::wstring surface;
    std::wstring surfaceAlt;
    std::wstring border;
// This is a LIGHT hairline instead: it reads as an edge against a dark app, and against a light app it is
// unobtrusive because there the dark body is already doing the separating. It is a pre-BLENDED opaque colour
// (light-over-surface computed here, not an alpha), so app content never shows through it.
    std::wstring rim;
    std::wstring textPrimary;
    std::wstring textSecondary;
    std::wstring textTertiary;
    std::wstring accent;
    std::wstring accentText;
    std::wstring liveRest;
    std::wstring caution;
    bool highContrast = false;
};

inline std::wstring DevToolsSysColorHex(int idx)
{
    DWORD c = GetSysColor(idx);
    wchar_t buf[10];
    _snwprintf_s(buf, _countof(buf), _TRUNCATE, L"#FF%02X%02X%02X", GetRValue(c), GetGValue(c), GetBValue(c));
    return std::wstring(buf);
}

inline const DevToolsPalette& DevToolsBuildPalette()
{
    static const DevToolsPalette pal = []() -> DevToolsPalette {
        DevToolsPalette p;
        HIGHCONTRAST hc; hc.cbSize = sizeof(hc); hc.dwFlags = 0; hc.lpszDefaultScheme = nullptr;
        p.highContrast = SystemParametersInfoW(SPI_GETHIGHCONTRAST, sizeof(hc), &hc, 0)
                         && (hc.dwFlags & HCF_HIGHCONTRASTON) != 0;
        if (p.highContrast) {
            const std::wstring win  = DevToolsSysColorHex(COLOR_WINDOW);
            const std::wstring text = DevToolsSysColorHex(COLOR_WINDOWTEXT);
            const std::wstring hl   = DevToolsSysColorHex(COLOR_HIGHLIGHT);
            const std::wstring hlt  = DevToolsSysColorHex(COLOR_HIGHLIGHTTEXT);
            p.surface = win; p.surfaceAlt = win; p.border = text; p.rim = text;
            p.textPrimary = text; p.textSecondary = text; p.textTertiary = text;
            p.accent = hl; p.accentText = hlt; p.liveRest = text; p.caution = hl;
        } else {
// The overlay's own warm-neutral palette: fixed, fully opaque, independent of the app theme.
            p.surface       = L"#FF262220";
            p.surfaceAlt    = L"#FF2F2A25";
            p.border        = L"#FF443C34";
// white at ~30% blended over the surface -- an OPAQUE #6F6A66, computed once here so nothing translucent ever
// reaches the tree.
            p.rim           = L"#FF6F6A66";
            p.textPrimary   = L"#FFECE5DC";
            p.textSecondary = L"#FFB3A89B";
            p.textTertiary  = L"#FF8A8078";
            p.accent        = L"#FF4CC2FF";
            p.accentText    = L"#FF06222E";
            p.liveRest      = L"#FFC1A889";
            p.caution       = L"#FFFFC83D";
        }
        return p;
    }();
    return pal;
}

inline const DevToolsPalette& DevToolsInverseAcrylicPalette()
{
    static const DevToolsPalette pal = {
        L"{ThemeResource AcrylicBackgroundFillColorDefaultBrush}",
        L"{ThemeResource ControlFillColorSecondaryBrush}",
        L"{ThemeResource DividerStrokeColorDefaultBrush}",
        L"{ThemeResource SurfaceStrokeColorFlyoutBrush}",
        L"{ThemeResource TextFillColorPrimaryBrush}",
        L"{ThemeResource TextFillColorSecondaryBrush}",
        L"{ThemeResource TextFillColorTertiaryBrush}",
        L"{ThemeResource AccentFillColorDefaultBrush}",
        L"{ThemeResource TextOnAccentFillColorPrimaryBrush}",
        L"{ThemeResource TextFillColorSecondaryBrush}",
        L"{ThemeResource SystemFillColorCautionBrush}",
        false,
    };
    return pal;
}
