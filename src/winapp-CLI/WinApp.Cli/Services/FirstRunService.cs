// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace WinApp.Cli.Services;

internal class FirstRunService : IFirstRunService
{
    private const string FirstRunMarkerFileName = ".first-run-complete";
    private readonly FileInfo _firstRunMarkerFile;
    private readonly ILogger<FirstRunService> _logger;

    public FirstRunService(IWinappDirectoryService directoryService, ILogger<FirstRunService> logger)
    {
        var globalWinappDirectory = directoryService.GetGlobalWinappDirectory();
        _firstRunMarkerFile = new FileInfo(Path.Combine(globalWinappDirectory.FullName, FirstRunMarkerFileName));
        _logger = logger;
    }

    public void CheckAndDisplayFirstRunNotice()
    {
        _firstRunMarkerFile.Refresh();
        if (!_firstRunMarkerFile.Exists)
        {
            _logger.LogInformation("Welcome to WinApp CLI! By using this tool, you agree to the collection of anonymous usage data to help improve the product.");
            _logger.LogInformation("You can opt out of telemetry by setting the WINAPP_CLI_TELEMETRY_OPTOUT environment variable to '1'.");
            _logger.LogInformation("For more information, please visit: https://aka.ms/winappcli-telemetry-optout{NewLine}", Environment.NewLine);

            try
            {
                _firstRunMarkerFile.Directory?.Create();
                using var fs = _firstRunMarkerFile.Create();
                _firstRunMarkerFile.Attributes |= FileAttributes.Hidden;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to create first run marker file: {ErrorMessage}", ex.Message);
            }
        }
    }
}
