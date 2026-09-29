// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsWindow.cpp; included into that translation unit, not compiled on its own.

struct PaneEditTarget
{
    const DevToolsCardRow* row = nullptr;
    std::wstring path;
    size_t owner = 0;
};

// The emitter and the wiring loop are a two-sided contract with no compiler behind it: one decides which rows
// get an E<k> and what k is, the other decides which k to look for. They now derive that from ONE function, so
// adding a row shape cannot renumber one side and not the other.
struct PaneEditMap
{
    std::vector<PaneEditTarget> targets;
    std::vector<int>            rowIdx;
    std::vector<std::vector<int>> childIdx;
};

static PaneEditMap BuildEditMap(const std::vector<DevToolsCardRow>& rows)
{
    PaneEditMap map;
    map.rowIdx.assign(rows.size(), -1);
    map.childIdx.assign(rows.size(), {});
    // ORIGINAL row order, parent immediately followed by its own children. Visual regrouping and filtering
    // happen later and must not renumber anything: E<k> is looked up by name after the fragment loads, so a
    // number that moved would wire the wrong property's editor to the right property's sink.
    for (size_t i = 0; i < rows.size(); ++i) {
        if (!rows[i].type.empty()) {
            map.rowIdx[i] = (int)map.targets.size();
            map.targets.push_back({ &rows[i], rows[i].name, i });
        }
        map.childIdx[i].assign(rows[i].children.size(), -1);
        for (size_t c = 0; c < rows[i].children.size(); ++c) {
            if (rows[i].children[c].type.empty()) continue;
            map.childIdx[i][c] = (int)map.targets.size();
            map.targets.push_back({ &rows[i].children[c], rows[i].name + L"." + rows[i].children[c].name, i });
        }
    }
    return map;
}

static bool RowValueIsMissing(const DevToolsCardRow& r)
{
    return r.valueState == L"unset" || r.valueState == L"null" || r.valueState == L"unresolved";
}

static std::wstring RowAuthoredLiteral(const DevToolsCardRow& r)
{
    if (r.authoredKind != L"literal" || r.authored.empty()) return std::wstring();
    return r.authored;
}
// The edit seed is also the commit comparison value; mismatch causes never-commit or always-commit rows.
static std::wstring RowEditSeed(const DevToolsCardRow& r)
{
    if (RowValueIsMissing(r)) return std::wstring();
    if (r.value.empty() && ShortType(r.type) != L"String") return RowAuthoredLiteral(r);
    return r.value;
}

static std::wstring RowEditPlaceholder(const DevToolsCardRow& r)
{
    if (r.valueState == L"unset")      return L"not set - type a value";
    if (r.valueState == L"null")       return L"null - type a value";
    if (r.valueState == L"unresolved") return ShortType(r.value.empty() ? r.type : r.value) + L" - type a value to replace it";
    return std::wstring();
}

static bool RowHasOriginLine(const DevToolsCardRow& r, const std::wstring& path)
{
    if (BindingLost(path) || LinkLost(path)) return true;
    if (r.authoredKind == L"literal") return false;
    if (!r.authored.empty() || !r.binding.empty()) return true;
    return r.source == L"Binding";
}

static const wchar_t* OriginChip(const DevToolsCardRow& r);

static const wchar_t* OriginChip(const DevToolsCardRow& r)
{
    if (r.authoredKind == L"themeResource")   return L"THEME";
    if (r.authoredKind == L"staticResource")  return L"RES";
    if (r.authoredKind == L"xBind")           return L"X:BIND";
    if (r.authoredKind == L"binding")         return L"BIND";
    if (r.authoredKind == L"templateBinding") return L"TMPL";
    if (r.authoredKind == L"customMarkup")    return L"MARKUP";
    // No authoredKind but an expression: the binding was reconstructed from the LIVE element rather than read
    // from source, which only ever happens for a runtime {Binding}.
    return L"BIND";
}

// Each property is its own named row so filtering, virtualization, and per-row actions can target one element.
static std::wstring BuildPropsFragment(const std::wstring& type, const std::wstring& name,
                                       const std::wstring& label, const std::wstring& breadcrumb,
                                       const std::wstring& sourceUri, unsigned int sourceLine,
                                       const std::vector<DevToolsCardRow>& rows,
                                       const std::wstring& authoredState,
                                       const std::wstring& authoredXaml,
                                       const DevToolsWindowLayout* layout)
{
    std::wstring badge = type.empty() ? label : ShortType(type);
    if (!name.empty()) badge += L"  \u00B7  #" + name;
    std::wstring m;
    m += L"<StackPanel xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\""
         L" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" Spacing=\"10\""
         L" AutomationProperties.AutomationId=\"WinAppDevToolsProps\">";
    m += L"<TextBlock Text=\"PROPERTIES\" Style=\"{StaticResource CaptionTextBlockStyle}\""
         L" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\" CharacterSpacing=\"60\"/>";
    m += L"<TextBlock Text=\"" + DevToolsXmlEscape(badge) + L"\" Style=\"{StaticResource BodyStrongTextBlockStyle}\""
         L" Foreground=\"{ThemeResource TextFillColorPrimaryBrush}\" TextTrimming=\"CharacterEllipsis\"/>";
    // Ancestor breadcrumb (root -> parent). Collapsed to one line by default: a real WinUI chain is 20+
    // levels, and rendering it expanded pushed the actual properties off the bottom of the pane. NOTE the
    // explicit TextWrapping="NoWrap" -- CaptionTextBlockStyle turns wrapping ON, and with wrapping enabled
    // TextTrimming only applies to the LAST line, which is precisely how a "trimmed" breadcrumb still managed
    // to wrap to seven lines. The full chain stays available: tooltip, plus an expander when it was elided.
    if (!breadcrumb.empty()) {
        size_t segs = 0;
        const std::wstring shortBc = ElideBreadcrumb(breadcrumb, segs);
        m += L"<TextBlock x:Name=\"BcShort\" Text=\"" + DevToolsXmlEscape(shortBc) + L"\" Style=\"{StaticResource CaptionTextBlockStyle}\""
             L" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\" TextWrapping=\"NoWrap\""
             L" TextTrimming=\"CharacterEllipsis\" Margin=\"0,-6,0,0\""
             L" ToolTipService.ToolTip=\"" + DevToolsXmlEscape(breadcrumb) + L"\""
             L" AutomationProperties.AutomationId=\"WinAppDevToolsBreadcrumb\"/>";
        if (shortBc != breadcrumb) {
            m += L"<TextBlock x:Name=\"BcFull\" Text=\"" + DevToolsXmlEscape(breadcrumb) + L"\" Style=\"{StaticResource CaptionTextBlockStyle}\""
                 L" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\" TextWrapping=\"Wrap\""
                 L" Visibility=\"Collapsed\" Margin=\"0,-6,0,0\""
                 L" AutomationProperties.AutomationId=\"WinAppDevToolsBreadcrumbFull\"/>";
            g_bcExpandText = L"Show all " + std::to_wstring(segs) + L" ancestors";
            const std::wstring toggleLabel = g_bcExpanded ? std::wstring(L"Show less") : g_bcExpandText;
            m += L"<HyperlinkButton x:Name=\"BcToggle\" Padding=\"0\" Margin=\"0,-8,0,0\" FontSize=\"12\""
                 L" AutomationProperties.Name=\"Toggle full ancestor path\""
                 L" AutomationProperties.AutomationId=\"WinAppDevToolsBreadcrumbToggle\">";
            m += L"<TextBlock x:Name=\"BcToggleLbl\" FontSize=\"12\" Text=\"" + DevToolsXmlEscape(toggleLabel) +
                 L"\" AutomationProperties.AutomationId=\"WinAppDevToolsBreadcrumbToggleLabel\""
                 L" AutomationProperties.AccessibilityView=\"Raw\"/>";
            m += L"</HyperlinkButton>";
        }
    }
    // Show a source link only with actual XBF/source instrumentation and a file resolved under
    // the configured source root. Environment delivery is the host's responsibility.
    {
        std::wstring disp = SourceDisplay(sourceUri, sourceLine);
        if (!disp.empty()) {
            const bool canOpen = !ResolveSourcePath(sourceUri).empty();
            m += L"<StackPanel Orientation=\"Horizontal\" Spacing=\"6\" Margin=\"0,2,0,0\">";
            m += L"<FontIcon Glyph=\"&#xE8A5;\" FontSize=\"12\" VerticalAlignment=\"Center\""
                 L" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\"/>";
            m += L"<TextBlock Text=\"" + DevToolsXmlEscape(disp) + L"\" Style=\"{StaticResource CaptionTextBlockStyle}\""
                 L" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\" VerticalAlignment=\"Center\""
                 L" TextTrimming=\"CharacterEllipsis\" AutomationProperties.AutomationId=\"WinAppDevToolsSource\"/>";
            if (canOpen) {
                m += L"<HyperlinkButton x:Name=\"OpenSource\" Content=\"Open\" Padding=\"4,0,4,0\" FontSize=\"12\""
                     L" VerticalAlignment=\"Center\" ToolTipService.ToolTip=\"Open the source file in your default editor\"/>";
            }
            m += L"</StackPanel>";
        }
    }
    if (!authoredXaml.empty()) {
        m += L"<Border Background=\"{ThemeResource ControlFillColorSecondaryBrush}\" CornerRadius=\"4\""
             L" Padding=\"8,6,8,6\" Margin=\"0,8,0,0\">";
        m += L"<StackPanel Spacing=\"0\">";
        m += L"<TextBlock Text=\"" + std::wstring(authoredState == L"likely"
             ? L"Likely source: unique line and identity match; compiled identity and line preservation are unverified."
             : L"Source on disk; the running app may differ after a rebuild or hot reload.") + L"\""
             L" Style=\"{StaticResource CaptionTextBlockStyle}\" TextWrapping=\"Wrap\""
             L" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\"/>";
        m += L"<HyperlinkButton x:Name=\"AxmlToggle\" Padding=\"0\" HorizontalAlignment=\"Stretch\""
             L" HorizontalContentAlignment=\"Left\" AutomationProperties.Name=\"Toggle authored XAML\""
             L" AutomationProperties.AutomationId=\"WinAppDevToolsAuthoredXamlToggle\">";
        m += L"<Grid ColumnSpacing=\"7\"><Grid.ColumnDefinitions><ColumnDefinition Width=\"10\"/>"
             L"<ColumnDefinition Width=\"*\"/></Grid.ColumnDefinitions>";
        m += L"<TextBlock x:Name=\"AxmlChev\" Grid.Column=\"0\" FontFamily=\"Segoe Fluent Icons,Segoe MDL2 Assets\""
             L" FontSize=\"8\" VerticalAlignment=\"Center\""
             L" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\" Text=\"\uE76C\""
             L" AutomationProperties.AccessibilityView=\"Raw\"/>";
        m += L"<TextBlock x:Name=\"AxmlOne\" Grid.Column=\"1\" Text=\"" +
             DevToolsXmlEscape(AuthoredXamlOneLine(authoredXaml)) +
             L"\" FontFamily=\"Cascadia Mono, Consolas\" FontSize=\"11\" TextWrapping=\"NoWrap\""
             L" TextTrimming=\"CharacterEllipsis\" VerticalAlignment=\"Center\""
             L" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\""
             L" AutomationProperties.AutomationId=\"WinAppDevToolsAuthoredXamlOneLine\"/>";
        m += L"</Grid></HyperlinkButton>";
        m += L"<TextBlock x:Name=\"AxmlFull\" Text=\"" + DevToolsXmlEscape(authoredXaml, true) + L"\""
             L" FontFamily=\"Cascadia Mono, Consolas\" Visibility=\"Collapsed\" Margin=\"17,4,0,0\""
             L" FontSize=\"11\" TextWrapping=\"Wrap\" IsTextSelectionEnabled=\"True\""
             L" Foreground=\"{ThemeResource TextFillColorPrimaryBrush}\""
             L" AutomationProperties.AutomationId=\"WinAppDevToolsAuthoredXaml\"/>";
        m += L"</StackPanel></Border>";
    }
    if (layout && (layout->haveDesired || layout->haveRender || layout->haveOffset)) {
        m += BuildLayoutSection(*layout, rows);
    }
    if (rows.empty()) {
        m += L"<TextBlock Text=\"(no curated properties)\" Foreground=\"{ThemeResource TextFillColorTertiaryBrush}\"/>";
        m += L"</StackPanel>";
        return m;
    }

    const PaneEditMap emap = BuildEditMap(rows);
    const bool anyEditable = !emap.targets.empty();

    if (authoredState != L"available") {
        // Framework source with no readable app declaration is not missing app instrumentation.
            // Do not recommend changing a correct build for that case.
        const bool frameworkDeclared = !sourceUri.empty() && !DevToolsAppXaml_IsAppAuthored(sourceUri);
        std::wstring why;
        if (frameworkDeclared) {
            why = SourceFileName(sourceUri) + L" is framework XAML, not your app's, so there is no authored "
                  L"source to read for this element. Authored values ({x:Bind}, {ThemeResource}) are shown "
                  L"for elements your app declares.";
        } else if (authoredState == L"noSourceInfo" && g_jmxSrcSeen > 0) {
            // Instrumentation demonstrably works in this process -- g_jmxSrcSeen counts nodes that DID report
            // a source URI -- so the build and the launch are not the cause and must not be named as one.
            why = L"The runtime reported no XAML source location for this element, so its authored values "
                  L"can't be shown. Other elements in this app do report one, so the build and the launch are "
                  L"fine. An element created from a template or in code simply has no authored line.";
        } else if (authoredState == L"noSourceInfo") {
            why = L"XAML source info isn't available in this process, so authored values "
                  L"({x:Bind}, {ThemeResource}) can't be shown. Either the build carries no XBF line info "
                  L"(Debug bakes it in; a Release build needs -p:DisableXbfLineInfo=false), or this launch "
                  L"couldn't deliver ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO. winapp warns when that is the reason.";
        } else if (authoredState == L"noFile") {
            why = L"The app's XAML source file couldn't be opened, so authored values "
                  L"({x:Bind}, {ThemeResource}) can't be shown.";
        } else if (authoredState == L"stale") {
            why = L"The source on disk changed after the captured build or source snapshot. "
                  L"Authored values are hidden because the running app's source identity is no longer verified.";
        } else if (authoredState == L"unavailable") {
            why = L"The source coordinates and element identity do not identify one XAML declaration. "
                  L"Authored values are hidden rather than attributed to a different element.";
        } else if (authoredState == L"unverifiedBuild") {
            why = L"The compiler output, original source and deployed XAML could not be verified together. "
                  L"Check the original XAML before editing; rebuilding alone does not verify the column mapping.";
        } else if (authoredState == L"likely") {
            why = L"The likely declaration is shown for review, but its authored values are not treated as verified runtime properties.";
        } else {
            why = L"Authored values ({x:Bind}, {ThemeResource}) can't be shown for this element.";
        }
        m += L"<Border Background=\"{ThemeResource ControlFillColorSecondaryBrush}\" CornerRadius=\"4\""
             L" Padding=\"8,6,8,6\" Margin=\"0,0,0,6\">";
        m += L"<TextBlock Text=\"" + DevToolsXmlEscape(why) + L"\" FontSize=\"11\" TextWrapping=\"Wrap\""
             L" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\""
             L" AutomationProperties.Name=\"Authored values unavailable\"/>";
        m += L"</Border>";
    }

    m += L"<Grid ColumnSpacing=\"8\" Margin=\"0,2,0,0\"><Grid.ColumnDefinitions>"
         L"<ColumnDefinition Width=\"*\"/><ColumnDefinition Width=\"Auto\"/>"
         L"</Grid.ColumnDefinitions>";
    m += L"<TextBox x:Name=\"PropFilter\" Grid.Column=\"0\" MinHeight=\"32\" FontSize=\"12\""
         L" PlaceholderText=\"Filter properties\u2026\" Text=\"" + DevToolsXmlEscape(g_propFilterQuery) + L"\""
         L" ToolTipService.ToolTip=\"Show only properties whose name, value or authored text contains this text\""
         L" AutomationProperties.AutomationId=\"WinAppDevToolsPropFilter\""
         L" AutomationProperties.Name=\"Filter properties\"/>";
    m += L"<TextBlock x:Name=\"PropCount\" Grid.Column=\"1\" VerticalAlignment=\"Center\" FontSize=\"11\""
         L" Foreground=\"{ThemeResource TextFillColorTertiaryBrush}\""
         L" AutomationProperties.AutomationId=\"WinAppDevToolsPropCount\" Text=\"" +
         DevToolsXmlEscape(std::to_wstring(rows.size()) + L" properties") + L"\"/>";    m += L"</Grid>";

    m += L"<StackPanel Orientation=\"Horizontal\" Spacing=\"0\" Margin=\"0,2,0,0\" HorizontalAlignment=\"Left\""
         L" AutomationProperties.AutomationId=\"WinAppDevToolsPropView\">";
    m += L"<ToggleButton x:Name=\"ViewComputed\" Padding=\"10,3,10,3\" MinWidth=\"0\" CornerRadius=\"4,0,0,4\""
         L" AutomationProperties.AutomationId=\"WinAppDevToolsViewComputed\" AutomationProperties.Name=\"Computed view\""
         L" IsChecked=\""; m += (ShowingSetView() ? L"False" : L"True");
    m += L"\" ToolTipService.ToolTip=\"Every property, with one resolved value each: what this element's state IS\">"
         L"<TextBlock Text=\"Computed\" FontSize=\"12\"/></ToggleButton>";
    m += L"<ToggleButton x:Name=\"OnlySetToggle\" Padding=\"10,3,10,3\" MinWidth=\"0\" CornerRadius=\"0,4,4,0\""
         L" AutomationProperties.AutomationId=\"WinAppDevToolsViewSet\" AutomationProperties.Name=\"Set view\""
         L" IsChecked=\""; m += (ShowingSetView() ? L"True" : L"False");
    m += L"\" ToolTipService.ToolTip=\"Only what was written on this element, grouped by where it came from"
         L": what you actually SET\">"
         L"<TextBlock Text=\"Set\" FontSize=\"12\"/></ToggleButton>";
    m += L"</StackPanel>";

    if (ShowingSetView()) {
        size_t authored = 0, styled = 0;
        for (const auto& r : rows) {
            if (!r.authoredKind.empty() || SourceIsSetHere(r.source)) ++authored;
            else if (r.source == L"Style" || r.source == L"Built-in style") ++styled;
        }
        std::wstring line = std::to_wstring(authored) + L" of " + std::to_wstring(rows.size()) +
                            L" values " + (authored == 1 ? L"was" : L"were") + L" written on this element.";
        if (styled) line += L" " + std::to_wstring(styled) + L" came from a style; the rest are type defaults.";
        else        line += L" The rest are type defaults.";
        line += L" A defaulted value is still a value. It is the one nobody remembers choosing.";
        m += L"<Border Margin=\"0,6,0,0\" Padding=\"8,6,8,6\" CornerRadius=\"4\""
             L" Background=\"{ThemeResource CardBackgroundFillColorSecondaryBrush}\">";
        m += L"<TextBlock Text=\"" + DevToolsXmlEscape(line) + L"\" FontSize=\"11\" TextWrapping=\"Wrap\""
             L" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\"/></Border>";
    }

    m += L"<ItemsRepeater x:Name=\"PropRepeater\" TabFocusNavigation=\"Local\">"
         L"<ItemsRepeater.Layout><StackLayout/></ItemsRepeater.Layout>"
         L"</ItemsRepeater>";

    m += L"<Border x:Name=\"PropNoMatch\" Visibility=\"Collapsed\" Margin=\"0,10,0,0\" Padding=\"10,8,10,9\""
         L" CornerRadius=\"4\" Background=\"{ThemeResource ControlFillColorSecondaryBrush}\""
         L" BorderThickness=\"1\" BorderBrush=\"{ThemeResource ControlStrokeColorDefaultBrush}\""
         L" AutomationProperties.AutomationId=\"WinAppDevToolsPropNoMatch\">";
    m += L"<StackPanel Spacing=\"3\">";
    m += L"<TextBlock x:Name=\"PropNoMatchLbl\" FontSize=\"12\" TextWrapping=\"Wrap\" Text=\"\""
         L" AutomationProperties.AutomationId=\"WinAppDevToolsPropNoMatchLabel\"/>";
    m += L"<TextBlock FontSize=\"11\" TextWrapping=\"Wrap\""
         L" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\""
         L" Text=\"The filter matches a property name, its value, and its authored text.\"/>";
    m += L"<HyperlinkButton x:Name=\"PropClearFilter\" Padding=\"0\" Margin=\"0,2,0,0\" FontSize=\"12\""
         L" Visibility=\"Collapsed\""
         L" AutomationProperties.AutomationId=\"WinAppDevToolsPropClearFilter\""
         L" AutomationProperties.Name=\"Clear the property filter\">"
         L"<TextBlock Text=\"Clear the filter\" FontSize=\"12\""
         L" AutomationProperties.AccessibilityView=\"Raw\"/>"
         L"</HyperlinkButton>";
    m += L"</StackPanel></Border>";

    if (!ShowingSetView()) {
        m += L"<HyperlinkButton x:Name=\"PropShowAll\" Padding=\"0\" Margin=\"0,4,0,0\" FontSize=\"12\""
             L" AutomationProperties.AutomationId=\"WinAppDevToolsPropShowAll\""
             L" AutomationProperties.Name=\"Toggle the full property list\">";
        m += L"<TextBlock x:Name=\"PropShowAllLbl\" FontSize=\"12\" Text=\"" +
             DevToolsXmlEscape(PropShowAllLabel(rows)) + L"\""
             L" AutomationProperties.AutomationId=\"WinAppDevToolsPropShowAllLabel\""
             L" AutomationProperties.AccessibilityView=\"Raw\"/>";
        m += L"</HyperlinkButton>";
    }

    if (anyEditable) {
        m += L"<TextBlock x:Name=\"Status\" Text=\"Edits apply when you leave a field\""
             L" Style=\"{StaticResource CaptionTextBlockStyle}\" Foreground=\"{ThemeResource TextFillColorTertiaryBrush}\""
             L" AutomationProperties.Name=\"Property pane status\""
             L" AutomationProperties.AutomationId=\"WinAppDevToolsPropStatus\""
             L" Margin=\"0,4,0,0\" TextWrapping=\"Wrap\"/>";
    }
    m += L"</StackPanel>";
    return m;
}


static void ShowPaneNotice(const wchar_t* text)
{
    if (!g_ctx.propsHostChildren) return;
    // No x:Name here: XamlReader.Load rejects a name on the ROOT of a fragment (there is no namescope owner
    // to register it in), and the load fails wholesale. The error-line fragment below is unnamed for the same
    // reason. The text itself is the assertable surface.
    std::wstring m = L"<TextBlock xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\""
                     L" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\" FontSize=\"12\""
                     L" Margin=\"12,12,12,12\" TextWrapping=\"Wrap\" Text=\"";
    m += DevToolsXmlEscape(text);
    m += L"\"/>";
    IInspectable* frag = nullptr;
    if (FAILED(LoadMarkupWin(m, &frag)) || !frag) {
        // Say so. A notice that fails to load is the one failure mode this function must not hide: the caller
        // has already cleared the pane, so a silent failure leaves a blank panel and no explanation.
        DevToolsOverlayLog(L"route1.props notice FAILED to load");
        return;
    }
    {
        void* ui = nullptr;
        if (SUCCEEDED(frag->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) && ui) {
            DevToolsVecAppend(g_ctx.propsHostChildren, reinterpret_cast<IInspectable*>(ui));
            reinterpret_cast<IUnknown*>(ui)->Release();
        }
        frag->Release();
    }
}

// Debounce for the live tree refresh. DevToolsTreeWatch already coalesces a burst of mutations into one
// delivery, but a navigation is several bursts across several dispatcher turns, and a row rebuild is far more
// expensive than a notification. Re-arming the SAME timer id resets its elapsed time, so N notifications
// arriving inside the window produce exactly one rebuild, once the app has settled.
static const UINT_PTR kRefreshTimerId  = 0xD72; // unique on the inspector HWND (see kPlaceTimerId / kWatchTimerId)
static const UINT     kRefreshDebounceMs = 250;
static HWND Route1WindowHwnd();

static VOID CALLBACK RefreshTimerProc(HWND hwnd, UINT, UINT_PTR id, DWORD)
{
    KillTimer(hwnd, id);
    RefreshTreeFromApp();
}

static void ArmTreeRefresh()
{
    // Nothing rebuilds rows while the window is hidden. A rebuild allocates per-row sinks that are
    // deliberately immortal (DevToolsSinkBase never frees), so letting a hidden window track every navigation the
    // user makes with the inspector closed would grow the app for the life of the process -- the exact class
    // of leak is about. The show path re-reads the tree before the window comes back on screen, so the
    // work is deferred rather than dropped.
    if (g_route1WindowHidden) {
        DevToolsOverlayLog(L"route1.refresh NOT armed -- window is hidden; the tree is re-read on show");
        return;
    }
    HWND h = Route1WindowHwnd();
    if (!h) {
        DevToolsOverlayLog(L"route1.refresh NOT armed -- no inspector HWND; the tree will stay out of date");
        return;
    }
    // SetTimer returns 0 on failure. A refresh that never got scheduled must say so rather than leave the
    // window sitting on a stale tree looking like it is about to update.
    if (!SetTimer(h, kRefreshTimerId, kRefreshDebounceMs, RefreshTimerProc))
        DevToolsOverlayLog(L"route1.refresh SetTimer FAILED (err=%lu) -- the tree will stay out of date", GetLastError());
}

static const UINT_PTR kDisarmAfterWindowPickTimerId = 0xD73; // unique on the inspector HWND (see kRefreshTimerId etc.)

static VOID CALLBACK DisarmAfterWindowPickTimerProc(HWND hwnd, UINT, UINT_PTR id, DWORD)
{
    KillTimer(hwnd, id);
    DevToolsOverlay_CompleteOneShotPick();
}

static void ArmDisarmAfterWindowPick()
{
    HWND h = Route1WindowHwnd();
    if (!h) {
        // Should not happen while DevToolsWindow_IsOpen() (the only caller checks that first), but skip rather
        // than fall back to a direct call here -- a direct call is exactly the reentrant shape this exists
        // to avoid, and a rare miss just leaves the catcher armed for a future explicit disarm/Esc/toggle.
        DevToolsOverlayLog(L"route1.pick disarm NOT armed -- no inspector HWND; shared pick catcher stays armed");
        return;
    }
    if (!SetTimer(h, kDisarmAfterWindowPickTimerId, 1, DisarmAfterWindowPickTimerProc))
        DevToolsOverlayLog(L"route1.pick disarm SetTimer FAILED (err=%lu) -- shared pick catcher stays armed", GetLastError());
}

static void RecomputeCommentLiveness();

static void OnAppTreeChanged()
{
    // `not on this screen` is a fact about the TREE, and this is the only notification that the tree
    // moved. Recomputing here rather than in the comment-set push is the whole fix -- the push fires when the
    // SET changes, which a navigation does not do. It writes properties on rows that already exist; nothing
    // here re-parses markup, which is what keeps a navigation off the resource-resolution curve.
    RecomputeCommentLiveness();
    // Schedule the row rebuild. Marking the header stale below stays: it is what the user sees in the gap
    // before the debounce fires, and what they keep seeing if the rebuild fails. The latch is cleared by
    // RefreshTreeFromApp on SUCCESS only -- closing it on the attempt would claim a current tree from
    // a rebuild that rolled back.
    ArmTreeRefresh();
    if (!g_treeStale && g_ctx.censusCountFn && g_treeHeaderTb) {
        const size_t now = g_ctx.censusCountFn();
        if (now != g_snapshotTotal) {
            g_treeStale = true;
            wchar_t hdr[200];
            _snwprintf_s(hdr, _countof(hdr), _TRUNCATE,
                         L"VISUAL TREE \u00B7 %zu NODES (OUT OF DATE: app now has %zu, refreshing\u2026)",
                         g_snapshotTotal, now);
            HSTRING hs = MakeStr(hdr);
            DevToolsPutText(g_treeHeaderTb, hs);
            WindowsDeleteString(hs);
            DevToolsOverlayLog(L"route1.treechange snapshot %zu != live %zu -> tree marked out of date",
                          g_snapshotTotal, now);
        }
    }
    if (!g_selWire || !g_ctx.wireLiveFn) {
        DevToolsOverlayLog(L"route1.treechange ignored (selWire=%llu liveFn=%d)",
                      (unsigned long long)g_selWire, g_ctx.wireLiveFn ? 1 : 0);
        return;
    }
    const bool live = g_ctx.wireLiveFn(g_selWire);
    DevToolsOverlayLog(L"route1.treechange selWire=%llu live=%d", (unsigned long long)g_selWire, live ? 1 : 0);
    // Ask whether the handle still resolves. Asking is the ONLY way to know: a handle that resolved a moment
    // ago says nothing about now, which is the whole reason this notification exists.
    if (live) return;
    DevToolsOverlayLog(L"route1.treechange selected wire %llu is gone -> dropping the selection",
                  (unsigned long long)g_selWire);
    g_selWire = 0;
    g_selectedWire = 0;
    SelectRowVisual(nullptr);
    ShowPropsForNode(0, L"(no selection)", L"", true);
    ShowPaneNotice(L"The selected element is gone. The app navigated or removed it.");
}



enum class DevToolsPaneItemKind { GroupHeader, Row };
struct DevToolsPaneItem
{
    DevToolsPaneItemKind kind = DevToolsPaneItemKind::Row;
    size_t rowIndex = 0;
    int    group    = 0;
    std::wstring caption;
};
static std::vector<DevToolsCardRow> g_propRows;
static std::vector<DevToolsPaneItem> g_paneItems;
static std::vector<std::wstring> g_propRowLabels;
static PaneEditMap g_paneEditMap;
static void* g_propRepeater = nullptr;          // the ItemsRepeater in the current pane (AddRef'd)

static IInspectable* g_tmplHeader  = nullptr;
static IInspectable* g_tmplRow     = nullptr;
static IInspectable* g_tmplExpRead = nullptr;
static IInspectable* g_tmplExpEdit = nullptr;

static std::vector<std::wstring> g_propExpanded;
static bool PropIsExpanded(const std::wstring& name)
{
    return std::find(g_propExpanded.begin(), g_propExpanded.end(), name) != g_propExpanded.end();
}
static void PropSetExpanded(const std::wstring& name, bool on)
{
    auto it = std::find(g_propExpanded.begin(), g_propExpanded.end(), name);
    if (on && it == g_propExpanded.end())      g_propExpanded.push_back(name);
    else if (!on && it != g_propExpanded.end()) g_propExpanded.erase(it);
}

static void CapturePropRowTemplates(void* feRoot)
{
    struct T { const wchar_t* name; IInspectable** slot; };
    const T all[] = {
        { L"TmplHeader", &g_tmplHeader }, { L"TmplRow", &g_tmplRow }, { L"TmplExpRead", &g_tmplExpRead },
        { L"TmplExpEdit", &g_tmplExpEdit },
    };
    for (const T& t : all) {
        HSTRING nm = MakeStr(t.name);
        IInspectable* host = nullptr;
        DevToolsFindName(feRoot, nm, &host);
        WindowsDeleteString(nm);
        if (!host) { DevToolsOverlayLog(L"route1.props template MISS %s -- rows of that shape cannot render", t.name); continue; }
        void* cc = nullptr;
        if (SUCCEEDED(host->QueryInterface(DevToolsIid<DevToolsXC::IContentControl>(), &cc)) && cc) {
            DevToolsGetContentTemplate(cc, t.slot);
            reinterpret_cast<IUnknown*>(cc)->Release();
        }
        host->Release();
        if (!*t.slot) DevToolsOverlayLog(L"route1.props template %s has no ContentTemplate", t.name);
    }
    int have = 0;
    for (const T& t : all) if (*t.slot) ++have;
    DevToolsOverlayLog(L"route1.props row templates resolved=%d/%d", have, (int)(sizeof(all) / sizeof(all[0])));
}

static IInspectable* ExpansionTemplateForRow(const DevToolsCardRow& r, int edit)
{
    if (edit < 0 || r.type.empty()) return g_tmplExpRead;
    return g_tmplExpEdit;
}

static IInspectable* FindInRow(void* rowFe, const wchar_t* name)
{
    if (!rowFe) return nullptr;
    HSTRING h = MakeStr(name);
    IInspectable* found = nullptr;
    DevToolsFindName(rowFe, h, &found);
    WindowsDeleteString(h);
    return found;
}
static void SetTextOn(IInspectable* el, const std::wstring& text)
{
    if (!el) return;
    void* tb = nullptr;
    if (SUCCEEDED(el->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &tb)) && tb) {
        HSTRING h = MakeStr(text); DevToolsPutText(tb, h); WindowsDeleteString(h);
        reinterpret_cast<IUnknown*>(tb)->Release();
    }
}
static void ShowEl(IInspectable* el, bool visible)
{
    if (!el) return;
    void* ui = nullptr;
    if (SUCCEEDED(el->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) && ui) {
        DevToolsPutVisibility(ui, visible ? 0 : 1);
        reinterpret_cast<IUnknown*>(ui)->Release();
    }
}

static DevToolsCardRow* FindPropertyRow(std::vector<DevToolsCardRow>& rows, const std::wstring& path)
{
    for (auto& row : rows) if (row.name == path) return &row;
    for (auto& row : rows) {
        if (path.compare(0, row.name.size() + 1, row.name + L".") == 0)
            return FindPropertyRow(row.children, path.substr(row.name.size() + 1));
    }
    return nullptr;
}

static void FillCombo(IInspectable* combo, const std::vector<std::wstring>& options)
{
    if (!combo || options.empty()) return;
    void* ic = nullptr;
    if (FAILED(combo->QueryInterface(DevToolsIid<DevToolsXC::IItemsControl>(), &ic)) || !ic) return;
    IInspectable* raw = nullptr;
    void* items = nullptr;
    if (SUCCEEDED(DevToolsAbi<DevToolsXC::IItemsControl>(ic)->get_Items(reinterpret_cast<void**>(&raw))) && raw) {
        raw->QueryInterface(
            DevToolsIid<winrt::Windows::Foundation::Collections::IVector<winrt::Windows::Foundation::IInspectable>>(),
            &items);
        raw->Release();
    }
    reinterpret_cast<IUnknown*>(ic)->Release();
    if (!items) return;
    for (const std::wstring& o : options) {
        IInspectable* it = DevToolsActivate(L"Microsoft.UI.Xaml.Controls.ComboBoxItem");
        if (!it) continue;
        void* cc = nullptr;
        if (SUCCEEDED(it->QueryInterface(DevToolsIid<DevToolsXC::IContentControl>(), &cc)) && cc) {
            IInspectable* boxed = nullptr;
            if (SUCCEEDED(BoxString(o, &boxed)) && boxed) { DevToolsPutContent(cc, boxed); boxed->Release(); }
            reinterpret_cast<IUnknown*>(cc)->Release();
        }
        DevToolsVecAppend(reinterpret_cast<IInspectable*>(items), it);
        it->Release();
    }
    reinterpret_cast<IUnknown*>(items)->Release();
}

// Forward: build and populate one collapsed row instance. Declared here because an expansion builds its
// child rows through it, while it in turn populates rows whose expansion builds children -- one of the two
// has to be.
static void* BuildRowElement(const DevToolsCardRow& r, const std::wstring& path, int edit);

// The ROW LINE currently having its expansion populated. The expansion is loaded from its own DataTemplate,
// so it gets its OWN namescope: FindName inside it cannot see PValue on the line above, and the commit
// repaint needs exactly that. Set for the duration of one PopulateExpansionInto call and cleared after --
// everything here runs on the UI thread, one row at a time.
static void* g_expansionRowFe = nullptr;
static bool RowHasExpansion(const DevToolsCardRow& r, const std::wstring& path, int edit)
{
    if (edit >= 0 && !r.type.empty()) return true;
    if (r.chain.size() > 1)   return true;
    if (!r.children.empty())  return true;
    return RowHasOriginLine(r, path);
}

static bool EnsureExpansion(void* rowFe, const DevToolsCardRow& r, const std::wstring& path, int edit);

static bool FocusExpansionEditor(void* rowFe, const std::wstring& path);

static void ApplyRowExpansionState(void* rowFe, const std::wstring& path, bool open);

struct RowExpandSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen || wire != g_selWire || !rowFe || !row) return S_OK;
        const bool wantOpen = wantsEditor ? true : !PropIsExpanded(row->name);
        if (wantOpen) {
            if (!EnsureExpansion(rowFe, ExpansionRow(), path, edit)) return S_OK;
        }
        PropSetExpanded(row->name, wantOpen);
        ApplyRowExpansionState(rowFe, path, wantOpen);
        if (wantOpen && wantsEditor) FocusExpansionEditor(rowFe, path);
        return S_OK;
    }
    const DevToolsCardRow& ExpansionRow() const
    {
        const auto* current = wire == g_selWire ? FindPropertyRow(g_propRows, path) : nullptr;
        return current ? *current : *row;
    }
    void Init(std::shared_ptr<const DevToolsCardRow> r, const std::wstring& p, int e, void* fe, bool editor)
    {
        row = std::move(r); path = p; edit = e; rowFe = fe; wantsEditor = editor; wire = g_selWire;
        InitSink(IID_RoutedEventHandler_Del, g_route1Gen);
    }
    std::shared_ptr<const DevToolsCardRow> row;
    std::wstring path;
    InstanceHandle wire = 0;
    int edit = -1;
    void* rowFe = nullptr;
    bool wantsEditor = false;
};

// Show or hide a realized row's expansion and turn its triangle to match. One function so the three callers
// (the sink, the initial realize of an already-expanded property, and the rebuild after a commit) cannot
// disagree about which glyph means open.
static void ApplyRowExpansionState(void* rowFe, const std::wstring& path, bool open)
{
    if (IInspectable* blk = FindInRow(rowFe, L"PExpand")) {
        ShowEl(blk, open);
        blk->Release();
    }
    if (IInspectable* ch = FindInRow(rowFe, L"PChevron")) {
        void* cc = nullptr;
        if (SUCCEEDED(ch->QueryInterface(DevToolsIid<DevToolsXC::IContentControl>(), &cc)) && cc) {
            IInspectable* boxed = nullptr;
            if (SUCCEEDED(BoxString(open ? L"\uE70D" : L"\uE76C", &boxed)) && boxed) {
                DevToolsPutContent(cc, boxed);
                boxed->Release();
            }
            reinterpret_cast<IUnknown*>(cc)->Release();
        }
        SetAutomationTextOn(ch, (open ? L"Collapse " : L"Expand ") + path, L"", L"");
        ch->Release();
    }
}
