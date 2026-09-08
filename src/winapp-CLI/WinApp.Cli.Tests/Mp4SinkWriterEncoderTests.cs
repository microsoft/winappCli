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
            var path = Path.Join(dir, "one-frame.mp4");
            using var encoder = CreateEncoderOrInconclusive(path);
            Assert.AreEqual(64, encoder.Width);
            Assert.AreEqual(64, encoder.Height);

            var shortFrame = new byte[63 * 64 * 4];
            var ex = Assert.ThrowsExactly<ArgumentException>(
                () => encoder.WriteFrame(shortFrame, 0, 10_000_000));
            StringAssert.Contains(ex.Message, "expected 16384");

            encoder.WriteFrame(Enumerable.Repeat((byte)0x22, 64 * 64 * 4).ToArray(), 0, 10_000_000);
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
