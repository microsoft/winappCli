// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    [TestMethod]
    public async Task Record_AmbiguousSelector_ReturnsError()
    {
        // FindSingleElementAsync now throws UiAmbiguousSelectorException (not InvalidOperationException)
        // when a plain-text selector matches multiple elements. Record must surface this as exit code 1.
        _fakeRecording.RecordException = new UiAmbiguousSelectorException(
            "Selector matched 3 elements:\n  [0] Button \"OK\" -> btn-ok-a1b2\n  [1] Button \"Cancel\" -> btn-cancel-c3d4\nUse a slug from 'inspect' to target a specific element.");

        var outputPath = Path.Combine(_tempDirectory.FullName, "ambiguous.mp4");
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "OK", "-o", outputPath, "--json"]);

        Assert.AreEqual(1, exitCode, "ambiguous selector must return exit code 1");
        Assert.IsFalse(File.Exists(outputPath), "no output file should be written for ambiguous selectors");
    }

    [TestMethod]
    public async Task Record_AmbiguousSelector_EmitsAmbiguousSelectorCode()
    {
        // M3: the JSON error code for an ambiguous selector must be "ambiguous_selector", not "internal_error".
        _fakeRecording.RecordException = new UiAmbiguousSelectorException(
            "Selector matched 2 elements:\n  [0] Button \"Submit\" -> btn-submit-a1b2\n  [1] Button \"Submit\" -> btn-submit-c3d4\nUse a slug from 'inspect' to target a specific element.");

        var outputPath = Path.Combine(_tempDirectory.FullName, "ambiguous-code.mp4");
        var command = GetRequiredService<UiRecordCommand>();

        var origError = Console.Error;
        var stderrCapture = new System.IO.StringWriter();
        Console.SetError(stderrCapture);
        try
        {
            var exitCode = await ParseAndInvokeWithCaptureAsync(
                command, ["-a", "TestApp", "Submit", "-o", outputPath, "--json"]);
            Assert.AreEqual(1, exitCode, "ambiguous selector must exit 1");

            var stderrText = stderrCapture.ToString();
            // The structured JSON error must appear on stderr with the correct code.
            Assert.IsTrue(stderrText.Contains("ambiguous_selector"),
                $"stderr must contain 'ambiguous_selector' error code; got: {stderrText}");
        }
        finally
        {
            Console.SetError(origError);
        }
    }

    [TestMethod]
    public async Task Record_NoSelector_RecordsWholeWindow()
    {
        // Without a selector, record must capture the whole window (by design, unchanged by H3).
        _fakeRecording.RecordResult = new RecordCaptureResult { Frames = 5, Width = 1280, Height = 720, Mode = "wgc" };

        var outputPath = Path.Combine(_tempDirectory.FullName, "whole-window.mp4");
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--duration-sec", "1", "-o", outputPath, "--json"]);

        Assert.AreEqual(0, exitCode, "whole-window recording (no selector) must succeed");
        Assert.IsTrue(File.Exists(outputPath), "output file must be produced for whole-window recording");
    }

    [TestMethod]
    public void ResolvePopupCaptureHwnd_SameWindow_NoRetarget()
    {
        // When the element's WindowHandle matches the session HWND, the method must return
        // the session HWND unchanged and leave all capture params untouched.
        var targetWindowHwnd = (nint)0x1000;
        int originLeft = 100, originTop = 200, srcW = 500, srcH = 375;

        var result = UiRecordingService.ResolvePopupCaptureHwnd(
            elementWindowHandle: (long)targetWindowHwnd,
            targetWindowHwnd: targetWindowHwnd,
            captureOriginLeft: ref originLeft, captureOriginTop: ref originTop,
            srcWidth: ref srcW, srcHeight: ref srcH);

        Assert.AreEqual(targetWindowHwnd, result, "same-window element must return the target window HWND");
        Assert.AreEqual(100, originLeft, "captureOriginLeft must be unchanged");
        Assert.AreEqual(200, originTop, "captureOriginTop must be unchanged");
        Assert.AreEqual(500, srcW, "srcWidth must be unchanged");
        Assert.AreEqual(375, srcH, "srcHeight must be unchanged");
    }

    [TestMethod]
    public void ResolvePopupCaptureHwnd_NullWindowHandle_NoRetarget()
    {
        // A null WindowHandle must leave all params unchanged and return the session HWND.
        var targetWindowHwnd = (nint)0x1000;
        int originLeft = 100, originTop = 200, srcW = 500, srcH = 375;

        var result = UiRecordingService.ResolvePopupCaptureHwnd(
            elementWindowHandle: null,
            targetWindowHwnd: targetWindowHwnd,
            captureOriginLeft: ref originLeft, captureOriginTop: ref originTop,
            srcWidth: ref srcW, srcHeight: ref srcH);

        Assert.AreEqual(targetWindowHwnd, result, "null WindowHandle must return the target window HWND");
        Assert.AreEqual(100, originLeft, "captureOriginLeft must be unchanged");
        Assert.AreEqual(200, originTop, "captureOriginTop must be unchanged");
        Assert.AreEqual(500, srcW, "srcWidth must be unchanged");
        Assert.AreEqual(375, srcH, "srcHeight must be unchanged");
    }

    [TestMethod]
    public void ResolvePopupCaptureHwnd_PopupWindow_RetargetsToCaptureOriginAndSize()
    {
        // Core H1 regression test: when element's WindowHandle differs from the session HWND,
        // ResolvePopupCaptureHwnd must:
        //   1. Return the popup's top-level HWND (not the session HWND).
        //   2. Update captureOriginLeft/Top to the popup window's screen origin.
        //   3. Update srcWidth/srcHeight to the popup window's pixel dimensions.
        // Before the fix, the crop would be clamped against the main window's frame, producing
        // a truncated sliver (e.g. "width":64,"height":100 instead of 200×100).
        var targetWindowHwnd = (nint)0x1000;
        var popupHwnd = (nint)0x2000;

        // Main window at (100,100), size 500×375.
        int originLeft = 100, originTop = 100, srcW = 500, srcH = 375;

        // Popup window at (550,30), size 220×130 (partly outside main window).
        var result = UiRecordingService.ResolvePopupCaptureHwnd(
            elementWindowHandle: (long)popupHwnd,
            targetWindowHwnd: targetWindowHwnd,
            captureOriginLeft: ref originLeft, captureOriginTop: ref originTop,
            srcWidth: ref srcW, srcHeight: ref srcH,
            getAncestorRoot: h => h,                             // popup IS already the root
            getWindowRect: _ => (550, 30, 770, 160));            // left=550,top=30,right=770,bottom=160

        Assert.AreEqual(popupHwnd, result, "popup element must retarget to popup HWND");
        Assert.AreEqual(550, originLeft, "captureOriginLeft must be updated to popup window's screen-left");
        Assert.AreEqual(30, originTop, "captureOriginTop must be updated to popup window's screen-top");
        Assert.AreEqual(220, srcW, "srcWidth must be updated to popup window's width (770-550)");
        Assert.AreEqual(130, srcH, "srcHeight must be updated to popup window's height (160-30)");
    }

    [TestMethod]
    public void ResolvePopupCaptureHwnd_PopupWindow_CropRectUsesRetargetedOrigin()
    {
        // Regression: after retargeting, an element at screen (600,50) with size 200×100
        // in a popup window at screen (550,30,810,150) must produce cropX=50, cropY=20,
        // cropW=200, cropH=100 — not the clamped sliver from using the main window origin.
        // Without the fix, using the main window's origin (100,100) with srcW=500 would
        // give cropX=499 (clamped), cropW=1 — a 1-pixel sliver.
        var targetWindowHwnd = (nint)0x1000;
        var popupHwnd = (nint)0x2000;
        int originLeft = 100, originTop = 100, srcW = 500, srcH = 375;

        // Popup window at (550,30), size 260×120 — large enough to fully contain the element.
        UiRecordingService.ResolvePopupCaptureHwnd(
            elementWindowHandle: (long)popupHwnd,
            targetWindowHwnd: targetWindowHwnd,
            captureOriginLeft: ref originLeft, captureOriginTop: ref originTop,
            srcWidth: ref srcW, srcHeight: ref srcH,
            getAncestorRoot: h => h,
            getWindowRect: _ => (550, 30, 810, 150));    // width=260, height=120

        // Simulate the crop computation performed in RecordAsync after retarget.
        double elemX = 600, elemY = 50, elemW = 200, elemH = 100;
        var cropX = Math.Clamp((int)elemX - originLeft, 0, Math.Max(0, srcW - 1));
        var cropY = Math.Clamp((int)elemY - originTop, 0, Math.Max(0, srcH - 1));
        var cropW = Math.Clamp((int)elemW, 1, srcW - cropX);
        var cropH = Math.Clamp((int)elemH, 1, srcH - cropY);

        Assert.AreEqual(50, cropX, "cropX after retarget must be element.X(600) - popupOrigin.X(550)");
        Assert.AreEqual(20, cropY, "cropY after retarget must be element.Y(50) - popupOrigin.Y(30)");
        Assert.AreEqual(200, cropW, "cropW must match element width (element fits within popup window)");
        Assert.AreEqual(100, cropH, "cropH must match element height (element fits within popup window)");
    }

    [TestMethod]
    public void ResolvePopupCaptureHwnd_GaRootResolvesToSessionWindow_NoRetarget()
    {
        // If GetAncestor(GA_ROOT) of the element's HWND returns the session HWND, the element's
        // HWND is a child control inside the session window — no retarget is needed.
        var targetWindowHwnd = (nint)0x1000;
        var childHwnd = (nint)0x2000; // a child HWND inside the session window

        int originLeft = 100, originTop = 100, srcW = 500, srcH = 375;

        var result = UiRecordingService.ResolvePopupCaptureHwnd(
            elementWindowHandle: (long)childHwnd,
            targetWindowHwnd: targetWindowHwnd,
            captureOriginLeft: ref originLeft, captureOriginTop: ref originTop,
            srcWidth: ref srcW, srcHeight: ref srcH,
            getAncestorRoot: _ => targetWindowHwnd,            // GA_ROOT resolves to the session window
            getWindowRect: _ => throw new InvalidOperationException("must not be called"));

        Assert.AreEqual(targetWindowHwnd, result, "when GA_ROOT = the target window HWND, no retarget should occur");
        Assert.AreEqual(100, originLeft, "params must be unchanged when GA_ROOT = the target window HWND");
        Assert.AreEqual(500, srcW, "params must be unchanged when GA_ROOT = the target window HWND");
    }

    [TestMethod]
    public void DeriveElementCaptureHwnd_PopupAncestor_RetargetsAndUpdatesRect()
    {
        // H2 regression test: the element's UIA native-window ancestor resolves to a top-level
        // popup/dialog window distinct from the session window. Expected: retarget to that HWND
        // and update the capture origin/size to the popup window's rect.
        var targetWindowHwnd   = (nint)0x1000;
        var popupRootHwnd = (nint)0x2000;

        int originLeft = 100, originTop = 100, srcW = 500, srcH = 375;

        var result = UiRecordingService.DeriveElementCaptureHwnd(
            targetWindowHwnd: targetWindowHwnd,
            captureOriginLeft: ref originLeft, captureOriginTop: ref originTop,
            srcWidth: ref srcW, srcHeight: ref srcH,
            getElementTopLevelHwnd: () => popupRootHwnd,
            getWindowRect:          _ => (550, 30, 770, 160));      // popup rect

        Assert.AreEqual(popupRootHwnd, result,
            "element whose UIA ancestor is a distinct top-level window must retarget to it");
        Assert.AreEqual(550, originLeft, "captureOriginLeft must be updated to popup left");
        Assert.AreEqual(30,  originTop,  "captureOriginTop must be updated to popup top");
        Assert.AreEqual(220, srcW,       "srcWidth must be popup width (770-550)");
        Assert.AreEqual(130, srcH,       "srcHeight must be popup height (160-30)");
    }

    [TestMethod]
    public void DeriveElementCaptureHwnd_SessionAncestor_NoRetarget()
    {
        // Genuine in-window element: its UIA native-window ancestor IS the session window.
        // This is also the overlap-safety case — an unrelated window overlapping the element
        // center is never a UIA ancestor, so the ancestor walk still returns the session window
        // and no retarget occurs (the round-11 geometry/z-order bug is structurally impossible).
        var targetWindowHwnd = (nint)0x1000;
        int originLeft = 100, originTop = 100, srcW = 500, srcH = 375;

        var result = UiRecordingService.DeriveElementCaptureHwnd(
            targetWindowHwnd: targetWindowHwnd,
            captureOriginLeft: ref originLeft, captureOriginTop: ref originTop,
            srcWidth: ref srcW, srcHeight: ref srcH,
            getElementTopLevelHwnd: () => targetWindowHwnd,
            getWindowRect:          _ => throw new InvalidOperationException("must not be called"));

        Assert.AreEqual(targetWindowHwnd, result,
            "element whose top-level is the target window must not retarget");
        Assert.AreEqual(100, originLeft, "params must be unchanged for in-window element");
        Assert.AreEqual(500, srcW,       "params must be unchanged for in-window element");
    }

    [TestMethod]
    public void DeriveElementCaptureHwnd_NoNativeAncestor_NoRetarget()
    {
        // The element could not be re-resolved or has no native-window ancestor (getter returns 0).
        // Expected: leave capture on the session window; do not read a window rect.
        var targetWindowHwnd = (nint)0x1000;
        int originLeft = 100, originTop = 100, srcW = 500, srcH = 375;

        var result = UiRecordingService.DeriveElementCaptureHwnd(
            targetWindowHwnd: targetWindowHwnd,
            captureOriginLeft: ref originLeft, captureOriginTop: ref originTop,
            srcWidth: ref srcW, srcHeight: ref srcH,
            getElementTopLevelHwnd: () => 0,
            getWindowRect:          _ => throw new InvalidOperationException("must not be called"));

        Assert.AreEqual(targetWindowHwnd, result, "no derivable top-level window must not retarget");
        Assert.AreEqual(100, originLeft, "params must be unchanged when no ancestor is derived");
        Assert.AreEqual(500, srcW,       "params must be unchanged when no ancestor is derived");
    }

    [TestMethod]
    public void IsElementOffscreen_ElementFullyInside_ReturnsFalse()
    {
        // Element entirely within the capture surface — must NOT be flagged offscreen.
        Assert.IsFalse(UiRecordingService.IsElementOffscreen(10, 20, 100, 80, 0, 0, 400, 300));
    }

    [TestMethod]
    public void IsElementOffscreen_ElementPartiallyClipped_ReturnsFalse()
    {
        // Element overlaps the right edge of the surface — intersection > 0, NOT offscreen.
        Assert.IsFalse(UiRecordingService.IsElementOffscreen(350, 0, 100, 50, 0, 0, 400, 300));
    }

    [TestMethod]
    public void IsElementOffscreen_SmallOnSurface_ReturnsFalse()
    {
        // Tiny element (10×10) that IS on the capture surface must NOT be flagged offscreen.
        // The encoder-min padding path must still apply for legitimately small elements.
        Assert.IsFalse(UiRecordingService.IsElementOffscreen(5, 5, 10, 10, 0, 0, 400, 300));
    }

    [TestMethod]
    public void IsElementOffscreen_ElementEntirelyRight_ReturnsTrue()
    {
        // Element starts at x=400 in a 400-wide surface → no intersection → offscreen.
        Assert.IsTrue(UiRecordingService.IsElementOffscreen(400, 0, 100, 100, 0, 0, 400, 300));
    }

    [TestMethod]
    public void IsElementOffscreen_ElementEntirelyLeft_ReturnsTrue()
    {
        // Element right edge is at x=-100 → entirely left of surface → offscreen.
        Assert.IsTrue(UiRecordingService.IsElementOffscreen(-200, 0, 100, 100, 0, 0, 400, 300));
    }

    [TestMethod]
    public void IsElementOffscreen_ElementEntirelyBelow_ReturnsTrue()
    {
        // Element top is at y=300 in a 300-tall surface → no intersection → offscreen.
        Assert.IsTrue(UiRecordingService.IsElementOffscreen(0, 300, 100, 100, 0, 0, 400, 300));
    }

    [TestMethod]
    public void IsElementOffscreen_ElementEntirelyAbove_ReturnsTrue()
    {
        // Element bottom is above the surface top → entirely above → offscreen.
        Assert.IsTrue(UiRecordingService.IsElementOffscreen(0, -100, 100, 50, 0, 0, 400, 300));
    }

    [TestMethod]
    public void IsElementOffscreen_ZeroWidth_ReturnsTrue()
    {
        // Degenerate element: zero width → nothing to capture → offscreen.
        Assert.IsTrue(UiRecordingService.IsElementOffscreen(100, 100, 0, 80, 0, 0, 400, 300));
    }

    [TestMethod]
    public void IsElementOffscreen_ZeroHeight_ReturnsTrue()
    {
        // Degenerate element: zero height → nothing to capture → offscreen.
        Assert.IsTrue(UiRecordingService.IsElementOffscreen(100, 100, 200, 0, 0, 0, 400, 300));
    }

    [TestMethod]
    public void IsElementOffscreen_NegativeDimensions_ReturnsTrue()
    {
        // Negative dimensions are degenerate → offscreen.
        Assert.IsTrue(UiRecordingService.IsElementOffscreen(100, 100, -10, -10, 0, 0, 400, 300));
    }

    [TestMethod]
    public void IsElementOffscreen_ReviewerRepro_200x80ButtonOutsidePopup_ReturnsTrue()
    {
        // Reviewer repro: 200×80 button entirely outside its 280×180 owned popup.
        // captureOrigin=(0,0) srcW=280 srcH=180; element at screen (300,10) size 200×80.
        // Before the fix this clamped to a 1-px sliver and recorded garbage at exit 0.
        Assert.IsTrue(UiRecordingService.IsElementOffscreen(300, 10, 200, 80, 0, 0, 280, 180));
    }

    [TestMethod]
    public void IsElementOffscreen_NonZeroOrigin_ElementOutside_ReturnsTrue()
    {
        // Capture surface origin is not (0,0) — element is entirely to the left of the surface.
        // surface: origin(500,400), 280×180; element at screen (100,100) size 200×80 → no intersection.
        Assert.IsTrue(UiRecordingService.IsElementOffscreen(100, 100, 200, 80, 500, 400, 280, 180));
    }

    [TestMethod]
    public void IsElementOffscreen_NonZeroOrigin_ElementInside_ReturnsFalse()
    {
        // Capture surface origin is not (0,0) — element is inside the surface.
        // surface: origin(500,400), 280×180; element at screen (550,450) size 50×60 → inside.
        Assert.IsFalse(UiRecordingService.IsElementOffscreen(550, 450, 50, 60, 500, 400, 280, 180));
    }

    [TestMethod]
    public async Task Record_ElementOffscreen_ReturnsElementNotFoundError()
    {
        // When the service throws UiElementOffscreenException (element resolved but entirely
        // outside the capture surface), the command must exit 1 with element_not_found code
        // and an actionable "offscreen" message — NOT record garbage pixels at exit 0.
        _fakeRecording.RecordException = new UiElementOffscreenException("btn-offscreen-a1b2");

        var outputPath = Path.Combine(_tempDirectory.FullName, "offscreen.mp4");
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "btn-offscreen-a1b2", "-o", outputPath, "--json"]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "offscreen",
            "error message must contain 'offscreen' to guide the user");
        Assert.IsFalse(File.Exists(outputPath),
            "no output file should be written when element is offscreen");
    }
}
