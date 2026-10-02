// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Commands;

internal partial class UnregisterCommand
{
    public partial class Handler
    {
        /// <summary>
        /// Removes exactly one managed package registration from the selected target.
        /// </summary>
        /// <remarks>
        /// The host requires a current-generation ownership record whose managed location matches
        /// Windows' actual development registration. The guest then removes that exact package full
        /// name, and the host clears evidence only after a second Windows query proves it is gone.
        /// </remarks>
        private async Task<int> UnregisterOnTargetAsync(
            MsixIdentityResult? identity,
            string? canonicalOwner,
            string? hostLayout,
            FileInfo? manifest,
            bool isJson,
            CancellationToken cancellationToken)
        {
            try
            {
                await using var target = await orchestrator.PrepareAsync(
                    PrepareTargetOptions.Mutating with { RequireInteractiveDesktop = false },
                    cancellationToken);

                var candidates = deploymentStateStore.List(target.Reference)
                    .Where(state => state.IsForEpoch(target.Epoch) && state.Package != null)
                    .ToList();
                var matches = SelectTargetDeployments(candidates, identity, manifest, hostLayout, canonicalOwner);
                if (matches.Count > 1)
                {
                    return FailWith(AmbiguousLayouts(matches.Select(state =>
                        state.Package!.HostLayoutPath ?? state.Package.RegisteredLocation)), isJson);
                }
                if (matches.Count == 0)
                {
                    if (hostLayout != null && candidates.Any(state =>
                        state.Package!.HostLayoutPath is { } path && SamePath(path, hostLayout)))
                    {
                        return FailWith("The selected target layout's recorded owner or identity does not match the app input or --manifest.", isJson);
                    }
                    if (manifest != null && candidates.Any(state => state.Package!.Identity is { } recorded
                        && BelongsToManifest(recorded, manifest)))
                    {
                        return FailWith("The target deployment at this app's path does not match --manifest. Select its app input or --output-appx-directory instead.", isJson);
                    }
                    return ReportNoRegistration(isJson);
                }

                var deployment = matches[0];
                var package = deployment.Package!;
                if (!string.Equals(package.PackageFamilyName,
                    appLauncherService.ComputePackageFamilyName(package.PackageName, package.Publisher),
                    StringComparison.OrdinalIgnoreCase))
                {
                    return FailWith("The target deployment's package family does not match its recorded name and publisher. No package was removed.", isJson);
                }
                if (package.Identity is { } recorded &&
                    (!string.Equals(package.PackageName, recorded.EffectivePackageName, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(package.Publisher, recorded.Publisher, StringComparison.Ordinal)
                    || !string.Equals(package.PackageFamilyName, recorded.PackageFamilyName, StringComparison.OrdinalIgnoreCase)
                    || !TargetPathSafety.PathsEqual(package.RegisteredLocation, recorded.LayoutPath)
                    || (recorded.PackageFullName != null && !string.Equals(
                        package.PackageFullName, recorded.PackageFullName, StringComparison.OrdinalIgnoreCase))))
                {
                    return FailWith("The target deployment's recorded identities disagree. No package was removed.", isJson);
                }

                var unregistered = await guestApplicationRunner.UnregisterOwnedPackageAsync(
                    target,
                    package.PackageName,
                    package.Publisher,
                    package.PackageFamilyName,
                    requiredDeploymentId: deployment.DeploymentId,
                    requiredRevision: deployment.Revision,
                    cancellationToken);

                if (unregistered is null)
                {
                    // Matches the local command: nothing registered is not a failure.
                    if (isJson)
                    {
                        PrintJson([], [], errorMessage: null);
                    }
                    else
                    {
                        logger.LogInformation(
                            "{UISymbol} No package deployed on {Target} for '{PackageName}'.",
                            UiSymbols.Note,
                            target.Reference.Selector,
                            package.PackageName);
                    }

                    return 0;
                }

                if (isJson)
                {
                    PrintJson([unregistered.FullName], [], errorMessage: null);
                }
                else
                {
                    ansiConsole.MarkupLineInterpolated($"{UiSymbols.Check} Unregistered {unregistered.FullName}");
                }

                return 0;
            }
            catch (ExecutionTargetException ex)
            {
                return TargetOutput.Fail(ansiConsole, isJson, ex.Error);
            }
        }

        internal static IReadOnlyList<DeploymentState> SelectTargetDeployments(
            IReadOnlyList<DeploymentState> candidates,
            MsixIdentityResult? identity,
            FileInfo? manifest,
            string? hostLayout,
            string? canonicalOwner = null) =>
            candidates.Where(state => state.Package is { } package
                && (canonicalOwner == null || (package.Identity is { } owned && SamePath(owned.OwnerPath, canonicalOwner)))
                && (hostLayout == null || (package.HostLayoutPath is { } path && SamePath(path, hostLayout)))
                && (identity == null
                    || (package.Identity is { } recorded
                        ? MatchesManifest(recorded, identity)
                            && (hostLayout != null || manifest == null
                                || BelongsToManifest(recorded, manifest)
                                || (package.HostLayoutPath != null && SamePath(package.HostLayoutPath, manifest.DirectoryName!)))
                        : string.Equals(package.PackageName, identity.PackageName, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(package.Publisher, identity.Publisher, StringComparison.Ordinal))))
                .ToList();

    }
}
