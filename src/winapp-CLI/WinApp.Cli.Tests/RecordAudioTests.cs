// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;

namespace WinApp.Cli.Tests;

/// <summary>The audio track: where loopback packets land, silence fill, and refusing silent video.</summary>
[TestClass]
[DoNotParallelize]
public sealed class RecordAudioTests
{
    private const int Rate = 48_000;
    private const long Second = 10_000_000;

    private static readonly PcmFormat Format = LoopbackAudioCapture.AudioFormat;

    private readonly Func<string, int, int, int, uint, IVideoEncoder> _originalEncoder = Mp4SinkWriterEncoder.s_createNoClobber;
    private readonly Func<string, int, int, int, uint, PcmFormat, IVideoEncoder> _originalAudioEncoder =
        Mp4SinkWriterEncoder.s_createNoClobberWithAudio;
    private readonly Func<RecordAudioMode, int, ILoopbackAudioSource> _originalOpenAudio = UiRecordingService.s_openAudioSource;
    private string _root = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = TestPaths.TempRoot(nameof(RecordAudioTests));
        Directory.CreateDirectory(_root);
        Mp4SinkWriterEncoder.s_createNoClobber = (_, _, _, _, _) =>
            throw new AssertFailedException("A recording with audio must use the audio encoder.");
    }

    [TestCleanup]
    public void Cleanup()
    {
        Mp4SinkWriterEncoder.s_createNoClobber = _originalEncoder;
        Mp4SinkWriterEncoder.s_createNoClobberWithAudio = _originalAudioEncoder;
        UiRecordingService.s_openAudioSource = _originalOpenAudio;
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }

    [TestMethod]
    public void EstimatePacketStarts_TrustsBelievableTimestampsAndBacksMissingOnesFromTheReadTime()
    {
        // 480 frames = 10 ms. The Sandbox's Remote Audio device reports no usable timestamps, so
        // its packets are assumed to have just finished playing, the last one ending at the read.
        var missing = AudioTimeline.EstimatePacketStarts([(480, 0), (480, 0)], readHns: Second, Rate);
        Assert.AreSequenceEqual(new[] { Second - 200_000, Second - 100_000 }, missing);

        var trusted = AudioTimeline.EstimatePacketStarts([(480, Second - 50_000)], readHns: Second, Rate);
        Assert.AreSequenceEqual(new[] { Second - 50_000 }, trusted);

        var implausible = AudioTimeline.EstimatePacketStarts([(480, Second - 3 * Second)], readHns: Second, Rate);
        Assert.AreSequenceEqual(new[] { Second - 100_000 }, implausible);

        var future = AudioTimeline.EstimatePacketStarts([(480, Second + 8_000_000)], readHns: Second, Rate);
        Assert.AreSequenceEqual(new[] { Second - 100_000 }, future, "A packet can't start after it was read.");
    }

    [TestMethod]
    public void Recorder_DropsSoundPastTheEndOfTheVideo()
    {
        var now = 0L;
        var source = new FakeAudioSource();
        var writer = new CollectingAudioWriter();
        using var recorder = new AudioTrackRecorder(source, writer, Format, () => now);

        // A loud packet that played from 0.9 s to 3.0 s arrives as recording stops at 1.0 s.
        now = 3 * Second;
        source.Packets.Enqueue((Tone(Rate * 21 / 10, 1_000), Rate * 21 / 10, 0));
        recorder.Finish(endHns: Second);

        Assert.AreEqual(Second, writer.EndHns, "The audio ends with the video.");
        var samples = writer.Samples();
        Assert.AreEqual(Rate * 9 / 10, Array.FindIndex(samples, sample => sample != 0) / Format.Channels,
            "Only the part that played before the video's end, 0.9-1.0 s, is kept.");
        writer.AssertContiguous();
    }

    [TestMethod]
    [DataRow(1_000L, 6_000L, 480, 5_000L, 0, DisplayName = "gap -> silence first")]
    [DataRow(6_000L, 1_000L, 480, 0L, 480, DisplayName = "overlap longer than the packet -> drop it all")]
    [DataRow(6_000L, 3_000L, 4_000, 0L, 3_000, DisplayName = "overlap -> drop the overlapping start")]
    [DataRow(1_000L, 1_100L, 480, 0L, 0, DisplayName = "jitter -> append as is")]
    public void Place_FillsGapsTrimsOverlapsAndIgnoresJitter(
        long written, long start, int frames, long expectedSilence, int expectedSkip)
    {
        Assert.AreEqual((expectedSilence, expectedSkip), AudioTimeline.Place(written, start, frames, toleranceFrames: 2_880));
    }

    [TestMethod]
    public void IdleSilence_KeepsTheTrackALittleBehindNow()
    {
        Assert.AreEqual(24_000, AudioTimeline.IdleSilence(0, nowFrames: 48_000, lagFrames: 24_000, toleranceFrames: 2_880));
        Assert.AreEqual(0, AudioTimeline.IdleSilence(23_000, nowFrames: 48_000, lagFrames: 24_000, toleranceFrames: 2_880));
    }

    [TestMethod]
    public void NextVideoSlot_FollowsTheClockWhenCaptureFallsBehind()
    {
        Assert.AreEqual(0, AudioTimeline.NextVideoSlot(-1, elapsedMs: 0, fps: 30));
        Assert.AreEqual(1, AudioTimeline.NextVideoSlot(0, elapsedMs: 20, fps: 30), "Early frames still advance one slot.");
        Assert.AreEqual(15, AudioTimeline.NextVideoSlot(5, elapsedMs: 500, fps: 30), "A late frame skips to its own time.");
    }

    [TestMethod]
    public void TimestampToHns_DoesNotOverflowOnALongUptime()
    {
        const long frequency = 24_000_000;
        const long thirtyDays = 86_400L * 30;
        Assert.AreEqual(thirtyDays * Second, AudioTimeline.TimestampToHns(frequency * thirtyDays, frequency));
    }

    [TestMethod]
    public void Recorder_FillsSilenceAroundPacketsSoTheTrackCoversTheWholeVideo()
    {
        var now = 0L;
        var source = new FakeAudioSource();
        var writer = new CollectingAudioWriter();
        // Not started, so no background pump races the test: it drives Pump itself, with time zero at 0.
        using var recorder = new AudioTrackRecorder(source, writer, Format, () => now);

        now = Second;
        recorder.Pump();
        Assert.AreEqual(Rate / 2, writer.Frames, "With nothing playing, silence keeps up to 500 ms behind now.");

        now = 2 * Second;
        source.Packets.Enqueue((Tone(480, 1_000), 480, 0));
        recorder.Pump();

        recorder.Finish(endHns: 3 * Second);

        var samples = writer.Samples();
        Assert.AreEqual(3 * Rate, samples.Length / Format.Channels, "The track ends where the video ends.");
        var firstSound = Array.FindIndex(samples, sample => sample != 0) / Format.Channels;
        Assert.AreEqual(2 * Rate - 480, firstSound, "The packet sits where it finished playing, at 1.99 s.");
        Assert.AreEqual(1_000 / 32768.0, recorder.PeakLevel, 1e-9);
        writer.AssertContiguous();
    }

    [TestMethod]
    public async Task Record_AudioUnavailable_FailsBeforeCreatingAnyVideo()
    {
        UiRecordingService.s_openAudioSource = (_, _) => throw new RecordAudioUnavailableException(
            "Can't record system audio: this machine has no playback device.", "Enable a playback device.");
        Mp4SinkWriterEncoder.s_createNoClobberWithAudio = (_, _, _, _, _, _) =>
            throw new AssertFailedException("No encoder may be created when audio is unavailable.");
        var output = Path.Join(_root, "silent.mp4");

        var error = await Assert.ThrowsExactlyAsync<RecordAudioUnavailableException>(() => Recorder().RecordDesktopAsync(
            new RecordOptions { OutputPath = output, DurationSec = 1, Fps = 5, Audio = RecordAudioMode.System },
            TestContext.CancellationToken));

        Assert.Contains("no playback device", error.Message);
        Assert.IsFalse(File.Exists(output));
    }

    [TestMethod]
    public async Task Record_WithAudio_ReportsTheTrackAndKeepsItWhenReplacingAnEarlierTake()
    {
        var output = Path.Join(_root, "take.mp4");
        await File.WriteAllTextAsync(output, "earlier take", TestContext.CancellationToken);
        RecordAudioMode? opened = null;
        UiRecordingService.s_openAudioSource = (mode, _) =>
        {
            opened = mode;
            return new FakeAudioSource();
        };
        AudioEncoder? encoder = null;
        Mp4SinkWriterEncoder.s_createNoClobberWithAudio = (path, width, height, _, _, format) =>
        {
            Assert.AreEqual(Format, format);
            return encoder = new AudioEncoder(path, width, height);
        };

        var result = await Recorder().RecordDesktopAsync(
            new RecordOptions { OutputPath = output, Overwrite = true, DurationSec = 1, Fps = 5, Audio = RecordAudioMode.System },
            TestContext.CancellationToken);

        Assert.AreEqual(RecordAudioMode.System, opened);
        Assert.IsNotNull(result.Audio, "Replacing a take must not drop the audio description.");
        Assert.AreEqual("system", result.Audio.Mode);
        Assert.AreEqual((48_000, 2, "aac"), (result.Audio.SampleRate, result.Audio.Channels, result.Audio.Codec));
        Assert.AreEqual(0, result.Audio.PeakLevel);
        Assert.IsTrue(result.Warnings!.Any(warning => warning.Contains("audio track is silent")));
        Assert.AreEqual(encoder!.VideoEndHns, encoder.Audio.EndHns, "Audio and video end together.");
        Assert.AreEqual(encoder.VideoEndHns / 10_000, result.Audio.DurationMs);
        encoder.Audio.AssertContiguous();
    }

    [TestMethod]
    public async Task Record_AppAudioOfAWholeDesktop_IsRefused()
    {
        UiRecordingService.s_openAudioSource = (_, _) => throw new AssertFailedException("Nothing to open.");
        var output = Path.Join(_root, "desktop-app.mp4");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => Recorder().RecordDesktopAsync(
            new RecordOptions { OutputPath = output, DurationSec = 1, Fps = 5, Audio = RecordAudioMode.App },
            TestContext.CancellationToken));
        Assert.IsFalse(File.Exists(output));
    }

    private static UiRecordingService Recorder() => new(
        new FakeUiAutomationService(),
        new FakeWindowCapture
        {
            DesktopBoundsOverride = () => new PointerRect(0, 0, 64, 64),
            CaptureScreenOverride = (_, _, _, _, ew, eh, _, _) => new byte[ew * eh * 4],
            CaptureWindowOverride = (_, _, _) => throw new AssertFailedException("No window capture is allowed."),
            StartGrabberCallback = (_, _) => throw new AssertFailedException("No window grabber is allowed."),
        },
        new NoSelectors(),
        NullLogger<UiRecordingService>.Instance);

    private static byte[] Tone(int frames, short level)
    {
        var samples = new short[frames * Format.Channels];
        Array.Fill(samples, level);
        return MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
    }

    private sealed class NoSelectors : IUiSelectorParser
    {
        public UiSelector Parse(string selector) => throw new AssertFailedException("Desktop recording resolves no elements.");
    }

    private sealed class FakeAudioSource : ILoopbackAudioSource
    {
        public Queue<(byte[] Pcm, int Frames, long QpcHns)> Packets { get; } = new();

        public void Start() { }

        public void Drain(LoopbackPacketHandler onPacket)
        {
            lock (Packets)
            {
                while (Packets.TryDequeue(out var packet))
                {
                    onPacket(packet.Pcm, packet.Frames, packet.QpcHns, silent: false);
                }
            }
        }

        public void Dispose() { }
    }

    private sealed class CollectingAudioWriter : IAudioSampleWriter
    {
        private readonly List<(long Time, long Duration, byte[] Pcm)> _chunks = [];

        public long Frames => _chunks.Sum(chunk => (long)chunk.Pcm.Length) / Format.BlockAlign;

        public long EndHns => _chunks.Count == 0 ? 0 : _chunks[^1].Time + _chunks[^1].Duration;

        public void WriteAudio(ReadOnlySpan<byte> pcm, long sampleTimeHns, long sampleDurationHns)
        {
            lock (_chunks)
            {
                _chunks.Add((sampleTimeHns, sampleDurationHns, pcm.ToArray()));
            }
        }

        public short[] Samples() => MemoryMarshal.Cast<byte, short>(_chunks.SelectMany(chunk => chunk.Pcm).ToArray()).ToArray();

        public void AssertContiguous()
        {
            long expected = 0;
            foreach (var (time, duration, _) in _chunks)
            {
                Assert.AreEqual(expected, time, "Audio samples must follow one another without gaps.");
                expected = time + duration;
            }
        }
    }

    private sealed class AudioEncoder(string path, int width, int height) : IVideoEncoder, IAudioSampleWriter
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public long VideoEndHns { get; private set; }
        public CollectingAudioWriter Audio { get; } = new();

        public void WriteFrame(ReadOnlySpan<byte> bgra, long sampleTimeHns, long sampleDurationHns) =>
            VideoEndHns = sampleTimeHns + sampleDurationHns;

        public void WriteAudio(ReadOnlySpan<byte> pcm, long sampleTimeHns, long sampleDurationHns) =>
            Audio.WriteAudio(pcm, sampleTimeHns, sampleDurationHns);

        public void Complete() => File.WriteAllBytes(path, [1, 2, 3, 4]);

        public void Dispose() { }
    }
}
