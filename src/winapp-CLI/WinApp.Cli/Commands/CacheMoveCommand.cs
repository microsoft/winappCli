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
            Description = "The new path for the packages cache directory"
        };
    }

    public CacheMoveCommand() : base("move", "Move the packages cache to a new location")
    {
        Arguments.Add(NewPathArgument);
    }

    public class Handler(
        IWinappDirectoryService winappDirectoryService,
        ICacheConfigService cacheConfigService,
        ILogger<CacheMoveCommand> logger) : AsynchronousCommandLineAction
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
                var newPathFullName = Path.GetFullPath(newPath.FullName);
                var newDir = new DirectoryInfo(newPathFullName);

                // Check if the directory exists
                if (!newDir.Exists)
                {
                    logger.LogWarning("{UISymbol} Directory does not exist: {NewPath}", UiSymbols.Warning, newPathFullName);
                    logger.LogInformation("Do you want to create it? (y/n): ");
                    
                    if (!Program.PromptYesNo(""))
                    {
                        logger.LogError("{UISymbol} Operation cancelled", UiSymbols.Error);
                        return Task.FromResult(1);
                    }

                    newDir.Create();
                    logger.LogInformation("{UISymbol} Created directory: {NewPath}", UiSymbols.Check, newPathFullName);
                }
                else
                {
                    // If directory exists, check if it's empty or already has packages
                    var files = newDir.GetFileSystemInfos();
                    if (files.Length > 0)
                    {
                        // Check if it looks like a packages directory
                        var hasPackages = files.Any(f => f is DirectoryInfo);
                        if (hasPackages)
                        {
                            logger.LogWarning("{UISymbol} Directory is not empty: {NewPath}", UiSymbols.Warning, newPathFullName);
                            logger.LogInformation("The directory contains files/folders. Continue anyway? (y/n): ");
                            
                            if (!Program.PromptYesNo(""))
                            {
                                logger.LogError("{UISymbol} Operation cancelled", UiSymbols.Error);
                                return Task.FromResult(1);
                            }
                        }
                    }
                }

                // Get current cache directory
                var currentCacheDir = winappDirectoryService.GetPackagesCacheDirectory();
                
                // If current cache exists and has content, move it
                if (currentCacheDir.Exists)
                {
                    var currentFiles = currentCacheDir.GetFileSystemInfos();
                    if (currentFiles.Length > 0)
                    {
                        logger.LogInformation("{UISymbol} Moving cache from {CurrentPath} to {NewPath}...", UiSymbols.Package, currentCacheDir.FullName, newPathFullName);
                        
                        // Move all files and directories
                        foreach (var item in currentFiles)
                        {
                            var destPath = Path.Combine(newPathFullName, item.Name);
                            if (item is DirectoryInfo dir)
                            {
                                if (!Directory.Exists(destPath))
                                {
                                    Directory.Move(dir.FullName, destPath);
                                    logger.LogDebug("  Moved directory: {ItemName}", item.Name);
                                }
                                else
                                {
                                    logger.LogWarning("  Skipped existing directory: {ItemName}", item.Name);
                                }
                            }
                            else if (item is FileInfo file)
                            {
                                if (!File.Exists(destPath))
                                {
                                    File.Move(file.FullName, destPath);
                                    logger.LogDebug("  Moved file: {ItemName}", item.Name);
                                }
                                else
                                {
                                    logger.LogWarning("  Skipped existing file: {ItemName}", item.Name);
                                }
                            }
                        }
                    }
                }

                // Save the new cache path to configuration
                cacheConfigService.SetCustomCachePath(newPathFullName);

                logger.LogInformation("{UISymbol} Cache location updated successfully", UiSymbols.Check);
                logger.LogInformation("{UISymbol} New cache path: {NewPath}", UiSymbols.Folder, newPathFullName);

                return Task.FromResult(0);
            }
            catch (Exception ex)
            {
                logger.LogError("{UISymbol} Error moving cache: {ErrorMessage}", UiSymbols.Error, ex.Message);
                return Task.FromResult(1);
            }
        }
    }
}
