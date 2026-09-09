// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class AppxManifestDocumentTests
{
    private const string MinimalManifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
          <Identity Name="TestApp" Publisher="CN=Test" Version="1.0.0.0" ProcessorArchitecture="x64" />
          <Properties>
            <DisplayName>Test App</DisplayName>
          </Properties>
          <Dependencies>
            <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.22621.0" />
          </Dependencies>
          <Resources>
            <Resource Language="en-US" />
          </Resources>
          <Applications>
            <Application Id="App" Executable="TestApp.exe" EntryPoint="Windows.FullTrustApplication">
              <uap:VisualElements DisplayName="Test App" Square150x150Logo="Assets\Logo.png" />
            </Application>
          </Applications>
        </Package>
        """;

    private const string BareMinimalManifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
          <Identity Name="BareApp" Publisher="CN=Bare" Version="0.0.1.0" />
        </Package>
        """;

    #region Parse / ToXml Round-Trip

    [TestMethod]
    public void Parse_AndToXml_RoundTrips_PreservesContent()
    {
        var doc = AppxManifestDocument.Parse(MinimalManifest);
        var xml = doc.ToXml();

        Assert.Contains("TestApp", xml);
        Assert.Contains("CN=Test", xml);
        Assert.Contains("1.0.0.0", xml);
    }

    [TestMethod]
    public void Load_FromFile_AndSave_RoundTrips()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"AppxDocTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "appxmanifest.xml");
            File.WriteAllText(filePath, MinimalManifest);

            var doc = AppxManifestDocument.Load(filePath);
            Assert.AreEqual("TestApp", doc.IdentityName);

            doc.IdentityName = "ModifiedApp";
            var savePath = Path.Combine(tempDir, "saved.xml");
            doc.Save(savePath);

            var reloaded = AppxManifestDocument.Load(savePath);
            Assert.AreEqual("ModifiedApp", reloaded.IdentityName);

            // Verify UTF-8 no BOM
            var bytes = File.ReadAllBytes(savePath);
            Assert.IsFalse(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "File should not have a UTF-8 BOM");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [TestMethod]
    public void Load_FromStream_Works()
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(MinimalManifest));
        var doc = AppxManifestDocument.Load(stream);

        Assert.AreEqual("TestApp", doc.IdentityName);
        Assert.AreEqual("CN=Test", doc.IdentityPublisher);
    }

    #endregion

    #region Identity Properties

    [TestMethod]
    public void IdentityName_GetAndSet()
    {
        var doc = AppxManifestDocument.Parse(MinimalManifest);

        Assert.AreEqual("TestApp", doc.IdentityName);
        doc.IdentityName = "NewName";
        Assert.AreEqual("NewName", doc.IdentityName);
    }

    [TestMethod]
    public void IdentityPublisher_GetAndSet()
    {
        var doc = AppxManifestDocument.Parse(MinimalManifest);

        Assert.AreEqual("CN=Test", doc.IdentityPublisher);
        doc.IdentityPublisher = "CN=NewPublisher";
        Assert.AreEqual("CN=NewPublisher", doc.IdentityPublisher);
    }

    [TestMethod]
    public void IdentityVersion_GetAndSet()
    {
        var doc = AppxManifestDocument.Parse(MinimalManifest);

        Assert.AreEqual("1.0.0.0", doc.IdentityVersion);
        doc.IdentityVersion = "2.0.0.0";
        Assert.AreEqual("2.0.0.0", doc.IdentityVersion);
    }

    [TestMethod]
    public void IdentityProcessorArchitecture_GetAndSet()
    {
        var doc = AppxManifestDocument.Parse(MinimalManifest);

        Assert.AreEqual("x64", doc.IdentityProcessorArchitecture);
        doc.IdentityProcessorArchitecture = "arm64";
        Assert.AreEqual("arm64", doc.IdentityProcessorArchitecture);
    }

    [TestMethod]
    public void IdentityProperties_SetNull_RemovesAttribute()
    {
        var doc = AppxManifestDocument.Parse(MinimalManifest);

        doc.IdentityProcessorArchitecture = null;
        Assert.IsNull(doc.IdentityProcessorArchitecture);

        // Other attributes should be unaffected
        Assert.AreEqual("TestApp", doc.IdentityName);
    }

    [TestMethod]
    public void IdentityProperties_CreatesIdentityElement_WhenMissing()
    {
        // A manifest with no Identity element
        var xml = """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
            </Package>
            """;
        var doc = AppxManifestDocument.Parse(xml);

        Assert.IsNull(doc.IdentityName);

        doc.IdentityName = "CreatedApp";
        Assert.AreEqual("CreatedApp", doc.IdentityName);
        Assert.IsNotNull(doc.GetIdentityElement());
    }

    [TestMethod]
    public void IdentityProperties_SetNullOnMissingElement_IsNoOp()
    {
        var xml = """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
            </Package>
            """;
        var doc = AppxManifestDocument.Parse(xml);

        // Should not throw or create an Identity element
        doc.IdentityName = null;
        Assert.IsNull(doc.GetIdentityElement());
    }

    #endregion

    #region Application Properties

    [TestMethod]
    public void ApplicationId_GetAndSet()
    {
        var doc = AppxManifestDocument.Parse(MinimalManifest);

        Assert.AreEqual("App", doc.ApplicationId);
        doc.ApplicationId = "NewApp";
        Assert.AreEqual("NewApp", doc.ApplicationId);
    }

    [TestMethod]
    public void ApplicationExecutable_GetAndSet()
    {
        var doc = AppxManifestDocument.Parse(MinimalManifest);

        Assert.AreEqual("TestApp.exe", doc.ApplicationExecutable);
        doc.ApplicationExecutable = "Other.exe";
        Assert.AreEqual("Other.exe", doc.ApplicationExecutable);
    }

    [TestMethod]
    public void ApplicationEntryPoint_GetAndSet()
    {
        var doc = AppxManifestDocument.Parse(MinimalManifest);

        Assert.AreEqual("Windows.FullTrustApplication", doc.ApplicationEntryPoint);
        doc.ApplicationEntryPoint = "App.Main";
        Assert.AreEqual("App.Main", doc.ApplicationEntryPoint);
    }

    [TestMethod]
    public void ApplicationProperties_ReturnNull_WhenNoApplicationElement()
    {
        var doc = AppxManifestDocument.Parse(BareMinimalManifest);

        Assert.IsNull(doc.ApplicationId);
        Assert.IsNull(doc.ApplicationExecutable);
        Assert.IsNull(doc.ApplicationEntryPoint);
    }

    [TestMethod]
    public void ApplicationProperties_SetOnMissing_IsNoOp()
    {
        var doc = AppxManifestDocument.Parse(BareMinimalManifest);

        doc.ApplicationId = "ShouldNotThrow";
        Assert.IsNull(doc.ApplicationId); // still null because there's no Application element
    }

    [TestMethod]
    public void ApplicationExecutable_SetNull_RemovesAttribute()
    {
        var doc = AppxManifestDocument.Parse(MinimalManifest);
        Assert.AreEqual("TestApp.exe", doc.ApplicationExecutable);

        doc.ApplicationExecutable = null;

        Assert.IsNull(doc.ApplicationExecutable);
        Assert.DoesNotContain("Executable=", doc.ToXml());
    }

    #endregion

    #region VisualElements

    [TestMethod]
    public void VisualElementsDisplayName_GetAndSet()
    {
        var doc = AppxManifestDocument.Parse(MinimalManifest);

        Assert.AreEqual("Test App", doc.VisualElementsDisplayName);
        doc.VisualElementsDisplayName = "New Name";
        Assert.AreEqual("New Name", doc.VisualElementsDisplayName);
    }

    [TestMethod]
    public void VisualElementsDisplayName_ReturnsNull_WhenMissing()
    {
        var doc = AppxManifestDocument.Parse(BareMinimalManifest);
        Assert.IsNull(doc.VisualElementsDisplayName);
    }

    [TestMethod]
    public void VisualElementsDisplayName_SetNull_RemovesAttribute()
    {
        var doc = AppxManifestDocument.Parse(MinimalManifest);
        Assert.AreEqual("Test App", doc.VisualElementsDisplayName);

        doc.VisualElementsDisplayName = null;

        Assert.IsNull(doc.VisualElementsDisplayName);
    }

    [TestMethod]
    public void VisualElementsDisplayName_SetOnMissing_IsNoOp()
    {
        var doc = AppxManifestDocument.Parse(BareMinimalManifest);

        doc.VisualElementsDisplayName = "ShouldNotThrow";

        Assert.IsNull(doc.VisualElementsDisplayName); // still null because there is no VisualElements element
    }

    #endregion

    #region Resource Languages

    [TestMethod]
    public void GetResourceLanguages_ReturnsList()
    {
        var doc = AppxManifestDocument.Parse(MinimalManifest);

        var languages = doc.GetResourceLanguages();
        Assert.AreEqual(1, languages.Count);
        Assert.AreEqual("en-US", languages[0]);
    }

    [TestMethod]
    public void GetResourceLanguages_ReturnsEmpty_WhenNoResources()
    {
        var doc = AppxManifestDocument.Parse(BareMinimalManifest);

        var languages = doc.GetResourceLanguages();
        Assert.AreEqual(0, languages.Count);
    }

    [TestMethod]
    public void SetResourceLanguages_SingleLanguage()
    {
        var doc = AppxManifestDocument.Parse(MinimalManifest);

        doc.SetResourceLanguages(["fr-FR"]);
        var languages = doc.GetResourceLanguages();

        Assert.AreEqual(1, languages.Count);
        Assert.AreEqual("fr-FR", languages[0]);
    }

    [TestMethod]
    public void SetResourceLanguages_MultipleLanguages()
    {
        var doc = AppxManifestDocument.Parse(MinimalManifest);

        doc.SetResourceLanguages(["en-US", "fr-FR", "de-DE"]);
        var languages = doc.GetResourceLanguages();

        Assert.AreEqual(3, languages.Count);
        Assert.AreEqual("en-US", languages[0]);
        Assert.AreEqual("fr-FR", languages[1]);
        Assert.AreEqual("de-DE", languages[2]);
    }

    [TestMethod]
    public void SetResourceLanguages_ReplacesExisting()
    {
        var doc = AppxManifestDocument.Parse(MinimalManifest);

        // Replace en-US with ja-JP
        doc.SetResourceLanguages(["ja-JP"]);
        var languages = doc.GetResourceLanguages();

        Assert.AreEqual(1, languages.Count);
        Assert.AreEqual("ja-JP", languages[0]);
        Assert.DoesNotContain("en-US", doc.ToXml());
    }

    [TestMethod]
    public void SetResourceLanguages_CreatesResourcesElement_WhenMissing()
    {
        var doc = AppxManifestDocument.Parse(BareMinimalManifest);

        doc.SetResourceLanguages(["en-US"]);
        var languages = doc.GetResourceLanguages();

        Assert.AreEqual(1, languages.Count);
        Assert.AreEqual("en-US", languages[0]);
    }

    [TestMethod]
    public void SetResourceLanguages_AddsToRoot_WhenNoIdentityOrDependencies()
    {
        // No Identity and no Dependencies, so the new Resources element falls through
        // to being appended directly to the Package root.
        var xml = """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Applications>
                <Application Id="App" />
              </Applications>
            </Package>
            """;
        var doc = AppxManifestDocument.Parse(xml);

        doc.SetResourceLanguages(["en-US", "fr-FR"]);
        var languages = doc.GetResourceLanguages();

        Assert.AreEqual(2, languages.Count);
        Assert.AreEqual("en-US", languages[0]);
        Assert.AreEqual("fr-FR", languages[1]);
    }

    [TestMethod]
    public void SetResourceLanguages_InsertsAfterDependencies_WhenResourcesMissing()
    {
        // Identity + Dependencies but no Resources: the new Resources element must be
        // inserted immediately after Dependencies to satisfy the manifest schema ordering.
        var xml = """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="App" Publisher="CN=Test" Version="1.0.0.0" />
              <Dependencies>
                <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.22621.0" />
              </Dependencies>
              <Applications>
                <Application Id="App" />
              </Applications>
            </Package>
            """;
        var doc = AppxManifestDocument.Parse(xml);

        doc.SetResourceLanguages(["en-US"]);

        var result = doc.ToXml();
        var languages = doc.GetResourceLanguages();
        Assert.AreEqual(1, languages.Count);
        Assert.AreEqual("en-US", languages[0]);
        Assert.IsTrue(
            result.IndexOf("</Dependencies>", StringComparison.Ordinal) < result.IndexOf("<Resources", StringComparison.Ordinal),
            "Resources element is inserted after Dependencies");
        Assert.IsTrue(
            result.IndexOf("<Resources", StringComparison.Ordinal) < result.IndexOf("<Applications", StringComparison.Ordinal),
            "Resources element precedes Applications");
    }

    #endregion

    #region Namespace Management

    [TestMethod]
    public void AddIgnorableNamespace_AddsNew()
    {
        var xml = """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     IgnorableNamespaces="uap">
            </Package>
            """;
        var doc = AppxManifestDocument.Parse(xml);

        doc.AddIgnorableNamespace("rescap");

        var result = doc.ToXml();
        Assert.Contains("IgnorableNamespaces=\"uap rescap\"", result);
    }

    [TestMethod]
    public void AddIgnorableNamespace_SkipsDuplicate()
    {
        var xml = """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     IgnorableNamespaces="uap rescap">
            </Package>
            """;
        var doc = AppxManifestDocument.Parse(xml);

        doc.AddIgnorableNamespace("rescap");

        var result = doc.ToXml();
        // Should not duplicate
        Assert.AreEqual(1, CountOccurrences(result, "rescap"));
    }

    [TestMethod]
    public void AddIgnorableNamespace_CreatesAttribute_WhenMissing()
    {
        var xml = """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
            </Package>
            """;
        var doc = AppxManifestDocument.Parse(xml);

        doc.AddIgnorableNamespace("build");

        var result = doc.ToXml();
        Assert.Contains("IgnorableNamespaces=\"build\"", result);
    }

    [TestMethod]
    public void EnsureNamespace_AddsXmlnsDeclaration()
    {
        var xml = """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
            </Package>
            """;
        var doc = AppxManifestDocument.Parse(xml);

        doc.EnsureNamespace("build", AppxManifestDocument.BuildNs);

        var result = doc.ToXml();
        Assert.Contains("xmlns:build=\"http://schemas.microsoft.com/developer/appx/2015/build\"", result);
    }

    [TestMethod]
    public void EnsureNamespace_DoesNotDuplicate()
    {
        var xml = """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:build="http://schemas.microsoft.com/developer/appx/2015/build">
            </Package>
            """;
        var doc = AppxManifestDocument.Parse(xml);

        doc.EnsureNamespace("build", AppxManifestDocument.BuildNs);

        var result = doc.ToXml();
        Assert.AreEqual(1, CountOccurrences(result, "xmlns:build="));
    }

    #endregion

    #region Capabilities

    [TestMethod]
    public void EnsureCapability_AddsNew()
    {
        var doc = AppxManifestDocument.Parse(BareMinimalManifest);

        doc.EnsureCapability("runFullTrust");

        var xml = doc.ToXml();
        Assert.Contains("runFullTrust", xml);
        Assert.Contains("<Capabilities", xml);
    }

    [TestMethod]
    public void EnsureCapability_SkipsDuplicate()
    {
        var xml = """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities">
              <Capabilities>
                <rescap:Capability Name="runFullTrust" />
              </Capabilities>
            </Package>
            """;
        var doc = AppxManifestDocument.Parse(xml);

        // Adding the same capability (case-insensitive match) should not duplicate
        doc.EnsureCapability("runFullTrust");

        var result = doc.ToXml();
        Assert.AreEqual(1, CountOccurrences(result, "runFullTrust"));
    }

    [TestMethod]
    [DataRow("uap5", "http://schemas.microsoft.com/appx/manifest/uap/windows10/5", DisplayName = "uap5 form")]
    [DataRow("desktop", "http://schemas.microsoft.com/appx/manifest/desktop/windows10", DisplayName = "legacy desktop form")]
    public void GetExecutionAliases_FindsBothDeclarationNamespaces(string prefix, string ns)
    {
        // Both are valid. Reading only the uap5 form would treat an authored desktop:ExecutionAlias as
        // "no alias declared", so winapp would stage a second generated one instead of using the command
        // name the author chose — and could fail an explicit --with-alias on that generated name.
        var xml = $"""
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:{prefix}="{ns}">
              <Applications>
                <Application Id="App">
                  <Extensions>
                    <{prefix}:Extension Category="windows.appExecutionAlias">
                      <{prefix}:AppExecutionAlias>
                        <{prefix}:ExecutionAlias Alias="tool.exe" />
                      </{prefix}:AppExecutionAlias>
                    </{prefix}:Extension>
                  </Extensions>
                </Application>
              </Applications>
            </Package>
            """;

        var aliases = AppxManifestDocument.Parse(xml).GetExecutionAliases();

        Assert.AreEqual(1, aliases.Count);
        Assert.AreEqual("tool.exe", aliases[0]);
    }

    [TestMethod]
    public void GetExecutionAliases_NoneDeclared_ReturnsEmpty()
    {
        var xml = """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Applications><Application Id="App" /></Applications>
            </Package>
            """;

        Assert.AreEqual(0, AppxManifestDocument.Parse(xml).GetExecutionAliases().Count);
    }

    [TestMethod]
    public void EnsureCapability_TypedOverload_SkipsADuplicateSpelledDifferently()
    {
        // Two entries for one capability is a schema violation whatever the casing, and the sparse-manifest
        // check in MsixService already matches case-insensitively — these must not disagree.
        var xml = """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities">
              <Capabilities>
                <rescap:Capability Name="RunFullTrust" />
              </Capabilities>
            </Package>
            """;
        var doc = AppxManifestDocument.Parse(xml);

        doc.EnsureCapability(new AppxCapability(
            "runFullTrust", "Capability", "rescap", AppxManifestDocument.RescapNs));

        var result = doc.ToXml();
        Assert.AreEqual(1, CountOccurrences(result.ToLowerInvariant(), "runfulltrust"));
    }

    [TestMethod]
    public void EnsureCapability_WithCustomNamespace()
    {
        var doc = AppxManifestDocument.Parse(BareMinimalManifest);

        doc.EnsureCapability("runFullTrust", AppxManifestDocument.RescapNs);

        var result = doc.ToXml();
        Assert.Contains("runFullTrust", result);
    }

    [TestMethod]
    public void EnsureCapability_CreatesCapabilitiesElement_WhenMissing()
    {
        var doc = AppxManifestDocument.Parse(BareMinimalManifest);
        Assert.IsNull(doc.GetCapabilitiesElement());

        doc.EnsureCapability("internetClient");

        Assert.IsNotNull(doc.GetCapabilitiesElement());
        Assert.Contains("internetClient", doc.ToXml());
    }

    #endregion

    #region Build Metadata

    [TestMethod]
    public void SetBuildMetadata_CreatesSection()
    {
        var doc = AppxManifestDocument.Parse(BareMinimalManifest);

        doc.SetBuildMetadata("Microsoft.WinAppCli", "1.0.0");

        var xml = doc.ToXml();
        Assert.Contains("Microsoft.WinAppCli", xml);
        Assert.Contains("1.0.0", xml);
    }

    [TestMethod]
    public void SetBuildMetadata_UpdatesExisting()
    {
        var xml = """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:build="http://schemas.microsoft.com/developer/appx/2015/build">
              <Identity Name="Test" />
              <build:Metadata>
                <build:Item Name="Microsoft.WinAppCli" Version="0.0.1" />
              </build:Metadata>
            </Package>
            """;
        var doc = AppxManifestDocument.Parse(xml);

        doc.SetBuildMetadata("Microsoft.WinAppCli", "2.0.0");

        var result = doc.ToXml();
        Assert.Contains("2.0.0", result);
        Assert.DoesNotContain("0.0.1", result);
        Assert.AreEqual(1, CountOccurrences(result, "Microsoft.WinAppCli"));
    }

    [TestMethod]
    public void SetBuildMetadata_AddsAlongsideExisting()
    {
        var xml = """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:build="http://schemas.microsoft.com/developer/appx/2015/build">
              <Identity Name="Test" />
              <build:Metadata>
                <build:Item Name="OtherTool" Version="1.0.0" />
              </build:Metadata>
            </Package>
            """;
        var doc = AppxManifestDocument.Parse(xml);

        doc.SetBuildMetadata("Microsoft.WinAppCli", "3.0.0");

        var result = doc.ToXml();
        Assert.Contains("OtherTool", result);
        Assert.Contains("Microsoft.WinAppCli", result);
    }

    #endregion

    #region Package-Level Extensions

    [TestMethod]
    public void GetOrCreatePackageLevelExtensionsElement_CreatesAfterApplications()
    {
        var doc = AppxManifestDocument.Parse(MinimalManifest);
        Assert.IsNull(doc.GetExtensionsElement()); // no package-level extensions yet

        var extensions = doc.GetOrCreatePackageLevelExtensionsElement();
        Assert.IsNotNull(extensions);

        var xml = doc.ToXml();
        var applicationsClose = xml.IndexOf("</Applications>", StringComparison.Ordinal);
        var extensionsOpen = xml.IndexOf("<Extensions", StringComparison.Ordinal);
        Assert.IsTrue(extensionsOpen > applicationsClose,
            "Package-level Extensions should be after </Applications>");
    }

    [TestMethod]
    public void GetOrCreatePackageLevelExtensionsElement_ReturnsExisting()
    {
        var xml = """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Applications>
                <Application Id="App" />
              </Applications>
              <Extensions>
                <Extension Category="windows.activatableClass.proxyStub" />
              </Extensions>
            </Package>
            """;
        var doc = AppxManifestDocument.Parse(xml);

        var extensions = doc.GetOrCreatePackageLevelExtensionsElement();

        Assert.IsNotNull(extensions);
        Assert.IsTrue(extensions.HasElements, "Should return existing Extensions with children");
    }

    [TestMethod]
    public void GetOrCreatePackageLevelExtensionsElement_AddsToRoot_WhenNoApplications()
    {
        // No Applications element, so the new Extensions element is appended
        // directly to the Package root rather than after Applications.
        var doc = AppxManifestDocument.Parse(BareMinimalManifest);
        Assert.IsNull(doc.GetExtensionsElement());

        var extensions = doc.GetOrCreatePackageLevelExtensionsElement();

        Assert.IsNotNull(extensions);
        Assert.AreSame(extensions, doc.GetExtensionsElement());
        Assert.Contains("<Extensions", doc.ToXml());
    }

    [TestMethod]
    public void AddInProcessServerExtension_AddsCorrectStructure()
    {
        var doc = AppxManifestDocument.Parse(MinimalManifest);

        doc.AddInProcessServerExtension("MyRuntime.dll", ["My.Namespace.ClassA", "My.Namespace.ClassB"]);

        var xml = doc.ToXml();
        Assert.Contains("MyRuntime.dll", xml);
        Assert.Contains("My.Namespace.ClassA", xml);
        Assert.Contains("My.Namespace.ClassB", xml);
        Assert.Contains("windows.activatableClass.inProcessServer", xml);
        Assert.Contains("ThreadingModel=\"both\"", xml);
    }

    [TestMethod]
    public void GetRegisteredExtensionDllPaths_ReturnsAllPaths()
    {
        var xml = """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Extensions>
                <Extension Category="windows.activatableClass.inProcessServer">
                  <InProcessServer>
                    <Path>RuntimeA.dll</Path>
                    <ActivatableClass ActivatableClassId="ClassA" ThreadingModel="both"/>
                  </InProcessServer>
                </Extension>
                <Extension Category="windows.activatableClass.inProcessServer">
                  <InProcessServer>
                    <Path>RuntimeB.dll</Path>
                    <ActivatableClass ActivatableClassId="ClassB" ThreadingModel="both"/>
                  </InProcessServer>
                </Extension>
              </Extensions>
            </Package>
            """;
        var doc = AppxManifestDocument.Parse(xml);

        var paths = doc.GetRegisteredExtensionDllPaths();

        Assert.AreEqual(2, paths.Count);
        Assert.IsTrue(paths.Contains("RuntimeA.dll"));
        Assert.IsTrue(paths.Contains("RuntimeB.dll"));
    }

    [TestMethod]
    public void GetRegisteredExtensionDllPaths_IsCaseInsensitive()
    {
        var xml = """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Extensions>
                <Extension Category="windows.activatableClass.inProcessServer">
                  <InProcessServer>
                    <Path>Runtime.dll</Path>
                  </InProcessServer>
                </Extension>
                <Extension Category="windows.activatableClass.inProcessServer">
                  <InProcessServer>
                    <Path>runtime.dll</Path>
                  </InProcessServer>
                </Extension>
              </Extensions>
            </Package>
            """;
        var doc = AppxManifestDocument.Parse(xml);

        var paths = doc.GetRegisteredExtensionDllPaths();

        // Case-insensitive dedup: should be only 1
        Assert.AreEqual(1, paths.Count);
    }

    [TestMethod]
    public void GetRegisteredExtensionDllPaths_ReturnsEmpty_WhenNoPaths()
    {
        var doc = AppxManifestDocument.Parse(BareMinimalManifest);

        var paths = doc.GetRegisteredExtensionDllPaths();
        Assert.AreEqual(0, paths.Count);
    }

    #endregion

    #region Package Dependencies

    [TestMethod]
    public void HasPackageDependency_ReturnsFalse_WhenNone()
    {
        var doc = AppxManifestDocument.Parse(BareMinimalManifest);
        Assert.IsFalse(doc.HasPackageDependency("Microsoft.WindowsAppRuntime"));
    }

    [TestMethod]
    public void SetPackageDependency_AddsNew()
    {
        var doc = AppxManifestDocument.Parse(MinimalManifest);

        doc.SetPackageDependency(
            "Microsoft.WindowsAppRuntime.1.5",
            "5001.178.1908.0",
            "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US");

        Assert.IsTrue(doc.HasPackageDependency("Microsoft.WindowsAppRuntime"));
        var xml = doc.ToXml();
        Assert.Contains("Microsoft.WindowsAppRuntime.1.5", xml);
        Assert.Contains("5001.178.1908.0", xml);
    }

    [TestMethod]
    public void SetPackageDependency_UpdatesExisting()
    {
        var doc = AppxManifestDocument.Parse(MinimalManifest);

        doc.SetPackageDependency("MyDep", "1.0.0.0", "CN=Pub");
        doc.SetPackageDependency("MyDep", "2.0.0.0", "CN=Pub");

        var xml = doc.ToXml();
        Assert.Contains("MinVersion=\"2.0.0.0\"", xml);
        // The old MinVersion should be gone (but 1.0.0.0 exists in Identity, so check specifically)
        Assert.DoesNotContain("MinVersion=\"1.0.0.0\"", xml);
        Assert.AreEqual(1, CountOccurrences(xml, "MyDep"));
    }

    [TestMethod]
    public void SetPackageDependency_CreatesDependenciesElement_WhenMissing()
    {
        // BareMinimalManifest has no Dependencies element
        var xml = """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="Test" />
            </Package>
            """;
        var doc = AppxManifestDocument.Parse(xml);
        Assert.IsNull(doc.GetDependenciesElement());

        doc.SetPackageDependency("TestDep", "1.0.0.0", "CN=Test");

        Assert.IsNotNull(doc.GetDependenciesElement());
        Assert.IsTrue(doc.HasPackageDependency("TestDep"));
    }

    #endregion

    #region Element Accessors

    [TestMethod]
    public void GetFirstApplicationElement_ReturnsCorrectElement()
    {
        var doc = AppxManifestDocument.Parse(MinimalManifest);
        var app = doc.GetFirstApplicationElement();

        Assert.IsNotNull(app);
        Assert.AreEqual("App", app.Attribute("Id")?.Value);
    }

    [TestMethod]
    public void GetVisualElements_ReturnsCorrectElement()
    {
        var doc = AppxManifestDocument.Parse(MinimalManifest);
        var ve = doc.GetVisualElements();

        Assert.IsNotNull(ve);
        Assert.AreEqual("Test App", ve.Attribute("DisplayName")?.Value);
    }

    [TestMethod]
    public void GetResourcesElement_ReturnsCorrectElement()
    {
        var doc = AppxManifestDocument.Parse(MinimalManifest);
        Assert.IsNotNull(doc.GetResourcesElement());
    }

    [TestMethod]
    public void GetDependenciesElement_ReturnsCorrectElement()
    {
        var doc = AppxManifestDocument.Parse(MinimalManifest);
        Assert.IsNotNull(doc.GetDependenciesElement());
    }

    [TestMethod]
    public void AllAccessors_ReturnNull_ForBareManifest()
    {
        var doc = AppxManifestDocument.Parse(BareMinimalManifest);

        Assert.IsNull(doc.GetFirstApplicationElement());
        Assert.IsNull(doc.GetVisualElements());
        Assert.IsNull(doc.GetResourcesElement());
        Assert.IsNull(doc.GetExtensionsElement());
        Assert.IsNull(doc.GetCapabilitiesElement());
    }

    #endregion

    #region Helpers

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    #endregion
}
