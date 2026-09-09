// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>
/// Builds the guest command line for the hidden guest-launch verb (spec §"Coordination between
/// commands").
/// </summary>
/// <remarks>
/// Pure translation, like <see cref="GuestRunPlanner"/>: the guest runs its own hidden
/// <c>guest-launch</c> verb, so the only thing that can be wrong here is which flags are forwarded.
/// Deliberately separate from <see cref="GuestRunPlanner.BuildRunArguments"/> rather than a third
/// branch inside it: the two verbs have no registration/mutation options in common (no
/// <c>--no-launch</c>, no <c>--clean</c>) and mixing them would make it easy to forward one where it
/// does not belong.
/// </remarks>
internal static class GuestLaunchPlanner
{
    /// <summary>The hidden guest verb name for the verify-and-launch verb.</summary>
    public const string Verb = "guest-launch";

    /// <summary>
    /// The guest command that verifies an exact registration and launches it -- the unlocked half
    /// of a launching packaged run, after registration itself already completed under the mutation
    /// lease.
    /// </summary>
    /// <param name="packageName">Identity name the caller's own registration phase just used.</param>
    /// <param name="publisher">Identity publisher the caller's own registration phase just used.</param>
    /// <param name="applicationId">Application ID from the manifest.</param>
    /// <param name="expectedLayoutPath">
    /// Guest path the currently registered package must be installed from, or the guest refuses to
    /// launch rather than registering or unregistering anything to reconcile a mismatch.
    /// </param>
    /// <param name="payloadPath">Guest folder holding the deployed application files.</param>
    /// <param name="targetSelector">Copyable host-side selector for the execution target.</param>
    /// <param name="options">
    /// Run options to forward. <see cref="GuestRunOptions.NoLaunch"/>, <see cref="GuestRunOptions.Clean"/>,
    /// and <see cref="GuestRunOptions.UnregisterOnExit"/> do not apply to this verb and are ignored:
    /// there is nothing here that ever registers or unregisters, so none of the three has a code path
    /// to affect. <c>--unregister-on-exit</c> is instead honored by the host as its own separate,
    /// locked, exact-layout-verified phase after this verb returns -- see
    /// <c>RunCommand.Sandbox.cs</c>'s <c>UnregisterDeploymentAfterExitAsync</c>.
    /// </param>
    public static List<string> BuildLaunchArguments(
        string packageName,
        string publisher,
        string applicationId,
        string expectedLayoutPath,
        string payloadPath,
        string targetSelector,
        GuestRunOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(publisher);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedLayoutPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSelector);
        ArgumentNullException.ThrowIfNull(options);

        var arguments = new List<string>
        {
            Verb,
            "--package-name", packageName,
            "--publisher", publisher,
            "--application-id", applicationId,
            "--expected-layout", expectedLayoutPath,
            "--payload", payloadPath,
            "--target-selector", targetSelector,
        };

        if (options.WithAlias)
        {
            arguments.Add("--with-alias");
        }

        if (options.DebugOutput)
        {
            arguments.Add("--debug-output");
        }

        // No --unregister-on-exit: GuestLaunchCommand does not accept it at all. Forwarding it
        // here would have no effect other than a parse error, since the verb has no option, and
        // no code path, for it.

        if (options.Detach)
        {
            arguments.Add("--detach");
        }

        if (options.Json)
        {
            arguments.Add("--json");
        }

        if (!string.IsNullOrEmpty(options.AppArguments))
        {
            // --args rather than a '--' separator, matching GuestRunPlanner: the value arrives as
            // one already-quoted string, and splitting it here to re-quote it would be a second
            // parser to disagree with the one the guest already applies.
            arguments.Add("--args");
            arguments.Add(options.AppArguments);
        }

        return arguments;
    }
}
