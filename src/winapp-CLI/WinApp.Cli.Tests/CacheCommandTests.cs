// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class CacheCommandTests : BaseCommandTests
{
    private CacheGetPathCommand.Handler _getPathHandler = null!;
    private CacheMoveCommand.Handler _moveHandler = null!;
    private CacheClearCommand.Handler _clearHandler = null!;
    private IWinappDirectoryService _directoryService = null!;
    private ICacheConfigService _cacheConfigService = null!;

    [TestInitialize]
    public void Setup()
    {
        _getPathHandler = GetRequiredService<CacheGetPathCommand.Handler>();
        _moveHandler = GetRequiredService<CacheMoveCommand.Handler>();
        _clearHandler = GetRequiredService<CacheClearCommand.Handler>();
        _directoryService = GetRequiredService<IWinappDirectoryService>();
        _cacheConfigService = GetRequiredService<ICacheConfigService>();
    }

    [TestMethod]
    public async Task CacheGetPath_ReturnsDefaultPath_WhenNoCustomPathSet()
    {
        // Arrange
        var command = new CacheGetPathCommand();
        var parseResult = command.Parse("");

        // Act
        var result = await _getPathHandler.InvokeAsync(parseResult);

        // Assert
        Assert.AreEqual(0, result);
        var output = ConsoleStdOut.ToString();
        StringAssert.Contains(output, "packages", "Output should contain 'packages'");
    }

    [TestMethod]
    public async Task CacheMoveCommand_MovesCache_ToNewLocation()
    {
        // Arrange
        var newCacheDir = _tempDirectory.CreateSubdirectory("new-cache");
        var currentCacheDir = _directoryService.GetPackagesCacheDirectory();
        
        // Create some dummy files in current cache
        currentCacheDir.Create();
        File.WriteAllText(Path.Combine(currentCacheDir.FullName, "test.txt"), "test content");
        var testSubDir = currentCacheDir.CreateSubdirectory("TestPackage.1.0.0");
        File.WriteAllText(Path.Combine(testSubDir.FullName, "package.txt"), "package content");

        var command = new CacheMoveCommand();
        var parseResult = command.Parse($"\"{newCacheDir.FullName}\"");

        // Mock Console.ReadLine for the prompt
        var originalIn = Console.In;
        try
        {
            Console.SetIn(new StringReader("y"));

            // Act
            var result = await _moveHandler.InvokeAsync(parseResult);

            // Assert
            Assert.AreEqual(0, result);
            
            // Verify files were moved
            Assert.IsTrue(File.Exists(Path.Combine(newCacheDir.FullName, "test.txt")));
            Assert.IsTrue(Directory.Exists(Path.Combine(newCacheDir.FullName, "TestPackage.1.0.0")));
            Assert.IsTrue(File.Exists(Path.Combine(newCacheDir.FullName, "TestPackage.1.0.0", "package.txt")));
            
            // Verify custom path was saved
            var customPath = _cacheConfigService.GetCustomCachePath();
            Assert.AreEqual(newCacheDir.FullName, customPath);
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    [TestMethod]
    public async Task CacheMoveCommand_CreatesDirectory_WhenItDoesNotExist()
    {
        // Arrange
        var newCacheDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "non-existent-cache"));
        
        var command = new CacheMoveCommand();
        var parseResult = command.Parse($"\"{newCacheDir.FullName}\"");

        // Mock Console.ReadLine for the prompts
        var originalIn = Console.In;
        try
        {
            Console.SetIn(new StringReader("y\n"));

            // Act
            var result = await _moveHandler.InvokeAsync(parseResult);

            // Assert
            Assert.AreEqual(0, result);
            Assert.IsTrue(newCacheDir.Exists);
            
            // Verify custom path was saved
            var customPath = _cacheConfigService.GetCustomCachePath();
            Assert.AreEqual(newCacheDir.FullName, customPath);
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    [TestMethod]
    public async Task CacheMoveCommand_CancelsOperation_WhenUserDeclinesDirectoryCreation()
    {
        // Arrange
        var newCacheDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "non-existent-cache"));
        
        var command = new CacheMoveCommand();
        var parseResult = command.Parse($"\"{newCacheDir.FullName}\"");

        // Mock Console.ReadLine for the prompts
        var originalIn = Console.In;
        try
        {
            Console.SetIn(new StringReader("n\n"));

            // Act
            var result = await _moveHandler.InvokeAsync(parseResult);

            // Assert
            Assert.AreEqual(1, result);
            Assert.IsFalse(newCacheDir.Exists);
            
            // Verify custom path was not saved
            var customPath = _cacheConfigService.GetCustomCachePath();
            Assert.IsNull(customPath);
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    [TestMethod]
    public async Task CacheClearCommand_ClearsCache_WhenUserConfirms()
    {
        // Arrange
        var cacheDir = _directoryService.GetPackagesCacheDirectory();
        cacheDir.Create();
        
        // Create some dummy files in cache
        File.WriteAllText(Path.Combine(cacheDir.FullName, "test.txt"), "test content");
        var testSubDir = cacheDir.CreateSubdirectory("TestPackage.1.0.0");
        File.WriteAllText(Path.Combine(testSubDir.FullName, "package.txt"), "package content");

        var command = new CacheClearCommand();
        var parseResult = command.Parse("");

        // Mock Console.ReadLine for the prompt
        var originalIn = Console.In;
        try
        {
            Console.SetIn(new StringReader("y\n"));

            // Act
            var result = await _clearHandler.InvokeAsync(parseResult);

            // Assert
            Assert.AreEqual(0, result);
            
            // Verify cache was cleared
            cacheDir.Refresh();
            Assert.IsTrue(cacheDir.Exists); // Directory itself should still exist
            CollectionAssert.AreEqual(Array.Empty<FileSystemInfo>(), cacheDir.GetFileSystemInfos()); // But should be empty
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    [TestMethod]
    public async Task CacheClearCommand_CancelsOperation_WhenUserDeclines()
    {
        // Arrange
        var cacheDir = _directoryService.GetPackagesCacheDirectory();
        cacheDir.Create();
        
        // Create some dummy files in cache
        File.WriteAllText(Path.Combine(cacheDir.FullName, "test.txt"), "test content");

        var command = new CacheClearCommand();
        var parseResult = command.Parse("");

        // Mock Console.ReadLine for the prompt
        var originalIn = Console.In;
        try
        {
            Console.SetIn(new StringReader("n\n"));

            // Act
            var result = await _clearHandler.InvokeAsync(parseResult);

            // Assert
            Assert.AreEqual(1, result);
            
            // Verify cache was not cleared
            Assert.IsTrue(File.Exists(Path.Combine(cacheDir.FullName, "test.txt")));
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    [TestMethod]
    public async Task CacheClearCommand_SucceedsGracefully_WhenCacheDoesNotExist()
    {
        // Arrange
        var command = new CacheClearCommand();
        var parseResult = command.Parse("");

        // Act
        var result = await _clearHandler.InvokeAsync(parseResult);

        // Assert
        Assert.AreEqual(0, result);
    }

    [TestMethod]
    public async Task CacheGetPath_ReturnsCustomPath_AfterMovingCache()
    {
        // Arrange
        var newCacheDir = _tempDirectory.CreateSubdirectory("custom-cache");
        var command = new CacheMoveCommand();
        var parseResult = command.Parse($"\"{newCacheDir.FullName}\"");

        // Mock Console.ReadLine for the prompt
        var originalIn = Console.In;
        try
        {
            Console.SetIn(new StringReader("y\n"));

            // Move the cache
            await _moveHandler.InvokeAsync(parseResult);

            // Act - Get the cache path
            var getPathCommand = new CacheGetPathCommand();
            var getPathParseResult = getPathCommand.Parse("");
            var result = await _getPathHandler.InvokeAsync(getPathParseResult);

            // Assert
            Assert.AreEqual(0, result);
            var output = ConsoleStdOut.ToString();
            StringAssert.Contains(output, newCacheDir.FullName, "Output should contain the custom cache path");
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }
}
