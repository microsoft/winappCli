// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="ManifestGenerateCommand"/> exercising the real <see cref="ManifestService"/>:
/// option parsing, successful generation for both templates, and the if-exists
/// Error/Skip/Overwrite branches. The generation failure branch is covered separately in
/// <see cref="ManifestGenerateCommandFailureTests"/> via a throwing fake.
/// </summary>
[TestClass]
public class ManifestGenerateCommandTests : BaseCommandTests
{
    private const string PlaceholderManifest = "<Package>PLACEHOLDER</Package>";

    private FileInfo PackageManifestPath => new(Path.Combine(_tempDirectory.FullName, "Package.appxmanifest"));

    private static bool ManifestExists(DirectoryInfo dir)
        => File.Exists(Path.Combine(dir.FullName, "Package.appxmanifest"))
        || File.Exists(Path.Combine(dir.FullName, "appxmanifest.xml"));

    // ── Parse-level tests ───────────────────────────────────────────────

    [TestMethod]
    public void Parse_RejectsNonExistentDirectory()
    {
        var command = GetRequiredService<ManifestGenerateCommand>();
        var missing = Path.Combine(_tempDirectory.FullName, "does-not-exist");

        var parseResult = command.Parse([missing]);

        Assert.IsNotEmpty(parseResult.Errors, "directory argument uses AcceptExistingOnly");
    }

    [TestMethod]
    public void Parse_VersionDefaultsToFourPart()
    {
        var command = GetRequiredService<ManifestGenerateCommand>();

        var parseResult = command.Parse([]);

        Assert.AreEqual("1.0.0.0", parseResult.GetValue(ManifestGenerateCommand.VersionOption));
        Assert.AreEqual(ManifestTemplates.Packaged, parseResult.GetValue(ManifestGenerateCommand.TemplateOption));
    }

    // ── Successful generation ───────────────────────────────────────────

    [TestMethod]
    public async Task Generate_PackagedTemplate_CreatesManifest()
    {
        var command = GetRequiredService<ManifestGenerateCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--package-name", "MyApp"]);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(ManifestExists(_tempDirectory), "A manifest file should be generated");
    }

    [TestMethod]
    public async Task Generate_SparseTemplate_CreatesManifest()
    {
        var command = GetRequiredService<ManifestGenerateCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--package-name", "MyApp", "--template", "Sparse"]);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(ManifestExists(_tempDirectory));
    }

    // ── if-exists behaviour ─────────────────────────────────────────────

    [TestMethod]
    public async Task Generate_ManifestExists_DefaultErrors()
    {
        File.WriteAllText(PackageManifestPath.FullName, PlaceholderManifest);
        var command = GetRequiredService<ManifestGenerateCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(1, exitCode);
        Assert.Contains("already exists", ConsoleStdErr.ToString());
        Assert.AreEqual(PlaceholderManifest, File.ReadAllText(PackageManifestPath.FullName), "Existing manifest is left untouched");
    }

    [TestMethod]
    public async Task Generate_ManifestExists_SkipLeavesFileUntouched()
    {
        File.WriteAllText(PackageManifestPath.FullName, PlaceholderManifest);
        var command = GetRequiredService<ManifestGenerateCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--if-exists", "Skip"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(PlaceholderManifest, File.ReadAllText(PackageManifestPath.FullName), "Skip must not regenerate");
    }

    [TestMethod]
    public async Task Generate_ManifestExists_OverwriteRegenerates()
    {
        File.WriteAllText(PackageManifestPath.FullName, PlaceholderManifest);
        var command = GetRequiredService<ManifestGenerateCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--package-name", "MyApp", "--if-exists", "Overwrite"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreNotEqual(PlaceholderManifest, File.ReadAllText(PackageManifestPath.FullName), "Overwrite must regenerate the manifest");
    }
}

/// <summary>
/// Covers the generation failure branch of <see cref="ManifestGenerateCommand"/> by injecting a
/// manifest service whose <c>GenerateManifestAsync</c> throws.
/// </summary>
[TestClass]
public class ManifestGenerateCommandFailureTests : BaseCommandTests
{
    protected override IServiceCollection ConfigureServices(IServiceCollection services)
        => services.AddSingleton<IManifestService>(new ThrowingManifestService());

    [TestMethod]
    public async Task Generate_WhenServiceThrows_ReturnsError()
    {
        var command = GetRequiredService<ManifestGenerateCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(1, exitCode);
        Assert.Contains("Error generating manifest", ConsoleStdErr.ToString());
        Assert.Contains("disk full", ConsoleStdErr.ToString());
    }

    private sealed class ThrowingManifestService : IManifestService
    {
        public Task<ManifestGenerationInfo> PromptForManifestInfoAsync(
            DirectoryInfo directory, string? packageName, string? publisherName, string version,
            string? description, string? executable, bool useDefaults, CancellationToken cancellationToken = default)
            => Task.FromResult(new ManifestGenerationInfo(
                packageName ?? "App", publisherName ?? "CN=Test", version, description ?? "Test"));

        public Task GenerateManifestAsync(
            DirectoryInfo directory, ManifestGenerationInfo manifestGenerationInfo, ManifestTemplates manifestTemplate,
            FileInfo? logoPath, string? executable, TaskContext taskContext, CancellationToken cancellationToken = default)
            => throw new IOException("disk full");

        public Task UpdateManifestAssetsAsync(FileInfo manifestPath, FileInfo imagePath, TaskContext taskContext,
            FileInfo? lightImagePath = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AddExecutionAliasResult> AddExecutionAliasAsync(AddExecutionAliasOptions options, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ManifestGenerationInfo> PrepareSparseManifestInfoAsync(
            DirectoryInfo outputDirectory, FileInfo executable, string? packageName, string? publisherName,
            bool useDefaults, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SparseInitResult> GenerateSparseIdentityManifestAsync(
            DirectoryInfo outputDirectory, FileInfo executable, ManifestGenerationInfo info,
            TaskContext taskContext, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
