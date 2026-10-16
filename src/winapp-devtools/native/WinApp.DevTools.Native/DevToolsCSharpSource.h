// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

// Reads the C# statement a Reactor call site points at, for the inspector, the in-app panel and Source.get.
// Text only: no compiler, so it follows brackets and skips strings and comments, which is what a factory call
// (`Button("+1", () => setCount(count + 1))`, possibly over several lines) needs.

#include <algorithm>
#include <cwctype>
#include <string>
#include <vector>

namespace DevToolsCSharpSource {

inline constexpr wchar_t kRedacted[] = L"<redacted>";

inline std::vector<std::wstring> SplitLines(const std::wstring& text)
{
    std::vector<std::wstring> lines;
    size_t start = 0;
    while (start <= text.size()) {
        size_t end = text.find(L'\n', start);
        std::wstring line = text.substr(start, end == std::wstring::npos ? std::wstring::npos : end - start);
        if (!line.empty() && line.back() == L'\r') line.pop_back();
        lines.push_back(std::move(line));
        if (end == std::wstring::npos) break;
        start = end + 1;
    }
    return lines;
}

// Scanner state carried across lines: inside a block comment, a verbatim/raw string, or neither.
struct ScanState
{
    int depth = 0;
    bool sawOpen = false;
    bool blockComment = false;
    int rawQuotes = 0;      // > 0 inside a raw string literal closed by that many quotes
    bool verbatim = false;  // inside @"..." that continues onto the next line
};

// Advances over one line from `from`, tracking bracket depth and skipping strings and comments.
inline void ScanLine(const std::wstring& line, size_t from, ScanState& s)
{
    size_t i = from;
    const size_t n = line.size();
    auto quotesAt = [&](size_t at) { size_t k = 0; while (at + k < n && line[at + k] == L'"') ++k; return k; };
    while (i < n) {
        if (s.blockComment) {
            const size_t end = line.find(L"*/", i);
            if (end == std::wstring::npos) return;
            s.blockComment = false;
            i = end + 2;
            continue;
        }
        if (s.rawQuotes > 0) {
            const size_t end = line.find(std::wstring((size_t)s.rawQuotes, L'"'), i);
            if (end == std::wstring::npos) return;
            i = end + (size_t)s.rawQuotes;
            s.rawQuotes = 0;
            continue;
        }
        if (s.verbatim) {
            while (i < n) {
                if (line[i] == L'"') {
                    if (i + 1 < n && line[i + 1] == L'"') { i += 2; continue; }
                    s.verbatim = false; ++i; break;
                }
                ++i;
            }
            continue;
        }
        const wchar_t c = line[i];
        if (c == L'/' && i + 1 < n && line[i + 1] == L'/') return;
        if (c == L'/' && i + 1 < n && line[i + 1] == L'*') { s.blockComment = true; i += 2; continue; }
        if (c == L'\'') {
            ++i;
            while (i < n && line[i] != L'\'') { if (line[i] == L'\\') ++i; ++i; }
            ++i;
            continue;
        }
        if (c == L'"' || ((c == L'@' || c == L'$') && i + 1 < n && (line[i + 1] == L'"' || line[i + 1] == L'@' || line[i + 1] == L'$'))) {
            size_t j = i;
            bool isVerbatim = false;
            while (j < n && (line[j] == L'@' || line[j] == L'$')) { if (line[j] == L'@') isVerbatim = true; ++j; }
            const size_t quotes = quotesAt(j);
            if (quotes >= 3) { s.rawQuotes = (int)quotes; i = j + quotes; continue; }
            if (isVerbatim) { s.verbatim = true; i = j + 1; continue; }
            i = j + 1;
            while (i < n && line[i] != L'"') { if (line[i] == L'\\') ++i; ++i; }
            ++i;
            continue;
        }
        if (c == L'(' || c == L'[' || c == L'{') { ++s.depth; s.sawOpen = true; }
        else if (c == L')' || c == L']' || c == L'}') --s.depth;
        ++i;
    }
}

// The statement that starts at `line` (1-based) and `column` (1-based; 0 = start of line): that line, and the lines
// after it until the brackets it opened close, at most `maxLines`. Indentation shared with the first line is removed;
// a statement cut short ends with "...". Empty when the line does not exist.
inline std::wstring Statement(const std::vector<std::wstring>& lines, unsigned int line, unsigned int column,
                              size_t maxLines = 8, size_t maxChars = 1200)
{
    if (line == 0 || line > lines.size() || maxLines == 0) return {};
    ScanState state;
    std::vector<std::wstring> picked;
    bool complete = false;
    for (size_t index = line - 1; index < lines.size() && picked.size() < maxLines; ++index) {
        const std::wstring& text = lines[index];
        const size_t from = index == line - 1 && column > 0 ? (std::min)((size_t)column - 1, text.size()) : 0;
        ScanLine(text, from, state);
        picked.push_back(text);
        if (!state.sawOpen || state.depth <= 0) { complete = true; break; }
    }

    size_t indent = 0;
    while (indent < picked.front().size() && (picked.front()[indent] == L' ' || picked.front()[indent] == L'\t')) ++indent;
    std::wstring out;
    for (size_t k = 0; k < picked.size(); ++k) {
        std::wstring text = picked[k];
        size_t strip = 0;
        while (strip < indent && strip < text.size() && (text[strip] == L' ' || text[strip] == L'\t')) ++strip;
        text.erase(0, strip);
        while (!text.empty() && (text.back() == L' ' || text.back() == L'\t')) text.pop_back();
        if (k) out += L'\n';
        out += text;
    }
    if (!complete) out += L"\n...";
    if (out.size() > maxChars) out = out.substr(0, maxChars) + L"...";
    return out;
}

inline bool MentionsPassword(const std::wstring& code)
{
    static const wchar_t kWord[] = L"password";
    for (size_t i = 0; i + 8 <= code.size(); ++i) {
        size_t k = 0;
        while (k < 8 && (wchar_t)towlower(code[i + k]) == kWord[k]) ++k;
        if (k == 8) return true;
    }
    return false;
}

// Code that mentions a password (`PasswordBox("hunter2")`, `Password = "..."`) shows no string literal values.
inline void Redact(std::wstring& code)
{
    if (!MentionsPassword(code)) return;
    std::wstring out;
    out.reserve(code.size());
    size_t i = 0;
    const size_t n = code.size();
    while (i < n) {
        const wchar_t c = code[i];
        if (c == L'/' && i + 1 < n && code[i + 1] == L'/') {
            const size_t end = code.find(L'\n', i);
            out.append(code, i, end == std::wstring::npos ? std::wstring::npos : end - i);
            if (end == std::wstring::npos) break;
            i = end;
            continue;
        }
        if (c == L'"' || ((c == L'@' || c == L'$') && i + 1 < n && (code[i + 1] == L'"' || code[i + 1] == L'@' || code[i + 1] == L'$'))) {
            size_t j = i;
            bool verbatim = false;
            while (j < n && (code[j] == L'@' || code[j] == L'$')) { if (code[j] == L'@') verbatim = true; ++j; }
            size_t quotes = 0;
            while (j + quotes < n && code[j + quotes] == L'"') ++quotes;
            if (quotes == 2 && !verbatim) { out.append(code, i, j + 2 - i); i = j + 2; continue; }  // ""
            const size_t open = quotes >= 3 ? quotes : 1;
            out.append(code, i, j + open - i);
            size_t k = j + open;
            if (open >= 3) {
                const size_t end = code.find(std::wstring(open, L'"'), k);
                k = end == std::wstring::npos ? n : end;
            } else {
                while (k < n && code[k] != L'\n') {
                    if (code[k] == L'"') {
                        if (verbatim && k + 1 < n && code[k + 1] == L'"') { k += 2; continue; }
                        break;
                    }
                    if (!verbatim && code[k] == L'\\') ++k;
                    ++k;
                }
            }
            out += kRedacted;
            if (k < n) out.append(code, k, open >= 3 ? open : 1);
            i = k < n ? k + (open >= 3 ? open : 1) : n;
            continue;
        }
        out += c;
        ++i;
    }
    code = std::move(out);
}

} // namespace DevToolsCSharpSource
