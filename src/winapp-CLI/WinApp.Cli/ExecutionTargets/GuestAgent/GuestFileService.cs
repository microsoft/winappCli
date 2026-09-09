// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.ExecutionTargets.GuestAgent;

/// <summary>
/// Reconciles files between the host and managed guest-local roots
/// (spec §"Guest winapp agent mode", §"Exact in-place reconciliation").
/// </summary>
/// <remarks>
/// Every path crossing the boundary is resolved against a named managed root and proven to stay
/// inside it. That is what makes "guest-provided paths never directly select arbitrary host
/// destinations" hold in the other direction too: the host names a root and a relative path, and
/// nothing it sends can address a location the guest did not offer.
/// <para>
/// Writes land in a sibling temporary file and are verified before the destination is replaced, so
/// an interrupted transfer never leaves a half-written file that the next hash comparison would
/// accept as merely "changed".
/// </para>
/// </remarks>
internal sealed class GuestFileService(string managedRoot)
{
    /// <summary>The root every managed guest folder lives under.</summary>
    public string ManagedRoot { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(managedRoot));

    /// <summary>Resolves the directory a scope addresses, creating it when asked.</summary>
    /// <exception cref="ExecutionTargetException">The root name or scope is not a safe segment.</exception>
    public string ResolveScopeDirectory(GuestPathScope scope, bool create)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var directory = scope.Scope is null
            ? TargetPathSafety.CombineInsideRoot(ManagedRoot, RootFolder(scope.Root))
            : TargetPathSafety.CombineInsideRoot(ManagedRoot, RootFolder(scope.Root), scope.Scope);

        if (create)
        {
            Directory.CreateDirectory(directory);
        }

        return directory;
    }

    /// <summary>Enumerates the actual contents of a scope.</summary>
    /// <remarks>
    /// Hashes are computed here rather than trusting timestamps because a rerun must detect an edit
    /// that preserved both size and timestamp, which build tools produce more often than one would
    /// like. A scope that does not exist yet is empty, not an error: that is a first deployment.
    /// <para>
    /// Directories are walked manually rather than with <see cref="SearchOption.AllDirectories"/>
    /// so a reparse point can be refused instead of followed. Skipping only reparse <em>files</em>
    /// would still let a junction placed as a directory make the whole subtree beneath it appear to
    /// be inside the managed root when it is somewhere else entirely.
    /// </para>
    /// </remarks>
    public async Task<List<GuestFileInfo>> ListAsync(GuestPathScope scope, CancellationToken cancellationToken)
    {
        var directory = ResolveScopeDirectory(scope, create: false);
        var files = new List<GuestFileInfo>();

        if (!Directory.Exists(directory))
        {
            return files;
        }

        await CollectAsync(directory, directory, files, cancellationToken).ConfigureAwait(false);

        files.Sort((left, right) =>
            string.Compare(left.RelativePath, right.RelativePath, StringComparison.OrdinalIgnoreCase));

        return files;
    }

    /// <summary>Walks one directory level, refusing to descend through reparse points.</summary>
    private static async Task CollectAsync(
        string scopeRoot,
        string directory,
        List<GuestFileInfo> files,
        CancellationToken cancellationToken)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A reparse point -- file or directory -- is reported as absent rather than followed.
            // Reconciliation then deletes it, which is the repair: nothing inside a managed root
            // may redirect elsewhere.
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            if (entry is DirectoryInfo child)
            {
                await CollectAsync(scopeRoot, child.FullName, files, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var file = (FileInfo)entry;

            files.Add(new GuestFileInfo(
                file.FullName[(scopeRoot.Length + 1)..],
                file.Length,
                file.LastWriteTimeUtc.Ticks,
                await ComputeHashAsync(file.FullName, cancellationToken).ConfigureAwait(false)));
        }
    }

    /// <summary>Opens a temporary destination for an incoming file.</summary>
    /// <exception cref="ExecutionTargetException">The path escapes its managed root.</exception>
    public GuestFileWrite BeginWrite(GuestPathScope scope, GuestFileInfo file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var directory = ResolveScopeDirectory(scope, create: true);
        var destination = DeploymentPlanner.ResolveContainedPath(directory, file.RelativePath);

        // Lexical containment is not enough on its own: a junction planted as one of the
        // intermediate directories would satisfy it while pointing the write somewhere else. Each
        // existing ancestor is checked and any missing ones are created here. See
        // EnsureNoReparseAncestor for exactly what this does and does not guarantee.
        EnsureNoReparseAncestor(directory, destination);

        // The temporary sits beside the destination so the final move stays on one volume and is
        // therefore atomic.
        var temporary = $"{destination}.{Guid.NewGuid():n}.part";

        return new GuestFileWrite(destination, temporary, file);
    }

    /// <summary>
    /// Creates the directories leading to <paramref name="destination"/>, refusing any that is a
    /// reparse point.
    /// </summary>
    /// <remarks>
    /// Checked and created top-down, so on return every ancestor is a real directory this call
    /// either verified or made.
    /// <para>
    /// <strong>The guarantee this provides is bounded and worth stating precisely.</strong> It
    /// reliably refuses a link that <em>already exists</em> in the path — a junction left behind by
    /// an earlier deployment, an application, or an extracted archive, which is the realistic way
    /// one appears. It is <em>not</em> proof against a co-resident guest process replacing a
    /// verified directory with a junction between this check and the write: that is a
    /// time-of-check-to-time-of-use race, and closing it needs handle-relative, no-follow opens
    /// rather than path-based ones.
    /// </para>
    /// <para>
    /// That residual race is accepted for v1 and is consistent with the specification's threat
    /// model, which states plainly that everything inside the Sandbox runs as the same user and
    /// that co-resident applications are mutually trusted and "can observe or interfere with one
    /// another". A guest process able to win this race can already terminate the agent, rewrite the
    /// deployment directly, or interfere with the application — so the race is not the weakest link
    /// it would be under an isolation model that claimed otherwise. Mutual isolation requires
    /// separate machines or a future provider.
    /// </para>
    /// </remarks>
    internal static void EnsureNoReparseAncestor(string scopeRoot, string destination)
    {
        var relative = destination[(scopeRoot.Length + 1)..];
        var current = scopeRoot;

        foreach (var segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries).SkipLast(1))
        {
            current = Path.Join(current, segment);

            var info = new DirectoryInfo(current);

            if (info.Exists)
            {
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw ExecutionTargetException.Create(
                        ExecutionTargetErrorCodes.DeploymentDirty,
                        "A folder inside the managed guest location is a link, so writing through it was refused.",
                        userAction: "Retry the command to redeploy from scratch.",
                        context: new Dictionary<string, string> { ["segment"] = segment });
                }

                continue;
            }

            info.Create();
        }
    }

    /// <summary>Opens a managed file for streaming back to the host.</summary>
    /// <exception cref="ExecutionTargetException">The path escapes its root or does not exist.</exception>
    public FileStream OpenRead(GuestPathScope scope, string relativePath)
    {
        var directory = ResolveScopeDirectory(scope, create: false);
        var source = DeploymentPlanner.ResolveContainedPath(directory, relativePath);

        if (!File.Exists(source))
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.ArtifactFailed,
                $"'{relativePath}' does not exist in the guest.",
                userAction: "Check the path, then retry.",
                context: new Dictionary<string, string> { ["relativePath"] = relativePath });
        }

        return new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
    }

    /// <summary>Deletes managed files, then prunes the directories they leave empty.</summary>
    /// <remarks>
    /// Deleting files absent from the desired state is deliberate: leaving a stale binary behind is
    /// how a rerun silently keeps executing code the developer just removed.
    /// </remarks>
    public void Delete(GuestPathScope scope, IReadOnlyList<string> relativePaths)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);

        var directory = ResolveScopeDirectory(scope, create: false);
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var relativePath in relativePaths)
        {
            var path = DeploymentPlanner.ResolveContainedPath(directory, relativePath);

            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw LockedFileFailure(relativePath, ex);
            }
        }

        PruneEmptyDirectories(directory, directory);
    }

    /// <summary>Removes an entire scope, used by a clean reinstall.</summary>
    public void RemoveScope(GuestPathScope scope)
    {
        var directory = ResolveScopeDirectory(scope, create: false);

        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw LockedFileFailure(relativePath: null, ex);
        }
    }

    /// <summary>
    /// Whether an exception from a file operation is a sharing or lock violation — a running
    /// process still has the file open — rather than some other I/O failure.
    /// </summary>
    internal static bool IsSharingViolation(Exception ex) =>
        ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021);

    /// <summary>
    /// Builds a specific, actionable failure for a delete that could not complete, naming the
    /// offending path when one is known rather than surfacing the raw OS message.
    /// </summary>
    private static ExecutionTargetException LockedFileFailure(string? relativePath, Exception ex)
    {
        var subject = relativePath is null
            ? "The registration layout"
            : $"'{relativePath}'";

        return ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.TransferInterrupted,
            IsSharingViolation(ex)
                ? $"{subject} could not be removed from Windows Sandbox because a running process still has it open."
                : $"{subject} could not be removed from Windows Sandbox.",
            userAction: "Close the application that is still running in Windows Sandbox, then retry.",
            context: relativePath is null
                ? null
                : new Dictionary<string, string> { ["relativePath"] = relativePath },
            innerException: ex);
    }

    /// <summary>Lowercase hex SHA-256 of a file's contents.</summary>
    public static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 64 * 1024,
            useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Maps a root name to its folder.
    /// </summary>
    /// <remarks>
    /// A closed set shared with the host: an unrecognised root is refused rather than treated as a
    /// directory name, which would let the host name any folder it liked under the managed root.
    /// </remarks>
    private static string RootFolder(string root) => GuestRootNames.FolderFor(root);

    /// <summary>Removes directories left empty by deletion, without touching the scope root.</summary>
    private static void PruneEmptyDirectories(string scopeRoot, string directory)
    {
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            PruneEmptyDirectories(scopeRoot, child);
        }

        if (!string.Equals(directory, scopeRoot, StringComparison.OrdinalIgnoreCase) &&
            !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
        }
    }
}

/// <summary>
/// One in-progress file write: content accumulates in a temporary file and is verified before the
/// destination is replaced.
/// </summary>
/// <remarks>
/// Verifying before publishing is what makes an interrupted transfer safe. Writing straight to the
/// destination would leave a file whose size and hash no longer match either the old or the new
/// content, and the next reconciliation would see only "changed" — silently accepting a corrupt
/// binary as a legitimate update.
/// </remarks>
internal sealed class GuestFileWrite : IAsyncDisposable
{
    private readonly string _destination;
    private readonly string _temporary;
    private readonly GuestFileInfo _expected;
    private readonly FileStream _stream;
    private long _written;
    private bool _published;

    internal GuestFileWrite(string destination, string temporary, GuestFileInfo expected)
    {
        _destination = destination;
        _temporary = temporary;
        _expected = expected;
        _stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
    }

    /// <summary>Bytes received so far, reported when a transfer is interrupted.</summary>
    public long BytesWritten => _written;

    /// <summary>Appends a chunk, refusing to exceed the announced size.</summary>
    /// <exception cref="ExecutionTargetException">More bytes arrived than were announced.</exception>
    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (_written + data.Length > _expected.Size)
        {
            throw Interrupted("more content arrived than the transfer announced");
        }

        await _stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        _written += data.Length;
    }

    /// <summary>Verifies size and hash, then atomically replaces the destination.</summary>
    /// <exception cref="ExecutionTargetException">The content did not match what was announced.</exception>
    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);

        if (_written != _expected.Size)
        {
            throw Interrupted($"the transfer ended after {_written} of {_expected.Size} bytes");
        }

        var actualHash = await GuestFileService.ComputeHashAsync(_temporary, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualHash, _expected.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw Interrupted("the transferred content did not match its hash");
        }

        File.Move(_temporary, _destination, overwrite: true);

        // Preserving the source timestamp keeps guest file times meaningful, and keeps a
        // timestamp-based comparison from reporting every file as changed on the next run.
        File.SetLastWriteTimeUtc(_destination, new DateTime(_expected.LastWriteUtcTicks, DateTimeKind.Utc));
        _published = true;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);

        if (_published)
        {
            return;
        }

        // An unpublished transfer leaves nothing behind. Cleanup failure must never turn an
        // interrupted transfer into a success, so it is best-effort and non-fatal.
        try
        {
            File.Delete(_temporary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The temporary is named with a fresh GUID per transfer, so a file left behind here is
            // never picked up as content and never collides with a retry. Reporting it would turn a
            // harmless leftover into a failure the caller cannot act on, so it is traced instead.
            System.Diagnostics.Trace.TraceWarning(
                "Could not remove the incomplete transfer file '{0}': {1}", _temporary, ex.Message);
        }
    }

    private ExecutionTargetException Interrupted(string reason) =>
        ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.TransferInterrupted,
            $"Transferring '{_expected.RelativePath}' failed because {reason}.",
            userAction: "Retry the command.",
            context: new Dictionary<string, string>
            {
                ["relativePath"] = _expected.RelativePath,
                ["expectedSize"] = _expected.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["receivedBytes"] = _written.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
}
