// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>Verifies guest capture artifacts before publishing a complete host-side take.</summary>
internal sealed class TargetArtifactService
{
    private const long MaximumVideoBytes = 512L * 1024 * 1024 * 1024;
    private const int MaximumArtifactFiles = 1_000_000;

    public static GuestPathScope ScopeFor(Guid operationId) =>
        new(GuestRootNames.Artifacts, operationId.ToString("n", CultureInfo.InvariantCulture));

    public static void ValidateDestination(RoutedArtifact artifact)
    {
        try
        {
            if (artifact.IsRecording)
            {
                RecordingArtifactPublisher.ValidateDestination(
                    artifact.HostDestination, artifact.HostFramesDirectory, artifact.Overwrite);
            }
            else if (Directory.Exists(artifact.HostDestination))
            {
                throw new IOException($"Output is an existing directory: {artifact.HostDestination}");
            }
        }
        catch (IOException ex)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.ArtifactFailed, ex.Message,
                userAction: "Choose a new --output file path, or use --overwrite for an existing recording.",
                innerException: ex);
        }
    }

    /// <summary>Returns the recovery directory for a failed capture, or null for normal publication.</summary>
    public static async Task<string?> PublishAsync(
        ITargetOperationExecutor channel,
        GuestPathScope scope,
        RoutedArtifact artifact,
        CancellationToken cancellationToken,
        bool partial = false)
    {
        var guestRoot = Path.GetDirectoryName(artifact.GuestFullPath)!;
        IReadOnlyList<GuestFileInfo> staged;
        try
        {
            staged = await channel.ListFilesAsync(scope, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ExecutionTargetException or IOException or OperationCanceledException)
        {
            throw RecoveryFailure(artifact, guestRoot, null, "The capture inventory could not be retrieved.", ex);
        }
        if (partial && staged.Count == 0)
        {
            return null;
        }

        var framesRelative = Path.GetFileName(artifact.GuestFramesDirectory);
        var files = staged.Where(file => partial ||
            file.RelativePath.Equals(artifact.GuestRelativePath, StringComparison.OrdinalIgnoreCase) ||
            (artifact.Frames && IsBelow(file.RelativePath, framesRelative))).ToArray();
        var video = files.FirstOrDefault(file =>
            file.RelativePath.Equals(artifact.GuestRelativePath, StringComparison.OrdinalIgnoreCase));
        if (!partial && (video is null || files.Length != staged.Count ||
            (artifact.Frames && (!HasFile(files, Path.Join(framesRelative, "manifest.json")) ||
                                !HasFile(files, Path.Join(framesRelative, "frames.ndjson"))))))
        {
            throw RecoveryFailure(artifact, guestRoot, null,
                "The guest did not produce the complete declared capture.", null);
        }

        var recovery = $"{artifact.HostDestination}.{(partial ? "partial" : "recovery")}-{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(recovery);
            ValidateInventory(files, recovery, artifact, partial);
            foreach (var file in files)
            {
                var destination = ResolveRelative(recovery, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using (var stream = new BoundedArtifactStream(destination, file.Size))
                {
                    await channel.GetFileAsync(scope, file.RelativePath, stream, cancellationToken).ConfigureAwait(false);
                }
                await VerifyAsync(destination, file, artifact, cancellationToken).ConfigureAwait(false);
            }

            // Manifests and diagnostics must refer to delivered host artifacts, including partial takes.
            foreach (var file in files.Where(file => Path.GetFileName(file.RelativePath) == "manifest.json"))
            {
                var manifestPath = ResolveRelative(recovery, file.RelativePath);
                using var document = JsonDocument.Parse(
                    await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false));
                var guestVideo = document.RootElement.GetProperty("video").GetProperty("path").GetString()!;
                var hostVideo = partial
                    ? ResolveRelative(recovery, Path.GetRelativePath(guestRoot, guestVideo))
                    : artifact.HostDestination;
                await ValidateFrameIndexAsync(Path.GetDirectoryName(manifestPath)!, cancellationToken).ConfigureAwait(false);
                await RecordingArtifactPublisher.RewriteManifestVideoAsync(
                    Path.GetDirectoryName(manifestPath)!, hostVideo, cancellationToken).ConfigureAwait(false);
            }

            if (partial)
            {
                return recovery;
            }

            var receivedVideo = ResolveRelative(recovery, artifact.GuestRelativePath);
            if (artifact.IsRecording)
            {
                _ = RecordingArtifactPublisher.Publish(
                    receivedVideo, artifact.HostDestination,
                    artifact.Frames ? ResolveRelative(recovery, framesRelative) : null,
                    artifact.HostFramesDirectory, artifact.Overwrite);
            }
            else
            {
                File.Move(receivedVideo, artifact.HostDestination, overwrite: artifact.Overwrite);
            }
            Directory.Delete(recovery);
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The verified prefix and the guest originals remain available after a transfer failure.
            var receivedFrames = ResolveRelative(recovery, framesRelative);
            if (!partial && File.Exists(Path.Join(receivedFrames, "manifest.json")))
            {
                try
                {
                    using var repair = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await RecordingArtifactPublisher.RewriteManifestVideoAsync(
                        receivedFrames, ResolveRelative(recovery, artifact.GuestRelativePath), repair.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception repairError) when (repairError is IOException or JsonException or OperationCanceledException)
                {
                    System.Diagnostics.Trace.TraceWarning("Could not update recovery manifest: {0}", repairError.Message);
                }
            }
            throw RecoveryFailure(artifact, guestRoot, Directory.Exists(recovery) ? recovery : null,
                $"The capture could not be published to '{artifact.HostDestination}'.", ex);
        }
    }

    private static async Task ValidateFrameIndexAsync(string framesDirectory, CancellationToken cancellationToken)
    {
        using var reader = File.OpenText(Path.Join(framesDirectory, "frames.ndjson"));
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            var entry = JsonSerializer.Deserialize(line, UiJsonContext.Default.RecordFrameIndexEntry)
                ?? throw new IOException("The frame index contains an invalid entry.");
            if (!File.Exists(ResolveRelative(framesDirectory, entry.File)))
            {
                throw new IOException($"The frame bundle is missing indexed image '{entry.File}'.");
            }
        }
    }

    private static void ValidateInventory(GuestFileInfo[] files, string root, RoutedArtifact artifact, bool partial)
    {
        if (files.Length > MaximumArtifactFiles)
        {
            throw new IOException("The capture contains too many artifact files.");
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long frameBytes = 0;
        long videoBytes = 0;
        foreach (var file in files)
        {
            if (!names.Add(ResolveRelative(root, file.RelativePath)) || file.Size < 0)
            {
                throw new IOException("The capture inventory contains a duplicate or invalid file.");
            }
            if (file.RelativePath.Equals(artifact.GuestRelativePath, StringComparison.OrdinalIgnoreCase) ||
                (partial && Path.GetFileName(file.RelativePath).Equals(artifact.GuestRelativePath, StringComparison.OrdinalIgnoreCase)))
            {
                videoBytes = checked(videoBytes + file.Size);
                if (videoBytes > MaximumVideoBytes)
                {
                    throw new IOException("The recording exceeds the transfer size limit.");
                }
            }
            else
            {
                frameBytes = checked(frameBytes + file.Size);
                if (frameBytes > RecordingArtifactPublisher.DefaultMaximumFrameBundleBytes)
                {
                    throw new IOException("The frame bundle exceeds its 1 GiB byte limit.");
                }
                if (Path.GetFileName(file.RelativePath) == "manifest.json" && file.Size > 1024 * 1024)
                {
                    throw new IOException("The frame manifest exceeds its 1 MiB size limit.");
                }
            }
        }
    }

    private static bool HasFile(IEnumerable<GuestFileInfo> files, string name) =>
        files.Any(file => file.RelativePath.Replace('/', '\\').Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool IsBelow(string path, string directory) =>
        path.Replace('/', '\\').StartsWith(directory + "\\", StringComparison.OrdinalIgnoreCase);

    private static string ResolveRelative(string root, string relative) =>
        TargetPathSafety.CombineInsideRoot(root, relative.Split(['\\', '/']));

    internal static string RecoveryAction(string guestRoot) =>
        $"Keep Sandbox running. Use 'winapp target exec' to copy '{guestRoot}' into the guest work folder, then use 'winapp target pull' with the copied path relative to that folder.";

    private static ExecutionTargetException RecoveryFailure(
        RoutedArtifact artifact, string guestRoot, string? hostRecovery, string message, Exception? inner) =>
        ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.ArtifactFailed, message,
            userAction: hostRecovery is null
                ? $"Guest evidence was retained. {RecoveryAction(guestRoot)}"
                : $"Received evidence is retained at '{hostRecovery}'. {RecoveryAction(guestRoot)}",
            context: new Dictionary<string, string>
            {
                ["artifact"] = artifact.HostDestination,
                ["guestRecoveryPath"] = guestRoot,
                ["hostRecoveryPath"] = hostRecovery ?? "",
            },
            innerException: inner);

    /// <summary>Only called after every guest artifact has been delivered.</summary>
    public static async Task TryRemoveAsync(
        ITargetOperationExecutor channel, GuestPathScope scope, CancellationToken cancellationToken)
    {
        try
        {
            await channel.DeleteScopeAsync(scope, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ExecutionTargetException or OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not remove guest artifact staging '{0}': {1}", scope.Scope, ex.Message);
        }
    }

    internal static async Task VerifyAsync(
        string path, GuestFileInfo declared, RoutedArtifact artifact, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length != declared.Size)
        {
            throw Incomplete(artifact, declared, info.Length, "size");
        }
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(hash, declared.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw Incomplete(artifact, declared, info.Length, "hash");
        }
    }

    private static ExecutionTargetException Incomplete(RoutedArtifact artifact, GuestFileInfo declared, long received, string phase) =>
        ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.TransferInterrupted,
            $"The capture for '{artifact.HostDestination}' did not arrive intact.",
            userAction: "Keep Sandbox running and recover the retained guest artifacts.",
            context: new Dictionary<string, string>
            {
                ["artifact"] = artifact.HostDestination,
                ["expectedBytes"] = declared.Size.ToString(CultureInfo.InvariantCulture),
                ["receivedBytes"] = received.ToString(CultureInfo.InvariantCulture),
                ["phase"] = phase,
            });

    private sealed class BoundedArtifactStream(string path, long maximumBytes)
        : FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true)
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            CheckSize(buffer.Length);
            base.Write(buffer);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            CheckSize(count);
            base.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            CheckSize(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            CheckSize(count);
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        private void CheckSize(int count)
        {
            if (count > maximumBytes - Position)
            {
                throw new IOException("The artifact exceeded its declared transfer size.");
            }
        }
    }
}
