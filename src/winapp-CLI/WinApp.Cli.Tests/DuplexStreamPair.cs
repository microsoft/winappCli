// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Tests;

/// <summary>
/// A pair of connected in-memory duplex streams, standing in for a TCP connection.
/// </summary>
/// <remarks>
/// This lets handshake and framing behaviour be exercised end to end — including a hostile peer
/// writing arbitrary bytes — without Windows Sandbox, a network stack, or a real guest, which is
/// what makes the transport contract testable in ordinary CI.
/// </remarks>
internal static class DuplexStreamPair
{
    /// <summary>Creates two streams where each one's writes are the other's reads.</summary>
    public static (Stream Client, Stream Server) Create()
    {
        var clientToServer = new BlockingPipe();
        var serverToClient = new BlockingPipe();

        return (new PipeStream(serverToClient, clientToServer), new PipeStream(clientToServer, serverToClient));
    }

    /// <summary>An unbounded byte queue supporting one reader and one writer.</summary>
    /// <remarks>
    /// Waiting uses a replaceable <see cref="TaskCompletionSource"/> rather than a semaphore: the
    /// waiter captures the current signal <em>inside</em> the lock before releasing it, so a write
    /// that lands between the emptiness check and the await cannot be missed. It also keeps this
    /// type free of disposable state, which matters because both ends share the same pipes and
    /// neither may dispose state the other still needs.
    /// </remarks>
    private sealed class BlockingPipe
    {
        private readonly Queue<byte> _buffer = new();
        private readonly Lock _gate = new();
        private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _completed;

        public void Write(ReadOnlySpan<byte> data)
        {
            lock (_gate)
            {
                foreach (var b in data)
                {
                    _buffer.Enqueue(b);
                }
            }

            Signal();
        }

        public void Complete()
        {
            lock (_gate)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
            }

            Signal();
        }

        public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            if (destination.Length == 0)
            {
                return 0;
            }

            while (true)
            {
                Task wait;
                lock (_gate)
                {
                    if (_buffer.Count > 0)
                    {
                        var count = Math.Min(destination.Length, _buffer.Count);
                        for (var i = 0; i < count; i++)
                        {
                            destination.Span[i] = _buffer.Dequeue();
                        }

                        return count;
                    }

                    if (_completed)
                    {
                        // Completed and drained: end of stream, and every later read agrees.
                        return 0;
                    }

                    wait = _signal.Task;
                }

                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private void Signal()
        {
            TaskCompletionSource previous;
            lock (_gate)
            {
                previous = _signal;
                _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            previous.TrySetResult();
        }
    }

    private sealed class PipeStream(BlockingPipe readPipe, BlockingPipe writePipe) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            readPipe.ReadAsync(buffer, cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            readPipe.ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            writePipe.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count) => writePipe.Write(buffer.AsSpan(offset, count));

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                writePipe.Complete();
            }

            base.Dispose(disposing);
        }
    }
}
