// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal partial class MsixService
{
    /// <summary>
    /// Composes one architecture <c>.msixbundle</c> from packages produced per-slice by project mode (each
    /// already a complete <c>.msix</c>). Validates cross-slice identity/version and distinct architecture,
    /// bundles the produced files via <see cref="IBundleService.CreateBundleAsync"/> (never repackaging
    /// them), signs the outer bundle once if requested, and delivers it transactionally per <c>--output</c>
    /// precedence (spec §8).
    /// </summary>
    public async Task<CreateMsixBundleResult> CreateBundleFromPackagesAsync(
        IReadOnlyList<FileInfo> sliceMsixFiles,
        FileInfo? output,
        string? name,
        TaskContext taskContext,
        bool autoSign,
        FileInfo? certPath,
        string certPassword,
        bool generateDevCert,
        bool installDevCert,
        string? publisher,
        string? timestampUrl,
        CancellationToken cancellationToken)
    {
        if (sliceMsixFiles.Count < 2)
        {
            throw new InvalidOperationException("A bundle requires at least two package slices.");
        }

        // Extract each slice's manifest for cross-slice validation, naming, and signing-publisher resolution.
        var extractedManifests = new List<FileInfo>();
        try
        {
            var slices = new List<(FileInfo Msix, AppxManifestDocument Doc, string Arch)>();
            foreach (var msix in sliceMsixFiles)
            {
                var manifestTemp = new FileInfo(Path.Combine(Path.GetTempPath(), $"winapp-bundle-manifest-{Guid.NewGuid():N}.xml"));
                extractedManifests.Add(manifestTemp);
                using (var archive = ZipFile.OpenRead(msix.FullName))
                {
                    var entry = archive.GetEntry("AppxManifest.xml")
                        ?? throw new InvalidOperationException($"Bundle slice '{msix.Name}' contains no AppxManifest.xml.");
                    entry.ExtractToFile(manifestTemp.FullName, overwrite: true);
                }
                var doc = AppxManifestDocument.Load(manifestTemp.FullName);
                var arch = doc.IdentityProcessorArchitecture
                    ?? throw new InvalidOperationException($"Bundle slice '{msix.Name}' has no Identity/@ProcessorArchitecture.");
                slices.Add((msix, doc, arch));
            }

            // Cross-slice invariants: identical Identity Name/Publisher/Version, distinct architectures.
            var reference = slices[0].Doc;
            var referenceName = reference.IdentityName;
            var referenceVersion = reference.IdentityVersion;
            foreach (var (msix, doc, _) in slices)
            {
                if (!string.Equals(doc.IdentityName, referenceName, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Bundle slices disagree on Identity/@Name ('{referenceName}' vs '{doc.IdentityName}' in {msix.Name}).");
                }
                if (!string.Equals(doc.IdentityPublisher, reference.IdentityPublisher, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Bundle slices disagree on Identity/@Publisher (in {msix.Name}).");
                }
                if (!string.Equals(doc.IdentityVersion, referenceVersion, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Bundle slices disagree on Identity/@Version ('{referenceVersion}' vs '{doc.IdentityVersion}' in {msix.Name}).");
                }
            }
            var duplicateArch = slices.GroupBy(s => s.Arch, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (duplicateArch != null)
            {
                throw new InvalidOperationException($"Two bundle slices target the same architecture '{duplicateArch.Key}'.");
            }

            if (!MsixVersion.TryParse(referenceVersion, out var bundleVersion))
            {
                throw new InvalidOperationException($"Identity/@Version '{referenceVersion}' is not a canonical four-part MSIX version (e.g. 1.2.3.4).");
            }

            var finalPackageName = ManifestService.CleanPackageName(name ?? referenceName ?? "Package");
            var sortedArches = slices.Select(s => s.Arch).OrderBy(a => a, StringComparer.OrdinalIgnoreCase);
            var defaultBundleFileName = $"{finalPackageName}_{referenceVersion}_{string.Join("_", sortedArches)}.msixbundle";

            // Output precedence: an explicit --output .msixbundle file, else an --output directory, else cwd.
            FileInfo finalBundlePath;
            if (output != null && Path.HasExtension(output.Name)
                && string.Equals(Path.GetExtension(output.Name), ".msixbundle", StringComparison.OrdinalIgnoreCase))
            {
                finalBundlePath = output;
            }
            else
            {
                var destinationDir = output?.FullName ?? currentDirectoryProvider.GetCurrentDirectoryInfo().FullName;
                finalBundlePath = new FileInfo(Path.Combine(destinationDir, defaultBundleFileName));
            }
            if (!finalBundlePath.Directory!.Exists)
            {
                finalBundlePath.Directory.Create();
            }

            // Stage → sign → atomic replace: bundle to a sibling temp path, sign it, then move over the
            // destination only after success so an existing artifact is never clobbered on failure.
            var stagingBundle = CreateStagingSiblingPath(finalBundlePath);
            try
            {
                await bundleService.CreateBundleAsync(sliceMsixFiles, stagingBundle, taskContext, bundleVersion, cancellationToken);

                var signed = false;
                if (autoSign)
                {
                    await SignMsixPackageAsync(
                        finalBundlePath.Directory!, certPassword, generateDevCert, installDevCert,
                        finalPackageName, publisher ?? reference.IdentityPublisher,
                        stagingBundle, certPath, extractedManifests[0], taskContext, cancellationToken, timestampUrl);
                    signed = true;
                }

                File.Move(stagingBundle.FullName, finalBundlePath.FullName, overwrite: true);
                var sliceResults = slices
                    .Select(s => new BundleSliceInfo(s.Msix.Directory!, s.Arch, s.Msix))
                    .ToList();
                return new CreateMsixBundleResult(finalBundlePath, signed, sliceResults);
            }
            catch
            {
                try
                {
                    stagingBundle.Refresh();
                    if (stagingBundle.Exists)
                    {
                        stagingBundle.Delete();
                    }
                }
                catch
                {
                    // Best-effort cleanup of the staged bundle.
                }
                throw;
            }
        }
        finally
        {
            foreach (var manifest in extractedManifests)
            {
                try
                {
                    manifest.Refresh();
                    if (manifest.Exists)
                    {
                        manifest.Delete();
                    }
                }
                catch
                {
                    // Best-effort cleanup of extracted temp manifests.
                }
            }
        }
    }
}
