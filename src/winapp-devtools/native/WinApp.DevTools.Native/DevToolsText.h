// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once
#include <windows.h>
#include <string>

inline std::string DevToolsToUtf8(const std::wstring& w)
{
    if (w.empty()) return {};
    const int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    if (n <= 0) return {};
    std::string s((size_t)n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), s.data(), n, nullptr, nullptr);
    return s;
}

inline std::wstring DevToolsFromUtf8(const char* p, size_t len)
{
    if (!p || len == 0) return {};
    const int n = MultiByteToWideChar(CP_UTF8, 0, p, (int)len, nullptr, 0);
    if (n <= 0) return {};
    std::wstring w((size_t)n, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, p, (int)len, w.data(), n);
    return w;
}

inline std::wstring DevToolsFromUtf8(const std::string& s) { return DevToolsFromUtf8(s.data(), s.size()); }

inline std::wstring DevToolsXmlEscape(const std::wstring& text, bool encodeWhitespace = false)
{
    std::wstring out = !text.empty() && text.front() == L'{' ? L"{}" : L"";
    out.reserve(out.size() + text.size() + 8);
    for (const wchar_t c : text) {
        switch (c) {
        case L'&': out += L"&amp;"; break;
        case L'<': out += L"&lt;"; break;
        case L'>': out += L"&gt;"; break;
        case L'"': out += L"&quot;"; break;
        case L'\'': out += L"&apos;"; break;
        case L'\r': if (encodeWhitespace) out += L"&#xD;"; else out += c; break;
        case L'\n': if (encodeWhitespace) out += L"&#xA;"; else out += c; break;
        case L'\t': if (encodeWhitespace) out += L"&#x9;"; else out += c; break;
        default: out += c; break;
        }
    }
    return out;
}
