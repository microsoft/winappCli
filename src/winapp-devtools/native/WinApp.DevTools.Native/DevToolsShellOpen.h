// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once


#include <functional>
#include <string>

namespace DevToolsShellOpen {

enum class Outcome {
    NoPath,        // the source URI did not resolve to a file under the source root
    NotFound,      // it resolved, but nothing is there on this machine
    NoHandler,     // the file exists and.xaml has no registered handler -- the picker was NOT shown
    LaunchFailed,  // a handler exists and the shell refused the verb anyway
    Handed,        // the shell accepted the verb for the named handler
};

struct Result {
    Outcome      outcome = Outcome::NoPath;
    std::wstring handler;          // the handler's display/executable name, when one was found
    bool         lineHonoured = false;  // did the launcher that ran actually take the line number?
};

// Returns the registered handler for `extension` (".xaml"), or an empty string when there is none.
using AssocLookup = std::function<std::wstring(const std::wstring& extension)>;
using Launcher = std::function<int(const std::wstring& path)>;
using Exists = std::function<bool(const std::wstring& path)>;

inline Result Decide(const std::wstring& resolvedPath,
                     const std::wstring& extension,
                     const AssocLookup& assoc,
                     const Exists& exists,
                     const Launcher& launch)
{
    Result r;
    if (resolvedPath.empty()) { r.outcome = Outcome::NoPath;   return r; }
    if (!exists(resolvedPath)) { r.outcome = Outcome::NotFound; return r; }

    r.handler = assoc ? assoc(extension) : std::wstring();
    if (r.handler.empty()) { r.outcome = Outcome::NoHandler; return r; }

    const int rc = launch ? launch(resolvedPath) : 0;
    r.outcome      = (rc > 32) ? Outcome::Handed : Outcome::LaunchFailed;
    r.lineHonoured = false;   // the `open` verb takes no line argument. See rule 3.
    return r;
}

inline std::wstring Describe(const Result& r,
                             const std::wstring& display,
                             unsigned int line,
                             const std::wstring& resolvedPath,
                             const std::wstring& offScreenPrefix)
{
    const std::wstring lead = offScreenPrefix.empty() ? std::wstring() : offScreenPrefix + L" \u2014 ";
    switch (r.outcome) {
    case Outcome::NoPath:
        return display.empty()
            ? lead + L"there is no source file to open."
            : lead + L"source path unavailable for " + display + L" (build from a project?)";
    case Outcome::NotFound:
        return lead + L"source file not found: " + resolvedPath;
    case Outcome::NoHandler:
        return lead + L"nothing on this machine opens .xaml files. Use Reveal in File Explorer or Copy path.";
    case Outcome::LaunchFailed:
        return lead + L"couldn't hand " + display + L" to " + r.handler + L".";
    case Outcome::Handed:
    default:
        break;
    }
    std::wstring m = lead + L"handed " + display;
    if (r.lineHonoured && line > 0) m += L" : " + std::to_wstring(line);
    m += L" to " + r.handler + L".";
    return m;
}

} // namespace DevToolsShellOpen
