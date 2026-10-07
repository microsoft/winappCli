// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace ReproApp;

public sealed partial class CheckBoxPage : Page
{
    public CheckBoxPage() => InitializeComponent();
}

public sealed partial class EmptyPage : Page
{
    public EmptyPage() => InitializeComponent();
}

// Runs the measurement by itself and exits:
//   ReproApp.exe [--out <csv>] [--go <file>] [--rounds 12] [--navs 10] [--churn 40000]
// Phase 1 (memory): each round creates and removes --churn collapsed elements (Border + TextBlock,
// never rendered) and records the settled private bytes. Phase 2 (navigation): each round navigates
// --navs times to CheckBoxPage and back and records the median UI-thread CPU time of a navigation
// (Frame.Navigate until the page's Loaded event) and the median wall-clock time (until Loaded plus
// the next rendered frame).
// Without --go it starts 5 s after launch (for F5 in Visual Studio); --out defaults to
// %TEMP%\winui-diag-repro\run-<time>.csv.
public partial class App : Application
{
    private Window? _window;
    private Frame _frame = null!;
    private Grid _churnHost = null!;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _frame = new Frame();
        _churnHost = new Grid();
        var root = new Grid();
        root.Children.Add(_frame);
        root.Children.Add(_churnHost);
        _window = new Window { Content = root, Title = "WinUI diagnostics repro" };
        _window.AppWindow.Resize(new SizeInt32(1280, 800));
        _window.AppWindow.Show(false);
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        var arguments = Environment.GetCommandLineArgs();
        string Arg(string name, string fallback)
        {
            var i = Array.IndexOf(arguments, name);
            return i >= 0 && i + 1 < arguments.Length ? arguments[i + 1] : fallback;
        }
        var outPath = Arg("--out", Path.Combine(Path.GetTempPath(), "winui-diag-repro", $"run-{DateTime.Now:yyyyMMdd-HHmmss}.csv"));
        var go = Arg("--go", "");
        var rounds = int.Parse(Arg("--rounds", "12"));
        var navs = int.Parse(Arg("--navs", "10"));
        var churn = int.Parse(Arg("--churn", "40000"));
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        _frame.Navigate(typeof(EmptyPage));
        if (go.Length != 0) { while (!File.Exists(go)) await Task.Delay(100); }
        else await Task.Delay(5000);

        using var csv = new StreamWriter(outPath) { AutoFlush = true };
        var modules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
            .Select(m => m.ModuleName).Where(n => n.Contains("Tap", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("XamlDiagnostics", StringComparison.OrdinalIgnoreCase));
        csv.WriteLine($"# sourceInfoEnv={Environment.GetEnvironmentVariable("ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO")}; debugger={Debugger.IsAttached}; diagnosticsModules={string.Join(" ", modules)}");
        csv.WriteLine("phase,round,churnMs,privateMB,navP50Ms,navMaxMs,navWallP50Ms");

        // Phase 1, memory: only collapsed element churn, so nothing is rendered and the settled
        // private-bytes floor isolates per-element retention from renderer caches.
        await Churn(4000);
        for (var round = 1; round <= rounds; round++)
        {
            var clock = Stopwatch.StartNew();
            await Churn(churn);
            var churnMs = clock.Elapsed.TotalMilliseconds;
            await Task.Delay(1000);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var privateMB = Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0;
            csv.WriteLine($"memory,{round},{churnMs:F0},{privateMB:F1},,");
        }

        // Phase 2, navigation: repeated navigation to the same XAML page.
        for (var i = 0; i < 2; i++) { await Navigate(typeof(CheckBoxPage)); await Navigate(typeof(EmptyPage)); }
        for (var round = 1; round <= rounds; round++)
        {
            var cpu = new List<double>();
            var wall = new List<double>();
            for (var n = 0; n < navs; n++)
            {
                var (c, w) = await Navigate(typeof(CheckBoxPage));
                cpu.Add(c); wall.Add(w);
                await Navigate(typeof(EmptyPage));
            }
            cpu.Sort(); wall.Sort();
            csv.WriteLine($"navigation,{round},,,{cpu[cpu.Count / 2]:F0},{cpu[^1]:F0},{wall[wall.Count / 2]:F0}");
            // Collect between rounds so released pages do not pile up waiting for the GC: without
            // this, navigation slows down over time even with no diagnostics attached.
            await Task.Delay(500);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        Exit();
    }

    private async Task Churn(int elements)
    {
        for (var created = 0; created < elements; created += 4000)
        {
            var panel = new StackPanel { Visibility = Visibility.Collapsed };
            for (var i = 0; i < 2000; i++) panel.Children.Add(new Border { Child = new TextBlock { Text = "Churn " + i } });
            _churnHost.Children.Add(panel);
            await NextFrame();
            _churnHost.Children.Clear();
            await NextFrame();
        }
    }

    // Returns, for one navigation, the UI thread's CPU time from Frame.Navigate until the page's
    // Loaded event (unaffected by other load on the machine, but excluding render-thread work), and
    // the wall-clock time until Loaded plus the next rendered frame, both in ms.
    private async Task<(double Cpu, double Wall)> Navigate(Type page)
    {
        var clock = Stopwatch.StartNew();
        var cpu0 = UiThreadCpuMs();
        var loaded = new TaskCompletionSource();
        void OnNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            _frame.Navigated -= OnNavigated;
            var content = (FrameworkElement)e.Content;
            if (content.IsLoaded) loaded.TrySetResult();
            else content.Loaded += (_, _) => loaded.TrySetResult();
        }
        _frame.Navigated += OnNavigated;
        _frame.Navigate(page, null, new SuppressNavigationTransitionInfo());
        await loaded.Task;
        var cpu = UiThreadCpuMs() - cpu0;
        await NextFrame();
        _frame.BackStack.Clear();
        return (cpu, clock.Elapsed.TotalMilliseconds);
    }

    private static double UiThreadCpuMs()
    {
        GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user);
        return (kernel + user) / 10000.0;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    private static async Task NextFrame()
    {
        var done = new TaskCompletionSource();
        void Tick(object? sender, object e) { CompositionTarget.Rendering -= Tick; done.TrySetResult(); }
        CompositionTarget.Rendering += Tick;
        if (await Task.WhenAny(done.Task, Task.Delay(1000)) != done.Task) CompositionTarget.Rendering -= Tick;
    }
}
