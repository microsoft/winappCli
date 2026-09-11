// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;

using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

[TestClass]
public class TargetConnectionLockTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = TestPaths.TempRoot(nameof(TargetConnectionLockTests));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A failed assertion is more useful than cleanup noise.
        }
    }

    [TestMethod]
    public void Lease_SerializesChannelsAndReleasesOnDispose()
    {
        var connectionLock = new TargetConnectionLock(new FixedProvider(_root));
        var target = WindowsSandboxTarget.Default;

        using var first = connectionLock.TryAcquire(target, TimeSpan.FromSeconds(1));
        Assert.IsNotNull(first);
        Assert.IsNull(connectionLock.TryAcquire(target, TimeSpan.FromMilliseconds(50)));

        first.Dispose();

        using var second = connectionLock.TryAcquire(target, TimeSpan.FromSeconds(1));
        Assert.IsNotNull(second);
    }

    [TestMethod]
    public void LockFile_IsScopedToTheTargetStateRoot()
    {
        var connectionLock = new TargetConnectionLock(new FixedProvider(_root));

        using var lease = connectionLock.TryAcquire(
            WindowsSandboxTarget.Default,
            TimeSpan.FromSeconds(1));

        Assert.IsTrue(File.Exists(Path.Join(_root, TargetConnectionLock.LockFileName)));
    }

    private sealed class FixedProvider(string root) : ITargetStateDirectoryProvider
    {
        public DirectoryInfo GetTargetRoot(ExecutionTargetRef target, bool create = true)
        {
            var directory = new DirectoryInfo(root);
            if (create)
            {
                directory.Create();
            }

            return directory;
        }
    }
}
