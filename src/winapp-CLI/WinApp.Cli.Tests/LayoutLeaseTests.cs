// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public class LayoutLeaseTests
{
    private DirectoryInfo _root = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, $"layout-lease-tests-{Guid.NewGuid():N}"));
    }

    [TestCleanup]
    public void Cleanup() => _root.Delete(recursive: true);

    [TestMethod]
    public void LockPath_DependsOnlyOnTheLayout_NotWorkingDirectory()
    {
        var layout = new DirectoryInfo(Path.Combine(_root.FullName, "output", "AppX"));
        var originalDirectory = Environment.CurrentDirectory;
        var otherDirectory = _root.CreateSubdirectory("other-working-directory");
        var expectedPath = LayoutLease.GetLockPath(layout);

        using var first = LayoutLease.Acquire(layout, TestContext.CancellationToken);
        try
        {
            Environment.CurrentDirectory = otherDirectory.FullName;
            var otherSpelling = new DirectoryInfo(Path.Combine(layout.Parent!.FullName, ".", "APPX") + "\\");
            Assert.AreEqual(expectedPath, LayoutLease.GetLockPath(otherSpelling), ignoreCase: true);
            Assert.ThrowsExactly<TimeoutException>(() =>
                LayoutLease.Acquire(otherSpelling, TestContext.CancellationToken, TimeSpan.Zero));
            Assert.IsEmpty(otherDirectory.GetFileSystemInfos());
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }

        Assert.AreEqual(Path.Combine(layout.Parent!.FullName, LayoutLease.LockDirectoryName),
            Path.GetDirectoryName(expectedPath));
        Assert.IsFalse(layout.Exists, "Acquiring the lease must not materialize the layout.");
    }

    [TestMethod]
    public void LockPath_NormalizesExtendedPrefixAndCase_AndHasFixedLengthName()
    {
        var layout = new DirectoryInfo(Path.Combine(_root.FullName, new string('a', 150), "AppX"));
        var extended = new DirectoryInfo(@"\\?\" + layout.FullName.ToUpperInvariant());
        var path = LayoutLease.GetLockPath(layout);

        Assert.AreEqual(path, LayoutLease.GetLockPath(extended), ignoreCase: true);
        Assert.AreEqual(69, Path.GetFileName(path).Length);
        using (LayoutLease.Acquire(layout, TestContext.CancellationToken))
        {
            Assert.IsTrue(File.Exists(path));
        }
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public async Task ConcurrentInstances_WaitUntilTheFirstIsReleasedAcrossAnAwait()
    {
        var layout = new DirectoryInfo(Path.Combine(_root.FullName, "AppX"));
        using var first = LayoutLease.Acquire(layout, TestContext.CancellationToken);
        using var attempted = new ManualResetEventSlim();
        var acquired = false;
        var waiting = Task.Run(() =>
        {
            using var second = LayoutLease.Acquire(layout, TestContext.CancellationToken,
                TimeSpan.FromSeconds(10), path =>
                {
                    attempted.Set();
                    return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                        FileShare.None, 1, FileOptions.DeleteOnClose);
                });
            acquired = true;
        }, TestContext.CancellationToken);

        try
        {
            Assert.IsTrue(attempted.Wait(TimeSpan.FromSeconds(5), TestContext.CancellationToken));
            Assert.IsFalse(waiting.IsCompleted);
        }
        finally
        {
            await Task.Run(first.Dispose, TestContext.CancellationToken);
            await waiting;
        }

        Assert.IsTrue(acquired);
        Assert.IsFalse(File.Exists(LayoutLease.GetLockPath(layout)));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void StorageFailure_IsReportedImmediatelyWithoutRetry(bool accessDenied)
    {
        var layout = new DirectoryInfo(Path.Combine(_root.FullName, "AppX"));
        Exception cause = accessDenied
            ? new UnauthorizedAccessException("layout storage denied")
            : new IOException("disk is full", unchecked((int)0x80070070));
        var attempts = 0;
        var elapsed = Stopwatch.StartNew();

        void Acquire() =>
            LayoutLease.Acquire(layout, TestContext.CancellationToken, TimeSpan.FromSeconds(10), _ =>
            {
                attempts++;
                throw cause;
            });
        Exception actual = accessDenied
            ? Assert.ThrowsExactly<UnauthorizedAccessException>(Acquire)
            : Assert.ThrowsExactly<IOException>(Acquire);

        Assert.AreSame(cause, actual);
        Assert.AreEqual(1, attempts);
        Assert.IsLessThan(TimeSpan.FromSeconds(2), elapsed.Elapsed);
    }

    [TestMethod]
    public void ReadOnlyLockFile_ReportsAccessDeniedWithoutAContentionTimeout()
    {
        var layout = new DirectoryInfo(Path.Combine(_root.FullName, "AppX"));
        var lockPath = LayoutLease.GetLockPath(layout);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        File.WriteAllText(lockPath, "");
        File.SetAttributes(lockPath, FileAttributes.ReadOnly);
        try
        {
            Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
                LayoutLease.Acquire(layout, TestContext.CancellationToken, TimeSpan.Zero));
        }
        finally
        {
            File.SetAttributes(lockPath, FileAttributes.Normal);
        }
    }

    [TestMethod]
    [DataRow(unchecked((int)0x80070020), true)]
    [DataRow(unchecked((int)0x80070021), true)]
    [DataRow(unchecked((int)0x80070005), false)]
    [DataRow(unchecked((int)0x80070003), false)]
    [DataRow(unchecked((int)0x80070070), false)]
    [DataRow(unchecked((int)0x80131620), false)]
    public void OnlyWindowsSharingAndLockViolationsAreContention(int hresult, bool expected)
    {
        Assert.AreEqual(expected, LayoutLease.IsContention(new IOException("storage error", hresult)));
    }

    [TestMethod]
    public async Task ContentionWait_IsCancellable()
    {
        var layout = new DirectoryInfo(Path.Combine(_root.FullName, "AppX"));
        using var first = LayoutLease.Acquire(layout, TestContext.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
        var elapsed = Stopwatch.StartNew();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => Task.Run(() =>
            LayoutLease.Acquire(layout, cancellation.Token, TimeSpan.FromSeconds(10)), TestContext.CancellationToken));

        Assert.IsLessThan(TimeSpan.FromSeconds(2), elapsed.Elapsed);
    }

    [TestMethod]
    public void AlreadyCancelled_DoesNotCreateLockArtifacts()
    {
        var layout = new DirectoryInfo(Path.Combine(_root.FullName, "AppX"));
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            LayoutLease.Acquire(layout, new CancellationToken(canceled: true)));
        Assert.IsEmpty(_root.GetFileSystemInfos());
    }

    [TestMethod]
    public void ExistingUnlockedFile_IsReusable_AndDisposalRemovesOnlyThatFile()
    {
        var layout = new DirectoryInfo(Path.Combine(_root.FullName, "AppX"));
        var lockPath = LayoutLease.GetLockPath(layout);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        File.WriteAllText(lockPath, "old state");
        var other = new DirectoryInfo(Path.Combine(_root.FullName, "other"));
        using var otherLease = LayoutLease.Acquire(other, TestContext.CancellationToken);

        using (var lease = LayoutLease.Acquire(layout, TestContext.CancellationToken, TimeSpan.Zero))
        {
            lease.Dispose();
            lease.Dispose();
        }

        Assert.IsFalse(File.Exists(lockPath));
        Assert.IsTrue(File.Exists(LayoutLease.GetLockPath(other)));
        using var next = LayoutLease.Acquire(layout, TestContext.CancellationToken, TimeSpan.Zero);
    }

    [TestMethod]
    public async Task OtherProcess_WithDifferentWorkingAndCacheDirectories_BlocksUntilKilled()
    {
        var layout = new DirectoryInfo(Path.Combine(_root.FullName, "AppX"));
        var lockPath = LayoutLease.GetLockPath(layout);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var childWorkingDirectory = _root.CreateSubdirectory("child-working-directory");
        var childCacheDirectory = _root.CreateSubdirectory("child-cache");
        const string script = """
            $ErrorActionPreference = 'Stop'
            $stream = [System.IO.FileStream]::new($env:WINAPP_TEST_LAYOUT_LOCK,
                [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite,
                [System.IO.FileShare]::None, 1, [System.IO.FileOptions]::DeleteOnClose)
            [Console]::Out.WriteLine('locked')
            [Console]::Out.Flush()
            [Console]::In.ReadLine() | Out-Null
            $stream.Dispose()
            """;
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe"),
            WorkingDirectory = childWorkingDirectory.FullName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        start.Environment["WINAPP_TEST_LAYOUT_LOCK"] = lockPath;
        start.Environment["LOCALAPPDATA"] = childCacheDirectory.FullName;
        using var child = Process.Start(start)!;
        try
        {
            using var readyTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
            readyTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            Assert.AreEqual("locked", await child.StandardOutput.ReadLineAsync(readyTimeout.Token));

            Assert.ThrowsExactly<TimeoutException>(() =>
                LayoutLease.Acquire(layout, TestContext.CancellationToken, TimeSpan.Zero));
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }
            await child.WaitForExitAsync(TestContext.CancellationToken);
        }

        using (LayoutLease.Acquire(layout, TestContext.CancellationToken, TimeSpan.FromSeconds(1)))
        {
            Assert.IsTrue(File.Exists(lockPath));
        }
        Assert.IsFalse(File.Exists(lockPath));
    }

    [TestMethod]
    public void ReservedLayoutPath_IsRefusedBeforeCreatingAnything()
    {
        var layout = new DirectoryInfo(Path.Combine(_root.FullName, LayoutLease.LockDirectoryName, "AppX"));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            LayoutLease.Acquire(layout, TestContext.CancellationToken));
        Assert.IsEmpty(_root.GetFileSystemInfos());
    }
}
