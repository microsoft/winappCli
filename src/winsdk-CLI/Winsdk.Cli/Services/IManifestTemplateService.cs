// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Winsdk.Cli.Models;

namespace Winsdk.Cli.Services;

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
        CancellationToken cancellationToken = default);
}
