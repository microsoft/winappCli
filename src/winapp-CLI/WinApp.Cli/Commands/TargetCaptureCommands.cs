// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;
using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Commands;

/// <summary>Captures the rendered target desktop on the host without activating its client window.</summary>
internal class TargetScreenshotCommand : Command, IShortDescription
{
    /// <inheritdoc/>
    public string ShortDescription => "Capture an execution target's whole desktop as a PNG";

    /// <summary>Which target to capture.</summary>
    public static Argument<string> SelectorArgument { get; } = TargetVerb.NewSelectorArgument();

    /// <summary>Creates the command.</summary>
    public TargetScreenshotCommand()
        : base(
            "screenshot",
            "Capture an execution target's entire desktop as a PNG on this machine. " +
            "Captures the whole rendered guest desktop, so no application or window has to be named.")
    {
        Arguments.Add(SelectorArgument);
        Options.Add(SharedUiOptions.OutputOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    /// <summary>Resolves the target's desktop window and captures it.</summary>
    public class Handler(
        ExecutionTargetOrchestrator orchestrator,
        IWindowCapture windowCapture,
        IAnsiConsole console,
        ILogger<TargetScreenshotCommand> logger) : AsynchronousCommandLineAction
    {
        /// <inheritdoc/>
        public override async Task<int> InvokeAsync(
            ParseResult parseResult,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(parseResult);

            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            ExecutionTargetRef reference;

            try
            {
                reference = TargetVerb.Resolve(orchestrator, parseResult.GetValue(SelectorArgument));
            }
            catch (ExecutionTargetException ex)
            {
                return TargetOutput.RejectSelection(console, json, ex.Error);
            }

            var filePath = Path.GetFullPath(
                parseResult.GetValue(SharedUiOptions.OutputOption) ?? "screenshot.png");

            try
            {
                // Capture needs a rendering desktop, but must not activate the client window.
                await using var target = await orchestrator
                    .PrepareAsync(PrepareTargetOptions.Interactive, cancellationToken)
                    .ConfigureAwait(false);

                var surface = orchestrator.ResolveDesktopSurface(TargetDesktopUse.PixelCapture);

                var frame = await windowCapture
                    .TryCaptureWindowWithoutActivationAsync(surface.WindowHandle, cancellationToken)
                    .ConfigureAwait(false);

                if (frame is not { } captured)
                {
                    return TargetOutput.Fail(console, json, ExecutionTargetException.Create(
                        ExecutionTargetErrorCodes.ArtifactFailed,
                        $"The {reference.Selector} desktop could not be captured without bringing its " +
                        "window to the front, so nothing was captured.",
                        userAction:
                            "The target's window is minimized, has no size, or is not rendering. " +
                            $"'winapp target snapshot {reference.Selector}' reports which.",
                        example: $"winapp target snapshot {reference.Selector}").Error);
                }

                var (pixels, width, height) = captured;

                var png = PngImage.Encode(pixels, width, height);

                if (Path.GetDirectoryName(filePath) is { } directory && directory.Length > 0)
                {
                    Directory.CreateDirectory(directory);
                }

                // Keep the previous screenshot intact until replacement is ready.
                await AtomicFile.WriteAllBytesAsync(filePath, png, cancellationToken).ConfigureAwait(false);

                if (json)
                {
                    console.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(
                        new UiScreenshotResult
                        {
                            FilePath = filePath,
                            Width = width,
                            Height = height,
                            ProcessId = surface.ProcessId,
                            WindowTitle = null,
                            Hwnd = surface.WindowHandle,
                            ExecutionTarget = ExecutionTargetScope.For(reference, target.Epoch),
                        },
                        UiJsonContext.Default.UiScreenshotResult));
                }
                else
                {
                    logger.LogInformation(
                        "Screenshot of the {Selector} desktop saved to {Path} ({Width}x{Height}, {Size}KB)",
                        reference.Selector, filePath, width, height, png.Length / 1024);
                }

                return 0;
            }
            catch (ExecutionTargetException ex)
            {
                return TargetOutput.Fail(console, json, ex.Error);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return TargetOutput.Fail(console, json, ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.ArtifactFailed,
                    $"The {reference.Selector} desktop could not be captured: {ex.Message}",
                    userAction:
                        "Check that the target's window is still open, then retry. " +
                        $"'winapp target snapshot {reference.Selector}' reports whether it is.",
                    example: $"winapp target screenshot {reference.Selector} -o .\\desktop.png").Error);
            }
        }
    }
}

/// <summary>Records the rendered target desktop through the shared host recording pipeline.</summary>
internal class TargetRecordCommand : Command, IShortDescription
{
    /// <inheritdoc/>
    public string ShortDescription => "Record an execution target's whole desktop to an MP4";

    /// <summary>Which target to record.</summary>
    public static Argument<string> SelectorArgument { get; } = TargetVerb.NewSelectorArgument();

    /// <summary>Creates the command.</summary>
    public TargetRecordCommand()
        : base(
            "record",
            "Record an execution target's entire desktop to an H.264 MP4 on this machine. " +
            "Records the whole rendered guest desktop, so no application or window has to be named. " +
            "Prefer --duration-sec: without it the recording runs until Ctrl+C or a newline on redirected stdin.")
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

    /// <summary>Releases the guest channel before host-side recording starts.</summary>
    public class Handler(
        ExecutionTargetOrchestrator orchestrator,
        IUiTargetResolver targetResolver,
        IUiRecordingService recordingService,
        IWindowCapture windowCapture,
        ISystemUiQuery systemQuery,
        IAnsiConsole ansiConsole,
        IInteractiveDesktopLock desktopLock,
        ILogger<UiRecordCommand> logger)
        : UiRecordCommand.Handler(
            targetResolver,
            recordingService,
            windowCapture,
            systemQuery,
            ansiConsole,
            desktopLock,
            logger)
    {
        private UiTarget? _subject;
        private ExecutionTargetScope? _scope;
        private ExecutionTargetRef? _reference;
        private string _selector = ExecutionTargetRef.SandboxKind;

        /// <inheritdoc/>
        protected override int? Preflight(ParseResult parseResult)
        {
            ArgumentNullException.ThrowIfNull(parseResult);

            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var selector = parseResult.GetValue(SelectorArgument);

            if (base.Preflight(parseResult) is { } baseFailure)
            {
                return baseFailure;
            }

            try
            {
                _reference = TargetVerb.Resolve(orchestrator, selector);
                _selector = _reference.Selector;
                return null;
            }
            catch (ExecutionTargetException ex)
            {
                return TargetOutput.RejectSelection(Output, json, ex.Error);
            }
        }

        protected override int ReportOptionError(ParseResult parseResult, UiRecordOptionError error) =>
            TargetOutput.RejectOptions(
                Output, parseResult.GetValue(WinAppRootCommand.JsonOption),
                ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.TargetInvalidArguments, error.Message,
                    userAction: error.RecoveryHint ?? "Correct the option and run the command again.",
                    example: "winapp target record sandbox -o .\\desktop.mp4 --duration-sec 10").Error);

        /// <inheritdoc/>
        protected override async Task<int> ExecuteAsync(
            ParseResult parseResult,
            IUiTurn turn,
            CancellationToken cancellationToken)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var reference = _reference ?? throw new InvalidOperationException(
                "The execution target is selected during preflight.");

            try
            {
                TargetDesktopSurface surface;
                ExecutionTargetEpoch epoch;

                // Scoped tightly on purpose: everything the recording needs from the target is known
                // by the end of this block, and the channel is a resource the guest agent rations.
                await using (var target = await orchestrator
                    .PrepareAsync(PrepareTargetOptions.Interactive, cancellationToken)
                    .ConfigureAwait(false))
                {
                    surface = orchestrator.ResolveDesktopSurface(TargetDesktopUse.PixelCapture);
                    epoch = target.Epoch;
                }

                _subject = new UiTarget
                {
                    WindowHandle = surface.WindowHandle,
                    ProcessId = surface.ProcessId,
                    ProcessName = surface.ProcessName,
                    IsExplicitWindow = true,
                };
                _scope = ExecutionTargetScope.For(reference, epoch);

                return await base.ExecuteAsync(parseResult, turn, cancellationToken).ConfigureAwait(false);
            }
            catch (ExecutionTargetException ex)
            {
                return TargetOutput.Fail(Output, json, ex.Error);
            }
        }

        /// <inheritdoc/>
        protected override Task<UiTarget> ResolveSubjectAsync(
            ParseResult parseResult,
            CancellationToken cancellationToken) =>
            Task.FromResult(_subject ?? throw new InvalidOperationException(
                "The target's desktop window is resolved before the recording starts."));

        /// <inheritdoc/>
        /// <remarks>The whole desktop is the subject, so there is nothing to select.</remarks>
        protected override bool TrySelectSubject(ParseResult parseResult, bool json) => true;

        /// <inheritdoc/>
        /// <remarks>No element to crop to: this verb records the desktop, not a control in it.</remarks>
        protected override string? ElementSelector(ParseResult parseResult) => null;

        /// <inheritdoc/>
        /// <remarks>Host screen coordinates would capture the user's desktop, not the parked client.</remarks>
        protected override bool CaptureScreen(ParseResult parseResult) => false;

        /// <inheritdoc/>
        /// <remarks>An uncapturable client must fail rather than take focus from the user.</remarks>
        protected override bool NoActivation(ParseResult parseResult) => true;

        /// <inheritdoc/>
        protected override string DescribeSubject(UiTarget uiTarget) => $"the {_selector} desktop";

        /// <inheritdoc/>
        protected override ExecutionTargetScope? Scope => _scope;
    }
}
