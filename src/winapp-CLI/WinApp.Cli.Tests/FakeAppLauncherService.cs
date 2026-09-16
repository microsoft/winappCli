// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Fake app launcher service that records launch calls without actually launching applications.
/// </summary>
internal class FakeAppLauncherService : IAppLauncherService
{
    public List<(string Aumid, string? Arguments)> LaunchCalls { get; } = [];
    public List<(string ExePath, string? Arguments, string? WorkingDirectory)> LaunchExecutableCalls { get; } = [];

    /// <summary>The stdio mode passed to the most recent <see cref="LaunchExecutable"/> call.</summary>
    public LaunchStdioMode? LastLaunchStdioMode { get; private set; }
    public List<(string? PackageFullName, uint ProcessId)> TerminateCalls { get; } = [];
    public uint FakeProcessId { get; set; } = 12345;

    /// <summary>Exit code the fake launched process reports from <c>WaitForExitAsync</c>.</summary>
    public int FakeExitCode { get; set; }

    /// <summary>The most recent handle returned from <see cref="LaunchExecutable"/> (for assertions).</summary>
    public FakeLaunchedProcess? LastLaunchedProcess { get; private set; }

    public string? FakePackageFullName { get; set; } = "FakePackage_1.0.0.0_x64__fakefamily";
    public string FakePackageName { get; set; } = "Contoso.MyApp";
    public string FakePublisher { get; set; } = "CN=Contoso";
    public bool FakeIsDevelopmentMode { get; set; } = true;

    /// <summary>
    /// When set, <see cref="LaunchByAumid"/> throws this instead of returning a process ID. Used to
    /// exercise the AUMID activation-failure path (e.g. RunCommand's own catch around it, and
    /// GuestLaunchCommand's equivalent guard).
    /// </summary>
    public Exception? LaunchByAumidThrows { get; set; }

    public uint LaunchByAumid(string aumid, string? arguments = null)
    {
        if (LaunchByAumidThrows is not null)
        {
            throw LaunchByAumidThrows;
        }

        LaunchCalls.Add((aumid, arguments));
        return FakeProcessId;
    }

    public ILaunchedProcess LaunchExecutable(string exePath, string? arguments = null, string? workingDirectory = null, LaunchStdioMode stdioMode = LaunchStdioMode.Inherit)
    {
        LaunchExecutableCalls.Add((exePath, arguments, workingDirectory));
        LastLaunchStdioMode = stdioMode;
        LastLaunchedProcess = new FakeLaunchedProcess(FakeProcessId, FakeExitCode);
        return LastLaunchedProcess;
    }

    public string ComputePackageFamilyName(string packageName, string publisher)
    {
        return $"{packageName}_fakefamily";
    }

    public string? GetPackageFullName(string packageFamilyName)
    {
        return FakePackageFullName;
    }

    /// <summary>
    /// The install location <see cref="GetRegisteredPackageOrThrow"/> reports alongside
    /// <see cref="FakePackageFullName"/> — the fake's simulated ground truth for "where this
    /// family is actually registered from right now". Tests set this to whichever deployment's
    /// layout the fake should pretend genuinely owns the current registration. A path set here
    /// need not exist on disk: the real implementation reads the location the package manager
    /// itself recorded (<c>Package.InstalledPath</c>), never one this fake or the real
    /// implementation verifies against the filesystem. Left <c>null</c> to simulate an inventory
    /// that could not report a location at all.
    /// </summary>
    public string? FakeRegisteredLocation { get; set; } = string.Empty;

    /// <summary>
    /// When set, <see cref="GetRegisteredPackageOrThrow"/> throws this instead of returning a
    /// result — simulating an inventory query failure, as distinct from a query that succeeded
    /// and confirmed nothing is registered.
    /// </summary>
    public Exception? GetRegisteredPackageFailure { get; set; }

    public RegisteredPackage? GetRegisteredPackageOrThrow(string packageFamilyName)
    {
        if (GetRegisteredPackageFailure is { } failure)
        {
            throw failure;
        }

        return FakePackageFullName is null
            ? null
            : new RegisteredPackage(
                FakePackageFullName,
                FakePackageName,
                FakePublisher,
                FakeRegisteredLocation,
                FakeIsDevelopmentMode);
    }

    public void TerminatePackageProcesses(string? packageFullName, uint processId)
    {
        TerminateCalls.Add((packageFullName, processId));
    }

    /// <summary>Recorded calls to <see cref="StopPackageProcessesOrThrow"/>, in order.</summary>
    public List<string> StopPackageCalls { get; } = [];

    /// <summary>When set, <see cref="StopPackageProcessesOrThrow"/> throws this instead of recording a call.</summary>
    public Exception? StopPackageProcessesFailure { get; set; }

    public void StopPackageProcessesOrThrow(string packageFullName)
    {
        if (StopPackageProcessesFailure is { } failure)
        {
            throw failure;
        }

        StopPackageCalls.Add(packageFullName);
    }
}

/// <summary>
/// Fake <see cref="ILaunchedProcess"/> that reports a fixed PID/exit code and exits immediately,
/// so command tests can exercise the unpackaged wait/exit-code path without a real process.
/// </summary>
internal sealed class FakeLaunchedProcess(uint processId, int exitCode) : ILaunchedProcess
{
    public bool Disposed { get; private set; }
    public bool Killed { get; private set; }

    public uint ProcessId => processId;

    public int ExitCode => exitCode;

    public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Kill() => Killed = true;

    public void Dispose() => Disposed = true;
}
