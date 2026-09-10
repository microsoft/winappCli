// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection.PortableExecutable;

namespace WinApp.Cli.Services;

/// <summary>
/// Static helper for detecting executable architecture from PE headers.
/// </summary>
internal static class PeHelper
{
    /// <summary>
    /// Detects the architecture of a PE file and returns an MSIX-style architecture string:
    /// "x86", "x64", "arm", "arm64", or "neutral".
    ///
    /// Rules:
    /// - Native PE images are classified from the COFF Machine field.
    /// - Managed .NET IL-only images are classified using COR flags:
    ///     * I386 + ILOnly + Requires32Bit => x86
    ///     * I386 + ILOnly + !Requires32Bit => neutral
    /// - Mixed-mode / native-hosted managed images fall back to the native Machine field.
    ///
    /// Returns null if the file is not a valid PE image or uses an unsupported architecture.
    /// </summary>
    internal static string? DetectPeArchitecture(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var peReader = new PEReader(stream);

            var headers = peReader.PEHeaders;
            var coff = headers.CoffHeader;
            var cor = headers.CorHeader;

            ushort machine = (ushort)coff.Machine;

            return ClassifyArchitecture(machine, cor?.Flags);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns whether a PE image is a console (character-mode) executable, or <see langword="null"/>
    /// when the file cannot be read as a PE image.
    /// </summary>
    /// <remarks>
    /// This is what <c>OutputType</c> compiles down to: <c>Exe</c> produces
    /// <c>IMAGE_SUBSYSTEM_WINDOWS_CUI</c> and <c>WinExe</c> produces <c>IMAGE_SUBSYSTEM_WINDOWS_GUI</c>.
    /// Reading it from the built binary lets folder mode make the same console/windowed decision that
    /// project and single-file mode make from the evaluated property, without a project to evaluate.
    /// </remarks>
    internal static bool? IsConsoleSubsystem(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var peReader = new PEReader(stream);

            var peHeader = peReader.PEHeaders.PEHeader;
            return peHeader is null ? null : peHeader.Subsystem == Subsystem.WindowsCui;
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Maps a COFF machine value and optional COR flags to an MSIX-style architecture string.
    /// Extracted as a pure function (no file I/O) so every native/managed/mixed-mode branch can be
    /// unit tested directly.
    /// <list type="bullet">
    /// <item>When <paramref name="corFlags"/> is <c>null</c> the image is native/mixed-mode and is
    /// classified from <paramref name="machine"/>.</item>
    /// <item>Managed IL-only images use the COR flags: I386 + Requires32Bit =&gt; x86, otherwise
    /// I386 =&gt; neutral (AnyCPU).</item>
    /// </list>
    /// Returns <c>null</c> for unsupported architectures.
    /// </summary>
    internal static string? ClassifyArchitecture(ushort machine, CorFlags? corFlags)
    {
        // Native or mixed-mode case: use the PE machine directly.
        if (corFlags is null)
        {
            return MapNativeMachine(machine);
        }

        CorFlags flags = corFlags.Value;
        bool isIlOnly = (flags & CorFlags.ILOnly) != 0;
        bool requires32Bit = (flags & CorFlags.Requires32Bit) != 0;

        // Managed IL-only assemblies need special handling.
        // In particular, IL-only I386 without Requires32Bit is effectively AnyCPU/neutral.
        if (isIlOnly)
        {
            return machine switch
            {
                0x014C => requires32Bit ? "x86" : "neutral", // I386
                0x8664 => "x64",   // unusual for pure IL-only, but valid to preserve
                0x01C0 => "arm",   // ARM
                0x01C4 => "arm",   // ARMNT
                0xAA64 => "arm64", // ARM64
                _ => null
            };
        }

        // Mixed-mode / native-entry managed image: machine matters.
        return MapNativeMachine(machine);
    }

    private static string? MapNativeMachine(ushort machine) => machine switch
    {
        0x014C => "x86",    // IMAGE_FILE_MACHINE_I386
        0x8664 => "x64",    // IMAGE_FILE_MACHINE_AMD64
        0xAA64 => "arm64",  // IMAGE_FILE_MACHINE_ARM64
        0x01C4 => "arm",    // IMAGE_FILE_MACHINE_ARMNT
        0x01C0 => "arm",    // IMAGE_FILE_MACHINE_ARM
        _ => null
    };
}
