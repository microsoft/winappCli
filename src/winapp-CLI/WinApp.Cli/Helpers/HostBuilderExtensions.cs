// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics.CodeAnalysis;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;

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
            .AddSingleton<IDevModeService, DevModeService>()
            .AddSingleton<IDirectoryPackagesService, DirectoryPackagesService>()
            .AddSingleton<IManifestTemplateService, ManifestTemplateService>()
            .AddSingleton<IManifestService, ManifestService>()
            .AddSingleton<IImageAssetService, ImageAssetService>()
            .AddSingleton<IMsixService, MsixService>()
            .AddSingleton<INugetService, NugetService>()
            .AddSingleton<IPackageCacheService, PackageCacheService>()
            .AddSingleton<IPackageInstallationService, PackageInstallationService>()
            .AddSingleton<IPackageLayoutService, PackageLayoutService>()
            .AddSingleton<IPowerShellService, PowerShellService>()
            .AddSingleton<IWinappDirectoryService, WinappDirectoryService>()
            .AddSingleton<IWorkspaceSetupService, WorkspaceSetupService>()
            .AddSingleton<IGitignoreService, GitignoreService>()
            .AddSingleton<IFirstRunService, FirstRunService>();
    }

    public static IServiceCollection ConfigureCommands(this IServiceCollection serviceCollection)
    {
        return serviceCollection
                .UseCommandHandler<InitCommand, InitCommand.Handler>()
                .ConfigureCommand<WinAppRootCommand>()
                .UseCommandHandler<RestoreCommand, RestoreCommand.Handler>()
                .UseCommandHandler<PackageCommand, PackageCommand.Handler>()
                .ConfigureCommand<ManifestCommand>()
                .UseCommandHandler<ManifestGenerateCommand, ManifestGenerateCommand.Handler>()
                .UseCommandHandler<ManifestUpdateAssetsCommand, ManifestUpdateAssetsCommand.Handler>()
                .UseCommandHandler<UpdateCommand, UpdateCommand.Handler>()
                .UseCommandHandler<CreateDebugIdentityCommand, CreateDebugIdentityCommand.Handler>()
                .UseCommandHandler<GetWinappPathCommand, GetWinappPathCommand.Handler>()
                .ConfigureCommand<CertCommand>()
                .UseCommandHandler<CertGenerateCommand, CertGenerateCommand.Handler>()
                .UseCommandHandler<CertInstallCommand, CertInstallCommand.Handler>()
                .UseCommandHandler<SignCommand, SignCommand.Handler>()
                .UseCommandHandler<ToolCommand, ToolCommand.Handler>();
    }

    public static IServiceCollection UseCommandHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TCommand, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(this IServiceCollection services)
        where TCommand : Command
        where THandler : AsynchronousCommandLineAction
    {
        return services
            .AddSingleton<THandler>()
            .AddSingleton(sp =>
            {
                var command = ActivatorUtilities.CreateInstance<TCommand>(sp);
                command.Options.Add(WinAppRootCommand.VerboseOption);
                command.Options.Add(WinAppRootCommand.QuietOption);
                command.SetAction((parseResult, ct) => sp.GetRequiredService<THandler>().InvokeAsync(parseResult, ct));
                return command;
            });
    }

    public static IServiceCollection ConfigureCommand<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TCommand>(this IServiceCollection services)
        where TCommand : Command
    {
        return services
            .AddSingleton(sp =>
            {
                var command = ActivatorUtilities.CreateInstance<TCommand>(sp);
                return command;
            });
    }
}
