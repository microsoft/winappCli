// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using WinApp.Cli.Commands;
using WinApp.Cli.Services.Controls;
using WinApp.Cli.Services.Learn;

namespace WinApp.Cli.Tests;

[TestClass]
public class AskCommandTests : BaseCommandTests
{
    private FakeControlsSearchService _samples = null!;
    private readonly FakeLearnClient _learn = new();

    protected override IServiceCollection ConfigureServices(IServiceCollection services) => services
        .AddSingleton<IControlsSearchService>(_ => _samples)
        .AddSingleton<ILearnSearchClient>(_ => _learn);

    private AskCommand Command()
    {
        _samples ??= FakeControlsSearchService.WithEngine(new SearchEngine(
            [
                new Scenario
                {
                    Id = "tabview-1", ControlId = "tabview", ControlName = "TabView",
                    HeaderText = "Tabs [example]", Source = "gallery", Xaml = "<TabView />"
                }
            ], [], new(), new()));
        return GetRequiredService<AskCommand>();
    }

    [TestMethod]
    public async Task Search_Json_CombinesSeparateSamplesAndDocsEvenWithLocalMatch()
    {
        var command = Command();
        _samples.LoadedOrigin = CorpusOrigin.Cache;
        var exit = await ParseAndInvokeWithCaptureAsync(command, ["tabview", "--json"]);
        using var doc = JsonDocument.Parse(TestAnsiConsole.Output);
        var output = doc.RootElement;

        Assert.AreEqual(0, exit);
        Assert.AreEqual("tabview", output.GetProperty("query").GetString());
        Assert.AreEqual("cache", output.GetProperty("corpus").GetString());
        Assert.AreEqual(1, output.GetProperty("matches").GetArrayLength());
        var sample = output.GetProperty("matches")[0];
        Assert.AreEqual("TabView", sample.GetProperty("control").GetString());
        Assert.AreEqual("gallery-tabview-1", sample.GetProperty("scenarios")[0].GetProperty("id").GetString());
        Assert.AreEqual(1, output.GetProperty("docs").GetArrayLength());
        Assert.AreEqual("https://learn.microsoft.com/windows/apps/example",
            output.GetProperty("docs")[0].GetProperty("url").GetString());
        Assert.AreEqual("Short excerpt", output.GetProperty("docs")[0].GetProperty("excerpt").GetString());
        Assert.AreEqual("tabview", _learn.Question);
        Assert.AreEqual(1, _learn.Calls);
        Assert.IsFalse(_samples.LastIncludeReactor);
        Assert.IsFalse(_samples.LastForceRefresh);
        Assert.IsNull(_samples.LastOnFetchStarting);
        Assert.IsFalse(output.TryGetProperty("docsError", out _));
        Assert.IsFalse(output.TryGetProperty("samplesError", out _));
    }

    [TestMethod]
    public async Task Search_Text_ShowsSamplesBeforeRequestingDocsAndEscapesMarkup()
    {
        _learn.BeforeSearch = () => StringAssert.Contains(TestAnsiConsole.Output, "gallery-tabview-1");
        _learn.Results = [new LearnSearchResult("Docs [example]", new Uri("https://learn.microsoft.com/windows/apps/example"), "Short [excerpt]")];
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["tabview"]);
        var output = TestAnsiConsole.Output;

        Assert.AreEqual(0, exit);
        Assert.IsTrue(output.IndexOf("WinUI samples", StringComparison.Ordinal)
            < output.IndexOf("Docs [example]", StringComparison.Ordinal));
        StringAssert.Contains(output, "Tabs [example]");
        StringAssert.Contains(output, "Docs [example]");
        StringAssert.Contains(output, "Short [excerpt]");
        StringAssert.Contains(output, "winapp find-ui --id <id>");
        Assert.IsFalse(output.Contains("Reactor tabs", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Search_Text_NotesEmbeddedSamples()
    {
        var command = Command();
        _samples.LoadedOrigin = CorpusOrigin.Embedded;
        await ParseAndInvokeWithCaptureAsync(command, ["tabview"]);
        StringAssert.Contains(TestAnsiConsole.Output, "Using samples built into the CLI");
    }

    [TestMethod]
    public async Task Search_Json_LimitsSamplesToThreeGroupsAndThreeScenarios()
    {
        var scenarios = Enumerable.Range(0, 5).SelectMany(control => Enumerable.Range(0, 5).Select(scenario =>
            new Scenario
            {
                Id = $"tabview{control}-{scenario}", ControlId = $"tabview{control}", ControlName = "TabView",
                HeaderText = "TabView tabs", Source = "gallery", Xaml = "<TabView />"
            })).ToArray();
        _samples = FakeControlsSearchService.WithEngine(new SearchEngine(scenarios, [], new(), new()));
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["tabview", "--json"]);
        using var doc = JsonDocument.Parse(TestAnsiConsole.Output);
        Assert.AreEqual(0, exit);
        var matches = doc.RootElement.GetProperty("matches");
        Assert.AreEqual(3, matches.GetArrayLength());
        foreach (var match in matches.EnumerateArray())
        {
            Assert.AreEqual(3, match.GetProperty("scenarios").GetArrayLength());
        }
    }

    [TestMethod]
    [DataRow("timeout")]
    [DataRow("http")]
    [DataRow("mcp")]
    [DataRow("shape")]
    [DataRow("json")]
    [DataRow("client-timeout")]
    public async Task Search_Json_DocsFailureKeepsSamplesAndReportsReason(string failure)
    {
        _learn.Error = failure switch
        {
            "timeout" => new TimeoutException(),
            "http" => new HttpRequestException(),
            "mcp" => new McpException("Tool failed"),
            "shape" => new InvalidDataException(),
            "json" => new JsonException(),
            _ => new TaskCanceledException("HTTP timeout")
        };
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["tabview", "--json"]);
        using var doc = JsonDocument.Parse(TestAnsiConsole.Output);

        Assert.AreEqual(0, exit);
        Assert.AreEqual(1, doc.RootElement.GetProperty("matches").GetArrayLength());
        Assert.AreEqual(0, doc.RootElement.GetProperty("docs").GetArrayLength());
        StringAssert.Contains(doc.RootElement.GetProperty("docsError").GetString()!, "Docs unavailable");
    }

    [TestMethod]
    public async Task Search_Text_DocsTimeoutIsVisibleEvenWithQuiet()
    {
        _learn.Error = new TimeoutException();
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["tabview", "--quiet"]);
        Assert.AreEqual(0, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "gallery-tabview-1");
        StringAssert.Contains(TestAnsiConsole.Output, "Docs unavailable: Microsoft Learn timed out");
    }

    [TestMethod]
    public async Task Search_Json_DocsCanAnswerWhenThereAreNoLocalMatches()
    {
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["zzzz-unmatched", "--json"]);
        using var doc = JsonDocument.Parse(TestAnsiConsole.Output);
        Assert.AreEqual(0, exit);
        Assert.AreEqual(0, doc.RootElement.GetProperty("matches").GetArrayLength());
        Assert.AreEqual(1, doc.RootElement.GetProperty("docs").GetArrayLength());
    }

    [TestMethod]
    public async Task Search_Json_SamplesFailureStillReturnsDocs()
    {
        _samples = FakeControlsSearchService.Unavailable();
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["tabview", "--json"]);
        using var doc = JsonDocument.Parse(TestAnsiConsole.Output);
        Assert.AreEqual(0, exit);
        Assert.AreEqual(0, doc.RootElement.GetProperty("matches").GetArrayLength());
        Assert.AreEqual(1, doc.RootElement.GetProperty("docs").GetArrayLength());
        Assert.IsTrue(doc.RootElement.TryGetProperty("samplesError", out _));
        Assert.IsFalse(doc.RootElement.TryGetProperty("corpus", out _));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Search_NoMatches_ExitsOne(bool json)
    {
        _learn.Results = [];
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), json ? ["zzzz-unmatched", "--json"] : ["zzzz-unmatched"]);
        Assert.AreEqual(1, exit);
        if (json)
        {
            using var doc = JsonDocument.Parse(TestAnsiConsole.Output);
            Assert.AreEqual(0, doc.RootElement.GetProperty("docs").GetArrayLength());
            Assert.IsFalse(doc.RootElement.TryGetProperty("docsError", out _));
        }
        else
        {
            StringAssert.Contains(TestAnsiConsole.Output, "No WinUI samples matched.");
            StringAssert.Contains(TestAnsiConsole.Output, "No docs matched.");
        }
    }

    [TestMethod]
    public async Task Search_NoSamplesAndUnavailableDocs_ExitsOne()
    {
        _learn.Error = new HttpRequestException();
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["zzzz-unmatched", "--json"]);
        using var doc = JsonDocument.Parse(TestAnsiConsole.Output);
        Assert.AreEqual(1, exit);
        Assert.IsTrue(doc.RootElement.TryGetProperty("docsError", out _));
    }

    [TestMethod]
    public async Task Search_BothSourcesUnavailable_ReportsBothErrorsAndExitsOne()
    {
        _samples = FakeControlsSearchService.Unavailable();
        _learn.Error = new HttpRequestException();
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["tabview", "--json"]);
        using var doc = JsonDocument.Parse(TestAnsiConsole.Output);
        Assert.AreEqual(1, exit);
        Assert.AreEqual(0, doc.RootElement.GetProperty("matches").GetArrayLength());
        Assert.AreEqual(0, doc.RootElement.GetProperty("docs").GetArrayLength());
        Assert.IsTrue(doc.RootElement.TryGetProperty("samplesError", out _));
        Assert.IsTrue(doc.RootElement.TryGetProperty("docsError", out _));
        Assert.IsFalse(doc.RootElement.TryGetProperty("corpus", out _));
        Assert.AreEqual(1, _learn.Calls);
    }

    [TestMethod]
    public async Task Search_BlankQuestion_DoesNotSearchEitherSource()
    {
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), [" ", "--json"]);
        using var doc = JsonDocument.Parse(TestAnsiConsole.Output);
        Assert.AreEqual(1, exit);
        Assert.IsTrue(doc.RootElement.TryGetProperty("error", out _));
        Assert.AreEqual(0, _samples.GetEngineCalls);
        Assert.AreEqual(0, _learn.Calls);
    }

    [TestMethod]
    public async Task Search_PreCanceled_DoesNotSearchEitherSource()
    {
        var command = Command();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            GetRequiredService<AskCommand.Handler>().InvokeAsync(command.Parse(["tabview"]), cancellation.Token));
        Assert.AreEqual(0, _samples.GetEngineCalls);
        Assert.AreEqual(0, _learn.Calls);
    }

    [TestMethod]
    public async Task Search_CallerCancellationIsNotReportedAsDocsUnavailable()
    {
        using var cancellation = new CancellationTokenSource();
        _learn.BeforeSearch = cancellation.Cancel;
        var command = Command();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            GetRequiredService<AskCommand.Handler>().InvokeAsync(command.Parse(["tabview", "--json"]), cancellation.Token));
        Assert.AreEqual("", TestAnsiConsole.Output);
    }

    [TestMethod]
    public async Task FindUi_DoesNotCallLearn()
    {
        Command();
        var exit = await ParseAndInvokeWithCaptureAsync(GetRequiredService<FindUiCommand>(), ["tabview", "--json"]);
        Assert.AreEqual(0, exit);
        Assert.AreEqual(0, _learn.Calls);
    }

    [TestMethod]
    public async Task Root_RegistersAskAndExposesPrivacyNoticeInHelp()
    {
        Command();
        var root = GetRequiredService<WinAppRootCommand>();
        Assert.IsTrue(root.Subcommands.Any(c => c.Name == "ask"));
        var exit = await ParseAndInvokeWithCaptureAsync(root, ["ask", "--help"]);
        Assert.AreEqual(0, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "Every question is sent");
        Assert.AreEqual(0, _learn.Calls);
    }

    private sealed class FakeLearnClient : ILearnSearchClient
    {
        public int Calls { get; private set; }
        public string? Question { get; private set; }
        public Exception? Error { get; set; }
        public Action? BeforeSearch { get; set; }
        public IReadOnlyList<LearnSearchResult> Results { get; set; } =
            [new("Example", new Uri("https://learn.microsoft.com/windows/apps/example"), "Short excerpt")];

        public Task<IReadOnlyList<LearnSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            Calls++;
            Question = query;
            BeforeSearch?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Error is null ? Task.FromResult(Results) : Task.FromException<IReadOnlyList<LearnSearchResult>>(Error);
        }
    }
}
