// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Commands;

/// <summary>
/// How <c>winapp run</c> decides whether to launch a packaged app through its execution alias or through
/// AUMID activation.
/// </summary>
/// <remarks>
/// A packaged app launched by AUMID activation has no console, so a console app runs correctly and prints
/// nothing — a silent success, and the single most confusing thing about running a console app with
/// identity. Launching through an execution alias is an ordinary <c>CreateProcess</c>, so the app inherits
/// this terminal's stdin/stdout/stderr.
/// <para>
/// Because that is what a console app almost always wants, it is the DEFAULT for <c>OutputType=Exe</c>
/// rather than something the user has to know to ask for. A windowed app (<c>WinExe</c>) shows a window
/// and gains nothing from it, so it keeps AUMID activation.
/// </para>
/// </remarks>
internal partial class RunCommand
{
    public partial class Handler
    {
        /// <summary>
        /// Turns the evaluated <c>ProjectAssetsFile</c> path into a file to read the package graph from,
        /// or null when the build did not report one.
        /// </summary>
        /// <remarks>
        /// Existence is deliberately NOT checked here: <see cref="Services.DotNetService"/> falls back to
        /// <c>dotnet list package</c> when the file is missing or unreadable, so a stale or absent path
        /// degrades to the previous behavior rather than failing the run.
        /// </remarks>
        private static FileInfo? ToAssetsFile(string? projectAssetsFile) =>
            string.IsNullOrWhiteSpace(projectAssetsFile) ? null : new FileInfo(projectAssetsFile);

        /// <summary>
        /// The launch mechanism chosen for a run, and the alias name it needs when that mechanism is the
        /// execution alias.
        /// </summary>
        /// <param name="UseAlias">Whether to launch through the execution alias.</param>
        /// <param name="AliasName">
        /// The alias winapp should declare in the manifest it stages. Null when the app already declares
        /// its own, or when alias launch is not being used.
        /// </param>
        /// <param name="Explicit">
        /// Whether the user asked for alias launch by name (<c>--with-alias</c>). An explicit request that
        /// cannot be honored is an error; the default silently falls back to AUMID, because a default must
        /// not turn a run that works today into a failure.
        /// </param>
        internal readonly record struct AliasLaunchDecision(bool UseAlias, string? AliasName, bool Explicit)
        {
            public static AliasLaunchDecision Aumid => new(UseAlias: false, AliasName: null, Explicit: false);
        }

        /// <summary>
        /// Decides how to launch, from the command line, the app's own preference, and its output type.
        /// </summary>
        /// <param name="outputType">Evaluated <c>OutputType</c>; <c>Exe</c> means a console app.</param>
        /// <param name="preferAlias">
        /// The app's own <c>WinAppRunUseExecutionAlias</c> preference, when it declares one.
        /// </param>
        /// <remarks>
        /// <c>--no-launch</c>, <c>--detach</c> and <c>--json</c> each describe a launch the alias cannot
        /// express — nothing is started, nothing is waited on, or stdout must stay machine-readable — so
        /// they keep AUMID regardless of what the app prefers.
        /// <para>
        /// <paramref name="withAlias"/> and <paramref name="withoutAlias"/> are never both set here: the
        /// combination is rejected during option validation, so this only has to honor one of them.
        /// </para>
        /// </remarks>
        internal static AliasLaunchDecision ResolveAliasLaunch(
            bool withAlias,
            bool withoutAlias,
            bool noLaunch,
            bool detach,
            bool isJson,
            string? outputType,
            bool? preferAlias = null)
        {
            if (noLaunch || detach || isJson || withoutAlias)
            {
                return AliasLaunchDecision.Aumid;
            }

            var isConsoleApp = string.Equals(outputType?.Trim(), "Exe", StringComparison.OrdinalIgnoreCase);

            // Precedence: the command line, then the app's declared preference, then the output type.
            var useAlias = withAlias || (preferAlias ?? isConsoleApp);
            if (!useAlias)
            {
                return AliasLaunchDecision.Aumid;
            }

            // The alias NAME is resolved later, from the manifest that is actually registered — the
            // package family it names is not known here for every mode.
            //
            // A DECLARED preference is explicit, exactly like --with-alias: the NuGet targets forward
            // WinAppRunUseExecutionAlias=true as --with-alias, so treating it as a mere default here would
            // make the same property mean different things depending on whether the app was started through
            // `dotnet run` or directly. Only the output-type inference is a default that may degrade.
            return new AliasLaunchDecision(
                UseAlias: true,
                AliasName: null,
                Explicit: withAlias || preferAlias == true);
        }

        /// <summary>
        /// Checks that the alias winapp is about to declare is not already owned by a different package,
        /// BEFORE anything is registered.
        /// </summary>
        /// <remarks>
        /// Windows gives an alias to the first package that claims it and silently ignores later claims,
        /// so registering into a taken name produces an app whose alias launches something else. Checking
        /// first means the run never reaches that state.
        /// <para>
        /// The generated alias is derived from package identity and prefixed, so this should be
        /// unreachable in practice; it exists because the cost of being wrong is launching another app.
        /// An explicitly requested alias fails the run, while the default degrades to AUMID with a warning.
        /// </para>
        /// </remarks>
        private bool TryConfirmAliasIsAvailable(AliasLaunchDecision decision, string? packageFamilyName, bool isJson)
        {
            if (decision.AliasName is not { Length: > 0 } alias)
            {
                return true;
            }

            var proxy = ExecutionAliasResolver.ResolveAliasPath(alias);
            if (proxy is null || !proxy.Exists)
            {
                return true;
            }

            if (ReadAliasOwner(proxy.FullName) is not { } owner)
            {
                // Fail CLOSED. A file exists at the alias path but is not a readable app-exec-link, so
                // launching it would start something whose identity we could not establish while
                // reporting that this package was launched.
                if (decision.Explicit)
                {
                    logger.LogError(
                        "{UISymbol} Could not read which package owns the execution alias '{Alias}' at '{Path}'. Refusing to launch it, since it may belong to another app. Re-run with --without-alias to launch via AUMID.",
                        UiSymbols.Error,
                        alias,
                        proxy.FullName);
                }
                else if (!isJson)
                {
                    // Same user-visible consequence as a known different owner — the app prints nothing —
                    // so it gets the same visibility rather than a Debug line the user will not see.
                    logger.LogWarning(
                        "{UISymbol} Could not read which package owns the execution alias '{Alias}', so this app will launch via AUMID and print nothing to this terminal. Remove '{Path}' if it is stale.",
                        UiSymbols.Warning,
                        alias,
                        proxy.FullName);
                }

                return false;
            }

            if (string.Equals(owner, packageFamilyName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (decision.Explicit)
            {
                logger.LogError(
                    "{UISymbol} Execution alias '{Alias}' already belongs to package '{Owner}'. Windows gives an alias to the first package that claims it, so launching it would run that app instead. Unregister the owning package, or re-run with --without-alias to launch via AUMID.",
                    UiSymbols.Error,
                    alias,
                    owner);
                return false;
            }

            if (!isJson)
            {
                logger.LogWarning(
                    "{UISymbol} Execution alias '{Alias}' already belongs to package '{Owner}', so this app will launch via AUMID and print nothing to this terminal. Unregister the owning package to get console output.",
                    UiSymbols.Warning,
                    alias,
                    owner);
            }

            return false;
        }
    }
}
