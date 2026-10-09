// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services.DevTools.Comments;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class GuestConsoleLifetimeTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "guest-lifetime", "WinApp.Cli.LifetimeFixture.exe");
    private static string EventName() => $@"Local\winapp-lifetime-test-{Guid.NewGuid():N}";

    private static Process ObserveApp(GuestProcessStart identity)
    {
        var app = Process.GetProcessById(identity.ProcessId);
        try
        {
            // Retain the process object before releasing the fixture's exit barrier.
            _ = app.SafeHandle;
            Assert.AreEqual(identity.StartTicksUtc, app.StartTime.ToUniversalTime().Ticks);
            return app;
        }
        catch
        {
            app.Dispose();
            throw;
        }
    }

    [TestMethod]
    public async Task RootOnlyTermination_PreservesTheLaunchedAppsChild()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var rootName = EventName();
        var childName = EventName();
        using var rootClose = new EventWaitHandle(false, EventResetMode.ManualReset, rootName);
        using var childClose = new EventWaitHandle(false, EventResetMode.ManualReset, childName);
        using var started = Process.Start(new ProcessStartInfo(Fixture)
        {
            ArgumentList = { "tree", rootName, childName },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        Process? child = null;
        try
        {
            var line = await started.StandardOutput.ReadLineAsync(timeout.Token);
            using var tree = JsonDocument.Parse(line!);
            child = ObserveApp(tree.RootElement.GetProperty("Child").Deserialize<GuestProcessStart>()!);
            using var root = new LaunchedProcess(started);
            var identity = GuestInspectedAppLifetime.CaptureIdentity(root);
            Assert.IsTrue(await GuestInspectedAppLifetime.CloseOwnedAsync(root, identity));
            Assert.IsTrue(root.HasExited);
            Assert.IsFalse(child.HasExited, "Root-only fallback must not terminate descendants.");
        }
        finally
        {
            rootClose.Set();
            childClose.Set();
            if (child is not null)
            {
                await child.WaitForExitAsync(timeout.Token);
                Assert.AreEqual(7, child.ExitCode, "The child must exit through its own normal close event.");
                child.Dispose();
            }
        }
    }

    [TestMethod]
    [DataRow("cancel")]
    [DataRow("helper-exit")]
    [DataRow("helper-failure")]
    [DataRow("helper-death")]
    public async Task OperationJob_ContainsDescendantsThroughBarrierAndLeavesOtherProcessAlive(string outcome)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var rootName = EventName();
        var childName = EventName();
        var otherName = EventName();
        using var rootClose = new EventWaitHandle(false, EventResetMode.ManualReset, rootName);
        using var childClose = new EventWaitHandle(false, EventResetMode.ManualReset, childName);
        using var otherClose = new EventWaitHandle(false, EventResetMode.ManualReset, otherName);
        using var other = Process.Start(new ProcessStartInfo(Fixture)
        {
            ArgumentList = { "app", otherName },
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var childReady = new TaskCompletionSource<(GuestProcessStart Root, GuestProcessStart Child)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stdout = new System.Text.StringBuilder();
        var exitCode = outcome == "helper-failure" ? 31 : 7;
        await using var host = GuestProcessHost.Start(new GuestExecRequest
        {
            Executable = Fixture,
            Arguments = ["tree", rootName, childName, exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)],
        }, (stream, data) =>
        {
            if (stream == GuestStreamId.StandardOutput)
            {
                stdout.Append(System.Text.Encoding.UTF8.GetString(data.Span));
                if (stdout.ToString().Contains('\n'))
                {
                    using var tree = JsonDocument.Parse(stdout.ToString());
                    childReady.TrySetResult((tree.RootElement.GetProperty("Root").Deserialize<GuestProcessStart>()!,
                        tree.RootElement.GetProperty("Child").Deserialize<GuestProcessStart>()!));
                }
            }
            return Task.CompletedTask;
        });
        try
        {
            var identities = await childReady.Task.WaitAsync(timeout.Token);
            using var command = ObserveApp(identities.Root);
            using var child = ObserveApp(identities.Child);
            if (outcome == "cancel")
            {
                Assert.AreNotEqual(0, await host.StopAsync(TimeSpan.FromMilliseconds(50), timeout.Token));
            }
            else
            {
                if (outcome == "helper-death") { command.Kill(entireProcessTree: false); }
                else { rootClose.Set(); }
                host.CloseStandardInput();
                await command.WaitForExitAsync(timeout.Token);
                if (outcome == "helper-death") { Assert.AreNotEqual(7, command.ExitCode); }
                else { Assert.AreEqual(exitCode, command.ExitCode); }
                Assert.IsFalse(child.HasExited, "Helper exit alone does not terminate its descendant.");
            }
            await host.DisposeAsync();
            await child.WaitForExitAsync(timeout.Token);
            Assert.AreNotEqual(7, child.ExitCode, "Operation teardown, not the child's close event, terminated it.");
            Assert.IsFalse(other.HasExited, "The independent owned control process must survive operation teardown.");
        }
        finally
        {
            rootClose.Set();
            childClose.Set();
            otherClose.Set();
            await other.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(7, other.ExitCode);
        }
    }

    [TestMethod]
    public async Task ObservedApp_RetainsActualExitCodeAfterLaunchingOwnerReleasesProcess()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var name = EventName();
        using var close = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        using var started = Process.Start(new ProcessStartInfo(Fixture)
        {
            ArgumentList = { "app", name },
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            using var app = ObserveApp(new(started.Id, started.StartTime.ToUniversalTime().Ticks));
            close.Set();
            await started.WaitForExitAsync(timeout.Token);
            started.Dispose();
            await app.WaitForExitAsync(timeout.Token);
            Assert.AreEqual(7, app.ExitCode);
        }
        finally
        {
            close.Set();
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    [DataRow("app-exit")]
    [DataRow("exit-control-race")]
    [DataRow("stop")]
    [DataRow("eof")]
    [DataRow("cancel")]
    public async Task RealConsoleInput_DrainsWithoutLosingActualExitOrFailure(string outcome)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var name = EventName();
        using var close = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        var resumeName = EventName();
        var observedName = EventName();
        using var resume = new EventWaitHandle(false, EventResetMode.ManualReset, resumeName);
        using var observed = new EventWaitHandle(false, EventResetMode.ManualReset, observedName);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var harness = new RealChannel();
        Guid operation = default;
        var ready = new TaskCompletionSource<GuestProcessStart>(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = new GuestCommentFrames.Reader(bytes =>
        {
            var frame = JsonSerializer.Deserialize(bytes, GuestCommentsJsonContext.Default.GuestInspectedAppFrame)!;
            if (frame.Phase == "ready")
            {
                ready.TrySetResult(frame.Process!);
            }
        });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arguments = new List<string> { "launch", name };
        if (outcome == "exit-control-race")
        {
            arguments.AddRange([resumeName, observedName]);
        }
        var execution = harness.Channel.ExecuteAsync(new GuestExecRequest { Executable = Fixture, Arguments = arguments },
            new GuestExecCallbacks(OnOperationId: id => operation = id, OnStarted: _ => started.TrySetResult(),
                OnStandardOutput: bytes => frames.Append(bytes.Span)), cancellation.Token);
        await started.Task.WaitAsync(timeout.Token);
        await harness.Channel.SendStandardInputAsync(operation,
            GuestCommentFrames.Encode(new GuestInspectedAppControl("owner-ready"), GuestCommentsJsonContext.Default.GuestInspectedAppControl),
            timeout.Token);
        var identity = await ready.Task.WaitAsync(timeout.Token);
        using var app = ObserveApp(identity);
        var normal = outcome is "app-exit" or "exit-control-race";
        if (normal)
        {
            close.Set();
            await app.WaitForExitAsync(timeout.Token);
            Assert.AreEqual(7, app.ExitCode);
            Assert.AreNotEqual(execution, await Task.WhenAny(execution, Task.Delay(200, timeout.Token)),
                "Actual Console stdin is still open: cancellation alone does not complete the already-running read.");
        }
        else
        {
            Assert.IsFalse(app.HasExited);
        }
        if (outcome == "cancel")
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => execution.WaitAsync(timeout.Token));
            await app.WaitForExitAsync(timeout.Token);
            return;
        }
        if (outcome == "eof")
        {
            await harness.Channel.CloseStandardInputAsync(operation, timeout.Token);
        }
        else
        {
            await harness.Channel.SendStandardInputAsync(operation,
                GuestCommentFrames.Encode(new GuestInspectedAppControl("stop"), GuestCommentsJsonContext.Default.GuestInspectedAppControl),
                timeout.Token);
        }
        if (outcome == "exit-control-race")
        {
            await Task.Run(() => observed.WaitOne(), timeout.Token);
            resume.Set();
        }
        Assert.AreEqual(normal ? 7 : outcome == "eof" ? 98 : 99, (await execution.WaitAsync(timeout.Token)).ExitCode);
        await app.WaitForExitAsync(timeout.Token);
    }

    [TestMethod]
    [TestCategory("E2E")]
    [DataRow("normal")]
    [DataRow("relay-zero")]
    [DataRow("relay-error")]
    public async Task Session_OnlyExactAppExitCompletesNormallyAndLeavesOtherJobAlive(string outcome)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var name = EventName();
        var otherName = EventName();
        using var close = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        using var otherClose = new EventWaitHandle(false, EventResetMode.ManualReset, otherName);
        var relayName = EventName();
        using var relayClose = new EventWaitHandle(false, EventResetMode.ManualReset, relayName);
        var root = Directory.CreateTempSubdirectory("winapp-console-session-");
        File.WriteAllText(Path.Combine(root.FullName, "App.csproj"), "<Project/>");
        File.WriteAllText(Path.Combine(root.FullName, "Main.xaml"), "<Grid/>");
        try
        {
            await using var harness = new RealChannel(outcome == "normal" ? null : relayName, outcome == "relay-error" ? 12 : 0);
            var otherStarted = new TaskCompletionSource<GuestProcessStart>(TaskCreationOptions.RunContinuationsAsynchronously);
            var other = harness.Channel.ExecuteAsync(new GuestExecRequest { Executable = Fixture, Arguments = ["app", otherName] },
                new GuestExecCallbacks(OnStarted: value => otherStarted.TrySetResult(value)), timeout.Token);
            _ = await otherStarted.Task.WaitAsync(timeout.Token);
            try
            {
                var target = new PreparedTarget(new("sandbox", "console-fixture"), harness.Channel, harness.Epoch,
                    await harness.Channel.GetCapabilitiesAsync(timeout.Token), true);
                var states = new DeploymentStateStore(new TargetStateDirectoryProvider(Path.Combine(root.FullName, "state")));
                var deployment = states.Commit(target.Reference, new DeploymentState
                {
                    SchemaVersion = DeploymentStateStore.CurrentSchemaVersion,
                    Revision = 0,
                    DeploymentId = "console-app",
                    TargetEpoch = harness.Epoch.Value,
                    Dirty = false,
                }, 0);
                var session = new GuestDevToolsSession(target,
                    new(Path.Combine(root.FullName, "App.csproj"), [new("Main.xaml", 7, new string('A', 64))]),
                    new CommentStore(), new GuestApplicationRunner(new TargetDeploymentService(states)), deployment);
                var ready = new TaskCompletionSource<GuestProcessStart>(TaskCreationOptions.RunContinuationsAsynchronously);
                var running = session.RunAsync(new GuestExecRequest { Executable = Fixture, Arguments = ["launch", name] },
                    (frame, _) =>
                    {
                        ready.TrySetResult(frame.Process!);
                        return Task.CompletedTask;
                    }, _ => { }, timeout.Token);
                var identity = await ready.Task.WaitAsync(timeout.Token);
                using var app = ObserveApp(identity);
                if (outcome == "normal")
                {
                    close.Set();
                    await app.WaitForExitAsync(timeout.Token);
                    Assert.AreEqual(7, await running.WaitAsync(timeout.Token));
                }
                else
                {
                    Assert.IsFalse(app.HasExited, "Relay EOF/error must occur while the actual application is still alive.");
                    relayClose.Set();
                    var error = await Assert.ThrowsAsync<IOException>(() => running.WaitAsync(timeout.Token));
                    StringAssert.Contains(error.Message, "relay disconnected");
                    await app.WaitForExitAsync(timeout.Token);
                }
                Assert.IsFalse(other.IsCompleted, "Normal-close cleanup must not terminate another operation's job.");
            }
            finally
            {
                otherClose.Set();
                Assert.AreEqual(7, (await other.WaitAsync(timeout.Token)).ExitCode);
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private sealed class RealChannel : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly GuestCommandServer _server;
        private readonly Task _serving;
        internal ExecutionTargetEpoch Epoch { get; } = ExecutionTargetEpoch.Create("console", Guid.NewGuid().ToString("N"));
        internal GuestCommandChannel Channel { get; }

        internal RealChannel(string? relayEvent = null, int relayExit = 0)
        {
            Assert.IsTrue(File.Exists(Fixture), "The test build must copy the headless lifetime fixture.");
            var pair = new LoopbackTransportPair();
            _server = new GuestCommandServer(pair.Guest, Epoch,
                new FixtureProcesses(relayEvent, relayExit),
                new StaticGuestSessionProbe(new GuestSessionInfo(1, "WinSta0", true)),
                new GuestAgentIdentity("test", "test", System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
                    GuestProtocol.MinimumVersion, GuestProtocol.CurrentVersion),
                guestWinapp: Fixture);
            _serving = _server.RunAsync(_stop.Token);
            Channel = new GuestCommandChannel(pair.Host, Epoch);
            Channel.Start();
        }

        public async ValueTask DisposeAsync()
        {
            await Channel.DisposeAsync();
            await _stop.CancelAsync();
            await _server.DisposeAsync();
            try { await _serving; }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            _stop.Dispose();
        }

        private sealed class FixtureProcesses(string? relayEvent, int relayExit) : IGuestProcessHostFactory
        {
            public IGuestProcessHost Start(GuestExecRequest request, Func<GuestStreamId, ReadOnlyMemory<byte>, Task> onOutput)
            {
                if (relayEvent is not null && request.Arguments[0] == "guest-comment-relay")
                {
                    request = new GuestExecRequest
                    {
                        Executable = Fixture,
                        Arguments = ["relay-exit", relayEvent, relayExit.ToString(System.Globalization.CultureInfo.InvariantCulture), .. request.Arguments],
                    };
                }
                return GuestProcessHost.Start(request, onOutput);
            }
        }
    }
}
