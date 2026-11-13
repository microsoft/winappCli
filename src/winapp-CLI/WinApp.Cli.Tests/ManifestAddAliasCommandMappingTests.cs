// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Covers the <see cref="ManifestAddAliasCommand"/> status-to-message mappings that the real
/// manifest service cannot easily produce end-to-end (parse errors, an empty document, and the
/// defensive default arm). A fake service returns each status directly so the command's error
/// reporting and exit codes are verified deterministically.
/// </summary>
[TestClass]
public class ManifestAddAliasCommandMappingTests : BaseCommandTests
{
    private FakeAliasManifestService _fakeManifest = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeManifest = new FakeAliasManifestService();
        return services.AddSingleton<IManifestService>(_fakeManifest);
    }

    private FileInfo CreateManifestStub()
    {
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "Package.appxmanifest"));
        File.WriteAllText(manifest.FullName, "<Package />");
        return manifest;
    }

    [TestMethod]
    public async Task AddAlias_ManifestParseError_ReturnsError()
    {
        var manifest = CreateManifestStub();
        _fakeManifest.ResultToReturn = new AddExecutionAliasResult(
            AddExecutionAliasStatus.ManifestParseError, ErrorMessage: "unexpected token");
        var command = GetRequiredService<ManifestAddAliasCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName]);

        Assert.AreEqual(1, exitCode);
        Assert.Contains("Failed to parse manifest", ConsoleStdErr.ToString());
    }

    [TestMethod]
    public async Task AddAlias_ManifestEmpty_ReturnsError()
    {
        var manifest = CreateManifestStub();
        _fakeManifest.ResultToReturn = new AddExecutionAliasResult(AddExecutionAliasStatus.ManifestEmpty);
        var command = GetRequiredService<ManifestAddAliasCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName]);

        Assert.AreEqual(1, exitCode);
        Assert.Contains("no root element", ConsoleStdErr.ToString());
    }

    [TestMethod]
    public async Task AddAlias_UnexpectedStatus_ReturnsError()
    {
        var manifest = CreateManifestStub();
        // Cast an out-of-range value to exercise the switch's defensive default arm.
        _fakeManifest.ResultToReturn = new AddExecutionAliasResult((AddExecutionAliasStatus)999);
        var command = GetRequiredService<ManifestAddAliasCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName]);

        Assert.AreEqual(1, exitCode);
        Assert.Contains("Unexpected error", ConsoleStdErr.ToString());
    }

    private sealed class FakeAliasManifestService : IManifestService
    {
        public AddExecutionAliasResult ResultToReturn { get; set; } = new(AddExecutionAliasStatus.Added);

        public Task<AddExecutionAliasResult> AddExecutionAliasAsync(AddExecutionAliasOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult(ResultToReturn);

        public Task<ManifestGenerationInfo> PromptForManifestInfoAsync(DirectoryInfo directory, string? packageName, string? publisherName, string version, string? description, string? executable, bool useDefaults, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task GenerateManifestAsync(DirectoryInfo directory, ManifestGenerationInfo manifestGenerationInfo, ManifestTemplates manifestTemplate, FileInfo? logoPath, string? executable, TaskContext taskContext, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task UpdateManifestAssetsAsync(FileInfo manifestPath, FileInfo imagePath, TaskContext taskContext, FileInfo? lightImagePath = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ManifestGenerationInfo> PrepareSparseManifestInfoAsync(DirectoryInfo outputDirectory, FileInfo executable, string? packageName, string? publisherName, bool useDefaults, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SparseInitResult> GenerateSparseIdentityManifestAsync(DirectoryInfo outputDirectory, FileInfo executable, ManifestGenerationInfo info, TaskContext taskContext, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
