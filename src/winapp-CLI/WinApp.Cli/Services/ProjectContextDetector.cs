// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Xml.Linq;

namespace WinApp.Cli.Services;

/// <summary>
/// Classifies projects from a bounded set of recognized metadata markers. It never scans source
/// files, recursively searches a workspace, or returns project paths or names.
/// </summary>
internal sealed class ProjectContextDetector : IProjectContextDetector
{
    private const int MaxAncestorDepth = 8;
    private const int MaxProjectFilesPerDirectory = 8;
    private const long MaxMetadataFileBytes = 512 * 1024;
    private static readonly string[] DependencySections =
        ["dependencies", "devDependencies", "peerDependencies", "optionalDependencies"];

    public ProjectContext DetectProject(FileInfo projectFile)
    {
        if (projectFile.Extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return DetectDotnetProject(projectFile) with
            {
                TargetKind = ProjectTargetKind.SourceProject,
                Source = ProjectContextSource.ResolvedProject,
            };
        }

        if (projectFile.Extension.Equals(".vcxproj", StringComparison.OrdinalIgnoreCase))
        {
            return new ProjectContext(
                ProjectFamily.Cpp,
                ProjectAppFramework.Unknown,
                ProjectTargetKind.SourceProject,
                ProjectContextSource.ResolvedProject,
                ProjectContextConfidence.High);
        }

        return ProjectContext.Unknown(ProjectTargetKind.SourceProject);
    }

    public ProjectContext DetectDirectory(
        DirectoryInfo directory,
        ProjectTargetKind fallbackTargetKind = ProjectTargetKind.Workspace)
    {
        DirectoryInfo? current = directory;
        for (var depth = 0; current is not null && depth <= MaxAncestorDepth; depth++)
        {
            var detected = DetectAt(current);
            if (detected is not null)
            {
                return detected with
                {
                    TargetKind = ProjectTargetKind.SourceProject,
                    Source = depth == 0 ? ProjectContextSource.ExactMarker : ProjectContextSource.AncestorMarker,
                    Confidence = depth == 0
                        ? detected.Confidence
                        : ProjectContextConfidence.Medium,
                };
            }

            if (IsRepositoryBoundary(current))
            {
                break;
            }

            current = current.Parent;
        }

        return ProjectContext.Unknown(fallbackTargetKind);
    }

    public ProjectContext DetectDirectories(
        IEnumerable<DirectoryInfo> directories,
        ProjectTargetKind fallbackTargetKind)
    {
        var contexts = directories
            .Select(directory => DetectDirectory(directory, fallbackTargetKind))
            .Where(context => context.IsKnown)
            .ToList();

        if (contexts.Count == 0)
        {
            return ProjectContext.Unknown(fallbackTargetKind);
        }

        if (contexts.Count == 1)
        {
            return contexts[0];
        }

        var families = contexts.Select(context => context.Family).Distinct().ToList();
        var frameworks = contexts
            .Select(context => context.Framework)
            .Where(framework => framework != ProjectAppFramework.Unknown)
            .Distinct()
            .ToList();
        var packagingKinds = contexts
            .Select(context => context.Packaging)
            .Where(packaging => packaging != ProjectContextPackaging.Unknown)
            .Distinct()
            .ToList();

        return new ProjectContext(
            families.Count == 1 ? families[0] : ProjectFamily.Mixed,
            frameworks.Count switch
            {
                0 => ProjectAppFramework.Unknown,
                1 => frameworks[0],
                _ => ProjectAppFramework.Mixed,
            },
            ProjectTargetKind.SourceProject,
            contexts.Any(context => context.Source == ProjectContextSource.AncestorMarker)
                ? ProjectContextSource.AncestorMarker
                : ProjectContextSource.ExactMarker,
            contexts.All(context => context.Confidence == ProjectContextConfidence.High)
                ? ProjectContextConfidence.High
                : ProjectContextConfidence.Medium,
            packagingKinds.Count == 1 ? packagingKinds[0] : ProjectContextPackaging.Unknown);
    }

    public ProjectContext CreateNuGetContext(string? frameworkHint)
    {
        var framework = frameworkHint?.Trim().ToLowerInvariant() switch
        {
            "winui" => ProjectAppFramework.WinUI,
            "wpf" => ProjectAppFramework.Wpf,
            "winforms" => ProjectAppFramework.WinForms,
            "maui" => ProjectAppFramework.Maui,
            "avalonia" => ProjectAppFramework.Avalonia,
            "other-dotnet" => ProjectAppFramework.OtherDotnet,
            _ => ProjectAppFramework.OtherDotnet,
        };

        var recognizedHint = frameworkHint?.Trim().ToLowerInvariant() is
            "winui" or "wpf" or "winforms" or "maui" or "avalonia" or "other-dotnet";

        return new ProjectContext(
            ProjectFamily.Dotnet,
            framework,
            ProjectTargetKind.SourceProject,
            ProjectContextSource.NuGetMsBuild,
            recognizedHint ? ProjectContextConfidence.High : ProjectContextConfidence.Medium,
            ProjectContextPackaging.Packaged,
            ProjectExecutionMode.Folder);
    }

    private static ProjectContext? DetectAt(DirectoryInfo directory)
    {
        if (HasTauriConfig(directory))
        {
            return Known(ProjectFamily.Hybrid, ProjectAppFramework.Tauri);
        }

        ProjectContext? nodeContext = null;
        var packageJson = new FileInfo(Path.Join(directory.FullName, "package.json"));
        if (packageJson.Exists)
        {
            nodeContext = DetectNodeProject(packageJson);
            if (nodeContext.Framework != ProjectAppFramework.Unknown)
            {
                return nodeContext;
            }
        }

        if (File.Exists(Path.Join(directory.FullName, "pubspec.yaml")))
        {
            return Known(ProjectFamily.Dart, ProjectAppFramework.Flutter);
        }

        var dotnetProjects = EnumerateFiles(directory, "*.csproj");
        if (dotnetProjects.Count > 0)
        {
            return MergeProjects(dotnetProjects.Select(DetectDotnetProject));
        }

        if (EnumerateFiles(directory, "*.vcxproj").Count > 0)
        {
            return DetectCppProject(directory);
        }

        if (File.Exists(Path.Join(directory.FullName, "Cargo.toml")))
        {
            return Known(ProjectFamily.Rust, ProjectAppFramework.Unknown);
        }

        if (File.Exists(Path.Join(directory.FullName, "CMakeLists.txt")))
        {
            return DetectCppProject(directory);
        }

        return nodeContext;
    }

    private static ProjectContext DetectDotnetProject(FileInfo projectFile)
    {
        try
        {
            if (!CanReadMetadata(projectFile))
            {
                return Known(ProjectFamily.Dotnet, ProjectAppFramework.OtherDotnet, ProjectContextConfidence.Medium);
            }

            var document = XDocument.Load(projectFile.FullName);
            var properties = document
                .Descendants()
                .Where(element => element.Parent?.Name.LocalName == "PropertyGroup")
                .GroupBy(element => element.Name.LocalName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last().Value.Trim(), StringComparer.OrdinalIgnoreCase);
            var packageReferences = document
                .Descendants()
                .Where(element => element.Name.LocalName == "PackageReference")
                .Select(element => element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var framework =
                IsTrue(properties, "UseMaui") ? ProjectAppFramework.Maui :
                IsTrue(properties, "UseWinUI") ? ProjectAppFramework.WinUI :
                IsTrue(properties, "UseWPF") ? ProjectAppFramework.Wpf :
                IsTrue(properties, "UseWindowsForms") ? ProjectAppFramework.WinForms :
                IsUwp(properties) ? ProjectAppFramework.Uwp :
                packageReferences.Any(reference => reference!.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase))
                    ? ProjectAppFramework.Avalonia :
                packageReferences.Contains("Microsoft.WindowsAppSDK")
                    ? ProjectAppFramework.WindowsAppSdk :
                ProjectAppFramework.OtherDotnet;

            var packaging = properties.TryGetValue("WindowsPackageType", out var packageType)
                && packageType.Equals("None", StringComparison.OrdinalIgnoreCase)
                    ? ProjectContextPackaging.Unpackaged
                    : HasManifest(projectFile.Directory)
                        || IsTrue(properties, "EnableMsixTooling")
                        || !string.IsNullOrWhiteSpace(packageType)
                            ? ProjectContextPackaging.Packaged
                            : ProjectContextPackaging.Unknown;

            return Known(ProjectFamily.Dotnet, framework) with { Packaging = packaging };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return Known(ProjectFamily.Dotnet, ProjectAppFramework.OtherDotnet, ProjectContextConfidence.Medium);
        }
    }

    private static ProjectContext DetectNodeProject(FileInfo packageJson)
    {
        try
        {
            if (!CanReadMetadata(packageJson))
            {
                return Known(ProjectFamily.Node, ProjectAppFramework.Unknown, ProjectContextConfidence.Medium);
            }

            using var document = JsonDocument.Parse(File.ReadAllText(packageJson.FullName));
            var root = document.RootElement;

            if (HasDependency(root, "@tauri-apps/cli") || HasDependency(root, "@tauri-apps/api"))
            {
                return Known(ProjectFamily.Hybrid, ProjectAppFramework.Tauri);
            }
            if (HasDependency(root, "electron"))
            {
                return Known(ProjectFamily.Node, ProjectAppFramework.Electron);
            }
            if (HasDependency(root, "react-native-windows"))
            {
                return Known(ProjectFamily.Node, ProjectAppFramework.ReactNativeWindows);
            }
            if (UsesWinUiBindings(root))
            {
                return Known(ProjectFamily.Node, ProjectAppFramework.WinUI);
            }

            return Known(ProjectFamily.Node, ProjectAppFramework.Unknown);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return Known(ProjectFamily.Node, ProjectAppFramework.Unknown, ProjectContextConfidence.Medium);
        }
    }

    private static ProjectContext DetectCppProject(DirectoryInfo directory)
    {
        var configFile = new FileInfo(Path.Join(directory.FullName, "winapp.yaml"));
        if (!CanReadMetadata(configFile))
        {
            return Known(ProjectFamily.Cpp, ProjectAppFramework.Unknown);
        }

        try
        {
            var config = ConfigService.Parse(File.ReadAllText(configFile.FullName));
            var framework = config.Packages.Any(package =>
                package.Name.Equals("Microsoft.WindowsAppSDK", StringComparison.OrdinalIgnoreCase))
                ? ProjectAppFramework.WindowsAppSdk
                : ProjectAppFramework.Unknown;
            return Known(ProjectFamily.Cpp, framework);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Known(ProjectFamily.Cpp, ProjectAppFramework.Unknown, ProjectContextConfidence.Medium);
        }
    }

    private static ProjectContext MergeProjects(IEnumerable<ProjectContext> projects)
    {
        var contexts = projects.ToList();
        var frameworks = contexts.Select(context => context.Framework).Distinct().ToList();
        var packagingKinds = contexts
            .Select(context => context.Packaging)
            .Where(packaging => packaging != ProjectContextPackaging.Unknown)
            .Distinct()
            .ToList();

        return Known(
            ProjectFamily.Dotnet,
            frameworks.Count == 1 ? frameworks[0] : ProjectAppFramework.Mixed,
            contexts.All(context => context.Confidence == ProjectContextConfidence.High)
                ? ProjectContextConfidence.High
                : ProjectContextConfidence.Medium) with
        {
            Packaging = packagingKinds.Count == 1 ? packagingKinds[0] : ProjectContextPackaging.Unknown,
        };
    }

    private static ProjectContext Known(
        ProjectFamily family,
        ProjectAppFramework framework,
        ProjectContextConfidence confidence = ProjectContextConfidence.High) =>
        new(
            family,
            framework,
            ProjectTargetKind.SourceProject,
            ProjectContextSource.ExactMarker,
            confidence);

    private static bool HasDependency(JsonElement root, string dependencyName) =>
        DependencySections.Any(sectionName =>
            root.TryGetProperty(sectionName, out var dependencies)
            && dependencies.ValueKind == JsonValueKind.Object
            && dependencies.TryGetProperty(dependencyName, out _));

    private static bool UsesWinUiBindings(JsonElement root)
    {
        if (!root.TryGetProperty("winapp", out var winapp)
            || !winapp.TryGetProperty("jsBindings", out var bindings)
            || !bindings.TryGetProperty("additionalWinmds", out var winmds)
            || winmds.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return winmds
            .EnumerateArray()
            .Where(winmd =>
                winmd.TryGetProperty("namespace", out var namespaceElement)
                && namespaceElement.ValueKind == JsonValueKind.String)
            .Any(winmd =>
                winmd.GetProperty("namespace").GetString()
                    ?.StartsWith("Microsoft.UI.Xaml", StringComparison.Ordinal) == true);
    }

    private static bool IsTrue(Dictionary<string, string> properties, string name) =>
        properties.TryGetValue(name, out var value)
        && value.Equals("true", StringComparison.OrdinalIgnoreCase);

    private static bool IsUwp(Dictionary<string, string> properties) =>
        (properties.TryGetValue("TargetPlatformIdentifier", out var platformIdentifier)
            && platformIdentifier.Equals("UAP", StringComparison.OrdinalIgnoreCase))
        || (properties.TryGetValue("TargetFrameworkIdentifier", out var frameworkIdentifier)
            && frameworkIdentifier.Equals("UAP", StringComparison.OrdinalIgnoreCase));

    private static bool HasManifest(DirectoryInfo? directory) =>
        directory is not null
        && (File.Exists(Path.Join(directory.FullName, "Package.appxmanifest"))
            || File.Exists(Path.Join(directory.FullName, "appxmanifest.xml")));

    private static bool HasTauriConfig(DirectoryInfo directory)
    {
        try
        {
            return directory
                .EnumerateDirectories()
                .Where(subdirectory => !subdirectory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .Take(32)
                .Any(subdirectory => File.Exists(Path.Join(subdirectory.FullName, "tauri.conf.json")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool CanReadMetadata(FileInfo file)
    {
        try
        {
            file.Refresh();
            return file.Exists && file.Length <= MaxMetadataFileBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static List<FileInfo> EnumerateFiles(DirectoryInfo directory, string pattern)
    {
        try
        {
            return directory
                .EnumerateFiles(pattern, SearchOption.TopDirectoryOnly)
                .Take(MaxProjectFilesPerDirectory)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsRepositoryBoundary(DirectoryInfo directory) =>
        Directory.Exists(Path.Join(directory.FullName, ".git"))
        || File.Exists(Path.Join(directory.FullName, ".git"));
}
