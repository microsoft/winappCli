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
            MsixIdentityResult identity,
            IEnumerable<string> uniqueNames,
            bool isJson,
            CancellationToken cancellationToken)
        {
            try
            {
                await using var target = await orchestrator.PrepareAsync(
                    PrepareTargetOptions.Mutating with { RequireInteractiveDesktop = false },
                    cancellationToken);

                // A --unique-identity run deployed under a derived name instead of the manifest's.
                GuestPackageRegistration? unregistered = null;
                foreach (var name in uniqueNames.Prepend(identity.PackageName))
                {
                    unregistered = await guestApplicationRunner.UnregisterOwnedPackageAsync(
                        target,
                        name,
                        identity.Publisher,
                        appLauncherService.ComputePackageFamilyName(name, identity.Publisher),
                        requiredDeploymentId: null,
                        requiredRevision: null,
                        cancellationToken);
                    if (unregistered is not null)
                    {
                        break;
                    }
                }

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
                            identity.PackageName);
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

    }
}
