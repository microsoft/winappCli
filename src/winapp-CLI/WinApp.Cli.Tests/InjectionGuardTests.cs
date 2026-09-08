// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// Unit tests for the pure decision logic behind the input-injection guards. The branch selection
/// is extracted from the PInvoke-backed guards so the post-message may-not-deliver warning gate
/// (<see cref="UiSendKeysCommand.Handler.ShouldWarnPostMessageMayNotDeliver"/>) can be verified without
/// a live desktop or a real XAML window.
/// </summary>
[TestClass]
public class InjectionGuardTests
{
    // -----------------------------------------------------------------
    // UiSendKeysCommand.ShouldWarnPostMessageMayNotDeliver — post-message drop warning gate (#655)
    // -----------------------------------------------------------------

    [TestMethod]
    // Warn whenever posting to a XAML target — typed text AND named keys/combos are dropped by the
    // windowless XAML input pipeline (#655 broadened this from text-only to all post-message payloads).
    [DataRow(true, true, true)]
    // send-input transport delivers real keystrokes to XAML → no drop → no warning.
    [DataRow(false, true, false)]
    // Non-XAML target (Win32 / WPF / Electron consume posted messages) → no warning. This scoping
    // avoids false-alarming the majority of apps that DO receive posted input.
    [DataRow(true, false, false)]
    [DataRow(false, false, false)]
    public void ShouldWarnPostMessageMayNotDeliver_OnlyForXamlPostMessage(
        bool isPostMessage, bool targetLooksXaml, bool expected)
    {
        Assert.AreEqual(expected,
            UiSendKeysCommand.Handler.ShouldWarnPostMessageMayNotDeliver(isPostMessage, targetLooksXaml));
    }
}
