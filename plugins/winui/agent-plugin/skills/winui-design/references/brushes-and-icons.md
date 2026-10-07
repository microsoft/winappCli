# Brushes and icons reference

Load this when looking up a specific brush key by purpose, deciding between WinUI icon types, or wiring `Icon` / `IconSource` properties on a control.

Sources: [XAML theme resources](https://learn.microsoft.com/en-us/windows/apps/develop/platform/xaml/xaml-theme-resources), [Icons for Windows apps](https://learn.microsoft.com/en-us/windows/apps/design/style/icons), [Segoe Fluent Icons font](https://learn.microsoft.com/en-us/windows/apps/design/style/segoe-fluent-icons-font), `IconElement` / `IconSource` API references on Microsoft Learn.

For runnable samples (full XAML + C# with the right `Glyph` codepoints, brush keys in context, etc.) prefer `winapp find-ui "<query>"`, then fetch the best scenario with `winapp find-ui --id <id>`.

---

## Part 1 — Brush catalogue

Every theme brush below is defined in WinUI's `Common_themeresources_any.xaml` and resolves automatically across Light, Dark, and Contrast themes. Reference them with `{ThemeResource <name>}` at usage sites; don't hard-code colors.

There are matching `Color` resources (same name without the `Brush` suffix) — use those only when an API specifically wants a `Color`. For a `Brush` property (`Background`, `Foreground`, `BorderBrush`, `Stroke`), always end the key in `Brush`.

### Text

| Brush | Use for |
|-------|---------|
| `TextFillColorPrimaryBrush` | Standard body text and headings; the default `Foreground` for most controls |
| `TextFillColorSecondaryBrush` | Subdued metadata, captions under titles, helper text |
| `TextFillColorTertiaryBrush` | Inactive but legible labels, placeholder text |
| `TextFillColorDisabledBrush` | Text in a disabled control |
| `TextOnAccentFillColorPrimaryBrush` | Text drawn on top of an accent fill (white-equivalent in Light, near-black in Dark) |
| `TextOnAccentFillColorSecondaryBrush` | Subdued text on an accent surface |
| `TextOnAccentFillColorDisabledBrush` | Disabled text on an accent surface |
| `AccentTextFillColorPrimaryBrush` | Accented foreground text (e.g. hyperlinks); follow with the accent variants for hover/pressed |
| `AccentTextFillColorSecondaryBrush` / `TertiaryBrush` | Lighter or muted accented text |
| `AccentTextFillColorDisabledBrush` | Disabled hyperlink-style text |

### Control fills (button, slider, combo box, etc.)

| Brush | Use for |
|-------|---------|
| `ControlFillColorDefaultBrush` | Resting fill for buttons, combo boxes, slider tracks |
| `ControlFillColorSecondaryBrush` | Pointer-over fill on the same controls |
| `ControlFillColorTertiaryBrush` | Pressed fill |
| `ControlFillColorDisabledBrush` | Disabled fill |
| `ControlFillColorTransparentBrush` | Resting fill for a subtle (text-only) button |
| `ControlFillColorInputActiveBrush` | Fill for an active text input |
| `ControlStrongFillColorDefaultBrush` | Slider thumbs, toggle knobs, focused indicators |
| `ControlStrongFillColorDisabledBrush` | Same, when disabled |
| `ControlAltFillColorTransparentBrush` | Resting fill for a check-box-style control |
| `ControlAltFillColorSecondaryBrush` | Pointer-over for the same |
| `ControlAltFillColorTertiaryBrush` | Pressed |
| `ControlAltFillColorQuarternaryBrush` | Selected / checked state |
| `ControlAltFillColorDisabledBrush` | Disabled |
| `ControlSolidFillColorDefaultBrush` | Opaque button fill where transparency would be wrong (e.g. above content) |

### Strokes (borders, dividers, focus rings)

| Brush | Use for |
|-------|---------|
| `ControlStrokeColorDefaultBrush` | Resting 1-px border on buttons, inputs, cards |
| `ControlStrokeColorSecondaryBrush` | Stronger / pointer-over border |
| `ControlStrokeColorOnAccentDefaultBrush` | Border for a control sitting on an accent surface |
| `ControlStrokeColorOnAccentSecondaryBrush` | Same, with elevation |
| `ControlStrongStrokeColorDefaultBrush` | High-contrast border (e.g. selected card) |
| `ControlStrongStrokeColorDisabledBrush` | Disabled selected border |
| `FocusStrokeColorOuterBrush` | Outer ring of the system focus rectangle |
| `FocusStrokeColorInnerBrush` | Inner ring of the focus rectangle |
| `CardStrokeColorDefaultBrush` | Border on a content card |
| `CardStrokeColorDefaultSolidBrush` | Same, but opaque (use over translucent surfaces) |
| `DividerStrokeColorDefaultBrush` | Horizontal / vertical separator lines |
| `SurfaceStrokeColorDefaultBrush` | Border on an in-app surface (sidebar, command bar) |
| `SurfaceStrokeColorFlyoutBrush` | Border on a flyout / menu / tooltip surface |

### Surfaces and layers

| Brush | Use for |
|-------|---------|
| `LayerFillColorDefaultBrush` | The translucent overlay that sits above Mica on the content layer |
| `LayerFillColorAltBrush` | Same purpose, slightly different opacity for layered content |
| `LayerOnAccentFillColorDefaultBrush` | Layer above an accent surface |
| `LayerOnMicaBaseAltFillColorDefaultBrush` | Layer above Mica Alt for the content area of a tabbed window |
| `SolidBackgroundFillColorBaseBrush` | Opaque base page background |
| `SolidBackgroundFillColorSecondaryBrush` | Opaque secondary surface |
| `SolidBackgroundFillColorTertiaryBrush` | Opaque tertiary surface (deepest layer) |
| `SolidBackgroundFillColorQuarternaryBrush` | Opaque quaternary surface (between body and chrome) |
| `SolidBackgroundFillColorBaseAltBrush` | Used in commanding contexts where Mica Alt would normally apply |
| `CardBackgroundFillColorDefaultBrush` | Filled card body in Light/Dark |
| `CardBackgroundFillColorSecondaryBrush` | Subdued / nested card |
| `SubtleFillColorTransparentBrush` | Transparent resting fill for "ghost" surfaces (subtle buttons, list-item rows) |
| `SubtleFillColorSecondaryBrush` | Pointer-over for the same |
| `SubtleFillColorTertiaryBrush` | Pressed |
| `SubtleFillColorDisabledBrush` | Disabled |

### Accent

| Brush | Use for |
|-------|---------|
| `AccentFillColorDefaultBrush` | Primary accent fill (e.g. `AccentButtonStyle` background) |
| `AccentFillColorSecondaryBrush` | Pointer-over on accent fill |
| `AccentFillColorTertiaryBrush` | Pressed on accent fill |
| `AccentFillColorDisabledBrush` | Disabled accent fill |
| `AccentFillColorSelectedTextBackgroundBrush` | Selection highlight in editable text |
| `SystemAccentColor` (Color) | Raw accent — use the variants `SystemAccentColorLight1/2/3` and `SystemAccentColorDark1/2/3` to shift up/down |

Pair an accent background with `TextOnAccentFillColorPrimaryBrush` for foreground text.

### Acrylic and Mica

| Brush | Use for |
|-------|---------|
| `AcrylicBackgroundFillColorDefaultBrush` | Flyouts, menus, tooltips |
| `AcrylicBackgroundFillColorBaseBrush` | In-app sidebars, command bars |
| `AcrylicInAppFillColorDefaultBrush` | In-app acrylic with default tint |
| `AcrylicInAppFillColorSecondaryBrush` | In-app acrylic with stronger tint |

Mica is a backdrop, not a brush — apply it via `Window.SystemBackdrop = new MicaBackdrop()` (or `MicaBackdrop { Kind = MicaKind.BaseAlt }` for Mica Alt). The content layer above Mica picks up `LayerFillColorDefaultBrush`; the commanding layer above Mica Alt picks up `LayerOnMicaBaseAltFillColorDefaultBrush`.

### High Contrast — the only brushes allowed in an HC dictionary

The `SystemColor*Brush` family. Pair by role; never set `Opacity` on these.

| Background | Foreground | Role |
|------------|-----------|------|
| `SystemColorWindowColorBrush` | `SystemColorWindowTextColorBrush` | Pages, panes, popups, general content |
| `SystemColorWindowColorBrush` | `SystemColorHotlightColorBrush` | Hyperlinks |
| `SystemColorWindowColorBrush` | `SystemColorGrayTextColorBrush` | Disabled / inactive |
| `SystemColorHighlightColorBrush` | `SystemColorHighlightTextColorBrush` | Selected, hovered, pressed, in-progress |
| `SystemColorButtonFaceColorBrush` | `SystemColorButtonTextColorBrush` | Buttons and interactive surfaces |

If the platform brushes already work in contrast themes (they usually do), keep the dictionary empty: `<ResourceDictionary x:Key="HighContrast" />`.

---

## Part 2 — Icons

WinUI exposes icons through two parallel API trees:

- **`IconElement`** — a `FrameworkElement`. Drop it directly into the visual tree. Cannot live in a `ResourceDictionary`.
- **`IconSource`** — non-element. Share via `ResourceDictionary` and consume from properties that end in `IconSource` (`TabViewItem.IconSource`, `InfoBar.IconSource`, etc.). Wrap with `IconSourceElement` to use as an element.

Whether a control's icon slot wants an element or a source is encoded in the property name: `Icon` → `IconElement`, `IconSource` → `IconSource`. Both trees expose the same six concrete types:

| Element / Source | Reach for it when |
|------------------|-------------------|
| `FontIcon` / `FontIconSource` | You have a Unicode glyph from a glyph font. **The default pick** — Segoe Fluent Icons (Windows 11) is referenced via `{ThemeResource SymbolThemeFontFamily}` and gives you 1000+ Fluent-aligned glyphs. Crisp at any size, theme-aware. |
| `SymbolIcon` / `SymbolIconSource` | The glyph you need is in the [`Symbol` enumeration](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.symbol). Shortest XAML (`Symbol="Save"`), no Unicode lookup. Limited to enumerated glyphs. |
| `PathIcon` / `PathIconSource` | You have a custom vector shape (logo, brand mark, custom geometry) you need as an icon. Resolution-independent, recolours via `Foreground`. |
| `BitmapIcon` / `BitmapIconSource` | You have a single PNG/JPG/BMP. Monochrome treatment — uses `Foreground` to recolour a mask. Avoid for anything that needs to scale. |
| `ImageIcon` / `ImageIconSource` | You have a multi-colour raster or vector asset that should *not* be recoloured by the theme. Treats the source as opaque image data. |
| `AnimatedIcon` / `AnimatedIconSource` | You need an icon that animates on visual-state changes (Lottie / `IRichAnimatedVisualSource`). Always pair with a `FallbackIconSource` for downlevel / contrast themes. |

### Default pick patterns

Symbol (shortest):
```xml
<AppBarButton Icon="Send" Label="Send" />
```

FontIcon with Segoe Fluent Icons:
```xml
<FontIcon Glyph="&#xE724;" />              <!-- Cloud -->
<FontIcon Glyph="&#xE713;" FontFamily="{ThemeResource SymbolThemeFontFamily}" />
```

The `SymbolThemeFontFamily` theme resource is the canonical font reference — it resolves to Segoe Fluent Icons on Windows 11 and falls back to Segoe MDL2 Assets on Windows 10. Never hard-code the font family.

Glyph codepoint lookup: use the [Segoe Fluent Icons font page](https://learn.microsoft.com/en-us/windows/apps/design/style/segoe-fluent-icons-font) for the canonical table, or pull a worked sample with `winapp find-ui "icon button"`.

### When icon-only is OK

Icon-only buttons must carry an accessible name:

```xml
<Button AutomationProperties.Name="Save"
        ToolTipService.ToolTip="Save (Ctrl+S)">
    <SymbolIcon Symbol="Save" />
</Button>
```

Don't rely on the glyph alone — screen readers won't announce "save icon" without `AutomationProperties.Name`.

### Reusing an icon definition

Define once in `App.xaml`, consume anywhere via `IconSourceElement`:

```xml
<!-- App.xaml -->
<FontIconSource x:Key="CertIconSource" Glyph="&#xEB95;" />
```
```xml
<!-- usage -->
<IconSourceElement IconSource="{StaticResource CertIconSource}" />
<InfoBar IconSource="{StaticResource CertIconSource}" Title="Certificate expired" />
```

### IconElement-bearing controls (cheat-sheet)

Controls whose `Icon` property takes an `IconElement`:
- `AppBarButton`, `AppBarToggleButton`
- `MenuFlyoutItem`, `MenuFlyoutSubItem`
- `AutoSuggestBox.QueryIcon`
- `NavigationViewItem`
- `SelectorBarItem`

Controls whose `IconSource` property takes an `IconSource`:
- `TabViewItem`, `SwipeItem`
- `InfoBar`, `InfoBadge`
- `TeachingTip`
- `XamlUICommand`
- `AnimatedIcon.FallbackIconSource`, `AnimatedIconSource.FallbackIconSource`

If you find yourself wrapping a `FontIcon` in `IconSourceElement` repeatedly, you've picked the wrong slot — switch the parent's property to its `IconSource`-typed sibling if it exists.
