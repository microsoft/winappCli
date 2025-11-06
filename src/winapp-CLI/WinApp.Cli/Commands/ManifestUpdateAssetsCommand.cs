// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class ManifestUpdateAssetsCommand : Command
{
    public static Argument<FileInfo> ImageArgument { get; }
    public static Option<FileInfo> ManifestOption { get; }

    static ManifestUpdateAssetsCommand()
    {
        ImageArgument = new Argument<FileInfo>("image-path")
        {
            Description = "Path to source image file"
        };
        ImageArgument.AcceptExistingOnly();

        ManifestOption = new Option<FileInfo>("--manifest")
        {
            Description = "Path to AppxManifest.xml file (default: search current directory)"
        };
        ManifestOption.AcceptExistingOnly();
    }

    public ManifestUpdateAssetsCommand() : base("update-assets", "Update image assets in AppxManifest.xml from a source image")
    {
        Arguments.Add(ImageArgument);
        Options.Add(ManifestOption);
    }

    public class Handler(IManifestService manifestService, ICurrentDirectoryProvider currentDirectoryProvider, ILogger<ManifestUpdateAssetsCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var imagePath = parseResult.GetValue(ImageArgument);
            var manifestPath = parseResult.GetValue(ManifestOption);

            // If manifest path is not provided, try to find it in the current directory
            if (manifestPath == null)
            {
                manifestPath = MsixService.FindProjectManifest(currentDirectoryProvider);
                if (manifestPath == null)
                {
                    logger.LogError("{UISymbol} Could not find AppxManifest.xml in current directory or parent directories", UiSymbols.Error);
                    return 1;
                }
                logger.LogDebug("Found manifest at: {ManifestPath}", manifestPath.FullName);
            }

            if (imagePath == null)
            {
                logger.LogError("{UISymbol} Image path is required", UiSymbols.Error);
                return 1;
            }

            try
            {
                await manifestService.UpdateManifestAssetsAsync(manifestPath, imagePath, cancellationToken);
                return 0;
            }
            catch (Exception ex)
            {
                logger.LogError("{UISymbol} Error updating assets: {ErrorMessage}", UiSymbols.Error, ex.Message);
                return 1;
            }
        }
    }
}
