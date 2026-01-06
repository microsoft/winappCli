// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;

namespace WinApp.Cli.Services;

internal class MSStoreCLIService(ILogger<MSStoreCLIService> logger) : IMSStoreCLIService
{
    public async Task EnsureMSStoreCLIAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (!IsMSStoreCLIInPath())
        {
            // MSStoreCLI is not in the path, proceed to install it via winget
            var confirm = Program.PromptYesNo("MSStoreCLI not installed - install MSStore Developer CLI with winget (user interaction may be required)? (y/N) ");
            if (!confirm)
            {
                throw new InvalidOperationException("MSStoreCLI is required but not installed.");
            }

            var args = "install --id 9P53PC5S0PHJ -e --source msstore";
            logger.LogDebug("Running winget with arguments: {Args}", args);

            ProcessStartInfo installProcessStartInfo = new()
            {
                FileName = "winget",
                Arguments = args,
                RedirectStandardInput = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = false
            };

            using Process? installProcess = Process.Start(installProcessStartInfo)
                ?? throw new InvalidOperationException("Failed to start process to install MSStoreCLI.");

            await installProcess.WaitForExitAsync(cancellationToken);
            const int NoApplicableUpgradeFound = -1978335189;
            if (installProcess.ExitCode != 0 && installProcess.ExitCode != NoApplicableUpgradeFound)
            {
                throw new InvalidOperationException("Failed to install MSStoreCLI via winget.");
            }

            logger.LogInformation("MSStoreCLI installation completed.");
        }
    }

    private bool IsMSStoreCLIInPath()
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
        {
            return false;
        }

        string[] paths = pathEnv.Split(Path.PathSeparator);
        var isAvailable = paths.Any(p =>
        {
            string fullPath = Path.Combine(p, "MSStore.exe");
            return File.Exists(fullPath);
        });
        if (isAvailable)
        {
            logger.LogDebug("MSStoreCLI is available in PATH.");
        }
        return isAvailable;
    }
}
