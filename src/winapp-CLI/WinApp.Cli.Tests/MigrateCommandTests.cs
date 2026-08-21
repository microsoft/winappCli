// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public class MigrateCommandTests : MigrateCommandTestBase
{
    private async Task WriteAsync(DirectoryInfo dir, string relative, string content)
    {
        var path = Path.Combine(dir.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, TestContext.CancellationToken);
    }

    private async Task<DirectoryInfo> CreateUwpSourceAsync(string name)
    {
        var dir = _tempDirectory.CreateSubdirectory(name);
        await WriteAsync(dir, $"{name}.csproj", CleanCsproj);
        await WriteAsync(dir, "Package.appxmanifest", """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
              <Capabilities><Capability Name="internetClient" /></Capabilities>
              <Applications><Application><Extensions>
                <uap:Extension Category="windows.appService" />
              </Extensions></Application></Applications>
            </Package>
            """);
        return dir;
    }

    private void ArrangeTemplateCreation(
        DirectoryInfo target,
        string projectName = "MigratedApp",
        Action<DirectoryInfo>? customizeTemplate = null)
    {
        FakeDotNet.RunDotnetCommandHandler = arguments =>
        {
            Assert.IsTrue(arguments.Contains("new winui", StringComparison.Ordinal), arguments);
            var templateOutput = GetTemplateOutput(arguments);
            templateOutput.Create();
            File.WriteAllText(Path.Combine(templateOutput.FullName, $"{projectName}.csproj"), CleanCsproj);
            File.WriteAllText(Path.Combine(templateOutput.FullName, "App.xaml"),
                $"<Application x:Class=\"{projectName}.App\"><Application.Resources><ResourceDictionary /></Application.Resources></Application>");
            File.WriteAllText(Path.Combine(templateOutput.FullName, "App.xaml.cs"),
                $"namespace {projectName}; public partial class App {{ }}");
            File.WriteAllText(Path.Combine(templateOutput.FullName, "MainWindow.xaml"),
                $"<Window x:Class=\"{projectName}.MainWindow\"><Grid Grid.Row=\"1\" /></Window>");
            File.WriteAllText(Path.Combine(templateOutput.FullName, "MainWindow.xaml.cs"),
                $"namespace {projectName}; public partial class MainWindow {{ public MainWindow() {{ InitializeComponent(); }} }}");
            customizeTemplate?.Invoke(templateOutput);
            return (0, "Template created.", string.Empty);
        };
    }

    private static DirectoryInfo GetTemplateOutput(string arguments)
    {
        var tokens = WindowsCommandLine.SplitArguments(arguments);
        var outputIndex = tokens.ToList().IndexOf("--output");
        Assert.IsTrue(outputIndex >= 0 && outputIndex + 1 < tokens.Count, "Template command must specify --output.");
        return new DirectoryInfo(tokens[outputIndex + 1]);
    }

    private async Task<(int ExitCode, string Output)> InvokeAsync(DirectoryInfo source, DirectoryInfo target, params string[] extra)
    {
        var args = new List<string> { source.FullName, "--output", target.FullName };
        args.AddRange(extra);
        return await InvokeCapturingConsoleAsync(GetRequiredService<MigrateCommand>(), [.. args]);
    }

    private Task<(int ExitCode, string Output)> InvokeVerifyAsync(DirectoryInfo target) =>
        InvokeCapturingConsoleAsync(GetRequiredService<MigrateVerifyCommand>(), target.FullName);

    [TestMethod]
    public async Task Migrate_CreatesWinuiProjectAndWritesReport()
    {
        var source = await CreateUwpSourceAsync("SensorApp");
        await WriteAsync(source, "MainPage.xaml", "<Page x:Class=\"SDKTemplate.MainPage\" />");
        await WriteAsync(source, "MainPage.xaml.cs",
            "using Windows.UI.Xaml.Controls; namespace SDKTemplate; public partial class MainPage { }");
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "output"));
        ArrangeTemplateCreation(target, "SensorAppApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        StringAssert.Contains(output, "=== MECHANICAL MIGRATION COMPLETE ===");
        Assert.HasCount(1, FakeDotNet.CommandCalls);
        StringAssert.Contains(FakeDotNet.CommandCalls[0].Arguments, "new winui");
        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, "SensorAppApp.csproj")));
        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, "migration-report.json")));
        var migrated = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "MainPage.xaml.cs"), TestContext.CancellationToken);
        StringAssert.Contains(migrated, "Microsoft.UI.Xaml.Controls");

        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"), TestContext.CancellationToken));
        Assert.AreEqual("1.1", report.RootElement.GetProperty("schemaVersion").GetString());
        Assert.AreEqual("mechanical-migration-complete", report.RootElement.GetProperty("status").GetString());
        Assert.IsTrue(report.RootElement.GetProperty("todos").GetArrayLength() >= 2);
        Assert.IsTrue(report.RootElement.GetProperty("todos").EnumerateArray()
            .All(todo => todo.GetProperty("status").GetString() == "pending"));
        var validation = report.RootElement.GetProperty("validation");
        Assert.AreEqual(
            ".migration-evidence/state-plan.json",
            validation.GetProperty("statePlan").GetString());
        Assert.AreEqual(
            "not-run",
            validation.GetProperty("sourceBaseline").GetProperty("status").GetString());
        Assert.AreEqual(
            ".migration-evidence/source",
            validation.GetProperty("sourceBaseline").GetProperty("evidenceRoot").GetString());
        Assert.AreEqual(
            "not-run",
            validation.GetProperty("targetReplay").GetProperty("status").GetString());
        Assert.AreEqual(
            ".migration-evidence/target",
            validation.GetProperty("targetReplay").GetProperty("evidenceRoot").GetString());
        Assert.AreEqual("unverified", validation.GetProperty("parityStatus").GetString());
    }

    [TestMethod]
    public async Task Migrate_LegacyAssemblyInfoDisablesGeneratedAssemblyAttributes()
    {
        var source = await CreateUwpSourceAsync("AssemblyInfoApp");
        await WriteAsync(
            source,
            @"Properties\AssemblyInfo.cs",
            """[assembly: System.Reflection.AssemblyCompany("Contoso")]""");
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "assembly-info-output"));
        ArrangeTemplateCreation(target, "AssemblyInfoAppApp", templateOutput =>
        {
            var nestedDirectory = templateOutput.CreateSubdirectory("NestedLibrary");
            File.WriteAllText(Path.Combine(nestedDirectory.FullName, "NestedLibrary.csproj"), CleanCsproj);
        });

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, "Properties", "AssemblyInfo.cs")));
        var project = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "AssemblyInfoAppApp.csproj"),
            TestContext.CancellationToken);
        StringAssert.Contains(project, "<GenerateAssemblyInfo>false</GenerateAssemblyInfo>");
        var nestedProject = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "NestedLibrary", "NestedLibrary.csproj"),
            TestContext.CancellationToken);
        Assert.DoesNotContain("<GenerateAssemblyInfo>", nestedProject);
        StringAssert.Contains(output, "preserved Properties\\AssemblyInfo.cs remains authoritative");
    }

    [TestMethod]
    public async Task Migrate_RewritesReswResourceKeyNamespaces()
    {
        var source = await CreateUwpSourceAsync("LocalizedApp");
        await WriteAsync(source, @"Resources\en-us\Resources.resw", """
            <?xml version="1.0" encoding="utf-8"?>
            <root>
              <data name="AddButton.[using:Windows.UI.Xaml.Automation]AutomationProperties.Name" xml:space="preserve">
                <value>Add</value>
              </data>
            </root>
            """);
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "localized-output"));
        ArrangeTemplateCreation(target, "LocalizedAppApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        var migrated = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "Resources", "en-us", "Resources.resw"),
            TestContext.CancellationToken);
        StringAssert.Contains(
            migrated,
            "[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name");
        Assert.DoesNotContain("Windows.UI.Xaml", migrated);

        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"),
            TestContext.CancellationToken));
        var transform = report.RootElement.GetProperty("transforms").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "UWMIG-RESW-NS");
        Assert.AreEqual(1, transform.GetProperty("changedFiles").GetInt32());
        var verification = report.RootElement.GetProperty("mechanicalVerification");
        Assert.AreEqual("passed", verification.GetProperty("status").GetString());
        Assert.AreEqual(0, verification.GetProperty("legacyNamespaceResiduals").GetArrayLength());
    }

    [TestMethod]
    public async Task Migrate_MigratesLocalContentAndPriResourceProjectItems()
    {
        var source = await CreateUwpSourceAsync("ProjectItemsApp");
        await WriteAsync(source, "ProjectItemsApp.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>uap10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <Content Include="Data\Model.json">
                  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
                </Content>
                <PRIResource Include="Resources\en-us\Resources.resw" />
              </ItemGroup>
            </Project>
            """);
        await WriteAsync(source, @"Data\Model.json", """{"name":"lamp"}""");
        await WriteAsync(source, @"Resources\en-us\Resources.resw", "<root />");
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "project-items-output"));
        ArrangeTemplateCreation(target, "ProjectItemsAppApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        var targetProject = XDocument.Load(Path.Combine(target.FullName, "ProjectItemsAppApp.csproj"));
        Assert.IsTrue(targetProject.Descendants().Any(element =>
            element.Name.LocalName == "Content"
            && element.Attribute("Include")?.Value == @"Data\Model.json"));
        Assert.IsTrue(targetProject.Descendants().Any(element =>
            element.Name.LocalName == "None"
            && element.Attribute("Remove")?.Value.Contains(@"Data\Model.json", StringComparison.Ordinal) == true));
        Assert.IsFalse(targetProject.Descendants().Any(element =>
            element.Name.LocalName == "PRIResource"
            && element.Attribute("Include")?.Value == @"Resources\en-us\Resources.resw"));

        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"),
            TestContext.CancellationToken));
        var projectItems = report.RootElement
            .GetProperty("mechanicalVerification")
            .GetProperty("projectItems");
        Assert.AreEqual(2, projectItems.GetProperty("sourceItems").GetInt32());
        Assert.AreEqual(2, projectItems.GetProperty("migratedItems").GetInt32());
        Assert.AreEqual(0, projectItems.GetProperty("missingTargetItems").GetArrayLength());
    }

    [TestMethod]
    public async Task Migrate_ReportsProjectItemsThatRequireMsbuildEvaluation()
    {
        var source = await CreateUwpSourceAsync("ConditionalItemsApp");
        await WriteAsync(source, "ConditionalItemsApp.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Content Include="Data\**\*.json" Condition="'$(Configuration)' == 'Debug'" />
              </ItemGroup>
            </Project>
            """);
        await WriteAsync(source, @"Data\Model.json", """{"name":"lamp"}""");
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "conditional-items-output"));
        ArrangeTemplateCreation(target, "ConditionalItemsAppApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"),
            TestContext.CancellationToken));
        var projectItems = report.RootElement
            .GetProperty("mechanicalVerification")
            .GetProperty("projectItems");
        Assert.AreEqual(1, projectItems.GetProperty("sourceItems").GetInt32());
        Assert.AreEqual(0, projectItems.GetProperty("migratedItems").GetInt32());
        Assert.AreEqual(1, projectItems.GetProperty("unresolvedItems").GetArrayLength());
        Assert.IsTrue(report.RootElement.GetProperty("todos").EnumerateArray().Any(todo =>
            todo.GetProperty("category").GetString() == "project-items"));
    }

    [TestMethod]
    public async Task Migrate_FailsVerificationForLegacyNamespaceInOtherText()
    {
        var source = await CreateUwpSourceAsync("ResidualApp");
        await WriteAsync(source, "Data.json", """{"type":"Windows.UI.Xaml.Controls.Button"}""");
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "residual-output"));
        ArrangeTemplateCreation(target, "ResidualAppApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(1, exit, output);
        StringAssert.Contains(output, "MECHANICAL MIGRATION VERIFICATION FAILED");
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"),
            TestContext.CancellationToken));
        Assert.AreEqual(
            "mechanical-verification-failed",
            report.RootElement.GetProperty("status").GetString());
        var verification = report.RootElement.GetProperty("mechanicalVerification");
        Assert.AreEqual("failed", verification.GetProperty("status").GetString());
        var residual = verification.GetProperty("legacyNamespaceResiduals")[0];
        Assert.AreEqual("Data.json", residual.GetProperty("path").GetString());
        Assert.AreEqual(1, residual.GetProperty("line").GetInt32());
    }

    [TestMethod]
    public async Task Migrate_IgnoresNestedMigrationReportsDuringVerification()
    {
        var source = await CreateUwpSourceAsync("NestedReportApp");
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "nested-report-output"));
        ArrangeTemplateCreation(target, "NestedReportAppApp", templateOutput =>
        {
            var nestedDirectory = templateOutput.CreateSubdirectory("NestedLibrary");
            File.WriteAllText(
                Path.Combine(nestedDirectory.FullName, "migration-report.json"),
                """{"legacyExample":"Windows.UI.Xaml.Controls.Button"}""");
        });

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"),
            TestContext.CancellationToken));
        Assert.AreEqual(
            0,
            report.RootElement
                .GetProperty("mechanicalVerification")
                .GetProperty("legacyNamespaceResiduals")
                .GetArrayLength());
    }

    [TestMethod]
    public async Task Migrate_AccountsForEveryEligibleSourceFile()
    {
        var source = await CreateUwpSourceAsync("InventoryApp");
        await WriteAsync(source, "MainPage.xaml", "<Page />");
        await WriteAsync(source, "InventoryApp_TemporaryKey.pfx", "not-a-real-certificate");
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "inventory-output"));
        ArrangeTemplateCreation(target, "InventoryAppApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"),
            TestContext.CancellationToken));
        var inventory = report.RootElement
            .GetProperty("mechanicalVerification")
            .GetProperty("inventory");
        Assert.AreEqual(
            inventory.GetProperty("sourceFiles").GetInt32(),
            inventory.GetProperty("classifiedFiles").GetInt32());
        Assert.AreEqual(1, inventory.GetProperty("copiedFiles").GetInt32());
        Assert.AreEqual(2, inventory.GetProperty("preservedReferenceFiles").GetInt32());
        Assert.AreEqual(1, inventory.GetProperty("intentionallyExcludedFiles").GetInt32());
        Assert.AreEqual(0, inventory.GetProperty("unclassifiedFiles").GetArrayLength());
    }

    [TestMethod]
    public async Task Verify_RefreshesMechanicalStateWithoutChangingBehavioralValidation()
    {
        var source = await CreateUwpSourceAsync("VerifyApp");
        await WriteAsync(source, "MainPage.xaml", "<Page />");
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "verify-output"));
        ArrangeTemplateCreation(target, "VerifyAppApp");
        var (migrateExit, migrateOutput) = await InvokeAsync(source, target);
        Assert.AreEqual(0, migrateExit, migrateOutput);

        var reportPath = Path.Combine(target.FullName, "migration-report.json");
        var reportNode = JsonNode.Parse(await File.ReadAllTextAsync(
            reportPath,
            TestContext.CancellationToken))!;
        reportNode["validation"]!["sourceBaseline"]!["status"] = "captured";
        await File.WriteAllTextAsync(
            reportPath,
            reportNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            TestContext.CancellationToken);
        await WriteAsync(
            target,
            "AgentChange.json",
            """{"type":"Windows.UI.Xaml.Controls.Button"}""");

        var (failedExit, failedOutput) = await InvokeVerifyAsync(target);

        Assert.AreEqual(1, failedExit, failedOutput);
        StringAssert.Contains(failedOutput, "verification failed");
        using (var failedReport = JsonDocument.Parse(await File.ReadAllTextAsync(
            reportPath,
            TestContext.CancellationToken)))
        {
            Assert.AreEqual(
                "captured",
                failedReport.RootElement
                    .GetProperty("validation")
                    .GetProperty("sourceBaseline")
                    .GetProperty("status")
                    .GetString());
            Assert.IsTrue(failedReport.RootElement.GetProperty("todos").EnumerateArray().Any(todo =>
                todo.GetProperty("id").GetString() == "UWMIG011"));
        }

        await WriteAsync(
            target,
            "AgentChange.json",
            """{"type":"Microsoft.UI.Xaml.Controls.Button"}""");
        var (passedExit, passedOutput) = await InvokeVerifyAsync(target);

        Assert.AreEqual(0, passedExit, passedOutput);
        StringAssert.Contains(passedOutput, "verification passed");
        using var passedReport = JsonDocument.Parse(await File.ReadAllTextAsync(
            reportPath,
            TestContext.CancellationToken));
        Assert.IsFalse(passedReport.RootElement.GetProperty("todos").EnumerateArray().Any(todo =>
            todo.GetProperty("id").GetString() == "UWMIG011"));
        Assert.AreEqual(
            "captured",
            passedReport.RootElement
                .GetProperty("validation")
                .GetProperty("sourceBaseline")
                .GetProperty("status")
                .GetString());
    }

    [TestMethod]
    public async Task Migrate_LeavesSemanticAndExternalLayoutDecisionsForLater()
    {
        var source = await CreateUwpSourceAsync("BoundaryApp");
        await WriteAsync(source, "App.xaml", """
            <Application>
              <Application.Resources>
                <ResourceDictionary Source="Styles.xaml" />
              </Application.Resources>
            </Application>
            """);
        const string navigationHelper = """
            namespace BoundaryApp.Common;
            public sealed class RootFrameNavigationHelper
            {
                public bool IsEnabled { get; set; } = true;
            }
            """;
        await WriteAsync(source, @"Common\NavigationHelper.cs", navigationHelper);
        await WriteAsync(source, "Styles.xaml", "<ResourceDictionary />");

        var siblingShared = source.Parent!.CreateSubdirectory("shared");
        await WriteAsync(siblingShared, "SiblingOnly.cs", "namespace External.Shared;");

        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "boundary-output"));
        ArrangeTemplateCreation(target, "BoundaryAppApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        Assert.IsFalse(File.Exists(Path.Combine(target.FullName, "SiblingOnly.cs")));
        Assert.AreEqual(
            navigationHelper,
            await File.ReadAllTextAsync(
                Path.Combine(target.FullName, "Common", "NavigationHelper.cs"),
                TestContext.CancellationToken));

        var targetApp = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "App.xaml"),
            TestContext.CancellationToken);
        Assert.DoesNotContain("Styles.xaml", targetApp);

        var mainWindow = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "MainWindow.xaml"),
            TestContext.CancellationToken);
        Assert.DoesNotContain("RootFrame", mainWindow);

        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"),
            TestContext.CancellationToken));
        Assert.IsTrue(report.RootElement.GetProperty("todos").EnumerateArray().Any(todo =>
            todo.GetProperty("category").GetString() == "app-resources"));
    }

    [TestMethod]
    public async Task Migrate_RewritesSafeVirtualizingStackPanelAndPreservesCompatibleAttributes()
    {
        var source = await CreateUwpSourceAsync("ItemsPanelApp");
        await WriteAsync(source, "Styles.xaml", """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                xmlns:controls="using:Microsoft.UI.Xaml.Controls">
              <!-- <VirtualizingStackPanel Background="Red" /> -->
              <ItemsPanelTemplate x:Key="LegacyItemsPanel">
                <VirtualizingStackPanel Background="Transparent" Orientation="Horizontal" CacheLength="2" AreStickyGroupHeadersEnabled="False" GroupHeaderPlacement="Top" />
              </ItemsPanelTemplate>
              <ItemsPanelTemplate x:Key="PrefixedItemsPanel">
                <controls:VirtualizingStackPanel Background="Green"></controls:VirtualizingStackPanel>
              </ItemsPanelTemplate>
              <ItemsPanelTemplate x:Key="AlreadyMigrated">
                <ItemsStackPanel Background="Blue" />
              </ItemsPanelTemplate>
              <TextBlock Text="VirtualizingStackPanel" />
            </ResourceDictionary>
            """);
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "items-panel-output"));
        ArrangeTemplateCreation(target, "ItemsPanelApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        var migrated = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "Styles.xaml"), TestContext.CancellationToken);
        StringAssert.Contains(
            migrated,
            "<ItemsStackPanel Background=\"Transparent\" Orientation=\"Horizontal\" CacheLength=\"2\" AreStickyGroupHeadersEnabled=\"False\" GroupHeaderPlacement=\"Top\" />");
        StringAssert.Contains(
            migrated,
            "<controls:ItemsStackPanel Background=\"Green\"></controls:ItemsStackPanel>");
        StringAssert.Contains(migrated, "<!-- <VirtualizingStackPanel Background=\"Red\" /> -->");
        StringAssert.Contains(migrated, "<ItemsStackPanel Background=\"Blue\" />");
        StringAssert.Contains(migrated, "Text=\"VirtualizingStackPanel\"");

        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"), TestContext.CancellationToken));
        var transform = report.RootElement.GetProperty("transforms").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "UWMIG-XAML-ITEMS-PANEL");
        Assert.AreEqual(1, transform.GetProperty("changedFiles").GetInt32());
        Assert.IsFalse(report.RootElement.GetProperty("todos").EnumerateArray()
            .Any(todo => todo.GetProperty("category").GetString() == "xaml-items-panel"));
    }

    [TestMethod]
    public async Task Migrate_PreservesAndReportsUnsafeVirtualizingStackPanelOccurrences()
    {
        var source = await CreateUwpSourceAsync("UnsafeItemsPanelApp");
        await WriteAsync(source, "Styles.xaml", """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                xmlns:local="using:UnsafeItemsPanelApp">
              <VirtualizingStackPanel Background="Transparent" />
              <ItemsPanelTemplate x:Key="UnsupportedAttribute">
                <VirtualizingStackPanel VirtualizingStackPanel.VirtualizationMode="Recycling" />
              </ItemsPanelTemplate>
              <ItemsPanelTemplate x:Key="TypeSpecificStyle">
                <VirtualizingStackPanel Style="{StaticResource LegacyPanelStyle}" />
              </ItemsPanelTemplate>
              <ResourceDictionary>
                <Style TargetType="VirtualizingStackPanel" />
                <ItemsPanelTemplate x:Key="ImplicitTypeSpecificStyle">
                  <VirtualizingStackPanel />
                </ItemsPanelTemplate>
              </ResourceDictionary>
              <ItemsPanelTemplate x:Key="CustomType">
                <local:VirtualizingStackPanel />
              </ItemsPanelTemplate>
              <!-- <VirtualizingStackPanel /> -->
            </ResourceDictionary>
            """);
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "unsafe-items-panel-output"));
        ArrangeTemplateCreation(target, "UnsafeItemsPanelApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        var migrated = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "Styles.xaml"), TestContext.CancellationToken);
        StringAssert.Contains(migrated, "<VirtualizingStackPanel Background=\"Transparent\" />");
        StringAssert.Contains(
            migrated,
            "<VirtualizingStackPanel VirtualizingStackPanel.VirtualizationMode=\"Recycling\" />");
        StringAssert.Contains(
            migrated,
            "<VirtualizingStackPanel Style=\"{StaticResource LegacyPanelStyle}\" />");
        StringAssert.Contains(migrated, "<local:VirtualizingStackPanel />");
        StringAssert.Contains(migrated, "<!-- <VirtualizingStackPanel /> -->");

        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"), TestContext.CancellationToken));
        var todo = report.RootElement.GetProperty("todos").EnumerateArray()
            .Single(item => item.GetProperty("category").GetString() == "xaml-items-panel");
        Assert.AreEqual("required", todo.GetProperty("priority").GetString());
        var locations = todo.GetProperty("locations").EnumerateArray().ToList();
        Assert.HasCount(5, locations);
        Assert.AreEqual(4, locations[0].GetProperty("line").GetInt32());
        Assert.AreEqual(6, locations[1].GetProperty("line").GetInt32());
        Assert.AreEqual(9, locations[2].GetProperty("line").GetInt32());
        Assert.AreEqual(14, locations[3].GetProperty("line").GetInt32());
        Assert.AreEqual(18, locations[4].GetProperty("line").GetInt32());
        Assert.IsTrue(locations.All(location =>
            location.GetProperty("path").GetString() == "Styles.xaml"));
    }

    [TestMethod]
    public async Task Migrate_PreservesUtf16EncodingWhenRewritingItemsPanel()
    {
        var source = await CreateUwpSourceAsync("Utf16ItemsPanelApp");
        var stylesPath = Path.Combine(source.FullName, "Styles.xaml");
        const string styles = """
            <?xml version="1.0" encoding="utf-16"?>
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                xmlns:legacy="using:Windows.UI.Xaml.Controls">
              <ItemsPanelTemplate x:Key="LegacyItemsPanel">
                <VirtualizingStackPanel Orientation="Vertical" />
              </ItemsPanelTemplate>
            </ResourceDictionary>
            """;
        await File.WriteAllTextAsync(
            stylesPath,
            styles,
            new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            TestContext.CancellationToken);
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "utf16-items-panel-output"));
        ArrangeTemplateCreation(target, "Utf16ItemsPanelApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        var migratedPath = Path.Combine(target.FullName, "Styles.xaml");
        var bytes = await File.ReadAllBytesAsync(migratedPath, TestContext.CancellationToken);
        CollectionAssert.AreEqual(Encoding.Unicode.GetPreamble(), bytes[..2]);
        var migrated = await File.ReadAllTextAsync(migratedPath, TestContext.CancellationToken);
        StringAssert.Contains(migrated, "<ItemsStackPanel Orientation=\"Vertical\" />");
        StringAssert.Contains(migrated, "xmlns:legacy=\"using:Microsoft.UI.Xaml.Controls\"");
        _ = XDocument.Load(migratedPath);
    }

    [TestMethod]
    public async Task Migrate_ReportsPanelWhenAnotherDictionaryDefinesImplicitLegacyStyle()
    {
        var source = await CreateUwpSourceAsync("GlobalStyleItemsPanelApp");
        await WriteAsync(source, "Styles.xaml", """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Style TargetType="VirtualizingStackPanel" />
            </ResourceDictionary>
            """);
        await WriteAsync(source, "PageResources.xaml", """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <ItemsPanelTemplate x:Key="LegacyItemsPanel">
                <VirtualizingStackPanel />
              </ItemsPanelTemplate>
            </ResourceDictionary>
            """);
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "global-style-output"));
        ArrangeTemplateCreation(target, "GlobalStyleItemsPanelApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        var migrated = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "PageResources.xaml"),
            TestContext.CancellationToken);
        StringAssert.Contains(migrated, "<VirtualizingStackPanel />");
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"),
            TestContext.CancellationToken));
        Assert.IsTrue(report.RootElement.GetProperty("todos").EnumerateArray()
            .Any(todo => todo.GetProperty("category").GetString() == "xaml-items-panel"));
    }

    [TestMethod]
    public async Task Migrate_DoesNotRewriteBomlessLegacyEncodedXaml()
    {
        var source = await CreateUwpSourceAsync("LegacyEncodingItemsPanelApp");
        var stylesPath = Path.Combine(source.FullName, "Styles.xaml");
        var styles = """
            <?xml version="1.0" encoding="windows-1252"?>
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <!-- café -->
              <ItemsPanelTemplate>
                <VirtualizingStackPanel />
              </ItemsPanelTemplate>
            </ResourceDictionary>
            """;
        var originalBytes = Encoding.Latin1.GetBytes(styles);
        await File.WriteAllBytesAsync(
            stylesPath,
            originalBytes,
            TestContext.CancellationToken);
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "legacy-encoding-output"));
        ArrangeTemplateCreation(target, "LegacyEncodingItemsPanelApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        CollectionAssert.AreEqual(
            originalBytes,
            await File.ReadAllBytesAsync(
                Path.Combine(target.FullName, "Styles.xaml"),
                TestContext.CancellationToken));
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"),
            TestContext.CancellationToken));
        Assert.IsTrue(report.RootElement.GetProperty("todos").EnumerateArray()
            .Any(todo => todo.GetProperty("category").GetString() == "xaml-items-panel"));
    }

    [TestMethod]
    public async Task Migrate_LeavesExistingItemsStackPanelUnchanged()
    {
        var source = await CreateUwpSourceAsync("CurrentItemsPanelApp");
        const string styles = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <ItemsPanelTemplate x:Key="CurrentItemsPanel">
                <ItemsStackPanel Background="Transparent" Orientation="Vertical" />
              </ItemsPanelTemplate>
            </ResourceDictionary>
            """;
        await WriteAsync(source, "Styles.xaml", styles);
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "current-items-panel-output"));
        ArrangeTemplateCreation(target, "CurrentItemsPanelApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        Assert.AreEqual(
            styles,
            await File.ReadAllTextAsync(
                Path.Combine(target.FullName, "Styles.xaml"),
                TestContext.CancellationToken));
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"), TestContext.CancellationToken));
        var transform = report.RootElement.GetProperty("transforms").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "UWMIG-XAML-ITEMS-PANEL");
        Assert.AreEqual(0, transform.GetProperty("changedFiles").GetInt32());
        Assert.IsFalse(report.RootElement.GetProperty("todos").EnumerateArray()
            .Any(todo => todo.GetProperty("category").GetString() == "xaml-items-panel"));
    }

    [TestMethod]
    public async Task Migrate_RewritesDispatcherHasThreadAccessWithoutTodo()
    {
        var source = await CreateUwpSourceAsync("DispatcherApp");
        await WriteAsync(source, "MainPage.xaml", "<Page x:Class=\"SDKTemplate.MainPage\" />");
        await WriteAsync(source, "MainPage.xaml.cs", """
            namespace SDKTemplate;
            public partial class MainPage : Page
            {
                void NotifyUser()
                {
                    if (Dispatcher.HasThreadAccess) { }
                }
            }
            """);
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "dispatcher-output"));
        ArrangeTemplateCreation(target, "DispatcherApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        var migrated = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "MainPage.xaml.cs"), TestContext.CancellationToken);
        StringAssert.Contains(migrated, "DispatcherQueue.HasThreadAccess");
        Assert.IsFalse(migrated.Contains("Dispatcher.HasThreadAccess", StringComparison.Ordinal));

        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"), TestContext.CancellationToken));
        var todos = report.RootElement.GetProperty("todos").EnumerateArray();
        Assert.IsFalse(todos.Any(todo => todo.GetProperty("category").GetString() == "dispatcher"));
    }

    [TestMethod]
    public async Task Migrate_ReportsDispatcherRunAsyncAsKnownResidual()
    {
        var source = await CreateUwpSourceAsync("AsyncDispatcherApp");
        await WriteAsync(source, "MainPage.xaml", "<Page x:Class=\"SDKTemplate.MainPage\" />");
        await WriteAsync(source, "MainPage.xaml.cs", """
            namespace SDKTemplate;
            public partial class MainPage
            {
                async void Update() =>
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => { });
            }
            """);
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "async-dispatcher-output"));
        ArrangeTemplateCreation(target, "AsyncDispatcherApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"), TestContext.CancellationToken));
        var dispatcherTodo = report.RootElement.GetProperty("todos").EnumerateArray()
            .Single(todo => todo.GetProperty("category").GetString() == "dispatcher");
        Assert.AreEqual("required", dispatcherTodo.GetProperty("priority").GetString());
        Assert.AreEqual(1, dispatcherTodo.GetProperty("locations").GetArrayLength());
        Assert.AreEqual(5, dispatcherTodo.GetProperty("locations")[0].GetProperty("line").GetInt32());
    }

    [TestMethod]
    public async Task Migrate_ReportsWindowCurrentBoundsAsWindowSizingResidual()
    {
        var source = await CreateUwpSourceAsync("WindowApp");
        await WriteAsync(source, "MainPage.xaml", "<Page x:Class=\"SDKTemplate.MainPage\" />");
        await WriteAsync(source, "MainPage.xaml.cs", """
            namespace SDKTemplate;
            public partial class MainPage
            {
                double Width() => Window.Current.Bounds.Width;
            }
            """);
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "window-output"));
        ArrangeTemplateCreation(target, "WindowApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"), TestContext.CancellationToken));
        var windowSizingTodo = report.RootElement.GetProperty("todos").EnumerateArray()
            .Single(todo => todo.GetProperty("category").GetString() == "window-sizing");
        Assert.AreEqual("required", windowSizingTodo.GetProperty("priority").GetString());
        Assert.AreEqual(4, windowSizingTodo.GetProperty("locations")[0].GetProperty("line").GetInt32());
        StringAssert.Contains(windowSizingTodo.GetProperty("reason").GetString(), "XamlRoot.Size");
        Assert.IsFalse(report.RootElement.GetProperty("todos").EnumerateArray()
            .Any(todo => todo.GetProperty("category").GetString() == "windowing"));
    }

    [TestMethod]
    public async Task Migrate_ReportsOtherWindowCurrentAsWindowingResidual()
    {
        var source = await CreateUwpSourceAsync("WindowContentApp");
        await WriteAsync(source, "MainPage.xaml", "<Page x:Class=\"SDKTemplate.MainPage\" />");
        await WriteAsync(source, "MainPage.xaml.cs", """
            namespace SDKTemplate;
            public partial class MainPage
            {
                object? Content() => Window.Current.Content;
            }
            """);
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "window-content-output"));
        ArrangeTemplateCreation(target, "WindowContentApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"), TestContext.CancellationToken));
        Assert.IsTrue(report.RootElement.GetProperty("todos").EnumerateArray()
            .Any(todo => todo.GetProperty("category").GetString() == "windowing"));
        Assert.IsFalse(report.RootElement.GetProperty("todos").EnumerateArray()
            .Any(todo => todo.GetProperty("category").GetString() == "window-sizing"));
    }

    [TestMethod]
    public async Task Migrate_ReportsDisplayInformationWithApiSpecificGuidance()
    {
        var source = await CreateUwpSourceAsync("DisplayInformationApp");
        await WriteAsync(source, "MainPage.xaml", "<Page x:Class=\"SDKTemplate.MainPage\" />");
        await WriteAsync(source, "MainPage.xaml.cs", """
            using Windows.Graphics.Display;
            namespace SDKTemplate;
            public partial class MainPage
            {
                DisplayInformation Current() => DisplayInformation.GetForCurrentView();
            }
            """);
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "display-information-output"));
        ArrangeTemplateCreation(target, "DisplayInformationApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"), TestContext.CancellationToken));
        var todo = report.RootElement.GetProperty("todos").EnumerateArray()
            .Single(item => item.GetProperty("category").GetString() == "display-information");
        StringAssert.Contains(todo.GetProperty("reason").GetString(), "Do not invent IDisplayInformationStaticsInterop");
        StringAssert.Contains(todo.GetProperty("reason").GetString(), "MonitorFromWindow");
        StringAssert.Contains(todo.GetProperty("reason").GetString(), "GetMonitorInfo");
        StringAssert.Contains(todo.GetProperty("reason").GetString(), "EnumDisplaySettings");
    }

    [TestMethod]
    public async Task Migrate_DoesNotRewriteDispatcherTextOrAmbiguousReceiver()
    {
        var source = await CreateUwpSourceAsync("AmbiguousDispatcherApp");
        await WriteAsync(source, "MainPage.xaml", "<Page x:Class=\"SDKTemplate.MainPage\" />");
        await WriteAsync(source, "MainPage.xaml.cs", """
            namespace SDKTemplate;
            public partial class MainPage : Page
            {
                string Description = "Dispatcher.HasThreadAccess";
                // Dispatcher.HasThreadAccess must not be changed in this comment.
                bool Access() => Dispatcher.HasThreadAccess;
            }
            """);
        await WriteAsync(source, "Worker.cs", """
            public class Worker
            {
                bool Access() => Dispatcher.HasThreadAccess;
            }
            """);
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "ambiguous-dispatcher-output"));
        ArrangeTemplateCreation(target, "AmbiguousDispatcherApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        var page = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "MainPage.xaml.cs"), TestContext.CancellationToken);
        StringAssert.Contains(page, "\"Dispatcher.HasThreadAccess\"");
        StringAssert.Contains(page, "// Dispatcher.HasThreadAccess must not be changed");
        StringAssert.Contains(page, "DispatcherQueue.HasThreadAccess");
        var worker = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "Worker.cs"), TestContext.CancellationToken);
        StringAssert.Contains(worker, "Dispatcher.HasThreadAccess");

        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"), TestContext.CancellationToken));
        var dispatcherTodo = report.RootElement.GetProperty("todos").EnumerateArray()
            .Single(todo => todo.GetProperty("category").GetString() == "dispatcher");
        Assert.IsTrue(dispatcherTodo.GetProperty("locations").EnumerateArray()
            .Any(location => location.GetProperty("path").GetString() == "Worker.cs"));
    }

    [TestMethod]
    public async Task Migrate_PreservesSkippedAppFilesForTodo()
    {
        var source = await CreateUwpSourceAsync("ResourcesApp");
        await WriteAsync(source, "App.xaml", "<Application><Application.Resources><Color x:Key=\"Accent\">Red</Color></Application.Resources></Application>");
        await WriteAsync(source, "App.xaml.cs", "class UwpApp { }");
        await WriteAsync(source, "MainPage.xaml", "<Page x:Class=\"SDKTemplate.MainPage\" />");
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "resources-output"));
        ArrangeTemplateCreation(target, "ResourcesApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, ".uwp-source", "App.xaml.reference")));
        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, ".uwp-source", "App.xaml.cs.reference")));
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"), TestContext.CancellationToken));
        Assert.IsTrue(report.RootElement.GetProperty("todos").EnumerateArray()
            .Any(todo => todo.GetProperty("category").GetString() == "app-resources"));
    }

    [TestMethod]
    public async Task Migrate_TemplateFailureRemovesNewPartialOutput()
    {
        var source = await CreateUwpSourceAsync("FailedTemplateApp");
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "failed-output"));
        FakeDotNet.RunDotnetCommandHandler = arguments =>
        {
            var templateOutput = GetTemplateOutput(arguments);
            templateOutput.Create();
            File.WriteAllText(Path.Combine(templateOutput.FullName, "partial.txt"), "partial");
            return (1, string.Empty, "Template not installed.");
        };

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "Template not installed.");
        Assert.IsFalse(target.Exists);
    }

    [TestMethod]
    public async Task Migrate_MalformedTemplateRestoresExistingEmptyOutput()
    {
        var source = await CreateUwpSourceAsync("MalformedTemplateApp");
        var target = _tempDirectory.CreateSubdirectory("empty-output");
        FakeDotNet.RunDotnetCommandHandler = arguments =>
        {
            var templateOutput = GetTemplateOutput(arguments);
            templateOutput.Create();
            File.WriteAllText(Path.Combine(templateOutput.FullName, "partial.txt"), "partial");
            return (0, "Template claimed success.", string.Empty);
        };

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "did not produce the expected project files");
        Assert.IsTrue(target.Exists);
        Assert.IsFalse(target.EnumerateFileSystemInfos().Any());
    }

    [TestMethod]
    public async Task Migrate_QuietRemainsActiveAcrossAsyncTemplateCreation()
    {
        var source = await CreateUwpSourceAsync("QuietApp");
        await WriteAsync(source, "MainPage.xaml", "<Page x:Class=\"SDKTemplate.MainPage\" />");
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "quiet-output"));
        FakeDotNet.RunDotnetCommandAsyncHandler = async arguments =>
        {
            await Task.Yield();
            var templateOutput = GetTemplateOutput(arguments);
            templateOutput.Create();
            File.WriteAllText(Path.Combine(templateOutput.FullName, "QuietAppApp.csproj"), CleanCsproj);
            File.WriteAllText(Path.Combine(templateOutput.FullName, "App.xaml"),
                "<Application><Application.Resources><ResourceDictionary /></Application.Resources></Application>");
            File.WriteAllText(Path.Combine(templateOutput.FullName, "App.xaml.cs"), "class App { }");
            File.WriteAllText(Path.Combine(templateOutput.FullName, "MainWindow.xaml"), "<Window><Grid Grid.Row=\"1\" /></Window>");
            File.WriteAllText(Path.Combine(templateOutput.FullName, "MainWindow.xaml.cs"),
                "class MainWindow { MainWindow() { InitializeComponent(); } }");
            return (0, "Template created.", string.Empty);
        };

        var (exit, output) = await InvokeAsync(source, target, "--quiet");

        Assert.AreEqual(0, exit, output);
        Assert.IsFalse(output.Contains("MECHANICAL MIGRATION COMPLETE", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("Copied", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Migrate_NonEmptyOutputFailsBeforeTemplateCreation()
    {
        var source = await CreateUwpSourceAsync("ExistingOutputApp");
        var target = _tempDirectory.CreateSubdirectory("existing-output");
        await WriteAsync(target, "keep.txt", "existing");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "[ERROR] Output directory may contain only supported control-plane metadata");
        Assert.IsEmpty(FakeDotNet.CommandCalls);
        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, "keep.txt")));
    }

    [TestMethod]
    public async Task Migrate_MetadataOnlyOutputPreservesMetadataAndMergesScaffold()
    {
        var source = await CreateUwpSourceAsync("MetadataOutputApp");
        await WriteAsync(source, "MainPage.xaml", "<Page x:Class=\"SDKTemplate.MainPage\" />");
        var target = _tempDirectory.CreateSubdirectory("metadata-output");
        await WriteAsync(target, ".git\\HEAD", "ref: refs/heads/main\n");
        await WriteAsync(target, ".github\\instructions\\existing.md", "existing instructions");
        ArrangeTemplateCreation(target, "MetadataOutputApp", template =>
        {
            var instructions = Directory.CreateDirectory(Path.Combine(template.FullName, ".github", "instructions"));
            File.WriteAllText(Path.Combine(instructions.FullName, "template.md"), "template instructions");
        });

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        Assert.AreEqual("ref: refs/heads/main\n", await File.ReadAllTextAsync(
            Path.Combine(target.FullName, ".git", "HEAD"), TestContext.CancellationToken));
        Assert.AreEqual("existing instructions", await File.ReadAllTextAsync(
            Path.Combine(target.FullName, ".github", "instructions", "existing.md"), TestContext.CancellationToken));
        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, ".github", "instructions", "template.md")));
        Assert.IsFalse(Directory.Exists(Path.Combine(target.FullName, ".github", ".github")));
        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, "MetadataOutputApp.csproj")));

        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"), TestContext.CancellationToken));
        Assert.AreEqual(
            target.FullName.Replace('\\', '/'),
            report.RootElement.GetProperty("target").GetProperty("root").GetString());
    }

    [TestMethod]
    public async Task Migrate_MetadataConflictLeavesExistingOutputUnchanged()
    {
        var source = await CreateUwpSourceAsync("MetadataConflictApp");
        var target = _tempDirectory.CreateSubdirectory("metadata-conflict-output");
        await WriteAsync(target, ".git\\HEAD", "original head");
        await WriteAsync(target, ".github\\instructions\\conflict.md", "original instructions");
        ArrangeTemplateCreation(target, "MetadataConflictApp", template =>
        {
            var instructions = Directory.CreateDirectory(Path.Combine(template.FullName, ".github", "instructions"));
            File.WriteAllText(Path.Combine(instructions.FullName, "conflict.md"), "different instructions");
        });

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "conflicts with an existing metadata file");
        Assert.AreEqual("original head", await File.ReadAllTextAsync(
            Path.Combine(target.FullName, ".git", "HEAD"), TestContext.CancellationToken));
        Assert.AreEqual("original instructions", await File.ReadAllTextAsync(
            Path.Combine(target.FullName, ".github", "instructions", "conflict.md"), TestContext.CancellationToken));
        Assert.AreEqual(2, target.EnumerateFileSystemInfos().Count());
    }

    [TestMethod]
    public async Task Migrate_TemplateFailurePreservesMetadataOnlyOutput()
    {
        var source = await CreateUwpSourceAsync("MetadataFailureApp");
        var target = _tempDirectory.CreateSubdirectory("metadata-failure-output");
        await WriteAsync(target, ".git\\HEAD", "original head");
        await WriteAsync(target, ".github\\instructions\\existing.md", "original instructions");
        FakeDotNet.RunDotnetCommandHandler = arguments =>
        {
            var templateOutput = GetTemplateOutput(arguments);
            templateOutput.Create();
            File.WriteAllText(Path.Combine(templateOutput.FullName, "partial.txt"), "partial");
            return (1, string.Empty, "Template failed.");
        };

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "Template failed.");
        Assert.AreEqual("original head", await File.ReadAllTextAsync(
            Path.Combine(target.FullName, ".git", "HEAD"), TestContext.CancellationToken));
        Assert.AreEqual("original instructions", await File.ReadAllTextAsync(
            Path.Combine(target.FullName, ".github", "instructions", "existing.md"), TestContext.CancellationToken));
        Assert.AreEqual(2, target.EnumerateFileSystemInfos().Count());
    }

    [TestMethod]
    public async Task Migrate_SourceWithoutProjectFails()
    {
        var source = _tempDirectory.CreateSubdirectory("not-uwp");
        await WriteAsync(source, "MainPage.xaml", "<Page />");
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "invalid-source-output"));

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "[ERROR] Source is not a UWP project");
        Assert.IsEmpty(FakeDotNet.CommandCalls);
    }
}
