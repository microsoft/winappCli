// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

#include <string>

std::wstring DevToolsPathSyntax_Reason(const std::wstring& path);

bool DevToolsPathSyntax_Ok(const std::wstring& path);

// Splits a dotted binding path around one of its segments, so a view can emphasize the segment that failed:
// "Vm.Title.Length" around "Title" is "Vm.", "Title", ".Length". A segment the path doesn't contain is emphasized alone.
inline void DevToolsPath_SplitAroundSegment(const std::wstring& path, const std::wstring& segment,
                                            std::wstring* head, std::wstring* emph, std::wstring* tail)
{
    *head = *emph = *tail = L"";
    if (segment.empty()) { *emph = path; return; }

    for (size_t start = 0; start <= path.size();) {
        const size_t dot = path.find(L'.', start);
        const size_t len = (dot == std::wstring::npos ? path.size() : dot) - start;
        if (path.compare(start, len, segment) == 0) {
            *head = path.substr(0, start);
            *emph = segment;
            *tail = path.substr(start + segment.size());
            return;
        }
        if (dot == std::wstring::npos) break;
        start = dot + 1;
    }
    *emph = segment;
}
