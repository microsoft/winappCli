// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once
#include <windows.h>
#include <shlobj.h>
#include <string>

// Per-user UI preferences (toolbar pin and corner, layout adorners, inspector sections), one file per setting,
// in the CLI's per-user state folder. The user profile, unlike AppData, is not redirected per package, so every
// app DevTools attaches to shares them.
inline std::wstring DevToolsSettingsFile(const wchar_t* name)
{
    PWSTR profile = nullptr;
    if (FAILED(SHGetKnownFolderPath(FOLDERID_Profile, KF_FLAG_DEFAULT, nullptr, &profile)) || !profile) {
        CoTaskMemFree(profile);
        return std::wstring();
    }
    std::wstring dir = std::wstring(profile) + L"\\.winapp";
    CoTaskMemFree(profile);
    CreateDirectoryW(dir.c_str(), nullptr);
    dir += L"\\state";
    CreateDirectoryW(dir.c_str(), nullptr);
    return dir + L"\\devtools-" + name + L".setting";
}

inline bool DevToolsSettingsGetBool(const wchar_t* name, bool fallback)
{
    std::wstring path = DevToolsSettingsFile(name);
    if (path.empty()) return fallback;
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return fallback;
    char buf[1] = { 0 }; DWORD got = 0;
    BOOL ok = ReadFile(h, buf, 1, &got, nullptr);
    CloseHandle(h);
    if (!ok || got == 0) return fallback;
    return buf[0] == '1';
}

inline void DevToolsSettingsSetBool(const wchar_t* name, bool value)
{
    std::wstring path = DevToolsSettingsFile(name);
    if (path.empty()) return;
    HANDLE h = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;
    const char c = value ? '1' : '0'; DWORD wrote = 0;
    WriteFile(h, &c, 1, &wrote, nullptr);
    CloseHandle(h);
}

inline int DevToolsSettingsGetIndex(const wchar_t* name, int fallback, int maxExclusive)
{
    std::wstring path = DevToolsSettingsFile(name);
    if (path.empty()) return fallback;
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return fallback;
    char buf[1] = { 0 }; DWORD got = 0;
    BOOL ok = ReadFile(h, buf, 1, &got, nullptr);
    CloseHandle(h);
    if (!ok || got == 0) return fallback;
    const int v = buf[0] - '0';
    return (v >= 0 && v < maxExclusive) ? v : fallback;
}

inline void DevToolsSettingsSetIndex(const wchar_t* name, int value)
{
    std::wstring path = DevToolsSettingsFile(name);
    if (path.empty() || value < 0 || value > 9) return;
    HANDLE h = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;
    const char c = (char)('0' + value); DWORD wrote = 0;
    WriteFile(h, &c, 1, &wrote, nullptr);
    CloseHandle(h);
}
