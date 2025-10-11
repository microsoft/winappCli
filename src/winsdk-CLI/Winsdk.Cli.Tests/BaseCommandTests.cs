using Microsoft.Extensions.DependencyInjection;
using Winsdk.Cli.Helpers;

namespace Winsdk.Cli.Tests;

public class BaseCommandTests : IDisposable
{
    private ServiceProvider _serviceProvider;

    public BaseCommandTests()
    {
        var services = new ServiceCollection()
            .ConfigureServices()
            .ConfigureCommands();

        _serviceProvider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _serviceProvider?.Dispose();
        GC.SuppressFinalize(this);
    }

    protected T GetRequiredService<T>() where T : notnull
    {
        return _serviceProvider.GetRequiredService<T>();
    }
}
