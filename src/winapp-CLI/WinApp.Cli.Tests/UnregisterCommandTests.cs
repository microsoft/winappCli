// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class UnregisterCommandTests : BaseCommandTests
{
    private FakePackageRegistrationService _fakePackageRegistrationService = null!;

    private const string TestManifestContent = """
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
        _fakePackageRegistrationService = new FakePackageRegistrationService();
        return services
            .AddSingleton<IPackageRegistrationService>(_fakePackageRegistrationService);
    }

    private async Task<FileInfo> CreateTestManifestAsync(string? directory = null)
    {
        directory ??= _tempDirectory.FullName;
        var manifestPath = Path.Combine(directory, "appxmanifest.xml");
        await File.WriteAllTextAsync(manifestPath, TestManifestContent, TestContext.CancellationToken);
        return new FileInfo(manifestPath);
    }

    [TestMethod]
    public async Task UnregisterCommand_WithManifest_UnregistersDevPackages()
    {
        // Arrange
        var manifest = await CreateTestManifestAsync();
        var command = GetRequiredService<UnregisterCommand>();

        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("TestPackage_1.0.0.0_x64__abc123", "TestPackage", "1.0.0.0",
                _tempDirectory.FullName, IsDevelopmentMode: true)
        ];
        _fakePackageRegistrationService.FakeUnregisterResult = true;

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName]);

        // Assert
        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(_fakePackageRegistrationService.FindDevPackagesCalls.Contains("TestPackage"));
        Assert.IsTrue(_fakePackageRegistrationService.UnregisterCalls.Any(c => c.PackageName == "TestPackage"));
    }

    [TestMethod]
    public async Task UnregisterCommand_ChecksBothNameAndDebugVariant()
    {
        // Arrange
        var manifest = await CreateTestManifestAsync();
        var command = GetRequiredService<UnregisterCommand>();
        _fakePackageRegistrationService.FakeDevPackages = [];

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName]);

        // Assert
        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(_fakePackageRegistrationService.FindDevPackagesCalls.Contains("TestPackage"));
        Assert.IsTrue(_fakePackageRegistrationService.FindDevPackagesCalls.Contains("TestPackage.debug"));
    }

    [TestMethod]
    public async Task UnregisterCommand_SkipsNonDevModePackages()
    {
        // Arrange
        var manifest = await CreateTestManifestAsync();
        var command = GetRequiredService<UnregisterCommand>();

        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("TestPackage_1.0.0.0_x64__abc123", "TestPackage", "1.0.0.0",
                _tempDirectory.FullName, IsDevelopmentMode: false)
        ];

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName]);

        // Assert
        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterCalls.Count);
    }

    [TestMethod]
    public async Task UnregisterCommand_SkipsPackagesFromDifferentTree()
    {
        // Arrange
        var manifest = await CreateTestManifestAsync();
        var command = GetRequiredService<UnregisterCommand>();

        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("TestPackage_1.0.0.0_x64__abc123", "TestPackage", "1.0.0.0",
                @"C:\OtherProject\bin\Debug\AppX", IsDevelopmentMode: true)
        ];

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName]);

        // Assert
        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterCalls.Count);
    }

    [TestMethod]
    public async Task UnregisterCommand_WithForce_SkipsLocationCheck()
    {
        // Arrange
        var manifest = await CreateTestManifestAsync();
        var command = GetRequiredService<UnregisterCommand>();

        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("TestPackage_1.0.0.0_x64__abc123", "TestPackage", "1.0.0.0",
                @"C:\OtherProject\bin\Debug\AppX", IsDevelopmentMode: true)
        ];
        _fakePackageRegistrationService.FakeUnregisterResult = true;

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName, "--force"]);

        // Assert
        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(_fakePackageRegistrationService.UnregisterCalls.Any(c => c.PackageName == "TestPackage"));
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnregisterCommand_WithForceOnTarget_IsRejectedCoherently(bool json)
    {
        var manifest = await CreateTestManifestAsync();
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var arguments = new List<string>
        {
            "unregister",
            "--manifest",
            manifest.FullName,
            "--on",
            "sandbox",
            "--force",
        };
        if (json)
        {
            arguments.Add("--json");
        }

        var originalError = Console.Error;
        using var capturedError = new StringWriter();
        Console.SetError(capturedError);
        int exitCode;
        try
        {
            exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand, [.. arguments]);
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.AreEqual(TargetOutput.InvalidCommandLineExitCode, exitCode);
        var error = capturedError.ToString().Trim();
        StringAssert.Contains(error, "--force");
        if (json)
        {
            var root = System.Text.Json.JsonDocument.Parse(error).RootElement;
            Assert.AreEqual(
                ExecutionTargetErrorCodes.TargetInvalidArguments,
                root.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [TestMethod]
    public async Task UnregisterCommand_WithJson_ReturnsJsonOutput()
    {
        // Arrange
        var manifest = await CreateTestManifestAsync();
        var command = GetRequiredService<UnregisterCommand>();

        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("TestPackage_1.0.0.0_x64__abc123", "TestPackage", "1.0.0.0",
                _tempDirectory.FullName, IsDevelopmentMode: true)
        ];
        _fakePackageRegistrationService.FakeUnregisterResult = true;

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName, "--json"]);

        // Assert
        Assert.AreEqual(0, exitCode);
        var output = TestAnsiConsole.Output;
        Assert.IsTrue(output.Contains("TestPackage_1.0.0.0_x64__abc123"));
    }

    [TestMethod]
    public async Task UnregisterCommand_NoManifest_ReturnsError()
    {
        // Arrange — empty temp directory with no manifest
        var emptyDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "empty"));
        emptyDir.Create();
        var command = GetRequiredService<UnregisterCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        // Assert
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task UnregisterCommand_NoManifest_WithJson_EmitsJsonError()
    {
        // No manifest in the current directory + --json should emit a structured JSON error
        // (rather than a plain log line) and still fail.
        TestAnsiConsole.Profile.Width = 1000; // avoid line-wrapping that would corrupt the JSON
        var command = GetRequiredService<UnregisterCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--json"]);

        Assert.AreEqual(1, exitCode);
        var output = TestAnsiConsole.Output.Trim();
        var root = System.Text.Json.JsonDocument.Parse(output).RootElement;
        Assert.IsTrue(root.TryGetProperty("Error", out var error), "JSON output should carry an Error property");
        StringAssert.Contains(error.GetString(), "No manifest found");
    }
}
