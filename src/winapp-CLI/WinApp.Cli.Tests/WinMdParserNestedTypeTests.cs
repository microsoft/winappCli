// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using WinApp.Cli.Services.ApiSearch;

namespace WinApp.Cli.Tests;

/// <summary>
/// A nested type carries no namespace of its own: ECMA-335 puts the namespace on the
/// outermost enclosing type and links the nested type to its parent through the
/// NestedClass table. Naming one from its own namespace and name therefore records it
/// in the global namespace under its bare name, where every same-named nested type in
/// the file collides with it. Win32 metadata makes that the common case rather than a
/// corner case, so these pin the qualified naming.
/// </summary>
[TestClass]
public class WinMdParserNestedTypeTests
{
    private DirectoryInfo _tempDir = null!;

    [TestInitialize]
    public void Setup() =>
        _tempDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "winapp-nested-" + Guid.NewGuid().ToString("N")));

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            _tempDir.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A type to emit: its namespace, its name, the outer type it nests in, and whether it is publicly visible.</summary>
    private sealed record TypeSpec(string Namespace, string Name, string? DeclaredIn = null, bool IsPublic = true);

    /// <summary>
    /// Writes a minimal but valid .winmd containing the given types. Nested types are
    /// emitted with no namespace of their own and a NestedClass row pointing at their
    /// declaring type, which is exactly how a real compiler emits them.
    /// </summary>
    private string WriteWinmd(string fileName, params TypeSpec[] specs)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString("<Module>"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(
            metadata.GetOrAddString("NestedTestWinmd"),
            new Version(1, 0, 0, 0),
            default, default, default, AssemblyHashAlgorithm.None);

        var systemRuntimeRef = metadata.AddAssemblyReference(
            metadata.GetOrAddString("System.Runtime"),
            new Version(4, 0, 0, 0),
            default, default, default, default);
        var objectTypeRef = metadata.AddTypeReference(
            systemRuntimeRef, metadata.GetOrAddString("System"), metadata.GetOrAddString("Object"));

        metadata.AddTypeDefinition(
            default, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        var handles = new Dictionary<string, TypeDefinitionHandle>(StringComparer.Ordinal);
        var nesting = new List<(TypeDefinitionHandle Nested, string Outer)>();
        foreach (TypeSpec spec in specs)
        {
            bool nested = spec.DeclaredIn is not null;
            TypeAttributes visibility = nested
                ? (spec.IsPublic ? TypeAttributes.NestedPublic : TypeAttributes.NestedAssembly)
                : (spec.IsPublic ? TypeAttributes.Public : TypeAttributes.NotPublic);
            TypeDefinitionHandle handle = metadata.AddTypeDefinition(
                visibility | TypeAttributes.Class,
                metadata.GetOrAddString(nested ? string.Empty : spec.Namespace),
                metadata.GetOrAddString(spec.Name),
                objectTypeRef,
                MetadataTokens.FieldDefinitionHandle(1),
                MetadataTokens.MethodDefinitionHandle(1));
            handles[Key(spec)] = handle;
            if (nested)
            {
                nesting.Add((handle, spec.DeclaredIn!));
            }
        }

        // The NestedClass table must be sorted by the nested type's row number.
        foreach (var (nestedHandle, outerKey) in nesting.OrderBy(n => MetadataTokens.GetRowNumber(n.Nested)))
        {
            metadata.AddNestedType(nestedHandle, handles[outerKey]);
        }

        var peBuilder = new ManagedPEBuilder(
            new PEHeaderBuilder(imageCharacteristics: Characteristics.Dll),
            new MetadataRootBuilder(metadata),
            ilStream: new BlobBuilder());
        var blob = new BlobBuilder();
        peBuilder.Serialize(blob);

        string path = Path.Combine(_tempDir.FullName, fileName);
        using FileStream fs = File.Create(path);
        blob.WriteContentTo(fs);
        return path;
    }

    private static string Key(TypeSpec spec) => spec.DeclaredIn is null ? spec.Name : spec.DeclaredIn + "+" + spec.Name;

    [TestMethod]
    public void ParseFile_NestedType_IsQualifiedThroughItsDeclaringType()
    {
        string path = WriteWinmd(
            "Nested.winmd",
            new TypeSpec("My.Space", "Outer"),
            new TypeSpec("", "Inner", DeclaredIn: "Outer"));

        WinMdParser.WinMdParseResult result = WinMdParser.ParseFile(path);

        Assert.IsNull(result.Error);
        var inner = result.Types.Single(t => t.Name == "Inner");
        Assert.AreEqual("My.Space.Outer.Inner", inner.FullName);
        Assert.AreEqual("My.Space", inner.Namespace);
    }

    [TestMethod]
    public void ParseFile_SameNamedNestedTypes_DoNotCollide()
    {
        // The Win32 metadata case: '_Anonymous_e__Union' occurs thousands of times, once
        // per struct that has an anonymous union. Named from its own namespace and name
        // every one of them is the same global-namespace type, and the query engine —
        // which dedupes by full name — answers for all of them with whichever it read
        // first.
        string path = WriteWinmd(
            "Colliding.winmd",
            new TypeSpec("My.Space", "SLIST_HEADER"),
            new TypeSpec("My.Space", "CONTEXT"),
            new TypeSpec("", "_Anonymous_e__Union", DeclaredIn: "SLIST_HEADER"),
            new TypeSpec("", "_Anonymous_e__Union", DeclaredIn: "CONTEXT"));

        WinMdParser.WinMdParseResult result = WinMdParser.ParseFile(path);

        Assert.IsNull(result.Error);
        var fullNames = result.Types
            .Where(t => t.Name == "_Anonymous_e__Union")
            .Select(t => t.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        CollectionAssert.AreEqual(ExpectedAnonymousUnionNames, fullNames);
    }

    [TestMethod]
    public void ParseFile_PublicTypeNestedInInternalType_IsNotIndexed()
    {
        // NestedPublic means "public *within its declaring type*", so a NestedPublic type
        // inside an internal class is unreachable from outside the assembly. Judging the
        // nested type's own flag alone indexes it, and `find-api check-property
        // Hidden.Visible Foo` then describes an API the caller cannot reference — while
        // `find-api members Hidden` correctly reports the outer type as not found. The
        // public sibling pins that the walk does not over-reject.
        string path = WriteWinmd(
            "Visibility.winmd",
            new TypeSpec("My.Space", "Hidden", IsPublic: false),
            new TypeSpec("", "Visible", DeclaredIn: "Hidden"),
            new TypeSpec("My.Space", "Shown"),
            new TypeSpec("", "AlsoVisible", DeclaredIn: "Shown"));

        WinMdParser.WinMdParseResult result = WinMdParser.ParseFile(path);

        Assert.IsNull(result.Error);
        var names = result.Types.Select(t => t.FullName).ToList();
        CollectionAssert.DoesNotContain(names, "My.Space.Hidden.Visible", "a type nested in an internal type is not externally visible");
        CollectionAssert.DoesNotContain(names, "My.Space.Hidden");
        CollectionAssert.Contains(names, "My.Space.Shown.AlsoVisible", "a public type nested in a public type is still indexed");
    }

    [TestMethod]
    public void TypeProvider_NestedTypeDefinition_IsQualifiedInSignatures()
    {
        // A signature naming a nested type must produce the same name the index records
        // for it, or a member's rendered type resolves to nothing.
        string path = WriteWinmd(
            "ProviderDef.winmd",
            new TypeSpec("My.Space", "Outer"),
            new TypeSpec("", "Inner", DeclaredIn: "Outer"));

        using FileStream fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        MetadataReader reader = pe.GetMetadataReader();
        var provider = new SimpleTypeProvider();

        TypeDefinitionHandle innerHandle = reader.TypeDefinitions
            .Single(h => reader.GetString(reader.GetTypeDefinition(h).Name) == "Inner");

        Assert.AreEqual("My.Space.Outer.Inner", provider.GetTypeFromDefinition(reader, innerHandle, 0));
    }

    [TestMethod]
    public void TypeProvider_NestedTypeReference_IsQualifiedThroughItsResolutionScope()
    {
        // A reference to a nested type in another assembly carries only its own simple
        // name and reaches its parent through ResolutionScope, so reading its namespace
        // alone renders it as a bare 'Inner'.
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString("<Module>"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(
            metadata.GetOrAddString("RefTestWinmd"), new Version(1, 0, 0, 0),
            default, default, default, AssemblyHashAlgorithm.None);
        var otherAssembly = metadata.AddAssemblyReference(
            metadata.GetOrAddString("Other"), new Version(1, 0, 0, 0), default, default, default, default);

        var outerRef = metadata.AddTypeReference(
            otherAssembly, metadata.GetOrAddString("Their.Space"), metadata.GetOrAddString("Outer"));
        // A nested reference scopes to its declaring reference and has no namespace.
        var innerRef = metadata.AddTypeReference(
            outerRef, default, metadata.GetOrAddString("Inner"));

        metadata.AddTypeDefinition(
            default, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        var peBuilder = new ManagedPEBuilder(
            new PEHeaderBuilder(imageCharacteristics: Characteristics.Dll),
            new MetadataRootBuilder(metadata),
            ilStream: new BlobBuilder());
        var blob = new BlobBuilder();
        peBuilder.Serialize(blob);
        string path = Path.Combine(_tempDir.FullName, "ProviderRef.winmd");
        using (FileStream write = File.Create(path))
        {
            blob.WriteContentTo(write);
        }

        using FileStream fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        MetadataReader reader = pe.GetMetadataReader();
        var provider = new SimpleTypeProvider();

        Assert.AreEqual("Their.Space.Outer", provider.GetTypeFromReference(reader, outerRef, 0));
        Assert.AreEqual("Their.Space.Outer.Inner", provider.GetTypeFromReference(reader, innerRef, 0));
    }

    private static readonly string[] ExpectedAnonymousUnionNames =
    [
        "My.Space.CONTEXT._Anonymous_e__Union",
        "My.Space.SLIST_HEADER._Anonymous_e__Union",
    ];

    [TestMethod]
    public void ParseFile_DoublyNestedType_QualifiesEveryEnclosingLevel()
    {
        string path = WriteWinmd(
            "DeepNested.winmd",
            new TypeSpec("My.Space", "Outer"),
            new TypeSpec("", "Middle", DeclaredIn: "Outer"),
            new TypeSpec("", "Inner", DeclaredIn: "Outer+Middle"));

        WinMdParser.WinMdParseResult result = WinMdParser.ParseFile(path);

        Assert.IsNull(result.Error);
        var inner = result.Types.Single(t => t.Name == "Inner");
        Assert.AreEqual("My.Space.Outer.Middle.Inner", inner.FullName);
        Assert.AreEqual("My.Space", inner.Namespace);
    }

    [TestMethod]
    public void ParseFile_TopLevelTypes_AreUnaffected()
    {
        string path = WriteWinmd(
            "TopLevel.winmd",
            new TypeSpec("My.Space", "Outer"),
            new TypeSpec("", "Global"),
            new TypeSpec("", "Inner", DeclaredIn: "Outer"));

        WinMdParser.WinMdParseResult result = WinMdParser.ParseFile(path);

        Assert.IsNull(result.Error);
        var outer = result.Types.Single(t => t.Name == "Outer");
        Assert.AreEqual("My.Space.Outer", outer.FullName);
        Assert.AreEqual("My.Space", outer.Namespace);

        var global = result.Types.Single(t => t.Name == "Global");
        Assert.AreEqual("Global", global.FullName);
        Assert.AreEqual(string.Empty, global.Namespace);
    }
}
