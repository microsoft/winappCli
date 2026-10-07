// Copyright (c) Microsoft Corporation and Contributors.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace winui_app;

// In-app probe for scripts/devtools-perf.ps1. The script drops cmd.json into the control directory;
// the app runs the scenario on its UI thread and writes res-<id>.json. All timings are measured from
// inside the app, so they are identical with and without DevTools attached.
internal static class Bench
{
    private static Frame s_frame = null!;
    private static Grid s_churnHost = null!;
    private static DispatcherQueue s_queue = null!;
    private static double s_refreshMs = 1000.0 / 60;
    private static readonly List<WeakReference<Page>> s_heavyPages = new();
    private static int s_heavyPagesCreated;

    // Tracks every HeavyPage instance so "pages" can report how many are still alive after a GC:
    // a page that outlives its navigation is held by something native (for example diagnostics).
    public static void TrackHeavyPage(Page page)
    {
        s_heavyPagesCreated++;
        s_heavyPages.Add(new WeakReference<Page>(page));
    }

    private static JsonObject PagesAlive()
    {
        for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        s_heavyPages.RemoveAll(w => !w.TryGetTarget(out _));
        return new JsonObject { ["heavyPagesAlive"] = s_heavyPages.Count, ["heavyPagesCreated"] = s_heavyPagesCreated };
    }

    public static void Start(Window window, Frame frame, Grid churnHost)
    {
        s_frame = frame;
        s_churnHost = churnHost;
        s_queue = window.DispatcherQueue;
        var hz = GetDeviceCaps(GetDC(IntPtr.Zero), 116 /* VREFRESH */);
        if (hz > 1) s_refreshMs = 1000.0 / hz;
        Directory.CreateDirectory(BenchConfig.ControlDirectory);
        var root = (FrameworkElement)window.Content;
        double loaded = 0;
        root.Loaded += async (_, _) =>
        {
            loaded = App.SinceProcessStart();
            await NextFrame();
            var firstFrame = App.SinceProcessStart();
            Write("startup.json", new JsonObject
            {
                ["pid"] = Environment.ProcessId,
                ["constructedMs"] = App.ConstructedMs,
                ["launchedMs"] = App.LaunchedMs,
                ["loadedMs"] = loaded,
                ["firstFrameMs"] = firstFrame,
                ["refreshMs"] = s_refreshMs,
            });
            new Thread(PollCommands) { IsBackground = true, Name = "bench-commands" }.Start();
        };
        frame.Navigate(typeof(HomePage), null, new SuppressNavigationTransitionInfo());
    }

    private static void PollCommands()
    {
        var path = Path.Combine(BenchConfig.ControlDirectory, "cmd.json");
        while (true)
        {
            Thread.Sleep(100);
            if (!File.Exists(path)) continue;
            JsonObject? command;
            try
            {
                command = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
                File.Delete(path);
            }
            catch (IOException) { continue; }
            if (command is null) continue;
            s_queue.TryEnqueue(async () =>
            {
                var id = (int?)command["id"] ?? 0;
                Log($"start {id} {command["name"]}");
                var timeouts = s_frameTimeouts;
                JsonObject result;
                try { result = await Run(command); }
                catch (Exception ex) { result = new JsonObject { ["error"] = ex.ToString() }; }
                result["id"] = id;
                result["frameWaitTimeouts"] = s_frameTimeouts - timeouts;
                Write($"res-{id}.json", result);
                Log($"end {id}");
                if ((string?)command["name"] == "exit") Application.Current.Exit();
            });
        }
    }

    private static async Task<JsonObject> Run(JsonObject c)
    {
        switch ((string?)c["name"])
        {
            case "ping":
            case "exit":
                return new JsonObject();
            case "mem":
                return Memory();
            case "pages":
                return PagesAlive();
            case "goto":
                {
                    // Navigate and stay, so an attach can be measured against a large live tree.
                    var target = (string?)c["page"] ?? "heavy";
                    var ms = target == "bigtree"
                        ? await Navigate(typeof(BigTreePage), (int?)c["count"] ?? 5000)
                        : await Navigate(target == "plain" ? typeof(PlainPage) : typeof(HeavyPage), null);
                    return new JsonObject { ["navMs"] = ms, ["elements"] = Count((DependencyObject)s_frame.Content) };
                }
            case "home":
                await Navigate(typeof(HomePage), null);
                return Memory();
            case "scroll":
                return await Scroll((double?)c["seconds"] ?? 8, (double?)c["px"] ?? 12);
            case "nav":
                return await NavLoop((int?)c["cycles"] ?? 30, (int?)c["warmup"] ?? 3, (string?)c["page"] ?? "heavy");
            case "bigtree":
                return await BigTree((int?)c["count"] ?? 5000);
            case "churn":
                return await Churn((int?)c["count"] ?? 2000, (int?)c["cycles"] ?? 10, (bool?)c["visible"] ?? false, (int?)c["rounds"] ?? 4);
            default:
                throw new InvalidOperationException("Unknown command " + c["name"]);
        }
    }

    private static async Task<JsonObject> Scroll(double seconds, double px)
    {
        await Navigate(typeof(ListPage), null);
        await Delay(500);
        var list = (ListView)((ListPage)s_frame.Content).Content;
        var viewer = Find<ScrollViewer>(list) ?? throw new InvalidOperationException("No ScrollViewer");
        var offset = 0.0;
        var direction = 1.0;
        var done = new TaskCompletionSource();
        var clock = Stopwatch.StartNew();
        using var recorder = new FrameRecorder(s_queue, s_refreshMs);
        void Tick(object? sender, object e)
        {
            if (clock.Elapsed.TotalSeconds >= seconds)
            {
                CompositionTarget.Rendering -= Tick;
                done.TrySetResult();
                return;
            }
            offset += px * direction;
            if (offset >= viewer.ScrollableHeight || offset <= 0) direction = -direction;
            viewer.ChangeView(null, offset, null, true);
        }
        CompositionTarget.Rendering += Tick;
        // Task.Delay, not a DispatcherQueueTimer: an unreferenced timer can be collected and never fire.
        if (await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(seconds + 5))) != done.Task)
        {
            Log("scroll: no frames, stopping");
            CompositionTarget.Rendering -= Tick;
        }
        var result = recorder.Report();
        result["scrollableHeight"] = viewer.ScrollableHeight;
        await Navigate(typeof(EmptyPage), null);
        return result;
    }

    private static async Task<JsonObject> NavLoop(int cycles, int warmup, string pageName)
    {
        var page = pageName switch
        {
            "heavy" => typeof(HeavyPage),
            "plain" => typeof(PlainPage),
            "resource" => typeof(ResourcePage),
            "styled" => typeof(StyledPage),
            "button" => typeof(ButtonPage),
            "checkbox" => typeof(CheckBoxPage),
            _ => throw new InvalidOperationException("Unknown page " + pageName),
        };
        await Navigate(typeof(EmptyPage), null);
        for (var i = 0; i < warmup; i++)
        {
            await Navigate(page, null);
            await Navigate(typeof(EmptyPage), null);
        }
        await Navigate(page, null);
        var elements = Count((DependencyObject)s_frame.Content);
        await Navigate(typeof(EmptyPage), null);
        var before = Memory();
        var navMs = new List<double>();
        JsonObject frames;
        using (var recorder = new FrameRecorder(s_queue, s_refreshMs))
        {
            for (var i = 0; i < cycles; i++)
            {
                navMs.Add(await Navigate(page, null));
                await Navigate(typeof(EmptyPage), null);
            }
            frames = recorder.Report();
        }
        var after = Memory();
        return new JsonObject
        {
            ["cycles"] = cycles,
            ["page"] = pageName,
            ["elementsPerCycle"] = elements,
            ["navMs"] = Stats(navMs),
            ["frames"] = frames,
            ["before"] = before,
            ["after"] = after,
        };
    }

    private static async Task<JsonObject> BigTree(int count)
    {
        await Navigate(typeof(EmptyPage), null);
        var before = Memory();
        var ms = await Navigate(typeof(BigTreePage), count);
        var elements = Count((DependencyObject)s_frame.Content);
        var loaded = Memory();
        await Navigate(typeof(EmptyPage), null);
        var after = Memory();
        return new JsonObject
        {
            ["items"] = count,
            ["elements"] = elements,
            ["navMs"] = ms,
            ["before"] = before,
            ["loaded"] = loaded,
            ["after"] = after,
        };
    }

    // Adds and removes `count` Border+TextBlock pairs per cycle, for `rounds` rounds of `cycles`.
    // Collapsed by default so nothing is rendered: memory then isolates per-element costs (such as
    // diagnostics bookkeeping) from the renderer's surface caches. Retention is the growth of the
    // settled memory floor between the first and last round, which is robust to lazy frees.
    private static async Task<JsonObject> Churn(int count, int cycles, bool visible, int rounds)
    {
        await Navigate(typeof(EmptyPage), null);
        await RunChurn(count, 2, visible);
        var floors = new JsonArray();
        var times = new List<double>();
        var first = await SettledMemory();
        floors.Add(first["privateMB"]!.GetValue<double>());
        for (var r = 0; r < rounds; r++)
        {
            var clock = Stopwatch.StartNew();
            await RunChurn(count, cycles, visible);
            times.Add(clock.Elapsed.TotalMilliseconds);
            floors.Add((await SettledMemory())["privateMB"]!.GetValue<double>());
        }
        var created = 2L * count * cycles * rounds;
        var last = floors[^1]!.GetValue<double>();
        return new JsonObject
        {
            ["elementsCreated"] = created,
            ["visible"] = visible,
            ["ms"] = Math.Round(times.OrderBy(t => t).ElementAt(times.Count / 2), 1),
            ["floorsPrivateMB"] = floors,
            ["retainedBytesPerElement"] = Math.Round((last - first["privateMB"]!.GetValue<double>()) * 1048576 / created, 1),
        };
    }

    private static async Task<JsonObject> SettledMemory()
    {
        await Task.Delay(1000);
        return Memory();
    }

    private static async Task RunChurn(int count, int cycles, bool visible)
    {
        for (var c = 0; c < cycles; c++)
        {
            var panel = new StackPanel { Visibility = visible ? Visibility.Visible : Visibility.Collapsed };
            for (var i = 0; i < count; i++)
                panel.Children.Add(new Border { Child = new TextBlock { Text = "Churn " + i } });
            s_churnHost.Children.Add(panel);
            await NextFrame();
            s_churnHost.Children.Clear();
            await NextFrame();
        }
    }

    private static async Task<double> Navigate(Type page, object? parameter)
    {
        var clock = Stopwatch.StartNew();
        var loaded = new TaskCompletionSource();
        void OnNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            s_frame.Navigated -= OnNavigated;
            var content = (FrameworkElement)e.Content;
            if (content.IsLoaded) loaded.TrySetResult();
            else content.Loaded += (_, _) => loaded.TrySetResult();
        }
        s_frame.Navigated += OnNavigated;
        s_frame.Navigate(page, parameter, new SuppressNavigationTransitionInfo());
        if (await Task.WhenAny(loaded.Task, Task.Delay(30_000)) != loaded.Task) Log($"navigate {page.Name}: Loaded not raised in 30 s");
        await NextFrame();
        s_frame.BackStack.Clear();
        return clock.Elapsed.TotalMilliseconds;
    }

    private static int s_frameTimeouts;

    // Waits for the next CompositionTarget.Rendering. If no frame arrives within 1 s (for example
    // the compositor paused), continue and count it so a stalled renderer cannot hang a scenario.
    private static async Task NextFrame()
    {
        var done = new TaskCompletionSource();
        void Tick(object? sender, object e)
        {
            CompositionTarget.Rendering -= Tick;
            done.TrySetResult();
        }
        CompositionTarget.Rendering += Tick;
        if (await Task.WhenAny(done.Task, Task.Delay(1000)) != done.Task)
        {
            CompositionTarget.Rendering -= Tick;
            s_frameTimeouts++;
            Log("frame wait timed out");
        }
    }

    internal static void Log(string message)
    {
        try { File.AppendAllText(Path.Combine(BenchConfig.ControlDirectory, "bench.log"), $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}"); }
        catch (IOException) { }
    }

    private static Task Delay(int ms) => Task.Delay(ms);

    private static JsonObject Memory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        return new JsonObject
        {
            ["privateMB"] = Math.Round(process.PrivateMemorySize64 / 1048576.0, 2),
            ["workingSetMB"] = Math.Round(process.WorkingSet64 / 1048576.0, 2),
            ["managedMB"] = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 2),
        };
    }

    private static int Count(DependencyObject root)
    {
        var count = 1;
        var children = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < children; i++) count += Count(VisualTreeHelper.GetChild(root, i));
        return count;
    }

    private static T? Find<T>(DependencyObject root) where T : class
    {
        if (root is T match) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (Find<T>(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        }
        return null;
    }

    internal static JsonObject Stats(List<double> values)
    {
        if (values.Count == 0) return new JsonObject { ["n"] = 0 };
        var sorted = values.OrderBy(v => v).ToArray();
        double P(double p) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];
        return new JsonObject
        {
            ["n"] = sorted.Length,
            ["mean"] = Math.Round(sorted.Average(), 3),
            ["p50"] = Math.Round(P(0.50), 3),
            ["p95"] = Math.Round(P(0.95), 3),
            ["p99"] = Math.Round(P(0.99), 3),
            ["max"] = Math.Round(sorted[^1], 3),
        };
    }

    private static void Write(string name, JsonObject value)
    {
        var path = Path.Combine(BenchConfig.ControlDirectory, name);
        File.WriteAllText(path + ".tmp", value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr dc, int index);
}

// Records frame-to-frame gaps (CompositionTarget.Rendering) and UI-thread dispatch latency
// (a background thread enqueues a callback every 16 ms and measures how long it waits).
internal sealed class FrameRecorder : IDisposable
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<double> _gaps = new();
    private readonly List<double> _lags = new();
    private readonly double _refreshMs;
    private readonly Thread _probe;
    private volatile bool _running = true;
    private double _last = -1;

    public FrameRecorder(DispatcherQueue queue, double refreshMs)
    {
        _refreshMs = refreshMs;
        CompositionTarget.Rendering += OnRendering;
        _probe = new Thread(() =>
        {
            while (_running)
            {
                var start = _clock.Elapsed.TotalMilliseconds;
                queue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
                {
                    lock (_lags) _lags.Add(_clock.Elapsed.TotalMilliseconds - start);
                });
                Thread.Sleep(16);
            }
        }) { IsBackground = true, Name = "bench-lag-probe" };
        _probe.Start();
    }

    private void OnRendering(object? sender, object e)
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        if (_last >= 0) _gaps.Add(now - _last);
        _last = now;
    }

    public JsonObject Report()
    {
        Dispose();
        List<double> lags;
        lock (_lags) lags = _lags.ToList();
        var missed = _gaps.Sum(g => Math.Max(0, Math.Round(g / _refreshMs) - 1));
        return new JsonObject
        {
            ["seconds"] = Math.Round(_clock.Elapsed.TotalSeconds, 3),
            ["refreshMs"] = Math.Round(_refreshMs, 3),
            ["frames"] = _gaps.Count + 1,
            ["missedFrames"] = missed,
            ["longFrames"] = _gaps.Count(g => g > 1.5 * _refreshMs),
            ["frameGapMs"] = Bench.Stats(_gaps),
            ["uiLagMs"] = Bench.Stats(lags),
            ["uiLagOver50ms"] = lags.Count(l => l > 50),
        };
    }

    public void Dispose()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnRendering;
    }
}
