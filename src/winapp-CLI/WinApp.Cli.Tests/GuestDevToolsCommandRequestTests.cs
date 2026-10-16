// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class GuestDevToolsCommandRequestTests : BaseCommandTests
{
    private static readonly string[] CallParameters = ["handle=123", "depth:=2"];
    protected override IServiceCollection ConfigureServices(IServiceCollection services) => services;

    private static GuestDevToolsApplication Application => new("guest:503765a8388748429b5820335d0cdd1a",
        new(123, 638936747284321987), "sandbox:epoch.with.dots", "503765a8388748429b5820335d0cdd1a",
        new(@"C:\host\Project.csproj", [], @"C:\guest\snapshot"));

    [TestMethod]
    [DataRow("inspect", false)]
    [DataRow("inspect", true)]
    [DataRow("search", false)]
    [DataRow("search", true)]
    public void ForwardedLiveCommand_PreservesExplicitAttachmentConsent(string command, bool attach)
    {
        var root = GetRequiredService<WinAppRootCommand>();
        var arguments = new List<string> { "devtools", command, "--on", "sandbox", "--app", Application.Selector };
        if (command == "search") { arguments.Add("TextBlock"); }
        if (attach) { arguments.Add("--attach"); }
        var parsed = root.Parse(arguments.ToArray());
        Assert.IsEmpty(parsed.Errors);
        var request = GuestDevToolsCommandRequest.Create(parsed, Application);
        var forwarded = root.Parse(request.Arguments.ToArray());
        Assert.IsEmpty(forwarded.Errors);
        Assert.AreEqual(attach, forwarded.GetValue(SharedDevToolsOptions.AttachOption));
        Assert.IsFalse(request.RequiresRealInput);
    }

    [TestMethod]
    [DataRow("--on:sandbox", "-a")]
    [DataRow("--on=sandbox", "--app")]
    public void ForwardedTokens_DropHostRoutingInEverySpelling(string on, string app)
    {
        var root = GetRequiredService<WinAppRootCommand>();
        var parsed = root.Parse(["devtools", "inspect", on, app, Application.Selector]);
        Assert.IsEmpty(parsed.Errors);
        var request = GuestDevToolsCommandRequest.Create(parsed, Application);
        Assert.IsFalse(request.Arguments.Any(a => a.StartsWith("--on", StringComparison.Ordinal) || a == "sandbox" ||
            a == "-a" || a == Application.Selector), string.Join(" ", request.Arguments));
        Assert.IsEmpty(root.Parse(request.Arguments.ToArray()).Errors);
    }

    [TestMethod]
    [DataRow("search")]
    [DataRow("get-property")]
    [DataRow("set-property")]
    public void ForwardedQuery_PreservesAndPredicatesAndVerifiedGuestScope(string command)
    {
        var root = GetRequiredService<WinAppRootCommand>();
        var arguments = new List<string> { "devtools", command, "--on", "sandbox", "--app", Application.Selector,
            "--of-type", "TextBlock", "--with", "FontSize>=20", "--with", "Text*=Today's intentions", "--root", "9001", "--json" };
        if (command == "search")
        {
            arguments.AddRange(["--fields", "Text,FontSize"]);
        }

        if (command == "set-property")
        {
            arguments.AddRange(["--property", "Text", "--value=--literal, not an option"]);
        }

        var parsed = root.Parse(arguments.ToArray());
        Assert.IsEmpty(parsed.Errors);
        var request = GuestDevToolsCommandRequest.Create(parsed, Application);
        var forwarded = root.Parse(request.Arguments.ToArray());
        Assert.IsEmpty(forwarded.Errors);
        Assert.AreEqual("FontSize>=20|Text*=Today's intentions", string.Join('|', forwarded.GetValue(DevToolsQueryOptions.With)!));
        Assert.AreEqual("TextBlock", forwarded.GetValue(DevToolsQueryOptions.OfType));
        Assert.AreEqual("9001", forwarded.GetValue(SharedDevToolsOptions.RootOption));
        Assert.AreEqual("123", forwarded.GetValue(SharedUiOptions.AppOption));
        Assert.IsFalse(request.RequiresRealInput);
        if (command == "set-property")
        {
            Assert.AreEqual("--literal, not an option", forwarded.GetValue(DevToolsQueryOptions.Value));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ForwardedInvocation_PreservesExactCommentTextAndOwnsOnlyGuestPaths(bool confirmLikelySource)
    {
        var root = GetRequiredService<WinAppRootCommand>();
        const string Text = "--on sandbox\r\n\r\n  exact \"quoted\" text\\";
        var arguments = new List<string> { "devtools", "comments", "add", "--on=sandbox",
            "--app", Application.Selector, "--from-element", "handle:123", "--text=" + Text, "--json" };
        if (confirmLikelySource) { arguments.Add("--confirm-likely-source"); }
        var parsed = root.Parse(arguments.ToArray());
        Assert.IsEmpty(parsed.Errors);
        var request = GuestDevToolsCommandRequest.Create(parsed, Application);
        Assert.IsTrue(request.UseGuestWinapp);
        Assert.IsNull(request.Executable);
        Assert.IsFalse(request.RequiresRealInput);
        Assert.AreEqual(@"C:\guest\snapshot", request.Environment!["WINAPP_DEVTOOLS_SOURCE_ROOT"]);
        Assert.IsFalse(request.Arguments.Any(value => value.Contains(@"C:\host", StringComparison.Ordinal)));
        var forwarded = root.Parse(request.Arguments.ToArray());
        Assert.IsEmpty(forwarded.Errors);
        Assert.AreEqual(Text, forwarded.GetValue(DevToolsCommentsAddCommand.TextOption));
        Assert.AreEqual("123", forwarded.GetValue(DevToolsCommentsAddCommand.AppOption));
        Assert.AreEqual("handle:123", forwarded.GetValue(DevToolsCommentsAddCommand.FromElementOption));
        Assert.AreEqual(confirmLikelySource, forwarded.GetValue(DevToolsCommentsAddCommand.ConfirmLikelySourceOption));
        Assert.IsTrue(ExecutionTargetSelection.Resolve(forwarded).IsLocal);
        Assert.AreEqual("123.638936747284321987.503765a8388748429b5820335d0cdd1a.sandbox:epoch.with.dots",
            forwarded.GetValue(WinAppRootCommand.GuestInspectionOption));
    }

    [TestMethod]
    [DataRow("open")]
    [DataRow("resolved")]
    [DataRow("stale")]
    [DataRow("dismissed")]
    public void ForwardedUpdate_PreservesStatusMetadataAndQualifiedAuthority(string status)
    {
        var root = GetRequiredService<WinAppRootCommand>();
        const string note = "--text is not an option\r\n  preserve \"quotes\"";
        var parsed = root.Parse(["devtools", "comments", "update", "review[1]", "--status", status,
            "--note=" + note, "--by", "reviewer", "--on", "sandbox",
            "--app", Application.Selector, "--source-root", @"C:\host", "--json"]);
        Assert.IsEmpty(parsed.Errors);
        var request = GuestDevToolsCommandRequest.Create(parsed, Application);
        var forwarded = root.Parse(request.Arguments.ToArray());
        Assert.IsEmpty(forwarded.Errors);
        Assert.IsInstanceOfType<DevToolsCommentsUpdateCommand>(forwarded.CommandResult.Command);
        Assert.AreEqual(status, forwarded.GetValue(DevToolsCommentsUpdateCommand.StatusOption));
        Assert.AreEqual(note, forwarded.GetValue(DevToolsCommentsUpdateCommand.NoteOption));
        Assert.AreEqual("reviewer", forwarded.GetValue(DevToolsCommentsUpdateCommand.ByOption));
        Assert.AreEqual("review[1]", forwarded.GetValue(DevToolsCommentsUpdateCommand.IdArgument));
        Assert.AreEqual("123", forwarded.GetValue(DevToolsCommentsUpdateCommand.AppOption));
        Assert.AreEqual(@"C:\guest\snapshot", forwarded.GetValue(CommentsSharedOptions.SourceRootOption));
        Assert.AreEqual("123.638936747284321987.503765a8388748429b5820335d0cdd1a.sandbox:epoch.with.dots",
            forwarded.GetValue(WinAppRootCommand.GuestInspectionOption));
        Assert.IsFalse(request.RequiresRealInput);
    }

    [TestMethod]
    public void ForwardedCall_PreservesPositionalParametersAndHasNoHostRuntimeOverride()
    {
        var root = GetRequiredService<WinAppRootCommand>();
        var parsed = root.Parse(["devtools", "call", "VisualTree.get",
            "--app", Application.Selector, "--on", "sandbox", "--json", "--", "handle=123", "depth:=2"]);
        var request = GuestDevToolsCommandRequest.Create(parsed, Application);
        var forwarded = root.Parse(request.Arguments.ToArray());
        Assert.IsEmpty(forwarded.Errors);
        Assert.AreEqual("VisualTree.get", forwarded.GetValue(DevToolsCallCommand.MethodArgument));
        CollectionAssert.AreEqual(CallParameters, forwarded.GetValue(DevToolsCallCommand.ParamsArgument));
        Assert.AreEqual(1, request.Environment!.Count);
    }

    [TestMethod]
    [DataRow("handoff")]
    [DataRow("resolve c1")]
    [DataRow("flag c1 --status stale")]
    [DataRow("update c1")]
    [DataRow("update c1 --status resolved --commit abc123")]
    public void RemovedCommandsAndMissingStatus_CannotBecomeGuestRequests(string command)
    {
        var parsed = GetRequiredService<WinAppRootCommand>().Parse(
            $"devtools comments {command} --on sandbox --app {Application.Selector}");
        Assert.IsNotEmpty(parsed.Errors);
        Assert.ThrowsExactly<InvalidOperationException>(() => GuestDevToolsCommandRequest.Create(parsed, Application));
    }

    [TestMethod]
    [DataRow("--help")]
    [DataRow("--cli-schema")]
    [DataRow("--depth=0")]
    [DataRow("--depth=invalid")]
    [DataRow("--ancestors")]
    [DataRow("--window=123")]
    [DataRow("--guest-inspection=foreign")]
    public void InvalidOrMetaAction_DoesNotBecomeAGuestRequest(string option)
    {
        var parsed = GetRequiredService<WinAppRootCommand>().Parse(
            ["devtools", "inspect", "--app", Application.Selector, "--on", "sandbox", option]);
        Assert.ThrowsExactly<InvalidOperationException>(() => GuestDevToolsCommandRequest.Create(parsed, Application));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ActualChannel_RefusesReusedPidOrExecutesScopedGuestCliOnly(bool reused)
    {
        var root = GetRequiredService<WinAppRootCommand>();
        var cli = Path.Combine(_tempDirectory.FullName, "winapp.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "kernel32.dll"), cli);
        File.Copy(cli, Path.Combine(_tempDirectory.FullName, GuestDevTools.NativeFileName));
        File.Copy(GetType().Assembly.Location, Path.Combine(_tempDirectory.FullName, GuestDevTools.ManagedFileName));
        var expected = (await GuestDevTools.ReadCapabilitiesAsync(cli, TestContext.CancellationToken))!;
        var identity = new GuestAgentIdentity("9.9.9", "abc123", expected.Architecture,
            GuestProtocol.MinimumVersion, GuestProtocol.CurrentVersion);
        await using var harness = new GuestCommandServerTests.Harness(new(1, "WinSta0", true),
            guestWinapp: cli, agentIdentity: identity);
        harness.Server.QueryProcessImpl = (pid, start) => !reused && pid == 123 && start == 456;
        var epoch = ExecutionTargetEpoch.Create("sandbox-1", "nonce-a");
        var target = new PreparedTarget(new("sandbox", "default"), harness.Channel, epoch,
            await harness.Channel.GetCapabilitiesAsync(harness.Token), true);
        var host = new GuestDevToolsHost(new TargetStateDirectoryProvider(_tempDirectory.FullName), new FakeAppLauncherService());
        var selector = GuestDevToolsSelector.ForProcess(target.Reference, epoch, new(123, 456));
        var parsed = root.Parse(["devtools", "inspect", "--app", selector, "--on", "sandbox", "--json"]);
        var running = GuestDevToolsCommandRequest.ExecuteAsync(parsed, target, host, expected, selector,
            new GuestExecCallbacks(), harness.Token);
        if (reused)
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => running);
            Assert.IsTrue(harness.Processes.Started.IsEmpty);
            return;
        }
        var process = await harness.Processes.WaitForNextAsync(harness.Token);
        Assert.AreEqual(cli, process.Request.Executable);
        var forwarded = root.Parse(process.Request.Arguments.ToArray());
        Assert.IsEmpty(forwarded.Errors);
        Assert.AreEqual("123", forwarded.GetValue(SharedUiOptions.AppOption));
        Assert.AreEqual($"123.456..{epoch.Value}", forwarded.GetValue(WinAppRootCommand.GuestInspectionOption));
        Assert.AreEqual(string.Empty, process.Request.Environment!["WINAPP_DEVTOOLS_SOURCE_ROOT"]);
        process.Exit(0);
        var outcome = await running;
        Assert.AreEqual(0, outcome.Result.ExitCode);
        Assert.AreEqual(new GuestProcessStart(123, 456), outcome.Application.Process);
    }

    [TestMethod]
    public async Task ActualDiscovery_EmitsOnlyCurrentScopedSelectorsWithoutInjection()
    {
        var cli = Path.Combine(_tempDirectory.FullName, "winapp.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "kernel32.dll"), cli);
        File.Copy(cli, Path.Combine(_tempDirectory.FullName, GuestDevTools.NativeFileName));
        File.Copy(GetType().Assembly.Location, Path.Combine(_tempDirectory.FullName, GuestDevTools.ManagedFileName));
        var expected = (await GuestDevTools.ReadCapabilitiesAsync(cli, TestContext.CancellationToken))!;
        await using var harness = new GuestCommandServerTests.Harness(new(1, "WinSta0", true),
            guestWinapp: cli, agentIdentity: new("9.9.9", "abc123", expected.Architecture,
                GuestProtocol.MinimumVersion, GuestProtocol.CurrentVersion));
        harness.Server.QueryProcessImpl = (pid, start) => pid == 123 && start == 456;
        var epoch = ExecutionTargetEpoch.Create("sandbox-1", "nonce-a");
        var target = new PreparedTarget(new("sandbox", "default"), harness.Channel, epoch,
            await harness.Channel.GetCapabilitiesAsync(harness.Token), true);
        var host = new GuestDevToolsHost(new TargetStateDirectoryProvider(_tempDirectory.FullName), new FakeAppLauncherService());
        var running = GuestDevToolsCommandRequest.DiscoverAsync(target, host, expected, true, harness.Token);
        var process = await harness.Processes.WaitForNextAsync(harness.Token);
        var forwarded = GetRequiredService<WinAppRootCommand>().Parse(process.Request.Arguments.ToArray());
        Assert.IsEmpty(forwarded.Errors);
        Assert.IsInstanceOfType<DevToolsListCommand>(forwarded.CommandResult.Command);
        Assert.IsTrue(forwarded.GetValue(DevToolsListCommand.GuestDiscoveryOption));
        Assert.IsTrue(forwarded.GetValue(DevToolsListCommand.IncludeAvailableOption));
        Assert.IsFalse(process.Request.RequiresRealInput);
        await process.EmitBytesAsync(GuestStreamId.StandardOutput, System.Text.Encoding.UTF8.GetBytes(
            """{"apps":[{"processId":123,"startTicksUtc":"456","appSelector":"guest:forged","attached":false},{"processId":124,"startTicksUtc":"457"},{"processId":125}]}"""));
        process.Exit(0);
        var apps = (await running).Apps;
        Assert.AreEqual($"guest-process:123:456:{epoch.Value}", apps[0].AppSelector);
        Assert.IsNull(apps[1].AppSelector);
        Assert.AreEqual("exited", apps[1].Status);
        Assert.IsNull(apps[2].AppSelector);
        Assert.AreEqual("identity-unavailable", apps[2].Status);
        Assert.IsTrue(harness.Processes.Started.IsEmpty, "No process besides the consumed read-only discovery CLI was executed.");
    }
}
