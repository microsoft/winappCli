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
public class MigrateTargetProjectGraphTests : MigrateCommandTestBase
{
    private static readonly string[] ExpectedCollisionKinds =
    [
        "Compile",
        "Page",
        "ApplicationDefinition",
        "PRIResource",
        "Content",
        "None"
    ];

    private async Task WriteAsync(
        DirectoryInfo directory,
        string relativePath,
        string content)
    {
        var path = Path.Combine(
            directory.FullName,
            relativePath);
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            content,
            TestContext.CancellationToken);
    }

    private void ArrangeTemplateCreation(
        string projectName,
        Action<DirectoryInfo>? customizeTemplate = null)
    {
        FakeDotNet.RunDotnetCommandHandler = arguments =>
        {
            var tokens = WindowsCommandLine.SplitArguments(arguments);
            var outputIndex = tokens.ToList().IndexOf("--output");
            Assert.IsTrue(
                outputIndex >= 0
                && outputIndex + 1 < tokens.Count,
                arguments);
            var output = new DirectoryInfo(
                tokens[outputIndex + 1]);
            output.Create();
            File.WriteAllText(
                Path.Combine(
                    output.FullName,
                    $"{projectName}.csproj"),
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
            customizeTemplate?.Invoke(output);
            return (0, "Template created.", string.Empty);
        };
    }

    private async Task<(
        DirectoryInfo Target,
        string EntryProject,
        string ProjectName)> CreateMigrationAsync(
            string name)
    {
        var source = _tempDirectory.CreateSubdirectory(
            $"{name}-source");
        await WriteAsync(
            source,
            $"{name}.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>uap10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        await WriteAsync(
            source,
            "MainPage.xaml.cs",
            $"namespace {name}; public sealed class MainPage {{ }}");
        var target = new DirectoryInfo(Path.Combine(
            _tempDirectory.FullName,
            $"{name}-target"));
        var projectName = $"{name}App";
        ArrangeTemplateCreation(projectName);

        var (exitCode, output) =
            await InvokeCapturingConsoleAsync(
                GetRequiredService<MigrateCommand>(),
                source.FullName,
                "--output",
                target.FullName);

        Assert.AreEqual(0, exitCode, output);
        return (
            target,
            Path.Combine(
                target.FullName,
                $"{projectName}.csproj"),
            projectName);
    }

    private Task<(int ExitCode, string Output)> VerifyAsync(
        DirectoryInfo target) =>
        InvokeCapturingConsoleAsync(
            GetRequiredService<MigrateVerifyCommand>(),
            target.FullName);

    private async Task AddNestedProjectAsync(
        DirectoryInfo target,
        bool addExplicitContent = false)
    {
        await WriteAsync(
            target,
            @"Nested\Nested.csproj",
            CleanCsproj);
        await WriteAsync(
            target,
            @"Nested\Model.cs",
            "namespace Nested; public sealed class Model { }");
        await WriteAsync(
            target,
            @"Nested\View.xaml",
            "<Page x:Class=\"Nested.View\" />");
        await WriteAsync(
            target,
            @"Nested\App.xaml",
            "<Application x:Class=\"Nested.App\" />");
        await WriteAsync(
            target,
            @"Nested\Resources\en-us\Resources.resw",
            "<root />");
        await WriteAsync(
            target,
            @"Nested\Assets\data.json",
            """{"ownedBy":"Nested"}""");
        await WriteAsync(
            target,
            @"Nested\obj\Debug\net8.0\Generated.g.cs",
            "namespace Nested; internal sealed class Generated { }");
        await WriteAsync(
            target,
            @"Nested\bin\Debug\net8.0\Nested.runtimeconfig.json",
            """{"runtimeOptions":{}}""");

        if (addExplicitContent)
        {
            MutateProject(
                target,
                document =>
                {
                    var ns = document.Root!.Name.Namespace;
                    document.Root.Add(new XElement(
                        ns + "ItemGroup",
                        new XElement(
                            ns + "Content",
                            new XAttribute(
                                "Include",
                                @"Nested\Assets\**"))));
                });
        }
    }

    private static void MutateProject(
        DirectoryInfo target,
        Action<XDocument> mutation)
    {
        var project = target.GetFiles(
            "*.csproj",
            SearchOption.TopDirectoryOnly).Single();
        var document = XDocument.Load(
            project.FullName,
            LoadOptions.PreserveWhitespace);
        mutation(document);
        document.Save(
            project.FullName,
            SaveOptions.DisableFormatting);
    }

    private static void AddDefaultExclusionAndContentRemoval(
        DirectoryInfo target)
    {
        MutateProject(
            target,
            document =>
            {
                var ns = document.Root!.Name.Namespace;
                document.Root.AddFirst(new XElement(
                    ns + "PropertyGroup",
                    new XElement(
                        ns + "DefaultItemExcludes",
                        "$(DefaultItemExcludes);Nested\\**")));
                document.Root.Add(new XElement(
                    ns + "ItemGroup",
                    new XElement(
                        ns + "Content",
                        new XAttribute(
                            "Remove",
                            @"Nested\**"))));
            });
    }

    private async Task<JsonDocument> ReadReportAsync(
        DirectoryInfo target) =>
        JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(
                target.FullName,
                "migration-report.json"),
            TestContext.CancellationToken));

    [TestMethod]
    public async Task Migrate_DetectsNestedProjectCollisionBeforeBuild()
    {
        var source = _tempDirectory.CreateSubdirectory(
            "GraphInitial-source");
        await WriteAsync(
            source,
            "GraphInitial.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>uap10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        await WriteAsync(
            source,
            "MainPage.xaml.cs",
            "namespace GraphInitial; public sealed class MainPage { }");
        var target = new DirectoryInfo(Path.Combine(
            _tempDirectory.FullName,
            "GraphInitial-target"));
        ArrangeTemplateCreation(
            "GraphInitialApp",
            output =>
            {
                var nested = output.CreateSubdirectory("Nested");
                File.WriteAllText(
                    Path.Combine(
                        nested.FullName,
                        "Nested.csproj"),
                    CleanCsproj);
                File.WriteAllText(
                    Path.Combine(
                        nested.FullName,
                        "Nested.cs"),
                    "namespace Nested; public sealed class NestedType { }");
            });

        var (exitCode, output) =
            await InvokeCapturingConsoleAsync(
                GetRequiredService<MigrateCommand>(),
                source.FullName,
                "--output",
                target.FullName);

        Assert.AreEqual(1, exitCode, output);
        StringAssert.Contains(
            output,
            "target-project-graph");
        using var report = await ReadReportAsync(target);
        Assert.AreEqual(
            "failed",
            report.RootElement
                .GetProperty("mechanicalVerification")
                .GetProperty("targetProjectGraph")
                .GetProperty("status")
                .GetString());
    }

    [TestMethod]
    public async Task Verify_BackfillsTargetGraphForExistingSchema13Report()
    {
        var (target, _, _) =
            await CreateMigrationAsync("GraphSchema13");
        var reportPath = Path.Combine(
            target.FullName,
            "migration-report.json");
        var report = JsonNode.Parse(
            await File.ReadAllTextAsync(
                reportPath,
                TestContext.CancellationToken))!;
        report["mechanicalVerification"]!
            .AsObject()
            .Remove("targetProjectGraph");
        await File.WriteAllTextAsync(
            reportPath,
            report.ToJsonString(
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }),
            TestContext.CancellationToken);

        var (exitCode, output) = await VerifyAsync(target);

        Assert.AreEqual(0, exitCode, output);
        using var verifiedReport =
            await ReadReportAsync(target);
        Assert.AreEqual(
            "1.3",
            verifiedReport.RootElement
                .GetProperty("schemaVersion")
                .GetString());
        Assert.AreEqual(
            "not-required",
            verifiedReport.RootElement
                .GetProperty("mechanicalVerification")
                .GetProperty("targetProjectGraph")
                .GetProperty("status")
                .GetString());
    }

    [TestMethod]
    public async Task Verify_DetectsNestedSourceXamlResourceAndGeneratedCollisions()
    {
        var (target, _, _) =
            await CreateMigrationAsync("GraphCollision");
        await AddNestedProjectAsync(
            target,
            addExplicitContent: true);

        var (exitCode, output) = await VerifyAsync(target);

        Assert.AreEqual(1, exitCode, output);
        using var report = await ReadReportAsync(target);
        var verification = report.RootElement
            .GetProperty("mechanicalVerification");
        Assert.AreEqual(
            "failed",
            verification.GetProperty("status").GetString());
        var graph = verification.GetProperty(
            "targetProjectGraph");
        Assert.AreEqual(
            "failed",
            graph.GetProperty("status").GetString());
        Assert.AreEqual(
            "Nested/Nested.csproj",
            graph.GetProperty("nestedProjects")[0].GetString());
        var issue = graph.GetProperty("issues")
            .EnumerateArray()
            .Single(item =>
                item.GetProperty("kind").GetString()
                    == "nested-project-default-item-collision");
        var itemKinds = issue
            .GetProperty("itemKinds")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        CollectionAssert.IsSubsetOf(
            ExpectedCollisionKinds,
            itemKinds.ToList());
        var generatedPaths = issue
            .GetProperty("generatedPaths")
            .EnumerateArray()
            .Select(path => path.GetString())
            .ToList();
        Assert.IsTrue(generatedPaths.Any(path =>
            path!.Contains(
                "/obj/",
                StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(generatedPaths.Any(path =>
            path!.Contains(
                "/bin/",
                StringComparison.OrdinalIgnoreCase)));
        StringAssert.Contains(
            issue.GetProperty("requiredResolution").GetString(),
            "ProjectReference ownership");
        Assert.AreEqual(
            1,
            report.RootElement
                .GetProperty("todos")
                .EnumerateArray()
                .Count(todo =>
                    todo.GetProperty("id").GetString()
                        == "UWMIG013"));

        Assert.AreEqual(
            1,
            (await VerifyAsync(target)).ExitCode);
        using var repeatedReport =
            await ReadReportAsync(target);
        Assert.AreEqual(
            1,
            repeatedReport.RootElement
                .GetProperty("mechanicalVerification")
                .GetProperty("targetProjectGraph")
                .GetProperty("issues")
                .GetArrayLength());
        Assert.AreEqual(
            1,
            repeatedReport.RootElement
                .GetProperty("todos")
                .EnumerateArray()
                .Count(todo =>
                    todo.GetProperty("id").GetString()
                        == "UWMIG013"));
    }

    [TestMethod]
    public async Task Verify_DoesNotClaimOwnershipForNestedProjectExcludedSource()
    {
        var (target, _, _) =
            await CreateMigrationAsync("GraphNestedOwnership");
        await WriteAsync(
            target,
            @"Nested\Nested.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <Compile Remove="Excluded.cs" />
              </ItemGroup>
            </Project>
            """);
        await WriteAsync(
            target,
            @"Nested\Excluded.cs",
            "internal sealed class Excluded { }");

        var (exitCode, output) = await VerifyAsync(target);

        Assert.AreEqual(0, exitCode, output);
        using var report = await ReadReportAsync(target);
        var graph = report.RootElement
            .GetProperty("mechanicalVerification")
            .GetProperty("targetProjectGraph");
        Assert.AreEqual(
            "passed",
            graph.GetProperty("status").GetString());
        Assert.AreEqual(
            0,
            graph.GetProperty("issues").GetArrayLength());
    }

    [TestMethod]
    public async Task Verify_DefaultImageContentSurvivesNoneRemoval()
    {
        var (target, _, _) =
            await CreateMigrationAsync("GraphDefaultContent");
        await WriteAsync(
            target,
            @"Nested\Nested.csproj",
            CleanCsproj);
        await WriteAsync(
            target,
            @"Nested\Assets\logo.png",
            "image-bytes");
        MutateProject(
            target,
            document =>
            {
                var ns = document.Root!.Name.Namespace;
                document.Root.Add(new XElement(
                    ns + "ItemGroup",
                    new XElement(
                        ns + "None",
                        new XAttribute(
                            "Remove",
                            @"Nested\Assets\**"))));
            });

        var (collisionExit, collisionOutput) =
            await VerifyAsync(target);

        Assert.AreEqual(1, collisionExit, collisionOutput);
        using (var collisionReport =
            await ReadReportAsync(target))
        {
            var itemKinds = collisionReport.RootElement
                .GetProperty("mechanicalVerification")
                .GetProperty("targetProjectGraph")
                .GetProperty("issues")[0]
                .GetProperty("itemKinds")
                .EnumerateArray()
                .Select(kind => kind.GetString())
                .ToList();
            CollectionAssert.Contains(
                itemKinds,
                "Content");
            CollectionAssert.DoesNotContain(
                itemKinds,
                "None");
        }

        MutateProject(
            target,
            document =>
            {
                var ns = document.Root!.Name.Namespace;
                document.Root.AddFirst(new XElement(
                    ns + "PropertyGroup",
                    new XElement(
                        ns + "EnableDefaultWindowsAppSdkContentItems",
                        "false")));
            });

        var (disabledExit, disabledOutput) =
            await VerifyAsync(target);

        Assert.AreEqual(0, disabledExit, disabledOutput);
    }

    [TestMethod]
    public async Task Verify_WindowsAppSdkPriDefaultsDoNotDependOnUseWinUI()
    {
        var (target, _, _) =
            await CreateMigrationAsync("GraphDefaultPri");
        await WriteAsync(
            target,
            @"Nested\Nested.csproj",
            CleanCsproj);
        await WriteAsync(
            target,
            @"Nested\Resources\en-us\Resources.resw",
            "<root />");
        MutateProject(
            target,
            document =>
            {
                var ns = document.Root!.Name.Namespace;
                document.Descendants()
                    .Single(element =>
                        element.Name.LocalName == "UseWinUI")
                    .Value = "false";
                document.Root.Add(new XElement(
                    ns + "ItemGroup",
                    new XElement(
                        ns + "None",
                        new XAttribute(
                            "Remove",
                            @"Nested\Resources\**"))));
            });

        var (collisionExit, collisionOutput) =
            await VerifyAsync(target);

        Assert.AreEqual(1, collisionExit, collisionOutput);
        using (var collisionReport =
            await ReadReportAsync(target))
        {
            var itemKinds = collisionReport.RootElement
                .GetProperty("mechanicalVerification")
                .GetProperty("targetProjectGraph")
                .GetProperty("issues")[0]
                .GetProperty("itemKinds")
                .EnumerateArray()
                .Select(kind => kind.GetString())
                .ToList();
            CollectionAssert.Contains(
                itemKinds,
                "PRIResource");
            CollectionAssert.DoesNotContain(
                itemKinds,
                "None");
        }

        MutateProject(
            target,
            document =>
            {
                var ns = document.Root!.Name.Namespace;
                document.Root.AddFirst(new XElement(
                    ns + "PropertyGroup",
                    new XElement(
                        ns + "EnableDefaultWindowsAppSdkPRIResourceItems",
                        "false")));
            });

        var (disabledExit, disabledOutput) =
            await VerifyAsync(target);

        Assert.AreEqual(0, disabledExit, disabledOutput);
    }

    [TestMethod]
    public async Task Verify_ExcludedIncludeDoesNotRemoveSdkDefaultOwnership()
    {
        var (target, _, _) =
            await CreateMigrationAsync("GraphIncludeExclude");
        await WriteAsync(
            target,
            @"Nested\Nested.csproj",
            CleanCsproj);
        await WriteAsync(
            target,
            @"Nested\Model.cs",
            "namespace Nested; public sealed class Model { }");
        MutateProject(
            target,
            document =>
            {
                var ns = document.Root!.Name.Namespace;
                document.Root.Add(new XElement(
                    ns + "ItemGroup",
                    new XElement(
                        ns + "Compile",
                        new XAttribute(
                            "Include",
                            @"Nested\**"),
                        new XAttribute(
                            "Exclude",
                            @"Nested\Model.cs"))));
            });

        var (defaultExit, defaultOutput) =
            await VerifyAsync(target);

        Assert.AreEqual(1, defaultExit, defaultOutput);

        MutateProject(
            target,
            document =>
            {
                var ns = document.Root!.Name.Namespace;
                document.Root.AddFirst(new XElement(
                    ns + "PropertyGroup",
                    new XElement(
                        ns + "DefaultItemExcludes",
                        "$(DefaultItemExcludes);Nested\\**")));
            });

        var (excludedExit, excludedOutput) =
            await VerifyAsync(target);

        Assert.AreEqual(0, excludedExit, excludedOutput);
    }

    [TestMethod]
    public async Task Verify_ActiveExclusionsClearAndRemovedCoverageReopens()
    {
        var (target, entryProject, _) =
            await CreateMigrationAsync("GraphClosure");
        await AddNestedProjectAsync(
            target,
            addExplicitContent: true);
        AddDefaultExclusionAndContentRemoval(target);
        var reportPath = Path.Combine(
            target.FullName,
            "migration-report.json");
        var report = JsonNode.Parse(
            await File.ReadAllTextAsync(
                reportPath,
                TestContext.CancellationToken))!;
        report["validation"]!["parityStatus"] = "verified";
        report["todos"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "SEM900",
            ["category"] = "semantic-validation",
            ["priority"] = "required",
            ["summary"] = "Preserve semantic validation",
            ["reason"] = "Regression marker",
            ["status"] = "resolved",
            ["locations"] = new JsonArray()
        });
        await File.WriteAllTextAsync(
            reportPath,
            report.ToJsonString(
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }),
            TestContext.CancellationToken);

        var (closedExit, closedOutput) =
            await VerifyAsync(target);

        Assert.AreEqual(0, closedExit, closedOutput);
        using (var closedReport =
            await ReadReportAsync(target))
        {
            var root = closedReport.RootElement;
            Assert.AreEqual(
                "passed",
                root.GetProperty("mechanicalVerification")
                    .GetProperty("targetProjectGraph")
                    .GetProperty("status")
                    .GetString());
            Assert.IsFalse(root.GetProperty("todos")
                .EnumerateArray()
                .Any(todo =>
                    todo.GetProperty("id").GetString()
                        == "UWMIG013"));
            Assert.IsTrue(root.GetProperty("todos")
                .EnumerateArray()
                .Any(todo =>
                    todo.GetProperty("id").GetString()
                        == "SEM900"
                    && todo.GetProperty("status").GetString()
                        == "resolved"));
            Assert.AreEqual(
                "verified",
                root.GetProperty("validation")
                    .GetProperty("parityStatus")
                    .GetString());
        }

        var entryDocument = XDocument.Load(
            entryProject,
            LoadOptions.PreserveWhitespace);
        entryDocument.Descendants()
            .Single(element =>
                element.Name.LocalName.Equals(
                    "Content",
                    StringComparison.OrdinalIgnoreCase)
                && element.Attribute("Remove") is not null)
            .Remove();
        entryDocument.Save(
            entryProject,
            SaveOptions.DisableFormatting);

        var (reopenedExit, reopenedOutput) =
            await VerifyAsync(target);

        Assert.AreEqual(1, reopenedExit, reopenedOutput);
        using (var reopenedReport =
            await ReadReportAsync(target))
        {
            var graph = reopenedReport.RootElement
                .GetProperty("mechanicalVerification")
                .GetProperty("targetProjectGraph");
            Assert.AreEqual(
                "failed",
                graph.GetProperty("status").GetString());
            Assert.IsTrue(
                graph.GetProperty("issues")
                    .EnumerateArray()
                    .Single()
                    .GetProperty("itemKinds")
                    .EnumerateArray()
                    .Any(kind =>
                        kind.GetString() == "Content"));
            Assert.AreEqual(
                1,
                reopenedReport.RootElement
                    .GetProperty("todos")
                    .EnumerateArray()
                    .Count(todo =>
                        todo.GetProperty("id").GetString()
                            == "UWMIG013"));
        }

        MutateProject(
            target,
            document =>
            {
                var ns = document.Root!.Name.Namespace;
                document.Root.Add(new XElement(
                    ns + "ItemGroup",
                    new XElement(
                        ns + "Content",
                        new XAttribute(
                            "Remove",
                            @"Nested\**"))));
            });
        Assert.AreEqual(
            0,
            (await VerifyAsync(target)).ExitCode);
        Assert.AreEqual(
            0,
            (await VerifyAsync(target)).ExitCode);
        using var finalReport =
            await ReadReportAsync(target);
        Assert.AreEqual(
            0,
            finalReport.RootElement
                .GetProperty("todos")
                .EnumerateArray()
                .Count(todo =>
                    todo.GetProperty("id").GetString()
                        == "UWMIG013"));
    }

    [TestMethod]
    public async Task Verify_UsesPropsAndPostDefaultImportedRemovalsInOrder()
    {
        var (target, _, projectName) =
            await CreateMigrationAsync("GraphOrdering");
        await AddNestedProjectAsync(target);
        await WriteAsync(
            target,
            "Directory.Build.props",
            """
            <Project>
              <ItemGroup>
                <Compile Remove="Nested\**" />
                <Page Remove="Nested\**" />
                <ApplicationDefinition Remove="Nested\**" />
                <PRIResource Remove="Nested\**" />
                <Content Remove="Nested\**" />
                <None Remove="Nested\**" />
              </ItemGroup>
            </Project>
            """);

        var (earlyRemovalExit, earlyRemovalOutput) =
            await VerifyAsync(target);

        Assert.AreEqual(
            1,
            earlyRemovalExit,
            earlyRemovalOutput);
        await WriteAsync(
            target,
            "Directory.Build.props",
            $$"""
            <Project>
              <Choose>
                <When Condition="'$(MSBuildProjectName)' == '{{projectName}}'">
                  <PropertyGroup>
                    <DefaultItemExcludes>$(DefaultItemExcludes);.\Nested\**</DefaultItemExcludes>
                  </PropertyGroup>
                </When>
              </Choose>
            </Project>
            """);

        var (propsExit, propsOutput) =
            await VerifyAsync(target);

        Assert.AreEqual(0, propsExit, propsOutput);
        MutateProject(
            target,
            document =>
            {
                var ns = document.Root!.Name.Namespace;
                document.Root.Add(new XElement(
                    ns + "PropertyGroup",
                    new XElement(
                        ns + "DefaultItemExcludes",
                        @"bin\**;obj\**")));
            });

        var (overrideExit, overrideOutput) =
            await VerifyAsync(target);

        Assert.AreEqual(1, overrideExit, overrideOutput);
        MutateProject(
            target,
            document =>
            {
                document.Root!.Elements()
                    .Last(element =>
                        element.Name.LocalName
                            == "PropertyGroup"
                        && element.Elements().Any(property =>
                            property.Name.LocalName
                                == "DefaultItemExcludes"))
                    .Remove();
            });

        File.Delete(Path.Combine(
            target.FullName,
            "Directory.Build.props"));
        await WriteAsync(
            target,
            "Directory.Build.targets",
            """
            <Project>
              <PropertyGroup>
                <DefaultItemExcludes>$(DefaultItemExcludes);Nested\**</DefaultItemExcludes>
              </PropertyGroup>
            </Project>
            """);

        var (latePropertyExit, latePropertyOutput) =
            await VerifyAsync(target);

        Assert.AreEqual(
            1,
            latePropertyExit,
            latePropertyOutput);
        await WriteAsync(
            target,
            "NestedOwnership.targets",
            """
            <Project>
              <ItemGroup>
                <Compile Remove="Nested\**" />
                <Page Remove="Nested\**" />
                <ApplicationDefinition Remove="Nested\**" />
                <PRIResource Remove="Nested\**" />
                <Content Remove="Nested\**" />
                <None Remove="Nested\**" />
              </ItemGroup>
            </Project>
            """);
        await WriteAsync(
            target,
            "Directory.Build.targets",
            """
            <Project>
              <Import Project="NestedOwnership.targets" />
            </Project>
            """);

        var (importExit, importOutput) =
            await VerifyAsync(target);

        Assert.AreEqual(0, importExit, importOutput);
        using var report = await ReadReportAsync(target);
        Assert.AreEqual(
            "passed",
            report.RootElement
                .GetProperty("mechanicalVerification")
                .GetProperty("targetProjectGraph")
                .GetProperty("status")
                .GetString());
    }

    [TestMethod]
    public async Task Verify_ChooseSelectionControlsActiveNestedItemRemovals()
    {
        var (target, _, projectName) =
            await CreateMigrationAsync("GraphChoose");
        await AddNestedProjectAsync(target);
        var targetsPath = Path.Combine(
            target.FullName,
            "Directory.Build.targets");
        await WriteAsync(
            target,
            "Directory.Build.targets",
            $$"""
            <Project>
              <Choose>
                <When Condition="'$(MSBuildProjectName)' == '{{projectName}}'">
                  <PropertyGroup>
                    <UnrelatedProperty>true</UnrelatedProperty>
                  </PropertyGroup>
                </When>
                <Otherwise>
                  <ItemGroup>
                    <Compile Remove="Nested\**" />
                    <Page Remove="Nested\**" />
                    <ApplicationDefinition Remove="Nested\**" />
                    <PRIResource Remove="Nested\**" />
                    <Content Remove="Nested\**" />
                    <None Remove="Nested\**" />
                  </ItemGroup>
                </Otherwise>
              </Choose>
            </Project>
            """);

        var (inactiveOtherwiseExit, inactiveOtherwiseOutput) =
            await VerifyAsync(target);

        Assert.AreEqual(
            1,
            inactiveOtherwiseExit,
            inactiveOtherwiseOutput);

        await File.WriteAllTextAsync(
            targetsPath,
            """
            <Project>
              <Choose>
                <When Condition="'$(MSBuildProjectName)' == 'OtherOne'">
                  <PropertyGroup>
                    <UnrelatedProperty>one</UnrelatedProperty>
                  </PropertyGroup>
                </When>
                <When Condition="'$(MSBuildProjectName)' == 'OtherTwo'">
                  <PropertyGroup>
                    <UnrelatedProperty>two</UnrelatedProperty>
                  </PropertyGroup>
                </When>
                <Otherwise>
                  <ItemGroup>
                    <Compile Remove="Nested\**" />
                    <Page Remove="Nested\**" />
                    <ApplicationDefinition Remove="Nested\**" />
                    <PRIResource Remove="Nested\**" />
                    <Content Remove="Nested\**" />
                    <None Remove="Nested\**" />
                  </ItemGroup>
                </Otherwise>
              </Choose>
            </Project>
            """,
            TestContext.CancellationToken);

        var (otherwiseExit, otherwiseOutput) =
            await VerifyAsync(target);

        Assert.AreEqual(0, otherwiseExit, otherwiseOutput);

        await File.WriteAllTextAsync(
            targetsPath,
            """
            <Project>
              <Choose>
                <When Condition="'$(Configuration)' == 'Debug'">
                  <ItemGroup>
                    <Compile Remove="Nested\**" />
                  </ItemGroup>
                </When>
                <Otherwise>
                  <ItemGroup>
                    <Compile Remove="Nested\**" />
                    <Page Remove="Nested\**" />
                    <ApplicationDefinition Remove="Nested\**" />
                    <PRIResource Remove="Nested\**" />
                    <Content Remove="Nested\**" />
                    <None Remove="Nested\**" />
                  </ItemGroup>
                </Otherwise>
              </Choose>
            </Project>
            """,
            TestContext.CancellationToken);

        var (unknownExit, unknownOutput) =
            await VerifyAsync(target);

        Assert.AreEqual(1, unknownExit, unknownOutput);
        using var report = await ReadReportAsync(target);
        Assert.AreEqual(
            "incomplete",
            report.RootElement
                .GetProperty("mechanicalVerification")
                .GetProperty("targetProjectGraph")
                .GetProperty("status")
                .GetString());
    }

    [TestMethod]
    public async Task Verify_DirectoryBuildTargetsControlsUseOrderedContainedEvidence()
    {
        var (target, entryProject, _) =
            await CreateMigrationAsync("GraphTargetsControl");
        await AddNestedProjectAsync(target);
        const string ownershipTargets = """
            <Project>
              <ItemGroup>
                <Compile Remove="Nested\**" />
                <Page Remove="Nested\**" />
                <ApplicationDefinition Remove="Nested\**" />
                <PRIResource Remove="Nested\**" />
                <Content Remove="Nested\**" />
                <None Remove="Nested\**" />
              </ItemGroup>
            </Project>
            """;
        await WriteAsync(
            target,
            "Directory.Build.targets",
            ownershipTargets);
        MutateProject(
            target,
            document =>
            {
                var ns = document.Root!.Name.Namespace;
                document.Root.Add(new XElement(
                    ns + "PropertyGroup",
                    new XElement(
                        ns + "ImportDirectoryBuildTargets",
                        "false"),
                    new XElement(
                        ns + "ImportDirectoryBuildTargets",
                        "true")));
            });

        var (reenabledExit, reenabledOutput) =
            await VerifyAsync(target);

        Assert.AreEqual(0, reenabledExit, reenabledOutput);

        var document = XDocument.Load(
            entryProject,
            LoadOptions.PreserveWhitespace);
        document.Descendants()
            .Last(element =>
                element.Name.LocalName
                    == "ImportDirectoryBuildTargets")
            .Remove();
        document.Save(
            entryProject,
            SaveOptions.DisableFormatting);

        var (disabledExit, disabledOutput) =
            await VerifyAsync(target);

        Assert.AreEqual(1, disabledExit, disabledOutput);

        document = XDocument.Load(
            entryProject,
            LoadOptions.PreserveWhitespace);
        document.Descendants()
            .Where(element =>
                element.Name.LocalName
                    == "ImportDirectoryBuildTargets")
            .Select(element => element.Parent)
            .Where(element => element is not null)
            .Cast<XElement>()
            .Distinct()
            .Remove();
        document.Root!.Add(new XElement(
            document.Root.Name.Namespace + "PropertyGroup",
            new XElement(
                document.Root.Name.Namespace
                    + "DirectoryBuildTargetsPath",
                @".\build\NestedOwnership.targets")));
        document.Save(
            entryProject,
            SaveOptions.DisableFormatting);
        File.Delete(Path.Combine(
            target.FullName,
            "Directory.Build.targets"));
        await WriteAsync(
            target,
            @"build\NestedOwnership.targets",
            ownershipTargets);

        var (overrideExit, overrideOutput) =
            await VerifyAsync(target);

        Assert.AreEqual(0, overrideExit, overrideOutput);

        document = XDocument.Load(
            entryProject,
            LoadOptions.PreserveWhitespace);
        document.Descendants()
            .Single(element =>
                element.Name.LocalName
                    == "DirectoryBuildTargetsPath")
            .Value = "$(NestedOwnershipTargets)";
        document.Save(
            entryProject,
            SaveOptions.DisableFormatting);

        var (uncertainExit, uncertainOutput) =
            await VerifyAsync(target);

        Assert.AreEqual(1, uncertainExit, uncertainOutput);
        using var report = await ReadReportAsync(target);
        Assert.AreEqual(
            "incomplete",
            report.RootElement
                .GetProperty("mechanicalVerification")
                .GetProperty("targetProjectGraph")
                .GetProperty("status")
                .GetString());
    }

    [TestMethod]
    public async Task Verify_LockedProjectEvidenceBecomesIncompleteAndRecovers()
    {
        var (target, _, _) =
            await CreateMigrationAsync("GraphLockedEvidence");
        await AddNestedProjectAsync(target);
        var targetsPath = Path.Combine(
            target.FullName,
            "Directory.Build.targets");
        await WriteAsync(
            target,
            "Directory.Build.targets",
            """
            <Project>
              <ItemGroup>
                <Compile Remove="Nested\**" />
                <Page Remove="Nested\**" />
                <ApplicationDefinition Remove="Nested\**" />
                <PRIResource Remove="Nested\**" />
                <Content Remove="Nested\**" />
                <None Remove="Nested\**" />
              </ItemGroup>
            </Project>
            """);

        int lockedExit;
        string lockedOutput;
        await using (var heldFile = new FileStream(
            targetsPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None))
        {
            (lockedExit, lockedOutput) =
                await VerifyAsync(target);
        }

        Assert.AreEqual(1, lockedExit, lockedOutput);
        using (var lockedReport =
            await ReadReportAsync(target))
        {
            Assert.AreEqual(
                "incomplete",
                lockedReport.RootElement
                    .GetProperty("mechanicalVerification")
                    .GetProperty("targetProjectGraph")
                    .GetProperty("status")
                    .GetString());
        }

        var (recoveredExit, recoveredOutput) =
            await VerifyAsync(target);

        Assert.AreEqual(0, recoveredExit, recoveredOutput);
    }

    [TestMethod]
    public async Task Verify_LockedResidualFileIsRecordedWithoutAborting()
    {
        var (target, _, _) =
            await CreateMigrationAsync("GraphLockedResidual");
        var lockedPath = Path.Combine(
            target.FullName,
            "Locked.cs");
        await File.WriteAllTextAsync(
            lockedPath,
            "namespace GraphLockedResidual; internal sealed class Locked { }",
            TestContext.CancellationToken);

        int lockedExit;
        string lockedOutput;
        await using (var heldFile = new FileStream(
            lockedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None))
        {
            (lockedExit, lockedOutput) =
                await VerifyAsync(target);
        }

        Assert.AreEqual(0, lockedExit, lockedOutput);
        using (var lockedReport =
            await ReadReportAsync(target))
        {
            Assert.IsTrue(
                lockedReport.RootElement
                    .GetProperty("mechanicalVerification")
                    .GetProperty("uninspectedFiles")
                    .GetInt32() > 0);
        }

        Assert.AreEqual(
            0,
            (await VerifyAsync(target)).ExitCode);
        using var recoveredReport =
            await ReadReportAsync(target);
        Assert.AreEqual(
            0,
            recoveredReport.RootElement
                .GetProperty("mechanicalVerification")
                .GetProperty("uninspectedFiles")
                .GetInt32());
    }

    [TestMethod]
    public async Task Verify_UncertainExclusionsNeverProduceFalseSuccess()
    {
        var (target, entryProject, _) =
            await CreateMigrationAsync("GraphUncertain");
        await AddNestedProjectAsync(target);

        var document = XDocument.Load(
            entryProject,
            LoadOptions.PreserveWhitespace);
        var ns = document.Root!.Name.Namespace;
        document.Root.AddFirst(new XElement(
            ns + "PropertyGroup",
            new XAttribute(
                "Condition",
                "'$(Configuration)' == 'Debug'"),
            new XElement(
                ns + "DefaultItemExcludes",
                "$(DefaultItemExcludes);Nested\\**")));
        document.Save(
            entryProject,
            SaveOptions.DisableFormatting);

        var (conditionExit, conditionOutput) =
            await VerifyAsync(target);

        Assert.AreEqual(1, conditionExit, conditionOutput);
        using (var conditionReport =
            await ReadReportAsync(target))
        {
            Assert.AreEqual(
                "incomplete",
                conditionReport.RootElement
                    .GetProperty("mechanicalVerification")
                    .GetProperty("targetProjectGraph")
                    .GetProperty("status")
                    .GetString());
        }

        document = XDocument.Load(
            entryProject,
            LoadOptions.PreserveWhitespace);
        document.Descendants()
            .Where(element =>
                element.Name.LocalName == "PropertyGroup"
                && element.Attribute("Condition") is not null)
            .Remove();
        document.Root!.AddFirst(new XElement(
            ns + "PropertyGroup",
            new XElement(
                ns + "DefaultItemExcludes",
                "$(DefaultItemExcludes);$(NestedRoot)\\**")));
        document.Save(
            entryProject,
            SaveOptions.DisableFormatting);

        var (expandedExit, expandedOutput) =
            await VerifyAsync(target);

        Assert.AreEqual(1, expandedExit, expandedOutput);
        using (var expandedReport =
            await ReadReportAsync(target))
        {
            Assert.AreEqual(
                "incomplete",
                expandedReport.RootElement
                    .GetProperty("mechanicalVerification")
                    .GetProperty("targetProjectGraph")
                    .GetProperty("status")
                    .GetString());
        }

        document = XDocument.Load(
            entryProject,
            LoadOptions.PreserveWhitespace);
        document.Descendants()
            .Where(element =>
                element.Name.LocalName
                    == "DefaultItemExcludes")
            .Select(element => element.Parent)
            .Where(element => element is not null)
            .Cast<XElement>()
            .Remove();
        document.Root!.AddFirst(new XElement(
            ns + "Choose",
            new XElement(
                ns + "When",
                new XAttribute(
                    "Condition",
                    "'$(MSBuildProjectName)' == 'OtherProject'"),
                new XElement(
                    ns + "PropertyGroup",
                    new XElement(
                        ns + "DefaultItemExcludes",
                        "$(DefaultItemExcludes);Nested\\**")))));
        document.Save(
            entryProject,
            SaveOptions.DisableFormatting);

        var (falseBranchExit, falseBranchOutput) =
            await VerifyAsync(target);

        Assert.AreEqual(
            1,
            falseBranchExit,
            falseBranchOutput);
        using (var falseBranchReport =
            await ReadReportAsync(target))
        {
            Assert.AreEqual(
                "failed",
                falseBranchReport.RootElement
                    .GetProperty("mechanicalVerification")
                    .GetProperty("targetProjectGraph")
                    .GetProperty("status")
                    .GetString());
        }

        document = XDocument.Load(
            entryProject,
            LoadOptions.PreserveWhitespace);
        document.Descendants()
            .Where(element =>
                element.Name.LocalName == "Choose")
            .Remove();
        document.Root!.AddFirst(new XElement(
            ns + "PropertyGroup",
            new XElement(
                ns + "DefaultItemExcludes",
                "$(DefaultItemExcludes);Nested\\**")));
        document.Root.Add(new XElement(
            ns + "Choose",
            new XElement(
                ns + "When",
                new XAttribute(
                    "Condition",
                    "'$(MSBuildProjectName)' == 'OtherProject'"),
                new XElement(
                    ns + "PropertyGroup",
                    new XElement(
                        ns + "ImportDirectoryBuildTargets",
                        "false"))),
            new XElement(
                ns + "When",
                new XAttribute(
                    "Condition",
                    "'$(Configuration)' == 'Debug'"),
                new XElement(
                    ns + "PropertyGroup",
                    new XElement(
                        ns + "UnrelatedProperty",
                        "value")))));
        document.Save(
            entryProject,
            SaveOptions.DisableFormatting);

        var (benignExit, benignOutput) =
            await VerifyAsync(target);

        Assert.AreEqual(0, benignExit, benignOutput);
    }

    [TestMethod]
    public async Task Verify_IgnoresControlProjectsOrdinaryDirectoriesAndExternalReferences()
    {
        var (target, _, _) =
            await CreateMigrationAsync("GraphIgnored");
        foreach (var directory in new[]
        {
            ".uwp-source",
            ".migration-evidence",
            ".git",
            ".github",
            "obj"
        })
        {
            await WriteAsync(
                target,
                $@"{directory}\Ignored\Ignored.csproj",
                CleanCsproj);
            await WriteAsync(
                target,
                $@"{directory}\Ignored\Ignored.cs",
                "internal sealed class Ignored { }");
        }
        await WriteAsync(
            target,
            @"Ordinary\Ordinary.cs",
            "internal sealed class Ordinary { }");
        var external = _tempDirectory.CreateSubdirectory(
            "ExternalSibling");
        await WriteAsync(
            external,
            "ExternalSibling.csproj",
            CleanCsproj);
        await WriteAsync(
            external,
            "External.cs",
            "internal sealed class External { }");
        MutateProject(
            target,
            document =>
            {
                var ns = document.Root!.Name.Namespace;
                document.Root.Add(new XElement(
                    ns + "ItemGroup",
                    new XElement(
                        ns + "ProjectReference",
                        new XAttribute(
                            "Include",
                            Path.GetRelativePath(
                                target.FullName,
                                Path.Combine(
                                    external.FullName,
                                    "ExternalSibling.csproj"))))));
            });

        var (exitCode, output) = await VerifyAsync(target);

        Assert.AreEqual(0, exitCode, output);
        using var report = await ReadReportAsync(target);
        var graph = report.RootElement
            .GetProperty("mechanicalVerification")
            .GetProperty("targetProjectGraph");
        Assert.AreEqual(
            "not-required",
            graph.GetProperty("status").GetString());
        Assert.AreEqual(
            0,
            graph.GetProperty("nestedProjects")
                .GetArrayLength());
    }

    [TestMethod]
    public async Task Analyze_UsesEntryDirectoryScopeAndTargetRelativeEvidencePaths()
    {
        var targetRoot = _tempDirectory.CreateSubdirectory(
            "EntrySubdirectoryTarget");
        var entryDirectory =
            targetRoot.CreateSubdirectory("App");
        var siblingDirectory =
            targetRoot.CreateSubdirectory("Shared");
        await WriteAsync(
            siblingDirectory,
            "Shared.csproj",
            CleanCsproj);
        await WriteAsync(
            siblingDirectory,
            "Shared.cs",
            "namespace Shared; public sealed class SharedType { }");
        await WriteAsync(
            entryDirectory,
            "App.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
                <UseWinUI>true</UseWinUI>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\Shared\Shared.csproj" />
              </ItemGroup>
            </Project>
            """);
        await WriteAsync(
            entryDirectory,
            @"Nested\Nested.csproj",
            CleanCsproj);
        await WriteAsync(
            entryDirectory,
            @"Nested\Model.cs",
            "namespace Nested; public sealed class Model { }");

        var analysis =
            MigrateCommand.Handler.AnalyzeTargetProjectGraph(
                targetRoot.FullName,
                Path.Combine(
                    entryDirectory.FullName,
                    "App.csproj"));

        Assert.AreEqual("failed", analysis.Status);
        Assert.HasCount(
            1,
            analysis.NestedProjects);
        Assert.AreEqual(
            "App/Nested/Nested.csproj",
            analysis.NestedProjects[0]);
        Assert.AreEqual(1, analysis.Issues.Count);
        Assert.AreEqual(
            "App/Nested",
            analysis.Issues[0].NestedDirectory);
        CollectionAssert.Contains(
            analysis.Issues[0].SamplePaths,
            "App/Nested/Model.cs");
        Assert.IsFalse(analysis.NestedProjects.Any(path =>
            path.Contains(
                "Shared",
                StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task Verify_DoesNotTraverseNestedProjectJunction()
    {
        var (target, _, _) =
            await CreateMigrationAsync("GraphJunction");
        var external = _tempDirectory.CreateSubdirectory(
            "ExternalJunctionProject");
        await WriteAsync(
            external,
            "ExternalJunctionProject.csproj",
            CleanCsproj);
        var externalFile = Path.Combine(
            external.FullName,
            "External.cs");
        await File.WriteAllTextAsync(
            externalFile,
            "using Windows.UI.Xaml; internal sealed class External { }",
            TestContext.CancellationToken);
        var before = await File.ReadAllTextAsync(
            externalFile,
            TestContext.CancellationToken);
        var junction = Path.Combine(
            target.FullName,
            "LinkedProject");
        if (!TryCreateJunction(
                junction,
                external.FullName))
        {
            Assert.Inconclusive(
                "The host cannot create a Windows directory junction.");
            return;
        }

        try
        {
            var (exitCode, output) =
                await VerifyAsync(target);

            Assert.AreEqual(0, exitCode, output);
            using var report = await ReadReportAsync(target);
            Assert.AreEqual(
                "not-required",
                report.RootElement
                    .GetProperty("mechanicalVerification")
                    .GetProperty("targetProjectGraph")
                    .GetProperty("status")
                    .GetString());
            Assert.AreEqual(
                before,
                await File.ReadAllTextAsync(
                    externalFile,
                    TestContext.CancellationToken));
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    private static bool TryCreateJunction(
        string link,
        string target)
    {
        try
        {
            var startInfo =
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments =
                        $"/c mklink /J \"{link}\" \"{target}\"",
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
}
