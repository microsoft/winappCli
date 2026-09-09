// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal interface IManifestTemplateService
{
    /// <param name="displayName">
    /// Human-readable name for <c>Properties/DisplayName</c> and <c>uap:VisualElements/@DisplayName</c>.
    /// <see langword="null"/> keeps the pre-existing behavior of reusing <paramref name="packageName"/>.
    /// </param>
    /// <param name="applicationId">
    /// Explicit <c>Application/@Id</c>. <see langword="null"/> keeps the pre-existing behavior of deriving
    /// it from <paramref name="packageName"/>.
    /// </param>
    Task GenerateCompleteManifestAsync(
        DirectoryInfo outputDirectory,
        string packageName,
        string publisherName,
        string version,
        ManifestTemplates manifestTemplate,
        string description,
        TaskContext taskContext,
        string manifestFileName = "Package.appxmanifest",
        string? executableName = null,
        string? displayName = null,
        string? applicationId = null,
        CancellationToken cancellationToken = default);
}
