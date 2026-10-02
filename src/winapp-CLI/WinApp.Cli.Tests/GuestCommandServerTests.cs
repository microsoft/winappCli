// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Tests;

/// <summary>
/// Runs the host command channel against the real guest command server over an in-memory transport.
/// </summary>
/// <remarks>
/// Both halves of the protocol are exercised together, so a change that breaks their agreement —
/// a renamed message type, a different operation-identity encoding, a stream header change — fails
/// here rather than only inside a real Sandbox. Nothing in this file touches Windows Sandbox, which
/// is the structural point: if orchestration ever reached for a <c>wsb</c> command, these tests
/// could not run at all.
/// </remarks>
[TestClass]
public class GuestCommandServerTests
{
    private static readonly ExecutionTargetEpoch Epoch = ExecutionTargetEpoch.Create("sandbox-1", "nonce-a");

    private static readonly string[] InspectArguments = ["ui", "inspect"];

    private static GuestSessionInfo Interactive => new(SessionId: 1, "WinSta0", HasInputDesktop: true);

    private static GuestAgentIdentity Identity => new(
        Version: "9.9.9",
        BinaryHash: "abc123",
        Architecture: "arm64",
        ProtocolMinimum: GuestProtocol.MinimumVersion,
        ProtocolMaximum: GuestProtocol.CurrentVersion);

    private static GuestExecRequest Request(params string[] arguments) => new()
    {
        Executable = "winapp.exe",
        Arguments = [.. arguments],
    };

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DevToolsSession_DrainsFailureFrameBeforeReportingEarlyExit(bool immediateExit)
    {
        var root = TestPaths.TempRoot("devtools-failure");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "App.csproj"), "<Project/>");
        File.WriteAllText(Path.Combine(root, "Main.xaml"), "<Page/>");
        try
        {
            using var harness = new Harness(Interactive, guestWinapp: @"C:\WinApp\winapp.exe");
            harness.Server.QueryProcessImpl = (pid, start) => pid == 123 && start == 456;
            var target = new PreparedTarget(new("sandbox", "default"), harness.Channel, Epoch,
                await harness.Channel.GetCapabilitiesAsync(harness.Token), true);
            var states = new DeploymentStateStore(new TargetStateDirectoryProvider(Path.Combine(root, "state")));
            var deployment = states.Commit(target.Reference, new DeploymentState
            {
                SchemaVersion = DeploymentStateStore.CurrentSchemaVersion,
                Revision = 0,
                DeploymentId = "failed-app",
                TargetEpoch = Epoch.Value,
                Dirty = false,
            }, 0);
            var session = new GuestDevToolsSession(target,
                new(Path.Combine(root, "App.csproj"), [new("Main.xaml", 7, new string('A', 64))]), new CommentStore(),
                new GuestApplicationRunner(new TargetDeploymentService(states)), deployment);
            var host = new GuestDevToolsHost(new TargetStateDirectoryProvider(Path.Combine(root, "state")), new FakeAppLauncherService());
            var id = Guid.NewGuid().ToString("N");
            var directory = host.Create(target.Reference, id);
            var running = session.RunAsync(new GuestExecRequest { UseGuestWinapp = true, Arguments = ["guest-devtools-launch"] },
                (_, _) => throw new AssertFailedException("A failed launch cannot become ready."),
                _ => { }, harness.Token, publishStarted: frame =>
                    GuestDevToolsHost.WriteState(directory, new("started", frame, SessionId: id)));
            var launch = await harness.Processes.WaitForNextAsync(harness.Token);
            launch.OnStandardInput = (_, _) => { launch.Exit(1); return Task.CompletedTask; };
            await launch.EmitBytesAsync(GuestStreamId.StandardOutput,
                GuestCommentFrames.Encode(new GuestInspectedAppFrame("started", new(123, 456)),
                    GuestCommentsJsonContext.Default.GuestInspectedAppFrame));
            var relay = await harness.Processes.WaitForNextAsync(harness.Token);
            var started = host.ReadState(target.Reference, id);
            Assert.AreEqual("started", started.Phase);
            Assert.AreEqual(new GuestProcessStart(123, 456), started.App!.Process);
            Assert.AreNotEqual(launch.ProcessId, started.App.Process!.ProcessId);
            Assert.IsNull(started.BindingId);
            await launch.EmitBytesAsync(GuestStreamId.StandardOutput,
                GuestCommentFrames.Encode(new GuestInspectedAppFrame("failed", new(123, 456),
                    Error: "Guest DevTools negotiation failed (unauthorized, code -32004)."),
                    GuestCommentsJsonContext.Default.GuestInspectedAppFrame));
            if (immediateExit)
            {
                launch.Exit(1);
            }
            var failure = await Assert.ThrowsAsync<IOException>(() => running.WaitAsync(harness.Token));
            StringAssert.Contains(failure.Message, "negotiation failed");
            Assert.IsTrue(launch.Disposed);
            Assert.IsTrue(relay.Disposed);
            Assert.IsTrue(relay.StopRequested);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task DevToolsSession_PublishesActualAppIdentityOnlyAfterRelayAndInspection_ThenDrainsBothOperations(bool competingCommit, bool cancel)
    {
        var root = TestPaths.TempRoot("devtools-session");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "App.csproj"), "<Project/>");
        File.WriteAllText(Path.Combine(root, "Main.xaml"), "<Page/>");
        try
        {
            using var harness = new Harness(Interactive, guestWinapp: @"C:\WinApp\winapp.exe");
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(harness.Token);
            harness.Server.QueryProcessImpl = (pid, start) => pid == 123 && start == 456;
            var target = new PreparedTarget(new("sandbox", "default"), harness.Channel, Epoch,
                await harness.Channel.GetCapabilitiesAsync(harness.Token), true);
            var states = new DeploymentStateStore(new TargetStateDirectoryProvider(Path.Combine(root, "state")));
            var deployment = states.Commit(target.Reference, new DeploymentState
            {
                SchemaVersion = DeploymentStateStore.CurrentSchemaVersion,
                Revision = 0,
                DeploymentId = "owned-app",
                TargetEpoch = Epoch.Value,
                Dirty = false,
            }, 0);
            if (competingCommit)
            {
                _ = states.Commit(target.Reference, deployment, deployment.Revision);
            }
            var published = new TaskCompletionSource<GuestCommentBinding>(TaskCreationOptions.RunContinuationsAsynchronously);
            var session = new GuestDevToolsSession(target,
                new(Path.Combine(root, "App.csproj"), [new("Main.xaml", 7, new string('A', 64))]), new CommentStore(),
                new GuestApplicationRunner(new TargetDeploymentService(states)), deployment);
            var running = session.RunAsync(new GuestExecRequest { UseGuestWinapp = true, Arguments = ["guest-devtools-launch"] },
                (frame, binding) =>
                {
                    Assert.AreEqual(new GuestProcessStart(123, 456), frame.Process);
                    Assert.AreEqual(22, frame.NodeCount);
                    published.TrySetResult(binding);
                    return Task.CompletedTask;
                }, _ => { }, cancellation.Token);
            var launch = await harness.Processes.WaitForNextAsync(harness.Token);
            if (competingCommit)
            {
                var failure = await Assert.ThrowsAsync<IOException>(() => running.WaitAsync(harness.Token));
                StringAssert.Contains(failure.Message, "deployment ownership");
                Assert.IsFalse(published.Task.IsCompleted);
                Assert.IsTrue(launch.StopRequested);
                Assert.IsTrue(launch.Disposed);
                Assert.IsNull(states.Read(target.Reference, deployment.DeploymentId)!.TrackedOperationProcessId);
                return;
            }
            var gracefulStop = false;
            var controlReader = new GuestCommentFrames.Reader(bytes =>
            {
                var control = JsonSerializer.Deserialize(bytes, GuestCommentsJsonContext.Default.GuestInspectedAppControl)!;
                if (control.Kind == "stop")
                {
                    gracefulStop = true;
                    launch.Exit(0);
                    return;
                }
                Assert.AreEqual("owner-ready", control.Kind);
                launch.InitialOutput = launch.EmitBytesAsync(GuestStreamId.StandardOutput,
                    GuestCommentFrames.Encode(new GuestInspectedAppFrame("ready", new(123, 456), 22, true),
                        GuestCommentsJsonContext.Default.GuestInspectedAppFrame));
            });
            launch.OnStandardInput = (bytes, _) =>
            {
                controlReader.Append(bytes.Span);
                return Task.CompletedTask;
            };
            await launch.EmitBytesAsync(GuestStreamId.StandardOutput,
                GuestCommentFrames.Encode(new GuestInspectedAppFrame("started", new(123, 456)),
                    GuestCommentsJsonContext.Default.GuestInspectedAppFrame));
            var relay = await harness.Processes.WaitForNextAsync(harness.Token);
            Assert.IsFalse(published.Task.IsCompleted);
            var bindingIdIndex = relay.Request.Arguments.IndexOf("--binding") + 1;
            var bindingId = relay.Request.Arguments[bindingIdIndex];
            await relay.EmitBytesAsync(GuestStreamId.StandardOutput,
                GuestCommentFrames.Encode(new GuestCommentEnvelope("ready", new(bindingId, target.Reference.StateKey,
                    Epoch.Value, new(123, 456), Guid.NewGuid().ToString("N"), "", "", null)),
                    GuestCommentsJsonContext.Default.GuestCommentEnvelope));
            var publishedBinding = await published.Task.WaitAsync(harness.Token);
            var recorded = states.Read(target.Reference, deployment.DeploymentId)!;
            Assert.AreEqual(launch.ProcessId, recorded.TrackedOperationProcessId);
            Assert.AreEqual(launch.StartTicksUtc, recorded.TrackedOperationProcessStartTicksUtc);
            Assert.AreEqual(deployment.Revision + 1, recorded.Revision);
            Assert.AreEqual(123, publishedBinding.Process.ProcessId);
            Assert.AreNotEqual(launch.ProcessId, publishedBinding.Process.ProcessId, "A barrier ID must never become the app target.");
            Assert.IsFalse(running.IsCompleted);
            if (cancel)
            {
                await cancellation.CancelAsync();
                await Assert.ThrowsAsync<OperationCanceledException>(() => running.WaitAsync(harness.Token));
                Assert.IsTrue(gracefulStop, "The guest launcher must release its actual app before forced job cancellation.");
                Assert.IsFalse(launch.StopRequested);
            }
            else
            {
                launch.Exit(0);
                Assert.AreEqual(0, await running.WaitAsync(harness.Token));
            }
            Assert.IsTrue(relay.StopRequested);
            Assert.IsTrue(relay.Disposed);
            Assert.IsTrue(launch.Disposed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("ready")]
    [DataRow("pid-reused")]
    [DataRow("epoch-recreated")]
    [DataRow("cli-changed")]
    [DataRow("started")]
    [DataRow("failed")]
    public async Task GuestSelector_UsesProtectedHostReceiptAndRechecksActualGuestLifetime(string state)
    {
        var root = TestPaths.TempRoot("guest-selector-channel");
        Directory.CreateDirectory(root);
        try
        {
            var cli = Path.Combine(root, "winapp.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "kernel32.dll"), cli);
            File.Copy(cli, Path.Combine(root, GuestDevTools.NativeFileName));
            File.Copy(typeof(GuestCommandServerTests).Assembly.Location, Path.Combine(root, GuestDevTools.ManagedFileName));
            var expected = (await GuestDevTools.ReadCapabilitiesAsync(cli, CancellationToken.None))!;
            using var harness = new Harness(Interactive, guestWinapp: cli,
                agentIdentity: Identity with { Architecture = expected.Architecture });
            var queries = 0;
            harness.Server.QueryProcessImpl = (pid, start) =>
            {
                Interlocked.Increment(ref queries);
                return pid == 123 && start == (state == "pid-reused" ? 457 : 456);
            };
            var target = new PreparedTarget(new("sandbox", "default"), harness.Channel, Epoch,
                await harness.Channel.GetCapabilitiesAsync(harness.Token), true);
            var host = new GuestDevToolsHost(new TargetStateDirectoryProvider(Path.Combine(root, "state")), new FakeAppLauncherService());
            var id = Guid.NewGuid().ToString("N");
            var directory = host.Create(target.Reference, id);
            var epoch = state == "epoch-recreated" ? "previous:incarnation" : Epoch.Value;
            host.WritePlan(target.Reference, id, new(epoch,
                new(Path.Combine(root, "App.csproj"), [new("Main.xaml", 7, new string('A', 64))]),
                new GuestExecRequest { UseGuestWinapp = true, Arguments = ["guest-devtools-launch"] }, "app", 1));
            var binding = Guid.NewGuid().ToString("N");
            GuestDevToolsHost.WriteState(directory, new(state is "started" or "failed" ? state : "ready", new("ready", new(123, 456), 22, true),
                SessionId: id, BindingId: binding));
            if (state == "cli-changed")
            {
                using var changed = new FileStream(cli, FileMode.Append, FileAccess.Write);
                changed.WriteByte(1);
            }
            var resolving = host.ResolveApplicationAsync(target, GuestDevToolsSelector.ForLaunch(id), expected, harness.Token);
            if (state == "ready")
            {
                var app = await resolving;
                Assert.AreEqual(new GuestProcessStart(123, 456), app.Process);
                Assert.AreEqual(binding, app.BindingId);
                Assert.AreEqual(1, queries);
                Assert.AreEqual(GuestDevToolsSelector.ForLaunch(id), host.FindSelector(target, app.Process));
                Assert.AreEqual(GuestDevToolsSelector.ForProcess(target.Reference, Epoch, new(123, 457)),
                    host.FindSelector(target, new(123, 457)));
            }
            else if (state == "cli-changed")
            {
                var failure = await Assert.ThrowsAsync<ExecutionTargetException>(() => resolving);
                Assert.AreEqual(ExecutionTargetErrorCodes.AgentIncompatible, failure.Error.Code);
                Assert.AreEqual(0, queries);
            }
            else if (state is "started" or "failed")
            {
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => resolving);
                StringAssert.Contains(failure.Message, "no longer ready");
                Assert.AreEqual(0, queries);
                Assert.AreEqual(GuestDevToolsSelector.ForProcess(target.Reference, Epoch, new(123, 456)),
                    host.FindSelector(target, new(123, 456)));
            }
            else
            {
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => resolving);
                StringAssert.Contains(failure.Message, "old selector and its element handles");
                Assert.AreEqual(state == "pid-reused" ? 1 : 0, queries);
            }
            Assert.IsTrue(harness.Processes.Started.IsEmpty, "Scope validation must not inject, launch or fall back to a host process.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("NativeIntegration")]
    [DataRow(-30, "available")]
    [DataRow(2, "available")]
    [DataRow(3, "stale")]
    public async Task SourceSnapshot_TransfersOnlyCapturedFiles_AndRetainsReadOnlyGuestContent(int sourceOffsetSeconds, string authoredState)
    {
        var root = TestPaths.TempRoot("guest-source-transfer");
        var host = Directory.CreateDirectory(Path.Combine(root, "host"));
        File.WriteAllText(Path.Combine(host.FullName, "App.csproj"), "<Project/>");
        File.WriteAllText(Path.Combine(host.FullName, "Main.xaml"), "<Page>Exact source</Page>");
        var built = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var timestamp = built.UtcDateTime.AddSeconds(sourceOffsetSeconds).AddTicks(1234567);
        File.SetLastWriteTimeUtc(Path.Combine(host.FullName, "Main.xaml"), timestamp);
        try
        {
            var snapshot = new DirectoryInfo(Path.Combine(root, "capture"));
            var manifest = await GuestSourceSnapshot.CreateAsync(new(Path.Combine(host.FullName, "App.csproj")),
                ["Main.xaml"], snapshot, CancellationToken.None);
            var files = new GuestFileService(Path.Combine(root, "guest"));
            using var harness = new Harness(Interactive, files: files);
            using var mutation = new TargetMutationLease(new FileStream(Path.Combine(root, "mutation"),
                FileMode.Create, FileAccess.ReadWrite, FileShare.None), false);
            var target = new PreparedTarget(new("sandbox", "default"), harness.Channel, Epoch,
                await harness.Channel.GetCapabilitiesAsync(harness.Token), true, mutation);
            var launch = Guid.NewGuid();
            var deployed = await GuestSourceSnapshot.DeployAsync(target, manifest, snapshot, launch, harness.Token);
            var copied = await harness.Channel.ListFilesAsync(
                new(GuestRootNames.Artifacts, "devtools-sources-" + launch.ToString("N")), harness.Token);
            Assert.HasCount(2, copied);
            Assert.AreEqual(timestamp.Ticks, copied.Single(file => file.RelativePath == "Main.xaml").LastWriteUtcTicks);
            Assert.IsFalse(File.ReadAllText(Path.Combine(deployed.GuestRoot!, GuestSourceSnapshot.InventoryFileName))
                .Contains(host.FullName, StringComparison.OrdinalIgnoreCase), "Guest inventory must not publish the host project or store path.");
            var source = Path.Combine(deployed.GuestRoot!, "Main.xaml");
            Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(source));
            using (await GuestSourceSnapshot.OpenReadOnlyAsync(new(deployed.GuestRoot!), deployed.ManifestHash!, harness.Token))
            {
                Assert.AreEqual("<Page>Exact source</Page>", File.ReadAllText(source));
                var fixture = NativeTestFixture.Resolve();
                var start = new System.Diagnostics.ProcessStartInfo(fixture)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (var argument in new[] { "--read-source", deployed.GuestRoot!, "Main.xaml",
                    built.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture) })
                {
                    start.ArgumentList.Add(argument);
                }
                using var reader = System.Diagnostics.Process.Start(start)!;
                try
                {
                    var output = reader.StandardOutput.ReadToEndAsync(harness.Token);
                    var error = reader.StandardError.ReadToEndAsync(harness.Token);
                    await reader.WaitForExitAsync(harness.Token);
                    Assert.AreEqual(0, reader.ExitCode, await error);
                    Assert.AreEqual(authoredState, (await output).Trim(),
                        "The shipping native reader must retain the original source-to-build freshness relationship.");
                }
                finally
                {
                    if (!reader.HasExited)
                    {
                        reader.Kill(entireProcessTree: true);
                        await reader.WaitForExitAsync(CancellationToken.None);
                    }
                }
                Assert.ThrowsExactly<IOException>(() => File.WriteAllText(source, "guest edit"));
                Assert.ThrowsExactly<IOException>(() => File.Delete(source));
                Assert.ThrowsExactly<IOException>(() => File.Move(source, source + ".moved"));
            }
            await Assert.ThrowsAsync<IOException>(() =>
                GuestSourceSnapshot.DeployAsync(target, manifest, snapshot, launch, harness.Token));
            File.WriteAllText(source, "changed after the retained owner exited");
            await Assert.ThrowsAsync<IOException>(() =>
                GuestSourceSnapshot.OpenReadOnlyAsync(new(deployed.GuestRoot!), deployed.ManifestHash!, harness.Token));
            File.WriteAllText(source, "failure released every source handle");
            mutation.Dispose();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                GuestSourceSnapshot.DeployAsync(target, manifest, snapshot, Guid.NewGuid(), harness.Token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CommentRelay_UsesGuestCliAndMultiplexedStreams_AcknowledgesHostPersistence()
    {
        var root = TestPaths.TempRoot("comment-relay");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "App.csproj"), "<Project/>");
        File.WriteAllText(Path.Combine(root, "Main.xaml"), "<Page/>");
        try
        {
            using var harness = new Harness(Interactive, guestWinapp: @"C:\WinApp\winapp.exe");
            var alive = true;
            harness.Server.QueryProcessImpl = (pid, start) => pid == 123 && start == 456 && Volatile.Read(ref alive);
            var binding = new GuestCommentBinding(new("sandbox", "default"), Epoch, new(123, 456),
                new FileInfo(Path.Combine(root, "App.csproj")), ["Main.xaml"]);
            var store = new CommentStore();
            var diagnostics = new StringBuilder();
            var relay = new GuestCommentRelay(binding, new(binding, store), harness.Channel)
            {
                LifetimePollInterval = TimeSpan.FromMilliseconds(10),
            };
            var running = relay.RunAsync(bytes => diagnostics.Append(Encoding.UTF8.GetString(bytes.Span)), harness.Token);
            var process = await harness.Processes.WaitForNextAsync(harness.Token);
            Assert.AreEqual(@"C:\WinApp\winapp.exe", process.Request.Executable);
            CollectionAssert.Contains(process.Request.Arguments, binding.Id);
            Assert.AreEqual("guest-comment-relay", process.Request.Arguments[0]);
            Assert.AreEqual("1", process.Request.Environment!["WINAPP_CLI_TELEMETRY_OPTOUT"]);
            var replies = System.Threading.Channels.Channel.CreateUnbounded<GuestCommentReply>();
            var refreshes = System.Threading.Channels.Channel.CreateUnbounded<GuestCommentReply>();
            var reader = new GuestCommentFrames.Reader(bytes =>
            {
                var reply = JsonSerializer.Deserialize(bytes, GuestCommentsJsonContext.Default.GuestCommentReply)!;
                (reply.OperationId == GuestCommentReply.RefreshOperationId ? refreshes : replies).Writer.TryWrite(reply);
            });
            process.OnStandardInput = (bytes, _) =>
            {
                reader.Append(bytes.Span);
                return Task.CompletedTask;
            };
            await process.EmitAsync(GuestStreamId.StandardError, "diagnostic-only");
            var request = new GuestCommentRequest(binding.Id, binding.TargetId, binding.Epoch, binding.Process,
                Guid.NewGuid().ToString("N"), "note", string.Empty,
                new Comment { Id = "note", Text = " \nexact\n ", Anchor = new() { SourceFile = "Main.xaml" } });
            var frame = GuestCommentFrames.Encode(new GuestCommentEnvelope("apply", request),
                GuestCommentsJsonContext.Default.GuestCommentEnvelope);
            await process.EmitBytesAsync(GuestStreamId.StandardOutput,
                GuestCommentFrames.Encode(new GuestCommentEnvelope("ready", request with { Replacement = null }),
                    GuestCommentsJsonContext.Default.GuestCommentEnvelope));
            // Split inside the length header and UTF-8 payload: stdout chunks are not messages.
            await process.EmitBytesAsync(GuestStreamId.StandardOutput, frame.AsMemory(0, 2));
            await process.EmitBytesAsync(GuestStreamId.StandardOutput, frame.AsMemory(2, 7));
            await process.EmitBytesAsync(GuestStreamId.StandardOutput, frame.AsMemory(9));
            var acknowledgement = await replies.Reader.ReadAsync(harness.Token);
            Assert.IsNull(acknowledgement.Error);
            Assert.AreEqual(request.Replacement!.Text, store.Get(binding.StorePath, "note")!.Text);
            Assert.AreEqual(CommentStore.Revision(store.Get(binding.StorePath, "note")), acknowledgement.Commit!.PersistedRevision);

            var inspection = harness.Channel.ExecuteAsync(Request("devtools", "inspect"), null, harness.Token);
            var otherProcess = await harness.Processes.WaitForNextAsync(harness.Token);
            otherProcess.Exit(0);
            Assert.AreEqual(0, (await inspection).ExitCode);
            Assert.IsFalse(running.IsCompleted, "A relay must not monopolize or finish with another operation.");
            Assert.AreEqual("diagnostic-only", diagnostics.ToString());

            store.Update(binding.StorePath, "note", note => note.Text = "host edit after lost ack");
            var hostGeneration = store.Load(binding.StorePath).Generation;
            GuestCommentReply refresh;
            do
            {
                refresh = await refreshes.Reader.ReadAsync(harness.Token);
            }
            while (refresh.Generation < hostGeneration);
            Assert.AreEqual(hostGeneration, refresh.Generation, "A host-side edit reaches the guest app as an ordered snapshot.");
            Assert.AreEqual("host edit after lost ack", refresh.Comments!.Single().Text);
            await process.EmitBytesAsync(GuestStreamId.StandardOutput, frame);
            var replay = await replies.Reader.ReadAsync(harness.Token);
            Assert.AreEqual(acknowledgement.Commit.PersistedRevision, replay.Commit!.PersistedRevision);
            Assert.AreEqual("host edit after lost ack", replay.Commit.Current!.Text);
            Volatile.Write(ref alive, false);
            await running.WaitAsync(harness.Token);
            Assert.IsTrue(process.StopRequested);
            Assert.IsTrue(process.Disposed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("cancel")]
    [DataRow("oversize")]
    [DataRow("disconnect")]
    [DataRow("stale")]
    [DataRow("malformed")]
    public async Task CommentRelay_StopOrProtocolFailure_DrainsOwnedOperationWithoutCreatingStore(string reason)
    {
        var root = TestPaths.TempRoot("relay-stop");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "App.csproj"), "<Project/>");
        File.WriteAllText(Path.Combine(root, "Main.xaml"), "<Page/>");
        try
        {
            using var harness = new Harness(Interactive, guestWinapp: @"C:\WinApp\winapp.exe");
            harness.Server.QueryProcessImpl = (_, _) => true;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(harness.Token);
            var binding = new GuestCommentBinding(new("sandbox", "default"), Epoch, new(123, 456),
                new FileInfo(Path.Combine(root, "App.csproj")), ["Main.xaml"]);
            var relay = new GuestCommentRelay(binding, new(binding, new CommentStore()), harness.Channel);
            var running = relay.RunAsync(_ => { }, cancellation.Token);
            var process = await harness.Processes.WaitForNextAsync(harness.Token);
            if (reason == "cancel")
            {
                await cancellation.CancelAsync();
                await Assert.ThrowsAsync<OperationCanceledException>(() => running);
            }
            else if (reason == "oversize")
            {
                await process.EmitBytesAsync(GuestStreamId.StandardOutput, BitConverter.GetBytes(GuestCommentFrames.MaximumLength + 1));
                await Assert.ThrowsAsync<InvalidDataException>(() => running);
            }
            else if (reason == "stale")
            {
                var request = new GuestCommentRequest(binding.Id, binding.TargetId, "old-epoch", binding.Process,
                    Guid.NewGuid().ToString("N"), "note", "", new Comment { Id = "note", Text = "wrong epoch", Anchor = new() { SourceFile = "Main.xaml" } });
                await process.EmitBytesAsync(GuestStreamId.StandardOutput,
                    GuestCommentFrames.Encode(new GuestCommentEnvelope("apply", request), GuestCommentsJsonContext.Default.GuestCommentEnvelope));
                await Assert.ThrowsAsync<InvalidOperationException>(() => running);
            }
            else if (reason == "malformed")
            {
                await process.EmitBytesAsync(GuestStreamId.StandardOutput, new byte[] { 1, 0, 0, 0, (byte)'{' });
                await Assert.ThrowsAsync<JsonException>(() => running);
            }
            else
            {
                process.Exit(1);
                await Assert.ThrowsAsync<IOException>(() => running);
            }
            Assert.IsTrue(process.Disposed);
            Assert.IsFalse(File.Exists(binding.StorePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("0")]
    [DataRow("1")]
    public async Task Execute_GuestWinapp_OptsOutBeforeStartingWithoutChangingOtherEnvironment(string? requestedOptOut)
    {
        using var harness = new Harness(Interactive, guestWinapp: @"C:\WinApp\winapp.exe");
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["WINAPP_UI_WORKFLOW_ID"] = "workflow-token",
            ["APP_TELEMETRY_ENABLED"] = "true",
        };
        if (requestedOptOut is not null)
        {
            environment["winapp_cli_telemetry_optout"] = requestedOptOut;
        }

        var execution = harness.Channel.ExecuteAsync(new GuestExecRequest
        {
            UseGuestWinapp = true,
            Arguments = [.. InspectArguments],
            Environment = environment,
        }, callbacks: null, harness.Token);
        var process = await harness.Processes.WaitForNextAsync(harness.Token);
        process.Exit(0);
        await execution;

        Assert.AreEqual(@"C:\WinApp\winapp.exe", process.Request.Executable);
        Assert.AreEqual("1", process.Request.Environment!["WINAPP_CLI_TELEMETRY_OPTOUT"]);
        Assert.AreEqual("workflow-token", process.Request.Environment["WINAPP_UI_WORKFLOW_ID"]);
        Assert.AreEqual("true", process.Request.Environment["APP_TELEMETRY_ENABLED"]);
        Assert.AreEqual(requestedOptOut, environment.GetValueOrDefault("WINAPP_CLI_TELEMETRY_OPTOUT"));
    }

    [TestMethod]
    public async Task Execute_ExternalApplication_PreservesItsRequestedEnvironment()
    {
        using var harness = new Harness(Interactive);
        var execution = harness.Channel.ExecuteAsync(new GuestExecRequest
        {
            Executable = @"C:\Apps\app.exe",
            Arguments = [],
            Environment = new Dictionary<string, string>
            {
                ["WINAPP_CLI_TELEMETRY_OPTOUT"] = "0",
                ["APP_TELEMETRY_ENABLED"] = "true",
            },
        }, callbacks: null, harness.Token);
        var process = await harness.Processes.WaitForNextAsync(harness.Token);
        process.Exit(0);
        await execution;

        Assert.AreEqual("0", process.Request.Environment!["WINAPP_CLI_TELEMETRY_OPTOUT"]);
        Assert.AreEqual("true", process.Request.Environment["APP_TELEMETRY_ENABLED"]);
    }

    [TestMethod]
    public async Task Capabilities_ReportsGuestArchitectureAndReadiness()
    {
        using var harness = new Harness(Interactive);

        var capabilities = await harness.Channel.GetCapabilitiesAsync(harness.Token);

        Assert.AreEqual("arm64", capabilities.Architecture);
        Assert.IsTrue(capabilities.SupportsRealInput);
        Assert.IsTrue(capabilities.SupportsScreenCapture);
        Assert.AreEqual(GuestOwnerContext.CooperativeUiTurnsVersion, capabilities.CooperativeUiTurnsVersion);

        // Windows Sandbox keeps nothing across teardown, which is why every new epoch must
        // reconcile deployments and runtimes from scratch.
        Assert.IsFalse(capabilities.PersistentStorage);
        Assert.IsNull(capabilities.DevTools, "Missing engines cannot advertise inspection support.");
    }

    [TestMethod]
    public async Task Capabilities_ReportsActualPairedEngineHashesOverCommandChannel()
    {
        var root = TestPaths.TempRoot(nameof(Capabilities_ReportsActualPairedEngineHashesOverCommandChannel));
        Directory.CreateDirectory(root);
        try
        {
            var cli = Path.Combine(root, "winapp.exe");
            var native = Path.Combine(root, GuestDevTools.NativeFileName);
            var managed = Path.Combine(root, GuestDevTools.ManagedFileName);
            File.Copy(Path.Combine(Environment.SystemDirectory, "kernel32.dll"), cli);
            File.Copy(cli, native);
            File.Copy(typeof(GuestCommandServerTests).Assembly.Location, managed);
            using var harness = new Harness(Interactive, guestWinapp: cli);
            var capabilities = await harness.Channel.GetCapabilitiesAsync(harness.Token);
            Assert.IsNotNull(capabilities.DevTools);
            Assert.AreEqual(await GuestAgentIdentity.ComputeBinaryHashAsync(native, harness.Token),
                capabilities.DevTools.NativeHash);
            Assert.AreEqual(await GuestAgentIdentity.ComputeBinaryHashAsync(managed, harness.Token),
                capabilities.DevTools.ManagedHash);
            Assert.AreEqual(await GuestAgentIdentity.ComputeBinaryHashAsync(cli, harness.Token),
                capabilities.DevTools.CliHash);
            Assert.AreEqual(2, capabilities.DevTools.Version);
            var execution = harness.Channel.ExecuteAsync(new GuestExecRequest { UseGuestWinapp = true, Arguments = ["--help"] },
                new GuestExecCallbacks(), harness.Token);
            var process = await harness.Processes.WaitForNextAsync(harness.Token);
            Assert.AreEqual(cli, process.Request.Executable, "The capability hashes the CLI that UseGuestWinapp actually executes.");
            process.Exit(0);
            Assert.AreEqual(0, (await execution).ExitCode);
            using (var changed = new FileStream(cli, FileMode.Append, FileAccess.Write))
            {
                changed.WriteByte(1);
            }
            var refreshed = (await harness.Channel.GetCapabilitiesAsync(harness.Token)).DevTools!;
            Assert.AreNotEqual(capabilities.DevTools.CliHash, refreshed.CliHash, "Same epoch must not imply binary parity.");
            Assert.AreEqual(capabilities.DevTools.NativeHash, refreshed.NativeHash);
            File.Delete(managed);
            Assert.IsNull((await harness.Channel.GetCapabilitiesAsync(harness.Token)).DevTools);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Capabilities_DisconnectedClient_KeepsInspectionButRefusesInput()
    {
        // A closed Sandbox client leaves the guest session and UI Automation working while real
        // input and Windows Graphics Capture stop. Reporting input as available here would let a
        // command claim delivery for input that never arrives.
        using var harness = new Harness(Interactive with { HasInputDesktop = false });

        var capabilities = await harness.Channel.GetCapabilitiesAsync(harness.Token);

        Assert.IsTrue(capabilities.SupportsInteractiveDesktop);
        Assert.IsFalse(capabilities.SupportsRealInput);
        Assert.IsFalse(capabilities.SupportsScreenCapture);
    }

    [TestMethod]
    public async Task QueryPackage_RegisteredResponseWithoutPackage_IsAProtocolFailure()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pair = new LoopbackTransportPair();
        await using var guest = pair.Guest;
        await using var channel = new GuestCommandChannel(pair.Host, Epoch);
        channel.Start();

        var query = channel.GetRegisteredPackageAsync(
            "Contoso.MyApp",
            "CN=Contoso",
            "Contoso.MyApp_abc",
            cancellation.Token);
        var requestFrame = await guest.ReceiveFrameAsync(cancellation.Token);
        var request = GuestPayloadCodec.TryDecodeJson(requestFrame!.Value.Span);

        await guest.SendFrameAsync(
            GuestPayloadCodec.EncodeJson(
                new GuestMessage
                {
                    Type = GuestMessageTypes.QueryPackageResponse,
                    OperationId = request!.OperationId,
                    TargetEpoch = Epoch.Value,
                    PackageRegistered = true,
                }),
            cancellation.Token);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() => query);

        Assert.AreEqual(ExecutionTargetErrorCodes.TransportFailed, failure.Error.Code);
        StringAssert.Contains(failure.Error.Message, "incomplete package registration response");
    }

    [TestMethod]
    public async Task UnregisterPackage_RemovesOnlyTheExactFullName()
    {
        var packages = new FakePackageRegistrationService();
        var launcher = new FakeAppLauncherService
        {
            FakePackageFullName = "Contoso.MyApp_2.0.0.0_arm64__abc",
            FakeRegisteredLocation = FakeLayout,
        };
        using var harness = new Harness(Interactive, appLauncher: launcher, packageRegistration: packages);

        await harness.Channel.UnregisterPackageAsync(
            "Contoso.MyApp_abc",
            "Contoso.MyApp_2.0.0.0_arm64__abc",
            FakeLayout,
            harness.Token);

        Assert.HasCount(1, packages.UnregisterByFullNameCalls);
        Assert.AreEqual(
            ("Contoso.MyApp_2.0.0.0_arm64__abc", false),
            packages.UnregisterByFullNameCalls[0]);
        Assert.HasCount(0, packages.UnregisterCalls);
    }

    [TestMethod]
    public async Task UnregisterPackage_WhenRegistrationLocationChanged_RefusesWithoutRemoval()
    {
        var packages = new FakePackageRegistrationService();
        var launcher = new FakeAppLauncherService
        {
            FakePackageFullName = "Contoso.MyApp_2.0.0.0_arm64__abc",
            FakeRegisteredLocation = @"C:\External\App",
        };
        using var harness = new Harness(Interactive, appLauncher: launcher, packageRegistration: packages);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            harness.Channel.UnregisterPackageAsync(
                "Contoso.MyApp_abc",
                "Contoso.MyApp_2.0.0.0_arm64__abc",
                FakeLayout,
                harness.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.PackageConflict, failure.Error.Code);
        Assert.HasCount(0, packages.UnregisterByFullNameCalls);
    }

    [TestMethod]
    public async Task Execute_StreamsOutputInOrderAndReturnsExitCode()
    {
        using var harness = new Harness(Interactive);

        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var execution = harness.Channel.ExecuteAsync(
            Request("ui", "inspect"),
            new GuestExecCallbacks(
                OnStarted: process => started.TrySetResult(process.ProcessId),
                OnStandardOutput: data => standardOutput.Append(Encoding.UTF8.GetString(data.Span)),
                OnStandardError: data => standardError.Append(Encoding.UTF8.GetString(data.Span))),
            harness.Token);

        var process = await harness.Processes.WaitForNextAsync(harness.Token);
        var processId = await started.Task.WaitAsync(harness.Token);

        Assert.AreEqual(process.ProcessId, processId);
        CollectionAssert.AreEqual(InspectArguments, process.Request.Arguments);

        await process.EmitAsync(GuestStreamId.StandardOutput, "first ");
        await process.EmitAsync(GuestStreamId.StandardOutput, "second");
        await process.EmitAsync(GuestStreamId.StandardError, "warning");
        process.Exit(3);

        var result = await execution;

        Assert.AreEqual(3, result.ExitCode);
        Assert.AreEqual(process.ProcessId, result.ProcessId);
        Assert.AreEqual("first second", standardOutput.ToString());
        Assert.AreEqual("warning", standardError.ToString());
    }

    [TestMethod]
    public async Task Execute_DetachedProcessReturnsAfterStartAndSurvivesTheChannel()
    {
        using var harness = new Harness(Interactive);

        var execution = harness.Channel.ExecuteAsync(
            new GuestExecRequest
            {
                Executable = "app.exe",
                Arguments = [],
                Detach = true,
            },
            callbacks: null,
            harness.Token);

        var process = await harness.Processes.WaitForNextAsync(harness.Token);
        var result = await execution.WaitAsync(harness.Token);

        Assert.AreEqual(0, result.ExitCode);
        Assert.IsFalse(process.StopRequested);
        Assert.IsFalse(process.Disposed);

        await harness.Channel.DisposeAsync();
        await process.EmitAsync(GuestStreamId.StandardOutput, "after disconnect").WaitAsync(TimeSpan.FromSeconds(5));
        process.Exit(17);

        // The agent should release the detached process after it exits.
        await WaitUntilAsync(() => process.Disposed, harness.Token);
    }

    [TestMethod]
    public async Task Execute_OutputProducerWaitsForTransportAndResumesInOrder()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var harness = new Harness(Interactive, beforeGuestSend: async (frame, token) =>
        {
            if (GuestPayloadCodec.TryGetKind(frame.Span, out var kind) && kind == GuestPayloadKind.Stream)
            {
                sending.TrySetResult();
                await gate.Task.WaitAsync(token);
            }
        });
        var output = new List<string>();
        var execution = harness.Channel.ExecuteAsync(
            Request("output"), new GuestExecCallbacks(
                OnStandardOutput: data => output.Add(Encoding.UTF8.GetString(data.Span))), harness.Token);
        var process = await harness.Processes.WaitForNextAsync(harness.Token);
        var completedChunks = 0;
        var producer = Task.Run(async () =>
        {
            for (var i = 0; i < 32; i++)
            {
                await process.EmitAsync(GuestStreamId.StandardOutput, $"{i:D2}:{new string('x', 64 * 1024)}");
                Interlocked.Increment(ref completedChunks);
            }
            process.Exit(0);
        }, harness.Token);
        try
        {
            await sending.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100, harness.Token);
            Assert.AreEqual(0, Volatile.Read(ref completedChunks), "A blocked send must pause the producer.");
            Assert.IsFalse(execution.IsCompleted);
        }
        finally
        {
            gate.TrySetResult();
        }

        await producer.WaitAsync(TimeSpan.FromSeconds(5));
        await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.HasCount(32, output);
        for (var i = 0; i < output.Count; i++)
        {
            StringAssert.StartsWith(output[i], $"{i:D2}:");
            Assert.AreEqual(64 * 1024 + 3, output[i].Length);
        }
    }

    [TestMethod]
    public async Task Execute_OutputBeforeRegistrationIsDeliveredAndDrained()
    {
        using var harness = new Harness(Interactive);
        harness.Processes.InitialOutput = "early output";
        var output = new StringBuilder();
        var execution = harness.Channel.ExecuteAsync(Request("fast"),
            new GuestExecCallbacks(OnStandardOutput: data => output.Append(Encoding.UTF8.GetString(data.Span))),
            harness.Token);
        var process = await harness.Processes.WaitForNextAsync(harness.Token);
        process.Exit(0);
        await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("early output", output.ToString());
    }

    [TestMethod]
    public async Task Cancellation_BlockedStandardInputDoesNotBlockStopOrItsAcknowledgement()
    {
        using var harness = new Harness(Interactive);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(harness.Token);
        var operationId = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new StringBuilder();
        var execution = harness.Channel.ExecuteAsync(Request("blocked-input"),
            new GuestExecCallbacks(
                OnOperationId: id => operationId.TrySetResult(id),
                OnStandardOutput: data => output.Append(Encoding.UTF8.GetString(data.Span))),
            cancellation.Token);
        var process = await harness.Processes.WaitForNextAsync(harness.Token);
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inputEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inputReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OnStandardInput = async (_, _) =>
        {
            writing.TrySetResult();
            try
            {
                // Synchronous Windows pipe handles can ignore cancellation until the child stops.
                await inputReleased.Task;
            }
            finally
            {
                inputEnded.TrySetResult();
            }
        };
        process.OnStop = async _ =>
        {
            stopping.TrySetResult();
            inputReleased.TrySetResult();
            await stopGate.Task;
            await process.EmitAsync(GuestStreamId.StandardOutput, "finalized");
        };

        var id = await operationId.Task.WaitAsync(harness.Token);
        await harness.Channel.SendStandardInputAsync(id, new byte[8192], harness.Token);
        await writing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        try
        {
            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await inputEnded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(execution.IsCompleted, "The host must wait for the guest's stop to finish.");
            await harness.Channel.GetCapabilitiesAsync(harness.Token).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            inputReleased.TrySetResult();
            stopGate.TrySetResult();
        }

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => execution.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual("finalized", output.ToString());
        await WaitUntilAsync(() => process.Disposed, harness.Token);
        await harness.Channel.GetCapabilitiesAsync(harness.Token).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task StandardInput_OverflowFailsExplicitlyAndStopsTheProcess()
    {
        using var harness = new Harness(Interactive);
        var operationId = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = harness.Channel.ExecuteAsync(Request("blocked-input"),
            new GuestExecCallbacks(OnOperationId: id => operationId.TrySetResult(id)), harness.Token);
        var process = await harness.Processes.WaitForNextAsync(harness.Token);
        var inputEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OnStandardInput = async (_, token) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                inputEnded.TrySetResult();
            }
        };

        await harness.Channel.SendStandardInputAsync(
            await operationId.Task.WaitAsync(harness.Token), new byte[5 * 1024 * 1024], harness.Token);
        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => execution.WaitAsync(TimeSpan.FromSeconds(5)));
        StringAssert.Contains(failure.Error.Message, "input buffer is full");
        Assert.IsTrue(process.StopRequested);
        Assert.IsTrue(inputEnded.Task.IsCompleted);
        await WaitUntilAsync(() => process.Disposed, harness.Token);
    }

    [TestMethod]
    public async Task Disconnect_CancelsBlockedInputAndReleasesTheProcess()
    {
        using var harness = new Harness(Interactive);
        var operationId = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = harness.Channel.ExecuteAsync(Request("blocked-input"),
            new GuestExecCallbacks(OnOperationId: id => operationId.TrySetResult(id)), harness.Token);
        var process = await harness.Processes.WaitForNextAsync(harness.Token);
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inputEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OnStandardInput = async (_, token) =>
        {
            writing.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                inputEnded.TrySetResult();
            }
        };
        await harness.Channel.SendStandardInputAsync(
            await operationId.Task.WaitAsync(harness.Token), new byte[8192], harness.Token);
        await writing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.Channel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => execution.WaitAsync(TimeSpan.FromSeconds(5)));
        await inputEnded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => process.Disposed, harness.Token);
        Assert.IsTrue(process.StopRequested);
    }

    [TestMethod]
    public async Task ListFiles_ServerBatchesRealInventoryBelowTheFrameLimit()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), $"guest-inventory-{Guid.NewGuid():N}");
        var files = new GuestFileService(root);
        var scope = new GuestPathScope(GuestRootNames.Deployment, "app");
        var directory = files.ResolveScopeDirectory(scope, create: true);
        try
        {
            for (var i = 0; i < 1800; i++)
            {
                await File.WriteAllBytesAsync(
                    Path.Combine(directory, $"{new string('文', 120)}-{i:D5}.bin"), []);
            }
            using var harness = new Harness(Interactive, files: files);
            var actual = await harness.Channel.ListFilesAsync(scope, harness.Token).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.HasCount(1800, actual);
            Assert.AreEqual($"{new string('文', 120)}-00000.bin", actual[0].RelativePath);
            Assert.AreEqual($"{new string('文', 120)}-01799.bin", actual[^1].RelativePath);
            Assert.HasCount(0, await harness.Channel.ListFilesAsync(
                new GuestPathScope(GuestRootNames.Work, null), harness.Token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
    [TestMethod]
    public async Task Execute_PreservesUnicodeAndArgumentBoundaries()
    {
        using var harness = new Harness(Interactive);

        // Arguments that would be mangled by any string-interpolated command line: embedded
        // quotes, spaces, and non-ASCII text.
        var arguments = new[] { "ui", "set-value", "a b\"c", "日本語 テキスト", string.Empty };

        var execution = harness.Channel.ExecuteAsync(
            new GuestExecRequest { Executable = "winapp.exe", Arguments = [.. arguments] },
            callbacks: null,
            harness.Token);

        var process = await harness.Processes.WaitForNextAsync(harness.Token);
        CollectionAssert.AreEqual(arguments, process.Request.Arguments);

        process.Exit(0);
        Assert.AreEqual(0, (await execution).ExitCode);
    }

    [TestMethod]
    public async Task StandardInput_IsForwardedAndClosed()
    {
        using var harness = new Harness(Interactive);

        var operationId = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);

        var execution = harness.Channel.ExecuteAsync(
            Request("ui", "record"),
            new GuestExecCallbacks(OnOperationId: id => operationId.TrySetResult(id)),
            harness.Token);

        var process = await harness.Processes.WaitForNextAsync(harness.Token);
        var id = await operationId.Task.WaitAsync(harness.Token);

        await harness.Channel.SendStandardInputAsync(id, "hello"u8.ToArray(), harness.Token);
        await harness.Channel.SendStandardInputAsync(id, " world"u8.ToArray(), harness.Token);
        await harness.Channel.CloseStandardInputAsync(id, harness.Token);

        await WaitUntilAsync(() => process.StandardInputClosed, harness.Token);

        // Chunks must arrive whole and in order: a recording command that reads a newline to stop
        // would otherwise stop on a torn read.
        Assert.AreEqual(
            "hello world",
            string.Concat(process.StandardInput.Select(Encoding.UTF8.GetString)));

        process.Exit(0);
        await execution;

        Assert.IsTrue(process.Disposed);
    }

    [TestMethod]
    public async Task Cancellation_RequestsGracefulStopInTheGuest()
    {
        using var harness = new Harness(Interactive);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(harness.Token);

        var execution = harness.Channel.ExecuteAsync(Request("run", "."), callbacks: null, cancellation.Token);
        var process = await harness.Processes.WaitForNextAsync(harness.Token);

        await cancellation.CancelAsync();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => execution);

        // Cancelling must actually reach the guest. Leaving the child running would strand a
        // process holding files the next deployment has to replace.
        await WaitUntilAsync(() => process.StopRequested, harness.Token);
    }

    [TestMethod]
    public async Task StaleEpoch_IsRefusedRatherThanApplied()
    {
        // The host believes it is talking to a generation the guest is not serving: exactly what a
        // command built before the Sandbox was recreated looks like.
        using var harness = new Harness(
            Interactive,
            hostEpoch: ExecutionTargetEpoch.Create("sandbox-1", "nonce-b"));

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => harness.Channel.ExecuteAsync(Request("run", "."), callbacks: null, harness.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.TargetStale, failure.Error.Code);
        Assert.IsTrue(harness.Processes.Started.IsEmpty, "A stale request must never start a process.");
    }

    [TestMethod]
    public async Task MatchingEpoch_IsAccepted()
    {
        using var harness = new Harness(Interactive);

        var execution = harness.Channel.ExecuteAsync(Request("run", "."), callbacks: null, harness.Token);
        var process = await harness.Processes.WaitForNextAsync(harness.Token);
        process.Exit(0);

        Assert.AreEqual(0, (await execution).ExitCode);
    }

    [TestMethod]
    public async Task ProcessStartFailure_IsReportedAsStructuredFailure()
    {
        using var harness = new Harness(Interactive);

        harness.Processes.FailWith = new ExecutionTargetErrorInfo
        {
            Code = ExecutionTargetErrorCodes.TransportFailed,
            Message = "The guest could not start 'winapp.exe'.",
        };

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => harness.Channel.ExecuteAsync(Request("run", "."), callbacks: null, harness.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.TransportFailed, failure.Error.Code);
    }

    [TestMethod]
    public async Task ServerShutdown_StopsRunningOperations()
    {
        using var harness = new Harness(Interactive);

        _ = harness.Channel.ExecuteAsync(Request("run", "."), callbacks: null, harness.Token);
        var process = await harness.Processes.WaitForNextAsync(harness.Token);

        await harness.StopServerAsync();

        // Nothing the agent started may outlive the connection that asked for it.
        await WaitUntilAsync(() => process.StopRequested, CancellationToken.None);
    }

    // ---- Missing working directory --------------------------------------------------

    [TestMethod]
    public async Task Exec_WithAMissingWorkingDirectory_NamesTheDirectoryRatherThanBlamingTheExecutable()
    {
        using var harness = new Harness(Interactive);

        var missing = @"C:\WinApp\does-not-exist\anywhere";

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            harness.Channel.ExecuteAsync(
                new GuestExecRequest { Executable = "app.exe", Arguments = [], WorkingDirectory = missing },
                callbacks: null,
                harness.Token));

        StringAssert.Contains(failure.Error.Message, missing);
        Assert.IsTrue(harness.Processes.Started.IsEmpty, "A missing --cwd must be refused before anything starts.");
    }

    // ---- Stop before redeploy --------------------------------------------------------

    private const string FakeLayout = @"C:\WinApp\deployments\dep-1-layout";

    /// <summary>The one full name every stop-before-redeploy case expects to be terminated.</summary>
    private static readonly string[] ExpectedStoppedFullNames = ["Contoso.MyApp_1.0.0.0_x64__abc"];

    [TestMethod]
    public async Task StopPackage_ResolvesTheFamilyNameAndTerminatesTheCurrentFullName()
    {
        var launcher = new FakeAppLauncherService
        {
            FakePackageFullName = "Contoso.MyApp_1.0.0.0_x64__abc",
            FakeRegisteredLocation = FakeLayout,
        };
        using var harness = new Harness(Interactive, appLauncher: launcher);

        await harness.Channel.StopPackageProcessesAsync("Contoso.MyApp_abc", FakeLayout, harness.Token);

        CollectionAssert.AreEqual(ExpectedStoppedFullNames, launcher.StopPackageCalls);
    }

    [TestMethod]
    public async Task StopPackage_WhenNothingIsCurrentlyRegistered_SucceedsWithoutTerminatingAnything()
    {
        var launcher = new FakeAppLauncherService { FakePackageFullName = null };
        using var harness = new Harness(Interactive, appLauncher: launcher);

        // Nothing registered under that family any more means nothing could be running under it.
        await harness.Channel.StopPackageProcessesAsync("Contoso.MyApp_abc", FakeLayout, harness.Token);

        Assert.AreEqual(0, launcher.StopPackageCalls.Count);
    }

    [TestMethod]
    public async Task QueryPackage_ReturnsAuthoritativeRegistrationDetails()
    {
        var launcher = new FakeAppLauncherService
        {
            FakePackageFullName = "Contoso.MyApp_2.0.0.0_arm64__fakefamily",
            FakeRegisteredLocation = FakeLayout,
            FakeIsDevelopmentMode = true,
        };
        using var harness = new Harness(Interactive, appLauncher: launcher);

        var package = await harness.Channel.GetRegisteredPackageAsync(
            "Contoso.MyApp",
            "CN=Contoso",
            "Contoso.MyApp_fakefamily",
            harness.Token);

        Assert.IsNotNull(package);
        Assert.AreEqual(launcher.FakePackageFullName, package.FullName);
        Assert.AreEqual(FakeLayout, package.RegisteredLocation);
        Assert.IsTrue(package.IsDevelopmentMode);
    }

    [TestMethod]
    public async Task QueryPackage_WhenNothingIsRegistered_ReturnsNull()
    {
        var launcher = new FakeAppLauncherService { FakePackageFullName = null };
        using var harness = new Harness(Interactive, appLauncher: launcher);

        var package = await harness.Channel.GetRegisteredPackageAsync(
            "Contoso.MyApp",
            "CN=Contoso",
            "Contoso.MyApp_fakefamily",
            harness.Token);

        Assert.IsNull(package);
    }

    [TestMethod]
    public async Task QueryPackage_PreservesNonDevelopmentStatus()
    {
        var launcher = new FakeAppLauncherService { FakeIsDevelopmentMode = false };
        using var harness = new Harness(Interactive, appLauncher: launcher);

        var package = await harness.Channel.GetRegisteredPackageAsync(
            "Contoso.MyApp",
            "CN=Contoso",
            "Contoso.MyApp_fakefamily",
            harness.Token);

        Assert.IsNotNull(package);
        Assert.IsFalse(package.IsDevelopmentMode);
    }

    [TestMethod]
    public async Task QueryPackage_WhenInventoryFails_ReportsStructuredFailure()
    {
        var launcher = new FakeAppLauncherService
        {
            GetRegisteredPackageFailure = new InvalidOperationException("inventory unavailable"),
        };
        using var harness = new Harness(Interactive, appLauncher: launcher);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            harness.Channel.GetRegisteredPackageAsync(
                "Contoso.MyApp",
                "CN=Contoso",
                "Contoso.MyApp_fakefamily",
                harness.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.StaleHandle, failure.Error.Code);
    }

    [TestMethod]
    public async Task StopPackage_WhenTerminationCannotBeProven_FailsWithGuidanceNamingThePackage()
    {
        var launcher = new FakeAppLauncherService
        {
            FakePackageFullName = "Contoso.MyApp_1.0.0.0_x64__abc",
            FakeRegisteredLocation = FakeLayout,
            StopPackageProcessesFailure = new InvalidOperationException("still running"),
        };
        using var harness = new Harness(Interactive, appLauncher: launcher);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => harness.Channel.StopPackageProcessesAsync("Contoso.MyApp_abc", FakeLayout, harness.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.StaleHandle, failure.Error.Code);
        StringAssert.Contains(failure.Error.Message, "Contoso.MyApp_abc");
    }

    [TestMethod]
    public async Task StopPackage_WithoutAConfiguredLauncher_FailsRatherThanSilentlyDoingNothing()
    {
        using var harness = new Harness(Interactive);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => harness.Channel.StopPackageProcessesAsync("Contoso.MyApp_abc", FakeLayout, harness.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.TransportFailed, failure.Error.Code);
    }

    /// <summary>
    /// Two deployments built from different source paths can share a package identity. Only one
    /// of them can be genuinely registered at a time, so a family-name match alone must never be
    /// enough to terminate: the currently registered install location has to match too.
    /// </summary>
    [TestMethod]
    public async Task StopPackage_WhenTheCurrentRegistrationIsADifferentDeploymentsLayout_RefusesRatherThanStoppingIt()
    {
        var launcher = new FakeAppLauncherService
        {
            FakePackageFullName = "Contoso.MyApp_1.0.0.0_x64__abc",
            FakeRegisteredLocation = @"C:\WinApp\deployments\dep-A-layout",
        };
        using var harness = new Harness(Interactive, appLauncher: launcher);

        // This request believes it owns the registration from dep-B's layout, but the guest's
        // actual live registration is dep-A's.
        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            harness.Channel.StopPackageProcessesAsync(
                "Contoso.MyApp_abc", @"C:\WinApp\deployments\dep-B-layout", harness.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.StaleHandle, failure.Error.Code);
        StringAssert.Contains(failure.Error.Message, "not the one this deployment registered");
        Assert.AreEqual(0, launcher.StopPackageCalls.Count, "dep-A's legitimate registration must never be terminated.");
    }

    /// <summary>
    /// The location check must canonicalize rather than do a literal string compare: a trailing
    /// separator or a case difference must not itself cause a false mismatch (NTFS is
    /// case-insensitive), and neither may it be fooled into a false match by a genuinely different
    /// path that happens to share a prefix.
    /// </summary>
    [TestMethod]
    public async Task StopPackage_TheLocationCheckIsCaseInsensitiveAndTrailingSeparatorInsensitive()
    {
        var launcher = new FakeAppLauncherService
        {
            FakePackageFullName = "Contoso.MyApp_1.0.0.0_x64__abc",
            FakeRegisteredLocation = @"C:\WinApp\deployments\Dep-1-Layout\",
        };
        using var harness = new Harness(Interactive, appLauncher: launcher);

        await harness.Channel.StopPackageProcessesAsync(
            "Contoso.MyApp_abc", @"c:\winapp\deployments\dep-1-layout", harness.Token);

        CollectionAssert.AreEqual(ExpectedStoppedFullNames, launcher.StopPackageCalls);
    }

    [TestMethod]
    public async Task StopPackage_APathThatMerelySharesAPrefixIsNotTreatedAsAMatch()
    {
        var launcher = new FakeAppLauncherService
        {
            FakePackageFullName = "Contoso.MyApp_1.0.0.0_x64__abc",
            FakeRegisteredLocation = @"C:\WinApp\deployments\dep-1-layout",
        };
        using var harness = new Harness(Interactive, appLauncher: launcher);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            harness.Channel.StopPackageProcessesAsync(
                "Contoso.MyApp_abc", @"C:\WinApp\deployments\dep-1-layout-2", harness.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.StaleHandle, failure.Error.Code);
        Assert.AreEqual(0, launcher.StopPackageCalls.Count);
    }

    /// <summary>
    /// A package stays registered even after the files it was registered from are gone (an
    /// interrupted <c>--clean</c> deletes the layout's files, not the registration). The recorded
    /// install location the guest compares against is never required to exist on disk, so a stop
    /// (and the redeploy behind it) must succeed here exactly as it would if the folder were intact.
    /// </summary>
    [TestMethod]
    public async Task StopPackage_WhenTheRegisteredLocationNoLongerExistsOnDisk_StillMatchesAndStops()
    {
        var deletedLayout = Path.Join(Path.GetTempPath(), $"winapp-test-deleted-layout-{Guid.NewGuid():n}");
        Assert.IsFalse(Directory.Exists(deletedLayout), "Precondition: the simulated layout must not exist.");

        var launcher = new FakeAppLauncherService
        {
            FakePackageFullName = "Contoso.MyApp_1.0.0.0_x64__abc",
            FakeRegisteredLocation = deletedLayout,
        };
        using var harness = new Harness(Interactive, appLauncher: launcher);

        // Must not throw, even though nothing exists at this path.
        await harness.Channel.StopPackageProcessesAsync("Contoso.MyApp_abc", deletedLayout, harness.Token);

        CollectionAssert.AreEqual(ExpectedStoppedFullNames, launcher.StopPackageCalls);
    }

    /// <summary>
    /// A location the inventory could not report at all (the real API surfacing an empty value) is
    /// proof failure, not proof of absence, and must fail exactly like a genuine mismatch rather
    /// than being read as "nothing to compare, so proceed".
    /// </summary>
    [TestMethod]
    public async Task StopPackage_WhenTheInventoryCannotReportALocation_FailsClosedRatherThanProceeding()
    {
        var launcher = new FakeAppLauncherService
        {
            FakePackageFullName = "Contoso.MyApp_1.0.0.0_x64__abc",
            FakeRegisteredLocation = null,
        };
        using var harness = new Harness(Interactive, appLauncher: launcher);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            harness.Channel.StopPackageProcessesAsync(
                "Contoso.MyApp_abc", @"C:\WinApp\deployments\dep-1-layout", harness.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.StaleHandle, failure.Error.Code);
        Assert.AreEqual(0, launcher.StopPackageCalls.Count);
    }

    [TestMethod]
    public async Task StopProcess_WithAMatchingPidAndStartTime_Stops()
    {
        using var harness = new Harness(Interactive);

        harness.Server.StopTrackedProcessImpl = (pid, ticks) =>
        {
            Assert.AreEqual(4242, pid);
            Assert.AreEqual(555L, ticks);
            return GuestCommandServer.ProcessStopOutcome.Stopped;
        };

        // Must not throw.
        await harness.Channel.StopTrackedProcessAsync(4242, 555L, harness.Token);
    }

    [TestMethod]
    public async Task StopProcess_ThatIsAlreadyGone_SucceedsWithoutFailing()
    {
        using var harness = new Harness(Interactive);
        harness.Server.StopTrackedProcessImpl = (_, _) => GuestCommandServer.ProcessStopOutcome.AlreadyGone;

        await harness.Channel.StopTrackedProcessAsync(4242, 555L, harness.Token);
    }

    [TestMethod]
    public async Task StopProcess_WhenItCannotBeProvenStopped_FailsWithGuidanceNamingThePid()
    {
        using var harness = new Harness(Interactive);
        harness.Server.StopTrackedProcessImpl = (_, _) => GuestCommandServer.ProcessStopOutcome.Unproven;

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => harness.Channel.StopTrackedProcessAsync(4242, 555L, harness.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.StaleHandle, failure.Error.Code);
        StringAssert.Contains(failure.Error.Message, "4242");
    }

    /// <summary>
    /// Killing a real process tree can itself throw -- <c>Process.Kill(entireProcessTree: true)</c>
    /// aggregates a partial per-process failure into an <see cref="AggregateException"/> -- and
    /// <see cref="GuestCommandServer.StopTrackedProcessImpl"/> is a replaceable delegate a test (or
    /// a future implementation) can make throw anything. Whatever escapes it must become the same
    /// structured, fail-closed response an <see cref="GuestCommandServer.ProcessStopOutcome.Unproven"/>
    /// outcome produces, and must never propagate out of the request into the dispatch loop serving
    /// every other operation on this connection.
    /// </summary>
    [TestMethod]
    public async Task StopProcess_WhenTheStopDelegateThrows_FailsWithStructuredResponseRatherThanCrashingTheAgent()
    {
        using var harness = new Harness(Interactive);

        harness.Server.StopTrackedProcessImpl = (_, _) => throw new AggregateException(
            "Kill failed for some processes.", new InvalidOperationException("access denied"));

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => harness.Channel.StopTrackedProcessAsync(4242, 555L, harness.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.StaleHandle, failure.Error.Code);
        StringAssert.Contains(failure.Error.Message, "4242");

        // The connection survives: a second, unrelated operation on the same channel still
        // completes normally rather than the whole server having gone down with the first.
        var execution = harness.Channel.ExecuteAsync(Request("run", "."), callbacks: null, harness.Token);
        var process = await harness.Processes.WaitForNextAsync(harness.Token);
        process.Exit(0);

        Assert.AreEqual(0, (await execution).ExitCode);
    }

    /// <summary>
    /// Same guarantee at the default implementation itself, using a real process: an
    /// <see cref="AggregateException"/> raised by the kill call (simulated here since a real
    /// process can be made to throw one only under specific, unreliable conditions) must be caught
    /// and converted to <see cref="GuestCommandServer.ProcessStopOutcome.Unproven"/>, matching the
    /// existing narrower catch it replaces.
    /// </summary>
    [TestMethod]
    public void DefaultStopTrackedProcess_StartTimeReadThrowsAnUnrecognisedExceptionType_ReportsAlreadyGoneRatherThanThrowing()
    {
        using var helper = StartHelperProcess();
        helper.Kill(entireProcessTree: true);
        helper.WaitForExit(5000);

        // The process has already exited by the time its start time is read: on some runtimes this
        // surfaces as InvalidOperationException, on others as Win32Exception. Either way the method
        // itself must never throw.
        var outcome = GuestCommandServer.DefaultStopTrackedProcess(helper.Id, expectedStartTicksUtc: 0);

        Assert.AreEqual(GuestCommandServer.ProcessStopOutcome.AlreadyGone, outcome);
    }

    /// <summary>
    /// A stale or recycled PID must never be killed: a mismatched start time proves the tracked
    /// process is not the one currently holding that PID, however that came to be.
    /// </summary>
    [TestMethod]
    public void DefaultStopTrackedProcess_ARealProcessWithADifferentStartTime_IsNeverTouched()
    {
        using var helper = StartHelperProcess();
        var wrongStartTicksUtc = helper.StartTime.ToUniversalTime().Ticks - TimeSpan.FromDays(1).Ticks;

        var outcome = GuestCommandServer.DefaultStopTrackedProcess(helper.Id, wrongStartTicksUtc);

        Assert.AreEqual(GuestCommandServer.ProcessStopOutcome.AlreadyGone, outcome);
        Assert.IsFalse(helper.HasExited, "A process must never be touched when its start time does not match.");
    }

    [TestMethod]
    public void DefaultStopTrackedProcess_ARealProcessWithAMatchingStartTime_IsStopped()
    {
        using var helper = StartHelperProcess();
        var expectedStartTicksUtc = helper.StartTime.ToUniversalTime().Ticks;

        var outcome = GuestCommandServer.DefaultStopTrackedProcess(helper.Id, expectedStartTicksUtc);

        Assert.AreEqual(GuestCommandServer.ProcessStopOutcome.Stopped, outcome);
        Assert.IsTrue(helper.WaitForExit(5000));
    }

    [TestMethod]
    public void DefaultStopTrackedProcess_WhenNothingHasThatPid_ReportsAlreadyGone()
    {
        // No real process is expected to ever hold this PID during a test run.
        var outcome = GuestCommandServer.DefaultStopTrackedProcess(int.MaxValue - 5, expectedStartTicksUtc: 0);

        Assert.AreEqual(GuestCommandServer.ProcessStopOutcome.AlreadyGone, outcome);
    }

    private static System.Diagnostics.Process StartHelperProcess() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 60\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The expected condition was not reached in time.");
            }

            await Task.Delay(10, cancellationToken);
        }
    }

    /// <summary>A connected host channel and guest server sharing one in-memory transport.</summary>
    internal sealed class Harness : IAsyncDisposable, IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(30));
        private readonly GuestCommandServer _server;
        private readonly Task _serverTask;

        public Harness(
            GuestSessionInfo session,
            ExecutionTargetEpoch? hostEpoch = null,
            IAppLauncherService? appLauncher = null,
            IPackageRegistrationService? packageRegistration = null,
            Func<ReadOnlyMemory<byte>, CancellationToken, Task>? beforeGuestSend = null,
            GuestFileService? files = null,
            string? guestWinapp = null,
            GuestAgentIdentity? agentIdentity = null)
        {
            var pair = new LoopbackTransportPair(beforeGuestSend);
            Processes = new FakeGuestProcessHostFactory();

            _server = new GuestCommandServer(
                pair.Guest,
                Epoch,
                Processes,
                new StaticGuestSessionProbe(session),
                agentIdentity ?? Identity,
                files,
                guestWinapp,
                appLauncher,
                packageRegistration);

            _serverTask = _server.RunAsync(_cancellation.Token);

            Channel = new GuestCommandChannel(pair.Host, hostEpoch ?? Epoch);
            Channel.Start();
        }

        public FakeGuestProcessHostFactory Processes { get; }

        public GuestCommandChannel Channel { get; }

        public GuestCommandServer Server => _server;

        public CancellationToken Token => _cancellation.Token;

        public async Task StopServerAsync()
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
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            _cancellation.Dispose();

            // The server owns the guest transport and any process hosts still running, so it must
            // be disposed rather than merely cancelled.
            _server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync();
            _cancellation.Dispose();
            await _server.DisposeAsync();
        }
    }
}
