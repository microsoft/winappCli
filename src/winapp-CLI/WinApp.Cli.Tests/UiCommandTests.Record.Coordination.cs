// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text;
using WinApp.Cli.Commands;

namespace WinApp.Cli.Tests;

/// <summary>
/// How <c>ui record</c> hands the desktop back. Recording is the only command that keeps running
/// after it stops needing exclusive use of the desktop, so the moment it releases the section — and
/// what can delay that moment — is its own coverage area.
/// </summary>
public partial class UiCommandTests
{
    /// <summary>
    /// A <see cref="TextWriter"/> whose first <c>WriteLine</c> blocks until the test releases it,
    /// standing in for a caller that reads the command's stderr slowly (a full pipe buffer, a paused
    /// consumer, a debugger-attached parent).
    /// </summary>
    private sealed class BlockingWriter : TextWriter
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly ManualResetEventSlim _entered = new(false);
        private int _writes;

        public override Encoding Encoding => Encoding.UTF8;

        /// <summary>Waits until the command is actually blocked inside the write.</summary>
        public bool WaitUntilBlocked(TimeSpan timeout) => _entered.Wait(timeout);

        public void Release() => _release.Set();

        public override void WriteLine(string? value)
        {
            if (Interlocked.Increment(ref _writes) == 1)
            {
                _entered.Set();
                _release.Wait(TimeSpan.FromSeconds(30));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _release.Set();
                _release.Dispose();
                _entered.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// A blocked "recording-started" write must not keep the desktop locked.
    /// </summary>
    /// <remarks>
    /// The engine raises its first-frame callback on the capture thread, and the CLI both signals its
    /// own "the desktop is free now" gate and writes the liveness JSON from inside that callback. If
    /// the write happens first, a caller that is slow to drain stderr pins the capture thread inside
    /// the callback; the gate is never signalled, the recording task never completes, and the section
    /// stays open for as long as the reader is slow — blocking every other winapp ui command on the
    /// machine. Signalling first makes the release independent of the write, which is what this
    /// asserts: the section is observed closed while the writer is still blocked.
    /// </remarks>
    [TestMethod]
    public async Task Record_BlockedStartedNotification_DoesNotHoldTheDesktopSection()
    {
        var outputPath = Path.Combine(_tempDirectory.FullName, "blocked-notify.mp4");

        // WGC, not PrintWindow: PrintWindow deliberately holds the section for the whole recording,
        // so only the frame-capture path exercises the first-frame handoff this test is about.
        _fakeWindowCapture.Supported = true;

        // Keep the fake recording running until the test says otherwise, so the only thing that can
        // close the section is the first-frame handoff — not the recording simply ending.
        var finishRecording = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fakeRecording.WaitAfterRecordingStarted = finishRecording.Task;

        using var blockingStderr = new BlockingWriter();
        var command = GetRequiredService<UiRecordCommand>();
        var parseResult = command.Parse(
            ["-a", "TestApp", "--duration-sec", "1", "-o", outputPath, "--json"]);
        parseResult.InvocationConfiguration.Output = TestAnsiConsole.Profile.Out.Writer;
        parseResult.InvocationConfiguration.Error = blockingStderr;

        var invocation = Task.Run(
            () => parseResult.InvokeAsync(parseResult.InvocationConfiguration, CancellationToken.None),
            CancellationToken.None);

        Assert.IsTrue(
            blockingStderr.WaitUntilBlocked(TimeSpan.FromSeconds(20)),
            "the command should have reached the recording-started write");

        // The write is still blocked right now. The section must already be closed anyway.
        var releasedWhileBlocked = await WaitForConditionAsync(
            () => _fakeDesktopLock.OpenDesktopSections == 0,
            TimeSpan.FromSeconds(10));

        blockingStderr.Release();
        finishRecording.TrySetResult();
        var exitCode = await invocation;

        Assert.IsTrue(
            releasedWhileBlocked,
            "the desktop section must be released from the first-frame signal alone; a caller that is " +
            "slow to read stderr must not be able to hold the desktop.");
        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeDesktopLock.DesktopSectionEnters, "recording takes exactly one section");
    }

    /// <summary>
    /// A target whose handle was recycled while the command queued must be refused from inside the
    /// section, before the engine is handed the stale handle.
    /// </summary>
    /// <remarks>
    /// The target is resolved before the command queues for the desktop. By the time the turn is
    /// granted, the original window may have closed and Windows may have reused its handle for an
    /// unrelated process — and a recording of the wrong application looks exactly like a correct one.
    /// </remarks>
    [TestMethod]
    public async Task Record_TargetHandleRecycledWhileQueued_RefusesAndRecordsNothing()
    {
        var outputPath = Path.Combine(_tempDirectory.FullName, "recycled.mp4");
        _fakeWindowCapture.Supported = true;

        const long hwnd = 777;
        _fakeTargetResolver.TargetResult = new UiTarget
        {
            ProcessId = 1234,
            ProcessName = "TestApp",
            WindowTitle = "Test Window",
            WindowHandle = hwnd,
        };

        // The window the command resolved is gone; the handle now belongs to an unrelated process
        // and has no owner chain leading back to the expected one.
        _fakeSystemQuery.ProcessIdByHwnd[hwnd] = 9999;

        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--duration-sec", "1", "-o", outputPath, "--json"]);

        Assert.AreEqual(1, exitCode);
        AssertJsonErrorCode("stale_element");
        Assert.IsFalse(File.Exists(outputPath), "a refused recording must not leave an MP4 behind");
        Assert.AreEqual(1, _fakeDesktopLock.DesktopSectionEnters,
            "the check belongs inside the section — it is only meaningful once the turn is held");
        Assert.AreEqual(0, _fakeDesktopLock.OpenDesktopSections, "the section must still be released");
    }

    private static async Task<bool> WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25, CancellationToken.None);
        }

        return condition();
    }
}
