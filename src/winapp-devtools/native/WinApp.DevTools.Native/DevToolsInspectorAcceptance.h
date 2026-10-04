// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

#include <windows.h>
#include <algorithm>
#include <cmath>
#include <cwchar>
#include <cwctype>
#include <string>

namespace DevToolsInspector
{
    inline double TreeWidth(double requested, double available)
    {
        const double maximum = (std::max)(0.0, available - 6.0 - 300.0);
        return (std::clamp)(requested, (std::min)(220.0, maximum), maximum);
    }

    inline double PopupOffset(double currentOffset, double measuredOrigin)
    {
        return currentOffset - measuredOrigin;
    }

    inline bool CanRefreshSnapshot(bool focusedInput, bool focusedPropertyPane, bool pendingEdit)
    {
        return !focusedInput && !focusedPropertyPane && !pendingEdit;
    }

    inline bool NegativeDimension(const std::wstring& property, const std::wstring& value)
    {
        if (property != L"Width" && property != L"Height" &&
            property != L"MinWidth" && property != L"MinHeight" &&
            property != L"MaxWidth" && property != L"MaxHeight") return false;
        wchar_t* end = nullptr;
        const double number = std::wcstod(value.c_str(), &end);
        if (end == value.c_str()) return false;
        while (*end && std::iswspace(*end)) ++end;
        return !*end && number < 0.0;
    }

    inline std::wstring InvalidValueMessage(const std::wstring& property, const std::wstring& type,
                                            const std::wstring& value, const std::wstring& reason)
    {
        return L"Cannot set property '" + property + L"' to '" + value + L"' (type " +
            (type.empty() ? L"unspecified" : type) + L"): " + reason;
    }

    template<typename Convert, typename Set>
    HRESULT WriteLiteral(const std::wstring& property, const std::wstring& type, const std::wstring& value,
                         Convert convert, Set set, std::wstring* error)
    {
        if (error) error->clear();
        auto fail = [&](HRESULT hr, const wchar_t* reason, bool invalid) {
            wchar_t hex[16]{};
            swprintf_s(hex, L"0x%08X", static_cast<unsigned int>(hr));
            if (error) *error = (invalid ? L"invalid-value|" : L"property-write-failed|") +
                InvalidValueMessage(property, type, value, std::wstring(reason) + L" (" + hex + L")");
            return hr;
        };
        const bool doubleType = type == L"Double" || type == L"System.Double" || type == L"Windows.Foundation.Double";
        if (doubleType && NegativeDimension(property, value))
            return fail(E_INVALIDARG, L"out of range; dimensions must be nonnegative (Width/Height also accept Auto)", true);
        const HRESULT converted = convert();
        if (FAILED(converted))
            return fail(converted, L"conversion to the requested XAML type failed", converted != E_OUTOFMEMORY);
        const HRESULT written = set();
        if (FAILED(written))
            return fail(written, L"the property setter rejected the value; check its allowed range", written == E_INVALIDARG);
        return written;
    }
}
