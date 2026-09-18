// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Windows.Management.Deployment;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Console;
using Windows.Win32.UI.Shell;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

internal class AppLauncherService(ILogger<AppLauncherService> logger) : IAppLauncherService
{
    /// <inheritdoc />
    [SupportedOSPlatform("windows8.0")]
    public uint LaunchByAumid(string aumid, string? arguments = null)
    {
        return ActivateApplicationImpl(aumid, arguments);
    }

    /// <summary>
    /// COM activation seam. Defaults to the real <see cref="IApplicationActivationManager"/>;
    /// overridable in tests so the public contract can be verified without launching an app.
    /// </summary>
    internal Func<string, string?, uint> ActivateApplicationImpl { get; set; } = DefaultActivateApplication;

    [SupportedOSPlatform("windows8.0")]
    private static uint DefaultActivateApplication(string aumid, string? arguments)
    {
        var aam = ApplicationActivationManager.CreateInstance<IApplicationActivationManager>();
        aam.ActivateApplication(aumid, arguments ?? string.Empty, ACTIVATEOPTIONS.AO_NONE, out uint pid);
        return pid;
    }

    /// <inheritdoc />
    public ILaunchedProcess LaunchExecutable(string exePath, string? arguments = null, string? workingDirectory = null, LaunchStdioMode stdioMode = LaunchStdioMode.Inherit)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
        };

        if (stdioMode == LaunchStdioMode.Suppress)
        {
            // Detach/JSON launches must NOT let the child inherit winapp's standard handles. Redirecting to
            // owned pipes gives the child fresh std streams (drained below so a chatty app can't block), but
            // Process.Start still calls CreateProcess with bInheritHandles=TRUE, so the child would ALSO
            // inherit copies of winapp's own std handles. When winapp's stdout is an npm-captured pipe those
            // copies keep it open, so `run({detach:true})` blocks until the app exits. Clearing inheritance
            // on our std handles across Start (see below) prevents that.
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardInput = true;
        }

        if (!string.IsNullOrEmpty(arguments))
        {
            psi.Arguments = arguments;
        }

        if (!string.IsNullOrEmpty(workingDirectory))
        {
            psi.WorkingDirectory = workingDirectory;
        }

        // Return the owned Process wrapped in ILaunchedProcess. The caller keeps the handle to wait
        // and read the exit code — re-attaching by PID later would race PID reuse and lose the exit
        // code once the process exits.
        // The child's redirect pipes are created inside Process.Start and stay inheritable, so it still
        // gets its own std streams; only the extra copies of winapp's real std handles are suppressed.
        var cleared = stdioMode == LaunchStdioMode.Suppress ? ClearStdHandleInheritance() : null;
        Process process;
        try
        {
            process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start process '{exePath}'.");
        }
        finally
        {
            if (cleared is not null)
            {
                RestoreStdHandleInheritance(cleared);
            }
        }

        if (stdioMode == LaunchStdioMode.Suppress)
        {
            // Discard the child's output so a chatty app can't block on a full pipe. Begin* is only
            // valid because the streams are redirected above.
            process.OutputDataReceived += static (_, _) => { };
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.StandardInput.Close();
        }

        logger.LogDebug("Launched executable {ExePath} (PID {PID}).", exePath, process.Id);
        return new LaunchedProcess(process);
    }

    private static readonly STD_HANDLE[] StdHandleIds =
        [STD_HANDLE.STD_INPUT_HANDLE, STD_HANDLE.STD_OUTPUT_HANDLE, STD_HANDLE.STD_ERROR_HANDLE];

    /// <summary>
    /// Clears <c>HANDLE_FLAG_INHERIT</c> on winapp's standard handles that currently have it set, so a
    /// child launched under bInheritHandles=TRUE (any redirect) doesn't inherit copies of them. Returns the
    /// handles that were changed for <see cref="RestoreStdHandleInheritance"/>. Process-global — call only
    /// around a launch that is serialized against other launches.
    /// </summary>
    private static List<HANDLE> ClearStdHandleInheritance()
    {
        var cleared = new List<HANDLE>(StdHandleIds.Length);
        foreach (var id in StdHandleIds)
        {
            var handle = PInvoke.GetStdHandle(id);
            if (handle.IsNull)
            {
                continue;
            }

            using var safe = new SafeFileHandle((nint)handle, ownsHandle: false);
            if (!PInvoke.GetHandleInformation(safe, out uint flags))
            {
                continue;
            }

            if (((HANDLE_FLAGS)flags & HANDLE_FLAGS.HANDLE_FLAG_INHERIT) != 0 &&
                PInvoke.SetHandleInformation(safe, (uint)HANDLE_FLAGS.HANDLE_FLAG_INHERIT, 0))
            {
                cleared.Add(handle);
            }
        }

        return cleared;
    }

    private static void RestoreStdHandleInheritance(List<HANDLE> cleared)
    {
        foreach (var handle in cleared)
        {
            using var safe = new SafeFileHandle((nint)handle, ownsHandle: false);
            PInvoke.SetHandleInformation(safe, (uint)HANDLE_FLAGS.HANDLE_FLAG_INHERIT, HANDLE_FLAGS.HANDLE_FLAG_INHERIT);
        }
    }

    /// <inheritdoc />
    public string ComputePackageFamilyName(string packageName, string publisher)
        => ComputeFamilyName(packageName, publisher);

    /// <summary>
    /// Computes a package family name without needing an instance, for callers that have a manifest but
    /// no launcher (manifest staging in <see cref="MsixService"/>).
    /// </summary>
    public static string ComputeFamilyName(string packageName, string publisher)
        => DevelopmentIdentityHelper.ComputeFamilyName(packageName, publisher);

    /// <inheritdoc />
    public string? GetPackageFullName(string packageFamilyName)
    {
        try
        {
            return FindPackageFullNameImpl(packageFamilyName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Package-manager lookup seam. Defaults to the real <see cref="PackageManager"/> query;
    /// overridable in tests to exercise the not-found and error fallbacks.
    /// </summary>
    internal Func<string, string?> FindPackageFullNameImpl { get; set; } = DefaultFindPackageFullName;

    private static string? DefaultFindPackageFullName(string packageFamilyName)
    {
        var pm = new PackageManager();
        var packages = pm.FindPackages(packageFamilyName);
        return packages.FirstOrDefault()?.Id.FullName;
    }

    /// <inheritdoc />
    public RegisteredPackage? GetRegisteredPackageOrThrow(string packageFamilyName) =>
        FindRegisteredPackageImpl(packageFamilyName);

    /// <summary>
    /// Package-manager lookup seam for the full-name-plus-install-location query. Defaults to the
    /// real <see cref="PackageManager"/> query; overridable in tests to exercise the not-found,
    /// error, and mismatched-location fallbacks.
    /// </summary>
    internal Func<string, RegisteredPackage?> FindRegisteredPackageImpl { get; set; } = DefaultFindRegisteredPackage;

    [SupportedOSPlatform("windows10.0.19041.0")]
    private static RegisteredPackage? DefaultFindRegisteredPackage(string packageFamilyName)
    {
        var pm = new PackageManager();

        // FindPackagesForUser(userSecurityId, familyName), not the parameterless-user
        // FindPackages(familyName): the latter enumerates every user's packages and requires
        // administrative rights, which a stop-before-mutate check run as an ordinary user must not
        // depend on -- it would otherwise throw UnauthorizedAccessException for every caller
        // without elevation, which the strict, non-swallowing contract here would then report as a
        // genuine query failure on every single redeploy. Scoping to the current user with an
        // empty security ID matches the pattern PackageRegistrationService already uses for the
        // same reason.
        var package = pm.FindPackagesForUser(string.Empty, packageFamilyName).FirstOrDefault();

        if (package is null)
        {
            return null;
        }

        // Package.InstalledPath, not Package.InstalledLocation.Path: the latter binds a live
        // StorageFolder and throws once the folder underneath it is gone -- which a package stays
        // registered through (an interrupted `--clean` deletes the layout's files, not the
        // registration). InstalledPath returns the path exactly as the package manager recorded it,
        // with no requirement that anything still exists there, which is what lets a caller repair
        // that damage instead of every retry failing before it can even prove what it is looking at.
        var installedPath = package.InstalledPath;

        return new RegisteredPackage(
            package.Id.FullName,
            package.Id.Name,
            package.Id.Publisher,
            string.IsNullOrWhiteSpace(installedPath) ? null : installedPath,
            package.IsDevelopmentMode);
    }

    /// <inheritdoc />
    [SupportedOSPlatform("windows8.0")]
    public void TerminatePackageProcesses(string? packageFullName, uint processId)
    {
        if (packageFullName is not null)
        {
            try
            {
                TerminateAllProcessesImpl(packageFullName);
                logger.LogDebug("Terminated all processes for package {PackageFullName}.", packageFullName);
                return;
            }
            catch (Exception ex)
            {
                logger.LogDebug("IPackageDebugSettings.TerminateAllProcesses failed: {Message}. Falling back to PID-based kill.", ex.Message);
            }
        }

        // Fallback: kill the specific process by PID
        if (processId == 0 || processId > int.MaxValue)
        {
            return;
        }

        try
        {
            KillProcessTreeByPidImpl(processId);
            logger.LogDebug("Terminated process tree for PID {PID}.", processId);
        }
        catch (ArgumentException)
        {
            // Process already exited.
        }
        catch (InvalidOperationException)
        {
            // Process already exited.
        }
    }

    /// <summary>
    /// PID-kill seam. Defaults to the real <see cref="Process.Kill(bool)"/>; overridable in
    /// tests to exercise the already-exited fallbacks (<see cref="ArgumentException"/> /
    /// <see cref="InvalidOperationException"/>) deterministically without a TOCTOU race.
    /// </summary>
    internal Action<uint> KillProcessTreeByPidImpl { get; set; } = DefaultKillProcessTreeByPid;

    private static void DefaultKillProcessTreeByPid(uint processId)
    {
        using var process = Process.GetProcessById(unchecked((int)processId));
        process.Kill(entireProcessTree: true);
    }

    /// <inheritdoc />
    [SupportedOSPlatform("windows8.0")]
    public void StopPackageProcessesOrThrow(string packageFullName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFullName);

        TerminateAllProcessesImpl(packageFullName);
    }

    /// <summary>
    /// COM package-termination seam. Defaults to the real <see cref="IPackageDebugSettings"/>;
    /// overridable in tests to exercise both the success and failure-fallback branches.
    /// </summary>
    internal Action<string> TerminateAllProcessesImpl { get; set; } = DefaultTerminateAllProcesses;

    [SupportedOSPlatform("windows8.0")]
    private static void DefaultTerminateAllProcesses(string packageFullName)
    {
        var debugSettings = PackageDebugSettings.CreateInstance<IPackageDebugSettings>();
        debugSettings.TerminateAllProcesses(packageFullName);
    }
}
