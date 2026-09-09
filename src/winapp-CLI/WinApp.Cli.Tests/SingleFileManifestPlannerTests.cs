// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="SingleFileManifestPlanner"/> — the pure mapping from a .NET file-based app's
/// evaluated <c>#:property</c> values onto appxmanifest metadata.
/// </summary>
[TestClass]
public class SingleFileManifestPlannerTests
{
    private static FileInfo SingleFile(string name = "counter.cs", string? folder = null) =>
        new(folder is null
            ? Path.Join(Path.GetTempPath(), name)
            : Path.Join(Path.GetTempPath(), folder, name));

    private static Dictionary<string, string> Props(params (string Name, string Value)[] values)
    {
        var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values)
        {
            dictionary[name] = value;
        }
        return dictionary;
    }

    #region Defaults

    [TestMethod]
    public void Plan_NoProperties_DerivesEverythingFromTheFileStem()
    {
        var info = SingleFileManifestPlanner.Plan(SingleFile(), Props(), defaultPublisher: "CN=tester");

        // The identity carries a hash of the file's path, so two counter.cs files in different folders
        // do not share an identity — and therefore do not share LocalState.
        StringAssert.Matches(info.PackageName, PathHashedName("counter"));
        Assert.AreEqual("counter", info.DisplayName, "the display name stays the readable stem");
        Assert.AreEqual("CN=tester", info.PublisherDN);
        Assert.AreEqual("1.0.0.0", info.Version);
        Assert.AreEqual("counter", info.Description, "description falls back to the display name");
    }

    [TestMethod]
    public void Plan_SameStemInDifferentFolders_GetsDifferentIdentities()
    {
        // The collision this exists to prevent: same identity means the second run replaces the first's
        // registration AND inherits its LocalState.
        var a = SingleFileManifestPlanner.Plan(SingleFile("counter.cs", "app-a"), Props(), defaultPublisher: "CN=tester");
        var b = SingleFileManifestPlanner.Plan(SingleFile("counter.cs", "app-b"), Props(), defaultPublisher: "CN=tester");

        Assert.AreNotEqual(a.PackageName, b.PackageName);
        Assert.AreEqual(a.DisplayName, b.DisplayName, "only the identity diverges; both still show as 'counter'");
    }

    [TestMethod]
    public void Plan_SameFile_GetsAStableIdentityAcrossCalls()
    {
        // Identity has to survive edits and re-runs, or every run would strand the previous registration.
        var first = SingleFileManifestPlanner.Plan(SingleFile(), Props(), defaultPublisher: "CN=tester");
        var second = SingleFileManifestPlanner.Plan(SingleFile(), Props(), defaultPublisher: "CN=tester");

        Assert.AreEqual(first.PackageName, second.PackageName);
    }

    [TestMethod]
    public void Plan_DeclaredPackageName_IsUsedVerbatimWithNoHash()
    {
        // Naming the package is how a user opts into an identity they control, so it must not be altered.
        var info = SingleFileManifestPlanner.Plan(
            SingleFile(),
            Props((SingleFileManifestPlanner.PackageNameProperty, "com.contoso.counter")),
            defaultPublisher: "CN=tester");

        Assert.AreEqual("com.contoso.counter", info.PackageName);
    }

    [TestMethod]
    public void Plan_UndeclaredPropertiesEvaluateToEmpty_AreTreatedAsAbsent()
    {
        // MSBuild returns "" for a property no #:property declared, so the planner must treat an empty
        // string exactly like a missing key rather than emitting Name="" into the manifest.
        var props = Props(
            (SingleFileManifestPlanner.PackageNameProperty, ""),
            (SingleFileManifestPlanner.DisplayNameProperty, "   "),
            (SingleFileManifestPlanner.PublisherProperty, ""),
            (SingleFileManifestPlanner.DescriptionProperty, ""),
            (SingleFileManifestPlanner.VersionProperty, ""),
            ("Version", ""));

        var info = SingleFileManifestPlanner.Plan(SingleFile(), props, defaultPublisher: "CN=tester");

        StringAssert.Matches(info.PackageName, PathHashedName("counter"));
        Assert.AreEqual("counter", info.DisplayName);
        Assert.AreEqual("CN=tester", info.PublisherDN);
        Assert.AreEqual("1.0.0.0", info.Version);
    }

    [TestMethod]
    public void Plan_StemWithInvalidIdentityCharacters_IsSanitizedForIdentityButNotForDisplay()
    {
        // Identity/@Name must match [-.A-Za-z0-9]+, but the display name is free text, so the RAW stem is
        // the better default there.
        var info = SingleFileManifestPlanner.Plan(SingleFile("my tool.cs"), Props(), defaultPublisher: "CN=tester");

        StringAssert.Matches(info.PackageName, PathHashedName("mytool"));
        Assert.AreEqual("my tool", info.DisplayName);
    }

    [TestMethod]
    [DataRow(42, DisplayName = "42-char stem: the first length that would overflow")]
    [DataRow(50, DisplayName = "50-char stem: already at the cap")]
    [DataRow(120, DisplayName = "120-char stem: far past the cap")]
    public void Plan_LongFileName_StaysWithinTheIdentityLengthLimit(int stemLength)
    {
        // Identity/@Name maxes out at 50 characters. Appending the hash after sanitizing pushed a long
        // stem past that, and Windows rejects the manifest with an opaque 0xC00CE169 at registration
        // rather than anything naming the length.
        var info = SingleFileManifestPlanner.Plan(
            SingleFile($"{new string('a', stemLength)}.cs"), Props(), defaultPublisher: "CN=tester");

        Assert.IsLessThanOrEqualTo(50, info.PackageName.Length,
            $"A {stemLength}-character stem produced '{info.PackageName}' ({info.PackageName.Length} chars)");
        StringAssert.Matches(info.PackageName, new Regex("-[0-9a-f]{8}$"),
            "The path hash must survive truncation — it is what makes the identity unique");
    }

    [TestMethod]
    public void Plan_LongFileNamesDifferingOnlyByFolder_StillGetDifferentIdentities()
    {
        // Truncation must not collapse two long-named files back into one identity.
        var name = $"{new string('a', 80)}.cs";
        var a = SingleFileManifestPlanner.Plan(SingleFile(name, "app-a"), Props(), defaultPublisher: "CN=tester");
        var b = SingleFileManifestPlanner.Plan(SingleFile(name, "app-b"), Props(), defaultPublisher: "CN=tester");

        Assert.AreNotEqual(a.PackageName, b.PackageName);
    }

    /// <summary>Matches "&lt;stem&gt;-&lt;8 hex&gt;", the shape a default identity takes.</summary>
    private static Regex PathHashedName(string stem) => new($"^{Regex.Escape(stem)}-[0-9a-f]{{8}}$");

    #endregion

    #region Declared values

    [TestMethod]
    public void Plan_DeclaredProperties_AreUsedVerbatim()
    {
        var props = Props(
            (SingleFileManifestPlanner.PackageNameProperty, "com.contoso.counter"),
            (SingleFileManifestPlanner.DisplayNameProperty, "Contoso Counter"),
            (SingleFileManifestPlanner.PublisherProperty, "CN=Contoso Ltd"),
            (SingleFileManifestPlanner.VersionProperty, "4.5.6.7"),
            (SingleFileManifestPlanner.DescriptionProperty, "Counts things"));

        var info = SingleFileManifestPlanner.Plan(SingleFile(), props);

        Assert.AreEqual("com.contoso.counter", info.PackageName, "dots are valid in an Identity name");
        Assert.AreEqual("Contoso Counter", info.DisplayName);
        Assert.AreEqual("CN=Contoso Ltd", info.PublisherDN);
        Assert.AreEqual("4.5.6.7", info.Version);
        Assert.AreEqual("Counts things", info.Description);
    }

    [TestMethod]
    public void Plan_BarePublisherName_IsWrappedAsCommonName()
    {
        // Matches what `manifest generate --publisher-name` already does; diverging would be surprising.
        var props = Props((SingleFileManifestPlanner.PublisherProperty, "Contoso Ltd"));

        var info = SingleFileManifestPlanner.Plan(SingleFile(), props);

        Assert.AreEqual("CN=Contoso Ltd", info.PublisherDN);
    }

    [TestMethod]
    public void Plan_DeclaredDisplayName_BecomesTheDescriptionFallback()
    {
        var props = Props((SingleFileManifestPlanner.DisplayNameProperty, "Contoso Counter"));

        var info = SingleFileManifestPlanner.Plan(SingleFile(), props);

        Assert.AreEqual("Contoso Counter", info.Description);
    }

    #endregion

    #region Version normalization

    [TestMethod]
    [DataRow("1.0.0", "1.0.0.0", DisplayName = "MSBuild's three-part default is padded")]
    [DataRow("2.1", "2.1.0.0", DisplayName = "two components are padded")]
    [DataRow("7", "7.0.0.0", DisplayName = "one component is padded")]
    [DataRow("1.2.3.4", "1.2.3.4", DisplayName = "an already-valid four-part version is unchanged")]
    [DataRow("1.2.3-preview.4", "1.2.3.0", DisplayName = "a semver pre-release suffix is cut, then padded")]
    [DataRow("2.0.0-rc.1+build.55", "2.0.0.0", DisplayName = "pre-release and build metadata are cut")]
    [DataRow("65535.65535.65535.65535", "65535.65535.65535.65535", DisplayName = "the upper bound is accepted")]
    public void Plan_Version_IsNormalizedToFourComponents(string declared, string expected)
    {
        var info = SingleFileManifestPlanner.Plan(SingleFile(), Props(("Version", declared)));

        Assert.AreEqual(expected, info.Version);
    }

    [TestMethod]
    public void Plan_ReadsMSBuildVersion_NotVersionPrefix()
    {
        // Setting $(Version) explicitly leaves $(VersionPrefix) EMPTY, so a planner that consulted
        // VersionPrefix first would silently discard the user's version and emit the 1.0.0.0 default.
        var props = Props(("Version", "3.4.5"), ("VersionPrefix", ""));

        var info = SingleFileManifestPlanner.Plan(SingleFile(), props);

        Assert.AreEqual("3.4.5.0", info.Version);
    }

    [TestMethod]
    public void Plan_WinAppVersion_TakesPrecedenceOverMSBuildVersion()
    {
        var props = Props(
            (SingleFileManifestPlanner.VersionProperty, "9.9.9.9"),
            ("Version", "1.2.3"));

        var info = SingleFileManifestPlanner.Plan(SingleFile(), props);

        Assert.AreEqual("9.9.9.9", info.Version);
    }

    [TestMethod]
    [DataRow("70000.1.2.3", DisplayName = "a component above 65535 is rejected")]
    [DataRow("1.2.3.4.5", DisplayName = "five components are rejected, not truncated")]
    [DataRow("not-a-version", DisplayName = "unparseable text is rejected")]
    [DataRow("1.2.3oops", DisplayName = "a trailing non-numeric suffix is rejected, not silently dropped")]
    [DataRow("v1.2.3", DisplayName = "a leading non-numeric prefix is rejected")]
    [DataRow("1.2.3 (build 7)", DisplayName = "decorated build metadata is rejected")]
    public void Plan_UnusableVersion_ThrowsInsteadOfSilentlyChangingIt(string declared)
    {
        var exception = Assert.ThrowsExactly<ProjectRunException>(() =>
            SingleFileManifestPlanner.Plan(SingleFile(), Props((SingleFileManifestPlanner.VersionProperty, declared))));

        StringAssert.Contains(exception.Message, "counter.cs", "the message should name the offending file");
        StringAssert.Contains(exception.Message, "65535", "the message should state the valid range");
    }

    [TestMethod]
    public void Plan_UnusableVersion_NamesThePropertyThatDeclaredIt()
    {
        // The two sources have different fixes, so the error must say which one was actually read.
        var winApp = Assert.ThrowsExactly<ProjectRunException>(() =>
            SingleFileManifestPlanner.Plan(SingleFile(), Props((SingleFileManifestPlanner.VersionProperty, "70000.0"))));
        StringAssert.Contains(winApp.Message, "WinAppVersion='70000.0'");

        var msBuild = Assert.ThrowsExactly<ProjectRunException>(() =>
            SingleFileManifestPlanner.Plan(SingleFile(), Props(("Version", "70000.0"))));
        StringAssert.Contains(msBuild.Message, "Version='70000.0'");
    }

    [TestMethod]
    public void Plan_PublisherThatNormalizesToEmpty_IsRejectedAsACommandError()
    {
        // PublisherDnHelper.Normalize throws ArgumentException once wrapper quotes strip to nothing.
        // Letting that escape printed a stack trace and, under --json, no envelope at all — one
        // malformed optional directive breaking automation.
        var ex = Assert.ThrowsExactly<ProjectRunException>(() =>
            SingleFileManifestPlanner.Plan(SingleFile(), Props((SingleFileManifestPlanner.PublisherProperty, "''"))));

        StringAssert.Contains(ex.Message, SingleFileManifestPlanner.PublisherProperty);
        StringAssert.Contains(ex.Message, "CN=");
    }

    [TestMethod]
    public void Plan_EmptyPublisher_FallsBackToTheDefault()
    {
        // An empty value reads as "undeclared", so it must take the default rather than fail.
        var info = SingleFileManifestPlanner.Plan(
            SingleFile(), Props((SingleFileManifestPlanner.PublisherProperty, "")), defaultPublisher: "CN=tester");

        Assert.AreEqual("CN=tester", info.PublisherDN);
    }

    #endregion

    #region Capabilities

    [TestMethod]
    public void Plan_NoCapabilitiesDeclared_YieldsNone()
    {
        // The common app declares nothing: runFullTrust is template boilerplate, not user input.
        Assert.AreEqual(0, SingleFileManifestPlanner.Plan(SingleFile(), Props()).Capabilities.Count);
    }

    [TestMethod]
    public void Plan_Capabilities_AreResolvedToElementAndNamespace()
    {
        var info = SingleFileManifestPlanner.Plan(
            SingleFile(),
            Props((SingleFileManifestPlanner.CapabilitiesProperty, "systemAIModels;microphone")));

        Assert.AreEqual(2, info.Capabilities.Count);
        Assert.AreEqual("systemai", info.Capabilities[0].Prefix);
        Assert.IsTrue(info.Capabilities[1].IsDeviceCapability);
    }

    [TestMethod]
    public void Plan_UnusableCapability_IsACommandError()
    {
        // Surfaced at plan time so the user gets an actionable message, rather than a manifest Windows
        // refuses at registration with an opaque HRESULT.
        var ex = Assert.ThrowsExactly<ProjectRunException>(() =>
            SingleFileManifestPlanner.Plan(
                SingleFile(),
                Props((SingleFileManifestPlanner.CapabilitiesProperty, "notARealCapability"))));

        StringAssert.Contains(ex.Message, SingleFileManifestPlanner.CapabilitiesProperty);
        StringAssert.Contains(ex.Message, "notARealCapability");
    }

    #endregion

    #region Authored manifest resolution

    private static DirectoryInfo NewAppDirectory()
    {
        var directory = new DirectoryInfo(Path.Join(Path.GetTempPath(), $"sfmp_{Guid.NewGuid():N}"));
        directory.Create();
        return directory;
    }

    [TestMethod]
    public void FindAuthoredManifest_NoneAuthored_ReturnsNull()
    {
        var directory = NewAppDirectory();
        try
        {
            var singleFile = new FileInfo(Path.Join(directory.FullName, "counter.cs"));
            Assert.IsNull(SingleFileManifestPlanner.FindAuthoredManifest(singleFile, Props()));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [TestMethod]
    public void FindAuthoredManifest_IgnoresDirectoryWideNames()
    {
        // foo.cs and bar.cs can share a directory, so adopting a shared Package.appxmanifest would run
        // one app under another's identity.
        var directory = NewAppDirectory();
        try
        {
            var singleFile = new FileInfo(Path.Join(directory.FullName, "counter.cs"));
            File.WriteAllText(Path.Join(directory.FullName, "Package.appxmanifest"), "<Package />");
            File.WriteAllText(Path.Join(directory.FullName, "appxmanifest.xml"), "<Package />");

            Assert.IsNull(SingleFileManifestPlanner.FindAuthoredManifest(singleFile, Props()));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [TestMethod]
    public void FindAuthoredManifest_PerFileName_IsDiscovered()
    {
        var directory = NewAppDirectory();
        try
        {
            var singleFile = new FileInfo(Path.Join(directory.FullName, "counter.cs"));
            var authoredPath = Path.Join(directory.FullName, "counter.appxmanifest");
            File.WriteAllText(authoredPath, "<Package />");

            var found = SingleFileManifestPlanner.FindAuthoredManifest(singleFile, Props());

            Assert.IsNotNull(found);
            Assert.AreEqual(authoredPath, found.File.FullName);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [TestMethod]
    public void FindAuthoredManifest_ManifestPathProperty_WinsOverPerFileName()
    {
        var directory = NewAppDirectory();
        try
        {
            var singleFile = new FileInfo(Path.Join(directory.FullName, "counter.cs"));
            File.WriteAllText(Path.Join(directory.FullName, "counter.appxmanifest"), "<Package />");
            var declaredPath = Path.Join(directory.FullName, "Custom.appxmanifest");
            File.WriteAllText(declaredPath, "<Package />");

            var found = SingleFileManifestPlanner.FindAuthoredManifest(
                singleFile,
                Props((SingleFileManifestPlanner.ManifestPathProperty, "Custom.appxmanifest")));

            Assert.IsNotNull(found);
            Assert.AreEqual(declaredPath, found.File.FullName);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [TestMethod]
    public void FindAuthoredManifest_ManifestPathPointsNowhere_IsRejected()
    {
        var directory = NewAppDirectory();
        try
        {
            var singleFile = new FileInfo(Path.Join(directory.FullName, "counter.cs"));

            var ex = Assert.ThrowsExactly<ProjectRunException>(() =>
                SingleFileManifestPlanner.FindAuthoredManifest(
                    singleFile,
                    Props((SingleFileManifestPlanner.ManifestPathProperty, "Missing.appxmanifest"))));

            StringAssert.Contains(ex.Message, "no file exists there");
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [TestMethod]
    public void ResolvePackageName_NoAuthoredManifest_UsesTheInferredIdentity()
    {
        var directory = NewAppDirectory();
        try
        {
            var singleFile = new FileInfo(Path.Join(directory.FullName, "counter.cs"));

            Assert.AreEqual(
                "com.contoso.counter",
                SingleFileManifestPlanner.ResolvePackageName(
                    singleFile,
                    Props((SingleFileManifestPlanner.PackageNameProperty, "com.contoso.counter"))));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [TestMethod]
    public void ResolvePackageName_AuthoredManifest_ReadsItsIdentityInsteadOfInferring()
    {
        // The authored manifest is what actually registers, so its Identity/@Name must win over any
        // WinAppPackageName the file also declares.
        var directory = NewAppDirectory();
        try
        {
            var singleFile = new FileInfo(Path.Join(directory.FullName, "counter.cs"));
            File.WriteAllText(
                Path.Join(directory.FullName, "counter.appxmanifest"),
                """
                <?xml version="1.0" encoding="utf-8"?>
                <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
                  <Identity Name="Authored.Identity" Publisher="CN=Someone" Version="1.0.0.0" />
                </Package>
                """);

            Assert.AreEqual(
                "Authored.Identity",
                SingleFileManifestPlanner.ResolvePackageName(
                    singleFile,
                    Props((SingleFileManifestPlanner.PackageNameProperty, "inferred.name"))));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [TestMethod]
    public void ResolvePackageName_AuthoredManifestWithoutIdentity_IsRejected()
    {
        var directory = NewAppDirectory();
        try
        {
            var singleFile = new FileInfo(Path.Join(directory.FullName, "counter.cs"));
            File.WriteAllText(
                Path.Join(directory.FullName, "counter.appxmanifest"),
                """
                <?xml version="1.0" encoding="utf-8"?>
                <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" />
                """);

            var ex = Assert.ThrowsExactly<ProjectRunException>(() =>
                SingleFileManifestPlanner.ResolvePackageName(singleFile, Props()));

            StringAssert.Contains(ex.Message, "no Identity/@Name");
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [TestMethod]
    public void ResolvePackageName_InvalidVersion_DoesNotBlockIdentityResolution()
    {
        // An app registered earlier, then edited to add a bad version, must still be unregisterable:
        // the version does not affect WHICH package is registered, only what a generated manifest would
        // contain. Plan still rejects it — this is scoped to identity resolution.
        var directory = NewAppDirectory();
        try
        {
            var singleFile = new FileInfo(Path.Join(directory.FullName, "counter.cs"));
            var props = Props((SingleFileManifestPlanner.VersionProperty, "70000.0"));

            StringAssert.Matches(SingleFileManifestPlanner.ResolvePackageName(singleFile, props), PathHashedName("counter"));
            Assert.ThrowsExactly<ProjectRunException>(() => SingleFileManifestPlanner.Plan(singleFile, props),
                "Generating a manifest with that version must still fail");
        }
        finally
        {
            directory.Delete(true);
        }
    }

    #endregion
}

