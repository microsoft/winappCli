// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Globalization;
using Spectre.Console;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

/// <summary>Captures native guest desktop pixels and delivers the PNG to the host.</summary>
internal class TargetScreenshotCommand : Command, IShortDescription
{
    public string ShortDescription => "Capture an execution target's whole desktop as a PNG";
    public static Argument<string> SelectorArgument { get; } = TargetVerb.NewSelectorArgument();

    public TargetScreenshotCommand() : base("screenshot",
        "Capture an execution target's entire desktop at its native pixel size. " +
        "Saves a PNG on this machine without activating a host or guest window. " +
        "JSON includes the guest screen origin and pixel-coordinate mapping.")
    {
        Arguments.Add(SelectorArgument);
        Options.Add(SharedUiOptions.OutputOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public sealed class Handler(ExecutionTargetOrchestrator orchestrator, IAnsiConsole console)
        : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default) =>
            TargetDesktopCapture.InvokeAsync(orchestrator, console, parseResult,
                parseResult.GetValue(SelectorArgument), recording: false, cancellationToken);
    }
}

/// <summary>Records in the guest and delivers finalized video and frame artifacts to the host.</summary>
internal class TargetRecordCommand : Command, IShortDescription
{
    public string ShortDescription => "Record an execution target's whole desktop to an MP4";
    public static Argument<string> SelectorArgument { get; } = TargetVerb.NewSelectorArgument();

    public TargetRecordCommand() : base("record",
        "Record an execution target's entire desktop without activating a host or guest window. " +
        "The MP4 and optional frame bundle are delivered to this machine when recording finishes. " +
        "JSON and the frame manifest describe the guest-screen mapping after scaling or padding. " +
        "Prefer --duration-sec; otherwise stop with Ctrl+C or a newline on redirected stdin.")
    {
        Arguments.Add(SelectorArgument);
        Options.Add(SharedUiOptions.DurationSecOption);
        Options.Add(SharedUiOptions.FpsOption);
        Options.Add(SharedUiOptions.MaxEdgeOption);
        Options.Add(SharedUiOptions.OutputOption);
        Options.Add(UiRecordCommand.FramesOption);
        Options.Add(UiRecordCommand.OverwriteOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public sealed class Handler(ExecutionTargetOrchestrator orchestrator, IAnsiConsole console)
        : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default) =>
            TargetDesktopCapture.InvokeAsync(orchestrator, console, parseResult,
                parseResult.GetValue(SelectorArgument), recording: true, cancellationToken);
    }
}

internal static class TargetDesktopCapture
{
    internal static async Task<int> InvokeAsync(
        ExecutionTargetOrchestrator orchestrator, IAnsiConsole console, ParseResult parseResult,
        string? selector, bool recording, CancellationToken cancellationToken)
    {
        var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
        UiRecordResolvedOptions? options = null;
        if (recording && UiRecordOptionValidator.Validate(parseResult, out options) is { } error)
        {
            return TargetOutput.RejectOptions(console, json, ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TargetInvalidArguments, error.Message,
                userAction: error.RecoveryHint ?? "Correct the recording options and retry.",
                example: "winapp target record sandbox -o .\\desktop.mp4 --duration-sec 10").Error);
        }
        try
        {
            _ = TargetVerb.Resolve(orchestrator, selector);
        }
        catch (ExecutionTargetException ex)
        {
            return TargetOutput.RejectSelection(console, json, ex.Error);
        }

        var verb = recording ? "record" : "screenshot";
        var arguments = new List<string> { GuestDesktopCaptureCommand.Verb, verb };
        arguments.AddRange(["--output", options?.FilePath ??
            parseResult.GetValue(SharedUiOptions.OutputOption) ?? "screenshot.png"]);
        if (options is not null)
        {
            arguments.AddRange([
                "--duration-sec", options.DurationSec.ToString(CultureInfo.InvariantCulture),
                "--fps", options.Fps.ToString(CultureInfo.InvariantCulture),
                "--max-edge", options.MaxEdge.ToString(CultureInfo.InvariantCulture)]);
            if (options.FramesDirectory is not null)
            {
                arguments.Add("--frames");
            }
            if (parseResult.GetValue(UiRecordCommand.OverwriteOption))
            {
                arguments.Add("--overwrite");
            }
        }
        if (json)
        {
            arguments.Add("--json");
        }
        if (parseResult.GetValue(WinAppRootCommand.QuietOption))
        {
            arguments.Add("--quiet");
        }
        return await new ExecutionTargetUiRouter(orchestrator, console).RouteAsync(
            arguments,
            TargetUiRequirements.Interactive with { CommandName = verb, GuestDesktopCapture = true },
            json, cancellationToken).ConfigureAwait(false);
    }
}
