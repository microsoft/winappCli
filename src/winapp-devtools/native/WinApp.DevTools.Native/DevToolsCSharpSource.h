// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

// Reads the C# statement a Reactor call site points at, for the inspector, the in-app panel and Source.get.
// Text only: no compiler, so it follows brackets and skips strings and comments, which is what a factory call
// (`Button("+1", () => setCount(count + 1))`, possibly over several lines) needs.

#include <algorithm>
#include <cwctype>
#include <initializer_list>
#include <regex>
#include <string>
#include <utility>
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

// Replaces the value of each string literal whose opening quote (its index in `code`) `redact` selects with
// <redacted>. A literal ends at its line's end unless it is a raw literal.
template <typename Predicate>
inline void RedactLiterals(std::wstring& code, Predicate redact)
{
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
            const size_t close = k < n ? (std::min)(n, k + (open >= 3 ? open : 1)) : n;
            if (!redact(i)) { out.append(code, i, close - i); i = close; continue; }
            out.append(code, i, j + open - i);
            out += kRedacted;
            if (k < n) out.append(code, k, close - k);
            i = close;
            continue;
        }
        out += c;
        ++i;
    }
    code = std::move(out);
}

// Code that mentions a password (`PasswordBox("hunter2")`, `Password = "..."`) shows no string literal values.
inline void Redact(std::wstring& code)
{
    if (!MentionsPassword(code)) return;
    RedactLiterals(code, [](size_t) { return true; });
}

// The lines with comments and the contents of string and character literals replaced by spaces, so columns still line
// up and brackets or calls inside them are not code. Quotes stay so a literal still separates tokens.
inline std::vector<std::wstring> Mask(const std::vector<std::wstring>& lines)
{
    std::vector<std::wstring> masked;
    masked.reserve(lines.size());
    bool blockComment = false, verbatim = false;
    size_t rawQuotes = 0;
    for (const auto& source : lines) {
        std::wstring line = source;
        const size_t n = line.size();
        auto blank = [&](size_t from, size_t to) { for (size_t k = from; k < to && k < n; ++k) line[k] = L' '; };
        size_t i = 0;
        while (i < n) {
            if (blockComment) {
                const size_t end = line.find(L"*/", i);
                blank(i, end == std::wstring::npos ? n : end + 2);
                if (end == std::wstring::npos) break;
                blockComment = false;
                i = end + 2;
                continue;
            }
            if (rawQuotes > 0) {
                const size_t end = line.find(std::wstring(rawQuotes, L'"'), i);
                blank(i, end == std::wstring::npos ? n : end);
                if (end == std::wstring::npos) break;
                i = end + rawQuotes;
                rawQuotes = 0;
                continue;
            }
            if (verbatim) {
                while (i < n) {
                    if (line[i] == L'"') {
                        if (i + 1 < n && line[i + 1] == L'"') { line[i] = line[i + 1] = L' '; i += 2; continue; }
                        verbatim = false; ++i; break;
                    }
                    line[i++] = L' ';
                }
                continue;
            }
            const wchar_t c = line[i];
            if (c == L'/' && i + 1 < n && line[i + 1] == L'/') { blank(i, n); break; }
            if (c == L'/' && i + 1 < n && line[i + 1] == L'*') { blockComment = true; blank(i, i + 2); i += 2; continue; }
            if (c == L'\'') {
                size_t j = i + 1;
                while (j < n && line[j] != L'\'') { if (line[j] == L'\\') line[j++] = L' '; if (j < n) line[j++] = L' '; }
                i = j + 1;
                continue;
            }
            if (c == L'"' || ((c == L'@' || c == L'$') && i + 1 < n && (line[i + 1] == L'"' || line[i + 1] == L'@' || line[i + 1] == L'$'))) {
                size_t j = i;
                bool isVerbatim = false;
                while (j < n && (line[j] == L'@' || line[j] == L'$')) { if (line[j] == L'@') isVerbatim = true; ++j; }
                size_t quotes = 0;
                while (j + quotes < n && line[j + quotes] == L'"') ++quotes;
                if (quotes >= 3) { rawQuotes = quotes; i = j + quotes; continue; }
                if (isVerbatim) { verbatim = true; i = j + 1; continue; }
                i = j + 1;
                while (i < n && line[i] != L'"') { if (line[i] == L'\\') line[i++] = L' '; if (i < n) line[i++] = L' '; }
                ++i;
                continue;
            }
            ++i;
        }
        masked.push_back(std::move(line));
    }
    return masked;
}

namespace detail {

inline bool IsIdentifierChar(wchar_t c) { return std::iswalnum(c) || c == L'_'; }
inline bool IsIdentifierStart(wchar_t c) { return std::iswalpha(c) || c == L'_'; }

inline long long SkipSpaceBack(const std::wstring& text, long long from)
{
    while (from >= 0 && std::iswspace(text[(size_t)from])) --from;
    return from;
}

inline size_t SkipSpace(const std::wstring& text, size_t from, size_t end)
{
    while (from < end && std::iswspace(text[from])) ++from;
    return from;
}

inline std::wstring WordEndingAt(const std::wstring& text, long long end)
{
    if (end < 0 || !IsIdentifierChar(text[(size_t)end])) return {};
    long long start = end;
    while (start > 0 && IsIdentifierChar(text[(size_t)start - 1])) --start;
    return text.substr((size_t)start, (size_t)(end - start + 1));
}

inline bool OneOf(const std::wstring& word, std::initializer_list<const wchar_t*> words)
{
    for (const wchar_t* w : words) if (word == w) return true;
    return false;
}

inline bool IsExit(const std::wstring& w) { return OneOf(w, {L"return", L"throw", L"yield", L"goto"}); }
inline bool IsBlockHead(const std::wstring& w)
{
    return OneOf(w, {L"if", L"for", L"foreach", L"while", L"using", L"lock", L"switch", L"catch", L"fixed", L"when"});
}
inline bool IsBlockWord(const std::wstring& w)
{
    return OneOf(w, {L"else", L"try", L"finally", L"do", L"checked", L"unchecked", L"unsafe"});
}

// The hook kind a Use* call makes, as the live component reports it; empty for a custom or composite hook.
inline std::wstring HookKind(const std::wstring& call)
{
    static const std::pair<const wchar_t*, const wchar_t*> kKinds[] = {
        {L"UseState", L"state"}, {L"UseReducer", L"reducer"}, {L"UseRef", L"ref"}, {L"UseMemo", L"memo"},
        {L"UseCallback", L"memo"}, {L"UseEffect", L"effect"}, {L"UseContext", L"context"},
        {L"UsePersisted", L"persisted"}, {L"UseNavigationLifecycle", L"navigationLifecycle"},
    };
    for (const auto& [name, kind] : kKinds) if (call == name) return kind;
    return {};
}

inline long long Enclosing(const std::wstring& text, long long from)
{
    int depth = 0;
    for (long long i = (std::min)(from, (long long)text.size() - 1); i >= 0; --i) {
        if (text[(size_t)i] == L'}') ++depth;
        else if (text[(size_t)i] == L'{') { if (depth == 0) return i; --depth; }
    }
    return -1;
}

inline long long MatchingBack(const std::wstring& text, long long close, wchar_t open, wchar_t shut)
{
    int depth = 0;
    for (long long i = close; i >= 0; --i) {
        const wchar_t c = text[(size_t)i];
        if (c == shut) ++depth;
        else if (c == open && --depth == 0) return i;
        else if (open == L'<' && (c == L';' || c == L'{' || c == L'}')) return -1;
    }
    return -1;
}

// 'B' for a method, local function or lambda body; 'S' for a statement's block; '?' for anything else.
inline wchar_t Owner(const std::wstring& text, long long open)
{
    const long long i = SkipSpaceBack(text, open - 1);
    if (i >= 1 && text[(size_t)i] == L'>' && text[(size_t)i - 1] == L'=') return L'B';
    if (i >= 0 && text[(size_t)i] == L')') {
        const long long paren = MatchingBack(text, i, L'(', L')');
        if (paren < 0) return L'?';
        long long end = SkipSpaceBack(text, paren - 1);
        if (end >= 0 && text[(size_t)end] == L'>') end = SkipSpaceBack(text, MatchingBack(text, end, L'<', L'>') - 1);
        const std::wstring word = WordEndingAt(text, end);
        return word.empty() ? L'?' : IsBlockHead(word) ? L'S' : L'B';
    }
    if (i < 0 || text[(size_t)i] == L';' || text[(size_t)i] == L'{' || text[(size_t)i] == L'}') return L'S';
    return IsBlockWord(WordEndingAt(text, i)) ? L'S' : L'?';
}

inline long long BodyOpen(const std::wstring& text, size_t position)
{
    long long open = position < text.size() && text[position] == L'{' ? (long long)position : Enclosing(text, (long long)position - 1);
    while (open >= 0) {
        const wchar_t owner = Owner(text, open);
        if (owner == L'B') return open;
        if (owner != L'S') return -1;
        open = Enclosing(text, open - 1);
    }
    return -1;
}

inline long long Matching(const std::wstring& text, size_t open)
{
    int depth = 0;
    for (size_t i = open; i < text.size(); ++i) {
        if (text[i] == L'{') ++depth;
        else if (text[i] == L'}' && --depth == 0) return (long long)i;
    }
    return -1;
}

// `UseState(` or `UseState<int>(`.
inline bool IsCall(const std::wstring& text, size_t wordEnd, size_t end)
{
    size_t next = SkipSpace(text, wordEnd, end);
    if (next < end && text[next] == L'<') {
        int depth = 0;
        for (; next < end; ++next) {
            const wchar_t c = text[next];
            if (c == L'<') ++depth;
            else if (c == L'>' && --depth == 0) break;
            else if (c == L';' || c == L'{' || c == L'}' || c == L'=') return false;
        }
        next = SkipSpace(text, next + 1, end);
    }
    return next < end && text[next] == L'(';
}

// Where the hook's call starts, with a `ctx.` or `this.` receiver; -1 for any other qualifier or a declaration.
inline long long Receiver(const std::wstring& text, size_t call, size_t statement)
{
    const long long before = SkipSpaceBack(text, (long long)call - 1);
    if (before < (long long)statement || text[(size_t)before] != L'.')
        return before >= (long long)statement && IsIdentifierChar(text[(size_t)before]) ? -1 : (long long)call;
    const long long end = SkipSpaceBack(text, before - 1);
    long long start = end;
    while (start > (long long)statement && IsIdentifierChar(text[(size_t)start - 1])) --start;
    if (end < (long long)statement || start > end || !IsIdentifierStart(text[(size_t)start])) return -1;
    const long long outer = SkipSpaceBack(text, start - 1);
    // `ctx.` and `this.`: a parameter, local or the component itself. A PascalCase qualifier is a type's method.
    return (outer >= (long long)statement && (text[(size_t)outer] == L'.' || IsIdentifierChar(text[(size_t)outer]))) ||
           std::iswupper(text[(size_t)start]) ? -1 : start;
}

// What a hook call is assigned to: nothing, `var name =`, `Type name =`, `name =`, or `var (name, setName) =`.
// False for anything else (a condition, an operator), which is not straight-line.
inline bool HookTarget(const std::wstring& prefix, std::wstring* name)
{
    name->clear();
    const size_t first = prefix.find_first_not_of(L" \t\r\n");
    if (first == std::wstring::npos) return true;
    const std::wstring text = prefix.substr(first, prefix.find_last_not_of(L" \t\r\n") - first + 1);
    static const std::wregex assignment(
        L"^(?:([A-Za-z_][\\w.]*(?:<[^;{}()=]*>)?(?:\\[\\])*\\??)\\s+)?(?:this\\s*\\.\\s*)?@?([A-Za-z_]\\w*)\\s*=$");
    static const std::wregex deconstruction(L"^(?:var\\s*)?\\(([^()]*)\\)\\s*=$");
    std::wsmatch m;
    if (std::regex_match(text, m, assignment)) {
        const std::wstring type = m[1].matched ? m[1].str() : std::wstring();
        if (IsExit(type) || IsBlockHead(type) || IsBlockWord(type)) return false;
        *name = m[2].str();
        return true;
    }
    if (std::regex_match(text, m, deconstruction)) {
        std::wstring item = m[1].str().substr(0, m[1].str().find(L','));
        while (!item.empty() && std::iswspace(item.back())) item.pop_back();
        const size_t space = item.find_last_of(L" \t");
        std::wstring last = space == std::wstring::npos ? item : item.substr(space + 1);
        if (!last.empty() && last[0] == L'@') last.erase(0, 1);
        if (last.empty() || last == L"_") return true;
        if (!IsIdentifierStart(last[0])) return false;
        *name = last;
        return true;
    }
    return false;
}

} // namespace detail

// The C# names of a Reactor component's hooks, read from its render body when Reactor recorded none (DevTools attached
// after the app started): one per hook, empty for a hook assigned to nothing (`UseEffect(...)`). `line` and `column`
// (1-based; 0 when unknown) are where the render body starts, as the PDB records it; `kinds` are the live hooks'
// kinds. Only straight-line render code counts: a hook in a branch, a loop, a lambda or an argument, a custom hook, an
// early return, or any count or kind mismatch returns false and names nothing. The CLI's ReactorHookSource is the same.
inline bool HookNames(const std::vector<std::wstring>& lines, unsigned int line, unsigned int column,
                      const std::vector<std::wstring>& kinds, std::vector<std::wstring>* names)
{
    using namespace detail;
    names->clear();
    if (line == 0 || line > lines.size() || kinds.empty()) return false;
    const auto masked = Mask(lines);
    std::wstring text;
    size_t offset = 0;
    for (size_t k = 0; k < masked.size(); ++k) {
        if (k) text += L'\n';
        if (k == line - 1) offset = text.size();
        text += masked[k];
    }
    const std::wstring& row = masked[line - 1];
    const size_t at = column > 0 ? (std::min)((size_t)column - 1, row.size())
                                 : (std::min)(row.find_first_not_of(L" \t"), row.size());
    const long long open = BodyOpen(text, offset + at);
    const long long close = open < 0 ? -1 : Matching(text, (size_t)open);
    if (close < 0) return false;

    std::vector<std::pair<std::wstring, std::wstring>> calls;  // kind, name
    std::vector<bool> braces;  // true for a lambda's block
    int parens = 0;
    size_t statement = (size_t)open + 1;
    bool exited = false;
    const size_t end = (size_t)close;
    for (size_t i = (size_t)open + 1; i < end; ++i) {
        const wchar_t c = text[i];
        if (c == L'(' || c == L'[') { ++parens; continue; }
        if (c == L')' || c == L']') { --parens; continue; }
        if (c == L'{') {
            const long long before = SkipSpaceBack(text, (long long)i - 1);
            braces.push_back(before >= 1 && text[(size_t)before] == L'>' && text[(size_t)before - 1] == L'=');
            continue;
        }
        if (c == L'}') {
            if (!braces.empty()) braces.pop_back();
            if (braces.empty() && parens == 0) statement = i + 1;
            continue;
        }
        if (c == L';') { if (braces.empty() && parens == 0) statement = i + 1; continue; }
        if (!IsIdentifierStart(c) || (i > (size_t)open + 1 && IsIdentifierChar(text[i - 1]))) continue;
        size_t wordEnd = i;
        while (wordEnd < end && IsIdentifierChar(text[wordEnd])) ++wordEnd;
        const std::wstring word = text.substr(i, wordEnd - i);
        const bool inLambda = std::find(braces.begin(), braces.end(), true) != braces.end();
        if (IsExit(word) && parens == 0 && !inLambda) exited = true;
        if (word.size() > 3 && word.compare(0, 3, L"Use") == 0 && std::iswupper(word[3]) && IsCall(text, wordEnd, end)) {
            const std::wstring kind = HookKind(word);
            if (exited || parens != 0 || !braces.empty() || kind.empty()) return false;
            const long long receiver = Receiver(text, i, statement);
            std::wstring name;
            if (receiver < 0 || !HookTarget(text.substr(statement, (size_t)receiver - statement), &name)) return false;
            calls.emplace_back(kind, name);
        }
        i = wordEnd - 1;
    }
    if (calls.size() != kinds.size()) return false;
    for (size_t k = 0; k < calls.size(); ++k) if (calls[k].first != kinds[k]) return false;
    bool any = false;
    for (auto& call : calls) { any = any || !call.second.empty(); names->push_back(call.second); }
    if (!any) names->clear();
    return any;
}

} // namespace DevToolsCSharpSource
