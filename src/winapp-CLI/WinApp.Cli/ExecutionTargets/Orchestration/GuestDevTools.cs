// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.Services;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

internal sealed record GuestDevToolsCapabilities(
    int Version, string NativeHash, string ManagedHash, string Architecture, string CliHash);

internal static class GuestDevTools
{
    internal const string NativeFileName = "WinApp.DevTools.Native.dll";
    internal const string ManagedFileName = "WinApp.DevTools.Managed.dll";
    internal static readonly string[] EngineFileNames = [NativeFileName, ManagedFileName];

    internal static async Task<GuestDevToolsCapabilities?> ReadCapabilitiesAsync(
        string? cliPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(cliPath))
        {
            return null;
        }
        var directory = Path.GetDirectoryName(cliPath)!;
        var native = Path.Combine(directory, NativeFileName);
        var managed = Path.Combine(directory, ManagedFileName);
        if (!File.Exists(native) || !File.Exists(managed))
        {
            return null;
        }
        var architecture = PeHelper.DetectPeArchitecture(native);
        if (architecture is not ("x64" or "arm64") ||
            architecture != PeHelper.DetectPeArchitecture(cliPath) ||
            PeHelper.DetectPeArchitecture(managed) is not "neutral")
        {
            return null;
        }
        return new GuestDevToolsCapabilities(2,
            await GuestAgentIdentity.ComputeBinaryHashAsync(native, cancellationToken).ConfigureAwait(false),
            await GuestAgentIdentity.ComputeBinaryHashAsync(managed, cancellationToken).ConfigureAwait(false),
            architecture,
            await GuestAgentIdentity.ComputeBinaryHashAsync(cliPath, cancellationToken).ConfigureAwait(false));
    }

    internal static void RequireMatchingEngines(
        GuestDevToolsCapabilities? expected, ExecutionTargetCapabilities actual)
    {
        if (expected is not { Version: 2 } || actual.DevTools is not { Version: 2 } received ||
            received.Architecture != expected.Architecture || actual.Architecture != expected.Architecture ||
            !string.Equals(received.NativeHash, expected.NativeHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(received.ManagedHash, expected.ManagedHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(received.CliHash, expected.CliHash, StringComparison.OrdinalIgnoreCase))
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.AgentIncompatible,
                "The execution target does not have the matching guest CLI, native and managed DevTools engines.",
                userAction: "Update the guest agent to this complete winapp build and reconnect before retrying. Updating only the host does not replace a running guest agent.");
        }
    }
}
