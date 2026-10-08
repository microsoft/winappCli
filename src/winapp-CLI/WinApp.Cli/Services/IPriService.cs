// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Services;

internal interface IPriService
{
    Task<FileInfo> CreatePriConfigAsync(
        DirectoryInfo packageDir,
        TaskContext taskContext,
        IEnumerable<string> precomputedPriResourceCandidates,
        string language = "en-US",
        string platformVersion = "10.0.0",
        CancellationToken cancellationToken = default);

    Task<List<FileInfo>> GeneratePriFileAsync(
        DirectoryInfo packageDir,
        TaskContext taskContext,
        FileInfo? configPath = null,
        FileInfo? outputPath = null,
        CancellationToken cancellationToken = default);

    Task<List<string>> ExtractLanguagesFromPriAsync(
        FileInfo priFile,
        TaskContext taskContext,
        CancellationToken cancellationToken);

    /// <summary>Re-indexes the layout's <c>resources.pri</c>, if any, under a new package name.</summary>
    Task ReindexIdentityAsync(
        DirectoryInfo layout,
        string packageName,
        TaskContext taskContext,
        CancellationToken cancellationToken = default);
}
