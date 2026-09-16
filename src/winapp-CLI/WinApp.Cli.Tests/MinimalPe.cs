// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Tests;

/// <summary>
/// Builds the smallest native PE image whose COFF machine field is readable.
/// </summary>
/// <remarks>
/// Exists so a test can exercise a real architecture probe instead of stubbing it. Anything that
/// decides what a binary is for by reading its headers is only meaningfully covered by bytes that
/// actually have those headers.
/// </remarks>
internal static class MinimalPe
{
    /// <summary>Builds an image for a canonical winapp architecture token.</summary>
    public static byte[] ForArchitecture(string architecture) => Build(architecture switch
    {
        "x64" => 0x8664,
        "arm64" => 0xAA64,
        "x86" => 0x014C,
        _ => throw new ArgumentOutOfRangeException(nameof(architecture), architecture, "not a PE machine type"),
    });

    /// <summary>Builds a native PE with the given COFF machine value and no COR header.</summary>
    public static byte[] Build(ushort machineType)
    {
        var is64Bit = machineType is 0x8664 or 0xAA64;
        var optionalHeaderSize = is64Bit ? (ushort)0xF0 : (ushort)0xE0;
        const int CoffStart = 0x84;

        var image = new byte[CoffStart + 20 + optionalHeaderSize + 64];

        image[0] = 0x4D;
        image[1] = 0x5A;
        BitConverter.GetBytes(0x80).CopyTo(image, 0x3C);
        image[0x80] = 0x50;
        image[0x81] = 0x45;

        BitConverter.GetBytes(machineType).CopyTo(image, CoffStart);
        BitConverter.GetBytes(optionalHeaderSize).CopyTo(image, CoffStart + 16);
        image[CoffStart + 18] = 0x02;

        var optionalStart = CoffStart + 20;

        if (is64Bit)
        {
            image[optionalStart] = 0x0B;
            image[optionalStart + 1] = 0x02;
            BitConverter.GetBytes(16).CopyTo(image, optionalStart + 108);
        }
        else
        {
            image[optionalStart] = 0x0B;
            image[optionalStart + 1] = 0x01;
            BitConverter.GetBytes(16).CopyTo(image, optionalStart + 92);
        }

        return image;
    }
}
