// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Controls how a directly-launched child process's standard streams are wired up.
/// </summary>
internal enum LaunchStdioMode
{
    /// <summary>
    /// The child inherits winapp's stdin/stdout/stderr so console/UI output streams inline,
    /// matching <c>dotnet run</c>. Correct for a foreground, non-JSON launch.
    /// </summary>
    Inherit,

    /// <summary>
    /// The child's standard streams are redirected away from winapp's own handles and drained to
    /// nothing. Required for <c>--detach</c> and <c>--json</c>: it stops the child from holding the
    /// parent's captured stdout pipe open (so a detached launch returns promptly instead of blocking
    /// the npm wrapper until the app exits) and keeps winapp's <c>--json</c> stdout free of app output.
    /// </summary>
    Suppress,
}

/// <summary>
/// Provides methods for launching packaged Windows applications and computing
/// MSIX package identity values.
/// </summary>
internal interface IAppLauncherService
{
    /// <summary>
    /// Launches a packaged application by its Application User Model ID (AUMID).
    /// </summary>
    /// <param name="aumid">The Application User Model ID (e.g. <c>PackageFamilyName!App</c>).</param>
    /// <param name="arguments">Optional command-line arguments to pass to the application.</param>
    /// <returns>The process ID of the launched application.</returns>
    uint LaunchByAumid(string aumid, string? arguments = null);

    /// <summary>
    /// Launches an executable directly as a child process. Used for unpackaged (project-mode)
    /// WinUI apps, where there is no MSIX identity and the app is started via its apphost
    /// <c>.exe</c> (the evaluated MSBuild <c>RunCommand</c>).
    /// </summary>
    /// <param name="exePath">Absolute path to the runnable apphost <c>.exe</c>.</param>
    /// <param name="arguments">Optional command-line arguments to forward to the application.</param>
    /// <param name="workingDirectory">Working directory for the process (typically the output dir), or <c>null</c> to inherit.</param>
    /// <param name="stdioMode">
    /// How to wire the child's standard streams. <see cref="LaunchStdioMode.Inherit"/> (default) streams
    /// output inline for a foreground launch; <see cref="LaunchStdioMode.Suppress"/> decouples the child's
    /// streams for <c>--detach</c>/<c>--json</c> so it neither holds the parent capture pipe open nor
    /// corrupts JSON stdout.
    /// </param>
    /// <returns>
    /// An owned <see cref="ILaunchedProcess"/> handle. The caller must dispose it; keeping the handle
    /// (rather than the bare PID) preserves the exit code and prevents PID reuse while waiting.
    /// </returns>
    ILaunchedProcess LaunchExecutable(string exePath, string? arguments = null, string? workingDirectory = null, LaunchStdioMode stdioMode = LaunchStdioMode.Inherit);

    /// <summary>
    /// Terminates all processes belonging to a packaged application using
    /// <c>IPackageDebugSettings.TerminateAllProcesses</c>. Falls back to killing a
    /// single process by PID when the package-level termination fails.
    /// </summary>
    /// <param name="packageFullName">The full name of the package whose processes should be terminated, or <c>null</c> to skip package-level termination.</param>
    /// <param name="processId">Fallback process ID to kill if package-level termination fails or <paramref name="packageFullName"/> is <c>null</c>.</param>
    void TerminatePackageProcesses(string? packageFullName, uint processId);

    /// <summary>
    /// Terminates all processes belonging to a packaged application using
    /// <c>IPackageDebugSettings.TerminateAllProcesses</c>, and throws when the termination call
    /// itself reports failure.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="TerminatePackageProcesses"/>, this never falls back to a best-effort PID
    /// kill and never swallows the failure: it exists for callers that must prove the app was
    /// stopped before mutating files it may still have open, and for whom silently continuing on
    /// an unverified assumption would be worse than failing loudly.
    /// </remarks>
    /// <param name="packageFullName">The full name of the package whose processes should be terminated.</param>
    /// <exception cref="System.Exception">The termination call reported failure.</exception>
    void StopPackageProcessesOrThrow(string packageFullName);

    /// <summary>
    /// Computes the package family name from a package name and publisher distinguished name.
    /// The result follows the Windows format: <c>{packageName}_{publisherId}</c>, where the
    /// publisher ID is a 13-character Crockford Base32 encoding derived from the publisher's SHA256 hash.
    /// </summary>
    /// <param name="packageName">The MSIX package name (e.g. <c>MyCompany.MyApp</c>).</param>
    /// <param name="publisher">The publisher distinguished name (e.g. <c>CN=MyCompany</c>).</param>
    /// <returns>The computed package family name.</returns>
    string ComputePackageFamilyName(string packageName, string publisher);

    /// <summary>
    /// Resolves the package full name from a package family name by querying
    /// the system's package inventory.
    /// </summary>
    /// <remarks>
    /// Tolerant: an inventory query failure is treated the same as "not installed", which is safe
    /// for read-only, best-effort callers such as reporting a name for display. It is not safe for
    /// a caller that must prove a package is absent before doing something consequential — use
    /// <see cref="GetRegisteredPackageOrThrow"/> for that.
    /// </remarks>
    /// <param name="packageFamilyName">The package family name (e.g. <c>MyApp_abc123def</c>).</param>
    /// <returns>The package full name, or <c>null</c> if the package is not installed.</returns>
    string? GetPackageFullName(string packageFamilyName);

    /// <summary>
    /// Resolves the package currently registered under a family name — its full name together with
    /// its recorded install location — distinguishing a confirmed "nothing is registered under this
    /// family" from an inventory query that itself failed.
    /// </summary>
    /// <remarks>
    /// Used by callers for whom that distinction is load-bearing: a caller about to prove a
    /// previous instance was stopped before mutating files must never treat "the inventory query
    /// failed" the same as "confirmed not installed" — the package could still be installed and
    /// running, and proceeding on that assumption is exactly the fail-open gap this exists to close.
    /// A confirmed absence (the query succeeded and found nothing) still returns <c>null</c> and is
    /// safe to treat as "nothing to stop".
    /// <para>
    /// The install location is included because a family name alone does not prove <em>which</em>
    /// deployment's registration is currently live: two deployments built from different source
    /// paths can share the same package identity, and only one of them can be genuinely registered
    /// at a time. A caller must compare this location against the one it expects before treating
    /// the result as "this is my registration" — this method itself makes no such judgement.
    /// </para>
    /// <para>
    /// The location is read without requiring the folder to still exist on disk: a package stays
    /// registered even after its files have been deleted (for example by an interrupted
    /// <c>--clean</c>), and a caller repairing that damage from a retry must still be able to prove
    /// the registration is its own before it can stop whatever is running and redeploy. A location
    /// this method could not determine is reported as <c>null</c> rather than making the whole call
    /// throw, and a caller must treat that the same as a mismatch — proof failed, not proof of
    /// absence.
    /// </para>
    /// </remarks>
    /// <param name="packageFamilyName">The package family name (e.g. <c>MyApp_abc123def</c>).</param>
    /// <returns>
    /// The currently registered package's full name and install location, or <c>null</c> when the
    /// query confirmed nothing is registered.
    /// </returns>
    /// <exception cref="System.Exception">The inventory query itself failed.</exception>
    RegisteredPackage? GetRegisteredPackageOrThrow(string packageFamilyName);
}

/// <summary>The package currently registered under a family name, as reported by the OS inventory.</summary>
/// <param name="FullName">The package's full name (name, version, architecture, and publisher hash).</param>
/// <param name="Name">The package identity name.</param>
/// <param name="Publisher">The package identity publisher.</param>
/// <param name="InstallLocation">
/// The path the package was installed from, as the package manager itself recorded it — present
/// even when nothing exists at that path any more. Null when the inventory could not report one.
/// </param>
/// <param name="IsDevelopmentMode">Whether Windows reports a development-mode registration.</param>
internal sealed record RegisteredPackage(
    string FullName,
    string Name,
    string Publisher,
    string? InstallLocation,
    bool IsDevelopmentMode);
