// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    [TestMethod]
    [DataRow(null, null, null, 0x000000, DisplayName = "defaults: auto padding, black")]
    [DataRow("auto", "#00FF00", null, 0x00FF00, DisplayName = "auto, green screen")]
    [DataRow("40", "#1e1e1e", 40, 0x1E1E1E, DisplayName = "fixed padding, hex color")]
    [DataRow("0", "Magenta", 0, 0xFF00FF, DisplayName = "window only, named color")]
    public async Task Record_PaddingAndBackground_ArePassedToTheRecordingService(
        string? padding, string? background, int? expectedPadding, int expectedColor)
    {
        var output = Path.Join(_tempDirectory.FullName, "framed.mp4");
        List<string> args = ["-a", "TestApp", "--duration-sec", "1", "-o", output, "--json"];
        if (padding is not null)
        {
            args.AddRange(["--padding", padding]);
        }
        if (background is not null)
        {
            args.AddRange(["--background", background]);
        }

        var exitCode = await ParseAndInvokeWithCaptureAsync(GetRequiredService<UiRecordCommand>(), [.. args]);

        Assert.AreEqual(0, exitCode, ConsoleStdErr.ToString());
        Assert.AreEqual(expectedPadding, _fakeRecording.LastRecordOptions!.Padding);
        Assert.AreEqual(expectedColor, _fakeRecording.LastRecordOptions.BackgroundColor);
    }

    [TestMethod]
    [DataRow("--padding", "-5", "--padding must be 'auto' or a whole number")]
    [DataRow("--padding", "1001", "--padding must be 'auto' or a whole number")]
    [DataRow("--padding", "12px", "--padding must be 'auto' or a whole number")]
    [DataRow("--background", "#0F0", "--background must be #RRGGBB")]
    [DataRow("--background", "chartreuse", "--background must be #RRGGBB")]
    public async Task Record_InvalidPaddingOrBackground_FailsBeforeRecording(string option, string value, string expected)
    {
        var output = Path.Join(_tempDirectory.FullName, "invalid-framing.mp4");
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            GetRequiredService<UiRecordCommand>(), ["-a", "TestApp", "--duration-sec", "1", option, value, "-o", output]);

        Assert.AreEqual(1, exitCode);
        Assert.Contains(expected, ConsoleStdErr.ToString());
        Assert.IsNull(_fakeRecording.LastRecordOptions);
    }

    [TestMethod]
    [DataRow("--capture-screen")]
    [DataRow("btn-save")]
    public async Task Record_PaddingOnlyAppliesToWholeWindowRecordings(string notWholeWindow)
    {
        var output = Path.Join(_tempDirectory.FullName, "not-whole-window.mp4");
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            GetRequiredService<UiRecordCommand>(),
            [notWholeWindow, "-a", "TestApp", "--duration-sec", "1", "--padding", "40", "-o", output]);

        Assert.AreEqual(1, exitCode);
        Assert.Contains("only apply when recording a whole window", ConsoleStdErr.ToString());
        Assert.IsNull(_fakeRecording.LastRecordOptions);
    }
}
