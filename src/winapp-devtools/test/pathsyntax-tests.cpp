// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Syntax tests require distinct actionable reasons and syntax-accepted paths that the walker accepts.

#include "DevToolsPathSyntax.h"
#include "DevToolsPathProbe.h"

#include <cstdio>
#include <string>
#include <vector>

// Narrow a wide test label for printf. Explicit rather than an iterator-pair std::string construction, which
// is a lossy implicit conversion the build treats as an error.
static std::string Narrow(const std::wstring& w)
{
    std::string s;
    s.reserve(w.size());
    for (wchar_t c : w) s.push_back(c < 128 ? (char)c : '?');
    return s;
}

static int g_failures = 0;

static void Check(bool cond, const char* what)
{
    if (!cond) { ++g_failures; std::printf("  FAIL  %s\n", what); }
    else       {              std::printf("  ok    %s\n", what); }
}

// The cases the design page lists under "What syntax actually rejects", plus the ones this grammar adds.
static void Test_WellFormedPathsAreAccepted()
{
    std::printf("[syntax] well-formed paths are accepted\n");
    const wchar_t* ok[] = { L"Title", L"Vm.Title", L"Vm.Nested.Title", L"_private", L"A1.B2",
                            L"Vm.Items[0].Title", L"Items[0]", L"Items[key].Name" };
    for (const wchar_t* p : ok) {
        const std::wstring why = DevToolsPathSyntax_Reason(p);
        Check(why.empty(), (std::string("accepted: ") + Narrow(p)).c_str());
    }
}

static void Test_EachMistakeGetsItsOwnReason()
{
    std::printf("[syntax] each mistake names the rule it broke\n");
    const std::wstring empty    = DevToolsPathSyntax_Reason(L"");
    const std::wstring dbldot   = DevToolsPathSyntax_Reason(L"Vm..Title");
    const std::wstring trailing = DevToolsPathSyntax_Reason(L"Vm.");
    const std::wstring leading  = DevToolsPathSyntax_Reason(L".Vm");
    const std::wstring space    = DevToolsPathSyntax_Reason(L"Vm Title");
    const std::wstring digit    = DevToolsPathSyntax_Reason(L"2Vm.Title");

    Check(!empty.empty(),    "an empty path is rejected");
    Check(!dbldot.empty(),   "an empty step between dots is rejected");
    Check(!trailing.empty(), "a trailing dot is rejected");
    Check(!leading.empty(),  "a leading dot is rejected");
    Check(!space.empty(),    "a space is rejected");
    Check(!digit.empty(),    "a step starting with a digit is rejected");

    // THE ASSERTION THAT MATTERS. "invalid path" six times would satisfy every check above.
    Check(dbldot != trailing, "a double dot and a trailing dot do not give the same reason");
    Check(space  != dbldot,   "a space and a double dot do not give the same reason");
    Check(digit  != space,    "a leading digit and a space do not give the same reason");
    Check(leading != trailing,"a leading dot and a trailing dot do not give the same reason");

    // And the space case names the cause rather than the symptom: it must not read as "not a property name".
    Check(space.find(L"space") != std::wstring::npos, "the space case says 'space'");
}

static void Test_IndexersAreCheckedRatherThanWaved()
{
    std::printf("[syntax] indexers are checked, not waved through\n");
    Check(!DevToolsPathSyntax_Reason(L"Items[0").empty(),   "an unclosed indexer is rejected");
    Check(!DevToolsPathSyntax_Reason(L"Items[]").empty(),   "an empty indexer is rejected");
    Check(!DevToolsPathSyntax_Reason(L"Items[[0]]").empty(),"a nested indexer is rejected");
    Check(DevToolsPathSyntax_Reason(L"Items[0].Name").empty(), "a normal indexer followed by a step is accepted");
}

// The grammar this pane can WALK, not the whole {Binding} markup grammar. Green-lighting a path the walk
// cannot follow is the one failure a validator must not have.
static void Test_ItDoesNotGreenLightWhatTheWalkCannotFollow()
{
    std::printf("[syntax] it does not accept markup the walk cannot follow\n");
    Check(!DevToolsPathSyntax_Reason(L"(Grid.Row)").empty(),          "an attached property in parens is rejected");
    Check(!DevToolsPathSyntax_Reason(L"Vm.Title, Mode=OneWay").empty(),"a full binding expression is rejected");
    Check(!DevToolsPathSyntax_Reason(L"Vm/Title").empty(),             "a slash is rejected");
}

// Syntax-OK implies walk-OK, not the reverse. The walker permits opaque segments such as
// 2Vm.Title, Items[0 and Items[] that interactive syntax validation deliberately rejects.
static void Test_ItNeverGreenLightsWhatTheWalkCannotFollow()
{
    std::printf("[syntax] anything it accepts, the walk will follow\n");
    const wchar_t* cases[] = {
        L"Title", L"Vm.Title", L"Vm.Nested.Title", L"_private", L"Items[0]", L"Vm.Items[0].Title",
        L"", L"Vm..Title", L"Vm.", L".Vm", L"Vm Title", L"2Vm.Title", L"Items[0", L"Items[]"
    };
    for (const wchar_t* p : cases) {
        std::vector<std::wstring> segs;
        const bool walkOk   = DevToolsPathProbe_SplitPath(p, &segs);
        const bool syntaxOk = DevToolsPathSyntax_Ok(p);
        const std::string label = "accepted => walkable: '" + Narrow(p) + "'";
        Check(!syntaxOk || walkOk, label.c_str());
    }

    std::printf("[syntax] and it is stricter than the splitter on purpose, in exactly three places\n");
    const wchar_t* stricter[] = { L"2Vm.Title", L"Items[0", L"Items[]" };
    for (const wchar_t* p : stricter) {
        std::vector<std::wstring> segs;
        const bool walkOk = DevToolsPathProbe_SplitPath(p, &segs);
        const std::string label = "deliberately stricter than the splitter on: '" + Narrow(p) + "'";
        Check(walkOk && !DevToolsPathSyntax_Ok(p), label.c_str());
    }
}

int RunPathSyntaxTests()
{
    std::printf("DevToolsPathSyntax tests\n");
    Test_WellFormedPathsAreAccepted();
    Test_EachMistakeGetsItsOwnReason();
    Test_IndexersAreCheckedRatherThanWaved();
    Test_ItDoesNotGreenLightWhatTheWalkCannotFollow();
    Test_ItNeverGreenLightsWhatTheWalkCannotFollow();
    return g_failures;
}
