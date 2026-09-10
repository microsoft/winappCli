// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
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
    /// Strips CN= prefix from publisher name if present.
    /// For non-CN DNs, returns the input unchanged.
    /// </summary>
    /// <param name="publisher">Publisher string</param>
    /// <returns>Publisher without CN= prefix</returns>
    public static string StripCnPrefix(string publisher)
    {
        return PublisherDnHelper.GetDisplayName(publisher);
    }

    /// <summary>
    /// Returns true if the input is already a valid X.500 distinguished name
    /// (e.g., "CN=Name", "OU=Finance, DC=corp, DC=com").
    /// </summary>
    internal static bool IsDistinguishedName(string input)
    {
        return PublisherDnHelper.IsDistinguishedName(input);
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
        string publisherDN,
        string version,
        string description,
        string? displayName,
        string? applicationId)
    {
        // Both overrides default to the pre-existing derivation, so callers that don't supply them
        // (manifest generate, init, sparse identity) produce byte-identical output to before.
        var resolvedApplicationId = string.IsNullOrWhiteSpace(applicationId)
            ? FixAsciiWindowsId(ToCamelCase(packageName))
            : FixAsciiWindowsId(applicationId);

        var resolvedDisplayName = string.IsNullOrWhiteSpace(displayName) ? packageName : displayName;

        var result = template
            .Replace("{PackageName}", packageName)
            .Replace("{DisplayName}", PublisherDnHelper.XmlEscape(resolvedDisplayName))
            .Replace("{ApplicationId}", resolvedApplicationId)
            .Replace("{PublisherDN}", PublisherDnHelper.XmlEscape(publisherDN))
            .Replace("{PublisherName}", PublisherDnHelper.XmlEscape(publisherName))
            .Replace("Version=\"1.0.0.0\"", $"Version=\"{version}\"")
            .Replace("{Description}", PublisherDnHelper.XmlEscape(description));

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

        // If the input was made up entirely of separators (e.g. "..."), every segment
        // was empty and reassembly yields "", which is not a valid Windows id. Fall back.
        if (string.IsNullOrEmpty(result))
        {
            return "Default";
        }

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
    /// <param name="manifestTemplate">Manifest template type</param>
    /// <param name="description">Description for manifest</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task GenerateCompleteManifestAsync(
        DirectoryInfo outputDirectory,
        string packageName,
        string publisherName,
        string version,
        ManifestTemplates manifestTemplate,
        string description,
        TaskContext taskContext,
        string manifestFileName = "Package.appxmanifest",
        string? executableName = null,
        string? displayName = null,
        string? applicationId = null,
        CancellationToken cancellationToken = default)
    {
        // Normalize publisher to a valid distinguished name
        var publisherDN = PublisherDnHelper.Normalize(publisherName);
        var publisherDisplayName = PublisherDnHelper.GetDisplayName(publisherDN);

        taskContext.AddDebugMessage($"Package name: {packageName}");
        taskContext.AddDebugMessage($"Publisher: {publisherDN}");
        taskContext.AddDebugMessage($"Version: {version}");
        taskContext.AddDebugMessage($"Description: {description}");
        taskContext.AddDebugMessage($"Manifest template: {manifestTemplate}");

        // Create output directory if needed
        outputDirectory.Create();

        // Generate manifest content using templates
        string templateSuffix = manifestTemplate.ToString().ToLower();
        var template = await LoadManifestTemplateAsync(templateSuffix, cancellationToken);

        var content = ApplyTemplateReplacements(
            template,
            packageName,
            publisherDisplayName,
            publisherDN,
            version,
            description,
            displayName,
            applicationId);

        // When a concrete executable name is provided (e.g. sparse identity packages
        // that reference an external exe), replace the $targetnametoken$ build token so
        // the manifest is self-contained and can be packed without --executable.
        if (!string.IsNullOrWhiteSpace(executableName))
        {
            content = content.Replace("$targetnametoken$.exe", PublisherDnHelper.XmlEscape(executableName));
        }

        // Write manifest file. manifestFileName must be a bare file name; a rooted or nested value
        // would make Path.Combine silently drop outputDirectory and write outside the intended folder.
        if (Path.IsPathRooted(manifestFileName) || manifestFileName != Path.GetFileName(manifestFileName))
        {
            throw new ArgumentException(
                $"manifestFileName must be a bare file name, not a path: '{manifestFileName}'.", nameof(manifestFileName));
        }

        // Validated above to be a bare file name; use the sanitized local so the combine is
        // unambiguously within outputDirectory.
        var safeManifestFileName = Path.GetFileName(manifestFileName);
        var manifestPath = Path.Join(outputDirectory.FullName, safeManifestFileName);
        await File.WriteAllTextAsync(manifestPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

        // Generate default assets
        await GenerateDefaultAssetsAsync(outputDirectory, taskContext, cancellationToken);
    }
}
