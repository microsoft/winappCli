// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsWindow.cpp; included into that translation unit, not compiled on its own.

// Group a curated property into one of four inspector sections so the pane reads by concern instead of one
// long list. Unknown properties fall into "Other". Pure name lookup (0 ABI); the grouping is presentation-only
// and never reorders the editable-index assignment (see BuildPropsFragment).
static int PropGroupIndex(const std::wstring& n)
{
    static const wchar_t* layout[] = { L"Width", L"Height", L"Margin", L"Padding", L"HorizontalAlignment",
        L"VerticalAlignment", L"MinWidth", L"MinHeight", L"MaxWidth", L"MaxHeight", L"Grid.Row", L"Grid.Column" };
    static const wchar_t* appearance[] = { L"Foreground", L"Background", L"BorderBrush", L"BorderThickness",
        L"Opacity", L"Visibility", L"CornerRadius", L"Fill", L"Stroke" };
    static const wchar_t* text[] = { L"Text", L"Content", L"FontSize", L"FontFamily", L"FontWeight",
        L"FontStyle", L"CharacterSpacing", L"TextWrapping" };
    // Behavior is its own OPEN group because "Other" is collapsed by default. IsEnabled is the curated set's
    // one boolean, and it was landing in that collapsed group - so the boolean editor shipped but nobody could
    // reach it without expanding a section named "Other". "Why is this control dead?" is a first-class
    // debugging question and deserves a visible row.
    static const wchar_t* behavior[] = { L"IsEnabled", L"IsChecked", L"IsReadOnly", L"IsTabStop",
        L"IsHitTestVisible" };
    for (auto k : layout)     if (n == k) return 0;
    for (auto k : appearance) if (n == k) return 1;
    for (auto k : text)       if (n == k) return 2;
    for (auto k : behavior)   if (n == k) return 3;
    return 4;
}
static const wchar_t* GroupTitle(int g)
{
    switch (g) {
        case 0: return L"Layout"; case 1: return L"Appearance"; case 2: return L"Text";
        case 3: return L"Behavior"; default: return L"Other";
    }
}

static std::vector<std::wstring> EnumMembersFor(const std::wstring& type) { return DevToolsEnumMembersFor(type); }

static int PropGroupIndexForView(const DevToolsCardRow& r)
{
    if (!ShowingSetView()) return PropGroupIndex(r.name);
    return (!r.authoredKind.empty() || SourceIsSetHere(r.source)) ? 0 : 1;
}
static const wchar_t* GroupTitleForView(int g)
{
    if (!ShowingSetView()) return GroupTitle(g);
    return g == 0 ? L"Authored on this element" : L"From a style";
}
static int PropGroupCount() { return ShowingSetView() ? 2 : 5; }

static bool OnlySetKeeps(const DevToolsCardRow& r)
{
    if (!ShowingSetView()) return true;
    return SourceIsSetHere(r.source) || !r.authoredKind.empty()
        || r.source == L"Style" || r.source == L"Built-in style";
}

static std::wstring PropShowAllLabel(const std::vector<DevToolsCardRow>& rows)
{
    if (g_propShowAll) return L"Show common properties only";
    return L"Show all " + std::to_wstring(rows.size()) + L" properties";
}

// Is a row rendered in the CURRENT pane? ONE rule, so the pane's item list and BuildEditMap's numbering
// cannot disagree about which rows exist. A child is rendered exactly when its parent is, so callers pass the
// OWNING top-level row either way.
static bool PropRowIsRendered(const DevToolsCardRow& owner)
{
    if (!OnlySetKeeps(owner)) return false;
    if (ShowingSetView() || g_propShowAll || !g_propFilterQuery.empty()) return true;
    return DevToolsRead_IsCoreProp(owner.name) || SourceIsSetHere(owner.source) || !owner.authoredKind.empty();
}

static int EnumIndexOf(const std::vector<std::wstring>& members, const std::wstring& value)
{
    return DevToolsEnumIndexOf(members, value);
}

static std::vector<std::wstring> SplitFields(const std::wstring& v, size_t count)
{
    std::vector<std::wstring> parts; std::wstring cur;
    for (wchar_t c : v) {
        if (c == L',') { parts.push_back(cur); cur.clear(); }
        else if (c != L' ') cur += c;
    }
    parts.push_back(cur);
    std::vector<std::wstring> out(count);
    if (count == 4 && parts.size() == 1)      { out[0] = out[1] = out[2] = out[3] = parts[0]; }
    else if (count == 4 && parts.size() == 2) { out[0] = out[2] = parts[0]; out[1] = out[3] = parts[1]; }
    else if (parts.size() >= count)           { for (size_t k = 0; k < count; ++k) out[k] = parts[k]; }
    else {
        for (size_t k = 0; k < parts.size() && k < count; ++k) out[k] = parts[k];
    }
    for (size_t k = 0; k < count; ++k) if (out[k].empty()) out[k] = L"0";
    return out;
}

// Prefer authoredKind for origin; fall back to valueSource when no authored expression exists.
// A resource-resolved Local value is not necessarily an authored literal.
static std::wstring OriginLabel(const DevToolsCardRow& r)
{
    if (r.authoredKind == L"themeResource")   return L"Theme resource";
    if (r.authoredKind == L"staticResource")  return L"Static resource";
    if (r.authoredKind == L"xBind")           return L"x:Bind";
    if (r.authoredKind == L"binding")         return L"Binding";
    if (r.authoredKind == L"templateBinding") return L"TemplateBinding";
    if (r.authoredKind == L"customMarkup")    return L"Markup";
    return r.source;
}

// One line explaining what the origin means, used as the pill's tooltip. Written per KIND rather than per
// source, because "not set here -- value comes from Theme resource" reads as nonsense.
static std::wstring OriginTooltip(const DevToolsCardRow& r)
{
    if (r.authoredKind == L"themeResource")
        return L"Set on this element from a theme resource. The value follows the app's theme, and the brush "
               L"itself is shared with everything else that uses the same key.";
    if (r.authoredKind == L"staticResource")
        return L"Set on this element from a static resource, resolved once when the XAML loaded.";
    if (r.authoredKind == L"xBind")
        return L"Set on this element by a compiled {x:Bind}. The runtime applies it as an ordinary local "
               L"value, which is why the precedence chain reports it as Local.";
    if (r.authoredKind == L"binding")     return L"Set on this element by a runtime {Binding}.";
    if (r.authoredKind == L"templateBinding")
        return L"Supplied by the control template, from the templated parent's property of the same name.";
    if (r.authoredKind == L"customMarkup") return L"Set on this element by a markup extension.";
    if (SourceIsSetHere(r.source))         return L"Set on this element (in code / XAML)";
    return L"Not set here. Value comes from " + r.source;
}

static std::wstring LayoutNumW(double v)
{
    if (!(v == v)) return L"\u2014";
    wchar_t buf[32];
    _snwprintf_s(buf, _countof(buf), _TRUNCATE, L"%.2f", v);
    std::wstring s = buf;
    if (s.find(L'.') != std::wstring::npos) {
        while (!s.empty() && s.back() == L'0') s.pop_back();
        if (!s.empty() && s.back() == L'.') s.pop_back();
    }
    return s.empty() ? L"0" : s;
}

static std::wstring LayoutLine(const std::wstring& label, const std::wstring& value, bool emphasise)
{
    std::wstring m = L"<Grid Margin=\"0,0,0,2\"><Grid.ColumnDefinitions>"
                     L"<ColumnDefinition Width=\"104\"/><ColumnDefinition Width=\"*\"/>"
                     L"</Grid.ColumnDefinitions>";
    m += L"<TextBlock Grid.Column=\"0\" Text=\"" + DevToolsXmlEscape(label) + L"\" FontSize=\"12\""
         L" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\" TextWrapping=\"NoWrap\""
         L" TextTrimming=\"CharacterEllipsis\"/>";
    m += L"<TextBlock Grid.Column=\"1\" Text=\"" + DevToolsXmlEscape(value) + L"\" FontSize=\"12\""
         L" FontFamily=\"Consolas\" TextWrapping=\"NoWrap\" TextTrimming=\"CharacterEllipsis\" Foreground=\"";
    m += emphasise ? L"{ThemeResource TextFillColorPrimaryBrush}" : L"{ThemeResource TextFillColorSecondaryBrush}";
    m += L"\"/></Grid>";
    return m;
}

// Fixed box-model rings: margin, border, padding, content. Render nonzero, zero and unavailable
// as distinct states, preserving four edge positions. Derive inward sizes from RenderSize;
// only margin lies outside it. Stroke patterns preserve ring identity in High Contrast.
struct DevToolsRing
{
    bool present = false;
    double l = 0, t = 0, r = 0, b = 0;
    bool Zero() const { return fabs(l) < 0.005 && fabs(t) < 0.005 && fabs(r) < 0.005 && fabs(b) < 0.005; }
};

static DevToolsRing RingFromRows(const std::vector<DevToolsCardRow>& rows, const wchar_t* name)
{
    DevToolsRing g;
    for (const auto& r : rows) {
        if (r.name != name) continue;
        if (r.value.empty()) return g;
        const std::vector<std::wstring> f = SplitFields(r.value, 4);
        double v[4] = { 0, 0, 0, 0 };
        for (size_t k = 0; k < 4; ++k) {
            wchar_t* end = nullptr;
            v[k] = wcstod(f[k].c_str(), &end);
            const bool finite = (v[k] == v[k]) && v[k] > -1e18 && v[k] < 1e18;
            if (end == f[k].c_str() || !finite) return g;
        }
        g.present = true; g.l = v[0]; g.t = v[1]; g.r = v[2]; g.b = v[3];
        return g;
    }
    return g;
}

static std::wstring BoxSizeText(double w, double h)
{
    return LayoutNumW(w > 0 ? w : 0) + L" \u00D7 " + LayoutNumW(h > 0 ? h : 0);
}

static bool SystemHighContrast()
{
    HIGHCONTRASTW hc{};
    hc.cbSize = sizeof(hc);
    if (!SystemParametersInfoW(SPI_GETHIGHCONTRAST, sizeof(hc), &hc, 0)) return false;
    return (hc.dwFlags & HCF_HIGHCONTRASTON) != 0;
}

// One ring around `inner`. The numbers sit in REAL CELLS of a 3x3 grid whose outer tracks are the band
// widths, so a number is centred in the band it measures and cannot overdraw the ring inside it.
static std::wstring RingMarkup(const wchar_t* label, const DevToolsRing& g, const wchar_t* dash, double strokeW,
                               const std::wstring& ink, const std::wstring& dim, const std::wstring& tint,
                               const std::wstring& boxSize, bool hc, const std::wstring& inner)
{
    // Callers do not build a ring for a property the type lacks, so `present` is always true here.
    const bool zero = g.Zero();
    // Wide enough for the widest side value, and never so narrow that a two-digit number touches a stroke.
    const size_t widest = (std::max)(LayoutNumW(g.l).size(), LayoutNumW(g.r).size());
    const double bandW = (std::max)(20.0, 5.0 * (double)widest + 10.0);
    const std::wstring bw = LayoutNumW(bandW);
    const std::wstring numInk = zero ? dim : ink;

    std::wstring autoName = zero
        ? std::wstring(label) + L" 0 on all sides"
        : std::wstring(label) + L" " + LayoutNumW(g.l) + L" left, " + LayoutNumW(g.t) + L" top, " +
          LayoutNumW(g.r) + L" right, " + LayoutNumW(g.b) + L" bottom";
    if (!boxSize.empty()) autoName += L", box " + boxSize;

    auto cell = [&numInk](int row, int col, const std::wstring& text) {
        return L"<TextBlock Grid.Row=\"" + std::to_wstring(row) + L"\" Grid.Column=\"" + std::to_wstring(col) +
               L"\" Text=\"" + DevToolsXmlEscape(text) + L"\" FontSize=\"9\" FontFamily=\"Consolas\" Foreground=\"" + numInk +
               L"\" HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\""
               L" AutomationProperties.AccessibilityView=\"Raw\"/>";
    };

    std::wstring body = L"<Grid AutomationProperties.Name=\"" + DevToolsXmlEscape(autoName) + L"\">"
        L"<Grid.ColumnDefinitions><ColumnDefinition Width=\"" + bw + L"\"/><ColumnDefinition Width=\"*\"/>"
        L"<ColumnDefinition Width=\"" + bw + L"\"/></Grid.ColumnDefinitions>"
        L"<Grid.RowDefinitions><RowDefinition Height=\"14\"/><RowDefinition Height=\"*\"/>"
        L"<RowDefinition Height=\"12\"/></Grid.RowDefinitions>";
    body += L"<TextBlock Grid.Row=\"0\" Grid.ColumnSpan=\"3\" Text=\"" + std::wstring(label) +
            L"\" FontSize=\"9\" Foreground=\"" + dim + L"\" HorizontalAlignment=\"Left\""
            L" VerticalAlignment=\"Center\" Margin=\"4,0,0,0\""
            L" AutomationProperties.AccessibilityView=\"Raw\"/>";
    if (!boxSize.empty())
        body += L"<TextBlock Grid.Row=\"0\" Grid.ColumnSpan=\"3\" Text=\"" + DevToolsXmlEscape(boxSize) +
                L"\" FontSize=\"9\" FontFamily=\"Consolas\" Foreground=\"" + dim + L"\" Opacity=\"0.85\""
                L" HorizontalAlignment=\"Right\" VerticalAlignment=\"Center\" Margin=\"0,0,5,0\""
                L" AutomationProperties.AccessibilityView=\"Raw\"/>";
    body += cell(0, 1, LayoutNumW(g.t));
    body += cell(2, 1, LayoutNumW(g.b));
    body += cell(1, 0, LayoutNumW(g.l));
    body += cell(1, 2, LayoutNumW(g.r));
    body += L"<Grid Grid.Row=\"1\" Grid.Column=\"1\">" + inner + L"</Grid></Grid>";

    // In High Contrast, emit NO fill at all: no tint and no opaque base. The window's own background shows
    // through and the strokes carry the ring identity, which is what the design claimed and what the first
    // rendering disproved. Outside HC the opaque base is required, because nested translucent fills compound
    // and the innermost box ends up wearing every ring's tint at once.
    std::wstring fill;
    if (!hc) {
        fill = L"<Rectangle Fill=\"{ThemeResource SolidBackgroundFillColorBaseBrush}\""
               L" HorizontalAlignment=\"Stretch\" VerticalAlignment=\"Stretch\" IsHitTestVisible=\"False\"/>";
        if (!tint.empty() && !zero)
            fill += L"<Rectangle Fill=\"" + tint + L"\" Opacity=\"0.15\" HorizontalAlignment=\"Stretch\""
                    L" VerticalAlignment=\"Stretch\" IsHitTestVisible=\"False\"/>";
    }

    const double op = zero ? 0.62 : 1.0;
    std::wstring open = L"<Grid Opacity=\"" + LayoutNumW(op) + L"\">";
    if (dash) {
        return open + fill + L"<Rectangle Stroke=\"" + ink + L"\" StrokeThickness=\"" + LayoutNumW(strokeW) +
               L"\" StrokeDashArray=\"" + dash + L"\" StrokeDashCap=\"Flat\""
               L" HorizontalAlignment=\"Stretch\" VerticalAlignment=\"Stretch\"/>" + body + L"</Grid>";
    }
    return open + L"<Border BorderThickness=\"" + LayoutNumW(strokeW) + L"\" BorderBrush=\"" + ink + L"\">"
           L"<Grid>" + fill + body + L"</Grid></Border></Grid>";
}

// MinWidth/MinHeight keep tiny boxes visible while star sizing preserves relative position.
static std::wstring BuildFitThumb(const DevToolsWindowLayout& L, const std::wstring& ink, const std::wstring& dim)
{
    if (!L.haveParentBox || !L.haveRender || L.parentW <= 0.5 || L.parentH <= 0.5) return std::wstring();
    const double aw = (double)L.parentW, ah = (double)L.parentH;
    const double w = (double)L.renderW, h = (double)L.renderH;
    const double x = L.haveInParent ? (double)L.inParentX : 0.0;
    const double y = L.haveInParent ? (double)L.inParentY : 0.0;

    auto star = [](double v, double total, int floorU) {
        double u = (total > 0 ? (v / total) : 0) * 1000.0;
        if (u < 0) u = 0;
        if (u > 1000) u = 1000;
        int i = (int)(u + 0.5);
        return i < floorU ? floorU : i;
    };
    const int cx = star(x, aw, 0), cw = star(w, aw, 40);
    const int ca = (std::max)(1000 - cx - cw, 0);
    const int ry = star(y, ah, 0), rh = star(h, ah, 40);
    const int rb = (std::max)(1000 - ry - rh, 0);

    const bool fills = fabs(w - aw) < 0.5 && fabs(h - ah) < 0.5 && x < 0.5 && y < 0.5;
    std::wstring name = fills
        ? L"This element fills the " + BoxSizeText(aw, ah) + L" its parent offered"
        : L"This element occupies " + BoxSizeText(w, h) + L" of the " + BoxSizeText(aw, ah) +
          L" its parent offered, at " + LayoutNumW(x) + L", " + LayoutNumW(y);

    std::wstring m = L"<Grid Width=\"46\" Height=\"46\" VerticalAlignment=\"Top\""
                     L" AutomationProperties.Name=\"" + DevToolsXmlEscape(name) + L"\">";
    m += L"<Rectangle Stroke=\"" + dim + L"\" StrokeThickness=\"1\" StrokeDashArray=\"2,2\""
         L" StrokeDashCap=\"Flat\" HorizontalAlignment=\"Stretch\" VerticalAlignment=\"Stretch\"/>";
    m += L"<Grid Margin=\"2\"><Grid.ColumnDefinitions>"
         L"<ColumnDefinition Width=\"" + std::to_wstring(cx) + L"*\"/>"
         L"<ColumnDefinition Width=\"" + std::to_wstring(cw) + L"*\"/>"
         L"<ColumnDefinition Width=\"" + std::to_wstring(ca) + L"*\"/></Grid.ColumnDefinitions>"
         L"<Grid.RowDefinitions>"
         L"<RowDefinition Height=\"" + std::to_wstring(ry) + L"*\"/>"
         L"<RowDefinition Height=\"" + std::to_wstring(rh) + L"*\"/>"
         L"<RowDefinition Height=\"" + std::to_wstring(rb) + L"*\"/></Grid.RowDefinitions>";
    m += L"<Border Grid.Column=\"1\" Grid.Row=\"1\" Background=\"" + ink + L"\" Opacity=\"0.85\""
         L" MinWidth=\"3\" MinHeight=\"3\"/>";
    m += L"</Grid></Grid>";

    std::wstring t = L"<TextBlock FontSize=\"11\" TextWrapping=\"Wrap\" Foreground=\"" + dim + L"\">";
    auto run = [&](const wchar_t* k, const std::wstring& v) {
        return L"<Run Text=\"" + std::wstring(k) + L" \"/><Run Text=\"" + DevToolsXmlEscape(v) +
               L"\" FontFamily=\"Consolas\" FontWeight=\"SemiBold\" Foreground=\"" + ink + L"\"/>";
    };
    t += run(L"offered", BoxSizeText(aw, ah));
    t += L"<LineBreak/>";
    if (fills) t += L"<Run Text=\"this fills it\"/>";
    else {
        t += run(L"this took", BoxSizeText(w, h));
        if (x > 0.5 || y > 0.5) t += run(L" at", LayoutNumW(x) + L", " + LayoutNumW(y));
    }
    t += L"</TextBlock>";

    return L"<Grid Margin=\"0,9,0,0\" ColumnSpacing=\"10\"><Grid.ColumnDefinitions>"
           L"<ColumnDefinition Width=\"Auto\"/><ColumnDefinition Width=\"*\"/></Grid.ColumnDefinitions>" +
           m + L"<ContentControl Grid.Column=\"1\" VerticalAlignment=\"Top\" IsTabStop=\"False\">" + t +
           L"</ContentControl></Grid>";
}

static std::wstring DetailRow(const std::wstring& term, const std::wstring& value,
                             const std::wstring& ink, const std::wstring& dim)
{
    return L"<Grid Margin=\"0,0,0,3\"><Grid.ColumnDefinitions><ColumnDefinition Width=\"78\"/>"
           L"<ColumnDefinition Width=\"*\"/></Grid.ColumnDefinitions>"
           L"<TextBlock Grid.Column=\"0\" Text=\"" + DevToolsXmlEscape(term) + L"\" FontSize=\"11\" Foreground=\"" + dim +
           L"\" TextWrapping=\"NoWrap\" TextTrimming=\"CharacterEllipsis\"/>"
           L"<TextBlock Grid.Column=\"1\" Text=\"" + DevToolsXmlEscape(value) + L"\" FontSize=\"11\" FontFamily=\"Consolas\""
           L" Foreground=\"" + ink + L"\" TextWrapping=\"NoWrap\" TextTrimming=\"CharacterEllipsis\"/></Grid>";
}
static std::wstring DetailGroup(const std::wstring& caption, const std::wstring& dim)
{
    return L"<TextBlock Text=\"" + DevToolsXmlEscape(caption) + L"\" FontSize=\"10\" CharacterSpacing=\"80\""
           L" Foreground=\"" + dim + L"\" Margin=\"0,0,0,5\"/>";
}

// Draw the box model and fit thumbnail, with details behind More. Resolve each theme key once
// before parsing and inline its current literal; invalidate on theme changes for High Contrast.
static std::wstring BuildBoxModel(const DevToolsWindowLayout& L, const std::vector<DevToolsCardRow>& rows,
                                  const std::wstring& ink, const std::wstring& dim, std::wstring& more)
{
    const DevToolsRing margin  = RingFromRows(rows, L"Margin");
    const DevToolsRing border  = RingFromRows(rows, L"BorderThickness");
    const DevToolsRing padding = RingFromRows(rows, L"Padding");

    const double rw = (double)L.renderW, rh = (double)L.renderH;
    const double bw = border.present  ? border.l + border.r : 0,  bh = border.present  ? border.t + border.b : 0;
    const double pw = padding.present ? padding.l + padding.r : 0, ph = padding.present ? padding.t + padding.b : 0;
    const double mw = margin.present  ? margin.l + margin.r : 0,  mh = margin.present  ? margin.t + margin.b : 0;
    const std::wstring sMargin  = BoxSizeText(rw + mw, rh + mh);
    const std::wstring sBorder  = BoxSizeText(rw, rh);
    const std::wstring sPadding = BoxSizeText(rw - bw, rh - bh);
    const std::wstring sContent = BoxSizeText(rw - bw - pw, rh - bh - ph);
    const bool degenerate = (rw - bw - pw) < 0 || (rh - bh - ph) < 0;

    const std::wstring tintMargin  = L"{ThemeResource SystemFillColorCautionBrush}";
    const std::wstring tintPadding = L"{ThemeResource SystemFillColorSuccessBrush}";
    const std::wstring tintContent = L"{ThemeResource AccentFillColorDefaultBrush}";

    const bool hc = SystemHighContrast();
    std::wstring m = L"<Grid MinHeight=\"32\">";
    if (!hc) {
        m += L"<Rectangle Fill=\"{ThemeResource SolidBackgroundFillColorBaseBrush}\""
             L" HorizontalAlignment=\"Stretch\" VerticalAlignment=\"Stretch\"/>";
        m += L"<Rectangle Fill=\"" + tintContent + L"\" Opacity=\"0.17\""
             L" HorizontalAlignment=\"Stretch\" VerticalAlignment=\"Stretch\"/>";
    }
    m += L"<Border BorderThickness=\"1\" BorderBrush=\"" + ink + L"\"/>";
    m += L"<StackPanel Spacing=\"1\" Margin=\"6,4,6,4\" HorizontalAlignment=\"Center\""
         L" VerticalAlignment=\"Center\">";
    m += L"<TextBlock Text=\"content\" FontSize=\"9\" Foreground=\"" + dim +
         L"\" HorizontalAlignment=\"Center\" AutomationProperties.AccessibilityView=\"Raw\"/>";
    m += L"<TextBlock Text=\"" + DevToolsXmlEscape(degenerate ? std::wstring(L"\u2014") : sContent) +
         L"\" FontSize=\"12\" FontFamily=\"Consolas\" FontWeight=\"SemiBold\" Foreground=\"" + ink +
         L"\" HorizontalAlignment=\"Center\" TextTrimming=\"CharacterEllipsis\" TextWrapping=\"NoWrap\""
         L" AutomationProperties.Name=\"content box " + DevToolsXmlEscape(sContent) + L"\"/>";
    m += L"</StackPanel></Grid>";

    if (padding.present) m = RingMarkup(L"padding", padding, L"2,2",  1, ink, dim, tintPadding, sPadding, hc, m);
    if (border.present)  m = RingMarkup(L"border",  border,  nullptr, 2, ink, dim, L"",         sBorder,  hc, m);
    if (margin.present)  m = RingMarkup(L"margin",  margin,  L"1,3",  1, ink, dim, tintMargin,  sMargin,  hc, m);

    std::wstring out = L"<StackPanel Spacing=\"0\" Margin=\"0,8,0,0\""
                       L" AutomationProperties.AutomationId=\"WinAppDevToolsBoxModel\">";
    out += m;
    out += BuildFitThumb(L, ink, dim);
    out += L"</StackPanel>";

    more = DetailGroup(L"THIS ELEMENT", dim);
    if (L.haveDesired) more += DetailRow(L"asked for", BoxSizeText(L.desiredW, L.desiredH), ink, dim);
    if (L.haveRender)  more += DetailRow(L"arranged",  sBorder, ink, dim);
    if (L.haveOffset)  more += DetailRow(L"placed at", LayoutNumW(L.offsetX) + L", " + LayoutNumW(L.offsetY), ink, dim);
    if (L.haveActual && L.haveRender &&
        (fabs(L.actualW - rw) > 0.5 || fabs(L.actualH - rh) > 0.5))
        more += DetailRow(L"text extent", BoxSizeText(L.actualW, L.actualH), ink, dim);
    if (degenerate)
        more += L"<TextBlock Text=\"Content is negative. Padding and border exceed the arranged size.\""
                L" FontSize=\"11\" TextWrapping=\"Wrap\" Margin=\"0,0,0,3\" Foreground=\"" + dim + L"\"/>";

    if (!L.parentType.empty()) {
        more += L"<TextBlock Height=\"8\"/>";
        more += DetailGroup(L"ARRANGED BY " + ShortType(L.parentType), dim);
        if (!L.parentOrientation.empty()) more += DetailRow(L"orientation", L.parentOrientation, ink, dim);
        if (!L.parentPadding.empty())     more += DetailRow(L"its padding", L.parentPadding, ink, dim);
        if (!L.parentSpacing.empty())     more += DetailRow(L"its spacing", L.parentSpacing, ink, dim);
        if (L.childIndex >= 0 && L.childCount > 0)
            more += DetailRow(L"this child", std::to_wstring(L.childIndex + 1) + L" of " +
                                             std::to_wstring(L.childCount), ink, dim);
        if (!L.gridRow.empty() || !L.gridColumn.empty()) {
            std::wstring cell = L"row " + (L.gridRow.empty() ? std::wstring(L"0") : L.gridRow) +
                                L", col " + (L.gridColumn.empty() ? std::wstring(L"0") : L.gridColumn);
            auto span = [](const std::wstring& s) { return !s.empty() && s != L"1"; };
            if (span(L.gridRowSpan))    cell += L" (spans " + L.gridRowSpan + L" rows)";
            if (span(L.gridColumnSpan)) cell += L" (spans " + L.gridColumnSpan + L" cols)";
            more += DetailRow(L"grid cell", cell, ink, dim);
        }
    }
    more += L"<TextBlock Text=\"Ring thickness is schematic. The numbers are exact, the thickness is "
            L"not. The thumbnail is to scale.\" FontSize=\"10\" TextWrapping=\"Wrap\" Margin=\"0,6,0,0\""
            L" Foreground=\"" + dim + L"\"/>";
    return out;
}
// Build the collapsed authored-XAML line from source text so it reflects what the user wrote.
static std::wstring AuthoredXamlOneLine(const std::wstring& xaml)
{
    size_t nl = xaml.find_first_of(L"\r\n");
    std::wstring head = (nl == std::wstring::npos) ? xaml : xaml.substr(0, nl);
    while (!head.empty() && (head.back() == L' ' || head.back() == L'\t')) head.pop_back();

    const std::wstring key = L"x:Name=\"";
    const size_t at = xaml.find(key);
    if (at != std::wstring::npos) {
        const size_t vs = at + key.size();
        const size_t ve = xaml.find(L'"', vs);
        if (ve != std::wstring::npos && head.find(key) == std::wstring::npos)
            head += L" x:Name=\"" + xaml.substr(vs, ve - vs) + L"\"";
    }
    const bool truncated = (nl != std::wstring::npos) || head.size() < xaml.size();
    if (truncated) head += L" \u2026";
    return head;
}

static std::wstring BuildParentDecidesBlock(const DevToolsWindowLayout& L)
{
    if (L.parentType.empty()) return L"";
    const bool anyDetail = !L.parentOrientation.empty() || !L.parentSpacing.empty() || !L.parentPadding.empty()
                        || L.childIndex >= 0 || L.haveInParent || !L.gridRow.empty() || !L.gridColumn.empty();
    if (!anyDetail) return L"";

    std::wstring m;
    m += L"<TextBlock Text=\"PARENT DECIDES\" Style=\"{StaticResource CaptionTextBlockStyle}\""
         L" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\" CharacterSpacing=\"60\""
         L" Margin=\"0,10,0,0\"/>";
    m += L"<StackPanel Spacing=\"0\" Margin=\"0,-4,0,0\""
         L" AutomationProperties.AutomationId=\"WinAppDevToolsParentDecides\">";

    m += LayoutLine(L"Container", ShortType(L.parentType), true);
    if (!L.parentOrientation.empty()) m += LayoutLine(L"Orientation", L.parentOrientation, false);
    if (!L.parentPadding.empty())     m += LayoutLine(L"Its padding", L.parentPadding, false);
    if (!L.parentSpacing.empty())     m += LayoutLine(L"Its spacing", L.parentSpacing, false);
    if (L.haveParentBox)              m += LayoutLine(L"Its content box", LayoutNumW(L.parentW) + L" \u00D7 " + LayoutNumW(L.parentH), false);
    if (L.haveInParent)               m += LayoutLine(L"Placed in it at", LayoutNumW(L.inParentX) + L", " + LayoutNumW(L.inParentY), false);
    if (L.childIndex >= 0 && L.childCount > 0)
        m += LayoutLine(L"Child", std::to_wstring(L.childIndex + 1) + L" of " + std::to_wstring(L.childCount), false);
    if (!L.gridRow.empty() || !L.gridColumn.empty()) {
        std::wstring cell = L"row " + (L.gridRow.empty() ? std::wstring(L"0") : L.gridRow)
                          + L", col " + (L.gridColumn.empty() ? std::wstring(L"0") : L.gridColumn);
        auto span = [](const std::wstring& s) { return !s.empty() && s != L"1"; };
        if (span(L.gridRowSpan))    cell += L" (spans " + L.gridRowSpan + L" rows)";
        if (span(L.gridColumnSpan)) cell += L" (spans " + L.gridColumnSpan + L" cols)";
        m += LayoutLine(L"Grid cell", cell, false);
    }

    std::wstring why;
    auto num = [](const std::wstring& s, double& outV) -> bool {
        if (s.empty()) return false;
        wchar_t* end = nullptr;
        const double v = wcstod(s.c_str(), &end);
        if (end == s.c_str() || !(v == v)) return false;
        outV = v; return true;
    };
    if (L.haveInParent && L.childIndex == 0) {
        double padTop = 0, padLeft = 0;
        if (!L.parentPadding.empty()) {
            const std::vector<std::wstring> p = SplitFields(L.parentPadding, 4);
            num(p[0], padLeft); num(p[1], padTop);
        }
        if ((padTop > 0.5 || padLeft > 0.5) &&
            fabs((double)L.inParentY - padTop) < 0.5 && fabs((double)L.inParentX - padLeft) < 0.5)
        {
            why = L"It sits exactly on " + ShortType(L.parentType) + L"'s padding (" +
                  LayoutNumW(padLeft) + L", " + LayoutNumW(padTop) +
                  L"). It is the FIRST child, so no spacing applies. That gap belongs to the parent, "
                  L"not to this element's Margin.";
        }
    } else if (L.childIndex > 0 && !L.parentSpacing.empty()) {
        double sp = 0;
        if (num(L.parentSpacing, sp) && sp > 0.5) {
            why = L"It is child " + std::to_wstring(L.childIndex + 1) + L", so " + ShortType(L.parentType) +
                  L"'s Spacing (" + LayoutNumW(sp) + L") is added above it on top of any Margin.";
        }
    }
    if (!why.empty()) {
        m += L"<Border Margin=\"0,6,0,0\" Padding=\"8,6,8,6\" CornerRadius=\"4\""
             L" Background=\"{ThemeResource CardBackgroundFillColorSecondaryBrush}\">";
        m += L"<TextBlock Text=\"" + DevToolsXmlEscape(why) + L"\" FontSize=\"11\" TextWrapping=\"Wrap\""
             L" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\"/></Border>";
    }
    m += L"</StackPanel>";
    return m;
}

static std::wstring BuildLayoutSection(const DevToolsWindowLayout& L, const std::vector<DevToolsCardRow>& rows)
{
    // Named as resource keys, not hand-resolved literals: the pane's pre-parse substitution resolves each key
    // once and caches it for the process, and naming keys is what lets High Contrast redefine them.
    const std::wstring ink = L"{ThemeResource TextFillColorPrimaryBrush}";
    const std::wstring dim = L"{ThemeResource TextFillColorSecondaryBrush}";

    std::wstring m = L"<StackPanel Spacing=\"0\""
                     L" AutomationProperties.AutomationId=\"WinAppDevToolsLayout\">";

    std::wstring more;
    m += BuildBoxModel(L, rows, ink, dim, more);
    const bool haveBoth = L.haveDesired && L.haveRender;

    auto rowValue = [&rows](const wchar_t* n) -> std::wstring {
        for (const auto& r : rows) if (r.name == n) return r.value;
        return std::wstring();
    };
    auto asNumber = [](const std::wstring& s, double& out) -> bool {
        if (s.empty() || s == L"inf" || s == L"Auto") return false;
        wchar_t* end = nullptr;
        double v = wcstod(s.c_str(), &end);
        if (end == s.c_str() || !(v == v)) return false;
        out = v; return true;
    };
    std::wstring why;
    if (haveBoth) {
        const double dw = L.desiredW, rw = L.renderW;
        double maxW = 0, minW = 0;
        const bool hasMax = asNumber(rowValue(L"MaxWidth"), maxW);
        const bool hasMin = asNumber(rowValue(L"MinWidth"), minW);
        if (hasMax && fabs((double)rw - maxW) < 0.5)
            why = L"Its width is sitting exactly on MaxWidth (" + LayoutNumW(maxW) +
                  L"). That clamp is what decided this size, not the content and not the parent.";
        else if (hasMin && fabs((double)rw - minW) < 0.5 && minW > dw + 0.5)
            why = L"Its width is being held up by MinWidth (" + LayoutNumW(minW) +
                  L"), which is larger than the " + LayoutNumW(dw) + L" the content asked for.";
        else if (rw > dw + 0.5 && !rowValue(L"HorizontalAlignment").empty() && rowValue(L"HorizontalAlignment") != L"Stretch")
            why = L"It got MORE width than it asked for even though HorizontalAlignment is " +
                  rowValue(L"HorizontalAlignment") + L". Check the parent's layout.";
        else if (rw < dw - 0.5)
            why = L"It got LESS width than it asked for. It is being constrained. Check MaxWidth on this "
                  L"element and the space the parent had to give.";
        else if ((double)rw <= 0.5 && (double)L.renderH <= 0.5)
            why = L"It has no size. Check Visibility, an empty parent, or a zero-width constraint.";
    }
    if (!why.empty()) {
        m += L"<Border Margin=\"0,8,0,0\" Padding=\"9,7,9,7\" CornerRadius=\"4\""
             L" Background=\"{ThemeResource CardBackgroundFillColorSecondaryBrush}\">";
        m += L"<TextBlock Text=\"" + DevToolsXmlEscape(why) + L"\" FontSize=\"11\" TextWrapping=\"Wrap\""
             L" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\"/></Border>";
    }

    m += L"<HyperlinkButton x:Name=\"LayoutToggle\" Padding=\"0\" Margin=\"0,8,0,0\""
         L" HorizontalAlignment=\"Left\" AutomationProperties.Name=\"Show more layout detail\""
         L" AutomationProperties.AutomationId=\"WinAppDevToolsLayoutToggle\">";
    m += L"<StackPanel Orientation=\"Horizontal\" Spacing=\"5\">";
    m += L"<TextBlock x:Name=\"LayoutMoreLbl\" Text=\"More\" FontSize=\"11.5\" VerticalAlignment=\"Center\""
         L" AutomationProperties.AccessibilityView=\"Raw\"/>";
    m += L"<TextBlock x:Name=\"LayoutChev\" FontFamily=\"Segoe Fluent Icons,Segoe MDL2 Assets\" FontSize=\"8\""
         L" VerticalAlignment=\"Center\" Text=\"\uE70D\" AutomationProperties.AccessibilityView=\"Raw\"/>";
    m += L"</StackPanel></HyperlinkButton>";

    // Emitted collapsed and flipped by Visibility rather than rebuilt -- same discipline as the breadcrumb
    // toggle, so opening costs no XamlReader work, keeps scroll position, and cannot re-enter the pane
    // rebuild teardown.
    m += L"<StackPanel x:Name=\"LayoutMore\" Spacing=\"0\" Margin=\"0,7,0,0\" Visibility=\"Collapsed\""
         L" AutomationProperties.AutomationId=\"WinAppDevToolsLayoutMore\">";
    m += more;
    m += L"</StackPanel>";

    m += L"</StackPanel>";
    return m;
}
