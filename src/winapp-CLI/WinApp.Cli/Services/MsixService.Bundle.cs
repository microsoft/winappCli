// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal partial class MsixService
{
    public async Task<CreateMsixBundleResult> CreateMsixBundleAsync(
        DirectoryInfo[] inputFolders,
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
        // Stage intermediates under a short-path temp dir to avoid MAX_PATH issues
        var guidSuffix = Guid.NewGuid().ToString("N")[..8];
        var bundleTempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "winapp", $"b-{guidSuffix}"));
        bundleTempDir.Create();

        try
        {
            // --- Step 1: Detect architecture per folder ---
            var sliceInfos = new List<(DirectoryInfo Folder, string Arch, string? ExePath)>();

            for (int i = 0; i < inputFolders.Length; i++)
            {
                var folder = inputFolders[i];
                var exePath = ResolveExecutableForFolder(folder, executable, manifestPath, taskContext);

                if (exePath == null)
                {
                    throw new InvalidOperationException(
                        $"Cannot detect architecture for '{folder.FullName}': no executable found. " +
                        (executable != null ? $"The specified --executable '{executable}' was not found in this folder." : "Provide --executable or ensure the manifest references an executable that exists in each input folder."));
                }

                var detectedArch = PeHelper.DetectPeArchitecture(exePath);
                if (detectedArch == null)
                {
                    throw new InvalidOperationException(
                        $"Cannot detect architecture from '{exePath}': unrecognized PE format or not a valid executable.");
                }

                taskContext.AddDebugMessage($"[{i + 1}/{inputFolders.Length}] {folder.Name} → {detectedArch}");
                sliceInfos.Add((folder, detectedArch, exePath));
            }

            var detectedArchitectures = sliceInfos.Select(s => s.Arch).ToList();

            // --- Step 2: Resolve manifest per folder and finalize ---
            var finalizedManifests = new List<(string Content, AppxManifestDocument Doc, FileInfo ResolvedManifestPath)>();
            var dotNetPackageList = await FetchDotNetPackageListAsync(cancellationToken);

            for (int i = 0; i < sliceInfos.Count; i++)
            {
                var (folder, arch, exePath) = sliceInfos[i];

                // Resolve manifest for this slice
                string manifestContent;
                FileInfo resolvedManifestPath;

                if (manifestPath != null)
                {
                    resolvedManifestPath = manifestPath;
                }
                else
                {
                    var resolved = FindManifestInDirectory(folder)
                        ?? FindManifestInDirectory(new DirectoryInfo(currentDirectoryProvider.GetCurrentDirectory()));

                    if (resolved == null)
                    {
                        throw new FileNotFoundException(
                            $"Manifest file not found for '{folder.FullName}'. Searched for Package.appxmanifest, then appxmanifest.xml in: input folder, current directory. Use --manifest to specify explicitly.");
                    }
                    resolvedManifestPath = resolved;
                }

                manifestContent = await File.ReadAllTextAsync(resolvedManifestPath.FullName, Encoding.UTF8, cancellationToken);
                manifestContent = ResolveManifestPlaceholders(manifestContent, executable, folder, taskContext);
                manifestContent = await ResolveResourceLanguageXGenerateAsync(manifestContent, folder, taskContext, cancellationToken);

                // Update manifest (deps, build metadata, arch detection)
                (manifestContent, _) = await UpdateAppxManifestContentAsync(
                    manifestContent, null, null, exePath,
                    sparse: false, selfContained: selfContained, dotNetPackageList,
                    taskContext, cancellationToken);

                // Force-stamp the detected architecture (overrides whatever UpdateAppxManifestContentAsync detected)
                var doc = AppxManifestDocument.Parse(manifestContent);
                doc.IdentityProcessorArchitecture = arch;
                manifestContent = doc.ToXml();
                doc = AppxManifestDocument.Parse(manifestContent);

                finalizedManifests.Add((manifestContent, doc, resolvedManifestPath));
            }

            // --- Step 3: Cross-slice validation (BEFORE packing) ---
            var manifests = finalizedManifests.Select(m => m.Doc).ToList();
            var folders = sliceInfos.Select(s => s.Folder).ToList();

            var validationErrors = bundleValidationService.Validate(manifests, detectedArchitectures, folders);
            if (validationErrors.Count > 0)
            {
                var errorBuilder = new StringBuilder();
                errorBuilder.AppendLine("Bundle validation failed. The following inconsistencies were found across slices:");
                errorBuilder.AppendLine();
                foreach (var error in validationErrors)
                {
                    errorBuilder.AppendLine($"  {error.Field}: {error.Message}");
                    foreach (var sliceValue in error.SliceValues)
                    {
                        errorBuilder.AppendLine($"    • {sliceValue}");
                    }
                    errorBuilder.AppendLine();
                }
                throw new InvalidOperationException(errorBuilder.ToString());
            }

            taskContext.AddDebugMessage("Cross-slice validation passed.");

            // --- Step 4: Resolve identity for output naming ---
            var referenceDoc = finalizedManifests[0].Doc;
            var finalPackageName = packageName ?? referenceDoc.IdentityName ?? "Package";
            finalPackageName = ManifestService.CleanPackageName(finalPackageName);
            var extractedVersion = referenceDoc.IdentityVersion;
            var extractedPublisher = publisher ?? referenceDoc.IdentityPublisher;

            // Guard against malformed or malicious Identity/@Version values before they reach the
            // file system or the makeappx command line. Parse once into a canonical MsixVersion and
            // reuse it for both path construction and the makeappx /bv argument.
            MsixVersion? bundleVersion = null;
            if (!MsixVersion.TryParse(extractedVersion, out var parsedVersion))
            {
                throw new InvalidOperationException(
                    $"Identity/@Version '{extractedVersion}' is not a canonical four-part MSIX version (e.g. 1.2.3.4).");
            }

            bundleVersion = parsedVersion;

            // Compose output filename: <Name>_<Version>_<arch1>_<arch2>.msixbundle (arches sorted)
            var sortedArches = detectedArchitectures.OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
            var archSuffix = string.Join("_", sortedArches);

            // Keep using the `extractedVersion` string for the filename to preserve the existing naming convention.
            var defaultBundleFileName = $"{finalPackageName}_{extractedVersion}_{archSuffix}.msixbundle";

            FileInfo outputBundlePath;
            DirectoryInfo outputFolder;
            if (outputPath == null)
            {
                outputFolder = currentDirectoryProvider.GetCurrentDirectoryInfo();
                outputBundlePath = new FileInfo(Path.Combine(outputFolder.FullName, defaultBundleFileName));
            }
            else
            {
                var ext = Path.GetExtension(outputPath.Name);
                if (string.Equals(ext, ".msixbundle", StringComparison.OrdinalIgnoreCase))
                {
                    outputBundlePath = new FileInfo(outputPath.FullName);
                    outputFolder = outputBundlePath.Directory!;
                }
                else
                {
                    // Treat as directory (preserve existing semantics)
                    outputFolder = new DirectoryInfo(outputPath.FullName);
                    outputBundlePath = new FileInfo(Path.Combine(outputPath.FullName, defaultBundleFileName));
                }
            }

            if (!outputFolder.Exists)
            {
                outputFolder.Create();
            }

            // --- Step 4b: Validate self-contained + neutral combination ---
            if (selfContained)
            {
                var neutralSlices = sliceInfos.Where(s => string.Equals(s.Arch, "neutral", StringComparison.OrdinalIgnoreCase)).ToList();
                if (neutralSlices.Count > 0)
                {
                    var neutralFolders = string.Join(", ", neutralSlices.Select(s => s.Folder.Name));
                    throw new InvalidOperationException(
                        $"Cannot use --self-contained with architecture-neutral slices ({neutralFolders}). " +
                        "Self-contained runtime requires a specific architecture (x64, arm64, x86). " +
                        "Either remove --self-contained or ensure all input folders contain architecture-specific binaries.");
                }
            }

            // --- Step 5: Pack each slice serially ---
            var intermediateFiles = new List<FileInfo>();
            var sliceResults = new List<BundleSliceInfo>();

            for (int i = 0; i < sliceInfos.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (folder, arch, exePath) = sliceInfos[i];
                var (manifestContent, _, sliceManifestPath) = finalizedManifests[i];

                taskContext.AddStatusMessage($"[{i + 1}/{sliceInfos.Count}] Packing {arch}...");

                // Each intermediate .msix goes into the bundle temp dir
                var intermediateMsixName = $"{finalPackageName}_{arch}.msix";
                var intermediateMsixPath = new FileInfo(Path.Combine(bundleTempDir.FullName, intermediateMsixName));

                await PackSingleFolderToMsixAsync(
                    folder, manifestContent, sliceManifestPath, intermediateMsixPath,
                    taskContext, selfContained, executable, arch,
                    skipPri, dotNetPackageList, cancellationToken);

                intermediateFiles.Add(intermediateMsixPath);
                sliceResults.Add(new BundleSliceInfo(folder, arch, intermediateMsixPath));
            }

            // --- Step 6: Create the bundle ---
            taskContext.AddStatusMessage("Creating bundle...");
            // Pass the validated, consistent slice Identity/@Version through to makeappx's /bv
            // switch so Bundle.Identity/@Version matches the source manifests instead of
            // makeappx's default timestamp-derived version. See microsoft/winappCli#677.
            await bundleService.CreateBundleAsync(intermediateFiles, outputBundlePath, taskContext, bundleVersion, cancellationToken);

            // --- Step 7: Sign if requested ---
            if (autoSign)
            {
                var resolvedManifest = manifestPath ?? FindManifestInDirectory(inputFolders[0])
                    ?? FindManifestInDirectory(new DirectoryInfo(currentDirectoryProvider.GetCurrentDirectory()))
                    ?? throw new FileNotFoundException("Cannot find manifest for publisher validation during signing.");

                await SignMsixPackageAsync(outputFolder, certificatePassword, generateDevCert, installDevCert,
                    finalPackageName, extractedPublisher, outputBundlePath, certificatePath,
                    resolvedManifest, taskContext, cancellationToken);
            }

            return new CreateMsixBundleResult(outputBundlePath, autoSign, sliceResults);
        }
        finally
        {
            // Clean up bundle temp directory
            try
            {
                if (bundleTempDir.Exists)
                {
                    bundleTempDir.Delete(recursive: true);
                    taskContext.AddDebugMessage($"Cleaned up bundle temp directory: {bundleTempDir.FullName}");
                }
            }
            catch
            {
                taskContext.AddDebugMessage($"{UiSymbols.Warning} Could not clean up bundle temp directory: {bundleTempDir.FullName}");
            }
        }
    }

    /// <summary>
    /// Resolves the executable path for arch detection within a given folder.
    /// If --executable is specified, uses that relative path. Otherwise, reads
    /// the manifest's Application/@Executable for the folder.
    /// </summary>
    private string? ResolveExecutableForFolder(DirectoryInfo folder, string? executableOption, FileInfo? manifestPath, TaskContext taskContext)
    {
        if (executableOption != null)
        {
            var fullPath = ResolveAndValidatePathUnderFolder(folder, executableOption);
            return fullPath != null && File.Exists(fullPath) ? fullPath : null;
        }

        // Auto-detect: prefer explicit --manifest, then folder-local, then cwd
        var manifest = manifestPath
            ?? FindManifestInDirectory(folder)
            ?? FindManifestInDirectory(new DirectoryInfo(currentDirectoryProvider.GetCurrentDirectory()));

        if (manifest != null)
        {
            try
            {
                var doc = AppxManifestDocument.Load(manifest.FullName);
                var appExe = doc.ApplicationExecutable;
                if (appExe != null)
                {
                    var fullPath = ResolveAndValidatePathUnderFolder(folder, appExe);
                    if (fullPath != null && File.Exists(fullPath))
                    {
                        return fullPath;
                    }
                }
            }
            catch
            {
                // Fall through to EXE scan
            }
        }

        // Last resort: find first .exe in the folder root (excluding runtime tools)
        var firstExe = folder.EnumerateFiles("*.exe", SearchOption.TopDirectoryOnly)
            .Where(f => !IsRuntimeToolExecutable(f.Name))
            .FirstOrDefault();
        if (firstExe != null)
        {
            taskContext.AddDebugMessage($"Auto-detected executable: {firstExe.Name} in {folder.Name}");
            return firstExe.FullName;
        }

        return null;
    }

    /// <summary>
    /// Resolves a relative path under a base folder, rejecting rooted paths and path traversal.
    /// Returns the full path if valid and contained within the folder, or null otherwise.
    /// </summary>
    private static string? ResolveAndValidatePathUnderFolder(DirectoryInfo folder, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            return null;
        }

        if (relativePath.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(Path.Combine(folder.FullName, relativePath));
        var folderPrefix = folder.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return fullPath;
    }

    /// <summary>
    /// Packs a single folder into an unsigned .msix file. This is the core packing logic
    /// extracted for reuse by both single-package and bundle workflows.
    /// Does NOT sign, does NOT resolve the manifest, does NOT determine output naming.
    /// </summary>
    private async Task PackSingleFolderToMsixAsync(
        DirectoryInfo inputFolder,
        string manifestContent,
        FileInfo resolvedManifestPath,
        FileInfo outputMsixPath,
        TaskContext taskContext,
        bool selfContained,
        string? executable,
        string targetArch,
        bool skipPri,
        DotNetPackageListJson? dotNetPackageList,
        CancellationToken cancellationToken)
    {
        // Create staging directory
        var stagingDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"winapp-package-{Guid.NewGuid():N}"));
        stagingDir.Create();

        try
        {
            var manifestDoc = AppxManifestDocument.Parse(manifestContent);

            // Check for MSBuild-generated manifest and recipe
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
                taskContext.AddDebugMessage($"Using appxrecipe for staging: {recipeFile.Name}");
                await CopyFilesFromRecipeAsync(recipeFile, stagingDir, taskContext, LayoutReconciliation.None, cancellationToken);
            }
            else
            {
                var excludedDirectories = BuildStagingExclusions(inputFolder, taskContext);
                CopyDirectoryRecursive(inputFolder, stagingDir, excludedDirectories);
            }

            // Write finalized manifest into staging
            var updatedManifestPath = Path.Combine(stagingDir.FullName, "appxmanifest.xml");
            await File.WriteAllTextAsync(updatedManifestPath, manifestContent, Encoding.UTF8, cancellationToken);

            // Copy external manifest-referenced assets if manifest lives outside the input folder
            var manifestIsOutsideInputFolder = !inputFolder.FullName.TrimEnd(Path.DirectorySeparatorChar)
                .Equals(resolvedManifestPath.Directory!.FullName.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

            var allManifestReferences = ManifestFileReferenceHelper.ExtractAllFileReferencesFromManifest(manifestContent);

            if (manifestIsOutsideInputFolder)
            {
                var externalAssets = MrtAssetHelper.GetExpandedManifestReferencedFiles(resolvedManifestPath, taskContext);
                MrtAssetHelper.CopyAllAssets(externalAssets, stagingDir, taskContext);
            }

            CopyManifestReferencedFiles(allManifestReferences, resolvedManifestPath.Directory!, inputFolder, stagingDir, taskContext, cancellationToken);

            // PRI generation
            var existingPri = new FileInfo(Path.Combine(stagingDir.FullName, "resources.pri"));
            if (!skipPri && !existingPri.Exists)
            {
                taskContext.AddDebugMessage("Generating PRI files...");
                var stagingManifest = new FileInfo(Path.Combine(stagingDir.FullName, "appxmanifest.xml"));
                var priExpandedFiles = MrtAssetHelper.GetExpandedManifestReferencedFiles(stagingManifest, taskContext);
                var priResourceCandidates = priExpandedFiles.Select(file => file.RelativePath);
                await priService.CreatePriConfigAsync(stagingDir, taskContext, precomputedPriResourceCandidates: priResourceCandidates, cancellationToken: cancellationToken);
                await priService.GeneratePriFileAsync(stagingDir, taskContext, cancellationToken: cancellationToken);
            }

            // Self-contained runtime (arch-aware)
            if (selfContained)
            {
                var applicationExecutable = manifestDoc.ApplicationExecutable;
                var validatedExePath = applicationExecutable != null
                    ? ResolveAndValidatePathUnderFolder(stagingDir, applicationExecutable)
                    : null;
                FileInfo? executablePath = validatedExePath != null ? new FileInfo(validatedExePath) : null;

                if (executablePath != null)
                {
                    taskContext.AddDebugMessage($"Preparing self-contained runtime for {targetArch}...");
                    var winAppSDKDeploymentDir = await PrepareRuntimeForPackagingAsync(stagingDir, dotNetPackageList, taskContext, cancellationToken, overrideArch: targetArch);
                    var resolvedDeploymentDir = Path.Combine(winAppSDKDeploymentDir.FullName, "..", "extracted");
                    var windowsAppSDKManifestPath = new FileInfo(Path.Combine(resolvedDeploymentDir, "AppxManifest.xml"));
                    await EmbedActivationManifestToExeAsync(executablePath, winAppSDKDeploymentDir, windowsAppSDKManifestPath, dotNetPackageList, taskContext, cancellationToken);
                }
            }

            // Pack
            await CreateMsixPackageFromFolderAsync(stagingDir, outputMsixPath, taskContext, cancellationToken);
        }
        finally
        {
            try
            {
                if (stagingDir.Exists)
                {
                    stagingDir.Delete(recursive: true);
                }
            }
            catch
            {
                taskContext.AddDebugMessage($"{UiSymbols.Warning} Could not clean up staging directory: {stagingDir.FullName}");
            }
        }
    }
}
