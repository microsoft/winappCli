// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Tests;

/// <summary>
/// Path construction for execution-target tests.
/// </summary>
/// <remarks>
/// One helper rather than the same join repeated across a dozen fixtures, for the same reason
/// production paths go through <c>TargetPathSafety</c>: the value of a rule is that no call site is
/// exempt from it.
/// <para>
/// <see cref="Path.Join(string, string)"/> is used throughout because
/// <see cref="Path.Combine(string, string)"/> silently discards everything before a rooted segment,
/// so <c>Combine(root, @"C:\Windows")</c> returns <c>C:\Windows</c>. These are test paths built from
/// trusted roots and literal names, so no containment check is warranted here — deliberately not
/// duplicating the production security invariant, which would then have two implementations to keep
/// in agreement.
/// </para>
/// </remarks>
internal static class TestPaths
{
    /// <summary>A unique, not-yet-created temp directory path for one fixture.</summary>
    public static string TempRoot(string prefix) =>
        Path.Join(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");

    /// <summary>A unique, not-yet-created temp file path with the given extension.</summary>
    public static string TempFile(string prefix, string extension) =>
        Path.Join(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}{extension}");

    /// <summary>Joins <paramref name="segments"/> beneath <paramref name="root"/>.</summary>
    public static string Under(string root, params string[] segments)
    {
        var path = root;

        foreach (var segment in segments)
        {
            path = Path.Join(path, segment);
        }

        return path;
    }

    /// <summary>A relative path built from ordered segments.</summary>
    public static string Relative(params string[] segments)
    {
        var path = segments.Length == 0 ? string.Empty : segments[0];

        for (var i = 1; i < segments.Length; i++)
        {
            path = Path.Join(path, segments[i]);
        }

        return path;
    }

    /// <summary>Full path of an executable in the Windows system directory.</summary>
    public static string SystemExecutable(string fileName) =>
        Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.System), fileName);
}
