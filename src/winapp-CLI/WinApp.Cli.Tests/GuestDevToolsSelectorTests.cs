// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class GuestDevToolsSelectorTests
{
    [TestMethod]
    [DataRow("")]
    [DataRow("123")]
    [DataRow("MyApp")]
    [DataRow("guest:../other")]
    [DataRow("guest:503765a8388748429b58203355d0cdd1a/file")]
    [DataRow("guest-process:2147483648:638936747284321987:epoch")]
    [DataRow("guest-process:123:9223372036854775808:epoch")]
    [DataRow("guest-process:123:3155378976000000000:epoch")]
    [DataRow("guest-process:123:-1:epoch")]
    [DataRow("guest-process:123:2026-09-18T00:00:00Z:epoch")]
    [DataRow("guest-process:123:0:epoch")]
    [DataRow("guest-process:123:638936747284321987:")]
    [DataRow("guest-process:123:638936747284321987:epoch\ncommand")]
    public void InvalidOrUnqualifiedSelector_IsRejectedWithDiscoveryRecovery(string value)
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            GuestDevToolsSelector.Parse(value, WindowsSandboxTarget.Default));
        StringAssert.Contains(error.Message, "winapp devtools list --on sandbox");
    }

    [TestMethod]
    public void DiscoverySelector_RoundTripsExactUtcTicksAndOpaqueColonEpoch()
    {
        var epoch = new ExecutionTargetEpoch("vm-id:nonce:still-opaque");
        var process = new GuestProcessStart(123, 638936747284321987);
        var value = GuestDevToolsSelector.ForProcess(WindowsSandboxTarget.Default, epoch, process);
        Assert.AreEqual("guest-process:123:638936747284321987:vm-id:nonce:still-opaque", value);
        var parsed = GuestDevToolsSelector.Parse(value, WindowsSandboxTarget.Default);
        Assert.AreEqual(process, parsed.Process);
        Assert.AreEqual(epoch.Value, parsed.Epoch);
    }

    [TestMethod]
    public void LaunchSelector_IsOnlyAnIdentity_NotAHostPath()
    {
        const string Id = "503765a8388748429b5820335d0cdd1a";
        var parsed = GuestDevToolsSelector.Parse(GuestDevToolsSelector.ForLaunch(Id), WindowsSandboxTarget.Default);
        Assert.AreEqual(Id, parsed.LaunchId);
        Assert.IsNull(parsed.Process, "Only the protected host receipt supplies the actual app PID/start.");
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            GuestDevToolsSelector.Parse("guest:" + Id, new("sandbox", "other")));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            GuestDevToolsSelector.Parse("guest-process:123:456:epoch", new("another-provider", "default")));
    }
}
