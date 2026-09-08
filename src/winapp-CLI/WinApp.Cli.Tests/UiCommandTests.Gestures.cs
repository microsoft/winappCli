// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Models;

namespace WinApp.Cli.Tests;

/// <summary>
/// Remaining coordinate-gesture branches not covered by the existing gesture tests: the shared
/// guard rails (missing app / selector, element-not-found, zero-size), the <em>final</em> foreground
/// gate (the second check, immediately before injection), the confirm-read "target moved" races,
/// non-JSON success logging, and the COM / generic catch handlers. Driven entirely through the fakes
/// (FindSingle results, MovingResults sequences, FakeForegroundGuard.DenyOnCallNumber) — no real input.
/// </summary>
public partial class UiCommandTests
{
    private static UiElement StableButton(string selector = "btn-1234")
        => new() { Id = "e0", Type = "Button", Selector = selector, X = 10, Y = 20, Width = 100, Height = 30 };

    // ---------------------------------------------------------------- click

    [TestMethod]
    public async Task Click_MissingApp_ReturnsError()
    {
        var command = GetRequiredService<UiClickCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-1234", "--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Click_MissingSelector_ReturnsError()
    {
        var command = GetRequiredService<UiClickCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Click_ElementNotFound_ReturnsError()
    {
        _fakeUia.FindSingleResult = null;
        var command = GetRequiredService<UiClickCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["missing-1234", "-a", "TestApp", "--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Click_ZeroSizeElement_ReturnsError()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Button", Selector = "btn-0", X = 5, Y = 5, Width = 0, Height = 0 };
        var command = GetRequiredService<UiClickCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-0", "-a", "TestApp", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMouse.ClickCalls.Count);
    }

    [TestMethod]
    public async Task Click_FinalForegroundGateDenies_AbortsWithoutClicking()
    {
        // Element is static, so both foreground gates are reached; deny the *second* (final) gate only.
        _fakeUia.FindSingleResult = StableButton();
        _fakeForeground.DenyOnCallNumber = 2;

        var command = GetRequiredService<UiClickCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-1234", "-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMouse.ClickCalls.Count, "the final gate must abort before the button-down");
        Assert.AreEqual(2, _fakeForeground.Calls.Count, "both gates run; the second one denies");
    }

    [TestMethod]
    public async Task Click_NonJson_LogsSuccess()
    {
        _fakeUia.FindSingleResult = StableButton();
        var command = GetRequiredService<UiClickCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-1234", "-a", "TestApp"]);
        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeMouse.ClickCalls.Count);
    }

    [TestMethod]
    public async Task Click_Com_ReturnsError()
    {
        _fakeUia.FindSingleThrow = FakeComException;
        var command = GetRequiredService<UiClickCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-1234", "-a", "TestApp", "--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Click_Generic_ReturnsError()
    {
        _fakeTargetResolver.ResolveThrow = FakeGenericException;
        var command = GetRequiredService<UiClickCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-1234", "-a", "TestApp", "--json"]);
        Assert.AreEqual(1, exitCode);
    }

    // ---------------------------------------------------------------- hover

    [TestMethod]
    public async Task Hover_MovingTarget_ReturnsTargetMoved()
    {
        const string sel = "btn-hover-moving-1234";
        var seq = new Queue<UiElement?>();
        for (int i = 0; i < 4; i++)
        {
            seq.Enqueue(new UiElement { Id = "e0", Type = "Button", Selector = sel, X = 10, Y = 20 + i * 60, Width = 100, Height = 30 });
        }
        _fakeUia.MovingResults[sel] = seq;

        var command = GetRequiredService<UiHoverCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [sel, "-a", "TestApp", "--json", "--dwell-time", "0"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMouse.HoverCalls.Count);
    }

    [TestMethod]
    public async Task Hover_NonJson_LogsSuccess()
    {
        _fakeUia.FindSingleResult = StableButton("btn-hover-1234");
        var command = GetRequiredService<UiHoverCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-hover-1234", "-a", "TestApp", "--dwell-time", "0"]);
        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeMouse.HoverCalls.Count);
    }

    [TestMethod]
    public async Task Hover_Com_ReturnsError()
    {
        _fakeUia.FindSingleThrow = FakeComException;
        var command = GetRequiredService<UiHoverCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-hover-1234", "-a", "TestApp", "--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Hover_Generic_ReturnsError()
    {
        _fakeTargetResolver.ResolveThrow = FakeGenericException;
        var command = GetRequiredService<UiHoverCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-hover-1234", "-a", "TestApp", "--json"]);
        Assert.AreEqual(1, exitCode);
    }

    // ---------------------------------------------------------------- scroll

    [TestMethod]
    public async Task Scroll_MissingApp_ReturnsError()
    {
        var command = GetRequiredService<UiScrollCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["lst-1234", "--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Scroll_MissingSelector_ReturnsError()
    {
        var command = GetRequiredService<UiScrollCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Scroll_ElementNotFound_ReturnsError()
    {
        _fakeUia.FindSingleResult = null;
        var command = GetRequiredService<UiScrollCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["missing-1234", "-a", "TestApp", "--direction", "down", "--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Scroll_Wheel_ConfirmMoving_ReturnsTargetMoved()
    {
        // Settles on the stability re-read, then drifts on the final confirm read before the wheel.
        const string sel = "lst-drift-1234";
        var seq = new Queue<UiElement?>();
        seq.Enqueue(new UiElement { Id = "e0", Type = "List", Selector = sel, X = 10, Y = 20, Width = 120, Height = 40 }); // initial
        seq.Enqueue(new UiElement { Id = "e0", Type = "List", Selector = sel, X = 10, Y = 20, Width = 120, Height = 40 }); // settles
        seq.Enqueue(new UiElement { Id = "e0", Type = "List", Selector = sel, X = 10, Y = 500, Width = 120, Height = 40 }); // drifted on confirm
        _fakeUia.MovingResults[sel] = seq;

        var command = GetRequiredService<UiScrollCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [sel, "-a", "TestApp", "--wheel", "-3", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMouse.ScrollWheelCalls.Count);
    }

    [TestMethod]
    public async Task Scroll_Wheel_FinalForegroundGateDenies_AbortsWithoutScrolling()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "List", Selector = "lst-1234", X = 10, Y = 20, Width = 120, Height = 40 };
        _fakeForeground.DenyOnCallNumber = 2;

        var command = GetRequiredService<UiScrollCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["lst-1234", "-a", "TestApp", "--wheel", "-3", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMouse.ScrollWheelCalls.Count);
        Assert.AreEqual(2, _fakeForeground.Calls.Count);
    }

    [TestMethod]
    public async Task Scroll_Direction_NonJson_LogsSuccess()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "List", Selector = "lst-1234", X = 10, Y = 20, Width = 120, Height = 40 };
        var command = GetRequiredService<UiScrollCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["lst-1234", "-a", "TestApp", "--direction", "down"]);
        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task Scroll_Com_ReturnsError()
    {
        _fakeUia.FindSingleThrow = FakeComException;
        var command = GetRequiredService<UiScrollCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["lst-1234", "-a", "TestApp", "--direction", "down", "--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Scroll_Generic_ReturnsError()
    {
        _fakeTargetResolver.ResolveThrow = FakeGenericException;
        var command = GetRequiredService<UiScrollCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["lst-1234", "-a", "TestApp", "--direction", "down", "--json"]);
        Assert.AreEqual(1, exitCode);
    }

    // ---------------------------------------------------------------- drag

    [TestMethod]
    public async Task Drag_FromEndpointMoving_ReturnsTargetMoved()
    {
        // <from> is an element selector that never settles → StabilizeAsync("from") aborts the drag.
        const string fromSel = "row-from-moving-1234";
        var seq = new Queue<UiElement?>();
        for (int i = 0; i < 4; i++)
        {
            seq.Enqueue(new UiElement { Id = "e0", Type = "DataItem", Selector = fromSel, X = 10, Y = 20 + i * 60, Width = 80, Height = 24 });
        }
        _fakeUia.MovingResults[fromSel] = seq;

        var command = GetRequiredService<UiDragCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [fromSel, "500,500", "-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMouse.DragCalls.Count);
    }

    [TestMethod]
    public async Task Drag_ToEndpointMoving_ReturnsTargetMoved()
    {
        // <from> is a stable coordinate; <to> is an element selector that never settles.
        const string toSel = "row-to-moving-1234";
        var seq = new Queue<UiElement?>();
        for (int i = 0; i < 4; i++)
        {
            seq.Enqueue(new UiElement { Id = "e1", Type = "DataItem", Selector = toSel, X = 200, Y = 20 + i * 60, Width = 80, Height = 24 });
        }
        _fakeUia.MovingResults[toSel] = seq;

        var command = GetRequiredService<UiDragCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["100,100", toSel, "-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMouse.DragCalls.Count);
    }

    [TestMethod]
    public async Task Drag_FinalForegroundGateDenies_AbortsWithoutDragging()
    {
        // <from> is a static element (so the confirm + final gate block runs); deny the final gate.
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "DataItem", Selector = "row-1234", X = 10, Y = 20, Width = 80, Height = 24, WindowHandle = 4242 };
        _fakeForeground.DenyOnCallNumber = 2;

        var command = GetRequiredService<UiDragCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["row-1234", "500,500", "-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMouse.DragCalls.Count);
        Assert.AreEqual(2, _fakeForeground.Calls.Count);
    }

    [TestMethod]
    public async Task Drag_NonJson_LogsSuccess()
    {
        // Both endpoints are raw coordinates → no element confirm block; straight to the drag + log.
        var command = GetRequiredService<UiDragCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["100,100", "200,200", "-a", "TestApp"]);
        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeMouse.DragCalls.Count);
    }

    [TestMethod]
    public async Task Drag_Com_ReturnsError()
    {
        _fakeUia.FindSingleThrow = FakeComException;
        var command = GetRequiredService<UiDragCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["row-1234", "200,200", "-a", "TestApp", "--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Drag_Generic_ReturnsError()
    {
        _fakeTargetResolver.ResolveThrow = FakeGenericException;
        var command = GetRequiredService<UiDragCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["100,100", "200,200", "-a", "TestApp", "--json"]);
        Assert.AreEqual(1, exitCode);
    }
}
