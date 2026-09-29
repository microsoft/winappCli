// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class GuestDevToolsRunPipelineTests() : BaseCommandTests(logLevel: Microsoft.Extensions.Logging.LogLevel.Warning), IAsyncDisposable
{
    private readonly FakeProjectRunService _projects = new();
    private readonly FakeMsixService _msix = new() { FakeIdentityResult = new("TestPackage", "CN=TestPublisher", "App") };
    private readonly FakeAppLauncherService _launcher = new();
    private readonly Backend _backend = new();
    private bool _disposed;
    private static readonly ExecutionTargetEpoch Epoch = ExecutionTargetEpoch.Create("sandbox", "pipeline");

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FakeHelperExit_CompletesAlreadyPendingWait(bool killed)
    {
        using var process = new FakeLaunchedProcess(123, 0) { HasExited = false };
        var waiting = process.WaitForExitAsync(CancellationToken.None);
        Assert.IsFalse(waiting.IsCompleted);
        if (killed)
        {
            process.Kill();
        }
        else
        {
            process.HasExited = true;
        }
        await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.IsTrue(process.HasExited);
        Assert.AreEqual(killed, process.Killed);
    }

    [TestMethod]
    public async Task FakeHelperExit_AlreadyExitedAndCancellationAreDistinct()
    {
        using var exited = new FakeLaunchedProcess(123, 0);
        Assert.IsTrue(exited.WaitForExitAsync(CancellationToken.None).IsCompletedSuccessfully);
        using var running = new FakeLaunchedProcess(124, 0) { HasExited = false };
        using var cancellation = new CancellationTokenSource();
        var wait = running.WaitForExitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => wait);
        Assert.IsFalse(running.HasExited);
        Assert.IsFalse(running.Killed);
        running.HasExited = true;
        Assert.IsTrue(running.WaitForExitAsync(CancellationToken.None).IsCompletedSuccessfully);
    }

    protected override IServiceCollection ConfigureServices(IServiceCollection services) => services
        .AddSingleton<IProjectRunService>(_projects)
        .AddSingleton<IMsixService>(_msix)
        .AddSingleton<IAppLauncherService>(_launcher)
        .AddSingleton<IPackageRegistrationService>(new FakePackageRegistrationService())
        .AddSingleton<IDebugOutputService>(new FakeDebugOutputService())
        .AddSingleton<INugetService, FakeNugetService>()
        .AddSingleton<IExecutionTargetBackend>(_backend)
        .AddSingleton<ITargetStateDirectoryProvider>(new TargetStateDirectoryProvider(Path.Combine(_tempDirectory.FullName, "state")));

    [TestCleanup]
    public async Task StopBackendAsync() => await DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await _backend.DisposeAsync();
        }
        GC.SuppressFinalize(this);
    }

    [TestMethod]
    [DataRow("--with-alias")]
    [DataRow("--debug-output")]
    [DataRow("--unregister-on-exit")]
    [DataRow("--without-alias")]
    [DataRow("--no-launch")]
    public async Task UnsupportedCombinations_FailBeforeBuildOrTargetProbe(string option)
    {
        var project = new FileInfo(Path.Combine(_tempDirectory.FullName, "App.csproj"));
        File.WriteAllText(project.FullName, "<Project />");
        var parsed = GetRequiredService<WinAppRootCommand>().Parse(
            ["run", project.FullName, "--devtools", "--on=sandbox", "--json", option]);
        Assert.AreEqual(1, await GetRequiredService<RunCommand.Handler>().InvokeAsync(parsed, TestContext.CancellationToken));
        Assert.AreEqual(0, _backend.ProbeCalls);
        Assert.AreEqual(0, _backend.ConnectionCalls);
        Assert.IsEmpty(_projects.BuildAndResolveCalls);
        Assert.IsEmpty(_launcher.LaunchExecutableCalls);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("outside")]
    [DataRow("empty")]
    [DataRow("oversized")]
    [DataRow("missing-engines")]
    public async Task UnprovedSourcesOrMissingEngines_FailBeforePreparingGuest(string scenario)
    {
        var project = _tempDirectory.CreateSubdirectory("project");
        var csproj = new FileInfo(Path.Combine(project.FullName, "App.csproj"));
        File.WriteAllText(csproj.FullName, "<Project />");
        var source = Path.Combine(project.FullName, "Main.xaml");
        if (scenario != "missing")
        {
            File.WriteAllText(source, scenario == "oversized" ? new string('x', GuestSourceSnapshot.MaximumFileBytes + 1) : "<Grid />");
        }
        var output = _tempDirectory.CreateSubdirectory("output");
        var executable = Path.Combine(output.FullName, "App.exe");
        File.WriteAllText(executable, "MZ");
        _projects.BuildOutcome = new(new ProjectRunResolution(csproj, output.FullName, executable,
            ProjectPackaging.Unpackaged, true, "x64",
            DevToolsXamlSources: scenario == "empty" ? [] : [scenario == "outside" ? @"..\outside.xaml" : "Main.xaml"]), 0);
        var handler = GetRequiredService<RunCommand.Handler>();
        handler.ReadGuestDevToolsCapabilities = _ => Task.FromResult<GuestDevToolsCapabilities?>(null);
        var parsed = GetRequiredService<WinAppRootCommand>().Parse(
            ["run", csproj.FullName, "--devtools", "--on=sandbox", "--json", "--no-build"]);
        Assert.AreEqual(1, await handler.InvokeAsync(parsed, TestContext.CancellationToken));
        Assert.AreEqual(1, _backend.ProbeCalls);
        Assert.AreEqual(0, _backend.ConnectionCalls);
        Assert.IsEmpty(_launcher.LaunchExecutableCalls);
        using var error = JsonDocument.Parse(TestAnsiConsole.Output);
        Assert.IsTrue(error.RootElement.TryGetProperty("Error", out _));
    }

    [TestMethod]
    [DataRow(false, false, false, false)]
    [DataRow(false, true, true, false)]
    [DataRow(true, false, true, false)]
    [DataRow(true, true, false, false)]
    [DataRow(false, false, false, true)]
    [DataRow(false, true, true, true)]
    [DataRow(true, false, true, true)]
    [DataRow(true, true, false, true)]
    public async Task PublicRun_DeploysSnapshotAndPublishesOnlyAcknowledgedActualApp(bool packaged, bool overlay, bool detach, bool aot)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var root = GetRequiredService<WinAppRootCommand>();
        var project = _tempDirectory.CreateSubdirectory("project");
        var csproj = new FileInfo(Path.Combine(project.FullName, "App.csproj"));
        File.WriteAllText(csproj.FullName, "<Project />");
        File.WriteAllText(Path.Combine(project.FullName, "Main.xaml"), "<Grid />");
        var output = _tempDirectory.CreateSubdirectory("output");
        var executable = Path.Combine(output.FullName, "App.exe");
        File.WriteAllText(executable, "MZ");
        File.WriteAllText(Path.Combine(output.FullName, "AppxManifest.xml"), RunCommandTests.TestManifestContent);
        var layout = _tempDirectory.CreateSubdirectory("layout");
        File.Copy(executable, Path.Combine(layout.FullName, "App.exe"));
        File.Copy(Path.Combine(output.FullName, "AppxManifest.xml"), Path.Combine(layout.FullName, "AppxManifest.xml"));

        var guestRoot = _tempDirectory.CreateSubdirectory("guest");
        var bundle = guestRoot.CreateSubdirectory("agent");
        var cli = Path.Combine(bundle.FullName, "winapp.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "kernel32.dll"), cli);
        File.Copy(cli, Path.Combine(bundle.FullName, GuestDevTools.NativeFileName));
        File.Copy(GetType().Assembly.Location, Path.Combine(bundle.FullName, GuestDevTools.ManagedFileName));
        var expected = (await GuestDevTools.ReadCapabilitiesAsync(cli, timeout.Token))!;
        _backend.Configure(guestRoot.FullName, cli, expected.Architecture, root);
        _projects.BuildOutcome = new(new ProjectRunResolution(csproj, output.FullName,
            packaged ? null : executable, packaged ? ProjectPackaging.Packaged : ProjectPackaging.Unpackaged,
            true, expected.Architecture, PreferExecutionAlias: false, DevToolsXamlSources: ["Main.xaml"]), 0);
        var recipe = Path.Combine(output.FullName, "App.build.appxrecipe");
        _projects.AotOutcome = new(_projects.BuildOutcome.Resolution! with
        {
            IsAot = true,
            AppxManifestPath = packaged ? Path.Combine(output.FullName, "AppxManifest.xml") : null,
            AppxRecipePath = packaged ? recipe : null,
        }, 0);
        var handler = GetRequiredService<RunCommand.Handler>();
        handler.ReadGuestDevToolsCapabilities = _ => Task.FromResult<GuestDevToolsCapabilities?>(expected);
        var host = GetRequiredService<GuestDevToolsHost>();
        var helperProcess = new FakeLaunchedProcess((uint)Environment.ProcessId, 0) { HasExited = false };
        var observed = new TaskCompletionSource<GuestDevToolsHostPlan>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? helper = null;
        _launcher.LaunchOverride = () =>
        {
            var launch = _launcher.LaunchExecutableCalls[^1];
            var argv = WindowsCommandLine.SplitArguments(launch.Arguments!);
            Assert.AreEqual("guest-devtools-host", argv[0]);
            var id = argv[2];
            helper = SimulateRetainedOwnerAsync(id);
            return helperProcess;
        };

        async Task SimulateRetainedOwnerAsync(string id)
        {
            using var pipe = new NamedPipeClientStream(".", GuestDevToolsHost.PipeName(id), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token);
            var plan = host.ReadPlan(_backend.Target, id);
            using (var available = new TargetMutationLock(GetRequiredService<ITargetStateDirectoryProvider>())
                .TryAcquire(_backend.Target, TimeSpan.Zero, timeout.Token))
            {
                Assert.IsNotNull(available, "The app lifetime must not retain the deployment mutation lease.");
            }
            var frame = new GuestInspectedAppFrame("ready", new(54321, 67890), 12, overlay);
            await GuestCommentFrames.WriteAsync(pipe, new GuestDevToolsHostMessage("ready", frame, SessionId: id, BindingId: id),
                GuestCommentsJsonContext.Default.GuestDevToolsHostMessage, timeout.Token);
            var control = JsonSerializer.Deserialize(await GuestCommentFrames.ReadAsync(pipe, timeout.Token),
                GuestCommentsJsonContext.Default.GuestInspectedAppControl)!;
            Assert.AreEqual(detach ? "detach" : "wait", control.Kind);
            Assert.DoesNotContain("appSelector", TestAnsiConsole.Output, "Readiness is not output before accepted ownership.");
            await GuestCommentFrames.WriteAsync(pipe, new GuestDevToolsHostMessage("accepted", SessionId: id),
                GuestCommentsJsonContext.Default.GuestDevToolsHostMessage, timeout.Token);
            observed.SetResult(plan);
            if (!detach)
            {
                helperProcess.HasExited = true;
                await GuestCommentFrames.WriteAsync(pipe, new GuestDevToolsHostMessage("exited", ExitCode: 7, SessionId: id),
                    GuestCommentsJsonContext.Default.GuestDevToolsHostMessage, timeout.Token);
            }
        }

        List<string> args = ["run", csproj.FullName, "--devtools", "--on=sandbox", aot ? "--aot" : "--no-build", "--json"];
        if (packaged)
        {
            args.Add("--output-appx-directory=" + layout.FullName);
        }
        if (!overlay)
        {
            args.Add("--no-overlay");
        }
        if (detach)
        {
            args.Add("--detach");
        }
        var parsed = root.Parse([.. args]);
        Assert.IsEmpty(parsed.Errors);
        var running = handler.InvokeAsync(parsed, timeout.Token);
        if (packaged)
        {
            var registration = await _backend.Processes.WaitForNextAsync(timeout.Token);
            var registrationArgs = root.Parse(registration.Request.Arguments.ToArray());
            Assert.IsTrue(registrationArgs.GetValue(RunCommand.NoLaunchOption));
            Assert.AreEqual("App", registrationArgs.GetValue(RunCommand.GuestInspectorApplicationOption));
            registration.Exit(0);
        }
        Assert.AreEqual(detach ? 0 : 7, await running);
        var plan = await observed.Task.WaitAsync(timeout.Token);
        await helper!;
        Assert.AreEqual(csproj.FullName, plan.Sources.ProjectPath);
        Assert.AreEqual("<Grid />", File.ReadAllText(Path.Combine(plan.Sources.GuestRoot!, "Main.xaml")));
        Assert.IsTrue(plan.Request.UseGuestWinapp);
        var guestArgs = new GuestDevToolsLaunchCommand().Parse(plan.Request.Arguments.Skip(1).ToArray());
        Assert.IsEmpty(guestArgs.Errors);
        Assert.AreEqual(!overlay, guestArgs.GetValue(GuestDevToolsLaunchCommand.NoOverlayOption));
        Assert.AreEqual(!aot, guestArgs.GetValue(GuestDevToolsLaunchCommand.ManagedOption));
        Assert.AreEqual(packaged ? "App" : null, guestArgs.GetValue(GuestDevToolsLaunchCommand.ApplicationIdOption));
        Assert.IsFalse(plan.Request.Arguments.Any(arg => arg.Contains(project.FullName, StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue((aot ? _projects.AotOptions : _projects.BuildOptions).Single().CaptureDevToolsSources);
        if (packaged)
        {
            Assert.AreEqual(aot ? recipe : null, _msix.AddLooseLayoutRecipeCalls.Single());
            Assert.AreEqual(Path.Combine(output.FullName, "AppxManifest.xml"), _msix.MaterializeLooseLayoutCalls.Single().Manifest,
                StringComparer.OrdinalIgnoreCase);
        }
        Assert.IsEmpty(_launcher.LaunchCalls);
        Assert.HasCount(1, _launcher.LaunchExecutableCalls, "Only the retained host owner is launched locally, never the application.");
        Assert.IsFalse(helperProcess.Killed);
        using var result = JsonDocument.Parse(TestAnsiConsole.Output);
        Assert.AreEqual(54321, result.RootElement.GetProperty("ProcessId").GetInt32());
        var selector = result.RootElement.GetProperty("appSelector").GetString()!;
        var id = selector["guest:".Length..];
        Assert.IsFalse(Directory.Exists(Path.Combine(host.Resolve(_backend.Target, id).FullName, "sources")),
            "The transferred source copy is not retained on the host.");
        Assert.AreEqual(overlay, result.RootElement.GetProperty("devTools").GetProperty("overlayShown").GetBoolean());
    }

    [TestMethod]
    [DataRow("negotiation")]
    [DataRow("relay-disconnect")]
    [DataRow("normal-exit")]
    [DataRow("stdin-wake")]
    [DataRow("terminal-timeout")]
    public async Task ActualRetainedHandler_PersistsTerminalStateAndAppIdentity(string outcome)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var root = GetRequiredService<WinAppRootCommand>();
        var project = _tempDirectory.CreateSubdirectory("failed-project");
        var csproj = Path.Combine(project.FullName, "App.csproj");
        File.WriteAllText(csproj, "<Project/>");
        File.WriteAllText(Path.Combine(project.FullName, "Main.xaml"), "<Page/>");
        var bundle = _tempDirectory.CreateSubdirectory("failed-guest");
        var cli = Path.Combine(bundle.FullName, "winapp.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "kernel32.dll"), cli);
        File.Copy(cli, Path.Combine(bundle.FullName, GuestDevTools.NativeFileName));
        File.Copy(GetType().Assembly.Location, Path.Combine(bundle.FullName, GuestDevTools.ManagedFileName));
        var expected = (await GuestDevTools.ReadCapabilitiesAsync(cli, timeout.Token))!;
        _backend.Configure(bundle.FullName, cli, expected.Architecture, root);
        _backend.Processes.OnStart = _ => { };
        _backend.Processes.OnExit = (_, _) => { };
        var alive = true;
        _backend.QueryProcess = (pid, start) => pid == 123 && start == 456 && Volatile.Read(ref alive);
        var states = GetRequiredService<IDeploymentStateStore>();
        var deployment = states.Commit(_backend.Target, new DeploymentState
        {
            SchemaVersion = DeploymentStateStore.CurrentSchemaVersion,
            Revision = 0,
            DeploymentId = "failed-app",
            TargetEpoch = Epoch.Value,
            Dirty = false,
        }, 0);
        var host = GetRequiredService<GuestDevToolsHost>();
        var id = Guid.NewGuid().ToString("N");
        host.Create(_backend.Target, id);
        var handler = GetRequiredService<GuestDevToolsHostCommand.Handler>();
        handler.ReadGuestDevToolsCapabilities = _ => Task.FromResult<GuestDevToolsCapabilities?>(expected);
        var process = new FakeLaunchedProcess((uint)Environment.ProcessId, 0) { HasExited = false };
        Task<int>? helper = null;
        _launcher.LaunchOverride = () =>
        {
            helper = RunHelperAsync();
            return process;
        };
        async Task<int> RunHelperAsync()
        {
            try
            {
                return await handler.InvokeAsync(root.Parse(["guest-devtools-host", "--launch-id", id]), timeout.Token);
            }
            finally
            {
                process.HasExited = true;
            }
        }
        var ready = false;
        var starting = host.StartAsync(_backend.Target, id, new(Epoch.Value,
            new(csproj, [new("Main.xaml", 7, new string('A', 64))]),
            new GuestExecRequest { UseGuestWinapp = true, Arguments = ["guest-devtools-launch"] },
            deployment.DeploymentId, deployment.Revision), true, timeout.Token, _ => ready = true);
        var launch = await _backend.Processes.WaitForNextAsync(timeout.Token);
        await launch.EmitBytesAsync(GuestStreamId.StandardOutput,
            GuestCommentFrames.Encode(new GuestInspectedAppFrame("started", new(123, 456)),
                GuestCommentsJsonContext.Default.GuestInspectedAppFrame));
        var relay = await _backend.Processes.WaitForNextAsync(timeout.Token);
        var initial = host.ReadState(_backend.Target, id);
        Assert.AreEqual("started", initial.Phase);
        Assert.AreEqual(new GuestProcessStart(123, 456), initial.App!.Process);
        Assert.IsFalse(ready);
        if (outcome is "normal-exit" or "stdin-wake" or "terminal-timeout")
        {
            launch.OnStandardInput = async (bytes, cancellation) =>
            {
                using var input = new MemoryStream(bytes.ToArray());
                var control = JsonSerializer.Deserialize(await GuestCommentFrames.ReadAsync(input, cancellation),
                    GuestCommentsJsonContext.Default.GuestInspectedAppControl);
                if (control!.Kind == "owner-ready")
                {
                    await launch.EmitBytesAsync(GuestStreamId.StandardOutput,
                        GuestCommentFrames.Encode(new GuestInspectedAppFrame("ready", new(123, 456), 12, true),
                            GuestCommentsJsonContext.Default.GuestInspectedAppFrame));
                }
                else if (control.Kind == "stop" && outcome != "terminal-timeout")
                {
                    launch.Exit(7);
                }
            };
            var relayArgs = root.Parse(relay.Request.Arguments.ToArray());
            var request = new GuestCommentRequest(relayArgs.GetValue(GuestCommentRelayCommand.BindingOption)!,
                relayArgs.GetValue(GuestCommentRelayCommand.TargetOption)!, relayArgs.GetValue(GuestCommentRelayCommand.EpochOption)!,
                new(123, 456), Guid.NewGuid().ToString("N"), "", "", null);
            await relay.EmitBytesAsync(GuestStreamId.StandardOutput,
                GuestCommentFrames.Encode(new GuestCommentEnvelope("ready", request), GuestCommentsJsonContext.Default.GuestCommentEnvelope));
            Assert.AreEqual("ready", (await starting).Phase);
            Volatile.Write(ref alive, false);
            if (outcome == "normal-exit")
            {
                launch.Exit(7);
            }
            var normal = outcome is "normal-exit" or "stdin-wake";
            Assert.AreEqual(normal ? 7 : 1, await helper!);
            var final = host.ReadState(_backend.Target, id);
            Assert.AreEqual(normal ? "exited" : "failed", final.Phase);
            Assert.AreEqual(normal ? 7 : (int?)null, final.ExitCode);
            Assert.AreEqual(new GuestProcessStart(123, 456), final.App!.Process);
            Assert.IsNull(final.BindingId);
            Assert.IsTrue(launch.Disposed);
            Assert.IsTrue(relay.Disposed);
            Assert.IsFalse(process.Killed);
            return;
        }
        await launch.EmitBytesAsync(GuestStreamId.StandardOutput,
            GuestCommentFrames.Encode(new GuestInspectedAppFrame("failed", new(123, 456),
                Error: "DevTools negotiation failed (no-response, code -32000)."),
                GuestCommentsJsonContext.Default.GuestInspectedAppFrame));
        launch.Exit(1);
        if (outcome == "relay-disconnect")
        {
            relay.Exit(1);
        }
        var error = await Assert.ThrowsAsync<IOException>(() => starting);
        StringAssert.Contains(error.Message, "negotiation failed");
        StringAssert.Contains(error.Message, "diagnostics.log");
        Assert.AreEqual(1, await helper!);
        var failed = host.ReadState(_backend.Target, id);
        Assert.AreEqual("failed", failed.Phase);
        StringAssert.Contains(failed.Error!, "negotiation failed");
        Assert.AreEqual(new GuestProcessStart(123, 456), failed.App!.Process);
        Assert.IsNull(failed.BindingId);
        Assert.IsFalse(ready);
        Assert.IsTrue(relay.Disposed);
        Assert.IsTrue(launch.Disposed);
        Assert.IsFalse(process.Killed);
        Assert.AreEqual(0, _backend.ConnectionCalls, "The retained owner only inspects an existing connection.");
    }

    private sealed class Backend : IExecutionTargetBackend, IInspectableTarget, IAsyncDisposable
    {
        private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromSeconds(30));
        private GuestCommandServer? _server;
        private IGuestTransport? _hostTransport;
        public ExecutionTargetRef Target => WindowsSandboxTarget.Default;
        public FakeGuestProcessHostFactory Processes { get; } = new();
        public int ProbeCalls { get; private set; }
        public int ConnectionCalls { get; private set; }
        public Func<int, long, bool>? QueryProcess { set => _server!.QueryProcessImpl = value!; }

        public void Configure(string guestRoot, string cli, string architecture, WinAppRootCommand root)
        {
            var inventory = new FakeAppLauncherService
            {
                FakePackageFullName = null,
                FakePackageName = "TestPackage",
                FakePublisher = "CN=TestPublisher",
            };
            Processes.OnStart = request =>
            {
                var parsed = root.Parse(request.Arguments.ToArray());
                Directory.CreateDirectory(parsed.GetValue(RunCommand.ManagedAppXDirectoryOption)!.FullName);
            };
            Processes.OnExit = (request, code) =>
            {
                if (code == 0)
                {
                    var parsed = root.Parse(request.Arguments.ToArray());
                    inventory.FakePackageFullName = "TestPackage_1.0.0.0_x64__fakefamily";
                    inventory.FakeRegisteredLocation = parsed.GetValue(RunCommand.ManagedAppXDirectoryOption)!.FullName;
                }
            };
            var pair = new LoopbackTransportPair();
            _hostTransport = pair.Host;
            _server = new(pair.Guest, Epoch, Processes, new StaticGuestSessionProbe(new(1, "WinSta0", true)),
                new("1.0.0", "test", architecture, GuestProtocol.MinimumVersion, GuestProtocol.CurrentVersion),
                new GuestFileService(guestRoot), cli, inventory, new FakePackageRegistrationService());
            _ = _server.RunAsync(_lifetime.Token);
        }

        public Task<TargetSupportResult> ProbeSupportAsync(CancellationToken cancellationToken)
        {
            ProbeCalls++;
            return Task.FromResult(TargetSupportResult.Supported);
        }
        public Task<TargetConnection> EnsureConnectedAsync(EnsureTargetOptions options, CancellationToken cancellationToken)
        {
            ConnectionCalls++;
            return Task.FromResult(new TargetConnection(Epoch,
                _hostTransport ?? throw new AssertFailedException("The guest must not be prepared."), false));
        }
        public IReadOnlyDictionary<string, string> DescribeForDiagnostics() => new Dictionary<string, string>();
        public Task<TargetAttachment> TryAttachAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new TargetAttachment(true, Epoch, new(Epoch, _hostTransport!, true)));
        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync();
            if (_server is not null)
            {
                await _server.DisposeAsync();
            }
            _lifetime.Dispose();
        }
    }
}
