// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Services;

internal interface IPriService
{
    Task ReindexIdentityAsync(
        DirectoryInfo layout,
        string originalPackageName,
        string effectivePackageName,
        TaskContext taskContext,
        CancellationToken cancellationToken = default);

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
}
