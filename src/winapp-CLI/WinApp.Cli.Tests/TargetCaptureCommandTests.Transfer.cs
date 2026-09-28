// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Spectre.Console.Testing;
using WinApp.Cli.Commands;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

public partial class TargetCaptureCommandTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Push_NondefaultRootReportsTheActualDestination(bool json)
    {
        await using var harness = new Harness(GuestWindows());
        using var console = new TestConsole();
        console.Profile.Width = 240;
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "probe.txt");
        await File.WriteAllTextAsync(source, "work-root probe", TestContext.CancellationToken);
        var command = new TargetPushCommand();
        var arguments = new[] { "sandbox", source, "Setup files/./probe.txt" };

        Assert.AreEqual(0, await new TargetPushCommand.Handler(harness.Orchestrator, console)
            .InvokeAsync(Parse(command, json ? [.. arguments, "--json"] : arguments),
                TestContext.CancellationToken));

        var workRoot = new GuestFileService(harness.GuestManaged)
            .ResolveScopeDirectory(TargetFileTransferService.WorkScope, create: false);
        var destination = Path.Combine(workRoot, "Setup files", "probe.txt");
        Assert.AreEqual("work-root probe",
            await File.ReadAllTextAsync(destination, TestContext.CancellationToken));
        if (json)
        {
            using var document = JsonDocument.Parse(console.Output);
            Assert.AreEqual(destination, document.RootElement.GetProperty("targetPath").GetString());
        }
        else
        {
            StringAssert.Contains(console.Output, $"to {destination} on sandbox.");
        }
    }

    [TestMethod]
    public async Task Push_DirectoryToDotReportsWorkRoot()
    {
        await using var harness = new Harness(GuestWindows());
        using var console = new TestConsole();
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "probe.txt"), "work-root probe",
            TestContext.CancellationToken);
        var command = new TargetPushCommand();

        Assert.AreEqual(0, await new TargetPushCommand.Handler(harness.Orchestrator, console)
            .InvokeAsync(Parse(command, "sandbox", _root, ".", "--json"),
                TestContext.CancellationToken));

        var workRoot = new GuestFileService(harness.GuestManaged)
            .ResolveScopeDirectory(TargetFileTransferService.WorkScope, create: false);
        using var document = JsonDocument.Parse(console.Output);
        Assert.AreEqual(workRoot, document.RootElement.GetProperty("targetPath").GetString());
        Assert.AreEqual("work-root probe",
            await File.ReadAllTextAsync(Path.Combine(workRoot, "probe.txt"), TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Push_MissingManagedRootFailsBeforeWriting()
    {
        await using var harness = new Harness(GuestWindows());
        harness.Backend.ReportsManagedRoot = false;
        using var console = new TestConsole();
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "probe.txt");
        await File.WriteAllTextAsync(source, "work-root probe", TestContext.CancellationToken);
        var command = new TargetPushCommand();

        var result = await CaptureStandardErrorAsync(() =>
            new TargetPushCommand.Handler(harness.Orchestrator, console).InvokeAsync(
                Parse(command, "sandbox", source, "probe.txt", "--json"),
                TestContext.CancellationToken));

        Assert.AreNotEqual(0, result.ExitCode);
        using var error = JsonDocument.Parse(result.StandardError);
        Assert.AreEqual(ExecutionTargetErrorCodes.AgentIncompatible,
            error.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.IsFalse(Directory.Exists(Path.Combine(harness.GuestManaged, "work")));
        Assert.AreEqual(string.Empty, console.Output);
    }

    [TestMethod]
    public void Push_RootedPathAdviceDoesNotInventAGuestRoot()
    {
        var failure = Assert.ThrowsExactly<ExecutionTargetException>(() =>
            TargetFileTransferService.NormalizeTargetRelative(@"D:\Managed\work\probe.txt"));

        StringAssert.Contains(failure.Error.Message, "managed work area");
        StringAssert.Contains(failure.Error.UserAction, "target snapshot");
        Assert.IsFalse(failure.Error.Message.Contains(@"C:\WinApp", StringComparison.Ordinal));
        Assert.IsFalse(failure.Error.UserAction!.Contains(@"C:\WinApp", StringComparison.Ordinal));
    }
}
