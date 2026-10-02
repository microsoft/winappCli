// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

[TestClass]
public class BootstrapPayloadTests
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
    [DataRow(false)]
    [DataRow(true)]
    public async Task ChangedBinaryOrCompanionDoesNotOverwriteLockedPriorPayload(bool changeCompanion)
    {
        var root = Directory.CreateTempSubdirectory("winapp-bootstrap-");
        try
        {
            var source = root.CreateSubdirectory("source");
            var bootstrap = root.CreateSubdirectory("bootstrap");
            var binary = new FileInfo(Path.Combine(source.FullName, "winapp.exe"));
            var companion = Path.Combine(source.FullName, "libSkiaSharp.dll");
            File.WriteAllText(binary.FullName, "first-binary");
            File.WriteAllText(companion, "first-companion");
            var first = await WindowsSandboxBackend.StageBootstrapPayloadAsync(binary, bootstrap.FullName, TestContext.CancellationToken);
            var leaf = changeCompanion ? "libSkiaSharp.dll" : "winapp.exe";
            var oldPath = Path.Combine(bootstrap.FullName, first.DirectoryName, leaf);
            using (new FileStream(oldPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                File.WriteAllText(changeCompanion ? companion : binary.FullName, "new-content");
                var next = await WindowsSandboxBackend.StageBootstrapPayloadAsync(binary, bootstrap.FullName, TestContext.CancellationToken);
                Assert.AreNotEqual(first.DirectoryName, next.DirectoryName);
                Assert.AreEqual(changeCompanion ? "first-companion" : "first-binary", File.ReadAllText(oldPath));
                Assert.AreEqual("new-content", File.ReadAllText(Path.Combine(bootstrap.FullName, next.DirectoryName, leaf)));
                if (changeCompanion)
                {
                    Assert.AreEqual(first.BinaryHash, next.BinaryHash, "The complete bundle, not only EXE hash, selects the immutable directory.");
                }
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
