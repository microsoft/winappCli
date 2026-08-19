// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Builds and prepares classic C# UWP projects for the packaged <c>winapp run</c> pipeline.
/// </summary>
internal interface ILegacyUwpRunService
{
    /// <summary>Returns true when the project is a classic UAP application.</summary>
    bool IsLegacyUwpProject(FileInfo csproj);

    /// <summary>
    /// Builds with Visual Studio MSBuild, resolves the loose AppX layout, and installs restored
    /// framework APPX dependencies required before the main package can be registered.
    /// </summary>
    Task<LegacyUwpBuildOutcome> BuildAndPrepareAsync(
        FileInfo csproj,
        LegacyUwpRunOptions options,
        CancellationToken cancellationToken);
}
