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

            // The SDK package is already a complete artifact, so delivery is a copy (overwrite per the
            // existing pack policy), never a repackage.
            File.Copy(producedMsix.FullName, finalMsixPath.FullName, overwrite: true);
            taskContext.AddDebugMessage($"{UiSymbols.Package} Delivered native package to {finalMsixPath.FullName}");

            var signed = false;
            if (autoSign)
            {
                await SignMsixPackageAsync(
                    finalMsixPath.Directory!, certPassword, generateDevCert, installDevCert,
                    Path.GetFileNameWithoutExtension(finalMsixPath.Name), extractedPublisher,
                    finalMsixPath, certPath, manifestTemp, taskContext, cancellationToken);
                signed = true;
            }

            return new CreateMsixPackageResult(finalMsixPath, signed);
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
            var produced = producedMsix.Name;
            var underscore = produced.IndexOf('_');
            fileName = underscore > 0 ? name + produced[underscore..] : $"{name}.msix";
        }
        else
        {
            fileName = producedMsix.Name;
        }

        var destinationDir = output?.FullName ?? currentDirectoryProvider.GetCurrentDirectoryInfo().FullName;
        return new FileInfo(Path.Combine(destinationDir, fileName));
    }
}
