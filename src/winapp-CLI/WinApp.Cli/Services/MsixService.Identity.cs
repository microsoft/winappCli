// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security;
using System.Collections.Concurrent;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Tools;

namespace WinApp.Cli.Services;

internal partial class MsixService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RecipeLayoutLocks =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Namespace of the SxS assembly manifest root element.
    /// </summary>
    private static readonly XNamespace AsmV1Ns = "urn:schemas-microsoft-com:asm.v1";

    /// <summary>
    /// Namespace of the &lt;msix&gt; package-identity element embedded in a fusion manifest.
    /// </summary>
    private static readonly XNamespace MsixV1Ns = "urn:schemas-microsoft-com:msix.v1";

    public async Task<MsixIdentityResult> AddSparseIdentityAsync(string? entryPointPath, FileInfo appxManifestPath, bool noInstall, bool keepIdentity, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        // Validate inputs
        if (!appxManifestPath.Exists)
        {
            throw new FileNotFoundException($"AppX manifest not found at: {appxManifestPath}. You can generate one using 'winapp manifest generate'.");
        }

        if (!devModeService.IsEnabled() && noInstall == false)
        {
            throw new InvalidOperationException("Developer Mode is not enabled on this machine. Please enable Developer Mode and try again.");
        }

        if (entryPointPath == null)
        {
            var manifestContent = await File.ReadAllTextAsync(appxManifestPath.FullName, Encoding.UTF8, cancellationToken);

            // Parse once to extract the executable path
            var doc = AppxManifestDocument.Parse(manifestContent);

            if (PlaceholderHelper.ContainsPlaceholders(manifestContent))
            {
                // Without an explicit entrypoint, we can't resolve $targetnametoken$ in the executable
                if (doc.ApplicationExecutable != null && PlaceholderHelper.ContainsPlaceholders(doc.ApplicationExecutable))
                {
                    throw new InvalidOperationException(
                        "The manifest contains a placeholder for the executable. " +
                        "Provide the entrypoint argument to specify the executable path.");
                }

                // Resolve built-in tokens (e.g. $targetentrypoint$) in memory — the executable
                // attribute itself has no placeholders, so its value from the initial parse is valid.
                manifestContent = PlaceholderHelper.ReplacePlaceholders(manifestContent);
            }

            entryPointPath = doc.ApplicationExecutable ?? entryPointPath;
        }

        // Validate inputs
        if (!File.Exists(entryPointPath))
        {
            throw new FileNotFoundException($"EntryPoint/Executable not found at: {entryPointPath}");
        }

        taskContext.AddDebugMessage($"Processing entryPoint/executable: {entryPointPath}");
        taskContext.AddDebugMessage($"Using AppX manifest: {appxManifestPath}");

        // Generate sparse package structure
        // Fetch dotnet package list once for all downstream operations
        var dotNetPackageList = await FetchDotNetPackageListAsync(cancellationToken);

        var (debugManifestPath, debugIdentity) = await GenerateSparsePackageStructureAsync(
            appxManifestPath,
            entryPointPath,
            keepIdentity,
            dotNetPackageList,
            taskContext,
            cancellationToken);

        // Update executable with debug identity
        if (Path.HasExtension(entryPointPath) && string.Equals(Path.GetExtension(entryPointPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            var exePath = new FileInfo(entryPointPath);
            await EmbedMsixIdentityToExeAsync(exePath, debugIdentity, taskContext, cancellationToken);
        }

        if (noInstall)
        {
            taskContext.AddDebugMessage("Skipping package installation as per --no-install option.");
        }
        else
        {
            // Register the debug appxmanifest
            var entryPointDir = Path.GetDirectoryName(entryPointPath);
            var externalLocation = new DirectoryInfo(string.IsNullOrEmpty(entryPointDir) ? currentDirectoryProvider.GetCurrentDirectory() : entryPointDir);

            // Unregister any existing package first (preserving app data by default)
            await UnregisterExistingPackageAsync(
                debugIdentity.PackageName,
                taskContext,
                debugIdentity.Publisher,
                cancellationToken: cancellationToken);

            // Register the new debug manifest with external location
            await RegisterSparsePackageAsync(debugManifestPath, externalLocation, taskContext, cancellationToken);
        }

        return new MsixIdentityResult(debugIdentity.PackageName, debugIdentity.Publisher, debugIdentity.ApplicationId);
    }

    public Task<MsixIdentityResult> AddLooseLayoutIdentityAsync(FileInfo appxManifestPath, DirectoryInfo inputDirectory, DirectoryInfo outputAppXDirectory, TaskContext taskContext, LayoutReconciliation reconciliation = LayoutReconciliation.Additive, bool clean = false, string? executable = null, string? runtimeArch = null, FileInfo? projectFile = null, string? framework = null, bool noRestore = false, bool selfContained = false, bool ensureExecutionAlias = false, PackageGraphSource? packageGraph = null, CancellationToken cancellationToken = default)
        => BuildLooseLayoutAsync(appxManifestPath, inputDirectory, outputAppXDirectory, taskContext, LooseLayoutOutcome.Registered, reconciliation, clean, executable, runtimeArch, projectFile, framework, noRestore, selfContained, ensureExecutionAlias, packageGraph, cancellationToken);

    /// <inheritdoc/>
    public Task<MsixIdentityResult> MaterializeLooseLayoutAsync(FileInfo appxManifestPath, DirectoryInfo inputDirectory, DirectoryInfo outputAppXDirectory, TaskContext taskContext, LayoutReconciliation reconciliation, string? executable = null, FileInfo? projectFile = null, string? framework = null, bool noRestore = false, bool selfContained = false, bool ensureExecutionAlias = false, PackageGraphSource? packageGraph = null, CancellationToken cancellationToken = default)
        => BuildLooseLayoutAsync(appxManifestPath, inputDirectory, outputAppXDirectory, taskContext, LooseLayoutOutcome.Materialized, reconciliation, clean: false, executable, runtimeArch: null, projectFile, framework, noRestore, selfContained, ensureExecutionAlias, packageGraph, cancellationToken);

    /// <summary>How far <see cref="BuildLooseLayoutAsync"/> takes a loose layout.</summary>
    private enum LooseLayoutOutcome
    {
        /// <summary>Materialize, provision the runtime for this machine, and register the package.</summary>
        Registered,

        /// <summary>Materialize only. Nothing about the host machine is inspected or changed.</summary>
        Materialized,
    }

    /// <summary>
    /// Produces the loose layout, then — for <see cref="LooseLayoutOutcome.Registered"/> — provisions
    /// the Windows App Runtime and registers the package on this machine.
    /// </summary>
    /// <remarks>
    /// The split exists because those last two steps are the ones that must not happen when the app
    /// is going somewhere else. An execution target needs the materialized layout and the identity
    /// parsed out of it, and nothing more: installing a runtime on the host for an app that will run
    /// in a guest would change the developer's machine for no reason, and registering the package
    /// here would mean a <c>--on sandbox</c> run silently deployed to the host as well.
    /// <para>
    /// Materialization itself is byte-for-byte the same work in both modes, deliberately: a layout
    /// that behaves differently depending on where it is going would make a guest failure impossible
    /// to reproduce locally.
    /// </para>
    /// </remarks>
    private async Task<MsixIdentityResult> BuildLooseLayoutAsync(FileInfo appxManifestPath, DirectoryInfo inputDirectory, DirectoryInfo outputAppXDirectory, TaskContext taskContext, LooseLayoutOutcome outcome, LayoutReconciliation reconciliation, bool clean, string? executable, string? runtimeArch, FileInfo? projectFile, string? framework, bool noRestore, bool selfContained, bool ensureExecutionAlias, PackageGraphSource? packageGraph, CancellationToken cancellationToken)
    {
        // Validate inputs
        if (!appxManifestPath.Exists)
        {
            throw new FileNotFoundException($"AppX manifest not found at: {appxManifestPath}. You can generate one using 'winapp manifest generate'.");
        }

        if (!devModeService.IsEnabled() && outcome == LooseLayoutOutcome.Registered)
        {
            // Only registration needs Developer Mode. Requiring it to materialize a layout would
            // make a host that never registers anything — the `--on sandbox` case — fail on a
            // prerequisite for a step it does not perform; the guest checks its own.
            throw new InvalidOperationException("Developer Mode is not enabled on this machine. Please enable Developer Mode and try again.");
        }

        taskContext.AddDebugMessage($"Using AppX manifest: {appxManifestPath}");

        var manifestContent = await File.ReadAllTextAsync(appxManifestPath.FullName, Encoding.UTF8, cancellationToken);

        // Detect whether this manifest was generated by MSBuild (dotnet build).
        // MSBuild-generated manifests have build:Metadata with a makepri.exe entry.
        // When MSBuild-generated, the build output includes a .appxrecipe file that
        // lists all files and their correct source paths for the AppX layout.
        var doc = AppxManifestDocument.Parse(manifestContent);
        var isMSBuildGenerated = doc.Document.Root?
            .Element(AppxManifestDocument.BuildNs + "Metadata")?
            .Elements(AppxManifestDocument.BuildNs + "Item")
            .Any(e => string.Equals(e.Attribute("Name")?.Value, "makepri.exe", StringComparison.OrdinalIgnoreCase)) == true;

        if (isMSBuildGenerated)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Note} MSBuild-generated manifest detected");

            // Snapshot the previous registered manifest BEFORE the copy/sync overwrites it (issue #537).
            var previousManifestBytes = TryReadExistingLayoutManifestBytes(outputAppXDirectory);

            // Look for a .build.appxrecipe file in the input directory
            var recipeFile = inputDirectory.EnumerateFiles("*.build.appxrecipe", SearchOption.TopDirectoryOnly).FirstOrDefault();

            if (recipeFile != null)
            {
                taskContext.AddDebugMessage($"{UiSymbols.Files} Using appxrecipe for layout: {recipeFile.Name}");
                await CopyFilesFromRecipeAsync(recipeFile, outputAppXDirectory, taskContext, reconciliation, cancellationToken);

                // The recipe copy does not delete stale files, so a reused layout can still hold a
                // Package.appxmanifest from an earlier run. FindManifest below prefers that name, which
                // would register a stale manifest instead of the one the recipe just staged.
                RemoveCompetingLayoutManifests(outputAppXDirectory, taskContext);
            }
            else
            {
                // No recipe — fall back to incremental copy from input directory
                taskContext.AddDebugMessage($"{UiSymbols.Warning} No .appxrecipe found, falling back to file copy");
                SyncFilesToOutputDirectory(inputDirectory, outputAppXDirectory, appxManifestPath, taskContext, reconciliation);
            }

            var identity = ParseAppxManifestAsync(manifestContent);

            if (outcome == LooseLayoutOutcome.Materialized)
            {
                return new MsixIdentityResult(identity.PackageName, identity.Publisher, identity.ApplicationId);
            }

            // Install the Windows App Runtime framework packages if not already present. Pin the package
            // list to the effective built TFM so a multi-targeted app doesn't pick a sibling framework's
            // divergent Windows App SDK version (M2). This loose-layout pipeline is shared with folder mode
            // (which always restores) and packaged project mode (which honors the run's --no-restore), so
            // thread the caller's setting through instead of forcing a restore during discovery.
            // A self-contained app carries its own Windows App SDK, so both steps are skipped.
            if (!selfContained)
            {
                var msbuildPackageList = await ResolveDotNetPackageListAsync(projectFile, framework, noRestore, packageGraph, cancellationToken);
                await EnsureWindowsAppRuntimeInstalledAsync(msbuildPackageList, runtimeArch, taskContext, cancellationToken);
            }

            // Resolve the manifest that will be registered (issue #537 / TrySkipRegistration).
            // Prefer the canonical appxmanifest.xml directly rather than probing: the staging cleanup that
            // removes a stale Package.appxmanifest is best-effort, so a locked leftover would otherwise win
            // ManifestHelper.FindManifest's name preference and register the wrong manifest. Fall back to
            // the probe when the canonical file is absent, so a genuinely missing manifest still surfaces
            // through RegisterLooseLayoutPackageAsync's error.
            var registrationManifest = ResolveLayoutRegistrationManifest(outputAppXDirectory);

            // Stage the alias into the manifest the recipe just laid down. This branch has its own
            // staging path, so the mutation applied to the raw-manifest branch below does not reach it —
            // without this, a recipe-backed project asking for alias launch registers fine and then fails
            // with "No execution alias found in the manifest". Applied BEFORE the skip check so a run that
            // adds an alias is not mistaken for an unchanged one.
            if (ensureExecutionAlias)
            {
                EnsureStagedExecutionAlias(registrationManifest, taskContext);
            }

            var skipResult = TrySkipRegistration(
                identity.PackageName, identity.Publisher, identity.ApplicationId,
                previousManifestBytes, registrationManifest, outputAppXDirectory,
                clean, taskContext, cancellationToken);
            if (skipResult is not null)
            {
                return skipResult;
            }

            // Unregister any existing package first (preserving app data by default)
            await UnregisterExistingPackageAsync(
                identity.PackageName,
                taskContext,
                identity.Publisher,
                preserveAppData: !clean,
                cancellationToken);

            // Register from the AppX layout directory
            await RegisterLooseLayoutPackageAsync(registrationManifest, taskContext, cancellationToken);

            return new MsixIdentityResult(identity.PackageName, identity.Publisher, identity.ApplicationId);
        }

        // --- Non-MSBuild manifest path (raw Package.appxmanifest with unresolved placeholders) ---

        if (!outputAppXDirectory.Exists)
        {
            outputAppXDirectory.Create();
        }

        // Snapshot the previously-registered manifest BEFORE Sync overwrites it (issue #537).
        var previousRawManifestBytes = TryReadExistingLayoutManifestBytes(outputAppXDirectory);

        SyncFilesToOutputDirectory(inputDirectory, outputAppXDirectory, appxManifestPath, taskContext, reconciliation);

        // SyncFilesToOutputDirectory normalizes any *.appxmanifest → appxmanifest.xml
        var copiedManifestName = string.Equals(appxManifestPath.Extension, ".appxmanifest", StringComparison.OrdinalIgnoreCase)
            ? "appxmanifest.xml"
            : appxManifestPath.Name;
        var copiedAppxManifestPath = new FileInfo(Path.Combine(outputAppXDirectory.FullName, copiedManifestName));
        manifestContent = await File.ReadAllTextAsync(copiedAppxManifestPath.FullName, Encoding.UTF8, cancellationToken);

        // Resolve $targetnametoken$ and other placeholders using the same logic as
        // winapp package — uses --executable if provided, otherwise searches the AppX
        // root for a single non-runtime exe.
        manifestContent = ResolveManifestPlaceholders(manifestContent, executable, outputAppXDirectory, taskContext);

        // Determine the resolved executable for downstream operations (PRI rename, arch detection).
        // ResolveManifestPlaceholders guarantees the Executable attribute is non-placeholder
        // on success, so we expect a concrete file name here.
        var resolvedDoc = AppxManifestDocument.Parse(manifestContent);
        var resolvedExeName = resolvedDoc.ApplicationExecutable
            ?? throw new InvalidOperationException(
                "Manifest has no Application/Executable attribute. Cannot determine the application executable.");
        var executableMatch = new FileInfo(Path.Combine(outputAppXDirectory.FullName, resolvedExeName));
        if (!executableMatch.Exists)
        {
            throw new FileNotFoundException(
                $"Executable '{resolvedExeName}' (from manifest) was not found in the output directory '{outputAppXDirectory.FullName}'. " +
                "Ensure the build output contains the exe, or pass --executable with the correct relative path.");
        }

        // Fetch dotnet package list once for all downstream operations. Pin to the effective built TFM
        // (M2) so a multi-targeted app resolves the runtime for the framework it was actually built for.
        // Shared loose-layout pipeline (folder + packaged project mode); honors the caller's --no-restore
        // so a run that opted out of restoring cannot trigger an implicit one during discovery.
        // A self-contained app carries its own Windows App SDK, so skip discovery entirely.
        var dotNetPackageList = selfContained
            ? null
            : await ResolveDotNetPackageListAsync(projectFile, framework, noRestore, packageGraph, cancellationToken);

        // If there is a pri file named after the executable, rename it to resources.pri
        var priFilePath = Path.Combine(outputAppXDirectory.FullName, Path.GetFileNameWithoutExtension(executableMatch.Name) + ".pri");
        if (File.Exists(priFilePath))
        {
            var resourcesPriPath = Path.Combine(outputAppXDirectory.FullName, "resources.pri");
            File.Move(priFilePath, resourcesPriPath, overwrite: true);
            taskContext.AddDebugMessage($"{UiSymbols.Files} Renamed {Path.GetFileName(priFilePath)} to resources.pri");
        }

        // Generate resources.pri if not present (matches winapp package behavior)
        var existingPri = new FileInfo(Path.Combine(outputAppXDirectory.FullName, "resources.pri"));
        if (!existingPri.Exists)
        {
            try
            {
                var stagingManifest = new FileInfo(Path.Combine(outputAppXDirectory.FullName, "appxmanifest.xml"));
                var priExpandedFiles = MrtAssetHelper.GetExpandedManifestReferencedFiles(stagingManifest, taskContext);
                var priResourceCandidates = priExpandedFiles.Select(file => file.RelativePath);
                await priService.CreatePriConfigAsync(
                    outputAppXDirectory,
                    taskContext,
                    precomputedPriResourceCandidates: priResourceCandidates,
                    cancellationToken: cancellationToken);
                await priService.GeneratePriFileAsync(outputAppXDirectory, taskContext, cancellationToken: cancellationToken);
                taskContext.AddDebugMessage($"{UiSymbols.Files} Generated resources.pri");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "{UISymbol} Failed to generate resources.pri: {Message}. The app may not launch correctly. Re-run with --verbose for details.", UiSymbols.Warning, ex.Message);
                taskContext.AddDebugMessage($"{UiSymbols.Warning} PRI generation error details: {ex}");
            }
        }

        // Resolve <Resource Language="x-generate"/> — falls back to "en-US" if no PRI found
        manifestContent = manifestContent.Replace("x-generate", "EN-US");

        // Unified manifest processing: WinAppSDK dependency, third-party WinRT components,
        // ProcessorArchitecture auto-detection, and build metadata
        (manifestContent, _) = await UpdateAppxManifestContentAsync(
            manifestContent, null, null, executableMatch.FullName,
            sparse: false, selfContained,
            dotNetPackageList, taskContext, cancellationToken);

        // Give the app an execution alias when it needs one to reach this terminal and does not author one
        // itself. This edits the manifest STAGED in the AppX layout, never the one the user checked in —
        // the alias is a launch mechanism winapp chose, so it should not appear in their source tree.
        if (ensureExecutionAlias)
        {
            var staged = AppxManifestDocument.Parse(manifestContent);
            if (TryAddDefaultExecutionAlias(staged, taskContext))
            {
                manifestContent = staged.ToXml();
            }
        }

        await File.WriteAllTextAsync(copiedAppxManifestPath.FullName, manifestContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

        // Copy all assets
        var originalManifestDir = appxManifestPath.DirectoryName;

        if (!string.Equals(originalManifestDir, outputAppXDirectory.FullName, StringComparison.OrdinalIgnoreCase))
        {
            var expandedFiles = MrtAssetHelper.GetExpandedManifestReferencedFiles(appxManifestPath, taskContext);
            MrtAssetHelper.CopyAllAssets(expandedFiles, outputAppXDirectory, taskContext);
        }
        else
        {
            taskContext.AddDebugMessage($"{UiSymbols.Warning} Manifest directory and target directory are the same, skipping assets copy");
        }

        {
            var identity = ParseAppxManifestAsync(manifestContent);

            if (outcome == LooseLayoutOutcome.Materialized)
            {
                return new MsixIdentityResult(identity.PackageName, identity.Publisher, identity.ApplicationId);
            }

            // Install the Windows App Runtime framework packages if not already present. A self-contained
            // app ships its own copy, so provisioning is skipped (dotNetPackageList is null there).
            if (!selfContained)
            {
                await EnsureWindowsAppRuntimeInstalledAsync(dotNetPackageList, runtimeArch, taskContext, cancellationToken);
            }

            // See MSBuild branch above for the rationale (issue #537).
            var skipResult = TrySkipRegistration(
                identity.PackageName, identity.Publisher, identity.ApplicationId,
                previousRawManifestBytes, copiedAppxManifestPath, outputAppXDirectory,
                clean, taskContext, cancellationToken);
            if (skipResult is not null)
            {
                return skipResult;
            }

            // Unregister any existing package first (preserving app data by default)
            await UnregisterExistingPackageAsync(
                identity.PackageName,
                taskContext,
                identity.Publisher,
                preserveAppData: !clean,
                cancellationToken);

            // Register the new debug manifest with external location
            await RegisterLooseLayoutPackageAsync(copiedAppxManifestPath, taskContext, cancellationToken);

            return new MsixIdentityResult(identity.PackageName, identity.Publisher, identity.ApplicationId);
        }
    }

    /// <summary>
    /// Layout files that are never pruned, even when the recipe does not name them.
    /// </summary>
    /// <remarks>
    /// These two are what a previously registered loose layout is served from. A recipe that
    /// somehow omits them is far more likely to be truncated than to mean "the app has no
    /// manifest", and deleting them would break a registration that is currently working.
    /// </remarks>
    private static readonly HashSet<string> ProtectedLayoutFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "appxmanifest.xml",
        "resources.pri",
    };

    /// <summary>One validated recipe entry: an existing source file and where it goes in the layout.</summary>
    private readonly record struct RecipeEntry(string SourcePath, string PackagePath);

    /// <summary>
    /// Rejects a layout path that is, or sits beneath, a reparse point.
    /// </summary>
    /// <remarks>
    /// Every safety property here is expressed as "inside the layout directory". A junction or
    /// symlink anywhere in the layout's own path makes that phrase mean a different tree than the
    /// caller believes, so deletions judged safe against the layout could land somewhere else
    /// entirely. Refusing such a path outright is the only cheap way to keep the reasoning sound;
    /// it is re-checked immediately before each destructive phase because a link can be swapped in
    /// after the first check.
    /// </remarks>
    private static void EnsureLayoutPathHasNoReparsePoint(DirectoryInfo layout)
    {
        var link = FindReparsePointComponent(layout.FullName);

        if (link is not null)
        {
            throw new InvalidOperationException(
                $"Refusing to use '{layout.FullName}' as an AppX layout directory because '{link.FullName}' " +
                "is a symbolic link or junction. Point --output-appx-directory at a real directory instead.");
        }
    }

    /// <summary>
    /// The first component of <paramref name="path"/> that is a symbolic link or junction, walking
    /// from the path itself up to the drive root, or <see langword="null"/> when there is none.
    /// </summary>
    private static DirectoryInfo? FindReparsePointComponent(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));

        while (current is not null)
        {
            // Refresh(): DirectoryInfo caches attributes, and this is re-run specifically to observe
            // a change made since the previous call.
            current.Refresh();

            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return current;
            }

            current = current.Parent;
        }

        return null;
    }

    /// <summary>
    /// Reads and fully validates a recipe before any file is written or deleted.
    /// </summary>
    /// <remarks>
    /// Everything that can disqualify a recipe is checked up front, on purpose. A layout is only
    /// meaningful as a whole — a half-applied one can be registered and launched, and would run with
    /// a stale executable, manifest or PRI while looking like it succeeded. Failing before the first
    /// mutation leaves the previous layout exactly as it was, which is a state known to work.
    /// </remarks>
    private static List<RecipeEntry> ReadAndValidateRecipe(FileInfo recipeFile, DirectoryInfo outputDir, string recipeContent)
    {
        System.Xml.Linq.XDocument recipeDoc;
        try
        {
            recipeDoc = System.Xml.Linq.XDocument.Parse(recipeContent);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new InvalidOperationException(
                $"The build recipe '{recipeFile.FullName}' is not valid XML: {ex.Message}. Rebuild the project and try again.", ex);
        }

        System.Xml.Linq.XNamespace msbuildNs = "http://schemas.microsoft.com/developer/msbuild/2003";

        var rawEntries = recipeDoc.Descendants(msbuildNs + "AppXManifest")
            .Concat(recipeDoc.Descendants(msbuildNs + "AppxPackagedFile"))
            .ToList();

        if (rawEntries.Count == 0)
        {
            throw new InvalidOperationException(
                $"The build recipe '{recipeFile.FullName}' lists no files. This usually means the build did not " +
                "complete; rebuild the project and try again.");
        }

        var entries = new List<RecipeEntry>(rawEntries.Count);
        var byPackagePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missingSources = new List<string>();

        foreach (var entry in rawEntries)
        {
            var sourcePath = entry.Attribute("Include")?.Value;
            var rawPackagePath = entry.Element(msbuildNs + "PackagePath")?.Value;

            if (string.IsNullOrWhiteSpace(rawPackagePath))
            {
                throw new InvalidOperationException(
                    $"The build recipe '{recipeFile.FullName}' has an entry with no PackagePath " +
                    $"(source '{sourcePath ?? "<none>"}'). Rebuild the project and try again.");
            }

            var packagePath = NormalizePackagePath(rawPackagePath);

            if (packagePath.Length == 0 || Path.IsPathRooted(packagePath))
            {
                throw new InvalidOperationException(
                    $"The build recipe '{recipeFile.FullName}' maps a file to '{rawPackagePath}', which is not a " +
                    "location inside the package. Rebuild the project and try again.");
            }

            // The destination must land inside the layout even after the path is resolved, so a
            // traversal segment cannot make a copy (or a later prune) reach outside it.
            var destinationPath = Path.GetFullPath(Path.Combine(outputDir.FullName, packagePath));
            if (!IsPathInsideDirectory(destinationPath, outputDir.FullName))
            {
                throw new InvalidOperationException(
                    $"The build recipe '{recipeFile.FullName}' maps a file to '{rawPackagePath}', which resolves " +
                    $"outside the layout directory '{outputDir.FullName}'. Rebuild the project and try again.");
            }

            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new InvalidOperationException(
                    $"The build recipe '{recipeFile.FullName}' has an entry for '{rawPackagePath}' with no source " +
                    "file. Rebuild the project and try again.");
            }

            if (byPackagePath.TryGetValue(packagePath, out var previousSource))
            {
                // Two sources for one destination is ambiguous, and on Windows two spellings that
                // differ only in case are the same destination.
                if (!string.Equals(previousSource, sourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"The build recipe '{recipeFile.FullName}' maps two different files to '{rawPackagePath}' " +
                        $"('{previousSource}' and '{sourcePath}'). Rebuild the project and try again.");
                }

                continue;
            }

            if (!File.Exists(sourcePath))
            {
                missingSources.Add($"{rawPackagePath} (from '{sourcePath}')");
                continue;
            }

            byPackagePath[packagePath] = sourcePath;
            entries.Add(new RecipeEntry(sourcePath, packagePath));
        }

        if (missingSources.Count > 0)
        {
            // Previously these were skipped and whatever the layout already held was kept, which
            // quietly produced a layout mixing this build's files with an older build's.
            throw new InvalidOperationException(
                $"The build did not produce {missingSources.Count} file(s) the recipe '{recipeFile.FullName}' lists: " +
                string.Join(", ", missingSources.Take(5)) +
                (missingSources.Count > 5 ? $", and {missingSources.Count - 5} more" : string.Empty) +
                ". Rebuild the project and try again.");
        }

        return entries;
    }

    /// <summary>
    /// Copies the files a <c>.build.appxrecipe</c> lists into the AppX layout directory, then — for a
    /// layout winapp generated itself — removes layout files the app no longer contains.
    /// </summary>
    /// <remarks>
    /// The generated layout is winapp's artifact: winapp creates it and MSBuild never sees it, so
    /// <c>dotnet clean</c> does not empty it. Copying without reconciling therefore turned it into an
    /// ever-growing union of every build ever materialized into it — a file deleted from the project
    /// stayed in the package and, because an execution target mirrors this layout exactly, stayed in
    /// the guest too.
    /// <para>
    /// Deletion is confined to that generated layout. A directory the caller named may hold files
    /// winapp never put there and cannot recognize, so it is only ever copied into.
    /// </para>
    /// </remarks>
    private static async Task CopyFilesFromRecipeAsync(
        FileInfo recipeFile,
        DirectoryInfo outputDir,
        TaskContext taskContext,
        LayoutReconciliation reconciliation,
        CancellationToken cancellationToken)
    {
        var layoutPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDir.FullName));
        var gate = RecipeLayoutLocks.GetOrAdd(layoutPath, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CopyFilesFromRecipeCoreAsync(
                recipeFile,
                outputDir,
                taskContext,
                reconciliation,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task CopyFilesFromRecipeCoreAsync(
        FileInfo recipeFile,
        DirectoryInfo outputDir,
        TaskContext taskContext,
        LayoutReconciliation reconciliation,
        CancellationToken cancellationToken)
    {
        // A `None` layout is a staging directory winapp just created, commonly under the system temp
        // directory, which on some machines is reached through a junction. Nothing there is pruned,
        // so the link checks that make deletion safe would only reject a legitimate path.
        var enforceRealPaths = reconciliation != LayoutReconciliation.None;

        if (enforceRealPaths)
        {
            // Validated before anything is created, so a layout path that is refused never gets a
            // directory created for it as a side effect.
            EnsureLayoutPathHasNoReparsePoint(outputDir);
        }

        string recipeContent;
        try
        {
            recipeContent = await File.ReadAllTextAsync(recipeFile.FullName, Encoding.UTF8, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Could not read the build recipe '{recipeFile.FullName}': {ex.Message}. Rebuild the project and try again.", ex);
        }

        // Fully validated before the first mutation: on any problem the previous layout is untouched
        // and still whatever it was before this run.
        var entries = ReadAndValidateRecipe(recipeFile, outputDir, recipeContent);

        // The recipe lists only what goes into the package, so it is authoritative for the layout but
        // NOT for the build output it copies from: a .pdb, .deps.json or the recipe itself are absent
        // from it. If the layout contains the build output, treating the recipe as the complete
        // desired set would delete the build, so nothing is reconciled in that shape.
        if (reconciliation == LayoutReconciliation.Exact && IsPathInsideDirectory(recipeFile.DirectoryName, outputDir.FullName))
        {
            taskContext.AddDebugMessage(
                $"{UiSymbols.Warning} Layout directory contains the build output; leaving files the recipe does not list in place. " +
                "Use a separate --output-appx-directory if files removed from the app must disappear from the layout.");
            reconciliation = LayoutReconciliation.Additive;
        }

        if (!outputDir.Exists)
        {
            outputDir.Create();
            outputDir.Refresh();
        }

        if (enforceRealPaths)
        {
            // Re-checked after Create: the path may have been swapped for a link since the first check.
            EnsureLayoutPathHasNoReparsePoint(outputDir);
        }

        var desired = CopyRecipeEntries(entries, outputDir, enforceRealPaths, out var copied, out var skipped);

        if (reconciliation != LayoutReconciliation.Exact)
        {
            taskContext.AddDebugMessage(
                $"{UiSymbols.Check} AppX layout from recipe: {copied} copied, {skipped} unchanged, 0 removed");
            return;
        }

        // Re-checked immediately before the destructive phase, for the same reason as above.
        EnsureLayoutPathHasNoReparsePoint(outputDir);

        var (removed, unremovable) = PruneLayout(outputDir, desired, taskContext);

        ThrowIfLayoutStillHoldsStaleContent(outputDir, unremovable);

        taskContext.AddDebugMessage(
            $"{UiSymbols.Check} AppX layout from recipe: {copied} copied, {skipped} unchanged, {removed} removed");
    }

    /// <summary>
    /// Fails the materialization when the layout still holds content the app no longer contains.
    /// </summary>
    /// <remarks>
    /// Continuing would register or deploy a layout holding files the app dropped — exactly the
    /// stale-content bug this reconciliation exists to prevent — while reporting success.
    /// </remarks>
    private static void ThrowIfLayoutStillHoldsStaleContent(DirectoryInfo outputDir, List<string> unremovable)
    {
        if (unremovable.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Could not remove {unremovable.Count} item(s) the app no longer contains from the layout at " +
            $"'{outputDir.FullName}': {string.Join(", ", unremovable.Take(5))}" +
            (unremovable.Count > 5 ? $", and {unremovable.Count - 5} more" : string.Empty) +
            ". They are usually held open by a running instance of the app, or are symbolic links or " +
            "junctions winapp will not delete through — close the app or remove the link, and try again.");
    }

    /// <summary>
    /// Copies every validated entry into the layout and returns the set of package-relative paths
    /// that now make up the app, which is both the copy result and the desired state to reconcile to.
    /// </summary>
    /// <remarks>
    /// Each destination is re-checked for links immediately before it is written. Validating only the
    /// layout root is not enough: a junction at <c>Assets</c> inside the layout would send
    /// <c>Assets\logo.png</c> through to whatever it points at, so a copy the caller believes is
    /// confined to the layout could overwrite a file anywhere on the machine.
    /// </remarks>
    private static HashSet<string> CopyRecipeEntries(List<RecipeEntry> entries, DirectoryInfo outputDir, bool enforceRealPaths, out int copied, out int skipped)
    {
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        copied = 0;
        skipped = 0;

        foreach (var entry in entries)
        {
            desired.Add(entry.PackagePath);

            var destPath = Path.Combine(outputDir.FullName, entry.PackagePath);
            var destFile = new FileInfo(destPath);

            // Skip unchanged files (same size and timestamp)
            if (destFile.Exists)
            {
                var srcFile = new FileInfo(entry.SourcePath);
                if (destFile.Length == srcFile.Length && destFile.LastWriteTimeUtc == srcFile.LastWriteTimeUtc)
                {
                    skipped++;
                    continue;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

            if (enforceRealPaths)
            {
                EnsureDestinationIsInsideLayout(outputDir, destPath);
            }

            AtomicFile.Copy(entry.SourcePath, destPath);
            copied++;
        }

        return desired;
    }

    /// <summary>
    /// Rejects a destination whose own path, or any directory between it and the layout root, is a
    /// link out of the layout — checked after the directories are created and before the write.
    /// </summary>
    private static void EnsureDestinationIsInsideLayout(DirectoryInfo outputDir, string destPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDir.FullName));
        var current = Path.GetFullPath(destPath);

        while (true)
        {
            if (IsReparsePoint(current))
            {
                throw new InvalidOperationException(
                    $"Refusing to write '{destPath}' because '{current}' is a symbolic link or junction. " +
                    "A link inside an AppX layout points writes outside it. Remove the link and try again.");
            }

            var parent = Path.GetDirectoryName(current);

            if (parent is null || string.Equals(Path.TrimEndingDirectorySeparator(parent), root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            current = parent;
        }
    }

    /// <summary>
    /// True when the path exists and is a link. Attributes are read by path rather than through
    /// <see cref="FileInfo"/>, whose <c>Exists</c> is false for a directory and would silently skip
    /// every intermediate directory in a destination path.
    /// </summary>
    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Normalizes a recipe PackagePath to the form <see cref="Path.GetRelativePath"/> produces.</summary>
    private static string NormalizePackagePath(string packagePath)
        => packagePath.Replace('/', Path.DirectorySeparatorChar)
            .Trim()
            .TrimStart(Path.DirectorySeparatorChar);

    /// <summary>
    /// Removes layout files the app no longer contains, and any directory those removals emptied.
    /// Returns how many were removed and which ones could not be.
    /// </summary>
    /// <remarks>
    /// Only ever called for <see cref="LayoutReconciliation.Exact"/> — the <c>AppX</c> directory
    /// winapp generates for itself — which is reconciled to <paramref name="desired"/> exactly. A
    /// directory the caller named is never pruned at all, so files winapp never staged survive.
    /// <para>
    /// Directory reparse points are never descended into, so a junction placed in the layout cannot
    /// make this delete files outside it — but neither can it be left in place and called exact, so
    /// it is reported as unremovable. Files that cannot be deleted are collected rather than
    /// ignored — the caller fails the materialization, because a layout that still holds dropped
    /// content must not go on to be registered or deployed.
    /// </para>
    /// </remarks>
    private static (int Removed, List<string> Unremovable) PruneLayout(
        DirectoryInfo outputDir,
        HashSet<string> desired,
        TaskContext taskContext)
    {
        var removed = 0;
        var unremovable = new List<string>();
        var emptiedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var linkedDirectories = new List<DirectoryInfo>();

        foreach (var file in EnumerateLayoutFiles(outputDir, linkedDirectories))
        {
            var relativePath = Path.GetRelativePath(outputDir.FullName, file.FullName);

            if (desired.Contains(relativePath) || ProtectedLayoutFiles.Contains(relativePath))
            {
                continue;
            }

            // Revalidate immediately before the destructive call: containment is what makes this safe,
            // and the tree can change between enumeration and deletion.
            file.Refresh();
            if (!file.Exists)
            {
                continue;
            }

            if (!IsPathInsideDirectory(file.FullName, outputDir.FullName)
                || file.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || IsUnderReparsePoint(file, outputDir))
            {
                // Deleting through a link would reach outside the layout, so it is not deleted — but
                // it is stale content in a layout about to be registered or deployed, so it is
                // reported as unremovable and fails the materialization rather than shipping.
                taskContext.AddDebugMessage(
                    $"{UiSymbols.Warning} Cannot remove '{relativePath}': it is a link or no longer inside the layout directory.");
                unremovable.Add(relativePath);
                continue;
            }

            try
            {
                file.Delete();
                removed++;
                if (file.DirectoryName is { } parent)
                {
                    emptiedDirectories.Add(parent);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                taskContext.AddDebugMessage(
                    $"{UiSymbols.Warning} Could not remove stale layout file '{relativePath}': {ex.Message}");
                unremovable.Add(relativePath);
            }
        }

        // A directory link in the layout is content winapp never staged and will not delete through:
        // removing it would either reach into whatever it points at or leave the layout claiming to
        // be exact while an unknown tree hangs off it. It is reported so the caller fails instead.
        foreach (var linked in linkedDirectories)
        {
            var relativePath = Path.GetRelativePath(outputDir.FullName, linked.FullName);

            taskContext.AddDebugMessage(
                $"{UiSymbols.Warning} Cannot reconcile '{relativePath}': it is a symbolic link or junction, " +
                "and winapp does not delete through one.");
            unremovable.Add(relativePath);
        }

        if (emptiedDirectories.Count > 0)
        {
            EnsureLayoutPathHasNoReparsePoint(outputDir);
            RemoveEmptiedDirectories(outputDir, emptiedDirectories);
        }

        return (removed, unremovable);
    }

    /// <summary>
    /// Returns true if any directory between <paramref name="file"/> and <paramref name="root"/> is a
    /// reparse point, meaning the file is not really inside the layout the caller reasoned about.
    /// </summary>
    private static bool IsUnderReparsePoint(FileInfo file, DirectoryInfo root)
    {
        var rootPath = Path.GetFullPath(root.FullName).TrimEnd(Path.DirectorySeparatorChar);
        var current = file.Directory;

        while (current is not null
            && !string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar), rootPath, StringComparison.OrdinalIgnoreCase))
        {
            current.Refresh();
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    /// <summary>
    /// Enumerates every file under <paramref name="root"/>, never following a directory reparse
    /// point. Each one encountered is collected in <paramref name="linkedDirectories"/> instead,
    /// because a link is not a directory this can reconcile and must not be silently passed over.
    /// </summary>
    private static IEnumerable<FileInfo> EnumerateLayoutFiles(DirectoryInfo root, List<DirectoryInfo> linkedDirectories)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            foreach (var subdirectory in directory.EnumerateDirectories())
            {
                if (subdirectory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    linkedDirectories.Add(subdirectory);
                    continue;
                }

                pending.Push(subdirectory);
            }

            foreach (var file in directory.EnumerateFiles())
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// Removes directories that pruning left empty, walking up to (but never removing)
    /// <paramref name="root"/>. Only directories a deletion actually emptied are considered, so a
    /// directory that was already empty before this run is left as the caller had it.
    /// </summary>
    private static void RemoveEmptiedDirectories(DirectoryInfo root, IEnumerable<string> candidates)
    {
        var rootPath = root.FullName.TrimEnd(Path.DirectorySeparatorChar);

        foreach (var candidate in candidates.OrderByDescending(path => path.Length))
        {
            var directory = new DirectoryInfo(candidate);

            while (directory.Exists
                && !string.Equals(directory.FullName.TrimEnd(Path.DirectorySeparatorChar), rootPath, StringComparison.OrdinalIgnoreCase)
                && !directory.Attributes.HasFlag(FileAttributes.ReparsePoint)
                && !directory.EnumerateFileSystemInfos().Any())
            {
                var parent = directory.Parent;

                try
                {
                    directory.Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    break;
                }

                if (parent is null)
                {
                    break;
                }

                directory = parent;
            }
        }
    }

    /// <summary>
    /// Copies the input directory into the layout when there is no recipe to work from, then copies
    /// and normalizes the manifest.
    /// </summary>
    /// <remarks>
    /// Without a recipe the input directory is the only description of what the layout should hold,
    /// and it is a weaker one: it is whatever the build happened to leave on disk. That is good
    /// enough to copy from and not good enough to delete by, so files are only removed from a layout
    /// winapp generated itself (<see cref="LayoutReconciliation.Exact"/>). For a directory the caller
    /// named, the copy is purely additive.
    /// <para>
    /// The layout is very often a subdirectory of the input (<c>winapp run . --output-appx-directory
    /// .\AppX</c>), so it is excluded from the walk of the input. Copying it into itself would leave
    /// a copy of the layout inside the layout, and a copy of that on the run after, without limit.
    /// That exclusion compares paths as they are spelled, so an input reached through a link is
    /// refused: the layout could then be inside it without either path saying so.
    /// </para>
    /// <para>
    /// A link inside the input is refused too, in every mode, because winapp will not copy through
    /// one and a layout quietly missing whatever was behind it is not a success worth reporting.
    /// </para>
    /// </remarks>
    private static void SyncFilesToOutputDirectory(DirectoryInfo inputDirectory, DirectoryInfo outputAppXDirectory, FileInfo appxManifestPath, TaskContext taskContext, LayoutReconciliation reconciliation)
    {
        // A `None` layout is a staging directory winapp just created, commonly under the system temp
        // directory, which on some machines is reached through a junction. Nothing there is pruned,
        // so the link checks that make deletion safe would only reject a legitimate path.
        var enforceRealPaths = reconciliation != LayoutReconciliation.None;

        if (enforceRealPaths)
        {
            // Before Create, so a layout path that is refused never gets a directory made for it.
            EnsureLayoutPathHasNoReparsePoint(outputAppXDirectory);
        }

        if (!outputAppXDirectory.Exists)
        {
            outputAppXDirectory.Create();
            outputAppXDirectory.Refresh();
        }

        if (enforceRealPaths)
        {
            EnsureLayoutPathHasNoReparsePoint(outputAppXDirectory);
        }

        var layoutIsTheInput = inputDirectory is not null && string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(inputDirectory.FullName)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputAppXDirectory.FullName)),
            StringComparison.OrdinalIgnoreCase);

        if (inputDirectory is not null && !layoutIsTheInput)
        {
            if (enforceRealPaths)
            {
                // Both nesting checks below compare paths as they are spelled. A link anywhere in
                // the input's path makes that spelling name a different tree than the one on disk,
                // so a layout reached by its real path can sit inside the input while looking like
                // it does not — and the walk then stages the layout into itself on every run.
                var linkedInput = FindReparsePointComponent(inputDirectory.FullName);

                if (linkedInput is not null)
                {
                    throw new InvalidOperationException(
                        $"Refusing to build an AppX layout from '{inputDirectory.FullName}' because " +
                        $"'{linkedInput.FullName}' is a symbolic link or junction, so winapp cannot tell whether " +
                        $"the layout directory '{outputAppXDirectory.FullName}' sits inside it. Package the real " +
                        "directory the link points at instead.");
                }
            }

            // The supported nesting is the layout inside the build output. The reverse cannot be
            // made to work: every input file would land beside the input's own copy of it, and an
            // exact layout would then judge the build itself to be content the app dropped.
            if (IsPathInsideDirectory(inputDirectory.FullName, outputAppXDirectory.FullName))
            {
                throw new InvalidOperationException(
                    $"The AppX layout directory '{outputAppXDirectory.FullName}' contains the folder being packaged " +
                    $"('{inputDirectory.FullName}'). Point --output-appx-directory at a directory outside it.");
            }

            var sourceFiles = EnumerateInputFilesForLayout(inputDirectory, outputAppXDirectory);

            // The same copier and pruner the recipe path uses, so a layout built without a recipe
            // gets the same per-destination link checks and the same reconciliation.
            var desired = CopyRecipeEntries(sourceFiles, outputAppXDirectory, enforceRealPaths, out var copied, out var skipped);

            if (reconciliation == LayoutReconciliation.Exact)
            {
                // Re-checked immediately before the destructive phase: a link can be swapped in
                // after the earlier check.
                EnsureLayoutPathHasNoReparsePoint(outputAppXDirectory);

                var (removed, unremovable) = PruneLayout(outputAppXDirectory, desired, taskContext);
                ThrowIfLayoutStillHoldsStaleContent(outputAppXDirectory, unremovable);

                taskContext.AddDebugMessage($"{UiSymbols.Check} Sync to output directory: {copied} copied, {skipped} unchanged, {removed} deleted");
            }
            else
            {
                taskContext.AddDebugMessage($"{UiSymbols.Check} Copy to output directory: {copied} copied, {skipped} unchanged, 0 deleted");
            }
        }

        // Copy the appxmanifest to the output directory
        appxManifestPath.CopyTo(Path.Combine(outputAppXDirectory.FullName, appxManifestPath.Name), overwrite: true);

        // Windows requires the manifest in a registered loose layout to be named appxmanifest.xml, so
        // normalize ANY authoring-side *.appxmanifest name — Package.appxmanifest, but also a per-file
        // name like counter.appxmanifest — rather than only the conventional one. Copying a differently
        // named manifest verbatim can never register: PackageManager rejects it with "An invalid manifest
        // file name was passed to this function. This file must be named AppxManifest.xml".
        if (string.Equals(appxManifestPath.Extension, ".appxmanifest", StringComparison.OrdinalIgnoreCase))
        {
            var renamedPath = Path.Combine(outputAppXDirectory.FullName, "appxmanifest.xml");
            var originalPath = Path.Combine(outputAppXDirectory.FullName, appxManifestPath.Name);
            File.Move(originalPath, renamedPath, true);
            taskContext.AddDebugMessage($"{UiSymbols.Files} Renamed {appxManifestPath.Name} to appxmanifest.xml");
        }

        RemoveCompetingLayoutManifests(outputAppXDirectory, taskContext);
    }

    /// <summary>
    /// Picks the manifest to register from a staged loose layout, preferring the canonical
    /// <c>appxmanifest.xml</c> that Windows requires over whatever
    /// <see cref="ManifestHelper.FindManifest"/>'s name preference would select.
    /// </summary>
    /// <remarks>
    /// <see cref="RemoveCompetingLayoutManifests"/> normally deletes a stale
    /// <c>Package.appxmanifest</c> from the layout, but that cleanup is best-effort — a locked leftover
    /// survives, and the probe prefers exactly that name. Selecting the canonical file directly means a
    /// failed cleanup can no longer change which manifest gets registered.
    /// </remarks>
    private static FileInfo ResolveLayoutRegistrationManifest(DirectoryInfo outputAppXDirectory)
    {
        var canonical = new FileInfo(Path.Join(outputAppXDirectory.FullName, "appxmanifest.xml"));
        return canonical.Exists ? canonical : ManifestHelper.FindManifest(outputAppXDirectory.FullName);
    }

    /// <summary>
    /// Declares the identity-derived execution alias on a manifest document, unless it already declares
    /// one of its own. Returns whether the document was modified.
    /// </summary>
    private static bool TryAddDefaultExecutionAlias(AppxManifestDocument document, TaskContext taskContext)
    {
        // Scoped to the package FAMILY, not the bare identity name: two side-loaded packages can share a
        // name under different publishers and coexist, so a name-only alias would put them in contention
        // for the one global entry Windows allows.
        if (string.IsNullOrEmpty(document.IdentityName) || string.IsNullOrEmpty(document.IdentityPublisher))
        {
            return false;
        }

        var familyName = AppLauncherService.ComputeFamilyName(document.IdentityName, document.IdentityPublisher);
        var aliasName = ExecutionAliasResolver.BuildDefaultAliasName(familyName);
        if (aliasName == null || document.EnsureExecutionAlias(aliasName) == null)
        {
            return false;
        }

        taskContext.AddDebugMessage($"{UiSymbols.Link} Staged execution alias '{aliasName}'");
        return true;
    }

    /// <summary>
    /// Adds the execution alias to a manifest already written into the AppX layout, for the staging paths
    /// that register a file rather than an in-memory string.
    /// </summary>
    /// <remarks>
    /// Non-fatal: the launch reports a missing alias with actionable guidance, and a default alias falls
    /// back to AUMID, so failing the whole registration here would be worse than the problem.
    /// </remarks>
    private void EnsureStagedExecutionAlias(FileInfo manifestFile, TaskContext taskContext)
    {
        try
        {
            if (!manifestFile.Exists)
            {
                return;
            }

            var document = AppxManifestDocument.Load(manifestFile.FullName);
            if (TryAddDefaultExecutionAlias(document, taskContext))
            {
                document.Save(manifestFile.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            logger.LogWarning(
                "{UISymbol} Could not add an execution alias to the staged manifest: {Message}. Alias launch may fail; declare a uap5:ExecutionAlias in the manifest to control it.",
                UiSymbols.Warning,
                ex.Message);
        }
    }

    /// <summary>
    /// Deletes any manifest in the layout root other than the <c>appxmanifest.xml</c> that was just
    /// staged, so a registered loose layout carries exactly one manifest.
    /// </summary>
    /// <remarks>
    /// The directory sync copies the whole input folder into the layout, so a manifest that lives in the
    /// payload — for example the <c>Package.appxmanifest</c> winapp generates into a file-based app's
    /// build output — arrives as an ordinary file. Registration always uses the normalized
    /// <c>appxmanifest.xml</c>, but downstream readers do not: <see cref="ManifestHelper.FindManifest"/>
    /// probes <c>Package.appxmanifest</c> FIRST, so leaving the stale copy behind makes
    /// <c>--with-alias</c> read the wrong manifest and report "No execution alias found" even though the
    /// app was registered from a manifest that declares one.
    /// </remarks>
    private static void RemoveCompetingLayoutManifests(DirectoryInfo outputAppXDirectory, TaskContext taskContext)
    {
        // Only prune once the canonical file is in place, so this can never leave a layout with no
        // manifest at all (for example if a staging step wrote only Package.appxmanifest).
        if (!File.Exists(Path.Join(outputAppXDirectory.FullName, "appxmanifest.xml")))
        {
            return;
        }

        foreach (var candidate in outputAppXDirectory.EnumerateFiles("*.appxmanifest", SearchOption.TopDirectoryOnly))
        {
            try
            {
                candidate.Delete();
                taskContext.AddDebugMessage($"{UiSymbols.Files} Removed competing layout manifest: {candidate.Name}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: a locked leftover must not fail the run, and registration still uses the
                // normalized appxmanifest.xml.
                taskContext.AddDebugMessage($"{UiSymbols.Warning} Could not remove {candidate.Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Every file in the build output that belongs in the layout: the whole tree, minus the layout
    /// itself. Refuses the whole run if any of it is reached through a link.
    /// </summary>
    /// <remarks>
    /// winapp never copies through a symbolic link or junction: following one leads outside the
    /// folder being packaged, and can lead back into the layout. Not following one means the layout
    /// silently lacks whatever was behind it, which is the same class of bug as the stale content
    /// this reconciliation exists to remove — an app that registers and runs while missing files.
    /// Neither is acceptable, so a link in the build output fails the run and says which one, for a
    /// layout winapp generated and for one the caller named alike. Nothing is copied or deleted
    /// first: the whole tree is walked before the first write, so the previous layout survives.
    /// </remarks>
    private static List<RecipeEntry> EnumerateInputFilesForLayout(
        DirectoryInfo inputDirectory, DirectoryInfo outputAppXDirectory)
    {
        var entries = new List<RecipeEntry>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(inputDirectory);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            foreach (var subdirectory in directory.EnumerateDirectories())
            {
                if (IsPathInsideDirectory(subdirectory.FullName, outputAppXDirectory.FullName))
                {
                    continue;
                }

                if (subdirectory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidOperationException(ThisIsALinkMessage(subdirectory.FullName, inputDirectory));
                }

                pending.Push(subdirectory);
            }

            foreach (var file in directory.EnumerateFiles())
            {
                // A linked file is refused for the same reason a linked directory is: its content
                // comes from outside the folder being packaged, so the layout would not be built
                // from the app it claims to describe.
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidOperationException(ThisIsALinkMessage(file.FullName, inputDirectory));
                }

                entries.Add(new RecipeEntry(file.FullName, Path.GetRelativePath(inputDirectory.FullName, file.FullName)));
            }
        }

        return entries;
    }

    /// <summary>
    /// Why a link in the folder being packaged stops the run, and what to do about it.
    /// </summary>
    private static string ThisIsALinkMessage(string linkPath, DirectoryInfo inputDirectory) =>
        $"'{linkPath}' is a symbolic link or junction. winapp will not build an AppX layout through a link, " +
        $"because the layout would not hold what '{inputDirectory.FullName}' appears to contain and the app " +
        "would register and run with those files missing. Replace it with the real file or directory.";

    /// <summary>
    /// Public entry point for the project-mode <b>unpackaged</b> path: resolves the project's package
    /// list (or falls back to a cwd glob) and installs the Windows App Runtime framework packages for
    /// the given architecture. Callers gate on <c>WindowsAppSDKSelfContained</c> before calling.
    /// </summary>
    public async Task<bool> EnsureWindowsAppRuntimeInstalledAsync(FileInfo? projectFile, string? architecture, string? framework, bool noRestore, TaskContext taskContext, PackageGraphSource? packageGraph = null, CancellationToken cancellationToken = default)
    {
        var packageList = await ResolveDotNetPackageListAsync(projectFile, framework, noRestore, packageGraph, cancellationToken);

        // A framework-dependent app needs the Windows App Runtime only if it actually uses the Windows
        // App SDK. A plain console/desktop Exe doesn't — preparing the runtime for it is wasted work and
        // prints a noisy "could not determine runtime" warning. Skip only on a positive no-reference
        // result; an unresolved list falls through to prep so a real WinUI app keeps its runtime.
        //
        // Callers that know the build's assets file pass it, which makes the graph exact. A caller that
        // does not falls back to re-evaluating the project in the default Configuration/RID, where a
        // Windows App SDK reference conditioned on a non-default one would be missed and prep wrongly
        // skipped — bounded by the skip firing only on a positive no-reference result.
        var referencesWindowsAppSdk = packageList is not null && ReferencesWindowsAppSdk(packageList);
        if (packageList is not null && !referencesWindowsAppSdk)
        {
            taskContext.AddDebugMessage(
                $"{UiSymbols.Note} No Windows App SDK reference found; skipping Windows App Runtime preparation.");
            return false;
        }

        // requireExactVersion:true — this unpackaged path must resolve the runtime the app was actually
        // built against, never an unrelated cached WinAppSDK version, or the presence gate below could
        // pass against the wrong runtime family.
        var expectedRuntimePackages = await EnsureWindowsAppRuntimeInstalledAsync(packageList, architecture, taskContext, cancellationToken, requireExactVersion: true);

        // Callers gate on WindowsAppSDKSelfContained, so a framework-dependent app here always needs a
        // Framework + DDLM. An empty list means the exact runtime packages couldn't be located, so the
        // version-specific identities can't be derived and the gate below would fall open to a generic
        // prefix check that could accept an unrelated registered version.
        if (expectedRuntimePackages.Count == 0)
        {
            if (referencesWindowsAppSdk)
            {
                // The app references the Windows App SDK but we couldn't locate the exact runtime it
                // needs. Falling open risks launching against an unrelated registered runtime, so fail
                // closed with an actionable error.
                var arch = architecture ?? WorkspaceSetupService.GetSystemArchitecture();
                throw new InvalidOperationException(
                    $"The exact Windows App Runtime the app requires for architecture '{arch}' could not be located, so it can't be " +
                    "installed or version-verified and the app would fail to start. Restore the project so the matching Windows App SDK " +
                    "runtime is available for that architecture, install it manually, or build a self-contained app (WindowsAppSDKSelfContained=true).");
            }

            // Package list was unresolved (couldn't positively confirm the reference): keep the tolerant
            // behavior and surface the risk loudly rather than blocking a launch we can't reason about.
            taskContext.AddStatusMessage(
                $"{UiSymbols.Warning} Could not determine the exact Windows App Runtime the app requires, so its " +
                "presence can't be version-verified. If the app fails to start, restore the project or install the " +
                "matching Windows App SDK runtime manually.");
        }

        // Presence gate: after the install attempt, verify the framework-dependent runtime (Framework +
        // matching-arch DDLM) is actually registered for the target arch, so a failed or skipped install
        // can't reach the launch. The expected identities pin the check to the specific version the app
        // needs, so a different (or older-patch) registered version can't mask the failure.
        if (!windowsAppRuntimeService.IsWindowsAppRuntimeRegistered(architecture, expectedRuntimePackages))
        {
            var arch = architecture ?? WorkspaceSetupService.GetSystemArchitecture();
            throw new InvalidOperationException(
                $"The Windows App Runtime (Framework + DDLM) for architecture '{arch}' is not registered and could not be installed, " +
                "so the app would fail to start. Restore the project so the matching Windows App SDK runtime is available for that " +
                "architecture, install it manually, or build a self-contained app (WindowsAppSDKSelfContained=true).");
        }

        return true;
    }

    /// <summary>
    /// Resolves the .NET package list from an explicit project file when available (project mode),
    /// otherwise falls back to the current-directory glob used by folder mode. When an effective
    /// <paramref name="framework"/> is supplied, the list is narrowed to that TFM so a multi-targeted
    /// project's other frameworks (which may reference a different Windows App SDK version) don't drive
    /// the runtime version resolution for the framework we actually built. <paramref name="noRestore"/>
    /// forwards <c>--no-restore</c> to <c>dotnet list package</c> so a no-restore run can't trigger an
    /// implicit restore during runtime discovery.
    /// </summary>
    private async Task<DotNetPackageListJson?> ResolveDotNetPackageListAsync(FileInfo? projectFile, string? framework, bool noRestore, PackageGraphSource? packageGraph, CancellationToken cancellationToken)
    {
        var packageList = projectFile is not null
            ? await dotNetService.GetPackageListAsync(projectFile, noRestore: noRestore, packageGraph: packageGraph, cancellationToken: cancellationToken)
            : await FetchDotNetPackageListAsync(cancellationToken);

        // A named project that yields no graph is worth saying out loud: the framework dependency written
        // into the manifest, and the runtime provisioned for it, are then decided without the app's real
        // package list. `dotnet package list` fails this way when the project and project.assets.json are
        // out of sync — for instance under --no-restore after a build in another configuration.
        if (projectFile is not null && packageList is null)
        {
            logger.LogWarning(
                "{UISymbol} Could not read the package list for '{File}', so its Windows App SDK dependency is inferred without it. If the app fails to activate, re-run without --no-restore.",
                UiSymbols.Warning, projectFile.Name);
        }

        return FilterPackageListToFramework(packageList, framework);
    }

    /// <summary>
    /// Restricts each project's <see cref="DotNetProject.Frameworks"/> to the one matching the built
    /// TFM. <c>dotnet list package</c> has no <c>--framework</c> filter, so a multi-targeted project
    /// returns every TFM; without this a sibling framework's Windows App SDK version could be picked.
    /// A null/empty <paramref name="framework"/>, or a project that doesn't list the TFM, is left as-is
    /// (fail-open) so single-targeted and folder-mode flows are unchanged.
    /// </summary>
    internal static DotNetPackageListJson? FilterPackageListToFramework(DotNetPackageListJson? packageList, string? framework)
    {
        if (packageList?.Projects is null || string.IsNullOrEmpty(framework))
        {
            return packageList;
        }

        var filteredProjects = packageList.Projects
            .Select(project =>
            {
                var frameworks = project.Frameworks;
                if (frameworks is null || frameworks.Count <= 1)
                {
                    return project;
                }

                var matched = frameworks
                    .Where(f => string.Equals(f.Framework, framework, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // Only narrow when the TFM is actually present; otherwise keep all frameworks so an
                // unexpected moniker mismatch doesn't blank out the SDK reference entirely.
                return matched.Count > 0 ? project with { Frameworks = matched } : project;
            })
            .ToList();

        return packageList with { Projects = filteredProjects };
    }

    /// <summary>
    /// True when the resolved package list references the Windows App SDK (top-level or transitive).
    /// Used to skip Windows App Runtime preparation for apps that don't use the SDK.
    /// </summary>
    internal static bool ReferencesWindowsAppSdk(DotNetPackageListJson packageList)
    {
        if (packageList.Projects is null)
        {
            return false;
        }

        return packageList.Projects
            .SelectMany(p => p.Frameworks ?? [])
            .SelectMany(f => (f.TopLevelPackages ?? []).Concat(f.TransitivePackages ?? []))
            .Any(pkg => pkg.Id.StartsWith("Microsoft.WindowsAppSDK", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Ensures that the Windows App Runtime framework MSIX packages are installed on the machine.
    /// Locates the runtime MSIX directory from the NuGet package cache and installs any
    /// missing or outdated packages (Framework, DDLM, Singleton, Main) via Add-AppxPackage.
    /// </summary>
    private async Task<IReadOnlyList<(string Name, string Version)>> EnsureWindowsAppRuntimeInstalledAsync(DotNetPackageListJson? dotNetPackageList, string? architecture, TaskContext taskContext, CancellationToken cancellationToken, bool requireExactVersion = false)
    {
        var msixDir = await GetRuntimeMsixDirAsync(dotNetPackageList, taskContext, cancellationToken, requireExactVersion);
        if (msixDir == null)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Warning} Could not locate Windows App Runtime MSIX packages. The runtime may need to be installed manually.");
            return Array.Empty<(string, string)>();
        }

        var (installedCount, errorCount, runtimePackages) = await windowsAppRuntimeService.InstallWindowsAppRuntimeAsync(msixDir, taskContext, cancellationToken, architecture);

        if (errorCount > 0)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Warning} {errorCount} runtime package(s) failed to install. The app may not launch correctly.");
        }
        else if (installedCount > 0)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Check} Installed {installedCount} Windows App Runtime package(s)");
        }

        return runtimePackages;
    }

    private async Task EmbedMsixIdentityToExeAsync(FileInfo exePath, MsixIdentityResult identityInfo, TaskContext taskContext, CancellationToken cancellationToken)
    {
        // Create the MSIX element for the win32 manifest
        string assemblyIdentity = $@"<assemblyIdentity version=""1.0.0.0"" name=""{SecurityElement.Escape(identityInfo.PackageName)}"" type=""win32""/>";
        var existingManifestPath = CreateTempManifestFile("extracted");

        try
        {
            bool hasExistingManifest = await TryExtractManifestFromExeAsync(exePath, existingManifestPath, taskContext, cancellationToken);
            if (hasExistingManifest)
            {
                taskContext.AddDebugMessage("Existing manifest found in executable, checking for a top-level AssemblyIdentity...");
                var existingManifestContent = await File.ReadAllTextAsync(existingManifestPath.FullName, Encoding.UTF8, cancellationToken);
                if (HasTopLevelAssemblyIdentity(existingManifestContent))
                {
                    taskContext.AddDebugMessage("Top-level AssemblyIdentity already present in manifest, will not add a new one.");
                    assemblyIdentity = string.Empty;
                }
                else
                {
                    // A manifest with only a nested/dependency <assemblyIdentity> (e.g.
                    // Microsoft.Windows.Common-Controls) does not give the assembly its own
                    // identity, so we still add a top-level one alongside the <msix> element.
                    taskContext.AddDebugMessage("No top-level AssemblyIdentity found, adding one so identity can be granted.");
                }
            }
            else
            {
                // A bare exe (Rust / C++ / trimmed .NET) has no embedded manifest; mt.exe will
                // create one from ours, so it must carry the top-level <assemblyIdentity>.
                taskContext.AddDebugMessage("No embedded manifest in executable, adding a top-level AssemblyIdentity.");
            }
        }
        finally
        {
            TryDeleteFile(existingManifestPath);
        }

        var manifestContent = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<assembly xmlns=""urn:schemas-microsoft-com:asm.v1"" manifestVersion=""1.0"">
    {assemblyIdentity}
  <msix xmlns=""urn:schemas-microsoft-com:msix.v1""
            publisher=""{SecurityElement.Escape(identityInfo.Publisher)}""
            packageName=""{SecurityElement.Escape(identityInfo.PackageName)}""
            applicationId=""{SecurityElement.Escape(identityInfo.ApplicationId)}""
        />
</assembly>";

        // Create a temporary manifest file
        var tempManifestPath = CreateTempManifestFile("identity");

        try
        {
            await File.WriteAllTextAsync(tempManifestPath.FullName, manifestContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

            // Use mt.exe to merge manifests
            await EmbedManifestFileToExeAsync(exePath, tempManifestPath, taskContext, cancellationToken);
        }
        finally
        {
            TryDeleteFile(tempManifestPath);
        }
    }

    /// <summary>
    /// Returns true if the side-by-side manifest XML has a top-level (root-child)
    /// asm.v1 &lt;assemblyIdentity&gt; element. Unlike a whole-file scan, this ignores nested
    /// &lt;assemblyIdentity&gt; elements (e.g. a &lt;dependency&gt; on
    /// Microsoft.Windows.Common-Controls) and any identically named element from a different
    /// namespace (e.g. asm.v3), which do not grant the assembly its own SxS identity.
    /// Returns false if the content cannot be parsed as XML.
    /// </summary>
    private static bool HasTopLevelAssemblyIdentity(string manifestContent)
    {
        try
        {
            var root = XDocument.Parse(manifestContent).Root;
            return root is not null && root.Elements(AsmV1Ns + "assemblyIdentity").Any();
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates a uniquely named temp manifest file path under the system temp directory. Callers own
    /// deletion. Using the temp directory rather than the executable's own directory avoids silently
    /// clobbering any same-named files a user keeps next to their exe.
    /// </summary>
    private static FileInfo CreateTempManifestFile(string label)
        => new(Path.Join(Path.GetTempPath(), $"winapp_{label}_{Guid.NewGuid():N}.manifest"));

    /// <summary>
    /// Removes any &lt;msix&gt; identity element(s) from a side-by-side manifest file on disk so a
    /// subsequent mt.exe merge can insert a fresh identity without colliding with a stale one.
    /// No-ops if the file cannot be parsed as XML.
    /// </summary>
    private static void RemoveMsixElements(FileInfo manifestPath, TaskContext taskContext)
    {
        try
        {
            var xdoc = XDocument.Load(manifestPath.FullName);
            var msixElements = xdoc.Descendants(MsixV1Ns + "msix").ToList();
            if (msixElements.Count == 0)
            {
                return;
            }

            foreach (var element in msixElements)
            {
                element.Remove();
            }

            xdoc.Save(manifestPath.FullName);
            taskContext.AddDebugMessage("Removed existing <msix> identity from embedded manifest before merge.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or System.Xml.XmlException)
        {
            taskContext.AddDebugMessage($"Could not strip existing <msix> identity (continuing): {ex.Message}");
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
        TaskContext taskContext,
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

        taskContext.AddDebugMessage($"Processing executable: {exePath}");
        taskContext.AddDebugMessage($"Embedding manifest: {manifestPath}");

        var tempManifestPath = CreateTempManifestFile("extracted");
        var mergedManifestPath = CreateTempManifestFile("merged");

        try
        {
            bool hasExistingManifest = await TryExtractManifestFromExeAsync(exePath, tempManifestPath, taskContext, cancellationToken);

            if (hasExistingManifest)
            {
                // Drop any <msix> identity already embedded in the exe. Otherwise mt.exe refuses to
                // merge when the new identity differs from the old one (error c1010001: "Values of
                // attribute ... not equal in different manifest snippets"), which would make
                // re-branding an exe a hard failure instead of an idempotent update.
                RemoveMsixElements(tempManifestPath, taskContext);

                taskContext.AddDebugMessage("Merging with existing manifest using mt.exe...");

                // Use mt.exe to merge existing manifest with new manifest
                await RunMtToolAsync($@"-manifest ""{tempManifestPath}"" ""{manifestPath}"" -out:""{mergedManifestPath}""", true, taskContext, cancellationToken);
            }
            else
            {
                taskContext.AddDebugMessage("No existing manifest, using new manifest as-is");

                // No existing manifest, use the new manifest directly
                manifestPath.CopyTo(mergedManifestPath.FullName);
            }

            taskContext.AddDebugMessage("Embedding merged manifest into executable...");

            // Update the executable with merged manifest
            await RunMtToolAsync($@"-manifest ""{mergedManifestPath}"" -outputresource:""{exePath}"";#1", true, taskContext, cancellationToken);

            taskContext.AddDebugMessage($"{UiSymbols.Check} Successfully embedded manifest into: {exePath}");
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

    private async Task<bool> TryExtractManifestFromExeAsync(FileInfo exePath, FileInfo tempManifestPath, TaskContext taskContext, CancellationToken cancellationToken)
    {
        taskContext.AddDebugMessage("Extracting current manifest from executable...");

        // Extract current manifest from the executable
        bool hasExistingManifest = false;
        try
        {
            await RunMtToolAsync($@"-inputresource:""{exePath}"";#1 -out:""{tempManifestPath}""", false, taskContext, cancellationToken);
            tempManifestPath.Refresh();
            hasExistingManifest = tempManifestPath.Exists;
        }
        catch
        {
            taskContext.AddDebugMessage("No existing manifest found in executable");
        }

        return hasExistingManifest;
    }

    private async Task RunMtToolAsync(string arguments, bool printErrors, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        // Use BuildToolsService to run mt.exe
        await buildToolsService.RunBuildToolAsync(new GenericTool("mt.exe"), arguments, taskContext, printErrors, cancellationToken: cancellationToken);
    }

    /// <param name="originalManifestPath">Path to the original appxmanifest.xml</param>
    /// <param name="entryPointPath">Path to the entryPoint/executable that the manifest should reference</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple containing the debug manifest path and modified identity info</returns>
    public async Task<(FileInfo debugManifestPath, MsixIdentityResult debugIdentity)> GenerateSparsePackageStructureAsync(
        FileInfo originalManifestPath,
        string entryPointPath,
        bool keepIdentity,
        DotNetPackageListJson? dotNetPackageList,
        TaskContext taskContext,
        CancellationToken cancellationToken = default)
    {
        var winappDir = winappDirectoryService.GetLocalWinappDirectory();
        var debugDir = new DirectoryInfo(Path.Combine(winappDir.FullName, "debug"));

        taskContext.AddDebugMessage($"{UiSymbols.Note} Creating sparse package structure in: {debugDir.FullName}");

        // Step 1: Create debug directory, removing existing one if present
        if (debugDir.Exists)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Trash} Removing existing debug directory...");
            debugDir.Delete(recursive: true);
        }

        debugDir.Create();
        taskContext.AddDebugMessage($"{UiSymbols.Folder} Created debug directory");

        // Step 2: Parse original manifest to get identity and assets
        var originalManifestContent = await File.ReadAllTextAsync(originalManifestPath.FullName, Encoding.UTF8, cancellationToken);

        // Resolve placeholders in memory (never write back to the original manifest)
        if (PlaceholderHelper.ContainsPlaceholders(originalManifestContent))
        {
            var nameWithoutExtension = Path.GetFileNameWithoutExtension(entryPointPath);
            var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlaceholderHelper.TargetNameToken] = nameWithoutExtension
            };

            // Also replace the Executable attribute if it has a placeholder
            var doc = AppxManifestDocument.Parse(originalManifestContent);
            if (doc.ApplicationExecutable != null && PlaceholderHelper.ContainsPlaceholders(doc.ApplicationExecutable))
            {
                var exeName = Path.GetFileName(entryPointPath);
                doc.ApplicationExecutable = exeName;
                originalManifestContent = doc.ToXml();
            }

            originalManifestContent = PlaceholderHelper.ReplacePlaceholders(originalManifestContent, replacements);
            PlaceholderHelper.ThrowIfUnresolvedPlaceholders(originalManifestContent);

            taskContext.AddDebugMessage($"{UiSymbols.Note} Resolved manifest placeholders for debug identity");
        }

        var originalIdentity = ParseAppxManifestAsync(originalManifestContent);

        // Step 3: Create debug identity (optionally with ".debug" suffix)
        var debugIdentity = keepIdentity ? originalIdentity : CreateDebugIdentity(originalIdentity);

        // Step 4: Modify manifest for sparse packaging and debug identity
        (var debugManifestContent, _) = await UpdateAppxManifestContentAsync(
            originalManifestContent,
            debugIdentity,
            entryPointPath,
            entryPointPath,
            sparse: true,
            selfContained: false,
            dotNetPackageList,
            taskContext,
            cancellationToken);

        taskContext.AddDebugMessage($"{UiSymbols.Note} Modified manifest for sparse packaging and debug identity");

        // Step 5: Write debug manifest
        var debugManifestPath = new FileInfo(Path.Combine(debugDir.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(debugManifestPath.FullName, debugManifestContent, Encoding.UTF8, cancellationToken);

        taskContext.AddDebugMessage($"{UiSymbols.Files} Created debug manifest: {debugManifestPath.FullName}");

        // Step 6: Copy all assets and generate resources.pri
        var entryPointDir = Path.GetDirectoryName(entryPointPath);
        if (!string.IsNullOrEmpty(entryPointDir))
        {
            var entryPointDirInfo = new DirectoryInfo(entryPointDir);
            var originalManifestDir = originalManifestPath.DirectoryName;
            var expandedFiles = MrtAssetHelper.GetExpandedManifestReferencedFiles(originalManifestPath, taskContext);

            if (!string.Equals(originalManifestDir, entryPointDirInfo.FullName, StringComparison.OrdinalIgnoreCase))
            {
                MrtAssetHelper.CopyAllAssets(expandedFiles, entryPointDirInfo, taskContext);
            }
            else
            {
                taskContext.AddDebugMessage($"{UiSymbols.Warning} Manifest directory and target directory are the same, skipping assets copy");
            }

            // Generate resources.pri in a temporary staging directory, then copy only the
            // final resources.pri into the ExternalLocation (entry point directory). This avoids
            // leaving intermediate files such as priconfig.xml and pri.resfiles alongside app output.
            // Sparse packages look for resources.pri in the ExternalLocation, not alongside the manifest.
            if (expandedFiles.Count > 0)
            {
                string? priStagingDir = null;

                try
                {
                    taskContext.AddDebugMessage($"{UiSymbols.Note} Generating PRI for asset resource resolution...");
                    var priResourceCandidates = expandedFiles.Select(file => file.RelativePath).ToArray();

                    priStagingDir = Path.Combine(
                        Path.GetTempPath(),
                        "WinAppCli-Pri-" + Guid.NewGuid().ToString("N"));

                    var priStagingDirInfo = Directory.CreateDirectory(priStagingDir);
                    MrtAssetHelper.CopyAllAssets(expandedFiles, priStagingDirInfo, taskContext);

                    await priService.CreatePriConfigAsync(
                        priStagingDirInfo,
                        taskContext,
                        precomputedPriResourceCandidates: priResourceCandidates,
                        cancellationToken: cancellationToken);
                    await priService.GeneratePriFileAsync(priStagingDirInfo, taskContext, cancellationToken: cancellationToken);

                    var stagedPriPath = Path.Combine(priStagingDirInfo.FullName, "resources.pri");
                    var targetPriPath = Path.Combine(entryPointDirInfo.FullName, "resources.pri");

                    if (!File.Exists(stagedPriPath))
                    {
                        throw new FileNotFoundException("Generated resources.pri was not found in the staging directory.", stagedPriPath);
                    }

                    if (File.Exists(targetPriPath))
                    {
                        File.Delete(targetPriPath);
                    }

                    File.Copy(stagedPriPath, targetPriPath);
                    taskContext.AddDebugMessage($"{UiSymbols.Check} Generated resources.pri in entry point directory");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "{UISymbol} Failed to generate resources.pri: {Message}. The app may not launch correctly. Re-run with --verbose for details.", UiSymbols.Warning, ex.Message);
                    taskContext.AddDebugMessage($"{UiSymbols.Warning} PRI generation error details: {ex}");
                }
                finally
                {
                    if (!string.IsNullOrWhiteSpace(priStagingDir) && Directory.Exists(priStagingDir))
                    {
                        try
                        {
                            Directory.Delete(priStagingDir, recursive: true);
                        }
                        catch (Exception cleanupEx)
                        {
                            taskContext.AddDebugMessage($"{UiSymbols.Warning} Failed to clean up PRI staging directory '{priStagingDir}': {cleanupEx.Message}");
                        }
                    }
                }
            }
        }

        return (debugManifestPath, debugIdentity);
    }

    /// <summary>
    /// Auto-detects ProcessorArchitecture from the executable PE header and sets it in the manifest
    /// if not already present. Mirrors the logic used by all three code paths (run, create-debug-identity, package).
    /// Without this, ARM64 Windows resolves framework dependencies to ARM64 DLLs even for x64 apps.
    /// </summary>
    /// <returns>The effective architecture (detected or existing), or null if unknown.</returns>
    internal static (string manifestContent, string? architecture) AutoDetectProcessorArchitecture(string manifestContent, string exePath, TaskContext taskContext)
    {
        var detectedArch = PeHelper.DetectPeArchitecture(exePath);
        if (detectedArch == null)
        {
            // Can't detect — return whatever the manifest already has
            var existingDoc = AppxManifestDocument.Parse(manifestContent);
            return (manifestContent, existingDoc.IdentityProcessorArchitecture);
        }

        var doc = AppxManifestDocument.Parse(manifestContent);
        var existingArch = doc.IdentityProcessorArchitecture;

        if (existingArch == null)
        {
            doc.IdentityProcessorArchitecture = detectedArch;
            taskContext.AddDebugMessage($"{UiSymbols.Note} Auto-detected ProcessorArchitecture: {detectedArch}");
            return (doc.ToXml(), detectedArch);
        }

        if (!string.Equals(existingArch, detectedArch, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(existingArch, "neutral", StringComparison.OrdinalIgnoreCase))
        {
            taskContext.AddStatusMessage($"{UiSymbols.Warning} Manifest ProcessorArchitecture is '{existingArch}' but the executable is {detectedArch}. This may cause runtime failures.");
        }

        return (manifestContent, existingArch);
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
    /// Copies files referenced in the manifest to the target directory.
    /// </summary>
    /// <summary>
    /// Checks if a package with the given name exists and unregisters it if found
    /// </summary>
    /// <param name="packageName">The name of the package to check and unregister</param>
    /// <param name="taskContext">Task context for debug output</param>
    /// <param name="preserveAppData">When true, preserves the package's application data during removal</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if package was found and unregistered, false if no package was found</returns>
    public async Task<bool> UnregisterExistingPackageAsync(
        string packageName,
        TaskContext taskContext,
        string? publisher = null,
        bool preserveAppData = true,
        CancellationToken cancellationToken = default)
    {
        taskContext.AddDebugMessage($"{UiSymbols.Trash} Checking for existing package...");

        try
        {
            // Inspect installed packages first so we can make a safe, per-package decision
            // about non-dev-mode installations (where PreserveApplicationData is rejected
            // and a blind removal would wipe user data).
            // NOTE: despite its name, IPackageRegistrationService.FindDevPackages returns
            // *all* same-name packages (dev-mode AND non-dev-mode); the IsDevelopmentMode
            // flag on each entry is what we classify on below.
            var installed = packageRegistrationService.FindDevPackages(packageName)
                .Where(package =>
                    publisher is null ||
                    string.Equals(package.Publisher, publisher, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (installed.Count == 0)
            {
                taskContext.AddDebugMessage($"{UiSymbols.Note} No existing package found");
                return false;
            }

            var cwd = Path.GetFullPath(currentDirectoryProvider.GetCurrentDirectory());

            // First pass: classify packages and refuse the whole operation if any
            // out-of-tree non-dev install is present. Doing this up front (instead of
            // mid-loop) prevents the first removal from racing ahead and wiping data
            // that the safety check on a *later* package was meant to protect.
            foreach (var pkg in installed)
            {
                if (pkg.IsDevelopmentMode)
                {
                    continue;
                }

                if (!IsPathInsideDirectory(pkg.InstallLocation, cwd))
                {
                    var locationDescription = string.IsNullOrEmpty(pkg.InstallLocation)
                        ? "."
                        : $" at '{Path.GetFullPath(pkg.InstallLocation)}'.";
                    throw new InvalidOperationException(
                        $"A package with the same identity ('{pkg.FullName}') is already installed " +
                        $"as a non-development-mode package" + locationDescription + Environment.NewLine +
                        "Remove it manually if you intended to replace it:" + Environment.NewLine +
                        $"  Get-AppxPackage {packageName} | Remove-AppxPackage");
                }
            }

            // Second pass: safe to remove each package individually using its full name.
            // Using the per-FullName API ensures one iteration cannot wipe packages the
            // first pass approved or rejected separately.
            var anyRemoved = false;
            var anyFailed = false;
            foreach (var pkg in installed)
            {
                bool removed;
                if (pkg.IsDevelopmentMode)
                {
                    removed = await packageRegistrationService.UnregisterByFullNameAsync(pkg.FullName, preserveAppData, cancellationToken);
                }
                else
                {
                    // Verified in-tree above: safe to remove (app data deleted, since
                    // PreserveApplicationData isn't valid for non-dev-mode packages).
                    taskContext.AddDebugMessage(
                        $"{UiSymbols.Warning} Existing non-dev-mode package {pkg.FullName} is rooted in the current " +
                        $"project tree ({pkg.InstallLocation}); removing it (application data will be deleted).");
                    removed = await packageRegistrationService.UnregisterByFullNameAsync(pkg.FullName, preserveAppData: false, cancellationToken);
                }

                // Windows reports a refused removal as error text rather than an exception. Reporting
                // success here would let `run --clean` continue believing the old registration and its
                // application data were cleared when they are still there.
                if (removed)
                {
                    anyRemoved = true;
                }
                else
                {
                    anyFailed = true;
                    taskContext.AddDebugMessage($"{UiSymbols.Warning} Windows refused to remove {pkg.FullName}.");
                }
            }

            if (anyFailed)
            {
                // THROWN, not returned false. Every caller awaits this method to clear the way before
                // registering, and `false` is also the ordinary "nothing was registered" answer — so
                // returning it here would let `run --clean` carry on and re-register over a package
                // Windows refused to remove, silently keeping the application data --clean promises to
                // delete. InvalidOperationException is already the established signal for an actionable
                // conflict on this path and propagates through the catch below.
                throw new InvalidOperationException(
                    $"Windows refused to remove the existing registration of '{packageName}'. " +
                    "Close the app if it is running, then retry; or remove it manually with " +
                    $"'Get-AppxPackage {packageName} | Remove-AppxPackage'.");
            }

            if (anyRemoved)
            {
                taskContext.AddDebugMessage($"{UiSymbols.Check} Existing package unregistered successfully{(preserveAppData ? " (app data preserved where possible)" : "")}");
            }

            return anyRemoved;
        }
        catch (InvalidOperationException)
        {
            // Surface actionable conflicts (non-dev-mode package outside project tree) to the caller.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Cancellation must propagate so callers (StatusService etc.) can treat it
            // distinctly from a normal "no package removed" outcome.
            throw;
        }
        catch (Exception ex)
        {
            // Other failures (e.g., transient deployment errors during inspection or
            // removal) shouldn't block the caller's overall flow — log and continue,
            // matching prior behavior.
            taskContext.AddDebugMessage($"{UiSymbols.Note} Could not unregister existing package: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Segment-aware containment check: returns true iff <paramref name="candidatePath"/>
    /// lives inside <paramref name="containerPath"/>. Uses <see cref="Path.GetRelativePath"/>
    /// so that sibling directories sharing a string prefix (e.g. <c>C:\proj</c> vs
    /// <c>C:\project2</c>) are correctly treated as outside.
    /// </summary>
    internal static bool IsPathInsideDirectory(string? candidatePath, string containerPath)
    {
        if (string.IsNullOrEmpty(candidatePath))
        {
            return false;
        }

        string fullCandidate;
        string fullContainer;
        try
        {
            fullCandidate = Path.GetFullPath(candidatePath);
            fullContainer = Path.GetFullPath(containerPath);
        }
        catch
        {
            return false;
        }

        var relative = Path.GetRelativePath(fullContainer, fullCandidate);
        if (Path.IsPathRooted(relative))
        {
            // Different volume / UNC root — definitely not contained.
            return false;
        }

        // Reject any traversal out of the container ("..", "..\foo", etc.).
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                             || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Registers a sparse package with external location using Add-AppxPackage
    /// </summary>
    /// <param name="manifestPath">Path to the appxmanifest.xml file</param>
    /// <param name="externalLocation">External location path (typically the working directory)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task RegisterSparsePackageAsync(FileInfo manifestPath, DirectoryInfo externalLocation, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        taskContext.AddDebugMessage($"{UiSymbols.Clipboard} Registering sparse package with external location...");

        try
        {
            await packageRegistrationService.RegisterSparseAsync(
                manifestPath.FullName, externalLocation.FullName, cancellationToken);

            taskContext.AddDebugMessage($"{UiSymbols.Check} Sparse package registered successfully");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to register sparse package: {ex.Message}", ex);
        }
    }

    public async Task RegisterLooseLayoutPackageAsync(FileInfo manifestPath, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        taskContext.AddDebugMessage($"{UiSymbols.Clipboard} Registering loose layout package...");

        try
        {
            await packageRegistrationService.RegisterLooseLayoutAsync(
                manifestPath.FullName, cancellationToken);

            taskContext.AddDebugMessage($"{UiSymbols.Check} Package registered successfully");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to register package: {ex.Message}", ex);
        }
    }
}
