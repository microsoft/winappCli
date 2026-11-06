// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

// The comment-marker anchor of a Microsoft.UI.Reactor element: the C# call site that created it (project-relative
// file, line, column), its element kind, key and rendering component, which of the elements that call site created
// under the same parent it is, and -- for an element with no call site of its own -- the path from the nearest Reactor
// element down to it. Opaque to the CLI, which stores it and hands it back to place a marker.
//
// Resolution prefers the exact call site; when the app was rebuilt after the call site moved, a unique element with the
// same file, kind, key, component and ordinal still resolves (the line is the only thing that changed). The C# variable
// the element was assigned to, when it has one, must match too: it survives a line move and tells apart two moved call
// sites of the same kind.

#include <algorithm>
#include <cwchar>
#include <cwctype>
#include <string>
#include <utility>
#include <vector>

namespace DevToolsReactorAnchor {

inline constexpr wchar_t kPrefix[] = L"reactor:";

struct Site
{
    std::wstring file;          // project-relative, or the leaf when the file is outside the project
    unsigned int line = 0;
    unsigned int column = 0;
    std::wstring element;
    std::wstring key;
    std::wstring owner;
    unsigned int ordinal = 0;
    std::vector<std::pair<unsigned int, std::wstring>> sub; // (child index, short type) from the call-site element down
    std::wstring name;          // the C# variable the call-site element was assigned to, if any
};

inline bool IsReactorAnchor(const std::wstring& anchor) { return anchor.rfind(kPrefix, 0) == 0; }

inline void AppendField(std::wstring& out, const std::wstring& value)
{
    out += std::to_wstring(value.size());
    out += L':';
    out += value;
}

inline std::wstring Encode(const Site& site)
{
    std::wstring out = kPrefix;
    AppendField(out, site.file);
    AppendField(out, std::to_wstring(site.line));
    AppendField(out, std::to_wstring(site.column));
    AppendField(out, site.element);
    AppendField(out, site.key);
    AppendField(out, site.owner);
    AppendField(out, std::to_wstring(site.ordinal));
    AppendField(out, std::to_wstring(site.sub.size()));
    for (const auto& step : site.sub) {
        AppendField(out, std::to_wstring(step.first));
        AppendField(out, step.second);
    }
    AppendField(out, site.name);
    return out;
}

inline bool ReadField(const std::wstring& text, size_t& at, std::wstring* out)
{
    size_t length = 0;
    size_t digits = 0;
    while (at < text.size() && text[at] >= L'0' && text[at] <= L'9' && digits < 7) {
        length = length * 10 + (size_t)(text[at] - L'0');
        ++at; ++digits;
    }
    if (!digits || at >= text.size() || text[at] != L':' || text.size() - at - 1 < length) return false;
    ++at;
    *out = text.substr(at, length);
    at += length;
    return true;
}

inline bool ReadNumber(const std::wstring& text, size_t& at, unsigned int* out)
{
    std::wstring field;
    if (!ReadField(text, at, &field) || field.empty() || field.size() > 9) return false;
    unsigned int value = 0;
    for (wchar_t c : field) {
        if (c < L'0' || c > L'9') return false;
        value = value * 10 + (unsigned int)(c - L'0');
    }
    *out = value;
    return true;
}

inline bool Decode(const std::wstring& anchor, Site* site)
{
    if (!site || !IsReactorAnchor(anchor)) return false;
    Site out;
    size_t at = wcslen(kPrefix);
    unsigned int steps = 0;
    if (!ReadField(anchor, at, &out.file) || !ReadNumber(anchor, at, &out.line) || !ReadNumber(anchor, at, &out.column) ||
        !ReadField(anchor, at, &out.element) || !ReadField(anchor, at, &out.key) || !ReadField(anchor, at, &out.owner) ||
        !ReadNumber(anchor, at, &out.ordinal) || !ReadNumber(anchor, at, &steps) || steps > 64 || out.file.empty())
        return false;
    for (unsigned int i = 0; i < steps; ++i) {
        std::pair<unsigned int, std::wstring> step;
        if (!ReadNumber(anchor, at, &step.first) || !ReadField(anchor, at, &step.second)) return false;
        out.sub.push_back(std::move(step));
    }
    if (!ReadField(anchor, at, &out.name) || at != anchor.size()) return false;
    *site = std::move(out);
    return true;
}

inline bool SameText(const std::wstring& a, const std::wstring& b)
{
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i)
        if (towlower(a[i]) != towlower(b[i])) return false;
    return true;
}

// Whether `candidate` (an element's own call site, without sub path) is the element `wanted` names. `positioned`
// also requires the same line and column.
inline bool Matches(const Site& candidate, const Site& wanted, bool positioned)
{
    return SameText(candidate.file, wanted.file) && candidate.element == wanted.element && candidate.key == wanted.key &&
           candidate.owner == wanted.owner && candidate.ordinal == wanted.ordinal &&
           (!positioned || (candidate.line == wanted.line && candidate.column == wanted.column));
}

// The candidates an anchor names: those at the exact call site, else those that differ only by position, provided they
// all sit at one position (one call site shown in several windows). Empty when it names none, or two moved call sites.
inline std::vector<size_t> Matching(const std::vector<Site>& candidates, const Site& wanted)
{
    std::vector<size_t> exact, loose;
    for (size_t i = 0; i < candidates.size(); ++i) {
        if (Matches(candidates[i], wanted, true)) exact.push_back(i);
        else if (Matches(candidates[i], wanted, false) && (wanted.name.empty() || candidates[i].name == wanted.name))
            loose.push_back(i);
    }
    if (!exact.empty()) return exact;
    for (size_t i : loose) {
        if (candidates[i].line != candidates[loose.front()].line || candidates[i].column != candidates[loose.front()].column)
            return {};
    }
    return loose;
}

// The first candidate Matching names, or -1.
inline int Pick(const std::vector<Site>& candidates, const Site& wanted)
{
    const auto matching = Matching(candidates, wanted);
    return matching.empty() ? -1 : (int)matching.front();
}

// `path` made relative to `root` (both absolute, either separator), or its leaf when it is not under `root`.
inline std::wstring ProjectRelative(const std::wstring& path, const std::wstring& root)
{
    auto normal = [](std::wstring value) {
        for (auto& c : value) if (c == L'/') c = L'\\';
        while (!value.empty() && value.back() == L'\\') value.pop_back();
        return value;
    };
    const std::wstring p = normal(path), r = normal(root);
    if (!r.empty() && p.size() > r.size() + 1 && p[r.size()] == L'\\' && SameText(p.substr(0, r.size()), r))
        return p.substr(r.size() + 1);
    const size_t slash = p.find_last_of(L'\\');
    return slash == std::wstring::npos ? p : p.substr(slash + 1);
}

// Index of `target` among `peers` ordered by their document position (child-index paths from a common ancestor).
template <typename Handle>
unsigned int Ordinal(Handle target, std::vector<std::pair<Handle, std::vector<size_t>>> peers)
{
    std::sort(peers.begin(), peers.end(), [](const auto& a, const auto& b) { return a.second < b.second; });
    for (size_t i = 0; i < peers.size(); ++i)
        if (peers[i].first == target) return (unsigned int)i;
    return 0;
}

} // namespace DevToolsReactorAnchor
