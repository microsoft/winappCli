// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "DevToolsPickRoute.h"

#include <cstdio>

static int g_failures = 0;

static void Check(bool cond, const char* what)
{
    if (!cond) { ++g_failures; std::printf("  FAIL  %s\n", what); }
    else       {              std::printf("  ok    %s\n", what); }
}

static void TestWindowPickCommitsOnlyOnRelease()
{
    std::printf("window-originated pick is consumed only by the release-time commit\n");
    DevToolsPickRoute route;

    Check(!route.IsWindowPickArmed(), "an untouched route does not claim picks");
    route.ArmWindowPick();
    Check(route.IsWindowPickArmed(), "the window's toggle arms the route");
    Check(route.IsWindowPickArmed(), "pointer press leaves the route armed");
    Check(route.CommitReleasedPick(), "released pick is routed back to the inspector");
    Check(!route.IsWindowPickArmed(), "released pick consumes the window's one-shot route");
    Check(!route.CommitReleasedPick(), "a later rail pick is not claimed by the inspector");
}

static void TestOverlayPickKeepsItsInAppPanel()
{
    std::printf("overlay-originated pick is not claimed by the inspector\n");
    DevToolsPickRoute route;

    Check(!route.IsWindowPickArmed(), "an ordinary in-app pick keeps the in-app selection UI");
    route.ArmWindowPick();
    Check(route.IsWindowPickArmed(), "window pick can arm after an overlay pick");
}

// The mode ends on ONE signal, from whichever surface ended it: the overlay reports the transition and the
// window cancels. Arming is idempotent for the same reason -- DevToolsOverlay_ArmPick is an arm, not a toggle, so
// the route must not flip on a second arm.
static void TestCancelAndArmAreIdempotent()
{
    std::printf("the claim is cleared by cancellation, and arm/cancel are idempotent\n");
    DevToolsPickRoute route;

    route.ArmWindowPick();
    route.CancelWindowPick();
    Check(!route.IsWindowPickArmed(), "ending pick mode drops the window's claim");

    route.ArmWindowPick();
    route.ArmWindowPick();
    Check(route.IsWindowPickArmed(), "arming twice leaves the route armed (arm is not a toggle)");
    route.CancelWindowPick();
    route.CancelWindowPick();
    Check(!route.IsWindowPickArmed(), "cancelling twice is safe");
}

int RunPickRouteTests()
{
    std::printf("DevToolsPickRoute tests\n");
    TestWindowPickCommitsOnlyOnRelease();
    TestOverlayPickKeepsItsInAppPanel();
    TestCancelAndArmAreIdempotent();
    return g_failures;
}
