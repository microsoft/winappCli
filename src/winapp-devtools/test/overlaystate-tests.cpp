// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Tests for the synchronized selection-arm / overlay state contract's pure logic:
//
// * DevToolsOverlayStateTracker (DevToolsOverlayStateTracker.h) -- the payload-free sink's diff decision, run without
// any COM/XAML/app: does a fresh flat snapshot warrant Selection.armChanged, Overlay.stateChanged, both,
// or neither, relative to the last snapshot broadcast (not merely last observed)?
// * The Overlay domain's DevToolsEvents.cpp subscription bit -- Overlay gained an event domain in
// (Overlay.stateChanged), so it now goes through the SAME enable/disable/subscriber-count/disconnect
// lifecycle every other event domain does; this pins that it does, mirroring
// focus-subscription-tests.cpp's shape for a domain that (unlike Focus) owns no separate activation hook.
//
// Build/run: scripts/test-native-units.ps1 (also invoked by src/winapp-devtools/build-devtools.ps1).

#include "DevToolsOverlayStateTracker.h"
#include "DevToolsEvents.h"

#include <cstdio>

namespace {

int g_overlayStateFailures = 0;

void Check(bool condition, const char* message)
{
    if (condition) std::printf("  ok    %s\n", message);
    else { ++g_overlayStateFailures; std::printf("  FAIL  %s\n", message); }
}

// ---- DevToolsOverlayStateTracker: which event(s) does a snapshot change warrant? ---------------------------

void TestFirstReconcileReportsOnlyWhatActuallyDiffersFromTheDefault()
{
    std::printf("The first reconcile reports exactly the axes that differ from DevToolsOverlay's own defaults\n");
    DevToolsOverlayStateTracker tracker;

    // Positive control: a tracker that always reports "changed" (the bug this type exists to avoid -- a
    // caller that fires Selection.armChanged/Overlay.stateChanged on EVERY payload-free sink call, including
    // ones where nothing moved) would make repeated arm/show/hide non-idempotent on the wire even though the
    // underlying DevToolsOverlay calls already refuse to no-op mutate. This control shows that failure mode is
    // real: a diff-free reporter can't distinguish it from an actual first change.
    struct AlwaysChangedControl { DevToolsOverlayStateDelta Reconcile(const DevToolsOverlayStateSnapshot&) { return { true, true }; } };
    AlwaysChangedControl control;
    Check(control.Reconcile({}).Any(), "control: an always-changed reporter fires even for the untouched default");

    // The tracker itself starts at the same all-default snapshot DevToolsOverlay reports before anything happens
    // (nothing armed, toolbar not yet visible, nothing highlighted, layout adorners off), so reconciling with
    // that exact snapshot first must report nothing.
    DevToolsOverlayStateDelta delta = tracker.Reconcile(DevToolsOverlayStateSnapshot{});
    Check(!delta.Any(), "reconciling the untouched default against itself reports no change");

    // Now a real first change: the overlay finished building (toolbarVisible flips true).
    delta = tracker.Reconcile(DevToolsOverlayStateSnapshot{ false, true, 0, false });
    Check(delta.overlayChanged, "toolbar becoming visible is reported as an overlay change");
    Check(!delta.armedChanged, "an overlay-only change does not also report an arm change");
}

void TestArmAndOverlayAxesAreIndependent()
{
    std::printf("Selection-arm and overlay (toolbar/highlight/layout) axes are independently reported\n");
    DevToolsOverlayStateTracker tracker;
    tracker.Reconcile(DevToolsOverlayStateSnapshot{ false, true, 0, false }); // baseline: toolbar already up

    // Arm only.
    DevToolsOverlayStateDelta delta = tracker.Reconcile(DevToolsOverlayStateSnapshot{ true, true, 0, false });
    Check(delta.armedChanged, "arming pick reports an arm change");
    Check(!delta.overlayChanged, "arming pick alone does not report an overlay change (independent axes)");

    // Highlight only (arm stays true).
    delta = tracker.Reconcile(DevToolsOverlayStateSnapshot{ true, true, 42, false });
    Check(!delta.armedChanged, "a highlight-only change does not re-report the already-reported arm");
    Check(delta.overlayChanged, "a highlight move reports an overlay change");

    // Layout only.
    delta = tracker.Reconcile(DevToolsOverlayStateSnapshot{ true, true, 42, true });
    Check(!delta.armedChanged, "toggling layout adorners does not report an arm change");
    Check(delta.overlayChanged, "toggling layout adorners reports an overlay change");

    // Toolbar hidden only.
    delta = tracker.Reconcile(DevToolsOverlayStateSnapshot{ true, false, 42, true });
    Check(!delta.armedChanged, "hiding the toolbar does not report an arm change");
    Check(delta.overlayChanged, "hiding the toolbar reports an overlay change");
    Check(tracker.Last().layoutAdornersOn, "the recorded snapshot keeps the OTHER axes as reported (layout stays on)");

    // Disarm only, with everything else unchanged.
    delta = tracker.Reconcile(DevToolsOverlayStateSnapshot{ false, false, 42, true });
    Check(delta.armedChanged, "disarming reports an arm change");
    Check(!delta.overlayChanged, "disarming alone does not report an overlay change");
}

void TestRepeatedIdenticalSnapshotsAreIdempotent()
{
    std::printf("Repeated identical snapshots report nothing -- wire-level idempotence\n");
    DevToolsOverlayStateTracker tracker;
    DevToolsOverlayStateSnapshot armed{ true, true, 7, false };
    DevToolsOverlayStateDelta first = tracker.Reconcile(armed);
    Check(first.Any(), "the first arm is a real change");

    // Repeated arm/show/hide must be idempotent (acceptance): a defensive re-arm, or the payload-free
    // sink firing again for an unrelated reason while nothing actually moved, must not re-broadcast.
    DevToolsOverlayStateDelta second = tracker.Reconcile(armed);
    Check(!second.Any(), "reconciling the identical snapshot again reports no change");
    DevToolsOverlayStateDelta third = tracker.Reconcile(armed);
    Check(!third.Any(), "a third identical reconcile still reports no change");
}

void TestMultipleAxesInOneReconcileEachReportIndependently()
{
    std::printf("A single reconcile can report both events at once when both axes moved together\n");
    DevToolsOverlayStateTracker tracker;
    // Baseline: overlay already built and idle (toolbar visible, nothing armed, nothing highlighted, layout
    // off) -- matches the snapshot every reconcile below starts from, so only the axes that ACTUALLY move
    // relative to THIS baseline should be reported.
    tracker.Reconcile(DevToolsOverlayStateSnapshot{ false, true, 0, false });

    // Esc: disarms pick AND (per DevToolsOverlay.cpp's ExitPickMode reconciliation) settles the highlight back to
    // whatever the committed selection is -- if that selection was NOTHING, both axes move in the one event.
    DevToolsOverlayStateDelta delta = tracker.Reconcile(DevToolsOverlayStateSnapshot{ false, true, 0, false });
    Check(!delta.Any(), "no selection existed and nothing was armed -- Esc on an idle overlay changes nothing");

    tracker.Reconcile(DevToolsOverlayStateSnapshot{ true, true, 55, false }); // armed, something highlighted
    delta = tracker.Reconcile(DevToolsOverlayStateSnapshot{ false, true, 0, false }); // Esc: disarm + clear
    Check(delta.armedChanged, "Esc reports the arm change");
    Check(delta.overlayChanged, "Esc reports the overlay change (highlight cleared) in the SAME reconcile");
}

// ---- Overlay domain: same subscription lifecycle every event domain gets --------------------------------

void TestOverlayDomainBitAndLifecycle()
{
    std::printf("The Overlay domain has an event bit and follows the standard subscription lifecycle\n");

    // Positive control: before, Overlay was request-only and DevToolsEvents_DomainBit returned 0 for it (the
    // same answer it still gives for DevTools/HotReload/Source/Resource, which remain request-only). This control
    // shows the OLD answer really was 0, so the assertion below is a real behavior change, not vacuous.
    Check(DevToolsEvents_DomainBit(L"HotReload") == 0, "control: a request-only domain (HotReload) has no event bit");

    Check(DevToolsEvents_DomainBit(L"Overlay") == DevToolsDomain_Overlay,
          "Overlay maps to its own subscription bit for Overlay.stateChanged");

    DevToolsConn* first = DevToolsEvents_Register(INVALID_HANDLE_VALUE);
    DevToolsConn* second = DevToolsEvents_Register(INVALID_HANDLE_VALUE);
    Check(first && second, "two independent connections register");
    if (!first || !second) {
        if (first) DevToolsEvents_Unregister(first);
        if (second) DevToolsEvents_Unregister(second);
        return;
    }

    Check(!DevToolsEvents_DomainEnabled(first, DevToolsDomain_Overlay), "a fresh connection starts unsubscribed from Overlay");
    Check(DevToolsEvents_SetDomain(first, DevToolsDomain_Overlay, true) == DevToolsDomain_FirstEnabled,
          "the first Overlay enable reports the activation edge");
    Check(DevToolsEvents_DomainEnabled(first, DevToolsDomain_Overlay), "the connection now reports itself subscribed");
    Check(DevToolsEvents_DomainSubscriberCount(DevToolsDomain_Overlay) == 1, "the Overlay subscriber count becomes one");

    // Unlike Focus, Overlay owns no separate target hook to start/stop: toolbar/highlight/layout state is
    // always tracked regardless of subscriber count (see DevToolsTap.cpp's HandleRpc, which folds Overlay.enable/
    // disable into the SAME plain bit-toggle block as VisualTree/Property/Selection). So a second subscriber
    // enabling is unremarkable reference-counted bookkeeping, not a "no-op because already active" edge --
    // there is no activation to be idempotent about, unlike Focus's DevToolsDomain_FirstEnabled.
    Check(DevToolsEvents_SetDomain(first, DevToolsDomain_Overlay, true) == DevToolsDomain_NoChange,
          "a repeated enable by the SAME connection is idempotent");
    Check(DevToolsEvents_DomainSubscriberCount(DevToolsDomain_Overlay) == 1, "an idempotent enable does not inflate the count");

    Check(DevToolsEvents_SetDomain(second, DevToolsDomain_Overlay, true) == DevToolsDomain_NoChange,
          "a second connection's enable does not re-report first-activation");
    Check(DevToolsEvents_DomainSubscriberCount(DevToolsDomain_Overlay) == 2, "both connections are reference counted");

    Check(DevToolsEvents_SetDomain(first, DevToolsDomain_Overlay, false) == DevToolsDomain_NoChange,
          "one disable leaves the domain active for the other connection");
    Check(DevToolsEvents_DomainSubscriberCount(DevToolsDomain_Overlay) == 1, "the remaining connection keeps one reference");

    Check((DevToolsEvents_Unregister(first) & DevToolsDomain_Overlay) == 0,
          "disconnecting an already-disabled connection has no Overlay lifecycle edge");
    Check((DevToolsEvents_Unregister(second) & DevToolsDomain_Overlay) != 0,
          "disconnecting the final subscribed connection owns the Overlay cleanup edge");
    Check(DevToolsEvents_DomainSubscriberCount(DevToolsDomain_Overlay) == 0, "disconnect cleanup releases the final reference");
}

} // namespace

int RunOverlayStateTests()
{
    std::printf("Overlay/selection-arm state tests\n");
    TestFirstReconcileReportsOnlyWhatActuallyDiffersFromTheDefault();
    TestArmAndOverlayAxesAreIndependent();
    TestRepeatedIdenticalSnapshotsAreIdempotent();
    TestMultipleAxesInOneReconcileEachReportIndependently();
    TestOverlayDomainBitAndLifecycle();
    return g_overlayStateFailures;
}
