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
    public void Mp4Trimmer_KeepsOnlyThePlannedAreaAtItsOriginalPosition()
    {
        // A 128x96 recording: a red 64x64 "window" at (32, 16) on black. Trimming to the window must give a
        // 64x64 video that is all red, upright and unscaled.
        var dir = CreateScratchDirectory();
        try
        {
            var source = Path.Join(dir, "padded.mp4");
            var frame = new byte[128 * 96 * 4];
            for (var y = 16; y < 80; y++)
            {
                for (var x = 32; x < 96; x++)
                {
                    frame[((y * 128) + x) * 4 + 2] = 255;
                    frame[((y * 128) + x) * 4 + 3] = 255;
                }
            }
            using (var encoder = new Mp4SinkWriterEncoder(source, 128, 96, 1, 4_000_000))
            {
                WritePastEncoderLookahead(encoder, frame);
                encoder.Complete();
            }

            var trimmed = Path.Join(dir, "trimmed.mp4");
            Mp4Trimmer.Trim(source, trimmed, new RecordTrimPlan(128, 96, 32, 16, 64, 64, 64, 64, 64, 64), fps: 1);

            var frames = Mp4Trimmer.ReadFrames(trimmed, 64, 64).ToList();
            Assert.HasCount(FramesPastEncoderLookahead, frames);
            var pixels = frames[^1].Pixels;
            foreach (var (x, y) in new[] { (2, 2), (61, 2), (2, 61), (61, 61), (32, 32) })
            {
                var i = ((y * 64) + x) * 4;
                Assert.IsGreaterThan(200, pixels[i + 2], $"red at ({x}, {y})");
                Assert.IsLessThan(50, pixels[i + 1], $"no green at ({x}, {y})");
                Assert.IsLessThan(50, pixels[i], $"no blue at ({x}, {y})");
            }
            // The source file must be closed once trimming finishes, so the recorder can replace it.
            File.Delete(source);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
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
