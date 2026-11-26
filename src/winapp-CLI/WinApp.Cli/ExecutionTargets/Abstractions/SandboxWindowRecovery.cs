// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.ExecutionTargets.Abstractions;

/// <summary>What to tell a user whose Sandbox has no window they can see.</summary>
/// <remarks>
/// <para>
/// The recovery is a winapp command, never <c>wsb connect</c>. winapp opens the Sandbox window on
/// the user's desktop even when it runs on an agent tool's private desktop, while <c>wsb connect</c>
/// run from such a tool opens the window on that private desktop and hides the Sandbox again.
/// </para>
/// <para>
/// <c>target screenshot</c> is used because it is an existing interactive command with no effect
/// inside the guest: preparing it opens the window when none is visible, and the PNG it saves shows
/// the user that the Sandbox desktop is back. It also needs nothing from the failed command line,
/// which can hold values such as text typed into the app that must not be echoed into an error.
/// </para>
/// </remarks>
internal static class SandboxWindowRecovery
{
    /// <summary>The instruction that accompanies <see cref="NextCommand"/>.</summary>
    public const string UserAction = "Reopen the Sandbox window on your desktop with the command below, then retry.";

    /// <summary>The command that reopens the window.</summary>
    public static ExecutionTargetNextCommand NextCommand { get; } = new()
    {
        Command = @"winapp target screenshot sandbox -o .\sandbox.png",
        Advisory = false,
    };
}
