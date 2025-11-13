// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

[JsonSerializable(typeof(CacheConfiguration))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class CacheConfigurationJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Manages the winapp package cache location and operations
/// </summary>
internal sealed class CacheService : ICacheService
{
    private const string CacheConfigFileName = "cache-config.json";
    private readonly IWinappDirectoryService _directoryService;
    private readonly ILogger<CacheService> _logger;
    private readonly FileInfo _cacheConfigFilePath;

    public CacheService(IWinappDirectoryService directoryService, ILogger<CacheService> logger)
    {
        _directoryService = directoryService;
        _logger = logger;
        var globalWinappDirectory = directoryService.GetGlobalWinappDirectory();
        _cacheConfigFilePath = new FileInfo(Path.Combine(globalWinappDirectory.FullName, CacheConfigFileName));
    }

    public DirectoryInfo GetCacheDirectory()
    {
        return _directoryService.GetPackagesDirectory();
    }

    public Task MoveCacheAsync(string newPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newPath))
        {
            throw new ArgumentException("New path cannot be empty", nameof(newPath));
        }

        var newPathExpanded = Environment.ExpandEnvironmentVariables(newPath);
        var newDir = new DirectoryInfo(newPathExpanded);

        // Validate the new directory
        if (newDir.Exists)
        {
            // Check if directory is empty
            if (newDir.GetFileSystemInfos().Length > 0)
            {
                throw new InvalidOperationException($"Target directory is not empty: {newDir.FullName}. Please use an empty directory or create a new one.");
            }
        }
        else
        {
            // Ask for confirmation to create
            _logger.LogInformation("Target directory does not exist: {Path}", newDir.FullName);
            if (!Program.PromptYesNo("Do you want to create it? (y/n): "))
            {
                throw new OperationCanceledException("Operation cancelled by user.");
            }
            newDir.Create();
            _logger.LogInformation("{UISymbol} Created directory: {Path}", UiSymbols.Check, newDir.FullName);
        }

        // Get current cache directory
        var currentCacheDir = GetCacheDirectory();

        if (currentCacheDir.Exists)
        {
            _logger.LogInformation("Moving cache from {OldPath} to {NewPath}...", currentCacheDir.FullName, newDir.FullName);
            
            // Move all contents
            foreach (var item in currentCacheDir.GetFileSystemInfos())
            {
                var destPath = Path.Combine(newDir.FullName, item.Name);
                if (item is DirectoryInfo dirInfo)
                {
                    dirInfo.MoveTo(destPath);
                }
                else if (item is FileInfo fileInfo)
                {
                    fileInfo.MoveTo(destPath);
                }
            }

            _logger.LogInformation("{UISymbol} Cache moved successfully", UiSymbols.Check);
        }
        else
        {
            _logger.LogInformation("No existing cache to move, initializing new cache location");
        }

        // Update configuration
        SetCustomCacheLocation(newDir.FullName);
        _logger.LogInformation("{UISymbol} Cache location updated to: {Path}", UiSymbols.Save, newDir.FullName);

        return Task.CompletedTask;
    }

    public Task ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        var cacheDir = GetCacheDirectory();

        if (!cacheDir.Exists)
        {
            _logger.LogInformation("Cache directory does not exist, nothing to clear");
            return Task.CompletedTask;
        }

        _logger.LogInformation("Clearing cache at: {Path}", cacheDir.FullName);

        // Delete all contents
        foreach (var item in cacheDir.GetFileSystemInfos())
        {
            try
            {
                if (item is DirectoryInfo dirInfo)
                {
                    dirInfo.Delete(true);
                }
                else if (item is FileInfo fileInfo)
                {
                    fileInfo.Delete();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to delete {Item}: {Error}", item.Name, ex.Message);
            }
        }

        _logger.LogInformation("{UISymbol} Cache cleared successfully", UiSymbols.Check);

        return Task.CompletedTask;
    }

    public string? GetCustomCacheLocation()
    {
        _cacheConfigFilePath.Refresh();
        if (!_cacheConfigFilePath.Exists)
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(_cacheConfigFilePath.FullName);
            var config = JsonSerializer.Deserialize(json, CacheConfigurationJsonContext.Default.CacheConfiguration);
            return config?.CustomCacheLocation;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to read cache configuration: {Error}", ex.Message);
            return null;
        }
    }

    public void SetCustomCacheLocation(string path)
    {
        var config = new CacheConfiguration { CustomCacheLocation = path };

        try
        {
            // Ensure the .winapp directory exists
            if (_cacheConfigFilePath.Directory?.Exists != true)
            {
                _cacheConfigFilePath.Directory?.Create();
            }

            var json = JsonSerializer.Serialize(config, CacheConfigurationJsonContext.Default.CacheConfiguration);
            File.WriteAllText(_cacheConfigFilePath.FullName, json);
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to save cache configuration: {Error}", ex.Message);
            throw;
        }
    }

    public void RemoveCustomCacheLocation()
    {
        _cacheConfigFilePath.Refresh();
        if (_cacheConfigFilePath.Exists)
        {
            _cacheConfigFilePath.Delete();
        }
    }
}

/// <summary>
/// Configuration for cache location
/// </summary>
internal sealed class CacheConfiguration
{
    public string? CustomCacheLocation { get; set; }
}
