// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="GitignoreService"/>, which keeps a project's .gitignore in sync by
/// adding the generated <c>.winapp</c> folder and dev-certificate files. Covers first-time
/// creation, appending to an existing file, idempotency, and the resilient error path (the
/// service must never throw — a failed .gitignore edit is non-fatal to the overall workflow).
/// </summary>
[TestClass]
public class GitignoreServiceTests : BaseCommandTests
{
    private IGitignoreService _service = null!;
    private DirectoryInfo _projectDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _service = GetRequiredService<IGitignoreService>();
        _projectDir = _tempDirectory.CreateSubdirectory($"proj_{Guid.NewGuid():N}");
    }

    private string GitignorePath => Path.Combine(_projectDir.FullName, ".gitignore");

    // ---------------------------------------------------------------------
    // AddWinAppFolderToGitIgnoreAsync
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task AddWinAppFolder_NoExistingGitignore_CreatesFileWithEntry()
    {
        var updated = await _service.AddWinAppFolderToGitIgnoreAsync(_projectDir, TestTaskContext, TestContext.CancellationToken);

        Assert.IsTrue(updated, "Should report the file was updated.");
        Assert.IsTrue(File.Exists(GitignorePath), ".gitignore should be created.");
        var content = await File.ReadAllTextAsync(GitignorePath);
        Assert.Contains(".winapp", content);
        Assert.Contains("# Windows SDK packages and generated files", content);
    }

    [TestMethod]
    public async Task AddWinAppFolder_ExistingFileWithoutTrailingNewline_AppendsCleanly()
    {
        await File.WriteAllTextAsync(GitignorePath, "bin/\nobj/");

        var updated = await _service.AddWinAppFolderToGitIgnoreAsync(_projectDir, TestTaskContext, TestContext.CancellationToken);

        Assert.IsTrue(updated);
        var content = await File.ReadAllTextAsync(GitignorePath);
        Assert.Contains("bin/", content, "Existing entries must be preserved.");
        Assert.Contains("obj/", content);
        Assert.Contains(".winapp", content);
        // The original content lacked a trailing newline; the service must not glue ".winapp" onto "obj/".
        Assert.IsFalse(content.Contains("obj/\n.winapp", StringComparison.Ordinal) && !content.Contains("obj/\n\n", StringComparison.Ordinal),
            "A separating blank line/comment should precede the .winapp entry.");
    }

    [TestMethod]
    public async Task AddWinAppFolder_AlreadyPresent_ReturnsFalseAndLeavesContentUnchanged()
    {
        var original = "node_modules\n.winapp\ndist\n";
        await File.WriteAllTextAsync(GitignorePath, original);

        var updated = await _service.AddWinAppFolderToGitIgnoreAsync(_projectDir, TestTaskContext, TestContext.CancellationToken);

        Assert.IsFalse(updated, "Existing .winapp entry must not be duplicated.");
        Assert.AreEqual(original, await File.ReadAllTextAsync(GitignorePath), "Content must be untouched.");
    }

    [TestMethod]
    public async Task AddWinAppFolder_IsIdempotent()
    {
        var first = await _service.AddWinAppFolderToGitIgnoreAsync(_projectDir, TestTaskContext, TestContext.CancellationToken);
        var second = await _service.AddWinAppFolderToGitIgnoreAsync(_projectDir, TestTaskContext, TestContext.CancellationToken);

        Assert.IsTrue(first);
        Assert.IsFalse(second, "Second call must be a no-op.");

        // Exactly one .winapp entry.
        var lines = (await File.ReadAllTextAsync(GitignorePath)).Split('\n').Select(l => l.Trim());
        Assert.AreEqual(1, lines.Count(l => l == ".winapp"));
    }

    [TestMethod]
    public async Task AddWinAppFolder_WriteFailure_ReturnsFalseWithoutThrowing()
    {
        // Make ".gitignore" a directory so writing to it fails; the service must swallow the error.
        Directory.CreateDirectory(GitignorePath);

        var updated = await _service.AddWinAppFolderToGitIgnoreAsync(_projectDir, TestTaskContext, TestContext.CancellationToken);

        Assert.IsFalse(updated, "A failed write must return false rather than throw.");
    }

    // ---------------------------------------------------------------------
    // AddCertificateToGitignoreAsync
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task AddCertificate_NoExistingGitignore_CreatesFileWithEntry()
    {
        var updated = await _service.AddCertificateToGitignoreAsync(_projectDir, "MyApp_Cert.pfx", TestTaskContext, TestContext.CancellationToken);

        Assert.IsTrue(updated);
        var content = await File.ReadAllTextAsync(GitignorePath);
        Assert.Contains("MyApp_Cert.pfx", content);
        Assert.Contains("# Development certificate", content);
    }

    [TestMethod]
    public async Task AddCertificate_ExistingFileWithoutTrailingNewline_AppendsCleanly()
    {
        await File.WriteAllTextAsync(GitignorePath, ".winapp");

        var updated = await _service.AddCertificateToGitignoreAsync(_projectDir, "cert.pfx", TestTaskContext, TestContext.CancellationToken);

        Assert.IsTrue(updated);
        var content = await File.ReadAllTextAsync(GitignorePath);
        Assert.Contains(".winapp", content);
        Assert.Contains("cert.pfx", content);
    }

    [TestMethod]
    public async Task AddCertificate_AlreadyPresent_ReturnsFalse()
    {
        var original = "# Development certificate\nMyApp_Cert.pfx\n";
        await File.WriteAllTextAsync(GitignorePath, original);

        var updated = await _service.AddCertificateToGitignoreAsync(_projectDir, "MyApp_Cert.pfx", TestTaskContext, TestContext.CancellationToken);

        Assert.IsFalse(updated);
        Assert.AreEqual(original, await File.ReadAllTextAsync(GitignorePath));
    }

    [TestMethod]
    public async Task AddCertificate_IsIdempotent()
    {
        var first = await _service.AddCertificateToGitignoreAsync(_projectDir, "cert.pfx", TestTaskContext, TestContext.CancellationToken);
        var second = await _service.AddCertificateToGitignoreAsync(_projectDir, "cert.pfx", TestTaskContext, TestContext.CancellationToken);

        Assert.IsTrue(first);
        Assert.IsFalse(second);

        var lines = (await File.ReadAllTextAsync(GitignorePath)).Split('\n').Select(l => l.Trim());
        Assert.AreEqual(1, lines.Count(l => l == "cert.pfx"));
    }

    [TestMethod]
    public async Task AddCertificate_DistinctNames_BothAdded()
    {
        await _service.AddCertificateToGitignoreAsync(_projectDir, "certA.pfx", TestTaskContext, TestContext.CancellationToken);
        var updated = await _service.AddCertificateToGitignoreAsync(_projectDir, "certB.pfx", TestTaskContext, TestContext.CancellationToken);

        Assert.IsTrue(updated, "A different certificate name should still be appended.");
        var content = await File.ReadAllTextAsync(GitignorePath);
        Assert.Contains("certA.pfx", content);
        Assert.Contains("certB.pfx", content);
    }

    [TestMethod]
    public async Task AddCertificate_WriteFailure_ReturnsFalseWithoutThrowing()
    {
        Directory.CreateDirectory(GitignorePath);

        var updated = await _service.AddCertificateToGitignoreAsync(_projectDir, "cert.pfx", TestTaskContext, TestContext.CancellationToken);

        Assert.IsFalse(updated);
    }
}
