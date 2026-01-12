// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;
public record ManifestGenerationInfo(
    string PackageName,
    string PublisherName,
    string Version,
    string Description,
    string EntryPoint);

internal interface IManifestService
{
    public Task<ManifestGenerationInfo> PromptForManifestInfoAsync(
        DirectoryInfo directory,
        string? packageName,
        string? publisherName,
        string version,
        string? description,
        string? entryPoint,
        bool useDefaults,
        CancellationToken cancellationToken = default);

    public Task GenerateManifestAsync(
        DirectoryInfo directory,
        ManifestGenerationInfo manifestGenerationInfo,
        ManifestTemplates manifestTemplate,
        FileInfo? logoPath,
        TaskContext taskContext,
        CancellationToken cancellationToken = default);

    public Task UpdateManifestAssetsAsync(
        FileInfo manifestPath,
        FileInfo imagePath,
        TaskContext taskContext,
        CancellationToken cancellationToken = default);
}
