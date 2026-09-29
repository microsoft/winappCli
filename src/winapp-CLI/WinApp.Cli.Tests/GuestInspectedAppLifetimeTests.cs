// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Commands;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

[TestClass]
public class GuestInspectedAppLifetimeTests
{
    [TestMethod]
    [DoNotParallelize]
    [DataRow(false, false, false)]
    [DataRow(false, false, true)]
    [DataRow(false, true, false)]
    [DataRow(true, false, false)]
    [DataRow(true, false, true)]
    public async Task ProgramEntry_KeepsAmbientDiagnosticsOutOfActualHelperFrames(bool relay, bool failInjection, bool startupNoise)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pair = DuplexStreamPair.Create();
        using var host = pair.Client;
        using var guest = pair.Server;
        using var app = new ControlledApp();
        using var stdout = new StreamWriter(guest, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;
        var previousTelemetry = Environment.GetEnvironmentVariable(Telemetry.Telemetry.OptOutEnvironmentVariable);
        var notices = new StartupNotices(startupNoise);
        var phases = new List<string>();
        var relayAlive = true;
        var reader = new GuestCommentFrames.Reader(bytes =>
        {
            if (relay)
            {
                var envelope = JsonSerializer.Deserialize(bytes, GuestCommentsJsonContext.Default.GuestCommentEnvelope)!;
                Assert.AreEqual(new GuestProcessStart(123, 456), envelope.Request.Process);
                phases.Add(envelope.Kind);
                Volatile.Write(ref relayAlive, false);
                return;
            }
            var frame = JsonSerializer.Deserialize(bytes, GuestCommentsJsonContext.Default.GuestInspectedAppFrame)!;
            Assert.AreEqual(new GuestProcessStart(123, 456), frame.Process);
            phases.Add(frame.Phase);
            if (frame.Phase == "started")
            {
                host.Write(GuestCommentFrames.Encode(new GuestInspectedAppControl("owner-ready"),
                    GuestCommentsJsonContext.Default.GuestInspectedAppControl));
            }
            else if (frame.Phase == "failed")
            {
                Assert.IsTrue(failInjection);
                StringAssert.Contains(frame.Error!, "controlled injection failure");
            }
            else
            {
                Assert.AreEqual("ready", frame.Phase);
                Assert.AreEqual(12, frame.NodeCount);
                Assert.IsFalse(app.HasExited);
                app.Exit(7);
            }
        });
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            Environment.SetEnvironmentVariable(Telemetry.Telemetry.OptOutEnvironmentVariable, "1");
            var receiving = ReceiveAsync();
            var running = InvokeAsync();
            await Task.WhenAll(receiving, running);
            Assert.AreEqual(relay ? 0 : failInjection ? 1 : 7, await running);
            string[] expectedPhases = relay ? ["ready"] : failInjection ? ["started", "failed"] : ["started", "ready"];
            CollectionAssert.AreEqual(expectedPhases, phases);
            Assert.AreEqual(0, notices.FirstRunCalls);
            Assert.AreEqual(0, notices.UpdateCalls);
            StringAssert.Contains(stderr.ToString(), relay ? "relay diagnostic" : failInjection
                ? "controlled injection failure" : "DevTools injection succeeded on attempt 2");
            StringAssert.Contains(stderr.ToString(), "warning diagnostic");
            StringAssert.Contains(stderr.ToString(), "console diagnostic");
            StringAssert.Contains(stderr.ToString(), "ansi diagnostic");
            if (!relay)
            {
                Assert.AreEqual(failInjection, app.Killed);
            }
        }
        finally
        {
            await cancellation.CancelAsync();
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable(Telemetry.Telemetry.OptOutEnvironmentVariable, previousTelemetry);
        }

        async Task ReceiveAsync()
        {
            try
            {
                var bytes = new byte[4096];
                int count;
                while ((count = await host.ReadAsync(bytes, cancellation.Token)) != 0)
                {
                    reader.Append(bytes.AsSpan(0, count));
                }
            }
            catch
            {
                await cancellation.CancelAsync();
                throw;
            }
        }

        async Task<int> InvokeAsync()
        {
            try
            {
                return await Program.RunAsync(relay
                    ? ["guest-comment-relay", "--binding", Guid.NewGuid().ToString("N"), "--target", "sandbox",
                        "--epoch", "epoch", "--pid", "123", "--start", "456"]
                    : ["guest-devtools-launch", "--source-root", Path.GetTempPath(), "--source-hash", new string('A', 64)],
                    services =>
                    {
                        services.AddSingleton<IFirstRunService>(notices).AddSingleton<IUpdateNotificationService>(notices);
                        if (relay)
                        {
                            services.AddSingleton(sp =>
                            {
                                var command = new GuestCommentRelayCommand();
                                command.SetAction(async (_, _) =>
                                {
                                    var logger = sp.GetRequiredService<ILogger<GuestCommentRelayCommand>>();
                                    logger.LogInformation("relay diagnostic");
                                    logger.LogWarning("warning diagnostic");
                                    Console.WriteLine("console diagnostic");
                                    sp.GetRequiredService<IAnsiConsole>().WriteLine("ansi diagnostic");
                                    var session = new GuestCommentSession(Guid.NewGuid().ToString("N"), "sandbox", "epoch", new(123, 456));
                                    await new GuestCommentEndpoint(session).RunAsync(guest, guest,
                                        message => logger.LogError("{Message}", message), cancellation.Token,
                                        () => Volatile.Read(ref relayAlive));
                                    return 0;
                                });
                                return command;
                            });
                        }
                        else
                        {
                            services.AddSingleton(sp =>
                            {
                                var command = new GuestDevToolsLaunchCommand();
                                command.SetAction((_, _) => GuestInspectedAppLifetime.RunAsync(app, new(123, 456),
                                    new LoggingInspectionService(sp.GetRequiredService<ILogger<DevToolsService>>(),
                                        sp.GetRequiredService<IAnsiConsole>(), failInjection),
                                    guest, guest, true, cancellation.Token));
                                return command;
                            });
                        }
                    });
            }
            finally
            {
                guest.Dispose();
            }
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Ready_WaitsForHostOwnerAndInjection_KeepsAppAliveUntilActualExit(bool overlay)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pair = DuplexStreamPair.Create();
        using var host = pair.Client;
        using var guest = pair.Server;
        using var app = new ControlledApp();
        var service = new InspectionService();
        var commentsPrepared = false;
        var lifetime = GuestInspectedAppLifetime.RunAsync(app, new(123, 456), service, guest, guest, overlay, cancellation.Token,
            prepareComments: () =>
            {
                Assert.IsFalse(service.Called);
                commentsPrepared = true;
            });
        var started = await ReadAsync(host, cancellation.Token);
        Assert.AreEqual("started", started.Phase);
        Assert.AreEqual(new GuestProcessStart(123, 456), started.Process);
        Assert.IsFalse(service.Called);
        Assert.IsFalse(commentsPrepared);
        Assert.IsFalse(app.Killed);
        await GuestCommentFrames.WriteAsync(host, new GuestInspectedAppControl("owner-ready"),
            GuestCommentsJsonContext.Default.GuestInspectedAppControl, cancellation.Token);
        var ready = await ReadAsync(host, cancellation.Token);
        Assert.AreEqual("ready", ready.Phase);
        Assert.AreEqual(123u, service.ProcessId);
        Assert.AreEqual(overlay, ready.OverlayShown);
        Assert.IsTrue(commentsPrepared);
        Assert.IsFalse(lifetime.IsCompleted, "Startup JSON must not release the job that owns the app.");
        app.Exit(7);
        Assert.AreEqual(7, await lifetime);
        Assert.IsFalse(app.Killed);
    }

    [TestMethod]
    public async Task CommentOwnerRefusal_PreventsInjectionAndStopsOwnedLaunch()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pair = DuplexStreamPair.Create();
        using var host = pair.Client;
        using var guest = pair.Server;
        using var app = new ControlledApp();
        var service = new InspectionService();
        var lifetime = GuestInspectedAppLifetime.RunAsync(app, new(123, 456), service, guest, guest, true, cancellation.Token,
            prepareComments: () => throw new IOException("host snapshot refused"));
        _ = await ReadAsync(host, cancellation.Token);
        await GuestCommentFrames.WriteAsync(host, new GuestInspectedAppControl("owner-ready"),
            GuestCommentsJsonContext.Default.GuestInspectedAppControl, cancellation.Token);
        var error = await Assert.ThrowsAsync<IOException>(() => lifetime);
        StringAssert.Contains(error.Message, "host snapshot refused");
        Assert.IsFalse(service.Called);
        Assert.IsTrue(app.Killed);
        var failure = await ReadAsync(host, cancellation.Token);
        StringAssert.Contains(failure.Error!, "comment binding");
        Assert.DoesNotContain("host snapshot refused", failure.Error!, "Arbitrary exception text is not copied into structured public failure.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HostDisconnection_KillsOnlyTheOwnedLaunch(bool afterReady)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pair = DuplexStreamPair.Create();
        using var host = pair.Client;
        using var guest = pair.Server;
        using var app = new ControlledApp();
        var lifetime = GuestInspectedAppLifetime.RunAsync(app, new(123, 456), new InspectionService(),
            guest, guest, true, cancellation.Token);
        _ = await ReadAsync(host, cancellation.Token);
        if (afterReady)
        {
            await GuestCommentFrames.WriteAsync(host, new GuestInspectedAppControl("owner-ready"),
                GuestCommentsJsonContext.Default.GuestInspectedAppControl, cancellation.Token);
            _ = await ReadAsync(host, cancellation.Token);
        }
        host.Dispose();
        await Assert.ThrowsAsync<IOException>(() => lifetime);
        Assert.IsTrue(app.Killed);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StartupFailure_DoesNotPublishReadyAndStopsOwnedApp(bool overlayFailure)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pair = DuplexStreamPair.Create();
        using var host = pair.Client;
        using var guest = pair.Server;
        using var app = new ControlledApp();
        var lifetime = GuestInspectedAppLifetime.RunAsync(app, new(123, 456),
            new InspectionService { Fail = !overlayFailure, OverlayFailure = overlayFailure },
            guest, guest, true, cancellation.Token);
        _ = await ReadAsync(host, cancellation.Token);
        await GuestCommentFrames.WriteAsync(host, new GuestInspectedAppControl("owner-ready"),
            GuestCommentsJsonContext.Default.GuestInspectedAppControl, cancellation.Token);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => lifetime);
        var reason = overlayFailure ? "Cannot find AcrylicBackgroundFillColorDefaultBrush" : "not injected";
        StringAssert.Contains(error.Message, reason);
        Assert.IsTrue(app.Killed);
        var failure = await ReadAsync(host, cancellation.Token);
        Assert.AreEqual("failed", failure.Phase);
        Assert.AreEqual(new GuestProcessStart(123, 456), failure.Process);
        StringAssert.Contains(failure.Error!, reason);
        if (overlayFailure)
        {
            StringAssert.Contains(failure.Error!, "Protocol attached");
            StringAssert.Contains(failure.Error!, "overlay startup");
            StringAssert.Contains(failure.Error!, "--no-overlay");
        }
    }

    private static async Task<GuestInspectedAppFrame> ReadAsync(Stream input, CancellationToken token) =>
        JsonSerializer.Deserialize(await GuestCommentFrames.ReadAsync(input, token),
            GuestCommentsJsonContext.Default.GuestInspectedAppFrame)!;

    [TestMethod]
    public async Task Lifetime_InvalidLaunchIdentityStillCleansRetainedOwnedApp()
    {
        using var app = new ControlledApp { OnClose = () => false };
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        var owned = GuestInspectedAppLifetime.CaptureIdentity(app);
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            GuestInspectedAppLifetime.RunAsync(app, new(123, 457), new InspectionService(),
                input, output, false, CancellationToken.None, ownedIdentity: owned));
        StringAssert.Contains(error.Message, "identity");
        Assert.AreEqual("close,root-kill", string.Join(",", app.CleanupCalls));
        Assert.IsTrue(app.HasExited);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Lifetime_CleanupFailureRetainsOriginalFailureAndReportsCleanup()
    {
        using var app = new ControlledApp { OnRootKill = () => throw new System.ComponentModel.Win32Exception(5) };
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        using var errors = new StringWriter();
        var previous = Console.Error;
        try
        {
            Console.SetError(errors);
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                GuestInspectedAppLifetime.RunAsync(app, new(123, 457), new InspectionService(),
                    input, output, false, CancellationToken.None));
            StringAssert.Contains(error.Message, "identity");
            StringAssert.Contains(errors.ToString(), "cleanup also failed");
            StringAssert.Contains(errors.ToString(), "Win32Exception");
            Assert.IsFalse(app.HasExited);
        }
        finally
        {
            Console.SetError(previous);
        }
    }

    [TestMethod]
    public async Task Cleanup_NormalCloseDoesNotForceTerminate()
    {
        using var app = new ControlledApp();
        app.OnClose = () => { app.Exit(7); return true; };
        Assert.IsFalse(await GuestInspectedAppLifetime.CloseOwnedAsync(app,
            GuestInspectedAppLifetime.CaptureIdentity(app)));
        Assert.AreEqual("close", string.Join(",", app.CleanupCalls));
        Assert.IsFalse(app.Killed);
        Assert.IsFalse(app.TreeKilled);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Cleanup_RefusedOrTimedOutCloseTerminatesOnlyRoot(bool accepted)
    {
        using var app = new ControlledApp { OnClose = () => accepted };
        Assert.IsTrue(await GuestInspectedAppLifetime.CloseOwnedAsync(app,
            GuestInspectedAppLifetime.CaptureIdentity(app), TimeSpan.FromMilliseconds(20)));
        Assert.AreEqual("close,root-kill", string.Join(",", app.CleanupCalls));
        Assert.IsTrue(app.HasExited);
        Assert.IsFalse(app.TreeKilled);
    }

    [TestMethod]
    [DataRow("start", false)]
    [DataRow("exe", false)]
    [DataRow("start", true)]
    [DataRow("exe", true)]
    [DataRow("package", false)]
    [DataRow("package", true)]
    [DataRow("aumid", false)]
    [DataRow("aumid", true)]
    public async Task Cleanup_ChangedIdentityNeverReceivesFallbackKill(string changed, bool afterClose)
    {
        using var app = new ControlledApp();
        var identity = GuestInspectedAppLifetime.CaptureIdentity(app);
        void Change()
        {
            if (changed == "start") { app.StartTicksUtc++; }
            else if (changed == "exe") { app.ExecutablePath = @"C:\foreign\App.exe"; }
            else if (changed == "package") { app.PackageFamilyName = "foreign"; }
            else { app.ApplicationUserModelId = "foreign!App"; }
        }
        if (afterClose) { app.OnClose = () => { Change(); return false; }; }
        else { Change(); }
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            GuestInspectedAppLifetime.CloseOwnedAsync(app, identity));
        Assert.AreEqual(afterClose ? 1 : 0, app.CleanupCalls.Count);
        Assert.IsFalse(app.Killed);
        Assert.IsFalse(app.TreeKilled);
    }

    [TestMethod]
    public async Task Cleanup_RefusedTerminationIsReportedWithoutTreeFallback()
    {
        using var app = new ControlledApp { OnRootKill = () => throw new System.ComponentModel.Win32Exception(5) };
        await Assert.ThrowsExactlyAsync<System.ComponentModel.Win32Exception>(() =>
            GuestInspectedAppLifetime.CloseOwnedAsync(app, GuestInspectedAppLifetime.CaptureIdentity(app)));
        Assert.AreEqual("close,root-kill", string.Join(",", app.CleanupCalls));
        Assert.IsFalse(app.HasExited);
        Assert.IsFalse(app.TreeKilled);
    }

    [TestMethod]
    public async Task Cleanup_AlreadyExitedDoesNotRequestClose()
    {
        using var app = new ControlledApp();
        var identity = GuestInspectedAppLifetime.CaptureIdentity(app);
        app.Exit(7);
        Assert.IsFalse(await GuestInspectedAppLifetime.CloseOwnedAsync(app, identity));
        Assert.IsEmpty(app.CleanupCalls);
    }

    [TestMethod]
    public async Task PreparationFailure_IsFramedAndBoundedWithoutAnInventedProcess()
    {
        using var output = new MemoryStream();
        await GuestInspectedAppLifetime.ReportFailureAsync(output, null,
            new string('x', GuestInspectedAppFrame.MaximumErrorLength + 1), CancellationToken.None);
        output.Position = 0;
        var failure = await ReadAsync(output, CancellationToken.None);
        Assert.AreEqual("failed", failure.Phase);
        Assert.IsNull(failure.Process);
        Assert.AreEqual(GuestInspectedAppFrame.MaximumErrorLength, failure.Error!.Length);
    }

    private sealed class InspectionService : IDevToolsService
    {
        public bool Called { get; private set; }
        public uint ProcessId { get; private set; }
        public bool Fail { get; init; }
        public bool OverlayFailure { get; init; }
        public Task<DevToolsConnection> ConnectAsync(uint targetPid, bool showOverlay, DevToolsAccess requestedAccess, CancellationToken cancellationToken)
        {
            Called = true;
            ProcessId = targetPid;
            return Task.FromResult(Fail ? DevToolsConnection.Fail("not injected") :
                OverlayFailure ? DevToolsConnection.Ok(12, false, "Cannot find AcrylicBackgroundFillColorDefaultBrush") :
                DevToolsConnection.Ok(12, showOverlay));
        }
    }

    private sealed class StartupNotices(bool emit) : IFirstRunService, IUpdateNotificationService
    {
        internal int FirstRunCalls { get; private set; }
        internal int UpdateCalls { get; private set; }
        public bool CheckAndDisplayFirstRunNotice()
        {
            FirstRunCalls++;
            if (emit)
            {
                Console.WriteLine("first-run notice");
            }
            return false;
        }
        public void CheckAndNotify()
        {
            UpdateCalls++;
            if (emit)
            {
                Console.WriteLine("update notice");
            }
        }
    }

    private sealed class LoggingInspectionService(ILogger logger, IAnsiConsole console, bool fail) : IDevToolsService
    {
        public async Task<DevToolsConnection> ConnectAsync(uint targetPid, bool showOverlay,
            DevToolsAccess requestedAccess, CancellationToken cancellationToken)
        {
            var attempts = 0;
            var outcome = await DevToolsInjectionRetry.RunAsync(
                () => ++attempts == 1 ? DevToolsInjectionRetry.ErrorNotFound : fail ? unchecked((int)0x80070005) : 0,
                () => true, 2, TimeSpan.Zero, (_, _) => Task.CompletedTask, cancellationToken);
            if (outcome.Succeeded)
            {
                logger.LogInformation("DevTools injection succeeded on attempt {Attempts} of 2.", outcome.Attempts);
            }
            else
            {
                logger.LogError("controlled injection failure");
            }
            logger.LogWarning("warning diagnostic");
            Console.WriteLine("console diagnostic");
            console.WriteLine("ansi diagnostic");
            return outcome.Succeeded ? DevToolsConnection.Ok(12, showOverlay) : DevToolsConnection.Fail("controlled injection failure");
        }
    }

    private sealed class ControlledApp : ILaunchedProcess
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public uint ProcessId => 123;
        public long? StartTicksUtc { get; set; } = 456;
        public bool HasExited => _exit.Task.IsCompleted;
        public string? PackageFamilyName { get; set; }
        public string? ApplicationUserModelId { get; set; }
        public string? ExecutablePath { get; set; } = @"C:\owned\App.exe";
        public int ExitCode { get; private set; }
        public bool Killed { get; private set; }
        public bool TreeKilled { get; private set; }
        public List<string> CleanupCalls { get; } = [];
        public Func<bool>? OnClose { get; set; }
        public Action? OnRootKill { get; set; }
        public Task WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);
        public void Exit(int code)
        {
            ExitCode = code;
            _exit.TrySetResult();
        }
        public void Kill()
        {
            TreeKilled = true;
            Killed = true;
            Exit(-1);
        }
        public bool RequestClose()
        {
            CleanupCalls.Add("close");
            return OnClose?.Invoke() ?? false;
        }
        public void KillProcessOnly()
        {
            CleanupCalls.Add("root-kill");
            OnRootKill?.Invoke();
            Killed = true;
            Exit(-1);
        }
        public void Dispose() { }
    }
}
