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
    [TestCategory("MigrationAcceptance")]
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
        Assert.AreEqual("1.4", report.RootElement.GetProperty("schemaVersion").GetString());
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
    [TestCategory("MigrationAcceptance")]
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
    [TestCategory("MigrationAcceptance")]
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
    public async Task AnalyzeActivation_MalformedSourceManifestReportsInspectionFailure()
    {
        var source = _tempDirectory.CreateSubdirectory(
            "MalformedSourceManifestApp");
        await WriteAsync(
            source,
            "Package.appxmanifest",
            "<Package><Applications>");
        var target = NewTarget("malformed-source-manifest-output");
        target.Create();
        await WriteAsync(target, "Package.appxmanifest", TargetManifest);

        var result = MigrateCommand.Handler.AnalyzeActivationContracts(
            source.FullName,
            target.FullName,
            applyChanges: true);

        Assert.AreEqual("incomplete", result.Analysis.Status);
        Assert.AreEqual(0, result.ChangedFiles);
        Assert.IsTrue(result.Analysis.Issues.Any(issue =>
            issue.Kind == "source-manifest-inspection-failed"
            && issue.Severity == "error"));
    }

    [TestMethod]
    public async Task AnalyzeActivation_AmbiguousSourceManifestReportsError()
    {
        var source = _tempDirectory.CreateSubdirectory(
            "AmbiguousSourceManifestApp");
        await WriteAsync(
            source,
            "Package.appxmanifest",
            SourceManifest(string.Empty));
        await WriteAsync(
            source,
            "Second.appxmanifest",
            SourceManifest(string.Empty));
        var target = NewTarget("ambiguous-source-manifest-output");
        target.Create();
        await WriteAsync(target, "Package.appxmanifest", TargetManifest);

        var result = MigrateCommand.Handler.AnalyzeActivationContracts(
            source.FullName,
            target.FullName,
            applyChanges: true);

        Assert.AreEqual("incomplete", result.Analysis.Status);
        Assert.AreEqual(0, result.ChangedFiles);
        Assert.IsTrue(result.Analysis.Issues.Any(issue =>
            issue.Kind == "ambiguous-source-manifest"
            && issue.Severity == "error"));
    }

    [TestMethod]
    public async Task AnalyzeActivation_MalformedTargetManifestReportsFailure()
    {
        var source = await CreateSourceAsync(
            "MalformedTargetManifestApp",
            SourceManifest(
                """
                <uap:Extension Category="windows.protocol">
                  <uap:Protocol Name="malformed-target" />
                </uap:Extension>
                """));
        var target = NewTarget("malformed-target-manifest-output");
        target.Create();
        await WriteAsync(
            target,
            "Package.appxmanifest",
            "<Package><Applications>");

        var result = MigrateCommand.Handler.AnalyzeActivationContracts(
            source.FullName,
            target.FullName,
            applyChanges: true);

        Assert.AreEqual("failed", result.Analysis.Status);
        Assert.AreEqual(0, result.ChangedFiles);
        Assert.IsTrue(result.Analysis.Issues.Any(issue =>
            issue.Kind == "target-manifest-inspection-failed"
            && issue.Severity == "error"));
        Assert.AreEqual(
            "missing",
            result.Analysis.Contracts.Single().VerificationStatus);
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
        Assert.AreEqual("1.4", upgraded.RootElement.GetProperty("schemaVersion").GetString());
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
    public async Task Verify_IgnoresPackagingPrerequisitesOutsideMechanicalFragments()
    {
        var source = await CreateSourceAsync(
            "ExternalPackagingValidationApp",
            SourceManifest(
                """
                <uap:Extension Category="windows.protocol">
                  <uap:Protocol Name="external-package-validation" />
                </uap:Extension>
                """));
        var target = NewTarget("external-package-validation-output");
        ArrangeTemplateCreation(target, "ExternalPackagingValidationAppApp");
        var (migrateExit, migrateOutput) = await InvokeMigrateAsync(source, target);
        Assert.AreEqual(0, migrateExit, migrateOutput);

        var manifestPath = Path.Combine(target.FullName, "Package.appxmanifest");
        var manifest = XDocument.Load(manifestPath);
        manifest.Descendants()
            .Single(element =>
                element.Name.LocalName == "TargetDeviceFamily")
            .SetAttributeValue("Name", "Windows.Universal");
        manifest.Descendants()
            .Single(element =>
                element.Name.LocalName == "Capability"
                && element.Attribute("Name")?.Value == "runFullTrust")
            .Remove();
        manifest.Save(manifestPath);

        var (exit, output) = await InvokeVerifyAsync(target);

        Assert.AreEqual(0, exit, output);
        using var report = await ReadReportAsync(target);
        var activation = report.RootElement.GetProperty("activationAnalysis");
        Assert.AreEqual("passed", activation.GetProperty("status").GetString());
        Assert.AreEqual(
            "verified",
            activation.GetProperty("contracts")
                .EnumerateArray()
                .Single()
                .GetProperty("verificationStatus")
                .GetString());
    }

    [TestMethod]
    public async Task Verify_MissingPreviouslyAnalyzedSourceManifestPreservesContracts()
    {
        var source = await CreateSourceAsync(
            "MissingAnalyzedManifestApp",
            SourceManifest(
                """
                <uap:Extension Category="windows.protocol">
                  <uap:Protocol Name="preserved-protocol">
                    <uap:DisplayName>Preserved Protocol</uap:DisplayName>
                  </uap:Protocol>
                </uap:Extension>
                """));
        var target = NewTarget("missing-analyzed-manifest-output");
        ArrangeTemplateCreation(target, "MissingAnalyzedManifestAppApp");
        var (migrateExit, migrateOutput) = await InvokeMigrateAsync(source, target);
        Assert.AreEqual(0, migrateExit, migrateOutput);
        File.Delete(Path.Combine(source.FullName, "Package.appxmanifest"));

        var (retainedExit, retainedOutput) = await InvokeVerifyAsync(target);

        Assert.AreEqual(1, retainedExit, retainedOutput);
        using (var retainedReport = await ReadReportAsync(target))
        {
            Assert.AreEqual(
                "failed",
                retainedReport.RootElement
                    .GetProperty("mechanicalVerification")
                    .GetProperty("status")
                    .GetString());
            var activation = retainedReport.RootElement.GetProperty("activationAnalysis");
            Assert.AreEqual("incomplete", activation.GetProperty("status").GetString());
            var contract = activation.GetProperty("contracts").EnumerateArray().Single();
            Assert.AreEqual(
                "windows.protocol:preserved-protocol",
                contract.GetProperty("id").GetString());
            Assert.AreEqual(
                "Preserved Protocol",
                contract.GetProperty("displayName").GetString());
            Assert.AreEqual(
                "verified",
                contract.GetProperty("verificationStatus").GetString());
            Assert.IsTrue(activation.GetProperty("issues").EnumerateArray().Any(issue =>
                issue.GetProperty("kind").GetString() ==
                "source-manifest-missing-after-analysis"));
        }

        var targetManifestPath = Path.Combine(
            target.FullName,
            "Package.appxmanifest");
        var targetManifest = XDocument.Load(targetManifestPath);
        targetManifest.Descendants()
            .Single(element =>
                element.Name.LocalName == "Extension"
                && element.Attribute("Category")?.Value == "windows.protocol")
            .Remove();
        targetManifest.Save(targetManifestPath);

        var (removedExit, removedOutput) = await InvokeVerifyAsync(target);

        Assert.AreEqual(1, removedExit, removedOutput);
        using var removedReport = await ReadReportAsync(target);
        var removedActivation =
            removedReport.RootElement.GetProperty("activationAnalysis");
        Assert.AreEqual("incomplete", removedActivation.GetProperty("status").GetString());
        var removedContract = removedActivation
            .GetProperty("contracts")
            .EnumerateArray()
            .Single();
        Assert.AreEqual(
            "windows.protocol:preserved-protocol",
            removedContract.GetProperty("id").GetString());
        Assert.AreEqual(
            "missing",
            removedContract.GetProperty("verificationStatus").GetString());
    }

    [TestMethod]
    public async Task Verify_RemovedSourceContractCannotErasePriorFacts()
    {
        var source = await CreateSourceAsync(
            "RemovedSourceContractApp",
            SourceManifest(
                """
                <uap:Extension Category="windows.protocol">
                  <uap:Protocol Name="removed-source-contract" />
                </uap:Extension>
                """));
        var target = NewTarget("removed-source-contract-output");
        ArrangeTemplateCreation(target, "RemovedSourceContractAppApp");
        var (migrateExit, migrateOutput) = await InvokeMigrateAsync(source, target);
        Assert.AreEqual(0, migrateExit, migrateOutput);
        await WriteAsync(
            source,
            "Package.appxmanifest",
            SourceManifest(
                """
                <uap:Extension Category="windows.appService" />
                """));
        var targetManifestPath = Path.Combine(
            target.FullName,
            "Package.appxmanifest");
        var targetManifest = XDocument.Load(targetManifestPath);
        targetManifest.Descendants()
            .Single(element =>
                element.Name.LocalName == "Extension"
                && element.Attribute("Category")?.Value == "windows.protocol")
            .Remove();
        targetManifest.Save(targetManifestPath);

        var (exit, output) = await InvokeVerifyAsync(target);

        Assert.AreEqual(1, exit, output);
        using var report = await ReadReportAsync(target);
        var activation = report.RootElement.GetProperty("activationAnalysis");
        Assert.AreEqual("incomplete", activation.GetProperty("status").GetString());
        var contract = activation.GetProperty("contracts").EnumerateArray().Single();
        Assert.AreEqual(
            "windows.protocol:removed-source-contract",
            contract.GetProperty("id").GetString());
        Assert.AreEqual(
            "missing",
            contract.GetProperty("verificationStatus").GetString());
        Assert.IsTrue(activation.GetProperty("issues").EnumerateArray().Any(issue =>
            issue.GetProperty("kind").GetString() ==
            "source-activation-contracts-missing-after-analysis"));
    }

    [TestMethod]
    public async Task Verify_RepairedTargetRebuildsRetainedActivationStatusWithoutDuplicates()
    {
        var source = await CreateSourceAsync(
            "RepairRetainedActivationApp",
            SourceManifest(
                """
                <uap:Extension Category="windows.protocol">
                  <uap:Protocol Name="repair-retained-protocol" />
                </uap:Extension>
                """));
        var target = NewTarget("repair-retained-activation-output");
        ArrangeTemplateCreation(target, "RepairRetainedActivationAppApp");
        var (migrateExit, migrateOutput) = await InvokeMigrateAsync(source, target);
        Assert.AreEqual(0, migrateExit, migrateOutput);
        var sourceManifestPath = Path.Combine(
            source.FullName,
            "Package.appxmanifest");
        var targetManifestPath = Path.Combine(
            target.FullName,
            "Package.appxmanifest");
        var targetManifest = await File.ReadAllTextAsync(
            targetManifestPath,
            TestContext.CancellationToken);
        File.Delete(sourceManifestPath);
        File.Delete(targetManifestPath);

        var (missingExit, missingOutput) = await InvokeVerifyAsync(target);

        Assert.AreEqual(1, missingExit, missingOutput);
        using (var missingReport = await ReadReportAsync(target))
        {
            var issues = missingReport.RootElement
                .GetProperty("activationAnalysis")
                .GetProperty("issues")
                .EnumerateArray()
                .Select(issue => issue.GetProperty("kind").GetString())
                .ToList();
            Assert.IsTrue(issues.Contains("target-manifest-missing"));
            Assert.IsTrue(issues.Contains(
                "source-manifest-missing-after-analysis"));
        }

        await File.WriteAllTextAsync(
            targetManifestPath,
            targetManifest,
            TestContext.CancellationToken);
        var duplicateManifestPath = Path.Combine(
            target.FullName,
            "Duplicate.appxmanifest");
        await File.WriteAllTextAsync(
            duplicateManifestPath,
            targetManifest,
            TestContext.CancellationToken);
        var (ambiguousExit, ambiguousOutput) =
            await InvokeVerifyAsync(target);
        Assert.AreEqual(1, ambiguousExit, ambiguousOutput);
        using (var ambiguousReport = await ReadReportAsync(target))
        {
            Assert.AreEqual(
                1,
                ambiguousReport.RootElement
                    .GetProperty("activationAnalysis")
                    .GetProperty("issues")
                    .EnumerateArray()
                    .Count(issue =>
                        issue.GetProperty("kind").GetString()
                        == "ambiguous-target-manifest"));
        }
        File.Delete(duplicateManifestPath);
        var (firstRepairExit, firstRepairOutput) =
            await InvokeVerifyAsync(target);
        var (secondRepairExit, secondRepairOutput) =
            await InvokeVerifyAsync(target);

        Assert.AreEqual(1, firstRepairExit, firstRepairOutput);
        Assert.AreEqual(1, secondRepairExit, secondRepairOutput);
        using var repairedReport = await ReadReportAsync(target);
        var activation =
            repairedReport.RootElement.GetProperty("activationAnalysis");
        Assert.AreEqual("incomplete", activation.GetProperty("status").GetString());
        Assert.AreEqual(
            "verified",
            activation.GetProperty("contracts")
                .EnumerateArray()
                .Single()
                .GetProperty("verificationStatus")
                .GetString());
        var repairedIssues = activation.GetProperty("issues")
            .EnumerateArray()
            .Select(issue => issue.GetProperty("kind").GetString())
            .ToList();
        Assert.AreEqual(
            1,
            repairedIssues.Count(kind =>
                kind == "source-manifest-missing-after-analysis"));
        Assert.IsFalse(repairedIssues.Contains("target-manifest-missing"));
        Assert.IsFalse(repairedIssues.Contains("ambiguous-target-manifest"));
        Assert.IsFalse(repairedIssues.Any(kind =>
            kind?.StartsWith("target-", StringComparison.Ordinal) == true));
    }

    [TestMethod]
    public async Task Verify_TargetManifestFileSymlinkIsNotTrustedOrModified()
    {
        var source = await CreateSourceAsync(
            "SymlinkTargetManifestApp",
            SourceManifest(
                """
                <uap:Extension Category="windows.protocol">
                  <uap:Protocol Name="symlink-target-protocol" />
                </uap:Extension>
                """));
        var target = NewTarget("symlink-target-manifest-output");
        ArrangeTemplateCreation(target, "SymlinkTargetManifestAppApp");
        var (migrateExit, migrateOutput) = await InvokeMigrateAsync(source, target);
        Assert.AreEqual(0, migrateExit, migrateOutput);

        var targetManifestPath = Path.Combine(
            target.FullName,
            "Package.appxmanifest");
        var external = _tempDirectory.CreateSubdirectory(
            "external-target-manifest");
        var externalManifestPath = Path.Combine(
            external.FullName,
            "External.appxmanifest");
        var externalManifest = TargetManifest;
        await File.WriteAllTextAsync(
            externalManifestPath,
            externalManifest,
            TestContext.CancellationToken);
        File.Delete(targetManifestPath);
        try
        {
            File.CreateSymbolicLink(
                targetManifestPath,
                externalManifestPath);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
            or IOException
            or PlatformNotSupportedException)
        {
            Assert.Inconclusive(
                $"The host cannot create a file symbolic link: {exception.Message}");
            return;
        }

        var directAnalysis =
            MigrateCommand.Handler.AnalyzeActivationContracts(
                source.FullName,
                target.FullName,
                applyChanges: true);
        var (verifyExit, verifyOutput) = await InvokeVerifyAsync(target);

        Assert.AreEqual("failed", directAnalysis.Analysis.Status);
        Assert.AreEqual(0, directAnalysis.ChangedFiles);
        Assert.IsTrue(directAnalysis.Analysis.Issues.Any(issue =>
            issue.Kind == "target-manifest-path-unsafe"));
        Assert.AreEqual(1, verifyExit, verifyOutput);
        Assert.AreEqual(
            externalManifest,
            await File.ReadAllTextAsync(
                externalManifestPath,
                TestContext.CancellationToken));
        using var report = await ReadReportAsync(target);
        Assert.AreEqual(
            "failed",
            report.RootElement
                .GetProperty("mechanicalVerification")
                .GetProperty("status")
                .GetString());
        var activation = report.RootElement.GetProperty("activationAnalysis");
        Assert.IsTrue(activation.GetProperty("issues").EnumerateArray().Any(issue =>
            issue.GetProperty("kind").GetString() ==
            "target-manifest-path-unsafe"));
        Assert.IsFalse(activation.GetProperty("contracts").EnumerateArray().Any(contract =>
            contract.GetProperty("verificationStatus").GetString() == "verified"));
    }

    [TestMethod]
    public async Task AnalyzeActivation_TargetJunctionRootIsRejected()
    {
        var source = await CreateSourceAsync(
            "JunctionTargetManifestApp",
            SourceManifest(
                """
                <uap:Extension Category="windows.protocol">
                  <uap:Protocol Name="junction-target-protocol" />
                </uap:Extension>
                """));
        var physicalTarget = _tempDirectory.CreateSubdirectory(
            "physical-junction-target");
        var physicalManifestPath = Path.Combine(
            physicalTarget.FullName,
            "Package.appxmanifest");
        await File.WriteAllTextAsync(
            physicalManifestPath,
            TargetManifest,
            TestContext.CancellationToken);
        var junctionPath = Path.Combine(
            _tempDirectory.FullName,
            "junction-target-manifest");
        if (!TryCreateJunction(
                junctionPath,
                physicalTarget.FullName))
        {
            Assert.Inconclusive(
                "The host cannot create a Windows directory junction.");
            return;
        }

        try
        {
            var before = await File.ReadAllTextAsync(
                physicalManifestPath,
                TestContext.CancellationToken);

            var result = MigrateCommand.Handler.AnalyzeActivationContracts(
                source.FullName,
                junctionPath,
                applyChanges: true);

            Assert.AreEqual("failed", result.Analysis.Status);
            Assert.AreEqual(0, result.ChangedFiles);
            Assert.IsTrue(result.Analysis.Issues.Any(issue =>
                issue.Kind == "target-manifest-path-unsafe"));
            Assert.AreEqual(
                before,
                await File.ReadAllTextAsync(
                    physicalManifestPath,
                    TestContext.CancellationToken));
        }
        finally
        {
            Directory.Delete(junctionPath);
        }
    }

    [TestMethod]
    public async Task MigrateAndVerify_CsprojOnlySourceKeepsActivationUnavailable()
    {
        var source = _tempDirectory.CreateSubdirectory("CsprojOnlyActivationApp");
        await WriteAsync(source, "CsprojOnlyActivationApp.csproj", CleanCsproj);
        var target = NewTarget("csproj-only-activation-output");
        ArrangeTemplateCreation(target, "CsprojOnlyActivationAppApp");

        var (migrateExit, migrateOutput) = await InvokeMigrateAsync(source, target);
        var (verifyExit, verifyOutput) = await InvokeVerifyAsync(target);

        Assert.AreEqual(0, migrateExit, migrateOutput);
        Assert.AreEqual(0, verifyExit, verifyOutput);
        using var report = await ReadReportAsync(target);
        var activation = report.RootElement.GetProperty("activationAnalysis");
        Assert.AreEqual("not-available", activation.GetProperty("status").GetString());
        Assert.AreEqual(0, activation.GetProperty("contracts").GetArrayLength());
        Assert.AreEqual(
            "passed",
            report.RootElement
                .GetProperty("mechanicalVerification")
                .GetProperty("status")
                .GetString());
    }

    [TestMethod]
    public async Task MigrateAndVerify_ManifestOnlyBaselineKeepsNotRequired()
    {
        var source = _tempDirectory.CreateSubdirectory("ManifestOnlyActivationApp");
        await WriteAsync(
            source,
            "Package.appxmanifest",
            SourceManifest(
                """
                <uap:Extension Category="windows.appService" />
                """));
        var target = NewTarget("manifest-only-activation-output");
        ArrangeTemplateCreation(target, "ManifestOnlyActivationAppApp");

        var (migrateExit, migrateOutput) = await InvokeMigrateAsync(source, target);
        var (verifyExit, verifyOutput) = await InvokeVerifyAsync(target);

        Assert.AreEqual(0, migrateExit, migrateOutput);
        Assert.AreEqual(0, verifyExit, verifyOutput);
        using var report = await ReadReportAsync(target);
        var activation = report.RootElement.GetProperty("activationAnalysis");
        Assert.AreEqual("not-required", activation.GetProperty("status").GetString());
        Assert.AreEqual(0, activation.GetProperty("contracts").GetArrayLength());
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

    private static bool TryCreateJunction(
        string link,
        string target)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process =
                System.Diagnostics.Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }
            process.WaitForExit(5000);
            return process.ExitCode == 0
                && Directory.Exists(link);
        }
        catch
        {
            return false;
        }
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
