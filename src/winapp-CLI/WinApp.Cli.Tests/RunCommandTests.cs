// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Testing;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using WinApp.Cli.Commands;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class RunCommandTests : BaseCommandTests
{
    private FakeMsixService _fakeMsixService = null!;
    private FakeAppLauncherService _fakeAppLauncherService = null!;
    private FakeDebugOutputService _fakeDebugOutputService = null!;
    private FakePackageRegistrationService _fakePackageRegistrationService = null!;

    private static readonly string[] SupportedArchitectures = ["x64", "arm64", "x86"];
    private static readonly string[] ForcedUnpackagedProperties = ["WindowsPackageType=None", "Foo=Bar"];

    internal const string TestManifestContent = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
                 IgnorableNamespaces="uap rescap">
          <Identity Name="TestPackage"
                    Publisher="CN=TestPublisher"
                    Version="1.0.0.0" />
          <Properties>
            <DisplayName>Test Package</DisplayName>
            <PublisherDisplayName>Test Publisher</PublisherDisplayName>
            <Description>Test package</Description>
            <Logo>Assets\Logo.png</Logo>
          </Properties>
          <Dependencies>
            <TargetDeviceFamily Name="Windows.Universal" MinVersion="10.0.18362.0" MaxVersionTested="10.0.26100.0" />
          </Dependencies>
          <Applications>
            <Application Id="TestApp" Executable="TestApp.exe" EntryPoint="TestApp.App">
              <uap:VisualElements DisplayName="Test App" Description="Test application"
                                  BackgroundColor="#777777" Square150x150Logo="Assets\Logo.png" Square44x44Logo="Assets\Logo.png" />
            </Application>
          </Applications>
          <Capabilities>
            <rescap:Capability Name="runFullTrust" />
          </Capabilities>
        </Package>
        """;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeMsixService = new FakeMsixService();
        _fakeAppLauncherService = new FakeAppLauncherService();
        _fakeDebugOutputService = new FakeDebugOutputService();
        _fakePackageRegistrationService = new FakePackageRegistrationService();
        return services
            .AddSingleton<IMsixService>(_fakeMsixService)
            .AddSingleton<IAppLauncherService>(_fakeAppLauncherService)
            .AddSingleton<IDebugOutputService>(_fakeDebugOutputService)
            .AddSingleton<IPackageRegistrationService>(_fakePackageRegistrationService)
            .AddSingleton<INugetService, FakeNugetService>();
    }

    private async Task<FileInfo> CreateTestManifestAsync(string? directory = null)
    {
        directory ??= _tempDirectory.FullName;
        var manifestPath = Path.Combine(directory, "appxmanifest.xml");
        await File.WriteAllTextAsync(manifestPath, TestManifestContent, TestContext.CancellationToken);
        return new FileInfo(manifestPath);
    }

    #region Option parsing tests

    [TestMethod]
    public void RunCommand_ExposesShortDescription()
    {
        // The command surfaces a non-empty short description used in help output.
        var command = GetRequiredService<RunCommand>();

        Assert.IsFalse(string.IsNullOrWhiteSpace(((IShortDescription)command).ShortDescription));
    }

    [TestMethod]
    public void ParseOptions_NoLaunch_IsParsedCorrectly()
    {
        // Arrange
        var command = GetRequiredService<RunCommand>();

        // Act
        var parseResult = command.Parse([_tempDirectory.FullName, "--no-launch"]);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        Assert.IsTrue(parseResult.GetValue(RunCommand.NoLaunchOption));
    }

    [TestMethod]
    public void ParseOptions_NoLaunchNotSpecified_DefaultsToFalse()
    {
        // Arrange
        var command = GetRequiredService<RunCommand>();

        // Act
        var parseResult = command.Parse([_tempDirectory.FullName]);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        Assert.IsFalse(parseResult.GetValue(RunCommand.NoLaunchOption));
    }

    [TestMethod]
    public void ParseOptions_InputFolder_IsParsedCorrectly()
    {
        // Arrange
        var command = GetRequiredService<RunCommand>();

        // Act
        var parseResult = command.Parse([_tempDirectory.FullName]);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        var folder = parseResult.GetValue(RunCommand.InputArgument);
        Assert.IsNotNull(folder);
        Assert.AreEqual(_tempDirectory.FullName, folder.FullName);
    }

    [TestMethod]
    public void ParseOptions_NoInputFolder_NoParseError_ArgumentIsNull()
    {
        // Arrange
        var command = GetRequiredService<RunCommand>();

        // Act
        var parseResult = command.Parse([]);

        // Assert: input-folder is now optional (ArgumentArity.ZeroOrOne). With no path there is no
        // parse error; the argument value is null and the handler substitutes the current directory.
        Assert.IsEmpty(parseResult.Errors, "Omitting the optional input-folder should not produce a parse error");
        Assert.IsNull(parseResult.GetValue(RunCommand.InputArgument),
            "With no positional token, the input-folder value should be null (handler defaults it to cwd)");
    }

    [TestMethod]
    public void ParseOptions_NoInputFolderWithPassthrough_ProducesNoParseErrors()
    {
        // Arrange
        var command = GetRequiredService<RunCommand>();

        // Act: no path, straight into '-- --appflag value'. With input-folder now optional
        // (ArgumentArity.ZeroOrOne) and AcceptExistingOnly removed, System.CommandLine greedily
        // binds the first post-'--' token to the positional, but that no longer hard-errors. The
        // handler detects the stolen token and falls back to cwd (see the handler-level test
        // RunCommand_NoInputFolderWithPassthrough_UsesCwdAndForwardsAppArgs).
        var parseResult = command.Parse(["--", "--appflag", "value"]);

        // Assert
        Assert.IsEmpty(parseResult.Errors,
            "A bare '-- <app args>' with no path must not produce a parse error");
    }

    [TestMethod]
    public void ParseOptions_DotInputFolder_ResolvesToProcessCurrentDirectory()
    {
        // Arrange
        var command = GetRequiredService<RunCommand>();

        // Act: an explicit '.' is a normal pre-'--' path token and binds to input-folder.
        var parseResult = command.Parse(["."]);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "'.' is a valid explicit path and should not error");
        var input = parseResult.GetValue(RunCommand.InputArgument);
        Assert.IsNotNull(input, "'.' should bind to the input-folder argument");
        Assert.AreEqual(Path.GetFullPath("."), input.FullName,
            "'.' should resolve to the current directory");
    }

    [TestMethod]
    public void ParseOptions_DotInputFolderWithPassthrough_PassthroughCaptured()
    {
        // Arrange
        var command = GetRequiredService<RunCommand>();

        // Act: an explicit path before '--' must not disturb passthrough capture.
        var parseResult = command.Parse([".", "--", "--appflag", "value"]);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "'. -- <app args>' should not produce a parse error");
        Assert.IsNotNull(parseResult.GetValue(RunCommand.InputArgument),
            "'.' should bind to the input-folder argument");
        var passthrough = parseResult.GetValue(RunCommand.PassthroughArgument);
        var expectedPassthrough = new[] { "--appflag", "value" };
        CollectionAssert.AreEqual(expectedPassthrough, passthrough,
            "The post-'--' tokens should be captured by the passthrough argument");
    }

    [TestMethod]
    public async Task ParseOptions_AllOptions_AreParsedCorrectly()
    {
        // Arrange
        var command = GetRequiredService<RunCommand>();
        var manifest = await CreateTestManifestAsync();
        var outputDir = Path.Combine(_tempDirectory.FullName, "output");
        var args = new[]
        {
            _tempDirectory.FullName,
            "--manifest", manifest.FullName,
            "--output-appx-directory", outputDir,
            "--args", "arg1 arg2",
            "--no-launch"
        };

        // Act
        var parseResult = command.Parse(args);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        Assert.IsTrue(parseResult.GetValue(RunCommand.NoLaunchOption));
        Assert.AreEqual("arg1 arg2", parseResult.GetValue(RunCommand.ArgsOption));
        var folder = parseResult.GetValue(RunCommand.InputArgument);
        Assert.IsNotNull(folder);
        Assert.AreEqual(_tempDirectory.FullName, folder.FullName);
    }

    /// <summary>
    /// The three ways a run can name its layout, and who owns each. This is the decision that
    /// makes deletion safe or unsafe, and it is made here -- from the options as typed -- because
    /// once the default has been filled in the path alone no longer says who created the directory.
    /// </summary>
    [TestMethod]
    public void ResolveLayoutOutput_NoDirectoryNamed_IsWinappsOwnGeneratedLayout()
    {
        var parseResult = GetRequiredService<RunCommand>().Parse([_tempDirectory.FullName]);

        Assert.IsTrue(RunCommand.TryResolveLayoutOutput(parseResult, out var layoutOutput, out var error), error);
        Assert.IsNull(layoutOutput.Directory, "the generated default is filled in later, by the run itself");
        Assert.AreEqual(LayoutReconciliation.Exact, layoutOutput.Reconciliation);
    }

    [TestMethod]
    public void ResolveLayoutOutput_UserTypedDirectory_IsNeverPruned()
    {
        var outputDir = Path.Combine(_tempDirectory.FullName, "mine");
        var parseResult = GetRequiredService<RunCommand>()
            .Parse([_tempDirectory.FullName, "--output-appx-directory", outputDir]);

        Assert.IsTrue(RunCommand.TryResolveLayoutOutput(parseResult, out var layoutOutput, out var error), error);
        Assert.AreEqual(outputDir, layoutOutput.Directory?.FullName);
        Assert.AreEqual(LayoutReconciliation.Additive, layoutOutput.Reconciliation);
    }

    [TestMethod]
    public void ResolveLayoutOutput_HostNamedGuestLayout_IsWinappOwned()
    {
        var layout = Path.Combine(_tempDirectory.FullName, "abc-layout");
        var parseResult = GetRequiredService<RunCommand>()
            .Parse([_tempDirectory.FullName, "--managed-appx-directory", layout]);

        Assert.IsTrue(RunCommand.TryResolveLayoutOutput(parseResult, out var layoutOutput, out var error), error);
        Assert.AreEqual(layout, layoutOutput.Directory?.FullName);
        Assert.AreEqual(LayoutReconciliation.Exact, layoutOutput.Reconciliation);
    }

    /// <summary>
    /// Both options name the same thing and disagree about who owns it. Picking one silently would
    /// settle a deletion question by argument order, so the run refuses instead.
    /// </summary>
    [TestMethod]
    public void ResolveLayoutOutput_BothDirectoryOptions_IsRefused()
    {
        var parseResult = GetRequiredService<RunCommand>().Parse(
        [
            _tempDirectory.FullName,
            "--output-appx-directory", Path.Combine(_tempDirectory.FullName, "mine"),
            "--managed-appx-directory", Path.Combine(_tempDirectory.FullName, "theirs"),
        ]);

        Assert.IsFalse(RunCommand.TryResolveLayoutOutput(parseResult, out _, out var error));
        Assert.IsNotNull(error);
        StringAssert.Contains(error, "--output-appx-directory", StringComparison.Ordinal);
        StringAssert.Contains(error, "--managed-appx-directory", StringComparison.Ordinal);
    }

    /// <summary>The internal option stays out of help and the generated schema.</summary>
    [TestMethod]
    public void ManagedAppXDirectoryOption_IsHiddenFromUsers()
    {
        Assert.IsTrue(RunCommand.ManagedAppXDirectoryOption.Hidden);
        Assert.IsFalse(RunCommand.OutputAppXDirectoryOption.Hidden);
    }

    [TestMethod]
    public void ParseOptions_Clean_IsParsedCorrectly()
    {
        // Arrange
        var command = GetRequiredService<RunCommand>();

        // Act
        var parseResult = command.Parse([_tempDirectory.FullName, "--clean"]);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        Assert.IsTrue(parseResult.GetValue(RunCommand.CleanOption));
    }

    [TestMethod]
    public void ParseOptions_CleanNotSpecified_DefaultsToFalse()
    {
        // Arrange
        var command = GetRequiredService<RunCommand>();

        // Act
        var parseResult = command.Parse([_tempDirectory.FullName]);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        Assert.IsFalse(parseResult.GetValue(RunCommand.CleanOption));
    }

    #endregion

    #region Handler tests

    [TestMethod]
    public async Task RunCommand_WithNoLaunch_RegistersIdentityButDoesNotLaunch()
    {
        // Arrange - manifest in input folder
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--no-launch"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutCalls.Count, "Debug identity should be created");
        Assert.IsFalse(_fakeMsixService.AddLooseLayoutCalls[0].Clean, "Default run should preserve app data (clean=false)");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "Application should NOT be launched with --no-launch");
    }

    [TestMethod]
    public async Task RunCommand_WithClean_PassesCleanThroughToMsixService()
    {
        // Arrange
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--no-launch", "--clean"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutCalls.Count, "Debug identity should be created");
        Assert.IsTrue(_fakeMsixService.AddLooseLayoutCalls[0].Clean, "--clean should be passed through to MSIX service");
    }

    [TestMethod]
    public async Task RunCommand_WithoutClean_DefaultsToPreservingAppData()
    {
        // Arrange
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--no-launch"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutCalls.Count, "Debug identity should be created");
        Assert.IsFalse(_fakeMsixService.AddLooseLayoutCalls[0].Clean, "Without --clean, app data should be preserved");
    }

    [TestMethod]
    public async Task RunCommand_WithNoLaunchAndManifest_RegistersIdentityButDoesNotLaunch()
    {
        // Arrange
        var manifest = await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--manifest", manifest.FullName, "--no-launch"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutCalls.Count, "Debug identity should be created");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "Application should NOT be launched with --no-launch");
    }

    [TestMethod]
    public async Task RunCommand_WithInputFolder_ResolvesManifestFromFolder()
    {
        // Arrange - manifest in a subfolder, not in cwd
        var subFolder = _tempDirectory.CreateSubdirectory("app-output");
        await CreateTestManifestAsync(subFolder.FullName);
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [subFolder.FullName, "--no-launch"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutCalls.Count, "Debug identity should be created");
        StringAssert.Contains(_fakeMsixService.AddLooseLayoutCalls[0].ManifestPath, subFolder.FullName,
            "Manifest should be resolved from the input folder");
    }

    [TestMethod]
    public async Task RunCommand_WithInputFolderAndManifest_UsesExplicitManifest()
    {
        // Arrange - manifest explicitly specified, different from folder
        var subFolder = _tempDirectory.CreateSubdirectory("app-output");
        var manifest = await CreateTestManifestAsync(subFolder.FullName);
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--manifest", manifest.FullName, "--no-launch"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutCalls.Count, "Debug identity should be created");
        StringAssert.Contains(_fakeMsixService.AddLooseLayoutCalls[0].ManifestPath, manifest.FullName,
            "Explicit --manifest should take priority");
    }

    [TestMethod]
    public async Task RunCommand_WithNoManifestAnywhere_ReturnsError()
    {
        // Arrange - no manifest in cwd or folder
        var emptyFolder = _tempDirectory.CreateSubdirectory("empty");
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [emptyFolder.FullName, "--no-launch"]);

        // Assert
        Assert.AreNotEqual(0, exitCode, "Command should fail when no manifest is found");
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count, "No identity should be created");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "No application should be launched");
    }

    #endregion

    #region JSON output tests

    [TestMethod]
    public async Task RunCommand_WithJsonAndNoLaunch_OutputsJsonWithAumid()
    {
        // Arrange
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--no-launch", "--json"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");

        var json = ParseJsonOutput();
        Assert.AreEqual("TestPackage_fakefamily!TestApp", json.GetProperty("AUMID").GetString());
        Assert.IsFalse(json.TryGetProperty("ProcessId", out _), "ProcessId should not be present in no-launch mode");
        Assert.IsFalse(json.TryGetProperty("Error", out _), "Error should not be present on success");
    }

    [TestMethod]
    public async Task RunCommand_WithJsonAndError_OutputsJsonWithErrorField()
    {
        // Arrange
        await CreateTestManifestAsync();
        _fakeMsixService.ExceptionToThrow = new InvalidOperationException("Test error message");
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--no-launch", "--json"]);

        // Assert
        Assert.AreNotEqual(0, exitCode, "Command should fail");

        var json = ParseJsonOutput();
        Assert.AreEqual("Test error message", json.GetProperty("Error").GetString());
        Assert.IsFalse(json.TryGetProperty("AUMID", out _), "AUMID should not be present on error before identity is created");
        Assert.IsFalse(json.TryGetProperty("ProcessId", out _), "ProcessId should not be present on error");
    }

    [TestMethod]
    public async Task RunCommand_WithoutJsonFlag_DoesNotOutputJson()
    {
        // Arrange
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--no-launch"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");

        var output = TestAnsiConsole.Output;
        Assert.IsFalse(output.Contains("\"AUMID\""), "JSON fields should not appear without --json flag");
    }

    [TestMethod]
    public async Task RunCommand_WithJson_OutputsValidJsonDocument()
    {
        // Arrange
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--no-launch", "--json"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");

        var output = TestAnsiConsole.Output;
        Assert.Contains("{\n", output, "JSON should use \\n line endings");
    }

    [TestMethod]
    public async Task RunCommand_MutualExclusionViolation_WithJson_EmitsJsonError()
    {
        // Change 2 (L5): run's own mutual-exclusion validation errors must be emitted as a JSON
        // error object under --json, not a plain-text banner. --detach + --no-launch is one such
        // invalid combination; it fails fast before any identity/launch work, so no manifest is
        // needed. The test logger routes LogError to stderr, so stdout carries only the JSON.
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--detach", "--no-launch", "--json"]);

        // Assert
        Assert.AreEqual(1, exitCode, "A mutually-exclusive option combination should fail");

        // stdout must be pure JSON with no plain-text banner.
        var stdout = TestAnsiConsole.Output.Trim();
        Assert.IsTrue(stdout.StartsWith('{') && stdout.EndsWith('}'),
            $"Under --json, stdout should contain only the JSON error object, but was: {stdout}");

        var json = ParseJsonOutput();
        Assert.AreEqual("--detach and --no-launch cannot be used together.",
            json.GetProperty("Error").GetString(),
            "The mutual-exclusion error should be surfaced in the JSON Error field");
        Assert.IsFalse(json.TryGetProperty("AUMID", out _), "AUMID should not be present on a validation error");
        Assert.IsFalse(json.TryGetProperty("ProcessId", out _), "ProcessId should not be present on a validation error");
    }

    [TestMethod]
    public async Task RunCommand_LongPathValidation_WithJson_EmitsJsonError()
    {
        // Change 2 (L5) completeness: the early long-path validation is another run-local validation
        // and, like the mutual-exclusion checks, must emit a JSON error object under --json rather than
        // a suppressed-logger silent exit. ValidatePathLength only throws when the OS does not have long
        // path support enabled, so guard on that (mirrors LongPathHelperTests). The >260-char path is
        // supplied via the current-directory default (no input arg) so it is not pre-empted by the
        // provided-path existence check, and needs no long path to exist on disk. The base harness
        // registers ICurrentDirectoryProvider last (last-wins), so the long cwd is injected by
        // constructing the handler directly with a CurrentDirectoryProvider override.
        if (LongPathHelper.IsSystemLongPathEnabled())
        {
            Assert.Inconclusive(
                "System long path support is enabled; ValidatePathLength does not throw, so the JSON error path cannot be exercised.");
            return;
        }

        var longCwd = @"C:\" + new string('a', 300);
        var command = GetRequiredService<RunCommand>();
        var parseResult = command.Parse(["--json"]);

        var handler = new RunCommand.Handler(
            GetRequiredService<IMsixService>(),
            GetRequiredService<IAppLauncherService>(),
            GetRequiredService<IPackageRegistrationService>(),
            GetRequiredService<IDebugOutputService>(),
            new CurrentDirectoryProvider(longCwd),
            GetRequiredService<IAnsiConsole>(),
            GetRequiredService<IStatusService>(),
            GetRequiredService<IProjectRunService>(),
            GetRequiredService<IProjectContextDetector>(),
            GetRequiredService<ExecutionTargetOrchestrator>(),
            GetRequiredService<GuestApplicationRunner>(),
            GetRequiredService<TargetRuntimeService>(),
            GetRequiredService<IWinappDirectoryService>(),
            GetRequiredService<ILogger<RunCommand>>());

        // Act
        var exitCode = await handler.InvokeAsync(parseResult, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, exitCode, "A path exceeding MAX_PATH without long-path support should fail");

        // stdout must be pure JSON with no plain-text banner.
        var stdout = TestAnsiConsole.Output.Trim();
        Assert.IsTrue(stdout.StartsWith('{') && stdout.EndsWith('}'),
            $"Under --json, stdout should contain only the JSON error object, but was: {stdout}");

        var json = ParseJsonOutput();
        Assert.IsTrue(json.GetProperty("Error").GetString()!.Contains("MAX_PATH"),
            "The long-path validation error should be surfaced in the JSON Error field");
        Assert.IsFalse(json.TryGetProperty("AUMID", out _), "AUMID should not be present on a validation error");
    }

    [TestMethod]
    public async Task RunCommand_LongErrorMessage_WithJson_EmitsValidParseableJson()
    {
        // Regression (M1): run's PrintJson previously emitted the payload via ansiConsole.WriteLine,
        // which routes through Spectre's word-wrapping renderer and injects raw CR/LF *inside* the
        // "Error" string value once the message exceeds the redirected console width (~80 cols) — the
        // result is INVALID, unparseable JSON for any long error. Unlike the long-path validation above
        // (which only throws when OS long-path support is disabled, so it self-skips on most machines),
        // the provided-path existence check emits an always-long "'<path>' does not exist." message on
        // EVERY machine, so this exercises the wrapping fix with a strict parser (JsonDocument.Parse)
        // independent of any registry/OS state. The path stays under MAX_PATH (260) so FileSystemInfo
        // binding never throws, but the full message comfortably exceeds the wrap width.
        var longMissingPath = @"C:\" + new string('a', 200) + @"\does-not-exist";
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [longMissingPath, "--json"]);

        Assert.AreEqual(1, exitCode, "A non-existent input path should fail");

        // stdout must be pure, single-object JSON that a strict parser accepts (PowerShell's lenient
        // ConvertFrom-Json masked the wrapping; JsonDocument.Parse does not).
        var output = TestAnsiConsole.Output.Trim();
        Assert.IsTrue(output.StartsWith('{') && output.EndsWith('}'),
            $"Under --json, stdout should contain only the JSON error object, but was: {output}");

        var root = JsonDocument.Parse(output).RootElement;
        var error = root.GetProperty("Error").GetString();
        Assert.IsNotNull(error, "The JSON error object must carry an Error field");
        StringAssert.Contains(error, "does not exist",
            "The existence error should be surfaced in the JSON Error field");
        Assert.IsFalse(root.TryGetProperty("AUMID", out _), "AUMID should not be present on a validation error");
    }

    [TestMethod]
    public async Task RunCommand_LayoutFailure_WithJson_NamesTheLinkInTheErrorField()
    {
        // winapp refuses to build a layout through a symbolic link or junction, because a layout
        // quietly missing whatever was behind the link registers and runs with files absent. That
        // refusal has to reach the caller in every output mode -- a machine-readable run that saw
        // only success would be the same silence, one level up. This is the --json half.
        await CreateTestManifestAsync();
        var linkPath = Path.Combine(_tempDirectory.FullName, "Plugins");
        _fakeMsixService.ExceptionToThrow = new InvalidOperationException(
            $"'{linkPath}' is a symbolic link or junction. winapp will not build an AppX layout through a link.");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--no-launch", "--json"]);

        Assert.AreNotEqual(0, exitCode, "A layout winapp will not build must fail the run");

        var json = ParseJsonOutput();
        StringAssert.Contains(json.GetProperty("Error").GetString(), linkPath, StringComparison.OrdinalIgnoreCase);
        Assert.IsFalse(json.TryGetProperty("AUMID", out _),
            "nothing may be registered when the layout was refused");
    }

    [TestMethod]
    public async Task RunCommand_LayoutFailure_WithQuiet_StillFailsAndNamesTheLink()
    {
        // The --quiet half. Quiet suppresses progress, not the reason a run produced no app.
        await CreateTestManifestAsync();
        var linkPath = Path.Combine(_tempDirectory.FullName, "Plugins");
        _fakeMsixService.ExceptionToThrow = new InvalidOperationException(
            $"'{linkPath}' is a symbolic link or junction. winapp will not build an AppX layout through a link.");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--no-launch", "--quiet"]);

        Assert.AreNotEqual(0, exitCode, "A layout winapp will not build must fail the run");
        StringAssert.Contains(ConsoleStdErr.ToString(), linkPath, StringComparison.OrdinalIgnoreCase,
            "quiet suppresses progress, not the reason the run produced no app");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count,
            "nothing may be launched when the layout was refused");
    }

    [TestMethod]
    public void ParseOptions_JsonOption_IsParsedCorrectly()
    {
        // Arrange
        var command = GetRequiredService<RunCommand>();

        // Act
        var parseResult = command.Parse([_tempDirectory.FullName, "--json"]);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        Assert.IsTrue(parseResult.GetValue(WinAppRootCommand.JsonOption));
    }

    private JsonElement ParseJsonOutput()
    {
        var output = TestAnsiConsole.Output;

        // Find the JSON object in the output (skip any non-JSON status output)
        var jsonStart = output.IndexOf('{');
        var jsonEnd = output.LastIndexOf('}');
        Assert.IsTrue(jsonStart >= 0 && jsonEnd > jsonStart, "Output should contain a JSON object");

        var jsonText = output[jsonStart..(jsonEnd + 1)];
        var doc = JsonDocument.Parse(jsonText);
        return doc.RootElement;
    }

    #endregion

    #region --with-alias option tests

    [TestMethod]
    public void ParseOptions_WithAlias_IsParsedCorrectly()
    {
        // Arrange
        var command = GetRequiredService<RunCommand>();

        // Act
        var parseResult = command.Parse([_tempDirectory.FullName, "--with-alias"]);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        Assert.IsTrue(parseResult.GetValue(RunCommand.WithAliasOption));
    }

    [TestMethod]
    public void ParseOptions_WithAliasNotSpecified_DefaultsToFalse()
    {
        // Arrange
        var command = GetRequiredService<RunCommand>();

        // Act
        var parseResult = command.Parse([_tempDirectory.FullName]);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        Assert.IsFalse(parseResult.GetValue(RunCommand.WithAliasOption));
    }

    [TestMethod]
    public async Task RunCommand_WithAliasAndNoLaunch_ReturnsError()
    {
        // Arrange - --with-alias and --no-launch are mutually exclusive
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--with-alias", "--no-launch"]);

        // Assert
        Assert.AreEqual(1, exitCode, "Command should fail when both --with-alias and --no-launch are specified");
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count, "No identity should be created");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "No application should be launched");
    }

    [TestMethod]
    public async Task RunCommand_WithAlias_RegistersIdentityButDoesNotLaunchByAumid()
    {
        // Arrange - manifest in input folder, --with-alias means no AUMID launch.
        // The LaunchViaExecutionAliasAsync will fail because there's no processed manifest
        // in the AppX output directory, but we can verify that it does NOT use AUMID launch.
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--with-alias"]);

        // Assert - identity should be created but AUMID launch should NOT be used
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutCalls.Count, "Debug identity should be created");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count,
            "Application should NOT be launched via AUMID when --with-alias is specified");
    }

    #endregion

    #region --debug-output option tests

    [TestMethod]
    public void ParseOptions_DebugOutput_IsParsedCorrectly()
    {
        // Arrange
        var command = GetRequiredService<RunCommand>();

        // Act
        var parseResult = command.Parse([_tempDirectory.FullName, "--debug-output"]);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        Assert.IsTrue(parseResult.GetValue(RunCommand.DebugOutputOption));
    }

    [TestMethod]
    public void ParseOptions_DebugOutputNotSpecified_DefaultsToFalse()
    {
        // Arrange
        var command = GetRequiredService<RunCommand>();

        // Act
        var parseResult = command.Parse([_tempDirectory.FullName]);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        Assert.IsFalse(parseResult.GetValue(RunCommand.DebugOutputOption));
    }

    [TestMethod]
    public async Task RunCommand_DebugOutputAndNoLaunch_ReturnsError()
    {
        // Arrange - --debug-output and --no-launch are mutually exclusive
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--debug-output", "--no-launch"]);

        // Assert
        Assert.AreEqual(1, exitCode, "Command should fail when both --debug-output and --no-launch are specified");
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count, "No identity should be created");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "No application should be launched");
        Assert.AreEqual(0, _fakeDebugOutputService.AttachCalls.Count, "Debug loop should not run");
    }

    [TestMethod]
    public async Task RunCommand_DebugOutput_LaunchesByAumidAndCallsDebugService()
    {
        // Arrange
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--debug-output"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutCalls.Count, "Debug identity should be created");
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchCalls.Count, "Application should be launched via AUMID");
        Assert.AreEqual(1, _fakeDebugOutputService.AttachCalls.Count, "Debug service should be called");
        Assert.AreEqual(_fakeAppLauncherService.FakeProcessId, _fakeDebugOutputService.AttachCalls[0],
            "Debug service should receive the launched process ID");
    }

    [TestMethod]
    public async Task RunCommand_DebugOutput_CancelledDuringLoop_TerminatesPackageProcesses()
    {
        // --debug-output (AUMID launch): a Ctrl+C that arrives while the debug loop is running makes
        // the loop return, after which the command terminates the package's processes before
        // returning the loop's exit code. Covers the AUMID-path post-loop cancellation cleanup.
        await CreateTestManifestAsync();
        _fakeDebugOutputService.FakeExitCode = 42;
        var handler = GetRequiredService<RunCommand.Handler>();
        var command = GetRequiredService<RunCommand>();
        var parseResult = command.Parse([_tempDirectory.FullName, "--debug-output"]);
        using var cts = new CancellationTokenSource();
        _fakeDebugOutputService.CancelTokenDuringLoop = cts;

        var exitCode = await handler.InvokeAsync(parseResult, cts.Token);

        Assert.AreEqual(42, exitCode, "The debug loop's exit code is returned even after cancellation cleanup");
        Assert.AreEqual(1, _fakeDebugOutputService.AttachCalls.Count, "The debug loop should have run");
        Assert.AreEqual(1, _fakeAppLauncherService.TerminateCalls.Count,
            "Cancellation after the debug loop should terminate the package's processes");
        Assert.AreEqual(_fakeAppLauncherService.FakeProcessId, _fakeAppLauncherService.TerminateCalls[0].ProcessId,
            "Terminate should target the launched (AUMID) process");
    }

    [TestMethod]
    public async Task RunCommand_DebugOutputWithAlias_SkipsAumidLaunch()
    {
        // Arrange - with both --debug-output and --with-alias, the execution alias path is used.
        // LaunchViaExecutionAliasAsync will fail because there's no processed manifest in AppX output,
        // but verify that AUMID launch is not used.
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--debug-output", "--with-alias"]);

        // Assert - identity should be created but AUMID launch should NOT be used
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutCalls.Count, "Debug identity should be created");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count,
            "Application should NOT be launched via AUMID when --with-alias is specified");
    }

    [TestMethod]
    public async Task RunCommand_DebugOutput_UsesDebugServiceExitCode()
    {
        // Arrange
        await CreateTestManifestAsync();
        _fakeDebugOutputService.FakeExitCode = 42;
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--debug-output"]);

        // Assert
        Assert.AreEqual(42, exitCode, "Exit code should come from the debug service");
    }

    [TestMethod]
    public async Task RunCommand_JsonAndDebugOutput_ReturnsError()
    {
        // Arrange - --json and --debug-output are mutually exclusive. In --json mode the
        // human-readable logger is suppressed, so the rejection must still surface a
        // machine-readable error object (not an empty stdout with exit code 1).
        TestAnsiConsole.Profile.Width = 1000; // avoid line-wrapping that would corrupt the JSON string
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--debug-output", "--json"]);

        // Assert
        Assert.AreEqual(1, exitCode, "Command should fail when both --json and --debug-output are specified");
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count, "No identity should be created");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "No application should be launched");
        Assert.AreEqual(0, _fakeDebugOutputService.AttachCalls.Count, "Debug loop should not run");

        // Regression guard: without the structured-error fallback the command would exit 1 with
        // empty stdout, so this assertion (not just the exit code) is what fails if PrintJson is removed.
        var json = ParseJsonOutput();
        Assert.IsTrue(json.TryGetProperty("Error", out var error),
            "JSON output should contain an Error property when --json and --debug-output are combined");
        StringAssert.Contains(error.GetString(), "--json and --debug-output cannot be used together",
            "The structured error should explain the mutually exclusive options");
    }

    [TestMethod]
    [DoNotParallelize] // temporarily swaps the process-wide ambient AnsiConsole to capture logger warnings
    public async Task RunCommand_SymbolsWithoutDebugOutput_WarnsAndContinues()
    {
        // Regression for issue #662: --symbols only affects the --debug-output stowed-exception
        // triage. Passing it on its own must NOT silently no-op — it should emit a non-fatal
        // warning and let the command continue (here the default AUMID launch path).
        // Non-error logger output routes through the static ambient AnsiConsole (TextWriterLogger),
        // so we swap it to a capturing console for the invoke; [DoNotParallelize] isolates the swap.
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var previousAmbient = AnsiConsole.Console;
        var ambient = new TestConsole();
        AnsiConsole.Console = ambient;
        int exitCode;
        try
        {
            exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--symbols"]);
        }
        finally
        {
            AnsiConsole.Console = previousAmbient;
        }

        // Assert - non-fatal: the app is still launched normally and no debug loop runs.
        Assert.AreEqual(0, exitCode, "--symbols without --debug-output must remain non-fatal");
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchCalls.Count, "The app should still launch via AUMID");
        Assert.AreEqual(0, _fakeDebugOutputService.AttachCalls.Count,
            "No debug loop should run without --debug-output");

        StringAssert.Contains(ambient.Output, "--symbols has no effect without --debug-output",
            "A warning should tell the user --symbols was ignored");
    }

    [TestMethod]
    public async Task RunCommand_JsonAndWithAlias_ReturnsError()
    {
        // Arrange - --json and --with-alias are mutually exclusive
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--with-alias", "--json"]);

        // Assert
        Assert.AreEqual(1, exitCode, "Command should fail when both --json and --with-alias are specified");
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count, "No identity should be created");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "No application should be launched");
    }

    [TestMethod]
    public async Task RunCommand_DebugOutput_PropagatesFailureExitCode()
    {
        // Arrange — debug service returns -1 (e.g., DebugActiveProcess failed)
        await CreateTestManifestAsync();
        _fakeDebugOutputService.FakeExitCode = -1;
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--debug-output"]);

        // Assert
        Assert.AreEqual(-1, exitCode, "Failure exit code from the debug service should propagate");
    }

    [TestMethod]
    public async Task RunCommand_DebugOutputWithAliasAndNoLaunch_ReturnsError()
    {
        // Arrange — all three flags conflict; --with-alias + --no-launch is caught first
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--debug-output", "--with-alias", "--no-launch"]);

        // Assert
        Assert.AreEqual(1, exitCode, "Command should fail with conflicting flags");
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count, "No identity should be created");
        Assert.AreEqual(0, _fakeDebugOutputService.AttachCalls.Count, "Debug loop should not run");
    }

    [TestMethod]
    public async Task RunCommand_DebugOutputWithArgs_ForwardsArgsToLauncher()
    {
        // Arrange
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--debug-output", "--args", "--my-flag value"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchCalls.Count, "Application should be launched");
        Assert.AreEqual("--my-flag value", _fakeAppLauncherService.LaunchCalls[0].Arguments,
            "Arguments should be forwarded to the launcher");
        Assert.AreEqual(1, _fakeDebugOutputService.AttachCalls.Count, "Debug service should be called");
    }

    #endregion

    #region --detach option tests

    [TestMethod]
    public void ParseOptions_Detach_IsParsedCorrectly()
    {
        // Arrange
        var command = GetRequiredService<RunCommand>();

        // Act
        var parseResult = command.Parse([_tempDirectory.FullName, "--detach"]);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        Assert.IsTrue(parseResult.GetValue(RunCommand.DetachOption));
    }

    [TestMethod]
    public void ParseOptions_DetachNotSpecified_DefaultsToFalse()
    {
        // Arrange
        var command = GetRequiredService<RunCommand>();

        // Act
        var parseResult = command.Parse([_tempDirectory.FullName]);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        Assert.IsFalse(parseResult.GetValue(RunCommand.DetachOption));
    }

    [TestMethod]
    public async Task RunCommand_DetachAndNoLaunch_ReturnsError()
    {
        // Arrange - --detach and --no-launch are mutually exclusive
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--detach", "--no-launch"]);

        // Assert
        Assert.AreEqual(1, exitCode, "Command should fail when both --detach and --no-launch are specified");
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count, "No identity should be created");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "No application should be launched");
    }

    [TestMethod]
    public async Task RunCommand_DetachAndDebugOutput_ReturnsError()
    {
        // Arrange - --detach and --debug-output are mutually exclusive
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--detach", "--debug-output"]);

        // Assert
        Assert.AreEqual(1, exitCode, "Command should fail when both --detach and --debug-output are specified");
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count, "No identity should be created");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "No application should be launched");
        Assert.AreEqual(0, _fakeDebugOutputService.AttachCalls.Count, "Debug loop should not run");
    }

    [TestMethod]
    public async Task RunCommand_DetachAndWithAlias_ReturnsError()
    {
        // Arrange - --detach and --with-alias are mutually exclusive
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--detach", "--with-alias"]);

        // Assert
        Assert.AreEqual(1, exitCode, "Command should fail when both --detach and --with-alias are specified");
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count, "No identity should be created");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "No application should be launched");
    }

    [TestMethod]
    public async Task RunCommand_DetachAndUnregisterOnExit_ReturnsError()
    {
        // Arrange - --detach and --unregister-on-exit are mutually exclusive
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--detach", "--unregister-on-exit"]);

        // Assert
        Assert.AreEqual(1, exitCode, "Command should fail when both --detach and --unregister-on-exit are specified");
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count, "No identity should be created");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "No application should be launched");
    }

    [TestMethod]
    public async Task RunCommand_Detach_LaunchesByAumidAndReturnsImmediately()
    {
        // Arrange
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--detach"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutCalls.Count, "Debug identity should be created");
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchCalls.Count, "Application should be launched via AUMID");
    }

    [TestMethod]
    public async Task RunCommand_DetachWithJson_OutputsJsonWithProcessId()
    {
        // Arrange
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--detach", "--json"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");

        var json = ParseJsonOutput();
        Assert.AreEqual("TestPackage_fakefamily!TestApp", json.GetProperty("AUMID").GetString());
        Assert.AreEqual(_fakeAppLauncherService.FakeProcessId, json.GetProperty("ProcessId").GetUInt32(),
            "ProcessId should be present in detach mode");
        Assert.IsFalse(json.TryGetProperty("Error", out _), "Error should not be present on success");
    }

    [TestMethod]
    public async Task RunCommand_DetachWithoutJson_PrintsPidAndNoJson()
    {
        // Arrange
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--detach"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");

        var output = TestAnsiConsole.Output;
        Assert.IsFalse(output.Contains("\"AUMID\""), "JSON fields should not appear without --json flag");
        Assert.IsFalse(output.Contains("\"ProcessId\""), "JSON fields should not appear without --json flag");
        // Change 3 (L6): the packaged --detach path must surface the launched PID in human-readable
        // output too, consistent with the unpackaged/project-mode detach path.
        Assert.Contains(_fakeAppLauncherService.FakeProcessId.ToString(), output,
            "The launched PID should be printed in non-JSON output for the packaged --detach path");
    }

    #endregion

    #region -- passthrough argument tests

    // --- Parse-level behaviour ---

    [TestMethod]
    public void ParseOptions_DoubleDashPassthrough_ProducesNoParseErrors()
    {
        var command = GetRequiredService<RunCommand>();
        var parseResult = command.Parse([_tempDirectory.FullName, "--", "--flag", "value"]);
        Assert.IsEmpty(parseResult.Errors, "Tokens after -- should not cause parse errors");
    }

    [TestMethod]
    public void ParseOptions_BareDoubleDash_ProducesNoParseErrors()
    {
        // A bare '--' with nothing after it is valid; the app simply receives no passthrough args.
        var command = GetRequiredService<RunCommand>();
        var parseResult = command.Parse([_tempDirectory.FullName, "--"]);
        Assert.IsEmpty(parseResult.Errors, "A bare -- with nothing following should not cause parse errors");
    }

    [TestMethod]
    public void ParseOptions_UnknownOptionBeforeDoubleDash_AbsorbedIntoZeroOrMore_NoParseError()
    {
        // With a ZeroOrMore positional argument, System.CommandLine absorbs unrecognised
        // option-like tokens (e.g. '--unknown-opt') into the argument rather than reporting
        // them as parse errors. The handler uses SplitPassthroughTokens to detect and reject
        // these tokens at invocation time.
        var command = GetRequiredService<RunCommand>();
        var parseResult = command.Parse([_tempDirectory.FullName, "--unknown-opt", "--", "--app-flag"]);
        Assert.IsEmpty(parseResult.Errors,
            "ZeroOrMore absorbs pre-'--' unknown tokens silently; the handler validates them");
    }

    // --- Handler: basic passthrough scenarios ---

    [TestMethod]
    public async Task RunCommand_DoubleDashPassthrough_ForwardsArgsToLauncher()
    {
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--", "--my-flag", "value"]);

        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchCalls.Count, "Application should be launched");
        Assert.AreEqual("--my-flag value", _fakeAppLauncherService.LaunchCalls[0].Arguments,
            "Passthrough args after -- should be forwarded to the launcher");
    }

    // --- Migration notice: dotnet run argument routing ---

    [TestMethod]
    public async Task RunCommand_NuGetCaller_ForwardsKnownOption_PointsAtTheMSBuildProperty()
    {
        // The NuGet targets end RunArguments with a separator, so everything typed after
        // `dotnet run` reaches the app. A project that previously relied on `dotnet run --detach`
        // keeps working syntactically but silently changes meaning, and MSBuild cannot warn because
        // it never sees those tokens. winapp is the only place that can.
        await CreateTestManifestAsync();
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["run", _tempDirectory.FullName, "--caller", "nuget-package", "--", "--detach"]);

        Assert.AreEqual(0, exitCode, "Forwarding is not an error");
        var output = $"{ConsoleStdOut}{ConsoleStdErr}{TestAnsiConsole.Output}";
        StringAssert.Contains(output, "'--detach' was passed to your application",
            "The notice should name the token that changed meaning");
        StringAssert.Contains(output, "WinAppRunDetach=true",
            "The notice should name the property that replaces it");
        Assert.AreEqual("--detach", _fakeAppLauncherService.LaunchCalls[0].Arguments,
            "The token must still reach the app");
    }

    [TestMethod]
    [DataRow("--verbose", DisplayName = "global option with no property")]
    [DataRow("--help", DisplayName = "the app's own help flag")]
    [DataRow("--configuration", DisplayName = "project-mode option, ignored in folder mode")]
    [DataRow("-p", DisplayName = "project-mode short option")]
    [DataRow("--no-build", DisplayName = "project-mode switch")]
    public async Task RunCommand_NuGetCaller_ForwardsOptionWithNoProperty_StaysSilent(string forwarded)
    {
        // Notices are limited to options that have a property replacing them. Everything here either
        // never applied on this path (the project-mode options are ignored in folder mode, which is
        // the only mode the NuGet targets use) or has no property to point at, so there is nothing to
        // migrate and a notice would be noise on an ordinary application flag.
        //
        // The removed generic WinAppRunArgs fallback also produced advice that fails: for
        // `--configuration Release` it suggested WinAppRunArgs="--configuration", dropping the value,
        // and that command errors with "Required argument missing for option: '--configuration'".
        await CreateTestManifestAsync();
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["run", _tempDirectory.FullName, "--caller", "nuget-package", "--", forwarded]);

        Assert.AreEqual(0, exitCode);
        Assert.IsFalse($"{ConsoleStdOut}{ConsoleStdErr}{TestAnsiConsole.Output}".Contains("was passed to your application", StringComparison.Ordinal),
            $"'{forwarded}' has no replacement property, so it must not produce a migration notice");
    }

    [TestMethod]
    public async Task RunCommand_NuGetCaller_ForwardsOptionWithValue_KeepsTheValueWithTheApp()
    {
        // Regression guard for the dropped-value problem: an option taking a value must reach the app
        // intact, and must not be described by a notice that omits the value.
        await CreateTestManifestAsync();
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["run", _tempDirectory.FullName, "--caller", "nuget-package", "--", "--configuration", "Release"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual("--configuration Release", _fakeAppLauncherService.LaunchCalls[0].Arguments,
            "The option and its value must both reach the app");
        Assert.IsFalse($"{ConsoleStdOut}{ConsoleStdErr}{TestAnsiConsole.Output}".Contains("WinAppRunArgs", StringComparison.Ordinal),
            "No WinAppRunArgs suggestion should be emitted for an option whose value it would drop");
    }

    [TestMethod]
    public async Task RunCommand_NuGetCaller_ForwardsUnknownArgument_StaysSilent()
    {
        // A genuine app argument never had a winapp meaning, so there is nothing to migrate and a
        // notice would be pure noise on every single run.
        await CreateTestManifestAsync();
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["run", _tempDirectory.FullName, "--caller", "nuget-package", "--", "--devtools"]);

        Assert.AreEqual(0, exitCode);
        Assert.IsFalse($"{ConsoleStdOut}{ConsoleStdErr}{TestAnsiConsole.Output}".Contains("was passed to your application", StringComparison.Ordinal),
            "An argument winapp never owned should not produce a migration notice");
    }

    [TestMethod]
    public async Task RunCommand_NuGetCaller_ForwardsAttachedValueOption_StillPointsAtTheProperty()
    {
        // `--executable=foo.exe` and `--detach=true` configured winapp before this change just as the
        // separated spelling did, so they need the same notice. Matching the whole token would miss
        // them and let a previously-working invocation change meaning silently.
        await CreateTestManifestAsync();
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["run", _tempDirectory.FullName, "--caller", "nuget-package", "--", "--executable=foo.exe"]);

        Assert.AreEqual(0, exitCode);
        var output = $"{ConsoleStdOut}{ConsoleStdErr}{TestAnsiConsole.Output}";
        StringAssert.Contains(output, "'--executable=foo.exe' was passed to your application",
            "The notice should quote the token exactly as the user typed it");
        StringAssert.Contains(output, "WinAppRunExecutable=<path>",
            "The property lookup should use the option name, not the attached-value token");
    }

    [TestMethod]
    public async Task RunCommand_DirectCliCaller_ForwardsKnownOption_StaysSilent()
    {
        // `winapp run . -- --detach` is an explicit, unambiguous request to forward the token. Only
        // the NuGet path had its meaning changed, so only it gets the notice.
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--", "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.IsFalse($"{ConsoleStdOut}{ConsoleStdErr}{TestAnsiConsole.Output}".Contains("was passed to your application", StringComparison.Ordinal),
            "A direct CLI invocation should not be told to use MSBuild properties");
    }

    [TestMethod]
    public async Task RunCommand_BareDoubleDash_LaunchesWithNoArgs()
    {
        // A bare '--' separator with nothing after it should launch successfully with no app args.
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--"]);

        Assert.AreEqual(0, exitCode, "Command should succeed with a bare --");
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchCalls.Count, "Application should be launched");
        Assert.IsNull(_fakeAppLauncherService.LaunchCalls[0].Arguments,
            "No app args should be passed when nothing follows --");
    }

    [TestMethod]
    public async Task RunCommand_DoubleDashPassthrough_MultipleArgs_AllForwarded()
    {
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--", "--flag1", "v1", "--flag2", "v2", "--flag3"]);

        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual("--flag1 v1 --flag2 v2 --flag3",
            _fakeAppLauncherService.LaunchCalls[0].Arguments,
            "All passthrough tokens should be forwarded in order");
    }

    [TestMethod]
    public async Task RunCommand_DoubleDashPassthrough_MergesWithArgsOption()
    {
        // --args value and tokens after -- are both forwarded, --args first.
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--args", "--existing", "--", "--flag"]);

        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual("--existing --flag", _fakeAppLauncherService.LaunchCalls[0].Arguments,
            "--args value and -- passthrough args should both be forwarded");
    }

    [TestMethod]
    public async Task RunCommand_DoubleDashPassthrough_ValueWithSpace_QuotedInLaunchArgs()
    {
        // This test verifies the full pipeline: token → JoinArguments → launcher.
        // A value that contains a space must be quoted so the launched app's CommandLineToArgvW
        // recovers the original token correctly.
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--", "--title", "hello world"]);

        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchCalls.Count, "Application should be launched");
        Assert.AreEqual("--title \"hello world\"", _fakeAppLauncherService.LaunchCalls[0].Arguments,
            "Values containing spaces must be quoted in the final command-line string");
    }

    // --- Handler: default-to-current-directory (no input path) ---

    [TestMethod]
    public async Task RunCommand_NoInputFolder_DefaultsToCurrentDirectory()
    {
        // `winapp run` with no path must default to the current directory (matches `dotnet run`).
        // ICurrentDirectoryProvider is wired to _tempDirectory in the test harness, where
        // CreateTestManifestAsync places a manifest, so folder mode resolves and launches.
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(0, exitCode, "Command with no path should default to cwd and succeed");
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchCalls.Count,
            "Application should be launched from the current directory");
        Assert.IsNull(_fakeAppLauncherService.LaunchCalls[0].Arguments,
            "No app args should be passed when only the default input is used");
    }

    [TestMethod]
    public async Task RunCommand_NoInputFolderWithPassthrough_UsesCwdAndForwardsAppArgs()
    {
        // The tricky parser-interaction case: `winapp run -- --appflag value`. With input-folder
        // optional, System.CommandLine binds the first post-'--' token ('--appflag') to the
        // positional. The handler must detect that the token was stolen from passthrough, fall back
        // to the current directory, AND still forward '--appflag value' to the launched app.
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["--", "--appflag", "value"]);

        Assert.AreEqual(0, exitCode, "No-path invocation with passthrough should default to cwd and succeed");
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchCalls.Count,
            "Application should be launched from the current directory");
        Assert.AreEqual("--appflag value", _fakeAppLauncherService.LaunchCalls[0].Arguments,
            "The post-'--' tokens must be forwarded to the app, not consumed as the input path");
    }

    // --- Handler: unknown-token rejection ---

    [TestMethod]
    public async Task RunCommand_UnknownOptionBeforeDoubleDash_ReturnsError()
    {
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--unknown-winapp-option", "--", "--app-flag"]);

        Assert.AreEqual(1, exitCode, "Unknown winapp options before -- should still fail");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "No application should be launched");
    }

    [TestMethod]
    public async Task RunCommand_BadTokenBeforeDoubleDash_RejectsWithError_DoesNotForwardGoodToken()
    {
        // Explicit test for: winapp run . --badtoken -- --cooltoken
        // --badtoken is an unrecognised winapp option BEFORE '--' → error, exit 1
        // --cooltoken is a legitimate passthrough AFTER '--' → NOT forwarded (command aborts)
        // This ensures the bad pre-dash token is caught and no launch occurs.
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--badtoken", "--", "--cooltoken"]);

        Assert.AreEqual(1, exitCode, "Bad pre-dash token must cause exit code 1");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count,
            "App must NOT be launched when a bad pre-dash token is present");
    }

    [TestMethod]
    public async Task RunCommand_UnknownOptionWithNoDoubleDash_ReturnsError()
    {
        // Ensures the guard fires even when the user never typed '--'.
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--unknown-winapp-option"]);

        Assert.AreEqual(1, exitCode, "Unknown options without -- should fail");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "No application should be launched");
    }

    [TestMethod]
    public async Task RunCommand_SameTokenBeforeAndAfterDoubleDash_ReturnsError()
    {
        // The duplicate-value edge case: the same string appears before '--' (bad) and after '--'
        // (legitimate passthrough).  A naïve set-based check would cancel them out and let the bad
        // token through.  The count-based implementation must catch it.
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--flag", "--", "--flag"]);

        Assert.AreEqual(1, exitCode,
            "The pre-dash unknown token must be rejected even when the same value appears as passthrough");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "No application should be launched");
    }

    // --- Handler: passthrough interacts correctly with other mode flags ---

    [TestMethod]
    public async Task RunCommand_DoubleDashPassthrough_WithNoLaunch_Succeeds()
    {
        // --no-launch registers the package without launching; passthrough args are collected but
        // irrelevant — the important thing is the command does NOT error just because -- was used.
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--no-launch", "--", "--app-flag"]);

        Assert.AreEqual(0, exitCode, "-- passthrough should not cause an error when combined with --no-launch");
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutCalls.Count, "Package should still be registered");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "App must NOT be launched with --no-launch");
    }

    [TestMethod]
    public async Task RunCommand_DoubleDashPassthrough_WithDetach_ForwardsArgs()
    {
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--detach", "--", "--app-flag", "value"]);

        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchCalls.Count, "Application should be launched");
        Assert.AreEqual("--app-flag value", _fakeAppLauncherService.LaunchCalls[0].Arguments,
            "Passthrough args should be forwarded to the launcher in --detach mode");
    }

    [TestMethod]
    public async Task RunCommand_DoubleDashPassthrough_WithDebugOutput_ForwardsArgs()
    {
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--debug-output", "--", "--app-flag", "value"]);

        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchCalls.Count, "Application should be launched");
        Assert.AreEqual("--app-flag value", _fakeAppLauncherService.LaunchCalls[0].Arguments,
            "Passthrough args should be forwarded to the launcher in --debug-output mode");
        Assert.AreEqual(1, _fakeDebugOutputService.AttachCalls.Count, "Debug service should still be called");
    }

    [TestMethod]
    public async Task RunCommand_DoubleDashPassthrough_ForwardsLiteralDoubleDash()
    {
        // A '--' that appears AFTER the separator is an app argument, not another separator.
        // It must be forwarded as the literal string "--".
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--", "--"]);

        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchCalls.Count, "Application should be launched");
        Assert.AreEqual("--", _fakeAppLauncherService.LaunchCalls[0].Arguments,
            "A literal -- after the passthrough separator should be forwarded to the app");
    }

    [TestMethod]
    public async Task RunCommand_BadTokenBeforeDoubleDash_WithJson_EmitsJsonErrorBody()
    {
        // Regression for: in --json mode the logger is suppressed, so a bad pre-dash token
        // would otherwise produce only exit code 1 with empty stdout. The handler must emit
        // a structured JSON error body so machine-readable callers can surface a useful message.
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--json", "--badtoken", "--", "--cooltoken"]);

        Assert.AreEqual(1, exitCode, "Bad pre-dash token must cause exit code 1 even in --json mode");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "App must NOT be launched");

        // The handler must have written a single-object JSON document (with an Error field that names
        // the offending token) to stdout. With the M1 fix, run's --json payload is emitted without
        // Spectre word-wrapping, so a strict parser accepts it directly even though this message
        // (~105 chars) exceeds the redirected console width.
        var json = ParseJsonOutput();
        var error = json.GetProperty("Error").GetString();
        Assert.IsNotNull(error, "JSON output must contain an Error field in --json mode");
        StringAssert.Contains(error, "--badtoken", "Error message should name the offending token");
    }

    // --- BuildAliasProcessStartInfo: passthrough forwarded into execution-alias ProcessStartInfo ---

    [TestMethod]
    public void BuildAliasProcessStartInfo_WithAppArgs_SetsArgumentsOnProcessStartInfo()
    {
        // The execution-alias launch path uses a separate Process.Start, so this test
        // verifies that passthrough args (after merge with --args) are forwarded into
        // ProcessStartInfo.Arguments verbatim.
        var psi = RunCommand.Handler.BuildAliasProcessStartInfo("myalias.exe", "--flag value");

        Assert.AreEqual("myalias.exe", psi.FileName);
        Assert.AreEqual("--flag value", psi.Arguments);
        Assert.IsFalse(psi.UseShellExecute, "UseShellExecute must be false so stdio inherits");
    }

    [TestMethod]
    public void BuildAliasProcessStartInfo_WithQuotedAppArgs_PreservesQuoting()
    {
        // The merged appArgs string for the alias path has already been escaped via
        // WindowsCommandLine.JoinArguments. BuildAliasProcessStartInfo must pass the
        // escaped string through unchanged so CommandLineToArgvW recovers original tokens.
        var psi = RunCommand.Handler.BuildAliasProcessStartInfo("myalias.exe", "--title \"hello world\"");

        Assert.AreEqual("--title \"hello world\"", psi.Arguments);
    }

    [TestMethod]
    public void BuildAliasProcessStartInfo_WithNullAppArgs_LeavesArgumentsEmpty()
    {
        var psi = RunCommand.Handler.BuildAliasProcessStartInfo("myalias.exe", null);

        Assert.AreEqual("myalias.exe", psi.FileName);
        Assert.AreEqual(string.Empty, psi.Arguments,
            "Null appArgs must NOT set Arguments (default ProcessStartInfo.Arguments is empty string)");
    }

    [TestMethod]
    public void BuildAliasProcessStartInfo_WithEmptyAppArgs_LeavesArgumentsEmpty()
    {
        var psi = RunCommand.Handler.BuildAliasProcessStartInfo("myalias.exe", string.Empty);

        Assert.AreEqual(string.Empty, psi.Arguments,
            "Empty appArgs must NOT set Arguments");
    }

    #endregion

    #region Mutually-exclusive option / structured-error tests

    [TestMethod]
    public async Task RunCommand_UnregisterOnExitWithNoLaunch_ReturnsError()
    {
        // --unregister-on-exit and --no-launch are mutually exclusive: unregister-on-exit only
        // makes sense when the app is actually launched.
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--unregister-on-exit", "--no-launch"]);

        Assert.AreEqual(1, exitCode,
            "Command should fail when both --unregister-on-exit and --no-launch are specified");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "No application should be launched");
    }

    [TestMethod]
    public async Task RunCommand_UnrecognizedPreDashToken_WithJson_EmitsStructuredError()
    {
        // In --json mode the human-readable logger is suppressed, so an unrecognized pre-dash
        // token must still surface a machine-readable error object (and fail with exit code 1).
        TestAnsiConsole.Profile.Width = 1000; // avoid line-wrapping that would corrupt the JSON string
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--json", "--unknown-winapp-option"]);

        Assert.AreEqual(1, exitCode, "Unrecognized pre-dash token must fail even in --json mode");

        var json = ParseJsonOutput();
        Assert.IsTrue(json.TryGetProperty("Error", out var error),
            "JSON output should contain an Error property when a token is unrecognized");
        StringAssert.Contains(error.GetString(), "Unrecognized argument",
            "The structured error should explain the unrecognized argument");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "No application should be launched");
    }

    #endregion

    #region Default launch + wait tests

    [TestMethod]
    public async Task RunCommand_DefaultLaunch_LaunchesByAumidAndReturnsZero()
    {
        // Neither --no-launch, --detach, --with-alias nor --debug-output: the command launches
        // via AUMID and then waits for the (fake) process to exit. The fake launcher returns a
        // PID that is not a live process, so Process.GetProcessById throws ArgumentException,
        // which the handler treats as "already exited" (exit code 0).
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName]);

        Assert.AreEqual(0, exitCode, "A launched-then-exited app should return success");
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchCalls.Count, "The app should be launched via AUMID");
    }

    [TestMethod]
    public async Task RunCommand_DefaultLaunch_WithJson_PrintsAumidAndProcessId()
    {
        // The default (waiting) launch path still emits JSON when --json is passed.
        TestAnsiConsole.Profile.Width = 1000;
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--json"]);

        Assert.AreEqual(0, exitCode);
        var json = ParseJsonOutput();
        Assert.AreEqual("TestPackage_fakefamily!TestApp", json.GetProperty("AUMID").GetString());
        Assert.AreEqual(_fakeAppLauncherService.FakeProcessId, json.GetProperty("ProcessId").GetUInt32(),
            "The launched PID should be reported in JSON on the default launch path");
    }

    [TestMethod]
    public async Task RunCommand_DefaultLaunch_HugeProcessId_TreatedAsSuccess()
    {
        // PIDs above int.MaxValue cannot be tracked via Process.GetProcessById, so the handler
        // skips the wait and returns success.
        _fakeAppLauncherService.FakeProcessId = 3_000_000_000; // > int.MaxValue
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName]);

        Assert.AreEqual(0, exitCode, "A PID above int.MaxValue is treated as an immediate success");
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchCalls.Count);
    }

    [TestMethod]
    public async Task RunCommand_DefaultLaunch_WaitsForRealProcessExit_PropagatesExitCode()
    {
        // Point the fake launcher at a real, short-lived process so the handler exercises the
        // Process.GetProcessById -> WaitForExitAsync -> ExitCode path and propagates the exit code.
        await CreateTestManifestAsync();
        using var helper = StartHelperProcess("/c exit 3");
        _fakeAppLauncherService.FakeProcessId = (uint)helper.Id;
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName]);

        // Either we attached and observed exit code 3, or the process already exited before we
        // could attach (ArgumentException path) and the handler reported success. Both are valid
        // real behaviours of the wait path; assert it did not hang or fault.
        Assert.IsTrue(exitCode is 3 or 0, $"Expected 3 (observed exit) or 0 (already exited), got {exitCode}");
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchCalls.Count);
    }

    [TestMethod]
    public async Task RunCommand_DefaultLaunch_CancelledDuringWait_TerminatesAndReturnsCancelled()
    {
        // Ctrl+C while the command is blocked in the post-launch WaitForExit terminates the
        // package's processes and returns -1. A real, longer-lived helper process stands in for
        // the launched app; the token is cancelled well after the (instant, faked) status phase
        // completes but long before the helper would exit on its own.
        await CreateTestManifestAsync();
        using var longProc = StartHelperProcess("/c ping -n 6 127.0.0.1");
        _fakeAppLauncherService.FakeProcessId = (uint)longProc.Id;
        var handler = GetRequiredService<RunCommand.Handler>();
        var command = GetRequiredService<RunCommand>();
        var parseResult = command.Parse([_tempDirectory.FullName]);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));

        var exitCode = await handler.InvokeAsync(parseResult, cts.Token);

        Assert.AreEqual(-1, exitCode, "Cancellation during the wait returns -1");
        Assert.AreEqual(1, _fakeAppLauncherService.TerminateCalls.Count, "The package's processes should be terminated on cancel");
        TryKill(longProc);
    }

    #endregion

    #region --unregister-on-exit tests

    [TestMethod]
    public async Task RunCommand_UnregisterOnExit_DefaultLaunch_UnregistersOnlyDevPackages()
    {
        // After the launched app exits, dev-mode packages matching the identity name are
        // unregistered. Non-dev packages are skipped.
        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("TestPackage_1.0.0.0_x64__dev", "TestPackage", "1.0.0.0", null, IsDevelopmentMode: true),
            new DevPackageInfo("OtherPackage_1.0.0.0_x64__prod", "OtherPackage", "1.0.0.0", null, IsDevelopmentMode: false),
        ];
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--unregister-on-exit"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakePackageRegistrationService.FindDevPackagesCalls.Count);
        Assert.AreEqual("TestPackage", _fakePackageRegistrationService.FindDevPackagesCalls[0]);
        Assert.AreEqual(1, _fakePackageRegistrationService.UnregisterCalls.Count, "Only the dev-mode package should be unregistered");
        Assert.AreEqual("TestPackage", _fakePackageRegistrationService.UnregisterCalls[0].PackageName);
        Assert.IsFalse(_fakePackageRegistrationService.UnregisterCalls[0].PreserveAppData, "unregister-on-exit should not preserve app data");
    }

    [TestMethod]
    public async Task RunCommand_UnregisterOnExit_SwallowsUnregisterFailures()
    {
        // A failure while unregistering on exit must not fault the command (it is best-effort).
        _fakePackageRegistrationService.FindDevPackagesThrows = new InvalidOperationException("boom");
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--unregister-on-exit"]);

        Assert.AreEqual(0, exitCode, "Unregister failures on exit are non-fatal");
    }

    [TestMethod]
    public async Task RunCommand_DebugOutput_UnregisterOnExit_Unregisters()
    {
        // The --debug-output launch path also honours --unregister-on-exit after the debug loop.
        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("TestPackage_1.0.0.0_x64__dev", "TestPackage", "1.0.0.0", null, IsDevelopmentMode: true),
        ];
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--debug-output", "--unregister-on-exit"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeDebugOutputService.AttachCalls.Count, "The debug loop should run");
        Assert.AreEqual(1, _fakePackageRegistrationService.UnregisterCalls.Count, "The dev package should be unregistered after the debug loop");
    }

    #endregion

    #region --with-alias launch tests

    [TestMethod]
    public async Task RunCommand_WithAlias_ProcessedManifestMissing_ReturnsError()
    {
        // --with-alias reads the processed manifest from the AppX output directory. When it is
        // absent, the command cannot determine an execution alias and fails.
        await CreateTestManifestAsync();
        var outputDir = _tempDirectory.CreateSubdirectory("appx-empty");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--with-alias", "--output-appx-directory", outputDir.FullName]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "Processed manifest not found");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "AUMID launch must not be used with --with-alias");
    }

    [TestMethod]
    public async Task RunCommand_WithAlias_NoExecutionAlias_ReturnsError()
    {
        // A processed manifest without any ExecutionAlias entry fails with a helpful message.
        await CreateTestManifestAsync();
        var outputDir = await CreateProcessedManifestAsync("appx-noalias", alias: null);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--with-alias", "--output-appx-directory", outputDir.FullName]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "No execution alias found");
    }

    [TestMethod]
    public async Task RunCommand_WithAlias_UnsafeAlias_ReturnsError()
    {
        // An attacker-controlled alias that is not a bare .exe filename is rejected before launch.
        await CreateTestManifestAsync();
        var outputDir = await CreateProcessedManifestAsync("appx-unsafe", alias: "..\\evil.exe");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--with-alias", "--output-appx-directory", outputDir.FullName]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "is not a valid bare");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count);
    }

    [TestMethod]
    public async Task RunCommand_WithAlias_ProxyNotFound_ReturnsError()
    {
        // A safe alias whose Windows App Execution Alias proxy is not registered on this machine
        // fails with a "not found at the expected location" error rather than launching.
        await CreateTestManifestAsync();
        var outputDir = await CreateProcessedManifestAsync("appx-proxy", alias: "winapp-run-test-missing.exe");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--with-alias", "--output-appx-directory", outputDir.FullName]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "was not found");
    }

    [TestMethod]
    public async Task RunCommand_WithAlias_ResolveProxyReturnsNull_ReturnsError()
    {
        // The alias-resolution seam can yield null when no proxy path can be produced at all. The
        // `aliasFile is null` operand of the proxy guard must be covered: the command reports the
        // proxy-not-found error and returns 1 without falling back to an AUMID launch.
        await CreateTestManifestAsync();
        var outputDir = await CreateProcessedManifestAsync("appx-nullproxy", alias: "winapp-run-test.exe");
        var handler = GetRequiredService<RunCommand.Handler>();
        handler.ResolveAliasProxy = _ => null;
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--with-alias", "--output-appx-directory", outputDir.FullName]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "was not found");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "The command must not fall back to an AUMID launch");
    }

    [TestMethod]
    public async Task RunCommand_WithAlias_UnregisterOnExit_UnregistersAfterAliasPath()
    {
        // --with-alias combined with --unregister-on-exit unregisters dev packages after the
        // alias launch path returns (here it returns early because the proxy is missing).
        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("TestPackage_1.0.0.0_x64__dev", "TestPackage", "1.0.0.0", null, IsDevelopmentMode: true),
        ];
        await CreateTestManifestAsync();
        var outputDir = await CreateProcessedManifestAsync("appx-proxy2", alias: "winapp-run-test-missing.exe");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--with-alias", "--unregister-on-exit", "--output-appx-directory", outputDir.FullName]);

        Assert.AreEqual(1, exitCode, "The alias proxy is missing, so the alias path returns 1");
        Assert.AreEqual(1, _fakePackageRegistrationService.UnregisterCalls.Count, "Dev package should still be unregistered on exit");
    }

    [TestMethod]
    public async Task RunCommand_WithAlias_LaunchesViaProxy_ReturnsProcessExitCode()
    {
        // Happy path: with a registered alias proxy present, --with-alias resolves the proxy and
        // launches it, propagating the launched process's exit code. The two operating-system
        // boundaries (alias resolution + process start) are replaced with test seams so the test
        // needs no real WindowsApps proxy registration and does not spawn the resolved binary.
        await CreateTestManifestAsync();
        var outputDir = await CreateProcessedManifestAsync("appx-launch", alias: "winapp-run-test.exe");
        var aliasProxy = CreateExistingFile("winapp-run-test.exe");
        var handler = GetRequiredService<RunCommand.Handler>();
        handler.ResolveAliasProxy = _ => aliasProxy;
        Process? started = null;
        handler.ProcessStarter = _ => started = StartHelperProcess("/c exit 7");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--with-alias", "--output-appx-directory", outputDir.FullName]);

        Assert.AreEqual(7, exitCode, "The launched alias process's exit code should be propagated");
        Assert.IsNotNull(started, "The process-start seam should have been invoked");
    }

    [TestMethod]
    public async Task RunCommand_WithAlias_ProcessStartReturnsNull_ReturnsError()
    {
        // Defensive branch: if Process.Start returns null the command reports a start failure.
        await CreateTestManifestAsync();
        var outputDir = await CreateProcessedManifestAsync("appx-null", alias: "winapp-run-test.exe");
        var aliasProxy = CreateExistingFile("winapp-run-test.exe");
        var handler = GetRequiredService<RunCommand.Handler>();
        handler.ResolveAliasProxy = _ => aliasProxy;
        handler.ProcessStarter = _ => null;
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--with-alias", "--output-appx-directory", outputDir.FullName]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "Failed to start process via execution alias");
    }

    [TestMethod]
    public async Task RunCommand_WithAlias_DebugOutput_RunsDebugLoopAndReturnsItsExitCode()
    {
        // --with-alias + --debug-output runs the debug event loop against the launched process and
        // returns the loop's exit code instead of plain WaitForExit.
        await CreateTestManifestAsync();
        var outputDir = await CreateProcessedManifestAsync("appx-dbg", alias: "winapp-run-test.exe");
        var aliasProxy = CreateExistingFile("winapp-run-test.exe");
        _fakeDebugOutputService.FakeExitCode = 42;
        var handler = GetRequiredService<RunCommand.Handler>();
        handler.ResolveAliasProxy = _ => aliasProxy;
        handler.ProcessStarter = _ => StartHelperProcess("/c exit 0");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--with-alias", "--debug-output", "--output-appx-directory", outputDir.FullName]);

        Assert.AreEqual(42, exitCode, "The debug loop's exit code should be returned in --debug-output mode");
        Assert.AreEqual(1, _fakeDebugOutputService.AttachCalls.Count, "The debug loop should attach to the launched process");
    }

    [TestMethod]
    public async Task RunCommand_WithAlias_DebugOutput_CancelledDuringLoop_TerminatesPackageProcesses()
    {
        // --with-alias + --debug-output: a Ctrl+C that arrives while the debug loop is running makes
        // the loop return, after which the command terminates the package's processes before
        // returning the loop's exit code. Covers the alias-path post-loop cancellation cleanup.
        await CreateTestManifestAsync();
        var outputDir = await CreateProcessedManifestAsync("appx-dbgcancel", alias: "winapp-run-test.exe");
        var aliasProxy = CreateExistingFile("winapp-run-test.exe");
        _fakeDebugOutputService.FakeExitCode = 7;
        var handler = GetRequiredService<RunCommand.Handler>();
        handler.ResolveAliasProxy = _ => aliasProxy;
        handler.ProcessStarter = _ => StartHelperProcess("/c exit 0");
        var command = GetRequiredService<RunCommand>();
        var parseResult = command.Parse([_tempDirectory.FullName, "--with-alias", "--debug-output", "--output-appx-directory", outputDir.FullName]);
        using var cts = new CancellationTokenSource();
        _fakeDebugOutputService.CancelTokenDuringLoop = cts;

        var exitCode = await handler.InvokeAsync(parseResult, cts.Token);

        Assert.AreEqual(7, exitCode, "The debug loop's exit code is returned even after cancellation cleanup");
        Assert.AreEqual(1, _fakeDebugOutputService.AttachCalls.Count, "The debug loop should have run");
        Assert.AreEqual(1, _fakeAppLauncherService.TerminateCalls.Count,
            "Cancellation after the debug loop should terminate the package's processes on the alias path");
    }

    [TestMethod]
    public async Task RunCommand_WithAlias_ProcessStartThrows_ReturnsError()
    {
        // If starting the resolved proxy throws, the exception is caught and reported as a launch failure.
        await CreateTestManifestAsync();
        var outputDir = await CreateProcessedManifestAsync("appx-throw", alias: "winapp-run-test.exe");
        var aliasProxy = CreateExistingFile("winapp-run-test.exe");
        var handler = GetRequiredService<RunCommand.Handler>();
        handler.ResolveAliasProxy = _ => aliasProxy;
        handler.ProcessStarter = _ => throw new InvalidOperationException("boom");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--with-alias", "--output-appx-directory", outputDir.FullName]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "Failed to launch via execution alias");
    }

    [TestMethod]
    public async Task RunCommand_WithAlias_CancelledDuringWait_TerminatesAndReturnsCancelled()
    {
        // Ctrl+C while blocked in the alias-launch WaitForExit terminates the package's processes
        // and returns -1. The process-start seam yields a real, longer-lived helper process and the
        // token is cancelled during the wait.
        await CreateTestManifestAsync();
        var outputDir = await CreateProcessedManifestAsync("appx-cancel", alias: "winapp-run-test.exe");
        var aliasProxy = CreateExistingFile("winapp-run-test.exe");
        var helperPid = 0;
        var processStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = GetRequiredService<RunCommand.Handler>();
        handler.ResolveAliasProxy = _ => aliasProxy;
        handler.ProcessStarter = _ =>
        {
            var p = StartHelperProcess("/c ping -n 6 127.0.0.1");
            helperPid = p.Id;
            processStarted.SetResult();
            return p;
        };
        var command = GetRequiredService<RunCommand>();
        var parseResult = command.Parse([_tempDirectory.FullName, "--with-alias", "--output-appx-directory", outputDir.FullName]);
        using var cts = new CancellationTokenSource();

        var invocation = handler.InvokeAsync(parseResult, cts.Token);
        await processStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        var exitCode = await invocation;

        Assert.AreEqual(-1, exitCode, "Cancellation during the alias wait returns -1");
        Assert.AreEqual(1, _fakeAppLauncherService.TerminateCalls.Count, "The package's processes should be terminated on cancel");
        TryKillByPid(helperPid);
    }

    #endregion

    #region Manifest resolution + structured error tests

    [TestMethod]
    public async Task RunCommand_ResolvesManifestFromCurrentDirectory_WhenNotInInputFolder()
    {
        // Manifest resolution priority falls back to the current directory when neither --manifest
        // nor the input folder contains a manifest. The current directory provider points at
        // _tempDirectory, so place the manifest there and use an empty input subfolder.
        await CreateTestManifestAsync(_tempDirectory.FullName);
        var inputFolder = _tempDirectory.CreateSubdirectory("empty-input");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [inputFolder.FullName, "--no-launch"]);

        Assert.AreEqual(0, exitCode, "The manifest from the current directory should be used");
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutCalls.Count, "Identity should be created using the cwd manifest");
    }

    [TestMethod]
    public async Task RunCommand_MultipleUnrecognizedPreDashTokens_WithJson_EmitsPluralError()
    {
        // Two or more unrecognized pre-'--' tokens produce a pluralized structured error in JSON mode.
        TestAnsiConsole.Profile.Width = 1000;
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--json", "--unknown-a", "--unknown-b"]);

        Assert.AreEqual(1, exitCode);
        var json = ParseJsonOutput();
        Assert.IsTrue(json.TryGetProperty("Error", out var error));
        StringAssert.Contains(error.GetString(), "Unrecognized arguments:", "Multiple bad tokens should use the plural form");
    }

    #endregion

    #region Alias-launch test helpers

    private const string AliasManifestTemplate = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                 xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"
                 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
                 IgnorableNamespaces="uap uap5 rescap">
          <Identity Name="TestPackage" Publisher="CN=TestPublisher" Version="1.0.0.0" />
          <Properties>
            <DisplayName>Test Package</DisplayName>
            <PublisherDisplayName>Test Publisher</PublisherDisplayName>
            <Description>Test package</Description>
            <Logo>Assets\Logo.png</Logo>
          </Properties>
          <Dependencies>
            <TargetDeviceFamily Name="Windows.Universal" MinVersion="10.0.18362.0" MaxVersionTested="10.0.26100.0" />
          </Dependencies>
          <Applications>
            <Application Id="TestApp" Executable="TestApp.exe" EntryPoint="TestApp.App">
              <uap:VisualElements DisplayName="Test App" Description="Test application"
                                  BackgroundColor="#777777" Square150x150Logo="Assets\Logo.png" Square44x44Logo="Assets\Logo.png" />
              <Extensions>
                <uap5:Extension Category="windows.appExecutionAlias">
                  <uap5:AppExecutionAlias>
                    <uap5:ExecutionAlias Alias="__ALIAS__" />
                  </uap5:AppExecutionAlias>
                </uap5:Extension>
              </Extensions>
            </Application>
          </Applications>
          <Capabilities>
            <rescap:Capability Name="runFullTrust" />
          </Capabilities>
        </Package>
        """;

    /// <summary>
    /// Creates an AppX output directory containing a "processed" appxmanifest.xml. When
    /// <paramref name="alias"/> is null the manifest has no ExecutionAlias entry; otherwise it
    /// embeds the given alias so the --with-alias path can extract and validate it.
    /// </summary>
    private async Task<DirectoryInfo> CreateProcessedManifestAsync(string subdirName, string? alias)
    {
        var dir = _tempDirectory.CreateSubdirectory(subdirName);
        var content = alias is null
            ? TestManifestContent
            : AliasManifestTemplate.Replace("__ALIAS__", alias);
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "appxmanifest.xml"), content, TestContext.CancellationToken);
        return dir;
    }

    /// <summary>
    /// Creates a real, existing file inside the temp directory and returns a <see cref="FileInfo"/>
    /// for it. Used to stand in for a resolved Windows App Execution Alias proxy so the
    /// <c>aliasFile.Exists</c> check passes without registering a real proxy.
    /// </summary>
    private FileInfo CreateExistingFile(string name)
    {
        var path = Path.Combine(_tempDirectory.FullName, name);
        File.WriteAllText(path, string.Empty);
        return new FileInfo(path);
    }

    /// <summary>
    /// Starts a short-lived real cmd.exe process for exercising the process-wait path.
    /// </summary>
    private static Process StartHelperProcess(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            // Use the fixed, fully-qualified System32 cmd.exe rather than the ComSpec
            // environment variable so the helper cannot be redirected via a hijacked
            // environment/PATH entry.
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        return Process.Start(psi)!;
    }

    /// <summary>Best-effort termination of a helper process handle owned by the test.</summary>
    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may have already exited or been disposed — nothing to clean up.
        }
    }

    /// <summary>Best-effort termination of a helper process by PID (used when the product code owns the Process object).</summary>
    private static void TryKillByPid(int pid)
    {
        if (pid == 0)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process may have already exited — nothing to clean up.
        }
    }

    #endregion

    #region Project-mode option parsing

    [TestMethod]
    public void ParseOptions_Configuration_DefaultsToDebug()
    {
        var command = GetRequiredService<RunCommand>();

        var parseResult = command.Parse([_tempDirectory.FullName]);

        Assert.IsEmpty(parseResult.Errors);
        Assert.AreEqual("Debug", parseResult.GetValue(RunCommand.ConfigurationOption));
    }

    [TestMethod]
    public void ParseOptions_ConfigurationShortAlias_IsParsed()
    {
        var command = GetRequiredService<RunCommand>();

        var parseResult = command.Parse([_tempDirectory.FullName, "-c", "Release"]);

        Assert.IsEmpty(parseResult.Errors);
        Assert.AreEqual("Release", parseResult.GetValue(RunCommand.ConfigurationOption));
    }

    [TestMethod]
    public void ParseOptions_ArchAndRuntime_AreParsed()
    {
        var command = GetRequiredService<RunCommand>();

        var parseResult = command.Parse([_tempDirectory.FullName, "--arch", "arm64", "-r", "win-x64"]);

        Assert.IsEmpty(parseResult.Errors);
        Assert.AreEqual("arm64", parseResult.GetValue(RunCommand.ArchOption));
        Assert.AreEqual("win-x64", parseResult.GetValue(RunCommand.RuntimeOption));
    }

    [TestMethod]
    public void ParseOptions_FrameworkShortAlias_IsParsed()
    {
        var command = GetRequiredService<RunCommand>();

        var parseResult = command.Parse([_tempDirectory.FullName, "-f", "net10.0-windows10.0.26100.0"]);

        Assert.IsEmpty(parseResult.Errors);
        Assert.AreEqual("net10.0-windows10.0.26100.0", parseResult.GetValue(RunCommand.FrameworkOption));
    }

    [TestMethod]
    public void ParseOptions_NoBuildAndNoRestore_AreParsed()
    {
        var command = GetRequiredService<RunCommand>();

        var parseResult = command.Parse([_tempDirectory.FullName, "--no-build", "--no-restore"]);

        Assert.IsEmpty(parseResult.Errors);
        Assert.IsTrue(parseResult.GetValue(RunCommand.NoBuildOption));
        Assert.IsTrue(parseResult.GetValue(RunCommand.NoRestoreOption));
    }

    [TestMethod]
    public void ParseOptions_RepeatableProperty_CollectsDotnetStyleTokens()
    {
        var command = GetRequiredService<RunCommand>();

        // System.CommandLine splits -p:Name=Value on the first ':' so dotnet-style tokens work.
        var parseResult = command.Parse(
            [_tempDirectory.FullName, "-p", "WindowsPackageType=None", "-p", "Foo=Bar"]);

        Assert.IsEmpty(parseResult.Errors);
        var properties = parseResult.GetValue(RunCommand.PropertyOption);
        Assert.IsNotNull(properties);
        CollectionAssert.AreEquivalent(ForcedUnpackagedProperties, properties);
    }

    #endregion

    #region TryResolveArchitecture

    [TestMethod]
    public void TryResolveArchitecture_NoOptions_UsesProcessDefault()
    {
        var ok = RunCommand.Handler.TryResolveArchitecture(null, null, out var arch, out var error);

        Assert.IsTrue(ok);
        Assert.IsNull(error);
        CollectionAssert.Contains(SupportedArchitectures, arch);
    }

    [TestMethod]
    public void TryResolveArchitecture_ArchOption_IsNormalized()
    {
        var ok = RunCommand.Handler.TryResolveArchitecture("ARM64", null, out var arch, out var error);

        Assert.IsTrue(ok);
        Assert.IsNull(error);
        Assert.AreEqual("arm64", arch);
    }

    [TestMethod]
    [DataRow("win-x64", "x64")]
    [DataRow("win-arm64", "arm64")]
    [DataRow("win-x86", "x86")]
    [DataRow("x64", "x64")]
    [DataRow("arm64", "arm64")]
    [DataRow("x86", "x86")]
    public void TryResolveArchitecture_RuntimeAlone_ResolvesArchFromRid(string runtime, string expected)
    {
        // M6: with only --runtime (no --arch), the architecture is derived from the RID. A bare arch
        // (no win- prefix, e.g. `--runtime x64`) is also accepted and used directly.
        var ok = RunCommand.Handler.TryResolveArchitecture(null, runtime, out var arch, out var error);

        Assert.IsTrue(ok);
        Assert.IsNull(error);
        Assert.AreEqual(expected, arch);
    }

    [TestMethod]
    public void TryResolveArchitecture_RuntimeArchBeatsArch()
    {
        // --runtime is more specific (a RID) so its architecture wins over --arch.
        var ok = RunCommand.Handler.TryResolveArchitecture("x64", "win-arm64", out var arch, out var error);

        Assert.IsTrue(ok);
        Assert.IsNull(error);
        Assert.AreEqual("arm64", arch);
    }

    [TestMethod]
    public void TryResolveArchitecture_InvalidArch_ReturnsError()
    {
        var ok = RunCommand.Handler.TryResolveArchitecture("sparc", null, out _, out var error);

        Assert.IsFalse(ok);
        Assert.IsNotNull(error);
        StringAssert.Contains(error, "sparc");
    }

    [TestMethod]
    public void TryResolveArchitecture_InvalidRuntime_ReturnsError()
    {
        var ok = RunCommand.Handler.TryResolveArchitecture(null, "win-loongarch64", out _, out var error);

        Assert.IsFalse(ok);
        Assert.IsNotNull(error);
        StringAssert.Contains(error, "win-loongarch64");
    }

    [TestMethod]
    [DataRow("linux-x64")]
    [DataRow("osx-arm64")]
    public void TryResolveArchitecture_NonWindowsRuntime_ReturnsError(string runtime)
    {
        // A non-Windows RID must not be silently reduced to win-<arch>; the user asked for a runtime
        // target project mode can't produce, so surface an error instead of building something else.
        var ok = RunCommand.Handler.TryResolveArchitecture(null, runtime, out _, out var error);

        Assert.IsFalse(ok);
        Assert.IsNotNull(error);
        StringAssert.Contains(error, runtime);
    }

    #endregion

    #region CombineLaunchArguments (non-apphost RunCommand arg ordering)

    [TestMethod]
    public void CombineLaunchArguments_PrependsRunArgumentsBeforeAppArgs()
    {
        var combined = RunCommand.Handler.CombineLaunchArguments("exec \"App.dll\"", "--flag value");

        Assert.AreEqual("exec \"App.dll\" --flag value", combined);
    }

    [TestMethod]
    [DataRow(null, "--flag", "--flag")]
    [DataRow("exec App.dll", null, "exec App.dll")]
    [DataRow("exec App.dll", "", "exec App.dll")]
    [DataRow(null, null, null)]
    public void CombineLaunchArguments_HandlesMissingSides(string? runArguments, string? appArgs, string? expected)
    {
        Assert.AreEqual(expected, RunCommand.Handler.CombineLaunchArguments(runArguments, appArgs));
    }

    #endregion

    #region F2 cross-arch launch diagnostics

    // The reviewer's F2 repro (arm64 target on an x64 host) can't be reproduced on an arm64 machine,
    // because arm64 Windows executes arm64 AND x64 AND x86. These assert the decision table in a
    // host-agnostic way so the behavior is pinned on whichever machine runs the suite.

    [TestMethod]
    public void CanCurrentOsRunArchitecture_HostOwnArchitecture_IsAlwaysRunnable()
    {
        var hostArch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

        Assert.IsTrue(RunCommand.Handler.CanCurrentOsRunArchitecture(hostArch),
            $"the host's own architecture ('{hostArch}') must always be considered runnable");
    }

    [TestMethod]
    public void CanCurrentOsRunArchitecture_Arm64_RunnableOnlyOnArm64Host()
    {
        var expected = RuntimeInformation.OSArchitecture == Architecture.Arm64;

        Assert.AreEqual(expected, RunCommand.Handler.CanCurrentOsRunArchitecture("arm64"),
            "arm64 binaries run only on an arm64 host — this is the case that triggers the F2 hint on x64");
    }

    [TestMethod]
    public void CanCurrentOsRunArchitecture_X86_RunnableOnEveryWindowsHostArch()
    {
        // x86 is emulated on x64 and arm64, and native on x86.
        Assert.IsTrue(RunCommand.Handler.CanCurrentOsRunArchitecture("x86"));
    }

    [TestMethod]
    public void CanCurrentOsRunArchitecture_UnknownMoniker_TreatedAsRunnable()
    {
        // Never mask a genuine launch failure behind a bogus "wrong architecture" message.
        Assert.IsTrue(RunCommand.Handler.CanCurrentOsRunArchitecture("sparc"));
    }

    [TestMethod]
    public async Task RunCommand_FolderMode_AtDebugVerbosity_PrintsDiscoveryBreadcrumb()
    {
        // This class runs at LogLevel.Debug — the level `--verbose` selects — so the breadcrumb
        // must still be emitted here. It is how a user who pointed at a source directory expecting
        // a build finds out why nothing was built. The companion class
        // RunCommandFolderModeBreadcrumbTests asserts it is hidden at default verbosity.
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName]);

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(
            TestAnsiConsole.Output,
            "No .csproj/.sln/.slnx with a runnable app found",
            "Debug/verbose output should explain why a directory fell back to build-output folder mode.");
    }

    [TestMethod]
    public void CanCurrentOsRunArchitecture_IsCaseInsensitive()
    {
        Assert.AreEqual(
            RunCommand.Handler.CanCurrentOsRunArchitecture("arm64"),
            RunCommand.Handler.CanCurrentOsRunArchitecture("ARM64"));
    }

    #endregion
}

/// <summary>
/// Covers the folder-mode discovery breadcrumb at the CLI's DEFAULT verbosity.
/// <see cref="RunCommandTests"/> runs at <see cref="LogLevel.Debug"/> (the harness default), which
/// is the level <c>--verbose</c> selects, so it cannot observe what a normal run prints. This class
/// pins the level to <see cref="LogLevel.Information"/> — what <c>winapp run</c> uses with no
/// verbosity flags — to assert the breadcrumb stays hidden.
/// </summary>
[TestClass]
public class RunCommandFolderModeBreadcrumbTests() : BaseCommandTests(logLevel: LogLevel.Information)
{
    protected override IServiceCollection ConfigureServices(IServiceCollection services)
        => services
            .AddSingleton<IMsixService>(new FakeMsixService())
            .AddSingleton<IAppLauncherService>(new FakeAppLauncherService())
            .AddSingleton<IDebugOutputService>(new FakeDebugOutputService())
            .AddSingleton<IPackageRegistrationService>(new FakePackageRegistrationService())
            .AddSingleton<INugetService, FakeNugetService>();

    [TestMethod]
    public async Task RunCommand_FolderMode_AtDefaultVerbosity_OmitsDiscoveryBreadcrumb()
    {
        // Running a build-output folder is the normal path — it is what every `dotnet run` through
        // the NuGet package does, since the targets point winapp at $(OutputPath). Announcing it at
        // Information made a routine, successful run look like something had gone wrong.
        await File.WriteAllTextAsync(
            Path.Join(_tempDirectory.FullName, "appxmanifest.xml"),
            RunCommandTests.TestManifestContent,
            TestContext.CancellationToken);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName]);

        Assert.AreEqual(0, exitCode, "Folder mode should succeed");
        Assert.IsFalse(
            TestAnsiConsole.Output.Contains("No .csproj/.sln/.slnx with a runnable app found", StringComparison.Ordinal),
            "The folder-mode breadcrumb is a troubleshooting aid and must not appear at default verbosity");
    }
}
