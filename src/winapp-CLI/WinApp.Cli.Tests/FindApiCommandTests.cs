// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using System.CommandLine;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;
using WinApp.Cli.Services.ApiSearch;

namespace WinApp.Cli.Tests;

/// <summary>
/// A hand-rolled <see cref="IApiMetadataService"/> that records what each verb was
/// called with and returns canned results, so the command layer can be tested for
/// argument parsing, routing, exit codes, and JSON shape without a real cache.
/// Special query/property sentinels drive the non-happy paths.
/// </summary>
internal sealed class FakeApiMetadataService : IApiMetadataService
{
    public const string EmptyQuery = "__empty__";
    public const string NoProjectQuery = "__noproject__";
    public const string MissingProperty = "__missing__";
    public const string MissingType = "__notype__";

    /// <summary>A getter-only attached property, which is found but cannot be assigned.</summary>
    public const string AttachedReadOnlyProperty = "__attachedreadonly__";

    /// <summary>Cache path the fake reports for a namespace hit; only shown at --verbose.</summary>
    public const string CacheFilePath = @"C:\fake\cache\find-api\Test_Ns.json";

    /// <summary>Every subject passed to each verb, in call order — lets batch tests assert fan-out.</summary>
    public List<string> SearchQueries { get; } = new();
    public List<string> MembersTypes { get; } = new();
    public List<string> EnumsTypes { get; } = new();
    public List<string> CheckedProperties { get; } = new();

    public bool SearchCalled { get; private set; }
    public string? LastSearchQuery { get; private set; }
    public int LastMax { get; private set; }
    public string? LastMembersType { get; private set; }
    public string? LastMembersFilter { get; private set; }
    public bool LastMembersIncludeAll { get; private set; }
    public string? LastEnumsFilter { get; private set; }
    public string? LastCheckType { get; private set; }
    public string? LastCheckProperty { get; private set; }
    public ApiRequestScope LastScope { get; private set; }
    public bool LastRefreshForce { get; private set; }

    public ApiQueryResult<ApiSearchOutput> Search(string query, int maxResults, ApiRequestScope scope)
    {
        SearchCalled = true;
        LastSearchQuery = query;
        LastMax = maxResults;
        LastScope = scope;
        SearchQueries.Add(query);

        if (query == NoProjectQuery)
        {
            return ApiQueryResult<ApiSearchOutput>.NoProject("No indexed API metadata was found for this project.");
        }

        var results = query == EmptyQuery
            ? new List<ApiNamespaceHit>()
            : new List<ApiNamespaceHit>
            {
                new()
                {
                    Namespace = "Test.Ns",
                    Score = 100,
                    Files = new List<string> { CacheFilePath },
                    Matches = new List<ApiTypeHit> { new() { Display = "Class Test.Ns.Foo", Score = 100 } },
                },
            };
        return ApiQueryResult<ApiSearchOutput>.Ok(new ApiSearchOutput { Query = query, Ambiguous = null, Results = results });
    }

    public ApiQueryResult<ApiMembersOutput> Members(string fullName, ApiRequestScope scope, string? filter = null, bool includeAll = false)
    {
        LastMembersType = fullName;
        LastMembersFilter = filter;
        LastMembersIncludeAll = includeAll;
        LastScope = scope;
        MembersTypes.Add(fullName);
        if (fullName == MissingType)
        {
            return ApiQueryResult<ApiMembersOutput>.NotFound($"Type '{fullName}' was not found.");
        }

        return ApiQueryResult<ApiMembersOutput>.Ok(new ApiMembersOutput
        {
            FullName = fullName,
            Kind = "Class",
            Properties = new List<ApiMemberOutput>
            {
                new() { Name = "Color", Kind = "Property", Signature = "String Color { get; set; }" },
            },
            Events = new List<ApiMemberOutput>(),
            Methods = new List<ApiMemberOutput>(),
        });
    }

    public ApiQueryResult<ApiCheckPropertyOutput> CheckProperty(string typeName, string propertyName, ApiRequestScope scope)
    {
        LastCheckType = typeName;
        LastCheckProperty = propertyName;
        LastScope = scope;
        CheckedProperties.Add(propertyName);
        if (propertyName == AttachedReadOnlyProperty)
        {
            return ApiQueryResult<ApiCheckPropertyOutput>.Ok(new ApiCheckPropertyOutput
            {
                Found = true,
                Type = typeName,
                Property = propertyName,
                Attached = true,
                AttachedInfo = "Int32 — via Gadget.GetLevel() (read-only)",
                Writable = false,
            });
        }

        bool found = propertyName != MissingProperty;
        return ApiQueryResult<ApiCheckPropertyOutput>.Ok(new ApiCheckPropertyOutput
        {
            Found = found,
            Type = typeName,
            Property = propertyName,
            Match = found ? new ApiMemberOutput { Name = propertyName, Kind = "Property", Signature = $"String {propertyName}" } : null,
            SimilarOnType = new List<ApiMemberOutput>(),
            TypesWithProperty = new List<ApiCrossTypeMember>(),
            TypesWithSimilar = new List<ApiCrossTypeMember>(),
        });
    }

    /// <summary>
    /// Counts batch calls so a test can prove the command asks for the whole batch at
    /// once. Checking one property at a time reloads the on-disk index per property,
    /// which is the entire cost of the operation.
    /// </summary>
    public int CheckPropertiesCallCount { get; private set; }

    /// <summary>Counts batch calls for <c>members</c>, as <see cref="CheckPropertiesCallCount"/> does for properties.</summary>
    public int MembersBatchCallCount { get; private set; }

    /// <summary>Counts batch calls for <c>enums</c>, as <see cref="CheckPropertiesCallCount"/> does for properties.</summary>
    public int EnumsBatchCallCount { get; private set; }

    public List<(string Type, ApiQueryResult<ApiMembersOutput> Result)> MembersBatch(
        IReadOnlyList<string> fullNames, ApiRequestScope scope, string? filter = null, bool includeAll = false)
    {
        MembersBatchCallCount++;
        var results = new List<(string, ApiQueryResult<ApiMembersOutput>)>(fullNames.Count);
        foreach (string fullName in fullNames)
        {
            results.Add((fullName, Members(fullName, scope, filter, includeAll)));
        }
        return results;
    }

    public List<(string Type, ApiQueryResult<ApiEnumsOutput> Result)> EnumsBatch(
        IReadOnlyList<string> fullNames, ApiRequestScope scope, string? filter = null)
    {
        EnumsBatchCallCount++;
        var results = new List<(string, ApiQueryResult<ApiEnumsOutput>)>(fullNames.Count);
        foreach (string fullName in fullNames)
        {
            results.Add((fullName, Enums(fullName, scope, filter)));
        }
        return results;
    }

    public List<(string Property, ApiQueryResult<ApiCheckPropertyOutput> Result)> CheckProperties(
        string typeName, IReadOnlyList<string> propertyNames, ApiRequestScope scope)
    {
        CheckPropertiesCallCount++;
        var results = new List<(string, ApiQueryResult<ApiCheckPropertyOutput>)>(propertyNames.Count);
        foreach (string propertyName in propertyNames)
        {
            results.Add((propertyName, CheckProperty(typeName, propertyName, scope)));
        }
        return results;
    }

    public ApiQueryResult<ApiTypesOutput> Types(string ns, ApiRequestScope scope)
    {
        LastScope = scope;
        return ApiQueryResult<ApiTypesOutput>.Ok(new ApiTypesOutput
        {
            Namespace = ns,
            Types = new List<ApiTypeSummary> { new() { FullName = ns + ".Foo", Kind = "Class" } },
        });
    }

    public ApiQueryResult<ApiEnumsOutput> Enums(string fullName, ApiRequestScope scope, string? filter = null)
    {
        LastScope = scope;
        LastEnumsFilter = filter;
        EnumsTypes.Add(fullName);
        return ApiQueryResult<ApiEnumsOutput>.Ok(new ApiEnumsOutput
        {
            FullName = fullName,
            Values = new List<string> { "One", "Two" },
        });
    }

    public ApiQueryResult<ApiNamespacesOutput> Namespaces(string? filter, ApiRequestScope scope)
    {
        LastScope = scope;
        return ApiQueryResult<ApiNamespacesOutput>.Ok(new ApiNamespacesOutput
        {
            Namespaces = new List<string> { "Test.Ns" },
        });
    }

    public ApiQueryResult<ApiPackagesOutput> Packages(ApiRequestScope scope)
    {
        LastScope = scope;
        return ApiQueryResult<ApiPackagesOutput>.Ok(new ApiPackagesOutput
        {
            ProjectName = "TestApp",
            Packages = new List<ApiPackageSummary>
            {
                new() { Id = "Test.Pkg", Version = "1.0.0", Status = "ok", TotalTypes = 1, TotalMembers = 1 },
                new() { Id = "Partial.Pkg", Version = "2.0.0", Status = "incomplete", TotalTypes = 7, TotalMembers = 42 },
            },
        });
    }

    public ApiQueryResult<ApiStatsOutput> Stats(ApiRequestScope scope)
    {
        LastScope = scope;
        return ApiQueryResult<ApiStatsOutput>.Ok(new ApiStatsOutput
        {
            ProjectName = "TestApp", Packages = 1, Namespaces = 1, Types = 1, Members = 1, WinMdFiles = 1,
        });
    }

    public ApiProjectsOutput Projects() => new()
    {
        Projects = new List<ApiProjectSummary> { new() { Name = "TestApp", PackageCount = 1 } },
    };

    public ApiQueryResult<ApiRefreshOutput> Refresh(ApiRequestScope scope, bool scan, Action<string>? onProgress = null, bool force = false)
    {
        LastScope = scope;
        LastRefreshForce = force;
        return ApiQueryResult<ApiRefreshOutput>.Ok(new ApiRefreshOutput
        {
            ProjectsProcessed = 1, PackagesParsed = 1, PackagesReused = 0, ProjectNames = new List<string> { "TestApp" },
        });
    }
}

[TestClass]
public sealed class FindApiCommandTests : BaseCommandTests
{
    private static readonly string[] ThreeSearchSubjects = ["NavigationView", "TeachingTip", "InfoBar"];
    private static readonly string[] TwoMemberTypes = ["InfoBar", "TeachingTip"];
    private static readonly string[] TwoEnumTypes = ["Symbol", "Visibility"];
    private static readonly string[] ThreeCheckedProperties = ["Severity", "IsOpen", "Message"];

    private FakeApiMetadataService _fake = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fake = new FakeApiMetadataService();
        return services.AddSingleton<IApiMetadataService>(_fake);
    }

    private FindApiCommand Command => GetRequiredService<FindApiCommand>();

    [TestMethod]
    public async Task BareQuery_RoutesToSearch_ExitsZero()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["NavigationView"]);

        Assert.AreEqual(0, exit);
        Assert.IsTrue(_fake.SearchCalled);
        Assert.AreEqual("NavigationView", _fake.LastSearchQuery);
    }

    [TestMethod]
    public async Task NoQuery_ShowsUsage_WithoutCallingService()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, []);

        Assert.AreEqual(0, exit);
        Assert.IsFalse(_fake.SearchCalled);
        StringAssert.Contains(TestAnsiConsole.Output, "Usage");
        StringAssert.Contains(TestAnsiConsole.Output, "check-property");
    }

    [TestMethod]
    public async Task NoQuery_WithJson_Fails_WithoutCallingService()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["--json"]);

        Assert.AreEqual(1, exit);
        Assert.IsFalse(_fake.SearchCalled);
    }

    [TestMethod]
    public async Task MaxZero_Fails()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["NavigationView", "--max", "0"]);

        Assert.AreEqual(1, exit);
    }

    [TestMethod]
    public async Task EmptyResults_ExitsOne()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, [FakeApiMetadataService.EmptyQuery]);

        Assert.AreEqual(1, exit);
    }

    [TestMethod]
    public async Task NoProject_ExitsOne_AndReportsMessage()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, [FakeApiMetadataService.NoProjectQuery]);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "No indexed API metadata");
    }

    [TestMethod]
    public async Task Members_RoutesThroughParent()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["members", "Microsoft.UI.Xaml.Controls.NavigationView"]);

        Assert.AreEqual(0, exit);
        Assert.AreEqual("Microsoft.UI.Xaml.Controls.NavigationView", _fake.LastMembersType);
    }

    [TestMethod]
    public async Task Members_NoType_Fails()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["members"]);

        Assert.AreEqual(1, exit);
        Assert.IsNull(_fake.LastMembersType);
    }

    [TestMethod]
    public async Task Members_FilterOption_FlowsToService()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["members", "NavigationView", "--filter", "background"]);

        Assert.AreEqual(0, exit);
        Assert.AreEqual("background", _fake.LastMembersFilter);
    }

    [TestMethod]
    public async Task Members_WithoutFilterOption_PassesNull()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["members", "NavigationView"]);

        Assert.AreEqual(0, exit);
        Assert.IsNull(_fake.LastMembersFilter);
    }

    [TestMethod]
    public async Task Enums_FilterOption_FlowsToService()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["enums", "Symbol", "--filter", "folder"]);

        Assert.AreEqual(0, exit);
        Assert.AreEqual("folder", _fake.LastEnumsFilter);
    }

    [TestMethod]
    public async Task CheckProperty_Present_ExitsZero()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["check-property", "Button", "Background"]);

        Assert.AreEqual(0, exit);
        Assert.AreEqual("Button", _fake.LastCheckType);
        Assert.AreEqual("Background", _fake.LastCheckProperty);
    }

    [TestMethod]
    public async Task CheckProperty_Missing_ExitsOne()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["check-property", "Button", FakeApiMetadataService.MissingProperty]);

        Assert.AreEqual(1, exit);
    }

    [TestMethod]
    public async Task Search_Json_EmitsQueryField()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["NavigationView", "--json"]);

        Assert.AreEqual(0, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "\"query\"");
        StringAssert.Contains(TestAnsiConsole.Output, "NavigationView");
    }

    [TestMethod]
    public async Task ProjectOption_FlowsIntoScope()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["NavigationView", "--project", "MyApp"]);

        Assert.AreEqual(0, exit);
        Assert.AreEqual("MyApp", _fake.LastScope.Project);
    }

    [DataRow(new[] { "NavigationView", "--project", "MyApp", "--project-dir", "C:\\other" })]
    [DataRow(new[] { "members", "Button", "--project", "sdk", "--project-dir", "C:\\other" })]
    [DataRow(new[] { "refresh", "--project", "MyApp", "--project-dir", "C:\\other" })]
    [TestMethod]
    public async Task ScopeOptionsTogether_AreRejected(string[] args)
    {
        // The two options name the scope in different ways, so combining them leaves one
        // silently ignored and the answer coming from a scope the caller did not ask for —
        // which for find-api means a confident result about the wrong project.
        ParseResult parsed = Command.Parse(args);
        Assert.IsTrue(
            parsed.Errors.Any(e => e.Message.Contains("cannot be combined", StringComparison.Ordinal)),
            "the conflict must be reported, not resolved silently");

        int exit = await ParseAndInvokeWithCaptureAsync(Command, args);

        Assert.AreNotEqual(0, exit, "combining the scope options must not succeed");
    }

    [TestMethod]
    public async Task Packages_PartiallyIndexedPackage_ReportsItsContentsNotCacheMissing()
    {
        // A package whose status is 'incomplete' still has usable types. It used to fall
        // through to the '(cache missing)' arm, which tells the caller to re-index
        // something that is already there and hides what it does contain.
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["packages"]);

        Assert.AreEqual(0, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "7 types, 42 members");
        StringAssert.Contains(TestAnsiConsole.Output, "partial");
        Assert.IsFalse(
            TestAnsiConsole.Output.Contains("cache missing", StringComparison.Ordinal),
            "an indexed-but-partial package is not a missing cache");
    }

    [TestMethod]
    public async Task Refresh_ForcesFullRebuild()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["refresh"]);

        Assert.AreEqual(0, exit);
        Assert.IsTrue(_fake.LastRefreshForce);
    }

    [TestMethod]
    public async Task Refresh_ProjectOption_FlowsIntoScope()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["refresh", "--project", "MyApp"]);

        Assert.AreEqual(0, exit);
        Assert.AreEqual("MyApp", _fake.LastScope.Project);
    }

    // ---- batching ----
    // A single subject must keep the exact pre-batch payload shape (back-compat);
    // two or more subjects fan out to one service call each and are wrapped in an
    // envelope. A batch may only exit 0 when every subject resolved and was found,
    // otherwise batching would silently hide a miss.

    [TestMethod]
    public async Task Search_Batch_QueriesEverySubject()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["NavigationView", "TeachingTip", "InfoBar"]);

        Assert.AreEqual(0, exit);
        CollectionAssert.AreEqual(ThreeSearchSubjects, _fake.SearchQueries);
    }

    [TestMethod]
    public async Task Search_Batch_Json_WrapsResultsInEnvelope()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["NavigationView", "TeachingTip", "--json"]);

        Assert.AreEqual(0, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "\"count\"");
        StringAssert.Contains(TestAnsiConsole.Output, "\"results\"");
    }

    [TestMethod]
    public async Task Search_SingleSubject_Json_KeepsFlatPayload()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["NavigationView", "--json"]);

        Assert.AreEqual(0, exit);
        Assert.IsFalse(TestAnsiConsole.Output.Contains("\"results\":["), "single-subject payload must not be wrapped in a batch envelope");
    }

    [TestMethod]
    public async Task Search_Batch_ExitsOne_WhenAnySubjectHasNoHits()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["NavigationView", FakeApiMetadataService.EmptyQuery]);

        Assert.AreEqual(1, exit);
        Assert.AreEqual(2, _fake.SearchQueries.Count);
    }

    [TestMethod]
    public async Task Members_Batch_QueriesEveryType()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["members", "InfoBar", "TeachingTip"]);

        Assert.AreEqual(0, exit);
        CollectionAssert.AreEqual(TwoMemberTypes, _fake.MembersTypes);
    }

    [TestMethod]
    public async Task Members_Batch_AppliesFilterToEverySubject()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["members", "InfoBar", "TeachingTip", "--filter", "color"]);

        Assert.AreEqual(0, exit);
        Assert.AreEqual(2, _fake.MembersTypes.Count);
        Assert.AreEqual("color", _fake.LastMembersFilter);
    }

    [TestMethod]
    public async Task Members_Batch_ExitsOne_WhenAnyTypeIsMissing()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["members", "InfoBar", FakeApiMetadataService.MissingType]);

        Assert.AreEqual(1, exit);
        Assert.AreEqual(2, _fake.MembersTypes.Count);
        StringAssert.Contains(TestAnsiConsole.Output, "Color");
    }

    [TestMethod]
    public async Task Enums_Batch_QueriesEveryType()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["enums", "Symbol", "Visibility"]);

        Assert.AreEqual(0, exit);
        CollectionAssert.AreEqual(TwoEnumTypes, _fake.EnumsTypes);
    }

    [TestMethod]
    public async Task CheckProperty_Batch_ChecksEveryPropertyOnTheType()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["check-property", "InfoBar", "Severity", "IsOpen", "Message"]);

        Assert.AreEqual(0, exit);
        Assert.AreEqual("InfoBar", _fake.LastCheckType);
        CollectionAssert.AreEqual(ThreeCheckedProperties, _fake.CheckedProperties);
    }

    [TestMethod]
    public async Task CheckProperty_Batch_ReadsTheIndexOnceForTheWholeBatch()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["check-property", "InfoBar", "Severity", "IsOpen", "Message"]);

        Assert.AreEqual(0, exit);
        Assert.AreEqual(3, _fake.CheckedProperties.Count);
        // One batch call, not one per property: the per-call cost is loading the index,
        // which does not depend on which property is asked about.
        Assert.AreEqual(1, _fake.CheckPropertiesCallCount);
    }

    [TestMethod]
    public async Task CheckProperty_Batch_ExitsOne_WhenAnyPropertyIsMissing()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(
            Command,
            ["check-property", "InfoBar", "Severity", FakeApiMetadataService.MissingProperty, "IsOpen"]);

        Assert.AreEqual(1, exit);
        Assert.AreEqual(3, _fake.CheckedProperties.Count);
    }

    [TestMethod]
    public async Task CheckProperty_Batch_Json_ReportsMissingCount()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(
            Command,
            ["check-property", "InfoBar", "Severity", FakeApiMetadataService.MissingProperty, "--json"]);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "\"missingCount\"");
        StringAssert.Contains(TestAnsiConsole.Output, "\"count\"");
    }

    [TestMethod]
    public async Task CheckProperty_AttachedReadOnly_SaysItCannotBeAssigned()
    {
        // A second property forces the batch path, whose compact confirmation is what a
        // caller normally sees. Dropping the read-only status there is the difference
        // between "yes, use it" and markup that will not load.
        int exit = await ParseAndInvokeWithCaptureAsync(
            Command, ["check-property", "Gadget", FakeApiMetadataService.AttachedReadOnlyProperty, "Severity"]);

        Assert.AreEqual(0, exit, "a read-only property still exists");
        StringAssert.Contains(TestAnsiConsole.Output, "read-only");
    }

    [TestMethod]
    public async Task CheckProperty_AttachedReadOnly_Single_DoesNotGiveItAPlainTick()
    {
        // The detail view spelled out "(read-only)" in prose but still led with the same
        // green tick a settable property gets, which is the part a reader skims.
        int exit = await ParseAndInvokeWithCaptureAsync(
            Command, ["check-property", "Gadget", FakeApiMetadataService.AttachedReadOnlyProperty]);

        Assert.AreEqual(0, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "\u26a0");
        Assert.IsFalse(
            TestAnsiConsole.Output.Contains('\u2705'),
            "a property that cannot be assigned must not be confirmed like one that can");
    }

    [TestMethod]
    public async Task CheckProperty_AttachedReadOnly_Json_CarriesWritableFalse()
    {
        // --json is what an agent reads, and it is the only place the answer can be acted
        // on without parsing prose.
        int exit = await ParseAndInvokeWithCaptureAsync(
            Command, ["check-property", "Gadget", FakeApiMetadataService.AttachedReadOnlyProperty, "--json"]);

        Assert.AreEqual(0, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "\"writable\": false");
    }

    [TestMethod]
    public async Task CheckProperty_SingleProperty_KeepsOriginalBehaviour()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["check-property", "InfoBar", "Severity"]);

        Assert.AreEqual(0, exit);
        Assert.AreEqual("InfoBar", _fake.LastCheckType);
        Assert.AreEqual("Severity", _fake.LastCheckProperty);
        Assert.AreEqual(1, _fake.CheckedProperties.Count);
    }

    [TestMethod]
    public async Task CheckProperty_NoProperty_Fails()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["check-property", "InfoBar"]);

        Assert.AreEqual(1, exit);
        Assert.AreEqual(0, _fake.CheckedProperties.Count);
    }

    [TestMethod]
    public async Task HiddenVerbs_AreNotAdvertised()
    {
        var hidden = Command.Subcommands
            .Where(c => c.Hidden)
            .Select(c => c.Name)
            .ToList();

        CollectionAssert.Contains(hidden, "types");
        CollectionAssert.Contains(hidden, "namespaces");
        CollectionAssert.Contains(hidden, "projects");
    }

    [TestMethod]
    public async Task HiddenVerbs_StillWork_WhenInvokedExplicitly()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["types", "Test.Ns"]);

        Assert.AreEqual(0, exit);
    }

    // ---- cache-path noise is verbose-only ----

    [TestMethod]
    public async Task Search_DefaultVerbosity_OmitsCacheFilePath()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["NavigationView"]);

        Assert.AreEqual(0, exit);
        // The on-disk cache path is a debugging aid. At default verbosity it was up to
        // ~40% of the response, crowding out the API facts the caller asked for.
        StringAssert.Contains(TestAnsiConsole.Output, "Test.Ns");
        Assert.IsFalse(
            TestAnsiConsole.Output.Contains(FakeApiMetadataService.CacheFilePath, StringComparison.Ordinal),
            "search output must not print the internal cache path at default verbosity");
    }

    [TestMethod]
    public async Task Search_Verbose_ShowsCacheFilePath()
    {
        int exit = await ParseAndInvokeWithCaptureAsync(Command, ["NavigationView", "--verbose"]);

        Assert.AreEqual(0, exit);
        StringAssert.Contains(TestAnsiConsole.Output, FakeApiMetadataService.CacheFilePath);
    }
}
