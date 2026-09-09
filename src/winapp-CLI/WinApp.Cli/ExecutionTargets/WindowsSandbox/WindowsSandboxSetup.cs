// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

/// <summary>Brings a host to the point where Windows Sandbox can actually be used.</summary>
internal interface IWindowsSandboxSetup
{
    /// <summary>
    /// Reports what this host currently looks like, changing nothing.
    /// </summary>
    /// <remarks>
    /// The read-only half of this interface, and the one any future discovery, status, or
    /// diagnostics surface must use. It never enables a Windows feature, never starts a process,
    /// and never waits.
    /// </remarks>
    Task<WindowsSandboxHostFacts> InspectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns once <c>wsb.exe</c> answers, performing whatever setup is still outstanding.
    /// </summary>
    /// <remarks>
    /// <b>This mutates the machine.</b> It can enable a Windows optional feature behind a UAC
    /// prompt and start the OS client installer, and it can block for minutes. It is the only
    /// member here that does any of that, which is what makes "did winapp change my machine?"
    /// answerable by looking at the call sites of one method. Calling it is an explicit act; a
    /// caller that only wants to know the state calls <see cref="InspectAsync"/>.
    /// </remarks>
    /// <exception cref="ExecutionTargetException">
    /// Setup needs something only Windows or the user can supply: elevation, a restart, or a
    /// working Store connection.
    /// </exception>
    Task<WindowsSandboxHostFacts> EnsureReadyAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Performs Windows Sandbox prerequisite setup automatically, because <c>--on sandbox</c> asked for it.
/// </summary>
/// <remarks>
/// <para>
/// Passing <c>--on sandbox</c> is explicit consent to do everything feasible to make Windows Sandbox
/// usable in one command. So this enables the optional feature and initializes the Store-delivered
/// client without a second flag, a subcommand, or a prompt. Only the things winapp genuinely cannot
/// do are handed back: a denied or unavailable UAC elevation, a restart, an unsupported edition, and
/// a servicing or Store failure.
/// </para>
/// <para>
/// Setup is visible. Both phases can take minutes, and Windows shows its own update UI while the
/// client initializes — a window can appear and take focus, which winapp cannot prevent because it
/// belongs to the OS bootstrapper, not to winapp. Progress therefore goes to standard error as each
/// phase begins, leaving <c>--json</c> stdout untouched.
/// </para>
/// <para>
/// Every phase is resumable. Nothing is torn down on failure and nothing is relaunched while it is
/// already running, so a timed-out first attempt is continued by the next command rather than
/// restarted by it.
/// </para>
/// </remarks>
internal sealed class WindowsSandboxSetup(
    IWindowsSandboxHostProbe probe,
    IWindowsFeatureEnabler featureEnabler,
    ITargetProgress? progress = null) : IWindowsSandboxSetup
{
    private readonly ITargetProgress _progress = progress ?? NullTargetProgress.Instance;

    /// <summary>How long to wait for the Store-delivered client to become usable.</summary>
    /// <remarks>
    /// Generous because the work behind it is a download over whatever connection the machine has,
    /// but still bounded: a command that never returns is worse than one that says what it was
    /// waiting for and that retrying resumes.
    /// </remarks>
    internal static readonly TimeSpan ClientInitializationTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Gap between readiness polls while the client initializes.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    /// <summary>How often to repeat the "still working" progress line.</summary>
    internal static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(30);

    /// <summary>Announced before enabling the optional feature, which raises a UAC prompt.</summary>
    internal const string EnablingFeatureMessage =
        "Enabling the Windows Sandbox feature (Windows will ask for permission)...";

    /// <summary>Announced before the Store-delivered client is initialized.</summary>
    internal const string InstallingClientMessage = "Installing/updating the Windows Sandbox client...";

    /// <summary>Repeated while that installation is still running.</summary>
    internal const string StillInstallingClientMessage =
        "Still installing/updating the Windows Sandbox client...";

    /// <summary>Delay seam, so timeout and cancellation are exercised without real waiting.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <summary>Clock seam, so the bound is exercised without real time passing.</summary>
    internal Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Bootstrapper-launch seam.</summary>
    internal Action<string> LaunchClientBootstrapper { get; set; } = StartClientBootstrapper;

    /// <summary>Whether the OS client bootstrapper is already running.</summary>
    internal Func<bool> IsBootstrapperRunning { get; set; } = DefaultIsBootstrapperRunning;

    /// <summary>
    /// Whether this build of Windows ships the Sandbox command line winapp drives.
    /// </summary>
    /// <remarks>
    /// <c>wsb.exe</c> arrived in Windows 11 24H2 (build 26100). Earlier builds can still have the
    /// Sandbox <em>feature</em> and its System32 payload, so the payload signal alone would send
    /// such a host into a client installation that can never finish. Seamed so the refusal can be
    /// exercised from any machine.
    /// </remarks>
    internal Func<bool> SupportsSandboxCli { get; set; } =
        () => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100);

    /// <inheritdoc/>
    public Task<WindowsSandboxHostFacts> InspectAsync(CancellationToken cancellationToken) =>
        probe.ProbeAsync(cancellationToken);

    /// <inheritdoc/>
    public async Task<WindowsSandboxHostFacts> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        var facts = await probe.ProbeAsync(cancellationToken).ConfigureAwait(false);

        if (facts.State is WindowsSandboxSetupState.Ready)
        {
            return facts;
        }

        if (facts.State is WindowsSandboxSetupState.NotWindows)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.Unsupported,
                "Windows Sandbox execution requires Windows.",
                userAction: "Run this command on a Windows 11 machine.");
        }

        // Checked only after readiness, never before it: a host where `wsb` answers is usable
        // whatever its build number says, and refusing that would break a working machine over a
        // version check. It matters here because the alternative is a ten-minute wait for a client
        // this build of Windows cannot deliver.
        if (!SupportsSandboxCli())
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.Unsupported,
                "This version of Windows does not provide the Windows Sandbox command line.",
                userAction: "Run this command on Windows 11 24H2 or newer.",
                context: new Dictionary<string, string>
                {
                    ["osVersion"] = Environment.OSVersion.Version.ToString(),
                },
                example: "winapp run . --on sandbox");
        }

        if (facts.State is WindowsSandboxSetupState.FeaturePayloadMissing)
        {
            facts = await EnableFeatureAsync(facts, cancellationToken).ConfigureAwait(false);

            if (facts.State is WindowsSandboxSetupState.Ready)
            {
                return facts;
            }
        }

        // A client that answers but whose package Windows reports as unhealthy is being updated,
        // not missing. Launching the installer and waiting ten minutes for it would be the wrong
        // remedy for a machine that is already most of the way there, so this says what is happening
        // and returns immediately.
        if (!string.IsNullOrWhiteSpace(facts.Version) && !facts.IsPackageHealthy)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.SetupIncomplete,
                "Windows is still updating the Windows Sandbox client.",
                userAction: "Wait for Windows to finish, then run the command again.",
                context: Detail(facts, facts.Detail),
                example: "winapp run . --on sandbox");
        }

        return await InitializeClientAsync(facts, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Enables the optional feature, then re-measures rather than assuming it worked.</summary>
    private async Task<WindowsSandboxHostFacts> EnableFeatureAsync(
        WindowsSandboxHostFacts facts,
        CancellationToken cancellationToken)
    {
        _progress.Report(EnablingFeatureMessage);

        var result = await featureEnabler
            .EnableAsync(WindowsSandboxReadiness.FeatureName, cancellationToken)
            .ConfigureAwait(false);

        switch (result.Outcome)
        {
            case FeatureEnableOutcome.ElevationUnavailable:
                throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.SetupRequiresElevation,
                    "Windows Sandbox is not installed, and enabling it needs administrator permission "
                    + "that was declined or could not be requested.",
                    userAction:
                        "Run the command below from an elevated terminal, restart if Windows asks, then retry.",
                    context: Detail(facts, result.Detail),
                    nextCommand: new ExecutionTargetNextCommand
                    {
                        Command =
                            $"dism.exe /Online /Enable-Feature /FeatureName:{WindowsSandboxReadiness.FeatureName} "
                            + "/All /NoRestart",

                        // Enabling a Windows feature changes machine configuration and normally
                        // needs a restart, so running it stays the user's decision.
                        Advisory = true,
                    },
                    example: "winapp run . --on sandbox");

            case FeatureEnableOutcome.RestartRequired:
                throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.SetupRequiresRestart,
                    "The Windows Sandbox feature was enabled and Windows needs a restart to finish.",
                    userAction: "Restart Windows, then run the command again.",
                    context: Detail(facts, result.Detail),
                    example: "winapp run . --on sandbox");

            case FeatureEnableOutcome.Failed:
                throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.SetupFailed,
                    "Windows could not enable the Windows Sandbox feature.",
                    userAction:
                        "Check that this edition supports Windows Sandbox, that hardware virtualization is "
                        + "enabled in firmware, and that policy allows optional features, then retry.",
                    context: Detail(facts, result.Detail),
                    example: "winapp run . --on sandbox");
        }

        // Enabled without a restart. Re-measured rather than assumed: the client still has to
        // initialize, and only a fresh probe can say whether it already has.
        facts = await probe.ProbeAsync(cancellationToken).ConfigureAwait(false);

        // Servicing reported success, yet the payload it writes is still not there. winapp cannot
        // see why -- a restart Windows did not ask for, or servicing still finishing -- so it says
        // what it measured instead of naming a cause it did not observe.
        if (facts.State is WindowsSandboxSetupState.FeaturePayloadMissing)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.SetupIncomplete,
                "Windows reported the Windows Sandbox feature as enabled, but its files are not in place yet.",
                userAction:
                    "Windows may still be finishing. Wait, run the command again, and restart Windows if it "
                    + "keeps reporting this.",
                context: Detail(facts, result.Detail),
                example: "winapp run . --on sandbox");
        }

        return facts;
    }

    /// <summary>
    /// Makes the Store-delivered client initialize, then waits for it to become usable.
    /// </summary>
    /// <remarks>
    /// Launching <c>%SystemRoot%\System32\WindowsSandbox.exe</c> is what triggers Windows to fetch
    /// and register the client; it is the same thing that happens when a user starts Windows Sandbox
    /// from the Start menu, and it shows the OS's own "Downloading and installing updates" UI while
    /// it runs. winapp does not wait for that process to exit — it can stay up as the Sandbox
    /// window itself — and instead waits for the outcome that matters: a <c>wsb.exe</c> that answers.
    /// </remarks>
    private async Task<WindowsSandboxHostFacts> InitializeClientAsync(
        WindowsSandboxHostFacts facts,
        CancellationToken cancellationToken)
    {
        _progress.Report(InstallingClientMessage);

        // Not relaunched when one is already up: repeating this on every retry would stack OS
        // bootstrapper processes and update windows on top of an installation that is progressing.
        if (!IsBootstrapperRunning())
        {
            try
            {
                LaunchClientBootstrapper(WindowsSandboxHostProbe.PayloadExecutablePath());
            }
            catch (Exception ex) when (
                ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException
                  or ExecutionTargetException)
            {
                throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.SetupFailed,
                    "Windows Sandbox is installed but its client could not be started to finish setting up.",
                    userAction: "Start Windows Sandbox once from the Start menu, then retry.",
                    context: Detail(facts, ex.Message),
                    example: "winapp run . --on sandbox",
                    innerException: ex);
            }
        }

        var deadline = UtcNow() + ClientInitializationTimeout;
        var nextProgress = UtcNow() + ProgressInterval;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await Delay(PollInterval, cancellationToken).ConfigureAwait(false);

            facts = await probe.ProbeAsync(cancellationToken).ConfigureAwait(false);

            if (facts.State is WindowsSandboxSetupState.Ready)
            {
                return facts;
            }

            var now = UtcNow();

            if (now >= deadline)
            {
                throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.SetupIncomplete,
                    "The Windows Sandbox client is still being installed or updated.",
                    userAction:
                        "Windows may still be working in the background. Wait for it to finish, then run the "
                        + "command again — retrying continues the installation rather than restarting it.",
                    context: Detail(facts, facts.Detail),
                    example: "winapp run . --on sandbox");
            }

            if (now >= nextProgress)
            {
                _progress.Report(StillInstallingClientMessage);
                nextProgress = now + ProgressInterval;
            }
        }
    }

    /// <summary>The observed state, for a failure a user has to act on.</summary>
    /// <remarks>
    /// Reports what was measured rather than what winapp concluded, so someone reading a timeout can
    /// see whether Windows said the package was servicing, offline, or simply absent.
    /// </remarks>
    private static Dictionary<string, string> Detail(WindowsSandboxHostFacts facts, string? detail)
    {
        var context = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["setupState"] = facts.State.ToString(),
            ["featurePayloadPresent"] = facts.FeaturePayloadPresent ? "true" : "false",
            ["packageRegistered"] = facts.PackageRegistered ? "true" : "false",
            ["aliasPresent"] = facts.AliasPresent ? "true" : "false",
            ["wsbVersion"] = facts.Version ?? "none",
        };

        if (!string.IsNullOrWhiteSpace(facts.PackageStatus))
        {
            context["packageStatus"] = facts.PackageStatus;
        }

        if (!string.IsNullOrWhiteSpace(detail))
        {
            context["detail"] = detail;
        }

        return context;
    }

    /// <summary>Starts the OS bootstrapper without waiting for it.</summary>
    /// <remarks>
    /// Absolute, trusted, and taken from the system directory, so nothing on PATH or in the current
    /// directory can stand in for it. Deliberately not a hidden window: this is the OS's own setup
    /// UI, and hiding it would leave the user watching a silent terminal while Windows waited on a
    /// dialog they could not see.
    /// </remarks>
    private static void StartClientBootstrapper(string executablePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = true,
            CreateNoWindow = false,
        };

        // This child outlives the command that started it, so it must not hold a duplicate of a
        // caller's captured stdout: a caller piping winapp would otherwise not see end of stream
        // until Windows Sandbox itself closed.
        using (Helpers.StandardHandleInheritance.Suppress())
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows did not start the Sandbox client.");
        }
    }

    /// <summary>Whether an OS Sandbox bootstrapper process is already running.</summary>
    private static bool DefaultIsBootstrapperRunning()
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(WindowsSandboxReadiness.PayloadExecutableName);
            var running = Process.GetProcessesByName(name);

            foreach (var process in running)
            {
                process.Dispose();
            }

            return running.Length > 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                     or NotSupportedException)
        {
            // If the process list cannot be read, launching is still the safe answer: a duplicate
            // bootstrapper is recoverable, whereas never launching one wedges setup permanently.
            return false;
        }
    }
}
