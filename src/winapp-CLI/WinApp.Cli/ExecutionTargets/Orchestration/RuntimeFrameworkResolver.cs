// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>A portable .NET layout the host built and can stage.</summary>
/// <param name="Archive">Host path of the cached layout archive.</param>
/// <param name="Name">Shared framework the layout provides.</param>
/// <param name="Version">Exact framework version inside it.</param>
/// <param name="Architecture">Architecture the layout is for.</param>
internal sealed record RuntimeFrameworkPayload(
    FileInfo Archive,
    string Name,
    string Version,
    string Architecture);

/// <summary>
/// Resolves an official .NET shared framework into a portable layout the guest can unpack
/// (spec §"Runtime provisioning" steps 2 and 3).
/// </summary>
/// <remarks>
/// Behind an interface for the same reason payload resolution is: whether a given runtime pack is
/// on a developer's machine is not something a test can arrange, and the sequence that consumes the
/// result has to be exercisable either way.
/// </remarks>
internal interface IRuntimeFrameworkResolver
{
    /// <summary>
    /// Returns a portable layout satisfying <paramref name="requirement"/>, or null when none could
    /// be assembled from an official source.
    /// </summary>
    Task<RuntimeFrameworkPayload?> ResolveAsync(
        RuntimeFrameworkRequirement requirement,
        DirectoryInfo projectRoot,
        TaskContext taskContext,
        CancellationToken cancellationToken);
}

/// <summary>
/// Builds a portable .NET layout from official payloads the host already has, acquiring the matching
/// runtime pack through the existing NuGet path only when it has none.
/// </summary>
/// <remarks>
/// Two official sources, in preference order. An installation already on the host is used first,
/// because it is a complete, shipped shared-framework folder — nothing is assembled or inferred.
/// Otherwise the <c>Microsoft.NETCore.App.Runtime.win-{arch}</c> and
/// <c>Microsoft.WindowsDesktop.App.Runtime.win-{arch}</c> packs are used: they are the same signed
/// payloads the .NET installer publishes, and their <c>runtimes/win-{arch}</c> content is a shared
/// framework folder in all but name.
/// <para>
/// Only those two known package families are ever turned into a layout. This is not a general
/// "download and lay out any NuGet package" facility, and the validation below is what keeps it from
/// becoming one: a layout that does not contain the files a working framework must have is
/// discarded rather than staged.
/// </para>
/// <para>
/// The result is cached on the host by identity, so the tens of megabytes are assembled once and
/// every later run stages an archive it already has.
/// </para>
/// </remarks>
internal sealed class RuntimeFrameworkResolver(
    INugetService nugetService,
    IPackageInstallationService packageInstallationService,
    IWinappDirectoryService winappDirectoryService) : IRuntimeFrameworkResolver
{
    /// <summary>Folder inside the shared winapp cache that built layouts are kept in.</summary>
    internal const string CacheFolderName = "dotnet-layouts";

    /// <summary>The runtime pack that carries each shared framework winapp will provision.</summary>
    /// <remarks>
    /// A closed map, not a naming convention. Provisioning a framework means knowing what a valid
    /// layout of it looks like, and that is a statement about specific known packages rather than
    /// about anything whose id happens to end in <c>.Runtime.win-x64</c>.
    /// </remarks>
    private static readonly Dictionary<string, string> RuntimePacks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.NETCore.App"] = "Microsoft.NETCore.App.Runtime.win-{0}",
        ["Microsoft.WindowsDesktop.App"] = "Microsoft.WindowsDesktop.App.Runtime.win-{0}",
    };

    /// <summary>Host .NET installation roots probed for an already-installed framework.</summary>
    internal Func<IEnumerable<string>> HostDotNetRoots { get; set; } = DotNetLayout.DefaultRoots;

    /// <inheritdoc/>
    public async Task<RuntimeFrameworkPayload?> ResolveAsync(
        RuntimeFrameworkRequirement requirement,
        DirectoryInfo projectRoot,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(projectRoot);
        ArgumentNullException.ThrowIfNull(taskContext);

        if (!RuntimePacks.ContainsKey(requirement.Name))
        {
            // Not a framework winapp knows how to lay out. It is still verified in the guest, so an
            // application that needs one is told exactly what is missing rather than left to fail at
            // startup.
            return null;
        }

        var source = FindHostInstallation(requirement)
            ?? FindRuntimePack(requirement)
            ?? await AcquireRuntimePackAsync(requirement, projectRoot, taskContext, cancellationToken)
                .ConfigureAwait(false);

        if (source is null)
        {
            taskContext.AddDebugMessage(
                $"{UiSymbols.Note} No official {requirement.Name} {requirement.MinVersion} payload for {requirement.Architecture} is available on this machine.");

            return null;
        }

        return await BuildAsync(requirement, source, taskContext, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds a complete shared framework in a .NET installation on the host.
    /// </summary>
    /// <remarks>
    /// Preferred over a runtime pack because it is exactly what the .NET installer produced: the
    /// framework's own <c>deps.json</c>, <c>runtimeconfig.json</c>, and <c>.version</c> files are
    /// already beside the assemblies, so the layout is copied rather than assembled.
    /// <para>
    /// The host's own architecture is irrelevant here — only the requirement's is. A machine with an
    /// x64 installation cannot supply the x86 framework an x86 application needs, and the
    /// architecture-specific roots are what express that.
    /// </para>
    /// </remarks>
    private DotNetLayoutSource? FindHostInstallation(RuntimeFrameworkRequirement requirement) =>
        HostDotNetRoots()
            .Where(root => DotNetLayout.MatchesArchitecture(root, requirement.Architecture))
            .SelectMany(root => DotNetLayout.EnumerateInstalled(root, requirement))
            .OrderBy(candidate => candidate.Version)
            .FirstOrDefault();

    /// <summary>Finds a cached official runtime pack that satisfies the requirement.</summary>
    private DotNetLayoutSource? FindRuntimePack(RuntimeFrameworkRequirement requirement)
    {
        var packRoot = new DirectoryInfo(Path.Join(
            nugetService.GetNuGetGlobalPackagesDir().FullName,
            PackId(requirement).ToLowerInvariant()));

        return DotNetLayout.EnumeratePacks(packRoot, requirement)
            .OrderBy(candidate => candidate.Version)
            .FirstOrDefault();
    }

    /// <summary>
    /// Restores the exact runtime pack the application needs, then looks again.
    /// </summary>
    /// <remarks>
    /// Through the same installation service every other winapp command uses, so the feed,
    /// authentication, and cache location are the configured ones rather than a second download path
    /// with its own trust story. The exact required version is asked for: a runtime pack is not a
    /// tool winapp is choosing, it is the runtime a build already resolved against.
    /// </remarks>
    private async Task<DotNetLayoutSource?> AcquireRuntimePackAsync(
        RuntimeFrameworkRequirement requirement,
        DirectoryInfo projectRoot,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        try
        {
            await packageInstallationService
                .EnsurePackageAsync(
                    projectRoot,
                    PackId(requirement),
                    taskContext,
                    version: requirement.MinVersion,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Reported, not thrown. The guest may already have the framework, and only an
            // unsatisfied guest verification is grounds for refusing to launch.
            taskContext.AddDebugMessage(
                $"{UiSymbols.Note} The {requirement.Name} runtime pack could not be restored: {ex.Message}");

            return null;
        }

        return FindRuntimePack(requirement);
    }

    /// <summary>
    /// Assembles and caches the portable layout, unless an identical one is already cached.
    /// </summary>
    private async Task<RuntimeFrameworkPayload?> BuildAsync(
        RuntimeFrameworkRequirement requirement,
        DotNetLayoutSource source,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        var cache = new DirectoryInfo(Path.Join(
            winappDirectoryService.GetGlobalWinappDirectory().FullName, "cache", CacheFolderName));

        cache.Create();

        var archive = new FileInfo(Path.Join(
            cache.FullName,
            TargetPathSafety.EnsureSafeSegment(
                $"{requirement.Name}_{source.Version}_{requirement.Architecture}.zip")));

        if (!archive.Exists)
        {
            try
            {
                await DotNetLayout.BuildArchiveAsync(source, archive.FullName, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                taskContext.AddDebugMessage(
                    $"{UiSymbols.Note} The {requirement.Name} layout could not be assembled: {ex.Message}");

                return null;
            }

            archive.Refresh();
        }

        return new RuntimeFrameworkPayload(
            archive,
            requirement.Name,
            source.Version.ToString(),
            requirement.Architecture);
    }

    private static string PackId(RuntimeFrameworkRequirement requirement) =>
        string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            RuntimePacks[requirement.Name],
            requirement.Architecture);
}
