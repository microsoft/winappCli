// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

#include <cstddef>
#include <functional>
#include <string>

namespace DevToolsResInline
{
using ValueResolver = std::function<std::wstring(const wchar_t* ext, const std::wstring& key)>;
using StyleResolver = std::function<std::wstring(const wchar_t* ext, const std::wstring& key)>;

inline size_t CountOf(const std::wstring& hay, const wchar_t* needle)
{
    size_t n = 0, at = 0, len = wcslen(needle);
    while ((at = hay.find(needle, at)) != std::wstring::npos) { ++n; at += len; }
    return n;
}

// SUBSTITUTES ONLY IN ATTRIBUTE POSITION, AND ONLY WHEN THE EXTENSION IS THE WHOLE VALUE -- `="{Ext K}"`.
inline std::wstring Apply(const std::wstring& markup, const ValueResolver& value, const StyleResolver& style,
                          size_t* asked, size_t* leftBehind)
{
    size_t found = 0;
    std::wstring out = markup;
    for (const wchar_t* ext : { L"ThemeResource", L"StaticResource" }) {
        const std::wstring stylePrefix = std::wstring(L"Style=\"{") + ext + L" ";
        for (size_t at = 0; (at = out.find(stylePrefix, at)) != std::wstring::npos; ) {
            const size_t keyAt = at + stylePrefix.size();
            const size_t end = out.find(L"}\"", keyAt);
            if (end == std::wstring::npos) break;
            ++found;
            const std::wstring attrs = style(ext, out.substr(keyAt, end - keyAt));
            if (attrs.empty()) { at = end + 2; continue; }
            out.replace(at, end + 2 - at, attrs);
            at += attrs.size();
        }
        const std::wstring valPrefix = std::wstring(L"=\"{") + ext + L" ";
        for (size_t at = 0; (at = out.find(valPrefix, at)) != std::wstring::npos; ) {
            const size_t keyAt = at + valPrefix.size();
            const size_t end = out.find(L"}\"", keyAt);
            if (end == std::wstring::npos) break;
            ++found;
            const std::wstring lit = value(ext, out.substr(keyAt, end - keyAt));
            if (lit.empty()) { at = end + 2; continue; }
            out.replace(at + 2, end + 1 - (at + 2), lit);   // the extension only; keep `="` and the closing `"`
            at += 2 + lit.size();
        }
    }
    if (asked) *asked = found;
    if (leftBehind) *leftBehind = CountOf(out, L"=\"{ThemeResource ") + CountOf(out, L"=\"{StaticResource ");
    return out;
}

inline const wchar_t* FontWeightName(unsigned short w)
{
    switch (w) {
        case 100: return L"Thin";      case 200: return L"ExtraLight"; case 300: return L"Light";
        case 350: return L"SemiLight"; case 400: return L"Normal";     case 500: return L"Medium";
        case 600: return L"SemiBold";  case 700: return L"Bold";       case 800: return L"ExtraBold";
        case 900: return L"Black";     default:  return nullptr;
    }
}
}
