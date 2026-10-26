// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

#include "DevToolsProjected.h"
#include <string>

inline int DevToolsBindingInstall_ModeOrdinal(const std::wstring& mode)
{
    using Mode = DevToolsXD::BindingMode;
    if (mode == L"OneTime") return static_cast<int>(Mode::OneTime);
    if (mode == L"TwoWay") return static_cast<int>(Mode::TwoWay);
    if (mode.empty() || mode == L"OneWay") return static_cast<int>(Mode::OneWay);
    return -1;
}
