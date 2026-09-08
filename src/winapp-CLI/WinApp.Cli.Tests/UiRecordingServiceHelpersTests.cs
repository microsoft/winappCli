// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.


namespace WinApp.Cli.Tests;

/// <summary>
/// Pure helpers behind `ui record`: capture retargeting, sizing, offscreen rejection and the
/// frame fast path. They are internal to the recording package, so these tests live here, where
/// the assembly grants InternalsVisibleTo.
/// </summary>
[TestClass]
public class UiRecordingServiceHelpersTests
{
    [TestMethod]
    public void RecordHelpers_CoverRetargetAndSizingBranches()
    {
        var (encoderW, encoderH, displayW, displayH) = UiRecordingService.ComputeTargetSize(100, 401, 99);
        Assert.AreEqual(64, encoderW);
        Assert.AreEqual(98, encoderH);
        Assert.AreEqual(24, displayW);
        Assert.AreEqual(98, displayH);

        var left = 1;
        var top = 2;
        var width = 300;
        var height = 200;
        Assert.AreEqual(10, UiRecordingService.ResolvePopupCaptureHwnd(null, 10, ref left, ref top, ref width, ref height));
        Assert.AreEqual(10, UiRecordingService.ResolvePopupCaptureHwnd(10, 10, ref left, ref top, ref width, ref height));

        var popup = UiRecordingService.ResolvePopupCaptureHwnd(
            44, 10, ref left, ref top, ref width, ref height,
            getAncestorRoot: hwnd => hwnd == 44 ? 0 : hwnd,
            getWindowRect: _ => (7, 8, 9, 10));
        Assert.AreEqual(44, popup);
        Assert.AreEqual((7, 8, 2, 2), (left, top, width, height));

        popup = UiRecordingService.ResolvePopupCaptureHwnd(
            44, 10, ref left, ref top, ref width, ref height,
            getAncestorRoot: _ => 10,
            getWindowRect: _ => throw new AssertFailedException("main-window child must not query rect"));
        Assert.AreEqual(10, popup);

        var derived = UiRecordingService.DeriveElementCaptureHwnd(
            10, ref left, ref top, ref width, ref height,
            getElementTopLevelHwnd: () => 20,
            getWindowRect: _ => (30, 40, 25, 41));
        Assert.AreEqual(20, derived);
        Assert.AreEqual((30, 40, 1, 1), (left, top, width, height));
        Assert.AreEqual(10, UiRecordingService.DeriveElementCaptureHwnd(10, ref left, ref top, ref width, ref height, () => 0));
        Assert.AreEqual(10, UiRecordingService.DeriveElementCaptureHwnd(10, ref left, ref top, ref width, ref height, () => 10));
    }

    [TestMethod]
    public void RecordHelpers_CoverOffscreenAndFrameFastPath()
    {
        Assert.IsTrue(UiRecordingService.IsElementOffscreen(0, 0, 0, 10, 0, 0, 100, 100));
        Assert.IsTrue(UiRecordingService.IsElementOffscreen(200, 0, 10, 10, 0, 0, 100, 100));
        Assert.IsFalse(UiRecordingService.IsElementOffscreen(90, 90, 20, 20, 0, 0, 100, 100));

        var source = Enumerable.Range(0, 2 * 2 * 4).Select(i => (byte)(i + 1)).ToArray();
        var fast = UiRecordingService.ProcessFrame(source, 2, 2, 0, 0, 2, 2, 2, 2, 2, 2);
        Assert.AreSame(source, fast, "native-size whole-frame path should avoid an unnecessary copy");

        var scaled = UiRecordingService.ProcessFrame(source, 2, 2, -5, -5, 50, 50, 64, 64, 32, 32);
        Assert.AreEqual(64 * 64 * 4, scaled.Length);
        Assert.IsTrue(scaled.Any(b => b != 0), "scaled frame must contain source pixels, not just a black letterbox");
        Assert.AreEqual(
            (25, 0, 25, 100),
            UiRecordingService.ClampCropRect(25, 0, 100, 100, 50, 100));
    }


    private static string CreateScratchDirectory()
    {
        var dir = Path.Join(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
