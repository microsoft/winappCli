// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

internal class FirstRunService : IFirstRunService
{
    private const string FirstRunMarkerFileName = ".first-run-complete";
    private readonly IWinappDirectoryService _directoryService;
    private readonly ILogger<FirstRunService> _logger;
    private readonly IStorageDiagnostics _diagnostics;

    public FirstRunService(
        IWinappDirectoryService directoryService,
        ILogger<FirstRunService> logger,
        IStorageDiagnostics? diagnostics = null)
    {
        _directoryService = directoryService;
        _logger = logger;
        _diagnostics = diagnostics ?? new StorageDiagnostics(Console.Error);
    }

    public bool CheckAndDisplayFirstRunNotice()
    {
        FileInfo marker;
        try
        {
            marker = new FileInfo(Path.Combine(_directoryService.GetGlobalWinappDirectory().FullName, FirstRunMarkerFileName));
            marker.Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            _diagnostics.Warning("optional_storage_unavailable", $"First-run bookkeeping is unavailable: {ex.Message}");
            return false;
        }

        if (!marker.Exists)
        {
            BannerHelper.DisplayBanner();

            _logger.LogInformation("Welcome to the Windows App Development CLI! By using this tool, you agree to the collection of anonymous usage data to help improve the product. You can read the full privacy policy at https://go.microsoft.com/fwlink/?LinkId=521839");
            _logger.LogInformation("You can opt out of telemetry by setting the WINAPP_CLI_TELEMETRY_OPTOUT environment variable to '1'.");
            _logger.LogInformation("For more information, please visit: https://aka.ms/winappcli-telemetry-optout{NewLine}", Environment.NewLine);

            try
            {
                marker.Directory?.Create();
                using var fs = marker.Create();
                marker.Attributes |= FileAttributes.Hidden;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _diagnostics.Warning("optional_storage_unavailable",
                    $"Cannot save the first-run marker at '{marker.FullName}'. Continuing without saving it: {ex.Message}");
            }

            return true;
        }

        return false;
    }
}
