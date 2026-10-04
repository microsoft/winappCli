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
        /// Launches only when the current development registration matches the expected layout.
        /// </summary>
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
            var useAlias = withAlias || parseResult.GetValue(GuestLaunchCommand.PreferAliasOption);
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
            if (GuestLaunchCommand.RegistrationError(packageRegistrationService, packageName, expectedLayout.FullName) is { } registrationError)
            {
                return Fail(registrationError, isJson);
            }

            var packageFullName = appLauncherService.GetPackageFullName(familyName);

            // Guarded exactly like the local (non-sandbox) run's own AUMID activation, which this
            // mirrors: an activation failure is a normal, expected outcome (the app may simply
            // refuse to start), not an unhandled crash, and must still produce the same structured
            // --json error envelope / human-readable message every other launch failure in this
            // command does -- never bare process stderr with no RunCommandResult at all.
            uint processId = 0;
            if (!useAlias)
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
                useAlias, debugOutput, unregisterOnExit: false, detach, useSymbols, isJson,
                familyName, aliasWasRequested: withAlias, targetSelector, cancellationToken);
        }

    }
}
