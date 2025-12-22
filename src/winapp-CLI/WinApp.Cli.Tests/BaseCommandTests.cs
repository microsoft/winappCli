// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Testing;
using System.CommandLine;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

public abstract class BaseCommandTests(bool configPaths = true)
{
    private protected DirectoryInfo _tempDirectory = null!;
    private protected DirectoryInfo _testWinappDirectory = null!;
    private protected DirectoryInfo _testCacheDirectory = null!;
    private protected IConfigService _configService = null!;
    private protected IBuildToolsService _buildToolsService = null!;

    private ServiceProvider _serviceProvider = null!;
    private protected OutputCapture ConsoleStdOut { private set; get; } = null!;
    private protected OutputCapture ConsoleStdErr { private set; get; } = null!;

    public TestContext TestContext { get; set; } = null!;
    private protected TaskContext TestTaskContext { private set; get; } = null!;
    private protected LiveUpdateSignal LiveUpdateSignal { private set; get; } = null!;
    private protected TestConsole TestAnsiConsole { private set; get; } = null!;

    [TestInitialize]
    public void SetupBase()
    {
        TestAnsiConsole = new TestConsole();

        ConsoleStdOut = new OutputCapture(TestAnsiConsole.Profile.Out.Writer);
        ConsoleStdErr = new OutputCapture(Console.Error);

        // Create a temporary directory for testing
        _tempDirectory = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"{this.GetType().Name}_{Guid.NewGuid():N}"));
        _tempDirectory.Create();

        // Set up a temporary winapp directory for testing (isolates tests from real winapp directory)
        _testWinappDirectory = _tempDirectory.CreateSubdirectory(".winapp");

        var services = new ServiceCollection()
            .ConfigureServices(ConsoleStdOut)
            .ConfigureCommands();
        services =
            ConfigureServices(services)
            // Override services
            .AddSingleton<ICurrentDirectoryProvider>(sp => new CurrentDirectoryProvider(_tempDirectory.FullName))
            .AddSingleton<IAnsiConsole>(TestAnsiConsole)
            .AddLogging(b =>
            {
                b.ClearProviders();
                b.AddTextWriterLogger(ConsoleStdOut, ConsoleStdErr);
                b.SetMinimumLevel(LogLevel.Debug);
            });

        _serviceProvider = services.BuildServiceProvider();

        GroupableTask dummyTask = new GroupableTask("Dummy Task", null);

        LiveUpdateSignal = new LiveUpdateSignal();
        TestTaskContext = new TaskContext(dummyTask, null, TestAnsiConsole, GetRequiredService<ILogger<TaskContext>>(), LiveUpdateSignal);

        // Set up services with test cache directory
        if (configPaths)
        {
            _configService = GetRequiredService<IConfigService>();
            _configService.ConfigPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "winapp.yaml"));

            var directoryService = GetRequiredService<IWinappDirectoryService>();
            _testCacheDirectory = _tempDirectory.CreateSubdirectory(".winappcache");
            directoryService.SetCacheDirectoryForTesting(_testCacheDirectory);
            _buildToolsService = GetRequiredService<IBuildToolsService>();
        }
    }

    protected async Task<int> ParseAndInvokeWithCaptureAsync(Command command, string[] manifestArgs)
    {
        var parseResult = command.Parse(manifestArgs);
        parseResult.InvocationConfiguration.Output = TestAnsiConsole.Profile.Out.Writer;
        parseResult.InvocationConfiguration.Error = ConsoleStdErr;
        return await parseResult.InvokeAsync(parseResult.InvocationConfiguration, cancellationToken: TestContext.CancellationToken);
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
