// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.Versioning;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Production implementation — delegates to the <see cref="PointerInput"/> static P/Invoke helpers
/// that drive the Windows touch- and pen-injection APIs.
/// </summary>
/// <remarks>
/// Coverage ceiling (issue #630): this adapter is a one-line pass-through to the native
/// <see cref="PointerInput.Touch"/> / <see cref="PointerInput.Pen"/> P/Invoke helpers, so exercising
/// its two delegating bodies would perform real OS input injection on an unlocked interactive desktop.
/// In the default (headless/shared-CI) test run the DI container substitutes a fake
/// <see cref="IPointerInput"/> so command behavior is asserted without live injection; the live native
/// path is exercised only on a dedicated interactive lane (see the WINAPP_UI_INJECTION_LIVE-gated
/// tests). These two members therefore remain un-coverable in the default run by design.
/// </remarks>
[SupportedOSPlatform("windows10.0.17763.0")]
internal class RealPointerInput : IPointerInput
{
    public void Touch(TouchGesture gesture, IReadOnlyList<IReadOnlyList<PointerPoint>> contactPaths, int holdMs, int durationMs)
        => PointerInput.Touch(gesture, contactPaths, holdMs, durationMs);

    public void Pen(IReadOnlyList<PointerPoint> path, float pressure, int tiltX, int tiltY, bool eraser, int durationMs)
        => PointerInput.Pen(path, pressure, tiltX, tiltY, eraser, durationMs);
}
