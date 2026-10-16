// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class ManifestAddAliasCommandTests : BaseCommandTests
{
    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        return services
            .AddSingleton<IDevModeService, FakeDevModeService>();
    }

    #region Command registration tests

    [TestMethod]
    public void ManifestCommandShouldHaveAddAliasSubcommand()
    {
        // Arrange & Act
        var manifestCommand = GetRequiredService<ManifestCommand>();

        // Assert
        Assert.IsTrue(manifestCommand.Subcommands.Any(c => c.Name == "add-alias"), "Should have 'add-alias' subcommand");
    }

    [TestMethod]
    public void AddAliasCommandShouldHaveExpectedOptions()
    {
        // Arrange & Act
        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Assert
        Assert.IsNotNull(command, "ManifestAddAliasCommand should be created");
        Assert.AreEqual("add-alias", command.Name);
        Assert.IsTrue(command.Options.Any(o => o.Name == "--name"), "Should have --name option");
        Assert.IsTrue(command.Options.Any(o => o.Name == "--manifest"), "Should have --manifest option");
        Assert.IsTrue(command.Options.Any(o => o.Name == "--app-id"), "Should have --app-id option");
    }

    #endregion

    #region Fresh alias addition tests

    [TestMethod]
    public async Task AddAlias_SiblingClaimReportsConflictAndPreservesSource()
    {
        var manifestPath = CreateManifest("""
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3"
                     xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="First" Executable="first.exe" EntryPoint="Windows.FullTrustApplication">
                  <Extensions>
                    <uap3:Extension Category="windows.appExecutionAlias">
                      <uap3:AppExecutionAlias><desktop:ExecutionAlias Alias="shared.exe" /></uap3:AppExecutionAlias>
                    </uap3:Extension>
                  </Extensions>
                </Application>
                <Application Id="Second" Executable="second.exe" EntryPoint="Windows.FullTrustApplication" />
              </Applications>
            </Package>
            """);
        var before = await File.ReadAllBytesAsync(manifestPath, TestContext.CancellationToken);
        var exitCode = await ParseAndInvokeWithCaptureAsync(GetRequiredService<ManifestAddAliasCommand>(),
            ["--manifest", manifestPath, "--app-id", "Second", "--name", "SHARED.exe"]);
        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "another application");
        StringAssert.Contains(ConsoleStdErr.ToString(), "Choose a different alias name");
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(manifestPath, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task AddAlias_FreshManifestNoExtensions_AddsAliasSuccessfully()
    {
        // Arrange - manifest with no Extensions block
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
                     IgnorableNamespaces="uap10">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="testApp" Executable="myapp.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        var content = await File.ReadAllTextAsync(manifestPath);
        Assert.Contains("uap5:ExecutionAlias", content, "Should contain ExecutionAlias element");
        Assert.Contains("Alias=\"myapp.exe\"", content, "Alias should match the Executable attribute");
        Assert.Contains("uap5:Extension", content, "Should contain Extension element");
        Assert.Contains("uap5:AppExecutionAlias", content, "Should contain AppExecutionAlias element");
        Assert.Contains("windows.appExecutionAlias", content, "Should contain correct category");
        Assert.Contains("xmlns:uap5=", content, "Should add uap5 namespace declaration");
    }

    [TestMethod]
    public async Task AddAlias_WithCustomName_UsesCustomName()
    {
        // Arrange
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     IgnorableNamespaces="">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="testApp" Executable="myapp.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath, "--name", "custom-alias.exe"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        var content = await File.ReadAllTextAsync(manifestPath);
        Assert.Contains("Alias=\"custom-alias.exe\"", content, "Alias should use the custom name");
    }

    [TestMethod]
    public async Task AddAlias_NameWithoutExeExtension_AppendsExe()
    {
        // Arrange
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     IgnorableNamespaces="">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="testApp" Executable="myapp.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath, "--name", "myapp"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        var content = await File.ReadAllTextAsync(manifestPath);
        Assert.Contains("Alias=\"myapp.exe\"", content, "Alias should have .exe appended");
    }

    [TestMethod]
    public async Task AddAlias_WithTargetNameToken_InfersTokenAsAlias()
    {
        // Arrange
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     IgnorableNamespaces="">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="testApp" Executable="$targetnametoken$.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        var content = await File.ReadAllTextAsync(manifestPath);
        Assert.Contains("Alias=\"$targetnametoken$.exe\"", content, "Alias should preserve $targetnametoken$ placeholder");
    }

    #endregion

    #region Idempotent / error case tests

    [TestMethod]
    public async Task AddAlias_SameAliasAlreadyExists_ReturnsSuccessWithWarning()
    {
        // Arrange - manifest with existing alias
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"
                     IgnorableNamespaces="uap5">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="testApp" Executable="myapp.exe" EntryPoint="Windows.FullTrustApplication">
                  <Extensions>
                    <uap5:Extension Category="windows.appExecutionAlias">
                      <uap5:AppExecutionAlias>
                        <uap5:ExecutionAlias Alias="myapp.exe" />
                      </uap5:AppExecutionAlias>
                    </uap5:Extension>
                  </Extensions>
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath]);

        // Assert - idempotent: same alias returns 0
        Assert.AreEqual(0, exitCode, "Command should succeed when same alias already exists");
    }

    [TestMethod]
    public async Task AddAlias_DifferentAliasAlreadyExists_ReturnsError()
    {
        // Arrange - manifest with a different existing alias
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"
                     IgnorableNamespaces="uap5">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="testApp" Executable="myapp.exe" EntryPoint="Windows.FullTrustApplication">
                  <Extensions>
                    <uap5:Extension Category="windows.appExecutionAlias">
                      <uap5:AppExecutionAlias>
                        <uap5:ExecutionAlias Alias="existing-alias.exe" />
                      </uap5:AppExecutionAlias>
                    </uap5:Extension>
                  </Extensions>
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath, "--name", "different.exe"]);

        // Assert - should error when a different alias exists
        Assert.AreEqual(1, exitCode, "Command should fail when a different alias already exists");
    }

    #endregion

    #region App ID selection tests

    [TestMethod]
    public async Task AddAlias_WithAppId_TargetsCorrectApplication()
    {
        // Arrange - manifest with multiple Application elements
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     IgnorableNamespaces="">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="App1" Executable="app1.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
                <Application Id="App2" Executable="app2.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath, "--app-id", "App2"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        var content = await File.ReadAllTextAsync(manifestPath);
        Assert.Contains("Alias=\"app2.exe\"", content, "Alias should be inferred from App2's Executable");
    }

    [TestMethod]
    public async Task AddAlias_WithInvalidAppId_ReturnsError()
    {
        // Arrange
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     IgnorableNamespaces="">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="testApp" Executable="myapp.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath, "--app-id", "NonExistent"]);

        // Assert
        Assert.AreEqual(1, exitCode, "Command should fail for non-existent app ID");
    }

    [TestMethod]
    public async Task AddAlias_MultipleApps_NoAppId_TargetsFirstApplication()
    {
        // Arrange - manifest with multiple Application elements, no --app-id
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     IgnorableNamespaces="">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="First" Executable="first.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
                <Application Id="Second" Executable="second.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act - no --app-id specified, should default to first Application
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        var content = await File.ReadAllTextAsync(manifestPath);
        Assert.Contains("Alias=\"first.exe\"", content, "Alias should be inferred from the first Application's Executable");

        // The alias should be inside the first Application, not the second
        var doc = System.Xml.Linq.XDocument.Parse(content);
        var ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        var firstApp = doc.Descendants(System.Xml.Linq.XName.Get("Application", ns)).First();
        Assert.AreEqual("First", firstApp.Attribute("Id")?.Value);
        Assert.IsNotNull(firstApp.Element(System.Xml.Linq.XName.Get("Extensions", ns)),
            "Extensions should be added to the first Application");

        var secondApp = doc.Descendants(System.Xml.Linq.XName.Get("Application", ns)).Last();
        Assert.IsNull(secondApp.Element(System.Xml.Linq.XName.Get("Extensions", ns)),
            "Second Application should not have Extensions");
    }

    [TestMethod]
    public async Task AddAlias_MultipleApps_WithAppId_OnlyModifiesTargetApp()
    {
        // Arrange - manifest with multiple apps, target the second one
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     IgnorableNamespaces="">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="App1" Executable="app1.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
                <Application Id="App2" Executable="app2.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
                <Application Id="App3" Executable="app3.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act - target App2
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath, "--app-id", "App2"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        var doc = System.Xml.Linq.XDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        var ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        var apps = doc.Descendants(System.Xml.Linq.XName.Get("Application", ns)).ToList();

        // Only App2 should have Extensions
        Assert.IsNull(apps[0].Element(System.Xml.Linq.XName.Get("Extensions", ns)),
            "App1 should not have Extensions");
        Assert.IsNotNull(apps[1].Element(System.Xml.Linq.XName.Get("Extensions", ns)),
            "App2 should have Extensions with the alias");
        Assert.IsNull(apps[2].Element(System.Xml.Linq.XName.Get("Extensions", ns)),
            "App3 should not have Extensions");
    }

    [TestMethod]
    public async Task AddAlias_MultipleApps_ExistingAliasOnOtherApp_DoesNotConflict()
    {
        // Arrange - App1 already has an alias, adding alias to App2 should work
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"
                     IgnorableNamespaces="uap5">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="App1" Executable="app1.exe" EntryPoint="Windows.FullTrustApplication">
                  <Extensions>
                    <uap5:Extension Category="windows.appExecutionAlias">
                      <uap5:AppExecutionAlias>
                        <uap5:ExecutionAlias Alias="app1.exe" />
                      </uap5:AppExecutionAlias>
                    </uap5:Extension>
                  </Extensions>
                </Application>
                <Application Id="App2" Executable="app2.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act - add alias to App2 (App1 already has one, but that shouldn't matter)
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath, "--app-id", "App2"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed — alias on other app should not conflict");
        var content = await File.ReadAllTextAsync(manifestPath);
        Assert.Contains("Alias=\"app1.exe\"", content, "App1's existing alias should remain");
        Assert.Contains("Alias=\"app2.exe\"", content, "App2's new alias should be added");
    }

    [TestMethod]
    public async Task AddAlias_MultipleApps_CustomNameOnSpecificApp()
    {
        // Arrange
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     IgnorableNamespaces="">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="MainApp" Executable="myapp.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
                <Application Id="HelperApp" Executable="helper.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act - custom alias name on the second app
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath, "--app-id", "HelperApp", "--name", "my-helper"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        var content = await File.ReadAllTextAsync(manifestPath);
        Assert.Contains("Alias=\"my-helper.exe\"", content, "Custom alias should be applied to HelperApp");
        // Ensure MainApp wasn't touched
        Assert.IsFalse(content.Contains("Alias=\"myapp.exe\""),
            "MainApp should not have received an alias");
    }

    #endregion

    #region Error handling tests

    [TestMethod]
    public async Task AddAlias_NoManifestFound_ReturnsError()
    {
        // Arrange - no manifest in the temp directory
        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act - no --manifest and no manifest in cwd
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        // Assert
        Assert.AreEqual(1, exitCode, "Command should fail when no manifest is found");
    }

    [TestMethod]
    public async Task AddAlias_ManifestNoApplicationElement_ReturnsError()
    {
        // Arrange - minimal manifest without Applications
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     IgnorableNamespaces="">
              <Identity Name="test" Publisher="CN=test" Version="1.0.0.0" />
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath]);

        // Assert
        Assert.AreEqual(1, exitCode, "Command should fail when no Application element exists");
    }

    [TestMethod]
    public async Task AddAlias_NoExecutableAndNoName_ReturnsError()
    {
        // Arrange - Application without Executable attribute
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     IgnorableNamespaces="">
              <Identity Name="test" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="testApp" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath]);

        // Assert
        Assert.AreEqual(1, exitCode, "Command should fail when Executable attr missing and --name not specified");
    }

    #endregion

    #region Namespace handling tests

    [TestMethod]
    public async Task AddAlias_Uap5NamespaceAlreadyDeclared_DoesNotDuplicate()
    {
        // Arrange - uap5 already declared
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"
                     IgnorableNamespaces="uap5">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="testApp" Executable="myapp.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        var content = await File.ReadAllTextAsync(manifestPath);
        // Count occurrences - should only have one uap5 namespace declaration
        var count = content.Split("xmlns:uap5=").Length - 1;
        Assert.AreEqual(1, count, "Should have exactly one uap5 namespace declaration");
    }

    [TestMethod]
    public async Task AddAlias_AddsUap5ToIgnorableNamespaces()
    {
        // Arrange
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
                     IgnorableNamespaces="uap10">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="testApp" Executable="myapp.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        var content = await File.ReadAllTextAsync(manifestPath);
        Assert.Contains("uap5", content, "IgnorableNamespaces should include uap5");
    }

    #endregion

    #region XML formatting tests

    [TestMethod]
    public async Task AddAlias_OutputXmlIsWellFormed()
    {
        // Arrange
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
                     IgnorableNamespaces="uap10">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="testApp" Executable="myapp.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");

        // Verify the output is well-formed XML by parsing it again
        var content = await File.ReadAllTextAsync(manifestPath);
        var doc = System.Xml.Linq.XDocument.Parse(content);
        Assert.IsNotNull(doc.Root, "Output should be valid XML");
    }

    [TestMethod]
    public async Task AddAlias_ElementsWithManyAttributes_AreFormattedOnSeparateLines()
    {
        // Arrange - manifest with elements that have 3+ attributes
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                     xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
                     IgnorableNamespaces="uap uap10">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="testApp" Executable="myapp.exe" EntryPoint="Windows.FullTrustApplication"
                             uap10:TrustLevel="mediumIL" uap10:RuntimeBehavior="packagedClassicApp">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        var lines = (await File.ReadAllTextAsync(manifestPath)).Split('\n');

        // Find the <Application line (should have 5+ attrs and be split across lines)
        var applicationLineIdx = Array.FindIndex(lines, l =>
        {
            var trimmed = l.TrimStart();
            return trimmed.StartsWith("<Application", StringComparison.Ordinal) && !trimmed.StartsWith("<Applications", StringComparison.Ordinal);
        });
        Assert.IsTrue(applicationLineIdx >= 0, "Should find <Application element");

        // With 5 attributes, the element should span multiple lines.
        // Next line should be an attribute (indented continuation), not a closing tag or child element
        var nextLine = lines[applicationLineIdx + 1].Trim();
        Assert.IsTrue(
            nextLine.StartsWith("Id=", StringComparison.Ordinal) ||
            nextLine.StartsWith("Executable=", StringComparison.Ordinal) ||
            nextLine.StartsWith("EntryPoint=", StringComparison.Ordinal) ||
            nextLine.StartsWith("uap10:", StringComparison.Ordinal),
            $"Next line after <Application should be an attribute on its own line, got: '{nextLine}'");
    }

    #endregion

    #region Alias name validation tests

    [TestMethod]
    [DataRow("..\\evil.exe", DisplayName = "parent traversal")]
    [DataRow("dir\\evil.exe", DisplayName = "path separator")]
    [DataRow("C:\\Windows\\System32\\calc.exe", DisplayName = "absolute path")]
    [DataRow("evil:test.exe", DisplayName = "colon / ADS")]
    [DataRow("evil*.exe", DisplayName = "invalid char (asterisk)")]
    [DataRow("CON.exe", DisplayName = "reserved DOS device name")]
    [DataRow("NUL.exe", DisplayName = "reserved DOS device name (NUL)")]
    [DataRow("COM1.exe", DisplayName = "reserved DOS device name (COM1)")]
    public async Task AddAlias_UnsafeName_RejectsAndDoesNotModifyManifest(string unsafeAlias)
    {
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     IgnorableNamespaces="">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="testApp" Executable="myapp.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);
        var originalContent = await File.ReadAllTextAsync(manifestPath);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath, "--name", unsafeAlias]);

        Assert.AreEqual(1, exitCode, $"Command should fail for unsafe alias '{unsafeAlias}'");
        var currentContent = await File.ReadAllTextAsync(manifestPath);
        Assert.AreEqual(originalContent, currentContent, "Manifest must not be modified when alias is rejected");
    }

    [TestMethod]
    public async Task AddAlias_UnsafeExecutableInferred_RejectsAndDoesNotModifyManifest()
    {
        // Executable attribute itself is the attack vector here — it gets used
        // as the alias name when --name is not supplied.
        var manifestPath = CreateManifest("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     IgnorableNamespaces="">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="testApp" Executable="..\evil.exe" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);
        var originalContent = await File.ReadAllTextAsync(manifestPath);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath]);

        Assert.AreEqual(1, exitCode, "Command should fail when inferred alias is unsafe");
        var currentContent = await File.ReadAllTextAsync(manifestPath);
        Assert.AreEqual(originalContent, currentContent, "Manifest must not be modified when inferred alias is rejected");
    }

    [TestMethod]
    [DataRow("app\\my-app.exe", "my-app.exe", DisplayName = "backslash subdir (Electron pattern)")]
    [DataRow("app/my-app.exe", "my-app.exe", DisplayName = "forward slash subdir")]
    [DataRow("bin\\sub\\nested.exe", "nested.exe", DisplayName = "multi-segment subdir")]
    public async Task AddAlias_PathPrefixedExecutable_InfersLeafFilenameAsAlias(string executable, string expectedAlias)
    {
        // The MSIX manifest's Application/@Executable is a package-relative path
        // (e.g. "app\my-app.exe" — Electron guide). The inferred alias must be
        // the leaf filename, not the full path.
        var manifestPath = CreateManifest($"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     IgnorableNamespaces="">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="testApp" Executable="{executable}" EntryPoint="Windows.FullTrustApplication">
                </Application>
              </Applications>
            </Package>
            """);

        var command = GetRequiredService<ManifestAddAliasCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifestPath]);

        Assert.AreEqual(0, exitCode, $"Command should succeed for Executable='{executable}'");
        var content = await File.ReadAllTextAsync(manifestPath);
        Assert.Contains($"Alias=\"{expectedAlias}\"", content, "Alias should be the leaf filename of Executable");
    }

    #endregion

    #region Helper methods

    private string CreateManifest(string content)
    {
        var path = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        File.WriteAllText(path, content);
        return path;
    }

    #endregion
}
