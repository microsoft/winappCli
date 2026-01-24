// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Spectre.Console;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal partial class ManifestService(
    IManifestTemplateService manifestTemplateService,
    IImageAssetService imageAssetService,
    IAnsiConsole ansiConsole) : IManifestService
{
    public async Task<ManifestGenerationInfo> PromptForManifestInfoAsync(
        DirectoryInfo directory,
        string? packageName,
        string? publisherName,
        string version,
        string? description,
        string? entryPoint,
        bool useDefaults,
        CancellationToken cancellationToken = default)
    {
        // Interactive mode if not --use-defaults (get defaults for prompts)
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

        packageName = CleanPackageName(packageName);

        // Interactive mode if not --use-defaults
        if (!useDefaults)
        {
            packageName = await PromptForValueAsync(ansiConsole, "Package name", packageName, cancellationToken);
            publisherName = await PromptForValueAsync(ansiConsole, "Publisher name", publisherName, cancellationToken);
            version = await PromptForValueAsync(ansiConsole, "Version", version, cancellationToken);
            description = await PromptForValueAsync(ansiConsole, "Description", description, cancellationToken);
            entryPoint = await PromptForValueAsync(ansiConsole, "EntryPoint/Executable", entryPoint, cancellationToken);
        }

        return new ManifestGenerationInfo(
            packageName,
            publisherName,
            version,
            description,
            entryPoint);
    }

    public async Task GenerateManifestAsync(
        DirectoryInfo directory,
        ManifestGenerationInfo manifestGenerationInfo,
        ManifestTemplates manifestTemplate,
        FileInfo? logoPath,
        TaskContext taskContext,
        CancellationToken cancellationToken = default)
    {
        taskContext.AddDebugMessage($"Generating manifest in directory: {directory}");

        string? packageName = manifestGenerationInfo.PackageName;
        string? publisherName = manifestGenerationInfo.PublisherName;
        string version = manifestGenerationInfo.Version;
        string description = manifestGenerationInfo.Description;
        string? entryPoint = manifestGenerationInfo.EntryPoint;

        taskContext.AddDebugMessage($"Logo path: {logoPath?.FullName ?? "None"}");

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
                taskContext.AddDebugMessage($"Hosted app entry point file not found: {entryPointAbsolute}");
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
            taskContext,
            cancellationToken);

        string? extractedLogoPath = null;

        // If no logo provided, extract from entry point
        if (logoPath == null)
        {
            taskContext.AddDebugMessage($"No logo path provided, attempting to extract from entry point: {entryPointAbsolute}");
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
                    taskContext.AddDebugMessage($"Extracted logo path: {logoPath.FullName}");
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


        // If logo path is provided, update manifest assets
        if (logoPath?.Exists == true)
        {
            var manifestPath = new FileInfo(Path.Combine(directory.FullName, "appxmanifest.xml"));
            await UpdateManifestAssetsAsync(manifestPath, logoPath, taskContext, cancellationToken);
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
                taskContext.AddDebugMessage($"Could not delete temporary extracted logo: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Cleans and sanitizes a package name to meet MSIX AppxManifest Identity Name schema requirements.
    /// The Identity Name must match the pattern [-.A-Za-z0-9]+ (only letters, digits, periods, and hyphens).
    /// </summary>
    /// <param name="packageName">The package name to clean</param>
    /// <returns>A cleaned package name that meets MSIX Identity Name schema requirements</returns>
    internal static string CleanPackageName(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return "DefaultPackage";
        }

        // Trim whitespace
        var cleaned = packageName.Trim();

        // Remove invalid characters (keep only letters, numbers, hyphens, and periods)
        // MSIX Identity Name schema pattern="[-.A-Za-z0-9]+"
        cleaned = InvalidPackageNameCharRegex().Replace(cleaned, "");

        // If empty or whitespace after cleaning, use default
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = "DefaultPackage";
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

    private static async Task<string> PromptForValueAsync(IAnsiConsole ansiConsole, string prompt, string defaultValue, CancellationToken cancellationToken)
    {
        var result = await ansiConsole.PromptAsync(
            new TextPrompt<string>(prompt)
                .AllowEmpty()
                .DefaultValue(defaultValue)
                .ShowDefaultValue(),
            cancellationToken);

        ansiConsole.Cursor.MoveUp();
        ansiConsole.Write("\x1b[2K"); // Clear line
        ansiConsole.MarkupLine($"{prompt}: [underline]{result}[/]");

        return result;
    }

    [GeneratedRegex(@"[^A-Za-z0-9\-.]")]
    private static partial Regex InvalidPackageNameCharRegex();

    public async Task UpdateManifestAssetsAsync(
        FileInfo manifestPath,
        FileInfo imagePath,
        TaskContext taskContext,
        CancellationToken cancellationToken = default)
    {
        taskContext.AddStatusMessage($"{UiSymbols.Info} Updating assets for manifest: {manifestPath.FullName}");

        // Determine the manifest directory
        var manifestDir = manifestPath.Directory;
        if (manifestDir == null)
        {
            throw new InvalidOperationException("Could not determine manifest directory");
        }

        // Extract asset references from the manifest
        var assetReferences = ExtractAssetReferencesFromManifest(manifestPath, taskContext);

        if (assetReferences.Count > 0)
        {
            // Generate assets based on manifest references
            await imageAssetService.GenerateAssetsFromManifestAsync(imagePath, manifestDir, assetReferences, taskContext, cancellationToken);
        }
        else
        {
            // Fallback to default behavior if no asset references found
            taskContext.AddStatusMessage($"{UiSymbols.Warning} No asset references found in manifest, generating default assets");
            var assetsDir = manifestDir.CreateSubdirectory("Assets");
            await imageAssetService.GenerateAssetsAsync(imagePath, assetsDir, taskContext, cancellationToken);
        }
    }

    /// <summary>
    /// Extracts asset references from an AppxManifest.xml file.
    /// Parses the manifest to find Logo, Square150x150Logo, Square44x44Logo, Wide310x150Logo, 
    /// and other image asset attributes, then determines their expected dimensions.
    /// </summary>
    private static List<ManifestAssetReference> ExtractAssetReferencesFromManifest(FileInfo manifestPath, TaskContext taskContext)
    {
        var assetReferences = new List<ManifestAssetReference>();

        try
        {
            var doc = new System.Xml.XmlDocument();
            doc.Load(manifestPath.FullName);

            var nsmgr = new System.Xml.XmlNamespaceManager(doc.NameTable);
            nsmgr.AddNamespace("m", "http://schemas.microsoft.com/appx/manifest/foundation/windows10");
            nsmgr.AddNamespace("uap", "http://schemas.microsoft.com/appx/manifest/uap/windows10");

            // Known asset types and their base dimensions
            var assetTypeDimensions = new Dictionary<string, (int Width, int Height)>(StringComparer.OrdinalIgnoreCase)
            {
                // Square logos
                { "Square44x44Logo", (44, 44) },
                { "Square71x71Logo", (71, 71) },
                { "Square150x150Logo", (150, 150) },
                { "Square310x310Logo", (310, 310) },
                // Wide logos
                { "Wide310x150Logo", (310, 150) },
                // Store logo (typically 50x50)
                { "Logo", (50, 50) },
                { "StoreLogo", (50, 50) },
                // Splash screen
                { "SplashScreen", (620, 300) },
                // Badge logo
                { "BadgeLogo", (24, 24) },
                // Lock screen logo
                { "LockScreenLogo", (24, 24) },
            };

            var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Extract Logo from Properties
            var logoNode = doc.SelectSingleNode("//m:Properties/m:Logo", nsmgr);
            if (logoNode != null && !string.IsNullOrWhiteSpace(logoNode.InnerText))
            {
                var logoPath = logoNode.InnerText.Trim();
                if (!addedPaths.Contains(logoPath))
                {
                    // Determine dimensions from filename or use default Store logo size
                    var dimensions = GetDimensionsFromPath(logoPath, assetTypeDimensions);
                    assetReferences.Add(new ManifestAssetReference(logoPath, dimensions.Width, dimensions.Height));
                    addedPaths.Add(logoPath);
                    taskContext.AddDebugMessage($"  Found Logo: {logoPath} ({dimensions.Width}x{dimensions.Height})");
                }
            }

            // Extract from uap:VisualElements attributes
            var visualElementsNodes = doc.SelectNodes("//uap:VisualElements", nsmgr);
            if (visualElementsNodes != null)
            {
                foreach (System.Xml.XmlNode visualElements in visualElementsNodes)
                {
                    if (visualElements.Attributes == null)
                    {
                        continue;
                    }

                    foreach (System.Xml.XmlAttribute attr in visualElements.Attributes)
                    {
                        if (assetTypeDimensions.TryGetValue(attr.Name, out var dimensions) && !string.IsNullOrWhiteSpace(attr.Value))
                        {
                            var assetPath = attr.Value.Trim();
                            if (!addedPaths.Contains(assetPath))
                            {
                                assetReferences.Add(new ManifestAssetReference(assetPath, dimensions.Width, dimensions.Height));
                                addedPaths.Add(assetPath);
                                taskContext.AddDebugMessage($"  Found {attr.Name}: {assetPath} ({dimensions.Width}x{dimensions.Height})");
                            }
                        }
                    }
                }
            }

            // Extract from uap:DefaultTile attributes
            var defaultTileNodes = doc.SelectNodes("//uap:DefaultTile", nsmgr);
            if (defaultTileNodes != null)
            {
                foreach (System.Xml.XmlNode defaultTile in defaultTileNodes)
                {
                    if (defaultTile.Attributes == null)
                    {
                        continue;
                    }

                    foreach (System.Xml.XmlAttribute attr in defaultTile.Attributes)
                    {
                        if (assetTypeDimensions.TryGetValue(attr.Name, out var dimensions) && !string.IsNullOrWhiteSpace(attr.Value))
                        {
                            var assetPath = attr.Value.Trim();
                            if (!addedPaths.Contains(assetPath))
                            {
                                assetReferences.Add(new ManifestAssetReference(assetPath, dimensions.Width, dimensions.Height));
                                addedPaths.Add(assetPath);
                                taskContext.AddDebugMessage($"  Found {attr.Name}: {assetPath} ({dimensions.Width}x{dimensions.Height})");
                            }
                        }
                    }
                }
            }

            // Extract from uap:SplashScreen attributes
            var splashScreenNodes = doc.SelectNodes("//uap:SplashScreen", nsmgr);
            if (splashScreenNodes != null)
            {
                foreach (System.Xml.XmlNode splashScreen in splashScreenNodes)
                {
                    var imageAttr = splashScreen.Attributes?["Image"];
                    if (imageAttr != null && !string.IsNullOrWhiteSpace(imageAttr.Value))
                    {
                        var assetPath = imageAttr.Value.Trim();
                        if (!addedPaths.Contains(assetPath))
                        {
                            var dimensions = assetTypeDimensions["SplashScreen"];
                            assetReferences.Add(new ManifestAssetReference(assetPath, dimensions.Width, dimensions.Height));
                            addedPaths.Add(assetPath);
                            taskContext.AddDebugMessage($"  Found SplashScreen: {assetPath} ({dimensions.Width}x{dimensions.Height})");
                        }
                    }
                }
            }

            // Extract from uap:LockScreen attributes
            var lockScreenNodes = doc.SelectNodes("//uap:LockScreen", nsmgr);
            if (lockScreenNodes != null)
            {
                foreach (System.Xml.XmlNode lockScreen in lockScreenNodes)
                {
                    var badgeLogoAttr = lockScreen.Attributes?["BadgeLogo"];
                    if (badgeLogoAttr != null && !string.IsNullOrWhiteSpace(badgeLogoAttr.Value))
                    {
                        var assetPath = badgeLogoAttr.Value.Trim();
                        if (!addedPaths.Contains(assetPath))
                        {
                            var dimensions = assetTypeDimensions["BadgeLogo"];
                            assetReferences.Add(new ManifestAssetReference(assetPath, dimensions.Width, dimensions.Height));
                            addedPaths.Add(assetPath);
                            taskContext.AddDebugMessage($"  Found BadgeLogo: {assetPath} ({dimensions.Width}x{dimensions.Height})");
                        }
                    }
                }
            }

            taskContext.AddDebugMessage($"Extracted {assetReferences.Count} asset references from manifest");
        }
        catch (Exception ex)
        {
            taskContext.AddDebugMessage($"Error parsing manifest for asset references: {ex.Message}");
        }

        return assetReferences;
    }

    /// <summary>
    /// Attempts to determine asset dimensions from the file path/name.
    /// Parses patterns like "Square150x150Logo.png" or "Wide310x150Logo.png".
    /// </summary>
    private static (int Width, int Height) GetDimensionsFromPath(string path, Dictionary<string, (int Width, int Height)> knownDimensions)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);

        // Check if the filename matches any known asset type
        foreach (var kvp in knownDimensions)
        {
            if (fileName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }

        // Try to parse dimensions from filename pattern like "Square150x150" or "Wide310x150"
        var match = DimensionRegex().Match(fileName);
        if (match.Success)
        {
            if (int.TryParse(match.Groups[1].Value, out var width) &&
                int.TryParse(match.Groups[2].Value, out var height))
            {
                return (width, height);
            }
        }

        // Default to store logo size
        return (50, 50);
    }

    [GeneratedRegex(@"(\d+)x(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex DimensionRegex();
}
