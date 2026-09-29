// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class BootstrapPayloadTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task IdenticalPayloadReusesTheUnchangedMappedFile()
    {
        var root = Directory.CreateTempSubdirectory("winapp-bootstrap-");
        try
        {
            var source = root.CreateSubdirectory("source");
            var bootstrap = root.CreateSubdirectory("bootstrap");
            var binary = new FileInfo(Path.Combine(source.FullName, "winapp.exe"));
            File.WriteAllText(binary.FullName, "first-binary");
            File.WriteAllText(Path.Combine(source.FullName, "libSkiaSharp.dll"), "skia");
            File.WriteAllText(Path.Combine(source.FullName, "libHarfBuzzSharp.dll"), "harfbuzz");
            var first = await WindowsSandboxBackend.StageBootstrapPayloadAsync(binary, bootstrap.FullName, TestContext.CancellationToken);
            var staged = Path.Combine(bootstrap.FullName, first.DirectoryName, "winapp.exe");
            var stamp = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(staged, stamp);
            using (new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var repeated = await WindowsSandboxBackend.StageBootstrapPayloadAsync(binary, bootstrap.FullName, TestContext.CancellationToken);
                Assert.AreEqual(first, repeated);
                Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(staged));
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    [DataRow("winapp.exe")]
    [DataRow("libSkiaSharp.dll")]
    [DataRow("libHarfBuzzSharp.dll")]
    [DataRow(GuestDevTools.NativeFileName)]
    [DataRow(GuestDevTools.ManagedFileName)]
    public async Task ChangedBundleMemberDoesNotOverwriteLockedPriorPayload(string changed)
    {
        var root = Directory.CreateTempSubdirectory("winapp-bootstrap-");
        try
        {
            var source = root.CreateSubdirectory("source");
            var bootstrap = root.CreateSubdirectory("bootstrap");
            var binary = new FileInfo(Path.Combine(source.FullName, "winapp.exe"));
            string[] members = ["winapp.exe", "libSkiaSharp.dll", "libHarfBuzzSharp.dll", .. GuestDevTools.EngineFileNames];
            foreach (var member in members)
            {
                File.WriteAllText(Path.Combine(source.FullName, member), member + "-original");
            }
            File.WriteAllText(Path.Combine(source.FullName, "unrelated-secret.txt"), "not a bundle member");
            var first = await WindowsSandboxBackend.StageBootstrapPayloadAsync(binary, bootstrap.FullName, TestContext.CancellationToken);
            var oldPath = Path.Combine(bootstrap.FullName, first.DirectoryName, changed);
            using (new FileStream(oldPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                File.WriteAllText(Path.Combine(source.FullName, changed), "new-content");
                var next = await WindowsSandboxBackend.StageBootstrapPayloadAsync(binary, bootstrap.FullName, TestContext.CancellationToken);
                Assert.AreNotEqual(first.DirectoryName, next.DirectoryName);
                Assert.AreEqual(changed + "-original", File.ReadAllText(oldPath));
                foreach (var member in members)
                {
                    Assert.AreEqual(member == changed ? "new-content" : member + "-original",
                        File.ReadAllText(Path.Combine(bootstrap.FullName, next.DirectoryName, member)));
                }
                Assert.AreEqual(members.Length, Directory.GetFiles(Path.Combine(bootstrap.FullName, next.DirectoryName)).Length);
                if (changed != "winapp.exe")
                {
                    Assert.AreEqual(first.BinaryHash, next.BinaryHash, "The entire bundle, not only EXE hash, chooses the directory.");
                }
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    [DataRow(GuestDevTools.NativeFileName)]
    public async Task MissingCompanionDoesNotReuseOneFromAnOlderPayload(string name)
    {
        var root = Directory.CreateTempSubdirectory("winapp-bootstrap-");
        try
        {
            var source = root.CreateSubdirectory("source");
            var bootstrap = root.CreateSubdirectory("bootstrap");
            var binary = new FileInfo(Path.Combine(source.FullName, "winapp.exe"));
            File.WriteAllText(binary.FullName, "binary");
            File.WriteAllText(Path.Combine(source.FullName, "libSkiaSharp.dll"), "skia");
            File.WriteAllText(Path.Combine(source.FullName, "libHarfBuzzSharp.dll"), "harfbuzz");
            var engine = Path.Combine(source.FullName, name);
            File.WriteAllText(engine, "native");
            var first = await WindowsSandboxBackend.StageBootstrapPayloadAsync(binary, bootstrap.FullName, TestContext.CancellationToken);
            File.Delete(engine);
            var next = await WindowsSandboxBackend.StageBootstrapPayloadAsync(binary, bootstrap.FullName, TestContext.CancellationToken);
            Assert.AreNotEqual(first.DirectoryName, next.DirectoryName);
            Assert.IsFalse(File.Exists(Path.Combine(bootstrap.FullName, next.DirectoryName, name)));
            Assert.IsTrue(File.Exists(Path.Combine(bootstrap.FullName, first.DirectoryName, name)));
        }
        finally
        {
            root.Delete(recursive: true);
        }

    }

    [TestMethod]
    [DataRow("libSkiaSharp.dll")]
    [DataRow("libHarfBuzzSharp.dll")]
    public async Task MissingRequiredGraphicsCompanionFailsWithoutReusingOrChangingPriorPayload(string name)
    {
        var root = Directory.CreateTempSubdirectory("winapp-bootstrap-");
        try
        {
            var source = root.CreateSubdirectory("source");
            var bootstrap = root.CreateSubdirectory("bootstrap");
            var binary = new FileInfo(Path.Combine(source.FullName, "winapp.exe"));
            File.WriteAllText(binary.FullName, "binary");
            File.WriteAllText(Path.Combine(source.FullName, "libSkiaSharp.dll"), "skia");
            File.WriteAllText(Path.Combine(source.FullName, "libHarfBuzzSharp.dll"), "harfbuzz");
            var first = await WindowsSandboxBackend.StageBootstrapPayloadAsync(binary, bootstrap.FullName, TestContext.CancellationToken);
            var prior = Path.Combine(bootstrap.FullName, first.DirectoryName, name);
            var bytes = File.ReadAllBytes(prior);
            using var locked = new FileStream(prior, FileMode.Open, FileAccess.Read, FileShare.Read);
            File.Delete(Path.Combine(source.FullName, name));

            var error = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
                WindowsSandboxBackend.StageBootstrapPayloadAsync(binary, bootstrap.FullName, TestContext.CancellationToken));

            Assert.AreEqual(ExecutionTargetErrorCodes.AgentUpgradeFailed, error.Error.Code);
            StringAssert.Contains(error.Message, name);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(prior));
            Assert.HasCount(1, bootstrap.GetDirectories(), "Missing input must not create a partial replacement bundle.");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
