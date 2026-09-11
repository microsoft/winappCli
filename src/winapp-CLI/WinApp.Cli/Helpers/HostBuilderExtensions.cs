// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics.CodeAnalysis;
using WinApp.Cli.Commands;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;
using WinApp.Cli.Services;
using WinApp.Cli.Services.ApiSearch;
using WinApp.Cli.Services.Controls;
using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Helpers;

internal static class StoreHostBuilderExtensions
{
    public static IServiceCollection ConfigureServices(this IServiceCollection services)
    {
        return services
            .AddSingleton<ICurrentDirectoryProvider>(sp => new CurrentDirectoryProvider(Directory.GetCurrentDirectory()))
            .AddSingleton<IBuildToolsService, BuildToolsService>()
            .AddSingleton<ICertificateService, CertificateService>()
            .AddSingleton<IConfigService, ConfigService>()
            .AddSingleton<ICppWinrtService, CppWinrtService>()
            .AddSingleton<IDotNetService, DotNetService>()
            .AddSingleton<IDotNetProjectRestoreService, DotNetProjectRestoreService>()
            .AddSingleton<IDevModeService, DevModeService>()
            .AddSingleton<IDirectoryPackagesService, DirectoryPackagesService>()
            .AddSingleton<IManifestTemplateService, ManifestTemplateService>()
            .AddSingleton<IManifestService, ManifestService>()
            .AddSingleton<IImageAssetService, ImageAssetService>()
            .AddSingleton<IMsixService, MsixService>()
            .AddSingleton<IBundleService, BundleService>()
            .AddSingleton<IBundleValidationService, BundleValidationService>()
            .AddSingleton<IPriService, PriService>()
            .AddSingleton<NugetSourceProvider>()
            .AddSingleton<NugetPackageDownloader>()
            .AddSingleton<INugetService, NugetService>()
            .AddSingleton<IPackageInstallationService, PackageInstallationService>()
            .AddSingleton<IPackageLayoutService, PackageLayoutService>()
            .AddSingleton<IWinappDirectoryService, WinappDirectoryService>()
            .AddSingleton<IApiMetadataService, ApiMetadataService>()
            .AddSingleton<ISdkPackageSource, SdkPackageSource>()
            .AddSingleton<IWinmdService, WinmdService>()
            .AddSingleton<IWinmdsLockfileService, WinmdsLockfileService>()
            .AddSingleton<IProjectDetectionService, ProjectDetectionService>()
            .AddSingleton<IProjectContextDetector, ProjectContextDetector>()
            .AddSingleton<ICsWinRTMetadataShimService, CsWinRTMetadataShimService>()
            .AddSingleton<IProjectRunService, ProjectRunService>()
            .AddSingleton<ITemplateCacheReader, TemplateCacheReader>()
            .AddSingleton<ITemplateUpdateCheckThrottle, TemplateUpdateCheckThrottle>()
            .AddSingleton<IWorkspaceSetupService, WorkspaceSetupService>()
            .AddSingleton<IWindowsAppRuntimeService, WindowsAppRuntimeService>()
            .AddSingleton<IGitignoreService, GitignoreService>()
            .AddSingleton<IFirstRunService, FirstRunService>()
            .AddSingleton<ICodeIntegrityCatalogService, CodeIntegrityCatalogService>()
            .AddSingleton<IAppLauncherService, AppLauncherService>()
            .AddSingleton<IPackageRegistrationService, PackageRegistrationService>()
            .AddSingleton<IDebugOutputService, DebugOutputService>()
            .AddSingleton<IXamlTriageService, XamlTriageService>()
            .AddSingleton<ICrashDumpService, CrashDumpService>()
            .AddSingleton(AnsiConsole.Console)
            .AddSingleton<IStatusService, StatusService>()
            .AddSingleton<IMSStoreCLIService, MSStoreCLIService>()
            .AddSingleton<IUpdateNotificationService, UpdateNotificationService>()
            // Azure Trusted Signing services
            .AddSingleton<IProcessRunner, ProcessRunner>()
            .AddSingleton<IAzureAuthService, AzureAuthService>()
            .AddSingleton<IAzureSigningService, AzureSigningService>()
            .AddSingleton<IAzureSignToolService, AzureSignToolService>()
            // UI Automation services (from the Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation package)
            .AddWinAppUiAutomation()
            .AddWinAppUiRecording()
            .AddSingleton<IProcessInspector, ProcessInspector>()
            .AddSingleton<IMonotonicClock, TickCountClock>()
            .AddSingleton<IInteractiveDesktopPaths, InteractiveDesktopPaths>()
            .AddSingleton<IParticipantRegistry, ParticipantRegistry>()
            .AddSingleton<IParticipantSignals, ParticipantSignals>()
            .AddSingleton<IInteractiveDesktopStateStore, InteractiveDesktopStateStore>()
            .AddSingleton<IUiOwnerResolver, UiOwnerResolver>()
            .AddSingleton<IInteractiveDesktopLock, InteractiveDesktopLock>()
            .AddSingleton<IDesktopForegroundService, DesktopForegroundService>()
            .AddSingleton<IControlsSearchService, ControlsSearchService>()
            // Execution targets (Windows Sandbox and any future target)
            .AddSingleton<ITargetStateDirectoryProvider>(_ => new TargetStateDirectoryProvider())
            .AddSingleton<ITargetProgress, LoggerTargetProgress>()
            .AddSingleton<ITargetStateStore, TargetStateStore>()
            .AddSingleton<ITargetMutationLock, TargetMutationLock>()
            .AddSingleton<ITargetConnectionLock, TargetConnectionLock>()
            .AddSingleton<IWindowsSandboxCli, WindowsSandboxCli>()
            .AddSingleton<IWindowsSandboxWindowController, WindowsSandboxWindowController>()
            .AddSingleton<IWindowsSandboxHostProbe, WindowsSandboxHostProbe>()
            .AddSingleton<IWindowsFeatureEnabler, WindowsFeatureEnabler>()
            .AddSingleton<IWindowsSandboxSetup, WindowsSandboxSetup>()
            .AddSingleton<WindowsSandboxLifecycle>()
            .AddSingleton<IGuestSessionProbe, GuestSessionProbe>()
            .AddSingleton<IGuestProcessHostFactory, GuestProcessHostFactory>()
            .AddSingleton<IHostWinappBinaryProvider, HostWinappBinaryProvider>()
            .AddSingleton<IDeploymentStateStore, DeploymentStateStore>()
            .AddSingleton<IVcLibsPayloadAcquirer, VcLibsPayloadAcquirer>()
            .AddSingleton<IRuntimePayloadResolver, RuntimePayloadResolver>()
            .AddSingleton<IRuntimeFrameworkResolver, RuntimeFrameworkResolver>()
            .AddSingleton<TargetRuntimeService>()
            .AddSingleton<TargetDeploymentService>()
            .AddSingleton<GuestApplicationRunner>()
            .AddSingleton<ExecutionTargetUiRouter>()
            .AddSingleton<IExecutionTargetBackend, WindowsSandboxBackend>()
            .AddSingleton<ExecutionTargetOrchestrator>();
    }

    public static IServiceCollection ConfigureCommands(this IServiceCollection serviceCollection)
    {
        return serviceCollection
                .UseCommandHandler<InitCommand, InitCommand.Handler>()
                .UseCommandHandler<NewCommand, NewCommand.Handler>()
                .ConfigureCommand<WinAppRootCommand>()
                .UseCommandHandler<RestoreCommand, RestoreCommand.Handler>()
                .UseCommandHandler<PackageCommand, PackageCommand.Handler>()
                .ConfigureCommand<ManifestCommand>()
                .UseCommandHandler<ManifestGenerateCommand, ManifestGenerateCommand.Handler>()
                .UseCommandHandler<ManifestUpdateAssetsCommand, ManifestUpdateAssetsCommand.Handler>()
                .UseCommandHandler<ManifestAddAliasCommand, ManifestAddAliasCommand.Handler>()
                .UseCommandHandler<UpdateCommand, UpdateCommand.Handler>()
                .UseCommandHandler<CreateDebugIdentityCommand, CreateDebugIdentityCommand.Handler>()
                .UseCommandHandler<EmbedIdentityCommand, EmbedIdentityCommand.Handler>()
                .UseCommandHandler<RunCommand, RunCommand.Handler>()
                // GuestLaunchCommand shares RunCommand.Handler rather than a second handler
                // instance: it is a structurally distinct, hidden verb (see
                // RunCommand.GuestLaunch.cs) dispatched from the same class, because it reuses
                // that handler's launch/debug/alias/unregister logic without any path back into
                // package registration.
                .AddSingleton(sp =>
                {
                    var command = ActivatorUtilities.CreateInstance<GuestLaunchCommand>(sp);
                    command.Options.Add(WinAppRootCommand.VerboseOption);
                    command.Options.Add(WinAppRootCommand.QuietOption);
                    command.SetAction((parseResult, ct) => sp.GetRequiredService<RunCommand.Handler>().InvokeAsync(parseResult, ct));
                    return command;
                })
                .UseCommandHandler<UnregisterCommand, UnregisterCommand.Handler>()
                .UseCommandHandler<GetWinappPathCommand, GetWinappPathCommand.Handler>()
                .UseCommandHandler<FindUiCommand, FindUiCommand.Handler>()
                .ConfigureCommand<CertCommand>()
                .UseCommandHandler<CertGenerateCommand, CertGenerateCommand.Handler>()
                .UseCommandHandler<CertInstallCommand, CertInstallCommand.Handler>()
                .UseCommandHandler<CertInfoCommand, CertInfoCommand.Handler>()
                .UseCommandHandler<SignCommand, SignCommand.Handler>()
                .UseCommandHandler<AzSignCommand, AzSignCommand.Handler>()
                .UseCommandHandler<ToolCommand, ToolCommand.Handler>()
                .UseCommandHandler<MSStoreCommand, MSStoreCommand.Handler>(false)
                .UseCommandHandler<CreateExternalCatalogCommand, CreateExternalCatalogCommand.Handler>()
                // API discovery commands
                .UseCommandHandler<FindApiCommand, FindApiCommand.Handler>()
                .UseCommandHandler<FindApiMembersCommand, FindApiMembersCommand.Handler>()
                .UseCommandHandler<FindApiCheckPropertyCommand, FindApiCheckPropertyCommand.Handler>()
                .UseCommandHandler<FindApiTypesCommand, FindApiTypesCommand.Handler>()
                .UseCommandHandler<FindApiEnumsCommand, FindApiEnumsCommand.Handler>()
                .UseCommandHandler<FindApiNamespacesCommand, FindApiNamespacesCommand.Handler>()
                .UseCommandHandler<FindApiPackagesCommand, FindApiPackagesCommand.Handler>()
                .UseCommandHandler<FindApiStatsCommand, FindApiStatsCommand.Handler>()
                .UseCommandHandler<FindApiProjectsCommand, FindApiProjectsCommand.Handler>()
                .UseCommandHandler<FindApiRefreshCommand, FindApiRefreshCommand.Handler>()
                // UI Automation commands
                .ConfigureCommand<UiCommand>()
                .UseCommandHandler<UiStatusCommand, UiStatusCommand.Handler>()
                .UseCommandHandler<UiInspectCommand, UiInspectCommand.Handler>()
                .UseCommandHandler<UiSearchCommand, UiSearchCommand.Handler>()
                .UseCommandHandler<UiGetPropertyCommand, UiGetPropertyCommand.Handler>()
                .UseCommandHandler<UiGetValueCommand, UiGetValueCommand.Handler>()
                .UseCommandHandler<UiScreenshotCommand, UiScreenshotCommand.Handler>()
                .UseCommandHandler<UiRecordCommand, UiRecordCommand.Handler>()
                .UseCommandHandler<UiInvokeCommand, UiInvokeCommand.Handler>()
                .UseCommandHandler<UiClickCommand, UiClickCommand.Handler>()
                .UseCommandHandler<UiDragCommand, UiDragCommand.Handler>()
                .UseCommandHandler<UiTouchCommand, UiTouchCommand.Handler>()
                .UseCommandHandler<UiPenCommand, UiPenCommand.Handler>()
                .UseCommandHandler<UiHoverCommand, UiHoverCommand.Handler>()
                .UseCommandHandler<UiSendKeysCommand, UiSendKeysCommand.Handler>()
                .UseCommandHandler<UiSetValueCommand, UiSetValueCommand.Handler>()
                .UseCommandHandler<UiFocusCommand, UiFocusCommand.Handler>()
                .UseCommandHandler<UiScrollIntoViewCommand, UiScrollIntoViewCommand.Handler>()
                .UseCommandHandler<UiScrollCommand, UiScrollCommand.Handler>()
                .UseCommandHandler<UiWaitForCommand, UiWaitForCommand.Handler>()
                .UseCommandHandler<UiListWindowsCommand, UiListWindowsCommand.Handler>()
                .UseCommandHandler<UiGetFocusedCommand, UiGetFocusedCommand.Handler>()
                .UseCommandHandler<UiYieldCommand, UiYieldCommand.Handler>()
                // Execution-target guest agent: hidden, internal transport endpoint
                .UseCommandHandler<GuestAgentCommand, GuestAgentCommand.Handler>()
                .UseCommandHandler<GuestDesktopScreenshotCommand, GuestDesktopScreenshotCommand.Handler>()
                .UseCommandHandler<GuestDesktopRecordCommand, GuestDesktopRecordCommand.Handler>()
                .ConfigureCommand<GuestDesktopCaptureCommand>()
                // Execution-target runtime provisioning: hidden, driven by the host over the channel
                .UseCommandHandler<GuestRuntimeCommand, GuestRuntimeCommand.Handler>()
                // Generic execution-target escape hatches
                .ConfigureCommand<TargetCommand>()
                .UseCommandHandler<TargetExecCommand, TargetExecCommand.Handler>()
                .UseCommandHandler<TargetPushCommand, TargetPushCommand.Handler>()
                .UseCommandHandler<TargetPullCommand, TargetPullCommand.Handler>()
                // Execution-target diagnostics and guest-native capture
                .UseCommandHandler<TargetSnapshotCommand, TargetSnapshotCommand.Handler>()
                .UseCommandHandler<TargetScreenshotCommand, TargetScreenshotCommand.Handler>()
                .UseCommandHandler<TargetRecordCommand, TargetRecordCommand.Handler>()
                .ConfigureCommand<CompleteCommand>();
    }

    public static IServiceCollection UseCommandHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TCommand, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(this IServiceCollection services, bool addDefaultOptions = true)
        where TCommand : Command, IShortDescription
        where THandler : AsynchronousCommandLineAction
    {
        return services
            .AddSingleton<THandler>()
            .AddSingleton(sp =>
            {
                var command = ActivatorUtilities.CreateInstance<TCommand>(sp);
                if (addDefaultOptions)
                {
                    command.Options.Add(WinAppRootCommand.VerboseOption);
                    command.Options.Add(WinAppRootCommand.QuietOption);
                }
                command.SetAction((parseResult, ct) => sp.GetRequiredService<THandler>().InvokeAsync(parseResult, ct));
                return command;
            });
    }

    public static IServiceCollection ConfigureCommand<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TCommand>(this IServiceCollection services)
        where TCommand : Command, IShortDescription
    {
        return services
            .AddSingleton(sp =>
            {
                var command = ActivatorUtilities.CreateInstance<TCommand>(sp);
                return command;
            });
    }
}
