// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;

using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

/// <summary>
/// End-to-end deployment reconciliation: a host snapshot driven through the real command channel
/// into a real guest file service, with only the transport faked.
/// </summary>
/// <remarks>
/// This is acceptance criterion 13 for deployment: nothing here invokes Windows Sandbox, yet the
/// whole sequence runs — snapshot, plan, dirty marking, streamed transfer with hash verification,
/// exact deletion, post-verification, and the clean commit. If any of that logic reached for a
/// <c>wsb</c> command or a Sandbox path, these tests could not run at all.
/// </remarks>
[TestClass]
public class TargetDeploymentServiceTests
{
    private static readonly ExecutionTargetRef Target = WindowsSandboxTarget.Default;
    private static readonly ExecutionTargetEpoch Epoch = ExecutionTargetEpoch.Create("sandbox-1", "nonce-a");
    private static readonly string[] RemovedStaleDll = ["stale.dll"];

    private string _root = null!;
    private string _hostSource = null!;
    private string _guestManaged = null!;
    private string _stateRoot = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = TestPaths.TempRoot(nameof(TargetDeploymentServiceTests));
        _hostSource = TestPaths.Under(_root, "host");
        _guestManaged = TestPaths.Under(_root, "guest");
        _stateRoot = TestPaths.Under(_root, "state");

        Directory.CreateDirectory(_hostSource);
        Directory.CreateDirectory(_guestManaged);
        Directory.CreateDirectory(_stateRoot);
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
            // A leftover temp directory is not worth failing a test over.
        }
    }

    [TestMethod]
    public async Task Reconcile_FirstDeployment_TransfersEverythingAndCommitsClean()
    {
        await WriteHostFileAsync("app.exe", "binary-v1");
        await WriteHostFileAsync(TestPaths.Relative("assets", "logo.png"), "image-v1");

        await using var harness = new Harness(_guestManaged, _stateRoot);
        var result = await harness.ReconcileAsync(_hostSource, clean: false, TestContext.CancellationToken);

        Assert.AreEqual(2, result.Plan.Added.Count);
        Assert.AreEqual(0, result.Plan.Removed.Count);
        Assert.IsFalse(result.State.Dirty);

        Assert.AreEqual("binary-v1", await ReadGuestFileAsync(result.DeploymentId, "app.exe"));
        Assert.AreEqual("image-v1", await ReadGuestFileAsync(result.DeploymentId, TestPaths.Relative("assets", "logo.png")));
    }

    [TestMethod]
    public async Task Reconcile_WarmRerun_TransfersOnlyWhatChangedAndDeletesTheRest()
    {
        await WriteHostFileAsync("app.exe", "binary-v1");
        await WriteHostFileAsync("stale.dll", "removed-later");
        await WriteHostFileAsync("unchanged.txt", "same");

        await using var harness = new Harness(_guestManaged, _stateRoot);
        var first = await harness.ReconcileAsync(_hostSource, clean: false, TestContext.CancellationToken);
        Assert.AreEqual(3, first.Plan.Added.Count);

        await WriteHostFileAsync("app.exe", "binary-v2");
        File.Delete(TestPaths.Under(_hostSource, "stale.dll"));

        var second = await harness.ReconcileAsync(_hostSource, clean: false, TestContext.CancellationToken);

        Assert.AreEqual(0, second.Plan.Added.Count);
        Assert.AreEqual(1, second.Plan.Changed.Count);
        CollectionAssert.AreEqual(RemovedStaleDll, second.Plan.Removed.ToArray());

        Assert.AreEqual("binary-v2", await ReadGuestFileAsync(second.DeploymentId, "app.exe"));

        // Leaving a removed binary behind is how a rerun silently keeps executing code the
        // developer just deleted.
        Assert.IsFalse(File.Exists(GuestPath(second.DeploymentId, "stale.dll")));
        Assert.AreEqual("same", await ReadGuestFileAsync(second.DeploymentId, "unchanged.txt"));
    }

    [TestMethod]
    public async Task Reconcile_ContentChangeThatPreservesSizeAndTimestamp_IsStillDetected()
    {
        await WriteHostFileAsync("app.exe", "aaaa");
        var path = TestPaths.Under(_hostSource, "app.exe");
        var timestamp = File.GetLastWriteTimeUtc(path);

        await using var harness = new Harness(_guestManaged, _stateRoot);
        await harness.ReconcileAsync(_hostSource, clean: false, TestContext.CancellationToken);

        // Same length, same timestamp, different content — which build tools produce more often
        // than one would like, and which a size/timestamp comparison would miss entirely.
        await File.WriteAllTextAsync(path, "bbbb", TestContext.CancellationToken);
        File.SetLastWriteTimeUtc(path, timestamp);

        var second = await harness.ReconcileAsync(_hostSource, clean: false, TestContext.CancellationToken);

        Assert.AreEqual(1, second.Plan.Changed.Count);
        Assert.AreEqual("bbbb", await ReadGuestFileAsync(second.DeploymentId, "app.exe"));
    }

    [TestMethod]
    public async Task Reconcile_Clean_DiscardsTheGuestCopyFirst()
    {
        await WriteHostFileAsync("app.exe", "binary-v1");
        await WriteHostFileAsync("extra.txt", "extra");

        await using var harness = new Harness(_guestManaged, _stateRoot);
        var first = await harness.ReconcileAsync(_hostSource, clean: false, TestContext.CancellationToken);

        File.Delete(TestPaths.Under(_hostSource, "extra.txt"));

        var second = await harness.ReconcileAsync(_hostSource, clean: true, TestContext.CancellationToken);

        // A clean reinstall starts from nothing, so everything is added rather than diffed.
        Assert.AreEqual(1, second.Plan.Added.Count);
        Assert.AreEqual(0, second.Plan.Removed.Count);
        Assert.IsFalse(File.Exists(GuestPath(first.DeploymentId, "extra.txt")));
    }

    [TestMethod]
    public async Task Reconcile_CorruptedTransfer_FailsWithoutPublishingTheFile()
    {
        await WriteHostFileAsync("app.exe", "binary-v1");

        await using var harness = new Harness(_guestManaged, _stateRoot);
        var snapshot = await Harness.SnapshotAsync(_hostSource, TestContext.CancellationToken);
        var file = snapshot.Files[0];

        await using var content = new MemoryStream("tampered"u8.ToArray());

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => harness.Channel.PutFileAsync(
                new GuestPathScope(GuestRootNames.Deployment, snapshot.DeploymentId),
                new GuestFileInfo(file.RelativePath, file.Size, file.LastWriteUtc.UtcTicks, file.Sha256),
                content,
                TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.TransferInterrupted, failure.Error.Code);

        // Nothing is published on a failed transfer, and no partial file survives to be mistaken
        // for a legitimate one by the next hash comparison.
        Assert.IsFalse(File.Exists(GuestPath(snapshot.DeploymentId, "app.exe")));
        Assert.IsFalse(
            Directory.EnumerateFiles(_guestManaged, "*.part", SearchOption.AllDirectories).Any(),
            "A partial transfer must not be left behind.");
    }

    [TestMethod]
    public async Task Reconcile_PathEscape_IsRefused()
    {
        await using var harness = new Harness(_guestManaged, _stateRoot);
        await using var content = new MemoryStream("payload"u8.ToArray());

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => harness.Channel.PutFileAsync(
                new GuestPathScope(GuestRootNames.Deployment, "dep-1"),
                new GuestFileInfo(@"..\..\escaped.txt", 7, DateTime.UtcNow.Ticks, new string('0', 64)),
                content,
                TestContext.CancellationToken));

        // Rejecting rather than normalising is deliberate: silently rewriting an escape attempt
        // hides it.
        Assert.AreEqual(ExecutionTargetErrorCodes.DeploymentDirty, failure.Error.Code);
        Assert.IsFalse(File.Exists(TestPaths.Under(_root, "escaped.txt")));
    }

    [TestMethod]
    public async Task Reconcile_UnknownManagedRoot_IsRefused()
    {
        await using var harness = new Harness(_guestManaged, _stateRoot);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => harness.Channel.ListFilesAsync(
                new GuestPathScope("anything-i-like", "dep-1"),
                TestContext.CancellationToken));

        // The root name is a closed set. Treating it as a folder name would let the host address
        // any directory under the managed root.
        Assert.AreEqual(ExecutionTargetErrorCodes.TargetAmbiguous, failure.Error.Code);
    }

    [TestMethod]
    public async Task EnsureLaunchable_RefusesDirtyStaleAndMissingDeployments()
    {
        var clean = new DeploymentState
        {
            SchemaVersion = DeploymentStateStore.CurrentSchemaVersion,
            Revision = 1,
            DeploymentId = "dep-1",
            TargetEpoch = Epoch.Value,
            Dirty = false,
        };

        TargetDeploymentService.EnsureLaunchable(clean, Epoch);

        var missing = Assert.ThrowsExactly<ExecutionTargetException>(
            () => TargetDeploymentService.EnsureLaunchable(null, Epoch));
        Assert.AreEqual(ExecutionTargetErrorCodes.DeploymentDirty, missing.Error.Code);

        // A partially applied layout would run a mixture of two builds.
        var dirty = Assert.ThrowsExactly<ExecutionTargetException>(
            () => TargetDeploymentService.EnsureLaunchable(clean with { Dirty = true }, Epoch));
        Assert.AreEqual(ExecutionTargetErrorCodes.DeploymentDirty, dirty.Error.Code);

        // State from a previous generation describes a guest that no longer exists.
        var stale = Assert.ThrowsExactly<ExecutionTargetException>(
            () => TargetDeploymentService.EnsureLaunchable(clean, ExecutionTargetEpoch.Create("sandbox-1", "nonce-b")));
        Assert.AreEqual(ExecutionTargetErrorCodes.TargetStale, stale.Error.Code);

        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Reconcile_PreviousEpoch_DropsPackageAndProcessRecords()
    {
        await WriteHostFileAsync("app.exe", "binary-v1");

        await using var harness = new Harness(_guestManaged, _stateRoot);
        var first = await harness.ReconcileAsync(_hostSource, clean: false, TestContext.CancellationToken);

        harness.Deployments.CommitPackage(
            Target,
            first.State,
            new PackageOwnership
            {
                PackageName = "Contoso.MyApp",
                Publisher = "CN=Contoso",
                PackageFullName = "Contoso.MyApp_1.0.0.0_arm64__abc",
                PackageFamilyName = "Contoso.MyApp_abc",
                RegisteredLocation = @"C:\WinApp\deployments\dep-1",
            });

        // A new generation is a different guest. Carrying the package record forward would let an
        // unregister act on a package that no longer exists — or worse, one someone else installed.
        await using var recreated = new Harness(
            _guestManaged,
            _stateRoot,
            ExecutionTargetEpoch.Create("sandbox-1", "nonce-b"));

        var second = await recreated.ReconcileAsync(_hostSource, clean: false, TestContext.CancellationToken);

        Assert.IsNull(second.State.Package);
        Assert.IsNull(second.State.TrackedOperationProcessId);
    }

    [TestMethod]
    public async Task ReconcileAndUnregister_LiteralLegacyPackageRetainsPackagedHistory()
    {
        await WriteHostFileAsync("app.exe", "binary-v1");
        var snapshot = await Harness.SnapshotAsync(_hostSource, TestContext.CancellationToken);
        var stateDirectory = TestPaths.Under(
            _stateRoot,
            Target.StateKey,
            DeploymentStateStore.DeploymentsFolder);
        Directory.CreateDirectory(stateDirectory);
        await File.WriteAllTextAsync(
            TestPaths.Under(stateDirectory, $"{snapshot.DeploymentId}.json"),
            $$"""
            {
              "schemaVersion": 1,
              "revision": 4,
              "deploymentId": "{{snapshot.DeploymentId}}",
              "targetEpoch": "{{Epoch.Value}}",
              "dirty": false,
              "desired": [],
              "package": {
                "packageName": "Contoso.MyApp",
                "publisher": "CN=Contoso",
                "packageFullName": "Contoso.MyApp_1.0.0.0_arm64__abc",
                "packageFamilyName": "Contoso.MyApp_abc",
                "registeredLocation": "C:\\WinApp\\deployments\\legacy"
              }
            }
            """,
            TestContext.CancellationToken);

        await using var harness = new Harness(_guestManaged, _stateRoot);
        var reconciled = await harness.ReconcileAsync(
            _hostSource, clean: false, TestContext.CancellationToken);
        var unregistered = harness.Deployments.ClearRegistration(Target, reconciled.State);

        Assert.IsTrue(reconciled.State.WasPackaged, "A legacy package claim establishes packaged history.");
        Assert.IsNull(unregistered.Package);
        Assert.IsTrue(unregistered.WasPackaged, "Unregister preserves packaged history after clearing ownership.");
    }

    [TestMethod]
    public void PackageOwnership_MatchesOnlyTheExactManagedPackage()
    {
        var owned = new PackageOwnership
        {
            PackageName = "Contoso.MyApp",
            Publisher = "CN=Contoso",
            PackageFullName = "Contoso.MyApp_1.0.0.0_arm64__abc",
            PackageFamilyName = "Contoso.MyApp_abc",
            RegisteredLocation = @"C:\WinApp\deployments\dep-1",
        };

        Assert.IsTrue(owned.Owns("Contoso.MyApp_1.0.0.0_arm64__abc", @"C:\WinApp\deployments\dep-1\"));

        // A different registration of the same package, or a different package registered from a
        // path this deployment happens to have used, is not ours to remove.
        Assert.IsFalse(owned.Owns("Contoso.MyApp_1.0.0.0_arm64__abc", @"C:\Users\someone\MyApp"));
        Assert.IsFalse(owned.Owns("Contoso.MyApp_2.0.0.0_arm64__abc", @"C:\WinApp\deployments\dep-1"));
    }

    private async Task WriteHostFileAsync(string relativePath, string contents)
    {
        var path = TestPaths.Under(_hostSource, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents, TestContext.CancellationToken);
    }

    private string GuestPath(string deploymentId, string relativePath) =>
        TestPaths.Under(_guestManaged, "deployments", deploymentId, relativePath);

    private Task<string> ReadGuestFileAsync(string deploymentId, string relativePath) =>
        File.ReadAllTextAsync(GuestPath(deploymentId, relativePath), TestContext.CancellationToken);

    /// <summary>Host channel and guest server sharing one in-memory transport and a real file service.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(60));
        private readonly Task _serverTask;
        private readonly ExecutionTargetEpoch _epoch;

        public Harness(string guestManagedRoot, string stateRoot, ExecutionTargetEpoch? epoch = null)
        {
            _epoch = epoch ?? Epoch;

            var pair = new LoopbackTransportPair();

            var server = new GuestCommandServer(
                pair.Guest,
                _epoch,
                new FakeGuestProcessHostFactory(),
                new StaticGuestSessionProbe(new GuestSessionInfo(1, "WinSta0", true)),
                new GuestAgentIdentity("1.0.0", "hash", "arm64", 1, 1),
                new GuestFileService(guestManagedRoot));

            _serverTask = server.RunAsync(_cancellation.Token);

            Channel = new GuestCommandChannel(pair.Host, _epoch);
            Channel.Start();

            Deployments = new TargetDeploymentService(
                new DeploymentStateStore(new FixedTargetStateDirectoryProvider(stateRoot)));
        }

        public GuestCommandChannel Channel { get; }

        public TargetDeploymentService Deployments { get; }

        public static async Task<DeploymentSnapshot> SnapshotAsync(string sourceRoot, CancellationToken cancellationToken)
        {
            var deploymentId = DeploymentPlanner.CreateDeploymentId(sourceRoot, originalPackageIdentity: null);
            return await DeploymentPlanner.CreateSnapshotAsync(
                new DirectoryInfo(sourceRoot), deploymentId, cancellationToken);
        }

        public async Task<DeploymentResult> ReconcileAsync(
            string sourceRoot,
            bool clean,
            CancellationToken cancellationToken)
        {
            var snapshot = await SnapshotAsync(sourceRoot, cancellationToken);

            return await Deployments.ReconcileAsync(
                Target,
                _epoch,
                Channel,
                snapshot.DeploymentId,
                snapshot,
                new DirectoryInfo(sourceRoot),
                clean,
                cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync();

            try
            {
                await _serverTask;
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }

            await Channel.DisposeAsync();
            _cancellation.Dispose();
        }
    }

    /// <summary>A state directory provider rooted at a test-owned folder.</summary>
    /// <remarks>
    /// The spec pins the real root under <c>%LOCALAPPDATA%</c>; injecting it here keeps tests from
    /// ever touching a developer's actual Sandbox state.
    /// </remarks>
    private sealed class FixedTargetStateDirectoryProvider(string root) : ITargetStateDirectoryProvider
    {
        public DirectoryInfo GetTargetRoot(ExecutionTargetRef target, bool create)
        {
            var directory = new DirectoryInfo(TestPaths.Under(root, target.StateKey));

            if (create)
            {
                directory.Create();
            }

            return directory;
        }
    }
}
