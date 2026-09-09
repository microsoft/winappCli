// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Thrown when the foreground window is not the intended target at a moment when acting anyway would
/// affect the wrong window.
/// </summary>
/// <remarks>
/// <para>
/// Raised by <see cref="KeyboardInput"/> when the foreground drifts away from the injection target
/// partway through a throttled SendInput sequence (issue #657 follow-up H1). A long payload is paced
/// over many SendInput calls spanning seconds; SendInput is OS-wide, so continuing after focus leaves
/// the target would type the remaining keystrokes into whatever window grabbed focus.
/// </para>
/// <para>
/// Also raised before a screen-DC capture. <c>SetForegroundWindow</c> is advisory — Windows refuses it
/// under focus-stealing prevention, a UAC prompt, a locked session, or when another app activates
/// itself in the same instant — and a screen capture reads whatever is genuinely in front. Without this
/// check the caller receives a perfectly valid-looking image of an unrelated window and has no way to
/// tell.
/// </para>
/// <para>
/// The <c>send-keys</c> and capture commands map this to the <c>foreground_not_target</c> error — the
/// same contract as the pre-send foreground check — rather than a generic failure.
/// </para>
/// </remarks>
public sealed class ForegroundLostException : InvalidOperationException
{
    /// <summary>Creates an exception with a message describing how foreground ownership was lost.</summary>
    /// <param name="message">Human-readable description of the foreground-window mismatch.</param>
    public ForegroundLostException(string message) : base(message) { }
}
