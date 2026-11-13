// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <remarks>
/// <para><b>Documented coverage ceiling (~97% Debug line coverage).</b> The acquisition orchestration
/// (<c>TryAcquireCoreAsync</c>: range-read, MSIX extraction, signature + engine-compatibility verification,
/// ret/failure paths) is covered hermetically via the injected reader and verifier seams. Two residual
/// lines are pure OS/network boundaries left honestly uncovered rather than exercised against real
/// infrastructure:</para>
/// <list type="bullet">
///   <item>50 — the <c>HostArchitectureProvider</c> seam's default lambda
///   (<c>() =&gt; RuntimeInformation.ProcessArchitecture</c>): the OS boundary, overridden in every test.</item>
///   <item>68-70 — the real <c>HttpClient</c> / <c>HttpRangeReader</c> wiring in the public
///   <c>TryAcquireAsync</c> wrapper: the live-network entry point that constructs the reader passed into the
///   fully-tested <c>TryAcquireCoreAsync</c>.</item>
/// </list>
/// </remarks>
[TestClass]
[DoNotParallelize] // mutates static verification/architecture seams on WinDbgJsProviderAcquirer
public class WinDbgJsProviderAcquirerTests
{
    private const string InnerMsixName = "windbg_win-arm64.msix";
    private const string Prefix = "arm64";
    private const string JsProviderPath = "arm64/winext/JsProvider.dll";

    private string _tempDir = null!;
    private Func<string, Microsoft.Extensions.Logging.ILogger, bool> _origSig = null!;
    private Func<string, string, Microsoft.Extensions.Logging.ILogger, bool> _origEngine = null!;
    private Func<Architecture> _origArch = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"JsAcq_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _origSig = WinDbgJsProviderAcquirer.SignatureVerifier;
        _origEngine = WinDbgJsProviderAcquirer.EngineCompatibilityVerifier;
        _origArch = WinDbgJsProviderAcquirer.HostArchitectureProvider;
    }

    [TestCleanup]
    public void Cleanup()
    {
        WinDbgJsProviderAcquirer.SignatureVerifier = _origSig;
        WinDbgJsProviderAcquirer.EngineCompatibilityVerifier = _origEngine;
        WinDbgJsProviderAcquirer.HostArchitectureProvider = _origArch;
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    // ---- TryAcquireAsync architecture gate ----------------------------------------------------

    [TestMethod]
    public async Task TryAcquireAsync_UnsupportedArchitecture_SkipsWithoutNetwork()
    {
        WinDbgJsProviderAcquirer.HostArchitectureProvider = () => Architecture.Wasm;

        var acquired = await WinDbgJsProviderAcquirer.TryAcquireAsync(
            new DirectoryInfo(_tempDir), NullLogger.Instance, CancellationToken.None);

        Assert.IsFalse(acquired, "An unsupported host architecture must short-circuit before any download.");
    }

    // ---- TryAcquireCoreAsync (network-free orchestration) -------------------------------------

    [TestMethod]
    public async Task TryAcquireCoreAsync_ValidBundleAndVerifiersPass_PublishesProvider()
    {
        var (bundle, expected) = BuildNestedBundle();
        WinDbgJsProviderAcquirer.SignatureVerifier = (_, _) => true;
        WinDbgJsProviderAcquirer.EngineCompatibilityVerifier = (_, _, _) => true;

        var acquired = await WinDbgJsProviderAcquirer.TryAcquireCoreAsync(
            new MemoryRangeReader(bundle), new DirectoryInfo(_tempDir), InnerMsixName, Prefix,
            NullLogger.Instance, CancellationToken.None);

        Assert.IsTrue(acquired, "A valid bundle with passing verifiers must publish the provider.");
        var published = Path.Combine(_tempDir, "JsProvider.dll");
        Assert.IsTrue(File.Exists(published), "JsProvider.dll must be published into the destination directory.");
        Assert.AreSequenceEqual(expected, await File.ReadAllBytesAsync(published),
            "Published bytes must match the extracted provider.");
    }

    [TestMethod]
    public async Task TryAcquireCoreAsync_SignatureRejected_DiscardsAndReturnsFalse()
    {
        var (bundle, _) = BuildNestedBundle();
        WinDbgJsProviderAcquirer.SignatureVerifier = (_, _) => false;
        WinDbgJsProviderAcquirer.EngineCompatibilityVerifier = (_, _, _) => true;

        var acquired = await WinDbgJsProviderAcquirer.TryAcquireCoreAsync(
            new MemoryRangeReader(bundle), new DirectoryInfo(_tempDir), InnerMsixName, Prefix,
            NullLogger.Instance, CancellationToken.None);

        Assert.IsFalse(acquired, "An unsigned provider must be rejected.");
        Assert.IsFalse(File.Exists(Path.Combine(_tempDir, "JsProvider.dll")),
            "A rejected provider must not be published to the final path.");
    }

    [TestMethod]
    public async Task TryAcquireCoreAsync_EngineMismatch_DiscardsAndReturnsFalse()
    {
        var (bundle, _) = BuildNestedBundle();
        WinDbgJsProviderAcquirer.SignatureVerifier = (_, _) => true;
        WinDbgJsProviderAcquirer.EngineCompatibilityVerifier = (_, _, _) => false;

        var acquired = await WinDbgJsProviderAcquirer.TryAcquireCoreAsync(
            new MemoryRangeReader(bundle), new DirectoryInfo(_tempDir), InnerMsixName, Prefix,
            NullLogger.Instance, CancellationToken.None);

        Assert.IsFalse(acquired, "A provider whose build mismatches the engine must be rejected.");
        Assert.IsFalse(File.Exists(Path.Combine(_tempDir, "JsProvider.dll")));
    }

    [TestMethod]
    public async Task TryAcquireCoreAsync_ProviderNotInBundle_ReturnsFalse()
    {
        var (bundle, _) = BuildNestedBundle();
        WinDbgJsProviderAcquirer.SignatureVerifier = (_, _) => true;
        WinDbgJsProviderAcquirer.EngineCompatibilityVerifier = (_, _, _) => true;

        // Ask for an inner msix that is not present -> ExtractJsProviderAsync returns null.
        var acquired = await WinDbgJsProviderAcquirer.TryAcquireCoreAsync(
            new MemoryRangeReader(bundle), new DirectoryInfo(_tempDir), "windbg_win-missing.msix", Prefix,
            NullLogger.Instance, CancellationToken.None);

        Assert.IsFalse(acquired, "A bundle missing the provider must yield false, not throw.");
    }

    [TestMethod]
    public async Task TryAcquireCoreAsync_ReaderThrows_SwallowsAndReturnsFalse()
    {
        var acquired = await WinDbgJsProviderAcquirer.TryAcquireCoreAsync(
            new ThrowingRangeReader(new InvalidOperationException("boom")), new DirectoryInfo(_tempDir),
            InnerMsixName, Prefix, NullLogger.Instance, CancellationToken.None);

        Assert.IsFalse(acquired, "An unexpected failure must be logged and swallowed (fail open).");
    }

    [TestMethod]
    public async Task TryAcquireCoreAsync_ReaderCancelled_PropagatesCancellation()
    {
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await WinDbgJsProviderAcquirer.TryAcquireCoreAsync(
                new ThrowingRangeReader(new OperationCanceledException()), new DirectoryInfo(_tempDir),
                InnerMsixName, Prefix, NullLogger.Instance, CancellationToken.None));
    }

    // ---- ExtractJsProviderAsync PE validation -------------------------------------------------

    [TestMethod]
    public async Task ExtractJsProviderAsync_NonPeProvider_Throws()
    {
        // The extracted JsProvider entry does not start with the "MZ" PE signature.
        var (bundle, _) = BuildNestedBundle(providerPayload: Encoding.UTF8.GetBytes("not-a-pe-image-at-all"));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await WinDbgJsProviderAcquirer.ExtractJsProviderAsync(
                new MemoryRangeReader(bundle), InnerMsixName, Prefix, CancellationToken.None));
    }

    // ---- HttpRangeReader (fake transport) -----------------------------------------------------

    [TestMethod]
    public async Task HttpRangeReader_GetLength_ReturnsContentRangeTotalAndCaches()
    {
        var handler = new StubHandler((_, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([0]) };
            resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, 987654);
            return resp;
        });
        using var http = new HttpClient(handler);
        var reader = new WinDbgJsProviderAcquirer.HttpRangeReader(http, "https://example.test/bundle");

        Assert.AreEqual(987654, await reader.GetLengthAsync(CancellationToken.None));
        Assert.AreEqual(987654, await reader.GetLengthAsync(CancellationToken.None));
        Assert.AreEqual(1, handler.CallCount, "The total length must be cached after the first request.");
    }

    [TestMethod]
    public async Task HttpRangeReader_GetLength_NoContentRange_Throws()
    {
        var handler = new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0]) });
        using var http = new HttpClient(handler);
        var reader = new WinDbgJsProviderAcquirer.HttpRangeReader(http, "https://example.test/bundle");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await reader.GetLengthAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task HttpRangeReader_GetLength_NonSuccess_Throws()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);
        var reader = new WinDbgJsProviderAcquirer.HttpRangeReader(http, "https://example.test/bundle");

        await Assert.ThrowsExactlyAsync<HttpRequestException>(async () =>
            await reader.GetLengthAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task HttpRangeReader_Read_PartialContentExactLength_ReturnsBytes()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var handler = new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(payload) });
        using var http = new HttpClient(handler);
        var reader = new WinDbgJsProviderAcquirer.HttpRangeReader(http, "https://example.test/bundle");

        var bytes = await reader.ReadAsync(100, payload.Length, CancellationToken.None);

        Assert.AreSequenceEqual(payload, bytes);
    }

    [TestMethod]
    public async Task HttpRangeReader_Read_RetriesAfterNon206ThenSucceeds()
    {
        var payload = new byte[] { 9, 8, 7 };
        var handler = new StubHandler((_, attempt) => attempt == 0
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) } // wrong status
            : new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(payload) });
        using var http = new HttpClient(handler);
        var reader = new WinDbgJsProviderAcquirer.HttpRangeReader(http, "https://example.test/bundle");

        var bytes = await reader.ReadAsync(0, payload.Length, CancellationToken.None);

        Assert.AreSequenceEqual(payload, bytes);
        Assert.AreEqual(2, handler.CallCount, "A non-206 response must trigger exactly one retry here.");
    }

    [TestMethod]
    public async Task HttpRangeReader_Read_ShortReadExhaustsRetries_Throws()
    {
        // Always return 206 but with fewer bytes than requested -> short-read path every attempt.
        var handler = new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([1, 2]) });
        using var http = new HttpClient(handler);
        var reader = new WinDbgJsProviderAcquirer.HttpRangeReader(http, "https://example.test/bundle");

        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await reader.ReadAsync(0, 16, CancellationToken.None));
        Assert.AreEqual(5, handler.CallCount, "All five attempts must be exhausted before failing.");
    }

    [TestMethod]
    public async Task HttpRangeReader_Read_Cancelled_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([1]) });
        using var http = new HttpClient(handler);
        var reader = new WinDbgJsProviderAcquirer.HttpRangeReader(http, "https://example.test/bundle");

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
            await reader.ReadAsync(0, 1, cts.Token));
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static byte[] ValidPeProvider()
    {
        var payload = new byte[2048];
        payload[0] = (byte)'M';
        payload[1] = (byte)'Z';
        for (var i = 2; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 11);
        }

        return payload;
    }

    private static byte[] BuildZip(IEnumerable<(string Name, byte[] Data, CompressionLevel Level)> entries)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data, level) in entries)
            {
                var entry = archive.CreateEntry(name, level);
                using var stream = entry.Open();
                stream.Write(data, 0, data.Length);
            }
        }

        return ms.ToArray();
    }

    private static (byte[] Bundle, byte[] ExpectedProvider) BuildNestedBundle(byte[]? providerPayload = null)
    {
        var js = providerPayload ?? ValidPeProvider();
        var innerMsix = BuildZip(
        [
            ("AppxManifest.xml", Encoding.UTF8.GetBytes("<manifest/>"), CompressionLevel.Optimal),
            (JsProviderPath, js, CompressionLevel.Optimal),
        ]);

        var bundle = BuildZip(
        [
            ("AppxMetadata/AppxBundleManifest.xml", Encoding.UTF8.GetBytes("<bundle/>"), CompressionLevel.Optimal),
            (InnerMsixName, innerMsix, CompressionLevel.NoCompression),
        ]);

        return (bundle, js);
    }

    private sealed class ThrowingRangeReader(Exception toThrow) : IRangeReader
    {
        public Task<long> GetLengthAsync(CancellationToken cancellationToken) => throw toThrow;

        public Task<byte[]> ReadAsync(long offset, int length, CancellationToken cancellationToken) => throw toThrow;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = responder(request, CallCount);
            CallCount++;
            return Task.FromResult(response);
        }
    }
}
