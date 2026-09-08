// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class UiListWindowsCommand : Command, IShortDescription
{
    public string ShortDescription => "List all visible windows, optionally filtered by app";

    public UiListWindowsCommand()
        : base("list-windows", "List all visible windows with their HWND, title, process, and size. " +
               "Use -a to filter by app name. Use the HWND with -w to target a specific window.")
    {
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(WinAppRootCommand.JsonOption);
        Options.Add(ShowHiddenOption);
    }

    internal static Option<bool> ShowHiddenOption { get; } = new("--show-hidden")
    {
        Description = "Include untitled zero-size windows that are hidden by default"
    };

    /// <summary>
    /// Determines whether a window should be included in list-windows output.
    /// An untitled window is only excluded when it also has zero size (not a real visible window).
    /// </summary>
    internal static bool ShouldIncludeWindow(string? title, int width, int height, bool showHidden)
    {
        if (showHidden)
        {
            return true;
        }

        if (!string.IsNullOrEmpty(title))
        {
            return true;
        }

        return width > 0 && height > 0;
    }

    public class Handler(
        IUiAutomation uiAutomation,
        IAnsiConsole ansiConsole,
        ILogger<UiListWindowsCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var showHidden = parseResult.GetValue(ShowHiddenOption);

            try
            {
                List<(nint Hwnd, int Pid, string Title)> windows;

                if (!string.IsNullOrWhiteSpace(app))
                {
                    // Try as PID first
                    if (int.TryParse(app, out var pid))
                    {
                        windows = uiAutomation.FindWindowsByPid(pid);
                    }
                    else
                    {
                        // Try process name match, then title match
                        var byName = System.Diagnostics.Process.GetProcessesByName(app);
                        if (byName.Length > 0)
                        {
                            windows = [];
                            foreach (var process in byName)
                            {
                                windows.AddRange(uiAutomation.FindWindowsByPid(process.Id));
                            }
                        }
                        else
                        {
                            // Partial process name
                            var partial = System.Diagnostics.Process.GetProcesses()
                                .Where(p =>
                                {
                                    try { return p.ProcessName.Contains(app, StringComparison.OrdinalIgnoreCase); }
                                    catch { return false; }
                                })
                                .ToArray();

                            if (partial.Length > 0)
                            {
                                windows = [];
                                foreach (var p in partial)
                                {
                                    windows.AddRange(uiAutomation.FindWindowsByPid(p.Id));
                                }
                            }
                            else
                            {
                                // Fall back to title search
                                windows = uiAutomation.FindWindowsByTitle(app);
                            }
                        }
                    }
                }
                else
                {
                    // No filter — list ALL visible windows
                    windows = uiAutomation.FindWindowsByTitle("");
                }

                if (json)
                {
                    var foregroundHwnd = (long)Windows.Win32.PInvoke.GetForegroundWindow();
                    var results = windows.Select(w =>
                    {
                        var info = UiTargetResolver.GetWindowInfo(w.Hwnd);
                        return (w, info);
                    })
                    .Where(x => ShouldIncludeWindow(x.w.Title, x.info.Width, x.info.Height, showHidden))
                    .Select(x => new WindowInfo
                    {
                        Hwnd = x.w.Hwnd,
                        ProcessId = x.w.Pid,
                        ProcessName = GetProcessNameSafe(x.w.Pid),
                        Title = string.IsNullOrEmpty(x.w.Title) ? null : x.w.Title,
                        Label = x.info.Label,
                        Width = x.info.Width,
                        Height = x.info.Height,
                        OwnerHwnd = (long)x.info.OwnerHwnd,
                        ClassName = x.info.ClassName,
                        IsForeground = x.w.Hwnd == foregroundHwnd
                    }).ToArray();
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(results, UiJsonContext.Default.WindowInfoArray));
                    return 0;
                }

                // Human-readable output with metadata
                var fgHwnd = (nint)Windows.Win32.PInvoke.GetForegroundWindow();
                var displayedCount = 0;
                foreach (var w in windows)
                {
                    var info = UiTargetResolver.GetWindowInfo(w.Hwnd);

                    if (!ShouldIncludeWindow(w.Title, info.Width, info.Height, showHidden))
                    {
                        continue;
                    }

                    displayedCount++;
                    var procName = Markup.Escape(GetProcessNameSafe(w.Pid));
                    var titleDisplay = string.IsNullOrEmpty(w.Title)
                        ? "Untitled window"
                        : $"\"{Markup.Escape(w.Title)}\"";
                    var label = Markup.Escape(info.Label);
                    var className = Markup.Escape(info.ClassName);
                    var fg = w.Hwnd == fgHwnd ? ", [green]foreground[/]" : "";
                    var owner = info.OwnerHwnd != 0 ? $", owner: HWND {info.OwnerHwnd}" : "";
                    ansiConsole.MarkupLine($"  HWND [cyan]{w.Hwnd}[/]: {titleDisplay} [grey]({label}, {info.Width}x{info.Height}{fg}{owner}) [[{className}]] ({procName}, PID {w.Pid})[/]");
                }

                logger.LogInformation("Found {Count} windows", displayedCount);
                return 0;
            }
            catch (Exception ex)
            {
                UiErrors.GenericError(logger, ex, json);
                return 1;
            }
        }

        private static string GetProcessNameSafe(int pid)
        {
            try { return System.Diagnostics.Process.GetProcessById(pid).ProcessName; }
            catch { return "Unknown"; }
        }
    }
}
