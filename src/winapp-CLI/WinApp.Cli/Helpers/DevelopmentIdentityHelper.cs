// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Helpers;

/// <summary>Derives the per-checkout package identity used by <c>winapp run --unique-identity</c>.</summary>
internal static partial class DevelopmentIdentityHelper
{
    // Part of every derived name. Changing it gives every checkout a new identity and app data.
    internal const string SeedVersion = "winapp-unique-v1";

    // Original names are truncated so the derived name stays within the 50-character limit.
    private const int MaxOriginalNameLength = 24;

    /// <summary>
    /// Returns the path with links, <c>subst</c> drives, and short names resolved, upper-cased, so
    /// every spelling of one checkout derives the same identity. Paths that don't exist are only
    /// normalized.
    /// </summary>
    public static unsafe string CanonicalizePath(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        using var handle = CreateFile(full, 0, FileShare.ReadWrite | FileShare.Delete, 0, OpenExisting, BackupSemantics, 0);
        if (handle.IsInvalid)
        {
            return full.ToUpperInvariant();
        }

        var buffer = new char[32768];
        uint length;
        fixed (char* output = buffer)
        {
            length = GetFinalPathNameByHandle(handle, output, (uint)buffer.Length, 0);
        }
        if (length == 0 || length >= buffer.Length)
        {
            return full.ToUpperInvariant();
        }

        var final = new string(buffer, 0, (int)length);
        final = final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + final[8..]
            : final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..]
            : final;
        return Path.TrimEndingDirectorySeparator(final).ToUpperInvariant();
    }

    /// <summary>
    /// Derives <c>&lt;name&gt;.w&lt;24 hex&gt;</c> from the checkout path and the original name. The publisher
    /// is left out: Windows already keeps packages from different publishers apart by package family.
    /// </summary>
    public static string DeriveName(string ownerPath, string originalName)
    {
        var seed = $"{SeedVersion}\0{CanonicalizePath(ownerPath)}\0{originalName}";
        var suffix = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed)).AsSpan(0, 12));
        return originalName[..Math.Min(originalName.Length, MaxOriginalNameLength)] + ".w" + suffix;
    }

    /// <summary>
    /// Renames an authored execution alias for a derived package name: <c>foo.exe</c> becomes
    /// <c>foo.w&lt;24 hex&gt;.exe</c>. An alias that already carries the suffix is returned unchanged.
    /// </summary>
    public static string RenameAlias(string alias, string derivedName)
    {
        var suffix = derivedName[^26..] + ".exe";
        return alias.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? alias : alias[..^".exe".Length] + suffix;
    }

    /// <summary>Builds the unique identity for a source manifest. Aliases are filled in when the staged copy is renamed.</summary>
    public static DevelopmentIdentity Create(AppxManifestDocument document, string ownerPath)
    {
        var originalName = document.IdentityName
            ?? throw new InvalidOperationException("The manifest must specify Identity/@Name to use --unique-identity.");
        var publisher = document.IdentityPublisher
            ?? throw new InvalidOperationException("The manifest must specify Identity/@Publisher to use --unique-identity.");
        if (!IsValidPackageName(originalName))
        {
            throw new InvalidOperationException(
                $"Identity/@Name '{originalName}' is not a valid package name. Use 3-50 letters, digits, periods, or hyphens.");
        }
        var name = DeriveName(ownerPath, originalName);
        return new DevelopmentIdentity
        {
            OriginalPackageName = originalName,
            PackageName = name,
            PackageFamilyName = AppLauncherService.ComputeFamilyName(name, publisher),
            OwnerPath = CanonicalizePath(ownerPath),
        };
    }

    /// <summary>Windows package names: 3-50 ASCII letters, digits, periods, or hyphens.</summary>
    public static bool IsValidPackageName(string name) =>
        name.Length is >= 3 and <= 50 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-');

    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string path, uint access, FileShare share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle handle, char* path, uint count, uint flags);
}
