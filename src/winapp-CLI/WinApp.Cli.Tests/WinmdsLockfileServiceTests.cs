// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class WinmdsLockfileServiceTests
{
    public TestContext TestContext { get; set; } = null!;

    private DirectoryInfo _temp = null!;
    private WinmdsLockfileService _svc = null!;

    [TestInitialize]
    public void Init()
    {
        _temp = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"WinmdsLockfileTests_{Guid.NewGuid():N}"));
        _temp.Create();
        _svc = new WinmdsLockfileService(NullLogger<WinmdsLockfileService>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { _temp.Delete(recursive: true); } catch { /* ignore */ }
    }

    [TestMethod]
    public void GetLockfilePath_LandsUnderWinappDir()
    {
        var path = _svc.GetLockfilePath(_temp);
        Assert.AreEqual(Path.Combine(_temp.FullName, "winmds.lock.json"), path.FullName);
    }

    [TestMethod]
    public async Task WriteAsync_ProducesIndentedSchemaVersionedJson()
    {
        var winapp = _temp.CreateSubdirectory("winapp");
        // PackageLayoutService.TryGetPackageIdFromPath keys off the cache root, so the
        // winmd must live under a `<cache>/<id-lc>/<version>/...` layout.
        var cache = _temp.CreateSubdirectory("packages");
        var winmd = new FileInfo(Path.Combine(
            cache.FullName, "microsoft.windowsappsdk.ai", "1.8.39", "metadata", "Microsoft.Windows.AI.winmd"));
        winmd.Directory!.Create();
        await File.WriteAllTextAsync(winmd.FullName, "");

        var usedVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft.WindowsAppSDK.AI"] = "1.8.39",
        };
        await _svc.WriteAsync(winapp, usedVersions, new[] { winmd }, cache, default);

        var path = _svc.GetLockfilePath(winapp);
        Assert.IsTrue(path.Exists, "Lockfile must be written under .winapp/.");
        var json = await File.ReadAllTextAsync(path.FullName);
        Assert.Contains("\"schema\": 3", json);
        Assert.Contains("\"generated_at\"", json);
        Assert.Contains("Microsoft.WindowsAppSDK.AI", json);
        Assert.IsFalse(json.Contains("\"category\""),
            "v3 lockfile must NOT emit a `category` field — that classification is owned by the @microsoft/winapp npm wrapper now.");
        Assert.IsTrue(json.Contains('\n'), "Output must be indented (multiple lines).");
        var bytes = await File.ReadAllBytesAsync(path.FullName);
        Assert.IsFalse(
            bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "Lockfile must be UTF-8 without BOM (so diff tools and external readers don't choke).");
    }

    [TestMethod]
    public async Task RoundTrip_PreservesPackageVersionsAndWinmds()
    {
        var winapp = _temp.CreateSubdirectory("winapp");
        var cache = _temp.CreateSubdirectory("packages");

        // Realistic mix including a package with zero winmds (the umbrella).
        var aiWinmd = MakeFile(cache, "microsoft.windowsappsdk.ai", "1.8.39", "metadata", "Microsoft.Windows.AI.winmd");
        var ieWinmd = MakeFile(cache, "microsoft.windowsappsdk.interactiveexperiences", "1.8.0", "metadata", "10.0.18362.0", "Microsoft.UI.winmd");
        var winuiWinmd = MakeFile(cache, "microsoft.windowsappsdk.winui", "1.8.0", "metadata", "Microsoft.UI.Xaml.winmd");

        var usedVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft.WindowsAppSDK.AI"] = "1.8.39",
            ["Microsoft.WindowsAppSDK.InteractiveExperiences"] = "1.8.0",
            ["Microsoft.WindowsAppSDK.WinUI"] = "1.8.0",
            ["Microsoft.WindowsAppSDK"] = "1.8.0",  // umbrella, no winmd
        };
        await _svc.WriteAsync(winapp, usedVersions, new[] { aiWinmd, ieWinmd, winuiWinmd }, cache, default);

        var lockfile = await _svc.TryReadAsync(winapp, default);
        Assert.IsNotNull(lockfile);
        Assert.AreEqual(3, lockfile.Schema);
        Assert.AreEqual(4, lockfile.Packages.Count);

        // Packages are sorted alphabetically (case-insensitive) by name.
        var ai = lockfile.Packages.Single(p => p.Name == "Microsoft.WindowsAppSDK.AI");
        Assert.AreEqual("1.8.39", ai.Version);
        Assert.AreEqual(1, ai.Winmds.Count);
        Assert.IsTrue(ai.Winmds[0].EndsWith("Microsoft.Windows.AI.winmd", StringComparison.OrdinalIgnoreCase));

        var ie = lockfile.Packages.Single(p => p.Name == "Microsoft.WindowsAppSDK.InteractiveExperiences");
        Assert.AreEqual(1, ie.Winmds.Count);

        var winui = lockfile.Packages.Single(p => p.Name == "Microsoft.WindowsAppSDK.WinUI");
        Assert.AreEqual(1, winui.Winmds.Count);

        var umbrella = lockfile.Packages.Single(p => p.Name == "Microsoft.WindowsAppSDK");
        Assert.AreEqual(0, umbrella.Winmds.Count, "Umbrella package has no winmd files; lockfile records it for completeness.");
    }

    [TestMethod]
    public async Task TryReadAsync_MissingFile_ReturnsNull()
    {
        var result = await _svc.TryReadAsync(_temp, default);
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task TryReadAsync_CorruptedJson_ReturnsNull()
    {
        var path = _svc.GetLockfilePath(_temp);
        await File.WriteAllTextAsync(path.FullName, "{this is not json");

        var result = await _svc.TryReadAsync(_temp, default);
        Assert.IsNull(result, "Corrupted lockfile must trigger fallback (return null) rather than throw.");
    }

    [TestMethod]
    public async Task TryReadAsync_UnknownSchemaVersion_ReturnsNull()
    {
        // A future schema bump must not crash older clients.
        var path = _svc.GetLockfilePath(_temp);
        await File.WriteAllTextAsync(path.FullName, "{\"schema\": 999, \"packages\": []}");

        var result = await _svc.TryReadAsync(_temp, default);
        Assert.IsNull(result, "Unknown schema version must be treated as missing.");
    }

    [TestMethod]
    public void BuildLockfile_VendorWinmdsOutsideCache_AreDropped()
    {
        // Lockfile is a record of "what restore put on disk", not user-supplied refs.
        var cache = _temp.CreateSubdirectory("packages");
        var vendorPath = Path.Combine(_temp.FullName, "vendor", "MyCompany.Custom.winmd");
        Directory.CreateDirectory(Path.GetDirectoryName(vendorPath)!);
        File.WriteAllText(vendorPath, "");

        var lockfile = WinmdsLockfileService.BuildLockfile(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Microsoft.WindowsAppSDK.AI"] = "1.8.39" },
            new[] { new FileInfo(vendorPath) },
            cache);

        Assert.AreEqual(1, lockfile.Packages.Count);
        Assert.AreEqual(0, lockfile.Packages[0].Winmds.Count,
            "Vendor winmd outside the NuGet cache must not get attached to any package.");
    }

    // -------------------------------------------------------------------------
    // v2.3 — yaml hash, atomic write, schema-bump back-compat
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task WriteAsync_StoresYamlPackagesHash()
    {
        var winapp = _temp.CreateSubdirectory("winapp");
        var cache = _temp.CreateSubdirectory("packages");
        var winmd = MakeFile(cache, "microsoft.windowsappsdk.ai", "1.8.39", "metadata", "Microsoft.Windows.AI.winmd");

        await _svc.WriteAsync(
            winapp,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Microsoft.WindowsAppSDK.AI"] = "1.8.39" },
            new[] { winmd },
            cache,
            yamlPackagesHash: "abc123deadbeef",
            cancellationToken: default);

        var lockfile = await _svc.TryReadAsync(winapp, default);
        Assert.IsNotNull(lockfile);
        Assert.AreEqual("abc123deadbeef", lockfile.YamlPackagesHash);
    }

    [TestMethod]
    public async Task TryReadAsync_OlderSchemaVersions_ReturnNull()
    {
        // Pre-v3 lockfiles (schema 1 or 2) used a Category field that was
        // computed by native; v3 readers must ignore them so the npm wrapper
        // can force a fresh restore that omits that field.
        foreach (var oldSchema in new[] { 1, 2 })
        {
            var path = _svc.GetLockfilePath(_temp);
            await File.WriteAllTextAsync(path.FullName, $"{{\"schema\": {oldSchema}, \"packages\": []}}");
            var result = await _svc.TryReadAsync(_temp, default);
            Assert.IsNull(result, $"Schema {oldSchema} lockfiles must be ignored after the v3 schema bump.");
        }
    }

    [TestMethod]
    public async Task WriteAsync_UsesAtomicTempThenRename()
    {
        // Can't observe mid-write reliably; check post-conditions: final
        // file exists, no .tmp left behind.
        var winapp = _temp.CreateSubdirectory("winapp");
        var cache = _temp.CreateSubdirectory("packages");
        var winmd = MakeFile(cache, "microsoft.windowsappsdk.ai", "1.8.39", "metadata", "AI.winmd");

        await _svc.WriteAsync(
            winapp,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Microsoft.WindowsAppSDK.AI"] = "1.8.39" },
            new[] { winmd },
            cache,
            yamlPackagesHash: "h",
            cancellationToken: default);

        var entries = winapp.EnumerateFiles().Select(f => f.Name).ToList();
        Assert.Contains("winmds.lock.json", entries, "Final lockfile must exist.");
        Assert.IsFalse(
            entries.Any(n => n.StartsWith("winmds.lock.json.tmp", StringComparison.Ordinal)),
            $"No tmp staging file should remain after a successful write. Found: {string.Join(", ", entries)}");
    }

    [TestMethod]
    public async Task WriteAsync_WhenTargetPathIsDirectory_LogsAndSwallows()
    {
        // The lockfile is best-effort: any non-cancellation failure during the atomic
        // write must be swallowed so restore/discovery can continue. Pre-creating a
        // directory at the lockfile path makes the final File.Move fail.
        var winapp = _temp.CreateSubdirectory("winapp");
        var cache = _temp.CreateSubdirectory("packages");
        var winmd = MakeFile(cache, "microsoft.windowsappsdk.ai", "1.8.39", "metadata", "AI.winmd");
        Directory.CreateDirectory(Path.Combine(winapp.FullName, "winmds.lock.json"));

        // Must NOT throw despite the write failing.
        await _svc.WriteAsync(
            winapp,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Microsoft.WindowsAppSDK.AI"] = "1.8.39" },
            new[] { winmd },
            cache,
            cancellationToken: default);

        // The path is still the pre-existing directory (no file clobbered it).
        Assert.IsTrue(Directory.Exists(Path.Combine(winapp.FullName, "winmds.lock.json")),
            "A failed write must leave the pre-existing entry untouched.");
    }

    [TestMethod]
    public async Task TryReadAsync_JsonLiteralNull_ReturnsNull()
    {
        // A file whose entire content is the JSON literal `null` deserializes to a null
        // object; the reader must treat that as "no lockfile" rather than dereferencing it.
        var path = _svc.GetLockfilePath(_temp);
        await File.WriteAllTextAsync(path.FullName, "null");

        var result = await _svc.TryReadAsync(_temp, default);

        Assert.IsNull(result, "A `null` JSON body must be treated as a missing lockfile.");
    }

    [TestMethod]
    public async Task TryReadAsync_CancellationRequested_PropagatesOperationCanceled()
    {
        // Cancellation must NOT be swallowed by the generic fallback catch; it has to
        // surface so callers can abort. A pre-canceled token trips the first stream read.
        var path = _svc.GetLockfilePath(_temp);
        await File.WriteAllTextAsync(path.FullName, "{\"schema\": 3, \"packages\": []}");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        OperationCanceledException? caught = null;
        try
        {
            await _svc.TryReadAsync(_temp, cts.Token);
        }
        catch (OperationCanceledException ex)
        {
            caught = ex;
        }

        Assert.IsNotNull(caught, "A canceled token must propagate as OperationCanceledException, not be swallowed.");
    }

    private static FileInfo MakeFile(DirectoryInfo cache, params string[] segments)
    {
        var path = Path.Combine(new[] { cache.FullName }.Concat(segments).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return new FileInfo(path);
    }

    [TestMethod]
    public async Task WriteAsync_WinappDirIsJunction_LogsAndSkipsWithoutWriting()
    {
        // Refuse unsafe lockfile targets without writing through the junction.
        var realDir = _temp.CreateSubdirectory("real-winapp");
        var winappJunction = Path.Combine(_temp.FullName, ".winapp");
        if (!TryCreateJunction(winappJunction, realDir.FullName))
        {
            Assert.Inconclusive("Could not create a junction (CI may lack the privilege).");
            return;
        }

        try
        {
            var winappDir = new DirectoryInfo(winappJunction);
            var cache = _temp.CreateSubdirectory("packages");
            var winmd = MakeFile(cache, "microsoft.windowsappsdk.ai", "1.8.39", "metadata", "Microsoft.Windows.AI.winmd");
            var usedVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Microsoft.WindowsAppSDK.AI"] = "1.8.39",
            };

            await _svc.WriteAsync(winappDir, usedVersions, new[] { winmd }, cache, default);

            // Nothing inside the (junction-targeted) real dir AND nothing
            // inside the junction view. Skip = no write.
            Assert.IsFalse(File.Exists(Path.Combine(realDir.FullName, "winmds.lock.json")),
                "Lockfile must NOT be written through a junctioned .winapp.");
        }
        finally
        {
            try { Directory.Delete(winappJunction, recursive: false); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public async Task TryReadAsync_WinappDirIsJunction_ReturnsNullWithoutReading()
    {
        var realDir = _temp.CreateSubdirectory("real-winapp");
        // Plant a valid-looking lockfile under the REAL dir so the only
        // way a read could succeed is by traversing the junction.
        var lockfilePath = Path.Combine(realDir.FullName, "winmds.lock.json");
        await File.WriteAllTextAsync(lockfilePath, """
            { "schema": 3, "generated_at": "2025-01-01T00:00:00Z", "packages": [] }
            """);

        var winappJunction = Path.Combine(_temp.FullName, ".winapp");
        if (!TryCreateJunction(winappJunction, realDir.FullName))
        {
            Assert.Inconclusive("Could not create a junction (CI may lack the privilege).");
            return;
        }

        try
        {
            var winappDir = new DirectoryInfo(winappJunction);
            var result = await _svc.TryReadAsync(winappDir, default);

            Assert.IsNull(result,
                "TryReadAsync must refuse to traverse a junctioned .winapp and return null.");
        }
        finally
        {
            try { Directory.Delete(winappJunction, recursive: false); } catch { /* ignore */ }
        }
    }

    // Junction creation helper (mklink /J — non-elevating on Windows).
    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
            {
                return false;
            }
            p.WaitForExit(5000);
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        catch
        {
            return false;
        }
    }
}
