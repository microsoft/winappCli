// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>What the host should do about the guest agent it found.</summary>
internal enum GuestAgentAction
{
    /// <summary>Nothing to do; the running agent is the right one.</summary>
    Reuse,

    /// <summary>The host is newer. Stage, self-test, and activate its guest binary.</summary>
    StageAndActivate,

    /// <summary>
    /// No usable agent is present at all, so the host's binary is installed rather than upgraded.
    /// </summary>
    Install,

    /// <summary>
    /// The guest is newer and speaks no protocol revision this host does. Upgrading the host is the
    /// only correct fix; downgrading the guest is never an option.
    /// </summary>
    FailIncompatible,
}

/// <summary>The decision plus why it was reached.</summary>
/// <param name="Action">What to do.</param>
/// <param name="Reason">Human-readable justification, used in diagnostics and progress output.</param>
internal sealed record GuestAgentPlan(GuestAgentAction Action, string Reason)
{
    /// <summary>True when the plan requires mutating the guest, and therefore the mutation lock.</summary>
    public bool RequiresMutation => Action is GuestAgentAction.StageAndActivate or GuestAgentAction.Install;
}

/// <summary>
/// Decides whether a running guest agent can be reused, must be replaced, or is incompatible
/// (spec §"Agent versioning and upgrades").
/// </summary>
/// <remarks>
/// Pure and side-effect free, so every rule — including the ones that are awkward to reach for real,
/// such as a guest newer than the host — is directly testable.
/// <para>
/// Version and binary hash are both consulted. The version alone cannot tell two builds of the same
/// version apart, which is exactly the case a developer iterating on winapp hits constantly, and the
/// hash alone carries no ordering, so it cannot distinguish an upgrade from a downgrade.
/// </para>
/// </remarks>
internal static class GuestAgentUpdatePlanner
{
    /// <summary>Plans the action for a host and the agent it found, if any.</summary>
    public static GuestAgentPlan Plan(GuestAgentIdentity host, GuestAgentHeartbeat? guest)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (guest is null)
        {
            return new GuestAgentPlan(GuestAgentAction.Install, "No guest agent is running.");
        }

        if (string.Equals(guest.Version, host.Version, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(guest.BinaryHash, host.BinaryHash, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(guest.Architecture, host.Architecture, StringComparison.OrdinalIgnoreCase))
        {
            return new GuestAgentPlan(GuestAgentAction.Reuse, "The guest agent already matches this host.");
        }

        var comparison = NuGetVersionHelper.Compare(host.Version, guest.Version);

        // Version ordering is decided *before* architecture, and deliberately so. A guest can be
        // running under emulation, or report an architecture this host spells differently, and
        // treating that as "install the host's binary" would replace a newer guest with an older
        // one -- a downgrade arrived at through a code path that never consulted the versions.
        // The no-downgrade rule has no exceptions, so it is enforced first.
        if (comparison is null)
        {
            // An unorderable version cannot be proven older, so it cannot be safely replaced
            // either. Failing closed is the only option that cannot silently move the guest
            // backwards.
            return new GuestAgentPlan(
                GuestAgentAction.FailIncompatible,
                $"The guest agent reports a version that cannot be compared ('{guest.Version}').");
        }

        if (comparison < 0)
        {
            // Guest is newer. Never downgraded -- reuse it when it still speaks a protocol revision
            // this host knows, otherwise the host is the thing that must be updated. Architecture
            // is irrelevant here: whatever it is, replacing a newer guest is a downgrade.
            return ProtocolOverlaps(host, guest)
                ? new GuestAgentPlan(
                    GuestAgentAction.Reuse,
                    $"The guest agent ({guest.Version}) is newer and protocol compatible.")
                : new GuestAgentPlan(
                    GuestAgentAction.FailIncompatible,
                    $"The guest agent ({guest.Version}) requires a newer winapp than this host ({host.Version}).");
        }

        // From here the host is newer or equal, so replacing is never a downgrade.
        if (!string.Equals(guest.Architecture, host.Architecture, StringComparison.OrdinalIgnoreCase))
        {
            return new GuestAgentPlan(
                GuestAgentAction.Install,
                $"The guest agent is {guest.Architecture} but this host is {host.Architecture}.");
        }

        if (comparison > 0)
        {
            return new GuestAgentPlan(
                GuestAgentAction.StageAndActivate,
                $"This host ({host.Version}) is newer than the guest agent ({guest.Version}).");
        }

        // Same version, different binary. This is a local winapp build against a released guest, or
        // a corrupted guest binary. Either way the host's binary is authoritative and replacing it
        // is not a downgrade.
        return new GuestAgentPlan(
            GuestAgentAction.StageAndActivate,
            "The guest agent is the same version but a different binary.");
    }

    /// <summary>
    /// Whether a forced repair may reinstall the host's binary.
    /// </summary>
    /// <remarks>
    /// Force-repair exists for a guest that is present but broken. It is still not allowed to move
    /// the guest backwards, so a genuinely newer guest is refused even when repair was asked for
    /// explicitly — and so is one whose version cannot be ordered, since that cannot be proven not
    /// to be newer.
    /// </remarks>
    public static bool CanForceRepair(GuestAgentIdentity host, GuestAgentHeartbeat? guest)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (guest is null)
        {
            return true;
        }

        // Architecture is not consulted: it can never make a downgrade acceptable.
        return NuGetVersionHelper.Compare(host.Version, guest.Version) is { } comparison && comparison >= 0;
    }

    /// <summary>Whether the two ends share at least one protocol revision.</summary>
    private static bool ProtocolOverlaps(GuestAgentIdentity host, GuestAgentHeartbeat guest) =>
        host.ProtocolMinimum <= guest.ProtocolMaximum && guest.ProtocolMinimum <= host.ProtocolMaximum;

    /// <summary>Builds the failure for an incompatible guest.</summary>
    public static ExecutionTargetException Incompatible(GuestAgentIdentity host, GuestAgentHeartbeat guest)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(guest);

        return ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.AgentIncompatible,
            $"The Windows Sandbox agent requires a newer winapp than this one ({host.Version}).",
            userAction: "Update winapp on this machine, then retry.",
            context: new Dictionary<string, string>
            {
                ["hostVersion"] = host.Version,
                ["guestVersion"] = guest.Version,
                ["hostProtocol"] = $"{host.ProtocolMinimum}-{host.ProtocolMaximum}",
                ["guestProtocol"] = $"{guest.ProtocolMinimum}-{guest.ProtocolMaximum}",
            },
            nextCommand: new ExecutionTargetNextCommand { Command = "winapp update", Advisory = false });
    }
}
