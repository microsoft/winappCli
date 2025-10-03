using System.Reflection;
using System.Text;

namespace Winsdk.Cli.Services;

/// <summary>
/// Shared service for manifest template operations and utilities
/// </summary>
internal class ManifestTemplateService
{
    private static readonly char[] WordSeparators = [' ', '-', '_'];

    /// <summary>
    /// Finds an embedded resource that ends with the specified suffix
    /// </summary>
    /// <param name="endsWith">The suffix to search for</param>
    /// <returns>Resource name if found, null otherwise</returns>
    public static string? FindResourceEnding(string endsWith)
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
    public static string ToCamelCase(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

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
    public static string NormalizePublisher(string publisher)
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
    public static async Task<string> LoadManifestTemplateAsync(string templateSuffix, CancellationToken cancellationToken = default)
    {
        var templateResName = FindResourceEnding($".Templates.appxmanifest.{templateSuffix}.xml")
                              ?? throw new FileNotFoundException($"Embedded template not found for suffix: {templateSuffix}");

        var asm = Assembly.GetExecutingAssembly();
        await using var stream = asm.GetManifestResourceStream(templateResName) 
            ?? throw new FileNotFoundException($"Template resource not found: {templateResName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        
        return await reader.ReadToEndAsync(cancellationToken);
    }
    
    /// <summary>
    /// Applies common template replacements to manifest content
    /// </summary>
    /// <param name="template">Template content</param>
    /// <param name="packageName">Package name</param>
    /// <param name="publisherName">Publisher name (without CN= prefix)</param>
    /// <param name="version">Version string</param>
    /// <param name="executable">Executable name</param>
    /// <param name="description">Package description</param>
    /// <returns>Template with replacements applied</returns>
    public static string ApplyTemplateReplacements(
        string template, 
        string packageName, 
        string publisherName, 
        string version, 
        string executable, 
        string description)
    {
        var packageNameCamel = ToCamelCase(packageName);
        
        var result = template
            .Replace("{PackageName}", packageName)
            .Replace("{PackageNameCamelCase}", packageNameCamel)
            .Replace("{PublisherName}", publisherName)
            .Replace("Version=\"1.0.0.0\"", $"Version=\"{version}\"")
            .Replace("{ExecutableName}", executable)
            .Replace("{Executable}", executable)
            .Replace("{Description}", description);

        return result;
    }

    /// <summary>
    /// Generates default MSIX assets from embedded resources
    /// </summary>
    /// <param name="outputDirectory">Directory to generate assets in</param>
    /// <param name="verbose">Enable verbose output</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public static async Task GenerateDefaultAssetsAsync(string outputDirectory, bool verbose = false, CancellationToken cancellationToken = default)
    {
        var assetsDir = Path.Combine(outputDirectory, "Assets");
        Directory.CreateDirectory(assetsDir);

        var asm = Assembly.GetExecutingAssembly();
        var resPrefix = ".Assets.msix_default_assets.";
        var assetNames = asm.GetManifestResourceNames()
            .Where(n => n.Contains(resPrefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var res in assetNames)
        {
            var fileName = res.Substring(res.LastIndexOf(resPrefix, StringComparison.OrdinalIgnoreCase) + resPrefix.Length);
            var target = Path.Combine(assetsDir, fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            
            await using var s = asm.GetManifestResourceStream(res)!;
            await using var fs = File.Create(target);
            await s.CopyToAsync(fs, cancellationToken);
            
            if (verbose)
            {
                Console.WriteLine($"✓ Generated asset: {fileName}");
            }
        }
    }

    /// <summary>
    /// Generates a complete manifest with defaults, template processing, and asset generation
    /// </summary>
    /// <param name="outputDirectory">Directory to generate manifest and assets in</param>
    /// <param name="packageName">Package name (null for auto-generated from directory)</param>
    /// <param name="publisherName">Publisher name (null for current user default)</param>
    /// <param name="version">Version string</param>
    /// <param name="executable">Executable name (null for auto-generated from package name)</param>
    /// <param name="sparse">Whether to generate sparse package manifest</param>
    /// <param name="description">Description for manifest</param>
    /// <param name="verbose">Whether to output verbose information</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Generated manifest content</returns>
    public static async Task GenerateCompleteManifestAsync(
        string outputDirectory,
        string packageName,
        string publisherName, 
        string version,
        string executable,
        bool sparse,
        string description,
        bool verbose = false,
        CancellationToken cancellationToken = default)
    {
        // Normalize publisher name
        publisherName = StripCnPrefix(NormalizePublisher(publisherName));

        if (verbose)
        {
            Console.WriteLine($"Package name: {packageName}");
            Console.WriteLine($"Publisher: {publisherName}");
            Console.WriteLine($"Version: {version}");
            Console.WriteLine($"Description: {description}");
            Console.WriteLine($"Executable: {executable}");
            Console.WriteLine($"Sparse: {sparse}");
        }

        // Create output directory if needed
        Directory.CreateDirectory(outputDirectory);

        // Generate manifest content using templates
        var templateSuffix = sparse ? "sparse" : "packaged";
        var template = await LoadManifestTemplateAsync(templateSuffix, cancellationToken);
        
        var content = ApplyTemplateReplacements(
            template, 
            packageName, 
            publisherName, 
            version, 
            executable, 
            description);

        // Write manifest file
        var manifestPath = Path.Combine(outputDirectory, "appxmanifest.xml");
        await File.WriteAllTextAsync(manifestPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

        // Generate default assets
        await GenerateDefaultAssetsAsync(outputDirectory, verbose, cancellationToken);
    }
}
