// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>One file that belongs in a portable .NET layout.</summary>
/// <param name="EntryPath">Path inside the layout, always forward-slashed.</param>
/// <param name="SourcePath">Host file the content comes from.</param>
internal sealed record DotNetLayoutEntry(string EntryPath, string SourcePath);

/// <summary>An official payload the host can turn into a portable .NET layout.</summary>
/// <param name="Name">Shared framework the layout provides.</param>
/// <param name="Version">Exact framework version.</param>
/// <param name="Entries">Every file the layout is made of.</param>
internal sealed record DotNetLayoutSource(string Name, Version Version, IReadOnlyList<DotNetLayoutEntry> Entries);

/// <summary>
/// What a .NET installation looks like on disk, in the one place both halves can agree on it.
/// </summary>
/// <remarks>
/// The host reads official payloads through this and the guest writes and verifies installations
/// through it, so "where a shared framework lives" and "what a usable one contains" are one
/// definition rather than two that can drift. A drift here would not fail loudly; it would produce a
/// guest that reports a framework installed and an application that cannot start.
/// <para>
/// The layout is the one <c>DOTNET_ROOT</c> describes: <c>shared/{framework}/{version}</c> beside
/// <c>host/fxr/{version}</c>. Pointing an apphost at such a root makes it resolve from there
/// exclusively, which is what lets a managed per-user installation work without touching, or even
/// knowing about, any machine-wide one.
/// </para>
/// </remarks>
internal static class DotNetLayout
{
    /// <summary>Folder holding shared frameworks inside a .NET root.</summary>
    internal const string SharedFolder = "shared";

    /// <summary>Folder holding host resolvers inside a .NET root.</summary>
    internal const string HostFxrFolder = "host/fxr";

    /// <summary>The framework whose payload also carries the host resolver.</summary>
    internal const string CoreFramework = "Microsoft.NETCore.App";

    /// <summary>Files a layout must contain before it is worth staging.</summary>
    /// <remarks>
    /// Assembled layouts are validated because assembling one is the step that can go quietly wrong:
    /// a pack whose folder names changed, or a partial copy, produces a directory that looks like a
    /// framework and cannot host anything. Checking for the pieces the runtime actually loads turns
    /// that into a resolution failure the user is told about, instead of a launch failure they are
    /// not.
    /// </remarks>
    private static readonly Dictionary<string, string[]> RequiredFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        [CoreFramework] = ["hostpolicy.dll", "coreclr.dll", "System.Private.CoreLib.dll"],
        ["Microsoft.WindowsDesktop.App"] = ["WindowsBase.dll", "System.Windows.Forms.dll"],
    };

    /// <summary>The .NET roots an apphost resolves against, in the order it consults them.</summary>
    /// <remarks>
    /// Every standard location, not just the default one. A false "not installed" would refuse to
    /// run an application that works, which is worse than the launch failure the check replaces — so
    /// the probe errs towards finding an installation wherever it plausibly is.
    /// </remarks>
    public static IEnumerable<string> DefaultRoots()
    {
        // DOTNET_ROOT first, and exclusively when it is set: that is the apphost's own precedence,
        // and mirroring it is what keeps a verification result equal to what the launch will find.
        foreach (var root in ((string[])
            ["DOTNET_ROOT", "DOTNET_ROOT(x86)", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64"])
            .Select(Environment.GetEnvironmentVariable)
            .OfType<string>()
            .Where(value => value.Length > 0))
        {
            yield return root;
        }

        foreach (var programFiles in ((Environment.SpecialFolder[])
            [Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86])
            .Select(Environment.GetFolderPath)
            .Where(value => value.Length > 0))
        {
            yield return Path.Join(programFiles, "dotnet");
        }

        // A per-user install, which is what the dotnet-install script produces by default.
        if (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } localAppData)
        {
            yield return Path.Join(localAppData, "Microsoft", "dotnet");
        }
    }

    /// <summary>
    /// Whether a .NET root serves <paramref name="architecture"/>.
    /// </summary>
    /// <remarks>
    /// Decided by reading the machine type of a native binary the root contains rather than by
    /// guessing from its path. An x64 machine has an x86 installation under
    /// <c>Program Files (x86)</c> and an arm64 one may carry an x64 installation in a subfolder, so
    /// path-shaped rules get this wrong in exactly the cross-architecture cases that matter.
    /// A root with nothing readable in it is treated as not matching: an unusable source is worse
    /// than no source, because it would be staged and then fail in the guest.
    /// </remarks>
    public static bool MatchesArchitecture(string root, string architecture)
    {
        foreach (var framework in SafeDirectories(Path.Join(root, SharedFolder)))
        {
            foreach (var probe in SafeDirectories(framework)
                .Select(version => Path.Join(version, "hostpolicy.dll")))
            {
                if (!File.Exists(probe))
                {
                    continue;
                }

                return string.Equals(
                    PeHelper.DetectPeArchitecture(probe),
                    RunArchHelper.NormalizeArchitecture(architecture),
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    /// <summary>Versions of one shared framework installed under a .NET root.</summary>
    public static IEnumerable<Version> InstalledVersions(string root, string frameworkName) =>
        SafeDirectories(Path.Join(root, SharedFolder, frameworkName))
            .Where(directory => IsUsableInstalledFramework(root, frameworkName, directory))
            .Select(directory => RuntimeRequirementDiscovery.ComparableVersion(Path.GetFileName(directory)))
            .Where(version => version > new Version(0, 0));

    /// <summary>
    /// Whether an installed version is complete enough for an apphost to use.
    /// </summary>
    /// <remarks>
    /// Directory existence alone is not evidence of a usable runtime: a process can die after
    /// publishing the shared-framework folder but before publishing its host resolver. Requiring the
    /// same load-bearing files used for host-side payload validation makes the next pass repair that
    /// interrupted install instead of treating it as complete.
    /// </remarks>
    private static bool IsUsableInstalledFramework(string root, string frameworkName, string directory)
    {
        if (!File.Exists(Path.Join(directory, $"{frameworkName}.deps.json")))
        {
            return false;
        }

        if (RequiredFiles.TryGetValue(frameworkName, out var required) &&
            required.Any(file => !File.Exists(Path.Join(directory, file))))
        {
            return false;
        }

        if (!IsCore(frameworkName))
        {
            return true;
        }

        return SafeDirectories(Path.Join(root, HostFxrFolder))
            .Any(version => File.Exists(Path.Join(version, "hostfxr.dll")));
    }

    /// <summary>Complete shared frameworks in a host installation that satisfy a requirement.</summary>
    public static IEnumerable<DotNetLayoutSource> EnumerateInstalled(
        string root,
        RuntimeFrameworkRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        foreach (var directory in SafeDirectories(Path.Join(root, SharedFolder, requirement.Name)))
        {
            var version = RuntimeRequirementDiscovery.ComparableVersion(Path.GetFileName(directory));
            if (!requirement.IsSatisfiedBy(version))
            {
                continue;
            }

            var entries = new List<DotNetLayoutEntry>(
                Relative(directory, $"{SharedFolder}/{requirement.Name}/{Path.GetFileName(directory)}"));

            if (IsCore(requirement.Name))
            {
                entries.AddRange(HostResolver(root, Path.GetFileName(directory)));
            }

            if (TryValidate(requirement.Name, entries) is { } valid)
            {
                yield return new DotNetLayoutSource(requirement.Name, version, valid);
            }
        }
    }

    /// <summary>Official runtime packs in a NuGet cache that satisfy a requirement.</summary>
    /// <remarks>
    /// A runtime pack's <c>runtimes/win-{arch}</c> content is a shared framework folder split in
    /// two: managed assemblies and the framework's own <c>deps.json</c> under <c>lib/{tfm}</c>,
    /// native components under <c>native</c>. Recombining them is the whole of the transformation —
    /// nothing is generated, and every file is the one Microsoft signed and published.
    /// </remarks>
    public static IEnumerable<DotNetLayoutSource> EnumeratePacks(
        DirectoryInfo packRoot,
        RuntimeFrameworkRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(packRoot);
        ArgumentNullException.ThrowIfNull(requirement);

        var runtimeIdentifier = RunArchHelper.ToRuntimeIdentifier(requirement.Architecture);

        foreach (var directory in SafeDirectories(packRoot.FullName))
        {
            var version = RuntimeRequirementDiscovery.ComparableVersion(Path.GetFileName(directory));
            if (!requirement.IsSatisfiedBy(version))
            {
                continue;
            }

            var runtimes = Path.Join(directory, "runtimes", runtimeIdentifier);
            var target = $"{SharedFolder}/{requirement.Name}/{Path.GetFileName(directory)}";

            var entries = new List<DotNetLayoutEntry>();

            foreach (var framework in SafeDirectories(Path.Join(runtimes, "lib")))
            {
                entries.AddRange(Relative(framework, target));
            }

            // hostfxr is the resolver a root exposes under host/fxr, not a framework assembly. A
            // copy left in the shared folder would be harmless but wrong, and the layout is easier
            // to reason about when it matches what the installer produces.
            entries.AddRange(Relative(Path.Join(runtimes, "native"), target)
                .Where(entry => !IsHostResolver(entry.EntryPath)));

            if (IsCore(requirement.Name))
            {
                var resolver = Path.Join(runtimes, "native", "hostfxr.dll");
                if (File.Exists(resolver))
                {
                    entries.Add(new DotNetLayoutEntry(
                        $"{HostFxrFolder}/{Path.GetFileName(directory)}/hostfxr.dll", resolver));
                }
            }

            if (TryValidate(requirement.Name, entries) is { } valid)
            {
                yield return new DotNetLayoutSource(requirement.Name, version, valid);
            }
        }
    }

    /// <summary>Writes a layout to a single archive, publishing it only once it is complete.</summary>
    /// <remarks>
    /// Staged under a temporary name and moved into place, so a run interrupted mid-write leaves a
    /// discardable temporary rather than a truncated archive a later run would treat as cached.
    /// </remarks>
    public static async Task BuildArchiveAsync(
        DotNetLayoutSource source,
        string archivePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        var staged = $"{archivePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var entry in source.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var created = archive.CreateEntry(entry.EntryPath, CompressionLevel.Fastest);

                    await using var content = File.OpenRead(entry.SourcePath);
                    await using var target = created.Open();
                    await content.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                }
            }

            File.Move(staged, archivePath, overwrite: true);
        }
        catch
        {
            AtomicFile.DiscardStaged(staged);
            throw;
        }
    }

    /// <summary>Returns the entries when they form a usable framework, or null when they do not.</summary>
    internal static IReadOnlyList<DotNetLayoutEntry>? TryValidate(
        string frameworkName,
        List<DotNetLayoutEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var names = entries
            .Select(entry => entry.EntryPath[(entry.EntryPath.LastIndexOf('/') + 1)..])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The framework's own dependency manifest. Without it the host has no assembly list to
        // resolve against, which is the difference between a framework folder and a pile of files.
        if (!names.Contains($"{frameworkName}.deps.json"))
        {
            return null;
        }

        if (RequiredFiles.TryGetValue(frameworkName, out var required) &&
            required.Any(file => !names.Contains(file)))
        {
            return null;
        }

        if (IsCore(frameworkName) && !entries.Any(entry => IsHostResolver(entry.EntryPath)))
        {
            return null;
        }

        return entries;
    }

    /// <summary>
    /// The host resolver a core framework layout must carry with it.
    /// </summary>
    /// <remarks>
    /// Matched to the framework version when the installation has one, and otherwise the newest
    /// available: hostfxr is forward-compatible with the frameworks it resolves, so a newer resolver
    /// beside an older framework works, while no resolver at all makes the root unusable.
    /// </remarks>
    private static List<DotNetLayoutEntry> HostResolver(string root, string frameworkVersion)
    {
        var exact = Path.Join(root, "host", "fxr", frameworkVersion);
        if (Directory.Exists(exact))
        {
            return Relative(exact, $"{HostFxrFolder}/{frameworkVersion}");
        }

        var newest = SafeDirectories(Path.Join(root, "host", "fxr"))
            .OrderByDescending(directory => RuntimeRequirementDiscovery.ComparableVersion(Path.GetFileName(directory)))
            .FirstOrDefault();

        return newest is null
            ? []
            : Relative(newest, $"{HostFxrFolder}/{Path.GetFileName(newest)}");
    }

    private static bool IsCore(string frameworkName) =>
        string.Equals(frameworkName, CoreFramework, StringComparison.OrdinalIgnoreCase);

    private static bool IsHostResolver(string entryPath) =>
        entryPath.EndsWith("/hostfxr.dll", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every file under a directory, addressed relative to a layout folder.</summary>
    private static List<DotNetLayoutEntry> Relative(string directory, string entryPrefix)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            return
            [
                .. Directory
                    .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    .Select(file => new DotNetLayoutEntry(
                        $"{entryPrefix}/{Path.GetRelativePath(directory, file).Replace('\\', '/')}",
                        file)),
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string[] SafeDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
