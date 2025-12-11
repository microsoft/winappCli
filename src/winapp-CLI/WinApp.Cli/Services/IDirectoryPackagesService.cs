// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Services;

/// <summary>
/// Service for managing Directory.Packages.props files
/// </summary>
internal interface IDirectoryPackagesService
{
    /// <summary>
    /// Updates Directory.Packages.props in the specified directory to match versions from winapp.yaml
    /// </summary>
    /// <param name="configDir">Directory containing winapp.yaml and potentially Directory.Packages.props</param>
    /// <param name="packageVersions">Dictionary of package names to versions from winapp.yaml</param>
    /// <returns>True if file was found and updated, false otherwise</returns>
    bool UpdatePackageVersions(DirectoryInfo configDir, Dictionary<string, string> packageVersions, TaskContext taskContext);
}
