// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="WindowsAppRuntimeService.ParseMsixInventoryAsync"/> arch selection.
/// The Windows App Runtime MSIX packages are laid out per-arch under <c>win10-{arch}</c>; project mode
/// must read the inventory for the app's resolved TARGET architecture, not the CLI host arch, so a
/// cross-arch run installs the right packages. A missing/empty inventory must return <c>null</c>
/// (no packages) rather than silently succeeding.
/// </summary>
[TestClass]
public class WorkspaceSetupServiceInventoryTests
{
    private DirectoryInfo _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"WsInventoryTests_{Guid.NewGuid():N}"));
        _tempDir.Create();
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { _tempDir.Delete(true); } catch { /* ignore */ }
    }

    private static TaskContext CreateTaskContext()
    {
        var task = new GroupableTask("test", null);
        var console = new TestConsole();
        var renderLock = new Lock();
        return new TaskContext(task, null, console, NullLogger<TaskContext>.Instance, renderLock);
    }

    private void WriteInventory(string arch, params string[] entries)
    {
        var archDir = Path.Combine(_tempDir.FullName, $"win10-{arch}");
        Directory.CreateDirectory(archDir);
        File.WriteAllLines(Path.Combine(archDir, "msix.inventory"), entries);
    }

    [TestMethod]
    public async Task ParseMsixInventoryAsync_ReadsTargetArchInventory_NotHostArch()
    {
        // Both arch inventories exist with DISTINCT entries. Requesting arm64 must read the arm64
        // inventory regardless of the host arch — the H1 bug (hard-pinned to the host arch) would
        // read the wrong directory here.
        WriteInventory("x64", "runtime.x64.msix=Microsoft.WindowsAppRuntime.1.7_x64");
        WriteInventory("arm64", "runtime.arm64.msix=Microsoft.WindowsAppRuntime.1.7_arm64");

        var entries = await WindowsAppRuntimeService.ParseMsixInventoryAsync(
            CreateTaskContext(), _tempDir, CancellationToken.None, architecture: "arm64");

        Assert.IsNotNull(entries);
        Assert.AreEqual(1, entries!.Count);
        Assert.AreEqual("runtime.arm64.msix", entries[0].FileName);
        Assert.Contains("arm64", entries[0].PackageIdentity);
    }

    [TestMethod]
    public async Task ParseMsixInventoryAsync_MissingArchDir_ReturnsNull()
    {
        // Only x64 exists; requesting arm64 finds no win10-arm64 dir → null (no packages), never a
        // false "installed" signal.
        WriteInventory("x64", "runtime.x64.msix=Microsoft.WindowsAppRuntime.1.7_x64");

        var entries = await WindowsAppRuntimeService.ParseMsixInventoryAsync(
            CreateTaskContext(), _tempDir, CancellationToken.None, architecture: "arm64");

        Assert.IsNull(entries);
    }

    [TestMethod]
    public async Task ParseMsixInventoryAsync_EmptyInventory_ReturnsNull()
    {
        // The arch dir exists but the inventory has no valid Name=Value entries → null.
        WriteInventory("x64", "   ", "# comment with no equals");

        var entries = await WindowsAppRuntimeService.ParseMsixInventoryAsync(
            CreateTaskContext(), _tempDir, CancellationToken.None, architecture: "x64");

        Assert.IsNull(entries);
    }
}

