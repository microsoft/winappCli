// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>
/// Resolves and forwards Cooperative UI Turns workflow identity from host to guest
/// (spec §"Owner-context forwarding").
/// </summary>
/// <remarks>
/// Cooperative UI Turns groups commands by <c>WINAPP_UI_WORKFLOW_ID</c>. In the guest every command
/// is a child of the persistent agent, so the host must forward that explicit workflow identity
/// rather than let each guest invocation mint an unrelated anonymous one-shot.
/// <para>
/// The raw workflow ID never leaves the host. It is hashed with the target identity and epoch, and
/// the opaque token is set as the guest child's ordinary <c>WINAPP_UI_WORKFLOW_ID</c>. Guest-side
/// resolution and scheduling therefore use exactly the same contract as local commands.
/// </para>
/// </remarks>
internal static class GuestOwnerContext
{
    /// <summary>The environment variable Cooperative UI Turns reads to identify a workflow.</summary>
    public const string WorkflowVariable = UiOwnerResolver.WorkflowIdVariable;

    /// <summary>Cooperative UI Turns protocol revision this build forwards for.</summary>
    /// <remarks>
    /// Reported in guest capabilities and versioned with the guest protocol, so a host and guest
    /// that disagree about owner semantics can detect it rather than silently mis-group workflows.
    /// </remarks>
    public const int CooperativeUiTurnsVersion = 1;

    /// <summary>Longest owner value Cooperative UI Turns accepts.</summary>
    /// <remarks>
    /// Matched to the local implementation's bound so a forwarded token can never be rejected by
    /// the guest for a reason a local command would not hit.
    /// </remarks>
    public const int MaximumOwnerLength = 256;

    /// <summary>
    /// Resolves the calling host workflow using the same rules as <see cref="UiOwnerResolver"/>.
    /// </summary>
    /// <remarks>
    /// An explicit value is taken exactly as given or rejected — never trimmed or truncated.
    /// Trimming would make <c>" a"</c> and <c>"a"</c> the same workflow when the guest's own
    /// resolver treats them as different, and truncating an oversized value would silently merge
    /// two distinct long owners into one. Both would break the property this exists to preserve:
    /// commands that cooperate locally cooperate in the guest, and commands that do not, do not.
    /// </para>
    /// </remarks>
    /// <exception cref="ExecutionTargetException">
    /// An explicit owner was set but is blank or longer than <see cref="MaximumOwnerLength"/>.
    /// </exception>
    public static string ResolveHostOwner(IReadOnlyDictionary<string, string?>? environment = null)
    {
        var workflowId = environment is null
            ? Environment.GetEnvironmentVariable(WorkflowVariable)
            : environment.GetValueOrDefault(WorkflowVariable);

        if (workflowId is not null)
        {
            return ValidateExplicitWorkflow(workflowId);
        }

        // No process-ancestry fallback. Two commands from one shell are not necessarily one logical
        // workflow, so an invocation without an explicit ID is always a standalone one-shot.
        return $"anonymous:{Guid.NewGuid():n}";
    }

    /// <summary>
    /// Accepts an explicit owner exactly as given, or refuses it.
    /// </summary>
    /// <remarks>
    /// Matches the local Cooperative UI Turns resolver's contract rather than being lenient. A
    /// forwarded owner that the host silently altered would group differently in the guest than the
    /// same value used locally, which is precisely the divergence forwarding exists to prevent.
    /// </remarks>
    internal static string ValidateExplicitWorkflow(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TargetAmbiguous,
                $"{WorkflowVariable} is set but empty, so the workflow cannot be determined.",
                userAction: $"Set {WorkflowVariable} to a non-empty value, or unset it to run a standalone command.");
        }

        if (value.Length > MaximumOwnerLength)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TargetAmbiguous,
                $"{WorkflowVariable} is longer than {MaximumOwnerLength} characters.",
                userAction: $"Use a shorter {WorkflowVariable} value.",
                context: new Dictionary<string, string>
                {
                    // The value itself is never reported: it is the one thing that must not reach
                    // logs, output, or telemetry.
                    ["length"] = value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["maximumLength"] = MaximumOwnerLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TargetAmbiguous,
                $"{WorkflowVariable} is not valid text: it contains an unpaired UTF-16 surrogate.",
                userAction: $"Set {WorkflowVariable} to a plain text value such as a GUID.");
        }

        return value;
    }

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Derives the opaque token carried to the guest for <paramref name="hostOwner"/>.
    /// </summary>
    /// <remarks>
    /// Hashing is what keeps the requirement that a raw explicit owner ID never reaches state,
    /// output, logs, protocol events, or telemetry — the token is all that ever leaves the host.
    /// <para>
    /// Target ID and epoch are mixed in so the same host owner produces a different token in a
    /// different or recreated environment. Without that, an owner captured before a Sandbox was
    /// recreated could group with commands in the new one, which is exactly the cross-environment
    /// grouping the spec forbids.
    /// </para>
    /// </remarks>
    public static string DeriveGuestToken(string hostOwner, string targetId, string targetEpoch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostOwner);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);

        // NUL separators keep the fields unambiguous: no field can contain one, so no combination of
        // values can be rearranged into a different combination with the same hash input.
        var material = string.Join('\u0000', "winapp-guest-owner-v1", targetId, targetEpoch, hostOwner);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));

        return $"gt1_{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    /// <summary>Resolves and derives in one step, for the common forwarding path.</summary>
    public static string ResolveGuestToken(string targetId, string targetEpoch) =>
        DeriveGuestToken(ResolveHostOwner(), targetId, targetEpoch);

    /// <summary>Builds the child environment that carries the owner token into the guest.</summary>
    /// <remarks>
    /// Merged onto any caller-supplied environment rather than replacing it, so a command that also
    /// needs its own variables does not have to know about owner forwarding.
    /// </remarks>
    public static Dictionary<string, string> WithWorkflow(
        IReadOnlyDictionary<string, string>? environment,
        string guestWorkflowToken)
    {
        var merged = environment is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase);

        merged[WorkflowVariable] = guestWorkflowToken;
        return merged;
    }
}
