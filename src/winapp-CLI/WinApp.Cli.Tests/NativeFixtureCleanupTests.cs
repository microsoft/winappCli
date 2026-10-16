// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;

namespace WinApp.Cli.Tests;

internal static class NativeFixtureCleanup
{
    internal static async Task DeleteAsync(
        DirectoryInfo directory,
        Action<string> diagnostic,
        TimeSpan? retryWindow = null)
    {
        var budget = retryWindow ?? TimeSpan.FromSeconds(5);
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, TimeSpan.Zero);
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                directory.Delete(recursive: true);
                return;
            }
            catch (Exception error) when (
                (error is IOException or UnauthorizedAccessException) &&
                ((error.HResult & 0xffff) is 5 or 32 or 33))
            {
                diagnostic($"Owned fixture cleanup at '{directory.FullName}': " +
                    $"{error.GetType().Name}, HRESULT 0x{error.HResult:X8}, elapsed {elapsed.Elapsed}.");
                if (elapsed.Elapsed >= budget)
                {
                    throw;
                }

                // External file handles can outlive the fixture process. Retry only deletion;
                // assertions are not rerun, and a persistent cleanup failure still fails the test.
                await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
            }
        }
    }
}

[TestClass]
public class NativeFixtureCleanupTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TemporaryFileBlock_IsReleasedBeforeCleanupCanPass(bool readOnly)
    {
        var directory = Directory.CreateTempSubdirectory("winapp-fixture-cleanup-");
        var path = Path.Combine(directory.FullName, "owned.bin");
        FileStream? reader = null;
        try
        {
            File.WriteAllText(path, "owned fixture");
            if (readOnly)
            {
                File.SetAttributes(path, FileAttributes.ReadOnly);
            }
            else
            {
                reader = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            var messages = new List<string>();
            var cleanup = NativeFixtureCleanup.DeleteAsync(directory, messages.Add);
            var wasPending = !cleanup.IsCompleted;
            reader?.Dispose();
            reader = null;
            if (readOnly)
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            await cleanup;

            Assert.IsTrue(wasPending, "The first deletion must encounter the controlled file block.");
            Assert.IsNotEmpty(messages, "The transient failure must be reported, not silently ignored.");
            Assert.IsFalse(Directory.Exists(directory.FullName), "Success requires actual removal.");
        }
        finally
        {
            reader?.Dispose();
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
            if (Directory.Exists(directory.FullName))
            {
                await NativeFixtureCleanup.DeleteAsync(directory, TestContext.WriteLine);
            }
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnreleasedFileBlock_ThrowsAtTheCleanupBound(bool readOnly)
    {
        var directory = Directory.CreateTempSubdirectory("winapp-fixture-cleanup-");
        var path = Path.Combine(directory.FullName, "owned.bin");
        FileStream? reader = null;
        try
        {
            File.WriteAllText(path, "owned fixture");
            if (readOnly)
            {
                File.SetAttributes(path, FileAttributes.ReadOnly);
            }
            else
            {
                reader = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            var messages = new List<string>();
            var cleanup = NativeFixtureCleanup.DeleteAsync(directory, messages.Add, TimeSpan.Zero);
            if (readOnly)
            {
                await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => cleanup);
            }
            else
            {
                await Assert.ThrowsExactlyAsync<IOException>(() => cleanup);
            }
            Assert.HasCount(1, messages);
            Assert.IsTrue(File.Exists(path), "An exhausted cleanup budget must not claim removal.");
        }
        finally
        {
            reader?.Dispose();
            File.SetAttributes(path, FileAttributes.Normal);
            await NativeFixtureCleanup.DeleteAsync(directory, TestContext.WriteLine);
        }
    }

    public TestContext TestContext { get; set; } = null!;
}
