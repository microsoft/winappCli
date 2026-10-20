using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed class SuiteInputTests
{
    private string directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        directory = Directory.CreateTempSubdirectory("accessloop-suite-").FullName;
        File.WriteAllText(Path.Combine(directory, "Demo.csproj"), "<Project />");
        WriteCase("first.json", "first");
        WriteCase("second.json", "second");
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(directory, true);

    [TestMethod]
    public void SingleScenarioIsAValidOneCaseSuite()
    {
        var input = SuiteInput.Load(Path.Combine(directory, "first.json"));
        Assert.AreEqual("first", input.Name);
        Assert.HasCount(1, input.Cases);
        Assert.AreEqual(Path.Combine(directory, "Demo.csproj"), input.Cases[0].Scenario.Project);
    }

    [TestMethod]
    public void SuiteResolvesEachPathAndCapturesAllInputBytes()
    {
        var path = WriteSuite(["first.json", "second.json"]);
        var input = SuiteInput.Load(path);
        Assert.AreEqual("suite", input.Name);
        CollectionAssert.AreEqual(new[] { "first", "second" }, input.Cases.Select(c => c.Scenario.Name).ToArray());
        var output = Path.Combine(directory, "evidence");
        var snapshots = input.SaveSnapshots(output);
        Assert.HasCount(3, snapshots);
        foreach (var snapshot in snapshots)
            CollectionAssert.AreEqual(File.ReadAllBytes(snapshot.Path), File.ReadAllBytes(Path.Combine(output, snapshot.EvidencePath)));
        input.EnsureUnchanged();
        File.AppendAllText(Path.Combine(directory, "second.json"), "\n");
        Assert.ThrowsExactly<InvalidOperationException>(input.EnsureUnchanged);
    }

    [TestMethod]
    public void EditingSuiteItselfInvalidatesVerification()
    {
        var path = WriteSuite(["first.json"]);
        var input = SuiteInput.Load(path);
        File.AppendAllText(path, " ");
        Assert.ThrowsExactly<InvalidOperationException>(input.EnsureUnchanged);
    }

    [TestMethod]
    public void DuplicatePathsAndNamesAreRejected()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => SuiteInput.Load(WriteSuite(["first.json", ".\\first.json"])));
        WriteCase("second.json", "FIRST");
        Assert.ThrowsExactly<InvalidDataException>(() => SuiteInput.Load(WriteSuite(["first.json", "second.json"])));
    }

    [TestMethod]
    public void SuiteCannotBeEmptyUnboundedOrRecursive()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => SuiteInput.Load(WriteSuite([])));
        Assert.ThrowsExactly<InvalidDataException>(() => SuiteInput.Load(WriteSuite(Enumerable.Repeat("first.json", 21).ToArray())));
        Assert.ThrowsExactly<InvalidDataException>(() => SuiteInput.Load(WriteSuite(["suite.json"])));
    }

    [TestMethod]
    public void MalformedInputIsNotSilentlyInterpretedAsAScenario()
    {
        var path = WriteSuite(["first.json"]);
        File.WriteAllText(path, """{"name":"suite","scenarios":[],"scenarios":["first.json"]}""");
        Assert.ThrowsExactly<InvalidDataException>(() => SuiteInput.Load(path));
        File.WriteAllText(path, """{"name":"suite","scenarios":["first.json"],"unknown":true}""");
        Assert.ThrowsExactly<JsonException>(() => SuiteInput.Load(path));
    }

    [TestMethod]
    public void FindingSelectionAndExitCodesAreExplicit()
    {
        var options = WorkbenchOptions.Parse(["repair", "suite.json", "--finding", "AL-123", "--apply"]);
        Assert.AreEqual("AL-123", options.Finding);
        Assert.IsTrue(options.Apply);
        Assert.ThrowsExactly<ArgumentException>(() => WorkbenchOptions.Parse(["check", "suite.json", "--finding", "AL-123"]));
        Assert.ThrowsExactly<ArgumentException>(() => WorkbenchOptions.Parse(["repair", "suite.json", "--finding"]));
        Assert.ThrowsExactly<ArgumentException>(() => WorkbenchOptions.Parse(["repair", "suite.json", "--apply", "--apply"]));
        Assert.AreEqual(0, Program.ExitCode("fixed"));
        Assert.AreEqual(1, Program.ExitCode("regression"));
        Assert.AreEqual(2, Program.ExitCode("inconclusive"));
        Assert.AreEqual(3, Program.ExitCode("awaiting-selection"));
    }

    private void WriteCase(string path, string name) => Json.Write(Path.Combine(directory, path), new Scenario
    {
        Name = name, Project = "Demo.csproj", TargetId = "SaveButton", ExpectedName = "Save",
        AnchorId = "Input", ResultId = "Status", InputText = "test", ExpectedResult = "Saved"
    });

    private string WriteSuite(string[] paths)
    {
        var path = Path.Combine(directory, "suite.json");
        Json.Write(path, new { schemaVersion = 1, name = "suite", scenarios = paths });
        return path;
    }
}
