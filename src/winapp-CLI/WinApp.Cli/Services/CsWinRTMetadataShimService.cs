// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

/// <summary>
/// SHIM (temporary) — see <see cref="ICsWinRTMetadataShimService"/>.
/// <para>
/// C#/WinRT authoring projects fail to build via <c>winapp run</c> project mode on hosts with no
/// registered Windows SDK (clean CI, containers, SDK-less dev boxes). Root cause:
/// <c>Microsoft.Windows.CsWinRT.targets</c> defaults <c>CsWinRTWindowsMetadata</c> to a bare SDK
/// version (<c>$(WindowsSDKVersion)</c> → <c>$(TargetPlatformVersion)</c>), which <c>cswinrt.exe</c>
/// resolves via <c>HKLM\SOFTWARE\Microsoft\Windows Kits\Installed Roots\KitsRoot10</c> and fails with
/// "Could not find the Windows SDK in the registry", cascading into a wall of WMC XAML errors.
/// </para>
/// <para>
/// Fix: point <c>CsWinRTWindowsMetadata</c> at a folder of winmds. Those already ship via the
/// <c>Microsoft.Windows.SDK.NET.Ref</c> ref pack auto-restored for any <c>net*-windows10.0.x</c> TFM,
/// on disk at <c>&lt;nuget-global&gt;\microsoft.windows.sdk.net.ref\&lt;ver&gt;\winmd\</c>. This service
/// injects that folder ONLY when no SDK is registered, so SDK-installed builds are untouched.
/// </para>
/// <para>
/// This is a stopgap pending a durable upstream cswinrt targets fix (cswinrt PR in flight); once
/// consumers are on a fixed <c>Microsoft.Windows.CsWinRT</c> this whole service can be deleted.
/// </para>
/// </summary>
internal sealed partial class CsWinRTMetadataShimService(
    INugetService nugetService,
    ILogger<CsWinRTMetadataShimService> logger) : ICsWinRTMetadataShimService
{
    private const string RefPackId = "microsoft.windows.sdk.net.ref";
    private const string SentinelWinmd = "Windows.Foundation.FoundationContract.winmd";

    /// <summary>
    /// Reports whether a Windows SDK is registered on the machine. Overridable so the resolution logic
    /// can be unit-tested without depending on the host's registry state. Defaults to a check that
    /// mirrors cswinrt's own lookup.
    /// </summary>
    internal Func<bool> IsWindowsSdkRegistered { get; set; } = static () => WindowsKitsRoot.GetRegistered() is not null;

    /// <inheritdoc />
    public string? ResolveMetadataFolder(string? targetFrameworkMoniker)
    {
        // A registered SDK means cswinrt's default registry resolution works; leave the build untouched.
        if (IsWindowsSdkRegistered())
        {
            logger.LogDebug("Windows SDK is registered; not injecting CsWinRTWindowsMetadata.");
            return null;
        }

        DirectoryInfo refPackRoot;
        try
        {
            var cache = nugetService.GetNuGetGlobalPackagesDir();
            refPackRoot = new DirectoryInfo(Path.Combine(cache.FullName, RefPackId));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not resolve the NuGet global packages cache; skipping CsWinRTWindowsMetadata shim.");
            return null;
        }

        if (!refPackRoot.Exists)
        {
            // The ref pack isn't restored (e.g. a non-CsWinRT project, or restore hasn't run). No-op and
            // let the normal build error surface rather than failing here.
            logger.LogDebug("{RefPackId} is not restored under {Path}; skipping CsWinRTWindowsMetadata shim.", RefPackId, refPackRoot.FullName);
            return null;
        }

        var tpvPrefix = ExtractPlatformVersionPrefix(targetFrameworkMoniker);

        var versionNames = refPackRoot.GetDirectories().Select(d => d.Name).ToList();
        var chosen = SelectBestRefPackVersionDir(
            versionNames,
            tpvPrefix,
            versionName => File.Exists(Path.Combine(refPackRoot.FullName, versionName, "winmd", SentinelWinmd)));

        if (chosen is null)
        {
            logger.LogDebug(
                "No {RefPackId} version with {Sentinel} found under {Path}; skipping CsWinRTWindowsMetadata shim.",
                RefPackId, SentinelWinmd, refPackRoot.FullName);
            return null;
        }

        // 'chosen' is a directory name enumerated from refPackRoot, so it is never rooted in practice; guard
        // anyway so a rooted value can't silently discard refPackRoot and 'winmd' in Path.Combine below.
        if (Path.IsPathRooted(chosen))
        {
            logger.LogDebug(
                "Chosen {RefPackId} version directory '{Version}' is unexpectedly rooted; skipping CsWinRTWindowsMetadata shim.",
                RefPackId, chosen);
            return null;
        }

        var winmdFolder = Path.Combine(refPackRoot.FullName, chosen, "winmd");
        logger.LogDebug(
            "No Windows SDK registered; injecting CsWinRTWindowsMetadata={Folder} (ref pack {Version}) for SDK-less build.",
            winmdFolder, chosen);
        return winmdFolder;
    }

    /// <inheritdoc />
    public bool IsWindowsSdkAbsent() => !IsWindowsSdkRegistered();

    /// <summary>
    /// Extracts the <c>major.minor.build</c> platform-version prefix (e.g. <c>10.0.19041</c>) from a
    /// target framework moniker such as <c>net10.0-windows10.0.19041.0</c>. Returns <c>null</c> when the
    /// moniker is absent or carries no Windows platform version.
    /// </summary>
    internal static string? ExtractPlatformVersionPrefix(string? targetFrameworkMoniker)
    {
        if (string.IsNullOrWhiteSpace(targetFrameworkMoniker))
        {
            return null;
        }

        var match = PlatformVersionRegex().Match(targetFrameworkMoniker);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"windows(\d+\.\d+\.\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PlatformVersionRegex();

    /// <summary>
    /// Chooses the best <c>Microsoft.Windows.SDK.NET.Ref</c> version directory name from
    /// <paramref name="versionNames"/>: the highest version whose first three components match
    /// <paramref name="platformVersionPrefix"/> (when supplied) and that passes <paramref name="isUsable"/>;
    /// otherwise the highest usable version overall. Pre-release suffixes (e.g. <c>-preview</c>) are ignored
    /// for ordering and lose ties to a stable build of the same version. Returns <c>null</c> when none are usable.
    /// Pure and unit-testable.
    /// </summary>
    internal static string? SelectBestRefPackVersionDir(
        IEnumerable<string> versionNames,
        string? platformVersionPrefix,
        Func<string, bool> isUsable)
    {
        var parsed = versionNames
            .Select(name => (Name: name, Version: TryParseRefPackVersion(name, out var v, out var stable) ? v : null, Stable: stable))
            .Where(x => x.Version is not null)
            .OrderByDescending(x => x.Version!)
            .ThenByDescending(x => x.Stable)
            .ToList();

        if (!string.IsNullOrEmpty(platformVersionPrefix))
        {
            var preferred = parsed.FirstOrDefault(x =>
                PrefixMatches(x.Name, platformVersionPrefix) && isUsable(x.Name));
            if (preferred.Name is not null)
            {
                return preferred.Name;
            }
        }

        var fallback = parsed.FirstOrDefault(x => isUsable(x.Name));
        return fallback.Name;
    }

    private static bool PrefixMatches(string versionName, string platformVersionPrefix)
    {
        // Compare the first three components (major.minor.build) numerically-safely as text: the ref-pack
        // folder name is like "10.0.19041.55"; the prefix is like "10.0.19041".
        return versionName.StartsWith(platformVersionPrefix + ".", StringComparison.Ordinal)
            || string.Equals(versionName, platformVersionPrefix, StringComparison.Ordinal);
    }

    private static bool TryParseRefPackVersion(string name, out Version? version, out bool isStable)
    {
        version = null;
        isStable = true;

        var dash = name.IndexOf('-');
        if (dash >= 0)
        {
            isStable = false;
            name = name[..dash];
        }

        return Version.TryParse(name, out version);
    }
}
