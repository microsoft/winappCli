// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

internal class FirstRunService : IFirstRunService
{
    private const string FirstRunMarkerFileName = ".first-run-complete";
    private const string PrivacyUrl = "https://go.microsoft.com/fwlink/?LinkId=521839";
    private readonly FileInfo _firstRunMarkerFile;
    private readonly ILogger<FirstRunService> _logger;

    // The short notice shown when the marker can't be saved goes to stderr so it never
    // corrupts stdout for scripts. Tests override this to capture it.
    internal TextWriter UnsavedNoticeWriter { get; set; } = Console.Error;

    public FirstRunService(IWinappDirectoryService directoryService, ILogger<FirstRunService> logger)
    {
        var globalWinappDirectory = directoryService.GetGlobalWinappDirectory();
        _firstRunMarkerFile = new FileInfo(Path.Combine(globalWinappDirectory.FullName, FirstRunMarkerFileName));
        _logger = logger;
    }

    public FirstRunNotice CheckAndDisplayFirstRunNotice()
    {
        _firstRunMarkerFile.Refresh();
        if (_firstRunMarkerFile.Exists)
        {
            return FirstRunNotice.None;
        }

        try
        {
            _firstRunMarkerFile.Directory?.Create();
            using (_firstRunMarkerFile.Create())
            {
            }
            _firstRunMarkerFile.Attributes |= FileAttributes.Hidden;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("Could not save first run marker {Path}: {ErrorMessage}", _firstRunMarkerFile.FullName, ex.Message);
            UnsavedNoticeWriter.WriteLine($"winapp collects anonymous usage data ({PrivacyUrl}). Set WINAPP_CLI_TELEMETRY_OPTOUT=1 to opt out.");
            return FirstRunNotice.Unsaved;
        }

        BannerHelper.DisplayBanner();

        _logger.LogInformation("Welcome to the Windows App Development CLI! By using this tool, you agree to the collection of anonymous usage data to help improve the product. You can read the full privacy policy at {PrivacyUrl}", PrivacyUrl);
        _logger.LogInformation("You can opt out of telemetry by setting the WINAPP_CLI_TELEMETRY_OPTOUT environment variable to '1'.");
        _logger.LogInformation("For more information, please visit: https://aka.ms/winappcli-telemetry-optout{NewLine}", Environment.NewLine);

        return FirstRunNotice.Shown;
    }
}
