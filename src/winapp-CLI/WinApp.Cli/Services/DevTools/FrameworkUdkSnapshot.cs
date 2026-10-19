// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services.DevTools;

// Only authenticated bytes copied into our own medium-integrity namespace may enter the loader.
// A process module name, runtime-looking folder, and the override are discovery hints, not trust.
internal sealed unsafe partial class FrameworkUdkSnapshot : IDisposable
{
    internal const string FileName = "Microsoft.Internal.FrameworkUdk.dll";
    private readonly string directory;
    private readonly List<SafeFileHandle> directories = [];
    private FileStream? file;
    private FileStream? sourceFile;
    private bool createdDirectory;
    public string Path { get; }

    private FrameworkUdkSnapshot(string directory)
    {
        this.directory = directory;
        Path = System.IO.Path.Combine(directory, FileName);
    }

    internal static string ValidateSourcePath(string path)
    {
        if (!System.IO.Path.IsPathFullyQualified(path) || PathSafety.IsNetworkPath(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.AsSpan(2).Contains(':') ||
            !string.Equals(System.IO.Path.GetFileName(path), FileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"The framework UDK must be a full local path to {FileName}.");
        }
        var full = System.IO.Path.GetFullPath(path);
        if (PathSafety.HasReparsePointOnPath(full, System.IO.Path.GetPathRoot(full)!))
        {
            throw new IOException("The framework UDK path is redirected or inaccessible. Use an unredirected local runtime path.");
        }
        return full;
    }

    public static FrameworkUdkSnapshot Create(string sourcePath) =>
        Create(sourcePath, System.IO.Path.GetTempPath(),
            path => AuthenticodeVerifier.IsTrustedMicrosoftSignedFileOrCatalog(path, NullLogger.Instance),
            path => FileVersionInfo.GetVersionInfo(path).OriginalFilename,
            PeHelper.DetectPeArchitecture);

    internal static FrameworkUdkSnapshot Create(string sourcePath, string temporaryRoot,
        Func<string, bool> verifySignature, Func<string, string?> originalName, Func<string, string?> architecture)
    {
        var source = ValidateSourcePath(sourcePath);
        var root = System.IO.Path.GetFullPath(temporaryRoot);
        if (PathSafety.HasReparsePointOnPath(root, System.IO.Path.GetPathRoot(root)!))
        {
            throw new IOException("The framework UDK temporary directory is redirected or inaccessible.");
        }
        var snapshot = new FrameworkUdkSnapshot(System.IO.Path.Combine(root, $"winapp-udk-{Guid.NewGuid():N}"));
        try
        {
            // Pin the namespace before creating anything: neither a parent rename nor a junction
            // substitution may redirect a later signature check or executable load.
            var chain = new Stack<string>();
            for (var parent = new DirectoryInfo(root); parent is not null; parent = parent.Parent)
            {
                chain.Push(parent.FullName);
            }
            foreach (var parent in chain)
            {
                snapshot.PinDirectory(parent);
            }
            snapshot.sourceFile = OpenSource(source);
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new IOException("Could not determine the UDK loader's user.");
            var security = new DirectorySecurity();
            security.SetSecurityDescriptorSddlForm($"O:{user.Value}D:P(A;OICI;FA;;;{user.Value})");
            new DirectoryInfo(snapshot.directory).Create(security);
            snapshot.createdDirectory = true;
            snapshot.PinDirectory(snapshot.directory, readSecurity: true);
            snapshot.RequireMediumIntegrity();
            using (var output = new FileStream(snapshot.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                snapshot.sourceFile.CopyTo(output);
            }
            snapshot.file = new FileStream(snapshot.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!verifySignature(snapshot.Path))
            {
                throw new IOException("The framework UDK does not have a trusted Microsoft Authenticode signature. Repair the Windows App Runtime or remove the unsafe override.");
            }
            if (!string.Equals(originalName(snapshot.Path), FileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The signed file is not the Windows App Runtime framework UDK.");
            }
            if (!string.Equals(architecture(snapshot.Path), RuntimeInformation.ProcessArchitecture.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The framework UDK architecture does not match winapp. Use a matching winapp and target architecture.");
            }
            return snapshot;
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    private static FileStream OpenSource(string source)
    {
        // OBJ_DONT_REPARSE refuses redirection atomically, including an ancestor replaced after
        // discovery. Opening the file directly also works under non-enumerable WindowsApps.
        var nativePath = @"\??\" + source;
        fixed (char* path = nativePath)
        {
            var name = new UnicodeString
            {
                Length = checked((ushort)(nativePath.Length * sizeof(char))),
                MaximumLength = checked((ushort)((nativePath.Length + 1) * sizeof(char))),
                Buffer = path,
            };
            var attributes = new ObjectAttributes
            {
                Length = (uint)sizeof(ObjectAttributes),
                ObjectName = &name,
                Attributes = 0x1040,
            };
            var status = NtCreateFile(out var handle, 0x80100000, &attributes, out _, 0, 0,
                1, 1, 0x60, 0, 0);
            if (status < 0)
            {
                handle.Dispose();
                throw new Win32Exception((int)RtlNtStatusToDosError(status),
                    "Could not open an unredirected, stable target framework UDK.");
            }
            return new FileStream(handle, FileAccess.Read);
        }
    }

    private void PinDirectory(string path, bool readSecurity = false)
    {
        var handle = CreateFileW(path, readSecurity ? 0x20000u : 0, 1, 0, 3, 0x02200000, 0); // read sharing, OPEN_REPARSE_POINT | BACKUP_SEMANTICS
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, $"Could not secure the UDK loader directory: {path}");
        }
        directories.Add(handle);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"The UDK loader directory is redirected: {path}");
        }
    }

    private void RequireMediumIntegrity()
    {
        // LABEL_SECURITY_INFORMATION can be read without SeSecurityPrivilege (unlike a full SACL).
        var error = GetSecurityInfo(directories[^1], 1, 0x10, 0, 0, 0, 0, out var descriptor);
        if (error != 0)
        {
            throw new Win32Exception((int)error, "Could not verify the UDK snapshot's integrity label.");
        }
        try
        {
            var bytes = new byte[GetSecurityDescriptorLength(descriptor)];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            if (!IsMediumOrHigher(new RawSecurityDescriptor(bytes, 0)))
            {
                throw new IOException("The UDK loader requires a medium-integrity temporary directory. Choose a normal local TEMP directory; permissions were not changed.");
            }
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    internal static bool IsMediumOrHigher(RawSecurityDescriptor descriptor)
    {
        // An object with no mandatory label is medium integrity under Windows' mandatory policy.
        if (descriptor.SystemAcl is not null)
        {
            foreach (GenericAce ace in descriptor.SystemAcl)
            {
                if ((byte)ace.AceType != 0x11) // SYSTEM_MANDATORY_LABEL_ACE_TYPE
                {
                    continue;
                }
                var bytes = new byte[ace.BinaryLength];
                ace.GetBinaryForm(bytes, 0);
                var sid = new SecurityIdentifier(bytes, 8);
                if ((BitConverter.ToUInt32(bytes, 4) & 1) == 0 ||
                    !sid.Value.StartsWith("S-1-16-", StringComparison.Ordinal) ||
                    !uint.TryParse(sid.Value.Split('-')[^1], out var level) || level < 8192)
                {
                    return false;
                }
            }
        }
        return true;
    }

    public void Dispose()
    {
        file?.Dispose();
        file = null;
        sourceFile?.Dispose();
        sourceFile = null;
        try
        {
            if (createdDirectory)
            {
                if (File.Exists(Path))
                {
                    File.Delete(Path);
                }
                directories[^1].Dispose();
                Directory.Delete(directory);
                createdDirectory = false;
            }
        }
        finally
        {
            for (var index = directories.Count - 1; index >= 0; index--)
            {
                directories[index].Dispose();
            }
            directories.Clear();
        }
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(string path, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);

    [LibraryImport("advapi32.dll")]
    private static partial uint GetSecurityInfo(SafeFileHandle handle, uint type, uint information,
        nint owner, nint group, nint dacl, nint sacl, out nint descriptor);

    [LibraryImport("advapi32.dll")]
    private static partial uint GetSecurityDescriptorLength(nint descriptor);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public char* Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public uint Length;
        public nint RootDirectory;
        public UnicodeString* ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor;
        public nint SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public nint Status;
        public nuint Information;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtCreateFile(out SafeFileHandle handle, uint access, ObjectAttributes* attributes,
        out IoStatusBlock status, nint allocationSize, uint fileAttributes, uint share, uint disposition,
        uint options, nint eaBuffer, uint eaLength);

    [LibraryImport("ntdll.dll")]
    private static partial uint RtlNtStatusToDosError(int status);
}
