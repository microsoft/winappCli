// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>One requirement and the payload the host found for it, if any.</summary>
/// <param name="Requirement">The constraint, including any requirement derived from a runtime.</param>
/// <param name="Payload">Host payload to stage, or null when the guest must already have it.</param>
internal sealed record ResolvedRuntimePackage(RuntimePackageRequirement Requirement, RuntimePayload? Payload);

/// <summary>
/// Finds official framework package payloads for discovered requirements
/// (spec §"Runtime provisioning" steps 2 and 3).
/// </summary>
/// <remarks>
/// Behind an interface so the whole provisioning sequence can be exercised against a scripted
/// resolver: the interesting cases are "the cache already has it", "the cache does not and
/// acquisition succeeds", and "no payload exists at all", and only the first is reproducible from a
/// real machine on demand.
/// </remarks>
internal interface IRuntimePayloadResolver
{
    /// <summary>
    /// Resolves the complete package graph for <paramref name="requirements"/>.
    /// </summary>
    /// <remarks>
    /// Takes the whole set rather than one requirement at a time because resolving one can add
    /// others: a Windows App Runtime dependency names only the Framework package, and the runtime
    /// that satisfies it is the Framework, DDLM, Main, and Singleton together.
    /// </remarks>
    Task<IReadOnlyList<ResolvedRuntimePackage>> ResolveAsync(
        RuntimeRequirements requirements,
        DirectoryInfo projectRoot,
        TaskContext taskContext,
        CancellationToken cancellationToken);
}

/// <summary>
/// Resolves payloads from caches the host already has, acquiring through an existing official
/// winapp download path only when they cannot satisfy the constraint.
/// </summary>
/// <remarks>
/// Cache-first is not just an optimisation. The host has already restored and built the application
/// by the time this runs, so the runtime the app was compiled against is normally present; going to
/// the network first would make a warm, offline run slower and less reliable for no gain.
/// <para>
/// Acquisition is narrow by construction. Only two families are ever fetched — the Windows App SDK
/// runtime, through <see cref="IPackageInstallationService"/> so the feed, authentication, and cache
/// location are the ones every other winapp command uses, and the Microsoft VC runtime, through its
/// single official endpoint. Any other dependency a manifest happens to name is verified in the
/// guest, never downloaded.
/// </para>
/// </remarks>
internal sealed class RuntimePayloadResolver(
    INugetService nugetService,
    IPackageInstallationService packageInstallationService,
    IVcLibsPayloadAcquirer vcLibsAcquirer) : IRuntimePayloadResolver
{
    /// <summary>Identity prefixes of the app-facing Windows App Runtime Framework package.</summary>
    /// <remarks>
    /// Only the Framework is ever <em>declared</em> by an application manifest. Its siblings are
    /// discovered from the resolved runtime's own inventory, so this set does not need to name them.
    /// </remarks>
    private static readonly string[] WindowsAppRuntimePrefixes =
    [
        "Microsoft.WindowsAppRuntime.",
        "Microsoft.WinAppRuntime.",
        "MicrosoftCorporationII.WinAppRuntime.",
    ];

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ResolvedRuntimePackage>> ResolveAsync(
        RuntimeRequirements requirements,
        DirectoryInfo projectRoot,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(projectRoot);

        var resolved = new List<ResolvedRuntimePackage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var requirement in requirements.Packages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!seen.Add(requirement.Name))
            {
                continue;
            }

            if (IsWindowsAppRuntime(requirement.Name))
            {
                foreach (var entry in await ResolveWindowsAppRuntimeAsync(
                    requirement, projectRoot, taskContext, cancellationToken).ConfigureAwait(false))
                {
                    if (entry.Requirement.Derived && !seen.Add(entry.Requirement.Name))
                    {
                        continue;
                    }

                    resolved.Add(entry);
                }

                continue;
            }

            resolved.Add(new ResolvedRuntimePackage(
                requirement,
                await ResolveSingleAsync(requirement, projectRoot, taskContext, cancellationToken)
                    .ConfigureAwait(false)));
        }

        if (requirements.WindowsAppSdkVersion is { Length: > 0 } sdkVersion &&
            !requirements.Packages.Any(requirement => IsWindowsAppRuntime(requirement.Name)))
        {
            foreach (var entry in await ResolveWindowsAppRuntimeAsync(
                sdkVersion,
                requirements.Architecture,
                projectRoot,
                taskContext,
                cancellationToken).ConfigureAwait(false))
            {
                if (seen.Add(entry.Requirement.Name))
                {
                    resolved.Add(entry);
                }
            }
        }

        return resolved;
    }

    /// <summary>Resolves the exact runtime restored for an unpackaged Windows App SDK build.</summary>
    private async Task<List<ResolvedRuntimePackage>> ResolveWindowsAppRuntimeAsync(
        string sdkVersion,
        string architecture,
        DirectoryInfo projectRoot,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        var inventory = FindRuntimeInventory(sdkVersion, architecture);
        if (inventory is null)
        {
            await packageInstallationService
                .EnsurePackageAsync(
                    projectRoot,
                    BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE,
                    taskContext,
                    version: sdkVersion,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            inventory = FindRuntimeInventory(sdkVersion, architecture);
        }

        if (inventory is null)
        {
            var band = RuntimeBand(sdkVersion);
            taskContext.AddDebugMessage(
                $"{UiSymbols.Note} No cached Windows App Runtime inventory matches Microsoft.WindowsAppSDK {sdkVersion}; it will be verified in the guest instead.");

            return
            [
                new ResolvedRuntimePackage(
                    new RuntimePackageRequirement
                    {
                        Name = $"Microsoft.WindowsAppRuntime.{band}",
                        MinVersion = "0.0.0.0",
                        Architecture = architecture,
                        Derived = true,
                    },
                    null),
            ];
        }

        return
        [
            .. new[] { inventory.Framework }
                .Concat(inventory.Siblings)
                .Select(payload => new ResolvedRuntimePackage(
                    new RuntimePackageRequirement
                    {
                        Name = payload.PackageName,
                        MinVersion = payload.Version,
                        Architecture = payload.Architecture,
                        Publisher = payload.Publisher,
                        Derived = true,
                    },
                    payload)),
        ];
    }

    /// <summary>
    /// Resolves the <em>complete</em> runtime inventory that satisfies one Windows App Runtime
    /// dependency.
    /// </summary>
    /// <remarks>
    /// A manifest declares only the Framework, but installing only the Framework produces a guest
    /// where a WinUI application still fails to start: the DDLM is what lets an unpackaged process
    /// find the runtime, and Main and Singleton carry the background infrastructure the runtime's
    /// own APIs depend on. The whole cached inventory directory is therefore staged and installed,
    /// and every package derived from it is verified afterwards rather than assumed.
    /// </remarks>
    private async Task<List<ResolvedRuntimePackage>> ResolveWindowsAppRuntimeAsync(
        RuntimePackageRequirement declared,
        DirectoryInfo projectRoot,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        var inventory = FindRuntimeInventory(declared, cancellationToken);

        if (inventory is null)
        {
            // Version deliberately left unset: the workspace's pinned version wins when there is
            // one, and otherwise the newest is fetched — the same resolution `winapp restore`
            // performs, not a second download path.
            await packageInstallationService
                .EnsurePackageAsync(
                    projectRoot,
                    BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE,
                    taskContext,
                    version: null,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            inventory = FindRuntimeInventory(declared, cancellationToken);
        }

        if (inventory is null)
        {
            taskContext.AddDebugMessage(
                $"{UiSymbols.Note} No cached Windows App Runtime satisfies '{declared.Name}'; it will be verified in the guest instead.");

            return [new ResolvedRuntimePackage(declared, null)];
        }

        var resolved = new List<ResolvedRuntimePackage>
        {
            new(declared, inventory.Framework),
        };

        foreach (var sibling in inventory.Siblings)
        {
            // The derived constraint is the sibling's own identity, read from its own manifest. The
            // application's MinVersion says nothing about it — the Singleton's version is not even
            // in the same numbering as the Framework's — so requiring anything else would either
            // fail a good runtime or accept a mismatched one.
            resolved.Add(new ResolvedRuntimePackage(
                new RuntimePackageRequirement
                {
                    Name = sibling.PackageName,
                    MinVersion = sibling.Version,
                    Architecture = sibling.Architecture,
                    Publisher = sibling.Publisher,
                    Derived = true,
                },
                sibling));
        }

        return resolved;
    }

    /// <summary>Every payload in the cached runtime directory that satisfies the dependency.</summary>
    private sealed record RuntimeInventory(RuntimePayload Framework, IReadOnlyList<RuntimePayload> Siblings);

    /// <summary>
    /// Finds the cached runtime whose Framework package satisfies <paramref name="declared"/>, and
    /// returns everything beside it.
    /// </summary>
    /// <remarks>
    /// Candidate directories are ordered by the Framework version they contain, ascending, and the
    /// first satisfying one wins — so a guest gets the runtime the project actually restored rather
    /// than the newest one that happens to be on the developer's machine. Installing a much newer
    /// runtime than the app was built against is not a downgrade, but it is a difference between the
    /// host's local run and the guest's, and differences like that are what make a Sandbox failure
    /// hard to reproduce.
    /// </remarks>
    private RuntimeInventory? FindRuntimeInventory(
        RuntimePackageRequirement declared,
        CancellationToken cancellationToken)
    {
        RuntimeInventory? best = null;
        Version? bestVersion = null;

        foreach (var directory in EnumerateRuntimeDirectories(declared.Architecture))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var payloads = SafePackageFiles(directory)
                .Select(RuntimePayloadIdentity.TryRead)
                .OfType<RuntimePayload>()
                .ToList();

            var framework = payloads.FirstOrDefault(
                payload => RuntimePayloadIdentity.Satisfies(payload, declared));

            if (framework is null)
            {
                continue;
            }

            var version = RuntimeRequirementDiscovery.ComparableVersion(framework.Version);
            if (bestVersion is not null && version >= bestVersion)
            {
                continue;
            }

            best = new RuntimeInventory(
                framework,
                [.. payloads.Where(payload => !ReferenceEquals(payload, framework))]);

            bestVersion = version;
        }

        return best;
    }

    private RuntimeInventory? FindRuntimeInventory(string sdkVersion, string architecture)
    {
        var cache = nugetService.GetNuGetGlobalPackagesDir();
        foreach (var packageId in (string[])
            [BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE, BuildToolsService.WINAPP_SDK_PACKAGE])
        {
            var directory = new DirectoryInfo(Path.Join(
                cache.FullName,
                packageId.ToLowerInvariant(),
                sdkVersion,
                "tools",
                "MSIX",
                $"win10-{architecture}"));
            if (!directory.Exists)
            {
                continue;
            }

            var payloads = SafePackageFiles(directory)
                .Select(RuntimePayloadIdentity.TryRead)
                .OfType<RuntimePayload>()
                .Where(payload =>
                    string.Equals(payload.Architecture, architecture, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        payload.Architecture,
                        RuntimePackageRequirement.NeutralArchitecture,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
            var framework = payloads.FirstOrDefault(payload =>
                payload.PackageName.StartsWith(
                    "Microsoft.WindowsAppRuntime.",
                    StringComparison.OrdinalIgnoreCase) &&
                !payload.PackageName.Contains(
                    WindowsAppRuntimeService.WinAppRuntimeCbsInfix,
                    StringComparison.OrdinalIgnoreCase));
            if (framework is not null)
            {
                return new RuntimeInventory(
                    framework,
                    [.. payloads.Where(payload => !ReferenceEquals(payload, framework))]);
            }
        }

        return null;
    }

    private static string RuntimeBand(string sdkVersion)
    {
        var parts = sdkVersion.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : sdkVersion;
    }

    /// <summary>Resolves a dependency that is not part of a Windows App Runtime inventory.</summary>
    private async Task<RuntimePayload?> ResolveSingleAsync(
        RuntimePackageRequirement requirement,
        DirectoryInfo projectRoot,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        var cached = EnumerateRuntimeDirectories(requirement.Architecture)
            .SelectMany(SafePackageFiles)
            .Select(RuntimePayloadIdentity.TryRead)
            .OfType<RuntimePayload>()
            .Where(payload => RuntimePayloadIdentity.Satisfies(payload, requirement))
            .OrderBy(payload => RuntimeRequirementDiscovery.ComparableVersion(payload.Version))
            .FirstOrDefault();

        if (cached is not null)
        {
            return cached;
        }

        // The one other family with an official endpoint. Everything else falls through to guest
        // verification, which is what keeps this from becoming "download whatever a manifest names".
        var acquired = await vcLibsAcquirer
            .TryAcquireAsync(requirement, projectRoot, taskContext, cancellationToken)
            .ConfigureAwait(false);

        if (acquired is null)
        {
            taskContext.AddDebugMessage(
                $"{UiSymbols.Note} No cached payload for '{requirement.Name}'; it will be verified in the guest instead.");
        }

        return acquired;
    }

    private static bool IsWindowsAppRuntime(string packageName) =>
        WindowsAppRuntimePrefixes.Any(prefix => packageName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every cached Windows App SDK runtime inventory directory for one architecture.</summary>
    private IEnumerable<DirectoryInfo> EnumerateRuntimeDirectories(string architecture)
    {
        var cache = nugetService.GetNuGetGlobalPackagesDir();
        if (!cache.Exists)
        {
            yield break;
        }

        string[] packageIds =
        [
            BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE,
            BuildToolsService.WINAPP_SDK_PACKAGE,
        ];

        foreach (var packageId in packageIds)
        {
            var packageRoot = new DirectoryInfo(Path.Join(cache.FullName, packageId.ToLowerInvariant()));
            if (!packageRoot.Exists)
            {
                continue;
            }

            foreach (var versionDirectory in SafeDirectories(packageRoot))
            {
                var msixDirectory = new DirectoryInfo(
                    Path.Join(versionDirectory.FullName, "tools", "MSIX", $"win10-{architecture}"));

                if (msixDirectory.Exists)
                {
                    yield return msixDirectory;
                }
            }
        }
    }

    private static DirectoryInfo[] SafeDirectories(DirectoryInfo directory)
    {
        try
        {
            return directory.GetDirectories();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Every package file in a directory, ignoring one winapp cannot enumerate.</summary>
    /// <remarks>
    /// The <c>msix.inventory</c> file beside them is deliberately not consulted. Its recorded
    /// identities are known to differ from what the packages contain — the DDLM's name and the
    /// Singleton's version both do — and identity is the whole basis for deciding whether a
    /// requirement is met, so it is read from each payload's own manifest instead.
    /// </remarks>
    internal static FileInfo[] SafePackageFiles(DirectoryInfo directory)
    {
        try
        {
            return
            [
                .. directory
                    .GetFiles()
                    .Where(file =>
                        file.Extension.Equals(".msix", StringComparison.OrdinalIgnoreCase) ||
                        file.Extension.Equals(".appx", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase),
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
