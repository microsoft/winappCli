// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Tests;

/// <summary>
/// Command-level coverage of <c>winapp ui yield</c>: the outcomes coordination reports must reach the
/// caller as the right exit code, error code and JSON shape, and yield must never register itself as a
/// participant of the turn it is releasing.
/// </summary>
public partial class UiCommandTests
{
    [TestMethod]
    public async Task Yield_ReleasesWithoutTakingATurnOrTheDesktop()
    {
        // Yield gives a turn back. Taking one to do it would make it the very live command that stops
        // the turn being idle, so it must never appear as a coordinated run or open a desktop section.
        _fakeDesktopLock.YieldResult = UiYieldResult.Released;
        var command = GetRequiredService<UiYieldCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--json"]);

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"released\": true");
        Assert.AreEqual(1, _fakeDesktopLock.YieldCalls.Count);
        Assert.AreEqual(0, _fakeDesktopLock.Runs.Count, "yield must not take a turn of its own");
        Assert.AreEqual(0, _fakeDesktopLock.DesktopSectionEnters, "yield must never take active.lock");
    }

    [TestMethod]
    public async Task Yield_WithNothingHeld_SucceedsAndReportsItReleasedNothing()
    {
        // Yielding after the grace already lapsed, or twice, is the normal end of a script.
        _fakeDesktopLock.YieldResult = UiYieldResult.NothingHeld;
        var command = GetRequiredService<UiYieldCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--json"]);

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"released\": false");
    }

    [TestMethod]
    public async Task Yield_WithoutAWorkflowId_FailsWithInvalidArguments()
    {
        // Deliberately not invalid_ui_workflow_id: that code means the variable is present but
        // malformed, and reporting it here would send the caller hunting for a value they never set.
        _fakeDesktopLock.YieldResult = UiYieldResult.NotAWorkflow;
        var command = GetRequiredService<UiYieldCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--json"]);

        Assert.AreEqual(1, exitCode);
        AssertJsonErrorCode("invalid_arguments");
        StringAssert.Contains(ConsoleStdErr.ToString(), UiOwnerResolver.WorkflowIdVariable);
    }

    [TestMethod]
    public async Task Yield_WhileTheWorkflowIsStillBusy_FailsWithTurnBusy()
    {
        _fakeDesktopLock.YieldResult = UiYieldResult.Busy;
        var command = GetRequiredService<UiYieldCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--json"]);

        Assert.AreEqual(1, exitCode);
        AssertJsonErrorCode(UiCoordinationErrorCodes.TurnBusy);
    }

    [TestMethod]
    public async Task Yield_WhenCoordinationIsUnavailable_SurfacesTheCoordinationError()
    {
        _fakeDesktopLock.ThrowOnYield = new UiCoordinationException(
            UiCoordinationErrorCodes.Unavailable, "state is unreadable", "update winapp");
        var command = GetRequiredService<UiYieldCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--json"]);

        Assert.AreEqual(1, exitCode);
        AssertJsonErrorCode(UiCoordinationErrorCodes.Unavailable);
    }

    [TestMethod]
    public async Task Yield_TextOutput_NeverRevealsTheWorkflowId()
    {
        _fakeDesktopLock.YieldResult = UiYieldResult.Released;
        var command = GetRequiredService<UiYieldCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(0, exitCode);
        var output = TestAnsiConsole.Output + ConsoleStdErr;
        Assert.IsFalse(output.Contains('{'), "text mode must not emit the JSON envelope");
        Assert.IsFalse(
            output.Contains(UiOwnerResolver.WorkflowIdVariable + "=", StringComparison.Ordinal),
            "the workflow id itself is never echoed back");
    }

    [TestMethod]
    public void Yield_TakesNoAppOrSelector()
    {
        // It releases a reservation, not a window: requiring -a would make the last step of a workflow
        // depend on an app that may already have closed.
        var command = GetRequiredService<UiYieldCommand>();

        Assert.AreEqual(0, command.Arguments.Count);
        Assert.IsFalse(
            command.Options.Any(o => o.Name is "--app" or "--window"),
            "yield targets no app");
    }
}
