// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Tests for DevToolsPathProbe's PURE half: path splitting, and the wire shape the renderer parses.
//
// THE POINT OF THIS FILE IS THE ROUND TRIP. DevToolsPathProbe produces the JSON and DevToolsPathWalk consumes it, and
// they live in different processes' concerns -- one in the app's tap, one in the pane. Testing each against
// a hand-written string proves only that each is self-consistent with a string a human typed. So every case
// here builds a DevToolsProbeResult, serializes it, parses it back through the SHIPPING renderer, and asserts on
// what a developer would actually read.
//
// That is what makes the missing-vs-null distinction testable end to end without an app: if either half
// collapses the two, the round trip says so.

#include "DevToolsPathProbe.h"
#include "DevToolsPathWalk.h"

#include <cstdio>
#include <string>
#include <vector>

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

static bool Has(const std::wstring& hay, const wchar_t* needle)
{
    return hay.find(needle) != std::wstring::npos;
}

// The fixture's XBindBroken, as the probe would record it: Vm resolves, Nested exists and is null, Title is
// never attempted.
static DevToolsProbeResult NullLinkResult()
{
    DevToolsProbeResult r;
    r.path = L"Vm.Nested.Title";
    r.against = L"MainWindow";
    r.againstKind = L"xbind";
    DevToolsProbeSegment a; a.path = L"Vm";              a.found = true;  a.hr = 0; a.isNull = false;
    a.type = L"winui_binding_fixture.FixtureViewModel";
    DevToolsProbeSegment b; b.path = L"Vm.Nested";       b.found = true;  b.hr = 0; b.isNull = true;
    DevToolsProbeSegment c; c.path = L"Vm.Nested.Title"; c.reached = false;
    r.segments = { a, b, c };
    return r;
}

// The fixture's BindBroken: the name is not on the type at all.
static DevToolsProbeResult MissingResult()
{
    DevToolsProbeResult r;
    r.path = L"NoSuchPropertyAtAll";
    r.against = L"winui_binding_fixture.FixtureViewModel";
    r.againstKind = L"binding";
    DevToolsProbeSegment a; a.path = L"NoSuchPropertyAtAll"; a.found = false;
    r.segments = { a };
    return r;
}

// The fixture's ThrowingGetter: the property exists and getting it raises.
static DevToolsProbeResult ThrewResult()
{
    DevToolsProbeResult r;
    r.path = L"Explodes";
    r.against = L"winui_binding_fixture.FixtureViewModel";
    r.againstKind = L"binding";
    DevToolsProbeSegment a; a.path = L"Explodes"; a.found = true; a.hr = (long)0x80131604;
    r.segments = { a };
    return r;
}

static void Test_PathSplitting()
{
    std::printf("a path is split into segments, and a malformed one is REFUSED rather than repaired\n");

    std::vector<std::wstring> segs;
    Check(DevToolsPathProbe_SplitPath(L"Vm.Nested.Title", &segs), "a well-formed path splits");
    Check(segs.size() == 3, "into one segment per step");
    CheckEqW(segs[0], L"Vm", "first");
    CheckEqW(segs[2], L"Title", "last");

    Check(DevToolsPathProbe_SplitPath(L"Title", &segs) && segs.size() == 1, "a single-step path is a path");

    // Repairing these would answer a question the user did not ask, and then show them the path they DID
    // type next to the answer -- which is worse than refusing.
    for (const wchar_t* bad : { L"", L".Title", L"Vm.", L"Vm..Title", L"Vm .Title", L"Vm.Nested Title" }) {
        std::vector<std::wstring> junk;
        Check(!DevToolsPathProbe_SplitPath(bad, &junk), "a malformed path is refused");
    }
}

static void Test_TheRoundTripKeepsMissingAndNullApart()
{
    std::printf("THE ROUND TRIP: missing and null survive serialization as different answers\n");

    const std::wstring missJson = DevToolsPathProbe_ToJson(MissingResult());
    const std::wstring nulJson  = DevToolsPathProbe_ToJson(NullLinkResult());

    // On the wire first: the two must be different SHAPES, not two spellings of one blank.
    Check(Has(missJson, L"\"found\":false"), "a missing property serializes found:false");
    Check(Has(nulJson,  L"\"isNull\":true"), "a null value serializes isNull:true");
    Check(Has(nulJson,  L"\"found\":true"),  "and ALSO found:true -- it exists, it is just null");
    Check(Has(nulJson,  L"\"hr\":\"0x0\""),  "with a SUCCEEDED get, because a null value is the answer");

    // Then through the shipping renderer, which is what a developer actually reads.
    DevToolsPathWalkView miss = DevToolsPathWalk_FromJson(missJson, false);
    DevToolsPathWalkView nul  = DevToolsPathWalk_FromJson(nulJson,  false);
    Check(miss.show && nul.show, "both render");
    Check(miss.steps[0].outcome == DevToolsWalkOutcome::Missing, "the missing one survives as Missing");
    Check(nul.steps[1].outcome  == DevToolsWalkOutcome::Null,    "the null one survives as Null");
    Check(miss.answer != nul.answer, "and they read as different sentences");

    // The specific collapse this whole design exists to prevent.
    Check(nul.steps[1].outcome != DevToolsWalkOutcome::Missing, "a null value never arrives as a missing property");
    Check(miss.steps[0].outcome != DevToolsWalkOutcome::Null,   "a missing property never arrives as a null value");
}

static void Test_UnreachedSegmentsCarryNothingElse()
{
    std::printf("a segment past the stop says only that it was not reached\n");

    const std::wstring json = DevToolsPathProbe_ToJson(NullLinkResult());
    // A default `found:true` riding along with `reached:false` would assert that the property exists, which
    // the walk never established -- it stopped before asking.
    // rfind, not find: the same string appears as the top-level `path` field, and matching that one made
    // this assertion read a slice of the header instead of the segment it is about.
    const size_t at = json.rfind(L"\"Vm.Nested.Title\"");
    Check(at != std::wstring::npos, "the unreached segment is still on the wire");
    const std::wstring tail = json.substr(at, 60);
    Check(Has(tail, L"\"reached\":false"), "and is marked as not reached");
    Check(!Has(tail, L"\"found\""), "and claims nothing about whether it exists");

    DevToolsPathWalkView v = DevToolsPathWalk_FromJson(json, false);
    Check(v.steps.size() == 3, "the renderer draws all three rows");
    Check(v.steps[2].outcome == DevToolsWalkOutcome::NotReached, "the third as never reached");
    Check(v.steps[1].stop, "and marks the null one as the stop");
    CheckEqW(v.qEmphasis, L"Nested", "emphasising the segment that stopped it");
}

static void Test_AThrowSurvivesAsAThrow()
{
    std::printf("a thrown getter round-trips as a throw, and carries no exception\n");

    const std::wstring json = DevToolsPathProbe_ToJson(ThrewResult());
    Check(Has(json, L"\"hr\":\"0x80131604\""), "COR_E_TARGETINVOCATION is on the wire verbatim");
    Check(Has(json, L"\"found\":true"), "and the property is reported as EXISTING -- only the get failed");

    // The probe must not carry the outer message forward. It is present in the app, non-empty, and useless.
    Check(!Has(json, L"target of an invocation"), "the useless outer message is not serialized");
    Check(!Has(json, L"InvalidOperationException"), "and no exception type is invented");

    DevToolsPathWalkView v = DevToolsPathWalk_FromJson(json, false);
    Check(v.steps[0].outcome == DevToolsWalkOutcome::Threw, "it renders as a throw");
    Check(!v.namesException, "naming no exception");
    Check(v.revealException, "and offering the disclosure that says how to see one");
}

static void Test_HealthyRoundTripsToSilence()
{
    std::printf("THE CONTROL: a fully resolving walk round-trips to NOTHING\n");

    DevToolsProbeResult r;
    r.path = L"Title";
    r.against = L"winui_binding_fixture.FixtureViewModel";
    r.againstKind = L"binding";
    DevToolsProbeSegment a; a.path = L"Title"; a.found = true; a.hr = 0; a.isNull = false;
    a.value = L"bound title"; a.type = L"String";
    r.segments = { a };

    const std::wstring json = DevToolsPathProbe_ToJson(r);
    // The successful segment states its facts explicitly rather than by omission: a consumer must never have
    // to infer "not null" from an absent field.
    Check(Has(json, L"\"isNull\":false"), "a resolved segment says isNull:false out loud");
    Check(Has(json, L"\"hr\":\"0x0\""),   "and that its get succeeded");

    DevToolsPathWalkView v = DevToolsPathWalk_FromJson(json, false);
    Check(!v.show, "and the renderer draws nothing at all");
    Check(v.answer.empty(), "with no text a row could accidentally show");
}

static void Test_TheWindowCaseRefusesRatherThanGuessing()
{
    std::printf("an unreachable {x:Bind} source is REFUSED, never silently retried against the DataContext\n");

    // An unreachable Window-rooted x:Bind source must not be substituted with DataContext;
    // that would probe a different object and falsely report a missing property.
    DevToolsProbeResult r;
    r.path = L"Vm.Nested.Title";
    r.againstKind = L"xbind";
    r.state = L"no-xbind-source";
    r.reason = L"an {x:Bind} resolves against the page's own generated code, and this app's is a Window, "
               L"which is not in the visual tree";

    const std::wstring json = DevToolsPathProbe_ToJson(r);
    Check(Has(json, L"no-xbind-source"), "the state says the source was not reachable");
    Check(!Has(json, L"\"segments\":[{"), "and NOTHING was probed");

    DevToolsPathWalkView v = DevToolsPathWalk_FromJson(json, false);
    Check(v.show, "the row still says something -- silence would read as a clean bill of health");
    Check(!Has(v.answer, L"no such property"), "and it does not accuse any segment of not existing");
}

int RunPathProbeTests()
{
    std::printf("DevToolsPathProbe tests -- the wire shape, round-tripped through the shipping renderer\n");
    Test_PathSplitting();
    Test_TheRoundTripKeepsMissingAndNullApart();
    Test_UnreachedSegmentsCarryNothingElse();
    Test_AThrowSurvivesAsAThrow();
    Test_HealthyRoundTripsToSilence();
    Test_TheWindowCaseRefusesRatherThanGuessing();
    return g_failures;
}
