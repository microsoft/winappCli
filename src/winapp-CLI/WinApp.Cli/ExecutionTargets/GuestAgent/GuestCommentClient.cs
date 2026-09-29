// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Pipes;
using System.Text.Json;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.ExecutionTargets.GuestAgent;

internal static class GuestCommentClient
{
    internal static async Task<GuestCommentReply> ExchangeAsync(
        GuestProcessStart process, Func<GuestCommentSession, GuestCommentEnvelope> createRequest,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var name = new GuestCommentSession(string.Empty, string.Empty, string.Empty, process).PipeName;
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            try
            {
                await pipe.ConnectAsync(3000, timeout.Token).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                // CurrentUserOnly inspects the remote ACL during ConnectAsync; a rejecting server
                // can disconnect during that inspection, before the connection is established.
                throw new IOException("The comment relay disconnected during authenticated connection.", ex);
            }
            GuestCommentPeer.Verify(pipe);
            var hello = await GuestCommentFrames.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
            var session = JsonSerializer.Deserialize(hello, GuestCommentsJsonContext.Default.GuestCommentSession)
                ?? throw new InvalidDataException("The host comment relay did not identify its bound launch.");
            if (session.Process != process)
            {
                throw new InvalidDataException("The host comment relay belongs to another process lifetime.");
            }
            var request = createRequest(session);
            session.Validate(request.Request);
            await GuestCommentFrames.WriteAsync(pipe, request, GuestCommentsJsonContext.Default.GuestCommentEnvelope, timeout.Token).ConfigureAwait(false);
            var response = await GuestCommentFrames.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
            var reply = JsonSerializer.Deserialize(response, GuestCommentsJsonContext.Default.GuestCommentReply)
                ?? throw new InvalidDataException("The host did not acknowledge comment persistence.");
            if (reply.OperationId != request.Request.OperationId)
            {
                throw new InvalidDataException("The host acknowledged a different comment operation.");
            }
            if (reply.Error is not null)
            {
                throw new InvalidOperationException(reply.Error);
            }
            return reply;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new IOException(
                "The host comment owner is unavailable or its acknowledgement was lost. Nothing was saved to a disposable guest store. " +
                "Refresh host comments before retrying.", ex);
        }
    }
}
