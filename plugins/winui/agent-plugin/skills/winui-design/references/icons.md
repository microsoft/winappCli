# Icons

## Icons

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
