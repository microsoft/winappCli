// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>
/// Forwards this process's standard input to a running guest operation.
/// </summary>
/// <remarks>
/// <para>
/// One implementation shared by every command that claims to stream stdin — <c>sandbox exec</c>,
/// <c>run --on sandbox --with-alias</c>, and the UI router. They have identical ordering and shutdown
/// constraints, and keeping separate copies is how one of them ends up quietly forwarding only
/// output.
/// </para>
/// <para>
/// <b>Start ordering.</b> Forwarding must not begin before the operation ID exists. The channel
/// assigns that ID as it sends the exec request, so bytes written eagerly by the caller — the common
/// <c>echo hi | winapp target exec sandbox ...</c> shape, where input is already buffered before winapp
/// even starts — would otherwise be sent for an operation the guest has not heard of and dropped.
/// Starting from <c>OnOperationId</c> is what makes those first bytes arrive.
/// </para>
/// <para>
/// <b>Shutdown.</b> Host EOF closes guest stdin, which is what lets a guest process that reads to
/// end-of-stream finish rather than wait for input that will never come. Cancellation does not send
/// that close: Ctrl+C is the user taking the command back, the guest tears the operation down, and
/// announcing EOF into a closing channel is noise at best.
/// </para>
/// <para>
/// <b>Why standard input is always opened.</b> Not gated on
/// <see cref="Console.IsInputRedirected"/>. When input is a real console, reading it is how a typed
/// character reaches the guest, and treating that case as immediate EOF would stop an unbounded
/// <c>winapp ui record --on sandbox</c> the instant it started, because that verb ends on stdin EOF by
/// design. The read is fire-and-forget on a background task, so a command whose guest process never
/// reads stdin is unaffected and never waits on it.
/// </para>
/// <para>
/// This is byte forwarding, not a terminal: no ConPTY and no echo, so an interactive console
/// application in the guest observes a redirected pipe. That is the documented no-TTY behaviour.
/// </para>
/// </remarks>
internal static class GuestStandardInputPump
{
    private const int BufferSize = 8 * 1024;

    /// <summary>
    /// Returns the <c>OnOperationId</c> callback that starts forwarding once the operation is named.
    /// </summary>
    /// <remarks>
    /// Deliberately not awaited. The pump's lifetime is the operation's, and awaiting it here would
    /// deadlock the very call it feeds: the operation does not complete until its input is consumed,
    /// and input does not end until the operation does.
    /// </remarks>
    public static Action<Guid> Attach(ITargetOperationExecutor channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return id => _ = RunAsync(channel, id, Console.OpenStandardInput(), cancellationToken);
    }

    /// <summary>
    /// Pumps <paramref name="input"/> into <paramref name="operationId"/>, then closes guest stdin.
    /// </summary>
    /// <param name="channel">Channel the operation is running on.</param>
    /// <param name="operationId">Operation the input belongs to.</param>
    /// <param name="input">Standard input to forward. Disposed when the pump ends.</param>
    /// <param name="cancellationToken">Cancellation, including Ctrl+C.</param>
    /// <remarks>
    /// Internal rather than private so tests can drive it with a real stream and observe the exact
    /// bytes and the close, neither of which is reachable through <see cref="Attach"/>.
    /// </remarks>
    internal static async Task RunAsync(
        ITargetOperationExecutor channel,
        Guid operationId,
        Stream input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(input);

        try
        {
            await using (input.ConfigureAwait(false))
            {
                var buffer = new byte[BufferSize];

                while (!cancellationToken.IsCancellationRequested)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

                    if (read <= 0)
                    {
                        break;
                    }

                    // Raw bytes, never decoded text: decoding per chunk would corrupt binary input
                    // and mangle any multi-byte UTF-8 character straddling a chunk boundary.
                    await channel.SendStandardInputAsync(
                        operationId, buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                await channel.CloseStandardInputAsync(operationId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (
            ex is IOException or ObjectDisposedException or OperationCanceledException or ExecutionTargetException)
        {
            // The command owns the outcome. A closed stdin, a cancelled run, or an operation that has
            // already finished is not a failure of the command itself, and surfacing it here would
            // replace the guest's real exit code with a plumbing error.
        }
    }
}
