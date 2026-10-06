// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

// Live style editing reads one Style back out of the user's saved XAML file so the inspector can load it with
// XamlReader and apply it to the running elements. XmlLite finds the element and its in-scope namespaces; the
// Style's own text is sliced verbatim so what loads is exactly what the user wrote.

#include <windows.h>
#include <shlwapi.h>
#include <xmllite.h>
#pragma comment(lib, "xmllite.lib")
#pragma comment(lib, "shlwapi.lib")

#include <cwctype>
#include <map>
#include <string>
#include <vector>

namespace DevToolsStyleSource {

constexpr const wchar_t* PresentationNs = L"http://schemas.microsoft.com/winfx/2006/xaml/presentation";
constexpr const wchar_t* XamlNs = L"http://schemas.microsoft.com/winfx/2006/xaml";
constexpr const wchar_t* XmlnsNs = L"http://www.w3.org/2000/xmlns/";
constexpr const wchar_t* CompatibilityNs = L"http://schemas.openxmlformats.org/markup-compatibility/2006";

struct Found {
    bool         ok = false;
    std::wstring markup;           // just this Style as a root element (namespaces in scope, no x:Key), ready for XamlReader.Load
    unsigned int line = 0;         // the Style's start line in the file
    unsigned int templateLine = 0; // its <Setter Property="Template"> line; 0 when it doesn't set one
    std::wstring error;
};

// The file's bytes as text, without a byte-order mark, so XmlLite's line/column land on the same characters.
inline std::wstring Decode(const std::string& bytes)
{
    if (bytes.size() >= 2 && static_cast<unsigned char>(bytes[0]) == 0xFF && static_cast<unsigned char>(bytes[1]) == 0xFE)
        return std::wstring(reinterpret_cast<const wchar_t*>(bytes.data() + 2), (bytes.size() - 2) / sizeof(wchar_t));
    size_t skip = 0;
    if (bytes.size() >= 3 && static_cast<unsigned char>(bytes[0]) == 0xEF && static_cast<unsigned char>(bytes[1]) == 0xBB &&
        static_cast<unsigned char>(bytes[2]) == 0xBF) skip = 3;
    const int size = static_cast<int>(bytes.size() - skip);
    if (size <= 0) return L"";
    const int n = MultiByteToWideChar(CP_UTF8, 0, bytes.data() + skip, size, nullptr, 0);
    std::wstring text(static_cast<size_t>(n), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, bytes.data() + skip, size, text.data(), n);
    return text;
}

// A type name as written ("local:Card", "controls:Card", "Card") without its prefix.
inline std::wstring ShortName(const std::wstring& type)
{
    const auto colon = type.find_last_of(L":.");
    return colon == std::wstring::npos ? type : type.substr(colon + 1);
}

namespace Detail {

inline std::vector<size_t> LineStarts(const std::wstring& text)
{
    std::vector<size_t> starts{ 0 };
    for (size_t i = 0; i < text.size(); ++i) {
        if (text[i] == L'\r') {
            if (i + 1 < text.size() && text[i + 1] == L'\n') ++i;
            starts.push_back(i + 1);
        } else if (text[i] == L'\n') {
            starts.push_back(i + 1);
        }
    }
    return starts;
}

inline size_t Offset(const std::vector<size_t>& starts, const std::wstring& text, UINT line, UINT position)
{
    if (line == 0 || line > starts.size()) return std::wstring::npos;
    const size_t at = starts[line - 1] + (position ? position - 1 : 0);
    return at < text.size() ? at : std::wstring::npos;
}

// The '>' that closes the tag starting at or after `from`, skipping quoted attribute values.
inline size_t TagEnd(const std::wstring& text, size_t from)
{
    wchar_t quote = 0;
    for (size_t i = from; i < text.size(); ++i) {
        const wchar_t c = text[i];
        if (quote) { if (c == quote) quote = 0; }
        else if (c == L'"' || c == L'\'') quote = c;
        else if (c == L'>') return i;
    }
    return std::wstring::npos;
}

// The span of the attribute XmlLite reports at `line`/`position` (its name), from the whitespace before it through its
// closing quote; false when it can't be found.
inline bool AttributeSpan(const std::vector<size_t>& starts, const std::wstring& text, UINT line, UINT position,
                          size_t* first, size_t* last)
{
    size_t at = Offset(starts, text, line, position);
    if (at == std::wstring::npos) return false;
    const size_t eq = text.find(L'=', at);
    const size_t q = eq == std::wstring::npos ? eq : text.find_first_of(L"\"'", eq);
    const size_t qEnd = q == std::wstring::npos ? q : text.find(text[q], q + 1);
    if (qEnd == std::wstring::npos) return false;
    while (at > 0 && iswspace(text[at - 1])) --at;
    *first = at;
    *last = qEnd;
    return true;
}

// `text` with [first, last] removed except its line breaks, so line numbers after it don't move.
inline void CutKeepingLines(std::wstring& text, size_t first, size_t last)
{
    std::wstring keep;
    for (size_t i = first; i <= last; ++i)
        if (text[i] == L'\r' || text[i] == L'\n') keep += text[i];
    text.replace(first, last - first + 1, keep);
}

inline std::wstring Escape(const std::wstring& value)
{
    std::wstring out;
    for (const wchar_t c : value) {
        switch (c) {
        case L'&': out += L"&amp;"; break;
        case L'"': out += L"&quot;"; break;
        case L'<': out += L"&lt;"; break;
        default: out += c;
        }
    }
    return out;
}

struct Scope {
    std::vector<std::pair<std::wstring, std::wstring>> namespaces; // prefix ("" for default) -> uri
    std::wstring ignorable;
};

} // namespace Detail

// Finds the Style keyed `key` in the file, or the implicit Style (no x:Key) for `targetType` when `key` is empty.
inline Found Extract(const std::string& bytes, const std::wstring& key, const std::wstring& targetType)
{
    Found f;
    const std::wstring text = Decode(bytes);
    const std::vector<size_t> starts = Detail::LineStarts(text);

    IStream* stream = SHCreateMemStream(reinterpret_cast<const BYTE*>(bytes.data()), static_cast<UINT>(bytes.size()));
    if (!stream) { f.error = L"Out of memory."; return f; }
    IXmlReader* reader = nullptr;
    if (FAILED(CreateXmlReader(__uuidof(IXmlReader), reinterpret_cast<void**>(&reader), nullptr)) || !reader) {
        stream->Release();
        f.error = L"XmlLite is not available.";
        return f;
    }
    reader->SetProperty(XmlReaderProperty_DtdProcessing, DtdProcessing_Prohibit);
    reader->SetInput(stream);

    std::vector<Detail::Scope> scopes;
    UINT targetDepth = 0;
    size_t start = std::wstring::npos, end = std::wstring::npos;
    size_t styleKeyFirst = std::wstring::npos, styleKeyLast = std::wstring::npos, nameEnd = std::wstring::npos;
    bool inside = false;
    Detail::Scope inScope, own;
    const std::wstring wantedType = ShortName(targetType);

    // Counted here: XmlLite reports an end element one level deeper than its start element.
    UINT open = 0;
    XmlNodeType type;
    HRESULT hr = S_OK;
    while ((hr = reader->Read(&type)) == S_OK) {
        if (type == XmlNodeType_Element) {
            UINT line = 0, position = 0;
            const UINT depth = open;
            reader->GetLineNumber(&line);
            reader->GetLinePosition(&position);
            const BOOL empty = reader->IsEmptyElement();
            const wchar_t* localName = nullptr;
            const wchar_t* ns = nullptr;
            reader->GetLocalName(&localName, nullptr);
            reader->GetNamespaceUri(&ns, nullptr);
            const std::wstring name = localName ? localName : L"";
            const std::wstring uri = ns ? ns : L"";

            Detail::Scope scope;
            std::wstring xKey, styleType, property;
            bool hasKey = false;
            size_t keyFirst = std::wstring::npos, keyLast = std::wstring::npos;
            for (HRESULT a = reader->MoveToFirstAttribute(); a == S_OK; a = reader->MoveToNextAttribute()) {
                const wchar_t* ap = nullptr; const wchar_t* al = nullptr; const wchar_t* an = nullptr; const wchar_t* av = nullptr;
                reader->GetPrefix(&ap, nullptr);
                reader->GetLocalName(&al, nullptr);
                reader->GetNamespaceUri(&an, nullptr);
                reader->GetValue(&av, nullptr);
                const std::wstring aprefix = ap ? ap : L"", alocal = al ? al : L"", ans = an ? an : L"", avalue = av ? av : L"";
                if (ans == XmlnsNs) scope.namespaces.emplace_back(aprefix.empty() ? L"" : alocal, avalue);
                else if (ans == CompatibilityNs && alocal == L"Ignorable") scope.ignorable = avalue;
                else if (ans == XamlNs && alocal == L"Key") {
                    xKey = avalue;
                    hasKey = true;
                    UINT kline = 0, kposition = 0;
                    reader->GetLineNumber(&kline);
                    reader->GetLinePosition(&kposition);
                    if (!Detail::AttributeSpan(starts, text, kline, kposition, &keyFirst, &keyLast)) keyFirst = keyLast = std::wstring::npos;
                }
                else if (ans.empty() && alocal == L"TargetType") styleType = avalue;
                else if (ans.empty() && alocal == L"Property") property = avalue;
            }
            reader->MoveToElement();

            if (inside) {
                if (depth == targetDepth + 1 && uri == PresentationNs && name == L"Setter" && property == L"Template" && !f.templateLine)
                    f.templateLine = line;
            } else if (start == std::wstring::npos && uri == PresentationNs && name == L"Style" &&
                       (key.empty() ? (!hasKey && ShortName(styleType) == wantedType) : (hasKey && xKey == key))) {
                size_t at = Detail::Offset(starts, text, line, position);
                while (at != std::wstring::npos && at > 0 && text[at] != L'<') --at;
                if (at == std::wstring::npos || text[at] != L'<') { f.error = L"Could not locate the Style's text."; break; }
                start = at;
                f.line = line;
                targetDepth = depth;
                styleKeyFirst = keyFirst;
                styleKeyLast = keyLast;
                own = scope;
                const wchar_t* qualified = nullptr;
                UINT qualifiedLength = 0;
                reader->GetQualifiedName(&qualified, &qualifiedLength);
                nameEnd = start + 1 + qualifiedLength;
                for (const auto& s : scopes) {
                    for (const auto& n : s.namespaces) {
                        bool replaced = false;
                        for (auto& existing : inScope.namespaces)
                            if (existing.first == n.first) { existing.second = n.second; replaced = true; }
                        if (!replaced) inScope.namespaces.push_back(n);
                    }
                    if (!s.ignorable.empty()) inScope.ignorable = s.ignorable;
                }
                if (empty) {
                    end = Detail::TagEnd(text, start);
                    break;
                }
                inside = true;
            }
            if (!empty) {
                if (scopes.size() <= depth) scopes.resize(depth + 1);
                scopes[depth] = std::move(scope);
                ++open;
            }
        } else if (type == XmlNodeType_EndElement) {
            UINT line = 0, position = 0;
            const UINT depth = open ? --open : 0;
            if (inside && depth == targetDepth) {
                reader->GetLineNumber(&line);
                reader->GetLinePosition(&position);
                size_t at = Detail::Offset(starts, text, line, position);
                while (at != std::wstring::npos && at > 0 && text[at] != L'<') --at;
                end = at == std::wstring::npos ? at : Detail::TagEnd(text, at);
                break;
            }
            if (depth < scopes.size()) scopes.resize(depth);
        }
    }
    if (FAILED(hr)) {
        UINT line = 0;
        reader->GetLineNumber(&line);
        f.error = L"The file is not well-formed XML near line " + std::to_wstring(line) + L".";
    }
    reader->Release();
    stream->Release();
    if (!f.error.empty()) return f;
    if (start == std::wstring::npos) {
        f.error = key.empty() ? L"No implicit Style for " + wantedType + L" is in this file."
                              : L"No Style with x:Key=\"" + key + L"\" is in this file.";
        return f;
    }
    if (end == std::wstring::npos) { f.error = L"Could not find the end of the Style."; return f; }

    // The Style itself is the root: XamlReader parses a root eagerly, so a bad Setter fails here with its line rather
    // than silently on first access inside a ResourceDictionary. The Style starts on line 1 of the markup.
    std::wstring body = text.substr(start, end - start + 1);
    if (styleKeyFirst != std::wstring::npos && styleKeyFirst > start && styleKeyLast <= end)
        Detail::CutKeepingLines(body, styleKeyFirst - start, styleKeyLast - start);
    const auto declares = [&](const std::wstring& prefix) {
        for (const auto& n : own.namespaces) if (n.first == prefix) return true;
        return false;
    };
    std::wstring decls;
    for (const auto& n : inScope.namespaces) {
        if (n.first == L"wrapmc" || declares(n.first)) continue;
        decls += n.first.empty() ? L" xmlns=\"" : L" xmlns:" + n.first + L"=\"";
        decls += Detail::Escape(n.second) + L"\"";
    }
    if (!inScope.ignorable.empty() && own.ignorable.empty() && !declares(L"wrapmc"))
        decls += L" xmlns:wrapmc=\"" + std::wstring(CompatibilityNs) + L"\" wrapmc:Ignorable=\"" + Detail::Escape(inScope.ignorable) + L"\"";
    body.insert(nameEnd - start, decls);
    f.markup = std::move(body);
    f.ok = true;
    return f;
}

// XamlReader reports "Line: N" in the markup Extract built, where the Style starts on line 1. Maps it back to the
// file; 0 when the message names no line.
inline unsigned int FileLine(const std::wstring& message, unsigned int styleLine)
{
    const auto at = message.find(L"Line: ");
    if (at == std::wstring::npos || styleLine == 0) return 0;
    const unsigned long n = wcstoul(message.c_str() + at + 6, nullptr, 10);
    if (n < 1) return styleLine;
    return styleLine + static_cast<unsigned int>(n) - 1;
}

// "The attachable property 'State' was not found in type 'AnimatedIcon'." -> {AnimatedIcon, State}. XamlReader only
// knows attached properties the app's own compiled XAML used, so a copied WinUI template can name one it lacks.
inline bool UnknownAttachable(const std::wstring& message, std::wstring* type, std::wstring* member)
{
    const std::wstring lead = L"attachable property '";
    const auto a = message.find(lead);
    if (a == std::wstring::npos) return false;
    const auto aEnd = message.find(L'\'', a + lead.size());
    const std::wstring typeLead = L"in type '";
    const auto t = aEnd == std::wstring::npos ? aEnd : message.find(typeLead, aEnd);
    if (t == std::wstring::npos) return false;
    const auto tEnd = message.find(L'\'', t + typeLead.size());
    if (tEnd == std::wstring::npos) return false;
    *member = message.substr(a + lead.size(), aEnd - a - lead.size());
    *type = ShortName(message.substr(t + typeLead.size(), tEnd - t - typeLead.size()));
    return !member->empty() && !type->empty();
}

// The markup without `Type.Member` attached-property attributes and the Setters that target it. Newlines inside the
// removed text are kept so XamlReader's line numbers still map back to the file. Empty when nothing matched.
inline std::wstring WithoutAttachable(const std::wstring& markup, const std::wstring& type, const std::wstring& member)
{
    const std::wstring dotted = type + L"." + member;
    const auto names = [&](const std::wstring& value) {
        // "AnimatedIcon.State", "local:AnimatedIcon.State" or a Target path ending in "(controls:AnimatedIcon.State)".
        const auto at = value.rfind(dotted);
        if (at == std::wstring::npos) return false;
        const bool before = at == 0 || value[at - 1] == L':' || value[at - 1] == L'(' || value[at - 1] == L'.';
        const size_t after = at + dotted.size();
        return before && (after == value.size() || value[after] == L')');
    };

    std::wstring utf16;
    utf16.reserve(markup.size() + 1);
    utf16.push_back(static_cast<wchar_t>(0xFEFF));
    utf16 += markup;
    IStream* stream = SHCreateMemStream(reinterpret_cast<const BYTE*>(utf16.data()), static_cast<UINT>(utf16.size() * sizeof(wchar_t)));
    if (!stream) return L"";
    IXmlReader* reader = nullptr;
    if (FAILED(CreateXmlReader(__uuidof(IXmlReader), reinterpret_cast<void**>(&reader), nullptr)) || !reader) {
        stream->Release();
        return L"";
    }
    reader->SetProperty(XmlReaderProperty_DtdProcessing, DtdProcessing_Prohibit);
    reader->SetInput(stream);

    const std::vector<size_t> starts = Detail::LineStarts(markup);
    std::vector<std::pair<size_t, size_t>> cuts; // [first, last] inclusive
    UINT open = 0, skipDepth = 0;
    size_t skipStart = std::wstring::npos;
    XmlNodeType nodeType;
    HRESULT hr = S_OK;
    while ((hr = reader->Read(&nodeType)) == S_OK) {
        if (nodeType == XmlNodeType_Element) {
            const UINT depth = open;
            const BOOL empty = reader->IsEmptyElement();
            if (skipStart == std::wstring::npos) {
                UINT line = 0, position = 0;
                reader->GetLineNumber(&line);
                reader->GetLinePosition(&position);
                const wchar_t* localName = nullptr;
                reader->GetLocalName(&localName, nullptr);
                const bool setter = localName && std::wstring(localName) == L"Setter";
                bool dropElement = false;
                for (HRESULT a = reader->MoveToFirstAttribute(); a == S_OK; a = reader->MoveToNextAttribute()) {
                    const wchar_t* al = nullptr; const wchar_t* av = nullptr;
                    reader->GetLocalName(&al, nullptr);
                    reader->GetValue(&av, nullptr);
                    const std::wstring alocal = al ? al : L"", avalue = av ? av : L"";
                    if (setter && (alocal == L"Target" || alocal == L"Property") && names(avalue)) { dropElement = true; continue; }
                    if (!names(alocal)) continue;
                    UINT aline = 0, aposition = 0;
                    reader->GetLineNumber(&aline);
                    reader->GetLinePosition(&aposition);
                    size_t first = 0, last = 0;
                    if (Detail::AttributeSpan(starts, markup, aline, aposition, &first, &last)) cuts.emplace_back(first, last);
                }
                reader->MoveToElement();
                if (dropElement) {
                    size_t at = Detail::Offset(starts, markup, line, position);
                    while (at != std::wstring::npos && at > 0 && markup[at] != L'<') --at;
                    if (at != std::wstring::npos) {
                        // The Setter's own attribute cuts are inside the element being dropped.
                        while (!cuts.empty() && cuts.back().first >= at) cuts.pop_back();
                        if (empty) {
                            const size_t end = Detail::TagEnd(markup, at);
                            if (end != std::wstring::npos) cuts.emplace_back(at, end);
                        } else {
                            skipStart = at;
                            skipDepth = depth;
                        }
                    }
                }
            }
            if (!empty) ++open;
        } else if (nodeType == XmlNodeType_EndElement) {
            const UINT depth = open ? --open : 0;
            if (skipStart != std::wstring::npos && depth == skipDepth) {
                UINT line = 0, position = 0;
                reader->GetLineNumber(&line);
                reader->GetLinePosition(&position);
                size_t at = Detail::Offset(starts, markup, line, position);
                while (at != std::wstring::npos && at > 0 && markup[at] != L'<') --at;
                const size_t end = at == std::wstring::npos ? at : Detail::TagEnd(markup, at);
                if (end != std::wstring::npos) cuts.emplace_back(skipStart, end);
                skipStart = std::wstring::npos;
            }
        }
    }
    reader->Release();
    stream->Release();
    if (FAILED(hr) || cuts.empty()) return L"";

    std::wstring out = markup;
    for (auto it = cuts.rbegin(); it != cuts.rend(); ++it) Detail::CutKeepingLines(out, it->first, it->second);
    return out;
}

} // namespace DevToolsStyleSource
