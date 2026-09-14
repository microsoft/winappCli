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
    /// Delivers a package the Windows App SDK MSIX targets already produced (see
    /// <c>ProjectRunService.PublishNativeMsixAsync</c>): copies it to the final destination per the same
    /// precedence as folder packaging (<c>--output</c> file/dir, else the current directory) and signs it
    /// once if requested. winapp never repackages the SDK output — the payload (including the Native AOT
    /// native binary) is exactly what the SDK produced.
    /// </summary>
    public async Task<CreateMsixPackageResult> DeliverNativeMsixAsync(
        FileInfo producedMsix,
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
        // The produced package carries its own AppxManifest.xml; extract it for publisher resolution and
        // certificate/publisher validation, mirroring the folder-mode signing contract.
        var manifestTemp = new FileInfo(Path.Combine(Path.GetTempPath(), $"winapp-native-manifest-{Guid.NewGuid():N}.xml"));
        using (var archive = ZipFile.OpenRead(producedMsix.FullName))
        {
            var entry = archive.GetEntry("AppxManifest.xml")
                ?? throw new InvalidOperationException(
                    $"The produced package '{producedMsix.Name}' contains no AppxManifest.xml.");
            entry.ExtractToFile(manifestTemp.FullName, overwrite: true);
        }

        try
        {
            var manifestDoc = AppxManifestDocument.Load(manifestTemp.FullName);
            var extractedPublisher = publisher ?? manifestDoc.IdentityPublisher;

            var finalMsixPath = ResolveNativeDeliveryPath(producedMsix, output, name);
            if (!finalMsixPath.Directory!.Exists)
            {
                finalMsixPath.Directory.Create();
            }

            // Stage → sign → atomic replace: build the artifact at a sibling temp path (same directory, so
            // the final move is atomic) and move it over the destination only after signing succeeds. A
            // sign/validation failure therefore never clobbers an existing artifact (spec §5/§11). The SDK
            // package is already complete, so staging is a copy, never a repackage.
            var stagingMsix = CreateStagingSiblingPath(finalMsixPath);
            File.Copy(producedMsix.FullName, stagingMsix.FullName, overwrite: true);
            try
            {
                var signed = false;
                if (autoSign)
                {
                    await SignMsixPackageAsync(
                        finalMsixPath.Directory!, certPassword, generateDevCert, installDevCert,
                        Path.GetFileNameWithoutExtension(finalMsixPath.Name), extractedPublisher,
                        stagingMsix, certPath, manifestTemp, taskContext, cancellationToken, timestampUrl);
                    signed = true;
                }

                File.Move(stagingMsix.FullName, finalMsixPath.FullName, overwrite: true);
                taskContext.AddDebugMessage($"{UiSymbols.Package} Delivered native package to {finalMsixPath.FullName}");
                return new CreateMsixPackageResult(finalMsixPath, signed);
            }
            catch
            {
                try
                {
                    stagingMsix.Refresh();
                    if (stagingMsix.Exists)
                    {
                        stagingMsix.Delete();
                    }
                }
                catch
                {
                    // Best-effort cleanup of the staged (unpublished) artifact.
                }
                throw;
            }
        }
        finally
        {
            try
            {
                manifestTemp.Refresh();
                if (manifestTemp.Exists)
                {
                    manifestTemp.Delete();
                }
            }
            catch
            {
                // Best-effort cleanup of the extracted temp manifest.
            }
        }
    }

    /// <summary>
    /// Resolves the final delivery path for a native package. Folder-mode precedence: an explicit
    /// <c>--output</c> <c>.msix</c> file wins (over <c>--name</c>); an <c>--output</c> directory (or the
    /// current directory) hosts the file. <c>--name</c> overrides only the filename prefix, preserving the
    /// SDK's <c>_&lt;Version&gt;_&lt;arch&gt;.msix</c> suffix.
    /// </summary>
    private FileInfo ResolveNativeDeliveryPath(FileInfo producedMsix, FileInfo? output, string? name)
    {
        if (output != null
            && Path.HasExtension(output.Name)
            && string.Equals(Path.GetExtension(output.Name), ".msix", StringComparison.OrdinalIgnoreCase))
        {
            return output;
        }

        string fileName;
        if (name is { Length: > 0 })
        {
            // Sanitize the requested name to a bare MSIX identity token (no path separators), matching
            // folder-mode packaging, so --name can only set the filename prefix and never redirect the
            // artifact outside the selected destination directory.
            var cleanName = ManifestService.CleanPackageName(name);
            var produced = producedMsix.Name;
            var underscore = produced.IndexOf('_');
            fileName = underscore > 0 ? cleanName + produced[underscore..] : $"{cleanName}.msix";
        }
        else
        {
            fileName = producedMsix.Name;
        }

        var destinationDir = output?.FullName ?? currentDirectoryProvider.GetCurrentDirectoryInfo().FullName;
        return new FileInfo(Path.Combine(destinationDir, fileName));
    }
}
