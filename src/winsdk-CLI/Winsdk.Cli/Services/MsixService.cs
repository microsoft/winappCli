using System.Diagnostics;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Winsdk.Cli.Helpers;
using Winsdk.Cli.Models;

namespace Winsdk.Cli.Services;

internal class MsixService : IMsixService
{
    private readonly IWinsdkDirectoryService _winsdkDirectoryService;
    private readonly IConfigService _configService;
    private readonly IBuildToolsService _buildToolsService;
    private readonly IPowerShellService _powerShellService;
    private readonly ICertificateService _certificateService;
    private readonly IPackageCacheService _packageCacheService;
    private readonly IWorkspaceSetupService _workspaceSetupService;

    public MsixService(IWinsdkDirectoryService winsdkDirectoryService, IConfigService configService, IBuildToolsService buildToolsService, IPowerShellService powerShellService, ICertificateService certificateService, IPackageCacheService packageCacheService, IWorkspaceSetupService workspaceSetupService)
    {
        _winsdkDirectoryService = winsdkDirectoryService;
        _configService = configService;
        _buildToolsService = buildToolsService;
        _powerShellService = powerShellService;
        _certificateService = certificateService;
        _packageCacheService = packageCacheService;
        _workspaceSetupService = workspaceSetupService;
    }

    /// <summary>
    /// Sets up Windows App SDK for self-contained deployment by extracting MSIX content
    /// and preparing the necessary files for embedding in applications.
    /// </summary>
    public async Task SetupSelfContainedAsync(string winsdkDir, string architecture, bool verbose, CancellationToken cancellationToken = default)
    {
        // Look for the Runtime package which contains the MSIX files
        var selfContainedDir = Path.Combine(winsdkDir, "self-contained");
        Directory.CreateDirectory(selfContainedDir);

        var archSelfContainedDir = Path.Combine(selfContainedDir, architecture);
        Directory.CreateDirectory(archSelfContainedDir);

        string? msixDir = GetRuntimeMsixDir(verbose);
        if (msixDir == null)
        {
            throw new DirectoryNotFoundException("Windows App SDK Runtime MSIX directory not found. Ensure Windows App SDK is installed.");
        }

        // Look for the MSIX file in the tools/MSIX folder
        var msixToolsDir = Path.Combine(msixDir, $"win10-{architecture}");
        if (!Directory.Exists(msixToolsDir))
        {
            throw new DirectoryNotFoundException($"MSIX tools directory not found: {msixToolsDir}");
        }

        // Try to use inventory first for accurate file selection
        string? msixPath = null;
        try
        {
            var packageEntries = await WorkspaceSetupService.ParseMsixInventoryAsync(msixDir, verbose, cancellationToken);
            if (packageEntries != null)
            {
                // Look for the base Windows App Runtime package (not Framework, DDLM, or Singleton packages)
                var mainRuntimeEntry = packageEntries.FirstOrDefault(entry =>
                    entry.PackageIdentity.StartsWith("Microsoft.WindowsAppRuntime.") &&
                    !entry.PackageIdentity.Contains("Framework") &&
                    !entry.FileName.Contains("DDLM", StringComparison.OrdinalIgnoreCase) &&
                    !entry.FileName.Contains("Singleton", StringComparison.OrdinalIgnoreCase));

                if (mainRuntimeEntry != null)
                {
                    msixPath = Path.Combine(msixToolsDir, mainRuntimeEntry.FileName);
                    if (verbose)
                    {
                        Console.WriteLine($"  {UiSymbols.Package} Found main runtime package from inventory: {mainRuntimeEntry.FileName}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (verbose)
            {
                Console.WriteLine($"  {UiSymbols.Note} Could not parse inventory, falling back to file search: {ex.Message}");
            }
        }

        // Fallback: search for files directly with pattern matching
        if (msixPath == null || !File.Exists(msixPath))
        {
            var msixFiles = Directory.GetFiles(msixToolsDir, "Microsoft.WindowsAppRuntime.*.msix");
            if (msixFiles.Length == 0)
            {
                throw new FileNotFoundException($"No MSIX files found in {msixToolsDir}");
            }

            // Look for the base runtime package (format: Microsoft.WindowsAppRuntime.{version}.msix)
            // Exclude files with additional suffixes like DDLM, Singleton, Framework, etc.
            msixPath = msixFiles.FirstOrDefault(f =>
            {
                var fileName = Path.GetFileName(f);
                return !fileName.Contains("DDLM", StringComparison.OrdinalIgnoreCase) &&
                       !fileName.Contains("Singleton", StringComparison.OrdinalIgnoreCase) &&
                       !fileName.Contains("Framework", StringComparison.OrdinalIgnoreCase) &&
                       System.Text.RegularExpressions.Regex.IsMatch(fileName, @"^Microsoft\.WindowsAppRuntime\.\d+\.\d+.*\.msix$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }) ?? msixFiles[0];
        }

        if (verbose)
        {
            Console.WriteLine($"  {UiSymbols.Package} Extracting MSIX: {Path.GetFileName(msixPath)}");
        }

        // Extract MSIX content
        var extractedDir = Path.Combine(archSelfContainedDir, "extracted");
        if (Directory.Exists(extractedDir))
        {
            Directory.Delete(extractedDir, true);
        }
        Directory.CreateDirectory(extractedDir);

        using (var archive = ZipFile.OpenRead(msixPath))
        {
            archive.ExtractToDirectory(extractedDir);
        }

        // Copy relevant files to deployment directory
        var deploymentDir = Path.Combine(archSelfContainedDir, "deployment");
        Directory.CreateDirectory(deploymentDir);

        // Copy DLLs, WinMD files, and other runtime assets
        CopyRuntimeFiles(extractedDir, deploymentDir, verbose);

        if (verbose)
        {
            Console.WriteLine($"  {UiSymbols.Check} Self-contained files prepared in: {archSelfContainedDir}");
        }
    }

    /// <summary>
    /// Parses an AppX manifest file and extracts the package identity information
    /// </summary>
    /// <param name="appxManifestPath">Path to the appxmanifest.xml file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>MsixIdentityResult containing package name, publisher, and application ID</returns>
    /// <exception cref="FileNotFoundException">Thrown when the manifest file is not found</exception>
    /// <exception cref="InvalidOperationException">Thrown when the manifest is invalid or missing required elements</exception>
    public static async Task<MsixIdentityResult> ParseAppxManifestFromPathAsync(string appxManifestPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(appxManifestPath))
            throw new FileNotFoundException($"AppX manifest not found at: {appxManifestPath}");

        // Read and extract MSIX identity from appxmanifest.xml
        var appxManifestContent = await File.ReadAllTextAsync(appxManifestPath, Encoding.UTF8, cancellationToken);

        return ParseAppxManifestAsync(appxManifestContent);
    }

    /// <summary>
    /// Parses an AppX manifest content and extracts the package identity information
    /// </summary>
    /// <param name="appxManifestContent">The content of the appxmanifest.xml file</param>
    /// <returns>MsixIdentityResult containing package name, publisher, and application ID</returns>
    /// <exception cref="InvalidOperationException">Thrown when the manifest is invalid or missing required elements</exception>
    public static MsixIdentityResult ParseAppxManifestAsync(string appxManifestContent)
    {
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

        return new MsixIdentityResult(packageName, publisher, applicationId);
    }

    public async Task<MsixIdentityResult> AddMsixIdentityToExeAsync(string exePath, string appxManifestPath, bool noInstall, string? applicationLocation = null, bool verbose = true, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathRooted(appxManifestPath))
        {
            appxManifestPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), appxManifestPath));
        }

        // Validate inputs
        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException($"Executable not found at: {exePath}");
        }

        if (!File.Exists(appxManifestPath))
        {
            throw new FileNotFoundException($"AppX manifest not found at: {appxManifestPath}. You can generate one using 'winsdk manifest generate'.");
        }

        if (verbose)
        {
            Console.WriteLine($"Processing executable: {exePath}");
            Console.WriteLine($"Using AppX manifest: {appxManifestPath}");
        }

        // Generate sparse package structure
        var (debugManifestPath, debugIdentity) = await GenerateSparsePackageStructureAsync(
            appxManifestPath,
            exePath,
            applicationLocation,
            verbose,
            cancellationToken);

        // Update executable with debug identity
        await EmbedMsixIdentityToExeAsync(exePath, debugIdentity, applicationLocation, verbose, cancellationToken);

        if (noInstall)
        {
            if (verbose)
            {
                Console.WriteLine("Skipping package installation as per --no-install option.");
            }
        }
        else
        {
            // Register the debug appxmanifest
            var workingDirectory = applicationLocation ?? Path.GetDirectoryName(exePath) ?? Directory.GetCurrentDirectory();

            // Unregister any existing package first
            await UnregisterExistingPackageAsync(debugIdentity.PackageName, verbose, cancellationToken);

            // Register the new debug manifest with external location
            await RegisterSparsePackageAsync(debugManifestPath, workingDirectory, verbose, cancellationToken);
        }

        return new MsixIdentityResult(debugIdentity.PackageName, debugIdentity.Publisher, debugIdentity.ApplicationId);
    }

    private async Task EmbedMsixIdentityToExeAsync(string exePath, MsixIdentityResult identityInfo, string? applicationLocation, bool verbose, CancellationToken cancellationToken)
    {
        // Create the MSIX element for the win32 manifest

        var manifestContent = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<assembly xmlns=""urn:schemas-microsoft-com:asm.v1"" manifestVersion=""1.0"">
  <msix xmlns=""urn:schemas-microsoft-com:msix.v1""
            publisher=""{SecurityElement.Escape(identityInfo.Publisher)}""
            packageName=""{SecurityElement.Escape(identityInfo.PackageName)}""
            applicationId=""{SecurityElement.Escape(identityInfo.ApplicationId)}""
        />
  <assemblyIdentity version=""1.0.0.0"" name=""{SecurityElement.Escape(identityInfo.PackageName)}"" type=""win32""/>
</assembly>";

        // Create a temporary manifest file
        var workingDir = applicationLocation ?? Path.GetDirectoryName(exePath)!;
        var tempManifestPath = Path.Combine(workingDir, "msix_identity_temp.manifest");

        try
        {
            await File.WriteAllTextAsync(tempManifestPath, manifestContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

            // Use mt.exe to merge manifests
            await EmbedManifestFileToExeAsync(exePath, tempManifestPath, applicationLocation, verbose, cancellationToken);
        }
        finally
        {
            TryDeleteFile(tempManifestPath);
        }
    }

    /// <summary>
    /// Embeds a manifest file into the Win32 manifest of an executable using mt.exe for proper merging.
    /// </summary>
    /// <param name="exePath">Path to the executable to modify</param>
    /// <param name="manifestPath">Path to the manifest file to embed</param>
    /// <param name="applicationLocation">Working directory for temporary files (optional, defaults to exe directory)</param>
    /// <param name="verbose">Enable verbose logging</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async Task EmbedManifestFileToExeAsync(
        string exePath,
        string manifestPath,
        string? applicationLocation = null,
        bool verbose = false,
        CancellationToken cancellationToken = default)
    {
        // Validate inputs
        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException($"Executable not found at: {exePath}");
        }

        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"Manifest file not found at: {manifestPath}");
        }

        if (verbose)
        {
            Console.WriteLine($"Processing executable: {exePath}");
            Console.WriteLine($"Embedding manifest: {manifestPath}");
        }

        var workingDir = applicationLocation ?? Path.GetDirectoryName(exePath)!;
        var tempManifestPath = Path.Combine(workingDir, "temp_extracted.manifest");
        var mergedManifestPath = Path.Combine(workingDir, "merged.manifest");

        try
        {
            if (verbose)
            {
                Console.WriteLine("Extracting current manifest from executable...");
            }

            // Extract current manifest from the executable
            bool hasExistingManifest = false;
            try
            {
                await RunMtToolAsync($@"-inputresource:""{exePath}"";#1 -out:""{tempManifestPath}""", verbose, cancellationToken);
                hasExistingManifest = File.Exists(tempManifestPath);
            }
            catch
            {
                if (verbose)
                {
                    Console.WriteLine("No existing manifest found in executable");
                }
            }

            if (hasExistingManifest)
            {
                if (verbose)
                {
                    Console.WriteLine("Merging with existing manifest using mt.exe...");
                }

                // Use mt.exe to merge existing manifest with new manifest
                await RunMtToolAsync($@"-manifest ""{tempManifestPath}"" ""{manifestPath}"" -out:""{mergedManifestPath}""", verbose, cancellationToken);
            }
            else
            {
                if (verbose)
                {
                    Console.WriteLine("No existing manifest, using new manifest as-is");
                }

                // No existing manifest, use the new manifest directly
                File.Copy(manifestPath, mergedManifestPath);
            }

            if (verbose)
            {
                Console.WriteLine("Embedding merged manifest into executable...");
            }

            // Update the executable with merged manifest
            await RunMtToolAsync($@"-manifest ""{mergedManifestPath}"" -outputresource:""{exePath}"";#1", verbose, cancellationToken);

            if (verbose)
            {
                Console.WriteLine($"✅ Successfully embedded manifest into: {exePath}");
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to embed manifest into executable: {ex.Message}", ex);
        }
        finally
        {
            // Clean up temporary files
            TryDeleteFile(tempManifestPath);
            TryDeleteFile(mergedManifestPath);
        }
    }

    /// <summary>
    /// Creates a PRI configuration file for the given package directory
    /// </summary>
    /// <param name="packageDir">Path to the package directory</param>
    /// <param name="language">Default language qualifier (default: 'en-US')</param>
    /// <param name="platformVersion">Platform version (default: '10.0.0')</param>
    /// <param name="verbose">Enable verbose logging</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Path to the created configuration file</returns>
    public async Task<string> CreatePriConfigAsync(string packageDir, string language = "en-US", string platformVersion = "10.0.0", bool verbose = true, CancellationToken cancellationToken = default)
    {
        // Remove trailing backslashes from packageDir
        packageDir = packageDir.TrimEnd('\\', '/');

        if (!Directory.Exists(packageDir))
            throw new DirectoryNotFoundException($"Package directory not found: {packageDir}");

        var resfilesPath = Path.Combine(packageDir, "pri.resfiles");
        var priFiles = (new DirectoryInfo(packageDir).EnumerateFiles("*.pri").Select(di => di.FullName)).ToList();
        using (var writer = new StreamWriter(resfilesPath))
        {
            foreach (var priFile in priFiles)
            {
                writer.WriteLine(priFile);
            }
        }

        var configPath = Path.Combine(packageDir, "priconfig.xml");
        var arguments = $@"createconfig /cf ""{configPath}"" /dq {language} /pv {platformVersion} /o";

        if (verbose)
        {
            Console.WriteLine("Creating PRI configuration file...");
        }

        try
        {
            await _buildToolsService.RunBuildToolAsync("makepri.exe", arguments, verbose, cancellationToken: cancellationToken);

            if (verbose)
            {
                Console.WriteLine($"PRI configuration created: {configPath}");
            }

            var xmlDoc = new XmlDocument();
            xmlDoc.Load(configPath);
            var resourcesNode = xmlDoc.SelectSingleNode("/resources");
            if (resourcesNode != null)
            {
                var indexNode = resourcesNode.SelectSingleNode("index");
                if (indexNode?.Attributes?["startIndexAt"]?.Value != null)
                {
                    // set to relative path
                    indexNode!.Attributes!["startIndexAt"]!.Value = ".\\pri.resfiles";
                    xmlDoc.Save(configPath);
                }
            }

            return configPath;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to create PRI configuration: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Generates a PRI file from the configuration
    /// </summary>
    /// <param name="packageDir">Path to the package directory</param>
    /// <param name="configPath">Path to PRI config file (default: packageDir/priconfig.xml)</param>
    /// <param name="outputPath">Output path for PRI file (default: packageDir/resources.pri)</param>
    /// <param name="verbose">Enable verbose logging</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of resource files that were processed</returns>
    public async Task<List<string>> GeneratePriFileAsync(string packageDir, string? configPath = null, string? outputPath = null, bool verbose = true, CancellationToken cancellationToken = default)
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
            var (stdout, stderr) = await _buildToolsService.RunBuildToolAsync("makepri.exe", arguments, verbose, cancellationToken: cancellationToken);

            // Parse the output to extract resource files
            var resourceFiles = new List<string>();
            var lines = stdout.Replace("\0", "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                // Look for lines that match the pattern "Resource File: *"
                if (line.StartsWith("Resource File: ", StringComparison.OrdinalIgnoreCase))
                {
                    var fileName = line.Substring("Resource File: ".Length).Trim();
                    if (!string.IsNullOrEmpty(fileName))
                    {
                        resourceFiles.Add(Path.Combine(packageDir, fileName));
                    }
                }
            }

            if (verbose)
            {
                Console.WriteLine($"PRI file generated: {priOutputPath}");
                if (resourceFiles.Count > 0)
                {
                    Console.WriteLine($"Processed {resourceFiles.Count} resource files");
                }
            }

            return resourceFiles;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to generate PRI file: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Creates an MSIX package from a prepared package directory
    /// </summary>
    /// <param name="inputFolder">Path to the folder containing the package contents</param>
    /// <param name="outputPath">Path to the file or folder for the output MSIX</param>
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
        string? outputPath,
        string? packageName = null,
        bool skipPri = false,
        bool autoSign = false,
        string? certificatePath = null,
        string certificatePassword = "password",
        bool generateDevCert = false,
        bool installDevCert = false,
        string? publisher = null,
        string? manifestPath = null,
        bool selfContained = false,
        bool verbose = true,
        CancellationToken cancellationToken = default)
    {
        // Remove trailing backslashes from inputFolder
        inputFolder = inputFolder.TrimEnd('\\', '/');

        // Validate input folder and manifest
        if (!Directory.Exists(inputFolder))
            throw new DirectoryNotFoundException($"Input folder not found: {inputFolder}");

        // Determine manifest path based on priority:
        // 1. Use provided manifestPath parameter
        // 2. Check for appxmanifest.xml in input folder
        // 3. Check for appxmanifest.xml in current directory
        string resolvedManifestPath;
        if (!string.IsNullOrEmpty(manifestPath))
        {
            resolvedManifestPath = manifestPath;
            if (verbose)
            {
                Console.WriteLine($"📄 Using specified manifest: {resolvedManifestPath}");
            }
        }
        else
        {
            var inputFolderManifest = Path.Combine(inputFolder, "appxmanifest.xml");
            if (File.Exists(inputFolderManifest))
            {
                resolvedManifestPath = inputFolderManifest;
                if (verbose)
                {
                    Console.WriteLine($"📄 Using manifest from input folder: {inputFolderManifest}");
                }
            }
            else
            {
                var currentDirManifest = Path.Combine(Directory.GetCurrentDirectory(), "appxmanifest.xml");
                if (File.Exists(currentDirManifest))
                {
                    resolvedManifestPath = currentDirManifest;
                    if (verbose)
                    {
                        Console.WriteLine($"📄 Using manifest from current directory: {currentDirManifest}");
                    }
                }
                else
                {
                    throw new FileNotFoundException($"Manifest file not found. Searched in: input folder ({inputFolderManifest}), current directory ({currentDirManifest})");
                }
            }
        }

        if (!File.Exists(resolvedManifestPath))
        {
            throw new FileNotFoundException($"Manifest file not found: {resolvedManifestPath}");
        }

        // Determine package name and publisher
        var finalPackageName = packageName;
        var extractedPublisher = publisher;

        var manifestContent = await File.ReadAllTextAsync(resolvedManifestPath, Encoding.UTF8, cancellationToken);

        // Update manifest content to ensure it's either referencing Windows App SDK or is self-contained
        manifestContent = UpdateAppxManifestContent(manifestContent, null, null, null, sparse: false, selfContained: selfContained, verbose);
        var updatedManifestPath = Path.Combine(inputFolder, "appxmanifest.xml");
        await File.WriteAllTextAsync(updatedManifestPath, manifestContent, Encoding.UTF8, cancellationToken);

        if (string.IsNullOrWhiteSpace(finalPackageName) || string.IsNullOrWhiteSpace(extractedPublisher))
        {
            try
            {
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

        var executableMatch = Regex.Match(manifestContent, @"<Application[^>]*Executable\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);
        string executablePath = executableMatch.Success ? executableMatch.Groups[1].Value : string.Empty;

        if (!string.IsNullOrWhiteSpace(executablePath) && !Path.IsPathRooted(executablePath))
        {
            executablePath = Path.GetFullPath(Path.Combine(inputFolder, executablePath));
        }

        // Clean the resolved package name to ensure it meets MSIX schema requirements
        finalPackageName = ManifestService.CleanPackageName(finalPackageName);

        string outputMsixPath;
        string outputFolder;
        if (string.IsNullOrEmpty(outputPath))
        {
            outputFolder = Directory.GetCurrentDirectory();
            outputMsixPath = Path.Combine(outputFolder, $"{finalPackageName}.msix");
        }
        else
        {
            outputFolder = outputPath;
            if (Path.HasExtension(outputPath) && string.Equals(Path.GetExtension(outputPath), ".msix", StringComparison.OrdinalIgnoreCase))
            {
                outputFolder = Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory();
                outputMsixPath = outputPath;
            }
            else
            {
                outputMsixPath = Path.Combine(outputPath, $"{finalPackageName}.msix");
            }
        }

        if (!Path.IsPathRooted(outputFolder))
        {
            outputFolder = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), outputFolder));
        }

        if (!Path.IsPathRooted(outputMsixPath))
        {
            outputMsixPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), outputMsixPath));
        }

        // Ensure output folder exists
        if (!Directory.Exists(outputFolder))
        {
            Directory.CreateDirectory(outputFolder);
        }

        // If manifest is outside input folder, copy it and any related assets into input folder
        if (!string.IsNullOrEmpty(resolvedManifestPath) && !inputFolder.Equals(Path.GetDirectoryName(resolvedManifestPath), StringComparison.OrdinalIgnoreCase))
        {
            await CopyAllAssetsAsync(resolvedManifestPath, inputFolder, verbose, cancellationToken);
        }

        if (verbose)
        {
            Console.WriteLine($"Creating MSIX package from: {inputFolder}");
            Console.WriteLine($"Output: {outputMsixPath}");
        }

        List<string> tempFiles = [];
        try
        {
            // Generate PRI files if not skipped
            if (!skipPri)
            {
                if (verbose)
                {
                    Console.WriteLine("Generating PRI configuration and files...");
                }

                string priConfigFilePath = await CreatePriConfigAsync(inputFolder, verbose: verbose, cancellationToken: cancellationToken);
                tempFiles.Add(priConfigFilePath);
                var resourceFiles = await GeneratePriFileAsync(inputFolder, verbose: verbose, cancellationToken: cancellationToken);
                tempFiles.AddRange(resourceFiles);
                if (verbose && resourceFiles.Count > 0)
                {
                    Console.WriteLine($"Resource files included in PRI:");
                    foreach (var resourceFile in resourceFiles)
                    {
                        Console.WriteLine($"  - {resourceFile}");
                    }
                }
            }

            // Handle self-contained deployment if requested
            if (selfContained)
            {
                if (verbose)
                {
                    Console.WriteLine($"{UiSymbols.Package} Preparing self-contained Windows App SDK runtime...");
                }

                var winAppSDKDeploymentDir = await PrepareRuntimeForPackagingAsync(inputFolder, verbose, cancellationToken);

                // Add WindowsAppSDK.manifest to existing manifest
                var resolvedDeploymentDir = Path.Combine(winAppSDKDeploymentDir, "..", "extracted");
                var windowsAppSDKManifestPath = Path.Combine(resolvedDeploymentDir, "AppxManifest.xml");
                await EmbedWindowsAppSDKManifestToExeAsync(executablePath, winAppSDKDeploymentDir, inputFolder, windowsAppSDKManifestPath, verbose, cancellationToken);
            }

            await CreateMsixPackageFromFolderAsync(inputFolder, verbose, outputMsixPath, cancellationToken);

            // Handle certificate generation and signing
            if (autoSign)
            {
                await SignMsixPackageAsync(outputFolder, certificatePassword, generateDevCert, installDevCert, verbose, finalPackageName, extractedPublisher, outputMsixPath, certificatePath, resolvedManifestPath, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to create MSIX package: {ex.Message}", ex);
        }
        finally
        {
            // Clean up temporary PRI files
            if (!skipPri)
            {
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

    private async Task EmbedWindowsAppSDKManifestToExeAsync(string exePath, string winAppSDKDeploymentDir, string? applicationLocation, string windowsAppSDKAppXManifestPath, bool verbose, CancellationToken cancellationToken)
    {
        // Use applicationLocation for DLL content (where runtime files were copied by PrepareRuntimeForPackagingAsync)
        var workingDir = applicationLocation ?? Path.GetDirectoryName(exePath)!;

        if (verbose)
        {
            Console.WriteLine($"📄 Generating Windows App SDK manifest from: {windowsAppSDKAppXManifestPath}");
            Console.WriteLine($"📦 Using DLL content from: {winAppSDKDeploymentDir}");
        }

        var dllFiles = (new DirectoryInfo(winAppSDKDeploymentDir).EnumerateFiles("*.dll").Select(di => di.Name)).ToList();

        // Create a temporary manifest file
        var tempManifestPath = Path.Combine(workingDir, "WindowsAppSDK_temp.manifest");

        try
        {
            // Generate the manifest content
            await GenerateAppManifestFromAppxAsync(
                redirectDlls: false,
                inDllFiles: dllFiles,
                inAppxManifests: [windowsAppSDKAppXManifestPath],
                fragments: false,
                outAppManifestPath: tempManifestPath,
                verbose: verbose,
                cancellationToken: cancellationToken);

            (var cachedPackages, var mainVersion) = GetCachedPackages(verbose);
            if (cachedPackages == null || cachedPackages.Count == 0)
            {
                throw new InvalidOperationException("No cached Windows SDK packages found. Please install the Windows SDK or Windows App SDK.");
            }

            IEnumerable<string> appxFragments = GetComponents(cachedPackages);
            var architecture = WorkspaceSetupService.GetSystemArchitecture();
            dllFiles = [.. appxFragments.Select(fragment => Path.Combine(Path.GetDirectoryName(fragment)!, $"win-{architecture}\\native"))
                .Where(Directory.Exists)
                .SelectMany(dir => Directory.EnumerateFiles(dir, "*.dll"))];

            await GenerateAppManifestFromAppxAsync(
                redirectDlls: false,
                inDllFiles: dllFiles,
                inAppxManifests: appxFragments,
                fragments: true,
                outAppManifestPath: tempManifestPath,
                verbose: verbose,
                cancellationToken: cancellationToken);

            // Use mt.exe to merge manifests
            await EmbedManifestFileToExeAsync(exePath, tempManifestPath, applicationLocation, verbose, cancellationToken);
        }
        finally
        {
            TryDeleteFile(tempManifestPath);
        }
    }

    private IEnumerable<string> GetComponents(Dictionary<string, string> cachedPackages)
    {
        var winsdkDir = _winsdkDirectoryService.GetGlobalWinsdkDirectory();
        var packagesDir = Path.Combine(winsdkDir, "packages");
        if (!Directory.Exists(packagesDir))
        {
            throw new DirectoryNotFoundException($"Packages directory not found: {packagesDir}");
        }

        // Find the packages directory
        var appxFragments = cachedPackages
            .Select(cachedPackage => Path.Combine(packagesDir, $"{cachedPackage.Key}.{cachedPackage.Value}", "runtimes-framework", "package.appxfragment"))
            .Where(File.Exists);
        return appxFragments;
    }

    /// <summary>
    /// Generates a Win32 manifest from an AppX manifest, similar to the GenerateAppManifestFromAppx MSBuild task.
    /// </summary>
    /// <param name="redirectDlls">Whether to redirect DLLs to %MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY%</param>
    /// <param name="inDllFiles">List of DLL files to include</param>
    /// <param name="inAppxManifests">List of paths to the input AppX manifest files, or fragments</param>
    /// <param name="fragments">Whether the input manifests are fragments (false), or full manifests (true)</param>
    /// <param name="verbose">Enable verbose logging</param>
    /// <returns>Generated manifest content</returns>
    private async Task GenerateAppManifestFromAppxAsync(
        bool redirectDlls,
        IEnumerable<string> inDllFiles,
        IEnumerable<string> inAppxManifests,
        bool fragments,
        string outAppManifestPath,
        bool verbose,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();

        // Write manifest header
        sb.AppendLine("<?xml version='1.0' encoding='utf-8' standalone='yes'?>");
        sb.AppendLine("<assembly manifestVersion='1.0'");
        sb.AppendLine("    xmlns:asmv3='urn:schemas-microsoft-com:asm.v3'");
        sb.AppendLine("    xmlns:winrtv1='urn:schemas-microsoft-com:winrt.v1'");
        sb.AppendLine("    xmlns='urn:schemas-microsoft-com:asm.v1'>");

        var prefix = fragments ? "Fragment" : "Package";

        var dllFileFormat = redirectDlls ?
            @"    <asmv3:file name='{0}' loadFrom='%MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY%{0}'>" :
            @"    <asmv3:file name='{0}'>";

        var dllFiles = inDllFiles.ToList();

        foreach (var inAppxManifest in inAppxManifests)
        {
            XmlDocument doc = new();
            doc.Load(inAppxManifest);
            var nsmgr = new XmlNamespaceManager(doc.NameTable);
            nsmgr.AddNamespace("m", "http://schemas.microsoft.com/appx/manifest/foundation/windows10");
            // Add InProcessServer elements to the generated appxmanifest
            var xQuery = $"./m:{prefix}/m:Extensions/m:Extension/m:InProcessServer";
            XmlNodeList? inProcessServers = doc.SelectNodes(xQuery, nsmgr);
            if (inProcessServers != null)
            {
                foreach (XmlNode winRTFactory in inProcessServers)
                {
                    var dllFileNode = winRTFactory.SelectSingleNode("./m:Path", nsmgr);
                    if (dllFileNode == null) continue;
                    var dllFile = dllFileNode.InnerText;
                    var typesNames = winRTFactory.SelectNodes("./m:ActivatableClass", nsmgr)?.OfType<XmlNode>();
                    sb.AppendFormat(dllFileFormat, dllFile);
                    sb.AppendLine();
                    if (typesNames != null)
                    {
                        foreach (var typeNode in typesNames)
                        {
                            var attribs = typeNode.Attributes?.OfType<XmlAttribute>().ToArray();
                            var typeName = attribs
                                ?.OfType<XmlAttribute>()
                                ?.SingleOrDefault(x => x.Name == "ActivatableClassId")
                                ?.InnerText;
                            var xmlEntryFormat =
        @"        <winrtv1:activatableClass name='{0}' threadingModel='both'/>";
                            sb.AppendFormat(xmlEntryFormat, typeName);
                            sb.AppendLine();
                            dllFiles.RemoveAll(e => e.Equals(dllFile, StringComparison.OrdinalIgnoreCase));
                        }
                    }
                    sb.AppendLine(@"    </asmv3:file>");
                }
            }

            // Only if packages
            if (!fragments && redirectDlls)
            {
                foreach (var dllFile in dllFiles)
                {
                    sb.AppendFormat(dllFileFormat, dllFile);
                    sb.AppendLine(@"</asmv3:file>");
                }
            }
            // Add ProxyStub elements to the generated appxmanifest
            dllFiles = inDllFiles.ToList();

            xQuery = $"./m:{prefix}/m:Extensions/m:Extension/m:ProxyStub";
            var inProcessProxystubs = doc.SelectNodes(xQuery, nsmgr);
            if (inProcessProxystubs != null)
            {
                foreach (XmlNode proxystub in inProcessProxystubs)
                {
                    var classIDAdded = false;

                    var dllFileNode = proxystub.SelectSingleNode("./m:Path", nsmgr);
                    var dllFile = dllFileNode?.InnerText;
                    // exclude PushNotificationsLongRunningTask, which requires the Singleton (which is unavailable for self-contained apps)
                    // exclude Widgets entries unless/until they have been tested and verified by the Widgets team
                    if (dllFile == null || dllFile == "PushNotificationsLongRunningTask.ProxyStub.dll" || dllFile == "Microsoft.Windows.Widgets.dll")
                    {
                        continue;
                    }
                    var typesNamesForProxy = proxystub.SelectNodes("./m:Interface", nsmgr)?.OfType<XmlNode>();
                    sb.AppendFormat(dllFileFormat, dllFile);
                    sb.AppendLine();
                    if (typesNamesForProxy != null)
                    {
                        foreach (var typeNode in typesNamesForProxy)
                        {
                            if (!classIDAdded)
                            {
                                var classIdAttribute = proxystub.Attributes?.OfType<XmlAttribute>().ToArray();
                                var classID = classIdAttribute
                                    ?.OfType<XmlAttribute>()
                                    ?.SingleOrDefault(x => x.Name == "ClassId")
                                    ?.InnerText;

                                if (classID != null)
                                {
                                    var xmlEntryFormat = @"        <asmv3:comClass clsid='{{{0}}}'/>";
                                    sb.AppendFormat(xmlEntryFormat, classID);
                                    classIDAdded = true;
                                }
                            }
                            var attribs = typeNode.Attributes?.OfType<XmlAttribute>().ToArray();
                            var typeID = attribs
                                ?.OfType<XmlAttribute>()
                                ?.SingleOrDefault(x => x.Name == "InterfaceId")
                                ?.InnerText;
                            var typeNames = attribs
                                ?.OfType<XmlAttribute>()
                                ?.SingleOrDefault(x => x.Name == "Name")
                                ?.InnerText;
                            var xmlEntryFormatForStubs = @"        <asmv3:comInterfaceProxyStub name='{0}' iid='{{{1}}}'/>";
                            if (typeNames != null && typeID != null)
                            {
                                sb.AppendFormat(xmlEntryFormatForStubs, typeNames, typeID);
                                sb.AppendLine();
                                dllFiles.RemoveAll(e => e.Equals(dllFile, StringComparison.OrdinalIgnoreCase));
                            }
                        }
                    }
                    sb.AppendLine(@"    </asmv3:file>");
                }
            }
        }

        if (!fragments && redirectDlls)
        {
            foreach (var dllFile in dllFiles)
            {
                sb.AppendFormat(dllFileFormat, dllFile);
                sb.AppendLine(@"</asmv3:file>");
            }
        }
        
        sb.AppendLine(@"</assembly>");
        var manifestContent = sb.ToString();

        await File.WriteAllTextAsync(outAppManifestPath, manifestContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
    }

    private async Task SignMsixPackageAsync(string outputFolder, string certificatePassword, bool generateDevCert, bool installDevCert, bool verbose, string finalPackageName, string? extractedPublisher, string outputMsixPath, string? certPath, string resolvedManifestPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(certPath) && generateDevCert)
        {
            if (string.IsNullOrWhiteSpace(extractedPublisher))
                throw new InvalidOperationException("Publisher name required for certificate generation. Provide publisher option or ensure it exists in manifest.");

            if (verbose)
            {
                Console.WriteLine($"Generating certificate for publisher: {extractedPublisher}");
            }

            certPath = Path.Combine(outputFolder, $"{finalPackageName}_cert.pfx");
            await _certificateService.GenerateDevCertificateAsync(extractedPublisher, certPath, certificatePassword, verbose: verbose, cancellationToken: cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(certPath))
            throw new InvalidOperationException("Certificate path required for signing. Provide certificatePath or set generateDevCert to true.");

        // Validate that the certificate publisher matches the manifest publisher
        if (verbose)
        {
            Console.WriteLine("🔍 Validating certificate and manifest publishers match...");
        }

        try
        {
            await CertificateService.ValidatePublisherMatchAsync(certPath, certificatePassword, resolvedManifestPath, cancellationToken);
            
            if (verbose)
            {
                Console.WriteLine("✅ Certificate and manifest publishers match");
            }
        }
        catch (InvalidOperationException ex)
        {
            // Re-throw with the specific error message format requested
            throw new InvalidOperationException(ex.Message, ex);
        }

        // Install certificate if requested
        if (installDevCert)
        {
            var result = await _certificateService.InstallCertificateAsync(certPath, certificatePassword, false, verbose, cancellationToken);
        }

        // Sign the package
        await _certificateService.SignFileAsync(outputMsixPath, certPath, certificatePassword, verbose: verbose, cancellationToken: cancellationToken);
    }

    private async Task CreateMsixPackageFromFolderAsync(string inputFolder, bool verbose, string outputMsixPath, CancellationToken cancellationToken)
    {
        // Create MSIX package
        var makeappxArguments = $@"pack /o /d ""{inputFolder}"" /nv /p ""{outputMsixPath}""";

        if (verbose)
        {
            Console.WriteLine("Creating MSIX package...");
        }

        await _buildToolsService.RunBuildToolAsync("makeappx.exe", makeappxArguments, verbose, cancellationToken: cancellationToken);
    }

    private async Task RunMtToolAsync(string arguments, bool verbose, CancellationToken cancellationToken = default)
    {
        // Use BuildToolsService to run mt.exe
        await _buildToolsService.RunBuildToolAsync("mt.exe", arguments, verbose, cancellationToken: cancellationToken);
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

    /// <summary>
    /// Searches for appxmanifest.xml in the project by looking for .winsdk directory in parent directories
    /// </summary>
    /// <param name="startDirectory">The directory to start searching from. If null, uses current directory.</param>
    /// <returns>Path to the project's appxmanifest.xml file, or null if not found</returns>
    public static string? FindProjectManifest(string? startDirectory = null)
    {
        var currentDir = startDirectory ?? Directory.GetCurrentDirectory();
        var directory = new DirectoryInfo(currentDir);

        while (directory != null)
        {
            var manifestPath = Path.Combine(directory.FullName, "appxmanifest.xml");
            if (File.Exists(manifestPath))
            {
                return manifestPath;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// Generates a sparse package structure for debug purposes
    /// </summary>
    /// <param name="originalManifestPath">Path to the original appxmanifest.xml</param>
    /// <param name="executablePath">Path to the executable that the manifest should reference</param>
    /// <param name="baseDirectory">Base directory to create the debug structure in</param>
    /// <param name="verbose">Enable verbose logging</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple containing the debug manifest path and modified identity info</returns>
    public async Task<(string debugManifestPath, MsixIdentityResult debugIdentity)> GenerateSparsePackageStructureAsync(
        string originalManifestPath,
        string executablePath,
        string? baseDirectory = null,
        bool verbose = true,
        CancellationToken cancellationToken = default)
    {
        var workingDir = baseDirectory ?? Directory.GetCurrentDirectory();
        var winsdkDir = Path.Combine(workingDir, ".winsdk");
        var debugDir = Path.Combine(winsdkDir, "debug");

        if (verbose)
        {
            Console.WriteLine($"🔧 Creating sparse package structure in: {debugDir}");
        }

        // Step 1: Create debug directory, removing existing one if present
        if (Directory.Exists(debugDir))
        {
            if (verbose)
            {
                Console.WriteLine("🗑️  Removing existing debug directory...");
            }
            Directory.Delete(debugDir, recursive: true);
        }

        Directory.CreateDirectory(debugDir);
        if (verbose)
        {
            Console.WriteLine("📁 Created debug directory");
        }

        // Step 2: Parse original manifest to get identity and assets
        var originalManifestContent = await File.ReadAllTextAsync(originalManifestPath, Encoding.UTF8, cancellationToken);
        var originalIdentity = ParseAppxManifestAsync(originalManifestContent);

        // Step 3: Create debug identity with ".debug" suffix
        var debugIdentity = CreateDebugIdentity(originalIdentity);

        // Step 4: Modify manifest for sparse packaging and debug identity
        var debugManifestContent = UpdateAppxManifestContent(
            originalManifestContent,
            debugIdentity,
            executablePath,
            baseDirectory,
            sparse: true,
            selfContained: false,
            verbose);

        if (verbose)
        {
            Console.WriteLine("✏️  Modified manifest for sparse packaging and debug identity");
        }

        // Step 5: Write debug manifest
        var debugManifestPath = Path.Combine(debugDir, "appxmanifest.xml");
        await File.WriteAllTextAsync(debugManifestPath, debugManifestContent, Encoding.UTF8, cancellationToken);

        if (verbose)
        {
            Console.WriteLine($"📄 Created debug manifest: {debugManifestPath}");
        }

        // Step 6: Copy all assets
        await CopyAllAssetsAsync(originalManifestPath, debugDir, verbose, cancellationToken);

        return (debugManifestPath, debugIdentity);
    }

    /// <summary>
    /// Creates a debug version of the identity by appending ".debug" to package name and application ID
    /// </summary>
    private static MsixIdentityResult CreateDebugIdentity(MsixIdentityResult originalIdentity)
    {
        var debugPackageName = originalIdentity.PackageName.EndsWith(".debug")
            ? originalIdentity.PackageName
            : $"{originalIdentity.PackageName}.debug";

        var debugApplicationId = originalIdentity.ApplicationId.EndsWith(".debug")
            ? originalIdentity.ApplicationId
            : $"{originalIdentity.ApplicationId}.debug";

        return new MsixIdentityResult(debugPackageName, originalIdentity.Publisher, debugApplicationId);
    }

    /// <summary>
    /// Updates the manifest identity, application ID, and executable path for sparse packaging
    /// </summary>
    private string UpdateAppxManifestContent(
        string originalAppxManifestContent,
        MsixIdentityResult? identity,
        string? executablePath,
        string? baseDirectory,
        bool sparse,
        bool selfContained,
        bool verbose)
    {
        var modifiedContent = originalAppxManifestContent;

        if (identity != null)
        {
            // Replace package identity attributes
            modifiedContent = Regex.Replace(
            modifiedContent,
            @"(<Identity[^>]*Name\s*=\s*)[""']([^""']*)[""']",
            $@"$1""{identity.PackageName}""",
            RegexOptions.IgnoreCase);

            // Replace application ID
            modifiedContent = Regex.Replace(
                modifiedContent,
                @"(<Application[^>]*Id\s*=\s*)[""']([^""']*)[""']",
                $@"$1""{identity.ApplicationId}""",
                RegexOptions.IgnoreCase);
        }

        if (executablePath != null)
        {
            // Replace executable path with relative path from package root
            var workingDir = baseDirectory ?? Directory.GetCurrentDirectory();
            string relativeExecutablePath;

            try
            {
                // Calculate relative path from the working directory (package root) to the executable
                relativeExecutablePath = Path.GetRelativePath(workingDir, executablePath);

                // Ensure we use forward slashes for consistency in manifest
                relativeExecutablePath = relativeExecutablePath.Replace('\\', '/');
            }
            catch
            {
                // Fallback to just the filename if relative path calculation fails
                relativeExecutablePath = Path.GetFileName(executablePath);
            }

            modifiedContent = Regex.Replace(
                modifiedContent,
                @"(<Application[^>]*Executable\s*=\s*)[""']([^""']*)[""']",
                $@"$1""{relativeExecutablePath}""",
                RegexOptions.IgnoreCase);
        }

        // Only apply sparse packaging modifications if sparse is true
        if (sparse)
        {
            // Add required namespaces for sparse packaging
            if (!modifiedContent.Contains("xmlns:uap10"))
            {
                modifiedContent = Regex.Replace(
                    modifiedContent,
                    @"(<Package[^>]*)(>)",
                    @"$1 xmlns:uap10=""http://schemas.microsoft.com/appx/manifest/uap/windows10/10""$2",
                    RegexOptions.IgnoreCase);
            }

            if (!modifiedContent.Contains("xmlns:desktop6"))
            {
                modifiedContent = Regex.Replace(
                    modifiedContent,
                    @"(<Package[^>]*)(>)",
                    @"$1 xmlns:desktop6=""http://schemas.microsoft.com/appx/manifest/desktop/windows10/6""$2",
                    RegexOptions.IgnoreCase);
            }

            // Add sparse package properties
            if (!modifiedContent.Contains("<uap10:AllowExternalContent>"))
            {
                modifiedContent = Regex.Replace(
                    modifiedContent,
                    @"(\s*</Properties>)",
                    @"    <uap10:AllowExternalContent>true</uap10:AllowExternalContent>
    <desktop6:RegistryWriteVirtualization>disabled</desktop6:RegistryWriteVirtualization>
$1",
                    RegexOptions.IgnoreCase);
            }

            // Ensure Application has sparse packaging attributes
            if (!modifiedContent.Contains("uap10:TrustLevel"))
            {
                modifiedContent = Regex.Replace(
                    modifiedContent,
                    @"(<Application[^>]*)(>)",
                    @"$1 uap10:TrustLevel=""mediumIL"" uap10:RuntimeBehavior=""packagedClassicApp""$2",
                    RegexOptions.IgnoreCase);
            }

            // Remove EntryPoint if present (not needed for sparse packages)
            modifiedContent = Regex.Replace(
                modifiedContent,
                @"\s*EntryPoint\s*=\s*[""'][^""']*[""']",
                "",
                RegexOptions.IgnoreCase);

            // Add AppListEntry="none" to VisualElements if not present
            if (!modifiedContent.Contains("AppListEntry"))
            {
                modifiedContent = Regex.Replace(
                    modifiedContent,
                    @"(<uap:VisualElements[^>]*)(>)",
                    @"$1 AppListEntry=""none""$2",
                    RegexOptions.IgnoreCase);
            }

            // Add sparse-specific capabilities if not present
            if (!modifiedContent.Contains("unvirtualizedResources"))
            {
                modifiedContent = Regex.Replace(
                    modifiedContent,
                    @"(\s*<rescap:Capability Name=""runFullTrust"" />)",
                    @"$1
    <rescap:Capability Name=""unvirtualizedResources""/>
    <rescap:Capability Name=""allowElevation"" />",
                    RegexOptions.IgnoreCase);
            }
        }

        // Update or insert Windows App SDK dependency (skip for self-contained packages)
        if (!selfContained)
        {
            modifiedContent = UpdateWindowsAppSdkDependency(modifiedContent, verbose);
        }

        return modifiedContent;
    }

    /// <summary>
    /// Updates or inserts the Windows App SDK dependency in the manifest
    /// </summary>
    /// <param name="manifestContent">The manifest content to modify</param>
    /// <param name="verbose">Enable verbose logging</param>
    /// <returns>The modified manifest content</returns>
    private string UpdateWindowsAppSdkDependency(string manifestContent, bool verbose)
    {
        // Get the Windows App SDK version from the locked winsdk.yaml config
        var winAppSdkInfo = GetWindowsAppSdkDependencyInfo(verbose);

        if (winAppSdkInfo == null)
        {
            if (verbose)
            {
                Console.WriteLine("⚠️  Could not determine Windows App SDK version, skipping dependency update");
            }
            return manifestContent;
        }

        // Check if Dependencies section exists
        if (!manifestContent.Contains("<Dependencies>"))
        {
            // Add Dependencies section before Applications
            manifestContent = Regex.Replace(
                manifestContent,
                @"(\s*<Applications>)",
                $@"  <Dependencies>
    <PackageDependency Name=""{winAppSdkInfo.RuntimeName}"" MinVersion=""{winAppSdkInfo.MinVersion}"" Publisher=""CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"" />
  </Dependencies>
$1",
                RegexOptions.IgnoreCase);

            if (verbose)
            {
                Console.WriteLine($"📦 Added Windows App SDK dependency {winAppSdkInfo.RuntimeName} (v{winAppSdkInfo.MinVersion})");
            }
        }
        else
        {
            // Check if Windows App SDK dependency already exists
            var existingDependencyPattern = @"<PackageDependency[^>]*Name\s*=\s*[""']Microsoft\.WindowsAppRuntime\.[^""']*[""'][^>]*>";
            var existingMatch = Regex.Match(manifestContent, existingDependencyPattern, RegexOptions.IgnoreCase);

            if (existingMatch.Success)
            {
                // Update existing dependency
                var newDependency = $@"<PackageDependency Name=""{winAppSdkInfo.RuntimeName}"" MinVersion=""{winAppSdkInfo.MinVersion}"" Publisher=""CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"" />";
                manifestContent = Regex.Replace(
                    manifestContent,
                    existingDependencyPattern,
                    newDependency,
                    RegexOptions.IgnoreCase);

                if (verbose)
                {
                    Console.WriteLine($"🔄 Updated Windows App SDK dependency to {winAppSdkInfo.RuntimeName} v{winAppSdkInfo.MinVersion}");
                }
            }
            else
            {
                // Add new dependency to existing Dependencies section
                manifestContent = Regex.Replace(
                    manifestContent,
                    @"(\s*</Dependencies>)",
                    $@"    <PackageDependency Name=""{winAppSdkInfo.RuntimeName}"" MinVersion=""{winAppSdkInfo.MinVersion}"" Publisher=""CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"" />
$1",
                    RegexOptions.IgnoreCase);

                if (verbose)
                {
                    Console.WriteLine($"➕ Added Windows App SDK dependency {winAppSdkInfo.RuntimeName} to existing Dependencies section (v{winAppSdkInfo.MinVersion})");
                }
            }
        }

        return manifestContent;
    }

    /// <summary>
    /// Gets the Windows App SDK dependency information from the locked winsdk.yaml config and package cache
    /// </summary>
    /// <param name="verbose">Enable verbose logging</param>
    /// <returns>The dependency information, or null if not found</returns>
    private WindowsAppRuntimePackageInfo? GetWindowsAppSdkDependencyInfo(bool verbose)
    {
        try
        {
            string? msixDir = GetRuntimeMsixDir(verbose);
            if (msixDir == null)
            {
                return null;
            }

            // Get the runtime package information from the MSIX inventory
            var runtimeInfo = GetWindowsAppRuntimePackageInfo(msixDir, verbose);
            if (runtimeInfo == null)
            {
                if (verbose)
                {
                    Console.WriteLine("⚠️  Could not parse Windows App Runtime package information from MSIX inventory");
                }
                return null;
            }

            return runtimeInfo;
        }
        catch (Exception ex)
        {
            if (verbose)
            {
                Console.WriteLine($"⚠️  Error getting Windows App SDK dependency info: {ex.Message}");
            }
            return null;
        }
    }

    private string? GetRuntimeMsixDir(bool verbose)
    {
        (var cachedPackages, var mainVersion) = GetCachedPackages(verbose);
        if (cachedPackages == null || mainVersion == null)
        {
            return null;
        }
        
        // Look for the runtime package in the cached dependencies
        var runtimePackage = cachedPackages.FirstOrDefault(kvp =>
            kvp.Key.StartsWith("Microsoft.WindowsAppSDK.Runtime", StringComparison.OrdinalIgnoreCase));

        // Create a dictionary with versions for FindWindowsAppSdkMsixDirectory
        var usedVersions = new Dictionary<string, string>
        {
            ["Microsoft.WindowsAppSDK"] = mainVersion
        };

        if (runtimePackage.Key != null)
        {
            // For Windows App SDK 1.8+, there's a separate runtime package
            var runtimeVersion = runtimePackage.Value;
            usedVersions[runtimePackage.Key] = runtimeVersion;

            if (verbose)
            {
                Console.WriteLine($"📦 Found cached runtime package: {runtimePackage.Key} v{runtimeVersion}");
            }
        }
        else
        {
            // For Windows App SDK 1.7 and earlier, runtime is included in the main package
            if (verbose)
            {
                Console.WriteLine("📝 No separate runtime package found - using main package (Windows App SDK 1.7 or earlier)");
                Console.WriteLine($"📝 Available cached packages: {string.Join(", ", cachedPackages.Keys)}");
            }
        }

        // Find the MSIX directory with the runtime package
        var msixDir = _workspaceSetupService.FindWindowsAppSdkMsixDirectory(usedVersions);
        if (msixDir == null)
        {
            if (verbose)
            {
                Console.WriteLine("⚠️  Windows App SDK MSIX directory not found for cached runtime package");
            }
            return null;
        }

        return msixDir;
    }

    private (Dictionary<string, string>? CachedPackages, string? MainVersion) GetCachedPackages(bool verbose)
    {
        // Load the locked config to get the actual package versions
        if (!_configService.Exists())
        {
            if (verbose)
            {
                Console.WriteLine("⚠️  No winsdk.yaml found, cannot determine locked Windows App SDK version");
            }

            return (null, null);
        }

        var config = _configService.Load();

        // Get the main Windows App SDK version from config
        var mainVersion = config.GetVersion("Microsoft.WindowsAppSDK");
        if (string.IsNullOrEmpty(mainVersion))
        {
            if (verbose)
            {
                Console.WriteLine("⚠️  No Microsoft.WindowsAppSDK package found in winsdk.yaml");
            }
            return (null, null);
        }

        if (verbose)
        {
            Console.WriteLine($"📦 Found Windows App SDK main package: v{mainVersion}");
        }

        try
        {
            // Use PackageCacheService to find the runtime package that was installed with the main package
            return (_packageCacheService.GetCachedPackageAsync("Microsoft.WindowsAppSDK", mainVersion, CancellationToken.None).GetAwaiter().GetResult(), mainVersion);
        }
        catch (KeyNotFoundException)
        {
            if (verbose)
            {
                Console.WriteLine($"⚠️  Microsoft.WindowsAppSDK v{mainVersion} not found in package cache");
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Parses the MSIX inventory file to extract Windows App Runtime package information
    /// </summary>
    /// <param name="msixDir">The MSIX directory containing the inventory file</param>
    /// <param name="verbose">Enable verbose logging</param>
    /// <returns>Package information, or null if not found</returns>
    private static WindowsAppRuntimePackageInfo? GetWindowsAppRuntimePackageInfo(string msixDir, bool verbose = false)
    {
        try
        {
            // Use the shared inventory parsing logic (synchronous version)
            var packageEntries = WorkspaceSetupService.ParseMsixInventoryAsync(msixDir, verbose, CancellationToken.None).GetAwaiter().GetResult();

            if (packageEntries == null || packageEntries.Count == 0)
            {
                return null;
            }

            // Look for the Windows App Runtime main package (not Framework packages)
            var mainRuntimeEntry = packageEntries
                .FirstOrDefault(entry => entry.PackageIdentity.StartsWith("Microsoft.WindowsAppRuntime.") &&
                                       !entry.PackageIdentity.Contains("Framework"));

            if (mainRuntimeEntry != null)
            {
                // Parse the PackageIdentity (format: Name_Version_Architecture_PublisherId)
                var identityParts = mainRuntimeEntry.PackageIdentity.Split('_');
                if (identityParts.Length >= 2)
                {
                    var runtimeName = identityParts[0];
                    var version = identityParts[1];

                    if (verbose)
                    {
                        Console.WriteLine($"{UiSymbols.Package} Found Windows App Runtime: {runtimeName} v{version}");
                    }

                    return new WindowsAppRuntimePackageInfo
                    {
                        RuntimeName = runtimeName,
                        MinVersion = version
                    };
                }
            }

            if (verbose)
            {
                Console.WriteLine($"{UiSymbols.Note} No Windows App Runtime main package found in inventory");
                Console.WriteLine($"{UiSymbols.Note} Available packages: {string.Join(", ", packageEntries.Select(e => e.PackageIdentity))}");
            }

            return null;
        }
        catch (Exception ex)
        {
            if (verbose)
            {
                Console.WriteLine($"{UiSymbols.Note} Error parsing MSIX inventory: {ex.Message}");
            }
            return null;
        }
    }

    /// <summary>
    /// Copies files referenced in the manifest to the target directory
    /// </summary>
    private async Task CopyAllAssetsAsync(string manifestPath, string targetDir, bool verbose, CancellationToken cancellationToken)
    {
        var originalManifestDir = Path.GetDirectoryName(manifestPath)!;

        if (verbose)
        {
            Console.WriteLine($"📋 Copying manifest-referenced files from: {originalManifestDir}");
        }

        var filesCopied = await CopyManifestReferencedFilesAsync(manifestPath, targetDir, verbose);

        if (verbose)
        {
            Console.WriteLine($"✅ Copied {filesCopied} files to target directory");
        }
    }

    /// <summary>
    /// Copies files that are referenced in the manifest using regex pattern matching
    /// </summary>
    private static async Task<int> CopyManifestReferencedFilesAsync(string manifestPath, string targetDir, bool verbose)
    {
        var filesCopied = 0;
        var manifestDir = Path.GetDirectoryName(manifestPath)!;

        // Read the manifest content
        var manifestContent = await File.ReadAllTextAsync(manifestPath, Encoding.UTF8);

        if (verbose)
        {
            Console.WriteLine($"📋 Reading manifest: {manifestPath}");
        }

        var referencedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // First, extract general file references (not within AppExtensions)
        var generalFilePatterns = new[]
        {
            // Logo and image files (e.g., Logo="Assets\Logo.png")
            @"(?:Logo|BackgroundImage|SplashScreen|Square\d+x\d+Logo|Wide\d+x\d+Logo|LockScreenLogo|BadgeLogo|StoreLogo)\s*=\s*[""']([^""']*)[""']",
            // Logo elements (e.g., <Logo>Assets\StoreLogo.png</Logo>)
            @"<(?:Logo|BackgroundImage|SplashScreen|Square\d+x\d+Logo|Wide\d+x\d+Logo|LockScreenLogo|BadgeLogo|StoreLogo)>\s*([^<]*)\s*</(?:Logo|BackgroundImage|SplashScreen|Square\d+x\d+Logo|Wide\d+x\d+Logo|LockScreenLogo|BadgeLogo|StoreLogo)>",
            // General Source attributes
            @"Source\s*=\s*[""']([^""']*)[""']",
            // Icon attributes
            @"Icon\s*=\s*[""']([^""']*)[""']",
            // Content references (e.g., in File elements)
            @"<File[^>]*Name\s*=\s*[""']([^""']*)[""'][^>]*>",
            // Resource files
            @"ResourceFile\s*=\s*[""']([^""']*)[""']"
        };

        // Extract general file references
        foreach (var pattern in generalFilePatterns)
        {
            var matches = Regex.Matches(manifestContent, pattern, RegexOptions.IgnoreCase);
            foreach (Match match in matches)
            {
                if (match.Groups.Count > 1)
                {
                    var filePath = match.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(filePath))
                    {
                        filePath = filePath.Replace('\\', Path.DirectorySeparatorChar);
                        referencedFiles.Add(filePath);
                    }
                }
            }
        }

        // Handle AppExtension elements with potential PublicFolder
        var appExtensionPattern = @"<(\w+:)?AppExtension[^>]*>(.*?)</(\w+:)?AppExtension>";
        var appExtensionMatches = Regex.Matches(manifestContent, appExtensionPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match appExtMatch in appExtensionMatches)
        {
            var appExtensionElement = appExtMatch.Value; // Full AppExtension element
            var appExtensionContent = appExtMatch.Groups[2].Value; // Content inside AppExtension

            // Extract PublicFolder from the AppExtension element attributes
            var publicFolderMatch = Regex.Match(appExtensionElement, @"PublicFolder\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);
            var publicFolder = publicFolderMatch.Success ? publicFolderMatch.Groups[1].Value.Trim() : string.Empty;

            // Extract file references within this AppExtension
            var internalFilePatterns = new[]
            {
                @"<Registration>\s*([^<]*)\s*</Registration>",
                @"<([^>]+)>\s*([^<]*\.(?:json|xml|txt|config|ini|dll|exe|png|jpg|jpeg|gif|svg|ico|bmp))\s*</\1>",
                @"[""']([^""']*\.(?:json|xml|txt|config|ini|dll|exe|png|jpg|jpeg|gif|svg|ico|bmp))[""']"
            };

            foreach (var pattern in internalFilePatterns)
            {
                var matches = Regex.Matches(appExtensionContent, pattern, RegexOptions.IgnoreCase);
                foreach (Match match in matches)
                {
                    string? filePath = null;
                    if (pattern.Contains("Registration"))
                    {
                        filePath = match.Groups[1].Value.Trim();
                    }
                    else if (pattern.Contains(@"</\1>")) // Element pattern
                    {
                        filePath = match.Groups[2].Value.Trim();
                    }
                    else // Quoted file pattern
                    {
                        filePath = match.Groups[1].Value.Trim();
                    }

                    if (!string.IsNullOrEmpty(filePath))
                    {
                        // If PublicFolder is specified, prepend it to the file path
                        if (!string.IsNullOrEmpty(publicFolder))
                        {
                            filePath = Path.Combine(publicFolder, filePath).Replace('\\', Path.DirectorySeparatorChar);
                            if (verbose)
                            {
                                Console.WriteLine($"📁 Found file in PublicFolder '{publicFolder}': {filePath}");
                            }
                        }
                        else
                        {
                            filePath = filePath.Replace('\\', Path.DirectorySeparatorChar);
                        }
                        referencedFiles.Add(filePath);
                    }
                }
            }
        }

        // Copy each referenced file
        foreach (var relativeFilePath in referencedFiles)
        {
            var sourceFile = Path.Combine(manifestDir, relativeFilePath);
            var targetFile = Path.Combine(targetDir, relativeFilePath);

            if (File.Exists(sourceFile))
            {
                // Ensure target directory exists
                var targetFileDir = Path.GetDirectoryName(targetFile);
                if (!string.IsNullOrEmpty(targetFileDir))
                {
                    Directory.CreateDirectory(targetFileDir);
                }

                File.Copy(sourceFile, targetFile, overwrite: true);
                filesCopied++;

                if (verbose)
                {
                    Console.WriteLine($"📄 Copied: {relativeFilePath}");
                }
            }
            else if (verbose)
            {
                Console.WriteLine($"⚠️  Referenced file not found: {sourceFile}");
            }
        }

        return filesCopied;
    }

    /// <summary>
    /// Checks if a package with the given name exists and unregisters it if found
    /// </summary>
    /// <param name="packageName">The name of the package to check and unregister</param>
    /// <param name="verbose">Enable verbose logging</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if package was found and unregistered, false if no package was found</returns>
    public async Task<bool> UnregisterExistingPackageAsync(string packageName, bool verbose = true, CancellationToken cancellationToken = default)
    {
        if (verbose)
        {
            Console.WriteLine("🗑️  Checking for existing package...");
        }

        try
        {
            // First check if package exists
            var checkCommand = $"Get-AppxPackage -Name '{packageName}'";
            var (_, checkResult) = await _powerShellService.RunCommandAsync(checkCommand, verbose: false, cancellationToken: cancellationToken);

            if (!string.IsNullOrWhiteSpace(checkResult))
            {
                // Package exists, remove it
                if (verbose)
                {
                    Console.WriteLine($"📦 Found existing package '{packageName}', removing it...");
                }

                var unregisterCommand = $"Get-AppxPackage -Name '{packageName}' | Remove-AppxPackage";
                await _powerShellService.RunCommandAsync(unregisterCommand, verbose: verbose, cancellationToken: cancellationToken);

                if (verbose)
                {
                    Console.WriteLine("✅ Existing package unregistered successfully");
                }
                return true;
            }
            else
            {
                // No package found
                if (verbose)
                {
                    Console.WriteLine("ℹ️  No existing package found");
                }
                return false;
            }
        }
        catch (Exception ex)
        {
            // If check fails, package likely doesn't exist or we don't have permission
            if (verbose)
            {
                Console.WriteLine($"ℹ️  Could not check for existing package: {ex.Message}");
            }
            return false;
        }
    }

    /// <summary>
    /// Registers a sparse package with external location using Add-AppxPackage
    /// </summary>
    /// <param name="manifestPath">Path to the appxmanifest.xml file</param>
    /// <param name="externalLocation">External location path (typically the working directory)</param>
    /// <param name="verbose">Enable verbose logging</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task RegisterSparsePackageAsync(string manifestPath, string externalLocation, bool verbose = true, CancellationToken cancellationToken = default)
    {
        if (verbose)
        {
            Console.WriteLine("📋 Registering sparse package with external location...");
        }

        var registerCommand = $"Add-AppxPackage -Path '{manifestPath}' -ExternalLocation '{externalLocation}' -Register -ForceUpdateFromAnyVersion";

        try
        {
            var (exitCode, _) = await _powerShellService.RunCommandAsync(registerCommand, verbose: verbose, cancellationToken: cancellationToken);

            if (exitCode != 0)
            {
                throw new InvalidOperationException($"PowerShell command failed with exit code {exitCode}");
            }

            if (verbose)
            {
                Console.WriteLine("✅ Sparse package registered successfully");
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to register sparse package: {ex.Message}", ex);
        }
    }

    private void CopyRuntimeFiles(string extractedDir, string deploymentDir, bool verbose)
    {
        var patterns = new[] { "*.dll", "workloads*.json", "restartAgent.exe", "map.html", "*.mui", "*.png", "*.winmd", "*.xaml", "*.xbf", "*.pri" };

        foreach (var pattern in patterns)
        {
            var files = Directory.GetFiles(extractedDir, pattern, SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var relativePath = Path.GetRelativePath(extractedDir, file);
                var destPath = Path.Combine(deploymentDir, relativePath);

                // Create destination directory if needed
                var destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                File.Copy(file, destPath, overwrite: true);

                if (verbose)
                {
                    Console.WriteLine($"    {UiSymbols.Files} {relativePath}");
                }
            }
        }
    }

    /// <summary>
    /// Prepares Windows App SDK runtime files for packaging into an MSIX by extracting them to the input folder
    /// </summary>
    /// <param name="inputFolder">The folder where runtime files should be copied</param>
    /// <param name="verbose">Enable verbose logging</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The path to the self-contained deployment directory</returns>
    private async Task<string> PrepareRuntimeForPackagingAsync(string inputFolder, bool verbose, CancellationToken cancellationToken)
    {
        var arch = WorkspaceSetupService.GetSystemArchitecture();

        var workingDir = Directory.GetCurrentDirectory();
        var winsdkDir = Path.Combine(workingDir, ".winsdk");

        // Extract runtime files using the existing method
        await SetupSelfContainedAsync(winsdkDir, arch, verbose, cancellationToken);

        // Copy runtime files from .winsdk/self-contained to input folder
        var runtimeSourceDir = Path.Combine(winsdkDir, "self-contained", arch, "deployment");

        if (Directory.Exists(runtimeSourceDir))
        {
            // Copy files recursively to maintain directory structure
            foreach (var file in Directory.GetFiles(runtimeSourceDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(runtimeSourceDir, file);
                var destFile = Path.Combine(inputFolder, relativePath);

                // Create destination directory if needed
                var destDir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                File.Copy(file, destFile, overwrite: true);

                if (verbose)
                {
                    Console.WriteLine($"{UiSymbols.Folder} Bundled runtime: {relativePath}");
                }
            }

            if (verbose)
            {
                Console.WriteLine($"{UiSymbols.Check} Windows App SDK runtime bundled into package");
            }
        }
        else
        {
            throw new DirectoryNotFoundException($"Runtime files not found at {runtimeSourceDir}");
        }

        return runtimeSourceDir;
    }
}
