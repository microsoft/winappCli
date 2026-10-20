// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Commands;

internal sealed class UiAuditSnapshotCommand : Command, IShortDescription
{
    public static Option<int> DepthOption { get; } = new("--depth", "-d")
    {
        Description = "Maximum raw-tree descendant depth, inclusive (1-50). The selected root has depth 0.",
        DefaultValueFactory = _ => 20,
    };

    public static Option<int> MaxElementsOption { get; } = new("--max-elements")
    {
        Description = "Maximum elements examined, including rejected elements (1-10000). Limits produce a partial snapshot.",
        DefaultValueFactory = _ => 2000,
    };

    public static Option<int> TimeoutOption { get; } = new("--timeout", "-t")
    {
        Description = "Cooperative timeout in milliseconds (1-120000). A blocking UIA provider requires an external process timeout.",
        DefaultValueFactory = _ => 5000,
    };

    public string ShortDescription => "Read a bounded, typed accessibility-evidence snapshot of one window";

    public UiAuditSnapshotCommand()
        : base("audit-snapshot",
            "Read the raw UI Automation tree for one explicitly selected top-level window without focusing it or sending input. " +
            "Child HWNDs inside that window are included; other top-level windows, including owned dialogs, are excluded and reported as window boundaries. " +
            "Use --json to obtain the complete typed snapshot, including unsupported/mixed text formatting attributes and partial-result issues. " +
            "Accessible names can contain user data; password or indeterminate-privacy names and text details are withheld. " +
            "Text content and Value properties are never collected. Formatting is limited to 16 visible runs per node and 32 rectangles per run. " +
            "A partial snapshot is still written to stdout with --json, but exits nonzero.")
    {
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);
        Options.Add(DepthOption);
        Options.Add(MaxElementsOption);
        Options.Add(TimeoutOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public sealed class Handler(
        IUiTargetResolver targetResolver,
        IUiAuditSnapshotService snapshotService,
        IAnsiConsole ansiConsole,
        ILogger<UiAuditSnapshotCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var quiet = parseResult.GetValue(WinAppRootCommand.QuietOption);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var depth = parseResult.GetValue(DepthOption);
            var maximum = parseResult.GetValue(MaxElementsOption);
            var timeout = parseResult.GetValue(TimeoutOption);

            int Fail(string code, string message)
            {
                UiJsonError.Emit(json, code, message, errorOut: parseResult.InvocationConfiguration.Error);
                if (!json)
                {
                    logger.LogError("{Symbol} {Message}", UiSymbols.Error, message);
                }
                return 1;
            }

            if (depth is < 1 or > 50 || maximum is < 1 or > 10000 || timeout is < 1 or > 120000)
            {
                return Fail(UiJsonError.CodeInvalidArguments,
                    "--depth must be 1-50, --max-elements 1-10000, and --timeout 1-120000 milliseconds.");
            }
            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                return Fail(UiJsonError.CodeMissingApp,
                    "Target app required. Use --app <name|title|PID> or --window <HWND>. Run 'winapp ui list-windows' to find a window.");
            }
            if (window is <= 0)
            {
                return Fail(UiJsonError.CodeInvalidArguments, "--window must be a positive HWND from 'winapp ui list-windows'.");
            }

            using var deadline = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                UiTarget target;
                try
                {
                    target = await targetResolver.ResolveAsync(app, window, linked.Token);
                }
                catch (InvalidOperationException)
                {
                    return Fail(UiJsonError.CodeNoTarget,
                        "The selected window could not be resolved. Use --window with an exact HWND from 'winapp ui list-windows'.");
                }
                if (target.WindowHandle <= 0 || target.ProcessId <= 0 || (window is not null && target.WindowHandle != window.Value))
                {
                    return Fail("invalid_target", "The target resolver did not return the exact requested window with a positive process ID.");
                }

                var result = await snapshotService.CaptureAsync(target, new UiAuditSnapshotOptions
                {
                    MaxDepth = depth,
                    MaxElements = maximum,
                    TimeoutMs = timeout,
                }, linked.Token);
                if (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    result = result with
                    {
                        Issues = result.Issues.Select(issue => issue.Code == "cancelled"
                            ? new UiAuditSnapshotIssue("deadline_reached", "The cooperative snapshot deadline was reached.") : issue).ToArray(),
                    };
                }

                if (json)
                {
                    ansiConsole.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(result, UiJsonContext.Default.UiAuditSnapshotResult));
                }
                else if (!quiet)
                {
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        $"{result.Status}: {result.Nodes.Count} elements from HWND {result.WindowHandle}, PID {result.ProcessId}.");
                    foreach (var issue in result.Issues)
                    {
                        parseResult.InvocationConfiguration.Error.WriteLine($"{issue.Code}: {issue.Message}");
                    }
                    ansiConsole.Profile.Out.Writer.WriteLine("Use --json for the complete snapshot, node issues, and typed text attributes.");
                }
                return result.Status == "captured" ? 0 : Fail(UiJsonError.CodePartialOutput,
                    "The snapshot is partial. Preserve the stdout JSON and inspect its issues before drawing conclusions.");
            }
            catch (UiAuditSnapshotException ex)
            {
                return ex.Code == "cancelled" && deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                    ? Fail("deadline_reached", "The snapshot deadline was reached before a document could be returned.")
                    : Fail(ex.Code, ex.Message);
            }
            catch (AppNotFoundException)
            {
                return Fail(UiJsonError.CodeNoTarget, "The selected app or window was not found. Run 'winapp ui list-windows' and choose an existing HWND.");
            }
            catch (OperationCanceledException)
            {
                return deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                    ? Fail("deadline_reached", "The snapshot deadline was reached before a document could be returned.")
                    : Fail("cancelled", "Snapshot capture was cancelled before a document could be returned.");
            }
            catch (Exception ex)
            {
                logger.LogDebug("UI audit snapshot failed ({ExceptionType}).", ex.GetType().Name);
                return Fail(UiJsonError.CodeInternalError, "Snapshot capture failed. Verify the selected window and try again.");
            }
        }
    }
}
