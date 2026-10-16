// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class PackageDigestAlgorithmTests : BaseCommandTests
{
    public PackageDigestAlgorithmTests() : base(configPaths: false)
    {
    }

    internal const string Sha256 = "http://www.w3.org/2001/04/xmlenc#sha256";
    internal const string Sha384 = "http://www.w3.org/2001/04/xmldsig-more#sha384";
    internal const string Sha512 = "http://www.w3.org/2001/04/xmlenc#sha512";

    /// <summary>Writes a minimal package-shaped zip whose AppxBlockMap.xml declares <paramref name="hashMethod"/>.</summary>
    internal static FileInfo CreatePackage(string path, string hashMethod)
    {
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("AppxBlockMap.xml").Open());
            writer.Write($"""<?xml version="1.0" encoding="UTF-8"?><BlockMap xmlns="http://schemas.microsoft.com/appx/2010/blockmap" HashMethod="{hashMethod}"></BlockMap>""");
        }
        return new FileInfo(path);
    }

    [TestMethod]
    [DataRow(Sha256, "SHA256", ".msix")]
    [DataRow(Sha384, "SHA384", ".msix")]
    [DataRow(Sha512, "SHA512", ".msix")]
    [DataRow(Sha384, "SHA384", ".msixbundle")]
    public void Detect_Package_ReturnsBlockMapHashAlgorithm(string hashMethod, string expected, string extension)
    {
        var package = CreatePackage(Path.Join(_tempDirectory.FullName, $"app{extension}"), hashMethod);

        Assert.AreEqual(expected, PackageDigestAlgorithm.Detect(package));
    }

    [TestMethod]
    public void Detect_NonPackageFile_ReturnsSha256()
    {
        var exe = new FileInfo(Path.Join(_tempDirectory.FullName, "app.exe"));
        File.WriteAllText(exe.FullName, "MZ");

        Assert.AreEqual("SHA256", PackageDigestAlgorithm.Detect(exe));
    }

    [TestMethod]
    public void Detect_ZipWithoutBlockMap_ReturnsSha256()
    {
        var path = Path.Join(_tempDirectory.FullName, "plain.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            archive.CreateEntry("readme.txt");
        }

        Assert.AreEqual("SHA256", PackageDigestAlgorithm.Detect(new FileInfo(path)));
    }

    [TestMethod]
    public void Detect_UnknownHashMethod_ReturnsSha256()
    {
        var package = CreatePackage(Path.Join(_tempDirectory.FullName, "unknown.msix"), "urn:unknown");

        Assert.AreEqual("SHA256", PackageDigestAlgorithm.Detect(package));
    }

    [TestMethod]
    public void Detect_MalformedBlockMap_ReturnsSha256()
    {
        var path = Path.Join(_tempDirectory.FullName, "malformed.msix");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("AppxBlockMap.xml").Open());
            writer.Write("not xml");
        }

        Assert.AreEqual("SHA256", PackageDigestAlgorithm.Detect(new FileInfo(path)));
    }
}
