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
    /// Checks if Directory.Packages.props exists in the specified directory
    /// </summary>
    /// <param name="configDir"></param>
    /// <returns></returns>
    bool Exists(DirectoryInfo configDir);

    /// <summary>
    /// Updates Directory.Packages.props in the specified directory to match versions from winapp.yaml
    /// </summary>
    /// <param name="configDir">Directory containing winapp.yaml and potentially Directory.Packages.props</param>
    /// <param name="packageVersions">Dictionary of package names to versions from winapp.yaml</param>
    /// <returns>True if any package versions were changed, false if no changes were needed</returns>
    /// <exception cref="FileNotFoundException">Thrown when Directory.Packages.props does not exist</exception>
    /// <exception cref="InvalidOperationException">Thrown when the file is invalid or has no PackageVersion elements</exception>
    bool UpdatePackageVersions(DirectoryInfo configDir, Dictionary<string, string> packageVersions, TaskContext taskContext);
}
