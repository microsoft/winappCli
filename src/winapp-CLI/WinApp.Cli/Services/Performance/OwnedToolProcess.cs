// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using WinApp.Cli.Services;

namespace WinApp.Cli.Services.Performance;

internal interface IOwnedToolProcess : IAsyncDisposable
{
    Task<ProcessRunResult> Completion { get; }

    Task<bool> RequestSoftStopAsync(string input, TimeSpan timeout);
}

internal interface IOwnedToolProcessFactory
{
    IOwnedToolProcess Start(ProcessRunRequest request);
}

internal sealed class OwnedToolProcessFactory : IOwnedToolProcessFactory
{
    public IOwnedToolProcess Start(ProcessRunRequest request) => new OwnedToolProcess(request);

    private sealed class OwnedToolProcess : IOwnedToolProcess
    {
        private readonly Process _process;
        private readonly StringBuilder _stdout = new();
        private readonly StringBuilder _stderr = new();
        private readonly Task _stdoutTask;
        private readonly Task _stderrTask;
        private bool _disposed;

        public OwnedToolProcess(ProcessRunRequest request)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = request.FileName,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = request.CreateNoWindow,
            };
            foreach (var argument in request.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
            if (request.Environment is not null)
            {
                foreach (var pair in request.Environment)
                {
                    startInfo.Environment[pair.Key] = pair.Value;
                }
            }

            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start process '{request.FileName}'.");
            _stdoutTask = DrainAsync(_process.StandardOutput, _stdout);
            _stderrTask = DrainAsync(_process.StandardError, _stderr);
            Completion = WaitForCompletionAsync();
        }

        public Task<ProcessRunResult> Completion { get; }

        public async Task<bool> RequestSoftStopAsync(string input, TimeSpan timeout)
        {
            if (_process.HasExited)
            {
                return true;
            }

            try
            {
                await _process.StandardInput.WriteAsync(input);
                await _process.StandardInput.FlushAsync();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                return _process.HasExited;
            }

            return await Task.WhenAny(Completion, Task.Delay(timeout)) == Completion;
        }

        private async Task<ProcessRunResult> WaitForCompletionAsync()
        {
            await _process.WaitForExitAsync();
            await Task.WhenAll(_stdoutTask, _stderrTask);
            return new(_process.ExitCode, _stdout.ToString(), _stderr.ToString());
        }

        private static async Task DrainAsync(StreamReader reader, StringBuilder sink)
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                sink.AppendLine(line);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            if (!_process.HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                }
            }

            try
            {
                await Completion;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
            }
            _process.Dispose();
        }
    }
}
