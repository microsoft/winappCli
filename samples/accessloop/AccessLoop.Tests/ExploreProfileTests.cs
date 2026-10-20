using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed class ExploreProfileTests
{
    private string root = null!;

    [TestInitialize]
    public void Initialize() => root = Directory.CreateTempSubdirectory("accessloop-explore-profile-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(root, recursive: true);

    [TestMethod]
    public void StrictProfilePreservesOriginalBytesAndResolvesOwnedProjectLocally()
    {
        Directory.CreateDirectory(Path.Combine(root, "Demo"));
        File.WriteAllText(Path.Combine(root, "Demo", "Demo.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var file = Path.Combine(root, "explore.json");
        var profile = Valid() with { Project = @"Demo\Demo.csproj" };
        File.WriteAllText(file, JsonSerializer.Serialize(profile, Json.Options));
        var bytes = File.ReadAllBytes(file);

        var loaded = ExploreProfileFile.Read(file);

        CollectionAssert.AreEqual(bytes, loaded.Bytes);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(file));
        Assert.AreEqual(AuditFiles.Hash(file), loaded.Sha256);
        Assert.AreEqual(Path.Combine(root, "Demo", "Demo.csproj"), loaded.ProjectPath);
        Assert.AreEqual("basics", loaded.Profile.InitialPageId);
    }

    [TestMethod]
    public void AttachedProfileDoesNotRequireSource()
    {
        var file = Path.Combine(root, "explore.json");
        File.WriteAllText(file, JsonSerializer.Serialize(Valid(), Json.Options));
        Assert.IsNull(ExploreProfileFile.Read(file).ProjectPath);
    }

    [TestMethod]
    [DataRow(@"..\outside.csproj")]
    [DataRow(@"C:\outside.csproj")]
    [DataRow(@"Demo\not-project.txt")]
    public void OwnedProjectCannotEscapeProfileOrBecomeAnArbitraryCommand(string project)
    {
        Directory.CreateDirectory(Path.Combine(root, "Demo"));
        File.WriteAllText(Path.Combine(root, "Demo", "not-project.txt"), "not a project");
        var file = Path.Combine(root, "explore.json");
        File.WriteAllText(file, JsonSerializer.Serialize(Valid() with { Project = project }, Json.Options));
        Assert.ThrowsExactly<InvalidDataException>(() => ExploreProfileFile.Read(file));
    }

    [TestMethod]
    public void DuplicateOrUnknownJsonMembersAreRejected()
    {
        var file = Path.Combine(root, "explore.json");
        var json = JsonSerializer.Serialize(Valid(), Json.Options);
        File.WriteAllText(file, json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"schemaVersion\": 1", StringComparison.Ordinal));
        Assert.ThrowsExactly<InvalidDataException>(() => ExploreProfileFile.Read(file));
        File.WriteAllText(file, json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"clickEverything\": true", StringComparison.Ordinal));
        Assert.ThrowsExactly<InvalidDataException>(() => ExploreProfileFile.Read(file));
    }

    [TestMethod]
    [DataRow("duplicate-page")]
    [DataRow("shared-role")]
    [DataRow("invalid-id")]
    [DataRow("wrong-initial")]
    [DataRow("empty-options")]
    [DataRow("duplicate-option")]
    [DataRow("unknown-dialog-scope")]
    [DataRow("unbounded-scroll")]
    [DataRow("nan-overlap")]
    [DataRow("too-few-states")]
    [DataRow("bad-probe-page")]
    [DataRow("probe-reuses-action")]
    [DataRow("too-long-run")]
    public void AmbiguousOrUnboundedInteractionPoliciesAreRejected(string kind)
    {
        var profile = Valid();
        var first = profile.Pages[0];
        profile = kind switch
        {
            "duplicate-page" => profile with { Pages = [first, first] },
            "shared-role" => profile with { Pages = [first with { ReadyId = first.NavigationId }] },
            "invalid-id" => profile with { Pages = [first with { NavigationId = "*Button" }] },
            "wrong-initial" => profile with { InitialPageId = "missing" },
            "empty-options" => profile with { Pages = [first with { Dropdowns = [new() { ControlId = "Options" }] }] },
            "duplicate-option" => profile with { Pages = [first with { Dropdowns = [new() { ControlId = "Options", ItemIds = ["Item", "Item"] }] }] },
            "unknown-dialog-scope" => profile with { Pages = [first with { Dialogs = [new() { OpenerId = "Open", RootId = "Dialog", DismissId = "Close", Scope = "any-window" }] }] },
            "unbounded-scroll" => profile with { Pages = [first with { Scroll = Scroll() with { MaxPositions = 1000 } }] },
            "nan-overlap" => profile with { Pages = [first with { Scroll = Scroll() with { OverlapFraction = double.NaN } }] },
            "too-few-states" => profile with { MaxStates = 1, Pages = [first with { Scroll = Scroll() }] },
            "bad-probe-page" => profile with { ScopeProbe = new("missing", "Probe", "Probe window") },
            "probe-reuses-action" => profile with { ScopeProbe = new(first.Id, first.NavigationId, "Probe window") },
            "too-long-run" => profile with { MaxDurationSeconds = 3600 },
            _ => throw new AssertFailedException("Unknown case.")
        };
        Assert.ThrowsExactly<InvalidDataException>(() => ExploreProfileFile.Validate(profile));
    }

    [TestMethod]
    public void OwnedLaunchAndScopeProbeRequireExplicitInteractionApproval()
    {
        var options = ExploreOptionsParser.Parse(
            ["explore", "profile.json", "--launch-owned", "--accept-interactions", "--output", "new-run", "--probe-scope-guard", "--json"]);
        Assert.IsTrue(options.LaunchOwned);
        Assert.IsTrue(options.AcceptInteractions);
        Assert.IsTrue(options.ProbeScopeGuard);
        Assert.IsTrue(options.Json);
        Assert.IsNull(options.ProcessId);
        Assert.IsNull(options.WindowHandle);
    }

    [TestMethod]
    public void ExistingWindowModeDoesNotAuthorizeAppLifecycle()
    {
        var options = ExploreOptionsParser.Parse(
            ["explore", "profile.json", "--window", "123", "--accept-interactions", "--output", "new-run", "--winapp", "native.exe"]);
        Assert.IsFalse(options.LaunchOwned);
        Assert.AreEqual(123L, options.WindowHandle);
        Assert.IsFalse(options.ProbeScopeGuard);
        Assert.AreEqual("native.exe", options.Winapp);
    }

    [TestMethod]
    [DataRow("missing-approval")]
    [DataRow("multiple-targets")]
    [DataRow("no-target")]
    [DataRow("no-output")]
    [DataRow("probe-existing")]
    [DataRow("mutation-option")]
    [DataRow("duplicate-option")]
    [DataRow("invalid-hwnd")]
    [DataRow("invalid-pid")]
    public void InvalidOrUnapprovedOptionsAreRejected(string kind)
    {
        var args = new List<string> { "explore", "profile.json", "--accept-interactions", "--output", "new-run", "--window", "123" };
        switch (kind)
        {
            case "missing-approval": args.Remove("--accept-interactions"); break;
            case "multiple-targets": args.Add("--launch-owned"); break;
            case "no-target": args.RemoveRange(args.Count - 2, 2); break;
            case "no-output": args.RemoveRange(3, 2); break;
            case "probe-existing": args.Add("--probe-scope-guard"); break;
            case "mutation-option": args.Add("--select-all"); break;
            case "duplicate-option": args.Add("--accept-interactions"); break;
            case "invalid-hwnd": args[^1] = "-1"; break;
            case "invalid-pid": args[^2] = "--pid"; args[^1] = "99999999999999999"; break;
            default: Assert.Fail("Unknown case."); break;
        }
        Assert.ThrowsExactly<ArgumentException>(() => ExploreOptionsParser.Parse(args));
    }

    private static ExploreProfile Valid() => new()
    {
        Name = "Profile fixture", ProcessName = "Fixture", NavigationRootId = "Navigation", InitialPageId = "basics",
        Pages = [new() { Id = "basics", Name = "Basics", NavigationId = "NavBasics", ReadyId = "BasicsReady" }]
    };

    private static ExploreScroll Scroll() => new()
    {
        ContainerId = "Scroller", TopAnchorId = "Top", BottomAnchorId = "Bottom"
    };
}
