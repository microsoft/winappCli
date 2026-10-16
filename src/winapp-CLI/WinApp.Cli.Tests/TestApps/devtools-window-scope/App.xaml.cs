// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace WindowScopeFixture;

public partial class App : Application
{
    private Window? first;
    private Window? second;
    private Window? third;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs();
        var report = commandLine.Length is 2 or 3 ? commandLine[1] : "";
        var allowFocus = commandLine.Length == 3 && commandLine[2] == "allow-focus";
        if (!Path.IsPathFullyQualified(report))
            throw new InvalidOperationException("Pass an absolute owned test output path as the launch argument.");
        if (allowFocus)
            Environment.SetEnvironmentVariable("WINAPP_DEVTOOLS_LOG", "1");
        UnhandledException += (_, error) => File.WriteAllText(report + ".unhandled.json",
            JsonSerializer.Serialize(new { error.Message, exception = error.Exception.ToString() }));
        first = Create("Alpha", "Alpha only");
        second = Create("Beta", "Beta only");
        first.AppWindow.Show(false);
        second.AppWindow.Show(false);
        File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            pid = Environment.ProcessId,
            alpha = WinRT.Interop.WindowNative.GetWindowHandle(first).ToInt64(),
            beta = WinRT.Interop.WindowNative.GetWindowHandle(second).ToInt64(),
        }));
        timer.Tick += (_, _) =>
        {
            var control = report + ".command";
            if (!File.Exists(control)) return;
            var command = File.ReadAllText(control);
            File.Delete(control);
            var action = command.Split('|')[0];
            if (action == "close-alpha")
            {
                first?.Close();
                first = null;
            }
            else if (action == "close-beta")
            {
                second?.Close();
                second = null;
            }
            else if (action == "open-gamma")
            {
                third ??= Create("Gamma", "Gamma only");
                third.AppWindow.Show(false);
            }
            else if (action == "close-gamma")
            {
                third?.Close();
                third = null;
            }
            else if (action.StartsWith("detach-overlays-", StringComparison.Ordinal))
            {
                var window = action.EndsWith("alpha", StringComparison.Ordinal) ? first : second;
                var panel = (Panel)(window ?? throw new InvalidOperationException("Window is closed.")).Content;
                for (var i = panel.Children.Count - 1; i >= 0; i--)
                    if (panel.Children[i] is Popup) panel.Children.RemoveAt(i);
            }
            else if (action.StartsWith("draft-", StringComparison.Ordinal))
            {
                var window = action.EndsWith("alpha", StringComparison.Ordinal) ? first : second;
                var root = ((FrameworkElement)(window ?? throw new InvalidOperationException("Window is closed.")).Content).XamlRoot;
                TextBox? Find(DependencyObject element)
                {
                    if (element is TextBox { Name: "DevToolsSelComment" } textBox) return textBox;
                    for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
                        if (Find(VisualTreeHelper.GetChild(element, i)) is { } found) return found;
                    return null;
                }
                TextBox? comment = null;
                foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
                    if (popup.Child is { } child && Find(child) is { } found) { comment = found; break; }
                if (comment is null)
                {
                    File.WriteAllText(report + ".snapshot.pending", JsonSerializer.Serialize(new
                    {
                        command,
                        error = "Selection comment input is unavailable.",
                    }));
                    File.Move(report + ".snapshot.pending", report + ".snapshot.json", true);
                    return;
                }
                comment.Text = "Owned unsaved draft";
            }
            else if (action.StartsWith("focus-", StringComparison.Ordinal))
            {
                if (!allowFocus) throw new InvalidOperationException("Focus actions were not authorized for this fixture run.");
                var window = action switch { "focus-alpha" => first, "focus-beta" => second, "focus-gamma" => third, _ => null };
                (window ?? throw new InvalidOperationException("Requested fixture window is not live.")).Activate();
            }
            else if (action == "exit")
            {
                timer.Stop();
                first?.Close();
                second?.Close();
                third?.Close();
                Exit();
                return;
            }
            else if (action != "snapshot") throw new InvalidOperationException($"Unknown fixture action: {action}");
            File.WriteAllText(report + ".snapshot.pending", JsonSerializer.Serialize(new
            {
                command,
                windows = new[] { Snapshot(first), Snapshot(second), Snapshot(third) },
            }));
            File.Move(report + ".snapshot.pending", report + ".snapshot.json", true);
        };
        timer.Start();
    }

    private static Window Create(string title, string text) => new()
    {
        Title = $"WinApp owned window-scope {Environment.ProcessId} {title}",
        Content = (StackPanel)XamlReader.Load($"""
            <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Name="Root{title}" Padding="24">
                <TextBlock x:Name="SharedLabel" Text="{text}" FontSize="24" />
                <TextBlock x:Name="Unique{title}" Text="{title} marker" />
                <TextBlock x:Name="DuplicateOne" Text="duplicate" />
                <TextBlock x:Name="DuplicateTwo" Text="duplicate" />
            </StackPanel>
            """),
    };

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private static object? Snapshot(Window? window)
    {
        if (window?.Content is not FrameworkElement content || content.XamlRoot is not { } root) return null;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var nodes = new List<object>();
        void Visit(DependencyObject element, int depth)
        {
            if (depth > 40 || nodes.Count > 2000) throw new InvalidOperationException("Fixture popup traversal exceeded its bound.");
            if (element is FrameworkElement fe)
            {
                var position = fe.TransformToVisual(root.Content).TransformPoint(new(0, 0));
                nodes.Add(new
                {
                    name = fe.Name,
                    automationId = AutomationProperties.GetAutomationId(fe),
                    type = fe.GetType().Name,
                    text = (fe as TextBox)?.Text,
                    visible = fe.Visibility == Visibility.Visible,
                    x = position.X, y = position.Y,
                    width = fe.ActualWidth, height = fe.ActualHeight,
                    sameRoot = Equals(fe.XamlRoot, root),
                });
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
                Visit(VisualTreeHelper.GetChild(element, i), depth + 1);
        }
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
            if (popup.Child is { } child) Visit(child, 0);
        var label = (TextBlock)content.FindName("SharedLabel");
        var labelOrigin = label.TransformToVisual(root.Content).TransformPoint(new(0, 0));
        var labelSize = label.RenderSize; // ActualWidth measures text, not the arranged control box.
        return new
        {
            window = hwnd.ToInt64(),
            focused = GetForegroundWindow() == hwnd,
            scale = root.RasterizationScale,
            width = root.Size.Width,
            height = root.Size.Height,
            label = new { x = labelOrigin.X, y = labelOrigin.Y, width = labelSize.Width, height = labelSize.Height },
            popupNodes = nodes,
        };
    }
}
