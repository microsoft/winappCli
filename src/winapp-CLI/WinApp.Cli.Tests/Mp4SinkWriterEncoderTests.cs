// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.


namespace WinApp.Cli.Tests;

/// <summary>
/// The Media Foundation MP4 encoder behind `ui record`. It is internal to the recording package, so
/// these tests live here, where the assembly grants InternalsVisibleTo.
/// </summary>
[TestClass]
public class Mp4SinkWriterEncoderTests
{
    // H.264 encoder MFTs hold a lookahead of input frames (12 with the Microsoft software encoder
    // in local measurements), so a single frame reaches Finalize() with nothing delivered to the MP4
    // sink and depends entirely on the end-of-stream drain. On some hosts that drain yields no
    // sample and Finalize() fails with MF_E_SINK_NO_SAMPLES_PROCESSED (issue #834). Writing well
    // past the lookahead makes the encoder emit samples during normal input processing, which
    // Finalize() delivers to the sink before closing the file.
    private const int FramesPastEncoderLookahead = 30;

    /// <summary>
    /// Writes enough 1-second frames to a real encoder (created with fps 1) that
    /// <see cref="Mp4SinkWriterEncoder.Complete"/> does not depend on the encoder's end-of-stream drain.
    /// </summary>
    internal static void WritePastEncoderLookahead(Mp4SinkWriterEncoder encoder, byte[] frame)
    {
        for (var i = 0; i < FramesPastEncoderLookahead; i++)
        {
            encoder.WriteFrame(frame, i * 10_000_000L, 10_000_000);
        }
    }

    private static string CreateScratchDirectory()
    {
        var dir = Path.Join(Path.GetTempPath(), "winapp-mp4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [TestMethod]
    public void Mp4Trimmer_JoinsWindowAndPaddedRecordingsIntoOneSize()
    {
        // The window recording (64x64 red) covers the frames before a menu opened; the padded recording
        // (128x96: the red window at (32, 16), a blue menu above it) covers the rest from frame 10 on. The joined
        // video is cut to the window plus the menu, 64x80, and the early frames show the background where the
        // menu will be.
        var dir = CreateScratchDirectory();
        try
        {
            var windowVideo = Path.Join(dir, "window.mp4");
            var paddedVideo = Path.Join(dir, "padded.mp4");
            using (var encoder = new Mp4SinkWriterEncoder(windowVideo, 64, 64, 1, 4_000_000))
            {
                WritePastEncoderLookahead(encoder, Fill(64, 64, (0, 0, 64, 64, Red)));
                encoder.Complete();
            }
            using (var encoder = new Mp4SinkWriterEncoder(paddedVideo, 128, 96, 1, 8_000_000))
            {
                WritePastEncoderLookahead(encoder, Fill(128, 96, (32, 16, 96, 80, Red), (32, 0, 96, 16, Blue)));
                encoder.Complete();
            }

            var trimmed = Path.Join(dir, "trimmed.mp4");
            var sources = new RecordTrimSources(paddedVideo, PaddedStartFrame: 10,
                UiRecordingService.PlanWindowCrop(new(32, 16, 96, 80), 128, 96), Background: 0x00FF00) { WindowVideo = windowVideo };
            Mp4Trimmer.Trim(sources, trimmed, new RecordTrimPlan(128, 96, 32, 0, 64, 80, 64, 80, 64, 80), fps: 1);

            var frames = Mp4Trimmer.ReadFrames(trimmed, 64, 80).ToList();
            Assert.HasCount(10 + FramesPastEncoderLookahead, frames);
            AssertColor(frames[0].Pixels, 32, 4, Green, "background above the window, before the menu opened");
            AssertColor(frames[0].Pixels, 32, 40, Red, "window, before the menu opened");
            AssertColor(frames[^1].Pixels, 32, 4, Blue, "menu");
            AssertColor(frames[^1].Pixels, 32, 40, Red, "window");
            // Both recordings must be closed once joining finishes, so the recorder can replace them.
            File.Delete(windowVideo);
            File.Delete(paddedVideo);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        static void AssertColor(byte[] pixels, int x, int y, (byte B, byte G, byte R) expected, string what)
        {
            var i = ((y * 64) + x) * 4;
            Assert.IsLessThan(60, Math.Abs(pixels[i] - expected.B), $"{what}: blue");
            Assert.IsLessThan(60, Math.Abs(pixels[i + 1] - expected.G), $"{what}: green");
            Assert.IsLessThan(60, Math.Abs(pixels[i + 2] - expected.R), $"{what}: red");
        }
    }

    private static readonly (byte B, byte G, byte R) Red = (0, 0, 255);
    private static readonly (byte B, byte G, byte R) Green = (0, 255, 0);
    private static readonly (byte B, byte G, byte R) Blue = (255, 0, 0);

    private static byte[] Fill(int width, int height, params (int Left, int Top, int Right, int Bottom, (byte B, byte G, byte R) Color)[] areas)
    {
        var pixels = new byte[width * height * 4];
        foreach (var (left, top, right, bottom, (b, g, r)) in areas)
        {
            for (var y = top; y < bottom; y++)
            {
                for (var x = left; x < right; x++)
                {
                    var i = ((y * width) + x) * 4;
                    (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (b, g, r, 255);
                }
            }
        }
        return pixels;
    }
    [TestMethod]
    public void Mp4SinkWriterEncoder_PublishAtomic_MovesNewDestination()
    {
        var dir = CreateScratchDirectory();
        try
        {
            var temp = Path.Join(dir, "temp.mp4");
            var dest = Path.Join(dir, "dest.mp4");
            File.WriteAllText(temp, "new");

            Mp4SinkWriterEncoder.PublishAtomic(temp, dest);

            Assert.IsFalse(File.Exists(temp));
            Assert.AreEqual("new", File.ReadAllText(dest));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void Mp4SinkWriterEncoder_TryDescribeEncoderInitFailure_ReturnsFalseForUnrelatedError()
    {
        Assert.IsFalse(Mp4SinkWriterEncoder.TryDescribeEncoderInitFailure(
            new InvalidOperationException("not media foundation"), out var message));
        Assert.AreEqual(string.Empty, message);
    }

    [TestMethod]
    public void Mp4SinkWriterEncoder_RealEncoderCoversValidationAndSuccessfulComplete()
    {
        var dir = CreateScratchDirectory();
        try
        {
            var path = Path.Join(dir, "encoded.mp4");
            using var encoder = CreateEncoderOrInconclusive(path);
            Assert.AreEqual(64, encoder.Width);
            Assert.AreEqual(64, encoder.Height);

            var shortFrame = new byte[63 * 64 * 4];
            var ex = Assert.ThrowsExactly<ArgumentException>(
                () => encoder.WriteFrame(shortFrame, 0, 10_000_000));
            Assert.Contains("expected 16384", ex.Message);

            WritePastEncoderLookahead(encoder, Enumerable.Repeat((byte)0x22, 64 * 64 * 4).ToArray());
            encoder.Complete();
            encoder.Complete();

            Assert.IsTrue(File.Exists(path));
            Assert.IsTrue(new FileInfo(path).Length > 0, "completed MP4 must be published to the final path");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static Mp4SinkWriterEncoder CreateEncoderOrInconclusive(string path)
    {
        try
        {
            return new Mp4SinkWriterEncoder(path, 64, 64, 1, 1_000_000);
        }
        catch (Mp4EncoderInitializationException ex)
        {
            Assert.Inconclusive($"Media Foundation H.264 encoder unavailable on this host: {ex.Message}");
            throw;
        }
    }
}
