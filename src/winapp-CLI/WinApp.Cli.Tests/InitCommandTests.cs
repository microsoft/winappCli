// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for the InitCommand including SDK installation mode handling
/// and project detection/selection behavior
/// </summary>
[TestClass]
public class InitCommandTests : BaseCommandTests
{
    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        return services;
    }

    /// <summary>
    /// Pushes keys to answer "yes" to the "no project detected" confirmation prompt.
    /// The prompt uses DefaultValue=false, so we must push 'y' then Enter.
    /// </summary>
    private void PushConfirmYes()
    {
        TestAnsiConsole.Input.PushKey(ConsoleKey.Y);
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);
    }

    /// <summary>
    /// Pushes keys to answer "no" (the default) to the "no project detected" confirmation prompt.
    /// </summary>
    private void PushConfirmNo()
    {
        TestAnsiConsole.Input.PushKey(ConsoleKey.N);
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);
    }

    [TestMethod]
    public async Task InitCommand_WithConfigOnly_CreatesConfigFile()
    {
        // Arrange
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--config-only" };

        // Explicit directory with no project files triggers a confirmation prompt
        PushConfirmYes();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Init command should complete successfully");

        // Verify winapp.yaml was created in the config directory
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        Assert.IsTrue(File.Exists(configPath), $"winapp.yaml should be created at {configPath}");

        // Verify config contains packages section
        var configContent = await File.ReadAllTextAsync(configPath);
        Assert.Contains("packages:", configContent, "Config should contain packages section");
    }

    [TestMethod]
    public async Task InitCommand_WithSetupSdksNone_CompletesSuccessfully()
    {
        // Arrange — --no-prompt is an alias for --use-defaults, so no prompts are shown
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--setup-sdks", "none", "--no-prompt" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Init command should complete successfully");

        // When SdkInstallMode is None, the command returns early after "Configuration processed"
        // The .winapp directory and config file are NOT created (this is by design)
    }

    [TestMethod]
    public async Task InitCommand_WithNoGitignore_DoesNotModifyGitignore()
    {
        // Arrange
        var gitignorePath = Path.Combine(_tempDirectory.FullName, ".gitignore");
        await File.WriteAllTextAsync(gitignorePath, "# Original content\n*.log");

        var initCommand = GetRequiredService<InitCommand>();
        // Use config-only to avoid long-running SDK installation
        var args = new[] { _tempDirectory.FullName, "--config-only", "--no-gitignore" };

        // Explicit directory with no project files triggers a confirmation prompt
        PushConfirmYes();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Init command should complete successfully");

        // Verify .gitignore was not modified
        var gitignoreContent = await File.ReadAllTextAsync(gitignorePath);
        Assert.DoesNotContain(".winapp", gitignoreContent, ".gitignore should not contain .winapp when --no-gitignore is used");
    }

    [TestMethod]
    public async Task InitCommand_WithConfigDir_CreatesConfigInSpecifiedDirectory()
    {
        // Arrange
        var configDir = _tempDirectory.CreateSubdirectory("config");
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--config-dir", configDir.FullName, "--config-only" };

        // Explicit directory with no project files triggers a confirmation prompt
        PushConfirmYes();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Init command should complete successfully");

        // Verify winapp.yaml was created in the specified config directory
        var configPath = Path.Combine(configDir.FullName, "winapp.yaml");
        Assert.IsTrue(File.Exists(configPath), $"winapp.yaml should be created at {configPath}");
    }

    [TestMethod]
    public async Task InitCommand_ConfigOnly_ExistingConfigValidated()
    {
        // Arrange - Create existing config
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        await File.WriteAllTextAsync(configPath, @"packages:
  - name: Microsoft.Windows.SDK.BuildTools
    version: 10.0.26100.1
");

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--config-only" };

        // Explicit directory with existing config but no project triggers a confirmation prompt
        PushConfirmYes();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Init command should complete successfully");

        // Verify existing config was not overwritten (same content)
        var configContent = await File.ReadAllTextAsync(configPath);
        Assert.Contains("10.0.26100.1", configContent, "Existing config version should be preserved");
    }

    [TestMethod]
    public async Task InitCommand_DoesNotGenerateCertificate()
    {
        // Arrange — --no-prompt skips all interactive prompts
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--setup-sdks", "none", "--no-prompt" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Init command should complete successfully");

        // Verify that no devcert.pfx was created - init should not generate certificates
        var certPath = Path.Combine(_tempDirectory.FullName, "devcert.pfx");
        Assert.IsFalse(File.Exists(certPath), "Init should not generate devcert.pfx - certificates should be generated separately with 'cert generate'");
    }

    // --- Project detection & selection behavior tests ---

    [TestMethod]
    public async Task InitCommand_ExplicitDirectory_SkipsSearchAndInitsDirectly()
    {
        // Arrange — create a .csproj in a subdirectory (would be found by search)
        // but pass the root directory explicitly — search should be skipped
        var subDir = _tempDirectory.CreateSubdirectory("nested");
        File.WriteAllText(Path.Combine(subDir.FullName, "MyApp.csproj"), "<Project />");

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--config-only" };

        // No project at root → confirmation prompt
        PushConfirmYes();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert — should init in the specified directory (not the nested one)
        Assert.AreEqual(0, exitCode, "Init should complete successfully");
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        Assert.IsTrue(File.Exists(configPath), "winapp.yaml should be created in the explicitly specified directory");
    }

    [TestMethod]
    public async Task InitCommand_ExplicitDirectory_NoProject_UserDeclines_ReturnsNonZero()
    {
        // Arrange — empty directory, no project
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--config-only" };

        // Decline the "no project detected" confirmation prompt
        PushConfirmNo();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert — user cancelled, should return non-zero
        Assert.AreEqual(1, exitCode, "Init should return 1 when user cancels");
    }

    [TestMethod]
    public async Task InitCommand_UseDefaults_NoProject_NoDirectory_UsesCurrentDirectory()
    {
        // Arrange — empty directory, no project markers, no explicit directory argument
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { "--use-defaults", "--config-only" };

        // Act — --use-defaults without explicit directory uses cwd directly
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert — should succeed using cwd (preserves pre-existing behavior)
        Assert.AreEqual(0, exitCode, "Init with --use-defaults should succeed using cwd even when no project is detected");
    }

    [TestMethod]
    public async Task InitCommand_ExplicitDirectory_WithProject_NoConfirmationPrompt()
    {
        // Arrange — create a .csproj so the project is detected at the explicit path
        File.WriteAllText(Path.Combine(_tempDirectory.FullName, "MyApp.csproj"), """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
          </PropertyGroup>
        </Project>
        """);

        var initCommand = GetRequiredService<InitCommand>();
        // Use --no-prompt to skip workspace setup prompts (TFM update, etc.)
        var args = new[] { _tempDirectory.FullName, "--config-only", "--no-prompt" };

        // Act — no "no project detected" confirmation prompt since project is found;
        // --no-prompt skips all subsequent workspace setup prompts
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert — should succeed without needing the no-project confirmation prompt
        Assert.AreEqual(0, exitCode, "Init should complete successfully when project is detected");
    }

    [TestMethod]
    public async Task InitCommand_UseDefaults_MultipleProjects_UsesCurrentDirectory()
    {
        // Arrange — create two project markers in subdirectories so detection would find them
        var subDir1 = _tempDirectory.CreateSubdirectory("app1");
        File.WriteAllText(Path.Combine(subDir1.FullName, "App1.csproj"), """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><OutputType>Exe</OutputType></PropertyGroup>
        </Project>
        """);
        var subDir2 = _tempDirectory.CreateSubdirectory("app2");
        File.WriteAllText(Path.Combine(subDir2.FullName, "Cargo.toml"), "");

        var initCommand = GetRequiredService<InitCommand>();
        // --use-defaults without explicit directory — uses cwd directly
        var args = new[] { "--use-defaults", "--config-only" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert — should succeed using cwd (no detection gating with --use-defaults)
        Assert.AreEqual(0, exitCode, "Init with --use-defaults should succeed using cwd regardless of detected projects");
    }

    [TestMethod]
    public async Task InitCommand_NoArgs_SingleNestedProject_UserAccepts_InitsInProjectDir()
    {
        // Arrange — create a Rust project in a nested directory
        var subDir = _tempDirectory.CreateSubdirectory("my-rust-app");
        File.WriteAllText(Path.Combine(subDir.FullName, "Cargo.toml"), "[package]\nname = \"test\"");

        var initCommand = GetRequiredService<InitCommand>();
        // No explicit directory argument — triggers interactive search
        var args = new[] { "--config-only" };

        // Accept the single-project confirmation prompt
        PushConfirmYes();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert — should init in the nested project directory
        Assert.AreEqual(0, exitCode, "Init should complete successfully");
        var configPath = Path.Combine(subDir.FullName, "winapp.yaml");
        Assert.IsTrue(File.Exists(configPath),
            $"winapp.yaml should be created in the nested project directory: {subDir.FullName}");
    }

    [TestMethod]
    public async Task InitCommand_NoArgs_SingleNestedProject_UserDeclines_Returns1()
    {
        // Arrange — create a project in a nested directory
        var subDir = _tempDirectory.CreateSubdirectory("my-app");
        File.WriteAllText(Path.Combine(subDir.FullName, "Cargo.toml"), "[package]\nname = \"test\"");

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { "--config-only" };

        // Decline the single-project confirmation prompt
        PushConfirmNo();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert — user cancelled
        Assert.AreEqual(1, exitCode, "Init should return 1 when user declines");
    }

    [TestMethod]
    public async Task InitCommand_NoArgs_MultipleProjects_UseDefaults_UsesCurrentDirectory()
    {
        // Arrange — create two projects in nested directories; the current dir has no project
        var subDir1 = _tempDirectory.CreateSubdirectory("app1");
        File.WriteAllText(Path.Combine(subDir1.FullName, "Cargo.toml"), "[package]\nname = \"app1\"");
        var subDir2 = _tempDirectory.CreateSubdirectory("app2");
        File.WriteAllText(Path.Combine(subDir2.FullName, "Cargo.toml"), "[package]\nname = \"app2\"");

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { "--use-defaults", "--config-only" };

        // Act — --use-defaults without explicit dir uses cwd directly
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert — should succeed using cwd (no detection gating)
        Assert.AreEqual(0, exitCode, "Init with --use-defaults should succeed using cwd regardless of multiple detected projects");
    }

    [TestMethod]
    public async Task InitCommand_NoArgs_NestedProject_ConfigPlacedInProjectDir()
    {
        // Arrange — create a project in a nested directory
        var subDir = _tempDirectory.CreateSubdirectory("my-app");
        File.WriteAllText(Path.Combine(subDir.FullName, "Cargo.toml"), "[package]\nname = \"test\"");

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { "--config-only" };

        // Accept the single-project confirmation prompt
        PushConfirmYes();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert — should init in nested dir (config-dir relocated) and show cd reminder
        Assert.AreEqual(0, exitCode, "Init should complete successfully");
        var configPath = Path.Combine(subDir.FullName, "winapp.yaml");
        Assert.IsTrue(File.Exists(configPath),
            "winapp.yaml should be created in the nested project directory (config-dir auto-relocated)");
        // The cd reminder is emitted via LogInformation → static AnsiConsole (not capturable in TestConsole)
        // but the key behavior is that config was placed in the nested dir, not the root
        var rootConfig = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        Assert.IsFalse(File.Exists(rootConfig),
            "winapp.yaml should NOT be in root when a nested project was selected");
    }

    [TestMethod]
    public async Task InitCommand_NonInteractiveShell_UsesDefaultsWithoutPrompting()
    {
        // Arrange — simulate a non-interactive environment (piped stdin, CI)
        TestAnsiConsole.Profile.Capabilities.Interactive = false;

        var initCommand = GetRequiredService<InitCommand>();
        // No --use-defaults, no explicit directory — would normally prompt interactively
        var args = new[] { "--config-only" };

        // Do NOT push any keys — if it tries to prompt, it will throw/hang

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert — should succeed using defaults (same as --use-defaults behavior)
        Assert.AreEqual(0, exitCode, "Init should succeed in non-interactive mode without prompting");

        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        Assert.IsTrue(File.Exists(configPath),
            "winapp.yaml should be created using defaults in non-interactive mode");
    }

    [TestMethod]
    public async Task InitCommand_NonInteractiveShell_WithNestedProject_UsesCurrentDirectory()
    {
        // Arrange — non-interactive with a nested project (would normally prompt to confirm)
        TestAnsiConsole.Profile.Capabilities.Interactive = false;

        var subDir = _tempDirectory.CreateSubdirectory("my-app");
        File.WriteAllText(Path.Combine(subDir.FullName, "Cargo.toml"), "[package]\nname = \"test\"");

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { "--config-only" };

        // Act — should not prompt, should use cwd directly
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert — uses cwd (--use-defaults behavior), not the nested project
        Assert.AreEqual(0, exitCode, "Init should succeed in non-interactive mode");

        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        Assert.IsTrue(File.Exists(configPath),
            "winapp.yaml should be created in cwd when non-interactive");
    }

    // ── Interactive selection / confirmation prompts (no --use-defaults) ──

    [TestMethod]
    public async Task InitCommand_NoArgs_MultipleProjects_Interactive_SelectsFirstProject()
    {
        // Arrange — two nested projects, cwd itself has no project. With no --use-defaults
        // and an interactive console, the multi-project SelectionPrompt is shown.
        var subDir1 = _tempDirectory.CreateSubdirectory("app1");
        File.WriteAllText(Path.Combine(subDir1.FullName, "Cargo.toml"), "[package]\nname = \"app1\"");
        var subDir2 = _tempDirectory.CreateSubdirectory("app2");
        File.WriteAllText(Path.Combine(subDir2.FullName, "Cargo.toml"), "[package]\nname = \"app2\"");

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { "--config-only" };

        // Select the first offered project (Enter confirms the default highlighted choice).
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert — succeeds and places the config in one of the selected project directories,
        // not in the current directory (proving the selection was routed to a project).
        Assert.AreEqual(0, exitCode, "Init should succeed after selecting a project from the list");
        var cwdConfig = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        Assert.IsFalse(File.Exists(cwdConfig), "Config should not be placed in cwd when a project was selected");
        var projectConfigCount =
            (File.Exists(Path.Combine(subDir1.FullName, "winapp.yaml")) ? 1 : 0) +
            (File.Exists(Path.Combine(subDir2.FullName, "winapp.yaml")) ? 1 : 0);
        Assert.AreEqual(1, projectConfigCount, "winapp.yaml should be created in exactly one selected project directory");
    }

    [TestMethod]
    public async Task InitCommand_NoArgs_MultipleProjects_Interactive_SelectsCurrentDirectoryFallback()
    {
        // Arrange — two nested projects; the appended "Current directory (./)" fallback is the
        // last choice because the cwd itself has no detected project.
        var subDir1 = _tempDirectory.CreateSubdirectory("app1");
        File.WriteAllText(Path.Combine(subDir1.FullName, "Cargo.toml"), "[package]\nname = \"app1\"");
        var subDir2 = _tempDirectory.CreateSubdirectory("app2");
        File.WriteAllText(Path.Combine(subDir2.FullName, "Cargo.toml"), "[package]\nname = \"app2\"");

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { "--config-only" };

        // Move past the two project choices to the appended current-directory fallback, then select it.
        TestAnsiConsole.Input.PushKey(ConsoleKey.DownArrow);
        TestAnsiConsole.Input.PushKey(ConsoleKey.DownArrow);
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert — the current-directory fallback routes the config to the search root (cwd).
        Assert.AreEqual(0, exitCode, "Init should succeed after selecting the current-directory fallback");
        var cwdConfig = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        Assert.IsTrue(File.Exists(cwdConfig), "winapp.yaml should be created in the current directory (fallback choice)");
    }

    [TestMethod]
    public async Task InitCommand_NoArgs_NoProjects_Interactive_UserAccepts_InitsInCwd()
    {
        // Arrange — an empty directory (no detectable project). With an interactive console the
        // "No known project types were found. Initialize with winapp.yaml here?" prompt is shown.
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { "--config-only" };

        // Accept the confirmation (DefaultValue is false, so push 'y' + Enter).
        PushConfirmYes();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert — accepting initializes in the current directory.
        Assert.AreEqual(0, exitCode, "Init should succeed when the user accepts the no-project prompt");
        var cwdConfig = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        Assert.IsTrue(File.Exists(cwdConfig), "winapp.yaml should be created in cwd after accepting");
    }

    [TestMethod]
    public async Task InitCommand_NoArgs_NoProjects_Interactive_UserDeclines_Returns1()
    {
        // Arrange — empty directory, interactive console, user declines the no-project prompt.
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { "--config-only" };

        // Decline the confirmation (the default).
        PushConfirmNo();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert — declining cancels init with a non-zero exit code and writes no config.
        Assert.AreEqual(1, exitCode, "Init should return 1 when the user declines the no-project prompt");
        var cwdConfig = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        Assert.IsFalse(File.Exists(cwdConfig), "winapp.yaml should not be created when the user declines");
    }

    [TestMethod]
    public async Task InitCommand_NoArgs_SingleProjectInCwd_Interactive_InitsDirectlyWithoutPrompt()
    {
        // Arrange — a single project located at the search root itself (not nested). Detection
        // reports it with DisplayPath ".", so init proceeds directly without any prompt.
        File.WriteAllText(Path.Combine(_tempDirectory.FullName, "Cargo.toml"), "[package]\nname = \"root-app\"");

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { "--config-only" };

        // Push no keys — a prompt here would throw (no input available), proving none is shown.

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert — the root project is used directly and the config lands in the current directory.
        Assert.AreEqual(0, exitCode, "Init should succeed directly when a single project sits at the search root");
        var cwdConfig = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        Assert.IsTrue(File.Exists(cwdConfig), "winapp.yaml should be created in the current directory for a root project");
    }

    [TestMethod]
    public async Task InitCommand_NoArgs_SearchLimitReached_Interactive_ShowsHintAndSelects()
    {
        // Arrange — create more nested projects than the internal detection cap (10) so the
        // selection prompt reports that the search limit was reached (adds the "run winapp
        // init <path>" hint to the title).
        for (var i = 0; i < 12; i++)
        {
            var sub = _tempDirectory.CreateSubdirectory($"app{i:D2}");
            File.WriteAllText(Path.Combine(sub.FullName, "Cargo.toml"), $"[package]\nname = \"app{i}\"");
        }

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { "--config-only" };

        // Select the first offered project.
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert — selection still succeeds when the search cap is hit, and the prompt surfaces the
        // "run winapp init <path>" hint so the user knows how to reach a project the capped search
        // may have missed.
        Assert.AreEqual(0, exitCode, "Init should succeed after selecting a project when the search limit is reached");
        StringAssert.Contains(TestAnsiConsole.Output, "path-to-project",
            "The search-limit hint pointing at 'winapp init <path-to-project>' should be shown");
    }

    [TestMethod]
    public async Task InitCommand_Sparse_WithConfigOnly_ReturnsErrorAndDoesNotInit()
    {
        // Arrange — --config-only belongs to the full-init flow; combining it with --sparse
        // should be rejected rather than silently ignored.
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { "--sparse", "--config-only" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(1, exitCode, "Sparse init should reject --config-only");
        StringAssert.Contains(ConsoleStdErr.ToString(), "--config-only",
            "Error should name the rejected option");
    }

    [TestMethod]
    public async Task InitCommand_Sparse_WithSetupSdks_ReturnsError()
    {
        // Arrange — --setup-sdks does nothing for identity packaging, so it must be rejected.
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { "--sparse", "--setup-sdks", "none" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(1, exitCode, "Sparse init should reject --setup-sdks");
        StringAssert.Contains(ConsoleStdErr.ToString(), "--setup-sdks",
            "Error should name the rejected option");
    }

    [TestMethod]
    public async Task InitCommand_Sparse_WithPositionalDirectory_ReturnsError()
    {
        // Arrange — a positional base directory configures the normal flow only; --sparse uses
        // --output-dir instead, so a positional argument should be rejected.
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--sparse" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(1, exitCode, "Sparse init should reject a positional directory");
        StringAssert.Contains(ConsoleStdErr.ToString(), "--output-dir",
            "Error should point users at --output-dir");
    }
}
