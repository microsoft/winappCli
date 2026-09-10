// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Commands;

/// <summary>
/// Verifies an exact package registration and launches it without mutating package state.
/// </summary>
/// <remarks>
/// Runs after the host releases its registration lease. A mismatch is refused, never repaired.
/// Exit cleanup belongs to the host, which rechecks the deployment revision under a new lease.
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

    public static Option<bool> PreferAliasOption { get; } = new("--prefer-alias");

    public static Option<bool> DebugOutputOption { get; } = new("--debug-output");

    public static Option<bool> DetachOption { get; } = new("--detach");

    public static Option<bool> SymbolsOption { get; } = new("--symbols");

    /// <summary>Creates the hidden verify-and-launch verb.</summary>
    public GuestLaunchCommand()
        : base(Verb, "Verify an exact package registration and launch it. Internal; not part of the public CLI.")
    {
        Hidden = true;

        Options.Add(PackageNameOption);
        Options.Add(PublisherOption);
        Options.Add(ApplicationIdOption);
        Options.Add(ExpectedLayoutOption);
        Options.Add(PayloadOption);
        Options.Add(TargetSelectorOption);
        Options.Add(ArgsOption);
        Options.Add(WithAliasOption);
        Options.Add(PreferAliasOption);
        Options.Add(DebugOutputOption);
        Options.Add(DetachOption);
        Options.Add(SymbolsOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }
}
