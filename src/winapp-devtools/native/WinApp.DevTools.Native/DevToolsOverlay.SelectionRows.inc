// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsOverlay.cpp; included into that translation unit, not compiled on its own.

// Every row sink wired for the CURRENT anchor, in row order. Non-owning: the sinks are leaked for the process
// lifetime exactly as before, this only remembers which ones belong to the panel that is up.
static std::vector<SelRowSink*> g_selRowSinks;

static bool SelSameObject(void* candidate, IUnknown* identity)
{
    if (!candidate || !identity) return false;
    winrt::com_ptr<IUnknown> other;
    return SUCCEEDED(reinterpret_cast<IUnknown*>(candidate)->QueryInterface(IID_IUnknown, other.put_void())) &&
        other.get() == identity;
}

static bool SelectionSourceIsTextEditor(IInspectable* source, bool ctrl)
{
    if (!source) return false;
    winrt::com_ptr<IUnknown> identity;
    if (FAILED(source->QueryInterface(IID_IUnknown, identity.put_void()))) return false;
    for (auto* sink : g_selRowSinks) {
        if (!sink || sink->gen != g_selGen || !sink->box) continue;
        if (SelSameObject(sink->box, identity.get())) return ctrl || !sink->multiline;
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
        if (SelSameObject(sink->box, identity.get())) { sink->Invoke(nullptr, nullptr); return; }
    }
}

// The row sink whose text or number editor contains `source`. A NumberBox raises keys from the TextBox inside
// its template, so the walk goes up a few visual parents.
static SelRowSink* SelSinkOwningEditor(IInspectable* source)
{
    try {
        winrt::Windows::Foundation::IInspectable object{ nullptr };
        winrt::copy_from_abi(object, source);
        auto node = object.try_as<DevToolsX::DependencyObject>();
        for (int depth = 0; node && depth < 8; ++depth, node = DevToolsXM::VisualTreeHelper::GetParent(node)) {
            auto* identity = static_cast<IUnknown*>(winrt::get_abi(node.as<winrt::Windows::Foundation::IUnknown>()));
            for (auto* sink : g_selRowSinks) {
                if (!sink || sink->gen != g_selGen) continue;
                if (SelSameObject(sink->box, identity) || SelSameObject(sink->number, identity)) return sink;
            }
        }
    } catch (...) {}
    return nullptr;
}

static bool SelectionRevertEditor(IInspectable* source)
{
    SelRowSink* sink = SelSinkOwningEditor(source);
    if (!sink) return false;
    try {
        // The key may come from inside the editor's template, so find the TextBox that holds the typed text: the
        // row's own box, or the one inside its NumberBox.
        winrt::Windows::Foundation::IInspectable object{ nullptr };
        winrt::copy_from_abi(object, sink->box ? sink->box : source);
        DevToolsXC::TextBox typing{ nullptr };
        auto node = object.try_as<DevToolsX::DependencyObject>();
        for (int depth = 0; node && !typing && depth < 8; ++depth, node = DevToolsXM::VisualTreeHelper::GetParent(node))
            typing = node.try_as<DevToolsXC::TextBox>();
        if (!typing) return false;
        std::wstring committed;
        if (sink->number) {
            winrt::Windows::Foundation::IInspectable number{ nullptr };
            winrt::copy_from_abi(number, sink->number);
            committed = number.as<DevToolsXC::NumberBox>().Text().c_str();
        } else {
            committed = sink->hasShown ? sink->shown : sink->original;
        }
        if (std::wstring(typing.Text().c_str()) == committed) return false;
        g_selReverting = true;
        typing.Text(committed);
        typing.Select(static_cast<int32_t>(committed.size()), 0);
        g_selReverting = false;
        return true;
    } catch (...) {
        g_selReverting = false;
        return false;
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

// Richer editing (color picker, per-side thickness, binding details) lives in the inspector window. A row's
// open-in-DevTools button records which property the window should reveal, then opens it on this element.
static std::wstring g_selRevealProp;

bool DevToolsOverlay_TakeInspectorReveal(std::wstring* prop)
{
    if (g_selRevealProp.empty()) return false;
    if (prop) *prop = g_selRevealProp;
    g_selRevealProp.clear();
    return true;
}

static void OnInspectClick(void*, void*);

struct SelRevealSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*) {
        if (gen != g_selGen) return S_OK;
        DevToolsOverlayLog(L"overlay.sel reveal %s in the inspector", prop.c_str());
        g_selRevealProp = prop;
        OnInspectClick(nullptr, nullptr);
        // A window that did not open leaves nothing to consume the request.
        g_selRevealProp.clear();
        return S_OK;
    }
    void Init(const std::wstring& p) { prop = p; InitSink(IID_RoutedEventHandler_Del, g_selGen); }
    std::wstring prop;
};

static void SetOverlayElementVisible(IInspectable* el, bool visible);

// Shows a bound row's one-line caution while one of its editors has focus.
struct SelCautionSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*) {
        if (gen != g_selGen || !g_selUi) return S_OK;
        if (IInspectable* el = DevToolsSelFindName(g_selUi, name.c_str())) {
            SetOverlayElementVisible(el, show);
            el->Release();
        }
        return S_OK;
    }
    void Init(const std::wstring& n, bool s) { name = n; show = s; InitSink(IID_RoutedEventHandler_Del, g_selGen); }
    std::wstring name;
    bool show = false;
};

static bool SelTypeIs(const std::wstring& type, std::initializer_list<const wchar_t*> names)
{
    for (const wchar_t* n : names) if (type == n) return true;
    return false;
}

static bool SelTypeEndsWith(const std::wstring& type, const wchar_t* suffix)
{
    const size_t n = wcslen(suffix);
    return type.size() >= n && type.compare(type.size() - n, n, suffix) == 0;
}

// The handful of values people change most on this kind of element, best first. Every list ends in the same
// generic fallbacks, so an element missing the specific ones still gets a useful panel.
static std::vector<const wchar_t*> SelCuratedNames(const std::wstring& shortType)
{
    if (SelTypeIs(shortType, { L"TextBlock", L"RichTextBlock" }))
        return { L"Text", L"Foreground", L"FontSize", L"TextWrapping", L"Margin", L"Visibility" };
    if (SelTypeIs(shortType, { L"CheckBox", L"RadioButton" }))
        return { L"Content", L"IsChecked", L"IsEnabled", L"Foreground", L"Margin", L"Visibility" };
    if (shortType == L"ToggleSwitch")
        return { L"Header", L"IsOn", L"IsEnabled", L"Margin", L"Visibility" };
    if (SelTypeIs(shortType, { L"TextBox", L"AutoSuggestBox", L"RichEditBox", L"NumberBox", L"ComboBox" }))
        return { L"Text", L"Header", L"PlaceholderText", L"IsEnabled", L"Width", L"Visibility" };
    if (SelTypeEndsWith(shortType, L"Button"))
        return { L"Content", L"Foreground", L"Background", L"IsEnabled", L"Padding", L"Visibility" };
    if (SelTypeIs(shortType, { L"StackPanel", L"VirtualizingStackPanel" }))
        return { L"Orientation", L"Spacing", L"Padding", L"HorizontalAlignment", L"Margin", L"Visibility" };
    if (shortType == L"Border")
        return { L"Background", L"BorderBrush", L"BorderThickness", L"CornerRadius", L"Padding", L"Width" };
    if (SelTypeIs(shortType, { L"Rectangle", L"Ellipse", L"Path", L"Line", L"Polygon", L"Polyline" }))
        return { L"Fill", L"Stroke", L"Width", L"Height", L"Opacity", L"Margin" };
    if (shortType == L"Image")
        return { L"Width", L"Height", L"Stretch", L"Opacity", L"Margin", L"Visibility" };
    if (SelTypeEndsWith(shortType, L"Panel") || SelTypeIs(shortType, { L"Grid", L"Canvas" }))
        return { L"Background", L"Padding", L"HorizontalAlignment", L"VerticalAlignment", L"Margin", L"Visibility" };
    return { L"Content", L"Text", L"Foreground", L"Opacity", L"HorizontalAlignment", L"Margin", L"Visibility" };
}

static bool SelRowHasExpression(const DevToolsCardRow& row);

static std::vector<DevToolsCardRow> DevToolsSelCurateRows(const std::vector<DevToolsCardRow>& rows,
                                                          const std::wstring& shortType = std::wstring())
{
    static const wchar_t* const kFallback[] = { L"Opacity", L"Visibility", L"IsEnabled", L"Margin", L"Padding" };
    constexpr size_t kMaxRows = 6;
    std::vector<DevToolsCardRow> out;
    auto take = [&](const wchar_t* want) {
        if (out.size() >= kMaxRows) return;
        for (const auto& existing : out) if (existing.name == want) return;
        // An unset optional value such as a missing Header says nothing; a bound null is worth seeing.
        for (const auto& r : rows)
            if (r.name == want && r.valueState != L"unresolved" &&
                (r.valueState != L"null" || SelRowHasExpression(r))) { out.push_back(r); return; }
    };
    for (const wchar_t* want : SelCuratedNames(shortType)) take(want);
    for (const wchar_t* want : kFallback) take(want);
    return out;
}

static bool SelRowHasExpression(const DevToolsCardRow& row)
{
    return row.source == L"Binding" || !row.binding.empty() ||
        (!row.authored.empty() && row.authoredKind != L"literal");
}

// A data binding, as opposed to a resource reference. Only these have a status worth diagnosing.
static bool SelRowIsBinding(const DevToolsCardRow& row)
{
    return row.source == L"Binding" || !row.binding.empty() ||
        row.authoredKind == L"xBind" || row.authoredKind == L"binding";
}

static bool SelRowAllowsAuto(const DevToolsCardRow& row)
{
    return (row.name == L"Width" || row.name == L"Height") && row.editKind == L"number";
}

// Brushes use the overlay palette while editor templates retain the app's Light, Dark and High Contrast
// styling.
static bool SelRowCanEdit(const DevToolsCardRow& row)
{
    if (row.type.empty() || row.editKind.empty() || row.editKind == L"none" ||
        row.valueState == L"unresolved" || row.valueState == L"null") return false;
    if (row.valueState == L"unset") return SelRowAllowsAuto(row);
    return row.editKind != L"number" || TrySelectionNumber(row.value, nullptr);
}

static bool SelRowUsesSlider(const DevToolsCardRow& row)
{
    double value = 0;
    return row.name == L"Opacity" && row.editKind == L"number" &&
        TrySelectionNumber(row.value, &value) && value >= 0 && value <= 1;
}

// Small, spatial choices read better as a segmented control; longer or abstract lists stay drop-downs.
static bool SelRowUsesSegments(const DevToolsCardRow& row)
{
    const std::wstring t = row.type;
    return SelTypeIs(t, { L"HorizontalAlignment", L"VerticalAlignment", L"Orientation" }) &&
        SelRowMembers(row).size() <= 4;
}

// "{x:Bind Greeting, Mode=OneWay}" -> "{x:Bind Greeting}": the part that names what an edit replaces.
static std::wstring SelShortExpression(const DevToolsCardRow& row)
{
    const std::wstring expr = !row.authored.empty() ? row.authored : row.binding;
    if (expr.empty()) return L"its binding";
    const size_t comma = expr.find(L',');
    if (comma == std::wstring::npos) return expr;
    std::wstring head = expr.substr(0, comma);
    while (!head.empty() && head.back() == L' ') head.pop_back();
    return head + L"}";
}

// "8,8,8,8" -> "8" and "8,4,8,4" -> "8,4": the shortest spelling that writes the same value back.
static std::wstring SelCompactFields(const DevToolsCardRow& row)
{
    if (row.editKind != L"fields" || row.fields.size() != 4) return row.value;
    std::vector<std::wstring> parts;
    size_t start = 0;
    for (;;) {
        const size_t comma = row.value.find(L',', start);
        parts.push_back(row.value.substr(start, comma == std::wstring::npos ? std::wstring::npos : comma - start));
        if (comma == std::wstring::npos) break;
        start = comma + 1;
    }
    if (parts.size() != 4) return row.value;
    if (parts[0] == parts[1] && parts[1] == parts[2] && parts[2] == parts[3]) return parts[0];
    if (row.valueType.find(L"Thickness") != std::wstring::npos && parts[0] == parts[2] && parts[1] == parts[3])
        return parts[0] + L"," + parts[1];
    return row.value;
}

// Flat label/value rows with the editor inline: one click or Tab reaches a value, and the row never re-states
// it. Editors stretch to the value column; the label column is shared with the Source row so every value starts
// on the same x.
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

static std::wstring SelRevealButton(size_t i, const std::wstring& prop, const std::wstring& what,
                                    const DevToolsPalette& pal, int column)
{
    const std::wstring n = std::to_wstring(i);
    return L"<Button x:Name=\"DevToolsSelReveal" + n + L"\" Grid.Column=\"" + std::to_wstring(column) + L"\""
           L" Width=\"28\" Height=\"28\" MinWidth=\"0\" MinHeight=\"0\" Padding=\"0\" VerticalAlignment=\"Center\""
           L" Background=\"Transparent\" BorderThickness=\"0\""
           L" ToolTipService.ToolTip=\"" + what + L" in DevTools\""
           L" AutomationProperties.AutomationId=\"DevToolsSelReveal" + n + L"\""
           L" AutomationProperties.Name=\"" + what + L" for " + DevToolsXmlEscape(prop) + L" in DevTools\">"
           L"<FontIcon FlowDirection=\"LeftToRight\" Glyph=\"&#xE8A7;\" FontSize=\"12\" Foreground=\"" +
           pal.textSecondary + L"\"/></Button>";
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
        const bool linked = SelRowHasExpression(r);
        const bool binding = SelRowIsBinding(r);
        const std::wstring display = SelectionPreviewValue(r);
        const std::wstring editName = L"DevToolsSelEdit" + n;

        m += L"<Grid ColumnSpacing=\"12\" AutomationProperties.AutomationId=\"DevToolsSelRow" + n + L"\">";
        m += kSelLabelColumn;
        m += L"<TextBlock x:Name=\"DevToolsSelLabel" + n + L"\" Text=\"" + DevToolsXmlEscape(r.name) + L"\""
             L" Style=\"{StaticResource CaptionTextBlockStyle}\" Margin=\"0,8,0,0\" VerticalAlignment=\"Top\""
             L" TextWrapping=\"NoWrap\" TextTrimming=\"CharacterEllipsis\" Foreground=\"" + pal.textSecondary + L"\""
             L" ToolTipService.ToolTip=\"" + DevToolsXmlEscape(r.name) + L"\"/>";
        m += L"<StackPanel x:Name=\"DevToolsSelValueCol" + n + L"\" Grid.Column=\"1\" Spacing=\"4\">";

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
            m += L"\" AutomationProperties.Name=\"" + DevToolsXmlEscape(r.name) + L"\"/>";
        } else if (SelRowUsesSlider(r)) {
            m += L"<Grid ColumnSpacing=\"12\"><Grid.ColumnDefinitions><ColumnDefinition Width=\"*\"/>"
                 L"<ColumnDefinition Width=\"36\"/></Grid.ColumnDefinitions>";
            m += L"<Slider x:Name=\"" + editName + L"\" Value=\"" + DevToolsXmlEscape(r.value) + L"\""
                 L" Minimum=\"0\" Maximum=\"1\" StepFrequency=\"0.01\" SmallChange=\"0.01\" LargeChange=\"0.1\""
                 L" VerticalAlignment=\"Center\" AutomationProperties.AutomationId=\"" + editName + L"\""
                 L" AutomationProperties.Name=\"Opacity\"/>";
            m += L"<TextBlock x:Name=\"DevToolsSelValue" + n + L"\" Grid.Column=\"1\" Text=\"" + XmlEscapeLines(display) + L"\""
                 L" VerticalAlignment=\"Center\" HorizontalAlignment=\"Right\" Foreground=\"" + pal.textPrimary + L"\""
                 L" AutomationProperties.AccessibilityView=\"Raw\"/>";
            m += L"</Grid>";
        } else if (r.editKind == L"number") {
            const bool autoValue = SelRowAllowsAuto(r) && !TrySelectionNumber(r.value, nullptr);
            m += L"<NumberBox x:Name=\"" + editName + L"\" Value=\"" +
                 (autoValue ? std::wstring(L"NaN") : DevToolsXmlEscape(r.value)) + L"\""
                 L" HorizontalAlignment=\"Stretch\" MinHeight=\"32\" SpinButtonPlacementMode=\"Compact\" SmallChange=\"" +
                 (r.name == L"Opacity" ? std::wstring(L"0.1") : std::wstring(L"1")) + L"\"";
            if (SelRowAllowsAuto(r))
                m += L" PlaceholderText=\"Auto\" ToolTipService.ToolTip=\"Empty means Auto\"";
            m += L" AutomationProperties.AutomationId=\"" + editName + L"\""
                 L" AutomationProperties.Name=\"" + DevToolsXmlEscape(r.name) + L"\"/>";
        } else if (const auto choices = SelRowMembers(r); !choices.empty() && SelRowUsesSegments(r)) {
            const int sel = DevToolsEnumIndexOf(choices, r.value);
            m += L"<Border Height=\"32\" Padding=\"2\" CornerRadius=\"4\" BorderThickness=\"1\""
                 L" Background=\"{ThemeResource ControlAltFillColorSecondaryBrush}\""
                 L" BorderBrush=\"{ThemeResource ControlStrokeColorDefaultBrush}\">";
            m += L"<Grid x:Name=\"" + editName + L"\" AutomationProperties.AutomationId=\"" + editName + L"\""
                 L" AutomationProperties.Name=\"" + DevToolsXmlEscape(r.name) + L"\"><Grid.ColumnDefinitions>";
            for (size_t k = 0; k < choices.size(); ++k) m += L"<ColumnDefinition Width=\"*\"/>";
            m += L"</Grid.ColumnDefinitions>";
            for (size_t k = 0; k < choices.size(); ++k) {
                const std::wstring seg = L"DevToolsSelSeg" + n + L"_" + std::to_wstring(k);
                m += L"<RadioButton x:Name=\"" + seg + L"\" Grid.Column=\"" + std::to_wstring(k) + L"\""
                     L" GroupName=\"DevToolsSelSeg" + n + L"\" Style=\"{StaticResource DevToolsSegment}\""
                     L" Content=\"" + DevToolsXmlEscape(choices[k]) + L"\" IsChecked=\"" +
                     ((int)k == sel ? L"True" : L"False") + L"\""
                     L" AutomationProperties.AutomationId=\"" + seg + L"\""
                     L" AutomationProperties.Name=\"" + DevToolsXmlEscape(r.name + L" " + choices[k]) + L"\"/>";
            }
            m += L"</Grid></Border>";
        } else if (!choices.empty()) {
            const int sel = DevToolsEnumIndexOf(choices, r.value);
            m += L"<ComboBox x:Name=\"" + editName + L"\" HorizontalAlignment=\"Stretch\" MinHeight=\"32\"";
            if (sel >= 0) m += L" SelectedIndex=\"" + std::to_wstring(sel) + L"\"";
            m += L" AutomationProperties.AutomationId=\"" + editName + L"\" AutomationProperties.Name=\"" +
                 DevToolsXmlEscape(r.name) + L"\">";
            for (const auto& mem : choices) m += L"<ComboBoxItem Content=\"" + DevToolsXmlEscape(mem) + L"\"/>";
            m += L"</ComboBox>";
        } else {
            const bool color = r.editKind == L"color";
            const bool fields = r.editKind == L"fields";
            const bool reveal = color || fields;
            if (reveal) {
                m += L"<Grid ColumnSpacing=\"8\"><Grid.ColumnDefinitions>";
                if (color) m += L"<ColumnDefinition Width=\"Auto\"/>";
                m += L"<ColumnDefinition Width=\"*\"/><ColumnDefinition Width=\"Auto\"/></Grid.ColumnDefinitions>";
            }
            if (color)
                m += L"<Border x:Name=\"DevToolsSelSwatch" + n + L"\" Width=\"20\" Height=\"20\" CornerRadius=\"4\""
                     L" VerticalAlignment=\"Center\" Background=\"" +
                     (SelectionHasColorSwatch(r) ? r.value : std::wstring(L"Transparent")) + L"\""
                     L" BorderThickness=\"1\" BorderBrush=\"" + pal.rim + L"\" AutomationProperties.AccessibilityView=\"Raw\"/>";
            m += L"<TextBox x:Name=\"" + editName + L"\"";
            if (color) m += L" Grid.Column=\"1\"";
            // Before Text: XAML applies attributes in order, and a single-line box keeps only the first line.
            if (DevToolsEditText::IsMultiline(r.value))
                m += L" AcceptsReturn=\"True\" TextWrapping=\"Wrap\" MaxHeight=\"104\"";
            m += L" Text=\"" + XmlEscapeLines(fields ? SelCompactFields(r) : r.value) + L"\" HorizontalAlignment=\"Stretch\" MinHeight=\"32\""
                 L" AutomationProperties.AutomationId=\"" + editName + L"\" AutomationProperties.Name=\"" + DevToolsXmlEscape(r.name) + L"\"";
            if (color) m += L" PlaceholderText=\"#AARRGGBB\" ToolTipService.ToolTip=\"Color as #AARRGGBB\"";
            else if (fields) m += L" ToolTipService.ToolTip=\"One value for all sides, two for left/right and top/bottom, or four\"";
            else if (DevToolsEditText::IsMultiline(r.value)) m += L" ToolTipService.ToolTip=\"Ctrl+Enter to apply\"";
            else if (r.value.empty()) m += L" PlaceholderText=\"Empty\"";
            m += L"/>";
            if (reveal) {
                m += SelRevealButton(i, r.name, color ? L"Pick a color" : L"Edit each side", pal, color ? 2 : 1);
                m += L"</Grid>";
            }
        }

        if (linked) {
            const bool confirmable = !rowReadOnly && r.source == L"Binding";
            m += L"<Grid ColumnSpacing=\"6\" MinHeight=\"28\"><Grid.ColumnDefinitions><ColumnDefinition Width=\"Auto\"/>"
                 L"<ColumnDefinition Width=\"*\"/><ColumnDefinition Width=\"Auto\"/><ColumnDefinition Width=\"Auto\"/>"
                 L"</Grid.ColumnDefinitions>"
                 L"<FontIcon FlowDirection=\"LeftToRight\" Glyph=\"" + std::wstring(binding ? L"&#xE71B;" : L"&#xE790;") +
                 L"\" FontSize=\"12\" VerticalAlignment=\"Center\" Foreground=\"" + pal.textSecondary + L"\""
                 L" AutomationProperties.AccessibilityView=\"Raw\"/>";
            m += L"<TextBlock Grid.Column=\"1\"";
            if (confirmable) m += L" x:Name=\"DevToolsSelExpr" + n + L"\"";
            // The panel names what the value follows; the full expression is in the tooltip and the inspector.
            const std::wstring shownExpr = exprUnknown ? std::wstring(L"Bound (expression unavailable)")
                : binding ? SelShortExpression(r)
                : (!r.authoredKey.empty() ? r.authoredKey : expr);
            m += L" Text=\"" + DevToolsXmlEscape(shownExpr) +
                 L"\" FontSize=\"12\" TextTrimming=\"CharacterEllipsis\" VerticalAlignment=\"Center\"";
            if (exprUnknown) {
                m += L" FontStyle=\"Italic\" Foreground=\"" + pal.textSecondary + L"\""
                     L" ToolTipService.ToolTip=\"This value comes from a binding, but its expression could not be read\"/>";
            } else {
                m += L" FontFamily=\"Consolas\" IsTextSelectionEnabled=\"True\" Foreground=\"" + pal.textSecondary + L"\""
                     L" ToolTipService.ToolTip=\"" + DevToolsXmlEscape(expr) + L"\"/>";
            }
            if (binding) {
                m += L"<TextBlock x:Name=\"DevToolsSelStatus" + n + L"\" Grid.Column=\"2\" Text=\"Checking&#x2026;\""
                     L" FontSize=\"12\" MaxWidth=\"132\" TextTrimming=\"CharacterEllipsis\" VerticalAlignment=\"Center\""
                     L" Foreground=\"" + pal.textSecondary + L"\" AutomationProperties.LiveSetting=\"Polite\""
                     L" AutomationProperties.Name=\"" + DevToolsXmlEscape(r.name) + L" binding status\"/>";
                m += L"<TextBlock x:Name=\"DevToolsSelStatusBad" + n + L"\" Grid.Column=\"2\" Visibility=\"Collapsed\""
                     L" FontSize=\"12\" MaxWidth=\"132\" TextTrimming=\"CharacterEllipsis\" VerticalAlignment=\"Center\""
                     L" Foreground=\"{ThemeResource SystemFillColorCriticalBrush}\" AutomationProperties.LiveSetting=\"Polite\""
                     L" AutomationProperties.Name=\"" + DevToolsXmlEscape(r.name) + L" binding status\"/>";
                m += SelRevealButton(i, r.name, L"Binding details", pal, 3);
            }
            m += L"</Grid>";
            if (confirmable) {
                m += L"<Button x:Name=\"DevToolsSelConfirm" + n + L"\" Visibility=\"Collapsed\""
                     L" Padding=\"8,4,8,4\" MinWidth=\"0\" HorizontalAlignment=\"Left\""
                     L" ToolTipService.ToolTip=\"" + DevToolsXmlEscape(r.name) + L" is bound. Committing replaces that"
                     L" binding with a literal value.\""
                     L" AutomationProperties.AutomationId=\"DevToolsSelConfirm" + n + L"\""
                     L" AutomationProperties.Name=\"Replace the binding on " + DevToolsXmlEscape(r.name) + L"\">"
                     L"<TextBlock x:Name=\"DevToolsSelConfirmText" + n + L"\" Text=\"\""
                     L" Style=\"{StaticResource CaptionTextBlockStyle}\" TextWrapping=\"Wrap\"/></Button>";
            }
            if (!rowReadOnly) {
                m += L"<TextBlock x:Name=\"DevToolsSelCaution" + n + L"\" Visibility=\"Collapsed\" FontSize=\"12\""
                     L" TextWrapping=\"Wrap\" Foreground=\"" + pal.caution + L"\" AutomationProperties.LiveSetting=\"Polite\""
                     L" Text=\"" + DevToolsXmlEscape(L"Editing replaces " + SelShortExpression(r) + L" until the app restarts.") + L"\"/>";
            }
        }
        m += L"</StackPanel></Grid>";
    }
    return m;
}

static void DevToolsSelWireClick(IInspectable* root, const std::wstring& name, DevToolsSinkBase* sink)
{
    IInspectable* el = DevToolsSelFindName(root, name.c_str());
    if (!el) { sink->Release(); return; }
    void* bb = nullptr;
    __int64 tok = 0;
    if (SUCCEEDED(el->QueryInterface(DevToolsIid<DevToolsXCP::IButtonBase>(), &bb)) && bb) {
        if (FAILED(DevToolsAddClick(bb, sink, &tok))) sink->Release();
        reinterpret_cast<IUnknown*>(bb)->Release();
    } else {
        sink->Release();
    }
    el->Release();
}

// Show the row's caution while any of its editors has focus. GotFocus and LostFocus bubble, so the value
// column hears every editor inside it.
static void DevToolsSelWireCaution(IInspectable* root, size_t i)
{
    const std::wstring n = std::to_wstring(i);
    IInspectable* col = DevToolsSelFindName(root, (L"DevToolsSelValueCol" + n).c_str());
    if (!col) return;
    void* ui = nullptr;
    if (SUCCEEDED(col->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &ui)) && ui) {
        auto* on = new SelCautionSink(); on->Init(L"DevToolsSelCaution" + n, true);
        auto* off = new SelCautionSink(); off->Init(L"DevToolsSelCaution" + n, false);
        __int64 tok = 0;
        if (FAILED(DevToolsAddGotFocus(ui, on, &tok))) on->Release();
        if (FAILED(DevToolsAddLostFocus(ui, off, &tok))) off->Release();
        reinterpret_cast<IUnknown*>(ui)->Release();
    }
    col->Release();
}

// Wire each quick-edit row's editor to a commit-on-leave sink: resolve DevToolsSelEdit{i} by FindName
// (transferring its ref into g_selRowBoxes for lifetime + teardown release), then hook the editor's own change or
// leave event so the value commits.
// Row sinks keep AddRef'd editors so blur and selection teardown can release them deterministically.
static void DevToolsSelWireRows(IInspectable* root, const std::vector<DevToolsCardRow>& rows)
{
    for (size_t i = 0; i < rows.size(); ++i) {
        const std::wstring n = std::to_wstring(i);
        {
            auto* reveal = new SelRevealSink(); reveal->Init(rows[i].name);
            DevToolsSelWireClick(root, L"DevToolsSelReveal" + n, reveal);
        }
        if (!SelRowCanEdit(rows[i])) continue;
        const std::wstring editName = L"DevToolsSelEdit" + n;
        IInspectable* ed = DevToolsSelFindName(root, editName.c_str());
        if (!ed) {
// Same two-sided contract as the window's props pane: BuildSelRowsMarkup decides which rows get an editor and
// what it is named, this loop decides what to look for.
            DevToolsOverlayLog(L"overlay.sel wire MISS: editable row '%s' (type '%s') has no editor %s -- it renders but will never commit",
                          rows[i].name.c_str(), rows[i].type.c_str(), editName.c_str());
            continue;
        }
        if (SelRowHasExpression(rows[i])) DevToolsSelWireCaution(root, i);
        std::vector<std::wstring> members = SelRowMembers(rows[i]);
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
                sink->allowAuto = SelRowAllowsAuto(rows[i]);
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
        } else if (!members.empty() && SelRowUsesSegments(rows[i])) {
            const int cur = DevToolsEnumIndexOf(members, rows[i].value);
            const std::wstring orig = (cur >= 0) ? members[(size_t)cur] : rows[i].value;
            std::vector<IInspectable*> buttons;
            for (size_t k = 0; k < members.size(); ++k) {
                IInspectable* b = DevToolsSelFindName(root, (L"DevToolsSelSeg" + n + L"_" + std::to_wstring(k)).c_str());
                if (!b) break;
                buttons.push_back(b);
            }
            if (buttons.size() == members.size()) {
                auto* s = new SelRowSink();
                s->InitSegments(rows[i].name, rows[i].type, orig, members, buttons);
                HRESULT hr = S_OK;
                for (IInspectable* b : buttons) {
                    void* tb = nullptr;
                    __int64 tok = 0;
                    if (SUCCEEDED(hr = b->QueryInterface(DevToolsIid<DevToolsXCP::IToggleButton>(), &tb)) && tb) {
                        hr = DevToolsAbi<DevToolsXCP::IToggleButton>(tb)->add_Checked(s, DevToolsAbiTok(&tok));
                        reinterpret_cast<IUnknown*>(tb)->Release();
                    }
                    if (FAILED(hr)) break;
                    g_selRowBoxes.push_back(b);
                }
                DevToolsOverlayLog(L"overlay.sel row %s segment wire hr=0x%08X", rows[i].name.c_str(), hr);
                if (SUCCEEDED(hr)) g_selRowSinks.push_back(s);
                else SetSelectionOperationError(rows[i].name, L"Couldn't connect the editor. Reopen this panel.");
            } else {
                for (IInspectable* b : buttons) b->Release();
                SetSelectionOperationError(rows[i].name, L"Couldn't connect the editor. Reopen this panel.");
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
        if (rows[i].source != L"Binding") continue;
        const std::wstring cName = L"DevToolsSelConfirm" + n;
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
        if (IInspectable* xl = DevToolsSelFindName(root, (L"DevToolsSelExpr" + n).c_str())) {
            xl->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), &refs.text);
            xl->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &refs.textTb);
            xl->Release();
        }
        if (IInspectable* xt = DevToolsSelFindName(root, (L"DevToolsSelConfirmText" + n).c_str())) {
            xt->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &refs.label);
            xt->Release();
        }
        g_selExprRefs.push_back(refs);
    }
}
