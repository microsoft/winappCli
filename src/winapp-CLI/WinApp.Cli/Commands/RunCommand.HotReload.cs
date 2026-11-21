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

        /// <summary>
        /// <c>winapp run --watch</c> for a WinUI app with XAML pages (engine B): launches the app the way a DevTools run
        /// does, starts the hot reload helper next to it, and prints the helper's one line per save. When a save needs a
        /// restart, the helper says why and exits; the app is stopped, rebuilt and launched again (packaged apps through
        /// their alias), and DevTools attaches again.
        /// </summary>
        /// <param name="launch">Launches one build of the app and waits for it (packaged or not); cancelling stops the app.</param>
        private async Task<int> RunHotReloadAsync(FileInfo csproj, ProjectRunResolution resolution, ProjectRunOptions buildOptions,
            HotReloadCapture capture, Func<ProjectRunResolution, bool, CancellationToken, Task<int>> launch, CancellationToken cancellationToken)
        {
            if (HotReloadCapture.HelperProblem(AppContext.BaseDirectory) is { } problem)
            {
                return Fail(problem, false);
            }
            logger.LogInformation("{Symbol} Watching {Project}: C# edits apply to the running app; other edits restart it and say why. Press Ctrl+C to stop.",
                UiSymbols.Search, csproj.Name);
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
                capture.WriteBuildArguments(ProjectRunService.BuildBuildPassArguments(csproj, resolution.BuildOptions ?? buildOptions, "quiet", resolution.CsWinRTMetadata));
                var (pipe, token, environment) = HotReloadCapture.NewSession();
                watchAppEnvironment = environment;
                watchIteration = iteration;
                using var appRun = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var app = launch(resolution, iteration == 1, appRun.Token);
                using var helper = StartHelper(csproj, capture, pipe, token);
                using var containment = ContainProcess(unchecked((uint)helper.Id));
                var helperExited = helper.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAny(app, helperExited);
                if (app.IsCompleted || (helperExited.IsCompleted && helper.ExitCode == 0))
                {
                    // The app closed (the helper ends with it), or Ctrl+C.
                    var exitCode = await app;
                    StopHelper(helper);
                    return cancellationToken.IsCancellationRequested ? -1 : exitCode;
                }
                if (helper.ExitCode != HelperRestartExitCode)
                {
                    // The helper stopped on an error (its last line says which): the app keeps running, and the next
                    // save rebuilds and restarts it rather than going unnoticed.
                    ansiConsole.WriteLine("[watch] note: live edits stopped; the next save rebuilds and restarts the app");
                    await Task.WhenAny(app, WaitForSaveAsync(csproj, appRun.Token));
                    if (app.IsCompleted)
                    {
                        return cancellationToken.IsCancellationRequested ? -1 : await app;
                    }
                    ansiConsole.WriteLine("[watch] restart a file was saved after live edits stopped");
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
                    await WaitForSaveAsync(csproj, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                ansiConsole.WriteLine("[watch] restart a file was saved: rebuilding the app");
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
                    line.StartsWith("[watch] note", StringComparison.Ordinal) ? "yellow"
                : line.StartsWith("[watch] error", StringComparison.Ordinal) ? "red"
                : null;
            ansiConsole.MarkupLine(color is null ? Markup.Escape(line) : $"[{color}]{Markup.Escape(line)}[/]");
        }

        /// <summary>Completes when a source file of the project is saved.</summary>
        private static async Task WaitForSaveAsync(FileInfo csproj, CancellationToken cancellationToken)
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
            await saved.Task.WaitAsync(cancellationToken);
            // Let the editor finish writing, and any files saved with it.
            await Task.Delay(300, cancellationToken);
        }
    }
}
