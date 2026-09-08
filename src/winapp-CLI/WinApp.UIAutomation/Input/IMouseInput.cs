// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Abstraction over mouse input for testability.
/// Real implementation calls SendInput P/Invoke; fakes record coordinates.
/// </summary>
public interface IMouseInput
{
    /// <summary>
    /// Moves the cursor to the target with a wiggle to trigger hover/tooltip detection.
    /// </summary>
    void Hover(int screenX, int screenY);

    /// <summary>
    /// Moves the cursor to the target without pressing a button. Lets the caller position the pointer,
    /// run its own settle + confirm read, then issue <see cref="Click"/> with <c>settleMs: 0</c> so the
    /// button-down happens immediately after a fresh position check.
    /// </summary>
    void MoveCursor(int screenX, int screenY);

    /// <summary>
    /// Clicks at screen coordinates.
    /// </summary>
    /// <param name="screenX">X position in screen coordinates.</param>
    /// <param name="screenY">Y position in screen coordinates.</param>
    /// <param name="doubleClick">Issue a double click instead of a single one.</param>
    /// <param name="rightClick">Use the right button instead of the left.</param>
    /// <param name="settleMs">Cursor-settle pause (ms) after positioning, before the button-down. Pass
    /// <c>0</c> when the caller already moved the cursor and confirmed the target hasn't moved, so no
    /// extra unguarded settle window reopens the move-during-settle race.</param>
    void Click(int screenX, int screenY, bool doubleClick = false, bool rightClick = false, int settleMs = 50);

    /// <summary>
    /// Presses the mouse button at the from-point, moves to the to-point in steps, then releases.
    /// </summary>
    /// <param name="fromScreenX">X position to press at, in screen coordinates.</param>
    /// <param name="fromScreenY">Y position to press at, in screen coordinates.</param>
    /// <param name="toScreenX">X position to release at, in screen coordinates.</param>
    /// <param name="toScreenY">Y position to release at, in screen coordinates.</param>
    /// <param name="rightButton">Use the right button instead of the left.</param>
    /// <param name="holdMs">Milliseconds to hold the button down at the start before moving. With
    /// <paramref name="fromScreenX"/>/<paramref name="fromScreenY"/> equal to the to-point (no movement)
    /// this performs a press-and-hold / long-press gesture.</param>
    /// <param name="dwellMs">Milliseconds to dwell at the destination after moving, before releasing —
    /// lets drop targets / merge overlays that arm from a sustained hover latch before the button-up.</param>
    /// <param name="settleMs">Cursor-settle pause (ms) after moving to the from-point, before the
    /// button-down. Pass <c>0</c> when the caller already moved the cursor to the from-point and confirmed
    /// the element hasn't moved, so no extra unguarded settle window reopens the move-during-settle race.</param>
    void Drag(int fromScreenX, int fromScreenY, int toScreenX, int toScreenY, bool rightButton = false, int holdMs = 0, int dwellMs = 0, int settleMs = 50);

    /// <summary>
    /// Rotates the mouse wheel at the given screen position. Positive delta scrolls up/away, negative down/toward.
    /// One notch is <c>120</c> units (WHEEL_DELTA).
    /// </summary>
    /// <param name="screenX">X position in screen coordinates.</param>
    /// <param name="screenY">Y position in screen coordinates.</param>
    /// <param name="delta">Wheel movement in WHEEL_DELTA units.</param>
    /// <param name="settleMs">Cursor-settle pause (ms) after positioning, before the wheel rotation. Pass
    /// <c>0</c> when the caller already moved the cursor and confirmed the target is stable.</param>
    void ScrollWheel(int screenX, int screenY, int delta, int settleMs = 30);
}
