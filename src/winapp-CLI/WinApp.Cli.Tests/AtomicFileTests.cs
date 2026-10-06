// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class AtomicFileTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"AtomicFile_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [TestMethod]
    public async Task WriteAllBytesAsync_WritesContentAndLeavesNoTempFiles()
    {
        var dest = Path.Combine(_tempDir, "out.bin");
        var bytes = Encoding.UTF8.GetBytes("hello atomic");

        await AtomicFile.WriteAllBytesAsync(dest, bytes, CancellationToken.None);

        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(dest));
        Assert.AreEqual(0, Directory.GetFiles(_tempDir, "*.tmp").Length, "No leftover temp files must remain.");
    }

    [TestMethod]
    public void Copy_OverwritesExistingDestinationAtomically()
    {
        var source = Path.Combine(_tempDir, "src.bin");
        var dest = Path.Combine(_tempDir, "dst.bin");
        File.WriteAllText(source, "new");
        File.WriteAllText(dest, "old");

        AtomicFile.Copy(source, dest);

        Assert.AreEqual("new", File.ReadAllText(dest));
        Assert.AreEqual(0, Directory.GetFiles(_tempDir, "*.tmp").Length);
    }

    [TestMethod]
    public async Task WriteStagedAsync_DoesNotPublishUntilPublishCalled()
    {
        var dest = Path.Combine(_tempDir, "staged.bin");
        var bytes = Encoding.UTF8.GetBytes("staged content");

        var staged = await AtomicFile.WriteStagedAsync(dest, bytes, CancellationToken.None);

        Assert.IsTrue(File.Exists(staged), "The staged temp file must exist.");
        Assert.IsFalse(File.Exists(dest), "The destination must not exist before Publish is called.");
        Assert.AreNotEqual(dest, staged, "The staged path must differ from the final destination.");

        AtomicFile.Publish(staged, dest);

        Assert.IsTrue(File.Exists(dest), "After Publish, the destination must exist.");
        Assert.IsFalse(File.Exists(staged), "After Publish, the staged temp file must be gone (moved).");
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(dest));
    }

    [TestMethod]
    public async Task DiscardStaged_RemovesStagedFileAndLeavesDestinationAbsent()
    {
        var dest = Path.Combine(_tempDir, "discard.bin");
        var staged = await AtomicFile.WriteStagedAsync(dest, [1, 2, 3], CancellationToken.None);

        AtomicFile.DiscardStaged(staged);

        Assert.IsFalse(File.Exists(staged), "The staged temp file must be deleted.");
        Assert.IsFalse(File.Exists(dest), "The destination must never have been created.");
    }

    [TestMethod]
    public async Task DiscardStaged_WhenDeleteFails_SwallowsErrorAndDoesNotThrow()
    {
        var dest = Path.Combine(_tempDir, "locked.bin");
        var staged = await AtomicFile.WriteStagedAsync(dest, [1, 2, 3], CancellationToken.None);

        // Hold the staged file open with no sharing so File.Delete raises a sharing violation,
        // exercising the best-effort catch inside TryDeleteLeftoverTemp.
        using (new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            AtomicFile.DiscardStaged(staged); // must not throw
            Assert.IsTrue(File.Exists(staged),
                "The locked file could not be deleted, confirming the swallowed-error path ran.");
        }
    }

    [TestMethod]
    public void WriteAllText_WhileAReaderHoldsTheDestinationOpen_PublishesOnceTheReaderCloses()
    {
        var dest = TestPaths.Under(_tempDir, "state.json");
        File.WriteAllText(dest, "old");

        // The way every state reader opens the file. Windows still refuses a rename over it while
        // it is open, so the writer must wait the reader out rather than fail with access denied.
        // The reader closes in response to the first refusal, on the writer's own thread. Closing it
        // from a timer instead needs a free thread-pool thread inside the retry window, which a
        // loaded parallel test run does not guarantee.
        using var reader = new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var refusals = 0;
        AtomicFile.WriteAllText(dest, "new", onRenameRefused: () =>
        {
            if (++refusals == 1)
            {
                reader.Dispose();
            }
        });

        Assert.IsGreaterThanOrEqualTo(1, refusals, "The open reader must refuse the first rename, or the retry was never exercised.");
        Assert.AreEqual("new", File.ReadAllText(dest));
        Assert.AreEqual(0, Directory.GetFiles(_tempDir, "*.tmp").Length);
    }

    [TestMethod]
    public async Task WriteAllText_ConcurrentWithAPollingReader_NeverFails()
    {
        var dest = TestPaths.Under(_tempDir, "state.json");
        File.WriteAllText(dest, "0");
        using var stop = new CancellationTokenSource();

        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var stream = new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                stream.ReadByte();
            }
        });

        try
        {
            for (var i = 1; i <= 200; i++)
            {
                AtomicFile.WriteAllText(dest, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        finally
        {
            await stop.CancelAsync();
            await reader;
        }

        Assert.AreEqual("200", File.ReadAllText(dest));
    }

    [TestMethod]
    public void WriteAllText_WhenTheDestinationStaysLocked_FailsAfterTheRetryWindow()
    {
        var dest = TestPaths.Under(_tempDir, "held.json");
        File.WriteAllText(dest, "old");

        using var holder = new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

        Assert.ThrowsExactly<UnauthorizedAccessException>(() => AtomicFile.WriteAllText(dest, "new"));
        Assert.AreEqual(0, Directory.GetFiles(_tempDir, "*.tmp").Length, "A failed publish must not leave its temp file.");
    }
}
