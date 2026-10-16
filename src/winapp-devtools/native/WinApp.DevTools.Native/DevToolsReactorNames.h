// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

// The C# name of a Reactor element, read from the text of its call site when Reactor did not publish one (`name=`).
// XAML names an element with x:Name; a Reactor element has a name when the C# assigns it to something:
//
//   var incrementButton = Button("+1", inc).Margin(4);     local (or `Element incrementButton =`)
//   header = Text("Tasks");                                 assignment to a field, property or local
//   Element IncrementButton => Button("+1", inc);           expression-bodied property
//   Element IncrementButton() => Button("+1", inc);         method or local function
//
// The call must be what is assigned: `var row = HStack(Text("a"))` names the HStack, not the Text inside it. With a
// column, everything between the `=`/`=>` and the call must be whitespace or a qualifier (`Ui.`). Without one, the
// first call on the line is taken. A statement split after the operator (`var header =` then `Text(...)` on the next
// line) is read from the line above. Names found this way are weaker than published ones: the line may have changed
// since the build, so callers report them as derived from source text.

#include <cwctype>
#include <regex>
#include <string>

namespace DevToolsReactorNames {

inline bool IsIdentifier(const std::wstring& text)
{
    if (text.empty() || !(std::iswalpha(text[0]) || text[0] == L'_')) return false;
    for (wchar_t c : text) if (!(std::iswalnum(c) || c == L'_')) return false;
    return true;
}

inline bool IsKeyword(const std::wstring& word)
{
    static const wchar_t* const kKeywords[] = {
        L"return", L"await", L"yield", L"case", L"else", L"if", L"while", L"for", L"foreach", L"switch", L"using",
        L"new", L"throw", L"in", L"out", L"ref", L"is", L"as", L"var", L"this", L"base", L"null", L"true", L"false",
        L"get", L"set", L"init", L"value", L"default", L"typeof", L"nameof", L"when", L"select", L"where", L"let",
    };
    for (const wchar_t* keyword : kKeywords) if (word == keyword) return true;
    return false;
}

// What may sit between the operator and the call: whitespace and dotted qualifiers, nothing that opens a call.
inline bool OnlyQualifiers(const std::wstring& text)
{
    for (wchar_t c : text) {
        if (!(std::iswspace(c) || std::iswalnum(c) || c == L'_' || c == L'.' || c == L'@')) return false;
    }
    return true;
}

namespace detail {

inline const std::wregex& Modifiers()
{
    static const std::wregex re(
        L"^\\s*(?:(?:public|private|protected|internal|static|readonly|const|required|override|virtual|sealed|unsafe|volatile|partial|new)\\s+)*");
    return re;
}

// `[Type ]name = rest` (not `==` or `=>`).
inline const std::wregex& Assignment()
{
    static const std::wregex re(
        L"^(?:([A-Za-z_][\\w.]*(?:<[^=;()]*>)?(?:\\[\\])?\\??)\\s+)?(?:this\\.)?@?([A-Za-z_]\\w*)\\s*=(?![=>])(.*)$");
    return re;
}

// `Type name => rest` or `Type name(params) => rest`.
inline const std::wregex& ExpressionBody()
{
    static const std::wregex re(
        L"^([A-Za-z_][\\w.]*(?:<[^=;()]*>)?\\??)\\s+@?([A-Za-z_]\\w*)\\s*(\\([^()]*\\))?\\s*=>(.*)$");
    return re;
}

inline std::wstring TrimRight(std::wstring text)
{
    while (!text.empty() && std::iswspace(text.back())) text.pop_back();
    return text;
}

// The name a statement start declares, and the text after its operator; empty when the line declares nothing.
inline std::wstring Declares(const std::wstring& line, std::wstring* rest, const std::wstring& statement)
{
    std::wsmatch modifiers;
    std::wstring body = line;
    if (std::regex_search(line, modifiers, Modifiers())) body = line.substr(static_cast<size_t>(modifiers.length(0)));
    std::wsmatch m;
    if (std::regex_match(body, m, ExpressionBody())) {
        const std::wstring type = m[1].str(), name = m[2].str();
        if (IsKeyword(type) || IsKeyword(name)) return {};
        *rest = m[4].str();
        return name;
    }
    if (std::regex_match(body, m, Assignment())) {
        const std::wstring type = m[1].matched ? m[1].str() : std::wstring();
        const std::wstring name = m[2].str();
        if ((!type.empty() && IsKeyword(type) && type != L"var") || IsKeyword(name)) return {};
        // `Content = Button(...),` is a member of an object initializer, not a variable.
        if (type.empty() && !TrimRight(statement).empty() && TrimRight(statement).back() == L',') return {};
        *rest = m[3].str();
        return name;
    }
    return {};
}

} // namespace detail

// Whether a factory call names the element kind it creates: `Button(` makes a Button, `VStack(` a Stack,
// `TextBlock(` a TextBlock, `Component<Counter>(` a Component. Compared loosely, because factory and record names differ.
inline bool CallMakes(const std::wstring& call, const std::wstring& kind)
{
    if (kind.empty()) return true;
    if (call.empty()) return false;
    auto lower = [](std::wstring text) { for (auto& c : text) c = static_cast<wchar_t>(std::towlower(c)); return text; };
    const std::wstring a = lower(call), b = lower(kind);
    return a.find(b) != std::wstring::npos || b.find(a) != std::wstring::npos;
}

// The name the call at `column` (1-based; 0 when unknown) on `line` is assigned to. `previous` is the line above, for
// a statement split after its operator. Without a column, `kind` (the element kind, such as Button) picks which call on
// the line is the element's own, so `var rows = items.Select(i => Text(i))` names no Text.
inline std::wstring FromSource(const std::wstring& line, unsigned int column, const std::wstring& previous = L"",
                               const std::wstring& kind = L"")
{
    auto declared = [](const std::wstring& before, const std::wstring& statement) -> std::wstring {
        std::wstring rest;
        const std::wstring name = detail::Declares(before, &rest, statement);
        return !name.empty() && OnlyQualifiers(rest) && IsIdentifier(name) ? name : std::wstring();
    };
    // The text before the call: up to the column when known. Without one, each call on the line is tried in turn,
    // with the call's own name dropped so only what precedes it is checked.
    std::wstring first;
    if (column > 0) {
        if (column - 1 > line.size()) return {};
        first = line.substr(0, column - 1);
        if (std::wstring name = declared(first, line); !name.empty()) return name;
    } else {
        bool any = false;
        for (size_t open = line.find(L'('); open != std::wstring::npos; open = line.find(L'(', open + 1)) {
            std::wstring before = line.substr(0, open);
            size_t end = before.size();
            while (end && std::iswspace(before[end - 1])) --end;
            // Type arguments: `Component<Counter, CounterProps>(`.
            if (end && before[end - 1] == L'>') {
                int depth = 0;
                while (end) {
                    const wchar_t c = before[--end];
                    if (c == L'>') ++depth;
                    else if (c == L'<' && --depth == 0) break;
                }
            }
            const size_t nameEnd = end;
            while (end && (std::iswalnum(before[end - 1]) || before[end - 1] == L'_' || before[end - 1] == L'@')) --end;
            const std::wstring call = before.substr(end, nameEnd - end);
            before.resize(end);
            if (!any) { first = before; any = true; }
            if (!CallMakes(call, kind)) continue;
            if (std::wstring name = declared(before, line); !name.empty()) return name;
        }
        if (!any) return {};
    }

    // `var header =` on the line above, the call on this one. A line that starts with `.` continues an expression
    // (a modifier chain), so it is not the call being assigned.
    if (!OnlyQualifiers(first)) return {};
    if (const size_t start = first.find_first_not_of(L" \t"); start != std::wstring::npos && first[start] == L'.') return {};
    const std::wstring above = detail::TrimRight(previous);
    if (above.empty() || (above.back() != L'=' && !(above.size() >= 2 && above.compare(above.size() - 2, 2, L"=>") == 0)))
        return {};
    return declared(above, above);
}

} // namespace DevToolsReactorNames
