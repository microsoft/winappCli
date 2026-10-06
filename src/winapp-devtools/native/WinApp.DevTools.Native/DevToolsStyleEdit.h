// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

// The inspector's "Edit Style" actions (Blend's Edit Current / Edit a Copy) run
// `winapp devtools resources copy-style` and turn its JSON answer into what the pane says and which file it offers
// to open. Pure so the wording and argument shaping are tested without a window or a CLI.

#include "DevToolsCommentText.h"
#include "DevToolsProtocol.h"

#include <string>

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
    std::wstring message;
    std::wstring openPath;  // absolute file the pane offers to open; empty when there is none
    unsigned int openLine = 0;
    std::wstring openLabel; // the link text, e.g. "Open App.xaml"
    bool         editable = false; // openPath is the user's own XAML, so the pane opens it straight away
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
    case -1: return L"winapp did not register its path with this app. Reconnect with `winapp devtools` and try again.";
    case -2: return L"Could not start winapp.";
    case -3: return L"winapp did not answer in time.";
    case -4: return L"This DevTools connection is read-only, so it cannot change your source.";
    case -5: return L"Editing styles is not available in guest mode.";
    default: return L"winapp failed (" + std::to_wstring(code) + L").";
    }
}

inline std::wstring FileName(const std::wstring& path)
{
    const auto slash = path.find_last_of(L"\\/");
    return slash == std::wstring::npos ? path : path.substr(slash + 1);
}

inline std::wstring At(const std::wstring& file, long long line)
{
    return line > 0 ? file + L":" + std::to_wstring(line) : file;
}

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
        if (o.message.empty()) o.message = L"winapp could not copy this Style (exit code " + std::to_wstring(exitCode) + L").";
        return o;
    }

    const DevToolsJson* source = j.Find(L"source");
    if (!source) { o.message = L"winapp's answer did not say which Style this element uses."; return o; }
    const bool app = source->GetString(L"definedIn") == L"app";
    const std::wstring sourceKey = source->GetString(L"key");
    const std::wstring sourceType = source->GetString(L"targetType");
    const std::wstring sourceFile = source->GetString(L"file");
    const long long sourceLine = source->GetInt(L"line", 0);
    const std::wstring sourceName = app
        ? (sourceKey.empty() ? L"your implicit " + sourceType + L" Style" : L"your Style " + sourceKey)
        : (source->GetBool(L"defaultStyle", false) || sourceKey.empty()
               ? L"WinUI's default " + sourceType + L" Style"
               : L"WinUI's " + sourceKey);
    o.ok = true;

    if (r.action == Action::Locate) {
        o.openPath = source->GetString(L"path");
        o.openLine = sourceLine > 0 ? static_cast<unsigned int>(sourceLine) : 0;
        if (app) {
            o.message = L"Uses " + sourceName + L" at " + At(sourceFile, sourceLine) + L".";
            o.openLabel = L"Open " + FileName(sourceFile);
            o.editable = !o.openPath.empty();
        } else {
            const std::wstring package = source->GetString(L"package");
            o.message = L"Uses " + sourceName + L" (" + At(sourceFile, sourceLine) +
                        (package.empty() ? L"" : L" in " + package) +
                        L"). WinUI's styles can't be edited in place: choose Edit a copy to copy it into your app.";
            o.openLabel = L"View " + FileName(sourceFile);
        }
        if (o.openPath.empty()) o.openLabel.clear();
        return o;
    }

    if (!j.GetBool(L"written", false)) {
        o.ok = false;
        o.message = L"winapp planned the copy but did not write it.";
        return o;
    }
    const DevToolsJson* edits = j.Find(L"edits");
    const DevToolsJson* added = nullptr;
    const DevToolsJson* pointed = nullptr;
    if (edits) {
        for (const auto& e : edits->arr) {
            const std::wstring kind = e.GetString(L"kind");
            if (kind == L"addStyle" && !added) added = &e;
            else if (kind == L"setStyle" && !pointed) pointed = &e;
        }
    }
    const std::wstring type = j.GetString(L"targetType", sourceType);
    const std::wstring key = j.GetString(L"key");
    std::wstring m = L"Copied " + sourceName;
    if (added) m += L" into " + At(added->GetString(L"file"), added->GetInt(L"line", 0));
    m += key.empty() || j.GetBool(L"implicit", false)
        ? L" as the implicit Style for every " + type + L"."
        : L" as " + key + L".";
    if (pointed) m += L" Set this " + type + L"'s Style in " + At(pointed->GetString(L"file"), pointed->GetInt(L"line", 0)) + L".";
    if (const DevToolsJson* notes = j.Find(L"notes")) {
        for (const auto& n : notes->arr) if (!n.str.empty()) m += L" " + n.str;
    }
    m += L" Rebuild and restart the app to see the change.";
    o.message = m;
    if (added) {
        o.openPath = added->GetString(L"path");
        const long long line = added->GetInt(L"line", 0);
        o.openLine = line > 0 ? static_cast<unsigned int>(line) : 0;
        if (!o.openPath.empty()) o.openLabel = L"Open " + FileName(added->GetString(L"file"));
        o.editable = !o.openPath.empty();
    }
    return o;
}

} // namespace DevToolsStyleEdit
