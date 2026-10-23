// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Represents a project detected during directory scanning.
/// </summary>
/// <param name="Type">The detected project type</param>
/// <param name="Directory">The root directory of the detected project</param>
/// <param name="DisplayPath">Relative path from the search root for display purposes</param>
/// <param name="ProjectFileName">The primary project file name (e.g., "MyApp.csproj", "package.json")</param>
internal sealed record DetectedProject(DetectedProjectType Type, DirectoryInfo Directory, string DisplayPath, string ProjectFileName)
{
    /// <summary>
    /// Returns the relative path to the project file with ./ prefix, e.g. "./src/MyApp/MyApp.csproj"
    /// </summary>
    public string DisplayFilePath => DisplayPath == "."
        ? $"./{ProjectFileName}"
        : $"./{DisplayPath}/{ProjectFileName}";

    /// <summary>
    /// Returns a human-readable description like ".NET project (./src/MyApp/MyApp.csproj)"
    /// </summary>
    public string ToDisplayString() => $"{TypeLabel} project ({DisplayFilePath})";

    /// <summary>
    /// Returns the user-friendly label for the project type.
    /// </summary>
    public string TypeLabel => Type switch
    {
        DetectedProjectType.Tauri => "Tauri",
        DetectedProjectType.Electron => "Electron",
        DetectedProjectType.Flutter => "Flutter",
        DetectedProjectType.Dotnet => ".NET",
        DetectedProjectType.Rust => "Rust",
        DetectedProjectType.CPP => "C++",
        _ => Type.ToString()
    };
}
