// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.


using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

/// <summary>
/// Unit tests for <see cref="PointerInput.MapPenPressure"/> — the pure float→Win32 (1..1024) pen
/// pressure mapping extracted from <see cref="PointerInput.Pen"/> so it is coverable without the
/// native synthetic-pointer device (issue #630). Verifies linear mapping, nearest-integer rounding,
/// clamping at both bounds, and the non-zero-pressure guarantee that in-contact pen frames require.
/// </summary>
[TestClass]
public class PointerInputPressureTests
{
    [TestMethod]
    [DataRow(0.0f, 1u, "0.0 maps to 0, then the in-contact guarantee bumps it up to 1")]
    [DataRow(0.0004f, 1u, "A fraction that rounds to 0 (0.0004*1024 ≈ 0.41) is bumped up to 1")]
    [DataRow(0.25f, 256u, "Interior value maps linearly: 0.25 * 1024 = 256")]
    [DataRow(0.5f, 512u, "Interior value maps linearly: 0.5 * 1024 = 512")]
    [DataRow(1.0f, 1024u, "Full pressure maps to the max: 1.0 * 1024 = 1024")]
    [DataRow(2.0f, 1024u, "Above-range pressure clamps down to the 1024 upper bound")]
    [DataRow(-1.0f, 1u, "Below-range pressure clamps to 0, then is bumped up to 1")]
    public void MapPenPressure_MapsAndClampsToWin32Range(float pressure, uint expected, string because)
    {
        Assert.AreEqual(expected, PointerInput.MapPenPressure(pressure), because);
    }

    [TestMethod]
    public void MapPenPressure_RoundsToNearestInteger_NotTruncates()
    {
        // 0.7 * 1024 = 716.8 -> must round to nearest (717), not truncate toward zero (716).
        Assert.AreEqual(717u, PointerInput.MapPenPressure(0.7f),
            "0.7 * 1024 = 716.8 must round to the nearest integer (717), not truncate to 716");
    }

    [TestMethod]
    public void MapPenPressure_NeverReturnsZero_SoInContactFramesStayInContact()
    {
        // Windows drops an in-contact pen frame that reports zero pressure, so any tiny or
        // non-positive input must still yield at least 1 (the ==0 -> 1 guard).
        Assert.AreEqual(1u, PointerInput.MapPenPressure(0.0f), "zero must not stay zero");
        Assert.AreEqual(1u, PointerInput.MapPenPressure(0.00001f), "a sub-half-unit fraction must not stay zero");
        Assert.AreEqual(1u, PointerInput.MapPenPressure(-5.0f), "a negative pressure clamps to 0 then bumps to 1");
    }
}
