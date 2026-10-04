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
