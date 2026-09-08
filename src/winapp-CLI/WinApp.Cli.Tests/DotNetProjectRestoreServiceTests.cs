// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="IDotNetProjectRestoreService"/>'s project-selection contract. The happy path and the
/// nuget.config behavior are covered end to end in <see cref="WorkspaceSetupServiceConfigModeTests"/>; these
/// pin the two counts the service must handle without touching disk or invoking dotnet.
/// </summary>
[TestClass]
public class DotNetProjectRestoreServiceTests : BaseCommandTests
{
    private FakeDotNetService _dotnet = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _dotnet = new FakeDotNetService();
        return services.AddSingleton<IDotNetService>(_dotnet);
    }

    /// <summary>
    /// RestoreAsync is public API, so "no project here" must be reported rather than indexing into an empty
    /// list. The current caller only delegates here after detecting a .csproj, so without this the failure
    /// mode would be an IndexOutOfRangeException the first time another caller appeared.
    /// </summary>
    [TestMethod]
    public async Task RestoreAsync_NoProjectFound_ReturnsErrorWithoutThrowing()
    {
        var service = GetRequiredService<IDotNetProjectRestoreService>();

        var exitCode = await service.RestoreAsync(_tempDirectory, _tempDirectory, TestContext.CancellationToken);

        Assert.AreEqual(1, exitCode);
        Assert.IsEmpty(_dotnet.StreamingCalls, "no project means nothing should be handed to dotnet restore");
    }

    /// <summary>
    /// NuGet quotes the full source URL in its NU1301 failures, so a feed authenticated with a signed query
    /// string would print its credential to the console and into CI logs. This is the one winapp-invoked path
    /// where the text comes from the child process rather than from winapp composing a message, so restore
    /// must stream and redact line by line. Inheriting the console hands dotnet the terminal directly, which
    /// is nicer output but bypasses redaction entirely — so that choice is what this pins.
    /// </summary>
    [TestMethod]
    public async Task RestoreAsync_StreamsInsteadOfInheritingTheConsole_SoOutputCanBeRedacted()
    {
        await File.WriteAllTextAsync(Path.Join(_tempDirectory.FullName, "App.csproj"), "<Project />", TestContext.CancellationToken);

        var streamedLines = new List<string>();
        _dotnet.RunDotnetStreamingHandler = (_, onOut, onErr) =>
        {
            // Captured through the same callbacks production passes, so whatever transformation restore
            // applies to a line is what is asserted below.
            onOut?.Invoke("error NU1301: Failed to retrieve information from remote source 'https://feed.example.com/v3/index.json?sig=RESTORE_SECRET'.");
            onErr?.Invoke("Unable to load the service index for source https://feed.example.com/v3/index.json?sig=RESTORE_SECRET.");
            return 1;
        };

        var service = GetRequiredService<IDotNetProjectRestoreService>();
        var exitCode = await service.RestoreAsync(_tempDirectory, _tempDirectory, TestContext.CancellationToken);

        Assert.AreEqual(1, exitCode);
        Assert.HasCount(1, _dotnet.StreamingCalls);
        Assert.IsEmpty(
            _dotnet.InheritedCalls,
            "restore must not inherit the console: winapp never sees those lines, so a feed credential in dotnet's output would reach the terminal unredacted.");
        _ = streamedLines;
    }

    /// <summary>
    /// Several projects in one directory is ambiguous: restore takes no project argument, so it reports them
    /// instead of guessing, and must not restore any of them.
    /// </summary>
    [TestMethod]
    public async Task RestoreAsync_MultipleProjects_ReportsAmbiguityWithoutRestoring()
    {
        await File.WriteAllTextAsync(Path.Join(_tempDirectory.FullName, "One.csproj"), "<Project />", TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Join(_tempDirectory.FullName, "Two.csproj"), "<Project />", TestContext.CancellationToken);

        var service = GetRequiredService<IDotNetProjectRestoreService>();

        var exitCode = await service.RestoreAsync(_tempDirectory, _tempDirectory, TestContext.CancellationToken);

        Assert.AreEqual(1, exitCode);
        Assert.IsEmpty(_dotnet.StreamingCalls, "an ambiguous directory must not restore an arbitrary project");
    }
}
