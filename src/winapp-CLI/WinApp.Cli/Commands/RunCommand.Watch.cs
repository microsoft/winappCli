// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using Spectre.Console;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal partial class RunCommand
{
    public static Option<bool> WatchOption { get; } = new("--watch")
    {
        Description = "Keep the app running and apply saved .cs, .xaml and project file changes to it live (WinUI 3 .NET projects). " +
            "Changes that can't be applied in place rebuild and restart the app. Uses the DevTools agent even when DevTools is off (then without the toolbar). " +
            "Requires the .NET SDK and a project that builds in a debuggable configuration such as Debug.",
    };

    public partial class Handler
    {
        private WatchSession? activeWatch;

        private static string? ValidateWatch(ParseResult parseResult, ExecutionTargetRef executionTarget, bool noLaunch, bool detach, bool aot) =>
            !executionTarget.IsLocal ? "--watch runs the app on this machine and cannot be combined with --on."
            : noLaunch || detach ? "--watch keeps the app attached and cannot be combined with --no-launch or --detach."
            : aot ? "--watch needs a debuggable build and cannot be combined with --aot."
            : parseResult.GetValue(NoBuildOption) ? "--watch builds the project and cannot be combined with --no-build."
            : parseResult.GetValue(WinAppRootCommand.JsonOption) ? "--watch prints live status lines and cannot be combined with --json."
            : null;

        private IReadOnlyDictionary<string, string?> WithWatchEnvironment(IReadOnlyDictionary<string, string?> environment) =>
            activeWatch?.Augment(environment) ?? environment;

        private async Task<int> RunProjectModeAsync(
            ParseResult parseResult,
            FileInfo csproj,
            FileInfo? solution,
            string? selectionReason,
            string? appArgs,
            bool isJson,
            ExecutionTargetRef executionTarget,
            CancellationToken cancellationToken)
        {
            if (!parseResult.GetValue(WatchOption))
            {
                return await RunProjectModeOnceAsync(parseResult, csproj, solution, selectionReason, appArgs, isJson, executionTarget, cancellationToken);
            }
            if (!File.Exists(WatchSession.HelperPath))
            {
                return Fail($"--watch is unavailable: {Path.GetFileName(WatchSession.HelperPath)} is missing next to winapp.", isJson);
            }

            activeWatch = WatchSession.TryStart(csproj, out var watchingPid);
            if (activeWatch is null)
            {
                return Fail($"{csproj.Name} is already being watched by winapp{(watchingPid is { } pid ? $" (PID {pid})" : "")}. Edit your files there, or stop it first.", isJson);
            }
            try
            {
                while (true)
                {
                    var restarting = activeWatch.RestartRequested;
                    activeWatch.RestartRequested = false;
                    activeWatch.Launched = false;
                    activeWatch.ExitedAtLaunch = false;
                    var buildStarted = DateTime.UtcNow;
                    var exitCode = await RunProjectModeOnceAsync(parseResult, csproj, solution, selectionReason, appArgs, isJson, executionTarget, cancellationToken);
                    if (restarting && !activeWatch.Launched && !cancellationToken.IsCancellationRequested)
                    {
                        if (activeWatch.ExitedAtLaunch)
                        {
                            // Built, but it ends as it starts (markup it can't load, say): relaunching before a fix would only repeat that.
                            ansiConsole.MarkupLine($"[red][[watch]] error the app exited at startup with code {exitCode}; fix it and save to try again[/]");
                            if (activeWatch.CrashReport(exitCode) is { } crash)
                            {
                                ansiConsole.WriteLine("  " + crash.Replace("\n", "\n  ", StringComparison.Ordinal));
                            }
                        }
                        else
                        {
                            // The rebuild for a restart failed (the build already printed why): keep watching and retry on the next save.
                            ansiConsole.MarkupLine("[red][[watch]] error the rebuild failed (see above); fix it and save to try again[/]");
                        }
                        try
                        {
                            await activeWatch.WaitForSourceChangeAsync(buildStarted, cancellationToken);
                        }
                        catch (OperationCanceledException)
                        {
                            return exitCode;
                        }
                        activeWatch.RestartRequested = true;
                        continue;
                    }
                    if (!activeWatch.RestartRequested && activeWatch.Launched && exitCode != 0 && !cancellationToken.IsCancellationRequested)
                    {
                        // The app crashed (closing it normally ends the session): restart it with the next save, or now
                        // when a save it was being sent never reached it.
                        var unapplied = activeWatch.HasUnappliedSave();
                        ansiConsole.MarkupLine(unapplied
                            ? $"[red][[watch]] the app exited with code {exitCode}; restarting it with the change it didn't get[/]"
                            : $"[red][[watch]] the app exited with code {exitCode}; save a file to restart it[/]");
                        if (activeWatch.CrashReport(exitCode) is { } crash)
                        {
                            ansiConsole.WriteLine("  " + crash.Replace("\n", "\n  ", StringComparison.Ordinal));
                        }
                        if (!unapplied)
                        {
                            try
                            {
                                await activeWatch.WaitForSourceChangeAsync(DateTime.UtcNow, cancellationToken);
                            }
                            catch (OperationCanceledException)
                            {
                                return exitCode;
                            }
                        }
                        activeWatch.RestartRequested = true;
                        continue;
                    }
                    if (!activeWatch.RestartRequested || cancellationToken.IsCancellationRequested)
                    {
                        if (activeWatch.Launched && !cancellationToken.IsCancellationRequested)
                        {
                            ansiConsole.MarkupLine("[[watch]] the app was closed; stopped watching");
                        }
                        return exitCode;
                    }
                }
            }
            finally
            {
                activeWatch.DeleteTemporaryFiles();
            }
        }
    }
}
