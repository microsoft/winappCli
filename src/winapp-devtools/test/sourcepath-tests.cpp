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
        Check(!DevToolsSourcePath_MapCompiled(L"../../../../outside/App.cs", app, L"", &mapped) &&
                  mapped == L"../../../../outside/App.cs",
              "a relative path that climbs out of the repository is refused and kept");
        Check(!DevToolsSourcePath_MapCompiled(L"Views/App.cs", L"", L"", &mapped),
              "a relative path does not map without a source root");
        {
            // root\ is a repository with the app (src\App), a referenced library (src\Lib) and a linked file
            // (src\Shared). Reactor publishes each call site relative to the project that compiled it.
            const std::wstring lib = src + L"\\Lib";
            const std::wstring libPanels = lib + L"\\Panels";
            const std::wstring appPanels = app + L"\\Panels";
            const std::wstring shared = src + L"\\Shared";
            CreateDirectoryW((root + L"\\.git").c_str(), nullptr);
            CreateDirectoryW(lib.c_str(), nullptr);
            CreateDirectoryW(libPanels.c_str(), nullptr);
            CreateDirectoryW(shared.c_str(), nullptr);
            WriteEmptyFile(libPanels + L"\\Footer.cs");
            WriteEmptyFile(shared + L"\\Badge.cs");
            Check(!DevToolsSourcePath_MapCompiled(L"Panels/Footer.cs", app, L"", &mapped),
                  "a library's project-relative path is not found under the app without its project");
            Check(DevToolsSourcePath_MapCompiled(L"Panels/Footer.cs", app, L"", &mapped, lib) &&
                      mapped == libPanels + L"\\Footer.cs",
                  "a library's project-relative path resolves under the referenced project");
            CreateDirectoryW(appPanels.c_str(), nullptr);
            WriteEmptyFile(appPanels + L"\\Footer.cs");
            std::vector<std::wstring> ambiguous;
            Check(!DevToolsSourcePath_MapCompiled(L"Panels/Footer.cs", app, L"", &mapped, lib, &ambiguous) &&
                      mapped == L"Panels/Footer.cs" && ambiguous.size() == 2,
                  "a same-named file under the app makes the path ambiguous instead of resolving to the app's file");
            Check(DevToolsSourcePath_MapCompiled(L"../Shared/Badge.cs", app, L"", &mapped, lib) &&
                      mapped == shared + L"\\Badge.cs",
                  "a linked file's ../ path resolves when the file is inside the repository");
            Check(DevToolsSourcePath_MapCompiled(L"Panels/../../Shared/Badge.cs", app, L"", &mapped) &&
                      mapped == shared + L"\\Badge.cs",
                  "a ../ path is normalized");
            std::wstring commentFile;
            Check(DevToolsSourcePath_ResolveProjectFile(app, L"Panels\\Footer.cs", &commentFile) &&
                      commentFile == appPanels + L"\\Footer.cs",
                  "a comment's project-relative C# file resolves under the source root only, not a referenced project");
            Check(DevToolsSourcePath_ResolveProjectFile(app, L"..\\Shared\\Badge.cs", &commentFile) &&
                      commentFile == shared + L"\\Badge.cs",
                  "a comment's C# file elsewhere in the repository resolves");
            Check(!DevToolsSourcePath_ResolveProjectFile(app, L"..\\..\\..\\..\\outside\\App.cs", &commentFile) &&
                      commentFile.empty(),
                  "a comment's C# file that climbs out of the repository is refused");
            Check(!DevToolsSourcePath_ResolveProjectFile(app, app + L"\\Panels\\Footer.cs", &commentFile) &&
                      !DevToolsSourcePath_ResolveProjectFile(L"", L"Panels\\Footer.cs", &commentFile) &&
                      !DevToolsSourcePath_ResolveProjectFile(app, L"Panels\\Missing.cs", &commentFile),
                  "a rooted, rootless or missing comment file does not resolve");
            DeleteFileW((appPanels + L"\\Footer.cs").c_str());
            DeleteFileW((libPanels + L"\\Footer.cs").c_str());
            DeleteFileW((shared + L"\\Badge.cs").c_str());
            RemoveDirectoryW(appPanels.c_str());
            RemoveDirectoryW(libPanels.c_str());
            RemoveDirectoryW(lib.c_str());
            RemoveDirectoryW(shared.c_str());
            RemoveDirectoryW((root + L"\\.git").c_str());
        }
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

    std::printf("== C# hook names ==\n");
    {
        using namespace DevToolsCSharpSource;
        const auto login = SplitLines(
            L"namespace App;\n"
            L"class Login2<T> : Component<Login2Props>\n"
            L"{\n"
            L"    public override Element Render()\n"
            L"    {\n"
            L"        // UseState(9) in a comment\n"
            L"        var (password, setPassword) = UseState(\"hook-pass-1\");\n"
            L"        var theme = ctx.UseContext(Theme.Current);\n"
            L"        UseEffect(() => { if (password.Length > 0) { return; } });\n"
            L"        var (_, setTick) = this.UseReducer(0);\n"
            L"        return TextBlock(\"x\");\n"
            L"    }\n"
            L"}\n");
        std::vector<std::wstring> names;
        const std::vector<std::wstring> kinds = {L"state", L"context", L"effect", L"reducer"};
        Check(HookNames(login, 5, 5, kinds, &names) && names.size() == 4 && names[0] == L"password" && names[1] == L"theme" &&
                  names[2].empty() && names[3].empty(),
              "hooks are named from deconstructions and assignments in call order, with ctx. and this. receivers");
        Check(HookNames(login, 7, 9, kinds, &names) && names[0] == L"password", "a Release build's first statement finds the same body");
        Check(!HookNames(login, 5, 5, {L"state", L"context", L"effect"}, &names) && names.empty(), "a different hook count names nothing");
        Check(!HookNames(login, 5, 5, {L"state", L"state", L"effect", L"reducer"}, &names), "kinds that do not line up name nothing");

        auto body = [](const std::wstring& statements) {
            return SplitLines(L"class C : Component\n{\n    public override Element Render()\n    {\n" + statements +
                              L"\n        return TextBlock(\"x\");\n    }\n}\n");
        };
        Check(!HookNames(body(L"        if (Props.On) { var (a, setA) = UseState(0); }"), 4, 5, {L"state"}, &names),
              "a hook in a branch names nothing");
        Check(!HookNames(body(L"        if (Props.Hidden) return Empty();\n        var (a, setA) = UseState(0);"), 4, 5, {L"state"}, &names),
              "a hook after an early return names nothing");
        Check(!HookNames(body(L"        var form = UseForm();"), 4, 5, {L"state"}, &names), "a custom hook names nothing");
        Check(!HookNames(body(L"        var a = Props.On ? UseState(1).Value : 0;"), 4, 5, {L"state"}, &names),
              "a hook in a conditional expression names nothing");

        auto secret = [](const std::wstring& name) {
            return name.size() >= 8 && _wcsicmp(name.c_str() + name.size() - 8, L"password") == 0;
        };
        const auto dark = body(L"        var dark = UseIsDarkTheme();\n        var (password, setPassword) = UseState(\"hunter2\");");
        Check(!HookNames(dark, 4, 5, {L"state", L"state"}, &names) && BodyNamesSecret(dark, 4, 5, secret),
              "a body the hooks can't be placed in still says it uses a password");
        Check(!BodyNamesSecret(body(L"        var dark = UseIsDarkTheme();\n        var (token, setToken) = UseState(\"Password\");"), 4, 5, secret),
              "a password only in a string or another name is not one");
        Check(!BodyNamesSecret(login, 99, 1, secret), "no body, no password");

        const auto box = body(L"        var (pwd, setPwd) = UseState(\"hunter2\");\n        var (user, setUser) = UseState(\"\");\n"
                              L"        var form = UseState(new Form());\n        var (pin, setPin) = UseState(\"\");\n"
                              L"        var (other, setOther) = UseState(\"\");\n"
                              L"        var a = PasswordBox(pwd, setPwd);\n        var b = TextBox(user, setUser);\n"
                              L"        var c = Field(label: user, adminPassword: form.Value.Secret);\n"
                              L"        var d = new Login { UserPassword = pin!, Name = other };\n"
                              L"        var e = PasswordBox(default, null);\n        var f = PasswordBox(pwd + other);");
        const auto variables = PasswordVariables(box, 4, 5, secret);
        Check(variables == std::vector<std::wstring>{L"pwd", L"form", L"pin"},
              "a PasswordBox argument, a password-named argument and a password-named member give their variables");
        Check(BodyNamesSecret(body(L"        var (pwd, setPwd) = UseState(\"\");\n        var a = PasswordBox(pwd, setPwd);"), 4, 5, secret),
              "a body with a PasswordBox uses a password");
        Check(BodyNamesSecret(body(L"        var (p, setP) = UseState(\"\");\n        var a = LoginForm(onPasswordChanged: setP);"), 4, 5, secret),
              "so does a body that hands a setter to a password-changed handler");
        Check(PasswordVariables(body(L"        var ok = userPassword == other;\n        Func<int> f = () => 1;"), 4, 5, secret).empty(),
              "a comparison is not a password assignment");
        Check(PasswordVariables(login, 99, 1, secret).empty(), "no body, no password variables");
        const auto uncontrolled = body(L"        var (a, setA) = UseState(\"\");\n        var (b, setB) = UseState(\"\");\n"
                                       L"        var (c, setC) = UseState(\"\");\n        var d = UseState(\"\");\n"
                                       L"        var (e, setE) = UseState(\"\");\n        var (shown, setShown) = UseState(\"\");\n"
                                       L"        var x1 = PasswordBox(default, setA);\n"
                                       L"        var x2 = PasswordBox(onPasswordChanged: v => setB(v));\n"
                                       L"        var x3 = PasswordBox().PasswordChanged(setC);\n"
                                       L"        var x4 = PasswordBox(null, d.Set);\n"
                                       L"        var x5 = PasswordBox(placeholderText: \"pin\", password: e, onPasswordChanged: setE);\n"
                                       L"        var x6 = TextBox(shown, setShown);");
        const auto setterVariables = PasswordVariables(uncontrolled, 4, 5, secret);
        auto has = [&](const wchar_t* name) {
            return std::find(setterVariables.begin(), setterVariables.end(), name) != setterVariables.end();
        };
        Check(has(L"a") && has(L"b") && has(L"c") && has(L"d") && has(L"e") && !has(L"shown"),
              "a hook whose setter a PasswordBox calls holds the password, as in an uncontrolled PasswordBox");
    }

    std::printf("== C# literals the scanner must not misread ==\n");
    {
        // @"""" is a verbatim string holding one quote, not the start of a raw string that runs to the end of the file.
        const std::vector<std::wstring> verbatimQuote{L"var q = @\"\"\"\" + F(", L"    1);", L"var b = Button(\"Save\");"};
        Check(DevToolsCSharpSource::Statement(verbatimQuote, 1, 0) == L"var q = @\"\"\"\" + F(\n    1);",
              "a call after a verbatim string of one quote ends where its parenthesis closes");
        const auto masked = DevToolsCSharpSource::Mask(verbatimQuote);
        Check(masked.size() == 3 && masked[2].find(L"Button(") != std::wstring::npos,
              "the line after a verbatim string of one quote is still code");

        std::wstring quoteChar = L"PasswordBox(c == '\"' ? \"hunter2\" : \"y\")";
        DevToolsCSharpSource::Redact(quoteChar);
        Check(quoteChar == L"PasswordBox(c == '\"' ? \"<redacted>\" : \"<redacted>\")",
              "a quote character literal does not hide the password after it");
        std::wstring comment = L"PasswordBox(/* \" */ \"hunter2\")";
        DevToolsCSharpSource::Redact(comment);
        Check(comment == L"PasswordBox(/* \" */ \"<redacted>\")", "a quote in a comment does not hide the password after it");
        std::wstring prose = L"Don't reuse the password \"hunter2\"";
        DevToolsCSharpSource::Redact(prose);
        Check(prose == L"Don't reuse the password \"<redacted>\"",
              "an apostrophe in text inside a multi-line string does not hide the password after it");
        std::wstring multiline = L"PasswordBox(@\"first\nhunter2\")";
        DevToolsCSharpSource::Redact(multiline);
        Check(multiline == L"PasswordBox(@\"<redacted>\")", "a verbatim string that spans lines is redacted to its end");
    }

    DeleteFileW((pages + L"\\MainPage.xaml").c_str());
    DeleteFileW((root + L"\\notes.txt").c_str());
    RemoveDirectoryW(pages.c_str());
    RemoveDirectoryW(root.c_str());
    return g_failures;
}
