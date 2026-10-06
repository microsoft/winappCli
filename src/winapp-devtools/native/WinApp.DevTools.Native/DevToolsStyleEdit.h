// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

// The inspector's "Edit Style" actions (Blend's Edit Current / Edit a Copy) run
// `winapp devtools resources copy-style` and turn its JSON answer into what the pane says and which file it offers
// to open. Pure so the wording and argument shaping are tested without a window or a CLI.

#include "DevToolsCommentText.h"
#include "DevToolsProtocol.h"

#include <string>
#include <vector>

namespace DevToolsStyleEdit {

enum class Action {
    Locate, // Edit current: find the Style the element uses; writes nothing.
    Copy,   // Edit a copy: copy that Style into the project and point the element (or every element of its type) at it.
};

struct Request {
    unsigned long long wire = 0;
    unsigned long      pid = 0;
    Action             action = Action::Locate;
    bool               allOfType = false;
    std::wstring       key;  // empty: the CLI picks <Type>Style1, <Type>Style2, ...
    std::wstring       into; // empty: App.xaml
};

struct Outcome {
    bool         ok = false;
    std::wstring message;      // one short sentence for the dialog or the pane

    // The Style the element uses now.
    bool         sourceInApp = false;
    std::wstring sourceName;   // "TextBoxStyle1", "your implicit TextBox style", "WinUI's default TextBox style"
    std::wstring sourceKey;    // empty for an implicit or default Style
    std::wstring targetType;
    std::wstring sourceFile;   // as winapp shows it, e.g. "App.xaml"
    std::wstring sourcePath;   // absolute; empty when the file isn't on disk
    unsigned int sourceLine = 0;

    // Edit a copy: the key winapp proposes (Locate) or used (Copy), and where the copy went.
    std::wstring key;
    bool         implicit = false;
    std::wstring copyFile;
    std::wstring copyPath;
    unsigned int copyLine = 0;
    std::vector<std::wstring> notes;
};

inline std::wstring Trim(const std::wstring& s)
{
    const auto first = s.find_first_not_of(L" \t\r\n");
    if (first == std::wstring::npos) return L"";
    return s.substr(first, s.find_last_not_of(L" \t\r\n") - first + 1);
}

// A key the pane will pass on: letters, digits, '_', '-' and '.', starting with a letter or '_'. Anything else would
// need escaping in XAML and is almost always a typo.
inline bool ValidKey(const std::wstring& key)
{
    if (key.empty()) return false;
    const auto isLetter = [](wchar_t c) { return (c >= L'A' && c <= L'Z') || (c >= L'a' && c <= L'z') || c == L'_'; };
    if (!isLetter(key[0])) return false;
    for (const wchar_t c : key) {
        if (!isLetter(c) && !(c >= L'0' && c <= L'9') && c != L'-' && c != L'.') return false;
    }
    return true;
}

inline std::wstring Arguments(const Request& r)
{
    std::wstring a = L"devtools resources copy-style " + std::to_wstring(r.wire) +
                     L" --app " + std::to_wstring(r.pid) + L" --json";
    if (r.action == Action::Copy) {
        a += L" --write";
        if (r.allOfType) a += L" --all-of-type";
        else if (!r.key.empty()) a += L" --key " + DevToolsCommentText::QuoteArgument(r.key);
        if (!r.into.empty()) a += L" --into " + DevToolsCommentText::QuoteArgument(r.into);
    }
    return a;
}

// The runner's own failures (it never reached the CLI's answer) as the pane says them.
inline std::wstring RunnerFailure(int code)
{
    switch (code) {
    case -1: return L"winapp isn't connected to this app anymore. Reconnect DevTools and try again.";
    case -2: return L"Could not start winapp.";
    case -3: return L"winapp did not answer in time.";
    case -4: return L"This DevTools connection is read-only, so it can't change your XAML.";
    case -5: return L"Editing styles isn't available in guest mode.";
    default: return L"winapp failed (" + std::to_wstring(code) + L").";
    }
}

inline std::wstring FileName(const std::wstring& path)
{
    const auto slash = path.find_last_of(L"\\/");
    return slash == std::wstring::npos ? path : path.substr(slash + 1);
}

inline unsigned int Line(long long line) { return line > 0 ? static_cast<unsigned int>(line) : 0; }

// `exitCode` < 0 is a runner failure; otherwise `output` is the CLI's stdout (JSON, possibly with stray text around it).
inline Outcome Interpret(const Request& r, int exitCode, const std::wstring& output)
{
    Outcome o;
    if (exitCode < 0) { o.message = RunnerFailure(exitCode); return o; }

    const auto open = output.find(L'{');
    const auto close = output.rfind(L'}');
    DevToolsJson j;
    if (open == std::wstring::npos || close == std::wstring::npos || close < open ||
        !DevToolsJsonParse(output.substr(open, close - open + 1), j) || !j.IsObject()) {
        o.message = L"winapp did not answer (exit code " + std::to_wstring(exitCode) + L").";
        return o;
    }
    if (!j.GetBool(L"ok", false)) {
        const DevToolsJson* error = j.Find(L"error");
        o.message = error ? error->GetString(L"message") : std::wstring();
        if (o.message.empty()) o.message = L"winapp could not find this element's Style (exit code " + std::to_wstring(exitCode) + L").";
        return o;
    }

    const DevToolsJson* source = j.Find(L"source");
    if (!source) { o.message = L"winapp's answer did not say which Style this element uses."; return o; }
    o.sourceInApp = source->GetString(L"definedIn") == L"app";
    o.sourceKey = source->GetString(L"key");
    o.targetType = j.GetString(L"targetType", source->GetString(L"targetType"));
    o.sourceFile = source->GetString(L"file");
    o.sourcePath = source->GetString(L"path");
    o.sourceLine = Line(source->GetInt(L"line", 0));
    const std::wstring sourceType = source->GetString(L"targetType");
    o.sourceName = o.sourceInApp
        ? (o.sourceKey.empty() ? L"your implicit " + sourceType + L" style" : o.sourceKey)
        : (source->GetBool(L"defaultStyle", false) || o.sourceKey.empty() ? L"WinUI's default " + sourceType + L" style"
                                                                          : L"WinUI's " + o.sourceKey);
    o.key = j.GetString(L"key");
    o.implicit = j.GetBool(L"implicit", false);
    if (const DevToolsJson* notes = j.Find(L"notes")) {
        for (const auto& n : notes->arr) if (!n.str.empty()) o.notes.push_back(n.str);
    }
    o.ok = true;

    if (r.action == Action::Locate) {
        o.message = o.sourceInApp
            ? L"Uses " + o.sourceName + L" from " + (o.sourceFile.empty() ? std::wstring(L"your app") : FileName(o.sourceFile)) + L"."
            : L"Uses " + o.sourceName + L", which is built into WinUI. Edit a copy to change it.";
        return o;
    }

    if (!j.GetBool(L"written", false)) {
        o.ok = false;
        o.message = L"winapp planned the copy but did not write it.";
        return o;
    }
    if (const DevToolsJson* edits = j.Find(L"edits")) {
        for (const auto& e : edits->arr) {
            if (e.GetString(L"kind") != L"addStyle") continue;
            o.copyFile = e.GetString(L"file");
            o.copyPath = e.GetString(L"path");
            o.copyLine = Line(e.GetInt(L"line", 0));
            break;
        }
    }
    const std::wstring where = o.copyFile.empty() ? std::wstring() : L" in " + FileName(o.copyFile);
    o.message = o.key.empty() || o.implicit
        ? L"Created an implicit " + o.targetType + L" style" + where + L"."
        : L"Created " + o.key + where + L".";
    return o;
}
} // namespace DevToolsStyleEdit
