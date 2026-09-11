// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Spectre.Console.Testing;
using WinApp.Cli.Commands;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

public partial class TargetCaptureCommandTests
{
    private static readonly string[] RecoveryInspectArguments = ["ui", "inspect", "-a", "4416"];
    private static readonly string[] RecoveryInspectJsonArguments = ["ui", "inspect", "-a", "4416", "--json"];

    [TestMethod]
    [DataRow("screenshot")]
    [DataRow("record")]
    public async Task UiRouter_GuestDesktopCaptureRequiresGuestReadinessButNoHostWindow(string verb)
    {
        await using var harness = new Harness(stdout: "", exitCode: 7, rendersDesktop: false);
        harness.Backend.Target = new ExecutionTargetRef("test-provider", "Agent 'one' --on local");
        var target = harness.Backend.Target;
        var destination = Path.Combine(_root, verb == "record" ? "desktop.mp4" : "desktop.png");
        var router = new ExecutionTargetUiRouter(harness.Orchestrator, new TestConsole());

        var exitCode = await router.RouteAsync(
            ["__guest-desktop", verb, "--json", "--output", destination, "--"],
            TargetUiRequirements.Interactive with { CommandName = verb, GuestDesktopCapture = true },
            isJson: true, TestContext.CancellationToken);

        Assert.AreEqual(7, exitCode, "A provider without a host desktop window can execute the capture.");
        Assert.AreEqual(1, harness.Backend.Requests.Count);
        var request = harness.Backend.Requests[0];
        Assert.IsTrue(request.RequiresRealInput, "Guest readiness checks must remain enabled.");
        Assert.AreEqual("__guest-desktop", request.Arguments[0]);
        Assert.AreEqual(verb, request.Arguments[1]);
        Assert.AreEqual("--", request.Arguments[^1]);
        CollectionAssert.AreEqual(
            new[] { "--target-kind", target.Kind, "--target-name", target.Id, "--target-epoch", Epoch.Value },
            request.Arguments.Skip(request.Arguments.Count - 7).Take(6).ToArray());
        Assert.IsFalse(request.Arguments.Contains("--on"), "Scope metadata must not re-route the guest command.");
        Assert.AreNotEqual(destination, request.Arguments[4], "Artifacts are still staged in the guest.");
        Assert.IsNotNull(request.Environment);
        Assert.AreEqual(target.Selector, request.Environment[UiCommandAdvice.TargetVariable]);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UiRouter_ForwardsDiagnosticTargetWithoutReroutingGuestArgv(bool json)
    {
        await using var harness = new Harness(stdout: "", exitCode: 7, rendersDesktop: false);
        harness.Backend.Target = new ExecutionTargetRef("test-provider", "Agent 'one' --on local");
        var target = harness.Backend.Target;
        const string RawOwner = "host-private-recovery-workflow";
        var previousOwner = Environment.GetEnvironmentVariable(GuestOwnerContext.WorkflowVariable);
        var previousTarget = Environment.GetEnvironmentVariable(UiCommandAdvice.TargetVariable);
        Environment.SetEnvironmentVariable(GuestOwnerContext.WorkflowVariable, RawOwner);
        Environment.SetEnvironmentVariable(UiCommandAdvice.TargetVariable, "wrong-provider:inherited");
        try
        {
            var arguments = new List<string> { "ui", "inspect", "--on", target.Selector, "-a", "4416" };
            if (json) { arguments.Add("--json"); }
            var router = new ExecutionTargetUiRouter(harness.Orchestrator, new TestConsole());

            var exitCode = await router.RouteAsync(arguments,
                TargetUiRequirements.ReadOnly with { CommandName = "inspect" },
                json, TestContext.CancellationToken);

            Assert.AreEqual(7, exitCode, "The guest's exit code is unchanged.");
            Assert.AreEqual(1, harness.Backend.Requests.Count);
            var request = harness.Backend.Requests[0];
            CollectionAssert.AreEqual(
                json ? RecoveryInspectJsonArguments : RecoveryInspectArguments,
                request.Arguments.ToArray());
            Assert.IsNotNull(request.Environment);
            Assert.AreEqual(target.Selector, request.Environment[UiCommandAdvice.TargetVariable]);
            Assert.AreEqual(
                GuestOwnerContext.DeriveGuestToken(RawOwner, target.StateKey, Epoch.Value),
                request.Environment[GuestOwnerContext.WorkflowVariable]);
            Assert.IsFalse(string.Join(' ', request.Environment.Values).Contains(RawOwner, StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(GuestOwnerContext.WorkflowVariable, previousOwner);
            Environment.SetEnvironmentVariable(UiCommandAdvice.TargetVariable, previousTarget);
        }
    }
}
