// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Reads <c>templatecache.json</c> files from the template engine's per-user cache root
/// (<c>&lt;dotnet home&gt;/.templateengine/dotnetcli/&lt;sdk-version&gt;/templatecache.json</c>). All IO
/// failures are swallowed into an empty result so template-metadata lookups degrade to a heuristic
/// rather than failing the command.
/// </summary>
internal sealed class TemplateCacheReader : ITemplateCacheReader
{
    private const string DotnetHomeVariableName = "DOTNET_CLI_HOME";

    // Seams so tests can point the reader at a temporary hive without touching the real environment.
    internal Func<string, string?> EnvironmentVariableProvider { get; set; } = Environment.GetEnvironmentVariable;

    internal Func<string> UserProfileProvider { get; set; } =
        () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// The home folder the dotnet CLI keeps its <c>.templateengine</c> hive under, resolved the same way
    /// the CLI does: <c>DOTNET_CLI_HOME</c> when set and non-empty, then <c>USERPROFILE</c>, then the OS
    /// profile folder. Reading any other location would describe a different set of installed packs
    /// than the <c>dotnet new</c> processes winapp runs.
    /// </summary>
    internal string? GetDotnetHomePath()
    {
        var home = EnvironmentVariableProvider(DotnetHomeVariableName);
        if (string.IsNullOrEmpty(home))
        {
            home = EnvironmentVariableProvider("USERPROFILE");
        }

        if (string.IsNullOrEmpty(home))
        {
            home = UserProfileProvider();
        }

        return string.IsNullOrEmpty(home) ? null : home;
    }

    public IReadOnlyList<string> ReadTemplateCacheDocuments()
    {
        var documents = new List<string>();
        string root;
        try
        {
            var home = GetDotnetHomePath();
            if (home is null)
            {
                return documents;
            }

            root = Path.Join(home, ".templateengine", "dotnetcli");
        }
        catch (Exception ex) when (ex is ArgumentException or PlatformNotSupportedException)
        {
            return documents;
        }

        if (!Directory.Exists(root))
        {
            return documents;
        }

        IEnumerable<string> caches;
        try
        {
            // One templatecache.json per SDK version folder. Folders for SDKs that are no longer used
            // keep describing whatever pack version was installed back then, so order newest-first:
            // the cache the active SDK just rebuilt comes before stale ones from older SDK bands.
            caches = new DirectoryInfo(root)
                .EnumerateFiles("templatecache.json", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.FullName)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return documents;
        }

        foreach (var file in caches)
        {
            try
            {
                documents.Add(File.ReadAllText(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip an unreadable cache file; another may still contain the template.
            }
        }

        return documents;
    }
}
