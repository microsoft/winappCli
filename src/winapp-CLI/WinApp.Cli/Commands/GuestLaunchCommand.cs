// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Commands;

/// <summary>
/// Verifies a package is registered from exactly the expected layout and launches it -- never
/// registers, unregisters, or otherwise mutates package state (spec §"Coordination between
/// commands").
/// </summary>
/// <remarks>
/// Exists because the ordinary <c>winapp run</c> registers and launches in a single call: after a
/// <c>--on sandbox</c> run's own locked registration phase releases the mutation lease, a second,
/// unrelated <c>--on sandbox</c> run sharing the same package identity but a different layout can
/// register in the gap. If the first run's launch phase were the ordinary <c>run</c>, it would
/// notice the now-mismatched install location and silently fall through to unregister-then-register
/// -- an unlocked package mutation that also disturbs the second run's registration. This verb
/// makes that structurally impossible: it has no code path that calls register or unregister at
/// all, so there is nothing for a mismatch to fall through to. A mismatch is refused outright.
/// <para>
/// This has no <c>--unregister-on-exit</c> option at all, deliberately: unregistering by name alone
/// after the app exits would reintroduce exactly the hazard above, since a different deployment
/// sharing this identity could have registered while this app was running. <c>--unregister-on-exit</c>
/// is instead honored by the host as its own separate, locked, exact-layout-verified phase after this
/// verb returns -- see <c>RunCommand.Sandbox.cs</c>'s <c>UnregisterDeploymentAfterExitAsync</c>.
/// </para>
/// <para>
/// Hidden, like the other guest verbs (<c>guest-agent</c>, <c>guest-runtime</c>): it is an internal
/// step of a host-driven workflow, launched only by <c>RunCommand.Sandbox</c>'s guest exec requests,
/// never something to run by hand, and carries no public schema/docs surface.
/// </para>
/// </remarks>
internal class GuestLaunchCommand : Command, IShortDescription
{
    /// <summary>The hidden guest verb name, forwarded through <c>UseGuestWinapp</c> exec requests.</summary>
    public const string Verb = "guest-launch";

    /// <inheritdoc/>
    public string ShortDescription => "Verify an exact package registration and launch it, without registering or unregistering anything";

    public static Option<string> PackageNameOption { get; } = new("--package-name") { Required = true };

    public static Option<string> PublisherOption { get; } = new("--publisher") { Required = true };

    public static Option<string> ApplicationIdOption { get; } = new("--application-id") { Required = true };

    /// <summary>
    /// The layout the caller's own locked registration phase just registered from. The currently
    /// installed package for the given identity must be registered from exactly this location, or
    /// the launch is refused.
    /// </summary>
    public static Option<DirectoryInfo> ExpectedLayoutOption { get; } = new("--expected-layout") { Required = true };

    /// <summary>Deployed application folder, used only for alias/debug symbol search, never mutated.</summary>
    public static Option<DirectoryInfo> PayloadOption { get; } = new("--payload") { Required = true };

    public static Option<string> TargetSelectorOption { get; } = new("--target-selector") { Required = true };

    public static Option<string> ArgsOption { get; } = new("--args");

    public static Option<bool> WithAliasOption { get; } = new("--with-alias");

    public static Option<bool> DebugOutputOption { get; } = new("--debug-output");

    public static Option<bool> DetachOption { get; } = new("--detach");

    public static Option<bool> SymbolsOption { get; } = new("--symbols");

    /// <summary>Creates the hidden verify-and-launch verb.</summary>
    public GuestLaunchCommand()
        : base(Verb, "Verify an exact package registration and launch it. Internal; not part of the public CLI.")
    {
        // Hidden, like the other guest verbs: an internal step of a host-driven workflow, not
        // something to run by hand, so it stays out of help, completions, and the published schema.
        Hidden = true;

        Options.Add(PackageNameOption);
        Options.Add(PublisherOption);
        Options.Add(ApplicationIdOption);
        Options.Add(ExpectedLayoutOption);
        Options.Add(PayloadOption);
        Options.Add(TargetSelectorOption);
        Options.Add(ArgsOption);
        Options.Add(WithAliasOption);
        Options.Add(DebugOutputOption);
        Options.Add(DetachOption);
        Options.Add(SymbolsOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }
}
