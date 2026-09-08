// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    [TestMethod]
    public void Record_ShortDescription_IsUserFacing()
    {
        var command = GetRequiredService<UiRecordCommand>();
        StringAssert.Contains(command.ShortDescription, "MP4");
    }

    [TestMethod]
    public async Task Record_MissingApp_ReturnsError()
    {
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Record_InvalidFps_ReturnsError()
    {
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--fps", "0", "--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Record_InvalidFps_EmitsInvalidArguments()
    {
        // When --json is set, the command must emit the "invalid_arguments" code (not "internal_error").
        // The JSON error code goes to Console.Error; the logger message goes to ConsoleStdErr.
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--fps", "0", "--json"]);
        Assert.AreEqual(1, exitCode);
        // Check logger message (in ConsoleStdErr) confirms the right validation fired.
        StringAssert.Contains(ConsoleStdErr.ToString(), "--fps must be at least 1");
    }

    [TestMethod]
    public async Task Record_InvalidMaxEdge_ReturnsError()
    {
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--max-edge=-1", "--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Record_InvalidMaxEdge_EmitsInvalidArguments()
    {
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--max-edge=-1", "--json"]);
        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "--max-edge must be 0");
    }

    [TestMethod]
    public async Task Record_InvalidDuration_ReturnsError()
    {
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--duration-sec=-1", "--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Record_InvalidDuration_EmitsInvalidArguments()
    {
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--duration-sec=-1", "--json"]);
        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "--duration-sec must be 0 or greater");
    }

    [TestMethod]
    public async Task Record_ExcessiveDuration_EmitsInvalidArguments()
    {
        // Durations > 86400 (24 hours) are rejected with invalid_arguments.
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--duration-sec=86401", "--json"]);
        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "86400");
    }

    [TestMethod]
    public async Task Record_DefaultDuration_IsZero()
    {
        // Default --duration-sec is now 0 (record until stopped). Agents must supply an explicit
        // --duration-sec N for timed captures; without it the recording runs until Ctrl+C/stdin.
        var outputPath = Path.Combine(_tempDirectory.FullName, "default-duration.mp4");
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "-o", outputPath, "--json"]);

        Assert.AreEqual(0, exitCode);
        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual(0, result.GetProperty("durationSec").GetInt32());
    }

    [TestMethod]
    public async Task Record_Success_EmitsRecordResultJson()
    {
        _fakeRecording.RecordResult = new RecordCaptureResult { Frames = 42, Width = 640, Height = 480, Mode = "wgc" };

        var outputPath = Path.Combine(_tempDirectory.FullName, "capture.mp4");
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--duration-sec", "1", "--fps", "10", "-o", outputPath, "--json"]);

        Assert.AreEqual(0, exitCode);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual(outputPath, result.GetProperty("path").GetString());
        Assert.AreEqual("h264", result.GetProperty("codec").GetString());
        Assert.AreEqual("wgc", result.GetProperty("mode").GetString());
        Assert.AreEqual(42, result.GetProperty("frames").GetInt32());
        Assert.AreEqual(10, result.GetProperty("fps").GetInt32());
        Assert.AreEqual(1, result.GetProperty("durationSec").GetInt32());
        // The fake writes a placeholder file at the requested path.
        Assert.IsTrue(File.Exists(outputPath), "record should have produced an output file");
    }

    [TestMethod]
    public async Task Record_JsonMode_EmitsLivenessEventToStderr_NotToStdout()
    {
        // In --json mode a "recording-started" liveness event must go to stderr (not stdout).
        // Stdout must contain only the final result JSON object — no liveness noise.
        // The event is written via parseResult.InvocationConfiguration.Error (= ConsoleStdErr in
        // tests), so we check ConsoleStdErr.ToString() for the event — no Console.SetError needed.
        var outputPath = Path.Combine(_tempDirectory.FullName, "liveness.mp4");
        var command = GetRequiredService<UiRecordCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--duration-sec", "1", "-o", outputPath, "--json"]);

        Assert.AreEqual(0, exitCode);

        // Stdout must be a single parseable JSON result — not polluted by the liveness event.
        var stdout = TestAnsiConsole.Output.Trim();
        Assert.IsFalse(stdout.Contains("recording-started"), "liveness event must NOT appear on stdout");
        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(stdout);
        Assert.AreEqual("h264", result.GetProperty("codec").GetString());

        var stderrLines = ConsoleStdErr.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.HasCount(1, stderrLines, "stderr events must be one JSON object per line");
        var liveness = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderrLines[0]);
        Assert.AreEqual("recording-started", liveness.GetProperty("event").GetString(), "event field must be 'recording-started'");
        Assert.AreEqual(outputPath, liveness.GetProperty("path").GetString(), "event path must match output path");
    }

    [TestMethod]
    public async Task Record_Success_ReportsPrintWindowMode()
    {
        // The mode field must reflect the capture path actually used (accuracy fix): a printwindow
        // capture must not be mislabeled. Here the fake reports "printwindow"; assert it round-trips.
        _fakeRecording.RecordResult = new RecordCaptureResult { Frames = 5, Width = 100, Height = 100, Mode = "printwindow" };

        var outputPath = Path.Combine(_tempDirectory.FullName, "pw.mp4");
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--duration-sec", "1", "-o", outputPath, "--json"]);

        Assert.AreEqual(0, exitCode);
        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual("printwindow", result.GetProperty("mode").GetString());
    }

    [TestMethod]
    public async Task Record_Success_ReportsScreenMode()
    {
        // Verify that when the fake service reports "screen" (consented --capture-screen path),
        // the command passes the mode through unchanged so consumers can detect the capture path.
        _fakeRecording.RecordResult = new RecordCaptureResult { Frames = 2, Width = 100, Height = 100, Mode = "screen" };

        var outputPath = Path.Combine(_tempDirectory.FullName, "screen.mp4");
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--capture-screen", "--duration-sec", "1", "-o", outputPath, "--json"]);

        Assert.AreEqual(0, exitCode);
        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual("screen", result.GetProperty("mode").GetString());
    }

    [TestMethod]
    public async Task Record_ElementNotFound_ReturnsElementNotFoundError()
    {
        // When a selector is given but the element is not found, the command must return
        // exit code 1 with an element_not_found error — not silently record the whole window.
        _fakeRecording.RecordException = new UiElementNotFoundException("btn-missing-a1b2");

        var outputPath = Path.Combine(_tempDirectory.FullName, "element-not-found.mp4");
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "btn-missing-a1b2", "-o", outputPath, "--json"]);

        Assert.AreEqual(1, exitCode);
        // Logger message (in ConsoleStdErr) confirms element-not-found path: contains the selector name.
        StringAssert.Contains(ConsoleStdErr.ToString(), "btn-missing-a1b2");
        // Output file must NOT exist (no recording was made).
        Assert.IsFalse(File.Exists(outputPath), "no output file should be written when the element is not found");
    }

    [TestMethod]
    public async Task Record_InvalidOutputPath_ReturnsStructuredError()
    {
        // When the output path is invalid (parent directory is a file), the command must return
        // exit code 1 with a clean error message — not an unhandled exception / stack trace.
        var blockingFile = Path.Combine(_tempDirectory.FullName, "blocker.txt");
        File.WriteAllText(blockingFile, "block");
        var badOutputPath = Path.Combine(blockingFile, "record.mp4"); // parent is a file

        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "-o", badOutputPath, "--json"]);

        Assert.AreEqual(1, exitCode);
        // Logger message (in ConsoleStdErr) confirms the path error was handled cleanly.
        StringAssert.Contains(ConsoleStdErr.ToString(), "Invalid output path");
    }

    [TestMethod]
    public async Task Record_RecordingFailure_DoesNotDeletePreexistingOutputFile()
    {
        // A pre-existing file at the output path must survive a recording failure.
        // The encoder writes to a temp file and only replaces the final path on Complete(),
        // so a failure must never clobber a file that existed before the recording attempt.
        var outputPath = Path.Combine(_tempDirectory.FullName, "preexisting.mp4");
        File.WriteAllText(outputPath, "sentinel content");

        _fakeRecording.RecordException = new InvalidOperationException("simulated capture failure");

        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "-o", outputPath, "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.IsTrue(File.Exists(outputPath), "pre-existing output file should survive a recording failure");
        Assert.AreEqual("sentinel content", File.ReadAllText(outputPath), "pre-existing file content should be unchanged");
    }

    [TestMethod]
    public async Task Record_ComFailure_ReturnsStructuredError()
    {
        _fakeRecording.RecordException = new System.Runtime.InteropServices.COMException(
            "simulated UIA COM failure", unchecked((int)0x80004005));

        var outputPath = Path.Combine(_tempDirectory.FullName, "com-fail.mp4");
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "-o", outputPath, "--json"]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "simulated UIA COM failure");
        Assert.IsFalse(File.Exists(outputPath));
    }
}
