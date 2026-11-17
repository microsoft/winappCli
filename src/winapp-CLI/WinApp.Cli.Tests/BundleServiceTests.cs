// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;
using WinApp.Cli.Tools;

namespace WinApp.Cli.Tests;

[TestClass]
public class BundleServiceTests
{
    private DirectoryInfo _tempDir = null!;
    private FakeBuildToolsService _buildToolsService = null!;
    private BundleService _service = null!;
    private TaskContext _taskContext = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"BundleSvcTest_{Guid.NewGuid():N}"));
        _tempDir.Create();
        _buildToolsService = new FakeBuildToolsService();
        _service = new BundleService(_buildToolsService, NullLogger<BundleService>.Instance);

        var task = new GroupableTask("test", null);
        var console = new TestConsole();
        var logger = NullLogger<BundleService>.Instance;
        var renderLock = new Lock();
        _taskContext = new TaskContext(task, null, console, logger, renderLock);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_tempDir.Exists)
        {
            _tempDir.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task CreateBundleAsync_EmptyList_ThrowsArgumentException()
    {
        var output = new FileInfo(Path.Combine(_tempDir.FullName, "out.msixbundle"));

        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            _service.CreateBundleAsync([], output, _taskContext));
    }

    [TestMethod]
    public async Task CreateBundleAsync_MultipleMsixFiles_InvokesMakeappxBundleWithCorrectArgs()
    {
        // Arrange
        var file1 = CreateFakeMsix("app_x64.msix");
        var file2 = CreateFakeMsix("app_arm64.msix");
        var output = new FileInfo(Path.Combine(_tempDir.FullName, "output", "app.msixbundle"));

        // Act
        await _service.CreateBundleAsync([file1, file2], output, _taskContext);

        // Assert
        Assert.AreEqual(1, _buildToolsService.Invocations.Count);
        var (toolName, args) = _buildToolsService.Invocations[0];
        Assert.AreEqual("makeappx.exe", toolName);
        Assert.Contains("bundle", args);
        Assert.Contains("/p", args);
        Assert.Contains("app.msixbundle", args);
    }

    [TestMethod]
    public async Task CreateBundleAsync_CreatesOutputDirectory()
    {
        // Arrange
        var file1 = CreateFakeMsix("slice.msix");
        var outputDir = Path.Combine(_tempDir.FullName, "nested", "dir");
        var output = new FileInfo(Path.Combine(outputDir, "bundle.msixbundle"));

        // Act
        await _service.CreateBundleAsync([file1], output, _taskContext);

        // Assert
        Assert.IsTrue(Directory.Exists(outputDir));
    }

    [TestMethod]
    public async Task CreateBundleAsync_WithBundleVersion_PassesBvArgumentToMakeappx()
    {
        // Arrange
        var file1 = CreateFakeMsix("app_x64.msix");
        var output = new FileInfo(Path.Combine(_tempDir.FullName, "versioned.msixbundle"));

        // Act
        await _service.CreateBundleAsync([file1], output, _taskContext, bundleVersion: new MsixVersion(1, 2, 3, 4));

        // Assert - the makeappx invocation must include /bv "1.2.3.4" so the resulting
        // Bundle.Identity/@Version matches the source manifest's Identity/@Version.
        Assert.AreEqual(1, _buildToolsService.Invocations.Count);
        var (_, args) = _buildToolsService.Invocations[0];
        Assert.Contains("/bv \"1.2.3.4\"", args);
    }

    [TestMethod]
    public async Task CreateBundleAsync_WithoutBundleVersion_OmitsBvArgument()
    {
        // Arrange
        var file1 = CreateFakeMsix("app_x64.msix");
        var output = new FileInfo(Path.Combine(_tempDir.FullName, "unversioned.msixbundle"));

        // Act - no bundleVersion supplied (default null), preserving existing makeappx default
        // (timestamp-derived) bundle version behavior when the manifest version is unavailable.
        await _service.CreateBundleAsync([file1], output, _taskContext);

        // Assert
        Assert.AreEqual(1, _buildToolsService.Invocations.Count);
        var (_, args) = _buildToolsService.Invocations[0];
        Assert.IsFalse(args.Contains("/bv", StringComparison.Ordinal), $"Expected no /bv argument when bundleVersion is null. Args: {args}");
    }

    private FileInfo CreateFakeMsix(string name)
    {
        var path = Path.Combine(_tempDir.FullName, name);
        File.WriteAllText(path, "fake msix content");
        return new FileInfo(path);
    }

    [TestMethod]
    public async Task CreateBundleAsync_StagingCleanupFailure_IsSwallowed()
    {
        var file1 = CreateFakeMsix("slice.msix");
        var output = new FileInfo(Path.Combine(_tempDir.FullName, "swallow.msixbundle"));

        FileStream? held = null;
        string? stagingDir = null;

        // While "makeappx" runs, hold a file in the staging directory open with no sharing so the
        // finally-block cleanup Delete throws — which the service must swallow (not rethrow).
        _buildToolsService.OnRun = args =>
        {
            stagingDir = ExtractDirectoryArg(args);
            held = new FileStream(Path.Combine(stagingDir!, "held.lock"), FileMode.Create, FileAccess.Write, FileShare.None);
        };

        try
        {
            // Must complete without throwing even though staging cleanup fails.
            await _service.CreateBundleAsync([file1], output, _taskContext);
        }
        finally
        {
            held?.Dispose();
            if (stagingDir != null && Directory.Exists(stagingDir))
            {
                Directory.Delete(stagingDir, recursive: true);
            }
        }
    }

    /// <summary>Extracts the staging directory from a makeappx <c>bundle /d "..."</c> argument string.</summary>
    private static string ExtractDirectoryArg(string arguments)
    {
        const string marker = "/d \"";
        var start = arguments.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = arguments.IndexOf('"', start);
        var dir = arguments[start..end];
        if (dir.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            dir = dir[4..];
        }
        return dir;
    }
}
