// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

internal static class Notes
{
    /// <summary>
    /// Cross-control family disambiguation. Single source of truth for "when to pick
    /// X vs Y vs Z" — referenced by every family member so we don't repeat the same
    /// guidance in 3-5 places (and don't drift between control entries).
    /// </summary>
    private static readonly Dictionary<string, string> FamilyGuide = new()
    {
        ["Tabs"] = "TabView=closable doc tabs (browser-style); Pivot=static sections (legacy, prefer SelectorBar for new code); SelectorBar=modern Win11 flat selector; Segmented (Toolkit)=2-5 mutually-exclusive short toggles",
        ["Toolbars"] = "CommandBar=app-wide toolbar (PrimaryCommands always-visible, SecondaryCommands overflow); TabbedCommandBar (Toolkit)=Office ribbon-style with multiple tabs of AppBarButton groups",
        ["Popups"] = "ContentDialog=modal blocking decision; Flyout=inline contextual UI; MenuFlyout=context/dropdown menu; TeachingTip=non-blocking targeted hint; InfoBar=persistent inline status (Severity Info/Success/Warning/Error); AppNotification=system-wide toast",
        ["Collections"] = "ListView=vertical text rows; GridView=image/card grid; ItemsView=modern flexible (LinedFlow/Stack/UniformGrid layouts; replaces ListView/GridView for new code); ItemsRepeater=fully custom layout (no selection, no scroll, wrap in ScrollViewer); TreeView=hierarchy",
        ["TextInput"] = "TextBox=plain; RichEditBox=formatted (bold/italic/lists); AutoSuggestBox=search-as-you-type; ComboBox=closed-list dropdown; TokenizingTextBox (Toolkit)=removable chip/tag pills; RichSuggestBox (Toolkit)=@mention/#hashtag inline tokens",
        ["Numeric"] = "NumberBox=precise typed value with validation; Slider=visual single-value range; RangeSelector (Toolkit)=two-handle min/max range",
        ["Navigation"] = "NavigationView=app sidebar with built-in UX (SelectionChanged + Frame); SplitView=low-level pane primitive (build the list yourself); BreadcrumbBar=path-style trail (ItemsSource only)",
        ["Settings"] = "SettingsCard=single setting row (Win11 standard); SettingsExpander=group of related cards under one collapsible header",
        ["Sizers"] = "GridSplitter (Toolkit)=column/row resize within a Grid; ContentSizer (Toolkit)=single-axis resize of a sibling element outside a Grid",
        ["LayoutPanels"] = "StackPanel=linear; Grid=row/column with sizes; Canvas=absolute positioning; DockPanel (Toolkit)=Dock attached property + LastChildFill; WrapPanel (Toolkit)=row/col then wrap; UniformGrid (Toolkit)=equal cells; StaggeredPanel (Toolkit)=Pinterest masonry; ItemsRepeater Layout=virtualized variants (StackLayout/UniformGridLayout/WrapLayout/StaggeredLayout)",
        ["ColorPickers"] = "Microsoft.UI.Xaml.Controls.ColorPicker=basic, dependency-free; CommunityToolkit.WinUI.Controls.ColorPicker=adds ShowAccentColors + IsColorPaletteVisible swatches + IsColorSpectrumVisible toggle; ColorPickerButton (Toolkit)=compact button+flyout wrapper",
    };

    /// <summary>Maps each control name to its family key.</summary>
    private static readonly Dictionary<string, string> ControlToFamily = new()
    {
        ["TabView"] = "Tabs",
        ["Pivot"] = "Tabs",
        ["SelectorBar"] = "Tabs",
        ["Segmented"] = "Tabs",
        ["CommandBar"] = "Toolbars",
        ["TabbedCommandBar"] = "Toolbars",
        ["ContentDialog"] = "Popups",
        ["Flyout"] = "Popups",
        ["MenuFlyout"] = "Popups",
        ["TeachingTip"] = "Popups",
        ["InfoBar"] = "Popups",
        ["AppNotification"] = "Popups",
        ["ListView"] = "Collections",
        ["GridView"] = "Collections",
        ["ItemsView"] = "Collections",
        ["ItemsRepeater"] = "Collections",
        ["TreeView"] = "Collections",
        ["TextBox"] = "TextInput",
        ["RichEditBox"] = "TextInput",
        ["AutoSuggestBox"] = "TextInput",
        ["ComboBox"] = "TextInput",
        ["TokenizingTextBox"] = "TextInput",
        ["RichSuggestBox"] = "TextInput",
        ["NumberBox"] = "Numeric",
        ["Slider"] = "Numeric",
        ["RangeSelector"] = "Numeric",
        ["NavigationView"] = "Navigation",
        ["SplitView"] = "Navigation",
        ["BreadcrumbBar"] = "Navigation",
        ["SettingsCard"] = "Settings",
        ["SettingsExpander"] = "Settings",
        ["GridSplitter"] = "Sizers",
        ["ContentSizer"] = "Sizers",
        ["DockPanel"] = "LayoutPanels",
        ["WrapPanel"] = "LayoutPanels",
        ["UniformGrid"] = "LayoutPanels",
        ["StaggeredPanel"] = "LayoutPanels",
        ["ColorPicker"] = "ColorPickers",
        ["ColorPickerButton"] = "ColorPickers",
    };

    /// <summary>
    /// Per-control pitfalls. ONLY control-specific guidance — cross-control "use X vs Y"
    /// disambiguation lives in FamilyGuide above.
    /// </summary>
    private static readonly Dictionary<string, string[]> KnownPitfalls = new()
    {
        // ─── Data binding (covers x:Bind / Binding / DataTemplate / x:DataType) ───
        ["Binding"] = [
            "x:Bind OneWay/TwoWay needs INotifyPropertyChanged on source. Easiest: inherit ViewModel from ObservableObject (CommunityToolkit.Mvvm), mark fields [ObservableProperty], collections ObservableCollection<T>. Else WMC1506 + UI silently never updates.",
            "x:Bind enforces target type at compile time. UIElement-typed prop bound to FrameworkElement-typed (e.g. TeachingTip.Target) → WMC1121. Bind to x:Name'd element, or expose prop as the more specific type."
        ],
        ["Templates"] = [
            "Every <DataTemplate> with x:Bind MUST set x:DataType — without it x:Bind silently falls back to {Binding} (no IntelliSense, no errors).",
            "x:Bind in a DataTemplate references the x:DataType item, NOT the parent Page's ViewModel. Use ElementName or RelativeSource to escape."
        ],

        // ─── Tabs ───
        ["TabView"] = [
            "TabCloseRequested provides args.Tab — don't construct TabViewTabCloseRequestedEventArgs manually.",
            "IsClosable on individual TabViewItem controls which tabs can be closed.",
            "Set VerticalAlignment=\"Stretch\" on TabView so content fills available space.",
            "TabViewItem.Content can be ANY UIElement (TextBox, Grid, UserControl); use Frame only for page navigation."
        ],

        // ─── Collections ───
        ["TreeView"] = [
            "Don't use C# record/class as TreeViewNode.Content with x:Bind — WinRT can't roundtrip custom managed types through IInspectable.",
            "Data-bound trees: ItemsSource → DataTemplate with TreeViewItem.ItemsSource={x:Bind Children}.",
            "TreeViewItemTemplateSelector doesn't exist in WinUI 3 — use one DataTemplate with x:Bind, or ItemContainerStyleSelector + ItemTemplate.",
            "TreeViewItem has no .Icon — put Image/FontIcon/SymbolIcon as first child of horizontal StackPanel inside DataTemplate.",
            "File-explorer sidebar: ObservableCollection<FolderNode> with Children:ObservableCollection<FolderNode>; root DataTemplate binds ItemsSource={x:Bind Children}."
        ],
        ["ListView"] = [
            "Don't wrap ListView in ScrollViewer — breaks virtualization.",
            "Prefer x:Bind in DataTemplates over Binding for performance.",
            "WinUI 3 has no DataGrid. For tables: ListView with Grid-based ItemTemplate (columns) + separate header Grid above; CommunityToolkit GridSplitter for resizable cols.",
            "Sortable columns: handle header Click events, re-sort ObservableCollection or use AdvancedCollectionView (Toolkit)."
        ],
        ["GridView"] = [
            "GridView has no built-in column resize — for spreadsheet UI use ListView + Grid ItemTemplate + GridSplitter."
        ],
        ["ItemsRepeater"] = [
            "ItemsRepeater is a layout primitive — NO selection, NO scrolling. Wrap in ScrollViewer.",
            "x:Bind OneWay binding needs INPC source — see Binding entry."
        ],
        ["ItemsView"] = [
            "x:Bind OneWay binding needs INPC source — see Binding entry."
        ],

        // ─── Popups ───
        ["ContentDialog"] = [
            "Set XamlRoot = Content.XamlRoot before ShowAsync() in WinUI 3 desktop apps.",
            "Save/discard/cancel flows: provide all three of PrimaryButtonText, SecondaryButtonText, CloseButtonText."
        ],
        ["Flyout"] = [
            "Flyout auto-dismisses on outside click; ShowMode='Standard' for explicit dismiss.",
            "Attach via FlyoutBase.AttachedFlyout or Button.Flyout."
        ],
        ["MenuFlyout"] = [
            "Items: MenuFlyoutItem / ToggleMenuFlyoutItem / MenuFlyoutSubItem."
        ],
        ["TeachingTip"] = [
            "Without a Target, TeachingTip shows as a banner instead of near a control.",
            "TeachingTip.Target is FrameworkElement, NOT UIElement. x:Bind from UIElement-typed prop → WMC1121. Bind to x:Name'd element or expose as FrameworkElement (Image/TextBlock/Button work)."
        ],
        ["InfoBar"] = [
            "InfoBar auto-closes on user-click — bind IsOpen TwoWay."
        ],

        // ─── Navigation ───
        ["NavigationView"] = [
            "MenuItems for static nav; MenuItemsSource for data-bound.",
            "Handle SelectionChanged, NOT ItemInvoked, for reliable navigation."
        ],
        ["SplitView"] = [
            "DisplayMode='Inline'=always-visible pane; 'CompactInline'=icon strip; 'Overlay'=hamburger flyout."
        ],
        ["BreadcrumbBar"] = [
            "BreadcrumbBar has no Items property — only ItemsSource."
        ],

        // ─── Numeric ───
        ["NumberBox"] = [
            "NumberBox.Value is double — Math.Round for integer scenarios.",
            "SpinButtonPlacementMode='Inline' shows +/- buttons.",
            "ValidationMode='InvalidInputOverwritten' auto-corrects invalid input."
        ],

        // ─── Text input ───
        ["AutoSuggestBox"] = [
            "Filter suggestions in TextChanged when reason==UserInput, NOT in SuggestionChosen."
        ],
        ["RichEditBox"] = [
            "Content access: Document.GetText/SetText with TextGetOptions/TextSetOptions."
        ],
        ["RichSuggestBox"] = [
            "Prefixes property defines trigger chars (e.g. '@#'); inline tokens tracked in Tokens collection."
        ],
        ["ComboBox"] = [
            "Bind SelectedItem/SelectedIndex, NOT SelectedValue (unless SelectedValuePath is also set)."
        ],

        // ─── Settings ───
        ["SettingsCard"] = [
            "Don't build settings UI with plain StackPanel+ToggleSwitch — use SettingsCard (Win11 standard).",
            "IsClickEnabled=True turns the card into a button (e.g. for nav to detail page)."
        ],
        ["SettingsExpander"] = [
            "Items collection holds child SettingsCards under one collapsible header."
        ],

        // ─── Sizers ───
        ["GridSplitter"] = [
            "Place in an Auto-width column between two content columns.",
            "ResizeBehavior='BasedOnAlignment' for between-columns; ResizeDirection='Auto' infers."
        ],

        // ─── Layout ───
        ["DockPanel"] = [
            "LastChildFill=True makes last child fill remaining space.",
            "Dock attached property on children: Top/Bottom/Left/Right."
        ],
        ["WrapPanel"] = [
            "For virtualized wrap: use WrapLayout (Toolkit) inside ItemsRepeater."
        ],
        ["UniformGrid"] = [
            "All cells equal-sized; specify Rows OR Columns, the other auto-calculates."
        ],
        ["StaggeredPanel"] = [
            "For virtualized masonry: StaggeredLayout (Toolkit) inside ItemsRepeater."
        ],

        // ─── Toolbars ───
        ["CommandBar"] = [
            "PrimaryCommands=always-visible; SecondaryCommands=overflow menu."
        ],

        // ─── Other commonly-confused controls ───
        ["Border"] = [
            "Border has no .Cursor in WinUI 3 (WPF holdover). Override ProtectedCursor on a custom UserControl hosting the Border, or set ProtectedCursor on nearest parent FrameworkElement deriving from Control.",
            "Clickable border: wrap with Button Style=\"{StaticResource TransparentButtonStyle}\" — don't catch pointer events on Border directly."
        ],
        ["WebView2"] = [
            "Always call EnsureCoreWebView2Async() before using CoreWebView2 properties.",
            "NavigateToString(html) loads inline HTML without a URL."
        ],
        ["Expander"] = [
            "Expander.IsExpanded TwoWay binding needs explicit x:Bind Mode=TwoWay (default doesn't work)."
        ],
        ["MenuBar"] = [
            "Place MenuBar in title bar or at top of window for standard layout."
        ],
        ["AdvancedCollectionView"] = [
            "Wrap ObservableCollection with AdvancedCollectionView for sorting/filtering/grouping without modifying source.",
            "Call RefreshFilter() after changing the Filter predicate."
        ],
    };

    /// <summary>Result payload combining specific pitfalls with the optional family guide.</summary>
    public readonly record struct NotesPayload(string[] Pitfalls, string? FamilyName, string? FamilyGuide);

    public static NotesPayload Get(string controlName)
    {
        var pitfalls = KnownPitfalls.TryGetValue(controlName, out var p) ? p : Array.Empty<string>();
        string? famName = null, famGuide = null;
        if (ControlToFamily.TryGetValue(controlName, out var fk) && FamilyGuide.TryGetValue(fk, out var guide))
        {
            famName = fk;
            famGuide = guide;
        }
        return new NotesPayload(pitfalls, famName, famGuide);
    }

    /// <summary>Backward-compat: just the pitfalls (no family).</summary>
    public static string[] GetNotes(string controlName) => Get(controlName).Pitfalls;
}
