// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal interface IManifestService
{
    public Task GenerateManifestAsync(
        DirectoryInfo directory,
        string? packageName,
        string? publisherName,
        string version,
        string description,
        string? entryPoint,
        ManifestTemplates manifestTemplate,
        FileInfo? logoPath,
        bool yes,
        CancellationToken cancellationToken = default);
}
