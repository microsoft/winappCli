using Microsoft.Extensions.DependencyInjection;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics.CodeAnalysis;
using Winsdk.Cli.Commands;
using Winsdk.Cli.Services;

namespace Winsdk.Cli.Helpers;

internal static class StoreHostBuilderExtensions
{
    public static IServiceCollection ConfigureServices(this IServiceCollection services)
    {
        return services
            .AddSingleton<IBuildToolsService, BuildToolsService>()
            .AddSingleton<ICertificateService, CertificateService>()
            .AddSingleton<IConfigService, ConfigService>()
            .AddSingleton<ICppWinrtService, CppWinrtService>()
            .AddSingleton<IDevModeService, DevModeService>()
            .AddSingleton<IManifestService, ManifestService>()
            .AddSingleton<IMsixService, MsixService>()
            .AddSingleton<INugetService, NugetService>()
            .AddSingleton<IPackageCacheService, PackageCacheService>()
            .AddSingleton<IPackageInstallationService, PackageInstallationService>()
            .AddSingleton<IPackageLayoutService, PackageLayoutService>()
            .AddSingleton<IPowerShellService, PowerShellService>()
            .AddSingleton<IWinsdkDirectoryService, WinsdkDirectoryService>()
            .AddSingleton<IWorkspaceSetupService, WorkspaceSetupService>();
    }

    public static IServiceCollection ConfigureCommands(this IServiceCollection serviceCollection)
    {
        return serviceCollection
                .UseCommandHandler<InitCommand, InitCommand.Handler>()
                .ConfigureCommand<WinSdkRootCommand>()
                .UseCommandHandler<RestoreCommand, RestoreCommand.Handler>()
                .UseCommandHandler<PackageCommand, PackageCommand.Handler>()
                .ConfigureCommand<ManifestCommand>()
                .UseCommandHandler<ManifestGenerateCommand, ManifestGenerateCommand.Handler>()
                .UseCommandHandler<UpdateCommand, UpdateCommand.Handler>()
                .UseCommandHandler<CreateDebugIdentityCommand, CreateDebugIdentityCommand.Handler>()
                .UseCommandHandler<GetWinsdkPathCommand, GetWinsdkPathCommand.Handler>()
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
                command.Options.Add(WinSdkRootCommand.VerboseOption);
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
                command.Options.Add(WinSdkRootCommand.VerboseOption);
                return command;
            });
    }
}
