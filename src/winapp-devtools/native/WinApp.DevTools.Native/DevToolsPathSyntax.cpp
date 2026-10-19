// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "DevToolsPathSyntax.h"

namespace {

bool IsStepStart(wchar_t c) { return (c >= L'A' && c <= L'Z') || (c >= L'a' && c <= L'z') || c == L'_'; }
bool IsStepRest(wchar_t c)  { return IsStepStart(c) || (c >= L'0' && c <= L'9'); }

std::wstring StepReason(const std::wstring& step)
{
    if (step.empty()) return L"Empty step between two dots.";

    size_t i = 0;
    if (!IsStepStart(step[0])) {
        if (step[0] >= L'0' && step[0] <= L'9')
            return L"A step cannot start with a digit.";
        return L"'" + step + L"' is not a property name.";
    }
    while (i < step.size() && IsStepRest(step[i])) ++i;
    if (i == step.size()) return std::wstring();

    // Whatever is left must be a run of well-formed [..] indexers and nothing else.
    while (i < step.size()) {
        if (step[i] != L'[') return L"'" + step + L"' is not a property name.";
        const size_t close = step.find(L']', i + 1);
        if (close == std::wstring::npos) return L"An indexer is missing its closing ].";
        if (close == i + 1) return L"An indexer needs something between [ and ].";
        if (step.find(L'[', i + 1) < close) return L"Nested [ inside an indexer.";
        i = close + 1;
    }
    return std::wstring();
}

} // namespace

std::wstring DevToolsPathSyntax_Reason(const std::wstring& path)
{
    if (path.empty()) return L"Empty - there is nothing to bind to.";

    for (wchar_t c : path)
        if (c == L' ' || c == L'\t' || c == L'\n' || c == L'\r')
            return L"A space is not part of a path.";

    if (path.front() == L'.') return L"Leading . - a path starts at the source object.";
    if (path.back()  == L'.') return L"Trailing . - the path stops mid-step.";

    size_t start = 0;
    for (;;) {
        const size_t dot = path.find(L'.', start);
        const std::wstring step = path.substr(start, dot == std::wstring::npos ? std::wstring::npos : dot - start);
        const std::wstring why = StepReason(step);
        if (!why.empty()) return why;
        if (dot == std::wstring::npos) break;
        start = dot + 1;
    }
    return std::wstring();
}

bool DevToolsPathSyntax_Ok(const std::wstring& path) { return DevToolsPathSyntax_Reason(path).empty(); }
