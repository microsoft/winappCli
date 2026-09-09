// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Security.Cryptography;
using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>
/// Brings a file a guest command produced back to the host path the caller asked for
/// (spec §"Artifact handling").
/// </summary>
/// <remarks>
/// The requested destination is written only after the whole file has arrived and its size and hash
/// match what the guest reported. An interrupted transfer therefore never appears as a shorter but
/// plausible result — the caller sees a failure naming the artifact, its expected size, and how much
/// arrived, and whatever was already at the destination is untouched.
/// <para>
/// v1 restarts a transfer from the beginning rather than resuming. Resumption would need the guest
/// to keep an artifact alive across connections, which is a larger promise than a screenshot or a
/// recording needs.
/// </para>
/// </remarks>
internal sealed class TargetArtifactService
{
    /// <summary>The guest staging folder for one operation's outputs.</summary>
    public static GuestPathScope ScopeFor(Guid operationId) =>
        new(GuestRootNames.Artifacts, operationId.ToString("n", CultureInfo.InvariantCulture));

    /// <summary>
    /// Fetches <paramref name="artifact"/> from guest staging and publishes it atomically.
    /// </summary>
    /// <exception cref="ExecutionTargetException">
    /// The guest produced nothing, produced something that failed verification, or the transfer was
    /// interrupted. Nothing is published in any of those cases.
    /// </exception>
    public static async Task PublishAsync(
        ITargetOperationExecutor channel,
        GuestPathScope scope,
        RoutedArtifact artifact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(artifact);

        var staged = await channel.ListFilesAsync(scope, cancellationToken).ConfigureAwait(false);

        var declared = staged.FirstOrDefault(file =>
            string.Equals(file.RelativePath, artifact.GuestRelativePath, StringComparison.OrdinalIgnoreCase))
            ?? throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.ArtifactFailed,
                $"The command reported success but produced no '{artifact.GuestRelativePath}' in Windows Sandbox.",
                userAction: "Retry the command.",
                context: new Dictionary<string, string> { ["artifact"] = artifact.GuestRelativePath });

        var directory = Path.GetDirectoryName(artifact.HostDestination);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // A sibling of the destination, so the publish below is a rename within one volume and
        // cannot leave a half-written file where a complete one is expected.
        var temporary = $"{artifact.HostDestination}.{Guid.NewGuid():n}.part";
        var received = 0L;

        try
        {
            await using (var stream = new FileStream(
                temporary, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                await channel.GetFileAsync(scope, artifact.GuestRelativePath, stream, cancellationToken)
                    .ConfigureAwait(false);

                received = stream.Length;
            }

            await VerifyAsync(temporary, declared, artifact, cancellationToken).ConfigureAwait(false);

            File.Move(temporary, artifact.HostDestination, overwrite: true);
        }
        catch (Exception ex)
        {
            TryDelete(temporary);

            if (ex is ExecutionTargetException or OperationCanceledException)
            {
                throw;
            }

            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TransferInterrupted,
                $"'{artifact.GuestRelativePath}' could not be copied out of Windows Sandbox.",
                userAction: "Retry the command.",
                context: new Dictionary<string, string>
                {
                    ["artifact"] = artifact.GuestRelativePath,
                    ["expectedBytes"] = declared.Size.ToString(CultureInfo.InvariantCulture),
                    ["receivedBytes"] = received.ToString(CultureInfo.InvariantCulture),
                    ["phase"] = "transfer",
                },
                innerException: ex);
        }
    }

    /// <summary>Discards an operation's guest staging, best effort.</summary>
    /// <remarks>
    /// A staging folder left behind wastes guest disk until the Sandbox is stopped; failing a
    /// command that already produced its result over that would be a worse trade.
    /// </remarks>
    public static async Task TryRemoveAsync(
        ITargetOperationExecutor channel,
        GuestPathScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        try
        {
            await channel.DeleteScopeAsync(scope, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ExecutionTargetException or OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Could not remove guest artifact staging '{0}': {1}", scope.Scope, ex.Message);
        }
    }

    /// <summary>
    /// Proves a received file is exactly what the guest declared, before anything is published.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so the guarantee can be verified directly: content changing
    /// between the guest's hash and the host's read is not something a test can schedule from the
    /// outside, and a guarantee that is only asserted through a happy path is not asserted at all.
    /// </remarks>
    internal static async Task VerifyAsync(
        string path,
        GuestFileInfo declared,
        RoutedArtifact artifact,
        CancellationToken cancellationToken)    {
        var info = new FileInfo(path);

        if (info.Length != declared.Size)
        {
            throw Incomplete(artifact, declared, info.Length, "size");
        }

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);

        var hash = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();

        if (!string.Equals(hash, declared.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw Incomplete(artifact, declared, info.Length, "hash");
        }
    }

    private static ExecutionTargetException Incomplete(
        RoutedArtifact artifact,
        GuestFileInfo declared,
        long received,
        string phase) =>
        ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.TransferInterrupted,
            $"'{artifact.GuestRelativePath}' did not arrive intact from Windows Sandbox.",
            userAction: "Retry the command.",
            context: new Dictionary<string, string>
            {
                ["artifact"] = artifact.GuestRelativePath,
                ["expectedBytes"] = declared.Size.ToString(CultureInfo.InvariantCulture),
                ["receivedBytes"] = received.ToString(CultureInfo.InvariantCulture),
                ["phase"] = phase,
            });

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup failure must never turn an interrupted transfer into a success, and the
            // caller is already failing.
            System.Diagnostics.Trace.TraceWarning("Could not remove '{0}': {1}", path, ex.Message);
        }
    }
}
