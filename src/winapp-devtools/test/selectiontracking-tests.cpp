// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "DevToolsSelectionTracking.h"

#include <cstdio>

using namespace DevToolsSelection;

static int g_selectionTrackingFailures = 0;

static void Check(bool cond, const char* what)
{
    if (!cond) { ++g_selectionTrackingFailures; std::printf("  FAIL  %s\n", what); }
    else       {                                std::printf("  ok    %s\n", what); }
}

static TrackingRect TrackPreFix(const ScrollAnchor& anchor, double, double)
{
    return anchor.rect;
}

static void TestScrollMovesTheRect()
{
    std::printf("scroll offsets move the tracked rect\n");
    const ScrollAnchor anchor{ { 100, 200, 180, 240 }, 20.0, 40.0 };

    const TrackingRect old = TrackPreFix(anchor, 50.0, 160.0);
    Check(old.left == 100 && old.top == 200,
          "control: the pre-fix rect stays at its selection-time coordinates");

    const TrackingRect tracked = TrackScroll(anchor, 50.0, 160.0);
    Check(tracked.left == 70 && tracked.right == 150, "horizontal scrolling moves the rect");
    Check(tracked.top == 80 && tracked.bottom == 120, "vertical scrolling moves the rect");
}

static void TestAbsoluteOffsetsDoNotAccumulateRounding()
{
    std::printf("absolute offsets avoid cumulative rounding drift\n");
    const ScrollAnchor anchor{ { 10, 10, 30, 30 }, 0.0, 0.0 };
    const TrackingRect first = TrackScroll(anchor, 0.0, 0.49);
    const TrackingRect second = TrackScroll(anchor, 0.0, 0.98);
    Check(first.top == 10, "a sub-pixel move stays at the nearest DIP");
    Check(second.top == 9, "two absolute sub-pixel reports become one DIP, not zero");
}

static void TestViewportIntersection()
{
    std::printf("viewport intersection controls chrome visibility\n");
    Check(IntersectsViewport({ -10, 10, 10, 30 }, 100, 100),
          "a partially clipped rect remains visible");
    Check(!IntersectsViewport({ 100, 10, 120, 30 }, 100, 100),
          "a rect beyond the right edge is hidden");
    Check(!IntersectsViewport({ 10, -30, 30, 0 }, 100, 100),
          "a rect above the viewport is hidden");
}

int RunSelectionTrackingTests()
{
    std::printf("\nDevToolsSelectionTracking tests\n");
    TestScrollMovesTheRect();
    TestAbsoluteOffsetsDoNotAccumulateRounding();
    TestViewportIntersection();
    return g_selectionTrackingFailures;
}
