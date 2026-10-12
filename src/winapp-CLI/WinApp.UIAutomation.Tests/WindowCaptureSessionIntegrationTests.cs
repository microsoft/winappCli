// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;
using Windows.Win32.Foundation;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
[DoNotParallelize]
public class WindowCaptureSessionIntegrationTests
{
    [TestMethod]
    [DataRow(false, 0)]
    [DataRow(true, 0)]
    [DataRow(true, 15)]
    [DataRow(true, 1)]
    public async Task Capture_IncludesOpenPopupAndRemovesItWhenClosed(bool continuous, int fps)
    {
        if (!WgcCapture.IsSupported())
        {
            Assert.Inconclusive("Owned-popup capture requires WGC.");
        }
        if (ForegroundGuard.NoInteractiveDesktop())
        {
            Assert.Inconclusive("Popup rendering requires an unlocked interactive desktop.");
        }

        using var fx = new UiaTestFixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        PopupWindow? popup = null;
        IFrameGrabber? grabber = null;
        long lastMatchedVersion = -1;
        try
        {
            fx.OnUiThread(() =>
            {
                foreach (Control control in fx.Form.Controls)
                {
                    control.Hide();
                }
                fx.Form.BackColor = System.Drawing.Color.Red;
                fx.Form.TopMost = true;
            });

            if (continuous)
            {
                grabber = WgcCapture.StartGrabber(new HWND(fx.Hwnd), NullLogger.Instance, fps);
                Assert.IsTrue(await grabber.WaitForFirstFrameAsync(TimeSpan.FromSeconds(3), timeout.Token));
            }

            await WaitForPopupPixelsAsync(expectedVisible: false);
            fx.OnUiThread(() =>
            {
                var origin = fx.Form.PointToScreen(System.Drawing.Point.Empty);
                popup = new PopupWindow
                {
                    Bounds = new System.Drawing.Rectangle(origin.X + 30, origin.Y + 50, 100, 90),
                };
                popup.Show(fx.Form);
                popup.Refresh();
            });

            await WaitForPopupPixelsAsync(expectedVisible: true);
            fx.OnUiThread(() => popup!.Close());
            await WaitForPopupPixelsAsync(expectedVisible: false);
            fx.OnUiThread(() =>
            {
                popup!.Dispose();
                var origin = fx.Form.PointToScreen(System.Drawing.Point.Empty);
                popup = new PopupWindow
                {
                    Bounds = new System.Drawing.Rectangle(origin.X + 30, origin.Y + 50, 100, 90),
                };
                popup.Show(fx.Form);
                popup.Refresh();
            });
            await WaitForPopupPixelsAsync(expectedVisible: true);
            fx.OnUiThread(() => popup!.Close());
            await WaitForPopupPixelsAsync(expectedVisible: false);
        }
        finally
        {
            grabber?.Dispose();
            fx.OnUiThread(() => popup?.Dispose());
        }

        async Task WaitForPopupPixelsAsync(bool expectedVisible)
        {
            var deadline = Environment.TickCount64 + 3000;
            long version = -1;
            var greenPixels = 0;
            var redPixels = 0;
            do
            {
                timeout.Token.ThrowIfCancellationRequested();
                byte[] pixels;
                if (grabber is not null)
                {
                    var latest = grabber.TryGetLatest();
                    if (latest is null)
                    {
                        await Task.Delay(50, timeout.Token);
                        continue;
                    }
                    pixels = latest.Value.Pixels;
                    version = latest.Value.Version;
                }
                else
                {
                    pixels = (await WgcCapture.CaptureAsync(new HWND(fx.Hwnd), NullLogger.Instance, timeout.Token)).Pixels;
                }
                greenPixels = 0;
                redPixels = 0;
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    if (pixels[i] == 0 && pixels[i + 1] == 255 && pixels[i + 2] == 0)
                    {
                        greenPixels++;
                    }
                    if (pixels[i] == 0 && pixels[i + 1] == 0 && pixels[i + 2] == 255)
                    {
                        redPixels++;
                    }
                }
                if (redPixels >= 1000 &&
                    (expectedVisible ? greenPixels >= 1000 : greenPixels == 0) &&
                    (grabber is null || version > lastMatchedVersion))
                {
                    lastMatchedVersion = version;
                    return;
                }
                await Task.Delay(50, timeout.Token);
            }
            while (Environment.TickCount64 < deadline);

            Assert.Fail($"Popup pixels did not become {(expectedVisible ? "visible" : "absent")} in {(continuous ? "recording" : "screenshot")} capture (fps={fps}, version={version}, previous={lastMatchedVersion}, green={greenPixels}, red={redPixels}).");
        }
    }

    private sealed class PopupWindow : Form
    {
        public PopupWindow()
        {
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = System.Drawing.Color.Lime;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.Style = unchecked((int)0x80000000); // WS_POPUP
                parameters.ExStyle |= 0x08000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                return parameters;
            }
        }
    }

}
