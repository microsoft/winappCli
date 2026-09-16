// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording;

#if WINAPP_CLI_ARTIFACTS
using ArtifactJsonContext = WinApp.Cli.Helpers.UiJsonContext;
namespace WinApp.Cli.Helpers;
#else
using ArtifactJsonContext = Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording.RecordingJsonContext;
namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording;
#endif

/// <summary>Publishes a finalized recording and its optional frame bundle.</summary>
internal static class RecordingArtifactPublisher
{
    /// <summary>Default byte limit for a recording's frame bundle.</summary>
    public const long DefaultMaximumFrameBundleBytes = 1024L * 1024 * 1024;

    /// <summary>Gets the frame bundle directory paired with a video path.</summary>
    public static string GetFramesDirectory(string videoPath)
    {
        var frames = Path.ChangeExtension(videoPath, ".frames");
        return string.Equals(frames, videoPath, StringComparison.OrdinalIgnoreCase)
            ? videoPath + ".frames"
            : frames;
    }

    /// <summary>Rejects conflicting paths before a recording starts.</summary>
    public static void ValidateDestination(string videoPath, string? framesDirectory, bool overwrite)
    {
        if (framesDirectory is not null &&
            (videoPath.Equals(framesDirectory, StringComparison.OrdinalIgnoreCase) ||
             videoPath.StartsWith(Path.TrimEndingDirectorySeparator(framesDirectory) + Path.DirectorySeparatorChar,
                 StringComparison.OrdinalIgnoreCase)))
        {
            throw new IOException("The video output must be outside its frame bundle directory.");
        }
        if (Directory.Exists(videoPath) || (framesDirectory is not null && File.Exists(framesDirectory)))
        {
            throw new IOException($"The recording output has the wrong file type: {videoPath}");
        }

        if (!overwrite && (Path.Exists(videoPath) ||
            (framesDirectory is not null && Path.Exists(framesDirectory))))
        {
            throw new IOException($"Recording output already exists: {videoPath}. Choose another path or use --overwrite.");
        }
    }

    /// <summary>Publishes staged output, restoring the old bundle if video publication fails.</summary>
    /// <returns>The retained previous frame directory, if any.</returns>
    /// <remarks>The final rename also rejects files created after validation unless overwrite is requested.</remarks>
    public static string? Publish(
        string stagedVideo,
        string videoPath,
        string? stagedFrames,
        string? framesDirectory,
        bool overwrite)
    {
        using var publicationLock = new FileStream(
            videoPath + ".publish.lock", FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 1, FileOptions.DeleteOnClose);
        ValidateDestination(videoPath, framesDirectory, overwrite);

        string? previousFrames = null;
        var framesMoved = false;
        try
        {
            if (framesDirectory is not null && Directory.Exists(framesDirectory))
            {
                var backup = $"{framesDirectory}.previous-{Guid.NewGuid():N}";
                Directory.Move(framesDirectory, backup);
                previousFrames = backup;
            }

            if (stagedFrames is not null)
            {
                Directory.Move(stagedFrames, framesDirectory!);
                framesMoved = true;
            }

            if (overwrite)
            {
                PublishVideo(stagedVideo, videoPath);
            }
            else
            {
                File.Move(stagedVideo, videoPath, overwrite: false);
            }

            // Never recursively delete a caller-selected directory, even with --overwrite.
            return previousFrames;
        }
        catch
        {
            if (framesMoved)
            {
                Directory.Move(framesDirectory!, stagedFrames!);
            }
            if (previousFrames is not null)
            {
                Directory.Move(previousFrames, framesDirectory!);
            }
            throw;
        }
    }

    internal static void PublishVideo(string stagedVideo, string videoPath)
    {
        if (File.Exists(videoPath))
        {
            // Preserve an existing output's ACL rather than inheriting the directory default.
            File.Replace(stagedVideo, videoPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(stagedVideo, videoPath, overwrite: false);
        }
    }

    /// <summary>Updates a frame bundle's manifest to point at the relocated video.</summary>
    public static async Task RewriteManifestVideoAsync(
        string framesDirectory, string videoPath, CancellationToken cancellationToken)
    {
        var path = Path.Join(framesDirectory, "manifest.json");
        RecordFrameBundleManifest manifest;
        await using (var stream = File.OpenRead(path))
        {
            manifest = await JsonSerializer.DeserializeAsync(
                stream, ArtifactJsonContext.Default.RecordFrameBundleManifest, cancellationToken)
                .ConfigureAwait(false) ?? throw new IOException($"Invalid recording manifest: {path}");
        }
        manifest.Video = new RecordFrameVideoManifest
        {
            Path = videoPath,
            Status = manifest.Video.Status,
            Codec = manifest.Video.Codec,
            FrameCount = manifest.Video.FrameCount,
            FileSize = manifest.Video.FileSize,
        };
        var staged = $"{path}.{Guid.NewGuid():N}.part";
        try
        {
            await File.WriteAllTextAsync(staged,
                JsonSerializer.Serialize(manifest, ArtifactJsonContext.Default.RecordFrameBundleManifest),
                cancellationToken).ConfigureAwait(false);
            File.Move(staged, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(staged))
            {
                File.Delete(staged);
            }
        }
    }

    /// <summary>Returns frame result paths rooted at a bundle's new location.</summary>
    public static RecordFrameArtifactResult RelocateFrames(RecordFrameArtifactResult result, string directory) =>
        new()
        {
            Directory = directory,
            Manifest = Path.Join(directory, "manifest.json"),
            Index = Path.Join(directory, "frames.ndjson"),
            Format = result.Format,
            Quality = result.Quality,
            Samples = result.Samples,
            Images = result.Images,
            RepeatedSamples = result.RepeatedSamples,
            TotalBytes = result.TotalBytes,
            Truncated = result.Truncated,
            ByteLimit = result.ByteLimit,
        };
}
