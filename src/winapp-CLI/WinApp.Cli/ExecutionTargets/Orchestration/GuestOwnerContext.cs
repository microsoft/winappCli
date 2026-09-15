// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>Forwards explicit workflow identity without turning anonymous commands into workflows.</summary>
internal static class GuestOwnerContext
{
    public const string WorkflowVariable = UiOwnerResolver.WorkflowIdVariable;
    public const int CooperativeUiTurnsVersion = 1;
    public const int MaximumOwnerLength = UiOwnerResolver.MaxWorkflowIdLength;

    public static string? ResolveHostOwner(IReadOnlyDictionary<string, string?>? environment = null)
    {
        var workflow = environment is null
            ? Environment.GetEnvironmentVariable(WorkflowVariable)
            : environment.GetValueOrDefault(WorkflowVariable);

        if (workflow is null)
        {
            return null;
        }

        try
        {
            _ = UiOwnerResolver.ResolveWorkflow(workflow);
            return workflow;
        }
        catch (UiCoordinationException ex)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TargetAmbiguous,
                ex.Message,
                userAction: ex.RecoveryHint);
        }
    }

    /// <summary>Only a target-generation-specific hash leaves the host, never the raw workflow ID.</summary>
    public static string DeriveGuestToken(string hostOwner, string targetId, string targetEpoch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostOwner);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        var material = string.Join('\0', "winapp-guest-owner-v1", targetId, targetEpoch, hostOwner);
        return $"gt1_{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)))}";
    }

    public static string? ResolveGuestToken(string targetId, string targetEpoch) =>
        ResolveHostOwner() is { } owner ? DeriveGuestToken(owner, targetId, targetEpoch) : null;

    public static Dictionary<string, string> WithWorkflow(
        IReadOnlyDictionary<string, string>? environment,
        string? guestWorkflowToken)
    {
        var merged = environment is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase);

        if (guestWorkflowToken is null)
        {
            merged.Remove(WorkflowVariable);
        }
        else
        {
            merged[WorkflowVariable] = guestWorkflowToken;
        }
        return merged;
    }
}
