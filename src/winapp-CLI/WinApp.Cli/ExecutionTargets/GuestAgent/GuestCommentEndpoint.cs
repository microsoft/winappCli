// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services.DevTools;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.ExecutionTargets.GuestAgent;

internal sealed record GuestCommentSession(string BindingId, string TargetId, string Epoch, GuestProcessStart Process)
{
    internal string PipeName => $"WinApp.DevTools.GuestComments.{Process.ProcessId}.{Process.StartTicksUtc}";

    internal void Validate(GuestCommentRequest request)
    {
        if (request.BindingId != BindingId || request.TargetId != TargetId ||
            request.Epoch != Epoch || request.Process != Process || !Guid.TryParseExact(request.OperationId, "N", out _))
        {
            throw new InvalidDataException("The comment request does not belong to this guest launch.");
        }
    }
}

// Guest-local same-owner clients; only host acknowledgements count as successful writes.
internal sealed class GuestCommentEndpoint(GuestCommentSession session)
{
    internal async Task RunAsync(
        Stream hostInput, Stream hostOutput, Action<string> diagnostic, CancellationToken cancellationToken,
        Func<bool>? isAppAlive = null, Action<NamedPipeServerStream>? verifyPeer = null,
        Action<GuestCommentReply>? pushToApp = null)
    {
        isAppAlive ??= () => GuestCommandServer.IsExactProcessRunning(session.Process.ProcessId, session.Process.StartTicksUtc);
        verifyPeer ??= GuestCommentPeer.Verify;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        pushToApp ??= PushToApp;
        var replies = Channel.CreateBounded<GuestCommentReply>(1);
        // Only the newest host snapshot matters; an older one waiting behind a slow app is dropped.
        var refreshes = Channel.CreateBounded<GuestCommentReply>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        var input = ReadHostAsync();
        var lifetime = WatchAppAsync();
        var listener = ServeAsync();
        var markers = RefreshMarkersAsync();
        try
        {
            var finished = await Task.WhenAny(input, lifetime, listener).ConfigureAwait(false);
            await finished.ConfigureAwait(false);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            replies.Writer.TryComplete();
            refreshes.Writer.TryComplete();
            try
            {
                await Task.WhenAll(input, lifetime, listener, markers).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // The pipe and both standard streams are released with the owning operation.
            }
        }

        async Task ReadHostAsync()
        {
            while (true)
            {
                var bytes = await GuestCommentFrames.ReadAsync(hostInput, stop.Token).ConfigureAwait(false);
                var reply = JsonSerializer.Deserialize(bytes, GuestCommentsJsonContext.Default.GuestCommentReply)
                    ?? throw new InvalidDataException("The host comment owner sent an empty acknowledgement.");
                if (reply.OperationId == GuestCommentReply.RefreshOperationId)
                {
                    refreshes.Writer.TryWrite(reply);
                    continue;
                }
                if (!replies.Writer.TryWrite(reply))
                {
                    throw new InvalidDataException("The host comment owner sent unsolicited acknowledgements.");
                }
            }
        }

        async Task RefreshMarkersAsync()
        {
            await foreach (var snapshot in refreshes.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
            {
                try
                {
                    pushToApp(snapshot);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
                    JsonException or TimeoutException or InvalidOperationException)
                {
                    diagnostic($"Comment markers were not refreshed: {ex.Message}");
                }
            }
        }

        void PushToApp(GuestCommentReply snapshot)
        {
            var comments = CommentPusher.ToTapComments(
                new CommentStoreDocument { Comments = snapshot.Comments?.ToList() ?? [] }, CommentStore.Revision);
            _ = new VisualTreeTap(checked((uint)session.Process.ProcessId))
                .SetComments(comments, snapshot.Generation, cancellationToken: stop.Token).RequireResult();
        }

        async Task WatchAppAsync()
        {
            while (isAppAlive())
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stop.Token).ConfigureAwait(false);
            }
        }

        async Task ServeAsync()
        {
            using var pipe = new NamedPipeServerStream(session.PipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
            await GuestCommentFrames.WriteAsync(hostOutput, new GuestCommentEnvelope("ready",
                new(session.BindingId, session.TargetId, session.Epoch, session.Process,
                    Guid.NewGuid().ToString("N"), string.Empty, string.Empty, null)),
                GuestCommentsJsonContext.Default.GuestCommentEnvelope, stop.Token).ConfigureAwait(false);
            while (!stop.IsCancellationRequested)
            {
                await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                var token = requestTimeout.Token;
                try
                {
                    verifyPeer(pipe);
                    await GuestCommentFrames.WriteAsync(pipe, session, GuestCommentsJsonContext.Default.GuestCommentSession, token).ConfigureAwait(false);
                    var bytes = await GuestCommentFrames.ReadAsync(pipe, token).ConfigureAwait(false);
                    var envelope = JsonSerializer.Deserialize(bytes, GuestCommentsJsonContext.Default.GuestCommentEnvelope)
                        ?? throw new InvalidDataException("The guest CLI sent an empty comment request.");
                    if (envelope.Request is null)
                    {
                        throw new InvalidDataException("The guest CLI omitted the comment binding.");
                    }
                    session.Validate(envelope.Request);
                    if (!isAppAlive())
                    {
                        throw new InvalidDataException("The bound guest application has exited.");
                    }
                    await GuestCommentFrames.WriteAsync(hostOutput, envelope, GuestCommentsJsonContext.Default.GuestCommentEnvelope, token).ConfigureAwait(false);
                    var reply = await replies.Reader.ReadAsync(token).ConfigureAwait(false);
                    if (reply.OperationId != envelope.Request.OperationId)
                    {
                        throw new InvalidDataException("The host comment acknowledgement belongs to another operation.");
                    }
                    await GuestCommentFrames.WriteAsync(pipe, reply, GuestCommentsJsonContext.Default.GuestCommentReply, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                    diagnostic(ex.Message);
                    // A write might have committed. Never reuse the stream with an unconsumed acknowledgement.
                    throw;
                }
                finally
                {
                    pipe.Disconnect();
                }
            }
        }
    }
}
