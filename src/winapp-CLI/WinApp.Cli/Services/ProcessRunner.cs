// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace WinApp.Cli.Services;

/// <summary>
/// Default <see cref="IProcessRunner"/> backed by <see cref="System.Diagnostics.Process"/>.
/// </summary>
internal sealed class ProcessRunner : IProcessRunner
{
    /// <summary>Starts the child, isolated from every handle this process inherited when it outlives us.</summary>
    private static Helpers.RedirectedProcess StartProcess(ProcessStartInfo psi, bool outlivesCaller)
    {
        if (outlivesCaller)
        {
            return Helpers.OutlivingProcess.StartRedirected(psi);
        }

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process '{psi.FileName}'.");
        return new Helpers.RedirectedProcess(process, process.StandardOutput, process.StandardError);
    }

    public async Task<ProcessRunResult> RunAsync(
        ProcessRunRequest request,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = request.CreateNoWindow,
        };

        // Use ArgumentList rather than a concatenated string so already-validated values can never
        // be reinterpreted as additional command-line arguments to the target.
        foreach (var arg in request.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        if (request.Environment != null)
        {
            foreach (var kvp in request.Environment)
            {
                psi.Environment[kvp.Key] = kvp.Value;
            }
        }

        // A child that outlives this process must not take a copy of any pipe our caller is
        // reading, or the caller would not see end of stream until that child exited.
        using var started = StartProcess(psi, request.OutlivesCaller);
        var process = started.Process;

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        var stdoutTask = DrainAsync(started.StandardOutput, stdout, onOutputLine, cancellationToken);
        var stderrTask = DrainAsync(started.StandardError, stderr, onErrorLine, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);

            // We killed the process while its pipes were still being drained. Observe both read
            // tasks before rethrowing so their reads don't keep running and can't surface later as
            // unobserved task exceptions; the failures they raise here are the expected result of
            // cancelling/killing mid-read.
            await DrainReadsQuietlyAsync(stdoutTask, stderrTask);
            throw;
        }

        await Task.WhenAll(stdoutTask, stderrTask);

        return new ProcessRunResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// Awaits the stdout/stderr drain tasks after the process was killed on cancellation, swallowing
    /// the expected failures (cancellation or a broken pipe from the kill) so the tasks are observed
    /// and never surface as unobserved exceptions. Any unexpected exception is left to propagate.
    /// </summary>
    private static async Task DrainReadsQuietlyAsync(Task stdoutTask, Task stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // Expected: the reads were cancelled or their pipe closed when we killed the process.
        }
    }

    private static async Task DrainAsync(StreamReader reader, StringBuilder sink, Action<string>? onLine, CancellationToken cancellationToken)
    {
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            sink.AppendLine(line);
            onLine?.Invoke(line);
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Best-effort cleanup on cancellation: the process may have already exited
            // (InvalidOperationException) or be unkillable; nothing more we can do.
        }
    }
}
