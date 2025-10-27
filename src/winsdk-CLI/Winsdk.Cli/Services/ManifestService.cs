// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using Winsdk.Cli.Helpers;
using Winsdk.Cli.Models;

namespace Winsdk.Cli.Services;

internal partial class ManifestService(
    IManifestTemplateService manifestTemplateService,
    ILogger<ManifestService> logger) : IManifestService
{
    public async Task GenerateManifestAsync(
        string directory,
        string? packageName,
        string? publisherName,
        string version,
        string description,
        string? entryPoint,
        ManifestTemplates manifestTemplate,
        string? logoPath,
        bool yes,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Generating manifest in directory: {Directory}", directory);

        // Check if manifest already exists
        var manifestPath = MsixService.FindProjectManifest(directory);
        if (File.Exists(manifestPath))
        {
            throw new InvalidOperationException($"Manifest already exists at: {manifestPath}");
        }

        // Interactive mode if not --yes (get defaults for prompts)
        packageName ??= SystemDefaultsHelper.GetDefaultPackageName(directory);
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

        logger.LogDebug("Logo path: {LogoPath}", logoPath ?? "None");

        packageName = CleanPackageName(packageName);

        string? hostId = null;
        string? hostParameters = null;
        string? hostRuntimeDependencyPackageName = null;
        string? hostRuntimeDependencyPublisherName = null;
        string? hostRuntimeDependencyMinVersion = null;
        if (manifestTemplate == ManifestTemplates.HostedApp)
        {
            var entryPointAbsolute = Path.IsPathRooted(entryPoint)
                ? entryPoint
                : Path.GetFullPath(Path.Combine(directory, entryPoint));
            if (!File.Exists(entryPointAbsolute))
            {
                logger.LogDebug("Hosted app entry point file not found: {EntryPointAbsolute}", entryPointAbsolute);
                throw new FileNotFoundException($"Hosted app entry point file not found.", entryPointAbsolute);
            }

            var relativeEntryPoint = Path.GetRelativePath(directory, entryPointAbsolute);

            // TODO: generalize this mapping or move to a config file
            if (entryPoint.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            {
                hostId = "Python314";
                hostParameters = $"$(package.effectivePath)\\{relativeEntryPoint}";
                hostRuntimeDependencyPackageName = "Python314";
                hostRuntimeDependencyPublisherName = "Test Publisher";
                hostRuntimeDependencyMinVersion = "3.14.0.0";
            }
            else if (entryPoint.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            {
                hostId = "Nodejs22";
                hostParameters = $"$(package.effectivePath)\\{relativeEntryPoint}";
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

        // If logo path is provided, copy it as additional asset
        if (!string.IsNullOrEmpty(logoPath) && File.Exists(logoPath))
        {
            await CopyLogoAsAdditionalAssetAsync(directory, logoPath, cancellationToken);
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

    private async Task CopyLogoAsAdditionalAssetAsync(string directory, string logoPath, CancellationToken cancellationToken = default)
    {
        var assetsDir = Path.Combine(directory, "Assets");
        Directory.CreateDirectory(assetsDir);

        var logoFileName = Path.GetFileName(logoPath);
        var destinationPath = Path.Combine(assetsDir, logoFileName);

        using var sourceStream = new FileStream(logoPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
        using var destinationStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);

        await sourceStream.CopyToAsync(destinationStream, cancellationToken);

        logger.LogDebug("Logo copied to: {DestinationPath}", destinationPath);
    }

    private string PromptForValue(string prompt, string defaultValue)
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
}
