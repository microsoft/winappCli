// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once
#include <string>

// What a property text editor writes back. A TextBox does not hold every string exactly: it stores line breaks
// as '\r', and a single-line box keeps only the first line. So "unchanged" means the editor still shows what it
// showed when it opened, not that it equals the property value.
namespace DevToolsEditText {

inline bool IsMultiline(const std::wstring& value) { return value.find_first_of(L"\r\n") != std::wstring::npos; }

// Enter applies a property editor, as it saves a comment; only Shift+Enter in a multi-line editor starts a new line.
inline bool StartsNewLine(bool multiline, bool shift, bool ctrl) { return multiline && shift && !ctrl; }

// The editor's line breaks, written in the original value's convention ("\n" when it has none).
inline std::wstring WithLineEndingsOf(const std::wstring& edited, const std::wstring& original)
{
    const wchar_t* eol = original.find(L"\r\n") != std::wstring::npos ? L"\r\n"
        : (original.find(L'\r') != std::wstring::npos && original.find(L'\n') == std::wstring::npos) ? L"\r" : L"\n";
    std::wstring out;
    for (size_t i = 0; i < edited.size(); ++i) {
        if (edited[i] == L'\r' || edited[i] == L'\n') {
            out += eol;
            if (edited[i] == L'\r' && i + 1 < edited.size() && edited[i + 1] == L'\n') ++i;
        } else out += edited[i];
    }
    return out;
}

// The value to write, or false when there is nothing to write. `shown` is what the editor held when it opened;
// null when that could not be read, in which case only the value itself is compared.
inline bool ValueToWrite(const std::wstring& current, const std::wstring* shown, const std::wstring& original,
                         std::wstring* out)
{
    if (shown && current == *shown) return false;
    *out = WithLineEndingsOf(current, original);
    return *out != original;
}

} // namespace DevToolsEditText
