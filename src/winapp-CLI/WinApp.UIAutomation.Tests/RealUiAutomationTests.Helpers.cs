// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32.UI.Accessibility;

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

public partial class RealUiAutomationTests
{
    // -----------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------

    private static async Task<double> VerticalPercentAsync(UiAutomationService svc, UiTarget uiTarget, string aid)
    {
        var el = await ResolveAsync(svc, uiTarget, aid);
        var props = await svc.GetPropertiesAsync(uiTarget, el, "ScrollVerticalPercent", CancellationToken.None);
        return Convert.ToDouble(props["ScrollVerticalPercent"], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<double> HorizontalPercentAsync(UiAutomationService svc, UiTarget uiTarget, string aid)
    {
        var el = await ResolveAsync(svc, uiTarget, aid);
        var props = await svc.GetPropertiesAsync(uiTarget, el, "ScrollHorizontalPercent", CancellationToken.None);
        return Convert.ToDouble(props["ScrollHorizontalPercent"], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static UiTarget NonExplicitSession(UiaTestFixture fx) => new()
    {
        ProcessId = fx.ProcessId,
        ProcessName = "WinApp.Cli.Tests",
        WindowHandle = fx.Hwnd,
        WindowTitle = fx.Title,
        IsExplicitWindow = false,
    };

    private static UiTarget PidOnlySession(UiaTestFixture fx) => new()
    {
        ProcessId = fx.ProcessId,
        ProcessName = "WinApp.Cli.Tests",
        WindowHandle = 0,
        WindowTitle = fx.Title,
        IsExplicitWindow = false,
    };

    /// <summary>Polls SearchAsync until at least one result appears (owned window takes a moment to register with UIA).</summary>
    private static async Task<UiElement[]> PollSearchAsync(UiAutomationService svc, UiTarget uiTarget, string query)
    {
        var deadline = Environment.TickCount64 + ReadyTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var results = await svc.SearchAsync(uiTarget, new UiSelector { Query = query }, 20, CancellationToken.None);
            if (results.Length > 0)
            {
                return results;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"Search for '{query}' never returned a result.");
    }

    /// <summary>Polls FindSingleElementAsync (via the other-windows path) until the element resolves.</summary>
    private static async Task<UiElement> PollFindOtherWindowAsync(UiAutomationService svc, UiTarget uiTarget, string query)
    {
        var deadline = Environment.TickCount64 + ReadyTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var found = await svc.FindSingleElementAsync(uiTarget, new UiSelector { Query = query }, CancellationToken.None);
            if (found is not null)
            {
                return found;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"FindSingle for '{query}' never resolved.");
    }

    /// <summary>
    /// Process id of the element that currently owns the system-wide keyboard focus, read via an
    /// independent UIA client (the same COM backend the service uses). Returns null if nothing has
    /// focus. Lets a test discover which process to query so the service's system-wide focused-element
    /// read can be asserted deterministically without fighting the contested foreground.
    /// </summary>
    private static int? FocusedElementProcessId()
    {
        try
        {
            var automation = CUIAutomation8.CreateInstance<IUIAutomation>();
            var focused = automation.GetFocusedElement();
            return focused?.get_CurrentProcessId();
        }
        catch
        {
            return null;
        }
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, string message, int timeoutMs = EffectTimeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (await condition())
            {
                return;
            }
            await Task.Delay(100);
        }
        Assert.Fail(message);
    }

    private static void Foreground(UiaTestFixture fx)
    {
        fx.OnUiThread(() =>
        {
            fx.Form.Activate();
            fx.Form.BringToFront();
        });
        DesktopTestHelpers.ForceForeground(fx.Hwnd);
        // Give the window manager a moment to settle the foreground change.
        Thread.Sleep(200);
    }

    /// <summary>
    /// Verifies a real (non-blank, non-uniform) window render. On hosts where the
    /// global::Windows.Graphics.Capture pipeline hands back a persistently blank/uniform frame
    /// (GPU-less/RDP/locked sessions), the product capture path still executed for coverage,
    /// so the pixel-content check is marked Inconclusive rather than failing — the non-blank
    /// guarantee is validated on the interactive CI runner.
    /// </summary>
    private static void AssertNonBlankRenderOrInconclusive(byte[] pixels)
    {
        if (pixels.Length == 0 || IsBlankOrUniform(pixels))
        {
            Assert.Inconclusive("WGC capture produced a blank/uniform frame on this host; the capture pipeline is unreliable here (pixel content validated on interactive CI).");
        }
    }

    /// <summary>True when the frame is entirely black or a single uniform color (e.g. a WGC first-frame miss).</summary>
    private static bool IsBlankOrUniform(byte[] pixels)
    {
        if (pixels.Length == 0) { return true; }
        var first = pixels[0];
        var allSame = true;
        var allZero = true;
        foreach (var b in pixels)
        {
            if (b != 0) { allZero = false; }
            if (b != first) { allSame = false; }
            if (!allZero && !allSame) { return false; }
        }
        return allZero || allSame;
    }

    /// <summary>
    /// Captures repeatedly (bounded) until a non-blank frame is produced. global::Windows.Graphics.Capture
    /// can hand back a black/uniform first frame while the capture pipeline warms up; retrying a few
    /// times makes the assertion deterministic without a fixed sleep.
    /// </summary>
    private static async Task<(byte[] Pixels, int Width, int Height)> CaptureNonBlankAsync(
        UiaTestFixture fx, Func<Task<(byte[] Pixels, int Width, int Height)>> capture, int attempts = 8)
    {
        (byte[] Pixels, int Width, int Height) last = default;
        for (var i = 0; i < attempts; i++)
        {
            Foreground(fx);
            last = await capture();
            if (!IsBlankOrUniform(last.Pixels))
            {
                return last;
            }
            await Task.Delay(150);
        }
        return last;
    }
}
