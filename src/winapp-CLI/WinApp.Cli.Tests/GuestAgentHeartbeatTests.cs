// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

[TestClass]
public class GuestAgentHeartbeatTests
{
    private static GuestAgentIdentity Host() =>
        new("1.2.0", new string('1', 64), "arm64", GuestProtocol.MinimumVersion, GuestProtocol.CurrentVersion);

    [TestMethod]
    public void Heartbeat_RoundTripsAndRejectsUnknownSchema()
    {
        var heartbeat = GuestAgentHeartbeat.Create(
            Host(), GuestReadinessFailure.None,
            ExecutionTargetEpoch.Create("sandbox-1", "nonce"), 51234, DateTimeOffset.UtcNow);

        var parsed = GuestAgentHeartbeat.TryParse(heartbeat.ToJson());
        Assert.IsNotNull(parsed);
        Assert.AreEqual(heartbeat.BinaryHash, parsed.BinaryHash);
        Assert.AreEqual(51234, parsed.Port);
        Assert.IsTrue(parsed.Ready);
        Assert.IsNull(GuestAgentHeartbeat.TryParse("{not json"));
        Assert.IsNull(GuestAgentHeartbeat.TryParse(string.Empty));
        Assert.IsNull(GuestAgentHeartbeat.TryParse(
            heartbeat.ToJson().Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Heartbeat_NotReady_StillPublishesTheReason()
    {
        var heartbeat = GuestAgentHeartbeat.Create(
            Host(), GuestReadinessFailure.Session0, ExecutionTargetEpoch.None, 0, DateTimeOffset.UtcNow);
        Assert.IsFalse(heartbeat.Ready);
        Assert.AreEqual(nameof(GuestReadinessFailure.Session0), heartbeat.NotReadyReason);
    }

    [TestMethod]
    public void Heartbeat_StaleTimestamp_IsNotFresh()
    {
        var now = DateTimeOffset.UtcNow;
        var heartbeat = GuestAgentHeartbeat.Create(
            Host(), GuestReadinessFailure.None, ExecutionTargetEpoch.None, 0, now);
        Assert.IsTrue(heartbeat.IsFresh(now));
        Assert.IsFalse(heartbeat.IsFresh(now + GuestAgentHeartbeat.MaximumAge + TimeSpan.FromSeconds(1)));
    }
}
