// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public sealed class GuestDevToolsInvocationTests : BaseCommandTests
{
    protected override IServiceCollection ConfigureServices(IServiceCollection services) => services;

    [TestMethod]
    [DataRow("guest-devtools-launch", true)]
    [DataRow("guest-devtools-launch", false)]
    [DataRow("guest-comment-relay", true)]
    [DataRow("guest-comment-relay", false)]
    public async Task ActualFramedEntry_HelpAndParseFailuresNeverWriteToProtocolOutput(string helper, bool help)
    {
        var result = await InvokeProgramAsync(help ? [helper, "--help"] : [helper], coldCache: true);
        Assert.AreEqual(help ? 0 : 1, result.ExitCode);
        Assert.AreEqual(string.Empty, result.Stdout);
        if (!help)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.Stderr));
        }
    }

    [TestMethod]
    [DataRow("guest-devtools-launch")]
    [DataRow("guest-comment-relay")]
    public async Task ActualFramedEntry_RejectsNoncanonicalPrefixBeforeStartupNotices(string helper)
    {
        var result = await InvokeProgramAsync(["--json", helper, "--help"], coldCache: true);
        Assert.AreEqual(1, result.ExitCode);
        Assert.AreEqual(string.Empty, result.Stdout);
        StringAssert.Contains(result.Stderr, "must be the first argument");
    }

    [TestMethod]
    public async Task ActualGuestEntry_RejectsReusedLifetimeBeforeAnyNativeConnection()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var pid = process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var token = $"{pid}.{process.StartTime.ToUniversalTime().Ticks + 1}..guest:epoch";
        var result = await InvokeProgramAsync(
            ["devtools", "inspect", "--app", pid, "--guest-inspection", token, "--json"]);
        Assert.AreEqual(1, result.ExitCode);
        using var payload = JsonDocument.Parse(result.Stdout);
        Assert.IsFalse(payload.RootElement.GetProperty("ok").GetBoolean());
        StringAssert.Contains(result.Stdout, "lifetime expired");
    }

    [TestMethod]
    [DataRow("--help")]
    [DataRow("--cli-schema")]
    public async Task ActualGuestMetaAction_DoesNotActivateMalformedScope(string action)
    {
        var result = await InvokeProgramAsync(["devtools", "inspect", action, "--guest-inspection", "not-a-token"]);
        Assert.AreEqual(0, result.ExitCode);
        Assert.DoesNotContain("Invalid guest", result.Stdout + result.Stderr);
    }

    [TestMethod]
    [DataRow("--help")]
    [DataRow("--cli-schema")]
    public async Task ActualRemoteMetaAction_BypassesTargetProvisioning(string action)
    {
        var result = await InvokeProgramAsync(["devtools", "inspect", "--on=sandbox", action]);
        Assert.AreEqual(0, result.ExitCode);
        Assert.DoesNotContain("Sandbox agent", result.Stdout + result.Stderr);
    }

    [TestMethod]
    public async Task ActualRemoteInvocation_RejectsMissingScopeBeforeAnyTargetConnection()
    {
        var result = await InvokeProgramAsync(["devtools", "inspect", "--on=sandbox", "--json"]);
        Assert.AreEqual(1, result.ExitCode);
        using var payload = JsonDocument.Parse(result.Stdout);
        Assert.IsFalse(payload.RootElement.GetProperty("ok").GetBoolean());
        StringAssert.Contains(result.Stdout, "Supply --app");
    }
}
