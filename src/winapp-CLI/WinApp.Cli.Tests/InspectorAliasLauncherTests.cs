// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class InspectorAliasLauncherTests
{
    public TestContext TestContext { get; set; } = null!;
    private static readonly ExecutionAliasResolver.AliasTarget Target =
        new("Contoso_publisher", "Contoso_publisher!App", @"C:\layout\app.exe");
    private static readonly InspectorAlias Alias = new("winapp-Contoso_publisher.exe", Target, false);

    [TestMethod]
    [DataRow("missing", InspectorAliasLaunchStatus.AliasUnavailable)]
    [DataRow("unreadable", InspectorAliasLaunchStatus.OwnerUnverifiable)]
    [DataRow("foreign", InspectorAliasLaunchStatus.WrongTarget)]
    [DataRow("same-family-wrong-app", InspectorAliasLaunchStatus.WrongTarget)]
    [DataRow("same-family-wrong-exe", InspectorAliasLaunchStatus.WrongTarget)]
    public async Task Launch_RechecksProxyAndRefusesWrongTarget(string mode, object expected)
    {
        var appLauncher = new FakeAppLauncherService();
        var launcher = new InspectorAliasLauncher(appLauncher)
        {
            ProxyExists = _ => mode != "missing",
            ReadTarget = _ => mode switch
            {
                "unreadable" => null,
                "foreign" => Target with { PackageFamilyName = "Other_publisher" },
                "same-family-wrong-app" => Target with { ApplicationUserModelId = "Contoso_publisher!Other" },
                "same-family-wrong-exe" => Target with { TargetExecutable = @"C:\other\app.exe" },
                _ => Target,
            },
        };
        var result = await launcher.LaunchAsync(Alias, cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual((InspectorAliasLaunchStatus)expected, result.Status);
        Assert.IsNotNull(result.Error);
        Assert.AreEqual(0, appLauncher.LaunchExecutableCalls.Count);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(7)]
    public async Task Launch_ImmediateExitIsNotAnInspectableTarget(int exitCode)
    {
        var process = new FakeLaunchedProcess(42, exitCode);
        var launcher = Create(process, out _);
        var result = await launcher.LaunchAsync(Alias, cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(InspectorAliasLaunchStatus.Exited, result.Status);
        Assert.AreEqual(exitCode, result.ExitCode);
        Assert.AreEqual(42u, result.ProcessId);
        Assert.IsTrue(process.Disposed);
        Assert.IsFalse(process.Killed);
    }

    [TestMethod]
    [DataRow(null, "Contoso_publisher!App", @"C:\layout\app.exe")]
    [DataRow("Other_publisher", "Contoso_publisher!App", @"C:\layout\app.exe")]
    [DataRow("Contoso_publisher", "Contoso_publisher!App", @"C:\other\app.exe")]
    [DataRow("Contoso_publisher", "Contoso_publisher!Other", @"C:\layout\app.exe")]
    [DataRow("Contoso_publisher", null, @"C:\layout\app.exe")]
    public async Task Launch_WrongLiveIdentityIsNotSuccess(string? family, string? aumid, string? path)
    {
        var process = new FakeLaunchedProcess(42, 0)
        {
            HasExited = false, PackageFamilyName = family, ApplicationUserModelId = aumid, ExecutablePath = path,
        };
        var launcher = Create(process, out _);
        var result = await launcher.LaunchAsync(Alias, settleTime: TimeSpan.Zero, cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(InspectorAliasLaunchStatus.WrongTarget, result.Status);
        Assert.IsTrue(process.Disposed);
        Assert.IsFalse(process.Killed);
    }

    [TestMethod]
    [DataRow(LaunchStdioMode.Inherit)]
    [DataRow(LaunchStdioMode.Suppress)]
    public async Task Launch_LiveTargetTransfersHandleAndForwardsLaunchInputs(object stdio)
    {
        var process = new FakeLaunchedProcess(42, 0)
        {
            HasExited = false, PackageFamilyName = Target.PackageFamilyName,
            ApplicationUserModelId = Target.ApplicationUserModelId, ExecutablePath = Target.TargetExecutable,
        };
        var launcher = Create(process, out var appLauncher);
        var environment = new Dictionary<string, string?> { ["SENTINEL"] = "private" };
        var result = await launcher.LaunchAsync(Alias, "--title \"hello world\"", @"C:\layout", environment,
            (LaunchStdioMode)stdio, settleTime: TimeSpan.Zero, cancellationToken: TestContext.CancellationToken);
        using var owned = result.Process;
        Assert.AreEqual(InspectorAliasLaunchStatus.Launched, result.Status);
        Assert.AreSame(process, owned);
        Assert.IsFalse(process.Disposed);
        Assert.IsFalse(process.Killed);
        var call = appLauncher.LaunchExecutableCalls.Single();
        Assert.AreEqual(ExecutionAliasResolver.ResolveAliasPath(Alias.AliasName)!.FullName, call.ExePath);
        Assert.AreEqual("--title \"hello world\"", call.Arguments);
        Assert.AreEqual(@"C:\layout", call.WorkingDirectory);
        Assert.AreEqual((LaunchStdioMode)stdio, appLauncher.LastLaunchStdioMode);
        Assert.AreSame(environment, appLauncher.LastEnvironment);
    }

    [TestMethod]
    public async Task Launch_StartFailureIsReported()
    {
        var launcher = Create(new FakeLaunchedProcess(42, 0), out var appLauncher);
        appLauncher.LaunchOverride = () => throw new System.ComponentModel.Win32Exception("start denied");
        var result = await launcher.LaunchAsync(Alias, cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(InspectorAliasLaunchStatus.LaunchFailed, result.Status);
        StringAssert.Contains(result.Error, "start denied");
    }

    [TestMethod]
    public async Task Launch_CancellationDisposesWithoutKillingTarget()
    {
        var process = new FakeLaunchedProcess(42, 0) { HasExited = false };
        var launcher = Create(process, out var appLauncher);
        using var cts = new CancellationTokenSource();
        appLauncher.LaunchOverride = () => { cts.Cancel(); return process; };
        await Assert.ThrowsAsync<OperationCanceledException>(() => launcher.LaunchAsync(Alias, cancellationToken: cts.Token));
        Assert.IsTrue(process.Disposed);
        Assert.IsFalse(process.Killed);
    }

    private static InspectorAliasLauncher Create(FakeLaunchedProcess process, out FakeAppLauncherService appLauncher)
    {
        appLauncher = new FakeAppLauncherService { LaunchOverride = () => process };
        return new InspectorAliasLauncher(appLauncher) { ProxyExists = _ => true, ReadTarget = _ => Target };
    }
}
