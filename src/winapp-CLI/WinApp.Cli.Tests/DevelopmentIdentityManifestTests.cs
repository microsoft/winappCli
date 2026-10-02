// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Xml.Linq;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class DevelopmentIdentityManifestTests
{
    private const string ManifestXml = """
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
            xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
            xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"
            xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
            xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
            xmlns:desktop4="http://schemas.microsoft.com/appx/manifest/desktop/windows10/4"
            IgnorableNamespaces="uap uap5 uap10 desktop desktop4">
          <Identity Name="Contoso.App" Publisher="CN=Contoso" Version="1.2.3.4" ProcessorArchitecture="x64" />
          <Properties><DisplayName>ms-resource:AppName</DisplayName><Description>App description</Description></Properties>
          <Dependencies><PackageDependency Name="Microsoft.WindowsAppRuntime.1.8" Publisher="CN=Microsoft Corporation" MinVersion="1.0.0.0" /></Dependencies>
          <Resources><Resource Language="en-US" /><Resource Language="fr-FR" /></Resources>
          <Applications>
            <Application Id="App" Executable="App.exe" EntryPoint="Windows.FullTrustApplication">
              <uap:VisualElements DisplayName="ms-resource:AppName" Square150x150Logo="Assets\Logo.png" />
            </Application>
          </Applications>
        </Package>
        """;

    [TestMethod]
    [DataRow("uap5")]
    [DataRow("desktop")]
    public void ApplyDevelopmentIdentity_ChangesOnlyNameAndAllAuthoredAliasValues(string prefix)
    {
        var original = Manifest();
        AddAliases(original, prefix);
        var staged = AppxManifestDocument.Parse(original.ToXml());
        var unchanged = new XDocument(original.Document);
        var identity = DevelopmentIdentityHelper.Create(staged, Environment.CurrentDirectory, Environment.CurrentDirectory, true);

        staged.ApplyDevelopmentIdentity(identity);

        Assert.AreEqual("Contoso.App", original.IdentityName);
        Assert.AreEqual(identity.EffectivePackageName, staged.IdentityName);
        CollectionAssert.AreEqual(identity.Aliases.Values.ToArray(), staged.GetExecutionAliases().ToArray());
        Assert.IsTrue(XNode.DeepEquals(unchanged, original.Document));
        staged.IdentityName = original.IdentityName;
        foreach (var alias in staged.Document.Descendants().Where(e => e.Name.LocalName == "ExecutionAlias"))
        {
            alias.SetAttributeValue("Alias", identity.Aliases.Single(pair => pair.Value == alias.Attribute("Alias")!.Value).Key);
        }
        Assert.IsTrue(XNode.DeepEquals(original.Document, staged.Document));
    }

    [TestMethod]
    public void Create_PreflightsAliasesWithoutMutatingSourceOrRequiringAliasLaunch()
    {
        var document = Manifest();
        AddAliases(document);
        var xml = document.ToXml();
        var identity = DevelopmentIdentityHelper.Create(document, Environment.CurrentDirectory, Environment.CurrentDirectory, true);
        Assert.HasCount(2, identity.Aliases);
        Assert.AreEqual(xml, document.ToXml());
    }

    [TestMethod]
    public void Create_StagesExistingGeneratedAliasUsingEffectiveFamilyWithoutChangingSource()
    {
        var source = Manifest();
        var originalDefault = ExecutionAliasResolver.BuildDefaultAliasName(
            DevelopmentIdentityHelper.ComputeFamilyName(source.IdentityName!, source.IdentityPublisher!));
        Assert.IsNotNull(originalDefault);
        source.EnsureExecutionAlias(originalDefault);
        var originalXml = source.ToXml();
        var staged = AppxManifestDocument.Parse(originalXml);

        var identity = DevelopmentIdentityHelper.Create(staged, Environment.CurrentDirectory, Environment.CurrentDirectory, true);
        var effectiveDefault = ExecutionAliasResolver.BuildDefaultAliasName(identity.PackageFamilyName);
        Assert.AreEqual(effectiveDefault, identity.Aliases[originalDefault]);
        staged.ApplyDevelopmentIdentity(identity);

        Assert.AreEqual(effectiveDefault, staged.GetExecutionAliases().Single());
        Assert.AreEqual(effectiveDefault, staged.EnsureExecutionAlias(effectiveDefault!));
        Assert.AreEqual(originalXml, source.ToXml());
        Assert.AreEqual(originalDefault, source.GetExecutionAliases().Single());
    }

    [TestMethod]
    public void ApplyDevelopmentIdentity_FailsBeforeAnyChangesForIncompleteOrInvalidMappings()
    {
        var document = Manifest();
        AddAliases(document);
        var xml = document.ToXml();
        var identity = DevelopmentIdentityHelper.Create(document, Environment.CurrentDirectory, Environment.CurrentDirectory, true);
        Assert.ThrowsExactly<InvalidOperationException>(() => document.ApplyDevelopmentIdentity(identity with
        {
            Aliases = new Dictionary<string, string> { ["first.exe"] = "safe.exe" },
        }));
        Assert.AreEqual(xml, document.ToXml());
        Assert.ThrowsExactly<InvalidOperationException>(() => document.ApplyDevelopmentIdentity(identity with
        {
            Aliases = new Dictionary<string, string> { ["first.exe"] = "safe.exe", ["second.exe"] = @"outside\bad.exe" },
        }));
        Assert.AreEqual(xml, document.ToXml());
    }

    [TestMethod]
    public void ApplyDevelopmentIdentity_RejectsMismatchedContextWithoutChangingManifest()
    {
        var document = Manifest();
        var xml = document.ToXml();
        var identity = DevelopmentIdentityHelper.Create(document, Environment.CurrentDirectory, Environment.CurrentDirectory, true);
        Assert.ThrowsExactly<InvalidOperationException>(() => document.ApplyDevelopmentIdentity(identity with { Publisher = "CN=Different" }));
        Assert.AreEqual(xml, document.ToXml());
    }

    [TestMethod]
    public void EnsureExecutionAlias_OriginalModeStillPreservesAuthoredAlias()
    {
        var document = Manifest();
        AddAliases(document);
        var xml = document.ToXml();
        Assert.AreEqual("first.exe", document.EnsureExecutionAlias("generated.exe"));
        document.ApplyDevelopmentIdentity(DevelopmentIdentityHelper.Create(
            document, Environment.CurrentDirectory, Environment.CurrentDirectory, false));
        Assert.AreEqual(xml, document.ToXml());
    }

    [TestMethod]
    [DataRow("windows.protocol")]
    [DataRow("windows.fileTypeAssociation")]
    [DataRow("windows.comServer")]
    [DataRow("windows.service")]
    [DataRow("windows.startupTask")]
    [DataRow("windows.publisherCacheFolders")]
    [DataRow("windows.futureUnknownContract")]
    [DataRow("windows.activatableClass.outOfProcessServer")]
    [DataRow("windows.activatableClass.proxyStub")]
    public void ValidateUniqueIdentitySupport_RejectsUnknownCategoryAtBothScopes(string category)
    {
        foreach (var packageScoped in new[] { false, true })
        {
            var document = Manifest();
            var parent = packageScoped ? document.Document.Root! : document.GetFirstApplicationElement()!;
            parent.Add(new XElement(AppxManifestDocument.DefaultNs + "Extensions",
                new XElement(AppxManifestDocument.DefaultNs + "Extension", new XAttribute("Category", category))));
            var error = Assert.ThrowsExactly<InvalidOperationException>(document.ValidateUniqueIdentitySupport);
            Assert.Contains(category, error.Message);
            Assert.Contains("--unique-identity", error.Message);
        }
    }

    [TestMethod]
    public void ValidateUniqueIdentitySupport_RejectsPackageScopedAliasAndWrongNamespace()
    {
        var document = Manifest();
        AddAliases(document);
        var extensions = document.GetFirstApplicationElement()!.Element(AppxManifestDocument.DefaultNs + "Extensions")!;
        extensions.Remove();
        document.Document.Root!.Add(extensions);
        Assert.ThrowsExactly<InvalidOperationException>(document.ValidateUniqueIdentitySupport);
        document = Manifest();
        AddAliases(document);
        document.Document.Descendants(AppxManifestDocument.Uap5Ns + "Extension").Single().Name = XNamespace.Get("urn:unknown") + "Extension";
        Assert.ThrowsExactly<InvalidOperationException>(document.ValidateUniqueIdentitySupport);
    }

    [TestMethod]
    [DataRow("CLSID")]
    [DataRow("SupportedProtocols")]
    [DataRow("AllowOverride")]
    public void ValidateUniqueIdentitySupport_RejectsExtraAliasContracts(string attribute)
    {
        var document = Manifest();
        AddAliases(document);
        document.Document.Descendants(AppxManifestDocument.Uap5Ns + "ExecutionAlias").First().SetAttributeValue(attribute, "value");
        var error = Assert.ThrowsExactly<InvalidOperationException>(document.ValidateUniqueIdentitySupport);
        Assert.Contains(attribute, error.Message);
        Assert.Contains("windows.appExecutionAlias", error.Message);
    }

    [TestMethod]
    public void ValidateUniqueIdentitySupport_RejectsNestedAliasProtocolElement()
    {
        var document = Manifest();
        AddAliases(document);
        document.Document.Descendants(AppxManifestDocument.Uap5Ns + "ExecutionAlias").First()
            .Add(new XElement(AppxManifestDocument.Uap5Ns + "SupportedProtocols"));
        Assert.ThrowsExactly<InvalidOperationException>(document.ValidateUniqueIdentitySupport);
    }

    [TestMethod]
    public void Create_RejectsDuplicateAliasDeclarations()
    {
        var document = Manifest();
        AddAliases(document);
        document.Document.Descendants(AppxManifestDocument.Uap5Ns + "ExecutionAlias").Last().SetAttributeValue("Alias", "FIRST.EXE");
        Assert.ThrowsExactly<InvalidOperationException>(() => DevelopmentIdentityHelper.Create(
            document, Environment.CurrentDirectory, Environment.CurrentDirectory, true));
    }

    [TestMethod]
    public void ValidateUniqueIdentitySupport_AllowsOrdinaryWinUiInProcessWinRtRegistration()
    {
        var document = Manifest();
        AddInProcessServer(document);
        document.ValidateUniqueIdentitySupport();
        var identity = DevelopmentIdentityHelper.Create(document, Environment.CurrentDirectory, Environment.CurrentDirectory, true);
        document.ApplyDevelopmentIdentity(identity);
        Assert.AreEqual("Microsoft.UI.Xaml.dll", document.Document.Descendants(AppxManifestDocument.DefaultNs + "Path").Single().Value);
        Assert.AreEqual("Microsoft.UI.Xaml.Application", document.Document.Descendants(AppxManifestDocument.DefaultNs + "ActivatableClass").Single().Attribute("ActivatableClassId")!.Value);
    }

    [TestMethod]
    public void ValidateUniqueIdentitySupport_RejectsComMasqueradingAsInProcessWinRt()
    {
        var document = Manifest();
        AddInProcessServer(document);
        document.Document.Descendants(AppxManifestDocument.DefaultNs + "InProcessServer").Single()
            .Add(new XElement(XNamespace.Get("http://schemas.microsoft.com/appx/manifest/com/windows10") + "Class",
                new XAttribute("Id", Guid.NewGuid().ToString("D"))));
        Assert.ThrowsExactly<InvalidOperationException>(document.ValidateUniqueIdentitySupport);
    }

    [TestMethod]
    public void ValidateUniqueIdentitySupport_RejectsAppScopedOrWrongNamespaceWinRt()
    {
        var document = Manifest();
        AddInProcessServer(document);
        var extensions = document.GetExtensionsElement()!;
        extensions.Remove();
        document.GetFirstApplicationElement()!.Add(extensions);
        Assert.ThrowsExactly<InvalidOperationException>(document.ValidateUniqueIdentitySupport);
        document = Manifest();
        AddInProcessServer(document);
        document.GetExtensionsElement()!.Elements().Single().Name = AppxManifestDocument.Uap5Ns + "Extension";
        Assert.ThrowsExactly<InvalidOperationException>(document.ValidateUniqueIdentitySupport);
    }

    [TestMethod]
    [DataRow("Framework", "true")]
    [DataRow("ResourcePackage", "true")]
    [DataRow("ResourcePackage", "1")]
    [DataRow("AllowExternalContent", "true")]
    [DataRow("AllowExternalContent", "1")]
    public void ValidateUniqueIdentitySupport_RejectsNonApplicationPackageShapes(string element, string value)
    {
        var document = Manifest();
        var ns = element == "AllowExternalContent" ? AppxManifestDocument.Uap10Ns : AppxManifestDocument.DefaultNs;
        document.GetPropertiesElement()!.Add(new XElement(ns + element, value));
        var error = Assert.ThrowsExactly<InvalidOperationException>(document.ValidateUniqueIdentitySupport);
        Assert.Contains(element, error.Message);
    }

    [TestMethod]
    public void ValidateUniqueIdentitySupport_RejectsOptionalBundleAndMultipleApplications()
    {
        var optional = Manifest();
        optional.GetDependenciesElement()!.Add(new XElement(AppxManifestDocument.UapNs + "MainPackageDependency", new XAttribute("Name", "Main")));
        Assert.ThrowsExactly<InvalidOperationException>(optional.ValidateUniqueIdentitySupport);
        var bundle = AppxManifestDocument.Parse("<Bundle xmlns='http://schemas.microsoft.com/appx/2013/bundle' />");
        Assert.ThrowsExactly<InvalidOperationException>(bundle.ValidateUniqueIdentitySupport);
        var multiple = Manifest();
        multiple.GetFirstApplicationElement()!.AddAfterSelf(new XElement(AppxManifestDocument.DefaultNs + "Application", new XAttribute("Id", "Other")));
        Assert.ThrowsExactly<InvalidOperationException>(multiple.ValidateUniqueIdentitySupport);
        var empty = Manifest();
        empty.GetFirstApplicationElement()!.Remove();
        Assert.ThrowsExactly<InvalidOperationException>(empty.ValidateUniqueIdentitySupport);
    }

    [TestMethod]
    public void ValidateUniqueIdentitySupport_RejectsExplicitOldAuthorityInTextAndAttributes()
    {
        var document = Manifest();
        document.GetPropertiesElement()!.Element(AppxManifestDocument.DefaultNs + "DisplayName")!.Value = "ms-resource://Contoso.App/Resources/Name";
        Assert.ThrowsExactly<InvalidOperationException>(document.ValidateUniqueIdentitySupport);
        document = Manifest();
        document.GetVisualElements()!.SetAttributeValue("Square150x150Logo", "ms-appx://Contoso.App/Assets/Logo.png");
        Assert.ThrowsExactly<InvalidOperationException>(document.ValidateUniqueIdentitySupport);
    }

    private static AppxManifestDocument Manifest() => AppxManifestDocument.Parse(ManifestXml);

    private static void AddAliases(AppxManifestDocument document, string prefix = "uap5")
    {
        var ns = prefix == "uap5" ? AppxManifestDocument.Uap5Ns : AppxManifestDocument.DesktopNs;
        document.GetFirstApplicationElement()!.Add(new XElement(AppxManifestDocument.DefaultNs + "Extensions",
            new XElement(ns + "Extension", new XAttribute("Category", "windows.appExecutionAlias"),
                new XAttribute("Executable", "App.exe"), new XAttribute("EntryPoint", "Windows.FullTrustApplication"),
                new XElement(ns + "AppExecutionAlias",
                    new XAttribute(XNamespace.Get("http://schemas.microsoft.com/appx/manifest/desktop/windows10/4") + "Subsystem", "console"),
                    new XElement(ns + "ExecutionAlias", new XAttribute("Alias", "first.exe")),
                    new XElement(ns + "ExecutionAlias", new XAttribute("Alias", "second.exe"))))));
    }

    private static void AddInProcessServer(AppxManifestDocument document)
    {
        var ns = AppxManifestDocument.DefaultNs;
        document.Document.Root!.Add(new XElement(ns + "Extensions",
            new XElement(ns + "Extension", new XAttribute("Category", "windows.activatableClass.inProcessServer"),
                new XElement(ns + "InProcessServer", new XElement(ns + "Path", "Microsoft.UI.Xaml.dll"),
                    new XElement(ns + "ActivatableClass", new XAttribute("ActivatableClassId", "Microsoft.UI.Xaml.Application"),
                        new XAttribute("ThreadingModel", "both"))))));
    }
}
