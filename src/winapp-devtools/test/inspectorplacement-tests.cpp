// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Tests for where the DevTools inspector window opens, including reopening at the size and spot it was closed.

#include "DevToolsInspectorPlacement.h"

#include <cstdio>
#include "DevToolsTestCheck.h"

using namespace DevToolsInspectorPlacement;

static bool Same(const Rect& a, const Rect& b)
{
    return a.left == b.left && a.top == b.top && a.right == b.right && a.bottom == b.bottom;
}

static const Rect kWork{ 0, 0, 2560, 1400 };
static const Rect kApp{ 100, 100, 1100, 900 };

static void TestFirstOpenUsesTheDefaultSizeBesideTheApp()
{
    const Rect r = Target(144, kWork, &kApp, nullptr, false);
    CheckEq(r.right - r.left, 1170, "first open is 780 DIPs wide at 150%");
    CheckEq(r.bottom - r.top, 930, "first open is 620 DIPs tall at 150%");
    CheckEq(r.left, kApp.right + 18, "first open sits a gap to the right of the app");
    CheckEq(r.top, kApp.top, "first open lines up with the app's top");
}

static void TestReopenReturnsToTheClosedRect()
{
    const Remembered last{ { 1500, 200, 2400, 1300 }, 96, { 7, 0, 7, 7 } };
    Check(Same(Target(96, kWork, &kApp, &last, true), last.rect),
          "reopening returns to the exact size and spot the window was closed at");
}

static void TestReopenKeepsTheSizeInDipsAcrossADpiChange()
{
    const Remembered last{ { 1000, 100, 1900, 700 }, 96, { 0, 0, 0, 0 } };
    const Rect r = Target(144, kWork, &kApp, &last, true);
    CheckEq(r.right - r.left, 1350, "a 900 px window closed at 100% reopens 1350 px wide at 150%");
    CheckEq(r.bottom - r.top, 900, "and 900 px tall");
    Check(r.left == 1000 && r.top == 100, "it reopens at the same spot");
}

static void TestMissingMonitorKeepsTheSizeBesideTheApp()
{
    // The remembered spot was on a monitor that was unplugged; the caller passes restorePosition=false.
    const Remembered last{ { -2000, 100, -1000, 900 }, 96, { 0, 0, 0, 0 } };
    const Rect r = Target(96, kWork, &kApp, &last, false);
    CheckEq(r.right - r.left, 1000, "the remembered width is kept");
    CheckEq(r.bottom - r.top, 800, "the remembered height is kept");
    CheckEq(r.left, kApp.right + 12, "the window opens beside the app instead of off-screen");
    CheckEq(r.top, kApp.top, "lined up with the app's top");
}

static void TestRestoreIsClampedOnScreen()
{
    // Partly past the right and bottom edges of the work area, e.g. after the taskbar grew.
    const Remembered last{ { 2000, 1000, 2900, 1600 }, 96, { 7, 0, 7, 7 } };
    const Rect r = Target(96, kWork, &kApp, &last, true);
    Check(r.right - r.left == 900 && r.bottom - r.top == 600, "a clamped window keeps its size");
    Check(r.right == kWork.right + 7 && r.bottom == kWork.bottom + 7,
          "it moves only far enough for its visible edge to meet the work area");

    // Flush with the left edge: the invisible resize border hangs past the screen and must not push the window in.
    const Remembered flush{ { -7, 0, 893, 600 }, 96, { 7, 0, 7, 7 } };
    Check(Same(Target(96, kWork, &kApp, &flush, true), flush.rect),
          "a window flush with the screen edge reopens flush with it");

    // Bigger than a smaller monitor it now opens on.
    const Remembered big{ { 0, 0, 3000, 1600 }, 96, { 0, 0, 0, 0 } };
    const Rect fit = Target(96, kWork, &kApp, &big, true);
    Check(fit.left >= kWork.left && fit.top >= kWork.top && fit.right <= kWork.right && fit.bottom <= kWork.bottom,
          "a window larger than the work area shrinks to fit");
}

static void TestMaximizedCloseKeepsTheEdgeAlignedSpot()
{
    // Flush with the left edge: the visible window starts at x=0, its invisible 7 px border hangs past the screen.
    const Rect restored{ -7, 0, 893, 600 };
    const NormalFrame normal{ { 7, 0, 7, 7 }, 96 };
    const Rect live = normal.insets;
    const Remembered closedNormal = OnClose(restored, 96, &live, normal);
    Check(Same(Target(96, kWork, &kApp, &closedNormal, true), restored),
          "closed in its normal state, the window reopens flush with the screen edge");
    // Maximized when closed: no live border reading, so the one from the normal state is used.
    const Remembered maximized = OnClose(restored, 96, nullptr, normal);
    Check(Same(Target(96, kWork, &kApp, &maximized, true), restored),
          "closed while maximized, the window reopens at its restored spot, still flush with the edge");
    const Remembered scaled = OnClose(restored, 144, nullptr, normal);
    Check(scaled.frame.left == 11 && scaled.frame.bottom == 11,
          "a border measured at 100% is scaled to the DPI the window closed at");
    // Spanning the whole work area plus its borders: it keeps its full size.
    const Rect full{ -7, 0, kWork.right + 7, kWork.bottom + 7 };
    const Remembered closedFull = OnClose(full, 96, nullptr, normal);
    Check(Same(Target(96, kWork, &kApp, &closedFull, true), full),
          "a window as large as the work area reopens without shrinking");
}

static void TestMinimumSizeStillApplies()
{
    const Remembered tiny{ { 100, 100, 300, 200 }, 96, { 0, 0, 0, 0 } };
    const Rect r = Target(120, kWork, &kApp, &tiny, true);
    CheckEq(r.right - r.left, 800, "the minimum width (640 DIPs) applies at 125%");
    CheckEq(r.bottom - r.top, 500, "the minimum height (400 DIPs) applies at 125%");
}

int RunInspectorPlacementTests()
{
    std::printf("\nDevToolsInspectorPlacement tests\n");
    TestFirstOpenUsesTheDefaultSizeBesideTheApp();
    TestReopenReturnsToTheClosedRect();
    TestReopenKeepsTheSizeInDipsAcrossADpiChange();
    TestMissingMonitorKeepsTheSizeBesideTheApp();
    TestRestoreIsClampedOnScreen();
    TestMaximizedCloseKeepsTheEdgeAlignedSpot();
    TestMinimumSizeStillApplies();
    return g_testFailures;
}
