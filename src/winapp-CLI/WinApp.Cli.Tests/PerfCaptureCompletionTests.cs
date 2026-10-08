// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Pipes;
using System.Text.Json;
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

    [TestMethod]
    public async Task MalformedControlFrameDoesNotTerminateWorker()
    {
        var directory = Directory.CreateTempSubdirectory("WinApp-Perf-Control-");
        try
        {
            var id = Guid.NewGuid().ToString("N");
            var registrationPath = Path.Join(directory.FullName, "control.json");
            var registration = new PerfControlRegistration(id, directory.FullName, new string('a', 64),
                Guid.NewGuid(), 30, 128);
            File.WriteAllText(registrationPath,
                JsonSerializer.Serialize(registration, PerfJsonContext.Default.PerfControlRegistration));
            new PerfCaptureDocument
            {
                Id = id,
                Directory = directory.FullName,
                SessionId = registration.SessionId,
                SessionName = "WinApp-Perf-" + id,
            }.Save();

            var workerTask = PerfCaptureWorker.RunAsync([PerfCaptureWorker.InternalVerb, registrationPath]);
            using (var malformed = new NamedPipeClientStream(".", PerfControlChannel.PipeName(id),
                PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await malformed.ConnectAsync(timeout.Token);
                await malformed.WriteAsync(BitConverter.GetBytes(5000), timeout.Token);
                await malformed.FlushAsync(timeout.Token);
            }

            var stopped = await PerfControlChannel.SendAsync(registration,
                new(registration.Credential, "stop"), CancellationToken.None);
            Assert.AreEqual("failed", stopped.State);
            Assert.Contains("size limit", stopped.LastControlError ?? string.Empty);
            Assert.AreEqual(1, await workerTask.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual("requested", PerfCaptureDocument.Load(directory.FullName).StopReason);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task MissingTargetDuringOrphanStopPersistsSessionUnavailable()
    {
        var root = Directory.CreateTempSubdirectory("WinApp-Perf-Orphan-");
        try
        {
            var id = Guid.NewGuid().ToString("N");
            var captureDirectory = root.CreateSubdirectory("capture");
            var registry = Directory.CreateDirectory(Path.Join(root.FullName, "perf-control", id));
            var missing = new PerfProcessIdentity(int.MaxValue, DateTime.UnixEpoch);
            var registration = new PerfControlRegistration(id, captureDirectory.FullName, new string('a', 64),
                Guid.NewGuid(), 30, 128, Worker: missing, Target: missing);
            File.WriteAllText(Path.Join(registry.FullName, "control.json"),
                JsonSerializer.Serialize(registration, PerfJsonContext.Default.PerfControlRegistration));
            new PerfCaptureDocument
            {
                Id = id,
                Directory = captureDirectory.FullName,
                SessionId = registration.SessionId,
                SessionName = "WinApp-Perf-" + id,
                State = "recording",
                ReadyQpc = 1,
            }.Save();
            var service = new PerfCaptureService(new FakeWinappDirectoryService(root), new FakeAppLauncherService());

            var result = await service.ControlAsync(id, "stop", null, CancellationToken.None);

            Assert.AreEqual("failed", result.State);
            Assert.AreEqual("session-unavailable", result.StopReason);
            Assert.IsNotNull(result.StopQpc);
            Assert.Contains("Orphan recovery failed", result.Error ?? string.Empty);
            var saved = PerfCaptureDocument.Load(captureDirectory.FullName);
            Assert.AreEqual("failed", saved.State);
            Assert.AreEqual("session-unavailable", saved.StopReason);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static PerfCaptureDocument Capture()
    {
        var id = Guid.NewGuid().ToString("N");
        return new() { Id = id, Directory = @"C:\unused", SessionId = Guid.NewGuid(),
            SessionName = "WinApp-Perf-" + id, ReadyQpc = 1 };
    }
}
