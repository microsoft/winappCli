// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

#include <string>
#include <vector>

struct IInspectable;
struct DevToolsReadProp;

struct DevToolsProbeSegment {
    std::wstring path;
    bool         reached = true;
    bool         found   = true;
    long         hr      = 0;
    bool         isNull  = false;
    std::wstring value;
    std::wstring type;
};

struct DevToolsProbeResult {
    std::wstring state;
    std::wstring against;
    std::wstring againstKind;
    std::wstring path;
    std::wstring reason;
    std::wstring expressionSource;
    std::wstring authoredState;
    std::vector<DevToolsProbeSegment> segments;
};

bool DevToolsPathProbe_Prepare(const DevToolsReadProp& prop, const std::wstring& authoredState,
                              const std::wstring& tryPath, DevToolsProbeResult* out);

bool DevToolsPathProbe_SplitPath(const std::wstring& path, std::vector<std::wstring>* out);

// UI thread only: the walk invokes live XAML/CLR property getters.
void DevToolsPathProbe_Walk(IInspectable* source, const std::wstring& path, DevToolsProbeResult* out);

std::wstring DevToolsPathProbe_ToJson(const DevToolsProbeResult& r);
