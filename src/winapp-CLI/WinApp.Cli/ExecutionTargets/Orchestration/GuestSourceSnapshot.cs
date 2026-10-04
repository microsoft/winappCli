// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text.Json;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

internal sealed record GuestSourceFile(string RelativePath, long Length, string Sha256);
internal sealed record GuestSourceManifest(
    string ProjectPath, IReadOnlyList<GuestSourceFile> Files, string? GuestRoot = null, string? ManifestHash = null,
    IReadOnlyList<XamlSourceCoordinateFile>? Coordinates = null, string? CoordinateError = null, bool CoordinatesAdvisory = false,
    IReadOnlyList<XamlSourceExclusion>? CoordinateExclusions = null);
internal sealed record GuestSourceInventory(IReadOnlyList<GuestSourceFile> Files,
    IReadOnlyList<XamlSourceCoordinateFile>? Coordinates = null, string? CoordinateError = null, bool CoordinatesAdvisory = false,
    IReadOnlyList<XamlSourceExclusion>? CoordinateExclusions = null);

internal static class GuestSourceSnapshot
{
    internal const int MaximumFiles = 2048;
    internal const int MaximumFileBytes = 2 * 1024 * 1024;
    internal const long MaximumTotalBytes = 32 * 1024 * 1024;
    internal const string InventoryFileName = ".winapp-source-snapshot.json";
    // About 70 bytes per mapped element; the native tap reads the same bound (DevToolsAuthored.cpp).
    internal const int MaximumInventoryBytes = 16 * 1024 * 1024;

    internal static Dictionary<string, string> ValidateSources(FileInfo project, IReadOnlyList<string> evaluatedXamlFiles)
    {
        if (!project.Exists || project.DirectoryName is null || evaluatedXamlFiles.Count is 0 or > MaximumFiles)
        {
            throw new InvalidOperationException("Host-backed Sandbox comments require a verified project with 1-2048 evaluated XAML source files.");
        }
        if (PathSafety.HasReparsePointOnPath(project.FullName, project.DirectoryName))
        {
            throw new InvalidOperationException("The project source is redirected or inaccessible.");
        }
        var admitted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var item in evaluatedXamlFiles)
        {
            var path = Path.GetFullPath(item, project.DirectoryName);
            var relative = GuestCommentBinding.ValidateRelativeSource(Path.GetRelativePath(project.DirectoryName, path));
            if (PathSafety.HasReparsePointOnPath(path, project.DirectoryName) || !File.Exists(path))
            {
                throw new InvalidOperationException($"XAML source '{relative}' is missing or outside the verified project. Linked external source is not copied.");
            }
            if (admitted.TryAdd(relative, path))
            {
                var length = new FileInfo(path).Length;
                total += length;
                if (length > MaximumFileBytes || total > MaximumTotalBytes)
                {
                    throw new IOException("The XAML source snapshot exceeds its 2 MiB/file or 32 MiB/launch limit.");
                }
            }
        }
        return admitted;
    }

    // A null destination hashes the sources in place; only a Sandbox launch needs copies to deploy.
    internal static async Task<GuestSourceManifest> CreateAsync(
        FileInfo project, IReadOnlyList<string> evaluatedXamlFiles, DirectoryInfo? destination,
        CancellationToken cancellationToken, XamlCompilerArtifacts? compiler = null)
    {
        var admitted = ValidateSources(project, evaluatedXamlFiles);
        if (destination?.Exists == true)
        {
            throw new IOException("The launch source snapshot destination already exists.");
        }

        destination?.Create();
        var files = new List<GuestSourceFile>();
        long total = 0;
        foreach (var (relative, original) in admitted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PathSafety.HasReparsePointOnPath(original, project.DirectoryName!))
            {
                throw new IOException("A XAML source path changed while preparing the snapshot.");
            }
            await using var input = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read);
            var length = input.Length;
            var lastWrite = File.GetLastWriteTimeUtc(input.SafeFileHandle);
            if (length > MaximumFileBytes || total + length > MaximumTotalBytes)
            {
                throw new IOException("The XAML source snapshot exceeds its 2 MiB/file or 32 MiB/launch limit.");
            }
            if (destination is null)
            {
                files.Add(new(relative, length, Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false))));
                total += length;
            }
            else
            {
                var copy = Path.Combine(destination.FullName, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                await using (var output = new FileStream(copy, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    output.Position = 0;
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(output, cancellationToken).ConfigureAwait(false));
                    files.Add(new(relative, output.Length, hash));
                    total += output.Length;
                }
                // Freshness is relative to the app build, not when this launch copied the source.
                File.SetLastWriteTimeUtc(copy, lastWrite);
            }
            if (input.Length != length || File.GetLastWriteTimeUtc(input.SafeFileHandle) != lastWrite)
            {
                throw new IOException("A XAML source changed while preparing the snapshot.");
            }
        }
        var manifest = new GuestSourceManifest(project.FullName, files);
        try
        {
            if (compiler is not null)
            {
                // Admission still requires the deployed disk payload. No build implies loaded-image identity.
                try
                {
                    var capture = XamlSourceCoordinates.Capture(project, manifest, compiler.Input, compiler.Output, compiler.SavedState);
                    manifest = manifest with
                    {
                        Coordinates = capture.Files,
                        CoordinateExclusions = capture.Exclusions,
                        CoordinatesAdvisory = false,
                    };
                }
                catch (FileNotFoundException)
                {
                    manifest = manifest with { Coordinates = XamlSourceCoordinates.CaptureLikely(project, manifest,
                        "A selected compiler artifact is missing; disk XBF and compiler line preservation are unverified.") };
                }
            }
            else
            {
                manifest = manifest with { Coordinates = XamlSourceCoordinates.CaptureLikely(project, manifest,
                    "No selected compiler artifact table; disk XBF and compiler line preservation are unverified.") };
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or
            System.Xml.XmlException or JsonException or ArgumentException)
        {
            manifest = manifest with { Coordinates = null, CoordinateError = $"Compiled XAML coordinates are unavailable: {ex.Message}" };
        }
        return manifest;
    }

    internal static async Task<GuestSourceManifest> DeployAsync(
        PreparedTarget target, GuestSourceManifest manifest, DirectoryInfo snapshot, Guid launchId,
        CancellationToken cancellationToken)
    {
        target.RequireMutationLease();
        ValidateInventory(manifest.Files);
        var inventory = SerializeInventory(manifest);
        if (inventory.Length > MaximumInventoryBytes)
        {
            throw new IOException("The source snapshot inventory exceeds its 16 MiB limit.");
        }
        var scope = new GuestPathScope(GuestRootNames.Artifacts, "devtools-sources-" + launchId.ToString("N"));
        if ((await target.Operations.ListFilesAsync(scope, cancellationToken).ConfigureAwait(false)).Count != 0)
        {
            throw new IOException("The guest source snapshot identity is already in use.");
        }
        foreach (var file in manifest.Files)
        {
            var path = Path.Combine(snapshot.FullName, file.RelativePath);
            if (PathSafety.HasReparsePointOnPath(path, snapshot.FullName))
            {
                throw new IOException("The host source snapshot is redirected or inaccessible.");
            }
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            await VerifyFileAsync(input, file, cancellationToken).ConfigureAwait(false);
            input.Position = 0;
            await target.Operations.PutFileAsync(scope,
                new(file.RelativePath, file.Length, File.GetLastWriteTimeUtc(path).Ticks, file.Sha256),
                input, cancellationToken).ConfigureAwait(false);
        }
        var hash = Convert.ToHexString(SHA256.HashData(inventory));
        using var body = new MemoryStream(inventory, writable: false);
        await target.Operations.PutFileAsync(scope,
            new(InventoryFileName, inventory.Length, DateTime.UtcNow.Ticks, hash), body, cancellationToken).ConfigureAwait(false);
        return manifest with { GuestRoot = GuestPaths.Resolve(target.Capabilities, scope), ManifestHash = hash };
    }

    internal static byte[] SerializeInventory(GuestSourceManifest manifest)
    {
        ValidateInventory(manifest.Files);
        var inventory = JsonSerializer.SerializeToUtf8Bytes(
            new GuestSourceInventory(manifest.Files, manifest.Coordinates, manifest.CoordinateError, manifest.CoordinatesAdvisory,
                manifest.CoordinateExclusions),
            GuestCommentsJsonContext.Default.GuestSourceInventory);
        if (inventory.Length > MaximumInventoryBytes)
        {
            throw new IOException("The source snapshot inventory exceeds its 16 MiB limit.");
        }
        return inventory;
    }

    internal static async Task<IDisposable> OpenReadOnlyAsync(
        DirectoryInfo directory, string expectedInventoryHash, CancellationToken cancellationToken)
    {
        if (!IsHash(expectedInventoryHash))
        {
            throw new InvalidOperationException("The host source snapshot hash is missing or invalid.");
        }
        var lease = new ReadOnlyLease();
        try
        {
            var inventoryPath = Path.Combine(directory.FullName, InventoryFileName);
            var inventoryStream = lease.Open(inventoryPath, directory.FullName);
            if (inventoryStream.Length > MaximumInventoryBytes)
            {
                throw new IOException("The source snapshot inventory exceeds its 16 MiB limit.");
            }
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(inventoryStream, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(hash, expectedInventoryHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The guest source snapshot inventory differs from the host launch.");
            }
            inventoryStream.Position = 0;
            var inventory = await JsonSerializer.DeserializeAsync(inventoryStream,
                GuestCommentsJsonContext.Default.GuestSourceInventory, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The guest source snapshot inventory is empty.");
            ValidateInventory(inventory.Files);
            foreach (var file in inventory.Files)
            {
                var input = lease.Open(Path.Combine(directory.FullName, file.RelativePath), directory.FullName);
                await VerifyFileAsync(input, file, cancellationToken).ConfigureAwait(false);
            }
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static async Task VerifyFileAsync(FileStream input, GuestSourceFile expected, CancellationToken cancellationToken)
    {
        if (input.Length != expected.Length || !string.Equals(
            Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false)),
            expected.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"Source snapshot '{expected.RelativePath}' no longer matches the host capture.");
        }
    }

    private static void ValidateInventory(IReadOnlyList<GuestSourceFile> files)
    {
        if (files is null || files.Count is 0 or > MaximumFiles)
        {
            throw new InvalidDataException("A source snapshot must contain 1-2048 evaluated XAML files.");
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in files)
        {
            if (file is null || file.Length < 0 || file.Length > MaximumFileBytes || !IsHash(file.Sha256))
            {
                throw new InvalidDataException("The source snapshot contains an invalid file description.");
            }
            var name = GuestCommentBinding.ValidateRelativeSource(file.RelativePath);
            total += file.Length;
            if (!names.Add(name) || total > MaximumTotalBytes)
            {
                throw new InvalidDataException("The source snapshot contains duplicates or exceeds its 32 MiB limit.");
            }
        }
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private sealed class ReadOnlyLease : IDisposable
    {
        private readonly List<FileStream> files = [];

        internal FileStream Open(string path, string root)
        {
            if (PathSafety.HasReparsePointOnPath(path, root))
            {
                throw new IOException("The guest source snapshot is redirected or inaccessible.");
            }
            // Held by the retained launcher: source readers can open these files, but writes,
            // replacement and deletion are denied until this application lifetime ends.
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            files.Add(file);
            return file;
        }

        public void Dispose()
        {
            foreach (var file in files)
            {
                file.Dispose();
            }
            files.Clear();
        }
    }
}
