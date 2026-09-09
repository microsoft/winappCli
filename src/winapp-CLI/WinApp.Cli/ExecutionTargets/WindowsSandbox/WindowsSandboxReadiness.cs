// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

/// <summary>How far this host is from being able to run a Windows Sandbox.</summary>
internal enum WindowsSandboxSetupState
{
    /// <summary>Not Windows, so no amount of setup helps.</summary>
    NotWindows,

    /// <summary>
    /// The optional feature's payload is not on disk, so the feature itself has to be enabled.
    /// </summary>
    FeaturePayloadMissing,

    /// <summary>
    /// The feature payload is present but the Store-delivered client is not usable yet — it is
    /// still being downloaded, still being serviced, or has never been initialized on this account.
    /// </summary>
    ClientNotInitialized,

    /// <summary>A trusted <c>wsb.exe</c> answered <c>--version</c>.</summary>
    Ready,
}

/// <summary>
/// Non-elevated, observed facts about Windows Sandbox on this host.
/// </summary>
/// <remarks>
/// Every member records something that was actually measured. Nothing here is inferred: a cause
/// winapp cannot observe — enterprise policy, an offline Store, a pending reboot — is never asserted
/// from the absence of something else, because telling a user to enable a feature that is already
/// enabled is worse than telling them nothing.
/// </remarks>
internal sealed record WindowsSandboxHostFacts
{
    /// <summary>Whether this host is Windows at all.</summary>
    public required bool IsWindows { get; init; }

    /// <summary>
    /// Whether <c>%SystemRoot%\System32\WindowsSandbox.exe</c> exists.
    /// </summary>
    /// <remarks>
    /// This is the optional feature's own payload, written by servicing when
    /// <c>Containers-DisposableClientVM</c> is enabled and removed when it is disabled. It is the
    /// one feature-state signal available without elevation: <c>dism /Online /Get-FeatureInfo</c>
    /// and <c>Get-WindowsOptionalFeature -Online</c> both fail with <c>ERROR_ELEVATION_REQUIRED</c>
    /// for an ordinary user, so neither can be used by a check that must work unelevated.
    /// </remarks>
    public required bool FeaturePayloadPresent { get; init; }

    /// <summary>Whether the Store-delivered Sandbox package is registered for this user.</summary>
    public required bool PackageRegistered { get; init; }

    /// <summary>
    /// The package's reported status, or null when it could not be observed.
    /// </summary>
    /// <remarks>
    /// Reported verbatim in setup failures so a user who is told to wait can see whether Windows
    /// said <c>Servicing</c>, <c>PackageOffline</c>, or something else entirely.
    /// </remarks>
    public string? PackageStatus { get; init; }

    /// <summary>Whether a <c>wsb.exe</c> execution alias was found.</summary>
    /// <remarks>
    /// Presence alone proves nothing. The alias is a zero-byte <c>APPEXECLINK</c> reparse point, so
    /// <see cref="File.Exists(string)"/> succeeds for one whose package cannot launch — which is
    /// exactly the state a first run on a freshly enabled feature is in.
    /// </remarks>
    public required bool AliasPresent { get; init; }

    /// <summary>Absolute path of the alias, when one was found.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// The version <c>wsb.exe --version</c> reported, or null when it did not answer.
    /// </summary>
    /// <remarks>
    /// The single fact that proves readiness. Everything else is a reason to keep setting up.
    /// </remarks>
    public string? Version { get; init; }

    /// <summary>Diagnostic detail from the last probe, for setup failure context.</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// Whether the package is either healthy or simply not observable.
    /// </summary>
    /// <remarks>
    /// A status winapp could not read is not evidence of a problem, so it does not count against
    /// the host. A status Windows actively reported as not OK does: <c>Servicing</c> means the
    /// package is being replaced underneath any command that starts now, and <c>Disabled</c> or
    /// <c>NeedsRemediation</c> mean it may stop working part-way through.
    /// </remarks>
    public bool IsPackageHealthy =>
        PackageStatus is null || string.Equals(PackageStatus, "Ok", StringComparison.Ordinal);

    /// <summary>Classifies these facts into the state the setup runner acts on.</summary>
    public WindowsSandboxSetupState State => WindowsSandboxReadiness.Classify(this);
}

/// <summary>
/// Decides what — if anything — winapp still has to do before Windows Sandbox can be used.
/// </summary>
/// <remarks>
/// Kept as a pure function over observed facts so the decision itself can be exercised exhaustively
/// without a host that is in any particular state. The ordering is the contract, and one case is
/// load-bearing: a host whose feature payload is present but whose client has not initialized is
/// <see cref="WindowsSandboxSetupState.ClientNotInitialized"/> and must never be told to enable an
/// optional feature that is already enabled.
/// </remarks>
internal static class WindowsSandboxReadiness
{
    /// <summary>Package family name of the Store-delivered Windows Sandbox client.</summary>
    internal const string PackageFamilyName = "MicrosoftWindows.WindowsSandbox_cw5n1h2txyewy";

    /// <summary>Optional feature that delivers the Sandbox payload.</summary>
    internal const string FeatureName = "Containers-DisposableClientVM";

    /// <summary>The feature payload, and the client bootstrapper, in System32.</summary>
    internal const string PayloadExecutableName = "WindowsSandbox.exe";

    /// <summary>Classifies observed host facts.</summary>
    public static WindowsSandboxSetupState Classify(WindowsSandboxHostFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (!facts.IsWindows)
        {
            return WindowsSandboxSetupState.NotWindows;
        }

        // A version reply is the only proof that the client works. It is deliberately checked before
        // the payload: a host can be ready through a client winapp did not watch arrive, and
        // re-running setup against a working Sandbox would start one for no reason.
        //
        // Package health is consulted alongside it rather than ignored. A package Windows reports as
        // Disabled, Servicing, or otherwise not OK is one that can stop working mid-command or is
        // being replaced underneath us, so a version reply from it is not a durable "ready" -- and
        // an unhealthy package with a working alias is exactly the state a mid-update machine is in.
        // A package winapp could not observe at all (PackageStatus null) is not held against it.
        if (!string.IsNullOrWhiteSpace(facts.Version) && facts.IsPackageHealthy)
        {
            return WindowsSandboxSetupState.Ready;
        }

        // The payload is on disk, so the feature is enabled and enabling it again would do nothing.
        // Whatever is missing is on the Store-client side -- not yet downloaded, mid-servicing, or
        // never initialized for this user -- which is a different problem with a different fix.
        if (facts.FeaturePayloadPresent)
        {
            return WindowsSandboxSetupState.ClientNotInitialized;
        }

        // A registered package with no payload is still the client-side story: servicing has staged
        // the package but the feature payload it drives is not there, and enabling the feature is
        // what puts it there. Fall through.
        return WindowsSandboxSetupState.FeaturePayloadMissing;
    }
}
