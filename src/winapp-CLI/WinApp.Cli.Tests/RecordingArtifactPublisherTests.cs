// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Tests;

[TestClass]
public class RecordingArtifactPublisherTests
{
    private string _root = null!;
    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = TestPaths.TempRoot(nameof(RecordingArtifactPublisherTests));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public async Task ConcurrentNoOverwritePublishers_CannotReplaceTheWinner()
    {
        var output = Path.Join(_root, "take.mp4");
        var sources = new[] { Path.Join(_root, "first.mp4"), Path.Join(_root, "second.mp4") };
        foreach (var source in sources)
        {
            await File.WriteAllTextAsync(source, source, TestContext.CancellationToken);
        }
        var results = await Task.WhenAll(sources.Select(source => Task.Run(() =>
        {
            try
            {
                RecordingArtifactPublisher.Publish(source, output, null, null, overwrite: false);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }, TestContext.CancellationToken)));
        Assert.AreEqual(1, results.Count(success => success));
        var winner = Array.FindIndex(results, success => success);
        Assert.AreEqual(sources[winner], await File.ReadAllTextAsync(output, TestContext.CancellationToken));
        Assert.IsTrue(File.Exists(sources[1 - winner]), "The losing take remains recoverable.");
    }

    [TestMethod]
    public async Task FailedReplacement_RestoresOldFramesAndPreservesBothTakes()
    {
        var output = Path.Join(_root, "take.mp4");
        var source = Path.Join(_root, "new.mp4");
        var oldFrames = Path.Join(_root, "take.frames");
        var newFrames = Path.Join(_root, "new.frames");
        Directory.CreateDirectory(oldFrames);
        Directory.CreateDirectory(newFrames);
        await File.WriteAllTextAsync(output, "old", TestContext.CancellationToken);
        await File.WriteAllTextAsync(source, "new", TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Join(oldFrames, "old.txt"), "old frames", TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Join(newFrames, "new.txt"), "new frames", TestContext.CancellationToken);
        using var locked = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.Throws<IOException>(() =>
            RecordingArtifactPublisher.Publish(source, output, newFrames, oldFrames, overwrite: true));

        Assert.IsTrue(File.Exists(Path.Join(oldFrames, "old.txt")));
        Assert.IsFalse(File.Exists(Path.Join(oldFrames, "new.txt")));
        Assert.IsTrue(File.Exists(Path.Join(newFrames, "new.txt")));
        Assert.AreEqual("new", await File.ReadAllTextAsync(source, TestContext.CancellationToken));
        Assert.AreEqual("old", await File.ReadAllTextAsync(output, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task VideoOnlyReplacement_ArchivesPreviousFramesInsteadOfLeavingAStalePair()
    {
        var output = Path.Join(_root, "take.mp4");
        var source = Path.Join(_root, "new.mp4");
        var frames = Path.Join(_root, "take.frames");
        Directory.CreateDirectory(frames);
        await File.WriteAllTextAsync(Path.Join(frames, "unrelated.txt"), "keep", TestContext.CancellationToken);
        await File.WriteAllTextAsync(output, "old", TestContext.CancellationToken);
        await File.WriteAllTextAsync(source, "new", TestContext.CancellationToken);

        var archived = RecordingArtifactPublisher.Publish(source, output, null, frames, overwrite: true);

        Assert.IsNotNull(archived);
        Assert.IsFalse(Directory.Exists(frames));
        Assert.AreEqual("keep", await File.ReadAllTextAsync(Path.Join(archived, "unrelated.txt"), TestContext.CancellationToken));
        Assert.AreEqual("new", await File.ReadAllTextAsync(output, TestContext.CancellationToken));
    }
}
