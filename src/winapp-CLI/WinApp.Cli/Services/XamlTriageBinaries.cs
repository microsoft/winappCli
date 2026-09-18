// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

/// <summary>
/// Native debugging binaries required to host DbgEng and run the WinUI JavaScript
/// extension (<c>!xamlstowed</c> / <c>!xamltriage</c>).
/// </summary>
/// <param name="BinDir">Directory containing <c>dbgeng.dll</c> (and co-located providers).</param>
/// <param name="JsProviderPath">Full path to the resolved <c>JsProvider.dll</c> (may live in a
/// <c>winext</c> subfolder rather than directly in <see cref="BinDir"/>).</param>
/// <param name="HasSymSrv"><c>symsrv.dll</c> is co-located, enabling <c>srv*</c> symbol paths.</param>
/// <param name="Source">Human-readable description of where the binaries were resolved from.</param>
internal sealed record ResolvedTriageBinaries(string BinDir, string JsProviderPath, bool HasSymSrv, string Source);

/// <summary>
/// Locates (and, when missing, downloads on first use) the host-architecture native
/// debugging binaries needed by <see cref="XamlTriageService"/>.
/// <para>
/// Resolution precedence:
/// <list type="number">
///   <item><c>WINAPP_DBGTOOLS_DIR</c> environment override.</item>
///   <item>An installed copy of <em>Debugging Tools for Windows</em> (Windows Kits).</item>
///   <item>A download-on-first-use cache populated from NuGet (mirrors the tool command).</item>
/// </list>
/// </para>
/// <para>
/// <c>JsProvider.dll</c> (the JS scripting host for <c>.scriptload</c>) is <strong>not</strong>
/// distributed on NuGet — it ships only inside the WinDbg bundle. The engine bits come from NuGet
/// (global cache or download), while <c>JsProvider.dll</c> is acquired separately via
/// <see cref="WinDbgJsProviderAcquirer"/>; when neither can be obtained the caller degrades
/// gracefully.
/// </para>
/// </summary>
internal static class XamlTriageBinaries
{
    /// <summary>
    /// Authoritative override directory containing a full debugger layout (dbgeng + JsProvider).
    /// When set, only this directory is considered (installed tools and cache are skipped).
    /// </summary>
    public const string EnvOverride = "WINAPP_DBGTOOLS_DIR";

    /// <summary>
    /// <c>true</c> when <see cref="EnvOverride"/> is configured. The override is authoritative:
    /// installed tools, the download-on-first-use cache, and cache acquisition are all skipped so the
    /// override remains the single source of truth (see <see cref="CandidateDirectories"/>).
    /// </summary>
    public static bool IsEnvOverrideSet =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvOverride));

    private const string FlatContainer = "https://api.nuget.org/v3-flatcontainer";

    /// <summary>
    /// Seam for the two flat-container HTTP GETs (<c>index.json</c> and the pinned <c>.nupkg</c>).
    /// Defaults to a real <see cref="HttpClient.GetAsync(string?, CancellationToken)"/>; tests replace
    /// it with canned responses so the download-orchestration, integrity-verification, and archive
    /// extraction logic can be exercised offline. Only the default delegate performs network I/O.
    /// </summary>
    internal static Func<HttpClient, string, CancellationToken, Task<HttpResponseMessage>> HttpGetAsync { get; set; }
        = static (http, url, cancellationToken) => http.GetAsync(url, cancellationToken);

    /// <summary>
    /// Pinned native-debugger package version. Must stay in sync with the
    /// <c>Microsoft.Debugging.Platform.*</c> entries in <c>Directory.Packages.props</c>; a unit test
    /// asserts they match so runtime acquisition uses the same version that <c>dotnet restore</c> pins.
    /// </summary>
    public const string DbgPackageVersion = "20260319.1511.0";

    // Expected SHA-512 (hex) of each pinned .nupkg, matching NuGet's own `.nupkg.sha512` for the
    // pinned version. Compiled in so the integrity check does not trust the same feed the package is
    // fetched from: a mirrored/compromised flat-container feed cannot serve altered native DLLs that
    // would then be loaded into the debugger process. Regenerate if DbgPackageVersion changes.
    private const string DbgEngPackageSha512 =
        "54cf706d6d49151f1b28d5c2eb9bfe2d989ddf461965b03c380409f0ad4e3b8628aedaabdf351b53d80c0cefe4a3dbc45e9d3efa233a86866923d41e062e8d70";
    private const string SymSrvPackageSha512 =
        "61dea5162daacf8c9bb601c67258add2f806c34781b4feceb90c1cfe214870f0454761e6e9102c5f294ca44c31c9512604b9e20a64242ecc0807b1383d128ab0";

    // Native engine bits available from NuGet. DbgEng ships the full engine layout (including
    // dbgmodel.dll and msdia140.dll), so no separate DbgX package is required. JsProvider.dll is
    // intentionally absent here — it is not on NuGet and is acquired from the WinDbg bundle instead.
    private static readonly (string Package, string Version, string Sha512, string[] Files)[] NuGetComponents =
    [
        ("Microsoft.Debugging.Platform.DbgEng", DbgPackageVersion, DbgEngPackageSha512, ["dbgeng.dll", "dbghelp.dll", "dbgcore.dll", "dbgmodel.dll", "msdia140.dll"]),
        ("Microsoft.Debugging.Platform.SymSrv", DbgPackageVersion, SymSrvPackageSha512, ["symsrv.dll"]),
    ];

    /// <summary>
    /// The pinned NuGet debugger packages and their expected <c>.nupkg</c> SHA-512 (hex). Exposed for a
    /// drift test that verifies the compiled-in hashes still match the restored packages.
    /// </summary>
    internal static IReadOnlyList<(string Package, string Version, string Sha512)> PinnedPackages =>
        NuGetComponents.Select(c => (c.Package, c.Version, c.Sha512)).ToList();

    /// <summary>Folder token used by the Windows Kits Debuggers layout for the host arch.</summary>
    public static string KitsArch => KitsArchFor(RuntimeInformation.ProcessArchitecture);

    /// <summary>Maps a CPU architecture to its Windows Kits Debuggers folder token. Pure and testable.</summary>
    internal static string KitsArchFor(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => "x64",
    };

    /// <summary>Folder token used by the NuGet debugging packages for the host arch.</summary>
    public static string NuGetArch => NuGetArchFor(RuntimeInformation.ProcessArchitecture);

    /// <summary>Maps a CPU architecture to its NuGet debugging-package folder token. Pure and testable.</summary>
    internal static string NuGetArchFor(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => "amd64",
    };

    /// <summary>
    /// Resolves an existing directory that contains both <c>dbgeng.dll</c> and
    /// <c>JsProvider.dll</c> for the host architecture, or <c>null</c> when none is found.
    /// <para>
    /// On every resolve (including cache hits) the <c>JsProvider.dll</c> is re-verified as validly
    /// Microsoft-signed <em>and</em> checked to be the same build as the co-located <c>dbgeng.dll</c>:
    /// it is loaded into the debugger process, and a copy that was replaced on disk, or that drifted
    /// from the engine build (which crashes the triage child with STATUS_BREAKPOINT), must be rejected
    /// so the cache self-heals instead of silently breaking triage.
    /// When <paramref name="requireTrustedCache"/> is set, the repository-local cache additionally
    /// requires authentication of the engine and its co-located loadable dependencies.
    /// </para>
    /// </summary>
    public static ResolvedTriageBinaries? ResolveExisting(DirectoryInfo cacheBinDir, ILogger logger, bool requireTrustedCache = false) =>
        ResolveExisting(cacheBinDir, logger, b =>
            AuthenticodeVerifier.IsTrustedMicrosoftSigned(b.JsProviderPath, logger)
            && IsProviderCompatibleWithEngine(b.BinDir, b.JsProviderPath, logger)
            && (!requireTrustedCache
                || !Path.GetFullPath(b.BinDir).Equals(cacheBinDir.FullName, StringComparison.OrdinalIgnoreCase)
                || IsTrustedCacheLayout(b.BinDir, logger, AuthenticodeVerifier.IsTrustedMicrosoftSigned)));

    /// <summary>
    /// A repository-local cache is executable input, not just cached data. Product versions
    /// establish compatibility, never authenticity. Require the engine payload and authenticate
    /// every co-located DLL before a triage child can load any of them.
    /// </summary>
    internal static bool IsTrustedCacheLayout(string binDir, ILogger logger, Func<string, ILogger, bool> signatureVerifier)
    {
        if (NuGetComponents[0].Files.Any(file => !File.Exists(Path.Combine(binDir, file))))
        {
            return false;
        }

        foreach (var path in Directory.EnumerateFiles(binDir, "*.dll", SearchOption.AllDirectories))
        {
            if (!signatureVerifier(path, logger))
            {
                logger.LogDebug("Rejecting local WinUI triage cache: {Path} is not validly signed by Microsoft.", path);
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Testable core of <see cref="ResolveExisting(DirectoryInfo, ILogger, bool)"/> with an injectable
    /// <paramref name="validator"/> so unit tests can exercise resolution without requiring a real
    /// Authenticode-signed, version-matched <c>JsProvider.dll</c>.
    /// </summary>
    internal static ResolvedTriageBinaries? ResolveExisting(DirectoryInfo cacheBinDir, ILogger logger, Func<ResolvedTriageBinaries, bool> validator)
    {
        foreach (var (dir, source) in CandidateDirectories(cacheBinDir))
        {
            var resolved = TryDirectory(dir, source);
            if (resolved == null)
            {
                continue;
            }

            if (!validator(resolved))
            {
                logger.LogDebug("Rejecting WinUI triage binaries from {Source}: {Path} failed signature/version validation.", source, resolved.JsProviderPath);
                continue;
            }

            logger.LogDebug("Resolved WinUI triage debugging binaries from {Source}: {Dir}", source, dir);
            return resolved;
        }

        return null;
    }

    private static IEnumerable<(string Dir, string Source)> CandidateDirectories(DirectoryInfo cacheBinDir)
    {
        // An explicit override is authoritative: when set, only that directory is considered.
        if (IsEnvOverrideSet)
        {
            var overrideDir = Environment.GetEnvironmentVariable(EnvOverride)!;
            yield return (overrideDir, $"{EnvOverride} override");
            yield break;
        }

        foreach (var root in InstalledDebuggerRoots())
        {
            yield return (Path.Combine(root, "Windows Kits", "10", "Debuggers", KitsArch), "installed Debugging Tools for Windows");
        }

        yield return (cacheBinDir.FullName, "download-on-first-use cache");
    }

    private static IEnumerable<string> InstalledDebuggerRoots()
    {
        foreach (var variable in new[] { "ProgramFiles(x86)", "ProgramW6432", "ProgramFiles" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }
    }

    /// <summary>
    /// Returns a resolved descriptor when <paramref name="dir"/> contains a usable engine
    /// (dbgeng.dll) and a co-located JsProvider.dll (in the directory or its <c>winext</c> child).
    /// </summary>
    private static ResolvedTriageBinaries? TryDirectory(string dir, string source)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            return null;
        }

        var dbgeng = Path.Combine(dir, "dbgeng.dll");
        if (!File.Exists(dbgeng))
        {
            return null;
        }

        // dbgeng searches its own directory and the winext subfolder for extension providers, but the
        // child runner must .load JsProvider.dll by explicit path, so capture where it actually lives.
        var jsProviderPath = new[]
        {
            Path.Combine(dir, "JsProvider.dll"),
            Path.Combine(dir, "winext", "JsProvider.dll"),
        }.FirstOrDefault(File.Exists);
        if (jsProviderPath == null)
        {
            return null;
        }

        var hasSymSrv = File.Exists(Path.Combine(dir, "symsrv.dll"));
        return new ResolvedTriageBinaries(dir, jsProviderPath, hasSymSrv, source);
    }

    /// <summary>
    /// Returns <c>true</c> when the cache directory already contains the native engine
    /// (<c>dbgeng.dll</c>), independent of whether <c>JsProvider.dll</c> is present yet.
    /// </summary>
    public static bool HasEngine(DirectoryInfo cacheBinDir) =>
        File.Exists(Path.Combine(cacheBinDir.FullName, "dbgeng.dll"));

    /// <summary>
    /// Returns <c>true</c> when the <c>JsProvider.dll</c> at <paramref name="jsProviderPath"/> is the
    /// same product build as the <c>dbgeng.dll</c> in <paramref name="binDir"/>. Loading a JsProvider
    /// from a different engine build crashes the triage child with STATUS_BREAKPOINT, so a mismatch (or
    /// an unreadable/corrupt engine whose version can't be read) is treated as incompatible.
    /// </summary>
    internal static bool IsProviderCompatibleWithEngine(string binDir, string jsProviderPath, ILogger logger)
    {
        var engineVersion = TryGetProductVersion(Path.Combine(binDir, "dbgeng.dll"));
        var providerVersion = TryGetProductVersion(jsProviderPath);
        if (!VersionsMatch(engineVersion, providerVersion))
        {
            logger.LogDebug(
                "Engine/JsProvider build mismatch: dbgeng.dll={Engine}, JsProvider.dll={Provider}. A mismatched provider crashes the triage child.",
                engineVersion ?? "<unreadable>", providerVersion ?? "<unreadable>");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Compares two file version strings for equality on their numeric <c>a.b.c.d</c> component,
    /// tolerating trailing decorations (e.g. <c>"10.0.29547.1002 (WinBuild.160101.0800)"</c>). Returns
    /// <c>false</c> when either value is missing or unparseable. Extracted for unit testing.
    /// </summary>
    internal static bool VersionsMatch(string? a, string? b)
    {
        var na = NormalizeVersion(a);
        var nb = NormalizeVersion(b);
        return na != null && na == nb;
    }

    private static string? NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var token = value.Trim().Split(' ')[0];
        return Version.TryParse(token, out var parsed) ? parsed.ToString() : null;
    }

    private static string? TryGetProductVersion(string path)
    {
        try
        {
            return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).ProductVersion : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="path"/> exists and looks like an intact PE image (starts
    /// with the <c>MZ</c> signature and is not implausibly small). Used to detect a truncated/corrupt
    /// cached engine DLL so it is re-acquired instead of poisoning the cache across runs — unlike
    /// <c>JsProvider.dll</c>, engine DLLs outside the repository-local fallback cache are not otherwise
    /// re-verified on a cache hit.
    /// </summary>
    private static bool IsUsablePeFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 4096)
            {
                return false;
            }

            using var stream = info.OpenRead();
            return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// When <see cref="EnvOverride"/> is set but the override directory is not a usable debugger
    /// layout, returns a human-readable description of the override directory and which required
    /// component(s) are missing. Returns <c>null</c> when no override is configured.
    /// </summary>
    public static string? DescribeOverrideGap()
    {
        if (!IsEnvOverrideSet)
        {
            return null;
        }

        var overrideDir = Environment.GetEnvironmentVariable(EnvOverride)!;
        if (!Directory.Exists(overrideDir))
        {
            return $"the {EnvOverride} override directory '{overrideDir}' does not exist";
        }

        var missing = new List<string>();
        if (!File.Exists(Path.Combine(overrideDir, "dbgeng.dll")))
        {
            missing.Add("dbgeng.dll");
        }

        var hasJsProvider = File.Exists(Path.Combine(overrideDir, "JsProvider.dll"))
            || File.Exists(Path.Combine(overrideDir, "winext", "JsProvider.dll"));
        if (!hasJsProvider)
        {
            missing.Add("JsProvider.dll");
        }

        if (missing.Count == 0)
        {
            return null;
        }

        return $"the {EnvOverride} override directory '{overrideDir}' is missing {string.Join(" and ", missing)}";
    }

    /// <summary>
    /// Best-effort population of the cache directory with the NuGet-available native debugging bits.
    /// Prefers copying from the NuGet global packages cache (populated by <c>dotnet restore</c>) and
    /// falls back to downloading the flat-container <c>.nupkg</c> on first use. Does not acquire
    /// <c>JsProvider.dll</c>. Returns the number of component packages successfully materialized.
    /// <para>
    /// This intentionally does <em>not</em> delegate to <c>INugetService</c>: that path performs no
    /// package-integrity verification, whereas the DLLs materialized here are loaded into the debugger
    /// process and are therefore version-pinned and checked against a compiled-in SHA-512 content hash
    /// (see <see cref="VerifyPackageHash"/>) before extraction. Reusing the general downloader would
    /// silently drop that guarantee, so the bespoke download is deliberate.
    /// </para>
    /// </summary>
    public static async Task<int> TryAcquireFromNuGetAsync(
        DirectoryInfo cacheBinDir, DirectoryInfo? nugetCacheDir, ILogger logger, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(cacheBinDir.FullName);
        using var http = new HttpClient();
        var acquired = 0;

        foreach (var (package, version, sha512, files) in NuGetComponents)
        {
            try
            {
                if (files.All(f => IsUsablePeFile(Path.Combine(cacheBinDir.FullName, f))))
                {
                    acquired++;
                    continue;
                }

                if (nugetCacheDir != null && TryCopyFromGlobalCache(package, version, files, nugetCacheDir, cacheBinDir, logger))
                {
                    acquired++;
                    continue;
                }

                if (await TryMaterializePackageAsync(http, package, version, sha512, files, cacheBinDir, logger, cancellationToken))
                {
                    acquired++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not CacheWriteException)
            {
                logger.LogDebug(ex, "Failed to acquire debugging component {Package} from NuGet.", package);
            }
        }

        return acquired;
    }

    /// <summary>
    /// Copies the required files for a component from the NuGet global packages cache
    /// (<c>&lt;cache&gt;/&lt;id&gt;/&lt;version&gt;/content/&lt;arch&gt;/</c>). Prefers the
    /// <paramref name="pinnedVersion"/> (the version pinned in <c>Directory.Packages.props</c> and
    /// guaranteed present after <c>dotnet restore</c>) and only falls back to the newest cached
    /// version when the pinned one is absent, so dev/CI builds are deterministic.
    /// </summary>
    internal static bool TryCopyFromGlobalCache(
        string package, string pinnedVersion, string[] files, DirectoryInfo nugetCacheDir, DirectoryInfo cacheBinDir, ILogger logger)
    {
        var packageDir = new DirectoryInfo(Path.Combine(nugetCacheDir.FullName, package.ToLowerInvariant()));
        if (!packageDir.Exists)
        {
            return false;
        }

        // Pinned version first (deterministic); then newest available as a graceful fallback.
        var pinnedDir = new DirectoryInfo(Path.Combine(packageDir.FullName, pinnedVersion));
        var candidates = new[] { pinnedDir }
            .Where(d => d.Exists)
            .Concat(packageDir.EnumerateDirectories()
                .Where(d => !d.Name.Equals(pinnedVersion, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.Name, StringComparer.OrdinalIgnoreCase));

        foreach (var versionDir in candidates)
        {
            var archDir = Path.Combine(versionDir.FullName, "content", NuGetArch);
            if (!Directory.Exists(archDir) || !files.All(f => File.Exists(Path.Combine(archDir, f))))
            {
                continue;
            }

            foreach (var file in files)
            {
                try { AtomicFile.Copy(Path.Combine(archDir, file), Path.Combine(cacheBinDir.FullName, file)); }
                catch (Exception ex) when (CacheStorage.IsStorageFailure(ex))
                {
                    throw new CacheWriteException(cacheBinDir.FullName, ex);
                }
            }

            if (!versionDir.Name.Equals(pinnedVersion, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug("Pinned version {Pinned} of {Package} not in NuGet cache; used {Used} instead.",
                    pinnedVersion, package, versionDir.Name);
            }

            logger.LogDebug("Copied {Count} file(s) for {Package} from NuGet global cache {Version}.", files.Length, package, versionDir.Name);
            return true;
        }

        return false;
    }

    internal static async Task<bool> TryMaterializePackageAsync(
        HttpClient http, string package, string pinnedVersion, string expectedSha512, string[] files, DirectoryInfo cacheBinDir, ILogger logger, CancellationToken cancellationToken)
    {
        var id = package.ToLowerInvariant();
        var version = await ResolveDownloadVersionAsync(http, id, pinnedVersion, logger, cancellationToken);
        if (string.IsNullOrEmpty(version))
        {
            return false;
        }

        // Integrity is anchored to the pinned version's compiled-in content hash. We only have a hash
        // for the pinned version, so refuse to download (and later load native code from) any other
        // version rather than extracting unverified bits into the debugger process.
        if (!string.Equals(version, pinnedVersion, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("Skipping {Id} {Version}: only pinned version {Pinned} has a verified content hash.", id, version, pinnedVersion);
            return false;
        }

        // Download the whole .nupkg into memory so its hash can be verified before anything is extracted.
        var nupkgUrl = $"{FlatContainer}/{id}/{version}/{id}.{version}.nupkg";
        using var nupkgResponse = await HttpGetAsync(http, nupkgUrl, cancellationToken);
        if (!nupkgResponse.IsSuccessStatusCode)
        {
            return false;
        }

        var nupkgBytes = await nupkgResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!VerifyPackageHash(nupkgBytes, expectedSha512))
        {
            logger.LogWarning("Refusing {Id} {Version}: downloaded package hash did not match the pinned value; the feed may be compromised or mirrored.", id, version);
            return false;
        }

        var tempPkgDir = Path.Combine(cacheBinDir.FullName, $".staging-{id}-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempPkgDir);
            using (var nupkgStream = new MemoryStream(nupkgBytes, writable: false))
            using (var archive = new ZipArchive(nupkgStream, ZipArchiveMode.Read))
            {
                archive.ExtractToDirectory(tempPkgDir, overwriteFiles: true);
            }

            var copied = 0;
            foreach (var file in files)
            {
                var source = FindBestArchMatch(tempPkgDir, file);
                if (source != null)
                {
                    AtomicFile.Copy(source, Path.Combine(cacheBinDir.FullName, file));
                    copied++;
                }
            }

            if (copied > 0)
            {
                logger.LogDebug("Materialized {Count}/{Total} file(s) from {Package} {Version}.", copied, files.Length, package, version);
            }

            return copied == files.Length;
        }
        catch (Exception ex) when (CacheStorage.IsStorageFailure(ex))
        {
            throw new CacheWriteException(cacheBinDir.FullName, ex);
        }
        finally
        {
            try { Directory.Delete(tempPkgDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Confirms the flat-container index lists the pinned version. Only the pinned version can be
    /// integrity-verified (its content hash is compiled in), so any other version is rejected rather
    /// than downloaded — there is deliberately no "latest" fallback for native code the debugger loads.
    /// </summary>
    private static async Task<string?> ResolveDownloadVersionAsync(
        HttpClient http, string id, string pinnedVersion, ILogger logger, CancellationToken cancellationToken)
    {
        using var indexResponse = await HttpGetAsync(http, $"{FlatContainer}/{id}/index.json", cancellationToken);
        if (!indexResponse.IsSuccessStatusCode)
        {
            return null;
        }

        await using var indexStream = await indexResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var indexDoc = await JsonDocument.ParseAsync(indexStream, cancellationToken: cancellationToken);
        var versions = indexDoc.RootElement.GetProperty("versions").EnumerateArray()
            .Select(v => v.GetString())
            .Where(v => !string.IsNullOrEmpty(v))
            .ToList();

        if (versions.Any(v => string.Equals(v, pinnedVersion, StringComparison.OrdinalIgnoreCase)))
        {
            return pinnedVersion;
        }

        logger.LogDebug("Pinned version {Pinned} of {Id} is not available on the feed; skipping download.", pinnedVersion, id);
        return null;
    }

    /// <summary>
    /// Verifies a downloaded <c>.nupkg</c>'s SHA-512 against the compiled-in pinned hash before any of
    /// its native DLLs are extracted and loaded into the debugger process. The comparison is
    /// case-insensitive hex and does not consult the feed, so a mirrored or compromised flat-container
    /// cannot substitute altered content. Returns <c>false</c> on any mismatch (fail closed).
    /// </summary>
    internal static bool VerifyPackageHash(byte[] nupkgBytes, string expectedSha512Hex)
    {
        var actual = Convert.ToHexString(SHA512.HashData(nupkgBytes));
        return string.Equals(actual, expectedSha512Hex, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds the copy of <paramref name="fileName"/> whose path best matches the host
    /// architecture, falling back to any match. Prefers paths containing the host arch token.
    /// </summary>
    private static string? FindBestArchMatch(string root, string fileName)
    {
        var matches = Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).ToList();
        if (matches.Count == 0)
        {
            return null;
        }

        var archTokens = new[] { NuGetArch, KitsArch };
        var preferred = matches.FirstOrDefault(m =>
            archTokens.Any(token => m.Contains(Path.DirectorySeparatorChar + token + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));

        return preferred ?? matches[0];
    }
}
