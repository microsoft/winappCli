// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Describes a child process to launch. Arguments are passed as a list (never a single
/// concatenated string) so already-validated values cannot be smuggled in as extra arguments.
/// </summary>
internal sealed record ProcessRunRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    bool CreateNoWindow = true,
    IReadOnlyDictionary<string, string>? Environment = null)
{
    /// <summary>
    /// Whether the child must not inherit this process's standard handles.
    /// </summary>
    /// <remarks>
    /// Set for a child that is expected to outlive winapp. Such a child would otherwise hold a
    /// duplicate of the caller's captured stdout/stderr pipe, and the caller would not reach end of
    /// stream until that child exited — long after the command it ran had finished. See
    /// <see cref="Helpers.StandardHandleInheritance"/> for why redirecting the child's own streams
    /// is not sufficient on its own.
    /// </remarks>
    public bool OutlivesCaller { get; init; }
}

/// <summary>Captured result of a completed child process.</summary>
internal sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Launches child processes with redirected, concurrently drained output. Injecting this behind an
/// interface lets security-sensitive process construction (the Azure CLI login/token fallback) be
/// exercised in tests without spawning a real process.
/// </summary>
internal interface IProcessRunner
{
    /// <summary>
    /// Starts the requested process, forwarding each stdout/stderr line to the optional callbacks
    /// as it arrives while also accumulating the full output. Draining both pipes concurrently
    /// avoids the deadlock that occurs when a child fills a redirected pipe. On cancellation the
    /// process tree is killed and <see cref="OperationCanceledException"/> propagates.
    /// </summary>
    Task<ProcessRunResult> RunAsync(
        ProcessRunRequest request,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null,
        CancellationToken cancellationToken = default);
}
