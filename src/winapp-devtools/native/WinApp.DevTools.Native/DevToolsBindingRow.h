// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

#include <string>

struct DevToolsBindingRowView {
    bool show = false;

    bool isFault = false;

    std::wstring head;
    std::wstring headEmphasis;
    std::wstring headTail;

    std::wstring subtitle;

    bool unavailable = false;
    bool suppressNativeWalk = false;
};

DevToolsBindingRowView DevToolsBindingRow_FromJson(const std::wstring& json);

const wchar_t* DevToolsBindingRow_PendingText();
