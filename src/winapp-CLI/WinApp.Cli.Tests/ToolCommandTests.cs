// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;
using WinApp.Cli.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="ToolCommand"/>. The command forwards unmatched tokens to a Windows SDK
/// build tool. A fake <see cref="IBuildToolsService"/> resolves the tool to a harmless real
/// executable (cmd.exe) so exit-code propagation is exercised without downloading Build Tools,
/// and can be configured to throw to cover each error branch.
/// </summary>
[TestClass]
[DoNotParallelize] // Redirects the process-wide Console streams to capture forwarded tool output
public class ToolCommandTests : BaseCommandTests
{
    private FakeToolBuildToolsService _fakeBuildTools = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeBuildTools = new FakeToolBuildToolsService();
        return services.AddSingleton<IBuildToolsService>(_fakeBuildTools);
    }

    [TestMethod]
    public void ToolCommand_HasRunBuildToolAlias()
    {
        var command = GetRequiredService<ToolCommand>();

        Assert.AreEqual("tool", command.Name);
        Assert.Contains("run-buildtool", command.Aliases);
    }

    [TestMethod]
    public async Task Tool_NoArguments_ReturnsUsageError()
    {
        var command = GetRequiredService<ToolCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(1, exitCode, "Running 'tool' with no sub-command should be a usage error");
        var stderr = ConsoleStdErr.ToString();
        StringAssert.Contains(stderr, "No build tool command specified");
    }

    [TestMethod]
    public async Task Tool_SuccessfulTool_ReturnsZero()
    {
        var command = GetRequiredService<ToolCommand>();

        // Fake resolves "cmd" to cmd.exe; "/c exit 0" makes it exit 0.
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["cmd", "/c", "exit", "0"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual("cmd", _fakeBuildTools.LastRequestedTool, "toolName should be the first unmatched token");
    }

    [TestMethod]
    public async Task Tool_PropagatesToolExitCode()
    {
        var command = GetRequiredService<ToolCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["cmd", "/c", "exit", "7"]);

        Assert.AreEqual(7, exitCode, "The launched tool's exit code should be propagated");
    }

    [TestMethod]
    public async Task Tool_QuotesArgumentsContainingSpaces()
    {
        // A forwarded argument containing a space must be re-quoted so it reaches the tool as ONE
        // token. cmd's echo prints its command line verbatim, so with correct quoting the value
        // comes back wrapped in the quotes the command added ("hello world"); a broken-quoting
        // regression would split it and echo the bare words with no surrounding quotes.
        var (exitCode, stdout, _) = await InvokeToolCapturingConsoleAsync(
            ["cmd", "/c", "echo", "hello world"], expectStdout: "\"hello world\"");

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(stdout, "\"hello world\"",
            "The space-containing argument must be forwarded to the tool as a single quoted token");
    }

    [TestMethod]
    public async Task Tool_ToolNotFound_ReturnsErrorWithGuidance()
    {
        var command = GetRequiredService<ToolCommand>();
        _fakeBuildTools.ExceptionToThrow = new FileNotFoundException("nope");

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["makeappx", "pack"]);

        Assert.AreEqual(1, exitCode);
        var stderr = ConsoleStdErr.ToString();
        StringAssert.Contains(stderr, "Could not find 'makeappx'");
    }

    [TestMethod]
    public async Task Tool_BuildToolsInstallFails_ReturnsError()
    {
        var command = GetRequiredService<ToolCommand>();
        _fakeBuildTools.ExceptionToThrow = new InvalidOperationException("install broke");

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["signtool", "sign"]);

        Assert.AreEqual(1, exitCode);
        var stderr = ConsoleStdErr.ToString();
        StringAssert.Contains(stderr, "Could not install or find Windows SDK Build Tools");
    }

    [TestMethod]
    public async Task Tool_SignatureRefused_ReportsRefusalWithoutTheInstallationPrefix()
    {
        var command = GetRequiredService<ToolCommand>();
        _fakeBuildTools.ExceptionToThrow = new BuildToolSignatureException(
            "'mt.exe' is not validly signed by Microsoft, so it was not run (C:\\tools\\mt.exe).");

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["mt", "-manifest"]);

        Assert.AreEqual(1, exitCode);
        var stderr = ConsoleStdErr.ToString();
        StringAssert.Contains(stderr, "is not validly signed by Microsoft");
        Assert.IsFalse(
            stderr.Contains("Could not install or find", StringComparison.Ordinal),
            "The tool was found and refused, so reporting it as missing would send users the wrong way.");
    }

    [TestMethod]
    public async Task Tool_UnexpectedError_ReturnsError()
    {
        var command = GetRequiredService<ToolCommand>();
        _fakeBuildTools.ExceptionToThrow = new Exception("boom");

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["makepri", "new"]);

        Assert.AreEqual(1, exitCode);
        var stderr = ConsoleStdErr.ToString();
        StringAssert.Contains(stderr, "Error executing 'makepri'");
    }

    [TestMethod]
    public async Task Tool_ForwardsToolStandardOutput()
    {
        // cmd.exe writes "hello" to stdout; the command's OutputDataReceived handler forwards it to
        // Console.Out, which we redirect and capture here.
        var (exitCode, stdout, _) = await InvokeToolCapturingConsoleAsync(
            ["cmd", "/c", "echo", "hello"], expectStdout: "hello");

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(stdout, "hello", "The tool's standard output should be forwarded to the console");
    }

    [TestMethod]
    public async Task Tool_ForwardsToolStandardError()
    {
        // Redirect echo to stderr (1>&2) so the ErrorDataReceived handler forwards it to
        // Console.Error, which we redirect and capture here.
        var (exitCode, _, stderr) = await InvokeToolCapturingConsoleAsync(
            ["cmd", "/c", "echo", "oops", "1>&2"], expectStderr: "oops");

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(stderr, "oops", "The tool's standard error should be forwarded to the console");
    }

    /// <summary>
    /// Invokes the tool command while temporarily redirecting the process-wide
    /// <see cref="Console"/> streams, so the output the command forwards from the launched tool
    /// (via <c>Console.Out</c>/<c>Console.Error</c>, not the logger) can be asserted. The forwarding
    /// callbacks run on threadpool threads and can lag the process exit, so we poll for the expected
    /// text before restoring the console to avoid a late write landing on the original stream. The
    /// class is <c>[DoNotParallelize]</c> because the redirected console is process-global state.
    /// </summary>
    private async Task<(int ExitCode, string Stdout, string Stderr)> InvokeToolCapturingConsoleAsync(
        string[] args, string? expectStdout = null, string? expectStderr = null)
    {
        var command = GetRequiredService<ToolCommand>();
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var stdout = new SyncStringWriter();
        var stderr = new SyncStringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exitCode = await ParseAndInvokeWithCaptureAsync(command, args);
            for (var i = 0; i < 200; i++)
            {
                var haveOut = expectStdout == null || stdout.Snapshot().Contains(expectStdout, StringComparison.Ordinal);
                var haveErr = expectStderr == null || stderr.Snapshot().Contains(expectStderr, StringComparison.Ordinal);
                if (haveOut && haveErr)
                {
                    break;
                }

                await Task.Delay(25);
            }

            return (exitCode, stdout.Snapshot(), stderr.Snapshot());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    /// <summary>
    /// Thread-safe <see cref="StringWriter"/> so the test can snapshot captured output while the
    /// tool's output-forwarding callbacks may still be writing on a threadpool thread.
    /// </summary>
    private sealed class SyncStringWriter : StringWriter
    {
        private readonly object _gate = new();

        public override void Write(char value)
        {
            lock (_gate)
            {
                base.Write(value);
            }
        }

        public override void Write(string? value)
        {
            lock (_gate)
            {
                base.Write(value);
            }
        }

        public override void Write(char[] buffer, int index, int count)
        {
            lock (_gate)
            {
                base.Write(buffer, index, count);
            }
        }

        public string Snapshot()
        {
            lock (_gate)
            {
                return base.ToString();
            }
        }
    }

    /// <summary>
    /// Fake build-tools service for the tool command. Resolves any tool name to a configurable
    /// real executable (cmd.exe by default) so a harmless process can be launched, or throws a
    /// configured exception to exercise the command's error branches.
    /// </summary>
    private sealed class FakeToolBuildToolsService : IBuildToolsService
    {
        public string? LastRequestedTool { get; private set; }
        public Exception? ExceptionToThrow { get; set; }
        public FileInfo ResolvedTool { get; set; } = new(Path.Combine(Environment.SystemDirectory, "cmd.exe"));

        public FileInfo? GetBuildToolPath(string toolName) => ResolvedTool;

        public VerifiedTool OpenVerifiedTool(FileInfo toolPath)
            => VerifiedTool.Open(toolPath, static (_, _) => true, NullLogger.Instance);

        public Task<FileInfo> EnsureBuildToolAvailableAsync(string toolName, TaskContext taskContext, CancellationToken cancellationToken = default)
        {
            LastRequestedTool = toolName;
            if (ExceptionToThrow != null)
            {
                throw ExceptionToThrow;
            }
            return Task.FromResult(ResolvedTool);
        }

        public Task<DirectoryInfo?> EnsureBuildToolsAsync(TaskContext taskContext, bool forceLatest = false, CancellationToken cancellationToken = default)
            => Task.FromResult<DirectoryInfo?>(null);

        public Task<(string stdout, string stderr)> RunBuildToolAsync(Tool tool, string arguments, TaskContext taskContext, bool printErrors = true, FileInfo? toolPathOverride = null, IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null, CancellationToken cancellationToken = default)
            => Task.FromResult((string.Empty, string.Empty));
    }
}
