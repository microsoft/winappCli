// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="WindowsSandboxReadiness"/>: which observed host facts mean which kind of
/// setup is still outstanding.
/// </summary>
/// <remarks>
/// Kept as a table over a pure function because the classification is where the live bug lived: a
/// host with the feature already enabled, but whose Store-delivered client had never initialized,
/// was reported as "feature not installed" and told to enable a feature that was already on.
/// </remarks>
[TestClass]
public class WindowsSandboxReadinessTests
{
    [TestMethod]
    public void NonWindowsHost_IsNotWindows()
    {
        var facts = Facts(isWindows: false);

        Assert.AreEqual(WindowsSandboxSetupState.NotWindows, facts.State);
    }

    [TestMethod]
    public void VersionAnswered_IsReady()
    {
        var facts = Facts(payload: true, package: true, alias: true, version: "0.8.107.0");

        Assert.AreEqual(WindowsSandboxSetupState.Ready, facts.State);
    }

    [TestMethod]
    public void VersionAnswered_WithoutAVisiblePayload_IsStillReady()
    {
        // Readiness is proven by wsb answering, not by winapp having watched every prerequisite
        // arrive. Demanding a payload sighting as well would make a working host look broken.
        var facts = Facts(payload: false, package: true, alias: true, version: "0.8.107.0");

        Assert.AreEqual(WindowsSandboxSetupState.Ready, facts.State);
    }

    [TestMethod]
    public void PayloadPresentButNothingElse_IsClientNotInitialized()
    {
        // The exact live failure: the optional feature is enabled and the machine has rebooted, but
        // the Store client has never been delivered, so there is no alias to run.
        var facts = Facts(payload: true, package: false, alias: false, version: null);

        Assert.AreEqual(WindowsSandboxSetupState.ClientNotInitialized, facts.State);
    }

    [TestMethod]
    public void PayloadAndPackagePresentButAliasSilent_IsClientNotInitialized()
    {
        // Servicing has staged the package but it is not usable yet.
        var facts = Facts(payload: true, package: true, alias: true, version: null, packageStatus: "Servicing");

        Assert.AreEqual(WindowsSandboxSetupState.ClientNotInitialized, facts.State);
    }

    [TestMethod]
    public void AliasPresentButSilent_IsNeverTreatedAsReady()
    {
        // The alias is a zero-byte APPEXECLINK reparse point, so File.Exists succeeds for one whose
        // package cannot launch. Only a version reply proves anything.
        var facts = Facts(payload: true, package: true, alias: true, version: null);

        Assert.AreNotEqual(WindowsSandboxSetupState.Ready, facts.State);
    }

    [TestMethod]
    public void NoPayload_IsFeaturePayloadMissing()
    {
        var facts = Facts(payload: false, package: false, alias: false, version: null);

        Assert.AreEqual(WindowsSandboxSetupState.FeaturePayloadMissing, facts.State);
    }

    [TestMethod]
    public void PackageStagedWithoutPayload_StillNeedsTheFeature()
    {
        // A package can be present on a machine whose optional feature was later turned off. The
        // payload is what the feature writes, so its absence is the feature's absence.
        var facts = Facts(payload: false, package: true, alias: true, version: null);

        Assert.AreEqual(WindowsSandboxSetupState.FeaturePayloadMissing, facts.State);
    }

    /// <summary>
    /// Regression: an enabled feature whose client has not initialized must never be told to enable
    /// the feature.
    /// </summary>
    /// <remarks>
    /// This is the whole point of separating the two states. Guidance is derived from the state, so
    /// pinning the state pins the guidance: the setup runner sends
    /// <see cref="WindowsSandboxSetupState.ClientNotInitialized"/> to the client bootstrapper and
    /// only <see cref="WindowsSandboxSetupState.FeaturePayloadMissing"/> to the feature enabler.
    /// </remarks>
    [TestMethod]
    [DataRow(true, false, false, DisplayName = "feature enabled, nothing delivered")]
    [DataRow(true, true, false, DisplayName = "feature enabled, package staged")]
    [DataRow(true, true, true, DisplayName = "feature enabled, alias present but silent")]
    public void EnabledFeatureWithUninitializedClient_NeverAsksToEnableTheFeature(
        bool payload,
        bool package,
        bool alias)
    {
        var facts = Facts(payload: payload, package: package, alias: alias, version: null);

        Assert.AreEqual(
            WindowsSandboxSetupState.ClientNotInitialized,
            facts.State,
            "An enabled feature must route to client initialization, never to enabling it again.");
    }

    private static WindowsSandboxHostFacts Facts(
        bool isWindows = true,
        bool payload = false,
        bool package = false,
        bool alias = false,
        string? version = null,
        string? packageStatus = null) =>
        new()
        {
            IsWindows = isWindows,
            FeaturePayloadPresent = payload,
            PackageRegistered = package,
            PackageStatus = packageStatus,
            AliasPresent = alias,
            ExecutablePath = alias ? @"C:\Users\test\AppData\Local\Microsoft\WindowsApps\wsb.exe" : null,
            Version = version,
        };
}
