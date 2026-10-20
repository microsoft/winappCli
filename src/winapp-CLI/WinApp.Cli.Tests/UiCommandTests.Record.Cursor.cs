// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    [TestMethod]
    public async Task Record_Cursor_IsOffByDefault()
    {
        var output = Path.Join(_tempDirectory.FullName, "no-cursor.mp4");
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            GetRequiredService<UiRecordCommand>(), ["-a", "TestApp", "--duration-sec", "1", "-o", output, "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.IsFalse(_fakeRecording.LastRecordOptions!.Cursor);
        Assert.IsTrue(_fakeRecording.LastRecordOptions.ClickHighlight);
    }

    [TestMethod]
    public async Task Record_CursorAndNoClickHighlight_ArePassedToTheRecordingService()
    {
        var output = Path.Join(_tempDirectory.FullName, "cursor.mp4");
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            GetRequiredService<UiRecordCommand>(),
            ["-a", "TestApp", "--duration-sec", "1", "--cursor", "--no-click-highlight", "-o", output, "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(_fakeRecording.LastRecordOptions!.Cursor);
        Assert.IsFalse(_fakeRecording.LastRecordOptions.ClickHighlight);
    }

    [TestMethod]
    public async Task Record_NoClickHighlightWithoutCursor_FailsBeforeRecording()
    {
        var output = Path.Join(_tempDirectory.FullName, "rings-only.mp4");
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            GetRequiredService<UiRecordCommand>(),
            ["-a", "TestApp", "--duration-sec", "1", "--no-click-highlight", "-o", output, "--json"]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "--no-click-highlight only applies with --cursor");
        Assert.IsNull(_fakeRecording.LastRecordOptions);
        Assert.IsFalse(File.Exists(output));
    }
}
