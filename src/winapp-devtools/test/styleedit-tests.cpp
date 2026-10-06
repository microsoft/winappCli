// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// The inspector's Edit Style card: the copy-style command line it builds, the keys it accepts, and what it says for
// each answer winapp can give.

#include "DevToolsStyleEdit.h"
#include "DevToolsStyleSource.h"

#include <algorithm>
#include <cstdio>
#include <string>

static int g_styleEditFailures = 0;

static void CheckStyleEdit(bool condition, const char* what)
{
    if (!condition) { ++g_styleEditFailures; std::printf("  FAIL  %s\n", what); }
    else            {                        std::printf("  ok    %s\n", what); }
}

static bool Has(const std::wstring& text, const std::wstring& part) { return text.find(part) != std::wstring::npos; }
static bool Ends(const std::wstring& text, const std::wstring& tail)
{
    return text.size() >= tail.size() && text.compare(text.size() - tail.size(), tail.size(), tail) == 0;
}

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
        L"noise {\"ok\":true,\"written\":false,\"key\":\"BigButton1\",\"source\":{\"definedIn\":\"app\",\"key\":\"BigButton\",\"targetType\":\"Button\","
        L"\"file\":\"Styles/Buttons.xaml\",\"path\":\"C:\\\\src\\\\Styles\\\\Buttons.xaml\",\"line\":7}} trailing");
    CheckStyleEdit(o.ok && o.message == L"Uses BigButton from Buttons.xaml." && o.sourceInApp && o.sourceKey == L"BigButton" &&
                       o.sourcePath == L"C:\\src\\Styles\\Buttons.xaml" && o.sourceLine == 7 && o.key == L"BigButton1",
                   "Edit current on an app Style names it, its file and line, and the key a copy would get");

    o = Interpret(Locate(), 0,
        L"{\"ok\":true,\"key\":\"ButtonStyle1\",\"source\":{\"definedIn\":\"winui\",\"defaultStyle\":true,\"targetType\":\"Button\",\"file\":\"generic.xaml\","
        L"\"path\":\"C:\\\\nuget\\\\generic.xaml\",\"line\":4510,\"package\":\"Microsoft.WindowsAppSDK.WinUI 1.8\"}}");
    CheckStyleEdit(o.ok && !o.sourceInApp && o.sourceName == L"WinUI's default Button style" && Has(o.message, L"Edit a copy") &&
                       !Has(o.message, L"winapp ") && o.key == L"ButtonStyle1",
                   "Edit current on WinUI's Style says it is built in and offers a copy, with no command line");

    o = Interpret(Copy(L"BigButton"), 0,
        L"{\"ok\":true,\"written\":true,\"key\":\"BigButton\",\"implicit\":false,\"targetType\":\"Button\","
        L"\"source\":{\"definedIn\":\"winui\",\"defaultStyle\":true,\"targetType\":\"Button\",\"file\":\"generic.xaml\",\"line\":4510},"
        L"\"edits\":[{\"kind\":\"setStyle\",\"file\":\"MainWindow.xaml\",\"path\":\"C:\\\\src\\\\MainWindow.xaml\",\"line\":22,\"text\":\"...\"},"
        L"{\"kind\":\"addStyle\",\"file\":\"App.xaml\",\"path\":\"C:\\\\src\\\\App.xaml\",\"line\":14,\"text\":\"...\"}],"
        L"\"notes\":[\"The copy keeps WinUI's resource keys.\"]}");
    CheckStyleEdit(o.ok && o.message == L"Created BigButton in App.xaml." && o.copyPath == L"C:\\src\\App.xaml" && o.copyLine == 14 &&
                       o.notes.size() == 1 && !Has(o.message, L"Rebuild"),
                   "a keyed copy says where it went, short, with notes kept apart");

    o = Interpret(Copy(L"", true), 0,
        L"{\"ok\":true,\"written\":true,\"implicit\":true,\"targetType\":\"Button\","
        L"\"source\":{\"definedIn\":\"app\",\"key\":\"BigButton\",\"targetType\":\"Button\",\"file\":\"App.xaml\",\"line\":9},"
        L"\"edits\":[{\"kind\":\"addStyle\",\"file\":\"App.xaml\",\"path\":\"C:\\\\src\\\\App.xaml\",\"line\":20}]}");
    CheckStyleEdit(o.ok && o.implicit && o.message == L"Created an implicit Button style in App.xaml.",
                   "Apply to all creates an implicit style");

    o = Interpret(Copy(), 0, L"{\"ok\":true,\"written\":false,\"source\":{\"definedIn\":\"app\",\"targetType\":\"Button\",\"file\":\"App.xaml\"}}");
    CheckStyleEdit(!o.ok && Has(o.message, L"did not write"), "a copy that was only planned is not reported as done");

    o = Interpret(Locate(), 0, L"{\"ok\":true}");
    CheckStyleEdit(!o.ok && Has(o.message, L"did not say which Style"), "an answer without a source is a failure");
}

static std::string Utf8(const std::wstring& s)
{
    std::string out;
    for (const wchar_t c : s) out += static_cast<char>(c); // test inputs are ASCII
    return out;
}

static const wchar_t* AppXaml =
    L"<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n"
    L"<Application x:Class=\"Probe.App\"\r\n"
    L"    xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"\r\n"
    L"    xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\r\n"
    L"    xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\"\r\n"
    L"    xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" mc:Ignorable=\"d\">\r\n"
    L"    <Application.Resources>\r\n"
    L"        <ResourceDictionary xmlns:local=\"using:Probe\">\r\n"
    L"            <Style x:Key=\"Big\" TargetType=\"Button\">\r\n"
    L"                <Setter Property=\"FontSize\" Value=\"24\"/>\r\n"
    L"                <Setter Property=\"Template\">\r\n"
    L"                    <Setter.Value><ControlTemplate TargetType=\"Button\"><Style x:Key=\"Inner\" TargetType=\"Button\"/></ControlTemplate></Setter.Value>\r\n"
    L"                </Setter>\r\n"
    L"            </Style>\r\n"
    L"            <Style TargetType=\"local:Card\"><Setter Property=\"Padding\" Value=\"4\"/></Style>\r\n"
    L"            <Style x:Key=\"Empty\" TargetType=\"TextBlock\" BasedOn=\"{StaticResource BodyTextBlockStyle}\" />\r\n"
    L"        </ResourceDictionary>\r\n"
    L"    </Application.Resources>\r\n"
    L"</Application>\r\n";

static void StyleSourceTests()
{
    using namespace DevToolsStyleSource;
    const std::string bytes = Utf8(AppXaml);

    Found f = Extract(bytes, L"Big", L"Button");
    CheckStyleEdit(f.ok && f.line == 9 && f.templateLine == 11, "a keyed Style is found with its line and its Template setter's line");
    CheckStyleEdit(f.ok && f.markup.rfind(L"<Style xmlns=", 0) == 0 && Has(f.markup, L" TargetType=\"Button\">") && !Has(f.markup, L"x:Key=\"Big\"") &&
                       Ends(f.markup, L"</Style>") &&
                       Has(f.markup, L"x:Key=\"Inner\"") && !Has(f.markup, L"local:Card") && !Has(f.markup, L"Application"),
                   "the Style is the root, without its own x:Key, nested Styles included, and nothing after it");
    CheckStyleEdit(Has(f.markup, L"xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"") &&
                       Has(f.markup, L"xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"") && Has(f.markup, L"xmlns:local=\"using:Probe\"") &&
                       Has(f.markup, L"wrapmc:Ignorable=\"d\""),
                   "the root carries every namespace in scope and mc:Ignorable");
    CheckStyleEdit(std::count(f.markup.begin(), f.markup.end(), L'\n') == 5, "the Style keeps its lines, starting on line 1");

    f = Extract(bytes, L"", L"Card");
    CheckStyleEdit(f.ok && f.line == 15 && f.templateLine == 0 && Has(f.markup, L" TargetType=\"local:Card\">"),
                   "an implicit Style is found by its TargetType, prefix or not");

    f = Extract(bytes, L"Empty", L"TextBlock");
    CheckStyleEdit(f.ok && Ends(f.markup, L"BasedOn=\"{StaticResource BodyTextBlockStyle}\" />"),
                   "a self-closing Style is sliced to its '/>'");

    f = Extract(Utf8(L"<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">\n"
                     L"<Style x:Key=\"S\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" TargetType=\"Button\"/>\n"
                     L"</ResourceDictionary>"), L"S", L"Button");
    CheckStyleEdit(f.ok && !Has(f.markup, L"x:Key") && Has(f.markup, L"xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"") &&
                       f.markup.find(L"xmlns:x=") == f.markup.rfind(L"xmlns:x="),
                   "a namespace the Style declares itself is not declared twice");

    f = Extract(bytes, L"Inner", L"Button");
    CheckStyleEdit(f.ok && f.line == 12, "a Style nested in a template is still found by key");

    f = Extract(bytes, L"Missing", L"Button");
    CheckStyleEdit(!f.ok && Has(f.error, L"Missing"), "a key that isn't there says so");

    f = Extract(Utf8(L"<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Style>"), L"X", L"");
    CheckStyleEdit(!f.ok && Has(f.error, L"well-formed"), "a half-saved file is reported, not sliced");

    CheckStyleEdit(FileLine(L"The text associated with this error code could not be found. [Line: 4 Position: 12]", 9) == 12 &&
                       FileLine(L"no line here", 9) == 0,
                   "XamlReader's line maps back to the file");

    std::wstring type, member;
    CheckStyleEdit(UnknownAttachable(L"The attachable property 'State' was not found in type 'AnimatedIcon'. [Line: 20 Position: 724]", &type, &member) &&
                       type == L"AnimatedIcon" && member == L"State" && !UnknownAttachable(L"Some other failure", &type, &member),
                   "an unknown attached property is read from XamlReader's message");

    const std::wstring markup =
        L"<wrap:ResourceDictionary xmlns:wrap=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:controls=\"using:Microsoft.UI.Xaml.Controls\">\n"
        L"<Style TargetType=\"Button\">\n"
        L"  <ContentPresenter x:Name=\"P\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n"
        L"      controls:AnimatedIcon.State=\"Normal\" Padding=\"1\" />\n"
        L"  <VisualState>\n"
        L"    <Setter Target=\"P.(controls:AnimatedIcon.State)\" Value=\"PointerOver\" />\n"
        L"    <Setter Target=\"P.Background\" Value=\"Red\" />\n"
        L"    <Setter Target=\"P.(controls:AnimatedIcon.State)\">\n"
        L"      <Setter.Value>Pressed</Setter.Value>\n"
        L"    </Setter>\n"
        L"  </VisualState>\n"
        L"</Style>\n"
        L"</wrap:ResourceDictionary>";
    const std::wstring stripped = WithoutAttachable(markup, L"AnimatedIcon", L"State");
    CheckStyleEdit(!stripped.empty() && !Has(stripped, L"AnimatedIcon") && Has(stripped, L"Padding=\"1\"") &&
                       Has(stripped, L"Target=\"P.Background\"") && Has(stripped, L"x:Name=\"P\""),
                   "the unknown attached property's attributes and Setters are removed, everything else stays");
    CheckStyleEdit(std::count(stripped.begin(), stripped.end(), L'\n') == std::count(markup.begin(), markup.end(), L'\n'),
                   "removing them keeps the line count so errors still map to the file");
    CheckStyleEdit(WithoutAttachable(markup, L"Grid", L"Row").empty(), "nothing to remove gives nothing to retry");
}

int RunStyleEditTests()
{
    std::printf("StyleEdit tests\n");
    ArgumentTests();
    KeyTests();
    InterpretTests();
    StyleSourceTests();
    std::printf("  StyleEdit failures: %d\n", g_styleEditFailures);
    return g_styleEditFailures;
}