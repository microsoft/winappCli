// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

#include <string>

struct DevToolsBindingReadBack
{
    bool         found     = false;
    bool         isBinding = false;
    std::wstring value;
};

const wchar_t* DevToolsBindingInstall_ReplacementWarning();

std::wstring DevToolsBindingInstall_Answer(const std::wstring& path, const std::wstring& mode,
                                      const DevToolsBindingReadBack& before, const DevToolsBindingReadBack& after,
                                      const std::wstring& failure);
