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
            StringAssert.Contains(ex.Message, "expected 16384");

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
