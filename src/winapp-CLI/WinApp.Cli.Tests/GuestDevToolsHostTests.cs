// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class GuestDevToolsHostTests
{
    private static readonly ExecutionTargetRef Target = new("sandbox", "default");
    private static GuestDevToolsHostPlan Plan => new("epoch",
        new(@"C:\project\App.csproj", [new("Main.xaml", 12, new string('A', 64))]),
        new GuestExecRequest { UseGuestWinapp = true, Arguments = ["guest-devtools-launch", "--exe", @"C:\guest\App.exe"] },
        "app-deployment", 1);

    [TestMethod]
    public void PrivateReceipt_AtomicReplacementRetainsOwnershipAndRejectsAnotherSession()
    {
        var root = TestPaths.TempRoot("guest-host-receipt");
        var host = new GuestDevToolsHost(new TargetStateDirectoryProvider(root), new FakeAppLauncherService());
        var id = Guid.NewGuid().ToString("N");
        try
        {
            var directory = host.Create(Target, id);
            GuestDevToolsHost.WriteState(directory, new("ready", SessionId: id));
            Assert.AreEqual("ready", host.ReadState(Target, id).Phase);
            using (var previous = new FileStream(Path.Combine(directory.FullName, "status.json"),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                GuestDevToolsHost.WriteState(directory, new("exited", ExitCode: 0, SessionId: id));
                Assert.AreEqual("exited", host.ReadState(Target, id).Phase);
                Assert.IsTrue(previous.Length > 0);
            }
            GuestDevToolsHost.WriteState(directory, new("ready", SessionId: Guid.NewGuid().ToString("N")));
            Assert.ThrowsExactly<InvalidDataException>(() => host.ReadState(Target, id));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void PrivateReceipt_RefusesForeignReaders()
    {
        var root = TestPaths.TempRoot("guest-host-receipt-acl");
        var host = new GuestDevToolsHost(new TargetStateDirectoryProvider(root), new FakeAppLauncherService());
        var id = Guid.NewGuid().ToString("N");
        try
        {
            var directory = host.Create(Target, id);
            GuestDevToolsHost.WriteState(directory, new("ready", SessionId: id));
            var file = new FileInfo(Path.Combine(directory.FullName, "status.json"));
            var acl = file.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.ReadData, AccessControlType.Allow));
            file.SetAccessControl(acl);
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => host.ReadState(Target, id));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void PrivatePlan_RoundTripsAndRejectsForeignAccess()
    {
        var root = TestPaths.TempRoot("guest-host-plan");
        var host = new GuestDevToolsHost(new TargetStateDirectoryProvider(root), new FakeAppLauncherService());
        var id = Guid.NewGuid().ToString("N");
        try
        {
            var directory = host.Create(Target, id);
            host.WritePlan(Target, id, Plan);
            var read = host.ReadPlan(Target, id);
            Assert.AreEqual(Plan.Epoch, read.Epoch);
            CollectionAssert.AreEqual(Plan.Request.Arguments.ToArray(), read.Request.Arguments.ToArray());
            Assert.AreEqual("Main.xaml", read.Sources.Files.Single().RelativePath);
            var file = new FileInfo(Path.Combine(directory.FullName, "launch.json"));
            var acl = file.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.ReadData, AccessControlType.Allow));
            file.SetAccessControl(acl);
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => host.ReadPlan(Target, id));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void SeededIdentity_AndInvalidIdentity_AreNeverRepairedOrUsed()
    {
        var root = TestPaths.TempRoot("guest-host-seeded");
        var host = new GuestDevToolsHost(new TargetStateDirectoryProvider(root), new FakeAppLauncherService());
        var id = Guid.NewGuid().ToString("N");
        try
        {
            host.Create(Target, id);
            Assert.ThrowsExactly<IOException>(() => host.Create(Target, id));
            Assert.ThrowsExactly<InvalidOperationException>(() => host.Resolve(Target, @"..\project"));
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => host.ReadPlan(Target, Guid.NewGuid().ToString("N")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ActualParentPipe_HandoffUsesReadinessAndSuppressesInheritedStreams(bool detach)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var root = TestPaths.TempRoot("guest-host-handoff");
        var process = new OwnedProcess();
        var launcher = new FakeAppLauncherService { LaunchOverride = () => process };
        var host = new GuestDevToolsHost(new TargetStateDirectoryProvider(root), launcher);
        var id = Guid.NewGuid().ToString("N");
        host.Create(Target, id);
        using var helper = new NamedPipeClientStream(".", GuestDevToolsHost.PipeName(id), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var observedReady = new TaskCompletionSource<GuestDevToolsHostMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var starting = host.StartAsync(Target, id, Plan, detach, timeout.Token, message => observedReady.SetResult(message));
            await helper.ConnectAsync(timeout.Token);
            Assert.IsFalse(starting.IsCompleted, "Pipe connection is not application/relay/inspection readiness.");
            var frame = new GuestInspectedAppFrame("ready", new(123, 456), 20, true);
            await GuestCommentFrames.WriteAsync(helper, new GuestDevToolsHostMessage("ready", frame, SessionId: id, BindingId: id),
                GuestCommentsJsonContext.Default.GuestDevToolsHostMessage, timeout.Token);
            var control = JsonSerializer.Deserialize(await GuestCommentFrames.ReadAsync(helper, timeout.Token),
                GuestCommentsJsonContext.Default.GuestInspectedAppControl)!;
            Assert.AreEqual(detach ? "detach" : "wait", control.Kind);
            Assert.IsFalse(starting.IsCompleted, "Detach must wait for the retained owner to acknowledge the handoff.");
            Assert.IsFalse(observedReady.Task.IsCompleted, "Run output must not report readiness before accepted ownership.");
            await GuestCommentFrames.WriteAsync(helper, new GuestDevToolsHostMessage("accepted", SessionId: id),
                GuestCommentsJsonContext.Default.GuestDevToolsHostMessage, timeout.Token);
            Assert.AreEqual(frame, (await observedReady.Task.WaitAsync(timeout.Token)).App);
            if (!detach)
            {
                Assert.IsFalse(starting.IsCompleted, "Foreground invocation must retain its owner until app exit.");
                await GuestCommentFrames.WriteAsync(helper, new GuestDevToolsHostMessage("exited", ExitCode: 7, SessionId: id),
                    GuestCommentsJsonContext.Default.GuestDevToolsHostMessage, timeout.Token);
                process.Exit();
            }
            var result = await starting;
            Assert.AreEqual(detach ? "ready" : "exited", result.Phase);
            Assert.IsFalse(process.Killed);
            Assert.AreEqual(LaunchStdioMode.Suppress, launcher.LastLaunchStdioMode);
            Assert.AreEqual(detach, !process.HasExited);
        }
        finally
        {
            process.Exit();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(0, 1, 1, true)]
    [DataRow(123, 0, 1, true)]
    [DataRow(123, 1, -1, true)]
    [DataRow(123, 1, 1, false)]
    public async Task InvalidReadiness_NeverPublishesOrTransfersOwnership(int pid, long ticks, int nodes, bool binding)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var root = TestPaths.TempRoot("guest-host-invalid-ready");
        var process = new OwnedProcess();
        var host = new GuestDevToolsHost(new TargetStateDirectoryProvider(root),
            new FakeAppLauncherService { LaunchOverride = () => process });
        var id = Guid.NewGuid().ToString("N");
        host.Create(Target, id);
        using var helper = new NamedPipeClientStream(".", GuestDevToolsHost.PipeName(id), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var published = false;
        try
        {
            var starting = host.StartAsync(Target, id, Plan, true, timeout.Token, _ => published = true);
            await helper.ConnectAsync(timeout.Token);
            await GuestCommentFrames.WriteAsync(helper, new GuestDevToolsHostMessage("ready",
                new("ready", new(pid, ticks), nodes, true), SessionId: id, BindingId: binding ? id : null),
                GuestCommentsJsonContext.Default.GuestDevToolsHostMessage, timeout.Token);
            await Assert.ThrowsExactlyAsync<IOException>(() => starting);
            Assert.IsFalse(published);
            Assert.IsTrue(process.Killed);
        }
        finally
        {
            process.Exit();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ParentCancellationBeforeReady_StopsOnlyItsOwnedHelper()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var root = TestPaths.TempRoot("guest-host-cancel");
        var process = new OwnedProcess();
        var host = new GuestDevToolsHost(new TargetStateDirectoryProvider(root),
            new FakeAppLauncherService { LaunchOverride = () => process });
        var id = Guid.NewGuid().ToString("N");
        host.Create(Target, id);
        using var helper = new NamedPipeClientStream(".", GuestDevToolsHost.PipeName(id), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            var starting = host.StartAsync(Target, id, Plan, detach: true, timeout.Token);
            await helper.ConnectAsync(timeout.Token);
            await timeout.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => starting);
            Assert.IsTrue(process.Killed);
        }
        finally
        {
            process.Exit();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class OwnedProcess : ILaunchedProcess
    {
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public uint ProcessId => (uint)Environment.ProcessId;
        public string? PackageFamilyName => null;
        public string? ApplicationUserModelId => null;
        public string? ExecutablePath => Environment.ProcessPath;
        public bool HasExited => completion.Task.IsCompleted;
        public int ExitCode => 0;
        public bool Killed { get; private set; }
        public Task WaitForExitAsync(CancellationToken cancellationToken) => completion.Task.WaitAsync(cancellationToken);
        public void Kill() { Killed = true; Exit(); }
        public void Exit() => completion.TrySetResult();
        public void Dispose() { }
    }
}
