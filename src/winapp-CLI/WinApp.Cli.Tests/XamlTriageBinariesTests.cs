// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.InteropServices;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Unit tests for <see cref="XamlTriageBinaries"/> debugger-layout resolution, engine/provider
/// compatibility, and global-cache copy logic. All probing is satisfied from local disk; the download
/// core is covered separately (offline) in <see cref="XamlTriageBinariesDownloadTests"/> via the
/// <c>HttpGetAsync</c> seam. Marked <c>[DoNotParallelize]</c> because it mutates the process-wide
/// <see cref="XamlTriageBinaries.EnvOverride"/> environment variable.
/// </summary>
/// <remarks>
/// <para><b>Documented coverage ceiling (~96% Debug line coverage across the file).</b> The remaining
/// uncovered lines require a foreign CPU architecture, real network I/O, or a faulting file handle, none
/// of which can be produced deterministically here; per policy they are left honestly uncovered rather
/// than excluded. Current uncovered ranges and why:</para>
/// <list type="bullet">
///   <item>69 — the <c>HttpGetAsync</c> seam's default body (the real <c>HttpClient.GetAsync</c>): the OS
///   network boundary, replaced by a stub in every test.</item>
///   <item>291-293, 316-318 — the <c>TryGetProductVersion</c> and <c>IsUsablePeFile</c> catch blocks:
///   reached only if reading an existing file throws (e.g. a locked handle); defensive, and forcing it
///   would be a TOCTOU/flaky test.</item>
///   <item>398-400 — the successful <c>.nupkg</c> download-and-verify tail: needs a real package whose
///   bytes hash to the compiled-in pinned SHA-512, i.e. real network content.</item>
///   <item>475-477 — the "downloaded version != pinned version" refusal in
///   <c>TryMaterializePackageAsync</c>: a deliberate defense-in-depth security guard.
///   <c>ResolveDownloadVersionAsync</c> only ever returns the pinned version or <c>null</c>, so no caller
///   can currently trigger it; it is kept (not deleted) because it guards native code loaded into the
///   debugger against a future change to the version-resolution contract.</item>
/// </list>
/// </remarks>
[TestClass]
[DoNotParallelize]
public class XamlTriageBinariesTests
{
    private string _tempDir = null!;
    private string? _originalOverride;

    // Pass-through validator for tests that use dummy (unsigned) binary files: resolution logic is under
    // test here, not the real Authenticode/version gate (covered by AuthenticodeVerifierTests, the L4
    // test, and the VersionsMatch tests below).
    private static readonly Func<ResolvedTriageBinaries, bool> AcceptAny = _ => true;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"XamlTriageBin_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _originalOverride = Environment.GetEnvironmentVariable(XamlTriageBinaries.EnvOverride);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, _originalOverride);
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [TestMethod]
    public void ResolveExisting_OverrideToEmptyDir_ReturnsNull()
    {
        var emptyDir = Path.Combine(_tempDir, "empty");
        Directory.CreateDirectory(emptyDir);
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, emptyDir);

        var resolved = XamlTriageBinaries.ResolveExisting(new DirectoryInfo(_tempDir), NullLogger.Instance);

        Assert.IsNull(resolved, "An override pointing at a directory without dbgeng.dll must not resolve.");
    }

    [TestMethod]
    public void ResolveExisting_FullLayout_ResolvesWithSymSrv()
    {
        var dir = Path.Combine(_tempDir, "full");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "dbgeng.dll"), "");
        File.WriteAllText(Path.Combine(dir, "JsProvider.dll"), "");
        File.WriteAllText(Path.Combine(dir, "symsrv.dll"), "");
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, dir);

        var resolved = XamlTriageBinaries.ResolveExisting(new DirectoryInfo(_tempDir), NullLogger.Instance, AcceptAny);

        Assert.IsNotNull(resolved);
        Assert.AreEqual(dir, resolved.BinDir);
        Assert.IsTrue(resolved.HasSymSrv, "symsrv.dll is present, so HasSymSrv must be true.");
    }

    [TestMethod]
    public void ResolveExisting_JsProviderInWinext_ResolvesWithoutSymSrv()
    {
        var dir = Path.Combine(_tempDir, "winext-layout");
        Directory.CreateDirectory(Path.Combine(dir, "winext"));
        File.WriteAllText(Path.Combine(dir, "dbgeng.dll"), "");
        File.WriteAllText(Path.Combine(dir, "winext", "JsProvider.dll"), "");
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, dir);

        var resolved = XamlTriageBinaries.ResolveExisting(new DirectoryInfo(_tempDir), NullLogger.Instance, AcceptAny);

        Assert.IsNotNull(resolved);
        Assert.IsFalse(resolved.HasSymSrv, "No symsrv.dll present, so HasSymSrv must be false.");
        Assert.AreEqual(Path.Combine(dir, "winext", "JsProvider.dll"), resolved.JsProviderPath,
            "The resolved JsProvider path must point at the winext copy so the child runner can .load it.");
    }

    [TestMethod]
    public void DescribeOverrideGap_NoOverride_ReturnsNull()
    {
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, null);

        Assert.IsNull(XamlTriageBinaries.DescribeOverrideGap());
    }

    [TestMethod]
    public void DescribeOverrideGap_MissingDirectory_ReportsNonexistent()
    {
        var missing = Path.Combine(_tempDir, "does-not-exist");
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, missing);

        var gap = XamlTriageBinaries.DescribeOverrideGap();

        Assert.IsNotNull(gap);
        StringAssert.Contains(gap, "does not exist");
        StringAssert.Contains(gap, missing);
    }

    [TestMethod]
    public void DescribeOverrideGap_EmptyDir_ListsBothMissingComponents()
    {
        var dir = Path.Combine(_tempDir, "override-empty");
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, dir);

        var gap = XamlTriageBinaries.DescribeOverrideGap();

        Assert.IsNotNull(gap);
        StringAssert.Contains(gap, "dbgeng.dll");
        StringAssert.Contains(gap, "JsProvider.dll");
    }

    [TestMethod]
    public void DescribeOverrideGap_EngineOnly_ListsOnlyJsProvider()
    {
        var dir = Path.Combine(_tempDir, "override-engine-only");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "dbgeng.dll"), "");
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, dir);

        var gap = XamlTriageBinaries.DescribeOverrideGap();

        Assert.IsNotNull(gap);
        StringAssert.Contains(gap, "JsProvider.dll");
        Assert.IsFalse(gap.Contains("dbgeng.dll"), "dbgeng.dll is present, so it must not be listed as missing.");
    }

    [TestMethod]
    public void DescribeOverrideGap_FullLayout_ReturnsNull()
    {
        var dir = Path.Combine(_tempDir, "override-full");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "dbgeng.dll"), "");
        File.WriteAllText(Path.Combine(dir, "JsProvider.dll"), "");
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, dir);

        Assert.IsNull(XamlTriageBinaries.DescribeOverrideGap(),
            "A complete override layout has no gap to describe.");
    }

    [TestMethod]
    public void ResolveExisting_JsProviderFailsVerification_ReturnsNull()
    {
        // L4: a full layout on disk whose JsProvider.dll fails Authenticode verification (e.g. it was
        // replaced in the cache after download) must be rejected rather than loaded into the debugger.
        var dir = Path.Combine(_tempDir, "tampered");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "dbgeng.dll"), "");
        File.WriteAllText(Path.Combine(dir, "JsProvider.dll"), "");
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, dir);

        var resolved = XamlTriageBinaries.ResolveExisting(new DirectoryInfo(_tempDir), NullLogger.Instance, _ => false);

        Assert.IsNull(resolved, "A JsProvider.dll that fails signature verification must not resolve.");
    }

    [TestMethod]
    [DataRow("dbgeng.dll")]
    [DataRow("dbghelp.dll")]
    [DataRow("dbgcore.dll")]
    [DataRow("dbgmodel.dll")]
    [DataRow("msdia140.dll")]
    [DataRow("symsrv.dll")]
    [DataRow("winext/JsProvider.dll")]
    [DataRow("extra.dll")]
    public void LocalCacheLayout_RejectsAnyUntrustedLoadable(string untrusted)
    {
        var files = new[] { "dbgeng.dll", "dbghelp.dll", "dbgcore.dll", "dbgmodel.dll",
            "msdia140.dll", "symsrv.dll", "winext/JsProvider.dll", "extra.dll" };
        Directory.CreateDirectory(Path.Combine(_tempDir, "winext"));
        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(_tempDir, file), "fixture");
        }
        var untrustedPath = Path.GetFullPath(Path.Combine(_tempDir, untrusted));
        var verified = new List<string>();

        var trusted = XamlTriageBinaries.IsTrustedCacheLayout(_tempDir, NullLogger.Instance,
            (path, _) => { verified.Add(path); return path != untrustedPath; });

        Assert.IsFalse(trusted, "A valid provider or matching editable version must not authorize another DLL.");
        CollectionAssert.Contains(verified, untrustedPath);
    }

    [TestMethod]
    public void LocalCacheLayout_AcceptsAuthenticatedCompleteLayout()
    {
        var files = new[] { "dbgeng.dll", "dbghelp.dll", "dbgcore.dll", "dbgmodel.dll", "msdia140.dll", "JsProvider.dll" };
        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(_tempDir, file), "fixture");
        }
        var verified = new List<string>();

        Assert.IsTrue(XamlTriageBinaries.IsTrustedCacheLayout(_tempDir, NullLogger.Instance,
            (path, _) => { verified.Add(Path.GetFileName(path)); return true; }));
        CollectionAssert.AreEquivalent(files, verified);
    }

    [TestMethod]
    public void LocalCacheLayout_MissingRequiredEngineDependency_IsRejected()
    {
        File.WriteAllText(Path.Combine(_tempDir, "dbgeng.dll"), "fixture");
        File.WriteAllText(Path.Combine(_tempDir, "JsProvider.dll"), "fixture");

        Assert.IsFalse(XamlTriageBinaries.IsTrustedCacheLayout(_tempDir, NullLogger.Instance, (_, _) => true));
    }

    [TestMethod]
    public void ResolveExisting_JsProviderInRoot_PrefersRootPath()
    {
        var dir = Path.Combine(_tempDir, "root-layout");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "dbgeng.dll"), "");
        File.WriteAllText(Path.Combine(dir, "JsProvider.dll"), "");
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, dir);

        var resolved = XamlTriageBinaries.ResolveExisting(new DirectoryInfo(_tempDir), NullLogger.Instance, AcceptAny);

        Assert.IsNotNull(resolved);
        Assert.AreEqual(Path.Combine(dir, "JsProvider.dll"), resolved.JsProviderPath);
    }

    [TestMethod]
    public void ResolveExisting_MissingJsProvider_ReturnsNull()
    {
        var dir = Path.Combine(_tempDir, "no-jsprovider");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "dbgeng.dll"), "");
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, dir);

        var resolved = XamlTriageBinaries.ResolveExisting(new DirectoryInfo(_tempDir), NullLogger.Instance);

        Assert.IsNull(resolved, "Without JsProvider.dll the JS extension cannot load, so resolution must fail.");
    }

    [TestMethod]
    public void ArchTokens_AreNonEmpty()
    {
        Assert.IsFalse(string.IsNullOrWhiteSpace(XamlTriageBinaries.KitsArch));
        Assert.IsFalse(string.IsNullOrWhiteSpace(XamlTriageBinaries.NuGetArch));
    }

    [TestMethod]
    [DataRow(Architecture.X64, "x64")]
    [DataRow(Architecture.Arm64, "arm64")]
    [DataRow(Architecture.X86, "x86")]
    [DataRow((Architecture)999, "x64", DisplayName = "Unknown arch falls back to x64")]
    public void KitsArchFor_MapsEveryArchitecture(Architecture arch, string expected)
    {
        // M3: the Windows Kits Debuggers folder-token switch is a pure decision, coverable off-host for
        // every arm (including the Arm64/X86/fallback arms unreachable on this x64 host at runtime).
        Assert.AreEqual(expected, XamlTriageBinaries.KitsArchFor(arch));
    }

    [TestMethod]
    [DataRow(Architecture.X64, "amd64")]
    [DataRow(Architecture.Arm64, "arm64")]
    [DataRow(Architecture.X86, "x86")]
    [DataRow((Architecture)999, "amd64", DisplayName = "Unknown arch falls back to amd64")]
    public void NuGetArchFor_MapsEveryArchitecture(Architecture arch, string expected)
    {
        // M3: the NuGet debugging-package folder-token switch, covered for every arm off-host.
        Assert.AreEqual(expected, XamlTriageBinaries.NuGetArchFor(arch));
    }

    [TestMethod]
    [DataRow("10.0.29547.1002", "10.0.29547.1002", true, DisplayName = "Identical")]
    [DataRow("10.0.29547.1002 (WinBuild.160101.0800)", "10.0.29547.1002", true, DisplayName = "Trailing FileVersion decoration ignored")]
    [DataRow("10.0.29547.1002", "10.0.29617.1000", false, DisplayName = "Different build")]
    [DataRow(null, "10.0.29547.1002", false, DisplayName = "Null engine version")]
    [DataRow("10.0.29547.1002", null, false, DisplayName = "Null provider version")]
    [DataRow("not-a-version", "10.0.29547.1002", false, DisplayName = "Unparseable")]
    public void VersionsMatch_ComparesNumericComponent(string? a, string? b, bool expected)
    {
        Assert.AreEqual(expected, XamlTriageBinaries.VersionsMatch(a, b));
    }

    [TestMethod]
    public void PinnedJsProviderProductVersion_MatchesRestoredEngineBuild()
    {
        // Drift guard mirroring the .nupkg SHA-512 pins: the JsProvider bundle build MUST equal the
        // engine build shipped by the pinned Microsoft.Debugging.Platform.DbgEng NuGet package —
        // loading a mismatched provider crashes the triage child with STATUS_BREAKPOINT, and the
        // runtime compat gate then fail-closes triage. Rather than compare two hand-maintained
        // constants (which wouldn't notice a DbgPackageVersion bump that ships a new engine build),
        // read the *actual* dbgeng.dll product version from the restored package so a bump that forgets
        // to re-pin PinnedBundleUrl + PinnedJsProviderProductVersion is caught here. The package is a
        // restore-only PackageReference, so its content is in the NuGet global cache on a build/CI
        // machine; if it can't be located (restored elsewhere), the assertion is inconclusive.
        var cache = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrEmpty(cache))
        {
            cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        }

        var dbgeng = Path.Combine(
            cache, "microsoft.debugging.platform.dbgeng", XamlTriageBinaries.DbgPackageVersion,
            "content", XamlTriageBinaries.NuGetArch, "dbgeng.dll");
        if (!File.Exists(dbgeng))
        {
            Assert.Inconclusive($"Restored DbgEng package not found in NuGet cache: {dbgeng}");
        }

        var engineBuild = System.Diagnostics.FileVersionInfo.GetVersionInfo(dbgeng).ProductVersion;
        Assert.IsTrue(
            XamlTriageBinaries.VersionsMatch(engineBuild, WinDbgJsProviderAcquirer.PinnedJsProviderProductVersion),
            $"JsProvider bundle build drifted from the engine: dbgeng.dll (pinned DbgEng {XamlTriageBinaries.DbgPackageVersion}) " +
            $"reports {engineBuild ?? "<unreadable>"}, but PinnedJsProviderProductVersion is {WinDbgJsProviderAcquirer.PinnedJsProviderProductVersion}. " +
            "Update PinnedBundleUrl to a WinDbg bundle whose JsProvider matches the engine, and update PinnedJsProviderProductVersion.");
    }

    [TestMethod]
    public void IsEnvOverrideSet_ReflectsEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, null);
        Assert.IsFalse(XamlTriageBinaries.IsEnvOverrideSet, "No override set: IsEnvOverrideSet must be false.");

        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, "   ");
        Assert.IsFalse(XamlTriageBinaries.IsEnvOverrideSet, "Whitespace-only override must be treated as unset.");

        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, _tempDir);
        Assert.IsTrue(XamlTriageBinaries.IsEnvOverrideSet, "A non-empty override must report as set.");
    }

    [TestMethod]
    public void TryCopyFromGlobalCache_PinnedVersionPresent_CopiesFromPinned()
    {
        const string package = "Test.Package.Bits";
        const string pinned = "2.0.0";
        var cache = new DirectoryInfo(Path.Combine(_tempDir, "cache"));
        // Pinned version + a numerically newer version; the newer one must be ignored.
        WriteCachePackage(cache, package, pinned, "engine.dll", "pinned");
        WriteCachePackage(cache, package, "9.9.9", "engine.dll", "newer");
        var binDir = new DirectoryInfo(Path.Combine(_tempDir, "bin"));
        binDir.Create();

        var ok = XamlTriageBinaries.TryCopyFromGlobalCache(
            package, pinned, ["engine.dll"], cache, binDir, NullLogger.Instance);

        Assert.IsTrue(ok);
        Assert.AreEqual("pinned", File.ReadAllText(Path.Combine(binDir.FullName, "engine.dll")),
            "The pinned version must win even when a higher version number exists in the cache.");
    }

    [TestMethod]
    public void TryCopyFromGlobalCache_PinnedAbsent_FallsBackToNewest()
    {
        const string package = "Test.Package.Bits";
        var cache = new DirectoryInfo(Path.Combine(_tempDir, "cache"));
        WriteCachePackage(cache, package, "1.0.0", "engine.dll", "older");
        WriteCachePackage(cache, package, "1.5.0", "engine.dll", "newer");
        var binDir = new DirectoryInfo(Path.Combine(_tempDir, "bin"));
        binDir.Create();

        var ok = XamlTriageBinaries.TryCopyFromGlobalCache(
            package, "2.0.0", ["engine.dll"], cache, binDir, NullLogger.Instance);

        Assert.IsTrue(ok, "When the pinned version is missing, the newest cached version is an acceptable fallback.");
        Assert.AreEqual("newer", File.ReadAllText(Path.Combine(binDir.FullName, "engine.dll")));
    }

    [TestMethod]
    public void DbgPackageVersion_MatchesDirectoryPackagesProps()
    {
        var propsPath = FindUpwards("Directory.Packages.props",
            p => File.ReadAllText(p).Contains("Microsoft.Debugging.Platform.DbgEng", StringComparison.Ordinal));
        Assert.IsNotNull(propsPath, "Could not locate the Directory.Packages.props that pins the DbgEng package.");

        var text = File.ReadAllText(propsPath);
        var match = System.Text.RegularExpressions.Regex.Match(
            text, "Microsoft\\.Debugging\\.Platform\\.DbgEng\"\\s+Version=\"([^\"]+)\"");
        Assert.IsTrue(match.Success, "Could not find the DbgEng PackageVersion entry in Directory.Packages.props.");
        Assert.AreEqual(XamlTriageBinaries.DbgPackageVersion, match.Groups[1].Value,
            "XamlTriageBinaries.DbgPackageVersion drifted from the version pinned in Directory.Packages.props.");
    }

    [TestMethod]
    public void VerifyPackageHash_MatchingSha512_ReturnsTrue()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("winapp-dbgtools-package-content");
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA512.HashData(bytes));

        Assert.IsTrue(XamlTriageBinaries.VerifyPackageHash(bytes, expected),
            "The exact pinned content hash must verify.");
        Assert.IsTrue(XamlTriageBinaries.VerifyPackageHash(bytes, expected.ToLowerInvariant()),
            "Hash comparison must be case-insensitive so lower-case hex pins also verify.");
    }

    [TestMethod]
    public void VerifyPackageHash_TamperedContent_ReturnsFalse()
    {
        var original = System.Text.Encoding.UTF8.GetBytes("winapp-dbgtools-package-content");
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA512.HashData(original));
        var tampered = System.Text.Encoding.UTF8.GetBytes("winapp-dbgtools-package-contenX");

        Assert.IsFalse(XamlTriageBinaries.VerifyPackageHash(tampered, expected),
            "A single altered byte must fail the integrity check so mirrored/compromised feeds are rejected.");
    }

    [TestMethod]
    public void PinnedPackages_Sha512_MatchesRestoredNupkg()
    {
        // Guards against a mistyped or stale pinned hash: the packages are restore-only PackageReferences,
        // so their .nupkg is present in the NuGet global cache. If this can't be located (e.g. a clean
        // machine that restored elsewhere), the assertion is inconclusive rather than a false failure.
        var cache = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrEmpty(cache))
        {
            cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        }

        foreach (var (package, version, expectedSha) in XamlTriageBinaries.PinnedPackages)
        {
            var id = package.ToLowerInvariant();
            var nupkg = Path.Combine(cache, id, version, $"{id}.{version}.nupkg");
            if (!File.Exists(nupkg))
            {
                Assert.Inconclusive($"Pinned package not found in NuGet cache: {nupkg}");
            }

            var actual = Convert.ToHexString(System.Security.Cryptography.SHA512.HashData(File.ReadAllBytes(nupkg)));
            Assert.IsTrue(StringComparer.OrdinalIgnoreCase.Equals(expectedSha, actual),
                $"Pinned SHA-512 for {package} {version} drifted from the restored .nupkg. Expected {expectedSha}, got {actual.ToLowerInvariant()}. Update the compiled-in hash.");
        }
    }

    private static void WriteCachePackage(DirectoryInfo cache, string package, string version, string file, string content)
    {
        var archDir = Path.Combine(cache.FullName, package.ToLowerInvariant(), version, "content", XamlTriageBinaries.NuGetArch);
        Directory.CreateDirectory(archDir);
        File.WriteAllText(Path.Combine(archDir, file), content);
    }

    [TestMethod]
    public void ResolveExisting_NoOverride_RejectsInstalledRootsThenFallsBackToCache()
    {
        // No override: CandidateDirectories enumerates the installed Debugging-Tools roots
        // (Program Files\Windows Kits\10\Debuggers\<arch>) and finally the download-on-first-use
        // cache. A validator that only accepts the seeded cache forces the full traversal — each
        // installed root that resolves is rejected (or is absent) before the cache fallback resolves.
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, null);
        var cache = new DirectoryInfo(Path.Combine(_tempDir, "cache-bin"));
        cache.Create();
        File.WriteAllText(Path.Combine(cache.FullName, "dbgeng.dll"), "");
        File.WriteAllText(Path.Combine(cache.FullName, "JsProvider.dll"), "");

        Func<ResolvedTriageBinaries, bool> onlyCache =
            r => r.JsProviderPath.StartsWith(cache.FullName, StringComparison.OrdinalIgnoreCase);

        var resolved = XamlTriageBinaries.ResolveExisting(cache, NullLogger.Instance, onlyCache);

        Assert.IsNotNull(resolved,
            "After rejecting/exhausting the installed roots, the candidate walk must fall back to the seeded cache.");
        StringAssert.StartsWith(resolved.JsProviderPath, cache.FullName);
        StringAssert.EndsWith(resolved.JsProviderPath, "JsProvider.dll");
    }

    [TestMethod]
    public void ResolveExisting_OverrideToNonexistentDir_ReturnsNull()
    {
        // An explicit override is authoritative and the only candidate considered. When it points at a
        // path that does not exist, TryDirectory short-circuits on the Directory.Exists check and nothing
        // resolves (covers the missing-directory branch deterministically, without env-var mutation).
        var missing = Path.Combine(_tempDir, "no-such-dir");
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, missing);

        var resolved = XamlTriageBinaries.ResolveExisting(new DirectoryInfo(_tempDir), NullLogger.Instance, AcceptAny);

        Assert.IsNull(resolved, "A non-existent override directory must resolve to null.");
    }

    [TestMethod]
    public void ResolveExisting_NoOverride_EveryCandidateRejected_ReturnsNull()
    {
        // No debugger anywhere the walk trusts: with no override and a reject-everything validator, the
        // candidate walk must enumerate every installed root and the cache, reject each, and return null
        // (the "nothing usable found" workflow — the caller then falls back to the download path).
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, null);
        var cache = new DirectoryInfo(Path.Combine(_tempDir, "empty-cache"));
        cache.Create();

        var resolved = XamlTriageBinaries.ResolveExisting(cache, NullLogger.Instance, _ => false);

        Assert.IsNull(resolved, "When every candidate is rejected, resolution must return null.");
    }

    [TestMethod]
    public void ResolveExisting_PublicOverload_UnsignedProvider_IsRejected()
    {
        // The public overload wires the real Authenticode + engine-version validator. A full but
        // unsigned dummy layout must fail IsTrustedMicrosoftSigned and be rejected, so nothing resolves.
        var dir = Path.Combine(_tempDir, "unsigned-full");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "dbgeng.dll"), "");
        File.WriteAllText(Path.Combine(dir, "JsProvider.dll"), "");
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, dir);

        var resolved = XamlTriageBinaries.ResolveExisting(new DirectoryInfo(_tempDir), NullLogger.Instance);

        Assert.IsNull(resolved,
            "An unsigned JsProvider.dll must fail the real signature validator and not resolve.");
    }

    [TestMethod]
    public void IsProviderCompatibleWithEngine_MatchingProductVersion_ReturnsTrue()
    {
        // Two copies of the same real, versioned system DLL stand in for an engine/provider pair
        // built from the same source: their product versions match, so the pair is compatible.
        var binDir = Path.Combine(_tempDir, "compat-match");
        Directory.CreateDirectory(binDir);
        var versionedDll = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        var provider = Path.Combine(binDir, "JsProvider.dll");
        File.Copy(versionedDll, Path.Combine(binDir, "dbgeng.dll"), overwrite: true);
        File.Copy(versionedDll, provider, overwrite: true);

        Assert.IsTrue(
            XamlTriageBinaries.IsProviderCompatibleWithEngine(binDir, provider, NullLogger.Instance),
            "An engine and provider reporting the same product version must be treated as compatible.");
    }

    [TestMethod]
    public void IsProviderCompatibleWithEngine_UnreadableVersions_ReturnsFalse()
    {
        // Neither dummy file carries a version resource, so both product versions read back as null.
        // A pair whose build cannot be confirmed to match must be rejected (a mismatch crashes triage).
        var binDir = Path.Combine(_tempDir, "compat-unreadable");
        Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(binDir, "dbgeng.dll"), "not a real pe image");
        var provider = Path.Combine(binDir, "JsProvider.dll");
        File.WriteAllText(provider, "not a real pe image");

        Assert.IsFalse(
            XamlTriageBinaries.IsProviderCompatibleWithEngine(binDir, provider, NullLogger.Instance),
            "When neither file exposes a readable product version, the provider must be treated as incompatible.");
    }

    [TestMethod]
    public void TryCopyFromGlobalCache_PackageDirAbsent_ReturnsFalse()
    {
        // The package id has no directory in the global cache at all -> nothing to copy.
        var cache = new DirectoryInfo(Path.Combine(_tempDir, "empty-cache"));
        cache.Create();
        var binDir = new DirectoryInfo(Path.Combine(_tempDir, "bin"));
        binDir.Create();

        var ok = XamlTriageBinaries.TryCopyFromGlobalCache(
            "Absent.Package", "1.0.0", ["engine.dll"], cache, binDir, NullLogger.Instance);

        Assert.IsFalse(ok, "A package absent from the global cache must not report a successful copy.");
    }

    [TestMethod]
    public void TryCopyFromGlobalCache_VersionPresentButArchFilesMissing_ReturnsFalse()
    {
        // A version directory exists but has no content/<arch> payload, so the candidate is skipped and
        // (with no other version to fall back to) the copy fails.
        const string package = "Test.Package.Bits";
        const string pinned = "3.1.4";
        var cache = new DirectoryInfo(Path.Combine(_tempDir, "cache"));
        Directory.CreateDirectory(Path.Combine(cache.FullName, package.ToLowerInvariant(), pinned));
        var binDir = new DirectoryInfo(Path.Combine(_tempDir, "bin"));
        binDir.Create();

        var ok = XamlTriageBinaries.TryCopyFromGlobalCache(
            package, pinned, ["engine.dll"], cache, binDir, NullLogger.Instance);

        Assert.IsFalse(ok, "A version directory lacking the arch payload must be skipped, yielding no copy.");
    }

    private static string? FindUpwards(string fileName, Func<string, bool> predicate)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate))
            {
                try
                {
                    if (predicate(candidate))
                    {
                        return candidate;
                    }
                }
                catch (IOException) { }
            }

            dir = dir.Parent;
        }

        return null;
    }
}
