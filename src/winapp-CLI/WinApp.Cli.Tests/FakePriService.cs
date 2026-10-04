// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Test double for <see cref="IPriService"/>. Records calls and returns canned results so
/// packaging code paths that depend on PRI generation / language extraction can be exercised
/// without invoking the real <c>makepri.exe</c> tool.
/// </summary>
internal sealed class FakePriService : IPriService
{
    public Task<PriXamlResources> VerifyXamlResourcesAsync(FileInfo priFile, IReadOnlyDictionary<string, string> expectedHashes,
        TaskContext taskContext, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("This test did not configure an embedded XAML resource proof.");
    public List<string> LanguagesToReturn { get; set; } = [];
    public List<FileInfo> GeneratedPriFiles { get; set; } = [];
    public int ExtractLanguagesCallCount { get; private set; }
    public int CreatePriConfigCallCount { get; private set; }
    public int GeneratePriFileCallCount { get; private set; }

    public Task<FileInfo> CreatePriConfigAsync(
        DirectoryInfo packageDir,
        TaskContext taskContext,
        IEnumerable<string> precomputedPriResourceCandidates,
        string language = "en-US",
        string platformVersion = "10.0.0",
        CancellationToken cancellationToken = default)
    {
        CreatePriConfigCallCount++;
        return Task.FromResult(new FileInfo(Path.Combine(packageDir.FullName, "priconfig.xml")));
    }

    public Task<List<FileInfo>> GeneratePriFileAsync(
        DirectoryInfo packageDir,
        TaskContext taskContext,
        FileInfo? configPath = null,
        FileInfo? outputPath = null,
        CancellationToken cancellationToken = default)
    {
        GeneratePriFileCallCount++;
        return Task.FromResult(GeneratedPriFiles);
    }

    public Task<List<string>> ExtractLanguagesFromPriAsync(
        FileInfo priFile,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        ExtractLanguagesCallCount++;
        return Task.FromResult(LanguagesToReturn);
    }
}
