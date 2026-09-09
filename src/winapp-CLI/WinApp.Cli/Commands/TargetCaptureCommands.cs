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

/// <summary>
/// Captures a target's whole rendered desktop, without naming anything inside it.
/// </summary>
/// <remarks>
/// The difference from <c>winapp ui screenshot --on &lt;target&gt;</c> is what is captured, not where
/// the file lands: that routes into the guest and captures one application's window, while this
/// captures the guest desktop exactly as it is being drawn, including the shell, dialogs owned by no
/// app winapp deployed, and anything an application put on screen before it could be named. That is
/// the picture worth having when a command failed and nobody knows why.
/// <para>
/// The client window is captured where it is, which for a managed Sandbox is parked off-screen. No
/// window is activated and no focus is taken — and that is a hard rule, not an intention: if the
/// window cannot be captured where it sits, the command fails and says so rather than bringing it to
/// the front to get a picture. Running this while something else on the desktop is being used never
/// interrupts it.
/// </para>
/// </remarks>
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
                // Interactive, because a desktop nobody is attached to renders nothing worth
                // capturing. Preparing this way reconnects a closed client first, so the picture is
                // of a live desktop rather than of whatever was last left on screen.
                await using var target = await orchestrator
                    .PrepareAsync(PrepareTargetOptions.Interactive, cancellationToken)
                    .ConfigureAwait(false);

                var surface = orchestrator.ResolveDesktopSurface(TargetDesktopUse.PixelCapture);

                // Strictly no activation. The ordinary screenshot path recovers from a blank frame by
                // foregrounding the window and trying again, which for a parked Sandbox client means
                // yanking it onto the user's screen -- the exact thing this command promises not to
                // do. Here a blank frame ends the command instead.
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

                // Published by rename, so the destination is only ever absent or a whole PNG. A
                // direct write would truncate an existing screenshot the moment capture started and
                // leave it truncated if the write failed or was cancelled -- destroying the last
                // good picture of a target in the middle of diagnosing why the target went wrong.
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

/// <summary>
/// Records a target's whole rendered desktop to an MP4 on this machine.
/// </summary>
/// <remarks>
/// The recording half of <see cref="TargetScreenshotCommand"/>, and the same distinction applies: it
/// captures the guest desktop rather than one application's window, so a launch that fails before
/// any window exists is still on the video.
/// <para>
/// Everything about how the recording is made and reported — cadence, downscaling, frame artifacts,
/// stop conditions, partial-output handling, and the JSON contract — is <c>winapp ui record</c>'s,
/// reused rather than reimplemented. Only what is being recorded differs.
/// </para>
/// </remarks>
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
        Options.Add(WinAppRootCommand.JsonOption);
    }

    /// <summary>
    /// Records the target's desktop window through the ordinary recording pipeline.
    /// </summary>
    /// <remarks>
    /// The guest channel is opened only long enough to validate the target and resolve which window
    /// on this machine is its desktop, then released before a single frame is captured. Recording is
    /// entirely host-side — Windows Graphics Capture reads the client window, not the guest — so
    /// holding a channel for the length of a take would occupy one of the few the agent allows for
    /// hours, and block deployments and other commands the whole time, in exchange for nothing.
    /// <para>
    /// The window can therefore close mid-take. That is reported the way the recording pipeline
    /// already reports it: the take ends early, the frames captured so far are published, and the
    /// result says the target closed.
    /// </para>
    /// </remarks>
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

            // Preserve the target-command error contract while still letting the coordinated base
            // perform the same validation before it creates a desktop participant.
            if (UiRecordOptionValidator.Validate(parseResult, out _) is { } optionError)
            {
                return TargetOutput.RejectOptions(
                    Output,
                    json,
                    ExecutionTargetException.Create(
                        ExecutionTargetErrorCodes.TargetInvalidArguments,
                        optionError.Message,
                        userAction: optionError.RecoveryHint ??
                            "Correct the option and run the command again.",
                        example:
                            $"winapp target record {selector} -o .\\desktop.mp4 --duration-sec 10").Error);
            }

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
        /// <remarks>
        /// Never. Screen capture reads host screen coordinates, and a managed client window is
        /// parked off-screen precisely so it is not on them — capturing that region would record the
        /// user's desktop instead of the target's.
        /// </remarks>
        protected override bool CaptureScreen(ParseResult parseResult) => false;

        /// <inheritdoc/>
        /// <remarks>
        /// Always. This verb records a machine the user is not looking at, from a host window they
        /// did not open and may not know exists, so restoring or foregrounding it would interrupt
        /// whatever they are actually doing. A window that can only be recorded by activating it is
        /// reported as uncapturable instead.
        /// </remarks>
        protected override bool NoActivation(ParseResult parseResult) => true;

        /// <inheritdoc/>
        protected override string DescribeSubject(UiTarget uiTarget) => $"the {_selector} desktop";

        /// <inheritdoc/>
        protected override ExecutionTargetScope? Scope => _scope;
    }
}
