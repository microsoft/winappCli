// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Commands;

/// <summary>
/// <c>winapp devtools attach --pid &lt;n&gt;</c> — inject the DevTools inspection agent into an
/// already-running WinUI 3 app that winapp did not launch, so an external protocol client can connect to the
/// resulting <c>winapp-devtools-&lt;pid&gt;</c> pipe. Reuses <see cref="IDevToolsService.ConnectAsync"/> (the same
/// inject orchestration <c>run --devtools</c> uses) and emits the connection info (pipe name, protocol
/// version, node count) for a client to consume, ideally via <c>--json</c>.
///
/// <para>Unlike interactive <c>run --devtools</c>, <c>attach</c> is headless: <c>--overlay</c>/<c>--show-window</c>
/// request UI posture. The connection is always writable — the injection posture is fixed for the process
/// lifetime, so attaching read-only would make every later live edit impossible until the app restarts, and
/// there is no CLI flag that could undo it.</para>
/// </summary>
internal class DevToolsAttachCommand : Command, IShortDescription, IHelpExamples
{
    public string ShortDescription => "Enable DevTools in a running app";

    public IReadOnlyList<string> Examples { get; } =
    [
        "winapp devtools attach --pid <pid>",
        "winapp devtools attach --pid <pid> --overlay",
    ];

    public static Option<int> PidOption { get; } = new("--pid")
    {
        Description = "Process id of the running WinUI 3 app to attach to.",
    };

    public static Option<string?> AppOption { get; } = new("--app", "-a")
    {
        Description = "App PID, process name, or window title; use --on sandbox for a guest app.",
    };

    public static Option<bool> OverlayOption { get; } = new("--overlay")
    {
        Description = "Show the in-app DevTools overlay after attaching.",
    };

    // Named --show-window, NOT --window: across the CLI, --window/-w means an HWND to target. This flag asks
    // for a UI surface to be OPENED, which is a different thing entirely.
    public static Option<bool> ShowWindowOption { get; } = new("--show-window")
    {
        Description = "Open the in-process DevTools inspector window and its supporting overlay after attaching.",
    };

    public DevToolsAttachCommand()
        : base("attach", "Enable DevTools in a running WinUI 3 app until that app exits.")
    {
        Options.Add(PidOption);
        Options.Add(AppOption);
        Options.Add(OverlayOption);
        Options.Add(ShowWindowOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        IDevToolsService devToolsService,
        IStatusService statusService,
        IAnsiConsole ansiConsole,
        ILogger<DevToolsAttachCommand> logger,
        IUiTargetResolver targetResolver) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var pid = parseResult.GetValue(PidOption);
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var showOverlay = parseResult.GetValue(OverlayOption);
            var showWindow = parseResult.GetValue(ShowWindowOption);
            var showInteractiveSurface = showOverlay || showWindow;

            if (parseResult.GetValue(AppOption) is { } app)
            {
                if (parseResult.GetResult(PidOption) is { Implicit: false })
                {
                    return Fail(json, pid, "Choose --app or --pid, not both.");
                }
                try
                {
                    pid = (await targetResolver.ResolveProcessAsync(app, cancellationToken).ConfigureAwait(false)).ProcessId;
                }
                catch (Exception ex) when (ex is AppNotFoundException or InvalidOperationException)
                {
                    return Fail(json, pid, ex.Message);
                }
            }

            // Every DevTools connection is writable. The posture is decided once, by the FIRST injection,
            // for the whole process lifetime — so a read-only attach here is not a reversible choice, it
            // permanently blocks `devtools set-property` on this app until it restarts.
            var requestedAccess = DevToolsAccess.Mutation;

            if (pid <= 0)
            {
                return Fail(json, pid, $"Invalid --pid '{pid}'. Provide the process id of a running app.");
            }

            // Pre-flight: distinguish a bad/absent pid up front so a later inject failure isn't misread. Keep
            // the handle to re-check HasExited if the attach fails.
            Process? target;
            try
            {
                target = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                return Fail(json, pid, $"Process {pid} is not running.");
            }

            using var targetLifetime = target;
            DevToolsConnection? connection = null;
            if (json)
            {
                connection = await devToolsService.ConnectAsync((uint)pid, showInteractiveSurface, requestedAccess, cancellationToken);
            }
            else
            {
                await statusService.ExecuteWithStatusAsync(
                    $"Attaching DevTools to process {pid}...",
                    async (_, ct) =>
                    {
                        connection = await devToolsService.ConnectAsync((uint)pid, showInteractiveSurface, requestedAccess, ct);
                        return (connection.Connected ? 0 : 1, string.Empty);
                    }, cancellationToken);
            }
            if (connection is null)
            {
                return Fail(json, pid, "DevTools attachment did not complete.");
            }

            if (!connection.Connected)
            {
                // a target that exited/crashed during attach surfaces from the injector as a confusing
                // FrameworkUdk / element-not-found error. Re-check the process so we report the real cause.
                var diagnosed = DiagnoseFailure(target, connection.Error);
                return Fail(json, pid, diagnosed);
            }

            // Negotiate the protocol version + mutation posture over the now-live pipe (best-effort).
            var tap = new VisualTreeTap(unchecked((uint)pid));
            var helloResponse = tap.Hello(cancellationToken: cancellationToken);
            var hello = TapHello.TryParse(helloResponse.ResultJson);
            if (hello is null && tap.LastConnectionError is not null)
            {
                return Fail(json, pid, tap.LastConnectionError);
            }
            if (hello is null)
            {
                return Fail(json, pid, "DevTools attached, but DevTools.negotiate did not return a valid trust posture.");
            }
            // The posture the process actually ended up with may be lower than the mutation we asked for: an
            // EARLIER injection already fixed it, or WINAPP_DEVTOOLS_MUTATION=deny capped it to 'ui'. Neither
            // is an attach failure — inspection still works — so attach succeeds and states the limitation.
            // Failing here would mean the deny floor made `devtools attach` unusable outright.
            var writable = Allows(hello.Posture, DevToolsAccess.Mutation);
            var postureNote = writable
                ? null
                : $"This process has '{hello.Posture}' posture, so live edits (devtools set-property) are refused. " +
                  "The FIRST injection fixes posture for the process lifetime — restart the target to change it. " +
                  "WINAPP_DEVTOOLS_MUTATION=deny caps every mutation request to 'ui'.";
            if (showInteractiveSurface && !connection.OverlayShown)
            {
                return Fail(json, pid, $"DevTools attached, but the requested in-app overlay did not open. {connection.OverlayError} " +
                    $"Headless inspection remains available: winapp devtools inspect -a {pid}.");
            }
            if (showWindow && !tap.OpenWindow(cancellationToken))
            {
                return Fail(json, pid, $"DevTools attached, but Window.open did not produce a live inspector window. {tap.LastConnectionError}");
            }

            var payload = new DevToolsAttachPayload
            {
                Ok = true,
                Pid = pid,
                PipeName = DevToolsPipeDiscovery.PipeNameFor(pid),
                ProtocolVersion = hello?.ProtocolVersion,
                Mutation = hello?.Mutation,
                Posture = hello?.Posture,
                Writable = writable,
                NodeCount = connection.NodeCount,
                OverlayShown = showInteractiveSurface ? true : null,
                WindowOpen = showWindow ? true : null,
                // A connected-but-degraded attach (visual tree didn't bind, or a capped posture) is still
                // Ok=true with a warning: the protocol is up and readable either way.
                Error = connection.Error ?? postureNote,
            };

            logger.LogDebug("DevTools attached to {Pid}: {Count} nodes, protocol {Proto}.",
                pid, connection.NodeCount, payload.ProtocolVersion ?? "?");

            if (json)
            {
                ansiConsole.Profile.Out.Writer.WriteLine(
                    JsonSerializer.Serialize(payload, DevToolsProtocolJsonContext.Default.DevToolsAttachPayload));
                return 0;
            }

            ansiConsole.MarkupLineInterpolated(
                $"{UiSymbols.Check} DevTools attached to process {pid} — {connection.NodeCount} nodes.");
            ansiConsole.MarkupLineInterpolated($"   Pipe: [grey]{payload.PipeName}[/]");
            if (payload.ProtocolVersion is not null)
            {
                ansiConsole.MarkupLineInterpolated(
                    $"   Protocol: v{payload.ProtocolVersion} ({payload.Posture ?? "unknown posture"})");
            }
            if (showInteractiveSurface)
            {
                ansiConsole.MarkupLine($"   Overlay: [green]shown[/]");
            }
            if (showWindow)
            {
                ansiConsole.MarkupLine($"   Window: [green]open[/]");
            }

            if (connection.Error is not null)
            {
                ansiConsole.MarkupLineInterpolated($"{UiSymbols.Warning} {connection.Error}");
            }
            else if (postureNote is not null)
            {
                ansiConsole.MarkupLineInterpolated($"{UiSymbols.Warning} {postureNote}");
            }

            return 0;
        }

        internal static bool Allows(string posture, DevToolsAccess requested) =>
            (posture, requested) switch
            {
                ("read" or "ui" or "mutation", DevToolsAccess.Read) => true,
                ("ui" or "mutation", DevToolsAccess.Ui) => true,
                ("mutation", DevToolsAccess.Mutation) => true,
                _ => false,
            };

        // Turn an inject failure into a diagnosable message: if the target has since exited, say so plainly
        // instead of leaking a FrameworkUdk/HRESULT error the user would chase down the wrong path.
        private static string DiagnoseFailure(Process target, string? underlyingError)
        {
            try
            {
                target.Refresh();
                if (target.HasExited)
                {
                    return $"Target process {target.Id} exited before the DevTools agent could attach " +
                           $"(the app closed or crashed on startup, exit code {target.ExitCode}). " +
                           "This is not a FrameworkUdk problem — re-launch the app and attach again.";
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Process object no longer queryable — treat as exited.
                return $"Target process exited before the DevTools agent could attach " +
                       "(the app closed or crashed on startup). This is not a FrameworkUdk problem.";
            }

            return underlyingError ?? "DevTools attach failed for an unknown reason.";
        }

        private int Fail(bool json, int pid, string message)
        {
            if (json)
            {
                var payload = new DevToolsAttachPayload { Ok = false, Pid = pid, Error = message };
                ansiConsole.Profile.Out.Writer.WriteLine(
                    JsonSerializer.Serialize(payload, DevToolsProtocolJsonContext.Default.DevToolsAttachPayload));
            }
            else
            {
                ansiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(message)}");
            }

            return 1;
        }
    }
}
