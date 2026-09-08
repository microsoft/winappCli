// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SkiaSharp;

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording;

internal interface IRecordFrameSink : IAsyncDisposable
{
    ValueTask WriteAsync(ReadOnlyMemory<byte> bgra, RecordFrameSample sample, CancellationToken cancellationToken);
    Task<RecordFrameArtifactResult> CompleteAsync(RecordFrameCompletion completion);
    Task AbortAsync();
}

internal sealed class RecordFrameBundleConfiguration
{
    public const long DefaultMaximumBundleBytes = 1024L * 1024 * 1024;

    public required string FinalDirectory { get; init; }
    public required string VideoPath { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required RecordFrameRequestManifest Requested { get; init; }
    public required ILogger Logger { get; init; }
    public long MaximumBundleBytes { get; init; } = DefaultMaximumBundleBytes;
}

internal sealed class RecordFrameCompletion
{
    public required string Status { get; init; }
    public required string StopReason { get; init; }
    public required long ElapsedMs { get; init; }
    public required double AchievedFps { get; init; }
    public required double CadenceRatio { get; init; }
    public required string VideoStatus { get; init; }
    public required int VideoFrameCount { get; init; }
    public long? VideoFileSize { get; init; }
    public string? PublicationDirectory { get; init; }
}

internal sealed class RecordFrameBundleWriter : IRecordFrameSink
{
    internal const int JpegQuality = 85;
    private const long ManifestByteReserve = 1024L * 1024;
    private const int QueueCapacity = 1;

    internal static Func<RecordFrameBundleConfiguration, IRecordFrameSink> s_create =
        configuration => new RecordFrameBundleWriter(configuration);
    internal static Func<byte[], int, int, byte[]> s_encodeJpeg = EncodeJpeg;

    internal static void ResetTestSeams()
    {
        s_create = configuration => new RecordFrameBundleWriter(configuration);
        s_encodeJpeg = EncodeJpeg;
    }

    private readonly RecordFrameBundleConfiguration _configuration;
    private readonly string _stagingDirectory;
    private readonly string _framesDirectory;
    private readonly string _indexPath;
    private readonly Channel<QueuedFrame> _channel;
    private readonly StreamWriter _indexWriter;
    private readonly Task _worker;
    private readonly long _maximumBundleBytes;
    private readonly long _dataByteLimit;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private int _sampleCount;
    private int _imageCount;
    private long _imageBytes;
    private long _indexBytes;
    private long _lastIndexedElapsedMs;
    private int _truncated;
    private bool _indexDisposed;
    private bool _published;
    private bool _aborted;

    public int SampleCount => Volatile.Read(ref _sampleCount);
    public int ImageCount => Volatile.Read(ref _imageCount);
    public bool IsTruncated => Volatile.Read(ref _truncated) != 0;

    internal RecordFrameBundleWriter(RecordFrameBundleConfiguration configuration)
    {
        _configuration = configuration;
        if (configuration.MaximumBundleBytes <= ManifestByteReserve)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                $"Frame artifact byte limit must exceed the {ManifestByteReserve / 1024 / 1024} MiB manifest reserve.");
        }
        _maximumBundleBytes = configuration.MaximumBundleBytes;
        _dataByteLimit = configuration.MaximumBundleBytes - ManifestByteReserve;
        if (Path.Exists(configuration.FinalDirectory))
        {
            throw new IOException($"Frame artifact directory already exists: {configuration.FinalDirectory}");
        }

        var parent = Path.GetDirectoryName(configuration.FinalDirectory);
        if (string.IsNullOrEmpty(parent))
        {
            throw new IOException("Frame artifact directory must have a parent directory.");
        }

        Directory.CreateDirectory(parent);
        var leaf = Path.GetFileName(configuration.FinalDirectory);
        if (string.IsNullOrEmpty(leaf) || Path.IsPathRooted(leaf))
        {
            throw new IOException("Frame artifact directory must end with a valid directory name.");
        }
        var stagingName = $".{leaf}.{Guid.NewGuid():N}.staging";
        _stagingDirectory = Path.Join(parent, stagingName);
        _framesDirectory = Path.Join(_stagingDirectory, "frames");
        _indexPath = Path.Join(_stagingDirectory, "frames.ndjson");

        try
        {
            Directory.CreateDirectory(_framesDirectory);
            _indexWriter = new StreamWriter(
                new FileStream(_indexPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _indexWriter.NewLine = "\n";

            _channel = Channel.CreateBounded<QueuedFrame>(new BoundedChannelOptions(QueueCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
            _worker = Task.Run(ProcessFramesAsync);
        }
        catch
        {
            DeleteStagingDirectory();
            throw;
        }
    }

    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> bgra,
        RecordFrameSample sample,
        CancellationToken cancellationToken)
    {
        var expectedBytes = checked(_configuration.Width * _configuration.Height * 4);
        if (bgra.Length != expectedBytes)
        {
            throw new ArgumentException(
                $"Frame buffer is {bgra.Length} bytes; expected {expectedBytes}.",
                nameof(bgra));
        }
        if (IsTruncated)
        {
            return;
        }

        try
        {
            while (await _channel.Writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false))
            {
                // Clone only after capacity is available to keep buffering bounded.
                var queuedFrame = new QueuedFrame(bgra.ToArray(), sample);
                if (_channel.Writer.TryWrite(queuedFrame))
                {
                    return;
                }
            }

            await _worker.ConfigureAwait(false);
            throw new ChannelClosedException();
        }
        catch (ChannelClosedException)
        {
            await _worker.ConfigureAwait(false);
            throw;
        }
    }

    public async Task<RecordFrameArtifactResult> CompleteAsync(RecordFrameCompletion completion)
    {
        if (_published)
        {
            throw new InvalidOperationException("Frame artifact bundle has already been published.");
        }
        if (_aborted)
        {
            throw new InvalidOperationException("Frame artifact bundle has already been aborted.");
        }

        _channel.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
        await DisposeIndexWriterAsync().ConfigureAwait(false);

        var indexBytes = new FileInfo(_indexPath).Length;
        var timingElapsedMs = IsTruncated
            ? Math.Max(1, Volatile.Read(ref _lastIndexedElapsedMs))
            : completion.ElapsedMs;
        var timingAchievedFps = IsTruncated
            ? SampleCount * 1000.0 / timingElapsedMs
            : completion.AchievedFps;
        var timingCadenceRatio = IsTruncated
            ? timingAchievedFps / _configuration.Requested.Fps
            : completion.CadenceRatio;
        var manifest = new RecordFrameBundleManifest
        {
            Status = completion.Status == "complete" && IsTruncated
                ? "truncated"
                : completion.Status,
            StartedUtc = _configuration.StartedUtc,
            CompletedUtc = DateTimeOffset.UtcNow,
            StopReason = completion.StopReason,
            Requested = _configuration.Requested,
            Timing = new RecordFrameTimingManifest
            {
                ElapsedMs = timingElapsedMs,
                SampleCount = SampleCount,
                ImageCount = ImageCount,
                RepeatedSampleCount = SampleCount - ImageCount,
                AchievedFps = timingAchievedFps,
                CadenceRatio = timingCadenceRatio,
            },
            Video = new RecordFrameVideoManifest
            {
                Path = _configuration.VideoPath,
                Status = completion.VideoStatus,
                FrameCount = completion.VideoFrameCount,
                FileSize = completion.VideoFileSize,
            },
            Frames = new RecordFrameImagesManifest
            {
                Width = _configuration.Width,
                Height = _configuration.Height,
                Truncated = IsTruncated,
                ByteLimit = _maximumBundleBytes,
            },
        };

        var manifestPath = Path.Join(_stagingDirectory, "manifest.json");
        var manifestJson = JsonSerializer.Serialize(
            manifest,
            RecordingJsonContext.Default.RecordFrameBundleManifest);
        var manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
        if (_imageBytes + indexBytes + manifestBytes.Length > _maximumBundleBytes)
        {
            throw new InvalidOperationException(
                "Frame artifact manifest exceeded its reserved space within the bundle byte limit.");
        }
        await using (var manifestStream = new FileStream(
            manifestPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4_096,
            useAsync: true))
        {
            await manifestStream.WriteAsync(
                manifestBytes,
                _lifetimeCts.Token).ConfigureAwait(false);
        }

        var publicationDirectory = completion.PublicationDirectory ?? _configuration.FinalDirectory;
        if (Path.Exists(publicationDirectory))
        {
            throw new IOException($"Frame artifact directory appeared before publication: {publicationDirectory}");
        }

        var totalBytes = Directory.EnumerateFiles(
            _stagingDirectory,
            "*",
            SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);

        var result = new RecordFrameArtifactResult
        {
            Directory = publicationDirectory,
            Manifest = Path.Join(publicationDirectory, "manifest.json"),
            Index = Path.Join(publicationDirectory, "frames.ndjson"),
            Samples = SampleCount,
            Images = ImageCount,
            RepeatedSamples = SampleCount - ImageCount,
            TotalBytes = totalBytes,
            Truncated = IsTruncated,
            ByteLimit = _maximumBundleBytes,
        };

        Directory.Move(_stagingDirectory, publicationDirectory);
        _published = true;
        return result;
    }

    public async Task AbortAsync()
    {
        if (_published || _aborted)
        {
            return;
        }

        _aborted = true;
        _lifetimeCts.Cancel();
        _channel.Writer.TryComplete();
        try
        {
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                _configuration.Logger.LogDebug(ex, "Frame artifact worker stopped during cleanup");
            }
        }
        finally
        {
            try
            {
                try
                {
                    await DisposeIndexWriterAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException
                    and not OutOfMemoryException
                    and not StackOverflowException
                    and not AccessViolationException)
                {
                    _configuration.Logger.LogDebug(ex, "Could not close the frame artifact index during cleanup");
                }
            }
            finally
            {
                try
                {
                    DeleteStagingDirectory();
                }
                finally
                {
                    _lifetimeCts.Dispose();
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_published && !_aborted)
        {
            await AbortAsync().ConfigureAwait(false);
            return;
        }

        if (_published)
        {
            _lifetimeCts.Dispose();
        }
    }

    private async Task ProcessFramesAsync()
    {
        byte[]? previousPixels = null;
        var currentImageIndex = -1;
        var currentFile = "";

        try
        {
            await foreach (var queued in _channel.Reader.ReadAllAsync(_lifetimeCts.Token).ConfigureAwait(false))
            {
                if (IsTruncated)
                {
                    continue;
                }

                var changed = previousPixels is null || !queued.Pixels.AsSpan().SequenceEqual(previousPixels);
                byte[]? jpeg = null;
                var nextImageIndex = currentImageIndex;
                var nextFile = currentFile;
                if (changed)
                {
                    nextImageIndex++;
                    var fileName = $"frame-{nextImageIndex:D6}-t{queued.Sample.ElapsedMs:D12}.jpg";
                    nextFile = $"frames/{fileName}";
                    jpeg = s_encodeJpeg(queued.Pixels, _configuration.Width, _configuration.Height);
                }

                var entry = new RecordFrameIndexEntry
                {
                    SampleIndex = queued.Sample.SampleIndex,
                    ElapsedMs = queued.Sample.ElapsedMs,
                    MediaTimeMs = queued.Sample.MediaTimeMs,
                    ImageIndex = nextImageIndex,
                    File = nextFile,
                    Changed = changed,
                };
                var line = JsonSerializer.Serialize(
                    entry,
                    RecordFrameIndexJsonContext.Default.RecordFrameIndexEntry);
                var lineBytes = Encoding.UTF8.GetByteCount(line) + 1;
                if (_imageBytes + _indexBytes + (jpeg?.Length ?? 0) + lineBytes > _dataByteLimit)
                {
                    Interlocked.Exchange(ref _truncated, 1);
                    continue;
                }

                if (jpeg is not null)
                {
                    var absolutePath = Path.Join(_stagingDirectory, nextFile.Replace('/', Path.DirectorySeparatorChar));
                    await using (var imageStream = new FileStream(
                        absolutePath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 64 * 1024,
                        useAsync: true))
                    {
                        await imageStream.WriteAsync(
                            jpeg,
                            _lifetimeCts.Token).ConfigureAwait(false);
                    }

                    currentImageIndex = nextImageIndex;
                    currentFile = nextFile;
                    Interlocked.Add(ref _imageBytes, jpeg.Length);
                    Interlocked.Increment(ref _imageCount);
                    previousPixels = queued.Pixels;
                }

                await _indexWriter.WriteLineAsync(
                    line.AsMemory(),
                    _lifetimeCts.Token).ConfigureAwait(false);
                _indexBytes += lineBytes;
                Interlocked.Exchange(ref _lastIndexedElapsedMs, queued.Sample.ElapsedMs);
                Interlocked.Increment(ref _sampleCount);
            }

            await _indexWriter.FlushAsync(_lifetimeCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _channel.Writer.TryComplete(ex);
            throw;
        }
    }

    private static byte[] EncodeJpeg(byte[] bgra, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var bitmap = new SKBitmap(info);
        Marshal.Copy(bgra, 0, bitmap.GetPixels(), bgra.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality)
            ?? throw new IOException("SkiaSharp could not encode the frame as JPEG.");
        return data.ToArray();
    }

    private async ValueTask DisposeIndexWriterAsync()
    {
        if (_indexDisposed)
        {
            return;
        }

        _indexDisposed = true;
        await _indexWriter.DisposeAsync().ConfigureAwait(false);
    }

    private void DeleteStagingDirectory()
    {
        if (!Directory.Exists(_stagingDirectory))
        {
            return;
        }

        try
        {
            Directory.Delete(_stagingDirectory, recursive: true);
        }
        catch (IOException ex)
        {
            _configuration.Logger.LogWarning(
                ex,
                "Could not remove incomplete frame artifact staging directory {StagingDirectory}",
                _stagingDirectory);
        }
        catch (UnauthorizedAccessException ex)
        {
            _configuration.Logger.LogWarning(
                ex,
                "Could not remove incomplete frame artifact staging directory {StagingDirectory}",
                _stagingDirectory);
        }
    }

    private sealed record QueuedFrame(byte[] Pixels, RecordFrameSample Sample);
}

[JsonSerializable(typeof(RecordFrameIndexEntry))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class RecordFrameIndexJsonContext : JsonSerializerContext;
