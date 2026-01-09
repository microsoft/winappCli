// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal partial class ManifestService(
    IManifestTemplateService manifestTemplateService,
    IImageAssetService imageAssetService,
    ICurrentDirectoryProvider currentDirectoryProvider,
    ILogger<ManifestService> logger) : IManifestService
{
    public async Task GenerateManifestAsync(
        DirectoryInfo directory,
        string? packageName,
        string? publisherName,
        string version,
        string? description,
        string? entryPoint,
        ManifestTemplates manifestTemplate,
        FileInfo? logoPath,
        bool yes,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Generating manifest in directory: {Directory}", directory);

        // Check if manifest already exists
        var manifestPath = MsixService.FindProjectManifest(currentDirectoryProvider, directory);
        if (manifestPath?.Exists == true)
        {
            throw new InvalidOperationException($"Manifest already exists at: {manifestPath}");
        }

        // Interactive mode if not --yes (get defaults for prompts)
        if (!string.IsNullOrEmpty(entryPoint))
        {
            var fileVersionInfo = FileVersionInfo.GetVersionInfo(entryPoint);
            packageName ??= !string.IsNullOrWhiteSpace(fileVersionInfo.FileDescription)
                ? fileVersionInfo.FileDescription
                : Path.GetFileNameWithoutExtension(entryPoint);
            if (!string.IsNullOrWhiteSpace(fileVersionInfo.Comments))
            {
                description = fileVersionInfo.Comments;
            }
            if (string.IsNullOrWhiteSpace(description) || description == packageName)
            {
                description = fileVersionInfo.FileDescription;
            }
            if (!string.IsNullOrWhiteSpace(fileVersionInfo.CompanyName))
            {
                publisherName ??= fileVersionInfo.CompanyName;
            }
        }
        packageName ??= SystemDefaultsHelper.GetDefaultPackageName(directory);
        description ??= SystemDefaultsHelper.GetDefaultDescription();
        publisherName ??= SystemDefaultsHelper.GetDefaultPublisherCN();
        entryPoint ??= $"{packageName}.exe";

        // Interactive mode if not --yes
        if (!yes)
        {
            packageName = PromptForValue("Package name", packageName);
            publisherName = PromptForValue("Publisher name", publisherName);
            version = PromptForValue("Version", version);
            description = PromptForValue("Description", description);
            entryPoint = PromptForValue("EntryPoint/Executable", entryPoint);
        }

        logger.LogDebug("Logo path: {LogoPath}", logoPath?.FullName ?? "None");

        packageName = CleanPackageName(packageName);

        var entryPointAbsolute = Path.IsPathRooted(entryPoint)
                ? entryPoint
                : Path.GetFullPath(Path.Combine(directory.FullName, entryPoint));

        entryPoint = Path.GetRelativePath(directory.FullName, entryPointAbsolute);

        string? hostId = null;
        string? hostParameters = null;
        string? hostRuntimeDependencyPackageName = null;
        string? hostRuntimeDependencyPublisherName = null;
        string? hostRuntimeDependencyMinVersion = null;
        if (manifestTemplate == ManifestTemplates.HostedApp)
        {
            if (!File.Exists(entryPointAbsolute))
            {
                logger.LogDebug("Hosted app entry point file not found: {EntryPointAbsolute}", entryPointAbsolute);
                throw new FileNotFoundException($"Hosted app entry point file not found.", entryPointAbsolute);
            }

            // TODO: generalize this mapping or move to a config file
            if (entryPoint.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            {
                hostId = "Python314";
                hostParameters = $"$(package.effectivePath)\\{entryPoint}";
                hostRuntimeDependencyPackageName = "Python314";
                hostRuntimeDependencyPublisherName = "Test Publisher";
                hostRuntimeDependencyMinVersion = "3.14.0.0";
            }
            else if (entryPoint.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            {
                hostId = "Nodejs22";
                hostParameters = $"$(package.effectivePath)\\{entryPoint}";
                hostRuntimeDependencyPackageName = "Nodejs22";
                hostRuntimeDependencyPublisherName = "Test Publisher";
                hostRuntimeDependencyMinVersion = "22.21.0.0";
            }
            else
            {
                throw new InvalidOperationException("Unsupported hosted app executable type. Only .py and .js are supported.");
            }
        }

        // Generate complete manifest using shared service
        await manifestTemplateService.GenerateCompleteManifestAsync(
            directory,
            packageName,
            publisherName,
            version,
            entryPoint,
            manifestTemplate,
            description,
            hostId,
            hostParameters,
            hostRuntimeDependencyPackageName,
            hostRuntimeDependencyPublisherName,
            hostRuntimeDependencyMinVersion,
            cancellationToken);

        string? extractedLogoPath = null;

        // If no logo provided, extract from entry point
        if (logoPath == null)
        {
            logger.LogDebug("No logo path provided, attempting to extract from entry point: {EntryPointAbsolute}", entryPointAbsolute);
            Icon? extractedIcon = null;
            try
            {
                extractedIcon = ShellIcon.GetJumboIcon(entryPointAbsolute);
                // save temporary
                if (extractedIcon != null)
                {
                    string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                    Directory.CreateDirectory(tempDir);

                    extractedLogoPath = Path.Combine(tempDir, "StoreLogo.png");
                    using (var stream = new FileStream(extractedLogoPath, FileMode.Create))
                    {
                        extractedIcon.ToBitmap().Save(stream, ImageFormat.Png);
                    }

                    logoPath = new FileInfo(extractedLogoPath);
                    logger.LogDebug("Extracted logo path: {ExtractedLogoPath}", logoPath.FullName);
                }
            }
            finally
            {
                if (extractedIcon != null)
                {
                    extractedIcon.Dispose();
                }
            }
        }

        manifestPath ??= new FileInfo(Path.Combine(directory.FullName, "appxmanifest.xml"));

        // If logo path is provided, update manifest assets
        if (logoPath?.Exists == true)
        {
            await UpdateManifestAssetsAsync(manifestPath, logoPath, cancellationToken);
        }

        if (extractedLogoPath != null)
        {
            // Clean up temporary extracted logo
            try
            {
                File.Delete(extractedLogoPath);
                Directory.Delete(Path.GetDirectoryName(extractedLogoPath)!);
            }
            catch (Exception ex)
            {
                logger.LogDebug("Could not delete temporary extracted logo: {ErrorMessage}", ex.Message);
            }
        }
    }

    /// <summary>
    /// Cleans and sanitizes a package name to meet MSIX AppxManifest schema requirements.
    /// Based on ST_PackageName type which restricts ST_AsciiIdentifier.
    /// </summary>
    /// <param name="packageName">The package name to clean</param>
    /// <returns>A cleaned package name that meets MSIX schema requirements</returns>
    internal static string CleanPackageName(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return "DefaultPackage";
        }

        // Trim whitespace
        var cleaned = packageName.Trim();

        // Remove invalid characters (keep only letters, numbers, hyphens, underscores, periods, and spaces)
        // ST_AllowedAsciiCharSet pattern="[-_. A-Za-z0-9]+"
        cleaned = InvalidPackageNameCharRegex().Replace(cleaned, "");

        // Check if it starts with underscore BEFORE removing them
        bool startsWithUnderscore = cleaned.StartsWith('_');

        // Remove leading underscores (ST_AsciiIdentifier restriction)
        cleaned = cleaned.TrimStart('_');

        // If still empty or whitespace after cleaning, use default
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = "DefaultPackage";
        }

        // If originally started with underscore, prepend "App"
        if (startsWithUnderscore)
        {
            cleaned = "App" + cleaned;
        }

        // Ensure minimum length of 3 characters
        if (cleaned.Length < 3)
        {
            cleaned = cleaned.PadRight(3, '1'); // Pad with '1' to reach minimum length
        }

        // Truncate to maximum length of 50 characters
        if (cleaned.Length > 50)
        {
            cleaned = cleaned[..50].TrimEnd(); // Trim end in case we cut off mid-word
        }

        return cleaned;
    }

    private static string PromptForValue(string prompt, string defaultValue)
    {
        if (!string.IsNullOrEmpty(defaultValue))
        {
            return defaultValue;
        }

        Console.Write($"{prompt} ({defaultValue}): ");
        var input = Console.ReadLine();
        return string.IsNullOrWhiteSpace(input) ? defaultValue : input.Trim();
    }

    [GeneratedRegex(@"[^A-Za-z0-9\-_. ]")]
    private static partial Regex InvalidPackageNameCharRegex();

    public async Task UpdateManifestAssetsAsync(
        FileInfo manifestPath,
        FileInfo imagePath,
        CancellationToken cancellationToken = default)
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
    }

    private void VerifyManifestAssetReferences(FileInfo manifestPath)
    {
        try
        {
            var doc = new System.Xml.XmlDocument();
            doc.Load(manifestPath.FullName);

            var nsmgr = new System.Xml.XmlNamespaceManager(doc.NameTable);
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
                foreach (System.Xml.XmlAttribute attr in visualElementsNode.Attributes)
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
