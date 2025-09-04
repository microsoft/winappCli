using System.Reflection;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Winsdk.Cli.Services;

namespace Winsdk.Cli;

internal sealed class MsixService
{
    public async Task GenerateMsixAssetsAsync(bool isSparse, string outputDir, string packageName, string publisherName, string description, string version, string executable)
    {
        Directory.CreateDirectory(outputDir);
        var manifestName = "appxmanifest.xml";
        var templateSuffix = isSparse ? "sparse" : "packaged";
        var templateResName = FindResourceEnding($".Templates.appxmanifest.{templateSuffix}.xml")
                              ?? throw new FileNotFoundException("Embedded template not found");

        string template;
        var asm = Assembly.GetExecutingAssembly();
        await using (var s = asm.GetManifestResourceStream(templateResName) ?? throw new FileNotFoundException(templateResName))
        using (var sr = new StreamReader(s, Encoding.UTF8))
        {
            template = await sr.ReadToEndAsync();
        }

        var packageNameCamel = ToCamelCase(packageName);
        var content = template
            .Replace("{PackageName}", packageName)
            .Replace("{PackageNameCamelCase}", packageNameCamel)
            .Replace("{PublisherName}", publisherName)
            .Replace("{Description}", description)
            .Replace("Version=\"1.0.0.0\"", $"Version=\"{version}\"")
            .Replace("{Executable}", executable);

        var manifestPath = Path.Combine(outputDir, manifestName);
        await File.WriteAllTextAsync(manifestPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var assetsDir = Path.Combine(outputDir, "Assets");
        Directory.CreateDirectory(assetsDir);
        var resPrefix = ".Assets.msix_default_assets.";
        var assetNames = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Where(n => n.Contains(resPrefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var res in assetNames)
        {
            var fileName = res.Substring(res.LastIndexOf(resPrefix, StringComparison.OrdinalIgnoreCase) + resPrefix.Length);
            var target = Path.Combine(assetsDir, fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var s = asm.GetManifestResourceStream(res)!;
            await using var fs = File.Create(target);
            await s.CopyToAsync(fs);
        }
    }

    public async Task<MsixIdentityResult> AddMsixIdentityToExeAsync(string exePath, string appxManifestPath, string? tempDir = null, bool verbose = true)
    {
        // Validate inputs
        if (!File.Exists(exePath))
            throw new FileNotFoundException($"Executable not found at: {exePath}");

        if (!File.Exists(appxManifestPath))
            throw new FileNotFoundException($"AppX manifest not found at: {appxManifestPath}");

        var workingDir = tempDir ?? Path.GetDirectoryName(exePath)!;
        var tempManifestPath = Path.Combine(workingDir, "temp_extracted.manifest");
        var combinedManifestPath = Path.Combine(workingDir, "combined.manifest");

        if (verbose)
        {
            Console.WriteLine($"Processing executable: {exePath}");
            Console.WriteLine($"Using AppX manifest: {appxManifestPath}");
        }

        try
        {
            // Read and extract MSIX identity from appxmanifest.xml
            var appxManifestContent = await File.ReadAllTextAsync(appxManifestPath, Encoding.UTF8);

            // Extract Package Identity information
            var identityMatch = Regex.Match(appxManifestContent, @"<Identity[^>]*>", RegexOptions.IgnoreCase);
            if (!identityMatch.Success)
                throw new InvalidOperationException("No Identity element found in AppX manifest");

            var identityElement = identityMatch.Value;

            // Extract attributes from Identity element
            var nameMatch = Regex.Match(identityElement, @"Name\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);
            var publisherMatch = Regex.Match(identityElement, @"Publisher\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);

            if (!nameMatch.Success || !publisherMatch.Success)
                throw new InvalidOperationException("AppX manifest Identity element missing required Name or Publisher attributes");

            var packageName = nameMatch.Groups[1].Value;
            var publisher = publisherMatch.Groups[1].Value;

            // Extract Application ID from Applications/Application element
            var applicationMatch = Regex.Match(appxManifestContent, @"<Application[^>]*Id\s*=\s*[""']([^""']*)[""'][^>]*>", RegexOptions.IgnoreCase);
            if (!applicationMatch.Success)
                throw new InvalidOperationException("No Application element with Id attribute found in AppX manifest");

            var applicationId = applicationMatch.Groups[1].Value;

            // Create the MSIX element for the win32 manifest
            var msixElement = $@"<msix xmlns=""urn:schemas-microsoft-com:msix.v1""
            publisher=""{SecurityElement.Escape(publisher)}""
            packageName=""{SecurityElement.Escape(packageName)}""
            applicationId=""{SecurityElement.Escape(applicationId)}""
        />";

            if (verbose)
            {
                Console.WriteLine("Extracting current manifest from executable...");
            }

            // Extract current manifest from the executable
            bool hasExistingManifest = false;
            try
            {
                await RunMtToolAsync($@"-inputresource:""{exePath}"";#1 -out:""{tempManifestPath}""", verbose);
                hasExistingManifest = File.Exists(tempManifestPath);
            }
            catch
            {
                if (verbose)
                {
                    Console.WriteLine("No existing manifest found in executable, creating new one");
                }
            }

            string finalManifest;

            if (hasExistingManifest)
            {
                if (verbose)
                {
                    Console.WriteLine("Combining with existing manifest...");
                }

                // Read existing manifest
                var existingManifest = await File.ReadAllTextAsync(tempManifestPath, Encoding.UTF8);

                // Find the closing </assembly> tag in existing manifest
                var existingManifestParts = existingManifest.Split("</assembly>");

                if (existingManifestParts.Length >= 2)
                {
                    // Remove any existing msix section
                    var cleanedExistingContent = existingManifestParts[0];
                    cleanedExistingContent = Regex.Replace(cleanedExistingContent, @"<msix[\s\S]*?</msix>", "", RegexOptions.IgnoreCase);
                    cleanedExistingContent = Regex.Replace(cleanedExistingContent, @"<msix[\s\S]*?/>", "", RegexOptions.IgnoreCase);

                    // Combine: existing content + msix element + closing tag + rest
                    finalManifest = cleanedExistingContent + "\n  " + msixElement + "\n</assembly>" + string.Join("</assembly>", existingManifestParts.Skip(1));
                }
                else
                {
                    throw new InvalidOperationException("Invalid existing manifest structure");
                }

                // Clean up temporary file
                TryDeleteFile(tempManifestPath);
            }
            else
            {
                // Create a new basic manifest with MSIX identity
                finalManifest = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<assembly xmlns=""urn:schemas-microsoft-com:asm.v1"" manifestVersion=""1.0"">
  {msixElement}
  <assemblyIdentity version=""1.0.0.0"" name=""{SecurityElement.Escape(packageName)}"" type=""win32""/>
</assembly>";
            }

            // Write the combined manifest
            await File.WriteAllTextAsync(combinedManifestPath, finalManifest, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var command = $@"-manifest ""{combinedManifestPath}"" -outputresource:""{exePath}"";#1";
            if (verbose)
            {
                Console.WriteLine($"Final manifest content: {finalManifest}");
                Console.WriteLine("Re-embedding manifest into executable...");
                Console.WriteLine($"Command: mt.exe {command}");
            }

            // Re-embed the combined manifest into the executable
            await RunMtToolAsync(command, verbose);

            if (verbose)
            {
                Console.WriteLine("MSIX identity successfully embedded into executable");
            }

            // Clean up combined manifest file
            TryDeleteFile(combinedManifestPath);

            return new MsixIdentityResult(packageName, publisher, applicationId);
        }
        catch (Exception ex)
        {
            // Clean up any temporary files
            TryDeleteFile(tempManifestPath);
            TryDeleteFile(combinedManifestPath);

            throw new Exception($"Failed to add MSIX identity to executable: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Creates a PRI configuration file for the given package directory
    /// </summary>
    /// <param name="packageDir">Path to the package directory</param>
    /// <param name="language">Default language qualifier (default: 'en-US')</param>
    /// <param name="platformVersion">Platform version (default: '10.0.0')</param>
    /// <param name="verbose">Enable verbose logging</param>
    /// <returns>Path to the created configuration file</returns>
    public async Task<string> CreatePriConfigAsync(string packageDir, string language = "en-US", string platformVersion = "10.0.0", bool verbose = true)
    {
        // Remove trailing backslashes from packageDir
        packageDir = packageDir.TrimEnd('\\', '/');

        if (!Directory.Exists(packageDir))
            throw new DirectoryNotFoundException($"Package directory not found: {packageDir}");

        var configPath = Path.Combine(packageDir, "priconfig.xml");
        var arguments = $@"createconfig /cf ""{configPath}"" /dq {language} /pv {platformVersion} /o";

        if (verbose)
        {
            Console.WriteLine("Creating PRI configuration file...");
        }

        try
        {
            await BuildToolsService.RunBuildToolAsync("makepri.exe", arguments, verbose);

            if (verbose)
            {
                Console.WriteLine($"PRI configuration created: {configPath}");
            }

            return configPath;
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to create PRI configuration: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Generates a PRI file from the configuration
    /// </summary>
    /// <param name="packageDir">Path to the package directory</param>
    /// <param name="configPath">Path to PRI config file (default: packageDir/priconfig.xml)</param>
    /// <param name="outputPath">Output path for PRI file (default: packageDir/resources.pri)</param>
    /// <param name="verbose">Enable verbose logging</param>
    /// <returns>Path to the generated PRI file</returns>
    public async Task<string> GeneratePriFileAsync(string packageDir, string? configPath = null, string? outputPath = null, bool verbose = true)
    {
        // Remove trailing backslashes from packageDir
        packageDir = packageDir.TrimEnd('\\', '/');

        if (!Directory.Exists(packageDir))
            throw new DirectoryNotFoundException($"Package directory not found: {packageDir}");

        var priConfigPath = configPath ?? Path.Combine(packageDir, "priconfig.xml");
        var priOutputPath = outputPath ?? Path.Combine(packageDir, "resources.pri");

        if (!File.Exists(priConfigPath))
            throw new FileNotFoundException($"PRI configuration file not found: {priConfigPath}");

        var arguments = $@"new /pr ""{packageDir}"" /cf ""{priConfigPath}"" /of ""{priOutputPath}"" /o";

        if (verbose)
        {
            Console.WriteLine("Generating PRI file...");
        }

        try
        {
            await BuildToolsService.RunBuildToolAsync("makepri.exe", arguments, verbose);

            if (verbose)
            {
                Console.WriteLine($"PRI file generated: {priOutputPath}");
            }

            return priOutputPath;
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to generate PRI file: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Creates an MSIX package from a prepared package directory
    /// </summary>
    /// <param name="inputFolder">Path to the folder containing the package contents</param>
    /// <param name="outputFolder">Path to the folder where the MSIX will be created</param>
    /// <param name="packageName">Name for the output MSIX file (default: derived from manifest)</param>
    /// <param name="skipPri">Skip PRI generation</param>
    /// <param name="autoSign">Automatically sign the package</param>
    /// <param name="certificatePath">Path to signing certificate (required if autoSign is true)</param>
    /// <param name="certificatePassword">Certificate password</param>
    /// <param name="generateDevCert">Generate a new development certificate if none provided</param>
    /// <param name="installDevCert">Install certificate to machine</param>
    /// <param name="publisher">Publisher name for certificate generation (default: extracted from manifest)</param>
    /// <param name="verbose">Enable verbose logging</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing the MSIX path and signing status</returns>
    public async Task<CreateMsixPackageResult> CreateMsixPackageAsync(
        string inputFolder, 
        string outputFolder, 
        string? packageName = null, 
        bool skipPri = false, 
        bool autoSign = false, 
        string? certificatePath = null, 
        string certificatePassword = "password", 
        bool generateDevCert = false, 
        bool installDevCert = false, 
        string? publisher = null, 
        bool verbose = true,
        CancellationToken cancellationToken = default)
    {
        // Remove trailing backslashes from inputFolder
        inputFolder = inputFolder.TrimEnd('\\', '/');

        // Validate input folder and manifest
        if (!Directory.Exists(inputFolder))
            throw new DirectoryNotFoundException($"Input folder not found: {inputFolder}");

        var manifestPath = Path.Combine(inputFolder, "appxmanifest.xml");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"appxmanifest.xml not found in input folder: {inputFolder}");

        // Ensure output folder exists
        if (!Directory.Exists(outputFolder))
        {
            Directory.CreateDirectory(outputFolder);
        }

        // Determine package name and publisher
        var finalPackageName = packageName;
        var extractedPublisher = publisher;

        if (string.IsNullOrWhiteSpace(finalPackageName) || string.IsNullOrWhiteSpace(extractedPublisher))
        {
            try
            {
                var manifestContent = await File.ReadAllTextAsync(manifestPath, Encoding.UTF8, cancellationToken);

                if (string.IsNullOrWhiteSpace(finalPackageName))
                {
                    var nameMatch = Regex.Match(manifestContent, @"<Identity[^>]*Name\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);
                    finalPackageName = nameMatch.Success ? nameMatch.Groups[1].Value : "Package";
                }

                if (string.IsNullOrWhiteSpace(extractedPublisher))
                {
                    var publisherMatch = Regex.Match(manifestContent, @"<Identity[^>]*Publisher\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);
                    extractedPublisher = publisherMatch.Success ? publisherMatch.Groups[1].Value : null;
                }
            }
            catch
            {
                finalPackageName ??= "Package";
            }
        }

        var outputMsixPath = Path.Combine(outputFolder, $"{finalPackageName}.msix");

        if (verbose)
        {
            Console.WriteLine($"Creating MSIX package from: {inputFolder}");
            Console.WriteLine($"Output: {outputMsixPath}");
        }

        try
        {
            // Generate PRI files if not skipped
            if (!skipPri)
            {
                if (verbose)
                {
                    Console.WriteLine("Generating PRI configuration and files...");
                }

                await CreatePriConfigAsync(inputFolder, verbose: verbose);
                await GeneratePriFileAsync(inputFolder, verbose: verbose);
            }

            // Create MSIX package
            var makeappxArguments = $@"pack /o /d ""{inputFolder}"" /nv /p ""{outputMsixPath}""";

            if (verbose)
            {
                Console.WriteLine("Creating MSIX package...");
            }

            await BuildToolsService.RunBuildToolAsync("makeappx.exe", makeappxArguments, verbose);

            var certPath = certificatePath;
            CertificateServices.CertificateResult? certInfo = null;

            // Handle certificate generation and signing
            if (autoSign)
            {
                var certificateService = new CertificateServices();

                if (string.IsNullOrWhiteSpace(certPath) && generateDevCert)
                {
                    if (string.IsNullOrWhiteSpace(extractedPublisher))
                        throw new InvalidOperationException("Publisher name required for certificate generation. Provide publisher option or ensure it exists in manifest.");

                    if (verbose)
                    {
                        Console.WriteLine($"Generating certificate for publisher: {extractedPublisher}");
                    }

                    certPath = Path.Combine(outputFolder, $"{finalPackageName}_cert.pfx");
                    certInfo = await certificateService.GenerateDevCertificateAsync(extractedPublisher, certPath, certificatePassword, verbose: verbose);
                }

                if (string.IsNullOrWhiteSpace(certPath))
                    throw new InvalidOperationException("Certificate path required for signing. Provide certificatePath or set generateDevCert to true.");

                // Install certificate if requested
                if (installDevCert)
                {
                    var result = await certificateService.InstallCertificateAsync(certPath, certificatePassword, false, verbose);
                }

                // Sign the package
                await certificateService.SignMsixPackageAsync(outputMsixPath, certPath, certificatePassword, verbose: verbose);
            }

            // Clean up temporary PRI files
            if (!skipPri)
            {
                var tempFiles = new[]
                {
                    Path.Combine(inputFolder, "priconfig.xml"),
                    Path.Combine(inputFolder, "resources.pri")
                };

                foreach (var file in tempFiles)
                {
                    try
                    {
                        if (File.Exists(file))
                        {
                            File.Delete(file);
                        }
                    }
                    catch
                    {
                        if (verbose)
                        {
                            Console.WriteLine($"Warning: Could not clean up {file}");
                        }
                    }
                }
            }

            if (verbose)
            {
                Console.WriteLine($"MSIX package created successfully: {outputMsixPath}");
                if (autoSign)
                {
                    Console.WriteLine("Package has been signed");
                }
            }

            return new CreateMsixPackageResult(outputMsixPath, autoSign);
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to create MSIX package: {ex.Message}", ex);
        }
    }

    private static async Task RunMtToolAsync(string arguments, bool verbose)
    {
        // Use the new BuildToolsService to run mt.exe
        await BuildToolsService.RunBuildToolAsync("mt.exe", arguments, verbose);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Ignore cleanup failures
        }
    }

    private static string ToCamelCase(string name)
    {
        var parts = Regex.Split(name, @"[-_\s]+").Where(p => p.Length > 0).ToArray();
        if (parts.Length == 0) return name;
        var sb = new StringBuilder(parts[0].ToLowerInvariant());
        for (int i = 1; i < parts.Length; i++)
        {
            sb.Append(char.ToUpperInvariant(parts[i][0]));
            if (parts[i].Length > 1) sb.Append(parts[i].Substring(1).ToLowerInvariant());
        }
        return sb.ToString();
    }

    private static string? FindResourceEnding(string endsWith)
    {
        var asm = Assembly.GetExecutingAssembly();
        return asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(endsWith, StringComparison.OrdinalIgnoreCase));
    }
}
