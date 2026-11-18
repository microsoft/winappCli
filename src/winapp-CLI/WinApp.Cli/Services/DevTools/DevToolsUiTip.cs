// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using WinApp.Cli.Commands;

namespace WinApp.Cli.Services.DevTools;

/// <summary>
/// One stderr line after a <c>winapp ui inspect</c> of an app that already has the DevTools agent: an agent
/// that only knows UI automation otherwise never learns it could read and change the app's XAML live. Only
/// <c>inspect</c> says it, because repeating it on every click and search costs a long session tokens.
/// </summary>
internal static class DevToolsUiTip
{
    internal const string Text =
        "Tip: this app has WinUI DevTools attached. 'winapp devtools --help' can read and change its XAML live.";

    public static void WriteIfApplies(ParseResult parseResult, TextWriter error)
    {
        var command = parseResult.CommandResult.Command;
        if (!ShownAfter(command) || !command.Options.Contains(SharedUiOptions.AppOption))
        {
            return;
        }

        var app = parseResult.GetValue(SharedUiOptions.AppOption);
        var window = command.Options.Contains(SharedUiOptions.WindowOption) ? parseResult.GetValue(SharedUiOptions.WindowOption) : null;
        if (string.IsNullOrWhiteSpace(app) && window is not > 0)
        {
            return;
        }

        try
        {
            // A listing of the local pipe namespace; nothing connects to the app.
            var pids = DevToolsPipeDiscovery.EnumerateInjectedPids();
            if (Applies(app, window, pids, WindowProcess, Describe))
            {
                error.WriteLine(Text);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The tip is best effort.
        }
    }

    internal static bool ShownAfter(Command command) => command is UiInspectCommand;

    internal static bool Applies(string? app, long? window, IReadOnlyList<int> devToolsPids,
        Func<long, int?> windowProcess, Func<int, (string Name, string Title)?> describe)
    {
        if (devToolsPids.Count == 0)
        {
            return false;
        }
        if (window is > 0)
        {
            return windowProcess(window.Value) is int owner && devToolsPids.Contains(owner);
        }
        if (string.IsNullOrWhiteSpace(app))
        {
            return false;
        }
        if (int.TryParse(app, out var pid))
        {
            return devToolsPids.Contains(pid);
        }

        var name = app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app;
        return devToolsPids.Any(candidate => describe(candidate) is { } process &&
            (process.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
             process.Title.Contains(app, StringComparison.OrdinalIgnoreCase)));
    }

    private static unsafe int? WindowProcess(long window)
    {
        uint pid = 0;
        return PInvoke.GetWindowThreadProcessId(new HWND((nint)window), &pid) != 0 ? (int)pid : null;
    }

    private static (string Name, string Title)? Describe(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return (process.ProcessName, process.MainWindowTitle);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
