// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MigrateProjectItemDecisionTests : MigrateCommandTestBase
{
    [TestMethod]
    [TestCategory("MigrationAcceptance")]
    public async Task DecideProjectItem_AuthoritativeFactsCoverAllStrategies()
    {
        var (_, target) = await CreateDecisionMigrationAsync(
            "AllStrategiesApp",
            includeDispositionItems: true);
        var facts = await ArrangeTargetFactsAsync(target);
        var items = await ReadDecisionItemsAsync(target);

        var sdk = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\en-us\Resources.resw"),
            "--strategy", "sdk-default-item",
            "--target-path", @"Resources\en-us\Resources.resw",
            "--rationale", "The evaluated SDK PRIResource preserves the source metadata.");
        var explicitItem = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\fr-fr\Resources.resw"),
            "--strategy", "explicit-target-item",
            "--target-path", @"Resources\fr-fr\Resources.resw",
            "--target-item-type", "PRIResource",
            "--evidence-file", @"Build\Items.targets",
            "--rationale", "The evaluated explicit PRIResource preserves the source metadata.");
        var copied = await InvokeDecisionAsync(
            target,
            "--item", ItemIdByLink(items, @"Strings\NOTICE.json"),
            "--strategy", "copied-linked-content",
            "--target-path", @"Strings\NOTICE.json",
            "--target-item-type", "Content",
            "--evidence-file", @"Build\Items.targets",
            "--rationale", "The evaluated Content item has the copied source bytes.");
        var replacement = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Data\**\*.json"),
            "--strategy", "target-owned-replacement",
            "--target-path", @"Assets\Replacement.json",
            "--target-item-type", "Content",
            "--evidence-file", TargetProject(target),
            "--rationale", "The target owns a replacement for the conditional source data.");
        var notRequired = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Legacy\**\*.json"),
            "--strategy", "target-not-required",
            "--reason-code", "target-platform-not-applicable",
            "--rationale", "The target platform does not consume the legacy payload.");
        var intentionallyOmitted = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Discarded\**\*.json"),
            "--strategy", "intentionally-not-migrated",
            "--rationale", "The omission remains a review decision.");

        foreach (var result in new[]
        {
            sdk,
            explicitItem,
            copied,
            replacement,
            notRequired,
            intentionallyOmitted
        })
        {
            Assert.AreEqual(0, result.ExitCode, result.Output);
        }

        using var report = await ReadReportAsync(target);
        var decisions = report.RootElement
            .GetProperty("projectItemDecisions")
            .EnumerateArray()
            .ToList();
        Assert.AreEqual("verified", Decision(decisions, "sdk-default-item")
            .GetProperty("verification").GetProperty("status").GetString());
        Assert.AreEqual(0, Decision(decisions, "sdk-default-item")
            .GetProperty("verification").GetProperty("projectEvidence")
            .GetArrayLength(), "External SDK defining files must not be emitted.");
        Assert.AreEqual("verified", Decision(decisions, "explicit-target-item")
            .GetProperty("verification").GetProperty("status").GetString());
        Assert.AreEqual("Build/Items.targets", Decision(decisions, "explicit-target-item")
            .GetProperty("verification").GetProperty("projectEvidence")[0]
            .GetProperty("path").GetString());
        var copiedDecision = Decision(decisions, "copied-linked-content");
        Assert.AreEqual("verified", copiedDecision
            .GetProperty("verification").GetProperty("status").GetString());
        Assert.IsTrue(copiedDecision
            .GetProperty("verification").GetProperty("contentMatches").GetBoolean());
        Assert.AreEqual("verified", Decision(decisions, "target-owned-replacement")
            .GetProperty("verification").GetProperty("status").GetString());
        Assert.AreEqual("accepted", Decision(decisions, "target-not-required")
            .GetProperty("verification").GetProperty("status").GetString());
        Assert.AreEqual("review-required", Decision(decisions, "intentionally-not-migrated")
            .GetProperty("verification").GetProperty("status").GetString());
        var accountedItems = report.RootElement
            .GetProperty("mechanicalVerification")
            .GetProperty("projectItems")
            .GetProperty("accountedItems")
            .EnumerateArray()
            .ToList();
        Assert.IsFalse(AccountedItem(
                accountedItems,
                "Resources/en-us/Resources.resw")
            .GetProperty("requiresProjectEntry").GetBoolean());
        Assert.IsTrue(AccountedItem(
                accountedItems,
                "Resources/fr-fr/Resources.resw")
            .GetProperty("requiresProjectEntry").GetBoolean());
        Assert.IsTrue(AccountedItem(
                accountedItems,
                "Strings/NOTICE.json")
            .GetProperty("requiresProjectEntry").GetBoolean());
        Assert.IsTrue(AccountedItem(
                accountedItems,
                "Assets/Replacement.json")
            .GetProperty("requiresProjectEntry").GetBoolean());
        Assert.IsTrue(report.RootElement.GetProperty("todos").EnumerateArray().Any(todo =>
            todo.GetProperty("id").GetString() == "UWMIG012"));
        Assert.AreEqual(
            6,
            facts.EvaluationCount,
            "Each reconciliation command reuses one entry-project evaluation for decisions and portability.");
    }

    [TestMethod]
    [TestCategory("MigrationAcceptance")]
    public async Task DecideProjectItem_RequiresExpectedTargetPathAndEvaluatedKind()
    {
        var (_, target) = await CreateDecisionMigrationAsync("PathBindingApp");
        await ArrangeTargetFactsAsync(target);
        var items = await ReadDecisionItemsAsync(target);

        var wrongPath = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\fr-fr\Resources.resw"),
            "--strategy", "explicit-target-item",
            "--target-path", @"Resources\en-us\Resources.resw",
            "--target-item-type", "PRIResource",
            "--rationale", "An unrelated evaluated resource must not satisfy the decision.");
        var wrongKind = await InvokeDecisionAsync(
            target,
            "--item", ItemIdByLink(items, @"Strings\NOTICE.json"),
            "--strategy", "copied-linked-content",
            "--target-path", @"Strings\NOTICE.json",
            "--target-item-type", "PRIResource",
            "--rationale", "The evaluated kind must match the copied source item.");

        Assert.AreEqual(1, wrongPath.ExitCode, wrongPath.Output);
        StringAssert.Contains(wrongPath.Output, "expected target path");
        Assert.AreEqual(1, wrongKind.ExitCode, wrongKind.Output);
        StringAssert.Contains(wrongKind.Output, "does not own");
    }

    [TestMethod]
    [TestCategory("MigrationAcceptance")]
    public async Task DecideProjectItem_MetadataMustMatchEvaluatedMetadata()
    {
        var (_, target) = await CreateDecisionMigrationAsync("MetadataMatchApp");
        await ArrangeTargetFactsAsync(target, metadataMatches: false);
        var items = await ReadDecisionItemsAsync(target);

        var mismatch = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\fr-fr\Resources.resw"),
            "--strategy", "explicit-target-item",
            "--target-path", @"Resources\fr-fr\Resources.resw",
            "--target-item-type", "PRIResource",
            "--rationale", "The source metadata must be present in evaluated target facts.");

        Assert.AreEqual(1, mismatch.ExitCode, mismatch.Output);
        StringAssert.Contains(mismatch.Output, "metadata 'SubType'");

        await ArrangeTargetFactsAsync(target);
        var matched = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\fr-fr\Resources.resw"),
            "--strategy", "explicit-target-item",
            "--target-path", @"Resources\fr-fr\Resources.resw",
            "--target-item-type", "PRIResource",
            "--rationale", "The evaluated target metadata matches the source.");
        Assert.AreEqual(0, matched.ExitCode, matched.Output);
    }

    [TestMethod]
    [TestCategory("MigrationAcceptance")]
    public async Task DecideProjectItem_CopiedContentRequiresMatchingHash()
    {
        var (_, target) = await CreateDecisionMigrationAsync("HashMatchApp");
        await ArrangeTargetFactsAsync(target);
        var items = await ReadDecisionItemsAsync(target);
        await File.WriteAllTextAsync(
            Path.Combine(target.FullName, "Strings", "NOTICE.json"),
            """{"license":"changed"}""",
            TestContext.CancellationToken);

        var mismatch = await InvokeDecisionAsync(
            target,
            "--item", ItemIdByLink(items, @"Strings\NOTICE.json"),
            "--strategy", "copied-linked-content",
            "--target-path", @"Strings\NOTICE.json",
            "--target-item-type", "Content",
            "--rationale", "Only byte-identical linked content is accounted.");

        Assert.AreEqual(1, mismatch.ExitCode, mismatch.Output);
        StringAssert.Contains(mismatch.Output, "does not match");
    }

    [TestMethod]
    [TestCategory("MigrationAcceptance")]
    public async Task DecideProjectItem_MsBuildExpressionCannotBeHashVerified()
    {
        var (_, target) = await CreateDecisionMigrationAsync(
            "ExpressionSourceApp",
            includeExpressionItem: true);
        var facts = await ArrangeTargetFactsAsync(target);
        facts.IncludeExpressionItem = true;
        await WriteAsync(
            target,
            @"Strings\Expression.json",
            """{"source":"expression"}""");
        var items = await ReadDecisionItemsAsync(target);

        var result = await InvokeDecisionAsync(
            target,
            "--item", ItemIdByLink(items, @"Strings\Expression.json"),
            "--strategy", "copied-linked-content",
            "--target-path", @"Strings\Expression.json",
            "--target-item-type", "Content",
            "--rationale",
            "MSBuild expressions are unavailable for source byte verification.");

        Assert.AreEqual(1, result.ExitCode, result.Output);
        StringAssert.Contains(
            result.Output,
            "unavailable for byte-hash verification");
    }

    [TestMethod]
    [TestCategory("MigrationAcceptance")]
    public async Task DecideProjectItem_CopiedContentRequiresMatchingMetadata()
    {
        var (_, target) = await CreateDecisionMigrationAsync(
            "CopiedMetadataMatchApp");
        await ArrangeTargetFactsAsync(target, metadataMatches: false);
        var items = await ReadDecisionItemsAsync(target);

        var mismatch = await InvokeDecisionAsync(
            target,
            "--item", ItemIdByLink(items, @"Strings\NOTICE.json"),
            "--strategy", "copied-linked-content",
            "--target-path", @"Strings\NOTICE.json",
            "--target-item-type", "Content",
            "--rationale", "Copied content must preserve evaluated packaging metadata.");

        Assert.AreEqual(1, mismatch.ExitCode, mismatch.Output);
        StringAssert.Contains(
            mismatch.Output,
            "metadata 'CopyToOutputDirectory'");
    }

    [TestMethod]
    [TestCategory("MigrationAcceptance")]
    public async Task DecideProjectItem_EvidenceFileMustBeActualDefiningProject()
    {
        var (_, target) = await CreateDecisionMigrationAsync("EvidenceBindingApp");
        await ArrangeTargetFactsAsync(target);
        await WriteAsync(target, @"Build\Unrelated.targets", "<Project />");
        var externalEvidence = Path.Combine(
            target.Parent!.FullName,
            "ExternalEvidence.props");
        await File.WriteAllTextAsync(
            externalEvidence,
            "<Project />",
            TestContext.CancellationToken);
        var items = await ReadDecisionItemsAsync(target);

        var unrelated = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\fr-fr\Resources.resw"),
            "--strategy", "explicit-target-item",
            "--target-path", @"Resources\fr-fr\Resources.resw",
            "--target-item-type", "PRIResource",
            "--evidence-file", @"Build\Unrelated.targets",
            "--rationale", "Arbitrary target files cannot authenticate evaluated ownership.");
        var external = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\fr-fr\Resources.resw"),
            "--strategy", "explicit-target-item",
            "--target-path", @"Resources\fr-fr\Resources.resw",
            "--target-item-type", "PRIResource",
            "--evidence-file", @"..\ExternalEvidence.props",
            "--rationale", "External files supplied by the caller are not trusted evidence.");

        Assert.AreEqual(1, unrelated.ExitCode, unrelated.Output);
        StringAssert.Contains(unrelated.Output, "not the evaluated defining project");
        Assert.AreEqual(1, external.ExitCode, external.Output);
        StringAssert.Contains(external.Output, "Evidence path is invalid");
    }

    [TestMethod]
    [TestCategory("MigrationAcceptance")]
    public async Task DecideProjectItem_StrategiesRequireAuthoritativeDefiningOrigin()
    {
        var (_, target) = await CreateDecisionMigrationAsync(
            "DefiningOriginApp");
        var items = await ReadDecisionItemsAsync(target);
        await ArrangeTargetFactsAsync(
            target,
            sdkOriginExternal: false);

        var containedSdk = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\en-us\Resources.resw"),
            "--strategy", "sdk-default-item",
            "--target-path", @"Resources\en-us\Resources.resw",
            "--rationale", "A contained definition is not an SDK default origin.");

        Assert.AreEqual(1, containedSdk.ExitCode, containedSdk.Output);
        StringAssert.Contains(
            containedSdk.Output,
            "Microsoft.NET.Sdk default-item targets");

        await ArrangeTargetFactsAsync(
            target,
            explicitOriginContained: false);
        var externalExplicit = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\fr-fr\Resources.resw"),
            "--strategy", "explicit-target-item",
            "--target-path", @"Resources\fr-fr\Resources.resw",
            "--target-item-type", "PRIResource",
            "--rationale", "Explicit target items require a contained defining project.");
        var externalCopied = await InvokeDecisionAsync(
            target,
            "--item", ItemIdByLink(items, @"Strings\NOTICE.json"),
            "--strategy", "copied-linked-content",
            "--target-path", @"Strings\NOTICE.json",
            "--target-item-type", "Content",
            "--rationale", "Copied linked content requires a contained defining project.");

        Assert.AreEqual(1, externalExplicit.ExitCode, externalExplicit.Output);
        StringAssert.Contains(externalExplicit.Output, "trusted build context");
        Assert.AreEqual(1, externalCopied.ExitCode, externalCopied.Output);
        StringAssert.Contains(externalCopied.Output, "trusted build context");
    }

    [TestMethod]
    [TestCategory("MigrationAcceptance")]
    public async Task Verify_LegacyEvidenceFilesNormalizeToAuthoritativeDefinition()
    {
        var (_, target) = await CreateDecisionMigrationAsync(
            "LegacyEvidenceCompatibilityApp");
        await ArrangeTargetFactsAsync(target);
        await WriteAsync(target, @"Build\Legacy.targets", "<Project />");
        var items = await ReadDecisionItemsAsync(target);
        var sdkRecorded = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\en-us\Resources.resw"),
            "--strategy", "sdk-default-item",
            "--target-path", @"Resources\en-us\Resources.resw",
            "--rationale", "Seed authoritative external SDK evidence.");
        var explicitRecorded = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\fr-fr\Resources.resw"),
            "--strategy", "explicit-target-item",
            "--target-path", @"Resources\fr-fr\Resources.resw",
            "--target-item-type", "PRIResource",
            "--evidence-file", @"Build\Items.targets",
            "--rationale", "Seed authoritative explicit target evidence.");
        Assert.AreEqual(0, sdkRecorded.ExitCode, sdkRecorded.Output);
        Assert.AreEqual(
            0,
            explicitRecorded.ExitCode,
            explicitRecorded.Output);

        var reportPath = Path.Combine(
            target.FullName,
            "migration-report.json");
        var report = JsonNode.Parse(await File.ReadAllTextAsync(
            reportPath,
            TestContext.CancellationToken))!;
        foreach (var decision in report["projectItemDecisions"]!.AsArray())
        {
            decision!["evidenceFiles"] =
                new JsonArray("Build/Legacy.targets");
        }
        await File.WriteAllTextAsync(
            reportPath,
            report.ToJsonString(),
            TestContext.CancellationToken);

        var (exit, output) = await InvokeVerifyAsync(target);

        Assert.AreEqual(0, exit, output);
        using var verified = await ReadReportAsync(target);
        var decisions = verified.RootElement
            .GetProperty("projectItemDecisions")
            .EnumerateArray()
            .ToList();
        var sdkDecision = Decision(decisions, "sdk-default-item");
        Assert.AreEqual(
            "verified",
            sdkDecision.GetProperty("verification")
                .GetProperty("status").GetString());
        Assert.AreEqual(
            0,
            sdkDecision.GetProperty("evidenceFiles")
                .GetArrayLength());
        var explicitDecision = Decision(
            decisions,
            "explicit-target-item");
        Assert.AreEqual(
            "verified",
            explicitDecision.GetProperty("verification")
                .GetProperty("status").GetString());
        var normalizedEvidence =
            explicitDecision.GetProperty("evidenceFiles");
        Assert.AreEqual(1, normalizedEvidence.GetArrayLength());
        Assert.AreEqual(
            "Build/Items.targets",
            normalizedEvidence[0].GetString());
    }

    [TestMethod]
    [TestCategory("MigrationAcceptance")]
    public async Task DecideAndVerify_CloseAndReopenUwmig012WithOneEvaluation()
    {
        var (_, target) = await CreateDecisionMigrationAsync("DecisionClosureApp");
        var facts = await ArrangeTargetFactsAsync(target);
        var items = await ReadDecisionItemsAsync(target);
        await RecordDeterministicDecisionsAsync(target, items);

        using (var closed = await ReadReportAsync(target))
        {
            Assert.IsFalse(closed.RootElement.GetProperty("todos").EnumerateArray().Any(todo =>
                todo.GetProperty("id").GetString() == "UWMIG012"));
        }

        facts.IncludeCopiedItem = false;
        var evaluationsBeforeVerify = facts.EvaluationCount;
        var (verifyExit, verifyOutput) = await InvokeVerifyAsync(target);

        Assert.AreEqual(0, verifyExit, verifyOutput);
        Assert.AreEqual(
            evaluationsBeforeVerify + 1,
            facts.EvaluationCount,
            "One authoritative evaluation must be reused for recorded decisions and portability.");
        using var reopened = await ReadReportAsync(target);
        Assert.IsTrue(reopened.RootElement.GetProperty("todos").EnumerateArray().Any(todo =>
            todo.GetProperty("id").GetString() == "UWMIG012"));
        Assert.IsTrue(reopened.RootElement
            .GetProperty("mechanicalVerification")
            .GetProperty("projectItems")
            .GetProperty("decisionRequiredItems")
            .EnumerateArray()
            .Any(item =>
                item.GetProperty("expectedTargetPath").GetString()
                == "Strings/NOTICE.json"));
        var copied = reopened.RootElement.GetProperty("projectItemDecisions")
            .EnumerateArray()
            .Single(decision =>
                decision.GetProperty("strategy").GetString()
                == "copied-linked-content");
        Assert.AreEqual(
            "invalid",
            copied.GetProperty("verification").GetProperty("status").GetString());
    }

    [TestMethod]
    [TestCategory("MigrationAcceptance")]
    public async Task Verify_StaleAndDuplicateDecisionsFailClosed()
    {
        var (_, target) = await CreateDecisionMigrationAsync("StaleDecisionApp");
        await ArrangeTargetFactsAsync(target);
        var items = await ReadDecisionItemsAsync(target);
        var result = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\fr-fr\Resources.resw"),
            "--strategy", "explicit-target-item",
            "--target-path", @"Resources\fr-fr\Resources.resw",
            "--target-item-type", "PRIResource",
            "--rationale", "Seed a valid decision.");
        Assert.AreEqual(0, result.ExitCode, result.Output);

        var reportPath = Path.Combine(target.FullName, "migration-report.json");
        var report = JsonNode.Parse(await File.ReadAllTextAsync(
            reportPath,
            TestContext.CancellationToken))!;
        var decisions = report["projectItemDecisions"]!.AsArray();
        decisions.Add(decisions[0]!.DeepClone());
        decisions.Add(new JsonObject
        {
            ["itemId"] = "project-item-stale",
            ["strategy"] = "target-not-required",
            ["reasonCode"] = "target-platform-not-applicable",
            ["rationale"] = "This source item no longer exists.",
            ["evidenceFiles"] = new JsonArray(),
            ["verification"] = new JsonObject
            {
                ["status"] = "accepted",
                ["projectEvidence"] = new JsonArray()
            }
        });
        await File.WriteAllTextAsync(
            reportPath,
            report.ToJsonString(),
            TestContext.CancellationToken);

        await InvokeVerifyAsync(target);

        using var verified = await ReadReportAsync(target);
        var statuses = verified.RootElement.GetProperty("projectItemDecisions")
            .EnumerateArray()
            .Select(decision =>
                decision.GetProperty("verification").GetProperty("status").GetString())
            .ToList();
        Assert.AreEqual(2, statuses.Count(status => status == "invalid"));
        Assert.AreEqual(1, statuses.Count(status => status == "stale"));
    }

    [TestMethod]
    [TestCategory("MigrationAcceptance")]
    public async Task DecideProjectItem_EvaluatorFailureFailsClosed()
    {
        var (_, target) = await CreateDecisionMigrationAsync("EvaluationFailureApp");
        FakeMsBuildEvaluator.Handler = (_, _) => Task.FromResult(
            MsBuildProjectItemEvaluation.Failure(
                "dotnet msbuild returned malformed item JSON."));
        var items = await ReadDecisionItemsAsync(target);

        var result = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\fr-fr\Resources.resw"),
            "--strategy", "explicit-target-item",
            "--target-path", @"Resources\fr-fr\Resources.resw",
            "--target-item-type", "PRIResource",
            "--rationale", "Evaluation failures cannot fall back to XML emulation.");

        Assert.AreEqual(1, result.ExitCode, result.Output);
        StringAssert.Contains(result.Output, "malformed item JSON");
        using var report = await ReadReportAsync(target);
        Assert.AreEqual(
            0,
            report.RootElement.GetProperty("projectItemDecisions").GetArrayLength());
    }

    [TestMethod]
    public async Task DecideProjectItem_UntrustworthyEvaluatedPathFailsClosed()
    {
        var (_, target) = await CreateDecisionMigrationAsync(
            "UntrustworthyEvaluationApp");
        var definingProject = Path.Combine(
            target.FullName,
            TargetProject(target));
        FakeMsBuildEvaluator.Handler = (_, _) => Task.FromResult(
            MsBuildProjectItemEvaluation.Success(
            [
                new MsBuildProjectItem(
                    "PRIResource",
                    @"Resources\fr-fr\Resources.resw",
                    @"Resources\fr-fr\Resources.resw",
                    definingProject,
                    new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase))
            ]));
        var items = await ReadDecisionItemsAsync(target);

        var result = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\fr-fr\Resources.resw"),
            "--strategy", "explicit-target-item",
            "--target-path", @"Resources\fr-fr\Resources.resw",
            "--target-item-type", "PRIResource",
            "--rationale", "Relative evaluated FullPath facts are not trustworthy.");

        Assert.AreEqual(1, result.ExitCode, result.Output);
        StringAssert.Contains(result.Output, "untrustworthy");
    }

    [TestMethod]
    [TestCategory("MigrationAcceptance")]
    public async Task TargetNotRequired_RejectsUnsupportedCurrentTargetProject()
    {
        var (_, target) = await CreateDecisionMigrationAsync(
            "UnsupportedTargetProjectApp",
            includeDispositionItems: true);
        var facts = await ArrangeTargetFactsAsync(target);
        var projectPath = Path.Combine(
            target.FullName,
            TargetProject(target));
        var project = await File.ReadAllTextAsync(
            projectPath,
            TestContext.CancellationToken);
        await File.WriteAllTextAsync(
            projectPath,
            project.Replace(
                "Microsoft.NET.Sdk",
                "Unsupported.Target.Sdk",
                StringComparison.Ordinal),
            TestContext.CancellationToken);
        var items = await ReadDecisionItemsAsync(target);

        var result = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Legacy\**\*.json"),
            "--strategy", "target-not-required",
            "--reason-code", "target-platform-not-applicable",
            "--rationale", "Accepted exclusions still require a supported current target.");

        Assert.AreEqual(1, result.ExitCode, result.Output);
        StringAssert.Contains(result.Output, "not a supported Microsoft.NET.Sdk");
        Assert.AreEqual(
            0,
            facts.EvaluationCount,
            "Unsupported projects must fail before invoking dotnet msbuild.");
    }

    [TestMethod]
    [TestCategory("MigrationAcceptance")]
    public async Task DecideProjectItem_CancellationStopsEvaluationAndWrite()
    {
        var (_, target) = await CreateDecisionMigrationAsync("CancellationApp");
        FakeMsBuildEvaluator.Handler = async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return MsBuildProjectItemEvaluation.Success([]);
        };
        var items = await ReadDecisionItemsAsync(target);
        var command = GetRequiredService<MigrateProjectItemDecisionCommand>();
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(100));

        var exit = await command.Parse(
            [
                target.FullName,
                ItemId(items, @"Resources\fr-fr\Resources.resw"),
                "explicit-target-item",
                "Cancellation must propagate through authoritative evaluation.",
                "--target-path", @"Resources\fr-fr\Resources.resw",
                "--target-item-type", "PRIResource"
            ]).InvokeAsync(cancellationToken: cancellation.Token);

        Assert.AreNotEqual(0, exit);
        using var report = await ReadReportAsync(target);
        Assert.AreEqual(
            0,
            report.RootElement.GetProperty("projectItemDecisions").GetArrayLength());
    }

    [TestMethod]
    [TestCategory("MigrationAcceptance")]
    public async Task DecideProjectItem_ExactIdThenUniquePrefixSelectionIsPreserved()
    {
        var (_, target) = await CreateDecisionMigrationAsync(
            "SelectorApp",
            includeDispositionItems: true);
        await ArrangeTargetFactsAsync(target);
        var items = await ReadDecisionItemsAsync(target);
        var exactId = ItemId(items, @"Legacy\**\*.json");
        var uniquePrefixLength = Enumerable.Range(1, exactId.Length)
            .First(length => items.Count(item =>
                item!["id"]!.GetValue<string>().StartsWith(
                    exactId[..length],
                    StringComparison.OrdinalIgnoreCase)) == 1);

        var exact = await InvokeDecisionAsync(
            target,
            "--item", exactId,
            "--strategy", "target-not-required",
            "--reason-code", "target-platform-not-applicable",
            "--rationale", "Exact IDs remain accepted.");
        var prefix = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Discarded\**\*.json")[..uniquePrefixLength],
            "--strategy", "intentionally-not-migrated",
            "--rationale", "Unique prefixes remain accepted.");

        Assert.AreEqual(0, exact.ExitCode, exact.Output);
        Assert.AreEqual(0, prefix.ExitCode, prefix.Output);
    }

    [TestMethod]
    [TestCategory("MigrationAcceptance")]
    public async Task DecideProjectItem_ConcurrentTransactionsPreserveUpdates()
    {
        var (_, target) = await CreateDecisionMigrationAsync("ConcurrentDecisionApp");
        await ArrangeTargetFactsAsync(target);
        var items = await ReadDecisionItemsAsync(target);
        var reportPath = Path.Combine(target.FullName, "migration-report.json");
        var heldLock = await MigrationReportStore.AcquireTransactionLockAsync(
            reportPath,
            TimeSpan.FromSeconds(1),
            TestContext.CancellationToken);
        Task<int> sdkTask = null!;
        Task<int> explicitTask = null!;
        try
        {
            var command = GetRequiredService<MigrateProjectItemDecisionCommand>();
            sdkTask = command.Parse(
            [
                target.FullName,
                ItemId(items, @"Resources\en-us\Resources.resw"),
                "sdk-default-item",
                "Concurrent SDK default decision.",
                "--target-path", @"Resources\en-us\Resources.resw"
            ]).InvokeAsync(cancellationToken: TestContext.CancellationToken);
            explicitTask = command.Parse(
            [
                target.FullName,
                ItemId(items, @"Resources\fr-fr\Resources.resw"),
                "explicit-target-item",
                "Concurrent explicit item decision.",
                "--target-path", @"Resources\fr-fr\Resources.resw",
                "--target-item-type", "PRIResource"
            ]).InvokeAsync(cancellationToken: TestContext.CancellationToken);
            await Task.Delay(150, TestContext.CancellationToken);
            Assert.IsFalse(sdkTask.IsCompleted);
            Assert.IsFalse(explicitTask.IsCompleted);
        }
        finally
        {
            await heldLock.DisposeAsync();
        }

        var exits = await Task.WhenAll(sdkTask, explicitTask);
        Assert.AreEqual(0, exits[0]);
        Assert.AreEqual(0, exits[1]);
        using var report = await ReadReportAsync(target);
        Assert.AreEqual(
            2,
            report.RootElement.GetProperty("projectItemDecisions").GetArrayLength());
    }

    [TestMethod]
    public async Task DecideProjectItem_RejectsUnsafeTargetPath()
    {
        var (_, target) = await CreateDecisionMigrationAsync("UnsafePathApp");
        await ArrangeTargetFactsAsync(target);
        var items = await ReadDecisionItemsAsync(target);

        var result = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\fr-fr\Resources.resw"),
            "--strategy", "explicit-target-item",
            "--target-path", @"..\Outside.resw",
            "--target-item-type", "PRIResource",
            "--rationale", "Target paths must remain contained.");

        Assert.AreEqual(1, result.ExitCode, result.Output);
        StringAssert.Contains(result.Output, "cannot contain parent traversal");
    }

    private async Task<(DirectoryInfo Source, DirectoryInfo Target)>
        CreateDecisionMigrationAsync(
            string name,
            bool includeDispositionItems = false,
            bool includeExpressionItem = false)
    {
        var source = _tempDirectory.CreateSubdirectory(name);
        var shared = _tempDirectory.CreateSubdirectory($"{name}-shared");
        await WriteAsync(shared, "NOTICE.json", """{"license":"sample"}""");
        var dispositionItems = includeDispositionItems
            ? """
                <Content Include="Data\**\*.json" Condition="'$(Configuration)' == 'Debug'" />
                <Content Include="Legacy\**\*.json" Condition="'$(Configuration)' == 'Debug'" />
                <Content Include="Discarded\**\*.json" Condition="'$(Configuration)' == 'Debug'" />
              """
            : string.Empty;
        var expressionProperty = includeExpressionItem
            ? """
                <PropertyGroup>
                  <ExpressionRoot>$(MSBuildProjectDirectory)\Expression</ExpressionRoot>
                </PropertyGroup>
              """
            : string.Empty;
        var expressionItem = includeExpressionItem
            ? """
                <Content Include="$(ExpressionRoot)\Payload.json">
                  <Link>Strings\Expression.json</Link>
                </Content>
              """
            : string.Empty;
        await WriteAsync(
            source,
            $"{name}.csproj",
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
            {{expressionProperty}}
              <ItemGroup>
                <PRIResource Include="Resources\en-us\Resources.resw">
                  <SubType>Designer</SubType>
                </PRIResource>
                <PRIResource Include="Resources\fr-fr\Resources.resw">
                  <SubType>Designer</SubType>
                </PRIResource>
                <Content Include="..\{{shared.Name}}\NOTICE.json">
                  <Link>Strings\NOTICE.json</Link>
                  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
                </Content>
            {{expressionItem}}
            {{dispositionItems}}
              </ItemGroup>
            </Project>
            """);
        await WriteAsync(source, @"Resources\en-us\Resources.resw", "<root />");
        await WriteAsync(source, @"Resources\fr-fr\Resources.resw", "<root />");
        await WriteAsync(source, @"Data\Model.json", """{"value":1}""");
        await WriteAsync(source, @"Legacy\Model.json", """{"legacy":true}""");
        await WriteAsync(source, @"Discarded\Model.json", """{"discarded":true}""");
        if (includeExpressionItem)
        {
            await WriteAsync(
                source,
                @"Expression\Payload.json",
                """{"source":"expression"}""");
        }
        await WriteAsync(source, "Package.appxmanifest", SourceManifest);

        var target = new DirectoryInfo(Path.Combine(
            _tempDirectory.FullName,
            $"{name}-output"));
        ArrangeTemplateCreation(target, $"{name}App");
        var (exit, output) = await InvokeMigrateAsync(source, target);
        Assert.AreEqual(0, exit, output);

        var targetNotice = Path.Combine(
            target.FullName,
            "Strings",
            "NOTICE.json");
        Directory.CreateDirectory(Path.GetDirectoryName(targetNotice)!);
        File.Copy(
            Path.Combine(shared.FullName, "NOTICE.json"),
            targetNotice,
            overwrite: true);
        await WriteAsync(target, @"Assets\Replacement.json", """{"replacement":true}""");
        await WriteAsync(target, @"Build\Items.targets", "<Project />");
        return (source, target);
    }

    private async Task<EvaluationState> ArrangeTargetFactsAsync(
        DirectoryInfo target,
        bool metadataMatches = true,
        bool sdkOriginExternal = true,
        bool explicitOriginContained = true)
    {
        var externalSdk = target.Parent!.CreateSubdirectory(
            $"{target.Name}-sdk");
        var sdkTargets = externalSdk.CreateSubdirectory(
            Path.Combine(
                "Sdks",
                "Microsoft.NET.Sdk",
                "targets"));
        var sdkProps = Path.Combine(
            sdkTargets.FullName,
            "Microsoft.NET.Sdk.DefaultItems.props");
        await File.WriteAllTextAsync(
            sdkProps,
            "<Project />",
            TestContext.CancellationToken);
        var state = new EvaluationState(
            target,
            sdkProps,
            metadataMatches,
            sdkOriginExternal,
            explicitOriginContained);
        FakeMsBuildEvaluator.Handler = state.EvaluateAsync;
        return state;
    }

    private async Task RecordDeterministicDecisionsAsync(
        DirectoryInfo target,
        JsonArray items)
    {
        var sdk = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\en-us\Resources.resw"),
            "--strategy", "sdk-default-item",
            "--target-path", @"Resources\en-us\Resources.resw",
            "--rationale", "Evaluated SDK PRIResource coverage.");
        var explicitItem = await InvokeDecisionAsync(
            target,
            "--item", ItemId(items, @"Resources\fr-fr\Resources.resw"),
            "--strategy", "explicit-target-item",
            "--target-path", @"Resources\fr-fr\Resources.resw",
            "--target-item-type", "PRIResource",
            "--rationale", "Evaluated explicit PRIResource coverage.");
        var copied = await InvokeDecisionAsync(
            target,
            "--item", ItemIdByLink(items, @"Strings\NOTICE.json"),
            "--strategy", "copied-linked-content",
            "--target-path", @"Strings\NOTICE.json",
            "--target-item-type", "Content",
            "--rationale", "Evaluated copied linked content.");
        Assert.AreEqual(0, sdk.ExitCode, sdk.Output);
        Assert.AreEqual(0, explicitItem.ExitCode, explicitItem.Output);
        Assert.AreEqual(0, copied.ExitCode, copied.Output);
    }

    private async Task WriteAsync(
        DirectoryInfo directory,
        string relativePath,
        string content)
    {
        var path = Path.Combine(directory.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            content,
            TestContext.CancellationToken);
    }

    private async Task<JsonArray> ReadDecisionItemsAsync(DirectoryInfo target)
    {
        var report = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"),
            TestContext.CancellationToken))!;
        return report["mechanicalVerification"]!["projectItems"]!
            ["decisionRequiredItems"]!.AsArray();
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

    private static JsonElement AccountedItem(
        IEnumerable<JsonElement> items,
        string path) =>
        items.Single(item =>
            item.GetProperty("path").GetString() == path);

    private static string TargetProject(DirectoryInfo target) =>
        Path.GetFileName(Directory.EnumerateFiles(
            target.FullName,
            "*.csproj",
            SearchOption.TopDirectoryOnly).Single());

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
        return InvokeCapturingConsoleAsync(
            GetRequiredService<MigrateProjectItemDecisionCommand>(),
            [
                target.FullName,
                item,
                strategy,
                rationale,
                .. remaining
            ]);
    }

    private async Task<(int ExitCode, string Output)> InvokeMigrateAsync(
        DirectoryInfo source,
        DirectoryInfo target) =>
        await InvokeCapturingConsoleAsync(
            GetRequiredService<MigrateCommand>(),
            source.FullName,
            "--output",
            target.FullName);

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

    private async Task<JsonDocument> ReadReportAsync(
        DirectoryInfo target) =>
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

    private static DirectoryInfo GetTemplateOutput(string arguments)
    {
        var tokens = WindowsCommandLine.SplitArguments(arguments);
        var outputIndex = tokens.ToList().IndexOf("--output");
        Assert.IsTrue(outputIndex >= 0 && outputIndex + 1 < tokens.Count);
        return new DirectoryInfo(tokens[outputIndex + 1]);
    }

    private sealed class EvaluationState(
        DirectoryInfo target,
        string sdkProps,
        bool metadataMatches,
        bool sdkOriginExternal,
        bool explicitOriginContained)
    {
        private int _evaluationCount;

        internal int EvaluationCount => Volatile.Read(
            ref _evaluationCount);

        internal bool IncludeCopiedItem { get; set; } = true;

        internal bool IncludeExpressionItem { get; set; }

        internal Task<MsBuildProjectItemEvaluation> EvaluateAsync(
            FileInfo project,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _evaluationCount);
            var explicitEvidence = Path.Combine(
                target.FullName,
                "Build",
                "Items.targets");
            var items = new List<MsBuildProjectItem>
            {
                Item(
                    "PRIResource",
                    @"Resources\en-us\Resources.resw",
                    sdkOriginExternal
                        ? sdkProps
                        : explicitEvidence,
                    ("SubType", metadataMatches ? "Designer" : "Code")),
                Item(
                    "PRIResource",
                    @"Resources\fr-fr\Resources.resw",
                    explicitOriginContained
                        ? explicitEvidence
                        : sdkProps,
                    ("SubType", metadataMatches ? "Designer" : "Code")),
                Item(
                    "Content",
                    @"Assets\Replacement.json",
                    project.FullName)
            };
            if (IncludeCopiedItem)
            {
                items.Add(Item(
                    "Content",
                    @"Strings\NOTICE.json",
                    explicitOriginContained
                        ? explicitEvidence
                        : sdkProps,
                    (
                        "CopyToOutputDirectory",
                        metadataMatches
                            ? "PreserveNewest"
                            : "Never")));
            }
            if (IncludeExpressionItem)
            {
                items.Add(Item(
                    "Content",
                    @"Strings\Expression.json",
                    explicitOriginContained
                        ? explicitEvidence
                        : sdkProps));
            }
            return Task.FromResult(
                MsBuildProjectItemEvaluation.Success(items));
        }

        private MsBuildProjectItem Item(
            string kind,
            string relativePath,
            string definingProject,
            params (string Name, string Value)[] metadata)
        {
            var fullPath = Path.Combine(target.FullName, relativePath);
            return new MsBuildProjectItem(
                kind,
                relativePath,
                fullPath,
                definingProject,
                metadata.ToDictionary(
                    pair => pair.Name,
                    pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase));
        }
    }

    private const string SourceManifest = """
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
          <Applications><Application Id="App" /></Applications>
        </Package>
        """;

    private const string TargetManifest = """
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
          <Applications><Application Id="App" /></Applications>
        </Package>
        """;
}
