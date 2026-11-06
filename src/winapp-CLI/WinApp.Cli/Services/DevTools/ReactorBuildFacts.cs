// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace WinApp.Cli.Services.DevTools;

/// <summary>
/// What a project's build says about Microsoft.UI.Reactor, for <c>winapp run --devtools on</c>: whether it uses
/// Reactor, whether this configuration compiles Reactor's source-map interceptors (the C# call sites DevTools
/// shows), whether it still references the deprecated <c>Microsoft.UI.Reactor.Devtools</c> package, the
/// configuration it builds, and the build's <c>PathMap</c> and the projects it references so recorded paths can be
/// mapped back to files.
/// </summary>
internal sealed record ReactorBuildFacts(bool Referenced, string? SourceMapProperty, bool Interceptors, bool DevtoolsPackage, string? PathMap,
    IReadOnlyList<string>? ProjectReferences = null, string? Configuration = null)
{
    internal const string PathMapVariable = "WINAPP_DEVTOOLS_PATH_MAP";
    internal const string ProjectDirectoriesVariable = "WINAPP_DEVTOOLS_PROJECT_DIRS";
    internal const string SourceRootVariable = "WINAPP_DEVTOOLS_SOURCE_ROOT";
    internal const string InterceptorsNamespace = "Microsoft.UI.Reactor.Generated";
    internal const string DevtoolsPackageId = "Microsoft.UI.Reactor.Devtools";

    /// <summary>The MSBuild properties these facts are read from.</summary>
    internal static readonly string[] Properties =
        ["ReactorSourceMap", "InterceptorsNamespaces", "_MicrosoftUIReactorTargetsLoaded", "PathMap", "Configuration"];

    public static ReactorBuildFacts? From(IReadOnlyDictionary<string, string> properties, string? assetsFile)
    {
        string? Get(string name) => properties.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        var libraries = ReadLibraries(assetsFile);
        var referenced = string.Equals(Get("_MicrosoftUIReactorTargetsLoaded"), "true", StringComparison.OrdinalIgnoreCase) ||
            libraries.Contains("Microsoft.UI.Reactor");
        if (!referenced)
        {
            return null;
        }
        var interceptors = (Get("InterceptorsNamespaces") ?? "")
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(InterceptorsNamespace, StringComparer.Ordinal);
        return new ReactorBuildFacts(true, Get("ReactorSourceMap"), interceptors, libraries.Contains(DevtoolsPackageId), Get("PathMap"),
            ReadProjectReferences(assetsFile), Get("Configuration"));
    }

    /// <summary>
    /// The project files restore resolved as references of the project, direct or transitive, as the assets file
    /// records them: relative to the project's folder.
    /// </summary>
    internal static List<string> ReadProjectReferences(string? assetsFile)
    {
        var projects = new List<string>();
        if (string.IsNullOrEmpty(assetsFile) || !File.Exists(assetsFile))
        {
            return projects;
        }
        try
        {
            using var stream = File.OpenRead(assetsFile);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("libraries", out var libraries) && libraries.ValueKind == JsonValueKind.Object)
            {
                projects.AddRange(libraries.EnumerateObject().Select(library => ProjectPath(library.Value)).OfType<string>());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // No restore graph: only the project itself is known.
        }
        return projects;
    }

    // A restore library entry's path when it is a project reference ("type": "project"); null for packages.
    private static string? ProjectPath(JsonElement library) =>
        library.ValueKind == JsonValueKind.Object &&
        library.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "project" &&
        library.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String &&
        path.GetString() is { Length: > 0 } project
            ? project
            : null;

    /// <summary>The existing folders of the projects <paramref name="projectReferences"/> names, relative to <paramref name="projectDirectory"/>.</summary>
    internal static List<string> ProjectDirectories(string projectDirectory, IEnumerable<string> projectReferences)
    {
        var folders = new List<string>();
        foreach (var reference in projectReferences)
        {
            try
            {
                var folder = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(projectDirectory, reference.Replace('/', Path.DirectorySeparatorChar))));
                if (folder is not null && Directory.Exists(folder) && !folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                {
                    folders.Add(folder);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                // A reference that is not a path here names no folder to look in.
            }
        }
        return folders;
    }

    /// <summary>The package ids restore resolved for the project (direct or transitive).</summary>
    internal static HashSet<string> ReadLibraries(string? assetsFile)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(assetsFile) || !File.Exists(assetsFile))
        {
            return ids;
        }
        try
        {
            using var stream = File.OpenRead(assetsFile);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("libraries", out var libraries) && libraries.ValueKind == JsonValueKind.Object)
            {
                foreach (var library in libraries.EnumerateObject())
                {
                    var slash = library.Name.IndexOf('/');
                    ids.Add(slash > 0 ? library.Name[..slash] : library.Name);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // No restore graph: report only what the properties say.
        }
        return ids;
    }

    // MSBuild's default configuration is Debug, so an unknown one counts as Debug.
    private bool IsDebug => Configuration is null || string.Equals(Configuration, "Debug", StringComparison.OrdinalIgnoreCase);

    /// <summary>One line each for what the launch should know before DevTools shows this app.</summary>
    public IEnumerable<string> Warnings()
    {
        if (!Interceptors)
        {
            yield return string.Equals(SourceMapProperty, "false", StringComparison.OrdinalIgnoreCase)
                ? "This project sets <ReactorSourceMap>false</ReactorSourceMap>, so Reactor records no C# call sites: DevTools can " +
                  "show the component tree, but not which line created each element. Remove the property (or set it to true) to see sources."
                : "This build configuration compiles no Reactor source mapping (Reactor turns it on by default only in Debug), so DevTools " +
                  "can show the component tree, but not which line created each element. Run a Debug build, or set " +
                  "<ReactorSourceMap>true</ReactorSourceMap> for a build you do not distribute.";
        }
        // New Reactor apps reference the package in Debug only, for the live previews, so like Reactor's own
        // REACTOR_DEVTOOLS_001 this speaks up only when another configuration still references it.
        if (DevtoolsPackage && !IsDebug)
        {
            yield return $"This project references {DevtoolsPackageId}, which is deprecated: winapp DevTools replaces its inspection " +
                "tools. Keep it (Debug-only) only if you use the VS Code live preview or the Visual Studio embedded preview; " +
                "otherwise remove the package reference.";
        }
    }

    /// <summary>
    /// Hands the build's PathMap and the folders of the projects it references to the in-app engine, so recorded C#
    /// paths map back to files: Reactor records a path relative to the project that compiled it.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> ApplyEnvironment(IReadOnlyDictionary<string, string?> environment, ReactorBuildFacts? facts)
    {
        if (facts is null)
        {
            return environment;
        }
        var result = new Dictionary<string, string?>(environment, StringComparer.OrdinalIgnoreCase);
        if (facts.PathMap is { Length: > 0 } pathMap)
        {
            result[PathMapVariable] = pathMap;
        }
        result[ProjectDirectoriesVariable] =
            environment.TryGetValue(SourceRootVariable, out var sourceRoot) && !string.IsNullOrEmpty(sourceRoot) &&
            facts.ProjectReferences is { Count: > 0 } references && ProjectDirectories(sourceRoot, references) is { Count: > 0 } folders
                ? string.Join(';', folders)
                : null;
        return result;
    }
}
