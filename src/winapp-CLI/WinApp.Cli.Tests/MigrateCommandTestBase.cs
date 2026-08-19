// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using System.CommandLine;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Shared base for the <c>winapp migrate</c> command tests.
/// </summary>
/// <remarks>
/// The migrate commands write to <see cref="System.Console.Out"/> directly (winapp ships as
/// NativeAOT and these commands predate the injected <c>IAnsiConsole</c> plumbing), so their
/// stdout is NOT captured by the base <c>TestAnsiConsole</c>. We redirect <see cref="Console.Out"/>
/// around the invocation, serialized through a process-wide gate so the method-level-parallel
/// migrate tests don't clobber each other's redirection. This is safe because no other command
/// test writes to <see cref="Console.Out"/>.
/// </remarks>
public abstract class MigrateCommandTestBase : BaseCommandTests
{
    private static readonly SemaphoreSlim ConsoleGate = new(1, 1);

    private protected FakeDotNetService FakeDotNet { get; } = new();

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
        => services.AddSingleton<IDotNetService>(FakeDotNet);

    private protected async Task<(int ExitCode, string Output)> InvokeCapturingConsoleAsync(Command command, params string[] args)
    {
        await ConsoleGate.WaitAsync(TestContext.CancellationToken);
        var original = Console.Out;
        using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            var exit = await ParseAndInvokeWithCaptureAsync(command, args);
            return (exit, writer.ToString());
        }
        finally
        {
            Console.SetOut(original);
            ConsoleGate.Release();
        }
    }

    private protected async Task<DirectoryInfo> CreateProjectDirAsync(string name, string csprojName = "App.csproj")
    {
        var dir = _tempDirectory.CreateSubdirectory(name);
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, csprojName), CleanCsproj, TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "MainWindow.xaml"),
            "<Window><Grid><Frame x:Name=\"RootFrame\" /></Grid></Window>", TestContext.CancellationToken);
        return dir;
    }

    private protected const string CleanCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>WinExe</OutputType>
            <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
            <UseWinUI>true</UseWinUI>
          </PropertyGroup>
        </Project>
        """;
}
