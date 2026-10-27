// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Commands;

internal sealed class UiRecordActionsCommand : Command, IShortDescription
{
    public static Option<bool> CaptureTextOption { get; } = new("--capture-text")
    {
        Description = "Opt in to storing full non-password edit-field values. By default text becomes named replay parameters. Password fields always stop recording as incomplete.",
    };

    public static Option<bool> IncludeInjectedOption { get; } = new("--include-injected")
    {
        Description = "Also record OS-injected input, for controlled automation tests. By default only hardware input is recorded.",
    };

    public static Option<int> MaxStepsOption { get; } = new("--max-steps")
    {
        Description = "Maximum retained actions (1-10000). Reaching the limit stops recording as incomplete.",
        DefaultValueFactory = _ => 1000,
    };

    public string ShortDescription => "Record a single window's inputs as a reviewable action-file draft";

    public UiRecordActionsCommand()
        : base("record-actions", "Record clicks, keys, full-field text replacements, and wheel input in one explicitly selected window. " +
               "Text becomes named parameters unless --capture-text is supplied. Review the JSON draft before using ui replay. " +
               "Default duration 0 stops on Ctrl+C or redirected-stdin newline/EOF; timed recordings support 1-3600 seconds. " +
               "Use ui record instead for MP4 video.")
    {
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);
        Options.Add(SharedUiOptions.OutputOption);
        Options.Add(SharedUiOptions.DurationSecOption);
        Options.Add(CaptureTextOption);
        Options.Add(IncludeInjectedOption);
        Options.Add(MaxStepsOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public sealed class Handler(
        IUiTargetResolver targetResolver,
        IUiActionService actionService,
        IAnsiConsole ansiConsole,
        ILogger<UiRecordActionsCommand> logger) : AsynchronousCommandLineAction
    {
        internal Func<bool> IsInputRedirected { get; set; } = () => Console.IsInputRedirected;
        internal TextReader? StdinOverride { get; set; }

        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var quiet = parseResult.GetValue(WinAppRootCommand.QuietOption);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var durationSec = parseResult.GetValue(SharedUiOptions.DurationSecOption);
            var maxSteps = parseResult.GetValue(MaxStepsOption);
            var captureText = parseResult.GetValue(CaptureTextOption);
            var includeInjected = parseResult.GetValue(IncludeInjectedOption);

            int Fail(string code, string message)
            {
                UiJsonError.Emit(json, code, message, errorOut: parseResult.InvocationConfiguration.Error);
                if (!json)
                {
                    logger.LogError("{Symbol} {Message}", UiSymbols.Error, message);
                }
                return 1;
            }

            if (durationSec is < 0 or > 3600)
            {
                return Fail(UiJsonError.CodeInvalidArguments, "--duration-sec must be between 0 and 3600.");
            }
            if (maxSteps is < 1 or > UiActionFlowDocument.MaximumSteps)
            {
                return Fail(UiJsonError.CodeInvalidArguments, "--max-steps must be between 1 and 10000.");
            }
            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                return Fail(UiJsonError.CodeMissingApp,
                    "Target app required. Use --app <name|title|PID> or --window <HWND>. Run 'winapp ui list-windows' to find running apps.");
            }
            if (window is <= 0)
            {
                return Fail(UiJsonError.CodeInvalidArguments, "--window must be a positive HWND from 'winapp ui list-windows'.");
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stdinMonitorStopped = false;
            try
            {
                string filePath;
                try
                {
                    filePath = Path.GetFullPath(parseResult.GetValue(SharedUiOptions.OutputOption)
                        ?? $"ui-actions-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
                    if (Path.Exists(filePath))
                    {
                        return Fail(UiJsonError.CodeOutputExists, "Action output already exists. Choose a new --output path; recordings are never overwritten.");
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
                {
                    return Fail(UiJsonError.CodeInvalidArguments, "Output path could not be prepared. Choose a new, writable --output file path.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                UiTarget target;
                try
                {
                    target = await targetResolver.ResolveAsync(app, window, cancellationToken);
                }
                catch (InvalidOperationException ex) when (ex is not UiActionException)
                {
                    return Fail(UiJsonError.CodeNoTarget, "The selected window could not be resolved for recording. Use --window with an HWND from 'winapp ui list-windows'.");
                }
                var redirected = IsInputRedirected();
                if (!json && !quiet)
                {
                    parseResult.InvocationConfiguration.Error.WriteLine(captureText
                        ? "Privacy: --capture-text stores full non-password field values in the action file. Do not enter secrets."
                        : "Privacy: text is stored as named parameter placeholders, not field values.");
                    parseResult.InvocationConfiguration.Error.WriteLine(
                        "Password fields stop recording as incomplete. Input outside the selected window pauses capture and is not logged.");
                    if (includeInjected)
                    {
                        parseResult.InvocationConfiguration.Error.WriteLine(
                            "--include-injected is enabled: OS-injected input will also be recorded.");
                    }
                    var until = durationSec > 0 ? $"{durationSec}s"
                        : redirected ? "Ctrl+C or redirected-stdin newline/EOF" : "Ctrl+C";
                    parseResult.InvocationConfiguration.Error.WriteLine($"Recording action draft to {filePath} until {until}.");
                }

                if (redirected && durationSec == 0)
                {
                    StdinStopMonitor.Start(StdinOverride ?? Console.In, ready.Task, () =>
                    {
                        if (!Volatile.Read(ref stdinMonitorStopped))
                        {
                            try { linkedCts.Cancel(); }
                            catch (ObjectDisposedException) { }
                        }
                    });
                }

                var started = 0;
                void OnReady()
                {
                    if (Interlocked.Exchange(ref started, 1) != 0)
                    {
                        return;
                    }
                    if (json)
                    {
                        var startedEvent = new UiActionsRecordStartedEvent
                        {
                            Path = filePath,
                            DurationSec = durationSec,
                            CaptureText = captureText,
                            IncludeInjected = includeInjected,
                        };
                        parseResult.InvocationConfiguration.Error.WriteLine(
                            JsonSerializer.Serialize(startedEvent, UiJsonLineContext.Default.UiActionsRecordStartedEvent));
                    }
                    ready.TrySetResult();
                }

                var result = await actionService.RecordAsync(target, new UiActionRecordOptions
                {
                    DurationSec = durationSec,
                    CaptureText = captureText,
                    IncludeInjected = includeInjected,
                    MaxSteps = maxSteps,
                }, linkedCts.Token, OnReady);

                var flow = result.Flow;
                if (flow.Steps.Count == 0 && flow.Status == "recorded")
                {
                    flow = flow with { Status = "incomplete", Warnings = [.. flow.Warnings, "No supported actions were recorded."] };
                }
                // Stopping capture cancels the capture token, not persistence of the retained draft.
                UiActionFlowDocument.WriteNew(filePath, flow);

                var parameters = flow.Steps.Where(step => step.Parameter is not null)
                    .Select(step => step.Parameter!).Distinct(StringComparer.Ordinal).ToArray();
                if (json)
                {
                    var payload = new UiActionsRecordResult
                    {
                        Path = filePath,
                        Status = flow.Status,
                        StepCount = flow.Steps.Count,
                        StopReason = result.StopReason,
                        Warnings = flow.Warnings,
                        Parameters = parameters,
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(payload, UiJsonContext.Default.UiActionsRecordResult));
                }
                else
                {
                    foreach (var warning in flow.Warnings)
                    {
                        parseResult.InvocationConfiguration.Error.WriteLine($"Warning: {warning}");
                    }
                    if (!quiet)
                    {
                        ansiConsole.Profile.Out.Writer.WriteLine(
                            $"Saved {flow.Status} draft with {flow.Steps.Count} actions to {filePath} (stop: {result.StopReason}).");
                        if (parameters.Length > 0)
                        {
                            ansiConsole.Profile.Out.Writer.WriteLine($"Required replay parameters: {string.Join(", ", parameters)}");
                        }
                        ansiConsole.Profile.Out.Writer.WriteLine(flow.Status == "recorded"
                            ? "Review every action before replaying with --accept-recording."
                            : "This incomplete draft cannot be replayed. Review the warnings and record a fresh supported flow.");
                    }
                }
                return flow.Status == "recorded" ? 0 : 1;
            }
            catch (UiActionException ex)
            {
                return Fail(ex.Code, ex.Message);
            }
            catch (AppNotFoundException)
            {
                return Fail(UiJsonError.CodeNoTarget, "The selected app or window was not found. Run 'winapp ui list-windows' and choose a running window.");
            }
            catch (UiAmbiguousSelectorException)
            {
                return Fail(UiJsonError.CodeAmbiguousSelector, "An action target is ambiguous. Give the control a unique AutomationId and record again.");
            }
            catch (UiElementNotFoundException)
            {
                return Fail(UiJsonError.CodeElementNotFound, "An action target could not be found in the selected window.");
            }
            catch (OperationCanceledException)
            {
                return Fail("cancelled", "Action recording was cancelled before a draft could be saved.");
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 80 or 183)
            {
                return Fail(UiJsonError.CodeOutputExists, "Action output already exists. Choose a new --output path; recordings are never overwritten.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Fail("output_failed", "The action draft could not be saved. Check disk space and permissions, then record to a new --output path.");
            }
            catch (Exception ex)
            {
                logger.LogDebug("Action recording failed ({ExceptionType}).", ex.GetType().Name);
                return Fail(UiJsonError.CodeInternalError, "Action recording failed. Check the selected window and try recording a fresh flow.");
            }
            finally
            {
                Volatile.Write(ref stdinMonitorStopped, true);
                ready.TrySetResult();
            }
        }
    }
}
