// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Validates execution alias names from AppX manifests and resolves them to the
/// canonical Windows App Execution Alias location under
/// <c>%LOCALAPPDATA%\Microsoft\WindowsApps</c>.
/// </summary>
/// <remarks>
/// Manifest <c>&lt;uap5:ExecutionAlias Alias="..."/&gt;</c> values are
/// attacker-controlled when the user runs <c>winapp run --with-alias</c> in an
/// untrusted repository. Passing a bare filename like <c>"a.exe"</c> directly to
/// <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/>
/// with <c>UseShellExecute = false</c> dispatches to <c>CreateProcessW</c>,
/// which resolves bare filenames against the current working directory first.
/// An attacker who ships a hostile <c>a.exe</c> next to a malicious manifest
/// would have it executed in place of the real Windows App Execution Alias
/// proxy. This resolver:
/// <list type="bullet">
///   <item>Rejects any alias that is not a bare filename (path separators,
///         <c>..</c>, drive letters, UNC paths, control chars).</item>
///   <item>Returns the absolute path under the WindowsApps folder so callers
///         can pass it to <c>ProcessStartInfo.FileName</c> directly. Absolute
///         paths bypass <c>CreateProcess</c>'s CWD/PATH search.</item>
/// </list>
/// </remarks>
internal static partial class ExecutionAliasResolver
{
    // Windows reserved device basenames. The OS treats these specially in
    // path resolution regardless of extension (e.g. "CON.exe" still binds
    // to the CON device), so they must never appear as an alias filename.
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Returns true when <paramref name="alias"/> is a safe bare filename
    /// suitable for resolving under the WindowsApps alias directory.
    /// </summary>
    /// <remarks>
    /// Rejects null/empty/whitespace, any path separator
    /// (<see cref="Path.DirectorySeparatorChar"/> or
    /// <see cref="Path.AltDirectorySeparatorChar"/>), rooted paths (drive
    /// letters, UNC, leading separators), <c>..</c> path components, the bare
    /// dot/double-dot names, any character in
    /// <see cref="Path.GetInvalidFileNameChars"/> (which includes NUL and
    /// other control chars on Windows), names longer than 255 characters,
    /// names without a <c>.exe</c> suffix (Windows App Execution Aliases are
    /// always <c>.exe</c> proxies), names ending in a dot or space (Win32
    /// silently strips these during path normalization, which would make the
    /// validated string and the launched file diverge), and Windows reserved
    /// device basenames such as <c>CON</c>, <c>NUL</c>, <c>COM1..9</c>,
    /// <c>LPT1..9</c>, with or without an extension.
    /// </remarks>
    public static bool IsSafeAliasName(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return false;
        }

        if (alias.Length > 255)
        {
            return false;
        }

        if (alias is "." or "..")
        {
            return false;
        }

        if (alias.Contains(Path.DirectorySeparatorChar)
            || alias.Contains(Path.AltDirectorySeparatorChar))
        {
            return false;
        }

        if (Path.IsPathRooted(alias))
        {
            return false;
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            if (alias.Contains(invalid))
            {
                return false;
            }
        }

        // Defence in depth: GetFileName strips any path components. If the
        // result differs from the input, the input contained path-like content
        // that the checks above missed (e.g. future platform differences).
        if (!string.Equals(Path.GetFileName(alias), alias, StringComparison.Ordinal))
        {
            return false;
        }

        // Windows path normalization silently trims trailing dots and spaces
        // ("evil.exe." resolves to "evil.exe"), which would let an attacker
        // construct an alias whose validated form differs from the file the
        // OS actually opens. Reject any such name.
        var lastChar = alias[^1];
        if (lastChar == '.' || lastChar == ' ')
        {
            return false;
        }

        // Windows App Execution Aliases are .exe proxy stubs under
        // %LOCALAPPDATA%\Microsoft\WindowsApps. Anything else cannot
        // legitimately resolve to a registered alias.
        if (!alias.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Reserved DOS device names are special-cased by Win32 regardless of
        // extension or directory ("CON", "CON.exe", "CON.txt.exe" all bind
        // to the CON device). Reject by checking the basename before the
        // first '.'.
        var stem = alias;
        var firstDot = alias.IndexOf('.');
        if (firstDot >= 0)
        {
            stem = alias[..firstDot];
        }
        if (ReservedDeviceNames.Contains(stem))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns the default Windows App Execution Alias base directory:
    /// <c>%LOCALAPPDATA%\Microsoft\WindowsApps</c>.
    /// </summary>
    public static string GetDefaultWindowsAppsDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps");

    /// <summary>
    /// Resolves <paramref name="alias"/> to an absolute <see cref="FileInfo"/>
    /// under the supplied <paramref name="baseDirectory"/> (or the default
    /// WindowsApps location when <paramref name="baseDirectory"/> is null).
    /// Returns null when the alias is not a safe bare filename, or when the
    /// base directory is not a rooted absolute path.
    /// </summary>
    /// <remarks>
    /// The returned <see cref="FileInfo"/>'s <c>Exists</c> property indicates
    /// whether Windows has actually registered an alias proxy at that path —
    /// callers should check it before launching.
    /// <para>
    /// Refusing to resolve when <paramref name="baseDirectory"/> (or
    /// <c>LocalApplicationData</c>) is empty/relative is critical: a
    /// CWD-relative base would let <see cref="FileInfo.FullName"/> root the
    /// resolved path under the (potentially hostile) current working
    /// directory, reintroducing the very CWD-search RCE this resolver exists
    /// to prevent.
    /// </para>
    /// </remarks>
    public static FileInfo? ResolveAliasPath(string? alias, string? baseDirectory = null)
    {
        if (!IsSafeAliasName(alias))
        {
            return null;
        }

        var dir = baseDirectory ?? GetDefaultWindowsAppsDirectory();
        if (string.IsNullOrEmpty(dir) || !Path.IsPathFullyQualified(dir))
        {
            return null;
        }

        return new FileInfo(Path.Combine(dir, alias!));
    }

    #region Default alias naming

    /// <summary>
    /// Prefix applied to every alias winapp generates.
    /// </summary>
    /// <remarks>
    /// Two things fall out of it. The alias can never collide with a real tool on PATH — an app called
    /// <c>python.cs</c> gets <c>winapp-python.exe</c>, not <c>python.exe</c> — and the stem before the
    /// first dot always starts with <c>winapp-</c>, so a package named <c>con</c> or <c>nul</c> cannot
    /// produce a reserved DOS device name.
    /// </remarks>
    public const string GeneratedAliasPrefix = "winapp-";

    /// <summary>
    /// Builds the alias winapp declares for a package that does not author one itself, derived from the
    /// <b>package family name</b> rather than the executable name.
    /// </summary>
    /// <remarks>
    /// An execution alias is a single global entry under <c>%LOCALAPPDATA%\Microsoft\WindowsApps</c>, and
    /// Windows gives it to the first package that claims it. Deriving it from the executable is what makes
    /// collisions likely, because unrelated apps share build output names — two different <c>main.cs</c>
    /// files both produce <c>main.exe</c>, and the second silently launches the first.
    /// <para>
    /// The family name is used rather than <c>Identity/@Name</c> alone because identity includes the
    /// publisher: two side-loaded packages can share a name under different publishers and coexist, so a
    /// name-only alias would put them back in contention. The family name is unique per installable
    /// package by construction.
    /// </para>
    /// <para>
    /// Returns <see langword="null"/> when no safe name can be built, and the caller then launches through
    /// AUMID rather than guessing at one.
    /// </para>
    /// </remarks>
    public static string? BuildDefaultAliasName(string? packageFamilyName)
    {
        if (string.IsNullOrWhiteSpace(packageFamilyName))
        {
            return null;
        }

        // Family names are already constrained to [-.A-Za-z0-9_], but this value reaches a file name, so
        // anything outside that set is dropped rather than trusted.
        //
        // Leading and trailing '.'/'-' are deliberately KEPT: they are valid in an Identity/@Name, so
        // trimming them maps distinct identities onto one alias — '-contoso_hash' and 'contoso_hash'
        // would both become 'winapp-contoso_hash.exe', and the second app to register would lose the
        // alias (and with it a console app's terminal output). The 'winapp-' prefix already guarantees
        // the file name never starts with '.' or '-', and IsSafeAliasName validates the final result.
        var sanitized = new string([.. packageFamilyName.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_')]);

        if (sanitized.Length == 0)
        {
            return null;
        }

        var alias = $"{GeneratedAliasPrefix}{sanitized}.exe";
        return IsSafeAliasName(alias) ? alias : null;
    }

    #endregion

    #region Alias ownership

    private const uint IoReparseTagAppExecLink = 0x8000001B;
    private const uint FsctlGetReparsePoint = 0x000900A8;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint OpenExisting = 3;
    private const int MaximumReparseDataBufferSize = 16 * 1024;

    /// <summary>
    /// Reads the package family name that an execution alias proxy actually resolves to.
    /// </summary>
    /// <remarks>
    /// An execution alias is a global, first-come-first-served name: the alias file is a single entry in
    /// <c>%LOCALAPPDATA%\Microsoft\WindowsApps</c>, so when two packages declare the same alias only one
    /// of them owns it. Launching the proxy then starts <b>the other app</b>, which is why the launcher
    /// compares this against the package it just registered instead of trusting the name.
    /// <para>
    /// The proxy is an <c>IO_REPARSE_TAG_APPEXECLINK</c> reparse point, which .NET does not resolve
    /// (<see cref="FileSystemInfo.LinkTarget"/> is null for it), so the payload is read directly. Its
    /// data is a version DWORD followed by null-terminated UTF-16 strings, the first of which is the
    /// package family name. Returns <see langword="false"/> for anything unreadable or not an
    /// app-exec-link, and the caller then proceeds rather than blocking a launch on a diagnostic.
    /// </para>
    /// </remarks>
    public static bool TryGetAliasPackageFamilyName(string aliasPath, out string? packageFamilyName)
    {
        packageFamilyName = null;

        if (string.IsNullOrEmpty(aliasPath) || !OperatingSystem.IsWindows())
        {
            return false;
        }

        using var handle = CreateFileW(
            aliasPath,
            0, // No access rights are needed to query a reparse point.
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return false;
        }

        var buffer = new byte[MaximumReparseDataBufferSize];
        if (!DeviceIoControl(handle, FsctlGetReparsePoint, IntPtr.Zero, 0, buffer, (uint)buffer.Length, out var returned, IntPtr.Zero))
        {
            return false;
        }

        // REPARSE_DATA_BUFFER: ReparseTag (4) + ReparseDataLength (2) + Reserved (2), then the payload,
        // which for an app-exec-link starts with a version DWORD before the string list.
        const int headerSize = 8;
        const int versionSize = 4;
        if (returned < headerSize + versionSize || BitConverter.ToUInt32(buffer, 0) != IoReparseTagAppExecLink)
        {
            return false;
        }

        var payloadLength = BitConverter.ToUInt16(buffer, 4);
        var available = Math.Min((int)returned - headerSize, payloadLength);
        if (available <= versionSize)
        {
            return false;
        }

        // The first string ends at the first UTF-16 NUL; a payload whose strings are absent or unterminated
        // is not something to guess at.
        var strings = Encoding.Unicode.GetString(buffer, headerSize + versionSize, available - versionSize);
        var terminator = strings.IndexOf('\0');
        if (terminator <= 0)
        {
            return false;
        }

        packageFamilyName = strings[..terminator];
        return true;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        [Out] byte[] lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    #endregion
}
