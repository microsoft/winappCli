// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

[TestClass]
public class GuestDevToolsTests
{
    [TestMethod]
    [DataRow("missing")]
    [DataRow("native")]
    [DataRow("managed")]
    [DataRow("cli")]
    [DataRow("architecture")]
    [DataRow("protocol")]
    [DataRow("host-missing")]
    public void MatchingEngineGate_RefusesIncompleteOrDifferentBundles(string mismatch)
    {
        var expected = new GuestDevToolsCapabilities(2, "native-hash", "managed-hash", "x64", "cli-hash");
        var received = mismatch switch
        {
            "missing" => null,
            "native" => expected with { NativeHash = "different" },
            "managed" => expected with { ManagedHash = "different" },
            "cli" => expected with { CliHash = "older-cli-with-the-same-engines" },
            "architecture" => expected with { Architecture = "arm64" },
            "protocol" => expected with { Version = 1 },
            _ => expected,
        };
        var error = Assert.ThrowsExactly<ExecutionTargetException>(() =>
            GuestDevTools.RequireMatchingEngines(mismatch == "host-missing" ? null : expected, Capabilities(received)));
        Assert.AreEqual(ExecutionTargetErrorCodes.AgentIncompatible, error.Error.Code);
    }

    [TestMethod]
    public void MatchingEngineGate_AcceptsAllExactHashesAndArchitecture()
    {
        var expected = new GuestDevToolsCapabilities(2, "abcd", "ef12", "x64", "ab12");
        GuestDevTools.RequireMatchingEngines(expected,
            Capabilities(expected with { NativeHash = "ABCD", ManagedHash = "EF12", CliHash = "AB12" }));
    }

    private static ExecutionTargetCapabilities Capabilities(GuestDevToolsCapabilities? devTools) => new()
    {
        Architecture = "x64",
        SupportsInteractiveDesktop = true,
        SupportsRealInput = true,
        SupportsScreenCapture = true,
        SupportsInternalSystemSetup = true,
        CooperativeUiTurnsVersion = GuestOwnerContext.CooperativeUiTurnsVersion,
        PersistentStorage = false,
        DevTools = devTools,
    };
}
