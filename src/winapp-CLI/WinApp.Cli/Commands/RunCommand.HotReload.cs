// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;
using WinApp.Cli.Services.HotReload;

namespace WinApp.Cli.Commands;

internal partial class RunCommand
{
    public partial class Handler
    {
        private const int HelperRestartExitCode = 3;

        private readonly HashSet<string> watchNotes = new(StringComparer.Ordinal);

        private volatile TaskCompletionSource restartKey = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// <c>winapp run --watch</c> for a WinUI app with XAML pages (engine B): launches the app the way a DevTools run
        /// does, starts the hot reload helper next to it, and prints the helper's one line per save. When a save needs a
        /// restart, the helper says why and exits, or the developer presses Ctrl+R; the app is stopped, rebuilt and
        /// launched again (packaged apps through their alias), and DevTools attaches again.
        /// </summary>
        /// <param name="launch">Launches one build of the app and waits for it (packaged or not); cancelling stops the app.</param>
        private async Task<int> RunHotReloadAsync(FileInfo csproj, ProjectRunResolution resolution, ProjectRunOptions buildOptions,
            HotReloadCapture capture, Func<ProjectRunResolution, bool, CancellationToken, Task<int>> launch, CancellationToken cancellationToken)
        {
            if (HotReloadCapture.HelperProblem(AppContext.BaseDirectory) is { } problem)
            {
                return Fail(problem, false);
            }
            logger.LogInformation("{Symbol} Watching {Project}: C# edits apply to the running app, and each save says what happened. Press Ctrl+R to restart, Ctrl+C to stop.",
                UiSymbols.Search, csproj.Name);
            ListenForRestartKey();
            for (var iteration = 1; ; iteration++)
            {
                if (iteration > 1 || !capture.Captured)
                {
                    if (await RebuildAsync(csproj, buildOptions, cancellationToken) is not { } rebuilt)
                    {
                        return -1;
                    }
                    resolution = rebuilt;
                }
                // Ctrl+R pressed while it was rebuilding is already answered.
                restartKey = new(TaskCreationOptions.RunContinuationsAsynchronously);
                capture.WriteBuildArguments(ProjectRunService.BuildBuildPassArguments(csproj, resolution.BuildOptions ?? buildOptions, "quiet", resolution.CsWinRTMetadata));
                var (pipe, token, environment) = HotReloadCapture.NewSession();
                watchAppEnvironment = environment;
                watchIteration = iteration;
                using var appRun = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var app = launch(resolution, iteration == 1, appRun.Token);
                using var helper = StartHelper(csproj, capture, pipe, token);
                using var containment = ContainProcess(unchecked((uint)helper.Id));
                var helperExited = helper.WaitForExitAsync(CancellationToken.None);
                var requested = await Task.WhenAny(app, helperExited, restartKey.Task) == restartKey.Task;
                if (requested)
                {
                    restartKey = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    ansiConsole.WriteLine("[watch] restart requested (Ctrl+R)");
                }
                else if (app.IsCompleted || (helperExited.IsCompleted && helper.ExitCode == 0))
                {
                    // The app closed (the helper ends with it), or Ctrl+C.
                    var exitCode = await app;
                    StopHelper(helper);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return -1;
                    }
                    if (exitCode == 0)
                    {
                        return 0;
                    }
                    // It crashed, perhaps on what was just saved: the next save rebuilds and starts it again.
                    ansiConsole.WriteLine($"[watch] waiting the app exited (exit code {exitCode}); save a file or press Ctrl+R to rebuild and start it again");
                    try
                    {
                        var why = await WaitForSaveAsync(csproj, cancellationToken);
                        ansiConsole.WriteLine($"[watch] restart {why ?? "a file was saved after the app exited: rebuilding it"}");
                    }
                    catch (OperationCanceledException)
                    {
                        return -1;
                    }
                    continue;
                }
                else if (helper.ExitCode != HelperRestartExitCode)
                {
                    // The helper stopped on an error (its last line says which): the app keeps running, and the next
                    // save rebuilds and restarts it rather than going unnoticed.
                    ansiConsole.WriteLine("[watch] note: live edits stopped; the next save rebuilds and restarts the app");
                    var saved = WaitForSaveAsync(csproj, appRun.Token);
                    await Task.WhenAny(app, saved);
                    if (app.IsCompleted)
                    {
                        return cancellationToken.IsCancellationRequested ? -1 : await app;
                    }
                    ansiConsole.WriteLine($"[watch] restart {await saved ?? "a file was saved after live edits stopped"}");
                }
                appRun.Cancel();
                await app;
                StopHelper(helper);
            }
        }

        /// <summary>Builds the app again, retrying after each save while the build fails; null when cancelled.</summary>
        private async Task<ProjectRunResolution?> RebuildAsync(FileInfo csproj, ProjectRunOptions buildOptions, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var outcome = await projectRunService.BuildAndResolveAsync(csproj, buildOptions, cancellationToken);
                    if (outcome.Resolution is { } resolution)
                    {
                        return resolution;
                    }
                }
                catch (ProjectRunException ex)
                {
                    logger.LogError("{Symbol} {Message}", UiSymbols.Error, ex.Message);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return null;
                }
                ansiConsole.WriteLine("[watch] waiting the rebuild failed; fix the errors above and save to try again");
                try
                {
                    var why = await WaitForSaveAsync(csproj, cancellationToken);
                    ansiConsole.WriteLine($"[watch] restart {why ?? "a file was saved: rebuilding the app"}");
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }
            return null;
        }

        private Process StartHelper(FileInfo csproj, HotReloadCapture capture, string pipe, string token)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = csproj.DirectoryName!,
            };
            foreach (var argument in new[] { "exec", HotReloadCapture.HelperPath(AppContext.BaseDirectory), "--project", csproj.FullName,
                "--capture", capture.Directory, "--pipe", pipe, "--token", token })
            {
                start.ArgumentList.Add(argument);
            }
            var helper = Process.Start(start) ?? throw new InvalidOperationException("Could not start the hot reload helper.");
            helper.OutputDataReceived += (_, e) => { if (e.Data is { } line) { PrintWatchLine(line); } };
            helper.ErrorDataReceived += (_, e) => { if (e.Data is { } line) { logger.LogDebug("{Line}", line); } };
            helper.BeginOutputReadLine();
            helper.BeginErrorReadLine();
            return helper;
        }

        private static void StopHelper(Process helper)
        {
            if (!helper.HasExited)
            {
                helper.Kill(entireProcessTree: true);
            }
            helper.WaitForExit();
        }

        private void PrintWatchLine(string line)
        {
            // A note about the whole session (C# hot reload unsupported on this SDK) is said once, not after every restart.
            if (line.StartsWith("[watch] note:", StringComparison.Ordinal) && !watchNotes.Add(line))
            {
                return;
            }
            if (Console.IsOutputRedirected)
            {
                // For tools that read the output: one line per outcome, never wrapped.
                ansiConsole.Profile.Out.Writer.WriteLine(line);
                ansiConsole.Profile.Out.Writer.Flush();
                return;
            }
            var color = line.StartsWith("[watch] applied", StringComparison.Ordinal) || line.StartsWith("[watch] ready", StringComparison.Ordinal) ? "green"
                : line.StartsWith("[watch] restart", StringComparison.Ordinal) || line.StartsWith("[watch] waiting", StringComparison.Ordinal) ||
                    line.StartsWith("[watch] not applied", StringComparison.Ordinal) ||
                    line.StartsWith("[watch] note", StringComparison.Ordinal) ? "yellow"
                : line.StartsWith("[watch] error", StringComparison.Ordinal) ? "red"
                : null;
            ansiConsole.MarkupLine(color is null ? Markup.Escape(line) : $"[{color}]{Markup.Escape(line)}[/]");
        }

        /// <summary>
        /// Listens for Ctrl+R for the rest of the session. On redirected input (tools, the watch journeys) the Ctrl+R
        /// character (0x12) counts, as with dotnet watch.
        /// </summary>
        private void ListenForRestartKey()
        {
            new Thread(() =>
            {
                try
                {
                    while (true)
                    {
                        var pressed = Console.IsInputRedirected
                            ? Console.In.Read() switch { < 0 => throw new EndOfStreamException(), var c => c == 0x12 }
                            : Console.ReadKey(intercept: true) is { Key: ConsoleKey.R, Modifiers: ConsoleModifiers.Control };
                        if (pressed)
                        {
                            restartKey.TrySetResult();
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    // No input to read (it ended, or there's no console): saves still restart when they need to.
                }
            }) { IsBackground = true, Name = "watch Ctrl+R" }.Start();
        }

        /// <summary>Completes when a source file of the project is saved (null), or Ctrl+R is pressed (the reason).</summary>
        private async Task<string?> WaitForSaveAsync(FileInfo csproj, CancellationToken cancellationToken)
        {
            var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher = new FileSystemWatcher(csproj.DirectoryName!) { IncludeSubdirectories = true };
            void OnChange(object sender, FileSystemEventArgs e)
            {
                var parts = Path.GetRelativePath(csproj.DirectoryName!, e.FullPath).Split(Path.DirectorySeparatorChar);
                if (Path.GetExtension(e.FullPath).ToLowerInvariant() is ".cs" or ".xaml" or ".csproj" or ".resw" or ".appxmanifest" &&
                    !parts.SkipLast(1).Any(p => p.Equals("bin", StringComparison.OrdinalIgnoreCase) || p.Equals("obj", StringComparison.OrdinalIgnoreCase)))
                {
                    saved.TrySetResult();
                }
            }
            watcher.Changed += OnChange;
            watcher.Created += OnChange;
            watcher.Deleted += OnChange;
            watcher.Renamed += OnChange;
            watcher.EnableRaisingEvents = true;
            if (await Task.WhenAny(saved.Task, restartKey.Task).WaitAsync(cancellationToken) != saved.Task)
            {
                restartKey = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return "requested (Ctrl+R)";
            }
            // Let the editor finish writing, and any files saved with it.
            await Task.Delay(300, cancellationToken);
            return null;
        }
    }
}
