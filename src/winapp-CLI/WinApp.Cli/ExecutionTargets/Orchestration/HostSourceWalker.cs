// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>What a host walk does when it meets a reparse point.</summary>
internal enum HostReparsePolicy
{
    /// <summary>
    /// Refuse the whole operation. Used by deployment, where a link in the source means the guest
    /// would not be a copy of the folder the developer named, and silently deploying something else
    /// is worse than saying so.
    /// </summary>
    Reject,

    /// <summary>
    /// Treat the entry as absent. Used by <c>target push</c>, matching the guest-side rule that
    /// nothing inside a managed root may redirect elsewhere.
    /// </summary>
    Skip,
}

/// <summary>
/// The single no-follow walk over a host folder that is about to be hashed, deployed, or copied
/// into a guest.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SearchOption.AllDirectories"/> follows directory junctions and symbolic links. That
/// makes a per-file reparse check useless as a containment defence: the junction is the directory,
/// and the ordinary files reached through it carry no reparse attribute at all, so every one of them
/// passes a file-only check while actually living outside the root. A junction named
/// <c>build\logs</c> pointing at <c>C:\Users\me\.ssh</c> would be enumerated, hashed, and copied
/// into the guest as <c>build\logs\id_rsa</c>.
/// </para>
/// <para>
/// So the walk is manual and one level at a time, and every directory is tested <em>before</em> it
/// is descended into — starting with the root itself, which the per-entry check never sees because
/// the walk begins by enumerating the root's contents rather than by looking at it. That also makes
/// a self-referencing junction terminate immediately rather than recursing until the path length or
/// the stack gives out: the loop edge is itself a reparse point, so it is refused at the point it
/// would have been followed.
/// </para>
/// <para>
/// <b>Threat model.</b> This is containment against links that exist, and against a link planted
/// between the walk and the read — <see cref="EnsureNoLinkOnPath"/> re-checks every component,
/// including the file itself, immediately before each file is opened. It is not a handle-relative
/// TOCTOU proof: a mutually trusted same-user process that wins a race in the window between that
/// re-check and the open can still swap a component. Closing that completely needs handle-relative
/// opens (<c>FILE_FLAG_OPEN_REPARSE_POINT</c> on every component), which is out of scope here and
/// documented as a known limit in <c>docs/sandbox-execution.md</c>.
/// </para>
/// </remarks>
internal static class HostSourceWalker
{
    /// <summary>
    /// Enumerates every ordinary file beneath <paramref name="rootPath"/> without following links.
    /// </summary>
    /// <param name="rootPath">Canonical, separator-trimmed root. Never descended out of.</param>
    /// <param name="policy">What to do with a reparse point found <em>inside</em> the root.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Files in a stable, case-insensitive full-path order.</returns>
    /// <exception cref="ExecutionTargetException">
    /// The root itself is a link, <paramref name="policy"/> is <see cref="HostReparsePolicy.Reject"/>
    /// and a link was found inside it, or an entry resolved outside the root.
    /// </exception>
    public static List<FileInfo> EnumerateFiles(
        string rootPath,
        HostReparsePolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));

        // The root is checked before anything is walked, and independently of the policy. Descending
        // into a linked root would make the entire tree beneath it appear to be inside the folder
        // that was named while it is somewhere else, which is the same defect the per-entry check
        // prevents one level down — the entry check never sees the root, because the walk starts by
        // enumerating its contents rather than by looking at it.
        //
        // This is a hard failure even under Skip. "Treat the link as absent" applied to the root
        // would mean silently copying nothing while reporting success, and a silent wrong answer is
        // worse here than refusing.
        if (IsReparsePoint(root))
        {
            throw LinkRejected(new DirectoryInfo(root));
        }

        var files = new List<FileInfo>();

        Collect(root, root, policy, files, cancellationToken);

        files.Sort((left, right) =>
            string.Compare(left.FullName, right.FullName, StringComparison.OrdinalIgnoreCase));

        return files;
    }

    /// <summary>Walks exactly one directory level, refusing to descend through a reparse point.</summary>
    private static void Collect(
        string root,
        string directory,
        HostReparsePolicy policy,
        List<FileInfo> files,
        CancellationToken cancellationToken)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Checked for every entry, directory and file alike, and before any decision to descend.
            // This single test is what the whole class exists for.
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (policy is HostReparsePolicy.Reject)
                {
                    throw LinkRejected(entry);
                }

                continue;
            }

            if (entry is DirectoryInfo child)
            {
                Collect(root, child.FullName, policy, files, cancellationToken);
                continue;
            }

            // Structurally guaranteed by descending only into real directories from the root, and
            // asserted anyway: containment is the property this walk is claimed to have, so it is
            // proven rather than assumed.
            if (!TargetPathSafety.IsInsideRoot(root, entry.FullName))
            {
                throw Escaped(entry.FullName);
            }

            files.Add((FileInfo)entry);
        }
    }

    /// <summary>
    /// Re-proves that no component of <paramref name="fullPath"/> — root, intermediate directory, or
    /// the file itself — became a link since the walk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called immediately before the file is opened for hashing or copying, so the window in which a
    /// planted link goes unnoticed is the open itself rather than the whole enumerate-then-read pass.
    /// Deliberately re-stats every component instead of reusing the attributes enumeration already
    /// read, which would defeat the point entirely.
    /// </para>
    /// <para>
    /// <b>The leaf is included, and that is load-bearing.</b> Enumeration checks the file's reparse
    /// state, but that check happened earlier — it is exactly what this method exists to redo — and
    /// the read does not repeat it: <see cref="FileStream"/> follows a symbolic link to its target
    /// like any other open. So a file swapped for a link after enumeration would otherwise be
    /// hashed and copied straight out of the tree, which is the same escape as a linked directory,
    /// one level down.
    /// </para>
    /// <para>
    /// This narrows the exposure to the check-to-open interval; it does not eliminate it. A
    /// same-user process that wins that race can still swap a component, because closing it
    /// completely requires handle-relative no-follow opens on every component. See
    /// <c>docs/sandbox-execution.md</c>.
    /// </para>
    /// </remarks>
    /// <exception cref="ExecutionTargetException">
    /// A component is a link, or the path is not inside the root.
    /// </exception>
    public static void EnsureNoLinkOnPath(string rootPath, string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));

        if (!TargetPathSafety.IsInsideRoot(root, fullPath))
        {
            throw Escaped(fullPath);
        }

        // The root is re-checked too: it is as swappable as anything beneath it, and the walk's
        // own root check ran before the enumeration rather than before this read.
        if (IsReparsePoint(root))
        {
            throw LinkRejected(new DirectoryInfo(root));
        }

        var current = root;

        foreach (var segment in fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, segment);

            if (IsReparsePoint(current))
            {
                throw LinkRejected(new FileInfo(current));
            }
        }
    }

    /// <summary>
    /// Whether a path exists and is a reparse point, without following it.
    /// </summary>
    /// <remarks>
    /// <see cref="File.GetAttributes(string)"/> reports the attributes of the link itself rather
    /// than its target, and works for a file and a directory alike, so one probe covers every
    /// component of a path. A component that does not exist cannot redirect anything; the open that
    /// follows will fail on its own terms and produce a better error than this method could.
    /// </remarks>
    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>Whether a path is a link, for callers that hold a single named source.</summary>
    /// <remarks>
    /// <c>target push</c> can be pointed straight at one file. That file never goes through
    /// <see cref="EnumerateFiles"/>, so the same rule is applied to it here rather than left to the
    /// directory case alone.
    /// </remarks>
    public static bool IsLink(FileSystemInfo entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return entry.Exists && entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    private static ExecutionTargetException LinkRejected(FileSystemInfo entry) =>
        ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.DeploymentDirty,
            $"'{entry.Name}' is a symbolic link or junction, which cannot be deployed into the guest.",
            userAction: "Replace the link with the real file or folder, then rebuild.",
            context: new Dictionary<string, string> { ["fileName"] = entry.Name });

    private static ExecutionTargetException Escaped(string fullPath) =>
        ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.DeploymentDirty,
            "A file resolved outside the folder being deployed and was refused.",
            userAction: "Remove links from the folder, then retry.",
            context: new Dictionary<string, string> { ["fileName"] = Path.GetFileName(fullPath) });
}
