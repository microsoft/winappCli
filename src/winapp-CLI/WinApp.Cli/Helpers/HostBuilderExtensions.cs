// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics.CodeAnalysis;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;
using WinApp.Cli.Services.Controls;

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
            .AddSingleton<IControlsSearchService, ControlsSearchService>();
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
