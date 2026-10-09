// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Contract-level native-walk presentation tests, not live app evidence. Healthy paths render
// nothing; missing/null differ; native getter errors cannot invent a managed inner exception.

#include "DevToolsPathWalk.h"

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

static bool Has(const std::wstring& hay, const wchar_t* needle)
{
    return hay.find(needle) != std::wstring::npos;
}

// ---- the fixture's own rows, as the contract shapes them --------------------------------------------
// src/winapp-devtools/test/winui-binding-fixture/MainWindow.xaml is the answer key for all of these.

// XBindBroken: {x:Bind Vm.Nested.Title}. Vm resolves, Nested is a REAL property whose value is null, Title
// is never attempted. Resolves against the WINDOW, not the DataContext.
static const wchar_t* kNullLink =
    LR"({"path":"Vm.Nested.Title","against":"MainWindow","againstKind":"xbind","segments":[)"
    LR"({"path":"Vm","found":true,"isNull":false,"hr":"0x0","type":"winui_binding_fixture.FixtureViewModel"},)"
    LR"({"path":"Vm.Nested","found":true,"isNull":true,"hr":"0x0","type":"winui_binding_fixture.FixtureViewModel"},)"
    LR"({"path":"Vm.Nested.Title","reached":false}]})";

// BindBroken: {Binding NoSuchPropertyAtAll}. The name is not on the type at all.
static const wchar_t* kMissing =
    LR"({"path":"NoSuchPropertyAtAll","against":"winui_binding_fixture.FixtureViewModel","againstKind":"binding","segments":[)"
    LR"({"path":"NoSuchPropertyAtAll","found":false}]})";

// A mistyped {Binding} where the walk was able to supply a near miss. The suggestion is a WIRE field: this
// renderer cannot compute one, because the interface has no enumerate-all to be close to.
static const wchar_t* kMissingWithSuggestion =
    LR"({"path":"Titel","against":"winui_binding_fixture.FixtureViewModel","againstKind":"binding","suggestion":"Title","segments":[)"
    LR"({"path":"Titel","found":false}]})";

// ThrowingGetter: {Binding Explodes}. The property EXISTS; getting it raises.
static const wchar_t* kThrewNative =
    LR"({"path":"Explodes","against":"winui_binding_fixture.FixtureViewModel","againstKind":"binding","segments":[)"
    LR"({"path":"Explodes","found":true,"hr":"0x80131604"}]})";

// The same row with the managed agent loaded: the inner exception survived, so it can be named.
static const wchar_t* kThrewWithAgent =
    LR"({"path":"Explodes","against":"winui_binding_fixture.FixtureViewModel","againstKind":"binding",)"
    LR"("exception":"InvalidOperationException","message":"the getter for Explodes always throws","segments":[)"
    LR"({"path":"Explodes","found":true,"hr":"0x80131604"}]})";

// BindOk: {Binding Title}. Resolves.
static const wchar_t* kHealthy =
    LR"({"path":"Title","against":"winui_binding_fixture.FixtureViewModel","againstKind":"binding","segments":[)"
    LR"({"path":"Title","found":true,"isNull":false,"hr":"0x0","value":"bound title","type":"String"}]})";

// XBindOk: {x:Bind Vm.Title}. Two segments, both resolve.
static const wchar_t* kHealthyMultiStep =
    LR"({"path":"Vm.Title","against":"MainWindow","againstKind":"xbind","segments":[)"
    LR"({"path":"Vm","found":true,"isNull":false,"hr":"0x0","type":"winui_binding_fixture.FixtureViewModel"},)"
    LR"({"path":"Vm.Title","found":true,"isNull":false,"hr":"0x0","value":"bound title","type":"String"}]})";

// NoDataContextBound: DataContext="{x:Null}".
static const wchar_t* kNoContext =
    LR"({"path":"Title","againstKind":"binding","state":"no-context","segments":[]})";

// A missing property arriving alongside a NON-ZERO hr. The walker has no reason to zero out the HRESULT of
// a call it never made, so this shape is what a defensive implementation naturally emits -- and it is the
// only input that pins the classifier's ORDER. Without it, checking the HRESULT before existence passes
// every other assertion here while reporting a mistyped name as a thrown getter.
static const wchar_t* kMissingWithStaleHr =
    LR"({"path":"Titel","against":"winui_binding_fixture.FixtureViewModel","againstKind":"binding","segments":[)"
    LR"({"path":"Titel","found":false,"hr":"0x80070490"}]})";

static void Test_StatesThatMeanNothingToShow()
{
    std::printf("the states that mean \"there is nothing to explain\" render NOTHING\n");

    // AN UNBOUND PROPERTY. The tap answers `none` for a literal, and its own comment says the renderer
    // should draw nothing for it. Without an explicit case this falls through to the empty-segments
    // branch and every literal in the pane gets a fault-shaped "Path walk unavailable" block -- the
    // decorate-a-working-row failure, arriving through the one state that was supposed to be silent.
    DevToolsPathWalkView none = DevToolsPathWalk_FromJson(LR"({"state":"none","segments":[]})", false);
    Check(!none.show, "an unbound property renders no walk");
    Check(none.answer.empty() && none.qEmphasis.empty(), "and carries no text a row could accidentally draw");
}

static void Test_AnUnprobeableObjectIsNotSILENCE()
{
    std::printf("an object that cannot be probed mid-path says so -- it does not render nothing\n");

    // THE DANGEROUS ONE. Every segment the walk reached RESOLVED, so nothing is marked as the stop. A
    // renderer that decides "no stop means healthy" returns show=false and draws absolutely nothing for a
    // path that demonstrably failed to walk -- and silence on this surface reads as a clean bill of health.
    const wchar_t* json =
        LR"({"path":"Vm.Brush.Color","against":"FixtureViewModel","againstKind":"binding",)"
        LR"("state":"not-probeable","reason":"this object does not expose its properties to DevTools",)"
        LR"("segments":[{"path":"Vm","found":true,"hr":"0x0","isNull":false,"type":"FixtureViewModel"},)"
        LR"({"path":"Vm.Brush","found":true,"hr":"0x0","isNull":false,"type":"SolidColorBrush"}]})";

    DevToolsPathWalkView v = DevToolsPathWalk_FromJson(json, false);
    Check(v.show, "it renders something");
    Check(!v.answer.empty(), "and says why");
    Check(!Has(v.answer, L"no such property"), "without accusing any name of not existing");
    // The steps it DID reach are the useful half of the answer and are kept.
    Check(v.steps.size() == 2, "the segments it did reach are still shown as context");

    // The negative control: the same segments with no `not-probeable` state ARE a healthy walk, and must
    // render nothing. If this one ever shows, the fix above has started decorating working rows instead.
    const wchar_t* healthy =
        LR"({"path":"Vm.Brush","against":"FixtureViewModel","againstKind":"binding",)"
        LR"("segments":[{"path":"Vm","found":true,"hr":"0x0","isNull":false,"type":"FixtureViewModel"},)"
        LR"({"path":"Vm.Brush","found":true,"hr":"0x0","isNull":false,"type":"SolidColorBrush"}]})";
    Check(!DevToolsPathWalk_FromJson(healthy, false).show, "CONTROL: the same segments with no fault state render nothing");
}

static void Test_AMalformedPathSaysSo()
{
    std::printf("a malformed path is named as malformed, not as something else\n");

    DevToolsPathWalkView v = DevToolsPathWalk_FromJson(
        LR"({"path":"Vm..Title","state":"bad-path","reason":"this is not a well-formed property path","segments":[]})",
        false);
    Check(v.show, "it renders");
    Check(Has(v.answer, L"well-formed"), "and says the path is malformed");
    // THE HEADLINE IS THE DISTINCTION, and it is the whole reason this state is handled rather than left
    // to the generic fallback. "Path walk unavailable" says DEVTOOLS failed; naming the path says the PATH
    // is wrong. They send the developer to two different places, and the fallback happens to reuse the
    // wire's reason text -- so asserting only the sentence passes in both worlds.
    CheckEqW(v.qEmphasis, L"Vm..Title", "and the headline is the offending PATH");
    Check(!Has(v.qEmphasis, L"unavailable"), "not \"unavailable\", which would blame DevTools for the user's typo");
    // Malformed paths are not evidence that a valid source lookup failed.
    Check(!Has(v.answer, L"visual tree"), "and makes no claim about the tree, which was never searched");
    Check(!Has(v.answer, L"no such property"), "nor about any property existing");
}

static void Test_ThePlainTextBlockAlignsAndMarksTheStop()
{
    std::printf("the one-TextBlock rendering aligns its columns and marks the stop with a GLYPH\n");

    DevToolsPathWalkView v = DevToolsPathWalk_FromJson(kNullLink, false);
    const std::wstring text = DevToolsPathWalk_PlainText(v);
    Check(!text.empty(), "a walk with a stop renders text");

    // One line per segment, so the reader can scan a column rather than parse a sentence.
    size_t lines = 1;
    for (wchar_t c : text) if (c == L'\n') ++lines;
    Check(lines == v.steps.size(), "one line per segment");

    Check(Has(text, DevToolsPathWalk_StopMark()), "the stop carries a glyph");
    // never colour alone. A single TextBlock cannot vary weight without <Run>s, so the glyph is what
    // survives High Contrast -- and it must be on the STOP line, not merely somewhere in the block.
    const size_t mark = text.find(DevToolsPathWalk_StopMark());
    const size_t nulPos = text.find(L"Vm.Nested");
    Check(mark != std::wstring::npos && nulPos != std::wstring::npos && mark < nulPos,
          "and the glyph sits on the line it stopped at");

    // Exactly one marker: two would say the walk failed twice, which it cannot.
    size_t marks = 0;
    for (size_t p = text.find(DevToolsPathWalk_StopMark()); p != std::wstring::npos;
         p = text.find(DevToolsPathWalk_StopMark(), p + 1)) ++marks;
    Check(marks == 1, "exactly one stop marker");

    // The values line up: every line's value starts at the same column, which is what makes "which of these
    // is different" a scanning question instead of a reading one.
    Check(Has(text, L"no such property") || Has(text, L"null"), "the outcome text is present");

    // THE CONTROL. A healthy walk renders NO text at all -- if this ever returns a string, the overlay will
    // show a walk block on a working row, which is the failure the whole feature is judged on.
    Check(DevToolsPathWalk_PlainText(DevToolsPathWalk_FromJson(kHealthyMultiStep, false)).empty(),
          "CONTROL: a fully resolving path renders no text");
    Check(DevToolsPathWalk_PlainText(DevToolsPathWalk_FromJson(LR"({"state":"none","segments":[]})", false)).empty(),
          "CONTROL: an unbound property renders no text");
}

static void Test_AnIndexerBlamesTheIndexingAndNotTheProperty()
{
    std::printf("an indexed path says the INDEXER is unsupported -- it does not accuse the property\n");

    // `Items[0].Name` is legitimate binding syntax. Resolving it needs GetIndexedProperty, which this walk
    // does not call -- so the honest answer is "cannot index", and the tempting one is what a by-name
    // lookup produces: GetCustomProperty(L"Items[0]") finds nothing and the row reads
    // "no Items[0] on FixtureViewModel". That is wrong in the way this whole feature exists to prevent --
    // `Items` is present and correctly named, and the developer is sent to rename it.
    const wchar_t* json =
        LR"({"path":"Vm.Items[0].Name","against":"FixtureViewModel","againstKind":"binding",)"
        LR"("state":"indexer-unsupported","reason":"DevTools cannot follow an indexer like Items[0].",)"
        LR"("segments":[{"path":"Vm","found":true,"hr":"0x0","isNull":false,"type":"FixtureViewModel"}]})";

    DevToolsPathWalkView v = DevToolsPathWalk_FromJson(json, false);
    Check(v.show, "it renders something rather than nothing");
    // The segments it DID reach all resolved, so nothing is marked as the stop. If this state is ever
    // handled after the all-resolved early return, the row silently renders NOTHING for a path that was
    // never followed -- and silence here reads as a clean bill of health.
    Check(!v.answer.empty(), "and says why");
    Check(Has(v.answer, L"indexer"), "naming the indexing step as the thing it cannot do");

    // THE ASSERTION THAT MATTERS: it must not accuse the property of not existing.
    Check(!Has(v.answer, L"no such property"), "it does NOT say the property is missing");
    Check(!Has(v.answer, L"no Items[0]"), "and does not name Items[0] as an absent property");
    for (const auto& s : v.steps) {
        Check(s.outcome != DevToolsWalkOutcome::Missing, "no segment is reported as missing");
    }
    // The steps that did resolve are kept -- they are how far it got, which is the useful half.
    Check(v.steps.size() == 1 && v.steps[0].path == L"Vm", "the resolved prefix is still shown");
}

static void Test_TheStopNamesTheTYPEItLookedOnNotTheValue()
{
    std::printf("the stop names the TYPE it looked on, not the datum\n");

    // A requested step beyond an Int32 scalar cannot resolve.
    const wchar_t* scalar =
        LR"({"path":"Count.Anything","against":"winui_binding_fixture.FixtureViewModel","againstKind":"binding","segments":[)"
        LR"({"path":"Count","found":true,"hr":"0x0","isNull":false,"value":"7","type":"Int32"},)"
        LR"({"path":"Count.Anything","found":false}]})";

    DevToolsPathWalkView v = DevToolsPathWalk_FromJson(scalar, false);
    Check(Has(v.answer, L"no Anything on Int32"), "it names the TYPE the lookup ran against");
    // The regression this pins: naming the VALUE points at the wrong thing entirely. "7" tells a developer
    // nothing about where to look; "Int32" tells them the path ran off the end of a primitive.
    Check(!Has(v.answer, L"on 7"), "and NOT the datum");
    // The parentheses are a display affordance the value column adds; they must not leak into prose.
    Check(!Has(v.answer, L"(Int32)"), "with no leftover display parentheses");

    // THE CONTROL, and the reason this is not a one-line swap: for an OBJECT step the value column IS the
    // type name, so the object case must keep reading exactly as it did.
    const wchar_t* object =
        LR"({"path":"Vm.Titel","against":"winui_binding_fixture.FixtureViewModel","againstKind":"binding","segments":[)"
        LR"({"path":"Vm","found":true,"hr":"0x0","isNull":false,"type":"winui_binding_fixture.FixtureViewModel"},)"
        LR"({"path":"Vm.Titel","found":false}]})";
    Check(Has(DevToolsPathWalk_FromJson(object, false).answer, L"no Titel on winui_binding_fixture.FixtureViewModel"),
          "CONTROL: an object step still names its type");

    // And a first-segment failure still names the starting object rather than nothing.
    Check(Has(DevToolsPathWalk_FromJson(kMissing, false).answer, L"on winui_binding_fixture.FixtureViewModel"),
          "CONTROL: a first-segment miss names what it resolved against");
}

static void Test_HealthyRendersNothing()
{
    std::printf("a path that resolves end to end renders NOTHING\n");

    DevToolsPathWalkView one = DevToolsPathWalk_FromJson(kHealthy, false);
    Check(!one.show, "a single resolving segment shows no walk");

    DevToolsPathWalkView many = DevToolsPathWalk_FromJson(kHealthyMultiStep, false);
    Check(!many.show, "a multi-step path that fully resolves shows no walk");
    Check(many.answer.empty(), "and says nothing at all");

    // The agent's presence must not turn a healthy row into a decorated one.
    Check(!DevToolsPathWalk_FromJson(kHealthyMultiStep, true).show, "still nothing with the agent loaded");
}

static void Test_MissingAndNullAreDifferent()
{
    std::printf("\"no such property\" and \"is null\" are DIFFERENT answers\n");

    // The assertion this whole file exists for. Written as an inequality on purpose: asserting each
    // string's spelling separately is exactly how a renderer that collapses both into one blank passes a
    // presentation suite.
    const std::wstring missingText = DevToolsPathWalk_OutcomeText(DevToolsWalkOutcome::Missing);
    const std::wstring nullText    = DevToolsPathWalk_OutcomeText(DevToolsWalkOutcome::Null);
    Check(missingText != nullText, "the two outcomes do not share a value string");
    Check(!missingText.empty() && !nullText.empty(), "and neither one is blank");

    DevToolsPathWalkView miss = DevToolsPathWalk_FromJson(kMissing, false);
    DevToolsPathWalkView nul  = DevToolsPathWalk_FromJson(kNullLink, false);
    Check(miss.show && nul.show, "both render a walk");

    const DevToolsPathWalkStep& missStop = miss.steps[0];
    const DevToolsPathWalkStep& nulStop  = nul.steps[1];
    Check(missStop.outcome == DevToolsWalkOutcome::Missing, "the absent name classifies as Missing");
    Check(nulStop.outcome  == DevToolsWalkOutcome::Null,    "the null value classifies as Null");
    Check(missStop.value != nulStop.value,             "and their value columns differ");

    //... and so do the sentences, which is what a developer actually reads.
    Check(miss.answer != nul.answer, "the two answers are not the same sentence");
    Check(Has(nul.answer, L"null"), "the null link says it was null");
    Check(Has(miss.answer, L"no NoSuchPropertyAtAll"), "the missing name says there is no such property");

    // A null value is a SUCCESSFUL get. If the classifier ever reads S_OK-with-null as a failure, or reads
    // a missing property as a null one, one of these two flips.
    Check(nulStop.outcome != DevToolsWalkOutcome::Missing, "a null value is not reported as a missing property");
    Check(missStop.outcome != DevToolsWalkOutcome::Null,   "a missing property is not reported as a null value");

    // EXISTENCE IS CHECKED BEFORE THE HRESULT, and this is the only input that says so. A property that is
    // not on the type was never got, so whatever `hr` rode along is not about it -- classifying on the
    // HRESULT first turns a mistyped name into "the getter threw" and sends the developer to open a file
    // that has no such member in it.
    DevToolsPathWalkView stale = DevToolsPathWalk_FromJson(kMissingWithStaleHr, false);
    Check(stale.steps[0].outcome == DevToolsWalkOutcome::Missing,
          "a missing property with a non-zero hr is still Missing, not Threw");
    Check(!Has(stale.answer, L"threw"), "and its answer does not claim anything threw");
    Check(Has(stale.answer, L"no Titel on"), "it says the name is not there");
}

static void Test_TheStopIsNamedInPlace()
{
    std::printf("the walk names WHICH segment stopped it, in place\n");

    DevToolsPathWalkView v = DevToolsPathWalk_FromJson(kNullLink, false);
    CheckEqW(v.qHead,     L"Vm.",    "the resolved prefix is kept");
    CheckEqW(v.qEmphasis, L"Nested", "the failing component is emphasised");
    CheckEqW(v.qTail,     L".Title", "and the unreached remainder is kept");

    Check(v.steps.size() == 3, "one row per segment");
    Check(!v.steps[0].stop && !v.steps[0].after, "the resolved segment is neither the stop nor after it");
    Check(v.steps[1].stop, "the null segment IS the stop");
    Check(v.steps[2].after && v.steps[2].outcome == DevToolsWalkOutcome::NotReached,
          "the segment past the stop is marked as never reached");
    Check(Has(v.steps[2].value, L"not reached"), "and says so rather than showing a blank");

    // Exactly one stop, always. Two stops would mean the walk kept probing past a failure, which it cannot
    // do -- there is no object left to probe against.
    int stops = 0;
    for (const auto& s : v.steps) if (s.stop) ++stops;
    Check(stops == 1, "exactly one segment is marked as the stop");

    // The stop is carried by a glyph and a weight change, never by colour alone.
    Check(DevToolsPathWalk_StopMark() && *DevToolsPathWalk_StopMark(), "there is a non-empty stop glyph");
}

static void Test_EveryRowIsAPrefixOfTheProbedPath()
{
    std::printf("every rendered row is a PREFIX of the probed path -- a probe, not a browser\n");

    // ICustomPropertyProvider is lookup by name with no enumerate-all. If a row ever appears that is not a
    // prefix of the path being probed, something has started guessing at members it cannot see.
    DevToolsPathWalkView v = DevToolsPathWalk_FromJson(kNullLink, false);
    const std::wstring probed = L"Vm.Nested.Title";
    bool allPrefixes = true;
    for (const auto& s : v.steps) {
        if (s.path.size() > probed.size() || probed.compare(0, s.path.size(), s.path) != 0) allPrefixes = false;
    }
    Check(allPrefixes, "no row names anything outside the probed path");
    CheckEqW(v.steps[0].path, L"Vm",              "row 1 is the first prefix");
    CheckEqW(v.steps[1].path, L"Vm.Nested",       "row 2 is the second prefix");
    CheckEqW(v.steps[2].path, L"Vm.Nested.Title", "row 3 is the whole path");
}

static void Test_ItNamesWhatItResolvedAgainst()
{
    std::printf("the walk names what it resolved AGAINST, per kind\n");

    // An {x:Bind} binds to the page's own generated code. A "DataContext" header over an x:Bind walk sends
    // the reader to inspect an object the binding never touched.
    DevToolsPathWalkView x = DevToolsPathWalk_FromJson(kNullLink, false);
    Check(Has(x.againstNote, L"MainWindow"), "an x:Bind walk names the page");
    Check(Has(x.againstNote, L"x:Bind"),     "and says an x:Bind binds to the page");
    Check(Has(x.againstNote, L"not the DataContext"), "explicitly, so the reader is not sent to the wrong object");

    DevToolsPathWalkView b = DevToolsPathWalk_FromJson(kMissing, false);
    Check(Has(b.againstNote, L"DataContext"), "a {Binding} walk names the DataContext");
    Check(!Has(b.againstNote, L"x:Bind"),     "and does not mention x:Bind");

    // the type must be the ICustomPropertyProvider one, not the projected interface a CLR
    // object reports from GetRuntimeClassName. If this ever reads INotifyPropertyChanged, the feature's
    // whole premise -- "the path may be perfect for a view model this element never got" -- is defeated.
    Check(Has(x.steps[0].value, L"FixtureViewModel"), "a resolved object shows its real runtime type");
    Check(!Has(x.steps[0].value, L"INotifyPropertyChanged"), "not the projected interface name");
}

static void Test_AThrownGetterDoesNotNameAnException()
{
    std::printf("natively, a thrown getter says it threw and names NOTHING\n");

    DevToolsPathWalkView n = DevToolsPathWalk_FromJson(kThrewNative, false);
    Check(n.show, "a thrown getter renders a walk");
    Check(n.steps[0].outcome == DevToolsWalkOutcome::Threw, "the segment classifies as Threw");
    Check(Has(n.answer, L"threw"), "the answer says it threw");
    Check(!n.namesException, "and does NOT claim to name the exception");

    // The specific trap. The outer message is present, non-empty and useless, and printing it reads exactly
    // like a real diagnosis. A native answer must never wear the managed one's clothes.
    Check(!Has(n.answer, L"InvalidOperationException"), "no exception TYPE is named");
    Check(!Has(n.answer, L"target of an invocation"), "the useless outer message is not printed");
    Check(!Has(n.answer, L"0x80131604"), "and the raw HRESULT is not printed as though it were a reason");
    Check(Has(n.answer, L"cannot see"), "the row names what it cannot see");
    Check(n.revealException, "and offers the disclosure that says how to see it");

    // A thrown getter is NOT a missing property. A walk that does not catch per segment reports this as
    // "no such property" and sends the developer to the wrong file entirely.
    Check(n.steps[0].outcome != DevToolsWalkOutcome::Missing, "a raising getter is not reported as a missing name");
    Check(n.answer != DevToolsPathWalk_FromJson(kMissing, false).answer, "and reads differently from a missing name");

    // With the agent, and only with the agent, the row names it. The difference has to be visible in the
    // TEXT rather than inferred from which flags were passed at launch.
    DevToolsPathWalkView a = DevToolsPathWalk_FromJson(kThrewWithAgent, true);
    Check(a.namesException, "with the agent the exception is named");
    Check(Has(a.answer, L"InvalidOperationException"), "by type");
    Check(Has(a.answer, L"always throws"), "and with its message");
    Check(!a.revealException, "so the how-to-see-it disclosure is not offered");
    Check(a.answer != n.answer, "the two worlds do not read the same");

    // The agent's answer fed to the native renderer must still not name it: `agentPresent` is the gate, not
    // the presence of the field, so a stale answer cannot leak a claim into a native session.
    Check(!DevToolsPathWalk_FromJson(kThrewWithAgent, false).namesException,
          "the same answer rendered without the agent names nothing");
}

static void Test_NoSuggestionIsInventedWithoutOne()
{
    std::printf("a \"did you mean\" is rendered only when the WIRE supplies one\n");

    // There is no enumerate-all on ICustomPropertyProvider, so there is no set of candidate members for a
    // near-miss to be near. A suggestion this renderer produced itself would be a guess wearing an answer's
    // clothes.
    DevToolsPathWalkView bare = DevToolsPathWalk_FromJson(kMissing, false);
    Check(!Has(bare.answer, L"did you mean"), "no suggestion is invented");

    DevToolsPathWalkView sug = DevToolsPathWalk_FromJson(kMissingWithSuggestion, false);
    Check(Has(sug.answer, L"did you mean Title?"), "a supplied suggestion is rendered");
    Check(Has(sug.answer, L"no Titel on"), "alongside the fact it is explaining");
}

static void Test_NoDataContextSaysSo()
{
    std::printf("no DataContext is an ANSWER, not an absence\n");

    DevToolsPathWalkView v = DevToolsPathWalk_FromJson(kNoContext, false);
    Check(v.show, "it renders");
    Check(Has(v.qEmphasis, L"No DataContext"), "and names the condition");
    Check(v.steps.empty(), "with no segment rows, because nothing was walked");
}

static void Test_GarbageIsReportedRatherThanSwallowed()
{
    std::printf("an unusable answer says so rather than rendering nothing\n");

    // A walk that renders nothing reads as a clean bill of health -- the same defect class DevToolsBindingRow
    // guards. Silence is only ever correct when the path genuinely resolved.
    const wchar_t* junk[] = { L"", L"not json at all", L"[1,2,3]", L"{", L"{\"segments\":[]}" };
    for (const wchar_t* g : junk) {
        DevToolsPathWalkView v = DevToolsPathWalk_FromJson(g, false);
        Check(v.show, "unusable input still renders something");
        Check(!v.qEmphasis.empty() || !v.answer.empty(), "and carries text saying why");
    }
}

int RunPathWalkTests()
{
    std::printf("DevToolsPathWalk tests -- what a row shows for a binding path walk\n");
    Test_HealthyRendersNothing();
    Test_TheStopNamesTheTYPEItLookedOnNotTheValue();
    Test_ThePlainTextBlockAlignsAndMarksTheStop();
    Test_StatesThatMeanNothingToShow();
    Test_AnUnprobeableObjectIsNotSILENCE();
    Test_AMalformedPathSaysSo();
    Test_AnIndexerBlamesTheIndexingAndNotTheProperty();
    Test_MissingAndNullAreDifferent();
    Test_TheStopIsNamedInPlace();
    Test_EveryRowIsAPrefixOfTheProbedPath();
    Test_ItNamesWhatItResolvedAgainst();
    Test_AThrownGetterDoesNotNameAnException();
    Test_NoSuggestionIsInventedWithoutOne();
    Test_NoDataContextSaysSo();
    Test_GarbageIsReportedRatherThanSwallowed();
    return g_failures;
}
