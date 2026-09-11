// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Security;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Tools;

namespace WinApp.Cli.Services;

internal partial class MsixService(
    IWinappDirectoryService winappDirectoryService,
    IConfigService configService,
    IBuildToolsService buildToolsService,
    ICertificateService certificateService,
    IWindowsAppRuntimeService windowsAppRuntimeService,
    IDevModeService devModeService,
    IDotNetService dotNetService,
    INugetService nugetService,
    IWinmdService winmdService,
    IPriService priService,
    IPackageRegistrationService packageRegistrationService,
    IBundleService bundleService,
    IBundleValidationService bundleValidationService,
    ILogger<MsixService> logger,
    ICurrentDirectoryProvider currentDirectoryProvider) : IMsixService
{
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

        // Read and extract package identity from appxmanifest.xml
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
        var doc = AppxManifestDocument.Parse(appxManifestContent);

        var identity = doc.GetIdentityElement()
            ?? throw new InvalidOperationException("No Identity element found in AppX manifest");

        var packageName = identity.Attribute("Name")?.Value
            ?? throw new InvalidOperationException("AppX manifest Identity element missing required Name or Publisher attributes");

        var publisher = identity.Attribute("Publisher")?.Value
            ?? throw new InvalidOperationException("AppX manifest Identity element missing required Name or Publisher attributes");

        var applicationId = doc.ApplicationId
            ?? throw new InvalidOperationException("No Application element with Id attribute found in AppX manifest");

        return new MsixIdentityResult(packageName, publisher, applicationId);
    }

    /// <summary>
    /// Extracts execution alias names from an AppX manifest content.
    /// Looks for uap5:ExecutionAlias or desktop:ExecutionAlias elements.
    /// </summary>
    /// <param name="manifestContent">The content of the appxmanifest.xml file</param>
    /// <returns>List of alias names (e.g. "myapp.exe")</returns>
    public static List<string> ExtractExecutionAliases(string manifestContent)
    {
        var doc = AppxManifestDocument.Parse(manifestContent);
        var aliases = new List<string>();
        var root = doc.Document.Root;
        if (root == null)
        {
            return aliases;
        }

        foreach (var element in root.Descendants()
            .Where(e => e.Name.LocalName == "ExecutionAlias"
                && (e.Name.Namespace == AppxManifestDocument.Uap5Ns || e.Name.Namespace == AppxManifestDocument.DesktopNs)))
        {
            var alias = element.Attribute("Alias")?.Value;
            if (alias != null)
            {
                aliases.Add(alias);
            }
        }

        return aliases;
    }

    /// <summary>
    /// Resolves $placeholder$ tokens in manifest content. Handles $targetnametoken$ and $targetentrypoint$.
    /// If the Executable attribute contains a placeholder and no --executable is provided,
    /// attempts to infer by searching for .exe files in the input folder.
    /// </summary>
    private static string ResolveManifestPlaceholders(string manifestContent, string? executable, DirectoryInfo inputFolder, TaskContext taskContext)
    {
        // Check if manifest contains any placeholders at all
        if (!PlaceholderHelper.ContainsPlaceholders(manifestContent))
        {
            return manifestContent;
        }

        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Determine the executable name for $targetnametoken$
        if (!string.IsNullOrWhiteSpace(executable))
        {
            // --executable was provided explicitly
            var nameWithoutExtension = Path.GetFileNameWithoutExtension(executable);
            replacements[PlaceholderHelper.TargetNameToken] = nameWithoutExtension;

            // Also replace the Executable attribute value if it contains a placeholder
            var doc = AppxManifestDocument.Parse(manifestContent);
            if (doc.ApplicationExecutable != null && PlaceholderHelper.ContainsPlaceholders(doc.ApplicationExecutable))
            {
                doc.ApplicationExecutable = executable;
                manifestContent = doc.ToXml();
            }

            taskContext.AddDebugMessage($"{UiSymbols.Note} Using specified executable: {executable}");
        }
        else
        {
            // Check if the Executable attribute in the manifest has a placeholder
            var doc = AppxManifestDocument.Parse(manifestContent);
            if (doc.ApplicationExecutable != null && PlaceholderHelper.ContainsPlaceholders(doc.ApplicationExecutable))
            {
                // Try to auto-infer by finding .exe files in the input folder root. Runtime
                // helpers (createdump.exe, RestartAgent.exe, ...) are never entry points, so
                // they are excluded from the candidates but kept for the error message.
                var allExeFiles = inputFolder.Exists
                    ? inputFolder.GetFiles("*.exe", SearchOption.TopDirectoryOnly)
                    : [];
                var exeFiles = allExeFiles.Where(f => !IsRuntimeToolExecutable(f.Name)).ToArray();

                if (exeFiles.Length == 1)
                {
                    var inferredExe = exeFiles[0].Name;
                    var nameWithoutExtension = Path.GetFileNameWithoutExtension(inferredExe);
                    replacements[PlaceholderHelper.TargetNameToken] = nameWithoutExtension;

                    doc.ApplicationExecutable = inferredExe;
                    manifestContent = doc.ToXml();

                    taskContext.AddDebugMessage($"{UiSymbols.Note} Auto-inferred executable: {inferredExe}");
                }
                else
                {
                    throw new InvalidOperationException(
                        $"The manifest contains a placeholder for the executable but {DescribeExecutableCandidates(allExeFiles, exeFiles)}. " +
                        "Edit the manifest to specify the executable or use --executable to specify the relative path to the exe.");
                }
            }
        }

        // Apply all placeholder replacements
        manifestContent = PlaceholderHelper.ReplacePlaceholders(manifestContent, replacements);

        // Validation check: ensure no unresolved placeholders remain
        PlaceholderHelper.ThrowIfUnresolvedPlaceholders(manifestContent);

        return manifestContent;
    }

    /// <summary>
    /// Builds the middle clause of the "cannot resolve the executable placeholder" error,
    /// naming the candidates so the caller knows what to pass to <c>--executable</c>. When
    /// every executable was filtered out as a runtime helper, it says so instead of claiming
    /// the folder has no <c>.exe</c> files at all.
    /// </summary>
    private static string DescribeExecutableCandidates(FileInfo[] allExeFiles, FileInfo[] candidates)
    {
        if (candidates.Length > 1)
        {
            return $"multiple .exe files in the input folder could be the app: {FormatExeNames(candidates)}";
        }

        var skipped = allExeFiles.Where(f => IsRuntimeToolExecutable(f.Name)).ToArray();
        if (skipped.Length > 0)
        {
            var helperLabel = skipped.Length == 1 ? "a known runtime helper" : "known runtime helpers";
            return $"no .exe file in the input folder could be the app ({FormatExeNames(skipped)} skipped as {helperLabel})";
        }

        return "no .exe files were found in the input folder";
    }

    private static string FormatExeNames(IEnumerable<FileInfo> files) =>
        string.Join(", ", files.Select(f => f.Name).Order(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves <c>&lt;Resource Language="x-generate"/&gt;</c> in the manifest by replacing it
    /// with concrete language tags. Languages are extracted from the existing <c>resources.pri</c>
    /// in the input folder; falls back to <c>en-US</c> when no PRI or no language qualifiers are found.
    /// </summary>
    private async Task<string> ResolveResourceLanguageXGenerateAsync(
        string manifestContent,
        DirectoryInfo inputFolder,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        if (!ContainsXGenerateLanguage(manifestContent))
        {
            return manifestContent;
        }

        taskContext.AddDebugMessage($"{UiSymbols.Note} Detected <Resource Language=\"x-generate\"/> — resolving to concrete language(s)");

        var languages = new List<string>();

        // Try to extract languages from existing resources.pri
        var priFile = new FileInfo(Path.Combine(inputFolder.FullName, "resources.pri"));
        if (priFile.Exists)
        {
            languages = await priService.ExtractLanguagesFromPriAsync(priFile, taskContext, cancellationToken);
        }

        if (languages.Count == 0)
        {
            languages.Add("en-US");
            taskContext.AddDebugMessage($"{UiSymbols.Note} No language qualifiers found in PRI — defaulting to en-US");
        }
        else
        {
            taskContext.AddDebugMessage($"{UiSymbols.Note} Resolved resource languages from PRI: {string.Join(", ", languages)}");
        }

        return ReplaceXGenerateLanguage(manifestContent, languages);
    }

    /// <summary>
    /// Returns true if the manifest contains a <c>&lt;Resource Language="x-generate"/&gt;</c> element.
    /// </summary>
    internal static bool ContainsXGenerateLanguage(string manifestContent)
    {
        var doc = AppxManifestDocument.Parse(manifestContent);
        return doc.GetResourceLanguages()
            .Any(lang => string.Equals(lang, "x-generate", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Replaces <c>&lt;Resource Language="x-generate"/&gt;</c> with concrete
    /// <c>&lt;Resource Language="..."/&gt;</c> entries for each specified language.
    /// </summary>
    internal static string ReplaceXGenerateLanguage(string manifestContent, IList<string> languages)
    {
        var doc = AppxManifestDocument.Parse(manifestContent);
        doc.SetResourceLanguages(languages);
        return doc.ToXml();
    }

    /// <summary>
    /// Creates an MSIX package from a prepared package directory
    /// </summary>
    /// <param name="installDevCert">Install certificate to machine</param>
    /// <param name="publisher">Publisher name for certificate generation (default: extracted from manifest)</param>
    /// <param name="manifestPath">Path to the manifest file (optional)</param>
    /// <param name="selfContained">Enable self-contained deployment</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing the MSIX path and signing status</returns>
    public async Task<CreateMsixPackageResult> CreateMsixPackageAsync(
        DirectoryInfo inputFolder,
        FileSystemInfo? outputPath,
        TaskContext taskContext,
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
        string? executable = null,
        CancellationToken cancellationToken = default)
    {
        // Validate input folder and manifest
        if (!inputFolder.Exists)
        {
            throw new DirectoryNotFoundException($"Input folder not found: {inputFolder}");
        }

        // Warn if the input folder contains .pfx certificate files, which are likely
        // development certificates that should not be included in the package payload.
        var pfxFiles = inputFolder.EnumerateFiles("*.pfx", SearchOption.AllDirectories).ToList();
        if (pfxFiles.Count > 0)
        {
            foreach (var pfxFile in pfxFiles)
            {
                var relativePath = Path.GetRelativePath(inputFolder.FullName, pfxFile.FullName);
                taskContext.AddStatusMessage($"{UiSymbols.Warning} PFX certificate file found in input folder: {relativePath}. Consider removing it before packaging.");
            }
        }

        // Check for build-artifact subdirectories (e.g. 'AppX') that must not be included in the
        // package. Any found are excluded from staging and the user is warned.
        var excludedDirectories = BuildStagingExclusions(inputFolder, taskContext);

        // Determine manifest path based on priority:
        // 1. Use provided manifestPath parameter
        // 2. Check for Package.appxmanifest or appxmanifest.xml in input folder
        // 3. Check for Package.appxmanifest or appxmanifest.xml in current directory
        FileInfo resolvedManifestPath;
        if (manifestPath != null)
        {
            resolvedManifestPath = manifestPath;
            taskContext.AddDebugMessage($"{UiSymbols.Note} Using specified manifest: {resolvedManifestPath}");
        }
        else
        {
            var resolvedFromSearch = FindManifestInDirectory(new DirectoryInfo(inputFolder.FullName))
                ?? FindManifestInDirectory(new DirectoryInfo(currentDirectoryProvider.GetCurrentDirectory()));

            if (resolvedFromSearch != null)
            {
                resolvedManifestPath = resolvedFromSearch;
                taskContext.AddDebugMessage($"{UiSymbols.Note} Using manifest: {resolvedManifestPath}");
            }
            else
            {
                throw new FileNotFoundException($"Manifest file not found. Searched for Package.appxmanifest, then appxmanifest.xml in: input folder ({inputFolder.FullName}), current directory ({currentDirectoryProvider.GetCurrentDirectory()})");
            }
        }

        if (!resolvedManifestPath.Exists)
        {
            throw new FileNotFoundException($"Manifest file not found: {resolvedManifestPath}");
        }

        // Determine package name and publisher
        var finalPackageName = packageName;
        var extractedPublisher = publisher;
        string? extractedVersion = null;

        var manifestContent = await File.ReadAllTextAsync(resolvedManifestPath.FullName, Encoding.UTF8, cancellationToken);

        // Resolve $placeholder$ tokens in the manifest
        manifestContent = ResolveManifestPlaceholders(manifestContent, executable, inputFolder, taskContext);

        // Resolve <Resource Language="x-generate"/> with concrete language(s) from PRI
        manifestContent = await ResolveResourceLanguageXGenerateAsync(manifestContent, inputFolder, taskContext, cancellationToken);

        // Update manifest content to ensure it's either referencing Windows App SDK or is self-contained
        // Fetch dotnet package list once for all downstream operations
        var dotNetPackageList = await FetchDotNetPackageListAsync(cancellationToken);

        // Determine executable path for ProcessorArchitecture auto-detection, and detect whether
        // this is a sparse (AllowExternalContent) manifest so the rewrite applies sparse
        // corrections (win32App/mediumIL, capabilities, AppListEntry) to folder inputs too.
        string? resolvedExePath = null;
        bool isSparseManifest;
        {
            var tempDoc = AppxManifestDocument.Parse(manifestContent);
            isSparseManifest = tempDoc.AllowsExternalContent;
            var appExe = tempDoc.ApplicationExecutable;
            if (appExe != null)
            {
                resolvedExePath = Path.Combine(inputFolder.FullName, appExe);
            }
        }

        (manifestContent, var packageArch) = await UpdateAppxManifestContentAsync(manifestContent, null, null, resolvedExePath, sparse: isSparseManifest, selfContained: selfContained, dotNetPackageList, taskContext, cancellationToken);

        // Parse the manifest to extract identity, executable, and architecture info
        var manifestDoc = AppxManifestDocument.Parse(manifestContent);

        // When packaging a FOLDER for a sparse (AllowExternalContent) package, warn about
        // content that should live at the external location instead of inside the .msix.
        foreach (var warning in GetSparseFolderContentWarnings(inputFolder, manifestContent))
        {
            taskContext.AddStatusMessage(warning);
        }

        try
        {
            if (string.IsNullOrWhiteSpace(finalPackageName))
            {
                finalPackageName = manifestDoc.IdentityName ?? "Package";
            }

            if (string.IsNullOrWhiteSpace(extractedPublisher))
            {
                extractedPublisher = manifestDoc.IdentityPublisher;
            }

            if (string.IsNullOrWhiteSpace(extractedVersion))
            {
                extractedVersion = manifestDoc.IdentityVersion;
            }
        }
        catch
        {
            finalPackageName ??= "Package";
        }

        // Clean the resolved package name to ensure it meets MSIX schema requirements
        finalPackageName = ManifestService.CleanPackageName(finalPackageName);

        var defaultMsixFileName = BuildDefaultMsixFileName(finalPackageName, packageArch, extractedVersion);

        FileInfo outputMsixPath;
        DirectoryInfo outputFolder;
        if (outputPath == null)
        {
            outputFolder = currentDirectoryProvider.GetCurrentDirectoryInfo();
            outputMsixPath = new FileInfo(Path.Combine(outputFolder.FullName, defaultMsixFileName));
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
                outputMsixPath = new FileInfo(Path.Combine(outputPath.FullName, defaultMsixFileName));
            }
        }

        // Ensure output folder exists
        if (!outputFolder.Exists)
        {
            outputFolder.Create();
        }

        // Create a temporary staging directory so we never modify the original input folder.
        // All packaging operations (manifest updates, asset copies, PRI generation, self-contained
        // runtime bundling) happen in this staging copy. The original target folder stays untouched.
        var stagingDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"winapp-package-{Guid.NewGuid():N}"));
        stagingDir.Create();

        taskContext.AddDebugMessage($"{UiSymbols.Note} Created staging directory: {stagingDir.FullName}");

        try
        {
            // Check if the manifest was generated by MSBuild and a .build.appxrecipe is available.
            // When present, the recipe lists exactly which files belong in the package and their
            // correct PackagePaths, producing a cleaner MSIX without build artifacts.
            var isMSBuildGenerated = manifestDoc.Document.Root?
                .Element(AppxManifestDocument.BuildNs + "Metadata")?
                .Elements(AppxManifestDocument.BuildNs + "Item")
                .Any(e => string.Equals(e.Attribute("Name")?.Value, "makepri.exe", StringComparison.OrdinalIgnoreCase)) == true;

            FileInfo? recipeFile = null;
            if (isMSBuildGenerated)
            {
                recipeFile = inputFolder.EnumerateFiles("*.build.appxrecipe", SearchOption.TopDirectoryOnly).FirstOrDefault();
            }

            if (recipeFile != null)
            {
                taskContext.AddDebugMessage($"{UiSymbols.Note} MSBuild-generated manifest detected");
                taskContext.AddDebugMessage($"{UiSymbols.Files} Using appxrecipe for staging: {recipeFile.Name}");
                await CopyFilesFromRecipeAsync(recipeFile, stagingDir, taskContext, LayoutReconciliation.None, cancellationToken);
            }
            else
            {
                // No recipe available — copy the entire input folder to staging
                CopyDirectoryRecursive(inputFolder, stagingDir, excludedDirectories);
                taskContext.AddDebugMessage($"{UiSymbols.Files} Copied input folder to staging directory");
            }

            // Write the updated manifest into the staging directory
            var updatedManifestPath = Path.Combine(stagingDir.FullName, "appxmanifest.xml");
            await File.WriteAllTextAsync(updatedManifestPath, manifestContent, Encoding.UTF8, cancellationToken);

            // Resolve executable path relative to the staging directory
            var applicationExecutable = manifestDoc.ApplicationExecutable;
            FileInfo? executablePath = applicationExecutable != null ? new FileInfo(Path.Combine(stagingDir.FullName, applicationExecutable)) : null;

            // Pre-compute expanded manifest resources from the original manifest
            var manifestIsOutsideInputFolder = !inputFolder.FullName.TrimEnd(Path.DirectorySeparatorChar)
                .Equals(resolvedManifestPath.Directory!.FullName.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

            // Pre-extract all file references from the manifest once (used by both
            // MrtAssetHelper for images and CopyManifestReferencedFiles for non-image files)
            var allManifestReferences = ManifestFileReferenceHelper.ExtractAllFileReferencesFromManifest(manifestContent);

            // If manifest is outside input folder, copy its referenced assets into the staging directory
            if (manifestIsOutsideInputFolder)
            {
                var externalAssets = MrtAssetHelper.GetExpandedManifestReferencedFiles(resolvedManifestPath, taskContext);
                MrtAssetHelper.CopyAllAssets(externalAssets, stagingDir, taskContext);
            }

            // Copy any non-image files referenced in the manifest that are missing from
            // staging. This handles files like manifest.json or other extension-referenced
            // resources that aren't discovered by the image-asset extractor.
            CopyManifestReferencedFiles(allManifestReferences, resolvedManifestPath.Directory!, inputFolder, stagingDir, taskContext, cancellationToken);

            taskContext.AddDebugMessage($"Creating MSIX package from staging: {stagingDir.FullName}");
            taskContext.AddDebugMessage($"Output: {outputMsixPath.FullName}");

            // Generate PRI files if not skipped and no existing PRI from the build output
            var existingPri = new FileInfo(Path.Combine(stagingDir.FullName, "resources.pri"));
            if (!skipPri && !existingPri.Exists)
            {
                taskContext.AddDebugMessage("Generating PRI configuration and files...");

                // Expand manifest-referenced files from the staging manifest so that
                // assets from both the input folder and external manifest are discovered.
                var stagingManifest = new FileInfo(Path.Combine(stagingDir.FullName, "appxmanifest.xml"));
                var priExpandedFiles = MrtAssetHelper.GetExpandedManifestReferencedFiles(stagingManifest, taskContext);
                var priResourceCandidates = priExpandedFiles.Select(file => file.RelativePath);
                await priService.CreatePriConfigAsync(
                    stagingDir,
                    taskContext,
                    precomputedPriResourceCandidates: priResourceCandidates,
                    cancellationToken: cancellationToken);
                var resourceFiles = await priService.GeneratePriFileAsync(stagingDir, taskContext, cancellationToken: cancellationToken);
                if (resourceFiles.Count > 0 && logger.IsEnabled(LogLevel.Debug))
                {
                    taskContext.AddDebugMessage($"Resource files included in PRI:");
                    await taskContext.AddSubTaskAsync("Pri Resources", async (taskContext, cancellationToken) =>
                    {
                        foreach (var resourceFile in resourceFiles)
                        {
                            taskContext.AddDebugMessage(resourceFile.ToString());
                        }
                        return Task.FromResult(0);
                    }, cancellationToken);
                }
            }
            else if (!skipPri && existingPri.Exists)
            {
                taskContext.AddDebugMessage("Skipping PRI generation — existing resources.pri found in input folder");
            }

            // Handle self-contained deployment if requested
            if (selfContained && executablePath != null)
            {
                taskContext.AddDebugMessage($"{UiSymbols.Package} Preparing self-contained Windows App SDK runtime...");

                var winAppSDKDeploymentDir = await PrepareRuntimeForPackagingAsync(stagingDir, dotNetPackageList, taskContext, cancellationToken);

                // Add WindowsAppSDK.manifest to existing manifest
                var resolvedDeploymentDir = Path.Combine(winAppSDKDeploymentDir.FullName, "..", "extracted");
                var windowsAppSDKManifestPath = new FileInfo(Path.Combine(resolvedDeploymentDir, "AppxManifest.xml"));
                await EmbedActivationManifestToExeAsync(executablePath, winAppSDKDeploymentDir, windowsAppSDKManifestPath, dotNetPackageList, taskContext, cancellationToken);
            }

            await CreateMsixPackageFromFolderAsync(stagingDir, outputMsixPath, taskContext, cancellationToken);

            // Handle certificate generation and signing
            if (autoSign)
            {
                await SignMsixPackageAsync(outputFolder, certificatePassword, generateDevCert, installDevCert, finalPackageName, extractedPublisher, outputMsixPath, certificatePath, resolvedManifestPath, taskContext, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to create MSIX package: {ex.Message}", ex);
        }
        finally
        {
            // Clean up the staging directory
            try
            {
                if (stagingDir.Exists)
                {
                    stagingDir.Delete(recursive: true);
                    taskContext.AddDebugMessage($"{UiSymbols.Note} Cleaned up staging directory");
                }
            }
            catch
            {
                taskContext.AddDebugMessage($"{UiSymbols.Warning} Could not clean up staging directory: {stagingDir.FullName}");
            }
        }

        taskContext.AddDebugMessage($"MSIX package created successfully: {outputMsixPath}");
        if (autoSign)
        {
            taskContext.AddDebugMessage("Package has been signed");
        }

        return new CreateMsixPackageResult(outputMsixPath, autoSign);
    }

    private async Task<DotNetPackageListJson?> FetchDotNetPackageListAsync(CancellationToken cancellationToken)
    {
        var cwd = new DirectoryInfo(currentDirectoryProvider.GetCurrentDirectory());
        var csprojFiles = dotNetService.FindCsproj(cwd);
        var csproj = csprojFiles.Count > 0 ? csprojFiles[0] : null;
        if (csproj == null)
        {
            return null;
        }

        return await dotNetService.GetPackageListAsync(csproj, cancellationToken: cancellationToken);
    }

    private async Task SignMsixPackageAsync(DirectoryInfo outputFolder, string certificatePassword, bool generateDevCert, bool installDevCert, string finalPackageName, string? extractedPublisher, FileInfo outputMsixPath, FileInfo? certPath, FileInfo resolvedManifestPath, TaskContext taskContext, CancellationToken cancellationToken)
    {
        if (certPath == null && generateDevCert)
        {
            if (string.IsNullOrWhiteSpace(extractedPublisher))
            {
                throw new InvalidOperationException("Publisher name required for certificate generation. Provide publisher option or ensure it exists in manifest.");
            }

            taskContext.AddDebugMessage($"{UiSymbols.Package} Generating certificate for publisher: {extractedPublisher}");

            certPath = new FileInfo(Path.Combine(outputFolder.FullName, $"{finalPackageName}_cert.pfx"));
            await certificateService.GenerateDevCertificateAsync(extractedPublisher, certPath, taskContext, certificatePassword, cancellationToken: cancellationToken);
        }

        if (certPath == null)
        {
            throw new InvalidOperationException("Certificate path required for signing. Provide certificatePath or set generateDevCert to true.");
        }

        // Validate that the certificate publisher matches the manifest publisher
        taskContext.AddDebugMessage($"{UiSymbols.Note} Validating certificate and manifest publishers match...");

        try
        {
            await CertificateService.ValidatePublisherMatchAsync(certPath, certificatePassword, resolvedManifestPath, cancellationToken);

            taskContext.AddDebugMessage($"{UiSymbols.Check} Certificate and manifest publishers match");
        }
        catch (InvalidOperationException ex)
        {
            // Re-throw with the specific error message format requested
            throw new InvalidOperationException(ex.Message, ex);
        }

        // Install certificate if requested
        if (installDevCert)
        {
            certificateService.InstallCertificate(certPath, certificatePassword, false, taskContext);
        }

        // Sign the package
        await certificateService.SignFileAsync(outputMsixPath, certPath, taskContext, certificatePassword, cancellationToken: cancellationToken);
    }

    private async Task CreateMsixPackageFromFolderAsync(DirectoryInfo inputFolder, FileInfo outputMsixPath, TaskContext taskContext, CancellationToken cancellationToken)
    {
        // Create MSIX package
        var inputPath = LongPathHelper.EnsureExtendedLengthPrefix(Path.TrimEndingDirectorySeparator(inputFolder.FullName));
        var outputPath = LongPathHelper.EnsureExtendedLengthPrefix(outputMsixPath.FullName);
        var makeappxArguments = $@"pack /o /d ""{inputPath}"" /nv /p ""{outputPath}""";

        taskContext.AddDebugMessage("Creating MSIX package...");

        await buildToolsService.RunBuildToolAsync(new MakeAppxTool(), makeappxArguments, taskContext, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Builds the default MSIX output file name from the resolved package name, optional processor
    /// architecture, and optional package version. Extracted as a pure function so the naming
    /// convention (name[_version][_arch].msix) can be verified directly by unit tests, including the
    /// architecture-only and versionless combinations that a real makeappx-backed flow cannot exercise.
    /// </summary>
    internal static string BuildDefaultMsixFileName(string finalPackageName, string? packageArch, string? extractedVersion) => (packageArch, extractedVersion) switch
    {
        (not null, not null) when !string.IsNullOrWhiteSpace(extractedVersion) => $"{finalPackageName}_{extractedVersion}_{packageArch}.msix",
        (null, not null) when !string.IsNullOrWhiteSpace(extractedVersion) => $"{finalPackageName}_{extractedVersion}.msix",
        (not null, _) => $"{finalPackageName}_{packageArch}.msix",
        _ => $"{finalPackageName}.msix"
    };

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
    /// Builds the set of top-level build-artifact directory names that must be excluded from the
    /// staged package (currently the MSBuild-generated 'AppX' output folder). When such a directory
    /// is found the user is warned. Shared by the single-package (<see cref="CreateMsixPackageAsync"/>)
    /// and per-slice bundle (<c>PackSingleFolderToMsixAsync</c>) staging paths so both exclude
    /// build artifacts consistently.
    /// </summary>
    private static HashSet<string> BuildStagingExclusions(DirectoryInfo inputFolder, TaskContext taskContext)
    {
        var excludedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var appxDir = new DirectoryInfo(Path.Combine(inputFolder.FullName, "AppX"));
        if (appxDir.Exists)
        {
            excludedDirectories.Add("AppX");
            taskContext.AddStatusMessage($"{UiSymbols.Warning} Found 'AppX' directory in input folder. It will be excluded from the package.");
        }

        return excludedDirectories;
    }

    /// <summary>
    /// Recursively copies all files and subdirectories from source to destination,
    /// skipping any top-level directories whose names appear in <paramref name="excludedDirectories"/>.
    /// </summary>
    private static void CopyDirectoryRecursive(DirectoryInfo source, DirectoryInfo destination, HashSet<string>? excludedDirectories = null)
    {
        destination.Create();

        foreach (var file in source.EnumerateFiles())
        {
            file.CopyTo(Path.Combine(destination.FullName, file.Name), overwrite: true);
        }

        foreach (var subDir in source.EnumerateDirectories())
        {
            if (excludedDirectories != null && excludedDirectories.Contains(subDir.Name))
            {
                continue;
            }

            var destSubDir = new DirectoryInfo(Path.Combine(destination.FullName, subDir.Name));
            CopyDirectoryRecursive(subDir, destSubDir);
        }
    }

    /// <summary>
    /// Copies files referenced in the manifest that are missing from the staging directory.
    /// Resolves file paths relative to the manifest directory first, then the input folder.
    /// This ensures non-image referenced files (e.g., manifest.json) are included in the package.
    /// </summary>
    private static void CopyManifestReferencedFiles(
        HashSet<string> referencedFiles,
        DirectoryInfo manifestDir,
        DirectoryInfo inputFolder,
        DirectoryInfo stagingDir,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        if (referencedFiles.Count == 0)
        {
            return;
        }

        int copied = 0;

        foreach (var relativePath in referencedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stagingPath = Path.GetFullPath(Path.Combine(stagingDir.FullName, relativePath));
            var stagingRoot = Path.GetFullPath(stagingDir.FullName).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            // Verify the destination stays within the staging directory
            if (!stagingPath.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
            {
                taskContext.AddDebugMessage($"{UiSymbols.Warning} Skipped manifest-referenced file with path escape: {relativePath}");
                continue;
            }

            if (File.Exists(stagingPath))
            {
                continue;
            }

            // Try manifest directory first, then input folder
            FileInfo? sourceFile = null;
            var manifestRelative = new FileInfo(Path.Combine(manifestDir.FullName, relativePath));
            var inputRelative = new FileInfo(Path.Combine(inputFolder.FullName, relativePath));

            if (manifestRelative.Exists)
            {
                sourceFile = manifestRelative;
            }
            else if (inputRelative.Exists)
            {
                sourceFile = inputRelative;
            }

            if (sourceFile == null)
            {
                continue;
            }

            // Security: verify the resolved source path stays within the allowed roots.
            // This prevents symlinks/junctions from escaping the project directory.
            var resolvedSourcePath = Path.GetFullPath(sourceFile.FullName);
            var manifestRoot = Path.GetFullPath(manifestDir.FullName).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var inputRoot = Path.GetFullPath(inputFolder.FullName).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            if (!resolvedSourcePath.StartsWith(manifestRoot, StringComparison.OrdinalIgnoreCase) &&
                !resolvedSourcePath.StartsWith(inputRoot, StringComparison.OrdinalIgnoreCase))
            {
                taskContext.AddDebugMessage($"{UiSymbols.Warning} Skipped manifest-referenced file outside project root: {relativePath}");
                continue;
            }

            // Reject reparse points (symlinks/junctions) that could redirect to arbitrary locations
            var sourceAttributes = File.GetAttributes(sourceFile.FullName);
            if (sourceAttributes.HasFlag(FileAttributes.ReparsePoint))
            {
                taskContext.AddDebugMessage($"{UiSymbols.Warning} Skipped manifest-referenced symlink/junction: {relativePath}");
                continue;
            }

            var destDir = Path.GetDirectoryName(stagingPath);
            if (destDir != null)
            {
                Directory.CreateDirectory(destDir);
            }

            sourceFile.CopyTo(stagingPath, overwrite: true);
            copied++;
            taskContext.AddDebugMessage($"{UiSymbols.Files} Copied manifest-referenced file: {relativePath}");
        }

        if (copied > 0)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Note} Manifest-referenced files: {copied} copied to staging");
        }
    }

    /// <summary>
    /// Searches for a manifest file in the start directory and walks up parent directories until one is found.
    /// </summary>
    /// <param name="startDirectory">The directory to start searching from. If null, uses current directory.</param>
    /// <returns>Path to the first manifest found (preferring Package.appxmanifest over appxmanifest.xml), or null if not found.</returns>
    public static FileInfo? FindProjectManifest(ICurrentDirectoryProvider currentDirectoryProvider, DirectoryInfo? startDirectory = null)
    {
        var directory = startDirectory ?? currentDirectoryProvider.GetCurrentDirectoryInfo();

        while (directory != null)
        {
            var found = FindManifestInDirectory(directory);
            if (found != null)
            {
                return found;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// Helper executables that runtimes drop next to the application executable in a build
    /// output. None of them is ever an app entry point, so they are excluded when
    /// auto-detecting the application executable.
    /// </summary>
    /// <remarks>
    /// <c>createdump.exe</c> and <c>apphost.exe</c> come from a .NET self-contained publish.
    /// <c>RestartAgent.exe</c> and <c>DeploymentAgent.exe</c> are the only two executables in
    /// the Windows App SDK framework payload, which <c>WindowsAppSDKSelfContained=true</c>
    /// extracts into the output root — so without them here, auto-detection is ambiguous for
    /// every Windows App SDK self-contained app (issue #790).
    /// </remarks>
    private static readonly HashSet<string> RuntimeToolExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "createdump.exe",
        "apphost.exe",
        "RestartAgent.exe",
        "DeploymentAgent.exe"
    };

    /// <summary>
    /// Returns true if the given file name belongs to a runtime helper that should be
    /// excluded when auto-detecting the application executable.
    /// </summary>
    internal static bool IsRuntimeToolExecutable(string fileName)
    {
        return RuntimeToolExecutables.Contains(fileName);
    }

    /// <summary>
    /// Checks a single directory for a manifest file (Package.appxmanifest or appxmanifest.xml).
    /// </summary>
    internal static FileInfo? FindManifestInDirectory(DirectoryInfo directory)
    {
        var packageManifest = new FileInfo(Path.Combine(directory.FullName, "Package.appxmanifest"));
        if (packageManifest.Exists)
        {
            return packageManifest;
        }

        var appxManifest = new FileInfo(Path.Combine(directory.FullName, "appxmanifest.xml"));
        if (appxManifest.Exists)
        {
            return appxManifest;
        }

        return null;
    }

    /// <summary>
    /// Updates the manifest identity, application ID, and executable path for sparse packaging
    /// </summary>
    private async Task<(string Content, string? DetectedArchitecture)> UpdateAppxManifestContentAsync(
        string originalAppxManifestContent,
        MsixIdentityResult? identity,
        string? entryPointPath,
        string? exePath,
        bool sparse,
        bool selfContained,
        DotNetPackageListJson? dotNetPackageList,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        var doc = AppxManifestDocument.Parse(originalAppxManifestContent);

        if (identity != null)
        {
            doc.IdentityName = identity.PackageName;
            doc.ApplicationId = identity.ApplicationId;
        }

        if (entryPointPath != null)
        {
            var entryPointDir = Path.GetDirectoryName(entryPointPath);
            var workingDir = string.IsNullOrEmpty(entryPointDir) ? currentDirectoryProvider.GetCurrentDirectory() : entryPointDir;
            string relativeExecutablePath;

            try
            {
                relativeExecutablePath = Path.GetRelativePath(workingDir, entryPointPath);
                relativeExecutablePath = relativeExecutablePath.Replace('\\', '/');
            }
            catch
            {
                relativeExecutablePath = Path.GetFileName(entryPointPath);
            }

            doc.ApplicationExecutable = relativeExecutablePath;
        }

        // Determine whether the app entry point is an .exe. For folder packing no explicit
        // entryPointPath is supplied, so fall back to the manifest's own <Application Executable>
        // — otherwise sparse corrections (win32App/mediumIL) would never apply to folder inputs.
        var executableForKind = entryPointPath ?? doc.ApplicationExecutable;
        bool isExe = Path.HasExtension(executableForKind) && string.Equals(Path.GetExtension(executableForKind), ".exe", StringComparison.OrdinalIgnoreCase);

        if (sparse)
        {
            // Add required namespaces for sparse packaging
            doc.EnsureNamespace("uap10", AppxManifestDocument.Uap10Ns);
            doc.EnsureNamespace("desktop6", AppxManifestDocument.Desktop6Ns);

            // Add sparse package properties
            var properties = doc.Document.Root?.Element(AppxManifestDocument.DefaultNs + "Properties");
            if (properties != null && properties.Element(AppxManifestDocument.Uap10Ns + "AllowExternalContent") == null)
            {
                properties.Add(new XElement(AppxManifestDocument.Uap10Ns + "AllowExternalContent", "true"));
                properties.Add(new XElement(AppxManifestDocument.Desktop6Ns + "RegistryWriteVirtualization", "disabled"));
            }

            // Ensure Application has sparse packaging attributes. For an EXE we always force
            // TrustLevel=mediumIL and RuntimeBehavior=win32App: a pre-existing (and for sparse
            // Win32 apps, incorrect) value such as RuntimeBehavior="packagedClassicApp" must be
            // corrected, so we set both unconditionally rather than only when TrustLevel is absent.
            var app = doc.GetFirstApplicationElement();
            if (app != null && isExe)
            {
                app.SetAttributeValue(AppxManifestDocument.Uap10Ns + "TrustLevel", "mediumIL");
                app.SetAttributeValue(AppxManifestDocument.Uap10Ns + "RuntimeBehavior", "win32App");
            }

            // Remove EntryPoint if present (not needed for sparse packages)
            doc.ApplicationEntryPoint = null;

            // Add AppListEntry="none" to VisualElements if not present
            var ve = doc.GetVisualElements();
            if (ve != null && ve.Attribute("AppListEntry") == null)
            {
                ve.SetAttributeValue("AppListEntry", "none");
            }

            // Add sparse-specific capabilities if not present
            var capsElement = doc.GetCapabilitiesElement();
            bool hasUnvirtualizedResources = capsElement?.Elements()
                .Any(e => string.Equals(e.Attribute("Name")?.Value, "unvirtualizedResources", StringComparison.OrdinalIgnoreCase)) == true;
            if (!hasUnvirtualizedResources)
            {
                doc.EnsureCapability("unvirtualizedResources", AppxManifestDocument.RescapNs);
                doc.EnsureCapability("allowElevation", AppxManifestDocument.RescapNs);
            }

            // Raise any TargetDeviceFamily MinVersion below the AllowExternalContent floor
            // (10.0.19041.0). A folder created from an older sparse template keeps 10.0.18362.0;
            // MakeAppx /nv would pack (and sign) it, but deployment rejects a sparse package below
            // 19041. Apply the same floor the manifest-file path enforces.
            foreach (var correction in RaiseSparseTargetDeviceFamilyMinVersion(doc))
            {
                taskContext.AddStatusMessage($"{UiSymbols.Info} Normalized sparse manifest: {correction}");
            }
        }

        // Convert to string for remaining string-based operations
        var modifiedContent = doc.ToXml();

        // Update or insert Windows App SDK dependency (skip for self-contained packages)
        if (!selfContained && (entryPointPath == null || isExe))
        {
            modifiedContent = await UpdateWindowsAppSdkDependencyAsync(modifiedContent, dotNetPackageList, taskContext, cancellationToken);
        }

        // Add InProcessServer entries for third-party WinRT components (e.g., Win2D, WebView2)
        // In self-contained mode, activation entries go in the SxS manifest embedded in the exe,
        // so we skip them here to avoid duplication.
        if (!selfContained)
        {
            modifiedContent = await AddThirdPartyWinRTExtensionsToAppxManifestAsync(modifiedContent, dotNetPackageList, taskContext, cancellationToken);
        }

        // Stamp build metadata with CLI version
        modifiedContent = AddBuildMetadata(modifiedContent);

        // Auto-detect ProcessorArchitecture from the executable PE header if not already set.
        // Without this, ARM64 Windows resolves framework dependencies to ARM64 DLLs even for x64 apps.
        string? detectedArch = null;
        if (exePath != null)
        {
            (modifiedContent, detectedArch) = AutoDetectProcessorArchitecture(modifiedContent, exePath, taskContext);
        }

        return (modifiedContent, detectedArch);
    }

    /// <summary>
    /// Adds or updates build:Metadata in the manifest with the CLI tool name and version.
    /// Inserts the build namespace and IgnorableNamespaces entry if not already present.
    /// </summary>
    internal static string AddBuildMetadata(string manifestContent)
    {
        var version = VersionHelper.GetVersionString();

        var doc = AppxManifestDocument.Parse(manifestContent);

        doc.EnsureNamespace("build", AppxManifestDocument.BuildNs);
        doc.AddIgnorableNamespace("build");
        doc.SetBuildMetadata("Microsoft.WinAppCli", version);

        return doc.ToXml();
    }
}
