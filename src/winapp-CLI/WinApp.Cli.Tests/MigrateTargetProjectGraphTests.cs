// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public class MigrateTargetProjectGraphTests : MigrateCommandTestBase
{
    private static readonly string?[] ExpectedCollisionKinds =
        ["Compile", "None"];

    private static readonly string?[] ExpectedCollisionPaths =
        ["Nested/Model.cs"];

    private static readonly string[] ExpectedNestedProjects =
        ["App/Nested/Nested.csproj"];

    private static readonly string[] ExpectedExternalItemKinds =
        ["Content"];

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

    private void ArrangeTemplateCreation(
        string projectName,
        Action<DirectoryInfo>? customizeTemplate = null)
    {
        FakeDotNet.RunDotnetCommandHandler = arguments =>
        {
            var tokens = WindowsCommandLine.SplitArguments(arguments);
            var outputIndex = tokens.ToList().IndexOf("--output");
            Assert.IsTrue(
                outputIndex >= 0 && outputIndex + 1 < tokens.Count,
                arguments);
            var output = new DirectoryInfo(tokens[outputIndex + 1]);
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
            customizeTemplate?.Invoke(output);
            return (0, "Template created.", string.Empty);
        };
    }

    private async Task<(DirectoryInfo Target, string EntryProject)>
        CreateMigrationAsync(string name)
    {
        var source = _tempDirectory.CreateSubdirectory($"{name}-source");
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

        var (exitCode, output) = await InvokeCapturingConsoleAsync(
            GetRequiredService<MigrateCommand>(),
            source.FullName,
            "--output",
            target.FullName);

        Assert.AreEqual(0, exitCode, output);
        return (
            target,
            Path.Combine(target.FullName, $"{projectName}.csproj"));
    }

    private Task<(int ExitCode, string Output)> VerifyAsync(
        DirectoryInfo target) =>
        InvokeCapturingConsoleAsync(
            GetRequiredService<MigrateVerifyCommand>(),
            target.FullName);

    private async Task AddNestedProjectAsync(
        DirectoryInfo target,
        string relativeFile = @"Nested\Model.cs")
    {
        await WriteAsync(
            target,
            @"Nested\Nested.csproj",
            CleanCsproj);
        await WriteAsync(
            target,
            relativeFile,
            "namespace Nested; public sealed class Model { }");
    }

    private static MsBuildProjectItemEvaluation Evaluation(
        FileInfo project,
        params (string Kind, string FullPath)[] items) =>
        MsBuildProjectItemEvaluation.Success(items
            .Select(item => new MsBuildProjectItem(
                item.Kind,
                Path.GetRelativePath(
                    project.DirectoryName!,
                    item.FullPath),
                item.FullPath,
                project.FullName,
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)))
            .ToList());

    private async Task<JsonDocument> ReadReportAsync(
        DirectoryInfo target) =>
        JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"),
            TestContext.CancellationToken));

    [TestMethod]
    [TestCategory("MigrationAcceptance")]
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
        var target = new DirectoryInfo(Path.Combine(
            _tempDirectory.FullName,
            "GraphInitial-target"));
        ArrangeTemplateCreation(
            "GraphInitialApp",
            output =>
            {
                var nested = output.CreateSubdirectory("Nested");
                File.WriteAllText(
                    Path.Combine(nested.FullName, "Nested.csproj"),
                    CleanCsproj);
                File.WriteAllText(
                    Path.Combine(nested.FullName, "Nested.cs"),
                    "namespace Nested; public sealed class NestedType { }");
            });
        FakeMsBuildEvaluator.Handler = (project, _) =>
        {
            var nestedFile = Path.Combine(
                target.FullName,
                "Nested",
                "Nested.cs");
            return Task.FromResult(Evaluation(
                project,
                ("Compile", nestedFile)));
        };

        var (exitCode, output) = await InvokeCapturingConsoleAsync(
            GetRequiredService<MigrateCommand>(),
            source.FullName,
            "--output",
            target.FullName);

        Assert.AreEqual(1, exitCode, output);
        StringAssert.Contains(output, "target-project-graph");
        using var report = await ReadReportAsync(target);
        Assert.AreEqual(
            "failed",
            report.RootElement
                .GetProperty("mechanicalVerification")
                .GetProperty("targetProjectGraph")
                .GetProperty("status")
                .GetString());
        Assert.HasCount(2, FakeMsBuildEvaluator.EvaluatedProjects);
    }

    [TestMethod]
    public async Task Verify_BackfillsTargetGraphForExistingSchema13Report()
    {
        var (target, _) = await CreateMigrationAsync("GraphSchema13");
        var reportPath = Path.Combine(
            target.FullName,
            "migration-report.json");
        var report = JsonNode.Parse(await File.ReadAllTextAsync(
            reportPath,
            TestContext.CancellationToken))!;
        report["mechanicalVerification"]!
            .AsObject()
            .Remove("targetProjectGraph");
        await File.WriteAllTextAsync(
            reportPath,
            report.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true }),
            TestContext.CancellationToken);
        FakeMsBuildEvaluator.EvaluatedProjects.Clear();

        var (exitCode, output) = await VerifyAsync(target);

        Assert.AreEqual(0, exitCode, output);
        using var verifiedReport = await ReadReportAsync(target);
        Assert.AreEqual(
            "not-required",
            verifiedReport.RootElement
                .GetProperty("mechanicalVerification")
                .GetProperty("targetProjectGraph")
                .GetProperty("status")
                .GetString());
        Assert.HasCount(1, FakeMsBuildEvaluator.EvaluatedProjects);
    }

    [TestMethod]
    public async Task Verify_DetectsOnlySharedEvaluatedPhysicalFiles()
    {
        var (target, entryProject) =
            await CreateMigrationAsync("GraphCollision");
        await AddNestedProjectAsync(target);
        var nestedProject = Path.Combine(
            target.FullName,
            "Nested",
            "Nested.csproj");
        var model = Path.Combine(
            target.FullName,
            "Nested",
            "Model.cs");
        var entryOnly = Path.Combine(target.FullName, "MainWindow.xaml.cs");
        FakeMsBuildEvaluator.Handler = (project, _) =>
            Task.FromResult(project.FullName.Equals(
                    entryProject,
                    StringComparison.OrdinalIgnoreCase)
                ? Evaluation(
                    project,
                    ("Compile", model),
                    ("Compile", entryOnly))
                : Evaluation(
                    project,
                    ("None", model)));

        var (exitCode, output) = await VerifyAsync(target);

        Assert.AreEqual(1, exitCode, output);
        using var report = await ReadReportAsync(target);
        var issue = report.RootElement
            .GetProperty("mechanicalVerification")
            .GetProperty("targetProjectGraph")
            .GetProperty("issues")[0];
        Assert.AreEqual(
            "nested-project-default-item-collision",
            issue.GetProperty("kind").GetString());
        CollectionAssert.AreEquivalent(
            ExpectedCollisionKinds,
            issue.GetProperty("itemKinds")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToList());
        CollectionAssert.AreEqual(
            ExpectedCollisionPaths,
            issue.GetProperty("samplePaths")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToList());
        Assert.AreEqual(
            NormalizePath(Path.GetRelativePath(
                target.FullName,
                nestedProject)),
            report.RootElement
                .GetProperty("mechanicalVerification")
                .GetProperty("targetProjectGraph")
                .GetProperty("nestedProjects")[0]
                .GetString());
    }

    [TestMethod]
    public async Task Verify_DoesNotCollideWhenOnlyNestedProjectOwnsFile()
    {
        var (target, entryProject) =
            await CreateMigrationAsync("GraphNoCollision");
        await AddNestedProjectAsync(target);
        var model = Path.Combine(
            target.FullName,
            "Nested",
            "Model.cs");
        FakeMsBuildEvaluator.Handler = (project, _) =>
            Task.FromResult(project.FullName.Equals(
                    entryProject,
                    StringComparison.OrdinalIgnoreCase)
                ? Evaluation(project)
                : Evaluation(project, ("Compile", model)));

        var (exitCode, output) = await VerifyAsync(target);

        Assert.AreEqual(0, exitCode, output);
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
    public async Task Verify_EvaluationFailureFailsClosed()
    {
        var (target, _) =
            await CreateMigrationAsync("GraphEvaluationFailure");
        await AddNestedProjectAsync(target);
        FakeMsBuildEvaluator.Handler = (_, _) => Task.FromResult(
            MsBuildProjectItemEvaluation.Failure(
                "malformed item JSON"));

        var (exitCode, output) = await VerifyAsync(target);

        Assert.AreEqual(1, exitCode, output);
        using var report = await ReadReportAsync(target);
        var graph = report.RootElement
            .GetProperty("mechanicalVerification")
            .GetProperty("targetProjectGraph");
        Assert.AreEqual(
            "incomplete",
            graph.GetProperty("status").GetString());
        var issue = graph.GetProperty("issues")[0];
        Assert.AreEqual(
            "target-project-graph-incomplete",
            issue.GetProperty("kind").GetString());
        Assert.AreEqual(
            "review-required",
            issue.GetProperty("severity").GetString());
        StringAssert.Contains(
            issue.GetProperty("reason").GetString(),
            "malformed item JSON");
    }

    [TestMethod]
    public async Task Analyze_UntrustworthyContainedPathFailsClosed()
    {
        var target = _tempDirectory.CreateSubdirectory(
            "GraphUntrustedPath");
        await WriteAsync(target, "App.csproj", CleanCsproj);
        await AddNestedProjectAsync(target);
        FakeMsBuildEvaluator.Handler = (project, _) =>
            Task.FromResult(MsBuildProjectItemEvaluation.Success(
            [
                new MsBuildProjectItem(
                    "Compile",
                    "Model.cs",
                    "Model.cs",
                    project.FullName,
                    new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase))
            ]));

        var analysis =
            await MigrateCommand.Handler.AnalyzeTargetProjectGraphAsync(
                target.FullName,
                Path.Combine(target.FullName, "App.csproj"),
                FakeMsBuildEvaluator,
                TestContext.CancellationToken);

        Assert.AreEqual("incomplete", analysis.Status);
        Assert.AreEqual(
            "target-project-graph-incomplete",
            analysis.Issues[0].Kind);
    }

    [TestMethod]
    public async Task Analyze_ExternalItemUsesEvaluatedFullPathAndIdentity()
    {
        var target = _tempDirectory.CreateSubdirectory(
            "GraphExternalItem");
        await WriteAsync(target, "App.csproj", CleanCsproj);
        var external = _tempDirectory.CreateSubdirectory(
            "GraphExternalContent");
        var externalFile = Path.Combine(
            external.FullName,
            "Shared.json");
        await File.WriteAllTextAsync(
            externalFile,
            "{}",
            TestContext.CancellationToken);
        FakeMsBuildEvaluator.Handler = (project, _) =>
            Task.FromResult(Evaluation(
                project,
                ("Content", externalFile)));

        var analysis =
            await MigrateCommand.Handler.AnalyzeTargetProjectGraphAsync(
                target.FullName,
                Path.Combine(target.FullName, "App.csproj"),
                FakeMsBuildEvaluator,
                TestContext.CancellationToken);

        Assert.AreEqual("failed", analysis.Status);
        Assert.HasCount(1, FakeMsBuildEvaluator.EvaluatedProjects);
        var issue = analysis.Issues.Single();
        Assert.AreEqual("target-external-file-items", issue.Kind);
        CollectionAssert.AreEqual(
            ExpectedExternalItemKinds,
            issue.ItemKinds);
        CollectionAssert.AreEqual(
            new[]
            {
                NormalizePath(Path.GetRelativePath(
                    target.FullName,
                    externalFile))
            },
            issue.SamplePaths);
    }

    [TestMethod]
    public async Task Analyze_IgnoresRestoredPackageItemsOutsideTarget()
    {
        var target = _tempDirectory.CreateSubdirectory(
            "GraphRestoredPackageItem");
        await WriteAsync(target, "App.csproj", CleanCsproj);
        var packageRoot = _tempDirectory.CreateSubdirectory(
            "GraphPackageRoot");
        var packageTargets = Path.Combine(
            packageRoot.FullName,
            "buildTransitive",
            "Package.targets");
        var generatedSource = Path.Combine(
            packageRoot.FullName,
            "include",
            "Generated.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(packageTargets)!);
        Directory.CreateDirectory(Path.GetDirectoryName(generatedSource)!);
        await File.WriteAllTextAsync(
            packageTargets,
            "<Project />",
            TestContext.CancellationToken);
        await File.WriteAllTextAsync(
            generatedSource,
            "namespace Package; internal sealed class Generated { }",
            TestContext.CancellationToken);
        FakeMsBuildEvaluator.Handler = (_, _) =>
            Task.FromResult(MsBuildProjectItemEvaluation.Success(
            [
                new MsBuildProjectItem(
                    "Compile",
                    generatedSource,
                    generatedSource,
                    packageTargets,
                    new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase))
            ],
            [packageRoot.FullName]));

        var analysis =
            await MigrateCommand.Handler.AnalyzeTargetProjectGraphAsync(
                target.FullName,
                Path.Combine(target.FullName, "App.csproj"),
                FakeMsBuildEvaluator,
                TestContext.CancellationToken);

        Assert.AreEqual("not-required", analysis.Status);
        Assert.IsEmpty(analysis.Issues);
    }

    [TestMethod]
    public async Task Analyze_BroadRestoredRootDoesNotHideAppOwnedExternalItem()
    {
        var target = _tempDirectory.CreateSubdirectory(
            "GraphBroadRestoredRoot");
        var targetProject = Path.Combine(
            target.FullName,
            "App.csproj");
        await WriteAsync(target, "App.csproj", CleanCsproj);
        var external = _tempDirectory.CreateSubdirectory(
            "GraphBroadRootExternal");
        var externalFile = Path.Combine(
            external.FullName,
            "Shared.json");
        await File.WriteAllTextAsync(
            externalFile,
            "{}",
            TestContext.CancellationToken);
        FakeMsBuildEvaluator.Handler = (_, _) =>
            Task.FromResult(MsBuildProjectItemEvaluation.Success(
            [
                new MsBuildProjectItem(
                    "Content",
                    externalFile,
                    externalFile,
                    targetProject,
                    new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase))
            ],
            [_tempDirectory.FullName]));

        var analysis =
            await MigrateCommand.Handler.AnalyzeTargetProjectGraphAsync(
                target.FullName,
                targetProject,
                FakeMsBuildEvaluator,
                TestContext.CancellationToken);

        Assert.AreEqual("failed", analysis.Status);
        Assert.AreEqual(
            "target-external-file-items",
            analysis.Issues.Single().Kind);
    }

    [TestMethod]
    public async Task Analyze_PropagatesCancellation()
    {
        var target = _tempDirectory.CreateSubdirectory(
            "GraphCancellation");
        await WriteAsync(target, "App.csproj", CleanCsproj);
        await AddNestedProjectAsync(target);
        using var cancellation = new CancellationTokenSource();
        FakeMsBuildEvaluator.Handler = async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return MsBuildProjectItemEvaluation.Success([]);
        };

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
            MigrateCommand.Handler.AnalyzeTargetProjectGraphAsync(
                target.FullName,
                Path.Combine(target.FullName, "App.csproj"),
                FakeMsBuildEvaluator,
                cancellation.Token));
    }

    [TestMethod]
    [TestCategory("MigrationAcceptance")]
    public async Task Verify_ActiveExclusionsClearAndRemovedCoverageReopens()
    {
        var (target, entryProject) =
            await CreateMigrationAsync("GraphClosure");
        await AddNestedProjectAsync(target);
        var model = Path.Combine(
            target.FullName,
            "Nested",
            "Model.cs");
        var entryOwnsNestedFile = false;
        FakeMsBuildEvaluator.Handler = (project, _) =>
            Task.FromResult(project.FullName.Equals(
                    entryProject,
                    StringComparison.OrdinalIgnoreCase)
                ? Evaluation(
                    project,
                    entryOwnsNestedFile
                        ? [("Compile", model)]
                        : [])
                : Evaluation(project, ("Compile", model)));

        Assert.AreEqual(0, (await VerifyAsync(target)).ExitCode);

        entryOwnsNestedFile = true;
        Assert.AreEqual(1, (await VerifyAsync(target)).ExitCode);
        using (var reopenedReport = await ReadReportAsync(target))
        {
            Assert.AreEqual(
                1,
                reopenedReport.RootElement
                    .GetProperty("todos")
                    .EnumerateArray()
                    .Count(todo =>
                        todo.GetProperty("id").GetString()
                            == "UWMIG013"));
        }

        entryOwnsNestedFile = false;
        Assert.AreEqual(0, (await VerifyAsync(target)).ExitCode);
        using var closedReport = await ReadReportAsync(target);
        Assert.AreEqual(
            0,
            closedReport.RootElement
                .GetProperty("todos")
                .EnumerateArray()
                .Count(todo =>
                    todo.GetProperty("id").GetString()
                        == "UWMIG013"));
    }

    [TestMethod]
    public async Task Analyze_UsesEntryDirectoryScopeAndTargetRelativePaths()
    {
        var targetRoot = _tempDirectory.CreateSubdirectory(
            "EntrySubdirectoryTarget");
        var entryDirectory = targetRoot.CreateSubdirectory("App");
        var siblingDirectory = targetRoot.CreateSubdirectory("Shared");
        await WriteAsync(
            siblingDirectory,
            "Shared.csproj",
            CleanCsproj);
        await WriteAsync(
            entryDirectory,
            "App.csproj",
            CleanCsproj);
        await AddNestedProjectAsync(entryDirectory);
        var model = Path.Combine(
            entryDirectory.FullName,
            "Nested",
            "Model.cs");
        FakeMsBuildEvaluator.Handler = (project, _) =>
            Task.FromResult(Evaluation(
                project,
                ("Compile", model)));

        var analysis =
            await MigrateCommand.Handler.AnalyzeTargetProjectGraphAsync(
                targetRoot.FullName,
                Path.Combine(entryDirectory.FullName, "App.csproj"),
                FakeMsBuildEvaluator,
                TestContext.CancellationToken);

        Assert.AreEqual("failed", analysis.Status);
        CollectionAssert.AreEqual(
            ExpectedNestedProjects,
            analysis.NestedProjects);
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
    public async Task Verify_IgnoresExcludedDiscoveryDirectories()
    {
        var (target, _) = await CreateMigrationAsync("GraphIgnored");
        foreach (var directory in new[]
        {
            ".uwp-source",
            ".migration-evidence",
            ".git",
            ".github",
            ".vs",
            "bin",
            "obj"
        })
        {
            await WriteAsync(
                target,
                $@"{directory}\Ignored\Ignored.csproj",
                CleanCsproj);
        }
        FakeMsBuildEvaluator.EvaluatedProjects.Clear();

        var (exitCode, output) = await VerifyAsync(target);

        Assert.AreEqual(0, exitCode, output);
        Assert.HasCount(1, FakeMsBuildEvaluator.EvaluatedProjects);
    }

    [TestMethod]
    public async Task Verify_DoesNotTraverseNestedProjectJunction()
    {
        var (target, _) = await CreateMigrationAsync("GraphJunction");
        var external = _tempDirectory.CreateSubdirectory(
            "ExternalJunctionProject");
        await WriteAsync(
            external,
            "ExternalJunctionProject.csproj",
            CleanCsproj);
        var junction = Path.Combine(
            target.FullName,
            "LinkedProject");
        if (!TryCreateJunction(junction, external.FullName))
        {
            Assert.Inconclusive(
                "The host cannot create a Windows directory junction.");
            return;
        }

        try
        {
            FakeMsBuildEvaluator.EvaluatedProjects.Clear();
            var (exitCode, output) = await VerifyAsync(target);

            Assert.AreEqual(0, exitCode, output);
            Assert.HasCount(1, FakeMsBuildEvaluator.EvaluatedProjects);
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/');

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
