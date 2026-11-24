// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording;

// Source-generated ([GeneratedComInterface]) COM objects are ComWrappers RCWs, not classic
// RCWs, so Marshal.ReleaseComObject throws for them. The ComObject wrapper implements IDisposable,
// whose Dispose() deterministically releases the underlying IUnknown — that is the AOT-safe way to
// drop a COM reference. This local helper centralizes that pattern.

/// <summary>
/// Encodes a sequence of BGRA (RGB32) frames to an H.264 MP4 file using the
/// Windows Media Foundation <c>IMFSinkWriter</c>. Frames are written
/// incrementally so the full sequence never has to be held in memory.
/// </summary>
/// <remarks>
/// Input frames are treated as top-down RGB32 (BGRA byte order, matching the
/// pixel layout produced by <see cref="WgcCapture"/> and GDI captures). The
/// sink writer inserts the color-conversion + H.264 encoder MFTs automatically.
/// <para>
/// <b>Atomicity:</b> frames are written to a temp sibling file and moved to the
/// final path only on <see cref="Complete"/>. This means a pre-existing file at
/// the output path is never touched unless recording completes successfully, and
/// a constructor or capture failure never leaves a corrupt file at the final path.
/// </para>
/// </remarks>
internal interface IVideoEncoder : IDisposable
{
    int Width { get; }

    int Height { get; }

    void WriteFrame(ReadOnlySpan<byte> bgra, long sampleTimeHns, long sampleDurationHns);

    void Complete();
}

internal sealed unsafe class Mp4SinkWriterEncoder : IVideoEncoder, IAudioSampleWriter
{
    // MF_VERSION for Windows 7+ (MF_SDK_VERSION 0x0002, MF_API_VERSION 0x0070).
    private const uint MF_VERSION = 0x00020070;
    private const uint MFSTARTUP_FULL = 0;
    private const uint MFVideoInterlace_Progressive = 2;

    // AAC-LC at 192 kbps. The Media Foundation AAC encoder accepts 12000, 16000, 20000, or 24000 bytes/s.
    internal const int AacBytesPerSecond = 24_000;

    private readonly IMFSinkWriter _writer;
    private readonly uint _streamIndex;
    private readonly uint _audioStreamIndex;
    private readonly PcmFormat? _audioFormat;

    // The sink writer is fed from both the capture loop and the audio thread.
    private readonly Lock _writeLock = new();
    private readonly uint _frameBytes;
    private readonly string _path;
    private readonly string _tempPath;
    private readonly bool _overwriteExisting;
    private bool _mfStarted;
    private bool _finalized;   // writer.Finalize() completed
    private bool _fileMoved;   // temp → final move succeeded
    private bool _disposed;

    public int Width { get; }

    public int Height { get; }

    /// <remarks>
    /// Coverage ceiling (issue #630): tests cover successful encoding on hosts with Media
    /// Foundation and constructor cleanup via seams. Remaining uncovered lines are MFStartup/
    /// sink-writer/media-type native initialization and native failure cleanup arms that require
    /// faulting COM objects after creation.
    /// </remarks>
    public Mp4SinkWriterEncoder(
        string path, int width, int height, int fps, uint bitrate, bool overwriteExisting = true, PcmFormat? audioFormat = null)
    {
        _audioFormat = audioFormat;
        Width = width;
        Height = height;
        _path = path;
        _overwriteExisting = overwriteExisting;
        _frameBytes = checked((uint)(width * height * 4));

        // Write to a temp sibling so that a pre-existing file at _path is never corrupted
        // if construction or encoding fails. Complete() atomically moves temp → final.
        var dir = Path.GetDirectoryName(path);
        _tempPath = Path.Combine(
            string.IsNullOrEmpty(dir) ? "." : dir,
            Guid.NewGuid().ToString("N") + ".mp4");

        IMFMediaType? outType = null;
        IMFMediaType? inType = null;
        try
        {
            // Test-only fault injection seam: when set, bypasses MF so tests can verify
            // that the constructor catch block deletes the temp file without needing MF hardware.
            // The delegate creates a placeholder file at _tempPath and then throws.
            // Always null in production code. Tests must clear this field in cleanup.
            if (s_testFaultAfterTempCreate is { } testFault)
            {
                s_testFaultAfterTempCreate = null;
                File.WriteAllBytes(_tempPath, []); // simulate the file MFCreateSinkWriterFromURL creates
                testFault();                        // must throw — exercises the catch below
            }

            PInvoke.MFStartup(MF_VERSION, MFSTARTUP_FULL).ThrowOnFailure();
            _mfStarted = true;

            PInvoke.MFCreateSinkWriterFromURL(_tempPath, null, null, out _writer).ThrowOnFailure();

            // Output (encoded) media type: H.264.
            PInvoke.MFCreateMediaType(out outType).ThrowOnFailure();
            outType.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            outType.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_H264);
            outType.SetUINT32(PInvoke.MF_MT_AVG_BITRATE, bitrate);
            outType.SetUINT32(PInvoke.MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
            outType.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, PackU64((uint)width, (uint)height));
            outType.SetUINT64(PInvoke.MF_MT_FRAME_RATE, PackU64((uint)fps, 1));
            outType.SetUINT64(PInvoke.MF_MT_PIXEL_ASPECT_RATIO, PackU64(1, 1));
            _writer.AddStream(outType, out _streamIndex);

            // Input (uncompressed) media type: RGB32, top-down (positive stride).
            PInvoke.MFCreateMediaType(out inType).ThrowOnFailure();
            inType.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            inType.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_RGB32);
            inType.SetUINT32(PInvoke.MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
            inType.SetUINT32(PInvoke.MF_MT_DEFAULT_STRIDE, (uint)(width * 4));
            inType.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, PackU64((uint)width, (uint)height));
            inType.SetUINT64(PInvoke.MF_MT_FRAME_RATE, PackU64((uint)fps, 1));
            inType.SetUINT64(PInvoke.MF_MT_PIXEL_ASPECT_RATIO, PackU64(1, 1));
            _writer.SetInputMediaType(_streamIndex, inType, null);

            if (audioFormat is { } audio)
            {
                _audioStreamIndex = AddAudioStream(_writer, audio);
            }

            _writer.BeginWriting();
        }
        catch (Exception ex)
        {
            // Constructor failed — release any partial writer and undo MFStartup.
            ReleaseCom(_writer);
            // Delete the temp file so it is never orphaned on construction failure.
            // Dispose() won't run because the constructor is throwing, so we must do it here.
            try
            {
                if (File.Exists(_tempPath))
                {
                    File.Delete(_tempPath);
                }
            }
            catch
            {
                // Best-effort cleanup of the temp file.
            }
            if (_mfStarted)
            {
                try
                {
                    PInvoke.MFShutdown();
                }
                catch
                {
                    // Best-effort shutdown.
                }
                _mfStarted = false;
            }
            if (TryDescribeEncoderInitFailure(ex, out var message))
            {
                throw new Mp4EncoderInitializationException(message, ex);
            }
            throw;
        }
        finally
        {
            // The one-time media-type descriptors are consumed by AddStream/SetInputMediaType;
            // release these ComWrappers RCWs deterministically rather than waiting on the GC.
            ReleaseCom(outType);
            ReleaseCom(inType);
        }
    }

    // Test-only fault injection seam. Null in production. When set, the constructor invokes
    // this delegate inside the try block (after creating _tempPath but before any MF calls)
    // to simulate a late-constructor failure and verify the catch block cleans up the temp file.
    // Tests must clear this field in cleanup if the constructor does not consume it.
    internal static volatile Action? s_testFaultAfterTempCreate;

    internal static volatile Action<string, string>? s_testPublishAtomic;

    // Recording never replaces an existing file, so these are the only creation seams.
    internal static Func<string, int, int, int, uint, IVideoEncoder> s_createNoClobber =
        (path, width, height, fps, bitrate) => new Mp4SinkWriterEncoder(
            path, width, height, fps, bitrate, overwriteExisting: false);

    /// <summary>Creates an encoder whose MP4 also carries an AAC track; it implements <see cref="IAudioSampleWriter"/>.</summary>
    internal static Func<string, int, int, int, uint, PcmFormat, IVideoEncoder> s_createNoClobberWithAudio =
        (path, width, height, fps, bitrate, audio) => new Mp4SinkWriterEncoder(
            path, width, height, fps, bitrate, overwriteExisting: false, audioFormat: audio);

    /// <summary>Adds an AAC output stream fed with <paramref name="audio"/> PCM.</summary>
    /// <exception cref="RecordAudioUnavailableException">The AAC encoder is unavailable.</exception>
    private static uint AddAudioStream(IMFSinkWriter writer, PcmFormat audio)
    {
        IMFMediaType? outType = null;
        IMFMediaType? inType = null;
        try
        {
            PInvoke.MFCreateMediaType(out outType).ThrowOnFailure();
            outType.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
            outType.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFAudioFormat_AAC);
            outType.SetUINT32(PInvoke.MF_MT_AUDIO_BITS_PER_SAMPLE, (uint)audio.BitsPerSample);
            outType.SetUINT32(PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)audio.SampleRate);
            outType.SetUINT32(PInvoke.MF_MT_AUDIO_NUM_CHANNELS, (uint)audio.Channels);
            outType.SetUINT32(PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, AacBytesPerSecond);
            writer.AddStream(outType, out var index);

            PInvoke.MFCreateMediaType(out inType).ThrowOnFailure();
            inType.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
            inType.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFAudioFormat_PCM);
            inType.SetUINT32(PInvoke.MF_MT_AUDIO_BITS_PER_SAMPLE, (uint)audio.BitsPerSample);
            inType.SetUINT32(PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)audio.SampleRate);
            inType.SetUINT32(PInvoke.MF_MT_AUDIO_NUM_CHANNELS, (uint)audio.Channels);
            inType.SetUINT32(PInvoke.MF_MT_AUDIO_BLOCK_ALIGNMENT, (uint)audio.BlockAlign);
            inType.SetUINT32(PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, (uint)audio.BytesPerSecond);
            writer.SetInputMediaType(index, inType, null);
            return index;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            throw new RecordAudioUnavailableException(
                $"Can't record audio: the AAC audio encoder could not be initialized (HRESULT 0x{ex.HResult:X8}).",
                "On Windows N/KN editions, install the Media Feature Pack (Settings > System > Optional features), or record without --audio.",
                ex);
        }
        finally
        {
            ReleaseCom(outType);
            ReleaseCom(inType);
        }
    }

    /// <summary>Writes interleaved PCM to the audio track. Only valid when created with an audio format.</summary>
    /// <remarks>Called from the audio thread while the capture loop writes video.</remarks>
    public void WriteAudio(ReadOnlySpan<byte> pcm, long sampleTimeHns, long sampleDurationHns)
    {
        if (_audioFormat is null)
        {
            throw new InvalidOperationException("This recording has no audio track.");
        }
        if (pcm.IsEmpty)
        {
            return;
        }

        var length = (uint)pcm.Length;
        PInvoke.MFCreateMemoryBuffer(length, out var buffer).ThrowOnFailure();
        try
        {
            buffer.Lock(out var dest, out _, out _);
            try
            {
                pcm.CopyTo(new Span<byte>(dest, pcm.Length));
            }
            finally
            {
                buffer.Unlock();
            }
            buffer.SetCurrentLength(length);

            PInvoke.MFCreateSample(out var sample).ThrowOnFailure();
            try
            {
                sample.AddBuffer(buffer);
                sample.SetSampleTime(sampleTimeHns);
                sample.SetSampleDuration(sampleDurationHns);
                lock (_writeLock)
                {
                    ObjectDisposedException.ThrowIf(_disposed || _finalized, this);
                    _writer.WriteSample(_audioStreamIndex, sample);
                }
            }
            finally
            {
                ReleaseCom(sample);
            }
        }
        finally
        {
            ReleaseCom(buffer);
        }
    }

    /// <remarks>
    /// Coverage ceiling (issue #630): descriptor mapping is unit-tested for ordinary HRESULTs; the
    /// remaining line is the native Media Foundation codec-missing HRESULT arm, which only occurs on
    /// Windows N/KN or hosts without the H.264 encoder.
    /// </remarks>
    internal static bool TryDescribeEncoderInitFailure(Exception ex, out string message)
    {
        var hr = ex.HResult;
        if (hr is unchecked((int)0xC00D5212)  // MF_E_TOPO_CODEC_NOT_FOUND
            or unchecked((int)0xC00D36B4))    // MF_E_INVALIDMEDIATYPE
        {
            message = $"Could not initialize the H.264 video encoder (HRESULT 0x{hr:X8}). On Windows N/KN editions, install the Media Feature Pack (Settings > System > Optional features > Add a feature > 'Media Feature Pack'), then retry.";
            return true;
        }

        message = string.Empty;
        return false;
    }

    /// <summary>
    /// Writes a single top-down BGRA frame. <paramref name="bgra"/> must contain
    /// exactly Width*Height*4 bytes.
    /// </summary>
    /// <remarks>
    /// Coverage ceiling (issue #630): the validation and successful frame-write path are tested, but
    /// the per-frame Media Foundation buffer/sample COM error cleanup arms require faulting native MF
    /// objects after creation and cannot be triggered safely with managed fakes.
    /// </remarks>
    public void WriteFrame(ReadOnlySpan<byte> bgra, long sampleTimeHns, long sampleDurationHns)
    {
        if (bgra.Length < _frameBytes)
        {
            throw new ArgumentException($"Frame buffer is {bgra.Length} bytes; expected {_frameBytes}.", nameof(bgra));
        }

        PInvoke.MFCreateMemoryBuffer(_frameBytes, out var buffer).ThrowOnFailure();
        try
        {
            buffer.Lock(out var dest, out _, out _);
            try
            {
                bgra[..(int)_frameBytes].CopyTo(new Span<byte>(dest, (int)_frameBytes));
            }
            finally
            {
                buffer.Unlock();
            }
            buffer.SetCurrentLength(_frameBytes);

            PInvoke.MFCreateSample(out var sample).ThrowOnFailure();
            try
            {
                sample.AddBuffer(buffer);
                sample.SetSampleTime(sampleTimeHns);
                sample.SetSampleDuration(sampleDurationHns);
                lock (_writeLock)
                {
                    _writer.WriteSample(_streamIndex, sample);
                }
            }
            finally
            {
                // Release the per-frame sample so its native MF buffers don't accumulate
                // across thousands of frames.
                ReleaseCom(sample);
            }
        }
        finally
        {
            ReleaseCom(buffer);
        }
    }

    /// <summary>Finalizes the MP4 container and atomically moves the temp file to the final path. Safe to call once; a no-op afterwards.</summary>
    public void Complete()
    {
        lock (_writeLock)
        {
            if (_finalized)
            {
                return;
            }
            _writer.Finalize();
            _finalized = true;
        }

        // Atomically publish the final path now that we have a fully valid MP4.
        // The temp file is now owned by _path.
        // _fileMoved is set ONLY after the move succeeds so that Dispose() can still
        // clean up the temp if the move throws (e.g., destination path locked).
        if (_overwriteExisting)
        {
            PublishAtomicWithTestSeam(_tempPath, _path);
        }
        else
        {
            File.Move(_tempPath, _path, overwrite: false);
        }
        _fileMoved = true;
    }

    /// <remarks>
    /// Coverage ceiling (issue #630): production atomic publish is covered; the remaining branch is a
    /// test seam used only for injected file-move faults and is reset after each test.
    /// </remarks>
    private static void PublishAtomicWithTestSeam(string tempPath, string destPath)
    {
        if (s_testPublishAtomic is { } testPublish)
        {
            testPublish(tempPath, destPath);
            return;
        }

        PublishAtomic(tempPath, destPath);
    }

    internal static void PublishAtomic(string tempPath, string destPath) =>
        RecordingArtifactPublisher.PublishVideo(tempPath, destPath);

    private static ulong PackU64(uint high, uint low) => ((ulong)high << 32) | low;

    /// <summary>Deterministically releases a source-generated COM RCW (ComWrappers-based).</summary>
    /// <remarks>
    /// Coverage ceiling (issue #630): this releases source-generated Media Foundation COM wrappers.
    /// The null/non-disposable arms are defensive COM cleanup paths not produced by the real MF APIs.
    /// </remarks>
    private static void ReleaseCom(object? comObject)
    {
        if (comObject is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    /// <remarks>
    /// Coverage ceiling (issue #630): tests cover successful completion/disposal and constructor
    /// failure cleanup. Remaining lines are best-effort temp-file/MFShutdown exception arms that
    /// require native Media Foundation or filesystem fault injection after COM allocation.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        lock (_writeLock)
        {
            _disposed = true;
        }

        ReleaseCom(_writer);

        if (!_fileMoved)
        {
            // Encoding did not complete successfully (writer was never finalized, or the
            // temp→final move failed). Delete the temp file so nothing is orphaned.
            // A pre-existing file at _path must not be touched on failure.
            try
            {
                if (File.Exists(_tempPath))
                {
                    File.Delete(_tempPath);
                }
            }
            catch
            {
                // Best-effort cleanup of the partial temp file.
            }
        }
        if (_mfStarted)
        {
            try
            {
                PInvoke.MFShutdown();
            }
            catch
            {
                // Best-effort shutdown.
            }
            _mfStarted = false;
        }
    }
}

/// <summary>
/// The H.264 encoder could not be initialized. Usually means Media Foundation or the H.264 encoder
/// is unavailable — most often on a Windows N/KN edition without the Media Feature Pack, or on a
/// Server SKU without the Desktop Experience.
/// </summary>
/// <param name="message">Describes what failed.</param>
/// <param name="innerException">The underlying Media Foundation failure.</param>
public sealed class Mp4EncoderInitializationException(string message, Exception innerException)
    : InvalidOperationException(message, innerException)
{
}
