// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Tests for the quick-edit panel placement used by the in-app DevTools overlay.

#include "DevToolsSelectionPlacement.h"

#include <cstdio>

using namespace DevToolsSelection;

static int g_selectionPlacementFailures = 0;

static void Check(bool cond, const char* what)
{
    if (!cond) { ++g_selectionPlacementFailures; std::printf("  FAIL  %s\n", what); }
    else       {                                 std::printf("  ok    %s\n", what); }
}

static Placement PlacePreFix(const Rect& element, const Rect& client, int panelWidth, int estimatedHeight)
{
    const int gap = 14;
    const int roomRight = client.right - element.right - gap;
    const int roomLeft = element.left - gap;
    const bool onRight = roomRight >= roomLeft;
    int x = onRight ? element.right + gap : element.left - panelWidth - gap;
    x = std::max(8, std::min(x, client.right - panelWidth - 8));
    int y = std::max(8, std::min(element.top - 10, client.bottom - estimatedHeight - 30));
    return Placement{ x, y, estimatedHeight, onRight };
}

static void TestPanelCanUseSpaceOutsideTheAppWindow()
{
    std::printf("panel can use monitor space outside a narrow app window\n");
    constexpr int panelWidth = 400;
    const Rect appClient{ 0, 0, 500, 700 };
    const Rect monitorWorkArea{ -300, 0, 1300, 900 };
    const Rect selected{ 420, 220, 480, 260 };

    const Placement old = PlacePreFix(selected, appClient, panelWidth, 340);
    Check(old.panelX + panelWidth <= appClient.right,
          "control: pre-fix placement is clamped inside the app client");

    const Placement placed = PlacePanel(selected, monitorWorkArea, panelWidth, 520);
    Check(placed.onRight, "panel stays beside the selected element on the roomier side");
    Check(placed.panelX >= selected.right, "panel does not cover the selected element");
    Check(placed.panelX + panelWidth > appClient.right,
          "panel extends beyond the app window when the monitor has room");
    Check(placed.panelX + panelWidth <= monitorWorkArea.right,
          "panel remains inside the monitor work area");
}

static void TestPanelFlipsAtTheMonitorEdge()
{
    std::printf("panel flips at the monitor edge\n");
    constexpr int panelWidth = 400;
    const Rect workArea{ 0, 0, 1200, 900 };
    const Rect selected{ 1100, 200, 1180, 260 };
    const Placement placed = PlacePanel(selected, workArea, panelWidth, 520);
    Check(!placed.onRight, "panel flips left when the right side leaves the monitor");
    Check(placed.panelX + panelWidth <= selected.left, "flipped panel remains beside the element");
    Check(placed.panelX >= workArea.left, "flipped panel remains on-screen");
}

static void TestTallPanelIsShiftedAndCapped()
{
    std::printf("tall panel is shifted and capped to the monitor\n");
    constexpr int panelWidth = 400;
    const Rect workArea{ 0, 40, 1200, 700 };
    const Rect selected{ 500, 620, 580, 680 };
    const Placement placed = PlacePanel(selected, workArea, panelWidth, 900);
    Check(placed.panelHeight == 644, "panel height is capped to the work area minus edge gaps");
    Check(placed.panelY == 48, "panel shifts upward instead of clipping at the bottom");
    Check(placed.panelY + placed.panelHeight <= workArea.bottom,
          "capped panel remains inside the work area");
}

static void TestPinsAvoidTheirElementWhenThereIsRoom()
{
    const Rect viewport{ 0, 0, 520, 360 };
    const Rect elements[] = {
        { 162, 163, 166, 182 }, // Narrow label, as observed in the hosted fixture.
        { 516, 163, 520, 182 }, // Flip left at the right edge.
        { 0, 163, 520, 182 },  // Use space above a full-width element.
        { 0, 0, 520, 19 },     // Use space below at the top edge.
        { -8, -8, 4, 19 },
        { 0, 340, 520, 360 },
    };
    for (const auto& element : elements) {
        const auto pin = PlacePin(element, viewport);
        Check(pin.x >= 0 && pin.x + 20 <= viewport.right &&
              pin.y >= 0 && pin.y + 20 <= viewport.bottom, "pin stays inside the client");
        for (const double scale : { 1.0, 1.25, 1.5, 2.0 }) {
            Check((pin.x + 20) * scale <= element.left * scale ||
                  pin.x * scale >= element.right * scale ||
                  (pin.y + 20) * scale <= element.top * scale ||
                  pin.y * scale >= element.bottom * scale,
                  "DIP placement keeps pin separate at 96/120/144/192 DPI");
        }
    }
    const auto narrow = PlacePin(elements[0], viewport);
    Check(narrow.x == 170 && narrow.y == 163, "narrow label keeps its pin nearby on the right");
    const auto full = PlacePin(viewport, viewport);
    Check(full.x == 500 && full.y == 0, "unavoidable overlap stays at the full-client element's edge");
    const auto tiny = PlacePin({ 0, 0, 4, 4 }, { 0, 0, 8, 8 });
    Check(tiny.x == 0 && tiny.y == 0, "client smaller than the marker never produces negative coordinates");
}

int RunSelectionPlacementTests()
{
    std::printf("\nDevToolsSelectionPlacement tests\n");
    TestPanelCanUseSpaceOutsideTheAppWindow();
    TestPanelFlipsAtTheMonitorEdge();
    TestTallPanelIsShiftedAndCapped();
    TestPinsAvoidTheirElementWhenThereIsRoom();
    return g_selectionPlacementFailures;
}
