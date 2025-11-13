// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class CacheMoveCommand : Command
{
    public static Argument<DirectoryInfo> NewPathArgument { get; }

    static CacheMoveCommand()
    {
        NewPathArgument = new Argument<DirectoryInfo>("new-path")
        {
            Description = "New directory path for the packages cache"
        };
    }

    public CacheMoveCommand() : base("move", "Move the packages cache to a new location")
    {
        Arguments.Add(NewPathArgument);
    }

    public class Handler(IWinappDirectoryService winappDirectoryService, ILogger<CacheMoveCommand> logger) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var newPath = parseResult.GetValue(NewPathArgument);
            if (newPath == null)
            {
                logger.LogError("{UISymbol} New path is required", UiSymbols.Error);
                return Task.FromResult(1);
            }

            try
            {
                var globalWinappDir = winappDirectoryService.GetGlobalWinappDirectory();
                var currentPackagesDir = winappDirectoryService.GetPackagesCacheDirectory();
                var newPackagesDir = new DirectoryInfo(Path.Combine(newPath.FullName, "packages"));

                logger.LogInformation("{UISymbol} Moving packages cache from {CurrentPath} to {NewPath}",
                    UiSymbols.Package, currentPackagesDir.FullName, newPackagesDir.FullName);

                // Validate the new path
                if (!newPath.Exists)
                {
                    logger.LogWarning("{UISymbol} Target directory does not exist: {NewPath}", UiSymbols.Warning, newPath.FullName);
                    var createDir = Program.PromptYesNo($"Create directory '{newPath.FullName}'? (y/n): ");
                    if (!createDir)
                    {
                        logger.LogInformation("Operation cancelled by user");
                        return Task.FromResult(1);
                    }

                    try
                    {
                        newPath.Create();
                        logger.LogInformation("{UISymbol} Created directory: {NewPath}", UiSymbols.Check, newPath.FullName);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError("{UISymbol} Failed to create directory: {ErrorMessage}", UiSymbols.Error, ex.Message);
                        return Task.FromResult(1);
                    }
                }

                // Check if target packages directory already exists and is not empty
                newPackagesDir.Refresh();
                if (newPackagesDir.Exists && newPackagesDir.EnumerateFileSystemInfos().Any())
                {
                    logger.LogWarning("{UISymbol} Target packages directory already exists and is not empty: {NewPackagesDir}",
                        UiSymbols.Warning, newPackagesDir.FullName);
                    var overwrite = Program.PromptYesNo("Continue and merge/overwrite? (y/n): ");
                    if (!overwrite)
                    {
                        logger.LogInformation("Operation cancelled by user");
                        return Task.FromResult(1);
                    }
                }

                // Check if current packages directory exists
                if (!currentPackagesDir.Exists)
                {
                    logger.LogInformation("{UISymbol} Current packages cache does not exist. Creating cache location marker at new path.",
                        UiSymbols.Info);
                }
                else
                {
                    // Move the packages directory
                    logger.LogInformation("{UISymbol} Moving packages...", UiSymbols.Package);

                    try
                    {
                        if (!newPackagesDir.Exists)
                        {
                            // Simple move if target doesn't exist
                            Directory.Move(currentPackagesDir.FullName, newPackagesDir.FullName);
                        }
                        else
                        {
                            // Copy files if target exists (merge mode)
                            CopyDirectory(currentPackagesDir, newPackagesDir);
                            // Remove old directory after successful copy
                            currentPackagesDir.Delete(true);
                        }

                        logger.LogInformation("{UISymbol} Packages moved successfully", UiSymbols.Check);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError("{UISymbol} Failed to move packages: {ErrorMessage}", UiSymbols.Error, ex.Message);
                        return Task.FromResult(1);
                    }
                }

                // Store the new cache location
                var cacheLocationFile = new FileInfo(Path.Combine(globalWinappDir.FullName, "cache_location.txt"));
                try
                {
                    File.WriteAllText(cacheLocationFile.FullName, newPath.FullName);
                    logger.LogInformation("{UISymbol} Cache location updated", UiSymbols.Check);
                    logger.LogInformation("{UISymbol} New packages cache path: {NewPackagesDir}", UiSymbols.Folder, newPackagesDir.FullName);
                }
                catch (Exception ex)
                {
                    logger.LogError("{UISymbol} Failed to save cache location: {ErrorMessage}", UiSymbols.Error, ex.Message);
                    logger.LogWarning("Packages were moved, but location marker could not be saved.");
                    return Task.FromResult(1);
                }

                return Task.FromResult(0);
            }
            catch (Exception ex)
            {
                logger.LogError("{UISymbol} Error moving cache: {ErrorMessage}", UiSymbols.Error, ex.Message);
                return Task.FromResult(1);
            }
        }

        private static void CopyDirectory(DirectoryInfo source, DirectoryInfo target)
        {
            // Ensure target directory exists
            if (!target.Exists)
            {
                target.Create();
            }

            // Copy all files
            foreach (var file in source.GetFiles())
            {
                file.CopyTo(Path.Combine(target.FullName, file.Name), true);
            }

            // Copy all subdirectories
            foreach (var subDir in source.GetDirectories())
            {
                var targetSubDir = target.CreateSubdirectory(subDir.Name);
                CopyDirectory(subDir, targetSubDir);
            }
        }
    }
}
