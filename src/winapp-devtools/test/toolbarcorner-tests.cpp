// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Docking tests compare the real anchor with an intentionally title-bar-blind negative control.

#include "DevToolsToolbarCorner.h"

#include <cstdio>

using namespace DevToolsToolbar;

static int g_failures = 0;

static void Check(bool cond, const char* what)
{
    if (!cond) { ++g_failures; std::printf("  FAIL  %s\n", what); }
    else       {              std::printf("  ok    %s\n", what); }
}

static void CheckEqD(double got, double want, const char* what)
{
    const double d = got - want;
    if (d > 0.001 || d < -0.001) { ++g_failures; std::printf("  FAIL  %s (want %.2f, got %.2f)\n", what, want, got); }
    else                         {              std::printf("  ok    %s (%.2f)\n", what, got); }
}

// Negative control: top corners ignore the app's title-bar inset. Keep this deliberately wrong.
static void AnchorPreFix(int c, const Client& client, double w, double h, double* x, double* y)
{
    *x = CornerIsLeft(c) ? kEdgeGap : (client.w - w - kEdgeGap);
    *y = CornerIsTop(c)  ? kEdgeGap : (client.h - h - kEdgeGap);
}

// 1265x1023 DIP client with a 72 physical-pixel caption at 144 DPI: 48 DIPs of inset.
static Client Gallery()      { return Client{ 1265.0, 1023.0, 48.0 }; }
// An app that does NOT extend into its title bar: its XAML island already begins below the caption, so the
// strip is not in this coordinate space and the inset is 0. samples/winui-unpackaged-app is this shape,
// which is why a green run there proves nothing about the bug above.
static Client NonExtending() { return Client{ 1265.0, 1023.0, 0.0 }; }

static const double kPillW = 104.0, kPillH = 28.0;
static const int kLegacyCornerCount = 4;

static void TestTopCornersClearTheCaption()
{
    std::printf("top corners clear the app's caption strip\n");
    const Client g = Gallery();

    for (int corner = 0; corner < kLegacyCornerCount; ++corner) {
        if (!CornerIsTop(corner)) continue;
        double ox = 0, oy = 0; AnchorPreFix(corner, g, kPillW, kPillH, &ox, &oy);
        // The negative control must land inside the caption strip.
        Check(oy < g.titleBarInsetDip, corner == CornerTL
              ? "control: pre-fix top-LEFT pill top is inside the caption strip"
              : "control: pre-fix top-RIGHT pill top is inside the caption strip");

        double x = 0, y = 0; Anchor(corner, g, kPillW, kPillH, &x, &y);
        Check(y >= g.titleBarInsetDip, corner == CornerTL
              ? "top-LEFT pill top is at or below the caption strip"
              : "top-RIGHT pill top is at or below the caption strip");
    }

    double tlx = 0, tly = 0; Anchor(CornerTL, g, kPillW, kPillH, &tlx, &tly);
    CheckEqD(tlx, 12.0,       "top-left x is the plain edge gap");
    CheckEqD(tly, 48.0 + 12.0, "top-left y is caption + edge gap");

    double trx = 0, tr_y = 0; Anchor(CornerTR, g, kPillW, kPillH, &trx, &tr_y);
    CheckEqD(trx, 1265.0 - 104.0 - 12.0, "top-right x is the client width less the pill and the gap");
    CheckEqD(tr_y, 48.0 + 12.0,          "top-right y is caption + edge gap");

    double tcx = 0, tcy = 0; Anchor(TopCenter, g, kPillW, kPillH, &tcx, &tcy);
    CheckEqD(tcx, (1265.0 - 104.0) / 2.0, "top-center x centers the pill in the client");
    CheckEqD(tcy, 48.0 + 12.0,            "top-center y is caption + edge gap");
}

static void TestBottomCornersAreUntouched()
{
    std::printf("bottom corners are unaffected by the title bar\n");
    const Client g = Gallery();
    for (int corner = 0; corner < kLegacyCornerCount; ++corner) {
        if (CornerIsTop(corner)) continue;
        double x = 0, y = 0;   Anchor(corner, g, kPillW, kPillH, &x, &y);
        double ox = 0, oy = 0; AnchorPreFix(corner, g, kPillW, kPillH, &ox, &oy);
        CheckEqD(x, ox, "bottom corner x matches the pre-fix anchor");
        CheckEqD(y, oy, "bottom corner y matches the pre-fix anchor");
    }

    double bcx = 0, bcy = 0; Anchor(BottomCenter, g, kPillW, kPillH, &bcx, &bcy);
    CheckEqD(bcx, (1265.0 - 104.0) / 2.0, "bottom-center x centers the pill in the client");
    CheckEqD(bcy, 1023.0 - 28.0 - 12.0,  "bottom-center y uses the bottom edge gap");
}

static void TestNonExtendingAppIsUnchanged()
{
    std::printf("an app that does not extend into its title bar is placed exactly as before\n");
    const Client n = NonExtending();
    for (int corner = 0; corner < kLegacyCornerCount; ++corner) {
        double x = 0, y = 0;   Anchor(corner, n, kPillW, kPillH, &x, &y);
        double ox = 0, oy = 0; AnchorPreFix(corner, n, kPillW, kPillH, &ox, &oy);
        CheckEqD(x, ox, "x matches the pre-fix anchor");
        CheckEqD(y, oy, "y matches the pre-fix anchor");
    }
}

static void TestNonsenseInsetCannotStrandTheToolbar()
{
    std::printf("a title bar reporting nonsense cannot park the toolbar off-screen\n");
    // The corner is persisted, so a pill parked outside the client area does not come back on a restart.
    Client absurd = Gallery(); absurd.titleBarInsetDip = 100000.0;
    double x = 0, y = 0; Anchor(CornerTL, absurd, kPillW, kPillH, &x, &y);
    Check(y + kPillH < absurd.h, "the pill stays inside the client area");
    CheckEqD(y, absurd.h / 3.0 + kEdgeGap, "the inset is capped at a third of the client height");

    Client negative = Gallery(); negative.titleBarInsetDip = -500.0;
    Anchor(CornerTL, negative, kPillW, kPillH, &x, &y);
    CheckEqD(y, kEdgeGap, "a negative inset degrades to no inset, never above the client top");
}

static void TestEveryPositionIsReachableByDrag()
{
    std::printf("every docking position is reachable by a drag\n");
    const Client g = Gallery();
    // Dropping the pill exactly where a corner parks must resolve to that corner. Top-right is the case
    // removed by construction and this change earns back.
    for (int corner = 0; corner < kCornerCount; ++corner) {
        double x = 0, y = 0; Anchor(corner, g, kPillW, kPillH, &x, &y);
        Check(NearestCorner(g, kPillW, kPillH, x, y) == corner, "a drop on a corner's anchor resolves to it");
    }
    // A drop in the top-right region resolves to top-right rather than to a surviving neighbour.
    Check(NearestCorner(g, kPillW, kPillH, g.w - 150.0, 40.0) == CornerTR,
          "a drop near the top-right resolves to top-right");
    Check(NearestCorner(g, kPillW, kPillH, g.w / 2.0 - kPillW / 2.0, 80.0) == TopCenter,
          "a drop near the top center resolves to top-center");
    Check(NearestCorner(g, kPillW, kPillH, g.w / 2.0 - kPillW / 2.0, g.h - 60.0) == BottomCenter,
          "a drop near the bottom center resolves to bottom-center");
    Check(kCornerCount == 6, "four corners and two center positions are offered");
}

int RunToolbarCornerTests()
{
    std::printf("DevToolsToolbarCorner tests\n");
    TestTopCornersClearTheCaption();
    TestBottomCornersAreUntouched();
    TestNonExtendingAppIsUnchanged();
    TestNonsenseInsetCannotStrandTheToolbar();
    TestEveryPositionIsReachableByDrag();
    return g_failures;
}
