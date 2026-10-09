// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class GuestDevToolsRunTests : BaseCommandTests
{
    private readonly FakeAppLauncherService _launcher = new();
    private readonly FakePackageRegistrationService _registrations = new();

    protected override IServiceCollection ConfigureServices(IServiceCollection services) => services
        .AddSingleton<IAppLauncherService>(_launcher)
        .AddSingleton<IPackageRegistrationService>(_registrations);

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void RetainedRequest_UsesGuestPayloadSourceAndPrivateLaunchOptions(bool packaged, bool overlay)
    {
        var deployment = new GuestDeployment(new DeploymentState
        {
            SchemaVersion = 1, Revision = 2, DeploymentId = "app", TargetEpoch = "epoch", Dirty = false,
        }, @"C:\guest\payload", @"C:\guest\layout");
        var sources = new GuestSourceManifest(@"C:\host\App.csproj", [],
            @"C:\guest\sources", new string('A', 64));
        const string arguments = "--literal \"two words\"";
        var request = RunCommand.Handler.CreateGuestInspectorRequest(deployment, sources, "App.exe", arguments,
            overlay, new Dictionary<string, string> { ["RUNTIME"] = @"C:\guest\runtime" },
            packaged ? new("Package", "CN=Test", "Second") : null);
        var parsed = new GuestDevToolsLaunchCommand().Parse(request.Arguments.Skip(1).ToArray());
        Assert.IsEmpty(parsed.Errors);
        Assert.IsTrue(request.UseGuestWinapp);
        Assert.IsFalse(request.Detach, "The host helper, not an unowned execution request, retains the app.");
        Assert.IsTrue(request.RequiresRealInput);
        Assert.AreEqual(deployment.PayloadPath, request.WorkingDirectory);
        Assert.AreEqual(@"C:\guest\runtime", request.Environment!["RUNTIME"]);
        Assert.AreEqual(sources.GuestRoot, parsed.GetValue(GuestDevToolsLaunchCommand.SourceRootOption)!.FullName);
        Assert.AreEqual(sources.ManifestHash, parsed.GetValue(GuestDevToolsLaunchCommand.SourceHashOption));
        Assert.AreEqual(arguments, parsed.GetValue(GuestDevToolsLaunchCommand.ArgumentsOption));
        Assert.IsTrue(parsed.GetValue(GuestDevToolsLaunchCommand.ManagedOption));
        Assert.AreEqual(!overlay, parsed.GetValue(GuestDevToolsLaunchCommand.NoOverlayOption));
        Assert.IsFalse(request.Arguments.Any(value => value.Contains(@"C:\host", StringComparison.Ordinal)));
        if (packaged)
        {
            Assert.IsNull(parsed.GetValue(GuestDevToolsLaunchCommand.ExecutableOption));
            Assert.AreEqual("Second", parsed.GetValue(GuestDevToolsLaunchCommand.ApplicationIdOption));
            Assert.AreEqual(deployment.LayoutPath, parsed.GetValue(GuestDevToolsLaunchCommand.ExpectedLayoutOption)!.FullName);
        }
        else
        {
            Assert.IsNull(parsed.GetValue(GuestDevToolsLaunchCommand.PackageOption));
            Assert.AreEqual(@"C:\guest\payload\App.exe", parsed.GetValue(GuestDevToolsLaunchCommand.ExecutableOption)!.FullName);
        }
    }

    [TestMethod]
    [DataRow("correct")]
    [DataRow("foreign")]
    [DataRow("missing")]
    [DataRow("unverifiable")]
    [DataRow("wrong-layout")]
    [DataRow("wrong-publisher")]
    [DataRow("undeclared")]
    public async Task PackagedLaunch_RequiresExactRegisteredApplicationWithoutMutation(string scenario)
    {
        var layout = _tempDirectory.CreateSubdirectory("layout");
        var name = "WinApp.Guest." + Guid.NewGuid().ToString("N");
        var manifest = AppxManifestDocument.Parse($"""
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="{name}" Publisher="CN=Test" Version="1.0.0.0" ProcessorArchitecture="x64" />
              <Applications>
                <Application Id="First" Executable="First.exe" EntryPoint="Windows.FullTrustApplication" />
                <Application Id="Second" Executable="Second.exe" EntryPoint="Windows.FullTrustApplication" />
              </Applications>
            </Package>
            """);
        var intended = manifest.GetExecutionAliasTarget(layout.FullName, "Second");
        var foreign = manifest.GetExecutionAliasTarget(layout.FullName, "First");
        var alias = ExecutionAliasResolver.SelectInspectorAlias(intended, exists: _ => false)!;
        manifest.AddExecutionAlias("authored.exe", "Second", ExecutionAliasConflictPolicy.Coexist);
        if (scenario != "undeclared")
        {
            manifest.AddExecutionAlias(alias, "Second", ExecutionAliasConflictPolicy.Coexist);
        }
        var manifestPath = Path.Combine(layout.FullName, "AppxManifest.xml");
        manifest.Save(manifestPath);
        var original = File.ReadAllText(manifestPath);
        _registrations.FakeDevPackages =
        [
            new(name + "_1.0.0.0_x64__test", name, "1.0.0.0",
                scenario == "wrong-layout" ? Path.Combine(layout.FullName, "other") : layout.FullName, IsDevelopmentMode: true),
        ];
        var process = new FakeLaunchedProcess(123, 0)
        {
            HasExited = false, PackageFamilyName = intended.PackageFamilyName,
            ApplicationUserModelId = intended.ApplicationUserModelId, ExecutablePath = intended.TargetExecutable,
        };
        _launcher.LaunchOverride = () => process;
        var aliases = GetRequiredService<InspectorAliasLauncher>();
        aliases.ProxyExists = _ => scenario != "missing";
        aliases.ReadTarget = _ => scenario switch { "foreign" => foreign, "unverifiable" => null, _ => intended };
        var parsed = new GuestDevToolsLaunchCommand().Parse([
            "--package=" + name, "--publisher=" + (scenario == "wrong-publisher" ? "CN=Other" : "CN=Test"),
            "--application-id=Second", "--expected-layout=" + layout.FullName,
            "--source-root=" + layout.FullName, "--source-hash=" + new string('A', 64), "--args=--exact \"text\"",
        ]);
        Assert.IsEmpty(parsed.Errors);
        var environment = new Dictionary<string, string?> { ["DOTNET_STARTUP_HOOKS"] = @"C:\guest\Managed.dll" };
        var handler = GetRequiredService<GuestDevToolsLaunchCommand.Handler>();
        if (scenario == "correct")
        {
            using var launched = await handler.LaunchPackageAsync(parsed, name, environment, TestContext.CancellationToken);
            Assert.AreSame(process, launched);
            Assert.HasCount(1, _launcher.LaunchExecutableCalls);
            Assert.AreEqual("--exact \"text\"", _launcher.LaunchExecutableCalls[0].Arguments);
            Assert.AreEqual(LaunchStdioMode.Suppress, _launcher.LastLaunchStdioMode);
            Assert.AreSame(environment, _launcher.LastEnvironment);
        }
        else
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                handler.LaunchPackageAsync(parsed, name, environment, TestContext.CancellationToken));
            Assert.IsEmpty(_launcher.LaunchExecutableCalls);
        }
        Assert.IsEmpty(_launcher.LaunchCalls, "AUMID activation cannot supply the private startup environment.");
        Assert.IsEmpty(_registrations.RegisterLooseLayoutCalls);
        Assert.IsEmpty(_registrations.RegisterSparseCalls);
        Assert.IsEmpty(_registrations.InstallPackageCalls);
        Assert.IsEmpty(_registrations.UnregisterCalls);
        Assert.IsEmpty(_registrations.UnregisterByFullNameCalls);
        Assert.AreEqual(original, File.ReadAllText(manifestPath), "Launch cannot repair or overwrite authored alias metadata.");
    }
}
