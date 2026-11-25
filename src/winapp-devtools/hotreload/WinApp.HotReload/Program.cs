// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using WinApp.HotReload.Protocol;

namespace WinApp.HotReload;

/// <summary>
/// The hot reload helper 'winapp run --watch' starts next to each app it launches (engine B). It watches the app's
/// files, prints one '[watch] ...' line per save, and exits with <see cref="WatchLoop.RestartExitCode"/> when the app
/// must be rebuilt and restarted, or 0 when the app is gone.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = args.Chunk(2).Where(pair => pair.Length == 2).ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
        if (!options.TryGetValue("--project", out var project) || !options.TryGetValue("--capture", out var capture) ||
            !options.TryGetValue("--pipe", out var pipe) || !options.TryGetValue("--token", out var token))
        {
            Console.Error.WriteLine("usage: WinApp.HotReload --project <csproj> --capture <folder> --pipe <name> --token <token>");
            return 2;
        }
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        try
        {
            var clock = Stopwatch.StartNew();
            var graph = new ProjectGraph(project, capture);
            using var loop = new WatchLoop(graph.Roots, graph.AsBuiltCopy);
            var (sdkDirectory, sdkVersion) = HotReloadApi.FindSdk(graph.ProjectDirectory);
            var unsupported = HotReloadApi.Register(sdkDirectory, sdkVersion);

            using var agent = await AgentClient.ConnectAsync(pipe, token, TimeSpan.FromMinutes(2), stop.Token).ConfigureAwait(false);
            var hello = await agent.SendAsync(new HotReloadRequest { Op = "hello" }, stop.Token).ConfigureAwait(false);
            if (!hello.Ok)
            {
                Console.WriteLine($"[watch] error the app refused hot reload: {hello.Error}");
                return 1;
            }
            using var app = Process.GetProcessById(hello.ProcessId);
            _ = app.WaitForExitAsync(stop.Token).ContinueWith(_ => stop.Cancel(), TaskScheduler.Default);

            ICSharpEngine engine;
            if (unsupported is not null)
            {
                engine = new RestartingEngine(unsupported);
            }
            else if (graph.IsOptimized)
            {
                engine = new RestartingEngine("this is an optimized build (for example -c Release), which .NET can't update while it runs, so saves restart the app; use the Debug configuration for live edits");
            }
            else
            {
                engine = await CreateEngineAsync(graph, agent, hello, capture, sdkVersion, stop.Token).ConfigureAwait(false);
            }
            Console.WriteLine($"[watch] ready ({clock.ElapsedMilliseconds} ms): saved files apply to the running app (PID {hello.ProcessId})");
            if (engine is RestartingEngine restarting)
            {
                Console.WriteLine($"[watch] note: {restarting.Reason}");
            }
            // WinUI's XAML compiler as a service and its runtime hook: XAML saves apply to the running app.
            string? xamlUnavailable = null;
            using var xaml = engine is RoslynEngine ? XamlCompiler.Start(graph.CompilerArguments(), graph.ProjectDirectory, graph.XamlInputsAsBuilt, graph.XamlBuildAsBuilt, out xamlUnavailable) : null;
            if (xamlUnavailable is not null)
            {
                Console.WriteLine($"[watch] note: XAML saves aren't applied: {xamlUnavailable}");
            }
            var save = xaml is null ? new Save(engine, graph, xamlUnavailable: xamlUnavailable)
                : new Save(engine, graph, xaml, (markup, ct) => agent.SendAsync(new HotReloadRequest { Op = "reload", Markup = markup }, ct));
            save.Start();
            return await loop.RunAsync(save, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[watch] error hot reload stopped: {ex.Message}");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    // Separate, so no Roslyn type loads before the SDK's compiler is registered (or at all, on an SDK without it).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<ICSharpEngine> CreateEngineAsync(ProjectGraph graph, AgentClient agent, HotReloadResponse hello, string capture,
        string sdkVersion, CancellationToken cancellationToken)
    {
        try
        {
            return await RoslynEngine.CreateAsync(graph, agent, hello.Capabilities ?? "", hello.BaseDirectory ?? "",
                Path.Combine(capture, "baseline"), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MissingMemberException or TypeLoadException or FileNotFoundException or MissingMethodException)
        {
            Console.Error.WriteLine(ex);
            return new RestartingEngine(HotReloadApi.Unsupported(sdkVersion, $"its hot reload API changed: {ex.Message}"));
        }
    }
}
