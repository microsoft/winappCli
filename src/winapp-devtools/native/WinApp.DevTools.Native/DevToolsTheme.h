// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

namespace DevToolsTheme
{
constexpr int kDefault = 0;
constexpr int kLight = 1;
constexpr int kDark = 2;

inline int ToggleTarget(int actualTheme) noexcept
{
    return actualTheme == kDark ? kLight : kDark;
}
}
