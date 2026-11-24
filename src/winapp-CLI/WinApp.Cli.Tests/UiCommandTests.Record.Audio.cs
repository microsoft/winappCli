// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Commands;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    [TestMethod]
    public async Task Record_WithoutAudio_RecordsVideoOnly()
    {
        var output = Path.Join(_tempDirectory.FullName, "video-only.mp4");
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            GetRequiredService<UiRecordCommand>(), ["-a", "TestApp", "--duration-sec", "1", "-o", output, "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(RecordAudioMode.None, _fakeRecording.LastRecordOptions!.Audio);
        var result = JsonSerializer.Deserialize<JsonElement>(TestAnsiConsole.Output);
        Assert.IsFalse(result.TryGetProperty("audio", out _));
    }

    [TestMethod]
    [DataRow(new[] { "--audio" }, RecordAudioMode.App, DisplayName = "bare --audio records the app")]
    [DataRow(new[] { "--audio", "app" }, RecordAudioMode.App)]
    [DataRow(new[] { "--audio", "system" }, RecordAudioMode.System)]
    public async Task Record_Audio_PassesTheModeAndReportsTheTrack(string[] audioArgs, RecordAudioMode expected)
    {
        _fakeRecording.RecordResult = new RecordCaptureResult
        {
            Frames = 3,
            Mode = "wgc",
            Audio = new RecordAudioResult { Mode = expected == RecordAudioMode.App ? "app" : "system", SampleRate = 48_000, Channels = 2, PeakLevel = 0.5 },
        };
        var output = Path.Join(_tempDirectory.FullName, "with-audio.mp4");
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            GetRequiredService<UiRecordCommand>(),
            ["-a", "TestApp", .. audioArgs, "--duration-sec", "1", "-o", output, "--json"]);

        Assert.AreEqual(0, exitCode, ConsoleStdErr.ToString());
        Assert.AreEqual(expected, _fakeRecording.LastRecordOptions!.Audio);
        var audio = JsonSerializer.Deserialize<JsonElement>(TestAnsiConsole.Output).GetProperty("audio");
        Assert.AreEqual(expected == RecordAudioMode.App ? "app" : "system", audio.GetProperty("mode").GetString());
        Assert.AreEqual(48_000, audio.GetProperty("sampleRate").GetInt32());
        Assert.Contains($"\"audio\":\"{(expected == RecordAudioMode.App ? "app" : "system")}\"", ConsoleStdErr.ToString(),
            "The recording-started event names the audio mode.");
    }

    [TestMethod]
    public async Task Record_AudioWithUnknownSource_IsRejected()
    {
        var output = Path.Join(_tempDirectory.FullName, "mic.mp4");
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            GetRequiredService<UiRecordCommand>(),
            ["-a", "TestApp", "--audio", "mic", "--duration-sec", "1", "-o", output, "--json"]);

        Assert.AreNotEqual(0, exitCode);
        Assert.IsNull(_fakeRecording.LastRecordOptions);
    }

    [TestMethod]
    public async Task Record_AudioUnavailable_FailsWithoutAVideo()
    {
        _fakeRecording.RecordException = new RecordAudioUnavailableException(
            "Can't record system audio: this machine has no playback device.",
            "Enable a playback device (Settings > System > Sound), or record without --audio.");
        var output = Path.Join(_tempDirectory.FullName, "no-device.mp4");
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            GetRequiredService<UiRecordCommand>(),
            ["-a", "TestApp", "--audio", "system", "--duration-sec", "1", "-o", output, "--json"]);

        Assert.AreEqual(1, exitCode);
        var error = JsonSerializer.Deserialize<JsonElement>(ConsoleStdErr.ToString().Split('\n')[0]).GetProperty("error");
        Assert.AreEqual("audio_unavailable", error.GetProperty("code").GetString());
        Assert.Contains("no playback device", error.GetProperty("message").GetString()!);
        Assert.Contains("Enable a playback device", error.GetProperty("recoveryHint").GetString()!);
        Assert.IsFalse(File.Exists(output));
    }

    [TestMethod]
    [DataRow(new[] { "--audio" }, RecordAudioMode.System, null, DisplayName = "bare --audio records everything the desktop plays")]
    [DataRow(new[] { "--audio", "system" }, RecordAudioMode.System, null)]
    [DataRow(new[] { "--audio", "app" }, RecordAudioMode.None, "--audio app records one app's sound", DisplayName = "a desktop has no app to take sound from")]
    public void Validate_DesktopRecording_DefaultsToSystemAudioAndRefusesAppAudio(
        string[] audioArgs, RecordAudioMode expected, string? expectedError)
    {
        var parse = new GuestDesktopRecordCommand().Parse(
            ["--target-kind", "sandbox", "--target-name", "default", "--target-epoch", "e",
             "-o", Path.Join(_tempDirectory.FullName, "desktop.mp4"), .. audioArgs]);
        Assert.IsEmpty(parse.Errors);

        var error = UiRecordOptionValidator.Validate(parse, out var resolved, desktop: true);

        if (expectedError is null)
        {
            Assert.IsNull(error);
            Assert.AreEqual(expected, resolved!.Audio);
        }
        else
        {
            Assert.IsNotNull(error);
            Assert.Contains(expectedError, error.Message);
        }
    }
}
