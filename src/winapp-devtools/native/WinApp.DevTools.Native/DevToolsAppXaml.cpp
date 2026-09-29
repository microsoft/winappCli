// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "DevToolsAppXaml.h"

#include <windows.h>
#include <atomic>
#include <mutex>
#include <set>
#include <string>
#include <vector>

namespace {

constexpr int    kMaxScanDepth = 6;
constexpr size_t kMaxScanEntries = 40000;

std::once_flag        g_scanOnce;
std::set<std::wstring> g_appXaml;   // lowercased leaf filenames, normalized to ".xaml"

std::atomic<bool> g_scanDiscredited{ false };

std::wstring ToLower(std::wstring s)
{
    for (auto& c : s) c = (wchar_t)towlower(c);
    return s;
}

bool IsFrameworkDir(const std::wstring& lowerName)
{
    return lowerName.rfind(L"microsoft.ui.xaml", 0) == 0
        || lowerName.rfind(L"microsoft.windowsappsdk", 0) == 0
        || lowerName.rfind(L"microsoft.winui", 0) == 0
        || lowerName.rfind(L"winui", 0) == 0;
}

std::wstring PathKey(const std::wstring& uriOrPath)
{
    std::wstring s = uriOrPath;
    size_t cut = s.find_first_of(L"?#");
    if (cut != std::wstring::npos) s.erase(cut);

    // Strip a URI scheme ("ms-appx:///Pages/Foo.xaml" -> "Pages/Foo.xaml").
    size_t scheme = s.find(L"://");
    if (scheme != std::wstring::npos) s.erase(0, scheme + 3);

    for (auto& c : s) if (c == L'\\') c = L'/';
    while (!s.empty() && s.front() == L'/') s.erase(0, 1);

    s = ToLower(s);
    if (s.size() > 4 && s.compare(s.size() - 4, 4, L".xbf") == 0)
        s.replace(s.size() - 4, 4, L".xaml");
    return s;
}

// Cap the in-process app-directory scan and skip reparse points so source classification cannot hang the app.
void ScanDir(const std::wstring& dir, int depth, size_t& budget, size_t rootLen)
{
    if (depth > kMaxScanDepth || budget == 0) return;
    WIN32_FIND_DATAW fd{};
    HANDLE h = FindFirstFileExW((dir + L"\\*").c_str(), FindExInfoBasic, &fd,
                                FindExSearchNameMatch, nullptr, 0);
    if (h == INVALID_HANDLE_VALUE) return;
    std::vector<std::wstring> subdirs;
    do {
        if (budget == 0) break;
        --budget;
        const std::wstring name = fd.cFileName;
        if (name == L"." || name == L"..") continue;
        // Never follow reparse points while running inside the target app.
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) continue; // never follow links
            if (IsFrameworkDir(ToLower(name))) continue;
            subdirs.push_back(dir + L"\\" + name);
            continue;
        }
        const std::wstring lower = ToLower(name);
        const bool isXaml = lower.size() > 5 && lower.compare(lower.size() - 5, 5, L".xaml") == 0;
        const bool isXbf  = lower.size() > 4 && lower.compare(lower.size() - 4, 4, L".xbf") == 0;
        if (isXaml || isXbf) {
            // Record the path RELATIVE to the app root, which is the space source URIs are expressed in.
            const std::wstring full = dir + L"\\" + name;
            g_appXaml.insert(PathKey(full.substr(rootLen)));
        }
    } while (FindNextFileW(h, &fd));
    FindClose(h);
    for (const auto& sd : subdirs) ScanDir(sd, depth + 1, budget, rootLen);
}

void EnsureScanned()
{
    std::call_once(g_scanOnce, [] {
        wchar_t exe[MAX_PATH * 4] = {};
        DWORD n = GetModuleFileNameW(nullptr, exe, (DWORD)(sizeof(exe) / sizeof(exe[0])));
        if (n == 0 || n >= (sizeof(exe) / sizeof(exe[0]))) return;
        std::wstring path = exe;
        size_t slash = path.find_last_of(L"\\/");
        if (slash == std::wstring::npos) return;
        path.erase(slash);
        size_t budget = kMaxScanEntries;
        ScanDir(path, 0, budget, path.size() + 1); // +1 skips the separator so keys are app-relative
    });
}

bool LegacyDenylistSaysApp(const std::wstring& lowerUri)
{
    if (lowerUri.find(L"microsoft.ui.xaml") != std::wstring::npos) return false;
    if (lowerUri.find(L"windows.ui.xaml")   != std::wstring::npos) return false;
    if (lowerUri.find(L"themeresources")    != std::wstring::npos) return false;
    if (lowerUri.find(L"generic.xaml")      != std::wstring::npos) return false;
    return true;
}

} // namespace

bool DevToolsAppXaml_IsAppAuthored(const std::wstring& uri)
{
    if (uri.empty()) return false;
    const std::wstring lower = ToLower(uri);
    if (!LegacyDenylistSaysApp(lower)) return false;
    EnsureScanned();
    // If the package layout cannot be scanned, fall back to the conservative denylist behavior.
    if (g_appXaml.empty()) return true; // scan unusable -> legacy behaviour (see comment above)
    if (g_scanDiscredited.load()) return true;
    return g_appXaml.count(PathKey(lower)) != 0;
}

void DevToolsAppXaml_ClassifyBatch(const std::vector<std::wstring>& uris, std::vector<char>& out)
{
    out.assign(uris.size(), 0);
    EnsureScanned();

    std::vector<char> notFramework(uris.size(), 0);
    bool anyCandidate = false;
    bool anyAllowlistHit = false;
    for (size_t i = 0; i < uris.size(); ++i) {
        if (uris[i].empty()) continue;
        const std::wstring lower = ToLower(uris[i]);
        if (!LegacyDenylistSaysApp(lower)) continue;   // framework by name -> stays 0
        notFramework[i] = 1;
        anyCandidate = true;
        if (!g_appXaml.empty() && g_appXaml.count(PathKey(lower)) != 0) anyAllowlistHit = true;
    }

    const bool usable = !g_appXaml.empty() && !(anyCandidate && !anyAllowlistHit);
    if (anyCandidate && !anyAllowlistHit) g_scanDiscredited.store(true);
    else if (anyAllowlistHit) g_scanDiscredited.store(false);

    for (size_t i = 0; i < uris.size(); ++i) {
        if (!notFramework[i]) continue;
        out[i] = usable ? (g_appXaml.count(PathKey(uris[i])) != 0 ? 1 : 0) : 1;
    }
}

bool DevToolsAppXaml_HasAppXamlSet()
{
    EnsureScanned();
    return !g_appXaml.empty();
}

bool DevToolsAppXaml_ScanDiscredited()
{
    return g_scanDiscredited.load();
}

