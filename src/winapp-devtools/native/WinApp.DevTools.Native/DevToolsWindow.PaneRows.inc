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

static const wchar_t* const kAddCommentHint = L"Enter to save \u00B7 Shift+Enter for a new line \u00B7 Esc to close";

// Rebuilding the properties pane (selecting another element, or the element disappearing) recreates the comment
// box. Text typed there but not saved is kept here so the rebuild never discards it: it comes back when the same
// element is selected again, and is offered for copying when the element is gone.
struct CommentBoxSaved {
    std::wstring id, text;
    bool editing = false, resolving = false;
    // Resolution removes the saved identity, not a draft opened while the writer was running.
    bool FinishResolve(int result) {
        resolving = false;
        if (result != 0 && result != 0x57410001) return false;
        id.clear();
        text.clear();
        return !editing;
    }
};
static std::map<InstanceHandle, std::wstring> g_commentDrafts; // unsaved text, per element
static winrt::weak_ref<DevToolsXC::TextBox> g_commentBox;
static std::shared_ptr<CommentBoxSaved> g_commentBoxSaved; // what the live box last loaded or saved

// Each property is its own named row so filtering, virtualization, and per-row actions can target one element.
static std::wstring BuildPropsFragment(const std::wstring& type, const std::wstring& name,
                                       const std::wstring& label, const std::wstring& breadcrumb,
                                       const std::wstring& sourceUri, unsigned int sourceLine,
                                       const std::vector<DevToolsCardRow>& rows,
                                       const std::wstring& authoredState,
                                       const std::wstring& authoredXaml,
                                       const DevToolsWindowLayout* layout)
{
    // The title, the breadcrumb and the Comment button live in the shell's details header, which does not scroll.
    (void)type; (void)name; (void)label; (void)breadcrumb;
    const bool csharp = authoredState == L"csharp";
    std::wstring m;
    m += L"<StackPanel xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\""
         L" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" Spacing=\"10\""
         L" AutomationProperties.AutomationId=\"WinAppDevToolsProps\">";
    // Show a source link only with actual XBF/source instrumentation and a file resolved under
    // the configured source root. Environment delivery is the host's responsibility.
    // The file, its actions and the authored XAML come from one source lookup, so they share one Source expander.
    const std::wstring disp = SourceDisplay(sourceUri, sourceLine);
    std::wstring sourceText, sourceStatus;
    if (!disp.empty()) {
        bool sourceRootMissing = false;
        const bool canOpen = !ResolveSourcePath(sourceUri, &sourceRootMissing).empty();
        // A left-aligned grid sizes its star column to the text, so the actions follow the file name and the name
        // trims with an ellipsis only when the row runs out of room.
        sourceText = L"<Grid Grid.Column=\"1\" HorizontalAlignment=\"Left\" VerticalAlignment=\"Center\" ColumnSpacing=\"6\">"
                     L"<Grid.ColumnDefinitions><ColumnDefinition Width=\"*\"/><ColumnDefinition Width=\"Auto\"/></Grid.ColumnDefinitions>"
                     L"<TextBlock Text=\"" + DevToolsXmlEscape(disp) + L"\" FontSize=\"12\" VerticalAlignment=\"Center\""
                     L" TextWrapping=\"NoWrap\" TextTrimming=\"CharacterEllipsis\" ToolTipService.ToolTip=\"" + DevToolsXmlEscape(disp) + L"\""
                     L" AutomationProperties.AutomationId=\"WinAppDevToolsSource\"/>";
        if (canOpen) {
            sourceText += L"<StackPanel Grid.Column=\"1\" Orientation=\"Horizontal\" Spacing=\"2\" VerticalAlignment=\"Center\">"
                          L"<HyperlinkButton x:Name=\"OpenSource\" Content=\"Open\" Padding=\"4,0,4,0\" FontSize=\"12\" VerticalAlignment=\"Center\""
                          L" ToolTipService.ToolTip=\"Open the source file in your default editor\""
                          L" AutomationProperties.Name=\"Open source file\" AutomationProperties.AutomationId=\"WinAppDevToolsOpenSource\"/>"
                          L"<HyperlinkButton x:Name=\"RevealSource\" Padding=\"4,2,4,2\" VerticalAlignment=\"Center\""
                          L" ToolTipService.ToolTip=\"Show in File Explorer\" AutomationProperties.Name=\"Show source file in File Explorer\""
                          L" AutomationProperties.AutomationId=\"WinAppDevToolsRevealSource\"><FontIcon Glyph=\"&#xE838;\" FontSize=\"12\"/></HyperlinkButton>"
                          L"<HyperlinkButton x:Name=\"CopySourcePath\" Padding=\"4,2,4,2\" VerticalAlignment=\"Center\""
                          L" ToolTipService.ToolTip=\"Copy path\" AutomationProperties.Name=\"Copy source file path\""
                          L" AutomationProperties.AutomationId=\"WinAppDevToolsCopySourcePath\"><FontIcon Glyph=\"&#xE8C8;\" FontSize=\"12\"/></HyperlinkButton>"
                          L"</StackPanel>";
        }
        sourceText += L"</Grid>";
        // Feedback from the source actions shows under the source and takes no space until there is something to say.
        const std::wstring status = canOpen ? std::wstring()
            : sourceRootMissing ? std::wstring(kSourceRootMissing)
            : std::wstring(L"Source file unavailable. Check that it exists under the project's source folder.");
        sourceStatus = L"<TextBlock x:Name=\"SourceStatus\" Text=\"" + DevToolsXmlEscape(status) + L"\""
                       L" Style=\"{StaticResource CaptionTextBlockStyle}\" TextWrapping=\"Wrap\""
                       DT_FG_SECONDARY L" AutomationProperties.LiveSetting=\"Polite\""
                       L" AutomationProperties.AutomationId=\"WinAppDevToolsSourceStatus\"" +
                       std::wstring(status.empty() ? L" Visibility=\"Collapsed\"" : L"") + L"/>";
    }
    // StackPanel spacing skips collapsed children, so the hidden feedback line adds no gap.
    if (!sourceStatus.empty()) m += L"<StackPanel Spacing=\"4\">";
    const std::wstring sourceIcon = L"<FontIcon Glyph=\"&#xE8A5;\" FontSize=\"12\" VerticalAlignment=\"Center\"" DT_FG_SECONDARY L"/>";
    if (!authoredXaml.empty()) {
        const bool likely = authoredState == L"likely";
        const std::wstring note = csharp
            ? L"C# on disk at the Reactor call site that created this element. The running app may differ after a rebuild or hot reload."
            : likely
            ? L"Likely source: unique line and identity match; compiled identity and line preservation are unverified."
            : L"Read from the file on disk. The running app may differ after a rebuild or hot reload.";
        m += L"<Expander x:Name=\"AxmlToggle\" HorizontalAlignment=\"Stretch\""
             L" HorizontalContentAlignment=\"Stretch\" AutomationProperties.Name=\"" +
             std::wstring(csharp ? L"C# source" : L"Source XAML") + L"\""
             L" AutomationProperties.AutomationId=\"WinAppDevToolsAuthoredXamlToggle\" IsExpanded=\"" +
             std::wstring(g_axmlExpanded ? L"True" : L"False") + L"\"><Expander.Header>";
        m += L"<Grid ColumnSpacing=\"8\"><Grid.ColumnDefinitions><ColumnDefinition Width=\"Auto\"/>"
             L"<ColumnDefinition Width=\"*\"/><ColumnDefinition Width=\"Auto\"/></Grid.ColumnDefinitions>";
        m += sourceIcon;
        m += sourceText.empty() ? std::wstring(L"<TextBlock Grid.Column=\"1\" Text=\"Source\" FontSize=\"12\" VerticalAlignment=\"Center\"/>") : sourceText;
        m += L"<Border Grid.Column=\"2\" Padding=\"6,0,6,1\" CornerRadius=\"4\" BorderThickness=\"1\" VerticalAlignment=\"Center\""
             L" BorderBrush=\"{ThemeResource ControlStrokeColorDefaultBrush}\""
             L" Background=\"{ThemeResource ControlFillColorSecondaryBrush}\""
             L" ToolTipService.ToolTip=\"" + DevToolsXmlEscape(note) + L"\">";
        m += L"<TextBlock Text=\"" + std::wstring(csharp ? L"C# on disk" : likely ? L"likely match" : L"on disk") + L"\" FontSize=\"11\""
             DT_FG_SECONDARY L" AutomationProperties.HelpText=\"" + DevToolsXmlEscape(note) + L"\""
             L" AutomationProperties.AutomationId=\"WinAppDevToolsSourceNote\"/></Border>";
        m += L"</Grid></Expander.Header>";
        m += L"<TextBlock x:Name=\"AxmlFull\" Text=\"" + DevToolsXmlEscape(authoredXaml, true) + L"\""
             L" FontFamily=\"Cascadia Mono, Consolas\" FontSize=\"11\" TextWrapping=\"Wrap\" IsTextSelectionEnabled=\"True\""
             L" Foreground=\"{ThemeResource TextFillColorPrimaryBrush}\""
             L" AutomationProperties.AutomationId=\"WinAppDevToolsAuthoredXaml\"/>";
        m += L"</Expander>";
    } else if (!sourceText.empty()) {
        m += L"<Grid ColumnSpacing=\"6\"><Grid.ColumnDefinitions><ColumnDefinition Width=\"Auto\"/>"
             L"<ColumnDefinition Width=\"*\"/></Grid.ColumnDefinitions>" + sourceIcon + sourceText + L"</Grid>";
    }
    if (!sourceStatus.empty()) m += sourceStatus + L"</StackPanel>";
    if (g_ctx.writeFn) {
        m += L"<StackPanel x:Name=\"AddCommentBox\" Visibility=\"Collapsed\" Spacing=\"6\">"
             L"<TextBox x:Name=\"AddCommentText\" AcceptsReturn=\"True\" TextWrapping=\"Wrap\" MinHeight=\"56\" MaxHeight=\"120\""
             L" FontSize=\"12\" PlaceholderText=\"Comment on this element&#x2026;\" AutomationProperties.Name=\"Comment\""
             L" AutomationProperties.AutomationId=\"WinAppDevToolsAddCommentText\"/>"
             L"<TextBlock x:Name=\"AddCommentStatus\" Text=\"" + DevToolsXmlEscape(kAddCommentHint) + L"\""
             L" Style=\"{StaticResource CaptionTextBlockStyle}\" TextWrapping=\"Wrap\""
             DT_FG_SECONDARY L" AutomationProperties.LiveSetting=\"Polite\""
             L" AutomationProperties.AutomationId=\"WinAppDevToolsAddCommentStatus\"/></StackPanel>";
        // The element's open comment. WireAddComment fills the card and swaps it with the box while editing.
        m += L"<Border x:Name=\"CommentCard\" Visibility=\"Collapsed\" Padding=\"12,10\" CornerRadius=\"6\" BorderThickness=\"1\""
             L" BorderBrush=\"{ThemeResource CardStrokeColorDefaultBrush}\""
             L" Background=\"{ThemeResource CardBackgroundFillColorDefaultBrush}\""
             L" AutomationProperties.AutomationId=\"WinAppDevToolsCommentCard\">"
             L"<Grid ColumnSpacing=\"10\"><Grid.ColumnDefinitions><ColumnDefinition Width=\"Auto\"/><ColumnDefinition Width=\"*\"/>"
             L"<ColumnDefinition Width=\"Auto\"/><ColumnDefinition Width=\"Auto\"/></Grid.ColumnDefinitions>"
             L"<Border Width=\"20\" Height=\"20\" CornerRadius=\"10\" VerticalAlignment=\"Top\""
             L" Background=\"{ThemeResource AccentFillColorDefaultBrush}\">"
             L"<FontIcon Glyph=\"&#xE90A;\" FontSize=\"10\" Foreground=\"{ThemeResource TextOnAccentFillColorPrimaryBrush}\""
             L" AutomationProperties.AccessibilityView=\"Raw\"/></Border>"
             L"<StackPanel Grid.Column=\"1\" Spacing=\"2\" VerticalAlignment=\"Center\">"
             L"<TextBlock x:Name=\"CommentCardText\" FontSize=\"13\" TextWrapping=\"Wrap\" IsTextSelectionEnabled=\"True\""
             L" AutomationProperties.AutomationId=\"WinAppDevToolsCommentCardText\"/>"
             L"<TextBlock x:Name=\"CommentCardMeta\" FontSize=\"11.5\" TextWrapping=\"Wrap\"" DT_FG_SECONDARY
             L" AutomationProperties.AutomationId=\"WinAppDevToolsCommentCardMeta\"/>"
             L"</StackPanel>"
             L"<HyperlinkButton x:Name=\"CommentCardEdit\" Grid.Column=\"2\" Content=\"Edit\" Padding=\"4,0\" FontSize=\"12\""
             L" VerticalAlignment=\"Top\" AutomationProperties.Name=\"Edit comment\""
             L" AutomationProperties.AutomationId=\"WinAppDevToolsCommentCardEdit\"/>"
             L"<Button x:Name=\"CommentCardResolve\" Grid.Column=\"3\" Padding=\"8,3\" FontSize=\"12\" VerticalAlignment=\"Top\""
             L" AutomationProperties.Name=\"Resolve comment\" AutomationProperties.AutomationId=\"WinAppDevToolsCommentCardResolve\">"
             L"<StackPanel Orientation=\"Horizontal\" Spacing=\"6\"><FontIcon Glyph=\"&#xE73E;\" FontSize=\"12\"/>"
             L"<TextBlock Text=\"Resolve\" FontSize=\"12\"/></StackPanel></Button>"
             L"</Grid></Border>";
    }
    if (rows.empty()) {
        m += L"<TextBlock Text=\"(no curated properties)\" Foreground=\"{ThemeResource TextFillColorTertiaryBrush}\"/>";
        m += g_selReferencesMarkup;
        m += L"<StackPanel x:Name=\"PendingReferences\"/>";
        if (layout && (layout->haveDesired || layout->haveRender || layout->haveOffset)) m += BuildLayoutDisclosure(*layout, rows);
        m += L"</StackPanel>";
        return m;
    }

    // LabeledBy / DescribedBy / FlowsTo / FlowsFrom / XYFocus* edges from and to this element, above the (long)
    // Reactor component section so they stay in view.
    m += g_selReferencesMarkup;
    m += L"<StackPanel x:Name=\"PendingReferences\"/>";

    // A Reactor element: the component that renders it (props, state, contexts, handlers), filled in after load.
    if (csharp || !g_selReactorLabel.empty()) {
        m += L"<StackPanel Spacing=\"4\" Margin=\"0,8,0,0\" AutomationProperties.AutomationId=\"WinAppDevToolsReactorSection\">";
        m += L"<TextBlock Text=\"Reactor component\" Style=\"{StaticResource CaptionTextBlockStyle}\""
             L" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\"/>";
        m += L"<StackPanel x:Name=\"ReactorSectionBody\" Spacing=\"4\"><TextBlock Text=\"Reading the component\u2026\" FontSize=\"12\""
             L" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\"/></StackPanel></StackPanel>";
    }

    if (authoredState != L"available" && !csharp) {
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
                  L"({x:Bind}, {ThemeResource}) can't be shown. A value an {x:Bind} sets appears as Local.";
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
        // One line in the header; the full reason is on hover and for screen readers. It names what is missing, so a
        // Local value set by a compiled binding is not read as a plain literal.
        m += L"<TextBlock Text=\"x:Bind and resource sources not shown (why?)\" FontSize=\"11\" TextWrapping=\"NoWrap\""
             L" TextTrimming=\"CharacterEllipsis\" Margin=\"0,0,0,2\""
             L" ToolTipService.ToolTip=\"" + DevToolsXmlEscape(why) + L"\""
             DT_FG_SECONDARY
             L" AutomationProperties.Name=\"x:Bind and resource sources not shown\""
             L" AutomationProperties.HelpText=\"" + DevToolsXmlEscape(why) + L"\""
             L" AutomationProperties.AutomationId=\"WinAppDevToolsAuthoredUnavailable\"/>";
    }
    if (layout && (layout->haveDesired || layout->haveRender || layout->haveOffset)) m += BuildLayoutDisclosure(*layout, rows);

    // Property toolbar: the filter box, with the Computed | Set choice to its right. ApplyPropViewSelection
    // rebuilds this fragment, so the selector's IsSelected state comes from the current view.
    const bool setView = ShowingSetView();
    m += L"<Grid ColumnSpacing=\"8\" Margin=\"0,2,0,0\"><Grid.ColumnDefinitions>"
         L"<ColumnDefinition Width=\"*\"/><ColumnDefinition Width=\"Auto\"/>"
         L"</Grid.ColumnDefinitions>";
    m += L"<TextBox x:Name=\"PropFilter\" MinHeight=\"32\" VerticalAlignment=\"Center\""
         L" PlaceholderText=\"Filter properties\u2026\" Text=\"" + DevToolsXmlEscape(g_propFilterQuery) + L"\""
         L" ToolTipService.ToolTip=\"Show only properties whose name, value or authored text contains this text\""
         L" AutomationProperties.AutomationId=\"WinAppDevToolsPropFilter\""
         L" AutomationProperties.Name=\"Filter properties\"/>";
    m += L"<SelectorBar x:Name=\"PropViewSelector\" Grid.Column=\"1\" VerticalAlignment=\"Center\""
         L" AutomationProperties.Name=\"Property view\" AutomationProperties.AutomationId=\"WinAppDevToolsPropView\">";
    m += L"<SelectorBarItem x:Name=\"ViewComputed\" Text=\"Computed\" IsSelected=\"" + std::wstring(setView ? L"False" : L"True") + L"\""
         L" AutomationProperties.Name=\"Computed view\" AutomationProperties.AutomationId=\"WinAppDevToolsViewComputed\""
         L" ToolTipService.ToolTip=\"Every property, with one resolved value each: what this element's state IS\"/>";
    m += L"<SelectorBarItem x:Name=\"OnlySetToggle\" Text=\"Set\" IsSelected=\"" + std::wstring(setView ? L"True" : L"False") + L"\""
         L" AutomationProperties.Name=\"Set view\" AutomationProperties.AutomationId=\"WinAppDevToolsViewSet\""
         L" ToolTipService.ToolTip=\"Only what was written on this element, grouped by where it came from: what you actually SET\"/>";
    m += L"</SelectorBar>";
    m += L"</Grid>";

    m += L"<ItemsRepeater x:Name=\"PropRepeater\" TabFocusNavigation=\"Local\">"
         L"<ItemsRepeater.Layout><StackLayout Spacing=\"2\"/></ItemsRepeater.Layout>"
         L"</ItemsRepeater>";

    m += L"<Border x:Name=\"PropNoMatch\" Visibility=\"Collapsed\" Margin=\"0,10,0,0\" Padding=\"10,8,10,9\""
         L" CornerRadius=\"4\" Background=\"{ThemeResource ControlFillColorSecondaryBrush}\""
         L" BorderThickness=\"1\" BorderBrush=\"{ThemeResource ControlStrokeColorDefaultBrush}\""
         L" AutomationProperties.AutomationId=\"WinAppDevToolsPropNoMatch\">";
    m += L"<StackPanel Spacing=\"3\">";
    m += L"<TextBlock x:Name=\"PropNoMatchLbl\" FontSize=\"12\" TextWrapping=\"Wrap\" Text=\"\""
         L" AutomationProperties.AutomationId=\"WinAppDevToolsPropNoMatchLabel\"/>";
    m += L"<TextBlock"
         DT_NOTE_TEXT
         L" Text=\"The filter matches a property name, its value, and its authored text.\"/>";
    m += L"<HyperlinkButton x:Name=\"PropClearFilter\" Padding=\"0\" Margin=\"0,2,0,0\" FontSize=\"12\""
         L" Visibility=\"Collapsed\""
         L" AutomationProperties.AutomationId=\"WinAppDevToolsPropClearFilter\""
         L" AutomationProperties.Name=\"Clear the property filter\">"
         L"<TextBlock Text=\"Clear the filter\" FontSize=\"12\""
         L" AutomationProperties.AccessibilityView=\"Raw\"/>"
         L"</HyperlinkButton>";
    m += L"</StackPanel></Border>";

    if (!setView) {
        m += L"<HyperlinkButton x:Name=\"PropShowAll\" Padding=\"0\" Margin=\"0,4,0,0\" FontSize=\"12\""
             L" AutomationProperties.AutomationId=\"WinAppDevToolsPropShowAll\""
             L" AutomationProperties.Name=\"Toggle the full property list\">";
        m += L"<TextBlock x:Name=\"PropShowAllLbl\" FontSize=\"12\" Text=\"" +
             DevToolsXmlEscape(PropShowAllLabel(rows)) + L"\""
             L" AutomationProperties.AutomationId=\"WinAppDevToolsPropShowAllLabel\""
             L" AutomationProperties.AccessibilityView=\"Raw\"/>";
        m += L"</HyperlinkButton>";
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
                     DT_FG_SECONDARY L" FontSize=\"12\""
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

// Call before the pane is rebuilt, while g_selWire still names the element the box belongs to.
static void StashCommentDraft()
{
    auto box = g_commentBox.get();
    g_commentBox = nullptr;
    if (!box || !g_selWire) return;
    try {
        // A read-only box is mid-save: the writer already has this text, so it is not an unsaved draft.
        if (box.IsReadOnly()) return;
        std::wstring value(box.Text());
        const bool unchanged = g_commentBoxSaved && DevToolsCommentText::Unchanged(value, g_commentBoxSaved->text);
        if (!DevToolsCommentText::IsBlank(value) && !unchanged) g_commentDrafts[g_selWire] = std::move(value);
        else g_commentDrafts.erase(g_selWire);
    } catch (const winrt::hresult_error&) {}
}

// The element-gone notice, plus the unsaved comment and a button that copies it. The element can't take the
// comment any more, so this is the user's only way to keep what they typed.
static void ShowCommentDraftNotice(const wchar_t* text, const std::wstring& draft)
{
    if (!g_ctx.propsHostChildren) return;
    std::wstring m = L"<StackPanel xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\""
                     L" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" Margin=\"12,12,12,12\" Spacing=\"8\">"
                     L"<TextBlock" DT_FG_SECONDARY L" FontSize=\"12\" TextWrapping=\"Wrap\" Text=\"";
    m += DevToolsXmlEscape(text);
    m += L" Your comment wasn't saved. Copy it so you can add it to another element.\"/>"
         L"<TextBox x:Name=\"DraftText\" IsReadOnly=\"True\" AcceptsReturn=\"True\" TextWrapping=\"Wrap\" MaxHeight=\"120\""
         L" FontSize=\"12\" AutomationProperties.Name=\"Unsaved comment\""
         L" AutomationProperties.AutomationId=\"WinAppDevToolsUnsavedComment\"/>"
         L"<Button x:Name=\"DraftCopy\" Content=\"Copy comment\" Padding=\"8,3\" FontSize=\"12\""
         L" AutomationProperties.AutomationId=\"WinAppDevToolsCopyUnsavedComment\"/>"
         L"<TextBlock x:Name=\"DraftStatus\"" DT_FG_SECONDARY L" FontSize=\"12\" AutomationProperties.LiveSetting=\"Polite\"/>"
         L"</StackPanel>";
    IInspectable* frag = nullptr;
    if (FAILED(LoadMarkupWin(m, &frag)) || !frag) {
        DevToolsOverlayLog(L"route1.props draft notice FAILED to load");
        ShowPaneNotice(text);
        return;
    }
    try {
        winrt::Windows::Foundation::IInspectable object{ nullptr };
        winrt::copy_from_abi(object, frag);
        auto root = object.as<DevToolsX::FrameworkElement>();
        auto box = root.FindName(L"DraftText").try_as<DevToolsXC::TextBox>();
        auto copy = root.FindName(L"DraftCopy").try_as<DevToolsXC::Button>();
        auto status = root.FindName(L"DraftStatus").try_as<DevToolsXC::TextBlock>();
        if (box) box.Text(draft);
        if (copy && status) {
            copy.Click([draft, status](auto&&, auto&&) {
                status.Text(CopyTextToClipboard(draft) ? L"Copied." : L"Couldn't copy. Select the text and press Ctrl+C.");
            });
        }
        DevToolsVecAppend(g_ctx.propsHostChildren, reinterpret_cast<IInspectable*>(winrt::get_abi(root.as<DevToolsX::UIElement>())));
    } catch (const winrt::hresult_error& e) {
        DevToolsOverlayLog(L"route1.props draft notice wiring failed hr=0x%08X", e.code().value);
        ShowPaneNotice(text);
    }
    frag->Release();
}

// Debounce for the live tree refresh. DevToolsTreeWatch already coalesces a burst of mutations into one
// delivery, but a navigation is several bursts across several dispatcher turns, and a row rebuild is far more
// expensive than a notification. Re-arming the SAME timer id resets its elapsed time, so N notifications
// arriving inside the window produce exactly one rebuild, once the app has settled.
static const UINT_PTR kRefreshTimerId  = 0xD72; // unique on the inspector HWND (see kPlaceTimerId / kWatchTimerId)
static const UINT     kRefreshDebounceMs = 250;
// A tree that never settles (a list re-created on a timer) is followed at a steady pace instead of after every change:
// at most one rebuild every two seconds, and none later than three seconds after the first change it shows. Each row
// rebuild costs the app memory it does not give back, so a list that changes several times a second must not set
// the pace.
static const ULONGLONG kRefreshMinGapMs = 2000;
static const ULONGLONG kRefreshMaxWaitMs = 3000;
static ULONGLONG g_treeRefreshFirstPending = 0, g_treeRefreshLast = 0;
static HWND Route1WindowHwnd();

// When the live tree refresh runs, for a change at `now`: the debounce, held to the pace above.
static ULONGLONG TreeRefreshDueAt(ULONGLONG now, ULONGLONG firstPending, ULONGLONG lastRefresh)
{
    ULONGLONG due = now + kRefreshDebounceMs;
    if (lastRefresh) due = (std::max)(due, lastRefresh + kRefreshMinGapMs);
    if (firstPending) due = (std::min)(due, firstPending + kRefreshMaxWaitMs);
    return (std::max)(due, now);
}

// A new Reactor description changes which rows are the app's own; it is applied with the next paced refresh.
static bool g_treeReclassifyPending = false;

static VOID CALLBACK RefreshTimerProc(HWND hwnd, UINT, UINT_PTR id, DWORD)
{
    KillTimer(hwnd, id);
    g_treeRefreshFirstPending = 0;
    g_treeRefreshLast = GetTickCount64();
    if (!g_treeReclassifyPending) { RefreshTreeFromApp(); return; }
    g_treeReclassifyPending = false;
    g_reclassifying = true;
    struct Reset { ~Reset() { g_reclassifying = false; } } reset;
    RefreshTreeFromApp(true);
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
    const ULONGLONG now = GetTickCount64();
    if (!g_treeRefreshFirstPending) g_treeRefreshFirstPending = now;
    const UINT wait = static_cast<UINT>(TreeRefreshDueAt(now, g_treeRefreshFirstPending, g_treeRefreshLast) - now);
    if (!SetTimer(h, kRefreshTimerId, (std::max)(wait, static_cast<UINT>(USER_TIMER_MINIMUM)), RefreshTimerProc))
        DevToolsOverlayLog(L"route1.refresh SetTimer FAILED (err=%lu) -- the tree will stay out of date", GetLastError());
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
                         L"%zu nodes (out of date: app now has %zu, refreshing\u2026)",
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
    const InstanceHandle gone = g_selWire;
    StashCommentDraft();
    // ShowPropsForNode below sees no selection change once g_selWire is 0, so clear the footer here.
    SetStatusText(L"");
    g_selWire = 0;
    g_selectedWire = 0;
    LiveUnwatch();
    SyncTreeSelection();
    ShowPropsForNode(0, L"(no selection)", L"", true);
    static const wchar_t* const kGone = L"The selected element is gone. The app navigated or removed it.";
    if (auto it = gone ? g_commentDrafts.find(gone) : g_commentDrafts.end(); it != g_commentDrafts.end()) {
        const std::wstring draft = std::move(it->second);
        g_commentDrafts.erase(it);
        ShowCommentDraftNotice(kGone, draft);
    } else {
        ShowPaneNotice(kGone);
    }
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

static void RebuildExpansion(void* rowFe, const DevToolsCardRow& r, const std::wstring& path, int edit);

static bool RefreshCommittedValue(const std::wstring& prop);

static void ApplyRowExpansionState(void* rowFe, const std::wstring& path, bool open);

struct RowExpandSink : DevToolsSinkBase
{
    // Unlike the shared/static sinks, these delegates have one owner per realized property row.
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID r, void** p) override
    {
        const HRESULT hr = DevToolsSinkBase::QueryInterface(r, p);
        if (SUCCEEDED(hr) && r != __uuidof(IMarshal)) AddRef();
        return hr;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&refs); }
    ULONG STDMETHODCALLTYPE Release() override
    {
        const ULONG remaining = InterlockedDecrement(&refs);
        if (!remaining) delete this;
        return remaining;
    }
    ~RowExpandSink()
    {
        if (rowFe) reinterpret_cast<IUnknown*>(rowFe)->Release();
        if (ftm) ftm->Release();
    }

    void Retire()
    {
        auto* heldRow = reinterpret_cast<IUnknown*>(rowFe);
        rowFe = nullptr;
        row.reset();
        if (publisher) {
            const HRESULT hr = open ? DevToolsRemoveExpanding(publisher.get(), token)
                                    : DevToolsRemoveCollapsed(publisher.get(), token);
            if (FAILED(hr))
                DevToolsOverlayLog(L"route1.props row '%s' unsubscribe failed hr=0x%08X", path.c_str(), hr);
            publisher = nullptr;
        }
        if (heldRow) heldRow->Release();
    }

    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen || wire != g_selWire || !rowFe || !row) return S_OK;
        // A live read can reenter pane replacement. Keep this invocation's inputs alive, but do not
        // continue acting on the old pane if that read retired the subscription.
        const auto snapshot = row;
        winrt::com_ptr<IUnknown> heldRow;
        heldRow.copy_from(reinterpret_cast<IUnknown*>(rowFe));
        const bool wantOpen = open;
        if (wantOpen) {
            // The row is a snapshot; opening it reads the property live, unless a write is still unconfirmed.
            const bool opening = !PropIsExpanded(row->name);
            const bool refreshed = opening && g_pendingWrites.empty() && g_pendingKind.empty() && RefreshCommittedValue(path);
            if (!rowFe) return S_OK;
            if (refreshed) {
                RebuildExpansion(rowFe, ExpansionRow(), path, edit);
            } else if (!EnsureExpansion(rowFe, ExpansionRow(), path, edit)) {
                ApplyRowExpansionState(rowFe, path, false);
                SetStatusText(L"Couldn't open " + path);
                return S_OK;
            }
        }
        if (!rowFe) return S_OK;
        PropSetExpanded(snapshot->name, wantOpen);
        ApplyRowExpansionState(rowFe, path, wantOpen);
        return S_OK;
    }
    const DevToolsCardRow& ExpansionRow() const
    {
        const auto* current = wire == g_selWire ? FindPropertyRow(g_propRows, path) : nullptr;
        return current ? *current : *row;
    }
    void Init(std::shared_ptr<const DevToolsCardRow> r, const std::wstring& p, int e, void* fe, bool expanding)
    {
        row = std::move(r); path = p; edit = e; rowFe = fe; open = expanding; wire = g_selWire;
        if (rowFe) reinterpret_cast<IUnknown*>(rowFe)->AddRef();
        const auto& iid = expanding
            ? DevToolsIid<winrt::Windows::Foundation::TypedEventHandler<DevToolsXC::Expander, DevToolsXC::ExpanderExpandingEventArgs>>()
            : DevToolsIid<winrt::Windows::Foundation::TypedEventHandler<DevToolsXC::Expander, DevToolsXC::ExpanderCollapsedEventArgs>>();
        InitSink(iid, g_route1Gen);
    }
    std::shared_ptr<const DevToolsCardRow> row;
    winrt::com_ptr<IUnknown> publisher;
    __int64 token = 0;
    long refs = 1;
    std::wstring path;
    InstanceHandle wire = 0;
    int edit = -1;
    void* rowFe = nullptr;
    bool open = false;
};

static std::vector<RowExpandSink*> g_rowExpandSinks;

static void WireRowExpansion(const std::shared_ptr<const DevToolsCardRow>& row, const std::wstring& path,
                             int edit, void* rowFe, void* expander)
{
    for (bool open : { true, false }) {
        auto* sink = new RowExpandSink();
        sink->Init(row, path, edit, rowFe, open);
        const HRESULT hr = open ? DevToolsAddExpanding(expander, sink, &sink->token)
                                : DevToolsAddCollapsed(expander, sink, &sink->token);
        if (FAILED(hr)) {
            DevToolsOverlayLog(L"route1.props row '%s' expansion event failed hr=0x%08X", path.c_str(), hr);
            sink->Release();
        } else {
            sink->publisher.copy_from(reinterpret_cast<IUnknown*>(expander));
            g_rowExpandSinks.push_back(sink);
        }
    }
}

// Descendant rows are replaced with their parent's expansion; the parent header remains subscribed.
static void RetireRowExpandSinks(void* rowFe = nullptr, bool descendantsOnly = false)
{
    std::wstring path;
    if (rowFe) {
        for (const auto* sink : g_rowExpandSinks)
            if (sink->rowFe == rowFe) { path = sink->path; break; }
        if (path.empty()) return;
    }
    std::vector<RowExpandSink*> retiring;
    for (size_t i = g_rowExpandSinks.size(); i-- > 0; ) {
        auto* sink = g_rowExpandSinks[i];
        const bool child = sink->path.compare(0, path.size() + 1, path + L".") == 0;
        if (rowFe && !(child || (!descendantsOnly && sink->rowFe == rowFe))) continue;
        retiring.push_back(sink);
        g_rowExpandSinks.erase(g_rowExpandSinks.begin() + i);
    }
    for (auto* sink : retiring) {
        sink->Retire();
        sink->Release();
    }
}

// Keep restored rows and editor rebuilds in sync with the platform's expansion state.
static void ApplyRowExpansionState(void* rowFe, const std::wstring& path, bool open)
{
    if (IInspectable* header = FindInRow(rowFe, L"PHeader")) {
        void* expander = nullptr;
        if (SUCCEEDED(header->QueryInterface(DevToolsIid<DevToolsXC::IExpander>(), &expander)) && expander) {
            bool current = false;
            HRESULT hr = DevToolsGetIsExpanded(expander, &current);
            if (SUCCEEDED(hr) && current != open)
                hr = DevToolsAbi<DevToolsXC::IExpander>(expander)->put_IsExpanded(open);
            if (FAILED(hr))
                DevToolsOverlayLog(L"route1.props expansion state failed for '%s' hr=0x%08X", path.c_str(), hr);
            reinterpret_cast<IUnknown*>(expander)->Release();
        }
        header->Release();
    }
}
