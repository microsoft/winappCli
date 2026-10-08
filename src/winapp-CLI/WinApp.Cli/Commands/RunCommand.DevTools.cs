// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;
using WinApp.Cli.Services.DevTools;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Commands;

internal partial class RunCommand
{
    internal static Option<string?> GuestInspectorApplicationOption { get; } = new("--guest-inspector-application")
    {
        Hidden = true,
    };

    public static Option<DevToolsMode?> DevToolsOption { get; } = new("--devtools")
    {
        HelpName = "on|off|headless",
        Description = "WinUI XAML inspection: on (the in-app toolbar), headless (nothing drawn in the app) or off. " +
            "On by default for WinUI projects outside CI; change the default with 'winapp devtools default'. " +
            "Packaged apps need App execution aliases enabled. Cannot be combined with --no-launch or --without-alias. " +
            "With --on sandbox, requires a project with XAML sources and does not support --with-alias, --debug-output " +
            "or --unregister-on-exit.",
    };

    public partial class Handler
    {
        private async Task<GuestSourceManifest> BindCoordinatePayloadAsync(
            GuestSourceManifest snapshot, string payloadRoot, CancellationToken cancellationToken)
        {
            if (snapshot.Coordinates is not { Count: > 0 } || snapshot.CoordinatesAdvisory ||
                snapshot.Coordinates.All(file => file.Attribution == "likely")) { return snapshot; }
            var pri = new FileInfo(Path.Combine(payloadRoot, "resources.pri"));
            if (!pri.Exists) { return snapshot; }
            PriXamlResources? resources = null;
            string? error = null;
            await statusService.ExecuteWithStatusAsync("Verifying compiled XAML resources...", async (context, token) =>
            {
                try
                {
                    if (priService is null) { throw new InvalidOperationException("The PRI validation service is unavailable."); }
                    if (PathSafety.HasReparsePointOnPath(pri.FullName, payloadRoot))
                    {
                        throw new InvalidDataException("The deployed PRI is redirected.");
                    }
                    resources = await priService.VerifyXamlResourcesAsync(pri,
                        snapshot.Coordinates.Where(file => file.Attribution != "likely")
                            .ToDictionary(file => file.Resource, file => file.XbfHash, StringComparer.OrdinalIgnoreCase),
                        context, token).ConfigureAwait(false);
                    return (0, "Compiled XAML resources matched on disk.");
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.Xml.XmlException)
                {
                    error = $"Compiled XAML coordinates are unavailable: {ex.Message}";
                    logger.LogWarning("{Message}", error);
                    return (1, error);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    error = "Compiled XAML coordinates are unavailable: PRI validation exceeded 30 seconds.";
                    logger.LogWarning("{Message}", error);
                    return (1, error);
                }
            }, cancellationToken).ConfigureAwait(false);
            return resources is null ? snapshot with { Coordinates = null, CoordinateError = error ?? "Compiled XAML resource validation did not complete." }
                : snapshot with { Coordinates = snapshot.Coordinates.Select(file => file.Attribution == "likely" ? file : file with
                { PriHash = resources.PriHash, PayloadPaths = resources.Paths[file.Resource] }).ToArray() };
        }

        internal Func<string?> ReadCiVariable { get; set; } = () => Environment.GetEnvironmentVariable("CI");

        internal Func<DevToolsMode?> ReadDefaultMode { get; set; } = () => DevToolsDefaultSetting.Read();

        // This run's DevTools mode, resolved once its input is known.
        private DevToolsResolution devToolsRun = new(DevToolsMode.Off, DevToolsModeSource.NotWinUI);

        // Whether --json reports the mode: a WinUI project run or an explicit --devtools. Other runs' JSON is unchanged.
        private bool devToolsReported;

        // Why DevTools the user didn't ask for on the command line stepped aside, for --json.
        private string? devToolsUnavailable;

        private static string JsonName(DevToolsModeSource source) => source switch
        {
            DevToolsModeSource.IncompatibleOption => "option",
            DevToolsModeSource.NotWinUI => "not-winui",
            _ => source.ToString().ToLowerInvariant(),
        };

        private GuestDevToolsRunInfo? WithDevToolsMode(GuestDevToolsRunInfo? info) => !devToolsReported ? info
            : (info ?? new(null, null, null)) with
            {
                Mode = devToolsRun.Mode.ToString().ToLowerInvariant(),
                Source = JsonName(devToolsRun.Source),
                Unavailable = devToolsUnavailable,
            };

        // The one line a run prints when DevTools came from the default rather than the command line.
        internal static string? DefaultDevToolsLine(DevToolsResolution run) => run is { Enabled: true, FailOpen: true }
            ? $"DevTools {run.Mode.ToString().ToLowerInvariant()} ({(run.Source == DevToolsModeSource.Setting ? "your default" : "default")}) · " +
              "turn off: --devtools off or winapp devtools default off"
            : null;

        private void AnnounceDefaultDevTools(bool isJson)
        {
            if (!isJson && DefaultDevToolsLine(devToolsRun) is { } line && logger.IsEnabled(LogLevel.Information))
            {
                ansiConsole.MarkupLineInterpolated($"{UiSymbols.Note} {line}");
            }
        }

        // DevTools the user didn't ask for on this command line steps aside rather than failing the run.
        private void DevToolsStepsAside(string reason, bool isJson)
        {
            devToolsUnavailable = reason;
            DevToolsRunTelemetryScope.SetOutcome(DevToolsOutcome.FellBack);
            if (!isJson)
            {
                logger.LogWarning("{UISymbol} DevTools is unavailable for this run: {Reason}", UiSymbols.Warning, reason);
            }
        }

        internal Func<string?, bool, IReadOnlyDictionary<string, string?>> CreateDevToolsEnvironment { get; set; } =
            DevToolsArtifacts.CreateLaunchEnvironment;

        internal Func<DirectoryInfo, IReadOnlyList<int>> ProcessesRunningFromLayout { get; set; } = RunFailure.ProcessesRunningFromLayout;

        internal Func<string, IReadOnlyList<int>> ProcessesRunningFrom { get; set; } = RunFailure.ProcessesRunningFrom;

        internal Func<int, bool> ProcessHasExited { get; set; } = RunFailure.HasExited;

        // Ends a running instance so a default DevTools run can start the app itself. False when it is still running.
        internal Func<int, bool> CloseRunningProcess { get; set; } = pid =>
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                process.Kill();
                return process.WaitForExit(5000);
            }
            catch (ArgumentException)
            {
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return RunFailure.HasExited(pid);
            }
        };

        // Null when DevTools the user didn't ask for stepped aside before launching anything; the caller launches plainly.
        private async Task<int?> RunInspectorAliasAsync(
            InspectorAlias? alias, DirectoryInfo inputFolder, FileInfo? projectFile, string? aumid, string? arguments,
            bool debugOutput, bool useSymbols, bool detach, bool isJson, bool showOverlay, bool unregisterOnExit,
            string? packageName, string? packageFullName, CancellationToken cancellationToken, bool nativeAot = false,
            IReadOnlyList<string>? sources = null, XamlCompilerArtifacts? compiler = null)
        {
            if (alias?.Target is null || alias.Error is not null)
            {
                var error = alias?.Error ?? "Registration did not provide a verified inspector execution alias.";
                if (devToolsRun.FailOpen)
                {
                    DevToolsStepsAside(error, isJson);
                    return null;
                }
                DevToolsRunTelemetryScope.SetOutcome(DevToolsOutcome.Failed);
                return Fail(error, isJson);
            }

            var launchedAny = false;
            try
            {
                var managed = !nativeAot && (projectFile is not null && !ProjectRunService.IsCppProject(projectFile) ||
                    (alias.Target.TargetExecutable is { } executable && File.Exists(Path.ChangeExtension(executable, ".runtimeconfig.json"))));
                var environment = CreateDevToolsEnvironment(projectFile?.DirectoryName, managed);
                using var coordinates = await XamlSourceCoordinates.XamlCoordinateLaunch.CreateAsync(projectFile, sources, compiler, cancellationToken,
                    (snapshot, token) => BindCoordinatePayloadAsync(snapshot, Path.GetDirectoryName(alias.Target.TargetExecutable!)!, token));
                if (coordinates is not null)
                {
                    environment = coordinates.Apply(environment, Path.GetDirectoryName(alias.Target.TargetExecutable!)!);
                    if (coordinates.Error is { } error) { logger.LogWarning("{Message}", error); }
                    LogSourceExclusions(coordinates.Exclusions);
                }
                var result = await inspectorAliasLauncher.LaunchAsync(alias, arguments, inputFolder.FullName, environment,
                    (detach || isJson) ? LaunchStdioMode.Suppress : LaunchStdioMode.Inherit,
                    cancellationToken: cancellationToken);
                if (result.Process is null)
                {
                    var running = result.Status == InspectorAliasLaunchStatus.Exited && alias.Target.TargetExecutable is { } image
                        ? ProcessesRunningFrom(image) : [];
                    if (running.Count > 0)
                    {
                        if (devToolsRun.FailOpen)
                        {
                            // The app took over the launch: it is running, just without DevTools.
                            DevToolsStepsAside($"the app is already running (PID {string.Join(", ", running)}) and took over this launch.", isJson);
                            if (isJson) { PrintJson(aumid, (uint)running[0], null, coordinates?.Exclusions, coordinates?.Error); }
                            return 0;
                        }
                        return InspectorFailure(aumid, (uint)running[0],
                            $"The app is already running (PID {string.Join(", ", running)}) and took over this launch, " +
                            "so DevTools could not start it. Close it, then run again.", isJson, coordinates);
                    }
                    var error = result.Error ?? $"Inspector launch failed ({result.Status}).";
                    if (devToolsRun.FailOpen && result.Status != InspectorAliasLaunchStatus.Exited)
                    {
                        DevToolsStepsAside(error, isJson);
                        return null;
                    }
                    if (devToolsRun.FailOpen)
                    {
                        DevToolsStepsAside($"the app exited right after launch{(result.ExitCode is int exited ? $" (exit code {exited})" : "")}.", isJson);
                        if (isJson) { PrintJson(aumid, null, null, coordinates?.Exclusions, coordinates?.Error); }
                        return result.ExitCode ?? 0;
                    }
                    if (result.ExitCode is int exitCode)
                    {
                        error += $" Exit code: {exitCode}.";
                    }
                    if (result.Status != InspectorAliasLaunchStatus.Exited)
                    {
                        error += " No existing instance was adopted. Check App execution aliases, or launch normally and use " +
                            "'winapp devtools attach --pid <pid>' without startup-only binding/source instrumentation.";
                    }
                    return InspectorFailure(aumid, result.Status == InspectorAliasLaunchStatus.Exited ? null : result.ProcessId,
                        error, isJson, coordinates);
                }

                launchedAny = true;
                using var launched = result.Process;
                var code = await RunInspectedProcessAsync(launched, aumid, inputFolder.FullName, debugOutput, useSymbols,
                    detach, isJson, showOverlay, cancellationToken, coordinates);
                if (unregisterOnExit && packageName is not null && (launched.HasExited || cancellationToken.IsCancellationRequested))
                {
                    await UnregisterDevPackageAsync(packageName, packageFullName);
                }
                return code;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return -1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                if (devToolsRun.FailOpen && !launchedAny)
                {
                    DevToolsStepsAside($"it could not be prepared: {RunFailure.Describe(ex)}", isJson);
                    return null;
                }
                return InspectorFailure(aumid, null, $"Could not prepare or launch DevTools: {RunFailure.Describe(ex)}", isJson);
            }
        }

        private async Task<int> RunInspectedProcessAsync(
            ILaunchedProcess launched, string? aumid, string symbolDirectory, bool debugOutput, bool useSymbols,
            bool detach, bool isJson, bool showOverlay, CancellationToken cancellationToken,
            XamlSourceCoordinates.XamlCoordinateLaunch? coordinates = null)
        {
            var pid = launched.ProcessId;
            try
            {
                // A failure after launch never relaunches: DevTools the user didn't ask for on the command line
                // reports it and the run carries on with the same process, exit code and wait.
                DevToolsConnection? connection = null;
                string? unavailable = null;
                if (launched.HasExited)
                {
                    unavailable = $"The app exited right after launch (exit code {launched.ExitCode}), before DevTools could inspect it.";
                }
                else
                {
                    connection = await devToolsService.ConnectAsync(pid, showOverlay, DevToolsAccess.Mutation, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!connection.Connected || launched.HasExited)
                    {
                        unavailable = (launched.HasExited
                            ? $"The launched process exited before inspection completed (exit code {launched.ExitCode})."
                            : connection.Error ?? "The requested DevTools overlay did not open.") +
                            $" If process {pid} is still running, inspect it with 'winapp devtools attach --pid {pid}'.";
                        connection = null;
                    }
                    else if (showOverlay && !connection.OverlayShown)
                    {
                        unavailable = $"DevTools attached to process {pid}, but the requested overlay did not open. {connection.OverlayError} " +
                            $"Headless inspection remains available: winapp devtools inspect -a {pid}. " +
                            "For a headless launch, use winapp run --devtools headless.";
                    }
                }
                if (unavailable is not null)
                {
                    if (!devToolsRun.FailOpen)
                    {
                        return InspectorFailure(aumid, pid, unavailable, isJson, coordinates);
                    }
                    DevToolsStepsAside(unavailable, isJson);
                }
                else
                {
                    DevToolsRunTelemetryScope.SetOutcome(DevToolsOutcome.Attached);
                    if (!showOverlay)
                    {
                        devToolsService.PrepareHeadless(pid, cancellationToken);
                    }
                }
                if (launched.HasExited && connection is null)
                {
                    if (isJson) { PrintJson(aumid, pid, null, coordinates?.Exclusions, coordinates?.Error); }
                    return launched.ExitCode;
                }

                if (isJson)
                {
                    PrintJson(aumid, pid, null, coordinates?.Exclusions, coordinates?.Error,
                        connection is null ? null : new(connection.NodeCount, connection.OverlayShown, "local"));
                }
                else if (detach)
                {
                    ansiConsole.WriteLine(pid.ToString());
                    if (connection is not null) { ansiConsole.WriteLine($"Next: winapp devtools inspect -a {pid}"); }
                }
                else if (connection is not null)
                {
                    logger.LogInformation("DevTools connected to process {Pid}: {Count} nodes.", pid, connection.NodeCount);
                }
                if (detach)
                {
                    return 0;
                }

                if (debugOutput)
                {
                    var exitCode = await debugOutputService.RunDebugLoopAsync(pid, cancellationToken, useSymbols,
                        symbolSearchPaths: [symbolDirectory]);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        launched.Kill();
                    }
                    return exitCode;
                }
                return await WaitForLaunchedProcessAsync(launched, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                launched.Kill();
                return -1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                if (!devToolsRun.FailOpen)
                {
                    return InspectorFailure(aumid, pid, $"DevTools initialization failed: {RunFailure.Describe(ex)}",
                        isJson, coordinates);
                }
                DevToolsStepsAside($"DevTools initialization failed: {RunFailure.Describe(ex)}", isJson);
                if (isJson) { PrintJson(aumid, pid, null, coordinates?.Exclusions, coordinates?.Error); }
                return detach ? 0 : await WaitForLaunchedProcessAsync(launched, cancellationToken);
            }
        }
        private void LogSourceExclusions(IReadOnlyList<XamlSourceExclusion>? exclusions)
        {
            foreach (var exclusion in XamlSourceExclusion.Collapse(exclusions) ?? [])
            {
                if (exclusion.Count is int count)
                {
                    logger.LogWarning("Source locations are unavailable for {Count} XAML files: {Reason}", count, exclusion.Reason);
                }
                else
                {
                    logger.LogWarning("Source locations are unavailable for {Source}: {Reason}", exclusion.Source, exclusion.Reason);
                }
            }
        }

        private int InspectorFailure(string? aumid, uint? pid, string message, bool isJson,
            XamlSourceCoordinates.XamlCoordinateLaunch? coordinates = null)
        {
            DevToolsRunTelemetryScope.SetOutcome(DevToolsOutcome.Failed);
            if (isJson)
            {
                PrintJson(aumid, pid, message, coordinates?.Exclusions, coordinates?.Error);
            }
            else
            {
                logger.LogError("{Message}", message);
            }
            return 1;
        }
    }
}
