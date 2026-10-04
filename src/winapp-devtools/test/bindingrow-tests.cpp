// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Tests for DevToolsBindingRow -- what a row SHOWS for a binding answer.
//
// The original JSON strings below are answers captured from the shipping path against
// src/winapp-devtools/test/winui-binding-fixture, whose MainWindow.xaml is the answer key. They are not
// hand-written approximations of what the agent might say, because a presentation suite fed invented input
// only proves the presenter is self-consistent.
//
// Additional contract cases cover scoped evaluation and restore confirmation. A resolved path must not
// render as a freshness verdict, nor as a fault when a OneTime binding or converter legitimately differs.
//
// Build/run: scripts/test-native-units.ps1 (also invoked by src/winapp-devtools/build-devtools.ps1).

#include "DevToolsBindingRow.h"

#include <cstdio>
#include <string>

static int g_failures = 0;

static void Check(bool cond, const char* what)
{
    if (!cond) { ++g_failures; std::printf("  FAIL  %s\n", what); }
    else       {              std::printf("  ok    %s\n", what); }
}

static void CheckEqW(const std::wstring& got, const std::wstring& want, const char* what)
{
    if (got != want) { ++g_failures; std::printf("  FAIL  %s (want \"%ls\", got \"%ls\")\n", what, want.c_str(), got.c_str()); }
    else             {              std::printf("  ok    %s (\"%ls\")\n", what, got.c_str()); }
}

// ---- verbatim answers from the fixture --------------------------------------------------------------
static const wchar_t* kBadSegment =
    LR"({"state":"bad-segment","segment":"NoSuchPropertyAtAll","reason":"no public property or field 'NoSuchPropertyAtAll' on FixtureViewModel","path":"NoSuchPropertyAtAll","source":"DataContext=FixtureViewModel","kind":"{Binding}","mode":"OneWay"})";
static const wchar_t* kNullLink =
    LR"({"state":"null-link","segment":"Nested","reason":"Nested is null, so 'Title' is never reached","path":"Vm.Nested.Title","source":"MainWindow","kind":"{x:Bind}"})";
static const wchar_t* kNoDataContext =
    LR"({"state":"no-datacontext","segment":"","reason":"DataContext is null on this element and its ancestors","path":"Title","source":"DataContext","kind":"{Binding}","mode":"OneWay"})";
static const wchar_t* kGetterThrew =
    LR"({"state":"threw","segment":"Explodes","reason":"FixtureViewModel.Explodes threw InvalidOperationException: the getter for Explodes always throws","path":"Explodes","source":"DataContext=FixtureViewModel","kind":"{Binding}","mode":"OneWay"})";
static const wchar_t* kConverterThrew =
    LR"({"state":"threw","segment":"ExplodingConverter","reason":"ExplodingConverter threw InvalidOperationException: this converter always throws","path":"Count","source":"DataContext=FixtureViewModel","kind":"{Binding}","mode":"OneWay"})";
static const wchar_t* kSilent =
    LR"({"state":"silent","resolvedValue":"7","resolvedType":"Int32","targetProperty":"Foreground","targetType":"Brush","path":"Count","source":"DataContext=FixtureViewModel","kind":"{Binding}","mode":"OneWay"})";
static const wchar_t* kHealthyBinding =
    LR"({"state":"ok","path":"Title","source":"DataContext=FixtureViewModel","kind":"{Binding}","mode":"OneWay"})";
static const wchar_t* kHealthyXBind =
    LR"({"state":"ok","path":"Vm.Title","source":"MainWindow","kind":"{x:Bind}"})";
static const wchar_t* kNotBound = LR"({"state":"none"})";
static const wchar_t* kUnavailable =
    LR"({"state":"unavailable","reason":"this .NET app has no loaded managed binding host."})";

static void Test_FailingSegmentIsTheHeadline()
{
    std::printf("the failing segment is the headline and the reason is the subtitle\n");

    DevToolsBindingRowView bad = DevToolsBindingRow_FromJson(kBadSegment);
    Check(bad.show && bad.isFault, "a bad segment is shown as a fault");
    CheckEqW(bad.headEmphasis, L"NoSuchPropertyAtAll", "the missing member is emphasised");
    Check(bad.subtitle.find(L"FixtureViewModel") != std::wstring::npos, "the subtitle names the type it looked on");

    DevToolsBindingRowView nul = DevToolsBindingRow_FromJson(kNullLink);
    Check(nul.show && nul.isFault, "a null link is shown as a fault");
    // The whole point of splitting the path: the developer must see WHICH hop went null, in place. A headline
    // of "Vm.Nested.Title" with nothing emphasised sends them to the wrong object.
    CheckEqW(nul.head,         L"Vm.",   "the resolved prefix is kept");
    CheckEqW(nul.headEmphasis, L"Nested", "the null link is emphasised");
    CheckEqW(nul.headTail,     L".Title", "the unreached remainder is kept");
    Check(nul.subtitle.find(L"never reached") != std::wstring::npos, "the subtitle says the rest was never reached");
}

static void Test_ThrowsNameTheThrower()
{
    std::printf("a throw names the thing that threw, because that is the file to open\n");
    DevToolsBindingRowView g = DevToolsBindingRow_FromJson(kGetterThrew);
    CheckEqW(g.headEmphasis, L"Explodes", "a throwing getter is headlined by the property");
    Check(g.subtitle.find(L"InvalidOperationException") != std::wstring::npos, "the exception type is in the subtitle");
    Check(g.isFault, "a throw is a fault");

    DevToolsBindingRowView c = DevToolsBindingRow_FromJson(kConverterThrew);
    CheckEqW(c.headEmphasis, L"ExplodingConverter", "a throwing converter is headlined by the converter");
    Check(c.subtitle.find(L"always throws") != std::wstring::npos, "the converter's own message is carried through");
}

static void Test_NoDataContextSaysSo()
{
    std::printf("a missing DataContext is named, not described as a bad path\n");
    DevToolsBindingRowView v = DevToolsBindingRow_FromJson(kNoDataContext);
    CheckEqW(v.headEmphasis, L"No DataContext", "the headline names the real problem");
    // The path is "Title" and Title is a perfectly good property. Headlining it would send the developer to
    // read a view model that is fine.
    Check(v.head.empty() && v.headTail.empty(), "the innocent path is not put in the headline");
    Check(v.isFault, "it is a fault");
}

static void Test_TheSilentCaseAssertsNoCause()
{
    std::printf("different values print two facts without inventing a cause\n");
    DevToolsBindingRowView v = DevToolsBindingRow_FromJson(kSilent);
    Check(v.show, "it is shown");
    // Different observed values alone do not establish a type-mismatch diagnosis.
    Check(v.subtitle.find(L"mismatch") == std::wstring::npos, "it does not say \"mismatch\"");
    Check(v.subtitle.find(L"type error") == std::wstring::npos, "it does not say \"type error\"");
    Check(v.subtitle.find(L"resolves to 7") != std::wstring::npos, "fact one: what the path resolves to");
    Check(v.subtitle.find(L"(Int32)") != std::wstring::npos, "...and its type");
    Check(v.subtitle.find(L"Foreground expects Brush") != std::wstring::npos, "fact two: what the target expects");
    // It reports without accusing, so it must not render in the fault colour.
    Check(!v.isFault, "it is not presented as a fault, because no cause is claimed");
}

static void Test_UndiagnosableSaysSoRatherThanNothing()
{
    std::printf("a row that cannot be diagnosed says so -- silence would read as health\n");
    DevToolsBindingRowView v = DevToolsBindingRow_FromJson(kUnavailable);
    Check(v.show, "it is shown");
    CheckEqW(v.headEmphasis, L"Diagnosis unavailable", "it says so plainly");
    Check(v.subtitle.find(L"no loaded managed binding host") != std::wstring::npos, "and carries the reason verbatim");
    Check(!v.isFault, "it is not a fault -- nothing is known to be wrong with the binding");
    // Mark unavailable managed diagnosis as a non-answer so a usable native walk can replace it.
    Check(v.unavailable, "and it is marked as a non-answer, not as a diagnosis");

    // The states that ARE answers must not be marked so, or the walk would displace real agent diagnoses.
    Check(!DevToolsBindingRow_FromJson(kBadSegment).unavailable, "a real fault is NOT marked unavailable");
    Check(!DevToolsBindingRow_FromJson(kSilent).unavailable, "and neither is the resolves-but-does-not-apply answer");

    // Garbage in must not become silence out, for the same reason. These are the transport failures: a
    // truncated line, an empty reply, a state a newer agent invented.
    for (const wchar_t* junk : { L"", L"not json at all", L"{\"state\":", L"{\"state\":\"something-new\"}" }) {
        DevToolsBindingRowView g = DevToolsBindingRow_FromJson(junk);
        Check(g.show, "an unusable answer still renders a row");
        Check(!g.headEmphasis.empty(), "...with a headline");
        Check(!g.subtitle.empty(), "...and a reason");
    }

    Check(std::wstring(DevToolsBindingRow_PendingText()).size() > 0,
          "there is a placeholder for the gap before the answer arrives");
}

static void Test_EvaluationDoesNotClaimFreshness()
{
    for (const wchar_t* healthy : { kHealthyBinding, kHealthyXBind }) {
        DevToolsBindingRowView v = DevToolsBindingRow_FromJson(healthy);
        Check(v.show && !v.isFault, "path evaluation is visible but not a fault");
        Check(v.subtitle.find(L"not checked") != std::wstring::npos, "the limited evaluation is disclosed");
    }
    const auto observed = DevToolsBindingRow_FromJson(
        LR"({"state":"evaluated","reason":"Freshness not checked","sourceValue":"NEW-NOT-NOTIFIED","resolvedValue":"formatted","targetValue":"INITIAL-STALE"})");
    Check(observed.show && !observed.isFault && !observed.unavailable, "observations are neither a fault nor a fallback invitation");
    Check(observed.subtitle.find(L"NEW-NOT-NOTIFIED") != std::wstring::npos, "source observation is visible");
    Check(observed.subtitle.find(L"INITIAL-STALE") != std::wstring::npos, "target observation is visible");
    DevToolsBindingRowView none = DevToolsBindingRow_FromJson(kNotBound);
    Check(!none.show, "a property that is not bound renders nothing either");
}

static void Test_ScopeWarningsCannotBeHiddenByNativeFallback()
{
    const auto unsupported = DevToolsBindingRow_FromJson(
        LR"({"state":"unavailable","reason":"unsupported path syntax","nativeFallback":"False"})");
    Check(unsupported.show && !unsupported.isFault && !unsupported.unavailable && unsupported.suppressNativeWalk,
          "unsupported managed syntax is not replaced by a less capable native path walk");
    const auto confirm = DevToolsBindingRow_FromJson(
        LR"({"state":"confirmation-required","restoreOwner":"TestPage","restoreScope":"owner","warning":"Can replace other live edits","restoreDisclosure":"Source/model changes are not undone"})");
    Check(confirm.subtitle.find(L"not undone") != std::wstring::npos, "restore confirmation discloses that source/model changes are not undone");
    Check(confirm.show && !confirm.isFault && !confirm.unavailable, "confirmation is visible and cannot be replaced by a path walk");
    Check(confirm.subtitle.find(L"other live edits") != std::wstring::npos, "the pre-write warning names the other edits");
    Check(confirm.subtitle.find(L"TestPage") != std::wstring::npos, "the owner is disclosed");
}

static void Test_SummaryIsAFewLabelledLines()
{
    std::printf("the summary reduces a diagnosis to status, source, path and value\n");
    const auto bad = DevToolsBindingRow_Summary(kBadSegment);
    Check(bad.tone == DevToolsBindingTone::Broken, "a missing segment is broken");
    CheckEqW(bad.status, L"\u2715 Broken at NoSuchPropertyAtAll", "the status names the segment");
    CheckEqW(bad.reason, L"not found on FixtureViewModel", "the reason is a few words");
    CheckEqW(bad.source, L"FixtureViewModel (DataContext)", "the source names the DataContext type");
    CheckEqW(bad.pathMode, L"NoSuchPropertyAtAll \u00b7 OneWay", "path and mode share one line");
    const auto xbind = DevToolsBindingRow_Summary(kHealthyXBind);
    Check(xbind.tone == DevToolsBindingTone::Works, "a resolving x:Bind works");
    CheckEqW(xbind.source, L"MainWindow (x:Bind owner)", "an x:Bind names its owner");
    const auto evaluated = DevToolsBindingRow_Summary(
        LR"({"state":"evaluated","reason":"Forward path and CLR type evaluated only; target freshness was not checked.","resolvedValue":"Hello","path":"Greeting","source":"MainWindow","kind":"{x:Bind}","mode":"OneWay"})");
    Check(evaluated.tone == DevToolsBindingTone::Works && evaluated.reason.empty(), "engine caveats stay out of the summary");
    CheckEqW(evaluated.value, L"Hello", "the resolved value is shown");
    Check(DevToolsBindingRow_Summary(kSilent).tone == DevToolsBindingTone::Warning, "a type mismatch is a warning, not a break");
    Check(DevToolsBindingRow_Summary(kNotBound).tone == DevToolsBindingTone::NotBound, "an unbound property says so");
    Check(DevToolsBindingRow_Summary(kUnavailable).tone == DevToolsBindingTone::Unknown, "no answer is unknown, never works");
    Check(DevToolsBindingRow_Summary(L"not json").tone == DevToolsBindingTone::Unknown, "garbage is unknown");
}

static void Test_LiveEditOverridesAnXBind()
{
    std::printf("a live edit over an x:Bind is reported as an override, not as working\n");
    const std::wstring json =
        LR"({"state":"evaluated","resolvedValue":"Start focusing","path":"FocusLabel","source":"MainWindow","kind":"{x:Bind}"})";
    auto shown = DevToolsBindingRow_Summary(json);
    Check(!DevToolsBindingRow_ApplyLiveOverride(shown, false, L"go") && shown.tone == DevToolsBindingTone::Works,
          "without a live edit the binding's own answer stands");
    Check(DevToolsBindingRow_ApplyLiveOverride(shown, true, L"go") && shown.tone == DevToolsBindingTone::Warning,
          "a live edit that shows a different value overrides the binding");
    CheckEqW(shown.status, L"Overridden by a live edit", "the status says so");
    auto same = DevToolsBindingRow_Summary(json);
    Check(!DevToolsBindingRow_ApplyLiveOverride(same, true, L"Start focusing"),
          "once the binding shows its own value again, it works");
    DevToolsBindingRow_NoteLiveWrite(42, L"Content");
    Check(DevToolsBindingRow_WasWrittenLive(42, L"Content") && !DevToolsBindingRow_WasWrittenLive(43, L"Content"),
          "live edits are remembered per element and property");
    DevToolsBindingRow_ForgetLiveWrite(42, L"Content");
    Check(!DevToolsBindingRow_WasWrittenLive(42, L"Content"), "a restore forgets the edit");
    CheckEqW(DevToolsBindingRow_SpokenStatus(L"\u2713 Works"), L"Works", "a screen reader hears the status, not the glyph");
    CheckEqW(DevToolsBindingRow_SpokenStatus(L"\u2715 Broken at Nope"), L"Broken at Nope", "including a broken one");
    const auto unanswered = DevToolsBindingRow_Summary(kUnavailable);
    Check(!DevToolsBindingRow_ShowsReplaced(unanswered, false, false),
          "an unanswered diagnosis (no managed agent) is not evidence that a binding was replaced");
    Check(DevToolsBindingRow_ShowsReplaced(unanswered, false, true) && DevToolsBindingRow_ShowsReplaced(unanswered, true, false),
          "our own edit of a bound property is");
    Check(DevToolsBindingRow_ShowsReplaced(DevToolsBindingRow_Summary(kNotBound), false, false),
          "so is the runtime reporting no binding where the XAML declares one");
    Check(!DevToolsBindingRow_ShowsReplaced(DevToolsBindingRow_Summary(kHealthyXBind), false, true),
          "a binding that still works is not replaced (an overridden x:Bind is reported separately)");
}

int RunBindingRowTests()
{
    std::printf("DevToolsBindingRow tests -- captured answers and scoped evaluation contracts\n");
    Test_FailingSegmentIsTheHeadline();
    Test_ThrowsNameTheThrower();
    Test_NoDataContextSaysSo();
    Test_TheSilentCaseAssertsNoCause();
    Test_UndiagnosableSaysSoRatherThanNothing();
    Test_EvaluationDoesNotClaimFreshness();
    Test_ScopeWarningsCannotBeHiddenByNativeFallback();
    Test_SummaryIsAFewLabelledLines();
    Test_LiveEditOverridesAnXBind();
    return g_failures;
}
