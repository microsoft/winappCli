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
    public Exception? LaunchException { get; set; }

    /// <summary>Exit code the fake launched process reports from <c>WaitForExitAsync</c>.</summary>
    public int FakeExitCode { get; set; }

    /// <summary>The most recent handle returned from <see cref="LaunchExecutable"/> (for assertions).</summary>
    public FakeLaunchedProcess? LastLaunchedProcess { get; private set; }

    public string? FakePackageFullName { get; set; } = "FakePackage_1.0.0.0_x64__fakefamily";

    public uint LaunchByAumid(string aumid, string? arguments = null)
    {
        LaunchCalls.Add((aumid, arguments));
        if (LaunchException is not null)
        {
            throw LaunchException;
        }
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

    public void TerminatePackageProcesses(string? packageFullName, uint processId)
    {
        TerminateCalls.Add((packageFullName, processId));
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
