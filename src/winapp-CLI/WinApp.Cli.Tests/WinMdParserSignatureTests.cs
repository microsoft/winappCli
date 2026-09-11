// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using WinApp.Cli.Services.ApiSearch;

namespace WinApp.Cli.Tests;

/// <summary>
/// A rendered signature is what a caller copies into their code, and a documentation key
/// is what decides whether that signature carries any explanation at all. Both are built
/// from the same metadata but spell it differently, so these pin the three places the
/// spelling has to be exact: the <c>static</c> keyword, the direction of a by-reference
/// parameter, and the type names an XML documentation file uses.
/// </summary>
[TestClass]
public class WinMdParserSignatureTests
{
    private const string Ns = "Sig.Space";
    private const string TypeName = "Holder";
    private const string TypeFullName = Ns + "." + TypeName;

    private DirectoryInfo _tempDir = null!;

    [TestInitialize]
    public void Setup() =>
        _tempDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "winapp-sig-" + Guid.NewGuid().ToString("N")));

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

    /// <summary>One parameter to emit: its name, whether it is by-reference, its direction
    /// flags, and whether it gets a Param row at all. A parameter with no row is legal
    /// metadata and is how the row list develops gaps.</summary>
    private sealed record ParamSpec(string Name, bool ByRef = false, ParameterAttributes Attributes = ParameterAttributes.None, bool EmitRow = true);

    /// <summary>One method to emit. Every parameter and the return value are String/Boolean
    /// so the test never depends on how an unrelated type resolves, unless the method
    /// declares generic parameters — then each parameter is that method's own type
    /// parameter, which is how a real generic API like <c>ToTensor&lt;T&gt;(T[])</c> is
    /// encoded.</summary>
    private sealed record MethodSpec(string Name, bool IsStatic, params ParamSpec[] Parameters)
    {
        /// <summary>Names of the method's own generic parameters, if any.</summary>
        public string[]? GenericParameters { get; init; }
    }

    /// <summary>
    /// Writes a minimal but valid .winmd holding a single public type with the given
    /// methods, each with real Param rows so parameter names and direction flags are
    /// present exactly as a compiler would emit them.
    /// </summary>
    private string WriteWinmd(string fileName, params MethodSpec[] methods)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString("<Module>"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(
            metadata.GetOrAddString("SignatureTestWinmd"),
            new Version(1, 0, 0, 0),
            default, default, default, AssemblyHashAlgorithm.None);

        var systemRuntimeRef = metadata.AddAssemblyReference(
            metadata.GetOrAddString("System.Runtime"),
            new Version(4, 0, 0, 0),
            default, default, default, default);
        var objectTypeRef = metadata.AddTypeReference(
            systemRuntimeRef, metadata.GetOrAddString("System"), metadata.GetOrAddString("Object"));

        // Methods and their parameters are emitted first so each TypeDef can point at the
        // row its own list starts on. A TypeDef owns the rows from its own start up to the
        // next TypeDef's start, so giving <Module> and the real type the same start leaves
        // <Module> owning nothing.
        var methodStarts = new List<MethodDefinitionHandle>();
        int nextParamRow = 1;
        foreach (MethodSpec method in methods)
        {
            var signature = new BlobBuilder();
            int genericCount = method.GenericParameters?.Length ?? 0;
            new BlobEncoder(signature)
                .MethodSignature(isInstanceMethod: !method.IsStatic, genericParameterCount: genericCount)
                .Parameters(
                    method.Parameters.Length,
                    returnType =>
                    {
                        if (genericCount > 0)
                        {
                            returnType.Type().GenericMethodTypeParameter(0);
                        }
                        else
                        {
                            returnType.Type().Boolean();
                        }
                    },
                    parameters =>
                    {
                        foreach (ParamSpec parameter in method.Parameters)
                        {
                            SignatureTypeEncoder encoder = parameters.AddParameter().Type(isByRef: parameter.ByRef);
                            if (genericCount > 0)
                            {
                                encoder.GenericMethodTypeParameter(0);
                            }
                            else
                            {
                                encoder.String();
                            }
                        }
                    });

            var firstParam = MetadataTokens.ParameterHandle(nextParamRow);
            MethodDefinitionHandle handle = metadata.AddMethodDefinition(
                MethodAttributes.Public | MethodAttributes.HideBySig | (method.IsStatic ? MethodAttributes.Static : 0),
                MethodImplAttributes.Runtime,
                metadata.GetOrAddString(method.Name),
                metadata.GetOrAddBlob(signature),
                bodyOffset: -1,
                parameterList: firstParam);
            methodStarts.Add(handle);

            for (int i = 0; i < genericCount; i++)
            {
                metadata.AddGenericParameter(
                    handle, GenericParameterAttributes.None,
                    metadata.GetOrAddString(method.GenericParameters![i]), i);
            }

            int sequence = 1;
            foreach (ParamSpec parameter in method.Parameters)
            {
                if (parameter.EmitRow)
                {
                    metadata.AddParameter(parameter.Attributes, metadata.GetOrAddString(parameter.Name), sequence);
                    nextParamRow++;
                }
                sequence++;
            }
        }

        MethodDefinitionHandle typeMethodStart = methodStarts.Count > 0
            ? methodStarts[0]
            : MetadataTokens.MethodDefinitionHandle(1);

        metadata.AddTypeDefinition(
            default, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), typeMethodStart);

        metadata.AddTypeDefinition(
            TypeAttributes.Public | TypeAttributes.Class,
            metadata.GetOrAddString(Ns),
            metadata.GetOrAddString(TypeName),
            objectTypeRef,
            MetadataTokens.FieldDefinitionHandle(1),
            typeMethodStart);

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

    private WinMdMemberInfo ParseSingleMethod(string fileName, MethodSpec method)
    {
        WinMdParser.WinMdParseResult result = WinMdParser.ParseFile(WriteWinmd(fileName, method));
        Assert.IsNull(result.Error);
        var type = result.Types.Single(t => t.FullName == TypeFullName);
        return type.Members.Single(m => m.Name == method.Name);
    }

    [TestMethod]
    public void ParseFile_StaticMethod_IsMarkedStaticInTheSignature()
    {
        // A static method is called on the type. A signature that omits the keyword reads
        // as an instance method, so a caller writes 'new Holder().Create(...)'.
        var member = ParseSingleMethod("Static.winmd", new MethodSpec("Create", IsStatic: true, new ParamSpec("name")));

        Assert.IsTrue(member.IsStatic);
        StringAssert.StartsWith(member.Signature, "static ");
    }

    [TestMethod]
    public void ParseFile_GenericMethod_RendersItsOwnTypeParameterName()
    {
        // A real API like 'DenseTensor<T> ToTensor<T>(T[] array)' encodes T as a generic
        // method parameter. Decoded without the method's own generic parameters, the
        // decoder has no name to print and invents one, so the reported signature reads
        // 'static TMethod0 ToTensor(TMethod0[] array)' — a caller who types that gets a
        // compile error on a type that does not exist, and the '<T>' they must actually
        // write is missing.
        var member = ParseSingleMethod(
            "Generic.winmd",
            new MethodSpec("ToTensor", IsStatic: true, new ParamSpec("array")) { GenericParameters = ["T"] });

        Assert.IsFalse(
            member.Signature.Contains("TMethod", StringComparison.Ordinal),
            $"invented type-parameter name in: {member.Signature}");
        StringAssert.Contains(member.Signature, "ToTensor<T>(");
        StringAssert.Contains(member.Signature, "T array");
    }

    [TestMethod]
    public void ParseFile_InstanceMethod_IsNotMarkedStatic()
    {
        var member = ParseSingleMethod("Instance.winmd", new MethodSpec("Update", IsStatic: false, new ParamSpec("name")));

        Assert.IsFalse(member.IsStatic);
        Assert.IsFalse(member.Signature.Contains("static", StringComparison.Ordinal), member.Signature);
    }

    [TestMethod]
    public void ParseFile_OutParameter_RendersAsOut()
    {
        // The Try pattern is the common shape: 'ref' here does not compile at the call site.
        var member = ParseSingleMethod(
            "Out.winmd",
            new MethodSpec("TryGetValue", IsStatic: false,
                new ParamSpec("key"),
                new ParamSpec("value", ByRef: true, ParameterAttributes.Out)));

        Assert.AreEqual("out String", member.Parameters![1].Type);
        StringAssert.Contains(member.Signature, "out String value");
    }

    [TestMethod]
    public void ParseFile_InParameter_RendersAsIn()
    {
        var member = ParseSingleMethod(
            "In.winmd",
            new MethodSpec("Accept", IsStatic: false,
                new ParamSpec("value", ByRef: true, ParameterAttributes.In)));

        Assert.AreEqual("in String", member.Parameters![0].Type);
    }

    [TestMethod]
    public void ParseFile_ReadWriteByRefParameter_RendersAsRef()
    {
        // [in, out] together is a genuine read-write reference.
        var member = ParseSingleMethod(
            "Ref.winmd",
            new MethodSpec("Swap", IsStatic: false,
                new ParamSpec("value", ByRef: true, ParameterAttributes.In | ParameterAttributes.Out)));

        Assert.AreEqual("ref String", member.Parameters![0].Type);
    }

    [TestMethod]
    public void ParseFile_ParameterWithNoRow_DoesNotShiftLaterParameterNames()
    {
        // A parameter with no Param row of its own is simply absent from the table.
        // Walking the rows in order attaches the second parameter's name to the first.
        var member = ParseSingleMethod(
            "Gap.winmd",
            new MethodSpec("Pair", IsStatic: false,
                new ParamSpec("skipped", EmitRow: false),
                new ParamSpec("second")));

        Assert.AreEqual("second", member.Parameters![1].Name);
        // The unnamed one falls back to a positional placeholder rather than stealing a name.
        Assert.AreEqual("arg0", member.Parameters[0].Name);
    }

    [TestMethod]
    public void MergeDescriptions_MethodWithPrimitiveParameters_MatchesTheCompilersDocKey()
    {
        // A documentation file spells parameters the way the compiler emits them:
        // 'System.String', not the 'String' shown to a reader. A key built from the
        // displayed types matches nothing for any method taking a primitive, which is
        // most of them.
        string path = WriteWinmd(
            "Docs.winmd",
            new MethodSpec("TryGetValue", IsStatic: false,
                new ParamSpec("key"),
                new ParamSpec("value", ByRef: true, ParameterAttributes.Out)));
        WinMdParser.WinMdParseResult result = WinMdParser.ParseFile(path);
        Assert.IsNull(result.Error);

        var docs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"M:{TypeFullName}.TryGetValue(System.String,System.String@)"] = "Looks up a value.",
        };
        XmlDocParser.MergeDescriptions(result.Types, docs);

        var member = result.Types.Single(t => t.FullName == TypeFullName).Members.Single(m => m.Name == "TryGetValue");
        Assert.AreEqual("Looks up a value.", member.Description);
    }

    [TestMethod]
    public void MergeDescriptions_DoesNotMatchAKeyBuiltFromDisplayedTypeNames()
    {
        // Guards the fix from being undone by anything that reintroduces display names
        // into the lookup key.
        string path = WriteWinmd("DocsDisplay.winmd", new MethodSpec("Set", IsStatic: false, new ParamSpec("value")));
        WinMdParser.WinMdParseResult result = WinMdParser.ParseFile(path);

        var docs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"M:{TypeFullName}.Set(String)"] = "Should not be used.",
        };
        XmlDocParser.MergeDescriptions(result.Types, docs);

        var member = result.Types.Single(t => t.FullName == TypeFullName).Members.Single(m => m.Name == "Set");
        Assert.IsNull(member.Description);
    }
}
