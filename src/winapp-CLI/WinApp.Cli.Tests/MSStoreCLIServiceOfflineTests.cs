// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Offline tests for <see cref="MSStoreCLIService"/> that drive the GitHub-release
/// download / checksum / verify / extract flow through the injected <c>Http</c> and
/// <c>OsArchitectureProvider</c> seams, so no real network or host-arch dependency is used.
/// </summary>
[TestClass]
public class MSStoreCLIServiceOfflineTests : BaseCommandTests
{
    private const string ReleaseApi = "api.github.com/repos/microsoft/msstore-cli/releases/latest";

    private MSStoreCLIService NewService(
        FakeHttpMessageHandler handler, out DirectoryInfo installDir, Architecture arch = Architecture.X64)
    {
        var global = _tempDirectory.CreateSubdirectory("msstore-" + Guid.NewGuid().ToString("N"));
        installDir = new DirectoryInfo(Path.Combine(global.FullName, "tools", "msstore"));
        return new MSStoreCLIService(new StubWinappDirectoryService(global), new CapturingLogger<MSStoreCLIService>())
        {
            Http = new HttpClient(handler),
            OsArchitectureProvider = () => arch,
        };
    }

    private static byte[] BuildExeZip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("msstore.exe");
            using var w = new StreamWriter(entry.Open());
            w.Write("fake-msstore-binary");
        }
        return ms.ToArray();
    }

    private static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private MSStoreCLIService NewLocalService(FakeHttpMessageHandler handler, out DirectoryInfo installDir)
    {
        var root = _tempDirectory.CreateSubdirectory("local-store-" + Guid.NewGuid().ToString("N"));
        var profile = root.CreateSubdirectory("profile");
        File.WriteAllText(Path.Combine(profile.FullName, ".winapp"), "blocked default");
        var cwd = root.CreateSubdirectory("project");
        var directories = new WinappDirectoryService(new CurrentDirectoryProvider(cwd.FullName))
        {
            UserProfileProvider = () => profile.FullName,
            CacheOverrideProvider = () => null,
        };
        installDir = cwd.CreateSubdirectory(Path.Combine(".winapp", "cache", "tools", "msstore"));
        return new MSStoreCLIService(directories, NullLogger<MSStoreCLIService>.Instance)
        {
            Http = new HttpClient(handler),
            OsArchitectureProvider = () => Architecture.X64,
        };
    }

    [TestMethod]
    public async Task LocalCache_TrustedToolAndDependencies_AreVerifiedAgainAtUse()
    {
        var svc = NewLocalService(new FakeHttpMessageHandler(), out var dir);
        var exe = Path.Combine(dir.FullName, "msstore.exe");
        var dll = Path.Combine(dir.FullName, "msalruntime.dll");
        File.WriteAllText(exe, "fixture");
        File.WriteAllText(dll, "fixture");
        var verified = new List<string>();
        svc.SignatureVerifier = (path, _) => { verified.Add(path); return true; };

        await svc.EnsureMSStoreCLIAvailableAsync(TestContext.CancellationToken);
        Assert.AreEqual(exe, svc.GetMSStoreCLIPath());

        Assert.AreEqual(2, verified.Count(path => path == exe));
        Assert.AreEqual(2, verified.Count(path => path == dll));
        svc.SignatureVerifier = (_, _) => false;
        Assert.Throws<InvalidOperationException>(() => svc.GetMSStoreCLIPath(),
            "A prior successful verification must not authorize changed bytes.");
    }

    [TestMethod]
    public async Task LocalCache_UntrustedDependency_IsRejectedEvenWhenExecutableIsTrusted()
    {
        var svc = NewLocalService(new FakeHttpMessageHandler(), out var dir);
        File.WriteAllText(Path.Combine(dir.FullName, "msstore.exe"), "fixture");
        var dll = Path.Combine(dir.FullName, "msalruntime.dll");
        File.WriteAllText(dll, "fixture");
        svc.SignatureVerifier = (path, _) => path != dll;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.EnsureMSStoreCLIAvailableAsync(TestContext.CancellationToken));

        StringAssert.Contains(error.Message, dll);
        StringAssert.Contains(error.Message, "not validly signed by Microsoft");
    }

    [TestMethod]
    public async Task ExplicitCache_ReadableTool_DoesNotApplyLocalSignaturePolicy()
    {
        var svc = NewService(new FakeHttpMessageHandler(), out var dir);
        dir.Create();
        File.WriteAllText(Path.Combine(dir.FullName, "msstore.exe"), "fixture");
        svc.SignatureVerifier = (_, _) => throw new AssertFailedException("Only local fallback changes trust policy.");

        await svc.EnsureMSStoreCLIAvailableAsync(TestContext.CancellationToken);

        Assert.AreEqual(Path.Combine(dir.FullName, "msstore.exe"), svc.GetMSStoreCLIPath());
    }

    [TestMethod]
    public async Task DefaultGlobalCache_ReadableTool_DoesNotApplyLocalSignaturePolicy()
    {
        var profile = _tempDirectory.CreateSubdirectory("global-store-" + Guid.NewGuid().ToString("N"));
        var dir = profile.CreateSubdirectory(Path.Combine(".winapp", "tools", "msstore"));
        var exe = Path.Combine(dir.FullName, "msstore.exe");
        File.WriteAllText(exe, "fixture");
        var directories = new WinappDirectoryService(new CurrentDirectoryProvider(_tempDirectory.FullName))
        {
            UserProfileProvider = () => profile.FullName,
            CacheOverrideProvider = () => null,
        };
        var svc = new MSStoreCLIService(directories, NullLogger<MSStoreCLIService>.Instance)
        {
            SignatureVerifier = (_, _) => throw new AssertFailedException("Only local fallback changes trust policy."),
        };

        await svc.EnsureMSStoreCLIAvailableAsync(TestContext.CancellationToken);

        Assert.AreEqual(exe, svc.GetMSStoreCLIPath());
    }

    [TestMethod]
    public async Task LocalCache_FreshDownload_StillRequiresAuthenticExecutable()
    {
        var zip = BuildExeZip();
        var handler = new FakeHttpMessageHandler()
            .WhenUriContains(ReleaseApi, HttpStatusCode.OK,
                ReleaseJson("v1.2.3", "x64", "https://dl.test/local.zip", "https://dl.test/local.sha256"))
            .WhenUriContains("/local.zip", HttpStatusCode.OK, zip)
            .WhenUriContains("/local.sha256", HttpStatusCode.OK, $"{Sha256Hex(zip)}  MSStoreCLI-win-x64.zip");
        var svc = NewLocalService(handler, out var dir);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.EnsureMSStoreCLIAvailableAsync(TestContext.CancellationToken));

        StringAssert.Contains(error.Message, "not validly signed by Microsoft");
        Assert.IsFalse(File.Exists(Path.Combine(dir.FullName, "MSStoreCLI.zip")));
        Assert.Throws<InvalidOperationException>(() => svc.GetMSStoreCLIPath());
    }

    private static string ReleaseJson(string tag, string arch, string? zipUrl, string? checksumUrl)
    {
        var assets = new List<string>();
        if (zipUrl is not null)
        {
            assets.Add($$"""{ "name": "MSStoreCLI-win-{{arch}}.zip", "browser_download_url": "{{zipUrl}}" }""");
        }
        if (checksumUrl is not null)
        {
            assets.Add($$"""{ "name": "MSStoreCLI-win-{{arch}}.zip.sha256.txt", "browser_download_url": "{{checksumUrl}}" }""");
        }
        return $$"""{ "tag_name": "{{tag}}", "assets": [ {{string.Join(",", assets)}} ] }""";
    }

    [TestMethod]
    public async Task EnsureMSStoreCLIAvailableAsync_FullDownloadSuccess_ExtractsExe()
    {
        var zip = BuildExeZip();
        var handler = new FakeHttpMessageHandler()
            .WhenUriContains(ReleaseApi, HttpStatusCode.OK,
                ReleaseJson("v1.2.3", "x64", "https://dl.test/msstore-x64.zip", "https://dl.test/msstore-x64.sha256"))
            .WhenUriContains("/msstore-x64.zip", HttpStatusCode.OK, zip)
            .WhenUriContains("/msstore-x64.sha256", HttpStatusCode.OK, $"{Sha256Hex(zip)}  MSStoreCLI-win-x64.zip");
        var svc = NewService(handler, out var installDir);

        await svc.EnsureMSStoreCLIAvailableAsync(TestContext.CancellationToken);

        Assert.IsTrue(File.Exists(Path.Combine(installDir.FullName, "msstore.exe")), "extracted msstore.exe should exist");
        Assert.IsFalse(File.Exists(Path.Combine(installDir.FullName, "MSStoreCLI.zip")), "temp zip should be cleaned up");
    }

    [TestMethod]
    public async Task EnsureMSStoreCLIAvailableAsync_AssetMissing_UsesFallbackDownloadUrl()
    {
        var zip = BuildExeZip();
        // No zip asset in the release → the download URL falls back to the github.com convention.
        var handler = new FakeHttpMessageHandler()
            .WhenUriContains(ReleaseApi, HttpStatusCode.OK,
                ReleaseJson("v9.9.9", "x64", zipUrl: null, checksumUrl: "https://dl.test/cs.sha256"))
            .WhenUriContains("/releases/download/v9.9.9/MSStoreCLI-win-x64.zip", HttpStatusCode.OK, zip)
            .WhenUriContains("/cs.sha256", HttpStatusCode.OK, $"{Sha256Hex(zip)}  MSStoreCLI-win-x64.zip");
        var svc = NewService(handler, out var installDir);

        await svc.EnsureMSStoreCLIAvailableAsync(TestContext.CancellationToken);

        Assert.IsTrue(File.Exists(Path.Combine(installDir.FullName, "msstore.exe")));
    }

    [TestMethod]
    public async Task EnsureMSStoreCLIAvailableAsync_NullTagName_Throws()
    {
        var handler = new FakeHttpMessageHandler()
            .WhenUriContains(ReleaseApi, HttpStatusCode.OK, """{ "tag_name": null, "assets": [] }""");
        var svc = NewService(handler, out _);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            svc.EnsureMSStoreCLIAvailableAsync(TestContext.CancellationToken));
        StringAssert.Contains(ex.Message, "Could not determine the latest MSStoreCLI version");
    }

    [TestMethod]
    public async Task EnsureMSStoreCLIAvailableAsync_NoAssets_Throws()
    {
        var handler = new FakeHttpMessageHandler()
            .WhenUriContains(ReleaseApi, HttpStatusCode.OK, """{ "tag_name": "v1.0.0" }""");
        var svc = NewService(handler, out _);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            svc.EnsureMSStoreCLIAvailableAsync(TestContext.CancellationToken));
        StringAssert.Contains(ex.Message, "No assets found");
    }

    [TestMethod]
    public async Task EnsureMSStoreCLIAvailableAsync_ChecksumFileMissing_Throws()
    {
        var handler = new FakeHttpMessageHandler()
            .WhenUriContains(ReleaseApi, HttpStatusCode.OK,
                ReleaseJson("v1.0.0", "x64", "https://dl.test/msstore-x64.zip", checksumUrl: null));
        var svc = NewService(handler, out _);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            svc.EnsureMSStoreCLIAvailableAsync(TestContext.CancellationToken));
        StringAssert.Contains(ex.Message, "not found");
    }

    [TestMethod]
    public async Task EnsureMSStoreCLIAvailableAsync_InvalidChecksumFormat_Throws()
    {
        var handler = new FakeHttpMessageHandler()
            .WhenUriContains(ReleaseApi, HttpStatusCode.OK,
                ReleaseJson("v1.0.0", "x64", "https://dl.test/msstore-x64.zip", "https://dl.test/msstore-x64.sha256"))
            .WhenUriContains("/msstore-x64.sha256", HttpStatusCode.OK, "not-a-valid-hash  MSStoreCLI-win-x64.zip");
        var svc = NewService(handler, out _);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            svc.EnsureMSStoreCLIAvailableAsync(TestContext.CancellationToken));
        StringAssert.Contains(ex.Message, "Invalid SHA-256 checksum format");
    }

    [TestMethod]
    public async Task EnsureMSStoreCLIAvailableAsync_HashMismatch_Throws()
    {
        var zip = BuildExeZip();
        var wrongHash = new string('a', 64);
        var handler = new FakeHttpMessageHandler()
            .WhenUriContains(ReleaseApi, HttpStatusCode.OK,
                ReleaseJson("v1.0.0", "x64", "https://dl.test/msstore-x64.zip", "https://dl.test/msstore-x64.sha256"))
            .WhenUriContains("/msstore-x64.zip", HttpStatusCode.OK, zip)
            .WhenUriContains("/msstore-x64.sha256", HttpStatusCode.OK, $"{wrongHash}  MSStoreCLI-win-x64.zip");
        var svc = NewService(handler, out var installDir);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            svc.EnsureMSStoreCLIAvailableAsync(TestContext.CancellationToken));
        StringAssert.Contains(ex.Message, "SHA-256 hash mismatch");
        // The temp zip must still be cleaned up by the finally block even on failure.
        Assert.IsFalse(File.Exists(Path.Combine(installDir.FullName, "MSStoreCLI.zip")));
    }

    [TestMethod]
    public async Task EnsureMSStoreCLIAvailableAsync_DownloadHttpError_Throws()
    {
        var handler = new FakeHttpMessageHandler { NotMatchedStatus = HttpStatusCode.InternalServerError }
            .WhenUriContains(ReleaseApi, HttpStatusCode.OK,
                ReleaseJson("v1.0.0", "x64", "https://dl.test/msstore-x64.zip", "https://dl.test/msstore-x64.sha256"))
            .WhenUriContains("/msstore-x64.sha256", HttpStatusCode.OK,
                $"{new string('b', 64)}  MSStoreCLI-win-x64.zip");
        // Download URL is not matched → 500 → EnsureSuccessStatusCode throws.
        var svc = NewService(handler, out _);

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
            svc.EnsureMSStoreCLIAvailableAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task EnsureMSStoreCLIAvailableAsync_Arm64_UsesArm64Asset()
    {
        var zip = BuildExeZip();
        var handler = new FakeHttpMessageHandler()
            .WhenUriContains(ReleaseApi, HttpStatusCode.OK,
                ReleaseJson("v2.0.0", "arm64", "https://dl.test/msstore-arm64.zip", "https://dl.test/msstore-arm64.sha256"))
            .WhenUriContains("/msstore-arm64.zip", HttpStatusCode.OK, zip)
            .WhenUriContains("/msstore-arm64.sha256", HttpStatusCode.OK, $"{Sha256Hex(zip)}  MSStoreCLI-win-arm64.zip");
        var svc = NewService(handler, out var installDir, Architecture.Arm64);

        await svc.EnsureMSStoreCLIAvailableAsync(TestContext.CancellationToken);

        Assert.IsTrue(File.Exists(Path.Combine(installDir.FullName, "msstore.exe")));
    }

    [TestMethod]
    public async Task EnsureMSStoreCLIAvailableAsync_UnsupportedArch_Throws()
    {
        var svc = NewService(new FakeHttpMessageHandler(), out _, Architecture.X86);

        await Assert.ThrowsExactlyAsync<PlatformNotSupportedException>(() =>
            svc.EnsureMSStoreCLIAvailableAsync(TestContext.CancellationToken));
    }
}
