// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Production implementation — delegates to <see cref="MouseInput"/> static P/Invoke helpers.
/// </summary>
internal class RealMouseInput : IMouseInput
{
    public void Hover(int screenX, int screenY) => MouseInput.Hover(screenX, screenY);

    public void MoveCursor(int screenX, int screenY) => MouseInput.MoveCursor(screenX, screenY);

    public void Click(int screenX, int screenY, bool doubleClick = false, bool rightClick = false, int settleMs = 50)
        => MouseInput.Click(screenX, screenY, doubleClick, rightClick, settleMs);

    public void Drag(int fromScreenX, int fromScreenY, int toScreenX, int toScreenY, bool rightButton = false, int holdMs = 0, int dwellMs = 0, int settleMs = 50)
        => MouseInput.Drag(fromScreenX, fromScreenY, toScreenX, toScreenY, rightButton, holdMs, dwellMs, settleMs);

    public void ScrollWheel(int screenX, int screenY, int delta, int settleMs = 30)
        => MouseInput.ScrollWheel(screenX, screenY, delta, settleMs);
}
