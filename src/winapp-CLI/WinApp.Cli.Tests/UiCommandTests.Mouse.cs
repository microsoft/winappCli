// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Models;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    // ---------------------------------------------------------------------
    // drag (#498) — mouse drag gesture: drag <from> <to>, each a selector
    // (element center) or screen x,y coordinates
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task Drag_RightButton_SetsButton()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Image", Selector = "img-canvas-1234", X = 0, Y = 0, Width = 100, Height = 100 };

        var command = GetRequiredService<UiDragCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["img-canvas-1234", "200,200", "-a", "TestApp", "--right", "--json"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(1, _fakeMouse.DragCalls.Count);
        Assert.IsTrue(_fakeMouse.DragCalls[0].RightButton);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual("right", result.GetProperty("button").GetString());
    }

    [TestMethod]
    public async Task Drag_MissingApp_ReturnsError()
    {
        var command = GetRequiredService<UiDragCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["100,100", "200,200", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMouse.DragCalls.Count);
    }

    [TestMethod]
    public async Task Drag_MissingArgs_ReturnsError()
    {
        var command = GetRequiredService<UiDragCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMouse.DragCalls.Count);
    }

    // ---- drag <from> <to> where each is a selector (center) or x,y screen coords ----

    [TestMethod]
    public async Task Drag_TwoArg_SelectorToCoordinates_UsesElementCenter()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Image", Selector = "img-canvas-1234", X = 50, Y = 60, Width = 200, Height = 200 };

        var command = GetRequiredService<UiDragCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["img-canvas-1234", "300,400", "-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual("img-canvas-1234", result.GetProperty("from").GetString());
        Assert.AreEqual("300,400", result.GetProperty("to").GetString());
        Assert.AreEqual(150, result.GetProperty("fromX").GetInt32());  // 50 + 200/2
        Assert.AreEqual(160, result.GetProperty("fromY").GetInt32());  // 60 + 200/2
        Assert.AreEqual(300, result.GetProperty("toX").GetInt32());
        Assert.AreEqual(400, result.GetProperty("toY").GetInt32());

        Assert.AreEqual(1, _fakeMouse.DragCalls.Count);
        var drag = _fakeMouse.DragCalls[0];
        Assert.AreEqual(150, drag.FromX);
        Assert.AreEqual(160, drag.FromY);
        Assert.AreEqual(300, drag.ToX);
        Assert.AreEqual(400, drag.ToY);
    }

    [TestMethod]
    public async Task Drag_TwoArg_CoordinatesToCoordinates_NoElementLookup()
    {
        // Both endpoints are bare coordinates, so no element is resolved (FindSingleResult stays null).
        _fakeUia.FindSingleResult = null;

        var command = GetRequiredService<UiDragCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["100,200", "300,400", "-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual("100,200", result.GetProperty("from").GetString());
        Assert.AreEqual("300,400", result.GetProperty("to").GetString());
        Assert.AreEqual(100, result.GetProperty("fromX").GetInt32());
        Assert.AreEqual(200, result.GetProperty("fromY").GetInt32());
        Assert.AreEqual(300, result.GetProperty("toX").GetInt32());
        Assert.AreEqual(400, result.GetProperty("toY").GetInt32());

        Assert.AreEqual(1, _fakeMouse.DragCalls.Count);
        var drag = _fakeMouse.DragCalls[0];
        Assert.AreEqual(100, drag.FromX);
        Assert.AreEqual(200, drag.FromY);
        Assert.AreEqual(300, drag.ToX);
        Assert.AreEqual(400, drag.ToY);
    }

    [TestMethod]
    public async Task Drag_TwoArg_SelectorToSelector_UsesBothCenters()
    {
        // Distinct from/to elements (via the per-call queue) so reusing one endpoint for both would fail.
        _fakeUia.FindSingleResults.Enqueue(new UiElement { Id = "e0", Type = "ListItem", Selector = "row-1111", X = 10, Y = 20, Width = 100, Height = 80 });   // center 60,60
        _fakeUia.FindSingleResults.Enqueue(new UiElement { Id = "e1", Type = "ListItem", Selector = "row-2222", X = 200, Y = 300, Width = 40, Height = 60 });  // center 220,330

        var command = GetRequiredService<UiDragCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["row-1111", "row-2222", "-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual("row-1111", result.GetProperty("from").GetString());
        Assert.AreEqual("row-2222", result.GetProperty("to").GetString());
        Assert.AreEqual(60, result.GetProperty("fromX").GetInt32());   // 10 + 100/2
        Assert.AreEqual(60, result.GetProperty("fromY").GetInt32());   // 20 + 80/2
        Assert.AreEqual(220, result.GetProperty("toX").GetInt32());    // 200 + 40/2
        Assert.AreEqual(330, result.GetProperty("toY").GetInt32());    // 300 + 60/2

        Assert.AreEqual(1, _fakeMouse.DragCalls.Count);
        var drag = _fakeMouse.DragCalls[0];
        Assert.AreEqual(60, drag.FromX);
        Assert.AreEqual(60, drag.FromY);
        Assert.AreEqual(220, drag.ToX);
        Assert.AreEqual(330, drag.ToY);
    }

    [TestMethod]
    public async Task Drag_TwoArg_CoordinatesToSelector_ResolvesOnlyTo()
    {
        // from is bare coords (no lookup); to is a selector resolving to its center.
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "ListItem", Selector = "row-2222", X = 200, Y = 300, Width = 40, Height = 60 };

        var command = GetRequiredService<UiDragCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["100,200", "row-2222", "-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual("100,200", result.GetProperty("from").GetString());
        Assert.AreEqual("row-2222", result.GetProperty("to").GetString());
        Assert.AreEqual(100, result.GetProperty("fromX").GetInt32());
        Assert.AreEqual(200, result.GetProperty("fromY").GetInt32());
        Assert.AreEqual(220, result.GetProperty("toX").GetInt32());   // 200 + 40/2
        Assert.AreEqual(330, result.GetProperty("toY").GetInt32());   // 300 + 60/2

        Assert.AreEqual(1, _fakeMouse.DragCalls.Count);
    }

    [TestMethod]
    public async Task Drag_TwoArg_ToSelectorNotFound_ReturnsError()
    {
        // from resolves fine; the second (to) selector lookup returns null → error, no drag.
        _fakeUia.FindSingleResults.Enqueue(new UiElement { Id = "e0", Type = "ListItem", Selector = "row-1111", X = 10, Y = 20, Width = 100, Height = 80 });
        _fakeUia.FindSingleResults.Enqueue(null);

        var command = GetRequiredService<UiDragCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["row-1111", "missing-9999", "-a", "TestApp", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMouse.DragCalls.Count);
    }

    [TestMethod]
    public async Task Drag_TwoArg_MissingTo_ReturnsError()
    {
        var command = GetRequiredService<UiDragCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["100,200", "-a", "TestApp", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMouse.DragCalls.Count);
    }

    [TestMethod]
    public async Task Drag_TwoArg_SelectorEndpointNotFound_ReturnsError()
    {
        _fakeUia.FindSingleResult = null;

        var command = GetRequiredService<UiDragCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["missing-0000", "300,400", "-a", "TestApp", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMouse.DragCalls.Count);
    }

    [TestMethod]
    public async Task Drag_TwoArg_ZeroSizeSelector_ReturnsError()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Image", Selector = "img-zero-0000", X = 10, Y = 20, Width = 0, Height = 0 };

        var command = GetRequiredService<UiDragCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["img-zero-0000", "300,400", "-a", "TestApp", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMouse.DragCalls.Count);
    }

    // ---- --hold-ms / --dwell-ms: press-and-hold (long-press) and drop-target dwell ----

    [TestMethod]
    public async Task Drag_HoldMs_FlowsToMouseInput()
    {
        // from == to with --hold-ms is a press-and-hold / long-press gesture.
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Button", Selector = "btn-tile-9001", X = 100, Y = 100, Width = 80, Height = 40 };

        var command = GetRequiredService<UiDragCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-tile-9001", "btn-tile-9001", "-a", "TestApp", "--hold-ms", "600", "--json"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(1, _fakeMouse.DragCalls.Count);
        var drag = _fakeMouse.DragCalls[0];
        Assert.AreEqual(600, drag.HoldMs);
        Assert.AreEqual(0, drag.DwellMs);
        Assert.AreEqual(140, drag.FromX); // 100 + 80/2
        Assert.AreEqual(140, drag.ToX);   // same element → long-press, no movement

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual(600, result.GetProperty("holdMs").GetInt32());
        Assert.AreEqual(0, result.GetProperty("dwellMs").GetInt32());
    }

    [TestMethod]
    public async Task Drag_DwellMs_FlowsToMouseInput()
    {
        // --dwell-ms holds at the destination so a drop target / merge overlay can latch before release.
        var command = GetRequiredService<UiDragCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["100,100", "300,300", "-a", "TestApp", "--dwell-ms", "350", "--json"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(1, _fakeMouse.DragCalls.Count);
        var drag = _fakeMouse.DragCalls[0];
        Assert.AreEqual(0, drag.HoldMs);
        Assert.AreEqual(350, drag.DwellMs);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual(350, result.GetProperty("dwellMs").GetInt32());
    }

    [TestMethod]
    public async Task Drag_HoldAndDwell_BothFlow()
    {
        var command = GetRequiredService<UiDragCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["100,100", "300,300", "-a", "TestApp", "--hold-ms", "120", "--dwell-ms", "200", "--json"]);
        Assert.AreEqual(0, exitCode);

        var drag = _fakeMouse.DragCalls[0];
        Assert.AreEqual(120, drag.HoldMs);
        Assert.AreEqual(200, drag.DwellMs);
    }

    [TestMethod]
    public async Task Drag_DefaultHoldAndDwell_AreZero()
    {
        var command = GetRequiredService<UiDragCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["100,100", "300,300", "-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);

        var drag = _fakeMouse.DragCalls[0];
        Assert.AreEqual(0, drag.HoldMs);
        Assert.AreEqual(0, drag.DwellMs);
    }

    [TestMethod]
    [DataRow("--hold-ms")]
    [DataRow("--dwell-ms")]
    public async Task Drag_NegativeHoldOrDwell_ReturnsError(string option)
    {
        var command = GetRequiredService<UiDragCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["100,100", "300,300", "-a", "TestApp", option, "-5", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMouse.DragCalls.Count);
    }

    [TestMethod]
    public async Task Drag_ForegroundGuardDenies_AbortsWithoutDragging()
    {
        // The drag must consult the foreground guard before the button-down; a denial (e.g. a locked
        // desktop or the wrong window in front) aborts without injecting anything (M5).
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Image", Selector = "img-canvas-1234", X = 0, Y = 0, Width = 100, Height = 100, WindowHandle = 7777 };
        _fakeForeground.Allow = false;
_fakeForeground.DenyReason = ForegroundCheck.ForegroundNotTarget;

        var command = GetRequiredService<UiDragCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["img-canvas-1234", "200,200", "-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMouse.DragCalls.Count, "no drag should be injected when the foreground guard refuses");
        Assert.AreEqual(0, _fakeMouse.MoveCursorCalls.Count, "the gate denies before the cursor is positioned");
        Assert.AreEqual(1, _fakeForeground.Calls.Count);
        StringAssert.Contains(ConsoleStdErr.ToString(), "refusing to drag",
            "the refusal must name the action the command was attempting");
    }

    [TestMethod]
    public async Task Drag_FromMovesDuringSettle_ReturnsTargetMoved()
    {
        // F3/N5 residual race for the drag from-point: ResolveStableAsync sees it settle (two equal reads),
        // but it drifts during the cursor-settle window before the button-down. The post-settle confirm read
        // must catch the drift and refuse rather than press on empty space (M6). Sequence: initial + stable
        // re-read at X=100, then the confirm read finds X=500.
        const string sel = "row-drift-1234";
        var seq = new Queue<UiElement?>();
        seq.Enqueue(new UiElement { Id = "e0", Type = "ListItem", Selector = sel, X = 100, Y = 20, Width = 40, Height = 30 }); // initial resolve
        seq.Enqueue(new UiElement { Id = "e0", Type = "ListItem", Selector = sel, X = 100, Y = 20, Width = 40, Height = 30 }); // stabilize re-read: settles
        seq.Enqueue(new UiElement { Id = "e0", Type = "ListItem", Selector = sel, X = 500, Y = 20, Width = 40, Height = 30 }); // confirm read: drifted
        _fakeUia.MovingResults[sel] = seq;

        var command = GetRequiredService<UiDragCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [sel, "300,400", "-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMouse.DragCalls.Count, "no button-down should fire when the from-point drifted during the settle");
        Assert.AreEqual(1, _fakeMouse.MoveCursorCalls.Count, "the cursor was positioned before the confirm read");
    }

    [TestMethod]
    [DataRow("100,")]
    [DataRow("100,200,300")]
    [DataRow("100,abc")]
    public async Task Drag_MalformedCoordinates_ReturnsErrorNotElementLookup(string badPoint)
    {
        // A comma-separated token that isn't a valid x,y pair must be rejected as bad coordinates, not
        // silently treated as a selector (M7). A valid element is configured on purpose: without the
        // coordinate-shape check the malformed token would resolve to it and the drag would wrongly run.
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Image", Selector = "img-1", X = 0, Y = 0, Width = 100, Height = 100 };

        var command = GetRequiredService<UiDragCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [badPoint, "300,400", "-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMouse.DragCalls.Count, "a malformed coordinate token must not fall through to a selector lookup");
    }

    // ---------------------------------------------------------------------
    // scroll --wheel (#498) — synthetic mouse-wheel input
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task Scroll_Wheel_SendsWheelDeltaAtElementCenter()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "List", Selector = "lst-items-1234", X = 50, Y = 60, Width = 120, Height = 40 };

        var command = GetRequiredService<UiScrollCommand>();
        // --wheel is in notches; -1 notch scales to the -120 WHEEL_DELTA the OS wheel consumes.
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["lst-items-1234", "-a", "TestApp", "--wheel", "-1", "--json"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(1, _fakeMouse.ScrollWheelCalls.Count);
        var wheel = _fakeMouse.ScrollWheelCalls[0];
        Assert.AreEqual(110, wheel.ScreenX); // 50 + 120/2
        Assert.AreEqual(80, wheel.ScreenY);  // 60 + 40/2
        Assert.AreEqual(-120, wheel.Delta);  // -1 notch * 120 (WHEEL_DELTA)

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual(-1, result.GetProperty("wheel").GetInt32()); // echoes the notch count the caller passed
    }

    [TestMethod]
    public async Task Scroll_Wheel_MultipleNotches_ScaleByWheelDelta()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "List", Selector = "lst-items-1234", X = 0, Y = 0, Width = 100, Height = 100 };

        var command = GetRequiredService<UiScrollCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["lst-items-1234", "-a", "TestApp", "--wheel", "3", "--json"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(1, _fakeMouse.ScrollWheelCalls.Count);
        Assert.AreEqual(360, _fakeMouse.ScrollWheelCalls[0].Delta); // 3 notches * 120
    }

    [TestMethod]
    public async Task Scroll_NoDirectionToOrWheel_ReturnsError()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "List", Selector = "lst-items-1234", X = 0, Y = 0, Width = 100, Height = 100 };

        var command = GetRequiredService<UiScrollCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["lst-items-1234", "-a", "TestApp", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMouse.ScrollWheelCalls.Count);
    }

    [TestMethod]
    public async Task Scroll_Wheel_ZeroSizeElement_ReturnsError()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "List", Selector = "lst-tiny-0000", X = 10, Y = 20, Width = 0, Height = 0 };

        var command = GetRequiredService<UiScrollCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["lst-tiny-0000", "-a", "TestApp", "--wheel", "-1", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMouse.ScrollWheelCalls.Count);
    }

    [TestMethod]
    public async Task Scroll_ConflictingModes_ReturnsError()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "List", Selector = "lst-items-1234", X = 0, Y = 0, Width = 100, Height = 100 };

        var command = GetRequiredService<UiScrollCommand>();
        // --wheel and --direction are mutually exclusive; specifying both must fail rather than
        // silently preferring --wheel.
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["lst-items-1234", "-a", "TestApp", "--wheel", "-1", "--direction", "down", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMouse.ScrollWheelCalls.Count);
    }

    [TestMethod]
    public async Task Scroll_Wheel_ForegroundGuardDenies_AbortsWithoutScrolling()
    {
        // The wheel inject must consult the foreground guard first; a denial aborts without injecting (M5).
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "List", Selector = "lst-items-1234", X = 50, Y = 60, Width = 120, Height = 40, WindowHandle = 7777 };
        _fakeForeground.Allow = false;
_fakeForeground.DenyReason = ForegroundCheck.ForegroundNotTarget;

        var command = GetRequiredService<UiScrollCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["lst-items-1234", "-a", "TestApp", "--wheel", "-1", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMouse.ScrollWheelCalls.Count, "no wheel should be injected when the foreground guard refuses");
        Assert.AreEqual(1, _fakeForeground.Calls.Count);
        StringAssert.Contains(ConsoleStdErr.ToString(), "refusing to scroll --wheel",
            "the refusal must name the action the command was attempting");
    }

}
