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
public sealed class MigrateProjectItemDecisionTests : MigrateCommandTestBase
{
    [TestMethod]
    public async Task DecideProjectItem_RecordsEverySupportedStrategy()
    {
        var (source, target) = await CreateDecisionMigrationAsync(
            "SupportedStrategiesApp",
            includeConditionalItem: true);
        await AddTargetEvidenceAsync(source, target);
        var items = await ReadReviewItemsAsync(target);

        var sdk = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\en-us\Resources.resw"),
            "--strategy", "sdk-default-item",
            "--target-path", @"Resources\en-us\Resources.resw",
            "--evidence-file", "Directory.Build.targets",
            "--rationale", "WinUI default PRI includes the resource and the target updates its metadata.");
        var explicitItem = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\fr-fr\Resources.resw"),
            "--strategy", "explicit-target-item",
            "--target-path", @"Resources\fr-fr\Resources.resw",
            "--target-item-type", "PRIResource",
            "--evidence-file", "Directory.Build.targets",
            "--rationale", "The target explicitly includes the resource with preserved metadata.");
        var copiedLink = await InvokeDecisionAsync(
            target,
            "--item", ItemIdByLink(items, @"Strings\NOTICE.json"),
            "--strategy", "copied-linked-content",
            "--target-path", @"Strings\NOTICE.json",
            "--target-item-type", "Content",
            "--evidence-file", "Directory.Build.targets",
            "--rationale", "The linked content is copied byte-for-byte to its target link path.");
        var intentionallyOmitted = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Data\**\*.json"),
            "--strategy", "intentionally-not-migrated",
            "--rationale", "The conditional wildcard remains a semantic review decision.");

        Assert.AreEqual(0, sdk.ExitCode, sdk.Output);
        Assert.AreEqual(0, explicitItem.ExitCode, explicitItem.Output);
        Assert.AreEqual(0, copiedLink.ExitCode, copiedLink.Output);
        Assert.AreEqual(0, intentionallyOmitted.ExitCode, intentionallyOmitted.Output);

        using var report = await ReadReportAsync(target);
        var decisions = report.RootElement
            .GetProperty("projectItemDecisions")
            .EnumerateArray()
            .ToList();
        Assert.HasCount(4, decisions);
        Assert.AreEqual(
            "verified",
            Decision(decisions, "sdk-default-item")
                .GetProperty("verification")
                .GetProperty("status")
                .GetString());
        Assert.AreEqual(
            "verified",
            Decision(decisions, "explicit-target-item")
                .GetProperty("verification")
                .GetProperty("status")
                .GetString());
        var copied = Decision(decisions, "copied-linked-content");
        Assert.AreEqual(
            "verified",
            copied.GetProperty("verification").GetProperty("status").GetString());
        Assert.IsTrue(
            copied.GetProperty("verification").GetProperty("contentMatches").GetBoolean());
        Assert.AreEqual(
            "review-required",
            Decision(decisions, "intentionally-not-migrated")
                .GetProperty("verification")
                .GetProperty("status")
                .GetString());
        Assert.IsTrue(report.RootElement.GetProperty("todos").EnumerateArray().Any(todo =>
            todo.GetProperty("id").GetString() == "UWMIG012"));
    }

    [TestMethod]
    public async Task DecideProjectItem_VerifiedDecisionsResolveAndVerifyReopensUwmig012()
    {
        var (source, target) = await CreateDecisionMigrationAsync(
            "DecisionClosureApp",
            includeConditionalItem: false);
        await AddTargetEvidenceAsync(source, target);
        var items = await ReadReviewItemsAsync(target);

        var sdk = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\en-us\Resources.resw"),
            "--strategy", "sdk-default-item",
            "--target-path", @"Resources\en-us\Resources.resw",
            "--evidence-file", "Directory.Build.targets",
            "--rationale", "Default PRI coverage with matching metadata.");
        var explicitItem = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\fr-fr\Resources.resw"),
            "--strategy", "explicit-target-item",
            "--target-path", @"Resources\fr-fr\Resources.resw",
            "--target-item-type", "PRIResource",
            "--evidence-file", "Directory.Build.targets",
            "--rationale", "Explicit PRIResource coverage.");
        var copiedLink = await InvokeDecisionAsync(
            target,
            "--item", ItemIdByLink(items, @"Strings\NOTICE.json"),
            "--strategy", "copied-linked-content",
            "--target-path", @"Strings\NOTICE.json",
            "--target-item-type", "Content",
            "--evidence-file", "Directory.Build.targets",
            "--rationale", "Copied linked content with matching bytes.");

        Assert.AreEqual(0, sdk.ExitCode, sdk.Output);
        Assert.AreEqual(0, explicitItem.ExitCode, explicitItem.Output);
        Assert.AreEqual(0, copiedLink.ExitCode, copiedLink.Output);
        using (var closed = await ReadReportAsync(target))
        {
            Assert.IsFalse(closed.RootElement.GetProperty("todos").EnumerateArray().Any(todo =>
                todo.GetProperty("id").GetString() == "UWMIG012"));
            var itemsVerification = closed.RootElement
                .GetProperty("mechanicalVerification")
                .GetProperty("projectItems");
            Assert.AreEqual(3, itemsVerification.GetProperty("verifiedDecisionItems").GetInt32());
            Assert.AreEqual(
                itemsVerification.GetProperty("sourceItems").GetInt32(),
                itemsVerification.GetProperty("migratedItems").GetInt32());
        }

        File.Delete(Path.Combine(target.FullName, "Strings", "NOTICE.json"));
        var (verifyExit, verifyOutput) = await InvokeVerifyAsync(target);

        Assert.AreEqual(0, verifyExit, verifyOutput);
        using var reopened = await ReadReportAsync(target);
        Assert.IsTrue(reopened.RootElement.GetProperty("todos").EnumerateArray().Any(todo =>
            todo.GetProperty("id").GetString() == "UWMIG012"));
        var copiedDecision = reopened.RootElement
            .GetProperty("projectItemDecisions")
            .EnumerateArray()
            .Single(decision =>
                decision.GetProperty("strategy").GetString() == "copied-linked-content");
        Assert.AreEqual(
            "invalid",
            copiedDecision.GetProperty("verification").GetProperty("status").GetString());
        StringAssert.Contains(
            copiedDecision.GetProperty("verification").GetProperty("reason").GetString(),
            "does not exist");
    }

    [TestMethod]
    public async Task DecideProjectItem_RejectsAmbiguousIdentityAndUnsupportedStrategy()
    {
        var (_, target) = await CreateDecisionMigrationAsync(
            "DecisionValidationApp",
            includeConditionalItem: true);

        var ambiguous = await InvokeDecisionAsync(
            target,
            "--item", "project-item-",
            "--strategy", "intentionally-not-migrated",
            "--rationale", "Ambiguous on purpose.");
        var unsupported = await InvokeDecisionAsync(
            target,
            "--item", "project-item-",
            "--strategy", "force-resolved",
            "--rationale", "Freeform success must not be accepted.");

        Assert.AreEqual(1, ambiguous.ExitCode, ambiguous.Output);
        StringAssert.Contains(ambiguous.Output, "ambiguous");
        Assert.AreEqual(1, unsupported.ExitCode, unsupported.Output);
        StringAssert.Contains(unsupported.Output, "ambiguous");

        var items = await ReadReviewItemsAsync(target);
        var unsupportedExact = await InvokeDecisionAsync(
            target,
            "--item", items[0]!["id"]!.GetValue<string>(),
            "--strategy", "force-resolved",
            "--rationale", "Freeform success must not be accepted.");
        Assert.AreEqual(1, unsupportedExact.ExitCode, unsupportedExact.Output);
        StringAssert.Contains(unsupportedExact.Output, "Unsupported strategy");
    }

    [TestMethod]
    public async Task DecideProjectItem_RequiresAllFourPositionalArguments()
    {
        var (_, target) = await CreateDecisionMigrationAsync(
            "RequiredDecisionArgumentsApp",
            includeConditionalItem: false);
        var items = await ReadReviewItemsAsync(target);
        var item = ItemId(items, @"Resources\en-us\Resources.resw");

        var (exit, _) = await InvokeCapturingConsoleAsync(
            GetRequiredService<MigrateProjectItemDecisionCommand>(),
            target.FullName,
            item,
            "sdk-default-item");

        Assert.AreNotEqual(0, exit);
    }

    [TestMethod]
    public async Task DecideProjectItem_RejectsMissingEvidenceAndRationaleCannotBypassIt()
    {
        var (_, target) = await CreateDecisionMigrationAsync(
            "MissingEvidenceApp",
            includeConditionalItem: false);
        var before = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"),
            TestContext.CancellationToken);
        var items = await ReadReviewItemsAsync(target);

        var missingFile = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\en-us\Resources.resw"),
            "--strategy", "sdk-default-item",
            "--target-path", @"Resources\missing\Resources.resw",
            "--rationale", "Treat this as resolved even though evidence is missing.");
        var noProjectItem = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\fr-fr\Resources.resw"),
            "--strategy", "explicit-target-item",
            "--target-path", @"Resources\fr-fr\Resources.resw",
            "--target-item-type", "PRIResource",
            "--rationale", "Trust the rationale without an MSBuild item.");

        Assert.AreEqual(1, missingFile.ExitCode, missingFile.Output);
        StringAssert.Contains(missingFile.Output, "does not exist");
        Assert.AreEqual(1, noProjectItem.ExitCode, noProjectItem.Output);
        StringAssert.Contains(noProjectItem.Output, "No active matching PRIResource");
        Assert.AreEqual(
            before,
            await File.ReadAllTextAsync(
                Path.Combine(target.FullName, "migration-report.json"),
                TestContext.CancellationToken));
        Assert.IsFalse(Directory.EnumerateFiles(
            target.FullName,
            ".migration-report.json.*.tmp",
            SearchOption.TopDirectoryOnly).Any());
    }

    [TestMethod]
    public async Task DecideProjectItem_RejectsUnusedAndUnimportedEvidenceFiles()
    {
        var (_, target) = await CreateDecisionMigrationAsync(
            "UnusedEvidenceApp",
            includeConditionalItem: false);
        var items = await ReadReviewItemsAsync(target);
        const string evidence = """
            <Project>
              <ItemGroup>
                <PRIResource Include="Resources\fr-fr\Resources.resw">
                  <SubType>Designer</SubType>
                </PRIResource>
              </ItemGroup>
            </Project>
            """;
        await WriteAsync(target, "UnusedEvidence.xml", evidence);
        await WriteAsync(target, "Build\\Unused.targets", evidence);

        foreach (var evidenceFile in new[]
        {
            "UnusedEvidence.xml",
            "Build\\Unused.targets"
        })
        {
            var result = await InvokeDecisionAsync(
                target,
                "--item", ItemId(items, @"Resources\fr-fr\Resources.resw"),
                "--strategy", "explicit-target-item",
                "--target-path", @"Resources\fr-fr\Resources.resw",
                "--target-item-type", "PRIResource",
                "--evidence-file", evidenceFile,
                "--rationale", "An existing but unused XML file must not count as build evidence.");

            Assert.AreEqual(1, result.ExitCode, result.Output);
            StringAssert.Contains(result.Output, "not the target project");
        }
    }

    [TestMethod]
    public async Task DecideProjectItem_RejectsConditionedItemsAndImports()
    {
        var (_, target) = await CreateDecisionMigrationAsync(
            "ConditionedEvidenceApp",
            includeConditionalItem: false);
        var items = await ReadReviewItemsAsync(target);
        await WriteAsync(
            target,
            "Build\\ConditionedItem.targets",
            """
            <Project>
              <ItemGroup Condition="'$(Configuration)' == 'Debug'">
                <PRIResource Include="Resources\fr-fr\Resources.resw">
                  <SubType>Designer</SubType>
                </PRIResource>
              </ItemGroup>
            </Project>
            """);
        await WriteAsync(
            target,
            "Build\\ConditionedImport.targets",
            """
            <Project>
              <ItemGroup>
                <PRIResource Include="Resources\fr-fr\Resources.resw">
                  <SubType>Designer</SubType>
                </PRIResource>
              </ItemGroup>
            </Project>
            """);
        await AppendTargetProjectXmlAsync(
            target,
            """
              <Import Project="Build\ConditionedItem.targets" />
              <Import Project="Build\ConditionedImport.targets"
                      Condition="'$(Configuration)' == 'Debug'" />
            """);

        var conditionedItem = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\fr-fr\Resources.resw"),
            "--strategy", "explicit-target-item",
            "--target-path", @"Resources\fr-fr\Resources.resw",
            "--target-item-type", "PRIResource",
            "--evidence-file", "Build\\ConditionedItem.targets",
            "--rationale", "Unknown configuration conditions cannot close review.");
        var conditionedImport = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\fr-fr\Resources.resw"),
            "--strategy", "explicit-target-item",
            "--target-path", @"Resources\fr-fr\Resources.resw",
            "--target-item-type", "PRIResource",
            "--evidence-file", "Build\\ConditionedImport.targets",
            "--rationale", "A conditioned import cannot be assumed active.");

        Assert.AreEqual(1, conditionedItem.ExitCode, conditionedItem.Output);
        StringAssert.Contains(conditionedItem.Output, "conditioned");
        Assert.AreEqual(1, conditionedImport.ExitCode, conditionedImport.Output);
        StringAssert.Contains(conditionedImport.Output, "conditioned");
    }

    [TestMethod]
    public async Task DecideProjectItem_RejectsUnprovenDirectoryBuildImportControl()
    {
        var (_, target) = await CreateDecisionMigrationAsync(
            "DirectoryBuildControlApp",
            includeConditionalItem: false);
        var items = await ReadReviewItemsAsync(target);
        await WriteAsync(
            target,
            "Directory.Build.targets",
            """
            <Project>
              <ItemGroup>
                <PRIResource Include="Resources\fr-fr\Resources.resw">
                  <SubType>Designer</SubType>
                </PRIResource>
              </ItemGroup>
            </Project>
            """);
        await AppendTargetProjectXmlAsync(
            target,
            """
              <PropertyGroup>
                <ImportDirectoryBuildTargets>$(UndefinedImportControl)</ImportDirectoryBuildTargets>
              </PropertyGroup>
            """);

        var result = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\fr-fr\Resources.resw"),
            "--strategy", "explicit-target-item",
            "--target-path", @"Resources\fr-fr\Resources.resw",
            "--target-item-type", "PRIResource",
            "--evidence-file", "Directory.Build.targets",
            "--rationale", "An unevaluated import control cannot authenticate build evidence.");

        Assert.AreEqual(1, result.ExitCode, result.Output);
        StringAssert.Contains(result.Output, "not proven enabled");
    }

    [TestMethod]
    public async Task DecideProjectItem_RejectsSdkQualifiedLocalImportLookalike()
    {
        var (_, target) = await CreateDecisionMigrationAsync(
            "SdkQualifiedImportApp",
            includeConditionalItem: false);
        var items = await ReadReviewItemsAsync(target);
        await WriteAsync(
            target,
            "Build\\SdkItems.targets",
            """
            <Project>
              <ItemGroup>
                <PRIResource Include="Resources\fr-fr\Resources.resw">
                  <SubType>Designer</SubType>
                </PRIResource>
              </ItemGroup>
            </Project>
            """);
        await AppendTargetProjectXmlAsync(
            target,
            """<Import Project="Build\SdkItems.targets" Sdk="Vendor.Build.Sdk" />""");

        var result = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\fr-fr\Resources.resw"),
            "--strategy", "explicit-target-item",
            "--target-path", @"Resources\fr-fr\Resources.resw",
            "--target-item-type", "PRIResource",
            "--evidence-file", "Build\\SdkItems.targets",
            "--rationale", "SDK-qualified imports resolve through the SDK, not beside the project.");

        Assert.AreEqual(1, result.ExitCode, result.Output);
        StringAssert.Contains(result.Output, "SDK-qualified");
    }

    [TestMethod]
    public async Task DecideProjectItem_RejectsUnmodeledActiveImport()
    {
        var (_, target) = await CreateDecisionMigrationAsync(
            "UnmodeledImportApp",
            includeConditionalItem: false);
        var items = await ReadReviewItemsAsync(target);
        await WriteAsync(
            target,
            "Directory.Build.targets",
            """
            <Project>
              <ItemGroup>
                <PRIResource Include="Resources\fr-fr\Resources.resw">
                  <SubType>Designer</SubType>
                </PRIResource>
              </ItemGroup>
            </Project>
            """);
        await AppendTargetProjectXmlAsync(
            target,
            """<Import Project="$(CustomTargetsPath)" />""");

        var result = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\fr-fr\Resources.resw"),
            "--strategy", "explicit-target-item",
            "--target-path", @"Resources\fr-fr\Resources.resw",
            "--target-item-type", "PRIResource",
            "--evidence-file", "Directory.Build.targets",
            "--rationale", "An active property-based import can change effective item coverage.");

        Assert.AreEqual(1, result.ExitCode, result.Output);
        StringAssert.Contains(result.Output, "not a contained literal import");
    }

    [TestMethod]
    public async Task DecideProjectItem_RejectsNearestDirectoryBuildFileOutsideTarget()
    {
        var (_, target) = await CreateDecisionMigrationAsync(
            "OutsideDirectoryBuildApp",
            includeConditionalItem: false);
        var items = await ReadReviewItemsAsync(target);
        await WriteAsync(
            _tempDirectory,
            "Directory.Build.props",
            """
            <Project>
              <PropertyGroup>
                <EnableDefaultItems>false</EnableDefaultItems>
              </PropertyGroup>
            </Project>
            """);
        await WriteAsync(
            target,
            "Directory.Build.targets",
            """
            <Project>
              <ItemGroup>
                <PRIResource Update="Resources\en-us\Resources.resw">
                  <SubType>Designer</SubType>
                </PRIResource>
              </ItemGroup>
            </Project>
            """);

        var result = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\en-us\Resources.resw"),
            "--strategy", "sdk-default-item",
            "--target-path", @"Resources\en-us\Resources.resw",
            "--evidence-file", "Directory.Build.targets",
            "--rationale", "An outside Directory.Build.props makes contained default coverage incomplete.");

        Assert.AreEqual(1, result.ExitCode, result.Output);
        StringAssert.Contains(result.Output, "nearest Directory.Build.props");
        StringAssert.Contains(result.Output, "outside the migration target");
    }

    [TestMethod]
    public async Task DecideProjectItem_AcceptsReachableLiteralImport()
    {
        var (_, target) = await CreateDecisionMigrationAsync(
            "LiteralImportEvidenceApp",
            includeConditionalItem: false);
        var items = await ReadReviewItemsAsync(target);
        await WriteAsync(
            target,
            "Build\\Items.targets",
            """
            <Project>
              <ItemGroup>
                <PRIResource Include="Resources\fr-fr\Resources.resw">
                  <SubType>Designer</SubType>
                </PRIResource>
              </ItemGroup>
            </Project>
            """);
        await AppendTargetProjectXmlAsync(
            target,
            """<Import Project="Build\Items.targets" />""");

        var result = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\fr-fr\Resources.resw"),
            "--strategy", "explicit-target-item",
            "--target-path", @"Resources\fr-fr\Resources.resw",
            "--target-item-type", "PRIResource",
            "--evidence-file", "Build\\Items.targets",
            "--rationale", "The literal import is reachable from the target project.");

        Assert.AreEqual(0, result.ExitCode, result.Output);
        using var report = await ReadReportAsync(target);
        var decision = report.RootElement
            .GetProperty("projectItemDecisions")
            .EnumerateArray()
            .Single();
        Assert.AreEqual(
            "verified",
            decision.GetProperty("verification").GetProperty("status").GetString());
        Assert.AreEqual(
            "Build/Items.targets",
            decision.GetProperty("verification")
                .GetProperty("projectEvidence")[0]
                .GetProperty("path")
                .GetString());
    }

    [TestMethod]
    public async Task DecideProjectItem_UpdateCannotReplaceDisabledSdkDefaultInclusion()
    {
        var (_, target) = await CreateDecisionMigrationAsync(
            "DisabledDefaultItemsApp",
            includeConditionalItem: false);
        var items = await ReadReviewItemsAsync(target);
        await WriteAsync(
            target,
            "Directory.Build.targets",
            """
            <Project>
              <ItemGroup>
                <PRIResource Update="Resources\en-us\Resources.resw">
                  <SubType>Designer</SubType>
                </PRIResource>
              </ItemGroup>
            </Project>
            """);
        await AppendTargetProjectXmlAsync(
            target,
            """
              <PropertyGroup>
                <enabledefaultitems>false</enabledefaultitems>
              </PropertyGroup>
            """);

        var result = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\en-us\Resources.resw"),
            "--strategy", "sdk-default-item",
            "--target-path", @"Resources\en-us\Resources.resw",
            "--evidence-file", "Directory.Build.targets",
            "--rationale", "Update metadata cannot create an item when SDK defaults are disabled.");

        Assert.AreEqual(1, result.ExitCode, result.Output);
        StringAssert.Contains(result.Output, "EnableDefaultItems");
    }

    [TestMethod]
    public async Task DecideProjectItem_DirectoryBuildTargetsCannotEstablishSdkDefaults()
    {
        var (_, target) = await CreateDecisionMigrationAsync(
            "LateUseWinUiApp",
            includeConditionalItem: false);
        var items = await ReadReviewItemsAsync(target);
        var project = Directory.EnumerateFiles(
            target.FullName,
            "*.csproj",
            SearchOption.TopDirectoryOnly)
            .Single();
        var projectText = await File.ReadAllTextAsync(
            project,
            TestContext.CancellationToken);
        projectText = projectText.Replace(
            "<UseWinUI>true</UseWinUI>",
            string.Empty,
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            project,
            projectText,
            TestContext.CancellationToken);
        await WriteAsync(
            target,
            "Directory.Build.targets",
            """
            <Project>
              <PropertyGroup>
                <UseWinUI>true</UseWinUI>
              </PropertyGroup>
              <ItemGroup>
                <PRIResource Update="Resources\en-us\Resources.resw">
                  <SubType>Designer</SubType>
                </PRIResource>
              </ItemGroup>
            </Project>
            """);

        var result = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\en-us\Resources.resw"),
            "--strategy", "sdk-default-item",
            "--target-path", @"Resources\en-us\Resources.resw",
            "--evidence-file", "Directory.Build.targets",
            "--rationale", "A late targets property cannot prove SDK default inclusion.");

        Assert.AreEqual(1, result.ExitCode, result.Output);
        StringAssert.Contains(result.Output, "does not deterministically enable UseWinUI");
    }

    [TestMethod]
    public async Task DecideProjectItem_ExplicitStrategyRejectsUpdateWithoutInclude()
    {
        var (_, target) = await CreateDecisionMigrationAsync(
            "UpdateOnlyEvidenceApp",
            includeConditionalItem: false);
        var items = await ReadReviewItemsAsync(target);
        await WriteAsync(
            target,
            "Directory.Build.targets",
            """
            <Project>
              <ItemGroup>
                <PRIResource Update="Resources\fr-fr\Resources.resw">
                  <SubType>Designer</SubType>
                </PRIResource>
              </ItemGroup>
            </Project>
            """);

        var result = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\fr-fr\Resources.resw"),
            "--strategy", "explicit-target-item",
            "--target-path", @"Resources\fr-fr\Resources.resw",
            "--target-item-type", "PRIResource",
            "--evidence-file", "Directory.Build.targets",
            "--rationale", "Update metadata alone must not count as target inclusion.");

        Assert.AreEqual(1, result.ExitCode, result.Output);
        StringAssert.Contains(result.Output, "Update does not include");
    }

    [TestMethod]
    public async Task DecideProjectItem_AbsoluteExternalItemsRequireMatchingContent()
    {
        var (source, target, externalContent, externalResource) =
            await CreateAbsoluteSourceMigrationAsync("AbsoluteEvidenceApp");
        var items = await ReadReviewItemsAsync(target);
        await AddAbsoluteTargetEvidenceAsync(
            target,
            externalContent,
            externalResource,
            resourceMatches: true);

        var explicitContent = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, externalContent),
            "--strategy", "explicit-target-item",
            "--target-path", @"Absolute\Content.json",
            "--target-item-type", "Content",
            "--evidence-file", "Directory.Build.targets",
            "--rationale", "An Include alone cannot prove an external absolute source copy.");
        var copiedContent = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, externalContent),
            "--strategy", "copied-linked-content",
            "--target-path", @"Absolute\Content.json",
            "--target-item-type", "Content",
            "--evidence-file", "Directory.Build.targets",
            "--rationale", "The absolute Content source and target have matching bytes.");
        var copiedResource = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, externalResource),
            "--strategy", "copied-linked-content",
            "--target-path", @"Absolute\Resources.resw",
            "--target-item-type", "PRIResource",
            "--evidence-file", "Directory.Build.targets",
            "--rationale", "The absolute PRIResource source and target have matching bytes.");

        Assert.AreEqual(1, explicitContent.ExitCode, explicitContent.Output);
        StringAssert.Contains(explicitContent.Output, "absolute or external");
        Assert.AreEqual(0, copiedContent.ExitCode, copiedContent.Output);
        Assert.AreEqual(0, copiedResource.ExitCode, copiedResource.Output);
        using var report = await ReadReportAsync(target);
        Assert.IsTrue(report.RootElement
            .GetProperty("projectItemDecisions")
            .EnumerateArray()
            .All(decision =>
                decision.GetProperty("verification").GetProperty("contentMatches").GetBoolean()));
        Assert.IsTrue(Directory.Exists(source.FullName));
    }

    [TestMethod]
    public async Task DecideProjectItem_AbsolutePathInsideSourceStillRequiresHashComparison()
    {
        var source = _tempDirectory.CreateSubdirectory("AbsoluteInsideSourceApp");
        var sourceContent = Path.Combine(
            source.FullName,
            "Data",
            "Inside.json");
        await WriteAsync(source, "Data\\Inside.json", """{"inside":true}""");
        await WriteAsync(
            source,
            "AbsoluteInsideSourceApp.csproj",
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Content Include="{{sourceContent}}">
                  <Link>Data\Inside.json</Link>
                </Content>
              </ItemGroup>
            </Project>
            """);
        await WriteAsync(source, "Package.appxmanifest", SourceManifest);
        var target = new DirectoryInfo(Path.Combine(
            _tempDirectory.FullName,
            "AbsoluteInsideSourceApp-output"));
        ArrangeTemplateCreation(target, "AbsoluteInsideSourceAppApp");
        var (migrateExit, migrateOutput) = await InvokeMigrateAsync(source, target);
        Assert.AreEqual(0, migrateExit, migrateOutput);
        await WriteAsync(
            target,
            "Directory.Build.targets",
            """
            <Project>
              <ItemGroup>
                <Content Include="Data\Inside.json" />
              </ItemGroup>
            </Project>
            """);
        var items = await ReadReviewItemsAsync(target);

        var explicitItem = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, sourceContent),
            "--strategy", "explicit-target-item",
            "--target-path", @"Data\Inside.json",
            "--target-item-type", "Content",
            "--evidence-file", "Directory.Build.targets",
            "--rationale", "An absolute Include requires content verification even within the source root.");
        var copiedItem = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, sourceContent),
            "--strategy", "copied-linked-content",
            "--target-path", @"Data\Inside.json",
            "--target-item-type", "Content",
            "--evidence-file", "Directory.Build.targets",
            "--rationale", "The contained absolute source and target bytes match.");

        Assert.AreEqual(1, explicitItem.ExitCode, explicitItem.Output);
        StringAssert.Contains(explicitItem.Output, "absolute or external");
        Assert.AreEqual(0, copiedItem.ExitCode, copiedItem.Output);
    }

    [TestMethod]
    public async Task DecideProjectItem_AbsoluteExternalContentMismatchIsRejected()
    {
        var (_, target, _, externalResource) =
            await CreateAbsoluteSourceMigrationAsync("AbsoluteMismatchApp");
        var items = await ReadReviewItemsAsync(target);
        await AddAbsoluteTargetEvidenceAsync(
            target,
            externalContent: null,
            externalResource: externalResource,
            resourceMatches: false);

        var result = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, externalResource),
            "--strategy", "copied-linked-content",
            "--target-path", @"Absolute\Resources.resw",
            "--target-item-type", "PRIResource",
            "--evidence-file", "Directory.Build.targets",
            "--rationale", "Mismatched external content must not be accepted.");

        Assert.AreEqual(1, result.ExitCode, result.Output);
        StringAssert.Contains(result.Output, "does not match");
    }

    [TestMethod]
    public async Task DecideProjectItem_UnreadableTargetContentReturnsInvalidDecision()
    {
        var (_, target, externalContent, externalResource) =
            await CreateAbsoluteSourceMigrationAsync("UnreadableTargetContentApp");
        var items = await ReadReviewItemsAsync(target);
        await AddAbsoluteTargetEvidenceAsync(
            target,
            externalContent,
            externalResource,
            resourceMatches: true);
        var targetContent = Path.Combine(
            target.FullName,
            "Absolute",
            "Content.json");
        await using var locked = new FileStream(
            targetContent,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        var result = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, externalContent),
            "--strategy", "copied-linked-content",
            "--target-path", @"Absolute\Content.json",
            "--target-item-type", "Content",
            "--evidence-file", "Directory.Build.targets",
            "--rationale", "Unreadable content must produce an invalid decision rather than a crash.");

        Assert.AreEqual(1, result.ExitCode, result.Output);
        StringAssert.Contains(result.Output, "Target content could not be hashed");
    }

    [TestMethod]
    public async Task Verify_RejectsRootedTraversalAndPrefixEscapeProjectPaths()
    {
        var (_, target) = await CreateDecisionMigrationAsync(
            "ReportPathVerifyApp",
            includeConditionalItem: false);
        var reportPath = Path.Combine(target.FullName, "migration-report.json");
        var report = JsonNode.Parse(await File.ReadAllTextAsync(
            reportPath,
            TestContext.CancellationToken))!;
        var originalSourceProject =
            report["source"]!["projectFile"]!.GetValue<string>();
        var originalTargetProject =
            report["target"]!["projectFile"]!.GetValue<string>();
        var outside = _tempDirectory.CreateSubdirectory(
            $"{target.Name}-prefix-escape");
        await WriteAsync(outside, "Outside.csproj", CleanCsproj);

        foreach (var mutation in new[]
        {
            (Section: "source", Value: @"nested/..\..\Outside.csproj"),
            (Section: "source", Value: @"...\Outside.csproj"),
            (Section: "target", Value: Path.Combine(target.FullName, originalTargetProject)),
            (Section: "target", Value: $@"..\{outside.Name}\Outside.csproj")
        })
        {
            report["source"]!["projectFile"] = originalSourceProject;
            report["target"]!["projectFile"] = originalTargetProject;
            report[mutation.Section]!["projectFile"] = mutation.Value;
            await File.WriteAllTextAsync(
                reportPath,
                report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                TestContext.CancellationToken);
            var before = await File.ReadAllTextAsync(
                reportPath,
                TestContext.CancellationToken);

            var (exit, output) = await InvokeVerifyAsync(target);

            Assert.AreEqual(1, exit, output);
            StringAssert.Contains(output, "project path recorded");
            Assert.AreEqual(
                before,
                await File.ReadAllTextAsync(
                    reportPath,
                    TestContext.CancellationToken));
        }
    }

    [TestMethod]
    public async Task DecideProjectItem_RejectsEscapingReportProjectPathBeforeEvidence()
    {
        var (_, target) = await CreateDecisionMigrationAsync(
            "ReportPathDecisionApp",
            includeConditionalItem: false);
        var items = await ReadReviewItemsAsync(target);
        var reportPath = Path.Combine(target.FullName, "migration-report.json");
        var report = JsonNode.Parse(await File.ReadAllTextAsync(
            reportPath,
            TestContext.CancellationToken))!;
        var outside = _tempDirectory.CreateSubdirectory("outside-source-project");
        await WriteAsync(outside, "Outside.csproj", "<not-msbuild />");
        report["source"]!["projectFile"] =
            Path.Combine(outside.FullName, "Outside.csproj");
        await File.WriteAllTextAsync(
            reportPath,
            report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            TestContext.CancellationToken);
        var before = await File.ReadAllTextAsync(
            reportPath,
            TestContext.CancellationToken);

        var result = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\en-us\Resources.resw"),
            "--strategy", "sdk-default-item",
            "--target-path", @"Resources\en-us\Resources.resw",
            "--rationale", "The report path must be rejected before outside XML is read.");

        Assert.AreEqual(1, result.ExitCode, result.Output);
        StringAssert.Contains(result.Output, "Source project path recorded");
        Assert.AreEqual(
            before,
            await File.ReadAllTextAsync(
                reportPath,
                TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Verify_RejectsReparsePointSourceRootBeforeProjectRead()
    {
        var (_, target) = await CreateDecisionMigrationAsync(
            "ReparseRootReportApp",
            includeConditionalItem: false);
        var physicalRoot = _tempDirectory.CreateSubdirectory(
            "reparse-physical-source");
        await WriteAsync(physicalRoot, "Outside.csproj", "<not-msbuild />");
        var linkPath = Path.Combine(
            _tempDirectory.FullName,
            "reparse-source-link");
        try
        {
            Directory.CreateSymbolicLink(
                linkPath,
                physicalRoot.FullName);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
            or IOException
            or PlatformNotSupportedException)
        {
            Assert.Inconclusive(
                $"The host cannot create a directory symbolic link: {exception.Message}");
            return;
        }

        var reportPath = Path.Combine(target.FullName, "migration-report.json");
        var report = JsonNode.Parse(await File.ReadAllTextAsync(
            reportPath,
            TestContext.CancellationToken))!;
        report["source"]!["root"] = linkPath;
        report["source"]!["projectFile"] = "Outside.csproj";
        await File.WriteAllTextAsync(
            reportPath,
            report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            TestContext.CancellationToken);
        var before = await File.ReadAllTextAsync(
            reportPath,
            TestContext.CancellationToken);

        var (exit, output) = await InvokeVerifyAsync(target);

        Assert.AreEqual(1, exit, output);
        StringAssert.Contains(output, "root");
        StringAssert.Contains(output, "reparse point");
        Assert.AreEqual(
            before,
            await File.ReadAllTextAsync(
                reportPath,
                TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task DecideProjectItem_PreservesUnrelatedReportFields()
    {
        var (source, target) = await CreateDecisionMigrationAsync(
            "DecisionPreservationApp",
            includeConditionalItem: false);
        await AddTargetEvidenceAsync(source, target);
        var reportPath = Path.Combine(target.FullName, "migration-report.json");
        var reportNode = JsonNode.Parse(await File.ReadAllTextAsync(
            reportPath,
            TestContext.CancellationToken))!;
        reportNode["validation"]!["sourceBaseline"]!["status"] = "captured";
        reportNode["dependencyAnalysis"]!["status"] = "resolved";
        await File.WriteAllTextAsync(
            reportPath,
            reportNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            TestContext.CancellationToken);
        var items = await ReadReviewItemsAsync(target);

        var result = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\en-us\Resources.resw"),
            "--strategy", "sdk-default-item",
            "--target-path", @"Resources\en-us\Resources.resw",
            "--evidence-file", "Directory.Build.targets",
            "--rationale", "Default PRI coverage with metadata.");

        Assert.AreEqual(0, result.ExitCode, result.Output);
        using var report = await ReadReportAsync(target);
        Assert.AreEqual(
            "captured",
            report.RootElement
                .GetProperty("validation")
                .GetProperty("sourceBaseline")
                .GetProperty("status")
                .GetString());
        Assert.AreEqual(
            "resolved",
            report.RootElement
                .GetProperty("dependencyAnalysis")
                .GetProperty("status")
                .GetString());
        var activation = report.RootElement.GetProperty("activationAnalysis");
        Assert.AreEqual("passed", activation.GetProperty("status").GetString());
        Assert.AreEqual(
            "decision-test",
            activation.GetProperty("contracts")[0].GetProperty("protocolName").GetString());
        Assert.AreEqual("1.3", report.RootElement.GetProperty("schemaVersion").GetString());
    }

    private async Task<(DirectoryInfo Source, DirectoryInfo Target)> CreateDecisionMigrationAsync(
        string name,
        bool includeConditionalItem)
    {
        var source = _tempDirectory.CreateSubdirectory(name);
        var shared = _tempDirectory.CreateSubdirectory($"{name}-shared");
        await WriteAsync(shared, "NOTICE.json", """{"license":"sample"}""");
        var conditionalItem = includeConditionalItem
            ? """
                <Content Include="Data\**\*.json" Condition="'$(Configuration)' == 'Debug'" />
              """
            : string.Empty;
        await WriteAsync(
            source,
            $"{name}.csproj",
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PRIResource Include="Resources\en-us\Resources.resw">
                  <SubType>Designer</SubType>
                </PRIResource>
                <PRIResource Include="Resources\fr-fr\Resources.resw">
                  <SubType>Designer</SubType>
                </PRIResource>
                <Content Include="..\{{shared.Name}}\NOTICE.json">
                  <Link>Strings\NOTICE.json</Link>
                </Content>
            {{conditionalItem}}
              </ItemGroup>
            </Project>
            """);
        await WriteAsync(source, @"Resources\en-us\Resources.resw", "<root />");
        await WriteAsync(source, @"Resources\fr-fr\Resources.resw", "<root />");
        await WriteAsync(source, @"Data\Model.json", """{"value":1}""");
        await WriteAsync(source, "Package.appxmanifest", SourceManifest);

        var target = new DirectoryInfo(Path.Combine(
            _tempDirectory.FullName,
            $"{name}-output"));
        ArrangeTemplateCreation(target, $"{name}App");
        var (exit, output) = await InvokeMigrateAsync(source, target);
        Assert.AreEqual(0, exit, output);
        return (source, target);
    }

    private async Task AddTargetEvidenceAsync(
        DirectoryInfo source,
        DirectoryInfo target)
    {
        var targetProjectName = Path.GetFileNameWithoutExtension(
            Directory.EnumerateFiles(
                target.FullName,
                "*.csproj",
                SearchOption.TopDirectoryOnly)
                .Single());
        await WriteAsync(
            target,
            "Directory.Build.targets",
            $$"""
            <Project>
              <ItemGroup Condition="'$(MSBuildProjectName)' == '{{targetProjectName}}'">
                <PRIResource Update="Resources\en-us\Resources.resw">
                  <SubType>Designer</SubType>
                </PRIResource>
                <PRIResource Include="Resources\fr-fr\Resources.resw">
                  <SubType>Designer</SubType>
                </PRIResource>
                <Content Include="Strings\NOTICE.json" />
              </ItemGroup>
            </Project>
            """);
        var sourceNotice = Path.Combine(
            source.Parent!.FullName,
            $"{source.Name}-shared",
            "NOTICE.json");
        var targetNotice = Path.Combine(target.FullName, "Strings", "NOTICE.json");
        Directory.CreateDirectory(Path.GetDirectoryName(targetNotice)!);
        File.Copy(sourceNotice, targetNotice, overwrite: true);
    }

    private async Task<(
        DirectoryInfo Source,
        DirectoryInfo Target,
        string ExternalContent,
        string ExternalResource)> CreateAbsoluteSourceMigrationAsync(
            string name)
    {
        var source = _tempDirectory.CreateSubdirectory(name);
        var external = _tempDirectory.CreateSubdirectory($"{name}-absolute");
        var externalContent = Path.Combine(external.FullName, "Content.json");
        var externalResource = Path.Combine(external.FullName, "Resources.resw");
        await File.WriteAllTextAsync(
            externalContent,
            """{"absolute":true}""",
            TestContext.CancellationToken);
        await File.WriteAllTextAsync(
            externalResource,
            "<root><data name=\"Absolute\"><value>Source</value></data></root>",
            TestContext.CancellationToken);
        await WriteAsync(
            source,
            $"{name}.csproj",
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Content Include="{{externalContent}}">
                  <Link>Absolute\Content.json</Link>
                </Content>
                <PRIResource Include="{{externalResource}}">
                  <Link>Absolute\Resources.resw</Link>
                </PRIResource>
              </ItemGroup>
            </Project>
            """);
        await WriteAsync(source, "Package.appxmanifest", SourceManifest);

        var target = new DirectoryInfo(Path.Combine(
            _tempDirectory.FullName,
            $"{name}-output"));
        ArrangeTemplateCreation(target, $"{name}App");
        var (exit, output) = await InvokeMigrateAsync(source, target);
        Assert.AreEqual(0, exit, output);
        return (source, target, externalContent, externalResource);
    }

    private async Task AddAbsoluteTargetEvidenceAsync(
        DirectoryInfo target,
        string? externalContent,
        string externalResource,
        bool resourceMatches)
    {
        await WriteAsync(
            target,
            "Directory.Build.targets",
            """
            <Project>
              <ItemGroup>
                <Content Include="Absolute\Content.json" />
                <PRIResource Include="Absolute\Resources.resw" />
              </ItemGroup>
            </Project>
            """);
        if (externalContent is not null)
        {
            var contentTarget = Path.Combine(
                target.FullName,
                "Absolute",
                "Content.json");
            Directory.CreateDirectory(Path.GetDirectoryName(contentTarget)!);
            File.Copy(externalContent, contentTarget, overwrite: true);
        }

        var resourceTarget = Path.Combine(
            target.FullName,
            "Absolute",
            "Resources.resw");
        Directory.CreateDirectory(Path.GetDirectoryName(resourceTarget)!);
        if (resourceMatches)
        {
            File.Copy(externalResource, resourceTarget, overwrite: true);
        }
        else
        {
            await File.WriteAllTextAsync(
                resourceTarget,
                "<root><data name=\"Absolute\"><value>Target</value></data></root>",
                TestContext.CancellationToken);
        }
    }

    private async Task AppendTargetProjectXmlAsync(
        DirectoryInfo target,
        string xml)
    {
        var project = Directory.EnumerateFiles(
            target.FullName,
            "*.csproj",
            SearchOption.TopDirectoryOnly)
            .Single();
        var content = await File.ReadAllTextAsync(
            project,
            TestContext.CancellationToken);
        content = content.Replace(
            "</Project>",
            $"{xml}{Environment.NewLine}</Project>",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            project,
            content,
            TestContext.CancellationToken);
    }

    private async Task<JsonArray> ReadReviewItemsAsync(DirectoryInfo target)
    {
        var report = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"),
            TestContext.CancellationToken))!;
        return report["mechanicalVerification"]!["projectItems"]!["reviewRequiredItems"]!.AsArray();
    }

    private static string ItemId(JsonArray items, string include) =>
        items.Single(item =>
                item!["include"]!.GetValue<string>() == include)!["id"]!
            .GetValue<string>();

    private static string ItemIdByLink(JsonArray items, string link) =>
        items.Single(item =>
                item!["link"]?.GetValue<string>() == link)!["id"]!
            .GetValue<string>();

    private static JsonElement Decision(
        IEnumerable<JsonElement> decisions,
        string strategy) =>
        decisions.Single(decision =>
            decision.GetProperty("strategy").GetString() == strategy);

    private async Task<(int ExitCode, string Output)> InvokeMigrateAsync(
        DirectoryInfo source,
        DirectoryInfo target) =>
        await InvokeCapturingConsoleAsync(
            GetRequiredService<MigrateCommand>(),
            source.FullName,
            "--output",
            target.FullName);

    private Task<(int ExitCode, string Output)> InvokeVerifyAsync(
        DirectoryInfo target) =>
        InvokeCapturingConsoleAsync(
            GetRequiredService<MigrateVerifyCommand>(),
            target.FullName);

    private Task<(int ExitCode, string Output)> InvokeDecisionAsync(
        DirectoryInfo target,
        params string[] arguments)
    {
        var remaining = arguments.ToList();
        var item = ExtractRequiredTestArgument(remaining, "--item");
        var strategy = ExtractRequiredTestArgument(remaining, "--strategy");
        var rationale = ExtractRequiredTestArgument(remaining, "--rationale");
        var commandArguments = new List<string>
        {
            target.FullName,
            item,
            strategy,
            rationale
        };
        commandArguments.AddRange(remaining);
        return InvokeCapturingConsoleAsync(
            GetRequiredService<MigrateProjectItemDecisionCommand>(),
            [.. commandArguments]);
    }

    private static string ExtractRequiredTestArgument(
        List<string> arguments,
        string name)
    {
        var index = arguments.IndexOf(name);
        Assert.IsTrue(index >= 0 && index + 1 < arguments.Count);
        var value = arguments[index + 1];
        arguments.RemoveRange(index, 2);
        return value;
    }

    private async Task<JsonDocument> ReadReportAsync(DirectoryInfo target) =>
        JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"),
            TestContext.CancellationToken));

    private void ArrangeTemplateCreation(
        DirectoryInfo target,
        string projectName)
    {
        FakeDotNet.RunDotnetCommandHandler = arguments =>
        {
            var output = GetTemplateOutput(arguments);
            output.Create();
            File.WriteAllText(
                Path.Combine(output.FullName, $"{projectName}.csproj"),
                CleanCsproj);
            File.WriteAllText(
                Path.Combine(output.FullName, "App.xaml"),
                $"<Application x:Class=\"{projectName}.App\" />");
            File.WriteAllText(
                Path.Combine(output.FullName, "App.xaml.cs"),
                $"namespace {projectName}; public partial class App {{ }}");
            File.WriteAllText(
                Path.Combine(output.FullName, "MainWindow.xaml"),
                $"<Window x:Class=\"{projectName}.MainWindow\" />");
            File.WriteAllText(
                Path.Combine(output.FullName, "MainWindow.xaml.cs"),
                $"namespace {projectName}; public partial class MainWindow {{ }}");
            File.WriteAllText(
                Path.Combine(output.FullName, "Package.appxmanifest"),
                TargetManifest);
            return (0, "Template created.", string.Empty);
        };
    }

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

    private const string SourceManifest = """
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
          <Applications>
            <Application Id="App">
              <Extensions>
                <uap:Extension Category="windows.protocol">
                  <uap:Protocol Name="decision-test" />
                </uap:Extension>
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
