// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Coverage for <see cref="DevModeService"/>. The registry read and the elevated
/// process launch are behind internal seams so the enablement decision and the
/// PowerShell→reg.exe fallback ladder can be exercised without touching HKLM or
/// raising a UAC prompt.
/// </summary>
[TestClass]
public class DevModeServiceTests
{
    private static TaskContext CreateTaskContext()
    {
        var task = new GroupableTask("dev-mode", null);
        return new TaskContext(task, null, new TestConsole(), NullLogger.Instance, new Lock());
    }

    [TestMethod]
    public void IsEnabled_RegistryValueOne_ReturnsTrue()
    {
        var svc = new DevModeService { DevModeRegistryValueProvider = () => 1 };
        Assert.IsTrue(svc.IsEnabled());
    }

    [TestMethod]
    public void IsEnabled_RegistryValueMissing_ReturnsFalse()
    {
        var svc = new DevModeService { DevModeRegistryValueProvider = () => null };
        Assert.IsFalse(svc.IsEnabled());
    }

    [TestMethod]
    public void IsEnabled_RegistryValueZero_ReturnsFalse()
    {
        var svc = new DevModeService { DevModeRegistryValueProvider = () => 0 };
        Assert.IsFalse(svc.IsEnabled());
    }

    [TestMethod]
    public void IsEnabled_DefaultProvider_ReadsRealRegistryWithoutThrowing()
    {
        // Exercises the real ReadDevModeRegistryValue path. Machine state is unknown,
        // but the read must be side-effect free and stable across two calls.
        var svc = new DevModeService();
        var first = svc.IsEnabled();
        var second = svc.IsEnabled();
        Assert.AreEqual(first, second, "Reading Developer Mode state must be deterministic.");
    }

    [TestMethod]
    public async Task EnsureWin11DevModeAsync_AlreadyEnabled_ShortCircuitsWithoutElevating()
    {
        var elevatedCalls = 0;
        var svc = new DevModeService
        {
            DevModeRegistryValueProvider = () => 1,
            RunElevatedProcess = _ => { elevatedCalls++; return 0; },
        };

        var exit = await svc.EnsureWin11DevModeAsync(CreateTaskContext(), CancellationToken.None);

        Assert.AreEqual(0, exit);
        Assert.AreEqual(0, elevatedCalls, "No elevation should be attempted when already enabled.");
    }

    [TestMethod]
    public async Task EnsureWin11DevModeAsync_PowerShellSucceeds_ReturnsZeroAndUsesRunas()
    {
        var captured = new List<ProcessStartInfo>();
        var svc = new DevModeService
        {
            DevModeRegistryValueProvider = () => null,
            RunElevatedProcess = psi => { captured.Add(psi); return 0; },
        };

        var exit = await svc.EnsureWin11DevModeAsync(CreateTaskContext(), CancellationToken.None);

        Assert.AreEqual(0, exit);
        Assert.AreEqual(1, captured.Count, "Only the PowerShell attempt should run when it succeeds.");
        var psi = captured[0];
        Assert.EndsWith("powershell.exe", psi.FileName);
        Assert.AreEqual("runas", psi.Verb);

        // The script is passed as base64 UTF-16LE via -EncodedCommand, so assert on the decoded form.
        var match = Regex.Match(psi.Arguments, @"-EncodedCommand\s+(?<b64>[A-Za-z0-9+/=]+)");
        Assert.IsTrue(match.Success, $"Expected an -EncodedCommand payload. Got: {psi.Arguments}");
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(match.Groups["b64"].Value));
        Assert.Contains("AppModelUnlock", script);
    }

    [TestMethod]
    [DataRow(3010)]
    [DataRow(0)]
    public async Task EnsureWin11DevModeAsync_PowerShellReturnsSuccessCode_PropagatesIt(int code)
    {
        var svc = new DevModeService
        {
            DevModeRegistryValueProvider = () => null,
            RunElevatedProcess = _ => code,
        };

        var exit = await svc.EnsureWin11DevModeAsync(CreateTaskContext(), CancellationToken.None);

        Assert.AreEqual(code, exit);
    }

    [TestMethod]
    public async Task EnsureWin11DevModeAsync_PowerShellThrowsWin32_FallsBackToRegExe()
    {
        var captured = new List<ProcessStartInfo>();
        var svc = new DevModeService
        {
            DevModeRegistryValueProvider = () => null,
            RunElevatedProcess = psi =>
            {
                captured.Add(psi);
                if (captured.Count == 1)
                {
                    throw new Win32Exception(1223); // user cancelled UAC
                }
                return 0;
            },
        };

        var exit = await svc.EnsureWin11DevModeAsync(CreateTaskContext(), CancellationToken.None);

        Assert.AreEqual(0, exit);
        Assert.AreEqual(2, captured.Count, "PowerShell failure must trigger the reg.exe fallback.");
        Assert.EndsWith("cmd.exe", captured[1].FileName);
        Assert.Contains("reg add", captured[1].Arguments);
    }

    [TestMethod]
    public async Task EnsureWin11DevModeAsync_PowerShellThrowsGeneric_FallsBackToRegExe()
    {
        var captured = new List<ProcessStartInfo>();
        var svc = new DevModeService
        {
            DevModeRegistryValueProvider = () => null,
            RunElevatedProcess = psi =>
            {
                captured.Add(psi);
                if (captured.Count == 1)
                {
                    throw new InvalidOperationException("powershell blocked");
                }
                return 0;
            },
        };

        var exit = await svc.EnsureWin11DevModeAsync(CreateTaskContext(), CancellationToken.None);

        Assert.AreEqual(0, exit);
        Assert.AreEqual(2, captured.Count);
    }

    [TestMethod]
    public async Task EnsureWin11DevModeAsync_PowerShellNonSuccess_FallsBackAndReturnsRegExitCode()
    {
        var calls = 0;
        var svc = new DevModeService
        {
            DevModeRegistryValueProvider = () => null,
            RunElevatedProcess = _ =>
            {
                calls++;
                return calls == 1 ? 1 : 5; // PS returns non-success, reg returns 5
            },
        };

        var exit = await svc.EnsureWin11DevModeAsync(CreateTaskContext(), CancellationToken.None);

        Assert.AreEqual(5, exit, "The reg.exe exit code is surfaced when PowerShell doesn't succeed.");
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public void RunElevatedProcess_DefaultImpl_StartsProcessAndReturnsExitCode()
    {
        // Covers the real StartAndWaitForExit seam default using a benign, non-elevated
        // process (no Verb=runas), so there is no UAC prompt but the start/wait/exit-code
        // plumbing is genuinely exercised.
        var svc = new DevModeService();
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c exit 7",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var exit = svc.RunElevatedProcess(psi);

        Assert.AreEqual(7, exit);
    }
}
