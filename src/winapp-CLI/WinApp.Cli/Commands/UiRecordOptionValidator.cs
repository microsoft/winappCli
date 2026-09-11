// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Commands;

/// <summary>Why a set of recording options was rejected.</summary>
/// <param name="Code">Machine-readable reason, one of <see cref="UiJsonError"/>'s codes.</param>
/// <param name="Message">What is wrong, in the caller's terms.</param>
/// <param name="RecoveryHint">What to do about it, when there is something specific to say.</param>
internal sealed record UiRecordOptionError(string Code, string Message, string? RecoveryHint = null);

/// <summary>Recording options after validation, with every derived default already applied.</summary>
/// <param name="FilePath">Absolute path the MP4 will be published to.</param>
/// <param name="FramesDirectory">Where frame artifacts go, or null when they were not requested.</param>
/// <param name="MaxEdge">Longest output edge, after the frame-artifact default is applied.</param>
/// <param name="DurationSec">Requested length, or 0 for "until stopped".</param>
/// <param name="Fps">Requested cadence.</param>
internal sealed record UiRecordResolvedOptions(
    string FilePath,
    string? FramesDirectory,
    int MaxEdge,
    int DurationSec,
    int Fps);

/// <summary>Validates recording options before taking a desktop turn or preparing a target.</summary>
internal static class UiRecordOptionValidator
{
    /// <summary>Longest recording, in seconds. Beyond a day the request is a mistake.</summary>
    internal const int MaximumDurationSec = 86400;

    /// <summary>Longest output edge when frame artifacts are requested and none is given.</summary>
    internal const int DefaultFrameArtifactMaxEdge = 1280;

    /// <summary>Largest output edge frame artifacts accept.</summary>
    internal const int MaximumFrameArtifactMaxEdge = 4096;

    /// <summary>Smallest output edge the H.264 encoder accepts.</summary>
    internal const int MinimumMaxEdge = 64;

    /// <summary>
    /// Validates a parsed recording request.
    /// </summary>
    /// <param name="parseResult">The parsed command line.</param>
    /// <param name="resolved">The validated options, or null when validation failed.</param>
    /// <returns>The first problem found, or null when the request is usable.</returns>
    public static UiRecordOptionError? Validate(
        ParseResult parseResult,
        out UiRecordResolvedOptions? resolved)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        resolved = null;

        var durationSec = parseResult.GetValue(SharedUiOptions.DurationSecOption);
        var fps = parseResult.GetValue(SharedUiOptions.FpsOption);
        var maxEdge = parseResult.GetValue(SharedUiOptions.MaxEdgeOption);
        var maxEdgeExplicit = parseResult.GetResult(SharedUiOptions.MaxEdgeOption)?.Implicit == false;
        var output = parseResult.GetValue(SharedUiOptions.OutputOption);
        var frames = parseResult.GetValue(UiRecordCommand.FramesOption);
        var overwrite = parseResult.GetValue(UiRecordCommand.OverwriteOption);

        if (durationSec < 0)
        {
            return Invalid("--duration-sec must be 0 or greater.");
        }

        if (durationSec > MaximumDurationSec)
        {
            return Invalid("--duration-sec must not exceed 86400 (24 hours).");
        }

        if (fps < 1)
        {
            return Invalid("--fps must be at least 1.");
        }

        if (!frames && maxEdge != 0 && maxEdge < MinimumMaxEdge)
        {
            return Invalid("--max-edge must be 0 (unbounded) or >= 64 (encoder minimum).");
        }

        if (frames)
        {
            if (fps > 30)
            {
                return Invalid("--frames supports --fps values from 1 through 30.");
            }

            if (maxEdgeExplicit && (maxEdge < MinimumMaxEdge || maxEdge > MaximumFrameArtifactMaxEdge))
            {
                return Invalid(
                    "--frames supports --max-edge values from 64 through 4096; omit --max-edge to use 1280.");
            }

            if (!maxEdgeExplicit)
            {
                maxEdge = DefaultFrameArtifactMaxEdge;
            }
        }

        string filePath;
        string? framesDirectory;

        try
        {
            if (output is not null && Path.EndsInDirectorySeparator(output))
            {
                return Invalid($"Invalid output path: '{output}' names a directory, not a file.");
            }
            filePath = Path.GetFullPath(output ?? DefaultOutputPath());
            framesDirectory = frames ? UiRecordCommand.Handler.GetFramesDirectory(filePath) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Invalid($"Invalid output path: {ex.Message}");
        }

        if (Directory.Exists(filePath))
        {
            return Invalid($"Invalid output path: '{filePath}' is an existing directory.");
        }

        var pairedDirectory = UiRecordCommand.Handler.GetFramesDirectory(filePath);
        if (File.Exists(pairedDirectory))
        {
            return Invalid($"Frame artifact output is an existing file: {pairedDirectory}");
        }
        if (!overwrite && (Path.Exists(filePath) || Path.Exists(pairedDirectory)))
        {
            return new UiRecordOptionError(
                UiJsonError.CodeOutputExists,
                $"Recording output already exists: {(Path.Exists(filePath) ? filePath : pairedDirectory)}",
                "Choose a new --output path or pass --overwrite to replace the recording after it finishes.");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Invalid($"Invalid output path: {ex.Message}");
        }

        resolved = new UiRecordResolvedOptions(filePath, framesDirectory, maxEdge, durationSec, fps);
        return null;
    }

    private static UiRecordOptionError Invalid(string message) =>
        new(UiJsonError.CodeInvalidArguments, message);

    internal static string DefaultOutputPath() =>
        $"recording-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.mp4";
}
