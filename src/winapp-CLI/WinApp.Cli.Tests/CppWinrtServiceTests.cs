// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="CppWinrtService"/>, which locates the cppwinrt.exe tool in the NuGet
/// cache and drives it through a generated response (.rsp) file to project WinRT metadata into
/// C++/WinRT headers. The process is exercised end-to-end using a tiny batch stub standing in
/// for cppwinrt.exe so both the success and non-zero-exit paths are covered without the real tool.
/// </summary>
[TestClass]
public class CppWinrtServiceTests : BaseCommandTests
{
    private const string CppWinrtPackageId = "Microsoft.Windows.CppWinRT";

    // ---------------------------------------------------------------------
    // FindCppWinrtExe
    // ---------------------------------------------------------------------

    [TestMethod]
    public void FindCppWinrtExe_PackageNotInUsedVersions_ReturnsNull()
    {
        var service = new CppWinrtService(NullLogger<CppWinrtService>.Instance);
        var packages = _tempDirectory.CreateSubdirectory("packages");

        var result = service.FindCppWinrtExe(packages, new Dictionary<string, string>());

        Assert.IsNull(result);
    }

    [TestMethod]
    public void FindCppWinrtExe_ExeMissingOnDisk_ReturnsNull()
    {
        var service = new CppWinrtService(NullLogger<CppWinrtService>.Instance);
        var packages = _tempDirectory.CreateSubdirectory("packages");
        var usedVersions = new Dictionary<string, string> { [CppWinrtPackageId] = "2.0.240111.5" };

        // Version is known but no cppwinrt.exe exists under the expected layout.
        var result = service.FindCppWinrtExe(packages, usedVersions);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void FindCppWinrtExe_ExePresent_ReturnsFileInfo()
    {
        var service = new CppWinrtService(NullLogger<CppWinrtService>.Instance);
        var packages = _tempDirectory.CreateSubdirectory("packages");
        const string version = "2.0.240111.5";

        // NuGet cache layout: {cache}/{lowercase-id}/{version}/bin/cppwinrt.exe
        var binDir = Directory.CreateDirectory(
            Path.Combine(packages.FullName, CppWinrtPackageId.ToLowerInvariant(), version, "bin"));
        var exePath = Path.Combine(binDir.FullName, "cppwinrt.exe");
        File.WriteAllText(exePath, "stub");

        var usedVersions = new Dictionary<string, string> { [CppWinrtPackageId] = version };

        var result = service.FindCppWinrtExe(packages, usedVersions);

        Assert.IsNotNull(result);
        Assert.AreEqual(exePath, result.FullName);
    }

    // ---------------------------------------------------------------------
    // RunWithRspAsync
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task RunWithRspAsync_Success_WritesResponseFileAndCreatesOutputDir()
    {
        var service = new CppWinrtService(NullLogger<CppWinrtService>.Instance);
        var exe = CreateStubExe("cppwinrt-ok", exitCode: 0);
        var winmd = CreateDummyWinmd("Microsoft.UI.winmd");
        var outputDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "generated"));
        var workingDir = _tempDirectory;

        await service.RunWithRspAsync(exe, new[] { winmd }, outputDir, workingDir, TestTaskContext, TestContext.CancellationToken);

        Assert.IsTrue(outputDir.Exists, "Output directory should be created.");
        var rspPath = Path.Combine(outputDir.FullName, ".cppwinrt.rsp");
        Assert.IsTrue(File.Exists(rspPath), "Response file should be written.");

        var rsp = await File.ReadAllTextAsync(rspPath);
        StringAssert.Contains(rsp, $"-input \"{winmd.FullName}\"");
        StringAssert.Contains(rsp, "-optimize");
        StringAssert.Contains(rsp, $"-output \"{outputDir.FullName}\"");
    }

    [TestMethod]
    public async Task RunWithRspAsync_MultipleWinmds_EmitsInputLinePerWinmd()
    {
        var service = new CppWinrtService(NullLogger<CppWinrtService>.Instance);
        var exe = CreateStubExe("cppwinrt-multi", exitCode: 0);
        var winmdA = CreateDummyWinmd("A.winmd");
        var winmdB = CreateDummyWinmd("B.winmd");
        var outputDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "gen-multi"));

        await service.RunWithRspAsync(exe, new[] { winmdA, winmdB }, outputDir, _tempDirectory, TestTaskContext, TestContext.CancellationToken);

        var rsp = await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, ".cppwinrt.rsp"));
        StringAssert.Contains(rsp, $"-input \"{winmdA.FullName}\"");
        StringAssert.Contains(rsp, $"-input \"{winmdB.FullName}\"");
    }

    [TestMethod]
    public async Task RunWithRspAsync_NonZeroExit_ThrowsInvalidOperationException()
    {
        var service = new CppWinrtService(NullLogger<CppWinrtService>.Instance);
        var exe = CreateStubExe("cppwinrt-fail", exitCode: 1);
        var winmd = CreateDummyWinmd("Fail.winmd");
        var outputDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "gen-fail"));

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await service.RunWithRspAsync(exe, new[] { winmd }, outputDir, _tempDirectory, TestTaskContext, TestContext.CancellationToken));

        StringAssert.Contains(ex.Message, "cppwinrt");
    }

    [TestMethod]
    public async Task RunWithRspAsync_DebugLoggingEnabled_AddsVerboseFlag()
    {
        // A logger that reports Debug as enabled must cause "-verbose" to be added to the rsp.
        var service = new CppWinrtService(new AlwaysEnabledLogger<CppWinrtService>());
        var exe = CreateStubExe("cppwinrt-verbose", exitCode: 0);
        var winmd = CreateDummyWinmd("Verbose.winmd");
        var outputDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "gen-verbose"));

        await service.RunWithRspAsync(exe, new[] { winmd }, outputDir, _tempDirectory, TestTaskContext, TestContext.CancellationToken);

        var rsp = await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, ".cppwinrt.rsp"));
        StringAssert.Contains(rsp, "-verbose");
    }

    [TestMethod]
    public async Task RunWithRspAsync_DebugLoggingDisabled_OmitsVerboseFlag()
    {
        var service = new CppWinrtService(NullLogger<CppWinrtService>.Instance);
        var exe = CreateStubExe("cppwinrt-quiet", exitCode: 0);
        var winmd = CreateDummyWinmd("Quiet.winmd");
        var outputDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "gen-quiet"));

        await service.RunWithRspAsync(exe, new[] { winmd }, outputDir, _tempDirectory, TestTaskContext, TestContext.CancellationToken);

        var rsp = await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, ".cppwinrt.rsp"));
        Assert.IsFalse(rsp.Contains("-verbose", StringComparison.Ordinal), "Non-debug logging must not add -verbose.");
    }

    [TestMethod]
    public async Task RunWithRspAsync_ResponseFileHasNoBom()
    {
        var service = new CppWinrtService(NullLogger<CppWinrtService>.Instance);
        var exe = CreateStubExe("cppwinrt-bom", exitCode: 0);
        var winmd = CreateDummyWinmd("Bom.winmd");
        var outputDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "gen-bom"));

        await service.RunWithRspAsync(exe, new[] { winmd }, outputDir, _tempDirectory, TestTaskContext, TestContext.CancellationToken);

        var bytes = await File.ReadAllBytesAsync(Path.Combine(outputDir.FullName, ".cppwinrt.rsp"));
        Assert.IsFalse(
            bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "Response file must be UTF-8 without BOM so cppwinrt.exe can parse it.");
    }

    [TestMethod]
    public async Task RunWithRspAsync_ProcessEmitsStdoutAndStderr_LogsBothStreams()
    {
        // The tool's stdout and stderr are surfaced as debug messages; a Debug-enabled
        // logger (BaseCommandTests wires logging at Debug level) renders them to the console.
        var service = new CppWinrtService(NullLogger<CppWinrtService>.Instance);
        var exe = CreateStubExeWithOutput("cppwinrt-io", stdoutToken: "cppwinrtout", stderrToken: "cppwinrterr", exitCode: 0);
        var winmd = CreateDummyWinmd("Io.winmd");
        var outputDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "gen-io"));

        await service.RunWithRspAsync(exe, new[] { winmd }, outputDir, _tempDirectory, TestTaskContext, TestContext.CancellationToken);

        var messages = TestTask.SubTasks.Select(t => t.InProgressMessage).ToList();
        Assert.IsTrue(messages.Any(m => m.Contains("cppwinrtout", StringComparison.Ordinal)), "stdout from the tool should be logged as a debug message.");
        Assert.IsTrue(messages.Any(m => m.Contains("cppwinrterr", StringComparison.Ordinal)), "stderr from the tool should be logged as a debug message.");
    }

    [TestMethod]
    public async Task RunWithRspAsync_ProcessEmitsStderrThenFails_LogsStderrAndThrows()
    {
        // Non-zero exit combined with stderr output exercises the stderr-logging branch
        // followed by the failure throw.
        var service = new CppWinrtService(NullLogger<CppWinrtService>.Instance);
        var exe = CreateStubExeWithOutput("cppwinrt-errfail", stdoutToken: null, stderrToken: "cppwinrtfaildiag", exitCode: 3);
        var winmd = CreateDummyWinmd("ErrFail.winmd");
        var outputDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "gen-errfail"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await service.RunWithRspAsync(exe, new[] { winmd }, outputDir, _tempDirectory, TestTaskContext, TestContext.CancellationToken));

        var messages = TestTask.SubTasks.Select(t => t.InProgressMessage).ToList();
        Assert.IsTrue(messages.Any(m => m.Contains("cppwinrtfaildiag", StringComparison.Ordinal)), "Diagnostic stderr should be logged before the failure is raised.");
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private FileInfo CreateDummyWinmd(string name)
    {
        var path = Path.Combine(_tempDirectory.FullName, name);
        File.WriteAllText(path, string.Empty);
        return new FileInfo(path);
    }

    /// <summary>
    /// Creates a batch stub that stands in for cppwinrt.exe. It ignores its response-file
    /// argument and returns the requested exit code. Batch files run through the same
    /// UseShellExecute=false path as the real tool (see BuildToolsService tests).
    /// </summary>
    private FileInfo CreateStubExe(string name, int exitCode)
    {
        var path = Path.Combine(_tempDirectory.FullName, $"{name}.cmd");
        File.WriteAllText(path, $"@echo off\r\nexit /b {exitCode}\r\n");
        return new FileInfo(path);
    }

    /// <summary>
    /// Creates a batch stub that writes the given tokens to stdout and/or stderr before
    /// returning <paramref name="exitCode"/>, so the stdout/stderr logging branches can be exercised.
    /// </summary>
    private FileInfo CreateStubExeWithOutput(string name, string? stdoutToken, string? stderrToken, int exitCode)
    {
        var path = Path.Combine(_tempDirectory.FullName, $"{name}.cmd");
        var sb = new System.Text.StringBuilder();
        sb.Append("@echo off\r\n");
        if (stdoutToken is not null)
        {
            sb.Append($"echo {stdoutToken}\r\n");
        }
        if (stderrToken is not null)
        {
            sb.Append($"echo {stderrToken} 1>&2\r\n");
        }
        sb.Append($"exit /b {exitCode}\r\n");
        File.WriteAllText(path, sb.ToString());
        return new FileInfo(path);
    }

    private sealed class AlwaysEnabledLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // No-op: this logger exists only to force IsEnabled(Debug) == true.
        }
    }
}
