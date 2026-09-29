// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "DevToolsBindingAnswer.h"

#include "DevToolsRead.h"
#include "DevToolsProtocol.h"

const wchar_t* DevToolsBindingInstall_ReplacementWarning()
{
    return L"Apply replaces the entire runtime binding with a new classic {Binding} using DataContext. "
           L"Only the entered path and mode are kept. Converter, ConverterParameter, ConverterLanguage, "
           L"UpdateSourceTrigger, FallbackValue, TargetNullValue, Source, ElementName and RelativeSource "
           L"are reset to defaults. This does not edit or recreate compiled x:Bind. "
           L"Your .xaml is unchanged. A prior managed capture can restore its saved classic binding; "
           L"this editor does not capture one for you.";
}

std::wstring DevToolsBindingInstall_Answer(const std::wstring& path, const std::wstring& mode,
                                      const DevToolsBindingReadBack& before, const DevToolsBindingReadBack& after,
                                      const std::wstring& failure)
{
    if (!failure.empty())
        return L"{\"state\":\"unavailable\",\"reason\":\"" + DevToolsJsonEscape(failure) + L"\"}";

    std::wstring j = L"{\"state\":\"bound\"";
    j += L",\"path\":\"" + DevToolsJsonEscape(path) + L"\"";
    j += L",\"mode\":\"" + DevToolsJsonEscape(mode) + L"\"";
    j += L",\"before\":\"" + DevToolsJsonEscape(before.value) + L"\"";
    j += L",\"after\":\"" + DevToolsJsonEscape(after.value) + L"\"";
    j += L",\"bound\":\"" + std::wstring(after.isBinding ? L"True" : L"False") + L"\"";
    j += L"}";
    return j;
}
