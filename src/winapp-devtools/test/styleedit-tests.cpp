// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// The inspector's Edit Style card: the copy-style command line it builds, the keys it accepts, and what it says for
// each answer winapp can give.

#include "DevToolsStyleEdit.h"

#include <cstdio>
#include <string>

static int g_styleEditFailures = 0;

static void CheckStyleEdit(bool condition, const char* what)
{
    if (!condition) { ++g_styleEditFailures; std::printf("  FAIL  %s\n", what); }
    else            {                        std::printf("  ok    %s\n", what); }
}

static bool Has(const std::wstring& text, const std::wstring& part) { return text.find(part) != std::wstring::npos; }

using namespace DevToolsStyleEdit;

static Request Copy(const std::wstring& key = L"", bool all = false, const std::wstring& into = L"")
{
    Request r;
    r.wire = 42; r.pid = 1234; r.action = Action::Copy; r.key = key; r.allOfType = all; r.into = into;
    return r;
}

static Request Locate()
{
    Request r;
    r.wire = 42; r.pid = 1234;
    return r;
}

static void ArgumentTests()
{
    CheckStyleEdit(Arguments(Locate()) == L"devtools resources copy-style 42 --app 1234 --json",
                   "Edit current only looks: no --write, no key");
    CheckStyleEdit(Arguments(Copy(L"BigButton")) ==
                       L"devtools resources copy-style 42 --app 1234 --json --write --key \"BigButton\"",
                   "Edit a copy with a name writes under that key");
    CheckStyleEdit(Arguments(Copy()) == L"devtools resources copy-style 42 --app 1234 --json --write",
                   "an empty name lets winapp pick the next free key");
    CheckStyleEdit(Arguments(Copy(L"Ignored", true)) == L"devtools resources copy-style 42 --app 1234 --json --write --all-of-type",
                   "Apply to all drops the key");
    CheckStyleEdit(Has(Arguments(Copy(L"", false, L"C:\\My App\\MainPage.xaml")), L" --into \"C:\\My App\\MainPage.xaml\""),
                   "a document path with spaces is quoted");
}

static void KeyTests()
{
    CheckStyleEdit(ValidKey(L"ButtonStyle1") && ValidKey(L"_private") && ValidKey(L"Brand.Primary-Button"),
                   "letters, digits, '_', '-' and '.' are accepted");
    CheckStyleEdit(!ValidKey(L"") && !ValidKey(L"1Style") && !ValidKey(L"My Style") && !ValidKey(L"a\"b") && !ValidKey(L"{x}"),
                   "empty, leading digit, spaces, quotes and braces are refused");
    CheckStyleEdit(Trim(L"  Key \t") == L"Key" && Trim(L"   ").empty(), "Trim strips surrounding whitespace only");
}

static void InterpretTests()
{
    Outcome o = Interpret(Copy(), -4, L"");
    CheckStyleEdit(!o.ok && Has(o.message, L"read-only"), "a read-only connection says why nothing was written");
    o = Interpret(Locate(), -1, L"");
    CheckStyleEdit(!o.ok && Has(o.message, L"Reconnect"), "a missing winapp path asks to reconnect");

    o = Interpret(Locate(), 1, L"Unhandled exception");
    CheckStyleEdit(!o.ok && Has(o.message, L"exit code 1"), "non-JSON output reports the exit code");

    o = Interpret(Copy(), 1, L"{\"ok\":false,\"error\":{\"message\":\"App.xaml:12 already has an implicit Style for Button.\"}}");
    CheckStyleEdit(!o.ok && o.message == L"App.xaml:12 already has an implicit Style for Button.",
                   "winapp's own error message is shown as-is");

    o = Interpret(Locate(), 0,
        L"noise {\"ok\":true,\"written\":false,\"source\":{\"definedIn\":\"app\",\"key\":\"BigButton\",\"targetType\":\"Button\","
        L"\"file\":\"Styles/Buttons.xaml\",\"path\":\"C:\\\\src\\\\Styles\\\\Buttons.xaml\",\"line\":7}} trailing");
    CheckStyleEdit(o.ok && o.message == L"Uses your Style BigButton at Styles/Buttons.xaml:7." &&
                       o.openPath == L"C:\\src\\Styles\\Buttons.xaml" && o.openLine == 7 && o.openLabel == L"Open Buttons.xaml" &&
                       o.editable,
                   "Edit current on an app Style points at its file and line and opens it");

    o = Interpret(Locate(), 0,
        L"{\"ok\":true,\"source\":{\"definedIn\":\"winui\",\"defaultStyle\":true,\"targetType\":\"Button\",\"file\":\"generic.xaml\","
        L"\"path\":\"C:\\\\nuget\\\\generic.xaml\",\"line\":4510,\"package\":\"Microsoft.WindowsAppSDK.WinUI 1.8\"}}");
    CheckStyleEdit(o.ok && Has(o.message, L"WinUI's default Button Style (generic.xaml:4510 in Microsoft.WindowsAppSDK.WinUI 1.8)") &&
                       Has(o.message, L"Edit a copy") && o.openLabel == L"View generic.xaml" && !o.editable,
                   "Edit current on WinUI's Style explains it can't be edited and offers a read-only view");

    o = Interpret(Locate(), 0, L"{\"ok\":true,\"source\":{\"definedIn\":\"winui\",\"defaultStyle\":true,\"targetType\":\"Button\",\"file\":\"generic.xaml\",\"line\":4510}}");
    CheckStyleEdit(o.ok && o.openLabel.empty(), "no link when the package file is not on disk");

    o = Interpret(Copy(L"BigButton"), 0,
        L"{\"ok\":true,\"written\":true,\"key\":\"BigButton\",\"implicit\":false,\"targetType\":\"Button\","
        L"\"source\":{\"definedIn\":\"winui\",\"defaultStyle\":true,\"targetType\":\"Button\",\"file\":\"generic.xaml\",\"line\":4510},"
        L"\"edits\":[{\"kind\":\"addStyle\",\"file\":\"App.xaml\",\"path\":\"C:\\\\src\\\\App.xaml\",\"line\":14,\"text\":\"...\"},"
        L"{\"kind\":\"setStyle\",\"file\":\"MainWindow.xaml\",\"path\":\"C:\\\\src\\\\MainWindow.xaml\",\"line\":22,\"text\":\"...\"}],"
        L"\"notes\":[\"The copy keeps WinUI's resource keys.\"]}");
    CheckStyleEdit(o.ok && Has(o.message, L"Copied WinUI's default Button Style into App.xaml:14 as BigButton.") &&
                       Has(o.message, L"Set this Button's Style in MainWindow.xaml:22.") &&
                       Has(o.message, L"The copy keeps WinUI's resource keys.") && Has(o.message, L"Rebuild and restart") &&
                       o.openPath == L"C:\\src\\App.xaml" && o.openLine == 14 && o.openLabel == L"Open App.xaml" && o.editable,
                   "a keyed copy says where the copy and the Style reference went, and opens the copy");

    o = Interpret(Copy(L"", true), 0,
        L"{\"ok\":true,\"written\":true,\"implicit\":true,\"targetType\":\"Button\","
        L"\"source\":{\"definedIn\":\"app\",\"key\":\"BigButton\",\"targetType\":\"Button\",\"file\":\"App.xaml\",\"line\":9},"
        L"\"edits\":[{\"kind\":\"addStyle\",\"file\":\"App.xaml\",\"path\":\"C:\\\\src\\\\App.xaml\",\"line\":20}]}");
    CheckStyleEdit(o.ok && Has(o.message, L"Copied your Style BigButton into App.xaml:20 as the implicit Style for every Button.") &&
                       !Has(o.message, L"Set this"),
                   "Apply to all says every element of the type gets it, with no per-element edit");

    o = Interpret(Copy(), 0, L"{\"ok\":true,\"written\":false,\"source\":{\"definedIn\":\"app\",\"targetType\":\"Button\",\"file\":\"App.xaml\"}}");
    CheckStyleEdit(!o.ok && Has(o.message, L"did not write"), "a copy that was only planned is not reported as done");

    o = Interpret(Locate(), 0, L"{\"ok\":true}");
    CheckStyleEdit(!o.ok && Has(o.message, L"did not say which Style"), "an answer without a source is a failure");
}

int RunStyleEditTests()
{
    std::printf("StyleEdit tests\n");
    ArgumentTests();
    KeyTests();
    InterpretTests();
    std::printf("  StyleEdit failures: %d\n", g_styleEditFailures);
    return g_styleEditFailures;
}
