// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Tests;

[TestClass]
public class PerfCaptureCompletionTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void AppExitCompletesWithoutInventingLossStatistics(bool sessionStopped)
    {
        var capture = Capture();
        capture.TargetExited = true;
        capture.StopReason = "target-exited";
        PerfCaptureWorker.SetFinalState(capture, sessionStopped);
        Assert.AreEqual("completed", capture.State);
        Assert.IsNull(capture.Error);
        Assert.IsNull(capture.EventsLost);
        Assert.IsNull(capture.BuffersLost);
        Assert.IsNotEmpty(capture.Warnings);
    }

    [TestMethod]
    public void AppExitDoesNotHideAnExistingCaptureFailure()
    {
        var capture = Capture();
        capture.TargetExited = true;
        capture.StopReason = "target-exited";
        capture.Error = "Stop failed with access denied.";
        PerfCaptureWorker.SetFinalState(capture, false);
        Assert.AreEqual("failed", capture.State);
        Assert.AreEqual("Stop failed with access denied.", capture.Error);
    }

    [TestMethod]
    public void UnboundExitOrUnexpectedMissingSessionIsStillAFailure()
    {
        var unbound = Capture();
        unbound.ReadyQpc = null;
        unbound.TargetExited = true;
        unbound.StopReason = "target-exited";
        PerfCaptureWorker.SetFinalState(unbound, false);
        Assert.AreEqual("failed", unbound.State);
        var requested = Capture();
        requested.StopReason = "requested";
        PerfCaptureWorker.SetFinalState(requested, false);
        Assert.AreEqual("failed", requested.State);
    }

    [TestMethod]
    public void FinalizedFileResolvesAControlDisconnectButCannotRedirectOwnership()
    {
        var directory = Directory.CreateTempSubdirectory("WinApp-Perf-Final-");
        try
        {
            var capture = Capture();
            capture.Directory = directory.FullName;
            capture.State = "completed";
            capture.StopQpc = 10;
            var identity = new PerfProcessIdentity(123, DateTime.UnixEpoch);
            var registration = new PerfControlRegistration(capture.Id, directory.FullName, new string('a', 64),
                capture.SessionId, 30, 128, Target: identity);
            capture.Save();
            var final = PerfCaptureService.ReadFinalCapture(registration, "stop");
            Assert.IsNotNull(final);
            Assert.AreEqual("completed", final.State);
            Assert.AreEqual(identity, final.Target);
            Assert.Throws<InvalidOperationException>(() => PerfCaptureService.ReadFinalCapture(registration, "mark"));
            Assert.AreEqual("completed", PerfCaptureDocument.Load(directory.FullName).State);
            capture.SessionName = "somebody-elses-session";
            capture.Save();
            Assert.Throws<InvalidDataException>(() => PerfCaptureService.ReadFinalCapture(registration, "stop"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static PerfCaptureDocument Capture()
    {
        var id = Guid.NewGuid().ToString("N");
        return new() { Id = id, Directory = @"C:\unused", SessionId = Guid.NewGuid(),
            SessionName = "WinApp-Perf-" + id, ReadyQpc = 1 };
    }
}
