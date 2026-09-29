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

    public static Option<bool> DevToolsOption { get; } = new("--devtools")
    {
        Description = "Launch with WinUI XAML inspection and managed binding support. Opens the in-app overlay; use --no-overlay to suppress it. " +
            "Automatically prepares a staged execution alias for packaged apps; the alias must be enabled and verifiable. " +
            "Native AOT apps support native inspection but not managed binding instrumentation. " +
            "Cannot be combined with --no-launch or --without-alias. With --on sandbox, requires a project with XAML sources " +
            "and does not support --with-alias, --debug-output or --unregister-on-exit.",
    };

    public static Option<bool> NoOverlayOption { get; } = new("--no-overlay")
    {
        Description = "Suppress the in-app DevTools overlay. Requires --devtools; independent of --json output.",
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
                    return (0, "Compiled XAML resources matched on disk; running bytes are not verified.");
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

        internal Func<string?, bool, IReadOnlyDictionary<string, string?>> CreateDevToolsEnvironment { get; set; } =
            DevToolsArtifacts.CreateLaunchEnvironment;

        private async Task<int> RunInspectorAliasAsync(
            InspectorAlias? alias, DirectoryInfo inputFolder, FileInfo? projectFile, string? aumid, string? arguments,
            bool debugOutput, bool useSymbols, bool detach, bool isJson, bool showOverlay, bool unregisterOnExit,
            string? packageName, string? packageFullName, CancellationToken cancellationToken, bool nativeAot = false,
            IReadOnlyList<string>? sources = null, XamlCompilerArtifacts? compiler = null)
        {
            if (alias?.Target is null || alias.Error is not null)
            {
                return Fail(alias?.Error ?? "Registration did not provide a verified inspector execution alias.", isJson);
            }

            try
            {
                var managed = !nativeAot && (projectFile is not null ||
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
                    var error = result.Error ?? $"Inspector launch failed ({result.Status}).";
                    if (result.ExitCode is int exitCode)
                    {
                        error += $" Exit code: {exitCode}.";
                    }
                    error += " No existing instance was adopted. Check App execution aliases, or launch normally and use " +
                        "'winapp devtools attach --pid <pid>' without startup-only binding/source instrumentation.";
                    return InspectorFailure(aumid, result.ProcessId, error, isJson, coordinates);
                }

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
                if (launched.HasExited)
                {
                    return InspectorFailure(aumid, pid, $"The launched process exited before inspection (exit code {launched.ExitCode}). " +
                        "No existing instance was adopted. If an earlier instance is still running, close it and retry.", isJson, coordinates);
                }
                var connection = await devToolsService.ConnectAsync(pid, showOverlay, DevToolsAccess.Mutation, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!connection.Connected || launched.HasExited)
                {
                    var reason = launched.HasExited
                        ? $"The launched process exited before inspection completed (exit code {launched.ExitCode})."
                        : connection.Error ?? "The requested DevTools overlay did not open.";
                    return InspectorFailure(aumid, pid, $"{reason} No existing instance was adopted. " +
                        $"If process {pid} is still running, inspect it with 'winapp devtools attach --pid {pid}'.", isJson, coordinates);
                }
                if (showOverlay && !connection.OverlayShown)
                {
                    return InspectorFailure(aumid, pid,
                        $"DevTools attached to process {pid}, but the requested overlay did not open. {connection.OverlayError} " +
                        $"Headless inspection remains available: winapp devtools inspect -a {pid}. " +
                        "For a headless launch, use winapp run --devtools --no-overlay.", isJson, coordinates);
                }

                if (isJson)
                {
                    PrintJson(aumid, pid, null, coordinates?.Exclusions, coordinates?.Error,
                        new(connection.NodeCount, connection.OverlayShown, "local"));
                }
                else if (detach)
                {
                    ansiConsole.WriteLine(pid.ToString());
                }
                else
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
                return InspectorFailure(aumid, pid, $"DevTools initialization failed: {RunFailure.Describe(ex)}",
                    isJson, coordinates);
            }
        }

        private void LogSourceExclusions(IReadOnlyList<XamlSourceExclusion>? exclusions)
        {
            foreach (var exclusion in exclusions ?? [])
            {
                logger.LogWarning("Compiled XAML coordinates excluded for {Source}: {Reason}", exclusion.Source, exclusion.Reason);
            }
        }

        private int InspectorFailure(string? aumid, uint? pid, string message, bool isJson,
            XamlSourceCoordinates.XamlCoordinateLaunch? coordinates = null)
        {
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
