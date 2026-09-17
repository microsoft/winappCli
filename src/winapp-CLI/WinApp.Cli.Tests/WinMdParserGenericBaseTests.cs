// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using WinApp.Cli.Services.ApiSearch;

namespace WinApp.Cli.Tests;

/// <summary>
/// A class that extends a *constructed* generic type — <c>class Derived : Base&lt;string&gt;</c>
/// — records its base through the TypeSpec table rather than TypeDef or TypeRef, because
/// the instantiation has no row of its own. Ignoring that handle kind leaves the type with
/// no recorded base, and every member it inherits disappears from the index.
/// </summary>
[TestClass]
public class WinMdParserGenericBaseTests
{
    private const string Ns = "Gen.Space";

    private DirectoryInfo _tempDir = null!;

    [TestInitialize]
    public void Setup() =>
        _tempDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "winapp-genbase-" + Guid.NewGuid().ToString("N")));

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

    /// <summary>
    /// Writes a .winmd holding <c>Base&lt;T&gt;</c> and <c>Derived : Base&lt;String&gt;</c>,
    /// with the base encoded as a TypeSpec generic instantiation exactly as a compiler
    /// emits it.
    /// </summary>
    private string WriteGenericBaseWinmd(string fileName)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString("<Module>"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(
            metadata.GetOrAddString("GenericBaseTestWinmd"),
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

        TypeDefinitionHandle baseHandle = metadata.AddTypeDefinition(
            TypeAttributes.Public | TypeAttributes.Class,
            metadata.GetOrAddString(Ns),
            metadata.GetOrAddString("Base`1"),
            objectTypeRef,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));

        // Base<String>: no TypeDef row exists for a constructed generic, so the base
        // reference has to go through TypeSpec.
        var signature = new BlobBuilder();
        new BlobEncoder(signature)
            .TypeSpecificationSignature()
            .GenericInstantiation(baseHandle, genericArgumentCount: 1, isValueType: false)
            .AddArgument()
            .String();
        TypeSpecificationHandle constructedBase = metadata.AddTypeSpecification(metadata.GetOrAddBlob(signature));

        metadata.AddTypeDefinition(
            TypeAttributes.Public | TypeAttributes.Class,
            metadata.GetOrAddString(Ns),
            metadata.GetOrAddString("Derived"),
            constructedBase,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));

        metadata.AddGenericParameter(baseHandle, GenericParameterAttributes.None, metadata.GetOrAddString("T"), 0);

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

    [TestMethod]
    public void ParseFile_BaseTypeIsAConstructedGeneric_IsRecorded()
    {
        // Without this, `find-api check-property Derived Value` answers "Derived does not
        // have property 'Value'" and then lists Base<T>.Value under "types that have a
        // 'Value' property" — a self-contradictory answer that talks a caller out of code
        // that compiles.
        string path = WriteGenericBaseWinmd("GenericBase.winmd");

        WinMdParser.WinMdParseResult result = WinMdParser.ParseFile(path);

        Assert.IsNull(result.Error);
        var derived = result.Types.Single(t => t.Name == "Derived");
        Assert.IsNotNull(derived.BaseType, "a constructed generic base must still be recorded as the base type");
        StringAssert.Contains(derived.BaseType, "Base", "the base is Base<...>");
    }
}
