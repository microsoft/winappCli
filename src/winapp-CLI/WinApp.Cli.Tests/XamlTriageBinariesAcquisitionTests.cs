// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Offline tests for the download-on-first-use acquisition orchestration in
/// <see cref="XamlTriageBinaries"/>. These exercise the two <em>deterministic</em> acquisition routes —
/// "already materialized in the cache" and "copied from the NuGet global packages cache" — plus the PE
/// sanity check that decides whether a cached file must be re-acquired. Every component is satisfied
/// from local disk, so the flat-container download path is never reached and no network I/O occurs. The
/// flat-container download core itself (<c>TryMaterializePackageAsync</c> /
/// <c>ResolveDownloadVersionAsync</c> / <c>FindBestArchMatch</c>) is covered offline via the
/// <c>HttpGetAsync</c> seam in <see cref="XamlTriageBinariesDownloadTests"/>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class XamlTriageBinariesAcquisitionTests
{
    // Mirror of XamlTriageBinaries.NuGetComponents (which is private). A drift test elsewhere pins the
    // package versions; here we only need the file lists to lay out a fake global cache.
    private const string DbgEngPackage = "Microsoft.Debugging.Platform.DbgEng";
    private const string SymSrvPackage = "Microsoft.Debugging.Platform.SymSrv";
    private static readonly string[] DbgEngFiles = ["dbgeng.dll", "dbghelp.dll", "dbgcore.dll", "dbgmodel.dll", "msdia140.dll"];
    private static readonly string[] SymSrvFiles = ["symsrv.dll"];

    private string _tempDir = null!;
    private Func<string, Microsoft.Extensions.Logging.ILogger, bool> _originalVerifier = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"XamlBinAcq_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // The stand-in DLLs here are unsigned; the signature gate is exercised explicitly below.
        _originalVerifier = XamlTriageBinaries.SignatureVerifier;
        XamlTriageBinaries.SignatureVerifier = (_, _) => true;
    }

    [TestCleanup]
    public void Cleanup()
    {
        XamlTriageBinaries.SignatureVerifier = _originalVerifier;
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public async Task TryAcquireFromNuGetAsync_AllComponentsAlreadyUsable_ShortCircuitsWithoutNetwork()
    {
        var binDir = new DirectoryInfo(Path.Combine(_tempDir, "bin"));
        binDir.Create();
        foreach (var file in DbgEngFiles.Concat(SymSrvFiles))
        {
            WriteFakePe(Path.Combine(binDir.FullName, file));
        }

        var acquired = await XamlTriageBinaries.TryAcquireFromNuGetAsync(
            binDir, nugetCacheDir: null, NullLogger.Instance, CancellationToken.None);

        Assert.AreEqual(2, acquired, "Both components should count as acquired straight from the usable cache.");
    }

    [TestMethod]
    public async Task TryAcquireFromNuGetAsync_EmptyCache_CopiesBothComponentsFromGlobalCache()
    {
        var binDir = new DirectoryInfo(Path.Combine(_tempDir, "bin"));
        binDir.Create();
        var globalCache = SeedGlobalCache();

        var acquired = await XamlTriageBinaries.TryAcquireFromNuGetAsync(
            binDir, globalCache, NullLogger.Instance, CancellationToken.None);

        Assert.AreEqual(2, acquired);
        foreach (var file in DbgEngFiles.Concat(SymSrvFiles))
        {
            Assert.IsTrue(File.Exists(Path.Combine(binDir.FullName, file)), $"{file} should have been copied from the global cache.");
        }
    }

    [TestMethod]
    public async Task TryAcquireFromNuGetAsync_TruncatedCachedEngine_ReacquiresFromGlobalCache()
    {
        var binDir = new DirectoryInfo(Path.Combine(_tempDir, "bin"));
        binDir.Create();
        // A too-small file fails the PE size check, forcing re-acquisition of that component.
        File.WriteAllBytes(Path.Combine(binDir.FullName, "dbgeng.dll"), new byte[16]);
        var globalCache = SeedGlobalCache();

        var acquired = await XamlTriageBinaries.TryAcquireFromNuGetAsync(
            binDir, globalCache, NullLogger.Instance, CancellationToken.None);

        Assert.AreEqual(2, acquired);
        Assert.AreEqual("dbgeng.dll-content", File.ReadAllText(Path.Combine(binDir.FullName, "dbgeng.dll")),
            "The truncated engine file should have been overwritten from the global cache.");
    }

    [TestMethod]
    public async Task TryAcquireFromNuGetAsync_NonPeCachedEngine_ReacquiresFromGlobalCache()
    {
        var binDir = new DirectoryInfo(Path.Combine(_tempDir, "bin"));
        binDir.Create();
        // Large enough to pass the size gate but lacking the MZ signature -> treated as corrupt.
        var notPe = new byte[8192];
        notPe[0] = (byte)'P';
        notPe[1] = (byte)'K';
        File.WriteAllBytes(Path.Combine(binDir.FullName, "dbgeng.dll"), notPe);
        var globalCache = SeedGlobalCache();

        var acquired = await XamlTriageBinaries.TryAcquireFromNuGetAsync(
            binDir, globalCache, NullLogger.Instance, CancellationToken.None);

        Assert.AreEqual(2, acquired);
        Assert.AreEqual("dbgeng.dll-content", File.ReadAllText(Path.Combine(binDir.FullName, "dbgeng.dll")),
            "The non-PE engine file should have been overwritten from the global cache.");
    }

    [TestMethod]
    public async Task TryAcquireFromNuGetAsync_UnsignedCachedEngine_ReacquiresFromGlobalCache()
    {
        // A cached engine DLL that is intact but not Microsoft-signed (replaced in the cache) is refused
        // at resolve time. Treating it as present would disable triage for good, so it is re-acquired.
        var binDir = new DirectoryInfo(Path.Combine(_tempDir, "bin"));
        binDir.Create();
        foreach (var file in DbgEngFiles.Concat(SymSrvFiles))
        {
            WriteFakePe(Path.Combine(binDir.FullName, file));
        }

        var tampered = Path.Combine(binDir.FullName, "dbgmodel.dll");
        XamlTriageBinaries.SignatureVerifier = (path, _) => !string.Equals(path, tampered, StringComparison.OrdinalIgnoreCase);
        var globalCache = SeedGlobalCache();

        var acquired = await XamlTriageBinaries.TryAcquireFromNuGetAsync(
            binDir, globalCache, NullLogger.Instance, CancellationToken.None);

        Assert.AreEqual(2, acquired);
        Assert.AreEqual("dbgmodel.dll-content", File.ReadAllText(tampered),
            "The unsigned engine file should have been overwritten from the global cache.");
    }

    [TestMethod]
    public void VerifyPackageHash_MatchingContent_ReturnsTrue()
    {
        var bytes = "the-quick-brown-fox"u8.ToArray();
        var hex = Convert.ToHexString(SHA512.HashData(bytes));

        Assert.IsTrue(XamlTriageBinaries.VerifyPackageHash(bytes, hex));
        Assert.IsTrue(XamlTriageBinaries.VerifyPackageHash(bytes, hex.ToLowerInvariant()),
            "Hash comparison must be case-insensitive.");
    }

    [TestMethod]
    public void HasEngine_ReflectsDbgEngPresence()
    {
        var binDir = new DirectoryInfo(Path.Combine(_tempDir, "engine"));
        binDir.Create();
        Assert.IsFalse(XamlTriageBinaries.HasEngine(binDir));

        File.WriteAllText(Path.Combine(binDir.FullName, "dbgeng.dll"), "x");
        Assert.IsTrue(XamlTriageBinaries.HasEngine(binDir));
    }

    private DirectoryInfo SeedGlobalCache()
    {
        var cache = new DirectoryInfo(Path.Combine(_tempDir, "nuget-global"));
        WriteComponent(cache, DbgEngPackage, DbgEngFiles);
        WriteComponent(cache, SymSrvPackage, SymSrvFiles);
        return cache;
    }

    private static void WriteComponent(DirectoryInfo cache, string package, string[] files)
    {
        var archDir = Path.Combine(
            cache.FullName, package.ToLowerInvariant(), XamlTriageBinaries.DbgPackageVersion, "content", XamlTriageBinaries.NuGetArch);
        Directory.CreateDirectory(archDir);
        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(archDir, file), $"{file}-content");
        }
    }

    private static void WriteFakePe(string path)
    {
        var bytes = new byte[8192];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        File.WriteAllBytes(path, bytes);
    }
}
