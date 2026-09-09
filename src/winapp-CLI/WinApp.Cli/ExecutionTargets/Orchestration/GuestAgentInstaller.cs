// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>Runs a candidate agent binary's self-test.</summary>
/// <remarks>
/// Behind an interface so activation, rollback, and the failed-self-test path can be exercised
/// without launching processes — the failure cases are the ones that matter and the ones a real
/// binary will not reproduce on demand.
/// </remarks>
internal interface IGuestAgentSelfTest
{
    /// <summary>Whether the binary at <paramref name="binaryPath"/> reports itself healthy.</summary>
    Task<bool> RunAsync(string binaryPath, CancellationToken cancellationToken);
}

/// <summary>Runs the self-test by invoking the candidate binary's hidden agent verb.</summary>
/// <remarks>
/// The candidate tests <em>itself</em>, in its own process. A check performed by the currently
/// running agent would prove only that the file exists — not that the new binary can actually load
/// and initialise on this machine, which is the failure staging exists to catch.
/// </remarks>
internal sealed class GuestAgentSelfTest : IGuestAgentSelfTest
{
    /// <summary>How long a candidate gets to prove itself before it is treated as failed.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <inheritdoc/>
    public async Task<bool> RunAsync(string binaryPath, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        var startInfo = new ProcessStartInfo
        {
            FileName = binaryPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add(GuestAgentCommandNames.Verb);
        startInfo.ArgumentList.Add(GuestAgentCommandNames.SelfTestOption);

        Process? process = null;

        try
        {
            process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            // Both streams are drained concurrently with the wait. A redirected pipe that nobody
            // reads fills at around 4 KB and blocks the child forever, so a candidate that printed
            // more than that would look like a hang rather than a pass.
            var drain = Task.WhenAll(
                process.StandardOutput.ReadToEndAsync(timeout.Token),
                process.StandardError.ReadToEndAsync(timeout.Token));

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await drain.ConfigureAwait(false);

            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A candidate that hangs is as broken as one that fails.
            return false;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The candidate could not be launched at all: wrong architecture, corrupt image, or
            // blocked by policy. All are self-test failures, not host failures.
            return false;
        }
        finally
        {
            // A timed-out or cancelled candidate is still running and still holding the staged
            // binary open, which would make the activation rename fail for a reason unrelated to
            // the real problem. Kill the whole tree and wait for it to actually go.
            await TerminateAsync(process).ConfigureAwait(false);
        }
    }

    /// <summary>Kills a candidate that did not exit on its own, and releases its handles.</summary>
    private static async Task TerminateAsync(Process? process)
    {
        if (process is null)
        {
            return;
        }

        using (process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);

                    // Waiting matters: the file lock is released when the process actually dies,
                    // not when the kill is requested.
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone, or not killable. Activation reports the real consequence if the
                // staged file is still locked.
            }
        }
    }
}

/// <summary>Names shared by the hidden agent verb and anything that invokes it.</summary>
internal static class GuestAgentCommandNames
{
    /// <summary>The hidden verb that runs winapp as a persistent guest agent.</summary>
    public const string Verb = "guest-agent";

    /// <summary>Flag that makes the agent verify itself and exit instead of serving.</summary>
    public const string SelfTestOption = "--self-test";
}

/// <summary>
/// Stages, verifies, self-tests, and activates a guest agent binary, keeping the previous one as
/// last-known-good (spec §"Agent versioning and upgrades").
/// </summary>
/// <remarks>
/// Layout under the agent root:
/// <code>
/// current\winapp.exe          the binary the agent runs from
/// staged\winapp.exe           the candidate being verified
/// previous\winapp.exe         last-known-good, kept until the replacement proves healthy
/// </code>
/// Activation is a rename, not a copy, so there is no window in which <c>current</c> holds a
/// half-written file. If any step after the swap fails, <c>previous</c> is restored, and the target
/// is never reported healthy on a failed activation.
/// </remarks>
internal sealed class GuestAgentInstaller(IGuestAgentSelfTest selfTest)
{
    /// <summary>Name every managed copy of the agent binary uses.</summary>
    internal const string BinaryName = "winapp.exe";

    private const string CurrentFolder = "current";
    private const string StagedFolder = "staged";
    private const string PreviousFolder = "previous";

    /// <summary>Path of the active binary under <paramref name="agentRoot"/>.</summary>
    public static string CurrentBinaryPath(string agentRoot) =>
        TargetPathSafety.CombineInsideRoot(agentRoot, CurrentFolder, BinaryName);

    /// <summary>Path of the staged candidate under <paramref name="agentRoot"/>.</summary>
    public static string StagedBinaryPath(string agentRoot) =>
        TargetPathSafety.CombineInsideRoot(agentRoot, StagedFolder, BinaryName);

    /// <summary>Path of the last-known-good binary under <paramref name="agentRoot"/>.</summary>
    public static string PreviousBinaryPath(string agentRoot) =>
        TargetPathSafety.CombineInsideRoot(agentRoot, PreviousFolder, BinaryName);

    /// <summary>
    /// Copies <paramref name="sourceBinary"/> into staging and proves it is byte-identical to what
    /// the host intended.
    /// </summary>
    /// <remarks>
    /// The hash is verified after the copy rather than before, because the value that matters is
    /// what landed in the guest — a truncated or torn transfer is exactly what this catches.
    /// </remarks>
    /// <exception cref="ExecutionTargetException">Staging or verification failed.</exception>
    public static async Task<string> StageAsync(
        string agentRoot,
        string sourceBinary,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBinary);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHash);

        var stagedPath = StagedBinaryPath(agentRoot);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
            File.Copy(sourceBinary, stagedPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Failed("The replacement Windows Sandbox agent could not be staged.", ex);
        }

        var actualHash = await GuestAgentIdentity
            .ComputeBinaryHashAsync(stagedPath, cancellationToken)
            .ConfigureAwait(false);

        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(stagedPath);

            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.AgentUpgradeFailed,
                "The staged Windows Sandbox agent did not match the expected binary.",
                userAction: "Retry the command. If it keeps failing, reinstall winapp.",
                context: new Dictionary<string, string>
                {
                    ["expectedHash"] = expectedHash,
                    ["actualHash"] = actualHash,
                });
        }

        return stagedPath;
    }

    /// <summary>
    /// Self-tests the staged binary and, only if it passes, makes it current.
    /// </summary>
    /// <remarks>
    /// The order is deliberate: a candidate that cannot even start is rejected before the current
    /// binary is touched, so the common failure never puts the target in a state needing recovery.
    /// The rename-based swap and rollback cover the rarer case where the candidate passes its own
    /// test but activation still fails.
    /// </remarks>
    /// <exception cref="ExecutionTargetException">The candidate failed, and was rolled back.</exception>
    public async Task ActivateAsync(string agentRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentRoot);

        var stagedPath = StagedBinaryPath(agentRoot);
        var currentPath = CurrentBinaryPath(agentRoot);
        var previousPath = PreviousBinaryPath(agentRoot);

        if (!File.Exists(stagedPath))
        {
            throw Failed("There is no staged Windows Sandbox agent to activate.", innerException: null);
        }

        if (!await selfTest.RunAsync(stagedPath, cancellationToken).ConfigureAwait(false))
        {
            TryDelete(stagedPath);

            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.AgentUpgradeFailed,
                "The replacement Windows Sandbox agent failed its self-test and was not activated.",
                userAction: "Retry the command. If it keeps failing, reinstall winapp.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(currentPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(previousPath)!);

        var hadCurrent = File.Exists(currentPath);

        try
        {
            if (hadCurrent)
            {
                // Keep the working binary as last-known-good until the replacement proves healthy.
                File.Move(currentPath, previousPath, overwrite: true);
            }

            File.Move(stagedPath, currentPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RollBack(agentRoot, hadCurrent);
            throw Failed("The replacement Windows Sandbox agent could not be activated.", ex);
        }
    }

    /// <summary>
    /// Restores the last-known-good binary after a failed activation.
    /// </summary>
    /// <remarks>
    /// Public so the caller can roll back when the newly activated agent never reports ready, which
    /// is a failure activation itself cannot observe.
    /// </remarks>
    public static void RollBack(string agentRoot, bool hadCurrent = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentRoot);

        if (!hadCurrent)
        {
            return;
        }

        var previousPath = PreviousBinaryPath(agentRoot);
        var currentPath = CurrentBinaryPath(agentRoot);

        if (!File.Exists(previousPath))
        {
            return;
        }

        try
        {
            File.Move(previousPath, currentPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Rollback is already the recovery path. Failing here must not replace the original,
            // more informative error with a second one.
        }
    }

    /// <summary>Installs a binary as current with no previous version to preserve.</summary>
    /// <remarks>
    /// Used for the first install into a fresh guest, where there is nothing to roll back to but
    /// the candidate must still prove it runs before the host waits on it.
    /// </remarks>
    public async Task InstallAsync(
        string agentRoot,
        string sourceBinary,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        await StageAsync(agentRoot, sourceBinary, expectedHash, cancellationToken).ConfigureAwait(false);
        await ActivateAsync(agentRoot, cancellationToken).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover staged file is harmless; the next staging overwrites it.
        }
    }

    private static ExecutionTargetException Failed(string message, Exception? innerException) =>
        ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.AgentUpgradeFailed,
            message,
            userAction: "Retry the command. If it keeps failing, close and reopen Windows Sandbox.",
            innerException: innerException);
}
