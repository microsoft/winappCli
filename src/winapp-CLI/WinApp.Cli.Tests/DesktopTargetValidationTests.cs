// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// Post-wait target validation (<see cref="DesktopTargetValidation"/>, issue #764 spec §10.5).
/// </summary>
/// <remarks>
/// The check has to satisfy two things that pull in opposite directions: refuse a handle Windows has
/// recycled onto an unrelated process during a queue wait, while still allowing the cross-process
/// windows an app legitimately owns. <c>UiAutomationService.GetAllAppWindows</c> deliberately
/// discovers common-item file pickers and system dialogs — they run in another process yet are part of
/// the app's UI, and elements found on them carry that foreign HWND — so a plain PID equality check
/// made every one of those targets unreachable after a wait.
/// </remarks>
[TestClass]
public class DesktopTargetValidationTests : IDisposable
{
    private const int SessionPid = 1234;
    private const int ForeignPid = 5678;

    private FakeSystemUiQuery _systemQuery = null!;
    private StringWriter _errorOut = null!;

    public void Dispose()
    {
        _errorOut?.Dispose();
        GC.SuppressFinalize(this);
    }

    [TestInitialize]
    public void Setup()
    {
        _systemQuery = new FakeSystemUiQuery();
        _errorOut = new StringWriter();
    }

    private bool Confirm(long hwnd, int expectedPid = SessionPid)
        => DesktopTargetValidation.TryConfirmTargetWindow(
            _systemQuery, hwnd, expectedPid, NullLogger.Instance, json: true, "click", _errorOut);

    private void AssertStaleElementEmitted()
        => StringAssert.Contains(_errorOut.ToString(), "\"code\":\"stale_element\"");

    // ------------------------------------------------------------------ the straightforward cases

    [TestMethod]
    public void DirectSamePidWindowIsAccepted()
    {
        _systemQuery.ProcessIdByHwnd[100] = SessionPid;

        Assert.IsTrue(Confirm(100));
        Assert.AreEqual(string.Empty, _errorOut.ToString(), "an accepted target emits no error");
    }

    [TestMethod]
    public void BareCoordinateTargetIsAccepted()
    {
        // hwnd 0 means "no window to confirm" — the foreground guard is the gate for coordinates.
        Assert.IsTrue(Confirm(0));
    }

    [TestMethod]
    public void ClosedWindowIsRejected()
    {
        _systemQuery.ProcessIdByHwnd[100] = 0;

        Assert.IsFalse(Confirm(100));
        AssertStaleElementEmitted();
    }

    [TestMethod]
    public void ForeignPidWithNoOwnerIsRejected()
    {
        // The recycled-handle case: the original window exited and Windows handed its handle to an
        // unrelated top-level window. No owner chain, so nothing associates it with the session.
        _systemQuery.ProcessIdByHwnd[100] = ForeignPid;

        Assert.IsFalse(Confirm(100));
        AssertStaleElementEmitted();
    }

    // -------------------------------------------------------- legitimate cross-process owned windows

    [TestMethod]
    public void CrossProcessWindowOwnedByTheExpectedProcessIsAccepted()
    {
        // Exactly what a common-item file picker looks like: a window in another process whose
        // GW_OWNER is one of the session's own windows. Discovery checks exactly this one hop; the
        // validator additionally follows longer chains, which the nested-dialog case below covers.
        _systemQuery.ProcessIdByHwnd[100] = ForeignPid;
        _systemQuery.WindowOwnerByHwnd[100] = 200;
        _systemQuery.ProcessIdByHwnd[200] = SessionPid;

        Assert.IsTrue(Confirm(100));
        Assert.AreEqual(string.Empty, _errorOut.ToString());
    }

    [TestMethod]
    public void CrossProcessWindowWhoseOwnerDiedIsRejected()
    {
        // The owning app exited during the queue wait; its window handle now resolves to no process.
        // The picker may still be on screen, but it is no longer the session's UI.
        _systemQuery.ProcessIdByHwnd[100] = ForeignPid;
        _systemQuery.WindowOwnerByHwnd[100] = 200;
        _systemQuery.ProcessIdByHwnd[200] = 0;

        Assert.IsFalse(Confirm(100));
        AssertStaleElementEmitted();
    }

    [TestMethod]
    public void CrossProcessWindowWhoseOwnerHandleWasRecycledIsRejected()
    {
        // The owner handle is live but now belongs to a *different* process than the one resolved.
        // Accepting on "the owner exists" alone would launder an unrelated window into the session.
        _systemQuery.ProcessIdByHwnd[100] = ForeignPid;
        _systemQuery.WindowOwnerByHwnd[100] = 200;
        _systemQuery.ProcessIdByHwnd[200] = 9999;

        Assert.IsFalse(Confirm(100));
        AssertStaleElementEmitted();
    }

    [TestMethod]
    public void OwnerChainIsFollowedThroughAnIntermediateWindow()
    {
        // A dialog owned by a dialog owned by the app window.
        _systemQuery.ProcessIdByHwnd[100] = ForeignPid;
        _systemQuery.WindowOwnerByHwnd[100] = 200;
        _systemQuery.ProcessIdByHwnd[200] = ForeignPid;
        _systemQuery.WindowOwnerByHwnd[200] = 300;
        _systemQuery.ProcessIdByHwnd[300] = SessionPid;

        Assert.IsTrue(Confirm(100));
    }

    [TestMethod]
    public void OwnerCycleIsRejectedRatherThanLoopingForever()
    {
        // A torn or hostile window tree must terminate the walk, not hang the command.
        _systemQuery.ProcessIdByHwnd[100] = ForeignPid;
        _systemQuery.WindowOwnerByHwnd[100] = 200;
        _systemQuery.ProcessIdByHwnd[200] = ForeignPid;
        _systemQuery.WindowOwnerByHwnd[200] = 100;

        Assert.IsFalse(Confirm(100));
        AssertStaleElementEmitted();
    }

    [TestMethod]
    public void UnknownExpectedProcessSkipsTheOwnershipCheck()
    {
        // expectedProcessId <= 0 means the caller had no PID to compare against; the existing contract
        // is to allow through rather than invent a rejection.
        _systemQuery.ProcessIdByHwnd[100] = ForeignPid;

        Assert.IsTrue(Confirm(100, expectedPid: 0));
    }

    // -------------------------------------------------- the silent verdict used for batched handles

    /// <remarks>
    /// The multi-window screenshot composite validates every handle it is about to capture. Each one
    /// needs a verdict without writing a top-level error envelope, because a single unreachable window
    /// is recorded against that window and the remaining ones are still captured — so the classifier
    /// has to be reusable separately from the reporting.
    /// </remarks>
    [TestMethod]
    public void ClassifierAgreesWithTheReportingCheckOnEveryOutcome()
    {
        _systemQuery.ProcessIdByHwnd[100] = SessionPid;                 // straightforward match
        _systemQuery.ProcessIdByHwnd[300] = ForeignPid;                 // recycled onto another process
        _systemQuery.ProcessIdByHwnd[400] = ForeignPid;                 // owned dialog
        _systemQuery.WindowOwnerByHwnd[400] = 100;
        // 200 is deliberately unmapped: GetProcessIdForWindow reports 0, i.e. destroyed.

        Assert.AreEqual(DesktopTargetValidation.TargetWindowState.Valid, Classify(100));
        Assert.AreEqual(DesktopTargetValidation.TargetWindowState.Gone, Classify(200));
        Assert.AreEqual(DesktopTargetValidation.TargetWindowState.Recycled, Classify(300));
        Assert.AreEqual(DesktopTargetValidation.TargetWindowState.Valid, Classify(400));

        // A zero handle is a bare-coordinate target, which has no window to confirm.
        Assert.AreEqual(DesktopTargetValidation.TargetWindowState.Valid, Classify(0));

        Assert.AreEqual(string.Empty, _errorOut.ToString(), "classifying must never emit an error envelope");
    }

    private DesktopTargetValidation.TargetWindowState Classify(long hwnd, int expectedPid = SessionPid)
        => DesktopTargetValidation.ClassifyTargetWindow(_systemQuery, hwnd, expectedPid);
}
