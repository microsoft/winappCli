// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording;

/// <summary>Accepts interleaved PCM for the recording's audio track.</summary>
internal interface IAudioSampleWriter
{
    /// <param name="pcm">Interleaved samples in the format the track was created with.</param>
    /// <param name="sampleTimeHns">Where the samples start on the recording's timeline, in 100 ns units.</param>
    /// <param name="sampleDurationHns">How long the samples last, in 100 ns units.</param>
    void WriteAudio(ReadOnlySpan<byte> pcm, long sampleTimeHns, long sampleDurationHns);
}

/// <summary>Where loopback audio lands on the recording's timeline.</summary>
/// <remarks>
/// Loopback is not a continuous stream: with nothing playing, the default device delivers no packets
/// at all, and some devices (the Windows Sandbox "Remote Audio" endpoint) report no usable timestamps.
/// These rules place every packet where it was played and fill the gaps with silence, so the audio
/// track covers the whole video and stays in sync with it.
/// </remarks>
internal static class AudioTimeline
{
    private const long HnsPerSecond = 10_000_000;

    /// <summary>A reported packet time more than this before the packet was read, or any time after it, is not trusted.</summary>
    internal const long MaxTimestampSkewHns = 2 * HnsPerSecond;

    /// <summary>Drift smaller than this is jitter, not a gap or an overlap, and is left alone.</summary>
    internal const long ToleranceHns = 60 * 10_000;

    /// <summary>
    /// While nothing is delivered, silence is written up to this far behind now. Packets for sound
    /// played in that window can still arrive late and be placed after it.
    /// </summary>
    internal const long IdleFillLagHns = 500 * 10_000;

    /// <summary>Converts a <see cref="Stopwatch"/> timestamp to 100 ns units without overflowing.</summary>
    internal static long TimestampToHns(long timestamp, long frequency) =>
        timestamp / frequency * HnsPerSecond + timestamp % frequency * HnsPerSecond / frequency;

    internal static long FramesToHns(long frames, int sampleRate) => frames * HnsPerSecond / sampleRate;

    internal static long HnsToFrames(long hns, int sampleRate) => hns * sampleRate / HnsPerSecond;

    /// <summary>The largest absolute value among little-endian 16-bit samples.</summary>
    internal static int Peak16(ReadOnlySpan<byte> pcm)
    {
        var peak = 0;
        foreach (var sample in MemoryMarshal.Cast<byte, short>(pcm))
        {
            peak = Math.Max(peak, Math.Abs((int)sample));
        }
        return peak;
    }

    /// <summary>
    /// Works out when each packet of a batch started, on the same clock as <paramref name="readHns"/>.
    /// </summary>
    /// <param name="packets">The frame count and reported start time of each packet, oldest first. A
    /// reported time of 0 or less means the device gave none.</param>
    /// <param name="readHns">When the batch was read.</param>
    /// <param name="sampleRate">Frames per second.</param>
    /// <returns>Each packet's start time.</returns>
    /// <remarks>
    /// A packet without a believable timestamp is assumed to have just finished playing when the batch
    /// was read, behind the packets that follow it in the same batch.
    /// </remarks>
    internal static long[] EstimatePacketStarts(
        IReadOnlyList<(int Frames, long ReportedHns)> packets, long readHns, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(packets);

        var starts = new long[packets.Count];
        long framesFromHereToEnd = 0;
        for (var index = packets.Count - 1; index >= 0; index--)
        {
            framesFromHereToEnd += packets[index].Frames;
            var reported = packets[index].ReportedHns;
            starts[index] = reported > 0 && reported <= readHns && readHns - reported <= MaxTimestampSkewHns
                ? reported
                : readHns - FramesToHns(framesFromHereToEnd, sampleRate);
        }
        return starts;
    }

    /// <summary>Decides how a packet joins the track written so far.</summary>
    /// <param name="writtenFrames">Frames already on the track.</param>
    /// <param name="packetStartFrame">Where the packet belongs on the track.</param>
    /// <param name="packetFrames">The packet's length.</param>
    /// <param name="toleranceFrames">Drift to ignore.</param>
    /// <returns>
    /// Silence to write before the packet (it starts after the track ends), and frames to drop from
    /// its start (it overlaps what is already written, or started before the recording).
    /// </returns>
    internal static (long SilenceFrames, int SkipFrames) Place(
        long writtenFrames, long packetStartFrame, int packetFrames, long toleranceFrames)
    {
        var gap = packetStartFrame - writtenFrames;
        if (gap > toleranceFrames)
        {
            return (gap, 0);
        }
        if (-gap > toleranceFrames)
        {
            return (0, (int)Math.Min(-gap, packetFrames));
        }
        return (0, 0);
    }

    /// <summary>Silence to write while nothing is arriving, so the track keeps up with the video.</summary>
    /// <param name="writtenFrames">Frames already on the track.</param>
    /// <param name="nowFrames">Frames since the recording started.</param>
    /// <param name="lagFrames">How far behind now to stop, leaving room for late packets.</param>
    /// <param name="toleranceFrames">Shortfall to ignore.</param>
    internal static long IdleSilence(long writtenFrames, long nowFrames, long lagFrames, long toleranceFrames)
    {
        var target = nowFrames - lagFrames;
        return target - writtenFrames > toleranceFrames ? target - writtenFrames : 0;
    }

    /// <summary>
    /// The media slot for the next video frame when the video carries audio.
    /// </summary>
    /// <param name="previousSlot">The previous frame's slot, or -1 before the first frame.</param>
    /// <param name="elapsedMs">Time since the recording started.</param>
    /// <param name="fps">Requested frame rate.</param>
    /// <remarks>
    /// Without audio, frames are numbered back to back and a slow capture just plays back faster.
    /// With audio that would drift away from the sound, so a frame captured late takes the slot of
    /// the time it was captured, and the previous frame stays on screen through the skipped slots.
    /// </remarks>
    internal static long NextVideoSlot(long previousSlot, double elapsedMs, int fps)
    {
        var wallSlot = (long)Math.Floor(elapsedMs * fps / 1000.0);
        return Math.Max(previousSlot + 1, wallSlot);
    }
}

/// <summary>Moves loopback audio onto the recording's audio track on a background thread.</summary>
internal sealed class AudioTrackRecorder : IDisposable
{
    private const int PollIntervalMs = 10;

    private readonly ILoopbackAudioSource _source;
    private readonly IAudioSampleWriter _writer;
    private readonly PcmFormat _format;
    private readonly Func<long> _nowHns;
    private readonly long _toleranceFrames;
    private readonly long _idleLagFrames;
    private readonly int _flushFrames;
    private byte[] _pending;
    private readonly List<(byte[]? Pcm, int Frames, long ReportedHns)> _batch = [];
    private readonly ManualResetEventSlim _stop = new();
    private Thread? _thread;
    private long _startHns;
    private long _writtenFrames;
    private long _endFrames = long.MaxValue;

    // How far the video has been written, in audio frames. Audio is held back until the video
    // reaches it, so audio captured after the last frame never makes the track longer than the video.
    private long _committedFrames;
    private int _peak;
    private int _pendingFrames;
    private bool _finished;
    private bool _disposed;

    /// <param name="source">The loopback stream, already opened.</param>
    /// <param name="writer">Where the track's samples go.</param>
    /// <param name="format">The format both sides use.</param>
    /// <param name="nowHns">The recording clock, in 100 ns units; defaults to <see cref="Stopwatch"/>.</param>
    public AudioTrackRecorder(ILoopbackAudioSource source, IAudioSampleWriter writer, PcmFormat format, Func<long>? nowHns = null)
    {
        _source = source;
        _writer = writer;
        _format = format;
        _nowHns = nowHns ?? (static () => AudioTimeline.TimestampToHns(Stopwatch.GetTimestamp(), Stopwatch.Frequency));
        _toleranceFrames = AudioTimeline.HnsToFrames(AudioTimeline.ToleranceHns, format.SampleRate);
        _idleLagFrames = AudioTimeline.HnsToFrames(AudioTimeline.IdleFillLagHns, format.SampleRate);

        // Hand the encoder about 100 ms at a time.
        _flushFrames = format.SampleRate / 10;
        _pending = new byte[format.BytesPerSecond];
    }

    /// <summary>Why capture stopped early, or null. The track is still completed with silence.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>Frames on the track.</summary>
    public long WrittenFrames => _writtenFrames;

    /// <summary>The loudest sample captured so far, from 0 to 1.</summary>
    public double PeakLevel => _peak / 32768.0;

    /// <summary>Starts the stream and the background pump. Time zero on the track is <paramref name="startHns"/>.</summary>
    /// <exception cref="RecordAudioUnavailableException">The device failed on the first read.</exception>
    public void Start(long startHns)
    {
        _startHns = startHns;
        _source.Start();

        // The first read must work, so a device that fails right away fails the command instead of
        // producing a silent track. Later failures only end the sound early.
        Pump(firstRead: true);
        _thread = new Thread(Run) { IsBackground = true, Name = "winapp audio capture" };
        _thread.Start();
    }

    /// <summary>Records that the video now reaches <paramref name="endHns"/>, so audio up to there can be written.</summary>
    public void CommitVideo(long endHns) =>
        Volatile.Write(ref _committedFrames, AudioTimeline.HnsToFrames(endHns, _format.SampleRate));

    /// <summary>
    /// Stops capturing and completes the track up to <paramref name="endHns"/> on the track's own
    /// timeline (the video's length), with silence where nothing played.
    /// </summary>
    public void Finish(long endHns)
    {
        if (_finished)
        {
            return;
        }
        _finished = true;
        _stop.Set();
        _thread?.Join();

        // Nothing past the video's end joins the track, including sound still waiting in the
        // buffer or arriving in the last drain.
        _endFrames = AudioTimeline.HnsToFrames(endHns, _format.SampleRate);
        Volatile.Write(ref _committedFrames, _endFrames);
        _pendingFrames = (int)Math.Clamp(_endFrames - _writtenFrames, 0, _pendingFrames);

        Pump();

        // A chunk at a time, so a long silent tail (after the device failed, say) never piles up.
        var remaining = _endFrames - _writtenFrames - _pendingFrames;
        while (remaining > 0)
        {
            var step = Math.Min(remaining, _flushFrames);
            AppendSilence(step);
            WriteCommitted(final: false);
            remaining -= step;
        }
        WriteCommitted(final: true);
    }

    /// <summary>Reads what has arrived and appends it to the track. Runs on the pump thread.</summary>
    internal void Pump(bool firstRead = false)
    {
        _batch.Clear();
        if (Failure is null)
        {
            try
            {
                _source.Drain((pcm, frames, qpcHns, silent) =>
                    _batch.Add((silent ? null : pcm.ToArray(), frames, qpcHns)));
            }
            catch (Exception ex) when (!firstRead && ex is not OutOfMemoryException)
            {
                // The device went away. Keep the track going with silence so it still covers the
                // video; the caller reports the failure as a warning.
                Failure = ex;
                _batch.Clear();
            }
        }
        var nowHns = _nowHns();
        var nowFrames = AudioTimeline.HnsToFrames(nowHns - _startHns, _format.SampleRate);

        if (_batch.Count == 0)
        {
            AppendSilence(AudioTimeline.IdleSilence(
                _writtenFrames + _pendingFrames, nowFrames, _idleLagFrames, _toleranceFrames));
            WriteCommitted(final: false);
            return;
        }

        var starts = AudioTimeline.EstimatePacketStarts(
            _batch.ConvertAll(packet => (packet.Frames, packet.ReportedHns)), nowHns, _format.SampleRate);
        for (var index = 0; index < _batch.Count; index++)
        {
            var (pcm, frames, _) = _batch[index];
            var startFrame = AudioTimeline.HnsToFrames(starts[index] - _startHns, _format.SampleRate);
            var (silence, skip) = AudioTimeline.Place(
                _writtenFrames + _pendingFrames, startFrame, frames, _toleranceFrames);
            AppendSilence(silence);
            if (skip >= frames)
            {
                continue;
            }

            var kept = frames - skip;
            if (pcm is null)
            {
                AppendSilence(kept);
            }
            else
            {
                Append(pcm.AsSpan(skip * _format.BlockAlign, kept * _format.BlockAlign));
            }
        }
        WriteCommitted(final: false);
    }

    private void Run()
    {
        try
        {
            while (!_stop.Wait(PollIntervalMs))
            {
                Pump();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The encoder refused audio. Finish surfaces the error again when it writes.
            Failure ??= ex;
        }
    }

    /// <summary>Frames the track can still take before the video's end, once that is known.</summary>
    private long Room => _endFrames - _writtenFrames - _pendingFrames;

    private void AppendSilence(long frames)
    {
        frames = Math.Min(frames, Room);
        if (frames <= 0)
        {
            return;
        }
        var bytes = Reserve((int)frames);
        _pending.AsSpan(bytes.Offset, bytes.Length).Clear();
    }

    private void Append(ReadOnlySpan<byte> pcm)
    {
        var room = Math.Max(0, Room);
        if (pcm.Length / _format.BlockAlign > room)
        {
            pcm = pcm[..(int)(room * _format.BlockAlign)];
        }
        if (pcm.IsEmpty)
        {
            return;
        }
        _peak = Math.Max(_peak, AudioTimeline.Peak16(pcm));
        var bytes = Reserve(pcm.Length / _format.BlockAlign);
        pcm.CopyTo(_pending.AsSpan(bytes.Offset, bytes.Length));
    }

    /// <summary>Makes room for <paramref name="frames"/> more pending frames and returns where they go.</summary>
    private (int Offset, int Length) Reserve(int frames)
    {
        var offset = _pendingFrames * _format.BlockAlign;
        var length = frames * _format.BlockAlign;
        if (offset + length > _pending.Length)
        {
            Array.Resize(ref _pending, Math.Max(_pending.Length * 2, offset + length));
        }
        _pendingFrames += frames;
        return (offset, length);
    }

    /// <summary>
    /// Hands pending audio the video has already reached to the encoder, about 100 ms at a time.
    /// With <paramref name="final"/>, also the last partial chunk.
    /// </summary>
    private void WriteCommitted(bool final)
    {
        var consumed = 0;
        while (consumed < _pendingFrames)
        {
            var available = Math.Min(_pendingFrames - consumed, Volatile.Read(ref _committedFrames) - _writtenFrames);
            var count = (int)Math.Min(available, _flushFrames);
            if (count <= 0 || (count < _flushFrames && !final))
            {
                break;
            }

            var startFrame = _writtenFrames;
            _writer.WriteAudio(
                _pending.AsSpan(consumed * _format.BlockAlign, count * _format.BlockAlign),
                AudioTimeline.FramesToHns(startFrame, _format.SampleRate),
                AudioTimeline.FramesToHns(startFrame + count, _format.SampleRate) -
                    AudioTimeline.FramesToHns(startFrame, _format.SampleRate));
            _writtenFrames += count;
            consumed += count;
        }

        if (consumed > 0)
        {
            _pendingFrames -= consumed;
            _pending.AsSpan(consumed * _format.BlockAlign, _pendingFrames * _format.BlockAlign).CopyTo(_pending);
        }
    }
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _stop.Set();
        _thread?.Join();
        _stop.Dispose();
        _source.Dispose();
    }
}
