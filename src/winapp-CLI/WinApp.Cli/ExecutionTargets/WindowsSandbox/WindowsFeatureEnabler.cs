// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

/// <summary>How an attempt to enable the optional feature ended.</summary>
internal enum FeatureEnableOutcome
{
    /// <summary>The feature is enabled and no restart is needed.</summary>
    Enabled,

    /// <summary>The feature is enabled but Windows needs a restart to finish.</summary>
    RestartRequired,

    /// <summary>Elevation was denied, or no interactive session could ask for it.</summary>
    ElevationUnavailable,

    /// <summary>Servicing refused: unsupported edition, policy, or a component-store failure.</summary>
    Failed,
}

/// <summary>Result of one enable attempt.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="ExitCode">The servicing exit code, when one was produced.</param>
/// <param name="Detail">Short diagnostic detail for the failure envelope.</param>
internal sealed record FeatureEnableResult(FeatureEnableOutcome Outcome, int? ExitCode = null, string? Detail = null);

/// <summary>Enables a Windows optional feature through the standard servicing tool.</summary>
internal interface IWindowsFeatureEnabler
{
    /// <summary>
    /// Enables <paramref name="featureName"/>, prompting for elevation, and never restarts.
    /// </summary>
    Task<FeatureEnableResult> EnableAsync(string featureName, CancellationToken cancellationToken);
}

/// <summary>
/// Runs <c>dism.exe /Online /Enable-Feature</c> elevated.
/// </summary>
/// <remarks>
/// <para>
/// This is the one place winapp changes machine configuration, and <c>--on sandbox</c> is what
/// authorises it: a user who asked for Sandbox execution has asked for the prerequisite that makes
/// it possible. It is still bounded to a single fixed operation — one absolute trusted binary from
/// the system directory, a fixed verb, and one caller-supplied feature name that is validated as a
/// plain name before it is used.
/// </para>
/// <para>
/// <c>/NoRestart</c> is not optional. Enabling this feature normally requires a reboot, and a tool
/// that restarted the machine on its own could discard unsaved work anywhere on the desktop. The
/// restart is reported and left to the user.
/// </para>
/// <para>
/// Elevation goes through <c>ShellExecuteEx</c> with the <c>runas</c> verb, which is what raises the
/// consent dialog. That rules out capturing the child's output — <c>UseShellExecute</c> and
/// redirection are mutually exclusive — so the exit code is the whole diagnosis, which is why the
/// exit codes below are mapped explicitly rather than lumped into a generic failure.
/// </para>
/// </remarks>
internal sealed class WindowsFeatureEnabler : IWindowsFeatureEnabler
{
    /// <summary>Servicing succeeded and the change is fully applied.</summary>
    internal const int ExitSuccess = 0;

    /// <summary>Servicing succeeded; Windows needs a restart to finish (ERROR_SUCCESS_REBOOT_REQUIRED).</summary>
    internal const int ExitRestartRequired = 3010;

    /// <summary>ShellExecute reports this when the user dismisses the consent dialog (ERROR_CANCELLED).</summary>
    internal const int ErrorCancelled = 1223;

    /// <summary>ShellExecute reports this when elevation is required but cannot be requested (ERROR_ELEVATION_REQUIRED).</summary>
    internal const int ErrorElevationRequired = 740;

    /// <summary>Process-launch seam. The default performs the real elevated launch.</summary>
    internal Func<ProcessStartInfo, CancellationToken, Task<int>> Launcher { get; set; } = RunElevatedAsync;

    /// <inheritdoc/>
    public async Task<FeatureEnableResult> EnableAsync(string featureName, CancellationToken cancellationToken)
    {
        // Validated as a plain name before it reaches a command line, so no value can ever add an
        // argument to a privileged invocation. The only caller passes a constant; the check exists
        // so that stays true if a future one does not.
        TargetPathSafety.EnsureSafeSegment(featureName);

        var startInfo = new ProcessStartInfo
        {
            FileName = TargetPathSafety.CombineInsideRoot(Environment.SystemDirectory, "dism.exe"),

            // Required for the runas verb, and therefore for the consent prompt. It also means the
            // child's output cannot be captured, which is why the exit code carries the diagnosis.
            UseShellExecute = true,
            Verb = "runas",
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        foreach (var argument in (string[])
        [
            "/Online",
            "/Enable-Feature",
            $"/FeatureName:{featureName}",
            "/All",

            // Never restart the machine on the user's behalf.
            "/NoRestart",
            "/Quiet",
        ])
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            var exitCode = await Launcher(startInfo, cancellationToken).ConfigureAwait(false);

            return exitCode switch
            {
                ExitSuccess => new FeatureEnableResult(FeatureEnableOutcome.Enabled, exitCode),
                ExitRestartRequired => new FeatureEnableResult(FeatureEnableOutcome.RestartRequired, exitCode),
                ErrorElevationRequired => new FeatureEnableResult(
                    FeatureEnableOutcome.ElevationUnavailable, exitCode),
                _ => new FeatureEnableResult(
                    FeatureEnableOutcome.Failed,
                    exitCode,
                    $"dism.exe exited with {exitCode}"),
            };
        }
        catch (System.ComponentModel.Win32Exception ex) when (
            ex.NativeErrorCode is ErrorCancelled or ErrorElevationRequired)
        {
            // The consent dialog was dismissed, or there was no interactive session to show one in.
            // Both mean the same thing to the caller: winapp may not make this change.
            return new FeatureEnableResult(FeatureEnableOutcome.ElevationUnavailable, null, ex.Message);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                     or IOException or Abstractions.ExecutionTargetException)
        {
            return new FeatureEnableResult(FeatureEnableOutcome.Failed, null, ex.Message);
        }
    }

    private static async Task<int> RunElevatedAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        // ShellExecute does not inherit handles, so an elevated child cannot hold a caller's
        // captured stdout open. Suppressed anyway, for the same reason every other long-lived child
        // launch in this backend is: the guarantee should not depend on a flag staying set.
        using (Helpers.StandardHandleInheritance.Suppress())
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows did not start the servicing tool.");

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
    }
}
