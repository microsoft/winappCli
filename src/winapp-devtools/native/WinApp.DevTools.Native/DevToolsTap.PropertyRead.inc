// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsTap.cpp; included into that translation unit, not compiled on its own.

// GetPropertyValuesChain renders a reference-typed value (a brush, a bound/complex value) as the object's
// InstanceHandle in decimal - a meaningless pointer-like number to a user - so this resolves brushes to a concrete
// #AARRGGBB and replaces any other unresolved object handle with a clean "{TypeName}" placeholder.
static std::wstring ResolveDisplayValue(const std::wstring& name, const std::wstring& valueType,
                                        const std::wstring& raw, bool isObject,
                                        std::wstring* effectiveType = nullptr, bool* readable = nullptr);

static std::wstring ShortTypeName(const std::wstring& t);

// Map a curated property name to the XAML type CreateInstance needs on write (defined below with the card
// bridges). Declared here because ReadCuratedProps now emits it as the wire's `writeType`, so the wire and
// both in-proc surfaces share ONE property->write-type map rather than each keeping their own.
static const wchar_t* CardMapType(const std::wstring& name);

// HONESTY: the runtime applies a resolved {StaticResource}/{ThemeResource} as a plain LOCAL value and surfaces NO
// resource key here (probed: srcName/SrcInfo empty, Source==Local for a ThemeResource-bound Foreground), so this
// never claims Static-vs-Theme resource - only Local/Style/Default/ Inherited/Animation/etc.
static std::wstring ValueSourceLabel(const PropertyChainValue& val, const PropertyChainSource* srcs, unsigned int sc)
{
    if (val.MetadataBits & 0x10 /*IsValueBindingExpression*/) return L"Binding";
    unsigned int si = val.PropertyChainIndex;
    int bvs = (srcs && si < sc) ? (int)srcs[si].Source : 0;
    switch (bvs) {
        case 1:  return L"Default";
        case 2:  return L"Built-in style";
        case 3:  return L"Style";
        case 4:  return L"Local";
        case 5:  return L"Inherited";
        case 6: case 7: case 8: case 11: return L"Trigger";
        case 9:  return L"Style";
        case 10: return L"Template";
        case 12: return L"Animation";
        case 13: return L"Coerced";
        case 14: return L"Visual state";
        default: return L"";
    }
}

// Pure text: each comma-separated part keeps its own spelling unless it is a plain decimal with a trailing run of
// zeros, so a value that is NOT numeric passes through untouched rather than being reformatted by a function that
// guessed at its shape.
static std::wstring TrimFieldNumbers(const std::wstring& v)
{
    std::wstring out; std::wstring cur;
    auto flush = [&out, &cur]() {
        if (cur.find(L'.') != std::wstring::npos) {
            bool numeric = true;
            for (wchar_t c : cur) if (!iswdigit(c) && c != L'.' && c != L'-' && c != L'+') { numeric = false; break; }
            if (numeric) {
                while (!cur.empty() && cur.back() == L'0') cur.pop_back();
                if (!cur.empty() && cur.back() == L'.') cur.pop_back();
                if (cur.empty() || cur == L"-") cur += L"0";
            }
        }
        out += cur; cur.clear();
    };
    for (wchar_t c : v) {
        if (c == L',') { flush(); out += L','; }
        else cur += c;
    }
    flush();
    return out;
}

static std::wstring DeriveEditKind(const std::wstring& name, const std::wstring& valueType,
                                   const std::wstring& value, const std::wstring& valueState,
                                   bool hasEnumMembers)
{
    if (valueState == L"unresolved") return L"none";
    std::wstring st = ShortTypeName(valueType);
    if (st == L"Boolean") return L"bool";
    if (hasEnumMembers) return L"enum";
    if (!DevToolsRead_FieldLabels(valueType).empty()) return L"fields";
    if (st == L"Double" || st == L"Single" || st == L"Int32") return L"number";
    const bool brushTyped = name == L"Foreground" || name == L"Background"
        || (st.size() >= 5 && st.compare(st.size() - 5, 5, L"Brush") == 0);
    if (brushTyped) {
        return (value.size() == 9 && value[0] == L'#') ? std::wstring(L"color") : std::wstring(L"none");
    }
    if (st == L"String") return L"text";
    return value.empty() ? L"none" : L"text";
}

static std::wstring PickValueType(const PropertyChainValue& pv)
{
    const std::wstring declared = pv.Type ? pv.Type : L"";
    const std::wstring reported = pv.ValueType ? pv.ValueType : declared;
    const bool isHandle  = (pv.MetadataBits & 0x1)  != 0;
    const bool isBinding = (pv.MetadataBits & 0x10) != 0;
    if (isHandle || isBinding || declared.empty() || declared == reported) return reported;
    static const wchar_t* const kPrimitives[] = {
        L"Windows.Foundation.Double", L"Windows.Foundation.Single",
        L"Windows.Foundation.Int32",  L"Windows.Foundation.Boolean",
    };
    for (const wchar_t* p : kPrimitives)
        if (declared == p) return declared;
    return reported;
}

static std::wstring HStrTake(void* raw)
{
    HSTRING hs = reinterpret_cast<HSTRING>(raw);
    if (!hs) return L"";
    UINT32 len = 0; const wchar_t* p = WindowsGetStringRawBuffer(hs, &len);
    std::wstring s(p ? p : L"", len);
    WindowsDeleteString(hs);
    return s;
}

static HRESULT ReadControlText(IInspectable* input, std::wstring* outText)
{
    if (!input || !outText) return E_INVALIDARG;
    void* control = nullptr;
    auto getter = &DevToolsGetTextBoxText;
    HRESULT hr = input->QueryInterface(DevToolsIid<DevToolsXC::ITextBox>(), &control);
    if (hr == E_NOINTERFACE) {
        getter = &DevToolsGetTextBlockText;
        hr = input->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &control);
    }
    if (hr == E_NOINTERFACE) return S_FALSE;
    if (FAILED(hr)) return hr;
    if (!control) return E_UNEXPECTED;
    HSTRING text = nullptr;
    hr = getter(control, &text);
    reinterpret_cast<IUnknown*>(control)->Release();
    if (FAILED(hr)) {
        WindowsDeleteString(text);
        return hr;
    }
    *outText = HStrTake(text);
    return S_OK;
}

// Read an evaluated binding value through GetProperty using its chain index, not a short-name
// lookup. UI thread only. Successful empty text is a real result, distinct from a failed read.
static HRESULT ReadInspectableValue(IInspectable* value, const std::wstring& name,
    const std::wstring& declaredType, std::wstring* out, std::wstring* effectiveType)
{
    if (!value || !out) return E_INVALIDARG;
    auto num = [](double d) {
        std::wstring s = std::to_wstring(d);
        size_t dot = s.find(L'.');
        if (dot != std::wstring::npos) {
            size_t last = s.find_last_not_of(L'0');
            s.erase((last == dot ? dot : last + 1));
        }
        return s;
    };
    winrt::com_ptr<IInspectable> typed;
    HRESULT hr = value->QueryInterface(DevToolsIid<winrt::Windows::Foundation::IPropertyValue>(), typed.put_void());
    if (SUCCEEDED(hr) && typed) {
        auto* p = DevToolsAbi<winrt::Windows::Foundation::IPropertyValue>(typed.get());
        int32_t ptype = 0;
        hr = p->get_Type(&ptype);
        if (FAILED(hr)) return hr;
        std::wstring text, type;
        switch (ptype) {
        case 12: { HSTRING hs = nullptr; hr = p->GetString(reinterpret_cast<void**>(&hs)); text = HStrTake(hs); type = L"String"; break; }
        case 11: { bool b = false; hr = p->GetBoolean(&b); text = b ? L"True" : L"False"; type = L"Boolean"; break; }
        case 9: { double d = 0; hr = p->GetDouble(&d); text = num(d); type = L"Double"; break; }
        case 8: { float f = 0; hr = p->GetSingle(&f); text = num(f); type = L"Single"; break; }
        case 4: { int32_t i = 0; hr = p->GetInt32(&i); text = std::to_wstring(i); type = L"Int32"; break; }
        case 6: { int64_t i = 0; hr = p->GetInt64(&i); text = std::to_wstring(i); type = L"Int64"; break; }
        default: return S_FALSE;
        }
        if (FAILED(hr)) return hr;
        *out = std::move(text);
        if (effectiveType) *effectiveType = L"Windows.Foundation." + type;
        return S_OK;
    }
    if (FAILED(hr) && hr != E_NOINTERFACE) return hr;
    const auto type = ShortTypeName(declaredType);
    if (name == L"FontFamily" || type == L"FontFamily") {
        typed = nullptr;
        hr = value->QueryInterface(DevToolsIid<DevToolsXM::IFontFamily>(), typed.put_void());
        if (FAILED(hr) || !typed) return FAILED(hr) ? hr : E_UNEXPECTED;
        HSTRING source = nullptr;
        hr = DevToolsGetFontFamilySource(typed.get(), &source);
        const auto text = HStrTake(source);
        if (FAILED(hr)) return hr;
        *out = text;
        if (effectiveType) *effectiveType = L"Microsoft.UI.Xaml.Media.FontFamily";
        return S_OK;
    }
    if (name == L"Foreground" || name == L"Background" ||
        (type.size() >= 5 && type.compare(type.size() - 5, 5, L"Brush") == 0)) {
        typed = nullptr;
        hr = value->QueryInterface(DevToolsIid<DevToolsXM::ISolidColorBrush>(), typed.put_void());
        if (hr == E_NOINTERFACE) return S_FALSE;
        if (FAILED(hr) || !typed) return FAILED(hr) ? hr : E_UNEXPECTED;
        unsigned char a = 0, r = 0, g = 0, b = 0;
        hr = DevToolsGetSolidColorBrushColor(typed.get(), &a, &r, &g, &b);
        if (FAILED(hr)) return hr;
        wchar_t color[10]{};
        swprintf_s(color, L"#%02X%02X%02X%02X", a, r, g, b);
        *out = color;
        if (effectiveType) *effectiveType = L"Microsoft.UI.Xaml.Media.SolidColorBrush";
        return S_OK;
    }
    return S_FALSE;
}

static bool ReadEvaluatedValue(InstanceHandle owner, const PropertyChainValue& row,
    const std::wstring& name, const std::wstring& declaredType, std::wstring* out, std::wstring* effectiveType,
    bool* isNull = nullptr)
{
    if (isNull) *isNull = false;
    if (!g_vts3 || !g_diag || !out) return false;
    InstanceHandle val = 0;
    HRESULT hr = g_vts3->GetProperty(owner, row.Index, &val);
    if (SUCCEEDED(hr) && !val) {
        if (isNull) *isNull = true;
        return false;
    }
    winrt::com_ptr<IInspectable> value;
    if (SUCCEEDED(hr) && val) hr = g_diag->GetIInspectableFromHandle(val, value.put());
    if (SUCCEEDED(hr) && value) hr = ReadInspectableValue(value.get(), name, declaredType, out, effectiveType);
    else if (SUCCEEDED(hr)) hr = E_UNEXPECTED;
    if (FAILED(hr)) Log(L"property.read %s: effective getter failed handle=%llu hr=0x%08X", name.c_str(), owner, hr);
    return hr == S_OK;
}

// Read the XAML file:line an element was declared at (defined below with the other source bridges).
// Declared here because ReadCuratedProps needs it to recover authored values.
static bool ReadElementSourceInfo(InstanceHandle ih, std::wstring& outFile, unsigned int& outLine, unsigned int& outCol);

static void ReadParentContext(InstanceHandle elem, InstanceHandle parent, void* elemUi, DevToolsReadLayout& out);

// make the IsValueHandle assumption FALSIFIABLE rather than silently load-bearing. A real element's chain has 100-200
// entries and always contains object-valued properties, so ZERO handle bits across a whole chain is the assumption
// failing, and it is detectable.
static void NoteHandleBitCoverage(unsigned int pc, unsigned int handleBits)
{
    static bool reported = false;
    if (reported || pc < 20 || handleBits > 0) return;
    reported = true;
    Log(L"ASSUMPTION FAILED: MetadataBit::IsValueHandle was ZERO across an entire %u-entry property chain. "
        L"Object-valued properties (brushes, templates) will be reported as plain strings. This is the "
        L"IsValueHandle assumption failing; values will not be guessed.", pc);
}

static bool IsNumericXamlType(const std::wstring& declaredType)
{
    const std::wstring st = ShortTypeName(declaredType);
    return st == L"Double" || st == L"Single" || st == L"Int32" || st == L"Int64";
}

// A brush and a CompositeTransform are the same thing to the diagnostics API -- an object with a property chain -- so
// opening either is a second GetPropertyValuesChain on a handle we already hold. What made them look different was
// the pane, which flattened one to a type name and never showed the other at all.
static void ReadChildProps(InstanceHandle childHandle, const std::wstring& expandKind, bool parentWritable,
                           std::vector<DevToolsReadProp>* out)
{
    if (!g_vts3 || !childHandle || !out) return;
    // Which leaves are worth a row, per structure. Curated because a raw brush chain is ~30 entries of
    // DependencyObject bookkeeping and a user opening `Foreground` is asking one question.
    static const wchar_t* const kBrush[]     = { L"Color", L"Opacity" };
    static const wchar_t* const kTransform[] = { L"TranslateX", L"TranslateY", L"ScaleX", L"ScaleY",
                                                 L"Rotation", L"SkewX", L"SkewY", L"CenterX", L"CenterY",
                                                 L"X", L"Y", L"Angle", L"Matrix" };
    const wchar_t* const* want = nullptr; size_t wantCount = 0;
    if (expandKind == L"brush")          { want = kBrush;     wantCount = _countof(kBrush); }
    else if (expandKind == L"transform") { want = kTransform; wantCount = _countof(kTransform); }
    else return;

    unsigned int sc = 0, pc = 0;
    PropertyChainSource* s = nullptr; PropertyChainValue* v = nullptr;
    if (FAILED(g_vts3->GetPropertyValuesChain(childHandle, &sc, &s, &pc, &v)) || !v) {
        FreePropChain(sc, s, pc, v);
        return;
    }
    std::set<std::wstring> taken;
    // Emit in the CURATED order, not the chain's order, so `Color` is the first row inside every brush and
    // the nine transform doubles read in the order the mockup's stepper grid lays them out.
    for (size_t k = 0; k < wantCount; ++k) {
        for (unsigned int i = 0; i < pc; i++) {
            if (!v[i].PropertyName || want[k] != std::wstring(v[i].PropertyName)) continue;
            if (!taken.insert(want[k]).second) continue;
            DevToolsReadProp c;
            c.name  = want[k];
            c.type  = PickValueType(v[i]);
            const long long bits = (long long)v[i].MetadataBits;
            const bool isHandle   = (bits & 0x1) != 0;
            const bool isReadOnly = (bits & 0x2) != 0;
            c.value = ResolveDisplayValue(c.name, c.type, v[i].Value ? v[i].Value : L"", isHandle);
            if (c.name == L"Color") c.value = DevToolsRead_NormalizeColor(c.value);
            c.editKind = (c.name == L"Color") ? std::wstring(L"color")
                                              : DeriveEditKind(c.name, c.type, c.value, c.valueState, false);
            if (!isReadOnly && parentWritable) {
                if (c.name == L"Color") c.writeType = L"Color";
                else c.writeType = DevToolsRead_DeriveWriteType(v[i].Type ? v[i].Type : L"", false);
            }
            if (c.editKind == L"fields") { c.fields = DevToolsRead_FieldLabels(c.type); c.value = TrimFieldNumbers(c.value); }
            out->push_back(std::move(c));
            break;
        }
    }
    FreePropChain(sc, s, pc, v);
}

static DevToolsAuthoredState ReadElementAuthoredContext(InstanceHandle h, const std::wstring& file,
    unsigned int line, unsigned int column, std::wstring* xaml, DevToolsAuthoredLocation* location)
{
    std::wstring type, name;
    std::vector<InstanceHandle> parents;
    {
        CensusSharedLock lock;
        const auto t = g_type.find(h), n = g_nameOf.find(h);
        if (t != g_type.end()) type = t->second;
        if (n != g_nameOf.end()) name = n->second;
        auto current = h;
        for (unsigned int depth = 0; depth < 64; ++depth) {
            const auto parent = g_parent.find(current);
            if (parent == g_parent.end() || !parent->second) break;
            current = parent->second;
            if (current == h || std::find(parents.begin(), parents.end(), current) != parents.end())
                return DevToolsAuthoredState::Unavailable;
            parents.push_back(current);
        }
    }
    auto state = DevToolsAuthored_ReadElement(file, line, xaml, column, type, name, location);
    if (state != DevToolsAuthoredState::Likely) return state;
    if (parents.size() == 64) { xaml->clear(); *location = {}; return DevToolsAuthoredState::Unavailable; }
    bool matched = location->parentLine == 0;
    for (auto parent : parents) {
        std::wstring parentFile; unsigned int parentLine = 0, parentColumn = 0;
        if (!ReadElementSourceInfo(parent, parentFile, parentLine, parentColumn) || parentFile != file) continue;
        std::wstring parentType, parentName;
        {
            CensusSharedLock lock;
            const auto t = g_type.find(parent), n = g_nameOf.find(parent);
            if (t != g_type.end()) parentType = t->second;
            if (n != g_nameOf.end()) parentName = n->second;
        }
        DevToolsAuthoredLocation parentLocation;
        std::wstring parentXaml;
        const auto parentState = DevToolsAuthored_ReadElement(parentFile, parentLine, &parentXaml,
            parentColumn, parentType, parentName, &parentLocation);
        matched = location->parentLine != 0 &&
            (parentState == DevToolsAuthoredState::Likely || parentState == DevToolsAuthoredState::Available) &&
            parentLocation.line == location->parentLine && parentName == location->parentName;
        break;
    }
    if (!matched) { xaml->clear(); *location = {}; return DevToolsAuthoredState::Unavailable; }
    return state;
}

// Read effective chain properties on the UI thread; editor metadata comes from runtime types.
static HRESULT ReadCuratedProps(InstanceHandle h, std::vector<DevToolsReadProp>* out, std::wstring* outAuthoredState,
                                std::wstring* outAuthoredXaml = nullptr,
                                const std::set<std::wstring>* projection = nullptr, size_t* retrievedRows = nullptr)
{
    if (!g_vts3) return E_FAIL;
    // Authored values come from the app's OWN.xaml, because the runtime discards authorship for
    // both {x:Bind} (compiled to a plain local value) and a resolved {ThemeResource} (key dropped). Resolve the
    // element's source location ONCE per element rather than once per row.
    std::wstring srcFile; unsigned int srcLine = 0, srcCol = 0;
    if (!projection) ReadElementSourceInfo(h, srcFile, srcLine, srcCol);
    std::wstring srcType, srcName;
    {
        CensusSharedLock lock;
        const auto type = g_type.find(h), name = g_nameOf.find(h);
        if (type != g_type.end()) srcType = type->second;
        if (name != g_nameOf.end()) srcName = name->second;
    }
    std::wstring authoredXaml;
    DevToolsAuthoredLocation authoredLocation;
    const DevToolsAuthoredState authoredState = projection ? DevToolsAuthoredState::Unavailable :
        ReadElementAuthoredContext(h, srcFile, srcLine, srcCol, &authoredXaml, &authoredLocation);
    if (outAuthoredXaml) *outAuthoredXaml = authoredXaml;
    unsigned int sc = 0, pc = 0;
    PropertyChainSource* s = nullptr; PropertyChainValue* v = nullptr;
    HRESULT hr = g_vts3->GetPropertyValuesChain(h, &sc, &s, &pc, &v);
    if (retrievedRows) *retrievedRows += pc;
    if (SUCCEEDED(hr) && v) {
        // Count the runtime's handle bits across the WHOLE chain: the detector's premise is that a real
        // element always has SOME object-valued property.
        unsigned int handleBits = 0;
        for (unsigned int i = 0; i < pc; i++) if (((long long)v[i].MetadataBits & 0x1) != 0) ++handleBits;
        NoteHandleBitCoverage(pc, handleBits);
        std::set<std::wstring> taken;
        for (unsigned int i = 0; i < pc; i++) {
            if (!v[i].PropertyName) continue;
            std::wstring name = v[i].PropertyName;
            if (!taken.insert(name).second) continue;
            if (projection && !projection->count(name)) continue;
            DevToolsReadProp prop;
            prop.name = name;
            prop.type = PickValueType(v[i]);
            const std::wstring declaredType = v[i].Type ? v[i].Type : L"";
            const std::wstring rawValue = v[i].Value ? v[i].Value : L"";
            const long long bits = (long long)v[i].MetadataBits;
            const bool isHandle  = (bits & 0x1)  != 0;
            const bool isReadOnly= (bits & 0x2)  != 0;
            const bool isBinding = (bits & 0x10) != 0;
            const bool isNull    = (bits & 0x20) != 0;

            bool readable = false;
            prop.value  = ResolveDisplayValue(name, prop.type, rawValue, isHandle, &prop.type, &readable);
            prop.source = ValueSourceLabel(v[i], s, sc);

            if (isBinding)     prop.valueState = L"binding";
            else if (isNull)   prop.valueState = L"null";
            else if (isHandle && !readable) prop.valueState = L"unresolved";

            if (isBinding) {
                prop.binding = DevToolsBinding_Describe(g_diag, wcstoull(rawValue.c_str(), nullptr, 10));
                std::wstring evaluated;
                bool evaluatedNull = false;
                if (ReadEvaluatedValue(h, v[i], name, declaredType, &evaluated, &prop.type, &evaluatedNull)) prop.value = evaluated;
                else { prop.value.clear(); prop.valueState = evaluatedNull ? L"null" : L"unresolved"; }
            } else if (name == L"FontFamily" && !isNull && (!isHandle || !readable)) {
                std::wstring source;
                if (ReadEvaluatedValue(h, v[i], name, declaredType, &source, &prop.type)) {
                    prop.value = std::move(source);
                    prop.valueState.clear();
                } else {
                    prop.value.clear();
                    prop.valueState = L"unresolved";
                }
            }

            if (name == L"Text" && g_diag) {
                IInspectable* element = nullptr;
                HRESULT read = g_diag->GetIInspectableFromHandle(h, &element);
                std::wstring text;
                if (SUCCEEDED(read)) read = ReadControlText(element, &text);
                if (element) element->Release();
                if (read == S_OK) {
                    prop.value = std::move(text);
                    prop.type = L"Windows.Foundation.String";
                    prop.valueState = isBinding ? L"binding" : L"";
                } else if (FAILED(read)) {
                    Log(L"property.read Text: live getter failed handle=%llu hr=0x%08X", h, read);
                    prop.value.clear();
                    prop.valueState = L"unresolved";
                }
            }

            const bool isBool = ShortTypeName(prop.type) == L"Boolean";
            if (isBool) {
                // Normalize whatever the chain reports (True/False, 1/0) to a single spelling, so a client
                // never has to guess which form a given app produced.
                if (prop.value == L"1" || _wcsicmp(prop.value.c_str(), L"true") == 0) prop.value = L"True";
                else if (prop.value == L"0" || _wcsicmp(prop.value.c_str(), L"false") == 0) prop.value = L"False";
            } else {
                std::vector<std::wstring> members = EnumTableFor(prop.type);
                if (!members.empty() && prop.valueState.empty()) {
                    std::wstring named = EnumNameForValue(prop.type, prop.value);
                    if (!named.empty()) prop.value = named;
                    prop.enumValues = members;
                }
            }

            // A NUMBER THE USER CANNOT TYPE BACK IS A STATE, NOT A VALUE. Both were going into a box whose only
            // accepted input is a Double, so the pane displayed a value no keystroke could restore.
            if (prop.valueState.empty() && prop.enumValues.empty() &&
                (prop.value == L"Auto" || prop.value == L"inf" || prop.value == L"NaN") &&
                IsNumericXamlType(declaredType))
            {
                prop.valueState = L"unset";
            }

            // Absence IS the read-only signal ("read-only properties are visibly non-editable rather than silently
            // failing") - deliberately not a second `readOnly` boolean that could disagree with it.
            if (!isReadOnly) {
                const wchar_t* wt = CardMapType(name);
                prop.writeType = wt ? std::wstring(wt)
                                    : DevToolsRead_DeriveWriteType(declaredType, !prop.enumValues.empty());
            }

            prop.editKind = DeriveEditKind(name, prop.type, prop.value, prop.valueState, !prop.enumValues.empty());
            if (prop.editKind == L"fields") {
                prop.fields = DevToolsRead_FieldLabels(prop.type);
                // The runtime renders a Vector3/Point component with six decimal places. Trimming here rather
                // than in each surface keeps the wire, the card and the pane showing the same number, and it
                // is the same value a write would take back: the boxes are seeded from this string.
                prop.value = TrimFieldNumbers(prop.value);
            }

            // This is the ONLY route to an {x:Bind} (compiled away entirely) or a {ThemeResource} key (resolved and
            // discarded), so it is read for every row, not just bound ones. The declaration was read once above and
            // is also the full XAML surfaced by Source.get, the DevTools window and the in-app card.
            {
                std::wstring authored;
                if (authoredState == DevToolsAuthoredState::Available &&
                    DevToolsAuthored_FindAttribute(authoredXaml, name, &authored)) {
                    prop.authored = authored;
                    prop.authoredKind = DevToolsRead_ClassifyAuthored(authored);
                    prop.authoredKey = DevToolsRead_AuthoredKey(authored);
                }
            }

            // Only object-valued properties can have children, and only the curated expandable types get them --
            // `Template` is deliberately excluded, because opening it yields another visual tree rather than leaf
            // values.
            if (!projection && isHandle && !isBinding && !isNull) {
                const std::wstring expandKind = DevToolsRead_ExpandableKind(prop.type);
                if (!expandKind.empty()) {
                    const InstanceHandle child = (InstanceHandle)wcstoull(rawValue.c_str(), nullptr, 10);
                    if (child) {
                        ReadChildProps(child, expandKind,
                                       DevToolsRead_ChildEditingIsSafe(prop.source, prop.authoredKind),
                                       &prop.children);
                    }
                }
            }

            out->push_back(std::move(prop));
        }

        if (!projection) {
            std::map<std::wstring, std::vector<DevToolsReadChainEntry>> chains;
            for (unsigned int i = 0; i < pc; i++) {
                if (!v[i].PropertyName) continue;
                std::wstring name = v[i].PropertyName;
                DevToolsReadChainEntry e;
                e.source  = ValueSourceLabel(v[i], s, sc);
                // Resolve like the row: a raw object handle ("0" for a null default) is not a value to show.
                const long long bits = (long long)v[i].MetadataBits;
                const std::wstring raw = v[i].Value ? v[i].Value : L"";
                const bool isHandle = (bits & 0x1) != 0;
                e.value   = (bits & 0x20) || (isHandle && raw == L"0") ? std::wstring()
                          : ResolveDisplayValue(name, PickValueType(v[i]), raw, isHandle);
                e.winner  = !v[i].Overridden;
                unsigned int si = v[i].PropertyChainIndex;
                if (s && si < sc) {
                    if (s[si].TargetType) e.targetType = s[si].TargetType;
                    if (s[si].SrcInfo.FileName) e.file = DevToolsSourcePath_AppUri(s[si].SrcInfo.FileName);
                    e.line = s[si].SrcInfo.LineNumber;
                }
                chains[name].push_back(std::move(e));
            }
            for (auto& prop : *out) {
                auto it = chains.find(prop.name);
                if (it != chains.end() && it->second.size() > 1) prop.chain = std::move(it->second);
            }
        }
    }
    FreePropChain(sc, s, pc, v);
    if (outAuthoredState) *outAuthoredState = DevToolsAuthored_StateToken(authoredState);
    return hr;
}
