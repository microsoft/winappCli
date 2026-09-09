// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>
/// Resolves the Microsoft VC runtime framework packages an application declares, from caches the
/// host already has or from their one official endpoint.
/// </summary>
/// <remarks>
/// Separated from the general resolver because the trust argument is different. Everything else is
/// resolved from a package a build already restored; this is the one family winapp will fetch from
/// a URL, so it is deliberately its own narrow, allowlisted, identity-validated path rather than a
/// capability the resolver has for any dependency a manifest happens to name.
/// </remarks>
internal interface IVcLibsPayloadAcquirer
{
    /// <summary>
    /// Returns a payload satisfying <paramref name="requirement"/>, or null when it is not a known
    /// VC framework package or none could be obtained.
    /// </summary>
    Task<RuntimePayload?> TryAcquireAsync(
        RuntimePackageRequirement requirement,
        DirectoryInfo projectRoot,
        TaskContext taskContext,
        CancellationToken cancellationToken);
}

/// <summary>
/// Finds a VC runtime framework package in the Windows SDK and winapp caches, and falls back to the
/// single official Microsoft endpoint for the one package that ships nowhere else.
/// </summary>
/// <remarks>
/// The desktop VC runtime (<c>Microsoft.VCLibs.140.00.UWPDesktop</c>) is not carried by any NuGet
/// package a build restores and is not in the Windows SDK, which ships only the UWP variant. Without
/// it, a packaged desktop application that declares it simply cannot register in a fresh guest, and
/// no amount of verification improves that outcome. So it is fetched — from one hard-coded official
/// address, for one allowlisted identity, and only after the bytes prove they are that package.
/// <para>
/// Validation is on the manifest inside the downloaded package, not on the URL that produced it: the
/// identity name, architecture, version, and publisher all have to match what was asked for before a
/// single byte is cached or staged. A redirect that ended somewhere unexpected therefore fails
/// closed rather than putting an unknown package into a guest.
/// </para>
/// </remarks>
internal sealed class VcLibsPayloadAcquirer(IWinappDirectoryService winappDirectoryService) : IVcLibsPayloadAcquirer
{
    /// <summary>
    /// Package identities this acquirer will fetch, and the official address each comes from.
    /// </summary>
    /// <remarks>
    /// A closed list, keyed by identity rather than by prefix. "Anything starting with
    /// Microsoft.VCLibs" would be a rule about names; this is a statement about two packages winapp
    /// knows exist, knows where Microsoft publishes, and knows how to recognize once fetched.
    /// </remarks>
    private static readonly Dictionary<string, string> OfficialSources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.VCLibs.140.00.UWPDesktop"] = "https://aka.ms/Microsoft.VCLibs.{0}.14.00.Desktop.appx",
    };

    /// <summary>Publisher every package in <see cref="OfficialSources"/> must carry.</summary>
    internal const string MicrosoftPublisher =
        "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";

    /// <summary>Folder inside the shared winapp cache that acquired payloads are kept in.</summary>
    internal const string CacheFolderName = "framework-packages";

    /// <summary>Largest official VC framework payload accepted from the network.</summary>
    internal const int MaxPayloadBytes = 128 * 1024 * 1024;

    /// <summary>
    /// Downloads one allowlisted address, seamed so acquisition can be exercised without a network.
    /// </summary>
    internal Func<string, CancellationToken, Task<byte[]>> Downloader { get; set; } = DownloadAsync;

    /// <summary>
    /// Directories searched for an already-present official copy, seamed for the same reason.
    /// </summary>
    internal Func<IEnumerable<DirectoryInfo>> CacheDirectories { get; set; } = DefaultCacheDirectories;

    /// <summary>
    /// Proves a staged payload really is Microsoft-signed, seamed so both verdicts are testable
    /// without a signed fixture.
    /// </summary>
    /// <remarks>
    /// Matches the seam <see cref="Services.WinDbgJsProviderAcquirer"/> uses for the same job. The
    /// default is the real fail-closed Authenticode gate; a test substitutes a verdict.
    /// </remarks>
    internal Func<string, bool> SignatureVerifier { get; set; } =
        path => AuthenticodeVerifier.IsTrustedMicrosoftSigned(path, NullLogger.Instance);

    /// <inheritdoc/>
    public async Task<RuntimePayload?> TryAcquireAsync(
        RuntimePackageRequirement requirement,
        DirectoryInfo projectRoot,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(projectRoot);
        ArgumentNullException.ThrowIfNull(taskContext);

        var hostCache = HostCacheDirectory();

        // Host caches first, including winapp's own: a payload fetched by a previous run is the same
        // official bytes, and re-downloading tens of megabytes on every run would make an offline
        // Sandbox session fail for no reason.
        if (FindInCaches(requirement, projectRoot, hostCache) is { } cached)
        {
            return cached;
        }

        if (!OfficialSources.TryGetValue(requirement.Name, out var addressTemplate))
        {
            return null;
        }

        if (RunArchHelper.NormalizeArchitecture(requirement.Architecture) is not { } architecture)
        {
            return null;
        }

        var address = string.Format(System.Globalization.CultureInfo.InvariantCulture, addressTemplate, architecture);

        byte[] payload;
        try
        {
            taskContext.AddDebugMessage($"{UiSymbols.Package} Acquiring {requirement.Name} for {architecture}...");
            payload = await Downloader(address, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            // A dependency that could not be fetched is not a failure here. The guest may already
            // have it, and only an unsatisfied guest verification is grounds for refusing to launch.
            taskContext.AddDebugMessage(
                $"{UiSymbols.Note} {requirement.Name} could not be downloaded: {ex.Message}");

            return null;
        }

        return await PublishAsync(requirement, payload, hostCache, taskContext, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates downloaded bytes and, only if they prove to be Microsoft's copy of the package that
    /// was asked for, caches them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two gates, in this order, on a staged file that is never published until both pass.
    /// </para>
    /// <para>
    /// <b>First, the signature.</b> Everything the identity gate reads — name, version,
    /// architecture, publisher — comes from <c>AppxManifest.xml</c> <em>inside</em> the downloaded
    /// zip, and anyone who can put bytes in front of this code can write those strings to say
    /// whatever the check wants to see. On their own they prove the package claims to be
    /// Microsoft's, not that it is. Authenticode is the only part of the payload that cannot be
    /// forged that way, so it is checked first and a failure means nothing is published: the poisoned
    /// bytes never become a host cache entry that later runs, and later guests, would trust without
    /// re-deriving anything.
    /// </para>
    /// <para>
    /// <b>Then the identity.</b> Kept as a second gate rather than replaced by the signature: a
    /// genuinely Microsoft-signed package that is the wrong package, wrong architecture, or wrong
    /// version is still the wrong thing to stage into a guest.
    /// </para>
    /// </remarks>
    private async Task<RuntimePayload?> PublishAsync(
        RuntimePackageRequirement requirement,
        byte[] payload,
        DirectoryInfo hostCache,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        hostCache.Create();

        var destination = Path.Join(hostCache.FullName, StagedName(requirement));
        var staged = await AtomicFile.WriteStagedAsync(destination, payload, cancellationToken).ConfigureAwait(false);

        if (!SignatureVerifier(staged))
        {
            AtomicFile.DiscardStaged(staged);

            taskContext.AddDebugMessage(
                $"{UiSymbols.Note} The downloaded {requirement.Name} package is not validly signed by Microsoft and was discarded.");

            return null;
        }

        var identity = RuntimePayloadIdentity.TryRead(new FileInfo(staged));

        if (identity is null || !IsOfficialAndSatisfying(identity, requirement))
        {
            AtomicFile.DiscardStaged(staged);

            taskContext.AddDebugMessage(
                $"{UiSymbols.Note} The downloaded {requirement.Name} package did not match the required identity and was discarded.");

            return null;
        }

        AtomicFile.Publish(staged, destination);

        return identity with { File = new FileInfo(destination) };
    }

    /// <summary>Finds a satisfying official copy in any searched cache.</summary>
    private RuntimePayload? FindInCaches(
        RuntimePackageRequirement requirement,
        DirectoryInfo projectRoot,
        DirectoryInfo hostCache) =>
        CacheDirectories()
            .Prepend(hostCache)
            .Concat(ProjectCacheDirectories(projectRoot))
            .Where(directory => directory.Exists)
            .SelectMany(RuntimePayloadResolver.SafePackageFiles)
            .Select(RuntimePayloadIdentity.TryRead)
            .OfType<RuntimePayload>()
            .Where(payload => IsOfficialAndSatisfying(payload, requirement))
            .OrderBy(payload => RuntimeRequirementDiscovery.ComparableVersion(payload.Version))
            .FirstOrDefault();

    /// <summary>Known project-local folders that can carry framework dependency payloads.</summary>
    private static IEnumerable<DirectoryInfo> ProjectCacheDirectories(DirectoryInfo projectRoot)
    {
        yield return projectRoot;

        foreach (var name in (string[])["Dependencies", "AppPackages", ".winapp"])
        {
            var root = new DirectoryInfo(Path.Join(projectRoot.FullName, name));
            if (!root.Exists)
            {
                continue;
            }

            yield return root;

            IEnumerable<string> descendants;
            try
            {
                descendants = Directory.EnumerateDirectories(
                    root.FullName,
                    "*",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                    });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var descendant in descendants)
            {
                yield return new DirectoryInfo(descendant);
            }
        }
    }

    /// <summary>
    /// Whether a candidate is both the required package and genuinely Microsoft's.
    /// </summary>
    /// <remarks>
    /// The publisher check is in addition to, not instead of, the requirement's own. A manifest that
    /// declared no publisher would otherwise accept a same-named package from anyone, which for a
    /// payload that may have arrived over the network is the wrong default.
    /// </remarks>
    private static bool IsOfficialAndSatisfying(RuntimePayload payload, RuntimePackageRequirement requirement) =>
        RuntimePayloadIdentity.Satisfies(payload, requirement)
        && payload.Publisher is not null
        && string.Equals(
            RuntimePackageRequirement.NormalizePublisher(payload.Publisher),
            RuntimePackageRequirement.NormalizePublisher(MicrosoftPublisher),
            StringComparison.OrdinalIgnoreCase);

    private static string StagedName(RuntimePackageRequirement requirement) =>
        TargetPathSafety.EnsureSafeSegment($"{requirement.Name}_{requirement.Architecture}.appx");

    private DirectoryInfo HostCacheDirectory() =>
        new(Path.Join(winappDirectoryService.GetGlobalWinappDirectory().FullName, "cache", CacheFolderName));

    /// <summary>
    /// The Windows SDK's own copies of the VC framework packages.
    /// </summary>
    /// <remarks>
    /// Every architecture folder is searched rather than the one whose name matches, because the
    /// folder naming is inconsistent (<c>ARM64</c> beside <c>x64</c>) and the identity inside each
    /// package is authoritative anyway.
    /// </remarks>
    private static IEnumerable<DirectoryInfo> DefaultCacheDirectories()
    {
        foreach (var folder in (Environment.SpecialFolder[])
            [Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles])
        {
            if (Environment.GetFolderPath(folder) is not { Length: > 0 } programFiles)
            {
                continue;
            }

            var appxRoot = new DirectoryInfo(Path.Join(
                programFiles, "Microsoft SDKs", "Windows Kits", "10", "ExtensionSDKs",
                "Microsoft.VCLibs", "14.0", "Appx", "Retail"));

            if (!appxRoot.Exists)
            {
                continue;
            }

            DirectoryInfo[] architectures;
            try
            {
                architectures = appxRoot.GetDirectories();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var architecture in architectures)
            {
                yield return architecture;
            }
        }
    }

    /// <summary>Fetches one address over HTTPS.</summary>
    private static async Task<byte[]> DownloadAsync(string address, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var response = await client
            .GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaxPayloadBytes)
        {
            throw new IOException(
                $"The VC runtime payload is larger than the {MaxPayloadBytes} byte safety limit.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[64 * 1024];

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (destination.Length + read > MaxPayloadBytes)
            {
                throw new IOException(
                    $"The VC runtime payload is larger than the {MaxPayloadBytes} byte safety limit.");
            }

            destination.Write(buffer, 0, read);
        }

        return destination.ToArray();
    }
}
