// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Services.ApiSearch;

namespace WinApp.Cli.Tests;

/// <summary>
/// Exercises the read side of the API-metadata engine against a hand-built cache
/// on disk, so the whole query surface is covered without parsing real .winmd
/// files. The cache layout mirrors what <see cref="ApiCacheBuilder"/> writes.
/// </summary>
[TestClass]
public sealed class ApiQueryEngineTests
{
    private static readonly string[] MoodValues = ["Happy", "Sad"];
    private static readonly string[] HappyOnly = ["Happy"];
    private static readonly string[] ColorOnly = ["Color"];
    private static readonly string[] ExpectedAlphaCandidates = ["Dup.Ns.Alpha", "Other.Ns.Alpha"];
    private static readonly string[] DuplicatePackageIds = ["Dup.PkgA", "Dup.PkgB"];
    private static readonly string[] UwpTwinFullName = ["Windows.UI.Input.Twin"];

    private string _cacheDir = null!;
    private ProjectManifest _manifest = null!;

    [TestInitialize]
    public void Setup()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), $"ApiQueryEngineTests_{Guid.NewGuid():N}");
        _manifest = BuildSyntheticCache(_cacheDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_cacheDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [TestMethod]
    public void Search_ByTypeName_ReturnsNamespaceHit()
    {
        var result = ApiQueryEngine.Search("Widget", 30, _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.IsTrue(result.Data!.Results.Any(r => r.Namespace == "My.Ns"), "Widget lives in My.Ns");
    }

    [TestMethod]
    public void Search_GlobalNamespaceType_DoesNotThrow()
    {
        // Regression: a global-namespace type (FullName == Name) once drove the
        // namespace-prefix substring negative and crashed the ambiguity grouping.
        var result = ApiQueryEngine.Search("GlobalThing", 30, _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.IsTrue(result.Data!.Results.Count > 0, "the global type should still be found");
    }

    [TestMethod]
    public void Search_EmptyQuery_IsInvalidInput()
    {
        var result = ApiQueryEngine.Search("   ", 30, _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.InvalidInput, result.Outcome);
    }

    [TestMethod]
    public void Members_ListsPropertiesAndMethods()
    {
        var result = ApiQueryEngine.Members("My.Ns.Widget", null, _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.IsTrue(result.Data!.Properties.Any(p => p.Name == "Color"));
        Assert.IsTrue(result.Data.Methods.Any(m => m.Name == "DoThing"));
    }

    [TestMethod]
    public void Members_ShortName_ResolvesType()
    {
        // Regression (C3): members must accept a short name, matching the help text
        // and the check-property resolution behavior.
        var result = ApiQueryEngine.Members("Widget", null, _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.AreEqual("My.Ns.Widget", result.Data!.FullName);
    }

    [TestMethod]
    public void Members_UnknownType_IsNotFound()
    {
        var result = ApiQueryEngine.Members("My.Ns.Nope", null, _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.NotFound, result.Outcome);
    }

    [TestMethod]
    public void Enums_ListsValues()
    {
        var result = ApiQueryEngine.Enums("My.Ns.Mood", null, _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        CollectionAssert.AreEqual(MoodValues, result.Data!.Values);
    }

    [TestMethod]
    public void Enums_ShortName_ResolvesType()
    {
        // Regression (C3): enums must accept a short name too.
        var result = ApiQueryEngine.Enums("Mood", null, _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.AreEqual("My.Ns.Mood", result.Data!.FullName);
    }

    [TestMethod]
    public void Enums_NonEnumType_IsNotAnEnum()
    {
        var result = ApiQueryEngine.Enums("My.Ns.Widget", null, _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.NotAnEnum, result.Outcome);
    }

    [TestMethod]
    public void Enums_Unfiltered_ReportsNoFilterMetadata()
    {
        var result = ApiQueryEngine.Enums("My.Ns.Mood", null, _cacheDir, _manifest);

        // Unfiltered payloads must stay byte-identical to before --filter existed.
        Assert.IsNull(result.Data!.Filter);
        Assert.IsNull(result.Data.TotalValues);
    }

    [TestMethod]
    public void Enums_Filter_NarrowsValuesAndReportsTotal()
    {
        var result = ApiQueryEngine.Enums("My.Ns.Mood", "hap", _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        CollectionAssert.AreEqual(HappyOnly, result.Data!.Values);
        Assert.AreEqual("hap", result.Data.Filter);
        // The pre-filter total must survive so a caller can tell a narrow view
        // from a genuinely small enum.
        Assert.AreEqual(2, result.Data.TotalValues);
    }

    [TestMethod]
    public void Enums_Filter_IsCaseInsensitiveSubstring()
    {
        var result = ApiQueryEngine.Enums("My.Ns.Mood", "AP", _cacheDir, _manifest);

        CollectionAssert.AreEqual(HappyOnly, result.Data!.Values);
    }

    [TestMethod]
    public void Enums_Filter_NoMatch_IsStillOkWithTotal()
    {
        var result = ApiQueryEngine.Enums("My.Ns.Mood", "zzz", _cacheDir, _manifest);

        // A filter miss is not a missing type: the outcome stays Ok so callers
        // don't confuse "nothing matched my filter" with "no such enum".
        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.AreEqual(0, result.Data!.Values.Count);
        Assert.AreEqual(2, result.Data.TotalValues);
    }

    [TestMethod]
    public void Members_Filter_NarrowsAllGroupsAndReportsTotals()
    {
        var result = ApiQueryEngine.Members("My.Ns.Widget", "col", _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        CollectionAssert.AreEqual(ColorOnly, result.Data!.Properties.Select(p => p.Name).ToArray());
        Assert.AreEqual(0, result.Data.Methods.Count);
        Assert.AreEqual("col", result.Data.Filter);
        Assert.AreEqual(1, result.Data.TotalProperties);
        Assert.AreEqual(1, result.Data.TotalMethods);
    }

    [TestMethod]
    public void Members_Unfiltered_HidesDependencyPropertyIdentifiers()
    {
        var result = ApiQueryEngine.Members("My.Ns.Ctl", null, _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        var names = result.Data!.Properties.Select(p => p.Name).ToList();
        CollectionAssert.Contains(names, "Background");
        CollectionAssert.DoesNotContain(names, "BackgroundProperty");

        // A member ending in "Property" that is not typed DependencyProperty is a real
        // API and must survive the trim.
        CollectionAssert.Contains(names, "NameProperty");

        Assert.AreEqual(1, result.Data.HiddenDependencyProperties);
        Assert.AreEqual(3, result.Data.TotalProperties, "totals must describe the type, not the trimmed view");
    }

    [TestMethod]
    public void Members_Unfiltered_OmitsDescriptionsAndSaysSo()
    {
        var result = ApiQueryEngine.Members("My.Ns.Ctl", null, _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.IsTrue(result.Data!.Properties.All(p => p.Description is null));
        Assert.IsTrue(result.Data.DescriptionsOmitted);
        Assert.IsNotNull(result.Data.Hint);
    }

    [TestMethod]
    public void Members_IncludeAll_RestoresIdentifiersAndDescriptions()
    {
        var result = ApiQueryEngine.Members("My.Ns.Ctl", null, _cacheDir, _manifest, includeAll: true);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        CollectionAssert.Contains(result.Data!.Properties.Select(p => p.Name).ToList(), "BackgroundProperty");
        Assert.AreEqual("The background brush.", result.Data.Properties.Single(p => p.Name == "Background").Description);
        Assert.IsNull(result.Data.HiddenDependencyProperties);
        Assert.IsNull(result.Data.DescriptionsOmitted);
        Assert.IsNull(result.Data.Hint);
    }

    [TestMethod]
    public void Members_Filter_ReachesDependencyPropertyIdentifiersAndKeepsDescriptions()
    {
        // A targeted query is already cheap, so it sees the whole surface — code-behind
        // work that needs BackgroundProperty must not be forced to --all.
        var result = ApiQueryEngine.Members("My.Ns.Ctl", "backgroundproperty", _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        var match = result.Data!.Properties.Single();
        Assert.AreEqual("BackgroundProperty", match.Name);
        Assert.AreEqual("Identifies the Background property.", match.Description);
        Assert.IsNull(result.Data.HiddenDependencyProperties);
    }

    [TestMethod]
    public void CheckProperty_Found_OmitsEmptySuggestionArrays()
    {
        // Empty suggestion arrays measured ~10% of a batched check-property payload
        // while carrying no information; null collapses them out of the JSON.
        var result = ApiQueryEngine.CheckProperty("My.Ns.Widget", "Color", _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.IsTrue(result.Data!.Found);
        Assert.IsNull(result.Data.SimilarOnType);
        Assert.IsNull(result.Data.TypesWithProperty);
        Assert.IsNull(result.Data.TypesWithSimilar);
    }

    [TestMethod]
    public void Members_Unfiltered_ReportsNoFilterMetadata()
    {
        var result = ApiQueryEngine.Members("My.Ns.Widget", null, _cacheDir, _manifest);

        Assert.IsNull(result.Data!.Filter);
        Assert.IsNull(result.Data.TotalProperties);
        Assert.IsNull(result.Data.TotalMethods);
    }

    [TestMethod]
    public void Types_ListsTypesInNamespace()
    {
        var result = ApiQueryEngine.Types("My.Ns", _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        var names = result.Data!.Types.Select(t => t.FullName).ToList();
        CollectionAssert.Contains(names, "My.Ns.Widget");
        CollectionAssert.Contains(names, "My.Ns.Mood");
        CollectionAssert.Contains(names, "My.Ns.Gadget");
    }

    [TestMethod]
    public void Namespaces_FilterByPrefix()
    {
        var result = ApiQueryEngine.Namespaces("My", _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        CollectionAssert.Contains(result.Data!.Namespaces, "My.Ns");
    }

    [TestMethod]
    public void CheckProperty_Existing_IsFound()
    {
        var result = ApiQueryEngine.CheckProperty("My.Ns.Widget", "Color", _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.IsTrue(result.Data!.Found);
    }

    [TestMethod]
    public void CheckProperty_MethodNameCollision_IsNotFound()
    {
        // Regression (C1): a method whose name matches the queried property must
        // not be reported as a found property. "DoThing" is a method on Widget.
        var result = ApiQueryEngine.CheckProperty("My.Ns.Widget", "DoThing", _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.IsFalse(result.Data!.Found);
    }

    [TestMethod]
    public void CheckProperty_Missing_IsNotFoundOnType()
    {
        var result = ApiQueryEngine.CheckProperty("My.Ns.Widget", "Bogus", _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.IsFalse(result.Data!.Found);
    }

    [TestMethod]
    public void Stats_AggregatesFromMeta()
    {
        var result = ApiQueryEngine.Stats(_cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.AreEqual(4, result.Data!.Types);
        Assert.AreEqual(2, result.Data.Members);
        Assert.AreEqual(1, result.Data.Packages);
    }

    [TestMethod]
    public void Packages_ReportsOkWithCounts()
    {
        var result = ApiQueryEngine.Packages(_cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        var pkg = result.Data!.Packages.Single();
        Assert.AreEqual("Test.Pkg", pkg.Id);
        Assert.AreEqual("ok", pkg.Status);
        Assert.AreEqual(4, pkg.TotalTypes);
    }

    [TestMethod]
    public void Projects_ListsCachedProjects()
    {
        var result = ApiQueryEngine.Projects(_cacheDir);

        Assert.IsTrue(result.Projects.Any(p => p.Name == "TestApp" && p.PackageCount == 1));
    }

    [TestMethod]
    public void Projects_ReportsManifestProjectName_NotHashedFileName()
    {
        // The manifest file name carries a path hash (TestApp_ab12cd34.json) so that
        // same-named projects in different directories don't collide. '--project'
        // matches ProjectManifest.ProjectName, so surfacing the file name would hand
        // callers a value that fails when they pass it back.
        var result = ApiQueryEngine.Projects(_cacheDir);

        Assert.IsFalse(
            result.Projects.Any(p => p.Name.Contains('_', StringComparison.Ordinal)),
            "project listings must not leak the hashed manifest file name");
    }

    [TestMethod]
    public void Search_NoResultsAgainstIncompleteIndex_IsFlaggedAsPossiblyFalseNegative()
    {
        // Search reports "found nothing" as success with an empty result set, so unlike
        // members/check-property it has no error message to carry the qualification. A
        // caller — very often an agent about to conclude an API does not exist — could
        // not tell that from an index that failed to load.
        MarkPackageIncomplete(_cacheDir, "Test.Pkg", "1.0.0");

        var result = ApiQueryEngine.Search("Nonexistent", 30, _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.AreEqual(0, result.Data!.Results.Count);
        Assert.IsNotNull(result.Data.Note, "an empty search against a partial index must be qualified");
        StringAssert.Contains(result.Data.Note, "Test.Pkg");
    }

    [TestMethod]
    public void Search_NoResultsAgainstCompleteIndex_HasNoNote()
    {
        // The note has to stay rare, or it becomes noise that callers learn to ignore.
        var result = ApiQueryEngine.Search("Nonexistent", 30, _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.AreEqual(0, result.Data!.Results.Count);
        Assert.IsNull(result.Data.Note);
    }

    [TestMethod]
    public void Search_WithResultsAgainstIncompleteIndex_HasNoNote()
    {
        // A search that found what was asked for is not a false negative.
        MarkPackageIncomplete(_cacheDir, "Test.Pkg", "1.0.0");

        var result = ApiQueryEngine.Search("Widget", 30, _cacheDir, _manifest);

        Assert.IsTrue(result.Data!.Results.Count > 0);
        Assert.IsNull(result.Data.Note);
    }

    [TestMethod]
    public void CheckProperty_MissAgainstIncompleteIndex_IsFlaggedAsPossiblyFalseNegative()
    {
        // A package whose metadata failed to parse contributes no types, so "no such
        // property" is indistinguishable from "never indexed" unless it is flagged.
        MarkPackageIncomplete(_cacheDir, "Test.Pkg", "1.0.0");

        var result = ApiQueryEngine.CheckProperty("My.Ns.Widget", "Nonexistent", _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.IsFalse(result.Data!.Found);
        Assert.IsNotNull(result.Data.Warning, "a miss from a partial index must be qualified");
        StringAssert.Contains(result.Data.Warning, "Test.Pkg");
    }

    [TestMethod]
    public void CheckProperty_MissAgainstCompleteIndex_HasNoWarning()
    {
        var result = ApiQueryEngine.CheckProperty("My.Ns.Widget", "Nonexistent", _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.IsFalse(result.Data!.Found);
        Assert.IsNull(result.Data.Warning);
    }

    [TestMethod]
    public void Packages_ReportsIncompleteStatusWhenMetadataFailedToParse()
    {
        MarkPackageIncomplete(_cacheDir, "Test.Pkg", "1.0.0");

        var result = ApiQueryEngine.Packages(_cacheDir, _manifest);

        Assert.AreEqual("incomplete", result.Data!.Packages.Single().Status);
    }

    /// <summary>Rewrites a cached package's meta.json as if one of its files failed to parse.</summary>
    private static void MarkPackageIncomplete(string cacheDir, string packageId, string version)
    {
        string metaPath = Path.Combine(cacheDir, "packages", packageId, version, TestSourceStamp, "meta.json");
        PackageMeta existing = JsonSerializer.Deserialize(File.ReadAllText(metaPath), ApiSearchJsonContext.Default.PackageMeta)!;
        var updated = new PackageMeta
        {
            Format = existing.Format,
            PackageId = existing.PackageId,
            Version = existing.Version,
            WinMdFiles = existing.WinMdFiles,
            TotalTypes = existing.TotalTypes,
            TotalMembers = existing.TotalMembers,
            TotalNamespaces = existing.TotalNamespaces,
            Incomplete = true,
            ParseErrors = ["test.winmd: file contains no CLI metadata"],
            GeneratedAt = existing.GeneratedAt,
        };
        File.WriteAllText(metaPath, JsonSerializer.Serialize(updated, ApiSearchJsonContext.Default.PackageMeta));
    }

    /// <summary>
    /// Stand-in for the source fingerprint <c>ApiCacheBuilder</c> derives from a package's
    /// metadata files. It is part of the cache directory name, so a fixture that writes a
    /// cache by hand has to use the same value it puts in the manifest.
    /// </summary>
    private const string TestSourceStamp = "0a1b2c3d";

    private static ProjectManifest BuildSyntheticCache(string cacheDir)
    {
        const string packageId = "Test.Pkg";
        const string version = "1.0.0";
        string packageDir = Path.Combine(cacheDir, "packages", packageId, version, TestSourceStamp);
        string typesDir = Path.Combine(packageDir, "types");
        Directory.CreateDirectory(typesDir);

        var myNsTypes = new List<WinMdTypeInfo>
        {
            new()
            {
                Namespace = "My.Ns", Name = "Widget", FullName = "My.Ns.Widget", Kind = TypeKind.Class,
                SourceFile = "test.winmd",
                Members =
                [
                    new WinMdMemberInfo { Name = "Color", Kind = MemberKind.Property, Signature = "String Color { get; set; }" },
                    new WinMdMemberInfo { Name = "DoThing", Kind = MemberKind.Method, Signature = "void DoThing()" },
                ],
            },
            new()
            {
                Namespace = "My.Ns", Name = "Mood", FullName = "My.Ns.Mood", Kind = TypeKind.Enum,
                SourceFile = "test.winmd", Members = [], EnumValues = ["Happy", "Sad"],
            },
            new()
            {
                // Mirrors a XAML attached property: static Get/Set accessors taking a
                // DependencyObject, with no property of that name on the type itself.
                Namespace = "My.Ns", Name = "Gadget", FullName = "My.Ns.Gadget", Kind = TypeKind.Class,
                SourceFile = "test.winmd",
                Members =
                [
                    new WinMdMemberInfo
                    {
                        Name = "GetRow", Kind = MemberKind.Method, IsStatic = true,
                        Signature = "static Int32 GetRow(My.Ns.DependencyObject element)",
                        ReturnType = "Int32",
                        Parameters = [new WinMdParameterInfo { Name = "element", Type = "My.Ns.DependencyObject" }],
                    },
                    new WinMdMemberInfo
                    {
                        Name = "SetRow", Kind = MemberKind.Method, IsStatic = true,
                        Signature = "static void SetRow(My.Ns.DependencyObject element, Int32 value)",
                        ReturnType = "void",
                        Parameters =
                        [
                            new WinMdParameterInfo { Name = "element", Type = "My.Ns.DependencyObject" },
                            new WinMdParameterInfo { Name = "value", Type = "Int32" },
                        ],
                    },
                    // Getter-only: the framework computes it and offers no setter, the
                    // shape behind AutomationProperties.Level and friends.
                    new WinMdMemberInfo
                    {
                        Name = "GetLevel", Kind = MemberKind.Method, IsStatic = true,
                        Signature = "static Int32 GetLevel(My.Ns.DependencyObject element)",
                        ReturnType = "Int32",
                        Parameters = [new WinMdParameterInfo { Name = "element", Type = "My.Ns.DependencyObject" }],
                    },
                ],
            },
            new()
            {
                // Same shape as Gadget but with *instance* accessors. C# instance methods
                // named GetX/SetX are ordinary methods; only the static form is the XAML
                // attached-property pattern, because there is no instance to call it on
                // when the property is set on someone else's element.
                Namespace = "My.Ns", Name = "InstanceGadget", FullName = "My.Ns.InstanceGadget", Kind = TypeKind.Class,
                SourceFile = "test.winmd",
                Members =
                [
                    new WinMdMemberInfo
                    {
                        Name = "GetRow", Kind = MemberKind.Method, IsStatic = false,
                        Signature = "Int32 GetRow(My.Ns.DependencyObject element)",
                        ReturnType = "Int32",
                        Parameters = [new WinMdParameterInfo { Name = "element", Type = "My.Ns.DependencyObject" }],
                    },
                    new WinMdMemberInfo
                    {
                        Name = "SetRow", Kind = MemberKind.Method, IsStatic = false,
                        Signature = "void SetRow(My.Ns.DependencyObject element, Int32 value)",
                        ReturnType = "void",
                        Parameters =
                        [
                            new WinMdParameterInfo { Name = "element", Type = "My.Ns.DependencyObject" },
                            new WinMdParameterInfo { Name = "value", Type = "Int32" },
                        ],
                    },
                ],
            },
            new()
            {
                // Mirrors a WinUI control: a real property, its dependency-property
                // identifier static, and XML-doc descriptions — the three things the
                // unfiltered-listing trim has to distinguish between.
                Namespace = "My.Ns", Name = "Ctl", FullName = "My.Ns.Ctl", Kind = TypeKind.Class,
                SourceFile = "test.winmd",
                Members =
                [
                    new WinMdMemberInfo
                    {
                        Name = "Background", Kind = MemberKind.Property,
                        Signature = "Brush Background { get; set; }",
                        ReturnType = "My.Ns.Brush", Description = "The background brush.",
                    },
                    new WinMdMemberInfo
                    {
                        Name = "BackgroundProperty", Kind = MemberKind.Property,
                        Signature = "My.Ns.DependencyProperty BackgroundProperty { get; }",
                        ReturnType = "My.Ns.DependencyProperty", Description = "Identifies the Background property.",
                    },
                    new WinMdMemberInfo
                    {
                        // Ends in "Property" but is not a DependencyProperty, so it must
                        // survive the trim.
                        Name = "NameProperty", Kind = MemberKind.Property,
                        Signature = "String NameProperty { get; set; }",
                        ReturnType = "String", Description = "Not a dependency property.",
                    },
                ],
            },
        };

        var globalTypes = new List<WinMdTypeInfo>
        {
            new()
            {
                Namespace = "", Name = "GlobalThing", FullName = "GlobalThing", Kind = TypeKind.Class,
                SourceFile = "test.winmd", Members = [],
            },
        };

        File.WriteAllText(Path.Combine(typesDir, ApiCachePaths.NamespaceFileName("My.Ns")), JsonSerializer.Serialize(myNsTypes, ApiSearchJsonContext.Default.ListWinMdTypeInfo));
        File.WriteAllText(Path.Combine(typesDir, ApiCachePaths.NamespaceFileName("_GlobalNamespace")), JsonSerializer.Serialize(globalTypes, ApiSearchJsonContext.Default.ListWinMdTypeInfo));
        File.WriteAllText(
            Path.Combine(packageDir, "namespaces.json"),
            JsonSerializer.Serialize(new List<string> { "My.Ns", "_GlobalNamespace" }, ApiSearchJsonContext.Default.ListString));

        var meta = new PackageMeta
        {
            PackageId = packageId, Version = version, WinMdFiles = ["test.winmd"],
            TotalTypes = 4, TotalMembers = 2, TotalNamespaces = 2, GeneratedAt = DateTime.UtcNow.ToString("o"),
        };
        File.WriteAllText(Path.Combine(packageDir, "meta.json"), JsonSerializer.Serialize(meta, ApiSearchJsonContext.Default.PackageMeta));

        var manifest = new ProjectManifest
        {
            ProjectName = "TestApp",
            ProjectDir = Path.Combine(cacheDir, "src"),
            ProjectFile = "TestApp.csproj",
            Packages = [new ProjectPackageRef { Id = packageId, Version = version, SourceStamp = TestSourceStamp, AssetPathKey = TestSourceStamp }],
            GeneratedAt = DateTime.UtcNow.ToString("o"),
        };
        // A manifest is only written for a project that was indexed, so its project file
        // is on disk. Listings drop a manifest whose project has since disappeared.
        Directory.CreateDirectory(Path.Combine(cacheDir, "src"));
        File.WriteAllText(Path.Combine(cacheDir, "src", "TestApp.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        string projectsDir = Path.Combine(cacheDir, "projects");
        Directory.CreateDirectory(projectsDir);
        // Manifest files carry a path hash in their name, exactly as ApiCacheBuilder
        // writes them, so anything that surfaces a project name has to read the
        // manifest rather than the file name.
        File.WriteAllText(
            Path.Combine(projectsDir, ApiCacheBuilder.ManifestName(Path.Combine(cacheDir, "src", "TestApp.csproj")) + ".json"),
            JsonSerializer.Serialize(manifest, ApiSearchJsonContext.Default.ProjectManifest));

        return manifest;
    }

    #region Duplicate collapse and ABI projection filtering

    [TestMethod]
    public void Search_SameTypeInSeveralPackages_EmitsEachFullNameOnce()
    {
        // Regression: Search walked every package's type files without a seen-set, so a type
        // shipped by more than one package (the norm — e.g. Microsoft.UI.Xaml.Controls.Button
        // lives in both WinAppSdkRuntime and Microsoft.WindowsAppSDK.WinUI) was scored and
        // emitted once per package. Roughly 42-44% of real ambiguity candidates were dupes.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildMultiPackageCache(cacheDir);

            var result = ApiQueryEngine.Search("Alpha", 30, cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
            ApiAmbiguityGroup group = result.Data!.Ambiguous!.Single(g => g.Name == "Alpha");
            CollectionAssert.AreEquivalent(
                ExpectedAlphaCandidates,
                group.Candidates.Select(c => c.FullName).ToList(),
                "Both packages ship both types, but each fully-qualified name must appear once.");
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void Search_DuplicatePackages_DoNotCrowdOutDistinctMatches()
    {
        // The per-namespace match list is capped at 5. Before the dedupe, copies of one type
        // could consume several of those slots and push genuinely different types out.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildMultiPackageCache(cacheDir);

            var result = ApiQueryEngine.Search("Alpha", 30, cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
            ApiNamespaceHit hit = result.Data!.Results.Single(r => r.Namespace == "Dup.Ns");
            var displays = hit.Matches.Select(m => m.Display).ToList();
            CollectionAssert.AllItemsAreUnique(displays, "A capped match list must not spend slots on duplicates.");
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void Search_AbiProjectionTwins_AreExcludedFromResults()
    {
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildMultiPackageCache(cacheDir);

            var result = ApiQueryEngine.Search("Alpha", 30, cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
            Assert.IsFalse(
                result.Data!.Results.Any(r => r.Namespace.StartsWith("ABI.", StringComparison.Ordinal)),
                "CsWinRT ABI projection namespaces are marshalling internals and must not surface in search.");
            Assert.IsFalse(
                result.Data.Ambiguous!.SelectMany(g => g.Candidates).Any(c => c.FullName.StartsWith("ABI.", StringComparison.Ordinal)),
                "ABI twins must not appear as disambiguation candidates.");
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void Search_TypeWithOnlyAnAbiTwin_IsNotReportedAmbiguous()
    {
        // 'ABI.Solo.Ns' counts as a distinct namespace prefix, so before the filter a type
        // living in exactly one real namespace was still flagged as a CS0104 collision —
        // and the advice to "use the fully-qualified name" could not resolve it.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildMultiPackageCache(cacheDir);

            var result = ApiQueryEngine.Search("Solo", 30, cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
            bool flagged = result.Data!.Ambiguous?.Any(g => g.Name == "Solo") ?? false;
            Assert.IsFalse(flagged, "Solo.Ns.Solo exists in one real namespace; its ABI twin must not fabricate ambiguity.");
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void Members_AbiProjectionType_StillResolvesByFullName()
    {
        // The filter is deliberately scoped to discovery. Exact lookups go through
        // LoadAllTypes, which stays unfiltered so an explicitly-named ABI type is reachable.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildMultiPackageCache(cacheDir);

            var result = ApiQueryEngine.Members("ABI.Dup.Ns.Alpha", filter: null, cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome, "Filtering search must not make ABI types unreachable by exact name.");
            Assert.AreEqual("ABI.Dup.Ns.Alpha", result.Data!.FullName);
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void Members_MicrosoftTypeVersusThirdPartyType_IsStillAmbiguous()
    {
        // The Microsoft-wins rule is only for the WinUI-3/UWP twin pair. A third-party
        // package shipping a same-named control must not be silently answered for by
        // the Microsoft type — that is the original bug, not the fix.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildMultiPackageCache(cacheDir);

            var result = ApiQueryEngine.Members("Widget", filter: null, cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.InvalidInput, result.Outcome);
            StringAssert.Contains(result.Message, "Contoso.Controls.Widget");
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void Members_WinUiAndUwpTwin_ResolvesToTheMicrosoftType()
    {
        // Every duplicated short name in the SDK scope is essentially this pair, so
        // erroring here would break the documented 'members NavigationView' form. The
        // earlier tiebreak recognised only Microsoft.UI.Xaml, which silently handed a
        // Microsoft.UI.Input caller the UWP type instead.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildMultiPackageCache(cacheDir);

            var result = ApiQueryEngine.Members("Twin", filter: null, cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
            Assert.AreEqual("Microsoft.UI.Input.Twin", result.Data!.FullName);
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void Members_WinUiAndUwpTwin_NamesTheTypeItPassedOver()
    {
        // The Microsoft.* preference is a guess about intent, and the two types are not
        // interchangeable: Microsoft.UI.Dispatching.DispatcherQueue has
        // EnsureSystemDispatcherQueue and RunEventLoop, Windows.System.DispatcherQueue
        // has neither, and a WinUI 3 app can legitimately use either. Answering without
        // saying which one it answered for is how a caller validates against the wrong
        // type and still gets a tick.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildMultiPackageCache(cacheDir);

            var result = ApiQueryEngine.Members("Twin", filter: null, cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
            CollectionAssert.AreEqual(UwpTwinFullName, result.Data!.AlsoMatched);
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void Members_FullyQualifiedName_DoesNotClaimAnythingElseMatched()
    {
        // The caller already said which type they meant, so the note would be noise on
        // every qualified query.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildMultiPackageCache(cacheDir);

            var result = ApiQueryEngine.Members("Microsoft.UI.Input.Twin", filter: null, cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
            Assert.IsNull(result.Data!.AlsoMatched);
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void CheckProperty_WinUiAndUwpTwin_NamesTheTypeItPassedOver()
    {
        // check-property is the verb whose whole output is one tick, so it is the one
        // where an unstated type choice is least recoverable.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildMultiPackageCache(cacheDir);

            var result = ApiQueryEngine.CheckProperty("Twin", "Missing", cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
            CollectionAssert.AreEqual(UwpTwinFullName, result.Data!.AlsoMatched);
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void Members_AmbiguousShortName_IsInvalidInputListingCandidates()
    {
        // Silently picking one of several same-named types answers a question the caller
        // did not ask: the members of Dup.Ns.Alpha and Other.Ns.Alpha differ, and a wrong
        // pick is indistinguishable from a right one in the output.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildMultiPackageCache(cacheDir);

            var result = ApiQueryEngine.Members("Alpha", filter: null, cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.InvalidInput, result.Outcome);
            StringAssert.Contains(result.Message, "Dup.Ns.Alpha");
            StringAssert.Contains(result.Message, "Other.Ns.Alpha");
            Assert.IsFalse(
                result.Message!.Contains("ABI.", StringComparison.Ordinal),
                "An ABI twin is the same type, not a competing candidate.");
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void Members_ShortNameWithOnlyAnAbiTwin_StillResolves()
    {
        // Solo.Ns.Solo has an ABI twin but exists in exactly one real namespace, so the
        // ambiguity check must not turn a previously-working lookup into an error.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildMultiPackageCache(cacheDir);

            var result = ApiQueryEngine.Members("Solo", filter: null, cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
            Assert.AreEqual("Solo.Ns.Solo", result.Data!.FullName);
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void CheckProperty_AmbiguousShortName_IsInvalidInput()
    {
        // check-property drives whether a caller writes XAML against a property; a wrong
        // type resolution here yields a confident answer about the wrong type.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildMultiPackageCache(cacheDir);

            var result = ApiQueryEngine.CheckProperty("Alpha", "Anything", cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.InvalidInput, result.Outcome);
            StringAssert.Contains(result.Message, "ambiguous");
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    #region Inherited member summarization

    private static readonly string[] ExpectedMiddleProperties = ["FromMiddle"];
    private static readonly string[] ExpectedMiddleEvents = ["MiddleHappened"];
    private static readonly string[] ExpectedRootProperties = ["FromRoot"];

    /// <summary>
    /// A two-level base chain, the shape that makes an unfiltered listing expensive: the
    /// leaf declares almost nothing and inherits almost everything (the real case is
    /// Button, which declares 8 members and inherits 280).
    /// </summary>
    private static ProjectManifest BuildInheritanceCache(string cacheDir)
    {
        WinMdTypeInfo WithMembers(string name, string? baseType, params WinMdMemberInfo[] members) => new()
        {
            Namespace = "Inh.Ns",
            Name = name,
            FullName = "Inh.Ns." + name,
            Kind = TypeKind.Class,
            BaseType = baseType,
            SourceFile = "inh.winmd",
            Members = members.ToList(),
        };

        WinMdMemberInfo Prop(string name) => new()
        {
            Name = name,
            Kind = MemberKind.Property,
            Signature = $"String {name} {{ get; set; }}",
            ReturnType = "String",
            Description = $"The {name}.",
        };

        WinMdMemberInfo Evt(string name) => new()
        {
            Name = name,
            Kind = MemberKind.Event,
            Signature = $"event Handler {name}",
            ReturnType = "Handler",
        };

        var namespaces = new Dictionary<string, List<WinMdTypeInfo>>(StringComparer.Ordinal)
        {
            ["Inh.Ns"] =
            [
                WithMembers("Leaf", "Inh.Ns.Middle", Prop("Own")),
                WithMembers("Middle", "Inh.Ns.Root", Prop("FromMiddle"), Evt("MiddleHappened")),
                WithMembers("Root", null, Prop("FromRoot")),
            ],
        };
        WriteSyntheticPackage(cacheDir, "Inh.Pkg", namespaces);

        var manifest = new ProjectManifest
        {
            ProjectName = "InhApp",
            ProjectDir = Path.Combine(cacheDir, "src"),
            ProjectFile = "InhApp.csproj",
            Packages = [new ProjectPackageRef { Id = "Inh.Pkg", Version = "1.0.0", SourceStamp = TestSourceStamp, AssetPathKey = TestSourceStamp }],
            GeneratedAt = DateTime.UtcNow.ToString("o"),
        };
        string projectsDir = Path.Combine(cacheDir, "projects");
        Directory.CreateDirectory(projectsDir);
        File.WriteAllText(
            Path.Combine(projectsDir, "InhApp.json"),
            JsonSerializer.Serialize(manifest, ApiSearchJsonContext.Default.ProjectManifest));
        return manifest;
    }

    [TestMethod]
    public void Members_Unfiltered_ShowsDeclaredInlineAndInheritedByName()
    {
        // An unfiltered listing is an orientation view. Inlining every inherited
        // signature answers a question the caller did not ask ("what does the base type
        // give me") and buries the members that are actually specific to the type.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildInheritanceCache(cacheDir);

            var result = ApiQueryEngine.Members("Inh.Ns.Leaf", filter: null, cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome, result.Message);

            // Declared members stay inline, with their signatures.
            Assert.AreEqual("Own", result.Data!.Properties.Single().Name);
            StringAssert.Contains(result.Data.Properties.Single().Signature, "Own");

            // Inherited members are summarized by the type that declares them.
            Assert.IsNotNull(result.Data.Inherited);
            var middle = result.Data.Inherited!.Single(g => g.DeclaringType == "Inh.Ns.Middle");
            var root = result.Data.Inherited!.Single(g => g.DeclaringType == "Inh.Ns.Root");
            CollectionAssert.AreEqual(ExpectedMiddleProperties, middle.Properties!.ToArray());
            CollectionAssert.AreEqual(ExpectedMiddleEvents, middle.Events!.ToArray());
            CollectionAssert.AreEqual(ExpectedRootProperties, root.Properties!.ToArray());

            // A group with no members of a kind carries no empty array.
            Assert.IsNull(middle.Methods);
            Assert.IsNull(root.Events);

            // Totals still describe the whole type, so the view is never mistaken for it.
            Assert.AreEqual(3, result.Data.TotalProperties);
            Assert.AreEqual(1, result.Data.TotalEvents);
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void Members_IncludeAll_InlinesInheritedMembersWithSignatures()
    {
        // --all is the escape hatch: it must still produce the complete listing, with
        // inherited members inline and attributed to their declaring type.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildInheritanceCache(cacheDir);

            var result = ApiQueryEngine.Members("Inh.Ns.Leaf", filter: null, cacheDir, manifest, includeAll: true);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
            Assert.IsNull(result.Data!.Inherited, "--all inlines inherited members instead of summarizing them");
            var names = result.Data.Properties.Select(p => p.Name).ToList();
            CollectionAssert.Contains(names, "Own");
            CollectionAssert.Contains(names, "FromMiddle");
            CollectionAssert.Contains(names, "FromRoot");

            var fromRoot = result.Data.Properties.Single(p => p.Name == "FromRoot");
            Assert.AreEqual("Inh.Ns.Root", fromRoot.DeclaringType);
            Assert.IsTrue(fromRoot.Inherited);
            Assert.AreEqual("The FromRoot.", fromRoot.Description);
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void Members_Filter_ReachesInheritedMembersWithSignatures()
    {
        // The summary is only useful if the detail is one targeted query away.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildInheritanceCache(cacheDir);

            var result = ApiQueryEngine.Members("Inh.Ns.Leaf", "fromroot", cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
            Assert.IsNull(result.Data!.Inherited);
            var match = result.Data.Properties.Single();
            Assert.AreEqual("FromRoot", match.Name);
            Assert.AreEqual("Inh.Ns.Root", match.DeclaringType);
            Assert.AreEqual("The FromRoot.", match.Description);
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void Members_Unfiltered_OmitsRedundantKindAndReturnType()
    {
        // kind is already stated by the array the entry sits in, and returnType is the
        // leading token of the signature. Together they measured ~26% of the payload,
        // repeated once per member.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildInheritanceCache(cacheDir);

            var result = ApiQueryEngine.Members("Inh.Ns.Leaf", filter: null, cacheDir, manifest);

            var own = result.Data!.Properties.Single();
            Assert.IsNull(own.Kind);
            Assert.IsNull(own.ReturnType);
            Assert.IsNull(own.Inherited, "a declared member carries no inherited flag");
            Assert.IsNull(own.DeclaringType, "a declared member carries no declaringType");
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    #region Interface inheritance

    /// <summary>
    /// The shape WinRT actually ships: an interface that declares almost nothing and
    /// inherits its surface from the interfaces it requires. IObservableVector&lt;T&gt;
    /// declares only its change event; Size, GetAt, and Append come from IVector&lt;T&gt;
    /// and IIterable&lt;T&gt;.
    /// <para>
    /// The required interfaces are spelled with a concrete argument
    /// (<c>IVector&lt;String&gt;</c>) while the definitions carry their declared parameter
    /// (<c>IVector&lt;T&gt;</c>), which is what metadata records — so resolving them cannot
    /// rely on the two strings being identical.
    /// </para>
    /// </summary>
    private static ProjectManifest BuildInterfaceInheritanceCache(string cacheDir)
    {
        WinMdTypeInfo Iface(string name, List<string>? interfaces, params WinMdMemberInfo[] members) => new()
        {
            Namespace = "Ifc.Ns",
            Name = name,
            FullName = "Ifc.Ns." + name,
            Kind = TypeKind.Interface,
            Interfaces = interfaces,
            SourceFile = "ifc.winmd",
            Members = members.ToList(),
        };

        WinMdMemberInfo Prop(string name) => new()
        {
            Name = name,
            Kind = MemberKind.Property,
            Signature = $"UInt32 {name} {{ get; }}",
            ReturnType = "UInt32",
        };

        WinMdMemberInfo Evt(string name) => new()
        {
            Name = name,
            Kind = MemberKind.Event,
            Signature = $"event Handler {name}",
            ReturnType = "Handler",
        };

        var namespaces = new Dictionary<string, List<WinMdTypeInfo>>(StringComparer.Ordinal)
        {
            ["Ifc.Ns"] =
            [
                Iface("IObservableVector<T>", ["Ifc.Ns.IVector<String>"], Evt("VectorChanged")),
                Iface("IVector<T>", ["Ifc.Ns.IIterable<T>"], Prop("Size")),
                Iface("IIterable<T>", null, Prop("First")),
            ],
        };
        WriteSyntheticPackage(cacheDir, "Ifc.Pkg", namespaces);

        var manifest = new ProjectManifest
        {
            ProjectName = "IfcApp",
            ProjectDir = Path.Combine(cacheDir, "src"),
            ProjectFile = "IfcApp.csproj",
            Packages = [new ProjectPackageRef { Id = "Ifc.Pkg", Version = "1.0.0", SourceStamp = TestSourceStamp, AssetPathKey = TestSourceStamp }],
            GeneratedAt = DateTime.UtcNow.ToString("o"),
        };
        string projectsDir = Path.Combine(cacheDir, "projects");
        Directory.CreateDirectory(projectsDir);
        File.WriteAllText(
            Path.Combine(projectsDir, "IfcApp.json"),
            JsonSerializer.Serialize(manifest, ApiSearchJsonContext.Default.ProjectManifest));
        return manifest;
    }

    [TestMethod]
    public void CheckProperty_MemberOfARequiredInterface_IsFound()
    {
        // Walking base types alone answers "does not exist" for a member the caller can
        // write today. On an interface-heavy surface that is a false negative on the exact
        // question this command exists to answer.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildInterfaceInheritanceCache(cacheDir);

            var result = ApiQueryEngine.CheckProperty("Ifc.Ns.IObservableVector<T>", "Size", cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
            Assert.IsTrue(result.Data!.Found, "Size is inherited from Ifc.Ns.IVector<T>");
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void CheckProperty_MemberOfATransitivelyRequiredInterface_IsFound()
    {
        // IIterable<T> is reached only through IVector<T>, so a one-level interface lookup
        // would still miss it.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildInterfaceInheritanceCache(cacheDir);

            var result = ApiQueryEngine.CheckProperty("Ifc.Ns.IObservableVector<T>", "First", cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
            Assert.IsTrue(result.Data!.Found, "First is inherited transitively from Ifc.Ns.IIterable<T>");
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void Members_InheritedFromAnInterface_IsAttributedToThatInterface()
    {
        // The caller needs to know where a member comes from to look it up; attributing an
        // interface member to the leaf type would misreport it.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildInterfaceInheritanceCache(cacheDir);

            var result = ApiQueryEngine.Members("Ifc.Ns.IObservableVector<T>", "size", cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
            var match = result.Data!.Properties.Single();
            Assert.AreEqual("Size", match.Name);
            Assert.AreEqual("Ifc.Ns.IVector<T>", match.DeclaringType);
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    #endregion

    #region Generic inheritance

    /// <summary>
    /// A hierarchy reached through constructed generics, the shape WinRT ships whenever a
    /// concrete collection type implements <c>IVector&lt;Object&gt;</c>: the definition
    /// declares its members in terms of <c>T</c>, and the derived type names the argument
    /// that <c>T</c> stands for.
    /// </summary>
    private static ProjectManifest BuildGenericInheritanceCache(string cacheDir)
    {
        WinMdTypeInfo Type(string name, string? baseType, List<string>? interfaces, params WinMdMemberInfo[] members) => new()
        {
            Namespace = "Gen.Ns",
            Name = name,
            FullName = "Gen.Ns." + name,
            Kind = TypeKind.Class,
            BaseType = baseType,
            Interfaces = interfaces,
            SourceFile = "gen.winmd",
            Members = members.ToList(),
        };

        WinMdMemberInfo Prop(string name, string type) => new()
        {
            Name = name,
            Kind = MemberKind.Property,
            Signature = $"{type} {name} {{ get; set; }}",
            ReturnType = type,
        };

        WinMdMemberInfo Method(string name, string returnType, string parameterType) => new()
        {
            Name = name,
            Kind = MemberKind.Method,
            Signature = $"{returnType} {name}({parameterType} item)",
            ReturnType = returnType,
            Parameters = [new WinMdParameterInfo { Name = "item", Type = parameterType }],
        };

        var namespaces = new Dictionary<string, List<WinMdTypeInfo>>(StringComparer.Ordinal)
        {
            ["Gen.Ns"] =
            [
                Type("Derived", "Gen.Ns.Middle<String>", null, Prop("Own", "Int32")),
                Type("Middle<T>", "Gen.Ns.Base<T>", null, Prop("FromMiddle", "T")),
                Type(
                    "Base<T>",
                    null,
                    null,
                    Prop("Value", "T"),
                    Method("Append", "Gen.Ns.IVector<T>", "T"),
                    // A member whose type merely starts with the parameter's name. A
                    // substring rewrite turns TimeSpan into StringimeSpan under T -> String.
                    Prop("Duration", "TimeSpan")),
                Type("Pairs", null, ["Gen.Ns.Map<String, Gen.Ns.Box<Int32>>"]),
                Type("Map<TKey, TValue>", null, null, Method("Lookup", "TValue", "TKey")),
            ],
        };
        WriteSyntheticPackage(cacheDir, "Gen.Pkg", namespaces);

        var manifest = new ProjectManifest
        {
            ProjectName = "GenApp",
            ProjectDir = Path.Combine(cacheDir, "src"),
            ProjectFile = "GenApp.csproj",
            Packages = [new ProjectPackageRef { Id = "Gen.Pkg", Version = "1.0.0", SourceStamp = TestSourceStamp, AssetPathKey = TestSourceStamp }],
            GeneratedAt = DateTime.UtcNow.ToString("o"),
        };
        string projectsDir = Path.Combine(cacheDir, "projects");
        Directory.CreateDirectory(projectsDir);
        File.WriteAllText(
            Path.Combine(projectsDir, "GenApp.json"),
            JsonSerializer.Serialize(manifest, ApiSearchJsonContext.Default.ProjectManifest));
        return manifest;
    }

    [TestMethod]
    public void Members_InheritedThroughAConstructedBase_ReportsTheArgumentNotTheParameter()
    {
        // `Derived : Middle<String>` inherits `T FromMiddle` as a String. Reporting the
        // parameter name hands back a signature naming a type that does not exist, which
        // is the one thing the command exists to prevent.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildGenericInheritanceCache(cacheDir);

            var result = ApiQueryEngine.Members("Gen.Ns.Derived", "FromMiddle", cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome, result.Message);
            var match = result.Data!.Properties.Single();
            Assert.AreEqual("String FromMiddle { get; set; }", match.Signature);
            Assert.AreEqual("String", match.ReturnType);
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void Members_InheritedThroughAChainOfGenericBases_SubstitutesAllTheWayDown()
    {
        // Base<T> is reached only through Middle<T>, which passes its own parameter on.
        // Stopping at the first level reports `T Value` on a type that has no T at all.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildGenericInheritanceCache(cacheDir);

            var value = ApiQueryEngine.Members("Gen.Ns.Derived", "Value", cacheDir, manifest);
            Assert.AreEqual(ApiQueryOutcome.Ok, value.Outcome, value.Message);
            Assert.AreEqual("String Value { get; set; }", value.Data!.Properties.Single().Signature);

            // Nested arguments and parameter lists get the same treatment.
            var append = ApiQueryEngine.Members("Gen.Ns.Derived", "Append", cacheDir, manifest);
            Assert.AreEqual(ApiQueryOutcome.Ok, append.Outcome, append.Message);
            var method = append.Data!.Methods.Single();
            Assert.AreEqual("Gen.Ns.IVector<String> Append(String item)", method.Signature);
            Assert.AreEqual("Gen.Ns.IVector<String>", method.ReturnType);
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void Members_InheritedTypeNameContainingAParameterName_IsLeftAlone()
    {
        // Substituting substrings rather than whole identifiers rewrites `TimeSpan` to
        // `StringimeSpan` under T -> String.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildGenericInheritanceCache(cacheDir);

            var result = ApiQueryEngine.Members("Gen.Ns.Derived", "Duration", cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome, result.Message);
            Assert.AreEqual("TimeSpan Duration { get; set; }", result.Data!.Properties.Single().Signature);
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void Members_InheritedThroughAMultiArgumentInterface_PairsArgumentsByPosition()
    {
        // A nested argument (Box<Int32>) contains the comma-free spelling of one argument
        // inside another; splitting on every comma pairs TKey with `String` and TValue
        // with `Gen.Ns.Box<Int32`.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildGenericInheritanceCache(cacheDir);

            var result = ApiQueryEngine.Members("Gen.Ns.Pairs", "Lookup", cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome, result.Message);
            Assert.AreEqual("Gen.Ns.Box<Int32> Lookup(String item)", result.Data!.Methods.Single().Signature);
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void Members_GenericDefinitionQueriedDirectly_KeepsItsParameterNames()
    {
        // Asking about the definition itself must still report it as declared: Base<T>
        // has no arguments to substitute, and inventing one would misreport the API.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildGenericInheritanceCache(cacheDir);

            var result = ApiQueryEngine.Members("Gen.Ns.Base<T>", "Value", cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome, result.Message);
            Assert.AreEqual("T Value { get; set; }", result.Data!.Properties.Single().Signature);
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    /// <summary>
    /// A base type may legally declare both a generic and a concrete overload of one
    /// method. Closing it over that same concrete type makes the two substitute to an
    /// identical signature, but they remain two declared members and both must survive.
    /// </summary>
    [TestMethod]
    public void Members_BaseDeclaringAGenericAndAConcreteOverload_KeepsBoth()
    {
        string cacheDir = NewCacheDir();
        try
        {
            var namespaces = new Dictionary<string, List<WinMdTypeInfo>>(StringComparer.Ordinal)
            {
                ["Ovl.Ns"] =
                [
                    new WinMdTypeInfo
                    {
                        Namespace = "Ovl.Ns",
                        Name = "Derived",
                        FullName = "Ovl.Ns.Derived",
                        Kind = TypeKind.Class,
                        BaseType = "Ovl.Ns.Base<String>",
                        SourceFile = "ovl.winmd",
                        Members = [],
                    },
                    new WinMdTypeInfo
                    {
                        Namespace = "Ovl.Ns",
                        Name = "Base<T>",
                        FullName = "Ovl.Ns.Base<T>",
                        Kind = TypeKind.Class,
                        SourceFile = "ovl.winmd",
                        Members =
                        [
                            new WinMdMemberInfo
                            {
                                Name = "M",
                                Kind = MemberKind.Method,
                                Signature = "void M(T item)",
                                ReturnType = "void",
                                Parameters = [new WinMdParameterInfo { Name = "item", Type = "T" }],
                            },
                            new WinMdMemberInfo
                            {
                                Name = "M",
                                Kind = MemberKind.Method,
                                Signature = "void M(String item)",
                                ReturnType = "void",
                                Parameters = [new WinMdParameterInfo { Name = "item", Type = "String" }],
                            },
                        ],
                    },
                ],
            };
            WriteSyntheticPackage(cacheDir, "Ovl.Pkg", namespaces);

            var manifest = new ProjectManifest
            {
                ProjectName = "OvlApp",
                ProjectDir = Path.Combine(cacheDir, "src"),
                ProjectFile = "OvlApp.csproj",
                Packages = [new ProjectPackageRef { Id = "Ovl.Pkg", Version = "1.0.0", SourceStamp = TestSourceStamp, AssetPathKey = TestSourceStamp }],
                GeneratedAt = DateTime.UtcNow.ToString("o"),
            };
            string projectsDir = Path.Combine(cacheDir, "projects");
            Directory.CreateDirectory(projectsDir);
            File.WriteAllText(
                Path.Combine(projectsDir, "OvlApp.json"),
                JsonSerializer.Serialize(manifest, ApiSearchJsonContext.Default.ProjectManifest));

            var result = ApiQueryEngine.Members("Ovl.Ns.Derived", "M", cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome, result.Message);
            Assert.AreEqual(2, result.Data!.Methods.Count, "Both declared overloads must be reported.");
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    /// <summary>
    /// The other half of the same rule: a derived type that redeclares an inherited
    /// member must still hide the base's copy, so it is reported once and attributed to
    /// the type that redeclared it. Holding a supertype's keys back must not weaken this.
    /// </summary>
    [TestMethod]
    public void Members_DerivedRedeclaringAnInheritedMember_HidesTheBaseCopy()
    {
        string cacheDir = NewCacheDir();
        try
        {
            WinMdMemberInfo Method(string parameterType) => new()
            {
                Name = "M",
                Kind = MemberKind.Method,
                Signature = $"void M({parameterType} item)",
                ReturnType = "void",
                Parameters = [new WinMdParameterInfo { Name = "item", Type = parameterType }],
            };

            var namespaces = new Dictionary<string, List<WinMdTypeInfo>>(StringComparer.Ordinal)
            {
                ["Ovr.Ns"] =
                [
                    new WinMdTypeInfo
                    {
                        Namespace = "Ovr.Ns",
                        Name = "Derived",
                        FullName = "Ovr.Ns.Derived",
                        Kind = TypeKind.Class,
                        BaseType = "Ovr.Ns.Base<String>",
                        SourceFile = "ovr.winmd",
                        Members = [Method("String")],
                    },
                    new WinMdTypeInfo
                    {
                        Namespace = "Ovr.Ns",
                        Name = "Base<T>",
                        FullName = "Ovr.Ns.Base<T>",
                        Kind = TypeKind.Class,
                        SourceFile = "ovr.winmd",
                        Members = [Method("T")],
                    },
                ],
            };
            WriteSyntheticPackage(cacheDir, "Ovr.Pkg", namespaces);

            var manifest = new ProjectManifest
            {
                ProjectName = "OvrApp",
                ProjectDir = Path.Combine(cacheDir, "src"),
                ProjectFile = "OvrApp.csproj",
                Packages = [new ProjectPackageRef { Id = "Ovr.Pkg", Version = "1.0.0", SourceStamp = TestSourceStamp, AssetPathKey = TestSourceStamp }],
                GeneratedAt = DateTime.UtcNow.ToString("o"),
            };
            string projectsDir = Path.Combine(cacheDir, "projects");
            Directory.CreateDirectory(projectsDir);
            File.WriteAllText(
                Path.Combine(projectsDir, "OvrApp.json"),
                JsonSerializer.Serialize(manifest, ApiSearchJsonContext.Default.ProjectManifest));

            var result = ApiQueryEngine.Members("Ovr.Ns.Derived", "M", cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome, result.Message);
            var method = result.Data!.Methods.Single();
            Assert.AreEqual("void M(String item)", method.Signature);

            // A member declared on the queried type itself is not attributed to anything;
            // the point here is that it is not attributed to Base<T>, which would mean the
            // base copy won and the redeclaration was the one that got dropped.
            Assert.IsTrue(
                string.IsNullOrEmpty(method.DeclaringType),
                $"Expected the redeclared member, but got one attributed to '{method.DeclaringType}'.");
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    /// <summary>
    /// Metadata parameter and member names are whatever the author wrote, including the
    /// letters used for type parameters. Substituting the rendered signature blindly
    /// renames them: `void Add(T T)` becomes `void Add(String String)`, and the signature
    /// is the whole of what a caller sees for a member.
    /// </summary>
    [TestMethod]
    public void Members_InheritedMemberWhoseNamesLookLikeTypeParameters_KeepsThoseNames()
    {
        string cacheDir = NewCacheDir();
        try
        {
            var namespaces = new Dictionary<string, List<WinMdTypeInfo>>(StringComparer.Ordinal)
            {
                ["Nm.Ns"] =
                [
                    new WinMdTypeInfo
                    {
                        Namespace = "Nm.Ns",
                        Name = "Derived",
                        FullName = "Nm.Ns.Derived",
                        Kind = TypeKind.Class,
                        BaseType = "Nm.Ns.Base<String>",
                        SourceFile = "nm.winmd",
                        Members = [],
                    },
                    new WinMdTypeInfo
                    {
                        Namespace = "Nm.Ns",
                        Name = "Base<T>",
                        FullName = "Nm.Ns.Base<T>",
                        Kind = TypeKind.Class,
                        SourceFile = "nm.winmd",
                        Members =
                        [
                            new WinMdMemberInfo
                            {
                                Name = "Add",
                                Kind = MemberKind.Method,
                                Signature = "void Add(T T)",
                                ReturnType = "void",
                                Parameters = [new WinMdParameterInfo { Name = "T", Type = "T" }],
                            },
                            new WinMdMemberInfo
                            {
                                Name = "T",
                                Kind = MemberKind.Property,
                                Signature = "T T { get; set; }",
                                ReturnType = "T",
                            },
                        ],
                    },
                ],
            };
            WriteSyntheticPackage(cacheDir, "Nm.Pkg", namespaces);

            var manifest = new ProjectManifest
            {
                ProjectName = "NmApp",
                ProjectDir = Path.Combine(cacheDir, "src"),
                ProjectFile = "NmApp.csproj",
                Packages = [new ProjectPackageRef { Id = "Nm.Pkg", Version = "1.0.0", SourceStamp = TestSourceStamp, AssetPathKey = TestSourceStamp }],
                GeneratedAt = DateTime.UtcNow.ToString("o"),
            };
            string projectsDir = Path.Combine(cacheDir, "projects");
            Directory.CreateDirectory(projectsDir);
            File.WriteAllText(
                Path.Combine(projectsDir, "NmApp.json"),
                JsonSerializer.Serialize(manifest, ApiSearchJsonContext.Default.ProjectManifest));

            var method = ApiQueryEngine.Members("Nm.Ns.Derived", "Add", cacheDir, manifest);
            Assert.AreEqual(ApiQueryOutcome.Ok, method.Outcome, method.Message);
            Assert.AreEqual("void Add(String T)", method.Data!.Methods.Single().Signature);

            var property = ApiQueryEngine.Members("Nm.Ns.Derived", "T", cacheDir, manifest);
            Assert.AreEqual(ApiQueryOutcome.Ok, property.Outcome, property.Message);
            Assert.AreEqual("String T { get; set; }", property.Data!.Properties.Single().Signature);
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    #endregion

    #region Public fields

    private static readonly string[] ExpectedPackageVersionFields = ["Major", "Minor", "Frozen", "Limit"];

    /// <summary>
    /// The shape a WinRT/interop struct actually ships: no properties or methods at all,
    /// only public fields. <c>CoreWebView2PhysicalKeyStatus</c> and <c>PackageVersion</c>
    /// are both this.
    /// </summary>
    private static ProjectManifest BuildFieldCache(string cacheDir)
    {
        WinMdMemberInfo Field(string name, string signature) => new()
        {
            Name = name,
            Kind = MemberKind.Field,
            Signature = signature,
            ReturnType = "UInt16",
        };

        var namespaces = new Dictionary<string, List<WinMdTypeInfo>>(StringComparer.Ordinal)
        {
            ["Fld.Ns"] =
            [
                new WinMdTypeInfo
                {
                    Namespace = "Fld.Ns",
                    Name = "PackageVersion",
                    FullName = "Fld.Ns.PackageVersion",
                    Kind = TypeKind.Struct,
                    SourceFile = "fld.winmd",
                    Members =
                    [
                        Field("Major", "UInt16 Major"),
                        Field("Minor", "UInt16 Minor"),
                        Field("Frozen", "readonly UInt16 Frozen"),
                        Field("Limit", "const UInt16 Limit"),
                    ],
                },
            ],
        };
        WriteSyntheticPackage(cacheDir, "Fld.Pkg", namespaces);

        var manifest = new ProjectManifest
        {
            ProjectName = "FldApp",
            ProjectDir = Path.Combine(cacheDir, "src"),
            ProjectFile = "FldApp.csproj",
            Packages = [new ProjectPackageRef { Id = "Fld.Pkg", Version = "1.0.0", SourceStamp = TestSourceStamp, AssetPathKey = TestSourceStamp }],
            GeneratedAt = DateTime.UtcNow.ToString("o"),
        };
        string projectsDir = Path.Combine(cacheDir, "projects");
        Directory.CreateDirectory(projectsDir);
        File.WriteAllText(
            Path.Combine(projectsDir, "FldApp.json"),
            JsonSerializer.Serialize(manifest, ApiSearchJsonContext.Default.ProjectManifest));
        return manifest;
    }

    [TestMethod]
    public void Members_OfAStructWithOnlyFields_ListsThem()
    {
        // Omitting fields renders such a type as "(no declared members)", which reads as
        // "this API is unusable" for a struct whose entire surface is fields.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildFieldCache(cacheDir);

            var result = ApiQueryEngine.Members("Fld.Ns.PackageVersion", filter: null, cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
            Assert.IsNotNull(result.Data!.Fields, "a field-only struct must report its fields");
            CollectionAssert.AreEquivalent(
                ExpectedPackageVersionFields,
                result.Data.Fields!.ConvertAll(f => f.Name));
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void CheckProperty_APublicField_IsFoundAndWritable()
    {
        // `version.Major` is what the caller writes; answering "does not have property
        // 'Major'" is a false negative on the exact question the command answers.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildFieldCache(cacheDir);

            var result = ApiQueryEngine.CheckProperty("Fld.Ns.PackageVersion", "Major", cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
            Assert.IsTrue(result.Data!.Found);
            Assert.AreEqual(true, result.Data.Writable, "a plain field is assignable");
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [DataRow("Frozen")]
    [DataRow("Limit")]
    [TestMethod]
    public void CheckProperty_AReadOnlyOrConstField_IsFoundButNotWritable(string fieldName)
    {
        // Existence alone is not a green light to assign: telling a caller a const field
        // is writable produces code that will not compile.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildFieldCache(cacheDir);

            var result = ApiQueryEngine.CheckProperty("Fld.Ns.PackageVersion", fieldName, cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
            Assert.IsTrue(result.Data!.Found);
            Assert.AreEqual(false, result.Data.Writable);
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    #endregion

    [TestMethod]
    public void CheckProperty_WrongCase_IsNotFoundButSuggestsTheRealSpelling()
    {
        // C# and XAML are both case-sensitive, so answering "found" for "color" sends a
        // caller off to write code that will not compile. The near-miss pass still has to
        // surface the canonical spelling, or the answer is unhelpful rather than merely
        // strict.
        var result = ApiQueryEngine.CheckProperty("My.Ns.Widget", "color", _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.IsFalse(result.Data!.Found, "a case-only difference is not the same property");
        Assert.IsNotNull(result.Data.SimilarOnType, "the correct spelling must still be offered");
        CollectionAssert.Contains(result.Data.SimilarOnType!.ConvertAll(m => m.Name), "Color");
    }

    [TestMethod]
    public void CheckProperty_AttachedProperty_IsFound()
    {
        // Grid.Row and friends have no property of that name on the type — only static
        // Get/Set accessors taking a DependencyObject.
        var result = ApiQueryEngine.CheckProperty("My.Ns.Gadget", "Row", _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.IsTrue(result.Data!.Found, "an attached property is a real property");
        Assert.IsTrue(result.Data.Attached);
        StringAssert.Contains(result.Data.AttachedInfo ?? string.Empty, "Gadget.GetRow()");
        Assert.AreEqual(true, result.Data.Writable, "a Get/Set pair can be assigned");
    }

    [TestMethod]
    public void CheckProperty_AttachedPropertyWithNoSetter_ReportsItReadOnly()
    {
        // A getter-only attached property is found, so the check is not a failure — but
        // answering with the same bare tick a settable one gets invites the caller to
        // write `<Button local:Gadget.Level="1"/>`, which does not compile. The
        // structured answer has to say so too, since --json is what an agent reads.
        var result = ApiQueryEngine.CheckProperty("My.Ns.Gadget", "Level", _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.IsTrue(result.Data!.Found);
        Assert.IsTrue(result.Data.Attached);
        Assert.AreEqual(false, result.Data.Writable, "no SetLevel means it cannot be assigned");
    }

    [TestMethod]
    public void CheckProperty_AttachedPropertyWrongCase_IsNotFound()
    {
        // XAML is case-sensitive: 'Grid.row' does not load. Matching the Get/Set
        // accessors case-insensitively answers "found" for a spelling that fails at
        // runtime, and the direct-property path already rejects the same mistake.
        var result = ApiQueryEngine.CheckProperty("My.Ns.Gadget", "row", _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.IsFalse(result.Data!.Found, "a case-only difference is not the same attached property");
    }

    [TestMethod]
    public void CheckProperty_InstanceGetSetAccessors_IsNotAttachedProperty()
    {
        // An attached property is set on *another* object, so its accessors must be
        // static: `Grid.SetRow(myButton, 0)`. A type with instance GetRow/SetRow has
        // ordinary methods and no Row property at all. Reporting "found" for it tells a
        // caller to emit `InstanceGadget.SetRow(element, 0)`, which does not compile —
        // the confidently-wrong answer this command exists to prevent.
        var result = ApiQueryEngine.CheckProperty("My.Ns.InstanceGadget", "Row", _cacheDir, _manifest);

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.IsFalse(result.Data!.Found, "instance Get/Set accessors are not an attached property");
        Assert.IsFalse(result.Data.Attached);
    }

    private static readonly string[] ThreeCheckedPropertyNames = ["Color", "Bogus", "DoThing"];

    [TestMethod]
    public void CheckProperties_ReturnsOneResultPerRequestedProperty()
    {
        var results = ApiQueryEngine.CheckProperties(
            "My.Ns.Widget", ThreeCheckedPropertyNames, _cacheDir, _manifest);

        CollectionAssert.AreEqual(ThreeCheckedPropertyNames, results.ConvertAll(r => r.Property));
        Assert.IsTrue(results[0].Result.Data!.Found);
        Assert.IsFalse(results[1].Result.Data!.Found);
        Assert.IsFalse(results[2].Result.Data!.Found, "a method of the same name is not a property");
    }

    [TestMethod]
    public void CheckProperties_MatchesWhatCheckPropertyReturnsOneAtATime()
    {
        // The batch path exists only to avoid reloading the index; it must not change
        // any answer.
        string[] names = ["Color", "color", "Bogus", "DoThing"];
        var batch = ApiQueryEngine.CheckProperties("My.Ns.Widget", names, _cacheDir, _manifest);

        foreach (string name in names)
        {
            var single = ApiQueryEngine.CheckProperty("My.Ns.Widget", name, _cacheDir, _manifest);
            var fromBatch = batch.Single(r => r.Property == name).Result;
            Assert.AreEqual(single.Outcome, fromBatch.Outcome, name);
            Assert.AreEqual(single.Data?.Found, fromBatch.Data?.Found, name);
        }
    }

    [TestMethod]
    public void MembersBatch_MatchesWhatMembersReturnsOneAtATime()
    {
        string[] names = ["My.Ns.Widget", "My.Ns.Nope"];
        var batch = ApiQueryEngine.MembersBatch(names, null, _cacheDir, _manifest);

        foreach (string name in names)
        {
            var single = ApiQueryEngine.Members(name, null, _cacheDir, _manifest);
            var fromBatch = batch.Single(r => r.Type == name).Result;
            Assert.AreEqual(single.Outcome, fromBatch.Outcome, name);
            Assert.AreEqual(single.Data?.FullName, fromBatch.Data?.FullName, name);
        }
    }

    [TestMethod]
    public void CheckProperty_StillReportsMemberKind()
    {
        // check-property returns a single member that is not grouped by kind, so the
        // kind is real information there and must survive the members-listing trim.
        var result = ApiQueryEngine.CheckProperty("My.Ns.Widget", "Color", _cacheDir, _manifest);

        Assert.IsTrue(result.Data!.Found);
        Assert.AreEqual(nameof(MemberKind.Property), result.Data.Match!.Kind);
    }

    #endregion

    #region Generic type resolution

    /// <summary>
    /// Metadata stores a generic under the name it was compiled with —
    /// <c>IAsyncOperation`1</c> — but nobody types the arity suffix. These cover the
    /// forms a caller (or an agent generating code) actually writes.
    /// </summary>
    private static ProjectManifest BuildGenericCache(string cacheDir)
    {
        var namespaces = new Dictionary<string, List<WinMdTypeInfo>>(StringComparer.Ordinal)
        {
            ["Gen.Ns"] =
            [
                SimpleType("Gen.Ns", "Holder`1"),
                SimpleType("Gen.Ns", "Pair`1"),
                SimpleType("Gen.Ns", "Pair`2"),
                SimpleType("Gen.Ns", "Plain"),
            ],
        };
        WriteSyntheticPackage(cacheDir, "Gen.Pkg", namespaces);

        var manifest = new ProjectManifest
        {
            ProjectName = "GenApp",
            ProjectDir = Path.Combine(cacheDir, "src"),
            ProjectFile = "GenApp.csproj",
            Packages = [new ProjectPackageRef { Id = "Gen.Pkg", Version = "1.0.0", SourceStamp = TestSourceStamp, AssetPathKey = TestSourceStamp }],
            GeneratedAt = DateTime.UtcNow.ToString("o"),
        };
        string projectsDir = Path.Combine(cacheDir, "projects");
        Directory.CreateDirectory(projectsDir);
        File.WriteAllText(
            Path.Combine(projectsDir, "GenApp.json"),
            JsonSerializer.Serialize(manifest, ApiSearchJsonContext.Default.ProjectManifest));
        return manifest;
    }

    [TestMethod]
    // Short name, no arity — the form a caller types.
    [DataRow("Holder")]
    // Fully-qualified, still without the arity suffix.
    [DataRow("Gen.Ns.Holder")]
    // The C# / source form an agent writes when generating code.
    [DataRow("Holder<Plain>")]
    [DataRow("Gen.Ns.Holder<Plain>")]
    // A nested argument is still one type argument.
    [DataRow("Holder<Pair<Plain, Plain>>")]
    // The metadata form itself must keep working.
    [DataRow("Holder`1")]
    [DataRow("Gen.Ns.Holder`1")]
    public void Members_GenericTypeInAnyWrittenForm_Resolves(string typeName)
    {
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildGenericCache(cacheDir);

            var result = ApiQueryEngine.Members(typeName, filter: null, cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome, result.Message);
            Assert.AreEqual("Gen.Ns.Holder`1", result.Data!.FullName);
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    [DataRow("Pair`2")]
    [DataRow("Pair<Plain, Plain>")]
    [DataRow("Gen.Ns.Pair<Plain, Plain>")]
    public void Members_GenericWithStatedArity_SelectsThatArity(string typeName)
    {
        // Dropping the arity entirely would make Pair`1 and Pair`2 interchangeable. An
        // arity the caller actually stated has to keep picking one out.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildGenericCache(cacheDir);

            var result = ApiQueryEngine.Members(typeName, filter: null, cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome, result.Message);
            Assert.AreEqual("Gen.Ns.Pair`2", result.Data!.FullName);
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void Members_GenericWithoutArityMatchingSeveralArities_IsAmbiguous()
    {
        // "Pair" alone cannot mean Pair`1 or Pair`2 — answering with either would be a
        // confident answer about a type the caller did not ask for.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildGenericCache(cacheDir);

            var result = ApiQueryEngine.Members("Pair", filter: null, cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.InvalidInput, result.Outcome);
            StringAssert.Contains(result.Message, "Gen.Ns.Pair`1");
            StringAssert.Contains(result.Message, "Gen.Ns.Pair`2");
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void Members_NonGenericTypeNameEndingInAngleBrackets_IsStillNotFound()
    {
        // Arity handling must not manufacture matches: a non-generic type asked for with
        // type arguments is a real mistake and has to stay a miss.
        string cacheDir = NewCacheDir();
        try
        {
            ProjectManifest manifest = BuildGenericCache(cacheDir);

            var result = ApiQueryEngine.Members("Plain<Holder>", filter: null, cacheDir, manifest);

            Assert.AreEqual(ApiQueryOutcome.NotFound, result.Outcome);
        }
        finally
        {
            TryDeleteDir(cacheDir);
        }
    }

    #endregion

    private static string NewCacheDir() =>
        Path.Combine(Path.GetTempPath(), $"ApiQueryEngineDedupe_{Guid.NewGuid():N}");
    private static void TryDeleteDir(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>
    /// Builds a cache where two packages each ship an identical set of namespaces and types —
    /// the real-world shape that produced duplicate search hits — plus ABI projection twins
    /// and a type whose only "second namespace" is its ABI twin.
    /// </summary>
    private static ProjectManifest BuildMultiPackageCache(string cacheDir)
    {
        var namespaces = new Dictionary<string, List<WinMdTypeInfo>>(StringComparer.Ordinal)
        {
            ["Dup.Ns"] =
            [
                SimpleType("Dup.Ns", "Alpha"),
                SimpleType("Dup.Ns", "AlphaOne"),
                SimpleType("Dup.Ns", "AlphaTwo"),
                SimpleType("Dup.Ns", "AlphaThree"),
                SimpleType("Dup.Ns", "AlphaFour"),
                SimpleType("Dup.Ns", "AlphaFive"),
            ],
            ["Other.Ns"] = [SimpleType("Other.Ns", "Alpha")],
            ["ABI.Dup.Ns"] = [SimpleType("ABI.Dup.Ns", "Alpha")],
            ["Solo.Ns"] = [SimpleType("Solo.Ns", "Solo")],
            ["ABI.Solo.Ns"] = [SimpleType("ABI.Solo.Ns", "Solo")],

            // The WinUI 3 / UWP twin shape: the same short name in a modern
            // Microsoft.* namespace and its legacy Windows.* counterpart.
            ["Microsoft.UI.Input"] = [SimpleType("Microsoft.UI.Input", "Twin")],
            ["Windows.UI.Input"] = [SimpleType("Windows.UI.Input", "Twin")],

            // A third-party type colliding with a Microsoft one is not a twin pair.
            ["Microsoft.UI.Xaml.Controls"] = [SimpleType("Microsoft.UI.Xaml.Controls", "Widget")],
            ["Contoso.Controls"] = [SimpleType("Contoso.Controls", "Widget")],
        };

        var packages = new List<ProjectPackageRef>();
        foreach (string packageId in DuplicatePackageIds)
        {
            WriteSyntheticPackage(cacheDir, packageId, namespaces);
            packages.Add(new ProjectPackageRef { Id = packageId, Version = "1.0.0", SourceStamp = TestSourceStamp, AssetPathKey = TestSourceStamp });
        }

        var manifest = new ProjectManifest
        {
            ProjectName = "DupApp",
            ProjectDir = Path.Combine(cacheDir, "src"),
            ProjectFile = "DupApp.csproj",
            Packages = packages,
            GeneratedAt = DateTime.UtcNow.ToString("o"),
        };
        string projectsDir = Path.Combine(cacheDir, "projects");
        Directory.CreateDirectory(projectsDir);
        File.WriteAllText(
            Path.Combine(projectsDir, "DupApp.json"),
            JsonSerializer.Serialize(manifest, ApiSearchJsonContext.Default.ProjectManifest));
        return manifest;
    }

    private static WinMdTypeInfo SimpleType(string ns, string name) => new()
    {
        Namespace = ns,
        Name = name,
        FullName = ns + "." + name,
        Kind = TypeKind.Class,
        SourceFile = "dup.winmd",
        Members = [],
    };

    private static void WriteSyntheticPackage(string cacheDir, string packageId, Dictionary<string, List<WinMdTypeInfo>> namespaces)
    {
        string packageDir = Path.Combine(cacheDir, "packages", packageId, "1.0.0", TestSourceStamp);
        string typesDir = Path.Combine(packageDir, "types");
        Directory.CreateDirectory(typesDir);
        foreach ((string ns, List<WinMdTypeInfo> types) in namespaces)
        {
            File.WriteAllText(
                Path.Combine(typesDir, ApiCachePaths.NamespaceFileName(ns)),
                JsonSerializer.Serialize(types, ApiSearchJsonContext.Default.ListWinMdTypeInfo));
        }
        File.WriteAllText(
            Path.Combine(packageDir, "namespaces.json"),
            JsonSerializer.Serialize(namespaces.Keys.ToList(), ApiSearchJsonContext.Default.ListString));

        var meta = new PackageMeta
        {
            PackageId = packageId,
            Version = "1.0.0",
            WinMdFiles = ["dup.winmd"],
            TotalTypes = namespaces.Sum(kv => kv.Value.Count),
            TotalMembers = 0,
            TotalNamespaces = namespaces.Count,
            GeneratedAt = DateTime.UtcNow.ToString("o"),
        };
        File.WriteAllText(Path.Combine(packageDir, "meta.json"), JsonSerializer.Serialize(meta, ApiSearchJsonContext.Default.PackageMeta));
    }

    #endregion
}
