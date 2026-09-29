// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;
using WinApp.Cli.Services;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class ExecutionTargetDevToolsRouterTests : BaseCommandTests
{
    private static readonly ExecutionTargetEpoch Epoch = ExecutionTargetEpoch.Create("sandbox", "opaque:epoch.with.dots");
    protected override IServiceCollection ConfigureServices(IServiceCollection services) => services;

    [TestMethod]
    [DataRow("inspect --depth=0")]
    [DataRow("inspect --ancestors")]
    [DataRow("inspect --window=123")]
    [DataRow("search --max=0")]
    [DataRow("search")]
    [DataRow("set-property Title")]
    [DataRow("call Internal.reset")]
    [DataRow("call DevTools.ping invalid")]
    [DataRow("attach --pid=123")]
    [DataRow("comments update c1 --status=invalid")]
    [DataRow("comments add --text=note --file=..\\foreign.xaml")]
    [DataRow("inspect --guest-inspection=foreign")]
    public async Task InvalidArguments_NeverConnectOrProvision(string command)
    {
        await using var backend = new Backend();
        var router = Router(backend);
        var args = new List<string> { "devtools" };
        args.AddRange(command.Split(' '));
        args.AddRange(["--on=sandbox", "--app=guest:" + new string('a', 32), "--json"]);
        var parsed = GetRequiredService<WinAppRootCommand>().Parse([.. args]);
        Assert.IsEmpty(parsed.Errors);
        Assert.AreEqual(1, await router.RouteAsync(parsed, TestContext.CancellationToken));
        Assert.AreEqual(0, backend.AttachCalls);
        Assert.IsTrue(backend.Processes.Started.IsEmpty);
        Assert.IsFalse(Directory.Exists(Path.Combine(_tempDirectory.FullName, "state")));
    }

    [TestMethod]
    [DataRow("--help")]
    [DataRow("--cli-schema")]
    [DataRow("--depth=not-a-number")]
    public void MetaAndParseErrors_DoNotRoute(string argument)
    {
        var parsed = GetRequiredService<WinAppRootCommand>().Parse(
            ["devtools", "inspect", "--on=sandbox", argument]);
        Assert.IsFalse(ExecutionTargetDevToolsRouter.ShouldRoute(parsed));
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("sandbox:123")]
    [DataRow("guest-process:123:0:epoch")]
    [DataRow("")]
    public async Task InvalidApp_NeverLooksUpAHostProcess(string selector)
    {
        await using var backend = new Backend();
        var parsed = GetRequiredService<WinAppRootCommand>().Parse(
            ["devtools", "inspect", "--on=sandbox", "--app", selector, "--json"]);
        Assert.AreEqual(1, await Router(backend).RouteAsync(parsed, TestContext.CancellationToken));
        Assert.AreEqual(0, backend.AttachCalls);
    }

    [TestMethod]
    public async Task MissingAgent_IsReadOnlyFailure_NotProvisioning()
    {
        await using var backend = new Backend();
        var parsed = GetRequiredService<WinAppRootCommand>().Parse(["devtools", "list", "--on=sandbox", "--json"]);
        Assert.AreEqual(1, await Router(backend).RouteAsync(parsed, TestContext.CancellationToken));
        Assert.AreEqual(1, backend.AttachCalls);
        StringAssert.Contains(TestAnsiConsole.Output, "No target was provisioned");
    }

    [TestMethod]
    [DataRow(false, false, false, "inspect")]
    [DataRow(true, false, false, "inspect")]
    [DataRow(false, true, false, "inspect")]
    [DataRow(false, false, true, "inspect")]
    [DataRow(false, false, false, "attach")]
    public async Task ActualDispatch_UsesGuestCliAndFencedIdentityOnly(bool reusedPid, bool wrongBundle, bool staleEpoch, string verb)
    {
        var cli = CreateBundle();
        var expected = (await GuestDevTools.ReadCapabilitiesAsync(cli, TestContext.CancellationToken))!;
        await using var backend = new Backend(cli, expected.Architecture) { ReusedPid = reusedPid };
        var selector = GuestDevToolsSelector.ForProcess(backend.Target, staleEpoch ? new("expired") : Epoch, new(123, 456));
        var parsed = GetRequiredService<WinAppRootCommand>().Parse(
            ["devtools", verb, "--on=sandbox", "--app=" + selector, "--json"]);
        var running = Router(backend, wrongBundle ? null : expected).RouteAsync(parsed, TestContext.CancellationToken);
        if (reusedPid || wrongBundle || staleEpoch)
        {
            Assert.AreNotEqual(0, await running);
            Assert.IsTrue(backend.Processes.Started.IsEmpty);
            return;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var process = await backend.Processes.WaitForNextAsync(timeout.Token);
        Assert.AreEqual(cli, process.Request.Executable);
        var forwarded = GetRequiredService<WinAppRootCommand>().Parse(process.Request.Arguments.ToArray());
        Assert.IsEmpty(forwarded.Errors);
        if (verb == "attach")
        {
            Assert.AreEqual(123, forwarded.GetValue(DevToolsAttachCommand.PidOption));
        }
        else
        {
            Assert.AreEqual("123", forwarded.GetValue(SharedUiOptions.AppOption));
        }
        Assert.AreEqual($"123.456..{Epoch.Value}", forwarded.GetValue(WinAppRootCommand.GuestInspectionOption));
        Assert.IsTrue(ExecutionTargetSelection.Resolve(forwarded).IsLocal);
        await process.EmitBytesAsync(GuestStreamId.StandardOutput, Encoding.UTF8.GetBytes(
            """{"ok":true,"processId":123,"executionTarget":{"kind":"forged"},"appSelector":"forged"}"""));
        process.Exit(0);
        Assert.AreEqual(0, await running);
        using var output = JsonDocument.Parse(TestAnsiConsole.Output);
        Assert.AreEqual(selector, output.RootElement.GetProperty("appSelector").GetString());
        Assert.AreEqual("sandbox", output.RootElement.GetProperty("executionTarget").GetProperty("kind").GetString());
        Assert.AreEqual(Epoch.Value, output.RootElement.GetProperty("executionTarget").GetProperty("epoch").GetString());
        Assert.IsTrue(backend.Processes.Started.IsEmpty);
    }

    [TestMethod]
    [DataRow("123", "inspect")]
    [DataRow("DAYLIGHT", "inspect")]
    [DataRow("day", "inspect")]
    [DataRow("Tasks", "inspect")]
    [DataRow("123", "attach")]
    [DataRow("123", "search heading")]
    [DataRow("123", "get-property Title --property Text")]
    [DataRow("123", "get-layout Title")]
    [DataRow("123", "get-source Title")]
    [DataRow("123", "diagnose-binding Title Text")]
    [DataRow("123", "set-property Title Changed --property Text")]
    [DataRow("123", "set-property Title Text Changed")]
    [DataRow("123", "call DevTools.ping")]
    [DataRow("123", "comments add --text note --from-element handle:42")]
    public async Task FriendlyApp_DiscoveryPreservesHostLaunchAuthority(string app, string command)
    {
        var cli = CreateBundle();
        var expected = (await GuestDevTools.ReadCapabilitiesAsync(cli, TestContext.CancellationToken))!;
        await using var backend = new Backend(cli, expected.Architecture);
        var (id, _) = CreateLaunch(backend);
        var root = GetRequiredService<WinAppRootCommand>();
        var parsed = root.Parse($"devtools {command} --on sandbox --app {app} --json");
        Assert.IsEmpty(parsed.Errors);
        var running = Router(backend, expected).RouteAsync(parsed, TestContext.CancellationToken);
        var discovery = await backend.Processes.WaitForNextAsync(TestContext.CancellationToken);
        CollectionAssert.Contains(discovery.Request.Arguments.ToList(), "--guest-discovery");
        await discovery.EmitBytesAsync(GuestStreamId.StandardOutput, Encoding.UTF8.GetBytes(
            """{"apps":[{"processId":123,"startTicksUtc":"456","processName":"Daylight","windowTitle":"Today's Tasks","appSelector":"guest:forged"}]}"""));
        discovery.Exit(0);
        var invocation = await backend.Processes.WaitForNextAsync(TestContext.CancellationToken);
        var forwarded = root.Parse(invocation.Request.Arguments.ToArray());
        Assert.IsEmpty(forwarded.Errors);
        Assert.AreEqual($"123.456.{id}.{Epoch.Value}", forwarded.GetValue(WinAppRootCommand.GuestInspectionOption));
        Assert.AreEqual(@"C:\guest\snapshot", invocation.Request.Environment!["WINAPP_DEVTOOLS_SOURCE_ROOT"]);
        await invocation.EmitBytesAsync(GuestStreamId.StandardOutput, Encoding.UTF8.GetBytes("""{"ok":true}"""));
        invocation.Exit(0);
        Assert.AreEqual(0, await running);
        using var result = JsonDocument.Parse(TestAnsiConsole.Output);
        Assert.AreEqual("guest:" + id, result.RootElement.GetProperty("appSelector").GetString());
        Assert.IsTrue(backend.Processes.Started.IsEmpty);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FriendlyApp_AmbiguityOrReuseAfterDiscoveryNeverDispatches(bool reused)
    {
        var cli = CreateBundle();
        var expected = (await GuestDevTools.ReadCapabilitiesAsync(cli, TestContext.CancellationToken))!;
        await using var backend = new Backend(cli, expected.Architecture)
        {
            AllowSecondProcess = !reused,
            FailProcessCheckAfter = reused ? 1 : null,
        };
        var parsed = GetRequiredService<WinAppRootCommand>().Parse("devtools inspect --on sandbox --app Daylight --json");
        var running = Router(backend, expected).RouteAsync(parsed, TestContext.CancellationToken);
        var discovery = await backend.Processes.WaitForNextAsync(TestContext.CancellationToken);
        await discovery.EmitBytesAsync(GuestStreamId.StandardOutput, Encoding.UTF8.GetBytes(reused
            ? """{"apps":[{"processId":123,"startTicksUtc":"456","processName":"Daylight"}]}"""
            : """{"apps":[{"processId":123,"startTicksUtc":"456","processName":"Daylight"},{"processId":124,"startTicksUtc":"457","processName":"Daylight"}]}"""));
        discovery.Exit(0);
        Assert.AreEqual(1, await running);
        Assert.IsTrue(backend.Processes.Started.IsEmpty);
        StringAssert.Contains(TestAnsiConsole.Output, reused ? "changed" : "Multiple guest apps");
    }

    [TestMethod]
    [DataRow("missing", "456")]
    [DataRow("Daylight", "not-ticks")]
    [DataRow("Daylight", "457")]
    public async Task FriendlyApp_MissingOrUnverifiedMatchNeverDispatches(string app, string startTicks)
    {
        var cli = CreateBundle();
        var expected = (await GuestDevTools.ReadCapabilitiesAsync(cli, TestContext.CancellationToken))!;
        await using var backend = new Backend(cli, expected.Architecture);
        var parsed = GetRequiredService<WinAppRootCommand>().Parse(
            ["devtools", "inspect", "--on", "sandbox", "--app", app, "--json"]);
        var running = Router(backend, expected).RouteAsync(parsed, TestContext.CancellationToken);
        var discovery = await backend.Processes.WaitForNextAsync(TestContext.CancellationToken);
        await discovery.EmitBytesAsync(GuestStreamId.StandardOutput, Encoding.UTF8.GetBytes(
            $$"""{"apps":[{"processId":123,"startTicksUtc":"{{startTicks}}","processName":"Daylight"}]}"""));
        discovery.Exit(0);
        Assert.AreEqual(1, await running);
        Assert.IsTrue(backend.Processes.Started.IsEmpty);
        StringAssert.Contains(TestAnsiConsole.Output, "No current guest WinUI app");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FriendlyApp_ExactNameWinsButCannotInventCommentBinding(bool comments)
    {
        var cli = CreateBundle();
        var expected = (await GuestDevTools.ReadCapabilitiesAsync(cli, TestContext.CancellationToken))!;
        await using var backend = new Backend(cli, expected.Architecture) { AllowSecondProcess = true };
        var root = GetRequiredService<WinAppRootCommand>();
        var parsed = root.Parse("devtools " + (comments ? "comments add --text note --from-element Title" : "inspect") +
            " --on sandbox --app Daylight --json");
        var running = Router(backend, expected).RouteAsync(parsed, TestContext.CancellationToken);
        var discovery = await backend.Processes.WaitForNextAsync(TestContext.CancellationToken);
        await discovery.EmitBytesAsync(GuestStreamId.StandardOutput, Encoding.UTF8.GetBytes(
            """{"apps":[{"processId":123,"startTicksUtc":"456","processName":"Daylight"},{"processId":124,"startTicksUtc":"457","processName":"DaylightPreview","windowTitle":"Daylight"}]}"""));
        discovery.Exit(0);
        if (comments)
        {
            Assert.AreEqual(1, await running);
            StringAssert.Contains(TestAnsiConsole.Output, "host-owned guest launch");
        }
        else
        {
            var invocation = await backend.Processes.WaitForNextAsync(TestContext.CancellationToken);
            var forwarded = root.Parse(invocation.Request.Arguments.ToArray());
            Assert.AreEqual($"123.456..{Epoch.Value}", forwarded.GetValue(WinAppRootCommand.GuestInspectionOption));
            await invocation.EmitBytesAsync(GuestStreamId.StandardOutput, Encoding.UTF8.GetBytes("""{"ok":true}"""));
            invocation.Exit(0);
            Assert.AreEqual(0, await running);
        }
        Assert.IsTrue(backend.Processes.Started.IsEmpty);
    }

    [TestMethod]
    [DataRow("update")]
    [DataRow("delete")]
    public async Task ExplicitRefresh_MissingSavedCommentFailsBeforeConnecting(string verb)
    {
        await using var backend = new Backend();
        List<string> args = ["devtools", "comments", verb, "missing", "--app", "123", "--on", "sandbox",
            "--source-root", _tempDirectory.FullName, "--json"];
        if (verb == "update") { args.AddRange(["--status", "resolved"]); }
        var parsed = GetRequiredService<WinAppRootCommand>().Parse([.. args]);
        Assert.AreEqual(1, await Router(backend).RouteAsync(parsed, TestContext.CancellationToken));
        Assert.AreEqual(0, backend.AttachCalls);
        Assert.IsTrue(backend.Processes.Started.IsEmpty);
        Assert.IsFalse(File.Exists(new CommentStore().GetStorePath(_tempDirectory)));
    }

    private (string Id, DirectoryInfo Project) CreateLaunch(Backend backend)
    {
        _tempDirectory.CreateSubdirectory(".git");
        var project = _tempDirectory.CreateSubdirectory("Project");
        var projectFile = Path.Combine(project.FullName, "App.csproj");
        File.WriteAllText(projectFile, "<Project />");
        File.WriteAllText(Path.Combine(project.FullName, "Main.xaml"), "<Grid />");
        var host = Host();
        var id = Guid.NewGuid().ToString("N");
        var directory = host.Create(backend.Target, id);
        host.WritePlan(backend.Target, id, new(Epoch.Value,
            new(projectFile, [new("Main.xaml", 8, new string('A', 64))], @"C:\guest\snapshot", new string('B', 64)),
            new GuestExecRequest { UseGuestWinapp = true, Arguments = ["guest-devtools-launch"] }, "app", 1));
        GuestDevToolsHost.WriteState(directory, new("ready", new("ready", new(123, 456), 1, true), SessionId: id, BindingId: id));
        return (id, project);
    }

    [TestMethod]
    [DataRow("update", false)]
    [DataRow("delete", false)]
    [DataRow("update", true)]
    [DataRow("delete", true)]
    public async Task ExplicitRefresh_RejectsDifferentProjectOrStoreBeforeMutation(string verb, bool differentStore)
    {
        var cli = CreateBundle();
        var expected = (await GuestDevTools.ReadCapabilitiesAsync(cli, TestContext.CancellationToken))!;
        await using var backend = new Backend(cli, expected.Architecture);
        var (id, project) = CreateLaunch(backend);
        var selected = _tempDirectory.CreateSubdirectory("Other");
        if (differentStore)
        {
            selected.CreateSubdirectory(".git");
        }
        var store = new CommentStore();
        var path = store.GetStorePath(selected);
        store.Add(path, new Comment
        {
            Id = "own", Text = "unchanged", ProjectRoot = differentStore ? project.FullName : selected.FullName,
            Status = CommentStatus.Open, Anchor = new() { SourceFile = "Main.xaml", Identity = new() },
        });
        var before = File.ReadAllBytes(path);
        List<string> args = ["devtools", "comments", verb, "own", "--on", "sandbox", "--app", "guest:" + id,
            "--source-root", selected.FullName, "--json"];
        if (verb == "update") { args.AddRange(["--status", "resolved"]); }
        var parsed = GetRequiredService<WinAppRootCommand>().Parse([.. args]);
        Assert.IsEmpty(parsed.Errors);
        Assert.AreEqual(1, await Router(backend, expected).RouteAsync(parsed, TestContext.CancellationToken));
        StringAssert.Contains(TestAnsiConsole.Output, "does not own");
        Assert.IsTrue(backend.Processes.Started.IsEmpty);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
    }

    [TestMethod]
    [DataRow("update")]
    [DataRow("delete")]
    public async Task ExplicitRefresh_MissingTargetDoesNotMutateChosenHostStore(string verb)
    {
        await using var backend = new Backend();
        var store = new CommentStore();
        var path = store.GetStorePath(_tempDirectory);
        store.Add(path, new Comment
        {
            Id = "own", Text = "unchanged", Status = CommentStatus.Open,
            Anchor = new() { Identity = new() },
        });
        var before = File.ReadAllBytes(path);
        List<string> args = ["devtools", "comments", verb, "own", "--on", "sandbox", "--app", "123",
            "--source-root", _tempDirectory.FullName, "--json"];
        if (verb == "update") { args.AddRange(["--status", "resolved"]); }
        var parsed = GetRequiredService<WinAppRootCommand>().Parse([.. args]);
        Assert.AreEqual(1, await Router(backend).RouteAsync(parsed, TestContext.CancellationToken));
        Assert.AreEqual(1, backend.AttachCalls);
        Assert.IsTrue(backend.Processes.Started.IsEmpty);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
    }

    [TestMethod]
    [DataRow("update")]
    [DataRow("delete")]
    public async Task ExplicitRefresh_UsesOneScopedWriterAndPreservesSavedWarning(string verb)
    {
        var cli = CreateBundle();
        var expected = (await GuestDevTools.ReadCapabilitiesAsync(cli, TestContext.CancellationToken))!;
        await using var backend = new Backend(cli, expected.Architecture);
        var (id, project) = CreateLaunch(backend);
        var store = new CommentStore();
        var path = store.GetStorePath(project);
        store.Add(path, new Comment
        {
            Id = "own", Text = "unchanged", ProjectRoot = project.FullName,
            Status = CommentStatus.Open, Anchor = new() { SourceFile = "Main.xaml", Identity = new() },
        });
        var before = File.ReadAllBytes(path);
        List<string> args = ["devtools", "comments", verb, "own", "--on", "sandbox", "--app", "guest:" + id,
            "--source-root", project.FullName, "--json"];
        if (verb == "update") { args.AddRange(["--status", "resolved"]); }
        var root = GetRequiredService<WinAppRootCommand>();
        var parsed = root.Parse([.. args]);
        var running = Router(backend, expected).RouteAsync(parsed, TestContext.CancellationToken);
        var invocation = await backend.Processes.WaitForNextAsync(TestContext.CancellationToken);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path), "Preflight does not perform a duplicate local mutation.");
        var forwarded = root.Parse(invocation.Request.Arguments.ToArray());
        Assert.IsEmpty(forwarded.Errors);
        Assert.AreEqual(verb, forwarded.CommandResult.Command.Name);
        Assert.AreEqual(@"C:\guest\snapshot", forwarded.GetValue(CommentsSharedOptions.SourceRootOption));
        Assert.AreEqual($"123.456.{id}.{Epoch.Value}", forwarded.GetValue(WinAppRootCommand.GuestInspectionOption));
        await invocation.EmitBytesAsync(GuestStreamId.StandardOutput, Encoding.UTF8.GetBytes(
            """{"ok":true,"comment":{"id":"own"},"warning":"Comment changes were saved, but markers could not refresh."}"""));
        invocation.Exit(0);
        Assert.AreEqual(0, await running);
        using var result = JsonDocument.Parse(TestAnsiConsole.Output);
        Assert.IsTrue(result.RootElement.GetProperty("ok").GetBoolean());
        StringAssert.Contains(result.RootElement.GetProperty("warning").GetString()!, "were saved");
        Assert.AreEqual(project.FullName, result.RootElement.GetProperty("comment").GetProperty("projectRoot").GetString());
        Assert.IsTrue(backend.Processes.Started.IsEmpty);
    }

    [TestMethod]
    [DataRow("list")]
    [DataRow("get")]
    [DataRow("update")]
    [DataRow("delete")]
    public async Task SavedComments_UseChosenHostStoreWithoutConnecting(string verb)
    {
        await using var backend = new Backend();
        _tempDirectory.CreateSubdirectory(".git");
        var project = _tempDirectory.CreateSubdirectory("Project");
        File.WriteAllText(Path.Combine(project.FullName, "Main.xaml"), "<Grid><TextBlock x:Name=\"Title\" /></Grid>");
        var store = new CommentStore();
        var path = store.GetStorePath(project);
        const string exact = "  host-backed\r\n\r\nnote [keep]  ";
        store.Add(path, new Comment
        {
            Id = "own", Text = exact, ProjectRoot = project.FullName, Status = CommentStatus.Open,
            Anchor = new() { SourceFile = "Main.xaml", Identity = new() { Name = "Title" } },
        });
        store.Add(path, new Comment
        {
            Id = "foreign", Text = "DO-NOT-LEAK", ProjectRoot = Path.Combine(_tempDirectory.FullName, "Other"),
            Status = CommentStatus.Open, Anchor = new() { SourceFile = "Main.xaml", Identity = new() },
        });
        List<string> args = ["devtools", "comments", verb];
        if (verb != "list")
        {
            args.Add("own");
        }
        else
        {
            args.AddRange(["--project", project.FullName]);
        }
        if (verb == "update")
        {
            args.AddRange(["--status", "stale"]);
        }
        args.AddRange(["--source-root", project.FullName, "--json"]);
        var root = GetRequiredService<WinAppRootCommand>();
        var parsed = root.Parse([.. args]);
        Assert.IsEmpty(parsed.Errors);
        Assert.IsNull(ExecutionTargetSelection.Validate(parsed));
        Assert.IsFalse(ExecutionTargetDevToolsRouter.ShouldRoute(parsed));
        Assert.AreEqual(0, await ParseAndInvokeWithCaptureAsync(root, [.. args]));
        Assert.AreEqual(0, backend.AttachCalls);
        Assert.IsTrue(backend.Processes.Started.IsEmpty);
        Assert.IsFalse(Directory.Exists(Path.Combine(_tempDirectory.FullName, "state")));
        var output = TestAnsiConsole.Output;
        Assert.IsFalse(output.Contains("DO-NOT-LEAK", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(output);
        var comment = verb != "list" ? document.RootElement.GetProperty("comment") :
            document.RootElement.GetProperty("comments")[0];
        Assert.AreEqual(exact, comment.GetProperty("text").GetString());
        Assert.AreEqual(project.FullName, comment.GetProperty("projectRoot").GetString());
        Assert.IsFalse(document.RootElement.TryGetProperty("appSelector", out _));
        Assert.AreEqual(verb == "delete" ? 1 : 2, store.Load(path).Comments.Count);
        Assert.AreEqual("DO-NOT-LEAK", store.Get(path, "foreign")!.Text);
        if (verb == "update")
        {
            Assert.AreEqual(CommentStatus.Stale, store.Get(path, "own")!.Status);
        }
    }

    private GuestDevToolsHost Host() => new(new TargetStateDirectoryProvider(Path.Combine(_tempDirectory.FullName, "state")),
        new FakeAppLauncherService());

    private ExecutionTargetDevToolsRouter Router(Backend backend, GuestDevToolsCapabilities? expected = null) =>
        new(new(backend, new NoLocks(), new NoLocks()), Host(), new CommentStore(),
            new CurrentDirectoryProvider(_tempDirectory.FullName), TestAnsiConsole, _ => Task.FromResult(expected));

    private string CreateBundle()
    {
        var bundle = _tempDirectory.CreateSubdirectory("bundle");
        var cli = Path.Combine(bundle.FullName, "winapp.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "kernel32.dll"), cli);
        File.Copy(cli, Path.Combine(bundle.FullName, GuestDevTools.NativeFileName));
        File.Copy(GetType().Assembly.Location, Path.Combine(bundle.FullName, GuestDevTools.ManagedFileName));
        return cli;
    }

    private sealed class NoLocks : ITargetMutationLock, ITargetConnectionLock
    {
        TargetMutationLease? ITargetMutationLock.TryAcquire(ExecutionTargetRef target, TimeSpan timeout, CancellationToken cancellationToken) =>
            throw new AssertFailedException("Inspection must not take a mutation lock.");
        TargetConnectionLease? ITargetConnectionLock.TryAcquire(ExecutionTargetRef target, TimeSpan timeout, CancellationToken cancellationToken) =>
            throw new AssertFailedException("Inspect-only dispatch must not establish or repair a target.");
    }

    private sealed class Backend(string? cli = null, string architecture = "x64") : IExecutionTargetBackend, IInspectableTarget, IAsyncDisposable
    {
        private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromSeconds(30));
        private GuestCommandServer? _server;
        public ExecutionTargetRef Target => WindowsSandboxTarget.Default;
        public FakeGuestProcessHostFactory Processes { get; } = new();
        public int AttachCalls { get; private set; }
        public bool ReusedPid { get; init; }
        public bool AllowSecondProcess { get; init; }
        public int? FailProcessCheckAfter { get; init; }
        private int _processChecks;
        public Task<TargetSupportResult> ProbeSupportAsync(CancellationToken cancellationToken) =>
            throw new AssertFailedException("Inspection must not provision a target.");
        public Task<TargetConnection> EnsureConnectedAsync(EnsureTargetOptions options, CancellationToken cancellationToken) =>
            throw new AssertFailedException("Inspection must not connect or repair a target.");
        public IReadOnlyDictionary<string, string> DescribeForDiagnostics() => new Dictionary<string, string>();
        public Task<TargetAttachment> TryAttachAsync(CancellationToken cancellationToken)
        {
            AttachCalls++;
            if (cli is null)
            {
                return Task.FromResult(TargetAttachment.NotRunning);
            }
            var pair = new LoopbackTransportPair();
            _server = new(pair.Guest, Epoch, Processes, new StaticGuestSessionProbe(new(1, "WinSta0", false)),
                new("9.9.9", "test", architecture, GuestProtocol.MinimumVersion, GuestProtocol.CurrentVersion), guestWinapp: cli);
            _server.QueryProcessImpl = (pid, start) => !ReusedPid &&
                (FailProcessCheckAfter is null || Interlocked.Increment(ref _processChecks) <= FailProcessCheckAfter) &&
                (pid == 123 && start == 456 || AllowSecondProcess && pid == 124 && start == 457);
            _ = _server.RunAsync(_lifetime.Token);
            return Task.FromResult(new TargetAttachment(true, Epoch, new(Epoch, pair.Host, true)));
        }
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
