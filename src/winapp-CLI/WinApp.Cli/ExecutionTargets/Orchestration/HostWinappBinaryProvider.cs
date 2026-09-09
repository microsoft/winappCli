// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>Locates the running host winapp binary that is staged as the guest agent.</summary>
internal interface IHostWinappBinaryProvider
{
    /// <summary>Returns the exact host binary this command is running from.</summary>
    FileInfo GetBinary();
}

/// <summary>Production binary provider: only the current <c>winapp.exe</c> process is trusted.</summary>
internal sealed class HostWinappBinaryProvider : IHostWinappBinaryProvider
{
    /// <inheritdoc/>
    public FileInfo GetBinary()
    {
        var processPath = Environment.ProcessPath;
        if (processPath is not { Length: > 0 } ||
            !string.Equals(
                Path.GetFileName(processPath),
                GuestAgentInstaller.BinaryName,
                StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(processPath))
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.AgentUpgradeFailed,
                "winapp could not locate its own executable to stage as the Windows Sandbox agent.",
                userAction: "Run the packaged winapp executable directly, then retry.");
        }

        return new FileInfo(processPath);
    }
}
