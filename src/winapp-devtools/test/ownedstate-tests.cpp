// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Tests for DevToolsOwnedState.h -- the compare-and-restore ownership contract that decides which pieces of the
// PROCESS-GLOBAL DevTools UI state a disconnecting (or target-switching) client is allowed to undo.
//
// Every case carries a POSITIVE CONTROL for the failure it exists to prevent, because both failure modes are
// invisible in a build log and only reproduce with two clients on one app:
//
// * "blanket reset on disconnect" -- the naive implementation. It tears down a second client's live pick,
// highlight, layout adorners or inspector window, which reads to that client's user as the app randomly
// dropping their selection.
// * "never reset" -- the other naive implementation. It leaves a highlight and an armed pick catcher painted
// on an app after the client that asked for them is gone, which reads as the app being stuck in DevTools.
//
// Build/run: scripts/test-native-units.ps1 (also invoked by src/winapp-devtools/build-devtools.ps1).

#include "DevToolsOwnedState.h"

#include <cstdio>
#include <string>

namespace {

int g_ownedStateFailures = 0;

void Check(bool condition, const char* message)
{
    if (condition) std::printf("  ok    %s\n", message);
    else { ++g_ownedStateFailures; std::printf("  FAIL  %s\n", message); }
}

constexpr unsigned long long kClientA = 7;
constexpr unsigned long long kClientB = 9;

bool Released(const DevToolsOwnedReleaseOutcome& outcome, DevToolsStateAxis axis, unsigned long long* restoreTo = nullptr)
{
    for (const DevToolsOwnedRestore& restore : outcome.released) {
        if (restore.axis == axis) {
            if (restoreTo) *restoreTo = restore.restoreTo;
            return true;
        }
    }
    return false;
}

bool Retained(const DevToolsOwnedReleaseOutcome& outcome, DevToolsStateAxis axis)
{
    for (DevToolsStateAxis retained : outcome.retained) if (retained == axis) return true;
    return false;
}

// The wire axis name, narrowed for printf-style assertion messages. Axis names are ASCII by construction
// (they are protocol identifiers), so the narrowing is lossless.
std::string DevToolsStateAxisNameUtf8(DevToolsStateAxis axis)
{
    const wchar_t* wide = DevToolsStateAxisName(axis);
    std::string out;
    for (; *wide; ++wide) out.push_back(static_cast<char>(*wide));
    return out;
}

void TestAClientUndoesOnlyWhatItEstablished()
{
    std::printf("A release restores the axes the caller established and nothing else\n");

    DevToolsOwnedState state;
    state.RecordSet(DevToolsStateAxis::Toolbar, kClientA, /*prior*/ 0, /*now*/ 1);
    state.RecordSet(DevToolsStateAxis::Highlight, kClientA, /*prior*/ 0, /*now*/ 4242);
    state.RecordSet(DevToolsStateAxis::LayoutAdorners, kClientB, /*prior*/ 0, /*now*/ 1);

    // Positive control: the blanket reset this type replaces, run against the SAME state. It knows only
    // "some axes are set", so undoing on A's behalf clears B's adorners too -- the bug, reproduced.
    int blanketCleared = 0;
    for (int i = 0; i < (int)DevToolsStateAxis::Count; ++i) {
        const DevToolsStateAxis axis = static_cast<DevToolsStateAxis>(i);
        if (state.Owner(axis) != 0) ++blanketCleared;   // a blanket reset clears every set axis, whoever set it
    }
    Check(blanketCleared == 3,
          "control: a blanket reset on A's behalf would clear all three axes, including the one B established");

    const DevToolsOwnedReleaseOutcome outcome = state.Release(kClientA);
    unsigned long long restoreTo = 1;
    Check(Released(outcome, DevToolsStateAxis::Toolbar, &restoreTo) && restoreTo == 0,
          "the toolbar it turned on is restored to the value it found (off)");
    restoreTo = 1;
    Check(Released(outcome, DevToolsStateAxis::Highlight, &restoreTo) && restoreTo == 0,
          "the highlight it set is restored to nothing highlighted");
    Check(outcome.released.size() == 2, "only the two axes this client established are restored");
    Check(!Released(outcome, DevToolsStateAxis::LayoutAdorners),
          "the axis the other client established is not in the restore list");
    Check(Retained(outcome, DevToolsStateAxis::LayoutAdorners),
          "it is reported retained instead, so the caller can say why it was left");
    Check(!Released(outcome, DevToolsStateAxis::PickArm) && !Retained(outcome, DevToolsStateAxis::PickArm),
          "an axis nobody touched is neither restored nor reported as another client's");
    Check(state.Owner(DevToolsStateAxis::Toolbar) == 0, "a released axis has no owner afterwards");
    Check(state.Owner(DevToolsStateAxis::LayoutAdorners) == kClientB, "the other client keeps its axis");
}

void TestANewerClientKeepsTheAxis()
{
    std::printf("A newer writer takes the axis, and the older client's release leaves it alone\n");

    DevToolsOwnedState state;
    state.RecordSet(DevToolsStateAxis::Highlight, kClientA, /*prior*/ 0, /*now*/ 100);
    Check(state.Owner(DevToolsStateAxis::Highlight) == kClientA, "the first client owns the highlight it set");

    state.RecordSet(DevToolsStateAxis::Highlight, kClientB, /*prior*/ 100, /*now*/ 200);
    Check(state.Owner(DevToolsStateAxis::Highlight) == kClientB, "the second client's write takes ownership");

    const DevToolsOwnedReleaseOutcome first = state.Release(kClientA);
    Check(!Released(first, DevToolsStateAxis::Highlight),
          "the first client's release does NOT stamp over the newer highlight");
    Check(Retained(first, DevToolsStateAxis::Highlight),
          "the axis is reported retained so the client can say why it did not restore");
    Check(state.Owner(DevToolsStateAxis::Highlight) == kClientB, "ownership stays with the newer writer");

    unsigned long long restoreTo = 0;
    const DevToolsOwnedReleaseOutcome second = state.Release(kClientB);
    Check(Released(second, DevToolsStateAxis::Highlight, &restoreTo) && restoreTo == 100,
          "the newer writer restores to what IT found, not to neutral");
}

void TestAnUnattributedChangeDisownsTheAxis()
{
    std::printf("A change from the app's own surfaces disowns the axis\n");

    DevToolsOwnedState state;
    state.RecordSet(DevToolsStateAxis::PickArm, kClientA, /*prior*/ 0, /*now*/ 1);

    // The rail's own Pick button, the inspector window's one-shot completion, or Esc: the value moves with no
    // protocol write behind it. Every state re-read folds the observed value back in through Reconcile.
    Check(state.Reconcile(DevToolsStateAxis::PickArm, 0), "an unattributed change is reported as an ownership move");
    Check(state.Owner(DevToolsStateAxis::PickArm) == 0, "the axis is disowned once someone else moved it");

    const DevToolsOwnedReleaseOutcome outcome = state.Release(kClientA);
    Check(!Released(outcome, DevToolsStateAxis::PickArm),
          "the client does not re-arm a catcher the user already dismissed");
    Check(!Retained(outcome, DevToolsStateAxis::PickArm),
          "a disowned axis belongs to nobody, so it is not reported as another client's either");
}

void TestReconcileIsSilentWhileTheOwnerAgrees()
{
    std::printf("Reconcile only moves ownership when the observed value actually disagrees\n");

    DevToolsOwnedState state;
    state.RecordSet(DevToolsStateAxis::LayoutAdorners, kClientA, /*prior*/ 0, /*now*/ 1);
    const unsigned long long revision = state.Revision();

    Check(!state.Reconcile(DevToolsStateAxis::LayoutAdorners, 1), "re-observing the owner's own value moves nothing");
    Check(state.Revision() == revision, "an agreeing reconcile does not bump the revision");
    Check(state.Owner(DevToolsStateAxis::LayoutAdorners) == kClientA, "the owner keeps the axis");
}

void TestReassertingAValueDoesNotAcquireAnAxis()
{
    std::printf("Asking for the value already in effect does not take the axis from its owner\n");

    DevToolsOwnedState state;
    state.RecordSet(DevToolsStateAxis::Toolbar, kClientA, /*prior*/ 0, /*now*/ 1);

    // B calls Overlay.show while the rail is already visible. Nothing changed, so B has established nothing;
    // treating that as acquisition would let a read-shaped call silently steal an axis and, worse, hand B a
    // "prior" of `visible` -- so B's release would leave the rail on forever.
    state.RecordSet(DevToolsStateAxis::Toolbar, kClientB, /*prior*/ 1, /*now*/ 1);
    Check(state.Owner(DevToolsStateAxis::Toolbar) == kClientA, "a no-op write does not transfer ownership");

    unsigned long long restoreTo = 1;
    const DevToolsOwnedReleaseOutcome outcome = state.Release(kClientA);
    Check(Released(outcome, DevToolsStateAxis::Toolbar, &restoreTo) && restoreTo == 0,
          "the original owner still restores the rail to hidden");
}

void TestRepeatedWritesRestoreToTheOriginalPriorValue()
{
    std::printf("Toggling an axis repeatedly still restores to what the owner first found\n");

    DevToolsOwnedState state;
    state.RecordSet(DevToolsStateAxis::Highlight, kClientA, /*prior*/ 55, /*now*/ 100);
    state.RecordSet(DevToolsStateAxis::Highlight, kClientA, /*prior*/ 100, /*now*/ 200);
    state.RecordSet(DevToolsStateAxis::Highlight, kClientA, /*prior*/ 200, /*now*/ 300);

    unsigned long long restoreTo = 0;
    const DevToolsOwnedReleaseOutcome outcome = state.Release(kClientA);
    Check(Released(outcome, DevToolsStateAxis::Highlight, &restoreTo) && restoreTo == 55,
          "the restore target is the value in effect before the FIRST write, not the previous one");
}

void TestToggleTwiceRestoresToTheOriginalValue()
{
    std::printf("Toggling an axis on and then off again leaves what the owner first found\n");

    // Positive control: the ordering bug this pins. The overlay's state-changed sink fires SYNCHRONOUSLY from
    // inside the mutation, i.e. before RecordSet runs, so a tap that let it reconcile there folded the NEW
    // value in first. That disowns the axis mid-write, and the RecordSet that follows re-acquires it with the
    // PREVIOUS value as its prior -- so a client that shows the rail and then hides it again would, on
    // release, SHOW a rail the user had hidden before it ever connected.
    {
        DevToolsOwnedState control;
        control.RecordSet(DevToolsStateAxis::Toolbar, kClientA, /*prior*/ 0, /*now*/ 1);
        control.Reconcile(DevToolsStateAxis::Toolbar, 0);                     // the sink, running before RecordSet
        control.RecordSet(DevToolsStateAxis::Toolbar, kClientA, /*prior*/ 1, /*now*/ 0);
        unsigned long long controlRestore = 0;
        const DevToolsOwnedReleaseOutcome controlOutcome = control.Release(kClientA);
        Check(Released(controlOutcome, DevToolsStateAxis::Toolbar, &controlRestore) && controlRestore == 1,
              "control: reconciling before RecordSet restores the rail to VISIBLE, which is not what was found");
    }

    // The shipping order: the mutation is recorded, and only then is the observed value folded in. The second
    // write returns the rail to the hidden state the client found, so the axis is disowned on the spot -- there
    // is nothing left to restore -- and the value stands at what was found either way.
    DevToolsOwnedState state;
    state.RecordSet(DevToolsStateAxis::Toolbar, kClientA, /*prior*/ 0, /*now*/ 1);
    state.Reconcile(DevToolsStateAxis::Toolbar, 1);
    state.RecordSet(DevToolsStateAxis::Toolbar, kClientA, /*prior*/ 1, /*now*/ 0);
    state.Reconcile(DevToolsStateAxis::Toolbar, 0);
    Check(state.Owner(DevToolsStateAxis::Toolbar) == 0,
          "returning the rail to what was found drops the claim rather than holding an empty one");

    const DevToolsOwnedReleaseOutcome outcome = state.Release(kClientA);
    Check(!Released(outcome, DevToolsStateAxis::Toolbar),
          "release has nothing to restore, and in particular does not re-show the rail");
    Check(!Retained(outcome, DevToolsStateAxis::Toolbar), "and does not report the axis as somebody else's");
}

// Restoring an axis to its original value must release ownership in the same operation,
// so state reports and subsequent acquisitions do not retain a stale owner.
void TestReturningAnAxisToItsFoundValueDisownsIt()
{
    std::printf("An owner that returns an axis to the value it found no longer owns it\n");

    struct AxisCase { DevToolsStateAxis axis; unsigned long long found; unsigned long long changed; };
    const AxisCase cases[] = {
        { DevToolsStateAxis::Toolbar,        0,  1    },   // rail hidden -> shown -> hidden
        { DevToolsStateAxis::Highlight,      0,  4242 },   // nothing highlighted -> a handle -> nothing
        { DevToolsStateAxis::LayoutAdorners, 0,  1    },   // adorners off -> on -> off
        { DevToolsStateAxis::PickArm,        0,  1    },   // pick disarmed -> armed -> disarmed
        { DevToolsStateAxis::Window,         0,  1    },   // inspector window closed -> open -> closed
    };

    for (const AxisCase& item : cases) {
        const std::string name = std::string("  axis ") + DevToolsStateAxisNameUtf8(item.axis);

        DevToolsOwnedState state;
        state.RecordSet(item.axis, kClientA, item.found, item.changed);
        const unsigned long long ownedRevision = state.Revision();
        Check(state.Owner(item.axis) == kClientA, (name + ": the first write acquires the axis").c_str());

        state.RecordSet(item.axis, kClientA, item.changed, item.found);
        Check(state.Owner(item.axis) == 0, (name + ": returning it to the found value disowns it").c_str());
        Check(state.Revision() > ownedRevision,
              (name + ": the revision advances, so the unowned state is observable").c_str());

        //... and the axis is genuinely free: another client can take it, and does so from the right prior.
        state.RecordSet(item.axis, kClientB, item.found, item.changed);
        Check(state.Owner(item.axis) == kClientB, (name + ": a second client can then acquire it").c_str());
        unsigned long long restoreTo = 12345;
        const DevToolsOwnedReleaseOutcome outcome = state.Release(kClientB);
        Check(Released(outcome, item.axis, &restoreTo) && restoreTo == item.found,
              (name + ": and B's release restores to the value A had already put back").c_str());

        // A, which tidied up, has nothing left to release and cannot disturb anyone.
        const DevToolsOwnedReleaseOutcome late = state.Release(kClientA);
        Check(late.released.empty(), (name + ": A's later disconnect restores nothing").c_str());
    }
}

// Disconnect restoration must still be correct for the owner that did NOT return the axis -- the fix above
// must not turn "release restores what I changed" into "release restores nothing".
void TestPartialTidyUpStillRestoresTheRest()
{
    std::printf("Tidying one axis does not weaken the restore of the others\n");

    DevToolsOwnedState state;
    state.RecordSet(DevToolsStateAxis::Toolbar, kClientA, /*prior*/ 0, /*now*/ 1);
    state.RecordSet(DevToolsStateAxis::Highlight, kClientA, /*prior*/ 0, /*now*/ 77);
    state.RecordSet(DevToolsStateAxis::Window, kClientA, /*prior*/ 0, /*now*/ 1);

    state.RecordSet(DevToolsStateAxis::Window, kClientA, /*prior*/ 1, /*now*/ 0);   // A closes the window it opened
    Check(state.Owner(DevToolsStateAxis::Window) == 0, "the tidied axis is disowned");
    Check(state.Owner(DevToolsStateAxis::Toolbar) == kClientA, "the untouched rail is still A's");
    Check(state.Owner(DevToolsStateAxis::Highlight) == kClientA, "the untouched highlight is still A's");

    unsigned long long toolbarRestore = 9;
    unsigned long long highlightRestore = 9;
    const DevToolsOwnedReleaseOutcome outcome = state.Release(kClientA);
    Check(Released(outcome, DevToolsStateAxis::Toolbar, &toolbarRestore) && toolbarRestore == 0,
          "disconnect still hides the rail A raised");
    Check(Released(outcome, DevToolsStateAxis::Highlight, &highlightRestore) && highlightRestore == 0,
          "disconnect still clears the highlight A set");
    Check(!Released(outcome, DevToolsStateAxis::Window), "and does not re-open the window A already closed");
}

void TestReleaseReportsTheValueItExpectsToFind()
{
    std::printf("A release states the value it expects, so the applier can refuse a stale restore\n");

    // Release() is bookkeeping; applying the restores is a separate UI-thread turn. Between them another
    // client can move the axis, and the restore would then stamp over a value this owner never saw. Carrying
    // the owner's OWN last value lets the applier compare and skip -- which is the disconnect-vs-user-reopened
    // -window case in DevToolsTap's ApplyOwnedRelease_ui.
    DevToolsOwnedState state;
    state.RecordSet(DevToolsStateAxis::Window, kClientA, /*prior*/ 0, /*now*/ 1);

    const DevToolsOwnedReleaseOutcome outcome = state.Release(kClientA);
    bool found = false;
    for (const DevToolsOwnedRestore& restore : outcome.released) {
        if (restore.axis != DevToolsStateAxis::Window) continue;
        found = true;
        Check(restore.restoreTo == 0, "the window is restored to closed, which is how the client found it");
        Check(restore.expectedValue == 1, "and the release states it expects to find the window still open");
    }
    Check(found, "the window axis is in the release list");
}

void TestTwoClientsOnDifferentAxesDoNotDisturbEachOther()
{
    std::printf("Two clients holding different axes release independently\n");

    // The cross-axis case the release ORDER exists for: A owns the pick catcher, B owns the selection. A's
    // release must end up touching only the catcher -- if tearing it down took the selection with it (which is
    // exactly what DevToolsOverlay_DisarmPick does, and why the release uses CompleteOneShotPick), B would watch its
    // selection vanish because a different client disconnected.
    DevToolsOwnedState state;
    state.RecordSet(DevToolsStateAxis::PickArm, kClientA, /*prior*/ 0, /*now*/ 1);
    state.RecordSet(DevToolsStateAxis::Highlight, kClientB, /*prior*/ 0, /*now*/ 4242);

    const DevToolsOwnedReleaseOutcome first = state.Release(kClientA);
    Check(Released(first, DevToolsStateAxis::PickArm), "the arming client hands its catcher back");
    Check(!Released(first, DevToolsStateAxis::Highlight), "and does not hand back the other client's selection");
    Check(Retained(first, DevToolsStateAxis::Highlight),
          "the other client's selection is reported retained, so the caller can say why it was left");
    Check(state.Owner(DevToolsStateAxis::Highlight) == kClientB, "the selection is still the second client's");

    unsigned long long restoreTo = 1;
    const DevToolsOwnedReleaseOutcome second = state.Release(kClientB);
    Check(Released(second, DevToolsStateAxis::Highlight, &restoreTo) && restoreTo == 0,
          "and the second client can still restore it afterwards");
}

void TestSameOwnerHoldingBothAxesRestoresBoth()
{
    std::printf("One client holding both the catcher and the selection restores both\n");

    // Same-owner cross-axis: A armed the pick AND selected an element. Both axes are its own, so both are in
    // the release -- the ORDER in which the caller applies them is what decides whether the restored highlight
    // survives the catcher teardown (DevToolsTap restores the highlight last for exactly this reason).
    DevToolsOwnedState state;
    state.RecordSet(DevToolsStateAxis::PickArm, kClientA, /*prior*/ 0, /*now*/ 1);
    state.RecordSet(DevToolsStateAxis::Highlight, kClientA, /*prior*/ 77, /*now*/ 4242);

    const DevToolsOwnedReleaseOutcome outcome = state.Release(kClientA);
    unsigned long long highlightRestore = 0;
    Check(Released(outcome, DevToolsStateAxis::PickArm), "the catcher is released");
    Check(Released(outcome, DevToolsStateAxis::Highlight, &highlightRestore) && highlightRestore == 77,
          "and the selection goes back to the element that was selected before this client touched it");
    Check(outcome.retained.empty(), "nothing is left owned by anyone");
}

void TestRevisionIsMonotonicAndAxisNamesAreStable()
{
    std::printf("The revision only moves forward, and axis names are the wire spelling\n");

    DevToolsOwnedState state;
    Check(state.Revision() == 0, "an untouched process starts at revision 0");
    state.RecordSet(DevToolsStateAxis::Window, kClientA, 0, 1);
    const unsigned long long afterSet = state.Revision();
    Check(afterSet > 0, "establishing an axis bumps the revision");
    state.Reconcile(DevToolsStateAxis::Window, 1);
    Check(state.Revision() == afterSet, "an agreeing observation does not bump it");
    state.Release(kClientA);
    Check(state.Revision() > afterSet, "a release that restored something bumps it again");

    Check(std::wstring(DevToolsStateAxisName(DevToolsStateAxis::Toolbar)) == L"overlayToolbar", "toolbar axis name");
    Check(std::wstring(DevToolsStateAxisName(DevToolsStateAxis::Highlight)) == L"selection", "selection axis name");
    Check(std::wstring(DevToolsStateAxisName(DevToolsStateAxis::LayoutAdorners)) == L"layoutAdorners", "layout axis name");
    Check(std::wstring(DevToolsStateAxisName(DevToolsStateAxis::PickArm)) == L"pickArm", "pick-arm axis name");
    Check(std::wstring(DevToolsStateAxisName(DevToolsStateAxis::Window)) == L"inspectorWindow", "window axis name");
    Check(state.OwnerToken(DevToolsStateAxis::Window).empty(), "an unowned axis reports an empty owner token");
    state.RecordSet(DevToolsStateAxis::Window, kClientB, 0, 1);
    Check(state.OwnerToken(DevToolsStateAxis::Window) == L"9", "an owned axis reports the decimal connection id");
}

void TestTheAppItselfCanNeverOwnAnAxis()
{
    std::printf("Connection id 0 (the app's own surfaces) never acquires an axis\n");

    DevToolsOwnedState state;
    state.RecordSet(DevToolsStateAxis::Toolbar, /*owner*/ 0, 0, 1);
    Check(state.Owner(DevToolsStateAxis::Toolbar) == 0, "a write with no connection behind it establishes no owner");

    const DevToolsOwnedReleaseOutcome outcome = state.Release(0);
    Check(outcome.released.empty(), "releasing on behalf of nobody restores nothing");
    Check(outcome.retained.empty(), "and reports nothing retained");
}

} // namespace

int RunOwnedStateTests()
{
    std::printf("Process-global state ownership tests (DevToolsOwnedState)\n");
    TestAClientUndoesOnlyWhatItEstablished();
    TestANewerClientKeepsTheAxis();
    TestAnUnattributedChangeDisownsTheAxis();
    TestReconcileIsSilentWhileTheOwnerAgrees();
    TestReassertingAValueDoesNotAcquireAnAxis();
    TestRepeatedWritesRestoreToTheOriginalPriorValue();
    TestToggleTwiceRestoresToTheOriginalValue();
    TestReturningAnAxisToItsFoundValueDisownsIt();
    TestPartialTidyUpStillRestoresTheRest();
    TestReleaseReportsTheValueItExpectsToFind();
    TestTwoClientsOnDifferentAxesDoNotDisturbEachOther();
    TestSameOwnerHoldingBothAxesRestoresBoth();
    TestRevisionIsMonotonicAndAxisNamesAreStable();
    TestTheAppItselfCanNeverOwnAnAxis();
    return g_ownedStateFailures;
}
