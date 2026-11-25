// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording;

/// <summary>
/// Re-encodes a finished H.264 MP4 with every frame cut down by a <see cref="RecordTrimPlan"/>.
/// </summary>
/// <remarks>
/// Media Foundation decodes the recording to RGB32, each frame is cropped and scaled, and
/// <see cref="Mp4SinkWriterEncoder"/> writes the result. The output is an ordinary MP4 of the final size,
/// so every player and editor shows it the same way.
/// <para>
/// The reader, its samples and their buffers are released as soon as they're used, not when the garbage
/// collector gets to them: the reader keeps the recording open, and the decoder can stall when its decoded
/// frames are still referenced.
/// </para>
/// </remarks>
internal static unsafe partial class Mp4Trimmer
{
    private const uint MF_VERSION = 0x00020070;
    private const uint MFSTARTUP_FULL = 0;
    private const uint FirstVideoStream = 0xFFFFFFFC;
    private const uint EndOfStream = 0x2;

    // Vtable slots: IUnknown (3) + IMFAttributes (30) put IMFSample::ConvertToContiguousBuffer at 41;
    // Lock/Unlock (IMFMediaBuffer) and Lock2D/Unlock2D (IMF2DBuffer) are the first two after IUnknown.
    private const int ConvertToContiguousBufferSlot = 41;
    private const int LockSlot = 3;
    private const int UnlockSlot = 4;
    private static readonly Guid IidImf2DBuffer = new("7DC9D5F9-9ED9-44EC-9BBF-0600BB589FBB");
    private static readonly StrategyBasedComWrappers s_comWrappers = new();

    public static void Trim(string sourcePath, string destinationPath, RecordTrimPlan plan, int fps)
    {
        using var encoder = new Mp4SinkWriterEncoder(
            destinationPath, plan.OutputWidth, plan.OutputHeight, fps,
            UiRecordingService.VideoBitrate(plan.OutputWidth, plan.OutputHeight, fps), overwriteExisting: false);
        var frameDuration = 10_000_000L / fps;
        foreach (var (pixels, time) in ReadFrames(sourcePath, plan.SourceWidth, plan.SourceHeight))
        {
            encoder.WriteFrame(plan.Apply(pixels), time, frameDuration);
        }
        encoder.Complete();
    }

    /// <summary>
    /// Decodes each frame of an MP4 as top-down BGRA of the given size, with its presentation time.
    /// </summary>
    internal static IEnumerable<(byte[] Pixels, long Time)> ReadFrames(string path, int width, int height)
    {
        PInvoke.MFStartup(MF_VERSION, MFSTARTUP_FULL).ThrowOnFailure();
        IMFSourceReader? reader = null;
        try
        {
            reader = OpenReader(path);
            var (decodedHeight, defaultStride) = RequestRgb32(reader, width, height);

            while (true)
            {
                var frame = ReadOne(reader, width, height, decodedHeight, defaultStride, out var endOfStream);
                if (frame is { } decoded)
                {
                    yield return decoded;
                }
                if (endOfStream)
                {
                    yield break;
                }
            }
        }
        finally
        {
            ((object?)reader as ComObject)?.FinalRelease();
            PInvoke.MFShutdown();
        }
    }

    private static IMFSourceReader OpenReader(string path)
    {
        PInvoke.MFCreateAttributes(out var attributes, 2).ThrowOnFailure();
        // Hardware decoding with GPU color conversion is several times faster than the software path.
        attributes.SetUINT32(PInvoke.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, 1);
        attributes.SetUINT32(PInvoke.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
        var rawAttributes = ComInterfaceMarshaller<IMFAttributes>.ConvertToUnmanaged(attributes);
        void* rawReader = null;
        try
        {
            Marshal.ThrowExceptionForHR(CreateSourceReader(path, rawAttributes, &rawReader));
        }
        finally
        {
            ComInterfaceMarshaller<IMFAttributes>.Free(rawAttributes);
        }
        // A unique wrapper, so FinalRelease closes the recording right away.
        var reader = (IMFSourceReader)s_comWrappers.GetOrCreateObjectForComInstance((nint)rawReader, CreateObjectFlags.UniqueInstance);
        Marshal.Release((nint)rawReader);
        return reader;
    }

    [LibraryImport("mfreadwrite.dll", EntryPoint = "MFCreateSourceReaderFromURL", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CreateSourceReader(string url, void* attributes, void** reader);
    private static (int DecodedHeight, int DefaultStride) RequestRgb32(IMFSourceReader reader, int width, int height)
    {
        PInvoke.MFCreateMediaType(out var rgb).ThrowOnFailure();
        try
        {
            rgb.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            rgb.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_RGB32);
            reader.SetCurrentMediaType(FirstVideoStream, null, rgb);
        }
        finally
        {
            Release(rgb);
        }

        reader.GetCurrentMediaType(FirstVideoStream, out var current);
        int decodedWidth, decodedHeight, defaultStride;
        try
        {
            current.GetUINT64(PInvoke.MF_MT_FRAME_SIZE, out var size);
            decodedWidth = (int)(size >> 32);
            decodedHeight = (int)(size & 0xFFFFFFFF);
            try
            {
                current.GetUINT32(PInvoke.MF_MT_DEFAULT_STRIDE, out var stride);
                defaultStride = (int)stride;
            }
            catch (Exception ex) when (ex.HResult == unchecked((int)0xC00D36E6)) // MF_E_ATTRIBUTENOTFOUND
            {
                defaultStride = decodedWidth * 4;
            }
        }
        finally
        {
            Release(current);
        }
        if (decodedWidth < width || decodedHeight < height)
        {
            throw new InvalidOperationException(
                $"The recording decoded as {decodedWidth}x{decodedHeight}, smaller than the expected {width}x{height}.");
        }
        return (decodedHeight, defaultStride);
    }


    private static (byte[] Pixels, long Time)? ReadOne(
        IMFSourceReader reader, int width, int height, int decodedHeight, int defaultStride, out bool endOfStream)
    {
        uint flags;
        long time;
        IMFSample_unmanaged* sample = null;
        reader.ReadSample(FirstVideoStream, 0, null, &flags, &time, &sample);
        endOfStream = (flags & EndOfStream) != 0;
        if (sample is null)
        {
            return null;
        }
        void* buffer = null;
        try
        {
            Marshal.ThrowExceptionForHR(VtableCall(sample, ConvertToContiguousBufferSlot, &buffer));
            var pixels = new byte[checked(width * height * 4)];
            var rowBytes = width * 4;
            if (Marshal.QueryInterface((nint)buffer, in IidImf2DBuffer, out var twoD) >= 0)
            {
                try
                {
                    byte* scan0;
                    int pitch;
                    Marshal.ThrowExceptionForHR(VtableCall((void*)twoD, LockSlot, &scan0, &pitch));
                    try
                    {
                        CopyRows(scan0, pitch, pixels, rowBytes, height);
                    }
                    finally
                    {
                        VtableCall((void*)twoD, UnlockSlot);
                    }
                }
                finally
                {
                    Marshal.Release(twoD);
                }
            }
            else
            {
                byte* data;
                Marshal.ThrowExceptionForHR(VtableCall(buffer, LockSlot, &data, null, null));
                try
                {
                    // A negative stride means the image is stored bottom-up.
                    var start = defaultStride < 0 ? data + (long)(decodedHeight - 1) * -defaultStride : data;
                    CopyRows(start, defaultStride, pixels, rowBytes, height);
                }
                finally
                {
                    VtableCall(buffer, UnlockSlot);
                }
            }
            return (pixels, time);
        }
        finally
        {
            if (buffer is not null)
            {
                Marshal.Release((nint)buffer);
            }
            Marshal.Release((nint)sample);
        }
    }

    private static int VtableCall(void* com, int slot)
        => ((delegate* unmanaged[MemberFunction]<void*, int>)(*(void***)com)[slot])(com);

    private static int VtableCall(void* com, int slot, void** result)
        => ((delegate* unmanaged[MemberFunction]<void*, void**, int>)(*(void***)com)[slot])(com, result);

    private static int VtableCall(void* com, int slot, byte** scan0, int* pitch)
        => ((delegate* unmanaged[MemberFunction]<void*, byte**, int*, int>)(*(void***)com)[slot])(com, scan0, pitch);

    private static int VtableCall(void* com, int slot, byte** data, uint* maxLength, uint* currentLength)
        => ((delegate* unmanaged[MemberFunction]<void*, byte**, uint*, uint*, int>)(*(void***)com)[slot])(com, data, maxLength, currentLength);

    private static void CopyRows(byte* scan0, int pitch, byte[] destination, int rowBytes, int rows)
    {
        for (var y = 0; y < rows; y++)
        {
            new ReadOnlySpan<byte>(scan0 + (long)y * pitch, rowBytes).CopyTo(destination.AsSpan(y * rowBytes, rowBytes));
        }
    }

    private static void Release(object? comObject)
    {
        if (comObject is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}