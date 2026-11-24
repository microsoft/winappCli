// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording;

/// <summary>A source of loopback PCM: what Windows is playing, as 48 kHz 16-bit stereo.</summary>
internal interface ILoopbackAudioSource : IDisposable
{
    /// <summary>Starts the stream. Packets played before this call are not delivered.</summary>
    void Start();

    /// <summary>
    /// Hands every packet captured since the last call to <paramref name="onPacket"/>, oldest first.
    /// </summary>
    /// <param name="onPacket">
    /// Receives the interleaved samples, the frame count, the packet's capture time on the
    /// <see cref="System.Diagnostics.Stopwatch"/> clock in 100 ns units (0 when the device gave no
    /// usable time), and whether the packet is silence (its bytes must then be ignored).
    /// </param>
    void Drain(LoopbackPacketHandler onPacket);
}

/// <summary>Receives one loopback packet. <paramref name="pcm"/> is only valid during the call.</summary>
internal delegate void LoopbackPacketHandler(ReadOnlySpan<byte> pcm, int frames, long qpcHns, bool silent);

/// <summary>
/// Captures what Windows is playing through WASAPI loopback: either the default playback device
/// (<see cref="RecordAudioMode.System"/>) or one process tree (<see cref="RecordAudioMode.App"/>).
/// </summary>
/// <remarks>
/// Both modes ask WASAPI to convert to <see cref="AudioFormat"/>, so the encoder sees one format no
/// matter what the device mixes at.
/// </remarks>
internal sealed unsafe partial class LoopbackAudioCapture : ILoopbackAudioSource
{
    /// <summary>The PCM format every capture is converted to.</summary>
    internal static readonly PcmFormat AudioFormat = new(48_000, 2, 16);

    // E_NOTFOUND: the machine has no playback device (or none is enabled).
    private const int ENotFound = unchecked((int)0x80070490);

    // AUDCLNT_E_DEVICE_INVALIDATED: the device was removed while starting.
    private const int AudclntEDeviceInvalidated = unchecked((int)0x88890004);

    // 1 second of buffer, so a capture thread that stalls briefly loses nothing.
    private const long BufferDurationHns = 10_000_000;

    private readonly IAudioClient _client;
    private readonly IAudioCaptureClient _capture;
    private bool _started;
    private bool _disposed;

    private LoopbackAudioCapture(IAudioClient client, IAudioCaptureClient capture)
    {
        _client = client;
        _capture = capture;
    }

    /// <summary>Opens a loopback stream, or explains why it can't.</summary>
    /// <exception cref="RecordAudioUnavailableException">Loopback audio can't be captured here.</exception>
    internal static LoopbackAudioCapture Open(RecordAudioMode mode, int processId) => mode switch
    {
        RecordAudioMode.System => OpenSystem(),
        RecordAudioMode.App => OpenProcess(processId),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static LoopbackAudioCapture OpenSystem()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? client = null;
        try
        {
            enumerator = MMDeviceEnumerator.CreateInstance<IMMDeviceEnumerator>();
            try
            {
                enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out device);
            }
            catch (Exception ex) when (ex.HResult == ENotFound)
            {
                throw new RecordAudioUnavailableException(
                    "Can't record system audio: this machine has no playback device.",
                    "Enable a playback device (Settings > System > Sound), or record without --audio.",
                    ex);
            }

            var iid = typeof(IAudioClient).GUID;
            device.Activate(&iid, CLSCTX.CLSCTX_ALL, null, out var activated);
            client = (IAudioClient)activated;
            return Initialize(client, "system audio");
        }
        catch (Exception ex) when (ex is not RecordAudioUnavailableException)
        {
            ReleaseCom(client);
            throw Unavailable("system audio", ex);
        }
        finally
        {
            ReleaseCom(device);
            ReleaseCom(enumerator);
        }
    }

    private static LoopbackAudioCapture OpenProcess(int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);

        // Process loopback is a virtual device activated asynchronously, with the target process
        // passed in a blob PROPVARIANT.
        var activation = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = AUDIOCLIENT_ACTIVATION_TYPE.AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
        };
        activation.ProcessLoopbackParams.TargetProcessId = (uint)processId;
        activation.ProcessLoopbackParams.ProcessLoopbackMode =
            PROCESS_LOOPBACK_MODE.PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE;
        var blob = new BlobPropVariant
        {
            Vt = 65, // VT_BLOB
            Size = (uint)sizeof(AUDIOCLIENT_ACTIVATION_PARAMS),
            Data = &activation,
        };

        IAudioClient? client = null;
        IActivateAudioInterfaceAsyncOperation? operation = null;
        var handler = new ActivationCompletion();
        try
        {
            var iid = typeof(IAudioClient).GUID;
            HRESULT hr;
            try
            {
                hr = ActivateAudioInterfaceAsync(
                    PInvoke.VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, &iid, &blob, handler, out operation);
            }
            catch (EntryPointNotFoundException ex)
            {
                throw ProcessLoopbackUnsupported(ex);
            }
            Marshal.ThrowExceptionForHR(hr);
            if (!handler.Completed.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Windows did not finish opening the app's audio stream within 10 seconds.");
            }

            HRESULT activateResult;
            operation!.GetActivateResult(&activateResult, out var activated);
            if (activateResult.Failed)
            {
                // Older Windows builds reject the process-loopback activation type outright.
                if (activateResult.Value is unchecked((int)0x80070057) or unchecked((int)0x80004001))
                {
                    throw ProcessLoopbackUnsupported(Marshal.GetExceptionForHR(activateResult.Value)!);
                }
                Marshal.ThrowExceptionForHR(activateResult.Value);
            }
            client = (IAudioClient)activated;
            return Initialize(client, "the app's audio");
        }
        catch (Exception ex) when (ex is not RecordAudioUnavailableException)
        {
            ReleaseCom(client);
            throw Unavailable("the app's audio", ex);
        }
        finally
        {
            ReleaseCom(operation);
            handler.Completed.Dispose();
        }
    }

    private static LoopbackAudioCapture Initialize(IAudioClient client, string what)
    {
        var format = new WAVEFORMATEX
        {
            wFormatTag = (ushort)PInvoke.WAVE_FORMAT_PCM,
            nChannels = (ushort)AudioFormat.Channels,
            nSamplesPerSec = (uint)AudioFormat.SampleRate,
            wBitsPerSample = (ushort)AudioFormat.BitsPerSample,
            nBlockAlign = (ushort)AudioFormat.BlockAlign,
            nAvgBytesPerSec = (uint)AudioFormat.BytesPerSecond,
            cbSize = 0,
        };

        client.Initialize(
            AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED,
            PInvoke.AUDCLNT_STREAMFLAGS_LOOPBACK |
                PInvoke.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
                PInvoke.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
            BufferDurationHns,
            0,
            &format,
            null);

        var captureIid = typeof(IAudioCaptureClient).GUID;
        client.GetService(&captureIid, out var capturePointer);
        try
        {
            var capture = (IAudioCaptureClient)ComInterfaceMarshaller<IAudioCaptureClient>.ConvertToManaged(capturePointer)!;
            return new LoopbackAudioCapture(client, capture);
        }
        finally
        {
            Marshal.Release((nint)capturePointer);
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            _client.Start();
        }
        catch (COMException ex)
        {
            // The device can go away between opening the stream and starting it.
            throw Unavailable("audio", ex);
        }
        _started = true;
    }

    /// <summary>
    /// For an ApplicationFrameHost frame, the process that owns the app content (its CoreWindow) inside
    /// it, or 0 when there is none; null when <paramref name="hwnd"/> is not such a frame.
    /// </summary>
    internal static int? FindFrameContentProcess(nint hwnd)
    {
        if (hwnd == 0)
        {
            return null;
        }

        var window = new HWND(hwnd);
        Span<char> className = stackalloc char[64];
        int length;
        fixed (char* buffer = className)
        {
            length = PInvoke.GetClassName(window, buffer, className.Length);
        }
        if (!className[..Math.Max(0, length)].SequenceEqual("ApplicationFrameWindow"))
        {
            return null;
        }

        var content = PInvoke.FindWindowEx(window, HWND.Null, "Windows.UI.Core.CoreWindow", null);
        if (content.IsNull)
        {
            return 0;
        }
        uint processId;
        _ = PInvoke.GetWindowThreadProcessId(content, &processId);
        return (int)processId;
    }

    public void Drain(LoopbackPacketHandler onPacket)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(onPacket);

        while (true)
        {
            _capture.GetNextPacketSize(out var next);
            if (next == 0)
            {
                return;
            }

            byte* data;
            ulong qpc;
            _capture.GetBuffer(&data, out var frames, out var flags, null, &qpc);
            try
            {
                var silent = (flags & (uint)_AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                var timestampBad = (flags & (uint)_AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) != 0;
                var bytes = checked((int)frames * AudioFormat.BlockAlign);
                onPacket(
                    silent ? default : new ReadOnlySpan<byte>(data, bytes),
                    (int)frames,
                    timestampBad ? 0 : (long)qpc,
                    silent);
            }
            finally
            {
                _capture.ReleaseBuffer(frames);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_started)
        {
            try
            {
                _client.Stop();
            }
            catch (COMException)
            {
                // The device may already be gone; the stream is being discarded either way.
            }
        }
        ReleaseCom(_capture);
        ReleaseCom(_client);
    }

    private static RecordAudioUnavailableException Unavailable(string what, Exception ex) =>
        ex.HResult == AudclntEDeviceInvalidated
            ? new RecordAudioUnavailableException(
                $"Can't record {what}: the playback device was removed or changed while recording started.",
                "Retry once the playback device is stable, or record without --audio.",
                ex)
            : new RecordAudioUnavailableException(
                $"Can't record {what}: {ex.Message} (HRESULT 0x{ex.HResult:X8}).",
                "Check that a playback device is enabled, or record without --audio.",
                ex);

    private static RecordAudioUnavailableException ProcessLoopbackUnsupported(Exception ex) => new(
        "Can't record only the app's audio: this version of Windows doesn't support capturing one app's sound (it needs Windows 10 build 20348 or Windows 11).",
        "Use --audio system to record everything the machine plays, or record without --audio.",
        ex);

    private static void ReleaseCom(object? comObject)
    {
        if (comObject is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    [LibraryImport("Mmdevapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial HRESULT ActivateAudioInterfaceAsync(
        string deviceInterfacePath,
        Guid* riid,
        BlobPropVariant* activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    /// <summary>A PROPVARIANT holding a VT_BLOB, laid out exactly as Windows expects.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BlobPropVariant
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public uint Size;
        public void* Data;
    }

    /// <summary>
    /// Signals when Windows has opened the process-loopback stream. Windows calls it from a worker
    /// thread and refuses handlers that aren't agile, hence <see cref="IAgileObject"/>.
    /// </summary>
    [GeneratedComClass]
    private sealed partial class ActivationCompletion : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public ManualResetEventSlim Completed { get; } = new();

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation) => Completed.Set();
    }
}

/// <summary>An uncompressed PCM format.</summary>
/// <param name="SampleRate">Frames per second.</param>
/// <param name="Channels">Interleaved channels per frame.</param>
/// <param name="BitsPerSample">Bits per channel sample.</param>
internal readonly record struct PcmFormat(int SampleRate, int Channels, int BitsPerSample)
{
    public int BlockAlign => Channels * BitsPerSample / 8;

    public int BytesPerSecond => SampleRate * BlockAlign;
}

/// <summary>Audio was requested but can't be recorded on this machine.</summary>
/// <param name="message">What can't be recorded and why.</param>
/// <param name="recoveryHint">What the user can do about it.</param>
/// <param name="innerException">The underlying failure.</param>
public sealed class RecordAudioUnavailableException(string message, string recoveryHint, Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    /// <summary>What the user can do about it.</summary>
    public string RecoveryHint { get; } = recoveryHint;
}
