// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class ProjectRunServiceAotTests
{
    private DirectoryInfo _tempDirectory = null!;
    private readonly List<TestConsole> _consoles = [];

    [TestInitialize]
    public void Setup()
    {
        _tempDirectory = new DirectoryInfo(
            Path.Join(Path.GetTempPath(), $"ProjectRunAotTests_{Guid.NewGuid():N}"));
        _tempDirectory.Create();
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var console in _consoles)
        {
            console.Dispose();
        }
        _consoles.Clear();

        try
        {
            _tempDirectory.Delete(recursive: true);
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"Could not delete '{_tempDirectory.FullName}': {ex}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Debug.WriteLine($"Could not delete '{_tempDirectory.FullName}': {ex}");
        }
    }

    [TestMethod]
    public void PublishArguments_PreserveInputsAndAddRecipeOutputGroup()
    {
        var project = WriteProject();
        var options = new ProjectRunOptions(
            "Release",
            "arm64",
            "net10.0-windows10.0.26100.0",
            NoBuild: false,
            NoRestore: true,
            Properties: ["PublishAot=true", "Flavor=Retail"],
            Platform: "ARM64");

        var arguments = ProjectRunService.BuildAotPublishArguments(
            project,
            options,
            "minimal");

        CollectionAssert.Contains(arguments.ToList(), "publish");
        CollectionAssert.Contains(arguments.ToList(), "Release");
        CollectionAssert.Contains(arguments.ToList(), "win-arm64");
        CollectionAssert.Contains(arguments.ToList(), "--no-restore");
        CollectionAssert.Contains(arguments.ToList(), "-p:PublishAot=true");
        CollectionAssert.Contains(arguments.ToList(), "-p:Flavor=Retail");
        CollectionAssert.Contains(arguments.ToList(), "-p:Platform=ARM64");
        var withoutAotProperty = ProjectRunService.BuildAotPublishArguments(
            project,
            options with { Properties = ["Flavor=Retail"] },
            "minimal");
        CollectionAssert.DoesNotContain(
            withoutAotProperty.ToList(),
            "-p:PublishAot=true",
            "WinApp must not enable PublishAot implicitly.");
        var argumentList = arguments.ToList();
        Assert.IsTrue(
            argumentList.IndexOf("-p:IncludePublishItemsOutputGroup=true") >
            argumentList.IndexOf("-p:PublishAot=true"),
            "WinApp's recipe switch must win over a conflicting user property.");
        CollectionAssert.Contains(
            arguments.ToList(),
            "--getProperty:AppxPackageRecipe");

        var evaluation = WindowsCommandLine.SplitArguments(
            ProjectRunService.BuildEvaluateArguments(
                project,
                options with { Properties = ["_IsPublishing=false"] },
                aotPublishContext: true)).ToList();
        Assert.IsTrue(
            evaluation.IndexOf("-p:_IsPublishing=true") >
            evaluation.IndexOf("-p:_IsPublishing=false"));
        CollectionAssert.DoesNotContain(
            WindowsCommandLine.SplitArguments(
                ProjectRunService.BuildEvaluateArguments(project, options)).ToList(),
            "-p:_IsPublishing=true");
    }

    [TestMethod]
    public async Task PublishAot_FalseEffectiveValueFailsBeforePublish()
    {
        var project = WriteProject();
        var assets = WriteFile("obj\\project.assets.json", "{}");
        var properties = PropertyJson(
            project,
            assets,
            publishAot: false,
            packaging: "None");
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = _ => (0, properties, string.Empty),
        };
        var service = NewService(dotnet);

        var error = await Assert.ThrowsAsync<ProjectRunException>(() =>
            service.PublishAotAndResolveAsync(project, Options(), CancellationToken.None));

        StringAssert.Contains(error.Message, "<PublishAot>true</PublishAot>");
        StringAssert.Contains(error.Message, "-p PublishAot=true");
        Assert.AreEqual(0, dotnet.ArgumentListInvocations.Count);
    }

    [TestMethod]
    public async Task PublishAot_UnknownEvaluationLetsPublishSurfaceNoRestoreFailure()
    {
        var project = WriteProject();
        var properties = PropertyJson(
            project,
            new FileInfo(Path.Join(_tempDirectory.FullName, "obj", "missing.assets.json")),
            publishAot: false,
            packaging: "None");
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = _ => (0, properties, string.Empty),
            RunDotnetArgumentListHandler = _ =>
                (73, string.Empty, "NETSDK1004: project.assets.json was not found."),
        };
        var service = NewService(dotnet);

        var outcome = await service.PublishAotAndResolveAsync(
            project,
            Options(noRestore: true),
            CancellationToken.None);

        Assert.IsNull(outcome.Resolution);
        Assert.AreEqual(73, outcome.ExitCode);
        CollectionAssert.Contains(
            dotnet.ArgumentListInvocations.Single().ToList(),
            "--no-restore");
    }

    [TestMethod]
    public async Task PublishAot_UnpackagedUsesTargetNameInsteadOfStaleAssemblyName()
    {
        var project = WriteProject();
        var assets = WriteFile("obj\\project.assets.json", "{}");
        var publishDirectory = _tempDirectory.CreateSubdirectory("artifacts")
            .CreateSubdirectory("native");
        var executable = WriteFile(
            Path.GetRelativePath(
                _tempDirectory.FullName,
                Path.Join(publishDirectory.FullName, "NativeRunner.exe")),
            "native");
        WriteFile(
            Path.GetRelativePath(
                _tempDirectory.FullName,
                Path.Join(publishDirectory.FullName, "ManagedAssembly.exe")),
            "stale");
        var properties = PropertyJson(
            project,
            assets,
            publishAot: true,
            packaging: "None",
            publishDir: @"artifacts\native\",
            assemblyName: "ManagedAssembly",
            targetName: "NativeRunner");
        var dotnet = SuccessfulDotnet(properties);
        var service = NewService(dotnet);

        var outcome = await service.PublishAotAndResolveAsync(
            project,
            Options(),
            CancellationToken.None);

        var resolution = outcome.Resolution;
        Assert.IsNotNull(resolution);
        Assert.IsTrue(resolution.IsAot);
        Assert.AreEqual(ProjectPackaging.Unpackaged, resolution.Packaging);
        Assert.AreEqual(publishDirectory.FullName, resolution.TargetDir);
        Assert.AreEqual(executable.FullName, resolution.RunCommand);
        Assert.IsNull(resolution.RunArguments);
        Assert.IsNull(resolution.AppxManifestPath);
        Assert.IsNull(resolution.AppxRecipePath);
    }

    [TestMethod]
    public async Task PublishAot_UsesPropertiesReturnedByPublishTarget()
    {
        var project = WriteProject();
        var assets = WriteFile("obj\\project.assets.json", "{}");
        var targetPublishDirectory = _tempDirectory.CreateSubdirectory("target-publish");
        var executable = WriteFile("target-publish\\TargetValue.exe", "native");
        var preEvaluation = PropertyJson(
            project,
            assets,
            publishAot: true,
            packaging: "None",
            publishDir: "stale-evaluation",
            assemblyName: "StaleValue");
        var publishProperties = PropertyJson(
            project,
            assets,
            publishAot: true,
            packaging: "None",
            publishDir: targetPublishDirectory.FullName,
            assemblyName: "TargetValue");
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = _ => (0, preEvaluation, string.Empty),
            RunDotnetArgumentListHandler = _ =>
                (0, publishProperties, string.Empty),
        };
        var service = NewService(dotnet);

        var outcome = await service.PublishAotAndResolveAsync(
            project,
            Options(),
            CancellationToken.None);

        Assert.AreEqual(targetPublishDirectory.FullName, outcome.Resolution!.TargetDir);
        Assert.AreEqual(executable.FullName, outcome.Resolution.RunCommand);
    }

    [TestMethod]
    public async Task PublishAot_PackagedReturnsEvaluatedExternalManifestAndRecipe()
    {
        var project = WriteProject(platforms: "x64;ARM64");
        var assets = WriteFile("obj\\project.assets.json", "{}");
        var publishDirectory = _tempDirectory.CreateSubdirectory("publish");
        var executable = WriteFile("publish\\PackagedNative.exe", "native");
        WriteFile("publish\\ManagedAssembly.exe", "stale");
        var manifest = WriteFile("obj\\generated\\AppxManifest.xml", "<Package />");
        var recipe = WriteFile("obj\\generated\\Sample.build.appxrecipe", "<Project />");
        var properties = PropertyJson(
            project,
            assets,
            publishAot: true,
            packaging: "MSIX",
            publishDir: publishDirectory.FullName,
            assemblyName: "ManagedAssembly",
            targetName: "PackagedNative",
            manifest: manifest.FullName,
            recipe: recipe.FullName);
        var dotnet = SuccessfulDotnet(properties);
        var service = NewService(dotnet);

        var outcome = await service.PublishAotAndResolveAsync(
            project,
            Options(),
            CancellationToken.None);

        var resolution = outcome.Resolution;
        Assert.IsNotNull(resolution);
        Assert.AreEqual(ProjectPackaging.Packaged, resolution.Packaging);
        Assert.AreEqual(executable.FullName, resolution.RunCommand);
        Assert.AreEqual(manifest.FullName, resolution.AppxManifestPath);
        Assert.AreEqual(recipe.FullName, resolution.AppxRecipePath);
        CollectionAssert.Contains(
            dotnet.ArgumentListInvocations.Single().ToList(),
            "-p:IncludePublishItemsOutputGroup=true");
    }

    [TestMethod]
    public async Task PublishAot_MissingPackagedRecipeFailsWithoutFallback()
    {
        var project = WriteProject();
        var assets = WriteFile("obj\\project.assets.json", "{}");
        var publishDirectory = _tempDirectory.CreateSubdirectory("publish");
        WriteFile("publish\\Sample.exe", "native");
        var manifest = WriteFile("obj\\AppxManifest.xml", "<Package />");
        var properties = PropertyJson(
            project,
            assets,
            publishAot: true,
            packaging: "MSIX",
            publishDir: publishDirectory.FullName,
            manifest: manifest.FullName,
            recipe: Path.Join(_tempDirectory.FullName, "obj", "missing.appxrecipe"));
        var service = NewService(SuccessfulDotnet(properties));

        var error = await Assert.ThrowsExactlyAsync<ProjectRunException>(() =>
            service.PublishAotAndResolveAsync(project, Options(), CancellationToken.None));

        StringAssert.Contains(error.Message, "AppxPackageRecipe");
        StringAssert.Contains(error.Message, "was not produced");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task PublishAot_PublishConditionalValueIsAcceptedWithoutRunningTargetsDuringPreflight(
        bool restored)
    {
        var project = WriteProject(
            extraProperties:
                """<PublishAot Condition="'$(_IsPublishing)' == 'true'">true</PublishAot>""");
        var assets = new FileInfo(Path.Join(_tempDirectory.FullName, "obj", "project.assets.json"));
        if (restored)
        {
            WriteFile("obj\\project.assets.json", "{}");
        }

        var publishDirectory = _tempDirectory.CreateSubdirectory("conditional-publish");
        var executable = WriteFile("conditional-publish\\Conditional.exe", "native");
        var ordinaryProperties = PropertyJson(
            project,
            assets,
            publishAot: false,
            packaging: "None",
            publishDir: publishDirectory.FullName,
            assemblyName: "ManagedAssembly",
            targetName: "Conditional");
        var publishingProperties = PropertyJson(
            project,
            assets,
            publishAot: true,
            packaging: "None",
            publishDir: publishDirectory.FullName,
            assemblyName: "ManagedAssembly",
            targetName: "Conditional");
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = arguments =>
                arguments.Contains("-p:_IsPublishing=true", StringComparison.Ordinal)
                    ? (0, publishingProperties, string.Empty)
                    : (0, ordinaryProperties, string.Empty),
            RunDotnetArgumentListHandler = _ =>
                (0, publishingProperties, string.Empty),
        };
        var service = NewService(dotnet);

        var outcome = await service.PublishAotAndResolveAsync(
            project,
            Options(),
            CancellationToken.None);

        Assert.AreEqual(executable.FullName, outcome.Resolution!.RunCommand);
        Assert.AreEqual(1, dotnet.ArgumentListInvocations.Count);
        Assert.IsTrue(dotnet.StringInvocations.Any(arguments =>
            arguments.Contains("-p:_IsPublishing=true", StringComparison.Ordinal)));
        Assert.IsTrue(dotnet.StringInvocations.All(arguments =>
            arguments.StartsWith("msbuild ", StringComparison.Ordinal) &&
            !arguments.Contains("-t:", StringComparison.OrdinalIgnoreCase) &&
            !arguments.Contains("--target", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void PublishEnvironment_AddsInstalledVsWhereDirectoryOnce()
    {
        var installer = _tempDirectory.CreateSubdirectory("Installer");
        WriteFile("Installer\\vswhere.exe", string.Empty);
        var existing = Path.Join(_tempDirectory.FullName, "tools");

        var environment = ProjectRunService.BuildAotPublishEnvironment(
            existing,
            installer.FullName);

        Assert.IsNotNull(environment);
        Assert.AreEqual(
            $"{installer.FullName}{Path.PathSeparator}{existing}",
            environment["PATH"]);
        Assert.IsNull(ProjectRunService.BuildAotPublishEnvironment(
            environment["PATH"],
            installer.FullName));
    }

    private static FakeDotNetService SuccessfulDotnet(string properties) =>
        new()
        {
            RunDotnetCommandHandler = _ => (0, properties, string.Empty),
            RunDotnetArgumentListHandler = _ => (0, properties, string.Empty),
        };

    private ProjectRunService NewService(FakeDotNetService dotnet)
    {
        var console = new TestConsole();
        _consoles.Add(console);
        return new(
            dotnet,
            new ProjectDetectionService(
                NullLogger<ProjectDetectionService>.Instance,
                dotnet),
            new FakeCsWinRTMetadataShimService(),
            console,
            NullLogger<ProjectRunService>.Instance);
    }

    private static ProjectRunOptions Options(bool noRestore = false) =>
        new(
            "Debug",
            "x64",
            Framework: null,
            NoBuild: false,
            NoRestore: noRestore,
            Properties: []);

    private FileInfo WriteProject(
        string? platforms = null,
        string? extraProperties = null)
    {
        var platformElement = platforms is null
            ? string.Empty
            : $"<Platforms>{platforms}</Platforms>";
        return WriteFile(
            "Sample.csproj",
            $"""
             <Project Sdk="Microsoft.NET.Sdk">
               <PropertyGroup>
                 <OutputType>WinExe</OutputType>
                 <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
                 {platformElement}
                 {extraProperties}
               </PropertyGroup>
             </Project>
             """);
    }

    private FileInfo WriteFile(string relativePath, string contents)
    {
        var path = Path.GetFullPath(relativePath, _tempDirectory.FullName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return new FileInfo(path);
    }

    private static string PropertyJson(
        FileInfo project,
        FileInfo assets,
        bool publishAot,
        string packaging,
        string? publishDir = null,
        string assemblyName = "Sample",
        string? targetName = null,
        string? manifest = null,
        string? recipe = null)
    {
        var projectDirectory = project.DirectoryName!;
        var properties = new Dictionary<string, string>
        {
            ["MSBuildProjectDirectory"] = projectDirectory,
            ["TargetDir"] = Path.Join(projectDirectory, "bin"),
            ["PublishDir"] = publishDir ?? Path.Join(projectDirectory, "publish"),
            ["PublishAot"] = publishAot ? "true" : "false",
            ["AssemblyName"] = assemblyName,
            ["TargetName"] = targetName ?? assemblyName,
            ["OutputType"] = "WinExe",
            ["WindowsPackageType"] = packaging,
            ["WindowsAppSDKSelfContained"] = "true",
            ["ProjectAssetsFile"] = assets.FullName,
            ["RuntimeIdentifier"] = "win-x64",
            ["FinalAppxManifestName"] = manifest ?? string.Empty,
            ["AppxPackageRecipe"] = recipe ?? string.Empty,
        };
        return JsonSerializer.Serialize(new { Properties = properties });
    }
}
