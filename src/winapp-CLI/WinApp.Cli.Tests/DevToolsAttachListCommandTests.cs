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

    [TestMethod]
    public void List_WinUiCandidateDetection_UsesRuntimeModuleNotProcessName()
    {
        Assert.IsTrue(DevToolsListCommand.Handler.IsWinUiModule("Microsoft.UI.Xaml.dll"));
        Assert.IsTrue(DevToolsListCommand.Handler.IsWinUiModule("microsoft.ui.xaml.DLL"));
        Assert.IsFalse(DevToolsListCommand.Handler.IsWinUiModule("WinUI-looking-process.exe"));
        Assert.IsFalse(DevToolsListCommand.Handler.IsWinUiModule("PresentationFramework.dll"));
        Assert.IsFalse(DevToolsListCommand.Handler.IsWinUiModule(null));
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
