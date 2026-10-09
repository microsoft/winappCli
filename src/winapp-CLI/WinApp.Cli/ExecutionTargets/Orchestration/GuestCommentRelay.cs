// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

internal sealed record GuestCommentEnvelope(string Kind, GuestCommentRequest Request);
internal sealed record GuestCommentReply(
    string OperationId, GuestCommentCommit? Commit = null, IReadOnlyList<Comment>? Comments = null, string? Error = null,
    string? HostStorePath = null, string? GuestSourceRoot = null, long Generation = 0)
{
    // Unsolicited host snapshot for the running app's markers; not an acknowledgement.
    internal const string RefreshOperationId = "refresh";
}

[JsonSerializable(typeof(GuestCommentEnvelope))]
[JsonSerializable(typeof(GuestCommentReply))]
[JsonSerializable(typeof(GuestAgent.GuestCommentSession))]
[JsonSerializable(typeof(GuestAgent.GuestInspectedAppFrame))]
[JsonSerializable(typeof(GuestAgent.GuestInspectedAppControl))]
[JsonSerializable(typeof(GuestDevToolsHostPlan))]
[JsonSerializable(typeof(GuestDevToolsHostMessage))]
[JsonSerializable(typeof(GuestSourceInventory))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class GuestCommentsJsonContext : JsonSerializerContext;

// Length-prefixed JSON carried on the existing authenticated operation streams.
internal static class GuestCommentFrames
{
    internal const int MaximumLength = 256 * 1024;

    internal static byte[] Encode<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        using var bounded = new MessageBuffer();
        JsonSerializer.Serialize(bounded, value, type);
        var json = bounded.ToArray();
        var frame = new byte[sizeof(int) + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length);
        json.CopyTo(frame.AsSpan(sizeof(int)));
        return frame;
    }

    private sealed class MessageBuffer : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Length + count > MaximumLength)
            {
                throw new InvalidOperationException("The comment response exceeds the relay message limit.");
            }
            base.Write(buffer, offset, count);
        }
    }

    internal static async Task<byte[]> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumLength)
        {
            throw new InvalidDataException("The comment relay sent an invalid or oversized message.");
        }
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    internal static async Task WriteAsync<T>(
        Stream stream, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encode(value, type), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal sealed class Reader(Action<byte[]> receive)
    {
        private readonly byte[] _header = new byte[sizeof(int)];
        private int _headerBytes;
        private byte[]? _payload;
        private int _payloadBytes;

        internal void Append(ReadOnlySpan<byte> chunk)
        {
            while (!chunk.IsEmpty)
            {
                if (_payload is null)
                {
                    var count = Math.Min(_header.Length - _headerBytes, chunk.Length);
                    chunk[..count].CopyTo(_header.AsSpan(_headerBytes));
                    _headerBytes += count;
                    chunk = chunk[count..];
                    if (_headerBytes != _header.Length)
                    {
                        continue;
                    }
                    var length = BinaryPrimitives.ReadInt32LittleEndian(_header);
                    if (length is <= 0 or > MaximumLength)
                    {
                        throw new InvalidDataException("The guest comment relay sent an invalid or oversized message.");
                    }
                    _payload = new byte[length];
                }
                var take = Math.Min(_payload.Length - _payloadBytes, chunk.Length);
                chunk[..take].CopyTo(_payload.AsSpan(_payloadBytes));
                _payloadBytes += take;
                chunk = chunk[take..];
                if (_payloadBytes == _payload.Length)
                {
                    var complete = _payload;
                    _payload = null;
                    _payloadBytes = 0;
                    _headerBytes = 0;
                    receive(complete);
                }
            }
        }
    }
}

/// <summary>Owns one relay operation, never the channel's receive pump or another guest operation.</summary>
internal sealed class GuestCommentRelay(
    GuestCommentBinding binding, GuestCommentOwner owner, ITargetOperationExecutor operations)
{
    internal TimeSpan LifetimePollInterval { get; init; } = TimeSpan.FromSeconds(1);

    internal async Task RunAsync(Action<ReadOnlyMemory<byte>> diagnostic, CancellationToken cancellationToken, Action? onReady = null)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var incoming = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait,
        });
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new GuestCommentFrames.Reader(packet =>
        {
            if (!incoming.Writer.TryWrite(packet))
            {
                throw new InvalidDataException("The guest comment relay exceeded its bounded request queue.");
            }
        });
        var execution = operations.ExecuteAsync(new GuestExecRequest
        {
            UseGuestWinapp = true,
            Arguments =
            [
                "guest-comment-relay", "--binding", binding.Id, "--target", binding.TargetId,
                "--epoch", binding.Epoch, "--pid", binding.Process.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--start", binding.Process.StartTicksUtc.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ],
        }, new GuestExecCallbacks(
            OnOperationId: id => operation.TrySetResult(id),
            OnStandardOutput: chunk =>
            {
                try
                {
                    reader.Append(chunk.Span);
                }
                catch (InvalidDataException ex)
                {
                    failed.TrySetException(ex);
                }
            },
            OnStandardError: diagnostic), stop.Token);
        var endpointReady = false;
        var requests = ProcessRequestsAsync();
        var lifetime = MonitorLifetimeAsync();
        try
        {
            var completed = await Task.WhenAny(execution, requests, lifetime, failed.Task).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (completed != lifetime)
            {
                throw new IOException("The guest comment relay disconnected. Host comments remain saved; authoring is unavailable.");
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            incoming.Writer.TryComplete();
            try
            {
                await Task.WhenAll(execution, requests, lifetime).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // ExecuteAsync has acknowledged cancellation before the operation is released.
            }
        }

        async Task ProcessRequestsAsync()
        {
            var ready = false;
            await foreach (var bytes in incoming.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
            {
                var message = JsonSerializer.Deserialize(bytes, GuestCommentsJsonContext.Default.GuestCommentEnvelope)
                    ?? throw new InvalidDataException("The guest comment relay sent an empty request.");
                if (message.Request is null)
                {
                    throw new InvalidDataException("The guest comment relay omitted its launch identity.");
                }
                binding.Validate(message.Request.BindingId, message.Request.TargetId, message.Request.Epoch, message.Request.Process);
                if (!await IsAppAliveAsync().ConfigureAwait(false))
                {
                    throw new IOException("The bound guest application exited. No comment mutation was applied.");
                }
                if (message.Kind == "ready")
                {
                    if (ready)
                    {
                        throw new InvalidDataException("The guest comment relay sent duplicate readiness.");
                    }
                    binding.VerifyHostPaths();
                    ready = true;
                    Volatile.Write(ref endpointReady, true);
                    onReady?.Invoke();
                    continue;
                }
                if (!ready)
                {
                    throw new InvalidDataException("The guest comment relay requested access before opening its endpoint.");
                }
                GuestCommentReply reply;
                try
                {
                    reply = message.Kind switch
                    {
                        "read" => ReadReply(message.Request.OperationId),
                        "apply" => new(message.Request.OperationId, Commit: owner.Apply(message.Request)),
                        _ => throw new InvalidOperationException("Unknown guest comment request."),
                    };
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or KeyNotFoundException or
                    UnauthorizedAccessException or TimeoutException or CommentStoreCorruptException)
                {
                    reply = new(message.Request.OperationId, Error: ex.Message);
                }
                byte[] frame;
                try
                {
                    frame = GuestCommentFrames.Encode(reply, GuestCommentsJsonContext.Default.GuestCommentReply);
                }
                catch (InvalidOperationException ex)
                {
                    frame = GuestCommentFrames.Encode(
                        new GuestCommentReply(message.Request.OperationId, Error: ex.Message),
                        GuestCommentsJsonContext.Default.GuestCommentReply);
                }
                var id = await operation.Task.WaitAsync(stop.Token).ConfigureAwait(false);
                await operations.SendStandardInputAsync(id, frame, stop.Token).ConfigureAwait(false);
            }
        }

        Task<bool> IsAppAliveAsync() => operations.IsTrackedProcessRunningAsync(
            binding.Process.ProcessId, binding.Process.StartTicksUtc, stop.Token);

        GuestCommentReply ReadReply(string operationId)
        {
            var snapshot = owner.ReadSnapshot();
            return new(operationId, Comments: snapshot.Comments, HostStorePath: binding.StorePath,
                GuestSourceRoot: binding.GuestSourceRoot, Generation: snapshot.Generation);
        }

        async Task MonitorLifetimeAsync()
        {
            (DateTime, long)? seen = null;
            long? sent = null;
            while (await IsAppAliveAsync().ConfigureAwait(false))
            {
                if (Volatile.Read(ref endpointReady))
                {
                    await RefreshGuestAsync().ConfigureAwait(false);
                }
                await Task.Delay(LifetimePollInterval, stop.Token).ConfigureAwait(false);
            }

            // Host-side edits (an agent resolving a comment) reach the guest app's markers on the next tick.
            async Task RefreshGuestAsync()
            {
                var file = new FileInfo(binding.StorePath);
                (DateTime, long)? stamp = file.Exists ? (file.LastWriteTimeUtc, file.Length) : null;
                if (sent is not null && stamp == seen)
                {
                    return;
                }
                GuestCommentReply reply;
                try
                {
                    reply = ReadReply(GuestCommentReply.RefreshOperationId);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or CommentStoreCorruptException)
                {
                    // Usually a read racing an atomic save; the next tick retries.
                    return;
                }
                seen = stamp;
                if (reply.Generation == sent)
                {
                    return;
                }
                var id = await operation.Task.WaitAsync(stop.Token).ConfigureAwait(false);
                await operations.SendStandardInputAsync(id,
                    GuestCommentFrames.Encode(reply, GuestCommentsJsonContext.Default.GuestCommentReply), stop.Token).ConfigureAwait(false);
                sent = reply.Generation;
            }
        }

    }
}
