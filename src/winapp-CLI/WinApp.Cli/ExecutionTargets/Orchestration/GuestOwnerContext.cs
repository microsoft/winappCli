// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>
/// Resolves and forwards Cooperative UI Turns owner identity from host to guest
/// (spec §"Owner-context forwarding").
/// </summary>
/// <remarks>
/// Cooperative UI Turns groups commands by their logical owner, and its default owner is derived
/// from the immediate parent process. In the guest that parent is always the one persistent agent,
/// so without forwarding every host workflow would collapse into a single owner and commands that
/// must queue against each other would instead be treated as cooperating.
/// <para>
/// The host therefore resolves its own owner using the same precedence Cooperative UI Turns uses
/// locally, hashes it into an opaque token, and carries the token in the operation envelope. The
/// agent sets the ordinary <c>WINAPP_UI_OWNER_ID</c> variable for the child, so guest-side owner
/// resolution and scheduling stay completely unchanged.
/// </para>
/// <para>
/// This deliberately implements only the forwarding contract, not Cooperative UI Turns itself. The
/// variable name, its length bound, and the precedence order are the contract; when the feature
/// lands, the local resolution below is replaced by its resolver and the token derivation is
/// unaffected.
/// </para>
/// </remarks>
internal static class GuestOwnerContext
{
    /// <summary>The environment variable Cooperative UI Turns reads to identify a workflow owner.</summary>
    public const string OwnerVariable = "WINAPP_UI_OWNER_ID";

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
    /// Resolves the calling host workflow's owner using Cooperative UI Turns precedence.
    /// </summary>
    /// <remarks>
    /// Precedence is explicit value, then immediate parent process identity, then a unique
    /// one-command owner. The parent's start time is included because a process ID alone is reused
    /// by Windows, and a reused ID would silently join an unrelated later workflow to this one.
    /// <para>
    /// An explicit value is taken exactly as given or rejected — never trimmed, never truncated.
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
        var explicitOwner = environment is null
            ? Environment.GetEnvironmentVariable(OwnerVariable)
            : environment.GetValueOrDefault(OwnerVariable);

        if (explicitOwner is not null)
        {
            return ValidateExplicitOwner(explicitOwner);
        }

        if (TryDescribeParent(out var parent))
        {
            return parent;
        }

        // No explicit owner and no observable parent: this invocation owns nothing but itself, so
        // it gets an identity no other command can share.
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
    internal static string ValidateExplicitOwner(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TargetAmbiguous,
                $"{OwnerVariable} is set but empty, so the workflow owner cannot be determined.",
                userAction: $"Set {OwnerVariable} to a non-empty value, or unset it to use the default owner.");
        }

        if (value.Length > MaximumOwnerLength)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TargetAmbiguous,
                $"{OwnerVariable} is longer than {MaximumOwnerLength} characters.",
                userAction: $"Use a shorter {OwnerVariable} value.",
                context: new Dictionary<string, string>
                {
                    // The value itself is never reported: it is the one thing that must not reach
                    // logs, output, or telemetry.
                    ["length"] = value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["maximumLength"] = MaximumOwnerLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        return value;
    }

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
    public static Dictionary<string, string> WithOwner(
        IReadOnlyDictionary<string, string>? environment,
        string guestOwnerToken)
    {
        var merged = environment is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase);

        merged[OwnerVariable] = guestOwnerToken;
        return merged;
    }

    /// <summary>Describes the immediate parent process as an owner value.</summary>
    private static bool TryDescribeParent(out string owner)
    {
        owner = string.Empty;

        try
        {
            using var current = Process.GetCurrentProcess();
            var parentId = ParentProcessId.TryGet(current.Id);
            if (parentId is not { } id)
            {
                return false;
            }

            using var parent = Process.GetProcessById(id);
            owner = string.Create(
                CultureInfo.InvariantCulture,
                $"parent:{id}:{parent.StartTime.ToUniversalTime().Ticks}");
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or SystemException)
        {
            // The parent already exited, or its start time is not readable. Either way there is no
            // stable identity to group by, so the caller falls back to an anonymous owner.
            return false;
        }
    }
}
