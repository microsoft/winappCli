// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>What installing one shared framework in the guest achieved.</summary>
/// <param name="Installed">True when this pass unpacked a layout rather than finding one present.</param>
/// <param name="PresentVersion">Highest satisfying version resolvable afterwards, when any.</param>
/// <param name="Detail">Why the requirement is unmet, when it is.</param>
internal sealed record DotNetInstallOutcome(bool Installed, Version? PresentVersion, string? Detail);

/// <summary>
/// Installs and verifies shared .NET frameworks inside an execution target, in a root the guest user
/// owns (spec §"Runtime provisioning" steps 5 and 6).
/// </summary>
/// <remarks>
/// Per-user and side-by-side, deliberately. A machine-wide <c>dotnet</c> layout would need elevation
/// winapp does not otherwise take in a guest, and would put winapp in the position of servicing an
/// installation other software depends on. A managed root under the guest's own profile is enough
/// for an apphost — <c>DOTNET_ROOT</c> is the first thing it consults — and it can be added to
/// without ever replacing or removing anything.
/// <para>
/// Every install is atomic in the only sense that matters here: content is unpacked to a disposable
/// staging folder, checked, and then moved into its final versioned path in one operation. A crash
/// at any point leaves either the previous state or the complete new one, never a half-populated
/// framework directory that would look installed and fail to load.
/// </para>
/// </remarks>
internal static class DotNetRuntimeInstaller
{
    /// <summary>Folder inside the managed .NET root used for in-flight extraction.</summary>
    internal const string StagingFolderName = ".staging";

    /// <summary>
    /// Ensures one framework requirement is satisfied, installing the staged layout if it is not.
    /// </summary>
    /// <param name="requirement">Constraint to satisfy.</param>
    /// <param name="managedRoot">The .NET root winapp owns in this guest.</param>
    /// <param name="stagingDirectory">Guest folder the host staged payloads into.</param>
    /// <param name="probeRoots">Every .NET root the apphost would consult, managed root included.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static DotNetInstallOutcome Ensure(
        RuntimeFrameworkRequirement requirement,
        string managedRoot,
        string stagingDirectory,
        IEnumerable<string> probeRoots,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        var present = FindSatisfying(requirement, probeRoots);
        if (present is not null)
        {
            // Already resolvable. Unpacking over it would at best be wasted time and at worst
            // disturb a framework another application in this guest is running on.
            return new DotNetInstallOutcome(Installed: false, present, Detail: null);
        }

        if (requirement.PayloadFile is not { } payloadFile)
        {
            return new DotNetInstallOutcome(false, null, "no runtime layout was available to install");
        }

        string archivePath;
        try
        {
            archivePath = TargetPathSafety.CombineInsideRoot(stagingDirectory, payloadFile);
        }
        catch (Abstractions.ExecutionTargetException ex)
        {
            return new DotNetInstallOutcome(false, null, ex.Message);
        }

        if (!File.Exists(archivePath))
        {
            return new DotNetInstallOutcome(false, null, "the staged runtime layout is missing");
        }

        try
        {
            Install(archivePath, managedRoot, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
                                    or UnauthorizedAccessException
                                    or InvalidDataException
                                    or Abstractions.ExecutionTargetException)
        {
            return new DotNetInstallOutcome(false, FindSatisfying(requirement, probeRoots), ex.Message);
        }

        // Re-probed rather than assumed: an extraction that reported success but produced a version
        // that does not satisfy the constraint is a real outcome, and the report has to say so.
        return new DotNetInstallOutcome(true, FindSatisfying(requirement, probeRoots), Detail: null);
    }

    /// <summary>The environment variable an apphost consults first to find a .NET root.</summary>
    internal const string DiscoveryVariable = "DOTNET_ROOT";

    /// <summary>
    /// Makes the managed root discoverable to processes winapp did not launch.
    /// </summary>
    /// <remarks>
    /// The authoritative path is the launched process's own environment, which the host sets from
    /// the report — that is what makes <c>run --on sandbox</c> work regardless of what the guest's user
    /// environment says. This is the additional, durable half: a per-user <c>DOTNET_ROOT</c> so an
    /// app started by hand or through <c>sandbox exec</c> resolves the same runtimes.
    /// <para>
    /// Per-user, and never overwritten. An existing value pointing somewhere else belongs to
    /// something already installed in the guest, and clobbering it would break that instead of
    /// helping this. The machine-wide registration the .NET installer writes is deliberately not
    /// touched: it needs elevation winapp does not take in a guest, and it would make winapp the
    /// servicer of an installation other software depends on.
    /// </para>
    /// </remarks>
    /// <returns>True when the variable now names <paramref name="managedRoot"/>.</returns>
    public static bool TryConfigureDiscovery(string managedRoot)
    {
        try
        {
            var existing = Environment.GetEnvironmentVariable(DiscoveryVariable, EnvironmentVariableTarget.User);

            if (string.Equals(existing, managedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(existing))
            {
                return false;
            }

            Environment.SetEnvironmentVariable(
                DiscoveryVariable, managedRoot, EnvironmentVariableTarget.User);

            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            // Not being able to write the user environment costs nothing that matters: the launch
            // carries the same value in the child's own environment, which is what the app winapp
            // starts actually reads.
            return false;
        }
    }

    /// <summary>The highest installed version that satisfies a requirement, across every root.</summary>
    /// <remarks>
    /// The guest's own installation counts. A Sandbox that already has the framework needs nothing
    /// from winapp, and installing anyway would move tens of megabytes to no effect.
    /// </remarks>
    internal static Version? FindSatisfying(
        RuntimeFrameworkRequirement requirement,
        IEnumerable<string> probeRoots)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(probeRoots);

        return probeRoots
            .SelectMany(root => DotNetLayout.InstalledVersions(root, requirement.Name))
            .Where(requirement.IsSatisfiedBy)
            .OrderByDescending(version => version)
            .FirstOrDefault();
    }

    /// <summary>
    /// Unpacks a layout archive into the managed root without disturbing what is already there.
    /// </summary>
    /// <remarks>
    /// Each versioned folder the archive carries is moved into place as a unit, and an existing one
    /// is left exactly as it is. That is what makes the operation both side-by-side and
    /// non-destructive: a guest already running on a version this layout also contains keeps the
    /// files it has open, and nothing is ever downgraded because nothing is ever replaced.
    /// </remarks>
    private static void Install(string archivePath, string managedRoot, CancellationToken cancellationToken)
    {
        var staging = Path.Join(managedRoot, StagingFolderName, Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(staging);
            Extract(archivePath, staging, cancellationToken);

            foreach (var (segments, stagedFolder) in VersionedFolders(staging))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var destination = TargetPathSafety.CombineInsideRoot(managedRoot, segments);

                if (Directory.Exists(destination))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                try
                {
                    Directory.Move(stagedFolder, destination);
                }
                catch (IOException) when (Directory.Exists(destination))
                {
                    // Another winapp process in this guest published the same version first. Its
                    // copy is the same official content, so losing the race is success.
                }
            }
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>
    /// Extracts an archive, refusing any entry that would land outside the destination.
    /// </summary>
    /// <remarks>
    /// The archive was built by the host and arrived over the verified file channel, so this is
    /// defence in depth rather than the primary control — but an extractor that can be talked into
    /// writing outside its root is worth never having, and the check costs one path comparison per
    /// entry.
    /// </remarks>
    private static void Extract(string archivePath, string destination, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Join(destination, entry.FullName));

            if (!TargetPathSafety.IsInsideRoot(Path.GetFullPath(destination), target))
            {
                throw new InvalidDataException(
                    $"The runtime layout contains an entry that would be written outside it: '{entry.FullName}'.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>
    /// The versioned folders a layout publishes, as paths relative to the .NET root.
    /// </summary>
    /// <remarks>
    /// A layout contains <c>shared/{framework}/{version}</c> and <c>host/fxr/{version}</c>. Those
    /// are the units Windows and the .NET host treat as indivisible, so they are also the units that
    /// are moved into place — moving individual files would let a crash leave a framework directory
    /// that exists but cannot load.
    /// </remarks>
    private static IEnumerable<(string[] Segments, string StagedFolder)> VersionedFolders(string staging)
    {
        foreach (var group in SafeDirectories(Path.Join(staging, DotNetLayout.SharedFolder)))
        {
            foreach (var version in SafeDirectories(group))
            {
                yield return (
                    [DotNetLayout.SharedFolder, Path.GetFileName(group), Path.GetFileName(version)],
                    version);
            }
        }

        foreach (var version in SafeDirectories(Path.Join(staging, "host", "fxr")))
        {
            yield return (["host", "fxr", Path.GetFileName(version)], version);
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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftover staging is disposable by construction and must never mask a real result.
        }
    }
}
