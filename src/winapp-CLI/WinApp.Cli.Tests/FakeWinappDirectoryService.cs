// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Fake <see cref="IWinappDirectoryService"/> that returns a caller-supplied global directory,
/// so services that derive cache paths from it can be tested against an isolated temp directory
/// without touching the real user profile.
/// </summary>
internal sealed class FakeWinappDirectoryService(DirectoryInfo globalDirectory) : IWinappDirectoryService
{
    public DirectoryInfo GlobalDirectory { get; set; } = globalDirectory;
    public bool IsGlobalCacheOverridden { get; set; } = true;
    public DirectoryInfo? LocalCacheDirectory { get; set; }
    public DirectoryInfo GetLocalCacheDirectory() => LocalCacheDirectory ?? new(Path.Combine(GlobalDirectory.FullName, "local-cache"));

    public DirectoryInfo GetGlobalWinappDirectory() => GlobalDirectory;

    public DirectoryInfo GetLocalWinappDirectory(DirectoryInfo? baseDirectory = null) =>
        new(Path.Combine((baseDirectory ?? GlobalDirectory).FullName, ".winapp"));

    public void SetCacheDirectoryForTesting(DirectoryInfo? cacheDirectory)
    {
        if (cacheDirectory != null)
        {
            GlobalDirectory = cacheDirectory;
        }
    }
}
