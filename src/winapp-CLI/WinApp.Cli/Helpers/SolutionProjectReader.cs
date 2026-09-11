// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Reads the project list out of a solution file — classic <c>.sln</c> via regex, XML
/// <c>.slnx</c> via <see cref="XDocument"/> — without shelling out to <c>dotnet sln
/// list</c>, which needs SDK 9.0.200+ to understand <c>.slnx</c> at all.
/// </summary>
/// <remarks>
/// A solution defines its own membership, so anything that needs to know "which projects
/// does this solution build" reads it here rather than guessing from the directory tree.
/// A sibling directory can hold a project the solution deliberately excludes.
/// </remarks>
internal static partial class SolutionProjectReader
{
    /// <summary>Matches any project entry in a classic <c>.sln</c>, capturing the (relative) project path (any type).</summary>
    [GeneratedRegex(
        "Project\\(\"\\{[^\"}]*\\}\"\\)\\s*=\\s*\"[^\"]*\",\\s*\"([^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SlnAnyProjectPathRegex();

    /// <summary>Whether a path names a solution file this reader understands.</summary>
    internal static bool IsSolutionPath(string path) =>
        path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

    /// <summary>Extracts every listed project path (any type) from a classic <c>.sln</c> file.</summary>
    internal static List<string> ExtractSlnAllProjectPaths(string text) =>
        SlnAnyProjectPathRegex().Matches(text).Select(m => m.Groups[1].Value).ToList();

    /// <summary>Extracts every listed project path (any type) from an XML <c>.slnx</c> solution.</summary>
    internal static List<string> ExtractSlnxAllProjectPaths(string text)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(text);
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }

        return doc.Descendants()
            .Where(e => string.Equals(e.Name.LocalName, "Project", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Attributes()
                .FirstOrDefault(a => string.Equals(a.Name.LocalName, "Path", StringComparison.OrdinalIgnoreCase))?.Value)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToList();
    }

    /// <summary>
    /// Normalizes a solution-relative project path (either slash flavor) and resolves it to
    /// an absolute path against <paramref name="solutionDir"/>. Returns <c>null</c> when the
    /// path is malformed or unsafe to touch, so callers skip it.
    /// <para>
    /// Only relative entries are accepted. A rooted path in the solution discards
    /// <paramref name="solutionDir"/> entirely (that is how <see cref="Path.Combine(string,
    /// string)"/> is defined), so a crafted solution could otherwise name any location it
    /// likes — including a UNC path, which callers would probe with <c>File.Exists</c>,
    /// opening an SMB connection that authenticates to whoever answers.
    /// </para>
    /// <para>
    /// Relative entries may climb above the solution directory: a solution in <c>src\</c>
    /// listing <c>..\libs\Lib\Lib.csproj</c> is ordinary. They are rejected only when
    /// reaching them crosses a reparse point, which is how a checked-in symlink would
    /// redirect a local path onto a share. A solution the user opened from a network
    /// location resolves its members on that same share, as it should.
    /// </para>
    /// </summary>
    internal static string? TryResolveRelativePath(string solutionDir, string relative)
    {
        string normalized = relative
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        try
        {
            if (Path.IsPathRooted(normalized))
            {
                return null;
            }

            string full = Path.GetFullPath(Path.Combine(solutionDir, normalized));
            return PathSafety.CrossesReparsePoint(full, solutionDir) ? null : full;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The absolute paths of every project a solution lists, in solution order and
    /// de-duplicated. Returns an empty list when the file cannot be read or lists nothing,
    /// which callers must treat as "membership unknown" rather than "no projects" — an
    /// unreadable solution should not be read as an empty one.
    /// </summary>
    internal static List<string> ReadProjectPaths(string solutionPath)
    {
        string text;
        try
        {
            text = File.ReadAllText(solutionPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        string solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? Directory.GetCurrentDirectory();
        List<string> relativePaths = solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            ? ExtractSlnxAllProjectPaths(text)
            : ExtractSlnAllProjectPaths(text);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return relativePaths
            .Select(relative => TryResolveRelativePath(solutionDir, relative))
            .Where(full => full is not null)
            .Select(full => full!)
            .Where(seen.Add)
            .ToList();
    }
}
