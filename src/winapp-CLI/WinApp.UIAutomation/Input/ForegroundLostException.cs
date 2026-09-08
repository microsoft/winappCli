// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Thrown by <see cref="KeyboardInput"/> when the foreground window drifts away from the injection target
/// partway through a throttled SendInput sequence (issue #657 follow-up H1). A long payload is paced over
/// many SendInput calls spanning seconds; SendInput is OS-wide, so continuing after focus leaves the target
/// would type the remaining keystrokes into whatever window grabbed focus. The send-keys command maps this
/// to the <c>foreground_not_target</c> error — the same contract as the pre-send foreground check — rather
/// than a generic failure.
/// </summary>
public sealed class ForegroundLostException : InvalidOperationException
{
    /// <summary>Creates an exception with a message describing how foreground ownership was lost.</summary>
    /// <param name="message">Human-readable description of the foreground-window mismatch.</param>
    public ForegroundLostException(string message) : base(message) { }
}
