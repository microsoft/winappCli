// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MigrateActivationTests : MigrateCommandTestBase
{
    [TestMethod]
    public async Task Migrate_ReportsAndMigratesProtocolAndFileAssociationContracts()
    {
        var source = await CreateSourceAsync(
            "ActivationApp",
            """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
              <Applications>
                <Application Id="App">
                  <Extensions>
                    <uap:Extension Category="windows.protocol">
                      <uap:Protocol Name="sample-protocol" DesiredView="useMore">
                        <uap:Logo>Assets\Protocol.png</uap:Logo>
                        <uap:DisplayName>Sample Protocol</uap:DisplayName>
                      </uap:Protocol>
                    </uap:Extension>
                    <uap:Extension Category="windows.fileTypeAssociation">
                      <uap:FileTypeAssociation Name="sample-files" MultiSelectModel="Document">
                        <uap:DisplayName>Sample File</uap:DisplayName>
                        <uap:Logo>Assets\File.png</uap:Logo>
                        <uap:SupportedFileTypes>
                          <uap:FileType ContentType="application/x-sample">.sample</uap:FileType>
                          <uap:FileType>.sample2</uap:FileType>
                        </uap:SupportedFileTypes>
                      </uap:FileTypeAssociation>
                    </uap:Extension>
                  </Extensions>
                </Application>
              </Applications>
            </Package>
            """);
        var target = NewTarget("activation-output");
        ArrangeTemplateCreation(target, "ActivationAppApp");

        var (exit, output) = await InvokeMigrateAsync(source, target);

        Assert.AreEqual(0, exit, output);
        using var report = await ReadReportAsync(target);
        Assert.AreEqual("1.3", report.RootElement.GetProperty("schemaVersion").GetString());
        var analysis = report.RootElement.GetProperty("activationAnalysis");
        Assert.AreEqual("passed", analysis.GetProperty("status").GetString());
        var contracts = analysis.GetProperty("contracts").EnumerateArray().ToList();
        Assert.HasCount(2, contracts);

        var protocol = contracts.Single(contract =>
            contract.GetProperty("category").GetString() == "windows.protocol");
        Assert.AreEqual("windows.protocol:sample-protocol", protocol.GetProperty("id").GetString());
        Assert.AreEqual("sample-protocol", protocol.GetProperty("protocolName").GetString());
        Assert.AreEqual("Sample Protocol", protocol.GetProperty("displayName").GetString());
        Assert.AreEqual(@"Assets\Protocol.png", protocol.GetProperty("logo").GetString());
        Assert.AreEqual("uap", protocol.GetProperty("sourceSchema").GetString());
        Assert.AreEqual("migrated", protocol.GetProperty("migrationStatus").GetString());
        Assert.AreEqual("verified", protocol.GetProperty("verificationStatus").GetString());
        Assert.AreEqual(
            "Package.appxmanifest",
            protocol.GetProperty("sourceLocation").GetProperty("path").GetString());
        Assert.AreEqual(
            6,
            protocol.GetProperty("sourceLocation").GetProperty("line").GetInt32());

        var fileAssociation = contracts.Single(contract =>
            contract.GetProperty("category").GetString() == "windows.fileTypeAssociation");
        Assert.AreEqual(
            "windows.filetypeassociation:sample-files",
            fileAssociation.GetProperty("id").GetString());
        Assert.AreEqual(
            "sample-files",
            fileAssociation.GetProperty("associationName").GetString());
        var fileTypes = fileAssociation.GetProperty("supportedFileTypes")
            .EnumerateArray()
            .ToList();
        Assert.HasCount(2, fileTypes);
        Assert.IsTrue(fileTypes.Any(fileType =>
            fileType.GetProperty("extension").GetString() == ".sample"
            && fileType.GetProperty("contentType").GetString() == "application/x-sample"));
        Assert.AreEqual(
            12,
            fileAssociation.GetProperty("sourceLocation").GetProperty("line").GetInt32());

        var targetManifest = XDocument.Load(
            Path.Combine(target.FullName, "Package.appxmanifest"));
        var root = targetManifest.Root!;
        Assert.AreEqual(
            "http://schemas.microsoft.com/appx/manifest/uap/windows10/3",
            root.GetNamespaceOfPrefix("uap3")?.NamespaceName);
        Assert.AreEqual(
            1,
            (root.Attribute("IgnorableNamespaces")?.Value ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Count(value => value == "uap3"));
        var targetExtensions = targetManifest.Descendants()
            .Where(element => element.Name.LocalName == "Extension")
            .ToList();
        Assert.HasCount(2, targetExtensions);
        Assert.IsTrue(targetExtensions.All(extension =>
            extension.Name.NamespaceName ==
            "http://schemas.microsoft.com/appx/manifest/uap/windows10/3"));

        var mechanical = report.RootElement
            .GetProperty("mechanicalVerification")
            .GetProperty("activationContracts");
        Assert.AreEqual(2, mechanical.GetProperty("sourceContracts").GetInt32());
        Assert.AreEqual(2, mechanical.GetProperty("migratedContracts").GetInt32());
        Assert.AreEqual(2, mechanical.GetProperty("verifiedContracts").GetInt32());
        Assert.IsTrue(report.RootElement.GetProperty("todos").EnumerateArray().Any(todo =>
            todo.GetProperty("id").GetString() == "UWMIG002"
            && todo.GetProperty("status").GetString() == "pending"));
    }

    [TestMethod]
    public async Task Verify_IsIdempotentDetectsActivationDriftAndPreservesSemanticState()
    {
        var source = await CreateSourceAsync(
            "ActivationVerifyApp",
            SourceManifest(
                """
                <uap:Extension Category="windows.protocol">
                  <uap:Protocol Name="verify-protocol" />
                </uap:Extension>
                <uap:Extension Category="windows.fileTypeAssociation">
                  <uap:FileTypeAssociation Name="verify-files">
                    <uap:SupportedFileTypes>
                      <uap:FileType>.verify</uap:FileType>
                    </uap:SupportedFileTypes>
                  </uap:FileTypeAssociation>
                </uap:Extension>
                """));
        var target = NewTarget("activation-verify-output");
        ArrangeTemplateCreation(target, "ActivationVerifyAppApp");
        var (migrateExit, migrateOutput) = await InvokeMigrateAsync(source, target);
        Assert.AreEqual(0, migrateExit, migrateOutput);

        var reportPath = Path.Combine(target.FullName, "migration-report.json");
        var reportNode = JsonNode.Parse(await File.ReadAllTextAsync(
            reportPath,
            TestContext.CancellationToken))!;
        reportNode["validation"]!["sourceBaseline"]!["status"] = "captured";
        var manifestTodo = reportNode["todos"]!.AsArray()
            .Single(todo => todo!["id"]!.GetValue<string>() == "UWMIG002")!;
        manifestTodo["status"] = "resolved";
        await File.WriteAllTextAsync(
            reportPath,
            reportNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            TestContext.CancellationToken);

        var (firstExit, firstOutput) = await InvokeVerifyAsync(target);
        var (secondExit, secondOutput) = await InvokeVerifyAsync(target);

        Assert.AreEqual(0, firstExit, firstOutput);
        Assert.AreEqual(0, secondExit, secondOutput);
        var targetManifestPath = Path.Combine(target.FullName, "Package.appxmanifest");
        var targetManifest = XDocument.Load(targetManifestPath);
        Assert.AreEqual(
            2,
            targetManifest.Descendants().Count(element =>
                element.Name.LocalName == "Extension"));
        Assert.AreEqual(
            1,
            (targetManifest.Root!.Attribute("IgnorableNamespaces")?.Value ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Count(value => value == "uap3"));

        var fileType = targetManifest.Descendants()
            .Single(element => element.Name.LocalName == "FileType");
        fileType.Value = ".changed";
        targetManifest.Save(targetManifestPath);
        var (driftExit, driftOutput) = await InvokeVerifyAsync(target);

        Assert.AreEqual(1, driftExit, driftOutput);
        using (var driftReport = await ReadReportAsync(target))
        {
            var drifted = driftReport.RootElement
                .GetProperty("activationAnalysis")
                .GetProperty("contracts")
                .EnumerateArray()
                .Single(contract =>
                    contract.GetProperty("category").GetString() ==
                    "windows.fileTypeAssociation");
            Assert.AreEqual("drifted", drifted.GetProperty("verificationStatus").GetString());
            Assert.AreEqual(
                "captured",
                driftReport.RootElement
                    .GetProperty("validation")
                    .GetProperty("sourceBaseline")
                    .GetProperty("status")
                    .GetString());
            Assert.AreEqual(
                "resolved",
                driftReport.RootElement
                    .GetProperty("todos")
                    .EnumerateArray()
                    .Single(todo => todo.GetProperty("id").GetString() == "UWMIG002")
                    .GetProperty("status")
                    .GetString());
        }

        targetManifest.Descendants()
            .Single(element =>
                element.Name.LocalName == "Extension"
                && element.Attribute("Category")?.Value == "windows.protocol")
            .Remove();
        targetManifest.Save(targetManifestPath);
        var (missingExit, missingOutput) = await InvokeVerifyAsync(target);

        Assert.AreEqual(1, missingExit, missingOutput);
        using var missingReport = await ReadReportAsync(target);
        var missing = missingReport.RootElement
            .GetProperty("activationAnalysis")
            .GetProperty("contracts")
            .EnumerateArray()
            .Single(contract =>
                contract.GetProperty("category").GetString() == "windows.protocol");
        Assert.AreEqual("missing", missing.GetProperty("verificationStatus").GetString());
    }

    [TestMethod]
    public async Task Migrate_NoActivationContracts_ReportsNotRequired()
    {
        var source = await CreateSourceAsync(
            "NoActivationApp",
            SourceManifest(
                """
                <uap:Extension Category="windows.appService" />
                """));
        var target = NewTarget("no-activation-output");
        ArrangeTemplateCreation(target, "NoActivationAppApp");

        var (exit, output) = await InvokeMigrateAsync(source, target);

        Assert.AreEqual(0, exit, output);
        using var report = await ReadReportAsync(target);
        var activation = report.RootElement.GetProperty("activationAnalysis");
        Assert.AreEqual("not-required", activation.GetProperty("status").GetString());
        Assert.AreEqual(0, activation.GetProperty("contracts").GetArrayLength());
        var targetManifest = XDocument.Load(
            Path.Combine(target.FullName, "Package.appxmanifest"));
        Assert.IsNull(targetManifest.Root!.GetNamespaceOfPrefix("uap3"));
    }

    [TestMethod]
    public async Task Migrate_UnsupportedActivationDeclaration_RemainsReviewRequired()
    {
        var source = await CreateSourceAsync(
            "UnsupportedActivationApp",
            SourceManifest(
                """
                <uap:Extension Category="windows.protocol">
                  <uap:Protocol Name="unsafe-protocol" Parameters="-- &quot;%1&quot;">
                    <uap:DisplayName>Unsafe</uap:DisplayName>
                  </uap:Protocol>
                </uap:Extension>
                """));
        var target = NewTarget("unsupported-activation-output");
        ArrangeTemplateCreation(target, "UnsupportedActivationAppApp");

        var (exit, output) = await InvokeMigrateAsync(source, target);

        Assert.AreEqual(0, exit, output);
        using var report = await ReadReportAsync(target);
        var activation = report.RootElement.GetProperty("activationAnalysis");
        Assert.AreEqual("review-required", activation.GetProperty("status").GetString());
        var contract = activation.GetProperty("contracts").EnumerateArray().Single();
        Assert.AreEqual("review-required", contract.GetProperty("migrationStatus").GetString());
        Assert.AreEqual(
            "not-applicable",
            contract.GetProperty("verificationStatus").GetString());
        Assert.IsTrue(activation.GetProperty("issues").EnumerateArray().Any(issue =>
            issue.GetProperty("kind").GetString() ==
            "protocol-attribute-review-required"));
        var targetManifest = XDocument.Load(
            Path.Combine(target.FullName, "Package.appxmanifest"));
        Assert.IsFalse(targetManifest.Descendants().Any(element =>
            element.Name.LocalName == "Protocol"));
    }

    [TestMethod]
    public async Task Migrate_VendorActivationElementsAreInspectionIssuesAndNeverMigrated()
    {
        var source = await CreateSourceAsync(
            "VendorActivationApp",
            """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                     xmlns:vendor="urn:vendor">
              <Applications>
                <Application Id="App">
                  <Extensions>
                    <vendor:Extension Category="windows.protocol">
                      <vendor:Protocol Name="vendor-extension" />
                    </vendor:Extension>
                    <uap:Extension Category="windows.protocol">
                      <vendor:Protocol Name="vendor-protocol" />
                    </uap:Extension>
                    <uap:Extension vendor:Category="windows.fileTypeAssociation">
                      <uap:FileTypeAssociation Name="vendor-category">
                        <uap:SupportedFileTypes>
                          <uap:FileType>.vendor</uap:FileType>
                        </uap:SupportedFileTypes>
                      </uap:FileTypeAssociation>
                    </uap:Extension>
                  </Extensions>
                </Application>
              </Applications>
            </Package>
            """);
        var target = NewTarget("vendor-activation-output");
        ArrangeTemplateCreation(target, "VendorActivationAppApp");

        var (exit, output) = await InvokeMigrateAsync(source, target);

        Assert.AreEqual(0, exit, output);
        using var report = await ReadReportAsync(target);
        var activation = report.RootElement.GetProperty("activationAnalysis");
        Assert.AreEqual("review-required", activation.GetProperty("status").GetString());
        Assert.AreEqual(0, activation.GetProperty("contracts").GetArrayLength());
        var issueKinds = activation.GetProperty("issues")
            .EnumerateArray()
            .Select(issue => issue.GetProperty("kind").GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.IsTrue(issueKinds.Contains(
            "source-activation-namespace-unsupported"));
        Assert.IsTrue(issueKinds.Contains(
            "protocol-namespace-unsupported"));
        Assert.IsTrue(issueKinds.Contains(
            "source-activation-category-namespace-unsupported"));
        var targetManifest = XDocument.Load(
            Path.Combine(target.FullName, "Package.appxmanifest"));
        Assert.IsFalse(targetManifest.Descendants().Any(element =>
            element.Name.NamespaceName ==
            "http://schemas.microsoft.com/appx/manifest/uap/windows10/3"
            && element.Name.LocalName == "Extension"));
    }

    [TestMethod]
    public async Task Migrate_WrongNamespaceActivationFactsRemainReviewRequired()
    {
        var source = await CreateSourceAsync(
            "WrongNamespaceFactsApp",
            """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                     xmlns:vendor="urn:vendor">
              <Applications>
                <Application Id="App">
                  <Extensions>
                    <uap:Extension Category="windows.protocol">
                      <uap:Protocol Name="wrong-protocol-facts">
                        <vendor:Logo>Assets\Wrong.png</vendor:Logo>
                        <vendor:DisplayName>Wrong Protocol</vendor:DisplayName>
                      </uap:Protocol>
                    </uap:Extension>
                    <uap:Extension Category="windows.fileTypeAssociation">
                      <uap:FileTypeAssociation Name="wrong-file-facts">
                        <vendor:DisplayName>Wrong File</vendor:DisplayName>
                        <vendor:Logo>Assets\WrongFile.png</vendor:Logo>
                        <uap:SupportedFileTypes>
                          <vendor:FileType>.wrong</vendor:FileType>
                        </uap:SupportedFileTypes>
                      </uap:FileTypeAssociation>
                    </uap:Extension>
                    <uap:Extension Category="windows.fileTypeAssociation">
                      <uap:FileTypeAssociation Name="wrong-file-group">
                        <vendor:SupportedFileTypes>
                          <vendor:FileType>.vendor</vendor:FileType>
                        </vendor:SupportedFileTypes>
                      </uap:FileTypeAssociation>
                    </uap:Extension>
                  </Extensions>
                </Application>
              </Applications>
            </Package>
            """);
        var target = NewTarget("wrong-namespace-facts-output");
        ArrangeTemplateCreation(target, "WrongNamespaceFactsAppApp");

        var (exit, output) = await InvokeMigrateAsync(source, target);

        Assert.AreEqual(0, exit, output);
        using var report = await ReadReportAsync(target);
        var activation = report.RootElement.GetProperty("activationAnalysis");
        Assert.AreEqual("review-required", activation.GetProperty("status").GetString());
        Assert.IsTrue(activation.GetProperty("contracts").EnumerateArray().All(contract =>
            contract.GetProperty("migrationStatus").GetString() == "review-required"
            && contract.GetProperty("verificationStatus").GetString() == "not-applicable"));
        Assert.IsTrue(activation.GetProperty("issues").GetArrayLength() >= 6);
        var targetManifest = XDocument.Load(
            Path.Combine(target.FullName, "Package.appxmanifest"));
        Assert.IsFalse(targetManifest.Descendants().Any(element =>
            element.Name.NamespaceName ==
            "http://schemas.microsoft.com/appx/manifest/uap/windows10/3"
            && element.Name.LocalName == "Extension"));
    }

    [TestMethod]
    public async Task Migrate_Uap3SourceDeclarationsUseUapFactChildren()
    {
        var source = await CreateSourceAsync(
            "Uap3SourceApp",
            """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                     xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3">
              <Applications>
                <Application Id="App">
                  <Extensions>
                    <uap3:Extension Category="windows.protocol">
                      <uap3:Protocol Name="uap3-source">
                        <uap:DisplayName>UAP3 Source</uap:DisplayName>
                        <uap:Logo>Assets\Uap3.png</uap:Logo>
                      </uap3:Protocol>
                    </uap3:Extension>
                  </Extensions>
                </Application>
              </Applications>
            </Package>
            """);
        var target = NewTarget("uap3-source-output");
        ArrangeTemplateCreation(target, "Uap3SourceAppApp");

        var (exit, output) = await InvokeMigrateAsync(source, target);

        Assert.AreEqual(0, exit, output);
        using var report = await ReadReportAsync(target);
        var contract = report.RootElement
            .GetProperty("activationAnalysis")
            .GetProperty("contracts")
            .EnumerateArray()
            .Single();
        Assert.AreEqual("uap3", contract.GetProperty("sourceSchema").GetString());
        Assert.AreEqual("verified", contract.GetProperty("verificationStatus").GetString());
        Assert.AreEqual("UAP3 Source", contract.GetProperty("displayName").GetString());
    }

    [TestMethod]
    public async Task Migrate_UapExtensionAcceptsUap3ProtocolDeclaration()
    {
        var source = await CreateSourceAsync(
            "MixedProtocolSchemaApp",
            """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                     xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3">
              <Applications>
                <Application Id="App">
                  <Extensions>
                    <uap:Extension Category="windows.protocol">
                      <uap3:Protocol Name="mixed-protocol">
                        <uap:DisplayName>Mixed Protocol</uap:DisplayName>
                      </uap3:Protocol>
                    </uap:Extension>
                  </Extensions>
                </Application>
              </Applications>
            </Package>
            """);
        var target = NewTarget("mixed-protocol-schema-output");
        ArrangeTemplateCreation(target, "MixedProtocolSchemaAppApp");

        var (exit, output) = await InvokeMigrateAsync(source, target);

        Assert.AreEqual(0, exit, output);
        using var report = await ReadReportAsync(target);
        var contract = report.RootElement
            .GetProperty("activationAnalysis")
            .GetProperty("contracts")
            .EnumerateArray()
            .Single();
        Assert.AreEqual(
            "uap-extension/uap3-declaration",
            contract.GetProperty("sourceSchema").GetString());
        Assert.AreEqual(
            "verified",
            contract.GetProperty("verificationStatus").GetString());
    }

    [TestMethod]
    public async Task Migrate_UapExtensionAcceptsUap3FileAssociationDeclaration()
    {
        var source = await CreateSourceAsync(
            "MixedFileSchemaApp",
            """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                     xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3">
              <Applications>
                <Application Id="App">
                  <Extensions>
                    <uap:Extension Category="windows.fileTypeAssociation">
                      <uap3:FileTypeAssociation Name="mixed-files">
                        <uap:DisplayName>Mixed File</uap:DisplayName>
                        <uap:SupportedFileTypes>
                          <uap:FileType>.mixed</uap:FileType>
                        </uap:SupportedFileTypes>
                      </uap3:FileTypeAssociation>
                    </uap:Extension>
                  </Extensions>
                </Application>
              </Applications>
            </Package>
            """);
        var target = NewTarget("mixed-file-schema-output");
        ArrangeTemplateCreation(target, "MixedFileSchemaAppApp");

        var (exit, output) = await InvokeMigrateAsync(source, target);

        Assert.AreEqual(0, exit, output);
        using var report = await ReadReportAsync(target);
        var contract = report.RootElement
            .GetProperty("activationAnalysis")
            .GetProperty("contracts")
            .EnumerateArray()
            .Single();
        Assert.AreEqual(
            "uap-extension/uap3-declaration",
            contract.GetProperty("sourceSchema").GetString());
        Assert.AreEqual(
            "verified",
            contract.GetProperty("verificationStatus").GetString());
        Assert.AreEqual(
            ".mixed",
            contract.GetProperty("supportedFileTypes")[0]
                .GetProperty("extension")
                .GetString());
    }

    [TestMethod]
    public async Task Migrate_ValidAndVendorSiblingDeclarationsRemainReviewRequired()
    {
        var source = await CreateSourceAsync(
            "SiblingDeclarationNamespaceApp",
            """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                     xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3"
                     xmlns:vendor="urn:vendor">
              <Applications>
                <Application Id="App">
                  <Extensions>
                    <uap:Extension Category="windows.protocol">
                      <uap3:Protocol Name="valid-protocol" />
                      <vendor:Protocol Name="vendor-protocol" />
                    </uap:Extension>
                    <uap:Extension Category="windows.fileTypeAssociation">
                      <uap3:FileTypeAssociation Name="valid-files">
                        <uap:SupportedFileTypes>
                          <uap:FileType>.valid</uap:FileType>
                        </uap:SupportedFileTypes>
                      </uap3:FileTypeAssociation>
                      <vendor:FileTypeAssociation Name="vendor-files" />
                    </uap:Extension>
                  </Extensions>
                </Application>
              </Applications>
            </Package>
            """);
        var target = NewTarget("sibling-declaration-namespace-output");
        ArrangeTemplateCreation(target, "SiblingDeclarationNamespaceAppApp");

        var (exit, output) = await InvokeMigrateAsync(source, target);

        Assert.AreEqual(0, exit, output);
        using var report = await ReadReportAsync(target);
        var activation = report.RootElement.GetProperty("activationAnalysis");
        Assert.AreEqual("review-required", activation.GetProperty("status").GetString());
        Assert.IsTrue(activation.GetProperty("contracts").EnumerateArray().All(contract =>
            contract.GetProperty("migrationStatus").GetString() == "review-required"));
        var issueKinds = activation.GetProperty("issues")
            .EnumerateArray()
            .Select(issue => issue.GetProperty("kind").GetString())
            .ToList();
        Assert.IsTrue(issueKinds.Contains(
            "protocol-extension-child-review-required"));
        Assert.IsTrue(issueKinds.Contains(
            "file-association-extension-child-review-required"));
        var targetManifest = XDocument.Load(
            Path.Combine(target.FullName, "Package.appxmanifest"));
        Assert.IsFalse(targetManifest.Descendants().Any(element =>
            element.Name.NamespaceName ==
            "http://schemas.microsoft.com/appx/manifest/uap/windows10/3"
            && element.Name.LocalName == "Extension"));
    }

    [TestMethod]
    public async Task Verify_WrongNamespaceTargetFactsAreNeverVerified()
    {
        var source = await CreateSourceAsync(
            "WrongTargetNamespaceApp",
            SourceManifest(
                """
                <uap:Extension Category="windows.protocol">
                  <uap:Protocol Name="target-protocol">
                    <uap:Logo>Assets\Protocol.png</uap:Logo>
                  </uap:Protocol>
                </uap:Extension>
                <uap:Extension Category="windows.fileTypeAssociation">
                  <uap:FileTypeAssociation Name="target-files">
                    <uap:SupportedFileTypes>
                      <uap:FileType>.target</uap:FileType>
                    </uap:SupportedFileTypes>
                  </uap:FileTypeAssociation>
                </uap:Extension>
                """));
        var target = NewTarget("wrong-target-namespace-output");
        ArrangeTemplateCreation(target, "WrongTargetNamespaceAppApp");
        var (migrateExit, migrateOutput) = await InvokeMigrateAsync(source, target);
        Assert.AreEqual(0, migrateExit, migrateOutput);

        var manifestPath = Path.Combine(target.FullName, "Package.appxmanifest");
        var manifest = XDocument.Load(manifestPath);
        XNamespace vendor = "urn:vendor";
        manifest.Descendants()
            .Single(element => element.Name.LocalName == "Logo")
            .Name = vendor + "Logo";
        manifest.Descendants()
            .Single(element => element.Name.LocalName == "FileType")
            .Name = vendor + "FileType";
        manifest.Save(manifestPath);

        var (exit, output) = await InvokeVerifyAsync(target);

        Assert.AreEqual(1, exit, output);
        using var report = await ReadReportAsync(target);
        var contracts = report.RootElement
            .GetProperty("activationAnalysis")
            .GetProperty("contracts")
            .EnumerateArray()
            .ToList();
        Assert.IsTrue(contracts.All(contract =>
            contract.GetProperty("verificationStatus").GetString() == "drifted"));
        var issues = report.RootElement
            .GetProperty("activationAnalysis")
            .GetProperty("issues")
            .EnumerateArray()
            .Select(issue => issue.GetProperty("kind").GetString())
            .ToList();
        Assert.IsTrue(issues.Contains(
            "target-activation-fact-namespace-unsupported"));
        Assert.IsTrue(issues.Contains(
            "target-file-type-namespace-unsupported"));
    }

    [TestMethod]
    public async Task Verify_WrongNamespaceTargetAttributesAreNeverVerified()
    {
        var source = await CreateSourceAsync(
            "WrongTargetAttributeNamespaceApp",
            SourceManifest(
                """
                <uap:Extension Category="windows.protocol">
                  <uap:Protocol Name="attribute-protocol" />
                </uap:Extension>
                <uap:Extension Category="windows.fileTypeAssociation">
                  <uap:FileTypeAssociation Name="attribute-files">
                    <uap:SupportedFileTypes>
                      <uap:FileType>.attribute</uap:FileType>
                    </uap:SupportedFileTypes>
                  </uap:FileTypeAssociation>
                </uap:Extension>
                """));
        var target = NewTarget("wrong-target-attribute-namespace-output");
        ArrangeTemplateCreation(target, "WrongTargetAttributeNamespaceAppApp");
        var (migrateExit, migrateOutput) = await InvokeMigrateAsync(source, target);
        Assert.AreEqual(0, migrateExit, migrateOutput);

        var manifestPath = Path.Combine(target.FullName, "Package.appxmanifest");
        var manifest = XDocument.Load(manifestPath);
        XNamespace vendor = "urn:vendor";
        var protocolExtension = manifest.Descendants()
            .Single(element =>
                element.Name.LocalName == "Extension"
                && element.Attribute("Category")?.Value == "windows.protocol");
        protocolExtension.SetAttributeValue(vendor + "Category", "windows.protocol");
        protocolExtension.Elements().Single()
            .SetAttributeValue(vendor + "DesiredView", "useMore");
        manifest.Descendants()
            .Single(element => element.Name.LocalName == "FileType")
            .SetAttributeValue(vendor + "ContentType", "application/x-vendor");
        manifest.Save(manifestPath);

        var (exit, output) = await InvokeVerifyAsync(target);

        Assert.AreEqual(1, exit, output);
        using var report = await ReadReportAsync(target);
        var activation = report.RootElement.GetProperty("activationAnalysis");
        Assert.IsTrue(activation.GetProperty("contracts").EnumerateArray().All(contract =>
            contract.GetProperty("verificationStatus").GetString() == "drifted"));
        Assert.IsTrue(activation.GetProperty("issues").EnumerateArray().Any(issue =>
            issue.GetProperty("kind").GetString() ==
            "target-activation-attribute-namespace-unsupported"));
    }

    [TestMethod]
    public async Task Migrate_DuplicateActivationIdentity_RemainsReviewRequired()
    {
        var source = await CreateSourceAsync(
            "DuplicateActivationApp",
            SourceManifest(
                """
                <uap:Extension Category="windows.protocol">
                  <uap:Protocol Name="duplicate-protocol" />
                </uap:Extension>
                <uap:Extension Category="windows.protocol">
                  <uap:Protocol Name="duplicate-protocol" />
                </uap:Extension>
                """));
        var target = NewTarget("duplicate-activation-output");
        ArrangeTemplateCreation(target, "DuplicateActivationAppApp");

        var (exit, output) = await InvokeMigrateAsync(source, target);

        Assert.AreEqual(0, exit, output);
        using var report = await ReadReportAsync(target);
        var activation = report.RootElement.GetProperty("activationAnalysis");
        Assert.AreEqual("review-required", activation.GetProperty("status").GetString());
        Assert.IsTrue(activation.GetProperty("contracts").EnumerateArray().All(contract =>
            contract.GetProperty("migrationStatus").GetString() == "review-required"));
        Assert.IsTrue(activation.GetProperty("issues").EnumerateArray().Any(issue =>
            issue.GetProperty("kind").GetString() ==
            "duplicate-source-activation-contract"));
        var targetManifest = XDocument.Load(
            Path.Combine(target.FullName, "Package.appxmanifest"));
        Assert.IsFalse(targetManifest.Descendants().Any(element =>
            element.Name.LocalName == "Protocol"));
    }

    [TestMethod]
    public async Task Verify_UpgradesSchema12WithoutLosingValidationOrDependencies()
    {
        var source = await CreateSourceAsync(
            "UpgradeApp",
            SourceManifest(string.Empty));
        var target = NewTarget("upgrade-output");
        ArrangeTemplateCreation(target, "UpgradeAppApp");
        var (migrateExit, migrateOutput) = await InvokeMigrateAsync(source, target);
        Assert.AreEqual(0, migrateExit, migrateOutput);

        var reportPath = Path.Combine(target.FullName, "migration-report.json");
        var report = JsonNode.Parse(await File.ReadAllTextAsync(
            reportPath,
            TestContext.CancellationToken))!;
        report["schemaVersion"] = "1.2";
        report.AsObject().Remove("activationAnalysis");
        report["mechanicalVerification"]!.AsObject().Remove("activationContracts");
        report["validation"]!["sourceBaseline"]!["status"] = "captured";
        report["dependencyAnalysis"]!["status"] = "resolved";
        report["dependencyAnalysis"]!["issues"]!.AsArray().Add(new JsonObject
        {
            ["kind"] = "preserved-test-issue",
            ["sourceProject"] = "UpgradeApp.csproj",
            ["reason"] = "preserve me"
        });
        await File.WriteAllTextAsync(
            reportPath,
            report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            TestContext.CancellationToken);

        var (exit, output) = await InvokeVerifyAsync(target);

        Assert.AreEqual(0, exit, output);
        using var upgraded = await ReadReportAsync(target);
        Assert.AreEqual("1.3", upgraded.RootElement.GetProperty("schemaVersion").GetString());
        Assert.AreEqual(
            "not-required",
            upgraded.RootElement
                .GetProperty("activationAnalysis")
                .GetProperty("status")
                .GetString());
        Assert.AreEqual(
            "captured",
            upgraded.RootElement
                .GetProperty("validation")
                .GetProperty("sourceBaseline")
                .GetProperty("status")
                .GetString());
        var dependencies = upgraded.RootElement.GetProperty("dependencyAnalysis");
        Assert.AreEqual("resolved", dependencies.GetProperty("status").GetString());
        Assert.IsTrue(dependencies.GetProperty("issues").EnumerateArray().Any(issue =>
            issue.GetProperty("kind").GetString() == "preserved-test-issue"));
    }

    [TestMethod]
    public async Task Verify_RejectsSystemManagedLifecycleWithoutRequiredCapability()
    {
        var source = await CreateSourceAsync(
            "InvalidCapabilityApp",
            SourceManifest(
                """
                <uap:Extension Category="windows.protocol">
                  <uap:Protocol Name="invalid-capability" />
                </uap:Extension>
                """));
        var target = NewTarget("invalid-capability-output");
        ArrangeTemplateCreation(target, "InvalidCapabilityAppApp");
        var (migrateExit, migrateOutput) = await InvokeMigrateAsync(source, target);
        Assert.AreEqual(0, migrateExit, migrateOutput);

        var manifestPath = Path.Combine(target.FullName, "Package.appxmanifest");
        var manifest = XDocument.Load(manifestPath);
        XNamespace desktop11 =
            "http://schemas.microsoft.com/appx/manifest/desktop/windows10/11";
        manifest.Root!.Add(new XAttribute(XNamespace.Xmlns + "desktop11", desktop11));
        var ignorable = manifest.Root.Attribute("IgnorableNamespaces")!.Value;
        manifest.Root.SetAttributeValue(
            "IgnorableNamespaces",
            $"{ignorable} desktop11");
        manifest.Descendants()
            .Single(element =>
                element.Name.LocalName == "Extension"
                && element.Attribute("Category")?.Value == "windows.protocol")
            .SetAttributeValue(
                desktop11 + "AppLifecycleBehavior",
                "systemManaged");
        manifest.Save(manifestPath);

        var (exit, output) = await InvokeVerifyAsync(target);

        Assert.AreEqual(1, exit, output);
        using var report = await ReadReportAsync(target);
        var activation = report.RootElement.GetProperty("activationAnalysis");
        Assert.AreEqual("failed", activation.GetProperty("status").GetString());
        Assert.IsTrue(activation.GetProperty("issues").EnumerateArray().Any(issue =>
            issue.GetProperty("kind").GetString() ==
            "shell-experience-capability-required"
            && issue.GetProperty("severity").GetString() == "error"));
    }

    private async Task<DirectoryInfo> CreateSourceAsync(
        string name,
        string manifest)
    {
        var source = _tempDirectory.CreateSubdirectory(name);
        await WriteAsync(source, $"{name}.csproj", CleanCsproj);
        await WriteAsync(source, "Package.appxmanifest", manifest);
        return source;
    }

    private void ArrangeTemplateCreation(
        DirectoryInfo target,
        string projectName)
    {
        FakeDotNet.RunDotnetCommandHandler = arguments =>
        {
            var templateOutput = GetTemplateOutput(arguments);
            templateOutput.Create();
            File.WriteAllText(
                Path.Combine(templateOutput.FullName, $"{projectName}.csproj"),
                CleanCsproj);
            File.WriteAllText(
                Path.Combine(templateOutput.FullName, "App.xaml"),
                $"<Application x:Class=\"{projectName}.App\" />");
            File.WriteAllText(
                Path.Combine(templateOutput.FullName, "App.xaml.cs"),
                $"namespace {projectName}; public partial class App {{ }}");
            File.WriteAllText(
                Path.Combine(templateOutput.FullName, "MainWindow.xaml"),
                $"<Window x:Class=\"{projectName}.MainWindow\" />");
            File.WriteAllText(
                Path.Combine(templateOutput.FullName, "MainWindow.xaml.cs"),
                $"namespace {projectName}; public partial class MainWindow {{ }}");
            File.WriteAllText(
                Path.Combine(templateOutput.FullName, "Package.appxmanifest"),
                TargetManifest);
            return (0, "Template created.", string.Empty);
        };
    }

    private async Task<(int ExitCode, string Output)> InvokeMigrateAsync(
        DirectoryInfo source,
        DirectoryInfo target)
        => await InvokeCapturingConsoleAsync(
            GetRequiredService<MigrateCommand>(),
            source.FullName,
            "--output",
            target.FullName);

    private Task<(int ExitCode, string Output)> InvokeVerifyAsync(
        DirectoryInfo target) =>
        InvokeCapturingConsoleAsync(
            GetRequiredService<MigrateVerifyCommand>(),
            target.FullName);

    private async Task<JsonDocument> ReadReportAsync(DirectoryInfo target) =>
        JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"),
            TestContext.CancellationToken));

    private DirectoryInfo NewTarget(string name) =>
        new(Path.Combine(_tempDirectory.FullName, name));

    private async Task WriteAsync(
        DirectoryInfo directory,
        string relativePath,
        string content)
    {
        var path = Path.Combine(directory.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, TestContext.CancellationToken);
    }

    private static DirectoryInfo GetTemplateOutput(string arguments)
    {
        var tokens = WindowsCommandLine.SplitArguments(arguments);
        var outputIndex = tokens.ToList().IndexOf("--output");
        Assert.IsTrue(outputIndex >= 0 && outputIndex + 1 < tokens.Count);
        return new DirectoryInfo(tokens[outputIndex + 1]);
    }

    private static string SourceManifest(string extensions) =>
        $$"""
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
          <Applications>
            <Application Id="App">
              <Extensions>
        {{extensions}}
              </Extensions>
            </Application>
          </Applications>
        </Package>
        """;

    private const string TargetManifest = """
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
                 IgnorableNamespaces="uap rescap">
          <Dependencies>
            <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" />
          </Dependencies>
          <Applications>
            <Application Id="App" Executable="$targetnametoken$.exe" EntryPoint="$targetentrypoint$">
              <uap:VisualElements DisplayName="Test" Description="Test"
                                  Square150x150Logo="Assets\Logo.png"
                                  Square44x44Logo="Assets\SmallLogo.png"
                                  BackgroundColor="transparent" />
            </Application>
          </Applications>
          <Capabilities>
            <rescap:Capability Name="runFullTrust" />
          </Capabilities>
        </Package>
        """;
}
