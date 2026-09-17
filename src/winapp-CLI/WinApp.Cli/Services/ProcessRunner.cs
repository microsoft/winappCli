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
    /// <summary>Starts the child, optionally detached from this process's standard handles.</summary>
    private static Process? StartProcess(ProcessStartInfo psi, bool outlivesCaller)
    {
        if (!outlivesCaller)
        {
            return Process.Start(psi);
        }

        using var _ = Helpers.StandardHandleInheritance.Suppress();
        return Process.Start(psi);
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

        // Started inside the suppression scope when the child will outlive this process, so it
        // cannot take a duplicate of the caller's captured stdout/stderr pipe with it. The child's
        // own redirected pipes are created inheritable by .NET and are unaffected.
        using var process = StartProcess(psi, request.OutlivesCaller)
            ?? throw new InvalidOperationException($"Failed to start process '{request.FileName}'.");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        var stdoutTask = DrainAsync(process.StandardOutput, stdout, onOutputLine, cancellationToken);
        var stderrTask = DrainAsync(process.StandardError, stderr, onErrorLine, cancellationToken);

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
