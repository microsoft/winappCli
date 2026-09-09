// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal partial class RunCommand
{
    public partial class Handler
    {
        /// <summary>
        /// Handles <see cref="GuestLaunchCommand"/>: verifies the currently registered package for
        /// the given identity is installed from exactly <see cref="GuestLaunchCommand.ExpectedLayoutOption"/>,
        /// then launches it. Never registers or unregisters anything -- a mismatch is refused, not
        /// repaired, and <c>--unregister-on-exit</c> is not accepted at all (the host handles it
        /// separately, as its own locked, exact-layout-verified phase after this verb returns; see
        /// <c>RunCommand.Sandbox.cs</c>'s <c>UnregisterDeploymentAfterExitAsync</c>).
        /// </summary>
        /// <remarks>
        /// This method has no code path that calls <see cref="IPackageRegistrationService.InstallPackageAsync"/>,
        /// <see cref="IMsixService.AddLooseLayoutIdentityAsync"/>, or any unregister API. That is
        /// deliberate: it is what makes this verb safe to run entirely without the target mutation
        /// lock. The ordinary <c>winapp run</c> cannot offer the same guarantee, because its
        /// registration and launch are inseparable steps of one call.
        /// </remarks>
        internal async Task<int> InvokeGuestLaunchAsync(
            System.CommandLine.ParseResult parseResult,
            CancellationToken cancellationToken)
        {
            var packageName = parseResult.GetValue(GuestLaunchCommand.PackageNameOption)!;
            var publisher = parseResult.GetValue(GuestLaunchCommand.PublisherOption)!;
            var applicationId = parseResult.GetValue(GuestLaunchCommand.ApplicationIdOption)!;
            var expectedLayout = parseResult.GetValue(GuestLaunchCommand.ExpectedLayoutOption)!;
            var payload = parseResult.GetValue(GuestLaunchCommand.PayloadOption)!;
            var targetSelector = parseResult.GetValue(GuestLaunchCommand.TargetSelectorOption)!;
            var appArgs = parseResult.GetValue(GuestLaunchCommand.ArgsOption);
            var withAlias = parseResult.GetValue(GuestLaunchCommand.WithAliasOption);
            var debugOutput = parseResult.GetValue(GuestLaunchCommand.DebugOutputOption);
            var detach = parseResult.GetValue(GuestLaunchCommand.DetachOption);
            var useSymbols = parseResult.GetValue(GuestLaunchCommand.SymbolsOption);
            var isJson = parseResult.GetValue(WinAppRootCommand.JsonOption);

            var familyName = appLauncherService.ComputePackageFamilyName(packageName, publisher);
            var aumid = $"{familyName}!{applicationId}";

            // Exactly one dev-mode package registered under this name, from exactly the layout the
            // caller's own registration phase just used. Anything else -- zero, more than one, a
            // non-dev-mode registration, or a different install location -- is refused outright.
            // There is no fallback path here that registers or unregisters to "fix" a mismatch.
            var candidates = packageRegistrationService.FindDevPackages(packageName)
                .Where(candidate => candidate.IsDevelopmentMode)
                .ToList();

            if (candidates.Count != 1)
            {
                return Fail(
                    candidates.Count == 0
                        ? $"No development-mode package named '{packageName}' is registered. Expected it registered from '{expectedLayout.FullName}'."
                        : $"{candidates.Count} development-mode packages named '{packageName}' are registered; expected exactly one, from '{expectedLayout.FullName}'.",
                    isJson);
            }

            var candidate = candidates[0];

            if (string.IsNullOrEmpty(candidate.InstallLocation) ||
                !TryPathsMatch(candidate.InstallLocation, expectedLayout.FullName))
            {
                return Fail(
                    $"The package named '{packageName}' is registered from '{candidate.InstallLocation}', " +
                    $"not the expected '{expectedLayout.FullName}'. Another deployment may have re-registered " +
                    "it since this run's own registration phase completed.",
                    isJson);
            }

            var packageFullName = appLauncherService.GetPackageFullName(familyName);

            // Guarded exactly like the local (non-sandbox) run's own AUMID activation, which this
            // mirrors: an activation failure is a normal, expected outcome (the app may simply
            // refuse to start), not an unhandled crash, and must still produce the same structured
            // --json error envelope / human-readable message every other launch failure in this
            // command does -- never bare process stderr with no RunCommandResult at all.
            uint processId = 0;
            if (!withAlias)
            {
                try
                {
                    processId = appLauncherService.LaunchByAumid(aumid, appArgs);
                }
                // IApplicationActivationManager.ActivateApplication has no documented, closed set of
                // failure exception types: the shell surfaces whatever .NET's HRESULT-to-exception
                // mapping produces for that particular failure. A missing package, for example,
                // throws a plain COMException, while a missing file throws FileNotFoundException
                // instead -- and other app-model-specific HRESULTs are free to map to still other
                // built-in types. Narrowing this catch to a fixed exception list would let some real,
                // expected activation failure whose HRESULT happens to map elsewhere escape as an
                // unhandled crash, breaking the guarantee above that every activation failure -- not
                // just the ones on a list -- produces the same structured --json envelope. Only
                // cancellation is excluded, since that is caller-directed shutdown, not an activation
                // failure.
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    logger.LogError("{UISymbol} Failed to launch application: {Message}", UiSymbols.Error, error.Message);

                    if (isJson)
                    {
                        PrintJson(aumid, processId: null, error.Message);
                    }

                    return 1;
                }
            }

            return await LaunchRegisteredApplicationAsync(
                aumid, packageName, packageFullName, expectedLayout, payload, appArgs, processId,
                withAlias, debugOutput, unregisterOnExit: false, detach, useSymbols, isJson,
                targetSelector, cancellationToken);
        }

        /// <summary>
        /// Compares two install-location paths the same way <c>MsixService.SkipRegistration</c>
        /// does: full path, trailing separators trimmed, ordinal-insensitive.
        /// </summary>
        private static bool TryPathsMatch(string installed, string expected)
        {
            try
            {
                var installedFullPath = Path.GetFullPath(installed)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var expectedFullPath = Path.GetFullPath(expected)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                return string.Equals(installedFullPath, expectedFullPath, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Any failure normalizing either path is treated as a mismatch: this verb only ever
                // refuses on uncertainty, it never falls back to registering or unregistering.
                return false;
            }
        }
    }
}
