// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    private sealed class SignalingStringReader(string text, Action onRead) : StringReader(text)
    {
        public override string? ReadLine() { onRead(); return base.ReadLine(); }
    }

    private sealed class SignalingEofReader(Action onRead) : TextReader
    {
        public override string? ReadLine() { onRead(); return null; }
    }

    private sealed class EofReader : TextReader
    {
        public override string? ReadLine() => null;
    }

    private sealed class ThrowingStdinReader : TextReader
    {
        public override string? ReadLine() => throw new IOException("stdin pipe broke");
    }

    [TestMethod]
    public void StdinStopMonitor_Newline_StopsRecording()
    {
        var stopped = false;
        using var reader = new StringReader("\n");
        StdinStopMonitor.MonitorCore(
            reader,
            Task.CompletedTask,
            () => stopped = true);
        Assert.IsTrue(stopped, "a newline should trigger stop");
    }

    [TestMethod]
    public void StdinStopMonitor_EmptyLine_StopsRecording()
    {
        var stopped = false;
        using var reader = new StringReader("\n");
        StdinStopMonitor.MonitorCore(
            reader,
            Task.CompletedTask,
            () => stopped = true);
        Assert.IsTrue(stopped, "an empty line (immediate enter) should trigger stop");
    }

    [TestMethod]
    public void StdinStopMonitor_ImmediateEof_Stops()
    {
        var stopped = false;
        StdinStopMonitor.MonitorCore(
            new EofReader(),
            Task.CompletedTask,
            () => stopped = true);
        Assert.IsTrue(stopped, "immediate EOF with a completed ready task must trigger stop");
    }

    [TestMethod]
    public void StdinStopMonitor_LineOfText_Stops()
    {
        var stopped = false;
        using var reader = new StringReader("stop");
        StdinStopMonitor.MonitorCore(
            reader,
            Task.CompletedTask,
            () => stopped = true);
        Assert.IsTrue(stopped, "any line of text should trigger stop");
    }

    [TestMethod]
    public void StdinStopMonitor_ReadThrows_TreatedAsEofAndStops()
    {
        var stopped = false;
        StdinStopMonitor.MonitorCore(
            new ThrowingStdinReader(),
            Task.CompletedTask,
            () => stopped = true);
        Assert.IsTrue(stopped, "a stdin read error must be treated as EOF and still trigger stop");
    }

    [TestMethod]
    public void StdinStopMonitor_Start_RunsOnBackgroundThreadAndFiresStop()
    {
        using var stopped = new ManualResetEventSlim(false);
        using var reader = new StringReader("\n");
        StdinStopMonitor.Start(
            reader,
            Task.CompletedTask,
            () => stopped.Set());

        Assert.IsTrue(stopped.Wait(TimeSpan.FromSeconds(5)),
            "Start must run the monitor on a background thread and fire stop for a pre-buffered line.");
    }

    [TestMethod]
    public async Task StdinStopMonitor_EofBeforeReady_WaitsForReadyThenStops()
    {
        var stopped = false;
        var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var t = new Thread(() =>
        {
            StdinStopMonitor.MonitorCore(
                new SignalingEofReader(() => readReturned.TrySetResult()),
                readyTcs.Task,
                () => stopped = true);
        });
        t.IsBackground = true;
        t.Start();

        await readReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(stopped, "stop must not fire before the ready task completes");

        readyTcs.SetResult();
        t.Join(TimeSpan.FromSeconds(5));
        Assert.IsTrue(stopped, "stop must fire after the ready task completes");
    }

    [TestMethod]
    public async Task StdinStopMonitor_NewlineBeforeReady_WaitsForReadyThenStops()
    {
        var stopped = false;
        var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var t = new Thread(() =>
        {
            StdinStopMonitor.MonitorCore(
                new SignalingStringReader("stop\n", () => readReturned.TrySetResult()),
                readyTcs.Task,
                () => stopped = true);
        });
        t.IsBackground = true;
        t.Start();

        await readReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(stopped, "stop must not fire before the ready task completes");

        readyTcs.SetResult();
        t.Join(TimeSpan.FromSeconds(5));
        Assert.IsTrue(stopped, "stop must fire after the ready task completes");
    }

    [TestMethod]
    public async Task Record_ReadinessHandshake_PrefilledStdinNewline_ExitsZeroWithValidFile()
    {
        var outputPath = Path.Combine(_tempDirectory.FullName, "readiness.mp4");
        var command = GetRequiredService<UiRecordCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--duration-sec", "1", "-o", outputPath, "--json"]);

        Assert.AreEqual(0, exitCode, "command must exit 0 (not internal_error)");
        Assert.IsTrue(File.Exists(outputPath), "a valid output file must be produced");
        Assert.IsTrue(new FileInfo(outputPath).Length > 0, "output file must be non-empty");
    }

    [TestMethod]
    public async Task Record_Quiet_SuppressesRecordingProgress()
    {
        var outputPath = Path.Combine(_tempDirectory.FullName, "quiet-suppress.mp4");
        var command = GetRequiredService<UiRecordCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--quiet", "--duration-sec", "1", "-o", outputPath]);

        Assert.AreEqual(0, exitCode, "--quiet must still exit 0");
        var stdout = TestAnsiConsole.Output;
        Assert.IsFalse(stdout.Contains("Recording"),
            $"--quiet must suppress 'Recording' progress text; got stdout: {stdout}");
    }

    [TestMethod]
    public async Task Record_Quiet_ExitsZeroAndProducesFile()
    {
        var outputPath = Path.Combine(_tempDirectory.FullName, "quiet-file.mp4");
        var command = GetRequiredService<UiRecordCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--quiet", "--duration-sec", "1", "-o", outputPath]);

        Assert.AreEqual(0, exitCode, "--quiet must exit 0 (recording proceeds normally)");
        Assert.IsTrue(File.Exists(outputPath), "--quiet must still produce the output file");
    }

    [TestMethod]
    public async Task Record_NonRedirectedStdin_StatusTextCtrlCOnly()
    {
        var outputPath = Path.Combine(_tempDirectory.FullName, "l1-noredirect.mp4");
        var command = GetRequiredService<UiRecordCommand>();

        UiRecordCommand.Handler.s_isInputRedirectedOverride = () => false;
        try
        {
            var exitCode = await ParseAndInvokeWithCaptureAsync(
                command, ["-a", "TestApp", "--duration-sec", "1", "-o", outputPath]);

            Assert.AreEqual(0, exitCode);
            var stdout = TestAnsiConsole.Output;
            Assert.IsFalse(stdout.Contains("EOF") || stdout.Contains("newline"),
                $"Non-redirected stdin status must not mention EOF/newline; got: {stdout}");
        }
        finally
        {
            UiRecordCommand.Handler.s_isInputRedirectedOverride = null;
        }
    }

    [TestMethod]
    public async Task Record_RedirectedStdin_StatusTextMentionsEof()
    {
        var outputPath = Path.Combine(_tempDirectory.FullName, "l1-redirect.mp4");
        var command = GetRequiredService<UiRecordCommand>();

        using var stdin = new StringReader("stop");
        UiRecordCommand.Handler.s_isInputRedirectedOverride = () => true;
        UiRecordCommand.Handler.s_stdinOverride = stdin;
        try
        {
            _fakeRecording.RecordResult = new RecordCaptureResult { Frames = 1, Width = 64, Height = 64, Mode = "wgc" };
            _fakeRecording.RecordShouldWaitForCancellation = true;
            var exitCode = await ParseAndInvokeWithCaptureAsync(
                command, ["-a", "TestApp", "--duration-sec", "0", "-o", outputPath]);

            Assert.AreEqual(0, exitCode);
            var stdout = TestAnsiConsole.Output;
            Assert.IsTrue(stdout.Contains("EOF") || stdout.Contains("stdin"),
                $"Redirected stdin status must mention EOF or stdin; got: {stdout}");
        }
        finally
        {
            UiRecordCommand.Handler.s_isInputRedirectedOverride = null;
            UiRecordCommand.Handler.s_stdinOverride = null;
            _fakeRecording.RecordShouldWaitForCancellation = false;
        }
    }

    [TestMethod]
    public async Task Record_Frames_RedirectedStdinStopsAndPublishesBundle()
    {
        var outputPath = Path.Combine(_tempDirectory.FullName, "frames-redirect.mp4");
        var command = GetRequiredService<UiRecordCommand>();

        using var stdin = new StringReader("stop");
        UiRecordCommand.Handler.s_isInputRedirectedOverride = () => true;
        UiRecordCommand.Handler.s_stdinOverride = stdin;
        try
        {
            _fakeRecording.RecordResult = new RecordCaptureResult
            {
                Frames = 1,
                Width = 64,
                Height = 64,
                Mode = "wgc",
                StopReason = "cancelled",
            };
            _fakeRecording.RecordShouldWaitForCancellation = true;

            var exitCode = await ParseAndInvokeWithCaptureAsync(
                command,
                ["-a", "TestApp", "--frames", "--duration-sec", "0", "-o", outputPath, "--json"]);

            Assert.AreEqual(0, exitCode);
            var framesDirectory = Path.Combine(_tempDirectory.FullName, "frames-redirect.frames");
            Assert.IsTrue(File.Exists(outputPath));
            Assert.IsTrue(File.Exists(Path.Combine(framesDirectory, "manifest.json")));
            Assert.IsTrue(File.Exists(Path.Combine(framesDirectory, "frames.ndjson")));
            StringAssert.Contains(TestAnsiConsole.Output, "\"stopReason\": \"cancelled\"");
        }
        finally
        {
            UiRecordCommand.Handler.s_isInputRedirectedOverride = null;
            UiRecordCommand.Handler.s_stdinOverride = null;
            _fakeRecording.RecordShouldWaitForCancellation = false;
        }
    }

    [TestMethod]
    public async Task Record_NonZeroDuration_WithRedirectedStdin_NeverReadsStdinAndExitsZero()
    {
        _fakeRecording.RecordResult = new RecordCaptureResult { Frames = 20, Width = 640, Height = 480, Mode = "wgc" };
        _fakeRecording.RecordShouldWaitForCancellation = false;

        var outputPath = Path.Combine(_tempDirectory.FullName, "h1-timed-ignores-stdin.mp4");
        var command = GetRequiredService<UiRecordCommand>();

        var stdinWasRead = false;
        UiRecordCommand.Handler.s_isInputRedirectedOverride = () => true;
        UiRecordCommand.Handler.s_stdinOverride = new SignalingEofReader(() => stdinWasRead = true);
        try
        {
            var exitCode = await ParseAndInvokeWithCaptureAsync(
                command, ["-a", "TestApp", "--duration-sec", "120", "-o", outputPath, "--json"]);

            await Task.Delay(100);

            Assert.AreEqual(0, exitCode, "a timed recording must complete via its duration deadline and exit 0");
            Assert.IsTrue(File.Exists(outputPath), "a valid output file must be produced");
            Assert.IsFalse(stdinWasRead, "a timed recording must never arm the stdin monitor or read redirected stdin");
        }
        finally
        {
            UiRecordCommand.Handler.s_isInputRedirectedOverride = null;
            UiRecordCommand.Handler.s_stdinOverride = null;
        }
    }

    [TestMethod]
    public async Task Record_NonZeroDuration_NoRedirectedStdin_DurationDeadlineWins()
    {
        _fakeRecording.RecordResult = new RecordCaptureResult { Frames = 5, Width = 640, Height = 480, Mode = "wgc" };

        var outputPath = Path.Combine(_tempDirectory.FullName, "h1-duration-wins.mp4");
        var command = GetRequiredService<UiRecordCommand>();

        UiRecordCommand.Handler.s_isInputRedirectedOverride = () => false;
        try
        {
            var exitCode = await ParseAndInvokeWithCaptureAsync(
                command, ["-a", "TestApp", "--duration-sec", "1", "-o", outputPath, "--json"]);
            Assert.AreEqual(0, exitCode, "non-redirected stdin with duration deadline must exit 0");
            Assert.IsTrue(File.Exists(outputPath));
        }
        finally
        {
            UiRecordCommand.Handler.s_isInputRedirectedOverride = null;
        }
    }

    [TestMethod]
    public async Task StdinMonitor_DisposedCts_NeverThrowsUnhandledException()
    {
        Exception? unhandled = null;
        UnhandledExceptionEventHandler probe = (_, e)
            => Interlocked.CompareExchange(ref unhandled, (Exception)e.ExceptionObject, null);
        AppDomain.CurrentDomain.UnhandledException += probe;
        try
        {
            for (var i = 0; i < 5; i++)
            {
                Interlocked.Exchange(ref unhandled, null);

                var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
                var handler = new UiRecordCommand.Handler(null!, null!, null!, null!, null!, null!, NullLogger<UiRecordCommand>.Instance);

                cts.Dispose();

                var callDone = new ManualResetEventSlim(false);
                new Thread(() => { handler.CancelFromStdinMonitor(cts); callDone.Set(); })
                {
                    IsBackground = true,
                    Name = $"disposed-cts-probe-{i}",
                }.Start();
                callDone.Wait(TimeSpan.FromSeconds(5));

                await Task.Delay(50);

                Assert.IsNull(unhandled,
                    $"run {i}: background thread threw unhandled: " +
                    $"{unhandled?.GetType().Name}: {unhandled?.Message}");
            }
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= probe;
        }
    }
}
