// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace WinApp.HotReload.Tests;

[TestClass]
public sealed class CompilerPipelineTests
{
    [TestMethod]
    public void NamedElementWhoseTypeChangesIsReported()
    {
        const string before = """
            partial class MainPage
            {
                private global::Microsoft.UI.Xaml.Controls.TextBlock Later;
                private global::Microsoft.UI.Xaml.Controls.Button Go;
            }
            """;
        const string after = """
            partial class MainPage
            {
                private global::Microsoft.UI.Xaml.Controls.TextBox Later;
                private global::Microsoft.UI.Xaml.Controls.Button Go;
            }
            """;

        Assert.AreEqual("x:Name 'Later' changes from TextBlock to TextBox", HotReloadSession.FieldTypeChange(before, after));
    }

    [TestMethod]
    public void NamedElementsAddedOrRemovedAreNotTypeChanges()
    {
        const string before = "partial class P { private global::X.TextBlock A; private global::X.Button B; }";
        const string after = "partial class P { private global::X.TextBlock A; private global::X.Grid C; }";

        Assert.IsNull(HotReloadSession.FieldTypeChange(before, after));
    }

    [TestMethod]
    public void BaselineCopyRecordsItsOwnPdb()
    {
        var directory = Directory.CreateTempSubdirectory("winapp-pdb-test").FullName;
        try
        {
            // Built "in obj", with a long PDB path recorded, then copied elsewhere as the session does.
            var obj = Path.Combine(directory, "obj", "x64", "Debug", "net10.0-windows10.0.26100.0", "win-x64");
            Directory.CreateDirectory(obj);
            var assembly = Path.Combine(obj, "App.dll");
            var compilation = CSharpCompilation.Create("App", [CSharpSyntaxTree.ParseText("class C { }")],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using (var pe = File.Create(assembly))
            using (var pdb = File.Create(Path.ChangeExtension(assembly, ".pdb")))
            {
                Assert.IsTrue(compilation.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb, pdbFilePath: Path.ChangeExtension(assembly, ".pdb"))).Success);
            }
            var copy = Path.Combine(directory, "b", "App.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(assembly, copy);

            HotReloadSession.PointToOwnPdb(copy);

            using var reader = new PEReader(File.OpenRead(copy));
            var codeView = reader.ReadDebugDirectory().Single(e => e.Type == DebugDirectoryEntryType.CodeView);
            Assert.AreEqual(Path.ChangeExtension(copy, ".pdb"), reader.ReadCodeViewDebugDirectoryData(codeView).Path);
            Assert.IsTrue(reader.HasMetadata);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void OnlyANewChecksumIsNoChange()
    {
        const string before = "#pragma checksum \"C:\\a\\Page.xaml\" \"{8829d00f}\" \"AAAA\"\r\nclass P\r\n{\r\n}\r\n";
        Assert.IsTrue(HotReloadSession.DiffersOnlyInChecksums(before, before.Replace("AAAA", "BBBB")));
        Assert.IsFalse(HotReloadSession.DiffersOnlyInChecksums(before, before.Replace("AAAA", "BBBB").Replace("class P", "class Q")));
    }

    [TestMethod]
    public void TheImageTakesTheVersionPass2SawAtBuild()
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("[assembly: System.Reflection.AssemblyVersionAttribute(\"3.0.0.0\")]\n[assembly: System.Reflection.AssemblyTitle(\"A\")]\nclass C { }");
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("App", [tree], [Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        Assert.AreEqual(new Version(3, 0, 0, 0), compilation.Assembly.Identity.Version);
        Assert.AreEqual(new Version(0, 0, 0, 0), HotReloadSession.WithAssemblyVersion(compilation, new Version(0, 0, 0, 0)).Assembly.Identity.Version);
        Assert.AreEqual(new Version(1, 2, 0, 0), HotReloadSession.WithAssemblyVersion(compilation, new Version(1, 2, 0, 0)).Assembly.Identity.Version);
    }

    [TestMethod]
    public void AChangedMethodOnNavigatedToCallsIsNoted()
    {
        static Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax Class(string text) =>
            Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(text).GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>().First();
        const string before = "class P { protected override void OnNavigatedTo(object e) { SetUp(); } void SetUp() { Add(\"Overview\"); } void Other() { } }";
        Assert.AreEqual("P.SetUp already ran when P was opened: navigate to P again to see this change",
            HotReloadSession.NavigationNote(Class(before.Replace("\"Overview\"", "\"Overview HR\"")), Class(before)));
        Assert.IsNull(HotReloadSession.NavigationNote(Class(before.Replace("void Other() { }", "void Other() { Add(1); }")), Class(before)));
    }

    [TestMethod]
    public void AGeneratedFileWithoutCodeIsNoChange()
    {
        Assert.IsTrue(HotReloadSession.HasNoCode("\uFEFF \r\n"));
        Assert.IsTrue(HotReloadSession.HasNoCode("#pragma checksum \"C:\\a\\Brand.xaml\" \"{8829d00f}\" \"AAAA\"\r\n// <auto-generated />\r\n"));
        Assert.IsFalse(HotReloadSession.HasNoCode("namespace App { partial class P { } }"));
        Assert.IsFalse(HotReloadSession.HasNoCode("[assembly: System.Reflection.AssemblyMetadata(\"a\", \"b\")]"));
    }

    [TestMethod]
    public void XamlTypeInfoMembersMapToTheirTypes()
    {
        const string typeInfo = """
            switch (longMemberName)
            {
            case "App.Controls.Card.Hint":
                userType = (global::App.App_XamlTypeInfo.XamlUserType)GetXamlTypeByName("App.Controls.Card");
                xamlMember = new global::App.App_XamlTypeInfo.XamlMember(this, "Hint", "String");
                break;
            }
            """;
        var members = HotReloadSession.XamlMembers(typeInfo);
        Assert.AreEqual(1, members.Count);
        Assert.AreEqual("App.Controls.Card", members["App.Controls.Card.Hint"]);
    }
}
