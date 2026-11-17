// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="MSStoreCommand"/>. The command ensures the Microsoft Store Developer CLI
/// is available and then forwards unmatched tokens to it. A fake <see cref="IMSStoreCLIService"/>
/// points the launcher at cmd.exe so exit-code propagation and the failure branch are exercised
/// without downloading the real CLI.
/// </summary>
[TestClass]
public class MSStoreCommandTests : BaseCommandTests
{
    private FakeMSStoreCLIService _fakeMSStore = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeMSStore = new FakeMSStoreCLIService();
        return services.AddSingleton<IMSStoreCLIService>(_fakeMSStore);
    }

    [TestMethod]
    public async Task Store_EnsuresCliAvailableAndPropagatesSuccess()
    {
        var command = GetRequiredService<MSStoreCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["/c", "exit", "0"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeMSStore.EnsureAvailableCallCount, "The CLI availability check should run before launching");
    }

    [TestMethod]
    public async Task Store_PropagatesNonZeroExitCode()
    {
        var command = GetRequiredService<MSStoreCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["/c", "exit", "4"]);

        Assert.AreEqual(4, exitCode, "The store CLI's exit code should be propagated");
    }

    [TestMethod]
    public async Task Store_EnsureAvailableThrows_ReturnsError()
    {
        var command = GetRequiredService<MSStoreCommand>();
        _fakeMSStore.EnsureException = new InvalidOperationException("download failed");

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["reltest"]);

        Assert.AreEqual(1, exitCode);
        var stderr = ConsoleStdErr.ToString();
        Assert.Contains("Error executing MSStoreCLI", stderr);
    }

    [TestMethod]
    public async Task Store_LaunchFailure_ReturnsError()
    {
        var command = GetRequiredService<MSStoreCommand>();
        // A non-existent executable path makes Process.Start throw, hitting the catch branch.
        _fakeMSStore.CliPath = Path.Combine(_tempDirectory.FullName, "does-not-exist.exe");

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["whoami"]);

        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Store_ProcessStartReturnsNull_ReturnsError()
    {
        // Defensive branch: if starting the store CLI process returns null, the command reports a
        // start failure. Process.Start only returns null in edge cases that cannot be reproduced
        // with UseShellExecute=false, so a test seam isolates that single OS boundary.
        var command = GetRequiredService<MSStoreCommand>();
        var handler = GetRequiredService<MSStoreCommand.Handler>();
        handler.ProcessStarter = _ => null;

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["whoami"]);

        Assert.AreEqual(1, exitCode);
        Assert.Contains("Failed to start process for MSStoreCLI", ConsoleStdErr.ToString());
    }
}
