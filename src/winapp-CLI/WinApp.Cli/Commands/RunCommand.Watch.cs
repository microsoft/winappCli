// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Commands;

internal partial class RunCommand
{
    public partial class Handler
    {
        internal Func<int, IReadOnlyList<(int ProcessId, string ImageName)>> ProcessDescendants { get; set; } = ParentProcessId.Descendants;

        /// <summary>
        /// <c>winapp run --watch</c>: runs the unpackaged project under <c>dotnet watch</c> with the DevTools
        /// launch environment, and attaches DevTools to every app process <c>dotnet watch</c> starts. Hot Reload edits
        /// keep the process (and DevTools); edits that need a restart start a new process, which is attached again.
        /// </summary>
        private async Task<int> RunWatchedProjectAsync(ProjectRunResolution resolution, FileInfo csproj, ProjectRunOptions options,
            string? appArgs, bool showOverlay, CancellationToken cancellationToken)
        {
            var exePath = resolution.RunCommand!;
            var image = Path.GetFileName(exePath);
            if (!string.Equals(Path.GetExtension(image), ".exe", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(image, "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                return Fail("--watch needs an app with its own .exe (UseAppHost=true), so DevTools can tell the app apart from 'dotnet watch'.", false);
            }
            if (await PrepareUnpackagedRuntimeAsync(resolution, csproj, false, cancellationToken) is int runtimeExit)
            {
                return runtimeExit;
            }

            var outputDirectory = Path.GetDirectoryName(exePath)!;
            using var coordinates = await XamlSourceCoordinates.XamlCoordinateLaunch.CreateAsync(csproj, resolution.DevToolsXamlSources,
                resolution.DevToolsCompilerArtifacts, cancellationToken,
                (snapshot, token) => BindCoordinatePayloadAsync(snapshot, outputDirectory, token));
            var environment = ReactorBuildFacts.ApplyEnvironment(CreateDevToolsEnvironment(csproj.DirectoryName, !resolution.IsAot), devToolsReactor);
            if (coordinates is not null)
            {
                environment = coordinates.Apply(environment, outputDirectory);
                if (coordinates.Error is { } coordinateError) { logger.LogWarning("{Message}", coordinateError); }
                LogSourceExclusions(coordinates.Exclusions);
            }
            var watchEnvironment = WatchEnvironment(environment, Environment.GetEnvironmentVariable(DevToolsStartupHooks.EnvVarName));
            string watchArguments;
            try
            {
                // The same Platform/RID the build pass resolved, so dotnet watch builds into, and starts, the same output.
                watchArguments = ProjectRunService.BuildWatchArguments(csproj, ProjectRunService.ResolvePlatformInjection(csproj, options), appArgs);
            }
            catch (ProjectRunException ex)
            {
                return Fail(ex.Message, false);
            }

            return await RunWatcherAsync(csproj, watchArguments, watchEnvironment, async (watcher, token) =>
            {
                var watcherId = unchecked((int)watcher.ProcessId);
                var session = new DevToolsWatchSession(
                    () => ProcessDescendants(watcherId)
                        .Where(process => string.Equals(process.ImageName, image, StringComparison.OrdinalIgnoreCase))
                        .Select(process => process.ProcessId).ToList(),
                    (pid, connectToken) => devToolsService.ConnectAsync(unchecked((uint)pid), showOverlay, DevToolsAccess.Mutation, connectToken),
                    () => watcher.HasExited,
                    pid => !ProcessHasExited(pid))
                {
                    Attached = ReportWatchAttach,
                };
                await session.RunAsync(token);
            }, cancellationToken);
        }

        /// <summary>
        /// <c>winapp run --watch</c> for a packaged project. <c>dotnet watch</c> builds the project and applies Hot
        /// Reload edits as usual, but starts the app through winapp (its <c>RunCommand</c>): each launch re-registers the
        /// loose layout and starts the app through its staged execution alias, which carries both <c>dotnet watch</c>'s
        /// Hot Reload environment and the DevTools environment into the packaged app, then attaches DevTools.
        /// </summary>
        private async Task<int> RunWatchedPackagedProjectAsync(ParseResult parseResult, FileInfo csproj, FileInfo? solution,
            ProjectRunOptions options, string? appArgs, CancellationToken cancellationToken)
        {
            if (WinappExecutable() is not { } winapp)
            {
                return Fail("--watch could not find winapp.exe, which 'dotnet watch' runs to relaunch the packaged app.", false);
            }
            string watchArguments;
            try
            {
                var launchTargets = WriteWatchLaunchTargets(winappDirectoryService.GetGlobalWinappDirectory());
                watchArguments = ProjectRunService.BuildWatchArguments(csproj, ProjectRunService.ResolvePlatformInjection(csproj, options), null,
                [
                    $"CustomAfterMicrosoftCommonTargets={EscapeMSBuildProperty(launchTargets)}",
                    $"WinAppDevToolsWatchRunCommand={EscapeMSBuildProperty(winapp)}",
                    $"WinAppDevToolsWatchRunArguments={WatchLaunchRunArguments(parseResult)}",
                ]);
            }
            catch (ProjectRunException ex)
            {
                return Fail(ex.Message, false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Fail($"--watch could not prepare the packaged app's relaunch: {ex.Message}", false);
            }
            var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [WatchLaunchVariable] = WindowsCommandLine.JoinArguments(WatchLaunchTokens(parseResult, csproj, solution, appArgs, devToolsRun.ShowToolbar)),
            };
            return await RunWatcherAsync(csproj, watchArguments, environment,
                (watcher, token) => watcher.WaitForExitAsync(token), cancellationToken);
        }

        /// <summary>Starts <c>dotnet watch</c> in this terminal and runs <paramref name="whileWatching"/> until it exits or Ctrl+C.</summary>
        private async Task<int> RunWatcherAsync(FileInfo csproj, string watchArguments, IReadOnlyDictionary<string, string?> environment,
            Func<ILaunchedProcess, CancellationToken, Task> whileWatching, CancellationToken cancellationToken)
        {
            ILaunchedProcess watcher;
            try
            {
                watcher = appLauncherService.LaunchExecutable("dotnet", watchArguments,
                    currentDirectoryProvider.GetCurrentDirectory(), LaunchStdioMode.Inherit, environment);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return Fail($"Could not start 'dotnet watch': {RunFailure.Describe(ex)}", false);
            }

            using (watcher)
            {
                logger.LogInformation("{Symbol} Watching {Project}: edits apply through Hot Reload, and DevTools attaches again when the app restarts. Press Ctrl+C to stop.",
                    UiSymbols.Search, csproj.Name);
                try
                {
                    await whileWatching(watcher, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Ctrl+C stops dotnet watch and the app below.
                }
                if (!watcher.HasExited)
                {
                    watcher.Kill();
                    return -1;
                }
                await watcher.WaitForExitAsync(CancellationToken.None);
                return watcher.ExitCode;
            }
        }

        /// <summary>
        /// Carries the packaged watch's run (input, build selection, overlay, app arguments) to each launch step, so
        /// every relaunch resolves and registers the build output exactly as the watching run did. An environment
        /// variable rather than <c>RunArguments</c>: MSBuild splits property values on ';' and ','.
        /// </summary>
        internal const string WatchLaunchVariable = "WINAPP_DEVTOOLS_WATCH_RUN";

        // dotnet watch numbers each app launch from 1, so a higher number is a restart.
        private const string WatchIterationVariable = "DOTNET_WATCH_ITERATION";

        // Set for a launch step: which dotnet watch launch it is.
        private int? watchIteration;

        internal Func<string?> WinappExecutable { get; set; } = () => DevToolsArtifacts.CliExecutablePath;

        internal Func<uint, IDisposable?> ContainProcess { get; set; } = ContainInKillOnCloseJob;

        internal Func<TimeSpan, CancellationToken, Task> WatchDelay { get; set; } = Task.Delay;

        internal TimeSpan LayoutCloseTimeout { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>The run a packaged watch's launch step repeats: no build (dotnet watch built it), DevTools on.</summary>
        internal static IReadOnlyList<string> WatchLaunchTokens(ParseResult parseResult, FileInfo csproj, FileInfo? solution, string? appArgs,
            bool showToolbar)
        {
            var tokens = new List<string> { solution?.FullName ?? csproj.FullName };
            void Add(string option, string? value)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    tokens.Add(option);
                    tokens.Add(value);
                }
            }
            if (solution is not null)
            {
                Add("--project", csproj.FullName);
            }
            tokens.Add("--no-build");
            tokens.Add("--devtools");
            tokens.Add(showToolbar ? "on" : "headless");
            Add("--configuration", parseResult.GetValue(ConfigurationOption));
            Add("--arch", parseResult.GetValue(ArchOption));
            Add("--runtime", parseResult.GetValue(RuntimeOption));
            Add("--framework", parseResult.GetValue(FrameworkOption));
            foreach (var property in parseResult.GetValue(PropertyOption) ?? [])
            {
                Add("--property", property);
            }
            Add("--manifest", parseResult.GetValue(ManifestOption)?.FullName);
            Add("--output-appx-directory", parseResult.GetValue(OutputAppXDirectoryOption)?.FullName);
            Add("--executable", parseResult.GetValue(ExecutableOption));
            if (!string.IsNullOrEmpty(appArgs))
            {
                tokens.Add("--");
                tokens.AddRange(WindowsCommandLine.SplitArguments(appArgs));
            }
            return tokens;
        }

        // Only switches without values: the rest of the run travels in WatchLaunchVariable.
        private static string WatchLaunchRunArguments(ParseResult parseResult)
        {
            var arguments = $"run {WatchLaunchOption.Name}";
            if (parseResult.GetResult(WinAppRootCommand.VerboseOption) is { } verbose && verbose.GetValueOrDefault<bool>()) { arguments += " --verbose"; }
            if (parseResult.GetResult(WinAppRootCommand.QuietOption) is { } quiet && quiet.GetValueOrDefault<bool>()) { arguments += " --quiet"; }
            return arguments;
        }

        // A command-line MSBuild property value: ';' and ',' separate properties, '%' starts an escape.
        internal static string EscapeMSBuildProperty(string value) =>
            value.Replace("%", "%25", StringComparison.Ordinal).Replace(";", "%3B", StringComparison.Ordinal).Replace(",", "%2C", StringComparison.Ordinal);

        /// <summary>
        /// MSBuild lets a target reassign a global property, and packaged projects commonly set <c>RunCommand</c> in one
        /// (the winapp NuGet package does, to launch through its own winapp). So the launch step is chosen by a target
        /// that runs after <c>ComputeRunArguments</c>, imported through <c>CustomAfterMicrosoftCommonTargets</c>.
        /// </summary>
        internal const string WatchLaunchTargets =
            """
            <Project>
              <!-- Written by winapp for a packaged DevTools watch: dotnet watch starts the packaged app through winapp. -->
              <Target Name="WinAppDevToolsWatchLaunch" AfterTargets="ComputeRunArguments" Condition="'$(WinAppDevToolsWatchRunCommand)' != ''">
                <PropertyGroup>
                  <RunCommand>$(WinAppDevToolsWatchRunCommand)</RunCommand>
                  <RunArguments>$(WinAppDevToolsWatchRunArguments)</RunArguments>
                </PropertyGroup>
              </Target>
            </Project>
            """;

        internal static string WriteWatchLaunchTargets(DirectoryInfo winappDirectory)
        {
            var path = Path.Combine(winappDirectory.FullName, "devtools", "watch-launch.targets");
            if (!File.Exists(path) || File.ReadAllText(path) != WatchLaunchTargets)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, WatchLaunchTargets);
            }
            return path;
        }

        /// <summary>
        /// A packaged watch's launch step (<c>dotnet watch</c> runs it as the app): repeats the watching run's
        /// <c>winapp run --no-build --devtools on</c>, which registers the fresh build and launches it through the staged alias.
        /// </summary>
        private async Task<int> InvokeWatchLaunchAsync(ParseResult parseResult, CancellationToken cancellationToken)
        {
            const string internalOnly = "--devtools-watch-launch is the launch step of 'winapp run --watch'; run that instead.";
            if (Environment.GetEnvironmentVariable(WatchLaunchVariable) is not { Length: > 0 } encoded)
            {
                return Fail(internalOnly, false);
            }
            var tokens = WindowsCommandLine.SplitArguments(encoded);
            var root = parseResult.RootCommandResult.Command;
            var launch = root.Parse(root is RunCommand ? [.. tokens] : ["run", .. tokens]);
            if (launch.Errors.Count > 0 || launch.CommandResult.Command is not RunCommand ||
                launch.GetValue(WatchLaunchOption) || launch.GetValue(WatchOption) ||
                launch.GetValue(DevToolsOption) is not (DevToolsMode.On or DevToolsMode.Headless) || !launch.GetValue(NoBuildOption))
            {
                return Fail(internalOnly, false);
            }
            watchIteration = int.TryParse(Environment.GetEnvironmentVariable(WatchIterationVariable), out var iteration) && iteration > 0
                ? iteration : 1;
            // dotnet watch meant these for the app. Everything else this step starts (MSBuild evaluations included) is
            // .NET too, and must not load the Hot Reload agent: the first process to connect takes dotnet watch's pipe.
            watchAppEnvironment = TakeHotReloadEnvironment();
            return await InvokeAsync(launch, cancellationToken);
        }

        // dotnet watch's Hot Reload environment, held back for the app alone.
        private IReadOnlyDictionary<string, string>? watchAppEnvironment;

        internal static bool IsHotReloadVariable(string name) =>
            name.Equals(DevToolsStartupHooks.EnvVarName, StringComparison.OrdinalIgnoreCase) ||
            name.Equals("DOTNET_MODIFIABLE_ASSEMBLIES", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("DOTNET_WATCH", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("DOTNET_HOTRELOAD", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("HOTRELOAD_", StringComparison.OrdinalIgnoreCase);

        private static Dictionary<string, string> TakeHotReloadEnvironment()
        {
            var taken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
            {
                if (variable.Key is string name && variable.Value is string value && IsHotReloadVariable(name))
                {
                    taken[name] = value;
                }
            }
            foreach (var name in taken.Keys)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
            return taken;
        }

        /// <summary>The app's launch environment plus dotnet watch's Hot Reload environment; Hot Reload's startup hook runs first.</summary>
        internal static IReadOnlyDictionary<string, string?> WithHotReloadEnvironment(
            IReadOnlyDictionary<string, string?> launch, IReadOnlyDictionary<string, string>? hotReload)
        {
            if (hotReload is null or { Count: 0 })
            {
                return launch;
            }
            var environment = new Dictionary<string, string?>(launch, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in hotReload)
            {
                environment[name] = name.Equals(DevToolsStartupHooks.EnvVarName, StringComparison.OrdinalIgnoreCase) &&
                    environment.TryGetValue(name, out var devTools) && !string.IsNullOrEmpty(devTools)
                    ? string.Join(Path.PathSeparator, value, devTools)
                    : value;
            }
            return environment;
        }

        /// <summary>Waits for the previous launch's app to close; returns the processes still running from the layout.</summary>
        private async Task<IReadOnlyList<int>> WaitForLayoutToCloseAsync(DirectoryInfo layout, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + LayoutCloseTimeout;
            var running = ProcessesRunningFromLayout(layout);
            while (running.Count > 0 && DateTime.UtcNow < deadline)
            {
                await WatchDelay(TimeSpan.FromMilliseconds(250), cancellationToken);
                running = ProcessesRunningFromLayout(layout);
            }
            return running;
        }

        private static IDisposable? ContainInKillOnCloseJob(uint processId)
        {
            GuestJobObject? job = null;
            try
            {
                job = GuestJobObject.Create();
                using var process = System.Diagnostics.Process.GetProcessById(unchecked((int)processId));
                if (job.TryAssign(process.SafeHandle))
                {
                    return job;
                }
            }
            catch (Exception ex) when (ex is ExecutionTargets.Abstractions.ExecutionTargetException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Best effort: dotnet watch still stops the app through this launch step's Ctrl+C handling.
            }
            job?.Dispose();
            return null;
        }

        private void ReportWatchAttach(int pid, DevToolsConnection connection, bool restart)
        {
            if (!connection.Connected)
            {
                logger.LogWarning("{Symbol} DevTools could not attach to the app (PID {Pid}): {Error}", UiSymbols.Warning, pid, connection.Error);
                return;
            }
            if (restart)
            {
                ansiConsole.MarkupLineInterpolated($"{UiSymbols.Check} The app restarted (PID {pid}); DevTools attached again.");
            }
            else
            {
                ansiConsole.MarkupLineInterpolated($"{UiSymbols.Check} DevTools attached to the app (PID {pid}). Next: winapp devtools inspect -a {pid}");
            }
            if (connection.OverlayError is { } overlayError)
            {
                logger.LogWarning("{Symbol} The DevTools overlay did not open: {Error}", UiSymbols.Warning, overlayError);
            }
            if (connection.ManagedAgent?.Note is { } note)
            {
                logger.LogWarning("{Symbol} {Note}", UiSymbols.Warning, note);
            }
        }

        /// <summary>
        /// The DevTools launch environment for <c>dotnet watch</c>. <c>dotnet watch</c> and <c>dotnet run</c> are .NET
        /// apps too, so the agent's startup hook is not set for them: <c>dotnet watch</c> replaces it in the app with its
        /// own Hot Reload hook anyway, and DevTools loads the agent into each app process when it attaches.
        /// </summary>
        internal static IReadOnlyDictionary<string, string?> WatchEnvironment(IReadOnlyDictionary<string, string?> launch, string? inheritedHooks)
        {
            var environment = new Dictionary<string, string?>(launch, StringComparer.OrdinalIgnoreCase)
            {
                [DevToolsStartupHooks.EnvVarName] = string.IsNullOrEmpty(inheritedHooks) ? null : inheritedHooks,
            };
            return environment;
        }
    }
}
