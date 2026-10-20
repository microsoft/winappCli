using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed class CopilotAdvisorTests
{
    private string directory = null!;
    private static readonly RepairProposal Valid = new(
        "MainWindow.xaml", new string('a', 64), "SaveButton", "Save document",
        "Give the Save icon a meaningful accessible name.");

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(AppContext.BaseDirectory, "advisor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(directory, recursive: true);

    [TestMethod]
    public void ReadsOneReviewedProposalWithoutCallingAi()
    {
        var json = JsonSerializer.Serialize(Valid, Json.Options);
        Assert.AreEqual(Valid, Read(" \n" + json + "\n "));
    }

    [TestMethod]
    [DataRow("markdown")]
    [DataRow("extra")]
    [DataRow("missing")]
    [DataRow("duplicate")]
    [DataRow("wrongCase")]
    [DataRow("null")]
    [DataRow("wrongType")]
    [DataRow("twoObjects")]
    [DataRow("array")]
    [DataRow("trailingComma")]
    [DataRow("comment")]
    public void RejectsAnythingOtherThanTheExactProposalObject(string mutation)
    {
        var json = JsonSerializer.Serialize(Valid, Json.Options);
        json = mutation switch
        {
            "markdown" => "```json\n" + json + "\n```",
            "extra" => json.Replace("\"relativePath\":", "\"confidence\": 1,\n\"relativePath\":", StringComparison.Ordinal),
            "missing" => json.Replace("\"relativePath\": \"MainWindow.xaml\",", "", StringComparison.Ordinal),
            "duplicate" => json.Replace("\"relativePath\":", "\"relativePath\":\"Other.xaml\", \"relativePath\":", StringComparison.Ordinal),
            "wrongCase" => json.Replace("\"relativePath\"", "\"RelativePath\"", StringComparison.Ordinal),
            "null" => json.Replace("\"Save document\"", "null", StringComparison.Ordinal),
            "wrongType" => json.Replace("\"Save document\"", "42", StringComparison.Ordinal),
            "twoObjects" => json + json,
            "array" => "[" + json + "]",
            "trailingComma" => json[..json.LastIndexOf('}')] + ",}",
            "comment" => "/* untrusted */" + json,
            _ => throw new InvalidOperationException()
        };
        Assert.ThrowsExactly<InvalidDataException>(() => Read(json));
    }

    [TestMethod]
    public void RejectsEmptyFieldsInvalidHashAndTraversal()
    {
        foreach (var invalid in new[]
        {
            Valid with { RelativePath = @"..\Secret.xaml" },
            Valid with { SourceSha256 = "not-a-hash" },
            Valid with { AutomationId = "" },
            Valid with { AccessibleName = "{Binding Secret}" },
            Valid with { Explanation = " " }
        })
            Assert.ThrowsExactly<InvalidDataException>(() => Read(JsonSerializer.Serialize(invalid, Json.Options)));
    }

    [TestMethod]
    public void RejectsOversizedResponseBeforeParsing()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => Read(new string(' ', 16 * 1024 + 1)));
    }

    [TestMethod]
    public async Task AmbiguityIsRejectedBeforeAnyAiProcessOrEvidenceWrites()
    {
        var candidate = new SourceCandidate("MainWindow.xaml", "unused", 1, "Button", "SaveButton", Valid.SourceSha256, "<Button />");
        var scenario = new Scenario
        {
            Name = "save", Project = "Demo.csproj", TargetId = "SaveButton", ExpectedName = "Save document",
            AnchorId = "TitleInput", ResultId = "Status", InputText = "AccessLoop", ExpectedResult = "Saved"
        };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => CopilotAdvisor.ProposeAsync(
            "must-not-execute", [candidate, candidate], scenario,
            new Observation { Phase = "before", Status = "failed" },
            Path.Combine(directory, "must-not-create"), CancellationToken.None));
        Assert.IsFalse(Directory.Exists(Path.Combine(directory, "must-not-create")));
    }

    [TestMethod]
    public async Task NonAiCliFailurePreservesStderrAndExplainsTheFailure()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("This process diagnostic test uses the Windows whoami command, never Copilot.");
        var candidate = new SourceCandidate("MainWindow.xaml", "must-not-send-full-path", 1, "Button",
            "SaveButton", Valid.SourceSha256, "<Button />");
        var scenario = new Scenario
        {
            Name = "save", Project = "Demo.csproj", TargetId = "SaveButton", ExpectedName = "Save document",
            AnchorId = "TitleInput", ResultId = "Status", InputText = "AccessLoop", ExpectedResult = "Saved"
        };
        var before = new Observation
        {
            Phase = "before",
            Status = "failed",
            Scan = ScanFixture.Create(ScanFixture.MissingName()),
            Checks =
            [
                new("accessible-name", false, scenario.ExpectedName, ""),
                new("target-enabled", true, "True", "True"),
                new("target-visible", true, "False", "False"),
                new("keyboard-reachable", true, scenario.TargetId, scenario.TargetId),
                new("keyboard-activation", true, scenario.ExpectedResult, scenario.ExpectedResult)
            ]
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => CopilotAdvisor.ProposeAsync(
            Path.Combine(Environment.SystemDirectory, "whoami.exe"), [candidate], scenario,
            before, directory, cancellation.Token));
        var stderr = File.ReadAllText(Path.Combine(directory, "advisor-stderr.txt"));
        Assert.IsFalse(string.IsNullOrWhiteSpace(stderr));
        StringAssert.Contains(error.Message, "exited with code");
        StringAssert.Contains(error.Message, "advisor-stderr.txt");
        Assert.IsTrue(error.Message.Contains(new string(stderr.Trim().Take(600)
            .Select(character => char.IsControl(character) ? ' ' : character).ToArray()), StringComparison.Ordinal));
        Assert.IsTrue(File.Exists(Path.Combine(directory, "advisor-events.jsonl")));
        Assert.IsFalse(File.ReadAllText(Path.Combine(directory, "advisor-prompt.txt"))
            .Contains(candidate.FullPath, StringComparison.Ordinal));
        Assert.HasCount(0, Directory.GetDirectories(directory, "advisor-workspace-*"));
    }

    [TestMethod]
    public void JsonEventsPreserveLongHashesAndEscapedQuotes()
    {
        var proposal = Valid with { Explanation = "Name the action \"Save document\"." };
        var content = JsonSerializer.Serialize(proposal, Json.Options);
        var events = JsonSerializer.Serialize(new { type = "assistant.message", data = new { content, toolRequests = Array.Empty<object>() } })
            + "\n" + JsonSerializer.Serialize(new { type = "result", exitCode = 0 }) + "\n";
        Assert.AreEqual(proposal, Read(CopilotAdvisor.ExtractResponse(events)));
    }

    [TestMethod]
    [DataRow("missing-result")]
    [DataRow("unsuccessful-result")]
    [DataRow("multiple-messages")]
    [DataRow("tools")]
    [DataRow("text-output")]
    public void IncompleteOrUnsafeAdvisorEventsAreRejected(string kind)
    {
        var message = JsonSerializer.Serialize(new
        {
            type = "assistant.message",
            data = new { content = JsonSerializer.Serialize(Valid, Json.Options), toolRequests = Array.Empty<object>() }
        }) + "\n";
        var result = """{"type":"result","exitCode":0}""";
        var events = kind switch
        {
            "missing-result" => message,
            "unsuccessful-result" => message + """{"type":"result","exitCode":1}""",
            "multiple-messages" => message + message + result,
            "tools" => """{"type":"tool.execution_start","data":{}}""" + "\n" + message + result,
            "text-output" => JsonSerializer.Serialize(Valid, Json.Options),
            _ => throw new InvalidOperationException()
        };
        Assert.ThrowsExactly<InvalidDataException>(() => CopilotAdvisor.ExtractResponse(events));
    }

    private RepairProposal Read(string content)
    {
        var path = Path.Combine(directory, "proposal.json");
        File.WriteAllText(path, content);
        return CopilotAdvisor.ReadProposal(path);
    }
}
