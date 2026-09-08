// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Shared helpers for the NuGet feed tests. Centralizes rooting a <see cref="NugetService"/> /
/// <see cref="NugetSourceProvider"/> at a temporary config directory and authoring local folder feeds, so
/// the two focused test classes it serves — <see cref="NugetServiceFeedTests"/> (source/configuration and
/// version selection) and <see cref="NugetServiceDownloadTests"/> (download/install/authentication) — do
/// not duplicate the setup. Imported with <c>using static</c> so call sites stay unqualified.
/// </summary>
internal static class NugetFeedTestHelpers
{
    /// <summary>
    /// <see cref="IWinappDirectoryService"/> whose global directory is the real default
    /// (<c>%USERPROFILE%\.winapp</c>), so <see cref="NugetService"/> does NOT treat it as a test
    /// override and instead resolves the global packages folder from the supplied nuget.config
    /// (exercising <c>SettingsUtility.GetGlobalPackagesFolder</c>).
    /// </summary>
    private sealed class DefaultWinappDirectoryService : IWinappDirectoryService
    {
        public DirectoryInfo GetGlobalWinappDirectory() =>
            new(Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".winapp"));

        public DirectoryInfo GetLocalWinappDirectory(DirectoryInfo? baseDirectory = null) =>
            new(Path.Join((baseDirectory ?? new DirectoryInfo(Directory.GetCurrentDirectory())).FullName, ".winapp"));

        public void SetCacheDirectoryForTesting(DirectoryInfo? cacheDirectory)
        {
        }
    }

    internal static NugetSourceProvider CreateSourceProviderRootedAt(DirectoryInfo root) =>
        new(new CurrentDirectoryProvider(root.FullName));

    internal static NugetService CreateServiceRootedAt(DirectoryInfo root)
    {
        var sourceProvider = CreateSourceProviderRootedAt(root);
        return new NugetService(new DefaultWinappDirectoryService(), sourceProvider, new NugetPackageDownloader(sourceProvider));
    }

    internal static DirectoryInfo CreateFeedTestDirectory()
    {
        var dir = new DirectoryInfo(Path.Join(Path.GetTempPath(), $"NugetFeedTests_{Guid.NewGuid():N}"));
        dir.Create();
        return dir;
    }

    internal static void WriteNuGetConfig(DirectoryInfo dir, string contents) =>
        File.WriteAllText(Path.Join(dir.FullName, "nuget.config"), contents);

    internal static void TryDelete(DirectoryInfo dir)
    {
        try
        {
            dir.Refresh();
            if (dir.Exists)
            {
                dir.Delete(true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>
    /// Builds a minimal but valid .nupkg (with an optional dependency group) in memory, so tests can serve
    /// it from a local folder feed or an in-process HTTP feed without network access.
    /// </summary>
    internal static byte[] BuildNupkgBytes(string id, string version, params (string Id, string Version)[] dependencies)
    {
        var builder = new PackageBuilder
        {
            Id = id,
            Version = NuGetVersion.Parse(version),
            Description = $"{id} test package",
        };
        builder.Authors.Add("winapp-tests");

        if (dependencies.Length > 0)
        {
            builder.DependencyGroups.Add(new PackageDependencyGroup(
                NuGetFramework.Parse("net10.0"),
                [.. dependencies.Select(d => new PackageDependency(d.Id, VersionRange.Parse(d.Version)))]));
        }

        // A .nupkg must contain at least one file; add a trivial lib file from a temp source.
        var contentFile = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid():N}.txt");
        File.WriteAllText(contentFile, "test");
        try
        {
            builder.Files.Add(new PhysicalPackageFile { SourcePath = contentFile, TargetPath = $"lib/net10.0/{id}.txt" });

            using var stream = new MemoryStream();
            builder.Save(stream);
            return stream.ToArray();
        }
        finally
        {
            File.Delete(contentFile);
        }
    }

    /// <summary>
    /// Authors a minimal but valid .nupkg (with an optional dependency group) into a flat local feed
    /// folder, so tests can exercise the real download/extract/nuspec/recursive-dependency paths without
    /// network access.
    /// </summary>
    internal static void WriteNupkgToFeed(DirectoryInfo feedDir, string id, string version, params (string Id, string Version)[] dependencies) =>
        File.WriteAllBytes(
            Path.Join(feedDir.FullName, $"{id}.{version}.nupkg"),
            BuildNupkgBytes(id, version, dependencies));

    internal static void WriteLocalFeedConfig(DirectoryInfo root, DirectoryInfo feed, DirectoryInfo packages) =>
        WriteNuGetConfig(root, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <config>
                <add key="globalPackagesFolder" value="{packages.FullName}" />
              </config>
              <packageSources>
                <clear />
                <add key="local" value="{feed.FullName}" />
              </packageSources>
              <disabledPackageSources>
                <clear />
              </disabledPackageSources>
              <packageSourceMapping>
                <clear />
                <packageSource key="local">
                  <package pattern="*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);
}
