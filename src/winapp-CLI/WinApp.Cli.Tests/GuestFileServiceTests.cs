// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// A locked file must fail a delete or clean with a specific, actionable error — never the raw OS
/// sharing-violation text a caller cannot act on.
/// </summary>
[TestClass]
public class GuestFileServiceTests
{
    private string _root = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Setup() => _root = TestPaths.TempRoot(nameof(GuestFileServiceTests));

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    [TestMethod]
    public async Task Delete_ALockedFile_FailsWithASpecificActionableMessageNamingThePath()
    {
        var service = new GuestFileService(_root);
        var scope = new GuestPathScope(GuestRootNames.Deployment, "dep-1");

        var directory = service.ResolveScopeDirectory(scope, create: true);
        var filePath = Path.Join(directory, "app.dll");
        await File.WriteAllTextAsync(filePath, "binary", TestContext.CancellationToken);

        // Shared for read only: no FileShare.Delete, so the delete below hits a real sharing
        // violation, exactly what a still-running app leaves behind.
        await using (new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = Assert.ThrowsExactly<ExecutionTargetException>(
                () => service.Delete(scope, ["app.dll"]));

            Assert.AreEqual(ExecutionTargetErrorCodes.TransferInterrupted, failure.Error.Code);
            StringAssert.Contains(failure.Error.Message, "app.dll");
            StringAssert.Contains(failure.Error.Message, "running process");

            // The raw OS text ("being used by another process", the HRESULT, etc.) must never be
            // the message a caller has to parse to understand what happened.
            Assert.IsFalse(failure.Error.Message.Contains("0x8007", StringComparison.Ordinal));
            Assert.IsNotNull(failure.Error.UserAction);
        }
    }

    [TestMethod]
    public async Task RemoveScope_WithALockedFileInside_FailsWithASpecificActionableMessage()
    {
        var service = new GuestFileService(_root);
        var scope = new GuestPathScope(GuestRootNames.Deployment, "dep-1-layout");

        var directory = service.ResolveScopeDirectory(scope, create: true);
        var filePath = Path.Join(directory, "appxmanifest.xml");
        await File.WriteAllTextAsync(filePath, "<Package/>", TestContext.CancellationToken);

        await using (new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = Assert.ThrowsExactly<ExecutionTargetException>(() => service.RemoveScope(scope));

            Assert.AreEqual(ExecutionTargetErrorCodes.TransferInterrupted, failure.Error.Code);
            StringAssert.Contains(failure.Error.Message, "running process");
            Assert.IsNotNull(failure.Error.UserAction);
        }

        // Nothing was left half-deleted: the manifest survives the failed attempt.
        Assert.IsTrue(File.Exists(filePath));
    }

    [TestMethod]
    public void Delete_ANonexistentFile_IsANoOp()
    {
        var service = new GuestFileService(_root);
        var scope = new GuestPathScope(GuestRootNames.Deployment, "dep-1");
        service.ResolveScopeDirectory(scope, create: true);

        // Must not throw: absence is not a failure.
        service.Delete(scope, ["does-not-exist.txt"]);
    }
}
