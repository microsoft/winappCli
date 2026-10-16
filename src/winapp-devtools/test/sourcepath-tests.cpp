// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "DevToolsSourcePath.h"
#include "DevToolsCSharpSource.h"

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

    std::printf("== compiled C# paths ==\n");
    {
        // root\ (repo) holds src\App\App.cs; the project (source root) is root\src\App.
        const std::wstring src = root + L"\\src";
        const std::wstring app = src + L"\\App";
        Check(CreateDirectoryW(src.c_str(), nullptr) == TRUE && CreateDirectoryW(app.c_str(), nullptr) == TRUE,
              "C# project directories created");
        WriteEmptyFile(app + L"\\App.cs");
        std::wstring mapped;
        Check(DevToolsSourcePath_MapCompiled(app + L"\\App.cs", app, L"", &mapped) && mapped == app + L"\\App.cs",
              "an absolute path that exists is used as is");
        Check(DevToolsSourcePath_MapCompiled(L"/_/src/App/App.cs", app, L"", &mapped) && mapped == app + L"\\App.cs",
              "a deterministic /_/ path resolves under an ancestor of the source root");
        Check(DevToolsSourcePath_MapCompiled(L"/_1/App.cs", app, L"", &mapped) && mapped == app + L"\\App.cs",
              "a second deterministic root /_1/ resolves too");
        {
            const std::wstring snapshot = app + L"-snapshot";
            CreateDirectoryW(snapshot.c_str(), nullptr);
            WriteEmptyFile(snapshot + L"\\App.cs");
            Check(!DevToolsSourcePath_MapCompiled(L"C:\\host-only\\TaskBoard\\App.cs", snapshot, L"", &mapped),
                  "a folder that is not a Sandbox source copy does not map another machine's path by its tail");
            WriteEmptyFile(snapshot + L"\\.winapp-source-snapshot.json");
            Check(DevToolsSourcePath_MapCompiled(L"C:\\host-only\\TaskBoard\\App.cs", snapshot, L"", &mapped) &&
                  mapped == snapshot + L"\\App.cs",
                  "a host path maps to the same file in a Sandbox source copy");
            Check(!DevToolsSourcePath_MapCompiled(L"C:\\host-only\\TaskBoard\\Missing.cs", snapshot, L"", &mapped) &&
                  mapped == L"C:\\host-only\\TaskBoard\\Missing.cs",
                  "a file with no copy stays unmapped");
            DeleteFileW((snapshot + L"\\App.cs").c_str());
            DeleteFileW((snapshot + L"\\.winapp-source-snapshot.json").c_str());
            RemoveDirectoryW(snapshot.c_str());
        }
        Check(DevToolsSourcePath_MapCompiled(L"/_/App.cs", src, root + L"\\src\\App\\=/_/", &mapped) &&
                  mapped == app + L"\\App.cs",
              "PathMap maps a recorded prefix back to its original folder");
        Check(!DevToolsSourcePath_MapCompiled(L"/_/../outside/App.cs", app, L"", &mapped) && mapped == L"/_/../outside/App.cs",
              "a parent segment in a deterministic path is refused and the path is kept");
        Check(!DevToolsSourcePath_MapCompiled(L"/_/src/App/Missing.cs", app, L"", &mapped),
              "a deterministic path with no file on this machine does not map");
        Check(!DevToolsSourcePath_MapCompiled(L"D:\\elsewhere\\App.cs", app, L"", &mapped) && mapped == L"D:\\elsewhere\\App.cs",
              "another machine's absolute path is reported unchanged");
        Check(DevToolsSourcePath_MapCompiled(L"App.cs", app, L"", &mapped) && mapped == app + L"\\App.cs",
              "a project-relative path resolves under the source root");
        Check(DevToolsSourcePath_MapCompiled(L"src/App/App.cs", app, L"", &mapped) && mapped == app + L"\\App.cs",
              "a repository-relative path (a file outside the project) resolves under a folder above it");
        Check(!DevToolsSourcePath_MapCompiled(L"../App/App.cs", app, L"", &mapped) && mapped == L"../App/App.cs",
              "a relative path that climbs out is refused and kept");
        Check(!DevToolsSourcePath_MapCompiled(L"Views/App.cs", L"", L"", &mapped),
              "a relative path does not map without a source root");
        DeleteFileW((app + L"\\App.cs").c_str());
        RemoveDirectoryW(app.c_str());
        RemoveDirectoryW(src.c_str());
    }

    std::printf("== C# statements ==\n");
    {
        using namespace DevToolsCSharpSource;
        const auto lines = SplitLines(
            L"class Counter : Component\r\n"
            L"{\r\n"
            L"        return HStack(12,\r\n"
            L"            TextBlock($\"Done: {count}\").FontSize(20),\r\n"
            L"            Button(\"+1\", () => setCount(count + 1)));\r\n"
            L"    // a comment (with a paren\r\n"
            L"    var s = \"text with ) paren\";\r\n");
        Check(Statement(lines, 4, 0) == L"TextBlock($\"Done: {count}\").FontSize(20),",
              "a one-line factory call is its own statement");
        Check(Statement(lines, 3, 0) ==
                  L"return HStack(12,\n    TextBlock($\"Done: {count}\").FontSize(20),\n    Button(\"+1\", () => setCount(count + 1)));",
              "a call over several lines runs to its closing bracket, indentation shared with the first line removed");
        Check(Statement(lines, 5, 13) == L"Button(\"+1\", () => setCount(count + 1)));",
              "a column starts the scan at the factory, ignoring brackets before it");
        Check(Statement(lines, 3, 0, 2) == L"return HStack(12,\n    TextBlock($\"Done: {count}\").FontSize(20),\n...",
              "a statement cut at the line limit says so");
        Check(Statement(lines, 7, 0) == L"var s = \"text with ) paren\";", "brackets in strings are not counted");
        Check(Statement(lines, 99, 0).empty() && Statement(lines, 0, 0).empty(), "a line outside the file has no statement");

        std::wstring secret = L"PasswordBox(\"hunter2\", header: \"Sign in\") // \"kept\"";
        Redact(secret);
        Check(secret == L"PasswordBox(\"<redacted>\", header: \"<redacted>\") // \"kept\"",
              "string literals in code that mentions a password are redacted");
        std::wstring verbatim = L"var password = @\"a\"\"b\" + $\"{x}\";";
        Redact(verbatim);
        Check(verbatim == L"var password = @\"<redacted>\" + $\"<redacted>\";", "verbatim and interpolated literals are redacted");
        std::wstring plain = L"TextBlock(\"Done today\")";
        Redact(plain);
        Check(plain == L"TextBlock(\"Done today\")", "code that names no password is unchanged");
    }

    DeleteFileW((pages + L"\\MainPage.xaml").c_str());
    DeleteFileW((root + L"\\notes.txt").c_str());
    RemoveDirectoryW(pages.c_str());
    RemoveDirectoryW(root.c_str());
    return g_failures;
}
