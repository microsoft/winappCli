// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Spectre.Console;
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
        string description,
        string? entryPoint,
        bool useDefaults,
        CancellationToken cancellationToken = default)
    {
        // Interactive mode if not --use-defaults (get defaults for prompts)
        if (string.IsNullOrEmpty(entryPoint))
        {
            packageName ??= SystemDefaultsHelper.GetDefaultPackageName(directory);
        }
        else
        {
            packageName ??= Path.GetFileNameWithoutExtension(entryPoint);
        }
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

        // If logo path is provided, copy it as additional asset
        if (logoPath?.Exists == true)
        {
            await CopyLogoAsAdditionalAssetAsync(directory, logoPath, taskContext, cancellationToken);
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

    private static async Task CopyLogoAsAdditionalAssetAsync(DirectoryInfo directory, FileInfo logoPath, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        var assetsDir = directory.CreateSubdirectory("Assets");

        var logoFileName = logoPath.Name;
        var destinationPath = Path.Combine(assetsDir.FullName, logoFileName);

        using var sourceStream = new FileStream(logoPath.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
        using var destinationStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);

        await sourceStream.CopyToAsync(destinationStream, cancellationToken);

        taskContext.AddDebugMessage($"Logo copied to: {destinationPath}");
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

    [GeneratedRegex(@"[^A-Za-z0-9\-_. ]")]
    private static partial Regex InvalidPackageNameCharRegex();

    public async Task UpdateManifestAssetsAsync(
        FileInfo manifestPath,
        FileInfo imagePath,
        TaskContext taskContext,
        CancellationToken cancellationToken = default)
    {
        taskContext.AddStatusMessage($"{UiSymbols.Info} Updating assets for manifest: {manifestPath.Name}");

        // Determine the Assets directory relative to the manifest
        var manifestDir = manifestPath.Directory;
        if (manifestDir == null)
        {
            throw new InvalidOperationException("Could not determine manifest directory");
        }

        var assetsDir = manifestDir.CreateSubdirectory("Assets");

        // Generate the image assets
        await imageAssetService.GenerateAssetsAsync(imagePath, assetsDir, taskContext, cancellationToken);

        // Verify that the manifest references the Assets directory correctly
        VerifyManifestAssetReferences(manifestPath, taskContext);

        taskContext.AddStatusMessage($"{UiSymbols.Party} Image assets updated successfully!");
        taskContext.AddStatusMessage($"Assets generated in: {assetsDir.FullName}");
    }

    private static void VerifyManifestAssetReferences(FileInfo manifestPath, TaskContext taskContext)
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
                taskContext.AddStatusMessage($"{UiSymbols.Warning} Manifest may not reference the Assets directory. Image assets were generated but may not be used by the manifest.");
                taskContext.AddStatusMessage("Consider updating your manifest to reference assets like: Assets\\Square150x150Logo.png");
            }
        }
        catch (Exception ex)
        {
            taskContext.AddDebugMessage($"Could not verify manifest asset references: {ex.Message}");
        }
    }
}
