// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="RestoreCommand"/>: argument/option parsing and delegation to
/// <see cref="IWorkspaceSetupService"/> with the expected <see cref="WorkspaceSetupOptions"/>.
/// </summary>
[TestClass]
public class RestoreCommandTests : BaseCommandTests
{
    private FakeWorkspaceSetupService _fakeWorkspaceSetupService = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeWorkspaceSetupService = new FakeWorkspaceSetupService();
        return services.AddSingleton<IWorkspaceSetupService>(_fakeWorkspaceSetupService);
    }

    // ── Parse-level tests ───────────────────────────────────────────────

    [TestMethod]
    public void Parse_NoArguments_Succeeds()
    {
        var command = GetRequiredService<RestoreCommand>();

        var parseResult = command.Parse([]);

        Assert.IsEmpty(parseResult.Errors, $"Errors: {string.Join("; ", parseResult.Errors)}");
    }

    [TestMethod]
    public void Parse_NonExistentBaseDirectory_ProducesError()
    {
        var command = GetRequiredService<RestoreCommand>();
        var missing = Path.Combine(_tempDirectory.FullName, "does-not-exist");

        var parseResult = command.Parse([missing]);

        Assert.IsNotEmpty(parseResult.Errors, "base-directory uses AcceptExistingOnly and should reject a missing directory");
    }

    [TestMethod]
    public void Parse_NonExistentConfigDir_ProducesError()
    {
        var command = GetRequiredService<RestoreCommand>();
        var missing = Path.Combine(_tempDirectory.FullName, "no-config");

        var parseResult = command.Parse(["--config-dir", missing]);

        Assert.IsNotEmpty(parseResult.Errors, "--config-dir uses AcceptExistingOnly and should reject a missing directory");
    }

    // ── Delegation tests ────────────────────────────────────────────────

    [TestMethod]
    public async Task Restore_NoArguments_DefaultsToCurrentDirectory()
    {
        var command = GetRequiredService<RestoreCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(0, exitCode);
        Assert.HasCount(1, _fakeWorkspaceSetupService.SetupWorkspaceCalls);
        var options = _fakeWorkspaceSetupService.SetupWorkspaceCalls[0];
        Assert.AreEqual(_tempDirectory.FullName, options.BaseDirectory.FullName, "BaseDirectory should default to the current directory");
        Assert.AreEqual(_tempDirectory.FullName, options.ConfigDir.FullName, "ConfigDir should default to the current directory");
    }

    [TestMethod]
    public async Task Restore_AlwaysRequiresExistingConfig_AndDoesNotForceLatestBuildTools()
    {
        var command = GetRequiredService<RestoreCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(0, exitCode);
        var options = _fakeWorkspaceSetupService.SetupWorkspaceCalls[0];
        Assert.IsTrue(options.RequireExistingConfig, "restore must require an existing winapp.yaml");
        Assert.IsFalse(options.ForceLatestBuildTools, "restore must not force latest build tools (that is 'update')");
    }

    [TestMethod]
    public async Task Restore_WithBaseDirectoryArgument_PassesItThrough()
    {
        var command = GetRequiredService<RestoreCommand>();
        var baseDir = Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, "workspace"));

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [baseDir.FullName]);

        Assert.AreEqual(0, exitCode);
        var options = _fakeWorkspaceSetupService.SetupWorkspaceCalls[0];
        Assert.AreEqual(baseDir.FullName, options.BaseDirectory.FullName);
    }

    /// <summary>
    /// `winapp restore ./my-project` must read ./my-project/winapp.yaml. `--config-dir` used to default to the
    /// process working directory even when a base directory was given, so restore looked in the wrong place and
    /// reported "nothing to restore" while exiting 0 — a silent no-op on the documented example. `init` already
    /// co-locates winapp.yaml with the selected directory, so the two commands disagreed.
    /// </summary>
    [TestMethod]
    public async Task Restore_WithBaseDirectoryArgument_DefaultsConfigDirToIt()
    {
        var command = GetRequiredService<RestoreCommand>();
        var baseDir = Directory.CreateDirectory(Path.Join(_tempDirectory.FullName, "my-project"));

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [baseDir.FullName]);

        Assert.AreEqual(0, exitCode);
        var options = _fakeWorkspaceSetupService.SetupWorkspaceCalls[0];
        Assert.AreEqual(baseDir.FullName, options.ConfigDir.FullName, "ConfigDir should follow the base directory when --config-dir is omitted");
    }

    /// <summary>
    /// An explicit `--config-dir` still wins over the base directory.
    /// </summary>
    [TestMethod]
    public async Task Restore_WithBaseDirectoryAndConfigDir_ConfigDirWins()
    {
        var command = GetRequiredService<RestoreCommand>();
        var baseDir = Directory.CreateDirectory(Path.Join(_tempDirectory.FullName, "my-project"));
        var configDir = Directory.CreateDirectory(Path.Join(_tempDirectory.FullName, "cfg"));

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [baseDir.FullName, "--config-dir", configDir.FullName]);

        Assert.AreEqual(0, exitCode);
        var options = _fakeWorkspaceSetupService.SetupWorkspaceCalls[0];
        Assert.AreEqual(baseDir.FullName, options.BaseDirectory.FullName);
        Assert.AreEqual(configDir.FullName, options.ConfigDir.FullName);
    }

    [TestMethod]
    public async Task Restore_WithConfigDirOption_PassesItThrough()
    {
        var command = GetRequiredService<RestoreCommand>();
        var configDir = Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, "cfg"));

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--config-dir", configDir.FullName]);

        Assert.AreEqual(0, exitCode);
        var options = _fakeWorkspaceSetupService.SetupWorkspaceCalls[0];
        Assert.AreEqual(configDir.FullName, options.ConfigDir.FullName);
        // base-directory was not supplied, so it defaults to the current directory
        Assert.AreEqual(_tempDirectory.FullName, options.BaseDirectory.FullName);
    }

    [TestMethod]
    public async Task Restore_PropagatesNonZeroExitCodeFromWorkspaceSetup()
    {
        var command = GetRequiredService<RestoreCommand>();
        _fakeWorkspaceSetupService.SetupWorkspaceResult = 3;

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(3, exitCode, "restore should surface the workspace setup exit code");
    }
}
