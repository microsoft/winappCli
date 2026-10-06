// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Tests for the comment flyout placement used by the in-app DevTools overlay.

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

    // An app-drawn title bar: a comment on the root content must not put its marker over the Close button.
    const Rect client{ 0, 0, 1120, 800 };
    const Rect closeButton{ 1074, 0, 1120, 32 };
    const Rect root{ 0, 0, 1120, 800 };
    const auto uncovered = PlacePin(root, client);
    Check(uncovered.x + 20 > closeButton.left && uncovered.y < closeButton.bottom,
          "control: the whole client lets a root marker sit over the caption buttons");
    const auto below = PlacePin(root, PinViewport(client.right, client.bottom, 32));
    Check(below.y >= 32, "a marker stays below the title bar the app draws");
    Check(PinViewport(100, 20, 32).top == 20 && PinViewport(100, 20, -4).top == 0,
          "the title bar inset is clamped to the client");
}

static void TestPanelKeepsClearOfTheToolbar()
{
    std::printf("panel keeps clear of the DevTools toolbar\n");
    constexpr int panelWidth = 400;
    const Rect workArea{ 0, 0, 1920, 1040 };
    const Rect toolbar{ 1500, 960, 1752, 996 };      // bar and pill union in the window's bottom-right corner
    const Rect selected{ 1640, 930, 1760, 954 };     // text just above the pill
    const Placement old = PlacePanel(selected, workArea, panelWidth, 520);
    Check(Overlaps(Rect{ old.panelX, old.panelY, old.panelX + panelWidth, old.panelY + old.panelHeight }, toolbar),
          "control: without the toolbar rect the panel covers the pill");
    const Placement placed = PlacePanel(selected, workArea, panelWidth, 520, 14, 8, &toolbar);
    const Rect panel{ placed.panelX, placed.panelY, placed.panelX + panelWidth, placed.panelY + placed.panelHeight };
    Check(!Overlaps(panel, toolbar), "panel does not overlap the toolbar");
    Check(!Overlaps(panel, selected), "panel does not cover the selected element");
    Check(placed.panelHeight == 520, "panel keeps its height when there is room above the toolbar");
    Check(panel.top >= workArea.top && panel.bottom <= workArea.bottom, "panel stays in the work area");

    const Rect shortArea{ 0, 0, 1000, 700 };
    const Rect wideBar{ 0, 600, 1000, 636 };         // toolbar spanning the whole width
    const Placement squeezed = PlacePanel({ 300, 560, 360, 590 }, shortArea, panelWidth, 900, 14, 8, &wideBar);
    const Rect squeezedPanel{ squeezed.panelX, squeezed.panelY, squeezed.panelX + panelWidth, squeezed.panelY + squeezed.panelHeight };
    Check(!Overlaps(squeezedPanel, wideBar) && squeezed.panelHeight >= 200,
          "panel shortens to the band above the toolbar when it cannot move around it");

    // A small window high on the screen: the element is at its top-left, the toolbar in its bottom-right corner.
    const Rect screen{ -200, -40, 1400, 900 };
    const Rect cornerBar{ 252, 316, 504, 352 };
    const Rect heading{ 24, 24, 64, 44 };
    const Placement beside = PlacePanel(heading, screen, panelWidth, 544, 14, 8, &cornerBar);
    const Rect besidePanel{ beside.panelX, beside.panelY, beside.panelX + panelWidth, beside.panelY + beside.panelHeight };
    Check(!Overlaps(besidePanel, cornerBar), "panel beside a top-left element avoids a bottom-right toolbar");
    Check(beside.panelX == heading.right + 14 && beside.panelY == 14,
          "panel stays beside the element and ends above the toolbar");
}

int RunSelectionPlacementTests()
{
    std::printf("\nDevToolsSelectionPlacement tests\n");
    TestPanelCanUseSpaceOutsideTheAppWindow();
    TestPanelFlipsAtTheMonitorEdge();
    TestTallPanelIsShiftedAndCapped();
    TestPanelKeepsClearOfTheToolbar();
    TestPinsAvoidTheirElementWhenThereIsRoom();
    return g_selectionPlacementFailures;
}
