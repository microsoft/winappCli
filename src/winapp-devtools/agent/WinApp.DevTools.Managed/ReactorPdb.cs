// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace WinApp.DevTools.Managed;

/// <summary>Where a method starts in its source, from the portable PDB next to (or embedded in) its assembly.</summary>
internal sealed record ReactorSourceSpan(string File, int Line, int Column, string? ChecksumAlgorithm, string? Checksum);

/// <summary>
/// Reads method locations from portable PDBs. Runs in the app's own process, where the assembly and its PDB are, so
/// the inspector, the overlay and the CLI share one answer. Readers are opened once per assembly and kept.
/// </summary>
internal static class ReactorPdb
{
    private static readonly Guid Sha1 = new("ff1816ec-aa5e-4d10-87f7-6f4963833460");
    private static readonly Guid Sha256 = new("8829d00f-11b8-4213-878b-770e8597ac16");
    private static readonly ConcurrentDictionary<string, MetadataReaderProvider?> Readers = new(StringComparer.OrdinalIgnoreCase);

    public static ReactorSourceSpan? Locate(MethodBase? method)
    {
        if (method is null)
        {
            return null;
        }
        try
        {
            var path = method.Module.Assembly.Location;
            if (string.IsNullOrEmpty(path) || Readers.GetOrAdd(path, Open) is not { } provider)
            {
                return null;
            }
            return Locate(provider.GetMetadataReader(), method.MetadataToken);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or InvalidOperationException or ArgumentException
            or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    internal static ReactorSourceSpan? Locate(MetadataReader reader, int methodToken)
    {
        var handle = MetadataTokens.MethodDefinitionHandle(methodToken & 0x00FFFFFF);
        var debug = reader.GetMethodDebugInformation(handle);
        foreach (var point in debug.GetSequencePoints())
        {
            if (point.IsHidden || point.Document.IsNil)
            {
                continue;
            }
            var document = reader.GetDocument(point.Document);
            var algorithm = document.HashAlgorithm.IsNil ? Guid.Empty : reader.GetGuid(document.HashAlgorithm);
            var hash = document.Hash.IsNil ? null : reader.GetBlobBytes(document.Hash);
            return new ReactorSourceSpan(reader.GetString(document.Name), point.StartLine, point.StartColumn,
                algorithm == Sha256 ? "SHA256" : algorithm == Sha1 ? "SHA1" : null,
                hash is { Length: > 0 } ? Convert.ToHexString(hash).ToLowerInvariant() : null);
        }
        return null;
    }

    private static MetadataReaderProvider? Open(string assemblyPath)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);
            if (pe.TryOpenAssociatedPortablePdb(assemblyPath,
                    candidate => File.Exists(candidate) ? File.OpenRead(candidate) : null, out var provider, out _) && provider is not null)
            {
                return provider;
            }
            // A packaged layout (AppX) copies the assembly but not its PDB; the build recorded where the PDB was written.
            foreach (var entry in pe.ReadDebugDirectory())
            {
                if (entry.Type != DebugDirectoryEntryType.CodeView)
                {
                    continue;
                }
                var codeView = pe.ReadCodeViewDebugDirectoryData(entry);
                if (!Path.IsPathFullyQualified(codeView.Path) || !File.Exists(codeView.Path))
                {
                    continue;
                }
                var candidate = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(codeView.Path));
                var id = candidate.GetMetadataReader().DebugMetadataHeader?.Id;
                if (id is { Length: >= 16 } && new Guid(id.Value.AsSpan()[..16]) == codeView.Guid)
                {
                    return candidate;
                }
                candidate.Dispose();
            }
            return null;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }
}
