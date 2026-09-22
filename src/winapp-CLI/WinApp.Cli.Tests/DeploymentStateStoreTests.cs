// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

[TestClass]
public class DeploymentStateStoreTests
{
    private readonly string _root = TestPaths.TempRoot("DeploymentState");
    private static readonly ExecutionTargetRef Target = WindowsSandboxTarget.Default;

    public TestContext TestContext { get; set; } = null!;

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private DeploymentStateStore CreateStore() => new(new TargetStateDirectoryProvider(_root));

    private DeploymentState Seed() => CreateStore().Commit(Target, new DeploymentState
    {
        SchemaVersion = DeploymentStateStore.CurrentSchemaVersion,
        Revision = 0,
        DeploymentId = "same-app",
        TargetEpoch = "same-generation",
        Dirty = false,
    }, expectedRevision: 0);

    [TestMethod]
    public async Task Commit_ConcurrentWritersCannotPublishTheSameRevision()
    {
        var original = Seed();
        using var start = new ManualResetEventSlim();
        var attempts = Enumerable.Range(1, 16).Select(processId => Task.Run(() =>
        {
            start.Wait();
            try
            {
                CreateStore().Commit(Target,
                    original with { TrackedOperationProcessId = processId }, original.Revision);
                return processId;
            }
            catch (ExecutionTargetException ex) when (ex.Error.Code == ExecutionTargetErrorCodes.TargetAmbiguous)
            {
                return 0;
            }
        })).ToArray();

        start.Set();
        var successful = (await Task.WhenAll(attempts)).Where(processId => processId != 0).ToArray();
        Assert.HasCount(1, successful, "Only one writer may advance the revision it read.");
        var current = CreateStore().Read(Target, original.DeploymentId)!;
        Assert.AreEqual(original.Revision + 1, current.Revision);
        Assert.AreEqual(successful[0], current.TrackedOperationProcessId);
    }

    [TestMethod]
    public void Commit_WithAnOpenReader_PublishesWithoutInvalidatingTheReader()
    {
        var original = Seed();
        var stateFile = Path.Join(_root, Target.StateKey, DeploymentStateStore.DeploymentsFolder, "same-app.json");
        using var stream = new FileStream(stateFile, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var originalJson = reader.ReadToEnd();
        stream.Position = 0;
        reader.DiscardBufferedData();

        var committed = CreateStore().Commit(Target, original with { Dirty = true }, original.Revision);

        Assert.AreEqual(original.Revision + 1, committed.Revision);
        Assert.IsTrue(CreateStore().Read(Target, original.DeploymentId)!.Dirty);
        Assert.AreEqual(originalJson, reader.ReadToEnd(), "The existing reader must retain the previous committed snapshot.");
    }

    [TestMethod]
    public async Task Read_PersistentSharingFailure_RemainsAnError()
    {
        var original = Seed();
        var stateFile = Path.Join(_root, Target.StateKey, DeploymentStateStore.DeploymentsFolder, "same-app.json");
        using var publisher = new FileStream(stateFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var error = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            Task.Run(() => CreateStore().Read(Target, original.DeploymentId), TestContext.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.DeploymentDirty, error.Error.Code);
        Assert.IsInstanceOfType<IOException>(error.InnerException);
        Assert.AreEqual(32, error.InnerException.HResult & 0xffff);
    }

    [TestMethod]
    public void Read_CorruptState_IsNotRetried()
    {
        var original = Seed();
        var stateFile = Path.Join(_root, Target.StateKey, DeploymentStateStore.DeploymentsFolder, "same-app.json");
        File.WriteAllText(stateFile, "{");
        var lookups = 0;
        var store = new DeploymentStateStore(new ObservingDirectoryProvider(_root, () => lookups++));

        var error = Assert.ThrowsExactly<ExecutionTargetException>(() => store.Read(Target, original.DeploymentId));

        Assert.AreEqual(ExecutionTargetErrorCodes.DeploymentDirty, error.Error.Code);
        Assert.IsInstanceOfType<System.Text.Json.JsonException>(error.InnerException);
        Assert.AreEqual(1, lookups);
    }

    [TestMethod]
    public async Task Read_ConcurrentPublications_ReturnsCompleteCommittedRecords()
    {
        var original = Seed();
        using var start = new ManualResetEventSlim();
        var writer = Task.Run(() =>
        {
            start.Wait(TestContext.CancellationToken);
            var current = original;
            for (var i = 0; i < 100; i++)
            {
                current = CreateStore().Commit(Target, current with { TrackedOperationProcessId = i }, current.Revision);
            }
        }, TestContext.CancellationToken);
        var reader = Task.Run(() =>
        {
            start.Wait(TestContext.CancellationToken);
            for (var i = 0; i < 100; i++)
            {
                var current = CreateStore().Read(Target, original.DeploymentId);
                Assert.IsNotNull(current, "Publishing a new revision must not look like a missing deployment.");
                Assert.AreEqual(original.DeploymentId, current.DeploymentId);
                Assert.AreEqual(original.TargetEpoch, current.TargetEpoch);
            }
        }, TestContext.CancellationToken);

        start.Set();
        await Task.WhenAll(writer, reader).WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);
        Assert.AreEqual(original.Revision + 100, CreateStore().Read(Target, original.DeploymentId)!.Revision);
    }

    private sealed class ObservingDirectoryProvider(string root, Action onLookup) : ITargetStateDirectoryProvider
    {
        private readonly TargetStateDirectoryProvider _inner = new(root);

        public DirectoryInfo GetTargetRoot(ExecutionTargetRef target, bool create = true)
        {
            onLookup();
            return _inner.GetTargetRoot(target, create);
        }
    }

    [TestMethod]
    public async Task Commit_DoesNotCheckOrReplaceStateWhileAnotherWriterHoldsItsLease()
    {
        var original = Seed();
        var stateFile = Path.Join(_root, Target.StateKey, DeploymentStateStore.DeploymentsFolder, "same-app.json");
        using var lease = new FileStream(stateFile + ".lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = Task.Run(() =>
        {
            started.SetResult();
            return CreateStore().Commit(Target, original, original.Revision);
        });

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100);
            Assert.IsFalse(commit.IsCompleted, "The revision check and replacement must share one lease.");
        }
        finally
        {
            lease.Dispose();
            await commit.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
