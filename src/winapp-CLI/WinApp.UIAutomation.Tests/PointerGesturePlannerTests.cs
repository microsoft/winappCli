// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.


using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

/// <summary>
/// Unit tests for <see cref="PointerGesturePlanner"/> geometry and planning logic.
/// No live injection — all tests call <see cref="PointerGesturePlanner.PlanTouch"/> or
/// <see cref="PointerGesturePlanner.FirstOutOfBounds"/> directly and assert on the resulting
/// path geometry. The injection layer is tested separately in <see cref="PointerInputFrameTests"/>.
/// </summary>
[TestClass]
public class PointerGesturePlannerTests
{
    [TestMethod]
    [DataRow("100,200", 100, 200)]
    [DataRow(" 100 , 200 ", 100, 200)]
    [DataRow("-5,+6", -5, 6)]
    public void CoordinateParser_TryParsePoint_AcceptsSharedCoordinateGrammar(string value, int expectedX, int expectedY)
    {
        Assert.IsTrue(CoordinateParser.TryParsePoint(value, out var point));
        Assert.AreEqual(expectedX, point.X);
        Assert.AreEqual(expectedY, point.Y);

        Assert.IsTrue(CoordinateParser.TryParsePoint(value, out int x, out int y));
        Assert.AreEqual(expectedX, x);
        Assert.AreEqual(expectedY, y);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("100")]
    [DataRow("100,")]
    [DataRow("100,200,300")]
    [DataRow("2147483648,1")]
    public void CoordinateParser_TryParsePoint_RejectsInvalidOrOutOfRangePairs(string value)
    {
        Assert.IsFalse(CoordinateParser.TryParsePoint(value, out _));
        Assert.IsFalse(CoordinateParser.TryParsePoint(value, out _, out _));
    }

    [TestMethod]
    [DataRow("100,", true)]
    [DataRow("100,abc", true)]
    [DataRow("100,200,300", true)]
    [DataRow("name=Save, Continue", false)]
    [DataRow("abc,100", false)]
    public void CoordinateParser_LooksLikeCoordinates_DisambiguatesMalformedCoordinatesFromSelectors(
        string token, bool expected)
    {
        Assert.AreEqual(expected, CoordinateParser.LooksLikeCoordinates(token));
    }

    // -------------------------------------------------------------------------
    // M9 — Pinch geometry
    // -------------------------------------------------------------------------

    [TestMethod]
    public void PlanTouch_Pinch_ReturnsTwoFingerPaths()
    {
        var start = new PointerPoint(400, 300);
        var (contactPaths, points, fingers) = PointerGesturePlanner.PlanTouch(
            TouchGesture.Pinch, start, end: null, distance: 100, fingers: 2);

        Assert.AreEqual(2, contactPaths.Count, "Pinch must produce exactly 2 contact paths");
        Assert.AreEqual(2, fingers, "Effective finger count must be 2 for pinch");
    }

    [TestMethod]
    public void PlanTouch_Pinch_FingersStartApartAndConvergeTowardCenter()
    {
        // Pinch: two fingers start far apart (distance/2 from center) and move inward.
        var start = new PointerPoint(400, 300);
        int distance = 100;
        var (contactPaths, _, _) = PointerGesturePlanner.PlanTouch(
            TouchGesture.Pinch, start, end: null, distance, fingers: 2);

        // Left finger: starts to the left of center and moves right (closer to center).
        var leftPath = contactPaths[0];
        Assert.IsTrue(leftPath[0].X < start.X, "Left finger start must be left of center");
        Assert.IsTrue(leftPath[^1].X > leftPath[0].X, "Left finger must move right (toward center) in a pinch");
        Assert.IsTrue(leftPath[^1].X < start.X, "Left finger end must still be left of center (gap preserved)");

        // Right finger: starts to the right of center and moves left (closer to center).
        var rightPath = contactPaths[1];
        Assert.IsTrue(rightPath[0].X > start.X, "Right finger start must be right of center");
        Assert.IsTrue(rightPath[^1].X < rightPath[0].X, "Right finger must move left (toward center) in a pinch");
        Assert.IsTrue(rightPath[^1].X > start.X, "Right finger end must still be right of center (gap preserved)");

        // Both fingers stay on the same Y as the center.
        Assert.AreEqual(start.Y, leftPath[0].Y, "Left finger start Y must match center Y");
        Assert.AreEqual(start.Y, rightPath[0].Y, "Right finger start Y must match center Y");
    }

    [TestMethod]
    public void PlanTouch_Pinch_StartCoordinatesRespectHalfDistance()
    {
        // The initial finger spread is distance/2 from center on each side (clamped to at least
        // PinchCenterGapPx+1 per finger so fingers don't start inside each other).
        var start = new PointerPoint(200, 200);
        int distance = 80; // half = 40
        var (contactPaths, _, _) = PointerGesturePlanner.PlanTouch(
            TouchGesture.Pinch, start, end: null, distance, fingers: 2);

        int expectedHalf = Math.Max(5, distance / 2); // PinchCenterGapPx=4 → +1 = 5
        Assert.AreEqual(start.X - expectedHalf, contactPaths[0][0].X,
            "Left finger X start must be center.X - half");
        Assert.AreEqual(start.X + expectedHalf, contactPaths[1][0].X,
            "Right finger X start must be center.X + half");
    }

    // -------------------------------------------------------------------------
    // M9 — Stretch geometry
    // -------------------------------------------------------------------------

    [TestMethod]
    public void PlanTouch_Stretch_ReturnsTwoFingerPaths()
    {
        var start = new PointerPoint(400, 300);
        var (contactPaths, _, fingers) = PointerGesturePlanner.PlanTouch(
            TouchGesture.Stretch, start, end: null, distance: 100, fingers: 2);

        Assert.AreEqual(2, contactPaths.Count, "Stretch must produce exactly 2 contact paths");
        Assert.AreEqual(2, fingers, "Effective finger count must be 2 for stretch");
    }

    [TestMethod]
    public void PlanTouch_Stretch_FingersStartNearCenterAndDivergeOutward()
    {
        // Stretch is the reverse of pinch: fingers start near center and move outward.
        var start = new PointerPoint(400, 300);
        int distance = 100;
        var (contactPaths, _, _) = PointerGesturePlanner.PlanTouch(
            TouchGesture.Stretch, start, end: null, distance, fingers: 2);

        // Left finger: starts near center (left side) and moves further left.
        var leftPath = contactPaths[0];
        Assert.IsTrue(leftPath[0].X > leftPath[^1].X,
            "Left finger must move left (away from center) in a stretch");

        // Right finger: starts near center (right side) and moves further right.
        var rightPath = contactPaths[1];
        Assert.IsTrue(rightPath[^1].X > rightPath[0].X,
            "Right finger must move right (away from center) in a stretch");
    }

    [TestMethod]
    public void PlanTouch_Stretch_IsReverseOfPinch()
    {
        // Stretch start == Pinch end, and Stretch end == Pinch start (same points, swapped order).
        var start = new PointerPoint(300, 400);
        int distance = 120;

        var (pinchPaths, _, _) = PointerGesturePlanner.PlanTouch(
            TouchGesture.Pinch, start, end: null, distance, fingers: 2);
        var (stretchPaths, _, _) = PointerGesturePlanner.PlanTouch(
            TouchGesture.Stretch, start, end: null, distance, fingers: 2);

        // Left contact: stretch start == pinch end, stretch end == pinch start.
        Assert.AreEqual(pinchPaths[0][^1], stretchPaths[0][0],
            "Left finger: stretch start must equal pinch end");
        Assert.AreEqual(pinchPaths[0][0], stretchPaths[0][^1],
            "Left finger: stretch end must equal pinch start");

        // Right contact: same relationship.
        Assert.AreEqual(pinchPaths[1][^1], stretchPaths[1][0],
            "Right finger: stretch start must equal pinch end");
        Assert.AreEqual(pinchPaths[1][0], stretchPaths[1][^1],
            "Right finger: stretch end must equal pinch start");
    }

    // -------------------------------------------------------------------------
    // M9 — Finger-count coercion for pinch/stretch
    // -------------------------------------------------------------------------

    [TestMethod]
    public void PlanTouch_Pinch_FingerCountCoercedToTwo_WhenOneProvided()
    {
        var (contactPaths, _, fingers) = PointerGesturePlanner.PlanTouch(
            TouchGesture.Pinch, new PointerPoint(100, 100), end: null, distance: 50, fingers: 1);

        Assert.AreEqual(2, contactPaths.Count, "Pinch must coerce --fingers 1 to 2");
        Assert.AreEqual(2, fingers, "Reported finger count must be coerced to 2 for pinch");
    }

    [TestMethod]
    public void PlanTouch_Stretch_FingerCountCoercedToTwo_WhenOneProvided()
    {
        var (contactPaths, _, fingers) = PointerGesturePlanner.PlanTouch(
            TouchGesture.Stretch, new PointerPoint(100, 100), end: null, distance: 50, fingers: 1);

        Assert.AreEqual(2, contactPaths.Count, "Stretch must coerce --fingers 1 to 2");
        Assert.AreEqual(2, fingers, "Reported finger count must be coerced to 2 for stretch");
    }

    [TestMethod]
    public void PlanTouch_Pinch_FingerCountCoercedToTwo_WhenFiveProvided()
    {
        // Even with more than 2 fingers requested, pinch always uses exactly 2.
        var (contactPaths, _, fingers) = PointerGesturePlanner.PlanTouch(
            TouchGesture.Pinch, new PointerPoint(200, 200), end: null, distance: 60, fingers: 5);

        Assert.AreEqual(2, contactPaths.Count, "Pinch must always produce exactly 2 contact paths regardless of --fingers");
        Assert.AreEqual(2, fingers, "Effective fingers for pinch must always be 2");
    }

    // -------------------------------------------------------------------------
    // M9 — Bounds rejection for pinch/stretch geometry
    // -------------------------------------------------------------------------

    [TestMethod]
    public void PlanTouch_Pinch_OutOfBoundsStart_DetectedByFirstOutOfBounds()
    {
        // A pinch centered at X=5 with distance=100 will place the left finger at X=5-50=-45,
        // which is outside a rect starting at X=0.
        var start = new PointerPoint(5, 300);
        var (_, points, _) = PointerGesturePlanner.PlanTouch(
            TouchGesture.Pinch, start, end: null, distance: 100, fingers: 2);

        var rect = new PointerRect(0, 0, 800, 600);
        var oob = PointerGesturePlanner.FirstOutOfBounds(rect, points);

        Assert.IsNotNull(oob, "FirstOutOfBounds must detect the out-of-bounds pinch finger");
        Assert.IsTrue(oob.Value.X < 0, "The detected OOB point must have negative X (left of window)");
    }

    [TestMethod]
    public void PlanTouch_Stretch_OutOfBoundsEnd_DetectedByFirstOutOfBounds()
    {
        // A stretch centered at X=790 with distance=100 will place the right finger end at X=790+50=840,
        // which is outside a rect with right edge at X=800.
        var start = new PointerPoint(790, 300);
        var (_, points, _) = PointerGesturePlanner.PlanTouch(
            TouchGesture.Stretch, start, end: null, distance: 100, fingers: 2);

        var rect = new PointerRect(0, 0, 800, 600);
        var oob = PointerGesturePlanner.FirstOutOfBounds(rect, points);

        Assert.IsNotNull(oob, "FirstOutOfBounds must detect the out-of-bounds stretch endpoint");
        Assert.IsTrue(oob.Value.X >= 800, "The detected OOB point must be at or past the right edge");
    }

    // -------------------------------------------------------------------------
    // M9 — Double-tap path structure (repetition is in PointerInput.RunTouchGesture)
    // -------------------------------------------------------------------------

    [TestMethod]
    public void PlanTouch_DoubleTap_ReturnsSinglePointPathPerFinger()
    {
        // DoubleTap produces the same path structure as Tap — a single-point path per finger.
        // The 2× tap repetition is handled at the injection layer (RunTouchGesture), not the planner.
        var start = new PointerPoint(100, 200);
        var (tapPaths, tapPoints, tapFingers) = PointerGesturePlanner.PlanTouch(
            TouchGesture.Tap, start, end: null, distance: 0, fingers: 1);
        var (dtPaths, dtPoints, dtFingers) = PointerGesturePlanner.PlanTouch(
            TouchGesture.DoubleTap, start, end: null, distance: 0, fingers: 1);

        // Same number of contact paths.
        Assert.AreEqual(tapPaths.Count, dtPaths.Count,
            "DoubleTap planner output must have the same path count as Tap");
        // Same finger count.
        Assert.AreEqual(tapFingers, dtFingers, "DoubleTap effective finger count must equal Tap");
        // Same single point per path (the repetition happens at injection time, not here).
        for (int i = 0; i < tapPaths.Count; i++)
        {
            Assert.AreEqual(tapPaths[i].Count, dtPaths[i].Count,
                $"Contact path {i} must have the same waypoint count in Tap and DoubleTap");
            Assert.AreEqual(tapPaths[i][0], dtPaths[i][0],
                $"Contact path {i} waypoint[0] must be the same for Tap and DoubleTap");
        }
    }

    [TestMethod]
    public void PlanTouch_DoubleTap_MultiFinger_SpacesContactsCorrectly()
    {
        // With --fingers 3, DoubleTap plans 3 contacts each offset by FingerSpacingPx on X.
        var start = new PointerPoint(200, 300);
        var (contactPaths, points, fingers) = PointerGesturePlanner.PlanTouch(
            TouchGesture.DoubleTap, start, end: null, distance: 0, fingers: 3);

        Assert.AreEqual(3, contactPaths.Count, "DoubleTap with --fingers 3 must produce 3 contact paths");
        Assert.AreEqual(3, fingers, "Reported finger count must be 3");
        // Each contact is a single point at (start.X + i*FingerSpacingPx, start.Y).
        for (int i = 0; i < 3; i++)
        {
            Assert.AreEqual(1, contactPaths[i].Count, $"Contact {i} must be a single-point path for tap/double-tap");
            Assert.AreEqual(start.Y, contactPaths[i][0].Y, $"Contact {i} Y must match start Y");
        }
        // Contacts must be spread along X (ascending).
        Assert.IsTrue(contactPaths[1][0].X > contactPaths[0][0].X, "Contact 1 X must be greater than contact 0 X");
        Assert.IsTrue(contactPaths[2][0].X > contactPaths[1][0].X, "Contact 2 X must be greater than contact 1 X");
    }

    [TestMethod]
    public void RunTouchGesture_DoubleTap_ProducesTwoDownUpCycles_WithInterTapDelay()
    {
        // M4: the old test manually called InjectTouchStroke twice, bypassing the production
        // repetition path. This test invokes RunTouchGesture directly with DoubleTap so the
        // real repeats branch is exercised. An injectable sleepInter captures the inter-tap gap.
        var allFlags = new List<global::Windows.Win32.UI.Input.Pointer.POINTER_FLAGS>();
        var interTapSleeps = new List<int>();

        PointerInput.TouchSender recorder = contacts =>
        {
            foreach (var c in contacts)
            {
                allFlags.Add(c.pointerInfo.pointerFlags);
            }
        };

        var paths = new List<IReadOnlyList<PointerPoint>>
        {
            new List<PointerPoint> { new PointerPoint(100, 200) }
        };

        PointerInput.RunTouchGesture(TouchGesture.DoubleTap, paths, holdMs: 0, durationMs: 0, recorder,
            sleepInter: ms => interTapSleeps.Add(ms));

        int downCount = allFlags.Count(f => f.HasFlag(global::Windows.Win32.UI.Input.Pointer.POINTER_FLAGS.POINTER_FLAG_DOWN));
        int upCount   = allFlags.Count(f => f.HasFlag(global::Windows.Win32.UI.Input.Pointer.POINTER_FLAGS.POINTER_FLAG_UP));

        Assert.AreEqual(2, downCount, "DoubleTap must produce exactly 2 DOWN frames");
        Assert.AreEqual(2, upCount,   "DoubleTap must produce exactly 2 UP frames");

        // Exactly one inter-tap sleep must have been requested, at the expected 60ms gap.
        Assert.AreEqual(1, interTapSleeps.Count,
            "RunTouchGesture must sleep exactly once between the two taps of a double-tap");
        Assert.AreEqual(60, interTapSleeps[0],
            "Inter-tap sleep must be 60ms");
    }
}
