// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>One file in a deployment's desired state.</summary>
/// <param name="RelativePath">Path relative to the deployment root, using backslash separators.</param>
/// <param name="Size">Length in bytes.</param>
/// <param name="LastWriteUtc">Last write time, preserved so guest timestamps stay useful.</param>
/// <param name="Sha256">Lowercase hex content hash, used to detect changes and verify transfers.</param>
internal sealed record DeploymentFile(string RelativePath, long Size, DateTimeOffset LastWriteUtc, string Sha256);

/// <summary>
/// The immutable desired state of one deployment (spec §"Host snapshot").
/// </summary>
/// <remarks>
/// Reconciliation compares against this rather than re-walking the source, so a build that changes
/// files midway cannot produce a guest that is a mix of two builds.
/// </remarks>
internal sealed record DeploymentSnapshot(string DeploymentId, IReadOnlyList<DeploymentFile> Files);

/// <summary>What must change in the guest to reach the desired state.</summary>
/// <param name="Added">Files not present in the guest.</param>
/// <param name="Changed">Files whose content differs.</param>
/// <param name="Removed">Guest-relative paths absent from the desired state.</param>
internal sealed record DeploymentPlan(
    IReadOnlyList<DeploymentFile> Added,
    IReadOnlyList<DeploymentFile> Changed,
    IReadOnlyList<string> Removed)
{
    /// <summary>True when the guest already matches the desired state.</summary>
    public bool IsEmpty => Added.Count == 0 && Changed.Count == 0 && Removed.Count == 0;

    /// <summary>Total bytes that must be transferred.</summary>
    public long TransferBytes => Added.Sum(f => f.Size) + Changed.Sum(f => f.Size);
}

/// <summary>Source-generated serializer context for persisted deployment state.</summary>
[JsonSerializable(typeof(DeploymentSnapshot))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class DeploymentJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Builds desired-state snapshots and reconciliation plans.
/// </summary>
/// <remarks>
/// Everything here is pure host-side file logic with no transport involved, so exact add, change,
/// delete, and path-safety behaviour is verifiable without a guest.
/// </remarks>
internal static class DeploymentPlanner
{
    /// <summary>
    /// Derives the internal deployment identity from the canonical input path and, when present,
    /// the original package identity.
    /// </summary>
    /// <remarks>
    /// This is never a public target and is never inferred from the current directory for UI
    /// selection. It exists only to scope guest directories, state, ownership, and artifacts.
    /// Two different projects that happen to share a package identity still get distinct IDs
    /// because the canonical path is included.
    /// </remarks>
    public static string CreateDeploymentId(string canonicalInputPath, string? originalPackageIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalInputPath);

        var material = originalPackageIdentity is null
            ? canonicalInputPath.ToUpperInvariant()
            : $"{canonicalInputPath.ToUpperInvariant()}\u0000{originalPackageIdentity}";

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));

        // 16 hex characters is ample to avoid collisions among one user's deployments and keeps
        // guest directory names short.
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    /// <summary>
    /// Captures the desired state of <paramref name="root"/>.
    /// </summary>
    /// <param name="root">Host folder to capture.</param>
    /// <param name="deploymentId">Deployment the snapshot belongs to.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="exclude">
    /// Optional predicate over the root-relative path, for content that must not be deployed.
    /// </param>
    /// <exception cref="ExecutionTargetException">
    /// A reparse point was found, a path escaped the root, or the source changed while being read.
    /// </exception>
    public static async Task<DeploymentSnapshot> CreateSnapshotAsync(
        DirectoryInfo root,
        string deploymentId,
        CancellationToken cancellationToken,
        Func<string, bool>? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(root);

        var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.FullName));
        var files = new List<DeploymentFile>();

        // Walked with the shared no-follow traversal rather than SearchOption.AllDirectories, which
        // follows directory junctions. A per-file reparse check is not a substitute and is not kept
        // here: files reached *through* a junction are ordinary files with no reparse attribute, so
        // a file-only check passes every one of them while deploying content from outside the root.
        // The walk refuses links — file or directory — before descending, so the rule lives in one
        // place rather than being restated at each call site.
        foreach (var info in HostSourceWalker.EnumerateFiles(rootPath, HostReparsePolicy.Reject, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = GetContainedRelativePath(rootPath, info.FullName);

            if (exclude?.Invoke(relativePath) == true)
            {
                continue;
            }

            // Re-proven immediately before the read, so a link planted after the walk — as an
            // ancestor or as the file itself — is caught rather than silently hashed through.
            HostSourceWalker.EnsureNoLinkOnPath(rootPath, info.FullName);

            var hash = await ComputeHashAsync(info.FullName, cancellationToken).ConfigureAwait(false);

            files.Add(new DeploymentFile(relativePath, info.Length, info.LastWriteTimeUtc, hash));
        }

        VerifyUnchanged(rootPath, files);

        // Ordering makes snapshots comparable and plans deterministic, which matters for both tests
        // and reproducible transfer order.
        files.Sort((left, right) => string.Compare(left.RelativePath, right.RelativePath, StringComparison.OrdinalIgnoreCase));
        return new DeploymentSnapshot(deploymentId, files);
    }

    /// <summary>
    /// Computes the exact add, change, and delete set needed to turn <paramref name="actual"/> into
    /// <paramref name="desired"/> (spec §"Exact in-place reconciliation").
    /// </summary>
    /// <remarks>
    /// Deletion of files absent from the desired state is deliberate: leaving a stale binary behind
    /// is how a rerun silently keeps executing code the developer just removed.
    /// </remarks>
    public static DeploymentPlan CreatePlan(
        DeploymentSnapshot desired,
        IReadOnlyList<DeploymentFile> actual)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(actual);

        var actualByPath = new Dictionary<string, DeploymentFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in actual)
        {
            actualByPath[file.RelativePath] = file;
        }

        var added = new List<DeploymentFile>();
        var changed = new List<DeploymentFile>();

        foreach (var file in desired.Files)
        {
            if (!actualByPath.TryGetValue(file.RelativePath, out var existing))
            {
                added.Add(file);
                continue;
            }

            // Content hash is authoritative. Size and timestamp alone would miss an edit that
            // preserved both, which build tools do more often than one would like.
            if (!string.Equals(existing.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                changed.Add(file);
            }
        }

        var desiredPaths = desired.Files
            .Select(f => f.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removed = actual
            .Select(f => f.RelativePath)
            .Where(path => !desiredPaths.Contains(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DeploymentPlan(added, changed, removed);
    }

    /// <summary>
    /// Resolves a guest-relative path against a root, refusing anything that escapes it.
    /// </summary>
    /// <remarks>
    /// Guest-provided paths never directly select host destinations, so every relative path that
    /// crosses the boundary is canonicalized and re-checked here. Rooted paths, drive-qualified
    /// paths, and <c>..</c> traversal are all rejected rather than normalized into something inside
    /// the root, because silently rewriting an escape attempt hides an attack.
    /// </remarks>
    public static string ResolveContainedPath(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw PathEscape(relativePath);
        }

        var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        // Path.Join rather than Path.Combine: Combine silently discards the root when the second
        // argument is rooted. The IsPathRooted guard above already rejects that case, and Join keeps
        // the two defences independent instead of relying on one.
        var combined = Path.GetFullPath(Path.Join(rootPath, relativePath));

        if (!TargetPathSafety.IsInsideRoot(rootPath, combined))
        {
            throw PathEscape(relativePath);
        }

        return combined;
    }

    /// <summary>Whether <paramref name="candidate"/> lies inside <paramref name="rootPath"/>.</summary>
    private static bool IsContained(string rootPath, string candidate) =>
        TargetPathSafety.IsInsideRoot(rootPath, candidate);

    private static string GetContainedRelativePath(string rootPath, string fullPath)
    {
        if (!IsContained(rootPath, fullPath))
        {
            throw PathEscape(fullPath);
        }

        return fullPath[(rootPath.Length + 1)..];
    }

    /// <summary>
    /// Confirms nothing changed while the snapshot was being taken.
    /// </summary>
    /// <remarks>
    /// Hashing a large output takes long enough for a concurrent build to rewrite part of it.
    /// Deploying that mixture would produce a guest matching no build at all, so the operation
    /// aborts and asks for a rebuild instead.
    /// <para>
    /// Internal rather than private so this guard can be verified directly: making a file change
    /// mid-enumeration is not something a test can schedule deterministically.
    /// </para>
    /// </remarks>
    internal static void VerifyUnchanged(string rootPath, IReadOnlyList<DeploymentFile> files)
    {
        foreach (var file in files)
        {
            // Route through the containment check rather than combining directly: these relative
            // paths come from a snapshot, and a snapshot that had been tampered with must not be
            // able to make this method stat a file outside the deployment root.
            var info = new FileInfo(ResolveContainedPath(rootPath, file.RelativePath));

            if (!info.Exists || info.Length != file.Size || info.LastWriteTimeUtc != file.LastWriteUtc)
            {
                throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.DeploymentDirty,
                    "The application's files changed while winapp was preparing to deploy them.",
                    userAction: "Rebuild, then run the command again.",
                    context: new Dictionary<string, string> { ["relativePath"] = file.RelativePath });
            }
        }
    }

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
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

    private static ExecutionTargetException PathEscape(string? relativePath) =>
        ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.DeploymentDirty,
            "A file path escaped the managed deployment folder and was rejected.",
            userAction: "Remove links or absolute paths from the application layout, then rebuild.",
            context: new Dictionary<string, string> { ["path"] = relativePath ?? string.Empty });
}
