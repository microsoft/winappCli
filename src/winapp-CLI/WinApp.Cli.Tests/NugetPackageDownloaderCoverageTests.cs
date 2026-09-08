// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Net;
using System.Net.Sockets;
using System.Text;
using NuGet.Common;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using WinApp.Cli.Services;
using static WinApp.Cli.Tests.NugetFeedTestHelpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// Focused tests for <see cref="NugetPackageDownloader"/>'s per-source failover diagnostics against an
/// in-process v3 flat-container feed that lists a version in its package index but fails the actual
/// <c>.nupkg</c> content request. This drives the download-content error path (the branch that captures the
/// underlying source error and surfaces it instead of a plain "not found"), which a local folder feed cannot
/// reach because it never separates "version listed" from "content unavailable".
/// </summary>
[TestClass]
public class NugetPackageDownloaderCoverageTests : BaseCommandTests
{
    [TestMethod]
    public async Task DownloadPackageAsync_ContentEndpointFails_SurfacesUnderlyingSourceError()
    {
        using var feed = new ContentBrokenFeed(("Broken.Content.Pkg", "1.0.0"), contentStatusCode: 500);

        var root = CreateFeedTestDirectory();
        try
        {
            WriteNuGetConfig(root, $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="broken-content" value="{feed.IndexUrl}" allowInsecureConnections="true" />
                  </packageSources>
                  <disabledPackageSources>
                    <clear />
                  </disabledPackageSources>
                  <packageSourceMapping>
                    <clear />
                    <packageSource key="broken-content">
                      <package pattern="*" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """);

            var sourceProvider = CreateSourceProviderRootedAt(root);
            var downloader = new NugetPackageDownloader(sourceProvider);
            var globalPackagesFolder = Path.Join(root.FullName, "packages");
            Directory.CreateDirectory(globalPackagesFolder);

            var identity = new PackageIdentity("Broken.Content.Pkg", NuGetVersion.Parse("1.0.0"));
            using var cacheContext = new SourceCacheContext { NoCache = true, DirectDownload = true };

            // The version is listed, but every attempt to fetch the .nupkg content fails, so the download must
            // fail over and ultimately throw. Assert it takes the diagnostic path that names the source and its
            // underlying error — NOT the generic "not found" fallback (which would hide an auth/network failure).
            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await downloader.DownloadPackageAsync(identity, globalPackagesFolder, cacheContext, TestContext.CancellationToken));

            StringAssert.Contains(ex.Message, "Broken.Content.Pkg", StringComparison.Ordinal);
            StringAssert.Contains(ex.Message, "Last error from source 'broken-content'", StringComparison.Ordinal);
            Assert.IsFalse(
                ex.Message.Contains("was not found on any configured source", StringComparison.Ordinal),
                "A content-endpoint failure must surface the underlying source error, not the generic not-found fallback.");
            Assert.IsTrue(
                feed.ContentRequestCount > 0,
                "The download must have reached the .nupkg content endpoint; otherwise the guard short-circuited and the failover path was never exercised.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task DownloadPackageAsync_TempFileCleanupFails_SwallowsErrorAndStillSucceeds()
    {
        var root = CreateFeedTestDirectory();
        string? leakedTempFile = null;
        try
        {
            var feed = new DirectoryInfo(Path.Join(root.FullName, "feed"));
            feed.Create();
            var packages = new DirectoryInfo(Path.Join(root.FullName, "packages"));
            packages.Create();
            WriteLocalFeedConfig(root, feed, packages);
            WriteNupkgToFeed(feed, "Cleanup.Pkg", "1.0.0");

            var sourceProvider = CreateSourceProviderRootedAt(root);
            var downloader = new NugetPackageDownloader(sourceProvider)
            {
                // Force the best-effort temp-file cleanup to throw after the package has transferred.
                // Capture the path first so this test can delete the temp file itself — otherwise disabling
                // the product's cleanup would orphan the downloaded .nupkg under %TEMP% on every run.
                DeleteTempFile = path =>
                {
                    leakedTempFile = path;
                    throw new IOException("simulated temp-file cleanup failure");
                },
            };

            var identity = new PackageIdentity("Cleanup.Pkg", NuGetVersion.Parse("1.0.0"));
            using var cacheContext = new SourceCacheContext { NoCache = true, DirectDownload = true };

            // A failure to delete the temporary download must be swallowed: the package still extracts and the
            // method completes normally instead of surfacing the cleanup error to the caller.
            await downloader.DownloadPackageAsync(identity, packages.FullName, cacheContext, TestContext.CancellationToken);

            Assert.IsTrue(
                Directory.Exists(Path.Join(packages.FullName, "cleanup.pkg", "1.0.0")),
                "The package must have extracted into the global packages folder despite the temp-cleanup failure.");

            Assert.IsNotNull(leakedTempFile, "The temp-file cleanup seam must have been invoked with a real temp-file path.");
        }
        finally
        {
            // The product's cleanup was deliberately disabled above, so delete the orphaned temp file here to
            // avoid leaking a .nupkg into the system temp directory on every test run.
            if (leakedTempFile is not null)
            {
                try
                {
                    File.Delete(leakedTempFile);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best-effort: the file may already be gone.
                }
            }

            TryDelete(root);
        }
    }

    [TestMethod]
    public void CollectingLogger_CapturesWarningAndErrorMessages_IgnoringLowerLevels()
    {
        var logger = new NugetPackageDownloader.CollectingLogger();
        Assert.IsNull(logger.LastErrorMessage, "Nothing has been logged yet.");

        // Below-warning diagnostics are routine progress noise and must not be mistaken for a failure reason.
        logger.Log(new RestoreLogMessage(LogLevel.Information, "just progress"));
        Assert.IsNull(logger.LastErrorMessage, "An informational message must not be captured as an error.");

        logger.Log(new RestoreLogMessage(LogLevel.Warning, "content endpoint returned 403"));
        Assert.AreEqual("content endpoint returned 403", logger.LastErrorMessage, "A warning must be captured as the last error reason.");

        logger.Log(new RestoreLogMessage(LogLevel.Error, "download failed"));
        Assert.AreEqual("download failed", logger.LastErrorMessage, "A later error must overwrite the previously captured reason.");
    }

    [TestMethod]
    public async Task CollectingLogger_LogAsync_CapturesWarningMessage()
    {
        var logger = new NugetPackageDownloader.CollectingLogger();

        await logger.LogAsync(new RestoreLogMessage(LogLevel.Warning, "async warning"));

        Assert.AreEqual("async warning", logger.LastErrorMessage, "LogAsync must capture the message just like the synchronous path.");
    }

    /// <summary>
    /// Minimal in-process v3 flat-container feed that advertises a PackageBaseAddress, lists the configured
    /// versions in <c>flat/{id}/index.json</c>, but returns a caller-chosen error status for the actual
    /// <c>.nupkg</c> content request — so a download resolves the version yet cannot retrieve the package.
    /// </summary>
    private sealed class ContentBrokenFeed : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _serveLoop;
        private readonly Dictionary<string, string[]> _versionsById = new(StringComparer.OrdinalIgnoreCase);
        private readonly int _contentStatusCode;
        private int _contentRequestCount;

        public string BaseUrl { get; }

        public string IndexUrl => BaseUrl + "v3/index.json";

        /// <summary>Number of times the <c>.nupkg</c> content endpoint was requested (proves the download was actually attempted).</summary>
        public int ContentRequestCount => Volatile.Read(ref _contentRequestCount);

        public ContentBrokenFeed((string Id, string Version) package, int contentStatusCode)
        {
            _contentStatusCode = contentStatusCode;
            var lowerId = package.Id.ToLowerInvariant();
            _versionsById[lowerId] = [package.Version];

            (_listener, BaseUrl) = StartListener();
            _serveLoop = Task.Run(() => ServeAsync(_cts.Token));
        }

        private static (HttpListener Listener, string BaseUrl) StartListener()
        {
            for (var attempt = 0; ; attempt++)
            {
                using var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();

                var baseUrl = $"http://127.0.0.1:{port}/";
                var listener = new HttpListener();
                listener.Prefixes.Add(baseUrl);
                try
                {
                    listener.Start();
                    return (listener, baseUrl);
                }
                catch (HttpListenerException) when (attempt < 4)
                {
                    listener.Close();
                }
            }
        }

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException)
                {
                    // Dispose() stops and closes the listener, which is how this loop is terminated; any of
                    // these means the listener is gone, so exit rather than spin. Anything else is a real
                    // defect in the fake feed and is allowed to surface.
                    break;
                }

                try
                {
                    Handle(context);
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException)
                {
                    try
                    {
                        context.Response.Abort();
                    }
                    catch (Exception abortEx) when (abortEx is HttpListenerException or ObjectDisposedException)
                    {
                        // Best-effort; the client may already have disconnected.
                    }
                }
            }
        }

        private void Handle(HttpListenerContext context)
        {
            var response = context.Response;
            var path = context.Request.Url!.AbsolutePath.TrimStart('/');

            if (path == "v3/index.json")
            {
                WriteJson(response, $$"""{"version":"3.0.0","resources":[{"@id":"{{BaseUrl}}flat/","@type":"PackageBaseAddress/3.0.0"}]}""");
                return;
            }

            if (path.StartsWith("flat/", StringComparison.Ordinal))
            {
                var rest = path["flat/".Length..];
                if (rest.EndsWith("/index.json", StringComparison.Ordinal))
                {
                    var id = rest[..^"/index.json".Length];
                    if (_versionsById.TryGetValue(id, out var versions))
                    {
                        WriteJson(response, "{\"versions\":[" + string.Join(",", versions.Select(v => $"\"{v}\"")) + "]}");
                        return;
                    }
                }
                else if (rest.EndsWith(".nupkg", StringComparison.Ordinal))
                {
                    // The version is listed, but the content endpoint fails: the download must fail over and
                    // surface the source error rather than treat the package as missing.
                    Interlocked.Increment(ref _contentRequestCount);
                    response.StatusCode = _contentStatusCode;
                    response.Close();
                    return;
                }
            }

            response.StatusCode = 404;
            response.Close();
        }

        private static void WriteJson(HttpListenerResponse response, string json)
        {
            var body = Encoding.UTF8.GetBytes(json);
            response.StatusCode = 200;
            response.ContentType = "application/json";
            response.ContentLength64 = body.Length;
            response.OutputStream.Write(body, 0, body.Length);
            response.Close();
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                // Best-effort teardown.
            }

            try
            {
                _serveLoop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is AggregateException or OperationCanceledException)
            {
                // The loop observes cancellation/disposal; ignore any teardown race.
            }

            _cts.Dispose();
        }
    }
}
