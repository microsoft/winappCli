// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class IncrementalCopyHelperTests
{
    private DirectoryInfo _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"IncrementalCopyTest_{Guid.NewGuid():N}"));
        _tempDir.Create();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_tempDir.Exists)
        {
            _tempDir.Delete(recursive: true);
        }
    }

    private DirectoryInfo CreateSubDir(string name)
    {
        var dir = new DirectoryInfo(Path.Combine(_tempDir.FullName, name));
        dir.Create();
        return dir;
    }

    private static FileInfo WriteFile(DirectoryInfo dir, string relativePath, string content)
    {
        var path = Path.Combine(dir.FullName, relativePath);
        var file = new FileInfo(path);
        file.Directory?.Create();
        File.WriteAllText(path, content);
        return file;
    }

    #region CopyFiles Tests

    [TestMethod]
    public void CopyFiles_FirstCopy_CopiesAll()
    {
        var source = CreateSubDir("source");
        var dest = CreateSubDir("dest");
        var file1 = WriteFile(source, "icon.png", "icon-data");
        var file2 = WriteFile(source, "assets\\logo.png", "logo-data");

        var files = new List<(FileInfo, string)>
        {
            (file1, "icon.png"),
            (file2, "assets\\logo.png"),
        };

        var (copied, skipped) = IncrementalCopyHelper.CopyFiles(files, dest);

        Assert.AreEqual(2, copied);
        Assert.AreEqual(0, skipped);
        Assert.IsTrue(File.Exists(Path.Combine(dest.FullName, "icon.png")));
        Assert.IsTrue(File.Exists(Path.Combine(dest.FullName, "assets", "logo.png")));
    }

    [TestMethod]
    public void CopyFiles_UnchangedFiles_AreSkipped()
    {
        var source = CreateSubDir("source");
        var dest = CreateSubDir("dest");
        var file1 = WriteFile(source, "icon.png", "icon-data");

        var files = new List<(FileInfo, string)> { (file1, "icon.png") };

        // First copy
        IncrementalCopyHelper.CopyFiles(files, dest);

        // Second copy should skip
        var (copied, skipped) = IncrementalCopyHelper.CopyFiles(files, dest);

        Assert.AreEqual(0, copied);
        Assert.AreEqual(1, skipped);
    }

    [TestMethod]
    public void CopyFiles_ModifiedFile_IsCopied()
    {
        var source = CreateSubDir("source");
        var dest = CreateSubDir("dest");
        var file1 = WriteFile(source, "icon.png", "original");

        var files = new List<(FileInfo, string)> { (file1, "icon.png") };

        // First copy
        IncrementalCopyHelper.CopyFiles(files, dest);

        // Modify the source
        Thread.Sleep(50);
        file1 = WriteFile(source, "icon.png", "modified-content-longer");
        files = [(file1, "icon.png")];

        var (copied, skipped) = IncrementalCopyHelper.CopyFiles(files, dest);

        Assert.AreEqual(1, copied);
        Assert.AreEqual(0, skipped);
        Assert.AreEqual("modified-content-longer", File.ReadAllText(Path.Combine(dest.FullName, "icon.png")));
    }

    #endregion
}
