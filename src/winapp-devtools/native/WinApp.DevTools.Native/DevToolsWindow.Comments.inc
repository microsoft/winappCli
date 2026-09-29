// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsWindow.cpp; included into that translation unit, not compiled on its own.

static DevToolsWindowCommentsFn       g_commentsFn;
static DevToolsWindowAnchorWireFn     g_anchorWireFn;
static DevToolsWindowCommentResolveFn g_commentResolveFn;
static bool          g_commentsView = false;
static IInspectable* g_commentsHostChildren = nullptr;
static void*         g_commentsScrollUi = nullptr;
static void*         g_treeControlsUi = nullptr;
static void*         g_commentsLbl = nullptr;
static void*         g_commentsBadgeUi = nullptr;
static void*         g_treeTabBtn = nullptr;
static void*         g_commentsTabBtn = nullptr;
static std::vector<DevToolsWindowComment> g_commentRows;
struct CommentRowUi
{
    void* actions = nullptr;
    void* dotLive = nullptr;
    void* dotDead = nullptr;
    void* chip    = nullptr;
    void* body    = nullptr;
    bool  done    = false;
    bool  live    = false;
};
static std::vector<CommentRowUi> g_commentRowUi;
static std::vector<char> g_commentRowLive;
static void* g_commentsOffNoteTb = nullptr;
static void* g_commentsOffNoteUi = nullptr;
static bool g_xamlHandlerPresent = false;

static void RefreshCommentsPane();

static void ReleaseCommentRowUi()
{
    auto drop = [](void*& p) { if (p) { reinterpret_cast<IUnknown*>(p)->Release(); p = nullptr; } };
    for (CommentRowUi& r : g_commentRowUi) {
        drop(r.actions); drop(r.dotLive); drop(r.dotDead); drop(r.chip); drop(r.body);
    }
    g_commentRowUi.clear();
    drop(g_commentsOffNoteTb);
    drop(g_commentsOffNoteUi);
}

// Always select the comment row; additionally select its element when realized. Resolve the
// comment identity at click time rather than reuse stale handles or file positions.
static void GoToComment(size_t idx)
{
    if (idx >= g_commentRows.size()) return;
    const DevToolsWindowComment c = g_commentRows[idx];

    InstanceHandle wire = (g_anchorWireFn && !c.anchor.empty()) ? g_anchorWireFn(c.anchor.c_str()) : 0;
    if (wire) {
        // SelectCause::Comment, not the default: this path must never write the "Just my XAML" filter, whose
        // toggle is not even on screen in this mode. SelectNodeByWire reports the collision instead.
        SelectNodeByWire(wire, SelectCause::Comment);
        SetStatusText(L"Selected the element for " + c.id);
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

// Mark resolved through the CLI so the window and comment store have one writer.
struct CommentResolveSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen) return S_OK;
        if (idx >= g_commentRows.size()) return S_OK;
        const std::wstring id = g_commentRows[idx].id;
        const auto generation = g_route1Gen;
        const auto answered = std::make_shared<bool>(false);
        SetStatusText(L"Resolving " + id + L"\u2026");
        if (!g_commentResolveFn || !g_commentResolveFn(id.c_str(), [generation, id, answered](int result) {
            *answered = true;
            if (generation != g_route1Gen) return;
            SetStatusText(result == 0 ? L"Resolved on the host: " + id :
                result == 0x57410001 ? L"Resolved on the host; comment markers could not refresh: " + id :
                result == 0x57410002 ? L"The host comment changed after the earlier save. Refresh: " + id :
                L"Host resolution was not confirmed. Refresh before retrying: " + id);
        })) {
            if (!*answered) SetStatusText(L"Resolution could not start. Finish any pending comment save and check the CLI/host connection.");
            return S_OK;
        }
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
        HandOffSource(c.uri, c.line, L"That element isn't on this screen right now");
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

// Accessible row name includes note, target, location and current state in reading order.
static std::wstring CommentRowAccessibleName(const DevToolsWindowComment& c, bool live, bool done)
{
    std::wstring n = c.text;
    if (!c.element.empty()) n += L" \u00B7 " + c.element;
    if (!c.file.empty()) {
        n += L", " + DevToolsCommentText::SourceLabel(c.file, c.line);
    }
    if (done)       n += L", resolved";
    else if (!live) n += std::wstring(L", ") + DevToolsCommentText::AbsentLabel(!c.anchor.empty());
    return n;
}

// Put status and live-anchor chips before trimming text so stale/offscreen state remains visible.
static std::wstring BuildCommentRowMarkup(size_t idx, const DevToolsWindowComment& c, bool live)
{
    const bool done = (c.status == L"resolved");
    const std::wstring i = std::to_wstring(idx);
    const std::wstring where = DevToolsCommentText::SourceLabel(c.file, c.line);
    // Qualify every action's accessible name with its comment; the visible label stays the short verb.
    const std::wstring subject = DevToolsXmlEscape(c.text.size() > 40 ? c.text.substr(0, 40) + L"\u2026" : c.text);

    std::wstring m;
    m += L"<Border x:Name=\"H" + i + L"\" Margin=\"0,0,0,2\" CornerRadius=\"4\" Background=\"Transparent\">";
    m += L"<StackPanel>";

    m += L"<Button x:Name=\"K" + i + L"\" HorizontalAlignment=\"Stretch\""
         L" HorizontalContentAlignment=\"Left\" Padding=\"8,6\" Background=\"Transparent\" BorderThickness=\"0\""
         L" AutomationProperties.AutomationId=\"Comment" + i + L"\"";
    m += L" AutomationProperties.Name=\"" + DevToolsXmlEscape(CommentRowAccessibleName(c, live, done), true) + L"\"";
    const std::wstring absent = DevToolsCommentText::AbsentLabel(!c.anchor.empty());
    m += L" ToolTipService.ToolTip=\"" + DevToolsXmlEscape(live ? L"Select this element"
                                                     : L"Marker unavailable: " + absent + L". Use the actions on the row") + L"\">";
    m += L"<Grid ColumnSpacing=\"8\"><Grid.ColumnDefinitions><ColumnDefinition Width=\"Auto\"/>"
         L"<ColumnDefinition Width=\"*\"/></Grid.ColumnDefinitions>";
    m += L"<Grid Grid.Column=\"0\" VerticalAlignment=\"Top\" Margin=\"0,6,0,0\">";
    m += L"<Ellipse x:Name=\"D" + i + L"\" Width=\"8\" Height=\"8\" StrokeThickness=\"1\""
         L" Fill=\"{ThemeResource AccentFillColorDefaultBrush}\""
         L" Stroke=\"{ThemeResource AccentFillColorDefaultBrush}\" Visibility=\"" +
         (live ? L"Visible" : L"Collapsed") + L"\"/>";
    m += L"<Ellipse x:Name=\"N" + i + L"\" Width=\"8\" Height=\"8\" StrokeThickness=\"1\""
         L" Fill=\"Transparent\" Stroke=\"{ThemeResource TextFillColorSecondaryBrush}\" Visibility=\"" +
         (live ? L"Collapsed" : L"Visible") + L"\"/>";
    m += L"</Grid>";
    m += L"<StackPanel Grid.Column=\"1\">";
    m += L"<TextBlock Text=\"" + DevToolsXmlEscape(c.text, true) + L"\" TextWrapping=\"Wrap\" MaxLines=\"2\""
         L" TextTrimming=\"CharacterEllipsis\" FontSize=\"13\"";
    m += done ? L" TextDecorations=\"Strikethrough\" Foreground=\"{ThemeResource TextFillColorTertiaryBrush}\"/>"
              : L" Foreground=\"{ThemeResource TextFillColorPrimaryBrush}\"/>";
    m += L"<StackPanel Orientation=\"Horizontal\" Spacing=\"6\" Margin=\"0,3,0,0\">";
    if (!done) {
        m += L"<Border x:Name=\"P" + i + L"\" CornerRadius=\"3\" Padding=\"5,1\" BorderThickness=\"1\""
             L" BorderBrush=\"{ThemeResource ControlStrokeColorDefaultBrush}\""
             L" Background=\"{ThemeResource ControlFillColorSecondaryBrush}\" VerticalAlignment=\"Center\""
             L" Visibility=\"" + (live ? L"Collapsed" : L"Visible") + L"\">"
             L"<TextBlock Text=\"" + absent + L"\" FontSize=\"10\""
             L" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\"/></Border>";
    }
    m += L"<TextBlock FontSize=\"11\" VerticalAlignment=\"Center\" TextTrimming=\"CharacterEllipsis\">";
    if (!c.element.empty()) {
        m += L"<Run Text=\"" + DevToolsXmlEscape(c.element) + L"\" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\"/>";
        m += L"<Run Text=\"   \"/>";
    }
    if (!where.empty()) {
        m += L"<Run Text=\"" + DevToolsXmlEscape(where) + L"\" Foreground=\"{ThemeResource AccentTextFillColorPrimaryBrush}\"/>";
    }
    m += L"</TextBlock></StackPanel></StackPanel></Grid></Button>";

    m += L"<StackPanel x:Name=\"A" + i + L"\" Orientation=\"Horizontal\""
         L" Margin=\"16,0,0,4\" Spacing=\"4\">";
    if (!c.uri.empty()) {
        if (g_xamlHandlerPresent) {
            m += L"<HyperlinkButton x:Name=\"O" + i + L"\" Content=\"Open source\" Padding=\"6,0\" FontSize=\"11\""
                 L" AutomationProperties.AutomationId=\"CommentOpenSource" + i + L"\""
                 L" AutomationProperties.Name=\"Open source for: " + subject + L"\""
                 L" ToolTipService.ToolTip=\"Hand this file to whatever opens .xaml on this machine\"/>";
        } else {
            m += L"<HyperlinkButton x:Name=\"E" + i + L"\" Content=\"Reveal\" Padding=\"6,0\" FontSize=\"11\""
                 L" AutomationProperties.Name=\"Reveal in File Explorer: " + subject + L"\""
                 L" AutomationProperties.AutomationId=\"CommentReveal" + i + L"\""
                 L" ToolTipService.ToolTip=\"Nothing on this machine opens .xaml files &#x2014; show it in File Explorer instead\"/>";
            m += L"<HyperlinkButton x:Name=\"C" + i + L"\" Content=\"Copy path\" Padding=\"6,0\" FontSize=\"11\""
                 L" AutomationProperties.AutomationId=\"CommentCopyPath" + i + L"\""
                 L" AutomationProperties.Name=\"Copy path for: " + subject + L"\""
                 L" ToolTipService.ToolTip=\"Copy this file's full path\"/>";
        }
    }
    if (!done && g_commentResolveFn) {
        m += L"<HyperlinkButton x:Name=\"R" + i + L"\" Content=\"Resolve\" Padding=\"6,0\" FontSize=\"11\""
             L" AutomationProperties.AutomationId=\"CommentResolve" + i + L"\""
             L" AutomationProperties.Name=\"Resolve: " + subject + L"\""
             L" ToolTipService.ToolTip=\"Mark this comment resolved\"/>";
    }
    m += L"</StackPanel>";
    m += L"</StackPanel></Border>";
    return m;
}

static std::wstring BuildCommentsFragment(const std::vector<DevToolsWindowComment>& all, size_t& outOpen)
{
    std::wstring m;
    m += L"<StackPanel xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\""
         L" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" Margin=\"4\">";

    auto caption = [&](const std::wstring& text) {
        m += L"<TextBlock Text=\"" + DevToolsXmlEscape(text) + L"\" Style=\"{StaticResource CaptionTextBlockStyle}\""
             L" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\" CharacterSpacing=\"60\""
             L" Margin=\"4,10,4,6\"/>";
    };

    g_commentRows.clear();
    g_commentRowLive.clear();
    g_xamlHandlerPresent = !ShellAssocFor(L".xaml").empty();
    size_t open = 0, offscreen = 0;
    for (const auto& c : all) if (c.status != L"resolved") ++open;

    if (all.empty()) {
        const std::wstring emptyMessage = g_commentResolveFn
            ? L"No comments for this app yet. Pick an element and use the Comment box on its panel; "
              L"they persist in the host project store. From your host project, an agent can read them with "
              L"&#x2018;winapp devtools comments list --status open --json&#x2019;."
            : L"No comments for this app yet. Restart the target so winapp can attach it writable, then add one.";
        m += L"<TextBlock TextWrapping=\"Wrap\" Margin=\"4,10,4,0\" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\""
             L" Text=\"" + emptyMessage + L"\"/>";
        m += L"</StackPanel>";
        outOpen = 0;
        return m;
    }

    std::vector<std::wstring> files;
    for (const auto& c : all) {
        if (c.status == L"resolved") continue;
        const std::wstring key = c.file.empty() ? std::wstring(L"(source not resolved)") : c.file;
        if (std::find(files.begin(), files.end(), key) == files.end()) files.push_back(key);
    }

    for (const auto& f : files) {
        caption(f);
        for (const auto& c : all) {
            if (c.status == L"resolved") continue;
            const std::wstring key = c.file.empty() ? std::wstring(L"(source not resolved)") : c.file;
            if (key != f) continue;
            const bool live = g_anchorWireFn && !c.anchor.empty() && g_anchorWireFn(c.anchor.c_str()) != 0;
            if (!live && !c.anchor.empty()) ++offscreen;
            m += BuildCommentRowMarkup(g_commentRows.size(), c, live);
            g_commentRows.push_back(c);
            g_commentRowLive.push_back(live ? 1 : 0);
        }
    }

    size_t resolved = 0;
    for (const auto& c : all) if (c.status == L"resolved") ++resolved;
    if (resolved) {
        caption(L"RESOLVED (" + std::to_wstring(resolved) + L")");
        for (const auto& c : all) {
            if (c.status != L"resolved") continue;
            m += BuildCommentRowMarkup(g_commentRows.size(), c, false);
            g_commentRows.push_back(c);
            g_commentRowLive.push_back(0);
        }
    }

    m += L"<TextBlock x:Name=\"OffNote\" FontSize=\"11\" TextWrapping=\"Wrap\" Margin=\"4,12,4,0\""
         L" Foreground=\"{ThemeResource TextFillColorTertiaryBrush}\" Visibility=\"Collapsed\"/>";

    m += L"</StackPanel>";
    outOpen = open;
    return m;
}

// The footnote's text for a given off-screen count. One writer, used by the initial render and by every
// recompute, so the two cannot drift.
static std::wstring CommentsOffscreenNote(size_t offscreen)
{
    return DevToolsCommentText::OffscreenNote(offscreen);
}

// Recompute anchor liveness after coalesced tree changes and update only changed row state.
// Run on the later UI dispatcher turn, not inside OnVisualTreeChange; do not reparse the list.
static void RecomputeCommentLiveness()
{
    if (!g_commentsView || g_commentRowUi.empty()) return;
    WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::WinCommentLive);
    size_t offscreen = 0, changed = 0;
    for (size_t i = 0; i < g_commentRowUi.size() && i < g_commentRows.size(); ++i) {
        CommentRowUi& r = g_commentRowUi[i];
        if (r.done) continue;
        const DevToolsWindowComment& c = g_commentRows[i];
        // Ask. A handle that resolved a moment ago says nothing about now, which is the whole reason the
        // tree-change notification exists.
        const bool live = g_anchorWireFn && !c.anchor.empty() && g_anchorWireFn(c.anchor.c_str()) != 0;
        if (!live && !c.anchor.empty()) ++offscreen;
        if (live == r.live) continue;
        r.live = live;
        ++changed;
        if (r.dotLive) DevToolsPutVisibility(r.dotLive, live ? 0 : 1);
        if (r.dotDead) DevToolsPutVisibility(r.dotDead, live ? 1 : 0);
        if (r.chip)    DevToolsPutVisibility(r.chip,    live ? 1 : 0);
        if (r.body) {
            HSTRING h = MakeStr(CommentRowAccessibleName(c, live, false));
            DevToolsSetAutomationName(g_apStatics, r.body, h);
            WindowsDeleteString(h);
        }
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

static void RefreshCommentsPane()
{
    WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::WinComments);
    if (!g_commentsHostChildren) return;

    std::vector<DevToolsWindowComment> all;
    if (g_commentsFn) g_commentsFn(&all);

    size_t open = 0;
    IInspectable* frag = nullptr;
    ReleaseCommentRowUi();
    if (FAILED(LoadMarkupWin(BuildCommentsFragment(all, open), &frag)) || !frag) {
        DevToolsOverlayLog(L"route1.comments fragment load failed");
        DevToolsVecClear(g_commentsHostChildren);
        g_commentRows.clear();
        g_commentRowLive.clear();
        return;
    }

    // Wire each row in the fragment's own namescope: the row body (K<i>), its always-on actions (O/E/C/R<i>)
    // and the four elements a tree change has to be able to rewrite without a re-parse -- the two dots
    // (D/N<i>), the chip (P<i>) and the row body's automation name.
    g_commentRowUi.assign(g_commentRows.size(), CommentRowUi{});
    void* fe = nullptr;
    if (SUCCEEDED(frag->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) && fe) {
        auto find = [&](const std::wstring& name) -> IInspectable* {
            HSTRING nm = MakeStr(name);
            IInspectable* found = nullptr;
            DevToolsFindName(fe, nm, &found);
            WindowsDeleteString(nm);
            return found;
        };
        // Hold one element by the interface the later writes need. Returns AddRef'd; released in
        // ReleaseCommentRowUi.
        auto hold = [&](const std::wstring& name, const GUID& iid, void** out) {
            IInspectable* found = find(name);
            if (!found) return;
            found->QueryInterface(iid, out);
            found->Release();
        };
        for (size_t i = 0; i < g_commentRows.size(); ++i) {
            const std::wstring n = std::to_wstring(i);
            CommentRowUi& r = g_commentRowUi[i];
            r.done = (g_commentRows[i].status == L"resolved");
            r.live = (i < g_commentRowLive.size() && g_commentRowLive[i] != 0);
            auto wireClick = [&](const std::wstring& name, DevToolsSinkBase* sink) {
                // Sinks are leaked for the process lifetime, exactly like every other sink here: they are COM
                // objects whose refcount the AddClick below owns, and a row that is absent from this fragment
                // (Resolve on a resolved row, Open source with no URI) just leaves an inert one behind.
                IInspectable* found = find(name);
                if (!found) return;
                void* bb = nullptr;
                if (SUCCEEDED(found->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), &bb)) && bb) {
                    __int64 tok = 0; DevToolsAddClick(bb, sink, &tok);
                    reinterpret_cast<IUnknown*>(bb)->Release();
                }
                found->Release();
            };
            { auto* s = new CommentRowSink();        s->Init(i); wireClick(L"K" + n, s); }
            { auto* s = new CommentResolveSink();    s->Init(i); wireClick(L"R" + n, s); }
            { auto* s = new CommentOpenSourceSink(); s->Init(i); wireClick(L"O" + n, s); }
            { auto* s = new CommentRevealSink();     s->Init(i); wireClick(L"E" + n, s); }
            { auto* s = new CommentCopyPathSink();   s->Init(i); wireClick(L"C" + n, s); }

            hold(L"A" + n, DevToolsIid<DevToolsX::IUIElement>(),        &r.actions);
            hold(L"D" + n, DevToolsIid<DevToolsX::IUIElement>(),        &r.dotLive);
            hold(L"N" + n, DevToolsIid<DevToolsX::IUIElement>(),        &r.dotDead);
            hold(L"P" + n, DevToolsIid<DevToolsX::IUIElement>(),        &r.chip);
            hold(L"K" + n, DevToolsIid<DevToolsX::IDependencyObject>(), &r.body);
            // A row whose liveness elements went missing can never be corrected by a tree change, and would
            // sit there looking authoritative. Say so rather than fail silently ('s shape).
            if (!r.done && (!r.dotLive || !r.dotDead || !r.chip || !r.body))
                DevToolsOverlayLog(L"route1.comments row %zu MISS: dotLive=%p dotDead=%p chip=%p body=%p"
                              L" -- this row's on-screen state will not follow the app's tree",
                              i, r.dotLive, r.dotDead, r.chip, r.body);
        }
        hold(L"OffNote", DevToolsIid<DevToolsXC::ITextBlock>(), &g_commentsOffNoteTb);
        hold(L"OffNote", DevToolsIid<DevToolsX::IUIElement>(),  &g_commentsOffNoteUi);
        reinterpret_cast<IUnknown*>(fe)->Release();
    }

    void* ui = nullptr;
    if (SUCCEEDED(frag->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) && ui) {
        DevToolsVecClear(g_commentsHostChildren);
        DevToolsVecAppend(g_commentsHostChildren, reinterpret_cast<IInspectable*>(ui));
        reinterpret_cast<IUnknown*>(ui)->Release();
    }
    frag->Release();

    RecomputeCommentLiveness();

    SetToggleLabel(g_commentsLbl, std::to_wstring(open).c_str());
    SetIconToggleState(g_commentsBadgeUi, open > 0);
    DevToolsOverlayLog(L"route1.comments rendered rows=%zu open=%zu", g_commentRows.size(), open);
}

// Tree-scoped controls collapse with the tree; comments view must not leave filters/toggles acting on nothing.
static void SetCommentsView(bool on)
{
    g_commentsView = on;
    if (g_treeScrollUi)     DevToolsPutVisibility(g_treeScrollUi, on ? 1 : 0);
    if (g_commentsScrollUi) DevToolsPutVisibility(g_commentsScrollUi, on ? 0 : 1);
    if (g_treeControlsUi)   DevToolsPutVisibility(g_treeControlsUi, on ? 1 : 0);
    auto checkTab = [](void* tab, bool checked) {
        if (!tab) return;
        IInspectable* boxed = nullptr;
        if (SUCCEEDED(BoxBool(checked, &boxed)) && boxed) {
            DevToolsToggleButtonPutIsChecked(tab, boxed);
            boxed->Release();
        }
    };
    checkTab(g_treeTabBtn, !on);
    checkTab(g_commentsTabBtn, on);
    if (on) RefreshCommentsPane();
}

struct CommentsViewSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen) return S_OK;
        if (g_commentsView != comments) SetCommentsView(comments);
        return S_OK;
    }
    void Init(bool toComments) { comments = toComments; InitSink(IID_RoutedEventHandler_Del, g_route1Gen); }
    bool comments = false;
};

static void UpdateCommentsButtonLabel()
{
    std::vector<DevToolsWindowComment> all;
    if (g_commentsFn) g_commentsFn(&all);
    size_t open = 0;
    for (const auto& c : all) if (c.status != L"resolved") ++open;
    SetToggleLabel(g_commentsLbl, std::to_wstring(open).c_str());
    SetIconToggleState(g_commentsBadgeUi, open > 0);
}
