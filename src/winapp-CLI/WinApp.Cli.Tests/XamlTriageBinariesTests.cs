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

    // Engine files a full NuGet-cache layout holds alongside dbgeng.dll.
    private static readonly string[] EngineFiles = ["dbgeng.dll", "dbghelp.dll", "dbgcore.dll", "dbgmodel.dll", "msdia140.dll", "symsrv.dll"];

    /// <summary>
    /// Resolves with the signature and engine-build gates replaced, for tests that use dummy (unsigned)
    /// binary files: resolution and holding are under test here, not the real Authenticode/version gate
    /// (covered by AuthenticodeVerifierTests, the public-overload test, and the VersionsMatch tests below).
    /// </summary>
    private static ResolvedTriageBinaries? Resolve(
        DirectoryInfo cacheBinDir, Func<string, bool>? signed = null, Func<string, string, bool>? compatible = null) =>
        XamlTriageBinaries.ResolveExisting(
            cacheBinDir,
            NullLogger.Instance,
            (path, _) => signed?.Invoke(path) ?? true,
            (binDir, jsProvider, _) => compatible?.Invoke(binDir, jsProvider) ?? true);

    /// <summary>
    /// Where a file really lives, as the resolver reports it. Temp paths can contain 8.3 short names
    /// (as on CI), which the resolver expands.
    /// </summary>
    private static string RealPath(string path)
    {
        using var held = VerifiedTool.Open(new FileInfo(path), (_, _) => true, NullLogger.Instance);
        return held.Path;
    }

    private static bool CanBeReplaced(string path)
    {
        try
        {
            using var writable = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Lays out every engine file plus <c>winext\JsProvider.dll</c> and returns all their paths.</summary>
    private static List<string> WriteFullLayout(string dir)
    {
        Directory.CreateDirectory(Path.Combine(dir, "winext"));
        var files = EngineFiles.Select(f => Path.Combine(dir, f)).Append(Path.Combine(dir, "winext", "JsProvider.dll")).ToList();
        foreach (var file in files)
        {
            File.WriteAllText(file, Path.GetFileName(file));
        }

        return files;
    }

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

        using var resolved = Resolve(new DirectoryInfo(_tempDir));

        Assert.IsNotNull(resolved);
        Assert.AreEqual(Path.GetDirectoryName(RealPath(Path.Combine(dir, "dbgeng.dll"))), resolved.BinDir);
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

        using var resolved = Resolve(new DirectoryInfo(_tempDir));

        Assert.IsNotNull(resolved);
        Assert.IsFalse(resolved.HasSymSrv, "No symsrv.dll present, so HasSymSrv must be false.");
        Assert.AreEqual(RealPath(Path.Combine(dir, "winext", "JsProvider.dll")), resolved.JsProviderPath,
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

        var resolved = Resolve(new DirectoryInfo(_tempDir), signed: _ => false);

        Assert.IsNull(resolved, "A JsProvider.dll that fails signature verification must not resolve.");
    }

    [TestMethod]
    public void ResolveExisting_JsProviderInRoot_PrefersRootPath()
    {
        var dir = Path.Combine(_tempDir, "root-layout");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "dbgeng.dll"), "");
        File.WriteAllText(Path.Combine(dir, "JsProvider.dll"), "");
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, dir);

        using var resolved = Resolve(new DirectoryInfo(_tempDir));

        Assert.IsNotNull(resolved);
        Assert.AreEqual(RealPath(Path.Combine(dir, "JsProvider.dll")), resolved.JsProviderPath);
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
    public void ResolveExisting_WhileResolved_EveryDllTheChildLoadsIsLocked()
    {
        // The triage child loads these files by path after resolution returns. If any of them could be
        // replaced in between, whoever can write the cache would choose what runs in the debugger.
        var dir = Path.Combine(_tempDir, "held");
        var files = WriteFullLayout(dir);
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, dir);

        using (var resolved = Resolve(new DirectoryInfo(_tempDir)))
        {
            Assert.IsNotNull(resolved);
            foreach (var file in files)
            {
                Assert.IsFalse(CanBeReplaced(file), $"{Path.GetFileName(file)} must not be writable while triage uses it.");
                Assert.ThrowsExactly<IOException>(() => File.Delete(file), $"{Path.GetFileName(file)} must not be deletable while triage uses it.");
            }

            Assert.ThrowsExactly<IOException>(() => Directory.Move(dir, dir + "-old"),
                "Renaming the engine directory aside would let a replacement take its path.");
        }

        foreach (var file in files)
        {
            Assert.IsTrue(CanBeReplaced(file), $"{Path.GetFileName(file)} must be released once triage is done, so the cache stays usable.");
        }
    }

    [TestMethod]
    public void ResolveExisting_VerifiesEveryDllTheChildLoads_ByThePathItReports()
    {
        var dir = Path.Combine(_tempDir, "verify-all");
        var files = WriteFullLayout(dir);
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, dir);
        var checkedPaths = new List<string>();

        using var resolved = Resolve(new DirectoryInfo(_tempDir), signed: path => { checkedPaths.Add(path); return true; });

        Assert.IsNotNull(resolved);
        CollectionAssert.AreEquivalent(files.Select(RealPath).ToList(), checkedPaths,
            "Every engine DLL and JsProvider.dll must pass the signature check, each by its resolved path.");
        CollectionAssert.Contains(checkedPaths, resolved.JsProviderPath);
        Assert.IsTrue(checkedPaths.All(p => p == resolved.JsProviderPath || Path.GetDirectoryName(p) == resolved.BinDir),
            "The engine files that were checked must be the ones the child loads from BinDir.");
    }

    [TestMethod]
    [DataRow("dbgeng.dll")]
    [DataRow("dbghelp.dll")]
    [DataRow("dbgmodel.dll")]
    [DataRow("msdia140.dll")]
    [DataRow("symsrv.dll")]
    [DataRow("JsProvider.dll")]
    public void ResolveExisting_AnyUnsignedDll_RejectsTheLayoutAndReleasesEverything(string rejectedFile)
    {
        var dir = Path.Combine(_tempDir, "one-unsigned");
        var files = WriteFullLayout(dir);
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, dir);

        var resolved = Resolve(new DirectoryInfo(_tempDir),
            signed: path => !Path.GetFileName(path).Equals(rejectedFile, StringComparison.OrdinalIgnoreCase));

        Assert.IsNull(resolved, $"An unsigned {rejectedFile} is loaded into the debugger, so the layout must be rejected.");
        foreach (var file in files)
        {
            Assert.IsTrue(CanBeReplaced(file), $"A rejected layout must not leave {Path.GetFileName(file)} locked.");
        }
    }

    [TestMethod]
    public void ResolveExisting_ProviderBuildMismatch_RejectsAndReleasesEverything()
    {
        var dir = Path.Combine(_tempDir, "mismatch");
        var files = WriteFullLayout(dir);
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, dir);
        (string BinDir, string JsProvider)? compared = null;

        var resolved = Resolve(new DirectoryInfo(_tempDir), compatible: (binDir, jsProvider) =>
        {
            compared = (binDir, jsProvider);
            return false;
        });

        Assert.IsNull(resolved);
        Assert.AreEqual(Path.GetDirectoryName(RealPath(Path.Combine(dir, "dbgeng.dll"))), compared?.BinDir,
            "The build check must read the held engine, not a path that could since have been re-pointed.");
        Assert.AreEqual(RealPath(Path.Combine(dir, "winext", "JsProvider.dll")), compared?.JsProvider);
        Assert.IsTrue(files.All(CanBeReplaced), "A rejected layout must not stay locked.");
    }

    [TestMethod]
    public void ResolveExisting_ARePointedJunctionCannotSubstituteADifferentLayout()
    {
        // Holding a file pins the file, not the path. A junction on the way can be deleted and re-created
        // while the handles stay valid, so the child must be given the files' own locations.
        var good = Path.Combine(_tempDir, "good");
        var evil = Path.Combine(_tempDir, "evil");
        WriteFullLayout(good);
        WriteFullLayout(evil);
        var junction = Path.Combine(_tempDir, "pkg");
        if (!TryCreateJunction(junction, good))
        {
            Assert.Inconclusive("Could not create a directory junction on this machine.");
        }

        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, junction);
        using var resolved = Resolve(new DirectoryInfo(_tempDir));
        Assert.IsNotNull(resolved);

        Directory.Delete(junction);
        Assert.IsTrue(TryCreateJunction(junction, evil), "Re-pointing the junction must succeed for this test to mean anything.");

        var realGood = Path.GetDirectoryName(RealPath(Path.Combine(good, "dbgeng.dll")));
        Assert.AreEqual(realGood, resolved.BinDir, "The child must load the engine from the verified directory.");
        Assert.AreEqual(RealPath(Path.Combine(good, "winext", "JsProvider.dll")), resolved.JsProviderPath);
    }

    [TestMethod]
    public void ResolveExisting_EngineDllLinkedFromElsewhere_IsRejected()
    {
        // The child loads the engine's companions by name from BinDir. A file symlink there pins only
        // its target, so the link itself could be swapped after the check.
        var dir = Path.Combine(_tempDir, "linked");
        WriteFullLayout(dir);
        var elsewhere = Path.Combine(_tempDir, "elsewhere-dbghelp.dll");
        File.WriteAllText(elsewhere, "dbghelp");
        File.Delete(Path.Combine(dir, "dbghelp.dll"));
        try
        {
            File.CreateSymbolicLink(Path.Combine(dir, "dbghelp.dll"), elsewhere);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive("Creating a file symbolic link needs Developer Mode or elevation on this machine.");
        }

        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, dir);

        var resolved = Resolve(new DirectoryInfo(_tempDir));

        Assert.IsNull(resolved, "An engine file that resolves outside the engine directory must not be trusted.");
        Assert.IsTrue(CanBeReplaced(elsewhere), "A rejected layout must not stay locked.");
    }

    [TestMethod]
    public void ResolveExisting_DbgEngLinkedToADifferentlyNamedFile_IsRejected()
    {
        // The child loads BinDir\dbgeng.dll by name. If dbgeng.dll links to target\other.dll, the held,
        // verified file is other.dll while target\dbgeng.dll stays unpinned and replaceable.
        var dir = Path.Combine(_tempDir, "engine-link");
        WriteFullLayout(dir);
        var target = Path.Combine(_tempDir, "engine-target");
        Directory.CreateDirectory(target);
        var verifiedCopy = Path.Combine(target, "verified-copy.dll");
        File.WriteAllText(verifiedCopy, "dbgeng");
        File.WriteAllText(Path.Combine(target, "dbgeng.dll"), "unverified");
        File.Delete(Path.Combine(dir, "dbgeng.dll"));
        try
        {
            File.CreateSymbolicLink(Path.Combine(dir, "dbgeng.dll"), verifiedCopy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive("Creating a file symbolic link needs Developer Mode or elevation on this machine.");
        }

        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, dir);

        var resolved = Resolve(new DirectoryInfo(_tempDir));

        Assert.IsNull(resolved, "dbgeng.dll must resolve to a file the child would load by that name.");
        Assert.IsTrue(CanBeReplaced(verifiedCopy), "A rejected layout must not stay locked.");
    }

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;

        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(linkPath);
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
        // cache. A signature gate that only accepts the seeded cache forces the full traversal — each
        // installed root that resolves is rejected (or is absent) before the cache fallback resolves.
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, null);
        var cache = new DirectoryInfo(Path.Combine(_tempDir, "cache-bin"));
        cache.Create();
        File.WriteAllText(Path.Combine(cache.FullName, "dbgeng.dll"), "");
        File.WriteAllText(Path.Combine(cache.FullName, "JsProvider.dll"), "");
        var realCache = Path.GetDirectoryName(RealPath(Path.Combine(cache.FullName, "dbgeng.dll")))!;

        using var resolved = Resolve(cache, signed: path => path.StartsWith(realCache, StringComparison.OrdinalIgnoreCase));

        Assert.IsNotNull(resolved,
            "After rejecting/exhausting the installed roots, the candidate walk must fall back to the seeded cache.");
        StringAssert.StartsWith(resolved.JsProviderPath, realCache);
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

        var resolved = Resolve(new DirectoryInfo(_tempDir));

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

        var resolved = Resolve(cache, signed: _ => false);

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
