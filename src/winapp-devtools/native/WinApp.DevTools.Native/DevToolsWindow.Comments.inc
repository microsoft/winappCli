// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsWindow.cpp; included into that translation unit, not compiled on its own.

static DevToolsWindowCommentsFn       g_commentsFn;
static size_t                         g_commentsResolvedHidden = 0;

void DevToolsWindow_SetResolvedCommentCount(size_t resolved) { g_commentsResolvedHidden = resolved; }
static DevToolsWindowAnchorWireFn     g_anchorWireFn;
static DevToolsWindowCommentResolveFn g_commentResolveFn;
enum class InspectorView { Tree, Comments, Settings };
static InspectorView g_inspectorView = InspectorView::Tree;
static void RefreshLaunchSettings();
static IInspectable* g_commentsHostChildren = nullptr;
static void*         g_commentsScrollUi = nullptr;
static void*         g_treeControlsUi = nullptr;
// Shell controls outside the tree and properties fragments: the rail, the panel headers and the details header.
// Held for the window's lifetime and dropped in ResetCommentsViewState, like the tree's own controls.
struct InspectorShellRefs
{
    DevToolsXC::NavigationView nav{nullptr};
    DevToolsXC::NavigationViewItem treeTab{nullptr}, commentsTab{nullptr}, settingsTab{nullptr};
    DevToolsXC::InfoBadge commentsBadge{nullptr};
    DevToolsXC::TextBlock detailTitle{nullptr}, detailCrumb{nullptr};
    DevToolsX::UIElement commentsHeader{nullptr}, detailsHeader{nullptr};
    DevToolsX::UIElement showInTree{nullptr}, addComment{nullptr};
    DevToolsX::UIElement inspectorCard{nullptr}, settingsPage{nullptr}, startToolbarPreference{nullptr};
    DevToolsXC::ToggleSwitch startEnabled{nullptr}, startShown{nullptr};
    DevToolsXC::TextBlock startEnabledState{nullptr}, startShownState{nullptr};
    DevToolsXC::TextBlock settingsError{nullptr};
};
static InspectorShellRefs g_shell;
static std::function<void()> g_openCommentBox; // opens the selected element's comment box; set per fragment
static bool g_navSyncing = false;              // the rail selection is being set from code, not by the user
static std::vector<DevToolsWindowComment> g_commentRows;
// The pane is a virtualizing list: one item per file caption or comment row, built only while it is on screen.
enum class CommentItemKind { Caption, Row };
struct CommentPaneItem { CommentItemKind kind = CommentItemKind::Row; size_t row = 0; std::wstring caption; };
static std::vector<CommentPaneItem> g_commentItems;
static std::vector<int> g_commentRowItem;   // row index -> item index
static std::vector<char> g_commentRowLive;
static std::wstring g_selectedCommentId;
static void* g_commentRepeater = nullptr;
static void* g_commentsOffNoteTb = nullptr;
static void* g_commentsOffNoteUi = nullptr;
static bool g_xamlHandlerPresent = false;
static bool g_csHandlerPresent = false;
static void RefreshCommentsPane();

static void UpdateCommentSelection()
{
    for (size_t i = 0; i < g_commentRows.size(); ++i) {
        IInspectable* element = nullptr;
        if (!g_commentRepeater || i >= g_commentRowItem.size() ||
            FAILED(DevToolsRepeaterTryGetElement(g_commentRepeater, g_commentRowItem[i], &element)) || !element) continue;
        try {
            const auto row = TreeObject<DevToolsX::FrameworkElement>(element);
            const std::wstring n = std::to_wstring(i);
            const bool selected = g_commentRows[i].id == g_selectedCommentId;
            if (const auto button = row.FindName(L"K" + n).try_as<DevToolsXC::Button>())
                DevToolsXA::AutomationProperties::SetItemStatus(button, selected ? L"Selected" : L"");
            if (const auto outline = row.FindName(L"S" + n).try_as<DevToolsX::UIElement>())
                outline.Visibility(selected ? DevToolsX::Visibility::Visible : DevToolsX::Visibility::Collapsed);
        } catch (const winrt::hresult_error& e) {
            DevToolsOverlayLog(L"route1.comments selection update failed hr=0x%08X", e.code().value);
        }
        element->Release();
    }
}

static void ReleaseCommentRowUi()
{
    auto drop = [](void*& p) { if (p) { reinterpret_cast<IUnknown*>(p)->Release(); p = nullptr; } };
    drop(g_commentRepeater);
    drop(g_commentsOffNoteTb);
    drop(g_commentsOffNoteUi);
}
// Always select the comment row; additionally select its element when realized. Resolve the
// comment identity at click time rather than reuse stale handles or file positions.
static void GoToComment(size_t idx)
{
    if (idx >= g_commentRows.size()) return;
    const DevToolsWindowComment c = g_commentRows[idx];
    g_selectedCommentId = c.id;
    UpdateCommentSelection();
    SetStatusText(L"");

    InstanceHandle wire = (g_anchorWireFn && !c.anchor.empty()) ? g_anchorWireFn(c.anchor.c_str()) : 0;
    if (wire) {
        // SelectCause::Comment, not the default: this path must never write the "Just my XAML" filter, whose
        // toggle is not even on screen in this mode. SelectNodeByWire reports the collision instead.
        SelectNodeByWire(wire, SelectCause::Comment);
        return;
    }
    SetStatusText(L"That element isn't on this screen right now. " +
                  std::wstring(c.file.empty() ? L"This comment has no source file."
                                              : L"Its actions are on the row."));
}

struct CommentRowSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen) return S_OK;
        GoToComment(idx);
        return S_OK;
    }
    void Init(size_t i) { idx = i; InitSink(IID_RoutedEventHandler_Del, g_route1Gen); }
    size_t    idx = 0;
};

// Mark resolved through the CLI so the window and comment store have one writer. `done` gets the host's answer.
static void ResolveCommentById(const std::wstring& id, std::function<void(int)> done = nullptr)
{
    if (id.empty()) return;
    if (g_commentBoxSaved && g_commentBoxSaved->id == id) {
        if (g_commentBoxSaved->resolving) return;
        g_commentBoxSaved->resolving = true;
    }
    const auto generation = g_route1Gen;
    const auto answered = std::make_shared<bool>(false);
    SetStatusText(L"Resolving " + id + L"\u2026");
    const auto complete = [generation, id, answered, done](int result) {
        if (*answered) return;
        *answered = true;
        if (generation != g_route1Gen) return;
        SetStatusText(result == 0 ? L"Resolved on the host: " + id :
            result == 0x57410001 ? L"Resolved on the host; comment markers could not refresh: " + id :
            result == 0x57410002 ? L"The host comment changed after the earlier save. Refresh: " + id :
            L"Host resolution was not confirmed. Refresh before retrying: " + id);
        // Both Resolve buttons retire the matching card's identity, never an unrelated element's draft.
        if (g_commentBoxSaved && g_commentBoxSaved->id == id && g_commentBoxSaved->FinishResolve(result)) {
            if (auto box = g_commentBox.get()) box.Text(L"");
            if (g_presentCommentBox) g_presentCommentBox(false);
        }
        if (done) done(result);
    };
    if (!g_commentResolveFn || !g_commentResolveFn(id.c_str(), complete)) {
        if (!*answered) {
            complete(1);
            if (generation == g_route1Gen)
                SetStatusText(L"Resolution could not start. Finish any pending comment save and check the CLI/host connection.");
        }
    }
}

struct CommentResolveSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen) return S_OK;
        if (idx >= g_commentRows.size()) return S_OK;
        ResolveCommentById(g_commentRows[idx].id);
        return S_OK;
    }
    void Init(size_t i) { idx = i; InitSink(IID_RoutedEventHandler_Del, g_route1Gen); }
    size_t    idx = 0;
};

// "Open source" on a selected comment row: the SAME shared hand-off the properties pane uses, so the shell's
// app-picker cannot appear unannounced from either call site.
struct CommentOpenSourceSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen) return S_OK;
        if (idx >= g_commentRows.size()) return S_OK;
        const DevToolsWindowComment c = g_commentRows[idx];
        HandOffSource(c.uri, c.line, L"");
        return S_OK;
    }
    void Init(size_t i) { idx = i; InitSink(IID_RoutedEventHandler_Del, g_route1Gen); }
    size_t idx = 0;
};

// The two actions that cannot fail, offered on every row that has a resolvable file so they are the answer
// whether or not a handler exists.
struct CommentRevealSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen) return S_OK;
        if (idx >= g_commentRows.size()) return S_OK;
        const std::wstring path = ResolveSourcePath(g_commentRows[idx].uri);
        if (path.empty()) { SetStatusText(L"Source path unavailable (build from a project?)"); return S_OK; }
        SetStatusText(RevealInExplorer(path) ? L"Showed " + path + L" in File Explorer."
                                             : L"Couldn't open File Explorer for " + path);
        return S_OK;
    }
    void Init(size_t i) { idx = i; InitSink(IID_RoutedEventHandler_Del, g_route1Gen); }
    size_t idx = 0;
};

struct CommentCopyPathSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen) return S_OK;
        if (idx >= g_commentRows.size()) return S_OK;
        std::wstring path = ResolveSourcePath(g_commentRows[idx].uri);
        if (path.empty()) path = g_commentRows[idx].file;
        if (path.empty()) { SetStatusText(L"This comment has no source file to copy."); return S_OK; }
        SetStatusText(CopyTextToClipboard(path) ? L"Copied " + path : L"Couldn't copy " + path);
        return S_OK;
    }
    void Init(size_t i) { idx = i; InitSink(IID_RoutedEventHandler_Del, g_route1Gen); }
    size_t idx = 0;
};

// The live element's own source while it is on screen; otherwise the stored creation location.
static std::wstring CommentWhere(const DevToolsWindowComment& c, bool live)
{
    std::wstring file;
    unsigned int line = 0, column = 0;
    const InstanceHandle wire = live && g_ctx.sourceFn && g_anchorWireFn ? g_anchorWireFn(c.anchor.c_str()) : 0;
    if (!wire || !g_ctx.sourceFn(wire, file, line, column)) file.clear();
    return DevToolsCommentText::SourceLabel(c.file, c.line, file, line);
}

// Accessible row name includes note, target, location and current state in reading order.
static std::wstring CommentRowAccessibleName(const DevToolsWindowComment& c, bool live, bool done)
{
    std::wstring n = c.text;
    if (!c.element.empty()) n += L" \u00B7 " + c.element;
    if (const auto where = CommentWhere(c, live && !done); !where.empty()) {
        n += L", " + where;
    }
    if (done)       n += L", resolved";
    else if (!live) n += std::wstring(L", ") + DevToolsCommentText::AbsentLabel(!c.anchor.empty());
    return n;
}

// Put status before trimming text so stale/offscreen state remains visible.
static std::wstring BuildCommentRowMarkup(size_t idx, const DevToolsWindowComment& c, bool live)
{
    const bool done = (c.status == L"resolved");
    const std::wstring i = std::to_wstring(idx);
    const std::wstring where = CommentWhere(c, live && !done);
    // Qualify icon actions with their comment for assistive technology.
    const std::wstring subject = DevToolsXmlEscape(c.text.size() > 40 ? c.text.substr(0, 40) + L"\u2026" : c.text);

    std::wstring m;
    m += L"<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\""
         L" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" Margin=\"0,0,0,8\">";
    m += L"<Border x:Name=\"H" + i + L"\" Padding=\"4\" CornerRadius=\"8\" BorderThickness=\"1\""
         L" BorderBrush=\"{ThemeResource CardStrokeColorDefaultBrush}\""
         L" Background=\"{ThemeResource CardBackgroundFillColorDefaultBrush}\">";
    m += L"<Grid ColumnSpacing=\"4\"><Grid.ColumnDefinitions><ColumnDefinition Width=\"*\"/>"
         L"<ColumnDefinition Width=\"Auto\"/></Grid.ColumnDefinitions>";

    m += L"<Button x:Name=\"K" + i + L"\" HorizontalAlignment=\"Stretch\""
         L" HorizontalContentAlignment=\"Stretch\" Padding=\"8,4\" Background=\"Transparent\" BorderThickness=\"0\""
         L" AutomationProperties.ItemStatus=\"" + (c.id == g_selectedCommentId ? L"Selected" : L"") + L"\""
         L" AutomationProperties.AutomationId=\"Comment" + i + L"\"";
    m += L" AutomationProperties.Name=\"" + DevToolsXmlEscape(CommentRowAccessibleName(c, live, done), true) + L"\"";
    const std::wstring absent = DevToolsCommentText::AbsentLabel(!c.anchor.empty());
    m += L" ToolTipService.ToolTip=\"" + DevToolsXmlEscape(live ? L"Select this element"
                                                     : L"Marker unavailable: " + absent + L". Use the actions on the row") + L"\">";
    m += L"<StackPanel VerticalAlignment=\"Center\">";
    m += L"<TextBlock Text=\"" + DevToolsXmlEscape(c.text, true) + L"\" TextWrapping=\"Wrap\" MaxLines=\"2\""
         L" TextTrimming=\"CharacterEllipsis\" FontSize=\"13\"";
    m += done ? L" TextDecorations=\"Strikethrough\" Foreground=\"{ThemeResource TextFillColorTertiaryBrush}\"/>"
              : L" Foreground=\"{ThemeResource TextFillColorPrimaryBrush}\"/>";
    m += L"<Grid Margin=\"0,3,0,0\"><Grid.ColumnDefinitions><ColumnDefinition Width=\"Auto\"/>"
         L"<ColumnDefinition Width=\"*\"/></Grid.ColumnDefinitions>";
    if (!done) {
        m += L"<Border x:Name=\"P" + i + L"\" CornerRadius=\"3\" Padding=\"5,1\" Margin=\"0,0,6,0\" BorderThickness=\"1\""
             L" BorderBrush=\"{ThemeResource ControlStrokeColorDefaultBrush}\""
             L" Background=\"{ThemeResource ControlFillColorSecondaryBrush}\" VerticalAlignment=\"Center\""
             L" Visibility=\"" + (live ? L"Collapsed" : L"Visible") + L"\">"
             L"<TextBlock Text=\"" + absent + L"\" FontSize=\"10\""
             DT_FG_SECONDARY L"/></Border>";
    }
    m += L"<TextBlock Grid.Column=\"1\" FontSize=\"11\" VerticalAlignment=\"Center\" TextTrimming=\"CharacterEllipsis\">";
    if (!c.element.empty()) {
        m += L"<Run Text=\"" + DevToolsXmlEscape(c.element) + L"\"" DT_FG_SECONDARY L"/>";
        m += L"<Run Text=\"   \"/>";
    }
    if (!where.empty()) {
        m += L"<Run Text=\"" + DevToolsXmlEscape(where) + L"\"" DT_FG_SECONDARY L"/>";
    }
    m += L"</TextBlock></Grid></StackPanel></Button>";

    m += L"<StackPanel x:Name=\"A" + i + L"\" Grid.Column=\"1\" Orientation=\"Vertical\""
         L" VerticalAlignment=\"Center\" Margin=\"2,0\" Spacing=\"0\">";
    if (!c.uri.empty()) {
        const bool csharp = IsCSharpPath(c.uri);
        const std::wstring extension = csharp ? L".cs" : L".xaml";
        if (csharp ? g_csHandlerPresent : g_xamlHandlerPresent) {
            m += L"<HyperlinkButton x:Name=\"O" + i + L"\" Padding=\"6\" MinWidth=\"28\" MinHeight=\"28\""
                 L" AutomationProperties.AutomationId=\"CommentOpenSource" + i + L"\""
                 L" AutomationProperties.Name=\"Open source for: " + subject + L"\""
                 L" ToolTipService.ToolTip=\"Open source\">"
                 L"<FontIcon Glyph=\"&#xE8A5;\" FontSize=\"14\"/></HyperlinkButton>";
        } else {
            m += L"<HyperlinkButton x:Name=\"E" + i + L"\" Padding=\"6\" MinWidth=\"28\" MinHeight=\"28\""
                 L" AutomationProperties.Name=\"Show in File Explorer: " + subject + L"\""
                 L" AutomationProperties.AutomationId=\"CommentReveal" + i + L"\""
                L" ToolTipService.ToolTip=\"Nothing on this machine opens " + extension + L" files. Show it in File Explorer instead\">"
                 L"<FontIcon Glyph=\"&#xE838;\" FontSize=\"14\"/></HyperlinkButton>";
            m += L"<HyperlinkButton x:Name=\"C" + i + L"\" Padding=\"6\" MinWidth=\"28\" MinHeight=\"28\""
                 L" AutomationProperties.AutomationId=\"CommentCopyPath" + i + L"\""
                 L" AutomationProperties.Name=\"Copy path for: " + subject + L"\""
                 L" ToolTipService.ToolTip=\"Copy path\">"
                 L"<FontIcon Glyph=\"&#xE8C8;\" FontSize=\"14\"/></HyperlinkButton>";
        }
    }
    if (!done && g_commentResolveFn) {
        m += L"<HyperlinkButton x:Name=\"R" + i + L"\" Padding=\"6\" MinWidth=\"28\" MinHeight=\"28\""
             L" AutomationProperties.AutomationId=\"CommentResolve" + i + L"\""
             L" AutomationProperties.Name=\"Resolve: " + subject + L"\""
             L" ToolTipService.ToolTip=\"Resolve comment\">"
             L"<FontIcon Glyph=\"&#xE73E;\" FontSize=\"14\"/></HyperlinkButton>";
    }
    m += L"</StackPanel>";
    m += L"</Grid></Border><Border x:Name=\"S" + i + L"\" CornerRadius=\"8\" BorderThickness=\"2\""
         L" BorderBrush=\"{ThemeResource AccentFillColorDefaultBrush}\" IsHitTestVisible=\"False\""
         L" Visibility=\"" + (c.id == g_selectedCommentId ? L"Visible" : L"Collapsed") + L"\"/></Grid>";
    return m;
}

static std::wstring CommentCaptionMarkup(const std::wstring& text)
{
    return L"<TextBlock xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Text=\"" + DevToolsXmlEscape(text) +
        L"\" Style=\"{StaticResource CaptionTextBlockStyle}\"" DT_FG_SECONDARY
        L" CharacterSpacing=\"60\" Margin=\"4,0,4,6\" AutomationProperties.HeadingLevel=\"Level2\"/>";
}

// Fills the list model (captions grouped by file, then resolved comments) and returns the pane around it.
static std::wstring BuildCommentsFragment(const std::vector<DevToolsWindowComment>& all, size_t& outOpen)
{
    std::wstring m;
    m += L"<StackPanel xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\""
         L" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" Margin=\"4\">";

    g_commentRows.clear();
    g_commentRowLive.clear();
    g_commentRowItem.clear();
    g_commentItems.clear();
    g_xamlHandlerPresent = !ShellAssocFor(L".xaml").empty();
    g_csHandlerPresent = !ShellAssocFor(L".cs").empty();
    size_t open = 0;
    for (const auto& c : all) if (c.status != L"resolved") ++open;

    if (all.empty()) {
        const std::wstring emptyMessage = g_commentsResolvedHidden > 0
            ? L"No open comments (" + std::to_wstring(g_commentsResolvedHidden) + L" resolved). "
              L"See them all with &#x2018;winapp devtools comments list --all&#x2019;."
            : g_commentResolveFn
            ? L"No comments for this app yet. Pick an element and use the Comment box on its panel; "
              L"they persist in the host project store. From your host project, an agent can read them with "
              L"&#x2018;winapp devtools comments list --status open --json&#x2019;."
            : L"No comments for this app yet. Restart the target so winapp can attach it writable, then add one.";
        m += L"<TextBlock TextWrapping=\"Wrap\" Margin=\"4,10,4,0\"" DT_FG_SECONDARY
             L" Text=\"" + emptyMessage + L"\"/>";
        m += L"</StackPanel>";
        outOpen = 0;
        return m;
    }

    auto addRow = [&](const DevToolsWindowComment& c) {
        const bool live = c.status != L"resolved" && g_anchorWireFn && !c.anchor.empty() &&
            g_anchorWireFn(c.anchor.c_str()) != 0;
        g_commentRowItem.push_back(static_cast<int>(g_commentItems.size()));
        g_commentItems.push_back({ CommentItemKind::Row, g_commentRows.size(), L"" });
        g_commentRows.push_back(c);
        // A C# comment has no ms-appx URI; its row actions use the file's full path, which they accept directly.
        if (auto& added = g_commentRows.back(); added.uri.empty()) added.uri = ResolveProjectCSharpFile(added.file);
        g_commentRowLive.push_back(live ? 1 : 0);
    };
    std::vector<std::wstring> files;
    for (const auto& c : all) {
        if (c.status == L"resolved") continue;
        const std::wstring key = c.file.empty() ? std::wstring(L"(source not resolved)") : c.file;
        if (std::find(files.begin(), files.end(), key) == files.end()) files.push_back(key);
    }
    for (const auto& f : files) {
        g_commentItems.push_back({ CommentItemKind::Caption, 0, f });
        for (const auto& c : all) {
            if (c.status == L"resolved") continue;
            if ((c.file.empty() ? std::wstring(L"(source not resolved)") : c.file) == f) addRow(c);
        }
    }
    size_t resolved = 0;
    for (const auto& c : all) if (c.status == L"resolved") ++resolved;
    if (resolved) {
        g_commentItems.push_back({ CommentItemKind::Caption, 0, L"RESOLVED (" + std::to_wstring(resolved) + L")" });
        for (const auto& c : all) if (c.status == L"resolved") addRow(c);
    }

    m += L"<ItemsRepeater x:Name=\"CommentRepeater\" TabFocusNavigation=\"Local\""
         L" AutomationProperties.AutomationId=\"WinAppDevToolsCommentList\" AutomationProperties.Name=\"Comments\">"
         L"<ItemsRepeater.Layout><StackLayout/></ItemsRepeater.Layout></ItemsRepeater>";
    m += L"<TextBlock x:Name=\"OffNote\" FontSize=\"11\" TextWrapping=\"Wrap\" Margin=\"4,12,4,0\""
         L" Foreground=\"{ThemeResource TextFillColorTertiaryBrush}\" Visibility=\"Collapsed\"/>";
    m += L"</StackPanel>";
    outOpen = open;
    return m;
}

// Each row's actions, created once per refresh and reused whenever the list builds the row again.
struct CommentRowSinks { DevToolsSinkBase* sinks[5] = {}; };
static std::vector<CommentRowSinks> g_commentRowSinks;

static void WireCommentRow(void* rowFe, size_t row)
{
    if (row >= g_commentRowSinks.size()) g_commentRowSinks.resize(g_commentRows.size());
    CommentRowSinks& s = g_commentRowSinks[row];
    if (!s.sinks[0]) {
        auto* select = new CommentRowSink();        select->Init(row);  s.sinks[0] = select;
        auto* resolve = new CommentResolveSink();   resolve->Init(row); s.sinks[1] = resolve;
        auto* open = new CommentOpenSourceSink();   open->Init(row);    s.sinks[2] = open;
        auto* reveal = new CommentRevealSink();     reveal->Init(row);  s.sinks[3] = reveal;
        auto* copy = new CommentCopyPathSink();     copy->Init(row);    s.sinks[4] = copy;
    }
    const std::wstring n = std::to_wstring(row);
    const wchar_t* names[5] = { L"K", L"R", L"O", L"E", L"C" };
    for (int k = 0; k < 5; ++k) {
        IInspectable* found = FindInRow(rowFe, (names[k] + n).c_str());
        if (!found) continue;
        void* bb = nullptr;
        if (SUCCEEDED(found->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), &bb)) && bb) {
            __int64 tok = 0; DevToolsAddClick(bb, s.sinks[k], &tok);
            reinterpret_cast<IUnknown*>(bb)->Release();
        }
        found->Release();
    }
}

struct CommentItemFactory : DevToolsElementFactoryBase
{
    // Rows are not pooled: each is built for its own comment and its sinks know that comment's index.
    HRESULT STDMETHODCALLTYPE GetElement(void* args, void** element) noexcept override
    {
        if (element) *element = nullptr;
        if (!args || !element) return E_POINTER;
        IInspectable* data = nullptr;
        if (FAILED(DevToolsGetArgsData(args, &data)) || !data) return E_FAIL;
        int idx = -1;
        const HRESULT hrU = DevToolsUnboxInt32(data, &idx);
        data->Release();
        if (FAILED(hrU) || idx < 0 || static_cast<size_t>(idx) >= g_commentItems.size()) return E_FAIL;
        const CommentPaneItem& item = g_commentItems[static_cast<size_t>(idx)];
        const bool row = item.kind == CommentItemKind::Row && item.row < g_commentRows.size();
        IInspectable* built = nullptr;
        if (FAILED(LoadMarkupWin(row ? BuildCommentRowMarkup(item.row, g_commentRows[item.row], g_commentRowLive[item.row] != 0)
                                     : CommentCaptionMarkup(item.caption), &built)) || !built) {
            DevToolsOverlayLog(L"route1.comments item %d did not build", idx);
            return E_FAIL;
        }
        if (row) {
            void* fe = nullptr;
            if (SUCCEEDED(built->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) && fe) {
                WireCommentRow(fe, item.row);
                reinterpret_cast<IUnknown*>(fe)->Release();
            }
        }
        const HRESULT hr = built->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), element);
        built->Release();
        return hr;
    }
    HRESULT STDMETHODCALLTYPE RecycleElement(void*) noexcept override { return S_OK; }
};
static CommentItemFactory g_commentItemFactory;
// The footnote's text for a given off-screen count. One writer, used by the initial render and by every
// recompute, so the two cannot drift.
static std::wstring CommentsOffscreenNote(size_t offscreen)
{
    return DevToolsCommentText::OffscreenNote(offscreen);
}

// Recompute anchor liveness after coalesced tree changes. Rows on screen are updated in place; rows the list has not
// built pick up the new state when it builds them. Run on the later UI dispatcher turn, not inside OnVisualTreeChange.
static void RecomputeCommentLiveness()
{
    if (g_inspectorView != InspectorView::Comments || g_commentRows.empty()) return;
    WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::WinCommentLive);
    size_t offscreen = 0, changed = 0;
    for (size_t i = 0; i < g_commentRows.size() && i < g_commentRowLive.size(); ++i) {
        const DevToolsWindowComment& c = g_commentRows[i];
        if (c.status == L"resolved") continue;
        // Ask. A handle that resolved a moment ago says nothing about now, which is the whole reason the
        // tree-change notification exists.
        const bool live = g_anchorWireFn && !c.anchor.empty() && g_anchorWireFn(c.anchor.c_str()) != 0;
        if (!live && !c.anchor.empty()) ++offscreen;
        if (live == (g_commentRowLive[i] != 0)) continue;
        g_commentRowLive[i] = live ? 1 : 0;
        ++changed;
        IInspectable* element = nullptr;
        if (!g_commentRepeater || i >= g_commentRowItem.size() ||
            FAILED(DevToolsRepeaterTryGetElement(g_commentRepeater, g_commentRowItem[i], &element)) || !element) continue;
        void* row = nullptr;
        if (SUCCEEDED(element->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &row)) && row) {
            const std::wstring n = std::to_wstring(i);
            auto show = [&](const wchar_t* name, bool visible) {
                if (IInspectable* part = FindInRow(row, (name + n).c_str())) {
                    void* ui = nullptr;
                    if (SUCCEEDED(part->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) && ui) {
                        DevToolsPutVisibility(ui, visible ? 0 : 1);
                        reinterpret_cast<IUnknown*>(ui)->Release();
                    }
                    part->Release();
                }
            };
            show(L"P", !live);
            if (IInspectable* body = FindInRow(row, (L"K" + n).c_str())) {
                HSTRING h = MakeStr(CommentRowAccessibleName(c, live, false));
                DevToolsSetAutomationName(g_apStatics, body, h);
                WindowsDeleteString(h);
                body->Release();
            }
            reinterpret_cast<IUnknown*>(row)->Release();
        }
        element->Release();
    }
    if (g_commentsOffNoteTb && g_commentsOffNoteUi) {
        const std::wstring note = CommentsOffscreenNote(offscreen);
        HSTRING h = MakeStr(note);
        DevToolsPutText(g_commentsOffNoteTb, h);
        WindowsDeleteString(h);
        DevToolsPutVisibility(g_commentsOffNoteUi, note.empty() ? 1 : 0);
    }
    if (changed)
        DevToolsOverlayLog(L"route1.comments liveness recomputed: %zu row(s) changed, %zu off screen", changed, offscreen);
}

static size_t CountOpenComments()
{
    std::vector<DevToolsWindowComment> all;
    if (g_commentsFn) g_commentsFn(&all);
    size_t open = 0;
    for (const auto& c : all) if (c.status != L"resolved") ++open;
    return open;
}

// The rail badge shows how many comments are open.
static void UpdateCommentsCount(size_t open)
{
    try {
        if (const auto& badge = g_shell.commentsBadge) {
            badge.Value(static_cast<int32_t>(open));
            badge.Visibility(open > 0 ? DevToolsX::Visibility::Visible : DevToolsX::Visibility::Collapsed);
        }
        if (const auto& tab = g_shell.commentsTab)
            DevToolsXA::AutomationProperties::SetItemStatus(tab, std::to_wstring(open) + L" open");
    } catch (const winrt::hresult_error& e) {
        DevToolsOverlayLog(L"route1.comments count update failed hr=0x%08X", e.code().value);
    }
}

static void RefreshCommentsPane()
{
    WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::WinComments);
    if (!g_commentsHostChildren) return;
    const ULONGLONG began = GetTickCount64();

    std::vector<DevToolsWindowComment> all;
    if (g_commentsFn) g_commentsFn(&all);

    size_t open = 0;
    IInspectable* frag = nullptr;
    ReleaseCommentRowUi();
    g_commentRowSinks.clear();
    if (FAILED(LoadMarkupWin(BuildCommentsFragment(all, open), &frag)) || !frag) {
        DevToolsOverlayLog(L"route1.comments fragment load failed");
        DevToolsVecClear(g_commentsHostChildren);
        g_commentRows.clear();
        g_commentRowLive.clear();
        g_commentItems.clear();
        g_commentRowItem.clear();
        return;
    }

    void* fe = nullptr;
    if (SUCCEEDED(frag->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) && fe) {
        auto hold = [&](const wchar_t* name, const GUID& iid, void** out) {
            if (IInspectable* found = FindInRow(fe, name)) { found->QueryInterface(iid, out); found->Release(); }
        };
        hold(L"CommentRepeater", DevToolsIid<DevToolsXC::IItemsRepeater>(), &g_commentRepeater);
        hold(L"OffNote", DevToolsIid<DevToolsXC::ITextBlock>(), &g_commentsOffNoteTb);
        hold(L"OffNote", DevToolsIid<DevToolsX::IUIElement>(),  &g_commentsOffNoteUi);
        reinterpret_cast<IUnknown*>(fe)->Release();
    }
    if (g_commentRepeater) {
        HRESULT hr = DevToolsRepeaterPutItemTemplate(g_commentRepeater, static_cast<DevToolsElementFactoryBase*>(&g_commentItemFactory));
        if (SUCCEEDED(hr)) {
            try {
                auto items = winrt::single_threaded_vector<winrt::Windows::Foundation::IInspectable>();
                for (size_t i = 0; i < g_commentItems.size(); ++i) items.Append(winrt::box_value(static_cast<int32_t>(i)));
                hr = DevToolsRepeaterPutItemsSource(g_commentRepeater, reinterpret_cast<IInspectable*>(winrt::get_abi(items)));
            } catch (const winrt::hresult_error& e) { hr = e.code(); }
        }
        if (FAILED(hr)) DevToolsOverlayLog(L"route1.comments list setup FAILED hr=0x%08X -- the pane will show no comments", hr);
    } else if (!g_commentItems.empty()) {
        DevToolsOverlayLog(L"route1.comments CommentRepeater not found -- the pane will show no comments");
    }

    void* ui = nullptr;
    if (SUCCEEDED(frag->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) && ui) {
        DevToolsVecClear(g_commentsHostChildren);
        DevToolsVecAppend(g_commentsHostChildren, reinterpret_cast<IInspectable*>(ui));
        reinterpret_cast<IUnknown*>(ui)->Release();
    }
    frag->Release();

    RecomputeCommentLiveness();

    UpdateCommentsCount(open);
    DevToolsOverlayLog(L"route1.comments rendered rows=%zu open=%zu ms=%llu", g_commentRows.size(), open, GetTickCount64() - began);
}
// Tree-scoped controls collapse with the tree; comments view must not leave filters/toggles acting on nothing.
static void SetInspectorView(InspectorView view)
{
    g_inspectorView = view;
    const bool on = view == InspectorView::Comments;
    if (g_treeScrollUi)     DevToolsPutVisibility(g_treeScrollUi, on ? 1 : 0);
    if (g_commentsScrollUi) DevToolsPutVisibility(g_commentsScrollUi, on ? 0 : 1);
    if (g_treeControlsUi)   DevToolsPutVisibility(g_treeControlsUi, on ? 1 : 0);
    try {
        const auto shown = [](bool v) { return v ? DevToolsX::Visibility::Visible : DevToolsX::Visibility::Collapsed; };
        if (const auto& card = g_shell.inspectorCard) card.Visibility(shown(view != InspectorView::Settings));
        if (const auto& page = g_shell.settingsPage) page.Visibility(shown(view == InspectorView::Settings));
        if (const auto& header = g_shell.commentsHeader) header.Visibility(shown(on));
        if (const auto& button = g_shell.showInTree)     button.Visibility(shown(on && g_selWire != 0));
        // Keep the rail in step when code switches the view (picking an element returns to the tree).
        const auto& nav = g_shell.nav;
        const auto& item = view == InspectorView::Settings ? g_shell.settingsTab : on ? g_shell.commentsTab : g_shell.treeTab;
        if (nav && item && nav.SelectedItem() != item) {
            g_navSyncing = true;
            nav.SelectedItem(item);
            g_navSyncing = false;
        }
    } catch (const winrt::hresult_error& e) {
        g_navSyncing = false;
        DevToolsOverlayLog(L"route1.comments view switch failed hr=0x%08X", e.code().value);
    }
    if (on) RefreshCommentsPane();
    if (view == InspectorView::Settings) RefreshLaunchSettings();
}

// A selection made to keep the rail in step is not a user choice.
static void OnInspectorRailSelection(InspectorView view)
{
    if (g_navSyncing || view == g_inspectorView) return;
    SetInspectorView(view);
}

// The details header's title: `Button #SaveButton`, or for a Reactor element its component or element kind first
// (`<Counter>  ·  Border`), named by its x:Name or the C# variable it was assigned to.
static std::wstring DetailsTitleText(const std::wstring& type, const std::wstring& name, const std::wstring& label)
{
    std::wstring text = type.empty() ? label : ShortType(type);
    if (!g_selReactorLabel.empty()) text = g_selReactorLabel + L"  \u00B7  " + text;
    if (!name.empty()) text += L" #" + name;
    else if (!g_selTreeName.empty()) text += L" #" + g_selTreeName;
    return text;
}

// The details header does not scroll: the element's title, its two nearest ancestors (the whole path is the
// tooltip), the Comment button, and "Show in tree" while the Comments panel is open.
static void UpdateDetailsHeader(const std::wstring& type, const std::wstring& name,
                                const std::wstring& label, const std::wstring& breadcrumb)
{
    try {
        const auto shown = [](bool v) { return v ? DevToolsX::Visibility::Visible : DevToolsX::Visibility::Collapsed; };
        const bool selected = g_selWire != 0;
        if (const auto& header = g_shell.detailsHeader) header.Visibility(shown(selected));
        if (const auto& title = g_shell.detailTitle) title.Text(DetailsTitleText(type, name, label));
        if (const auto& crumb = g_shell.detailCrumb) {
            size_t segments = 0;
            crumb.Text(ElideBreadcrumb(breadcrumb, segments));
            crumb.Visibility(shown(!breadcrumb.empty()));
            DevToolsXC::ToolTipService::SetToolTip(crumb, breadcrumb.empty() ? nullptr : winrt::box_value(breadcrumb));
            DevToolsXA::AutomationProperties::SetName(crumb, breadcrumb.empty() ? std::wstring() : L"Ancestors: " + breadcrumb);
        }
        if (const auto& button = g_shell.addComment) button.Visibility(shown(selected && g_ctx.writeFn));
        if (const auto& button = g_shell.showInTree)     button.Visibility(shown(selected && g_inspectorView == InspectorView::Comments));
    } catch (const winrt::hresult_error& e) {
        DevToolsOverlayLog(L"route1.props details header update failed hr=0x%08X", e.code().value);
    }
}

// The element's open comment, found the way the Comments pane finds a comment's element, so the inspector edits
// that comment rather than starting a second one.
static bool SelectedElementComment(InstanceHandle wire, DevToolsWindowComment* out)
{
    std::vector<DevToolsWindowComment> all;
    if (!wire || !out || !g_commentsFn || !g_anchorWireFn || !g_commentsFn(&all)) return false;
    for (auto& c : all) {
        if (c.status == L"resolved" || c.status == L"dismissed" || c.anchor.empty()) continue;
        if (g_anchorWireFn(c.anchor.c_str()) == wire) { *out = std::move(c); return true; }
    }
    return false;
}

// The details header's Comment button opens a box under Source, already holding the element's comment if it has
// one. A saved comment shows as a card with Edit and Resolve instead of the box. Enter saves through the in-app
// comment writer, Shift+Enter starts a new line and Esc closes the box.
static unsigned g_addCommentGen = 0;
static void WireAddComment(void* frameworkElement)
{
    g_openCommentBox = nullptr;
    g_presentCommentBox = nullptr;
    try {
        winrt::Windows::Foundation::IInspectable object{ nullptr };
        winrt::copy_from_abi(object, frameworkElement);
        auto root = object.as<DevToolsX::FrameworkElement>();
        auto box = root.FindName(L"AddCommentBox").try_as<DevToolsX::UIElement>();
        auto text = root.FindName(L"AddCommentText").try_as<DevToolsXC::TextBox>();
        auto status = root.FindName(L"AddCommentStatus").try_as<DevToolsXC::TextBlock>();
        if (!box || !text || !status) return;
        auto card = root.FindName(L"CommentCard").try_as<DevToolsX::UIElement>();
        auto cardText = root.FindName(L"CommentCardText").try_as<DevToolsXC::TextBlock>();
        auto cardMeta = root.FindName(L"CommentCardMeta").try_as<DevToolsXC::TextBlock>();
        auto cardEdit = root.FindName(L"CommentCardEdit").try_as<DevToolsXCP::ButtonBase>();
        auto cardResolve = root.FindName(L"CommentCardResolve").try_as<DevToolsXCP::ButtonBase>();
        const unsigned gen = ++g_addCommentGen;
        auto saved = std::make_shared<CommentBoxSaved>();
        // The handlers below live on elements of this fragment, so they hold its elements weakly. A strong
        // capture would form a cycle XAML cannot collect and keep every replaced pane alive.
        const winrt::weak_ref<DevToolsX::UIElement> weakBox{ box }, weakCard{ card };
        const winrt::weak_ref<DevToolsXC::TextBlock> weakCardText{ cardText }, weakCardMeta{ cardMeta };
        const winrt::weak_ref<DevToolsXC::TextBox> weakText{ text };
        const winrt::weak_ref<DevToolsXC::TextBlock> weakStatus{ status };
        const bool hasCard = static_cast<bool>(card);
        g_commentBoxParts = { box, text, status };
        if (card) g_commentBoxParts.push_back(card);
        if (cardText) g_commentBoxParts.push_back(cardText);
        if (cardMeta) g_commentBoxParts.push_back(cardMeta);
        // A saved comment shows as the card; the box shows while editing (or, without a card, whenever there is one).
        auto present = [weakBox, weakCard, weakCardText, weakCardMeta, hasCard, saved](bool editing) {
            auto box = weakBox.get();
            if (!box) return;
            auto card = weakCard.get();
            auto cardText = weakCardText.get();
            auto cardMeta = weakCardMeta.get();
            saved->editing = editing;
            const bool exists = !saved->id.empty() && !saved->text.empty();
            const bool asCard = card && exists && !editing;
            if (asCard) {
                if (cardText) cardText.Text(saved->text);
                if (cardMeta) cardMeta.Text(L"Open \u00B7 " + saved->id);
            }
            if (card) card.Visibility(asCard ? DevToolsX::Visibility::Visible : DevToolsX::Visibility::Collapsed);
            box.Visibility(editing || (exists && !hasCard) ? DevToolsX::Visibility::Visible : DevToolsX::Visibility::Collapsed);
        };
        DevToolsWindowComment existing;
        if (SelectedElementComment(g_selWire, &existing)) {
            saved->id = existing.id;
            saved->text = existing.text;
            text.Text(existing.text);
        }
        g_commentBox = weakText;
        g_commentBoxSaved = saved;
        g_presentCommentBox = present;
        if (auto it = g_selWire ? g_commentDrafts.find(g_selWire) : g_commentDrafts.end(); it != g_commentDrafts.end()) {
            text.Text(it->second);
            status.Text(L"Your unsaved comment is back. Press Enter to save it, or Esc to discard it.");
            present(true);
            g_commentDrafts.erase(it);
        } else {
            present(false);
        }
        g_openCommentBox = [present, weakText]() {
            present(true);
            if (auto box = weakText.get()) box.Focus(DevToolsX::FocusState::Programmatic);
        };
        if (cardEdit) {
            cardEdit.Click([present, weakText](auto&&, auto&&) {
                present(true);
                if (auto text = weakText.get()) text.Focus(DevToolsX::FocusState::Programmatic);
            });
        }
        if (cardResolve) {
            cardResolve.Click([weakText, saved](auto&&, auto&&) {
                auto text = weakText.get();
                if (!text || saved->id.empty() || saved->resolving || text.IsReadOnly()) return;
                ResolveCommentById(saved->id);
            });
        }
        text.PreviewKeyDown([present, weakText, weakStatus, gen, saved](auto&&, DevToolsXI::KeyRoutedEventArgs const& args) {
            using winrt::Windows::System::VirtualKey;
            auto text = weakText.get();
            auto status = weakStatus.get();
            if (!text || !status) return;
            if (args.Key() == VirtualKey::Escape) {
                args.Handled(true);
                text.Text(saved->text);
                status.Text(kAddCommentHint);
                present(false);
                return;
            }
            if (args.Key() != VirtualKey::Enter || (GetKeyState(VK_SHIFT) & 0x8000)) return;
            args.Handled(true);
            const std::wstring value(text.Text());
            if (saved->resolving || text.IsReadOnly() || value.find_first_not_of(L" \t\r\n") == std::wstring::npos) return;
            IInspectable* element = g_elementFn ? g_elementFn(g_selWire) : nullptr;
            text.IsReadOnly(true);
            status.Text(L"Saving\u2026");
            saved->id = DevToolsOverlay_AddComment(element, value, saved->id,
                [weakText, weakStatus, present, gen, saved, value](int code) {
                auto savedText = weakText.get();
                auto savedStatus = weakStatus.get();
                if (!savedText || !savedStatus || gen != g_addCommentGen) return;
                savedText.IsReadOnly(false);
                if (code != 0) {
                    savedStatus.Text(L"Couldn't save the comment. Try again.");
                    return;
                }
                saved->text = value;
                savedStatus.Text(kAddCommentHint);
                present(false);
                SetStatusText(L"Comment saved.");
            });
            if (element) element->Release();
        });
    } catch (const winrt::hresult_error& e) {
        DevToolsOverlayLog(L"route1.props comment box wiring failed hr=0x%08X", e.code().value);
    }
}