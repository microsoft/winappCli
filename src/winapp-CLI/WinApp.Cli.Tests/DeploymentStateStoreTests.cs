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
