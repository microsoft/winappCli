// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Covers the defensive catch-all error path in <see cref="GetWinappPathCommand"/>.
/// A throwing <see cref="IWinappDirectoryService"/> is injected so the handler's
/// try/catch converts the failure into a non-zero exit code and a stderr message.
/// </summary>
[TestClass]
public class GetWinappPathCommandErrorTests : BaseCommandTests
{
    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        // Registered last so it wins over the real service from the host builder.
        return services.AddSingleton<IWinappDirectoryService>(new ThrowingWinappDirectoryService());
    }

    [TestMethod]
    public async Task GetWinappPath_WhenServiceThrows_ReturnsErrorExitCode()
    {
        // Arrange — no --global, so the handler resolves the LOCAL directory, which throws.
        var command = GetRequiredService<GetWinappPathCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        // Assert — the exception is caught and surfaced as a failure (not an unhandled crash).
        Assert.AreEqual(1, exitCode, "A failure resolving the directory should return exit code 1.");
        StringAssert.Contains(ConsoleStdErr.ToString(), "Error getting local winapp directory",
            "The catch handler should log a descriptive error to stderr.");
    }

    [TestMethod]
    public async Task GetWinappPath_Global_WhenServiceThrows_ReturnsErrorExitCode()
    {
        // Arrange — --global routes to GetGlobalWinappDirectory, which also throws.
        var command = GetRequiredService<GetWinappPathCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--global"]);

        // Assert
        Assert.AreEqual(1, exitCode, "A failure resolving the global directory should return exit code 1.");
        StringAssert.Contains(ConsoleStdErr.ToString(), "Error getting global winapp directory",
            "The catch handler should name the global directory in its error message.");
    }

    private sealed class ThrowingWinappDirectoryService : IWinappDirectoryService
    {
        public bool IsGlobalCacheOverridden => true;
        public DirectoryInfo GetLocalCacheDirectory() => throw new InvalidOperationException("simulated cache failure");
        public DirectoryInfo GetGlobalWinappDirectory()
            => throw new InvalidOperationException("simulated failure resolving the global directory");

        public DirectoryInfo GetLocalWinappDirectory(DirectoryInfo? baseDirectory = null)
            => throw new InvalidOperationException("simulated failure resolving the local directory");

        public void SetCacheDirectoryForTesting(DirectoryInfo? cacheDirectory)
        {
        }
    }
}
