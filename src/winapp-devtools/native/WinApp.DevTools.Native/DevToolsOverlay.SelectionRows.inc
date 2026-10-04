// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsOverlay.cpp; included into that translation unit, not compiled on its own.

// Every row sink wired for the CURRENT anchor, in row order. Non-owning: the sinks are leaked for the process
// lifetime exactly as before, this only remembers which ones belong to the panel that is up.
static std::vector<SelRowSink*> g_selRowSinks;

static bool SelectionSourceIsTextEditor(IInspectable* source, bool ctrl)
{
    if (!source) return false;
    winrt::com_ptr<IUnknown> identity;
    if (FAILED(source->QueryInterface(IID_IUnknown, identity.put_void()))) return false;
    for (auto* sink : g_selRowSinks) {
        if (!sink || sink->gen != g_selGen || !sink->box) continue;
        winrt::com_ptr<IUnknown> editor;
        if (SUCCEEDED(sink->box->QueryInterface(IID_IUnknown, editor.put_void())) && editor.get() == identity.get())
            return ctrl || !sink->multiline;
    }
    return false;
}

static void SelectionCommitTextEditor(IInspectable* source)
{
    if (!source) return;
    winrt::com_ptr<IUnknown> identity;
    if (FAILED(source->QueryInterface(IID_IUnknown, identity.put_void()))) return;
    for (auto* sink : std::vector<SelRowSink*>(g_selRowSinks)) {
        if (!sink || sink->gen != g_selGen || !sink->box) continue;
        winrt::com_ptr<IUnknown> editor;
        if (SUCCEEDED(sink->box->QueryInterface(IID_IUnknown, editor.put_void())) && editor.get() == identity.get()) {
            sink->Invoke(nullptr, nullptr);
            return;
        }
    }
}

static void GuestCommentRememberDraft(bool composer);
static void GuestCommentRestoreDraft(bool composer);
static bool GuestCommentCanEdit(InstanceHandle handle);

// apply the panel's outstanding commits SYNCHRONOUSLY, on the caller's stack, before a dismiss tears the anchor
// down. That compare is what makes calling every row here equivalent to calling only the focused one, without
// having to ask XAML which element has focus — an answer that is itself delivered late.
// Dismiss applies row commits synchronously before tearing down the anchor.
static void DevToolsSelFlushCommits()
{
    if (g_selRootLost) return;
    std::vector<SelRowSink*> sinks = g_selRowSinks;
    for (SelRowSink* s : sinks) if (s) s->Invoke(nullptr, nullptr);
    DevToolsSelCommitComment();
}

struct SelConfirmSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*) {
        if (gen != g_selGen) return S_OK;
        if (!g_selTarget || !g_cardWrite) return S_OK;
        DevToolsSelPending* pw = DevToolsSelPendingFind(prop);
        if (!pw) return S_OK;
        const std::wstring type = pw->type, val = pw->value;
        const auto outcome = g_cardWrite(g_selTarget, prop.c_str(), type.c_str(), val.c_str(), true);
        if (gen != g_selGen) return S_OK;
        if (DevToolsSelApplyCommit(outcome, prop, type, val) && gen == g_selGen) {
            for (auto* sink : g_selRowSinks)
                if (sink && sink->prop == prop) sink->AcceptValue(val);
        }
        return S_OK;
    }
    void Init(const std::wstring& p) {
        prop = p;
        InitSink(IID_RoutedEventHandler_Del, g_selGen);
    }
    std::wstring prop;
};

// Defined with the binding diagnosis below: run one Restore/Clear off the UI thread and re-diagnose the row.
static void SelDiagRunAction(size_t index, const std::wstring& prop, const wchar_t* op);

// A Restore binding / Clear value click. The work is handed to a worker: this runs on the UI thread, and the
// agent needs the UI thread to touch live XAML, so doing it inline would freeze the app being inspected.
struct SelBindActionSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*) {
        if (gen != g_selGen) return S_OK;
        SelDiagRunAction(index, prop, op);
        return S_OK;
    }
    void Init(size_t i, const std::wstring& p, const wchar_t* o) {
        index = i; prop = p; op = o;
        InitSink(IID_RoutedEventHandler_Del, g_selGen);
    }
    size_t index = 0;
    std::wstring prop;
    const wchar_t* op = L"restore";
};

static std::vector<DevToolsCardRow> DevToolsSelCurateRows(const std::vector<DevToolsCardRow>& rows)
{
    static const wchar_t* const kOrder[] = {
        L"Text", L"Content", L"IsChecked", L"Foreground", L"FontFamily", L"Opacity",
        L"IsEnabled", L"Visibility", L"Margin", L"Padding",
    };
    std::vector<DevToolsCardRow> out;
    for (const wchar_t* want : kOrder)
        for (const auto& r : rows)
            if (r.name == want && r.valueState != L"unresolved") {
                out.push_back(r);
                if (out.size() == 5) return out;
                break;
            }
    return out;
}

static bool SelRowHasExpression(const DevToolsCardRow& row)
{
    return row.source == L"Binding" || !row.binding.empty() ||
        (!row.authored.empty() && row.authoredKind != L"literal");
}

// The per-row binding diagnosis block and its two actions. A row is never briefly blank while the agent is
// being asked: blank reads as "this binding is fine", which is the one thing the row must not say by accident.
static std::wstring BuildSelDiagMarkup(size_t i, const std::wstring& prop, const DevToolsPalette& pal)
{
    const std::wstring n = std::to_wstring(i);
    std::wstring m;
    m += L"<StackPanel x:Name=\"DevToolsSelDiag" + n + L"\" Visibility=\"Collapsed\" Margin=\"0,0,0,4\" Spacing=\"4\">";
    m += L"<StackPanel Orientation=\"Horizontal\">";
    m += L"<TextBlock x:Name=\"DevToolsSelDiagPre" + n + L"\" Text=\"\" FontSize=\"12\" FontFamily=\"Consolas\""
         L" TextTrimming=\"CharacterEllipsis\" Foreground=\"" + pal.textTertiary + L"\"/>";
    m += L"<TextBlock x:Name=\"DevToolsSelDiagSeg" + n + L"\" Text=\"" + DevToolsXmlEscape(DevToolsBindingRow_PendingText()) +
         L"\" FontSize=\"12\" FontFamily=\"Consolas\" FontWeight=\"SemiBold\" Foreground=\"" + pal.caution + L"\"/>";
    m += L"<TextBlock x:Name=\"DevToolsSelDiagTail" + n + L"\" Text=\"\" FontSize=\"12\" FontFamily=\"Consolas\""
         L" TextTrimming=\"CharacterEllipsis\" Foreground=\"" + pal.textTertiary + L"\"/>";
    m += L"</StackPanel>";
    m += L"<TextBlock x:Name=\"DevToolsSelDiagWhy" + n + L"\" Text=\"\" FontSize=\"12\" TextWrapping=\"Wrap\""
         L" IsTextSelectionEnabled=\"True\" Foreground=\"" + pal.textSecondary + L"\"/>";
// Use resolved palette colors to avoid repeated resource lookup while rebuilding per pick. The accessible prose
// above carries the same answer; the decorative table is AccessibilityView=Raw so a screen reader does not
// repeat alignment spaces and glyphs as a second diagnosis.
    m += L"<TextBlock x:Name=\"DevToolsSelDiagWalk" + n + L"\" Visibility=\"Collapsed\" Text=\"\" FontSize=\"11\""
         L" FontFamily=\"Consolas\" TextWrapping=\"NoWrap\" Margin=\"0,2,0,0\""
         L" AutomationProperties.AccessibilityView=\"Raw\""
         L" IsTextSelectionEnabled=\"True\" Foreground=\"" + pal.textSecondary + L"\"/>";
    m += L"</StackPanel>";

// Capture is asynchronous; disclose the unknown source-write risk even before it answers.
    m += L"<TextBlock x:Name=\"DevToolsSelThrough" + n + L"\" Text=\"Source-write behavior is not yet known. "
         L"Editing may change the source model; restore is not model undo.\" FontSize=\"12\""
         L" TextWrapping=\"Wrap\" Foreground=\"" + pal.caution + L"\"/>";

// "Restore binding" re-attaches what the author wrote; "Clear value" calls ClearValue, which DELETES -- the
// property falls back to its default and the binding is gone.
    m += L"<StackPanel x:Name=\"DevToolsSelBindBtns" + n + L"\" Visibility=\"Collapsed\" Orientation=\"Horizontal\""
         L" Spacing=\"8\" Margin=\"0,4,0,4\">";
    m += L"<Button x:Name=\"DevToolsSelRestore" + n + L"\" Padding=\"8,4,8,4\" MinWidth=\"0\""
         L" ToolTipService.ToolTip=\"Re-attach the binding the author wrote for " + DevToolsXmlEscape(prop) + L"\""
         L" AutomationProperties.Name=\"Restore the binding on " + DevToolsXmlEscape(prop) + L"\">"
         L"<TextBlock x:Name=\"DevToolsSelRestoreLabel" + n + L"\" Text=\"Restore binding\" FontSize=\"12\"/></Button>";
    m += L"<Button x:Name=\"DevToolsSelClearVal" + n + L"\" Padding=\"8,4,8,4\" MinWidth=\"0\""
         L" ToolTipService.ToolTip=\"Delete the local value. " + DevToolsXmlEscape(prop) + L" falls back to its default"
         L" and the binding is gone -- this is not an undo.\""
         L" AutomationProperties.Name=\"Clear the value of " + DevToolsXmlEscape(prop) + L"\">"
         L"<TextBlock Text=\"Clear value\" FontSize=\"12\"/></Button>";
    m += L"</StackPanel>";
    return m;
}

// Brushes use the overlay palette while editor templates retain the app's Light, Dark and High Contrast
// styling.
static bool SelRowCanEdit(const DevToolsCardRow& row)
{
    return !row.type.empty() && !row.editKind.empty() && row.editKind != L"none" &&
        row.valueState != L"unresolved" && row.valueState != L"null" && row.valueState != L"unset" &&
        (row.editKind != L"number" || TrySelectionNumber(row.value, nullptr));
}

static bool SelRowUsesSlider(const DevToolsCardRow& row)
{
    double value = 0;
    return row.name == L"Opacity" && row.editKind == L"number" &&
        TrySelectionNumber(row.value, &value) && value >= 0 && value <= 1;
}

// Flat label/value rows with the editor inline: one click or Tab reaches a value, and the row never re-states
// it. Only binding details stay behind a disclosure. Editors stretch to the value column; the label column is
// shared with the Source row so every value starts on the same x.
static const wchar_t* const kSelLabelColumn = L"<Grid.ColumnDefinitions><ColumnDefinition Width=\"88\"/>"
    L"<ColumnDefinition Width=\"*\"/></Grid.ColumnDefinitions>";

// A flat Expander whose header text lines up with the column it sits in: the template pads its header 16 px,
// which the negative margin returns, and the content padding re-applies it so disclosed content aligns too.
static std::wstring SelFlatDisclosureOpen(const std::wstring& automationId, const std::wstring& name,
                                          const std::wstring& headerMarkup)
{
    return L"<Expander IsExpanded=\"False\" MinHeight=\"32\" Margin=\"-16,0,0,0\" Padding=\"16,4,0,8\""
           L" Background=\"Transparent\" BorderBrush=\"Transparent\""
           L" HorizontalAlignment=\"Stretch\" HorizontalContentAlignment=\"Stretch\""
           L" AutomationProperties.AutomationId=\"" + automationId + L"\""
           L" AutomationProperties.Name=\"" + name + L"\"><Expander.Header>" + headerMarkup +
           L"</Expander.Header>";
}

static std::wstring BuildSelRowsMarkup(const std::vector<DevToolsCardRow>& rows, bool readOnly)
{
    const DevToolsPalette& pal = DevToolsInverseAcrylicPalette();
    if (rows.empty())
        return L"<TextBlock Text=\"No quick values. Open DevTools for more details.\" Style=\"{StaticResource CaptionTextBlockStyle}\""
            L" TextWrapping=\"Wrap\" Foreground=\"" + pal.textSecondary + L"\"/>";
    std::wstring m;
    for (size_t i = 0; i < rows.size(); ++i) {
        const DevToolsCardRow& r = rows[i];
        const std::wstring n = std::to_wstring(i);
// A curated property the runtime reports as read-only arrives with an empty write type; it shows as a static
// value instead of being dropped from the panel, which is what the window has always done for the same element.
        const bool rowReadOnly = readOnly || !SelRowCanEdit(r);
// Prefer what the developer literally wrote - that is the only way an {x:Bind} or a {ThemeResource} key can
// appear at all - and fall back to the reconstructed {Binding...}. A bound row gets the line EVEN WHEN neither
// is readable, saying so explicitly: silence would read as "not bound".
        const std::wstring expr = !r.authored.empty() ? r.authored : r.binding;
        const bool exprUnknown = expr.empty();
        const bool bound = SelRowHasExpression(r);
        const std::wstring display = SelectionPreviewValue(r);
        const std::wstring editName = L"DevToolsSelEdit" + n;
        const std::wstring boundTip = bound && !rowReadOnly
            ? L" ToolTipService.ToolTip=\"Bound value. Editing may replace the binding or change its source model.\""
            : std::wstring();

        m += L"<Grid ColumnSpacing=\"12\" AutomationProperties.AutomationId=\"DevToolsSelRow" + n + L"\">";
        m += kSelLabelColumn;
        m += L"<TextBlock x:Name=\"DevToolsSelLabel" + n + L"\" Text=\"" + DevToolsXmlEscape(r.name) + L"\""
             L" Style=\"{StaticResource CaptionTextBlockStyle}\" Margin=\"0,8,0,0\" VerticalAlignment=\"Top\""
             L" TextTrimming=\"CharacterEllipsis\" Foreground=\"" + pal.textSecondary + L"\""
             L" ToolTipService.ToolTip=\"" + DevToolsXmlEscape(r.name) + L"\"/>";
        m += L"<StackPanel Grid.Column=\"1\" Spacing=\"4\">";

        if (rowReadOnly) {
            m += L"<StackPanel Orientation=\"Horizontal\" Spacing=\"8\" Margin=\"0,6,0,6\">";
            if (SelectionHasColorSwatch(r))
                m += L"<Border x:Name=\"DevToolsSelSwatch" + n + L"\" Width=\"16\" Height=\"16\" CornerRadius=\"3\""
                     L" VerticalAlignment=\"Center\" Background=\"" + r.value + L"\" BorderThickness=\"1\""
                     L" BorderBrush=\"" + pal.rim + L"\" AutomationProperties.AccessibilityView=\"Raw\"/>";
            m += L"<TextBlock x:Name=\"DevToolsSelValue" + n + L"\" Text=\"" + XmlEscapeLines(display) + L"\""
                 L" TextWrapping=\"Wrap\" MaxLines=\"3\" TextTrimming=\"CharacterEllipsis\" IsTextSelectionEnabled=\"True\""
                 L" Foreground=\"" + pal.textPrimary + L"\" AutomationProperties.Name=\"" + DevToolsXmlEscape(r.name) + L"\"/>";
            m += L"</StackPanel>";
        } else if (r.editKind == L"bool") {
// Booleans render as a real ToggleSwitch in the card too -- the window and the card must agree on how a
// boolean looks.
            m += L"<ToggleSwitch x:Name=\"" + editName + L"\" MinWidth=\"0\" Height=\"32\" HorizontalAlignment=\"Left\""
                 L" AutomationProperties.AutomationId=\"" + editName + L"\" VerticalAlignment=\"Center\""
                 L" OnContent=\"True\" OffContent=\"False\" IsOn=\"";
            m += (DevToolsBoolValueIsOn(r.value) ? L"True" : L"False");
            m += L"\" AutomationProperties.Name=\"" + DevToolsXmlEscape(r.name) + L"\"" + boundTip + L"/>";
        } else if (SelRowUsesSlider(r)) {
            m += L"<Grid ColumnSpacing=\"12\"><Grid.ColumnDefinitions><ColumnDefinition Width=\"*\"/>"
                 L"<ColumnDefinition Width=\"36\"/></Grid.ColumnDefinitions>";
            m += L"<Slider x:Name=\"" + editName + L"\" Value=\"" + DevToolsXmlEscape(r.value) + L"\""
                 L" Minimum=\"0\" Maximum=\"1\" StepFrequency=\"0.01\" SmallChange=\"0.01\" LargeChange=\"0.1\""
                 L" VerticalAlignment=\"Center\" AutomationProperties.AutomationId=\"" + editName + L"\""
                 L" AutomationProperties.Name=\"Opacity\"" + boundTip + L"/>";
            m += L"<TextBlock x:Name=\"DevToolsSelValue" + n + L"\" Grid.Column=\"1\" Text=\"" + XmlEscapeLines(display) + L"\""
                 L" VerticalAlignment=\"Center\" HorizontalAlignment=\"Right\" Foreground=\"" + pal.textPrimary + L"\""
                 L" AutomationProperties.AccessibilityView=\"Raw\"/>";
            m += L"</Grid>";
        } else if (r.editKind == L"number") {
            m += L"<NumberBox x:Name=\"" + editName + L"\" Value=\"" + DevToolsXmlEscape(r.value) + L"\""
                 L" HorizontalAlignment=\"Stretch\" MinHeight=\"32\" SpinButtonPlacementMode=\"Compact\" SmallChange=\"" +
                 (r.name == L"Opacity" ? std::wstring(L"0.1") : std::wstring(L"1")) + L"\""
                 L" AutomationProperties.AutomationId=\"" + editName + L"\""
                 L" AutomationProperties.Name=\"" + DevToolsXmlEscape(r.name) + L"\"" + boundTip + L"/>";
        } else if (const auto members = DevToolsEnumMembersFor(r.type); !members.empty()) {
            const int sel = DevToolsEnumIndexOf(members, r.value);
            m += L"<ComboBox x:Name=\"" + editName + L"\" HorizontalAlignment=\"Stretch\" MinHeight=\"32\"";
            if (sel >= 0) m += L" SelectedIndex=\"" + std::to_wstring(sel) + L"\"";
            m += L" AutomationProperties.AutomationId=\"" + editName + L"\" AutomationProperties.Name=\"" +
                 DevToolsXmlEscape(r.name) + L"\"" + boundTip + L">";
            for (const auto& mem : members) m += L"<ComboBoxItem Content=\"" + DevToolsXmlEscape(mem) + L"\"/>";
            m += L"</ComboBox>";
        } else {
            const bool color = r.editKind == L"color";
            if (color) {
                m += L"<Grid ColumnSpacing=\"8\"><Grid.ColumnDefinitions><ColumnDefinition Width=\"Auto\"/>"
                     L"<ColumnDefinition Width=\"*\"/></Grid.ColumnDefinitions>";
                m += L"<Border x:Name=\"DevToolsSelSwatch" + n + L"\" Width=\"20\" Height=\"20\" CornerRadius=\"4\""
                     L" VerticalAlignment=\"Center\" Background=\"" +
                     (SelectionHasColorSwatch(r) ? r.value : std::wstring(L"Transparent")) + L"\""
                     L" BorderThickness=\"1\" BorderBrush=\"" + pal.rim + L"\" AutomationProperties.AccessibilityView=\"Raw\"/>";
            }
            m += L"<TextBox x:Name=\"" + editName + L"\"";
            if (color) m += L" Grid.Column=\"1\"";
            // Before Text: XAML applies attributes in order, and a single-line box keeps only the first line.
            if (DevToolsEditText::IsMultiline(r.value))
                m += L" AcceptsReturn=\"True\" TextWrapping=\"Wrap\" MaxHeight=\"104\"";
            m += L" Text=\"" + XmlEscapeLines(r.value) + L"\" HorizontalAlignment=\"Stretch\" MinHeight=\"32\""
                 L" AutomationProperties.AutomationId=\"" + editName + L"\" AutomationProperties.Name=\"" + DevToolsXmlEscape(r.name) + L"\"";
            if (color) m += L" PlaceholderText=\"#AARRGGBB\" ToolTipService.ToolTip=\"Color as #AARRGGBB\"";
            else if (r.value.empty()) m += L" PlaceholderText=\"Empty\"";
            else if (DevToolsEditText::IsMultiline(r.value)) m += L" ToolTipService.ToolTip=\"Ctrl+Enter to apply\"";
            else m += boundTip;
            m += L"/>";
            if (color) m += L"</Grid>";
        }

        if (bound) {
            const bool confirmable = !rowReadOnly && r.source == L"Binding";
            m += L"<Grid ColumnSpacing=\"6\"><Grid.ColumnDefinitions><ColumnDefinition Width=\"Auto\"/>"
                 L"<ColumnDefinition Width=\"*\"/></Grid.ColumnDefinitions>"
                 L"<FontIcon FlowDirection=\"LeftToRight\" Glyph=\"&#xE71B;\" FontSize=\"12\" VerticalAlignment=\"Center\""
                 L" Foreground=\"" + pal.textSecondary + L"\" AutomationProperties.AccessibilityView=\"Raw\"/>";
            m += L"<TextBlock Grid.Column=\"1\"";
            if (confirmable) m += L" x:Name=\"DevToolsSelExpr" + n + L"\"";
            m += L" Text=\"" + DevToolsXmlEscape(exprUnknown ? std::wstring(L"Bound (expression unavailable)") : expr) +
                 L"\" FontSize=\"12\" TextTrimming=\"CharacterEllipsis\" VerticalAlignment=\"Center\"";
            if (exprUnknown) {
                m += L" FontStyle=\"Italic\" Foreground=\"" + pal.textSecondary + L"\""
                     L" ToolTipService.ToolTip=\"This value comes from a binding, but its expression could not be read\"/>";
            } else {
                m += L" FontFamily=\"Consolas\" IsTextSelectionEnabled=\"True\" Foreground=\"" + pal.textSecondary + L"\""
                     L" ToolTipService.ToolTip=\"" + DevToolsXmlEscape(expr) + L"\"/>";
            }
            m += L"</Grid>";
            if (confirmable) {
                m += L"<Button x:Name=\"DevToolsSelConfirm" + n + L"\" Visibility=\"Collapsed\""
                     L" Padding=\"8,4,8,4\" MinWidth=\"0\" HorizontalAlignment=\"Left\""
                     L" ToolTipService.ToolTip=\"" + DevToolsXmlEscape(r.name) + L" is bound. Committing replaces that"
                     L" binding with a literal value. Inspect restoration support in DevTools.\""
                     L" AutomationProperties.AutomationId=\"DevToolsSelConfirm" + n + L"\""
                     L" AutomationProperties.Name=\"Replace the binding on " + DevToolsXmlEscape(r.name) + L"\">"
                     L"<TextBlock x:Name=\"DevToolsSelConfirmText" + n + L"\" Text=\"\""
                     L" Style=\"{StaticResource CaptionTextBlockStyle}\" TextWrapping=\"Wrap\"/></Button>";
            }
            m += SelFlatDisclosureOpen(L"DevToolsSelBinding" + n, L"Binding details for " + DevToolsXmlEscape(r.name),
                     L"<TextBlock Text=\"Binding details\" Style=\"{StaticResource CaptionTextBlockStyle}\""
                     L" Foreground=\"" + pal.textSecondary + L"\"/>");
            m += L"<StackPanel Spacing=\"4\">";
            m += BuildSelDiagMarkup(i, r.name, pal);
            m += L"</StackPanel></Expander>";
        }
        m += L"</StackPanel></Grid>";
    }
    return m;
}
// Hook one row's Restore binding / Clear value buttons. They stay Collapsed until the diagnosis says the row
// actually has something to restore, so a row that cannot be undone never shows an undo.
static void DevToolsSelWireBindButtons(IInspectable* root, size_t i, const std::wstring& prop)
{
    struct { const wchar_t* name; const wchar_t* op; } kBtns[] = {
        { L"DevToolsSelRestore",  L"restore" },
        { L"DevToolsSelClearVal", L"clear"   },
    };
    for (const auto& b : kBtns) {
        IInspectable* el = DevToolsSelFindName(root, (std::wstring(b.name) + std::to_wstring(i)).c_str());
        if (!el) continue;
        void* bb = nullptr;
        if (SUCCEEDED(el->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), reinterpret_cast<void**>(&bb))) && bb) {
            SelBindActionSink* s = new SelBindActionSink();
            s->Init(i, prop, b.op);
            __int64 tok = 0;
            HRESULT hr = DevToolsAddClick(bb, s, &tok);
            DevToolsOverlayLog(L"overlay.sel row %s %s wire hr=0x%08X", prop.c_str(), b.op, hr);
            reinterpret_cast<IUnknown*>(bb)->Release();
        }
        el->Release();
    }
}

// Wire each quick-edit row's editor to a commit-on-leave sink: resolve DevToolsSelEdit{i} by FindName
// (transferring its ref into g_selRowBoxes for lifetime + teardown release), then hook the ComboBox's
// SelectionChanged (enum rows) or the TextBox's LostFocus (the same projected add_LostFocus the card uses) so
// the value co.
// Row sinks keep AddRef'd editors so blur and selection teardown can release them deterministically.
static void DevToolsSelWireRows(IInspectable* root, const std::vector<DevToolsCardRow>& rows)
{    for (size_t i = 0; i < rows.size(); ++i) {
        if (!SelRowCanEdit(rows[i])) continue;
        const std::wstring editName = L"DevToolsSelEdit" + std::to_wstring(i);
        IInspectable* ed = DevToolsSelFindName(root, editName.c_str());
        if (!ed) {
// Same two-sided contract as the window's props pane: BuildSelRowsMarkup decides which rows get an editor and
// what it is named, this loop decides what to look for.
            DevToolsOverlayLog(L"overlay.sel wire MISS: editable row '%s' (type '%s') has no editor %s -- it renders but will never commit",
                          rows[i].name.c_str(), rows[i].type.c_str(), editName.c_str());
            continue;
        }
        std::vector<std::wstring> members = DevToolsEnumMembersFor(rows[i].type);
        if (SelRowUsesSlider(rows[i])) {
            void* range = nullptr;
            double seed = 0;
            HRESULT hr = ed->QueryInterface(DevToolsIid<DevToolsXCP::IRangeBase>(), &range);
            if (SUCCEEDED(hr) && range) {
                hr = DevToolsRangeGetValue(range, &seed);
                if (SUCCEEDED(hr) && std::isfinite(seed)) {
                    auto* sink = new SelRowSink();
                    sink->InitSlider(rows[i].name, rows[i].type, rows[i].value, range, seed);
                    __int64 token = 0;
                    hr = DevToolsRangeAddValueChanged(range, sink, &token);
                    if (SUCCEEDED(hr)) g_selRowSinks.push_back(sink);
                    else sink->Release();
                } else if (SUCCEEDED(hr)) hr = E_INVALIDARG;
                reinterpret_cast<IUnknown*>(range)->Release();
            }
            if (FAILED(hr) || !range)
                SetSelectionOperationError(rows[i].name, L"Couldn't connect the opacity slider. Reopen this panel.");
        } else if (rows[i].editKind == L"number") {
            void* number = nullptr;
            const HRESULT queried = ed->QueryInterface(DevToolsIid<DevToolsXC::INumberBox>(), &number);
            if (SUCCEEDED(queried) && number) {
                auto* sink = new SelRowSink();
                sink->InitNumber(rows[i].name, rows[i].type, rows[i].value, number);
                __int64 token = 0;
                const HRESULT hr = DevToolsNumberBoxAddValueChanged(number, sink, &token);
                if (SUCCEEDED(hr)) g_selRowSinks.push_back(sink);
                else {
                    SetSelectionOperationError(rows[i].name, L"Couldn't connect the " + rows[i].name + L" editor. Reopen this panel.");
                    sink->Release();
                }
                reinterpret_cast<IUnknown*>(number)->Release();
            } else {
                SetSelectionOperationError(rows[i].name, L"The numeric editor is unavailable. Open DevTools to edit this value.");
            }
        } else if (rows[i].editKind == L"bool") {
            void* ts = nullptr;
            if (SUCCEEDED(ed->QueryInterface(DevToolsIid<DevToolsXC::IToggleSwitch>(), reinterpret_cast<void**>(&ts))) && ts) {
                SelRowSink* s = new SelRowSink();
                s->InitBool(rows[i].name, rows[i].type, DevToolsBoolLiteral(DevToolsBoolValueIsOn(rows[i].value)), ts);
                __int64 tok = 0; HRESULT hr = DevToolsAddToggled(ts, s, &tok);
                DevToolsOverlayLog(L"overlay.sel row %s bool wire hr=0x%08X", rows[i].name.c_str(), hr);
                if (SUCCEEDED(hr)) g_selRowSinks.push_back(s);
                else { s->Release(); SetSelectionOperationError(rows[i].name, L"Couldn't connect the editor. Reopen this panel."); }
                reinterpret_cast<IUnknown*>(ts)->Release();
            }
        } else if (!members.empty()) {
            IInspectable* sel = nullptr;
            if (SUCCEEDED(ed->QueryInterface(DevToolsIid<DevToolsXCP::ISelector>(), reinterpret_cast<void**>(&sel))) && sel) {
                SelRowSink* s = new SelRowSink();
                int cur = DevToolsEnumIndexOf(members, rows[i].value);
                std::wstring orig = (cur >= 0) ? members[(size_t)cur] : rows[i].value;
                s->InitEnum(rows[i].name, rows[i].type, orig, members, sel);
                __int64 tok = 0; HRESULT hr = DevToolsAddSelectionChanged(sel, s, &tok);
                DevToolsOverlayLog(L"overlay.sel row %s enum wire hr=0x%08X", rows[i].name.c_str(), hr);
                if (SUCCEEDED(hr)) g_selRowSinks.push_back(s);
                else { s->Release(); SetSelectionOperationError(rows[i].name, L"Couldn't connect the editor. Reopen this panel."); }
                sel->Release();
            }
        } else {
            void* ui = nullptr;
            if (SUCCEEDED(ed->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) && ui) {
                SelRowSink* s = new SelRowSink();
                s->InitText(rows[i].name, rows[i].type, rows[i].value, ed);
                __int64 tok = 0;
                HRESULT hr = DevToolsAddLostFocus(ui, s, &tok);
                DevToolsOverlayLog(L"overlay.sel row %s text wire hr=0x%08X", rows[i].name.c_str(), hr);
                if (SUCCEEDED(hr)) {
                    g_selRowSinks.push_back(s);
                    auto* focus = new SelRowFocusSink();
                    focus->Init(s);
                    if (FAILED(DevToolsAddGotFocus(ui, focus, &tok))) focus->Release();
                }
                else { s->Release(); SetSelectionOperationError(rows[i].name, L"Couldn't connect the editor. Reopen this panel."); }
                reinterpret_cast<IUnknown*>(ui)->Release();
            }
        }
        g_selRowBoxes.push_back(ed);
        DevToolsSelWireBindButtons(root, i, rows[i].name);
        if (rows[i].source != L"Binding") continue;
        const std::wstring cName = L"DevToolsSelConfirm" + std::to_wstring(i);
        IInspectable* cb = DevToolsSelFindName(root, cName.c_str());
        if (!cb) continue;
        DevToolsSelExprRefs refs; refs.prop = rows[i].name;
        void* bb = nullptr;
        if (SUCCEEDED(cb->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), reinterpret_cast<void**>(&bb))) && bb) {
            SelConfirmSink* cs = new SelConfirmSink(); cs->Init(rows[i].name);
            __int64 tok = 0; HRESULT hr = DevToolsAddClick(bb, cs, &tok);
            DevToolsOverlayLog(L"overlay.sel row %s confirm wire hr=0x%08X", rows[i].name.c_str(), hr);
            reinterpret_cast<IUnknown*>(bb)->Release();
        }
        cb->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &refs.btn);
        cb->Release();
        if (IInspectable* xl = DevToolsSelFindName(root, (L"DevToolsSelExpr" + std::to_wstring(i)).c_str())) {
            xl->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &refs.text);
            xl->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &refs.textTb);
            xl->Release();
        }
        if (IInspectable* xt = DevToolsSelFindName(root, (L"DevToolsSelConfirmText" + std::to_wstring(i)).c_str())) {
            xt->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &refs.label);
            xt->Release();
        }
        g_selExprRefs.push_back(refs);
    }
}
