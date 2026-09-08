// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

/// <summary>
/// Frame-pool lifetime rules for the Windows Graphics Capture frame grabber. These are pure
/// decisions, so they are covered without a GPU or a live capture session.
/// </summary>
[TestClass]
public class WgcFrameGrabberTests
{
    [TestMethod]
    public void SizeChangeDetection_ResizeTriggersRecreate()
    {
        var poolSize = new global::Windows.Graphics.SizeInt32(800, 600);

        // Resize — different size, non-zero → should recreate.
        var shouldRecreate = WgcCapture.FrameGrabber.ShouldRecreateFramePool(
            poolSize,
            new global::Windows.Graphics.SizeInt32(1024, 768));
        Assert.IsTrue(shouldRecreate, "valid resize must trigger pool recreation");

        // Same size — must not recreate.
        var sameNoRecreate = WgcCapture.FrameGrabber.ShouldRecreateFramePool(
            poolSize,
            new global::Windows.Graphics.SizeInt32(800, 600));
        Assert.IsFalse(sameNoRecreate, "same size must not trigger pool recreation");

        // Zero size — must not recreate (guard against invalid frames).
        var zeroNoRecreate = WgcCapture.FrameGrabber.ShouldRecreateFramePool(
            poolSize,
            new global::Windows.Graphics.SizeInt32(0, 0));
        Assert.IsFalse(zeroNoRecreate, "zero-size frame must not trigger pool recreation");

        // Partial zero — must not recreate.
        var partialZeroNoRecreate = WgcCapture.FrameGrabber.ShouldRecreateFramePool(
            poolSize,
            new global::Windows.Graphics.SizeInt32(0, 600));
        Assert.IsFalse(partialZeroNoRecreate, "zero width must not trigger pool recreation");
    }

    [TestMethod]
    public void PoolRecreate_FrameDisposedBeforeRecreate_OrderingVerified()
    {
        var disposeOrder = new List<string>();

        WgcCapture.FrameGrabber.DisposeFrameBeforeRecreate(
            () => disposeOrder.Add("frame.Dispose"),
            () => disposeOrder.Add("pool.Recreate"));

        Assert.AreEqual("frame.Dispose", disposeOrder[0],
            "frame must be disposed before pool.Recreate (M10 fix)");
        Assert.AreEqual("pool.Recreate", disposeOrder[1],
            "pool.Recreate must execute after frame is disposed");

        var isResize = WgcCapture.FrameGrabber.ShouldRecreateFramePool(
            new global::Windows.Graphics.SizeInt32(800, 600),
            new global::Windows.Graphics.SizeInt32(1024, 768));
        Assert.IsTrue(isResize, "valid resize must still be detected");
    }
}
