// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public class DevToolsCliConsistencyTests : BaseCommandTests
{
    [TestMethod]
    [DataRow("add")]
    [DataRow("update")]
    [DataRow("delete")]
    public void CommentsCommandsAcceptTheShortAppAlias(string verb)
    {
        var parse = GetRequiredService<WinAppRootCommand>().Parse(["devtools", "comments", verb, "-a", "1234"]);
        Assert.IsFalse(parse.Errors.Any(error => error.Message.Contains("-a", StringComparison.Ordinal)),
            string.Join("; ", parse.Errors.Select(error => error.Message)));
        var option = verb switch
        {
            "add" => DevToolsCommentsAddCommand.AppOption,
            "update" => DevToolsCommentsUpdateCommand.AppOption,
            _ => DevToolsCommentsDeleteCommand.AppOption,
        };
        Assert.AreEqual("1234", parse.GetValue(option));
    }

    [TestMethod]
    [DataRow("list", false)]
    [DataRow("get", false)]
    [DataRow("update", true)]
    public async Task HelpShowsOnOnlyWhereTheCommandAcceptsIt(string verb, bool shown)
    {
        Assert.AreEqual(0, await ParseAndInvokeWithCaptureAsync(GetRequiredService<WinAppRootCommand>(),
            ["devtools", "comments", verb, "--help"]));
        Assert.AreEqual(shown, TestAnsiConsole.Output.Contains("--on <on>", StringComparison.Ordinal), TestAnsiConsole.Output);
        Assert.IsFalse(ExecutionTargetSelection.OnOption.Hidden, "Hiding --on for one help screen must not leak.");
    }

    [TestMethod]
    [DataRow(new[] { "SaveButton", "Width", "200" }, "Width", "200", false)]
    [DataRow(new[] { "SaveButton", "200", "-p", "Width" }, "Width", "200", false)]
    [DataRow(new[] { "SaveButton", "Width", "200", "-p", "Height" }, "Height", "200", true)]
    public void SetPropertyTakesThePropertyPositionallyOrWithTheOption(string[] args, string property, string value, bool conflict)
    {
        var parse = new DevToolsSetPropertyCommand().Parse(args);
        Assert.AreEqual((property, value, conflict), DevToolsSetPropertyCommand.ResolvePropertyAndValue(parse));
    }

    [TestMethod]
    public void GetPropertyTakesThePropertyPositionally()
    {
        var parse = new DevToolsGetPropertyCommand().Parse(["SaveButton", "Text"]);
        Assert.AreEqual("SaveButton", parse.GetValue(DevToolsGetPropertyCommand.SelectorArgument));
        Assert.AreEqual("Text", parse.GetValue(DevToolsGetPropertyCommand.PropertyArgument));
    }

    [TestMethod]
    public void StagingLockNamesTheRunningProcess()
    {
        var denied = new UnauthorizedAccessException("Access to the path is denied.") { HResult = unchecked((int)0x80070005) };
        StringAssert.Contains(RunFailure.Describe(denied, [8316, 24984]), "still running from this build (PID 8316, 24984)");
        StringAssert.Contains(RunFailure.Describe(denied, []), "Access was denied");
        Assert.IsFalse(RunFailure.Describe(new Win32Exception(2), [8316]).Contains("8316", StringComparison.Ordinal),
            "Only a lock failure is blamed on a running process.");
        CollectionAssert.Contains(RunFailure.ProcessesRunningFrom(Environment.ProcessPath!).ToArray(), Environment.ProcessId);
        Assert.AreEqual(0, RunFailure.ProcessesRunningFrom(Path.Combine(Path.GetTempPath(), "winapp-not-running.exe")).Count);
    }

    [TestMethod]
    public void DevToolsPayloadsNameTheProcessProcessId()
    {
        var list = System.Text.Json.JsonSerializer.Serialize(new DevToolsListPayload { Apps = [new() { Pid = 7 }] },
            DevToolsProtocolJsonContext.Default.DevToolsListPayload);
        var attach = System.Text.Json.JsonSerializer.Serialize(new DevToolsAttachPayload { Ok = true, Pid = 7 },
            DevToolsProtocolJsonContext.Default.DevToolsAttachPayload);
        foreach (var json in new[] { list, attach, DevToolsJson.Result(7, "{}"), DevToolsJson.Error(7, "failed") })
        {
            StringAssert.Contains(json, "\"processId\": 7");
            Assert.IsFalse(json.Contains("\"pid\"", StringComparison.Ordinal), json);
        }
    }

    [TestMethod]
    public void IdenticalSourceWarningsCollapseToOneEntry()
    {
        var warnings = Enumerable.Range(0, 94)
            .Select(i => new XamlSourceExclusion($"Pages/P{i}.xaml", $"Pages/P{i}.xaml", XamlCoordinateMap.StaleBuild))
            .Append(new XamlSourceExclusion("Other.xaml", "Other.xaml", "different"))
            .ToArray();
        var collapsed = XamlSourceExclusion.Collapse(warnings)!;
        Assert.HasCount(2, collapsed);
        Assert.AreEqual(94, collapsed[0].Count);
        StringAssert.Contains(collapsed[0].Reason, "Rebuild");
        Assert.IsNull(collapsed[1].Count);
        Assert.IsNull(XamlSourceExclusion.Collapse([]));
    }
}
