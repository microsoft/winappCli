// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>
/// The single fail-closed rule for building paths from derived or untrusted segments.
/// </summary>
/// <remarks>
/// Every managed path in the execution-target code is built from a trusted root plus one or more
/// derived segments — a target slug, a state file name, a guest-relative path. Each of those is a
/// place where a value that should name a file inside a managed folder could instead name something
/// outside it.
/// <para>
/// Centralising the rule matters more than the individual call sites: <see cref="Path.Combine"/>
/// silently discards everything before a rooted segment, so <c>Combine(root, @"C:\Windows")</c>
/// returns <c>C:\Windows</c>. <see cref="Path.Join"/> avoids that particular surprise but validates
/// nothing, so it is not a substitute — a segment containing <c>..</c> still escapes. Validation
/// happens here, and callers additionally keep the final canonicalisation and containment check.
/// </para>
/// </remarks>
internal static class TargetPathSafety
{
    /// <summary>
    /// Validates a single path segment: it must be a plain file or directory name.
    /// </summary>
    /// <remarks>
    /// Rejects empty values, rooted values, anything containing a directory separator, relative
    /// specifiers, and characters Windows does not permit in a name. Rejecting rather than
    /// sanitising is deliberate: silently rewriting a value that tried to escape hides the attempt.
    /// </remarks>
    /// <exception cref="ExecutionTargetException">The segment is not a plain name.</exception>
    public static string EnsureSafeSegment(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            throw Rejected(segment, "it is empty");
        }

        if (segment is "." or "..")
        {
            throw Rejected(segment, "it is a relative path specifier");
        }

        if (Path.IsPathRooted(segment))
        {
            throw Rejected(segment, "it is a rooted path");
        }

        if (segment.Contains(Path.DirectorySeparatorChar) ||
            segment.Contains(Path.AltDirectorySeparatorChar) ||
            segment.Contains(Path.VolumeSeparatorChar))
        {
            throw Rejected(segment, "it contains a path separator");
        }

        if (segment.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw Rejected(segment, "it contains characters that are not valid in a file name");
        }

        return segment;
    }

    /// <summary>
    /// Joins validated <paramref name="segments"/> onto <paramref name="root"/> and proves the
    /// result stays inside it.
    /// </summary>
    /// <remarks>
    /// Both halves are load-bearing. Segment validation stops a rooted or traversing value from
    /// being combined at all; the containment check afterwards is what makes the guarantee hold
    /// regardless of how the root itself was expressed.
    /// </remarks>
    /// <exception cref="ExecutionTargetException">A segment is unsafe, or the result escapes.</exception>
    public static string CombineInsideRoot(string root, params string[] segments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(segments);

        var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var combined = rootPath;

        foreach (var segment in segments)
        {
            combined = Path.Join(combined, EnsureSafeSegment(segment));
        }

        var full = Path.GetFullPath(combined);
        if (!IsInsideRoot(rootPath, full))
        {
            throw Rejected(string.Join('/', segments), "the result would fall outside its managed folder");
        }

        return full;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is <paramref name="rootPath"/> or lies beneath it.
    /// </summary>
    /// <remarks>
    /// The separator check after the prefix comparison is what stops a sibling whose name merely
    /// starts with the root's name — <c>C:\work-2</c> against <c>C:\work</c> — from counting as
    /// contained.
    /// </remarks>
    public static bool IsInsideRoot(string rootPath, string candidate)
    {
        if (!candidate.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return candidate.Length == rootPath.Length
            || candidate[rootPath.Length] == Path.DirectorySeparatorChar
            || candidate[rootPath.Length] == Path.AltDirectorySeparatorChar;
    }

    /// <summary>
    /// Whether two paths name the same location, after canonicalizing case, trailing separators,
    /// and relative segments.
    /// </summary>
    /// <remarks>
    /// For comparing two values this code already trusts — for example a location it resolved and
    /// persisted itself against a value an OS query just reported — never for validating an
    /// untrusted path, which is what <see cref="CombineInsideRoot"/> and
    /// <see cref="EnsureSafeSegment"/> are for. Ordinal and case-insensitive to match NTFS.
    /// </remarks>
    public static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    private static ExecutionTargetException Rejected(string? segment, string reason) =>
        ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.TargetAmbiguous,
            $"A managed path component was rejected because {reason}.",
            userAction: "Retry the command. If it keeps failing, report this with the command you ran.",
            context: new Dictionary<string, string> { ["segment"] = segment ?? string.Empty });
}
