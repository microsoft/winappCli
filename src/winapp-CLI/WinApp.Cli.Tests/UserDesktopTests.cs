// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class UserDesktopTests
{
    [TestMethod]
    [DataRow("Default")]
    [DataRow("default")]
    [DataRow(null)]
    public void UsersDesktopOrUnknown_NeedsNoWarning(string? desktop)
    {
        Assert.IsFalse(UserDesktop.IsHidden(desktop));
        Assert.IsNull(UserDesktop.WarningFor(desktop, uiCommand: false));
        Assert.IsNull(UserDesktop.WarningFor(desktop, uiCommand: true));
    }

    [TestMethod]
    [DataRow(false, "won't be visible to you")]
    [DataRow(true, "can't see windows on your desktop")]
    public void AgentToolDesktop_WarnsWhatGoesWrongForThatCommand(bool uiCommand, string consequence)
    {
        // Google Antigravity runs each command on a private desktop named like this. `run` opens an
        // app the user can't see; `ui` can't see the user's apps.
        var warning = UserDesktop.WarningFor("exebox-3ERQ6T4QWCS6R7AMPTFN5AC5A2", uiCommand);

        Assert.IsNotNull(warning);
        Assert.Contains("exebox-3ERQ6T4QWCS6R7AMPTFN5AC5A2", warning);
        Assert.Contains(consequence, warning);
        Assert.Contains("--on sandbox", warning);
        Assert.DoesNotContain('\n', warning, "The warning is one line.");
    }
}
