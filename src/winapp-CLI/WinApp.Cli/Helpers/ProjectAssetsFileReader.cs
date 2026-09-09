// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using WinApp.Cli.Models;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Reads the package graph a build actually resolved, from the <c>project.assets.json</c> that build
/// consumed.
/// </summary>
/// <remarks>
/// <c>dotnet package list</c> re-evaluates the project, and it accepts no <c>-c</c>, <c>-r</c> or
/// <c>-p</c> — so it cannot be told what the build used. Environment variables are not a substitute:
/// MSBuild ranks them BELOW a property the project assigns, while the <c>-c</c>/<c>-r</c>/<c>-p</c> the
/// build passes are global properties that outrank it. A file-based app with
/// <c>#:property MyFeature=off</c> and a <c>Directory.Build.props</c> that references a package when
/// <c>MyFeature == 'on'</c> demonstrates the gap: built with <c>-p:MyFeature=on</c> the package is in the
/// graph, while the same request made through the environment is not.
/// <para>
/// The assets file has no such ambiguity — it is restore's output for the inputs the build ran with, so
/// reading it describes the binary that exists rather than re-deriving a graph that may not match.
/// </para>
/// </remarks>
internal static class ProjectAssetsFileReader
{
    /// <summary>
    /// Reads <paramref name="assetsFile"/> into the same shape <c>dotnet package list --format json</c>
    /// produces, or null when it is absent or unreadable so the caller can fall back to that command.
    /// </summary>
    public static DotNetPackageListJson? TryRead(FileInfo assetsFile)
    {
        if (!assetsFile.Exists)
        {
            return null;
        }

        ProjectAssetsJson? assets;
        try
        {
            assets = JsonSerializer.Deserialize(
                File.ReadAllText(assetsFile.FullName), ProjectAssetsJsonContext.Default.ProjectAssetsJson);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (assets?.Project?.Frameworks is not { Count: > 0 } declaredFrameworks)
        {
            return null;
        }

        var frameworks = new List<DotNetFramework>();
        foreach (var (targetFramework, declared) in declaredFrameworks)
        {
            // Restore writes one target per TFM plus one per TFM/RID. The RID-qualified target is what a
            // RID-specific build consumed, so both are folded together and deduplicated: for this graph a
            // superset is the safe direction, matching the fail-open filtering the callers already use.
            var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (targetName, libraries) in assets.Targets ?? [])
            {
                if (!TargetMatchesFramework(targetName, targetFramework) || libraries is null)
                {
                    continue;
                }

                foreach (var (libraryKey, library) in libraries)
                {
                    // Skip project-to-project references: only NuGet packages belong in a package list.
                    if (!string.Equals(library?.Type, "package", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var separator = libraryKey.LastIndexOf('/');
                    if (separator > 0)
                    {
                        resolved.TryAdd(libraryKey[..separator], libraryKey[(separator + 1)..]);
                    }
                }
            }

            var topLevelIds = declared.Dependencies is { Count: > 0 } dependencies
                ? new HashSet<string>(dependencies.Keys, StringComparer.OrdinalIgnoreCase)
                : [];

            var topLevel = new List<DotNetPackage>();
            var transitive = new List<DotNetPackage>();
            foreach (var (id, version) in resolved)
            {
                (topLevelIds.Contains(id) ? topLevel : transitive).Add(new DotNetPackage(id, version, version));
            }

            frameworks.Add(new DotNetFramework(targetFramework, topLevel, transitive));
        }

        return frameworks.Count > 0
            ? new DotNetPackageListJson([new DotNetProject(frameworks)])
            : null;
    }

    /// <summary>
    /// True when a <c>targets</c> key is the given TFM, with or without a trailing <c>/&lt;rid&gt;</c>.
    /// </summary>
    private static bool TargetMatchesFramework(string targetName, string targetFramework)
    {
        if (string.Equals(targetName, targetFramework, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return targetName.Length > targetFramework.Length
            && targetName[targetFramework.Length] == '/'
            && targetName.AsSpan(0, targetFramework.Length).Equals(targetFramework, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record ProjectAssetsJson(
    Dictionary<string, Dictionary<string, ProjectAssetsLibrary?>?>? Targets,
    ProjectAssetsProject? Project);

internal sealed record ProjectAssetsLibrary(string? Type);

internal sealed record ProjectAssetsProject(Dictionary<string, ProjectAssetsFramework>? Frameworks);

internal sealed record ProjectAssetsFramework(Dictionary<string, JsonElement>? Dependencies);

[JsonSerializable(typeof(ProjectAssetsJson))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class ProjectAssetsJsonContext : JsonSerializerContext
{
}
