// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;
using WinApp.Cli.Telemetry;
using WinApp.Cli.Telemetry.Events;

namespace WinApp.Cli.Tests;

[DoNotParallelize]
[TestClass]
public sealed class ProjectContextCommandTelemetryTests : BaseCommandTests
{
    private CapturingTelemetry _telemetry = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        var buildTools = new FakeBuildToolsService
        {
            BuildToolsResult = _tempDirectory,
        };

        return services
            .AddSingleton<IWorkspaceSetupService>(new FakeWorkspaceSetupService())
            .AddSingleton<IMsixService>(new FakeMsixService())
            .AddSingleton<INugetService>(new FakeNugetService())
            .AddSingleton<IPackageInstallationService>(new FakePackageInstallationService())
            .AddSingleton<IBuildToolsService>(buildTools)
            .AddSingleton<IWindowsAppRuntimeService>(new FakeWindowsAppRuntimeService());
    }

    [TestInitialize]
    public void StartCapture()
    {
        _telemetry = new CapturingTelemetry();
        TelemetryFactory.SetOverrideForTesting(_telemetry);
    }

    [TestCleanup]
    public void StopCapture()
    {
        TelemetryFactory.SetOverrideForTesting(null);
    }

    [TestMethod]
    public async Task Init_ExplicitDefaults_PreservesExactMarkerContext()
    {
        CreateWpfProject();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            GetRequiredService<InitCommand>(),
            [_tempDirectory.FullName, "--use-defaults"]);

        Assert.AreEqual(0, exitCode);
        var context = GetProjectContextEvent();
        Assert.AreEqual("init", context.Command);
        Assert.AreEqual("dotnet", context.ProjectFamily);
        Assert.AreEqual("wpf", context.AppFramework);
        Assert.AreEqual("exact-marker", context.DetectionSource);
        Assert.AreEqual("high", context.Confidence);
        Assert.AreEqual("unknown", context.Packaging);
    }

    [TestMethod]
    public async Task Init_InteractiveDetection_EmitsSelectedProjectContext()
    {
        CreateWpfProject();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            GetRequiredService<InitCommand>(),
            []);

        Assert.AreEqual(0, exitCode);
        var context = GetProjectContextEvent();
        Assert.AreEqual("selected-project", context.DetectionSource);
        Assert.AreEqual("high", context.Confidence);
    }

    [TestMethod]
    public async Task Init_ExplicitNestedDirectory_PreservesAncestorConfidence()
    {
        CreateWpfProject();
        var nestedDirectory = Directory.CreateDirectory(
            Path.Join(_tempDirectory.FullName, "bin", "Debug"));

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            GetRequiredService<InitCommand>(),
            [nestedDirectory.FullName, "--use-defaults"]);

        Assert.AreEqual(0, exitCode);
        var context = GetProjectContextEvent();
        Assert.AreEqual("ancestor-marker", context.DetectionSource);
        Assert.AreEqual("medium", context.Confidence);
    }

    [TestMethod]
    public void EnabledTelemetry_EmitsProjectContextAtMeasureLevel()
    {
        ProjectContextEvent.Log(
            "restore",
            ProjectContext.Unknown(ProjectTargetKind.Workspace));

        Assert.AreEqual("ProjectContext_Event", _telemetry.EventNames.Single());
        Assert.AreEqual(LogLevel.Measure, _telemetry.Levels.Single());
    }

    [TestMethod]
    public async Task Restore_EmitsWorkspaceProjectContext()
    {
        CreateWpfProject();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            GetRequiredService<RestoreCommand>(),
            [_tempDirectory.FullName]);

        Assert.AreEqual(0, exitCode);
        var context = GetProjectContextEvent();
        Assert.AreEqual("restore", context.Command);
        Assert.AreEqual("wpf", context.AppFramework);
        Assert.AreEqual("exact-marker", context.DetectionSource);
    }

    [TestMethod]
    public async Task Restore_NonObjectPackageMetadata_DoesNotFailCommand()
    {
        File.WriteAllText(Path.Join(_tempDirectory.FullName, "package.json"), "[]");

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            GetRequiredService<RestoreCommand>(),
            [_tempDirectory.FullName]);

        Assert.AreEqual(0, exitCode);
        var context = GetProjectContextEvent();
        Assert.AreEqual("node", context.ProjectFamily);
        Assert.AreEqual("unknown", context.AppFramework);
        Assert.AreEqual("medium", context.Confidence);
    }

    [TestMethod]
    public async Task Update_EmitsCurrentWorkspaceProjectContext()
    {
        CreateWpfProject();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            GetRequiredService<UpdateCommand>(),
            []);

        Assert.AreEqual(0, exitCode);
        var context = GetProjectContextEvent();
        Assert.AreEqual("update", context.Command);
        Assert.AreEqual("wpf", context.AppFramework);
        Assert.AreEqual("source-project", context.TargetKind);
    }

    [TestMethod]
    public async Task Package_EmitsPackagedProjectContext()
    {
        CreateWpfProject();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            GetRequiredService<PackageCommand>(),
            [_tempDirectory.FullName]);

        Assert.AreEqual(0, exitCode);
        var context = GetProjectContextEvent();
        Assert.AreEqual("package", context.Command);
        Assert.AreEqual("wpf", context.AppFramework);
        Assert.AreEqual("packaged", context.Packaging);
    }

    [TestMethod]
    public async Task NuGetRun_UsesCallerFrameworkAndKeepsFolderExecutionSeparate()
    {
        var output = _tempDirectory.CreateSubdirectory("build-output");

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            GetRequiredService<WinAppRootCommand>(),
            ["run", output.FullName, "--caller", "nuget-package", "--project-framework", "winui"]);

        Assert.AreEqual(1, exitCode, "The empty folder has no manifest, so run should fail after emitting context.");
        var context = GetProjectContextEvent();
        Assert.AreEqual("run", context.Command);
        Assert.AreEqual("dotnet", context.ProjectFamily);
        Assert.AreEqual("winui", context.AppFramework);
        Assert.AreEqual("source-project", context.TargetKind);
        Assert.AreEqual("nuget-msbuild", context.DetectionSource);
        Assert.AreEqual("packaged", context.Packaging);
        Assert.AreEqual("folder", context.ExecutionMode);
    }

    [TestMethod]
    public void OptedOutTelemetry_DoesNotInspectProjectMetadata()
    {
        _telemetry.IsTelemetryOn = false;
        var detectorCalled = false;

        ProjectContextEvent.Log("run", () =>
        {
            detectorCalled = true;
            return ProjectContext.Unknown(ProjectTargetKind.BuildOutput);
        });

        Assert.IsFalse(detectorCalled);
        Assert.IsEmpty(_telemetry.Events);
    }

    private ProjectContextEvent GetProjectContextEvent() =>
        _telemetry.Events.OfType<ProjectContextEvent>().Single();

    private void CreateWpfProject()
    {
        File.WriteAllText(
            Path.Join(_tempDirectory.FullName, "App.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <UseWPF>true</UseWPF>
              </PropertyGroup>
            </Project>
            """);
    }

    private sealed class CapturingTelemetry : ITelemetry
    {
        public List<EventBase> Events { get; } = [];

        public List<string> EventNames { get; } = [];

        public List<LogLevel> Levels { get; } = [];

        public bool IsTelemetryOn { get; set; } = true;

        public bool IsDiagnosticTelemetryOn { get; set; }

        public void AddSensitiveString(string name, string replaceWith)
        {
        }

        public void LogException(string action, Exception e, Guid? relatedActivityId = null)
        {
        }

        public void LogTimeTaken(string eventName, uint timeTakenMilliseconds, Guid? relatedActivityId = null)
        {
        }

        public void LogCritical(string eventName, bool isError = false, Guid? relatedActivityId = null)
        {
        }

        public void Log<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
            string eventName,
            LogLevel level,
            T data,
            Guid? relatedActivityId = null)
            where T : EventBase
        {
            Events.Add(data);
            EventNames.Add(eventName);
            Levels.Add(level);
        }

        public void LogError<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
            string eventName,
            LogLevel level,
            T data,
            Guid? relatedActivityId = null)
            where T : EventBase
        {
            Events.Add(data);
        }
    }
}
