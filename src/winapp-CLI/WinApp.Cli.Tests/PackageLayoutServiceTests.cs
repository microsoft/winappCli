// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="PackageLayoutService"/>, which harvests headers, libs, runtimes and
/// WinMD files out of the NuGet global-cache package layout (id/version/...). All tests use a
/// temporary directory that mimics the real cache layout so no NuGet or network access is needed.
/// </summary>
[TestClass]
public class PackageLayoutServiceTests
{
    private DirectoryInfo _tempDir = null!;
    private DirectoryInfo _cacheDir = null!;
    private PackageLayoutService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"PkgLayoutTest_{Guid.NewGuid():N}"));
        _tempDir.Create();
        _cacheDir = _tempDir.CreateSubdirectory("cache");
        _service = new PackageLayoutService();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_tempDir.Exists)
        {
            _tempDir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Creates a package folder at cache/{name-lowercased}/{version}/{subPath} and writes a file there.
    /// Returns the created file path.
    /// </summary>
    private string WritePackageFile(string name, string version, string relativePath, byte[]? content = null)
    {
        var full = Path.Combine(_cacheDir.FullName, name.ToLowerInvariant(), version, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content ?? [1, 2, 3]);
        return full;
    }

    private static Dictionary<string, string> Used(params (string name, string version)[] pkgs)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, version) in pkgs)
        {
            d[name] = version;
        }
        return d;
    }

    #region TryGetPackageIdFromPath

    [TestMethod]
    public void TryGetPackageIdFromPath_PathInsideCache_ReturnsLowercasedId()
    {
        var path = Path.Combine(_cacheDir.FullName, "Microsoft.Foo", "1.2.3", "lib", "foo.dll");

        var id = PackageLayoutService.TryGetPackageIdFromPath(_cacheDir, path);

        Assert.AreEqual("microsoft.foo", id);
    }

    [TestMethod]
    public void TryGetPackageIdFromPath_PathOutsideCache_ReturnsNull()
    {
        var outside = Path.Combine(_tempDir.FullName, "elsewhere", "pkg", "1.0", "x.dll");

        var id = PackageLayoutService.TryGetPackageIdFromPath(_cacheDir, outside);

        Assert.IsNull(id);
    }

    [TestMethod]
    public void TryGetPackageIdFromPath_PathIsCacheRoot_ReturnsNull()
    {
        var id = PackageLayoutService.TryGetPackageIdFromPath(_cacheDir, _cacheDir.FullName);

        Assert.IsNull(id);
    }

    [TestMethod]
    public void TryGetPackageIdFromPath_PackageIdOnlyNoTrailingSegment_ReturnsNull()
    {
        // A path that is just cache/pkgid with no further separator has no version segment.
        var path = Path.Combine(_cacheDir.FullName, "somepkg");

        var id = PackageLayoutService.TryGetPackageIdFromPath(_cacheDir, path);

        Assert.IsNull(id);
    }

    [TestMethod]
    public void TryGetPackageIdFromPath_ForwardSlashSeparators_Resolved()
    {
        var path = _cacheDir.FullName.Replace('\\', '/') + "/My.Pkg/2.0.0/build/native/My.Pkg.targets";

        var id = PackageLayoutService.TryGetPackageIdFromPath(_cacheDir, path);

        Assert.AreEqual("my.pkg", id);
    }

    #endregion

    #region CopyIncludesFromPackages

    [TestMethod]
    public void CopyIncludesFromPackages_CopiesTopLevelHeaders()
    {
        WritePackageFile("Pkg.A", "1.0.0", Path.Combine("build", "native", "include", "header.h"));
        WritePackageFile("Pkg.A", "1.0.0", Path.Combine("build", "native", "include", "other.hpp"));
        var includeOut = new DirectoryInfo(Path.Combine(_tempDir.FullName, "out-include"));

        _service.CopyIncludesFromPackages(_cacheDir, includeOut, Used(("Pkg.A", "1.0.0")));

        Assert.IsTrue(File.Exists(Path.Combine(includeOut.FullName, "header.h")));
        Assert.IsTrue(File.Exists(Path.Combine(includeOut.FullName, "other.hpp")));
    }

    [TestMethod]
    public void CopyIncludesFromPackages_SkipsMissingPackage_AndCreatesOutputDir()
    {
        var includeOut = new DirectoryInfo(Path.Combine(_tempDir.FullName, "out-include-empty"));

        // usedVersions references a package/version that does not exist on disk.
        _service.CopyIncludesFromPackages(_cacheDir, includeOut, Used(("Does.Not.Exist", "9.9.9")));

        Assert.IsTrue(includeOut.Exists, "Output include dir should be created even when nothing is copied.");
        Assert.AreEqual(0, includeOut.GetFiles().Length);
    }

    [TestMethod]
    public void CopyIncludesFromPackages_ReadOnlyTarget_SwallowsUnauthorizedAccess()
    {
        WritePackageFile("Pkg.A", "1.0.0", Path.Combine("build", "native", "include", "header.h"));
        var includeOut = new DirectoryInfo(Path.Combine(_tempDir.FullName, "out-include-ro"));
        includeOut.Create();

        // Pre-create a read-only file at the destination; overwrite:true copy must throw
        // UnauthorizedAccessException, which TryCopy swallows to stay resilient.
        var target = Path.Combine(includeOut.FullName, "header.h");
        File.WriteAllText(target, "locked");
        var ro = new FileInfo(target) { IsReadOnly = true };
        try
        {
            _service.CopyIncludesFromPackages(_cacheDir, includeOut, Used(("Pkg.A", "1.0.0")));

            // The read-only file is preserved (copy was skipped) and no exception bubbled out.
            Assert.AreEqual("locked", File.ReadAllText(target));
        }
        finally
        {
            ro.IsReadOnly = false;
        }
    }

    [TestMethod]
    public void CopyIncludesFromPackages_LockedTarget_SwallowsIOException()
    {
        WritePackageFile("Pkg.A", "1.0.0", Path.Combine("build", "native", "include", "header.h"));
        var includeOut = new DirectoryInfo(Path.Combine(_tempDir.FullName, "out-include-locked"));
        includeOut.Create();

        // Hold the destination file open with no sharing so the overwrite copy throws IOException,
        // which TryCopy swallows to keep the harvest resilient.
        var target = Path.Combine(includeOut.FullName, "header.h");
        using (var _ = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            _service.CopyIncludesFromPackages(_cacheDir, includeOut, Used(("Pkg.A", "1.0.0")));
        }

        // No exception escaped; the destination file still exists.
        Assert.IsTrue(File.Exists(target));
    }

    #endregion

    #region CopyLibs (specific arch)

    [TestMethod]
    public void CopyLibs_CopiesLibsFromAllKnownArchLayouts()
    {
        const string arch = "x64";
        WritePackageFile("Pkg.Lib", "1.0.0", Path.Combine("lib", arch, "direct.lib"));
        WritePackageFile("Pkg.Lib", "1.0.0", Path.Combine("lib", "native", arch, "native.lib"));
        WritePackageFile("Pkg.Lib", "1.0.0", Path.Combine("lib", $"win-{arch}", "win.lib"));
        WritePackageFile("Pkg.Lib", "1.0.0", Path.Combine("lib", $"win10-{arch}", "win10.lib"));
        WritePackageFile("Pkg.Lib", "1.0.0", Path.Combine("lib", "native", $"win10-{arch}", "nativewin10.lib"));
        // Non-.lib file that must be ignored.
        WritePackageFile("Pkg.Lib", "1.0.0", Path.Combine("lib", arch, "ignore.txt"));

        var libOut = new DirectoryInfo(Path.Combine(_tempDir.FullName, "out-lib"));

        PackageLayoutService.CopyLibs(_cacheDir, libOut, arch, Used(("Pkg.Lib", "1.0.0")));

        var names = libOut.GetFiles().Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] expected = ["direct.lib", "native.lib", "win.lib", "win10.lib", "nativewin10.lib"];
        Assert.AreSequenceEqual(expected, names.ToArray(), SequenceOrder.InAnyOrder);
        Assert.IsFalse(names.Contains("ignore.txt"));
    }

    #endregion

    #region CopyRuntimes (specific arch)

    [TestMethod]
    public void CopyRuntimes_CopiesNativeRuntimeFiles()
    {
        const string arch = "x64";
        WritePackageFile("Pkg.Rt", "1.0.0", Path.Combine("runtimes", $"win-{arch}", "native", "core.dll"));
        WritePackageFile("Pkg.Rt", "1.0.0", Path.Combine("runtimes", $"win-{arch}", "native", "data.bin"));
        // A different platform that must not be picked up by the specific-arch copy.
        WritePackageFile("Pkg.Rt", "1.0.0", Path.Combine("runtimes", "win-arm64", "native", "other.dll"));

        var binOut = new DirectoryInfo(Path.Combine(_tempDir.FullName, "out-bin"));

        PackageLayoutService.CopyRuntimes(_cacheDir, binOut, arch, Used(("Pkg.Rt", "1.0.0")));

        var names = binOut.GetFiles().Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.IsTrue(names.Contains("core.dll"));
        Assert.IsTrue(names.Contains("data.bin"));
        Assert.IsFalse(names.Contains("other.dll"));
    }

    #endregion

    #region FindWinmds

    [TestMethod]
    public void FindWinmds_DiscoversWinmdsAcrossAllKnownLocations()
    {
        WritePackageFile("Pkg.Wmd", "1.0.0", Path.Combine("metadata", "Meta.winmd"));
        WritePackageFile("Pkg.Wmd", "1.0.0", Path.Combine("metadata", "10.0.18362.0", "Versioned.winmd"));
        WritePackageFile("Pkg.Wmd", "1.0.0", Path.Combine("lib", "Lib.winmd"));
        WritePackageFile("Pkg.Wmd", "1.0.0", Path.Combine("lib", "uap10.0", "Uap.winmd"));
        WritePackageFile("Pkg.Wmd", "1.0.0", Path.Combine("lib", "uap10.0.18362", "UapVersioned.winmd"));
        WritePackageFile("Pkg.Wmd", "1.0.0", Path.Combine("References", "deep", "nested", "Ref.winmd"));
        // Non-winmd file that must be ignored.
        WritePackageFile("Pkg.Wmd", "1.0.0", Path.Combine("lib", "notawinmd.dll"));

        var results = _service.FindWinmds(_cacheDir, Used(("Pkg.Wmd", "1.0.0"))).ToList();

        var names = results.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] expected = ["Meta.winmd", "Versioned.winmd", "Lib.winmd", "Uap.winmd", "UapVersioned.winmd", "Ref.winmd"];
        Assert.AreSequenceEqual(expected, names.ToArray(), SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public void FindWinmds_SameFileDiscoverableViaMultipleRoots_Deduplicates()
    {
        // A winmd under References/lib is discovered by BOTH the recursive References search
        // (line ~153) AND the lib search (line ~132), so without deduplication the same file
        // would be returned twice. Assert the HashSet collapses it to a single result.
        WritePackageFile("Pkg.Wmd", "1.0.0", Path.Combine("References", "lib", "Dup.winmd"));

        var results = _service.FindWinmds(_cacheDir, Used(("Pkg.Wmd", "1.0.0"))).ToList();

        Assert.AreEqual(1, results.Count, "The same winmd found via multiple search roots must be deduplicated");
        Assert.AreEqual("Dup.winmd", results[0].Name);
    }

    [TestMethod]
    public void FindWinmds_MissingPackage_ReturnsEmpty()
    {
        var results = _service.FindWinmds(_cacheDir, Used(("Nope", "0.0.0"))).ToList();

        Assert.AreEqual(0, results.Count);
    }

    #endregion

    #region CopyLibsAllArch

    [TestMethod]
    public void CopyLibsAllArch_RoutesEachLayoutToItsArchFolder()
    {
        WritePackageFile("Pkg.All", "1.0.0", Path.Combine("lib", "win-x64", "a.lib"));
        WritePackageFile("Pkg.All", "1.0.0", Path.Combine("lib", "win10-arm64", "b.lib"));
        WritePackageFile("Pkg.All", "1.0.0", Path.Combine("lib", "native", "win10-x86", "c.lib"));
        WritePackageFile("Pkg.All", "1.0.0", Path.Combine("lib", "native", "arm64", "d.lib"));
        WritePackageFile("Pkg.All", "1.0.0", Path.Combine("lib", "x64", "e.lib"));

        var libRoot = new DirectoryInfo(Path.Combine(_tempDir.FullName, "out-liball"));

        _service.CopyLibsAllArch(_cacheDir, libRoot, Used(("Pkg.All", "1.0.0")));

        Assert.IsTrue(File.Exists(Path.Combine(libRoot.FullName, "x64", "a.lib")));
        Assert.IsTrue(File.Exists(Path.Combine(libRoot.FullName, "arm64", "b.lib")));
        Assert.IsTrue(File.Exists(Path.Combine(libRoot.FullName, "x86", "c.lib")));
        Assert.IsTrue(File.Exists(Path.Combine(libRoot.FullName, "arm64", "d.lib")));
        Assert.IsTrue(File.Exists(Path.Combine(libRoot.FullName, "x64", "e.lib")));
    }

    [TestMethod]
    public void CopyLibsAllArch_IgnoresUnknownPlatformFolders()
    {
        WritePackageFile("Pkg.All", "1.0.0", Path.Combine("lib", "netstandard2.0", "managed.lib"));

        var libRoot = new DirectoryInfo(Path.Combine(_tempDir.FullName, "out-liball2"));

        _service.CopyLibsAllArch(_cacheDir, libRoot, Used(("Pkg.All", "1.0.0")));

        // netstandard2.0 is not an arch folder, so nothing should be routed anywhere.
        Assert.AreEqual(0, libRoot.GetDirectories().Length);
    }

    #endregion

    #region CopyRuntimesAllArch

    [TestMethod]
    public void CopyRuntimesAllArch_CopiesWinPlatformNativeFilesPerArch()
    {
        WritePackageFile("Pkg.Rt", "1.0.0", Path.Combine("runtimes", "win-x64", "native", "core.dll"));
        WritePackageFile("Pkg.Rt", "1.0.0", Path.Combine("runtimes", "win-arm64", "native", "arm.dll"));
        // Non win- platform should be ignored.
        WritePackageFile("Pkg.Rt", "1.0.0", Path.Combine("runtimes", "linux-x64", "native", "skip.so"));

        var binRoot = new DirectoryInfo(Path.Combine(_tempDir.FullName, "out-rtall"));

        _service.CopyRuntimesAllArch(_cacheDir, binRoot, Used(("Pkg.Rt", "1.0.0")));

        Assert.IsTrue(File.Exists(Path.Combine(binRoot.FullName, "x64", "core.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(binRoot.FullName, "arm64", "arm.dll")));
        Assert.IsFalse(Directory.Exists(Path.Combine(binRoot.FullName, "linux-x64")));
        Assert.IsFalse(File.Exists(Path.Combine(binRoot.FullName, "x64", "skip.so")));
    }

    #endregion

    #region Multiple packages

    [TestMethod]
    public void CopyLibs_ProcessesMultiplePackages()
    {
        WritePackageFile("Pkg.One", "1.0.0", Path.Combine("lib", "x64", "one.lib"));
        WritePackageFile("Pkg.Two", "2.0.0", Path.Combine("lib", "x64", "two.lib"));

        var libOut = new DirectoryInfo(Path.Combine(_tempDir.FullName, "out-multi"));

        PackageLayoutService.CopyLibs(_cacheDir, libOut, "x64", Used(("Pkg.One", "1.0.0"), ("Pkg.Two", "2.0.0")));

        Assert.IsTrue(File.Exists(Path.Combine(libOut.FullName, "one.lib")));
        Assert.IsTrue(File.Exists(Path.Combine(libOut.FullName, "two.lib")));
    }

    #endregion
}
