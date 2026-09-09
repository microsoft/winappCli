// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text.Json;
using WinApp.Cli.Services.ApiSearch;

namespace WinApp.Cli.Tests;

/// <summary>
/// A generic's arity suffix sits on the name segment that declares it, so
/// <c>Dictionary`2.KeyCollection</c> is written <c>Dictionary&lt;TKey, TValue&gt;.KeyCollection</c>
/// in source. Stripping every suffix and appending the whole parameter list once instead
/// yields <c>Dictionary.KeyCollection&lt;TKey, TValue&gt;</c> — a name that does not compile
/// and that a correctly spelled lookup never matches.
/// </summary>
[TestClass]
public sealed class WinMdParserNestedGenericTests
{
    [TestMethod]
    public void ToSourceGenericName_NestedTypeOfAGeneric_KeepsTheArgumentsOnTheDeclaringSegment()
    {
        Assert.AreEqual(
            "System.Collections.Generic.Dictionary<TKey, TValue>.KeyCollection",
            WinMdParser.ToSourceGenericName(
                "System.Collections.Generic.Dictionary`2.KeyCollection",
                ImmutableArray.Create("TKey", "TValue")));
    }

    [TestMethod]
    public void ToSourceGenericName_GenericNestedInAGeneric_SplitsTheParametersPerSegment()
    {
        // GetGenericParameters lists inherited parameters first, so the outer segment owns
        // the leading names and the nested segment the trailing ones.
        Assert.AreEqual(
            "My.Space.Outer<T>.Inner<U>",
            WinMdParser.ToSourceGenericName("My.Space.Outer`1.Inner`1", ImmutableArray.Create("T", "U")));
    }

    [TestMethod]
    public void ToSourceGenericName_NestedTypeAddingNoParameters_NamesOnlyItsOwnSegment()
    {
        // The nested type's own simple name carries no suffix even though its declaring
        // type's parameters are in scope, so nothing is appended to it.
        Assert.AreEqual("KeyCollection", WinMdParser.ToSourceGenericName("KeyCollection", ImmutableArray.Create("TKey", "TValue")));
    }

    [TestMethod]
    public void ToSourceGenericName_TopLevelGeneric_IsUnchanged()
    {
        Assert.AreEqual(
            "Windows.Foundation.Collections.IObservableVector<T>",
            WinMdParser.ToSourceGenericName("Windows.Foundation.Collections.IObservableVector`1", ImmutableArray.Create("T")));
    }

    [TestMethod]
    public void SimpleTypeProvider_GenericInstantiationOfANestedType_KeepsTheNestedSegment()
    {
        // Truncating at the first suffix drops '.KeyCollection' entirely, so a member of
        // that type renders as the wrong type outright.
        Assert.AreEqual(
            "System.Collections.Generic.Dictionary<System.String, System.Int32>.KeyCollection",
            new SimpleTypeProvider().GetGenericInstantiation(
                "System.Collections.Generic.Dictionary`2.KeyCollection",
                ImmutableArray.Create("System.String", "System.Int32")));
    }

    [TestMethod]
    public void DocIdTypeProvider_GenericInstantiationOfANestedType_KeepsTheNestedSegment()
    {
        Assert.AreEqual(
            "System.Collections.Generic.Dictionary{System.String,System.Int32}.KeyCollection",
            DocIdTypeProvider.Instance.GetGenericInstantiation(
                "System.Collections.Generic.Dictionary`2.KeyCollection",
                ImmutableArray.Create("System.String", "System.Int32")));
    }

    [TestMethod]
    public void Members_NestedGenericType_ResolvesFromEverySpellingOfItsName()
    {
        // The matcher reads the arity from whichever segment states it, so the indexed
        // name, the metadata form, and an instantiated form all name the same type.
        string cacheDir = Path.Combine(Path.GetTempPath(), $"NestedGenericTests_{Guid.NewGuid():N}");
        try
        {
            string packageDir = Path.Combine(cacheDir, "packages", "Test.Pkg", "1.0.0", "0a1b2c3d");
            string typesDir = Path.Combine(packageDir, "types");
            Directory.CreateDirectory(typesDir);

            var types = new List<WinMdTypeInfo>
            {
                new()
                {
                    Namespace = "My.Space",
                    Name = "KeyCollection",
                    FullName = "My.Space.Map<TKey, TValue>.KeyCollection",
                    Kind = TypeKind.Class,
                    SourceFile = "test.winmd",
                    Members = [new WinMdMemberInfo { Name = "Count", Kind = MemberKind.Property, Signature = "int Count { get; }", ReturnType = "System.Int32" }],
                },
            };
            File.WriteAllText(
                Path.Combine(typesDir, ApiCachePaths.NamespaceFileName("My.Space")),
                JsonSerializer.Serialize(types, ApiSearchJsonContext.Default.ListWinMdTypeInfo));
            File.WriteAllText(
                Path.Combine(packageDir, "namespaces.json"),
                JsonSerializer.Serialize(new List<string> { "My.Space" }, ApiSearchJsonContext.Default.ListString));

            var manifest = new ProjectManifest
            {
                ProjectName = "TestApp",
                ProjectDir = cacheDir,
                ProjectFile = "TestApp.csproj",
                Packages = [new ProjectPackageRef { Id = "Test.Pkg", Version = "1.0.0", SourceStamp = "0a1b2c3d", AssetPathKey = "0a1b2c3d" }],
                GeneratedAt = DateTime.UtcNow.ToString("o"),
            };

            foreach (string spelling in NestedGenericSpellings)
            {
                var result = ApiQueryEngine.Members(spelling, filter: null, cacheDir, manifest);
                Assert.IsNotNull(result.Data, $"'{spelling}' must resolve to the indexed nested generic type");
                Assert.AreEqual("Count", result.Data.Properties.Single().Name);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(cacheDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private static readonly string[] NestedGenericSpellings =
    [
        "My.Space.Map<TKey, TValue>.KeyCollection",
        "My.Space.Map`2.KeyCollection",
        "My.Space.Map<string, int>.KeyCollection",
    ];
}
