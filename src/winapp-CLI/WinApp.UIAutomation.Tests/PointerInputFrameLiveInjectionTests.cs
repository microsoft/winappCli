// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using Windows.Win32.Foundation;

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

/// <summary>
/// Gated live-injection smoke tests for the native PointerInput path. Normal frame coverage remains
/// in <see cref="PointerInputFrameTests"/> with recorder delegates; these tests only run when the
/// interactive lane explicitly sets WINAPP_UI_INJECTION_LIVE=1.
/// </summary>
[TestClass]
public class PointerInputFrameLiveInjectionTests
{
    private const string LiveInjectionEnvVar = "WINAPP_UI_INJECTION_LIVE";

    [TestMethod]
    [TestCategory("LiveInjection")]
    [TestCategory("InteractiveDesktop")]
    public void LiveInjection_Touch_RealNativePath_Completes_WhenExplicitlyEnabled()
    {
        Process? target = null;
        try
        {
            target = StartLiveInjectionTargetOrSkip();
            var point = CenterForegroundTargetOrSkip(target);
            var contactPaths = new List<IReadOnlyList<PointerPoint>>
            {
                new List<PointerPoint> { point }
            };

            PointerInput.Touch(TouchGesture.Tap, contactPaths, holdMs: 0, durationMs: 0);
        }
        finally
        {
            StopLiveInjectionTarget(target);
        }
    }

    [TestMethod]
    [TestCategory("LiveInjection")]
    [TestCategory("InteractiveDesktop")]
    public void LiveInjection_Pen_RealNativePath_Completes_WhenExplicitlyEnabled()
    {
        Process? target = null;
        try
        {
            target = StartLiveInjectionTargetOrSkip();
            var point = CenterForegroundTargetOrSkip(target);

            PointerInput.Pen([point], pressure: 0.5f, tiltX: 0, tiltY: 0, eraser: false, durationMs: 0);
        }
        finally
        {
            StopLiveInjectionTarget(target);
        }
    }

    private static Process StartLiveInjectionTargetOrSkip()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(LiveInjectionEnvVar), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive($"{LiveInjectionEnvVar}=1 is required for live native pointer injection.");
        }

        if (!Environment.UserInteractive)
        {
            Assert.Inconclusive("No interactive user session is available for live pointer injection.");
        }

        if (ForegroundGuard.IsRemoteSession())
        {
            Assert.Inconclusive("Live pointer injection is skipped in remote sessions.");
        }

        var foreground = global::Windows.Win32.PInvoke.GetForegroundWindow();
        if (foreground.IsNull)
        {
            Assert.Inconclusive("No foreground window is available; the desktop may be locked or headless.");
        }

        var process = Process.Start(new ProcessStartInfo("notepad.exe")
        {
            UseShellExecute = true
        });
        if (process is null)
        {
            Assert.Inconclusive("Could not start the throwaway Notepad target for live pointer injection.");
        }

        try
        {
            try
            {
                process.WaitForInputIdle(3000);
            }
            catch (InvalidOperationException)
            {
                // Some process states do not support WaitForInputIdle; the handle polling below is enough.
            }

            for (int i = 0; i < 30 && process.MainWindowHandle == IntPtr.Zero; i++)
            {
                Thread.Sleep(100);
                process.Refresh();
            }

            if (process.MainWindowHandle == IntPtr.Zero)
            {
                Assert.Inconclusive("The throwaway Notepad target did not create a main window.");
            }

            return process;
        }
        catch
        {
            StopLiveInjectionTarget(process);
            throw;
        }
    }

    private static PointerPoint CenterForegroundTargetOrSkip(Process target)
    {
        var hwnd = new HWND(target.MainWindowHandle);
        global::Windows.Win32.PInvoke.SetForegroundWindow(hwnd);
        Thread.Sleep(250);

        if (!global::Windows.Win32.PInvoke.GetWindowRect(hwnd, out var rect))
        {
            Assert.Inconclusive("Could not read the throwaway target window rectangle.");
        }

        int width = rect.right - rect.left;
        int height = rect.bottom - rect.top;
        if (width <= 0 || height <= 0)
        {
            Assert.Inconclusive("The throwaway target has an empty window rectangle.");
        }

        return new PointerPoint(rect.left + width / 2, rect.top + height / 2);
    }

    private static void StopLiveInjectionTarget(Process? target)
    {
        if (target is null)
        {
            return;
        }

        try
        {
            if (!target.HasExited)
            {
                target.CloseMainWindow();
                if (!target.WaitForExit(1000))
                {
                    target.Kill(entireProcessTree: true);
                    target.WaitForExit(1000);
                }
            }
        }
        catch
        {
            // Best-effort cleanup only; the test result should reflect the injection path.
        }
        finally
        {
            target.Dispose();
        }
    }
}
