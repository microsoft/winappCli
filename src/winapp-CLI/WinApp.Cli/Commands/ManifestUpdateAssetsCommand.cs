// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Xml;
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

    public class Handler(IImageAssetService imageAssetService, ICurrentDirectoryProvider currentDirectoryProvider, ILogger<ManifestUpdateAssetsCommand> logger) : AsynchronousCommandLineAction
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
                logger.LogInformation("{UISymbol} Updating assets for manifest: {ManifestPath}", UiSymbols.Info, manifestPath.Name);

                // Determine the Assets directory relative to the manifest
                var manifestDir = manifestPath.Directory;
                if (manifestDir == null)
                {
                    throw new InvalidOperationException("Could not determine manifest directory");
                }

                var assetsDir = manifestDir.CreateSubdirectory("Assets");

                // Generate the image assets
                await imageAssetService.GenerateAssetsAsync(imagePath, assetsDir, cancellationToken);

                // Verify that the manifest references the Assets directory correctly
                VerifyManifestAssetReferences(manifestPath);

                logger.LogInformation("{UISymbol} Image assets updated successfully!", UiSymbols.Party);
                logger.LogInformation("Assets generated in: {AssetsPath}", assetsDir.FullName);

                return 0;
            }
            catch (Exception ex)
            {
                logger.LogError("{UISymbol} Error updating assets: {ErrorMessage}", UiSymbols.Error, ex.Message);
                logger.LogDebug("Stack Trace: {StackTrace}", ex.StackTrace);
                return 1;
            }
        }

        private void VerifyManifestAssetReferences(FileInfo manifestPath)
        {
            try
            {
                var doc = new XmlDocument();
                doc.Load(manifestPath.FullName);

                var nsmgr = new XmlNamespaceManager(doc.NameTable);
                nsmgr.AddNamespace("m", "http://schemas.microsoft.com/appx/manifest/foundation/windows10");
                nsmgr.AddNamespace("uap", "http://schemas.microsoft.com/appx/manifest/uap/windows10");

                // Check if Logo references exist and use Assets folder
                var logoNode = doc.SelectSingleNode("//m:Properties/m:Logo", nsmgr);
                var visualElementsNode = doc.SelectSingleNode("//uap:VisualElements", nsmgr);

                var hasAssetReferences = false;
                if (logoNode?.InnerText.Contains("Assets", StringComparison.OrdinalIgnoreCase) == true)
                {
                    hasAssetReferences = true;
                }

                if (visualElementsNode?.Attributes != null)
                {
                    foreach (XmlAttribute attr in visualElementsNode.Attributes)
                    {
                        if (attr.Value.Contains("Assets", StringComparison.OrdinalIgnoreCase))
                        {
                            hasAssetReferences = true;
                            break;
                        }
                    }
                }

                if (!hasAssetReferences)
                {
                    logger.LogWarning("{UISymbol} Manifest may not reference the Assets directory. Image assets were generated but may not be used by the manifest.", UiSymbols.Warning);
                    logger.LogInformation("Consider updating your manifest to reference assets like: Assets\\Square150x150Logo.png");
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug("Could not verify manifest asset references: {ErrorMessage}", ex.Message);
            }
        }
    }
}
