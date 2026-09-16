// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="GuestAgentReadiness"/> — the rules that stop the guest agent advertising
/// itself as able to do things it cannot.
/// </summary>
/// <remarks>
/// These conditions (session 0, a non-interactive window station, a disconnected Sandbox client)
/// cannot be reproduced in ordinary CI, so they are exercised through the probe seam. Getting them
/// wrong would mean reporting successful input delivery for input that never reached the guest,
/// which the specification forbids outright.
/// </remarks>
[TestClass]
public class GuestAgentReadinessTests
{
    private static GuestSessionInfo Interactive => new(SessionId: 1, "WinSta0", HasInputDesktop: true);

    [TestMethod]
    public void Evaluate_InteractiveSession_IsReady()
    {
        Assert.AreEqual(GuestReadinessFailure.None, GuestAgentReadiness.Evaluate(Interactive));
        Assert.IsTrue(GuestAgentReadiness.CanPublishHeartbeat(Interactive));
        Assert.IsTrue(GuestAgentReadiness.SupportsReadOnlyAutomation(Interactive));
    }

    [TestMethod]
    public void Evaluate_Session0_IsRefused()
    {
        var session = Interactive with { SessionId = 0 };

        Assert.AreEqual(GuestReadinessFailure.Session0, GuestAgentReadiness.Evaluate(session));

        // The agent must not advertise readiness it cannot honour, or the host would connect and
        // then be refused the very commands it started the agent for.
        Assert.IsFalse(GuestAgentReadiness.CanPublishHeartbeat(session));
        Assert.IsFalse(GuestAgentReadiness.SupportsReadOnlyAutomation(session));
    }

    [TestMethod]
    public void Evaluate_NonInteractiveWindowStation_IsRefused()
    {
        var session = Interactive with { WindowStationName = "Service-0x0-3e7$" };

        Assert.AreEqual(GuestReadinessFailure.NonInteractiveWindowStation, GuestAgentReadiness.Evaluate(session));
        Assert.IsFalse(GuestAgentReadiness.CanPublishHeartbeat(session));
    }

    [TestMethod]
    public void Evaluate_UnknownWindowStation_IsRefused()
    {
        var session = Interactive with { WindowStationName = null };

        Assert.AreEqual(GuestReadinessFailure.NonInteractiveWindowStation, GuestAgentReadiness.Evaluate(session));
    }

    [TestMethod]
    public void Evaluate_WindowStationNameIsCaseInsensitive()
    {
        var session = Interactive with { WindowStationName = "winsta0" };

        Assert.AreEqual(GuestReadinessFailure.None, GuestAgentReadiness.Evaluate(session));
    }

    [TestMethod]
    public void Evaluate_DisconnectedClient_RefusesInputButKeepsInspection()
    {
        var session = Interactive with { HasInputDesktop = false };

        Assert.AreEqual(GuestReadinessFailure.NoInputDesktop, GuestAgentReadiness.Evaluate(session));

        // Closing the Sandbox client leaves the guest session, its processes, and UI Automation
        // working while real input and Windows Graphics Capture stop. Inspection must therefore
        // stay available in exactly the situation where input has to be refused.
        Assert.IsTrue(GuestAgentReadiness.SupportsReadOnlyAutomation(session));
        Assert.IsFalse(GuestAgentReadiness.CanPublishHeartbeat(session));
    }

    [TestMethod]
    public void Describe_Session0_ReportsNoInteractiveSession()
    {
        var error = GuestAgentReadiness.Describe(GuestReadinessFailure.Session0);

        Assert.AreEqual(ExecutionTargetErrorCodes.NoInteractiveSession, error.Code);
        Assert.IsNotNull(error.UserAction);
    }

    [TestMethod]
    public void Describe_DisconnectedClient_ReportsInputNotReadyWithAdvisoryReconnect()
    {
        var error = GuestAgentReadiness.Describe(GuestReadinessFailure.NoInputDesktop);

        // The session exists and inspection works, so this is a readiness problem rather than a
        // missing session.
        Assert.AreEqual(ExecutionTargetErrorCodes.InputNotReady, error.Code);

        // Reconnecting changes what is on screen, so it must never run automatically.
        Assert.IsTrue(error.NextCommand!.Advisory);
    }

    [TestMethod]
    public void ToErrorCode_MapsEveryFailureToAReleasedCode()
    {
        foreach (var failure in Enum.GetValues<GuestReadinessFailure>())
        {
            if (failure == GuestReadinessFailure.None)
            {
                continue;
            }

            var code = GuestAgentReadiness.ToErrorCode(failure);

            CollectionAssert.Contains(
                ExecutionTargetErrorCodes.All.ToArray(),
                code,
                $"{failure} maps to '{code}', which is not a released code.");
        }
    }

    [TestMethod]
    public void Describe_NeverReportsSuccess()
    {
        foreach (var failure in Enum.GetValues<GuestReadinessFailure>())
        {
            if (failure == GuestReadinessFailure.None)
            {
                continue;
            }

            var error = GuestAgentReadiness.Describe(failure);

            Assert.IsFalse(string.IsNullOrWhiteSpace(error.Code));
            Assert.IsFalse(string.IsNullOrWhiteSpace(error.Message));
            Assert.IsFalse(
                string.IsNullOrWhiteSpace(error.UserAction),
                $"{failure} must tell the user how to recover.");
        }
    }
}
