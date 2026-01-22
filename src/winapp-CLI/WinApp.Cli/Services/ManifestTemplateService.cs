// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Shared service for manifest template operations and utilities
/// </summary>
internal partial class ManifestTemplateService : IManifestTemplateService
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
        var applicationId = FixAsciiWindowsId(ToCamelCase(packageName));

        var result = template
            .Replace("{PackageName}", packageName)
            .Replace("{ApplicationId}", applicationId)
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

    [GeneratedRegex(@"[^A-Za-z0-9.]")]
    private static partial Regex InvalidWindowsIdCharRegex();

    [GeneratedRegex(@"[^A-Za-z0-9]")]
    private static partial Regex InvalidWindowsIdSegmentCharRegex();

    public static string FixAsciiWindowsId(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "Default"; // fallback
        }

        // 1. Replace invalid chars with dot (keep only letters, digits, dot)
        var cleaned = InvalidWindowsIdCharRegex().Replace(input, ".");

        // 2. Split into segments
        var segments = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries);

        var fixedSegments = new List<string>();

        foreach (var seg in segments)
        {
            var s = seg;

            // If segment doesn't start with a letter, prepend 'A'
            if (s.Length == 0 || !char.IsLetter(s[0]))
            {
                s = "A" + s;
            }

            // Now strip anything that isn't letter/digit
            s = InvalidWindowsIdSegmentCharRegex().Replace(s, "");

            // If still empty (e.g., segment was just digits or invalid), use a fallback
            if (s.Length == 0)
            {
                s = "A";
            }

            fixedSegments.Add(s);
        }

        // Reassemble
        var result = string.Join(".", fixedSegments);

        // Enforce max length
        if (result.Length > 255)
        {
            result = result[..255];
        }

        return result;
    }

    /// <summary>
    /// Generates default MSIX assets from embedded resources
    /// </summary>
    /// <param name="outputDirectory">Directory to generate assets in</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private static async Task GenerateDefaultAssetsAsync(DirectoryInfo outputDirectory, TaskContext taskContext, CancellationToken cancellationToken = default)
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

            taskContext.AddDebugMessage($"{UiSymbols.Check} Generated asset: {fileName}");
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
        TaskContext taskContext,
        CancellationToken cancellationToken = default)
    {
        // Normalize publisher name
        publisherName = StripCnPrefix(NormalizePublisher(publisherName));

        taskContext.AddDebugMessage($"Package name: {packageName}");
        taskContext.AddDebugMessage($"Publisher: {publisherName}");
        taskContext.AddDebugMessage($"Version: {version}");
        taskContext.AddDebugMessage($"Description: {description}");
        if (!string.IsNullOrEmpty(hostId))
        {
            taskContext.AddDebugMessage($"Host ID: {hostId}");
        }
        if (!string.IsNullOrEmpty(hostParameters))
        {
            taskContext.AddDebugMessage($"Host Parameters: {hostParameters}");
        }
        if (!string.IsNullOrEmpty(hostRuntimeDependencyPackageName))
        {
            taskContext.AddDebugMessage($"Host Runtime Dependency Package Name: {hostRuntimeDependencyPackageName}");
        }
        if (!string.IsNullOrEmpty(hostRuntimeDependencyPublisherName))
        {
            hostRuntimeDependencyPublisherName = StripCnPrefix(NormalizePublisher(hostRuntimeDependencyPublisherName));
            taskContext.AddDebugMessage($"Host Runtime Dependency Publisher Name: {hostRuntimeDependencyPublisherName}");
        }
        if (!string.IsNullOrEmpty(hostRuntimeDependencyMinVersion))
        {
            taskContext.AddDebugMessage($"Host Runtime Dependency Min Version: {hostRuntimeDependencyMinVersion}");
        }

        taskContext.AddDebugMessage($"EntryPoint/Executable: {entryPoint}");
        taskContext.AddDebugMessage($"Manifest template: {manifestTemplate}");

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
        await GenerateDefaultAssetsAsync(outputDirectory, taskContext, cancellationToken);
    }
}
