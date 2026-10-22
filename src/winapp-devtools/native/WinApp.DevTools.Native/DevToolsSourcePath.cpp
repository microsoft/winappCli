// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "DevToolsSourcePath.h"

#include <windows.h>
#include <cwctype>
#include <vector>

namespace
{
bool IsSlash(wchar_t c)
{
    return c == L'\\' || c == L'/';
}

std::wstring TrimTrailingSlash(std::wstring path)
{
    while (path.size() > 1 && IsSlash(path.back())) {
        if (path.size() >= 2 && path[path.size() - 2] == L':') break;
        path.pop_back();
    }
    return path;
}

bool EqualPrefixInsensitive(const std::wstring& value, const std::wstring& prefix)
{
    if (value.size() < prefix.size()) return false;
    for (size_t i = 0; i < prefix.size(); ++i) {
        if (towlower(value[i]) != towlower(prefix[i])) return false;
    }
    return true;
}

bool FullPath(const std::wstring& input, std::wstring* output)
{
    if (!output || input.empty()) return false;
    DWORD required = GetFullPathNameW(input.c_str(), 0, nullptr, nullptr);
    if (required == 0) return false;
    std::vector<wchar_t> buffer(static_cast<size_t>(required) + 1);
    DWORD written = GetFullPathNameW(input.c_str(), static_cast<DWORD>(buffer.size()), buffer.data(), nullptr);
    if (written == 0 || written >= buffer.size()) return false;
    output->assign(buffer.data(), written);
    return true;
}

bool FinalPath(HANDLE handle, std::wstring* output)
{
    if (!output || handle == INVALID_HANDLE_VALUE) return false;
    constexpr DWORD flags = FILE_NAME_NORMALIZED | VOLUME_NAME_DOS;
    DWORD required = GetFinalPathNameByHandleW(handle, nullptr, 0, flags);
    if (required == 0) return false;
    std::vector<wchar_t> buffer(static_cast<size_t>(required) + 1);
    DWORD written = GetFinalPathNameByHandleW(handle, buffer.data(), static_cast<DWORD>(buffer.size()), flags);
    if (written == 0 || written >= buffer.size()) return false;
    output->assign(buffer.data(), written);
    return true;
}

std::wstring ShellPathFromFinal(const std::wstring& finalPath)
{
    constexpr wchar_t uncPrefix[] = L"\\\\?\\UNC\\";
    constexpr wchar_t localPrefix[] = L"\\\\?\\";
    if (finalPath.rfind(uncPrefix, 0) == 0) return L"\\\\" + finalPath.substr(_countof(uncPrefix) - 1);
    if (finalPath.rfind(localPrefix, 0) == 0) return finalPath.substr(_countof(localPrefix) - 1);
    return finalPath;
}

bool IsSafeRelativeXamlPath(const std::wstring& relative, const std::wstring& extension)
{
    if (relative.empty() || IsSlash(relative.front()) || relative.back() == L'/') return false;
    if (relative.find(L'\\') != std::wstring::npos) return false;

    size_t start = 0;
    for (;;) {
        size_t slash = relative.find(L'/', start);
        std::wstring segment = relative.substr(start, slash == std::wstring::npos
            ? std::wstring::npos
            : slash - start);
        if (segment.empty() || segment == L"." || segment == L"..") return false;
        for (wchar_t c : segment) {
            if (c < 0x20 || wcschr(L"<>:\"|?*", c) != nullptr) return false;
        }
        if (slash == std::wstring::npos) break;
        start = slash + 1;
    }

    if (relative.size() < extension.size()) return false;
    const size_t offset = relative.size() - extension.size();
    for (size_t i = 0; i < extension.size(); ++i) {
        if (towlower(relative[offset + i]) != extension[i]) return false;
    }
    return true;
}
}

bool DevToolsSourcePath_IsWithinRoot(const std::wstring& canonicalRoot,
                                const std::wstring& canonicalCandidate)
{
    const std::wstring root = TrimTrailingSlash(canonicalRoot);
    if (root.empty() || canonicalCandidate.size() <= root.size()) return false;
    return EqualPrefixInsensitive(canonicalCandidate, root)
        && IsSlash(canonicalCandidate[root.size()]);
}

std::wstring DevToolsSourcePath_AppUri(const wchar_t* runtimeUri)
{
    if (!runtimeUri) return {};
    std::wstring uri = runtimeUri;
    constexpr wchar_t prefix[] = L"ms-resource:///Files/";
    constexpr size_t length = _countof(prefix) - 1;
    if (uri.size() <= length || CompareStringOrdinal(uri.c_str(), (int)length, prefix, (int)length, TRUE) != CSTR_EQUAL ||
        uri.find(L';', length) != std::wstring::npos) {
        return uri;
    }
    return L"ms-appx:///" + uri.substr(length);
}

static bool ResolveExisting(const std::wstring& sourceRoot,
                                   const std::wstring& sourceUri,
                                   std::wstring* resolvedPath, const std::wstring& extension)
{
    if (resolvedPath) resolvedPath->clear();
    if (!resolvedPath || sourceRoot.empty()) return false;

    constexpr wchar_t prefix[] = L"ms-appx:///";
    if (sourceUri.rfind(prefix, 0) != 0) return false;
    const std::wstring relative = sourceUri.substr(_countof(prefix) - 1);
    if (!IsSafeRelativeXamlPath(relative, extension)) return false;

    std::wstring rootFull;
    if (!FullPath(sourceRoot, &rootFull)) return false;
    rootFull = TrimTrailingSlash(rootFull);

    std::wstring relativeWindows = relative;
    for (wchar_t& c : relativeWindows) if (c == L'/') c = L'\\';
    std::wstring candidateFull;
    if (!FullPath(rootFull + L"\\" + relativeWindows, &candidateFull)
        || !DevToolsSourcePath_IsWithinRoot(rootFull, candidateFull)) {
        return false;
    }

    DWORD attributes = GetFileAttributesW(candidateFull.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0) return false;

    HANDLE rootHandle = CreateFileW(rootFull.c_str(), FILE_READ_ATTRIBUTES,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS, nullptr);
    if (rootHandle == INVALID_HANDLE_VALUE) return false;

    HANDLE candidateHandle = CreateFileW(candidateFull.c_str(), FILE_READ_ATTRIBUTES,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
    if (candidateHandle == INVALID_HANDLE_VALUE) {
        CloseHandle(rootHandle);
        return false;
    }

    std::wstring rootFinal;
    std::wstring candidateFinal;
    const bool canonicalized = FinalPath(rootHandle, &rootFinal) && FinalPath(candidateHandle, &candidateFinal);
    CloseHandle(candidateHandle);
    CloseHandle(rootHandle);
    if (!canonicalized || !DevToolsSourcePath_IsWithinRoot(rootFinal, candidateFinal)) return false;

    *resolvedPath = ShellPathFromFinal(candidateFinal);
    return true;
}

bool DevToolsSourcePath_ResolveExisting(const std::wstring& root, const std::wstring& uri, std::wstring* path)
{
    return ResolveExisting(root, uri, path, L".xaml");
}

bool DevToolsSourcePath_ResolveCompiled(const std::wstring& root, const std::wstring& uri, std::wstring* path)
{
    return ResolveExisting(root, uri, path, L".xbf");
}

bool DevToolsSourcePath_ResolvePri(const std::wstring& root, std::wstring* path)
{
    return ResolveExisting(root, L"ms-appx:///resources.pri", path, L".pri");
}

namespace
{
bool IsFile(const std::wstring& path)
{
    const DWORD attributes = GetFileAttributesW(path.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0;
}

std::wstring ToWindowsSlashes(std::wstring path)
{
    for (wchar_t& c : path) if (c == L'/') c = L'\\';
    return path;
}

// "rel\File.cs" stays below its root: no rooted, drive or parent segments.
bool IsSafeRelative(const std::wstring& relative)
{
    if (relative.empty() || IsSlash(relative.front()) || relative.find(L':') != std::wstring::npos) return false;
    size_t start = 0;
    for (;;) {
        size_t slash = relative.find_first_of(L"\\/", start);
        const std::wstring segment = relative.substr(start, slash == std::wstring::npos ? std::wstring::npos : slash - start);
        if (segment == L"..") return false;
        if (slash == std::wstring::npos) return true;
        start = slash + 1;
    }
}

// `relative` under the source root, or under the nearest folder above it that has it (a repository root above the
// project). False when there is no source root or no such file.
bool UnderSourceRoot(const std::wstring& relative, const std::wstring& sourceRoot, std::wstring* mapped)
{
    std::wstring root;
    if (!IsSafeRelative(relative) || !FullPath(sourceRoot, &root)) return false;
    root = TrimTrailingSlash(root);
    for (;;) {
        const std::wstring candidate = root + (IsSlash(root.back()) ? L"" : L"\\") + relative;
        if (IsFile(candidate)) { *mapped = candidate; return true; }
        const size_t slash = root.find_last_of(L"\\/");
        if (slash == std::wstring::npos) break;
        std::wstring parent = root.substr(0, slash);
        if (parent.size() == 2 && parent[1] == L':') parent += L'\\';
        if (parent.size() < 3 || parent == root) break;
        root = parent;
    }
    return false;
}

bool HasParentSegment(const std::wstring& relative)
{
    size_t start = 0;
    for (;;) {
        const size_t slash = relative.find_first_of(L"\\/", start);
        if (relative.compare(start, slash == std::wstring::npos ? std::wstring::npos : slash - start, L"..") == 0) return true;
        if (slash == std::wstring::npos) return false;
        start = slash + 1;
    }
}

// The repository the source root is in (the nearest folder above it with `.git`), or the source root's parent when
// it is in none: how far a `../` path may reach.
std::wstring ParentBound(const std::wstring& sourceRoot)
{
    std::wstring root;
    if (!FullPath(sourceRoot, &root)) return {};
    root = TrimTrailingSlash(root);
    for (std::wstring folder = root;;) {
        if (GetFileAttributesW((folder + L"\\.git").c_str()) != INVALID_FILE_ATTRIBUTES) return folder;
        const size_t slash = folder.find_last_of(L"\\/");
        if (slash == std::wstring::npos || slash < 3) break;
        folder = folder.substr(0, slash);
    }
    const size_t slash = root.find_last_of(L"\\/");
    return slash == std::wstring::npos || slash < 3 ? root : root.substr(0, slash);
}

// Each existing file `relative` names under the source root or one of the projects compiled into the app.
std::vector<std::wstring> ProjectCandidates(const std::wstring& relative, const std::wstring& sourceRoot,
                                            const std::wstring& projectDirs)
{
    std::vector<std::wstring> found;
    if (relative.empty() || IsSlash(relative.front()) || relative.find(L':') != std::wstring::npos) return found;
    const bool climbs = HasParentSegment(relative);
    const std::wstring bound = climbs ? ParentBound(sourceRoot) : std::wstring();
    if (climbs && bound.empty()) return found;
    std::vector<std::wstring> folders{ sourceRoot };
    for (size_t start = 0; start < projectDirs.size();) {
        const size_t semicolon = projectDirs.find(L';', start);
        folders.push_back(projectDirs.substr(start, semicolon == std::wstring::npos ? std::wstring::npos : semicolon - start));
        start = semicolon == std::wstring::npos ? projectDirs.size() : semicolon + 1;
    }
    for (const auto& folder : folders) {
        std::wstring base;
        if (folder.empty() || !FullPath(folder, &base)) continue;
        std::wstring candidate;
        if (!FullPath(TrimTrailingSlash(base) + L"\\" + relative, &candidate) || !IsFile(candidate)) continue;
        if (climbs && !DevToolsSourcePath_IsWithinRoot(bound, candidate)) continue;
        bool known = false;
        for (const auto& existing : found) known = known || _wcsicmp(existing.c_str(), candidate.c_str()) == 0;
        if (!known) found.push_back(candidate);
    }
    return found;
}
}

bool DevToolsSourcePath_MapCompiled(const std::wstring& compiledPath, const std::wstring& sourceRoot,
                                    const std::wstring& pathMap, std::wstring* mapped,
                                    const std::wstring& projectDirs, std::vector<std::wstring>* ambiguous)
{
    if (ambiguous) ambiguous->clear();
    if (!mapped) return false;
    *mapped = compiledPath;
    if (compiledPath.empty()) return false;

    // MSBuild PathMap: `original=mapped` pairs; a recorded path starting with `mapped` came from `original`.
    size_t start = 0;
    while (start < pathMap.size()) {
        size_t comma = pathMap.find(L',', start);
        const std::wstring entry = pathMap.substr(start, comma == std::wstring::npos ? std::wstring::npos : comma - start);
        start = comma == std::wstring::npos ? pathMap.size() : comma + 1;
        const size_t equals = entry.find(L'=');
        if (equals == std::wstring::npos || equals == 0 || equals + 1 >= entry.size()) continue;
        const std::wstring from = entry.substr(0, equals);
        const std::wstring to = entry.substr(equals + 1);
        if (!EqualPrefixInsensitive(compiledPath, to)) continue;
        std::wstring rest = compiledPath.substr(to.size());
        while (!rest.empty() && IsSlash(rest.front())) rest.erase(0, 1);
        if (!IsSafeRelative(rest)) continue;
        const std::wstring candidate = TrimTrailingSlash(ToWindowsSlashes(from)) + L"\\" + ToWindowsSlashes(rest);
        if (IsFile(candidate)) { *mapped = candidate; return true; }
    }

    // Deterministic source roots: /_/ for the first, /_1/, /_2/ ... for the others.
    if (compiledPath.size() > 3 && compiledPath[0] == L'/' && compiledPath[1] == L'_') {
        size_t i = 2;
        while (i < compiledPath.size() && iswdigit(compiledPath[i])) ++i;
        if (i < compiledPath.size() && compiledPath[i] == L'/')
            return UnderSourceRoot(ToWindowsSlashes(compiledPath.substr(i + 1)), sourceRoot, mapped);
    }

    // A project-relative path (`Views/App.cs`), as Reactor publishes it: under the project that compiled it, which
    // is the app or one of its referenced projects. Only one of them may have it: a file at the same path in another
    // project is not this one. A path relative to the repository root (a file outside every project) is looked for
    // under the folders above the source root.
    const bool rooted = IsSlash(compiledPath[0]) || (compiledPath.size() >= 2 && compiledPath[1] == L':');
    if (!rooted) {
        const std::wstring relative = ToWindowsSlashes(compiledPath);
        if (sourceRoot.empty()) return false;
        const std::vector<std::wstring> found = ProjectCandidates(relative, sourceRoot, projectDirs);
        if (found.size() == 1) { *mapped = found.front(); return true; }
        if (found.size() > 1) {
            if (ambiguous) *ambiguous = found;
            return false;
        }
        return UnderSourceRoot(relative, sourceRoot, mapped);
    }

    if (IsFile(compiledPath)) return true;

    // A Windows Sandbox launch's copy of the sources (marked by its inventory): the recorded host path's longest tail
    // that exists under the copy. Only used when the recorded file is not on this machine.
    std::wstring root;
    if (sourceRoot.empty() || !FullPath(sourceRoot, &root)) return false;
    root = TrimTrailingSlash(root);
    if (!IsFile(root + L"\\.winapp-source-snapshot.json")) return false;
    const std::wstring path = ToWindowsSlashes(compiledPath);
    for (size_t slash = path.find(L'\\'); slash != std::wstring::npos; slash = path.find(L'\\', slash + 1)) {
        const std::wstring rest = path.substr(slash + 1);
        if (rest.empty() || !IsSafeRelative(rest)) continue;
        const std::wstring candidate = root + L"\\" + rest;
        if (IsFile(candidate)) { *mapped = candidate; return true; }
    }
    return false;
}
