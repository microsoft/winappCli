// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "DevToolsTheme.h"

#include <cstdio>
#include <filesystem>
#include <fstream>
#include <string>

static int g_toolbarFailures = 0;

static void CheckToolbar(bool condition, const char* what)
{
    if (!condition) { ++g_toolbarFailures; std::printf("  FAIL  %s\n", what); }
    else            {                      std::printf("  ok    %s\n", what); }
}

static std::string ReadText(const std::filesystem::path& path)
{
    std::ifstream input(path, std::ios::binary);
    return std::string(std::istreambuf_iterator<char>(input), std::istreambuf_iterator<char>());
}

// A translation unit split into #include "X.*.inc" parts, read as the compiler sees it.
static std::string ReadUnit(const std::filesystem::path& path)
{
    std::string text = ReadText(path), out;
    size_t pos = 0;
    for (size_t at; (at = text.find("#include \"", pos)) != std::string::npos; ) {
        const size_t close = text.find('"', at + 10);
        const std::string name = text.substr(at + 10, close - at - 10);
        out += text.substr(pos, at - pos);
        out += name.size() > 4 && name.compare(name.size() - 4, 4, ".inc") == 0
            ? ReadText(path.parent_path() / name) : text.substr(at, close + 1 - at);
        pos = close + 1;
    }
    return out + text.substr(pos);
}

static size_t CountOf(const std::string& text, const std::string& value)
{
    size_t count = 0;
    for (size_t pos = text.find(value); pos != std::string::npos; pos = text.find(value, pos + value.size()))
        ++count;
    return count;
}

static void TestThemeTarget()
{
    CheckToolbar(DevToolsTheme::ToggleTarget(DevToolsTheme::kLight) == DevToolsTheme::kDark,
                 "Light targets Dark");
    CheckToolbar(DevToolsTheme::ToggleTarget(DevToolsTheme::kDark) == DevToolsTheme::kLight,
                 "Dark targets Light");
    CheckToolbar(DevToolsTheme::ToggleTarget(DevToolsTheme::kDefault) == DevToolsTheme::kDark,
                 "an unexpected effective theme safely targets Dark");
}

static void TestToolbarContract()
{
    const std::filesystem::path testDir = std::filesystem::path(__FILE__).parent_path();
    const std::filesystem::path tapDir = testDir.parent_path() / "native" / "WinApp.DevTools.Native";
    const std::string xaml = ReadText(tapDir / "xaml" / "toolbar.xaml");
    const std::string selectionPanel = ReadText(tapDir / "xaml" / "selection-panel.xaml");
    const std::string windowShell = ReadText(tapDir / "xaml-window" / "window-shell.xaml");
    const std::string overlay = ReadUnit(tapDir / "DevToolsOverlay.cpp");
    const std::string window = ReadUnit(tapDir / "DevToolsWindow.cpp");
    CheckToolbar(!xaml.empty(), "toolbar.xaml is readable");
    CheckToolbar(!selectionPanel.empty(), "selection-panel.xaml is readable");
    CheckToolbar(!windowShell.empty(), "window-shell.xaml is readable");
    CheckToolbar(!overlay.empty(), "DevToolsOverlay.cpp is readable");
    CheckToolbar(!window.empty(), "DevToolsWindow.cpp is readable");
    CheckToolbar(CountOf(selectionPanel, "AutomationProperties.AutomationId=\"DevToolsSelCommentSave\"") == 1 &&
                     selectionPanel.find("AutomationProperties.Name=\"Save comment\"") != std::string::npos &&
                     overlay.find("g_selCommentSaveSink.Bind(&IID_RoutedEventHandler_Del, &OnSelCommentSaveClick)") != std::string::npos,
                 "inline comment has one accessible Save action wired to the shared commit");
    CheckToolbar(xaml.find("-10000") == std::string::npos &&
                     overlay.find("kParkedPos") == std::string::npos,
                 "hidden toolbar and comment markers do not enlarge popup bounds by parking");
    CheckToolbar(xaml.find("x:Name=\"DevToolsBar\" Visibility=\"Collapsed\"") != std::string::npos &&
                     CountOf(xaml, "<Grid Visibility=\"Collapsed\" Canvas.ZIndex=\"1010\"") == 6,
                 "action bar and all docking targets start collapsed");
    CheckToolbar(overlay.find("DevToolsUiEvent::GotFocus, g_protoSink[12], &OnPillGotFocus") != std::string::npos &&
                     overlay.find("DevToolsUiEvent::GotFocus, g_protoSink[13], &OnToolbarGotFocus") != std::string::npos &&
                     overlay.find("DevToolsUiEvent::LostFocus, g_protoSink[14], &OnToolbarLostFocus") != std::string::npos,
                 "keyboard focus opens actions and keeps the toolbar visible");
    const auto readStart = windowShell.find("<ContentControl x:Name=\"TmplExpRead\">");
    const auto readEnd = windowShell.find("</ContentControl>", readStart);
    const std::string readExpansion = readStart != std::string::npos && readEnd != std::string::npos
        ? windowShell.substr(readStart, readEnd - readStart) : std::string();
    for (const auto* name : {"PWitAuthored", "POrigin", "PWitSession", "PSessionText", "PSessionNote", "PWitChain", "PChain"})
        CheckToolbar(CountOf(readExpansion, std::string("x:Name=\"") + name + "\"") == 1,
            "read-only expansion provides each production provenance target");

    for (const std::string& id : {
            "DevToolsProtoPick", "DevToolsProtoComments", "DevToolsProtoInspect",
            "DevToolsProtoLayout", "DevToolsProtoTheme", "DevToolsProtoPin" }) {
        CheckToolbar(CountOf(xaml, "x:Name=\"" + id + "\"") == 1,
                     (id + " has exactly one namescope entry").c_str());
    }
    CheckToolbar(CountOf(xaml, "AutomationProperties.AutomationId=\"DevToolsProtoTheme\"") == 1,
                 "Theme has exactly one stable AutomationId");
    CheckToolbar(xaml.find("<ToggleButton x:Name=\"DevToolsProtoLayout\"") != std::string::npos &&
                     xaml.find("ToolTip=\"Show layout adorners") == std::string::npos,
                 "Layout adorners is a toggle for UI Automation, with a tooltip that holds in both states");
    CheckToolbar(xaml.find("<ToggleButton x:Name=\"DevToolsProtoPin\"") != std::string::npos &&
                     xaml.find("x:Name=\"DevToolsPinOn\"") != std::string::npos &&
                     xaml.find("DevToolsProtoPinDot") == std::string::npos &&
                     overlay.find("ProtoSetToggle(L\"DevToolsProtoPin\", L\"DevToolsPinOn\", L\"DevToolsPinOff\", on);") != std::string::npos,
                 "Pinned uses the accent on-state and a UIA toggle, not a corner dot that reads as a badge");
    CheckToolbar(xaml.find("ToolTipService.ToolTip=\"Switch app theme\"") != std::string::npos,
                 "Theme tooltip is short enough not to clip at the window edge");
    CheckToolbar(xaml.find("AutomationProperties.Name=\"Switch app theme\"") != std::string::npos,
                 "Theme's accessible name matches its tooltip");
    CheckToolbar(xaml.find("Glyph=\"&#xE706;\"") != std::string::npos,
                 "Theme uses the Segoe MDL2 Brightness glyph");
    const size_t inspectPos = xaml.find("x:Name=\"DevToolsProtoInspect\"");
    const size_t pinPos = xaml.find("x:Name=\"DevToolsProtoPin\"");
    CheckToolbar(inspectPos < pinPos &&
                     CountOf(xaml.substr(inspectPos, pinPos - inspectPos), "x:Name=\"DevToolsProto") == 1,
                 "Open DevTools is immediately before Pin in the action order");
    CheckToolbar(xaml.find("FontFamily=\"Segoe MDL2 Assets\"") == std::string::npos &&
                     selectionPanel.find("FontFamily=\"Segoe MDL2 Assets\"") == std::string::npos &&
                     windowShell.find("FontFamily=\"Segoe MDL2 Assets\"") == std::string::npos &&
                     overlay.find("FontFamily=\\\"Segoe MDL2 Assets\\\"") == std::string::npos,
                 "DevTools FontIcons inherit the platform icon font");

    CheckToolbar(CountOf(xaml, "RequestedTheme=\"$TOOLBARTHEME$\"") == 2,
                 "both toolbar surfaces request the inverse app theme");
    CheckToolbar(CountOf(xaml, "BackgroundSizing=\"InnerBorderEdge\"") == 2,
                 "both toolbar surfaces clip Acrylic inside their borders");
    CheckToolbar(overlay.find("static const DevToolsPalette& DevToolsInverseAcrylicPalette()") != std::string::npos &&
                     overlay.find("{ThemeResource AcrylicBackgroundFillColorDefaultBrush}") != std::string::npos &&
                     overlay.find("{ThemeResource SurfaceStrokeColorFlyoutBrush}") != std::string::npos,
                 "the toolbar uses standard WinUI flyout Acrylic and stroke resources");
    CheckToolbar(overlay.find("{ThemeResource AccentFillColorSecondaryBrush}") != std::string::npos,
                 "the toolbar snap target uses a theme-aware accent brush");
    CheckToolbar(xaml.find("AutomationProperties.AutomationId=\"DevToolsSnapTC\"") != std::string::npos &&
                     xaml.find("AutomationProperties.AutomationId=\"DevToolsSnapBC\"") != std::string::npos &&
                     xaml.find("x:Name=\"DevToolsSnapNear5\"") != std::string::npos,
                 "the toolbar offers top-center and bottom-center docking targets");
    CheckToolbar(overlay.find("if (SUCCEEDED(hrPut)) DevToolsSetToolbarRequestedTheme(target);") != std::string::npos,
                 "the toolbar stays inverse when its app-theme action is used");

    CheckToolbar(selectionPanel.find("RequestedTheme=\"$PANELTHEME$\"") != std::string::npos,
                 "the quick-edit panel requests the inverse app theme");
    CheckToolbar(selectionPanel.find("BackgroundSizing=\"InnerBorderEdge\"") != std::string::npos,
                 "the quick-edit panel clips Acrylic inside its border");
    CheckToolbar(overlay.find("{ThemeResource AcrylicBackgroundFillColorDefaultBrush}") != std::string::npos,
                 "the quick-edit panel uses the standard flyout Acrylic brush");
    CheckToolbar(overlay.find("DevToolsSetSelectionPanelRequestedTheme(toolbarTheme);") != std::string::npos &&
                     overlay.find("DevToolsPutRequestedTheme(fe, requestedTheme);") != std::string::npos,
                 "an open quick-edit panel stays inverse when the app theme changes");
    CheckToolbar(overlay.find("{ThemeResource SurfaceStrokeColorFlyoutBrush}") != std::string::npos,
                 "the quick-edit panel uses the standard flyout stroke");
    CheckToolbar(selectionPanel.find("x:Key=\"ButtonBackground\"") == std::string::npos &&
                     selectionPanel.find("x:Key=\"ButtonForeground\"") == std::string::npos,
                 "quick-edit controls inherit inverse-theme platform states");
    const auto commentLabel = selectionPanel.find("Text=\"Comment\"");
    const auto commentInput = selectionPanel.find("<TextBox x:Name=\"DevToolsSelComment\"");
    const auto commentInputEnd = selectionPanel.find("/>", commentInput);
    const auto quickProperties = selectionPanel.find("x:Name=\"DevToolsSelRows\"");
    const auto openDevTools = selectionPanel.find("<Button x:Name=\"DevToolsSelOpen\"");
    CheckToolbar(commentLabel != std::string::npos && commentInput != std::string::npos &&
                     commentInputEnd != std::string::npos && commentLabel < commentInput &&
                     selectionPanel.substr(commentInput, commentInputEnd - commentInput).find(
                         "AutomationProperties.Name=\"Comment\"") != std::string::npos,
                 "the visible Comment label precedes a comment box with its own accessible name");
    CheckToolbar(quickProperties != std::string::npos && openDevTools != std::string::npos &&
                     commentInput < quickProperties && quickProperties < openDevTools &&
                     selectionPanel.find("TabIndex=") == std::string::npos,
                 "default keyboard order reaches the comment before quick properties and Open in DevTools");
    CheckToolbar(overlay.find("static const int kSelPanelW = 400;") != std::string::npos &&
                     overlay.find("<ColumnDefinition Width=\\\"104\\\"/>") != std::string::npos &&
                     overlay.find("HorizontalAlignment=\\\"Stretch\\\" MinHeight=\\\"32\\\"") != std::string::npos,
                 "quick-edit rows share one label column and stretch 32 DIP editors into the value column");
    CheckToolbar(windowShell.find("Background=\"Transparent\"") != std::string::npos,
                 "the DevTools window shell exposes its system backdrop");
    CheckToolbar(windowShell.find("InspectorTitleBarHost") == std::string::npos &&
                     window.find("<TitleBar xmlns=") == std::string::npos &&
                     window.find("DevToolsWinExtendTitleBar(") == std::string::npos &&
                     window.find("DevToolsWinSetTitleBar(") == std::string::npos &&
                     window.find("ConfigureInspectorCaptionTheme(winInsp, content)") != std::string::npos &&
                     window.find("DevToolsAddActualThemeChanged(g_captionThemeRoot, sink, &g_captionThemeToken)") != std::string::npos &&
                     windowShell.find("<TitleBar ") == std::string::npos,
                 "inspector themes the native caption without custom frame or duplicate titlebar");
    CheckToolbar(windowShell.find("Snapshot values") == std::string::npos &&
                     windowShell.find("RefreshInspectorSnapshot") == std::string::npos,
                 "the inspector has no snapshot banner or refresh button: the selected element is live");
    CheckToolbar(window.find("ApplyMicaBackdrop(winInsp)") != std::string::npos &&
                     window.find("<MicaBackdrop xmlns=") != std::string::npos &&
                     window.find("DesktopAcrylicBackdrop") == std::string::npos,
                 "the DevTools window uses the native Mica backdrop");
    CheckToolbar(windowShell.find("x:Name=\"DetailsPane\"") != std::string::npos &&
                     windowShell.find("Background=\"{ThemeResource CardBackgroundFillColorSecondaryBrush}\"") != std::string::npos &&
                     windowShell.find("BorderBrush=\"{ThemeResource CardBackgroundFillColorDefaultBrush}\"") != std::string::npos &&
                     windowShell.find("BorderThickness=\"1\"") != std::string::npos &&
                     windowShell.find("CornerRadius=\"8,0,0,0\"") != std::string::npos,
                 "the details pane uses the secondary card surface with a one-pixel rounded stroke");
    // a value the runtime cannot hand back as a real one is stated by TYPE STYLE -- italic secondary --
    // rather than by colour alone, because a High Contrast theme replaces the pane's brushes wholesale and a
    // colour-only signal says nothing there. So this asserts BOTH halves: the secondary brush and the
    // italic. Asserting only the brush would stay green on a build that dropped the style, which is the half
    // that survives HC.
    //
    // AND IT IS THREE ABSENCES, NOT ONE. "unset" is the Double.NaN case the runtime renders
    // as the word "Auto"; "null" is a null reference; "unresolved" is an object -- a gradient or image brush --
    // whose display value is the type name, which is what was filed on: a row reading "{SolidColorBrush}"
    // as though that were a value. Pinning only the "Auto" case would leave the other two rendering their
    // tokens verbatim, which is the class generalises.
    CheckToolbar(window.find("if (unset)                              shown = L\"Auto\";") != std::string::npos &&
                     window.find("else if (r.valueState == L\"null\")       shown = L\"(null)\";") != std::string::npos &&
                     window.find("else if (r.valueState == L\"unresolved\") shown = ShortType(") != std::string::npos &&
                     window.find("noValue ? static_cast<void*>(g_palSecondary)") != std::string::npos &&
                     window.find("DevToolsPutFontStyle(tb, noValue ? 2 /*Italic*/ : 0 /*Normal*/)") != std::string::npos,
                 "a row with no real value renders the absence, in italic secondary, never the runtime's token");
    // And the EDITOR for such a row seeds EMPTY with a placeholder, so the box genuinely contains nothing and
    // clearing it returns to that state. One rule, shared by the emitter and the commit sink's baseline.
    CheckToolbar(window.find("if (RowValueIsMissing(r)) return std::wstring();") != std::string::npos &&
                     window.find("SetPlaceholderOn(e, RowEditPlaceholder(r));") != std::string::npos,
                 "and its editor seeds empty with a placeholder rather than a value that cannot parse back");
    // An empty live String is a value, not missing authored text.
    CheckToolbar(window.find("if (r.value.empty() && ShortType(r.type) != L\"String\") return RowAuthoredLiteral(r);") != std::string::npos &&
                     window.find("shown = RowAuthoredLiteral(r);") != std::string::npos &&
                     window.find("if (r.authoredKind != L\"literal\" || r.authored.empty()) return std::wstring();")
                         != std::string::npos,
                 "only missing non-String literals fall back to authored text");
    // part 2 SPLIT THIS INVARIANT, so the two halves are asserted separately now.
    //
    // THE IN-APP FLYOUT still edits a compound value through ONE comma-separated TextBox: it is a 254px
    // editor in a 400px panel and has no room for four labelled boxes. THE WINDOW PANE does not: its expansion
    // is full width, and `editKind` "fields" carries its own labels on the wire (DevToolsRead.h:83-87), so a
    // Thickness gets four boxes, a Point two and a Vector3 three from ONE editor shape rather than a branch
    // per type. A pane that kept the joined box would be the "0.000000,0.000000,0.000000" row was filed
    // on, in the surface that has the space to fix it.
    CheckToolbar(overlay.find("const std::wstring editName = L\"DevToolsSelEdit\"") != std::string::npos &&
                     overlay.find("r.fields") == std::string::npos,
                 "the in-app flyout still edits a compound value through one comma-separated TextBox");
    CheckToolbar(windowShell.find("x:Name=\"PEdFields\"") != std::string::npos &&
                     windowShell.find("x:Name=\"PFieldBoxes\"") != std::string::npos &&
                     window.find("for (size_t i = 0; i < r.fields.size(); ++i)") != std::string::npos &&
                     window.find("ReadFieldBoxes(c.path, c.row->fields.size())") != std::string::npos,
                 "the window pane draws one labelled box per wire-carried field label, and commits them joined");
    // part 2. EVERY editor lives in ONE template and exactly one is Visible, so the "exactly one kind's
    // editor is present" invariant is a single loop rather than a rule spread over five templates -- and a
    // template chosen once from the row's value shape could not have held it at all, because staging a kind
    // swaps the editor inside an expansion that is already open.
    CheckToolbar(windowShell.find("x:Name=\"TmplExpEdit\"") != std::string::npos &&
                     windowShell.find("x:Name=\"TmplExpText\"") == std::string::npos &&
                     windowShell.find("x:Name=\"TmplExpBool\"") == std::string::npos &&
                     windowShell.find("x:Name=\"TmplExpEnum\"") == std::string::npos &&
                     window.find("ShowEl(el, wcscmp(b, show) == 0);") != std::string::npos,
                 "one editable expansion template, and exactly one of its editors is shown");
    // THE LITERAL KIND'S EDITOR IS THE TAP'S editKind, not a taxonomy the window invented. DevToolsRead.h:78-88
    // derives it ONCE precisely so three surfaces cannot disagree about how a value renders, and the window
    // re-deriving "is this a bool" from the TYPE is what that comment exists to prevent.
    CheckToolbar(window.find("const std::wstring ek = r.editKind;") != std::string::npos &&
                     window.find("if (ek == L\"bool\")        { show = L\"PEdBool\";") != std::string::npos &&
                     window.find("else if (ek == L\"enum\")   { show = L\"PEdEnum\";") != std::string::npos &&
                     window.find("else if (ek == L\"color\")  { show = L\"PEdColor\";") != std::string::npos &&
                     window.find("DevToolsIsBoolType(r.type)") == std::string::npos,
                 "a Literal's editor follows the tap's editKind, not a second taxonomy in the window");
    // AN {x:Bind} ROW HAS NO EDITABLE FIELD. PXExpr is a TextBlock, so there is nothing on the row that can
    // take a keystroke -- and it is deliberately absent from the focus list, so clicking the value opens the
    // row and focuses nothing instead of landing a caret in something read-only.
    CheckToolbar(windowShell.find("<TextBlock x:Name=\"PXExpr\"") != std::string::npos &&
                     windowShell.find("<TextBox x:Name=\"PXExpr\"") == std::string::npos &&
                     windowShell.find("x:Name=\"PXBindExit\"") != std::string::npos &&
                     window.find("{ L\"PEdit\", L\"PToggle\", L\"PCombo\", L\"PHex\", L\"PResKey\", L\"PPath\" };")
                         != std::string::npos,
                 "an {x:Bind} row's expression is a TextBlock, it carries an exit, and it is not a focus target");
    // A KIND CHANGE STAGES. Selecting writes the pending kind and re-renders; only Apply/Enter commits. A
    // build that called CommitKind from the selection sink would destroy a binding on the way past it.
    CheckToolbar(window.find("PendingKindSet(ctx.path, chosen);") != std::string::npos &&
                     window.find("RebuildExpansion(ctx.rowFe, *ctx.row, ctx.path, ctx.edit);") != std::string::npos &&
                     window.find("struct KindSelectSink") != std::string::npos &&
                     window.find("CommitKind") != std::string::npos,
                 "selecting a kind stages it; commit is a separate act");
    //, THE CONSTRAINT THAT IS NOT NEGOTIABLE. Every {ThemeResource}/{StaticResource} a XAML PARSE
    // resolves costs +0.003 ms more than the last one, permanently, for the life of the process. So the pane
    // holds a chosen key as TEXT and puts the reference on the APP's element through ResolveResource. The two
    // halves are asserted together: the assignment goes through resolveFn, and the ONE thing this editor
    // parses is a literal #AARRGGBB brush for the swatch -- a colour, not a reference. Anything that fed a
    // resource extension into the pane's own markup would have to break one of them.
    CheckToolbar(window.find("g_ctx.resolveFn(g_selWire, path, key, asTheme)") != std::string::npos &&
                     window.find("LoadMarkupWin(L\"<SolidColorBrush") != std::string::npos,
                 "a chosen resource is applied through ResolveResource; the pane parses a colour, not a reference");
    // moved the editor out of the row's grid and into the expansion, so it no longer carries a
    // Grid.Column. The invariant this guards is unchanged and is the reason it is still here: BOTH property
    // editors are the same compact 12 DIP, so the pane and the in-app flyout do not disagree about how big a
    // property value is.
    CheckToolbar(windowShell.find("<TextBox x:Name=\"PEdit\" MinHeight=\"30\" FontSize=\"12\"") != std::string::npos,
                 "inspector property TextBoxes match the flyout's compact 12-DIP text");
    // And the collapsed row's own type is the same size, so expanding a row does not resize its value.
    CheckToolbar(windowShell.find("x:Name=\"PName\" Grid.Column=\"1\" FontFamily=\"Consolas\" FontSize=\"12\"") != std::string::npos &&
                     windowShell.find("x:Name=\"PValue\" FontFamily=\"Consolas\" FontSize=\"12\"") != std::string::npos,
                 "the collapsed row's name and value are the same compact 12 DIP as the editor");
    // The chevron is a real ToggleButton, so its Checked visual state painted the accent
    // fill and every expanded branch rendered a blue block. The checked brushes are re-pointed in the shell's
    // markup, on TreeRowsHost -- not at the window root, where they would also reach JmxBtn and strip the
    // accent from a toggle whose accent IS the "mode is on" affordance. Hover and pressed keep the subtle
    // fills, so the chevron still reads as a control.
    CheckToolbar(windowShell.find("<StackPanel x:Name=\"TreeRowsHost\">") != std::string::npos &&
                     windowShell.find("ToggleButtonBackgroundChecked\" ResourceKey=\"DevToolsChevronTransparent\"") != std::string::npos &&
                     windowShell.find("ToggleButtonBorderBrushChecked\" ResourceKey=\"DevToolsChevronTransparent\"") != std::string::npos &&
                     windowShell.find("ToggleButtonBackgroundCheckedPointerOver\" ResourceKey=\"SubtleFillColorSecondaryBrush\"") != std::string::npos &&
                     windowShell.find("ToggleButtonBackgroundCheckedPressed\" ResourceKey=\"SubtleFillColorTertiaryBrush\"") != std::string::npos &&
                     window.find("SetTreeChevronCheckedResources") == std::string::npos &&
                     window.find("DevToolsPutTextForeground(gtb, pal.secondary)") != std::string::npos,
                 "tree chevrons use secondary text with a transparent checked state, scoped to the rows");
    CheckToolbar(window.find("DevToolsPutFontSize(ctb, 12);") != std::string::npos &&
                     window.find("DevToolsPutFontSize(ctb, 13);") == std::string::npos,
                 "tree rows use the compact 12-DIP caption size");
    CheckToolbar(windowShell.find("<ToggleButton x:Name=\"JmxBtn\"") != std::string::npos &&
                     windowShell.find("Glyph=\"&#xE943;\"") != std::string::npos &&
                     windowShell.find("ToolTipService.ToolTip=\"{Binding Text, ElementName=JmxLbl}\"") != std::string::npos &&
                     windowShell.find("<TextBox x:Name=\"TreeFilter\"") < windowShell.find("<ToggleButton x:Name=\"JmxBtn\"") &&
                     windowShell.find("Grid.Row=\"1\" Margin=\"8,8,8,4\" ColumnSpacing=\"8\"") != std::string::npos &&
                     windowShell.find("<ToggleButton x:Name=\"JmxBtn\"") < windowShell.find("<TextBlock x:Name=\"TreeHeader\""),
                 "Just my XAML is an icon toggle eight DIPs beside the tree filter with its status in the tooltip");
    // JmxLbl is collapsed, so it is absent from the UIA tree by construction and the toggle's
    // AutomationProperties.Name is the ONLY thing that carries the unavailable reason to assistive tech.
    // Nothing guarded that binding, which is how the reason became a tooltip-only affordance once already.
    CheckToolbar(windowShell.find("AutomationProperties.Name=\"{Binding Text, ElementName=JmxLbl}\"") != std::string::npos,
                 "the Just my XAML toggle's accessible name is bound to the live reason text");
    CheckToolbar(window.find("DevToolsToggleButtonPutIsChecked(toggle, boxed)") != std::string::npos,
                 "the Just my XAML toggle reflects programmatic filter changes");

    // ---- the left pane's mode chrome -------------------------------------------------------------
    // The tabs are the ONLY mode row, and every tree-scoped control lives inside the tree tab. Two facts,
    // both of which the shipped build got wrong: the CommentsToggle icon button carried the mode in a name
    // that was byte-identical in both views, and SetCommentsView collapsed the filter TEXTBOX rather than
    // the row that also holds Just my XAML -- so the toggle floated alone over a pane it could not filter.
    CheckToolbar(windowShell.find("AutomationProperties.AutomationId=\"ViewTabTree\"") != std::string::npos &&
                     windowShell.find("AutomationProperties.AutomationId=\"ViewTabComments\"") != std::string::npos &&
                     windowShell.find("AutomationProperties.AutomationId=\"CommentsToggle\"") == std::string::npos &&
                     windowShell.find("x:Name=\"CommentsBtn\"") == std::string::npos,
                 "the view tabs replace the Comments icon toggle rather than joining it");
    CheckToolbar(windowShell.find("<RadioButton x:Name=\"TreeTabBtn\"") != std::string::npos &&
                     windowShell.find("<RadioButton x:Name=\"CommentsTabBtn\"") != std::string::npos,
                 "each tab is a RadioButton, so the showing view is reported by SelectionItemPattern");
    // Inspector and overlay share an accessibility namespace; qualify the inspector Comments tab.
    CheckToolbar(windowShell.find("AutomationProperties.Name=\"Comments (inspector)\"") != std::string::npos &&
                     windowShell.find("AutomationProperties.Name=\"Comments\"") == std::string::npos,
                 "the Comments tab's announced name is disambiguated from the overlay's own Comments action");
    // All four tree-scoped controls in ONE row, in the order the gate above still measures, and that
    // row is what SetCommentsView collapses.
    CheckToolbar(windowShell.find("<Grid x:Name=\"TreeControls\"") != std::string::npos &&
                     windowShell.find("AutomationProperties.AutomationId=\"PickElementToggle\"") >
                         windowShell.find("<Grid x:Name=\"TreeControls\"") &&
                     windowShell.find("AutomationProperties.AutomationId=\"FocusTrackingToggle\"") >
                         windowShell.find("<Grid x:Name=\"TreeControls\"") &&
                     windowShell.find("<TextBox x:Name=\"TreeFilter\"") >
                         windowShell.find("<Grid x:Name=\"TreeControls\"") &&
                     windowShell.find("<ToggleButton x:Name=\"JmxBtn\"") >
                         windowShell.find("<Grid x:Name=\"TreeControls\""),
                 "Pick, Track focus, the filter and Just my XAML all live inside the one tree-scoped row");
    CheckToolbar(windowShell.find("AutomationProperties.AutomationId=\"PickElementToggle\"") <
                     windowShell.find("<TextBox x:Name=\"TreeFilter\"") &&
                     windowShell.find("AutomationProperties.AutomationId=\"FocusTrackingToggle\"") <
                         windowShell.find("<TextBox x:Name=\"TreeFilter\""),
                 "the inspector's actions still lead that row (they used to lead a strip of their own)");
    CheckToolbar(window.find("if (g_treeControlsUi)   DevToolsPutVisibility(g_treeControlsUi, on ? 1 : 0);") != std::string::npos &&
                     window.find("grabUi(L\"TreeControls\", &g_treeControlsUi);") != std::string::npos &&
                     window.find("g_treeFilterUi") == std::string::npos,
                 "SetCommentsView collapses the whole control ROW, not just the filter TextBox inside it");
    // The pick brings its own result forward -- wired at the app-originated entry point and NOWHERE else,
    // because a comment row click reaches SelectNodeByWire by the same road and must stay in the list.
    {
        const size_t pickedAt = window.find("bool DevToolsWindow_OnPicked(");
        const size_t pickedEnd = window.find("\n}", pickedAt);
        const std::string picked = (pickedAt == std::string::npos) ? std::string()
                                                                   : window.substr(pickedAt, pickedEnd - pickedAt);
        CheckToolbar(picked.find("SetCommentsView(false);") != std::string::npos,
                     "a committed pick switches to the Visual tree tab");
        const size_t selectAt = window.find("static void SelectNodeByWire(InstanceHandle wire");
        const size_t selectEnd = window.find("\n}", selectAt);
        const std::string select = (selectAt == std::string::npos) ? std::string()
                                                                   : window.substr(selectAt, selectEnd - selectAt);
        CheckToolbar(select.find("SetCommentsView") == std::string::npos,
                     "and SelectNodeByWire does NOT, so a live-comment click stays in the comments list");
        // The filter rule. A comment click must never write g_jmxActive: the toggle is not even on
        // screen in that mode, so the flip would be a state change with no visible cause. Enforced by an
        // explicit cause the way enforced the focus rule, not by anyone remembering.
        CheckToolbar(select.find("cause == SelectCause::Explicit && RowHiddenByJmx(si)") != std::string::npos &&
                         select.find("cause == SelectCause::Comment && RowHiddenByJmx(si)") != std::string::npos,
                     "only an EXPLICIT selection may clear Just my XAML; a comment click reports instead");
        CheckToolbar(window.find("enum class SelectCause { Explicit, Focus, Comment };") != std::string::npos &&
                         window.find("SelectNodeByWire(wire, SelectCause::Comment);") != std::string::npos,
                     "and the comments path is the thing that passes that cause");
    }
    // part 2: the row carries its own state, and the actions are not permanent chrome.
    CheckToolbar(window.find("static std::wstring CommentRowAccessibleName(") != std::string::npos &&
                     window.find("AutomationProperties.Name=\\\"\" + DevToolsXmlEscape(CommentRowAccessibleName(c, live, done), true)") != std::string::npos &&
                     window.find("AutomationProperties.Name=\\\"\" + DevToolsXmlEscape(c.id)") == std::string::npos,
                 "a comment row's accessible name is the whole row, not the comment id");
    CheckToolbar(window.find("DevToolsCommentText::AbsentLabel(!c.anchor.empty())") != std::string::npos &&
                     window.find("<Run Text=\\\"   not on this screen\\\"") == std::string::npos,
                 "the not-on-this-screen marker is a leading chip, not a trailing run the ellipsis eats first");
    // Every permanent comment action needs a comment-qualified accessible name.
    CheckToolbar(window.find("x:Name=\\\"A\" + i + L\"\\\" Visibility=\\\"Collapsed\\\"") == std::string::npos &&
                     window.find("struct CommentHoverSink") == std::string::npos,
                 "the row's actions are always visible -- no Collapsed action bar, no hover reveal");
    CheckToolbar(window.find("AutomationProperties.Name=\\\"Resolve: \" + subject") != std::string::npos &&
                     window.find("AutomationProperties.Name=\\\"Open source for: \" + subject") != std::string::npos &&
                     window.find("AutomationProperties.Name=\\\"Reveal in File Explorer: \" + subject") != std::string::npos &&
                     window.find("AutomationProperties.Name=\\\"Copy path for: \" + subject") != std::string::npos,
                 "every always-on action names the comment it acts on, so N rows are not N identical names");
    // Always-VISIBLE is not always-PRESENT (part 3): the SET of actions is still the association answer.
    CheckToolbar(window.find("if (g_xamlHandlerPresent) {") != std::string::npos,
                 "which actions exist still depends on whether anything opens .xaml");
    // One writer updates total-versus-placed text without reparsing the list.
    CheckToolbar(window.find("return DevToolsCommentText::OffscreenNote(offscreen);") != std::string::npos &&
                     window.find("static std::wstring CommentsOffscreenNote(size_t offscreen)") != std::string::npos,
                 "the set-level off-screen footnote has exactly one writer");
    // Comment liveness updates subscribe to the coalesced tree signal, not per-node notifications,
    // and must not rebuild markup on every change.
    CheckToolbar(window.find("static void OnAppTreeChanged()") != std::string::npos,
                 "the coalesced tree-change handler is still where a navigation lands");
    {
        const size_t treeAt = window.find("static void OnAppTreeChanged()");
        const size_t treeEnd = (treeAt == std::string::npos) ? std::string::npos : window.find("\n}", treeAt);
        const std::string onTree = (treeAt == std::string::npos) ? std::string()
                                                                 : window.substr(treeAt, treeEnd - treeAt);
        CheckToolbar(onTree.find("RecomputeCommentLiveness();") != std::string::npos,
                     "a tree change recomputes comment liveness");
    }
    CheckToolbar(window.find("DevToolsTreeWatch_Subscribe(&OnAppTreeChanged)") != std::string::npos,
                 "and it arrives on the COALESCED tree-watch signal, not on the per-element callback");
    {
        // rfind, not find: the forward declaration comes first, and anchoring on it would extract the WRONG
        // body and make both checks below vacuously green. (The file is CRLF, so "()\n{" matches nothing.)
        const size_t recomputeAt = window.rfind("static void RecomputeCommentLiveness()");
        const size_t recomputeEnd = (recomputeAt == std::string::npos) ? std::string::npos
                                                                      : window.find("\n}", recomputeAt);
        const std::string recompute = (recomputeAt == std::string::npos) ? std::string()
                                                                        : window.substr(recomputeAt, recomputeEnd - recomputeAt);
        CheckToolbar(!recompute.empty() &&
                         recompute.find("BuildCommentRowMarkup") == std::string::npos &&
                         recompute.find("BuildCommentsFragment") == std::string::npos &&
                         recompute.find("LoadMarkupWin") == std::string::npos &&
                         recompute.find("RefreshCommentsPane") == std::string::npos,
                     "the recompute parses no markup -- it writes properties on rows that already exist");
        CheckToolbar(recompute.find("DevToolsSetAutomationName(g_apStatics, r.body") != std::string::npos,
                     "and it moves the row's ACCESSIBLE name too, not just the dot and the chip");
    }
    // part 3: ONE shell hand-off, and neither call site may keep its own ShellExecute.
    CheckToolbar(window.find("DevToolsShellOpen::Decide(") != std::string::npos &&
                     CountOf(window, "static std::wstring HandOffSource(") == 1 &&
                     CountOf(window, "HandOffSource(") == 3,
                 "the comments row and the properties pane share one shell hand-off");
    CheckToolbar(CountOf(window, "ShellExecuteW(nullptr, L\"open\", path.c_str()") == 0 &&
                     window.find("AssocQueryStringW") != std::string::npos,
                 "no call site launches the shell without asking for the association first");

    CheckToolbar(overlay.find("static EventSink g_protoSink[15];") != std::string::npos,
                 "toolbar owns independent pointer, click, resize and focus sinks");
    CheckToolbar(overlay.find("wireNamedClick(L\"DevToolsProtoPick\", g_protoSink[2], &OnPickClick);") != std::string::npos,
                 "Pick is wired by namescope identity");
    CheckToolbar(overlay.find("wireNamedClick(L\"DevToolsProtoComments\", g_protoSink[3], &OnCommentsToggleClick);") != std::string::npos,
                 "Comments is wired by namescope identity");
    CheckToolbar(overlay.find("wireNamedClick(L\"DevToolsProtoInspect\", g_protoSink[4], &OnInspectClick);") != std::string::npos,
                 "Inspect is wired by namescope identity");
    CheckToolbar(overlay.find("wireNamedClick(L\"DevToolsProtoLayout\", g_protoSink[5], &OnLayoutToggleClick);") != std::string::npos,
                 "Layout is wired by namescope identity");
    CheckToolbar(overlay.find("wireNamedClick(L\"DevToolsProtoTheme\", g_protoSink[6], &OnThemeToggleClick);") != std::string::npos,
                 "Theme is wired by namescope identity");
    CheckToolbar(overlay.find("wireNamedClick(L\"DevToolsProtoPin\", g_protoSink[7], &OnPinClick);") != std::string::npos,
                 "Pin is wired by namescope identity");
}

int RunToolbarTests()
{
    std::printf("DevTools toolbar action tests\n");
    TestThemeTarget();
    TestToolbarContract();
    return g_toolbarFailures;
}
