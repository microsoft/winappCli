// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Tools;

namespace WinApp.Cli.Services;

internal class BundleService(
    IBuildToolsService buildToolsService,
    ILogger<BundleService> logger) : IBundleService
{
    public async Task CreateBundleAsync(IReadOnlyList<FileInfo> msixFiles, FileInfo output, TaskContext taskContext, MsixVersion? bundleVersion = null, CancellationToken cancellationToken = default)
    {
        if (msixFiles.Count == 0)
        {
            throw new ArgumentException("At least one .msix file is required to create a bundle.", nameof(msixFiles));
        }

        // Create a fresh dedicated directory containing only the .msix files for bundling.
        // This avoids stale files or naming collisions affecting the bundle.
        var bundleStagingDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "winapp", $"bundle-{Guid.NewGuid():N}"));
        bundleStagingDir.Create();

        try
        {
            // Copy all intermediate .msix files into the staging directory. Disambiguate any duplicate file
            // names (e.g. two architecture slices both named Fixed.msix when the identity name is forced with
            // -p AppxPackageName) so no slice silently overwrites another and drops its architecture from the
            // bundle — makeappx reads each package's architecture from its own manifest, not its file name.
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var msixFile in msixFiles)
            {
                var destName = msixFile.Name;
                if (!usedNames.Add(destName))
                {
                    var stem = Path.GetFileNameWithoutExtension(destName);
                    var extension = Path.GetExtension(destName);
                    var suffix = 1;
                    do
                    {
                        destName = $"{stem}_{suffix++}{extension}";
                    }
                    while (!usedNames.Add(destName));
                    taskContext.AddDebugMessage($"{UiSymbols.Warning} Bundle slice name collision on '{msixFile.Name}'; staged as '{destName}' so no architecture is dropped.");
                }

                var destPath = Path.Join(bundleStagingDir.FullName, Path.GetFileName(destName));
                msixFile.CopyTo(destPath, overwrite: true);
                taskContext.AddDebugMessage($"Staged for bundle: {destName}");
            }

            // Ensure output directory exists
            output.Directory?.Create();

            var inputPath = LongPathHelper.EnsureExtendedLengthPrefix(Path.TrimEndingDirectorySeparator(bundleStagingDir.FullName));
            var outputPath = LongPathHelper.EnsureExtendedLengthPrefix(output.FullName);
            var makeappxArguments = $@"bundle /o /d ""{inputPath}"" /p ""{outputPath}""";

            // Stamp Bundle.Identity/@Version to match the (validated, consistent) slice
            // Identity/@Version.
            if (bundleVersion != null)
            {
                makeappxArguments += $@" /bv ""{bundleVersion}""";
            }

            taskContext.AddDebugMessage($"Creating MSIX bundle with {msixFiles.Count} package(s)...");
            logger.LogDebug("Running makeappx bundle: {Arguments}", makeappxArguments);

            await buildToolsService.RunBuildToolAsync(new MakeAppxTool(), makeappxArguments, taskContext, cancellationToken: cancellationToken);

            taskContext.AddDebugMessage($"Bundle created: {output.FullName}");
        }
        finally
        {
            // Clean up bundle staging directory
            try
            {
                if (bundleStagingDir.Exists)
                {
                    bundleStagingDir.Delete(recursive: true);
                }
            }
            catch
            {
                taskContext.AddDebugMessage($"{UiSymbols.Warning} Could not clean up bundle staging directory: {bundleStagingDir.FullName}");
            }
        }
    }
}
