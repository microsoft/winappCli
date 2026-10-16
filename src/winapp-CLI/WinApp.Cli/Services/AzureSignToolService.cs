// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Tools;

namespace WinApp.Cli.Services;

/// <summary>
/// Acquires the Azure Trusted Signing client library and drives signtool to sign a file.
/// </summary>
internal class AzureSignToolService(
    IBuildToolsService buildToolsService,
    INugetService nugetService,
    IPackageInstallationService packageInstallationService,
    IWinappDirectoryService winappDirectoryService) : IAzureSignToolService
{
    internal const string ArtifactSigningClientPackage = "Microsoft.ArtifactSigning.Client";

    // Pin to a known-good version so the DLL loaded into signtool is reproducible and
    // not silently upgraded to whatever happens to be latest in the NuGet feed.
    internal const string ArtifactSigningClientVersion = "1.0.128";

    // SHA-256 of the exact Azure.CodeSigning.Dlib.dll payloads shipped in the pinned package
    // version above (bin/x64 and bin/x86). signtool loads this DLL and it acquires an Azure
    // access token inside the signing process, so the flat-container download (HTTPS + extract,
    // no NuGet signature/hash check) is not by itself sufficient to authenticate it: a mirrored
    // or compromised feed/CDN could serve an altered DLL. We therefore refuse to hand signtool a
    // dlib whose content does not match one of these compiled-in hashes (fail closed). When the
    // pinned version is bumped, refresh these values from the official nuget.org package.
    private static readonly HashSet<string> TrustedDlibSha256 = new(StringComparer.OrdinalIgnoreCase)
    {
        // bin/x64/Azure.CodeSigning.Dlib.dll
        "2D4C1BBC87467B3AC25BBC49DF58CC8B36A0F92B3E21AA98BBBAD08A4D7C98BA",
        // bin/x86/Azure.CodeSigning.Dlib.dll
        "584110D279E2C112206E4CC28949354654E8A72B143EDB52352CFA103C65AB7D",
    };

    // Microsoft's official RFC 3161 timestamp endpoint for Azure Trusted Signing. It is HTTP by
    // design, matching Microsoft's documented signtool integration and the industry norm for
    // timestamp authorities (DigiCert, Sectigo, etc. all publish http:// timestamp URLs). Security
    // does not depend on the transport: the timestamp token returned by the TSA is itself
    // cryptographically signed, and signtool verifies that signature, so a man-in-the-middle cannot
    // forge or alter the timestamp without invalidating it — a tampered response is rejected rather
    // than trusted. The TSA also does not receive the file being signed (only a hash), so HTTP does
    // not expose content. https is not offered at this endpoint, so using it would simply fail.
    private const string TimestampUrl = "http://timestamp.acs.microsoft.com";

    /// <summary>
    /// Resolves the trusted Azure CLI <c>wbin</c> directories (those containing a real <c>az.cmd</c>)
    /// that are prepended to signtool's PATH. Exposed as a settable seam only so tests can supply a
    /// deterministic set independent of what is installed on the test host; production uses the known
    /// machine/user install roots.
    /// </summary>
    internal Func<IReadOnlyList<string>> TrustedAzureCliBinDirsProvider { get; set; } = DefaultTrustedAzureCliBinDirs;

    private static IReadOnlyList<string> DefaultTrustedAzureCliBinDirs() =>
        AzureAuthService.GetAzureCliInstallRoots()
            .Select(root => Path.Join(root, "wbin"))
            .Where(wbin => File.Exists(Path.Join(wbin, "az.cmd")))
            .ToList();

    public async Task SignAsync(
        FileInfo filePath,
        FileInfo metadataFilePath,
        string? tenantId,
        TaskContext taskContext,
        CancellationToken cancellationToken = default)
    {
        // Ensure the Trusted Signing dlib is available
        var dlibPath = await EnsureTrustedSigningDlibAsync(taskContext, cancellationToken);

        // The dlib only ships as x64/x86. We must use the matching signtool architecture.
        // Determine dlib architecture from its path (bin/x64/ or bin/x86/)
        var dlibArch = dlibPath.Directory?.Name; // "x64" or "x86"

        // Ensure signtool is installed, then find the matching architecture version
        var signtoolPath = await buildToolsService.EnsureBuildToolAvailableAsync("signtool.exe", taskContext, cancellationToken: cancellationToken);

        // If we're on ARM64 and the signtool resolved is arm64, swap to x64 to match the dlib
        if (dlibArch != null && !signtoolPath.FullName.Contains($"\\{dlibArch}\\", StringComparison.OrdinalIgnoreCase))
        {
            // Try to find the x64 signtool alongside the resolved one
            var signtoolDir = signtoolPath.Directory!;
            var parentDir = signtoolDir.Parent;
            if (parentDir != null)
            {
                var matchingArchSigntool = new FileInfo(Path.Join(parentDir.FullName, dlibArch, "signtool.exe"));
                if (matchingArchSigntool.Exists)
                {
                    signtoolPath = matchingArchSigntool;
                }
            }
        }

        // Build signtool arguments for Azure Trusted Signing
        var arguments = $@"sign /v /debug /fd {PackageDigestAlgorithm.Detect(filePath)} /tr ""{TimestampUrl}"" /td SHA256 /dlib ""{dlibPath.FullName}"" /dmdf ""{metadataFilePath.FullName}"" ""{filePath.FullName}""";

        taskContext.AddDebugMessage($"Using signtool: {signtoolPath.FullName}");
        taskContext.AddDebugMessage($"Using dlib: {dlibPath.FullName}");

        // Build the child environment: forward the tenant so the dlib authenticates against the
        // correct tenant, and harden PATH so its Azure CLI lookup can't be hijacked (see below).
        var environment = BuildSigntoolEnvironment(tenantId);

        // Reuse the shared build-tool runner so process spawning, concurrent stream draining,
        // cancellation/kill, and exit-code handling live in one place. We pass the resolved
        // (architecture-matched) signtool path as an override rather than re-resolving by name.
        //
        // The dlib performs its own Azure.Identity authentication in-process and, unlike
        // AzureAuthService, its credential chain still includes AzureCliCredential, which resolves
        // 'az' by ambient lookup. Two things harden that lookup: (1) signtool runs from a trusted
        // system directory (System32) instead of inheriting the caller's working directory, so a
        // repo-local 'az.cmd' next to the target can't be found via the current-directory-first
        // search; and (2) the known Azure CLI install directories are prepended to PATH (see
        // BuildSigntoolEnvironment), so the legitimate az.cmd wins over any 'az.cmd' injected
        // elsewhere on PATH. All signtool file arguments (dlib, metadata, target) are absolute, so
        // the working directory does not affect signing.
        await buildToolsService.RunBuildToolAsync(
            new GenericTool("signtool.exe"),
            arguments,
            taskContext,
            toolPathOverride: signtoolPath,
            environment: environment,
            workingDirectory: Environment.SystemDirectory,
            cancellationToken: cancellationToken);

        taskContext.AddDebugMessage("File signed successfully");
    }

    /// <summary>
    /// Builds the environment overrides for the signtool child process: the tenant id (when known)
    /// and a hardened PATH that prepends the trusted Azure CLI install directories. Prepending is
    /// additive — existing PATH entries are preserved so non-standard installs keep working — while
    /// ensuring the dlib's <c>AzureCliCredential</c> resolves the legitimate <c>az.cmd</c> ahead of
    /// any attacker-injected one later on PATH. Returns null when there is nothing to override.
    /// </summary>
    private Dictionary<string, string>? BuildSigntoolEnvironment(string? tenantId)
    {
        var environment = new Dictionary<string, string>();

        if (!string.IsNullOrEmpty(tenantId))
        {
            environment["AZURE_TENANT_ID"] = tenantId;
        }

        var trustedAzBinDirs = TrustedAzureCliBinDirsProvider();
        if (trustedAzBinDirs.Count > 0)
        {
            var prefix = string.Join(Path.PathSeparator, trustedAzBinDirs);
            var currentPath = Environment.GetEnvironmentVariable("PATH");
            environment["PATH"] = string.IsNullOrEmpty(currentPath)
                ? prefix
                : prefix + Path.PathSeparator + currentPath;
        }

        return environment.Count > 0 ? environment : null;
    }

    /// <summary>
    /// Verifies that a located dlib matches one of the compiled-in pinned hashes before it is used.
    /// Exposed as a settable seam (default: <see cref="DefaultIsTrustedDlib"/>) only so unit tests can
    /// exercise the install/cache flow with placeholder DLL content; production never overrides it, so
    /// the real SHA-256 check always runs.
    /// </summary>
    internal Func<FileInfo, bool> DlibIntegrityVerifier { get; set; } = DefaultIsTrustedDlib;

    internal async Task<FileInfo> EnsureTrustedSigningDlibAsync(TaskContext taskContext, CancellationToken cancellationToken)
    {
        // Check if already available in NuGet cache. Verify the cached DLL's content before trusting
        // it: the shared NuGet global cache can be populated by anything, so a cache hit alone does
        // not prove the payload is the pinned, authenticated dlib.
        var dlibPath = FindTrustedSigningDlib(ArtifactSigningClientVersion);
        if (dlibPath != null)
        {
            if (DlibIntegrityVerifier(dlibPath))
            {
                return dlibPath;
            }

            // The cached DLL does not match a pinned hash — treat it like a poisoned cache entry and
            // delete the version directory so the install below re-extracts a clean copy.
            taskContext.AddDebugMessage(
                $"Cached {ArtifactSigningClientPackage} dlib failed integrity verification; removing and reinstalling.");
            RemoveVersionDir(ArtifactSigningClientVersion, taskContext);
        }

        // Download the pinned version of the package
        await taskContext.AddSubTaskAsync($"Installing {ArtifactSigningClientPackage} {ArtifactSigningClientVersion}...", async (subContext, ct) =>
        {
            // A previous install that failed or was cancelled mid-extraction can leave the version
            // directory present but without the dlib. EnsurePackageAsync treats any existing version
            // directory as installed and would skip re-downloading, permanently poisoning the cache.
            // Remove such a partial directory first so the install actually re-extracts.
            RemovePoisonedVersionDir(ArtifactSigningClientVersion, subContext);

            var globalWinappDir = winappDirectoryService.GetGlobalWinappDirectory();
            var success = await packageInstallationService.EnsurePackageAsync(
                globalWinappDir,
                ArtifactSigningClientPackage,
                subContext,
                version: ArtifactSigningClientVersion,
                cancellationToken: ct);

            if (!success)
            {
                return (1, $"Failed to install {ArtifactSigningClientPackage}.");
            }

            return (0, $"{ArtifactSigningClientPackage} installed successfully.");
        }, cancellationToken);

        // EnsurePackageAsync swallows all exceptions (including OperationCanceledException) and
        // returns false, and the subtask wrapper likewise converts a thrown cancellation into a
        // failed-result tuple rather than rethrowing. Either way a cancelled download would otherwise
        // fall through to the dlib lookup below. Re-check the caller's token here so Ctrl+C during the
        // install propagates as cancellation instead of a generic "could not find the dlib" failure.
        cancellationToken.ThrowIfCancellationRequested();

        dlibPath = FindTrustedSigningDlib(ArtifactSigningClientVersion);
        if (dlibPath == null)
        {
            throw new InvalidOperationException(
                $"Could not find the Trusted Signing client library after installing {ArtifactSigningClientPackage} {ArtifactSigningClientVersion}.\n" +
                "Ensure the package contains the expected DLL structure.");
        }

        // Fail closed: never hand signtool a freshly downloaded dlib whose content does not match a
        // pinned hash. A mismatch here means the feed/CDN served something other than the pinned,
        // known-good payload.
        if (!DlibIntegrityVerifier(dlibPath))
        {
            throw new InvalidOperationException(
                $"The downloaded {ArtifactSigningClientPackage} {ArtifactSigningClientVersion} dlib failed integrity verification " +
                $"(unexpected SHA-256 for {dlibPath.Name}). Refusing to use it; the NuGet feed or CDN may be compromised or mirrored.");
        }

        return dlibPath;
    }

    /// <summary>
    /// Returns <c>true</c> only when the DLL's SHA-256 matches one of the compiled-in hashes for the
    /// pinned package version. This authenticates the exact executable payload signtool loads,
    /// closing the gap that the flat-container downloader (HTTPS + unzip, no NuGet signature check)
    /// leaves open. Reads the file fully and hashes it; failures to read are treated as untrusted.
    /// </summary>
    private static bool DefaultIsTrustedDlib(FileInfo dlib)
    {
        try
        {
            using var stream = dlib.OpenRead();
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            return TrustedDlibSha256.Contains(hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cannot read the file to hash it (locked, missing, or access denied) — treat as untrusted.
            return false;
        }
    }

    /// <summary>
    /// Deletes a cached package version directory when it exists but does not contain the expected
    /// dlib — the signature of a failed or cancelled prior extraction. Leaving it in place would let
    /// <c>EnsurePackageAsync</c> treat the package as already installed and skip re-downloading,
    /// so every later run would fail without recovering. Best-effort: failures are logged, not thrown.
    /// </summary>
    private void RemovePoisonedVersionDir(string version, TaskContext taskContext)
    {
        try
        {
            var nugetCache = nugetService.GetNuGetGlobalPackagesDir();
            var versionDir = new DirectoryInfo(Path.Join(nugetCache.FullName, ArtifactSigningClientPackage.ToLowerInvariant(), version));
            if (!versionDir.Exists)
            {
                return;
            }

            // If the dlib is already present the cache is healthy — nothing to clean up.
            if (FindTrustedSigningDlib(version) != null)
            {
                return;
            }

            taskContext.AddDebugMessage($"Removing incomplete cached package at {versionDir.FullName} before reinstalling.");
            versionDir.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: if cleanup fails, the subsequent install/find will surface a clear error.
            taskContext.AddDebugMessage($"Could not clean up incomplete package cache: {ex.Message}");
        }
    }

    /// <summary>
    /// Unconditionally deletes the cached package version directory (best-effort). Used when a cached
    /// dlib fails integrity verification and must be removed before a clean reinstall.
    /// </summary>
    private void RemoveVersionDir(string version, TaskContext taskContext)
    {
        try
        {
            var nugetCache = nugetService.GetNuGetGlobalPackagesDir();
            var versionDir = new DirectoryInfo(Path.Join(nugetCache.FullName, ArtifactSigningClientPackage.ToLowerInvariant(), version));
            if (versionDir.Exists)
            {
                versionDir.Delete(recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            taskContext.AddDebugMessage($"Could not remove cached package directory: {ex.Message}");
        }
    }

    private FileInfo? FindTrustedSigningDlib(string? version = null)
    {
        var nugetCache = nugetService.GetNuGetGlobalPackagesDir();
        var packageDir = new DirectoryInfo(Path.Join(nugetCache.FullName, ArtifactSigningClientPackage.ToLowerInvariant()));

        if (!packageDir.Exists)
        {
            return null;
        }

        IEnumerable<DirectoryInfo> versionDirs;
        if (!string.IsNullOrEmpty(version))
        {
            // Prefer the exact pinned version so the loaded DLL is reproducible.
            var exactDir = new DirectoryInfo(Path.Join(packageDir.FullName, version));
            if (!exactDir.Exists)
            {
                return null;
            }
            versionDirs = [exactDir];
        }
        else
        {
            // Fall back to the highest installed version using semantic (not lexicographic) ordering.
            versionDirs = packageDir.GetDirectories()
                .OrderByDescending(d => d.Name, Comparer<string>.Create(NugetService.CompareVersions));
        }

        foreach (var versionDir in versionDirs)
        {
            // The dlib is at: bin/x64/Azure.CodeSigning.Dlib.dll (x64 works on ARM64 via emulation)
            var dlibFile = new FileInfo(Path.Join(versionDir.FullName, "bin", "x64", "Azure.CodeSigning.Dlib.dll"));
            if (dlibFile.Exists)
            {
                return dlibFile;
            }

            // Fallback: search recursively for the DLL, but never an x86 build. winapp ships and
            // documents x64/arm64 runtime prerequisites only (x64 runs on ARM64 via emulation), so
            // handing signtool an x86 dlib would load an architecture with no matching .NET/VC++
            // runtime on the machine. Exclude any candidate under an "x86" directory.
            var found = versionDir.GetFiles("Azure.CodeSigning.Dlib.dll", SearchOption.AllDirectories)
                .FirstOrDefault(f => !IsX86Dlib(f));
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns true when the resolved dlib lives under an <c>x86</c> directory (e.g. <c>bin/x86</c>).
    /// winapp only ships/documents x64 and arm64 runtime prerequisites, so an x86 dlib must never be
    /// handed to signtool even though the recursive fallback might otherwise find one.
    /// </summary>
    private static bool IsX86Dlib(FileInfo dlibFile)
    {
        for (var dir = dlibFile.Directory; dir != null; dir = dir.Parent)
        {
            if (string.Equals(dir.Name, "x86", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
