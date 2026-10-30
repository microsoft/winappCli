// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

// Finds the C# arguments a password parameter receives, without a compiler, the same rule as the CLI's
// CSharpDeclarations and CSharpArguments. A positional argument is matched to its parameter through the declaration of
// the called method, constructor or record (by name, type-argument count and argument count); a named argument
// (`userPassword: "..."`) and an initializer member (`Password = "..."`) by their name. A parameter whose name ends in
// "Password" is a secret, the rule XAML properties follow. When the declaration is not found, or its overloads
// disagree, the argument is left alone.

#include "DevToolsCSharpSource.h"

#include <map>
#include <regex>
#include <string>
#include <utility>
#include <vector>

namespace DevToolsCSharpArguments {

struct Parameter
{
    std::wstring name, type;
    bool isParams = false, hasDefault = false, isThis = false;
};

// kind: 'T' a type's primary constructor (`record Login(string User, string Password)`), 'C' a constructor, 'M' a method.
struct Declaration
{
    std::wstring name;
    wchar_t kind = L'M';
    std::vector<std::wstring> typeParameters;
    std::vector<Parameter> parameters;
};

namespace detail {

using DevToolsCSharpSource::detail::IsIdentifierChar;
using DevToolsCSharpSource::detail::IsIdentifierStart;
using DevToolsCSharpSource::detail::OneOf;

inline bool IsSecretName(const std::wstring& name)
{
    static const wchar_t kSuffix[] = L"password";
    if (name.size() < 8) return false;
    for (size_t k = 0; k < 8; ++k)
        if ((wchar_t)std::towlower(name[name.size() - 8 + k]) != kSuffix[k]) return false;
    return true;
}

inline bool IsTypeKeyword(const std::wstring& w) { return OneOf(w, {L"record", L"class", L"struct", L"interface"}); }
inline bool IsModifier(const std::wstring& w)
{
    return OneOf(w, {L"public", L"private", L"protected", L"internal", L"static", L"extern", L"unsafe", L"file"});
}
inline bool IsNotType(const std::wstring& w)
{
    return OneOf(w, {L"return", L"new", L"await", L"else", L"case", L"in", L"is", L"throw", L"when", L"and", L"or", L"not",
                     L"yield", L"using", L"lock", L"if", L"while", L"for", L"foreach", L"switch", L"catch", L"fixed", L"typeof",
                     L"sizeof", L"nameof", L"default", L"checked", L"unchecked", L"out", L"ref", L"as", L"from", L"select",
                     L"where", L"let", L"join", L"on", L"equals", L"by", L"group", L"into", L"orderby", L"operator", L"this",
                     L"base", L"goto", L"do", L"var", L"with", L"stackalloc", L"params", L"get", L"set", L"init", L"add",
                     L"remove", L"readonly", L"const", L"event", L"implicit", L"explicit", L"abstract", L"virtual",
                     L"override", L"sealed", L"async", L"partial", L"required", L"volatile", L"delegate"});
}
inline bool IsNotCall(const std::wstring& w)
{
    return OneOf(w, {L"if", L"while", L"for", L"foreach", L"switch", L"catch", L"using", L"lock", L"fixed", L"typeof",
                     L"sizeof", L"nameof", L"default", L"checked", L"unchecked", L"return", L"when", L"await", L"throw", L"is",
                     L"and", L"or", L"not", L"in", L"out", L"ref", L"var", L"stackalloc"});
}

inline std::wstring Trim(const std::wstring& text)
{
    const size_t first = text.find_first_not_of(L" \t\r\n");
    if (first == std::wstring::npos) return {};
    return text.substr(first, text.find_last_not_of(L" \t\r\n") - first + 1);
}

inline size_t SkipSpace(const std::wstring& text, size_t from, size_t end)
{
    while (from < end && std::iswspace(text[from])) ++from;
    return from;
}

inline long long SkipSpaceBack(const std::wstring& text, long long from)
{
    while (from >= 0 && std::iswspace(text[(size_t)from])) --from;
    return from;
}

// Splits at commas outside (), [], {} and <> (a declaration has no comparisons).
inline std::vector<std::wstring> SplitDeclared(const std::wstring& list)
{
    std::vector<std::wstring> parts;
    int depth = 0;
    size_t start = 0;
    for (size_t i = 0; i < list.size(); ++i) {
        const wchar_t c = list[i];
        if (c == L'(' || c == L'[' || c == L'{' || c == L'<') ++depth;
        else if (c == L')' || c == L']' || c == L'}' || c == L'>') --depth;
        else if (c == L',' && depth == 0) { parts.push_back(list.substr(start, i - start)); start = i + 1; }
    }
    parts.push_back(list.substr(start));
    return parts;
}

// The parameters of a declaration; false when the list is not one (`Foo(a, b)` is a call).
inline bool Parameters(const std::wstring& list, std::vector<Parameter>* out)
{
    out->clear();
    if (Trim(list).empty()) return true;
    static const std::wregex pattern(
        L"^\\s*(?:\\[[^\\]]*\\]\\s*)*((?:(?:this|params|ref|out|in|scoped|readonly)\\s+)*)"
        L"([A-Za-z_][\\w.]*(?:\\s*<[^()]*>)?\\??(?:\\s*\\[[\\s,]*\\])*\\??|\\([^()]*\\)\\??)\\s+@?([A-Za-z_]\\w*)\\s*(=[\\s\\S]*)?$");
    for (const auto& part : SplitDeclared(list)) {
        std::wsmatch m;
        if (!std::regex_match(part, m, pattern) || IsNotType(m[2].str())) return false;
        Parameter p;
        p.name = m[3].str();
        for (wchar_t c : m[2].str()) if (c != L' ') p.type += c;
        const std::wstring mods = m[1].str();
        p.isParams = mods.find(L"params") != std::wstring::npos;
        p.isThis = mods.find(L"this") != std::wstring::npos;
        p.hasDefault = m[4].matched;
        out->push_back(std::move(p));
    }
    return true;
}

inline std::wstring WordEndingAt(const std::wstring& text, long long end, long long* start)
{
    if (end < 0 || !IsIdentifierChar(text[(size_t)end])) return {};
    long long s = end;
    while (s > 0 && IsIdentifierChar(text[(size_t)s - 1])) --s;
    *start = s;
    return text.substr((size_t)s, (size_t)(end - s + 1));
}

// What the word before a name says about it: a type keyword, a modifier (a constructor) or a return type (a method).
inline wchar_t Kind(const std::wstring& text, size_t start)
{
    const long long p = SkipSpaceBack(text, (long long)start - 1);
    if (p < 0 || text[(size_t)p] == L';' || text[(size_t)p] == L'{' || text[(size_t)p] == L'}') return L'C';
    const wchar_t c = text[(size_t)p];
    if (c == L']') {
        const long long q = SkipSpaceBack(text, p - 1);
        return q >= 0 && (text[(size_t)q] == L'[' || text[(size_t)q] == L',') ? L'M' : L'C';
    }
    if (c == L'>') return p > 0 && text[(size_t)p - 1] == L'=' ? 0 : L'M';
    if (c == L'?') return p > 0 && (IsIdentifierChar(text[(size_t)p - 1]) || text[(size_t)p - 1] == L'>' || text[(size_t)p - 1] == L']') ? L'M' : 0;
    if (!IsIdentifierChar(c)) return 0;
    long long wordStart = 0;
    const std::wstring word = WordEndingAt(text, p, &wordStart);
    if (wordStart > 0 && text[(size_t)wordStart - 1] == L'.') return L'M';
    if (IsTypeKeyword(word)) return L'T';
    if (IsModifier(word)) return L'C';
    return IsNotType(word) && word != L"partial" ? 0 : L'M';
}

inline long long MatchParenDeclared(const std::wstring& text, size_t open)
{
    int depth = 0;
    for (size_t i = open; i < text.size(); ++i) {
        const wchar_t c = text[i];
        if (c == L'(') ++depth;
        else if (c == L')' && --depth == 0) return (long long)i;
        else if (c == L';' || c == L'{' || c == L'}') return -1;
    }
    return -1;
}

} // namespace detail

// The parameter lists C# declares, by name.
class Declarations
{
public:
    const std::vector<Declaration>& Find(const std::wstring& name) const
    {
        static const std::vector<Declaration> none;
        const auto found = byName_.find(name);
        return found == byName_.end() ? none : found->second;
    }

    bool Empty() const { return byName_.empty(); }

    // Adds the declarations of one file.
    void Add(const std::vector<std::wstring>& lines)
    {
        using namespace detail;
        std::wstring text;
        for (const auto& line : DevToolsCSharpSource::Mask(lines)) { text += line; text += L'\n'; }
        for (size_t i = 0; i < text.size(); ++i) {
            if (!IsIdentifierStart(text[i]) || (i > 0 && IsIdentifierChar(text[i - 1]))) continue;
            size_t end = i;
            while (end < text.size() && IsIdentifierChar(text[end])) ++end;
            Declaration declaration;
            if (Read(text, i, end, &declaration)) byName_[declaration.name].push_back(std::move(declaration));
            i = end - 1;
        }
    }

    // Adds the declarations another index holds, after this one's: the same result as Add-ing its files here.
    void Merge(const Declarations& other)
    {
        for (const auto& [name, declarations] : other.byName_) {
            auto& mine = byName_[name];
            mine.insert(mine.end(), declarations.begin(), declarations.end());
        }
    }

private:
    static bool Read(const std::wstring& text, size_t start, size_t end, Declaration* out)
    {
        using namespace detail;
        const std::wstring name = text.substr(start, end - start);
        if (IsNotType(name) || IsTypeKeyword(name) || IsModifier(name)) return false;
        size_t at = SkipSpace(text, end, text.size());
        std::vector<std::wstring> typeParameters;
        if (at < text.size() && text[at] == L'<') {
            const size_t close = text.find(L'>', at);
            if (close == std::wstring::npos) return false;
            std::wstring list = text.substr(at + 1, close - at - 1);
            size_t from = 0;
            while (true) {
                const size_t comma = list.find(L',', from);
                std::wstring item = Trim(list.substr(from, comma == std::wstring::npos ? std::wstring::npos : comma - from));
                if (item.rfind(L"in ", 0) == 0 || item.rfind(L"out ", 0) == 0) item = Trim(item.substr(item.find(L' ') + 1));
                if (item.empty() || !IsIdentifierStart(item[0])) return false;
                for (wchar_t c : item) if (!IsIdentifierChar(c)) return false;
                typeParameters.push_back(item);
                if (comma == std::wstring::npos) break;
                from = comma + 1;
            }
            at = SkipSpace(text, close + 1, text.size());
        }
        if (at >= text.size() || text[at] != L'(') return false;
        const wchar_t kind = Kind(text, start);
        if (!kind) return false;
        const long long close = MatchParenDeclared(text, at);
        if (close < 0) return false;
        const size_t after = SkipSpace(text, (size_t)close + 1, text.size());
        const bool follows = after >= text.size() || text[after] == L'{' || text[after] == L':' || text[after] == L';' ||
            (text[after] == L'=' && after + 1 < text.size() && text[after + 1] == L'>') ||
            (text.compare(after, 5, L"where") == 0 && (after + 5 >= text.size() || !IsIdentifierChar(text[after + 5])));
        if (!follows || !Parameters(text.substr(at + 1, (size_t)close - at - 1), &out->parameters)) return false;
        out->name = name;
        out->kind = kind;
        out->typeParameters = std::move(typeParameters);
        return true;
    }

    std::map<std::wstring, std::vector<Declaration>> byName_;
};

namespace detail {

struct TypeRef
{
    std::wstring name;
    std::vector<std::wstring> arguments;
    bool hasArguments = false;
};

using Ranges = std::vector<std::pair<size_t, size_t>>;

inline std::vector<std::wstring> SplitTypes(const std::wstring& list)
{
    std::vector<std::wstring> parts;
    int depth = 0;
    size_t start = 0;
    for (size_t i = 0; i < list.size(); ++i) {
        const wchar_t c = list[i];
        if (c == L'<' || c == L'(' || c == L'[') ++depth;
        else if (c == L'>' || c == L')' || c == L']') --depth;
        else if (c == L',' && depth == 0) { parts.push_back(Trim(list.substr(start, i - start))); start = i + 1; }
    }
    parts.push_back(Trim(list.substr(start)));
    return parts;
}

inline bool ParseType(const std::wstring& type, TypeRef* out)
{
    static const std::wregex pattern(L"^([A-Za-z_][\\w.]*)(?:\\s*<([\\s\\S]*)>)?\\??$");
    std::wsmatch m;
    const std::wstring text = Trim(type);
    if (!std::regex_match(text, m, pattern)) return false;
    std::wstring name = m[1].str();
    const size_t dot = name.find_last_of(L'.');
    out->name = dot == std::wstring::npos ? name : name.substr(dot + 1);
    out->hasArguments = m[2].matched;
    out->arguments = m[2].matched ? SplitTypes(m[2].str()) : std::vector<std::wstring>();
    return true;
}

class Walker
{
public:
    Walker(const std::wstring& text, const Declarations& declarations, Ranges& ranges)
        : text_(text), declarations_(declarations), ranges_(ranges) {}

    void Walk(size_t start, size_t end, const TypeRef* expected, int depth = 0)
    {
        if (depth > 64) return;
        const size_t first = SkipSpace(text_, start, end);
        size_t i = start;
        while (i < end) {
            if (!IsIdentifierStart(text_[i]) || (i > 0 && IsIdentifierChar(text_[i - 1]))) { ++i; continue; }
            size_t wordEnd = i;
            while (wordEnd < end && IsIdentifierChar(text_[wordEnd])) ++wordEnd;
            const std::wstring word = text_.substr(i, wordEnd - i);
            if (word == L"new") { i = New(wordEnd, end, i == first ? expected : nullptr, i, depth); continue; }
            if (word == L"with") {
                const size_t brace = SkipSpace(text_, wordEnd, end);
                if (brace < end && text_[brace] == L'{') { i = Initializer(brace, end, depth); continue; }
            }
            size_t at = SkipSpace(text_, wordEnd, end);
            TypeRef callee;
            callee.name = word;
            if (at < end && text_[at] == L'<') {
                const long long close = GenericEnd(at, end);
                if (close > 0) {
                    callee.hasArguments = true;
                    for (const auto& [s, e] : SplitTopLevel(at + 1, (size_t)close)) callee.arguments.push_back(Trim(text_.substr(s, e - s)));
                    at = SkipSpace(text_, (size_t)close + 1, end);
                }
            }
            if (at < end && text_[at] == L'(' && !IsNotCall(word)) {
                const long long before = SkipSpaceBack(text_, (long long)i - 1);
                i = Call(&callee, before >= 0 && text_[(size_t)before] == L'.', at, end, false, depth);
                continue;
            }
            i = wordEnd;
        }
    }

private:
    size_t New(size_t wordEnd, size_t end, const TypeRef* expected, size_t newAt, int depth)
    {
        const size_t at = SkipSpace(text_, wordEnd, end);
        if (at >= end) return end;
        if (text_[at] == L'(') {
            TypeRef declared;
            const TypeRef* type = expected ? expected : DeclaredType(newAt, &declared) ? &declared : nullptr;
            return InitializerAfter(Call(type, false, at, end, true, depth), end, depth);
        }
        if (text_[at] == L'{') return Initializer(at, end, depth);
        if (!IsIdentifierStart(text_[at])) return at;
        size_t typeEnd = at;
        while (typeEnd < end && (IsIdentifierChar(text_[typeEnd]) || text_[typeEnd] == L'.')) ++typeEnd;
        TypeRef callee;
        callee.name = text_.substr(at, typeEnd - at);
        if (const size_t dot = callee.name.find_last_of(L'.'); dot != std::wstring::npos) callee.name = callee.name.substr(dot + 1);
        size_t after = SkipSpace(text_, typeEnd, end);
        if (after < end && text_[after] == L'<') {
            const long long close = GenericEnd(after, end);
            if (close > 0) {
                callee.hasArguments = true;
                for (const auto& [s, e] : SplitTopLevel(after + 1, (size_t)close)) callee.arguments.push_back(Trim(text_.substr(s, e - s)));
                after = SkipSpace(text_, (size_t)close + 1, end);
            }
        }
        if (after < end && text_[after] == L'(') return InitializerAfter(Call(&callee, false, after, end, true, depth), end, depth);
        return after < end && text_[after] == L'{' ? Initializer(after, end, depth) : after;
    }

    size_t InitializerAfter(size_t next, size_t end, int depth)
    {
        const size_t brace = SkipSpace(text_, next, end);
        return brace < end && text_[brace] == L'{' ? Initializer(brace, end, depth) : next;
    }

    // `{ Name = value, ... }`: a member whose name is a password's has its value redacted.
    size_t Initializer(size_t open, size_t end, int depth)
    {
        const size_t close = Matching(open, end, L'{', L'}');
        static const std::wregex member(L"^\\s*@?([A-Za-z_]\\w*)\\s*=(?![=>])");
        for (const auto& [start, stop] : SplitTopLevel(open + 1, close)) {
            size_t value = start;
            std::wsmatch m;
            const std::wstring part = text_.substr(start, stop - start);
            if (std::regex_search(part, m, member, std::regex_constants::match_continuous)) {
                value = start + (size_t)m.length(0);
                if (IsSecretName(m[1].str())) ranges_.emplace_back(value, stop);
            }
            Walk(value, stop, nullptr, depth + 1);
        }
        return (std::min)(close + 1, end);
    }

    struct Argument { size_t start, valueStart, end; std::wstring name; bool named; };
    struct Candidate { const Declaration* declaration; std::vector<Parameter> parameters; };

    size_t Call(const TypeRef* callee, bool member, size_t open, size_t end, bool constructor, int depth)
    {
        const size_t close = Matching(open, end, L'(', L')');
        static const std::wregex namedPattern(L"^\\s*@?([A-Za-z_]\\w*)\\s*:(?!:)");
        std::vector<Argument> arguments;
        for (const auto& [s, e] : SplitTopLevel(open + 1, close)) {
            Argument argument{s, s, e, {}, false};
            std::wsmatch m;
            const std::wstring part = text_.substr(s, e - s);
            if (std::regex_search(part, m, namedPattern, std::regex_constants::match_continuous)) {
                argument.named = true;
                argument.name = m[1].str();
                argument.valueStart = s + (size_t)m.length(0);
            }
            arguments.push_back(std::move(argument));
        }
        if (arguments.size() == 1 && !arguments[0].named && Trim(text_.substr(arguments[0].start, arguments[0].end - arguments[0].start)).empty())
            arguments.clear();
        const auto candidates = callee ? Candidates(*callee, member, constructor, arguments) : std::vector<Candidate>();
        size_t position = 0;
        for (const auto& argument : arguments) {
            TypeRef type;
            bool hasType = false;
            const bool secret = Resolve(candidates, callee, argument, argument.named ? (size_t)-1 : position++, &type, &hasType);
            if (secret || (argument.named && IsSecretName(argument.name))) ranges_.emplace_back(argument.valueStart, argument.end);
            Walk(argument.valueStart, argument.end, hasType ? &type : nullptr, depth + 1);
        }
        return (std::min)(close + 1, end);
    }

    std::vector<Candidate> Candidates(const TypeRef& callee, bool member, bool constructor, const std::vector<Argument>& arguments) const
    {
        size_t positional = 0;
        std::vector<std::wstring> named;
        for (const auto& a : arguments) { if (a.named) named.push_back(a.name); else ++positional; }
        std::vector<Candidate> result;
        for (const auto& declaration : declarations_.Find(callee.name)) {
            if (constructor ? declaration.kind == L'M' : declaration.kind != L'M') continue;
            if (callee.hasArguments && declaration.kind != L'C' && declaration.typeParameters.size() != callee.arguments.size()) continue;
            Candidate candidate{&declaration, declaration.parameters};
            if (member && !candidate.parameters.empty() && candidate.parameters.front().isThis) candidate.parameters.erase(candidate.parameters.begin());
            const auto& ps = candidate.parameters;
            const bool hasParams = !ps.empty() && ps.back().isParams;
            size_t required = 0;
            for (const auto& p : ps) if (!p.hasDefault && !p.isParams) ++required;
            bool fits = (hasParams || positional + named.size() <= ps.size()) && positional + named.size() >= required;
            for (const auto& name : named) {
                bool found = false;
                for (const auto& p : ps) found = found || p.name == name;
                fits = fits && found;
            }
            if (fits) result.push_back(std::move(candidate));
        }
        return result;
    }

    static bool Resolve(const std::vector<Candidate>& candidates, const TypeRef* callee, const Argument& argument, size_t position,
                        TypeRef* type, bool* hasType)
    {
        *hasType = false;
        if (candidates.empty()) return false;
        bool secret = true, agreed = true, first = true;
        std::wstring typeText;
        for (const auto& candidate : candidates) {
            const Parameter* parameter = nullptr;
            const auto& ps = candidate.parameters;
            if (argument.named) {
                for (const auto& p : ps) if (p.name == argument.name) { parameter = &p; break; }
            } else if (position < ps.size() && !ps[position].isParams) {
                parameter = &ps[position];
            } else if (!ps.empty() && ps.back().isParams) {
                parameter = &ps.back();
            }
            if (!parameter) return false;
            secret = secret && IsSecretName(parameter->name);
            std::wstring substituted = parameter->type;
            std::wstring bare = substituted;
            while (!bare.empty() && bare.back() == L'?') bare.pop_back();
            const auto& tps = candidate.declaration->typeParameters;
            for (size_t k = 0; k < tps.size(); ++k)
                if (tps[k] == bare && callee && callee->hasArguments && k < callee->arguments.size()) substituted = callee->arguments[k];
            if (first) { typeText = substituted; first = false; }
            else if (typeText != substituted) agreed = false;
        }
        if (agreed && !typeText.empty()) *hasType = ParseType(typeText, type);
        return secret;
    }

    // `Login2Props props = new(...)`: the declared type a target-typed `new` creates.
    bool DeclaredType(size_t newAt, TypeRef* out) const
    {
        static const std::wregex pattern(L"([A-Za-z_][\\w.]*(?:<[^;{}()=]*>)?\\??)\\s+@?[A-Za-z_]\\w*\\s*=\\s*$");
        const size_t from = newAt > 256 ? newAt - 256 : 0;
        const std::wstring before = text_.substr(from, newAt - from);
        std::wsmatch m;
        if (!std::regex_search(before, m, pattern)) return false;
        const std::wstring type = m[1].str();
        if (type == L"var" || type == L"return" || type == L"await" || type == L"else") return false;
        return ParseType(type, out);
    }

    // The end of `<...>` at `open` when it is a type-argument list, or -1 for a comparison.
    long long GenericEnd(size_t open, size_t end) const
    {
        int depth = 0;
        for (size_t i = open; i < end; ++i) {
            const wchar_t c = text_[i];
            if (c == L'<') ++depth;
            else if (c == L'>') {
                if (--depth == 0) {
                    const size_t next = SkipSpace(text_, i + 1, end);
                    const wchar_t n = next < end ? text_[next] : 0;
                    return n == L'(' || n == L')' || n == L',' || n == L'{' || n == L'[' || n == L';' || n == L'.' ? (long long)i : -1;
                }
            } else if (!(IsIdentifierChar(c) || std::iswspace(c) || c == L',' || c == L'.' || c == L'?' || c == L'[' || c == L']')) {
                return -1;
            }
        }
        return -1;
    }

    Ranges SplitTopLevel(size_t start, size_t end) const
    {
        Ranges parts;
        int depth = 0;
        size_t from = start;
        for (size_t i = start; i < end; ++i) {
            const wchar_t c = text_[i];
            if (c == L'<' && i > start) {
                const long long before = SkipSpaceBack(text_, (long long)i - 1);
                if (before >= 0 && IsIdentifierChar(text_[(size_t)before])) {
                    const long long close = GenericEnd(i, end);
                    if (close > 0) { i = (size_t)close; continue; }
                }
            }
            if (c == L'(' || c == L'[' || c == L'{') ++depth;
            else if (c == L')' || c == L']' || c == L'}') --depth;
            else if (c == L',' && depth == 0) { parts.emplace_back(from, i); from = i + 1; }
        }
        parts.emplace_back(from, end);
        return parts;
    }

    // The bracket closing the one at `open`, or `end` when the text stops first (a statement cut short).
    size_t Matching(size_t open, size_t end, wchar_t opening, wchar_t closing) const
    {
        int depth = 0;
        for (size_t i = open; i < end; ++i) {
            if (text_[i] == opening) ++depth;
            else if (text_[i] == closing && --depth == 0) return i;
        }
        return end;
    }

    const std::wstring& text_;
    const Declarations& declarations_;
    Ranges& ranges_;
};

} // namespace detail

// The [start, end) ranges of masked C# whose string literals a password parameter receives.
inline std::vector<std::pair<size_t, size_t>> SecretRanges(const std::wstring& masked, const Declarations& declarations)
{
    detail::Ranges ranges;
    if (masked.find(L'"') == std::wstring::npos) return ranges;
    detail::Walker(masked, declarations, ranges).Walk(0, masked.size(), nullptr);
    return ranges;
}

// The lines of a file with the string literals a password parameter receives redacted, reading each call whole (its
// arguments may continue on later lines).
inline std::vector<std::wstring> RedactLines(const std::vector<std::wstring>& lines, const Declarations& declarations)
{
    std::vector<std::wstring> result = lines;
    std::wstring masked;
    const auto maskedLines = DevToolsCSharpSource::Mask(lines);
    for (size_t k = 0; k < maskedLines.size(); ++k) { if (k) masked += L'\n'; masked += maskedLines[k]; }
    const auto ranges = SecretRanges(masked, declarations);
    if (ranges.empty()) return result;
    size_t offset = 0;
    for (auto& line : result) {
        const size_t start = offset, end = offset + line.size();
        bool touched = false;
        for (const auto& [s, e] : ranges) touched = touched || (s < end && e > start);
        if (touched) {
            DevToolsCSharpSource::RedactLiterals(line, [&](size_t at) {
                for (const auto& [s, e] : ranges) if (start + at >= s && start + at < e) return true;
                return false;
            });
        }
        offset = end + 1;
    }
    return result;
}

} // namespace DevToolsCSharpArguments
