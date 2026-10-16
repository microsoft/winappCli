// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Tests for DevToolsReactorAnchor -- the comment-marker anchor of a Microsoft.UI.Reactor element: encoding, decoding
// (malformed input refused), which candidate an anchor names after a rebuild, project-relative paths and ordinals.
//
// Build/run: scripts/test-native-units.ps1 (also invoked by src/winapp-devtools/build-devtools.ps1).

#include "DevToolsReactorAnchor.h"

#include <cstdio>
#include <string>
#include <vector>

namespace {

int g_failures = 0;

void Check(bool cond, const char* what)
{
    if (!cond) { ++g_failures; std::printf("  FAIL  %s\n", what); }
    else       {              std::printf("  ok    %s\n", what); }
}

DevToolsReactorAnchor::Site Button(unsigned int line, unsigned int ordinal = 0, const wchar_t* key = L"")
{
    DevToolsReactorAnchor::Site site;
    site.file = L"Views\\App.cs";
    site.line = line;
    site.column = 13;
    site.element = L"Button";
    site.key = key;
    site.owner = L"App";
    site.ordinal = ordinal;
    return site;
}

void Test_RoundTrip()
{
    auto site = Button(35, 2, L"k:1");
    site.sub = { { 0, L"ContentPresenter" }, { 1, L"TextBlock" } };
    const auto text = DevToolsReactorAnchor::Encode(site);
    DevToolsReactorAnchor::Site back;
    Check(DevToolsReactorAnchor::IsReactorAnchor(text), "an encoded anchor is a Reactor anchor");
    Check(DevToolsReactorAnchor::Decode(text, &back), "it decodes");
    Check(back.file == site.file && back.line == 35 && back.column == 13 && back.element == L"Button" && back.key == L"k:1" &&
          back.owner == L"App" && back.ordinal == 2, "every field survives, including a key with the separator in it");
    Check(back.sub.size() == 2 && back.sub[1].first == 1 && back.sub[1].second == L"TextBlock", "the path below the call site survives");
    Check(!DevToolsReactorAnchor::IsReactorAnchor(L"source:3:App"), "a XAML anchor is not a Reactor anchor");
}

void Test_DecodeRefusesMalformed()
{
    DevToolsReactorAnchor::Site out;
    const auto good = DevToolsReactorAnchor::Encode(Button(3));
    Check(!DevToolsReactorAnchor::Decode(good + L"x", &out), "trailing text is refused");
    Check(!DevToolsReactorAnchor::Decode(good.substr(0, good.size() - 1), &out), "a truncated anchor is refused");
    Check(!DevToolsReactorAnchor::Decode(L"reactor:99:App.cs", &out), "a length past the end is refused");
    Check(!DevToolsReactorAnchor::Decode(L"reactor:0:1:31:11:13:6:Button0:3:App1:01:0", &out), "an empty file is refused");
    Check(!DevToolsReactorAnchor::Decode(L"reactor:6:App.cs1:x1:16:Button0:3:App1:01:0", &out), "a non-numeric line is refused");
}

void Test_PickPrefersTheExactCallSite()
{
    std::vector<DevToolsReactorAnchor::Site> live{ Button(40), Button(35), Button(35, 1) };
    Check(DevToolsReactorAnchor::Pick(live, Button(35)) == 1, "the exact line and ordinal win");
    Check(DevToolsReactorAnchor::Pick(live, Button(35, 1)) == 2, "the ordinal picks among one call site's elements");
    std::vector<DevToolsReactorAnchor::Site> rebuilt{ Button(41) };
    Check(DevToolsReactorAnchor::Pick(rebuilt, Button(35)) == 0, "after a rebuild moved the line, the only match by everything else");
    std::vector<DevToolsReactorAnchor::Site> two{ Button(41), Button(52) };
    Check(DevToolsReactorAnchor::Pick(two, Button(35)) == -1, "two moved candidates name neither");
    std::vector<DevToolsReactorAnchor::Site> windows{ Button(35), Button(40), Button(35) };
    Check(DevToolsReactorAnchor::Matching(windows, Button(35)) == std::vector<size_t>{ 0, 2 },
          "one call site shown in two windows names both, so the active window's can be chosen");
    std::vector<DevToolsReactorAnchor::Site> movedTwice{ Button(41), Button(41) };
    Check(DevToolsReactorAnchor::Matching(movedTwice, Button(35)).size() == 2,
          "after a rebuild, one moved call site shown twice still names both");
    auto lower = Button(35);
    lower.file = L"views\\app.cs";
    Check(DevToolsReactorAnchor::Pick({ lower }, Button(35)) == 0, "file names compare case-insensitively");
    auto keyed = Button(35, 0, L"b");
    Check(DevToolsReactorAnchor::Pick({ keyed }, Button(35, 0, L"a")) == -1, "a different key is a different element");
}

void Test_NameIsAStrongSignal()
{
    auto named = [](unsigned int line, const wchar_t* name) { auto site = Button(line); site.name = name; return site; };
    DevToolsReactorAnchor::Site back;
    Check(DevToolsReactorAnchor::Decode(DevToolsReactorAnchor::Encode(named(35, L"incrementButton")), &back) &&
          back.name == L"incrementButton", "the C# name survives a round trip");
    std::vector<DevToolsReactorAnchor::Site> moved{ named(41, L"resetButton"), named(52, L"incrementButton") };
    Check(DevToolsReactorAnchor::Pick(moved, named(35, L"incrementButton")) == 1,
          "after both moved, the name tells two call sites of the same kind apart");
    Check(DevToolsReactorAnchor::Pick({ named(41, L"resetButton") }, named(35, L"incrementButton")) == -1,
          "a moved element with another name is not the one the comment was left on");
    Check(DevToolsReactorAnchor::Pick({ named(35, L"renamed") }, named(35, L"incrementButton")) == 0,
          "the exact call site still wins when the variable was renamed");
    Check(DevToolsReactorAnchor::Pick({ Button(41) }, Button(35)) == 0, "an unnamed element resolves as before");
}

void Test_ProjectRelative()
{
    Check(DevToolsReactorAnchor::ProjectRelative(L"C:\\src\\app\\Views\\App.cs", L"C:\\src\\app") == L"Views\\App.cs",
          "a file under the project is relative to it");
    Check(DevToolsReactorAnchor::ProjectRelative(L"c:/SRC/app/App.cs", L"C:\\src\\app\\") == L"App.cs",
          "separators and case do not matter");
    Check(DevToolsReactorAnchor::ProjectRelative(L"D:\\other\\Shared.cs", L"C:\\src\\app") == L"Shared.cs",
          "a file elsewhere is its leaf");
    Check(DevToolsReactorAnchor::ProjectRelative(L"C:\\src\\application\\App.cs", L"C:\\src\\app") == L"App.cs",
          "a sibling folder with the same prefix is not under the project");
}

void Test_Ordinal()
{
    std::vector<std::pair<int, std::vector<size_t>>> peers{ { 7, { 0, 2 } }, { 5, { 0, 0 } }, { 9, { 1 } } };
    Check(DevToolsReactorAnchor::Ordinal(5, peers) == 0, "the first in document order is 0");
    Check(DevToolsReactorAnchor::Ordinal(7, peers) == 1, "deeper paths order by their indexes");
    Check(DevToolsReactorAnchor::Ordinal(9, peers) == 2, "a later branch comes after");
}

} // namespace

int RunReactorAnchorTests()
{
    std::printf("DevToolsReactorAnchor:\n");
    Test_RoundTrip();
    Test_DecodeRefusesMalformed();
    Test_PickPrefersTheExactCallSite();
    Test_NameIsAStrongSignal();
    Test_ProjectRelative();
    Test_Ordinal();
    return g_failures;
}
