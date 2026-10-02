// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public partial class DevelopmentIdentityHelperTests
{
    private const string CanonicalOwner = @"\\?\C:\WORKTREES\ONE\APP.CSPROJ";
    private const string MicrosoftPublisher = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
    private string _directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Environment.CurrentDirectory, ".identity-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    [TestMethod]
    public void DeriveName_HasFrozenVersionOneVectors()
    {
        Assert.AreEqual("Contoso.App.wdcd1321db22718d6e013867b",
            DevelopmentIdentityHelper.DeriveName(CanonicalOwner, "Contoso.App", "CN=Contoso"));
        Assert.AreEqual("Contoso.App.wb9c1b99e6b1a483239ecf779",
            DevelopmentIdentityHelper.DeriveName(CanonicalOwner, "Contoso.App", "CN=CONTOSO"));
        Assert.AreEqual("Contoso.App.wf6ef51c03ea96f909418b2f8",
            DevelopmentIdentityHelper.DeriveName(@"\\?\C:\WORKTREES\TWO\APP.CSPROJ", "Contoso.App", "CN=Contoso"));
        Assert.AreEqual("AAAAAAAAAAAAAAAAAAAAAAAA.we1d4803a08c0f27f7fcb0b5e",
            DevelopmentIdentityHelper.DeriveName(CanonicalOwner, new string('A', 50), "CN=Contoso"));
    }

    [TestMethod]
    public void ComputeFamilyName_UsesWindowsPublisherHash()
    {
        Assert.AreEqual("Contoso.App_8wekyb3d8bbwe",
            DevelopmentIdentityHelper.ComputeFamilyName("Contoso.App", MicrosoftPublisher));
        Assert.AreNotEqual(DevelopmentIdentityHelper.ComputeFamilyName("Contoso.App", "CN=Contoso"),
            DevelopmentIdentityHelper.ComputeFamilyName("Contoso.App", "CN=CONTOSO"));
    }

    [TestMethod]
    [DataRow("x86")]
    [DataRow("x64")]
    [DataRow("arm")]
    [DataRow("arm64")]
    [DataRow("neutral")]
    [DataRow("x86a64")]
    public void ComputeFullName_PreservesNativeVersionUnionAndArchitecture(string architecture)
    {
        var document = Manifest();
        document.IdentityPublisher = MicrosoftPublisher;
        document.IdentityVersion = "1.2.3.4";
        document.IdentityProcessorArchitecture = architecture;
        document.IdentityResourceId = "language-en";
        Assert.AreEqual($"Contoso.App_1.2.3.4_{architecture}_language-en_8wekyb3d8bbwe",
            DevelopmentIdentityHelper.ComputeFullName(document));
    }

    [TestMethod]
    public void ComputeFullName_SupportsMaximumVersionAndDefaultArchitecture()
    {
        var document = Manifest();
        document.IdentityPublisher = MicrosoftPublisher;
        document.IdentityVersion = "65535.65535.65535.65535";
        document.IdentityProcessorArchitecture = null;
        Assert.AreEqual("Contoso.App_65535.65535.65535.65535_neutral__8wekyb3d8bbwe",
            DevelopmentIdentityHelper.ComputeFullName(document));
    }

    [TestMethod]
    [DataRow("ab")]
    [DataRow("bad_name")]
    [DataRow("bad/name")]
    [DataRow("CON")]
    [DataRow("cOn.other")]
    [DataRow("PRN")]
    [DataRow("aux")]
    [DataRow("NUL.exe")]
    [DataRow("COM1")]
    [DataRow("LPT9.foo")]
    [DataRow("xn--bad")]
    [DataRow("ok.XN--bad")]
    [DataRow("Bad.")]
    [DataRow("...")]
    [DataRow("Bad\0Name")]
    public void DeriveName_RejectsInvalidOriginalRatherThanRepairingIt(string name)
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            DevelopmentIdentityHelper.DeriveName(CanonicalOwner, name, "CN=Contoso"));
    }

    [TestMethod]
    public void DeriveName_ValidatesBoundariesBeforeTruncating()
    {
        Assert.HasCount(29, DevelopmentIdentityHelper.DeriveName(CanonicalOwner, "App", "CN=Contoso"));
        Assert.HasCount(50, DevelopmentIdentityHelper.DeriveName(CanonicalOwner, new string('a', 50), "CN=Contoso"));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            DevelopmentIdentityHelper.DeriveName(CanonicalOwner, new string('a', 51), "CN=Contoso"));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("Not a distinguished name")]
    [DataRow("CN=Contoso\0CN=Other")]
    public void DeriveName_RejectsInvalidPublisher(string publisher)
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            DevelopmentIdentityHelper.DeriveName(CanonicalOwner, "Contoso.App", publisher));
    }

    [TestMethod]
    [DataRow("1.2.3")]
    [DataRow("1.2.3.65536")]
    [DataRow("-1.2.3.4")]
    [DataRow("1.2.3.4 ")]
    [DataRow("1.2.3.4.5")]
    public void ComputeFullName_RejectsMalformedVersion(string version)
    {
        var document = Manifest();
        document.IdentityVersion = version;
        Assert.ThrowsExactly<InvalidOperationException>(() => DevelopmentIdentityHelper.ComputeFullName(document));
    }

    [TestMethod]
    public void ComputeFullName_RejectsInvalidArchitectureAndResourceId()
    {
        var document = Manifest();
        document.IdentityProcessorArchitecture = "AnyCPU";
        Assert.ThrowsExactly<InvalidOperationException>(() => DevelopmentIdentityHelper.ComputeFullName(document));
        document.IdentityProcessorArchitecture = "x64";
        document.IdentityResourceId = "CON";
        Assert.ThrowsExactly<InvalidOperationException>(() => DevelopmentIdentityHelper.ComputeFullName(document));
    }

    [TestMethod]
    public void CanonicalizePath_ResolvesCaseRelativeDotSegmentsAndExtendedPaths()
    {
        var file = Path.Combine(_directory, "Owner.csproj");
        File.WriteAllText(file, string.Empty);
        var expected = DevelopmentIdentityHelper.CanonicalizePath(file);
        Assert.StartsWith(@"\\?\", expected);
        Assert.AreEqual(expected, expected.ToUpperInvariant());
        Assert.AreEqual(expected, DevelopmentIdentityHelper.CanonicalizePath(file.ToUpperInvariant()));
        Assert.AreEqual(expected, DevelopmentIdentityHelper.CanonicalizePath(Path.Combine(_directory, ".", "Owner.csproj")));
        Assert.AreEqual(expected, DevelopmentIdentityHelper.CanonicalizePath(Path.GetRelativePath(Environment.CurrentDirectory, file)));
        Assert.AreEqual(expected, DevelopmentIdentityHelper.CanonicalizePath(expected));
        Assert.AreEqual(DevelopmentIdentityHelper.CanonicalizePath(_directory),
            DevelopmentIdentityHelper.CanonicalizePath(_directory + Path.DirectorySeparatorChar));
    }

    [TestMethod]
    public void CanonicalizePath_ResolvesMissingOutputThroughExistingAncestor()
    {
        var output = Path.Combine(_directory, "missing", "AppX");
        var before = DevelopmentIdentityHelper.CanonicalizePath(output);
        Assert.IsFalse(Directory.Exists(output));
        Directory.CreateDirectory(output);
        Assert.AreEqual(before, DevelopmentIdentityHelper.CanonicalizePath(output));
        Assert.AreEqual(Path.Combine(DevelopmentIdentityHelper.CanonicalizePath(_directory), "MISSING", "APPX"), before);
    }

    [TestMethod]
    public void CanonicalizePath_RejectsFileAncestorAndUnnormalizedAbsentNames()
    {
        var file = Path.Combine(_directory, "Owner.csproj");
        File.WriteAllText(file, string.Empty);
        Assert.Throws<IOException>(() => DevelopmentIdentityHelper.CanonicalizePath(Path.Combine(file, "AppX")));
        Assert.AreEqual(DevelopmentIdentityHelper.CanonicalizePath(Path.Combine(_directory, "absent")),
            DevelopmentIdentityHelper.CanonicalizePath(Path.Combine(_directory, "absent.")));
        Assert.ThrowsExactly<ArgumentException>(() =>
            DevelopmentIdentityHelper.CanonicalizePath(@"\\?\" + Path.Combine(_directory, "absent.")));
    }

    [TestMethod]
    public unsafe void CanonicalizePath_ResolvesAvailableShortName()
    {
        var directory = Path.Combine(_directory, "Long owner directory");
        Directory.CreateDirectory(directory);
        var buffer = new char[32768];
        fixed (char* output = buffer)
        {
            var length = GetShortPathName(DevelopmentIdentityHelper.CanonicalizePath(directory), output, (uint)buffer.Length);
            Assert.IsGreaterThan(0u, length, new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            Assert.AreEqual(DevelopmentIdentityHelper.CanonicalizePath(directory),
                DevelopmentIdentityHelper.CanonicalizePath(new string(output, 0, (int)length)));
        }
    }

    [TestMethod]
    public void CanonicalizePath_ResolvesSymbolicLinkWhenPermitted()
    {
        var target = Path.Combine(_directory, "real");
        var link = Path.Combine(_directory, "link");
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (UnauthorizedAccessException)
        {
            Assert.Inconclusive("Creating symbolic links is not permitted on this machine.");
        }
        Assert.AreEqual(DevelopmentIdentityHelper.CanonicalizePath(target),
            DevelopmentIdentityHelper.CanonicalizePath(link));
        Assert.AreEqual(DevelopmentIdentityHelper.CanonicalizePath(Path.Combine(target, "AppX")),
            DevelopmentIdentityHelper.CanonicalizePath(Path.Combine(link, "AppX")));
        Directory.Delete(target);
        Assert.Throws<IOException>(() => DevelopmentIdentityHelper.CanonicalizePath(link));
    }

    [TestMethod]
    public void Create_UsesOnlyOwnerAndOriginalIdentityForFamily()
    {
        var document = Manifest();
        var originalXml = document.ToXml();
        var first = DevelopmentIdentityHelper.Create(document, _directory, Path.Combine(_directory, "Debug", "AppX"), true);
        document.IdentityVersion = "2.3.4.5";
        document.IdentityProcessorArchitecture = "arm64";
        var second = DevelopmentIdentityHelper.Create(document, _directory, Path.Combine(_directory, "Release", "AppX"), true);
        Assert.AreEqual(first.EffectivePackageName, second.EffectivePackageName);
        Assert.AreEqual(first.PackageFamilyName, second.PackageFamilyName);
        Assert.AreNotEqual(first.LayoutPath, second.LayoutPath);
        Assert.IsNull(first.PackageFullName);
        Assert.AreEqual("Unique", first.Mode);
        Assert.AreEqual(0L, first.Revision);
        document.IdentityVersion = "1.0.0.0";
        document.IdentityProcessorArchitecture = "x64";
        Assert.AreEqual(originalXml, document.ToXml());
        Assert.IsFalse(Directory.Exists(Path.Combine(_directory, "Debug")));
    }

    [TestMethod]
    public void Create_OriginalModeDoesNotChangeNameOrAuthorAliases()
    {
        var document = Manifest();
        document.EnsureExecutionAlias("authored.exe");
        var original = DevelopmentIdentityHelper.Create(document, _directory, _directory, false);
        Assert.AreEqual("Original", original.Mode);
        Assert.AreEqual(original.OriginalPackageName, original.EffectivePackageName);
        Assert.IsEmpty(original.Aliases);
        var result = new MsixIdentityResult("Contoso.App", "CN=Contoso", "App") { Identity = original };
        Assert.AreSame(original, result.Identity);
        Assert.IsNull(new MsixIdentityResult("Contoso.App", "CN=Contoso", "App").Identity);
    }

    [TestMethod]
    public void BuildAliasMappings_UsesSameIdentitySuffixAndPreservesAuthoredStem()
    {
        var mappings = DevelopmentIdentityHelper.BuildAliasMappings(["one.exe", "Two.EXE"], CanonicalOwner, "Contoso.App", "CN=Contoso");
        Assert.AreEqual("one.wdcd1321db22718d6e013867b.exe", mappings["one.exe"]);
        Assert.AreEqual("Two.wdcd1321db22718d6e013867b.exe", mappings["Two.EXE"]);
        Assert.AreEqual("winapp-Contoso.App.wdcd1321db22718d6e013867b_8wekyb3d8bbwe.exe",
            ExecutionAliasResolver.BuildDefaultAliasName("Contoso.App.wdcd1321db22718d6e013867b_8wekyb3d8bbwe"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BuildAliasMappings_MapsOriginalDefaultAliasToEffectiveFamilyDefault(bool uppercase)
    {
        const string originalDefaultAlias = "winapp-Contoso.App_8wekyb3d8bbwe.exe";
        var alias = uppercase ? originalDefaultAlias.ToUpperInvariant() : originalDefaultAlias;
        var effectiveName = DevelopmentIdentityHelper.DeriveName(CanonicalOwner, "Contoso.App", MicrosoftPublisher);
        var effectiveDefault = ExecutionAliasResolver.BuildDefaultAliasName(
            DevelopmentIdentityHelper.ComputeFamilyName(effectiveName, MicrosoftPublisher));
        var mappings = DevelopmentIdentityHelper.BuildAliasMappings(
            [alias, "custom.exe"], CanonicalOwner, "Contoso.App", MicrosoftPublisher);

        Assert.AreEqual(effectiveDefault, mappings[alias]);
        Assert.AreEqual("custom" + effectiveName[^26..] + ".exe", mappings["custom.exe"]);
    }

    [TestMethod]
    public void BuildAliasMappings_RejectsDefaultAliasCollisionWithOriginalAuthoredName()
    {
        var effectiveName = DevelopmentIdentityHelper.DeriveName(CanonicalOwner, "Contoso.App", MicrosoftPublisher);
        var effectiveDefault = ExecutionAliasResolver.BuildDefaultAliasName(
            DevelopmentIdentityHelper.ComputeFamilyName(effectiveName, MicrosoftPublisher));
        Assert.IsNotNull(effectiveDefault);
        Assert.ThrowsExactly<InvalidOperationException>(() => DevelopmentIdentityHelper.BuildAliasMappings(
            ["winapp-Contoso.App_8wekyb3d8bbwe.exe", effectiveDefault], CanonicalOwner, "Contoso.App", MicrosoftPublisher));
    }

    [TestMethod]
    public void BuildAliasMappings_TruncatesButRejectsAnyCollision()
    {
        var alias = new string('a', 251) + ".exe";
        var transformed = DevelopmentIdentityHelper.BuildAliasMappings([alias], CanonicalOwner, "Contoso.App", "CN=Contoso")[alias];
        Assert.HasCount(255, transformed);
        Assert.IsTrue(ExecutionAliasResolver.IsSafeAliasName(transformed));
        Assert.ThrowsExactly<InvalidOperationException>(() => DevelopmentIdentityHelper.BuildAliasMappings(
            ["same.exe", "SAME.EXE"], CanonicalOwner, "Contoso.App", "CN=Contoso"));
        Assert.ThrowsExactly<InvalidOperationException>(() => DevelopmentIdentityHelper.BuildAliasMappings(
            [new string('a', 226) + "1.exe", new string('a', 226) + "2.exe"], CanonicalOwner, "Contoso.App", "CN=Contoso"));
        Assert.ThrowsExactly<InvalidOperationException>(() => DevelopmentIdentityHelper.BuildAliasMappings(
            ["one.exe", "one.wdcd1321db22718d6e013867b.exe"], CanonicalOwner, "Contoso.App", "CN=Contoso"));
    }

    [TestMethod]
    public void BuildAliasMappings_DoesNotSplitUnicodeCharacterAtTruncationBoundary()
    {
        var alias = new string('a', 224) + "\U0001F680tool.exe";
        var transformed = DevelopmentIdentityHelper.BuildAliasMappings([alias], CanonicalOwner, "Contoso.App", "CN=Contoso")[alias];
        Assert.HasCount(254, transformed);
        Assert.IsFalse(transformed.Any(char.IsSurrogate));
        Assert.IsTrue(ExecutionAliasResolver.IsSafeAliasName(transformed));
    }

    [TestMethod]
    [DataRow("CON.exe")]
    [DataRow("../tool.exe")]
    [DataRow("folder\\tool.exe")]
    [DataRow("tool.exe ")]
    [DataRow("tool.cmd")]
    public void BuildAliasMappings_RejectsUnsafeOriginals(string alias)
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            DevelopmentIdentityHelper.BuildAliasMappings([alias], CanonicalOwner, "Contoso.App", "CN=Contoso"));
    }

    [TestMethod]
    [DataRow("ms-resource://Contoso.App/Resources/Title")]
    [DataRow("MS-APPX://contoso.app/logo.png")]
    [DataRow("ms-resource://%43ontoso.App/Resources/Title")]
    [DataRow("prefix ms-resource://Contoso.App_8wekyb3d8bbwe/Resources/Title")]
    public void ValidateResourceReference_RejectsKnownOriginalAuthorities(string value)
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            DevelopmentIdentityHelper.ValidateResourceReference(value, "Contoso.App", "Contoso.App_8wekyb3d8bbwe"));
    }

    [TestMethod]
    [DataRow("ms-resource:Title")]
    [DataRow("ms-resource:///Resources/Title")]
    [DataRow("ms-appx:///Assets/logo.png")]
    [DataRow("ms-appx://Another.Library/Assets/logo.png")]
    public void ValidateResourceReference_PreservesImplicitAndOtherAuthorities(string value)
        => DevelopmentIdentityHelper.ValidateResourceReference(value, "Contoso.App", "Contoso.App_8wekyb3d8bbwe");

    [TestMethod]
    [DataRow("prefix<ms-resource://Contoso.App>suffix")]
    [DataRow("<value>MS-APPX://CONTOSO.APP</value>")]
    [DataRow("embedded ms-resource://%43ontoso.App/Resources/Title")]
    public void ValidateResourceReference_NameOnlyRejectsEmbeddedAndEscapedAuthorities(string value)
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            DevelopmentIdentityHelper.ValidateResourceReference(value, "Contoso.App"));
    }

    [TestMethod]
    [DataRow("ms-resource:///Resources/Title")]
    [DataRow("ms-appx:///Assets/logo.png")]
    [DataRow("<value>ms-resource://</value>")]
    [DataRow("ms-resource://Contoso.App_8wekyb3d8bbwe/Resources/Title")]
    public void ValidateResourceReference_NameOnlyPreservesImplicitAndUnknownAuthorities(string value)
    {
        DevelopmentIdentityHelper.ValidateResourceReference(value, "Contoso.App");
        DevelopmentIdentityHelper.ValidateResourceReference(value, "Contoso.App", string.Empty);
    }

    private static AppxManifestDocument Manifest() => AppxManifestDocument.Parse("""
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
          <Identity Name="Contoso.App" Publisher="CN=Contoso" Version="1.0.0.0" ProcessorArchitecture="x64" />
          <Applications><Application Id="App" Executable="App.exe" EntryPoint="Windows.FullTrustApplication" /></Applications>
        </Package>
        """);

    [LibraryImport("kernel32.dll", EntryPoint = "GetShortPathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint GetShortPathName(string path, char* shortPath, uint count);
}
