// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Pipes;
using WinApp.HotReload.Protocol;

namespace WinApp.HotReload;

/// <summary>The helper's end of the pipe to the agent inside the app.</summary>
internal sealed class AgentClient(NamedPipeClientStream pipe, string token) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Connects once the app's agent listens (the app may still be starting).</summary>
    public static async Task<AgentClient> ConnectAsync(string pipeName, string token, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync(1000, cancellationToken).ConfigureAwait(false);
                return new AgentClient(pipe, token);
            }
            catch (TimeoutException) when (DateTime.UtcNow < deadline)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task<HotReloadResponse> SendAsync(HotReloadRequest request, CancellationToken cancellationToken)
    {
        request.Token = token;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await HotReloadFrames.WriteAsync(pipe, request, cancellationToken).ConfigureAwait(false);
            return await HotReloadFrames.ReadAsync<HotReloadResponse>(pipe, cancellationToken).ConfigureAwait(false)
                ?? throw new EndOfStreamException("The app closed the hot reload connection.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        pipe.Dispose();
        _gate.Dispose();
    }
}
