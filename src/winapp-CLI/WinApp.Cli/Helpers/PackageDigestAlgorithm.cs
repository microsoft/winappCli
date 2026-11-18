// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using System.Xml;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Chooses the SignTool file digest (<c>/fd</c>) for a file. An MSIX/APPX package or bundle records the
/// hash algorithm it was built with in <c>AppxBlockMap.xml</c>, and SignTool rejects a signature whose
/// digest differs from it (for example, a package built with <c>AppxHashAlgorithmId=SHA384</c>).
/// </summary>
internal static class PackageDigestAlgorithm
{
    public const string Default = "SHA256";

    private static readonly Dictionary<string, string> HashMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        ["http://www.w3.org/2001/04/xmlenc#sha256"] = "SHA256",
        ["http://www.w3.org/2001/04/xmldsig-more#sha384"] = "SHA384",
        ["http://www.w3.org/2001/04/xmlenc#sha512"] = "SHA512",
    };

    /// <summary>
    /// Returns the package's block map hash algorithm as a SignTool <c>/fd</c> value, or
    /// <see cref="Default"/> when the file is not a readable package (for example an .exe or .dll).
    /// </summary>
    public static string Detect(FileInfo file)
    {
        try
        {
            using var archive = ZipFile.OpenRead(file.FullName);
            var entry = archive.GetEntry("AppxBlockMap.xml");
            if (entry == null)
            {
                return Default;
            }

            // HashMethod is on the root element, so stop there instead of parsing the whole block map,
            // which lists a hash for every 64 KB block and can be many megabytes.
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            reader.MoveToContent();
            var hashMethod = reader.GetAttribute("HashMethod");
            return hashMethod != null && HashMethods.TryGetValue(hashMethod, out var algorithm) ? algorithm : Default;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or XmlException or UnauthorizedAccessException)
        {
            return Default;
        }
    }
}
