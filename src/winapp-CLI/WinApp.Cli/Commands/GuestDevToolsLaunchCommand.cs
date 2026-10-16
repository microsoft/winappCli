// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services;
using WinApp.Cli.Services.DevTools;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Commands;

internal sealed class GuestDevToolsLaunchCommand : Command, IShortDescription
{
    internal const string InternalVerb = "guest-devtools-launch";
    public string ShortDescription => "Launch and retain an instrumented guest application";
    internal static Option<FileInfo?> ExecutableOption { get; } = new("--exe");
    internal static Option<string?> PackageOption { get; } = new("--package");
    internal static Option<string?> PublisherOption { get; } = new("--publisher");
    internal static Option<string?> ApplicationIdOption { get; } = new("--application-id");
    internal static Option<DirectoryInfo?> ExpectedLayoutOption { get; } = new("--expected-layout");
    internal static Option<DirectoryInfo> SourceRootOption { get; } = new("--source-root") { Required = true };
    internal static Option<string> SourceHashOption { get; } = new("--source-hash") { Required = true };
    internal static Option<string> ArgumentsOption { get; } = new("--args");
    internal static Option<bool> ManagedOption { get; } = new("--managed");
    internal static Option<bool> NoOverlayOption { get; } = new("--no-overlay");

    public GuestDevToolsLaunchCommand() : base(InternalVerb, "Internal retained guest DevTools launch.")
    {
        Hidden = true;
        Options.Add(ExecutableOption);
        Options.Add(PackageOption);
        Options.Add(PublisherOption);
        Options.Add(ApplicationIdOption);
        Options.Add(ExpectedLayoutOption);
        Options.Add(SourceRootOption);
        Options.Add(SourceHashOption);
        Options.Add(ArgumentsOption);
        Options.Add(ManagedOption);
        Options.Add(NoOverlayOption);
    }
    internal sealed class Handler(IAppLauncherService launcher, IDevToolsService devTools, GuestCommentContext comments,
        Services.DevTools.Comments.ICommentStore store, IPackageRegistrationService registrations,
        InspectorAliasLauncher aliasLauncher) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var executable = parseResult.GetValue(ExecutableOption);
            var package = parseResult.GetValue(PackageOption);
            var source = parseResult.GetValue(SourceRootOption)!;
            if (!Console.IsInputRedirected || !Console.IsOutputRedirected || !source.Exists ||
                (package is null ? executable?.Exists != true : executable is not null))
            {
                await parseResult.InvocationConfiguration.Error.WriteLineAsync("Guest DevTools launch requires an executable, source snapshot and redirected host channel.").ConfigureAwait(false);
                return 1;
            }
            var lifetimeStarted = false;
            try
            {
                using var sources = await GuestSourceSnapshot.OpenReadOnlyAsync(
                    source, parseResult.GetValue(SourceHashOption)!, cancellationToken).ConfigureAwait(false);
                var inventoryPath = Path.Combine(source.FullName, GuestSourceSnapshot.InventoryFileName);
                using var payload = XamlSourceCoordinates.HoldPayload(inventoryPath,
                    package is null ? executable!.DirectoryName! : parseResult.GetValue(ExpectedLayoutOption)?.FullName
                        ?? throw new InvalidOperationException("Guest packaged inspection requires the registered layout."));
                if (payload.Error is { } payloadError) { await parseResult.InvocationConfiguration.Error.WriteLineAsync(payloadError).ConfigureAwait(false); }
                var environment = XamlSourceCoordinates.XamlCoordinateLaunch.Apply(
                    DevToolsArtifacts.CreateLaunchEnvironment(source.FullName, parseResult.GetValue(ManagedOption)),
                    inventoryPath, parseResult.GetValue(SourceHashOption)!, payload.Verified);
                using var app = package is null
                    ? launcher.LaunchExecutable(executable!.FullName, parseResult.GetValue(ArgumentsOption),
                        executable.DirectoryName!, LaunchStdioMode.Suppress, environment)
                    : await LaunchPackageAsync(parseResult, package, environment, cancellationToken).ConfigureAwait(false);
                var owned = GuestInspectedAppLifetime.CaptureIdentity(app);
                try
                {
                    var identity = new GuestProcessStart(checked((int)owned.ProcessId), owned.StartTicksUtc);
                    comments.Activate(identity, source.FullName, cancellationToken);
                    using var input = Console.OpenStandardInput();
                    using var output = Console.OpenStandardOutput();
                    lifetimeStarted = true;
                    return await GuestInspectedAppLifetime.RunAsync(app, identity,
                        devTools, input, output, !parseResult.GetValue(NoOverlayOption), cancellationToken,
                        prepareComments: () => store.Locate(source.FullName), ownedIdentity: owned).ConfigureAwait(false);
                }
                finally
                {
                    if (!lifetimeStarted)
                    {
                        await GuestInspectedAppLifetime.CloseOwnedAsync(app, owned).ConfigureAwait(false);
                    }
                }

            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
                System.ComponentModel.Win32Exception or System.Text.Json.JsonException or System.Xml.XmlException)
            {
                if (!lifetimeStarted)
                {
                    using var output = Console.OpenStandardOutput();
                    await GuestInspectedAppLifetime.ReportFailureAsync(output, null,
                        $"Guest DevTools preparation failed ({ex.GetType().Name}). See the launch diagnostics.log.",
                        cancellationToken).ConfigureAwait(false);
                }
                await parseResult.InvocationConfiguration.Error.WriteLineAsync($"Guest DevTools launch failed: {ex.Message}").ConfigureAwait(false);
                return 1;
            }
        }

        internal async Task<ILaunchedProcess> LaunchPackageAsync(
            ParseResult parsed, string package, IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken)
        {
            var layout = parsed.GetValue(ExpectedLayoutOption);
            var publisher = parsed.GetValue(PublisherOption);
            var applicationId = parsed.GetValue(ApplicationIdOption);
            if (layout is null || string.IsNullOrWhiteSpace(package) ||
                string.IsNullOrWhiteSpace(publisher) || string.IsNullOrWhiteSpace(applicationId))
            {
                throw new InvalidOperationException("Guest packaged inspection requires the exact package, publisher, application and registered layout.");
            }
            if (GuestLaunchCommand.RegistrationError(registrations, package, layout.FullName) is { } error)
            {
                throw new InvalidOperationException(error);
            }
            var document = AppxManifestDocument.Load(Path.Combine(layout.FullName, "AppxManifest.xml"));
            if (document.IdentityName != package || document.IdentityPublisher != publisher)
            {
                throw new InvalidOperationException("The guest inspector manifest no longer matches this deployment's package identity.");
            }
            var target = document.GetExecutionAliasTarget(layout.FullName, applicationId);
            var alias = ExecutionAliasResolver.SelectInspectorAlias(target,
                claimedAliases: document.GetExecutionAliasesClaimedByOtherApplications(applicationId));
            if (alias is null || !document.GetExecutionAliases(applicationId).Contains(alias, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The registered guest layout has no available inspector alias for the intended application. Relaunch to prepare it.");
            }
            var launched = await aliasLauncher.LaunchAsync(new InspectorAlias(alias, target, false),
                parsed.GetValue(ArgumentsOption), layout.FullName, environment, LaunchStdioMode.Suppress,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return launched.Process ?? throw new InvalidOperationException(launched.Error ?? "The guest inspector alias did not launch the intended application.");
        }
    }
}
