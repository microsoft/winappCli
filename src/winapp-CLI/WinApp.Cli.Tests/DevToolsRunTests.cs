// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using WinApp.Cli.Commands;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class DevToolsRunTests() : BaseCommandTests(logLevel: Microsoft.Extensions.Logging.LogLevel.Warning), IDisposable
{
    private FakeMsixService _msix = null!;
    private FakeAppLauncherService _launcher = null!;
    private FakeProjectRunService _projects = null!;
    private FakeDebugOutputService _debug = null!;
    private FakePackageRegistrationService _registration = null!;
    private RecordingAttach _attach = null!;
    private FakeLaunchedProcess _process = null!;
    private ExecutionAliasResolver.AliasTarget _target = null!;
    private readonly UnusedTargetBackend _backend = new();
    private readonly List<(string? Root, bool Managed)> _environments = [];
    private readonly List<string> _order = [];

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _msix = new();
        _launcher = new();
        _projects = new();
        _debug = new();
        _registration = new();
        _attach = new(_order);
        return services
            .AddSingleton<IMsixService>(_msix)
            .AddSingleton<IAppLauncherService>(_launcher)
            .AddSingleton<IProjectRunService>(_projects)
            .AddSingleton<IDebugOutputService>(_debug)
            .AddSingleton<IPackageRegistrationService>(_registration)
            .AddSingleton<IDevToolsService>(_attach)
            .AddSingleton<IExecutionTargetBackend>(_backend)
            .AddSingleton<INugetService, FakeNugetService>();
    }

    private (string Input, string Manifest, string Output, string? Source) Prepare(string mode, bool packaged)
    {
        var output = _tempDirectory.CreateSubdirectory("output").FullName;
        var manifest = Path.Combine(output, "AppxManifest.xml");
        File.WriteAllText(manifest, RunCommandTests.TestManifestContent);
        var exe = Path.Combine(output, "TestApp.exe");
        File.WriteAllText(exe, "MZ");
        string input;
        string? source = null;
        if (mode == "folder")
        {
            input = output;
        }
        else
        {
            source = _tempDirectory.CreateSubdirectory("source").FullName;
            input = Path.Combine(source, mode == "project" ? "App.csproj" : "App.cs");
            File.WriteAllText(input, mode == "project" ? "<Project />" : "Console.WriteLine();");
            if (mode == "project")
            {
                _projects.BuildOutcome = new(new ProjectRunResolution(new FileInfo(input), output,
                    packaged ? null : exe, packaged ? ProjectPackaging.Packaged : ProjectPackaging.Unpackaged,
                    true, "x64", RunArguments: "--from-project"), 0);
            }
            else
            {
                _projects.SingleFileBuildOutcome = new(new SingleFileRunResolution(
                    new FileInfo(input), output, "TestApp.exe", "x64", "net10.0-windows10.0.19041.0", true,
                    packaged ? ProjectPackaging.Packaged : ProjectPackaging.Unpackaged, packaged ? null : exe, "--from-project",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)), 0);
            }
        }

        _target = new("TestPackage_fakefamily", "TestPackage_fakefamily!TestApp", exe);
        _msix.FakeIdentityResult = new("TestPackage", "CN=TestPublisher", "TestApp")
        {
            InspectorAlias = new("winapp-inspector-unit.exe", _target, false),
        };
        ResetOwnedProcess();
        _launcher.LaunchOverride = () => { _order.Add("launch"); return _process; };
        var aliasLauncher = GetRequiredService<InspectorAliasLauncher>();
        aliasLauncher.ProxyExists = _ => true;
        aliasLauncher.ReadTarget = _ => _target;
        GetRequiredService<RunCommand.Handler>().CreateDevToolsEnvironment = (root, managed) =>
        {
            _order.Add("environment");
            _environments.Add((root, managed));
            return DevToolsArtifacts.ComposeLaunchEnvironment(root, managed ? @"C:\owned\WinApp.DevTools.Managed.dll" : null,
                @"C:\source\WinApp.DevTools.Managed.dll", @"C:\developer\diagnostics.dll");
        };
        return (input, manifest, output, source);
    }

    private void ResetOwnedProcess(uint pid = 12345) => _process = new(pid, 0)
    {
        HasExited = false,
        StartTicksUtc = DateTime.UtcNow.Ticks,
        PackageFamilyName = _target.PackageFamilyName,
        ApplicationUserModelId = _target.ApplicationUserModelId,
        ExecutablePath = _target.TargetExecutable,
    };

    private Task<int> Run(string input, params string[] options) =>
        ParseAndInvokeWithCaptureAsync(GetRequiredService<RunCommand>(), [input, .. options]);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NativeAot_InspectsWithoutManagedStartupHooks(bool packaged)
    {
        var app = Prepare("project", packaged);
        var recipe = Path.Combine(app.Output, "App.build.appxrecipe");
        _projects.AotOutcome = new(_projects.BuildOutcome!.Resolution! with
        {
            IsAot = true,
            AppxManifestPath = packaged ? app.Manifest : null,
            AppxRecipePath = packaged ? recipe : null,
        }, 0);
        _attach.Result = DevToolsConnection.Ok(3, true);

        Assert.AreEqual(0, await Run(app.Input, "--aot", "--devtools", "--detach"), TestAnsiConsole.Output);
        Assert.AreEqual((app.Source, false), _environments.Single());
        Assert.IsFalse(_launcher.LastEnvironment!.ContainsKey("DOTNET_STARTUP_HOOKS"));
        Assert.AreEqual("1", _launcher.LastEnvironment["ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO"]);
        Assert.HasCount(1, _attach.Calls);
        Assert.IsEmpty(_projects.BuildOptions);
        Assert.HasCount(1, _projects.AotOptions);
        if (packaged)
        {
            Assert.AreEqual(recipe, _msix.AddLooseLayoutRecipeCalls.Single());
            Assert.IsNotNull(_msix.AddLooseLayoutInspectorRequests.Single());
            Assert.IsFalse(_msix.AddLooseLayoutEnsureAliasCalls.Single());
        }
    }

    [TestMethod]
    public async Task Correction_CancelledConnect_CannotDetachSuccessfully()
    {
        var app = Prepare("folder", true);
        using var cancellation = new CancellationTokenSource();
        _attach.OnConnect = cancellation.Cancel;
        var exit = await GetRequiredService<RunCommand.Handler>().InvokeAsync(
            GetRequiredService<RunCommand>().Parse([app.Input, "--devtools", "--detach", "--json"]), cancellation.Token);
        Assert.AreNotEqual(0, exit);
        Assert.IsTrue(_process.Killed);
        Assert.IsTrue(_process.Disposed);
        Assert.IsFalse(TestAnsiConsole.Output.Contains("\"ProcessId\"", StringComparison.Ordinal));
    }

    public static IEnumerable<object[]> OverlayModes
    {
        get
        {
            foreach (var (mode, packaged) in new[] { ("folder", true), ("project", true), ("single", true), ("project", false), ("single", false) })
            {
                foreach (var json in new[] { false, true })
                {
                    foreach (var noOverlay in new[] { false, true })
                    {
                        foreach (var explicitLocal in new[] { false, true })
                        {
                            yield return [mode, packaged, json, noOverlay, explicitLocal];
                        }
                    }
                }
            }
        }
    }

    [TestMethod]
    [DynamicData(nameof(OverlayModes))]
    public async Task ActualMode_AttachesBeforeDetach_WithOwnedEnvironmentAndStdio(string mode, bool packaged, bool json, bool noOverlay, bool explicitLocal)
    {
        var app = Prepare(mode, packaged);
        _attach.Result = DevToolsConnection.Ok(3, !noOverlay);
        string[] manifest = mode == "single" && packaged ? ["--manifest", app.Manifest] : [];
        string[] format = json ? ["--json"] : [];
        string[] overlay = noOverlay ? ["--no-overlay"] : [];
        string[] target = explicitLocal ? ["--on", "local"] : [];
        var exit = await ParseAndInvokeWithCaptureAsync(GetRequiredService<WinAppRootCommand>(),
            ["run", app.Input, "--devtools", "--detach", .. target, .. format, .. overlay, .. manifest, "--", "space value", "tail"]);
        Assert.AreEqual(0, exit, TestAnsiConsole.Output);
        Assert.AreEqual(0, _backend.Calls);
        CollectionAssert.AreEqual(new List<string> { "environment", "launch", "attach" }, _order);
        Assert.AreEqual((app.Source, mode != "folder"), _environments.Single());
        Assert.AreEqual(LaunchStdioMode.Suppress, _launcher.LastLaunchStdioMode);
        Assert.AreEqual("1", _launcher.LastEnvironment!["ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO"]);
        Assert.IsNull(_launcher.LastEnvironment["WINAPP_WATCH_PID"]);
        Assert.IsFalse(_launcher.LastEnvironment.ContainsKey("DOTNET_MODIFIABLE_ASSEMBLIES"));
        var launch = _launcher.LaunchExecutableCalls.Single();
        StringAssert.Contains(launch.Arguments!, "\"space value\"");
        if (!packaged)
        {
            StringAssert.StartsWith(launch.Arguments!, "--from-project ");
        }
        Assert.AreEqual(packaged ? app.Output : _tempDirectory.FullName, launch.WorkingDirectory);
        Assert.AreEqual(0, _launcher.LaunchCalls.Count);
        Assert.AreEqual((12345u, !noOverlay, DevToolsAccess.Mutation), _attach.Calls.Single());
        Assert.IsTrue(_process.Disposed);
        Assert.IsFalse(_process.Killed);
        Assert.AreEqual(packaged ? 1 : 0, _msix.AddLooseLayoutInspectorRequests.Count);
        if (packaged)
        {
            Assert.IsNotNull(_msix.AddLooseLayoutInspectorRequests.Single());
            Assert.IsFalse(_msix.AddLooseLayoutEnsureAliasCalls.Single(), "Only the staged inspector alias is requested.");
        }
        if (json)
        {
            using var document = JsonDocument.Parse(TestAnsiConsole.Output);
            Assert.AreEqual(12345u, document.RootElement.GetProperty("ProcessId").GetUInt32());
            Assert.IsFalse(document.RootElement.TryGetProperty("Error", out _));
            var devTools = document.RootElement.GetProperty("devTools");
            Assert.AreEqual(3, devTools.GetProperty("NodeCount").GetInt32());
            Assert.AreEqual(!noOverlay, devTools.GetProperty("OverlayShown").GetBoolean());
            Assert.AreEqual("local", devTools.GetProperty("Comments").GetString());
            Assert.IsFalse(devTools.TryGetProperty("SourceSnapshot", out _));
        }
        else
        {
            StringAssert.Contains(TestAnsiConsole.Output, "12345");
        }
    }

    [TestMethod]
    [DataRow("folder", true, false)]
    [DataRow("folder", true, true)]
    [DataRow("project", true, false)]
    [DataRow("project", true, true)]
    [DataRow("project", false, false)]
    [DataRow("project", false, true)]
    [DataRow("single", true, false)]
    [DataRow("single", true, true)]
    [DataRow("single", false, false)]
    [DataRow("single", false, true)]
    public async Task RemoteDevTools_DebugOutputFailsBeforeTargetProbeBuildRegistrationOrLaunch(string mode, bool packaged, bool json)
    {
        var app = Prepare(mode, packaged);
        string[] format = json ? ["--json"] : [];
        Assert.AreEqual(1, await ParseAndInvokeWithCaptureAsync(GetRequiredService<WinAppRootCommand>(),
            ["run", app.Input, "--devtools", "--on", "sandbox", "--debug-output", .. format]));
        Assert.AreEqual(0, _backend.Calls);
        Assert.IsEmpty(_projects.BuildAndResolveCalls);
        Assert.IsEmpty(_projects.BuildAndResolveSingleFileCalls);
        Assert.IsEmpty(_msix.AddLooseLayoutCalls);
        Assert.IsEmpty(_launcher.LaunchExecutableCalls);
        Assert.IsEmpty(_launcher.LaunchCalls);
        Assert.IsEmpty(_attach.Calls);
        Assert.IsEmpty(_environments);
        var message = json ? TestAnsiConsole.Output : ConsoleStdErr.ToString();
        if (json)
        {
            using var document = JsonDocument.Parse(message);
            message = document.RootElement.GetProperty("Error").GetString()!;
            Assert.IsFalse(document.RootElement.TryGetProperty("ProcessId", out _));
        }
        StringAssert.Contains(message, "does not support --with-alias, --debug-output or --unregister-on-exit");
    }

    private sealed class UnusedTargetBackend : IExecutionTargetBackend
    {
        public int Calls { get; private set; }
        public ExecutionTargetRef Target => new(ExecutionTargetRef.SandboxKind, ExecutionTargetRef.DefaultId);

        public Task<TargetSupportResult> ProbeSupportAsync(CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("DevTools must not probe a remote target.");
        }

        public Task<TargetConnection> EnsureConnectedAsync(EnsureTargetOptions options, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("DevTools must not provision a remote target.");
        }

        public IReadOnlyDictionary<string, string> DescribeForDiagnostics() => new Dictionary<string, string>();
    }

    [TestMethod]
    [DataRow("folder", false)]
    [DataRow("folder", true)]
    [DataRow("project", false)]
    [DataRow("project", true)]
    [DataRow("single", false)]
    [DataRow("single", true)]
    public async Task NoOverlayWithoutDevTools_FailsBeforeBuildOrRegistration(string mode, bool json)
    {
        var app = Prepare(mode, true);
        string[] format = json ? ["--json"] : [];
        Assert.AreEqual(1, await Run(app.Input, ["--no-overlay", .. format]));
        Assert.AreEqual(0, _projects.BuildAndResolveCalls.Count);
        Assert.AreEqual(0, _projects.BuildAndResolveSingleFileCalls.Count);
        Assert.AreEqual(0, _msix.AddLooseLayoutCalls.Count);
        Assert.AreEqual(0, _launcher.LaunchExecutableCalls.Count);
        Assert.AreEqual(0, _attach.Calls.Count);
        if (json)
        {
            using var document = JsonDocument.Parse(TestAnsiConsole.Output);
            StringAssert.Contains(document.RootElement.GetProperty("Error").GetString()!, "--no-overlay requires --devtools");
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RequestedOverlayFailure_IsNotSuccessfulDetach(bool json)
    {
        var app = Prepare("folder", true);
        _attach.Result = DevToolsConnection.Ok(3, false, "Cannot find AcrylicBackgroundFillColorDefaultBrush.");
        string[] format = json ? ["--json"] : [];
        Assert.AreEqual(1, await Run(app.Input, ["--devtools", "--detach", .. format]));
        Assert.IsTrue(_attach.Calls.Single().Overlay);
        Assert.IsTrue(_process.Disposed);
        Assert.IsFalse(_process.Killed);
        var message = json ? TestAnsiConsole.Output : ConsoleStdErr.ToString();
        if (json)
        {
            using var document = JsonDocument.Parse(message);
            message = document.RootElement.GetProperty("Error").GetString()!;
        }
        StringAssert.Contains(message, "DevTools attached to process 12345");
        StringAssert.Contains(message, "AcrylicBackgroundFillColorDefaultBrush");
        StringAssert.Contains(message, "winapp devtools inspect -a 12345");
        StringAssert.Contains(message, "--no-overlay");
    }
    [TestMethod]
    public async Task Folder_ManagedRuntimeConfig_ArmsBindingHostWithoutInventingSourceRoot()
    {
        var app = Prepare("folder", true);
        File.WriteAllText(Path.Combine(app.Output, "TestApp.runtimeconfig.json"), "{}");
        Assert.AreEqual(0, await Run(app.Input, "--devtools", "--detach", "--json"));
        Assert.AreEqual((null, true), _environments.Single());
        Assert.IsTrue(_launcher.LastEnvironment!.ContainsKey("DOTNET_STARTUP_HOOKS"));
        Assert.IsFalse(_launcher.LastEnvironment.ContainsKey("WINAPP_DEVTOOLS_SOURCE_ROOT"));
    }

    [TestMethod]
    [DataRow("--no-launch")]
    [DataRow("--without-alias")]
    public async Task IncompatibleOptions_FailBeforeBuildOrRegistration(string option)
    {
        var app = Prepare("project", true);
        Assert.AreEqual(1, await Run(app.Input, "--devtools", option, "--json"));
        Assert.AreEqual(0, _projects.BuildAndResolveCalls.Count);
        Assert.AreEqual(0, _msix.AddLooseLayoutCalls.Count);
        Assert.AreEqual(0, _launcher.LaunchExecutableCalls.Count);
    }

    [TestMethod]
    public async Task OrdinaryProjectAliasPreference_DoesNotDisablePrivateDevToolsAlias()
    {
        var app = Prepare("project", true);
        _projects.BuildOutcome = _projects.BuildOutcome! with
        {
            Resolution = _projects.BuildOutcome!.Resolution! with { PreferExecutionAlias = false },
        };
        Assert.AreEqual(0, await Run(app.Input, "--devtools", "--detach", "--json"));
        Assert.HasCount(1, _msix.AddLooseLayoutCalls);
        Assert.HasCount(1, _launcher.LaunchExecutableCalls);
        Assert.IsEmpty(_launcher.LaunchCalls);
    }

    [TestMethod]
    [DataRow(null, false)]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(false, true)]
    public async Task ProjectAliasPreference_AllowsDefaultEnabledOrExplicitOverride(bool? preference, bool withAlias)
    {
        var app = Prepare("project", true);
        _projects.BuildOutcome = _projects.BuildOutcome! with
        {
            Resolution = _projects.BuildOutcome!.Resolution! with { PreferExecutionAlias = preference },
        };
        string[] launch = withAlias ? ["--with-alias", "--debug-output"] : ["--detach", "--json"];
        _debug.FakeExitCode = 0;
        Assert.AreEqual(0, await Run(app.Input, ["--devtools", .. launch]));
        Assert.HasCount(1, _msix.AddLooseLayoutCalls);
        Assert.HasCount(1, _launcher.LaunchExecutableCalls);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("owner")]
    [DataRow("wrong")]
    public async Task AliasFailure_IsExplicitWithoutAumidFallbackOrSecondLaunch(string failure)
    {
        var app = Prepare("folder", true);
        var alias = GetRequiredService<InspectorAliasLauncher>();
        alias.ProxyExists = _ => failure != "missing";
        alias.ReadTarget = _ => failure == "owner" ? null :
            failure == "wrong" ? _target with { PackageFamilyName = "other" } : _target;
        Assert.AreEqual(1, await Run(app.Input, "--devtools", "--detach", "--json"));
        Assert.AreEqual(0, _launcher.LaunchCalls.Count);
        Assert.AreEqual(0, _launcher.LaunchExecutableCalls.Count);
        Assert.AreEqual(0, _attach.Calls.Count);
        StringAssert.Contains(TestAnsiConsole.Output, "No existing instance was adopted");
    }

    [TestMethod]
    public async Task EarlyExit_IsNotReportedAsAttachOrAdoptedInstance()
    {
        var app = Prepare("folder", true);
        _process.HasExited = true;
        Assert.AreEqual(1, await Run(app.Input, "--devtools", "--detach", "--json"));
        Assert.AreEqual(1, _launcher.LaunchExecutableCalls.Count);
        Assert.AreEqual(0, _launcher.LaunchCalls.Count);
        Assert.AreEqual(0, _attach.Calls.Count);
        Assert.IsTrue(_process.Disposed);
        Assert.IsFalse(_process.Killed);
        StringAssert.Contains(TestAnsiConsole.Output, "Exit code: 0");
    }

    [TestMethod]
    public async Task AttachFailure_ReportsOwnedPidWithoutKillingApp()
    {
        var app = Prepare("project", false);
        _attach.Result = DevToolsConnection.Fail("controlled attach failure");
        Assert.AreEqual(1, await Run(app.Input, "--devtools", "--detach", "--json"));
        Assert.IsFalse(_process.Killed);
        Assert.IsTrue(_process.Disposed);
        StringAssert.Contains(TestAnsiConsole.Output, "controlled attach failure");
        using var json = JsonDocument.Parse(TestAnsiConsole.Output);
        Assert.AreEqual(12345u, json.RootElement.GetProperty("ProcessId").GetUInt32());
    }

    [TestMethod]
    public async Task WithAlias_UsesInspectorTarget_AndPreservesForegroundStdio()
    {
        var app = Prepare("project", true);
        _attach.OnConnect = () => _process.HasExited = false;
        _debug.FakeExitCode = 7;
        Assert.AreEqual(7, await Run(app.Input, "--devtools", "--with-alias", "--debug-output"));
        Assert.AreEqual(LaunchStdioMode.Inherit, _launcher.LastLaunchStdioMode);
        Assert.AreEqual(1, _launcher.LaunchExecutableCalls.Count);
        Assert.AreEqual(0, _launcher.LaunchCalls.Count);
        Assert.IsTrue(_attach.Calls.Single().Overlay);
        Assert.IsTrue(_process.Disposed);
    }

    [TestMethod]
    public async Task NoFlag_DoesNotPrepareOrAttach_AndAllowsFastExit()
    {
        var app = Prepare("project", false);
        _process.HasExited = true;
        Assert.AreEqual(0, await Run(app.Input));
        Assert.AreEqual(0, _environments.Count);
        Assert.AreEqual(0, _attach.Calls.Count);
        Assert.IsNull(_launcher.LastEnvironment);
        Assert.AreEqual(LaunchStdioMode.Inherit, _launcher.LastLaunchStdioMode);
    }

    [TestMethod]
    public async Task RepeatRun_UsesOnlyEachNewOwnedProcess()
    {
        var app = Prepare("folder", true);
        var first = _process;
        Assert.AreEqual(0, await Run(app.Input, "--devtools", "--detach", "--json"));
        ResetOwnedProcess(12346);
        Assert.AreEqual(0, await Run(app.Input, "--devtools", "--detach", "--json"));
        CollectionAssert.AreEqual(new List<uint> { 12345, 12346 }, _attach.Calls.Select(c => c.Pid).ToList());
        Assert.AreEqual(2, _launcher.LaunchExecutableCalls.Count);
        Assert.AreEqual(0, _launcher.LaunchCalls.Count);
        Assert.IsTrue(first.Disposed);
        Assert.IsTrue(_process.Disposed);
        Assert.IsFalse(first.Killed);
        Assert.IsFalse(_process.Killed);
    }

    [TestMethod]
    public async Task WrongLaunchedIdentity_IsNotAttachedOrKilled()
    {
        var app = Prepare("folder", true);
        _process.PackageFamilyName = "another-app";
        Assert.AreEqual(1, await Run(app.Input, "--devtools", "--detach", "--json"));
        Assert.AreEqual(1, _launcher.LaunchExecutableCalls.Count);
        Assert.AreEqual(0, _attach.Calls.Count);
        Assert.IsTrue(_process.Disposed);
        Assert.IsFalse(_process.Killed);
    }

    [TestMethod]
    public async Task EnvironmentFailure_IsNotASecondLaunchOrSuccess()
    {
        var app = Prepare("folder", true);
        GetRequiredService<RunCommand.Handler>().CreateDevToolsEnvironment = (_, _) =>
            throw new IOException("controlled staging failure");
        Assert.AreEqual(1, await Run(app.Input, "--devtools", "--detach", "--json"));
        Assert.AreEqual(0, _launcher.LaunchExecutableCalls.Count);
        Assert.AreEqual(0, _launcher.LaunchCalls.Count);
        Assert.AreEqual(0, _attach.Calls.Count);
        StringAssert.Contains(TestAnsiConsole.Output, "controlled staging failure");
    }

    [TestMethod]
    public async Task OrdinarySingleFileAliasPreference_DoesNotDisablePrivateDevToolsAlias()
    {
        var app = Prepare("single", true);
        _projects.SingleFileBuildOutcome = _projects.SingleFileBuildOutcome! with
        {
            Resolution = _projects.SingleFileBuildOutcome!.Resolution! with
            {
                Properties = new Dictionary<string, string> { [RunCommand.Handler.UseExecutionAliasProperty] = "false" },
            },
        };
        Assert.AreEqual(0, await Run(app.Input, "--devtools", "--detach", "--json", "--manifest", app.Manifest));
        Assert.HasCount(1, _msix.AddLooseLayoutCalls);
        Assert.HasCount(1, _launcher.LaunchExecutableCalls);
        Assert.IsEmpty(_launcher.LaunchCalls);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ForegroundCancellation_PreservesOwnedCleanup(bool debug)
    {
        var app = Prepare("folder", true);
        using var cancellation = new CancellationTokenSource();
        if (debug)
        {
            _debug.CancelTokenDuringLoop = cancellation;
        }
        else
        {
            _attach.OnConnect = cancellation.Cancel;
        }
        string[] options = debug ? ["--debug-output"] : ["--json"];
        await GetRequiredService<RunCommand.Handler>().InvokeAsync(
            GetRequiredService<RunCommand>().Parse([app.Input, "--devtools", .. options]), cancellation.Token);
        Assert.IsTrue(_process.Killed);
        Assert.IsTrue(_process.Disposed);
        Assert.AreEqual(0, _launcher.TerminateCalls.Count, "Only the verified owned process should be killed.");
    }

    [TestMethod]
    public async Task ForegroundExitOne_IsAppExitAndStillUnregisters()
    {
        var app = Prepare("folder", true);
        using var process = new ExitOneProcess(_target);
        _launcher.LaunchOverride = () => process;
        _attach.OnConnect = () => process.AllowExit = true;
        Assert.AreEqual(1, await Run(app.Input, "--devtools", "--unregister-on-exit", "--json"));
        Assert.IsTrue(process.HasExited);
        Assert.AreEqual(1, _registration.UnregisterByFullNameCalls.Count);
        using var json = JsonDocument.Parse(TestAnsiConsole.Output);
        Assert.IsFalse(json.RootElement.TryGetProperty("Error", out _), "Exit code 1 is not an attachment failure.");
    }

    private sealed class ExitOneProcess(ExecutionAliasResolver.AliasTarget target) : ILaunchedProcess
    {
        public uint ProcessId => 12345;
        public bool AllowExit { get; set; }
        public bool HasExited { get; private set; }
        public int ExitCode => 1;
        public string? PackageFamilyName => target.PackageFamilyName;
        public string? ApplicationUserModelId => target.ApplicationUserModelId;
        public string? ExecutablePath => target.TargetExecutable;
        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            if (!AllowExit)
            {
                return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            HasExited = true;
            return Task.CompletedTask;
        }
        public void Kill() => Assert.Fail("Normal app exit must not kill another process.");
        public void Dispose() { }
    }

    [TestCleanup]
    public void Dispose()
    {
        _process?.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class RecordingAttach(List<string> order) : IDevToolsService
    {
        public List<(uint Pid, bool Overlay, DevToolsAccess Access)> Calls { get; } = [];
        public DevToolsConnection Result { get; set; } = DevToolsConnection.Ok(3, true);
        public Action? OnConnect { get; set; }

        public Task<DevToolsConnection> ConnectAsync(uint targetPid, bool showOverlay, DevToolsAccess requestedAccess, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            order.Add("attach");
            Calls.Add((targetPid, showOverlay, requestedAccess));
            OnConnect?.Invoke();
            return Task.FromResult(Result);
        }
    }
}
