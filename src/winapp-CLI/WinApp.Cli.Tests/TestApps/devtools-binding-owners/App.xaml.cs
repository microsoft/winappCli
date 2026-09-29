// Copyright (c) Microsoft Corporation and Contributors.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Text.Json;
using Microsoft.UI.Xaml;

namespace BindingOwnersFixture;

public partial class App : Application
{
    private Window? first;
    private Window? second;
    private bool firstClosed;
    private bool unsupportedClosed;
    private static Window? unsupported;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Length is < 2 or > 3 || !Path.IsPathFullyQualified(arguments[1]) ||
            arguments.Length == 3 && arguments[2] is not ("large" or "single"))
            throw new InvalidOperationException("Pass an absolute owned report path.");
        var report = arguments[1];
        var large = arguments.Length == 3 && arguments[2] == "large";
        var single = arguments.Length == 3 && arguments[2] == "single";
        first = new MainWindow("Alpha", large);
        if (single)
        {
            first.Closed += (_, _) => { firstClosed = true; timer.Stop(); Exit(); };
        }
        else
        {
            second = new MainWindow("Beta", large);
            unsupported = new MainWindow("Static-only", large);
        }
        foreach (var window in new[] { first, second, unsupported })
            (window as MainWindow)?.ShowWithoutActivation();
        File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            pid = Environment.ProcessId,
            alpha = WinRT.Interop.WindowNative.GetWindowHandle(first).ToInt64(),
            beta = second is null ? 0 : WinRT.Interop.WindowNative.GetWindowHandle(second).ToInt64(),
            unsupported = unsupported is null ? 0 : WinRT.Interop.WindowNative.GetWindowHandle(unsupported).ToInt64(),
        }));
        var expires = DateTime.UtcNow.AddMinutes(3);
        timer.Tick += (_, _) =>
        {
            var path = report + ".command";
            var command = File.Exists(path) ? File.ReadAllText(path) : "";
            if (command.Length != 0) File.Delete(path);
            if (command == "close-alpha")
            {
                if (!firstClosed) first?.Close();
                firstClosed = true;
                File.WriteAllText(report + ".closed", "alpha");
            }
            else if (command == "close-duplicates")
            {
                if (!firstClosed) first?.Close();
                if (!unsupportedClosed) unsupported?.Close();
                firstClosed = true;
                unsupportedClosed = true;
                File.WriteAllText(report + ".single", "beta");
            }
            else if (command == "exit" || DateTime.UtcNow >= expires)
            {
                timer.Stop();
                if (!firstClosed) first?.Close();
                second?.Close();
                if (!unsupportedClosed) unsupported?.Close();
                Exit();
            }
            else if (command.Length != 0)
                throw new InvalidOperationException("Unknown owned fixture command: " + command);
        };
        timer.Start();
    }
}
