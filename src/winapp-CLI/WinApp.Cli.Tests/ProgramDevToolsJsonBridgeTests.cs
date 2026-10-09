// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public class ProgramDevToolsJsonBridgeTests : BaseCommandTests
{
    [TestMethod]
    [DataRow("")]
    [DataRow("0")]
    [DataRow("--on=sandbox")]
    [DataRow("-1.5")]
    public void NpmPropertyLiterals_AfterSeparatorRemainLocalValues(string value)
    {
        var parse = GetRequiredService<Commands.WinAppRootCommand>().Parse(
            ["devtools", "set-property", "--app", "12345", "--property", "Text", "--", "StatusText", value]);
        Assert.IsEmpty(parse.Errors);
        Assert.IsEmpty(Helpers.WindowsCommandLine.FindOptionLikePositionals(parse));
        Assert.IsFalse(Commands.ExecutionTargetSelection.WasSupplied(parse));
        Assert.AreEqual(value, Commands.DevToolsSetPropertyCommand.ResolvePropertyAndValue(parse).Value);
    }

    [TestMethod]
    public async Task Correction_Cancellation_IsNotInternalError()
    {
        var parse = GetRequiredService<Commands.WinAppRootCommand>().Parse(["devtools", "inspect", "--json"]);
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.AreEqual(1, await Program.RunWithTelemetryAsync(parse, true,
                () => throw new OperationCanceledException("The operation was cancelled.")));
        }
        finally { Console.SetOut(original); }
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.AreEqual("cancelled", doc.RootElement.GetProperty("error").GetProperty("token").GetString());
    }

    [TestMethod]
    [DataRow(new[] { "devtools", "attach", "--pid", "not-a-pid", "--json" }, false)]
    [DataRow(new[] { "devtools", "attach", "--pid", "1", "--overlay=bogus", "--json" }, false)]
    [DataRow(new[] { "devtools", "inspect", "-app", "owned", "--json" }, false)]
    [DataRow(new[] { "devtools", "inspect", "--json=bogus" }, false)]
    [DataRow(new[] { "devtools", "comments", "add", "--text", "note", "--line", "nope", "--json" }, true)]
    [DataRow(new[] { "devtools", "comments", "add", "--text", "note", "--from-selection=bogus", "--json" }, true)]
    [DataRow(new[] { "devtools", "comments", "list", "-source-root", "owned", "--json" }, true)]
    [DataRow(new[] { "devtools", "inspect", "--onn", "sandbox", "--json" }, false)]
    [DataRow(new[] { "devtools", "attach", "--pid", "12345", "--on", "sandbox", "--json" }, false)]
    [DataRow(new[] { "devtools", "inspect", "-a", "12345", "--on", "unknown-target", "--json" }, false)]
    [DataRow(new[] { "devtools", "comments", "list", "--on", "sandbox", "--json" }, true)]
    [DataRow(new[] { "devtools", "comments", "list", "--guest-comments", "invalid", "--source-root", @"C:\never", "--json" }, true,
        ExecutionTargets.GuestAgent.GuestCommentContext.ContextFailureExitCode)]
    public async Task DevTools_ParseFailures_UseTheirActualCommandEnvelope(string[] args, bool comments, int expectedExit = 1)
    {
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(args);
        Assert.AreEqual(expectedExit, exitCode);
        Assert.AreEqual(string.Empty, stderr.Trim(), stderr);
        using var doc = JsonDocument.Parse(stdout);
        Assert.IsFalse(doc.RootElement.GetProperty("ok").GetBoolean());
        var error = doc.RootElement.GetProperty("error");
        if (comments || args[1] == "attach")
        {
            Assert.AreEqual(JsonValueKind.String, error.ValueKind);
            Assert.IsFalse(string.IsNullOrWhiteSpace(error.GetString()));
        }
        else
        {
            Assert.AreEqual("invalid-arguments", error.GetProperty("token").GetString());
            Assert.IsFalse(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        }
    }

    [TestMethod]
    public async Task GuestContext_HelpBypassesProcessAndStoreAdmission()
    {
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["devtools", "comments", "list", "--guest-comments", "invalid", "--help"]);
        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(string.Empty, stderr.Trim(), stderr);
        StringAssert.Contains(stdout, "--source-root");
    }

    [TestMethod]
    public async Task Correction_ActualInvocationException_UsesCommentsJson()
    {
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["devtools", "comments", "add", "--text", "note", "--source-root", " ", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(string.Empty, stderr.Trim(), stderr);
        using var doc = JsonDocument.Parse(stdout);
        Assert.IsFalse(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.AreEqual(JsonValueKind.String, doc.RootElement.GetProperty("error").ValueKind);
    }

    [TestMethod]
    [DataRow("nope")]
    public async Task Correction_AttachErrors_KeepAttachEnvelope(string pid)
    {
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(["devtools", "attach", "--pid", pid, "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(string.Empty, stderr.Trim(), stderr);
        using var doc = JsonDocument.Parse(stdout);
        Assert.AreEqual(JsonValueKind.String, doc.RootElement.GetProperty("error").ValueKind);
        Assert.AreEqual(0, doc.RootElement.GetProperty("nodeCount").GetInt32());
    }

    [TestMethod]
    public async Task Correction_AttachValidation_UsesSameEnvelope()
    {
        var command = GetRequiredService<Commands.DevToolsAttachCommand>();
        Assert.AreEqual(1, await ParseAndInvokeWithCaptureAsync(command, ["--pid", "0", "--json"]));
        using var doc = JsonDocument.Parse(TestAnsiConsole.Output);
        Assert.AreEqual(JsonValueKind.String, doc.RootElement.GetProperty("error").ValueKind);
        Assert.AreEqual(0, doc.RootElement.GetProperty("nodeCount").GetInt32());
    }
}
