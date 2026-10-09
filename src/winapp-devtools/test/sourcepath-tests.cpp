// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "DevToolsSourcePath.h"

#include <windows.h>
#include <cstdio>
#include <string>

namespace
{
int g_failures = 0;

void Check(bool condition, const char* message)
{
    if (condition) std::printf("  ok    %s\n", message);
    else { ++g_failures; std::printf("  FAIL  %s\n", message); }
}

void WriteEmptyFile(const std::wstring& path)
{
    HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                              FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
}
}

int RunSourcePathTests()
{
    std::printf("== source URI containment ==\n");

    Check(DevToolsSourcePath_AppUri(L"ms-resource:///Files/Controls/HeaderTile.xaml") == L"ms-appx:///Controls/HeaderTile.xaml",
          "a same-package file resource maps to its app source URI");
    Check(DevToolsSourcePath_AppUri(L"MS-RESOURCE:///files/Styles/Card.xaml") == L"ms-appx:///Styles/Card.xaml",
          "the file resource scheme is matched case-insensitively");
    Check(DevToolsSourcePath_AppUri(L"ms-resource:///Files/Microsoft.UI.Xaml;component/themes/generic.xaml") ==
              L"ms-resource:///Files/Microsoft.UI.Xaml;component/themes/generic.xaml",
          "a framework component resource is not treated as app source");
    Check(DevToolsSourcePath_AppUri(L"ms-resource://OtherPackage/Files/Page.xaml") == L"ms-resource://OtherPackage/Files/Page.xaml",
          "another package's resource is not treated as app source");
    Check(DevToolsSourcePath_AppUri(L"ms-appx:///MainPage.xaml") == L"ms-appx:///MainPage.xaml",
          "an app URI is unchanged");
    wchar_t temp[MAX_PATH];
    DWORD tempLength = GetTempPathW(_countof(temp), temp);
    Check(tempLength > 0 && tempLength < _countof(temp), "temporary directory is available");
    if (tempLength == 0 || tempLength >= _countof(temp)) return g_failures;

    wchar_t leaf[80];
    _snwprintf_s(leaf, _countof(leaf), _TRUNCATE, L"winapp-devtools-sourcepath-%lu-%lu",
                 GetCurrentProcessId(), GetTickCount());
    std::wstring root = std::wstring(temp) + leaf;
    std::wstring pages = root + L"\\Pages";
    Check(CreateDirectoryW(root.c_str(), nullptr) == TRUE, "test source root created");
    Check(CreateDirectoryW(pages.c_str(), nullptr) == TRUE, "nested source directory created");
    WriteEmptyFile(pages + L"\\MainPage.xaml");
    WriteEmptyFile(root + L"\\notes.txt");

    std::wstring resolved;
    Check(DevToolsSourcePath_ResolveExisting(root, L"ms-appx:///Pages/MainPage.xaml", &resolved),
          "existing relative XAML URI resolves");
    Check(!resolved.empty() && resolved.find(L"MainPage.xaml") != std::wstring::npos,
          "resolved path names the intended XAML file");
    Check(DevToolsSourcePath_ResolveExisting(root, L"ms-appx:///Pages/MainPage.XAML", &resolved),
          "XAML extension comparison is case-insensitive");

    Check(!DevToolsSourcePath_ResolveExisting(root, L"ms-appx:///../outside.xaml", &resolved),
          "parent traversal is refused");
    Check(!DevToolsSourcePath_ResolveExisting(root, L"ms-appx:///Pages/./MainPage.xaml", &resolved),
          "dot segments are refused");
    Check(!DevToolsSourcePath_ResolveExisting(root, L"ms-appx:///Pages//MainPage.xaml", &resolved),
          "empty path segments are refused");
    Check(!DevToolsSourcePath_ResolveExisting(root, L"ms-appx:///C:/outside.xaml", &resolved),
          "drive-qualified paths are refused");
    Check(!DevToolsSourcePath_ResolveExisting(root, L"ms-appx:///Pages\\MainPage.xaml", &resolved),
          "backslash path forms are refused");
    Check(!DevToolsSourcePath_ResolveExisting(root, L"file:///Pages/MainPage.xaml", &resolved),
          "non-ms-appx URI schemes are refused");
    Check(!DevToolsSourcePath_ResolveExisting(root, L"ms-appx:///notes.txt", &resolved),
          "non-XAML files are refused");
    Check(!DevToolsSourcePath_ResolveExisting(root, L"ms-appx:///Missing.xaml", &resolved),
          "missing files are refused");

    Check(DevToolsSourcePath_IsWithinRoot(L"\\\\?\\C:\\repo\\app", L"\\\\?\\c:\\repo\\app\\Page.xaml"),
          "canonical containment is case-insensitive");
    Check(!DevToolsSourcePath_IsWithinRoot(L"\\\\?\\C:\\repo\\app", L"\\\\?\\C:\\repo\\app-evil\\Page.xaml"),
          "sibling path sharing the root prefix is refused");
    Check(!DevToolsSourcePath_IsWithinRoot(L"\\\\?\\C:\\repo\\app", L"\\\\?\\C:\\repo\\app"),
          "the source root itself is not a source file");

    DeleteFileW((pages + L"\\MainPage.xaml").c_str());
    DeleteFileW((root + L"\\notes.txt").c_str());
    RemoveDirectoryW(pages.c_str());
    RemoveDirectoryW(root.c_str());
    return g_failures;
}
