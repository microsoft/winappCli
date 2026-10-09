// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.DevTools.Comments;

// --source-root is a starting point, never a literal store path.
internal enum CommentStoreRootKind
{
    RepositoryRoot,

    ProjectRootFallback,
    HostBound,
}

internal sealed record CommentStoreLocation(string Root, CommentStoreRootKind Kind, string StartDirectory)
{
    public string WinappDirectory => Path.Combine(Root, ".winapp");

    public string StorePath => Path.Combine(WinappDirectory, CommentStoreLocator.FileName);

    public string Explain() => Kind switch
    {
        CommentStoreRootKind.HostBound => $"{StorePath} (persisted on the host; guest source snapshot: {StartDirectory})",
        CommentStoreRootKind.RepositoryRoot => $"{StorePath} (git repository root for {StartDirectory})",
        _ => $"{StorePath} ({StartDirectory} is not inside a git working tree, so its own directory is the root)",
    };
}

internal static class CommentStoreLocator
{
    internal static bool SameProject(string? left, string? right) =>
        string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)
            ? string.IsNullOrEmpty(left) && string.IsNullOrEmpty(right)
            : string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);

    public const string FileName = "ui-comments.json";

    public static CommentStoreLocation Resolve(string? startDirectory)
    {
        var start = string.IsNullOrWhiteSpace(startDirectory)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(startDirectory);

        var repoRoot = FindRepositoryRoot(start);
        return repoRoot is null
            ? new CommentStoreLocation(start, CommentStoreRootKind.ProjectRootFallback, start)
            : new CommentStoreLocation(repoRoot, CommentStoreRootKind.RepositoryRoot, start);
    }

    /// <summary>
    /// Nearest ancestor (inclusive) containing <c>.git</c>. Matches a <b>file</b> as well as a directory, because
    /// that is how git marks a linked worktree or a submodule — treating those as "not a repo" would drop the
    /// store back at the project root for anyone reviewing in a worktree.
    /// </summary>
    private static string? FindRepositoryRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
