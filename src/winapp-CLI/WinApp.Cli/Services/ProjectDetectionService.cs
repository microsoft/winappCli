// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

/// <summary>
/// Performs breadth-first detection of compatible projects in a directory tree.
/// Pure detection logic — no UI dependencies. Use <see cref="IProgress{T}"/> for live updates.
/// </summary>
internal sealed class ProjectDetectionService(
    ILogger<ProjectDetectionService> logger,
    IDotNetService dotNetService) : IProjectDetectionService
{
    /// <summary>
    /// Directory names that are skipped by default during project search.
    /// These are well-known output, cache, or dependency directories that
    /// are unlikely to contain user project roots.
    /// </summary>
    internal static readonly HashSet<string> DefaultIgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules",
        "bin",
        "obj",
        "debug",
        "release",
        ".git",
        ".vs",
        ".vscode",
        ".idea",
        "packages",
        "dist",
        "build",
        "out",
        "target",
        ".winapp",
        "artifacts",
        "TestResults",
        "__pycache__",
        ".gradle",
        ".dart_tool",
        ".pub-cache",
        ".nuget",
        ".cargo",
    };

    /// <summary>
    /// Directory names that are always skipped during project search.
    /// These are either not real project directories or can cause infinite loops.
    /// </summary>
    private static readonly HashSet<string> HardIgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        "node_modules",
    };

    public DetectedProject? DetectProjectAt(DirectoryInfo directory)
    {
        return DetectProject(directory, directory);
    }

    public Task<IReadOnlyList<DetectedProject>> DetectProjectsAsync(
        DirectoryInfo root,
        int maxProjects,
        IProgress<DetectedProject>? progress,
        CancellationToken cancellationToken)
    {
        var results = new List<DetectedProject>();
        var queue = new Queue<DirectoryInfo>();
        queue.Enqueue(root);

        while (queue.Count > 0 && results.Count < maxProjects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = queue.Dequeue();

            var detected = DetectProject(current, root);
            if (detected != null)
            {
                results.Add(detected);
                progress?.Report(detected);
                logger.LogDebug("Detected {Type} project at {Path}", detected.TypeLabel, detected.DisplayPath);
                // Prune: don't search subdirectories of a detected project
                continue;
            }

            // Enqueue subdirectories for further searching
            EnqueueSubdirectories(queue, current, DefaultIgnoredDirectories);
        }

        return Task.FromResult<IReadOnlyList<DetectedProject>>(results);
    }

    /// <summary>
    /// Checks a single directory for all known project markers.
    /// Returns the most specific match, or null if no project is detected.
    /// Detection order follows enum specificity: Tauri > Electron > Flutter > Dotnet > Rust > CPP.
    /// </summary>
    internal static DetectedProject? DetectProject(DirectoryInfo directory, DirectoryInfo searchRoot)
    {
        var displayPath = GetRelativeDisplayPath(directory, searchRoot);

        // Tauri: check immediate subdirectories for tauri.conf.json
        var tauriConf = FindTauriConfFile(directory);
        if (tauriConf != null)
        {
            return new DetectedProject(DetectedProjectType.Tauri, directory, displayPath, tauriConf);
        }

        // Electron: package.json with electron dependency (check before generic markers)
        if (IsElectronProject(directory))
        {
            return new DetectedProject(DetectedProjectType.Electron, directory, displayPath, "package.json");
        }

        // Flutter: pubspec.yaml
        if (File.Exists(Path.Combine(directory.FullName, "pubspec.yaml")))
        {
            return new DetectedProject(DetectedProjectType.Flutter, directory, displayPath, "pubspec.yaml");
        }

        // .NET: *.csproj (only executable, non-test projects)
        var csprojName = FindExecutableCsproj(directory);
        if (csprojName != null)
        {
            return new DetectedProject(DetectedProjectType.Dotnet, directory, displayPath, csprojName);
        }

        // Rust: Cargo.toml
        if (File.Exists(Path.Combine(directory.FullName, "Cargo.toml")))
        {
            return new DetectedProject(DetectedProjectType.Rust, directory, displayPath, "Cargo.toml");
        }

        // C++: CMakeLists.txt
        if (File.Exists(Path.Combine(directory.FullName, "CMakeLists.txt")))
        {
            return new DetectedProject(DetectedProjectType.CPP, directory, displayPath, "CMakeLists.txt");
        }

        return null;
    }

    private static string? FindTauriConfFile(DirectoryInfo directory)
    {
        try
        {
            foreach (var subDir in directory.EnumerateDirectories())
            {
                // Skip symlinks/junctions to prevent reading outside the search root
                if (subDir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (File.Exists(Path.Combine(subDir.FullName, "tauri.conf.json")))
                {
                    return $"{subDir.Name}/tauri.conf.json";
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we can't access
        }
        catch (IOException)
        {
            // Skip directories with I/O errors
        }

        return null;
    }

    private static bool IsElectronProject(DirectoryInfo directory)
    {
        var packageJsonPath = Path.Combine(directory.FullName, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            return false;
        }

        try
        {
            var content = File.ReadAllText(packageJsonPath);
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            return HasElectronDependency(root, "dependencies") ||
                   HasElectronDependency(root, "devDependencies");
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasElectronDependency(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var deps) &&
               deps.ValueKind == JsonValueKind.Object &&
               deps.TryGetProperty("electron", out _);
    }

    /// <summary>
    /// Returns the file name of the first executable .csproj in the directory,
    /// or null if none is found. Only executable (OutputType = Exe, WinExe, or AppContainerExe),
    /// non-test projects qualify.
    /// </summary>
    internal static string? FindExecutableCsproj(DirectoryInfo directory)
    {
        IEnumerable<FileInfo> csprojFiles;
        try
        {
            csprojFiles = directory.EnumerateFiles("*.csproj");
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        foreach (var csproj in csprojFiles)
        {
            if (IsExecutableNonTestProject(csproj))
            {
                return csproj.Name;
            }
        }

        return null;
    }

    internal static bool IsExecutableNonTestProject(FileInfo csprojFile)
        => ClassifyRunnableStatic(csprojFile) == ProjectRunnability.App;

    /// <summary>
    /// Static (XML-only) classification used by directory detection and as the fallback when MSBuild
    /// evaluation is unavailable. Reads <c>OutputType</c>/<c>IsTestProject</c> plus inline
    /// <c>ProjectCapability</c>/<c>PackageReference</c> items. Cannot see imported values (SDK defaults,
    /// the test SDK) — that is what <see cref="ClassifyRunnableAsync"/>'s evaluation adds.
    /// </summary>
    internal static ProjectRunnability ClassifyRunnableStatic(FileInfo csprojFile)
    {
        try
        {
            var doc = XDocument.Load(csprojFile.FullName);
            // Use LocalName to match elements regardless of XML namespace
            // (SDK-style projects have no namespace; legacy .NET Framework projects use the MSBuild namespace)
            var propertyGroups = doc.Descendants().Where(e => e.Name.LocalName == "PropertyGroup");

            string? outputType = null;
            bool? isTestProject = null;

            foreach (var pg in propertyGroups)
            {
                var outputTypeEl = pg.Elements().FirstOrDefault(e => e.Name.LocalName == "OutputType");
                if (outputTypeEl != null)
                {
                    outputType = outputTypeEl.Value.Trim();
                }

                var isTestEl = pg.Elements().FirstOrDefault(e => e.Name.LocalName == "IsTestProject");
                if (isTestEl != null)
                {
                    isTestProject = string.Equals(isTestEl.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase);
                }
            }

            // Only executable projects are ever runnable.
            if (!IsExecutableOutputType(outputType))
            {
                return ProjectRunnability.NotRunnable;
            }

            // Test signals: explicit IsTestProject, the VS TestContainer capability, or a test-framework
            // package reference. WinUI MSTest projects are WinExe apps that omit IsTestProject, so
            // OutputType alone cannot tell them apart from the real app.
            if (isTestProject == true || HasInlineTestMarkers(doc))
            {
                return ProjectRunnability.Test;
            }

            return ProjectRunnability.App;
        }
        catch
        {
            // If we can't parse the csproj, skip it
            return ProjectRunnability.NotRunnable;
        }
    }

    /// <summary>
    /// Reads the <em>explicitly declared</em> <c>OutputType</c> from the project XML (last-wins across
    /// PropertyGroups, namespace-agnostic), or <see langword="null"/> when none is declared (the value is
    /// then an SDK default that only an evaluation can resolve). Unlike <see cref="ClassifyRunnableStatic"/>
    /// this does NOT collapse an absent value to "not runnable" — the caller distinguishes the two so a
    /// project relying on an imported/SDK-default OutputType is never statically misjudged.
    /// </summary>
    internal static string? TryGetDeclaredOutputType(FileInfo csprojFile)
    {
        try
        {
            var doc = XDocument.Load(csprojFile.FullName);
            string? outputType = null;
            foreach (var pg in doc.Descendants().Where(e => e.Name.LocalName == "PropertyGroup"))
            {
                var outputTypeEl = pg.Elements().FirstOrDefault(e => e.Name.LocalName == "OutputType");
                if (outputTypeEl != null)
                {
                    outputType = outputTypeEl.Value.Trim();
                }
            }

            return string.IsNullOrEmpty(outputType) ? null : outputType;
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<ProjectRunnability> ClassifyRunnableAsync(
        FileInfo csproj,
        DirectoryInfo workingDirectory,
        IReadOnlyList<string>? extraMsbuildProperties,
        CancellationToken cancellationToken)
    {
        // Evaluate-only (no -t:Build): fast and side-effect free. We read static-ish properties plus the
        // ProjectCapability/PackageReference items, which are all evaluation-time (no restore needed).
        var argTokens = new List<string>
        {
            "msbuild",
            csproj.FullName,
            "--getProperty:OutputType",
            "--getProperty:IsTestProject",
            "--getItem:ProjectCapability",
            "--getItem:PackageReference",
        };

        // Match what the build pass will see: a project whose OutputType/IsTestProject depends on
        // $(SolutionDir) (shared prop imports) would otherwise evaluate differently here than at build
        // time and be misclassified. The caller injects the same Solution* properties when resolving
        // from a solution.
        if (extraMsbuildProperties is { Count: > 0 })
        {
            argTokens.AddRange(extraMsbuildProperties);
        }

        var arguments = WindowsCommandLine.JoinArguments(argTokens) ?? string.Empty;

        try
        {
            var (exitCode, stdout, _) = await dotNetService.RunDotnetCommandAsync(workingDirectory, arguments, cancellationToken);
            if (exitCode == 0)
            {
                var props = MsBuildPropertyReader.Parse(stdout, ["OutputType", "IsTestProject"]);
                if (props.Count > 0)
                {
                    var outputType = props.TryGetValue("OutputType", out var ot) ? ot.Trim() : null;
                    if (!IsExecutableOutputType(outputType))
                    {
                        return ProjectRunnability.NotRunnable;
                    }

                    var isTest = props.TryGetValue("IsTestProject", out var it)
                        && string.Equals(it.Trim(), "true", StringComparison.OrdinalIgnoreCase);
                    if (isTest)
                    {
                        return ProjectRunnability.Test;
                    }

                    var items = MsBuildPropertyReader.ParseItems(stdout);
                    return HasEvaluatedTestMarkers(items) ? ProjectRunnability.Test : ProjectRunnability.App;
                }
            }

            logger.LogDebug("{UISymbol} Could not evaluate {Project} for disambiguation; falling back to static parse.", UiSymbols.Note, csproj.Name);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller requested cancellation (e.g. Ctrl+C) — propagate instead of masking it as an
            // evaluation failure and silently continuing with the static fallback.
            throw;
        }
        catch (Exception ex)
        {
            // dotnet not on PATH / evaluation failed → fall back to the static parse below.
            logger.LogDebug("{UISymbol} Evaluation of {Project} failed ({Message}); falling back to static parse.", UiSymbols.Note, csproj.Name, ex.Message);
        }

        return ClassifyRunnableStatic(csproj);
    }

    /// <summary>
    /// Package-id prefixes (case-insensitive) that mark a project as a test project: the .NET test SDK
    /// and test host plus the MSTest/xUnit/NUnit families. A WinUI MSTest app references these but does
    /// not set <c>IsTestProject</c>, so this is how it is told apart from a real app.
    /// </summary>
    private static readonly string[] TestPackagePrefixes =
    [
        "microsoft.net.test.sdk",
        "microsoft.testplatform.testhost",
        "mstest",
        "xunit",
        "nunit",
    ];

    /// <summary>True when a project capability marks a test container (the VS/WinUI test marker).</summary>
    internal static bool IsTestContainerCapability(string? capability) =>
        string.Equals(capability?.Trim(), "TestContainer", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when a package id belongs to a known test framework / test host.</summary>
    internal static bool IsKnownTestPackage(string? packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return false;
        }

        var id = packageId.Trim();
        return TestPackagePrefixes.Any(prefix => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Scans inline csproj XML for a TestContainer capability or a test-framework package ref, counting
    /// ONLY unconditional markers — an element (or any ancestor <c>ItemGroup</c>/<c>Choose</c>/<c>When</c>/
    /// <c>Project</c>) carrying a <c>Condition</c> is ignored. This is the static fallback used when an
    /// evaluated classification isn't available; a test <c>PackageReference</c>/capability gated behind an
    /// inactive Condition (e.g. a Configuration-specific <c>ItemGroup</c>) would otherwise misclassify a
    /// runnable app as a test project and drop it from directory detection. The evaluated path
    /// (<see cref="HasEvaluatedTestMarkers"/>) already resolves Conditions under the real globals.
    /// </summary>
    private static bool HasInlineTestMarkers(XDocument doc)
    {
        // Trust a marker only when neither it nor any ancestor carries a Condition (mirrors the
        // unconditional-inline check used for static <TargetFrameworks> reads). A conditioned marker is
        // deferred to the authoritative evaluated classification rather than assumed active.
        static bool IsUnconditional(XElement element) =>
            !element.AncestorsAndSelf().Any(a => !string.IsNullOrWhiteSpace(a.Attribute("Condition")?.Value));

        var capabilities = doc.Descendants()
            .Where(e => e.Name.LocalName == "ProjectCapability" && IsUnconditional(e))
            .Select(e => (string?)e.Attribute("Include"));
        if (capabilities.Any(IsTestContainerCapability))
        {
            return true;
        }

        var packageRefs = doc.Descendants()
            .Where(e => e.Name.LocalName == "PackageReference" && IsUnconditional(e))
            .Select(e => (string?)e.Attribute("Include") ?? (string?)e.Attribute("Update"));
        return packageRefs.Any(IsKnownTestPackage);
    }

    /// <summary>Scans evaluated MSBuild items for a TestContainer capability or a test-framework package ref.</summary>
    private static bool HasEvaluatedTestMarkers(IReadOnlyDictionary<string, IReadOnlyList<string>> items)
    {
        if (items.TryGetValue("ProjectCapability", out var caps) && caps.Any(IsTestContainerCapability))
        {
            return true;
        }

        return items.TryGetValue("PackageReference", out var pkgs) && pkgs.Any(IsKnownTestPackage);
    }

    /// <summary>
    /// The single source of truth for which <c>OutputType</c> values count as a runnable executable
    /// (<c>Exe</c>, <c>WinExe</c>, or classic UWP <c>AppContainerExe</c>). Shared by the static parse and the evaluated classification so the
    /// rule cannot drift between them.
    /// </summary>
    internal static bool IsExecutableOutputType(string? outputType) =>
        string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(outputType, "AppContainerExe", StringComparison.OrdinalIgnoreCase);

    private void EnqueueSubdirectories(Queue<DirectoryInfo> queue, DirectoryInfo parent, HashSet<string> ignoredNames)
    {
        IEnumerable<DirectoryInfo> subdirs;
        try
        {
            subdirs = parent.EnumerateDirectories();
        }
        catch (UnauthorizedAccessException)
        {
            logger.LogDebug("Cannot access directory: {Path}", parent.FullName);
            return;
        }
        catch (IOException ex)
        {
            logger.LogDebug("I/O error enumerating {Path}: {Message}", parent.FullName, ex.Message);
            return;
        }

        try
        {
            foreach (var subDir in subdirs)
            {
                try
                {
                    // Skip hidden directories (starting with .) that aren't in the explicit ignore list
                    if (subDir.Name.StartsWith('.') && !ignoredNames.Contains(subDir.Name))
                    {
                        logger.LogDebug("Skipping hidden directory: {Path}", subDir.FullName);
                        continue;
                    }

                    if (ignoredNames.Contains(subDir.Name))
                    {
                        logger.LogDebug("Skipping ignored directory: {Path}", subDir.FullName);
                        continue;
                    }

                    // Skip reparse points (symlinks, junctions) to avoid cycles
                    if (subDir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        logger.LogDebug("Skipping reparse point: {Path}", subDir.FullName);
                        continue;
                    }

                    queue.Enqueue(subDir);
                }
                catch (UnauthorizedAccessException)
                {
                    logger.LogDebug("Cannot access directory: {Path}", subDir.FullName);
                }
                catch (IOException ex)
                {
                    logger.LogDebug("I/O error accessing {Path}: {Message}", subDir.FullName, ex.Message);
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            logger.LogDebug("Cannot access directory during enumeration: {Path}", parent.FullName);
        }
        catch (IOException ex)
        {
            logger.LogDebug("I/O error during enumeration of {Path}: {Message}", parent.FullName, ex.Message);
        }
    }

    private static string GetRelativeDisplayPath(DirectoryInfo directory, DirectoryInfo searchRoot)
    {
        var rootPath = searchRoot.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var dirPath = directory.FullName.TrimEnd(Path.DirectorySeparatorChar);

        if (string.Equals(dirPath + Path.DirectorySeparatorChar, rootPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(dirPath, searchRoot.FullName.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            return ".";
        }

        if (dirPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return dirPath[rootPath.Length..].Replace(Path.DirectorySeparatorChar, '/');
        }

        return dirPath;
    }
}
