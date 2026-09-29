// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once
#include <string>

namespace DevToolsCommentText {

inline bool IsBlank(const std::wstring& text) { return text.find_first_not_of(L" \t\r\n") == std::wstring::npos; }

inline std::wstring NormalizeLineEndings(const std::wstring& text)
{
    std::wstring result;
    for (size_t i = 0; i < text.size(); ++i) {
        if (text[i] == L'\r') {
            result += L'\n';
            if (i + 1 < text.size() && text[i + 1] == L'\n') ++i;
        } else result += text[i];
    }
    return result;
}

inline bool Unchanged(const std::wstring& text, const std::wstring& saved)
{
    return NormalizeLineEndings(text) == NormalizeLineEndings(saved);
}

// CreateProcess argument quoting; CR/LF are valid characters inside a quoted argument, not wire framing.
inline std::wstring QuoteArgument(const std::wstring& text)
{
    std::wstring out = L"\"";
    for (size_t i = 0; i < text.size(); ++i) {
        size_t slashes = 0;
        while (i < text.size() && text[i] == L'\\') { ++slashes; ++i; }
        if (i == text.size()) { out.append(slashes * 2, L'\\'); break; }
        if (text[i] == L'"') { out.append(slashes * 2 + 1, L'\\'); out += L'"'; }
        else { out.append(slashes, L'\\'); out += text[i]; }
    }
    return out + L"\"";
}

inline std::wstring SourceLabel(const std::wstring& file, unsigned int line)
{
    if (file.empty()) return L"";
    return L"Historical: " + file + (line ? L":" + std::to_wstring(line) : L"");
}

// A comment on an element without authored source is stored, but an agent has to search for where it belongs.
inline std::wstring SavedStatus(bool host, bool linked)
{
    return std::wstring(host ? L"Saved on the host." : L"Saved.") + (linked ? L"" : L" Not linked to source.");
}

inline std::wstring AddArguments(unsigned long pid, const std::wstring& text, const std::wstring& id,
    const std::wstring& handle)
{
    const auto capture = handle.empty() ? L"--from-selection" : L"--from-element " + QuoteArgument(handle);
    auto args = L"devtools comments add " + capture + L" --app " + std::to_wstring(pid) + L" -t " + QuoteArgument(text);
    if (!id.empty()) args += L" --id " + QuoteArgument(id);
    return args;
}

inline std::wstring WriterCommand(const std::wstring& exe, const std::wstring& args,
    const std::wstring& sourceRoot, const std::wstring& guestToken)
{
    auto command = QuoteArgument(exe) + L" " + args;
    if (!guestToken.empty()) command += L" --guest-comments " + QuoteArgument(guestToken) + L" --json";
    if (!sourceRoot.empty()) command += L" --source-root " + QuoteArgument(sourceRoot);
    return command;
}

inline const wchar_t* WriterFailureStage(unsigned long code)
{
    switch (code) {
    case 2: return L"arguments";
    case 0x57410101: return L"context";
    case 0x57410102: return L"capture";
    case 0x57410103: return L"host-read";
    case 0x57410104: return L"host-write";
    case 0x57410105: return L"host-acknowledgement";
    default: return L"writer-exit";
    }
}

inline std::wstring WriterFailureStatus(unsigned long code, const wchar_t* stage = nullptr, bool host = true)
{
    return std::wstring(host ? L"Host save not confirmed (stage: " : L"Save not confirmed (stage: ") +
        std::wstring(stage ? stage : WriterFailureStage(code)) + L", code: " + std::to_wstring(code) +
        (host ? L"). Draft retained; reconnect or refresh before retrying." :
            L"). Draft retained; check the comment store and retry Save, or copy your text and Close to discard the draft. Closing leaves stored comments unchanged.");
}
} // namespace DevToolsCommentText
