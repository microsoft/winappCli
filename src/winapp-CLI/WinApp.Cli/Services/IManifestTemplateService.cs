// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal interface IManifestTemplateService
{
    Task GenerateCompleteManifestAsync(
        DirectoryInfo outputDirectory,
        string packageName,
        string publisherName,
        string version,
        string entryPoint,
        ManifestTemplates manifestTemplate,
        string description,
        string? hostId,
        string? hostParameters,
        string? hostRuntimeDependencyPackageName,
        string? hostRuntimeDependencyPublisherName,
        string? hostRuntimeDependencyMinVersion,
        TaskContext taskContext,
        CancellationToken cancellationToken = default);
}
