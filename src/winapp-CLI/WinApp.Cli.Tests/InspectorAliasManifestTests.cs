// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class InspectorAliasManifestTests
{
    private const string LegacyNamespace = "http://schemas.microsoft.com/appx/manifest/uap/windows10/3";
    private const string Uap8Namespace = "http://schemas.microsoft.com/appx/manifest/uap/windows10/8";
    private static readonly string[] CoexistingAliases = ["authored.exe", "second-alias.exe", "inspector.exe"];
    private static readonly string[] AllApplicationAliases = ["first.exe", "authored.exe", "second-alias.exe"];
    private static readonly string[] FirstApplicationAliases = ["first.exe"];
    private static readonly string[] EmptyAlias = [""];

    [TestMethod]
    [DataRow("uap3", "desktop")]
    [DataRow("uap3", "uap8")]
    [DataRow("uap5", "uap5")]
    [DataRow("uap5", "uap8")]
    public void SchemaAliases_ReadingAndMutationShareAllSupportedLeaves(string container, string leaf)
    {
        var doc = Manifest(container, leaf);
        var before = doc.ToXml();
        var aliases = doc.GetExecutionAliases("Second");
        Assert.HasCount(2, aliases);
        Assert.AreEqual("authored.exe", aliases[0]);
        Assert.IsTrue(doc.GetExecutionAliasesClaimedByOtherApplications("First").Contains("AUTHORED.exe"));
        Assert.AreEqual(AddExecutionAliasStatus.ConflictingAliasExists,
            doc.AddExecutionAlias("authored.exe", "First", ExecutionAliasConflictPolicy.Coexist).Status);
        Assert.AreEqual("authored.exe", doc.EnsureExecutionAlias("generated.exe", "Second"));
        Assert.AreEqual(AddExecutionAliasStatus.AlreadyExists, doc.AddExecutionAlias("authored.exe", "Second").Status);
        Assert.AreEqual(AddExecutionAliasStatus.AlreadyExists,
            doc.AddExecutionAlias("authored.exe", "Second", ExecutionAliasConflictPolicy.Coexist).Status);
        Assert.AreEqual(before, doc.ToXml());

        var originalLeaves = doc.Document.Descendants().Where(e => e.Name.LocalName == "ExecutionAlias")
            .Select(e => e.ToString()).ToArray();
        Assert.AreEqual(AddExecutionAliasStatus.Added,
            doc.AddExecutionAlias("inspector.exe", "Second", ExecutionAliasConflictPolicy.Coexist).Status);
        CollectionAssert.AreEqual(CoexistingAliases, doc.GetExecutionAliases("Second").ToArray());
        CollectionAssert.AreEqual(originalLeaves, doc.Document.Descendants()
            .Where(e => e.Name.LocalName == "ExecutionAlias").Take(2).Select(e => e.ToString()).ToArray());
        var once = doc.ToXml();
        Assert.AreEqual(AddExecutionAliasStatus.AlreadyExists,
            doc.AddExecutionAlias("inspector.exe", "Second", ExecutionAliasConflictPolicy.Coexist).Status);
        Assert.AreEqual(once, doc.ToXml());
    }

    [TestMethod]
    [DataRow("uap3", "desktop")]
    [DataRow("uap3", "uap8")]
    [DataRow("uap5", "uap5")]
    [DataRow("uap5", "uap8")]
    public void SchemaAliases_PublicExtractionKeepsAllApplicationsAndOrder(string container, string leaf)
    {
        var doc = Manifest(container, leaf);
        doc.AddExecutionAlias("first.exe", "First");
        CollectionAssert.AreEqual(AllApplicationAliases,
            MsixService.ExtractExecutionAliases(doc.ToXml()));
        CollectionAssert.AreEqual(FirstApplicationAliases, doc.GetExecutionAliases().ToArray());
    }

    [TestMethod]
    public void SchemaAliases_PublicExtractionRetainsEmptyNamesForLaunchValidation()
    {
        var doc = Manifest("uap5");
        var aliases = doc.Document.Descendants().Where(e => e.Name.LocalName == "ExecutionAlias").ToArray();
        aliases[0].SetAttributeValue("Alias", "");
        aliases[1].Attribute("Alias")!.Remove();
        CollectionAssert.AreEqual(EmptyAlias, MsixService.ExtractExecutionAliases(doc.ToXml()));
        Assert.IsEmpty(doc.GetExecutionAliases("Second"));
    }

    [TestMethod]
    [DataRow("uap5")]
    [DataRow("uap3")]
    public void Coexist_PreservesMetadataAndIsIdempotent(string prefix)
    {
        var doc = Manifest(prefix);
        var extension = doc.Document.Descendants().Single(e => e.Name.LocalName == "Extension");
        var metadata = extension.Attributes().Select(a => a.ToString()).ToArray();
        var result = doc.AddExecutionAlias("inspector.exe", "Second", ExecutionAliasConflictPolicy.Coexist);
        Assert.AreEqual(AddExecutionAliasStatus.Added, result.Status);
        CollectionAssert.AreEqual(metadata, extension.Attributes().Select(a => a.ToString()).ToArray());
        CollectionAssert.AreEqual(CoexistingAliases, doc.GetExecutionAliases("Second").ToArray());
        Assert.IsEmpty(doc.GetExecutionAliases("First"));
        Assert.AreEqual(1, doc.Document.Descendants().Count(e => e.Name.LocalName == "Extension"));
        var leafNamespace = prefix == "uap3" ? AppxManifestDocument.DesktopNs : AppxManifestDocument.Uap5Ns;
        Assert.IsTrue(extension.Descendants().Where(e => e.Name.LocalName == "ExecutionAlias")
            .All(e => e.Name.Namespace == leafNamespace));
        var once = doc.ToXml();
        Assert.AreEqual(AddExecutionAliasStatus.AlreadyExists,
            doc.AddExecutionAlias("INSPECTOR.exe", "Second", ExecutionAliasConflictPolicy.Coexist).Status);
        Assert.AreEqual(once, doc.ToXml());
    }

    [TestMethod]
    [DataRow("authored.exe")]
    [DataRow("inspector.exe")]
    public void LegacyAlias_WrongTargetRefusesExistingAndNewNames(string alias)
    {
        var doc = Manifest("uap3");
        doc.Document.Descendants().Single(e => e.Name.LocalName == "Extension").SetAttributeValue("Executable", "different.exe");
        var before = doc.ToXml();
        Assert.AreEqual(AddExecutionAliasStatus.CoexistenceUnsafe,
            doc.AddExecutionAlias(alias, "Second", ExecutionAliasConflictPolicy.Coexist).Status);
        Assert.AreEqual(before, doc.ToXml());
    }

    [TestMethod]
    public void LegacyAlias_SafeExistingNameIsUnchanged()
    {
        var doc = Manifest("uap3");
        var before = doc.ToXml();
        Assert.AreEqual(AddExecutionAliasStatus.AlreadyExists,
            doc.AddExecutionAlias("AUTHORED.exe", "Second", ExecutionAliasConflictPolicy.Coexist).Status);
        Assert.AreEqual(before, doc.ToXml());
    }

    [TestMethod]
    [DataRow(ExecutionAliasConflictPolicy.PreserveExisting)]
    [DataRow(ExecutionAliasConflictPolicy.Reject)]
    [DataRow(ExecutionAliasConflictPolicy.Coexist)]
    public void AddAlias_SiblingClaimIsRejectedWithoutChangingDocument(object policy)
    {
        var doc = Manifest("uap3");
        var before = doc.ToXml();
        var result = doc.AddExecutionAlias("AUTHORED.exe", "First", (ExecutionAliasConflictPolicy)policy);
        Assert.AreEqual(AddExecutionAliasStatus.ConflictingAliasExists, result.Status);
        StringAssert.Contains(result.ErrorMessage, "another application");
        Assert.AreEqual(before, doc.ToXml());
    }

    [TestMethod]
    [DataRow("Executable", "other.exe")]
    [DataRow("Executable", "$targetnametoken$.exe")]
    [DataRow("EntryPoint", "Other.EntryPoint")]
    public void Coexist_RefusesDifferentTargetWithoutEditing(string attribute, string value)
    {
        var doc = Manifest("uap5");
        doc.Document.Descendants().Single(e => e.Name.LocalName == "Extension").SetAttributeValue(attribute, value);
        var before = doc.ToXml();
        Assert.AreEqual(AddExecutionAliasStatus.CoexistenceUnsafe,
            doc.AddExecutionAlias("inspector.exe", "Second", ExecutionAliasConflictPolicy.Coexist).Status);
        Assert.AreEqual(before, doc.ToXml());
        Assert.AreEqual(AddExecutionAliasStatus.CoexistenceUnsafe,
            doc.AddExecutionAlias("authored.exe", "Second", ExecutionAliasConflictPolicy.Coexist).Status,
            "An already-declared alias does not bypass target validation.");
    }

    [TestMethod]
    public void PublicPolicies_PreserveAuthoredNameOrReportConflict()
    {
        var doc = Manifest("uap5");
        var before = doc.ToXml();
        Assert.AreEqual("authored.exe", doc.EnsureExecutionAlias("new.exe", "Second"));
        Assert.AreEqual(AddExecutionAliasStatus.ConflictingAliasExists, doc.AddExecutionAlias("new.exe", "Second").Status);
        Assert.AreEqual(AddExecutionAliasStatus.AlreadyExists, doc.AddExecutionAlias("SECOND-alias.exe", "Second").Status);
        Assert.AreEqual(before, doc.ToXml());
    }

    [TestMethod]
    public void Coexist_InheritsApplicationTargetAndRefusesMultipleExtensions()
    {
        var doc = Manifest("uap5");
        var extension = doc.Document.Descendants().Single(e => e.Name.LocalName == "Extension");
        extension.Attribute("Executable")!.Remove();
        extension.Attribute("EntryPoint")!.Remove();
        Assert.AreEqual(AddExecutionAliasStatus.Added,
            doc.AddExecutionAlias("inspector.exe", "Second", ExecutionAliasConflictPolicy.Coexist).Status);
        extension.AddAfterSelf(new System.Xml.Linq.XElement(extension));
        var before = doc.ToXml();
        Assert.AreEqual(AddExecutionAliasStatus.CoexistenceUnsafe,
            doc.AddExecutionAlias("another.exe", "Second", ExecutionAliasConflictPolicy.Coexist).Status);
        Assert.AreEqual(before, doc.ToXml());
    }

    [TestMethod]
    public void ExpectedTarget_UsesSelectedApplicationAndFinalLayout()
    {
        var target = Manifest("uap5").GetExecutionAliasTarget(@"C:\staged", "Second");
        Assert.AreEqual(@"C:\staged\second.exe", target.TargetExecutable);
        StringAssert.EndsWith(target.ApplicationUserModelId, "!Second");
        Assert.AreEqual(AppLauncherService.ComputeFamilyName("Contoso", "CN=Test"), target.PackageFamilyName);
    }

    [TestMethod]
    [DataRow(@"..\other.exe")]
    [DataRow(@"C:\other.exe")]
    [DataRow("$targetnametoken$.exe")]
    public void ExpectedTarget_RejectsUnsafeOrUnresolvedExecutable(string executable)
    {
        var doc = Manifest("uap5");
        doc.Document.Descendants(AppxManifestDocument.DefaultNs + "Application").Last().SetAttributeValue("Executable", executable);
        Assert.Throws<InvalidOperationException>(() => doc.GetExecutionAliasTarget(@"C:\staged", "Second"));
    }

    private static AppxManifestDocument Manifest(string prefix, string? leaf = null)
    {
        var leafPrefix = leaf ?? (prefix == "uap3" ? "desktop" : prefix);
        return AppxManifestDocument.Parse($"""
        <Package xmlns="{AppxManifestDocument.DefaultNs}" xmlns:uap5="{AppxManifestDocument.Uap5Ns}" xmlns:uap3="{LegacyNamespace}" xmlns:uap8="{Uap8Namespace}" xmlns:desktop="{AppxManifestDocument.DesktopNs}">
          <Identity Name="Contoso" Publisher="CN=Test" Version="1.0.0.0" />
          <Applications>
            <Application Id="First" Executable="first.exe" />
            <Application Id="Second" Executable="second.exe" EntryPoint="Windows.FullTrustApplication">
              <Extensions>
                <{prefix}:Extension Category="windows.appExecutionAlias" Executable="second.exe" EntryPoint="Windows.FullTrustApplication">
                  <{prefix}:AppExecutionAlias>
                    <{leafPrefix}:ExecutionAlias Alias="authored.exe" />
                    <{leafPrefix}:ExecutionAlias Alias="second-alias.exe" />
                  </{prefix}:AppExecutionAlias>
                </{prefix}:Extension>
              </Extensions>
            </Application>
          </Applications>
        </Package>
        """);
    }
}
