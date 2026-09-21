// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.ExecutionTargets.GuestAgent;

/// <summary>
/// What the guest agent observed about the Windows session it is running in.
/// </summary>
/// <param name="SessionId">
/// Windows session ID. Session 0 is the non-interactive services session.
/// </param>
/// <param name="WindowStationName">
/// Name of the process window station, or <see langword="null"/> when it could not be read. Only
/// <c>WinSta0</c> is attached to the interactive desktop.
/// </param>
/// <param name="HasInputDesktop">
/// Whether the input desktop could be opened. This is what distinguishes a session that can receive
/// real input from one that merely exists.
/// </param>
internal sealed record GuestSessionInfo(int SessionId, string? WindowStationName, bool HasInputDesktop);

/// <summary>Reads the current process's Windows session and desktop state.</summary>
/// <remarks>
/// Behind an interface so the agent's refusal rules can be exercised without a real Sandbox, a real
/// session 0, or a disconnected desktop — none of which are reproducible in ordinary CI.
/// </remarks>
internal interface IGuestSessionProbe
{
    /// <summary>Reads the current session state.</summary>
    GuestSessionInfo Probe();
}

/// <summary>Why the guest agent is not ready to serve foreground-sensitive commands.</summary>
internal enum GuestReadinessFailure
{
    /// <summary>The agent is ready.</summary>
    None = 0,

    /// <summary>
    /// The agent is in session 0, the non-interactive services session. UI Automation against a
    /// real desktop is impossible from here.
    /// </summary>
    Session0,

    /// <summary>The process is not on the interactive window station.</summary>
    NonInteractiveWindowStation,

    /// <summary>
    /// No input desktop is available. On Windows Sandbox this is what a disconnected client looks
    /// like: the guest session survives and UI Automation still works, but real input and Windows
    /// Graphics Capture do not.
    /// </summary>
    NoInputDesktop,
}

/// <summary>
/// Decides whether the guest agent may publish a ready heartbeat and serve UI commands
/// (spec §"Guest winapp agent mode").
/// </summary>
/// <remarks>
/// The agent is always started as <c>ExistingLogin</c> after <c>wsb connect</c> establishes the
/// interactive session, but that is not something it may assume. An agent that advertised itself as
/// ready from session 0, or with no input desktop, would accept UI commands it cannot actually
/// perform and report success for input that was never delivered — which the spec forbids
/// explicitly. So readiness is verified, not assumed.
/// <para>
/// This is a <em>dynamic</em> check, deliberately re-evaluated rather than cached: the user can
/// close the Sandbox client at any moment, which silently removes real input and screen capture
/// while leaving UI Automation working.
/// </para>
/// </remarks>
internal static class GuestAgentReadiness
{
    /// <summary>The only window station attached to the interactive desktop.</summary>
    internal const string InteractiveWindowStation = "WinSta0";

    /// <summary>
    /// Evaluates whether <paramref name="session"/> can serve foreground-sensitive commands.
    /// </summary>
    public static GuestReadinessFailure Evaluate(GuestSessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.SessionId == 0)
        {
            return GuestReadinessFailure.Session0;
        }

        if (!string.Equals(session.WindowStationName, InteractiveWindowStation, StringComparison.OrdinalIgnoreCase))
        {
            return GuestReadinessFailure.NonInteractiveWindowStation;
        }

        if (!session.HasInputDesktop)
        {
            return GuestReadinessFailure.NoInputDesktop;
        }

        return GuestReadinessFailure.None;
    }

    /// <summary>
    /// Whether the agent may publish a ready heartbeat.
    /// </summary>
    /// <remarks>
    /// Tied to the same checks as UI readiness so the host never connects to an agent that would
    /// then refuse the very commands it was started for.
    /// </remarks>
    public static bool CanPublishHeartbeat(GuestSessionInfo session) =>
        Evaluate(session) == GuestReadinessFailure.None;

    /// <summary>
    /// Whether read-only UI Automation is possible.
    /// </summary>
    /// <remarks>
    /// An empirical Windows 11 ARM64 spike established that closing the Sandbox client leaves the
    /// guest user session, application processes, UI Automation, and UIA mutations working, while
    /// real input and Windows Graphics Capture stop. Inspection therefore stays available in the
    /// exact situation where input must be refused, so the two are classified separately.
    /// </remarks>
    public static bool SupportsReadOnlyAutomation(GuestSessionInfo session) =>
        Evaluate(session) is GuestReadinessFailure.None or GuestReadinessFailure.NoInputDesktop;

    /// <summary>Maps a readiness failure to the stable error code that describes it.</summary>
    public static string ToErrorCode(GuestReadinessFailure failure) => failure switch
    {
        GuestReadinessFailure.Session0 or GuestReadinessFailure.NonInteractiveWindowStation =>
            Abstractions.ExecutionTargetErrorCodes.NoInteractiveSession,

        // A missing input desktop is a readiness problem, not a missing session: the session is
        // there and inspection still works, but input cannot be delivered.
        GuestReadinessFailure.NoInputDesktop => Abstractions.ExecutionTargetErrorCodes.InputNotReady,

        _ => Abstractions.ExecutionTargetErrorCodes.InputNotReady,
    };

    /// <summary>Builds the structured failure for a readiness problem.</summary>
    /// <remarks>
    /// Readiness failures must never be reported as successful input delivery, so every path that
    /// would inject input converts the failure into this envelope instead.
    /// </remarks>
    public static Abstractions.ExecutionTargetErrorInfo Describe(GuestReadinessFailure failure) => failure switch
    {
        GuestReadinessFailure.Session0 => new Abstractions.ExecutionTargetErrorInfo
        {
            Code = Abstractions.ExecutionTargetErrorCodes.NoInteractiveSession,
            Message = "The guest agent is running in session 0, which has no interactive desktop.",
            UserAction = "Retry the command so winapp restarts the agent in the interactive Sandbox session.",
        },

        GuestReadinessFailure.NonInteractiveWindowStation => new Abstractions.ExecutionTargetErrorInfo
        {
            Code = Abstractions.ExecutionTargetErrorCodes.NoInteractiveSession,
            Message = "The guest agent is not attached to the interactive window station.",
            UserAction = "Retry the command so winapp restarts the agent in the interactive Sandbox session.",
        },

        GuestReadinessFailure.NoInputDesktop => new Abstractions.ExecutionTargetErrorInfo
        {
            Code = Abstractions.ExecutionTargetErrorCodes.InputNotReady,
            Message = "The Windows Sandbox window is disconnected, so real input and screen capture are unavailable.",
            UserAction = "Reconnect the Sandbox window, then retry.",
            NextCommand = new Abstractions.ExecutionTargetNextCommand
            {
                Command = "wsb connect",

                // Reconnecting changes what is on screen, so it needs a user decision.
                Advisory = true,
            },
        },

        _ => new Abstractions.ExecutionTargetErrorInfo
        {
            Code = Abstractions.ExecutionTargetErrorCodes.InputNotReady,
            Message = "The guest is not ready to receive input.",
            UserAction = "Retry the command.",
        },
    };
}
