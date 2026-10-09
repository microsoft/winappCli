// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace WinApp.Cli.Services.DevTools;

internal static class XbfSourceFingerprint
{
    // WinUI XamlBinaryMetadata.h / WidgetSpinner XbfReader: magic + section lengths,
    // version, six 64-bit metadata offsets, then 64 ASCII SHA256 characters.
    internal const int FingerprintOffset = 12 + 8 + 6 * 8;
    internal const int HeaderLength = FingerprintOffset + 64;

    internal static string Read(ReadOnlySpan<byte> xbf)
    {
        if (xbf.Length < HeaderLength || !xbf[..4].SequenceEqual("XBF\0"u8))
        {
            throw new InvalidDataException("The compiled XAML has no supported XBF header.");
        }
        var metadataSize = BinaryPrimitives.ReadUInt32LittleEndian(xbf[4..]);
        var nodeSize = BinaryPrimitives.ReadUInt32LittleEndian(xbf[8..]);
        var major = BinaryPrimitives.ReadUInt32LittleEndian(xbf[12..]);
        var minor = BinaryPrimitives.ReadUInt32LittleEndian(xbf[16..]);
        if (major != 2 || minor > 1 || metadataSize < HeaderLength - 12 ||
            (ulong)metadataSize + nodeSize + 12 != (ulong)xbf.Length || nodeSize == 0)
        {
            throw new InvalidDataException("The compiled XAML version or section bounds are unsupported.");
        }
        ulong previous = HeaderLength - 12;
        for (var table = 0; table < 6; table++)
        {
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(xbf[(20 + table * 8)..]);
            if (offset < previous || offset > metadataSize - 4 || table == 0 && offset != HeaderLength - 12)
            {
                throw new InvalidDataException("The compiled XAML metadata offsets are invalid.");
            }
            previous = offset;
        }
        var fingerprint = xbf.Slice(FingerprintOffset, 64);
        foreach (var character in fingerprint)
        {
            if (character is not (>= (byte)'0' and <= (byte)'9') and not (>= (byte)'A' and <= (byte)'F'))
            {
                throw new InvalidDataException("The compiled XAML has no original-source SHA256 fingerprint.");
            }
        }
        if (fingerprint.IndexOfAnyExcept((byte)'0') < 0)
        {
            throw new InvalidDataException("The compiled XAML original-source fingerprint is empty.");
        }
        return Encoding.ASCII.GetString(fingerprint);
    }

    internal static void Verify(ReadOnlySpan<byte> source, ReadOnlySpan<byte> xbf)
    {
        if (!Read(xbf).Equals(Convert.ToHexString(SHA256.HashData(source)), StringComparison.Ordinal))
        {
            throw new InvalidDataException("The original XAML differs from the source fingerprint recorded by the compiler.");
        }
    }
}
