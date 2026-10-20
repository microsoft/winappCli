using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed class LoopRunnerLogicTests
{
    private static LoopVerifiedRound Round(bool firstFixed = false, bool secondFixed = false) => new(
        new() { Id = "round", Directory = "unused", Status = "completed", Sources = [new("MainWindow.xaml", new('A', 64))] },
        [],
        new[] { ("First", firstFixed), ("Second", secondFixed) }.Where(item => !item.Item2).Select(item =>
            new LoopFindingGroup(item.Item1 + ":NameNotNull", "NameNotNull", item.Item1, "Missing accessible name", true,
                [new("base", "basics", "unused", 0, item.Item1, "Button", null, "NameNotNull", "Missing accessible name")])).ToArray(),
        [new("First", "First action", firstFixed ? "First action" : null, firstFixed, "base"),
            new("Second", "Second action", secondFixed ? "Second action" : null, secondFixed, "base")],
        "partial");

    [TestMethod]
    public void ReproductionNeedsTheSameSourceAndFindingsNotJustACompletedActionLog()
    {
        LoopRunner.RequireEquivalent(Round(), Round());
        Assert.ThrowsExactly<InvalidDataException>(() => LoopRunner.RequireEquivalent(Round(), Round(firstFixed: true)));
        var changed = Round();
        changed.Round.Sources = [new("MainWindow.xaml", new('B', 64))];
        Assert.ThrowsExactly<InvalidDataException>(() => LoopRunner.RequireEquivalent(Round(), changed));
    }

    [TestMethod]
    public void UnmappedRepeatedRuleNamesDoNotProveTheSameIssueWasReproduced()
    {
        var before = Round();
        var unknown = before with { Findings = before.Findings.Select(item => item with { MappedTarget = false }).ToArray() };
        Assert.ThrowsExactly<InvalidDataException>(() => LoopRunner.RequireEquivalent(unknown, unknown));
    }

    [TestMethod]
    public void ARepairMustFixItsTargetWithoutChangingOtherNameContracts()
    {
        LoopRunner.RequireImprovement(Round(), Round(firstFixed: true), "First");
        Assert.ThrowsExactly<InvalidDataException>(() => LoopRunner.RequireImprovement(Round(), Round(), "First"));
        Assert.ThrowsExactly<InvalidDataException>(() => LoopRunner.RequireImprovement(Round(), Round(firstFixed: true, secondFixed: true), "First"));
    }

    [TestMethod]
    public void ANewOccurrenceIsARegressionEvenWhenItsRuleAndControlAlreadyExisted()
    {
        var after = Round(firstFixed: true);
        var existing = after.Findings.Single();
        after = after with { Findings = [existing with { Occurrences = [.. existing.Occurrences, existing.Occurrences[0]] }] };
        Assert.ThrowsExactly<InvalidDataException>(() => LoopRunner.RequireImprovement(Round(), after, "First"));
    }
}
