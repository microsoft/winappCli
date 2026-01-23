// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class ManifestValidateCommandTests : BaseCommandTests
{
    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        return services
            .AddSingleton<IDevModeService, FakeDevModeService>();
    }

    [TestMethod]
    public void ManifestCommandShouldHaveValidateSubcommand()
    {
        // Arrange & Act
        var manifestCommand = GetRequiredService<ManifestCommand>();

        // Assert
        Assert.IsNotNull(manifestCommand, "ManifestCommand should be created");
        Assert.AreEqual("manifest", manifestCommand.Name, "Command name should be 'manifest'");
        Assert.IsTrue(manifestCommand.Subcommands.Any(c => c.Name == "validate"), "Should have 'validate' subcommand");
    }

    [TestMethod]
    public async Task ManifestValidateCommand_WithValidManifest_ShouldSucceed()
    {
        // Arrange
        var validateCommand = GetRequiredService<ManifestValidateCommand>();
        
        // Create a valid manifest
        var manifestPath = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        var validManifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <Package 
              xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
              xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
              xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
              xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
              IgnorableNamespaces="uap rescap uap10">

              <Identity
                Name="TestApp"
                Publisher="CN=TestPublisher"
                Version="1.0.0.0" />

              <Properties>
                <DisplayName>Test App</DisplayName>
                <PublisherDisplayName>Test Publisher</PublisherDisplayName>
                <Logo>Assets\StoreLogo.png</Logo>
              </Properties>

              <Dependencies>
                <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.18362.0" MaxVersionTested="10.0.26100.0" />
              </Dependencies>

              <Resources>
                <Resource Language="en-us"/>
              </Resources>

              <Applications>
                <Application Id="App"
                  Executable="TestApp.exe"
                  EntryPoint="Windows.FullTrustApplication"
                  uap10:TrustLevel="mediumIL" 
                  uap10:RuntimeBehavior="packagedClassicApp">
                  <uap:VisualElements
                    DisplayName="Test App"
                    Description="Test Application"
                    BackgroundColor="transparent"
                    Square150x150Logo="Assets\Square150x150Logo.png"
                    Square44x44Logo="Assets\Square44x44Logo.png">
                  </uap:VisualElements>
                </Application>
              </Applications>

              <Capabilities>
                <rescap:Capability Name="runFullTrust" />
              </Capabilities>
            </Package>
            """;
        await File.WriteAllTextAsync(manifestPath, validManifest, TestContext.CancellationToken);

        var args = new[] { manifestPath };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(validateCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Validate command should succeed for valid manifest");
        // Output may go to TestAnsiConsole or ConsoleStdOut, check both
        var combinedOutput = ConsoleStdOut.ToString() + TestAnsiConsole.Output;
        StringAssert.Contains(combinedOutput, "valid", "Output should indicate manifest is valid");
    }

    [TestMethod]
    public async Task ManifestValidateCommand_WithMalformedXml_ShouldFail()
    {
        // Arrange
        var validateCommand = GetRequiredService<ManifestValidateCommand>();
        
        // Create a malformed manifest (unclosed tag)
        var manifestPath = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        var malformedManifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="TestApp" Publisher="CN=TestPublisher" Version="1.0.0.0" />
              <Properties>
                <DisplayName>Test App</DisplayName>
            </Package>
            """;
        await File.WriteAllTextAsync(manifestPath, malformedManifest, TestContext.CancellationToken);

        var args = new[] { manifestPath };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(validateCommand, args);

        // Assert
        Assert.AreEqual(1, exitCode, "Validate command should fail for malformed XML");
    }

    [TestMethod]
    public async Task ManifestValidateCommand_WithMissingRequiredElements_ShouldFail()
    {
        // Arrange
        var validateCommand = GetRequiredService<ManifestValidateCommand>();
        
        // Create a manifest missing required elements (no Properties, Dependencies, Resources, Applications)
        var manifestPath = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        var invalidManifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="TestApp" Publisher="CN=TestPublisher" Version="1.0.0.0" />
            </Package>
            """;
        await File.WriteAllTextAsync(manifestPath, invalidManifest, TestContext.CancellationToken);

        var args = new[] { manifestPath };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(validateCommand, args);

        // Assert
        Assert.AreEqual(1, exitCode, "Validate command should fail for manifest missing required elements");
    }

    [TestMethod]
    public async Task ManifestValidateCommand_WithInvalidVersionFormat_ShouldFail()
    {
        // Arrange
        var validateCommand = GetRequiredService<ManifestValidateCommand>();
        
        // Create a manifest with invalid version format
        var manifestPath = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        var invalidManifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <Package 
              xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
              xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
              xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
              xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
              IgnorableNamespaces="uap rescap uap10">

              <Identity
                Name="TestApp"
                Publisher="CN=TestPublisher"
                Version="invalid-version" />

              <Properties>
                <DisplayName>Test App</DisplayName>
                <PublisherDisplayName>Test Publisher</PublisherDisplayName>
                <Logo>Assets\StoreLogo.png</Logo>
              </Properties>

              <Dependencies>
                <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.18362.0" MaxVersionTested="10.0.26100.0" />
              </Dependencies>

              <Resources>
                <Resource Language="en-us"/>
              </Resources>

              <Applications>
                <Application Id="App"
                  Executable="TestApp.exe"
                  EntryPoint="Windows.FullTrustApplication"
                  uap10:TrustLevel="mediumIL" 
                  uap10:RuntimeBehavior="packagedClassicApp">
                  <uap:VisualElements
                    DisplayName="Test App"
                    Description="Test Application"
                    BackgroundColor="transparent"
                    Square150x150Logo="Assets\Square150x150Logo.png"
                    Square44x44Logo="Assets\Square44x44Logo.png">
                  </uap:VisualElements>
                </Application>
              </Applications>

              <Capabilities>
                <rescap:Capability Name="runFullTrust" />
              </Capabilities>
            </Package>
            """;
        await File.WriteAllTextAsync(manifestPath, invalidManifest, TestContext.CancellationToken);

        var args = new[] { manifestPath };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(validateCommand, args);

        // Assert
        Assert.AreEqual(1, exitCode, "Validate command should fail for manifest with invalid version format");
        var output = ConsoleStdErr.ToString();
        // MakeAppx reports pattern constraint violations for invalid versions
        StringAssert.Contains(output, "invalid-version", "Error message should show the invalid version value");
    }

    [TestMethod]
    public async Task ManifestValidateCommand_WithInvalidPublisherFormat_ShouldFail()
    {
        // Arrange
        var validateCommand = GetRequiredService<ManifestValidateCommand>();
        
        // Create a manifest with invalid publisher format (missing CN=)
        var manifestPath = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        var invalidManifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <Package 
              xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
              xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
              xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
              xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
              IgnorableNamespaces="uap rescap uap10">

              <Identity
                Name="TestApp"
                Publisher="InvalidPublisher"
                Version="1.0.0.0" />

              <Properties>
                <DisplayName>Test App</DisplayName>
                <PublisherDisplayName>Test Publisher</PublisherDisplayName>
                <Logo>Assets\StoreLogo.png</Logo>
              </Properties>

              <Dependencies>
                <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.18362.0" MaxVersionTested="10.0.26100.0" />
              </Dependencies>

              <Resources>
                <Resource Language="en-us"/>
              </Resources>

              <Applications>
                <Application Id="App"
                  Executable="TestApp.exe"
                  EntryPoint="Windows.FullTrustApplication"
                  uap10:TrustLevel="mediumIL" 
                  uap10:RuntimeBehavior="packagedClassicApp">
                  <uap:VisualElements
                    DisplayName="Test App"
                    Description="Test Application"
                    BackgroundColor="transparent"
                    Square150x150Logo="Assets\Square150x150Logo.png"
                    Square44x44Logo="Assets\Square44x44Logo.png">
                  </uap:VisualElements>
                </Application>
              </Applications>

              <Capabilities>
                <rescap:Capability Name="runFullTrust" />
              </Capabilities>
            </Package>
            """;
        await File.WriteAllTextAsync(manifestPath, invalidManifest, TestContext.CancellationToken);

        var args = new[] { manifestPath };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(validateCommand, args);

        // Assert
        Assert.AreEqual(1, exitCode, "Validate command should fail for manifest with invalid publisher format");
        var output = ConsoleStdErr.ToString();
        // MakeAppx reports pattern constraint violations for invalid publisher
        StringAssert.Contains(output, "InvalidPublisher", "Error message should show the invalid publisher value");
    }

    [TestMethod]
    public async Task ManifestValidateCommand_WithInvalidApplicationId_ShouldShowFriendlyError()
    {
        // Arrange
        var validateCommand = GetRequiredService<ManifestValidateCommand>();
        
        // Create a manifest with invalid Application Id (contains hyphen)
        var manifestPath = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        var invalidManifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <Package 
              xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
              xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
              xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
              xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
              IgnorableNamespaces="uap rescap uap10">

              <Identity
                Name="TestApp"
                Publisher="CN=TestPublisher"
                Version="1.0.0.0" />

              <Properties>
                <DisplayName>Test App</DisplayName>
                <PublisherDisplayName>Test Publisher</PublisherDisplayName>
                <Logo>Assets\StoreLogo.png</Logo>
              </Properties>

              <Dependencies>
                <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.18362.0" MaxVersionTested="10.0.26100.0" />
              </Dependencies>

              <Resources>
                <Resource Language="en-us"/>
              </Resources>

              <Applications>
                <Application Id="My-App"
                  Executable="TestApp.exe"
                  EntryPoint="Windows.FullTrustApplication"
                  uap10:TrustLevel="mediumIL" 
                  uap10:RuntimeBehavior="packagedClassicApp">
                  <uap:VisualElements
                    DisplayName="Test App"
                    Description="Test Application"
                    BackgroundColor="transparent"
                    Square150x150Logo="Assets\Square150x150Logo.png"
                    Square44x44Logo="Assets\Square44x44Logo.png">
                  </uap:VisualElements>
                </Application>
              </Applications>

              <Capabilities>
                <rescap:Capability Name="runFullTrust" />
              </Capabilities>
            </Package>
            """;
        await File.WriteAllTextAsync(manifestPath, invalidManifest, TestContext.CancellationToken);

        var args = new[] { manifestPath };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(validateCommand, args);

        // Assert
        Assert.AreEqual(1, exitCode, "Validate command should fail for manifest with invalid Application Id");
        var output = ConsoleStdErr.ToString();
        // Should show friendly error with suggestion (merged from structural validation)
        StringAssert.Contains(output, "Application Id", "Error message should mention Application Id");
        StringAssert.Contains(output, "Suggestion", "Error should include a suggestion for how to fix");
    }

    [TestMethod]
    public async Task ManifestValidateCommand_WithNonexistentFile_ShouldFailToParse()
    {
        // Arrange
        var validateCommand = GetRequiredService<ManifestValidateCommand>();
        var args = new[] { Path.Combine(_tempDirectory.FullName, "nonexistent.xml") };

        // Act
        var parseResult = validateCommand.Parse(args);
        var hasErrors = parseResult.Errors.Any();

        // Assert
        Assert.IsTrue(hasErrors, "Parse should fail for nonexistent file");
    }

    [TestMethod]
    public async Task ManifestValidateCommand_WithDesktopNamespaces_ShouldValidate()
    {
        // Arrange
        var validateCommand = GetRequiredService<ManifestValidateCommand>();
        
        // Create a manifest with desktop namespaces
        var manifestPath = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        var validManifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <Package 
              xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
              xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
              xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
              xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
              xmlns:desktop6="http://schemas.microsoft.com/appx/manifest/desktop/windows10/6"
              xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
              IgnorableNamespaces="uap rescap desktop desktop6 uap10">

              <Identity
                Name="DesktopApp"
                Publisher="CN=TestPublisher"
                Version="1.0.0.0" />

              <Properties>
                <DisplayName>Desktop App</DisplayName>
                <PublisherDisplayName>Test Publisher</PublisherDisplayName>
                <Logo>Assets\StoreLogo.png</Logo>
              </Properties>

              <Dependencies>
                <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.18362.0" MaxVersionTested="10.0.26100.0" />
              </Dependencies>

              <Resources>
                <Resource Language="en-us"/>
              </Resources>

              <Applications>
                <Application Id="App"
                  Executable="DesktopApp.exe"
                  EntryPoint="Windows.FullTrustApplication"
                  uap10:TrustLevel="mediumIL" 
                  uap10:RuntimeBehavior="packagedClassicApp">
                  <uap:VisualElements
                    DisplayName="Desktop App"
                    Description="Desktop Application"
                    BackgroundColor="transparent"
                    Square150x150Logo="Assets\Square150x150Logo.png"
                    Square44x44Logo="Assets\Square44x44Logo.png">
                  </uap:VisualElements>
                </Application>
              </Applications>

              <Capabilities>
                <rescap:Capability Name="runFullTrust" />
              </Capabilities>
            </Package>
            """;
        await File.WriteAllTextAsync(manifestPath, validManifest, TestContext.CancellationToken);

        var args = new[] { manifestPath };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(validateCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Validate command should succeed for valid manifest with desktop namespaces");
    }

    [TestMethod]
    public async Task ManifestValidateCommand_ShowsLineNumberOnError()
    {
        // Arrange
        var validateCommand = GetRequiredService<ManifestValidateCommand>();
        
        // Create a manifest with an error that has a specific line
        var manifestPath = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        var invalidManifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="TestApp" Publisher="InvalidPublisher" Version="1.0.0.0" />
              <Properties>
                <DisplayName>Test App</DisplayName>
                <PublisherDisplayName>Test Publisher</PublisherDisplayName>
                <Logo>Assets\StoreLogo.png</Logo>
              </Properties>
              <Dependencies>
                <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.18362.0" MaxVersionTested="10.0.26100.0" />
              </Dependencies>
              <Resources>
                <Resource Language="en-us"/>
              </Resources>
            </Package>
            """;
        await File.WriteAllTextAsync(manifestPath, invalidManifest, TestContext.CancellationToken);

        var args = new[] { manifestPath };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(validateCommand, args);

        // Assert
        Assert.AreEqual(1, exitCode, "Validate command should fail for invalid manifest");
        var output = ConsoleStdErr.ToString();
        // Should contain line number information in error output
        StringAssert.Contains(output, "Line", "Error should contain line number information");
    }
}
