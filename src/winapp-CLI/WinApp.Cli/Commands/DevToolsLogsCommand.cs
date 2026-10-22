// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text.Json;
using Spectre.Console;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Commands;

/// <summary>
/// <c>winapp devtools logs</c>: the Microsoft.UI.Reactor warnings and errors a running app has raised since launch
/// (render and swallowed errors, framework warnings, failed HRESULTs, keyed lists, hot reload and windows), optionally followed.
/// </summary>
internal class DevToolsLogsCommand : DevToolsLiveCommand, IHelpExamples
{
    public override string ShortDescription => "Show a Reactor app's warnings and errors since launch";

    public IReadOnlyList<string> Examples { get; } =
    [
        "winapp devtools logs -a <app>",
        "winapp devtools logs -a <app> --level error --follow",
    ];

    public static Option<bool> FollowOption { get; } = new("--follow", "-f")
    {
        Description = "Keep printing new entries until Ctrl+C or the app exits. With --json, prints each entry as one JSON object per line.",
    };

    public static Option<string> LevelOption { get; } = new("--level")
    {
        Description = "The least severe entries to show: error, warning or info (default info).",
        DefaultValueFactory = _ => "info",
    };

    internal static readonly TimeSpan FollowInterval = TimeSpan.FromMilliseconds(500);

    public DevToolsLogsCommand()
        : base("logs", "Show the Microsoft.UI.Reactor warnings and errors a running app has raised since launch: render and swallowed errors, framework warnings, keyed lists with null or duplicate keys, hot reload and window events. Messages carry exception types, never exception text or user data.",
            windowScoped: false)
    {
        LevelOption.AcceptOnlyFromAmong("error", "warning", "info");
        Options.Add(FollowOption);
        Options.Add(LevelOption);
    }

    public class Handler(
        IDevToolsTargetResolver resolver,
        IAnsiConsole ansiConsole) : LiveHandler(resolver, ansiConsole)
    {
        internal TimeSpan Interval { get; init; } = FollowInterval;

        protected override async Task<int> RunAsync(DevToolsTarget target, ParseResult parseResult, bool json, CancellationToken cancellationToken)
        {
            var follow = parseResult.GetValue(FollowOption);
            var level = parseResult.GetValue(LevelOption) ?? "info";
            long since = 0;
            var first = true;
            while (true)
            {
                var response = target.Tap!.RequestReactorLogs(since, level, cancellationToken);
                if (!response.Ok)
                {
                    if (!first && follow)
                    {
                        // The app went away while following: what was printed stands.
                        return 0;
                    }
                    return Fail(json, target.Pid, response.Error!);
                }
                using var doc = JsonDocument.Parse(response.ResultJson!);
                var root = doc.RootElement;
                var state = root.TryGetProperty("state", out var s) ? s.GetString() : null;
                if (state != "ok")
                {
                    var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null;
                    return state == "absent"
                        ? Fail(json, target.Pid, "This app does not use Microsoft.UI.Reactor, so it has no Reactor logs.", "reactor-absent")
                        : Fail(json, target.Pid, UnavailableMessage(reason), "reactor-unavailable");
                }
                var entries = root.GetProperty("entries");
                if (json && !follow)
                {
                    WriteJson(DevToolsJson.Serialize(writer =>
                    {
                        writer.WriteBoolean("ok", true);
                        writer.WriteNumber("processId", target.Pid);
                        foreach (var member in root.EnumerateObject())
                        {
                            if (member.Name != "state")
                            {
                                member.WriteTo(writer);
                            }
                        }
                    }));
                    return 0;
                }
                foreach (var entry in entries.EnumerateArray())
                {
                    if (json)
                    {
                        WriteJson(entry.GetRawText());
                    }
                    else
                    {
                        DevToolsRender.WriteMarkupLine(Console, Format(entry));
                    }
                }
                if (first && !json)
                {
                    if (entries.GetArrayLength() == 0)
                    {
                        DevToolsRender.WriteMarkupLine(Console, follow
                            ? "[grey]No Reactor warnings or errors yet. Waiting for new entries (Ctrl+C to stop)...[/]"
                            : "[grey]No Reactor warnings or errors since launch.[/]");
                    }
                    if (root.TryGetProperty("dropped", out var dropped) && dropped.GetInt64() > 0)
                    {
                        DevToolsRender.WriteMarkupLine(Console,
                            $"[grey]{dropped.GetInt64()} older entries were dropped; the app keeps the latest 500.[/]");
                    }
                }
                first = false;
                since = root.GetProperty("next").GetInt64();
                if (!follow)
                {
                    return 0;
                }
                try
                {
                    await Task.Delay(Interval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return 0;
                }
            }
        }
    }

    /// <summary>
    /// Why there are no logs, and what to do. A Native AOT app can never load the agent, so relaunching it the same way
    /// would not help; any other app gets the agent from a DevTools launch.
    /// </summary>
    internal static string UnavailableMessage(string? reason)
    {
        if (ReactorDescription.IsBusy(reason))
        {
            return ReactorDescription.BusyMessage;
        }
        var why = reason is { Length: > 0 } ? $"Reactor logs are unavailable: {reason.TrimEnd('.', ' ')}." : "Reactor logs are unavailable.";
        return why + (reason?.Contains("Native AOT", StringComparison.Ordinal) == true
            ? " Logs need the managed DevTools agent: run a build without --aot to read them."
            : " Launch the app with 'winapp run <project> --devtools on'.");
    }

    /// <summary>One entry as a line: time, level and what happened.</summary>
    internal static string Format(JsonElement entry)
    {
        string? Read(string name) => entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
        var time = Read("time") is { } stamp && DateTime.TryParse(stamp, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) : "--:--:--.---";
        var level = Read("level") ?? "info";
        var color = level switch { "error" => "red", "warning" => "yellow", _ => "grey" };
        var where = string.Join("/", new[] { Read("category"), Read("operation") }.Where(part => !string.IsNullOrEmpty(part)));
        var text = Read("kind") switch
        {
            "renderError" => $"Render error in <{Read("component")}>: {Read("exception")}",
            "swallowedError" => $"Swallowed error in {where}: {Read("exception")}",
            "hresult" => $"{where} failed with {Read("hresult")}",
            "warning" => $"{where}: {Read("message")}",
            "hotReload" when Read("source") == "metadataUpdate" => "Hot reload applied an edit",
            "hotReload" when Read("field") is { } field => $"Hot reload dropped {Read("owner")}.{field} ({Read("from")} -> {Read("to")})",
            "hotReload" => $"Hot reload kept the state of {Read("valueType")}",
            "keyedList" => $"Keyed list in {Read("control") ?? "a list"}" +
                (Read("component") is { } owner ? $" of <{owner}>" : "") +
                (Read("file") is { } file ? $" at {Path.GetFileName(file)}:{Read("line")}" : "") + " " +
                (Read("problem") == "nullKey" ? "has an item with a null key" : "has duplicate keys") +
                $" (x{Read("count")}); it re-creates its rows instead of moving them",
            "windowOpened" => $"Window opened: {Read("windowType")}",
            "windowClosed" => $"Window closed: {Read("windowType")}",
            _ => $"{Read("event")}: {Read("detail")}",
        };
        return $"[grey]{time}[/] [{color}]{level,-7}[/] {Markup.Escape(text)}";
    }
}
