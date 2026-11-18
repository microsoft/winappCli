// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace WinApp.Cli.Services.Performance;

internal static class PerfControlChannel
{
    public static string PipeName(string id) => "winapp-perf-" + id;

    internal static async Task<NamedPipeClientStream> ConnectAsync(string id, PipeDirection direction,
        CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(".", PipeName(id), direction, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(token);
            using var identity = WindowsIdentity.GetCurrent();
            // CurrentUserOnly compares against the token owner, which can be Administrators when elevated.
            if (pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) != identity.User)
            {
                throw new UnauthorizedAccessException("The performance control pipe is not owned by the current user.");
            }
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    public static async Task<T> ReadAsync<T>(Stream stream, JsonTypeInfo<T> type, int limit, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 1 || length > limit)
        {
            throw new InvalidDataException("Performance control message exceeds its size limit.");
        }
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize(bytes, type)
            ?? throw new InvalidDataException("Empty performance control message.");
    }

    public static async Task WriteAsync<T>(Stream stream, T message, JsonTypeInfo<T> type, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, type);
        if (bytes.Length > 262144)
        {
            throw new InvalidDataException("Performance control response exceeds its size limit.");
        }
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    public static async Task<PerfCaptureDocument> SendAsync(PerfControlRegistration registration,
        PerfControlRequest request, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.Operation == "bind" ? 30 : 8));
        using var pipe = await ConnectAsync(registration.Id, PipeDirection.InOut, timeout.Token);
        await WriteAsync(pipe, request, PerfJsonContext.Default.PerfControlRequest, timeout.Token);
        var response = await ReadAsync(pipe, PerfJsonContext.Default.PerfControlResponse, 262144, timeout.Token);
        if (response.Error is { } error)
        {
            throw new InvalidOperationException(error);
        }
        return response.Capture ?? throw new InvalidDataException("Worker returned no capture status.");
    }
}
