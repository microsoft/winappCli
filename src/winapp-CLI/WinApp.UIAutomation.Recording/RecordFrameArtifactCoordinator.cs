// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording;

internal sealed class RecordFrameArtifactCoordinator : IAsyncDisposable
{
    private readonly ILogger _logger;
    private IRecordFrameSink? _sink;

    private RecordFrameArtifactCoordinator(
        ILogger logger,
        IRecordFrameSink? sink,
        Exception? failure)
    {
        _logger = logger;
        _sink = sink;
        Failure = failure;
    }

    public Exception? Failure { get; private set; }

    public int SamplesAccepted { get; private set; }

    public bool IsActive => _sink is not null && Failure is null;

    public static RecordFrameArtifactCoordinator Create(RecordFrameBundleConfiguration configuration)
    {
        try
        {
            return new RecordFrameArtifactCoordinator(
                configuration.Logger,
                RecordFrameBundleWriter.s_create(configuration),
                failure: null);
        }
        catch (Exception ex) when (IsRecoverableFrameOutputFailure(ex))
        {
            LogExpectedFailure(configuration.Logger, ex, "Could not initialize frame artifact output");
            return new RecordFrameArtifactCoordinator(configuration.Logger, sink: null, ex);
        }
    }

    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> processedFrame,
        RecordFrameSample sample)
    {
        if (_sink is null)
        {
            return;
        }

        try
        {
            // Do not cancel a sample already accepted by the capture loop.
            await _sink.WriteAsync(processedFrame, sample, CancellationToken.None).ConfigureAwait(false);
            SamplesAccepted++;
        }
        catch (Exception ex) when (IsRecoverableFrameOutputFailure(ex))
        {
            Failure ??= ex;
            LogExpectedFailure(_logger, ex, "Frame artifact output failed; continuing MP4 recording");
            await AbortAndDisposeAsync("Frame artifact cleanup also failed").ConfigureAwait(false);
        }
    }

    public async Task<RecordFrameArtifactResult?> CompleteAfterVideoFailureAsync(
        RecordFrameCompletion completion)
    {
        if (_sink is null)
        {
            return null;
        }

        if (SamplesAccepted == 0)
        {
            await AbortAndDisposeAsync("Empty frame artifact cleanup failed").ConfigureAwait(false);
            return null;
        }

        try
        {
            var result = await _sink.CompleteAsync(completion).ConfigureAwait(false);
            await DisposeSinkAsync().ConfigureAwait(false);
            return result;
        }
        catch (Exception ex) when (IsRecoverableFrameOutputFailure(ex))
        {
            Failure ??= ex;
            LogExpectedFailure(_logger, ex, "Could not preserve partial frame artifacts after MP4 failure");
            await AbortAndDisposeAsync("Partial frame artifact cleanup also failed").ConfigureAwait(false);
            return null;
        }
    }

    public async Task<RecordFrameArtifactResult?> CompleteAfterVideoSuccessAsync(
        RecordFrameCompletion completion)
    {
        if (_sink is null)
        {
            return null;
        }

        try
        {
            var result = await _sink.CompleteAsync(completion).ConfigureAwait(false);
            await DisposeSinkAsync().ConfigureAwait(false);
            return result;
        }
        catch (Exception ex) when (IsRecoverableFrameOutputFailure(ex))
        {
            Failure ??= ex;
            LogExpectedFailure(_logger, ex, "Could not finalize frame artifact output");
            await AbortAndDisposeAsync("Frame artifact cleanup also failed").ConfigureAwait(false);
            return null;
        }
    }

    public Task AbortAsync()
        => AbortAndDisposeAsync("Frame artifact cleanup failed");

    public async ValueTask DisposeAsync()
    {
        await DisposeSinkAsync().ConfigureAwait(false);
    }

    private async Task AbortAndDisposeAsync(string cleanupMessage)
    {
        var sink = _sink;
        if (sink is null)
        {
            return;
        }

        try
        {
            await sink.AbortAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRecoverableFrameOutputFailure(ex))
        {
            Failure = Failure is null ? ex : new AggregateException(Failure, ex);
            _logger.LogDebug(ex, "Frame artifact cleanup failed during {CleanupStage}", cleanupMessage);
        }
        finally
        {
            await DisposeSinkAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask DisposeSinkAsync()
    {
        var sink = _sink;
        if (sink is null)
        {
            return;
        }

        _sink = null;
        await sink.DisposeAsync().ConfigureAwait(false);
    }

    private static void LogExpectedFailure(ILogger logger, Exception exception, string message)
    {
        logger.LogError("{Message}: {Reason}", message, exception.Message);
        logger.LogDebug(
            "{Message} ({ExceptionType}): {Reason}",
            message,
            exception.GetType().Name,
            exception.Message);
    }

    private static bool IsRecoverableFrameOutputFailure(Exception exception)
        => exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException;
}
