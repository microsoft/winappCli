// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Helpers;

internal static partial class DevelopmentIdentityHelper
{
    internal static string ResolvePathForIo(string path)
    {
        var finalPath = ResolveFinalPath(path);
        var canonical = finalPath.ToUpperInvariant();
        var resolved = finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            ? @"\\" + finalPath[8..]
            : finalPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? finalPath[4..] : finalPath;
        if (!string.Equals(CanonicalizePath(resolved), canonical, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The resolved path '{path}' cannot be represented safely for Windows package registration.");
        }
        return resolved;
    }

    private const int ErrorInsufficientBuffer = 122;
    internal const string SeedVersion = "winapp-unique-v1";

    /// <summary>
    /// Resolves the final DOS path through filesystem links, then folds case invariantly.
    /// An absent output path is resolved through its nearest existing directory; callers must
    /// revalidate it after creation and before using it as an ownership boundary.
    /// </summary>
    public static string CanonicalizePath(string path) => ResolveFinalPath(path).ToUpperInvariant();

    private static unsafe string ResolveFinalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var missing = new Stack<string>();
        while (true)
        {
            using var handle = CreateFile(current, 0, FileShare.ReadWrite | FileShare.Delete,
                0, 3, 0x02000000, 0);
            if (!handle.IsInvalid)
            {
                if (missing.Count > 0 && (File.GetAttributes(current) & FileAttributes.Directory) == 0)
                {
                    throw new IOException($"Cannot resolve '{path}': ancestor '{current}' is not a directory.");
                }

                var buffer = new char[32768];
                fixed (char* output = buffer)
                {
                    var length = GetFinalPathNameByHandle(handle, output, (uint)buffer.Length, 0);
                    if (length == 0)
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot resolve final path for '{path}'.");
                    }
                    if (length >= buffer.Length)
                    {
                        throw new PathTooLongException($"The final path for '{path}' exceeds the Windows path limit.");
                    }

                    var finalPath = new string(output, 0, (int)length);
                    if (!finalPath.StartsWith(@"\\?\", StringComparison.Ordinal))
                    {
                        throw new IOException($"Windows did not return a final DOS path for '{path}'.");
                    }
                    foreach (var component in missing)
                    {
                        finalPath = Path.Combine(finalPath, component);
                    }
                    return Path.TrimEndingDirectorySeparator(finalPath);
                }
            }

            var error = Marshal.GetLastPInvokeError();
            if (error is not (2 or 3))
            {
                throw new Win32Exception(error, $"Cannot open '{current}' to resolve '{path}'.");
            }
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"Cannot resolve the filesystem link '{current}' to its final target.");
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent))
            {
                throw new Win32Exception(error, $"No existing ancestor could be resolved for '{path}'.");
            }
            var componentName = Path.GetFileName(current);
            if (componentName.Length == 0 || componentName.EndsWith('.') || componentName.EndsWith(' ')
                || componentName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException($"The absent path component '{componentName}' is not a normalized Windows filename.", nameof(path));
            }
            missing.Push(componentName);
            current = parent;
        }
    }

    public static string DeriveName(string canonicalOwnerPath, string originalName, string exactPublisher)
    {
        ValidatePackageString(originalName, 3, 50, "Name");
        ValidatePublisher(exactPublisher);
        var name = originalName[..Math.Min(originalName.Length, 24)] + ".w"
            + DeriveSuffix(canonicalOwnerPath, originalName, exactPublisher);
        _ = ComputeFamilyName(name, exactPublisher);
        return name;
    }

    public static string ComputeFamilyName(string name, string publisher)
        => ComputeNativeName(name, publisher, 11, 0, string.Empty, fullName: false);

    public static string ComputeFullName(AppxManifestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return ComputeNativeName(
            Required(document.IdentityName, "Identity/@Name"),
            Required(document.IdentityPublisher, "Identity/@Publisher"),
            ParseArchitecture(document.IdentityProcessorArchitecture ?? "neutral"),
            ParseVersion(Required(document.IdentityVersion, "Identity/@Version")),
            document.IdentityResourceId ?? string.Empty,
            fullName: true);
    }

    /// <summary>Preflights an original normalized manifest without changing it or writing files.</summary>
    public static DevelopmentIdentity Create(AppxManifestDocument document, string ownerPath, string layoutPath, bool uniqueIdentity)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (uniqueIdentity)
        {
            document.ValidateUniqueIdentitySupport();
        }
        _ = ComputeFullName(document);
        var originalName = Required(document.IdentityName, "Identity/@Name");
        var publisher = Required(document.IdentityPublisher, "Identity/@Publisher");
        var canonicalOwner = CanonicalizePath(ownerPath);
        var effectiveName = uniqueIdentity ? DeriveName(canonicalOwner, originalName, publisher) : originalName;
        return new DevelopmentIdentity
        {
            Mode = uniqueIdentity ? "Unique" : "Original",
            OriginalPackageName = originalName,
            EffectivePackageName = effectiveName,
            Publisher = publisher,
            Version = Required(document.IdentityVersion, "Identity/@Version"),
            Architecture = document.IdentityProcessorArchitecture ?? "neutral",
            ResourceId = document.IdentityResourceId ?? string.Empty,
            PackageFamilyName = ComputeFamilyName(effectiveName, publisher),
            ApplicationId = Required(document.ApplicationId, "Application/@Id"),
            OwnerPath = canonicalOwner,
            LayoutPath = CanonicalizePath(layoutPath),
            Aliases = uniqueIdentity
                ? BuildAliasMappings(document.GetExecutionAliases(), canonicalOwner, originalName, publisher)
                : new Dictionary<string, string>(),
        };
    }

    /// <summary>Computes every authored alias before registration or other side effects.</summary>
    public static IReadOnlyDictionary<string, string> BuildAliasMappings(
        IEnumerable<string> aliases, string canonicalOwnerPath, string originalName, string exactPublisher)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        var effectiveName = DeriveName(canonicalOwnerPath, originalName, exactPublisher);
        var suffix = effectiveName[^26..];
        var originalDefaultAlias = ExecutionAliasResolver.BuildDefaultAliasName(ComputeFamilyName(originalName, exactPublisher))
            ?? throw new InvalidOperationException("Cannot derive a safe default execution alias for the original package family.");
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var effectiveAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in aliases)
        {
            if (!ExecutionAliasResolver.IsSafeAliasName(alias))
            {
                throw new InvalidOperationException($"Execution alias '{alias}' is not a safe .exe filename. Correct the manifest before using --unique-identity.");
            }
            string effectiveAlias;
            if (string.Equals(alias, originalDefaultAlias, StringComparison.OrdinalIgnoreCase))
            {
                effectiveAlias = ExecutionAliasResolver.BuildDefaultAliasName(ComputeFamilyName(effectiveName, exactPublisher))
                    ?? throw new InvalidOperationException("Cannot derive a safe default execution alias for the unique package family.");
            }
            else
            {
                var stem = alias[..^4];
                var stemLength = Math.Min(stem.Length, 255 - suffix.Length - 4);
                if (stemLength > 0 && char.IsHighSurrogate(stem[stemLength - 1]))
                {
                    stemLength--;
                }
                effectiveAlias = stem[..stemLength] + suffix + ".exe";
            }
            if (!ExecutionAliasResolver.IsSafeAliasName(effectiveAlias)
                || !effectiveAliases.Add(effectiveAlias) || !mappings.TryAdd(alias, effectiveAlias))
            {
                throw new InvalidOperationException($"Execution alias '{alias}' has a duplicate or conflicting unique name '{effectiveAlias}'. Use distinct shorter authored aliases.");
            }
        }
        if (mappings.Values.FirstOrDefault(mappings.ContainsKey) is { } originalCollision)
        {
            throw new InvalidOperationException($"Unique execution alias '{originalCollision}' conflicts with an original authored alias. Use distinct shorter authored aliases.");
        }
        return mappings;
    }

    /// <summary>Rejects known explicit original authorities; implicit authorities remain unchanged.</summary>
    public static void ValidateResourceReference(string value, string originalPackageName, string? originalPackageFamilyName = null)
    {
        foreach (var scheme in new[] { "ms-resource://", "ms-appx://" })
        {
            var offset = 0;
            while ((offset = value.IndexOf(scheme, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                offset += scheme.Length;
                var end = offset;
                while (end < value.Length && value[end] is not ('/' or '\\' or '?' or '#' or '"' or '\'' or '<' or '>')
                    && !char.IsWhiteSpace(value[end]))
                {
                    end++;
                }
                var authority = Uri.UnescapeDataString(value[offset..end]);
                if (string.Equals(authority, originalPackageName, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrEmpty(originalPackageFamilyName)
                        && string.Equals(authority, originalPackageFamilyName, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException(
                        $"--unique-identity does not support the explicit original package authority in '{value}'. Use an authority-less {scheme}/ reference instead.");
                }
                offset = end;
            }
        }
    }

    private static string DeriveSuffix(string canonicalOwnerPath, string originalName, string exactPublisher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalOwnerPath);
        if (!Path.IsPathFullyQualified(canonicalOwnerPath) || canonicalOwnerPath.Contains('\0'))
        {
            throw new ArgumentException("A canonical, absolute owner path is required.", nameof(canonicalOwnerPath));
        }
        var seed = $"{SeedVersion}\0{canonicalOwnerPath}\0{originalName}\0{exactPublisher}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed)).AsSpan(0, 12));
    }

    private static string Required(string? value, string field)
        => !string.IsNullOrWhiteSpace(value) && !value.Contains('\0')
            ? value
            : throw new InvalidOperationException($"The manifest must specify a valid {field}.");

    private static void ValidatePackageString(string value, int minimum, int maximum, string field)
    {
        ArgumentNullException.ThrowIfNull(value);
        var firstPart = value.Split('.')[0];
        var reserved = firstPart.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || firstPart.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || firstPart.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || firstPart.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (firstPart.Length == 4 && firstPart[3] is >= '1' and <= '9'
                && (firstPart.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || firstPart.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)));
        if (value.Length < minimum || value.Length > maximum
            || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-'))
            || value.EndsWith('.') || value.StartsWith("xn--", StringComparison.OrdinalIgnoreCase)
            || value.Contains(".xn--", StringComparison.OrdinalIgnoreCase) || reserved)
        {
            throw new InvalidOperationException($"Identity/@{field} '{value}' is not a valid Windows package string ({minimum}-{maximum} characters; letters, digits, '.' or '-', with no reserved names).");
        }
    }

    private static void ValidatePublisher(string publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        if (string.IsNullOrWhiteSpace(publisher) || publisher.Length > 8192 || publisher.Any(char.IsControl))
        {
            throw new InvalidOperationException("Identity/@Publisher must be an X.509 distinguished name of 1-8192 characters.");
        }
        try
        {
            _ = new X500DistinguishedName(publisher);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("Identity/@Publisher must be a valid X.509 distinguished name.", ex);
        }
    }

    private static uint ParseArchitecture(string architecture) => architecture switch
    {
        "x86" => 0,
        "arm" => 5,
        "x64" => 9,
        "neutral" => 11,
        "arm64" => 12,
        "x86a64" => 14,
        _ => throw new InvalidOperationException($"Identity/@ProcessorArchitecture '{architecture}' is unsupported."),
    };

    private static ulong ParseVersion(string version)
    {
        var parts = version.Split('.');
        ulong result = 0;
        if (parts.Length != 4)
        {
            throw new InvalidOperationException($"Identity/@Version '{version}' must contain four numbers in the range 0-65535.");
        }
        foreach (var part in parts)
        {
            if (!ushort.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                throw new InvalidOperationException($"Identity/@Version '{version}' must contain four numbers in the range 0-65535.");
            }
            // PACKAGE_VERSION stores Revision in the low word and Major in the high word.
            result = (result << 16) | number;
        }
        return result;
    }

    private static unsafe string ComputeNativeName(string name, string publisher, uint architecture, ulong version, string resourceId, bool fullName)
    {
        ValidatePackageString(name, 3, 50, "Name");
        ValidatePackageString(resourceId, 0, 30, "ResourceId");
        ValidatePublisher(publisher);
        fixed (char* namePointer = name)
        fixed (char* publisherPointer = publisher)
        fixed (char* resourcePointer = resourceId)
        {
            var id = new PackageId
            {
                ProcessorArchitecture = architecture,
                Version = version,
                Name = namePointer,
                Publisher = publisherPointer,
                ResourceId = resourcePointer,
            };
            uint length = 0;
            var status = fullName
                ? PackageFullNameFromId(&id, ref length, null)
                : PackageFamilyNameFromId(&id, ref length, null);
            if (status != ErrorInsufficientBuffer)
            {
                throw new Win32Exception(status, $"Windows rejected the package identity '{name}'.");
            }
            var buffer = new char[length];
            fixed (char* output = buffer)
            {
                status = fullName
                    ? PackageFullNameFromId(&id, ref length, output)
                    : PackageFamilyNameFromId(&id, ref length, output);
                if (status != 0)
                {
                    throw new Win32Exception(status, $"Windows could not compute the package identity '{name}'.");
                }
                return new string(output);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct PackageId
    {
        public uint Reserved;
        public uint ProcessorArchitecture;
        public ulong Version;
        public char* Name;
        public char* Publisher;
        public char* ResourceId;
        public char* PublisherId;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string path, uint access, FileShare share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle handle, char* path, uint count, uint flags);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int PackageFamilyNameFromId(PackageId* id, ref uint length, char* output);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int PackageFullNameFromId(PackageId* id, ref uint length, char* output);
}
