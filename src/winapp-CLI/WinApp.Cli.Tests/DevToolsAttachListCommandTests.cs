// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;
using WinApp.Cli.Commands;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

/// <summary>
/// Covers the <c>winapp devtools attach</c> / <c>devtools list</c> command surfaces added for external
/// protocol clients: the attach pre-flight failure modes (bad pid, absent process) that must be
/// diagnosable rather than silent, and that both commands honour the <c>--json</c> idiom. The happy-path
/// inject is exercised by the live E2E demo, not here (it needs a running WinUI app).
/// </summary>
[TestClass]
public class DevToolsAttachListCommandTests : BaseCommandTests
{
    private readonly CommentTestTargetResolver _targetResolver = new(2000000000);
    protected override IServiceCollection ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<IUiTargetResolver>(_targetResolver);

    [TestMethod]
    [DataRow("Daylight")]
    [DataRow("123")]
    [DataRow("Today's Tasks")]
    public async Task Attach_AppUsesSharedUiResolverWithoutAnIndependentParser(string app)
    {
        var command = GetRequiredService<DevToolsAttachCommand>();
        Assert.AreEqual(1, await ParseAndInvokeWithCaptureAsync(command, ["--app", app, "--json"]));
        Assert.AreEqual(app, _targetResolver.LastApp);
        Assert.IsTrue(_targetResolver.ProcessOnlyRequested);
        StringAssert.Contains(TestAnsiConsole.Output, "2000000000 is not running");
    }

    [TestMethod]
    public async Task Attach_ConflictingAppAndPidFailsBeforeResolution()
    {
        var command = GetRequiredService<DevToolsAttachCommand>();
        Assert.AreEqual(1, await ParseAndInvokeWithCaptureAsync(command, ["--app", "Daylight", "--pid", "123", "--json"]));
        Assert.IsNull(_targetResolver.LastApp);
        StringAssert.Contains(TestAnsiConsole.Output, "not both");
    }

    [TestMethod]
    public async Task Attach_ZeroPid_Json_ReportsInvalidPid()
    {
        var command = GetRequiredService<DevToolsAttachCommand>();

        var exit = await ParseAndInvokeWithCaptureAsync(command, ["--pid", "0", "--json"]);

        Assert.AreEqual(1, exit);
        var output = TestAnsiConsole.Output;
        StringAssert.Contains(output, "\"ok\": false", "Failure JSON must report ok:false.");
        StringAssert.Contains(output, "Invalid --pid", "The message must name the bad pid so it is diagnosable.");
    }

    [TestMethod]
    public async Task Attach_NonexistentPid_Json_ReportsNotRunning()
    {
        // A pid that is well-formed but not a live process must fail with a clear "not running" reason,
        // never a confusing FrameworkUdk/inject error.
        var command = GetRequiredService<DevToolsAttachCommand>();

        var exit = await ParseAndInvokeWithCaptureAsync(command, ["--pid", "2000000000", "--json"]);

        Assert.AreEqual(1, exit);
        var output = TestAnsiConsole.Output;
        StringAssert.Contains(output, "\"ok\": false");
        StringAssert.Contains(output, "not running", "An absent process must be surfaced as not-running.");
    }

    [TestMethod]
    public void Attach_SurfaceFlags_AreOptIn()
    {
        var command = GetRequiredService<DevToolsAttachCommand>();

        var headless = command.Parse(["--pid", "42"]);
        Assert.IsFalse(headless.GetValue(DevToolsAttachCommand.OverlayOption));
        Assert.IsFalse(headless.GetValue(DevToolsAttachCommand.ShowWindowOption));

        var interactive = command.Parse(["--pid", "42", "--overlay", "--show-window"]);
        Assert.IsTrue(interactive.GetValue(DevToolsAttachCommand.OverlayOption));
        Assert.IsTrue(interactive.GetValue(DevToolsAttachCommand.ShowWindowOption));
    }

    /// <summary>
    /// attach exposes no access-mode flag, and the UI-opening option is <c>--show-window</c> so that
    /// <c>--window/-w</c> can keep meaning "target this HWND" everywhere in the CLI.
    /// </summary>
    [TestMethod]
    public void Attach_HasNoAccessModeFlag_AndDoesNotClaimWindowForTargeting()
    {
        var command = GetRequiredService<DevToolsAttachCommand>();
        var names = command.Options.SelectMany(o => o.Aliases.Append(o.Name)).ToArray();

        CollectionAssert.DoesNotContain(names, "--allow-mutation",
            "Every DevTools connection is writable; there is no access-mode choice to expose.");
        CollectionAssert.DoesNotContain(names, "--window",
            "--window/-w means an HWND across the CLI; the inspector-opening flag is --show-window.");
        CollectionAssert.Contains(names, "--show-window");
    }

    [TestMethod]
    public void Attach_ExistingPostureCannotBeRaised()
    {
        Assert.IsTrue(DevToolsAttachCommand.Handler.Allows("mutation", DevToolsAccess.Read));
        Assert.IsTrue(DevToolsAttachCommand.Handler.Allows("mutation", DevToolsAccess.Ui));
        Assert.IsFalse(DevToolsAttachCommand.Handler.Allows("read", DevToolsAccess.Ui));
        Assert.IsFalse(DevToolsAttachCommand.Handler.Allows("read", DevToolsAccess.Mutation));
        Assert.IsFalse(DevToolsAttachCommand.Handler.Allows("ui", DevToolsAccess.Mutation));
    }

    [TestMethod]
    public async Task List_Json_EmitsAppsArray()
    {
        var command = GetRequiredService<DevToolsListCommand>();
        var handler = new DevToolsListCommand.Handler(TestAnsiConsole,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DevToolsListCommand>.Instance, () => []);
        var exit = await handler.InvokeAsync(command.Parse(["--json"]));

        Assert.AreEqual(0, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "\"apps\"", "list --json must emit an apps array.");
    }

    [TestMethod]
    public async Task List_PipedOutputKeepsOneLinePerApp()
    {
        var command = GetRequiredService<DevToolsListCommand>();
        var console = new Spectre.Console.Testing.TestConsole();
        console.Profile.Width = 30;
        var handler = new DevToolsListCommand.Handler(console,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DevToolsListCommand>.Instance, () => [Environment.ProcessId]);
        Assert.AreEqual(0, await handler.InvokeAsync(command.Parse([])));
        var lines = console.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.HasCount(1, lines, console.Output);
        StringAssert.StartsWith(lines[0], Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public void List_IncludeAvailable_IsOptIn()
    {
        var command = GetRequiredService<DevToolsListCommand>();

        Assert.IsFalse(command.Parse([]).GetValue(DevToolsListCommand.IncludeAvailableOption));
        Assert.IsTrue(command.Parse(["--include-available"]).GetValue(DevToolsListCommand.IncludeAvailableOption));
    }

    // WinUI 2 apps and the Windows shell load a Microsoft.UI.Xaml.dll too, so the file name alone offered explorer and
    // Store apps as "ready to attach".
    [TestMethod]
    [DataRow(@"C:\Apps\Gallery\Gallery.exe", "Microsoft.UI.Xaml.dll,Microsoft.Internal.FrameworkUdk.dll", null)]
    [DataRow(@"C:\Apps\Selfcontained\App.exe", "Microsoft.UI.Xaml.dll,Microsoft.WindowsAppRuntime.dll", null)]
    [DataRow(@"C:\Windows\explorer.exe", "Microsoft.UI.Xaml.dll,Microsoft.Internal.FrameworkUdk.dll", "is part of Windows")]
    [DataRow(@"C:\Windows\SystemApps\LockApp\LockApp.exe", "Microsoft.UI.Xaml.dll", "is part of Windows")]
    [DataRow(@"C:\Program Files\WindowsApps\Store.App\App.exe", "Windows.UI.Xaml.dll,Microsoft.UI.Xaml.dll", "uses WinUI 2")]
    [DataRow(@"C:\Apps\Wpf\Wpf.exe", "PresentationFramework.dll", "has not loaded WinUI 3")]
    public void AttachTarget_IsOnlyAWinUi3AppFromTheWindowsAppSdk(string exe, string modules, string? refusal)
    {
        var why = DevToolsAttachTarget.WhyNot(42, "App", exe, modules.Split(','), @"C:\Windows");
        if (refusal is null)
        {
            Assert.IsNull(why);
        }
        else
        {
            StringAssert.Contains(why, refusal);
            StringAssert.Contains(why, "App (PID 42)");
        }
    }

    // An app attached after launch has no project folder, so a comment left in it has nowhere to be saved.
    [TestMethod]
    [DoNotParallelize]
    public async Task Attach_SourceRoot_GivesTheAttachedAppItsProjectFolder()
    {
        var root = Directory.CreateTempSubdirectory("winapp-attach-root-");
        try
        {
            using var agent = new FakeDevToolsProtocolAgent()
                .Answer("DevTools.negotiate", $$"""{"protocol":"winapp-devtools","protocolVersion":"0","processId":{{Environment.ProcessId}},"mutation":true,"posture":"mutation"}""")
                .Answer("Internal.setSourceRoot", "null")
                .Answer("Internal.setComments", """{"total":0,"placed":0}""");
            var handler = new DevToolsAttachCommand.Handler(new DevToolsTargetResolverTests.FakeAttach(),
                GetRequiredService<WinApp.Cli.Services.IStatusService>(), TestAnsiConsole,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<DevToolsAttachCommand>.Instance, _targetResolver,
                new WinApp.Cli.Services.DevTools.Comments.CommentPusher(new WinApp.Cli.Services.DevTools.Comments.CommentStore()),
                whyNotAttachable: _ => null);
            var command = GetRequiredService<DevToolsAttachCommand>();

            var exit = await handler.InvokeAsync(command.Parse(
                ["--pid", agent.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "--source-root", root.FullName, "--json"]));

            Assert.AreEqual(0, exit, TestAnsiConsole.Output);
            using var request = JsonDocument.Parse(agent.ReceivedRequests.Single(r => r.Contains("Internal.setSourceRoot", StringComparison.Ordinal)));
            Assert.AreEqual(root.FullName, request.RootElement.GetProperty("params").GetProperty("path").GetString());
            Assert.IsTrue(agent.Received.Contains("Internal.setComments"), "comments saved there show as markers right away");
        }
        finally { root.Delete(recursive: true); }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Attach_SourceRootInsideARepository_UsesTheRepositoryRootStoreLikeRun()
    {
        // `run` gives the app its project folder, and the writer saves to .winapp at the repository root containing
        // it. Attach with the same folder must read and write that same store, so the same comments show as markers.
        var repo = Directory.CreateTempSubdirectory("winapp-attach-repo-");
        try
        {
            Directory.CreateDirectory(Path.Combine(repo.FullName, ".git"));
            var project = Directory.CreateDirectory(Path.Combine(repo.FullName, "src", "App"));
            var store = new WinApp.Cli.Services.DevTools.Comments.CommentStore();
            var storePath = store.GetStorePath(project);
            Assert.AreEqual(Path.Combine(repo.FullName, ".winapp", "ui-comments.json"), storePath);
            store.Add(storePath, new WinApp.Cli.Services.DevTools.Comments.Comment
            {
                Id = "from-run", Text = "made while the app ran under winapp run", Status = WinApp.Cli.Services.DevTools.Comments.CommentStatus.Open, ProjectRoot = project.FullName,
                Anchor = new WinApp.Cli.Services.DevTools.Comments.CommentAnchor
                {
                    SourceFile = "MainWindow.xaml", ElementPath = "Root/0",
                    Identity = new WinApp.Cli.Services.DevTools.Comments.CommentIdentity { Type = "Button" },
                },
            });
            using var agent = new FakeDevToolsProtocolAgent()
                .Answer("DevTools.negotiate", $$"""{"protocol":"winapp-devtools","protocolVersion":"0","processId":{{Environment.ProcessId}},"mutation":true,"posture":"mutation"}""")
                .Answer("Internal.setSourceRoot", "null")
                .Answer("Internal.setComments", """{"total":1,"placed":1}""");
            var handler = new DevToolsAttachCommand.Handler(new DevToolsTargetResolverTests.FakeAttach(),
                GetRequiredService<WinApp.Cli.Services.IStatusService>(), TestAnsiConsole,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<DevToolsAttachCommand>.Instance, _targetResolver,
                new WinApp.Cli.Services.DevTools.Comments.CommentPusher(store), whyNotAttachable: _ => null);

            var exit = await handler.InvokeAsync(GetRequiredService<DevToolsAttachCommand>().Parse(
                ["--pid", agent.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "--source-root", project.FullName, "--json"]));

            Assert.AreEqual(0, exit, TestAnsiConsole.Output);
            using var set = JsonDocument.Parse(agent.ReceivedRequests.Single(r => r.Contains("Internal.setSourceRoot", StringComparison.Ordinal)));
            Assert.AreEqual(project.FullName, set.RootElement.GetProperty("params").GetProperty("path").GetString(),
                "the in-app writer passes this folder to comments add, which resolves the same repository-root store");
            using var pushed = JsonDocument.Parse(agent.ReceivedRequests.Single(r => r.Contains("Internal.setComments", StringComparison.Ordinal)));
            var rows = pushed.RootElement.GetProperty("params").GetProperty("comments");
            Assert.AreEqual(1, rows.GetArrayLength());
            Assert.AreEqual("from-run", rows[0].GetProperty("id").GetString());
        }
        finally { repo.Delete(recursive: true); }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Attach_WithoutAProjectFolder_SaysHowToGiveOne()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("DevTools.negotiate", $$"""{"protocol":"winapp-devtools","protocolVersion":"0","processId":{{Environment.ProcessId}},"mutation":true,"posture":"mutation"}""")
            .Answer("Internal.sourceRoot", """{"sourceRoot":""}""");
        var handler = new DevToolsAttachCommand.Handler(new DevToolsTargetResolverTests.FakeAttach(),
            GetRequiredService<WinApp.Cli.Services.IStatusService>(), TestAnsiConsole,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DevToolsAttachCommand>.Instance, _targetResolver,
            whyNotAttachable: _ => null);
        var pid = agent.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var exit = await handler.InvokeAsync(GetRequiredService<DevToolsAttachCommand>().Parse(["--pid", pid]));

        Assert.AreEqual(0, exit, TestAnsiConsole.Output);
        var hints = TestAnsiConsole.Output.Split('\n').Where(l => l.Contains("--source-root", StringComparison.Ordinal)).ToList();
        Assert.AreEqual(1, hints.Count, TestAnsiConsole.Output);
        StringAssert.Contains(hints[0], "Run attach again with --source-root <project folder>.");
    }

    [TestMethod]
    public async Task Attach_MissingSourceRoot_FailsBeforeLoadingAnything()
    {
        var devTools = new DevToolsTargetResolverTests.FakeAttach();
        var handler = new DevToolsAttachCommand.Handler(devTools, GetRequiredService<WinApp.Cli.Services.IStatusService>(),
            TestAnsiConsole, Microsoft.Extensions.Logging.Abstractions.NullLogger<DevToolsAttachCommand>.Instance, _targetResolver);
        var missing = Path.Join(Path.GetTempPath(), "winapp-no-such-root-" + Guid.NewGuid().ToString("N"));

        var exit = await handler.InvokeAsync(GetRequiredService<DevToolsAttachCommand>().Parse(["--pid", "42", "--source-root", missing, "--json"]));

        Assert.AreEqual(1, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "The source root");
        Assert.IsFalse(devTools.WasCalled);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Attach_RefusesAProcessThatIsNotAWinUi3App_BeforeLoadingAnything()
    {
        var devTools = new DevToolsTargetResolverTests.FakeAttach();
        var handler = new DevToolsAttachCommand.Handler(devTools, GetRequiredService<WinApp.Cli.Services.IStatusService>(),
            TestAnsiConsole, Microsoft.Extensions.Logging.Abstractions.NullLogger<DevToolsAttachCommand>.Instance, _targetResolver);
        var command = GetRequiredService<DevToolsAttachCommand>();

        var exit = await handler.InvokeAsync(command.Parse(["--pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json"]));

        Assert.AreEqual(1, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "has not loaded WinUI 3");
        StringAssert.Contains(TestAnsiConsole.Output, "Nothing was loaded into that process.");
        Assert.IsFalse(devTools.WasCalled, "a test host is not a WinUI 3 app, so nothing is injected");
    }

    [TestMethod]
    public void List_Json_DistinguishesAttachedAndAvailableRows()
    {
        var payload = new DevToolsListPayload
        {
            Apps =
            [
                new()
                {
                    Pid = 100,
                    ProcessName = "AvailableApp",
                    WindowTitle = "Available window",
                    Attached = false,
                    Status = "available",
                },
                new()
                {
                    Pid = 200,
                    PipeName = "winapp-devtools-200",
                    ProcessName = "AttachedApp",
                    WindowTitle = "Attached window",
                    Attached = true,
                    Status = "attached",
                    Responsive = true,
                    ProtocolVersion = "0",
                    Mutation = true,
                    Posture = "mutation",
                    NodeCount = 42,
                },
            ],
        };

        var json = JsonSerializer.Serialize(payload, DevToolsProtocolJsonContext.Default.DevToolsListPayload);
        using var document = JsonDocument.Parse(json);
        var rows = document.RootElement.GetProperty("apps");

        Assert.AreEqual(2, rows.GetArrayLength());
        Assert.AreEqual("available", rows[0].GetProperty("status").GetString());
        Assert.IsFalse(rows[0].GetProperty("attached").GetBoolean());
        Assert.IsFalse(rows[0].TryGetProperty("pipeName", out _));
        Assert.AreEqual("attached", rows[1].GetProperty("status").GetString());
        Assert.IsTrue(rows[1].GetProperty("attached").GetBoolean());
        Assert.AreEqual("winapp-devtools-200", rows[1].GetProperty("pipeName").GetString());
        Assert.AreEqual(42, rows[1].GetProperty("nodeCount").GetInt32());
    }
}
