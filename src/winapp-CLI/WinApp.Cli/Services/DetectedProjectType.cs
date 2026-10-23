// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Types of projects that can be detected by the project detection service.
/// Ordered by specificity — more specific types should be checked first
/// to avoid misclassifying (e.g., a Tauri project also has Cargo.toml).
/// </summary>
internal enum DetectedProjectType
{
    /// <summary>
    /// Tauri project (tauri.conf.json found one level below root)
    /// </summary>
    Tauri,

    /// <summary>
    /// Electron project (package.json with electron dependency)
    /// </summary>
    Electron,

    /// <summary>
    /// Flutter project (pubspec.yaml at project root)
    /// </summary>
    Flutter,

    /// <summary>
    /// .NET project (.csproj at project root)
    /// </summary>
    Dotnet,

    /// <summary>
    /// Rust project (Cargo.toml at project root)
    /// </summary>
    Rust,

    /// <summary>
    /// C++ project (CMakeLists.txt at project root)
    /// </summary>
    CPP
}
