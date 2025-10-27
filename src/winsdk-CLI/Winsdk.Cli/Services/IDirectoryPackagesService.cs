// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace Winsdk.Cli.Services;

/// <summary>
/// Service for managing Directory.Packages.props files
/// </summary>
internal interface IDirectoryPackagesService
{
    /// <summary>
    /// Updates Directory.Packages.props in the specified directory to match versions from winsdk.yaml
    /// </summary>
    /// <param name="configDir">Directory containing winsdk.yaml and potentially Directory.Packages.props</param>
    /// <param name="packageVersions">Dictionary of package names to versions from winsdk.yaml</param>
    /// <returns>True if file was found and updated, false otherwise</returns>
    bool UpdatePackageVersions(string configDir, Dictionary<string, string> packageVersions);
}
