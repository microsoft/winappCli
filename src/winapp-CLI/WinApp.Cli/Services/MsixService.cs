// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal partial class MsixService(
    IWinappDirectoryService winappDirectoryService,
    IConfigService configService,
    IBuildToolsService buildToolsService,
    IPowerShellService powerShellService,
    ICertificateService certificateService,
    IPackageCacheService packageCacheService,
    IWorkspaceSetupService workspaceSetupService,
    ICurrentDirectoryProvider currentDirectoryProvider,
    ILogger<MsixService> logger) : IMsixService
{
    [GeneratedRegex(@"PublicFolder\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex PublicFolderRegex();
    [GeneratedRegex(@"^Microsoft\.WindowsAppRuntime\.\d+\.\d+.*\.msix$", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex WindowsAppRuntimeMsixRegex();
    [GeneratedRegex(@"<Identity[^>]*>", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex IdentityElementRegex();
    [GeneratedRegex(@"Name\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackageNameRegex();
    [GeneratedRegex(@"Publisher\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackagePublisherRegex();
    [GeneratedRegex(@"<Application[^>]*\sId\s*=\s*[""']([^""']*)[""'][^>]*>", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxApplicationIdRegex();
    [GeneratedRegex(@"<Identity[^>]*Name\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackageIdentityNameRegex();
    [GeneratedRegex(@"<Identity[^>]*Publisher\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackageIdentityPublisherRegex();
    [GeneratedRegex(@"<Application[^>]*Executable\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackageApplicationExecutableRegex();
    [GeneratedRegex(@"(<Identity[^>]*Name\s*=\s*)[""']([^""']*)[""']", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackageIdentityNameAssignmentRegex();
    [GeneratedRegex(@"(<Application[^>]*\sId\s*=\s*)[""']([^""']*)[""']", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxApplicationIdAssignmentRegex();
    [GeneratedRegex(@"(<Application[^>]*Executable\s*=\s*)[""']([^""']*)[""']", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackageApplicationExecutableAssignmentRegex();
    [GeneratedRegex(@"(<Application[^>]*\s*uap10:HostId\s*=\s*)[""']([^""']*)[""']", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackageApplicationHostIdAssignmentRegex();
    [GeneratedRegex(@"(<Application[^>]*\s*uap10:Parameters\s*=\s*)[""']([^""']*)[""']", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackageApplicationParametersAssignmentRegex();
    [GeneratedRegex(@"(<Package[^>]*)(>)", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackageElementOpenTagRegex();
    [GeneratedRegex(@"(<Package[^>]*)(>)", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackageOpenTagRegex();
    [GeneratedRegex(@"(\s*</Properties>)", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackagePropertiesCloseTagRegex();
    [GeneratedRegex(@"(<Application[^>]*)(>)", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxApplicationOpenTagRegex();
    [GeneratedRegex(@"\s*EntryPoint\s*=\s*[""'][^""']*[""']", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackageEntryPointRegex();
    [GeneratedRegex(@"(<uap:VisualElements[^>]*)(>)", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackageVisualElementsOpenTagRegex();
    [GeneratedRegex(@"(\s*<rescap:Capability Name=""runFullTrust"" />)", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackageRunFullTrustCapabilityRegex();
    [GeneratedRegex(@"(\s*<Applications>)", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackageApplicationsTagRegex();
    [GeneratedRegex(@"(\s*</Dependencies>)", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackageDependenciesCloseTagRegex();
    [GeneratedRegex(@"<assemblyIdentity[^>]*name\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AssemblyIdentityNameRegex();

    // Language (en, en-US, pt-BR, zh-Hans, etc.) – bare token
    [GeneratedRegex(@"^[a-zA-Z]{2,3}(-[A-Za-z0-9]{2,8})*$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex LanguageQualifierRegex();

    [GeneratedRegex(@"^scale-(\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    // scale-100, scale-200, etc.
    private static partial Regex ScaleQualifierRegex();

    // theme-dark, theme-light
    [GeneratedRegex(@"^theme-(light|dark)$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex ThemeQualifierRegex();

    // contrast-standard, contrast-high
    [GeneratedRegex(@"^contrast-(standard|high)$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex ContrastQualifierRegex();

    // dxfeaturelevel-9 / 10 / 11
    [GeneratedRegex(@"^dxfeaturelevel-(9|10|11)$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex DxFeatureLevelQualifierRegex();

    // device-family-desktop / xbox / team / iot / mobile
    [GeneratedRegex(@"^device-family-(desktop|mobile|team|xbox|iot)$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceFamilyQualifierRegex();

    // homeregion-US, homeregion-JP, ...
    [GeneratedRegex(@"^homeregion-[A-Za-z]{2}$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex HomeRegionQualifierRegex();

    // configuration-debug, configuration-retail, etc.
    [GeneratedRegex(@"^configuration-[A-Za-z0-9]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex ConfigurationQualifierRegex();

    // targetsize-16, targetsize-24, targetsize-256, ...
    [GeneratedRegex(@"^targetsize-(\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex TargetSizeQualifierRegex();

    // altform-unplated, altform-lightunplated, etc.
    [GeneratedRegex(@"^altform-[A-Za-z0-9]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex AltFormQualifierRegex();

    /// <summary>
    /// Sets up Windows App SDK for self-contained deployment by extracting MSIX content
    /// and preparing the necessary files for embedding in applications.
    /// </summary>
    public async Task SetupSelfContainedAsync(DirectoryInfo winappDir, string architecture, CancellationToken cancellationToken = default)
    {
        using var _ = logger.BeginScope("SetupSelfContained");

        // Look for the Runtime package which contains the MSIX files
        var selfContainedDir = winappDir.CreateSubdirectory("self-contained");
        var archSelfContainedDir = selfContainedDir.CreateSubdirectory(architecture);

        var msixDir = GetRuntimeMsixDir() ?? throw new DirectoryNotFoundException("Windows App SDK Runtime MSIX directory not found. Ensure Windows App SDK is installed.");

        // Look for the MSIX file in the tools/MSIX folder
        var msixToolsDir = new DirectoryInfo(Path.Combine(msixDir.FullName, $"win10-{architecture}"));
        if (!msixToolsDir.Exists)
        {
            throw new DirectoryNotFoundException($"MSIX tools directory not found: {msixToolsDir}");
        }

        // Try to use inventory first for accurate file selection
        FileInfo? msixPath = null;
        try
        {
            var packageEntries = await WorkspaceSetupService.ParseMsixInventoryAsync(logger, msixDir, cancellationToken);
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
                    msixPath = new FileInfo(Path.Combine(msixToolsDir.FullName, mainRuntimeEntry.FileName));
                    logger.LogDebug("{UISymbols} Found main runtime package from inventory: {MainRuntimeEntryFileName}", UiSymbols.Package, mainRuntimeEntry.FileName);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug("{UISymbols} Could not parse inventory, falling back to file search: {Message}", UiSymbols.Note, ex.Message);
        }

        // Fallback: search for files directly with pattern matching
        if (msixPath == null || !msixPath.Exists)
        {
            var msixFiles = msixToolsDir.GetFiles("Microsoft.WindowsAppRuntime.*.msix");
            if (msixFiles.Length == 0)
            {
                throw new FileNotFoundException($"No MSIX files found in {msixToolsDir}");
            }

            // Look for the base runtime package (format: Microsoft.WindowsAppRuntime.{version}.msix)
            // Exclude files with additional suffixes like DDLM, Singleton, Framework, etc.
            msixPath = msixFiles.FirstOrDefault(f =>
            {
                var fileName = f.Name;
                return !fileName.Contains("DDLM", StringComparison.OrdinalIgnoreCase) &&
                       !fileName.Contains("Singleton", StringComparison.OrdinalIgnoreCase) &&
                       !fileName.Contains("Framework", StringComparison.OrdinalIgnoreCase) &&
                       WindowsAppRuntimeMsixRegex().IsMatch(fileName);
            }) ?? msixFiles[0];
        }

        logger.LogDebug("{UISymbol} Extracting MSIX: {FileName}", UiSymbols.Package, msixPath.FullName);

        // Extract MSIX content
        var extractedDir = new DirectoryInfo(Path.Combine(archSelfContainedDir.FullName, "extracted"));
        if (extractedDir.Exists)
        {
            extractedDir.Delete(recursive: true);
        }
        extractedDir.Refresh();
        extractedDir.Create();

        using (var archive = await ZipFile.OpenReadAsync(msixPath.FullName, cancellationToken))
        {
            await archive.ExtractToDirectoryAsync(extractedDir.FullName, cancellationToken);
        }

        // Copy relevant files to deployment directory
        var deploymentDir = archSelfContainedDir.CreateSubdirectory("deployment");

        // Copy DLLs, WinMD files, and other runtime assets
        CopyRuntimeFiles(extractedDir, deploymentDir);

        logger.LogDebug("{UISymbol} Self-contained files prepared in: {Directory}", UiSymbols.Check, archSelfContainedDir);
    }

    /// <summary>
    /// Parses an AppX manifest file and extracts the package identity information
    /// </summary>
    /// <param name="appxManifestPath">Path to the appxmanifest.xml file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>MsixIdentityResult containing package name, publisher, and application ID</returns>
    /// <exception cref="FileNotFoundException">Thrown when the manifest file is not found</exception>
    /// <exception cref="InvalidOperationException">Thrown when the manifest is invalid or missing required elements</exception>
    public static async Task<MsixIdentityResult> ParseAppxManifestFromPathAsync(FileInfo appxManifestPath, CancellationToken cancellationToken = default)
    {
        if (!appxManifestPath.Exists)
        {
            throw new FileNotFoundException($"AppX manifest not found at: {appxManifestPath}");
        }

        // Read and extract MSIX identity from appxmanifest.xml
        var appxManifestContent = await File.ReadAllTextAsync(appxManifestPath.FullName, Encoding.UTF8, cancellationToken);

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
        var identityMatch = IdentityElementRegex().Match(appxManifestContent);
        if (!identityMatch.Success)
        {
            throw new InvalidOperationException("No Identity element found in AppX manifest");
        }

        var identityElement = identityMatch.Value;

        // Extract attributes from Identity element
        var nameMatch = AppxPackageNameRegex().Match(identityElement);
        var publisherMatch = AppxPackagePublisherRegex().Match(identityElement);

        if (!nameMatch.Success || !publisherMatch.Success)
        {
            throw new InvalidOperationException("AppX manifest Identity element missing required Name or Publisher attributes");
        }

        var packageName = nameMatch.Groups[1].Value;
        var publisher = publisherMatch.Groups[1].Value;

        // Extract Application ID from Applications/Application element
        var applicationMatch = AppxApplicationIdRegex().Match(appxManifestContent);
        if (!applicationMatch.Success)
        {
            throw new InvalidOperationException("No Application element with Id attribute found in AppX manifest");
        }

        var applicationId = applicationMatch.Groups[1].Value;

        return new MsixIdentityResult(packageName, publisher, applicationId);
    }

    public async Task<MsixIdentityResult> AddMsixIdentityAsync(string? entryPointPath, FileInfo appxManifestPath, bool noInstall, CancellationToken cancellationToken = default)
    {
        // Validate inputs
        if (!appxManifestPath.Exists)
        {
            throw new FileNotFoundException($"AppX manifest not found at: {appxManifestPath}. You can generate one using 'winapp manifest generate'.");
        }

        if (entryPointPath == null)
        {
            var manifestContent = await File.ReadAllTextAsync(appxManifestPath.FullName, Encoding.UTF8, cancellationToken);
            var executableMatch = AppxPackageApplicationExecutableRegex().Match(manifestContent);
            if (executableMatch.Success)
            {
                entryPointPath = executableMatch.Groups[1].Value;
            }
            else
            {
                var hostIdMatch = AppxPackageApplicationHostIdAssignmentRegex().Match(manifestContent);
                if (hostIdMatch.Success)
                {
                    // check HostParameter
                    var parametersMatch = AppxPackageApplicationParametersAssignmentRegex().Match(manifestContent);
                    if (parametersMatch.Success)
                    {
                        entryPointPath = parametersMatch.Groups[2].Value;
                        var prefix = @"$(package.effectivePath)\";
                        if (entryPointPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            var appxManifestDir = Path.GetDirectoryName(appxManifestPath.FullName);
                            var appxManifestLocation = string.IsNullOrEmpty(appxManifestDir) ? currentDirectoryProvider.GetCurrentDirectory() : appxManifestDir;
                            entryPointPath = Path.GetFullPath(Path.Combine(appxManifestLocation, entryPointPath[prefix.Length..]));
                        }
                    }
                }
            }
        }

        // Validate inputs
        if (!File.Exists(entryPointPath))
        {
            throw new FileNotFoundException($"EntryPoint/Executable not found at: {entryPointPath}");
        }

        logger.LogDebug("Processing entryPoint/executable: {EntryPointPath}", entryPointPath);
        logger.LogDebug("Using AppX manifest: {AppXManifestPath}", appxManifestPath);

        // Generate sparse package structure
        var (debugManifestPath, debugIdentity) = await GenerateSparsePackageStructureAsync(
            appxManifestPath,
            entryPointPath,
            cancellationToken);

        // Update executable with debug identity
        if (Path.HasExtension(entryPointPath) && string.Equals(Path.GetExtension(entryPointPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            var exePath = new FileInfo(entryPointPath);
            await EmbedMsixIdentityToExeAsync(exePath, debugIdentity, cancellationToken);
        }

        if (noInstall)
        {
            logger.LogDebug("Skipping package installation as per --no-install option.");
        }
        else
        {
            // Register the debug appxmanifest
            var entryPointDir = Path.GetDirectoryName(entryPointPath);
            var externalLocation = new DirectoryInfo(string.IsNullOrEmpty(entryPointDir) ? currentDirectoryProvider.GetCurrentDirectory() : entryPointDir);

            // Unregister any existing package first
            await UnregisterExistingPackageAsync(debugIdentity.PackageName, cancellationToken);

            // Register the new debug manifest with external location
            await RegisterSparsePackageAsync(debugManifestPath, externalLocation, cancellationToken);
        }

        return new MsixIdentityResult(debugIdentity.PackageName, debugIdentity.Publisher, debugIdentity.ApplicationId);
    }

    private async Task EmbedMsixIdentityToExeAsync(FileInfo exePath, MsixIdentityResult identityInfo, CancellationToken cancellationToken)
    {
        // Create the MSIX element for the win32 manifest
        string assemblyIdentity = $@"<assemblyIdentity version=""1.0.0.0"" name=""{SecurityElement.Escape(identityInfo.PackageName)}"" type=""win32""/>;";
        var existingManifestPath = new FileInfo(Path.Combine(exePath.DirectoryName!, "temp_extracted.manifest"));

        try
        {
            bool hasExistingManifest = await TryExtractManifestFromExeAsync(exePath, existingManifestPath, cancellationToken);
            if (!hasExistingManifest)
            {
                assemblyIdentity = string.Empty;
            }
            else
            {
                logger.LogDebug("Existing manifest found in executable, checking for AssemblyIdentity...");
                var existingManifestContent = await File.ReadAllTextAsync(existingManifestPath.FullName, Encoding.UTF8, cancellationToken);
                var assemblyIdentityMatch = AssemblyIdentityNameRegex().Match(existingManifestContent);
                if (assemblyIdentityMatch.Success)
                {
                    logger.LogDebug("Existing AssemblyIdentity found in manifest, will not add a new one.");
                    assemblyIdentity = string.Empty;
                }
            }
        }
        finally
        {
            TryDeleteFile(existingManifestPath);
        }

        var manifestContent = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<assembly xmlns=""urn:schemas-microsoft-com:asm.v1"" manifestVersion=""1.0"">
  <msix xmlns=""urn:schemas-microsoft-com:msix.v1""
            publisher=""{SecurityElement.Escape(identityInfo.Publisher)}""
            packageName=""{SecurityElement.Escape(identityInfo.PackageName)}""
            applicationId=""{SecurityElement.Escape(identityInfo.ApplicationId)}""
        />
    {assemblyIdentity}
</assembly>";

        // Create a temporary manifest file
        var tempManifestPath = new FileInfo(Path.Combine(exePath.DirectoryName!, "msix_identity_temp.manifest"));

        try
        {
            await File.WriteAllTextAsync(tempManifestPath.FullName, manifestContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

            // Use mt.exe to merge manifests
            await EmbedManifestFileToExeAsync(exePath, tempManifestPath, cancellationToken);
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
    /// <param name="cancellationToken">Cancellation token</param>
    private async Task EmbedManifestFileToExeAsync(
        FileInfo exePath,
        FileInfo manifestPath,
        CancellationToken cancellationToken = default)
    {
        // Validate inputs
        if (!exePath.Exists)
        {
            throw new FileNotFoundException($"Executable not found at: {exePath}");
        }

        if (!manifestPath.Exists)
        {
            throw new FileNotFoundException($"Manifest file not found at: {manifestPath}");
        }

        logger.LogDebug("Processing executable: {ExecutablePath}", exePath);
        logger.LogDebug("Embedding manifest: {ManifestPath}", manifestPath);

        var exeDir = exePath.DirectoryName!;
        var tempManifestPath = new FileInfo(Path.Combine(exeDir, "temp_extracted.manifest"));
        var mergedManifestPath = new FileInfo(Path.Combine(exeDir, "merged.manifest"));

        try
        {
            bool hasExistingManifest = await TryExtractManifestFromExeAsync(exePath, tempManifestPath, cancellationToken);

            if (hasExistingManifest)
            {
                logger.LogDebug("Merging with existing manifest using mt.exe...");

                // Use mt.exe to merge existing manifest with new manifest
                await RunMtToolAsync($@"-manifest ""{tempManifestPath}"" ""{manifestPath}"" -out:""{mergedManifestPath}""", cancellationToken);
            }
            else
            {
                logger.LogDebug("No existing manifest, using new manifest as-is");

                // No existing manifest, use the new manifest directly
                manifestPath.CopyTo(mergedManifestPath.FullName);
            }

            logger.LogDebug("Embedding merged manifest into executable...");

            // Update the executable with merged manifest
            await RunMtToolAsync($@"-manifest ""{mergedManifestPath}"" -outputresource:""{exePath}"";#1", cancellationToken);

            logger.LogDebug("{UISymbol} Successfully embedded manifest into: {ExecutablePath}", UiSymbols.Check, exePath);
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

    private async Task<bool> TryExtractManifestFromExeAsync(FileInfo exePath, FileInfo tempManifestPath, CancellationToken cancellationToken)
    {
        logger.LogDebug("Extracting current manifest from executable...");

        // Extract current manifest from the executable
        bool hasExistingManifest = false;
        try
        {
            await RunMtToolAsync($@"-inputresource:""{exePath}"";#1 -out:""{tempManifestPath}""", cancellationToken);
            tempManifestPath.Refresh();
            hasExistingManifest = tempManifestPath.Exists;
        }
        catch
        {
            logger.LogDebug("No existing manifest found in executable");
        }

        return hasExistingManifest;
    }

    /// <summary>
    /// Creates a PRI configuration file for the given package directory
    /// </summary>
    /// <param name="packageDir">Path to the package directory</param>
    /// <param name="language">Default language qualifier (default: 'en-US')</param>
    /// <param name="platformVersion">Platform version (default: '10.0.0')</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Path to the created configuration file</returns>
    public async Task<FileInfo> CreatePriConfigAsync(DirectoryInfo packageDir, string language = "en-US", string platformVersion = "10.0.0", CancellationToken cancellationToken = default)
    {
        if (!packageDir.Exists)
        {
            throw new DirectoryNotFoundException($"Package directory not found: {packageDir}");
        }

        var resfilesPath = Path.Combine(packageDir.FullName, "pri.resfiles");
        var priFiles = (packageDir.EnumerateFiles("*.pri").Select(di => di.FullName)).ToList();
        using (var writer = new StreamWriter(resfilesPath))
        {
            foreach (var priFile in priFiles)
            {
                await writer.WriteLineAsync(priFile);
            }
        }

        var configPath = new FileInfo(Path.Combine(packageDir.FullName, "priconfig.xml"));
        var arguments = $@"createconfig /cf ""{configPath}"" /dq {language} /pv {platformVersion} /o";

        logger.LogDebug("Creating PRI configuration file...");

        try
        {
            await buildToolsService.RunBuildToolAsync("makepri.exe", arguments, cancellationToken: cancellationToken);

            logger.LogDebug("PRI configuration created: {ConfigPath}", configPath);

            var xmlDoc = new XmlDocument();
            xmlDoc.Load(configPath.FullName);
            var resourcesNode = xmlDoc.SelectSingleNode("/resources");
            if (resourcesNode != null)
            {
                var indexNode = resourcesNode.SelectSingleNode("index");
                if (indexNode?.Attributes?["startIndexAt"]?.Value != null)
                {
                    // set to relative path
                    indexNode!.Attributes!["startIndexAt"]!.Value = ".\\pri.resfiles";
                    xmlDoc.Save(configPath.FullName);
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
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of resource files that were processed</returns>
    public async Task<List<FileInfo>> GeneratePriFileAsync(DirectoryInfo packageDir, FileInfo? configPath = null, FileInfo? outputPath = null, CancellationToken cancellationToken = default)
    {
        if (!packageDir.Exists)
        {
            throw new DirectoryNotFoundException($"Package directory not found: {packageDir}");
        }

        var priConfigPath = configPath ?? new FileInfo(Path.Combine(packageDir.FullName, "priconfig.xml"));
        var priOutputPath = outputPath ?? new FileInfo(Path.Combine(packageDir.FullName, "resources.pri"));

        if (!priConfigPath.Exists)
        {
            throw new FileNotFoundException($"PRI configuration file not found: {priConfigPath}");
        }

        var arguments = $@"new /pr ""{Path.TrimEndingDirectorySeparator(packageDir.FullName)}"" /cf ""{priConfigPath.FullName}"" /of ""{priOutputPath.FullName}"" /o";

        logger.LogDebug("Generating PRI file...");

        try
        {
            var (stdout, stderr) = await buildToolsService.RunBuildToolAsync("makepri.exe", arguments, cancellationToken: cancellationToken);

            // Parse the output to extract resource files
            var resourceFiles = new List<FileInfo>();
            var lines = stdout.Replace("\0", "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                // Look for lines that match the pattern "Resource File: *"
                const string resourceFileStr = "Resource File: ";
                if (line.StartsWith(resourceFileStr, StringComparison.OrdinalIgnoreCase))
                {
                    var fileName = line[resourceFileStr.Length..].Trim();
                    if (!string.IsNullOrEmpty(fileName))
                    {
                        resourceFiles.Add(new FileInfo(Path.Combine(packageDir.FullName, fileName)));
                    }
                }
            }

            logger.LogDebug("PRI file generated: {PriOutputPath}", priOutputPath);
            if (resourceFiles.Count > 0)
            {
                logger.LogDebug("Processed {ResourceFileCount} resource files", resourceFiles.Count);
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
    /// <param name="manifestPath">Path to the manifest file (optional)</param>
    /// <param name="selfContained">Enable self-contained deployment</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing the MSIX path and signing status</returns>
    public async Task<CreateMsixPackageResult> CreateMsixPackageAsync(
        DirectoryInfo inputFolder,
        FileSystemInfo? outputPath,
        string? packageName = null,
        bool skipPri = false,
        bool autoSign = false,
        FileInfo? certificatePath = null,
        string certificatePassword = "password",
        bool generateDevCert = false,
        bool installDevCert = false,
        string? publisher = null,
        FileInfo? manifestPath = null,
        bool selfContained = false,
        CancellationToken cancellationToken = default)
    {
        // Validate input folder and manifest
        if (!inputFolder.Exists)
        {
            throw new DirectoryNotFoundException($"Input folder not found: {inputFolder}");
        }

        // Determine manifest path based on priority:
        // 1. Use provided manifestPath parameter
        // 2. Check for appxmanifest.xml in input folder
        // 3. Check for appxmanifest.xml in current directory
        FileInfo resolvedManifestPath;
        if (manifestPath != null)
        {
            resolvedManifestPath = manifestPath;
            logger.LogDebug("{UISymbol} Using specified manifest: {ResolvedManifestPath}", UiSymbols.Note, resolvedManifestPath);
        }
        else
        {
            var inputFolderManifest = new FileInfo(Path.Combine(inputFolder.FullName, "appxmanifest.xml"));
            if (inputFolderManifest.Exists)
            {
                resolvedManifestPath = inputFolderManifest;
                logger.LogDebug("{UISymbol} Using manifest from input folder: {InputFolderManifest}", UiSymbols.Note, inputFolderManifest);
            }
            else
            {
                var currentDirManifest = new FileInfo(Path.Combine(currentDirectoryProvider.GetCurrentDirectory(), "appxmanifest.xml"));
                if (currentDirManifest.Exists)
                {
                    resolvedManifestPath = currentDirManifest;
                    logger.LogDebug("{UISymbol} Using manifest from current directory: {CurrentDirManifest}", UiSymbols.Note, currentDirManifest);
                }
                else
                {
                    throw new FileNotFoundException($"Manifest file not found. Searched in: input folder ({inputFolderManifest}), current directory ({currentDirManifest})");
                }
            }
        }

        if (!resolvedManifestPath.Exists)
        {
            throw new FileNotFoundException($"Manifest file not found: {resolvedManifestPath}");
        }

        // Determine package name and publisher
        var finalPackageName = packageName;
        var extractedPublisher = publisher;

        var manifestContent = await File.ReadAllTextAsync(resolvedManifestPath.FullName, Encoding.UTF8, cancellationToken);

        // Update manifest content to ensure it's either referencing Windows App SDK or is self-contained
        manifestContent = UpdateAppxManifestContent(manifestContent, null, null, sparse: false, selfContained: selfContained);
        var updatedManifestPath = Path.Combine(inputFolder.FullName, "appxmanifest.xml");
        await File.WriteAllTextAsync(updatedManifestPath, manifestContent, Encoding.UTF8, cancellationToken);

        if (string.IsNullOrWhiteSpace(finalPackageName) || string.IsNullOrWhiteSpace(extractedPublisher))
        {
            try
            {
                if (string.IsNullOrWhiteSpace(finalPackageName))
                {
                    var nameMatch = AppxPackageIdentityNameRegex().Match(manifestContent);
                    finalPackageName = nameMatch.Success ? nameMatch.Groups[1].Value : "Package";
                }

                if (string.IsNullOrWhiteSpace(extractedPublisher))
                {
                    var publisherMatch = AppxPackageIdentityPublisherRegex().Match(manifestContent);
                    extractedPublisher = publisherMatch.Success ? publisherMatch.Groups[1].Value : null;
                }
            }
            catch
            {
                finalPackageName ??= "Package";
            }
        }

        var executableMatch = AppxPackageApplicationExecutableRegex().Match(manifestContent);
        FileInfo? executablePath = executableMatch.Success ? new FileInfo(Path.Combine(inputFolder.FullName, executableMatch.Groups[1].Value)) : null;

        // Clean the resolved package name to ensure it meets MSIX schema requirements
        finalPackageName = ManifestService.CleanPackageName(finalPackageName);

        FileInfo outputMsixPath;
        DirectoryInfo outputFolder;
        if (outputPath == null)
        {
            outputFolder = currentDirectoryProvider.GetCurrentDirectoryInfo();
            outputMsixPath = new FileInfo(Path.Combine(outputFolder.FullName, $"{finalPackageName}.msix"));
        }
        else
        {
            if (Path.HasExtension(outputPath.Name) && string.Equals(Path.GetExtension(outputPath.Name), ".msix", StringComparison.OrdinalIgnoreCase))
            {
                outputMsixPath = new FileInfo(outputPath.FullName);
                outputFolder = outputMsixPath.Directory!;
            }
            else
            {
                outputFolder = new DirectoryInfo(outputPath.FullName);
                outputMsixPath = new FileInfo(Path.Combine(outputPath.FullName, $"{finalPackageName}.msix"));
            }
        }

        // Ensure output folder exists
        if (!outputFolder.Exists)
        {
            outputFolder.Create();
        }

        // If manifest is outside input folder, copy it and any related assets into input folder
        if (!inputFolder.FullName.TrimEnd(Path.DirectorySeparatorChar)
            .Equals(resolvedManifestPath.Directory!.FullName.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            await CopyAllAssetsAsync(resolvedManifestPath, inputFolder, cancellationToken);
        }

        logger.LogDebug("Creating MSIX package from: {InputFolder}", inputFolder.FullName);
        logger.LogDebug("Output: {OutputMsixPath}", outputMsixPath.FullName);

        List<FileInfo> tempFiles = [];
        try
        {
            // Generate PRI files if not skipped
            if (!skipPri)
            {
                logger.LogDebug("Generating PRI configuration and files...");

                FileInfo priConfigFilePath = await CreatePriConfigAsync(inputFolder, cancellationToken: cancellationToken);
                tempFiles.Add(priConfigFilePath);
                var resourceFiles = await GeneratePriFileAsync(inputFolder, cancellationToken: cancellationToken);
                tempFiles.AddRange(resourceFiles);
                if (resourceFiles.Count > 0)
                {
                    logger.LogDebug($"Resource files included in PRI:");
                    using var _ = logger.BeginScope("PRI Resources");
                    foreach (var resourceFile in resourceFiles)
                    {
                        logger.LogDebug("{ResourceFile}", resourceFile);
                    }
                }
            }

            // Handle self-contained deployment if requested
            if (selfContained && executablePath != null)
            {
                logger.LogDebug("{UISymbol} Preparing self-contained Windows App SDK runtime...", UiSymbols.Package);

                var winAppSDKDeploymentDir = await PrepareRuntimeForPackagingAsync(inputFolder, cancellationToken);

                // Add WindowsAppSDK.manifest to existing manifest
                var resolvedDeploymentDir = Path.Combine(winAppSDKDeploymentDir.FullName, "..", "extracted");
                var windowsAppSDKManifestPath = new FileInfo(Path.Combine(resolvedDeploymentDir, "AppxManifest.xml"));
                await EmbedWindowsAppSDKManifestToExeAsync(executablePath, winAppSDKDeploymentDir, windowsAppSDKManifestPath, cancellationToken);
            }

            await CreateMsixPackageFromFolderAsync(inputFolder, outputMsixPath, cancellationToken);

            // Handle certificate generation and signing
            if (autoSign)
            {
                await SignMsixPackageAsync(outputFolder, certificatePassword, generateDevCert, installDevCert, finalPackageName, extractedPublisher, outputMsixPath, certificatePath, resolvedManifestPath, cancellationToken);
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
                        file.Refresh();
                        if (file.Exists)
                        {
                            file.Delete();
                        }
                    }
                    catch
                    {
                        logger.LogDebug("Could not clean up {File}", file);
                    }
                }
            }
        }

        logger.LogDebug("MSIX package created successfully: {OutputMsixPath}", outputMsixPath);
        if (autoSign)
        {
            logger.LogDebug("Package has been signed");
        }

        return new CreateMsixPackageResult(outputMsixPath, autoSign);
    }

    private async Task EmbedWindowsAppSDKManifestToExeAsync(FileInfo exePath, DirectoryInfo winAppSDKDeploymentDir, FileInfo windowsAppSDKAppXManifestPath, CancellationToken cancellationToken)
    {
        // Use applicationLocation for DLL content (where runtime files were copied by PrepareRuntimeForPackagingAsync)
        var exeDir = exePath.Directory!;

        logger.LogDebug("{UISymbol} Generating Windows App SDK manifest from: {WindowsAppSDKAppXManifestPath}", UiSymbols.Note, windowsAppSDKAppXManifestPath);
        logger.LogDebug("{UISymbol} Using DLL content from: {WinAppSDKDeploymentDir}", UiSymbols.Package, winAppSDKDeploymentDir);

        var dllFiles = (winAppSDKDeploymentDir.EnumerateFiles("*.dll").Select(di => di.Name)).ToList();

        // Create a temporary manifest file
        var tempManifestPath = new FileInfo(Path.Combine(exeDir.FullName, "WindowsAppSDK_temp.manifest"));

        try
        {
            // Generate the manifest content
            await GenerateAppManifestFromAppxAsync(
                redirectDlls: false,
                inDllFiles: dllFiles,
                inAppxManifests: [windowsAppSDKAppXManifestPath],
                fragments: false,
                outAppManifestPath: tempManifestPath,
                cancellationToken: cancellationToken);

            (var cachedPackages, var mainVersion) = GetCachedPackages();
            if (cachedPackages == null || cachedPackages.Count == 0)
            {
                throw new InvalidOperationException("No cached Windows SDK packages found. Please install the Windows SDK or Windows App SDK.");
            }

            IEnumerable<FileInfo> appxFragments = GetComponents(cachedPackages);
            var architecture = WorkspaceSetupService.GetSystemArchitecture();
            dllFiles = [.. appxFragments.Select(fragment => Path.Combine(fragment.DirectoryName!, $"win-{architecture}\\native"))
                .Where(Directory.Exists)
                .SelectMany(dir => Directory.EnumerateFiles(dir, "*.dll"))];

            await GenerateAppManifestFromAppxAsync(
                redirectDlls: false,
                inDllFiles: dllFiles,
                inAppxManifests: appxFragments,
                fragments: true,
                outAppManifestPath: tempManifestPath,
                cancellationToken: cancellationToken);

            // Use mt.exe to merge manifests
            await EmbedManifestFileToExeAsync(exePath, tempManifestPath, cancellationToken);
        }
        finally
        {
            TryDeleteFile(tempManifestPath);
        }
    }

    private IEnumerable<FileInfo> GetComponents(Dictionary<string, string> cachedPackages)
    {
        var globalWinappDir = winappDirectoryService.GetGlobalWinappDirectory();
        var packagesDir = Path.Combine(globalWinappDir.FullName, "packages");
        if (!Directory.Exists(packagesDir))
        {
            throw new DirectoryNotFoundException($"Packages directory not found: {packagesDir}");
        }

        // Find the packages directory
        var appxFragments = cachedPackages
            .Select(cachedPackage => new FileInfo(Path.Combine(packagesDir, $"{cachedPackage.Key}.{cachedPackage.Value}", "runtimes-framework", "package.appxfragment")))
            .Where(f => f.Exists);
        return appxFragments;
    }

    /// <summary>
    /// Generates a Win32 manifest from an AppX manifest, similar to the GenerateAppManifestFromAppx MSBuild task.
    /// </summary>
    /// <param name="redirectDlls">Whether to redirect DLLs to %MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY%</param>
    /// <param name="inDllFiles">List of DLL files to include</param>
    /// <param name="inAppxManifests">List of paths to the input AppX manifest files, or fragments</param>
    /// <param name="fragments">Whether the input manifests are fragments (false), or full manifests (true)</param>
    /// <param name="outAppManifestPath">Path to write the generated manifest</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private static async Task GenerateAppManifestFromAppxAsync(
        bool redirectDlls,
        IEnumerable<string> inDllFiles,
        IEnumerable<FileInfo> inAppxManifests,
        bool fragments,
        FileInfo outAppManifestPath,
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
            doc.Load(inAppxManifest.FullName);
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
                    if (dllFileNode == null)
                    {
                        continue;
                    }

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
            dllFiles = [.. inDllFiles];

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

        await File.WriteAllTextAsync(outAppManifestPath.FullName, manifestContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
    }

    private async Task SignMsixPackageAsync(DirectoryInfo outputFolder, string certificatePassword, bool generateDevCert, bool installDevCert, string finalPackageName, string? extractedPublisher, FileInfo outputMsixPath, FileInfo? certPath, FileInfo resolvedManifestPath, CancellationToken cancellationToken)
    {
        if (certPath == null && generateDevCert)
        {
            if (string.IsNullOrWhiteSpace(extractedPublisher))
            {
                throw new InvalidOperationException("Publisher name required for certificate generation. Provide publisher option or ensure it exists in manifest.");
            }

            logger.LogDebug("Generating certificate for publisher: {ExtractedPublisher}", extractedPublisher);

            certPath = new FileInfo(Path.Combine(outputFolder.FullName, $"{finalPackageName}_cert.pfx"));
            await certificateService.GenerateDevCertificateAsync(extractedPublisher, certPath, certificatePassword, cancellationToken: cancellationToken);
        }

        if (certPath == null)
        {
            throw new InvalidOperationException("Certificate path required for signing. Provide certificatePath or set generateDevCert to true.");
        }

        // Validate that the certificate publisher matches the manifest publisher
        logger.LogDebug("{UISymbol} Validating certificate and manifest publishers match...", UiSymbols.Note);

        try
        {
            await CertificateService.ValidatePublisherMatchAsync(certPath, certificatePassword, resolvedManifestPath, cancellationToken);

            logger.LogDebug("{UISymbol} Certificate and manifest publishers match", UiSymbols.Check);
        }
        catch (InvalidOperationException ex)
        {
            // Re-throw with the specific error message format requested
            throw new InvalidOperationException(ex.Message, ex);
        }

        // Install certificate if requested
        if (installDevCert)
        {
            certificateService.InstallCertificate(certPath, certificatePassword, false);
        }

        // Sign the package
        await certificateService.SignFileAsync(outputMsixPath, certPath, certificatePassword, cancellationToken: cancellationToken);
    }

    private async Task CreateMsixPackageFromFolderAsync(DirectoryInfo inputFolder, FileInfo outputMsixPath, CancellationToken cancellationToken)
    {
        // Create MSIX package
        var makeappxArguments = $@"pack /o /d ""{Path.TrimEndingDirectorySeparator(inputFolder.FullName)}"" /nv /p ""{outputMsixPath.FullName}""";

        logger.LogDebug("Creating MSIX package...");

        await buildToolsService.RunBuildToolAsync("makeappx.exe", makeappxArguments, cancellationToken: cancellationToken);
    }

    private async Task RunMtToolAsync(string arguments, CancellationToken cancellationToken = default)
    {
        // Use BuildToolsService to run mt.exe
        await buildToolsService.RunBuildToolAsync("mt.exe", arguments, cancellationToken: cancellationToken);
    }

    private static void TryDeleteFile(FileInfo path)
    {
        try
        {
            path.Refresh();
            if (path.Exists)
            {
                path.Delete();
            }
        }
        catch
        {
            // Ignore cleanup failures
        }
    }

    /// <summary>
    /// Searches for appxmanifest.xml in the project by looking for .winapp directory in parent directories
    /// </summary>
    /// <param name="startDirectory">The directory to start searching from. If null, uses current directory.</param>
    /// <returns>Path to the project's appxmanifest.xml file, or null if not found</returns>
    public static FileInfo? FindProjectManifest(ICurrentDirectoryProvider currentDirectoryProvider, DirectoryInfo? startDirectory = null)
    {
        var directory = startDirectory ?? currentDirectoryProvider.GetCurrentDirectoryInfo();

        while (directory != null)
        {
            var manifestPath = new FileInfo(Path.Combine(directory.FullName, "appxmanifest.xml"));
            if (manifestPath.Exists)
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
    /// <param name="entryPointPath">Path to the entryPoint/executable that the manifest should reference</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple containing the debug manifest path and modified identity info</returns>
    public async Task<(FileInfo debugManifestPath, MsixIdentityResult debugIdentity)> GenerateSparsePackageStructureAsync(
        FileInfo originalManifestPath,
        string entryPointPath,
        CancellationToken cancellationToken = default)
    {
        var winappDir = winappDirectoryService.GetLocalWinappDirectory();
        var debugDir = new DirectoryInfo(Path.Combine(winappDir.FullName, "debug"));

        logger.LogDebug("{UISymbol} Creating sparse package structure in: {DebugDir}", UiSymbols.Note, debugDir.FullName);

        // Step 1: Create debug directory, removing existing one if present
        if (debugDir.Exists)
        {
            logger.LogDebug("{UISymbol} Removing existing debug directory...", UiSymbols.Trash);
            debugDir.Delete(recursive: true);
        }

        debugDir.Create();
        logger.LogDebug("{UISymbol} Created debug directory", UiSymbols.Folder);

        // Step 2: Parse original manifest to get identity and assets
        var originalManifestContent = await File.ReadAllTextAsync(originalManifestPath.FullName, Encoding.UTF8, cancellationToken);
        var originalIdentity = ParseAppxManifestAsync(originalManifestContent);

        // Step 3: Create debug identity with ".debug" suffix
        var debugIdentity = CreateDebugIdentity(originalIdentity);

        // Step 4: Modify manifest for sparse packaging and debug identity
        var debugManifestContent = UpdateAppxManifestContent(
            originalManifestContent,
            debugIdentity,
            entryPointPath,
            sparse: true,
            selfContained: false);

        logger.LogDebug("{UISymbol} Modified manifest for sparse packaging and debug identity", UiSymbols.Note);

        // Step 5: Write debug manifest
        var debugManifestPath = new FileInfo(Path.Combine(debugDir.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(debugManifestPath.FullName, debugManifestContent, Encoding.UTF8, cancellationToken);

        logger.LogDebug("{UISymbol} Created debug manifest: {DebugManifestPath}", UiSymbols.Files, debugManifestPath.FullName);

        // Step 6: Copy all assets
        await CopyAllAssetsAsync(originalManifestPath, debugDir, cancellationToken);

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
        string? entryPointPath,
        bool sparse,
        bool selfContained)
    {
        var modifiedContent = originalAppxManifestContent;

        if (identity != null)
        {
            // Replace package identity attributes
            modifiedContent = AppxPackageIdentityNameAssignmentRegex().Replace(modifiedContent, $@"$1""{identity.PackageName}""");

            // Replace application ID
            modifiedContent = AppxApplicationIdAssignmentRegex().Replace(modifiedContent, $@"$1""{identity.ApplicationId}""");
        }

        if (entryPointPath != null)
        {
            // Replace executable path with relative path from package root
            var entryPointDir = Path.GetDirectoryName(entryPointPath);
            var workingDir = string.IsNullOrEmpty(entryPointDir) ? currentDirectoryProvider.GetCurrentDirectory() : entryPointDir;
            string relativeExecutablePath;

            try
            {
                // Calculate relative path from the working directory (package root) to the executable
                relativeExecutablePath = Path.GetRelativePath(workingDir, entryPointPath);

                // Ensure we use forward slashes for consistency in manifest
                relativeExecutablePath = relativeExecutablePath.Replace('\\', '/');
            }
            catch
            {
                // Fallback to just the filename if relative path calculation fails
                relativeExecutablePath = Path.GetFileName(entryPointPath);
            }

            modifiedContent = AppxPackageApplicationExecutableAssignmentRegex().Replace(modifiedContent, $@"$1""{relativeExecutablePath}""");
        }

        bool isExe = Path.HasExtension(entryPointPath) && string.Equals(Path.GetExtension(entryPointPath), ".exe", StringComparison.OrdinalIgnoreCase);

        // Only apply sparse packaging modifications if sparse is true
        if (sparse)
        {
            // Add required namespaces for sparse packaging
            if (!modifiedContent.Contains("xmlns:uap10"))
            {
                modifiedContent = AppxPackageElementOpenTagRegex().Replace(modifiedContent, @"$1 xmlns:uap10=""http://schemas.microsoft.com/appx/manifest/uap/windows10/10""$2");
            }

            if (!modifiedContent.Contains("xmlns:desktop6"))
            {
                modifiedContent = AppxPackageOpenTagRegex().Replace(modifiedContent, @"$1 xmlns:desktop6=""http://schemas.microsoft.com/appx/manifest/desktop/windows10/6""$2");
            }

            // Add sparse package properties
            if (!modifiedContent.Contains("<uap10:AllowExternalContent>"))
            {
                modifiedContent = AppxPackagePropertiesCloseTagRegex().Replace(modifiedContent, @"    <uap10:AllowExternalContent>true</uap10:AllowExternalContent>
    <desktop6:RegistryWriteVirtualization>disabled</desktop6:RegistryWriteVirtualization>
$1");
            }

            // Ensure Application has sparse packaging attributes
            if (!modifiedContent.Contains("uap10:TrustLevel") && isExe)
            {
                modifiedContent = AppxApplicationOpenTagRegex().Replace(modifiedContent, @"$1 uap10:TrustLevel=""mediumIL"" uap10:RuntimeBehavior=""packagedClassicApp""$2");
            }

            // Remove EntryPoint if present (not needed for sparse packages)
            modifiedContent = AppxPackageEntryPointRegex().Replace(modifiedContent, "");

            // Add AppListEntry="none" to VisualElements if not present
            if (!modifiedContent.Contains("AppListEntry"))
            {
                modifiedContent = AppxPackageVisualElementsOpenTagRegex().Replace(modifiedContent, @"$1 AppListEntry=""none""$2");
            }

            // Add sparse-specific capabilities if not present
            if (!modifiedContent.Contains("unvirtualizedResources"))
            {
                modifiedContent = AppxPackageRunFullTrustCapabilityRegex().Replace(modifiedContent, @"$1
    <rescap:Capability Name=""unvirtualizedResources""/>
    <rescap:Capability Name=""allowElevation"" />");
            }
        }

        // Update or insert Windows App SDK dependency (skip for self-contained packages)
        if (!selfContained && (entryPointPath == null || isExe))
        {
            modifiedContent = UpdateWindowsAppSdkDependency(modifiedContent);
        }

        return modifiedContent;
    }

    /// <summary>
    /// Updates or inserts the Windows App SDK dependency in the manifest
    /// </summary>
    /// <param name="manifestContent">The manifest content to modify</param>
    /// <returns>The modified manifest content</returns>
    private string UpdateWindowsAppSdkDependency(string manifestContent)
    {
        // Get the Windows App SDK version from the locked winapp.yaml config
        var winAppSdkInfo = GetWindowsAppSdkDependencyInfo();

        if (winAppSdkInfo == null)
        {
            logger.LogDebug("{UISymbol} Could not determine Windows App SDK version, skipping dependency update", UiSymbols.Warning);
            return manifestContent;
        }

        // Check if Dependencies section exists
        if (!manifestContent.Contains("<Dependencies>"))
        {
            // Add Dependencies section before Applications
            manifestContent = AppxPackageApplicationsTagRegex().Replace(manifestContent, $@"  <Dependencies>
    <PackageDependency Name=""{winAppSdkInfo.RuntimeName}"" MinVersion=""{winAppSdkInfo.MinVersion}"" Publisher=""CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"" />
  </Dependencies>
$1");

            logger.LogDebug("{UISymbol} Added Windows App SDK dependency {RuntimeName} (v{MinVersion})", UiSymbols.Package, winAppSdkInfo.RuntimeName, winAppSdkInfo.MinVersion);
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

                logger.LogDebug("{UISymbols} Updated Windows App SDK dependency to {RuntimeName} v{MinVersion}", UiSymbols.Sync, winAppSdkInfo.RuntimeName, winAppSdkInfo.MinVersion);
            }
            else
            {
                // Add new dependency to existing Dependencies section
                manifestContent = AppxPackageDependenciesCloseTagRegex().Replace(manifestContent, $@"
    <PackageDependency Name=""{winAppSdkInfo.RuntimeName}"" MinVersion=""{winAppSdkInfo.MinVersion}"" Publisher=""CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"" />$1");

                logger.LogDebug("{UISymbols} Added Windows App SDK dependency {RuntimeName} to existing Dependencies section (v{MinVersion})", UiSymbols.Add, winAppSdkInfo.RuntimeName, winAppSdkInfo.MinVersion);
            }
        }

        return manifestContent;
    }

    /// <summary>
    /// Gets the Windows App SDK dependency information from the locked winapp.yaml config and package cache
    /// </summary>
    /// <returns>The dependency information, or null if not found</returns>
    private WindowsAppRuntimePackageInfo? GetWindowsAppSdkDependencyInfo()
    {
        try
        {
            var msixDir = GetRuntimeMsixDir();
            if (msixDir == null)
            {
                return null;
            }

            // Get the runtime package information from the MSIX inventory
            var runtimeInfo = GetWindowsAppRuntimePackageInfo(logger, msixDir);
            if (runtimeInfo == null)
            {
                logger.LogDebug("{UISymbol} Could not parse Windows App Runtime package information from MSIX inventory", UiSymbols.Warning);
                return null;
            }

            return runtimeInfo;
        }
        catch (Exception ex)
        {
            logger.LogDebug("{UISymbol} Error getting Windows App SDK dependency info: {Message}", UiSymbols.Warning, ex.Message);
            return null;
        }
    }

    private DirectoryInfo? GetRuntimeMsixDir()
    {
        (var cachedPackages, var mainVersion) = GetCachedPackages();
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

            logger.LogDebug("{UISymbol} Found cached runtime package: {RuntimePackage} v{RuntimeVersion}", UiSymbols.Package, runtimePackage.Key, runtimeVersion);
        }
        else
        {
            // For Windows App SDK 1.7 and earlier, runtime is included in the main package
            logger.LogDebug("{UISymbol} No separate runtime package found - using main package (Windows App SDK 1.7 or earlier)", UiSymbols.Note);
            logger.LogDebug("{UISymbol} Available cached packages: {CachedPackages}", UiSymbols.Note, string.Join(", ", cachedPackages.Keys));
        }

        // Find the MSIX directory with the runtime package
        var msixDir = workspaceSetupService.FindWindowsAppSdkMsixDirectory(usedVersions);
        if (msixDir == null)
        {
            logger.LogDebug("{UISymbol} Windows App SDK MSIX directory not found for cached runtime package", UiSymbols.Warning);
            return null;
        }

        return msixDir;
    }

    private (Dictionary<string, string>? CachedPackages, string? MainVersion) GetCachedPackages()
    {
        // Load the locked config to get the actual package versions
        if (!configService.Exists())
        {
            logger.LogDebug("{UISymbol} No winapp.yaml found, cannot determine locked Windows App SDK version", UiSymbols.Warning);
            return (null, null);
        }

        var config = configService.Load();

        // Get the main Windows App SDK version from config
        var mainVersion = config.GetVersion("Microsoft.WindowsAppSDK");
        if (string.IsNullOrEmpty(mainVersion))
        {
            logger.LogDebug("{UISymbol} No Microsoft.WindowsAppSDK package found in winapp.yaml", UiSymbols.Warning);
            return (null, null);
        }

        logger.LogDebug("{UISymbol} Found Windows App SDK main package: v{MainVersion}", UiSymbols.Package, mainVersion);

        try
        {
            // Use PackageCacheService to find the runtime package that was installed with the main package
            return (packageCacheService.GetCachedPackageAsync("Microsoft.WindowsAppSDK", mainVersion, CancellationToken.None).GetAwaiter().GetResult(), mainVersion);
        }
        catch (KeyNotFoundException)
        {
            logger.LogDebug("{UISymbol} Microsoft.WindowsAppSDK v{MainVersion} not found in package cache", UiSymbols.Warning, mainVersion);
        }

        return (null, null);
    }

    /// <summary>
    /// Parses the MSIX inventory file to extract Windows App Runtime package information
    /// </summary>
    /// <param name="msixDir">The MSIX directory containing the inventory file</param>
    /// <returns>Package information, or null if not found</returns>
    private static WindowsAppRuntimePackageInfo? GetWindowsAppRuntimePackageInfo(ILogger logger, DirectoryInfo msixDir)
    {
        try
        {
            // Use the shared inventory parsing logic (synchronous version)
            var packageEntries = WorkspaceSetupService.ParseMsixInventoryAsync(logger, msixDir, CancellationToken.None).GetAwaiter().GetResult();

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

                    logger.LogDebug("{UISymbol} Found Windows App Runtime: {RuntimeName} v{Version}", UiSymbols.Package, runtimeName, version);

                    return new WindowsAppRuntimePackageInfo
                    {
                        RuntimeName = runtimeName,
                        MinVersion = version
                    };
                }
            }

            logger.LogDebug("{UISymbol} No Windows App Runtime main package found in inventory", UiSymbols.Note);
            logger.LogDebug("{UISymbol} Available packages: {AvailablePackages}", UiSymbols.Note, string.Join(", ", packageEntries.Select(e => e.PackageIdentity)));

            return null;
        }
        catch (Exception ex)
        {
            logger.LogDebug("{UISymbol} Error parsing MSIX inventory: {Message}", UiSymbols.Note, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Copies files referenced in the manifest to the target directory
    /// </summary>
    private async Task CopyAllAssetsAsync(FileInfo manifestPath, DirectoryInfo targetDir, CancellationToken cancellationToken)
    {
        var originalManifestDir = manifestPath.DirectoryName;

        logger.LogDebug("{UISymbol} Copying manifest-referenced files from: {OriginalManifestDir}", UiSymbols.Note, originalManifestDir);

        var filesCopied = await CopyManifestReferencedFilesAsync(manifestPath, targetDir, cancellationToken);

        logger.LogDebug("{UISymbol} Copied {FilesCopied} files to target directory", UiSymbols.Note, filesCopied);
    }

    /// <summary>
    /// Copies files that are referenced in the manifest using regex pattern matching
    /// </summary>
    private async Task<int> CopyManifestReferencedFilesAsync(FileInfo manifestPath, DirectoryInfo targetDir, CancellationToken cancellationToken)
    {
        var filesCopied = 0;
        var manifestDir = manifestPath.Directory;
        if (manifestDir == null)
        {
            logger.LogWarning("{UISymbol} Manifest directory not found for: {ManifestPath}", UiSymbols.Warning, manifestPath);
            return filesCopied;
        }

        // Read the manifest content
        var manifestContent = await File.ReadAllTextAsync(manifestPath.FullName, Encoding.UTF8, cancellationToken);

        logger.LogDebug("{UISymbol} Reading manifest: {ManifestPath}", UiSymbols.Note, manifestPath);

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
            var publicFolderMatch = PublicFolderRegex().Match(appExtensionElement);
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
                    string? filePath;
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
                            logger.LogDebug("{UISymbol} Found file in PublicFolder '{PublicFolder}': {FilePath}", UiSymbols.Folder, publicFolder, filePath);
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

        // Copy MRT variants for each referenced file
        foreach (var relativeFilePath in referencedFiles)
        {
            var logicalSourceFile = new FileInfo(Path.Combine(manifestDir.FullName, relativeFilePath));
            var sourceDir = logicalSourceFile.Directory;

            if (sourceDir is null || !sourceDir.Exists)
            {
                logger.LogDebug("{UISymbol} Source directory not found for referenced file: {RelativeFilePath}",
                    UiSymbols.Warning, relativeFilePath);
                continue;
            }

            var logicalBaseName = Path.GetFileNameWithoutExtension(logicalSourceFile.Name);
            var extension = logicalSourceFile.Extension; // includes the dot, e.g. ".png"

            // Enumerate candidates: same directory, same extension, starting with base name
            // e.g. Logo.png, Logo.scale-200.png, Logo.scale-200.theme-dark.en-US.png, etc.
            var searchPattern = logicalBaseName + "*" + extension;
            var candidates = sourceDir.EnumerateFiles(searchPattern);
            var anyCopiedForLogical = false;

            foreach (var candidateFile in candidates)
            {
                var candidateName = candidateFile.Name;
                var candidateNameWithoutExtension = Path.GetFileNameWithoutExtension(candidateName);

                if (!IsMrtVariantName(logicalBaseName, candidateNameWithoutExtension))
                {
                    // e.g. Logo.old.png or Logo.scale-200.backup.png -> ignore
                    continue;
                }

                // Build target relative path preserving subdirectory & actual filename
                var relativeDir = Path.GetDirectoryName(relativeFilePath);
                string candidateRelativePath = string.IsNullOrEmpty(relativeDir)
                    ? candidateName
                    : Path.Combine(relativeDir, candidateName);

                var targetFile = new FileInfo(Path.Combine(targetDir.FullName, candidateRelativePath));

                targetFile.Directory?.Create();
                candidateFile.CopyTo(targetFile.FullName, overwrite: true);
                filesCopied++;
                anyCopiedForLogical = true;

                logger.LogDebug("{UISymbol} Copied MRT variant: {Logical} -> {Variant}",
                    UiSymbols.Files, relativeFilePath, candidateRelativePath);
            }

            // Fallback: if we didn't find any MRT variants but the logical file itself exists, copy it
            if (!anyCopiedForLogical && logicalSourceFile.Exists)
            {
                var targetFile = new FileInfo(Path.Combine(targetDir.FullName, relativeFilePath));
                targetFile.Directory?.Create();
                logicalSourceFile.CopyTo(targetFile.FullName, overwrite: true);
                filesCopied++;

                logger.LogDebug("{UISymbol} Copied (no MRT variants found): {RelativeFilePath}",
                    UiSymbols.Files, relativeFilePath);
            }
            else if (!anyCopiedForLogical && !logicalSourceFile.Exists)
            {
                logger.LogDebug("{UISymbol} Referenced file not found (no MRT variants): {SourceFile}",
                    UiSymbols.Warning, logicalSourceFile);
            }
        }

        return filesCopied;
    }

    // ltr / rtl
    private static bool IsLayoutDirectionQualifier(string token)
    {
        return token.Equals("ltr", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("rtl", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSingleQualifierToken(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        return LanguageQualifierRegex().IsMatch(token)
            || ScaleQualifierRegex().IsMatch(token)
            || ThemeQualifierRegex().IsMatch(token)
            || ContrastQualifierRegex().IsMatch(token)
            || DxFeatureLevelQualifierRegex().IsMatch(token)
            || DeviceFamilyQualifierRegex().IsMatch(token)
            || HomeRegionQualifierRegex().IsMatch(token)
            || ConfigurationQualifierRegex().IsMatch(token)
            || TargetSizeQualifierRegex().IsMatch(token)
            || AltFormQualifierRegex().IsMatch(token)
            || IsLayoutDirectionQualifier(token);
    }

    private static bool IsQualifierToken(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var parts = token.Split('_');

        foreach (var part in parts)
        {
            if (!IsSingleQualifierToken(part))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns true if <paramref name="candidateNameWithoutExtension"/> is a valid MRT
    /// variant of the logical base name (dots allowed in base name).
    /// </summary>
    private static bool IsMrtVariantName(string logicalBaseName, string candidateNameWithoutExtension)
    {
        // Split by '.'; "Logo.scale-200.theme-dark" -> ["Logo", "scale-200", "theme-dark"]
        var parts = candidateNameWithoutExtension.Split('.');

        if (parts.Length == 0)
        {
            return false;
        }

        // First token must match logical base name (case-insensitive)
        if (!parts[0].Equals(logicalBaseName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // No qualifiers -> exact logical name, valid
        if (parts.Length == 1)
        {
            return true;
        }

        // All remaining tokens must be valid MRT qualifiers
        for (int i = 1; i < parts.Length; i++)
        {
            if (!IsQualifierToken(parts[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks if a package with the given name exists and unregisters it if found
    /// </summary>
    /// <param name="packageName">The name of the package to check and unregister</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if package was found and unregistered, false if no package was found</returns>
    public async Task<bool> UnregisterExistingPackageAsync(string packageName, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("{UISymbol} Checking for existing package...", UiSymbols.Trash);

        try
        {
            // First check if package exists
            var checkCommand = $"Get-AppxPackage -Name '{packageName}'";
            var (_, checkResult) = await powerShellService.RunCommandAsync(checkCommand, cancellationToken: cancellationToken);

            if (!string.IsNullOrWhiteSpace(checkResult))
            {
                // Package exists, remove it
                logger.LogDebug("{UISymbol} Found existing package '{PackageName}', removing it...", UiSymbols.Package, packageName);

                var unregisterCommand = $"Get-AppxPackage -Name '{packageName}' | Remove-AppxPackage";
                await powerShellService.RunCommandAsync(unregisterCommand, cancellationToken: cancellationToken);

                logger.LogDebug("{UISymbol} Existing package unregistered successfully", UiSymbols.Check);
                return true;
            }
            else
            {
                // No package found
                logger.LogDebug("{UISymbol} No existing package found", UiSymbols.Note);
                return false;
            }
        }
        catch (Exception ex)
        {
            // If check fails, package likely doesn't exist or we don't have permission
            logger.LogDebug("{UISymbol} Could not check for existing package: {Message}", UiSymbols.Note, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Registers a sparse package with external location using Add-AppxPackage
    /// </summary>
    /// <param name="manifestPath">Path to the appxmanifest.xml file</param>
    /// <param name="externalLocation">External location path (typically the working directory)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task RegisterSparsePackageAsync(FileInfo manifestPath, DirectoryInfo externalLocation, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("{UISymbol} Registering sparse package with external location...", UiSymbols.Clipboard);

        var registerCommand = $"Add-AppxPackage -Path '{manifestPath.FullName}' -ExternalLocation '{externalLocation.FullName}' -Register -ForceUpdateFromAnyVersion";

        try
        {
            var (exitCode, _) = await powerShellService.RunCommandAsync(registerCommand, cancellationToken: cancellationToken);

            if (exitCode != 0)
            {
                throw new InvalidOperationException($"PowerShell command failed with exit code {exitCode}");
            }

            logger.LogDebug("{UISymbol} Sparse package registered successfully", UiSymbols.Check);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to register sparse package: {ex.Message}", ex);
        }
    }

    private void CopyRuntimeFiles(DirectoryInfo extractedDir, DirectoryInfo deploymentDir)
    {
        using var _ = logger.BeginScope("CopyRuntimeFiles");
        var patterns = new[] { "*.dll", "workloads*.json", "restartAgent.exe", "map.html", "*.mui", "*.png", "*.winmd", "*.xaml", "*.xbf", "*.pri" };

        foreach (var pattern in patterns)
        {
            var files = extractedDir.GetFiles(pattern, SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var relativePath = Path.GetRelativePath(extractedDir.FullName, file.FullName);
                var destPath = Path.Combine(deploymentDir.FullName, relativePath);

                // Create destination directory if needed
                var destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                file.CopyTo(destPath, overwrite: true);

                logger.LogDebug("{UISymbols} {RelativePath}", UiSymbols.Files, relativePath);
            }
        }
    }

    /// <summary>
    /// Prepares Windows App SDK runtime files for packaging into an MSIX by extracting them to the input folder
    /// </summary>
    /// <param name="inputFolder">The folder where runtime files should be copied</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The path to the self-contained deployment directory</returns>
    private async Task<DirectoryInfo> PrepareRuntimeForPackagingAsync(DirectoryInfo inputFolder, CancellationToken cancellationToken)
    {
        var arch = WorkspaceSetupService.GetSystemArchitecture();

        var winappDir = winappDirectoryService.GetLocalWinappDirectory();

        // Extract runtime files using the existing method
        await SetupSelfContainedAsync(winappDir, arch, cancellationToken);

        // Copy runtime files from .winapp/self-contained to input folder
        var runtimeSourceDir = new DirectoryInfo(Path.Combine(winappDir.FullName, "self-contained", arch, "deployment"));

        if (runtimeSourceDir.Exists)
        {
            // Copy files recursively to maintain directory structure
            foreach (var file in runtimeSourceDir.GetFiles("*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(runtimeSourceDir.FullName, file.FullName);
                var destFile = Path.Combine(inputFolder.FullName, relativePath);

                // Create destination directory if needed
                var destDir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                file.CopyTo(destFile, overwrite: true);

                logger.LogDebug("{UISymbol} Bundled runtime: {RelativePath}", UiSymbols.Folder, relativePath);
            }

            logger.LogDebug("{UISymbol} Windows App SDK runtime bundled into package", UiSymbols.Check);
        }
        else
        {
            throw new DirectoryNotFoundException($"Runtime files not found at {runtimeSourceDir}");
        }

        return runtimeSourceDir;
    }
}
