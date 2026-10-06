// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
    public async Task EnsureMSStoreCLIAvailableAsync_InstallFolderNotWritable_FailsClearlyBeforeAnyDownload()
    {
        var handler = new FakeHttpMessageHandler();
        var svc = NewService(handler, out var installDir);
        // A file where the tools folder should be makes the install folder impossible to create,
        // as a denied global winapp folder does.
        File.WriteAllText(installDir.Parent!.FullName, string.Empty);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            svc.EnsureMSStoreCLIAvailableAsync(TestContext.CancellationToken));

        StringAssert.Contains(ex.Message, installDir.FullName);
        StringAssert.Contains(ex.Message, "WINAPP_CLI_CACHE_DIRECTORY");
        Assert.AreEqual(0, handler.Requests.Count, "No network work should happen when the install can't succeed.");
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
