// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class GuestSourceSnapshotTests
{
    public TestContext TestContext { get; set; } = null!;
    private static readonly string[] MainOnly = ["Main.xaml"];
    private static readonly string[] BothSources = ["Main.xaml", "App.xaml"];
    private string _root = null!;
    private FileInfo _project = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = TestPaths.TempRoot("source-snapshot");
        Directory.CreateDirectory(_root);
        _project = new FileInfo(Path.Combine(_root, "App.csproj"));
        File.WriteAllText(_project.FullName, "<Project/>");
        File.WriteAllText(Path.Combine(_root, "Main.xaml"), "<Page>Exact source</Page>");
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public async Task Snapshot_ContainsOnlyEvaluatedXaml_NotStoresSecretsOrOtherSources()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".winapp"));
        File.WriteAllText(Path.Combine(_root, ".winapp", "ui-comments.json"), "host notes");
        File.WriteAllText(Path.Combine(_root, "secrets.json"), "not copied");
        File.WriteAllText(Path.Combine(_root, "Unrelated.xaml"), "<Page/>");
        File.WriteAllText(Path.Combine(_root, "App.cs"), "not copied");
        var destination = new DirectoryInfo(Path.Combine(_root, "snapshot"));
        var manifest = await GuestSourceSnapshot.CreateAsync(_project, ["Main.xaml"], destination, TestContext.CancellationToken);
        CollectionAssert.AreEqual(MainOnly, Directory.GetFiles(destination.FullName).Select(Path.GetFileName).ToArray());
        Assert.HasCount(1, manifest.Files);
        Assert.AreEqual(File.ReadAllText(Path.Combine(_root, "Main.xaml")), File.ReadAllText(Path.Combine(destination.FullName, "Main.xaml")));
        Assert.AreEqual(64, manifest.Files[0].Sha256.Length);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(3)]
    public async Task Snapshot_PreservesExactOriginalTimestampAndContent(int seconds)
    {
        var original = Path.Combine(_root, "Main.xaml");
        var timestamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddSeconds(seconds).AddTicks(1234567);
        File.SetLastWriteTimeUtc(original, timestamp);
        var destination = new DirectoryInfo(Path.Combine(_root, "snapshot"));
        var manifest = await GuestSourceSnapshot.CreateAsync(_project, MainOnly, destination, TestContext.CancellationToken);
        var copy = Path.Combine(destination.FullName, "Main.xaml");
        Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(copy));
        Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(original));
        Assert.AreEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(original))),
            manifest.Files.Single().Sha256);
        CollectionAssert.AreEqual(File.ReadAllBytes(original), File.ReadAllBytes(copy));
    }

    [TestMethod]
    public async Task LocalLaunch_HashesInPlaceWithoutCopying()
    {
        var before = Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories);
        var copied = await GuestSourceSnapshot.CreateAsync(_project, MainOnly, new(Path.Combine(_root, "snapshot")), TestContext.CancellationToken);
        Directory.Delete(Path.Combine(_root, "snapshot"), recursive: true);
        var local = await GuestSourceSnapshot.CreateAsync(_project, MainOnly, null, TestContext.CancellationToken);
        CollectionAssert.AreEqual(before, Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories));
        Assert.AreEqual(copied.Files.Single(), local.Files.Single());
    }

    [TestMethod]
    public async Task Snapshot_RefusesSourceWithAnActiveWriter()
    {
        using var writer = new FileStream(Path.Combine(_root, "Main.xaml"), FileMode.Open,
            FileAccess.ReadWrite, FileShare.ReadWrite);
        await Assert.ThrowsAsync<IOException>(() => GuestSourceSnapshot.CreateAsync(_project, MainOnly,
            new(Path.Combine(_root, "snapshot")), TestContext.CancellationToken));
    }

    [TestMethod]
    [DataRow(@"..\foreign.xaml")]
    [DataRow(@".winapp\secret.xaml")]
    [DataRow(@"bin\Main.xaml")]
    [DataRow("secrets.json")]
    [DataRow(@"Main.xaml:secret")]
    public async Task Snapshot_RefusesUnprovedOrForbiddenItemsBeforeCreatingDestination(string item)
    {
        var destination = new DirectoryInfo(Path.Combine(_root, "snapshot"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GuestSourceSnapshot.CreateAsync(_project, [item], destination, TestContext.CancellationToken));
        Assert.IsFalse(Directory.Exists(destination.FullName));
    }

    [TestMethod]
    public async Task Snapshot_EnforcesFileSizeInsteadOfCopyingAnUnboundedInput()
    {
        using (var file = File.Create(Path.Combine(_root, "Main.xaml")))
        {
            file.SetLength(GuestSourceSnapshot.MaximumFileBytes + 1);
        }
        await Assert.ThrowsAsync<IOException>(() => GuestSourceSnapshot.CreateAsync(_project, ["Main.xaml"],
            new DirectoryInfo(Path.Combine(_root, "snapshot")), TestContext.CancellationToken));
    }

    [TestMethod]
    [DataRow(@"..\foreign.xaml")]
    [DataRow(@".winapp\secret.xaml")]
    [DataRow(@"bin\Main.xaml")]
    [DataRow("secrets.json")]
    public async Task GuestInventory_RefusesForbiddenSourceMappingsBeforeOpeningFiles(string name)
    {
        var inventory = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new GuestSourceInventory([new(name, 7, new string('A', 64))]),
            GuestCommentsJsonContext.Default.GuestSourceInventory);
        var path = Path.Combine(_root, GuestSourceSnapshot.InventoryFileName);
        File.WriteAllBytes(path, inventory);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(inventory));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GuestSourceSnapshot.OpenReadOnlyAsync(new(_root), hash, TestContext.CancellationToken));
        File.WriteAllText(path, "failed admission released the inventory handle");
    }

    [TestMethod]
    public void Evaluation_RequestsSourceItemsOnlyForGuestDevToolsAndReadsActualItems()
    {
        var options = new ProjectRunOptions("Release", "arm64", "net10.0-windows", true, true, []);
        Assert.IsFalse(ProjectRunService.BuildEvaluateArguments(_project, options).Contains("--getItem:"));
        StringAssert.Contains(ProjectRunService.BuildEvaluateArguments(_project, options with { CaptureDevToolsSources = true }),
            "--getItem:Page,ApplicationDefinition");
        var files = ProjectRunService.ReadDevToolsSources("""
            {"Properties":{"TargetDir":"ignored"},"Items":{"Page":[{"Identity":"Main.xaml"},{"Identity":"Main.xaml"}],"ApplicationDefinition":[{"Identity":"App.xaml"}]}}
            """);
        CollectionAssert.AreEqual(BothSources, files.ToArray());
        Assert.ThrowsExactly<ProjectRunException>(() => ProjectRunService.ReadDevToolsSources("""{"Properties":{}}"""));
    }
}
