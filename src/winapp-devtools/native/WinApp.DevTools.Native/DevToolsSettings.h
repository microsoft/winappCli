// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once
#include <windows.h>
#include <shlobj.h>
#include <cstring>
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

// A short ASCII word, such as the default DevTools mode the CLI and the toolbar share ("on", "off", "headless").
inline std::wstring DevToolsSettingsGetText(const wchar_t* name)
{
    std::wstring path = DevToolsSettingsFile(name);
    if (path.empty()) return std::wstring();
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return std::wstring();
    char buf[32] = { 0 }; DWORD got = 0;
    const BOOL ok = ReadFile(h, buf, sizeof(buf), &got, nullptr);
    CloseHandle(h);
    std::wstring text;
    for (DWORD i = 0; ok && i < got; ++i) {
        const char c = buf[i];
        if (c == ' ' || c == '\r' || c == '\n' || c == '\t') continue;
        text += static_cast<wchar_t>(c >= 'A' && c <= 'Z' ? c - 'A' + 'a' : c);
    }
    return text;
}

inline void DevToolsSettingsSetText(const wchar_t* name, const char* value)
{
    std::wstring path = DevToolsSettingsFile(name);
    if (path.empty()) return;
    HANDLE h = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;
    DWORD wrote = 0;
    WriteFile(h, value, static_cast<DWORD>(strlen(value)), &wrote, nullptr);
    CloseHandle(h);
}