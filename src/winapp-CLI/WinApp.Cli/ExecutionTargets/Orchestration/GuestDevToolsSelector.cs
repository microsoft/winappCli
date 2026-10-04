// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

internal sealed record GuestDevToolsSelector(string? LaunchId, GuestProcessStart? Process, string? Epoch)
{
    internal static GuestDevToolsSelector Parse(string value, ExecutionTargetRef target)
    {
        // These new selectors describe the currently supported Sandbox target, not a generic
        // process on an arbitrary provider that happens to reuse an epoch or PID.
        if (target != WindowsSandboxTarget.Default)
        {
            throw Invalid("The selector does not belong to the selected Sandbox target.");
        }
        if (value.StartsWith("guest:", StringComparison.Ordinal))
        {
            var id = value["guest:".Length..];
            if (Guid.TryParseExact(id, "N", out _))
            {
                return new(id, null, null);
            }
        }
        else if (value.StartsWith("guest-process:", StringComparison.Ordinal))
        {
            var parts = value.Split(':', 4);
            if (parts.Length == 4 &&
                int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0 &&
                long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var start) &&
                start > 0 && start <= DateTime.MaxValue.Ticks &&
                !string.IsNullOrEmpty(parts[3]) && !parts[3].Any(char.IsControl))
            {
                return new(null, new(pid, start), parts[3]);
            }
        }
        throw Invalid("A qualified guest application selector is required; a bare PID or local process name is not sufficient.");
    }

    internal static string ForLaunch(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
        {
            throw Invalid("Invalid guest launch identity.");
        }
        return "guest:" + id;
    }

    internal static string ForProcess(ExecutionTargetRef target, ExecutionTargetEpoch epoch, GuestProcessStart process)
    {
        var value = string.Create(CultureInfo.InvariantCulture,
            $"guest-process:{process.ProcessId}:{process.StartTicksUtc}:{epoch.Value}");
        _ = Parse(value, target);
        return value;
    }

    internal static async Task VerifyProcessAsync(
        PreparedTarget target, string epoch, GuestProcessStart process, CancellationToken cancellationToken)
    {
        if (target.Reference != WindowsSandboxTarget.Default || target.Epoch.Value != epoch ||
            !await target.Operations.IsTrackedProcessRunningAsync(
                process.ProcessId, process.StartTicksUtc, cancellationToken).ConfigureAwait(false))
        {
            throw Invalid("The guest application or Sandbox incarnation changed. The old selector and its element handles are no longer valid.");
        }
    }

    private static InvalidOperationException Invalid(string reason) =>
        new(reason + " Run 'winapp devtools list --on sandbox' and copy the current appSelector.");
}
