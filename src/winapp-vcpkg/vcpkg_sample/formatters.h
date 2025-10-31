// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

#include <format>

template <typename CharT>
struct std::formatter<winrt::hstring, CharT>
{
    template <class ParseContext>
    constexpr auto parse(ParseContext& ctx) const
    {
        auto it = ctx.begin();
        if (it != ctx.end() && *it != '}')
        {
            throw std::runtime_error("Invalid format specifier");
        }

        return it;
    }

    template <class Context>
    auto format(winrt::hstring const& str, Context& ctx) const
    {
        auto utf8 = winrt::to_string(str);
        return std::copy(utf8.begin(), utf8.end(), ctx.out());
    }
};
