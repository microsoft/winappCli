// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;
using Windows.Win32.Foundation;
using static Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests.CaptureTestWindows;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
[DoNotParallelize]
public class PrintWindowCaptureHelperTests
{
    [TestMethod]
    public void CaptureValidatedPixels_InvalidWindowFailsExplicitly()
    {
        Assert.ThrowsExactly<Win32Exception>(() => PrintWindowCaptureHelper.CaptureValidatedPixels(new HWND(-1), 2, 2));
    }

    [TestMethod]
    [DataRow(false, 0)]
    [DataRow(true, 0)]
    [DataRow(true, 0x5A)]
    [DataRow(true, 0xA5)]
    [DataRow(true, -1)]
    public void CaptureValidatedPixels_RejectsPartialPaintingButAcceptsBlackAndGenuineSeedColors(bool full, int shade)
    {
        WithOwnedPopup((_, window, _) =>
        {
            var attempts = 0;
            byte[] Capture() => PrintWindowCaptureHelper.CaptureValidatedPixels(window, 16, 16, (_, dc) =>
            {
                attempts++;
                for (var y = 0; y < (full ? 16 : 1); y++)
                {
                    for (var x = 0; x < (full ? 16 : 1); x++)
                    {
                        var value = shade < 0 ? ((x + y) % 2 == 0 ? 0x5Au : 0xA5u) : (uint)shade;
                        Assert.AreNotEqual(uint.MaxValue, SetPixel(dc, x, y, value * 0x010101u));
                    }
                }
                return true;
            });
            if (!full)
            {
                var failure = Assert.ThrowsExactly<InvalidOperationException>(() => Capture());
                StringAssert.Contains(failure.Message, "did not paint all pixels");
                Assert.AreEqual(2, attempts);
                return;
            }
            var pixels = Capture();
            Assert.AreEqual(16 * 16 * 4, pixels.Length);
            for (var y = 0; y < 16; y++)
            {
                for (var x = 0; x < 16; x++)
                {
                    var value = shade < 0 ? ((x + y) % 2 == 0 ? (byte)0x5A : (byte)0xA5) : (byte)shade;
                    var index = (y * 16 + x) * 4;
                    CollectionAssert.AreEqual(new byte[] { value, value, value, 255 }, pixels[index..(index + 4)]);
                }
            }
            Assert.AreEqual(shade is 0x5A or -1 ? 2 : 1, attempts);
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CaptureValidatedPixels_NativeFailureOrUnpaintedBitmapFailsExplicitly(bool reportsSuccess)
    {
        if (ForegroundGuard.NoInteractiveDesktop())
        {
            Assert.Inconclusive("Native PrintWindow requires an interactive desktop.");
        }
        using var fixture = new UiaTestFixture();
        PopupWindow? popup = null;
        try
        {
            nint handle = 0;
            fixture.OnUiThread(() =>
            {
                popup = new PopupWindow { FormBorderStyle = FormBorderStyle.None };
                popup.Show(fixture.Form);
                handle = popup.Handle;
            });
            var attempts = 0;
            byte[] Capture() => PrintWindowCaptureHelper.CaptureValidatedPixels(new HWND(handle), 16, 16,
                (_, _) => { attempts++; return reportsSuccess; });
            if (reportsSuccess)
            {
                var failure = Assert.ThrowsExactly<InvalidOperationException>(() => Capture());
                StringAssert.Contains(failure.Message, "did not paint");
                Assert.AreEqual(2, attempts);
            }
            else
            {
                var failure = Assert.ThrowsExactly<Win32Exception>(() => Capture());
                StringAssert.Contains(failure.Message, "PrintWindow");
                Assert.AreEqual(1, attempts);
            }
        }
        finally
        {
            fixture.OnUiThread(() => popup?.Dispose());
        }
    }

    [DllImport("gdi32.dll")]
    private static extern uint SetPixel(global::Windows.Win32.Graphics.Gdi.HDC dc, int x, int y, uint color);
}
