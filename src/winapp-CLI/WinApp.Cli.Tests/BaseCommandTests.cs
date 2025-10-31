// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

public abstract class BaseCommandTests(bool configPaths = true)
{
    private protected DirectoryInfo _tempDirectory = null!;
    private protected DirectoryInfo _testWinappDirectory = null!;
    private protected IConfigService _configService = null!;
    private protected IBuildToolsService _buildToolsService = null!;

    private ServiceProvider _serviceProvider = null!;
    protected StringWriter ConsoleStdOut { private set; get; } = null!;
    protected StringWriter ConsoleStdErr { private set; get; } = null!;

    [TestInitialize]
    public void SetupBase()
    {
        ConsoleStdOut = new StringWriter();
        ConsoleStdErr = new StringWriter();

        // Create a temporary directory for testing
        _tempDirectory = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"{this.GetType().Name}_{Guid.NewGuid():N}"));
        _tempDirectory.Create();

        // Set up a temporary winapp directory for testing (isolates tests from real winapp directory)
        _testWinappDirectory = _tempDirectory.CreateSubdirectory(".winapp");

        var services = new ServiceCollection()
            .ConfigureServices()
            .ConfigureCommands();
        services =
            ConfigureServices(services)
            // Override services
            .AddSingleton<ICurrentDirectoryProvider>(sp => new CurrentDirectoryProvider(_tempDirectory.FullName))
            .AddLogging(b =>
            {
                b.ClearProviders();
                b.AddTextWriterLogger([Console.Out, ConsoleStdOut], [Console.Error, ConsoleStdErr]);
                b.SetMinimumLevel(LogLevel.Debug);
            });

        _serviceProvider = services.BuildServiceProvider();

        // Set up services with test cache directory
        if (configPaths)
        {
            _configService = GetRequiredService<IConfigService>();
            _configService.ConfigPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "winapp.yaml"));

            var directoryService = GetRequiredService<IWinappDirectoryService>();
            directoryService.SetCacheDirectoryForTesting(_testWinappDirectory);
            _buildToolsService = GetRequiredService<IBuildToolsService>();
        }
    }

    protected virtual IServiceCollection ConfigureServices(IServiceCollection services)
    {
        return services;
    }

    [TestCleanup]
    public void CleanupBase()
    {
        _serviceProvider?.Dispose();
        ConsoleStdOut?.Dispose();
        ConsoleStdErr?.Dispose();

        // Clean up temporary files and directories
        _tempDirectory.Refresh();
        if (_tempDirectory.Exists)
        {
            try
            {
                _tempDirectory.Delete(true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    protected T GetRequiredService<T>() where T : notnull
    {
        return _serviceProvider.GetRequiredService<T>();
    }
}
