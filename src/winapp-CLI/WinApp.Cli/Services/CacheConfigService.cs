// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

[JsonSerializable(typeof(CacheConfig))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class CacheConfigJsonContext : JsonSerializerContext
{
}

internal class CacheConfig
{
    public string? CustomCachePath { get; set; }
}

/// <summary>
/// Service for managing cache configuration
/// Stores custom cache path in .winapp/cache-config.json
/// </summary>
internal sealed class CacheConfigService : ICacheConfigService
{
    private const string ConfigFileName = "cache-config.json";
    private readonly FileInfo _configFilePath;
    private readonly ILogger<CacheConfigService> _logger;

    public CacheConfigService(IWinappDirectoryService directoryService, ILogger<CacheConfigService> logger)
    {
        var globalWinappDirectory = directoryService.GetGlobalWinappDirectory();
        _configFilePath = new FileInfo(Path.Combine(globalWinappDirectory.FullName, ConfigFileName));
        _logger = logger;
    }

    public string? GetCustomCachePath()
    {
        _configFilePath.Refresh();
        if (!_configFilePath.Exists)
        {
            return null;
        }

        try
        {
            using var fileStream = _configFilePath.OpenRead();
            var config = JsonSerializer.Deserialize(fileStream, CacheConfigJsonContext.Default.CacheConfig);
            return config?.CustomCachePath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Warning: Failed to load cache config: {ErrorMessage}", ex.Message);
            return null;
        }
    }

    public void SetCustomCachePath(string path)
    {
        try
        {
            // Ensure the .winapp directory exists
            if (_configFilePath.Directory?.Exists != true)
            {
                _configFilePath.Directory?.Create();
            }

            var config = new CacheConfig { CustomCachePath = path };
            using var stream = _configFilePath.Open(FileMode.Create, FileAccess.Write);
            JsonSerializer.Serialize(stream, config, CacheConfigJsonContext.Default.CacheConfig);
            _configFilePath.Refresh();
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to save cache config: {ErrorMessage}", ex.Message);
            throw;
        }
    }

    public void ClearCustomCachePath()
    {
        try
        {
            _configFilePath.Refresh();
            if (_configFilePath.Exists)
            {
                _configFilePath.Delete();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to delete cache config: {ErrorMessage}", ex.Message);
            throw;
        }
    }
}
