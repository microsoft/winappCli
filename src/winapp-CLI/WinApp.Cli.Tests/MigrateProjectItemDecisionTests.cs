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
        StringAssert.Contains(noProjectItem.Output, "No matching PRIResource");
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
        await WriteAsync(
            target,
            "Directory.Build.targets",
            """
            <Project>
              <ItemGroup>
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
        var commandArguments = new List<string> { target.FullName };
        commandArguments.AddRange(arguments);
        return InvokeCapturingConsoleAsync(
            GetRequiredService<MigrateProjectItemDecisionCommand>(),
            [.. commandArguments]);
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
