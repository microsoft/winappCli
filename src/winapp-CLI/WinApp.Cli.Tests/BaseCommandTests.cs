// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Testing;
using System.CommandLine;
using WinApp.Cli.Commands;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

public abstract class BaseCommandTests(bool configPaths = true, LogLevel logLevel = LogLevel.Debug)
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
    private protected GroupableTask TestTask { private set; get; } = null!;
    private protected Lock RenderLock { private set; get; } = null!;
    private protected TestConsole TestAnsiConsole { private set; get; } = null!;

    [TestInitialize]
    public void SetupBase()
    {
        TestAnsiConsole = new TestConsole();
        TestAnsiConsole.Profile.Capabilities.Interactive = true;

        ConsoleStdOut = new OutputCapture(TestAnsiConsole.Profile.Out.Writer);
        ConsoleStdErr = new OutputCapture(Console.Error);

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
            .AddSingleton<IAnsiConsole>(TestAnsiConsole)
            .AddLogging(b =>
            {
                b.ClearProviders();
                b.AddTextWriterLogger(ConsoleStdOut, ConsoleStdErr);
                // Use Debug level for verbose logging, Information level for non-verbose
                b.SetMinimumLevel(logLevel);
            });

        _serviceProvider = services.BuildServiceProvider();

        TestTask = new GroupableTask("Dummy Task", null);

        RenderLock = new Lock();
        TestTaskContext = new TaskContext(TestTask, null, TestAnsiConsole, GetRequiredService<ILogger<TaskContext>>(), RenderLock);

        // Set up services with test cache directory
        if (configPaths)
        {
            _configService = GetRequiredService<IConfigService>();
            _configService.ConfigPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "winapp.yaml"));

            var directoryService = GetRequiredService<IWinappDirectoryService>();
            _testCacheDirectory = _tempDirectory.CreateSubdirectory(".winappcache");
            directoryService.SetCacheDirectoryForTesting(_testCacheDirectory);

            // Wire up test cache directory for FakeNugetService if present
            if (GetRequiredService<INugetService>() is FakeNugetService fakeNuget)
            {
                fakeNuget.CacheDirectory = _testCacheDirectory;
            }

            _buildToolsService = GetRequiredService<IBuildToolsService>();
        }

        // Generated dev certificates must not accumulate in the developer's CurrentUser\My store.
        if (GetRequiredService<ICertificateService>() is CertificateService certificateService)
        {
            certificateService.PersistToCurrentUserStore = false;
        }
    }

    protected async Task<int> ParseAndInvokeWithCaptureAsync(Command command, string[] manifestArgs)
        => await ParseAndInvokeWithCaptureAsync(command, manifestArgs, TestContext.CancellationToken);

    /// <summary>
    /// Overload that invokes with a caller-supplied cancellation token (e.g. a pre-cancelled one) so
    /// tests can exercise a command's <see cref="OperationCanceledException"/> handling deterministically.
    /// </summary>
    protected async Task<int> ParseAndInvokeWithCaptureAsync(Command command, string[] manifestArgs, CancellationToken cancellationToken)
    {
        var parseResult = command.Parse(manifestArgs);
        parseResult.InvocationConfiguration.Output = TestAnsiConsole.Profile.Out.Writer;
        parseResult.InvocationConfiguration.Error = ConsoleStdErr;
        return await parseResult.InvokeAsync(parseResult.InvocationConfiguration, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Invokes <paramref name="command"/> with the process-wide Spectre ambient console
    /// (<see cref="AnsiConsole.Console"/>) temporarily redirected to a fresh <see cref="TestConsole"/>,
    /// then always restores the previous ambient console — even on failure — so global console state
    /// does not leak across tests. Use this to capture output written through the static ambient
    /// console (e.g. Info/Warning log lines that do not route through the injected stderr writer).
    /// </summary>
    /// <remarks>
    /// Callers must be <c>[DoNotParallelize]</c> because the ambient console is process-wide. The
    /// command's own stdout/stderr are still captured via <see cref="ParseAndInvokeWithCaptureAsync(Command, string[])"/>
    /// (into <see cref="TestAnsiConsole"/> / <see cref="ConsoleStdErr"/>); the returned string is the
    /// text written to the swapped-in ambient console.
    /// </remarks>
    /// <returns>The command exit code and the text captured from the ambient console.</returns>
    protected async Task<(int ExitCode, string AmbientOutput)> InvokeWithAmbientConsoleCaptureAsync(
        Command command, string[] manifestArgs)
    {
        var previousAmbient = AnsiConsole.Console;
        var ambient = new TestConsole();
        AnsiConsole.Console = ambient;
        try
        {
            int exitCode = await ParseAndInvokeWithCaptureAsync(command, manifestArgs);
            return (exitCode, ambient.Output);
        }
        finally
        {
            AnsiConsole.Console = previousAmbient;
        }
    }

    /// <summary>
    /// Invokes <see cref="Program.Main"/> with captured stdout/stderr.
    /// Writers are intentionally not disposed to avoid ObjectDisposedException from
    /// Spectre.Console's static AnsiConsole.Console which may reference them after return.
    /// </summary>
    /// <remarks>
    /// Must only be called from a <c>[DoNotParallelize]</c> test class because it redirects
    /// the process-wide <see cref="Console.Out"/> and <see cref="Console.Error"/> streams and
    /// mutates process environment variables for the duration of the call.
    /// <para>
    /// <see cref="Program.Main"/> builds its OWN service collection, so the per-test overrides
    /// wired up in <c>SetupBase</c> (SetCacheDirectoryForTesting / ConfigPath) do NOT reach it.
    /// This harness isolates the real first-run and update services the same way CI does — it
    /// redirects the global winapp directory (where the <c>.first-run-complete</c> marker lives)
    /// to a throwaway temp dir via <c>WINAPP_CLI_CACHE_DIRECTORY</c> and disables the update-check
    /// network call via <c>WINAPP_CLI_UPDATE_CHECK=0</c>. Without this, invoking Program.Main from
    /// a test would mutate the developer's real <c>~/.winapp</c> cache and could hit the network (M5).
    /// </para>
    /// </remarks>
    /// <param name="args">The argv passed to <see cref="Program.Main"/>.</param>
    /// <param name="coldCache">
    /// When <see langword="false"/> (default) the isolated cache is pre-seeded with the
    /// <c>.first-run-complete</c> marker so the first-run notice does not fire (a warm cache).
    /// Pass <see langword="true"/> to exercise the genuine first-run / cold-cache path.
    /// </param>
    protected static async Task<(string Stdout, string Stderr, int ExitCode)> InvokeProgramAsync(
        string[] args, bool coldCache = false)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var stdoutWriter = new StringWriter();
        var stderrWriter = new StringWriter();

        var isolatedCacheDir = new DirectoryInfo(
            Path.Combine(Path.GetTempPath(), $"winapp-invoke-{Guid.NewGuid():N}"));
        isolatedCacheDir.Create();

        // Pre-seed the first-run marker unless the test explicitly wants the cold-cache path, so
        // the first-run banner does not pollute the captured stdout of unrelated invocations.
        if (!coldCache)
        {
            File.WriteAllText(
                Path.Combine(isolatedCacheDir.FullName, ".first-run-complete"), string.Empty);
        }

        var originalCacheDir = Environment.GetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY");
        var originalUpdateCheck = Environment.GetEnvironmentVariable("WINAPP_CLI_UPDATE_CHECK");
        Environment.SetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY", isolatedCacheDir.FullName);
        Environment.SetEnvironmentVariable("WINAPP_CLI_UPDATE_CHECK", "0");

        try
        {
            Console.SetOut(stdoutWriter);
            Console.SetError(stderrWriter);
            var exitCode = await Program.Main(args);
            return (stdoutWriter.ToString(), stderrWriter.ToString(), exitCode);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            Environment.SetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY", originalCacheDir);
            Environment.SetEnvironmentVariable("WINAPP_CLI_UPDATE_CHECK", originalUpdateCheck);
            try
            {
                isolatedCacheDir.Delete(recursive: true);
            }
            catch
            {
                // Best-effort cleanup; a leaked temp dir must never fail a test.
            }
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

    /// <summary>
    /// Ensures a single NuGet package is available in the test NuGet cache by copying it
    /// from the real global NuGet cache if available, falling back to downloading from NuGet.org.
    /// <para>
    /// This avoids expensive HTTP downloads that can timeout (100 s default) when many tests
    /// run in parallel (12-way method-level parallelism) and all try to download large packages
    /// like <c>Microsoft.WindowsAppSDK.Runtime</c> simultaneously.
    /// </para>
    /// </summary>
    protected async Task EnsurePackageInTestCacheAsync(string packageId, string version, CancellationToken cancellationToken)
    {
        var nugetService = GetRequiredService<INugetService>();
        var testPackagesDir = nugetService.GetNuGetGlobalPackagesDir();
        var targetDir = new DirectoryInfo(Path.Combine(testPackagesDir.FullName, packageId.ToLowerInvariant(), version));

        if (targetDir.Exists)
        {
            return;
        }

        // Try to copy from the real NuGet cache (fast, no network needed).
        // For EndToEndTests, 'dotnet build' already downloads packages here.
        // For PackageCommandTests, previous test runs will have cached them.
        var realCachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages", packageId.ToLowerInvariant(), version);
        var realCacheDir = new DirectoryInfo(realCachePath);

        if (realCacheDir.Exists)
        {
            CopyDirectoryRecursive(realCacheDir, targetDir);
            return;
        }

        // Fallback: download from NuGet.org
        var packageInstallService = GetRequiredService<IPackageInstallationService>();
        await packageInstallService.EnsurePackageAsync(
            _testCacheDirectory, packageId, TestTaskContext,
            version: version, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Recursively copies a directory and all its contents to a new location.
    /// </summary>
    private static void CopyDirectoryRecursive(DirectoryInfo source, DirectoryInfo target)
    {
        target.Create();
        foreach (var file in source.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(source.FullName, file.FullName);
            var destPath = Path.Combine(target.FullName, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            file.CopyTo(destPath, overwrite: true);
        }
    }

    /// <summary>
    /// Push default (Enter) answers for manifest prompts (packageName, publisherName, version, description)
    /// </summary>
    protected void DefaultAnswers()
    {
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);
    }
}
