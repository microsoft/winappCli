// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;
using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Commands;

/// <summary>Internal guest-native desktop capture endpoint, invoked over the guest command channel.</summary>
internal sealed class GuestDesktopCaptureCommand : Command, IShortDescription
{
    public string ShortDescription => "Internal guest desktop capture";
    internal const string Verb = "__guest-desktop";
    internal static readonly Option<string> TargetKind = new("--target-kind") { Required = true };
    internal static readonly Option<string> TargetName = new("--target-name") { Required = true };
    internal static readonly Option<string> TargetEpoch = new("--target-epoch") { Required = true };

    internal static void AddScopeOptions(Command command)
    {
        command.Options.Add(TargetKind);
        command.Options.Add(TargetName);
        command.Options.Add(TargetEpoch);
    }

    internal static ExecutionTargetScope ReadScope(ParseResult parseResult) => new()
    {
        Kind = parseResult.GetValue(TargetKind)!,
        Id = parseResult.GetValue(TargetName)!,
        Epoch = parseResult.GetValue(TargetEpoch),
    };

    public GuestDesktopCaptureCommand(GuestDesktopScreenshotCommand screenshot, GuestDesktopRecordCommand record)
        : base(Verb, "Internal execution-target desktop capture.")
    {
        Hidden = true;
        Subcommands.Add(screenshot);
        Subcommands.Add(record);
    }
}

internal sealed class GuestDesktopScreenshotCommand : Command, IShortDescription
{
    public string ShortDescription => "Internal native desktop screenshot";
    public GuestDesktopScreenshotCommand() : base("screenshot", "Capture the guest's native desktop pixels.")
    {
        Hidden = true;
        GuestDesktopCaptureCommand.AddScopeOptions(this);
        Options.Add(SharedUiOptions.OutputOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public sealed class Handler(
        IWindowCapture capture,
        IAnsiConsole console,
        IInteractiveDesktopLock desktopLock,
        ILogger<GuestDesktopScreenshotCommand> logger) : UiCoordinatedAction(desktopLock, logger)
    {
        protected override string Operation => "target screenshot";
        protected override UiTurnMode ResolveMode(ParseResult parseResult) => UiTurnMode.Observe;
        protected override int? Preflight(ParseResult parseResult) => null;

        protected override async Task<int> ExecuteAsync(ParseResult parseResult, IUiTurn turn, CancellationToken cancellationToken)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bounds = capture.GetDesktopBounds();
                var width = checked(bounds.Right - bounds.Left);
                var height = checked(bounds.Bottom - bounds.Top);
                var pixels = capture.CaptureScreenPixels(
                    bounds.Left, bounds.Top, width, height, width, height, width, height);
                if (capture.GetDesktopBounds() != bounds)
                {
                    throw new InvalidOperationException("The desktop display bounds changed during the screenshot. Nothing was saved.");
                }
                if (pixels.Length != checked(width * height * 4))
                {
                    throw new InvalidOperationException("Desktop capture returned an incomplete pixel buffer.");
                }
                var filePath = Path.GetFullPath(parseResult.GetValue(SharedUiOptions.OutputOption) ?? "screenshot.png");
                var png = PngImage.Encode(pixels, width, height);
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                await AtomicFile.WriteAllBytesAsync(filePath, png, cancellationToken).ConfigureAwait(false);
                var coordinates = new CaptureCoordinates
                {
                    SourceBounds = bounds,
                    ContentRect = new PointerRect(0, 0, width, height),
                };
                if (json)
                {
                    console.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(new UiScreenshotResult
                    {
                        FilePath = filePath,
                        Width = width,
                        Height = height,
                        Coordinates = coordinates,
                        ExecutionTarget = GuestDesktopCaptureCommand.ReadScope(parseResult),
                    }, UiJsonContext.Default.UiScreenshotResult));
                }
                else
                {
                    logger.LogInformation("Guest desktop screenshot saved to {Path} ({Width}x{Height}); screen origin ({X},{Y})",
                        filePath, width, height, bounds.Left, bounds.Top);
                }
                return 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                UiJsonError.Emit(json, "capture_failed", ex.Message, errorOut: parseResult.InvocationConfiguration.Error);
                logger.LogError("{Message}", ex.Message);
                return 1;
            }
        }
    }
}

internal sealed class GuestDesktopRecordCommand : Command, IShortDescription
{
    public string ShortDescription => "Internal native desktop recording";
    public GuestDesktopRecordCommand() : base("record", "Record the guest's native desktop pixels.")
    {
        Hidden = true;
        GuestDesktopCaptureCommand.AddScopeOptions(this);
        Options.Add(SharedUiOptions.OutputOption);
        Options.Add(SharedUiOptions.DurationSecOption);
        Options.Add(SharedUiOptions.FpsOption);
        Options.Add(SharedUiOptions.MaxEdgeOption);
        Options.Add(UiRecordCommand.FramesOption);
        Options.Add(UiRecordCommand.OverwriteOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public sealed class Handler(
        IUiTargetResolver targetResolver, IUiRecordingService recordingService,
        IWindowCapture windowCapture, ISystemUiQuery systemQuery, IAnsiConsole console,
        IInteractiveDesktopLock desktopLock, ILogger<UiRecordCommand> logger)
        : UiRecordCommand.Handler(targetResolver, recordingService, windowCapture, systemQuery, console, desktopLock, logger)
    {
        private ExecutionTargetScope? _scope;
        protected override int? Preflight(ParseResult parseResult)
        {
            _scope = GuestDesktopCaptureCommand.ReadScope(parseResult);
            return base.Preflight(parseResult);
        }
        protected override ExecutionTargetScope? Scope => _scope;
        protected override string Operation => "target record";
        protected override bool IsDesktop => true;
        protected override bool TrySelectSubject(ParseResult parseResult, bool json) => true;
        protected override string? ElementSelector(ParseResult parseResult) => null;
        protected override bool CaptureScreen(ParseResult parseResult) => false;
    }
}
