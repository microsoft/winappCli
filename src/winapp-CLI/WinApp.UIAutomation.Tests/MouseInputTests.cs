// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
[DoNotParallelize]
public class MouseInputTests
{
    [TestInitialize]
    public void Initialize() => ResetSeams();

    [TestCleanup]
    public void Cleanup() => MouseInput.ResetNativeSeams();

    [TestMethod]
    public void NormalizeAbsolute_UsesVirtualDesktopOriginSizeAndClamps()
    {
        var metrics = new MouseInput.VirtualDesktopMetrics(-100, -50, 201, 101);

        Assert.AreEqual((0, 0), MouseInput.NormalizeAbsolute(-100, -50, metrics));
        Assert.AreEqual((32768, 32768), MouseInput.NormalizeAbsolute(0, 0, metrics));
        Assert.AreEqual((65535, 65535), MouseInput.NormalizeAbsolute(100, 50, metrics));
        Assert.AreEqual((65535, 0), MouseInput.NormalizeAbsolute(500, -500, metrics));
        Assert.AreEqual((0, 0), MouseInput.NormalizeAbsolute(0, 0, new MouseInput.VirtualDesktopMetrics(10, 10, 0, 0)));
    }

    [TestMethod]
    public void CreateInputHelpers_SetExpectedMouseFields()
    {
        var move = MouseInput.CreateMoveInput(5, 6, new MouseInput.VirtualDesktopMetrics(0, 0, 11, 11));
        Assert.AreEqual(INPUT_TYPE.INPUT_MOUSE, move.type);
        Assert.AreEqual(32768, move.Anonymous.mi.dx);
        Assert.AreEqual(39321, move.Anonymous.mi.dy);
        Assert.AreEqual(
            MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_ABSOLUTE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_VIRTUALDESK,
            move.Anonymous.mi.dwFlags);

        var button = MouseInput.CreateButtonInput(MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTDOWN);
        Assert.AreEqual(MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTDOWN, button.Anonymous.mi.dwFlags);

        var click = MouseInput.CreateClickInputs(MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN, MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP);
        Assert.AreEqual(2, click.Length);
        Assert.AreEqual(MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN, click[0].Anonymous.mi.dwFlags);
        Assert.AreEqual(MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP, click[1].Anonymous.mi.dwFlags);

        var wheel = MouseInput.CreateWheelInput(-120);
        Assert.AreEqual(unchecked((uint)-120), wheel.Anonymous.mi.mouseData);
        Assert.AreEqual(MOUSE_EVENT_FLAGS.MOUSEEVENTF_WHEEL, wheel.Anonymous.mi.dwFlags);
    }

    [TestMethod]
    public void ButtonFlags_SelectsLeftOrRightPair()
    {
        Assert.AreEqual((MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN, MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP), MouseInput.ButtonFlags(false));
        Assert.AreEqual((MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTDOWN, MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTUP), MouseInput.ButtonFlags(true));
    }

    [TestMethod]
    public void BuildDragPath_ReturnsTwentyRoundedStepsEndingAtDestination()
    {
        var path = MouseInput.BuildDragPath(0, 0, 10, -10);

        Assert.AreEqual(20, path.Count);
        Assert.AreEqual((0, 0), path[0]);
        Assert.AreEqual((5, -5), path[9]);
        Assert.AreEqual((10, -10), path[19]);
    }

    [TestMethod]
    public void Hover_SendsWiggleMovesAndExpectedSleeps()
    {
        var sent = new List<INPUT[]>();
        var sleeps = new List<int>();
        MouseInput.s_sendInput = inputs => { sent.Add(inputs.ToArray()); return (uint)inputs.Length; };
        MouseInput.s_sleep = sleeps.Add;

        MouseInput.Hover(10, 20);

        Assert.AreEqual(3, sleeps.Count);
        Assert.AreEqual(30, sleeps[0]);
        Assert.AreEqual(20, sleeps[1]);
        Assert.AreEqual(20, sleeps[2]);
        Assert.AreEqual(4, sent.Count);
        var hoverPoints = sent.Select(BatchPoint).ToArray();
        Assert.AreEqual((10, 20), hoverPoints[0]);
        Assert.AreEqual((12, 20), hoverPoints[1]);
        Assert.AreEqual((10, 22), hoverPoints[2]);
        Assert.AreEqual((10, 20), hoverPoints[3]);
    }

    [TestMethod]
    public void MoveCursor_UsesSetCursorSeamOnly()
    {
        (int X, int Y)? observed = null;
        var sends = 0;
        MouseInput.s_setCursorPos = (x, y) => { observed = (x, y); return true; };
        MouseInput.s_sendInput = inputs => { sends++; return (uint)inputs.Length; };

        MouseInput.MoveCursor(7, 9);

        Assert.AreEqual((7, 9), observed);
        Assert.AreEqual(0, sends);
    }

    [TestMethod]
    public void Click_SendsRightDoubleClickWithSettle()
    {
        (int X, int Y)? cursor = null;
        var sleeps = new List<int>();
        var sent = new List<INPUT[]>();
        MouseInput.s_setCursorPos = (x, y) => { cursor = (x, y); return true; };
        MouseInput.s_sleep = sleeps.Add;
        MouseInput.s_sendInput = inputs => { sent.Add(inputs.ToArray()); return (uint)inputs.Length; };

        MouseInput.Click(3, 4, doubleClick: true, rightClick: true, settleMs: 12);

        Assert.AreEqual((3, 4), cursor);
        Assert.AreEqual(2, sleeps.Count);
        Assert.AreEqual(12, sleeps[0]);
        Assert.AreEqual(50, sleeps[1]);
        Assert.AreEqual(2, sent.Count);
        Assert.AreEqual(MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTDOWN, sent[0][0].Anonymous.mi.dwFlags);
        Assert.AreEqual(MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTUP, sent[0][1].Anonymous.mi.dwFlags);
        Assert.AreEqual(MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTDOWN, sent[1][0].Anonymous.mi.dwFlags);
        Assert.AreEqual(MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTUP, sent[1][1].Anonymous.mi.dwFlags);
    }

    [TestMethod]
    public void Drag_SendsDownPathAndUpWithOptionalTiming()
    {
        var sleeps = new List<int>();
        var flags = new List<MOUSE_EVENT_FLAGS>();
        var points = new List<(int X, int Y)>();
        MouseInput.s_sleep = sleeps.Add;
        MouseInput.s_sendInput = inputs =>
        {
            if ((inputs[0].Anonymous.mi.dwFlags & MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE) != 0)
            {
                points.Add(BatchPoint(inputs));
            }
            else
            {
                flags.Add(inputs[0].Anonymous.mi.dwFlags);
            }

            return (uint)inputs.Length;
        };

        MouseInput.Drag(0, 0, 10, 10, rightButton: false, holdMs: 7, dwellMs: 8, settleMs: 9);

        Assert.AreEqual(21, points.Count);
        Assert.AreEqual((0, 0), points[0]);
        Assert.AreEqual((10, 10), points[^1]);
        Assert.AreEqual(2, flags.Count);
        Assert.AreEqual(MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN, flags[0]);
        Assert.AreEqual(MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP, flags[1]);
        CollectionAssert.Contains(sleeps, 9);
        CollectionAssert.Contains(sleeps, 7);
        CollectionAssert.Contains(sleeps, 8);
    }

    [TestMethod]
    public void Drag_WhenMoveThrows_ReleasesButtonBestEffort()
    {
        var flags = new List<MOUSE_EVENT_FLAGS>();
        var calls = 0;
        MouseInput.s_sendInput = inputs =>
        {
            calls++;
            var flag = inputs[0].Anonymous.mi.dwFlags;
            if ((flag & MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE) != 0 && calls > 2)
            {
                throw new InvalidOperationException("move failed");
            }

            if ((flag & MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE) == 0)
            {
                flags.Add(flag);
            }

            return (uint)inputs.Length;
        };

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            MouseInput.Drag(0, 0, 1, 1, settleMs: 0));

        Assert.AreEqual("move failed", ex.Message);
        Assert.AreEqual(2, flags.Count);
        Assert.AreEqual(MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN, flags[0]);
        Assert.AreEqual(MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP, flags[1]);
    }

    [TestMethod]
    public void Drag_WhenBestEffortReleaseAlsoFails_SwallowsReleaseFailure()
    {
        var upAttempts = 0;
        var moveAttempts = 0;
        MouseInput.s_sendInput = inputs =>
        {
            var flag = inputs[0].Anonymous.mi.dwFlags;
            if ((flag & MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE) != 0)
            {
                moveAttempts++;
                if (moveAttempts > 1)
                {
                    throw new InvalidOperationException("move failed");
                }
            }

            if (flag == MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP)
            {
                upAttempts++;
                throw new InvalidOperationException("up failed");
            }

            return (uint)inputs.Length;
        };

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            MouseInput.Drag(0, 0, 1, 1, settleMs: 0));

        Assert.AreEqual("move failed", ex.Message);
        Assert.AreEqual(1, upAttempts);
    }

    [TestMethod]
    public void ScrollWheel_MovesThenSendsWheelAfterSettle()
    {
        var sleeps = new List<int>();
        var sent = new List<INPUT[]>();
        MouseInput.s_sleep = sleeps.Add;
        MouseInput.s_sendInput = inputs => { sent.Add(inputs.ToArray()); return (uint)inputs.Length; };

        MouseInput.ScrollWheel(2, 3, -240, settleMs: 6);

        Assert.AreEqual(1, sleeps.Count);
        Assert.AreEqual(6, sleeps[0]);
        Assert.AreEqual(2, sent.Count);
        Assert.AreEqual((2, 3), BatchPoint(sent[0]));
        Assert.AreEqual(MOUSE_EVENT_FLAGS.MOUSEEVENTF_WHEEL, sent[1][0].Anonymous.mi.dwFlags);
        Assert.AreEqual(unchecked((uint)-240), sent[1][0].Anonymous.mi.mouseData);
    }

    [TestMethod]
    [DataRow(true, "no interactive desktop")]
    [DataRow(false, "elevated")]
    public void SendInputs_ZeroWriteReportsPreciseReason(bool noForeground, string expected)
    {
        MouseInput.s_foregroundWindowIsNull = () => noForeground;
        MouseInput.s_sendInput = _ => 0;

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => MouseInput.ScrollWheel(1, 1, 120, settleMs: 0));

        StringAssert.Contains(ex.Message, expected);
    }

    [TestMethod]
    public void SendInputs_ShortWriteReportsPartialGesture()
    {
        MouseInput.s_sendInput = inputs => inputs.Length == 1 ? 0u : 1u;

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => MouseInput.Click(1, 1, settleMs: 0));

        StringAssert.Contains(ex.Message, "partially applied");
    }

    [TestMethod]
    public void RealMouseInput_DelegatesAllMembersToMouseInput()
    {
        var real = new RealMouseInput();
        var cursorPositions = new List<(int X, int Y)>();
        var sent = new List<INPUT[]>();
        MouseInput.s_setCursorPos = (x, y) => { cursorPositions.Add((x, y)); return true; };
        MouseInput.s_sendInput = inputs => { sent.Add(inputs.ToArray()); return (uint)inputs.Length; };

        real.MoveCursor(1, 2);
        real.Hover(3, 4);
        real.Click(5, 6, doubleClick: false, rightClick: true, settleMs: 0);
        real.Drag(7, 8, 9, 10, rightButton: true, settleMs: 0);
        real.ScrollWheel(11, 12, 120, settleMs: 0);

        CollectionAssert.Contains(cursorPositions, (1, 2));
        CollectionAssert.Contains(cursorPositions, (5, 6));
        Assert.IsTrue(sent.Any(batch => batch[0].Anonymous.mi.dwFlags == MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTDOWN));
        Assert.IsTrue(sent.Any(batch => batch[0].Anonymous.mi.dwFlags == MOUSE_EVENT_FLAGS.MOUSEEVENTF_WHEEL));
        Assert.IsTrue(sent.Any(batch => (batch[0].Anonymous.mi.dwFlags & MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE) != 0));
    }
    internal static void ResetSeams()
    {
        MouseInput.s_sendInput = inputs => (uint)inputs.Length;
        MouseInput.s_setCursorPos = (_, _) => true;
        MouseInput.s_getSystemMetrics = metric => metric switch
        {
            SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN => 0,
            SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN => 0,
            SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN => 101,
            SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN => 101,
            _ => 0
        };
        MouseInput.s_foregroundWindowIsNull = () => false;
        MouseInput.s_sleep = _ => { };
    }

    private static (int X, int Y) BatchPoint(INPUT[] batch)
    {
        Assert.AreEqual(1, batch.Length);
        return Denormalize(batch[0]);
    }

    private static (int X, int Y) Denormalize(INPUT input)
        => ((int)Math.Round(input.Anonymous.mi.dx * 100.0 / 65535.0), (int)Math.Round(input.Anonymous.mi.dy * 100.0 / 65535.0));
}



