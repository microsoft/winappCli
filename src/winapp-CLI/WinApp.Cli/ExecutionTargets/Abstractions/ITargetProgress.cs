// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.ExecutionTargets.Abstractions;

/// <summary>
/// Reports what an execution target is doing while it is doing it.
/// </summary>
/// <remarks>
/// <para>
/// Preparing a Sandbox is a sequence of multi-second operations — starting the VM, connecting a
/// client, staging and launching the guest agent, provisioning runtimes, deploying files. Without
/// this the terminal is simply silent for that whole stretch, which is indistinguishable from a
/// hang, and the first thing a user does about a hang is kill the command halfway through a
/// deployment.
/// </para>
/// <para>
/// <b>Everything written here goes to standard error, never standard output.</b> That is not a
/// stylistic choice: <c>--json</c> commands emit a single machine-readable document on stdout, and a
/// progress line interleaved into it would corrupt the contract every scripted caller depends on.
/// Standard error is also the stream a terminal shows immediately and a pipe discards by default,
/// which is exactly the behaviour progress wants.
/// </para>
/// </remarks>
internal interface ITargetProgress
{
    /// <summary>Announces the phase that is about to start.</summary>
    /// <param name="message">
    /// Present-participle description of work in flight, such as "Starting Windows Sandbox...".
    /// </param>
    void Report(string message);
}

/// <summary>Writes progress to standard error.</summary>
/// <remarks>
/// Deliberately unconditional and unbuffered. A progress line that appears only after the slow
/// operation it describes has finished is worse than none, because it reports the past as if it
/// were the present.
/// </remarks>
internal sealed class StandardErrorTargetProgress : ITargetProgress
{
    private readonly Func<TextWriter> _writer;

    /// <summary>Writes to this process's standard error.</summary>
    public StandardErrorTargetProgress()
        : this(() => Console.Error)
    {
    }

    /// <summary>
    /// Writes to the stream <paramref name="writer"/> returns.
    /// </summary>
    /// <remarks>
    /// Resolved per call rather than captured once, because startup replaces the standard streams
    /// when it configures UTF-8 encoding — a writer captured at construction could outlive the
    /// stream it wrapped. Tests use this to observe output without redirecting the whole process,
    /// which would race every other test running in parallel.
    /// </remarks>
    internal StandardErrorTargetProgress(Func<TextWriter> writer) => _writer = writer;

    /// <inheritdoc/>
    public void Report(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var writer = _writer();
        writer.WriteLine(message);
        writer.Flush();
    }
}

/// <summary>Discards progress, for tests and for callers that render their own.</summary>
internal sealed class NullTargetProgress : ITargetProgress
{
    /// <summary>The shared instance.</summary>
    public static NullTargetProgress Instance { get; } = new();

    /// <inheritdoc/>
    public void Report(string message)
    {
    }
}
