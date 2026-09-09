// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>The run options that change what the guest is asked to do (spec §"Existing run option semantics").</summary>
/// <param name="NoLaunch">Deploy and register without launching.</param>
/// <param name="WithAlias">Launch through the guest execution alias with forwarded standard streams.</param>
/// <param name="DebugOutput">Run the debug loop inside the guest and stream its output.</param>
/// <param name="UnregisterOnExit">Unregister the guest package once its tracked process exits.</param>
/// <param name="Detach">Return once the guest process has started.</param>
/// <param name="Clean">Clear only this deployment's guest application data before deploying.</param>
/// <param name="Json">Keep the guest's stdout machine-readable.</param>
/// <param name="AppArguments">Arguments for the application itself, forwarded verbatim.</param>
internal sealed record GuestRunOptions(
    bool NoLaunch = false,
    bool WithAlias = false,
    bool DebugOutput = false,
    bool UnregisterOnExit = false,
    bool Detach = false,
    bool Clean = false,
    bool Json = false,
    string? AppArguments = null);

/// <summary>
/// Builds the guest winapp command lines that <c>--on sandbox</c> forwards.
/// </summary>
/// <remarks>
/// Pure translation, deliberately: the guest runs the ordinary <c>winapp run</c> and
/// <c>winapp unregister</c> commands, so the only thing that can be wrong here is which flags are
/// forwarded — and that is exactly what a test can pin without a Sandbox.
/// <para>
/// Every option in the spec's matrix is passed through to the guest rather than reimplemented on the
/// host. A host-side reimplementation of <c>--detach</c> or <c>--unregister-on-exit</c> would be a
/// second definition of those options, free to drift from the one users already rely on locally.
/// </para>
/// </remarks>
internal static class GuestRunPlanner
{
    /// <summary>The guest command that deploys, registers, and launches a packaged application.</summary>
    /// <param name="payloadPath">Guest folder holding the materialized layout the host deployed.</param>
    /// <param name="layoutPath">Guest folder the package is registered from.</param>
    /// <param name="options">Run options to forward.</param>
    /// <remarks>
    /// The layout is passed as <c>--managed-appx-directory</c>, not <c>--output-appx-directory</c>.
    /// Both name where the layout goes; they differ in who owns it. The host created this directory
    /// beside the payload for this deployment, so it is winapp's own and must be kept matching the
    /// payload -- a file dropped from the app has to disappear from it, or the guest would go on
    /// registering content the app no longer has. A directory a user names is theirs and is only
    /// ever added to, which is the right default for a person and the wrong one here.
    /// </remarks>
    public static List<string> BuildRunArguments(string payloadPath, string layoutPath, GuestRunOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(layoutPath);
        ArgumentNullException.ThrowIfNull(options);

        var arguments = new List<string> { "run", payloadPath, "--managed-appx-directory", layoutPath };

        if (options.NoLaunch)
        {
            arguments.Add("--no-launch");
        }

        if (options.WithAlias)
        {
            arguments.Add("--with-alias");
        }

        if (options.DebugOutput)
        {
            arguments.Add("--debug-output");
        }

        if (options.UnregisterOnExit)
        {
            arguments.Add("--unregister-on-exit");
        }

        if (options.Detach)
        {
            arguments.Add("--detach");
        }

        // Clearing the guest package's application data is the guest's business; the host's own
        // --clean additionally discards the deployed payload before reconciling.
        if (options.Clean)
        {
            arguments.Add("--clean");
        }

        if (options.Json)
        {
            arguments.Add("--json");
        }

        if (!string.IsNullOrEmpty(options.AppArguments))
        {
            // --args rather than a '--' separator: the value arrives as one already-quoted string,
            // and splitting it here to re-quote it would be a second parser to disagree with the one
            // the guest already applies.
            arguments.Add("--args");
            arguments.Add(options.AppArguments);
        }

        return arguments;
    }

    /// <summary>
    /// Refuses a combination the guest cannot honour, before anything is built or deployed.
    /// </summary>
    /// <remarks>
    /// Only one case exists, and it is a genuine gap rather than a policy: an unpackaged application
    /// is launched in the guest as a plain process, and there is no guest winapp verb that attaches
    /// the debug loop to one. Reporting that up front is better than deploying, launching, and then
    /// producing no debug output at all.
    /// </remarks>
    public static void EnsureSupportedForUnpackaged(GuestRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.DebugOutput)
        {
            return;
        }

        throw ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.Unsupported,
            "--debug-output is not available for an unpackaged app running in Windows Sandbox.",
            userAction: "Run it without --debug-output, or make the app packaged so guest winapp can debug it.",
            example: "winapp run . --on sandbox");
    }
}
