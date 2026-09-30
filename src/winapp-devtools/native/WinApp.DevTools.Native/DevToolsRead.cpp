// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "DevToolsRead.h"
#include "DevToolsProtocol.h"

#include <cstdio>
#include <cstdlib>
#include <cwctype>

static void AppendNode(std::wstring& out, const DevToolsReadNode& n)
{
    out += L"{\"handle\":\"";
    out += std::to_wstring(n.handle);          // u64 as a decimal STRING (JSON numbers lose >2^53 precision)
    out += L"\",\"name\":\"";
    out += DevToolsJsonEscape(n.name);
    out += L"\",\"type\":\"";
    out += DevToolsJsonEscape(n.type);
    out += L"\"";
    // (unattributed) rather than being spelled as an empty string a client has to special-case.
    if (!n.file.empty()) { out += L",\"file\":\""; out += DevToolsJsonEscape(n.file); out += L"\""; }
    if (!n.id.empty())   { out += L",\"id\":\"";   out += DevToolsJsonEscape(n.id);   out += L"\""; }
    // Emitted only when true, so absence carries the meaning "unnamed, or the name is shared".
    if (n.uniqueName)    { out += L",\"uniqueName\":true"; }
    out += L",\"childCount\":";
    out += std::to_wstring(n.childCount);
    out += L",\"children\":[";
    for (size_t i = 0; i < n.children.size(); ++i)
    {
        if (i) out += L',';
        AppendNode(out, n.children[i]);
    }
    out += L"]}";
}

std::wstring DevToolsRead_SerializeTree(const std::vector<DevToolsReadNode>& roots)
{
    std::wstring out;
    out += L'[';
    for (size_t i = 0; i < roots.size(); ++i)
    {
        if (i) out += L',';
        AppendNode(out, roots[i]);
    }
    out += L']';
    return out;
}

bool DevToolsRead_IsSecretProperty(const std::wstring& name)
{
    static constexpr wchar_t kSuffix[] = L"password";
    constexpr size_t length = sizeof(kSuffix) / sizeof(kSuffix[0]) - 1;
    return name.size() >= length && _wcsicmp(name.c_str() + name.size() - length, kSuffix) == 0;
}

void DevToolsRead_Redact(DevToolsReadProp& p)
{
    if (!DevToolsRead_IsSecretProperty(p.name)) return;
    p.redacted = true;
    p.value = kDevToolsRedacted;
    p.valueState.clear();
    p.writeType.clear();
    p.editKind = L"none";
    // A binding or resource reference names where the value comes from, not the value itself.
    if (!p.authored.empty() && p.authored.front() != L'{') p.authored = kDevToolsRedacted;
    for (auto& entry : p.chain) entry.value = kDevToolsRedacted;
    p.children.clear();
}

static void AppendProp(std::wstring& out, const DevToolsReadProp& source, int depth = 0)
{
    DevToolsReadProp redacted;
    const bool secret = DevToolsRead_IsSecretProperty(source.name) && !source.redacted;
    if (secret) { redacted = source; DevToolsRead_Redact(redacted); }
    const DevToolsReadProp& p = secret ? redacted : source;
    out += L"{\"name\":\"";
    out += DevToolsJsonEscape(p.name);
    out += L"\",\"value\":\"";
    out += DevToolsJsonEscape(p.value);
    out += L"\",\"valueType\":\"";
    out += DevToolsJsonEscape(p.type);
    out += L"\"";
    if (!p.source.empty()) { out += L",\"valueSource\":\""; out += DevToolsJsonEscape(p.source); out += L"\""; }
    out += L",\"editKind\":\"";
    out += DevToolsJsonEscape(p.editKind.empty() ? std::wstring(L"none") : p.editKind);
    out += L"\"";
    if (!p.writeType.empty())   { out += L",\"writeType\":\"";   out += DevToolsJsonEscape(p.writeType);   out += L"\""; }
    if (!p.fields.empty())
    {
        out += L",\"fields\":[";
        for (size_t k = 0; k < p.fields.size(); ++k)
        {
            if (k) out += L',';
            out += L'"';
            out += DevToolsJsonEscape(p.fields[k]);
            out += L'"';
        }
        out += L']';
    }
    if (!p.enumValues.empty())
    {
        out += L",\"enumValues\":[";
        for (size_t k = 0; k < p.enumValues.size(); ++k)
        {
            if (k) out += L',';
            out += L'"';
            out += DevToolsJsonEscape(p.enumValues[k]);
            out += L'"';
        }
        out += L']';
    }
    if (!p.valueState.empty())  { out += L",\"valueState\":\"";  out += DevToolsJsonEscape(p.valueState);  out += L"\""; }
    if (!p.binding.empty())     { out += L",\"binding\":\"";     out += DevToolsJsonEscape(p.binding);     out += L"\""; }
    if (!p.authored.empty())    { out += L",\"authored\":\"";    out += DevToolsJsonEscape(p.authored);    out += L"\""; }
    if (!p.authoredKind.empty()) { out += L",\"authoredKind\":\""; out += DevToolsJsonEscape(p.authoredKind); out += L"\""; }
    if (!p.authoredKey.empty()) { out += L",\"authoredKey\":\""; out += DevToolsJsonEscape(p.authoredKey); out += L"\""; }
    if (p.redacted) out += L",\"redacted\":true";
    if (!p.chain.empty())
    {
        out += L",\"chain\":[";
        for (size_t k = 0; k < p.chain.size(); ++k)
        {
            const DevToolsReadChainEntry& c = p.chain[k];
            if (k) out += L',';
            out += L"{\"source\":\"";  out += DevToolsJsonEscape(c.source);
            out += L"\",\"value\":\""; out += DevToolsJsonEscape(c.value);
            out += L"\",\"winner\":";  out += c.winner ? L"true" : L"false";
            if (!c.targetType.empty()) { out += L",\"targetType\":\""; out += DevToolsJsonEscape(c.targetType); out += L"\""; }
            if (!c.file.empty())       { out += L",\"file\":\"";       out += DevToolsJsonEscape(c.file);       out += L"\""; }
            if (c.line)                { out += L",\"line\":";         out += std::to_wstring(c.line); }
            out += L'}';
        }
        out += L']';
    }
    if (!p.children.empty() && depth < 1)
    {
        out += L",\"children\":[";
        for (size_t k = 0; k < p.children.size(); ++k)
        {
            if (k) out += L',';
            AppendProp(out, p.children[k], depth + 1);
        }
        out += L']';
    }
    out += L"}";
}

std::wstring DevToolsRead_SerializeProps(unsigned long long handle, const std::vector<DevToolsReadProp>& props,
                                    const std::wstring& authoredState)
{
    std::wstring out = L"{\"handle\":\"";
    out += std::to_wstring(handle);
    out += L"\",\"authoredState\":\"";
    out += DevToolsJsonEscape(authoredState.empty() ? std::wstring(L"noSourceInfo") : authoredState);
    out += L"\",\"props\":[";
    for (size_t i = 0; i < props.size(); ++i)
    {
        if (i) out += L',';
        AppendProp(out, props[i]);
    }
    out += L"]}";
    return out;
}


bool DevToolsRead_IsHexColor(const std::wstring& value)
{
    if (value.empty() || value[0] != L'#') return false;
    const size_t count = value.size() - 1;
    if (count != 3 && count != 4 && count != 6 && count != 8) return false;
    for (size_t i = 1; i < value.size(); ++i) if (!iswxdigit(value[i])) return false;
    return true;
}

std::wstring DevToolsRead_NormalizeColor(const std::wstring& raw)
{
    // Trim surrounding whitespace (diagnostics values are usually tight, but be defensive).
    size_t b = raw.find_first_not_of(L" \t");
    if (b == std::wstring::npos) return raw;
    size_t e = raw.find_last_not_of(L" \t");
    std::wstring s = raw.substr(b, e - b + 1);

    auto isHex = [](const std::wstring& str) {
        if (str.empty()) return false;
        for (wchar_t c : str) if (!iswxdigit(c)) return false;
        return true;
    };
    auto up = [](std::wstring str) { for (auto& c : str) c = (wchar_t)towupper(c); return str; };

    if (s[0] == L'#')
    {
        std::wstring hex = s.substr(1);
        if (isHex(hex))
        {
            if (hex.size() == 8) return L"#" + up(hex);                    // AARRGGBB (already canonical)
            if (hex.size() == 6) return L"#FF" + up(hex);                  // RRGGBB -> opaque
            if (hex.size() == 4) { std::wstring o; for (wchar_t c : hex) { o += c; o += c; } return L"#" + up(o); }        // ARGB nibbles
            if (hex.size() == 3) { std::wstring o = L"FF"; for (wchar_t c : hex) { o += c; o += c; } return L"#" + up(o); } // RGB nibbles -> opaque
        }
        return s; // unrecognized #-form: pass through unchanged
    }

    std::vector<int> parts; std::wstring cur; bool numeric = true;
    for (wchar_t c : s + L",")
    {
        if (c == L',')
        {
            if (cur.empty()) { numeric = false; break; }
            parts.push_back(_wtoi(cur.c_str())); cur.clear();
        }
        else if (c == L' ' || c == L'\t') { continue; }
        else if (iswdigit(c)) { cur += c; }
        else { numeric = false; break; }
    }
    if (numeric && (parts.size() == 3 || parts.size() == 4))
    {
        int a = 255, r, g, bl;
        if (parts.size() == 4) { a = parts[0]; r = parts[1]; g = parts[2]; bl = parts[3]; }
        else { r = parts[0]; g = parts[1]; bl = parts[2]; }
        auto clamp = [](int x) { return x < 0 ? 0 : (x > 255 ? 255 : x); };
        wchar_t buf[16];
        _snwprintf_s(buf, _countof(buf), _TRUNCATE, L"#%02X%02X%02X%02X", clamp(a), clamp(r), clamp(g), clamp(bl));
        return buf;
    }

    return raw; // not a recognizable color: hand back the diagnostics value verbatim
}

std::wstring DevToolsRead_SerializeResolve(const std::wstring& prop, const std::wstring& value,
                                      const std::wstring& valueType)
{
    std::wstring out = L"{\"prop\":\"";
    out += DevToolsJsonEscape(prop);
    out += L"\",\"value\":\"";
    out += DevToolsJsonEscape(value);
    out += L"\",\"valueType\":\"";
    out += DevToolsJsonEscape(valueType);
    out += L"\"}";
    return out;
}

static std::wstring LayoutNum(double v)
{
    if (!(v == v)) return L"null";                 // NaN (an unset Width arrives this way)
    if (v > 1e17 || v < -1e17) return L"null";     // infinity / absurd: not a number a UI can draw
    wchar_t buf[32];
    _snwprintf_s(buf, _countof(buf), _TRUNCATE, L"%.2f", v);
    std::wstring s = buf;
    if (s.find(L'.') != std::wstring::npos) {
        while (!s.empty() && s.back() == L'0') s.pop_back();
        if (!s.empty() && s.back() == L'.') s.pop_back();
    }
    return s.empty() ? L"0" : s;
}

std::wstring DevToolsRead_SerializeLayout(const DevToolsReadLayout& L)
{
    std::wstring out = L"{\"handle\":\"";
    out += std::to_wstring(L.handle);
    out += L"\"";
    if (L.haveDesired) { out += L",\"desired\":{\"w\":" + LayoutNum(L.desiredW) + L",\"h\":" + LayoutNum(L.desiredH) + L"}"; }
    if (L.haveRender)  { out += L",\"render\":{\"w\":"  + LayoutNum(L.renderW) + L",\"h\":" + LayoutNum(L.renderH) + L"}"; }
    if (L.haveActual)  { out += L",\"actual\":{\"w\":"  + LayoutNum(L.actualW) + L",\"h\":" + LayoutNum(L.actualH) + L"}"; }
    if (L.haveOffset)  { out += L",\"offset\":{\"x\":"  + LayoutNum(L.offsetX) + L",\"y\":" + LayoutNum(L.offsetY) + L"}"; }
    if (L.haveInParent) { out += L",\"inParent\":{\"x\":" + LayoutNum(L.inParentX) + L",\"y\":" + LayoutNum(L.inParentY) + L"}"; }
    if (!L.parentType.empty() || !L.parentHandle.empty())
    {
        out += L",\"parent\":{\"type\":\"";
        out += DevToolsJsonEscape(L.parentType);
        out += L"\"";
        if (!L.parentHandle.empty()) { out += L",\"handle\":\""; out += DevToolsJsonEscape(L.parentHandle); out += L"\""; }
        if (!L.parentOrientation.empty()) { out += L",\"orientation\":\""; out += DevToolsJsonEscape(L.parentOrientation); out += L"\""; }
        if (!L.parentSpacing.empty())     { out += L",\"spacing\":\"";     out += DevToolsJsonEscape(L.parentSpacing);     out += L"\""; }
        if (!L.parentPadding.empty())     { out += L",\"padding\":\"";     out += DevToolsJsonEscape(L.parentPadding);     out += L"\""; }
        if (L.haveParentBox) { out += L",\"w\":" + LayoutNum(L.parentW) + L",\"h\":" + LayoutNum(L.parentH); }
        if (L.childIndex >= 0) {
            out += L",\"childIndex\":" + std::to_wstring(L.childIndex);
            out += L",\"childCount\":" + std::to_wstring(L.childCount);
        }
        out += L"}";
    }
    if (!L.gridRow.empty() || !L.gridColumn.empty())
    {
        out += L",\"grid\":{\"row\":\"";        out += DevToolsJsonEscape(L.gridRow.empty() ? std::wstring(L"0") : L.gridRow);
        out += L"\",\"column\":\"";             out += DevToolsJsonEscape(L.gridColumn.empty() ? std::wstring(L"0") : L.gridColumn);
        out += L"\"";
        if (!L.gridRowSpan.empty())    { out += L",\"rowSpan\":\"";    out += DevToolsJsonEscape(L.gridRowSpan);    out += L"\""; }
        if (!L.gridColumnSpan.empty()) { out += L",\"columnSpan\":\""; out += DevToolsJsonEscape(L.gridColumnSpan); out += L"\""; }
        out += L"}";
    }
    out += L"}";
    return out;
}

bool DevToolsRead_IsCoreProp(const std::wstring& name)
{
    static const wchar_t* const kCore[] = {
        L"Text", L"Content", L"Foreground", L"Background", L"Visibility",
        L"HorizontalAlignment", L"VerticalAlignment", L"Margin", L"Padding",
        L"Opacity", L"Width", L"Height", L"FontSize",
        L"IsEnabled",
        L"DataContext",
        L"MinWidth", L"MaxWidth", L"MinHeight", L"MaxHeight", L"BorderThickness",
        L"RenderTransform",
    };
    for (const wchar_t* c : kCore)
    {
        if (name == c) return true;
    }
    return false;
}

std::wstring DevToolsRead_ClassifyAuthored(const std::wstring& authored)
{
    if (authored.empty()) return std::wstring();
    size_t b = authored.find_first_not_of(L" \t");
    if (b == std::wstring::npos) return L"literal";
    if (authored[b] != L'{') return L"literal";
    if (authored.compare(b, 2, L"{}") == 0) return L"literal";

    size_t s = b + 1;
    size_t e = authored.find_first_of(L" \t}", s);
    if (e == std::wstring::npos) e = authored.size();
    std::wstring ext = authored.substr(s, e - s);
    if (ext == L"x:Bind") return L"xBind";
    size_t colon = ext.find(L':');
    if (colon != std::wstring::npos) ext = ext.substr(colon + 1);
    if (ext == L"ThemeResource")   return L"themeResource";
    if (ext == L"StaticResource")  return L"staticResource";
    if (ext == L"Binding")         return L"binding";
    if (ext == L"TemplateBinding") return L"templateBinding";
    if (ext == L"Bind")            return L"xBind";     // an app that mapped the x: namespace to another prefix
    return L"customMarkup";
}

std::vector<std::wstring> DevToolsRead_FieldLabels(const std::wstring& valueType)
{
    if (valueType.empty()) return {};
    size_t dot = valueType.find_last_of(L'.');
    std::wstring st = (dot == std::wstring::npos) ? valueType : valueType.substr(dot + 1);
    if (st == L"Thickness")    return { L"Left", L"Top", L"Right", L"Bottom" };
    if (st == L"CornerRadius") return { L"TopLeft", L"TopRight", L"BottomRight", L"BottomLeft" };
    if (st == L"Point")        return { L"X", L"Y" };
    if (st == L"Vector3")      return { L"X", L"Y", L"Z" };
    if (st == L"Vector2")      return { L"X", L"Y" };
    return {};
}

std::wstring DevToolsRead_DeriveWriteType(const std::wstring& declaredType, bool isEnum)
{
    if (declaredType.empty()) return std::wstring();
    size_t dot = declaredType.find_last_of(L'.');
    std::wstring st = (dot == std::wstring::npos) ? declaredType : declaredType.substr(dot + 1);
    if (isEnum) return st;
    static const wchar_t* const kParsable[] = {
        L"Double", L"Single", L"Int32", L"Int64", L"Boolean", L"String", L"Thickness", L"CornerRadius",
        L"Point", L"Vector3", L"Vector2",
    };
    for (const wchar_t* p : kParsable)
        if (st == p) return st;
    if (st == L"Brush" || (st.size() > 5 && st.compare(st.size() - 5, 5, L"Brush") == 0))
        return L"SolidColorBrush";
    return std::wstring();
}

bool DevToolsRead_ChildEditingIsSafe(const std::wstring& valueSource, const std::wstring& authoredKind)
{
    // A resource-backed value is by definition shared with everything else that resolves the same key.
    if (authoredKind == L"themeResource" || authoredKind == L"staticResource") return false;
    return valueSource == L"Local";
}

std::wstring DevToolsRead_ExpandableKind(const std::wstring& valueType)
{
    if (valueType.empty()) return std::wstring();
    size_t dot = valueType.find_last_of(L'.');
    std::wstring st = (dot == std::wstring::npos) ? valueType : valueType.substr(dot + 1);
    if (st == L"SolidColorBrush") return L"brush";
    if (st == L"CompositeTransform" || st == L"TranslateTransform" || st == L"ScaleTransform" ||
        st == L"RotateTransform" || st == L"SkewTransform" || st == L"TransformGroup" ||
        st == L"MatrixTransform")
        return L"transform";
    return std::wstring();
}

std::wstring DevToolsRead_AuthoredKey(const std::wstring& authored)
{
    const std::wstring kind = DevToolsRead_ClassifyAuthored(authored);
    if (kind.empty() || kind == L"literal") return std::wstring();

    size_t b = authored.find_first_not_of(L" \t");
    if (b == std::wstring::npos || authored[b] != L'{') return std::wstring();
    size_t s = authored.find_first_of(L" \t}", b + 1);
    if (s == std::wstring::npos) return std::wstring();
    while (s < authored.size() && (authored[s] == L' ' || authored[s] == L'\t')) ++s;
    if (s >= authored.size() || authored[s] == L'}') return std::wstring();  // "{x:Null}" - no argument

    int depth = 0;
    size_t e = s;
    for (; e < authored.size(); ++e) {
        const wchar_t c = authored[e];
        if (c == L'{') { ++depth; continue; }
        if (c == L'}') { if (depth == 0) break; --depth; continue; }
        if (c == L',' && depth == 0) break;
    }
    std::wstring key = authored.substr(s, e - s);
    while (!key.empty() && (key.back() == L' ' || key.back() == L'\t')) key.pop_back();
    const size_t eq = key.find(L'=');
    if (eq != std::wstring::npos) {
        std::wstring lhs = key.substr(0, eq);
        while (!lhs.empty() && lhs.back() == L' ') lhs.pop_back();
        if (lhs != L"Path") {
            return std::wstring();
        }
        key = key.substr(eq + 1);
        while (!key.empty() && key.front() == L' ') key.erase(key.begin());
    }
    return key;
}


std::wstring DevToolsRead_ShortTypeName(const std::wstring& type)
{
    std::wstring name = type;
    {
        int depth = 0;
        for (size_t i = 0; i < type.size(); ++i) {
            const wchar_t c = type[i];
            if (c == L'[' || c == L'<') { ++depth; continue; }
            if (c == L']' || c == L'>') { if (depth > 0) --depth; continue; }
            if (depth == 0 && c == L',') { name = type.substr(0, i); break; }
        }
    }
    while (!name.empty() && (name.back() == L' ' || name.back() == L'\t')) name.pop_back();

    int depth = 0;
    size_t dot = std::wstring::npos;
    for (size_t i = 0; i < name.size(); ++i) {
        const wchar_t c = name[i];
        if (c == L'[' || c == L'<') { ++depth; continue; }
        if (c == L']' || c == L'>') { if (depth > 0) --depth; continue; }
        if (depth == 0 && c == L'.') dot = i;
    }
    return dot == std::wstring::npos ? name : name.substr(dot + 1);
}
