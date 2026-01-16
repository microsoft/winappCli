// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Win32;
using Spectre.Console;
using System.ComponentModel;
using System.Diagnostics;
using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Services;

internal sealed class DevModeService : IDevModeService
{
    public async Task<int> EnsureWin11DevModeAsync(TaskContext taskContext, CancellationToken cancellationToken)
    {
        if (IsEnabled())
        {
            taskContext.AddDebugMessage("Developer Mode already enabled.");
            return 0;
        }

        taskContext.AddDebugMessage("Developer Mode is OFF — enabling...");

        var shouldProceed = await taskContext.PromptConfirmationAsync("Enabling Developer Mode requires administrative privileges. You may be prompted by User Account Control (UAC). Do you want to proceed?", cancellationToken);
        if (!shouldProceed)
        {
            return -1;
        }

        // 1) Prefer PowerShell elevated
        string ps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"System32\WindowsPowerShell\v1.0\powershell.exe");

        string psScript = @"
New-Item -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -Force | Out-Null
Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -Name 'AllowDevelopmentWithoutDevLicense' -Value 1
Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -Name 'AllowAllTrustedApps' -Value 1
";

        try
        {
            var exit = RunElevated(ps,
                $"-NoProfile -ExecutionPolicy Bypass -Command \"& {{ {EscapeForPSArg(psScript)} }}\"");
            if (exit == 0 || exit == 3010)
            {
                taskContext.AddDebugMessage("Developer Mode enabled (via PowerShell).");
                return exit;
            }
        }
        catch (Win32Exception) { /* user cancelled UAC, or PS blocked */ }
        catch (Exception) { /* fallback below */ }

        // 2) Fallback: cmd + reg.exe
        string cmd = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"System32\cmd.exe");

        string regCmds =
            "reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\AppModelUnlock\" /f /v AllowDevelopmentWithoutDevLicense /t REG_DWORD /d 1 & " +
            "reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\AppModelUnlock\" /f /v AllowAllTrustedApps /t REG_DWORD /d 1";

        var cmdExit = RunElevated(cmd, "/c " + regCmds);
        if (cmdExit == 0)
        {
            taskContext.AddDebugMessage("Developer Mode enabled (via reg.exe fallback).");
        }

        return cmdExit;
    }

    public bool IsEnabled()
    {
        using var hklm64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm64.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock");
        if (key == null)
        {
            return false;
        }

        var dev = (int?)key.GetValue("AllowDevelopmentWithoutDevLicense") == 1;
        var sideload = (int?)key.GetValue("AllowAllTrustedApps") == 1;
        return dev && sideload;
    }

    private static int RunElevated(string fileName, string arguments)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,   // required for Verb=runas
                Verb = "runas",           // triggers UAC
                WindowStyle = ProcessWindowStyle.Hidden
            }
        };
        p.Start();
        p.WaitForExit();
        return p.ExitCode;
    }

    private static string EscapeForPSArg(string s)
    {
        // Minimal escaping for embedding a script inside -Command "..."
        return s.Replace("\"", "`\"");
    }
}
