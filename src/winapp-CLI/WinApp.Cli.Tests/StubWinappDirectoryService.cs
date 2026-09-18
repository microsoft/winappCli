// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Minimal <see cref="IWinappDirectoryService"/> stub returning a caller-controlled
/// global directory, so cache-path resolution can be driven deterministically.
/// </summary>
internal sealed class StubWinappDirectoryService(DirectoryInfo global) : IWinappDirectoryService
{
    public bool IsGlobalCacheOverridden => true;
    public DirectoryInfo GetLocalCacheDirectory() => new(Path.Combine(global.FullName, "local-cache"));
    public DirectoryInfo GetGlobalWinappDirectory() => global;
    public DirectoryInfo GetLocalWinappDirectory(DirectoryInfo? baseDirectory = null) => global;
    public void SetCacheDirectoryForTesting(DirectoryInfo? cacheDirectory) { }
}
