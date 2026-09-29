// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace WinApp.Cli.Helpers;

internal static unsafe partial class AuthenticodeVerifier
{
    private static bool VerifyCatalogSignature(string filePath, ILogger logger)
    {
        using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        foreach (var algorithm in new[] { "SHA256", "SHA1" })
        {
            if (!CryptCATAdminAcquireContext2(out var admin, 0, algorithm, 0, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not open the catalog database.");
            }
            try
            {
                var hash = CalculateCatalogHash(admin, file);
                fixed (byte* bytes = hash)
                {
                    var catalog = CryptCATAdminEnumCatalogFromHash(admin, bytes, (uint)hash.Length, 0, 0);
                    if (catalog == 0)
                    {
                        continue;
                    }
                    try
                    {
                        var info = new CatalogInfo { Size = (uint)sizeof(CatalogInfo) };
                        if (!CryptCATCatalogInfoFromContext(catalog, &info, 0))
                        {
                            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not resolve the catalog.");
                        }
                        if (VerifyCatalogMember(file, filePath, new string(info.Path), admin, hash, logger))
                        {
                            return true;
                        }
                    }
                    finally
                    {
                        CryptCATAdminReleaseCatalogContext(admin, catalog, 0);
                    }
                }
            }
            finally
            {
                CryptCATAdminReleaseContext(admin, 0);
            }
        }
        logger.LogDebug("No trusted Microsoft catalog signature for {File}.", filePath);
        return false;
    }

    private static byte[] CalculateCatalogHash(nint admin, FileStream file)
    {
        file.Position = 0;
        uint size = 0;
        if (!CryptCATAdminCalcHashFromFileHandle2(admin, file.SafeFileHandle, ref size, null, 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not size the catalog member hash.");
        }
        var hash = new byte[size];
        fixed (byte* bytes = hash)
        {
            file.Position = 0;
            if (!CryptCATAdminCalcHashFromFileHandle2(admin, file.SafeFileHandle, ref size, bytes, 0) ||
                size != hash.Length)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not hash the catalog member.");
            }
        }
        return hash;
    }

    // Also exercises unsigned/unregistered catalogs without installing anything into the machine's database.
    internal static bool VerifyCatalogMember(string filePath, string catalogPath, ILogger logger)
    {
        using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!CryptCATAdminAcquireContext2(out var admin, 0, "SHA256", 0, 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        try
        {
            return VerifyCatalogMember(file, filePath, catalogPath, admin, CalculateCatalogHash(admin, file), logger);
        }
        finally
        {
            CryptCATAdminReleaseContext(admin, 0);
        }
    }

    private static bool VerifyCatalogMember(FileStream file, string filePath, string catalogPath,
        nint admin, byte[] hash, ILogger logger)
    {
        using var catalog = new FileStream(catalogPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var tag = Convert.ToHexString(hash);
        fixed (char* path = filePath, catalogName = catalogPath, memberTag = tag)
        fixed (byte* bytes = hash)
        {
            var info = new WinTrustCatalogInfo
            {
                Size = (uint)sizeof(WinTrustCatalogInfo),
                CatalogPath = catalogName,
                MemberTag = memberTag,
                MemberPath = path,
                MemberFile = file.SafeFileHandle.DangerousGetHandle(),
                Hash = bytes,
                HashSize = (uint)hash.Length,
                Admin = admin,
            };
            var address = (nint)(&info);
            string? verifiedSubject = null;
            var result = IsTrustedMicrosoftSigned(filePath, logger,
                (_, revocation, flags) => VerifyCatalogTrustCore(address, revocation, flags, out verifiedSubject),
                _ => verifiedSubject is not null && IsMicrosoftSubject(verifiedSubject));
            GC.KeepAlive(file);
            return result;
        }
    }

    private static int VerifyCatalogTrustCore(nint info, uint revocation, uint flags, out string? verifiedSubject)
    {
        verifiedSubject = null;
        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)sizeof(WINTRUST_DATA),
            dwUIChoice = WTD_UI_NONE,
            fdwRevocationChecks = revocation,
            dwUnionChoice = 2, // WTD_CHOICE_CATALOG verifies membership, not just the catalog file.
            pInfo = info,
            dwStateAction = WTD_STATEACTION_VERIFY,
            dwProvFlags = flags,
        };
        var action = GenericVerifyV2;
        try
        {
            var result = WinVerifyTrust(0, ref action, ref data);
            if (result != 0)
            {
                return result;
            }
            // Read the signer from this successful trust decision, never from an independently reopened file.
            var provider = PInvoke.WTHelperProvDataFromStateData((HANDLE)data.hWVTStateData);
            var signer = provider == null ? null : PInvoke.WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            var certificate = signer == null ? null : PInvoke.WTHelperGetProvCertFromChain(signer, 0);
            if (certificate != null && certificate->pCert != null)
            {
                using var signingCertificate = new X509Certificate2((nint)certificate->pCert);
                verifiedSubject = signingCertificate.Subject;
            }
            return result;
        }
        finally
        {
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(0, ref action, ref data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CatalogInfo
    {
        public uint Size;
        public fixed char Path[260];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustCatalogInfo
    {
        public uint Size;
        public uint Version;
        public char* CatalogPath;
        public char* MemberTag;
        public char* MemberPath;
        public nint MemberFile;
        public byte* Hash;
        public uint HashSize;
        public nint CatalogContext;
        public nint Admin;
    }

    [LibraryImport("wintrust.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminAcquireContext2(out nint admin, nint subsystem, string algorithm, nint policy, uint flags);

    [LibraryImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminCalcHashFromFileHandle2(nint admin, SafeFileHandle file, ref uint size, byte* hash, uint flags);

    [LibraryImport("wintrust.dll", SetLastError = true)]
    private static partial nint CryptCATAdminEnumCatalogFromHash(nint admin, byte* hash, uint size, uint flags, nint previous);

    [LibraryImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATCatalogInfoFromContext(nint catalog, CatalogInfo* info, uint flags);

    [LibraryImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminReleaseCatalogContext(nint admin, nint catalog, uint flags);

    [LibraryImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminReleaseContext(nint admin, uint flags);
}
