// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Text;
using Winsdk.Cli.Models;

namespace Winsdk.Cli.Services;

/// <summary>
/// Shared service for manifest template operations and utilities
/// </summary>
internal class ManifestTemplateService(ILogger<ManifestTemplateService> logger) : IManifestTemplateService
{
    private static readonly char[] WordSeparators = [' ', '-', '_'];

    /// <summary>
    /// Finds an embedded resource that ends with the specified suffix
    /// </summary>
    /// <param name="endsWith">The suffix to search for</param>
    /// <returns>Resource name if found, null otherwise</returns>
    private static string? FindResourceEnding(string endsWith)
    {
        var asm = Assembly.GetExecutingAssembly();
        return asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(endsWith, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Converts a string to camelCase format
    /// </summary>
    /// <param name="input">Input string to convert</param>
    /// <returns>camelCase formatted string</returns>
    private static string ToCamelCase(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var words = input.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
        var result = new StringBuilder();

        for (int i = 0; i < words.Length; i++)
        {
            var word = words[i];
            if (i == 0)
            {
                result.Append(char.ToLowerInvariant(word[0]) + word[1..]);
            }
            else
            {
                result.Append(char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant());
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Strips CN= prefix from publisher name if present
    /// </summary>
    /// <param name="publisher">Publisher string</param>
    /// <returns>Publisher without CN= prefix</returns>
    public static string StripCnPrefix(string publisher)
    {
        var trimmed = publisher.Trim().Trim('"', '\'');
        return trimmed.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)
            ? trimmed[3..]
            : trimmed;
    }

    /// <summary>
    /// Ensures publisher name has CN= prefix
    /// </summary>
    /// <param name="publisher">Publisher string</param>
    /// <returns>Publisher with CN= prefix</returns>
    private static string NormalizePublisher(string publisher)
    {
        var trimmed = publisher.Trim().Trim('"', '\'');
        return trimmed.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : "CN=" + trimmed;
    }

    /// <summary>
    /// Loads a manifest template from embedded resources
    /// </summary>
    /// <param name="templateSuffix">Template suffix (e.g., "sparse", "packaged")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Template content as string</returns>
    /// <exception cref="FileNotFoundException">Thrown when template is not found</exception>
    private static Task<string> LoadManifestTemplateAsync(string templateSuffix, CancellationToken cancellationToken = default)
    {
        return LoadTemplateAsync($"appxmanifest.{templateSuffix}.xml", cancellationToken);
    }

    private static async Task<string> LoadTemplateAsync(string template, CancellationToken cancellationToken = default)
    {
        var templateResName = FindResourceEnding($".Templates.{template}")
                              ?? throw new FileNotFoundException($"Embedded template not found: {template}");

        var asm = Assembly.GetExecutingAssembly();
        await using var stream = asm.GetManifestResourceStream(templateResName)
            ?? throw new FileNotFoundException($"Template resource not found: {templateResName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);

        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static string ApplyTemplateReplacements(
        string template,
        string packageName,
        string publisherName,
        string version,
        string entryPoint,
        string description,
        string? hostId,
        string? hostParameters,
        string? hostRuntimeDependencyPackageName,
        string? hostRuntimeDependencyPublisherName,
        string? hostRuntimeDependencyMinVersion)
    {
        var packageNameCamel = ToCamelCase(packageName);

        var result = template
            .Replace("{PackageName}", packageName)
            .Replace("{PackageNameCamelCase}", packageNameCamel)
            .Replace("{PublisherName}", publisherName)
            .Replace("Version=\"1.0.0.0\"", $"Version=\"{version}\"")
            .Replace("{Executable}", entryPoint)
            .Replace("{Description}", description)
            .Replace("{HostId}", hostId)
            .Replace("{HostParameters}", hostParameters)
            .Replace("{HostRuntimeDependencyPackageName}", hostRuntimeDependencyPackageName)
            .Replace("{HostRuntimeDependencyPublisherName}", hostRuntimeDependencyPublisherName)
            .Replace("{HostRuntimeDependencyMinVersion}", hostRuntimeDependencyMinVersion);

        return result;
    }

    /// <summary>
    /// Generates default MSIX assets from embedded resources
    /// </summary>
    /// <param name="outputDirectory">Directory to generate assets in</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async Task GenerateDefaultAssetsAsync(DirectoryInfo outputDirectory, CancellationToken cancellationToken = default)
    {
        var assetsDir = outputDirectory.CreateSubdirectory("Assets");

        var asm = Assembly.GetExecutingAssembly();
        var resPrefix = ".Assets.msix_default_assets.";
        var assetNames = asm.GetManifestResourceNames()
            .Where(n => n.Contains(resPrefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var res in assetNames)
        {
            var fileName = res.Substring(res.LastIndexOf(resPrefix, StringComparison.OrdinalIgnoreCase) + resPrefix.Length);
            var target = Path.Combine(assetsDir.FullName, fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            await using var s = asm.GetManifestResourceStream(res)!;
            await using var fs = File.Create(target);
            await s.CopyToAsync(fs, cancellationToken);

            logger.LogDebug("✓ Generated asset: {FileName}", fileName);
        }
    }

    /// <summary>
    /// Generates a complete manifest with defaults, template processing, and asset generation
    /// </summary>
    /// <param name="outputDirectory">Directory to generate manifest and assets in</param>
    /// <param name="packageName">Package name (null for auto-generated from directory)</param>
    /// <param name="publisherName">Publisher name (null for current user default)</param>
    /// <param name="version">Version string</param>
    /// <param name="entryPoint">Entry point / executable name (null for auto-generated from package name)</param>
    /// <param name="manifestTemplate">Manifest template type</param>
    /// <param name="description">Description for manifest</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task GenerateCompleteManifestAsync(
        DirectoryInfo outputDirectory,
        string packageName,
        string publisherName,
        string version,
        string entryPoint,
        ManifestTemplates manifestTemplate,
        string description,
        string? hostId,
        string? hostParameters,
        string? hostRuntimeDependencyPackageName,
        string? hostRuntimeDependencyPublisherName,
        string? hostRuntimeDependencyMinVersion,
        CancellationToken cancellationToken = default)
    {
        // Normalize publisher name
        publisherName = StripCnPrefix(NormalizePublisher(publisherName));

        logger.LogDebug("Package name: {PackageName}", packageName);
        logger.LogDebug("Publisher: {PublisherName}", publisherName);
        logger.LogDebug("Version: {Version}", version);
        logger.LogDebug("Description: {Description}", description);
        if (!string.IsNullOrEmpty(hostId))
        {
            logger.LogDebug("Host ID: {HostId}", hostId);
        }
        if (!string.IsNullOrEmpty(hostParameters))
        {
            logger.LogDebug("Host Parameters: {HostParameters}", hostParameters);
        }
        if (!string.IsNullOrEmpty(hostRuntimeDependencyPackageName))
        {
            logger.LogDebug("Host Runtime Dependency Package Name: {HostRuntimeDependencyPackageName}", hostRuntimeDependencyPackageName);
        }
        if (!string.IsNullOrEmpty(hostRuntimeDependencyPublisherName))
        {
            hostRuntimeDependencyPublisherName = StripCnPrefix(NormalizePublisher(hostRuntimeDependencyPublisherName));
            logger.LogDebug("Host Runtime Dependency Publisher Name: {HostRuntimeDependencyPublisherName}", hostRuntimeDependencyPublisherName);
        }
        if (!string.IsNullOrEmpty(hostRuntimeDependencyMinVersion))
        {
            logger.LogDebug("Host Runtime Dependency Min Version: {HostRuntimeDependencyMinVersion}", hostRuntimeDependencyMinVersion);
        }

        logger.LogDebug("EntryPoint/Executable: {EntryPoint}", entryPoint);
        logger.LogDebug("Manifest template: {ManifestTemplate}", manifestTemplate);

        // Create output directory if needed
        outputDirectory.Create();

        // Generate manifest content using templates
        string templateSuffix = manifestTemplate.ToString().ToLower();
        var template = await LoadManifestTemplateAsync(templateSuffix, cancellationToken);

        var content = ApplyTemplateReplacements(
            template,
            packageName,
            publisherName,
            version,
            entryPoint,
            description,
            hostId,
            hostParameters,
            hostRuntimeDependencyPackageName,
            hostRuntimeDependencyPublisherName,
            hostRuntimeDependencyMinVersion);

        // Write manifest file
        var manifestPath = Path.Combine(outputDirectory.FullName, "appxmanifest.xml");
        await File.WriteAllTextAsync(manifestPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

        // Generate default assets
        await GenerateDefaultAssetsAsync(outputDirectory, cancellationToken);
    }
}
