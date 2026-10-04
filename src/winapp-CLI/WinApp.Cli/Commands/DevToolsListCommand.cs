// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Commands;

/// <summary>
/// <c>winapp devtools list</c> — discover already-injected DevTools apps and, when requested, running WinUI
/// candidates that can be attached by exact PID. Attached apps are probed through DevTools; available candidates
/// are identified centrally by the loaded WinUI runtime module so external clients never scan processes.
/// </summary>
internal class DevToolsListCommand : Command, IShortDescription, IHelpExamples
{
    public string ShortDescription => "List attached DevTools apps or running WinUI attach candidates";

    public IReadOnlyList<string> Examples { get; } =
    [
        "winapp devtools list",
        "winapp devtools list --include-available",
    ];

    internal static Option<bool> IncludeAvailableOption { get; } = new("--include-available")
    {
        Description = "Include running WinUI apps that do not yet have DevTools attached.",
    };

    internal static Option<bool> GuestDiscoveryOption { get; } = new("--guest-discovery") { Hidden = true };

    public DevToolsListCommand()
        : base("list", "List apps with DevTools attached.")
    {
        Options.Add(WinAppRootCommand.JsonOption);
        Options.Add(IncludeAvailableOption);
        Options.Add(GuestDiscoveryOption);
    }

    public class Handler(
        IAnsiConsole ansiConsole,
        ILogger<DevToolsListCommand> logger,
        Func<IReadOnlyList<int>>? attachedPidLister = null) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var includeAvailable = parseResult.GetValue(IncludeAvailableOption);
            var guestDiscovery = parseResult.GetValue(GuestDiscoveryOption);

            var attachedPids = (attachedPidLister?.Invoke() ?? DevToolsPipeDiscovery.EnumerateInjectedPids()).ToHashSet();
            var pids = includeAvailable
                ? attachedPids.Concat(EnumerateWinUiCandidatePids()).Distinct().Order().ToArray()
                : attachedPids.Order().ToArray();
            var apps = new List<DevToolsAppInfo>(pids.Length);
            foreach (var pid in pids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                apps.Add(attachedPids.Contains(pid) ? Probe(pid, guestDiscovery, cancellationToken) : DescribeAvailable(pid, guestDiscovery));
            }

            logger.LogDebug(
                "Discovered {Count} WinUI app(s), including {AttachedCount} with DevTools attached.",
                apps.Count,
                apps.Count(app => app.Attached));

            if (json)
            {
                var payload = new DevToolsListPayload { Apps = apps };
                ansiConsole.Profile.Out.Writer.WriteLine(
                    JsonSerializer.Serialize(payload, DevToolsProtocolJsonContext.Default.DevToolsListPayload));
                return Task.FromResult(0);
            }

            if (apps.Count == 0)
            {
                DevToolsRender.WriteMarkupLine(ansiConsole, includeAvailable
                    ? "[grey]No running WinUI apps found.[/]"
                    : "[grey]No DevTools-injected apps found. Use --include-available to show running WinUI apps.[/]");
                return Task.FromResult(0);
            }

            foreach (var app in apps)
            {
                var name = app.ProcessName is null ? string.Empty : $" [teal]{Markup.Escape(app.ProcessName)}[/]";
                var title = app.WindowTitle is null ? string.Empty : $" \"{Markup.Escape(app.WindowTitle)}\"";
                if (!app.Attached)
                {
                    DevToolsRender.WriteMarkupLine(ansiConsole,
                        $"[cyan]{app.Pid}[/]{name}{title}  [yellow]available[/]  [grey]ready to attach[/]");
                    continue;
                }

                var proto = app.ProtocolVersion is null ? "[grey]?[/]" : $"v{Markup.Escape(app.ProtocolVersion)}";
                var nodes = app.NodeCount is int n ? $"{n} nodes" : "unresponsive";
                var posture = app.Posture is null ? string.Empty : $" [grey]({Markup.Escape(app.Posture)})[/]";
                DevToolsRender.WriteMarkupLine(ansiConsole,
                    $"[cyan]{app.Pid}[/]{name}{title}  [green]attached[/]  " +
                    $"[grey]{Markup.Escape(app.PipeName ?? string.Empty)}[/]  {proto}  {nodes}{posture}");
            }

            return Task.FromResult(0);
        }

        // Probe one injected pid: resolve its process name, then best-effort read hello + node count. A tap
        // whose pipe exists but doesn't answer (busy/wedged) is still listed, marked unresponsive.
        private static DevToolsAppInfo Probe(int pid, bool guestDiscovery, CancellationToken cancellationToken)
        {
            var info = new DevToolsAppInfo
            {
                Pid = pid,
                PipeName = DevToolsPipeDiscovery.PipeNameFor(pid),
                Attached = true,
                Status = "attached",
            };
            PopulateIdentity(info, guestDiscovery);

            var tap = new VisualTreeTap(unchecked((uint)pid));
            if (TapHello.TryParse(tap.Hello(cancellationToken: cancellationToken).ResultJson) is TapHello hello)
            {
                info.Responsive = true;
                info.ProtocolVersion = hello.ProtocolVersion;
                info.Mutation = hello.Mutation;
                info.Posture = hello.Posture;
            }

            var nodeCount = tap.TryGetNodeCount(cancellationToken);
            if (nodeCount is int nc)
            {
                info.Responsive = true;
                info.NodeCount = nc < 0 ? 0 : nc;
            }

            return info;
        }

        private static DevToolsAppInfo DescribeAvailable(int pid, bool guestDiscovery)
        {
            var info = new DevToolsAppInfo
            {
                Pid = pid,
                Attached = false,
                Status = "available",
            };
            PopulateIdentity(info, guestDiscovery);
            return info;
        }

        private static IEnumerable<int> EnumerateWinUiCandidatePids()
        {
            foreach (var process in Process.GetProcesses())
            {
                int? candidatePid = null;
                using (process)
                {
                    try
                    {
                        if (!process.HasExited &&
                            process.Id != Environment.ProcessId &&
                            process.Modules.Cast<ProcessModule>().Any(module => IsWinUiModule(module.ModuleName)))
                        {
                            candidatePid = process.Id;
                        }
                    }
                    catch (Exception ex) when (
                        ex is Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                        // Protected, exited, or cross-architecture processes are not attach candidates this CLI
                        // can inspect safely. Continue without weakening discovery to process-name heuristics.
                    }
                }

                if (candidatePid is int pid)
                {
                    yield return pid;
                }
            }
        }

        internal static bool IsWinUiModule(string? moduleName) =>
            string.Equals(moduleName, "Microsoft.UI.Xaml.dll", StringComparison.OrdinalIgnoreCase);

        private static void PopulateIdentity(DevToolsAppInfo info, bool guestDiscovery)
        {
            try
            {
                using var process = Process.GetProcessById(info.Pid);
                if (guestDiscovery)
                {
                    _ = process.Handle;
                }
                info.ProcessName = process.ProcessName;
                var title = process.MainWindowTitle;
                info.WindowTitle = string.IsNullOrWhiteSpace(title) ? null : title;
                if (guestDiscovery && !process.HasExited)
                {
                    info.StartTicksUtc = process.StartTime.ToUniversalTime().Ticks
                        .ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex) when (
                ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // Identity is best-effort. The exact PID and attached status remain authoritative.
            }
        }
    }
}
