using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed class LoopInputTests
{
    private string root = null!;

    [TestInitialize]
    public void Initialize()
    {
        root = Directory.CreateTempSubdirectory("accessloop-preset-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "Demo"));
        File.WriteAllText(Path.Combine(root, "Demo", "Demo.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root, "Demo", "MainWindow.xaml"),
            "<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Button AutomationProperties.AutomationId=\"Broken\" /></Page>");
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(root, recursive: true);

    internal static LoopProfile Valid() => new()
    {
        Name = "Preset loop", Project = Path.Combine("Demo", "Demo.csproj"), ProcessName = "Demo",
        Pages = [new("basics", "Basics", "NavBasics", "BasicsReady")],
        Steps =
        [
            new() { Id = "enter", PageId = "basics", Name = "Enter", Kind = "navigate", TargetId = "NavBasics", ControlType = "TabItem" },
            new() { Id = "base", PageId = "basics", Name = "Base", Kind = "scan", RequiredIds = ["Broken"] }
        ],
        RepairTargets = [new("Broken", "Save", "basics", "base")]
    };

    [TestMethod]
    public void PresetIsStrictAndKeepsTheOriginalInputUnchanged()
    {
        var file = Path.Combine(root, "loop.json");
        File.WriteAllText(file, JsonSerializer.Serialize(Valid(), Json.Options));
        var loaded = LoopInput.Read(file);
        Assert.AreEqual(Path.Combine(root, "Demo", "Demo.csproj"), loaded.Profile.Project);
        loaded.EnsureUnchanged();
        File.AppendAllText(file, " ");
        Assert.ThrowsExactly<InvalidDataException>(loaded.EnsureUnchanged);
    }

    [TestMethod]
    public void AcceptanceFingerprintIsIndependentOfTheCopiedWorkspaceLocation()
    {
        var first = Valid() with { Project = Path.Combine(root, "first", "Demo.csproj") };
        var second = first with { Project = Path.Combine(root, "second", "Demo.csproj") };
        Assert.AreEqual(LoopInput.PlanHash(first), LoopInput.PlanHash(second));
        Assert.AreNotEqual(LoopInput.PlanHash(first), LoopInput.PlanHash(first with
        {
            RepairTargets = [new("Broken", "A different expected name", "basics", "base")]
        }));
    }

    [TestMethod]
    public void DuplicateAndUnknownInputFieldsAreRejected()
    {
        var json = JsonSerializer.Serialize(Valid(), Json.Options);
        var duplicate = json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"schemaVersion\": 1", StringComparison.Ordinal);
        var unknown = json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"clickEverything\": true", StringComparison.Ordinal);
        Assert.ThrowsExactly<InvalidDataException>(() => LoopInput.ReadProfile(System.Text.Encoding.UTF8.GetBytes(duplicate)));
        Assert.ThrowsExactly<InvalidDataException>(() => LoopInput.ReadProfile(System.Text.Encoding.UTF8.GetBytes(unknown)));
    }

    [TestMethod]
    [DataRow("missing-navigation")]
    [DataRow("undeclared-page")]
    [DataRow("duplicate-step")]
    [DataRow("unbounded-scroll")]
    [DataRow("system-hotkey")]
    [DataRow("arbitrary-command")]
    [DataRow("missing-checkpoint-target")]
    [DataRow("empty-assertion")]
    [DataRow("wrong-native-boolean")]
    [DataRow("wrong-selection-shape")]
    public void UnboundedOrAmbiguousRoutesAreRejected(string change)
    {
        var original = Valid();
        var navigation = original.Steps[0];
        var scan = original.Steps[1];
        var extra = new LoopStep { Id = "input", PageId = "basics", Name = "Input", Kind = "key", TargetId = "Broken", ControlType = "Button", Keys = "Tab" };
        var profile = change switch
        {
            "missing-navigation" => original with { Steps = [scan] },
            "undeclared-page" => original with { Steps = [navigation, scan with { PageId = "unknown" }] },
            "duplicate-step" => original with { Steps = [navigation, navigation, scan] },
            "unbounded-scroll" => original with { Steps = [navigation, extra with { Kind = "scroll", Keys = null, VerticalPercent = double.NaN }, scan] },
            "system-hotkey" => original with { Steps = [navigation, extra with { Keys = "Alt+F4" }, scan] },
            "arbitrary-command" => original with { Steps = [navigation, extra with { Kind = "shell" }, scan] },
            "missing-checkpoint-target" => original with { RepairTargets = [new("Other", "Other", "basics", "base")] },
            "empty-assertion" => original with { Steps = [navigation, extra with { Kind = "assert", Keys = null, TargetId = null, ControlType = null }, scan] },
            "wrong-native-boolean" => original with
            {
                Steps = [navigation, scan with { Expectations = [new("Broken", "IsEnabled", JsonSerializer.SerializeToElement(true))] }]
            },
            "wrong-selection-shape" => original with
            {
                Steps = [navigation, scan with { Expectations = [new("Broken", "SelectionAutomationIds", JsonSerializer.SerializeToElement("Item"))] }]
            },
            _ => throw new AssertFailedException("Unknown test case.")
        };
        Assert.ThrowsExactly<InvalidDataException>(() => LoopInput.Validate(profile));
    }

    [TestMethod]
    public void SourceCopyAndRepairLeaveTheOriginalDefectiveFixtureUntouched()
    {
        var originalProject = Path.Combine(root, "Demo", "Demo.csproj");
        var originalFile = Path.Combine(root, "Demo", "MainWindow.xaml");
        var originalHash = AuditFiles.Hash(originalFile);
        var copy = LoopSourceWorkspace.Copy(originalProject, Path.Combine(root, "run", "workspace"));
        var archive = LoopSourceWorkspace.Copy(copy, Path.Combine(root, "run", "sources", "baseline"));
        var source = SourceRepair.Locate(copy, "Broken").Single();
        _ = SourceRepair.Apply(source, new(source.RelativePath, source.Sha256, "Broken", "Save", "Give the icon button its reviewed name."), "Save");
        Assert.AreEqual(originalHash, AuditFiles.Hash(originalFile));
        Assert.AreEqual(originalHash, AuditFiles.Hash(Path.Combine(Path.GetDirectoryName(archive)!, "MainWindow.xaml")));
        Assert.AreNotEqual(originalHash, AuditFiles.Hash(source.FullPath));
    }

    [TestMethod]
    public void SourceCopiesCannotRecurseIntoTheOriginalProject()
    {
        var project = Path.Combine(root, "Demo", "Demo.csproj");
        Assert.ThrowsExactly<InvalidDataException>(() => LoopSourceWorkspace.Copy(project, Path.Combine(root, "Demo", "copy")));
    }

    [TestMethod]
    public void ScopedOptionsRequireInputApprovalAndKeepReplaySeparateFromRepair()
    {
        Assert.ThrowsExactly<ArgumentException>(() => LoopOptions.Parse(["loop", "preset.json", "--output", "new"]));
        Assert.ThrowsExactly<ArgumentException>(() => LoopOptions.Parse(["loop", "preset.json", "--replay", "old", "--accept-actions", "--output", "new"]));
        Assert.ThrowsExactly<ArgumentException>(() => LoopOptions.Parse(["loop", "--replay", "old", "--apply", "--accept-actions", "--output", "new"]));
        Assert.ThrowsExactly<ArgumentException>(() => LoopOptions.Parse(["loop", "preset.json", "--proposals", "reviewed", "--accept-actions", "--output", "new"]));
        var replay = LoopOptions.Parse(["loop", "--replay", "old", "--round", "after-01", "--accept-actions", "--output", "new"]);
        Assert.AreEqual("old", replay.Replay);
        Assert.AreEqual("after-01", replay.Round);
        Assert.IsNull(replay.Input);
    }

    [TestMethod]
    public void ShippedPresetUsesUniquelyMappedStaticButtonRepairTargets()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "loop-demo.json")))
            directory = directory.Parent;
        Assert.IsNotNull(directory, "The sample's shipped preset must be present.");
        var input = LoopInput.Read(Path.Combine(directory.FullName, "loop-demo.json"));
        foreach (var target in input.Profile.RepairTargets)
            Assert.AreEqual(1, SourceRepair.Locate(input.Profile.Project, target.AutomationId).Count, target.AutomationId);
        Assert.AreEqual(8, input.Profile.Steps.Count(step => step.Kind == "scan"));
        Assert.IsTrue(input.Profile.Steps.Any(step => step.Kind == "key" && step.Keys == "Shift+Tab"));
    }

    [TestMethod]
    public void EvidenceComparisonsDoNotTreatUnknownOrWrongTypedValuesAsSuccess()
    {
        var expected = JsonSerializer.SerializeToElement("True");
        Assert.IsFalse(LoopEvidence.MatchesExpected("IsEnabled", expected, null));
        Assert.IsFalse(LoopEvidence.MatchesExpected("IsEnabled", expected, JsonSerializer.SerializeToElement(true)));
        Assert.IsTrue(LoopEvidence.MatchesExpected("IsEnabled", expected, JsonSerializer.SerializeToElement("True")));
        Assert.IsTrue(LoopEvidence.MatchesExpected("ScrollVerticalPercent", JsonSerializer.SerializeToElement(50), JsonSerializer.SerializeToElement("50.05")));
        Assert.IsFalse(LoopEvidence.MatchesExpected("ScrollVerticalPercent", JsonSerializer.SerializeToElement(50), JsonSerializer.SerializeToElement("51")));
        Assert.IsFalse(LoopEvidence.MatchesExpected("ScrollVerticalPercent", JsonSerializer.SerializeToElement(50), JsonSerializer.SerializeToElement("NaN")));
        Assert.IsFalse(LoopEvidence.MatchesExpected("ScrollVerticalPercent", JsonSerializer.SerializeToElement("50"), JsonSerializer.SerializeToElement("50")));
    }
}
